# Round 2 item B -- store/cell epilogues (research brief)

Scope: fuse scalar consumers into their producers' output stores while keeping every named
store. There are four sub-items: B1 rank-1 producer, B2 stencil/recur cell epilogue, B3 licensed
compact-producer epilogue, and B4 maskagg compaction. Evidence: master 4136f0b5,
`bin\Release\net10.0\Blade.exe`, and `blade emit` / `blade plan` on the five bench kernels. Each
kernel was emitted licensed and with `BLADE_FP_REPRO=1`, mt8 (default) and serial
(`BLADE_OMP_THREADS=1`), in both the C++ lane and `BLADE_LLVM=1`. The emitted files are kept in
`scratchpad/round2/B/`. No timings were run. Every cost below is a byte/op count or latency
arithmetic, not a speed forecast.

**Headline finding (changes the ranking the plan assumed).** The plan framed B2 as "stop
re-reading a hot row". In the LICENSED default that re-read is small. It is a K-lane `sq` over a
row already in L1/L2: about 1.6% of stencil serial, about 0.2% per thread under mt8, and about
3.8% of recur serial.

The large, strictly legal opportunity is in REPRO. Today every row checksum chain and every
global checksum chain runs AFTER its producer. It runs as a latency-bound serial chain (3-4
cycles per link) while the core's execution ports sit idle. Interleaving the same chain into the
producer in small blocks lets out-of-order execution overlap it with the producer's independent
work. The measured gap between repro and licensed matches the chain arithmetic:

- recur: 13.83 - 9.16 = 4.67 ms, against 6.02 M links x 3-4 cycles (4.2-5.6 ms).
- stencil: 14.28 - 10.71 = 3.57 ms, against 3.0 M links (2.1-2.8 ms).

The interleaved form keeps the same chain order, the same seed and the same operations, so it
needs no licence. This brief calls it **chain interleave**.

## 1. Hook verification

| Plan claim | Current location | Status | Evidence |
|---|---|---|---|
| `planHotRowFolds` ~:1547, "requires rank >= 2" | `src/Optimize.fs:1547`; rank gate `producerShape` :1560, `at.IndexTypes.Length >= 2` at :1564 | holds | Read. It also admits plain/SymNone producers only (:1565), so compact producers are excluded too. |
| The hot-row plan walks top-level code | `planChain` :1681-1683 walks function bodies and `modul.Bindings` only | changed (gap) | A let chain INSIDE a top-level binding value is never walked. Example: pairkernel's `let (checksum, ..) = { let kmat ..; let ss .. }`. |
| `fuseRowFoldsModule` ~:2004 | `src/Optimize.fs:2004` (gate `BLADE_FUSION`, rule `row-fold-fusion`) | holds | Recur uses THIS pass (an IR rewrite), not hot-row. Its plan prints `[row-fold-fusion v1] kernel: applied` and has no `[hot-row-fold]` line. |
| `fuseSliceWritesModule` :1731 | `src/Optimize.fs:1731` | holds | Recur plan prints `[slice-write-fusion v1] kernel: applied`. |
| `optimizerGates` | `src/Optimize.fs:77` (6 gates); `CliSelfTests.suiteClearedKnobs` :83-95 appends them | holds | A new gate is cleared by the suite automatically. |
| Hot-row planner call site | `src/Lowering.fs:2911` (after CSE and pool reuse); table reset at :2715 | holds | Read. |
| `genLoopNestStreamed` hot-row ~:1738 | Function at `src/CodeGenLoopNest.fs:1463`; admission :1738-1760; row-fold placement and loop close :2149-2180 | holds | The admission guard requires `Bindings >= 2` and `compactFlat.IsNone` (:1752). |
| Compact producer nest ~:1491 | Inside `genLoopNestStreamed` (:1463). The compact pool-write plan is `compactFlatWritePlan` (:1084), used at :1730. | changed | Pairkernel emits `__opool___v33 + __ooff___v33_0` row pointers under `BLADE_OMP_PARALLEL_FOR_DYNAMIC`. |
| RowEpilogue builder | `src/CodeGenCuda.fs:2006 buildRowEpilogue`, attached at :3549-3553; type at `src/IRStorage.fs:123` | holds | Read. |
| Hot-row consumer and producer handshakes | `CodeGenBinding.fs:113` (consumer); :853 and `CodeGen.fs:346` (producer) | holds | Uses the `EmittedCpp` ref on the plan. |
| `EmitLlvm` ~:3993/4003 | `src/EmitLlvm.fs:3926-3944` (bindNamed arms), `tryBindHotRowProducer` :4004, `emitHotRowConsumer` :4047 | moved ~70 lines | The LLVM lane declines non-dense producers (:4039). |
| Rank-1 flat producer (saxpy) | `CodeGenLoopNest.fs:2371 tryGenFlatElementwiseNest`, called from `CodeGenCuda.fs:3584` | holds | Emits `// flat elementwise: 2000003 cells` with `BLADE_OMP_PARALLEL_FOR_SIMD`. |
| Saxpy Path B dot-class consumer | `CodeGenBinding.fs:4007-4075` (laneLines in `genReduceComputeBindingCore`, :3712) | holds | Emits `blade_fold::sq<double, 8>(__rsrc0 + __rlo, ..)` per chunk. |
| K-lane tree and chunk partition | `src/cpp/blade_fold_lanes.hpp`: skeleton `detail::fold` :210, `sq` :279, `chunk_lo` :102 | holds | Contract: chain c owns the elements at indices c mod 32; products are rounded (asm barrier). |
| `OrbitFold` (`BLADE_ORBIT_FOLD`) | `src/OrbitFold.fs` (`recognise` :314, `OrbitPlan` :111). C++ dispatch at `CodeGenBinding.fs:3740-3765`, `genOrbitFoldCpp` :3562. LLVM `tryOrbitFold` :5990, `emitOrbitFold` :6006. | holds | The orbit fold is decided at EMISSION time, not in Optimize.fs. |
| Pairkernel mt8 orbit pass is threaded (G32) | Emitted as a second `BLADE_OMP_PARALLEL_NUM_THREADS(__oT)` region: boustrophedon row deal, per-thread `__oa0/__oa1`, combined in thread order | holds | `pairkernel_lic_mt.cpp:300-349`. |
| `index_types.h` ~:354 | `src/cpp/index_types.h:354 struct compound_index_t<1>` | holds | The constructor runs a count pass and a branchless fill. |
| `CodeGenExprSupport.fs` ~:596 | `genCompoundIndexFromMask` rank-1 arm :675-684; plan record :701 | moved | Read. |
| `CodeGenExpr.fs` ~:1997 | `materializeMaskForm` :1996 | holds | This is the mask pass. |
| `EmitLlvm.emitCompoundInit` ~:6866 | `src/EmitLlvm.fs:6870` | holds | |
| Fresh/escape facts ~:3694, ~:4002 | `CodeGenLoopNest.fs:3696 computeFreshReturnFacts`, :4004 `computeScopeEscapes` | holds | Needed only for B4d. |
| Compound temporary for `y * y` | `CodeGenCuda.fs:3650-3675` (the compound output shares `y.idx`) | new hook | Maskagg emits `Compound<double,1> __v40 = { new double[y.idx->cardinality ..] }`. |
| Fold over a compound is refused in the deferred path | `CodeGenExprSupport.laneableFoldLevel` (:2160ff, `IRCompoundMask -> false`); comment at `CodeGenBinding.fs:3770` | new hook | This refusal is why `y*y` is materialized. |

## 2. The transformations, precisely

All four sub-items keep every named store. B1, B2 (licensed arm) and B4c share one runtime
primitive, P1. B1, B2 and B4c also share the repro form, P2.

**P1 -- streaming K-lane state** (`blade_fold_lanes.hpp`). It adds three pieces:

- `lane_state<T,K>`: K NAMED vector accumulators, never an indexed array.
- `feed_sq/feed_sum(state, p, nblocks)`: consumes whole blocks of B = K*W elements.
- `finish_sq(state, tail, rem)`: does the vector remainder, the ascending lane combine and the
  scalar tail.

The invariant is raw-bit testable against `model::`. For n = nb*B + rem,
`finish(feed(s, a, nb), a + nb*B, rem) == sq<T,K>(a, n)` bit for bit, under every contraction
mode, because the products keep `BLADE_FOLD_ROUND`.

The producer writes a block of `epi_blk` cells (a multiple of 32; default 256 = 2 KB) to the named
output, then feeds those cells from L1. This is the "final-store epilogue at block granularity"
of review constraint 1:

- No accumulator is live inside the producer's arithmetic.
- If the producer needs all 16 ymm registers, the state spills once per 256 cells. That is 16
  memory ops per 256 cells, against the 64 reloads from DRAM/L2 that it replaces.

**P2 -- chain interleave** (repro and other unlicensed folds). The producer loop is strip-mined
into mini-blocks of `epi_blk_r` cells (default 16). Each step does two things:

1. `BLADE_OMP_SIMD` produces and stores the mini-block.
2. The SAME scalar chain advances over those cells in ascending order. The statement is
   `acc += c * c`, the same statement today's prodsum/fold already spells. It reads the
   just-stored values (store-forwarded or from L1).

The loads are off the chain's critical path. So out-of-order execution runs the next mini-blocks'
producer work under the chain's latency. Evidence that this machine overlaps chains with
independent work: `src/microkernels/README.md` correction 15. There, the un-jammed baseline beats
its own 3.0-cycle chain through OoO overlap, and the recovery is capped by the instruction window.
That cap is why the mini-blocks must be SMALL, not row-sized.

### B1 rank-1 producer epilogue (saxpy)

**Recognized shape.** The producer is `let P = method_for(zip(A1..Ak)) <@> k |> compute` on the
flat elementwise path: dense, rank 1, scalar cells, literal extent. The consumer, later in the same
let chain, is `reduce(method_for(P) <@> g, F, seed)` or `reduce(P, F)`, where:

- g is a one-parameter scalar kernel of the cell;
- F is a two-parameter kernel;
- g and F are pure, capture-free and repeatable;
- nothing between producer and consumer reads P, assigns, or touches g's captures. This is exactly
  the between-check of `planHotRowFolds` (:1610-1622).

**Forms.** The form is decided per emission mode. Optimize plans independently of the mode, as
hot-row does.

- **Serial licensed** (the fold is dot-class per `dotClassFactors` and `foldReorderLicensed`): P1
  runs over the whole sweep. The consumer computes `acc = F(seed, partial)` exactly as today.
  **Bits are identical to today:** the result is `sq<8>(y2, n)` by the P1 invariant.
- **mt8 licensed**, when the producer is licensed (`where omp(u: 1)` covers the flat sweep) AND
  the fold is licensed (`where omp` on F). The producer becomes the Path B region:
  - Thread t owns `[chunk_lo(n,t,T), chunk_lo(n,t+1,T))`. It produces and stores its chunk, runs
    P1 over it into `__rpart[t]`, and sets `__rhad[t]`.
  - After the region, the partials combine in THREAD ORDER through F's wrapper: seed first, empty
    chunks skipped. This is the existing Path B combine, unchanged.
  - **Bits are identical to today's mt8:** same partition function, same per-chunk tree, same
    combine. A fixed team gives fixed bits, so the S11 review's mandatory job-order rule is
    satisfied by construction.
- **Serial repro:** P2 runs over the sweep. It is still one global chain, in the same order.
- **Refused** (each gets a declined record):
  - mt8 repro: one global ordered chain sits behind a threaded producer, so fusing would
    serialize the producer.
  - Producer licensed but fold not: same reason.
  - Fold licensed but producer serial: the fold would lose its team. The map licence is never
    transferred to the fold.
  - g reads P beyond its own cell.
  - A non-literal extent, a Reynolds body, or a cuda/mpi strategy.
  - A non-real element type, for P1 only (P2 still applies).
  - g may abort (kept simple in v1).

### B2 cell epilogue for stencil and recur

Today both kernels re-read the completed row in a separate loop (quoted in section 3).

- **Stencil** does it inside the hot-row epilogue: `blade_fold::sq` when licensed, an IIFE chain
  under repro.
- **Recur** does it after the slice-write-fused cell loop, in row-fold-fusion's `foldUpdate`
  (:2054-2083).

There are two arms:

- **B2r (repro, P2).** Each row's chain is fed per mini-block inside the cell loop. The row value
  comes from the same chain, and the outer combine is unchanged (the hot-row row-sum array, or the
  row-fold accumulator). It is strict: each row chain keeps seed +0 (prodsum's
  `double __ps = 0`) and ascending order, and the outer chain is unchanged. For stencil it also
  applies under mt8 repro: each thread interleaves its own rows inside the producer team.
- **B2l (licensed, P1).** An optional arm only (`BLADE_CELL_EPILOGUE=lanes`). It removes an L1/L2
  row reload worth at most 2-4%. Recur's RK4 cell body keeps about 10 vector temporaries live, so
  the state WILL spill (once per production block). The arm exists to measure, not to ship by
  default.

**Row kernels recognized in v1:** `prodsum(row, row)` and plain row sums with a +0 seed (the C++
IIFE and `blade_fold::sq|sum` shapes). An unseeded `reduce(row, op)` (seed = cell 0, plus the
empty-row abort) is left for v2.

**Refused:**

- The row kernel captures or reads the producer beyond its own row (already refused upstream).
- The cell loop is threaded. That is item E's future omp slot on the inductive arm; the row chain
  must then run after the threaded row.
- A `while`-guarded recursion (row-fold-fusion already declines it).
- A row kernel that is not a dot-class fold of the row's own cells.

**Contraction across the former store boundary.**

- **Licensed (P1):** products are rounded by the asm barrier and read from the stored value, so no
  FMA can absorb a producer op. Bits are unchanged in the licensed default.
- **Repro (P2):** the chain statement is the same text as today. But GCC's contraction under
  `-ffp-contract=fast` is context-dependent, so `BLADE_FP_REPRO=1` with default contraction MAY
  change bits relative to OLD. That build is not a bitwise contract, and elementwise fusion
  already accepts the same effect.
- **Repro with contraction off:** under `BLADE_FP_REPRO=1 BLADE_FP_CONTRACT=off` the operation
  sequence is identical. So `test interp`, `opt-diff` and `diff-oracle` see byte-identical output.

### B3 licensed compact-producer checksum epilogue (pairkernel, S11 rank-2 subset)

**Recognized shape.** A compact rank-2 producer (`SymIdx<2,n>` inclusive or `AntisymIdx<2,n>`
strict), emitted by the generic nest with `compactFlat` row pointers. It is followed by a logical
walk that `OrbitFold.recognise` accepts: licensed builtin `(+)` at both levels, inner seeds +0,
and a pure f of one read.

**Transformation: a COMPACT hot row.** Right after packed row i (the cells (i, i..n-1)) is
stored, per-row class partials go into rank-1 arrays:

- `d[i] = f(K(i,i))`: the weight-1 class.
- `o[i] = sum_{j>i} f(K(i,j))`: the weight-2 class. It uses the licensed K-lane / simd form over
  the hot packed row (at most 20 KB, in L1).
- Strict groups need `o[i]` only, plus `o-[i] = sum f(-x)` when the kernel's parity is unknown
  (per `OrbitPlan.AccCount`).

The consumer computes `S1 = sum_i d[i]` and `S2 = sum_i o[i]` in ascending order (the licensed
form is allowed). It then applies the plan's existing `Total` combine, which is addition-only
(`rep(S2, 2)`, `OuterSeed`). That combine code is the same code `genOrbitFoldCpp` /
`emitOrbitFold` use today.

**Legality: licensed only.** This is a regrouping of the logical multiset, exactly as the orbit
fold already is. The OrbitFold.fs header proves it: every leaf set is disjoint, and the whole is
one binary tree over the multiset. Under `BLADE_FP_REPRO=1` the epilogue declines. Item A's
ordered scatter owns the strict consumer, and producer completion order is not a proof of
sequence.

**Determinism is stronger than today.** The partials are PER ROW. Whichever dynamic-schedule
thread produced a row writes its partials, and the rows are combined serially in row order. So
the checksum's bits are independent of team size and schedule. Today's orbit pass depends on T
through the boustrophedon deal. This meets and exceeds the S11 review's "one state per job,
combined in job order".

**Refused:**

- Repro.
- f may abort (that would reorder an abort against later producer rows).
- Anything between producer and fold reads the producer or assigns (the hot-row between-check).
- Rank >= 3 or tile producers. Gram, antisym and comoment3 go through
  `dsyrk_tri`/`dantisym_tri`/`dsym3_tri`. Their last-K-block tile epilogue is the
  register-pressure-sensitive half of S11; it is out of scope and lives in D's files.
- The producer is emitted by any path other than the plain compact nest (dispatched, collapse
  pragma, MPI slab).

### B4 maskagg compaction + fold fusion

Today's chain has six passes: mask, count, branchless position fill, gather, the `y*y` pool, and
the fold. The C++ and LLVM lanes are structurally identical (quoted in section 3). Sub-items:

- **B4a -- no `y*y` pool.** Admit a rank-1, trailing-free Compound operand to the deferred
  elementwise fold path. Its `.data` is a contiguous rank-1 buffer of `cardinality` cells, so
  `reduce(y * y, (+))` can fold `y.data` directly. Licensed, that is the K-lane form. Under repro,
  it is the unseeded serial chain starting from `y[0]^2`, with an empty array still raising BL8003
  at the same span.
- **B4b -- one compaction pass.** The mask pass also counts m, which removes the count pass. The
  position fill and the value gather merge into one branchless pass:
  `v = x[i]; y[q] = v; pos[q] = i; q += keep[i]`, with a slack cell in both buffers.
- **B4c -- fold epilogue in that pass.** After each block of x, feed the newly COMPLETED y cells
  (those with index `f < q`):
  - licensed: P1 over whole 32-blocks; the tree equals `sq<8>(y, m)`;
  - repro: the ordered chain, receiving exactly the kept terms in kept order. The unseeded start is
    peeled, and `m == 0` raises BL8003 after the pass, as today.
- **B4d -- elide positions when no consumer needs them.** This requires no keyed read, no
  `range<CompoundIdx<keep>>`, and no escape of `y` or its idx; printing reads `y.data` only. The
  `compound_index_t<1>` is then built with its cardinality and no position table. This needs a
  census of consumers; `computeScopeEscapes` (:4004) supplies the escape half. When in doubt,
  build the positions: B4b still saves the separate passes.

**Constraints kept:**

- Compaction stays stable, ascending and serial (no parallel prefix).
- `y` stays stored, with exactly m cells.
- `keep` is STILL STORED in v1, because it is a user-named binding (see the protocol question at
  the end).

**Refused:**

- The predicate is not a pure, total scalar function of `x(i)`.
- The mask's source is not the compound's source.
- Rank >= 2, or trailing dims.
- The mask is consumed elsewhere between mask and compound. (Folding B4b's count into the mask pass
  is still fine in that case.)

## 3. Target cells and the work that disappears

Ratios are from RESULTS 14.8 (expert/Blade; gf / ifx). Incrementals are total minus the
input-only twin, from the round-3 bundles.

**B1 saxpy** (n = 2,000,003; y2 = 16.0 MB). Today's serial licensed C++ (`blade emit`):
```cpp
for (size_t __fk = 0; __fk < __fp_cells; __fk++)          // BLADE_OMP_SIMD
    __fp___v29[__fk] = ((1.5 * __fp_x[__fk]) + __fp_y[__fk]);
...
__v36 = __wrap_46___v36(__v36, blade_fold::sq<double, 8>(__rsrc0, __rhi));   // 16 MB reload
```
Other modes:

- **mt8:** after the producer's own `BLADE_OMP_PARALLEL_FOR_SIMD` team, a second team
  `BLADE_OMP_PARALLEL_NUM_THREADS(__rT)` uses a different partition (`chunk_lo`) and runs
  `sq<double,8>(__rsrc0 + __rlo, ..)`.
- **Repro (serial and mt8):** after the producer runs
  `for (__i0 ..) { double __v29____i0 = __v29[__i0]; __v36 = __wrap_46___v36(__v36, (__v29____i0 * __v29____i0)); }`.
- **LLVM:** `loop.cond6` stores y2, then `loop.cond26` reloads it with `fadd reassoc nsz contract`
  (licensed) or plain `fadd contract` (repro).

| Cell | Ratio | Incremental: Blade vs experts | What disappears |
|---|---|---|---|
| serial licensed | 1.244 * / 0.903 * | llvm 4.89 ms vs 3.50 / 3.63 | The 16 MB DRAM/L3 reload (2 M loads) and the second loop. |
| mt8 licensed | 0.821 * / 0.858 * | cpp 4.79 vs 3.19 / 3.31 | The 16 MB reload and one team fork/join. The `new/delete` of the partials stays. |
| serial repro | 1.082 * / 0.686 * | llvm 5.93 vs 4.18 / 2.14 | The 2.0 M-link chain (1.4-1.9 ms of latency) moves under the producer's 48-64 MB stream; the 16 MB reload goes. |
| mt8 repro | 0.765 * / 0.748 * | -- | Nothing (declined); this is a control cell. |

Note that the mt8 incrementals are close to serial for BOTH Blade and the experts: first-touch
of the fresh 16 MB y2 dominates. So the mt8 gain is bounded by the reload's share. ifx's remaining
edge in repro is G36 (input checksums fused into the kernel pass), which is item C.

**B2 stencil** (1499 x 2001 interior; lap = 24.0 MB). Today, mt8 licensed:
```cpp
BLADE_OMP_PARALLEL_FOR
for (size_t __i0 = 0; __i0 < 1499; __i0++) {
    ... for (size_t __i1 = 0; __i1 < 2001; __i1++) __orow___v39[__i1] = <5-point>;
    { Array<double, 1> __v39__hr__i0 = { __orow___v39, __v39.extents + 1 };
      __hr___v48[__i0] = blade_fold::sq<double, 8>(__v39__hr__i0.data, __v39__hr__i0.extents[0]); } }
```
Under repro the same slot is `[&]() { double __ps = 0; for (..) { __ps += r[__pt] * r[__pt]; } return __ps; }()`.
That is a 2001-link serial chain per row, run after the row. In LLVM, `loop.cond52` produces the
cells, then `loop.cond117` runs the row chain, with a row-sum pool of 1499.

| Cell | Ratio | Mechanism |
|---|---|---|
| serial repro | 1.224 * / 1.001 * | 3.0 M chain links (2.1-2.8 ms of latency) overlap with a memory-bound producer (u: 24 MB read; lap: 24 MB write). The measured repro-licensed gap is 3.57 ms. |
| mt8 repro | 0.815 * / 0.891 * | The same mechanism per thread: about 0.35 ms of chain per thread. Incremental is 4.39 vs 3.45 / 4.21 ms. |
| mt8 licensed | 0.885 * / 0.926 * | **NOT addressed by B.** See below. |

Why mt8 licensed stencil is out of reach for an epilogue:

- The licensed reload is a 16 KB L1/L2 row: about 500 cycles per row, about 0.02 ms per thread.
- The experts' stencil uses the SAME produce-then-reread-row schedule (`expert.f90`).
- Yet Blade's incremental is 4.92 ms against the experts' 3.86 / 3.98 ms. That 1 ms is elsewhere.
  Candidates: the second team for the 1499-element outer combine, first-touch of lap, and the
  nested-pointer `u[..][..]` reads.
- This cell needs attribution, not an epilogue.

**B2 recur** (301 x 20011; trajectory 48.2 MB). Today, serial repro C++:
```cpp
for (int64_t __k369 = 0L; __k369 < 20011L; __k369++) { ... __v363[__k366][__k369] = __v610; }
Array<double, 1> __v612 = { __v363.data[__k366], __v363.extents + 1 };
double __v613 = [&]() { double __ps = 0; for (..) { __ps += __v612[__pt] * __v612[__pt]; } return __ps; }();
__v611 = (__v611 + __v613);
```
Licensed, the row fold is `__v613 = blade_fold::sq<double, 8>(__v612.data, ..)`. That reads a
160 KB row from L2: about 5000 cycles per row, about 0.35 ms in total. This is the B2l ceiling.

| Cell | Ratio | Mechanism |
|---|---|---|
| serial repro | 0.985 * / 1.017 * | 6.02 M chain links (4.2-5.6 ms) overlap with the RK4 producer (compute plus a 48 MB store stream). The measured repro-licensed gap is 4.67 ms. |
| serial licensed | 1.114 * / 1.125 * | B2l only: at most 0.35 ms of row reload. This is an arm, not the default. |
| mt8 | no Blade arm (G5, item E) | -- |

**B3 pairkernel** (n = 2503; packed K = 3,133,756 cells = 25.07 MB). Today, mt8 licensed: the
producer region is `BLADE_OMP_PARALLEL_FOR_DYNAMIC` over rows. Then a SECOND region,
`BLADE_OMP_PARALLEL_NUM_THREADS(__oT)`, re-reads the whole pool:
```cpp
for (size_t __ob = 0; __ob * __ont < 2503; __ob++) { const size_t __oc1 = ..boustrophedon..;
    { const double __ox = __opool[__oo++]; __oa0 = __oa0 + ((__ox * __ox)); }
    BLADE_OMP_SIMD_REDUCTION(+:__oa1) for (__ok ..) { const double __ox = __orow[__ok]; __oa1 = __oa1 + ((__ox * __ox)); } }
```
| Cell | Ratio | What disappears |
|---|---|---|
| mt8 licensed | 0.800 * / 0.798 * | The 25 MB pool re-read from DRAM (the pool is larger than the 16 MB L3), plus one team region with its boustrophedon walk and per-row `canon_count`. It is replaced by an L1 re-read of each just-produced row (at most 20 KB) and a serial combine of 2 x 2503 values. |
| serial licensed | 1.122 / 1.047 | The same 25 MB pass, run serially. It is the largest single non-producer pass in the kernel. |
| repro cells | 0.903 / 0.840; mt8 0.370 / 0.359 | None (declined; item A). |

The expert uses exactly this schedule (produce the row, sum the hot row, `reduction(+:cs)`). So B3
closes a structural difference. G32's remaining "team reuse" direction belongs to D.

**B4 maskagg** (n = 1,000,003; m ~ 500 K). The emitted C++ is below. LLVM is the same pass for
pass: `loop.cond17` mask, `loop.cond33` count, `loop.cond51` fill, `loop.cond67` gather,
`loop.cond81` the `y*y` pool, `loop.cond106` fold, and four `blade_alloc_cells` calls.
```cpp
for (__mi ..) keep[__mi] = pred(x[__mi]);                                 // R 8 MB, W 1 MB
new compound_index_t<1>("y_idx", grid, pool);   // count R 1 MB; fill R 1 MB, W pos 4 MB
for (y_q ..) y_compact[y_q] = y_densepool[y_idx->rank_to_tuple[y_q][0]];  // R 4+8, W 4
Compound<double,1> __v40 = { new double[y.idx->cardinality * ..] .. };
for (__i0 ..) __v40.data[__i0] = (y____i0 * y____i0);                     // R 4, W 4 (y*y pool EXISTS)
double checksum = <K-lanes over __v40>;                                   // R 4
```
| Cell | Ratio | Incremental | Bytes today -> after |
|---|---|---|---|
| serial licensed | 0.871 * / 0.628 * | llvm 3.04 ms vs 1.16 / 1.19 | Today: 43 MB of logical traffic, 13 MB of fresh allocations, 6 passes. After B4a+b+c+d: 22 MB, 5 MB, 2 passes (26 MB and 9 MB if positions are still needed). |
| serial repro | 0.839 * / 0.676 * | llvm 3.76 vs 1.47 / 1.57 | The same, plus the m-link chain interleaved with the fill. |

## 4. Implementation design

**Plan layer (`src/Optimize.fs`).**

- **`HotRowPlan` (:1504):** add `Form: RowFold | ChunkFold | CompactClassRows of OrbitPlan` and
  `CellChain: CellTerm option` (Square | Plain of the cell, used by P2/P1). `HotRowTable` (:1530)
  keeps its shape.
- **`planHotRowFolds` (:1547)**, four additions:
  1. A rank-1 sibling in `producerShape`: a dense rank-1 flat producer becomes `ChunkFold`. Do NOT
     weaken the rank-2 guard; this is a separate arm.
  2. A compact rank-2 arm: a SymIdx/AntisymIdx rank-2 producer, plus `OrbitFold.recognise` on the
     consumer, becomes `CompactClassRows`. Call `recognise` here with an IR-level resolver; it
     already takes `resolve`/`resolveCl` functions.
  3. Walk let chains nested inside top-level binding values (`chain b.Value`), beside :1683.
  4. Record `CellChain` when the row kernel is a +0-seeded dot-class fold of the row's cells.
- **`fuseRowFoldsModule` (:2004):** when it applies and the row kernel is `prodsum(row,row)`,
  record a `CellFoldTable` entry with:
  - the cell loop's var id (the slice-write-fused IRForRange in the step body);
  - the row-store buffer and row;
  - the fold-let id `pId`;
  - the CellTerm.

  The IR itself is unchanged, so the interpreter keeps the spec.
- **New `planCompactionFusion`** (beside planHotRowFolds; called from `Lowering.fs:2911`): finds
  the mask -> compound -> fold/extents chain and records a `CompactionTable` entry with the mask id,
  the compound id, the consumer reduce id(s), the extents use, and `positionsNeeded`.
- **Gate `BLADE_CELL_EPILOGUE`**, appended to `optimizerGates` (:77). It is then cleared by the
  suite automatically and compared by `opt-diff`. Values:
  - unset / `1` / `on`: on;
  - `lanes`: also the licensed row arm B2l;
  - `0` / `off`: nothing reaches the tables, and decisions record
    `declined -- disabled by BLADE_CELL_EPILOGUE`.
- **`BLADE_TILE_TUNE` keys `epi_blk` and `epi_blk_r`** (`CodeGenExprSupport.tileTune` :1985). They
  are speed-only: no bit depends on them, because P1's tree is fixed by B = 32 and P2's chain order
  is fixed.
- **Plan rule `cell-epilogue` v1.** `applied` records name the form:
  - `rank-1 producer: K-lanes(8) stream per Path B chunk`;
  - `chain interleave (mini-blocks of 16)`;
  - `compact row classes (2 classes)`;
  - `compaction: one pass, fold fused, positions elided`.

  `declined -- <reason>` records come from the refusal lists above, including the per-mode emitter
  declines (published through the existing `EmittedCpp` handshake pattern). Keep the
  `[fold-form v1]` text tokens the bench harness classifies (`K-lanes(8)`, `simd`,
  `serial (repro)`).

**C++ lane.**

- **`blade_fold_lanes.hpp`:** add P1 (`lane_state`, `feed_*`, `finish_*`), always_inline, with
  rounded products. Tests go in `src/cpp/fold_lanes_tests.cpp` (`blade test fold-lanes`): raw bits
  of feed/finish against `model::sq/sum` for n in {0..70, 1001, 2001, 20011}, under both
  contraction modes.
- **B1:** a new `tryGenFlatEpilogueNest` next to `tryGenFlatElementwiseNest`
  (`CodeGenLoopNest.fs:2371`), chosen when the pending plan is `ChunkFold`. It emits one of three
  forms: the serial-licensed P1 loop, the mt8 Path B region (`chunk_lo`, `__rpart/__rhad`,
  published names), or the P2 loop.
  - The consumer side is the hot-row consumer arm at `CodeGenBinding.fs:113`, which learns
    `ChunkFold`: a thread-order combine of `__rpart` through the fold wrapper, seed first (a copy
    of the epilogue at :4075ff).
  - laneLines (:4007-4075) is NOT edited.
- **B2r stencil:** the `genLoopNestStreamed` hot-row path (:1738-1760, :2149-2180) strip-mines the
  inner level when `CellChain` is set and the row kernel's form is serial (repro or unlicensed).
  The row chain is a local `__hrc`, written to `__hr_*[__i0]` at row end. `RowEpilogue`
  (IRStorage.fs:123) gains `CellStep: (string -> string) option`, built in `buildRowEpilogue`
  (CodeGenCuda.fs:2006).
- **B2r recur:** `CodeGen.fs:233` (function-body IRForRange) consults `CellFoldTable` for the cell
  loop. It strip-mines the loop, adds the chain, and binds `pId` to the chain local instead of
  rendering the prodsum IIFE.
- **B3:**
  - Hot-row admission accepts `compactFlat`, dropping `compactFlat.IsNone` for this form only.
  - The row view is `__orow_*` of length `n - __i0`; the body writes the class partials
    `__hrd[__i0]` and `__hro[__i0]`.
  - The consumer sits at the orbit dispatch (`CodeGenBinding.fs:3740-3765`). When the plan has
    published, it emits the serial row combine plus `genOrbitFoldCpp`'s `Total` combine (factor
    `emitC` out of :3620).
- **B4:**
  - `materializeMaskForm` (`CodeGenExpr.fs:1996`) publishes m.
  - `genCompoundInitBinding` (`CodeGenBinding.fs:1908`) emits the fused fill/gather/fold pass.
  - `index_types.h:354` gains a constructor that takes (n, m) and an optional positions buffer the
    caller fills; the constructor no longer counts or fills.
  - B4a: admit the rank-1, trailing-free compound operand where `laneableFoldLevel` (:2160ff) and
    the `CodeGenBinding.fs:3770` reject list currently refuse it. The `y*y` compound temporary
    (`CodeGenCuda.fs:3650-3675`) is then never requested.

**LLVM lane** (serial only; it has no thread forms).

- `tryBindHotRowProducer` (:4004) gets the P2 strip-mine (repro) and the compact arm (B3, reusing
  the `emitOrbitFold` combine at :6006).
- B1 goes in the flat materialize path, plus a consumer that folds the published partial.
- B2r recur goes in `emitForRange` (:2978).
- B4 goes in `emitCompoundInit` (:6870), plus a new inline count/fill that needs no shim. The
  existing `blade_compact1_rank` keyed-read symbol stays for positions.
- Licensed LLVM folds keep their own `reassoc` trees (within 1e-9 of the C++ lane, as today).

**Interpreter twin.**

- No change in `src/Interp/`. All forms are emission schedules over side tables (the HotRowTable
  pattern), and the IR the interpreter evaluates is untouched.
- Under `BLADE_FP_REPRO=1 BLADE_FP_CONTRACT=off`, B1-repro, B2r and B4 produce the same operation
  sequences. So `test interp` and `diff-oracle` stay byte-identical, and `opt-diff` (gate off vs
  on) compares them.
- The LICENSED forms of B1, B3 and B4 are invisible to those gates, because the gates pin repro.
  Add a licensed block, either as `blade test optimize` cases or under a new key `cell-epilogue`,
  with three checks:
  - gate on vs off, at 1e-12 relative;
  - B1 licensed, raw bits OLD == NEW (the P1 invariant);
  - B3 determinism: OMP_NUM_THREADS 1, 3 and 8 give identical bits.

**Tests.** Corpus values must be association-invariant per the 2026-10-17 policy: integer-valued
or dyadic data whose sums are exact.

- `loops/480_cell_epilogue_rank1_checksum.blade`: saxpy shape, n = 1003, integer x/y. EXPECT a
  sample y2 cell (through a fold) and EXPECT s.
- `loops/481_cell_epilogue_rank1_declines.blade`: lost team, read beyond the cell, a reader in
  between. Values are pinned; the decisions are checked via OptimizeTests.
- `loops/482_hot_row_cell_chain_tails.blade`: stencil shape with interior widths 1, 17 and 33
  (mini-block tails). EXPECT s.
- `recursive-arrays/030_row_fold_cell_chain.blade`: a dyadic recurrence (`* 0.5 + 1.0`) over
  X = 1003. EXPECT the total and one row sum.
- `symmetry/128_compact_row_class_epilogue.blade`: SymIdx<2, 37> with an integer-valued pair
  kernel. EXPECT ss.
- `symmetry/129_compact_row_class_declines.blade`: AntisymIdx with unknown parity (two classes),
  and a reader between producer and fold (declined).
- `index-types/515_compaction_fused_fold.blade`: EXPECT checksum and m.
- `index-types/516_compaction_keyed_read_keeps_positions.blade`: a keyed read; ABORT BL8006 on a
  non-kept key.
- `index-types/517_compaction_empty_fold_aborts.blade`: ABORT "reduce: empty array".

Needles go in `tests/OptimizeTests.fs` (`blade test optimize`), next to the hot-row cases
(:798-892, :1883-1892):

- saxpy: `blade_fold::feed_sq` present, and no `sq<double, 8>(__rsrc0`;
- mt8 saxpy: exactly one `BLADE_OMP_PARALLEL_NUM_THREADS`;
- repro: the mini-block `+=` sits inside the producer loop, with no standalone reload loop;
- gate off: the two-pass form;
- pairkernel: no `__opart` region, and `__hrd_`/`__hro_` present;
- maskagg: no `new double[y.idx->cardinality`, and one fused pass;
- one decisionCase per declined reason.

The fold-lanes raw-bit additions go in `blade test fold-lanes`.

**Docs.**

- CLAUDE.md env-table row for `BLADE_CELL_EPILOGUE` and the two `BLADE_TILE_TUNE` keys.
- A `docs/features.md` row.
- `bench/notes/gap-register.md` entries, cross-referencing G10, G35 and G36.

## 5. Effort, risks, DROP experiments

Run each DROP experiment BEFORE implementing: hand-patch the `--verbose` C++.

| Sub-item | Effort | Main risks | DROP experiment |
|---|---|---|---|
| B2r chain interleave (recur, stencil) | M | The OoO window may be too small for overlap; GCC may mis-vectorize the fixed-trip mini-block; store-forward stalls. | Patch the recur and stencil serial-repro C++ with mini-blocks of 8/16/32/64 and check raw bits are equal under CONTRACT=off. DROP B2r if the best block recovers less than 25% of the measured repro-licensed gap (recur < 1.2 ms, stencil < 0.9 ms) over 11 interleaved runs. |
| B1 saxpy (P1, P2, mt8 region) | M | Per-block feed overhead; `chunk_lo` partition vs static first-touch placement. | Patch serial-licensed and serial-repro saxpy. DROP the licensed half if the incremental falls by less than 0.3 ms (half the modeled reload); DROP the repro half if it falls by less than 0.5 ms. |
| B4 maskagg (a+b+c; d optional) | M (a: S, d: M) | Keyed-read paths that assume positions exist (B4d); harness parsing of fold-form text. | Patch B4a alone, then a+b+c. DROP b+c if the incremental falls by less than 0.6 ms beyond B4a. |
| B3 pairkernel compact rows | M | Licensed checksum bits change vs OLD (regrouped differently, but deterministic). Dynamic-schedule load balance is unchanged. The row combine on the master thread is serial, 2 x 2503 adds (negligible). | Patch mt8 licensed: per-row partials in the producer loop, orbit region deleted. DROP if the mt8 total falls by less than 0.3 ms. |
| B2l licensed lanes arm | S once P1 exists | Spills in recur's RK4 block. | Ship only if `lanes` beats the default by more than 2% on recur serial licensed. |

Two cross-cutting points:

- Repro+fast bits may differ from OLD. That build is not a bitwise contract; the decision record
  should say so.
- Licensed B1 and B2l keep identical bits by construction. Verify that with raw bits; it is a
  strong correctness check for free.

## 6. File-overlap list

| File / function | B touches | A | C | D | E |
|---|---|---|---|---|---|
| `Optimize.fs` `optimizerGates` :77 | adds 1 gate | | likely adds 1 gate (textual merge only) | | |
| `Optimize.fs` `HotRowPlan`/`HotRowTable`/`planHotRowFolds` :1504-1683 | extends (rank-1, compact, nested chains, CellChain) | G35 may want row slots here; coordinate | C's let-use / sibling-fold planner should be a SEPARATE function; it only reads `cseFactsOf` | | |
| `Optimize.fs` `fuseRowFoldsModule` :2004 | records CellFoldTable | | | | **yes** (an omp slot on the inductive arm threads the same loop) |
| `Optimize.fs` `fuseSliceWritesModule` :1731 | read-only | | | | **yes** |
| `Lowering.fs` :2715, :2911 | table reset + planner call | | a planner call nearby (textual) | | |
| `CodeGenLoopNest.genLoopNestStreamed` hot-row :1738-1760, :2149-2180 | yes | G35's Path B region :1560-1640 (same function, other lines) | Path B region :1560-1640 and :2180ff (same function, other lines) | compact-nest pragma / schedule (pairkernel team) | |
| `CodeGenLoopNest.tryGenFlatElementwiseNest` :2371 | adds a sibling function | | possibly (sibling folds on the flat pass); **sequence B1 first** | | |
| `CodeGenBinding.fs:113` hot-row consumer arm | yes | | | | |
| `CodeGenBinding.fs` laneLines :4007-4075 | **no** (by design) | | **yes** | | |
| `CodeGenBinding.fs` orbit dispatch :3740-3765, `genOrbitFoldCpp` :3562 | yes (B3) | **yes** (ordered-scatter dispatch) | | yes (pairkernel team) | |
| `CodeGenCuda.fs` `buildRowEpilogue` :2006, compound temp :3650 | yes | | | | |
| `CodeGen.fs:233` IRForRange statement | yes (B2r recur) | | | | **yes** |
| `EmitLlvm.fs` hot-row :3926-4080, `emitForRange` :2978, `tryOrbitFold`/`emitOrbitFold` :5990-6100, `emitCompoundInit` :6870 | yes | `tryOrbitFold` neighbourhood | | | `emitForRange` |
| `blade_fold_lanes.hpp` | adds P1 | | may reuse P1 for sibling states | | |
| `index_types.h:354`, `CodeGenExpr.materializeMaskForm` :1996, `CodeGenBinding.genCompoundInitBinding` :1908 | B4 only | | | | |
| tile runtime (`blade_tiles.h`, `blade_packed_gemm.hpp`, `blade_llvm_tiles.c`) | **none** | | | yes | |

## 7. Recommended waves and A/B protocol

Ranking, by work removed per unit of effort, strictness, and the headline losses each addresses:

1. **B2r.** The largest strictly legal mechanism: recur and stencil serial repro, and stencil mt8
   repro.
2. **B4.** Maskagg is the only remaining serial-licensed loss to both experts. Half its bytes and
   about 60% of its allocations go, and its files are self-contained.
3. **B1.** Saxpy in every regime; it shares P1/P2 with B2.
4. **B3.** Pairkernel mt8 and serial licensed; it must be sequenced with A and D.

B2l is an arm, not an item.

**Wave 1** (parallel-safe):

- Branch `cell-epilogue`: P1 + P2 + B2r + B1 (one runtime piece, one gate, one decision rule).
- Branch `compaction-fusion`: B4. Its files are disjoint from the first branch except the
  Lowering call site and the gate list.
- Run the four hand-patch DROP experiments first.
- Sequence B1 before C's sibling-fold work on flat passes.
- Sequence B2r before E. E must keep B2r's decline rule: a threaded cell loop means the chain runs
  after the row.

**Wave 2:** B3. It lands after A's compact-consumer dispatch (or with an agreed dispatch hook), and
before D's pairkernel team-reuse item, because B3 changes what that team contains.

**Wave 3:** the B2l arm. Also an mt8-repro saxpy pipeline (one thread runs the global chain behind
the producer threads), but only if D/G35 build a pipeline runtime.

**A/B protocol.** Run it in the harness with three arms:

- OLD: the 4136f0b5 binary;
- NEW: the branch binary;
- CTRL: NEW with `BLADE_CELL_EPILOGUE=0`.

Use interleaved rounds, n = 21, medians with CIs and a drift check. Compute incrementals against
input-only twins from the same binary.

Wave-1 cells (arms blade-llvm serial, blade-cpp serial and mt8):

- saxpy: serial licensed, serial repro, mt8 licensed, and mt8 repro (a control; expect equal);
- stencil: serial repro and mt8 repro, with serial/mt8 licensed as controls;
- recur: serial repro, with serial licensed as a control;
- an `epi_blk_r` sweep over {8, 16, 24, 32, 64} on recur and stencil serial repro.

Bit checks:

- NEW == OLD raw checksums under `BLADE_FP_REPRO=1 BLADE_FP_CONTRACT=off`;
- saxpy licensed NEW == OLD, serial and mt8 at a fixed T.

B4: maskagg serial licensed and repro, both lanes, OLD/NEW/CTRL.

B3: pairkernel serial and mt8 licensed (the repro cells serve as controls), plus the team-size
determinism check.

Report incrementals, not only totals: G37's input-conversion fix moved the C++ twins.

**Protocol question for the coordinator.** B4 v1 keeps `keep` stored, because it is a user-named
binding; the spec names only `y` as the output. If an unprinted `keep` may be treated as dead, a
later v2 can fold the mask pass into the compaction pass: one pass over x, about 13 MB.
