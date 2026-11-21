# twofir — resident two-output FIR over one stored input (sibling-map fusion)

Group B (language-level structure). Directory `bench/kernels/twofir/`. Round 3, catalog
workload N07 (`round3/research/C-workloads.md` §N07), scoped by the Codex review (C/13):
this is the **resident two-output FIR / sibling-map fusion** experiment, NOT a ghost-reuse
experiment. A resident `Chunked<I, kc>` axis is invisible to a halo walk — chunk edges are
not boundaries, the windows cross them as a flat halo, and the run-plus-ghost path
(`CodeGenCuda.segmentRun`) exists only for a STREAMED source — so the source uses a plain
`Idx<n>` and no chunk parameter. A file-backed ghost-reuse benchmark needs its own provider
and cache protocol and is a separate, later spec.

## Object and operation

x ∈ [0, 1)ⁿ (0-based). Two direct FIR filters over the same stored input, each over its own
interior (the windows shrink the axis by 3 and by 8 on each side):

    y1(i) = 0.0625 x(i−3) + 0.125 x(i−2) + 0.1875 x(i−1) + 0.25 x(i)
            + 0.1875 x(i+1) + 0.125 x(i+2) + 0.0625 x(i+3)          3 <= i <= n−4   (n − 6 cells)
    y2(i) = 0.5 x(i) − 0.25 x(i−4) − 0.25 x(i+4) + 0.125 x(i−8) + 0.125 x(i+8)
                                                                     8 <= i <= n−9   (n − 16 cells)

each evaluated left to right as written. **Named outputs: y1 and y2**, both materialized
(different output domains: y1 has ten centres — 3..7 and n−8..n−4 — outside y2's domain).
Scalar results may be fused freely; the two stores must happen in every arm.

Every tap coefficient is a dyadic rational (1/16, 1/8, 3/16, 1/4, 1/2) and every input is a
multiple of 2⁻³¹ below 1, so every product and every partial sum of a cell is EXACT in
binary64: y1 and y2 are bit-identical in every arm, every FP regime and either contraction
mode (verified at smoke size: gfortran natural/expert dumps bitwise equal to the no-FMA
oracle). Only the checksum's own summation order differs between arms.

## Inputs

| array | shape | k | seed | lo | hi |
|---|---|---|---|---|---|
| x | (n) | k | 10000019 | 0.0 | 1.0 |

(The same x as fusedchain at the same n: the two input checksums agree.)

## Checksum

    s1 = Σ_i y1(i)²,   s2 = Σ_i y2(i)²       two separate chains, each seeded with +0.0, ascending i
    checksum       = s1 + s2                 (one add of the two finished sums)
    input_checksum = Σ x

All terms are non-negative. The input sum is exact in every order (multiples of 2⁻³¹ with a
total below 2²²), so every arm's `input_checksum` equals the pin exactly.

## Sizes

| variant | n | x | y1 | y2 | principal payload | est. serial (planning aid) |
|---|---|---|---|---|---|---|
| smoke | 20011 | 160 KB | 160 KB | 160 KB | 0.48 MB | < 0.3 ms |
| full | 2,000,003 | 16.0 MB | 16.0 MB | 16.0 MB | **48.0 MB** | 10–25 ms |

The 64 MB rule is checked with the live input included (review C/8): x is live through
both FIRs and the input sum, so the payload is x + y1 + y2 = 48.0 MB. (The brief's
n ≈ 3,000,017 would be 72 MB and is over the cap.) n is odd and not a power of two.

**Storage estimate per arm (full size; payload + scratch).**

| arm | named arrays | compiler / arm scratch | peak estimate |
|---|---|---|---|
| blade-cpp | x, y1, y2 | none in the emit (both checksum folds stream over the stored outputs; mt8 Path B partials are T doubles) | 48.0 MB |
| blade-llvm | x, y1, y2 | not audited beyond the smoke run (same named arrays) | 48.0 MB |
| natural | x, y1, y2 | none | 48.0 MB |
| expert | x, y1, y2 | none | 48.0 MB |
| every inputonly twin | x | none | 16.0 MB |

Input share: generation of 2M cells against two memory-bound passes — about 35–50% of the
region at smoke size in the check session (one run per arm, not a measurement) → **an
`inputonly` twin per arm is REQUIRED** (kernels/README.md §3.3). R >= 21 (memory-bound).

## Tiers (natural = dense baseline; expert = reviewed implementation; kernels/README.md §1)

- **natural (two-pass baseline)**: two separate loops, one per output (x is streamed twice),
  each output stored in its own array, then one sum loop per output (four passes after the
  fill). Storage `y1(m)` = centre m+2 (0-based), `y2(m)` = centre m+7. mt8: `!$omp parallel
  do` on the fill, both FIR loops, both sums (`reduction(+:)`) and the input sum
  (`reduction(+:)`). An explicit loop per output is the item brief's choice and the literal
  transcription; the array-section form (`y1 = 0.0625*x(1:n-6) + ...`) is equivalent and
  would need `!$omp workshare` in mt8, which ifx declines to thread for some statements
  (warning #5474, stencil/fusedchain notes) — so the loops.
- **expert (fused one pass)**: ONE direct loop over y2's domain producing BOTH outputs from
  the same loads of x(k)..x(k+16), storing each output in its own array and accumulating
  both checksum sums in the same loop; y1's five head and five tail centres are two
  five-cell loops before and after it, so s1 still accumulates in ascending centre order
  (head, fused body, tail). mt8: `!$omp parallel do reduction(+:s1, s2)` on the fused loop
  (fringes serial), the fill and the input sum threaded. Licensed: `simd reduction(+:s1, s2)`
  on the fused loop and `simd reduction(+:ics)` on the input sum (the matched loops below).
- **Reviewed alternative (not this arm)**: a 17-cell register ring (load each x once, rotate)
  — the direct form's re-loads are L1 hits and the fused loop vectorizes; a ring is an
  optional variant for the Fortran reviewer, not a guaranteed win (review C/13).
- **Selection record**: baseline = the two-loop transcription; expert = the fused direct
  loop (W1-5, 2026-10-20; not yet seen by the Fortran reviewer).

## Blade idiom

```blade sketch
type I = Idx<2000003>   // SIZE n
let y1 = method_for(halo<I, [-3, -2, -1, 0, 1, 2, 3]>) <@> lambda(w) where omp ->
    0.0625 * x(w(-3)) + 0.125 * x(w(-2)) + 0.1875 * x(w(-1)) + 0.25 * x(w(0))
    + 0.1875 * x(w(1)) + 0.125 * x(w(2)) + 0.0625 * x(w(3)) |> compute
let y2 = method_for(halo<I, [-8, -4, 0, 4, 8]>) <@> lambda(w) where omp ->
    0.5 * x(w(0)) - 0.25 * x(w(-4)) - 0.25 * x(w(4)) + 0.125 * x(w(-8)) + 0.125 * x(w(8)) |> compute
let s1 = reduce(method_for(y1) <@> lambda(v) -> v * v, lambda(p, q) where omp -> p + q, 0.0)
let s2 = reduce(method_for(y2) <@> lambda(v) -> v * v, lambda(p, q) where omp -> p + q, 0.0)
```

The checksum folds are saxpy's committed spelling (a licensed fold over a map of the stored
output, each bound to its own `let`, seeded with `0.0` — the same +0 chain `prodsum(y1, y1)`
runs, with a licence slot). The input checksum is `reduce(x, lambda(p, q) where omp -> p +
q, 0.0, axes = 1)` in its own function: a plain `reduce(x, (+))` in the result tuple opens
no team, and the same licensed fold WITHOUT `axes = 1` is declined as "reduce in expression
position" (the compiler's own suggestion is `axes = rank`).

`blade plan` (C++ lane, this tree 66e04875, smoke build of the harness check):

- serial-licensed: both FIR maps one plain loop each (no thread pragma: `BLADE_OMP_THREADS=1`
  declines the `[omp v1]` records with the census line); `[fold-form v1] __v40/__v47
  applied [fold form: simd K-lanes(8) self-dot ...]` for s1/s2; the input sum `simd
  K-lanes(8) sum`.
- mt8-licensed: `[omp v1] __ret.. applied [BLADE_OMP_PARALLEL_FOR; loop nest level 0]` for
  the generator, `__v27` (y1) and `__v33` (y2) — **two separate threaded loops, i.e. two
  passes over x**; s1, s2 `threaded(Path B chunks, simd K-lanes(8) self-dot per chunk)`; the
  input sum `threaded(Path B chunks, simd K-lanes(8) sum per chunk)`.
- repro (serial and mt8): every fold `serial (repro)`; in mt8 the maps stay threaded and the
  three fold requests print `[omp] requested but emitted serial: BLADE_FP_REPRO=1 ...`.
- Every regime: `[sibling-folds v1] kernel: declined -- reduce(<map over y1>, ..): left for
  the producer epilogue`, then `[cell-epilogue v1] __v40 / __v47: declined -- the producer
  did not take the flat elementwise path (a dispatched, sliced, tiled or segmented nest, or
  a flat-path gate) [... (two passes)]` — the halo walk is not the flat elementwise path,
  so the W1-B cell epilogue does not fold the checksum into the FIR loop either (in mt8
  repro: `the fold keeps its serial ordered chain ... while the producer is threaded`).
  `[pool-reuse v1] kernel: declined` (y1 and y2 are both live).
- The LLVM lane accepts the source (no fallback); it emits the maps serially and reassociates
  the three licensed folds (`[omp] requested but emitted serial: the LLVM lane has no
  thread-level form ...`).

**What this workload measures.** Today Blade emits the two sibling maps as two passes over
x, then two checksum passes over the stored outputs — the natural arm's structure. Fusing
the two maps into one traversal that writes both stored outputs from the same loads (with
y1's fringe centres peeled), and folding each checksum into that traversal, is the round-3
transformation (item B's shared-traversal track, W1-D's sibling folds generalized to maps,
plus a cell epilogue for a halo producer). A future fused form must keep y1 and y2 stored,
keep s1 and s2 separate +0 chains in ascending order under repro, and fire a BL8009/BL8006
failure at the same cell as the two-pass program.

## Regimes

licensed (serial, mt8), repro (serial, mt8). No fastmath: the only folds are the checksum
and input sums, and every cell of y1/y2 is exact, so the vendor's blanket permission has
nothing to change but those sums.

**Matched-permission loops (licensed, expert tier, behind `BENCH_LICENSED`)**: the s1/s2
accumulation (`simd reduction(+:s1, s2)` on the fused loop — two licensed folds in the Blade
source) and the input sum (`simd reduction(+:ics)`). The FIR loops have no reduction. The
natural tier has no directives beyond the mt8 `parallel do`s. Repro: no `simd reduction`
anywhere; the mt8 Fortran arms keep their thread-level `reduction(+:)` (the regime's
recorded asymmetry, README §3).

## Hypothesis

- vs natural (serial, both fp regimes): **EQUIVALENT** — both stream x twice and re-read
  both outputs for the checksum (Blade's licensed folds vectorize; natural's plain sum loops
  are serial chains in repro and in licensed, which may give Blade a detectable licensed
  edge on the two checksum passes).
- vs expert: **LOSS, about 0.70–0.85 today** (traffic model at full size, DRAM-bound since
  x, y1 and y2 each exceed half the 16 MB L3: Blade ≈ fill 32 + FIR passes 2 × (16 read +
  32 write-allocate+write) + checksum passes 32 + input sum 16 ≈ 176 MB against the expert's
  fill 32 + one pass (16 + 64) + input sum 16 ≈ 128 MB). After sibling-map fusion with
  fused checksums: **equivalent**. The ceiling is the traffic ratio, about 25–30%; a
  measured gap well beyond it points at the FIR loop's code, not at the pass count.
- mt8: the same structure; bandwidth-bound.

## Risks

- Store audit (README §1.5): the expert stores y1 and y2 in the same loop that consumes them
  for the checksum; a compiler that drops stores to a never-read array would make the arm
  "checksum-only computation". The `-DBENCH_DUMP` build reads them, the timed build does not
  (as in saxpy and stencil) — the full-size objdump audit decides.
- The incremental (total − twin) estimate is a difference of two medians of similar size;
  its interval may be wide (harness/README.md "Protocol and statistics").
- The fused-Blade form, when it lands, changes the first-failure point only if a halo
  extent check fires; both maps share one BL8009 precondition (x's extent), checked before
  the first map today.
