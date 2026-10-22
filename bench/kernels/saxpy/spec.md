# saxpy — y2 = αx + y (bandwidth control)

Control. Directory `bench/kernels/saxpy/`.

## Object and operation

x, y ∈ [0, 1)ⁿ, α = 1.5. `y2(k) = 1.5·x(k) + y(k)` (one multiply-add per element; FMA
contraction is the regime's business and identical across the arms by the flag table).
Named output: **y2**, materialized (three streams: read x, read y, write y2). The checksum
fold `Σ y2²` is a builtin `(+)` fold in the Blade source and therefore reorder-licensed
(both lanes honour it by default since the flip): under `BENCH_LICENSED`
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

**An `inputonly` twin per arm is REQUIRED** (kernels/README.md §3.3; the total is the result, the
incremental estimate secondary); R >= 21.

## Tiers (natural = dense baseline; expert = reviewed implementation; kernels/README.md §1)

- **natural (array-assignment baseline)**: `y2 = 1.5d0*x + y`, `checksum = sum(y2**2)`.
  mt8: `!$omp workshare` / `parallel do` + `reduction`. Fair counterparts (M1), subject to
  the store audit: `sum(y2**2)` could let a compiler stream without retaining `y2`.
- **expert**: one loop doing the store and the accumulation. mt8: `!$omp parallel do
  reduction(+:acc)`.
- **Selection record**: baseline = the array assignment; expert = the reviewed single loop.

**Stream control.** `saxpy/expert.f90`, gfortran, serial x repro, built at n = 700,001
(16.8 MB, three streams), is the suite's second drift control (harness/README.md "Protocol and statistics"): it tracks
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

**EQUIVALENT** in both fp regimes (bandwidth: 48 MB streamed + 16 MB read back). In
`licensed` every arm vectorizes the checksum fold (since the flip the C++ lane too — a
checksum over a computed map gets `SIMD_REDUCTION` by default); the three streams dominate
regardless. The independent explanation is bandwidth; a deviation either way is
investigated.

## Risks

- A compiler eliding the `y2` stores (in any arm) is the one way to "win" here; the store
  audit of the optimized machine code (README §1.5, M10) is the gate, and an arm that fails
  it is reported as "checksum-only computation".

## Round-4 PM49 measurement contract (B01, 2026-10-22)

`PM49-triad-materialized-v1` reuses this existing kernel as the round's **one** bandwidth
control. Swapping the sketch's operand names gives `y + alpha*x`, the identical triad.
No new control family, algorithm, source tuning or checksum-only replacement is added.
The committed Blade, natural/expert Fortran and input-only sources and independent pins
remain unchanged by B01. `kernel.json.memory_contract` carries the reporting metadata.

Required values are distinct, immutable `x` and `y` with the existing `Ix` provenance,
the named stored `y2`, and BOTH existing checksum scalars. Every `y2` cell must be assigned
and available through its checksum consumption. Original `x/y` are then consumed by the
input checksum. The natural tier uses an array assignment plus read-back; the expert
may fuse checksum arithmetic with the mandatory output stores. The output store audit is
required in **both** tiers, both Blade lanes and every measured regime; source assignments
or passing checksums alone cannot establish it.

For Float64 and extent `n`, analytical input payload is `16n` bytes, mandatory output
payload `8n` bytes. The triad operation's logical traffic is `16n` bytes read and `8n`
bytes written. These are operation/payload models, **not** live capacity or DRAM traffic.
The complete timed task also includes allocation/first touch, input generation, both
input sums, the output checksum, and implementation-dependent passes. A separate checksum
read-back reads `8n` output bytes; a fused checksum can avoid that read while keeping the
stores. No `24n / complete_time` DRAM-bandwidth claim is permitted. The input-only twin
requires the inputs and `checksum=0`; its process peaks are reported beside the total,
never subtracted to invent workspace.

| size | n | input payload bytes | output payload bytes | triad logical read/write bytes |
|---|---:|---:|---:|---:|
| smoke | 20,011 | 320,176 | 160,088 | 320,176 / 160,088 |
| control | 700,001 | 11,200,016 | 5,600,008 | 11,200,016 / 5,600,008 |
| full | 2,000,003 | 32,000,048 | 16,000,024 | 32,000,048 / 16,000,024 |

All extents are non-powers of two. These existing points characterize this control;
no asymptotic, cache-to-DRAM sweep or bandwidth saturation claim follows from them.

The timer starts before allocations and stops after both scalar checksums. Source intent:
Blade's function-local output is released at kernel return; outer input arrays at block
exit, before the stop line. Fortran program arrays remain through the stop line and are
explicitly deallocated after reporting (and after `BENCH_DUMP` when enabled). Retained
allocator capacity may persist after logical release. The process counters cover startup
through exit, including cleanup; they cannot locate a release or prove memory retention.

**Current route/lifetime status: unverified on base master `361504b6`.** The four known
inline cleanup defects are owned by A01
([existing brief](../../notes/round3/briefs/wave2-builder-briefs.md#w2-4-round4-lifetime)).
In particular LLVM saxpy's lifetime-matched attribution remains withdrawn. B01 makes no
memory optimization, lifetime-parity, store-audit or speedup claim. A01 plus fresh plan,
emission, optimized machine-code store and cleanup evidence must establish those facts
on the tested source identity before interpreting a comparison.

The existing independent reference is `bench/validation/ref_saxpy.py`, with smoke, control
and full pins in `pins-B.json`; its smoke result includes every `y2` cell. B01 adds
`bench/validation/dump/saxpy_dump.blade` for full smoke output validation. Existing natural
and expert `BENCH_DUMP` variants write every cell to `saxpy_dump.txt`. The diagnostic Blade
fixture deliberately retains its output for printing; its lifetime differs from the timed
program, so its time and process peaks must never stand in for benchmark measurements.

The runner must validate the smoke dump cell by cell against the independent reference,
validate both scalar pins in total and twin runs, and record actual selected/refused
routes and skips. B01's harness report records process resident and commit distributions;
live allocation/capacity, retention, DRAM traffic and allocation churn stay **unmeasured**.
