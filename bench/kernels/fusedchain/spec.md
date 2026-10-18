# fusedchain — a three-stage elementwise chain consumed by one reduction

Group B (language-level structure). Directory `bench/kernels/fusedchain/`.

## Object and operation

a ∈ ℝⁿ. Three elementwise stages and one fold:

    s1(k) = 1.5 · a(k) + 0.25
    s2(k) = s1(k) / (1 + |s1(k)|)
    s3(k) = s2(k)² − 0.5 · s2(k)
    checksum = Σ_k s3(k)²

Each stage is evaluated as written (same operation order in every arm). Output: the
**scalar** only. s1, s2, s3 are unnamed intermediates — fusing them away is exactly the
freedom under test (README §1.5).

## Inputs

| array | shape | k | seed | lo | hi |
|---|---|---|---|---|---|
| a | (n) | k | 10000019 | 0.0 | 1.0 |

## Checksum

    checksum       = Σ s3²          (s3 is sign-mixed, its square is not)
    input_checksum = Σ a

## Sizes

| variant | n | a bytes | natural temporaries | flops | est. serial |
|---|---|---|---|---|---|
| smoke | 20011 | 160 KB | 480 KB | 0.3 MF | < 1 ms |
| full | 2,000,003 | 16 MB | 48 MB (three n-arrays) | ~30 MF (+ 2M divisions) | 10–25 ms |

Working set: 64 MB in the natural arm (the cap). Memory-bound. Input share ≈ 6–8 ms of
~20 ms ≈ **35% → an `inputonly` twin per arm REQUIRED** (README §5.3); the incremental
total-minus-twin estimate is reported beside the total, never as kernel time. R >= 21
rounds (memory-bound, README §6).

## Tiers (natural = dense baseline; expert = reviewed implementation; README §2)

- **natural (staged baseline)**: `s1 = 1.5d0*a + 0.25d0`, `s2 = s1/(1+abs(s1))`, `s3 =
  s2*s2 - 0.5d0*s2`, `checksum = sum(s3**2)` — three named allocatable arrays, four passes.
  mt8: `!$omp workshare` around the array statements (or `parallel do` per statement) and
  `reduction(+:)` on the sum. Named honestly (M1): a single array expression or a fused
  loop is also natural when only a scalar is requested; this arm measures the staged
  spelling with named intermediates, i.e. what explicit fusion buys.
- **Selection record**: baseline = the spec's stages as array statements; expert = the
  Fortran agent's reviewed single loop.
- **expert**: one loop, scalar stages, one accumulator in `repro` (fold order k
  ascending); in `licensed` the same loop under `!$omp simd reduction(+:acc)` (behind
  `BENCH_LICENSED`, matching the Blade source's fold licence on the reduce kernel) and the
  input-checksum fold over `a` likewise. mt8: `!$omp parallel do reduction(+:acc)`.
  Recorded (Fortran review F7): ifx fuses input generation + kernel + input sum into one
  loop in this arm while retaining the stores — permitted compiler fusion (README §1.5);
  the arm record says so.

## Blade idiom

```blade sketch
let stage1 = lambda(v) -> 1.5 * v + 0.25
let stage2 = lambda(v) -> v / (1.0 + abs(v))
let stage3 = lambda(v) -> v * v - 0.5 * v
let chain = object_for(stage1) >>@ object_for(stage2) >>@ object_for(stage3)
let checksum = reduce(chain <@> a, lambda(p, q) where omp -> p + q, 0.0)   // square inside the fold operand or a last stage
```

The composed-chain spelling (examples/03 (a)) — blade-B established that lifted lets
(`let s1 = 1.5 * a + 0.25; ...`) are materialized one temporary per `let` and never fused
across lets, while an `object_for >>@` chain (now accepted inside functions in both lanes),
a single block kernel, or an inline fold operand fuses into the fold with no temporaries
(`notes/llvm-acceptance-B.md`; the exact final spelling is blade-B's). `blade plan` / the
emit must show one loop and no n-sized temporary. mt8: the fold licence on the reduce kernel
(README §7.4), C++ lane only.

## Regimes

licensed (serial, mt8), repro (serial, mt8), fastmath.

## Hypothesis

- vs natural: **WIN 1.5–2.5x** in both fp regimes — natural moves 16 MB x (1 read + 3
  writes + 3 reads + 1 read) ≈ 128 MB through DRAM; the fused loop moves 16 MB. (ifx may
  fuse adjacent array statements itself; if it does, the ifx-natural ratio shrinks and the
  Fortran agent records the optimization report.)
- vs expert, `repro`: **equivalent** (same single loop; the division dominates per element).
- vs expert, `licensed`: **blade-llvm equivalent** (both reorder the accumulation);
  **blade-cpp detectable loss to loss** — the C++ lane's knob does not reach this deferred
  fold (F11, G20), but the division keeps the serial chain from being the only cost, so the
  gap is smaller than dot's.
- fastmath: unequal permission sets (README §3) — GNU/Intel fast-math may replace the
  division by a reciprocal multiply, Blade's licence does not; reported under the fastmath
  label, both Blade lanes.

## Risks

- If the composed chain is not fused end to end (`blade plan`/emit), the stages materialize
  and Blade matches natural, not expert — record, do not force. The lifted-let spelling,
  which the style guide ranks higher, is known to materialize per let: that is itself a gap
  (register G8).
- Input share is large: the total is the primary estimand; the incremental estimate is
  secondary evidence with its own interval (README §5.3).
