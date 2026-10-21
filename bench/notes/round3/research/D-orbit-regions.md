# D -- the orbit / strict-symmetry whole-region plan: what round 2 built, what to build next

Research brief, round 3, item D. Base: `worktree-bench-fortran` e64fef3b (round-2 tip; line numbers below are
there unless marked "w3" = the wave-3 worktree `magical-cannon-db55bf` b80e29ad, where OrbitFold.fs is unchanged,
EmitLlvm.fs is +12 and Optimize.fs +6 around these hooks). Method: source reading plus 2 `blade plan` and 1
`blade emit` (no C++ compile) with the existing binary (Blade.dll C7B14497..., 2026-10-20 14:32), from the
private cwd `round3/research/D-work/` (outputs `plan01.txt`, `plan02.txt`, `emit02.txt` kept there). No build,
test, run or g++.

**Bottom line.**
- **Built (round 2):** the strict consumer of a STORED symmetric pool -- the ordered orbit scatter, rank 2 and 3,
  both lanes, every chain fed its source sequence (OrbitFold.planStrict, `blade_orbit_scatter.hpp`,
  EmitLlvm.emitOrbitScatter). It WON serial repro pairkernel (0.929/0.859 -> 1.163/1.079) and comoment3
  (0.770/0.775 -> 1.288/1.299). Also built: the licensed orbit-class fold, B3 licensed producer-fused row classes,
  G35 threaded complete rows, the syrk / antisym / simplex tiles. What all of them consume is ONE scalar total
  (a nest of folds ending in a scalar) over ONE compact read with a coordinate-free cell function.
- **Not built:** every other consumer the plan targets -- row actions `u(i) = fold_j w(i,j)*v(j)` (the plan's
  first target), several consumers sharing one pool pass, a contraction stage feeding a packed result
  (the tower), residual-symmetry deduction, waves / any strict producer-fused form, packed-coordinate reverse
  AD, AntisymIdx / rank >= 4 / wreath consumers. Fixture 01 today: three separate serial walks of n^2
  canonicalized reads each (`plan01.txt`).
- **Top piece (P1): a producer-fused ORDERED COMMIT for strict mt8 rank 2** -- the answer to "is a strict tile
  that keeps per-row chain order better than G35?". Yes, but not as waves: the box's measured ~40 us barrier
  (RESULTS 16.1) makes 2T-1 global waves cost more than the whole gap, and any STANDALONE consumer of the
  25 MB pairkernel pool is DRAM-floored (G35 already sits at ~2 reads of it). The form that wins is the serial
  scatter itself, run row-group by row-group inside the producer's team by the thread that just produced the
  group (L1/L2 reads), ordered by a ticket. Target: pairkernel mt8 repro 0.687 / 0.664 -> ~0.95-1.02.
- **Second piece (P2): weighted, multi-consumer orbit ACTIONS** -- the scatter generalized to cell terms that read
  vectors at the chain's own and partner coordinates, to map-shaped (vector / packed-H) outputs, and to K
  channels in one pool pass; rank-2 actions first, then the one-axis rank-3 stage (the tower), plus a 20-line
  Deduce rule so the tower's H gets packed storage without a `comm` declaration. New kernels `pairaction`
  and `momenttower` with expert twins.
- **Small companion (P3): the row-owned PULL scatter for rank 3** (comoment3 mt8 repro 0.925 vs ifx): each
  thread owns whole H rows; 1.68M contiguous reads instead of G35's 5.0M canonicalized; no synchronization.
- Defect seen in passing: fixture 02's C++ emission references undeclared `__v53` / `__v60` (`emit02.txt`:345-348;
  t, h, y never materialized) -- a g++ failure (BL9002) by inspection; likely the RESULTS 15.4 open card
  "unforced map over a compact pool fails BL9002". Not compiled here.

## 1. Hook verification

| Plan claim | Current location | Status | Evidence |
|---|---|---|---|
| Lowering law H ∩ Stab, formalism §§11-12 "at :2163" | `docs/formalism.md:2203` §11.2, §12.4 :2289 | drifted (+40) | unchanged semantics; §8.4 :1908 output deduction, §8.5 :1959 sign-character rule |
| OrbitFold "declines Hermitian and wreath at lines 368-369" | `src/OrbitFold.fs:397-398` (`recogniseWalk`) | drifted (+29, the round-2 walk / licensed / strict split) | `SymHermitian -> decline ...`, `SymWreath -> decline ...` |
| Licensed orbit-class fold | `OrbitFold.fs:448 planLicensed` (vetoes :459-483), `recognise` :529 | holds | builtin `+`, reorder licence, +0 inner seeds, `BLADE_ORBIT_FOLD`, not repro |
| "Ordered strict scatter is the most distinctive new algorithm" (README §"Why...") | `OrbitFold.fs:536-802` (`planStrict` :698, `recogniseStrict` :792); `src/cpp/blade_orbit_scatter.hpp` (`tri` :57, `scatter2` :135, `scatter3` :151); `CodeGenBinding.fs:4120 genOrbitScatterCpp`, dispatch :4224-4246, taken :5051; `EmitLlvm.fs:6581 tryOrbitScatter`, `:6621 emitOrbitScatter` (w3 :6593 / :6633) | BUILT | RESULTS 15.3b; `blade test orbit-scatter` 52 cases x 8 builds |
| Scatter admission = stored pool, coordinate-free f, scalar total | `OrbitFold.fs:241 pureScalarBody` ("reads another array" :263), `strictWalk` :630 (level coordinate refused :638/:644), LLVM `a.Src = APool` :6611-6616, C++ deferred refusal `CodeGenBinding.fs:4237-4239` | holds | the walk must be r nested `IRReduceCompute` levels (`levelOf` :301) |
| planStrict refusals | antisym :715, rank >= 4 :717, mixed licence :721-729, state cap 2^20 :774 | holds | v2 list in gap register G44 |
| Rank 3 "share identical inner chains" | `blade_orbit_scatter.hpp:150-164` (slices + THIRD event + retained H, then `scatter2` over H) | BUILT | Codex W2A audit ruling "Rank 3" |
| S11 producer-orbit epilogue (README "extension of an unlanded sketch") | B3: `Optimize.fs:3042 planCompactRowClasses` (w3 :3135), `CodeGenCuda.fs:2231 buildCompactRowEpilogue`, `CodeGenLoopNest.fs:1868` admission, `EmitLlvm.fs:4116 tryBindCompactRowProducer` | BUILT, licensed only | evidence string "licensed regrouping (taken only where the orbit-class fold is ...)" |
| G35 threaded complete rows | `IRStorage.fs:128 ChunkRowSlots`; choice `CodeGenBinding.fs:4442-4477`; consumer `CodeGenLoopNest.fs:1573-1693`; gate `CodeGenExprSupport.fs:2212 rowSlotsMin` | BUILT (C++) | precedence orbit -> compact rows -> G35 -> scatter -> walk [SUPERSEDED by round 3 W2-0a: where both admit, the default puts the scatter before G35; `BLADE_FOLD_ROW_SLOTS=prefer` gives the order recorded here] |
| BL3999 `reduce(w, (+))` over a deferred compact producer | `TypeCheckInfer.fs:2743` (fused leaf), :3332 (partial axes), :3973 (rank-1 form), :4156 (`prodsum`) | holds | message text identical to `orbit-sketches/validation.md` |
| BL3999 partial compact read `w(i)` | `TypeCheckSupport.fs:3930` | holds | "has no residual class: the subscripts supply 1 of the group's 2 coordinates" |
| Symmetric matvec has no surface | `LinAlgPatterns.fs:1836` ("PACKED-SYMMETRIC (dspmv): the shim entry `blade_symv` EXISTS ... no Blade surface form can currently produce a sym-compact matvec") | holds | P2's rank-2 action IS that surface's strict consumer |
| Fixture 02 H symmetry is "declared, not deduced" | `Deduce.fs:818` pair-parity of `TExprIndex` = `allInv (par arr :: idxs)` | holds (gap) | swapping i, j in `t(i, j, k)` makes index i non-invariant -> PBottom; no compact-group permutation rule |
| Sibling-fold sharing (README priority 3) | `Optimize.fs:3906 shareSiblingFoldsModule` | BUILT, dense axis only | rewrites scalar folds over one dense axis into `<&!>`; no compact read, no row maps |
| Packed-coordinate AD | forward: `GradSweeps.fs:963` C5 symmetric tangent over `range<SymIdx>`; reverse: `GradNormalize.fs:300` refusal ("a SymIdx/AntisymIdx/compound range hands the kernel PREFIX OFFSETS") | partial | `docs/proofs.md:682` BladeJacobian `symmetric_accumulation` (rank 2) |
| Triangular producer threading | `CodeGenLoopNest.fs:700 genNestPragma`: triangular-below -> `schedule(dynamic)` ascending | holds | P1 reuses the ascending row order |
| Shared write analysis | `Optimize.fs:1343 scheduleMayWriteIn`, :1379 `scheduleMayWrite`, :1190 `callbackTable` (w3 +6); totality `src/IRTotality.fs` | holds | B3's five-party matrix: Optimize.fs:3102-3140 |
| Knob registries | `Optimize.fs:123 optimizerGates` (w3 :129, adds `BLADE_REC_STEP_OMP`); `CliSelfTests.fs:93-95 suiteClearedKnobs` (`BLADE_ORBIT_FOLD`, `BLADE_FOLD_ROW_SLOTS`, `BLADE_ORBIT_SCATTER`) | holds | scatter_g / scatter_b `CodeGenExprSupport.fs:2245/2250` |
| Barrier cost on this box | RESULTS 16.1 (w3): "a team entry or a barrier is ~40 us (libgomp, Windows; wait-policy knobs change nothing)" | MEASURED | decides P1's form |
| Machine-checked symmetric facts | `docs/proofs.md:323` BladeLex `enum_offset_respects_lex` (pool order = lex order); :1290 BladeOptimal / :1349 BladeOrbitWork (counts, "Folds are not modelled"); :2627 unproved list | holds | the scatter ORDER lemma is NOT mechanized (round-2 JS checks + raw-bit block only) |

## 2. The plan against the code: built / partial / not

| Plan item | Status | Where / what is missing |
|---|---|---|
| T1 symmetric nonlinear operator, several consumers | **not** | `plan01.txt`: w stored packed (`[symmetric-storage] __v12 applied`), then z, u0, u1 three `serial (repro)` per-row folds, each n^2 canonicalized reads. Scatter needs a fold nest and a coordinate-free f (`OrbitFold.fs:263, :638`). |
| T1b parallel tiles preserving row order | **not** (G35 is the row-owned alternative) | no wave / DAG / commit form anywhere; scatter is serial (`scatterThreadRequests` census :808) |
| T2 contraction tower | **partial** | scatter3's retained H is the tower for a coordinate-free leaf; a weighted leaf `t(i,j,k)*c(k)`, a stored H output, a y stage and H-symmetry deduction are missing (§4) |
| T3 eightfold pair-of-pairs (S2 wr S2) | **not** | OrbitFold declines `SymWreath` (:398); no J/K event maps; no kernel |
| T4 differentiation in packed coordinates | **partial** | forward canonical tangent (C5); reverse over compact ranges refused; packed-parameter Eᵀ not emitted |
| Equality strata / canonical-address elimination | **partial** | orbit fold class nest (`compositions` :170, `repSteps` :183), simplex `tri_edge`, LLVM G34 coord tables, scatter removes addressing for its walks; G35 and every other compact read still use `canon_fold` per logical cell |
| Relational coupled orbit joins | **not** | item B's territory; no double-coset machinery |
| Rank 1 logical compact views + reductions | **not** | BL3999 sites above; `reduce(S, (+), axes = r)` desugars to the walk (materialized only) |
| Rank 2 orbit producer sharing across consumers | **not** | scatter has ONE read and ONE f; sibling sharing is dense-only |
| Rank 3 ordered tile waves | **not** | §3.3 explains why the box wants a commit, not waves |
| Rank 4 residual symmetry + source-identical chains | **partial** | H sharing at rank 3 (CSE of identical leaf chains); §8.5 sign-character rule deduces `S ∘ S`; no compact-read rule |
| Rank 5 stabilizer dispatch | **partial** | see equality strata above |
| Rank 6 packed primal/JVP/VJP | **partial** | see T4 |
| Rank 7 wreath consumers | **not** | see T3 |
| Rank 8 output-owned quotient gathers | **partial** | G35 = output-owned rows over canonicalized reads; a declared `comm(i, j)` H is an output-owned packed gather |
| Rank 9 symmetric support / joins | **not** | item B |
| Rank 10 general equivariant reps | out of scope | `IrrepsIdx` ML track |

## 3. P1 -- the producer-fused ordered commit (strict mt8, rank 2)

### 3.1 Recognized shape
In one let chain (function body, top level, or a binding block -- exactly `planCompactRowClasses`'s three
`planChain` scopes, Optimize.fs:3042ff): a producer `p = IRCompute(IRApplyCombinator pinfo)` whose output is a
non-virtual rank-2 `SymSymmetric` group (`compactRank2`), with an `omp` request covering level 0
(`pinfo.Kernel.IsOmpParallel`, the existing triangular team), followed by a consumer `c = IRReduceCompute(...)`
for which `OrbitFold.recogniseStrict` returns `StrictScatter sp` with `sp.Shape.SourceId = p`, `Rank = 2`, in a
build with `ompThreadEmissionEnabled ()`. pairkernel: `kmat` (comm, `omp(a: 1)`) then `ss` (repro build).

### 3.2 The schedule, and why it is the serial scatter exactly
The serial rank-2 scatter of ROW GROUP g (`tri`'s per-group body, hpp:61-118, rows i0 = gG .. i0+G-1) reads ONLY
pool rows i0..i0+G-1 (cells (i0+a, j >= i0+a)) and updates chains R[i0..n) -- nothing else. So:

```text
prelude (before the team): R[0..n) = s_in; out = s_out; ticket = 0; claim = 0      // hoisted invariant seeds
#pragma omp parallel num_threads(T)
  for (g = claim.fetch_add(1); g < ng; g = claim.fetch_add(1)):        // ascending claims by construction
      produce pool rows gG .. gG+G-1 (the producer's own cell kernel, unchanged)
      wait until ticket.load(acquire) == g                                // pause; yield after N spins
      tri_group<G,B,VADD>(pool, n, R, gG, f, k_in)                       // == serial scatter group g
      for i in group: out = k_out(out, R[i])                              // rows complete in ascending order
      ticket.store(g + 1, release)
```

Every write to R and `out` happens inside a commit; commits run in g order (ticket); each commit runs the
serial scatter's group-g code on the values it would see serially (R as left by commits < g, published by
release/acquire; the group's own pool rows written by the same thread in program order). Hence every row
chain and the outer chain receive EXACTLY the serial scatter's operand sequence, seeds and kernels -- which
round 2 CHECKED equal to the source walk (symbolic JS, 1,248 grouped cases; the raw-bit block; not mechanized,
§7). No chain is
split, no licence consumed; the producer's cells are evaluated concurrently as its own `omp` request already
allows. Deadlock-free: ticket g waits only on groups < g, all already claimed by running threads (explicit
ascending claims, so this does not depend on libgomp's dynamic-dispatch order, which the spec does not fix).

### 3.3 Why this and not waves, a 2-D DAG, or a standalone tile (pairkernel, n = 2503, T = 8)
- **Global waves (plan §"Parallel tiles"):** T blocks give 2T-1 waves and mean parallelism ~(T+1)/4; 8 busy
  threads need T >= ~32 -> 63 barriers x ~40 us = 2.5 ms, against a 3.2 ms gap. Refuted on this box.
- **Standalone consumer of the stored pool (any strict form, tiles included):** the pool is 25.07 MB > the
  16 MB L3 (Ryzen 7 5800HS). Wave 3 measured a 24 MB read at T=8 at 1.5-1.9 ms; G35 reads each off-diagonal
  cell twice (row part streamed, column part line-reused) ~50 MB ~= 3.1-3.8 ms, i.e. it already sits at its
  floor (strict excess 9.354 - 6.157 = ~3.2 ms, cross-session). A one-pass strict tile could at best save
  ~1.5 ms -> ~0.82 / 0.79. Not parity.
- **2-D point-to-point DAG ((I,J) after (I,J-1) and (I-1,J)):** legal, but only needed when the serialized
  consumer work rivals producer time. Here it does not: consumer ~3.13M cells at ~0.25-0.4 ns from L1/L2
  (vector lanes over distinct chains) ~= 0.8-1.2 ms serialized, against ~5.4 ms of producer wall time (G32
  diagnostic) -- a 1-D ticket suffices. Keep the DAG as P2's mt8 v2 for cheap-kernel / many-RHS actions.

### 3.4 Refusals and the write-analysis admission
Predicate `compactRowCommitAdmissible` (new, beside B3's checks at Optimize.fs:3102-3140), all five parties
through the shared `scheduleMayWriteIn programFuncs modul` (W) and `cseFactsOf` (F): producer kernel W+F;
consumer walk `comp` W+F; outer fold kernel W+F; seed W+F; every binding between p and c W+F and
`readsTransitively p` false. Plus: (a) `planStrict` admitted the walk (rank 2, symmetric, total f / kernels /
seeds under `Blade.IRTotality`, every level strict in this build); (b) the producer kernel is TOTAL
(`IRTotality.auditCallable`; matern52's sqrt / exp / divide pass); (c) every `sp.FreeIds` dominates p
(B3's `bodyDominated` against `DominatingIds`); (d) the producer nest is the plain compact flat-row nest
(`CodeGenLoopNest.fs:1868` admission: no hot-row, fold wrapper, MPI slab, share decl, streamed source,
collapse); (e) not a tile-dispatched producer. Declines record `compact-row-commit` with the first failed
condition and fall back to G35, then the serial scatter. Antisym producers: v2 (needs the signed events).

### 3.5 Lanes, knob, plan
- C++ only. LLVM has no threads (G11): it keeps the serial scatter; `CliCommands.llvmLaneDecision`
  (:988-1011) rewrites the commit record exactly as it does the G35 marker (`scatterServesSerialMarker`).
- Interpreter: none (the IR is the spec). Byte identity under REPRO+CONTRACT=off holds by 3.2.
- Knob `BLADE_ORBIT_COMMIT` (default on; `0`/`off` = today's G35 / scatter), a PLANNER gate ->
  `Optimize.optimizerGates` (opt-diff pins REPRO+off, the build where this form fires: off vs on must be
  byte-identical output) and so cleared by `suiteClearedKnobs`; `BLADE_TILE_TUNE commit_g` (default 4,
  speed-only). CLAUDE.md env-table row.
- `blade plan`: `[fold-form v1] ss: applied [ordered orbit scatter, producer-fused commit (rank 2, extent 2503,
  row groups of 4): each group committed by the thread that produced it, in group order; every row chain fed
  its source order; the outer chain serial from its seed]` plus an `omp` record on the producer
  (`omp parallel + ascending group claims + ticket commit`).

### 3.6 Target cell and counts
pairkernel mt8-repro (w2, `w2-ab.md`): Blade-cpp 9.354 ms; gf 6.423, ifx 6.213 -> 0.687 / 0.664 (ind); Blade
mt8-licensed 6.157 ms (B3 already fused). Parity needs <= 6.42 / 6.21 ms.

| | today (G35) | commit |
|---|---|---|
| checksum pool reads | 6,265,009 canonicalized (`canon_fold<2>` + nested row pointers) | 3,133,756 contiguous, from the producing core's L1/L2 (<= 80 KB per G=4 group) |
| checksum DRAM bytes | ~50 MB (two passes of the 25.07 MB pool) | ~0 beyond the producer's own write; R (20 KB) migrates via L3, ~6 MB total |
| f evaluations | 6,265,009 | 3,133,756 |
| chain applications | 6,265,009 + 2,503 | same (every chain keeps its operands) |
| team entries | 2 (producer, G35) | 1 |

Expected (hypothesis): 9.35 -> 6.3-6.8 ms, ratio ~0.94-1.02 / 0.91-0.99.

### 3.7 Implementation
- `Optimize.fs:3042`: factor `planChain`'s candidate walk; a strict arm records `CompactRowPlan` with a new
  `Form = RowClasses | RowCommit of StrictPlan` (`CompactRowTable`, :2990ff, unchanged API).
- `CodeGenCuda.fs:2231`: `buildCompactRowCommit` (prelude: R, out, ticket, claim; body per group).
- `CodeGenLoopNest.fs:1868` + the outer header (:1959): a commit arm beside the Path B `__rlo/__rhi`
  header: `#pragma omp parallel` + the claim loop over row groups (the inner triangular nest unchanged).
- `blade_orbit_scatter.hpp`: factor `tri`'s group body into `tri_group` (no behaviour change for `tri`), add
  `commit_wait` / `commit_release` (acquire/release, `_mm_pause`, yield fallback).
- `CodeGenBinding.fs:4255` (`compactRows`): a commit arm -- the consumer binds the producer's `out`;
  a producer that did not place the form falls through to G35 / scatter (today's order).
- `EmitLlvm.fs:4116 tryBindCompactRowProducer`: ignore `RowCommit` plans.
- Tests: `blade test orbit-scatter` commit leg (generated, REPRO+off, T = 1 / 3 / 8 and OMP_THREAD_LIMIT=2,
  `commit_g` = 1 / 4 / 8, knob off vs on: identical text; observed team asserted); corpus
  `tests/corpus/symmetry/136_orbit_commit_sym2.blade` (p = 13 and 23, `where comm(a, b), omp(a: 1)` producer,
  `radd` `where repro` kernels so the licensed default suite takes the strict path, a 1e16 cancellation row,
  EXPECT pins from the interpreter) and `137_orbit_commit_refusals.blade` (writing producer, integer-`/`
  producer, a binding reading the pool in between, a free scalar bound after the producer); plan needles in
  `tests/OptimizeTests.fs` (`decision_compact_row_commit_applied_repro`, `_declined_*`); OmpTests emission needle
  (ticket present, no `omp simd` / `BLADE_IVDEP` on any chain loop).
- Docs: CLAUDE.md row; `docs/plans/plan-compact-sym-folds.md` section; gap register row (pairkernel mt8 repro).

### 3.8 Effort, risks, drop
Effort M-L (one producer-nest arm, one planner arm, header helpers, tests). Risks: the producer's dynamic
balance with G-row claims (rows shrink; G = 4 keeps 626 claims); spin cost under oversubscription; L2
eviction of the group before its commit when a thread waits long. **Drop experiment (when the box is free):**
copy `blade emit` of pairkernel (REPRO, threads) into a scratch prototype; time (a) today's producer + G35
vs (b) producer + commit, T = 8, 21 interleaved rounds; instrument ticket-wait time. DROP if (b) is not
>= 1.5 ms below (a) or ticket waits exceed 20% of producer wall time.

## 4. P2 -- weighted, multi-consumer orbit actions (plan targets 1 and 2)

### 4.1 Shape
Channels over one STORED `SymIdx<r, n>` pool S (v1; a deferred producer is P1-fusion v2):
- **rank-2 row action** (vector output): `u = method_for(range<I>) <@> lambda(i) -> reduce(method_for(range<I>)
  <@> lambda(j) -> g(S(i, j), V1(i), V1(j), ...), k, s)` -- `IRApplyCombinator` over `IRRange([I])` whose kernel
  body is one `IRReduceCompute` level over `range<I>` (same tag as S). g is a pure total scalar function of
  x = S(i,j) or S(j,i), of rank-1 dense arrays read at the chain's OWN coordinate i or at the PARTNER j, and of
  invariant scalars; no other use of i, j. The existing scalar-total walk is the special case "outer level a
  fold, g coordinate-free".
- **rank-3 one-axis stage** (packed output): `H = method_for(ids, ids) <@> lambda(i, j) where comm(i, j) ->
  reduce(method_for(ids) <@> lambda(k) -> g(T(i, j, k), V(k)), k, s)` over a stored SymIdx<3> T (fixture 02),
  H a named packed SymIdx<2> output; weights only at the omitted coordinate in v1.
- **K channels**: up to 8 consumers of the same S in one let chain (fixture 01: z, u0, u1).

### 4.2 Legality (strict side identical to the scatter)
Rank 2: canonical (a, b), a < b, is read once and gives row chain a the term g(x, own=V(a), partner=V(b)) and
row chain b the term g(x, own=V(b), partner=V(a)); a = b gives one term. Each is bitwise the source's term at
that position (symmetric storage answers (a,b) and (b,a) from one cell; g pure), and the per-chain ORDER is the
rank-2 scatter's (unchanged proof). Rank 3: canonical (a <= b <= c) gives H[a,b] <- g(x, V(c)), H[a,c] <- g(x, V(b)),
H[b,c] <- g(x, V(a)), deduplicated by (retained pair, omitted value) -- in the slice form: row-chain term uses the
slice column, column-chain term the slice row, third-event term the slice coordinate; ties skipped as today
(hpp:72, :111). Order = the monotone-insertion lemma (orbit-wave-check: 2,664 insertion checks, 14,100 seeded
symbolic folds; round 2's slice check 2,304 cases). Every chain keeps its seed, kernel, operand order; a
channel's chains never mix with another's. The term is computed once per (cell, channel, role), stored to a
Z block, then applied -- under `-ffp-contract=fast` that may differ from the walk's per-context fusion (same
status as the scatter; v1.1 contraction invariance stays REJECTED).

### 4.3 Refusals
Two contracted axes in one stage (`T(i,j,k)*v(j)*w(k)`, catalog N11: equal-sum ownership and nested order
both fail -- orbit-wave-check's (0,0,3)/(0,1,2) collision) -> walk, census "two contracted axes: ordered
events need per-destination queues (v2)"; AntisymIdx (N06's signed events, v2); g reading S at a third
coordinate or a second compact array; weights not rank-1 dense over S's tag; per-row seeds (v2); a mixed
licence nest (as planStrict); state > 2^20 per channel; K > 8. Licensed builds: v1 takes the strict form for
row actions too (no licensed competitor but the per-row K-lane walk over canonicalized reads; the serial chain
is an admissible association) -- recorded as such; `BLADE_ORBIT_ACTIONS=strict` restricts it to strict builds
for the A/B (open question 4).

### 4.4 Write-analysis admission
`orbitActionAdmissible`: per channel, `scheduleMayWriteIn` and F false on its `comp`, fold kernel, seed and every
weight array's binding; between the first and last channel: no binding writes S, any weight, any free scalar
(W+F), and no channel's weight or free scalar is DEFINED in between (dominance at the first channel, where the
joint pass is emitted); every channel total (IRTotality), so hoisting a later channel's evaluation above
intervening bindings cannot move an abort; channel outputs stay stored under their names.

### 4.5 Target workloads (new kernels) and counts
| kernel (new) | spec | today (counts) | P2 | expert twin |
|---|---|---|---|---|
| `pairaction` (S2 producer, asymmetric consumers) | pairkernel's points and matern52 w (stored, named), R = 3 lcg RHS; z(i) = Σ_j w, u_r(i) = Σ_j w*v_r(j), y_r = u_r/z, checksum; n = 2503 | 4 x 6,265,009 = 25.06M canonicalized reads (~4 x 50 MB DRAM) | 3,133,756 contiguous reads (25 MB once) feeding 4 channels x 2 roles; mul+add counts unchanged (3 x 6.27M mul, 4 x 6.27M add) | packed-by-columns producer + per-column dspmv-style updates of all channels (gfortran / ifx, serial + mt8) |
| `momenttower` (S3 -> S2 -> vector) | comoment3's T (simplex tile, stored), H = Σ_k T(i,j,k)*c(k) packed (named), y = Σ_j H(i,j)*a(j); p = 171, n = 201 | H: 14,706 x 171 = 2,514,726 canonicalized T reads; y: 29,241 canonicalized H reads | H: 848,046 contiguous T reads, 2,514,726 mul+add (unchanged); y: 14,706 reads | canonical-triple loop with three weighted updates (expert), dense staged (natural) |

Fixture 01 at its own n = 13: 3 x 169 canonicalized reads -> 91. Plan cross-check: the N = 4 order-4 tower's
80+40+16 = 136 updates are ALREADY what Blade does with every stage declared `comm`; P2's gain over current
Blade is reads / addressing (and, with P1-style fusion, the T pool), not update counts.

### 4.6 Residual-symmetry deduction (sub-piece, enables the tower without declarations)
`Deduce.fs:818`: a `TExprIndex(arr, idxs)` whose array type's slot is a `SymSymmetric` group covering positions
p..p+r-1 is PInv under the pair swap if the swap image of that coordinate tuple is a PERMUTATION of it (same
stored cell, bitwise) and every other index and `arr` are PInv; `SymAntisymmetric` gives PNeg for an odd
permutation (and the zero diagonal is the storage law); Hermitian -> PBottom. Also extend `mirrorEq` (:91).
Then fixture 02's H deduces `comm(i, j)` -> packed storage and triangular iteration (no type change for a
declared program). Strict-exact: H(i,j) and H(j,i) are the same chain read for read.

### 4.7 Implementation
- `OrbitFold.fs`: `recogniseAction` beside `recogniseWalk` (:343): channel record {BindingId; Body with
  `readVarId` plus per-weight sentinels `ownVarId r` / `partnerVarId r`; Weights; Fold; Seed; Output =
  Vector | PackedPairs}; `planAction` reusing `strictWalk` / `IRTotality` (:698-778); plan text helpers.
- `Optimize.fs`: `planOrbitActions` + `OrbitActionTable` beside `planCompactRowClasses`; knob
  `BLADE_ORBIT_ACTIONS` in `optimizerGates`.
- C++: `blade_orbit_scatter.hpp` `tri_action<G,B,K,...>` (per channel Zrow / Zcol / Zthird blocks, lanes =
  distinct chains, no pragma on chain loops); `CodeGenBinding.fs` emits the joint pass at the first channel's
  binding (apply-combinator arm ~:1076) and binds later channels by name.
- LLVM: `EmitLlvm.fs tryOrbitAction` beside `tryOrbitScatter` (:6581), same `APool` admission, scalar chains +
  `<4 x double>` row-chain `fadd` without `reassoc`.
- Interpreter: none. Tests: corpus symmetry/138 (fixture 01 shape, p = 13, z / u0 / u1 / normalized outputs,
  seeds ±0 / 0.75), 139 (fixture 02 shape: declared H + y; an undeclared twin pinned through the Deduce rule's
  `[symmetric-storage]` plan needle), 140 refusals (two contracted axes, antisym, weight at a third coordinate,
  weight defined between channels); `blade test orbit-scatter` action leg (generated, 1-3 channels, bodies
  `x`, `x*v(j)`, `x*(v(j) - y(i))`, both lanes, walk-forced twins `w(i,j)*w(j,i)`-style, REPRO+off raw bits);
  Deduce cases in the symmetry block (PInv / PNeg / Hermitian refusal); bench `pairaction`, `momenttower`.

### 4.8 Effort, risks, drop
Effort L (rank-2 actions M; rank-3 stage M; Deduce S; two kernels with experts M). Risks: recognizer breadth
(the walk recognizer was the source of round-2's hardest review findings); K-channel register pressure; the
licensed-build policy. **Drop:** a scratch C++ prototype of fixture 01 at n = 2503 (3 walks vs one 3-channel
pass, contract=off, raw bits equal); drop the multi-channel form if the joint pass is not >= 2x faster than
three single-channel scatters (then keep per-consumer scatters only).

## 5. P3 -- the row-owned pull scatter, rank 3 (comoment3 mt8 repro)

**Shape / legality.** The same admitted rank-3 walk (planStrict), REPRO + threads + the outer fold's (or map's)
`where omp` request (G35's gate). The serial slice form gives leaf chain H[a,b] its c < a terms from earlier
slices' third events (slice c, row a-c: a contiguous run over b) and then c >= a from slice a. So the thread
owning H-row a does: for c < a, the contiguous run (c, a, b >= a) into chains H[a, b] (lanes = distinct chains);
then `tri<G,B,VADD,false>` over slice a with chains H + tri_row(p, a). Every chain lives on one thread and gets
the serial scatter's sequence; writes are disjoint by row; no synchronization. After the team, the middle /
outer folds are the existing serial `scatter2` over H (29,241 + 171 applications). Same admission as G35
(IRTotality whitelist; `scheduleMayWrite` false on walk, kernels, seeds -- callbacks are unreachable through
`strictWalk`'s closed whitelist).

**Cell and counts.** comoment3 mt8-repro: Blade-cpp 4.549 ms (w1 NEW; w2 ratio 1.097 / 0.925, ind), gf 4.914,
ifx 4.320; mt8-licensed Blade ~3.1 ms. Pool 848,046 cells = 6.78 MB (L3-resident). G35 v1: 5,000,211
canonicalized logical reads, 171 outer rows. Pull: 848,046 + 833,340 = 1,681,386 contiguous reads, 2,514,726
leaf applications as vector lanes, 14,706-chain serial tail. Hypothesis 4.55 -> 3.3-3.6 ms (~1.2-1.35 vs ifx).

**Implementation.** `CodeGenBinding.fs` scatter dispatch (:4224-4246, taken :5051): a pull arm ahead of
`ChunkRowSlots` for rank-3 walks with a request (`genOrbitPullCpp`, team with descending-work dynamic
claims); hpp `pull3` driver; knob `BLADE_ORBIT_PULL` (codegen knob -> `suiteClearedKnobs`, like
`BLADE_ORBIT_SCATTER`), size gate `BLADE_TILE_TUNE pull_max` (pool bytes, default 8 MB; rank-2 pools under it
are v1.1); LLVM serial scatter (llvmLaneDecision translation). Tests: orbit-scatter pull leg (T = 1/3/8 raw
bits), corpus symmetry/141, OmpTests needle. **Effort S-M. Drop** if comoment3 mt8-repro does not gain >= 0.6 ms
(median, CI excluding 1) over G35 in the A/B.

## 6. Weighed and not chosen this round

- **Packed-coordinate reverse AD (T4).** Grad is source-to-source before typecheck; the reverse lane refuses
  compact ranges (GradNormalize.fs:300); the Eᵀ rule (orbit sum of logical cotangents) is proved at rank 2
  (BladeJacobian `symmetric_accumulation`) but needs a new reverse rule for `range<SymIdx>` construction loops and
  a cotangent-metric record. No kernel; L; next after P2 (P2's channels are its forward/primal half).
- **Rank-4 partial symmetry / S2 wr S2 (T3).** Needs a pair-of-pairs domain fact, two event maps (J, K) and
  K's non-mirroring rule; OrbitFold refuses wreath; no kernel. The cheap step is planStrict rank 4 via recursive
  slices (G44 v2 list), worth doing only with a rank-4 workload.
- **Logical compact views (rank 1).** A type-level change at five BL3999 sites; P2 gets the same programs
  through the full-coordinate spelling the fixtures already use. Defer to a surface-design round.
- **2-D wave/DAG tiles.** Kept as P2's mt8 v2 (cheap Gaussian, R >= 8, where serialized commit work would rival
  producer time); global-barrier waves are refuted on this box.

## 7. Proof obligations (docs/proofs.md)

Machine-checked today: storage order = lex order (`BladeLex enum_offset_respects_lex`, :323), counts and
optimality of canonical enumeration (BladeOptimal / BladeOrbitWork, "Folds are not modelled"), rank-2 packed
gradient accumulation (BladeJacobian). NOT checked: the scatter's per-chain sequence lemma (finite JS checks +
raw-bit tests only). New obligations:
- P1: none on arithmetic; the commit's linearization is a memory-model argument (ticket release/acquire,
  same-thread production) -- prose plus T-sweep raw-bit tests; record under "What remains unproved".
- P2: `rowgroup_scatter_sequence` (rank 2, any G) and `insert_monotone` (one-axis fan-out at any rank, with the
  tie dedup) in a new `BladeScatter.v` on BladeLex's enumeration; the Deduce rule's soundness
  `compact_read_perm_invariant` in BladeDeduce (SymIdx PInv, AntisymIdx PNeg with the zero diagonal) and its
  place in BladeDeduceExact's unsigned fragment.
- P3: none beyond the P2 lemma (a partition of the same chains; disjoint row ownership).

## 8. File overlap with the other round-3 items

| file / area | A (census, remaining losses) | B (relational regions) | C (N01-N10) |
|---|---|---|---|
| `CodeGenLoopNest.fs` producer nest header / Path B (:1560-1693, :1868, :1959) | A's mt8 repro cells (pairkernel, comoment3) are P1 / P3's cells -- one owner | -- | N01 / N04 schedules may add nest arms |
| `CodeGenBinding.fs genReduceComputeBindingCore` dispatch (:4200-4271, :4442-4477, :5000-5051) | G48 (row-view row slots) edits `rowSlots` | joins / aggregates fold dispatch | N10 grouped folds |
| `Optimize.fs` planners (`planCompactRowClasses` :3042, gates :123, `scheduleMayWriteIn` :1343) | -- | membership hoisting / join planners and their admission predicates | any new planner + gate |
| `OrbitFold.fs`, `blade_orbit_scatter.hpp`, `EmitLlvm.fs` :6430-6900 | -- | coupled orbit joins (if B touches compact sources) | N06 (antisym pair action) = P2's signed v2; N11 = P2's refused two-axis shape |
| `Deduce.fs` :818 | -- | -- | -- |
| `bench/kernels/*` new kernels, harness `kernel.json`, run.py | -- | -- | N-kernels share the harness wiring; pairaction / momenttower vs N06 / N11 naming |
| `CliSelfTests.fs` dispatch + `suiteClearedKnobs` :93-95, CLAUDE.md env table, gap register | all | all | all |

## 9. Open questions for Codex

1. P1 deadlock / progress: explicit ascending claims + ticket spin inside a libgomp team -- safe under
   OMP_THREAD_LIMIT, OMP_DYNAMIC, nested parallelism, SMT oversubscription, and a producer panic (a thread
   in `blade_rt::panic` while others spin on its ticket)? Is producer TOTALITY required (its panic order is
   already racy under its own team), or only conservative?
2. P1 request policy: the producer's `omp(a: 1)` threads production; the consumer fold's `where omp` is NOT
   spent (commits are serialized). Is "consumer committed inside the producer's team" a transfer under the
   G7 explicit-request policy, and what must the census say?
3. P1 visibility: the commit reads pool rows written by the same thread and R / out published by the previous
   ticket holder -- any C++ memory-model hole (relaxed claim counter, non-atomic R)?
4. P2 in licensed builds: take the strict action form (serial chain per row) or keep the per-row K-lane walk?
   Changing default-build bits is allowed; is it wanted?
5. P2 own/partner weights: is g(x, V(own), V(partner)) bitwise the source term at both mirror positions when g
   is a callee with `where repro`, or under the fast-contraction default (the Z-buffer split)?
6. P3: the outer fold's request threads LEAF chains (H rows) -- evaluation freedom of pure total
   sub-computations, or a licence move across levels?
7. Deduce rule: any interaction with the witness sampler (`Deduce.fs:1031 canonicalCell`), the BL4013 sign law,
   or a SymIdx over a COMPOUND base (§8.4: the permutation acts on whole tuples, not per coordinate)?
8. Fixture 02's undeclared `__v53` / `__v60` (emit02.txt): the RESULTS 15.4 BL9002 card or a new
   tuple-of-computes emission defect?
9. The DRAM-floor argument (25 MB pool > 16 MB L3; 24 MB read at T=8 = 1.5-1.9 ms from wave 3): does it
   really cap any standalone strict consumer at ~0.82, i.e. is fusion the only route to pairkernel mt8 repro
   parity?
