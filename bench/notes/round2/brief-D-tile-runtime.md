# D -- tile runtime: persistent teams, stable ownership, antisym row groups, simplex edges, pairkernel mt8

Research brief, round 2, item D. Source of truth: master 4136f0b5 (`bin\Release\net10.0\Blade.exe`).
Evidence was gathered by reading source, by emitting C++ and plans for the four bench kernels (mt8
licensed default and `BLADE_FP_REPRO=1`; outputs in `scratchpad\round2\D\*.mt8.cpp`,
`*.plan`), and by counting with small Python models (`scratchpad\round2\D\scripts\{lanes,vec,cyc,own,traffic}.py`).
No timing was run. Every number below is a count or a recorded measurement, not a forecast.

## 0. Verdict and ranking

| rank | sub-item | verdict | why (one line) |
|---|---|---|---|
| 1 | **D2 antisym row-panel groups + cache gate** | BUILD (wave 1) | antisym today streams B from L3 at 1.333 B per lane-FMA, which is gram's `syrk_g=1` regime. G=4 gives 0.337, gram's default. Both lanes. Strict-legal: only the visiting order changes. |
| 2 | **D1-0 mt8 `syrk_g` sweep (no code)** | RUN FIRST (wave 1) | `syrk_g` was A/B'd **serial only** (RESULTS 14.5). Whether mt8 gram is bound by L3 B traffic is unmeasured, and the knob already exists. |
| 3 | **D3b simplex edge variants (live-accumulator kloops)** | BUILD after D2 (wave 2) | At p=171, 7.3% of the simplex tile's vector FMAs are wholly dead (staircase plus padding). That is ~4.0% of issue cycles under a 4-cycle FMA-latency floor. Both lanes, shared core. |
| 4 | **D4 pairkernel team reuse** | NO D CODE; hand the lever to B (S11) | Team entries cost tens of microseconds against 7.54 ms. The 1.5 ms gap is the separate 25.07 MB checksum re-read, which only a producer-local epilogue removes. |
| 5 | **D1 persistent syrk team + stable ownership** | DROP unless D1-i says otherwise | Per call it saves 2 forks and about 4-6 barriers (~10-40 us of ~12 ms). Dynamic(1) is already within 0.7% of ideal balance. Stable ownership buys no C locality: each thread's C share (1.28 MB) exceeds L2. |
| 6 | **D3a weighted prefix-block tasks (comoment3)** | DROP on the count | Under dynamic(1) over i, LPT makespan/ideal is **1.000** at p=171 and T=8: i=0 is only 1.7% of the work. Nothing is imbalanced, and W cannot be shared across i. |

## 1. Hook verification

| plan claim | current `file:function:line` | status | evidence |
|---|---|---|---|
| `dsyrk_tri` ~:234-360 | `src/cpp/blade_packed_gemm.hpp:dsyrk_tri:263-362` | holds (shifted) | jc loop :285, pc (K block) loop :292, `rowGroupVisit` :307, team :343-353, serial path :354-361 |
| parallel region INSIDE each pc iteration (~:343) | `blade_packed_gemm.hpp:343` `#pragma omp parallel num_threads((int)T)` inside `for (pc...)` :292 | holds | Per K block: fork, `omp for schedule(static)` packB :345 (barrier), `omp for schedule(dynamic, 1)` row groups :348 (barrier), join |
| gram bench shape | `bench/kernels/gram/gram.blade`: p=1601, n=1001; plan `P=1601 K=1001 KC=334 G=4` | holds | 1 column block (1601 < TRI_NC 2048). `tri_kc(1001,336)` gives 3 K blocks of 334/334/333. 67 row groups of 24 rows. T = min(8, (p²/2·k)>>20) = 8 |
| team entries per gram call | as above | **counted** | **3 forks/joins + 9 barriers** (3 × {pack end, loop end, join}). antisym and comoment3 at bench shape have **1 fork + 3 barriers** each: antisym has one jc block (NCb=1304 ≥ 1201), and dsym3 opens one region :561 |
| `dantisym_tri` ~:399-475 | `blade_packed_gemm.hpp:dantisym_tri:399-474` (`rowPanel` :434, team :457-468) | holds | `rowPanel(i0)` visits every q ≥ q0 and reloads that q's X/Y B pair. No row groups |
| 6x8 / 4x4 cores ~:310-408 | `src/cpp/blade_tiles.h:blade_anti_tile_6x8:325`, `_4x4:371`, `blade_anti_kc:311` | holds | Sequential P pass, 48-double spill, Q pass. `anti_kc` blocks inside one visit |
| `dsym3_tri` ~:504-575, `doI` ~:528, dynamic i ~:566 | `blade_packed_gemm.hpp:dsym3_tri:504-575`, `doI:528`, `omp for schedule(dynamic, 1)` over i :566 | holds | packK static :563, one region per call |
| core full/edge :118/:155 | `blade_tiles.h:blade_tri_tile_full:124`, `blade_tri_tile_edge:164` | holds (shifted) | The edge tile runs the full 6×8 `blade_tri_kloop` and masks only the store (:184-205) |
| "32 MB/CCD" comment ~:208 | `blade_packed_gemm.hpp:208` `// Blocking (Zen 3: 32 KB L1d, 512 KB L2, 32 MB L3 per CCD)` | **holds, and is WRONG for this box** | `bench/notes/charter.md:38` and `phase2-microkernel-plan.md:19`: Ryzen 7 5800HS has a **16 MB** L3. `anti_nc` :389 also sizes B at "~8 MB so the B side stays in L3", which is half the real L3 (7.75 MB at the antisym bench shape) |
| tile dispatch in `CodeGenExprSupport.fs` | knobs: `CodeGenExprSupport.fs:tileTune:1985`, `syrkRowGroup:2001`, `antisymKcMax:2004`, `packedTriWorth:1952`, `syrkTileKcMax:1961`. **Dispatch** is elsewhere: `CodeGenLoopNest.fs` syrk :3020-3090 (call text :3086), antisym :3180-3207, sym3 :3208-3226; gram node `CodeGenExpr.fs:3133-3185` | changed (split) | Emission text such as `..., true, 0, 0, (size_t)0);` is pinned by needles in `tests/LinAlgTests.fs:1820-1835` |
| LLVM lane shares the C core, no threads (G11) | `src/cpp/blade_llvm_tiles.c:blade_dsyrk_tri_f64:116`, `blade_dantisym_tri_f64:281` (row loop :333), `blade_dsym3_tri_f64:420` | holds | **Own serial driver copies**: C++ driver edits do not reach it. Scratch is sized by the emitter (`EmitLlvm.fs:antisymTileScratchCells:243`), and the core **silently falls back to the scalar chain if handed less** |
| `genLoopNestStreamed:1491` | `src/CodeGenLoopNest.fs:genLoopNestStreamed:1463` | shifted | -- |
| `CodeGenBinding.genOrbitFoldCpp` | `src/CodeGenBinding.fs:genOrbitFoldCpp:3562` | holds | Emitted as a separate `BLADE_OMP_PARALLEL_NUM_THREADS(__oT)` region (pairkernel.mt8.cpp:308-339) |
| G1 tuning (`syrk_g` 4 kept, `anti_kc` 200 ~3-4%) | `bench/RESULTS.md` §14.5 | holds; **serial-licensed only** | "Within-session A/B (items a, b; serial licensed)". No mt8 sweep of either knob exists |
| refuted schedules | `src/microkernels/README.md` corrections 1, 4, 5 | holds | per-i rank-3 repack 0.82-0.93x (0.89-0.95x even with free packing); paired diagonal triangles -4.9% at n=337; HW gather loses 1.09-1.65x. None is re-proposed below |

## 2. The transformations, precisely

All four touch only the **schedule of an already-landed per-cell computation**. Each cell keeps
exactly one ascending chain per sum: syrk and simplex have one, antisym has P and Q plus one
subtraction. The chain is run by one owner. K blocks store and reload the exact binary64 partial.
Hence every change below is **strict-legal**: it is ON under `BLADE_FP_REPRO=1` and `where repro`,
and the pool bytes are unchanged for every team size, group size and knob value. This is the shape
G35 already records as repro-safe. The thread licence stays as today: the tile threads only when
`codeGen.OmpRequested && ompThreadEmissionEnabled ()` (`CodeGenLoopNest.fs:3063`), i.e. a source
`omp` on the kernel and `BLADE_OMP_THREADS` not suppressing it. No licence is borrowed or unioned.
The interpreter twin is unaffected: it evaluates each cell's serial chain, which the tiles already
match bitwise under `BLADE_FP_CONTRACT=off`. Nothing here is in `Optimize.fs`, so no `optimizerGates`
entry is needed.

### D2 -- antisym row-panel groups (the syrk `rowGroup` pattern, applied to `dantisym_tri`)

*Recognized input:* unchanged. The `(|RowContraction|_|)` Anti shape is dispatched at
`CodeGenLoopNest.fs:3180` (literal p, n; `packedAntiWorth`).

*Today* (`blade_packed_gemm.hpp:434-452`). Each row panel packs its own X(i)/Y(i) panels, then
walks every column panel. Each of the 200 row panels therefore re-streams the shared B pair from L3:
```
auto rowPanel = [&](size_t i0, double* axp, double* ayp) {
    ... blade_tile_pack_t(sx, mr, RM, n, axp); blade_tile_pack_t(sy, mr, RM, n, ayp);
    const size_t q0 = blade_tile_panel_first(jc, nc, RN, i0 + 1);
    for (size_t q = q0; q < nq; q++) { ... blade_anti_tile_6x8(n, axp, ayp, bxp + q*RN*n, byp + q*RN*n, crow, i0, j0, mr, nr, sk, sc, kcMax); }
};
... #pragma omp for schedule(dynamic, 1)
    for (size_t i0 = 0; i0 < iTop; i0 += RM) rowPanel(i0, axp, ayp);
```
*Change.* `rowGroupVisit(i0g)` packs up to G row panels (G·2·RM·n doubles per thread). For each q
from the group's first panel, it calls the **same** tile, with the **same** (i0, j0, mr, nr, panels)
arguments, for each member row panel g whose diagonal the panel reaches. The skip test is
`if (hi(q) <= i0 + 1) continue;`, the strict analogue of syrk's `jhi <= i0 + S` at :325. The loop
over row starts becomes `i0g += G*RM` under the same `schedule(dynamic, 1)`.

*Bitwise argument.* Every visit is today's call. Each cell is touched by exactly one visit, which
runs both whole chains (no pool partial exists). Only the order of visits across cells changes, so
the pool bytes are independent of G. G=1 reproduces today's visit order exactly.

*Cache gate.* It is decided at compile time on the literal extents, like `packedTriWorth`. Choose
`G = max g in {4, 2, 1}` with `g·2·RM·n·8 + 2·RN·n·8 <= 256 KiB` (half of Zen 3's 512 KiB L2: the B
pair must stay L2-resident across the g visits). At n=401 this gives G=4 (200 KiB) and at n=1001,
G=1. A shape that fails the budget at G=2 keeps G=1 and records why.

*Refusals:* none new. Shapes below the crossover, non-literal extents and unproven components keep
today's declines. The paired 4x4 variant takes the same grouping (RM=4).

*Lanes:* C++ driver plus the LLVM C driver (`blade_dantisym_tri_f64` row loop :333 gains the
same group loop and a `row_group` i64 parameter). The **serial** antisym cell (1.113 / 1.075, ref
blade-llvm) therefore gets it too.

*Deferred, P3: paired 6x4 core.* 6 rows × 1 vector × {P, Q} = 12 accumulators + X(j), Y(j) vectors
+ ≥1 broadcast register = 15 of 16 YMM. Registers are not the binding limit. Per sample it issues 2
vector loads + 12 broadcasts per 12 FMAs, i.e. 1.17 loads/FMA, against the sequential 6x8's 8 per
12 (0.67). At 12 FMAs per 6 cycles that is 2.33 loads/cycle, above Zen 3's two 256-bit loads per
cycle. It is load-port-bound before it is register-bound, which is the same reason the paired 4x4
(10 loads per 8 FMAs) measured slower. Defer. Revisit only if D2 shows the sequential tile's spill
traffic dominating.

### D1 -- persistent syrk team + stable ownership (C++ lane, mt8 only)

*Today* (`blade_packed_gemm.hpp:292-353`), once per K block:
```
for (size_t pc = 0; pc < k; pc += kcn) {
    ...
#pragma omp parallel num_threads((int)T)
    {
#pragma omp for schedule(static)
        for (size_t q = 0; q < nq; q++) packB(q);
        double* ap = ascratch.get(G * MR * kc);
#pragma omp for schedule(dynamic, 1)
        for (size_t i0g = 0; i0g < iTop; i0g += G * MR) rowGroupVisit(i0g, ap);
    }
    continue;
}
```
*Change* (single-buffer form; `team=1`):
```
if (T > 1) {
  #pragma omp parallel num_threads((int)T)
  { const size_t nt = omp_get_num_threads(), t = omp_get_thread_num();
    for jc ...:                                   // every thread walks the same jc/pc sequence
      owned = groups dealt boustrophedon over nt (or static LPT by visit count), fixed for this jc
      for pc ...:                                 // ascending: the chain order
        #pragma omp for schedule(static)          // cooperative pack; implicit barrier = B ready
        for q: packB(q)
        for g in owned: rowGroupVisit(i0g(g), ap) // same owner for every K block of this jc
        #pragma omp barrier                       // nobody repacks bp while a reader remains
  }
}
```
The double-buffer form (`team=2`) packs block pc+1 into the other `bp` half. One barrier per K block
remains: the pack's own barrier, because every reader of the buffer being overwritten finished block
pc-1 before passing block pc's pack barrier. It costs a second 4.3 MB B buffer at the gram shape
(16 MB L3 already holds X 12.8 MB and C 10.3 MB, so it is not free).

*Bitwise argument.* Block pc+1's tile on a cell reads the partial that block pc stored. With stable
ownership the same thread wrote it (program order). With dynamic ownership, a barrier separates
them. Bits do not depend on T, nt, dealing or buffering. The serial path (:354-361, no OpenMP call)
is untouched byte-for-byte, and the LLVM core has no threads. **D1 is C++ mt8 only.**

*Counts at the gram shape.* Today: 3 forks/joins + 9 barriers. `team=1`: 1 fork/join + 6 barriers.
`team=2`: 1 fork/join + 3 barriers.

Balance, modelled in visits (`own.py`), as makespan/ideal at T=8 over the 67 groups: **dynamic(1)
1.007, boustrophedon 1.007, static LPT 1.007, weighted contiguous 1.176**. The planning doc's
"weighted contiguous ranges" are refuted: at 24-row groups the coarsest group is 3.0% of the work,
24% of a thread's share. Stable ownership must be a dealt or LPT assignment.

Locality: C is 10.3 MB, or 1.28 MB per thread, which exceeds the 512 KB L2. Each thread's partials
are evicted to L3 between K blocks whoever owns them, so stable ownership saves no line migration
at this shape. It only matters when the per-thread C share fits L2 (p ≲ 700 at k ≫ KC).

*Expected work removed.* 2 forks + 3-6 barriers ≈ 10-40 us against a tile phase of roughly 12 ms
(the mt8 total is 13.96 ms) ≈ **0.1-0.3%**. Barrier straggle under dynamic LPT is ≤ the last-dealt
(small) group. This is why D1 ranks last.

### D1-0 -- the experiment D1 should have been: mt8 `syrk_g` sweep

B-panel traffic from L3 per gram call (`traffic.py`, all 3 K blocks):

| syrk_g | MB from L3 | B per lane-FMA |
|---|---|---|
| 1 | 1725.5 | 1.333 |
| 2 | 867.0 | 0.670 |
| **4** | **437.8** | **0.338** |
| 8 | 222.2 | 0.172 |

Serially G=4 vs 8 measured flat to worse (57.3 vs 60.5 ms llvm; 60.1 vs 64.0 cpp). Under 8 threads
sharing one 16 MB L3, the B stream is shared bandwidth. mt8 gram scales 61.88 → 13.96 ms (4.4x on
8 cores); antisym scales 29.97 → 9.03 (3.3x). The sweep is **zero code**:
`BLADE_TILE_TUNE=syrk_g={2,4,6,8}` on gram mt8 licensed and mt8 repro.

### D3 -- simplex (comoment3) edge variants; D3a weighted tasks DROPPED

*D3a, counted and dropped.* Tile visits per i at p=171 (`lanes.py`): i=0 has 330 of 19,568
(1.7%). LPT under today's `schedule(dynamic, 1)` in ascending i (largest first) gives makespan/ideal
**1.000** at T=8 (1.002 at p=61, where T=7). W(i, j) = X(i)·X(j) depends on i, so there is no
cross-i W sharing to retain or gain. Prefix-block tasks would change nothing measurable.

*D3b, useful-lane counts* (`lanes.py`, `vec.py`; lanes = 48 per visit):

| shape | edge visits | useful | staircase | pad | dead vector-halves | cycle model |
|---|---|---|---|---|---|---|
| sym3 p=61 | 44.3% | 76.2% | 21.3% | 2.6% | 18.2% | 9.9% |
| sym3 p=62-66 | 41.8-43.7% | 76.5-77.7% | -- | -- | -- | -- |
| **sym3 p=171** | **18.9%** | **90.3%** | **9.3%** | **0.4%** | **7.3%** | **4.0%** |
| sym3 p=168-175 | -- | 90.1-90.5% | -- | -- | -- | -- |
| sym3 p=337 | 10.1% | 94.9% | 5.0% | -- | 3.9% | 2.1% |
| syrk p=1601 | 1.5% | 99.2% | -- | -- | 0.6% | -- |
| anti p=1201 | 2.0% | 99.1% | -- | -- | 0.7% | -- |

"Dead vector-halves" counts (r, h) accumulators with no live lane. The "cycle model" column is the
saving in per-sample issue cycles under max(live/2 FMA ports, 4-cycle FMA latency floor), against 6
today. Cells marked "--" were not computed. The edge kernels matter for comoment3 only; at
gram/antisym bench shapes they are noise.

*Change.* `blade_tri_tile_edge` (`blade_tiles.h:164`) dispatches to a kloop specialization
`kloop<L0, L1>`. L_h is the number of leading rows whose column half h has any live lane:
`L_h = clamp(4h + 4 + (c0 - r0 - S), 0, mr)`, and L1 = 0 when nr ≤ 4. The kloop issues broadcasts
for rows < L1 and FMAs into c[r][0] for r < L0 and into c[r][1] for r < L1. Valid pairs satisfy
0 ≤ L0 ≤ L1 ≤ 6, at most 28 instances, generated by macro in C (the header must stay C for the LLVM
core). Live accumulators get the identical s-ordered FMA sequence, and dead ones were never stored,
so the arithmetic is bitwise unchanged. This is neither a repack (refuted 0.82-0.93x) nor paired
triangles (refuted -4.9%): no layout or panel changes.

*Lanes:* the dispatch lives inside `blade_tri_tile_edge`, which both C++ drivers (`dsyrk_tri`,
`dsym3_tri`) and both LLVM C drivers call, so both lanes get it with no driver loop edit. Only a
mode argument threads through for the A/B (see §4).

### D4 -- pairkernel mt8 (G32): diagnosis; no D implementation

*Emitted mt8 schedule* (pairkernel.mt8.cpp):

- Producer, :287-301: `BLADE_OMP_PARALLEL_FOR_DYNAMIC` = `omp parallel for schedule(dynamic)`
  (chunk 1) over rows i ascending. Row i has 2503-i cells, so the largest rows go first. Each cell
  loads a row pointer `__v24.data[__i1 + __i0]` plus 3 AoS doubles, then runs `matern52`: sqrt, two
  divides, one scalar `blade_libm::exp`, the same platform `exp` symbol via an asm label. That is
  3,133,756 exp calls, diagonal included.
- **Second team** for the orbit-class checksum, :303-339: `BLADE_OMP_PARALLEL_NUM_THREADS(__oT)`.
  Rows are dealt boustrophedon. It **re-reads the whole 25.07 MB pool** (3,133,756 × 8 B) after the
  producer team joined.
- Two further teams: the input map :262 (7,509 lcg cells) and the input-sum Path B :352-376.

*Expert* (`bench/kernels/pairkernel/expert.f90:56-80`):

- `!$omp parallel do schedule(dynamic)` over columns j = n..1, largest first: ownership-equivalent
  to Blade's rows.
- SoA x/y/z, so no pointer load.
- The diagonal is stored as 1.0 with no exp: 2,503 fewer exp calls, 0.08%.
- **Each finished column (≤ 20 KB) is re-read for the checksum from L1 inside the same task.**
- Input fill and input sum are serial: 1 team total versus Blade's 4.

*Attribution.* Serial Blade already beats the expert: 29.15 ms vs 32.7/30.52, ratio 1.12/1.05. The
mt8 producer scales about like the expert's: the diagnostic ~5.4 ms vs the expert's 6.03 ms
including its fused checksum. So the per-cell work, exp-call count, AoS reads and triangular load
balance are not the loss. The 7.54 - 6.03 = **1.51 ms** gap matches the diagnosed **~1.5 ms
checksum pass**. A 25.07 MB pool cannot sit in a 16 MB L3, so at least ~9 MB of the re-read comes
from DRAM.

Three extra team entries with warm, spinning workers cost ~5-20 us each, **≤ 0.8% of 7.54 ms, and
not material**. Merging producer and orbit pass into one region, with a barrier between, still
re-reads 25 MB and saves one fork. **Most plausible cause of the 0.80: the separate DRAM-bound
checksum re-read.** About 0.6 ms of the 7.54 ms stays unattributed by the diagnostic split (input
teams, process noise).

The cure is B's producer-local epilogue (S11). D's one contribution is a **determinism rule for
that epilogue**. The producer is `schedule(dynamic)`, so per-thread class partials would depend on
the schedule. B should write per-row class partials into an indexed slot array (2 doubles × 2503
rows = 40 KB) and combine them in ascending row order. That is additions-only (licensed regrouping,
as today's orbit fold), and deterministic for **any** team size and schedule, stronger than today's
"deterministic for a fixed team". The team merge falls out of S11 for free; it is not a D work item.

## 3. Target cells (RESULTS §14.8, ratio = expert/Blade)

| cell | ratio gf / ifx | mechanism by which work disappears |
|---|---|---|
| mt8 licensed antisym | 1.179 * / 1.180 * | D2 G=4: L3 B-pair loads 777.6 → 196.3 MB per call (1.333 → 0.337 B/lane-FMA). A-pair L2 re-reads (583 MB) are unchanged. 50 groups instead of 200 row panels; LPT 1.009 |
| mt8 repro antisym | 0.993 / 1.000 | Same bytes. The tile runs identically under repro; the rest of the 11.22 ms is the serial repro checksum walk (item A) |
| serial antisym (ref llvm) | 1.113 / 1.075 | Same bytes via the LLVM C driver. The analogue in gram serial: `syrk_g` 1 → 4 measured 61.1 → 57.3 ms llvm, flat on cpp |
| mt8 licensed gram | 1.393 * / 1.332 * | D1-0: L3 B traffic 437.8 → 222.2 MB at G=8 (if mt8 rewards it). D1: 2 forks + ≤6 barriers ≈ 10-40 us |
| mt8 repro gram | 1.119 * / 1.036 * | Same tile mechanisms. 22.03 - 13.96 ≈ 8 ms of this cell is the serial repro logical checksum walk (item A), so the tile share is smaller |
| mt8 licensed comoment3 | 1.659 * / 1.373 * | D3b: 7.3% of simplex vector FMAs dead (19,568 visits, 18.9% edge). Model: ~4.0% of tile issue cycles |
| serial comoment3 | 1.325 / 1.338 | D3b in both lanes (shared core), same counts |
| mt8 repro comoment3 | 0.478 / 0.427 | Dominated by the repro checksum (item A). D3b moves only the tile share |
| mt8 licensed pairkernel | 0.800 * / 0.798 * | Not a D lever: the 25.07 MB re-read goes away only under B's S11 epilogue. Team merge ≈ one fork |

## 4. Implementation design

**Knobs** (all in `BLADE_TILE_TUNE`, already in `CliSelfTests.fs:94 suiteClearedKnobs`, parsed by
`CodeGenExprSupport.tileTune:1985`; speed-only, no licence, apply under repro; they change the
emission so the exe cache keys on them):

| key | values | default | where |
|---|---|---|---|
| `anti_g` | `0` = auto cache budget, `1..8` = forced G | 0 (auto) **only if the A/B wins; otherwise 1** | New `antisymRowGroup p n paired` beside `syrkRowGroup:2001`; also holds the budget rule and the L2 constant (512 KiB, halved) |
| `tri_edge` | `0` = today's full kloop, `1` = live-accumulator variants | 1 if it wins | -- |
| `syrk_team` | `0` per-block (today), `1` persistent, `2` persistent double-buffered | 0 | Only if D1 survives D1-i |
| `syrk_own` | `0` dynamic(1), `1` boustrophedon stable | 0 | Only if D1 survives D1-i |
| `anti_nc` | optional column-block cap for the sweep | today's `anti_nc` | Already a driver parameter (`ncMax`); the emitter passes 0 |

**D2 files.**

- `src/cpp/blade_packed_gemm.hpp:dantisym_tri:399-474`: new trailing parameter `size_t rowGroup = 1`;
  the group loop; scratch `axs/ays.get(G*RM*n+1)`.
- Fix the comments at :208 and :379 to "16 MB L3 (5800HS)" and state the budget.
- `src/cpp/blade_llvm_tiles.c:blade_dantisym_tri_f64:281`: add an `int64_t row_group` parameter, the
  group loop at :333, and `blade_llvm_anti_scratch:269` growing by (G-1)·2·RM·n.
- `src/EmitLlvm.fs:antisymTileScratchCells:243`: same formula. **Mismatch silently drops the core
  to its scalar loop**, which is still bitwise, so only a speed or tile-route assertion catches it.
- `EmitLlvm.fs:4889-4900`: the `declare` gains an `i64`, and the call passes G.
- `src/CodeGenLoopNest.fs:3180-3207`: G in the call text, in `recordCodegenDecision`
  (`P=.. N=.. KC=.. G=..`), and in `recordFoldForm`:
  `antisym tile sequential 6x8 ..., whole chain per tile, row groups of 4 (cache budget 200 KiB of 256 KiB)`.
  The decline wording `row groups of 1 -- cache budget: B pair + 2 row panels = N KiB > 256 KiB` is
  recorded as fold-form evidence, not a separate decision.
- The LLVM plan text in `CliCommands.fs` (~1011: `(shared C core)` evidence) gets the same phrase.

**D2 tests.**

- `src/cpp/row_contraction_tile_tests.cpp` (`blade test row-tile`): loop G ∈ {1,2,3,4,8} over the
  existing shape matrix, contract=off and fast. Assert `memcmp` of the pool **equal across G** as
  well as equal to the serial chain; threaded and serial.
- `src/cpp/llvm_tiles_rawbit.c` (`blade test llvm tiles`): pass row_group ∈ {1,4}. Cross-lane
  byte-identity, plus a tile-route check that the scratch was accepted.
- Needles:
  - `tests/LinAlgTests.fs:1820-1835` call text gains `, (size_t)G` (update 4-5 existing needles in the
    same change);
  - `tests/OptimizeTests.fs:1505-1532` gets `plan_antisym_tile_row_groups_knob` (`anti_g=2` → "row
    groups of 2") and `plan_antisym_tile_budget_auto` (n=401 → "row groups of 4"; a large-n program
    → "row groups of 1 -- cache budget");
  - `tests/LlvmTests.fs:~1069` gets the LLVM plan phrase.
- No corpus pins change: values are bitwise. An optional `symmetry/128` antisym at p=61, n=401 with
  existing EXPECT pins exercises the group loop in the default suite.

**D3b files.**

- `src/cpp/blade_tiles.h`: `blade_tri_kloop_live(kc, ap, bp, c, L0, L1)` with literal-bound
  instances, and `blade_tri_tile_edge` gains `int edge` (0 = today) and dispatches.
- Both C++ drivers pass `edge` (new defaulted parameter).
- The LLVM C drivers `blade_dsyrk_tri_f64` / `blade_dsym3_tri_f64` gain an `i64` (EmitLlvm declares
  :4774 and the sym3 call).
- Plan: `fold form: simplex tile 6x8 with W panel, KC=201, live-accumulator edges, then / d ...`.
- Tests: `syrk_tile_tests.cpp` (`syrk-tile`), `row_contraction_tile_tests.cpp` (`row-tile`) and
  `llvm_tiles_rawbit.c` (`llvm tiles`) loop edge ∈ {0,1} over shapes with p ≡ 0..7 (mod 8) and mr
  1..6 tails (p=13..30, 61, 171). Pools must be equal across modes and to the serial chain.
- Needles on the call text: `CodeGenLoopNest.fs:3086, 3225` and `CodeGenExpr.fs:3185`.

**D1 files** (if it survives).

- `blade_packed_gemm.hpp:dsyrk_tri:343-353` only (the serial path is untouched).
- `int team` and `int own` parameters, passed from `CodeGenLoopNest.fs:3086` and
  `CodeGenExpr.fs:3185`.
- The omp census string at :3071 / :3172 changes from "work units under schedule(dynamic)" to
  "one team per call, row groups dealt boustrophedon, stable across K blocks" (needle in
  `blade test linalg` / `optimize`).
- `syrk_tile_tests.cpp` already has threaded cases (`run_gram_case(..., threaded, ...)` :233). Add a
  team×own matrix with forced small kcMax (≥ 3 K blocks).

**Docs.** The CLAUDE.md env-table row `BLADE_TILE_TUNE` gains `anti_g`, `tri_edge` (and
`syrk_team` / `syrk_own` if kept). `docs/features.md` tile rows get one line each. No formalism
change: there is no semantic change.

## 5. Effort, risks, DROP experiments

| item | effort | risks | DROP if |
|---|---|---|---|
| D2 | **S-M** (two drivers, one emitter seam, ~10 needles, raw-bit loops) | (1) The scratch formula mismatch silently drops the LLVM lane to its scalar chain: assert the route in `llvm tiles`. (2) The 256 KiB budget is a guess: sweep `anti_g` 1/2/4/8 × `anti_kc` 0/200 × `anti_nc` {default, 4 MB}. (3) Fewer, bigger tasks (50 vs 200) at mt8: modelled LPT 1.009 | Interleaved A/B, antisym mt8 licensed, blade-cpp, `anti_g=1` vs `anti_g=4`, medians over ≥11 rounds: drop if < 2% AND the serial llvm arm shows < 1% |
| D1-0 | **none** (knob exists) | mt8 noise (gram cells are often "indicative") | The sweep IS the experiment. If no G beats 4 at mt8 by > 2%, the L3-traffic hypothesis for gram is closed |
| D3b | **M** (28 macro instances in a C header shared by 4 drivers; I-cache) | Code size; the dispatch switch on the edge path; the model ignores store masking. Must stay bitwise: raw-bit loops cover p mod 8 and mr tails | Comoment3 serial blade-llvm, `tri_edge=0` vs `1`, interleaved: drop if < 1.5% (half the 4.0% model). Also check p=61 (model 9.9%) as the sensitivity control |
| D1 | **M** (barrier protocol; double buffer adds 4.3 MB L3 pressure) | A wrong barrier is a data race on `bp` (pack vs read); the protocol needs a TSan-style threaded raw-bit loop with tiny kcMax | **D1-i first:** instrument a scratch copy of `dsyrk_tri` with per-thread `omp_get_wtime` at region entry, pack end, compute end and barrier exit, at the gram shape. Drop D1 if (fork + barrier + idle) < 2% of tile wall time. Prediction from the counts: ~0.1-0.3% |
| D3a | -- | -- | Already dropped on the LPT count (1.000) |
| D4 | none for D | -- | -- |

## 6. File-overlap list

| file / function | D's use | other items |
|---|---|---|
| `src/cpp/blade_packed_gemm.hpp` `dantisym_tri`, `dsyrk_tri`, `dsym3_tri` | D2, D1, D3b param threading | None expected. **Flag B:** if B extends a checksum epilogue into tile **stores** (last-K-block store) it collides here and in `blade_tiles.h` store paths (`blade_tri_tile_full/_edge`, `blade_anti_store_row`). B's stated scope is pairkernel's generic producer, so coordinate before B touches tiles |
| `src/cpp/blade_tiles.h` `blade_tri_tile_edge`, `blade_tri_kloop` | D3b | Same B caveat (store epilogue) |
| `src/cpp/blade_llvm_tiles.c` | D2, D3b | None known |
| `src/EmitLlvm.fs` tile declares/calls :243, :4774, :4889-4900 | D2, D3b | **A** touches EmitLlvm's compact fold/consumer paths (different functions, same file: merge-order care) |
| `src/CodeGenLoopNest.fs` syrk dispatch :3020-3090, row-contraction :3097-3226 | D2, D3b, D1 | **B** touches `genLoopNestStreamed:1463` (epilogue). **C** touches Path B fold emission (:1517-1580 region). Different functions, same file |
| `src/CodeGenExpr.fs:3133-3190` gram node | D1, D3b call text | None |
| `src/CodeGenExprSupport.fs` `tileTune` and siblings :1971-2004 | all D knobs | None (C might touch fold-lane dispatch ~:2002-2063 nearby, a different function) |
| `src/CodeGenBinding.fs:genOrbitFoldCpp:3562` | **not touched by D** | A (ordered scatter consumer), B (S11) |
| `tests/LinAlgTests.fs` needles ~1820-1835, `tests/OptimizeTests.fs` ~1505-1532, `tests/LlvmTests.fs` ~1025-1069 | D needles | B/C may add needles in the same files (append-only, low conflict) |
| `CLAUDE.md` env table | `BLADE_TILE_TUNE` row | A, B, C and E each add knob rows: textual merge conflicts likely, trivial to resolve |

## 7. Recommended waves and A/B protocol

**Wave 1** (parallel-safe with A, B, C, E: only tile drivers and tile emission):

1. **D1-0**: mt8 `syrk_g` ∈ {2,4,6,8} sweep on gram, mt8 licensed and mt8 repro, blade-cpp, OLD
   binary = 4136f0b5 (no code change: the knob value is the only variable). Interleave G values
   within a session; non-power-of-two shape (1601 × 1001); medians over ≥11 rounds.
2. **D2 implement** with `anti_g`. A/B on the NEW binary:
   - **arms:** OLD 4136f0b5; NEW `anti_g=1` (must be timing-neutral and byte-identical to OLD:
     identical visit order); NEW `anti_g=2`; NEW `anti_g=4` (= auto at n=401);
   - **cells:** antisym {serial licensed blade-llvm + blade-cpp, mt8 licensed blade-cpp, mt8 repro
     blade-cpp};
   - **gates:** `row-tile`, `llvm tiles`, `linalg`, `optimize`, `cli` 100%.
   - Shape sensitivity off-harness: p=601 and 1201 at n=401 and 1001 (the gate flips to G=1 at
     n=1001).
3. **D1-i** instrument (scratch only; not a branch).

**Wave 2** (after wave-1 numbers):

- **D3b**: A/B `tri_edge=0` vs `1` on comoment3 serial licensed (both lanes), mt8 licensed and
  serial repro. Gram serial is the control (it should move ≤ 0.6%: its dead fraction is 0.6%). Raw
  bits as above.
- Set `anti_g` default from the wave-1 result (auto if it won, else 1) in the same commit that
  updates the CLAUDE.md row.

**Wave 3 / conditional:** D1 persistent team only if D1-i shows > 2% team/barrier/idle time. The
paired 6x4 antisym core only if D2's measurements put the antisym tile's spill or reload traffic on
top. D4 has no D work: it rides B's S11, with the per-row-slot determinism rule above.

Serial arms must stay byte-identical and timing-neutral in every wave. D1 does not touch the serial
path. D2 at `anti_g=1` and D3b at `tri_edge=0` reproduce OLD visit orders and kloops exactly.
