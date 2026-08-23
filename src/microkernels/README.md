# Triangular microkernels — measured prototypes, and what they refuted

**Status: REFERENCE IMPLEMENTATIONS, not wired into the build.** Nothing here is
compiled by `Blade.fsproj`, called by either emitter, or covered by `blade test`.
Each file is a standalone, self-verifying C/C++ program: build it, run it, and it
checks itself against an independent oracle and prints its own timings. They exist
so that a future emitter has a *measured* target to reproduce rather than a design
sketch to guess at.

**Four findings have graduated into the compiler** — the output-axis jam
(ae951eb, widened to 5 in 272e9be, then made extent-derived in a3837e6),
`group_by`'s single CSR pool (e83f5d8), branchless `compound` compaction
(4eed8a4), and the heap-free small solve (d99195f). Their rows below say so.

This document has been through **two** rounds of contact with a compiler: the
kernels were built to test the design, and then a second pass was built to attack
the kernels. Both rounds are recorded, because several corrections in the first
round were themselves wrong, and an entry reading "X was refuted, and the
refutation was refuted" is the honest state of that question. Where a claim now
has a *narrower* scope than it was first written with, the scope is stated — a
claim that holds only on gcc, only on `double`, or only when `p >> R` is a useful
claim, but not the one it was originally taken for.

Design context: `docs/plans/plan-rank-r-former.md` (the KRS schedule),
`docs/plans/plan-simplex-blocked-compute.md` §0a-§0d, and
`docs/plans/plan-unroll-and-jam.md` (the jam, including what shipped).
`SURVEY.md` is the optimization-site census that produced the second batch.

## Measurement conditions — read before quoting any number

Ryzen 7 5800HS (Zen 3, AVX2, 16 YMM, 2 FMA units, DDR4-3200 dual channel),
gcc 15.2 and clang 22.1.8 (MSYS2 ucrt64), `-O3 -march=native -ffp-contract=fast`
(= `Build.fs optFlags()`), single-threaded.

**Box load is a first-class variable here, not a footnote.** The 2026-08-21 batch
was measured while five sibling jobs compiled on the same box; an in-process FMA
probe read 3.22 GHz against ~4.0 idle. That is not merely "absolute figures are
depressed" — **a ratio between a bandwidth-bound baseline and a register-blocked
numerator INFLATES under contention**, and one headline in this file (13.2x, now
3.14x) was produced that way. See correction 19.

The 2026-08-22 kernels were measured idle, under a mutex, with the core clock
calibrated **in-process** by a dependent `vaddsd` chain, and quoted in **cyc/MAC**
where possible — the form that survives both clock drift and a different machine.
Prefer it when adding kernels.

## The kernels

| file | what it is | verdict | headline |
|---|---|---|---|
| `krs_former.c` | rank-3 symmetric former, Khatri-Rao Simplex schedule | **BUILD** | **72.6% of FMA peak**; 2.34x at Blade's 61x2003 shape, 15.1x at 256x1024 |
| `packed_syr_syrk.c` | packed symmetric rank-1 (`syr`) and blocked rank-k | **BUILD extent-gated — headline REVISED** | the 13.2x was a loaded box against a baseline Blade never emits; see `syrk_orientation.c` for the number that transfers |
| `syrk_orientation.c` | the same contraction in **both** operand orientations, 11 arms, with the nest `materializeGramForm` actually emits as the reference | **BUILD `CC_xpose+blocked MULADD`, extent-gated** | **5.80x over the emitted nest and BITWISE** (n=2003, k=256; 9.19x clang) — but **loses to the shipped jam at n=61**, Blade's real comoment extent |
| `packed_symv.c` | packed symmetric matvec, fused dot+axpy | **BUILD** | **2.4x** over *optimized* dense, at 94% of the measured roof |
| `multiplicity_fold.c` | full-domain triangular fold via multiplicity classes | **BUILD when the fold refusal lifts** | 11-20x over the n^2 walk; its 63/63 "bitwise" is an integer-only addressing check, not an FP-safety one |
| `mirror_transpose.cpp` | 4x4 register transpose serving both mirror orientations | **BUILD for `decompact` only** | 1.3x and a `memcmp`-identical drop-in; the fold seam is the wrong one |
| `gather_probe.c` | the mirrored read three ways: compiler emission, hardware gather, **address recurrence** | **BUILD the recurrence, never the gather** | AVX2 gather **loses** 1.09-1.65x; the index-free recurrence **wins 1.30-1.34x** at n >= 2003 |
| `align_probe.c` | padded-aligned packed rows vs natural pitch, with a power-of-two control | **BUILD if the packed layout is Blade's to choose** | **1.11-1.21x for 0.05-0.97% memory**; the cost is the ADDRESS, not the instruction |
| `unroll_and_jam.c` | the original jam prototype | **SHIPPED, and superseded as evidence** | its **13.95x is SAMPLE-MAJOR and does not transfer**; the real shape gives 3.5x — see `gram_jam_width.cpp` |
| `gram_jam_vec.cpp` | the jam against a **j-major packed** operand, plus an intrinsics control | **BUILD — the next emitter target** | **6.65x on doubles, 10.6x on floats**, where the shipped jam gets 3.5x / 3.0x; hand-written AVX2 matches plain C to noise |
| `gram_iblock.cpp` | IB x R two-dimensional blocking, no packing | **BUILD for the small-`p` arm only** | +4% at p=303 (the shuffle wall) but **2.2x at p=8**; pack allocation measures free from m=8 |
| `krs_repack.c` | correction 4's per-`i` repack, measured three ways including a free-packing upper bound | **DO NOT BUILD** | **0.82-0.93x** at every shape on both compilers; **0.89-0.95x even with the packing free** |
| `segmented_fold.c` | fused segmented reduction (`group_by` + `reduce`) | **SHIPPED in part** (e83f5d8: the CSR pool) | 19-37x total — of which **one CSR pool instead of G mallocs is 62-94%** |
| `segmented_scan_safe.c` | segmented reset scan vs global-prefix differencing, 6 arms + a long-double Neumaier reference | **DO NOT BUILD the prefix family — now measured, not argued** | the trap reaches **relative error 1.00**; the safe repair is *free* (1.08-1.57x faster than the trap) yet the plain per-segment fold beats both by **1.02-3.92x** |
| `stream_compaction.c` | SIMD `mask`/`compound` (the WHERE idiom) | **SHIPPED in part** (4eed8a4: branchless scalar) | branchless scalar captures **95.6%** on doubles; SIMD is required on 32-bit (1.4-2.6x more) |
| `small_solve.c` | fixed-size LU / Cholesky, heap-free | **SHIPPED in part** (d99195f: the stack buffer) | 13.3x/3.4x/3.0x/1.86x/1.46x at n=2/3/4/6/8 — **73-85% is just removing malloc** |
| `small_solve_batched.c` | batched small solves, 4 layouts | **BUILD — gather, do NOT transpose (at one solve per matrix)** | 5.5-32x; but the transpose amortizes after **4.3-12.2 re-solves**, which the kernel never measured |
| `sym3_eigen.c` | symmetric 3x3 eigen, closed form vs Jacobi | **BUILD ONLY the gap-guarded variant** | 4.2x at Jacobi accuracy — the naive closed form returns **duplicate eigenvectors** |
| `sym3_gap_probe.c` | continuous gap sweep across the shipped `1e-6` guard, driving `sym3_eigen.c`'s own functions | **BUILD the guard — but the stated reason for it was wrong** | structural orthogonalization *alone* still returns duplicates **61.77%** of the time on `A = cI`; the guard buys the **residual**, not orthogonality |

## Instruments

Not candidates to build — they exist to answer "which resource binds" and to keep
the claims above honest.

| file | measures | what it established |
|---|---|---|
| `probe_chain_depth.c` | accumulator count vs working set, NACC 1-16, `double` and `float`, at power-of-two **and** divisible extents | knee located at **5-6**; the old probe's "12 regresses" is a power-of-two-tail artifact |
| `probe_bandwidth_roof.c` | this machine's single-core roofline | read 28.5-30.2 GB/s — but see correction 7, which splits it by direction |
| `probe_mlp.c` | memory-level parallelism vs prefetch vs L1 traffic | **2.22x of the 3.32x blocking win is already in L1**; stream count buys nothing with the prefetcher off |
| `jam_lane_model.cpp` | why the jam has a knee: gcc's transposed R-lane form against an R-wide **packed** operand | the knee is a **crossover**, not a shuffle ramp; the packed arm reaches **0.34 cyc/MAC** |
| `gram_jam_width.cpp` | the shipped jam, R swept 2-12, against the emitter's exact text | fixes the tile width; **its self-check passes vacuously on clang** |
| `gram_jam_cplx.cpp` | the shipped jam on `complex<double>` / `complex<float>` | complex peaks at **1.23x**, **regresses at R>=6**, and is **not bitwise at R=2** — why complex no longer jams |
| `small_p2.cpp` | the emitted text over an R x p grid with literal-baked extents | a fixed R gets **41-100%** of the best available width; the governing variable is `p mod R` |
| `deadtile.cpp` | 51-sample check that a `p < R` tile costs nothing | no regression: identical minima, identical `.exe` bytes |
| `gate_precision_probe.cpp` | k-ULP detection through the emitted `setprecision(15)` stream | 1 ULP seen **2.2-5.7%** per cell, but **99.8%** over a 100-cell array |
| `audit_corpus.py` | corpus round-capability census | **81.8% / 87.1%** of the two byte-exactness gate slices cannot round at all |

---

# What these established

## The jam: three transforms, and the one that matters is layout

Blade's surface puts every contraction's fold INSIDE the kernel body, so the
innermost emitted loop is a serial dependent chain while the independent OUTPUT
axis sits outside it contributing nothing. Jamming that axis — R output cells at
once, R accumulators, one shared fold — gives R independent chains **without
changing any cell's summation order**. It is bitwise, needs no licence, and it
shipped.

Three things about it are not what the first round concluded.

**The prototype's 13.95x does not transfer.** It measured sample-major operands;
Blade's emitted shape has the contracted axis contiguous, so the jam buys ILP and
operand reuse but not cross-cell SIMD. On the real shape it is **3.5x**.

**The width is set by `p mod R`, not by the knee.** A tile that does not divide
the output extent leaves its remainder to the un-jammed body at base speed, and at
small `p` that remainder *is* the work. A fixed R=5 delivers 41-100% of the best
available width, and its worst cases — p = 3, 7, 8, 9 — are exactly the extents
this corpus has. The emitter now derives R from a literal extent (a3837e6); see
correction 11.

**The licensed fold split is not the complement it was recorded as.** It was
written down at "1.00x on this shape". Re-measured across fold lengths it is
**0.86x to 4.30x** — 4.30x at the very shape where 1.00x was recorded — because
whether gcc collapses the named lanes into one YMM is a cost-model decision.
Neither "it does nothing" nor "it is the only lever" is true; it is unstable, and
that is the honest entry.

**And the real lever is none of these.** Pack `B` j-major once per gram (cost
`n*p`, amortised over `m`; its `malloc` measures free from m=8 up) and R
consecutive output cells become R *contiguous* elements: broadcast, multiply, add,
**zero shuffles**, each lane still summing one cell in ascending k and therefore
still bitwise. That is **6.65x on doubles and 10.6x on floats** against the
un-jammed nest. Removing the compiler's *reason* to shuffle is worth 1.9-4.0x;
beating its shuffling by hand is worth nothing (correction 16).

## Allocation is usually the dominant term — but it is a prior, not a law

Three kernels decompose that way: the segmented fold is 62-94% allocator, the
small solve 73-85%, and the batched solve's layout change is worth only 1.19x
against 5.1x for removing the heap. Consistent with
`plan-simplex-blocked-compute.md` §0c, which found ~55% of a benchmarked program
was allocator and first-touch.

**The dense-gram jam is the counterexample, and it was mispredicted on exactly
this prior.** Allocation was the leading hypothesis for why the shipped jam
underperformed its prediction; measured, it is worth **0.13x of the gap** (setup
0.30 ms, output alloc 7 us, first-touch 0.1 ms; nest-only 2.93x against
whole-program 2.80x). The real cause was correction 15. The packed-operand kernel
is a second counterexample: its packing `malloc` inside the timed region is
invisible from m=8 upward. A heuristic with a 3-of-5 hit rate is still a
heuristic — measure the split before optimizing the allocator.

## Threaded builds change which knob binds

At 8 threads the jam is ~1.5x and R is noise, against ~100 GB/s of operand
traffic — bandwidth-bound, where more accumulators buy nothing and only
blocking/packing would. Single-thread and multi-thread are different problems and
should stop being quoted as one number.

## What the gates can actually see

Two gates compare bits at all, and **the numerical blindness first recorded here
(a 15-digit stdout compare) is the weakest of four reasons**, in descending order
of importance:

1. **Fixture** — 81.8% of the `InterpDiff` slice and 87.1% of the `DiffOracle`
   slice contain no operation that can round. `symmetry` and `reynolds` are 100%
   blind.
2. **Contraction** — both gates PIN `BLADE_FP_CONTRACT=off` while the user default
   is `fast`, so FMA placement is excluded by construction.
3. **Opt-in** — both gates default OFF, so a plain `blade test` performs no
   byte-exactness checking at all.
4. **Precision** — `setprecision(15)` against a double's 17 round-trip digits. A
   1-ULP change is seen 2.2-5.7% of the time per cell, but **99.8%** over a
   100-cell array.

`// EXPECT:` pins contribute nothing at any layer: they compare at relative
tolerance 1e-9, roughly 4.5 million ULPs. Under a uniform 1-ULP perturbation about
96 of 1340 `InterpDiff` tests would fail — **the suite is not globally blind, it
is blind per route.**

---

# Corrections

Claims that did not survive contact with a compiler. Entries marked **(round 2)**
are corrections to corrections.

1. **"Multi-accumulator alone captures ~half the former's win."** For `double` it
   captures ZERO (1.02x / 0.97x / 0.86x — a net loss at the largest shape). The
   naive former nest is **operand-supply-bound**, not FMA-latency-bound: three
   scattered loads per FMA, all of `A` re-streamed per output cell. Reproduced by
   `probe_chain_depth.c` section B at 0.94-1.03x for every working set from 128 KB
   to 6.1 MB, on gcc and clang alike. It pays only when operands are L1-resident
   (1.37x at 18 KB on gcc — but **0.94x on clang**, so do not quote a magnitude),
   which is exactly the condition KRS *creates* by packing plus register tiling;
   only then do the chains matter. **(round 2)** This is a `double` result: the
   same nest in `float` recovers **1.17x at a 6.1 MB working set on both
   compilers**, because halving the operand bytes relieves enough supply pressure
   for the chains to bind. Complex is untested.

2. **"More accumulators regress past 8."** True for a pure *reduction*, false for a
   register-blocked *outer product*, where accumulator count buys operand reuse
   (1.50 vs 1.33 FMA-per-operand): 6x8 beats 4x8 by 12-28%. The shape decides, not
   a universal number. **(round 2)** Correction 16 does not undercut this: 2 was
   measured on **intrinsics**, which the vectorizer cannot reshape (disassembly
   confirms `micro_6x8`/`micro_4x8` emit literally). 16 is about what gcc does to
   *scalar source*; 2 and 3 are about what the hardware does to *hand-written
   vector chains*. A reader who conflates them will wrongly conclude 2 and 3 were
   measuring compiler artifacts.

3. **"4 YMM = 16 independent chains."** Lanes are not chains. The 4 lanes in one
   YMM advance together inside one instruction; a chain is per architectural
   register. Saturation needs ~6 (3-cycle latency x 2 pipes) — now measured rather
   than modelled: a flat L1 fold reads 0.690 cyc/elem at one accumulator (exactly
   the 3-cycle `vaddpd` latency) and flattens at NACC **5-6**, which is 92% of the
   eventual best; 8 is 98%, and past 8 nothing changes. Round to 8 and stop.
   **(round 2)** The companion datum "12 regresses" is **REFUTED, and it was our
   own benchmark-discipline bug**: `probe_accumulator_chains.c` sized buffers at
   exactly 2^k doubles, and a 12-wide loop consumes 48 per iteration, so
   2048 mod 48 = 32 elements fell to a strictly dependent scalar tail. The
   regression vanishes at a divisible extent on both compilers and is dramatic on
   `float` (44% -> 0%). Not spill — disassembly shows zero stack traffic at NACC 12
   or 16. CLAUDE.md forbids power-of-two extents for exactly this reason, and the
   instrument this file quoted violated it, producing a false architectural
   conclusion that correction 2 then had to argue against. `probe_chain_depth.c`
   replaces it; the flawed instrument was removed at the same commit, recoverable
   with `git show <that commit>^:src/microkernels/probe_accumulator_chains.c`.

4. **"The ragged tail amortizes to nothing."** Waste is 7.1% at n=256 but **45.4%
   at n=61, which is Blade's actual comoment3 extent** — and the convenient
   `~18.2/n` rule that reproduces the first number understates the second by 1.52x,
   because it is asymptotic and n=61 is not. The waste splits three ways at n=61
   (panel misalignment 36%, diagonal staircase 32%, tail padding 31%), so no single
   term dominates; misalignment reaches 55% only at n ≡ 0 (mod 8), where tail
   padding vanishes. **(round 2) The fix once proposed here — repacking `A` per `i`
   with panel origin at `k = i`, "worth an estimated further 1.3-1.5x" — was
   measured and is a LOSS**: 0.82x at 61x2003, 0.93x at 97x1009, 0.92x at 257x257
   on gcc, 0.85x on clang, and **0.89-0.95x even with the repacking made free**,
   which is the mechanism-only upper bound. Origin-at-`i` does not even remove the
   misalignment it names: row blocks step by MR=6 against NR=8 panels, so the phase
   is `6b mod 8`, and it merely trades head waste for tail padding — at n=96 and
   n=192 it wastes 46-49% MORE lanes than the shipped origin-0 packing. Perfect
   head alignment caps the arithmetic saving at 1.11x at n=61 and 1.00x at
   n=96/192, and measures 0.82-0.86x because it costs the kernel its single hot
   4 KB packed working set. **This was the one number in this file that was
   estimated rather than measured, and estimating it got the sign wrong.**

5. **"Pair two diagonal triangles into one dense square."** **(round 2: POSSIBLE,
   and it was built.)** What is impossible is *one* GEMM over the paired square: a
   GEMM cell factors as `rowop(a)*colop(b)`, and the upper half needs `A[t][i0+a]`
   in row `a` while the lower half needs `A[t][i1+a]` in the same row, so no single
   pair of packed panels serves both. That is a statement about CONTRACTIONS — it
   does not cover elementwise binops, where each cell reads its own two operands,
   nor storage, where RFP works because placing bytes has no operand structure. The
   escape is to classify each MR x NR register tile as pure-upper, pure-lower or
   straddling, feed the first two from different panels, and compute the straddlers
   twice and blend. `paired_triangle.c` did exactly that and is **bit-exact at
   every extent tested**, including n=61 d=2003, and it makes the staircase cost
   `O(B/MR)` tiles instead of `O(B^2/(MR*NR))` cells — so "the staircase is
   unavoidable" was also wrong. It still does not pay: the diagonal pass alone is
   1.06-1.63x, and end-to-end it is **1.03x at n=61 where the diagonal is HALF the
   work**, 1.01x at n=128, and a **4.9% LOSS at n=337**, because two operand panels
   feeding one tile run at lower GFLOP/s. The lesson is not that the geometry is
   impossible; it is that the diagonal pass is never expensive enough to justify a
   second operand stream — at large n because it is a small share, at small n
   because the share is large but the absolute work is tiny. `paired_triangle.c`
   was removed at 58aec84 and is the only evidence for this entry; recover it with
   `git show 58aec84^:src/microkernels/paired_triangle.c`.

6. **"Blocking amortizes byte traffic."** **(round 2: it does — the first
   refutation was itself wrong, because it counted the wrong cache.)** The original
   argument was that measured row-blocking (1.59-1.71x) EXCEEDS its own traffic
   model's 1.50x ceiling, so the model cannot be the mechanism and the win must be
   memory-level parallelism. But across four working sets the R=4 win is **2.22x
   with the entire problem resident in L1** (clang 2.01x) and only 3.32x at DRAM —
   two thirds of it exists with zero cache misses, where MLP predicts ~1.0x. At L1
   the R=2 win is **1.501x**: the traffic ceiling to three digits, because the model
   is exactly right about **L1** traffic (24 B/cell unblocked against 16 B/cell at
   R=2 — `y[j]` is loaded and stored once per R rows instead of once per row). The
   DRAM figure exceeds it by a further 1.24x, and that residue is latency hiding:
   **1.50x traffic x 1.24x latency = 1.86x.** Two controls rule out the
   alternatives: visiting row panels in RANDOM order costs only 1.05x and leaves
   the win intact (3.075x -> 3.039x), so it is not the prefetcher; and with the
   prefetcher defeated, read bandwidth is FLAT in stream count (1.00x -> 1.19x over
   R=1..16), so "R streams give R outstanding misses" is not a lever that exists.
   **Count L1 traffic, not DRAM traffic.**

7. **"35-45 GB/s achievable."** **(round 2: split it by direction.)** **Reads**:
   one Zen 3 core tops out at **28.5-30.2 GB/s**, confirmed three ways
   (8-accumulator 27.3, best multi-stream 28.6 at R=3, `probe_bandwidth_roof.c`
   30.2), and 35-45 really is the all-core aggregate for that direction.
   **Writes**: a single core reaches **35.3 GB/s (gcc) / 42.6 (clang)** with
   non-temporal stores — above its own read roof and inside the band declared
   all-core-only, so the line-fill-buffer story is a read-side story. **RMW**: on an
   idle box a same-array read-modify-write sustains **23.8 GB/s** in the
   16 B/element accounting these kernels use (28.3 counting the read-for-ownership,
   which is real DRAM traffic the 16 B figure omits). The previously recorded
   15-20 GB/s was a loaded-box measurement, and the "~0.7 ratio is what RMW should
   cost" compared a 16 B/element figure against an 8 B/element one — an accounting
   artifact, not a cost. Per ELEMENT an RMW costs about 3x a read; per LINE, reads
   and RMW run within 20% of each other, which is what an LFB-limited core should
   do.

8. **"The mirrored read emits `vgather`."** Two separable claims; only one stands.
   By default neither gcc 15.2 nor clang 22.1.8 emits any `vgather` on znver3 — but
   **(round 2)** "at all" is too strong: gcc's documented `-mgather`
   (`-mtune-ctrl=use_gather`) emits `vgatherdpd`/`vgatherqpd` on znver3 today, and
   `-march=haswell` does so with no extra flag, so this is a *tuning* decision, not
   an ISA one. Nor is the default emission a hand-rolled vector gather: without a
   vectorization licence gcc emits a **fully scalar** loop (`movslq` + `vaddsd`).
   The ~10-op hand-rolled sequence appears only once something FORCES vectorization
   — exactly what `mirror_transpose.cpp`'s `#pragma omp simd` arm does. The tuning
   decision is correct: an explicit `_mm256_i32gather_pd` is **1.09-1.65x SLOWER**
   than the hand-rolled sequence at every working set on both compilers, and the
   64-bit-index form is worse. What beats both is neither: for a packed triangle
   the mirror-column indices satisfy `idx(j+1) - idx(j) = n-j-1`, so the addresses
   regenerate with two adds and no index array — **1.30-1.34x faster than the
   hand-rolled gather at n >= 2003** (slower below n ~ 700, where the serial
   address chain is not worth it).

9. **The fold is the wrong driver for the mirror transpose.** For a pure sum the
   transposed operand contributes the same total (summation is
   permutation-invariant), so the transpose is provably dead and a sufficiently
   clever compiler may delete it. The fold measures the transpose's COST, not its
   NECESSITY. It is load-bearing only where the mirrored orientation is
   positionally observable — `decompact`, and mirror kernels not commutative in
   their two arguments. Round 2 found nothing to break this.

10. **FMA contraction is a bit-changing transform independent of reassociation —
    and the emission contracts, so the jam's bit-identity is an accident, not a
    construction.** The true half: contraction changes bits without reassociating,
    so a jam that hand-writes `fma()` breaks the byte-exact gates. **(round 2)** Two
    things around it were wrong. The derived rule — "gcc contracts a jammed body at
    R >= 8, so cap the tile at 4" — is a sample-major prototype artifact that does
    not reproduce on the shipped emission at any width to 16; believing it capped a
    release at R=4. But the reassurance that replaced it ("zero `vfmadd*` at any
    width") is *also* wrong: gcc vectorizes the reference nest's multiply and
    serializes its adds — which is why the k-loop body shows no `vfmadd` — while the
    vectorized loop's own `n mod 4` scalar remainder IS contracted. At the shipped
    fold length n=257 the emitted binaries carry `vfmadd` in both arms (base 3, jam
    5, including one packed `vfmadd231pd` the base does not have); at n=260 the
    count is zero everywhere. So the emitted gram is not bit-exact against a strict
    mul-then-add reference, and *whether it is* depends on `n mod 4`. Jam-vs-base
    survives because both contract the same tail — but a property proved by
    comparing two compiled arms to each other says nothing about the
    **interpreter**, which never contracts. Nor is the property structural: on
    `complex<double>`, same emitter text, same flags, **R=2 reads bitwise NO**.
    Bit-identity here is a joint property of (text, compiler, element type,
    `n mod 4`); the corpus tests one cell of that grid. (The tuning's own reason
    still holds: the fused single-accumulator form is *slower*, 3.14 cyc/MAC
    against 2.44.)

11. **The tile shape must be derived from the OUTPUT EXTENTS, not fixed** — and
    **(round 2)** the shipped emitter was the counterexample until a3837e6. A 4x8
    tile on a 6-row output covers rows 0-3 and sends 33% of the work to a scalar
    tail (2.18x, where a 2x8 tile dividing 6 exactly gets **12.63x on identical
    data**), and `n < R_j` kills the jam outright. What was over-read from p=303 —
    where a 3-cell remainder costs 1.4% — was that "fixed R plus a scalar tail is
    fine". It is fine *when `p >> R`*. Swept over p=3..40 at m=4001, n=257, a fixed
    R=5 delivers **41-100% of the best available R**, and its worst cases —
    p=3 (41%), p=9 (42%), p=8 (44%), p=7 (51%) — are exactly this corpus, whose
    largest gram extent is 8. **At p=8, R=4 is 83% faster than R=5.** The governing
    variable is `p mod R`: every extent that is a multiple of 5 scores 100%, every
    other loses its remainder cells to the reference body at base speed. No masking
    machinery is needed — only choosing R. `R = p` for `p <= 10`, else the largest
    divisor of `p` in [4..10], else the crossover value 6, is within 2% of the
    measured best at every extent tested. That is what a3837e6 emits; the fixed
    width survives only on the runtime-extent path, where nothing can be derived.

12. **The prefix-sum segmented fold is a trap, and it is not even fast.**
    `out[g] = S[off[g+1]] - S[off[g]]` vectorizes beautifully and is
    bitwise-correct on the small-integer inputs these kernels verify with. Against
    a long-double Neumaier reference it reaches **relative error 1.00** on a late
    short segment, and 4e-11 even on uniform data at G=1021: the differencing
    cancels against the *global* prefix. **(round 2)** The standard repair exists
    and is free — a SEGMENTED (reset) scan re-zeroes the running sum at each head,
    so a segment's last element already holds its sum and nothing is subtracted.
    Its scalar form is **bitwise identical** to the serial per-segment fold (so it
    needs no `BLADE_FP_REASSOC`), and its 4-lane form is **1.08-1.57x faster** than
    the global-prefix scan, which must materialize `S`. But the repair recovers
    nothing, because there was no win: at segment lengths from 2 to 62,000 the plain
    per-segment fold Blade already emits beats the best prefix-family arm by
    **1.02-3.92x**. A global prefix is ONE serial chain of length N; per-segment
    folds are G independent chains the out-of-order window overlaps for free —
    correction 15's mechanism, reproduced in a second kernel.

13. **Batched small solves: the win is SIMD, not layout — and transposing costs
    more than it saves.** Converting AoS to interleaved BLK4 costs 3.6-51.6 ns per
    system while the layout itself is worth only 1.05-1.31x, so gather-into-
    registers beats transpose-the-batch at every size tested. Interleave only if the
    array is *born* interleaved. Secondary: full SoA is *worse* than BLK4 at n=8
    (83.8 vs 48.1 ns) — `n*n` separate streams exhaust the prefetchers.
    **(round 2)** "Every size tested" means every size **at one solve per matrix**:
    the transpose is called once *outside* the timed region and timed separately.
    From the kernel's own B=65536 numbers it amortizes after **4.3-12.2 re-solves**
    (n=2: 12.2, n=3: 4.3, n=4: 6.9, n=6: 12.1, n=8: 4.7), and fusing it into the
    producer was never measured — which is the case the entry's own advice
    ("interleave only if born interleaved") points at. Also unmentioned: the vector
    arms are **not bitwise at n >= 6** (83.9% of cells match).

14. **The closed-form symmetric 3x3 eigendecomposition is not safe as usually
    written.** Independent cross products give **orthogonality error 1.00** on
    degenerate input — two returned eigenvectors are the same vector — with
    residuals to 3e-2, and degenerate 3x3 tensors are ordinary in physics
    (isotropic stress, `A = cI`). **(round 2)** The failure is *continuous*, not
    confined to degeneracy: orthogonality error is 2.0e-8 at gap 1e-4 and 2.3e-4 at
    1e-6, so the affected region is four decades wider than stated. And the two
    repairs are not co-equal, nor is the causal story the one given. Structural
    orthogonalization holds orthogonality at 5e-16 across every gap *including*
    exact degeneracy, and the gap guard contributes nothing to it; what the guard
    buys is the **residual**, which the closed form degrades to 3e-9 without it.
    `1e-6 max|lambda|` is therefore a residual BUDGET, not a correctness boundary:
    degradation is a smooth ~eps/gap, so the threshold accepts a worst-case residual
    of ~1e-10 and returns 6e-12 just above itself. Critically, **structural
    orthogonalization is NOT sufficient alone**: on `A = cI` — the very example
    above — the hybrid still returns duplicate eigenvectors **61.77%** of the time
    over 20,000 trials, against 0% for the guarded arm. The guard is load-bearing,
    not a refinement. "0% fallback on generic input" survives (0% for uniform random
    and for anisotropies to 1e-4; 100% below 1e-7), so the 4.2x holds on realistic
    stress and vanishes on a near-isotropic field.

15. **The un-jammed baseline is NOT a serial dependent chain, and that is why one
    speedup number is meaningless.** Adjacent output cells are independent, so the
    out-of-order window overlaps one cell's add-chain tail with the next cell's
    head. At n=257 that puts the baseline at **2.44 cyc/MAC, faster than the 3.0 its
    own `vaddsd` chain requires**, so the jam appears to win less. Proved by
    control, not by argument: `base_ser` (identical arithmetic, each cell seeded
    `prev - prev` so cells CANNOT overlap) sits at 3.03-3.13 cyc/MAC at every n.
    **(round 2)** "A fixed ~100-150 cycles per cell" holds only in the middle: at
    n=35 a whole cell is ~105 serial cycles, so 150 cannot be recovered (it is 62);
    at n=2051 both arms miss to L3 and it rises to ~250. Better: **~155 cycles,
    capped by the cell's own serial length below n≈100 and inflated once the operand
    leaves L2.** Consequence: the same transform measures 1.86x at n=35, 3.5x at
    n=257 and 4.19x at n=1027 — and then **falls back** to 4.02x at n=2051, so the
    curve PEAKS rather than approaching an asymptote. **Quote a jam speedup with its
    fold length or do not quote it.** And R=2 is not a cheap substitute: it captures
    47% of R=5 at n=257 and 44% at p=8.

16. **The jam's knee is a CROSSOVER, and which resource it crosses is the
    compiler's choice, not the transform's.** The natural model — R independent
    scalar chains saturating at ~6-8 — predicts the wrong shape *for gcc*, because
    gcc does not keep your chains. It transposes the R x 4 tile and runs R-lane
    `vaddpd` in k-order, so lane = output cell. **(round 2)** The first account of
    this said shuffle throughput binds. It does not: **shuffle pressure per MAC is
    FLAT** at 1.125-1.250 across R=4..12 — the raw counts 20/27/40 at R=4/6/8 are
    that same rate over 16/24/32 MACs — and a flat resource cannot make a peak, it
    makes a FLOOR. Meanwhile the loop-carried recurrence is **12 cycles at every
    width** (four dependent `vaddpd` per 4 k, i.e. exactly the fold's own 3 cyc per
    k, unchanged by jamming), so the chain bound is **3/R and falls**. The model,
    all three terms measured:

    ```
    cyc/MAC = max( 3/R ,  ~0.6 ,  0.25 )
               chain    gcc's    one operand
               bound    shuffle  load per MAC
                        floor
    ```

    The knee is simply where `3/R` meets the floor, at R ≈ 5. Three consequences.
    **At R=4 the chain still binds, not shuffles**: pre-packing the jammed operand
    so no transpose is needed buys only 1.12x at R=4 — but **1.80x at R=8 and 2.22x
    at R=12**, running to **0.34 cyc/MAC**, bitwise, before hitting the real floor.
    **Do not hand-roll intrinsics**: an AVX2 kernel matches the plain C
    emitter-shaped text to within noise (4.01x vs 4.01x); the win is LAYOUT, never
    codegen. And **suppressing contraction is free on Zen 3** — explicit
    `vmulpd`+`vaddpd` beats explicit `vfmadd` 4.01x to 3.69x, because mul issues on
    FP0/FP1 and add on FP2/FP3 — so the bitwise form costs only the function hoist
    that `__attribute__((optimize("fp-contract=off")))` requires
    (`#pragma STDC FP_CONTRACT` is silently ignored by g++; clang ignores the gcc
    attribute and needs `#pragma clang fp contract(off)`).
    **The knee is gcc's, not the transform's**: clang does not transpose at all (it
    hand-gathers into lanes and emits `vfmadd213pd`, chain 4/R) and is still
    improving at **R=12**, where a fixed R=5 leaves 28-36% on the table.
    `-fno-tree-vectorize` is 24-28% slower at R=4-6 but **35% faster at R=12**,
    where the R-scalar-chain model is right after all — yet it is not shippable:
    without the vectorizer gcc fuses each scalar accumulator into `vfmadd*sd` while
    leaving the baseline unfused, so every unvectorized width reads bitwise `NO`.
    **The transpose is what keeps the jam byte-exact**, by separating the multiply
    from the add.

17. **The jam's real coverage hole is SHAPE, not rounding.** `jamR` was 5, and every
    dense-gram nest in the corpus emitted `for (; __gj + 5 <= p; ...)` with `p` in
    {2, 3, 4} — a statically false condition. All 18 gram programs were emitted and
    checked: **the jammed body was unreachable in 100% of the corpus**, so no
    fixture could observe it non-bitwise because none executed it. Widening R from 4
    to 5 removed the last coverage there was, since at R=4 the p=4 case fired
    exactly one tile. Fixed two ways: `tests/corpus/math/063` uses p=13 so both the
    tile and the remainder run, and a3837e6 derives R so every gram site emits a
    tile that fires. Underneath that sits the four-layer gate blindness described in
    "What the gates can actually see" above — of which the `setprecision(15)`
    compare, the layer first recorded here, is the weakest. Any kernel claiming
    "bitwise" on small-integer or dyadic inputs is claiming nothing — **including
    `packed_symv.c` and `multiplicity_fold.c` in this very directory**.

18. **SEVEN native arms are unreachable in a default user environment, and two of
    them are shipped optimizations.** `resolveBlasTier` answers `TierOpenBlasDir` on
    `OPENBLAS_DIR` alone, and `lapackAvailable()` is true on that tier, so with the
    shipping default and `BLADE_BLAS` unset the shim takes `gram` (both modes),
    `matmul`, `dot`, `gemv` (the last two unless the kernel carries `omp` — L1/L2 are
    `OmpWins`), `solve` and `eigh`. The jammed gram arm never runs; neither does the
    **heap-free small solve (d99195f)**, which is `m.solve`'s native arm. No size
    threshold, purely environmental. What saves the suite is that `dispatchTest`
    clears `OPENBLAS_DIR` and `BLADE_BLAS` at the top of every `blade test`, so this
    is not dead code — it is code a default user shell cannot reach and that only
    the harness's own gate-clearing exercises. Every bench fixture for a native arm
    must pin `BLADE_BLAS=0`, and a speedup on such an arm describes a configuration,
    not a default.

19. **A ratio measured on a loaded box is not a ratio.** `packed_syr_syrk.c`'s 13.2x
    was taken while five sibling jobs compiled; re-run unmodified and idle, the same
    arm at the same shape reads **3.14x**. The inflation is systematic rather than
    noisy: contention costs a DRAM-bound baseline far more than a register-blocked
    numerator, so exactly the comparisons these kernels exist to make are the ones
    it distorts. Quote cyc/MAC with an in-process clock calibration, and re-measure
    anything whose baseline touches memory.

20. **Verify bitwise against the code you are REPLACING, not against your own
    reference.** `packed_syr_syrk.c`'s blocked rank-k is bitwise on arbitrary random
    doubles — against a reference also written with `fma()`. Against the nest
    `materializeGramForm` actually emits (`__gacc += a*b`) it differs on **every
    cell** by 6.6e-16, because gcc compiles that nest as `vmulpd` plus an in-order
    `vaddsd` chain and does **not** contract it. Rewriting the kernel with
    `_mm256_mul_pd` + `_mm256_add_pd` closes the gap and costs 17% (5.80x instead of
    6.80x) — but only if you also defeat the flag: **at `-ffp-contract=fast`,
    Blade's default, gcc 15.2 re-fuses explicit mul+add INTRINSICS straight back
    into `vfmadd231pd`**. `__attribute__((optimize("O3","fp-contract=off")))` works
    on gcc; **clang 22.1.8 has no per-function escape** — its `-ffp-contract=`
    overrides `#pragma clang fp contract(off)` at both function and loop-body
    placement — so a byte-exact kernel there needs the whole TU at
    `-ffp-contract=off`.

Also worth knowing, from the small-solve batch: **branchless pivoting failed
twice** — gcc emits branches rather than blends for `sw ? a : b` on doubles, and a
true bitmask select was 2x worse still (two GP<->XMM domain crossings per select).
Do not pay for pivoting on SPD input; use Cholesky, which needs none and is 1.6x
faster at n=3,4. **Specialization itself stops paying at n ~ 8-10** — against a
*no-heap* runtime-n loop it is 3.4x at n=2 but only 1.12x at n=8, so specialize
aggressively at n <= 4 and merely remove the allocation above that. And **fixing
`n` to a compile-time constant is bitwise** (955/955 identical, on full-mantissa
operands with a long-double reference — a genuine result), so it is a byte-exact
drop-in; switching LU to Cholesky is **not** (2.9%) and must be a visible semantic
choice.

---

# Three properties worth preserving in any emitter

**Bitwise exactness is achievable, but the evidence for it here is thinner than the
kernel count suggests.** Of the five kernels with a BITWISE column, **two** verify
on operands that can round: `krs_former.c` (its `verify` runs a random-double pass)
and `packed_syr_syrk.c`, whose blocked rank-k is bitwise on arbitrary random
doubles because it re-tiles which cells are visited without reassociating any
cell's sample sum. `packed_symv.c` runs `memcmp` only on integers in [-4,4];
`multiplicity_fold.c`'s `(+)` arm is integer-only by design (a real
multiplicity/addressing check, not an FP-safety one) and its `max` arm is exact for
any input by idempotence; `mirror_transpose.cpp` moves bytes and negates.
`small_solve.c`'s separate 955/955 IS genuine. The claim that survives is
`packed_syr_syrk.c`'s, and it is worth as much as before — subject to correction
20. The price is three invariants, not two: **keep the sample loop innermost and
ascending, do not fold a scale factor into a pre-multiplied operand, and reproduce
the reference's CONTRACTION pattern, which is a property of the compiler and the
trip count, not of your source text.**

**Packed rows cannot be collectively aligned — but that is a choice, not a fact.**
The row pitch shrinks by one every row, so at most 1/4 of natural packed rows start
32-byte aligned. This is not free: an unaligned 32-byte access costs **1.68x at
L1** (1.11x at L2, 1.05x at DRAM), and it is the ADDRESS that costs, not the
instruction — `vmovupd` on an aligned address is within 1% of `vmovapd` on the same
address. Rounding every row start up to a multiple of 4 doubles buys **1.11-1.21x**
on the packed `syr` for **0.05-0.97%** extra memory. What that costs elsewhere is a
real question — a padded pitch is no longer the layout BLAS expects, and
`rowbase(i)` stops being the storage index — so it belongs in the layout decision,
not in the emitter's load selection. These kernels use unaligned loads because they
keep the standard layout, which is defensible; it is not a consequence of the
layout being unalignable.

**A kernel's operand ORIENTATION is a hidden premise until you state it.** This
cost two headlines. `unroll_and_jam.c`'s 13.95x and `packed_syr_syrk.c`'s 13.2x
were both measured sample-major — the contracted sample axis leading — while
`materializeGramForm` emits the contracted axis CONTIGUOUS. Re-measured in Blade's
orientation the jam gives 3.5x, and the blocked rank-k gives 6.80x (5.80x
byte-exact) against 3.53x for the jam already shipped: an increment of 1.64x at
n=2003 k=256, and a **loss** at n=61, Blade's actual comoment extent. Both verdicts
had to be extent-gated afterwards. `krs_former.c` packs `A` explicitly and INSIDE
its timed region, so it carries no such premise — the contrast between the two is
the thing to notice.

---

# Building and running

```
export PATH="/c/msys64/ucrt64/bin:$PATH"
gcc -O3 -march=native -ffp-contract=fast -o krs.exe krs_former.c -lm
./krs.exe peak
./krs.exe verify <n> <d> <KC> <MR>
./krs.exe bench  <n> <d> <reps> <KC> <arms:RAVTB> <MR>
```

Each file documents its own arms and CLI in its header. The `.cpp` kernels need
`g++ -std=c++17`. `clangtimer.h` exists because clang64's mingw headers reference a
`clock_gettime64` its CRT does not export.

The jam kernels take `<rounds> <reps>` and print a bitwise column:

```
g++ -O3 -march=native -ffp-contract=fast -std=c++17 -o gjw.exe gram_jam_width.cpp
./gjw.exe 5 3
```

Shapes are compile-time: `-DMM=301 -DNN=257 -DPP=303`. Sweep `NN` to see correction
15 and `PP` to see correction 11, and keep extents non-power-of-two — CLAUDE.md's
benchmark discipline, and correction 3 is what ignoring it looks like.

## Two self-checks that are not optional

**The bitwise column must be live.** `gram_jam_width.cpp` uses full-mantissa
operands by default and keeps `-DDYADIC` only to demonstrate the trap: under
`-DDYADIC` every arm reports `yes` INCLUDING the explicit-`fma` control, because
`gramdense.blade`-style operands are exact dyadic rationals that never round. So
the check is that **`base_fma` MUST read `NO`**.

**That check has a second failure mode, and it is silent.** On clang 22.1.8
`base_fma` reads `yes` with full-mantissa operands — not because the operands
cannot round but because **clang contracts the reference nest too**, so control and
baseline are the same program. A reader following the first check alone would
diagnose their fixture when the fault is their compiler. Distinguish by
disassembling the reference for `vfmadd`, or by diffing against a
`-ffp-contract=off` build.

## Timing hygiene, learned the hard way

These kernels are pure with loop-invariant arguments, so a compiler will hoist the
whole call out of a repetition loop and report near-zero times. Every timed region
is bracketed with `asm volatile("" ::: "memory")` and the results are read back into
a printed checksum. **`asm volatile` *around* the timed region is not enough when
the call is pure with loop-invariant arguments** — gcc will CSE the calls across
reps and report ~0 ns/element. The observation must be INSIDE the repetition loop.
Keep both if you reuse this code.
