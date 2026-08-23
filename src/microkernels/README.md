# Triangular microkernels — measured prototypes, and what they refuted

**Status: REFERENCE IMPLEMENTATIONS, not wired into the build.** Nothing here is
compiled by `Blade.fsproj`, called by either emitter, or covered by `blade test`.
Each file is standalone and self-verifying: build it, run it, it checks itself
against an independent oracle and prints its own timings. They exist so a future
emitter has a *measured* target, not a design sketch.

**Four findings have graduated into the compiler**: the output-axis jam (ae951eb
→ 272e9be → extent-derived in a3837e6), `group_by`'s single CSR pool (e83f5d8),
branchless `compound` compaction (4eed8a4), and the heap-free small solve
(d99195f).

This document has survived **two rounds of contact**: kernels built to test the
design, then a second pass built to attack the kernels. Corrections overturned in
round 2 are marked **(r2)** — several first-round refutations were themselves
wrong, and that history is kept because it is the evidence for the methodology
rules (corrections 19–20).

Design context: `docs/plans/plan-rank-r-former.md`, `plan-simplex-blocked-compute.md`
§0a–§0d, `plan-unroll-and-jam.md`. `SURVEY.md` is the census that chose batch 2.

## Measurement conditions

Ryzen 7 5800HS (Zen 3, AVX2, 16 YMM, 2 FMA pipes, DDR4-3200), gcc 15.2 and
clang 22.1.8 (MSYS2 ucrt64), `-O3 -march=native -ffp-contract=fast`
(= `Build.fs optFlags()`), single-threaded.

**Box load is a first-class variable.** The 08-21 batch ran beside five compile
jobs (3.22 GHz observed vs ~4.0 idle); a bandwidth-bound baseline under a
register-blocked numerator INFLATES the ratio under contention, and one headline
(13.2x, really 3.14x) was produced exactly that way (correction 19). The 08-22
batch ran idle, under a mutex, clock-calibrated in-process, quoted in **cyc/MAC**
— prefer that form; it survives clock drift and other machines.

## The kernels

| file | what it is | verdict | headline |
|---|---|---|---|
| `krs_former.c` | rank-3 symmetric former, KRS schedule | **BUILD** | **72.6% of FMA peak**; 2.34x at Blade's 61x2003, 15.1x at 256x1024 |
| `packed_syr_syrk.c` | packed `syr` + blocked rank-k | **BUILD extent-gated — headline REVISED** | 13.2x was a loaded box vs a baseline Blade never emits; see `syrk_orientation.c` |
| `syrk_orientation.c` | the contraction in both operand orientations, 11 arms, emitted nest as reference | **BUILD `CC_xpose+blocked MULADD`, extent-gated** | **5.80x over the emitted nest, BITWISE** (n=2003 k=256); **loses to the jam at n=61** |
| `packed_symv.c` | packed symmetric matvec, fused dot+axpy | **BUILD** | **2.4x** over optimized dense, 94% of roof |
| `multiplicity_fold.c` | full-domain triangular fold via multiplicity classes | **BUILD when the fold refusal lifts** | 11-20x over the n^2 walk; its "bitwise" is integer-only addressing, not FP safety |
| `mirror_transpose.cpp` | 4x4 register transpose, both mirror orientations | **BUILD for `decompact` only** | 1.3x, `memcmp`-identical; the fold seam is the wrong one |
| `gather_probe.c` | mirrored read: compiler emission vs HW gather vs address recurrence | **BUILD the recurrence, never the gather** | gather LOSES 1.09-1.65x; recurrence WINS 1.30-1.34x at n >= 2003 |
| `align_probe.c` | padded-aligned packed rows vs natural pitch | **BUILD if the layout is Blade's to choose** | **1.11-1.21x for 0.05-0.97% memory**; the ADDRESS costs, not the instruction |
| `unroll_and_jam.c` | the original jam prototype | **SHIPPED; superseded as evidence** | its 13.95x is SAMPLE-MAJOR; the real shape gives 3.5x |
| `gram_jam_vec.cpp` | the jam vs a **j-major packed** operand + intrinsics control | **BUILD — the next emitter target** | **6.65x doubles / 10.6x floats** vs the jam's 3.5x / 3.0x; intrinsics match plain C |
| `gram_iblock.cpp` | IB x R 2-D blocking, no packing | **BUILD for small `p` only** | +4% at p=303 (shuffle wall), **2.2x at p=8**; pack alloc free from m=8 |
| `krs_repack.c` | correction 4's per-`i` repack + free-packing upper bound | **DO NOT BUILD** | **0.82-0.93x** everywhere, both compilers; 0.89-0.95x even free |
| `segmented_fold.c` | fused `group_by` + `reduce` | **SHIPPED in part** (e83f5d8) | 19-37x, of which the CSR pool is 62-94% |
| `segmented_scan_safe.c` | reset scan vs global-prefix differencing + Neumaier reference | **DO NOT BUILD the prefix family** | trap hits rel err **1.00**; safe repair is free yet the plain fold beats both 1.02-3.92x |
| `stream_compaction.c` | SIMD `mask`/`compound` | **SHIPPED in part** (4eed8a4) | branchless scalar = 95.6% on doubles; SIMD needed on 32-bit |
| `small_solve.c` | fixed-size LU/Cholesky, heap-free | **SHIPPED in part** (d99195f) | 13.3x…1.46x at n=2…8; **73-85% is removing malloc** |
| `small_solve_batched.c` | batched small solves, 4 layouts | **BUILD — gather, don't transpose (at one solve/matrix)** | 5.5-32x; transpose amortizes after 4.3-12.2 re-solves, never measured |
| `sym3_eigen.c` | symmetric 3x3 eigen, closed form vs Jacobi | **BUILD ONLY the gap-guarded variant** | 4.2x at Jacobi accuracy; the naive form returns duplicate eigenvectors |
| `sym3_gap_probe.c` | gap sweep across the `1e-6` guard, driving `sym3_eigen.c` itself | **BUILD the guard — the stated reason was wrong** | orthogonalization alone still duplicates **61.77%** on `A = cI`; the guard buys the residual |

## Instruments

| file | measures | what it established |
|---|---|---|
| `probe_chain_depth.c` | accumulators vs working set, NACC 1-16, double+float, divisible extents | knee at **5-6**; old probe's "12 regresses" was a power-of-two-tail artifact |
| `probe_bandwidth_roof.c` | single-core roofline | read 28.5-30.2 GB/s; correction 7 splits by direction |
| `probe_mlp.c` | MLP vs prefetch vs L1 traffic | **2.22x of the 3.32x blocking win is already in L1** |
| `jam_lane_model.cpp` | gcc's transposed R-lane jam vs an R-wide packed operand | knee is a **crossover**, not a shuffle ramp; packed arm reaches 0.34 cyc/MAC |
| `gram_jam_width.cpp` | the jam, R swept 2-12, emitter's exact text | fixed the tile width; **self-check passes vacuously on clang** |
| `gram_jam_cplx.cpp` | the jam on complex / float32 | complex peaks 1.23x, regresses at R>=6, not bitwise at R=2 — why complex declines |
| `small_p2.cpp` | emitted text over an R x p grid, literal extents | fixed R gets **41-100% of best**; governing variable is `p mod R` |
| `deadtile.cpp` | 51-sample p < R dead-tile cost | none: identical minima, identical `.exe` bytes |
| `gate_precision_probe.cpp` | k-ULP detection through `setprecision(15)` | 1 ULP seen 2.2-5.7% per cell, **99.8%** over 100 cells |
| `audit_corpus.py` | corpus round-capability census | **81.8% / 87.1%** of the two gate slices cannot round at all |

---

# What these established

**The jam** (fold inside the kernel body → serial chain; jam R output cells to
get R chains, bitwise, no licence — shipped). Three revisions: the prototype's
13.95x was sample-major (real shape 3.5x); the width is set by `p mod R`, not
the knee (a3837e6 derives it); and the licensed fold split, recorded at "1.00x",
is really **0.86-4.30x depending on n** — a gcc cost-model coin-flip, neither
useless nor reliable. The real lever is **layout**: pack `B` j-major (cost n·p,
amortized over m, malloc free from m=8) and the jam becomes broadcast-mul-add
with zero shuffles, still bitwise — **6.65x doubles / 10.6x floats**.

**Allocation is usually the dominant term — a prior, not a law.** Segmented fold
62-94%, small solve 73-85%, batched layout 1.19x vs 5.1x for de-heaping. But the
jam's gap was blamed on allocation and measured at **0.13x of it**, and the
packed gram's malloc is invisible. 3-of-5 is a heuristic: measure the split first.

**Threaded builds are bandwidth-bound** (~100 GB/s of operand traffic): the jam
is ~1.5x at 8 threads and R is noise. ILP and thread scaling are different
problems; never quote them as one number.

**What the gates can see.** Four blindnesses, descending: (1) **fixture** —
81.8% / 87.1% of the InterpDiff / DiffOracle slices cannot round; `symmetry` and
`reynolds` are 100% blind; (2) **contraction** — both gates pin
`BLADE_FP_CONTRACT=off` while users default to `fast`; (3) **opt-in** — both
gates default OFF; (4) **precision** — `setprecision(15)` sees 1 ULP 2.2-5.7%
per cell (99.8% per 100-cell array). `// EXPECT:` pins compare at 1e-9 relative
(~4.5M ULPs) and contribute nothing. Net: ~96/1340 InterpDiff tests would catch
a uniform 1-ULP change — blind per route, not globally.

---

# Corrections

Claims that did not survive contact. **(r2)** = revised by the second round.

1. **"Multi-accumulator captures ~half the former's win."** Zero for `double`
   (0.94-1.03x at every working set ≥ 128 KB, both compilers) — the nest is
   operand-supply-bound. Pays only L1-resident (1.37x gcc, **0.94x clang** — do
   not quote a magnitude), which is the condition KRS *creates*. **(r2)** `float`
   recovers 1.17x at 6.1 MB on both compilers; complex untested.

2. **"More accumulators regress past 8."** True for a reduction, false for a
   register-blocked outer product (6x8 beats 4x8 by 12-28% via operand reuse).
   **(r2)** Correction 16 does not undercut this: 2 and 3 were measured on
   intrinsics the vectorizer cannot reshape; 16 is about scalar source.

3. **"4 YMM = 16 chains."** Lanes are not chains. Measured: 0.690 cyc/elem at
   one accumulator (= the 3-cyc `vaddpd` latency), flat at NACC 5-6 (92% of
   best), 8 = 98%. **(r2)** "12 regresses" REFUTED — our own discipline bug:
   power-of-two buffers left 2048 mod 48 = 32 elements to a serial scalar tail.
   Vanishes at divisible extents; not spill (zero stack traffic at NACC 16).
   `probe_chain_depth.c` replaces the old probe (recover:
   `git show 87563fb^:src/microkernels/probe_accumulator_chains.c`).

4. **"The ragged tail amortizes to nothing."** 45.4% waste at n=61 — Blade's
   real comoment3 extent — and the `18.2/n` rule understates it 1.52x (it is
   asymptotic). Waste splits misalignment 36% / staircase 32% / padding 31%.
   **(r2)** The proposed fix (repack per `i`, "estimated 1.3-1.5x") measures
   **0.82-0.93x — a loss**, 0.89-0.95x even with packing free; origin-at-`i`
   trades head waste for tail padding (46-49% MORE lanes at n=96/192). The one
   estimated number in this file, and the estimate got the sign wrong.

5. **"Pairing two diagonal triangles into one dense square is impossible."**
   **(r2: possible, and built.)** Impossible only as ONE GEMM (a cell factors
   `rowop(a)*colop(b)`; the halves need different rows of the same panel) — a
   contraction-only statement; RFP works because bytes have no operand
   structure. Classifying register tiles pure-upper/pure-lower/straddling and
   blending the straddlers is bit-exact and makes the staircase O(B/MR), not
   O(B²/(MR·NR)). It still never pays: 1.03x at n=61 (diagonal = half the
   work), **-4.9% at n=337** — two operand panels per tile run slower. Removed
   at 58aec84; recover with `git show 58aec84^:src/microkernels/paired_triangle.c`.

6. **"Blocking amortizes byte traffic."** **(r2: it does — the first refutation
   counted the wrong cache.)** The R=4 win is **2.22x fully L1-resident** (MLP
   predicts ~1.0 there), and R=2 at L1 is **1.501x — the traffic model's own
   ceiling to three digits** (24 vs 16 B/cell of L1 traffic). DRAM adds a 1.24x
   latency-hiding residue: 1.50 x 1.24 = 1.86x measured. Random panel order
   costs 1.05x (not prefetch); prefetcher-defeated bandwidth is flat in stream
   count (no MLP lever). **Count L1 traffic, not DRAM traffic.**

7. **"35-45 GB/s achievable."** **(r2: split by direction.)** Reads: 28.5-30.2
   single-core, three methods; 35-45 is all-core. Writes: **35.3-42.6 GB/s on
   ONE core** with NT stores — the LFB story is read-side. RMW: 23.8 GB/s idle
   in the 16 B/elem accounting (the recorded 15-20 was a loaded box; the "~0.7
   ratio" compared 16 B/elem against 8 B/elem — an accounting artifact).

8. **"The mirrored read emits `vgather`."** By default neither compiler does on
   znver3 — **(r2)** but it is a *tuning* choice (`-mgather` emits it today),
   and the unlicensed default is fully scalar; the 10-op hand-rolled gather
   appears only when something forces vectorization. The tuning is right: the
   gather intrinsic loses 1.09-1.65x everywhere. What wins is neither: mirror
   indices satisfy `idx(j+1)-idx(j) = n-j-1`, so two adds regenerate the
   column — 1.30-1.34x at n >= 2003 (loses below n ~ 700).

9. **The fold is the wrong driver for the mirror transpose.** Summation is
   permutation-invariant, so the fold measures the transpose's cost, not its
   necessity; it is load-bearing only where orientation is observable
   (`decompact`, non-commutative mirror kernels). Round 2 found nothing to break.

10. **FMA contraction is bit-changing independent of reassociation — and the
    emission contracts.** A jam hand-writing `fma()` breaks the gates: true.
    **(r2)** Both derived rules were wrong. "gcc contracts at R >= 8, cap at 4"
    was a sample-major artifact (capped a release). "Zero `vfmadd` at any
    width" also false: gcc contracts the `n mod 4` scalar remainder (n=257 has
    `vfmadd` in both arms, n=260 none), so the nest is not bit-exact against
    strict mul-then-add and jam-vs-base holds only because both contract the
    same tail — which proves nothing about the interpreter. Complex at R=2
    reads bitwise NO. Bit-identity is a joint property of (text, compiler,
    element type, n mod 4); the corpus tests one cell.

11. **The tile shape must be derived from the OUTPUT EXTENTS.** Upheld; the
    shipped emitter was the counterexample until a3837e6. Fixed R=5 gets
    41-100% of best; worst cases (p=3,7,8,9) are exactly the corpus; **at p=8,
    R=4 is 83% faster than R=5**. Governing variable is `p mod R`. The rule —
    R=p for p<=10, else the largest divisor in [4..10], else 6 — is within 2%
    of best everywhere tested, and is what a3837e6 emits. Fixed width survives
    only on the runtime-extent path.

12. **The prefix-sum segmented fold is a trap, and not even fast.** Rel err
    **1.00** on a late short segment (Neumaier reference); bitwise-fine on the
    integer inputs it was verified with. **(r2)** The safe repair (reset scan)
    is free — bitwise vs the serial fold, 1.08-1.57x faster than the trap —
    but recovers nothing: the plain per-segment fold beats the whole prefix
    family 1.02-3.92x, by correction 15's own mechanism (G independent chains
    beat one long one).

13. **Batched small solves: gather, don't transpose.** Layout worth 1.05-1.31x,
    transpose costs more; SoA worse than BLK4 at n=8 (prefetcher exhaustion).
    **(r2)** Scope: measured at ONE solve per matrix; the transpose amortizes
    after 4.3-12.2 re-solves, and producer fusion was never measured. The
    vector arms are not bitwise at n >= 6 (83.9% of cells).

14. **The closed-form symmetric 3x3 eigen is unsafe as usually written.**
    Duplicate eigenvectors on degenerate input; degeneracy is ordinary in
    physics. **(r2)** The failure is continuous (orth err 2e-8 at gap 1e-4 —
    four decades wider than stated), and the repairs are not co-equal:
    orthogonalization holds orthogonality everywhere but **still duplicates
    61.77% on `A = cI`**; the guard is load-bearing and buys the residual.
    `1e-6` is a residual budget (~1e-10 worst-case), not a correctness
    boundary. "0% fallback on generic input" survives; the 4.2x vanishes on
    near-isotropic fields (100% fallback below gap 1e-7).

15. **The un-jammed baseline is NOT a serial chain.** OOO overlap of adjacent
    independent cells puts it at 2.44 cyc/MAC at n=257 — faster than its own
    3.0-cyc chain. Proved by control: cells seeded `prev - prev` sit at 3.03-
    3.13 at every n. **(r2)** The recovery is ~155 cyc/cell only mid-range —
    capped by the cell's serial length below n≈100, inflated past L2 — so the
    speedup PEAKS (1.86x @ n=35, 4.19x @ 1027, 4.02x @ 2051). **Quote a jam
    speedup with its fold length or not at all.** R=2 captures only 47%.

16. **The jam's knee is a CROSSOVER, and it belongs to the compiler.** gcc
    transposes the tile into R-lane `vaddpd`. **(r2)** Shuffles do not bind —
    shuffle/MAC is FLAT (1.125-1.25 across R=4..12), a floor not a ramp; the
    recurrence is 12 cyc at every width, so the chain bound is 3/R and falls:
    `cyc/MAC = max(3/R, ~0.6 floor, 0.25 load)`, knee where 3/R meets the
    floor. Consequences: packing the operand removes the floor (1.12x at R=4
    where the chain binds, **1.80x at R=8, 2.22x at R=12**, to 0.34 cyc/MAC,
    bitwise); **do not hand-roll intrinsics** (matches plain C exactly);
    explicit mul+add beats explicit FMA on Zen 3 (4.01x vs 3.69x — different
    pipes), so bitwise costs only the function hoist for
    `__attribute__((optimize("fp-contract=off")))` (`#pragma STDC FP_CONTRACT`
    is ignored by g++; clang needs its own pragma). The knee is gcc's: clang
    (no transpose, FMA chains at 4/R) is still improving at R=12, where R=5
    leaves 28-36%. `-fno-tree-vectorize` wins 35% at R=12 but every width
    reads bitwise NO — **the transpose is what keeps the jam byte-exact**.

17. **The jam's coverage hole was SHAPE, not rounding.** Every corpus gram had
    p in {2,3,4}, so at R=5 the tile guard was statically false — **the jammed
    body was unreachable in 100% of the corpus**, and widening 4→5 removed the
    one tile that fired. Fixed by `tests/corpus/math/063` (p=13: tile and
    remainder both run) and a3837e6 (every site's tile fires). Any kernel
    claiming "bitwise" on small-integer or dyadic inputs is claiming nothing —
    including `packed_symv.c` and `multiplicity_fold.c` here.

18. **SEVEN native arms are unreachable in a default user environment.**
    `OPENBLAS_DIR` alone enables the tier and `lapackAvailable()` rides it, so
    the shim takes `gram` (both), `matmul`, `dot`/`gemv` (unless `omp`),
    `solve`, `eigh` — including two shipped optimizations (the jam, the
    heap-free solve). The suite sees them only because `dispatchTest` clears
    the gate. Bench fixtures must pin `BLADE_BLAS=0`; a native-arm speedup
    describes a configuration, not a default.

19. **A ratio measured on a loaded box is not a ratio.** 13.2x re-measured idle
    is 3.14x. Contention taxes a DRAM-bound baseline far more than a
    register-blocked numerator — systematically inflating exactly the
    comparisons these kernels make. Quote cyc/MAC, calibrate in-process,
    re-measure anything whose baseline touches memory.

20. **Verify bitwise against the code you are REPLACING.** The blocked rank-k
    is bitwise vs its own `fma()` reference and differs from the emitted nest
    on every cell (6.6e-16): gcc does not contract that nest's body. Closing
    the gap costs 17% and requires defeating the flag — at `=fast` gcc
    re-fuses explicit mul+add INTRINSICS back into `vfmadd231pd`. gcc has the
    per-function attribute; **clang has no per-function escape** (its flag
    overrides the pragma) — a byte-exact kernel there needs the whole TU at
    `-ffp-contract=off`. The emitted nest is also not uniform with itself
    (contracted `k mod 4` remainder), so byte-exactness holds only at
    multiples of 4.

From the small-solve batch: **branchless pivoting failed twice** (gcc emits
branches for `sw ? a : b`; a bitmask select was 2x worse — domain crossings).
Use Cholesky on SPD input, no pivoting, 1.6x faster at n=3,4. **Specialization
stops paying at n ~ 8-10** (3.4x at n=2, 1.12x at n=8 vs a no-heap runtime-n
loop). Fixing `n` at compile time is bitwise (955/955, genuine full-mantissa
result); switching LU→Cholesky is not (2.9%) and must be a visible choice.

---

# Three properties worth preserving in any emitter

**Bitwise exactness is achievable, but the evidence is thinner than the kernel
count.** Only `krs_former.c` and `packed_syr_syrk.c` verify on operands that can
round (`packed_symv.c` memcmps integers; `multiplicity_fold.c` is integer/`max`
by design; `mirror_transpose.cpp` moves bytes; `small_solve.c`'s 955/955 is
genuine). Three invariants, not two: sample loop innermost and ascending; never
fold a scale factor into a pre-multiplied operand; and **reproduce the
reference's CONTRACTION pattern** — a property of the compiler and trip count,
not of your source (correction 20).

**Packed-row alignment is a choice, not an impossibility.** Natural pitch leaves
at most 1/4 of rows 32-byte aligned, and misalignment costs 1.68x at L1 — the
ADDRESS costs, not the instruction. Padding row starts to 4 doubles buys
1.11-1.21x for <1% memory, but breaks the BLAS-standard layout and `rowbase(i)`;
it belongs in the layout decision, not load selection.

**Operand ORIENTATION is a hidden premise until stated.** It cost two headlines:
13.95x (jam) and 13.2x (rank-k) were both sample-major; Blade emits the
contracted axis contiguous. Re-measured: 3.5x and 6.80x (5.80x byte-exact) — the
latter an increment of 1.64x over the shipped jam at n=2003, and a **loss** at
n=61. `krs_former.c` packs inside its timed region and carries no such premise;
the contrast is the thing to notice.

---

# Building and running

```
export PATH="/c/msys64/ucrt64/bin:$PATH"
gcc -O3 -march=native -ffp-contract=fast -o krs.exe krs_former.c -lm
g++ -O3 -march=native -ffp-contract=fast -std=c++17 -o gjw.exe gram_jam_width.cpp
./gjw.exe 5 3
```

Each file documents its arms and CLI in its header; `.cpp` needs `-std=c++17`.
`clangtimer.h` papers over clang64's missing `clock_gettime64`. Shapes are
compile-time (`-DMM=301 -DNN=257 -DPP=303`); sweep `NN` for correction 15, `PP`
for correction 11, and keep extents non-power-of-two — correction 3 is what
ignoring that discipline looks like.

**Two self-checks that are not optional.** The bitwise column is live only on
operands that can round: `gram_jam_width.cpp` defaults to full-mantissa and
keeps `-DDYADIC` to demonstrate the trap (under it, even the explicit-`fma`
control reads `yes`). So **`base_fma` must read `NO`** — and that check fails
SILENTLY on clang, which contracts the reference too (control and baseline
become the same program). Distinguish by disassembling the reference for
`vfmadd` or diffing against a `-ffp-contract=off` build.

**Timing hygiene.** These kernels are pure with loop-invariant arguments; a
compiler will hoist the whole call out of a repetition loop. Bracket timed
regions with `asm volatile("" ::: "memory")` AND read results into a printed
checksum INSIDE the repetition loop — the barrier alone is not enough; gcc will
CSE pure calls across reps and report ~0 ns.
