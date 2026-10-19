# gram — symmetric Gram matrix from one `where comm` declaration

Group A (structure the compiler derives). Directory `bench/kernels/gram/`.

## Object and operation

X is a p x n real matrix (p variables, n samples). The Gram matrix

    G(i, j) = Σ_{s=0}^{n-1} X(i, s) · X(j, s),   0 <= i, j < p

is p x p symmetric. The algorithm is the inner product over the sample axis, one sum per
cell, accumulated in ascending s in `repro` arms (this fixes every cell's rounding, so all
`repro` arms agree bit-for-bit up to the checksum's own summation order). In `licensed`
arms Blade's `prodsum` IS reassociated (4 or 16 lanes, see Regimes), so cells differ at the
ulp level between regimes and the 1e-12 tolerance covers it. Named output: **G**
(materialized; packed or dense is the arm's choice).

## Inputs

| array | shape (row-major as listed) | linear index k | seed | lo | hi |
|---|---|---|---|---|---|
| X | (p, n) — row i = variable, column s = sample | k = i·n + s | 10000019 | 0.0 | 1.0 |

Fortran stores `x(n, p)` (column i = variable i, samples contiguous) and computes
`k = (i-1)*n + (s-1)` from its 1-based loop indices. `(hi − lo) = 1` is a power of two
(kernels/README.md §3 FMA rule).

## Checksum

    checksum       = Σ_{i=0}^{p-1} Σ_{j=0}^{p-1} G(i, j)²        (full logical matrix)
    input_checksum = Σ_{i, s} X(i, s)

A packed arm computes the checksum as `Σ_i G(i,i)² + 2 Σ_{i<j} G(i,j)²`; Blade reads all
p² logical cells through its canonicalizing accessor (kernels/README.md §4.2). All terms are
non-negative.

## Sizes

| variant | p | n | packed cells | output bytes (packed / dense) | X bytes | flops (packed) | est. serial |
|---|---|---|---|---|---|---|---|
| smoke | 61 | 31 | 1,891 | 15 KB / 30 KB | 15 KB | 0.1 MF | < 1 ms |
| full | 1601 | 1001 | 1,282,401 | 10.3 MB / 20.5 MB | 12.8 MB | 2.57 GF | 0.4–1.0 s |
| scale (optional) | 2003 | 1001 | 2,007,006 | 16.1 MB / 32.1 MB | 16.0 MB | 4.0 GF | 0.7–1.6 s |

Working set (full, natural dense): X 12.8 MB + G 20.5 MB = 33 MB. Input generation:
1.6M LCG cells ≈ 5 ms ≈ 1% of the region — **no inputonly twin**. Planning estimate only
(never subtracted, kernels/README.md §3.4): Blade's logical-read checksum ≈ 2.56M canonicalizing
reads ≈ 5–10 ms ≈ 1–2% of the region.

## Storage orientation (Fortran)

Natural: `x(n, p)`, `g(p, p)` dense. Expert: any orientation; packed upper triangle
`g_packed(j*(j-1)/2 + i)` (1-based, i <= j) or a dense array written only on one side.

## Tiers (natural = dense baseline; expert = reviewed implementation; kernels/README.md §1)

- **natural (dense baseline)**: `g = matmul(transpose(x), x)`, then `sum(g**2)`. The
  intrinsic IS the natural spelling and is kept even if it is fast (M1): it is the runtime
  implementation (libgfortran / Intel runtime), disclosed per arm with whether gfortran
  inlined it (`-finline-matmul-limit` default, recorded) and where ifx's `matmul` routed
  (`/Qopt-matmul` default in 2026.1, recorded); `-fno-external-blas` explicit. Every
  logical cell is produced; no symmetry exploitation. mt8: the matmul form has no
  user-placeable pragma — the Fortran agent records whether the runtime threads it (with
  `/Qparallel` absent); if it does not, the cell reads "parallel preparation, serial
  kernel".
- **Selection record**: the baseline was chosen as the literal transcription of
  `G = X Xᵀ` in array syntax; the expert implementation is the one the Fortran agent and
  the Fortran reviewer settled on (`notes/fortran-notes.md`, `notes/review-fortran.md`),
  with the alternatives tried listed there. **`matmul` routing (Fortran agent):** gfortran
  calls libgfortran's SERIAL `matmul` routine at this size (its inline limit is 30, far
  below p = 1601); ifx INLINES `matmul`, and `/Qopt-matmul` changes nothing on 2026.1.
  Both are disclosed in the arm records.
- **expert**: canonical cells only (i <= j). Loop order is free: the dot form with a manual
  jam of several output cells (each cell's sum stays in s order), or the outer-product form
  `do s; do j; do i = 1, j: g(i,j) += x(i,s)*x(j,s)` (vectorizes over i; same per-cell order),
  possibly blocked. Checksum fused into the producing loop is allowed; the stores must
  happen. mt8: `!$omp parallel do schedule(dynamic)` on the outer triangle loop.
- **blas** regime (arms: `blade-cpp` with the auto-routed `dsyrk`, `gfortran-expert` and
  `ifx-expert` calling `dsyrk('U', 'T', ...)` from the same OpenBLAS; `blade-llvm`
  recorded as "no-blas"; the natural arms are EXCLUDED — no library route, their numbers
  are the native regime's). mt8 here is library threading on both sides
  (`OPENBLAS_NUM_THREADS=8`, no user pragmas), labelled "library threads"; serial uses
  `OPENBLAS_NUM_THREADS=1`.

## Blade idiom

```blade sketch
type Px = Idx<1601>     // SIZE p
type Nx = Idx<1001>     // SIZE n
let X = method_for(range<Px>, range<Nx>) <@> lambda(i, s) -> lcg(i * n + s, seed1, 0.0, 1.0) |> compute
let G = method_for(X, X) <@> lambda(x: Array<Float64 like Nx>, y: Array<Float64 like Nx>) where comm(x, y), omp(x: 1) -> prodsum(x, y) |> compute
```

Kernel parameters are typed CONCRETELY (`Array<Float64 like Nx>`, not `T^1`) so the dot
loop's trip count is the compile-time constant Fortran also sees (README §1.2; same plan
and checksum).

`prodsum(x, y)` is DECIDED (blade-A, `notes/llvm-acceptance-A.md`): in the C++ lane the
fixture spelling `reduce(x * y, (+))` materializes `x * y` as a length-n temporary per
output cell (allocated and freed per cell); `prodsum` streams in both lanes and is the
documented contraction intrinsic — equally natural. Checksum: the per-row streaming nest of
kernels/README.md §4.2. Arrays inside a function returning the scalar; only `checksum` and
`input_checksum` at top level.

`blade plan` must show, for G: `[symmetric-storage v1] applied [packed SymSymmetric storage
over 2 levels ...; operands: X, X]`, `[triangular-iteration v1] applied [... speedup x2]`,
`[omp v1] applied [BLADE_OMP_PARALLEL_FOR_DYNAMIC; loop nest level 0]`. The actual emit
(blade-A) is the triangular row loop (`__i1 < p - __i0`) around a PLAIN dot: one cell at a
time, one accumulator, no jam, no outer-product order, no allocation. In the `blas` regime
the plan should show the dsyrk route instead.

## Regimes

**mt8 matched parallelism (D44, 2026-10-18):** in mt8 the Blade source threads the
kernel's level 0 (`omp(x: 1)` on the `comm` kernel, dynamic schedule) AND the input map's
outer level AND the checksum nest (`where omp` licences on each), matching the Fortran
arms, which thread generation, kernel and checksum. Session 2's gram mt8 rows (kernel-only
threading) are superseded and re-run.

licensed (serial, mt8), repro (serial, mt8), fastmath, blas. **`licensed` differs from
`repro` here** (blade-A F15, `notes/llvm-acceptance-A.md` §11): the inner `prodsum` has no
THREADING licence slot but IS a reassociation-licensed fold under `BLADE_FP_REASSOC=1` —
C++ lane: the dot gets `BLADE_OMP_SIMD_REDUCTION(+:__ps)`, `vmulpd` + `vaddpd` into one
4-lane vector accumulator (g++ fuses no FMA there); LLVM lane: `fadd reassoc` chains with
four ymm FMA accumulators (16 lanes), 44 packed FMAs vs 0 in repro. The checksum's `(+)`
folds are licensed the same way. Serial arms are affected too. `blade plan` is IDENTICAL
under both settings (G21) — the regime record cites the emit, not the plan.

**Matched-permission loops (licensed, expert tier, behind `BENCH_LICENSED`): the
input-checksum fold over X only** (one `simd reduction` on the sum over X's cells, grouped
as the Blade source groups it). The kernel gets NONE: the committed expert uses the
register-tiled outer-product order, which keeps each cell's summation order and has no
reduction loop; its output checksum is accumulated in the same nest (README §3
matched-loop table).

## Hypothesis

- vs natural (serial, both fp regimes): **loss or equivalence expected** — the intrinsic `matmul`
  is a blocked runtime kernel that vectorizes over i without reassociating and may reach
  several GF/s on the full square; Blade's halved flop count does not offset a single
  latency-bound chain per cell. A win here would only appear if the compiler inlined
  `matmul` as a naive nest (recorded either way).
- vs expert, `repro` serial: **LOSS, plausibly 3–8x.** Mechanism: Blade emits a plain dot
  per cell with ONE accumulator — a dependent FMA chain of length n = 1001, i.e. ~4 cycles
  per FMA, ~0.25 FMA/cycle — while the expert's outer-product order (or a manual jam of
  4–8 cells) keeps 4–16 FMAs in flight with no reassociation. Half the flops of natural
  does not begin to compensate. If the measurement confirms it, this is gap G1.
- vs expert, `licensed` serial (HEADLINE): **equivalence to a modest loss** was the
  hypothesis; the pilot measured a load-bound deficit (blade-A §13, G1): gram LLVM 8.4 G
  FMA/s, C++ 4.4, experts 16.3–18.2 — Blade's per-cell dot reloads both operand rows per
  cell (2 loads per FMA) while the expert's 8x6 register tile does 0.67 loads per FMA; the
  C++ lane adds the single-accumulator simd reduction (G26). Pre-pilot mechanism statement:
  the licensed dot runs 4 lanes (C++: one vector accumulator, mul + add, no FMA) or 16
  lanes (LLVM: four ymm FMA accumulators) per cell against the expert's i-vectorized
  outer product, which also keeps several vectors in flight and additionally streams
  contiguous stores; Blade still pays the row-by-row re-read of X per output row and the
  logical-read checksum. The two lanes are expected to differ (16 FMA lanes vs 4 non-FMA
  lanes), so both are reported. A number is not defensible before the pilot.
- mt8: both tiers scale on the row loop; Blade's dynamic schedule should match the expert's;
  natural's static schedule on a square is balanced. Ratios as in serial.
- fastmath: the Fortran compilers may vectorize every chain by blanket permission; Blade's
  side is its licensed form — the comparison is labelled unequal.
- blas: parity by construction (same dsyrk).

## Risks

- The per-cell temporary of `reduce(x * y, (+))` in the C++ lane is a confirmed trap
  (kernels/README.md §4.8); `prodsum` avoids it. Any other elementwise op on the row parameters would
  reintroduce it.
- The LLVM lane accepted a `method_for(bm, bm) ... prodsum` shape in a probe emit; blade-A
  confirms at smoke size that no `llvm lane refused` line appears.
- Checksum: G entries ~ n/4 ≈ 250, squares ~ 6e4, sum ~ 1.6e11 — well inside double range;
  relative drift from summation order ~ 1e-15.
