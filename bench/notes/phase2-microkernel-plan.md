# Phase 2 microkernel plan -- closing the expert-tier gap

Status: PLANNING DOCUMENT, draft 3 (after two Codex attack rounds; section 7 logs every
change and every rejection). Nothing here was built, compiled, run or
benchmarked. Two planners: Claude (Blade side, author) and Codex (attacker), conversation
recorded in section 7. Every number below is either (i) a session-1a median
(`bench/results/2026-10-18-LAPTOP-350H6LDB/session-042223/summary.md`), (ii) a pilot or
attribution measurement already recorded in `notes/llvm-acceptance-A.md` section 13 /
`notes/llvm-acceptance-B.md` section 6 / `src/microkernels/README.md`, or (iii) an
arithmetic estimate, labelled as such.

Caveat carried from the session summary: inference is SUPPRESSED (drift) for comoment3,
gram, pairkernel, recur and stencil; only antisym's block is clean. The medians are good
for prioritisation, not for causal speedup claims. Checksum shares come from reduced-size
attribution runs, not full size.

Machine: Ryzen 7 5800HS, Zen 3: AVX2 + FMA, 2 x 256-bit FMA pipes (8 double FMA lanes per
cycle), 2 separate FADD pipes, FMA latency 4, FADD latency 3, 2 x 256-bit loads per cycle
(3 loads per cycle when narrower), 16 ymm registers, 32 KB L1d, 512 KB L2, 16 MB L3.

---

## 0. One page

| kernel | today (ms, llvm / cpp / gf-x / ifx-x) | root cause (one line) | fix (section) | expected after |
|---|---|---|---|---|
| gram | 157 / 275 / 81.7 / 73.2 | one cell per inner loop: both operand rows reloaded per cell (2 loads/FMA); C++ also one ymm accumulator | packed-triangle syrk tile at the BLAS-gate-off branch (4.1) | ~85-95 both lanes |
| antisym | 74.6 / 109 / 31.0 / 29.4 | same as gram, two dots per cell | paired strict tile, P then Q per block (4.1b) | ~33-37; ~31-34 with 4.4b |
| comoment3 | 41.5 / 110 / 11.5 / 11.7 | per-cell triple dot recomputing a*b for every k (1 mul + 1 FMA + 3 loads per term) + a p^3 canonicalizing checksum pass (36% llvm / 70% cpp) | rank-3 KRS tile with the W = X(i)X(j) panel (4.2) + orbit-weighted pool fold (4.4b) | ~13-16 |
| pairkernel | 42.6 / 77.3 / 32.8 / 30.8 | kernel == expert; the separate n^2 logical checksum pass (21% llvm / 57% cpp; C++ read 4.9x costlier) | inline canonical read (4.4a) then orbit-weighted pool fold (4.4b) | ~32-34 both lanes |
| recur | 117 / 134 / 10.5 / 11.3 | 8 x 160 KB temporaries allocated+freed per step, each re-faulting ~30-39 pages (Windows heap decommit); plus a 48 MB zero-fill and a 48 MB result copy | exact-size retained-block cache (4.5a), then zero-fill elision + destination passing (4.5b), then pointwise-body fusion (4.5c) | ~20-30 -> ~15-25 -> ~11-13 |
| stencil | 13.5 / 16.3 / 14.5 / 11.6 | at parity; lap stored then re-read for the checksum (expert checksums the hot row); C++/LLVM gap is the INPUT generator | nothing microkernel-level (4.6) | unchanged |

Five highest payoff per effort (section 6 has the full, Codex-reviewed order):
1. exact-size retained-block cache, both runtimes (S; recur 117 -> ~20-30 ms);
2. inline canonical read + bind-once in the C++ lane (S; C++ comoment3 110 -> ~50, pairkernel 77 -> ~42, in EVERY regime incl. repro);
3. orbit-weighted pool fold for the logical checksum (M; comoment3 llvm -14 ms, pairkernel llvm -10 ms, both lanes);
4. native packed-triangle syrk tile, C++ then LLVM via a shared arithmetic core (M + M; gram 275 / 157 -> ~85-95);
5. paired strict tile for antisym and the KRS rank-3 tile for comoment3 (M and M-L on top of 4; antisym 74.6 -> ~34, comoment3 ~27 -> ~13-16).

All "expected after" figures are arithmetic forecasts from measured rates, to be tested by
the bench; none was measured in this round.

---

## 1. Root causes, per kernel, with the deciding emission site

### 1.1 gram (G1, G26)

Mechanism. The nest is the comm triangle `i0 < p`, `i1 < p - i0`, and the cell body is the
kernel `prodsum(a, b)`: ONE output cell per inner loop, the sample loop innermost. Both
operand rows are re-read for every cell: 2 loads per FMA, 16 bytes per FMA lane. The expert's
8 x 6 tile does 2 vector loads + 6 broadcasts per 12 FMAs = 0.67 loads per FMA.

- LLVM lane: `materialize` (src/EmitLlvm.fs:4047-4061) picks `emitSimplexR` with
  `brickTileEdge true a.RowOpBytes n` (src/EmitLlvm.fs:2015): 64 x 64 CACHE bricks; inside,
  each cell calls `readCell` -> the kernel -> `emitProdSum` (src/EmitLlvm.fs:2613), one
  accumulator slot whose `fadd` carries `reassoc` under the licence, which the backend turns
  into 4 ymm accumulators. Load-bound at 1 ymm FMA per cycle = half the FMA peak. Measured
  8.4 G FMA/s vs experts 16.3-18.2.
- C++ lane: `IRProdSum` (src/CodeGenExpr.fs:322-435) chooses `omp simd reduction` at
  :412-420; g++ honours it with ONE ymm accumulator, `vmulpd` + `vaddpd` unfused, so the
  loop-carried 3-cycle `vaddpd` caps it at 1.33 lanes/cycle (4.4 G FMA/s measured). The
  triangle nest itself comes from `genLoopNestStreamed` (src/CodeGenLoopNest.fs:1251) with
  the `[ivdep] declined` line at :1580.
- KEY: the C++ lane already RECOGNISES this exact nest as a syrk. `(|BlasL3|_|)`
  (src/LinAlgPatterns.fs:1461) matches "two-level materialising comm nest over one array,
  kernel `prodsum(ri, rj)` optionally scaled", and `tryGenSyrkDispatch`
  (src/CodeGenLoopNest.fs:2546) only falls back to the nest because `shimEntryPoint` returns
  None with the BLAS gate off (plan line `[blas-routing v1] __v27: declined -- BLAS gate off
  ... [Syrk (L3, d)]`). And the repo already ships a bitwise 6 x 8 vector-extension GEMM tile
  (`src/cpp/blade_packed_gemm.hpp`, gated by `packedGemmWorth`,
  src/CodeGenExprSupport.fs:1755; called from src/CodeGenExpr.fs:3498-3514). The gram fix is
  therefore an arm, not an invention.
- Checksum: 1.4% (llvm) / 6% (cpp) of time at attribution size. Not the problem.

### 1.2 antisym (G1)

Mechanism: gram's, with two dots per cell (`prodsum(a(0), b(1)) - prodsum(b(0), a(1))` over
the stacked `Z(p, 2, n)`), strict triangle. LLVM fuses the two dots into one loop with 8 ymm
accumulators (still 2 loads per FMA, 7.7 G FMA/s); C++ runs two one-accumulator simd
reductions (5.0 G FMA/s). Not matched by `(|BlasL3|_|)` (strict, component peels, two
terms). Emission sites as gram. Checksum 3.5% / 7%.

Identity worth recording: `Q(i, j) = sum_s X(j,s) Y(i,s) = P(j, i)` where `P = X Y^T`, so
antisym is a dense GEMM plus `L = P - P^T` on the strict triangle (p^2 vs p(p-1) dots, i.e.
only 1/(p-1) = 0.08% extra work; the cost is an 11.5 MB dense intermediate and a
transpose-read pass). Used as the large-n fallback in 4.1b.

### 1.3 comoment3 (G1, G1b, G3a, G3b)

Kernel: per canonical cell a triple dot; per term `fmul(a, b)`, `fmul(., c)`, `fadd` (LLVM,
src/EmitLlvm.fs:2640-2656; contracted to `round(a*b)` + one FMA) = 1 multiply + 1 FMA and 3
loads per term. The expert forms `W(i, s) = X(i,s) X(j,s)` once per (i panel, j) and runs the
gram tile over k: 1 FMA and 0.67 loads per term. Vector-op ratio 1.97x, load ratio 4.5x.
The rank-3 simplex is never bricked (src/EmitLlvm.fs:4057, `if r = 2`).

Checksum: 171^3 = 5.0 M logical reads through the canonicalizing accessor = 36% (llvm) /
70% (cpp) at attribution size. In the C++ lane each read is the IIFE built at
src/CodeGenExpr.fs:803-890 (`lazyCompactRead`): `canon_fold<3>` (O(R^2) inversion count +
out-of-line `std::sort` -> `__insertion_sort` call) + `canon_left_justify<3>` + a
three-level nested-pointer load (runtime src/cpp/nested_array_utilities.hpp:707, :724).
AND the IIFE is emitted TWICE per cell for `t(i,j,k) * t(i,j,k)` (generated
comoment3.cpp:215 and :232; the opaque sort defeats CSE), so C++ pays 2 x p^3
canonicalizations. LLVM inlines a compare-and-swap: 4.9x cheaper per read.

Full-size split estimate for llvm (acceptance-A section 13): kernel ~25 ms + checksum
~14-17 ms.

### 1.4 pairkernel (G3a, G3b)

The kernel (scalar UCRT `exp` per cell, inline `vsqrtsd`) already runs at the expert's
per-cell cost (8.9-9.0 ns NOCS vs the expert's 9.8 ns total). The gap is the separate
6.27 M-read logical checksum over the 25 MB packed K: 21% (llvm) / 57% (cpp). C++ read cost
as comoment3 (rank-2 canon_fold + duplicated IIFE). Register tiling is not the target.
(Codex correction, accepted: ifx links its own static `exp`, so "the same exp in every arm"
holds for Blade and gfortran only; and "the whole 1.36x is the checksum" is an extrapolation
from a reduced probe.)

### 1.5 recur (G6, G8)

Per time step both lanes allocate 8 temporaries of 160,088 B (4 `rhs` results, 3 stage
inputs, the step result) and free them (recur.cpp:330-460 `step`; recur.ll `@bl_step_58`,
21 `blade_alloc_cells`/`blade_free` sites). The UCRT/NT heap decommits a freed block of
that size, so each allocation demand-faults its pages again: alloc probe 9.7 ms / 7308
faults vs 1.56 ms / 300 faults with buffers allocated once (29.5 faults per allocation).
Pool reuse declines in `step` (src/Optimize.fs:422 `planPoolReuse`: "a CALL declines it";
peak live 4 pools). The stage loops themselves are clean vectorized AVX2.

Two further passes both lanes pay (not in the gap register; confirmed by Codex):
- the let-rec desugar ZERO-FILLS the whole 301 x 20011 trajectory (48 MB) up front
  (src/TypeCheckInfer.fs:13840 `zerosValue`; recur.cpp `kernel()` first nest; recur.ll
  lines 68-95), although every row is written before it is read;
- each step's result is COPIED into trajectory row n and then freed (recur.cpp:474-479).
Relative to writing each result straight into its row, ~96 MB of stores (zero fill + the
separate result buffers) and ~48 MB of reads are avoidable (logical bytes, not measured
DRAM traffic).

Codex correction, accepted: "91k faults x 1.3 us = 118 ms" is an extrapolation; the
isolated probe gives 29.5 faults per allocation, and the 1.3 us folds in pass and
arithmetic changes. The DIRECTION is not in doubt (r_k block kernel: 2.5 ns/cell-step).

### 1.6 stencil

At parity (LLVM 13.5 vs gfortran-expert 14.5, ifx 11.6). The C++-vs-LLVM difference is in
the input-only twins (9.11 vs 6.26 ms) -- the input generator, not the kernel; incremental
medians are equal (7.25 / 7.23 ms). Blade stores the whole 24 MB `lap` and re-reads it for
the row-prodsum checksum; the expert checksums each row right after producing it
(incremental 4.84 / 5.70 ms). No new arithmetic microkernel is indicated.

---

## 2. Codex's independent diagnosis (step 1, before seeing mine)

Recorded verbatim in substance (its full text: conversation turn 2):
- antisym/gram: no cross-cell register reuse; C++ single-vector-accumulator mul/add;
  "do not keep two independent 8x6 tiles live simultaneously: 24 accumulators > 16 ymm";
  jamR (src/CodeGenLoopNest.fs:2401) is the row-fold microkernel, not this one.
- comoment3: hoist + tile; C++ rank-3 accessor; orbit weights 1/3/6 and "multiplying by
  multiplicity changes floating-point evaluation relative to repeated logical additions".
- gram: "64 rows fit exactly in L2" is approximate (512,512 B vs 524,288 B); "eight scalar
  K-lanes do not guarantee eight -- or even four -- independent YMM accumulators".
- pairkernel: checksum + C++ accessor; ifx's own exp; extrapolation caveat.
- recur: allocation churn; "a bounded allocator cache ... does not remove the extra memory
  passes"; the fault-cost arithmetic is an extrapolation.
- stencil: the lane gap is in the input-only twins; row-local producer/checksum fusion is
  the only opportunity; "stencil is a C++ vectorization failure" is unsupported.

Reconciliation (step 2): no disagreement on mechanism for any kernel. Codex added the
arithmetic-contract warnings now in section 3; I added the Syrk-recogniser fact, the
duplicated canonical read, and the two recur passes; Codex confirmed all three.

---

## 3. The arithmetic contract (what each draft may and may not change)

"Bitwise" always means bitwise AGAINST A NAMED REFERENCE, under a named regime and
contraction mode (README correction 20: identity is a joint property of text, compiler,
element type and tail). Two strengths of claim are used below and must not be conflated:

- PROVEN: under `BLADE_FP_CONTRACT=off` no operation can be fused, so a draft that keeps
  every cell's operation sequence (same products in the same order, same chain order, same
  final ops) is bitwise R_interp by construction. This is the identity the differential
  gates check and the first acceptance gate for every tile.
- EXPECTED + VERIFIED: under `-ffp-contract=fast` (the user default) the compiler is
  PERMITTED, not obliged, to fuse `c + a*b`; g++ and clang may decide differently per site
  (Codex, round 1, #5). "Same flag" is therefore permission, not proof. Each tile is
  verified per compiler by (a) disassembly (count `vfmadd` in the tile and in the serial
  reference: both all-fused or both unfused) and (b) raw-bit helper tests against a matched
  serial reference compiled in the same TU and mode (section 6, step 0).
- NaN payloads are outside the contract (x86 propagates the FIRST operand's payload; the
  printers print "nan" with no payload); nevertheless every draft keeps the SOURCE operand
  order of each product (`a[t] * b[t]` stays `a * b`), so even payloads match where the
  reference is sequential.

Three references exist:

- R_serial: the `BLADE_FP_REPRO=1` emission -- per cell one chain seeded with +0.0, terms
  added in ascending s, the product chain in source factor order; contracted or not per
  `BLADE_FP_CONTRACT` (C++ `-ffp-contract`, LLVM per-instruction `contract`).
- R_interp: the interpreter = R_serial with `BLADE_FP_CONTRACT=off`. The differential gates
  (`test interp`, `diff-oracle`, `opt-diff`) pin exactly this.
- R_licensed: today's default emission (4-lane LLVM reassociation; C++ simd reduction).
  Its bits are NOT a contract: the licence allows any association, and no draft promises
  to reproduce them.

| draft | association change? | equals | legal in default | legal under BLADE_FP_REPRO=1 |
|---|---|---|---|---|
| 4.1 / 4.1b / 4.2 register tiles | none per cell (one ascending chain; K blocks store and reload the exact binary64 partial, which adds no rounding) | R_interp PROVEN under contract=off; R_serial EXPECTED+VERIFIED under fast | yes (and changes the default's bits vs R_licensed by ~ulps) | yes -- ON in repro, so `test interp` exercises it bitwise |
| 4.2 W panel (hoist of round(a*b)) | none: pure CSE of a rounded product, factor order kept | R_serial | yes | yes |
| 4.3 vector-accumulator prodsum | yes (4 vector chains, fixed combine) | nothing; deterministic per text | yes (licensed fold) | NO -- repro keeps the serial chain |
| 4.4a inline canonical read | none (addressing only) | today, every regime | yes | yes |
| 4.4b orbit-weighted pool fold | yes (cells regrouped by orbit) | nothing | yes, licensed folds only | NO -- repro keeps the logical traversal (with 4.4a) |
| 4.5a allocator cache | no FP effect | today | yes | yes |
| 4.5b zero-fill elision, destination passing | no FP effect | today | yes | yes |
| 4.5c pointwise-body fusion | none per cell; may change CONTRACTION across former statement boundaries under `-ffp-contract=fast` | R_interp under contract=off | yes | yes (repro does not pin contraction) |

Rules every tile obeys (the three invariants of the microkernels README plus one):
1. sample loop ascending and outside the tile, every cell one chain;
2. never fold a scale into an operand: `prodsum(...) / n` divides the finished sum (vector
   `v / bcast(n)` is correctly rounded per lane, identical to the scalar divide); never
   turn `/ n` into `* (1/n)`;
3. reproduce the reference's contraction: the tile body is GCC/Clang VECTOR EXTENSIONS
   (`c += a * b`), never `_mm256_fmadd_pd`, so `-ffp-contract` governs fusion as it does
   for the scalar `__ps += a[t] * b[t]` (permission, verified per compiler -- above); zero-
   pad lanes, never peel the s loop, so every s step uses the same instruction;
4. a padded lane computes but is never stored; a later K block reloads only real cells;
5. keep each product's source operand order (`a * b`, and for antisym's Q term `X_j * Y_i`
   = `b(0) * a(1)`: the VECTOR operand first, then the broadcast);
6. never form an out-of-range pointer (`C + base - i - 1` at i = 0 is UB even if never
   dereferenced): compute row bases, index by `j - i - S` only for validated lanes; all
   binomial offsets and `p*p*k` products in checked size_t arithmetic.

---

## 4. The drafts

All C++ is written in the style of `src/cpp/blade_packed_gemm.hpp` (vector extensions,
`Scratch`, `threaded` argument, `#else` scalar fallback for cl.exe). Layout facts used
throughout (confirmed against the emitted nests):

- dense input `X` (p x n, row-major, contracted axis contiguous): `pool_base(x.data)`;
- rank-2 packed, left-justified row-major: row i holds j = i+S .. p-1 contiguously,
  `rowbase_S(i) = i*(2p - i + 1)/2` (S = 0, SymIdx) or `i*(2p - i - 1)/2` (S = 1,
  AntisymIdx); cell (i, j) at `rowbase_S(i) + (j - i - S)`. The emitted nest's
  `total - oend*(oend-1)/2` is the same number. `C.data[i]` is that row's pointer;
- rank-3 packed (SymIdx<3>): cell (i <= j <= k) at `base3(i, j) + (k - j)` with
  `e0 = p + 2 - i; off0 = C(p+2,3) - e0(e0-1)(e0-2)/6; h = p + 1 - i; e1 = p + 1 - j;`
  `base3(i, j) = off0 + h(h-1)/2 - e1(e1-1)/2` -- the emitted nest's `__ooff___v29_1`.

So the contiguous output direction is the LARGER index (j for rank 2, k for rank 3). The
tiles therefore VECTORISE over the larger index (NR = 8 = two v4d) and BROADCAST over the
smaller (MR = 6 rows) -- the transpose of the expert's column-packed choice, and exactly the
orientation of the shipped `dgemm_nn` tile (A rows broadcast, B columns vector).

### 4.1 (a) Same-array row contraction under `comm`: packed-triangle syrk tile

New code in `src/cpp/blade_packed_gemm.hpp` (or a sibling `blade_packed_tri.hpp`
included from it), reusing `MR = 6`, `NR = 8`, `KC = 256`, `MC = 96`, `Scratch`, `v4d`:

```cpp
// Transposing pack: W rows of X (row-major, length k) into an s-major W-wide panel.
// dst[s * W + r] = X[r0 + r][pc + s]; rows past `rows` are zero (computed, never stored).
template<size_t W>
inline void pack_rows_t(const double* X, size_t ldx, size_t r0, size_t rows,
                        size_t pc, size_t kc, double* dst) {
    for (size_t r = 0; r < rows; r++) {
        const double* src = X + (r0 + r) * ldx + pc;          // streams one row
        for (size_t s = 0; s < kc; s++) dst[s * W + r] = src[s];
    }
    for (size_t r = rows; r < W; r++)
        for (size_t s = 0; s < kc; s++) dst[s * W + r] = 0.0;
}

inline size_t rowbase_lj(size_t p, size_t i, size_t S) {      // S = 0 SymIdx, 1 AntisymIdx
    return S ? i * (2 * p - i - 1) / 2 : i * (2 * p - i + 1) / 2;
}

/// One MR x NR tile of the packed triangle over one K block. Lane (r, q) is cell
/// (i0 + r, j0 + q), stored iff r < mr, q < nr and j0 + q >= i0 + r + S. `first` seeds
/// +0.0 (the serial prodsum's `__ps = 0`); later blocks reload the stored partial, so
/// every cell stays ONE ascending chain.
template<size_t S>
inline __attribute__((always_inline))
void tile_tri(size_t kc, const double* ap, const double* bp, double* C, size_t p,
              size_t i0, size_t j0, size_t mr, size_t nr, bool first) {
    v4d c[MR][2];
    // FULL: every lane real -- the tile lies strictly above the diagonal band.
    const bool full = (mr == MR && nr == NR && j0 >= i0 + (MR - 1) + S);
    double* row[MR];                                  // row[r] = cell (i0+r, i0+r+S): a VALID pointer
    for (size_t r = 0; r < MR; r++)
        row[r] = (r < mr) ? C + rowbase_lj(p, i0 + r, S) : nullptr;
    // cell (i0+r, j) = row[r][j - (i0+r) - S], formed only for validated lanes (rule 6)
    if (first) {
        for (size_t r = 0; r < MR; r++) { c[r][0] = (v4d){0, 0, 0, 0}; c[r][1] = c[r][0]; }
    } else if (full) {
        for (size_t r = 0; r < MR; r++) {
            const size_t o = j0 - (i0 + r) - S;       // >= 0 by `full`
            std::memcpy(&c[r][0], row[r] + o, 32);
            std::memcpy(&c[r][1], row[r] + o + 4, 32);
        }
    } else {
        double t[MR][NR] = {};
        for (size_t r = 0; r < mr; r++)
            for (size_t q = 0; q < nr; q++)
                if (j0 + q >= i0 + r + S) t[r][q] = row[r][j0 + q - (i0 + r) - S];
        for (size_t r = 0; r < MR; r++) { std::memcpy(&c[r][0], &t[r][0], 32); std::memcpy(&c[r][1], &t[r][4], 32); }
    }
    for (size_t s = 0; s < kc; s++) {                 // ascending s: the chain order
        v4d b0, b1;
        std::memcpy(&b0, bp + s * NR, 32);            // X(j0 .. j0+7, s)
        std::memcpy(&b1, bp + s * NR + 4, 32);
        for (size_t r = 0; r < MR; r++) {
            const double a = ap[s * MR + r];          // X(i0 + r, s), broadcast
            const v4d av = (v4d){a, a, a, a};
            c[r][0] += av * b0;                       // contraction follows -ffp-contract
            c[r][1] += av * b1;
        }
    }
    if (full) {
        for (size_t r = 0; r < MR; r++) {
            const size_t o = j0 - (i0 + r) - S;
            std::memcpy(row[r] + o, &c[r][0], 32);
            std::memcpy(row[r] + o + 4, &c[r][1], 32);
        }
    } else {
        double t[MR][NR];
        for (size_t r = 0; r < MR; r++) { std::memcpy(&t[r][0], &c[r][0], 32); std::memcpy(&t[r][4], &c[r][1], 32); }
        for (size_t r = 0; r < mr; r++)
            for (size_t q = 0; q < nr; q++)
                if (j0 + q >= i0 + r + S) row[r][j0 + q - (i0 + r) - S] = t[r][q];
    }
}
// NOTE (Codex #9): `c[MR][2]` with runtime-indexed loops relies on full unrolling to stay
// in registers -- dgemm_nn's identical tile does, at -O3. The acceptance gate is a
// disassembly check (no stack traffic inside the s loop) for the full AND masked tile,
// under BOTH contraction modes. If contract=off spills (12 acc + 2 B + 1 broadcast + a
// product temp = 16, two overlapped products = 17), drop to MR = 4 (8 accumulators) for
// that mode: the tile SHAPE never changes a cell's arithmetic, so the choice is free with
// respect to bitwise identity and can differ per contraction mode.

/// C(i, j) = sum_s X(i, s) X(j, s) for i + S <= j < p, into Blade's packed triangle `C`
/// (flat pool). X: p rows of length k, leading dimension ldx. Per cell bitwise the
/// BLADE_FP_REPRO=1 serial prodsum under the same -ffp-contract. Cells are split across
/// threads, sums never are: the result is the same for every team size.
template<size_t S>
inline void dsyrk_lj(size_t p, size_t k, const double* X, size_t ldx, double* C, bool threaded) {
    if (p <= S) return;
    if (k == 0) { const size_t cells = S ? p * (p - 1) / 2 : p * (p + 1) / 2;
                  for (size_t c = 0; c < cells; c++) C[c] = 0.0; return; }
    static thread_local Scratch bscratch;
    size_t T = 1;
#ifdef _OPENMP
    if (threaded && !omp_in_parallel())
        T = std::max<size_t>(1, std::min<size_t>((size_t)omp_get_max_threads(), (p * p * k / 2) >> 20));
#else
    (void)threaded;
#endif
    for (size_t jc = 0; jc < p; jc += NC) {                    // column block: B panels in L3
        const size_t nc = std::min(NC, p - jc);
        const size_t npad = (nc + NR - 1) / NR * NR;
        const size_t iTop = std::min(p, jc + nc);              // rows i < iTop meet this block
        for (size_t pc = 0; pc < k; pc += KC) {                // K block: micro-panels in L1
            const size_t kc = std::min(KC, k - pc);
            const bool first = (pc == 0);
            double* bp = bscratch.get(kc * npad);
#ifdef _OPENMP
#pragma omp parallel num_threads((int)T) if (T > 1)
#endif
            {
#ifdef _OPENMP
#pragma omp for schedule(static)
#endif
                for (size_t jr = 0; jr < nc; jr += NR)
                    pack_rows_t<NR>(X, ldx, jc + jr, std::min(NR, nc - jr), pc, kc, bp + jr * kc);
                static thread_local Scratch ascratch;          // private per executing thread
                double* ap = ascratch.get(MC * kc);            // MC is a multiple of MR: ceil(mc/MR)*MR*kc <= MC*kc
#ifdef _OPENMP
#pragma omp for schedule(dynamic, 1)
#endif
                for (size_t ic = 0; ic < iTop; ic += MC) {             // row block: A panels in L2
                    const size_t mc = std::min(MC, iTop - ic);
                    for (size_t ir = 0; ir < mc; ir += MR)
                        pack_rows_t<MR>(X, ldx, ic + ir, std::min(MR, mc - ir), pc, kc, ap + ir * kc);
                    // first column panel that reaches row ic's diagonal
                    const size_t jlo = (ic + S > jc) ? (ic + S - jc) / NR * NR : 0;
                    for (size_t jr = jlo; jr < nc; jr += NR) {
                        const size_t j0 = jc + jr, nr = std::min(NR, nc - jr);
                        for (size_t ir = 0; ir < mc; ir += MR) {
                            const size_t i0 = ic + ir;
                            if (i0 + S > j0 + nr - 1) break;           // wholly below the diagonal
                            tile_tri<S>(kc, ap + ir * kc, bp + jr * kc, C, p, i0, j0,
                                        std::min(MR, mc - ir), nr, first);
                        }
                    }
                }
            }
        }
    }
}
```

Emission (C++ lane), in `tryGenSyrkDispatch` (src/CodeGenLoopNest.fs:2546): today the
`entryOpt = None` arm returns None. New: when the pattern matched, the gate is off, the
extents are literal and `packedTriWorth p n` holds, emit

```cpp
/* comm nest: packed-triangle syrk tile (blade_packed_gemm.hpp dsyrk_lj<0>, 6x8), per cell the serial prodsum */ blade_pgemm::dsyrk_lj<0>((size_t)(1601), (size_t)(1001), nested_array_utilities::pool_base(__v21.data), (size_t)(1001), nested_array_utilities::pool_base(__v27.data), false);
```

followed by the existing `scaleLine` tail unchanged (it already post-scales the packed
triangle, "one operation for one operation"), set `packedGemmUsedCell`, and record
`[microkernel v1] __v27: applied [packed syrk tile (dsyrk_lj, 6x8); P=1601 K=1001]`.
`packedTriWorth` (new, beside `packedGemmWorth` in src/CodeGenExprSupport.fs:1755) starts
as `p >= 24 && k >= 16 && p*(p+1)/2*k >= 262144` and is calibrated by the bench smoke and a
p sweep (the shipped triangular jam wins at small p: microkernels README, syrk_orientation
"loses to the jam at n=61"). Non-literal extents keep the nest.

LLVM lane. Recommendation (revised after Codex #8): ONE ARITHMETIC CORE, two drivers. A
C99 header `src/cpp/blade_tiles.h` holds only the pure parts -- `pack_rows_t` (W as an
argument), `tile_tri`, `tile_anti`, `tile_sym3` -- as `static inline` functions over
caller-provided buffers, vector extensions, no allocation, no threads, no TLS. The C++
driver (`dsyrk_lj` above: `Scratch`, OpenMP team, `threaded`) stays in
`blade_packed_gemm.hpp` and includes it. The LLVM driver lives in a NEW translation unit
`blade_llvm_tiles.c`: serial (the lane has no threads and `llvmOptFlags()` enables no
OpenMP), one grow-only static scratch per pack kind released by an `atexit` hook, ABI
`void blade_tri_syrk_d(int64_t p, int64_t k, const double* X, int64_t ldx, double* C, int64_t S)`.
That TU is compiled with `llvmOptFlags()` (`-O3 -march=native`) plus the build's
`-ffp-contract=` value -- NOT with the shim's `-c -O2` (src/Build.fs:1216, no `-march`:
SSE2) -- and the existing shim TU keeps its flags so its printers and `blade_fpowi` are
untouched. Cache identity of the object: the TU and `blade_tiles.h` contents, clang
identity (`clangStamp`), the resolved target (`clangNativeTargetIdentity`), the contraction
value and any sanitizer flag -- not the flag text alone. Same source compiled by g++ and
clang is NOT bitwise by construction (Codex #5): the two lanes agree PROVABLY under
contract=off and are verified under fast. EmitLlvm side: the outer-product builder
(src/EmitLlvm.fs:4330-4410) already has the same `LoopNestCodeGen` the C++ matcher reads, so
it runs the SAME active pattern; on a match it attaches `Contract = Some plan` to the
`ArrVal` (a new field, like `RowOpBytes`, so `readCell` keeps the per-cell closure for any
non-materialising consumer), and `materialize` (src/EmitLlvm.fs:4047) tests it first:
`allocPool` + `call void @blade_tri_syrk_d(i64 p, i64 k, ptr X, i64 ldx, ptr C, i64 S)` +
the scale pass. The attached plan is valid only while the deferred value still denotes
that exact contraction (Codex #7): any later map, view, slice or re-binding that rebuilds
the `ArrVal` clears `Contract`, and `materialize` resolves the operand's LIVE pool pointer
and leading dimension at that point. Why not emit the tile as IR: it is ~150 lines of
vector IR plus a driver per shape class -- a second copy of the arithmetic contract to keep
in step with the C++ one; the shared core keeps one copy. Alternative kept open: IR
emission with `<4 x double>` and `fmfFor` flags, if the lane must stay free of compute in C.

Expected payoff (arithmetic). Today: 2 loads per FMA; llvm 8.4 G FMA/s, cpp 4.4. Tile:
0.67 loads per FMA, 12 independent chains (>= 8 = latency 4 x 2 pipes), FMA-bound. The
shipped `dgemm_nn` reaches 0.17-0.18 cyc/MAC (~67% of FMA peak) at 1003^3 and krs_former
72.6% -- expect 15-18 G FMA/s -> 1.284e9 / 16.5e9 = ~78 ms kernel + packing (X packed
twice per K block, at both widths: ~26 MB read + ~26 MB written, ~3-4 ms; more when
p > NC forces A repacking per column block) + partial-output reload/store per K block
(4 blocks x 10 MB pool, ~2 ms) + checksum (~2 ms llvm) = ~85-90 ms in BOTH lanes (vs
157 / 275; expert 81.7 / 73.2, which pays the same packing). Ratio vs gfortran-expert:
0.52 -> ~0.9-0.95. All forecast ratios are hypotheses for the bench to test.

Registers: 12 accumulators + 2 B vectors + 1 broadcast = 15 of 16 ymm (identical to
dgemm_nn's measured tile). Cache: B micro-panel 8 x 256 x 8 B = 16 KB + A micro-panel
6 x 256 x 8 B = 12 KB in L1; A block 96 x 256 x 8 B = 192 KB in L2; B panel for NC = 2048
columns x 256 = 4 MB in L3 (p = 1601: 3.3 MB). Packing: B packed once per (jc, pc), A once
per (jc, pc, ic) -- i.e. once per pc when p <= NC.

Triangle edges. Tiles wholly below the band are skipped (`break`: rows only grow); tiles
straddling the diagonal (j0 < i0 + 5 + S) take the masked path -- at p = 1601 those are
~267 i-panels x 1-2 j-panels of ~51k tiles, ~0.5-1%; their below-diagonal lanes are computed
(exactly the mirrored value, since x*y == y*x and fma is symmetric in its factors) but
never stored. The last i-panel (p mod 6 = 5) and last j-panel (p mod 8 = 1) run masked.

Zen 3 pitfalls. (i) No shuffles: broadcasts come from the packed A panel, vectors from the
packed B panel (gram_jam_vec: "pack B j-major and the jam becomes broadcast-mul-add with
zero shuffles" -- the vpermpd/vunpck storm of the K-lane form cannot occur). (ii) Load
ports: 2 ymm + 6 scalar-broadcast loads per 12 FMAs = 4 cycles of load issue vs 6 cycles of
FMA: FMA-bound with margin. (iii) Unaligned stores to packed rows (natural pitch: at most
1/4 of rows 32 B-aligned) -- the align_probe measured 1.11-1.21x for padded rows; not
taken here (layout change), noted. (iv) Under `-ffp-contract=off` the body is `vmulpd` +
`vaddpd`: Zen 3 has separate FMUL and FADD pipes, so the lane throughput holds (correction
16 measured mul+add faster than FMA for the jam), but it needs one temp register per
in-flight product -- the compiler must reuse it; check for spills in the off build.

### 4.1b Antisym: the paired strict tile

Two sequential tiles per block, the expert's scheme, no simultaneous 24-accumulator state
(Codex's objection, accepted). Operands from `Z(p, 2, n)` (row-major, component 0 = X,
1 = Y): `X = pool_base(z.data)`, `Y = X + n`, both with leading dimension `ldx = 2n`
(Codex #6). Four DISJOINT packs (one `Scratch` each -- repeated `get()` on one Scratch
does not give independent buffers): `axp/ayp` = X / Y rows i, 6-wide broadcast panels;
`bxp/byp` = X / Y rows j, 8-wide vector panels.

```cpp
/// L(i, j) = P - Q, P = sum_s X(i,s) Y(j,s) (= prodsum(a(0), b(1)) : product a(0)*b(1) = X_i * Y_j),
///                  Q = sum_s X(j,s) Y(i,s) (= prodsum(b(0), a(1)) : product b(0)*a(1) = X_j * Y_i), i < j.
/// P and Q are each ONE ascending chain through every K block; the subtraction happens
/// ONCE per cell after both chains finish (a blockwise difference would re-associate).
inline void tile_anti(size_t kc, const double* axp, const double* byp,
                      const double* ayp, const double* bxp,
                      double* Pp /* 48 partials or null */, double* Qp /* 48 partials or null */,
                      bool first, bool last, double* L, size_t p,
                      size_t i0, size_t j0, size_t mr, size_t nr) {
    v4d c[MR][2];
    // P pass: seed 0 (first) or the stored P partial; c[r][h] += bcast(X_i) * Y_j   (source order X_i * Y_j)
    // -> spill to Pp (48 doubles; L1)
    // Q pass: seed 0 or the stored Q partial;     c[r][h] += X_j * bcast(Y_i)   (source order X_j * Y_i: VECTOR first)
    // last block: for each real lane (j0 + q >= i0 + r + 1):
    //     L[rowbase_lj(p, i0 + r, 1) + (j0 + q - i0 - r - 1)] = Pp[r][q] - Q[r][q];
    // otherwise: spill Q to Qp.
}
```

Driver and K blocking (revised after Codex #6, which rejected my "Q scratch = GEMM memory"
dismissal: a strict-packed Q scratch is HALF the dense GEMM intermediate). One structure for
every n:
- `n <= KC_ANTI` (a tuning threshold, initially 512; NOT a cache-residency proof -- one
  panel pair is 56 KiB at n = 512): a single K block; P and Q live in registers plus one
  48-double stack spill per tile; no scratch at all. The bench (n = 401) is here.
- `n > KC_ANTI`: K blocks OUTER (so a B-panel slice is reused across many tiles while hot);
  P partials in the output pool itself, Q partials in a strict-packed scratch `Qs` of the
  pool's size (5.8 MB at p = 1201), subtract fused into the last block's store. Strict-legal
  (Codex round 2 #4): on the FINAL block no later block reads that cell's P, so overwriting
  it with P - Q is safe. Invariants: output, inputs and Qs are disjoint; each cell is owned
  by exactly one tile; a tile finishes its P pass before its Q store; nothing reads the
  output before materialisation completes. The tile's 48 partials are GATHERED from the
  packed rows into the rectangular register/stack layout and SCATTERED back through the
  same lane mask as `tile_tri` (a packed pool is never passed as a rectangular tile). Test:
  a multi-K-block antisym shape whose every canonical cell is checked, which catches an
  accidental reload of a finalised P - Q. n = 0: every cell is `+0.0 - +0.0`.
- Measured alternatives, not defaults: (i) K blocks INSIDE each tile (two 48-double stack
  partials, no scratch; panels stream from L2/L3 instead of L1); (ii) the GEMM identity of
  1.2 -- dense `P = X Y^T` through a `dgemm_nt` variant (B read by `pack_rows_t<NR>`),
  then `L(i, j) = P(i, j) - P(j, i)` in 8 x 8 transposed blocks: bitwise the same two chains
  and one subtraction only if the GEMM's per-cell chain matches (it does: same tile), at
  2x the scratch of (Qs).

Emission: a NEW active pattern, because `(|BlasL3|_|)` is deliberately exact. Proposed
`(|RowContraction|_|)` in src/LinAlgPatterns.fs beside `(|BlasL3|_|)`. It inherits EVERY
structural check `(|BlasL3|_|)` makes (src/LinAlgPatterns.fs:1463-1528: no FoldWrapper /
FoldChunk, `nestModeOk`, one physical dense input array of the agreed precision, levels over
the same extent, the exact compact output group, peels identified by `ArrayPosition` and
`ParamVarId`) and matches the kernel as ORDERED ARITHMETIC, not as a normalised signed sum
(Codex #7): exactly one of
- `prodsum(p0, p1)` (gram), optionally `ProdSumScaled`;
- `IRBinOp(IRSub, prodsum(p0.c, p1.d), prodsum(p1.c, p0.d))` with literal components
  (antisym) -- the subtraction, its operand order and each prodsum's operand order are kept
  verbatim; `P + (-Q)` or a reordered pair is a different match (declines);
- `prodsum(p0, p1, p2)` scaled `/ d` (comoment3; 4.2's W precondition on operand order).
Returns `{ Rank; Strict; Shape (Gram | AntiPair | Sym3); Components; Scale }`; any scale
keeps its original position and is rendered through the emitter's own name map (as
`tryGenSyrkDispatch` already does). Anything else declines to the nest. The C++ dispatcher gains `tryGenRowContractionTile`
after `tryGenSyrkDispatch` in `tryGenLinAlgNest` (src/CodeGenLoopNest.fs:2638); LLVM as
4.1. In mt8, `omp(a: 1)` becomes the helper's `threaded` argument (the nest's own pragma is
not emitted); dynamic schedule over row blocks; cells, never sums, are split.

Expected: 5.78e8 FMAs at ~16-17 G/s = ~35 ms + packs (~1 ms) + checksum (llvm ~2.6 ms) ->
~35-38 ms (from 74.6 / 109; experts 31.0 / 29.4); with 4.4b's weight-2 strict pool fold the
checksum drops to ~0.5 ms -> ~33-35 ms.

### 4.2 (b) comoment3: the W panel and the rank-3 tile (KRS)

```cpp
/// T(i, j, k) = (sum_s W_ij(s) X(k, s)) / d, i <= j <= k, Blade's left-justified simplex,
/// W_ij(s) = X(i, s) * X(j, s) ROUNDED -- the serial prodsum's first product, factor order
/// (a, b) = the two OUTER levels, so the per-term arithmetic is unchanged:
/// R_serial computes fma_or_muladd(round(a*b), c, acc); so does the tile.
enum class Scale { None, Div, Mul };    // explicit, never "d == 0 means none" (Codex #2):
                                        // a real division by zero must still divide
inline void dsym3_lj(size_t p, size_t n, const double* X, size_t ldx, double* T, Scale sk, double d) {
    // k-panels: X packed ONCE, 8-wide, s-major: xk[q][s*8 + l] = X(8q + l, s), zero past p.
    // Size ceil(p/8)*8*n doubles (176*201 here = 283 KB -> L2), NOT p*n (Codex #2).
    // per i (outermost: the i sub-simplex of T stays hot):
    for i in [0, p):
      for j0 = i; j0 < p; j0 += MR:                          // MR = 6 prefixes (i, j0..j0+5)
        mr = min(MR, p - j0)
        for s in [0, n): for r in [0, MR): w[s*MR + r] = r < mr ? X[i][s] * X[j0 + r][s] : 0.0;   // 6n multiplies, L1
        for q = j0 / NR; q * NR < p; q++:                    // k panels reaching k >= j0
          v4d c[MR][2] = 0
          for s in [0, n): b0,b1 = xk[q][s*8 ..]; for r: c[r][*] += bcast(w[s*MR + r]) * b;   // 12 FMAs / 8 loads
          for r < mr: j = j0 + r; for l: k = q*NR + l;
             if (k >= j && k < p) T[base3(p, i, j) + (k - j)] = apply(sk, c[r][l], d);   // c / d, c * d, or c
             // full-vector store + v4d divide when the whole 8-lane run is real
}
```

K blocking (n > ~512 or W/xk micro-panels leaving L1: w is 6 x n x 8 B, xk micro-panel
8 x n x 8 B; at n = 201 together 22.5 KB, L1): reload the UNSCALED partial from T for later
blocks and apply the scale exactly once after the LAST block (the 4.1 rule; the KRS
prototype's `C += acc` per t-block is NOT bitwise and must not be copied --
krs_former.c:283). Not needed at the bench shape. n = 0: every cell is `apply(sk, +0.0, d)`
(the serial prodsum's +0.0 seed, then whatever scale the source has), never "no scale".

Counts (full shape p = 171, n = 201): useful terms 1.705e8. Today per term 1 vector-lane
multiply + 1 FMA = 3.41e8 lane-ops and 3 loads; tile: 1 FMA per term (+ 2.96e6 W
multiplies, 1.7%) and 0.67 loads: 1.97x fewer ops, 4.5x fewer loads. Waste: lanes computed
for k < j (diagonal head of each prefix block), k >= p (tail) and the 6-row staircase. A
loose bound (Codex round 2 #11): per real prefix (i, j) at most 5 tail + 7 aligned head + 5
staircase = 17 excess lanes; 14,706 real prefixes plus padded rows (<= 171 x 5 x 16 lanes)
against 848,046 useful cells gives < 24% wasted lanes at p = 171 (my draft-1 "25-35%"
borrowed the p = 61 prototype figure; README correction 4 measured 45% there, and its
repack "fix" measured a LOSS, so accept the waste). Compute the exact count before quoting. krs_former measured 72.6% of
FMA peak at 256 x 1024 and 2.34x at 61 x 2003. Expect ~12-15 G useful terms/s -> kernel
~11-14 ms (today ~25 ms llvm). With 4.4b the checksum goes from ~14-17 ms to ~0.5 ms:
total ~13-16 ms (from 41.5 / 110; experts 11.5 / 11.7). Memory: W is a per-(i, j-block)
6 x n panel, never the whole 23.6 MB W (Codex).

Registers and pitfalls: as 4.1 (same 6 x 8 tile, 15 ymm). The divide: `v4d / v4d` with a
broadcast d is lane-wise correctly rounded = the scalar `sum / static_cast<double>(n)`.

Legality of the hoist: pure CSE of a rounded product; licence-free; ON in repro. Precondition
checked by the matcher: the FIRST TWO prodsum operands are the peels of the two OUTER levels
(in either order, since round(a*b) == round(b*a)); `prodsum(c, a, b)` with c the inner level
declines (its first product is round(c*a), not reusable over k).

### 4.3 (c) The C++ lane's licensed prodsum: 4 vector accumulators

Replace the single-accumulator `omp simd reduction` arm at src/CodeGenExpr.fs:412-420 (for
`double`/`float` operands with a raw `.data` pointer) by an explicit vector-extension form.
Runtime helper (new `blade_fold.hpp`, or in `blade_packed_gemm.hpp`):

```cpp
// prodsum, licensed: FOUR independent v4d chains (16 lanes in flight), one fixed combine.
// The ASSOCIATION is a property of THIS TEXT (not of the vectorizer's choices, unlike
// `omp simd reduction`); under a fixed contraction policy the result is therefore fixed.
// Contraction itself still changes bits between `fast` and `off` (Codex #10).
inline __attribute__((always_inline))
double prodsum2_v(size_t n, const double* __restrict a, const double* __restrict b) {
    v4d s0 = {0,0,0,0}, s1 = s0, s2 = s0, s3 = s0, x, y;
    size_t t = 0;
    for (; t + 16 <= n; t += 16) {
        std::memcpy(&x, a + t,      32); std::memcpy(&y, b + t,      32); s0 += x * y;
        std::memcpy(&x, a + t + 4,  32); std::memcpy(&y, b + t + 4,  32); s1 += x * y;
        std::memcpy(&x, a + t + 8,  32); std::memcpy(&y, b + t + 8,  32); s2 += x * y;
        std::memcpy(&x, a + t + 12, 32); std::memcpy(&y, b + t + 12, 32); s3 += x * y;
    }
    for (; t + 4 <= n; t += 4) { std::memcpy(&x, a + t, 32); std::memcpy(&y, b + t, 32); s0 += x * y; }
    const v4d s = (s0 + s1) + (s2 + s3);
    double r = (s[0] + s[1]) + (s[2] + s[3]);
    for (; t < n; t++) r += a[t] * b[t];                 // scalar tail, ascending
    return r;
}
// prodsum3_v: the same with x * y * z (factor order kept: (x*y)*z).
```

Emission: `[&]() { return blade_fold::prodsum2_v((size_t)(1001), __v21____i0.data, __v21____i1.data); }()`
(the bound from `prodSumBound`), `recordFoldForm "" "vector-lanes(4x4)" "prodsum"`. Admitted
only when every operand is PROVEN contiguous unit-stride scalar storage (a plain
`Array<T, 1>` row view or pool, not a deferred, strided, ragged or gathered operand), the
operand expressions are bound once (the existing `__pa` alias rule), and the arity is 2 or
3. `float` gets its own helper (`v8f`, 8 lanes per vector, `float` accumulation as today)
-- the double helper is not reused. RaggedRow operands, complex and other element types
keep today's arms; cl.exe gets the simd form. Loop bounds are written overflow-safe
(`t <= n - 16` guarded by `n >= 16`).

Why 4 and not 8 on Zen 3: an unblocked dot is LOAD-bound at 2 loads per FMA -> at most 1
vector FMA per cycle; latency 4 x throughput 1 = 4 chains saturate it (8 is needed only when
FMA-bound at 2/cycle, i.e. inside a register tile). 4 chains + 2 operand vectors (+1 for a
third stream) = 6-8 ymm, no spill risk; the 16-wide main step leaves a <= 15-element tail
(n = 201: 12 x 16 + 9 -> 2 x 4-wide + 1 scalar). This is exactly what the LLVM lane already
does (4 ymm accumulators), which is the measured 1.4-1.9x lane gap on dot-class folds.

Caveats (Codex, accepted): README correction 1 found multi-accumulator worth ~0 for double
when operand supply binds (sample-major access, >= 128 KB); these rows are contiguous and
L1/L2-resident, and the measured C++ vs LLVM kernel-only gap (gram 8.67 vs 5.62 ms) is the
evidence that it binds here. After 4.1/4.2 the tiles REMOVE the prodsum IIFE from gram /
antisym / comoment3, so 4.3's remaining beneficiaries in the suite are dot, stencil's row
checksum, recur's checksum and the onepass/fusedchain folds. It must be re-measured against
the gemv fiber shape that made simd the site's form (src/CodeGenExpr.fs:386-408).

### 4.4 (d) The canonicalizing read and the canonical-cell fold

#### 4.4a Inline canonical read (C++ lane; every regime)

New runtime helpers in `src/cpp/nested_array_utilities.hpp` beside `canon_fold`:

```cpp
// Preconditions, checked ONCE where the read is emitted (the extent is literal): (n + 2)^3
// fits size_t, so every binomial numerator below is exact (Codex round 2 #5 -- the pool's
// existence does not prove the numerators do not overflow).
inline size_t sym3_off(size_t a, size_t b, size_t c, size_t n) {     // a <= b <= c < n
    const size_t e0 = n + 2 - a, h = n + 1 - a, e1 = n + 1 - b;
    const size_t total = n * (n + 1) * (n + 2) / 6;
    return total - e0 * (e0 - 1) * (e0 - 2) / 6 + h * (h - 1) / 2 - e1 * (e1 - 1) / 2 + (c - b);
}
// Rank 2: sorted pair + parity in one compare; flat pool offset; SIZE_MAX = strict diagonal.
template<bool STRICT>
inline size_t canon2_off(size_t i, size_t j, size_t n, int& parity) {
    parity = (i > j);
    const size_t lo = parity ? j : i, hi = parity ? i : j;          // cmov, no branch
    if constexpr (STRICT) {
        if (lo == hi) return SIZE_MAX;
        return lo * (2 * n - lo - 1) / 2 + (hi - lo - 1);
    } else {
        return lo * (2 * n - lo + 1) / 2 + (hi - lo);
    }
}
// Rank 3: 3-comparator sorting network (0,1)(1,2)(0,1); each swap flips parity (an adjacent
// swap of an out-of-order pair removes exactly one inversion, so swap parity == the
// inversion parity canon_fold counts; ties never swap, as canon_fold never counts them).
template<bool STRICT>
inline size_t canon3_off(size_t a, size_t b, size_t c, size_t n, int& parity) {
    int p = 0; size_t t;
    if (a > b) { t = a; a = b; b = t; p ^= 1; }
    if (b > c) { t = b; b = c; c = t; p ^= 1; }
    if (a > b) { t = a; a = b; b = t; p ^= 1; }
    parity = p;
    if constexpr (STRICT) {
        if (a == b || b == c) return SIZE_MAX;
        // a < b < c in [0, n)  <->  a <= b-1 <= c-2 in [0, n-2): the strict simplex IS the
        // inclusive one of extent n-2 (Codex #11), so reuse the inclusive formula.
        return sym3_off(a, b - 1, c - 2, n - 2);
    }
    return sym3_off(a, b, c, n);
}
```

Emission change in `lazyCompactRead` (src/CodeGenExpr.fs:803-890): when the index list is
ONE compact group of rank 2 or 3 (multi-group and wreath slots keep the generic path), the
extent is literal, AND the array is PROVEN to own one contiguous packed pool with the
`allocate<..., symm>` origin (a materialised compact binding -- a `MatPool` the emitter
allocated, or a parameter of that concrete type; not a sub-view, a decompact residual or a
provider-backed array; Codex #11), emit

```cpp
([&]() -> double { const size_t __c0 = (size_t)(i); const size_t __c1 = (size_t)(j); const size_t __c2 = (size_t)(k); int __p0; const size_t __o0 = nested_array_utilities::canon3_off<false>(__c0, __c1, __c2, 171, __p0); return nested_array_utilities::pool_base(t.data)[__o0]; }())
```

The coordinates are bound to locals FIRST, left to right, because the old brace-init list
fixed their evaluation order and function arguments do not (Codex #11). Strict classes test
`__o0 == SIZE_MAX` and return `double()` BEFORE touching `pool_base` (an empty strict pool
has no [0] spine); Hermitian / antisym keep `canon_transform` on `__p0`. `pool_base(t.data)`
is loop-invariant; with no opaque call left, g++ hoists it and CSEs the duplicated read of
`t(i,j,k) * t(i,j,k)` for free. Additionally bind a repeated pure read once at emission
(Codex round 0): a kernel body that reads the same compact cell twice, with no intervening
write to that array and pure coordinate expressions, emits one read into a local. Bitwise
in every regime: the same cell is read; the traversal and fold are untouched.

Payoff: the attribution measured the LLVM lane's inline compare-and-swap read 4.9x cheaper
than the C++ read (10.5 vs 2.15 ms of checksum at p = 91). Keeping each kernel's
non-checksum share and dividing its checksum share by 4.9: C++ comoment3 110 x (0.30 +
0.70 / 4.9) = ~49 ms and pairkernel 77 x (0.43 + 0.57 / 4.9) = ~42 ms before any tile or
4.4b (an extrapolation from reduced-size shares, not a full-size prediction); and it is the
ONLY checksum fix available under `BLADE_FP_REPRO=1`.

#### 4.4b The orbit-weighted pool fold (licensed; removes the logical pass)

Prior design: `docs/plans/plan-compact-sym-folds.md` section 4.3 RECOMMENDS (not yet
ratified: its P0 ratifies, P1 is the rank repair, P3/P4 the full-domain folds;
plan-compact-sym-folds.md:219, :486-490 -- Codex round 2 #6 corrected my draft-2 wording)
the FULL-DOMAIN meaning for folds over compact storage, implemented as a pool walk with
multiplicity, licence-gated ("licensed => pool walk with multiplicity; unlicensed => the
true logical order"). This draft needs NONE of those language changes: it optimises an
ALREADY-DEFINED logical traversal, the shape the bench (and every Blade user, since
`reduce` over a compact array is still refused, BL3999) actually writes:

```blade sketch
reduce(method_for(range<I>) <@> lambda(i) -> reduce(method_for(range<I>) <@> lambda(j) -> f(g(i, j)), (+)), (+))
```

Recogniser (shared, a new `LogicalSymFold` match in src/LinAlgPatterns.fs or its own small
module before CodeGen): r nested builtin-`(+)` licensed folds (`foldReorderLicensed` true at
every level) over `range<I>` with the SAME index type I as the compact group of `g`; the
innermost body is `f(g(v_1..v_r))` where (v_1..v_r) is a permutation of the r level
variables, `f` reads `g` only at that tuple and no level variable otherwise (so the body is a
function of the orbit's value), `g` is a stable (immutable, materialised) binding, and `f`
is drawn from a TYPED WHITELIST, never inferred (revised after Codex #3, a BLOCKER: complex
`x * x` is NOT conjugation-invariant -- (1+i)^2 = 2i, conj(1+i)^2 = -2i):

| class | admitted f | why invariant over the orbit | f at a strict zero |
|---|---|---|---|
| SymIdx, real | `x * x` (same read twice), `abs(x)` | every mirror reads the same value | -- |
| AntisymIdx, real | `x * x`, `abs(x)` | even: (-x)(-x) = x x, abs(-x) = abs(x), bitwise | `0 * 0 = +0`, `abs(0) = +0` exactly |
| Hermitian | `abs(x)` only | `abs(conj z) = abs(z)` bitwise (hypot of (a, -b) vs (a, b)) | -- |

Everything else declines (including `x * conj(x)`: its imaginary part `a(-b) + b a` is not
reliably 0 under contraction, and its real part's operand order differs between mirrors).
Seeds: the folds must be the bare builtin `(+)` (no seed: the first element seeds, empty
extent refuses BL8003 as today) or carry a literal `+0.0` seed at EVERY level; any other
seed declines (an explicit inner seed occurs p times in the logical sum and cannot vanish;
Codex #4). The admitted f are all >= +0 or NaN, so no -0 term exists and the sign-of-zero
question of a +0-seeded pool walk does not arise. Emptiness is judged on the LOGICAL domain
and the ORIGINAL seed semantics (Codex round 2 #3): p = 0 with +0.0 seeds returns the seed
(EmitLlvm.fs:4711-4718 separates seeded from unseeded); unseeded p = 0 keeps the BL8003
refusal; and p > 0 with p < r on a STRICT group (p = 1 at rank 2, p = 2 at rank 3) has an
EMPTY pool but a non-empty logical domain of zeros, whose admitted-f sum is +0 -- the pool
walk must answer +0 there, not refuse. Element types: real Float64 / Float32 arrays, and
Complex128 / Complex64 only under `abs`.

Emission (C++, at `genReduceComputeBinding`, src/CodeGenBinding.fs:3397; LLVM at
`emitReduce` / `emitCompactFold`, src/EmitLlvm.fs:4653 / :4786, which already walks the pool
canonically):

```cpp
/* logical fold over a SymIdx<2> pool, licensed: orbit classes, weights 1 (diagonal) / 2 (off) */
double __sd = 0.0, __so = 0.0;
for (size_t i = 0; i < 1601; i++) {
    const double* __r = __opool_g + rowbase_lj(1601, i, 0);
    __sd = __sd + f(__r[0]);                                   // class (i, i): weight 1
    BLADE_OMP_SIMD_REDUCTION(+:__so)
    for (size_t q = 1; q < 1601 - i; q++) __so = __so + f(__r[q]);   // class i < j: weight 2
}
const double ss = __sd + 2.0 * __so;
// SymIdx<3>: classes (i,i,i) w1, (i,i,k)+(i,k,k) w3, i<j<k w6:  ss = S1 + 3.0*S2 + 6.0*S3
// AntisymIdx<2>: ss = 2.0 * S_strict (the diagonal reads 0 and f(0) = 0)
```

Why the weights are an ASSOCIATION, not a new rounding (answering Codex's round-1
objection; Codex round 2 #4 agrees for 2/3/6 and the rank-4 weights): a class sum S is one
reduction subtree; the logical multiset contains w copies of that subtree's terms, and
regrouping them as w copies of S is an association. Then `2S = S + S` (doubling is exact
when representable, and overflows to the same infinity as S + S otherwise);
`round(3S) = round((S + S) + S)` because `S + S` is exact; `round(6S) = 2 round(3S)` because
scaling by 2 is exact (again with identical overflow). Hence `S1 + 3 S2 + 6 S3` is one
association of the logical multiset (the r nested `(+)` folds are all licensed, and assoc +
comm of one `+` admit any rearrangement of a multiset). All ranks <= 4 have weights of the
form 2^a 3^b (1, 4, 6, 12, 24 at rank 4); rank >= 5 introduces factor 5 (`round(5S) =
round(4S + S)` is still one), but decline above rank 4 until someone needs it.

Payoff: comoment3 llvm ~14-17 ms -> ~0.5 ms (848k contiguous cells); pairkernel llvm
-~10 ms (3.13 M cells streamed once instead of 6.27 M canonicalizing reads), C++ -~40 ms;
gram/antisym -1 to -3 ms. Not under repro (4.4a covers that).

Surface spelling (G2, the genuinely canonical sum): `reduce(pool(A), k)`, the plan's
preference (plan-compact-sym-folds 4.4), unchanged here; a decision for the language owner.
The logical sum needs NO new spelling: the user writes the logical sum and gets the pool
walk -- "the fastest way is the only way". Longer term, that plan's P3/P4 (full-domain
`reduce` over a compact array, lifting BL3999) should share THIS draft's internal
orbit-fold plan (classes, weights, seeds, emptiness) rather than re-derive it.

DEPENDENCY (correctness), stated precisely (Codex round 2 #6): G16 -- `R * R` of an
antisymmetric R is typed AND stored antisymmetric -- does NOT block the recogniser, which
keys on scalar reads `f(g(..))` of the SOURCE array and its class, never on the type of an
array product. It DOES block any claim that a future `reduce(l * l, (+), axes = 2)`
spelling is correct over antisymmetric `l` (that fold would come out 0). Negative
recogniser tests: an `f` outside the whitelist, a read of a DIFFERENT array, a level
variable used outside the read, an index type that is not the group's, a non-`(+)` level,
a non-zero seed -- each must fall back to the logical traversal.

### 4.5 (e) recur

#### 4.5a Exact-size retained-block cache in both allocators (the floor)

(Named for what it is -- an exact-size retained cache, not a size-class allocator; Codex #14.)

C++ lane: `allocate` / `allocate_strict` call `new SCALAR[total]` (nested_array_utilities.hpp
:239, :333) and `deallocate*` call `delete[]` (:399, :407, :453, :458). Route the POOL
(alloc #1) pair through a per-thread, per-type cache; the interior pointer rows (alloc #2)
are small and stay as they are. `release` must receive the ACTUAL allocation count
(`total > 0 ? total : 1`, the same expression `allocate` used).

Disable switch (fixed after Codex #12, a BLOCKER: `BLADE_MEMCHECK` is an ENVIRONMENT
setting, Build.fs:503/525/542 passes sanitizer flags but defines no macro, so
`#if defined(BLADE_MEMCHECK)` would never fire): Build.fs adds `-DBLADE_POOL_CACHE_OFF=1` to
every memcheck compile in BOTH lanes (the leak counter in blade_memcheck.hpp must see real
frees even where ASan is absent), and the header also self-disables on
`__SANITIZE_ADDRESS__` / `__has_feature(address_sanitizer)` (the blade_memcheck.hpp:39-48
pattern), because a cached free hides use-after-free from ASan.

```cpp
// blade_pool_cache.hpp -- included by nested_array_utilities.hpp.
// The NT/UCRT heap returns a freed block of this size's pages, so the next allocation of the
// same size demand-faults them again (bench G6: 29.5 faults per 160 KB allocation,
// 9.7 ms vs 1.56 ms for buffers kept). Keep a few freed pools committed and hand them back
// to an allocation of the SAME cell count. new[]/delete[] pairing is preserved per type.
#if defined(__SANITIZE_ADDRESS__)
#  define BLADE_POOL_CACHE_OFF 1
#elif defined(__has_feature)
#  if __has_feature(address_sanitizer)
#    define BLADE_POOL_CACHE_OFF 1
#  endif
#endif
namespace blade_pool {
#if defined(BLADE_POOL_CACHE_OFF) && BLADE_POOL_CACHE_OFF
template<class T> inline T* alloc(size_t cells) { return new T[cells]; }
template<class T> inline void release(T* p, size_t)  { delete[] p; }
#else
constexpr size_t kMinBytes   = 16384;            // hypothesis: ~the LFH eligibility boundary (Codex #14); B saw 3.2 faults/alloc at 16 KB -> measure 4/16/64 KB
constexpr size_t kMaxBlock   = size_t(8) << 20;   // a block larger than this is never retained (the 48 MB trajectory is not)
constexpr size_t kSlots      = 16;
constexpr size_t kMaxBytes   = size_t(32) << 20;  // per-thread budget -- ONE budget across all types (see below)
template<class T> struct Cache {
    T* p[kSlots]; size_t cells[kSlots]; size_t n = 0;
    ~Cache();                                       // frees its entries, decrements tl_retained
};
inline thread_local size_t tl_retained = 0;        // ONE per-thread budget across every Cache<T>
template<class T> inline Cache<T>& cache() { static thread_local Cache<T> c; return c; }
template<class T> inline T* alloc(size_t cells) {
    if constexpr (std::is_arithmetic<T>::value) {           // only types new[] leaves uninitialized
        if (cells * sizeof(T) >= kMinBytes) {
            Cache<T>& c = cache<T>();
            for (size_t k = c.n; k-- > 0; )                  // newest first: the hottest pages
                if (c.cells[k] == cells) {
                    T* r = c.p[k];
                    for (size_t m = k + 1; m < c.n; m++) { c.p[m - 1] = c.p[m]; c.cells[m - 1] = c.cells[m]; }
                    c.n--; tl_retained -= cells * sizeof(T);
                    return r;
                }
        }
    }
    return new T[cells];
}
template<class T> inline void release(T* p, size_t cells) {
    if constexpr (std::is_arithmetic<T>::value) {
        const size_t b = cells * sizeof(T);
        Cache<T>& c = cache<T>();
        if (p && b >= kMinBytes && b <= kMaxBlock) {
            // Make room by evicting THIS type's oldest entries only (Codex round 2 #1: a
            // shared counter must never drive eviction of an empty cache).
            while (c.n > 0 && (c.n == kSlots || tl_retained + b > kMaxBytes)) {
                const size_t ob = c.cells[0] * sizeof(T);
                delete[] c.p[0];
                for (size_t m = 1; m < c.n; m++) { c.p[m - 1] = c.p[m]; c.cells[m - 1] = c.cells[m]; }
                c.n--; tl_retained -= ob;
            }
            if (c.n < kSlots && tl_retained + b <= kMaxBytes) {   // still full (other types)? do not retain
                c.p[c.n] = p; c.cells[c.n] = cells; c.n++; tl_retained += b;
                return;
            }
        }
    }
    delete[] p;
}
// alloc's hit path decrements tl_retained; the bad_alloc flush and ~Cache() do too.
#endif
}
// allocate<>:   SCALAR* pool = blade_pool::alloc<SCALAR>(total > 0 ? total : 1);
// deallocate<>: blade_pool::release(pool, total > 0 ? total : 1);   (rank 1: `data`, count_leaves)
// Out of memory: alloc catches std::bad_alloc once, flushes this thread's cache, retries.
// Thread exit: the thread_local destructors free the cache; persistent OpenMP workers keep
// theirs until process exit (bounded by kMaxBytes each).
```

LLVM lane (src/cpp/blade_llvm_shim.c:186-238): `blade_free(void*)` has no size, so a
64-byte header carries it (alignment preserved: 64-aligned base + 64). Plain globals are
valid ONLY while the lane stays single-threaded (every emitted function is `nosync`; a
future thread runtime, G11, must switch them to `_Thread_local`). Compiled out under
`BLADE_POOL_CACHE_OFF` -- the C fragment below shows the enabled arm; the disabled arm is
today's code verbatim, selected by `#if` -- and Build.fs passes the define to the shim
compile for memcheck builds. BLOCKER found in round 2 (Codex #2): `buildLlvmShim`
(src/Build.fs:1213) reuses `blade_llvm_shim.o` by SOURCE TIMESTAMP alone, so a memcheck
build in a directory that already holds a normal build would silently link the
cache-enabled object. The shim object (and the new tiles object) must be keyed, or named,
by its effective flags, defines and compiler identity, and that identity must enter the
exe-cache key; acceptance test: normal build then memcheck build in the SAME output
directory, counting actual allocation/free events. The cache's blocks are released by an
`atexit` hook.

```c
#define BLADE_POOL_HDR 64
#define BLADE_CACHE_MIN ((size_t)16384)
#define BLADE_CACHE_SLOTS 16
#define BLADE_CACHE_MAXBLOCK ((size_t)8 << 20)
#define BLADE_CACHE_MAX ((size_t)32 << 20)
static void  *g_cache_p[BLADE_CACHE_SLOTS];      /* plain globals: this lane has no threads */
static size_t g_cache_b[BLADE_CACHE_SLOTS];
static int    g_cache_n;
static size_t g_cache_bytes;

static void *blade_alloc_aligned(size_t bytes) {
    size_t want = bytes ? bytes : (size_t)BLADE_POOL_ALIGN;
    size_t rounded = (want + (size_t)(BLADE_POOL_ALIGN - 1)) & ~(size_t)(BLADE_POOL_ALIGN - 1);
    if (rounded < want || rounded + BLADE_POOL_HDR < rounded) blade_panic("error[BL8006]: allocation size overflow");
    if (rounded >= BLADE_CACHE_MIN)
        for (int k = g_cache_n - 1; k >= 0; k--)
            if (g_cache_b[k] == rounded) {
                char *raw = (char *)g_cache_p[k];
                memmove(&g_cache_p[k], &g_cache_p[k + 1], (size_t)(g_cache_n - 1 - k) * sizeof(void *));
                memmove(&g_cache_b[k], &g_cache_b[k + 1], (size_t)(g_cache_n - 1 - k) * sizeof(size_t));
                g_cache_n--; g_cache_bytes -= rounded;
                return raw + BLADE_POOL_HDR;
            }
    char *raw = (char *)aligned_raw(rounded + BLADE_POOL_HDR);   /* the existing _aligned_malloc / aligned_alloc arms */
    if (!raw) blade_panic("error[BL8006]: out of memory");
    *(size_t *)raw = rounded;
    return raw + BLADE_POOL_HDR;
}

void blade_free(void *p) {
    if (!p) return;
    char *raw = (char *)p - BLADE_POOL_HDR;
    size_t b = *(size_t *)raw;
    if (b >= BLADE_CACHE_MIN && b <= BLADE_CACHE_MAXBLOCK) {
        while (g_cache_n == BLADE_CACHE_SLOTS || g_cache_bytes + b > BLADE_CACHE_MAX) {
            g_cache_bytes -= g_cache_b[0]; aligned_raw_free(g_cache_p[0]);
            memmove(&g_cache_p[0], &g_cache_p[1], (size_t)(g_cache_n - 1) * sizeof(void *));
            memmove(&g_cache_b[0], &g_cache_b[1], (size_t)(g_cache_n - 1) * sizeof(size_t));
            g_cache_n--;
        }
        g_cache_p[g_cache_n] = raw; g_cache_b[g_cache_n] = b; g_cache_n++; g_cache_bytes += b;
        return;
    }
    aligned_raw_free(raw);
}
```

Expected: recur 117 / 134 -> ~20-30 ms (the natural Fortran tier, which allocates its stage
arrays once, measures 16-20 ms; the probe's persistent-buffer arm is 6x faster than the
per-step arm). Also helps every per-iteration temporary in the repo (LSWOSA history,
plan-simplex 0c "most of the benchmark is the allocator"). Peak memory: retention is
bounded by kMaxBytes per thread (32 MB; 256 MB worst case at 8 threads -- the bench's
mt8 arms will show it in `peak WS`), not by "+1.3 MB" (Codex #13). Platform caveat: 16 KB
and exact-size matching are hypotheses, not Windows guarantees -- heap choice (NT vs
segment heap) depends on the executable manifest; measure faults, commit and cold/warm
behaviour at 4/16/64 KB thresholds before fixing the constants.

Rejected alternative: changing the heap's decommit behaviour globally
(`DeCommitFreeBlockThreshold` in the PE load-config, or opting into the segment heap by
manifest) -- platform-specific, invisible in the source, and it changes every allocation in
the process, including the C++ runtime's own.

#### 4.5b Zero-fill elision and destination passing (both lanes, via the desugar)

- Zero fill: the desugar at src/TypeCheckInfer.fs:13840 builds the buffer as `zerosValue`
  because reads past the built prefix are implicitly zero. Elide it when (definite-
  initialisation proof, Codex) the seed arm writes rows 0..s-1, the inductive arm writes
  row n for every n >= s, every prefix read in the arms is a row read `prefix(n - m)` with
  literal m >= 1 (reads below 0 are already guarded to zero by `guardWrap`, not by the
  buffer, src/TypeCheckInfer.fs:13970-13976), `prefix` has no whole-array use and no alias
  escapes, AND the recurrence has NO `while` guard: a guarded recurrence that stops before
  writing any row freezes from row 0 (src/TypeCheckInfer.fs:14085-14107), which is correct
  today only because row 0 is zero-filled (Codex #15) -- decline elision there, or
  initialise exactly the freeze source. The proof is over the LOWERED control flow (every
  seed / inductive / freeze path), not over the arm text. Then the buffer is a fresh
  UNINITIALISED pool (both lanes' allocators already return uninitialised storage). Saves a
  48 MB write pass (~3-5 ms at the ~12.5 GB/s plain-store rate of plan-simplex 0c). The page
  faults move to the first real write; they remain (the expert pays them too).
- Destination passing: `prefix :: step(prefix(n - 1))` allocates step's result, copies it
  into row n, frees it. When the callee's return value is a fresh pool of the row's static
  shape and the row is not read by the callee (it reads n-1 only), emit `step` with an
  out-parameter (a `__into` variant: the return pool is the caller's row view) so the final
  flat loop of `step` writes the trajectory directly. Saves 300 allocations, a 48 MB read
  and a 48 MB write. Preconditions (Codex #15): the destination is marked BORROWED, so no
  return or cleanup path in the callee frees it (it is an interior pointer of the
  trajectory pool); every return branch of the callee writes it; it aliases no argument
  (row n vs row n - 1), no capture and no caller alias, and does not escape.

#### 4.5c Pointwise-body fusion (G8; the doctrine fix)

`step` and `rhs` are POINTWISE: every let is an elementwise expression over arrays of the one
index space X (calls to pointwise functions included). Such a body IS a scalar kernel lifted
over X: fuse the whole DAG into one flat loop with scalar locals (each let computed once per
cell, so multi-consumer lets such as k1, which feeds both k2's stage and the final
combination, are fine), writing only the returned array (into the destination row under
4.5b). This is the r_k spelling the compiler should derive (1.5 ms vs 12.3 ms at the probe
size; 2.5 ns per cell-step). Site: the elementwise-chain fusion pass (`BLADE_FUSION`,
src/Optimize.fs, invoked at :1454) extended from single-consumer chains to a pointwise
function body, with `rhs` inlined; its gate stays in `optimizerGates`, so `blade test
opt-diff` compares fused vs unfused. "Pointwise" is a checked property (Codex #16): no
mutation, no stateful RNG (the `_at` indexed draws are pure and admissible), no observable
call, no cross-cell read (a halo or shifted read ends the region), no escaping
intermediate; DAG sharing is kept as scalar locals, never by substituting and recomputing
an expression. Fusion can raise the live vector state past 16 ymm: the pass stages the
region (split at a let) when its estimated live set exceeds the register file.

Which fix is right for "the fastest way is the only way"? 4.5c. The doctrine says no hand
spelling may beat the idiom; today the block-kernel spelling beats the spec's idiom ~7x, and
after 4.5a it still beats it ~2x (8 passes over 160 KB arrays vs one). Only fusion makes the
idiomatic RK4 the fast RK4, and it removes the allocations rather than recycling them. 4.5a is
still first: it is cheap, lane-agnostic, needs no analysis, and is the floor for every
per-iteration temporary that cannot fuse (non-pointwise steps: a stencil or a fold inside a
let rec). Hoisting the step temporaries out of the recursion (the third option) is NOT fully
subsumed (Codex #16, accepted in part): for this kernel there is nothing left to hoist after
4.5c, and before it 4.5a yields the same committed-page effect without an interprocedural
lifetime analysis; but for a non-fusible step, hoisted scratch gives a PREDICTABLE memory
bound that a cache does not. It stays a later option for that class, ranked below both.

Expected: 117 -> ~20-30 (4.5a) -> ~15-25 (4.5b) -> ~11-13 (4.5c) ms; experts 10.5 / 11.3.
(The 4.5a figure is likely optimistic -- Codex round 2 #10: the probes do not price the
remaining passes, the zero fill and the copies.)

### 4.6 (f) stencil

Nothing at the microkernel level: the halo nest is already clean (interior bounds, folded
offsets, ivdep, direct dense reads) and the LLVM arm beats gfortran-expert. Two optional
items, both low priority: (i) producer/consumer row scheduling -- run the row-prodsum
checksum for row r right after row r of `lap` is produced, keeping the `lap` stores (it is
the named output); both folds keep their own order (per-row prodsum, then the fold over
rows in ascending r), so this is legal in repro too and does not depend on 4.4b (Codex
#18); estimated ~1.5-2 ms of 13.5 (an opportunity estimate, not a promise); (ii)
investigate the C++ lane's input generator (input-only twin 9.11 vs 6.26 ms), which is not
the kernel.

---

## 5. Interactions

- mt8 (Path B): the tiles are MAPS, not folds; Path B chunking (G25) is not involved. The
  tile helpers take the `threaded` flag (`ompThreadEmissionEnabled`) like `dgemm_nn`, open
  a capped team, split CELLS (row blocks, dynamic), never a cell's sum -> team-size
  invariant, bitwise across thread counts. The SYRK driver's shared `bp` is safe because
  the packing `omp for` ends in an implicit barrier before any tile reads it, and the
  ascratch/ap buffers are private per executing thread: both barriers stay (Codex #17).
  4.4b's class partials are licensed folds and take Path B chunks in mt8 (serial within a
  chunk, G25) with the fixed class combine -- and inherit Path B's property that a threaded
  licensed fold is deterministic only for a FIXED team size (src/CodeGenBinding.fs:3030-
  3032). The producer's team-size invariance does NOT extend to the checksum. Threading a
  fold still needs the source `omp` authorisation, never the fold licence alone.
  4.5a's cache is thread_local; a block freed on another thread joins that thread's cache
  (legal: new[]/delete[] are thread-agnostic).
- BLADE_FP_REPRO=1: tiles, W, 4.4a, 4.5a-c ON; 4.3 and 4.4b OFF (serial / logical).
- BLADE_FP_CONTRACT=off: tile bodies become vmulpd + vaddpd (separate pipes on Zen 3);
  bitwise R_interp. The `test interp` lane therefore exercises the tiles bitwise.
- BLADE_BLAS on: BLAS keeps precedence for gram (dsyrk); the native tile is the gate-off
  arm. antisym/comoment3 have no BLAS route; the tile applies regardless.
- BLADE_MEMCHECK: cache compiled out in both lanes via `-DBLADE_POOL_CACHE_OFF=1` from
  Build.fs, plus sanitizer self-detection (ASan and the leak counter must see real frees).

---

## 6. Implementation order (payoff per effort)

(Effort S < 1 day, M 1-3 days, L > 3 days of compiler work.)

0. BEFORE any compiler rollout of a tile (Codex #18, accepted): direct HELPER tests -- a
   self-checking C++ program beside `cpp/linalg_probe_tests.cpp`, run by a `blade test`
   block, calling `dsyrk_lj<0|1>`, the antisym driver and `dsym3_lj` against a matched
   serial reference compiled in the SAME TU and mode, comparing RAW BITS (memcmp), at
   p = 0..9, every p mod 24 class, MC/NC boundaries, n = 0..17, KC-1/KC/KC+1, 511/512/513,
   both strictnesses, both `-ffp-contract` modes, full-mantissa inputs plus signed zeros,
   cancellation, subnormals, Inf/NaN, a zero scale. Compiler-level corpus tests then only
   need ROUTE assertions (the plan line names the tile) plus value pins: printing 100 cells
   and `test llvm`'s relative 1e-9 rule (tests/LlvmTests.fs:101-128) cannot establish
   bitwise identity; `test interp` (contract off, 15 digits) sees 1 ULP in only 2.2-5.7% of
   cells. Also: disassembly gates (no spills in the s loop; `vfmadd` counts agree with the
   serial reference) per compiler and mode.

   The gate is applied PER HELPER FAMILY (syrk, anti, sym3) before that family's rollout,
   not as one up-front barrier (Codex round 2 #9).

Order revised after Codex round 2 #9 (4.4b moved ahead of the tiles; 4.5b split, its
zero-fill half promoted; KRS stays after the LLVM syrk twin so the shared core and ABI are
proven on the simplest family first). Forecasts are hypotheses (section 0 caveat); round 2
#10 corrections are folded in.

1. 4.5a exact-size retained cache, both runtimes. S. Sites: nested_array_utilities.hpp
   allocate / allocate_strict / deallocate / deallocate_strict; blade_llvm_shim.c
   blade_alloc_aligned / blade_free; new blade_pool_cache.hpp (+ deploy list in
   CodeGenLoopNest.fs ~2745); Build.fs: memcheck define for BOTH lanes AND shim-object
   keying by effective flags/defines/compiler (src/Build.fs:1213 today keys on timestamp).
   Tests: `recursive-arrays/007` (RK4) and `008` unchanged pins; normal-then-memcheck build
   in the SAME directory with the cache proven OFF (allocation/free event counts); a runtime
   self-test for take/give/evict/shared budget/thread-exit/bad_alloc-retry. Bench: recur
   serial-licensed full, both lanes: 117 -> ~20-30 ms (Codex: ~20 is likely optimistic --
   the probes do not price Blade's remaining passes, zero fill and copies); record page
   faults and peak WS.
2. 4.4a inline canonical read + bind-once. S. Sites: CodeGenExpr.fs:803-890
   (`lazyCompactRead`), nested_array_utilities.hpp helpers. Tests: corpus reads at every
   rank-2/3 class incl. strict diagonal zero, Hermitian conj on swap, antisym sign on swap,
   an empty strict pool, effectful coordinate order (existing index-types / symmetry corpus
   covers most classes; add a rank-3 strict read test). `test interp` bitwise (addressing
   only). Bench: comoment3 / pairkernel cpp, BOTH regimes (this is the repro fix): by the
   attribution's own arithmetic (keep the non-checksum share, divide the checksum share by
   4.9) 110 -> ~50 and 77 -> ~42 (draft 2 said ~65 / ~48: inconsistent, Codex #10).
3. 4.4b orbit-weighted pool fold. M. Sites: recogniser (new, beside LinAlgPatterns,
   sharing an internal orbit-fold plan with any future native compact fold),
   CodeGenBinding.fs:3397, EmitLlvm.fs:4653/4786; plan line `[fold-form v1] ... orbit-
   weighted pool walk`. Tests: default-regime corpus with INTEGER-valued fixtures (exact
   sums, so pins are exact and association-invariant) at rank 2, 3, strict; seeded p = 0,
   unseeded p = 0 (BL8003), strict p = 1 / r = 2 and p = 2 / r = 3 (+0); the negative
   recogniser list of 4.4b; a repro test proving the logical order is kept; `opt-diff`
   cannot see it (it pins repro). Bench: comoment3 llvm 41.5 -> ~27, pairkernel llvm
   42.6 -> ~33, cpp (after 2.) ~34 for both kernels' checksum-free remainder.
4. 4.1 native packed-triangle syrk tile, C++. M. Sites: blade_packed_gemm.hpp +
   blade_tiles.h core, CodeGenLoopNest.fs:2546 gate-off arm, CodeGenExprSupport.fs
   `packedTriWorth`. Tests: step 0 for the syrk family; corpus gram with the EXPECTED ROUTE
   labelled per shape against `p(p+1)/2 * n >= 262144` -- tile: (61, 257), (61, 513),
   (97, 257), (97, 513), (29, 1001); decline: (29, 513) [223,155], (61, 31), (13, 31) --
   with full-mantissa lcg inputs, route assertions, `test interp`, `test llvm`. Note the
   bench SMOKE gram (61 x 31) declines: smoke validates the nest, full validates the tile.
   Bench: gram serial-licensed AND serial-repro full: cpp 275 -> ~90-100 (its C++ checksum
   accessor remains until 2. lands; ~85-90 after), ratio vs gfortran-expert ~0.85-0.95.
5. 4.5b(i) zero-fill elision, narrow proof (no `while` guard, row reads `prefix(n - m)`,
   m >= 1 literal, no whole-prefix use). S. Site: TypeCheckInfer.fs:13840. Tests:
   recursive-arrays with lags 1, 2, 3 (guarded negative reads still 0), a whole-prefix read
   and a `while`-guarded recurrence (both must KEEP the fill). Bench: recur -3..5 ms.
6. 4.1 LLVM twin. M. Sites: blade_tiles.h + blade_llvm_tiles.c, Build.fs (compile the new
   TU with llvmOptFlags + contract, keyed on headers/compiler/target/contract/sanitizer),
   EmitLlvm.fs ArrVal `Contract` field + materialize arm. Tests: RAW-BIT integration tests
   through the REAL clang-compiled object with emitter-selected operands and scales (step
   0's same-TU harness does not cover the separate object, the ABI or deployment), emitted-
   call assertions, `blade test llvm` (cpp vs llvm agree PROVABLY under contract=off,
   verified under fast -- not "bitwise by construction"), `test llvm blocks`. Bench: gram
   llvm 157 -> ~85-90.
7. 4.1b antisym paired strict tile (+ `(|RowContraction|_|)`). M on top of 4/6. Tests:
   step 0 for the anti family; corpus routes: tile (61, 401), multi-K-block tile (61, 600)
   with every canonical cell checked; decline (23, 11); strict edges p mod 6, p mod 8; a
   reordered `prodsum(b(1), a(0))` and a `P + (-Q)` spelling must decline. Bench: antisym
   74.6 -> ~34 (the one unsuppressed block: the best place to demonstrate a causal gain).
8. 4.2 KRS rank-3 tile + W. M-L. Tests: step 0 for sym3; corpus routes against a
   `packedSym3Worth` (C(p+2,3) * n >= 262144): tile (29, 67), (29, 201), (61, 11), (61, 201);
   decline (13, 11), (13, 201); the `prodsum(c, a, b)` order declines (plan line). Bench:
   comoment3 llvm ~27 -> ~13-16, cpp similar.
9. 4.3 vector-accumulator prodsum (C++). S. Tests: existing fold corpus (default-regime
   pins at 1e-9), every n mod 16 incl. n = 0..3, the float helper; re-measure the gemv
   fiber shape before switching the site. Bench: dot / onepass cpp.
10. 4.5b(ii) destination passing (borrowed out-parameter, both emitter ABIs). M (Codex: the
    ownership work makes "S-M" optimistic). Tests: recur corpus, a callee with two return
    branches, a destination that would alias an argument (must decline). Bench: recur -4..8 ms.
11. 4.5c pointwise-body fusion. L. Tests: opt-diff (gate in optimizerGates), recur corpus,
    a body with a halo read (region split), an RNG `_at` draw (admitted), a stateful draw
    (refused). Bench: recur -> ~11-13.
12. 4.6 stencil row scheduling; C++ input-generator investigation. L, low value.

---

## 7. The Codex conversation (attack log)

Round 1 (independent diagnosis) and round 2 (reconciliation): section 2 and the
corrections folded into section 1.

### Attack round 1 on draft 1 (18 findings; 3 BLOCKER)

Accepted and changed:
- #1 BLOCKER, 4.1: `row[r] = C + rowbase - i - S` forms `C - 1` at i = 0, S = 1 (UB).
  Fixed: row pointers are row bases; `j - i - S` only for validated lanes (rule 6). Also
  checked arithmetic for offsets and sizes; A scratch `MC * kc`.
- #2 BLOCKER, 4.2: the k-panel pack needs `ceil(p/8)*8*n` doubles, not `p*n`; and
  "d == 0 means no scale" turned a real division by zero into no division. Fixed: explicit
  `Scale` (None/Div/Mul); scale applied once after the last K block; n = 0 gives `0 / d`.
- #3 BLOCKER, 4.4b: complex `x * x` is not conjugation-invariant; "a product with a read
  factor" does not prove f(0) = 0 (0 * Inf). Fixed: typed whitelist (real `x*x`, `abs`;
  Hermitian `abs` only).
- #12 BLOCKER, 4.5a: `BLADE_MEMCHECK` is an env setting, never a macro; the LLVM cache had
  no bypass. Fixed: Build.fs defines `BLADE_POOL_CACHE_OFF` for memcheck compiles in both
  lanes, plus sanitizer self-detection.
- #4 (seeds, emptiness, signed zero, purity in 4.4b): seeds restricted to none or +0.0 at
  every level, empty keeps BL8003, f whitelist is non-negative, g must be stable. Codex
  withdrew its round-0 blanket objection to multiplicity weights: the 2/3/6 argument holds.
- #5 (same contraction flag is permission, not proof; g++ vs clang): section 3 now has
  PROVEN (contract=off) vs EXPECTED+VERIFIED (fast), disassembly gates, and no "bitwise by
  construction" claim across compilers. NaN payloads delimited; product operand order kept
  (this caught a real one: the antisym Q term must be `X_j * bcast(Y_i)`, vector first).
- #6 (antisym driver): `Y = X + n`, `ldx = 2n`, four disjoint Scratch buffers; the large-n
  plan changed from "GEMM" to "strict-packed Q scratch with K blocks outer" (half the
  GEMM's memory), with K-inside-tile and GEMM as measured alternatives.
- #7 (RowContraction must match ordered arithmetic): inherits all BlasL3 structural checks
  and matches `prodsum - prodsum` verbatim; LLVM `Contract` plan cleared on any rebuild.
- #8 (shared C header vs C++ driver lifecycle): split into a pure arithmetic core shared by
  both lanes and lane-specific drivers; LLVM driver serial, `atexit` cleanup; cache
  identity includes headers, clang identity, target, contraction, sanitizer.
- #9 (spills): disassembly gate in both modes; MR = 4 for contract=off if it spills (tile
  shape is free w.r.t. bitwise identity).
- #10 (4.3): separate float helper, contiguity proof, overflow-safe bounds, every n mod 16;
  "deterministic across flags" corrected to "fixed under a fixed contraction policy".
- #11 (4.4a): storage-eligibility proof (an owned contiguous pool), `strict3_off` =
  inclusive formula on (a, b-1, c-2) at extent n-2, coordinates bound left to right,
  strict zero tested before `pool_base`.
- #13 (retention bounds): one per-thread budget (32 MB) across types, 8 MB max block (the
  48 MB trajectory is never retained), bad_alloc flush-and-retry; shim globals documented
  as single-thread-only.
- #14: renamed "exact-size retained cache"; thresholds are hypotheses to measure.
- #15 (4.5b): no elision for `while`-guarded recurrences (freeze from row 0); proof over
  the lowered control flow; destination marked borrowed, all return branches write it.
- #16 (4.5c): explicit pointwise conditions, DAG sharing via scalar locals, staged fusion
  against spills; hoisting kept as a later option for non-fusible steps.
- #17 (threading): barriers retained; the checksum's Path B partials are team-size
  dependent, so the producer's invariance claim no longer extends to it.
- #18 (tests): new step 0 -- raw-bit helper tests before any rollout; corpus shapes
  restricted to ones that pass `packedTriWorth`; packing estimate now counts reads and
  writes; triangle edge 0.5-1%.

Rejected or kept, with reasons:
- My 2/3/6 multiplicity argument is KEPT (Codex agreed in round 1); I added that doubling
  and tripling overflow to the same infinity as the repeated sums, so "when
  representable" is not a restriction on the identity, only on finiteness.
- Kept 4.5c (fusion) ranked above hoisting as the doctrine fix; Codex's point that hoisting
  gives predictable memory for non-fusible steps is recorded, not promoted.

### Attack round 2 on draft 2 (11 findings; 2 BLOCKER)

Accepted and changed:
- #1 BLOCKER, 4.5a: a shared byte counter driving the per-type eviction loop could evict
  from an EMPTY cache (`cells[0]` with n = 0, n underflow). Fixed: one thread_local
  `tl_retained` budget; eviction touches only this type's entries and stops at n = 0; if the
  budget is still full (other types), the block is simply not retained.
- #2 BLOCKER, 4.5a: `buildLlvmShim` reuses the shim object by source timestamp
  (src/Build.fs:1213), so a memcheck build after a normal one in the same directory would
  link the cache-ENABLED object. Fixed: objects keyed by effective flags/defines/compiler,
  identity in the exe-cache key; same-directory transition test.
- #3, 4.4b: emptiness is now judged on the logical domain and original seeds (seeded p = 0
  returns the seed; strict p < r has an empty pool but a non-empty logical domain of zeros:
  answer +0, not BL8003); element types listed.
- #4 (antisym): P-in-pool overwritten by P - Q on the final block confirmed strict-legal;
  invariants and gather/scatter staging written down; multi-K-block test added.
- #5: `apply(sk, +0, d)` for n = 0; `sym3_off` moved above its users; checked-arithmetic
  stated as an entry precondition ((n+2)^3 fits size_t) instead of claimed in passing.
- #6: my "semantics already decided / P1 lifts BL3999" was mislabelled -- the compact-fold
  plan's 4.3 is a recommendation (P0 ratifies, P1 is the rank repair, P3/P4 the full-domain
  folds). Rewritten: the recogniser needs none of them; G16 blocks only the future
  array-product spelling; negative recogniser tests listed.
- #7: Codex APPROVED the shared C core + separate LLVM TU over IR emission; added raw-bit
  integration tests through the real clang object and emitted-call assertions; removed the
  last "bitwise by construction".
- #8: corpus shapes now carry their expected route against the stated threshold (draft 2's
  p = 29, n <= 513 and every n = 31 shape never reached the tile); "n = 600 (GEMM
  fallback)" corrected to the Q-scratch multi-block route.
- #9: section 6 reordered -- 4.4b ahead of all tiles, 4.5b split (zero-fill elision early,
  destination passing later at M), KRS kept after the LLVM syrk twin, helper gate per family.
- #10: forecasts corrected where my own arithmetic was inconsistent (4.4a: comoment3 cpp
  ~49 not ~65; pairkernel ~42 not ~48) and flagged where likely optimistic (recur after
  4.5a: ~20-30, not ~20; gram cpp ~90-100 until 4.4a lands).
- #11: KRS waste bound tightened from my borrowed "25-35%" to "< 24%, compute exactly".

Rejected / not changed:
- None of round 2's findings was rejected. One residual difference of emphasis: Codex
  would not promise a gram C++ ratio before 4.4a is in; I keep the ~0.85-0.95 range but
  state it conditional on 4.4a.

### What the two planners agree is still unproven (needs the bench, not more planning)

1. Whether g++ and clang contract the vector-extension tile and the serial reference alike
   under `fast` (verify by disassembly + raw-bit helper tests; if not, the repro-regime
   claim degrades to "bitwise under contract=off only").
2. The tile crossover (`packedTriWorth`, `packedSym3Worth`) -- the jam wins at small p.
3. Whether contract=off spills at MR = 6.
4. The Windows heap's behaviour at 4/16/64 KB thresholds, and peak-memory cost at mt8.
5. Every "expected after" number.
