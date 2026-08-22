# Triangular microkernels — verified prototypes

**Status: REFERENCE IMPLEMENTATIONS, not wired into the build.** Nothing here is
compiled by `Blade.fsproj`, called by either emitter, or covered by `blade test`.
Each file is a standalone, self-verifying C/C++ program: build it, run it, and it
checks itself against an independent oracle and prints its own timings. They exist
so that a future emitter has a *measured* target to reproduce rather than a design
sketch to guess at.

**Four findings have since graduated into the compiler** — the output-axis jam
(ae951eb, widened in 272e9be), `group_by`'s single CSR pool (e83f5d8), branchless
`compound` compaction (4eed8a4), and the heap-free small solve (d99195f). Their rows
below say so. `gram_jam_width.cpp` is no longer a sketch but the acceptance instrument
that fixes the shipped jam's tile width; re-run it after any gcc upgrade.

Built and measured 2026-08-21 on a Ryzen 7 5800HS (Zen 3, AVX2, 16 YMM, 2 FMA
units, DDR4-3200 dual channel), gcc 15.2 / clang 22.1.8 (MSYS2 ucrt64), at
`-O3 -march=native -ffp-contract=fast`, single-threaded. **Five of these were
measured while five sibling jobs compiled on the same box**, so absolute figures
are depressed (an in-process FMA probe read 3.22 GHz against ~4.0 idle) and
arm-vs-arm ratios are the trustworthy part. Where a `% of peak` or `% of roof`
appears it is against a roofline sampled *in the same window*, which is the
load-robust form.

`gram_jam_width.cpp` is the exception: measured 2026-08-22 on an IDLE box, with the
clock calibrated in-process by a dependent `vaddsd` chain (it reads 4.03 GHz against the
3.22 the loaded batch saw). Its `cyc/MAC` column is therefore comparable across machines
in a way the others' wall-clock figures are not.

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
| `probe_accumulator_chains.c` | instrument: accumulator count vs working set | — | 1/2/4/8 YMM = 0.204/0.100/0.056/0.042 ns/cell; 12 regresses |
| `probe_bandwidth_roof.c` | instrument: this machine's single-core roofline | — | read 28.5 GB/s, RMW 15-20 GB/s (single core, NOT the all-core figure) |

## Second batch — from the optimization-site survey (`SURVEY.md`)

| file | what it is | verdict | headline |
|---|---|---|---|
| `unroll_and_jam.c` | unroll-and-jam the OUTPUT axis around an in-nest fold | **SHIPPED** (ae951eb, 272e9be) | **13.95x is SAMPLE-MAJOR and does not transfer** — see `gram_jam_width.cpp` for 3.5x on the real shape. The 1.00x for the licence-requiring fold split DID reproduce |
| `gram_jam_width.cpp` | the SHIPPED jam, R swept 2-12, against the emitter's exact text | **the acceptance instrument** | knee at **R=5-6 (3.47-3.53x)**; bitwise `yes` at every width while `base_fma` reads `NO` |
| `segmented_fold.c` | fused segmented reduction (`group_by` + `reduce`) | **SHIPPED in part** (e83f5d8: the CSR pool) | 19-37x total — of which **one CSR pool instead of G mallocs is 62-94%** |
| `stream_compaction.c` | SIMD `mask`/`compound` (the WHERE idiom) | **SHIPPED in part** (4eed8a4: branchless scalar) | branchless scalar captures **95.6%** on doubles; SIMD is required on 32-bit (1.4-2.6x more) |
| `small_solve.c` | fixed-size LU / Cholesky, heap-free | **SHIPPED in part** (d99195f: the stack buffer) | 13.3x/3.4x/3.0x/1.86x/1.46x at n=2/3/4/6/8 — **73-85% is just removing malloc** |
| `small_solve_batched.c` | batched small solves, 4 layouts | **BUILD — gather, do NOT transpose** | 5.5-32x; decomposed as heap 5.1x x SIMD 2.3x x **layout only 1.19x** |
| `sym3_eigen.c` | symmetric 3x3 eigen, closed form vs Jacobi | **BUILD ONLY the gap-guarded variant** | 4.2x at Jacobi accuracy — the naive closed form returns **duplicate eigenvectors** |

### What the second batch established

**The bitwise transform beats the licensed one — the direction held, the magnitude did
not.** Unrolling the enclosing OUTPUT axis is bitwise-identical on arbitrary random
doubles, while splitting the fold across accumulators (which *requires*
`BLADE_FP_REASSOC`) measures **1.00x** once `d` pushes operands out of L1. Both halves
reproduced on the shipped emitter. But the prototype's **13.95x did not transfer**: it
measured SAMPLE-MAJOR operands, and Blade's real emitted shape has the contracted axis
contiguous, so the jam buys ILP and operand reuse but not cross-cell SIMD. On the real
shape it is **3.47-3.53x at the R=5-6 knee** (n=257), rising to ~3.7-3.8x asymptotically
— see correction 15 for why that single number is fold-length-dependent. The corpus
census (116 serial statement-form reduces against 6 licensed) is still addressable
**without touching a single `where` clause**.
The two transforms are **complementary, not competing**: rank >= 1 output takes the
jam (bitwise, default-on); a rank-0 output — a true `reduce` to a scalar — cannot be
jammed at all, and there the licensed fold split is the only lever and is worth 4-10x.

**Allocation is usually the dominant term — but check, do not assume.** Three of the
four kernels in this batch decompose that way: the segmented fold is 62-94% allocator,
the small solve 73-85%, and the batched solve's layout change is worth only 1.19x
against 5.1x for removing the heap. Consistent with `plan-simplex-blocked-compute.md`
§0c, which found ~55% of a benchmarked program was allocator and first-touch.

**The dense-gram jam is the counterexample, and it was mispredicted on exactly this
prior.** Allocation was the leading hypothesis for why the shipped jam underperformed
its prediction; measured, it is worth **0.13x of the gap** (setup 0.30 ms, output alloc
7 us, first-touch 0.1 ms; nest-only 2.93x against whole-program 2.80x). The real cause
was correction 15. A heuristic with a 3-of-4 hit rate is still a heuristic.

**Threaded builds change which knob binds.** At 8 threads the jam is ~1.5x and R is
noise, against ~100 GB/s of operand traffic — bandwidth-bound, where more accumulators
buy nothing and only blocking/packing would. Single-thread and multi-thread are
different problems and should stop being quoted as one number.
### More corrections, from building these

10. **FMA CONTRACTION IS A BIT-CHANGING TRANSFORM INDEPENDENT OF REASSOCIATION** — but
    the conclusion originally drawn from it was WRONG, and it cost a tile width.
    The true half: contraction changes bits without reassociating anything, so a jam
    that hand-writes `fma()` breaks the byte-exact gates. `gram_jam_width.cpp`'s
    `base_fma` arm still reads `NO` against `base` on full-mantissa operands.
    The false half: "a bitwise-safe jam must emit `vmulpd`+`vaddpd` and gives up
    11-20%", and the derived rule that gcc contracts a jammed body at R >= 8. **On the
    shipped emission that does not happen at ANY width** — zero `vfmadd*` in the jammed
    fold body from R=2 to R=16, and the full 301x303 output is bit-identical across all
    of them. The R >= 8 contraction was an artifact of the SAMPLE-MAJOR prototype, and
    believing it capped the shipped tile at R=4 for a release, costing 15.5%.
    Measure contraction on the emission you are shipping, never on a prototype.
    (The tuning's own reason still holds: `base_fma` is 3.13 cyc/MAC against `base`'s
    2.44, so the fused single-accumulator form really is slower.)
11. **The tile shape must be derived from the OUTPUT EXTENTS, not fixed** — partly
    upheld, partly overruled by what shipped. Upheld: a 4x8 tile on a 6-row output
    covers rows 0-3 and sends 33% of the work to a scalar tail (2.18x, where a 2x8 tile
    dividing 6 exactly gets **12.63x on identical data**), and `n < R_j` kills the jam
    outright (1.01x at n=6). Overruled: "emit a masked short tile, never a fallback to
    the reference nest" — the shipped emitter uses a FIXED R with a scalar remainder,
    and at p=303 that 3-cell remainder costs **1.4%**, which did not justify the
    masking machinery. The rule that survives is about the CHEAP case being cheap:
    fixed R plus a scalar tail is fine when `p >> R`, and the extent-derived tile only
    earns its complexity when `p` is small enough that the tail is a real fraction —
    which in this corpus is every gram site, since the largest extent anywhere is 8.
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

15. **The un-jammed baseline is NOT a serial dependent chain, and that is why one
    speedup number is meaningless.** Adjacent output cells are independent, so the
    out-of-order window overlaps one cell's add-chain tail with the next cell's head.
    Measured, that recovery is a fixed ~100-150 cycles PER OUTPUT CELL — a large
    fraction of a short fold, negligible on a long one. At n=257 it puts the baseline
    at **2.44 cyc/MAC, faster than the 3.0 its own `vaddsd` chain requires**, so the
    jam appears to win less. Proved by control, not by argument: `base_ser` (identical
    arithmetic, each cell seeded `prev - prev` so cells CANNOT overlap) sits at 3.05
    cyc/MAC at every n. Consequence: the same transform measures 2.96x at n=259 and
    3.69x at n=2051. **Quote a jam speedup with its fold length or do not quote it.**

16. **The jam's knee is SHUFFLE THROUGHPUT, not accumulator count.** The natural model
    -- R independent scalar chains, saturating at ~6-8 -- predicts the wrong shape.
    gcc transposes the R x 4 tile and runs R-lane `vaddpd` in k-order, so the binding
    resource is shuffles per iteration (R=4: 16, R=6: 23, R=8: 32). That puts the peak
    at R=5-6 and makes it fall back by 8, and it is why `-fno-tree-vectorize` — which
    really does produce R scalar chains — is **24% SLOWER** than the vectorized form.
    Correction 3 said lanes are not chains; this is the other half, that the compiler
    may convert your chains into lanes behind your back and change which knob binds.

17. **A fixture whose operands never round cannot test bitwise-ness — generalizes 12.**
    `gramdense.blade` builds operands from `1.0+0.5*i` and `0.25+0.125*j`: exact dyadic
    rationals whose products and sums are themselves exact. On it, an explicit-`fma`
    control hashes IDENTICAL to `vmulpd`+`vaddpd`. Compose that with `InterpDiff`
    comparing stdout at `std::setprecision(15)` when a double needs 17 digits to
    round-trip, and **nothing in the tree could have caught a non-bitwise jam**.
    `gram_jam_width.cpp` therefore defaults to full-mantissa operands and keeps
    `-DDYADIC` only to demonstrate the blindness; its self-check is that `base_fma`
    MUST read `NO` by default. Any kernel here claiming "bitwise" on small-integer or
    dyadic inputs is claiming nothing.

18. **A native arm can be dead code in the default environment.** With `OPENBLAS_DIR`
    set and `BLADE_BLAS` unset — the shipping default, since presence alone enables the
    route — dense `gram` goes to `blade_gram_distinct_d` and the jammed arm never runs.
    No size threshold, purely environmental. Every bench fixture for a native arm must
    pin `BLADE_BLAS=0`, and a speedup on such an arm describes a configuration, not a
    default. Check which arm your program actually reaches before optimizing it.

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

These prototypes exist because claims in the design did not survive contact
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
   staircase is unavoidable; you only choose where to pay it. (Measured anyway before
   being believed: 1.33-1.78x on the diagonal pass, but only **2-9% end-to-end**. The
   kernel that established this, `paired_triangle.c`, was the one DO-NOT-BUILD result
   in the set and has been removed — recover it from git history if the argument above
   ever needs re-testing rather than re-reading.)
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

Each file documents its own arms and CLI in its header. `mirror_transpose.cpp` and
`gram_jam_width.cpp` need `g++ -std=c++17`; the latter takes `<rounds> <reps>` and
prints a bitwise column that MUST show `NO` on its `base_fma` arm (see correction 17 —
if it shows `yes`, the operands cannot round and the column is inert):

```
g++ -O3 -march=native -ffp-contract=fast -std=c++17 -o gjw.exe gram_jam_width.cpp
./gjw.exe 5 3
```

Shapes are compile-time: `-DMM=301 -DNN=257 -DPP=303`. Sweep `NN` to see correction 15
(the speedup is fold-length-dependent), and keep extents non-power-of-two — CLAUDE.md's
benchmark discipline, and a ~7x cache artifact on dense data if you ignore it.

**Timing hygiene, learned the hard way**: these kernels are pure with
loop-invariant arguments, so gcc hoists the whole call out of a repetition loop
and reports near-zero times. Every timed region here is bracketed with
`asm volatile("" ::: "memory")` and the results are read back into a printed
checksum. Keep both if you reuse this code.
