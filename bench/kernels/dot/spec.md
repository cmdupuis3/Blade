# dot — inner product (parity control)

Control. Directory `bench/kernels/dot/`.

## Object and operation

a, b ∈ [0, 1)ⁿ. `checksum = Σ_k a(k)·b(k)`, accumulated in ascending k in `repro` arms (one
chain). In `licensed` the Blade source's fold licence (`lambda(p, q) where omp -> p + q`)
permits a lane-split accumulation, and the expert Fortran gets the matched `!$omp simd
reduction(+:s)` on its dot loop; mt8 adds the thread-level `reduction`. Output: the scalar.

## Inputs

| array | shape | k | seed | lo | hi |
|---|---|---|---|---|---|
| a | (n) | k | 10000019 | 0.0 | 1.0 |
| b | (n) | k | 20000038 | 0.0 | 1.0 |

## Checksum

    checksum       = Σ a·b          (non-negative terms)
    input_checksum = Σ a + Σ b

## Sizes

| variant | n | bytes | est. serial kernel | est. input generation |
|---|---|---|---|---|
| smoke | 30011 | 480 KB | < 0.1 ms | < 0.1 ms |
| full | 3,000,017 | 48 MB | 3–6 ms (latency-bound chain ≈ bandwidth) | 15–20 ms |

**Input generation dominates: an `inputonly` twin per arm is REQUIRED** (README §5.3). The
total timed region is the result; the incremental total-minus-twin estimate is reported
beside it with its interval and is NOT kernel time (M9). R >= 21.

## Tiers (natural = dense baseline; expert = reviewed implementation; README §2)

- **natural (intrinsic baseline)**: `checksum = dot_product(a, b)`. mt8: `!$omp parallel do
  reduction(+:s)` over an explicit loop (or `workshare`).
- **expert**: an explicit loop with one accumulator in `repro`; in `licensed` the same loop
  under `!$omp simd reduction(+:s)` (behind `BENCH_LICENSED`, matching the Blade fold
  licence), and the two input-checksum folds (a, b — one each, combined in the source's
  order) likewise. mt8: `!$omp parallel do reduction(+:s)` (`simd` added in licensed).
- **Selection record**: baseline = the intrinsic; expert = the reviewed explicit loop.

## Blade idiom

```blade sketch
let dot = reduce(method_for(zip(a, b)) <@> lambda(x, y) -> x * y, lambda(p, q) where omp -> p + q, 0.0)
```

bound to its own `let` (blade-B, `notes/llvm-acceptance-B.md`): this is the spelling that
streams AND threads in the C++ lane. `prodsum(a, b)` streams but has no licence slot;
`reduce(a * b, lambda ... where omp -> ..., 0.0)` threads but MATERIALIZES `a * b`; `omp` on
the zip map feeding `(+)` emits serial. The LLVM lane has no OpenMP (the licence compiles
serial there); the serial build must show no `simd reduction` (README §3).

## Regimes

licensed (serial, mt8), repro (serial, mt8), fastmath.

## Hypothesis

- `repro` serial: **EQUIVALENT** — one FMA chain in every language (≈ 4 cycles per
  element, which at n = 3M is ≈ 3 ms, about the DRAM time for 48 MB) — note the chain, not
  bandwidth, may be the binding constraint (M20).
- `licensed` serial: **blade-llvm EQUIVALENT to expert** (both reorder: `reassoc nsz` lanes
  vs `simd reduction`); **blade-cpp LOSS 3–5x** — the C++ lane's knob does not reach a
  fold over a deferred computation (blade-B F11, G20), so it keeps the serial chain against
  a vectorized expert. Natural (`dot_product`, no directive) stays a serial chain and is
  expected to lose to expert in licensed as well.
- mt8: thread reductions in all; same lane split.
- The independent explanation is the serial chain; a deviation either way is a codegen
  finding and is investigated.

## Risks

- The incremental estimate is a difference of two ~20 ms medians; its interval will be
  wide and its ratio may be `undefined` (README §6). That is the honest state of a
  bandwidth control under in-region input generation (D9); the total is the result.
- Store audit: the twin and the total must both keep `a` and `b` materialized (M8/M10);
  the owners inspect the optimized generation loops.
