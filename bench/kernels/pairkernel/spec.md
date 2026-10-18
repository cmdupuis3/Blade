# pairkernel — Matérn-5/2 kernel matrix of n points in 3-D, symmetric packed

Group A. Directory `bench/kernels/pairkernel/`.

## Object and operation

n points with coordinates (x_i, y_i, z_i). The kernel matrix

    r²(i, j) = (x_i − x_j)² + (y_i − y_j)² + (z_i − z_j)²        (evaluated as written: three squared differences, summed left to right)
    a(i, j)  = sqrt(5 · r²(i, j)) / ℓ,     ℓ = 0.7
    K(i, j)  = (1 + a + a²/3) · exp(−a)

is n x n symmetric with K(i, i) = 1. Every arm evaluates exactly this formula per cell, in
this operation order (`sqrt(5.0 * r2) / 0.7`, `(1.0 + a + a * a / 3.0) * exp(-a)`), so all
arms agree to the last bit per cell. Named output: **K** (packed C(n+1, 2) or dense n²).

This is the charter's A4 recast as a materialized matrix: the pair SUM over a `comm`
kernel is a fold over compact storage, which Blade refuses (BL3999; design-decisions D2).

## Inputs

| array | shape | k | seed | lo | hi |
|---|---|---|---|---|---|
| x | (n) | k = i | 10000019 | 0.0 | 1.0 |
| y | (n) | k = i | 20000038 | 0.0 | 1.0 |
| z | (n) | k = i | 30000057 | 0.0 | 1.0 |

Blade holds them as one array P of shape (n, 3), P(i, d) = the d-th coordinate of point i
(same linear index per coordinate array; the kernel needs one row per operand position).
Fortran: three vectors `x(n), y(n), z(n)` or `p(3, n)` — the agent's choice.

## Checksum

    checksum       = Σ_{i,j} K(i, j)²        (full logical matrix; = n + 2 Σ_{i<j} K²)
    input_checksum = Σ x + Σ y + Σ z

## Sizes

| variant | n | packed cells | packed / dense bytes | transcendental evaluations (packed / natural) | est. serial |
|---|---|---|---|---|---|
| smoke | 53 | 1,431 | 11 KB / 22 KB | 1.4k / 2.8k | < 1 ms |
| full | 2503 | 3,133,756 | 25.1 MB / 50.1 MB | 3.1M / 6.3M sqrt+exp | 0.1–0.2 s / 0.2–0.4 s |

Working set (natural dense): 50 MB. Inputs: 7.5k cells, negligible — **no inputonly
twin**. Planning estimate only, never subtracted: Blade's logical-read checksum ≈ 6.3M
canonicalizing reads ≈ 10–20 ms, perhaps 10% of the packed time. n is capped by the
natural arm's dense 64 MB, not by the packed form.

## Tiers (natural = dense baseline; expert = reviewed implementation; README §2)

- **natural (dense baseline)**: full n x n double nest, every cell evaluated (no mirror
  copy), dense; `sum(k**2)`. mt8: `!$omp parallel do` on the outer loop. Labelled honestly
  (M1): triangular evaluation plus mirror copies is common for a symmetric kernel matrix;
  this arm measures the fully independent-cell transcription (twice the transcendental
  work), and the expert arm is the principal comparison.
- **Selection record**: baseline = literal transcription per cell; expert = the Fortran
  agent's reviewed packed-triangle implementation (`notes/fortran-notes.md`,
  `notes/review-fortran.md`). Vector-math routes (scalar `exp` vs SVML) are archived per
  arm (M23).
- **expert**: `j >= i` triangle, packed storage, checksum fused with weight 2 (diagonal is
  exactly 1 and may be written without evaluating the kernel — allowed, it is the spec's own
  value; record it). mt8: `!$omp parallel do schedule(dynamic)` on the outer loop.

## Blade idiom

```blade sketch
type Nx = Idx<2503>    // SIZE n
type Dx = Idx<3>
function matern52(r2: Float64) -> Float64 = {
    let a = sqrt(5.0 * r2) / 0.7
    (1.0 + a + a * a / 3.0) * exp(-a)
}
let K = method_for(P, P) <@> lambda(a: Array<Float64 like Dx>, b: Array<Float64 like Dx>) where comm(a, b), omp(a: 1) ->
        matern52((a(0) - b(0)) * (a(0) - b(0))
               + (a(1) - b(1)) * (a(1) - b(1))
               + (a(2) - b(2)) * (a(2) - b(2))) |> compute
```

Probed: the `T^1` form typechecks with `packed SymSymmetric storage`, triangle `__i1 < n -
__i0`, dynamic omp. Literal reads on `T^k` parameters carry a BL8006 check per read and the
`(d : Dx)` cast does not remove it (blade-A, F13); concretely typed parameters
(`Array<Float64 like Dx>`) read without the guard — that is the kernel's spelling. The
commutativity claim is checked against the body (BL4013 would refuse a non-symmetric body;
the squared differences are symmetric). Row differences `a - b` as an array expression
(`prodsum(a - b, a - b)`) allocate a 3-vector per cell in the C++ lane (README §7.8) — the
scalar reads above are the spelling. Checksum: per-row nest of README §7.2.

`blade plan`: `[symmetric-storage v1] applied [packed SymSymmetric ...; operands: P, P]`,
`[triangular-iteration v1] applied [... speedup x2]`, `[omp v1] ... DYNAMIC`.

## Regimes

licensed (serial, mt8), repro (serial, mt8); `fastmath` not run. The kernel itself has no
fold, but **`licensed` still differs from `repro`** through the checksum: the per-row `(+)`
folds over the logical cells are reassociation-licensed under the knob (simd-reduction
loops in the C++ lane, `reassoc` in the LLVM lane; blade-A F15), and `blade plan` is
identical under both settings (G21).

**Matched-permission loops (licensed, expert, behind `BENCH_LICENSED`):** `!$omp simd
reduction(+:acc)` on the checksum accumulation loop over cells, and on the three
input-checksum folds (x, y, z — one each, combined in the source's order). Its Blade
counterparts are builtin `(+)` folds, reorder-licensed at the source even where the C++
lane does not yet honour it (README §3 matched-loop table). The kernel loop has no
reduction and gets nothing.

## Hypothesis

- vs natural (dense baseline): **WIN ~2x** (half the sqrt+exp evaluations;
  transcendental-bound).
- vs expert: **equivalent** (same evaluations; language overhead hidden by the ~40 ns per
  cell of libm work — unless the vector-math routes differ: SVML in ifx vs scalar libm in
  the others would show up here first, which is why the routes are archived).
- mt8: dynamic schedule vs the expert's dynamic — parity; natural's static schedule on a
  square — parity with its own serial ratio.

## Risks

- libm differences: g++/clang (ucrt64 / clang64) and gfortran call the same MSYS2 libm;
  ifx links Intel's libm (`exp`, `sqrt` may differ in the last bit under `/fp:precise`).
  A checksum disagreement at the 1e-15 level is expected and inside tolerance;
  larger means a formula-order difference — check the operation order first.
- The logical-read checksum is the largest in the suite relative to the per-cell work; it
  is a consequence of the missing canonical fold (G2/G3) and is part of the total, never
  subtracted.
