# antisym — antisymmetric angular-momentum tensor, strict-triangle storage

Group A. Directory `bench/kernels/antisym/`.

## Object and operation

X and Y are p x n (p particles, n components). The antisymmetric tensor

    L(i, j) = Σ_{s=0}^{n-1} ( X(i, s) · Y(j, s) − X(j, s) · Y(i, s) ),   0 <= i, j < p

satisfies L(j, i) = −L(i, j), L(i, i) = 0. Per cell: two inner products over s (each in
ascending s), subtracted once at the end: `L = dot(X_i, Y_j) − dot(X_j, Y_i)`. (Equivalent
to (X Yᵀ − Y Xᵀ)(i,j).) Named output: **L** (strict upper triangle, C(p, 2) cells, or the
dense p x p).

## Inputs

| array | shape | k | seed | lo | hi |
|---|---|---|---|---|---|
| X | (p, n) | k = i·n + s | 10000019 | 0.0 | 1.0 |
| Y | (p, n) | k = i·n + s | 20000038 | 0.0 | 1.0 |

Blade stacks them as one array Z of shape (p, 2, n) with Z(i, 0, s) = X(i, s) and
Z(i, 1, s) = Y(i, s) (the kernel needs both rows of one particle per operand position and
symmetric storage is derived only for the SAME array in both positions, README §7.1); the
mathematical arrays, their linear indices and seeds are unchanged. Fortran: `x(n, p)`,
`y(n, p)`.

## Checksum

    checksum       = Σ_{i,j} L(i, j)²         (full logical matrix; = 2 Σ_{i<j} L(i,j)²)
    input_checksum = Σ X + Σ Y

## Sizes

| variant | p | n | strict cells | packed / dense bytes | inputs | flops (strict / natural) | est. serial |
|---|---|---|---|---|---|---|---|
| smoke | 23 | 11 | 253 | 2 KB / 4 KB | 4 KB | 11 KF | < 1 ms |
| full | 1201 | 401 | 720,600 | 5.8 MB / 11.5 MB | 7.7 MB | 1.16 GF / 2.3 GF | 0.3–0.6 s |

Working set (natural): 11.5 + 7.7 = 19 MB. Inputs: 963k LCG cells ≈ 3 ms ≈ 1% — **no
inputonly twin**. Planning estimate only (never subtracted): Blade's logical-read checksum
≈ 1.44M sign-tracked canonicalizing reads ≈ 3–6 ms ≈ 1–2%.

## Storage orientation (Fortran)

Natural: `l(p, p)` dense, both inner products per cell. Expert: strict upper triangle
packed (`(j-1)*(j-2)/2 + i`, i < j) or one side of a dense array; may compute each
cell's two dot products jammed over several j.

## Tiers (natural = dense baseline; expert = reviewed implementation; README §2)

- **natural (dense baseline)**: full p x p nest, each cell its two `dot_product`s (or `l =
  matmul(transpose(x), y) - matmul(transpose(y), x)`, runtime routes disclosed), dense;
  `sum(l**2)`. mt8: `!$omp parallel do` on the outer loop. Labelled honestly (M1): an upper
  triangle with mirrored negated assignment is also natural dense Fortran; this arm
  measures the fully independent-cell transcription, and the expert arm is the principal
  comparison.
- **Selection record**: baseline = literal transcription of the definition cell by cell;
  expert = the Fortran agent's reviewed strict-triangle implementation
  (`notes/fortran-notes.md`, `notes/review-fortran.md`). Noted (Fortran review F9): `l =
  matmul(transpose(x), y) - matmul(transpose(y), x)` — two intrinsic matmuls and a
  subtraction — is an equally natural dense spelling; it was NOT measured, and the
  baseline's figure speaks only for the explicit cell-by-cell transcription.
- **expert**: `j > i` only, packed, jammed dot products, checksum with weight 2 fused. mt8:
  `!$omp parallel do schedule(dynamic)` on the outer loop.

## Blade idiom

```blade sketch
type Px = Idx<1201>    // SIZE p
type Nx = Idx<401>     // SIZE n
type Cx = Idx<2>
let Z = method_for(range<Px>, range<Cx>, range<Nx>) <@> lambda(i, c, s) -> ... |> compute   // X for c = 0, Y for c = 1
let L = method_for(Z, Z) <@> lambda(a: Array<Float64 like Cx, Nx>, b: Array<Float64 like Cx, Nx>)
        where anticomm(a, b), omp(a: 1)
        -> prodsum(a(0), b(1)) - prodsum(b(0), a(1)) |> compute
```

Probed (2026-10-17): the `T^2` form of this shape typechecks and emits `packed
SymAntisymmetric storage`, a strict triangle (`__i1 < p - __i0 - 1`), and
`BLADE_OMP_PARALLEL_FOR_DYNAMIC`. Literal row reads on `T^k` parameters carry a BL8006
bound check per read, and the cast `(0 : Cx)` does NOT remove it (blade-A, F13);
**concretely typed parameters** (`Array<Float64 like Cx, Nx>`) are checked at the type and
read without the guard — that is what the kernel uses. Checksum: the per-row nest of README
§7.2 reading `L(i, j) * L(i, j)` cell by cell (sign-tracked for i > j) — never `L * L` as an
array expression (README §7.9: the elementwise self-product of an antisymmetric array is
stored antisymmetric and mirrored reads come back negated, a silent wrong value).

`blade plan`: `[symmetric-storage v1] applied [packed SymAntisymmetric ...; operands: Z,
Z]`, `[triangular-iteration v1] applied`, `[omp v1] ... DYNAMIC`.

## Regimes

licensed (serial, mt8), repro (serial, mt8), fastmath. **`licensed` differs from `repro`**
(blade-A F15): both `prodsum`s are reassociation-licensed folds under the knob — C++ lane
`BLADE_OMP_SIMD_REDUCTION`, LLVM lane `fadd reassoc` with four ymm FMA accumulators and the
TWO dots fused into one loop, 42 packed FMAs vs 0 in repro; the checksum's `(+)` folds
likewise. `blade plan` differs only by the loss of the jam line (G21).

**Matched-permission loops (licensed, expert, behind `BENCH_LICENSED`): the
input-checksum folds over X and over Y only** (two sums, combined in the source's order).
The kernel gets NONE: the committed expert's register-tiled outer-product order keeps each
cell's two summations in order and has no reduction loop; the output checksum is
accumulated in the same nest (README §3 table).

## Hypothesis

- vs natural (dense baseline): **WIN ~2x** (half the cells, no diagonal, same per-cell work).
- vs expert, `licensed`: **equivalence to a modest loss** was the hypothesis; the pilot
  measured the same load-bound deficit as gram (blade-A §13, G1): antisym LLVM 7.7 G FMA/s,
  C++ 5.0, experts 16.7–18.6 — per-cell operand reloads (2 loads per FMA) against the
  expert's 8x6 register tile (0.67); both Blade lanes reported.
- vs expert, `repro`: **equivalent (±10%) to a loss** (same cells; the single-chain dot is the same
  mechanism as gram's — if gram loses to an outer-product expert, antisym's expert can use
  the same ordering (`do s; do j; do i < j`) and antisym becomes a loss of the same size;
  the Fortran agent decides the expert ordering per kernel).

## Risks

- Per-read bound checks in the inner loop if the parameters are left as `T^2`; concretely
  typed parameters are the fix, not a knob (F13).
- `L * L` as an array expression would be silently wrong (G16); the checksum reads cells.
- The T^2-row kernel is less common in the corpus than T^1 rows; LLVM-lane acceptance was
  confirmed by a probe emit (no refusal line), blade-A re-confirms at smoke size.
- Anti-invariance deduction: the body is syntactically `f(a,b) − f(b,a)`; BL4013 (claim
  contradicted by a witness) would fire if the checker disagreed — it did not in the probe.
