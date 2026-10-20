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
  `worktree-bench-fortran` as a1e93bb7 / a322ccd0.

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
