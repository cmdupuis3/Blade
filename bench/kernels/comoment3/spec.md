# comoment3 — rank-3 symmetric moment tensor in compact (simplex) storage

Group A. Directory `bench/kernels/comoment3/`.

## Object and operation

X is p x n (p variables, n samples). The raw third-moment tensor

    T(i, j, k) = (1/n) Σ_{s=0}^{n-1} X(i, s) · X(j, s) · X(k, s),   0 <= i, j, k < p

is symmetric under every permutation of (i, j, k). Per cell: accumulate the triple product
`(X(i,s) * X(j,s)) * X(k,s)` over s in ascending order, then DIVIDE BY n once per cell (not
per term, not a multiply by a precomputed 1/n). Two kinds of rounding difference must be
kept apart (Fortran review F1): the dense natural tier evaluates each PERMUTATION of a
canonical cell independently — T(i,j,k), T(j,i,k), ... each with its own rounding, which
is permitted and is what the 1e-12 tolerance absorbs; REGROUPING the canonical cell's
product (e.g. `Xi * (Xj * Xk)`) is NOT permitted in any arm, in any regime (the expert is
fixed to `(Xi*Xj)*Xk`). Named output: **T**
(C(p+2, 3) canonical cells packed, or the dense p³ cube).

## Inputs

| array | shape | k | seed | lo | hi |
|---|---|---|---|---|---|
| X | (p, n) | k = i·n + s | 10000019 | 0.0 | 1.0 |

Fortran: `x(n, p)`, `k = (i-1)*n + (s-1)`.

## Checksum

    checksum       = Σ_{i,j,k=0}^{p-1} T(i, j, k)²          (full logical cube, p³ terms)
    input_checksum = Σ X(i, s)

Packed arms sum canonical cells with multiplicity: 1 for i=j=k, 3 for exactly two equal,
6 for all distinct. Blade reads the p³ logical cells through the canonicalizing accessor
(rank-3 per-row nest: `reduce(method_for(range<Px>) <@> lambda(i) ->
reduce(method_for(range<Px>) <@> lambda(j) -> reduce(method_for(range<Px>) <@> lambda(k) ->
T(i,j,k) * T(i,j,k), (+)), (+)), (+))`, or the two-level form that returns a row — blade-A
checks no p² or p³ allocation appears).

## Sizes

| variant | p | n | packed cells | packed / dense bytes | flops (packed / dense) | est. serial (packed / dense) |
|---|---|---|---|---|---|---|
| smoke | 13 | 11 | 455 | 3.6 KB / 17.6 KB | 15 KF / 72 KF | < 1 ms |
| full | 171 | 201 | 848,046 | 6.8 MB / 40.0 MB | 0.51 GF / 3.0 GF | 0.15–0.4 s / 1–2 s |

Working set (natural dense): 40 MB + X 0.3 MB. Cell ratio 171³ / C(173,3) = 5.89. Inputs:
34k cells, negligible — **no inputonly twin**. Planning estimate only, never subtracted
(kernels/README.md §3.4, M13): Blade's logical-read checksum is 5.0M canonicalizing reads of a rank-3
index (sort three coordinates + simplex offset) ≈ 25–50 ms, perhaps 10–20% of the packed
time. The total is the result; the missing canonical fold (G2/G3) is the explanation if the
diagnosis — from phase evidence, not from this estimate — points there.

## Storage orientation (Fortran)

Natural: `t(p, p, p)` dense; loop nest in column-major order (i innermost), sample loop
inside or outside as the agent judges natural (record it). Expert: packed simplex, offset
of (i <= j <= k) by the standard tetrahedral formula, or any bijection; any loop order.

## Tiers (natural = dense baseline; expert = reviewed implementation; kernels/README.md §1)

- **natural (dense baseline)**: triple nest over (i, j, k) covering all p³ cells, each with
  its own s-loop (or `sum(x(:,i)*x(:,j)*x(:,k))`), dense cube; `sum(t**2)`. mt8: `!$omp
  parallel do` on the outermost loop. Label honestly (M1): exploiting the stated
  permutation symmetry is also ordinary scientific Fortran; this arm measures what the dense
  literal transcription costs, and the expert arm is the principal comparison.
- **Selection record**: baseline = literal transcription of the definition; expert = the
  Fortran agent's reviewed packed-simplex implementation (`notes/fortran-notes.md`,
  `notes/review-fortran.md`).
- **expert**: `i <= j <= k` nest only, hand-packed offsets, jam of several cells or the
  outer-product order over s (`do s; do k; do j <= k; do i <= j`), checksum fused with
  multiplicities. mt8: `!$omp parallel do schedule(dynamic)` on the outer loop.

## Blade idiom

```blade sketch
let T = method_for(X, X, X) <@> lambda(x: Array<Float64 like Nx>, y: Array<Float64 like Nx>, z: Array<Float64 like Nx>)
        where comm(x, y, z), omp(x: 1) -> prodsum(x, y, z) / Float64(n) |> compute
```

(Three-operand `prodsum` is blade-A's accepted spelling, `notes/llvm-acceptance-A.md`
(b); parameters typed concretely so the sample-loop trip count is a compile-time constant,
README §1.2.)

DECIDED (blade-A, `notes/llvm-acceptance-A.md`): the kernel uses the `prodsum`
contraction intrinsic, because `reduce(x * y * z, (+))` materializes a length-n row
temporary per output cell in the C++ lane (allocated and freed per cell — the
per-iteration-temporary trap; O4 answered, gap G15). The exact `prodsum` spelling for three
operands is the one in blade-A's notes.

`blade plan`: `[symmetric-storage v1] applied [packed SymSymmetric storage over 3 levels
...; operands: X, X, X]`, `[triangular-iteration v1] applied [triangular levels 2 ...]`,
`[omp v1] ... DYNAMIC; loop nest level 0`. Emit: a three-level simplex nest with **no
allocation inside it** and one accumulator per cell (the same single-chain dot as gram; the
chain here is n = 201 long, so the latency penalty vs an outer-product expert is smaller
than gram's but present).

## Regimes

**mt8 matched parallelism (D44, 2026-10-18):** in mt8 the Blade source threads the
kernel's level 0 AND the input map's outer level AND the rank-3 checksum nest (`where omp`
licences on each), matching the Fortran arms. Session 2's comoment3 mt8 rows (kernel-only
threading: 87 ms with a 70%-serial checksum pass) are superseded and re-run.

licensed (serial, mt8), repro (serial, mt8), fastmath. **`licensed` differs from `repro`**
(blade-A F15): the three-operand `prodsum` is a reassociation-licensed fold under the knob
— C++ lane `BLADE_OMP_SIMD_REDUCTION(+:__ps)` (one 4-lane accumulator), LLVM lane `fadd
reassoc` with four ymm FMA accumulators, 16 packed FMAs vs 0 in repro; the checksum's `(+)`
folds likewise. `blade plan` is identical under both (G21).

**Matched-permission loops (licensed, expert, behind `BENCH_LICENSED`): the
input-checksum fold over X only.** The kernel gets NONE: the committed expert's
register-tiled outer-product order over s keeps each cell's summation order and has no
reduction loop; the output checksum (with multiplicities) is accumulated in the same nest
(README §3 table).

## Hypothesis

- vs natural (dense baseline): **WIN 4–6x** (5.89x fewer cells; the logical-read checksum
  and any per-cell overhead eat into it).
- vs expert, `repro`: **equivalent to 2x loss** (identical cell count; the logical-read
  checksum costs more than the triangle read; the single accumulator chain of length 201
  per cell is latency-bound against an expert that jams or uses the outer-product order —
  the gram mechanism at a shorter chain).
- vs expert, `licensed`: **equivalence to a modest loss** was the hypothesis; the pilot
  (blade-A §13) attributes the observed loss to ~2.3x on the kernel — a multiply plus an FMA
  per term with 3 loads against the expert's hoisted pair product (one FMA, 0.67 loads;
  G1b) — plus the measured checksum share (36% of the LLVM time, 70% in C++; G3a). Both
  Blade lanes reported; the attribution column (README §5) carries the split, the total
  stays the result.
- mt8: the dynamic schedule on the outer simplex level should scale like the expert's.

## Risks

- O4 settled: `reduce(x * y * z, (+))` allocates per cell in the C++ lane; `prodsum` is the
  spelling.
- Rank-3 canonicalizing reads in the checksum may be slower than estimated; measure the
  checksum nest alone at smoke size if the full-size share is in doubt (blade-A, not timed
  as a result).
- LLVM lane: the rank-3 compact map is the shape of `tests/fixtures/llvm/bench_sym3_compact`
  (known to pass); the per-row checksum nest is new to the lane — smoke-probe it.
