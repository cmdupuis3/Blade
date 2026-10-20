# W1-B STEP 0 -- cell-epilogue drop experiments (hand-patched emitted C++; directional)

Builder W1-B, 2026-10-20. Scratch: `scratchpad\round2\W1B-drop\` (`gen.py` = the patches,
`build.py`, `run.py`, `asm.py`, `var\*.cpp`, `exe\*.exe`, `t_*.json` raw per-round times).

## Method

- Emitted with the worktree binary (== master 4136f0b5 compiler; only the P1 header was new),
  `BLADE_EXE_CACHE=0 BLADE_OMP_THREADS=1`, `blade emit`:
  recur / stencil / saxpy under `BLADE_FP_REPRO=1`, and recur / stencil / saxpy licensed
  (the licensed builds are the gap reference; saxpy licensed is also the B1-licensed base).
- `gen.py` instruments `kernel()` (steady_clock around the whole kernel body -> `KT` ms;
  `KC %a` = the result's raw bits; `KY` = a bit hash of EVERY producer cell, y2 / lap / traj --
  printed outside the timed region) and writes the variants:
  - `SR<B>` store/reload: producer strip-mined into B-cell mini-blocks, the SAME chain statement
    (`__ps += r[k] * r[k]` / `__v36 = wrap(__v36, w * w)`) advanced over the just-stored block.
  - `RC<B>` register-consume: `const double c = <producer>; out[k] = c; acc += c * c` inside
    the strip-mined cell loop; `RC0` = the same with NO strip-mining (one fused loop).
  - `saxpy_lic_P1E<E>`: the y2 producer in E-cell blocks, each followed by
    `blade_fold::feed_sq<double, 8>(state, y2 + blk, E / 32)`, then `finish_sq` on the
    remainder (E in 128 / 256 / 1024; the P1 header is the real one, src/cpp/blade_fold_lanes.hpp).
  - B in {8, 16, 24, 32, 64} for both forms (saxpy SR also 128 / 256).
- g++ 16.2 ucrt64 `-std=c++17 -O3 -march=native -fno-math-errno -fwrapv -fopenmp`, each variant
  under `-ffp-contract=off` AND `=fast`.
- Timing: `run.py`, one process at a time, OMP_NUM_THREADS=1, 1 warm-up, 21 interleaved rounds
  (rotated order), median / min / max of the kernel time. NOT pinned: the box was shared with
  other builders (dotnet / codex / claude processes visible), an 11-round CPU-2-pinned first pass
  was too noisy (maxes 2-3x) and is kept only as `t_recur.json`.

## Raw bits

EVERY variant (all 80+ executables) printed the SAME `KC` (result bits) AND the same `KY`
(producer bit hash) as its unpatched base, under BOTH `-ffp-contract=off` and `=fast`:
recur repro `0x1.4f6d56b38d543p+22`, stencil repro `0x1.310e0edbe2a08p+22`, saxpy repro
`0x1.bf2becab5820ep+21`, saxpy licensed (P1) `0x1.bf2becab583ep+21` == OLD `sq<double, 8>`.

## Results (kernel ms, median [min-max], 21 rounds)

Gap = repro base - licensed base (same mode). Recovered = repro base - variant.

### recur (t_recur3.json: the clean 21-round subset run; t_recur2.json has all variants, noisier)

| variant | fast | off |
|---|---|---|
| licensed base | 9.363 [8.63-12.51] | 10.989 [10.34-12.86] |
| repro base | 13.251 [12.66-16.66] | 14.844 [14.53-19.40] |
| gap | 3.89 | 3.86 |
| RC0 | 10.271 [9.79-12.46] -> **2.98 recovered (77%)** | 12.702 [12.47-15.56] -> **2.14 (56%)** |
| RC16 | 10.580 -> 2.67 (69%) | 12.864 -> 1.98 (51%) |
| RC32 | 10.341 -> 2.91 (75%) | 12.770 -> 2.07 (54%) |
| SR24 | 11.229 -> 2.02 (52%) | 13.564 -> 1.28 (33%) |
| SR32 | 11.345 -> 1.91 (49%) | 13.553 -> 1.29 (33%) |
| all-variant run (t_recur2): RC8/16/24/32/64 fast | 11.61 / 11.97 / 11.78 / 12.09 / 12.06 vs base 15.13, lic 10.99 | 14.14 / 14.38 / 14.46 / 14.38 / 14.61 vs base 17.14, lic 12.71 |
| SR8/16/24/32/64 fast | 15.47 / 14.44 / 13.21 / 13.61 / 13.88 | 17.89 / 16.37 / 15.61 / 15.86 / 15.62 |

### stencil (t_stencil.json)

| variant | fast | off |
|---|---|---|
| licensed base | 4.595 [4.29-6.74] | 4.639 [4.35-5.44] |
| repro base | 6.571 [6.26-7.38] | 6.683 [6.25-8.07] |
| gap | 1.98 | 2.04 |
| RC0 | 4.958 -> **1.61 (82%)** | 5.176 -> 1.51 (74%) |
| RC8 / 16 / 24 / 32 / 64 | 5.051 / 5.018 / 5.046 / 5.027 / 5.074 (1.50-1.55) | 5.008 / 5.195 / 5.065 / 4.957 / 5.079 (1.49-1.73) |
| SR8 / 16 / 24 / 32 / 64 | 5.869 / 5.878 / 5.510 / 4.991 / 5.367 | 6.031 / 5.781 / 5.740 / 5.128 / 5.524 |

### saxpy (t_saxpy.json)

| variant | fast | off |
|---|---|---|
| licensed base (`sq<double, 8>` reload) | 4.332 [3.92-6.06] | 4.158 [3.98-5.95] |
| **B1 licensed P1, E = 256** | 3.682 [3.30-5.20] -> **-0.65 ms** | 3.754 [3.32-4.40] -> **-0.40 ms** |
| B1 licensed P1, E = 128 / 1024 | 3.585 (-0.75) / 3.805 (-0.53) | 3.549 (-0.61) / 3.927 (-0.23) |
| repro base (chain after the producer) | 5.057 [4.69-7.14] | 5.323 [4.63-7.52] |
| **B1 repro RC0** | 3.506 -> **-1.55 ms** | 3.572 -> **-1.75 ms** |
| B1 repro RC8 / 16 / 24 / 32 / 64 | 3.363 / 3.361 / 3.436 / 3.393 / 3.375 (-1.62..-1.70) | 3.372 / 3.461 / 3.428 / 3.407 / 3.389 (-1.86..-1.95) |
| B1 repro SR8 / 16 / 24 / 32 / 64 / 128 / 256 | 4.198 / 3.913 / 3.905 / 3.390 / 3.725 / 3.592 / 3.913 | 4.154 / 3.950 / 3.823 / 3.584 / 3.655 / 3.720 / 3.886 |

## Vectorization / assembly (`-fopt-info-vec`, `asm.py` census of kernel())

- g++ 16.2 VECTORIZES the register-consume loop: the producer runs in 32-byte vectors and the
  ordered chain is a fold-left in-order reduction (lane extracts `vunpckhpd` / `vextractf128`
  then ascending `vaddsd`), bit-identical to the scalar chain (KC/KY above). This holds for
  recur's RK4 body (`loop vectorized using 32 byte vectors and unroll factor 4`, recur RC0 /
  RC16 cell loop), stencil RC0/RC16 and saxpy RC0/RC16. So register-consume does NOT lose
  producer SIMD here, and the mini-block is unnecessary: RC0 (one fused loop) is within noise of
  the best RC<B> everywhere (best on recur, tied on stencil, ~0.15 ms behind on saxpy).
- Store/reload costs a second pass over each block (more code: recur SR32 713 insns vs RC0 330)
  and recovers markedly less (recur 33-52%, stencil 50-85% only at B = 32).
- Spills: kernel() stack traffic is flat (recur 22 -> 24 rsp/rbp refs, stencil 4 -> 4, saxpy
  8 -> 12). P1 (saxpy E256): the eight lane accumulators stay in ymm1..ymm8 through the
  producer/feed loop (8 x `vmulpd ymm0,ymm0` + `vaddpd` per fed block, no stack access inside
  the loop); the state goes to memory once, before the out-of-line `finish_sq`.

## Verdicts against the bars

(Wording per the Codex diff review: every "recovered" figure here is a DIRECTIONAL difference of
whole-kernel medians on a shared box, not an isolated chain-cost recovery; `epi_blk_r = 0` is a
chosen simple competitive default -- e.g. stencil/off RC32 4.957 vs RC0 5.176, saxpy/fast RC16
3.361 vs RC0 3.506 -- with no paired uncertainty establishing equality.)

- **B2r SURVIVES** (falsification bar: best form recovers >= 25% of the gap AND recur >= 1.2 ms,
  stencil >= 0.9 ms): recur best 2.98 ms fast / 2.14 ms off; stencil 1.61 / 1.73 ms. No
  regression observed in any repro variant (every RC/SR is faster than its base). FORM CHOSEN:
  **register-consume**. `epi_blk_r` default **0 = unblocked** (one fused loop); a positive
  value strip-mines (kept as the A/B knob; 8-64 measured equal within noise).
- **B1 licensed SURVIVES** (bar -0.3 ms): -0.65 ms fast / -0.40 ms off at the default E = 256
  (E = 128 was -0.75 / -0.61, inside the noise of 256; the default stays 256 as specified).
- **B1 repro SURVIVES** (bar -0.5 ms): -1.55..-1.70 ms fast / -1.75..-1.95 ms off with
  register-consume.
- Caveats: shared box, unpinned, directional only; kernel-internal time (not whole-process);
  the runner's harness A/B decides retention.

## Addendum (after implementation, branch worktree-agent-af7b4bee750b29e49)

- The shipped C++ forms are exactly the drop winners: B2r register-consume, one fused loop
  (`epi_blk_r` = 0 default); B1 licensed P1 at `epi_blk` = 256; B1 repro register-consume.
- LLVM lane (implemented after the C++ lane; no drop experiment existed for it): one fused loop
  for B1 and register-consume chains for B2r (the LLVM vectorizer has no in-order reduction on
  x86, so the RK4 producer could have gone scalar -- measured instead of assumed). Whole-program
  `completed in` medians, 15 interleaved rounds, OMP_NUM_THREADS=1, `blade compile` with
  BLADE_LLVM=1, OLD = round2\old-bin-4136f0b5 vs NEW (scratchpad W1B-new\exe, timeexe.py):
  | cell | OLD med [min-max] | NEW med [min-max] |
  |---|---|---|
  | saxpy repro | 15.043 [14.20-15.71] | 13.127 [12.48-13.72] |
  | saxpy licensed | 12.257 [11.18-17.61] | 11.916 [10.95-13.37] |
  | stencil repro | 13.801 [13.34-14.29] | 12.311 [11.99-12.77] |
  | recur repro | 13.051 [12.70-13.34] | 10.038 [9.57-12.44] |
- Raw bits NEW vs OLD on the final emissions (rawbits.py, %a of the kernel result + a producer
  bit hash, both contraction modes): saxpy lic serial, saxpy lic mt at OMP_NUM_THREADS 1/3/8,
  saxpy repro serial, stencil repro serial + mt8, recur repro serial: all IDENTICAL. The gate-off
  (CTRL) C++ is byte-identical to the OLD binary's emission for all six cells.
