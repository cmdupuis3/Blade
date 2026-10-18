# Microkernel sketches for Blade: Codex, 2026-10-18

Planning only. No compilation, execution, benchmarks, disassembly, or git operations were
performed. Only this document was written, incrementally. Evidence is the existing source,
archived emissions, and measurements in the brief and acceptance notes. C++ below is a
design sketch, not a tested patch; named helper contracts describe omitted plumbing.

## Evidence and arithmetic conventions

Archive aliases used in every citation below:

- `A/<k>.cpp` = `bench/results/2026-10-18-LAPTOP-350H6LDB/session-042223/build/<k>/full/serial-licensed-native/blade-cpp/generated/<k>.cpp`.
- `A/<k>.ll` = the same path with `blade-llvm/generated/<k>.ll`.
- `A/<k>/plan` = the C++ arm's adjacent `plan.txt`.
- `B` = `bench/results/2026-10-18-LAPTOP-350H6LDB/session-113651/build/`;
  individual threaded paths are identified where used.

The phase-2 plan already supplies the main diagnosis. These sketches add executable-shaped
tile bodies, a simultaneous smaller antisymmetric tile, safer weighted-fold arithmetic,
allocation ownership rules, stable compaction with preserved index identity, and concrete
thread/fusion schedules. They do not propose changing the benchmark's Blade sources.

Important limits on all forecasts: the phase-2 plan reports drift-suppressed inference for
five of the six headline kernels; checksum fractions were measured at reduced sizes, using
attribution minima rather than the headline medians. A calculated residual is a prioritization
estimate, not a measured speedup. Improvements sharing a cost must not be added together.

`Rserial` means +0-seeded, ascending-s scalar contractions, retaining the source's factor
parentheses and final division/subtraction. `Rinterp` additionally disables contraction.
Strict-legal below means the transformation preserves that operation order and needs no
summation reassociation; it remains enabled with `BLADE_FP_REPRO=1`. It does NOT promise
cross-compiler bit identity with `-ffp-contract=fast`. That requires the actual contraction
graph to match. A serial reference compiled with the same flag is necessary but not sufficient:
inspect the instructions too. No explicit FMA intrinsic should defeat `contract=off`.

Licensed reductions have no obligation to reproduce today's licensed bits. Their raw-bit
test is against a scalar evaluator of the *proposed reduction tree*, alongside a separate
comparison with Rserial for numerical error. Requiring equality with the serial tree would
wrongly reject the optimization's defining reassociation.

Common C++17 vocabulary follows `src/cpp/blade_packed_gemm.hpp`: GCC/Clang vector extensions,
`memcpy` vector loads/stores, 64-byte scratch alignment, and an ordinary scalar fallback on
unsupported hosts. `v4d` is four doubles; `load4`, `store4`, and `splat` mean:

```cpp
using v4d = double __attribute__((vector_size(32)));
inline v4d load4(const double* p) { v4d v; std::memcpy(&v, p, 32); return v; }
inline void store4(double* p, v4d v) { std::memcpy(p, &v, 32); }
inline v4d splat(double x) { return v4d{x,x,x,x}; }
```

These are sketches within the existing compiler-conditional vector-extension arm, not
portable ISO vector types. Every size/offset computation is checked before allocation or
pointer formation; overflow refuses through the existing runtime diagnostic. In formulas,
`choose2` and `choose3` divide factors before checked multiplication. Mere existence of
the final pool does not prove an unchecked binomial numerator fits.

All budgets use Zen 3's 16 ymm registers, two loads/cycle, two FMA instructions/cycle,
four-cycle FMA latency, 32 KiB L1d, 512 KiB L2, and 16 MiB L3. A tile needs eight independent
vector accumulator chains to fill both FMA pipes; a two-stream dot can saturate its loads
with four. None of these ceilings includes packing, stores, divides, or address generation.

## S1. Same-array packed SYRK: a store mask around the existing 6x8 core

**Target and current form.** Gram. `src/LinAlgPatterns.fs:1461` already recognizes the
same-array rank-2 contraction; `src/CodeGenLoopNest.fs:2546` dispatches it, and its
`entryOpt = None` branch returns to the ordinary nest. The LLVM sites are
`src/EmitLlvm.fs:4047` (materialize), `:2015` (brick selection), and `:2613` (prodsum).
The shipped `src/cpp/blade_packed_gemm.hpp:103` tile is the arithmetic to reuse.
Actual archived text, `A/gram.cpp:293`:

```cpp
__orow___v27[__i1] = [&]() { const size_t __pn = 1001; double __ps = 0; BLADE_OMP_SIMD_REDUCTION(+:__ps) for (size_t __pt = 0; __pt < __pn; __pt++) {     __ps = __ps + __v21____i0[__pt] * __v21____i1[__pt]; } return __ps; }();
```

**Proposed form.** Preserve Blade's row-packed upper triangle, not Fortran's column-packed
layout. Broadcast the smaller index i and vectorize the larger index j. This is a transposition
of the expert's storage-facing orientation, not a transposition of the scalar product.
For `S=0` (inclusive) or `S=1` (strict), use checked row offsets
`base(i)=choose2(p+1-S)-choose2(p+1-S-i)` and a lane offset `j-i-S` only after `j>=i+S`.

```cpp
// A: kc x 6, s-major; B: kc x 8, s-major. C is the flat packed pool.
template<unsigned S>
inline void tri6x8(size_t p, size_t i0, size_t j0,
                  size_t mr, size_t nr, size_t kc,
                  const double* A, const double* B, double* C,
                  bool first) {
    v4d c[6][2];
    for (size_t r=0; r<6; ++r) {
        alignas(32) double t[8] = {};
        if (!first && r<mr) {
            const size_t i=i0+r;
            for (size_t q=0; q<nr; ++q) {
                const size_t j=j0+q;
                if (j>=i+S) t[q]=C[row_base<S>(p,i)+(j-i-S)];
            }
        }
        c[r][0]=load4(t); c[r][1]=load4(t+4);
    }
    for (size_t s=0; s<kc; ++s) {
        const v4d b0=load4(B+8*s), b1=load4(B+8*s+4);
        // Generate six constant-index copies (or use compile-time unrolling).
        for (size_t r=0; r<6; ++r) {
            const v4d a=splat(A[6*s+r]);
            c[r][0] += a*b0;
            c[r][1] += a*b1;
        }
    }
    for (size_t r=0; r<mr; ++r) {
        const size_t i=i0+r;
        if (nr==8 && j0>=i+S) {
            double* dst=C+row_base<S>(p,i)+(j0-i-S);
            store4(dst,c[r][0]); store4(dst+4,c[r][1]);
        } else {
            alignas(32) double t[8];
            store4(t,c[r][0]); store4(t+4,c[r][1]);
            for (size_t q=0; q<nr; ++q) {
                const size_t j=j0+q;
                if (j>=i+S) C[row_base<S>(p,i)+(j-i-S)]=t[q];
            }
        }
    }
}
```

Make the interior reload a pair of direct `load4`s too; the generic reload shown above is
for clarity at edges, not an excuse for scalar stores followed by vector reloads on every
tile. Full and edge specializations should be emitted separately so masking never enters s.
Pack `A[6*s+r]=X[(i0+r)*ldx+pc+s]` and `B[8*s+q]=X[(j0+q)*ldx+pc+s]`, with zero padding
only in missing **output** rows/columns. Do not pad the sample chain: extra +0 terms can
alter signed zero and floating-point exception behavior.

Driver: `jc` blocks up to 2048; ascending `pc` blocks up to 256; pack B once for that
`(jc,pc)`; pack A in 96-row blocks; visit only microtiles intersecting `i<=j`. Partial C
is reloaded as the next block's *initial accumulator*, never `C += dot(block)`. All
threads must finish reading B before its next refill. A single persistent team around
the pc loop can avoid repeated fork/join, but both worksharing barriers are necessary.
One owner completes each cell per pc. Preserve the existing scale tail, or apply it only
on the final block store. Recognize this before the gate-off fallback, while retaining
all of the existing pattern's storage, identity, precision, and mode guards.

**LLVM.** Replace the materializing contraction with the shared helper of S12, or twelve
`<4 x double>` PHIs with ascending s and plain `fmul`/`fadd` carrying only the resolved
contraction flag. `reassoc` is unnecessary even in a licensed build. Keep the old deferred
read closure for non-materializing consumers. A 64x64 cache brick alone is not this kernel.

**Legality.** Strict-legal per cell; no K splitting between threads. Live input/output alias
checks and the same-array identity proof are mandatory. Output scale and source product
operand order stay unchanged. Floating-point traps/flags are not assumed observable by
this proposal; if they are part of the language contract, speculative edge-lane arithmetic
needs an additional gate, since zero times infinity can trap even in an unstored lane.

**Budget and forecast.** Twelve accumulators + two B vectors + one broadcast = 15 ymm;
under contraction off, one product temporary consumes the last register. An unrolled 4x8
fallback (eight chains) is safer if that version spills. Per s: eight loads and twelve
vector FMAs, 0.667 loads/FMA, versus two for the dot. Actual loaded bytes are
`(2*32+6*8)/48 = 2.33 B/useful lane-FMA`, versus 16 B. A/B micro-panels at KC=256 total
28 KiB, leaving little L1 margin; MC panel = 192 KiB L2, B macro-panel <=4 MiB L3.
KC=192 gives 21 KiB and is a sensible alternative, priced against more C traffic.

Gram has `1,282,401*1001 = 1,283,683,401` useful lane-FMAs. At the measured expert
16.3-18.2 G/s, the arithmetic-equivalent time is 70.5-78.8 ms. Forecast a first implementation
at roughly 80-100 ms including packing/checksum/edges, versus 157 LLVM / 275 C++;
not a guarantee of matching ifx. Repro's reported 1219 ms has far more headroom. Four K
blocks write the 10.26 MB output four times and read it three times: 71.8 MB total output
traffic before checksum, not merely one packed write. Input packs add roughly 51 MB of
reads+writes at this shape. Price those bytes rather than assuming free blocking.

**Edges and verification.** Test p=0,1 and all remainders around 6/8; n=0,1,255,256,257,
1001; prime extents; diagonal-only fragments; and both masks. Strict p=1 has no row to
read and the strict final row is empty. Check all packed cells and mirrored reads against
Rserial bitwise with contraction off, then matched fast contraction; require 12 packed
FMA instructions per unrolled s body under fast when fully contracted, zero under off.
Audit no spills, no horizontal reductions, no `vpermpd`/`vunpck` train in the s loop.
Only a build can settle register allocation, KC crossover, actual FMA matching, and speed.

## S2. Antisym: fuse both dots in a 4x4 strict tile, compare with sequential 6x8

**Target/current.** `A/antisym.cpp:309` emits two independent simd reductions and a final
subtraction; the shorter function copy is `:212`, with the exact central expression:

```cpp
__ps = __ps + __pa0[__pt] * __pa1[__pt];
```

Its first aliases are `a.data[0L]`, `b.data[1L]`; the second are `b.data[0L]`, `a.data[1L]`.
Thus the contract is `sum(X_i*Y_j) - sum(X_j*Y_i)`, not a sum of per-s differences.
Target the same nest sites as S1 plus a separate ordered pattern beside
`src/LinAlgPatterns.fs:1461`; the SYRK match deliberately does not admit this case.

**Proposed form.** The plan correctly rejects 24 live vector accumulators for two 6x8
tiles. It need not follow that the two dots must always be separate passes. A 4x4 paired
tile has four P and four Q accumulators, exactly the eight needed for FMA latency hiding:

```cpp
// apX/apY: kc x 4 broadcast panels; bpX/bpY: kc x 4 vector panels.
v4d p0=splat(0), p1=p0, p2=p0, p3=p0;
v4d q0=p0, q1=p0, q2=p0, q3=p0;
for (size_t s=0; s<kc; ++s) {
    const v4d xj=load4(bpX+4*s), yj=load4(bpY+4*s);
    p0 += splat(apX[4*s+0])*yj; q0 += xj*splat(apY[4*s+0]);
    p1 += splat(apX[4*s+1])*yj; q1 += xj*splat(apY[4*s+1]);
    p2 += splat(apX[4*s+2])*yj; q2 += xj*splat(apY[4*s+2]);
    p3 += splat(apX[4*s+3])*yj; q3 += xj*splat(apY[4*s+3]);
}
// On final K block only, spill/store valid lanes of p_r - q_r at j>i.
// On earlier blocks store P and Q separately; the next block seeds from each.
```

This is a **candidate**, not a claim to beat sequential 6x8. It uses ten loads per eight
vector FMAs (1.25 loads/FMA): the smaller tile trades some reuse for simultaneous chains
and a single panel pass. Eight accumulators + two input vectors + one temporary broadcast
= 11 live ymm, leaving room for off-mode products and address scheduling. The larger
sequential variant remains the throughput baseline: run S1's core for P, save 48 doubles,
run it for Q with vector/broadcast product order reversed, then subtract once. It uses
0.667 loads/FMA and is likely better on the full shape; the paired 4x4 may win for short
n, ragged borders, or contraction-off register pressure. Do not confuse two dots in the
same source loop with greater throughput: 1.25 loads/FMA is an explicit cost.

For n=401, a tile may own the entire sample chain, eliminating whole-output Q scratch.
P/Q packs are disjoint: X base = Z pool, Y base = X+n, both `ldx=2*n`, not n. A 4x4
paired tile's four micro-panels total `16*401*8=51,328` bytes, fitting L2 but not L1.
With KC=192 their total is 24 KiB. For large n, put ascending K blocks outside tile
visitation to reuse panels: P partials in the output pool, Q in a separate strict-packed
pool, initialized only on first use. On the last block finalize L and never read it again
as P. Reload/store packed cells through the valid-lane mask, not as rectangular blocks.

**LLVM/legality.** Eight vector PHIs for the paired core, or S12's helper; preserve Q as
`X_j*Y_i` even though the vector is on the left. No reassociation needed. Never use
`acc += X_i*Y_j-X_j*Y_i`, and never subtract partial sums per block. Scale, if any, follows
the final subtraction. A dense `P=XY^T; L=P-P^T` is a useful comparison, but its p^2
intermediate costs about 11.5 MB versus 5.76 MB for a strict Q scratch at p=1201.

**Budget/payoff forecast.** Useful work is `720,600*802 = 577,921,200` lane-FMAs.
Sequential 6x8 at 16.3-18.2 G/s predicts 31.8-35.5 ms arithmetic; allow roughly 34-42 ms
total versus 74/108, or a little less after checksum optimization. The paired 4x4 load
ceiling is `2/1.25=1.6` vector FMAs/cycle = 6.4 lanes/cycle, 80% of FMA peak before
other costs; it cannot claim the same ceiling as 6x8. Compare both, including edge-only
dispatch, rather than baking the smaller shape in as the universal winner.

**Edges/verification.** p=0/1 returns without dereferencing the output spine; i=p-1
stores nothing. Test crossing `j=i+1`, the p=1201 tail, short n, and at least three K
blocks with independently checked P and Q. Include cancellation, +/-0, subnormals,
infinities, and non-NaN raw-bit checks (NaN payload propagation is separately documented).
Fast paired body should have eight packed FMA sites per s, plus four vector subtracts
after the chain; sequential form has twelve per pass. Check subtraction is not absorbed
into an altered summation graph, no repeated partial difference, and no vector reload
from partially written scalar scratch causing store-forwarding stalls. Only builds choose
between these two schedules.

## S3. Comoment3: a fixed-i, six-prefix by eight-k simplex tile

**Target/current.** `A/comoment3.cpp:320`:

```cpp
__orow___v29[__i2] = ([&]() { const size_t __pn = 201; double __ps = 0; BLADE_OMP_SIMD_REDUCTION(+:__ps) for (size_t __pt = 0; __pt < __pn; __pt++) {     __ps = __ps + __v21____i0[__pt] * __v21____i1[__pt] * __v21____i2[__pt]; } return __ps; }() / static_cast<double>(n));
```

Sites: generic simplex emission in `src/CodeGenLoopNest.fs:1251`, prodsum in
`src/CodeGenExpr.fs:322`, `src/EmitLlvm.fs:2613`, and rank-2-only brick decision at
`:4057`. The reviewed Fortran `bench/kernels/comoment3/expert.f90:101` explicitly
forms the first product Xi*Xj; its `tile` at :197 accumulates against Xk. Preserve that
correction; moving parentheses to Xi*(Xj*Xk) is not legal merely because the kernel is comm.

**Proposed form.** Pack all k rows once into 8-wide, s-major panels. Fix i; group j in
blocks of six beginning at i. Build one rounded W panel per prefix block, reuse it over
all k panels, and write Blade's k-contiguous packed rows:

```cpp
for (size_t i=0; i<p; ++i) {
    for (size_t j0=i; j0<p; j0+=6) {
        const size_t mr=std::min(size_t(6),p-j0);
        for (size_t s=0; s<n; ++s)
            for (size_t r=0; r<6; ++r)
                W[6*s+r] = r<mr ? X[i*ldx+s]*X[(j0+r)*ldx+s] : 0.0;
        for (size_t k0=(j0/8)*8; k0<p; k0+=8) {
            const size_t nr=std::min(size_t(8),p-k0);
            v4d c[6][2] = {};
            const double* b=packed_k_panel(k0); // n x 8
            for (size_t s=0; s<n; ++s) {
                const v4d b0=load4(b+8*s), b1=load4(b+8*s+4);
                for (size_t r=0; r<6; ++r) { // unroll constant r
                    const v4d w=splat(W[6*s+r]);
                    c[r][0] += w*b0; c[r][1] += w*b1;
                }
            }
            for (size_t r=0; r<mr; ++r) {
                const size_t j=j0+r;
                alignas(32) double tmp[8];
                store4(tmp,c[r][0]); store4(tmp+4,c[r][1]);
                for (size_t q=0; q<nr; ++q) {
                    const size_t k=k0+q;
                    if (k>=j) T[base3(p,i,j)+k-j]=tmp[q]/divisor;
                }
            }
        }
    }
}
```

`base3(p,i,j) = choose3(p+2)-choose3(p+2-i)
+ choose2(p+1-i)-choose2(p+1-j)` for `i<=j`; it is the emitted left-justified layout.
Use direct vector division/store for a fully valid row fragment. Apply `/divisor` once,
never multiply by its reciprocal, never pre-scale W, and never use divisor zero as a
sentinel for absence of scaling. Empty n produces the source's `+0/divisor`, including
its possible NaN. For large n, apply S1's partial-reload rule to *unscaled* T and divide
only on the last pc block. This avoids the older prototype's `T += dot(block)` bug.

**Legality/LLVM.** Strict-legal when the source's first two factors correspond to the
outer i,j prefixes in their original order. W is rounded binary64 before multiplication
by Xk, as in the source. Reject a kernel whose first product involves the varying k slot.
LLVM must not attach reassociation to the product chain or let a later map retain a stale
contraction plan. Use vector PHIs or the same shared core as S1. Reuse the existing
canonical storage mapping; no rank-3 brick-major representation is required.

**Budget and forecast.** Same 15-register 6x8 budget as S1. W is `6*201*8=9,648` bytes;
one Xk micro-panel 12,864 bytes; together 22,512 bytes in L1. All padded Xk panels occupy
`176*201*8=283,008` bytes. Original X is 274,968 bytes, so the two whole representations
together exceed 512 KiB L2: claiming they both fit would be false, although the hot
Xk+W working set does fit. The output is 6,784,368 bytes and fits in L3 alone.

There are 848,046 useful cells and 170,457,246 useful terms. First-products decrease
from one per term to `171*172/2*201 = 2,955,906`, a 57.7x reduction in that operation;
total multiply/FMA lane-op count improves about 1.966x. Ignoring edge padding, loads
drop from three per vector term to 0.667 per vector FMA, a 4.5x ratio. At 12-15 G useful
terms/s, forecast kernel time 11.4-14.2 ms; with S6 checksum, approximately 13-18 ms
total versus 41.5/110. Exact tile utilization must be counted before making a tighter claim:
the executed lanes are `48 * sum_i sum_{j0=i,i+6,...} (ceil(p/8)-floor(j0/8))`, whereas
useful cells are choose3(p+2). This expression includes padded prefix rows and both k edges.

**Edges/verification.** Exercise every equality stratum i=j=k, i=j<k, i<j=k, i<j<k,
the offset at each i-sub-simplex boundary, p=0..9 and ragged p=171, n near KC, and unusual
divisors. Strict rank-3 storage is outside this match; it would need i<j<k and an empty
pool for p<3. All cells raw-bit equal Rinterp under off; verify fast-mode first multiply
is rounded and only the final multiply/add contracts. Expected full core: twelve packed
FMAs per s plus W's separate multiplies. Count divide instructions in the store epilogue,
not the contraction loop. A build alone settles spills, padding cost, and W packing time.

## S4. Explicit vector accumulators for licensed contiguous prodsum

**Target/current.** `A/gram.cpp:208` (function) and :293 (nest) have the one-accumulator
`BLADE_OMP_SIMD_REDUCTION(+:__ps)` quoted in S1. `src/CodeGenExpr.fs:412` selects it;
`src/CodeGenExprSupport.fs:1563` supplies the scalar K-lane fallback. The emitter's own
comments at `CodeGenExpr.fs:385-408` record that naive scalar-lane SLP lost badly on a
gemv fiber and three-stream form. This sketch must beat that baseline, not replace it
on the assumption that eight C++ scalar variables mean eight independent ymm chains.

**Proposed C++17.** Admit proven contiguous unit-stride Float64 operands, evaluated once;
leave deferred gathers, ragged noncontiguous views, complex types, and unknown layouts
on their current path. Read-only operands may alias each other (self-dot is important),
so no unnecessary distinct-input promise is imposed.

```cpp
inline double dot4(size_t n, const double* a, const double* b) {
    v4d s0=splat(0), s1=s0, s2=s0, s3=s0;
    size_t t=0;
    for (; n-t>=16; t+=16) { // t<=n invariant, no t+16 overflow
        s0 += load4(a+t   )*load4(b+t   );
        s1 += load4(a+t+ 4)*load4(b+t+ 4);
        s2 += load4(a+t+ 8)*load4(b+t+ 8);
        s3 += load4(a+t+12)*load4(b+t+12);
    }
    for (; n-t>=4; t+=4) s0 += load4(a+t)*load4(b+t);
    const v4d v=(s0+s1)+(s2+s3);
    double ans=(v[0]+v[1])+(v[2]+v[3]);
    for (; t<n; ++t) ans += a[t]*b[t];
    return ans;
}
```

A three-input version computes `(load4(a)*load4(b))*load4(c)` with that factor order.
A self-dot variant loads once, then squares: it has one load per FMA and may benefit
from **eight** accumulator vectors. That is a principled split absent from an arity-only
rule: two syntactic operands can be one physical load stream. Eight streams of four
elements leave <=31 elements for cleanup; process full vectors without overreading,
then a scalar tail. Float32 needs its own v8f and Float32 accumulator semantics.

**Legality/LLVM.** Requires the fold's reassociation license and the live build gate;
repro takes the old ascending single chain. The combine tree above is explicit but
can still change under a backend given `reassoc`: either constrain IR to this tree for
deterministic helper tests or test the licensed result with tolerance, not assumed bits.
LLVM currently gets four accumulators from scalar licensed IR (`A/gram.ll:200-202`):

```llvm
%r74 = fmul contract double %r71, %r73
%r76 = fadd reassoc nsz contract double %r75, %r74
```

An explicit vector helper is primarily a C++ fix; do not add a redundant IR abstraction
without evidence. In a fixed-tree helper, use `contract` without `reassoc` after selecting
the legal tree. Never put the reduction license on the three-factor product itself.

**Budget/payoff forecast.** Two-stream dot: two loads/FMA, maximum one ymm FMA/cycle =
four lanes/cycle; four independent accumulators cover the four-cycle latency. Four
accumulators plus two inputs and a product temporary fit easily below 16. Eight do not
improve this load ceiling and enlarge tails/code. Self-dot: one load/FMA can supply two
FMAs/cycle, so eight chains can matter. Three streams impose a <=2/3-vector-FMA/cycle
load ceiling before the extra multiply; four chains are a reasonable candidate.
Today's one dependent ymm add caps the simple form near 4/3 lanes/cycle; an ideal four-chain
two-stream body raises that ceiling threefold, not eightfold. Measured C++/LLVM gram
rates 4.4/8.4 G/s suggest roughly 1.9x kernel headroom before packing. S1-S3 remove these
dots, so do not stack this gain on their forecasts; remaining beneficiaries are generic
dot/gemv, row checksums, and untiled small contractions. DRAM-bound folds may barely move.

**Edges/verification.** Raw-bit compare with a scalar model of each lane/tree under off
and matched fast; compare with Rserial separately for error. n=0..35 and 20011 exercise
all vector/scalar tails, same-pointer operands, and unaligned row starts. For the main
dot4 loop expect four packed FMA sites under fast, zero under off; do not infer success
from whole-executable counts. Require no shuffle inside the hot loop; horizontal
combines belong after it. Benchmark the gemv fiber already documented as a regression
case before replacing its simd form. Builds decide contraction, spills, crossover, and
whether the self-dot eight-chain specialization earns its code size.

## S5. Fixed-rank canonical reads, checked flat offsets, explicit bind-once

**Target/current.** `A/comoment3.cpp:215` and :232 duplicate the entire read for `t*t`.
An exact substring of each IIFE is:

```cpp
int __p0 = nested_array_utilities::canon_fold<3>(__g0, false, __z0); if (__z0) return double(); auto __c0 = nested_array_utilities::canon_left_justify<3>(__g0, false); double __v = t[__c0[0]][__c0[1]][__c0[2]];
```

`A/pairkernel.cpp:221,238` has rank 2. Actual runtime ownership of this particular sort
is `src/cpp/nested_array_utilities.hpp:707-720`, including `std::sort(idx.begin(), idx.end())`;
the brief's broad `index_types.h` attribution should not direct an edit to the wrong
helper. `src/CodeGenExpr.fs:803-890` emits `lazyCompactRead`.

**Proposed C++17.** Retain a generic accessor but specialize R=2 and R=3. Flat offsets
apply only to one proven contiguous packed group, not arbitrary residual views or mixed
groups. Bind indices in left-to-right statements, preserving the old brace-init order.

```cpp
inline bool swap_if_gt(size_t& a,size_t& b) {
    const bool sw=a>b;
    if (sw) { const size_t t=a; a=b; b=t; }
    return sw;
}
template<bool Strict>
inline double read3(const double* pool,size_t p,size_t a,size_t b,size_t c) {
    unsigned parity=swap_if_gt(a,b);
    parity ^= swap_if_gt(b,c);
    parity ^= swap_if_gt(a,b);
    if constexpr (Strict) {
        if (a==b || b==c) return 0.0; // BEFORE offset/pool dereference
        // a<b<c maps bijectively to a<=b-1<=c-2 in extent p-2.
        const double x=pool[base3(p-2,a,b-1)+(c-2)-(b-1)];
        return parity ? -x : x;
    } else {
        return pool[base3(p,a,b)+c-b];
    }
}
// Rank 2: one swap; strict equality returns zero; otherwise
// pool[row_base<Strict>(p,a) + b-a-Strict], then parity transform.
const size_t a=static_cast<size_t>(i);
const size_t b=static_cast<size_t>(j);
const size_t c=static_cast<size_t>(k);
const double tcell=read3<false>(tpool,p,a,b,c);
const double term=tcell*tcell;
```

Do not obtain `pool_base` by traversing an empty pointer spine before the strict-zero
check. For potentially empty storage use an allocation-owned flat base field or branch
before resolving it. Keep bound checks required by the original index type; a cast to
size_t is not a new bounds proof. Hermitian parity must conjugate, not negate; this
double-only strict helper is specifically the antisymmetric class. Preserve the generic
path for rank>3, providers, wreath groups, and compact groups separated by plain slots.

**Legality/LLVM.** Strict-legal addressing/CSE, provided coordinates and source reads are
pure and no intervening mutation or observable call exists. LLVM already uses compare/swap;
share its storage-offset proof rather than adding a separate formula family. Its benefit
here is limited to bind-once and invariant base/offset hoisting, not a 4.9x sort saving.

**Budget/payoff forecast.** Three compare-swaps and integer offsets use GPRs, not twelve
ymm accumulators. Cache traffic becomes one value load per term, versus two opaque
canonicalizations and a pointer chase. No all-lane gather or per-element permute network
is needed: the sort is over integer coordinates. With the measured 4.9x accessor ratio,
the reduced-share extrapolation is `110*(.30+.70/4.9)=48.7 ms` for C++ comoment3 and
`77*(.43+.57/4.9)=42.1 ms` for pairkernel. These are upper-level forecast examples,
not additional gains after S6 removes the same pass. This remains valuable in repro.

**Edges/verification.** Exhaust all coordinate tuples at p=0..9; compare old accessor and
new raw bits, parity, repeated coordinates, and strict zero behavior. Test all 6 rank-3
permutations and p<rank empty pools. Use large legal extents to stress checked offset
arithmetic without allocating impossible arrays in the offset-only test. Check code has
no out-of-line sort and only one semantic cell fetch for t*t. FMA count should be unchanged
for the surrounding checksum under the same policy; any newly fused square/add must be
explained as contraction, not an accessor effect. Builds decide whether direct flat
offset arithmetic beats an already hoisted row-pointer path for every access shape.

## S6. Licensed logical checksum as an orbit-class fold

**Target/current.** Same archived repeated-access expression as S5, nested over all
logical indices; `A/comoment3.cpp:232` accumulates `__ret1073741824 = __ret1073741824 +`
the product of those two IIFEs. The computed pool has 848,046 cells but the checksum
visits 5,000,211 logical triples. `src/CodeGenBinding.fs:3397`/`:3425` is the C++ fold
entry; LLVM `src/EmitLlvm.fs:4653`/`:4786` is the reduce/compact-fold family.

**Proposed C++17.** Recognize the already-valid nested logical traversal, with a license
on every addition being regrouped. Avoid tying the optimization to a new user spelling.
For rank 3, partition exactly by equality pattern and read the pool sequentially:

```cpp
double s1=0.0, s3=0.0, s6=0.0;
size_t off=0;
for (size_t i=0; i<p; ++i) {
    for (size_t j=i; j<p; ++j) {
        const double x=pool[off++]; // k=j
        if (i==j) s1 += x*x; else s3 += x*x;
        if (i==j) {
            for (size_t k=j+1; k<p; ++k) { const double y=pool[off++]; s3 += y*y; }
        } else {
            for (size_t k=j+1; k<p; ++k) { const double y=pool[off++]; s6 += y*y; }
        }
    }
}
// Explicit addition trees express an association of repeated class sums.
const double three3=(s3+s3)+s3;
const double three6=(s6+s6)+s6;
const double result=(s1+three3)+(three6+three6);
```

Vectorize the long interior class folds using S4 or a licensed simd reduction. For rank 2,
separate diagonal s1 and strict s2, then `s1+(s2+s2)`. For strict rank 2 and an even
transform such as square, only `s2+s2` remains. The empty strict last row contributes
zero and never yields a read. A future strict rank-3 version has weight 6 everywhere
stored; implicit repeated-coordinate zeros still require correct logical emptiness rules.

**A deliberate refinement of the prior plan.** Its `S1+3*S3+6*S6` compact notation is
reasonable, but the addition-only epilogue above makes the licensed operation manifest
and avoids relying on a general floating multiply/distributivity rewrite. It is not a
claim that its particular weights are wrong. Every class sum is replicated as a reduction
subtree, with the same terms/multiplicity; materializing a square before reducing also
pins where its rounding occurs. Source contraction may still permit a fused square-add;
the implementation must choose and test that policy explicitly rather than comparing
against a fused reference while silently using pre-rounded terms.

Typed recognition is narrow: immutable stable source; a single compact group; the same
index domain at every level; scalar square or abs for real symmetric/antisymmetric
values; no other coordinate dependence. Never infer symmetry from `comm` of a checksum
kernel alone. Complex `x*x` is not Hermitian-orbit invariant. A nonzero seed at an inner
level is repeated logically and cannot be discarded; initially decline such seeds.
Preserve unseeded logical-empty BL8003 and explicit +0 seed semantics. p=1 strict rank 2
has a nonempty logical domain of zeros although its physical pool is empty.

**Surface spelling.** Proposed, not existing syntax: `reduce(pool(A), (+))` for the
canonical-cell sum. That is different from this logical full-domain sum. A future
full-domain `reduce(A, (+), axes=rank)` can use the same internal orbit plan once the
language's compact-fold refusal is resolved. For today's benchmark, recognize its
explicit logical loops; no source change is needed. G16's antisymmetric `A*A` typing
bug does not affect scalar reads squared here, but blocks casually advertising
`reduce(A*A,...)` as an equivalent surface spelling until fixed.

**LLVM/legality.** Reassociation license required at all collapsed folds; OFF under
repro. Emit canonical counted loops and class accumulator PHIs, keep only their addition
instructions licensed, and retain scale/transform semantics. In repro use S5 with the
original logical nesting. Do not compare this result's bits against today's sequential
logical reduction as an acceptance gate.

**Budget/payoff forecast.** Three scalar class states or four vector accumulators for
one active interior class fit below 16 registers; no divisions or permutations per cell.
Rank-2/rank-3 multiplicity classification belongs in row headers, not a branch per lane.
Comoment3 reads fall from ~5.0M logical reads (twice in C++) to 0.848M, 5.90x fewer
semantic fetches and 11.79x fewer emitted C++ reads. Pairkernel falls from 6,265,009
to 3,133,756, almost 2x semantic fetches, while removing canonicalization entirely.
Streaming those pools is 6.78 MB / 25.07 MB respectively. At an illustrative effective
20 GB/s, just reading costs 0.34 / 1.25 ms, plus reduction overhead; calling both
checksums universally 0.5 ms would be unjustified. Reduced-share extrapolation gives
remaining pairkernel base `42.6*.79=33.65 ms` LLVM and `77*.43=33.11 ms` C++, then
add this cheaper pass. A plausible forecast is ~34-37 ms, before S11 fusion. Comoment3
removes about 14.9 ms of 41.5 LLVM or 77 ms of 110 C++ by the same share model, but S3
is still required for expert-class arithmetic.

**Edges/verification.** Enumerate orbit counts and compare integer multiplicities with
p^2/p^3; test p=0,1,2, ragged row lengths, and every equality stratum. Raw-bit compare
against a scalar model of the selected class tree under each contraction policy;
separately report error versus Rserial and interpreter. Negative tests: odd transform
on antisym, complex square on Hermitian, extra coordinate use, different input binding,
nonzero inner seed, missing one license. Inspect FMA counts in the square-add loop,
ensure no logical accessor survives, and preserve all producer stores. Builds decide
bandwidth, small-p overhead, exceptional-value behavior, and interaction with threading.

## S7. Retain freed numeric pools without changing array ownership

**Target/current.** `A/recur.cpp:345` begins `step`; its seven local frees are :442-448,
and caller free :481 releases the eighth result after copying. Exact archived examples:

```cpp
auto __v60 = rhs(u);
deallocate<typename promote<double, 1>::type, nullptr>(__v60.data, __v60.extents);
```

`A/recur.ll:330` contains `%r214 = call ptr @blade_alloc_cells(i64 20011, i64 8)`;
the same allocation/free pattern occurs in the LLVM step at :296. C++ pools originate
at `src/cpp/nested_array_utilities.hpp:239` and :333, release through :399/:407 and
:453/:458. LLVM uses `src/cpp/blade_llvm_shim.c`'s `blade_alloc_aligned` and `blade_free`
(around :176 and :237). Both return uninitialized storage already; memset in either
allocator would reinstate a pass the compiler intentionally avoided.

**Proposed C++17 contract.** Start with bounded exact-capacity reuse, per arithmetic
element type, not a new universal allocator. Keep pointer-skeleton allocation unchanged.
Only a true owned pool base goes to release; borrowed rows never do.

```cpp
template<class T> struct Entry { T* base; size_t cells; };
template<class T> struct PoolCache {
    Entry<T> entry[16]; size_t used=0, bytes=0;
    T* acquire(size_t actual_cells) {
        // checked_bytes checks the multiplication before it is evaluated.
        const size_t b=checked_bytes<T>(actual_cells);
        for (size_t j=used; j>0; --j) {
            const size_t k=j-1;
            if (entry[k].cells==actual_cells) {
                T* p=entry[k].base;
                bytes-=b; entry[k]=entry[--used]; return p;
            }
        }
        // On bad_alloc: flush retained blocks once, retry, then propagate.
        return fresh_with_one_flush_retry<T>(actual_cells,*this);
    }
    void release(T* p,size_t actual_cells) {
        if (!p) return;
        const size_t b=checked_bytes<T>(actual_cells);
        if (!eligible(b)) { delete[] p; return; }
        while (used && (used==16 || b>budget-bytes)) evict_one();
        if (used<16 && b<=budget-bytes) {
            entry[used++]={p,actual_cells}; bytes+=b;
        } else delete[] p;
    }
    // Destructor/flush call delete[] for every entry, exactly once.
};
```

`eligible` and `budget` are a measured policy: tentative minimum 16 KiB, maximum single
block 8 MiB. For a first implementation, use one typed double cache and a small total
budget adequate for eight 160,088-byte pools (~1.28 MB); do not silently replicate a
32 MiB allowance for every type and every worker. Extending to several types needs one
thread-owned accounting object with typed deleters and a destruction-safe lifetime.
Metadata uses fixed slots so a free never recursively allocates. Allocation/free must
preserve original alignment and the original allocator/deallocator pair.

C++ `actual_cells=max(total,1)` is known at allocation. Deallocation must use the same
count, not logical zero and not a row extent. The existing multidimensional total==0
case explicitly loses the sentinel address (`nested_array_utilities.hpp:408-412`): do
not claim a cache fixes it. Keep it out of the initial cache path, or separately carry
the owned base in compiler lifetime metadata so it can be released without traversing
an empty spine. Rank-1 extent zero is straightforward because `data` is the pool base.

**LLVM C equivalent.** `blade_free(void*)` lacks a size. Use a 64-byte-aligned header
preceding the aligned payload; store rounded capacity and the true base there. Checked
allocation is `round_up(max(bytes,64),64)+64`, guarding both additions. Cache hit returns
the payload; eviction passes the true base to `_aligned_free`/`free`, never `payload-64`
from an unrelated allocation. Only pools allocated through this ABI may reach this free.
Keep the header's type-independent C storage rules valid; C++ typed pools should not be
reused across object types by merely casting a `void*` (C++17 object lifetime matters).
The current LLVM lane is single-threaded; a thread-local cache is still a useful future-safe
interface, but it does not make the rest of the lane thread-safe.

**Exact-size versus size classes.** Exact-size is sufficient for the measured recurrence.
A size-class free list helps varying shapes but needs allocated capacity metadata,
logical length kept separately, overflow-safe rounding, and a bound on retained waste.
It must not cause initialization or reads of spare capacity. Start without it. Cross-thread
free can release to the freeing thread's cache for plain owned numeric buffers, provided
the allocator permits it; do not route through an already destroyed original-thread cache.

**Legality.** No floating arithmetic changes; strict-legal. Reused memory remains
uninitialized. Every language-required zero is still emitted. Disable caching in memcheck
builds so delayed free does not hide use-after-free, and preserve leak accounting. A
compile-time cache-off define must actually be supplied; the environment variable is not
automatically a C preprocessor define. LLVM shim objects currently reuse by source timestamp
(`src/Build.fs:1207-1216`), so effective flags/defines/compiler identity must enter the
object and executable keys before shipping either cache policy. Fresh and cached ABI
objects cannot be accidentally interchanged. See S12.

**Budget/payoff forecast.** At most 16 scalar comparisons on alloc, no SIMD pressure,
no permutes. Eight 160,088-byte buffers occupy 1.28 MB, beyond L2 but well within L3;
the gain is retained commitment, not making eight arrays fit L1. The probe measured
7,308 faults versus 300 at 31 steps. Scaling the observed ~29.5 faults/allocation to
2,400 full-step allocations gives ~70,800 avoidable faults; using ~38 gives ~91,200.
Warm retained pools pay first-touch only once, roughly 313-320 4-KiB pages for eight buffers
before alignment overhead, instead of those repeat faults. The inferred 1.3 us/fault includes
other changes, so multiplying it into 92-119 ms is a mechanism budget, not a causal
prediction. Forecast a large fraction of the 117/134 ms disappearing; ~20-40 ms is a
testable initial range, wider than the plan's optimistic 20-30. Trajectory faults remain.

**Edges/verification.** Raw-bit compare all outputs with cache on/off under both FP
policies; vfmadd sites must be unchanged. Exercise rank-1 zero, strict triangle p=1,
rank-3 p<3, varying shapes, overflow, double-free diagnostics, allocation failure,
thread exit, and sanitizer builds after ordinary builds in the same directory. Measure
cache hits/misses, retained bytes, OS faults, cold and warm calls, and real frees on
shutdown. A cache must not expose stale values through missing zero initialization.
Only a build can establish heap behavior and a worthwhile retention threshold.

## S8. RK4 into trajectory rows: fuse a pointwise DAG, preserve recursion semantics

**Target/current.** `A/recur.cpp:465` writes `__orow___v363[__i1] = 0.0;` over the
whole trajectory. :477-481 then reads:

```cpp
Array<double, 1> __v368 = step(__v367);
for (int64_t __k369 = 0L; __k369 < 20011L; __k369++) {
    __v363[__k366][__k369] = __v368[__k369];
}
deallocate<typename promote<double, 1>::type, nullptr>(__v368.data, __v368.extents);
```

The per-stage flat loop and final expression are visible at :337-340 and :437-440.
Emission/lowering targets: `src/TypeCheckInfer.fs:13840` (`zerosValue`),
`:13901-13975` (guarded prefix reads), `src/Optimize.fs:422` (pool reuse, calls not
classified fresh), and the BLADE_FUSION pipeline. Function-return allocation must gain
an explicit borrowed-destination contract in both emitters, not a string substitution
that leaves cleanup freeing an interior pointer.

**Proposed C++17.** A scalarized pointwise DAG is lifted once over x. Scalar locals keep
multi-use k1/k2/k3 values without repeated evaluation:

```cpp
inline void step_into(size_t nx,const double* u,double* out) {
    // Compiler proved non-overlap, static matching extents, pointwise purity.
    BLADE_IVDEP
    for (size_t x=0; x<nx; ++x) {
        const double v=u[x];
        const double k1=v-(v*v)*v;
        const double a=v+0.005*k1;
        const double k2=a-(a*a)*a;
        const double b=v+0.005*k2;
        const double k3=b-(b*b)*b;
        const double c=v+0.01*k3;
        const double k4=c-(c*c)*c;
        out[x]=v+(0.01/6.0)*(((k1+2.0*k2)+2.0*k3)+k4);
    }
}
// Fresh uninitialized trajectory, after a definite-write proof.
if (nt!=0) std::copy_n(u0,nx,traj);
for (size_t t=1; t<nt; ++t)
    step_into(nx,traj+(t-1)*nx,traj+t*nx);
```

This preserves every row and time dependency; no rolling-buffer substitution for the
observable trajectory. The final checksum can be scheduled per row under S13. Allow
fusion only for pure pointwise regions over the same index identity: no halo/gather,
stateful random call, mutation, alias escape, or effect that changes diagnostic ordering.
The actual checked-expression DAG, including factor parentheses, is the scalar program.
An instruction scheduler may free local stage vectors after their last use, but must not
reassociate the final weighted sum merely to shorten live ranges.

**Definite initialization and alternatives.** Elide the full zero pass only when every
subsequently read cell is written first in the lowered control flow. A prefix read beyond
the built portion is semantically zero; keep its guard or materialize the required zero
region. Decline initially for `while` freeze arms, whole-prefix escapes, indirect prefix
indices, or incomplete seed coverage. A guard that fails at the first step may freeze a
zero seed row; deleting initialization would break it. `nt=0` must skip the seed copy.
`step_into` destinations are BORROWED in all return/error/cleanup paths, never deallocated.

For a nonfusible step, hoist explicitly owned scratch once per recurrence invocation:

```cpp
StepWorkspace ws(nx); // retains the exact simultaneous scratch lifetimes
for (size_t t=1; t<nt; ++t)
    step_into_with_workspace(prev_row(t),row(t),ws);
```

Use per-call workspace rather than a shared static to support reentrancy. This gives a
predictable bound and helps stencil steps that pointwise fusion cannot handle. S7 is
cheaper and general, but cannot prove those lifetimes or eliminate the extra passes.

**LLVM/legality.** Emit a destination-pointer function with no result allocation/tracking
slot; use the same zero-elision proof, not an LLVM-only guess about stores. Strict-legal
association. Under fast contraction, scalarizing formerly materialized intermediates
can expose new fusion across stage boundaries: match the chosen rounded-value contract
with constrained boundaries or report the difference; simple C++ `const double` is not
a guaranteed contraction barrier. Under off, raw-bit agreement follows the preserved DAG.

**Budget/payoff forecast.** At the final combination, v,k1,k2,k3,k4 plus constants and
one or two temporaries fit within 16 ymm if the DAG is scheduled sensibly. This is a
dependent stage chain, not eight independent FMA accumulators; vectorizing x supplies
four cells per instruction, with independent vector iterations hiding some latency.
No transpose/permutation is needed. Two 160-KB rows fit in L2 (320 KB) but not L1;
write them in contiguous x blocks. The trajectory is 48,186,488 bytes; the 300 step
rows total 48,026,400 bytes. Removing the zero pass saves ~48.19 MB stores. Destination
passing removes ~48.03 MB temporary stores and ~48.03 MB copy reads: ~144.24 MB logical
traffic, not 48 MB on each of 300 steps. Fusion also removes seven intermediates per
step and their reads. Actual DRAM savings depend on cache behavior.

Measured fused probe cost ~2.5 ns/cell-step times 6,003,300 steps is ~15.0 ms, before
pricing a changed full-size checksum/initialization envelope. That arithmetic does NOT
by itself justify the plan's ~11-13 ms full-size claim. Forecast ~15-25 ms for an initial
fused+destination implementation, with the 10.5/11.3 ms experts a lower comparison target;
additional gains need actual full-size evidence. Do not add S7's saving to fusion's
allocation saving: the latter removes those cache requests altogether.

**Edges/verification.** Raw-bit every trajectory cell against unfused Rinterp, then a
matched fast reference; include unstable values outside [0,2), signed zeros, nx=0..9,
nt=0,1,2, and guarded/freeze negative tests. Inspect all vector/scalar tails for equal
arithmetic. Count fast-mode mul-add sites by stage (four cubic subtract opportunities,
three stage additions, final weighted combination), compare actual fusion graphs rather
than demanding a guessed universal vfmadd total. Confirm no stage allocations, no final
copy, no blanket zero pass, and retained trajectory stores. A build settles live registers,
cross-statement contraction, and whether the predictor reaches expert class.

## S9. Rank-1 mask compaction with prefix counts and preserved coordinate identity

**Target/current.** Maskagg is absent from the two requested October-18 archives. The
existing pilot cited by acceptance-B supplies the actual generated example:
`bench/results/2026-10-17-LAPTOP-350H6LDB/session-231651/build/maskagg/full/serial-licensed-native/blade-cpp/generated/maskagg.cpp:245-250`:

```cpp
compound_index_t<1>* y_idx = new compound_index_t<1>("y_idx", y_idx_extents, y_idx_maskvec);
double* y_compact = new double[y_idx->cardinality * y_trail + 1];
```

The square temporary at :253 is `new double[y.idx->cardinality * y.trailing_stride]`.
Targets: `src/CodeGenBinding.fs:1869`, :1971 (index build), :1986 onward (scatter);
`src/cpp/index_types.h:194-195` stores rank-to-tuple plus reverse unordered_map, and
`compound_index_t` at :253 delegates to that machinery. LLVM currently refuses mask
and compound; runtime changes alone cannot make its lane support this source.

**Proposed C++17.** A rank-1 mask has a known dense origin domain, so reverse indexing
has an arithmetic prefix-count representation. Preserve it rather than replacing the
compound index with an unrelated `Idx<m>`. Straightforward baseline:

```cpp
// All buffers are sized with checked n+1/cardinality arithmetic.
prefix[0]=0;
for (size_t i=0; i<n; ++i) prefix[i+1]=prefix[i]+size_t(keep[i]);
const size_t m=prefix[n];
for (size_t i=0; i<n; ++i) {
    if (keep[i]) {
        const size_t q=prefix[i];
        y[q]=x[i];
        selected[q]=i; // rank -> original coordinate
    }
}
// original coordinate -> rank:
// check i<n AND keep[i]; then prefix[i]. Absent keeps the existing BL8006.
```

For the measured sequential producer, one count plus a running scatter cursor is
enough; the full prefix array is needed only if reverse lookups must remain O(1).
A more compact general representation uses bit words and per-word prefix counts:
`rank(i)=word_prefix[i/64]+popcount(bits[i/64] & low_bits(i%64))`, after membership
validation. `low_bits(0)=0`; never shift by 64. A selected-coordinate vector supplies
unhash. This keeps Compound's existing index provenance and prefix slicing behavior.
Retain mask bits while the compound index owns them; views borrow side tables just as
today, and only the constructor frees them. For a no-index-consumer local value the
compiler can omit unneeded reverse metadata, but it must prove those uses absent.

Parallel extension, once a source license exists: count selected elements in contiguous
input blocks, exclusive-scan block counts in input order, then scatter each block in
ascending i into its disjoint offset range. Never use a shared atomic increment: it
would reorder the selected output. A boolean predicate is evaluated once if it has
effects or can fail; speculative recomputation is allowed only for a proven pure predicate.
If `keep` is named/shared, preserve its materialization. Branchless overstore with +1
slack is safe only in the sequential variant; at parallel chunk borders it races with
the next chunk even when the overall allocation has a spare element.

**Legality/LLVM.** Stable selection is strict-legal; all y values are bit copies. Fuse
the later `y*y` producer into its consuming fold without dropping y's output stores;
repro keeps ascending selected order and the original seed/empty refusal. LLVM needs
IRMask evaluation plus an owning compact-value/index descriptor and release tracking,
then the same count/scatter loops. That is more than adding a fast allocator to the shim.
Rank>1 compound and arbitrary sparse keys stay generic initially; no triangle/simplex
canonicalization is involved in this specialization.

**Budget/payoff forecast.** Scalar prefix/scatter needs few registers; vector count or
predicate can use 2-4 vectors, but AVX2 lacks native compress-store. Avoid a permutation
storm to manufacture compression before eliminating the hash. n~1M prefix size is
8 MB; selected indices ~4 MB; bitset+word prefixes ~0.25 MB, plus selected ~4 MB,
versus tens of MB of separately allocated hash nodes. The measured 77.8 ns/kept cell
times ~500k = 38.9 ms is the removable hash cost. The pilot's 62 ms incremental could
therefore fall toward ~23 ms plus replacement bookkeeping; forecast ~23-30 ms for the
basic change, with further gains only after square-temporary/fault removal is measured.
This is not a prediction from the six headline medians, which omit maskagg.

**Edges/verification.** All-false keeps m=0 and the current unseeded checksum refusal;
all-true, alternating, singleton, long gaps, last selected then false tail, and lengths
around 64 and the parallel block size. Compare y raw bits and exact selected coordinates,
linearize/unhash round trips, missing-key diagnostic, and order-sensitive fingerprints.
The compaction itself must introduce zero FMAs; square-fold FMA counts follow its chosen
policy. Check no per-cell unordered_map insertion and no cross-chunk overstore.
Only a build can choose full-prefix versus bitset metadata, branchy versus sequential
branchless scatter, and demonstrate the LLVM ownership path.

## S10. SIMD inside deferred Path B chunks, with actual-team and seed semantics

**Target/current.** Threaded archive
`B/gram/full/mt8-licensed-native/blade-cpp/generated/gram.cpp:314-318`:

```cpp
double __racc___v54 = double();
bool __rhas___v54 = false;
for (size_t __i0 = __rlo; __i0 < __rhi; __i0++) {
    int64_t i = __i0;
    { double __rv = __lambda_74(i, __v27); if (__rhas___v54) __racc___v54 = __wrap_75___v54(__racc___v54, __rv); else { __racc___v54 = __rv; __rhas___v54 = true; } }
}
```

Target `src/CodeGenLoopNest.fs:1354-1392` and its chunk body, plus the routing/census
at `src/CodeGenBinding.fs:3827-3828`. Correction to an overly broad reading of G25:
materialized Path B already emits K-lanes at `CodeGenBinding.fs:3239`; deferred chunks
are the demonstrated remaining serial form. S4's explicit vectors are not automatically
appropriate when each outer element invokes an entire row fold, as this archive does.

**Proposed C++17.** For a pure, rectangular, builtin-add scalar producer, use a private
SIMD fold inside each contiguous thread chunk. Reuse the emitter's expression builder
instead of calling an opaque lambda for each lane. The high-level emitted skeleton is:

```cpp
struct alignas(64) Partial { double value; bool has; };
std::vector<Partial> partial(requested_threads); // value-initializes unused slots
BLADE_OMP_PARALLEL_NUM_THREADS(requested_threads)
{
    const size_t team=blade_omp_get_num_threads();
    const size_t tid=blade_omp_get_thread_num();
    const size_t q=n/team, rem=n%team;
    const size_t lo=tid*q+std::min(tid,rem);
    const size_t hi=lo+q+size_t(tid<rem); // avoids n*tid overflow
    if (lo<hi) {
        double local=0.0; // only for proved builtin addition identity
        BLADE_OMP_SIMD_REDUCTION(+:local)
        for (size_t i=lo; i<hi; ++i) local += producer(i);
        partial[tid]={local,true};
    }
}
double result=source_seed;
for (size_t t=0; t<partial.size(); ++t)
    if (partial[t].has) result=combine(result,partial[t].value);
```

Replace the single-accumulator SIMD interior with S4 when it is a contiguous dot/square
producer; for a vectorizable general map use the same four-vector fold skeleton. The
OpenMP syntax above defines the location and ownership of the fast fold, not a promise
that one `simd` accumulator fixes G26. A generic associative commutative kernel without
an identity instead seeds each occupied lane from its first real element and carries a
`has` bit; empty lanes are omitted. The user seed occurs once, in the final combine,
not once per thread or lane. For unseeded folds, preserve the original first-element
and logical-empty behavior. The proposed balanced partition is deterministic for the
actual team, even if OpenMP launches fewer workers than requested.

Nested row bodies: keep their separate inner-fold licenses. If the outer producer cannot
be vectorized, interleave a few independent row calls only if it helps; it is not enough
to append `omp simd` above a loop containing serial calls. A reusable logical-to-orbit
plan (S6) or S11 fusion removes this particular checksum more effectively. Do not transfer
an outer fold's license onto an unlicensed inner fold. G14/G27 need a distinct threading
decision: propagate an existing map license to independent rows, or emit a clear decline;
`prodsum` should not manufacture a new thread license from its reassociation permission.

**LLVM/legality.** Licensed only; repro keeps the original serial fold, including seed
handling. LLVM has no thread runtime today: use the serial vector form and an accurate
census. A helper that secretly starts a team would violate the emitter's existing `nosync`
assumptions. No thread-sized partial array is needed in the present serial lane.

**Budget/payoff forecast.** Four vector dot accumulators plus 2-3 operand vectors fit
below 16; thread partials stay in scalar memory outside the hot loop. alignas(64) partial
slots cost 512 bytes at eight threads, preventing false sharing if future code updates
them repeatedly (one final store alone makes false sharing a small effect). Three-stream
producer pressure must count its live values, not just the fold variables. Ideal scalar
FMA chain is 0.25 lane/cycle; a two-stream vector fold can reach four lanes/cycle, a 16x
local ceiling improvement, not a 16x whole-program forecast. DRAM bandwidth and row-body
work substantially limit it. No standalone measured Path-B share supports a millisecond
prediction here; expect the largest benefit for cheap cache-resident flat producers,
and little from merely changing the outer sum of expensive row calls.

**Edges/verification.** n=0,1,team-1,team,team+1, nondivisible chunks, runtime-shrunk
teams, nested parallel invocation, empty inner triangle rows, and rank-3 subdomains
yielding no cells. Raw-bit against the fixed chunk+lane-tree reference; separate serial
error check; repro must be bitwise the untouched serial traversal. For a dot4 chunk
expect four packed FMA sites per main step, not scalar-only FMA; check threads and SIMD
both survive. Builds settle inlining, nested-fold scheduling, and team overhead. Census
should distinguish `threaded + vector(4x4)`, generic K-lanes, and scalar fallback.

## S11. Fuse the licensed orbit checksum into producer stores, including threads

**Target/current.** `A/pairkernel.cpp:299` stores each canonical Matérn result, then
:304-313 starts a separate logical checksum. Exact subsequent line:

```cpp
__v58 = __v58 + __lambda_76(i, __v33);
```

The checksum lambda performs the duplicate canonical reads at :238. Gram/comoment3/
antisym have the same materialize-then-logical-read pattern. Target a producer-consumer
rewrite in `src/Optimize.fs`, compact stores in `src/CodeGenLoopNest.fs:1251`, and the
fold consumer in `src/CodeGenBinding.fs:3397`; LLVM materialize at :4047 must consume
the same proven fold plan. These sites are integration proposals, not an existing pass.

**Proposed C++17.** S6 first proves that a logical fold is an orbit-invariant class sum.
Then attach a typed checksum epilogue to the producer, retaining its ordinary stores:

```cpp
struct ClassSum { double one=0.0, three=0.0, six=0.0; };
// Private state per worker, combined only after producer completion.
ClassSum local;
for (const TileJob& job : owned_jobs) {
    compute_tile(job); // no checksum accumulator live across the sample loop
    for_each_valid_final_cell(job,[&](size_t i,size_t j,size_t k,double value) {
        output[packed_offset(i,j,k)]=value; // REQUIRED materialization
        const double square=value*value;
        if (i==k) local.one += square;
        else if (i==j || j==k) local.three += square;
        else local.six += square;
    });
}
// Combine workers' class states; apply S6's multiplicity trees once afterward.
```

The illustrated rank-3 epilogue assumes i<=j<=k. Rank 2 needs diagonal/off-diagonal
states; strict rank 2 needs only off-diagonal. Pairkernel can accumulate the local
value immediately after `matern52` returns, avoiding the separate scalar exp call's
result reload. S1-S3 tiles run this epilogue only on their **last K block**, after
final scaling or P-Q subtraction. Never checksum unscaled partials. Named outputs
remain materialized and observable even when the checksum is their only local reader.

Use tile-local or per-worker class states, not a shared atomic scalar. Do not keep
three extra ymm accumulators live beside S1's 15-register core: finalize the tile,
release operand/broadcast registers, fold a row fragment, and spill tiny class state
between jobs if necessary. Classification can be hoisted into contiguous row runs;
the generic lambda above expresses semantics, not a reason for per-cell branches in
an off-diagonal interior. Final worker reduction is licensed; a dynamic scheduler gives
legal but schedule-dependent bits. If fixed-team repeatability is promised, choose
deterministic job partitions or store one small state per job and combine in job order.

**Legality/LLVM.** S6's complete licenses, type whitelist, seeds, and emptiness guards
apply; the producer's map license is separate. No use of the output may intervene
between production and its fold in a way that changes values. Pure reads of other
bindings may be rescheduled only when diagnostics/effects remain valid. Under repro,
do not scatter contributions into logical row sums as cells are produced: that changes
the logical row accumulation order. Keep the logical pass with S5, or use the separate
strict row-local optimization S13 for dense arrays. LLVM can return an extra class
state from a serial helper or emit epilogue PHIs; thread support is still explicit.

**Budget/payoff forecast.** Removing S6's canonical pool pass additionally avoids
6.78 MB comoment3, 25.07 MB pairkernel, 10.26 MB gram, or 5.76 MB antisym reads per
call. At an illustrative 20 GB/s that is 0.34/1.25/0.51/0.29 ms of traffic, plus fold
loop overhead; it is NOT another 14-17 ms saving after S6. Relative to the untouched
logical pass it removes the full tax, so combined S6+S11 pairkernel forecast is roughly
33-35 ms from the share-derived ~33 ms base, approaching the 31/33 ms experts. Register
pressure or extra scalar FMA dependency in a producer may consume some of the gain.
Store-forwarding is avoided by using the computed value, not loading an unaligned vector
that spans freshly stored scalar cells. Under eight threads, eliminate the second
team/pass; do not predict ideal 8x when scalar exp or shared bandwidth binds.

**Edges/verification.** Count exactly one store and one checksum contribution for every
valid final cell; none for diagonal of strict triangles, padding, empty rows, or earlier
K blocks. Test rank-3 equality weights and repeated materialization consumers. Raw-bit
all output cells against the producer reference; checksum against the chosen class/job
tree, not Rserial. Under fast, expected additional square-add FMA opportunities occur
only in epilogues; the tile core's twelve/eight count must remain unchanged without
spills. Inspect surviving output stores, no post-production logical accessor pass,
and no racing scalar reduction. Builds determine whether saving the pass outweighs
the checksum epilogue pressure, especially on short contractions.

## S12. LLVM tiles: one arithmetic core, policy-specific objects, two honest drivers

**Target/current.** `A/gram.ll:154` allocates the packed output, :157-190 builds 64-row
bricks, and :200-203 still emits a scalar dot chain:

```llvm
%r74 = fmul contract double %r71, %r73
%r75 = load double, ptr %r63
%r76 = fadd reassoc nsz contract double %r75, %r74
store double %r76, ptr %r63
```

This is cache tiling, not register tiling. Targets are `src/EmitLlvm.fs:4047` plus
contraction matching near its outer-product construction, flag emission :1004, and
`src/Build.fs:1207` (shim build). Existing `buildLlvmShim` uses `-c -O2` and timestamp
reuse; dropping numerical kernels into that object would neither target native AVX2
reliably nor segregate contraction policies.

**Proposal A: emit vector IR.** Use explicit vector PHIs per cell group:

```llvm
; one of twelve accumulator vectors; no horizontal reduction
%c00 = phi <4 x double> [ %initial00, %entry ], [ %next00, %body ]
; loads address packed s-major panels; the broadcast is insertelement + shufflevector
%prod00 = fmul contract <4 x double> %broadcast_a0, %b0
%next00 = fadd contract <4 x double> %c00, %prod00
```

Under off both instructions have no contract flag. The broadcast shuffle should lower
to one memory broadcast, not a hot-loop permute network. This route gives the emitter
control of target features, alias facts, tails, sanitizer instrumentation, and later
fusion; it requires duplicated driver/edge code and careful maintenance of the C++/IR
arithmetic contracts. Keeping a reusable vector-tile IR builder would reduce duplication
across S1-S3, but is substantial implementation effort.

**Proposal B, preferred first: shared pure arithmetic core.** Factor the vector kernels
into a small C-compatible header compiled by C++ and by a new C translation unit. The
core allocates nothing, starts no threads, stores no global scratch, and knows only
panels, valid row lengths, and ordered scalar arithmetic. C++17-facing declaration:

```cpp
extern "C" void blade_tri_syrk_d(
    size_t p,size_t n,const double* x,size_t ldx,double* packed,
    double* ap,size_t ap_cells,double* bp,size_t bp_cells);
// Separate anti/sym3 entries: exact component/scale contracts, no enum guesswork.
```

The LLVM driver owns scratch allocation/lifetime and calls this **once per contraction**,
not once per cell or per sample. A C macro-panel driver can live in the same new TU if
IR driver generation is too large. C++ keeps its own OpenMP/scratch driver and calls
the shared static-inline core. The C header can use C99/C11 loops and vector extensions;
the exported ABI avoids C++ templates, exceptions, `Scratch`, TLS destructors, and `bool`
layout assumptions. Scratch capacities and p*n sizes are validated before pointer math.

Compile the numerical TU once **per resolved build policy and compiler/target identity**,
not once forever. Key source/header content, compiler identity, effective target/ISA,
optimization/contraction flags, cache-off define and sanitizer instrumentation. Include
that object identity in the final executable cache key. `-march=native` text alone is
not a target identity; existing `clangNativeTargetIdentity` at `Build.fs:1250` supplies
the relevant pattern. Keep the printing shim's flags separate; changing its optimization
level globally to obtain AVX2 in one kernel risks unrelated runtime differences.

The shared recognizer returns ordered operands, exact layout/strides, strictness, scale,
and required arithmetic policy. Attach it only to the exact deferred contraction value;
invalidate after views/maps/slices that change meaning, resolve live pools at materialization,
and do not hoist a helper across mutations. A same-array proof is not an alias permission
between its input and a fresh output. The emitted helper declaration must have truthful
memory effects: it writes output/scratch, is not readnone, and is nosync only if the whole
called implementation remains serial. Caching scratch in an external shared mutable
global needs its own concurrency reasoning; it is not part of the pure core.

**Legality, budget, and payoff forecast.** Both routes implement strict-legal S1-S3;
sharing text reduces drift but does not force g++ and clang to make identical contraction
choices. Both inherit the 12-chain/15-register 6x8 or 8-chain/11-register 4x4 budgets.
ABI overhead once per O(p^2*n) contraction is negligible in this arithmetic model;
per-microtile external calls could inhibit allocation and epilogue optimization and
must be measured. S1's 157 -> ~80-100 ms LLVM forecast is the payoff; S12 adds no extra
speedup to that number. It is the delivery choice, with the C core usually less effort
and IR offering more long-term optimization visibility.

**Edges/verification.** Identical vector-core tests in g++ and clang for every triangle,
simplex, ragged, empty strict row, and multi-K-block case from S1-S3. Raw bits versus
each policy-matched scalar reference; cross-lane bits required under off. Inspect FMA
counts and absence of stack traffic at full and masked edges. Switch normal/off/fast/
memcheck builds in one output directory to demonstrate no stale object reuse; vary the
target identity too. The existing simplex plan's failed cache-bricking result does not
refute register reuse, nor does it prove a new native helper wins. Only future builds
answer codegen quality, object-cache correctness, and whether IR specialization beats
the shared core enough to justify maintaining it.

## S13. Strict dense row scheduling: produce, fold the hot row, combine rows in order

**Target/current.** Stencil `A/stencil.cpp:243` stores the Laplacian; only after the
whole image is filled does :255 execute:

```cpp
__v48 = __v48 + [&]() { const size_t __pn = __v39____i0.extents[0]; double __ps = 0; BLADE_OMP_SIMD_REDUCTION(+:__ps) for (size_t __pt = 0; __pt < __pn; __pt++) {     __ps = __ps + __v39____i0[__pt] * __v39____i0[__pt]; } return __ps; }();
```

Recur does the analogous trajectory read in `A/recur.cpp:492`. Targets: producer/consumer
scheduling in `src/Optimize.fs`, halo/map emission `src/CodeGenLoopNest.fs:1251`,
`src/CodeGenBinding.fs:3397`, and LLVM materialization/reduce emission. The existing
row-fold jam near `CodeGenLoopNest.fs:2401` is relevant to row reductions, but does
not already fuse this producer with the checksum and is not the SYRK tile of S1.

**Proposed C++17.** For an independent dense producer row, schedule its existing row
fold immediately afterward. This preserves the exact fold body, contraction, and
vectorization policy, instead of fusing a scalar checksum recurrence into the stencil's
vectorizable production loop:

```cpp
for (size_t r=0; r<ny-2; ++r) {
    double* dst=lap+r*(nx-2);
    produce_laplacian_row(r,u,dst); // unchanged grouping, bounds, stores
    row_sum[r]=same_row_prodsum(dst,nx-2); // strict or licensed original policy
}
double checksum=0.0; // preserve the actual source seed
for (size_t r=0; r<ny-2; ++r) checksum += row_sum[r];
```

With an existing producer row-map thread license, workers can execute whole rows and
fill distinct `row_sum[r]`; the final combine remains serial ascending in repro. This
is parallel evaluation of independent row values, not parallel reduction of one row.
If the original outer fold was unseeded (recur), seed from `row_sum[0]` and refuse on
logical empty, rather than introducing +0. For serial recur, consume each completed
time row before advancing; after S8 this reads only the newly written row, retaining
all trajectory stores. Its recursion still advances t sequentially. Do not create
an x-thread license that the source does not carry.

A second candidate fuses the value's square directly into the producer's loop. Under
repro that imposes a scalar checksum dependency and may destroy packed stencil arithmetic;
the two adjacent row loops are the safer first schedule. Under a license S4's self-dot
or a true producer+SIMD reduction can be compared. Reuse a row scalar array of size ny,
not a second image of squares. Validate source exceptions/bounds once before parallel
row work so reordering pure rows does not change observable failures.

**Legality/LLVM.** Strict-legal if each row fold and the outer fold retain their own
operation order and seeds. Order between independent rows' computations is irrelevant;
reduction order within a row is not. LLVM can emit the same adjacent loops with its
original flags. This is intentionally distinct from S11: no orbit weighting and no
logical mirrored read is removed. A packed triangle/simplex logical checksum cannot
be treated as a dense completed row, because mirrored dependencies and traversal order
differ; leave it on S5 under repro.

**Budget/payoff forecast.** Three input stencil rows total ~48 KB, output row ~16 KB;
together they exceed 32 KiB L1, but are comfortably below 512 KiB L2. The next-row
reuse and an immediate row fold avoid a distant 24 MB image reread (image exceeds
16 MiB L3). At an illustrative 12.5-20 GB/s, a cold 24 MB pass costs 1.2-1.9 ms.
Forecast a <=roughly 1-2 ms opportunity on stencil's 13.5/16.3 ms, not a large arithmetic
win. Existing measured incremental halo medians are equal between lanes; the C++ input
generator explains much of their overall difference, so a new stencil microkernel is
not indicated. Recur's removed distant read is ~48 MB, but its measured checksum share
was tiny before allocation fixes; measure only after S7/S8. Row sums cost ~12 KB for
stencil, keeping metadata near L1. Production and fold registers are live in separate
loops, avoiding both 16-register pressure and forwarding from partial scalar stores.

**Edges/verification.** nx/ny at minimum legal halo extents, zero interior, short/ragged
row lengths, nt=0/1, and ordinary recurrence tails. Preserve BL8009/BL8003 decisions.
Raw-bit all output cells and both nested fold levels under repro/off, then matched fast.
Count the same FMA sites in each relocated loop as before; there should be no new
horizontal reduction in repro and no loss of vector production. Verify original output
stores remain. Only builds settle whether adjacency alone keeps enough of the row hot,
whether fusing into one loop is profitable under license, and the true input-generator
share of the full-size stencil difference.

## S14. Fold input row sums into packing, preserving first-element seeds

**Target/current.** Gram `A/gram.cpp:312-316` opens a separate input-checksum row loop.
Its exact row binding is:

```cpp
Array<double, 1> __v21____i0 = { __v21.data[__i0], __v21.extents + 1 };
```

The following IIFE explicitly seeds `double __r = __v21____i0[0];` and sums the row.
The input pool was already read to build the contraction. `A/recur.cpp:469-474` also
shows a smaller avoidable preparation copy: `u0 -> __v364 -> trajectory row 0`.
Target S1's transposing pack driver and the consuming fold plan in
`src/CodeGenBinding.fs:3397`, with the ownership/purity analysis in `src/Optimize.fs`;
LLVM's same plan can attach input statistics to the serial packing helper. This is
compiler-derived shared traversal, analogous to the independent-chain fusion audited
for ifx in `bench/notes/fortran-notes.md:565-600`.

**Proposed C++17.** A B panel owns each input row exactly once per ascending pc block,
so sum it during that pack, not also during the repeated A packs:

```cpp
// One 8-row panel, initialized from first elements as the source reduce does.
double sums[8] = {};
for (size_t q=0; q<nr; ++q)
    sums[q]=(pc==0 ? X[(j0+q)*ldx] : input_row_sum[j0+q]);
for (size_t s=0; s<kc; ++s) {
    for (size_t q=0; q<8; ++q) {
        const double x=q<nr ? X[(j0+q)*ldx+pc+s] : 0.0;
        B[8*s+q]=x;
        if (q<nr && (pc!=0 || s!=0)) sums[q] += x;
    }
}
for (size_t q=0; q<nr; ++q) input_row_sum[j0+q]=sums[q];
// Once all samples/rows are packed, combine row sums in the source's outer order.
```

The example requires n>0; an unseeded empty row still raises the source's BL8003.
Every pc continues the saved row sum, not `row_sum += sum(pc)`. No missing sample is
invented for n tails. Eight independently ordered row chains can form two vectors
without any horizontal reduction; any transpose/shuffles belong to packing, outside
the much longer contraction body. A row-wise copy/pack variant may be better on g++:
choose based on packing throughput, not just how compact the source sketch looks.

If the driver repacks B, nominate one unique traversal to update the sums; never count
the same row again. Thread workers may pack disjoint row panels, then a barrier precedes
the final ascending row-sum fold. Antisym keeps X and Y row sums separate and combines
`sumX(i)+sumY(i)` in source order before the outer fold; summing interleaved X/Y terms
would change it. Comoment3 uses its raw Xk pack, not the rounded W panel, for statistics.
For tiny recur initialization, directly copy u0 to the seed row after proving no need
for the intermediate owned copy; this saves 160 KB each of temporary write/read, a
small separate cleanup, not a new explanation of its 100-ms gap.

**Legality/LLVM.** Strict-legal with the original first-element/+0 seed at each level,
same source data and ascending samples. Every input remains stored as required by the
protocol. Do not move a potentially failing unseeded reduction ahead of an observable
kernel call; initial match requires statically nonempty, pure operations. If bounds
or effects prevent safe fusion, decline. LLVM can express two vector PHIs across panel
rows or call the shared packer. Avoid granting `reassoc` merely because independent
chains sit beside each other.

**Budget/payoff forecast.** Eight scalar row sums can use two ymm registers when SLP
packs independent rows; even scalar allocation stays below the 16-register limit.
They do not coexist with twelve contraction accumulators because packing is a separate
phase. Extra work is one add per input cell, required by the original program; saving
is a separate read and traversal. Gram reads avoided: `1601*1001*8=12,820,808` bytes;
antisym: ~7.71 MB for both inputs; comoment3: ~0.275 MB. At 20 GB/s the pure traffic
budget is ~0.64/0.39/0.014 ms. Repro may gain more from overlapping independent chains
than from bandwidth alone, but no millisecond figure for that is measured here. Small
payoff means low priority after S1-S11, and it must not slow the pack enough to lose
the saving. Do not turn the ~13 MB opportunity into a forecasted multi-fold kernel win.

**Edges/verification.** p=0/1, n=0/1, pc tails, j0 tails, empty strict final output row
(its input row still belongs in the input checksum), and rank-3 reused panels. Raw-bit
row sums and final input checksum versus the source's serial tree; test values with
cancellation and -0, not just the suite's exact dyadic inputs. There are no new FMAs
in the packing sum; contraction tile counts must remain unchanged. Inspect that each
input coordinate is counted once, no stack spills/permutation storm slows the pack,
and original input stores survive. Builds decide whether this fusion is profitable
and whether direct recurrence seed copying is ownership-safe in every lowered branch.

## Ranked by expected payoff per implementation effort

Effort is relative: S = a localized emitter/runtime change, M = a shared pattern plus
runtime/emitter work, L = substantial analysis or a new supported representation.
Ranking is a sequencing judgment, not a measured ratio. All time ranges are forecasts;
where no isolated measurement supports one, the table says so. Combined scenarios are
not additive. S12 is an enabling route for LLVM, not another optimization multiplier.

| Rank | Sketch | Effort | Forecast / measurable opportunity | Dependencies or principal risk |
|---:|---|:---:|---|---|
| 1 | S7 retained numeric pools | S-M | Recur 117/134 -> roughly 20-40 ms; ~71k-91k repeat faults avoidable | Ownership, bounded retention, sanitizer/object-cache policy |
| 2 | S5 inline canonical reads | S | C++ comoment3 ~49 ms, pairkernel ~42 ms from reduced-share extrapolation | Flat-pool proof; valuable in repro too |
| 3 | S6 orbit-class checksum | M | Remove most of ~36%/70% comoment3 and ~21%/57% pairkernel checksum shares | All-fold licenses, seeds, typed orbit invariance |
| 4 | S1 packed 6x8 SYRK | M | Gram ~80-100 ms versus 157/275; much larger repro gain | Existing pattern/core reuse; KC and spill audit |
| 5 | S4 explicit vector prodsum | S-M | Up to ~1.9x kernel headroom suggested by 4.4/8.4 G/s; less when bandwidth-bound | Avoid documented gemv/SLP regressions; not additive to tiles |
| 6 | S9 stable rank-1 compaction | M C++, L LLVM | ~38.9 ms hash cost removed; pilot incremental ~62 -> ~23-30 ms initially | Preserve Compound index identity and stable order |
| 7 | S2 paired strict contraction | M after S1 | Antisym ~34-42 ms versus 74/108 | Two ordered dot chains; 4x4 candidate versus 6x8 baseline |
| 8 | S3 W-panel simplex tile | M-L after S1 | Combined with S6, comoment3 ~13-18 ms versus 41.5/110 | Factor order, simplex padding, divide once |
| 9 | S12 shared LLVM tile core | M | Delivers S1-S3's LLVM gains; no independent extra speedup | Policy-keyed objects; honest ABI and memory effects |
| 10 | S8 fused RK4 into rows | L | Roughly 15-25 ms initially; removes stage arrays and ~144 MB extra logical traffic | Definite initialization, borrowed destinations, contraction |
| 11 | S10 vector folds inside chunks | M | Local dot ceiling .25 -> 4 lanes/cycle; whole-program share unmeasured | Only deferred gap remains; nested-body limits |
| 12 | S11 producer/checksum fusion | M after S6 | Beyond S6, removes 0.3-1.3 ms illustrative pool-read budgets plus loop/team overhead | Last-K-block only; epilogue register pressure |
| 13 | S13 strict hot-row checksum | S-M | Stencil ~1-2 ms opportunity; recur reassess after allocation fixes | Keep row and outer seed/order; avoid scalarizing producer |
| 14 | S14 pack/input-sum fusion | M after tiles | Gram ~0.64 ms illustrative read budget; smaller for other kernels | Count each row once; must not slow packing |

## Open questions that require a build (none answered by this document)

1. Do g++ and clang fully scalar-replace the constant-index 6x8 accumulator arrays without
   spills in both contraction modes, including masked loads/stores? Does a 4x8 off-mode
   specialization beat it? Are the expected packed FMA counts accompanied by the same
   contraction graph as the serial reference, especially at tails and final subtraction?
2. Which KC/MC policy wins at non-power-of-two extents: 256's reuse or 192's L1 headroom?
   What is actual packed C traffic, and do smaller diagonal microtiles earn their dispatch
   cost? For antisym, where does simultaneous 4x4 beat sequential 6x8, if anywhere?
3. How much rank-3 lane waste does the explicit S3 schedule execute at p=171 and small p?
   Does fixing i keep the output hot enough, and is repacking at awkward boundaries ever
   cheaper than accepting padding? How do divide epilogues affect useful-term throughput?
4. Does explicit vector prodsum avoid the historical SLP shuffle storm across gemv,
   triple products, and ragged tails? When does self-dot merit eight chains instead of
   four? Does the chosen combine tree stay stable when permitted flags are applied?
5. Do checked flat offsets beat specialized pointer-spine access everywhere, or should
   invariant row bases be selected by access pattern? Are compare/swap networks always
   inlined, with duplicate reads actually eliminated?
6. What are full-size checksum shares after S5 and S6, under matched thermal conditions?
   Does S11's epilogue improve total time without spilling tile accumulators or weakening
   producer vectorization? What repeatability guarantee is intended for threaded jobs?
7. Which retention threshold removes Windows repeat faults without excessive committed
   memory? How many buffers are live/retained in actual optimized step code? Do real
   frees, thread teardown, failure recovery, and cache-off sanitizer builds work after
   normal builds in the same directory, without stale shim objects?
8. Can fused RK4 preserve rounded stage boundaries under fast contraction without costly
   barriers? Does the full-size implementation reach the probe's ~2.5 ns/cell-step or
   improve on it? Does a nonfusible recurrence benefit more from workspace hoisting than
   bounded allocator reuse? Do freeze/zero-prefix cases retain exact semantics?
9. Which rank-1 index representation wins: full prefix, bitset+word prefix, or selected
   coordinates plus binary search when reverse lookup is rare? How much time remains in
   scatter, square materialization, and input generation after removing the hash nodes?
10. Do deferred chunk bodies actually vectorize after inlining? How much of their time
    is the serial chunk combine versus inner row computation? Are source thread licenses
    honored or explicitly declined in row-map, tuple-position, and prodsum routes?
11. Does the shared C core match direct LLVM vector IR inlining/specialization quality?
    Are resolved target, contraction, sanitizer, and header-content changes all reflected
    in object/executable identity? Can the lane continue to assert nosync honestly?
12. Are S13/S14 gains visible in complete timed regions, including generation, allocation,
    both checksums, and required materialization? The stencil's input-generator gap and
    ifx's independent-loop fusion need separate attribution, not a new stencil-core claim.

Future acceptance should use raw-bit kernel tests with contraction off first, matched
fast-contraction tests second, then licensed-tree/numerical-error tests. Audit required
stores and helper routing in the built artifacts, not plan text alone. Performance runs
should interleave A/B, use medians at non-power-of-two extents, report skips and thermal
drift, and measure both full regions and narrowly controlled attribution variants. These
are proposed verification steps only; no such build or run was performed for these sketches.
