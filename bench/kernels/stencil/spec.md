# stencil — 2-D five-point Laplacian through `halo` windows

Group B (parity control). Directory `bench/kernels/stencil/`.

## Object and operation

u is an ny x nx grid (row i, column j). On the interior 1 <= i <= ny−2, 1 <= j <= nx−2:

    lap(i, j) = u(i−1, j) + u(i+1, j) + u(i, j−1) + u(i, j+1) − 4·u(i, j)

evaluated left to right as written. Named output: **lap**, (ny−2) x (nx−2), materialized.

## Inputs

| array | shape | k | seed | lo | hi |
|---|---|---|---|---|---|
| u | (ny, nx) | k = i·nx + j | 10000019 | 0.0 | 1.0 |

Fortran: `u(nx, ny)` (j contiguous) with `k = (i-1)*nx + (j-1)`; or `u(ny, nx)` with i
contiguous — the agent picks the orientation whose inner loop is contiguous and records it
(the mathematical grid is the same).

## Checksum

    checksum       = Σ_{interior} lap(i, j)²
    input_checksum = Σ u

## Sizes

| variant | ny | nx | u bytes | lap bytes | est. serial |
|---|---|---|---|---|---|
| smoke | 31 | 41 | 10 KB | 9 KB | < 1 ms |
| full | 1501 | 2003 | 24.1 MB | 24.0 MB | 8–20 ms |

Memory-bound (48 MB streamed; ~6 flops per cell). Input share ≈ 10 ms of ~20 ms ≈ **40%
→ an `inputonly` twin per arm REQUIRED** (README §5.3). R >= 21. No multi-sweep variant:
repeated application would need the full trajectory stored (`let rec` keeps every step) or
a different algorithm (design-decisions D15).

## Tiers (natural = dense baseline; expert = reviewed implementation; README §2)

- **natural (array-section baseline)**: one array-section statement `lap = u(1:ny-2,
  2:nx-1) + u(3:ny, 2:nx-1) + u(2:ny-1, 1:nx-2) + u(2:ny-1, 3:nx) - 4*u(2:ny-1, 2:nx-1)`
  (in its orientation), then `sum(lap**2)`. mt8: `!$omp workshare` (or a `parallel do` over
  rows). Array sections and an explicit contiguous nest are fair counterparts (M1); the
  array expression is NOT assumed to create a temporary — the Fortran agent records what
  the compiler did.
- **Selection record**: baseline = the array-section transcription; expert = the Fortran
  agent's reviewed explicit nest with the fused checksum.
- **expert**: explicit nest, inner loop contiguous, checksum accumulated in the same nest
  (stores to lap still happen). mt8: `!$omp parallel do reduction(+:acc)` on the row loop.

## Blade idiom

```blade sketch
type Y = Idx<1501>     // SIZE ny
type X = Idx<2003>     // SIZE nx
let lap = method_for(range<halo<Y, [-1, 0, 1]>, halo<X, [-1, 0, 1]>>) <@> lambda(wr, wc) where omp(wr: 1)
          -> u(wr(-1), wc(0)) + u(wr(1), wc(0)) + u(wr(0), wc(-1)) + u(wr(0), wc(1)) - 4.0 * u(wr(0), wc(0)) |> compute
let checksum = reduce(method_for(lap) <@> lambda(row) -> prodsum(row, row), lambda(p, q) where omp -> p + q, 0.0)
```

(corpus loops/076, the separable halo form; the checksum is the per-row `prodsum` under a
licensed fold — blade-B's committed spelling.) Probed: typechecks; the result type is
`Array<Float64 like Idx<ny-2>, Idx<nx-2>>`; the emit's only guards are two one-time
`BL8009 halo extent mismatch` checks outside the nest, no per-cell edge branches; `blade
plan` shows `[omp v1] applied [BLADE_OMP_PARALLEL_FOR; loop nest level 0]` for inputs and
`lap` and a `Path B` omp decision for the checksum fold. **Finding (blade-B, gap register
G10):** the earlier spelling `reduce(reduce(lap * lap, (+)), (+))` — and `reduce(lap * lap,
(+), axes = 2)` — MATERIALIZES a second (ny−2) x (nx−2) array for `lap * lap` (72 MB
working set at full size, over the cap), and a `where omp` fold kernel on the `axes = 2`
form is dropped silently. The per-row `prodsum` streams and threads. The row width is
derived as `Int64(extents(range<X>))`, never a second literal (README §4).

## Regimes

licensed (serial, mt8), repro (serial, mt8). The Blade checksum fold is licensed (the
per-row `prodsum` under `lambda(p, q) where omp -> p + q`), so under `BENCH_LICENSED` the
expert's checksum accumulation takes `!$omp simd reduction(+:acc)`, and so does the
input-checksum fold over u (per row where the Blade source sums rows, combined in the
source's order) — the matched loops for this kernel (README §3 table); the stencil nest
itself has no reduction and gets nothing. Expect blade-cpp's checksum to stay a serial chain over the row sums
only (the row `prodsum` is an inline, non-deferred fold — F11 does not apply to it; blade-B
reports it gets `BLADE_OMP_SIMD_REDUCTION` under the knob).

## Hypothesis

**EQUIVALENT across all tiers** (bandwidth-bound; all three write 24 MB and read 24 MB).
The independent explanation is bandwidth; a win or loss either way is investigated (a
Blade win would point at the natural arm's array-section lowering, a loss at the halo
nest or the checksum).

## Risks

- The materialized square (above) is avoided by the committed spelling; if the store
  audit (README §1.5) shows any arm's compiler eliminated the `lap` stores, that arm is
  reported as "checksum-only computation".
- The LLVM lane accepted the halo shape in a probe emit; confirm at smoke size.
