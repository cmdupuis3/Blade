# Ordered orbit scatter -- drop experiment timing (W1-A task 2, 2026-10-20)

Scratch only, no repo code. Source: `round2/A-work/scatter_timing.cpp` (extends `scatter_proto.cpp`).
MEASURED, directional (the runner owns the protocol A/B). Box was NOT quiet: other agents' processes were
live (CPU load 37-38% at the time), which shows as wide walk ranges; medians and ratios are stable across
two passes.

## What is compared

- **rank 2** (pairkernel): SymIdx<2, 2503> pool (3,133,756 cells), allocated through the same
  `allocate<promote<double,2>::type, __v33_symm>` call the emitted program uses, filled with xorshift
  doubles in [-370, 630). f(x) = x*x, leaf `(+)` seed 0.0, outer `(+)` seed 0.0.
  - **W** = `__lambda_79/80/81` + the `__v60` outer loop copied VERBATIM from
    `A-work/pairkernel.repro.serial.cpp` (canon_fold<2> + canon_left_justify<2> + nested row-pointer read
    `kmat[c0][c1]` per logical cell, BLADE_FRAME and the `__wrap_*` lambdas included), against the real
    runtime headers (`src/cpp` of the W1-A worktree).
  - **S** = the G = 4 row-group scatter, B = 64 (brief 2.5; prototype code), one pass over the pool.
- **rank 3** (comoment3): SymIdx<3, 171> pool (848,046 cells), same allocation/fill rule.
  - **W** = `__lambda_107/108/111/112/113` + the `__v88` loop VERBATIM from `comoment3.repro.serial.cpp`
    (canon_fold<3>, `t[c0][c1][c2]`).
  - **S** = the slice form (brief 2.3): per first coordinate i, the rank-2 G = 4 group scatter over the
    slice triangle with chains H[i, .] (H row i, contiguous), and the THIRD event H[j, k] <- z (j > i)
    served from the SAME Z block / the same scalar z before it is overwritten (Codex objection 7) -- the
    pool is read exactly once (848,046 reads); then the retained H (packed SymIdx<2, 171>, 14,706
    chains) walked by the middle and outer folds (29,241 + 171 adds).
- Layout asserted at start-up: every canonical cell's nested-pointer address equals
  `pool + canon_off2/canon_off3_incl(...)`, and the slice offsets equal `canon_off3_incl`.
- Every round compares W and S raw bits (`memcpy` to uint64). The data has no NaN, so this is raw-bit
  equality, not NaN-equivalence.

## Commands

```
g++ -std=c++17 -O3 -march=native -fno-math-errno -fwrapv -ffp-contract=off  -fopenmp -I<wt>/src/cpp -o scatter_timing_off.exe  scatter_timing.cpp
g++ -std=c++17 -O3 -march=native -fno-math-errno -fwrapv -ffp-contract=fast -fopenmp -I<wt>/src/cpp -o scatter_timing_fast.exe scatter_timing.cpp
OMP_NUM_THREADS=1 scatter_timing_off.exe off ; scatter_timing_fast.exe fast      (pass 1)
OMP_NUM_THREADS=1 scatter_timing_fast.exe fast2 ; scatter_timing_off.exe off2    (pass 2, reverse order)
```

21 rounds per process, interleaved (W then S on even rounds, S then W on odd), `std::chrono::steady_clock`,
one process at a time, no `blade test` running. g++ = MSYS2 ucrt64 (16.2).

## Results (ms; median [min, max] of 21)

| rank | contract | pass | W (walk) | S (scatter) | S/W | raw-bit W == S |
|---|---|---|---|---|---|---|
| 2 (n = 2503) | off  | 1 | 17.439 [11.321, 42.107] | 2.170 [1.620, 10.701] | **0.124** | 21/21 |
| 2 (n = 2503) | off  | 2 | 12.053 [11.065, 15.910] | 1.652 [1.518, 6.951]  | **0.137** | 21/21 |
| 2 (n = 2503) | fast | 1 | 16.212 [12.914, 37.669] | 2.110 [1.690, 6.624]  | **0.130** | 21/21 |
| 2 (n = 2503) | fast | 2 | 12.394 [10.305, 23.919] | 1.714 [1.437, 3.353]  | **0.138** | 21/21 |
| 3 (p = 171)  | off  | 1 | 16.332 [9.467, 22.593]  | 1.244 [0.826, 2.163]  | **0.076** | 21/21 |
| 3 (p = 171)  | off  | 2 | 8.494 [7.591, 31.011]   | 0.842 [0.656, 1.192]  | **0.099** | 21/21 |
| 3 (p = 171)  | fast | 1 | 16.981 [10.312, 33.708] | 1.173 [0.861, 1.476]  | **0.069** | 21/21 |
| 3 (p = 171)  | fast | 2 | 9.314 [6.667, 55.018]   | 0.924 [0.596, 2.334]  | **0.099** | 21/21 |

Raw per-round numbers: `A-work/scatter_timing_off.txt`, `scatter_timing_fast.txt` (pass 1; pass 2 kept the
summary lines in `*_off2.txt` / `*_fast2.txt`). Checksums: rank 2 `0x1.24723d281585ap+39`, rank 3
`0x1.d2414a3b1445cp+38`, identical in all four builds/passes.

**Contraction.** Under `-ffp-contract=off` W == S raw-bit in 21/21 rounds at both ranks (this is the claim the
gates define). Under `=fast` W == S was ALSO 21/21 on this data at these shapes -- 0 mismatches -- and the
fast bits equal the off bits. That is an observation for this data and this compile, not a guarantee: the
prototype's own small-n battery (brief 2.4) has 8/176 one-ulp fast-mode mismatches, and GCC's contraction is
context-dependent; v1 must claim identity only under contract=off (Codex objection 8).

## Verdict per rank (Codex objection 10 thresholds, orbit time approximated as zero)

- **Rank 2 (pairkernel): KEEP.** Measured S/W = 0.12-0.14 in both contraction modes, against parity bars of
  0.59 (gfortran) and 0.33 (ifx). Clears both with margin. In absolute terms the in-harness walk is 12-17 ms
  and the scatter 1.7-2.2 ms, i.e. W - S ~ 10-15 ms against lane deficits of 3.72 / 6.11 ms (LLVM serial
  repro pairkernel).
- **Rank 3 (comoment3): KEEP.** Measured S/W = 0.07-0.10 against bars of 0.47 / 0.49. W - S ~ 7.6-15.8 ms
  against deficits of 3.48 / 3.37 ms (LLVM) and 3.72 / 3.61 ms (C++).

## What this does NOT establish

- These are C++ (g++) numbers for a hand-written prototype; the LLVM lane needs new vector emission
  (Codex objection 9) and its speed is not measured here.
- The in-harness walk time is larger than the "repro excess" figures of the brief (9.1 / 6.6 ms, which were
  differences of whole-program medians across sessions); W here is the checksum walk ALONE, on a loaded box.
  The decision uses the ratio in one binary, as objection 10 asks; the runner's harness A/B is still the
  acceptance test.
- One data set per rank (no NaN / -0.0 cases in the timing run; those are covered by the prototype's
  176-case raw-bit battery under contract=off).
- No emitted-code proof that g++ vectorizes the scatter in the real emitter's spelling; only this
  prototype's spelling was compiled.
