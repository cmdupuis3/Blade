# saxpy — y2 = αx + y (bandwidth control)

Control. Directory `bench/kernels/saxpy/`.

## Object and operation

x, y ∈ [0, 1)ⁿ, α = 1.5. `y2(k) = 1.5·x(k) + y(k)` (one multiply-add per element; FMA
contraction is the regime's business and identical across the arms by the flag table).
Named output: **y2**, materialized (three streams: read x, read y, write y2). The checksum
fold `Σ y2²` is a builtin `(+)` fold in the Blade source and therefore reorder-licensed at
the source (even where the C++ lane does not yet honour it, F11): under `BENCH_LICENSED`
the expert's checksum accumulation loop takes `!$omp simd reduction(+:acc)`, as do the two
input-checksum folds (x, y — one each, combined in the source's order) — the matched loops
for this kernel (README §3 table). The saxpy store loop itself has no reduction.

## Inputs

| array | shape | k | seed | lo | hi |
|---|---|---|---|---|---|
| x | (n) | k | 10000019 | 0.0 | 1.0 |
| y | (n) | k | 20000038 | 0.0 | 1.0 |

## Checksum

    checksum       = Σ y2²
    input_checksum = Σ x + Σ y

## Sizes

| variant | n | bytes (x, y, y2) | est. serial kernel | est. input generation |
|---|---|---|---|---|
| smoke | 20011 | 480 KB | < 0.1 ms | < 0.1 ms |
| full | 2,000,003 | 48 MB | 4–8 ms (+ checksum read 16 MB) | 10–14 ms |

**An `inputonly` twin per arm is REQUIRED** (README §5.3; the total is the result, the
incremental estimate secondary); R >= 21.

## Tiers (natural = dense baseline; expert = reviewed implementation; README §2)

- **natural (array-assignment baseline)**: `y2 = 1.5d0*x + y`, `checksum = sum(y2**2)`.
  mt8: `!$omp workshare` / `parallel do` + `reduction`. Fair counterparts (M1), subject to
  the store audit: `sum(y2**2)` could let a compiler stream without retaining `y2`.
- **expert**: one loop doing the store and the accumulation. mt8: `!$omp parallel do
  reduction(+:acc)`.
- **Selection record**: baseline = the array assignment; expert = the reviewed single loop.

**Stream control.** `saxpy/expert.f90`, gfortran, serial x repro, built at n = 700,001
(16.8 MB, three streams), is the suite's second drift control (README §6): it tracks
memory-subsystem contention and background load, where the compute control (`recur`)
tracks core clock.

## Blade idiom

```blade sketch
let y2 = (1.5 * x + y) |> compute          // forced: the named output must be stored whatever the fuse pass would do
let checksum = reduce(y2 * y2, (+))
```

(blade-B reports lifted lets are materialized per let anyway; an architect probe in a
function body saw an unforced `y2` fused into the checksum — either way the `|> compute`
makes the store unconditional and the emit check settles it.)

If `|> compute` on a lifted expression is refused in some position, the equivalent licensed
map `method_for(zip(x, y)) <@> lambda(u, v) where omp(u: 1) -> 1.5 * u + v |> compute` is
the same rung for a two-operand co-iteration and carries the mt8 licence; blade-B records
which spelling was needed and why. The emit must show y2 allocated and written (one loop)
and the checksum as a second loop (or fused with the store — both are allowed; the store
must happen).

## Regimes

licensed (serial, mt8), repro (serial, mt8).

## Hypothesis

**EQUIVALENT** in `repro` (bandwidth: 48 MB streamed + 16 MB read back). In `licensed`, if
the checksum fold is licensed, blade-llvm and expert vectorize it while blade-cpp keeps a
serial chain over 16 MB (F11) — a detectable loss for blade-cpp at most, since the three
streams dominate. The independent explanation is bandwidth; a deviation either way is
investigated.

## Risks

- A compiler eliding the `y2` stores (in any arm) is the one way to "win" here; the store
  audit of the optimized machine code (README §1.5, M10) is the gate, and an arm that fails
  it is reported as "checksum-only computation".
