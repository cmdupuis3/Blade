# Round 2 microkernel plan -- staged waves (draft, 2026-10-19)

Status: PLANNING DOCUMENT. Nothing in it has been built or timed unless a line says
`MEASURED` and names the command. Baseline tree: master 4136f0b5 (= `worktree-bench-fortran`).
Baseline gate: `blade test` TOTAL 6800 passed, 0 failed, 1 skipped (benign: no `./docs` beside
the binary), 6.9 min, binary SHA256 462B9D92... snapshotted as OLD.

Inputs: the external plan `C:\Users\cdupu\Documents\Blade-kernel-planning-2026-10-19` (README
priorities 1-8, existing-kernels-review table B), round-1 record (`bench/RESULTS.md` 14.8,
`bench/notes/gap-register.md` G1-G38), and five research briefs written against 4136f0b5
(scratchpad `round2/research/{A,B,C,D,E}-*.md`), each attacked by an independent Codex review
(`round2/reviews/`). Where the briefs corrected the external plan, the brief wins; where the
Codex review and a brief disagree, the fable arbiter's verdict is recorded in section 8.

## 0. One page

Scope of this round: the existing-kernel backlog (README priorities 1-6). Out of scope, proposed
as follow-up sessions: the relational whole-region track (membership hoisting, hash equijoin,
aggregate-only grouping) and the N01-N10 new benchmark workloads.

| # | item | mechanism (one line) | target cells (RESULTS 14.8, gf / ifx) | legality | effort | wave |
|---|---|---|---|---|---|---|
| A1 | G35 row-slot threaded repro folds | each thread folds COMPLETE logical rows into indexed slots; the outer chain runs serially from its seed | mt8 repro pairkernel 0.370 / 0.359, comoment3 0.478 / 0.427 | strict (no chain split) | S-M | 1 |
| A2 | ordered orbit scatter, rank 2 then 3 | one pass over the packed pool feeding every original per-row / per-pair chain in its original order (G-row-group vectorizable form; rank 3 = slices + retained H) | serial repro pairkernel 0.903 / 0.840, comoment3 0.773 / 0.780 | strict; bitwise under REPRO + CONTRACT=off | M + M, both lanes | 2 (prototype timing gate in 1) |
| B1 | rank-1 producer epilogue (saxpy) | stream the y2 checksum from L1 in 256-cell blocks right behind the store (P1 lane state; same tree as `sq<8>`) | saxpy serial 0.903 ifx, mt8 0.821 / 0.858, repro 0.686 ifx | licensed bits identical; repro = chain interleave | M | 1 |
| B2r | chain interleave (recur, stencil) | strip-mine the producer into 16-cell mini-blocks and advance the SAME serial checksum chain after each block so OoO overlaps it | serial repro recur 0.985 / 1.017, stencil 1.224 / 1.001; mt8 repro stencil 0.815 / 0.891 | strict (same order, same seed) | M (shares P1/P2 with B1) | 1 |
| B3 | compact-producer row-class epilogue (pairkernel) | per-row class partials written as each packed row is produced; combined serially in row order; no second 25 MB pass | pairkernel mt8 licensed 0.800 / 0.798 (D4 attributes the 1.5 ms gap to this pass); serial licensed 1.122 / 1.047 | licensed only (orbit regrouping); deterministic for ANY team size | M | 1 (second half of a wave-1 builder) |
| B4 | maskagg compaction fusion | 6 passes -> 2: count in the mask pass, fused fill+gather+fold, no `y*y` pool; positions elided only when no consumer needs them | maskagg serial 0.871 / 0.628, repro 0.839 / 0.676 | strict (stable ascending compaction kept) | M | 1 |
| C | sibling-fold traversal sharing | Optimize.fs pass rewrites sibling scalar folds over the same dense axis into the existing `<&!>` join (one-level inliner; unseeded legs seeded -0.0 exactly; declines when a leg would thread) | ifx repro/fastmath dot 0.755 / 0.712, onepass 0.820 / 0.723, fusedchain 0.839 / 0.788 (G36) | strict (each leg keeps its chain) | M | 1 (drop experiment first: hand-joined sources on OLD) |
| D2 | antisym row-panel groups | reuse each B micro-panel pair across G row panels (syrk's `rowGroup` pattern), cache-budget gate; both lanes | mt8 antisym 1.179 / 1.180, serial 1.113 / 1.075 (margin) | strict (visit order only) | S-M | 2 |
| D3b | simplex edge kloops | live-accumulator variants for staircase/pad edges (7.3% dead vector FMAs at p=171) | comoment3 serial 1.325 / 1.338, mt8 1.659 / 1.373 (margin) | strict | M | 2 |
| E-G7 | thread-level `<&!>` join | generalized Path B: one team, per-leg private accumulators, per-leg simd reduction, fixed-order combine; fires when the join's fold kernel (or a leg) carries an EXPLICIT `where omp` request AND every leg independently admits reassociation (Codex objection 11; the repo's request-vs-licence policy is kept, no auto-threading of builtin `+`); onepass.blade gains `where omp` on the join's fold kernel -- the spelling G7 already named | onepass mt8: no Blade arm today vs 9.0 / 9.5 ms | licensed (each leg's own builtin licence; the request is the fold kernel's) | M | 2 |
| E-G5 | let-rec step threading (recur) | honour `where omp` on the step's final map through slice-write fusion; per-step team, then persistent fixed-x team | recur mt8: no Blade arm vs 7.1 / 8.0 ms | licensed map; repro checksum needs a barrier + serial row fold | M (admission) + L (persistent) | 3 |
| -- | DROPPED on the count | D1 persistent syrk team (saves 2 forks + <=6 barriers ~ 0.1-0.3%); D3a weighted comoment3 tasks (LPT already 1.000); D4 team merge (<= 0.8%) | | | | |
| -- | zero-code measurements | D1-0 `syrk_g` mt8 sweep on gram; C's hand-joined timing; A's scatter prototype timing | | | | 1 (runner / builder) |

Roster: 4 opus/high builders (one worktree each), 2 Codex reviewers (attack each branch's diff
before merge), 1 fable/high arbiter (disagreements + the two riskiest legality arguments), 1
sonnet/high runner (gates, drop experiments, harness A/B). Integration branch:
`worktree-bench-fortran` (`.claude/worktrees/bench-fortran`); master is fast-forwarded at the
end by the user.

## 0b. MEASURED before any code (directional: OLD binary 4136f0b5, interleaved, 11 serial / 7 mt8
rounds, no drift controls; `round2/gates/drop-experiments.md`)

- **C drop test PASSES.** Hand-joined sources vs the bench sources, wall ms, serial: blade-llvm
  repro dot 27.1 -> 22.2 (1.22x, ranges disjoint, checksum bit-identical); onepass llvm repro
  28.4 -> 22.4 (1.27x); fusedchain llvm repro 18.9 -> 16.5 (1.15x); cpp repro dot 55.9 -> 51.5
  (1.085x). Licensed cells flat within +-5% (cpp licensed dot 1.014, no regression). Licensed
  checksums differ at 1.4e-15..2e-14 relative (reassociation, inside the 1e-12 tolerance); all
  repro checksums and every input_checksum identical.
- **D1-0 `syrk_g` mt8 sweep: NULL.** gram mt8 blade-cpp, G in {2,4,6,8}: licensed medians
  33.9 / 35.3 / 34.6 / 35.9 ms, repro 36.0 / 37.9 / 36.2 / 36.3 ms, ranges fully overlapping,
  checksums identical across G. Keep the default 4; the mt8 L3-traffic hypothesis for gram is
  closed (the instrument is weak: ~17-20 ms internal time near fork/join cost).

## 1. What the research round changed

- Stale hooks: `emitOrbitFold` moved to EmitLlvm.fs:6006; hot-row LLVM code to :3926/:4004/:4047;
  tile dispatch lives in CodeGenLoopNest.fs:3020-3226 and CodeGenExpr.fs:3133-3185, not
  CodeGenExprSupport (which only holds the knobs); `genLoopNestStreamed` is at :1463.
- Already fixed on master (gap-register text stale): the silent omp drop on a join's fold kernel
  (G7) and the LLVM missing-thread census (G11) both print census lines today.
- The runtime comment at `blade_packed_gemm.hpp:208` says 32 MB L3; the bench box has 16 MB, and
  `anti_nc` sizes B at ~8 MB "to stay in L3" -- half the real L3. Fix the comments in D2.
- Recur's checksum goes through `fuseRowFoldsModule` (an IR rewrite), not the hot-row side table;
  B2r's recur hook is `CodeGen.fs:233` + a new `CellFoldTable`.
- The bench checksums are all licensed (inner builtin `(+)`, outer `where omp` lambda); the
  scatter serves REPRO builds and unlicensed nests, G35 serves REPRO + omp + threads.
- Surface join inference seeds `(+)` with +0.0, which is NOT exact for an unseeded fold (all
  -0.0 inputs give 0.0 instead of -0.0); C must seed with -0.0 (`Neg(0.0)` node).
- An explicit `<&!>` join in a function's RETURN position fails BL7004 (pre-existing defect); C
  always let-binds the join.
- The hot-row planner never walks a let chain nested inside a top-level binding value
  (pairkernel's shape); B3 adds that walk.
- `no_parallel_form: false` in kernel.json breaks `run.py load_kernel` (false -> `list(False)`);
  E must fix the loader (accept false as []) before flipping recur/onepass.
- The harness hardcodes the Blade binary path (run.py:61); OLD-vs-NEW timing needs a mirror root
  with the OLD bin. Wave-end gates must run from a short `C:\` cwd with a `tests` junction (the
  deployed bin has a stale empty `tests\corpus\lie` dir that ICEs corpus-wiring otherwise).

## 2. Wave 1 (four builders, parallel; merge order below)

| builder | branch | items | files owned (others keep out) |
|---|---|---|---|
| W1-A | `r2-row-slots` | A1 G35 row slots (C++ lane); A2 drop experiment: extend `round2/A-work/scatter_proto.cpp` with the emitted walk (verbatim `canon_fold` + nested pointers) and a rank-3 slice form; time walk vs scatter at n=2503 / p=171, contract=off, 21 interleaved | `IRStorage.fs ChunkForm` (:111), `CodeGenBinding.fs chunkForm` (:3916-3931), `CodeGenLoopNest.fs` Path B prologue/fold/epilogue/census (:1560-1640, :1807, :2132, :2189), `CliCommands.fs llvmLaneDecision` (:988), `tests/OmpTests.fs`, knob `BLADE_FOLD_ROW_SLOTS` (suiteClearedKnobs), `BLADE_TILE_TUNE rowslots_min` |
| W1-B | `r2-cell-epilogue` | P1 + P2 runtime; planner extensions for ALL B forms (rank-1 arm, compact rank-2 arm, nested-chain walk, `CellChain`, `CellFoldTable`); B1 saxpy; B2r recur + stencil | `blade_fold_lanes.hpp` (P1), `Optimize.fs HotRowPlan/planHotRowFolds/fuseRowFoldsModule` (:1504-1683, :2004), `Lowering.fs` :2715/:2911, `CodeGenLoopNest.fs` hot-row (:1738-1760, :2149-2180) + new `tryGenFlatEpilogueNest` beside :2371, `CodeGenBinding.fs:113`, `CodeGenCuda.fs buildRowEpilogue` (:2006), `CodeGen.fs:233`, `EmitLlvm.fs` hot-row (:3926-4080), `emitForRange` (:2978), flat path; gate `BLADE_CELL_EPILOGUE` (optimizerGates), `BLADE_TILE_TUNE epi_blk/epi_blk_r`; `tests/OptimizeTests.fs`, `fold_lanes_tests.cpp` |
| W1-C | `r2-compaction` then B3 | B4 maskagg (a+b+c; d only with a consumer census); THEN B3 pairkernel compact row classes, started after W1-B's planner commit is merged into integration | `index_types.h:354`, `CodeGenExpr.fs materializeMaskForm` (:1996), `CodeGenBinding.fs genCompoundInitBinding` (:1908), `CodeGenCuda.fs:3650-3675`, `CodeGenExprSupport.laneableFoldLevel` (:2160ff), `EmitLlvm.emitCompoundInit` (:6870); for B3: `CodeGenLoopNest` hot-row compact admission (:1752), orbit dispatch consumer `CodeGenBinding.fs:3740-3765` (factor `emitC` out of `genOrbitFoldCpp` :3620), `EmitLlvm tryBindHotRowProducer` (:4004) |
| W1-D | `r2-sibling-folds` | C: IRMono lifts (`freshen`, `mapOneArray`) as the FIRST commit; `Toolchain` twins of `fpReproEnabled`/`ompThreadEmissionEnabled`; `shareSiblingFoldsModule` last in `optimizeModule`; corpus `tests/corpus/sibling-folds/`; gate `BLADE_SIBLING_FOLDS` (optimizerGates) | `Optimize.fs` new pass + `optimizerGates`/`optimizeModule` edits only, `IRMono.fs` lifts, `Toolchain.fs`, `CodeGenState.fs` re-exports, `tests/OptimizeTests.fs` (append), `tests/corpus/sibling-folds/` |

Shared-surface rules for wave 1:
- `Optimize.fs optimizerGates` and `optimizeModule`: W1-B and W1-D each add one line; W1-D's pass
  goes LAST in `optimizeModule`. Textual merge only.
- `CodeGenLoopNest.genLoopNestStreamed`: W1-A (Path B region), W1-B (hot-row region), later D2
  (row-contraction dispatch :3180). Different line ranges of one function; the orchestrator merges
  W1-A first, then W1-B.
- `CLAUDE.md` env table: every item adds a row; resolve textually.
- C must NOT take a leg whose operand is a fresh `|> compute`d rank-1 map of the same region while
  `BLADE_CELL_EPILOGUE` is on (saxpy's `s` belongs to B1); it joins only the input-sum pair.
- Nobody touches the tile runtime (`blade_tiles.h`, `blade_packed_gemm.hpp`, `blade_llvm_tiles.c`)
  in wave 1.
- `keep` in maskagg stays STORED in v1 (user-named binding); one-pass-over-x is a v2 question of
  demand policy, not for this round.

Runner, wave 1 (while builders code): the two zero-code drop experiments (C's hand-joined sources;
D1-0 `syrk_g` sweep) -- RUNNING; then builders' targeted-gate requests on demand; then, when the
last wave-1 branch is merged, the five gates on the integration tree (default, interp, opt-diff,
llvm, docs) from `C:\bt_r2w1\` with a `tests` junction, then the harness A/B (OLD mirror root vs
NEW) on the wave-1 cells: saxpy (4 regimes), stencil + recur repro, pairkernel serial/mt8 licensed,
maskagg, dot/onepass/fusedchain serial (both lanes) + mt8 repro, gram mt8 (`syrk_g` result).

Codex, wave 1: review each branch's diff before merge (two at a time): legality against the
preamble constraints, bit claims against the raw-bit blocks, census/plan lines, tests.

Merge order into `worktree-bench-fortran`: W1-D lifts commit (early, so W1-B rebases trivially) ->
W1-A -> W1-B -> W1-C (B4) -> W1-D (rule) -> W1-C (B3). Each merge: build + `blade test optimize
omp linalg cli corpus-wiring` on the integration tree before the next.

## 3. Wave 2 (after wave-1 gates and A/B numbers)

| builder | items | gate from wave 1 |
|---|---|---|
| W2-A | A2 ordered scatter rank 2 (both lanes), then rank 3 (slices + retained H); `BLADE_ORBIT_SCATTER`, `BLADE_TILE_TUNE scatter_g/scatter_b`; raw-bit block `blade test orbit-scatter`; corpus symmetry/128-131 | prototype < 50% of the walk's time at the real shapes (else DROP) |
| W2-D | D2 antisym row groups (both lanes, `anti_g`, cache gate, fix the L3 comments) then D3b simplex edge kloops (`tri_edge`); needles in LinAlgTests/OptimizeTests/LlvmTests; `row-tile`, `llvm tiles`, `syrk-tile` raw-bit loops over G / edge | D1-0 result informs the `syrk_g` default; D2 default `anti_g` auto only if its A/B wins |
| W2-E | E-G7 thread-level `<&!>` join (generalized Path B, `BLADE_JOIN_THREADS`), both entry shapes (`genReduceComputeBindingCore` fused tree and `genReduceJoinCore` packs); harness `load_kernel` false fix; onepass kernel.json; then C's thread gate relaxed ONLY for joins whose legs all request threads | W1-A's Path B edits merged first (same machinery) |
| W2-B | B3 if not finished in wave 1; B2l `lanes` arm as a measured option; stencil mt8-licensed attribution (why Blade's kernel is ~1 ms behind an expert with the same schedule) | |

Wave-2 gates: as wave 1, plus `blade test orbit-scatter`, `row-tile`, `llvm tiles`, `syrk-tile`
100%; harness A/B on pairkernel/comoment3 repro (serial + mt8), antisym (serial both lanes, mt8),
comoment3 serial/mt8, onepass mt8 (new arm), gram serial as control.

## 4. Wave 3 (conditional)

E-G5 recur step threading (admission + per-step, then persistent fixed-x team; recur.blade gains
`where omp(v: 1)` on the step's final map; kernel.json); scatter v1.1 contraction invariance,
antisym extension, per-row seeds; D1 persistent syrk team only if an instrumented driver shows
> 2% fork/barrier/idle; G35 v2 (rank-3 leaf-chain slots) only if mt8 repro comoment3 is still
below parity. Then RESULTS.md section 15 and the gap register.

## 5. Protocol (unchanged from round 1 unless stated)

- Builders run TARGETED `blade test <key>` blocks only; the runner runs full suites at wave end.
- Every change: both lanes agree with the interpreter under `BLADE_FP_REPRO=1 BLADE_FP_CONTRACT=off`
  (`test interp`), optimizer-gated changes compare off/on (`opt-diff`), licensed-only forms get an
  explicit block (gate on vs off at 1e-12; raw bits where the brief claims identity).
- Named outputs stay stored; strict chains keep seed + order; no licence transfer or union;
  `blade plan` records every applied/declined decision; a dropped omp is never silent.
- Timing: non-power-of-two shapes (the bench shapes), interleaved OLD/NEW(/CTRL = NEW with the
  gate off), medians with CIs, drift controls, incrementals against input-only twins FROM THE SAME
  BINARY (C's rule fires on the twins too; G37 moved the C++ twins).

## 5b. Execution log

- 2026-10-19 ~23:58: W1-A (G35 + scatter prototype timing) and W1-D (sibling folds) started in
  their own worktrees from 4136f0b5 after the A/C Codex review landed; the B review and the
  fable arbitration (seeding policy; G7 policy confirmation) were still running.
- 2026-10-20 ~00:02: the B review landed (all ACCEPT WITH CHANGES); W1-B (P1/P2, B1, B2r) and
  W1-C (B4 now, B3 later) started. ~00:10: fable ruling in; W1-D told to keep -0.0 seeding and
  add the formalism paragraph + quoted pins. The runner is staging the OLD/NEW harness mirror
  (`C:\bt_r2ab\`) and the gate cwd (`C:\bt_r2w1\`). Plan + record committed on
  `worktree-bench-fortran` as a1e93bb7 / a322ccd0 / 141addd3.
- ~00:34: OLD mirror smoke clean (536 cells, 0 failures, ifx reachable; evidence keyed on
  462b9d92).
- ~00:45: **W1-A done** on `worktree-agent-a4bbab15b9c78bcf9` (1fec5666 + 1f043139): G35 row
  slots in the C++ lane (`ChunkRowSlots`, closed admission whitelist, consumer gate exempts
  the row-slot case, serial ascending epilogue from the seed, `BLADE_FOLD_ROW_SLOTS`,
  `rowslots_min`); omp-pragma 87/0, omp-reduce 91/0, optimize 152/0, symmetry 128/0, linalg
  153/0, cli 105/0, corpus-wiring 14/0, interp symmetry 129/0; pairkernel/comoment3 repro
  checksums identical threaded vs serial; raw-bit case T=1/4/7 vs serial identical.
  **Scatter prototype MEASURED (box loaded, directional):** rank 2 n=2503 S/W = 0.124-0.138
  (W 12-17 ms, S 1.7-2.2 ms), rank 3 p=171 S/W = 0.07-0.10, raw-bit W==S 21/21 under both
  contraction modes -> both ranks KEEP for wave 2 (bars 0.59/0.33 and 0.47/0.49). No G35
  timing yet (wave-end A/B). Codex diff review of W1-A launched (task-mvg6yixz).
- ~00:58: Codex diff review of W1-A: DO NOT MERGE yet -- one blocker (the OUTER fold kernel
  is never audited: a `where comm` fold kernel may carry a captured write, and hoisting all
  rows before the outer applications changes operands; fix = audit the outer callable with
  the same closed whitelist + decline fixture), two should-fix (whitelist has default-accept
  arms `IRMath2 _` / unary `_`; thread tests request but do not observe team sizes -> add an
  omp-coverage case, rn=0 and small-rn cases), one nit (name the identity case
  finite-value/low-bit + signed-zero + NaN-equivalence; assert the NaN output). Chains,
  consumer gate, census, knob, LLVM translation, docs and the scatter harness were confirmed
  sound (harness nit: return code ignores mismatches). Sent back to W1-A.
- ~01:10: **W1-C B4 done** on `worktree-agent-a9c504d47b6322e78` (1e8a864c + 53c84312 +
  975d849d): maskagg 6 passes -> 2 (count in the mask pass; fused branchless fill+gather with
  m+1 slack; no `y*y` pool; repro chain fed per 256-cell block from indices < q, first kept
  square peeled, BL8003 at the fold's line), gate `BLADE_COMPACTION_FUSION`; B4c licensed
  in-pass feed deferred to P1; B4d not done; `keep` stored. optimize 164/0, index-types
  378/0, cli 103/0, corpus-wiring 14/0, symmetry 127/0, interp index-types 377/0/2,
  opt-diff index-types 243/0/135 (7 "C++ differs, output identical"). Repro raw bits OLD ==
  NEW == gate-off (`0x1.9651090932898p+20`); licensed 2.8e-16 rel (sq<8> vs OLD's
  eight-scalar-lane fold); gate-off C++ byte-identical to 4136f0b5. Drop experiment
  (directional, kernel-segment medians): B4a -0.5..-1.0 ms, B4b -0.3..-0.9 ms, B4c ~0 ..
  -0.3 ms; b+c beyond a 0.6-0.95 ms (at the 0.6 bar in the quiet run). Codex diff review
  launched (task-mvg7p471); W1-C started B3 with its own side table.
- ~01:20: W1-A review fixes landed (fe43edfe: outer fold kernel audited with the closed
  whitelist, whitelist arms enumerated -- 16 libm names, atan2/log_base, lgamma/digamma/erf/
  hypot refused; omp-coverage observes teamsz/distinct = T at 521, clamp to 3 rows, 0 rows =
  seed; identity cases renamed, rank-3 runtime identity added). omp-pragma 102/0, omp-reduce
  93/0, omp-coverage 8/0, optimize 152/0, symmetry 128/0, cli 105/0, corpus-wiring 14/0.
  **MERGED** into `worktree-bench-fortran` as 6c096060; post-merge sanity blocks running.
  **Pre-existing race found (NOT this branch):** under the default licensed build with
  threads, old Path B threads a fold whose kernel is `where comm` (no `omp`) but WRITES a
  captured `let mut` (`G((0 : I)) = G((0 : I)) + 1.0; a + b`), reached through the map's
  `omp(i: 1)` licence: five mt8 runs printed five different values (70699503 .. 70700344 vs
  serial 70710120). The front-end write ban is conditional on a parallel body and `chunkable`
  does not inspect the fold kernel for writes. Follow-up item (own card): make `chunkable`
  (CodeGenBinding.fs ~:3892-3905) refuse a fold kernel with transitive writes, or extend the
  BL4005 ban to comm-licensed fold kernels reached by a map licence; corpus pin of the
  counterexample once fixed.
- ~01:40: Codex diff review of W1-C B4: MERGE AFTER FIXES -- blockers: (1) B4a admits a
  named local `let yy = y * y |> compute` with a mutating call between it and the reduce
  (OLD folds the pre-write snapshot, NEW reads post-write y) -> admit only the immediate
  compiler-lifted pair / decline across any MayWrite; (2) B4c's write barrier
  (`MayWrite && refs.Contains(yId)`) misses aliases and indirect writes (`let mut alias = y;
  overwrite(alias)`) -> reject in-pass on ANY MayWrite between construction and consumer.
  Should-fix: LLVM gate-off/on runtime cases (B4a-only lets `fmul contract` reach `fadd
  contract` across the former store boundary; identity regime still holds), licensed
  tolerance check vs sq<8>, pin the empty fold's source line; drop write-up must separate the
  MEASURED licensed c (streamed state) from the SHIPPED licensed b (post-pass sq<8>):
  shipped-arm savings licensed 1.709/0.833 ms, repro 1.943/1.237 ms. Index invariants, slack
  handling, ascending branchless fill, admission narrowness, gate placement, 515-518 mask
  shapes, ABORT pins all confirmed. Sent to W1-C (working B3 concurrently).
- ~01:50: post-merge sanity of 6c096060 (W1-A) green: build 0 warnings (Blade.dll 127A15E6),
  optimize 152/0, linalg 19/0, cli 105/0, corpus-wiring 14/0, symmetry 128/0, omp-pragma
  102/0, omp-reduce 93/0, omp-coverage 8/0.
- ~02:00: **W1-B done** on `worktree-agent-af7b4bee750b29e49` (de0a404b P1; a9fae4c4 planner
  types; 42e4426c C++ B1+B2r; 4dc86f7a tests; fdca9e55 LLVM; 23b8cdab docs; 23 files, +2192).
  Drop experiments (directional, kernel-internal ms): recur repro gap 3.9 ms, register-consume
  recovers 2.98 (77%) fast / 2.14 (56%) off (store/reload only 1.3-2.0); stencil gap 2.0,
  recovers 1.61 (82%); saxpy licensed P1 4.33 -> 3.68, repro 5.06 -> 3.36. All survive. Form
  chosen: REGISTER-CONSUME (producer stays SIMD, chain is an in-order fold-left), `epi_blk_r`
  default 0 (one fused loop; 8-64 made no difference), P1 state stays in ymm1-8. B2l not
  built (`lanes` records a decline). Gate-off C++ byte-identical to OLD (6 cells); raw bits
  NEW == OLD for saxpy licensed T=1/3/8, saxpy repro, stencil repro serial+mt8, recur repro
  (both contraction modes); printed checksums identical everywhere incl. LLVM. New default-
  suite block `blade test cell-epilogue` 44/0 (non-dyadic n=2003, NEW vs gate-off raw bits).
  fold-lanes 4/0 (120088 raw-bit cases), optimize 177/0, loops 245/0, recursive-arrays
  29/0, omp 76/90/5 all 0 failed, cli 103/0, corpus-wiring 14/0, interp loops 246/0,
  interp recursive-arrays 30/0, opt-diff loops 216/0/29, opt-diff recursive-arrays 27/0/2.
  LLVM whole-program directional: saxpy repro 15.04 -> 13.13 ms, stencil repro 13.80 ->
  12.31, recur repro 13.05 -> 10.04. Codex diff review launched (task-mvg9glpd).
- ~02:25: Codex diff review of W1-B: MERGE AFTER FIXES -- blockers (both in the new global
  nested-let-chain walk): (1) a row kernel's capture bound by an intervening let is
  scheduled before its declaration (the between-check tests references in the intervening
  value, not whether the binder is captured); (2) failure reordering: admission requires
  Repeatable (permits MayFail), so a failing row kernel (`1 % row(0)`) can run during P's fill
  ahead of an intervening `7 / d` -- require captures to dominate the producer and a movable
  (non-failing) row kernel. Should-fix: LLVM test programs must not turn refusals/exceptions
  into skips; add licensed T=3 fast, actual-team assertions, LLVM licensed on/off at lane
  tolerance, recur buffer hash; record B2r under `cell-epilogue` in both lanes and record
  constructs after the form is chosen; docs over-claim (identity only under repro+off;
  licensed C++ conditioned on identical producer values + teams; `lanes` reserved;
  epi_blk_r=0 "chosen simple default" not "measured best"). P1 contract, B1 partition/combine,
  seed-at-consumer, chains, recur lookup, line endings confirmed. Sent to W1-B.
- ~02:50: **W1-C B4 fixes + B3 done** (db042d9f, 5ad7d280, b6921568, 76fd75d2). Both B4
  blockers reproduced then fixed (snapshot case NEW 2699 vs OLD 203 -> 203; alias case NEW 203
  vs OLD 2699 -> 2699); LLVM fold-only form emits an unflagged `fmul` so it cannot contract;
  cli runtime cases n=1003 non-dyadic, both forms, both lanes, repro+off identical bits,
  default within 1e-12; corpus index-types/520 (interp-unsupported: no compound element
  store), 521 (intervening BL8013 before the empty fold); drop write-up corrected (shipped
  licensed arm b: 1.709/0.833 ms; repro arm c: 1.943/1.237 ms; run 2 misses the 0.6 bar for b
  beyond a). B3: `planCompactRowClasses` (IR facts, gate `BLADE_COMPACT_ROW_CLASSES`), orbit
  plan resolved at emission, licensed only; C++ producer writes `d[i]` from the stored cell
  and `o[i] = sq<8>(row+1, n-1-i)` inside the dynamic team, consumer sums each class
  ascending then the factored `genOrbitTotalCpp`; LLVM via `orbitCombineLlvm`. pairkernel mt8
  licensed: no `__opart`, no second team; NEW identical at T=1/3/8 + serial
  (residual 0.751854705624282) where OLD varied with T; NEW vs OLD ~8e-16 rel; repro
  byte-identical (declined path). optimize 173/0, cli 117/0, corpus-wiring 14/0, index-types
  380/0, symmetry 129/0, omp 76/90/5, interp index-types 378/0/3, interp symmetry 130/0,
  opt-diff index-types 245/0/135, opt-diff symmetry 91/0/38. Codex review of the four new
  commits launched. Attribution note: all three builders keep their own `Co-Authored-By:
  Claude Opus 5.5` trailer (their session-level instruction); merge commits carry the Fable
  line.
- ~03:05: **W1-D done** on `worktree-agent-a8d2474c563b1365c` (3d2aa4c3 IRMono lifts;
  a89f148a Toolchain predicates; eec6daad the pass + formalism 2.4 paragraph; 958870f4 corpus
  sibling-folds/001-012 + needles; b1d51902 docs). Seed function `sfIdentitySeed`. Plan lines:
  dot/fusedchain applied under THREADS=1 and REPRO, declined (thread gate) in the default;
  onepass applied in ALL regimes; saxpy input sums joined, kernel fold left for the producer
  epilogue; inputonly joined in all regimes (twins move). NEW vs OLD identical under
  REPRO+off+THREADS=1 in both lanes (dot 749475.148712715 / 2998681.67744271; onepass;
  fusedchain; inputonly). optimize 163/0, sibling-folds 12/0, loops 241/0, corpus-wiring
  14/0, cli 103/0, functions 324/0, interp sibling-folds 13/0, opt-diff sibling-folds 12/0
  (5 inert). **Finding:** the licensed C++ join starts `omp simd reduction` lanes at +0.0, so
  an all-negative-zero licensed sum prints 0.0 (pre-existing join behaviour, also for a
  user-written -0.0 seed; LLVM licensed drops it via nsz) -- the rewrite extends it to legs
  that used to be standalone K-lanes folds; documented as outside the licensed fold contract
  (CLAUDE.md, features.md, corpus 003/011). Unseeded empty axis is BL3007 at compile time (no
  test possible). The "mutable module binding" refusal was removed (top-level arrays are
  IsMutable; calls/assignments are the barriers) -- flagged for the Codex review. The pass
  also rewrites top-level scalar bindings corpus-wide: whole-corpus gates are the real check.
  Codex diff review launched.
- ~03:15: Codex review of W1-C's four new commits: MERGE AFTER FIXES -- B4 blockers CLOSED
  (whole-host write barrier; transitive effect summaries fail closed; 520/521 pin both
  counterexamples; LLVM fold-only `fmul` undecorated; drop write-up corrected). B3 blockers:
  (1) capture dominance -- a scalar bound between producer and consumer (`scale`) is read by
  the epilogue before its binding (OrbitFold.pureScalarBody admits scalar IRVars); (2) the
  write barrier ends before the consumer's OUTER SEED: a seed call mutating K through an alias
  is admitted. Should-fix: determinism case must observe actual teams; licensed B3 has no LLVM
  runtime gate (rowsRun hardcodes BLADE_LLVM=null) -> lane loop with 0/1, non-dyadic, strict
  unknown parity, nonzero outer seed, p=2503 assertion; pin the B4 `fmul` as undecorated in the
  default build. Nit: docs wording (bits change vs OLD; team independence per lane/build;
  LLVM per-row tree differs; expert folds columns). Merge preview W1-B x W1-C: textual
  conflicts in Optimize.fs gates, Lowering.fs resets, CodeGenCuda.fs epilogue builders,
  CodeGenLoopNest hot-row/compactRow setup, IRStorage LoopNestCodeGen fields, CLAUDE.md row,
  OptimizeTests; CodeGenBinding auto-merges but needs semantic review; W1-B's unused
  `CompactRowsCandidate`/`CompactClassRows` scaffold duplicates B3's `CompactRowTable` ->
  keep B3's table, remove the scaffold in a follow-up. Sent to W1-C.
- ~03:35: Codex review of W1-D: MERGE AFTER FIXES -- blockers: (1) the inliner inserts a
  callee's spine before its containing statement, hoisting the callee's folds across an
  earlier writing call in the SAME expression (`bump(x) + stats(x)`: 2568 -> 20); (2) a
  shared-kernel fusion tree's STRUCTURAL projections (`t[1]` on a nested-pair tuple) are
  remapped as flat leg indices (wrong leg); (3) streamed provider operands pass the
  stored-array admission (IsVirtual=false does not prove materialization; reaches BL7004 in
  streamedNestSetup). Should-fix: one-hop deferred facts; "left for the producer epilogue"
  reserves shapes B1 cannot consume; the signed-zero corpus (003/011) would FAIL `blade test
  llvm` (lane comparator rejects opposite zero spellings) and sibling-folds is missing from
  the llvm default list; applied record over-claims identity, convergence needle must
  assert seeds + simd arity. Audit confirmed: totality judge (float / discharged), interval
  barriers, thread/licence gates, dedupe keeps capture ids, seeds per arbitration, IRMono /
  Toolchain lifts exact, no fsproj change. Licensed zero sign: acceptable under the
  fastest-default policy's explicit `reassoc nsz`; the join emitter's +0 private accumulators
  override a user -0.0 seed too (pre-existing; gap note). Pin-risk suspects for the
  whole-corpus gates: spectra/062 Parseval boolean, loops/205 non-dyadic residuals, math/086
  solve sums, functions/056/137/198/199 statistics shapes. Conflicts W1-D x W1-B/W1-C:
  Optimize.fs gate declarations + the optimizerGates line, CLAUDE.md row, features.md row;
  OptimizeTests hunks disjoint. Sent to W1-D.
- ~04:00: **W1-B fixes landed** (53d9ca67 planner: capture dominance + non-failing row kernel
  -- blocker 1 was a PRE-EXISTING compile failure on OLD too (`'w2' was not declared`); corpus
  loops/484 (late capture, shadowed name) and 485 (ABORT order); 428b549c decision records
  unified under `cell-epilogue` in both lanes, constructs recorded after the form is chosen;
  47aa8ddb cell-epilogue block 56 cases: no silent LLVM skips, actual team asserted (3, 8, and
  2 under OMP_THREAD_LIMIT=2), licensed T=3 fast, recur buffer hashed, licensed LLVM on/off at
  1e-9; f8b41405 docs qualified). optimize 190/0, cell-epilogue 56/0, loops 247/0,
  recursive-arrays 29/0, math 78/0, symmetry 127/0, sgs 15/0, spectra 42/0, interp loops
  248/0, interp recursive-arrays 30/0, opt-diff loops 218/0/29, opt-diff recursive-arrays
  27/0/2. **MERGED** into `worktree-bench-fortran` as e5e24e17 (conflicts: CLAUDE.md
  TILE_TUNE row -- both clauses kept; CodeGenExprSupport.fs adjacent knob helpers -- both
  kept). Post-merge sanity running.
- ~04:20: **W1-C B3 fixes landed** (10d76944): planner records `DominatingIds` (earlier lets,
  function params, module bindings ahead of the top-level binding) and `bodyDominated` is
  checked at emission in both lanes; the consumer's own evaluation (seed + walk) declines when
  it may write (`symmetry/131` pins both: a scalar computed between producer and fold, and a
  `poke(kw)` seed writing a producer cell -> sw = 2002.0 = OLD); cli determinism case builds
  with omp-coverage instrumentation and observed teams 1/3/8 with identical full output;
  LLVM runtime gates (symmetric + strict unknown-parity, outer seed 0.37, n=101 tails;
  pairkernel p=2503 gate off/on within 1e-12; C++ vs LLVM within 1e-12); B4 fold-only LLVM
  `fmul` pinned undecorated in the default build; docs qualified. optimize 175/0, cli 124/0,
  corpus-wiring 14/0, symmetry 130/0, index-types 380/0, interp symmetry 131/0, opt-diff
  symmetry 92/0/38. Awaiting the W1-B post-merge build before merging.
- ~04:45: post-merge sanity of e5e24e17 (W1-B) green: build clean (Blade.dll 3089A028),
  optimize 190/0, cell-epilogue 56/0, fold-lanes 4/0, loops 247/0, recursive-arrays 29/0,
  symmetry 128/0, linalg 19/0, cli 105/0, corpus-wiring 14/0, omp 102/93/8.
  **W1-C MERGED** as 10b7903f: eight conflicts resolved by script -- CLAUDE.md optimizer-gates
  row three-way merged (both clauses), CodeGenBinding orbit dispatch keeps W1-A's
  `(plan, why)` tuple arms + W1-C's `compactRows` block, CodeGenCuda keeps both epilogue
  builders and W1-C's `CompactRowEpilogue` rebinding before W1-B's `flatEpiDeclined`,
  CodeGenLoopNest keeps `cellBlock` then `compactRow`, IRStorage keeps both
  `FlatEpilogue`/`CompactRowEpilogue` fields, Lowering resets all three tables,
  `optimizerGates` = union (CELL_EPILOGUE, COMPACTION_FUSION, COMPACT_ROW_CLASSES),
  OptimizeTests keeps both test families minus a duplicate `countOf` (identical semantics).
  First post-merge build FAILED (FS0001 at OptimizeTests.fs:1080): git had split a W1-B helper
  (the `true/else/false` tail landed after W1-C's inserted block); restored in acd8501e.
  Second build failure (FS0001 at :1262): the SAME six-line tail belongs to BOTH W1-B's llvm
  helper and W1-C's `codegenDecisionCase`; git emitted the common tail once, so both
  functions need it -- restored in 46007b87. Lesson for the W1-D merge: after a scripted
  resolution, diff each conflicted file against BOTH parents, and watch for shared text that
  git folds into one copy. Rebuilt clean (0/0) on 46007b87; blocks running.
- ~05:30: **W1-D fixes landed** (f1eed467: Optimize.fs fixes + tests -- a PowerShell quoting
  mishap folded the compiler change into the test commit, message describes the tests only;
  5f5d3248 docs + gap-register G40). Per finding: (1) a call is not inlined out of a statement
  that may write (transitive closure; `bump(xm) + stats(xm)` = 2568.0 in every lane); (2)
  shared `<&!>` trees with 3+ leaves and any structural projection DECLINE; (3) streamed
  provider operands refused in admission, the totality proof and call arguments, plus
  operands bound outside the region/module; (4) cycle-safe write closure over deferred
  producers, aliases, callable captures, nested callables; (5) epilogue reservation mirrors
  W1-B's rank-1 eligibility textually (`let y = x * 2.0 |> compute` now joins; saxpy reserves
  with the gate on, joins all three with `BLADE_CELL_EPILOGUE=0`); (6) licensed-sign cases
  moved to corpus 015 on `where repro` kernels, the surface +0.0 rule became a needle,
  `sibling-folds` added to `LlvmTests.defaultCategories`, G40 registered; (7) record wording
  qualified; convergence needle asserts seeds + simd arity; corpus 016 (Float32/Int32; LLVM
  skips Float32), 017 (different captures not deduped), 018 (failing callee refused, frame
  pinned by needle); streamed regression in ZarrTests 10f'. optimize 173/0, sibling-folds
  18/0, interp 19/0, opt-diff 18/0, llvm sibling-folds 17/0/1, loops 241/0, functions 324/0,
  cli 103/0, corpus-wiring 14/0, math 78/0, spectra 42/0, sql-reduce 30/0, rand 33/0, ppl
  140/0, zarr 355/0. NEW-vs-OLD identity NOT re-run after the fixes (the wave-end A/B
  covers it). **Pre-existing bugs found (NOT this branch, follow-up cards):** (a) C++ lane:
  `t[1]` of a THREE-leaf shared `<&!>` tree prints 28 where LLVM prints 34 (structural
  projection mapped as flat in the C++ join emitter), and `t[0][1]` is a BL9002 g++
  rejection; (b) a deferred map whose kernel WRITES, bound in a binding block, prints 68 in
  C++ vs 20 in LLVM (gate off too).
- ~05:50: post-merge blocks on 46007b87 (A+B+C): optimize 213/0, cell-epilogue 56/0,
  fold-lanes 4/0, index-types 380/0, symmetry 131/0, loops 247/0, recursive-arrays 29/0,
  linalg 153/21/4/19/19 all 0 failed, cli 126/0, corpus-wiring 14/0, omp-reduce 93/0,
  omp-coverage 8/0, omp-pragma 101/1 -- the one failure a stale W1-A pin
  (`row_slots_not_in_default_build` expected the orbit-class fold; B3's compact row classes
  now take that shape in the licensed build; the guard "no __rslot" still holds) -> pin
  accepts either licensed form (9e46be03). Merging W1-D.
- ~06:05: **W1-D MERGED**: three conflicts (CLAUDE.md gates row three-way merged -- all four
  clauses; features.md both rows kept; Optimize.fs gate declarations both kept, gate list =
  union incl. `BLADE_SIBLING_FOLDS`); deletions vs both parents inspected -- only the other
  side's own edits of the same rows/functions, no spliced bodies. Building the four-way tree.
- ~06:15: **WAVE 1 INTEGRATED** at 6b3e5293 (build 0/0, Blade.dll 3061E900). Runner started
  the five gates from `C:\bt_r2w1` (default, opt-diff, docs, llvm, interp). **WAVE 2 started**
  in fresh worktrees reset to 6b3e5293: W2-A ordered scatter rank 2 + 3 (both lanes,
  `BLADE_ORBIT_SCATTER`, `scatter_g/scatter_b`, `blade test orbit-scatter`); W2-D antisym row
  groups (`anti_g`, both drivers, scratch-aware LLVM emitter, route assertion) + simplex edge
  kloops with the CORRECTED L0/L1 formula (`tri_edge`); W2-E G7 thread-level `<&!>` join
  (generalized Path B, explicit-request policy, `BLADE_JOIN_THREADS`, onepass.blade gains
  `where omp` on its fold kernel, harness `load_kernel` false fix, the sibling-fold thread
  gate relaxed to the same policy); W2-F the three pre-existing correctness bugs (Path B
  comm-fold captured-write race; C++ structural projection of a three-leaf shared tree;
  writing deferred-map lane divergence) with pins and gap-register rows. Builders will be
  asked to IDLE during the harness A/B window after the gates.
- ~07:10: **W2-D done** on `worktree-agent-a062057a5ec063b98` (ec65b6b7 tiles; d6e7a3b3 docs +
  corpus symmetry/132): D2 `anti_g` row groups in both drivers (auto = largest of 4/2/1 under
  the 256 KiB active-pair budget: G=4 up to n=512, 2 up to 819 for 6x8; LLVM scratch
  G-aware; llvm tiles route assertion watches the scratch, 2538 tile-route + 28 scalar-route
  checks); D3b `tri_edge` live kloops with the corrected per-half formula -- finding: under
  contract=fast g++ (Zen `avoid-fma-max-bits=256`) does not contract a single-chain loop, so
  the (1,0)/(0,1) edge variants run the (1,1) variant to keep per-step contraction identical
  (caught by a 1-ulp row-tile mismatch at p=1..3). syrk-tile 4/0, row-tile 23/0, llvm tiles
  12/0, linalg 153/4/23/19 0 failed, optimize 240/0, cli 126/0, corpus-wiring 14/0, symmetry
  132/0; checksums identical to the integration binary in all 12 configurations.
  Directional timing (shared box): `tri_edge=1` ~2-3% on the simplex tile in both lanes
  (gram control within +-1.2%); `anti_g` auto G=4 is 5-7.5% SLOWER serially on a quiet box
  and faster only under load (micro-bench G4/G1 0.79-0.92 at load 19-26%, 1.05-1.06 at
  19-60%); default kept at auto pending the runner's quiet A/B (flip = `antisymRowGroup`
  default 0 -> 1 plus four needles). Codex diff review launched.
- ~07:35: Codex review of W2-D: MERGE AFTER FIXES -- should-fix: unset `anti_g` must default to
  1 (auto G=4 is an unearned serial regression by the plan's own rule; auto stays as
  `anti_g=0` for the A/B); nits: "fetched once per group" is a cache-model assumption; "largest
  fitting G in {4,2}, otherwise 1" (G=1 itself exceeds the budget at n=1201); the tested
  anti_g set is {1,2,3,4,8}, not 1..8. Geometry/alias/tail/scheduling invariants, budget
  thresholds, LLVM sizing + route oracle, D3b families and dispatch, ABI threading, corpus
  132 pins (independently recomputed), line endings all confirmed. Sent to W2-D.
- ~07:50: **W2-E done** on `worktree-agent-a0ba97387e51a733f` (0984a259 compiler; c91e21ec
  tests/docs; 9933063f bench; a93a9e91 purity audit binds range<I> params): G7 threaded
  `<&!>` join under the explicit-request policy (request on the join's fold kernel / a leg's
  fold kernel / a covering leaf-map omp; per-leg licence; shape via fusedFoldSimdPlan; purity
  via rowSlotWalk); generalized Path B (`JoinedFoldChunkPlan`, `genFusedLoopNestStreamedJoined`,
  `pathBTeamOpen/Close` shared with Path B and row slots, emitted C++ unchanged there); knob
  `BLADE_JOIN_THREADS`; sibling pass relaxed to the same policy; onepass.blade fold kernel
  `lambda(u, v) where omp -> u + v, 0.0`, `no_parallel_form: false`, run.py false fix; corpus
  loops/486-491. omp-pragma 102/0, omp-reduce 109/0 (17 g7 cases), omp-coverage 10/0 (teams
  1/3/8 observed at n=1009), optimize 236/0, loops 253/0, sibling-folds 18/0, cli 126/0,
  corpus-wiring 14/0, interp loops 254/0, opt-diff loops 223/0/30, `run.py check onepass
  --regimes all` 0 failures (mt8 blade-cpp `openmp (3 constructs)`). onepass full-size
  identity: serial licensed/repro both lanes identical to OLD; mt8 licensed 9e-15 rel of
  serial. **Side effects to measure:** dot and fusedchain now JOIN AND THREAD in the default
  build (the kernel's dot leg loses its K-lane `blade_fold::dot` form inside the joined team
  -> mt8 licensed dot at risk; the A/B must include NEW+BLADE_JOIN_THREADS=0); repro
  fold-form text now `serial (repro): k ascending original chains`. Codex review launched.
- ~08:05: **W2-A done** on `worktree-agent-ad47b0fa7031a085a` (7 commits 215933c9..fce70b45):
  OrbitFold split (`recogniseWalk` / `planLicensed` / `planStrict`), shared total whitelist in
  CodeGenExprSupport (rowSlotWalk reuses it), C++ `blade_orbit_scatter.hpp` (`tri<G,B,VADD,
  THIRD>`, `scatter2`, `scatter3` slices + same-Z third event + retained H), LLVM
  `emitOrbitScatter` (scalar independent chains + explicit `<4 x double>` row chains,
  `scatter_vrow`), precedence orbit -> compact rows -> G35 -> scatter -> walk with explicit
  `BLADE_ORBIT_FOLD=0` handling, honest `llvmLaneDecision`; knobs `BLADE_ORBIT_SCATTER`,
  `scatter_g`=4, `scatter_b`=64; new default-suite block `blade test orbit-scatter` (52
  cases x 8 builds, mutation-checked), corpus symmetry/132-135, G41. optimize 245/0, symmetry
  135/0, orbit-scatter 8/0, llvm symmetry 80/0/55, omp 105/93, cli 127/0, corpus-wiring 14/0,
  interp symmetry 136/0. Identity vs integration binary under REPRO+off both lanes:
  byte-identical. **Directional timing (shared box, whole program, repro serial):** pairkernel
  walk 41.2 -> scatter 31.0 ms C++, 39.5 -> 31.3 LLVM; comoment3 17.3 -> 10.3 C++, 17.7 ->
  10.4 LLVM; LLVM vector vs scalar row chains no consistent winner. Deviations: capture
  dominance = "in scope at the consumer" (nothing moves to the producer); f total always.
  Codex review launched. NOTE numbering: W2-A added G41, W2-F will add G41+ -- renumber at
  merge (W2-E's G39 pre-existed: the onepass/recur mt8-availability row).
- ~08:15: W2-D fixes landed (cb332d35): `anti_g` unset = G=1 (plan line says so; `anti_g=0` =
  cache-budget groups), cache-policy tests pass `anti_g=0` explicitly, seven LinAlgTests
  needles G=4 -> G=1, a cli twin runs symmetry/132 under default and `anti_g=0` in both lanes
  (equal, NumPy-checked); comment wording fixed; tested set {1,2,3,4,8} stated; two
  pre-existing `llvm facts` needle failures from its first commit removed. syrk-tile 4/0,
  row-tile 24/0, llvm tiles 12/0, llvm facts 60/0, linalg 153/4/24/19, optimize 242/0, cli
  128/0, corpus-wiring 14/0, symmetry 132/0. READY TO MERGE (after the wave-1 gates release
  the integration tree).
- ~08:20: Codex review of W2-E: MERGE AFTER FIXES -- should-fix (1) the sibling pass's purity
  language (`sfTotal`: accepts float ^ negative int) is broader than the joined emitter's
  (`rowSlotWalk` rejects it): a synthesized join the emitter then refuses LOSES the requesting
  fold's standalone Path B team (reproducer: sibling-folds/002 with `(x * y) ^ -1`) -> share
  one pre-codegen purity/shape predicate, preserve the original folds on failure, regression
  "applied rewrite implies applied joined team"; (2) missing refusal-boundary assertions
  (no-team/census for captured-write map, writing shared producer, unproven subscript;
  product legs with non-identity seeds; zero-extent seeded join; observed OMP_THREAD_LIMIT=3
  run). Policy/seed/emission/refactor/harness audit clean; item 7 recommendation (a): accept
  and let the A/B decide (OLD vs NEW vs NEW+BLADE_JOIN_THREADS=0, plus OLD with the new
  onepass spelling); if mt8 dot materially regresses choose (b) in sibling admission. Sent to
  W2-E.
- (file clock 04:30) **W2-F done** on `worktree-agent-af3ca5e161323786d` (2a0d2878 bug 1;
  973e7e92 bug 2; 9bf284b0 bug 3). Bug 1 (fix a, formalism 5.2: a serial body's captured
  write is an ordered side effect; a comm kernel is not a parallel body): `foldReorderLicensed`
  now requires `not (callableMayWrite callable)` -- one predicate gating Path B, row slots, K
  lanes, simd and the LLVM flags; a writing map body blocks every reordered form through
  `licensedFold`; reproduced on the integration binary: loops/486 printed 138996548 at
  THREADS=1 (a SERIAL failure too: K lanes evaluated rows before the kernel's writes) and
  five different mt8 values vs 141149320; mirror loops/487 (a `where omp` fold kernel let
  Path B chunk a writing map body); census lines + `LoopNestCodeGen.FoldOmpDecline`; G41. Bug 2:
  `flatTupleIdsCell` + `flatTupleProjection` translate structural projections of a flat C++
  join tuple (inline renders of `t[1]` printed the second leg; `t[0][k]` was BL9002); pins
  tuples/031-032 all lanes; G42. Bug 3: the reproducible class is a JOIN whose legs WRITE --
  three lanes, three schedules (10010 / 16020 / 10026), the interpreter contradicting formalism
  10.2 -> REFUSED in every lane (BL4005 via `Lowering.refuseWritingJoinLegs`, proven writes
  only); legal spellings pinned loops/488, refusals mutability-errors/014-016; formalism 5.2 /
  10.2 sentences; G43 (also records: a named deferred map consumed twice runs twice at top
  level but once in a function body -- all lanes agree; left as a language decision). 23
  blocks green (omp 104/93/8, loops 250/0, tuples 32/0, mutability-errors 16/0, diagnostics
  108/0, docs 131/0/78, interp loops/tuples/mutability-errors/sibling-folds, llvm loops/tuples,
  opt-diff loops/tuples). Codex review launched. Merge notes: W2-E's joined form must keep
  reading `foldReorderLicensed` / `exprMayWrite`; G41 collides with W2-A's G41 -> renumber.
- Gate consoles (file clock): default 03:25, docs/opt-diff 03:31, llvm 03:56, interp still
  running 04:23+ (UTF-16 consoles; the runner reports the TOTAL lines). Log times above
  marked "~hh:mm" are approximate session times, not the file clock.
- Codex review of W2-A: MERGE AFTER FIXES -- blockers (1) the LLVM stored-pool gate accepts
  `hasFlatRead` incl. `AVirtFlat`: an UNFORCED map over a symmetric pool enters the grouped
  schedule and `readFlat` executes the unaudited producer, bypassing forcing/printing rules
  -> require `APool` in v1; (2) the plain-add shortcut inlines a NAMED `where repro` callable
  (`BLADE_REPRO_FN`), removing the contraction boundary the attribute exists for -> keep the
  callable for repro-vetoed kernels + a contract=fast regression (x = 1+2^-27, seed
  -(1+2^-26)). Should-fix: `llvmLaneDecision` marks LLVM Applied from a C++-side marker
  (Float32 / deferred sources refused by LLVM would be misreported); inner-level `where omp`
  requests on an admitted walk vanish from the census. Nit: timing was cross-binary (OLD =
  bench-fortran binary), not same-binary as G41 says; G41 -> G44. Schedule, rank 3, admission
  (the shared whitelist preserves rowSlotWalk semantics), dispatch, tests, hygiene confirmed;
  both deviations ACCEPTED; parity with the repro experts "plausible in both lanes pending
  the runner A/B". Sent to W2-A.
- Codex review of W2-F: DO NOT MERGE yet -- blockers (1) locality decided by initializer
  SYNTAX: `let t = G(0); t(0) = ...` (a view), `let t = get_G()`, projections, returned aliases
  escape the write detector -> classify storage provenance (views/fields/projections/alias
  chains; deep-copied `let t = G` and fresh scratch ARE local); (2) the SHARED scalar fold
  kernel of a fusion tree (`reduce(f <&!> g, writer, 0.0)`) is never inspected; (3)
  `legWrites` / callable resolution ignore let aliases and unknown heads (`let alias = m`
  evades 014; `unknownWrites=false` admits unresolved writers) -> reuse W1-D's effect closure,
  unresolved heads decline. Should-fix: the map-write gate is C++-only (LLVM still decorates
  `fadd reassoc` after executing a writing deferred map); runtime T=1/8 checks of 486/487 in
  omp-reduce + join-specific message/span pins + final-binary re-runs; the scope-dependent
  evaluation count (36 at top level vs 20 in a function body) is an OPEN semantic defect ->
  own gap row G45, and the original "68 vs 20" plain-fold report stays OPEN; G43 is a
  policy CHANGE (new restriction), not restored semantics; BL4005 JSON text; CLAUDE.md lead.
  Confirmed: the G41 predicate fixes the serial failure too; G42's path offsets work for
  arbitrary nesting; FoldOmpDecline per nest; provider writes are a side table. Bug 3 policy
  ACCEPTED. Sent to W2-F.
- Gate consoles decoded (UTF-16): **default TOTAL 7024 passed, 0 failed** (baseline 6800);
  **llvm 780/0/680**; **opt-diff 2037/0/915**; **docs 131/0/78**; interp still running
  (runner summary pending). NEW harness mirror + smoke evidence being staged from the
  6b3e5293 binary.
- W2-E fixes landed (178973e9 shared admission; ca494bac tests): new `src/IRTotality.fs`
  (after IRMono, before Optimize) holds the closed purity/totality whitelist used by
  rowSlotWalk, the joined emitter AND the sibling pass; a requesting group joins only if the
  knob is on, every leg licensed, one builtin op, every map passes the audit (the `(x*y)^-1`
  reproducer now declines with the reason and keeps its Path B team); refusal-boundary cases
  in both entry shapes, product legs (identity 1 partials), zero-extent seeded joins,
  index-identity rejection on hand-built IR, OMP_THREAD_LIMIT=3 observed (team 3 of 8
  requested, five slots unpublished). omp-pragma 103/0, omp-reduce 117/0, omp-coverage 10/0,
  optimize 238/0, loops 253/0, sibling-folds 18/0, cli 126/0, corpus-wiring 14/0, interp loops
  254/0, opt-diff loops 223/0/30, symmetry 131/0. READY TO MERGE after W2-D. W2-A asked to
  merge W2-E's branch into its own and adopt IRTotality (it had moved the same whitelist into
  CodeGenExprSupport) before it reports. Wave-2 merge order: W2-D -> W2-E -> W2-F -> W2-A.
- **WAVE-1 GATES ALL GREEN on 6b3e5293** (runner `w1-gates-summary.md`): `blade test` TOTAL
  7024 passed, 0 failed, 0 skipped (18.5 min; baseline 6800/0/1 -- the docs skip gone);
  opt-diff 2037 passed (1845 inert), 0 failed, 915 skipped (5.5 min; baseline 1999);
  docs 131/0/78; llvm 780/0/680 (24.3 min; baseline 745/0/677); interp 2949/0/3
  skip-unsupported (48.6 min; baseline 2912/0/2 -- the new skip is the compaction
  mutated-compound fixture, no interpreter compound element store). No BL9002, no Value
  Error. Box was loaded (builders + an unrelated `blade ide serve`), wall times not
  timing-grade.
- W2-D merged (e33daa84) and W2-E merged (clean, no conflicts) into `worktree-bench-fortran`;
  building. Wave-1 harness A/B next: OLD mirror (4136f0b5) vs NEW mirror (6b3e5293 copy,
  smoke evidence being generated), with W2-A/W2-F idle.
- Merged tree (W2-D + W2-E) built clean (Blade.dll 64E4DD2C); post-merge blocks all green:
  optimize 246/0, omp 103/117/10, linalg 24/19, syrk-tile 4/0, row-tile 24/0, llvm tiles
  12/0, llvm facts 60/0, loops 253/0, sibling-folds 18/0, symmetry 132/0, index-types 380/0,
  cell-epilogue 56/0, fold-lanes 4/0, cli 128/0, corpus-wiring 14/0; `run.py check onepass`
  (serial + mt8 regimes) 0 failures, the new mt8 blade-cpp arm ok with 3 OpenMP constructs.
- NEW mirror staged (bin copy of 6b3e5293, hash 3061E900; bench content identical to OLD
  except gap-register + round2 notes); NEW smoke exit 0, 536 cells 484/42/8/2 = OLD, 0
  wave-1 regressions. **WAVE-1 A/B STARTED** (OLD 4136f0b5 vs NEW 6b3e5293, quiet-box check
  first); W2-A and W2-F asked to IDLE.
- W2-A fixes landed (50f18be4: LLVM gate takes `APool` only with regressions incl. the
  first-error order; `where repro` kernels excluded from the inline-add shortcut -- the bug
  was CONFIRMED first: old C++ scatter printed 5.55e-17 where the walk prints 0.0 under the
  default contracting build; symmetry/136 pins `"0.0"` at contract=fast in both lanes; LLVM
  emitter records its own scatter decisions merged by span, G35 marker no longer claims the
  scatter, cli cases for G35 / unforced source / Float32; `scatterThreadRequests` collects
  every level's requests for the census; row renumbered G44, timings labelled cross-binary)
  and W2-E MERGED IN (52d0c4aa: `planStrict` judges totality with `IRTotality.walk` /
  `auditCallable`, duplicate helpers deleted; CodeGenBinding = W2-E's file + 130 scatter
  lines). Post-merge: optimize 249/0, symmetry 136/0, orbit-scatter 17/0, omp 109/117/10,
  loops 253/0, sibling-folds 18/0; cli / corpus-wiring / interp symmetry still to run on
  RESUME. **Pre-existing bug found (card):** a logical walk reading an UNFORCED
  `method_for(g) <@> lambda(v) -> ...` map over a compact pool fails BL9002 in the C++ lane
  (`'h2' was not declared`), integration binary too, scatter knob off too -- the
  unforced-source regressions are LLVM-only for now. W2-A idle.
- W2-F fixes landed (e69b9f83 G41: storage-provenance write analysis -- local only when the
  storage is proved fresh in the body: literal / `|> compute` / let-rec buffer / scalar /
  `let mut` deep copy / alias-view-projection of such; views of captured arrays, call results
  and unproved = external; argument-aware callee summaries; deferred producers followed to a
  materialization boundary; unresolved head = write; loops/489 (row-view write) old
  138996548 + three mt8 values -> new 141149320 everywhere; LLVM `FoldOperandWrites`
  suppresses fold fast-math when the operand writes, loops/490 + llvm goldens; omp-reduce
  g41 runtime cases 486/487/489 at T=1 and 5x T=8. 85a02d76 G43: refusal checks every
  IRReduceCompute with 2+ leaves or a tuple kernel incl. the shared scalar kernel, callables
  through let aliases, deferred aliases via the let table, unresolved callee = write;
  mutability-errors/017-020; 014-020 pin message + span; G45 OPENED (36 vs 20
  scope-dependent evaluation; loops/491) and the "68 vs 20" report recorded open; policy
  wording. 03e88cb4 G42 follow-up: a function-body alias of a flat join tuple (BL9002 old and
  new) now routes through genBinding; tuples/033.) 25 blocks green on the final binary.
  Declared limits: codegen-side analysis cannot see `let mut` deep copies (conservative
  decline); `foldReorderLicensed` does not follow deferred maps a fold KERNEL reads; a
  possible checker bug with a local `let mut s` shadowing the fold's own `s` (renamed in the
  test). Codex RE-REVIEW launched (read-only during the A/B window). W2-F idle.
- Codex re-review of W2-F: DO NOT MERGE yet -- 2/5/6/7/8 closed; NEW blocker 9: provenance
  frozen at the initializer (a local rebound to a captured view `t = G(0)` keeps "fresh");
  NEW high 10: the deferred closure stops at IRChoice/IRFallback/IRGuard/IRSequence wrappers
  (a named choice of a writing map escapes the join refusal) and an unresolved IRVar in a
  KERNEL slot falls through as pure; NEW blocker 11: ordinary IRReduce (e.g. over a choice)
  bypasses LLVM's FoldOperandWrites, and a fold kernel reading a writing deferred map gets
  `reassoc` on its inner fold. Merge hazards: W2-E's tests (loops/489, three g7 decline cases)
  encode the OLD acceptance policy and must become BL4005 refusals under G43; joined-team
  admission must intersect write-aware foldReorderLicensed with every leaf/shared-producer
  effect; loops/486-491 numbered on both branches. Sent to W2-F with the instruction to merge
  the integration tip d332f700 (wave 1 + W2-D + W2-E) into its branch now (git only) and
  reconcile, building after RESUME.
- **WAVE-1 A/B MEASURED** (runner `w1-ab.md`; OLD 4136f0b5 vs NEW 6b3e5293; 20 sessions,
  05:13-08:15 file clock, quiet box 3-5% load, AC/Silent/uncapped; harness defaults, 21
  rounds; no failure/abort/excursion; controls within 0.95-1.04). Expert ratios
  (gf / ifx, > 1 Blade faster) OLD -> NEW: comoment3 mt8 repro **0.497/0.436 -> 1.080/0.950**
  (G35; NEW/OLD 0.458 live); pairkernel mt8 repro **0.383/0.372 -> 0.687/0.678** (G35; 0.546);
  pairkernel mt8 licensed **0.797/0.791 -> 1.020/0.967** (B3; 0.801; indicative both sides);
  maskagg serial licensed **0.856/0.619 -> 1.062/0.788**, repro 0.893/0.688 -> 1.062/0.813
  (B4); recur serial repro **0.993/1.016 -> 1.337/1.369** (B2r; 0.745); stencil serial repro
  1.229/0.986 -> 1.416/1.134 (B2r); saxpy serial repro 1.062/0.667 -> 1.375/0.878, serial
  licensed 1.255/0.925 -> 1.377/0.972, mt8 licensed 0.824/0.840 -> 0.938/0.970 (ind) (B1);
  dot serial repro **1.213/0.739 -> 1.696/1.012**, fastmath 1.395/0.671 -> 1.631/0.782,
  licensed 1.455/0.971 -> 1.642/1.090, mt8 repro 0.814/0.859 -> 1.163/1.182 (C); onepass
  serial repro **1.348/0.803 -> 1.714/1.044**, fastmath 1.477/0.745 -> 1.664/0.820, licensed
  1.449/1.005 -> 1.635/1.119 (C); fusedchain serial repro **1.393/0.823 -> 1.852/1.093**,
  fastmath 1.479/0.808 -> 1.688/0.941 (ind), mt8 repro 0.731/0.827 -> 1.017/1.128 (ind) (C).
  Controls unchanged: gram mt8 1.284/1.217 -> 1.267/1.218; comoment3 serial licensed
  1.300/1.305 -> 1.303/1.323; recur serial licensed 1.116/1.166 -> 1.075/1.114 (NEW/OLD 1.032
  [0.993, 1.053], not beyond CI); stencil licensed flat. **No Blade arm slower than OLD beyond
  its CI.** Remaining losses after wave 1: pairkernel serial repro 0.932/0.879 and comoment3
  serial repro 0.770/0.780 (the SCATTER's targets, wave 2); pairkernel mt8 repro 0.687/0.678;
  stencil mt8 repro 0.822/0.875 and mt8 licensed 0.903/0.970 (both ind); saxpy mt8 repro
  0.811/0.871 (control cell, moved anyway: twins moved); fastmath ifx dot/onepass 0.78/0.82.
  Twins: C's rule joins the input-only twins too (dot repro twin 0.849, saxpy repro 0.889);
  nets reported separately in `w1-ab.md` 2b.
- W2-A MERGED (763cbc3a; one CLAUDE.md two-row conflict three-way merged). Build clean
  (Blade.dll F1690954...); 18 post-merge blocks green on 12ec4734 (`postmerge-W2A.md`:
  optimize 257, orbit-scatter 17, omp-pragma 109, omp-reduce 117, symmetry 137, cli 132,
  loops 253, sibling-folds 18, index-types 380, cell-epilogue 56, tiles/llvm all 0 failed);
  harness checks onepass / pairkernel repro / comoment3 repro clean. W2-A's own last blocks on
  52d0c4aa green (cli 130, corpus-wiring 14, interp symmetry 137, llvm symmetry 81/0/55).
  W2-F REPORTED (merge d63293e6 of d332f700 + fix commit 29cb3506 for findings 9-11: rebinding
  provenance joins initializer with every later assignment (all three lanes alias on a whole-array
  rebind; loops/498, mutability-errors/022), deferred wrappers (choice/fallback/guard/sequence,
  tuple projections, unresolved function-typed kernel name = write; 023, 024), every LLVM fold
  entry sets FoldOperandWrites + a per-program let table for captured named deferred values;
  W2-E's writing-join tests re-pinned as refusals (021, g7RefusedCases), joinEffectAudit before
  joinThreadAudit gates both the serial simd form and the team; 25 blocks green on its tree;
  not run: full suite / whole-corpus differentials). Codex re-re-review launched
  (`prompt-diff-W2F-3.txt`, task-mvgom18i-yxuqht, read-only) in parallel with the merge: W2-F
  MERGED into the integration tree as **ea73698f** (conflicts: CLAUDE.md rows ROW_SLOTS +
  ORBIT_SCATTER from W2-A / JOIN_THREADS from W2-F; gap-register rows ordered G41-G45;
  OmpTests `row_slots_declined_outer_kernel_write` takes W2-F's G41 predicate followed by W2-A's
  scatter cases, and now also forbids `blade_scatter::` -- the scatter's `strictWalk` refuses any
  form outside its closed whitelist and any non-scalar capture, so a writing kernel never reaches
  it). Deletions vs parents: 56 vs ours (W2-F's edits), 27 vs theirs (W2-A's rows / recognise
  signature / LLVM own-records) -- all accounted. Build clean (Blade.dll 7DE2D603...); 27 blocks
  green on ea73698f (`postmerge-W2F.md`: omp-pragma 121, omp-reduce 121, loops 259,
  mutability-errors 24, tuples 33, functions 324, diagnostics 108, llvm goldens 23/3/64/19, the
  rest unchanged); harness checks clean. Codex re-re-review (`codex-diff-W2F-3.md`): DO NOT
  MERGE on four NEW pre-existing holes of the G41 class -- 12 HIGH partial-view provenance,
  13 BLOCKER LLVM orbit-class fold ignores FoldOperandWrites (3 canonical cells for 4 logical
  reads of a writing map), 14 BLOCKER hot-row scheduling moves a consumer read across the
  producer's writes (12 vs 9), 15 HIGH flat-epilogue / flat materializer `BLADE_IVDEP` on a
  writing producer; 9 and merge requirements (a), (c)-(f) CLOSED; the scatter needs no
  FoldOperandWrites read. DECISION: the merge stays (fixes real wrong answers, regresses
  nothing); 12-15 returned to W2-F as ONE fix commit on top of a merge of 62b87c31 into its
  branch (RESUME sent 09:15), to be merged before the wave-2 gates; G41's row must stop
  claiming "every entry" until then. Both records committed (62b87c31). Meanwhile the w1
  mirror (6b3e5293 binary + wave-2 bench) was made and smoked (all benched pairs, 0 failures,
  Blade.dll 3061E900...).
- W2-F FIXED 12-15 as **bd34627b** (one commit on a fast-forward of 62b87c31; 9 files, +198/-12):
  13 tryOrbitFold declines before emitting when FoldOperandWrites or the source may write
  (8/3 -> 10/4); 14 planHotRowFolds declines a MayWrite producer kernel (9 -> 12, all lanes,
  repro too); 15 exprMayWrite gates ivdep in the general nest, the plain flat loop and the
  flat-epilogue nest (pure producers keep it); 12 the chase follows array views (partial index,
  slice, shift, reverse, curry) and IRApp(IRObjectFor) counts as deferred, `|> compute` the
  boundary. Tests: llvm goldens G41-12/G41-13 with controls, finding-11 goldens identify the
  accumulator structurally, optimize hot-row decline (+repro), omp-pragma ivdep declined x3 +
  kept x2, loops/499, mutability-errors/025. Blocks green (optimize 259, omp-pragma 126,
  loops 260, llvm goldens 66 fact layer, interp loops 261, opt-diff loops 230). Open: the probe's
  writing virtual source is BL9002 in the C++ lane (pre-existing, in G41); compact-row-classes
  planner not re-audited for a class-partial body reading producer-written storage. Integration
  branch FAST-FORWARDED to bd34627b = THE WAVE-2 TIP. Build + five gates launched
  (`gates/w2/gates.ps1`, consoles `gates/w2/gate-*.txt`); Codex review of bd34627b launched in
  parallel (`prompt-diff-W2F-4.txt`, task-mvgqeu1g-3nn44i).
- FIVE GATES GREEN on bd34627b (Blade.dll 28EA3E0A...; consoles `gates/w2/gate-*.txt`, timing
  `gate-timing.txt`): default **7135 passed, 0 failed** (no skip; 14.5 min), opt-diff 2058/0/928
  sk (3.6 min), docs 131/0/78 (0.5), llvm 809/0/681 (16.6), interp 2983/0/3 skip-unsupported
  (39.9). Build: 2 FS0046 warnings (`pure` identifier in tests/LlvmTests.fs:871/876, W2-F's test).
- Codex on bd34627b (`codex-diff-W2F-4.md`): 13 CLOSED (8/3 right for the materialized writer);
  12/14/15 NOT CLOSED via four more same-class holes: **16 BLOCKER compact row classes (W1-C's
  B3!) never check the producer** (consumer reads a capture the `comm` producer writes: 12 vs 9);
  17 BLOCKER hot-row with a writing OUTER kernel / seed (1 vs 0); 18 HIGH producer SIMD /
  team emitted on a writing producer the lexical BL4005 check misses (local row view alias)
  -- `allParallel` wins before `producerWrites`; 19 HIGH the tuple chase's live filter omits
  views. (e): no new decline on the bench kernels. RESUME sent 11:05 with the instruction to
  close the CLASS (every admission reads the shared write analysis for producer, consumer,
  outer kernel, seed, intervening bindings; a per-pass table in the G41 row), plus test
  strengthening (f) and the `pure` rename.
- W2-F FIXED 16-19 as 843cb654 on its branch, CHERRY-PICKED onto the integration branch as
  **b161cf10** (14 files, +290/-29; diff --check clean; 0 build warnings). The class closed in one
  place: `Optimize.scheduleMayWrite` / `scheduleMayWriteIn` (shared writeAnalyzer over a module:
  callees through functions and let aliases, named deferred values through every let, `let mut`
  deep copies) consulted by every moving pass for producer / consumer / outer kernel / seed /
  intervening (matrix in the G41 row; cseFactsOf only where it already was). 16: 9/3 -> 12/3
  both lanes (loops/500; pairkernel still `[compact-row-classes v1] applied`; index-types 380,
  symmetry 137); 17: 0.0 -> 1.0 (loops/501); 18: refused in Lowering by provenance (BL4005,
  mutability-errors/027-028) + the flat path's write veto overrides SIMD/team routes with a
  census line + buildFlatEpilogue declines a writing producer; 19: tuple components chased with
  views (026; llvm goldens G41-19). ivdep negatives ban every directive spelling + producer SIMD
  pragmas with required-path markers (the bare word `ivdep` appears in emitted comments, so not
  banned). Blocks green (optimize 262, loops 262, mutability-errors 28, llvm goldens 67 fact
  layer, interp loops 263 / symmetry 138, opt-diff loops 232 / symmetry 99). Limits: no
  cross-module test of scheduleMayWrite (a cross-module call = possible write = decline: speed
  only). Build + five gates RE-RUN launched on b161cf10 (`gates/w2/gate-*.txt`; the bd34627b
  consoles archived in `gates/w2/run1-bd34627b/`); Codex review of 843cb654 launched
  (`prompt-diff-W2F-5.txt`, task-mvgu1x42-jnlqro).
- FIVE GATES GREEN on b161cf10 (Blade.dll DCDCDE0C...; `w2-gates-b161cf10.md`, consoles in
  `gates/w2/run2-b161cf10/`): default 7143/0, opt-diff 2060/0/931, docs 131/0/78, llvm
  812/0/681, interp 2988/0/3 -- 17 min total thanks to exe-cache hits.
- Codex on 843cb654 (`codex-diff-W2F-5.md`): 16/17/19 CLOSED; 18 NOT CLOSED via **20 HIGH**
  (an unresolved CALLBACK -- a function-valued parameter called inside a `where omp` kernel --
  escapes the lexical check and the walk (UnknownIsWrite=false); the general nest has no
  producer-write veto: threads-on races) and **21 MEDIUM regression** (the new Lowering refusal
  passes the body with EMPTY parameter maps, so a legal write to the function's OWN mut
  parameter is refused). (a): matrix admissions present, but domain diagonal / hoisting / CSE /
  sibling sharing keep their own older guards (not the new view-aware closure); (e): no bench
  kernel loses a form; an IMPORTED pure cell producer can newly decline the epilogue
  (module-local scheduleMayWrite); backend CopiedLet=false can reject a private copy. RESUME
  sent 11:50 for 20-21 (+ program-wide functions where cheap, extra pragma spellings in the
  negatives, CopiedLet note).
- W2-F FIXED 20-21 as d9289387, CHERRY-PICKED as **8e5764ac** (12 files, +254/-22): callbacks
  judged through every call site's argument (worst stands in; unresolved = write in a parallel
  body; writing/unresolvable callback BL4005, pure keeps the team; backend
  `programCallbackArgsCell`); general nest's team route takes the producer-write veto with a
  census line; own mut parameters kept apart from captured storage
  (`callableWriteSummaryWith`); AND a regression of 843cb654 caught by a 181-program rescan:
  examples/mandelbrot.bladenb was BL4005 because a self-recursive pure helper was summarized
  from the pessimistic start -- recursion is now a least fixpoint from "writes nothing" (8
  rounds, pessimistic fallback); loops/502-504, mutability-errors/029, omp-pragma x2.
  Planners take programFuncs (cross-module callees resolve); more pragma spellings banned;
  G41 row lists the other reordering passes with their own admissions. NEW OPEN **G46**: a
  kernel-body `let mut c = G` aliases G in the C++ lane but is copied by the interpreter
  (pre-existing lane divergence; no race because the team is dropped). Blocks green (loops
  265, mutability-errors 29, omp-pragma 128, interp loops 266 / functions 325, opt-diff loops
  235). Build + gates RUN 3 + an examples/notebooks `blade check` sweep launched
  (`examples-check.ps1`); Codex #6 launched (`prompt-diff-W2F-6.txt`).
- GATES RUN 3 GREEN on 8e5764ac (`w2-gates-8e5764ac.md`, consoles `run3-8e5764ac/`): default
  7149/0, opt-diff 2063/0/932, docs 131/0/78, llvm 814/0/682, interp 2992/0/3. Examples sweep
  (`blade check` of 68 examples/*.blade|bladenb): 6 non-zero exits -- 4 zarr stores absent at
  compile time (BL2008, data not in the repo), tools/check_cod_shells.blade BL4003,
  station_temps.bladenb BL3999 -- ALL SIX IDENTICAL on the master 4136f0b5 and wave-1 6b3e5293
  binaries (same codes, same exits): pre-existing, not regressions (the zarr ones are absent
  data; BL4003 / BL3999 are old diagnostics on old examples -- follow-up card).
- Codex #6 on d9289387 (`codex-diff-W2F-6.md`): 21 CLOSED; 20 NOT CLOSED via **22 HIGH**
  (both callback resolvers pick ONE candidate -- the first the plain resolver calls writing --
  which can turn out pure and hide a later writer; fix: join the SET of candidates' effects)
  and **23 HIGH** (the recursion cutoff is per callable, its fallback is not top (External =
  false) and summaries under the unfinished approximation stay cached; fix: SCC fixpoint,
  widen to top on budget, invalidate dependents). (e): pure-program emission CAN change
  (pure callbacks newly admit SIMD/teams/ivdep; programFuncs admits imported producers) --
  bench byte identity not established by review; the A/B measures the final tip anyway, and an
  emission diff of the bench kernels between bd34627b and the final tip will say whether the
  audit fixes are inert on them. RESUME sent 12:45 for 22-23 (+ the own-parameter per-cell
  race negative, the loops/504 pragma positive). Then: Codex #7, gates run 4, mirrors + smoke
  + A/B.
- BENCH EMISSION IDENTITY (`gates/w2/emit-identity.ps1`, result `emit-identity.txt`): `blade
  emit` of all 12 bench kernels x {cpp, llvm} x {licensed, repro} (harness-like env: BLADE_*
  and OPENBLAS_DIR cleared, lane/regime knobs set) with a fresh build of bd34627b
  (C:\bt_r2emit, detached worktree) vs the 8e5764ac build: **48 same, 0 differ, 0 failed** --
  W2-F's audit fixes 12-21 are byte-inert on the bench. Re-run against the final tip before
  the A/B. (PowerShell trap: `$K` and the loop variable `$k` are the SAME variable.)
- W2-F FIXED 22-23 as 463c3fee, CHERRY-PICKED as **ba585a28** (11 files, +208/-81): callbacks
  judged over EVERY candidate (`WriteFacts.Callbacks`: parameter -> every call site's
  argument; joined; unresolvable = write; only function-valued parameters count --
  `isFunctionValuedType`, after an intermediate build mistook index-arrow array parameters for
  callbacks and refused 9 accepted programs); recursion iterates to 64 rounds then widens to
  TOP (External + every storage parameter), unfinished summaries recomputed.
  mutability-errors/030-034, loops/505, omp-pragma team positives x2. (c): the front end
  already refuses a write to an own mut parameter INSIDE a `where omp` kernel (same-cell and
  per-cell-disjoint alike; conservative, in G41). Rescan of 187 programs: only the intended
  refusals. Blocks green (loops 266, mutability-errors 34, omp-pragma 130, interp loops 267).
  Build + GATES RUN 4 + examples sweep + emission identity vs bd34627b launched on ba585a28;
  Codex #7 launched (`prompt-diff-W2F-7.txt`). POLICY: #7 is the last review round -- any
  further finding that is neither a wrong answer nor a regression of an accepted program
  becomes a G-row + follow-up card, not another fix cycle; the A/B runs on the tip #7 gates.
- GATES RUN 4 GREEN on ba585a28 (Blade.dll 3796F2FD...; `w2-gates-ba585a28.md`): default
  7157/0, opt-diff 2064/0/937, docs 131/0/78, llvm 814/0/683, interp 2998/0/3; examples sweep
  the same 6 pre-existing; emission identity vs bd34627b 48/48.
- Codex #7 on 463c3fee (`codex-diff-W2F-7.md`): 23 CLOSED; 22 NOT CLOSED via **25 HIGH**
  (second-order callback propagation: `via(launch, bump)` never feeds launch.f, so a writing
  callback races); **24 BLOCKER = REGRESSION**: `isFunctionValuedType` excludes NULLARY
  functions (`() -> Float64` has no SVal slot), so `f()` in a pure `where omp` kernel is newly
  refused BL4005 (no checked-in program hits it; (d) none newly refused in examples / stdlib
  / bench). RESUME sent 13:35: fix 24 (one-liner) + 25 (callback-argument graph to a fixpoint,
  one shared collector) + a > 65-parameter widening regression, ONE commit -- the last fix
  round; then a short Codex #8 in parallel with gates run 5, mirrors + smoke + A/B.
- W2-F FIXED 24-25 as 804516d1 (Opus trailer -- the builder's session attribution; kept, with
  the Fable landing line appended), CHERRY-PICKED as **1cf90428** (13 files, +312/-44):
  `isFunctionValuedType` accepts empty or SVal-bearing slots (loops/506, mutability-errors/035);
  ONE shared `Optimize.callbackTable` (fixpoint over indirect calls, actuals propagated into
  every candidate's slots, escaping owners taint = unknown = write) used by Lowering and
  both emitters (036-038 refusals, loops/507-508 + omp-pragma team controls; 036-038 compiled
  with propagation off); 039 exercises the round-64 TOP branch (68-parameter rotation). Rescan
  195 programs: only intended refusals. Blocks green (loops 269, mutability-errors 39,
  omp-pragma 133, interp loops 270). Build + GATES RUN 5 + examples + emission identity
  launched on 1cf90428; Codex #8 (last) launched (`prompt-diff-W2F-8.txt`).
- GATES RUN 5 GREEN on 1cf90428 (Blade.dll 2293124A...; `w2-gates-1cf90428.md`): default
  7168/0, opt-diff 2067/0/942, docs 131/0/78, llvm 814/0/686, interp 3006/0/3; examples sweep
  the same 6 pre-existing; emission identity vs bd34627b 48/48.
- Codex #8 on 804516d1 (`codex-diff-W2F-8.md`): 24 CLOSED; 25 NOT CLOSED via 27 HIGH (a local
  alias `let k = h` of a callback parameter hides the edge); **26 BLOCKER** (a MODULE-level
  alias `let run = launch` is taken as an escape: a pure program newly BL4005) and **28
  BLOCKER** (a not-yet-populated slot is treated as unknown during discovery and the taint is
  never withdrawn: an all-pure third-order program newly refused by declaration order) --
  both regressions of accepted pure programs introduced by the new collector, so one more fix
  round (RESUME 14:12, 26-28 + the 1000-round budget widening to top). Review policy amended:
  Codex #9 scoped to "regressions of pure programs and the three fixes" only; anything else
  becomes a G-row. MEANWHILE (overlap, correctness only): mirrors w2/w2a/w2b built from the
  1cf90428 binary and smoked now; the A/B will run on that binary IF the final tip's bench
  emission is byte-identical to 1cf90428's (emit-identity against it) and src/cpp is untouched
  -- then "measured on 1cf90428, bench-identical to the final tip" is the recorded claim;
  otherwise re-mirror + re-smoke on the final tip.
- MIRRORS w2 / w2a / w2b MADE from the 1cf90428 build (Blade.dll 2293124A...) and SMOKED (all
  benched pairs, 0 failures; w2a bundles show `declined -- ordered orbit scatter not taken:
  disabled by BLADE_ORBIT_SCATTER=0` both lanes). PowerShell trap: `-File` passes
  `-Mirrors w2,w2a,w2b` as ONE string (a bogus `C:\bt_r2ab\w2,w2a,w2b` mirror was made and
  removed); use `-Command "& 'script' -Mirrors w2,w2a,w2b"`.
- W2-F FIXED 26-28 as 362470f8, CHERRY-PICKED as **f1910060** (12 files, +345/-84): the
  callback table REWRITTEN as a monotone least-fixpoint abstract interpretation (tracked
  names = callback params, local lets, module bindings; value = callable set + unknown bit +
  tuple components; empty start; unknown only from real sources / escapes; aliases evaluated;
  budget 10000 -> widen to empty = all unknown); loops/509-513 (+ five omp-pragma team
  cases), mutability-errors/040-041; rescan 202 programs clean; src/cpp untouched since
  1cf90428. Build + GATES RUN 6 + examples + emission identity vs the w2 mirror binary
  launched; Codex #9 (scoped: closure of 26-28 + regressions of accepted programs only)
  launched (`prompt-diff-W2F-9.txt`, task-mvh0s9v0-buob0y).
- **GATES RUN 6 GREEN on f1910060 = THE WAVE-2 TIP** (Blade.dll C7B14497...;
  `w2-gates-summary.md`): default **7180/0**, opt-diff 2072/0/944, docs 131/0/78, llvm
  814/0/691, interp 3013/0/3; examples sweep the same 6 pre-existing; emission identity vs the
  w2 mirror binary (1cf90428) **48/48**, src/cpp untouched -> the A/B on the 1cf90428 mirrors
  measures the final tip's bench programs bit for bit.
- Codex #9 (`codex-diff-W2F-9.md`): 26, 27, 28 and the budget CLOSED; **29 = a precision
  regression of the new escape rule**: returning a local alias of a callback parameter
  (`passthrough(h) = { let k = h; k }` then `passthrough(launch)(ident)`) or passing a
  tuple that holds a callable to a function that reads only its scalar component poisons
  launch.f, so a PURE higher-order program is refused BL4005 that master accepted; no
  checked-in program (examples, physics, stdlib, bench) hits it; out-of-scope holes: none.
  DECISION (orchestrator): ship the wave with 29 recorded as OPEN gap **G47** (conservative
  side -- a refusal, never a wrong answer; the fully precise rule needs return-flow and
  tuple-parameter tracking in the callback lattice) + a follow-up card. The effect-analysis
  track consumed nine review rounds; the lattice rewrite is the right base for the follow-up.
- **WAVE-2 A/B LAUNCHED 14:55** (`driver.ps1`, progress `ab-progress.txt`, consoles
  `bench-<kernel>-<regime>-<mirror>.txt`; 21 sessions; box quiet: W2-F and Codex idle).
  Wave-2 A/B scripts
  drafted in `gates/w2/` (mirrors.ps1: w1 = 6b3e5293 binary + wave-2 bench, w2 = tip, w2a =
  BLADE_ORBIT_SCATTER=0, w2b = anti_g=0,tri_edge=0 + BLADE_JOIN_THREADS=0; smoke.ps1 only
  the benched pairs; driver.ps1 12 cells x 2-3 mirrors = 21 sessions; analyze.py).

## 6. Decisions taken by the orchestrator

- D1, D3a, D4-team-merge dropped on the counts in the D brief (confirmed by Codex: see section 8).
- B3 moved from wave 2 to the back half of wave 1: it is the clearest licensed loss with a
  confirmed mechanism (D4), and the orbit-dispatch conflict with A2 is resolved by sequencing (A2
  is wave 2 and rebases on B3).
- D2 moved from wave 1 to wave 2: it adds margin on cells Blade already wins; the four wave-1
  builders go to losses. D1-0 (zero code) runs now.
- maskagg `keep` stays stored in v1.
- The relational whole-region track and the N-series workloads are follow-up sessions, not this
  round.

## 7. Review status

**D / E (Codex, `codex-review-DE.md`).** D1 ACCEPT (drop, conditional on the D1-i instrument);
D2 ACCEPT WITH CHANGES; D3a ACCEPT (drop); D3b ACCEPT WITH CHANGES; D4 ACCEPT WITH CHANGES; G7
ACCEPT WITH CHANGES; G5-admission / G5-persistent ACCEPT WITH CHANGES. Corrections carried into
the builder briefs:

- D2: state the invariant as "whole-call grouping visits the same (i0, j0) tiles with the same
  parameters" (global store order changes, pool bytes do not); label the byte model "B bytes if
  each distinct fetch misses L2" (modeled A+B traffic falls 42.7%, not 75%); the 256 KiB budget
  covers the ACTIVE B pair only; both drivers AND `EmitLlvm.fs:243-247` scratch become G-aware;
  `llvm_tiles_rawbit.c` gets G, exact-size and short-scratch cases plus a positive route
  assertion (the C core silently falls back to scalar chains on short scratch); fix the stale
  "no K blocking" comment at `blade_packed_gemm.hpp:373-375`; `anti_kc` blocks are per tile
  call, G multiplies only packed A.
- D3b: the brief's `L0 <= L1`, `L1 = 0 when nr <= 4`, "broadcast rows < L1" dispatch SKIPS
  LIVE WORK on left-only head panels (p=171, i=0, j0=0, mr=6, nr=3: six real cells, formula
  gives L0=4, L1=0). Use per half h: 0 when `nr <= 4h`, else `clamp(c0 + min(nr, 4h+4) - r0 -
  S, 0, mr)`; broadcast rows below `max(L0, L1)`; include the (L0 > 0, L1 = 0) family; avoid
  unsigned underflow; pin nr = 1..4 explicitly. 7.3% is dead vector-halves (staircase +
  padding), not "padding".
- D1 counts: today 3 fork/join + 9 barriers; team=1 would be 7 (6 + join), team=2 4 (3 + join).
  Still dropped; D1-i instrument stays the gate.
- D4: "the 1.51 ms gap IS the cold checksum pass" is the LEADING HYPOTHESIS, not a result (the
  5.4 / 1.5 ms split is a diagnostic single run; ~0.6 ms unexplained; L3 capacity does not
  establish a DRAM minimum). B3's A/B must include a same-team two-pass control and phase +
  total timing. The row-slot determinism rule needs a fixed within-row scheme (class sums
  computed from zero per row, stored once, combined ascending), never per-thread carries.
- G7: see the table row; the "no source change" promise is withdrawn. Seed contract = the
  builtin-`+` ChunkSimd specialization (zero per-chunk partials, original seed once); custom
  kernels keep the has-value machinery or decline. Threading must enclose the shared traversal
  (one load, one shared-producer evaluation per coordinate, both entry paths pinned).
- G5: current slice-write fusion REJECTS a licensed final map (`IRMono.fs:2523-2527`,
  `Optimize.fs:1826-1856`), so adding `where omp` alone can make the OLD compiler slower
  (temporaries return). The A/B must have five arms: OLD/original, OLD/licensed, NEW/licensed
  fused-serial, NEW/step, NEW/persistent. The licensed persistent schedule may split the
  inner row chain only under the row kernel's own builtin licence; repro keeps barrier +
  one complete ascending row fold. Any IRId-keyed `RecStepPlan` must be finalized and
  revalidated AFTER row-fold fusion, hoisting, `IRLift`, `forceCallableBody`, CSE,
  `eliminateDeadPolymorphs` and `qualifyModuleNames` (seams listed in the review); fail
  closed on a missing owner. This is a prerequisite, not a follow-up.

**A / C (Codex, `codex-review-AC.md`).** G35 ACCEPT WITH CHANGES; scatter rank 2 and rank 3
ACCEPT WITH CHANGES; scatter v1.1 contraction invariance REJECT (dropped from wave 3: a barrier
on the final term does not give fast/off identity for general f and fold bodies; v1 ships under
the gates' REPRO + CONTRACT=off identity and the bench tolerance covers fast-mode drift);
C sibling rewrite ACCEPT WITH CHANGES; C inliner ACCEPT WITH CHANGES; C -0.0 seed REJECT as an
unconditional replacement (sNaN quieting counterexample -> arbitration, section 8); C mt8
decline ACCEPT. Corrections carried into the builder briefs:

- G35: the decisive consumer gate is `CodeGenLoopNest.fs:1520` (`FoldChunk.IsSome &&
  ompThreadEmissionEnabled() && fpReassocEnabled()`), which suppresses every chunk plan under
  repro -- exempt the admitted row-slot case from the last conjunct, and adapt private-state
  selection, allocation, cleanup, census and plan records (adding a `ChunkForm` case alone is
  insufficient). Path B does NOT seed chunks with the caller's seed (ChunkSerial starts from its
  first value, ChunkSimd from 0/1, the seed enters the combine once), and neither partial form
  is usable here: compute each complete `body(i)` into `slot[i]`, then apply the original outer
  kernel over the slots ascending from the original seed. Require one complete scalar result
  per slotted outer index. Panic refusal is necessary (`blade_runtime.hpp:361-390`:
  first-arriving worker wins -> nondeterministic failing coordinate); admit pure, independent,
  immutable reads AND total bodies. The 2^18 work gate is provisional (no timing behind it).
  Test fixtures must EXCEED the gate (rank-2 extent 521 / rank-3 extent 67) or pin
  `rowslots_min` low, and assert a live team + slot emission; keep separate below-threshold
  refusal tests. comoment3 rows have equal arithmetic (171 x 171 each; chunk imbalance 1.03).
  Implement the `BLADE_ORBIT_FOLD=0` escape explicitly (the orbit `Declined` arm also fires for
  the disabled knob). Give G35 precedence over the scatter only once it passes its gate.
  `CliCommands.fs:988-998` maps unfamiliar fold forms to `reassoc` -- add honest scatter /
  row-slot translations. Byte identity is stated under contract-off only.
- Scatter rank 2: the G-group order proof holds (independent check n=1..19, G=1..9); the G=1
  first-failure argument covers f only -- fold kernels, seeds and callees must be total, with a
  CLOSED typed whitelist for total f (negative integer powers and lgamma/digamma abort too;
  `pureScalarBody` accepts every binop/unop today) and SrcLoc preserved for a relocated f.
- Scatter rank 3: event census confirmed (2,514,726 adds); the THIRD event (H[j,k] <- z, j > i)
  must be served from the SAME Z block before it is overwritten -- a second pool traversal would
  read another 833,340 cells and void the read-count claim.
- LLVM lane: `EmitLlvm.fs` has NO vector-type emission today (`<4 x double>`, insert/extract,
  shuffle) -- the scatter's LLVM form is new infrastructure (or scalar independent-chain IR
  relying on SLP); budget and test it; GCC's prototype vectorization is not evidence for clang.
- A's drop arithmetic: use lane-specific deficits (LLVM serial repro pairkernel 3.72 / 6.11 ms,
  comoment3 3.48 / 3.37 ms; C++ comoment3 3.72 / 3.61) and matched contraction regimes; the
  universal "scatter < 50% of walk" rule is replaced by "measured walk W and scatter S in the
  same build: W - S >= the lane's deficit" (parity needs S/W <= 0.59 / 0.33 pairkernel,
  0.47 / 0.49 comoment3 with the orbit time approximated as zero). The "mt8 pairkernel
  unreachable from the checksum" claim is withdrawn (the licensed 7.54 ms already includes
  ~1.5 ms of checksum). The prototype's NaN handling is NaN-equivalence, not raw-bit equality;
  label it so.
- C totality judge: `irNodeMayAbort` marks EVERY computed divisor aborting, including Float64 --
  fusedchain's `v / (1.0 + abs(v))` would be refused; explicitly discharge typed float
  division, supported static producer/zip shapes and every referenced callable; inspect seed
  expressions; unknown/recursive callees decline. Movement barriers must cover the whole
  interval between the original folds: intervening CALLS that may mutate an operand or capture
  (reuse CSE's transitive write/exposure reasoning, `Optimize.fs:848-889`), effectful or
  panicking seeds. Inlining preserves SrcLoc and callable attributes; a partial body declines.
- C licensed profitability: joined legs lose the K-lane form (one `omp simd reduction`); the
  record does not establish a dot regression (G26: whole-program default/off/4/8 within noise)
  and the drop experiment measured licensed cells flat -- keep the timing gate, decline
  heterogeneous operators in licensed v1.
- C mt8 decline: a request-only gate may over-decline (Path B also needs a rectangular licensed
  head; expression-position reductions never open teams) -- acceptable for v1, phrase honestly;
  share the exact repro predicate incl. `BLADE_FP_REASSOC=off`.
- C tests: `// EXPECT: s = -0.0` does NOT pin the sign (`tests/Expect.fs:645-653` accepts
  either zero) -- pin `1.0 / s` or raw bits. The BL7004 return-position defect is narrower
  than stated (a shared-deferred-map return fixture; a direct stored-array return join emits):
  always let-bind the synthesized join anyway.
- C post-pipeline: `IRLift`, `forceCallableBody`, CSE, hot-row planning run after
  `optimizeModule`; a let-bound `IRReduceCompute` join should survive (join-only deferred
  operand lets are exempt from forcing) -- add post-pipeline emission needles and gate
  combination tests with B. C's "leave for B" rule must test actual B eligibility, not reserve
  every computed array while B's knob is on.

**B (Codex, `codex-review-B.md`).** P1, P2/B2r, B1, B2l, B3, B4a, B4b+c, B4d all ACCEPT WITH
CHANGES (verdicts authorize prototypes, not default-on shipping before the acceptance
experiments). Corrections carried into the builder briefs:

- P2/B2r attribution: the 4.67 / 3.57 ms repro-minus-licensed gaps are LLVM totals from
  different indicative sessions; stencil repro ALSO serializes its input-row checksum
  (1501 x 2002 ordered adds) which B2r leaves unchanged; the link arithmetic implies ~4.3 GHz.
  Latency is a contributor, not the proven whole. A 16-link chain (~48-64 cycles) hides about
  one, perhaps part of a second, mini-block of recur's ~25-op RK4 cell -- not many blocks.
  Compare the store/reload form against a REGISTER-CONSUME form (`c = producer; out[i] = c;
  acc += c*c`, same bits under contract=off; may lose producer SIMD) and sweep 8/16/24/32/64 with
  assembly inspection. `src/microkernels/README.md` correction 15 measures overlap between
  adjacent independent output chains -- an analogy, not evidence for this schedule; ifx's
  buffered 64-cell ordered consumption (G36) is the closer precedent. The 25%-of-gap rule is a
  falsification bar for the overlap hypothesis only; retention is decided on a measured OLD/NEW
  total gain with no regressions (recur serial repro is already 0.985 / 1.017).
- P1: `detail::fold` assigns full B = K x W blocks to all K vectors, routes remaining whole
  vectors through v0, combines v1..v7 ascending, then lanes, then the < W tail. A streaming
  state reproduces `sq<T,K>` for arbitrary n ONLY if the finish phase first feeds the residual
  floor(r / B) whole blocks (n = 289 after a 256 feed: 33 = one 32-block + 1); B alignment is
  element-width dependent (double/K=8 -> 32, float/K=8 -> 64). Test 255/256/257, 287/288/289,
  multiple producer blocks, every remainder, K = 4/8, float/double, non-dyadic data, both
  contraction modes.
- B1: the bitwise claim needs IDENTICAL PRODUCER VALUES as an explicit precondition (P1's
  barrier fixes square rounding, not `1.5*x + y` contraction under fast): check y2 bits as
  well as the checksum in both modes; the OLD producer macro spells no explicit static
  partition, so do not rely on a remainder distribution; OMP dynamic team sizing must not be
  assumed fixed. mt8 ceiling = the measured separable reread/team share (unknown); first-touch
  of the fresh 16 MB y2 stays. Failure order: compute partials early but keep empty checks and
  seed evaluation at the original consumer site (Repeatable allows MayFail; moving an abort
  across another failing expression changes the diagnostic).
- B2l: opt-in only; spill claims are models; ship only on a measured > 2% win with assembly.
- B3: legal licensed regrouping (disjoint row subsets per class, rows combined, existing
  addition-only `Total`; no FMA-able weighted multiply; every fold-level licence, original
  outer seed, antisym parity handling kept). The expert folds packed COLUMNS into an OpenMP
  reduction -- say "same locality and one-team principle", not "same schedule". 1e-12 at
  p=2503 is plausible (tree depth ~5006, gamma ~5.6e-13) but must be tested with the full pins
  and non-dyadic stress; never relaxed. **Layering blocker:** Optimize.fs (fsproj :141)
  compiles before CodeGenExprSupport (:397) and OrbitFold (:401); the planner cannot hold an
  `OrbitPlan` or call `OrbitFold.recognise` -- use a lane-neutral candidate descriptor in the
  planner and resolve the orbit plan at emission. The 1.5 ms reread attribution stays a
  leading hypothesis; the A/B includes a same-team two-pass control and phase timing.
- B4a: remove the square pool through a NARROW materialized rank-1 Compound-value path
  (trailing stride 1, cardinality/data peels), not by broadly enabling Compound in
  `laneableFoldLevel` (which also controls Path B chunk form and the serial lane arm) or the
  `CodeGenBinding.fs:3772-3797` guard; do not admit `range<CompoundIdx>` drivers, outer
  products, different-mask co-iteration, trailing dims or joined leaves. OLD's licensed fold is
  an unseeded eight-scalar-lane eager reduction, so moving to P1 may change licensed bits
  (legitimate).
- B4b+c: allocate m+1 for BOTH arrays (also m = 0), feed only indices < q, never the slack
  slot; the final `compound_index_t<1>` must have dense extent n, cardinality m,
  `rank_to_tuple.n = m` and ascending kept positions (invariants of `unhash`, binary search,
  `linearize`, `present`, `prefix_range`); keep BL8006 keyed-read semantics; test all-false,
  all-true, last-kept-early, alternating; verify the compiled inner loop is branchless. Repro:
  peel the first kept square, BL8003 after the pass at the same span when m = 0, never a +0
  seed. Executed-access model is ~30 / 38 MB (without / with positions), not 22 / 26; outer
  sweeps 6 -> 2 holds.
- B4d (position elision): DEFERRED -- needs a transitive index-demand census across aliases,
  shared idx, range iteration, calls, prints and provider serialization; `computeScopeEscapes`
  alone is insufficient. B4b+c land with positions always built.
- Gate coverage: opt-diff (REPRO + CONTRACT=off, all gates toggled) sees side-table-driven
  repro emission; it does NOT see licensed B1/B3/B4 forms, `lanes`, or LLVM off/on -- a
  separate licensed block, explicit LLVM on/off tests, lane parity under the identity regime,
  and emitted needles proving each arm fired are required. All-gates-off is not the isolated
  performance control (it also disables fusion/hot-row); CTRL = NEW with only
  `BLADE_CELL_EPILOGUE=0`.
- Builder split: workable; the two B builders overlap in CodeGenBinding, CodeGenCuda, Optimize,
  Lowering and EmitLlvm -- W1-B owns the shared planner types and the gate/decision API;
  B2r before E; B1 before C's flat-producer work; B3 after A's dispatch contract and before
  any D pairkernel work; the emitter-decline fallback must be atomic with the consumer
  handshake (`EmittedCpp` must not suppress a consumer after a decline).

## 8. Disagreements and arbitration

1. **Thread-request policy** (E vs Codex DE objection 11). DECIDED by the orchestrator in
   Codex's favour without arbitration: the repo already separates the thread REQUEST
   (`where omp` / `IsOmpParallel`) from the reorder LICENCE (builtin `+`); a single
   `reduce(a, (+))` opens no team today (dot's plan line), so a join of such folds must not
   either. Auto-threading builtin `+` would be a language-policy change for the user, not this
   round. G7 therefore fires on an explicit request on the join's fold kernel, and
   `onepass.blade` adopts that spelling (bench fairness: the expert carries
   `!$omp parallel do reduction`).
2. D2 speed, D4 attribution, D1/D3b/G5 timing: measurement questions for the runner, not
   arbitration; each has its A/B and DROP rule in sections 2-4.
3. **Unseeded-leg seeding in C** (C brief vs Codex AC objection 14). C proposes seeding an
   unseeded builtin `(+)` leg with -0.0 (exact for all finite/inf/qNaN inputs, n >= 1; strictly
   better than the surface join's +0.0, which flips an all-negative-zero sum). Codex: an
   unseeded reduce COPIES a0 and does n-1 ops; adding `-0.0 + a0` quiets a signalling NaN
   (7ff0000000000001 -> 7ff8000000000001), so it is not an unconditional bit-exact
   replacement; use a peeled first-element start per leg (an emitter change in E's files) or
   restrict the domain. Precedent: `bench/notes/phase2-microkernel-plan.md` section 3 states
   "NaN payloads are outside the contract (x86 propagates the FIRST operand's payload; the
   printers print nan with no payload)". -> FABLE RULING (`round2/fable-arbitration.md`, Q1):
   -0.0 seeding is ADMISSIBLE in v1 with a documented exclusion; the peeled start is NOT
   required. Signalling-NaN state is outside the contract (formalism 2.4 has no payload row;
   every lane prints every NaN as `nan`; the gates compare normalized stdout; the provider
   boundary refuses NaN both ways, BL8015; no bit-cast intrinsic exists, so an sNaN is
   UNREACHABLE by any Blade program; g++ `-fno-signaling-nans` and LLVM already fold
   `x + -0.0` to `x`, so the stricter promise would be unkeepable anyway). Required: a
   formalism 2.4 paragraph, the CLAUDE.md row sentence, the plan-record wording, QUOTED string
   pins (`"-0.0"`, `"-inf"`, `"nan"`) in the corpus. Q2 (G7 policy, item 1 above) CONFIRMED:
   request and licence are separate axes; C must not manufacture a request.
4. **Scatter v1.1** rejected -> dropped; no arbitration needed.
5. (B disagreements: filled after its review.)
- **WAVE-2 A/B MEASURED** (16:22; `w2-ab.md`, 23 sessions, zero failures, no Blade arm slower
  beyond its CI): ordered scatter pairkernel serial repro 37.05 -> 29.49 ms (0.796; expert
  0.929/0.859 -> **1.163/1.079** win), comoment3 14.87 -> 8.93 ms (0.600; 0.770/0.775 ->
  **1.288/1.299** win), knob-off restores w1; mt8 repro unchanged (G35 owns it). G7 join team:
  onepass mt8 0.858 (expert 0.924/0.980 -> 1.135/1.166, the NEW arm), dot 0.867 (-> 1.053/1.127
  live), fusedchain 0.926 (-> 0.997/1.151); knob-off 1.10-1.15x slower -> Codex item 7 does not
  fire. Simplex edge k-loops: 2% serially (w2b/w2 1.021 [1.010, 1.033]). anti_g 1 vs auto: within
  noise serially, auto 0.954 [0.904, 0.996] mt8 (indicative) -- DEFAULT KEPT AT 1, re-measure
  in a quiet session (follow-up). gram control 0.998/0.987. RESULTS 15.3b filled, G44 CLOSED
  (cc07b403). ROUND 2 CLOSED on the integration branch; master fast-forward = the user's call.
