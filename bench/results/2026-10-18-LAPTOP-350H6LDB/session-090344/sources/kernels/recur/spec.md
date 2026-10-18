# recur — RK4 time-stepping of a nonlinear ODE as a recursive array (parity control, drift control)

Group B. Directory `bench/kernels/recur/`.

## Object and operation

nx independent scalar ODEs du/dt = u − u³, integrated with classical RK4, step h = 0.01,
from u0 for nt−1 steps. With f(u) = u − u·u·u (evaluated as `u - u * u * u`):

    k1 = f(u)
    k2 = f(u + (h/2)·k1)
    k3 = f(u + (h/2)·k2)
    k4 = f(u + h·k3)
    u' = u + (h/6)·(k1 + 2·k2 + 2·k3 + k4)

with the constants spelled `0.005 * k1`, `0.005 * k2`, `0.01 * k3`, `(0.01 / 6.0) * (...)`
and the bracket summed left to right, identically in every arm. Named output: the
**trajectory U(t, x)**, t = 0..nt−1, U(0, ·) = u0 — stored in full in every arm (the
recursive array stores every step; an expert that keeps only the current state would be
computing a different output; the expert may fuse the per-step checksum).

## Inputs

| array | shape | k | seed | lo | hi |
|---|---|---|---|---|---|
| u0 | (nx) | k = x | 10000019 | 0.0 | 2.0 |

## Checksum

    checksum       = Σ_{t,x} U(t, x)²      (u ∈ (0, 2) stays bounded: the flow contracts to u = 1)
    input_checksum = Σ u0

## Sizes

| variant | nt | nx | trajectory bytes | cell-steps | flops | est. serial |
|---|---|---|---|---|---|---|
| smoke | 11 | 211 | 18 KB | 2.1k | 0.1 MF | < 1 ms |
| full | 301 | 20011 | 48.2 MB | 6.0M | ~180 MF | 60–150 ms |

Per step the state is 160 KB (L2-resident); the stage arrays (natural, Blade) are 4–6
more of the same; the region as a whole allocates and writes the 48 MB trajectory. Inputs:
20k cells — **no inputonly twin**.

## Storage orientation (Fortran)

`u(nx, nt)` (x contiguous within a step). Blade: `Array<Float like T, X>` (x contiguous).

## Tiers (natural = dense baseline; expert = reviewed implementation; README §2)

- **natural (array-statement baseline)**: inside `do t = 2, nt`: `k1 = f(u(:,t-1))`, `k2 =
  ...`, ..., `u(:,t) = ...` with k1..k4 (and the stage inputs) as whole-array expressions —
  the same temporaries Blade's step kernel has. Checksum `sum(u**2)` afterwards. mt8:
  `!$omp workshare` around the stage statements inside the time loop.
- **expert**: `do t; do x` with the four stages as scalars, no stage arrays, the store to
  `u(x,t)` and the checksum accumulation in the same loop. mt8: `!$omp parallel do` over x
  inside the time loop (each trajectory's arithmetic order preserved; `reduction(+:)` on
  the checksum).
- **mt8 applies to the Fortran arms (M5).** The x-trajectories are independent and
  ordinary Fortran threads them; Blade's `let rec` step has no licence slot for the
  non-recursion axis (gap register G5), so the Blade cells read **"no parallel form"**.
- **Selection record**: baseline = RK4 as array statements per stage; expert = the Fortran
  agent's reviewed scalar-stage loop.

## Blade idiom

```blade sketch
type T = Idx<301>        // SIZE nt
type X = Idx<20011>      // SIZE nx
function rhs(u: Array<Float like X>) -> Array<Float like X> = u - u * u * u
function step(u: Array<Float like X>) -> Array<Float like X> = {
    let k1 = rhs(u)
    let k2 = rhs(u + 0.005 * k1)
    let k3 = rhs(u + 0.005 * k2)
    let k4 = rhs(u + 0.01 * k3)
    u + (0.01 / 6.0) * (k1 + 2.0 * k2 + 2.0 * k3 + k4)
}
let rec traj: Array<Float like T, X> =
    match traj with
    | zero -> zero
    | zero :: s -> zero :: u0
    | prefix :: n -> prefix :: step(prefix(n - 1))
```

Checksum: `reduce(method_for(traj) <@> lambda(row) -> prodsum(row, row), (+))` — the
per-row `prodsum` (blade-B); `reduce(reduce(traj * traj, (+)), (+))` would materialize a
second 48 MB trajectory (over the cap; G10).

(corpus recursive-arrays/007, the RK4 litmus.) Probed inside a function: typechecks; the
emit shows per step about eight row-sized allocate/free pairs (one per stage expression)
with fused `__fp_cells` loops, then a copy into the trajectory row, and a zero-fill pass
over the trajectory before the recursion — i.e. the natural tier's temporaries plus the
zero-fill. The LLVM lane accepted the shape (no `IRForRange` refusal).

## Regimes

licensed and repro, serial (all arms) and mt8 (Fortran arms; Blade "no parallel form").
The checksum fold `reduce(..., (+))` over the per-row `prodsum`s is a builtin-body fold and
therefore reassociation-licensed under the knob (the row `prodsum` likewise), so `licensed`
differs from `repro` in the checksum pass only; the RK4 stages carry no fold. Matched
permission (expert, licensed, behind `BENCH_LICENSED`): `!$omp simd reduction(+:acc)` on
the checksum accumulation loop and on the input-checksum fold over u0 — not on the time
loop or the stage arithmetic (README §3 matched-loop table).

## Hypothesis

- vs natural (array-statement baseline): **equivalent** (same stage temporaries;
  L2-resident per step).
- vs expert: **LOSS 1.5–3x** was the pre-pilot hypothesis; the pilot measured **~11x**
  (118 ms vs 10–11 ms) and blade-B's diagnosis (G6) is the ALLOCATOR, not the arithmetic:
  eight 160 KB temporaries freed and re-allocated every step re-take ~38 page faults each
  (~91k faults ≈ 118 ms), while the stage loops are fully vectorized. The hypothesis's
  mechanism (per-step temporaries) was right, its magnitude was not; the committed source
  stays the spec's idiom — this is the language result. Alternative-spelling note, not a
  result: RK4 written as one block kernel measured informally at near-expert speed with an
  identical checksum.

## Drift control

`recur/expert.f90`, gfortran, serial x repro, is the suite's **compute control** (README
§6): compute-bound, deterministic, ~0.1 s; it tracks core clock and thermal state. Its
region also allocates and writes the 48 MB trajectory ("L2-resident" describes a step,
not the region), which is why a second, stream control (`saxpy` at n = 700,001) sits
beside it.

## Risks

- `let rec` inside a function: a known unmerged fix concerns a frame leak of the recursive
  buffer at function exit (memory only, not timing). 48 MB leaked once per process is
  inside the cap. If blade-B sees the process exceed 64 MB RSS at full size, say so.
- Per-step allocations are the measured mechanism; do not hoist them by hand.
