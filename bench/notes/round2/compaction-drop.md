# B4 maskagg compaction -- DROP experiment (W1-C, 2026-10-20)

Directional only (builder box, other builders' processes alive during run 1). Hand-patched
emitted C++, scratch only; nothing in the compiler changed for this experiment.

## Method

- Source: `bench/kernels/maskagg/maskagg.blade` emitted by the worktree binary built from
  master 4136f0b5 (`blade run --verbose --print checksum,input_checksum`, `BLADE_EXE_CACHE=0`,
  `BLADE_OMP_THREADS=1`), licensed (default) and `BLADE_FP_REPRO=1`. Kept build dirs copied to
  `scratchpad/round2/W1C/drop_{lic,rep}/`.
- Generator `scratchpad/round2/W1C/mkdrop.py` writes 4 variants per mode into
  `W1C/variants/`:
  - `base`: unpatched (six passes: mask, count, position fill, gather, `y*y` pool, fold).
  - `a` (B4a): no `y*y` pool; the fold reads `y.data` directly. Licensed:
    `blade_fold::sq<double,8>(y.data, m)` behind the BL8003 guard; repro: `__r = y[0]*y[0]`, then
    the ascending `__wrap(__r, y[i]*y[i])` chain.
  - `b` (a + B4b): count folded into the mask pass; ONE fused branchless fill+gather pass
    `v = x[i]; y[q] = v; pos[q] = i; q += keep[i]` with m+1 cells in both buffers; an adopting
    `compound_index_t<1>(name, n, m, pos)` constructor (added to the scratch copy of
    index_types.h); fold as in `a`, after the pass.
  - `c` (b + B4c): the fold fed inside the fused pass after every 256-cell block of x, only
    indices < q. Licensed: a hand-rolled 8-named-vector lane state over whole 32-cell blocks +
    `detail::fold`'s exact finish (== `sq<double,8>(y, m)`); repro: peel the first kept square,
    then the ordered chain over kept terms.
  - All variants add a steady_clock kernel timer (mask through checksum, excludes the x
    generator and input_checksum) and print `%a` of checksum / input_checksum.
- Build: `g++ -std=c++17 -O3 -march=native -ffp-contract={fast,off} -fno-math-errno -fwrapv
  -fopenmp` (`W1C/build_drop.ps1`). Run: `W1C/run_drop.ps1`, `OMP_NUM_THREADS=1`, one process at
  a time, all 8 fast-contraction exes interleaved per round.

## Bits (`%a`)

| mode | contraction | base | a | b | c |
|---|---|---|---|---|---|
| repro | off | 0x1.9651090932898p+20 | same | same | same |
| repro | fast | 0x1.9651090932898p+20 | same | same | same |
| licensed | off | 0x1.9651090932942p+20 | 0x1.9651090932944p+20 | ...944 | ...944 |
| licensed | fast | ...942 | ...944 | ...944 | ...944 |

input_checksum `0x1.e7f383cac61c8p+19` in every cell. Repro: raw bits identical to the
unpatched program under both contraction modes. Licensed: a/b/c are mutually identical (all are
`sq<double,8>` over the same y) and differ from OLD's eight-scalar-lane eager fold by 2 ulp
(1.2e-16 relative) -- a legitimate licensed regrouping.

## Timing (kernel segment, ms; median [min, max])

Run 1, 11 rounds (other builders' processes active):

| | base | a | b | c |
|---|---|---|---|---|
| licensed | 5.655 [5.035, 7.510] | 4.868 [3.948, 5.628] | 3.946 [3.339, 5.169] | 4.110 [3.483, 5.202] |
| repro | 6.355 [5.152, 6.855] | 5.366 [4.355, 6.177] | 4.559 [3.897, 6.039] | 4.412 [3.444, 7.514] |

Run 2, 21 rounds (quieter):

| | base | a | b | c |
|---|---|---|---|---|
| licensed | 3.830 [3.352, 6.464] | 3.337 [2.725, 4.725] | 2.997 [2.608, 4.265] | 2.717 [2.061, 7.199] |
| repro | 4.129 [3.798, 4.859] | 3.495 [3.122, 4.093] | 3.153 [2.781, 7.621] | 2.892 [2.620, 3.895] |

Raw rows: `W1C/drop_times_r11.csv`, `W1C/drop_times.csv` (run 2).

Incremental savings (run 1 / run 2):

| step | licensed | repro |
|---|---|---|
| B4a (base -> a) | 0.79 / 0.49 ms | 0.99 / 0.63 ms |
| B4b (a -> b) | 0.92 / 0.34 ms | 0.81 / 0.34 ms |
| B4c (b -> c) | -0.16 / 0.28 ms | 0.15 / 0.26 ms |
| b+c beyond a | 0.76 / 0.62 ms | 0.95 / 0.60 ms |

## Verdict (corrected after the Codex diff review, 2026-10-20)

- B4a survives (0.5-1.0 ms).
- MEASURED arm c (a+b+c, licensed c = the hand-rolled streaming eight-vector state) beyond B4a:
  licensed 0.758 / 0.620 ms, repro 0.954 / 0.603 ms (run 1 / run 2) -- nominally at or above
  the 0.6 ms bar; run 2 is borderline.
- What SHIPS differs per mode:
  - repro ships c (the in-pass ordered chain): shipped-arm saving vs base 1.943 / 1.237 ms;
  - licensed ships b (fused pass, then `sq<double,8>` over y after the pass -- the streaming
    licensed feed needs W1-B's P1 lane state and is a follow-up): shipped-arm saving vs base
    1.709 / 0.833 ms; b beyond a is 0.922 / 0.340 ms, i.e. run 2 does NOT clear the 0.6 ms bar
    for the shipped licensed arm.
- These are hand-patched-C++ directional numbers on a shared box. The runner's isolated,
  interleaved A/B of the actual emitted code decides whether the bar is met; nothing here
  establishes a 1.5-2.4 ms range or a DRAM-byte saving.
