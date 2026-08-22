# Triangular microkernels — verified prototypes

**Status: REFERENCE IMPLEMENTATIONS, not wired into the build.** Nothing here is
compiled by `Blade.fsproj`, called by either emitter, or covered by `blade test`.
Each file is a standalone, self-verifying C/C++ program: build it, run it, and it
checks itself against an independent oracle and prints its own timings. They exist
so that a future emitter has a *measured* target to reproduce rather than a design
sketch to guess at.

Built and measured 2026-08-21 on a Ryzen 7 5800HS (Zen 3, AVX2, 16 YMM, 2 FMA
units, DDR4-3200 dual channel), gcc 15.2 / clang 22.1.8 (MSYS2 ucrt64), at
`-O3 -march=native -ffp-contract=fast`, single-threaded. **Five of these were
measured while five sibling jobs compiled on the same box**, so absolute figures
are depressed (an in-process FMA probe read 3.22 GHz against ~4.0 idle) and
arm-vs-arm ratios are the trustworthy part. Where a `% of peak` or `% of roof`
appears it is against a roofline sampled *in the same window*, which is the
load-robust form.

Design context: `docs/plans/plan-rank-r-former.md` (the KRS schedule) and
`docs/plans/plan-simplex-blocked-compute.md` §0a-§0d (the measurements that
bound what is left to win).

## The kernels

| file | what it is | verdict | headline |
|---|---|---|---|
| `krs_former.c` | rank-3 symmetric former, Khatri-Rao Simplex schedule | **BUILD** | **72.6% of FMA peak**; 2.34x at Blade's 61x2003 shape, 15.1x at 256x1024 |
| `packed_syr_syrk.c` | packed symmetric rank-1 (`syr`) and blocked rank-k | **BUILD** the rank-k arm | **13.2x** over repeated rank-1, and **bitwise identical** to it |
| `packed_symv.c` | packed symmetric matvec, fused dot+axpy | **BUILD** | **2.4x** over *optimized* dense, at 94% of the measured roof |
| `multiplicity_fold.c` | full-domain triangular fold via multiplicity classes | **BUILD when the fold refusal lifts** | 11-20x over the n^2 walk, 63/63 bitwise |
| `mirror_transpose.cpp` | 4x4 register transpose serving both mirror orientations | **BUILD for `decompact` only** | 1.3x and a `memcmp`-identical drop-in; the fold seam is the wrong one |
| `paired_triangle.c` | two diagonal blocks interlocked into one dense square | **DO NOT BUILD** | 1.33-1.78x on the diagonal pass but only **2-9% end-to-end** |
| `probe_accumulator_chains.c` | instrument: accumulator count vs working set | — | 1/2/4/8 YMM = 0.204/0.100/0.056/0.042 ns/cell; 12 regresses |
| `probe_bandwidth_roof.c` | instrument: this machine's single-core roofline | — | read 28.5 GB/s, RMW 15-20 GB/s (single core, NOT the all-core figure) |

## Second batch — from the optimization-site survey (`SURVEY.md`)

| file | what it is | verdict | headline |
|---|---|---|---|
| `unroll_and_jam.c` | unroll-and-jam the OUTPUT axis around an in-nest fold | **BUILD — the headline** | **13.95x, BITWISE on arbitrary doubles**, while the licence-requiring fold split gets **1.00x** |
| `segmented_fold.c` | fused segmented reduction (`group_by` + `reduce`) | **BUILD, but fix the allocator first** | 19-37x total — of which **one CSR pool instead of G mallocs is 62-94%** |
| `stream_compaction.c` | SIMD `mask`/`compound` (the WHERE idiom) | **BUILD, width-dependent** | branchless scalar captures **95.6%** on doubles; SIMD is required on 32-bit (1.4-2.6x more) |
| `small_solve.c` | fixed-size LU / Cholesky, heap-free | **BUILD, but the allocation is the win** | 13.3x/3.4x/3.0x/1.86x/1.46x at n=2/3/4/6/8 — **73-85% is just removing malloc** |
| `small_solve_batched.c` | batched small solves, 4 layouts | **BUILD — gather, do NOT transpose** | 5.5-32x; decomposed as heap 5.1x x SIMD 2.3x x **layout only 1.19x** |
| `sym3_eigen.c` | symmetric 3x3 eigen, closed form vs Jacobi | **BUILD ONLY the gap-guarded variant** | 4.2x at Jacobi accuracy — the naive closed form returns **duplicate eigenvectors** |

### What the second batch established

**The bitwise transform beats the licensed one, by a lot.** `unroll_and_jam.c` is the
survey's convergent finding built and measured: unrolling the enclosing OUTPUT axis
gives 8-14x on a 2-level contraction and is bitwise-identical on arbitrary random
doubles, while splitting the fold across accumulators — which *requires*
`BLADE_FP_REASSOC` — measures **1.00x** once `d` pushes operands out of L1. Chains
alone are worth ~0x; chains *inside* a jam are worth ~3.5x on top of it, because the
jam is the transform that creates the reuse. The corpus census (116 serial
statement-form reduces against 6 licensed) is therefore addressable **without touching
a single `where` clause**.

The two transforms are **complementary, not competing**: rank >= 1 output takes the
jam (bitwise, default-on); a rank-0 output — a true `reduce` to a scalar — cannot be
jammed at all, and there the licensed fold split is the only lever and is worth 4-10x.

**Allocation keeps being the dominant term.** Three of the four new kernels decompose
that way: the segmented fold is 62-94% allocator, the small solve 73-85%, and the
batched solve's layout change is worth only 1.19x against 5.1x for removing the heap.
Consistent with `plan-simplex-blocked-compute.md` §0c, which found ~55% of a
benchmarked program was allocator and first-touch.

### Five more corrections, from building these

10. **FMA CONTRACTION IS A BIT-CHANGING TRANSFORM INDEPENDENT OF REASSOCIATION.** gcc's
    `AVOID_256FMA_CHAINS` tuning on znver3 *refuses* to contract the single-accumulator
    fold Blade emits — the asm is byte-identical at `-ffp-contract=off` and `=fast` —
    but it *does* contract a fold-split form. So a jammed kernel that hand-writes FMA
    changes the result bits **without reassociating anything**, and would break the
    byte-exact interp/diff-oracle gates. A bitwise-safe jam must emit `vmulpd`+`vaddpd`
    and gives up 11-20% of its throughput to do so. (Measured reason the tuning exists:
    the fused single-accumulator form is *slower* than unfused — a 4-cycle FMA chain
    against a 3-cycle `vaddsd` chain.)
11. **The tile shape must be derived from the OUTPUT EXTENTS, not fixed.** A 4x8 tile on
    a 6-row output covers rows 0-3 and sends 33% of the work to a scalar tail: 2.18x,
    where a 2x8 tile that divides 6 exactly gets **12.63x on identical data**. And
    `n < R_j` kills the jam outright (1.01x at n=6). Emit a masked short tile, never a
    fallback to the reference nest.
12. **The prefix-sum segmented fold is a trap that would have passed our own test.**
    `out[g] = S[off[g+1]] - S[off[g]]` vectorizes beautifully and makes every hard case
    free — and it is bitwise-correct on the small-integer inputs these kernels verify
    with. It is unusable in production: the differencing cancels against the *global*
    prefix, so a late short segment loses most of its significant digits. Recorded
    because it looks excellent on exactly this benchmark.
13. **Batched small solves: the win is SIMD, not layout — and transposing costs more
    than it saves.** Converting AoS to interleaved BLK4 costs 3.6-51.6 ns per system
    while the layout itself is worth only 1.05-1.31x, so gather-into-registers beats
    transpose-the-batch at *every* size tested. Interleave only if the array is *born*
    interleaved. Secondary: full SoA is *worse* than BLK4 at n=8 (83.8 vs 48.1 ns) —
    `n*n` separate streams exhaust the prefetchers.
14. **The closed-form symmetric 3x3 eigendecomposition is not safe as usually written.**
    Independent cross products give **orthogonality error 1.00** on degenerate input —
    two returned eigenvectors are the same vector — with residuals to 3e-2, and
    degenerate 3x3 tensors are ordinary in physics (isotropic stress, `A = cI`). Two
    repairs, ~20 lines: orthogonalize structurally, and fall back to Jacobi when the
    analytic gap is below `1e-6 max|lambda|`. That version is 4.2x faster at Jacobi
    accuracy with a 0% fallback rate on generic input. The naive one is a fast wrong
    answer.

Also worth knowing, from the same batch: **branchless pivoting failed twice** — gcc
emits branches rather than blends for `sw ? a : b` on doubles, and a true bitmask
select was 2x worse still (two GP<->XMM domain crossings per select). Do not pay for
pivoting on SPD input; use Cholesky, which needs none and is 1.6x faster at n=3,4.
**Specialization itself stops paying at n ~ 8-10** — against a *no-heap* runtime-n
loop it is 3.4x at n=2 but only 1.12x at n=8, so specialize aggressively at n <= 4 and
merely remove the allocation above that. And **fixing `n` to a compile-time constant is
bitwise** (955/955 identical), so it is a byte-exact drop-in; switching LU to Cholesky
is **not** (2.9%) and must be a visible semantic choice.

## What building them corrected

These prototypes exist because nine claims in the design did not survive contact
with a compiler. Recorded so nobody re-derives them:

1. **"Multi-accumulator alone captures ~half the former's win."** It captures
   ZERO (1.02x / 0.97x / 0.86x — a net loss at the largest shape). The naive
   former nest is **operand-supply-bound**, not FMA-latency-bound: three
   scattered loads per FMA, all of `A` re-streamed per output cell. Isolated by
   sweeping working set at fixed loop shape — multi-accumulator pays 1.71x when
   operands are L1-resident, 1.03x at L2, 1.00x at L3. KRS wins because packing
   plus register tiling *create* the L1-resident high-reuse stream; only then do
   the chains matter.
2. **"More accumulators regress past 8."** True for a pure *reduction*
   (`probe_accumulator_chains.c`), false for a register-blocked *outer product*,
   where accumulator count buys operand reuse (1.50 vs 1.33 FMA-per-operand):
   6x8 beats 4x8 by 12-28%. The shape decides, not a universal number.
3. **"4 YMM = 16 independent chains."** Lanes are not chains. The 4 lanes in one
   YMM advance together inside one instruction; a chain is per architectural
   register. Saturation needs ~6 (3-cycle latency x 2 pipes), so 8.
4. **"The ragged tail amortizes to nothing."** Waste is ~18.2/n: 7.1% at n=256
   but **45.4% at n=61, which is Blade's actual comoment3 extent**. And the
   dominant term is *panel misalignment*, not raggedness — row blocks start at
   arbitrary j0 while packed panels are pinned to multiples of 8. Repacking `A`
   per `i` with panel origin at `k = i` is worth an estimated further 1.3-1.5x.
5. **"Pair two diagonal triangles into one dense square."** IMPOSSIBLE for a
   contraction. A GEMM cell factors as `rowop(a)*colop(b)`; triangle I needs
   `A[t][i0+a]` and its pair needs `A[t][i1+a]` for the *same* `a`. RFP works at
   the storage level because placing bytes has no operand structure. The
   staircase is unavoidable; you only choose where to pay it.
6. **"Blocking amortizes byte traffic."** It does not — measured row-blocking
   (1.59-1.71x) EXCEEDS its own traffic model's 1.50x ceiling, so the model
   cannot be the mechanism. It is **memory-level parallelism**: R independent
   streams give R outstanding misses against L3 latency. Confirmed twice
   independently (`packed_symv.c`, `packed_syr_syrk.c`).
7. **"35-45 GB/s achievable."** That is the *all-core aggregate*. One Zen 3 core
   runs out of line-fill buffers first: 28.5 GB/s read, 15-20 GB/s RMW, and the
   ~0.7 ratio between them is what RMW-vs-read should cost.
8. **"The mirrored read emits `vgather`."** Neither gcc nor clang emits
   `vgatherqpd` on znver3 at all (Zen tuning disables it). What appears is a
   *hand-rolled* gather — extract indices, 4 scalar loads, `vinsertf128`, ~10 ops
   per vector. Same disease, different encoding.
9. **The fold is the wrong driver for the mirror transpose.** For a pure sum the
   transposed operand contributes the same total (summation is
   permutation-invariant), so the transpose is provably dead and a sufficiently
   clever compiler may delete it. The fold measures the transpose's COST, not its
   NECESSITY. It is load-bearing only where the mirrored orientation is
   positionally observable — `decompact`, and mirror kernels not commutative in
   their two arguments.

## Two properties worth preserving in any emitter

**Bitwise exactness is achievable far more often than assumed.** Five of six
kernels verify BITWISE against their references — `packed_syr_syrk.c`'s blocked
rank-k schedule does so on *arbitrary random doubles*, because it re-tiles which
cells are visited without reassociating any cell's sample sum. So the fast
schedules can be default-on rather than licence-gated, which matters
disproportionately here: Blade's interp and diff-oracle gates are byte-exact.
The price is two invariants: **keep the sample loop innermost and ascending, and
do not fold a scale factor into a pre-multiplied operand.**

**Packed rows cannot be collectively aligned.** The row pitch shrinks by one
every row, so at most `1/R` of an R-row panel is ever 32-byte aligned. Any
emitter that assumes aligned packed row starts is wrong; these kernels use
unaligned loads throughout, and that is a consequence of the layout, not
laziness.

**`packed_syr_syrk.c` ASSUMES A SAMPLE-MAJOR OPERAND LAYOUT** (the contracted
sample axis is the leading/streamed one). This is the largest unstated
integration risk in the arm this README recommends building, flagged by the
2026-08-21 linalg survey: transposing operands to sample-major is precisely the
unlock for a register-blocked contraction, so if Blade's operands arrive in the
other orientation the kernel's numbers do not transfer until a transpose (or a
layout choice) is paid for. `krs_former.c` packs `A` explicitly and INSIDE its
timed region, so it carries no such hidden premise — the contrast between the
two is the thing to notice.

## Building and running

```
export PATH="/c/msys64/ucrt64/bin:$PATH"
gcc -O3 -march=native -ffp-contract=fast -o krs.exe krs_former.c -lm
./krs.exe peak
./krs.exe verify <n> <d> <KC> <MR>
./krs.exe bench  <n> <d> <reps> <KC> <arms:RAVTB> <MR>
```

Each file documents its own arms and CLI in its header. `mirror_transpose.cpp`
needs `g++ -std=c++17`.

**Timing hygiene, learned the hard way**: these kernels are pure with
loop-invariant arguments, so gcc hoists the whole call out of a repetition loop
and reports near-zero times. Every timed region here is bracketed with
`asm volatile("" ::: "memory")` and the results are read back into a printed
checksum. Keep both if you reuse this code.
