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
