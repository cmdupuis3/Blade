# pairaction — one-channel row actions of a stored symmetric Matérn-5/2 matrix

Group A. Directory `bench/kernels/pairaction/`. Round 3, wave 3 (W3-4, D-P2; gap G73,
`bench/notes/gap-register.md`). The executable contract Codex review D finding 8 asked for:
named, stored outputs read back element by element, the exact chain order, strict twins.

## Object and operation

pairkernel's n points (x_i, y_i, z_i) and its Matérn-5/2 matrix, per cell in this operation
order (pairkernel/spec.md):

    r²(i, j) = (x_i − x_j)² + (y_i − y_j)² + (z_i − z_j)²       (three squared differences, left to right)
    a(i, j)  = sqrt(5 · r²(i, j)) / 0.7
    W(i, j)  = (1 + a + a²/3) · exp(−a)                          (W(i, i) = 1 exactly)

and R = 3 right-hand sides v_1, v_2, v_3 (n each). The outputs, each an ORDERED chain:

    z(i)   = fold(+, 0.0, [ W(i, j)          for j = 1 .. n ])    -- ascending j, from 0.0
    u_r(i) = fold(+, 0.0, [ W(i, j) · v_r(j) for j = 1 .. n ])    -- ascending j, from 0.0
    y_r(i) = u_r(i) / z(i)

`fold(+, s, [t_1, .., t_n]) = (..((s + t_1) + t_2) .. + t_n)`. In the repro regimes every arm
computes exactly these chains (the products `W(i, j) · v_r(j)` rounded as written; contraction as
the regime's flags allow); the licensed regimes may reassociate each chain (the Blade source's
builtin `(+)` fold is reorder-licensed).

**Named outputs (all STORED):** `pts` (P(i, d), n × 3), `v0`, `v1`, `v2` (the RHS, n each), `w`
(W, packed SymIdx<2, n>: C(n+1, 2) cells; dense n² in the natural tier), `z`, `u0`, `u1`, `u2`,
`y0`, `y1`, `y2` (n each).

## Inputs

| array | shape | k | seed (j) | lo | hi |
|---|---|---|---|---|---|
| x | (n) | k = i | 10000019 (1) | 0.0 | 1.0 |
| y | (n) | k = i | 20000038 (2) | 0.0 | 1.0 |
| z | (n) | k = i | 30000057 (3) | 0.0 | 1.0 |
| v_1 | (n) | k = i | 40000076 (4) | 0.0 | 1.0 |
| v_2 | (n) | k = i | 50000095 (5) | 0.0 | 1.0 |
| v_3 | (n) | k = i | 60000114 (6) | 0.0 | 1.0 |

Blade holds the points as P(i, d) (pairkernel's layout) and the RHS as `v0`, `v1`, `v2`.

## Checksum (every stored element of W, z, u_r, y_r read back)

    checksum       = Σ_{i,j} W(i, j)  (the full logical matrix)
                   + Σ z + Σ u_1 + Σ u_2 + Σ u_3 + Σ y_1 + Σ y_2 + Σ y_3     (left to right)
    input_checksum = Σ_i (x_i + y_i + z_i) + Σ v_1 + Σ v_2 + Σ v_3          (left to right)

Each Σ is a licensed fold (any association; tolerance 1e-12 relative). The pins
(`bench/validation/pins-A.json`) come from `bench/validation/ref_pairaction.py`: an ORDERED oracle
(each z / u_r chain is numpy's sequential `add.accumulate` along the row, seed first) with exact
sums for the checksum terms. The inputs are dyadic, so input_checksum is exact in every arm.

## Sizes

| variant | n | packed cells | packed / dense bytes | sqrt+exp (packed / natural) |
|---|---|---|---|---|
| smoke | 53 | 1,431 | 11 KB / 22 KB | 1.4k / 2.8k |
| full | 2503 | 3,133,756 | 25.1 MB / 50.1 MB | 3.1M / 6.3M |

n = 2503 is not a power of two. Inputs 6n cells (negligible): the input-only twin exists so the
harness can report the net figure (`inputonly.blade`, `-DBENCH_INPUTONLY`, checksum = 0.0).

## Tiers

- **natural (dense baseline)**: W dense, every cell evaluated (pairkernel natural's producer,
  n² sqrt+exp); then the straightforward per-row loops, one pass over the dense matrix per output
  (z, u_1, u_2, u_3), each row an ordered scalar chain over j (row i read as the contiguous column
  i: W is symmetric cell for cell, since W(j, i) evaluates the same squared differences);
  y = u / z. mt8: `!$omp parallel do` on the producer's columns and on the rows (OUTPUT-OWNED:
  each row's chain on one thread -- strict).
- **expert**: W packed by columns (pairkernel expert's producer: SoA coordinates, the spec's
  operation order, the diagonal stored as the exact 1.0). SERIAL: ONE fused column sweep
  updates all four channels (the dspmv-style symmetric packed action): column j, rows i < j
  ascending, w = W(i, j) gives row i its term at partner j (`z(i) += w`, `u_r(i) += w · v_r(j)`)
  and row j its term at partner i (`zj += w`, `u_rj += w · v_r(i)`), then the diagonal. This IS
  strict: row j has received nothing before column j (column k touches rows ≤ k only), takes
  partners 1 .. j ascending inside column j, then partners j+1 .. n from the later columns in
  ascending order -- every chain's spec order from 0.0. Under `BENCH_LICENSED` the four column
  chains carry `!$omp simd reduction` (the source's `(+)` licence: the matched permission).
  **mt8: OUTPUT-OWNED**: each thread owns rows i (`schedule(dynamic)`) and gathers row i's
  canonical cells in ascending j (j < i contiguous from packed column i, j ≥ i strided across
  the columns) into its own four chains. The fused sweep has no strict thread form: its row
  updates are shared across columns, and a shared-update canonical loop with atomics or with
  private partial merges is NOT strict (it changes each chain's association) and is not a fair
  repro twin -- so it is not offered. Producer mt8: pairkernel expert's (`schedule(dynamic)`
  over columns, largest first).
- **Fairness (what each arm fuses)**: the expert serial twin reads W ONCE for all four channels
  (one fused sweep); Blade v1 is SINGLE-CHANNEL and reads W four times (one row action per
  output; the multi-channel joint pass is v1.1, review D finding 6). Every arm evaluates the
  same per-cell products in the same per-chain order (the repro regimes are bitwise the same
  chains up to libm and contraction; verified at n = 53 with `-ffp-contract=off` /
  `BLADE_FP_CONTRACT=off`: the expert serial and mt8 forms and the natural serial and mt8
  forms print z / u_r / y_r bit-identical to Blade's row action, which is bit-identical to
  Blade's row walk -- Float64, REPRO, contract off, in the C++ and the LLVM lane). Selection
  record: baseline = literal transcription; expert =
  this file's reviewed packed forms (the Codex kernel review judges the twins before timing).

## Blade idiom

```blade sketch
let w = method_for(pts, pts) <@> lambda(a: Array<Float64 like Dim>, b: Array<Float64 like Dim>) where comm(a, b), omp(a: 1) -> { .. matern52(..) } |> compute
let z = method_for(range<Pts>) <@> lambda(i) where omp(i: 1) -> reduce(method_for(range<Pts>) <@> lambda(j) -> w(i, j), (+), 0.0) |> compute
let u0 = method_for(range<Pts>) <@> lambda(i) where omp(i: 1) -> reduce(method_for(range<Pts>) <@> lambda(j) -> w(i, j) * v0(j), (+), 0.0) |> compute
let y0 = method_for(zip(u0, z)) <@> lambda(a, b) where omp(a: 1) -> a / b |> compute
```

`blade plan`, serial regimes (`BLADE_OMP_THREADS=1`): `[fold-form v1] z: applied [fold form:
orbit action (rank 2, extent 2503, 1 channel, row groups of 4): own / partner role terms; every
row chain fed its source order from its seed; ...]` for each of z, u0, u1, u2 (the five-party
matrix in the record), with the census line `[omp] requested but emitted serial:
BLADE_OMP_THREADS=1` naming the map's request; the licensed regime records that the strict form
was taken under the licence. mt8 -- a LANE SPLIT: the C++ lane, with threads enabled and the
map's request present, declines (`declined -- orbit action not taken: a thread request on the
map: this build emits threads and v1's action is serial -- the row walk keeps the request`) and
the old emitter runs the row walk on the team, each row's chain on one thread (output-owned, as
the Fortran mt8 twins); the LLVM lane (`blade-llvm`) has no thread runtime and takes the SERIAL
action whenever its own admission succeeds (its record says it has no thread runtime), otherwise
its own fallback -- so `blade-llvm` mt8 runs the serial action, not a threaded row walk. Types:
the C++ lane admits Float64 / Float32 / Int64 / Int32, the LLVM lane Float64 / Int64 (this
kernel is Float64 throughout). Multi-channel is NOT RECOGNIZED: a tuple kernel or seed returns
`ActionNotAction`, with no refusal record. W's read-back: the orbit-class fold (licensed) or the
ordered orbit scatter (repro).

## Regimes

serial-licensed, mt8-licensed, serial-repro, mt8-repro (native); fastmath not run.

## Hypothesis

- vs natural: WIN ~2x (half the transcendentals; the producer dominates).
- vs expert: parity on the producer; the actions are a small share (directional, one core,
  REPRO, a loaded box: one row action over W ≈ 1.8 ms vs the row walk ≈ 11.4 ms at n = 2503);
  the expert's one fused sweep reads W once where Blade v1 reads it four times.

## Risks

- libm differences (pairkernel's): ifx links Intel's libm; checksum disagreements at 1e-15 are
  inside tolerance.
- The producer (3.1M sqrt+exp) dominates every arm's total; the action's gain is visible in the
  directional per-action numbers (G73), not necessarily in the kernel total.
