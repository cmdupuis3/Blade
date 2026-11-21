# maskagg — filter (mask + compound) and aggregate (control Blade is not expected to win)

Group B. Directory `bench/kernels/maskagg/`.

## Object and operation

x ∈ [0, 2)ⁿ, threshold t = 1.0. The compacted subsequence

    y = ( x(k) : x(k) > 1.0, k ascending )      (order preserved)

is the named output (materialized, like `pack`), of length m ≈ n/2. Then

    checksum = Σ_j y(j)² + m           (m added as a float)

## Inputs

| array | shape | k | seed | lo | hi |
|---|---|---|---|---|---|
| x | (n) | k | 10000019 | 0.0 | 2.0 |

## Checksum

    checksum       = Σ y² + Float64(m)
    input_checksum = Σ x

Σy² is permutation-invariant, so the compaction ORDER is not verified by this checksum
(design-decisions D17); the oracle's comparison of the dump variant at smoke size (kernels/README.md
§3.4) verifies the order and the count independently — via an order-sensitive fingerprint
(two Horner folds + count + first/last cell) rather than the cells, because Blade cannot
print a runtime-extent array (`Idx<?>`: BL7004 "has no printed form"; gap G22).

## Sizes

| variant | n | x bytes | y bytes (≈ n/2) | est. serial |
|---|---|---|---|---|
| smoke | 30011 | 240 KB | 120 KB | < 1 ms |
| full | 1,000,003 | 8 MB | ~4 MB | 3–10 ms (Blade est. 40 MB peak, see below) |

Full n was cut from 3,000,017 to **1,000,003** (coordinator decision on blade-B's probe):
Blade's `compound` builds a hashed index of ~40 B per kept cell, estimated ~120 MB and
200–300 ms at the larger n — over the 64 MB cap. At 1,000,003 the Blade arm is estimated
at ~40 MB peak; the runner measures the peak working set once per kernel at full size and
the gap is registered (G9). Memory-bound. Input share ≈ 3 ms of ~8 ms (Fortran) ≈ **40% →
an `inputonly` twin per arm REQUIRED** (kernels/README.md §3.3). R >= 21.

## Tiers (natural = dense baseline; expert = reviewed implementation; kernels/README.md §1)

- **natural (`pack` baseline)**: `y = pack(x, x > 1.0d0)` (allocatable `y`), `m = size(y)`,
  `sum(y**2) + m`. `pack` is appropriate because the subsequence is an output (M1); a masked
  sum would implement a different output contract.
- **expert**: one counting pass (or a running counter in one pass with a pre-sized buffer
  of n), a scatter loop, `sum` fused into the scatter. Two passes over x at most.
- **No mt8 arm, by protocol (M5):** the suite restricts this kernel to sequential, stable
  compaction in every language; a parallel prefix-sum pack is a different algorithm under
  README §1.1. Compaction is not inherently sequential, and the table says "serial by
  protocol", not "sequential by construction".
- **Selection record**: baseline = the `pack` intrinsic; expert = the Fortran agent's
  reviewed count-and-scatter.

## Blade idiom

```blade sketch
let x = ...gen... |> compute                 // inside a function
let keep = mask(x, lambda(v) -> v > 1.0)
let y = compound(x, keep)                   // TOP LEVEL ONLY (BL6002 inside a function)
let checksum = reduce(y * y, (+)) + Float64(extents(y))
```

(examples/01 (a)/(f), the WHERE idiom: `mask` builds the presence vector, `compound`
compacts.) **Constraint:** `compound(dense, mask)` is only valid as a top-level `let`
(`src/Lowering.fs:857`), so `x`, `keep` and `y` are top-level bindings in this one kernel.
**The LLVM lane refuses `compound`** (blade-B, `notes/llvm-acceptance-B.md`), so
`blade-cpp` is the only Blade arm of this kernel; the fallback build honours `--print
checksum,input_checksum`, so the top-level arrays are not echoed. Expected emission: a Bool array of n
(mask), a count pass, a gather into y, a fold — three passes over x.

## Regimes

licensed and repro, serial only. **`licensed` differs from `repro`** (Fortran review F2
corrects this spec's earlier claim): the archived licensed Blade emission has 8-lane
accumulators for BOTH the output checksum `Σ y²` and the input sum `Σ x`. Matched
permission (expert, licensed, behind `BENCH_LICENSED`): `!$omp simd reduction(+:acc)` on
the output-checksum sum and on the input-checksum fold over x; the compaction itself stays
sequential in every regime (README §3 matched-loop table).

## Hypothesis

**LOSS, plausibly 2–5x, vs both tiers**: three passes (Bool mask write, count, gather)
plus a hashed index of ~40 B per kept cell against pack's two passes (gfortran's `pack`
counts then copies) and the expert's one or two. Pilot confirmation (blade-B §6, G9): ~85%
of Blade's time is one `std::unordered_map` insert per kept cell (77.8 ns per kept cell,
≈ 39 ms of the 62 ms incremental at full size), plus the `y * y` temporary. The LLVM lane
refuses `mask` on its own as well as `compound` ("IRMask in array position"), so
`blade-cpp` remains the only Blade arm. An unexpected win would be investigated.

## Risks

- If `mask` + `compound` are fused by the optimizer into a single counting gather, the
  hypothesis flips to parity — `blade plan`/emit will show it; report either way.
- `extents(y)` is a runtime extent (`Idx<?>`); fine.
