# D/P1 -- the producer-fused ordered commit: costed prototype (W3-3, round 3 wave 3)

A STANDALONE prototype, not a compiler change. It decides whether a later wave builds the planner item
(`compactRowCommitAdmissible`, `Form = RowCommit`, `BLADE_ORBIT_COMMIT`) for the strict mt8 rank-2 orbit
walk -- pairkernel's checksum `ss` under `BLADE_FP_REPRO=1` with threads. Design: `research/D-orbit-regions.md`
section 3; contract: `reviews/codex-review-D.md` findings 1-4; brief: W3-3.

## Files

| file | what |
|---|---|
| `pairkernel_repro_emit.cpp` | `blade emit bench/kernels/pairkernel/pairkernel.blade` at 361504b6, `BLADE_FP_REPRO=1 BLADE_FP_CONTRACT=off`, threads: the threaded `comm` producer (`schedule(dynamic)` over rows), then the serial ordered scatter `scatter2<4, 64, true>` |
| `pairkernel_repro_rowslots_emit.cpp` | the same plus `BLADE_ORBIT_PULL=0 BLADE_FOLD_ROW_SLOTS=prefer`: the G35 row slots |
| `p1_commit.cpp` | the prototype: the emitted pieces verbatim, the ticket commit `commit2`, the controls, the instruments, the raw-bit identity matrix |
| `run_matrix.py` | the measurement driver + analysis (interleaved rounds, paired bootstrap CIs, the decision rule) -- QUIET BOX ONLY |
| `identity-T1-3-8.txt`, `identity-thread-limit-2.txt` | the identity matrix's output (OMP_NUM_THREADS=8; and with OMP_THREAD_LIMIT=2) |

The only shared code: `src/cpp/blade_orbit_scatter.hpp` now spells `tri` as its ascending sequence of
`tri_group<G, B, VADD, THIRD>(.., i0, ..)` calls (rows i0 .. i0+G-1) followed by `tri_tail_row<THIRD>` per tail row --
the group and tail bodies moved verbatim, no behaviour change (`blade test orbit-scatter`: 30 passed, 0 failed,
0 skipped after the factoring).

## Build

PowerShell, `C:\msys64\ucrt64\bin` first on PATH, from the worktree root (the flags are `Build.fs` `optFlags`
under `BLADE_FP_CONTRACT=off`, plus `-fopenmp` and the safety flags, as `blade compile` passes them):

```text
g++ -std=c++17 -O3 -march=native -ffp-contract=off -fno-math-errno -fwrapv -fopenmp ^
    -Werror=float-conversion -Werror=narrowing -I src/cpp ^
    -o <scratch>\p1_commit.exe bench\notes\round3\prototypes\p1-commit\p1_commit.cpp
```

The two emitted files build with the same line (they are the bench arm's program, verbatim but for trailing
whitespace stripped from blank lines).

## The schedule as implemented (`commit2`)

```text
prelude:  R[0..n) = si; out = so; claim = 0; ticket = 0      (claim / ticket on their own cache lines)
          nfull = n / G; ng = nfull + (n % G != 0); team = min(T, ng)
#pragma omp parallel num_threads(team)
  for (g = claim.fetch_add(1, relaxed); g < ng; g = claim.fetch_add(1, relaxed)):
      lo = gG; hi = min(lo + G, n)
      produce pool rows lo .. hi-1          (the emitted producer row body, verbatim)
      while ticket.load(acquire) != g:      _mm_pause; std::this_thread::yield every 2048 spins
      g < nfull:  tri_group<G, 64, VADD, false>(pool, n, R, .., lo, f, k_in)    (the serial scatter's group g)
      g == nfull: tri_tail_row<false>(pool, n, R, .., i, f, k_in), i = lo .. n-1 ascending   (the TAIL interval)
      for i = lo .. hi-1: out = k_out(out, R[i])                                (ONE outer accumulator)
      ticket.store(g + 1, release)
```

- The ticket unit is an EXPLICIT canonical row interval, G-aligned (`[gG, min(gG + G, n))`), never a thread id or
  a readiness ordinal; claims are unique (relaxed `fetch_add`) and ascending, so interval g waits only on
  intervals < g, each claimed by a thread that is already running (progress assumes runnable workers run).
- Every pool row the interval reads (its own rows -- `tri_group` reads nothing else) is produced, by the
  committing thread, before its ticket wait.
- The tail (n = 2503, G = 4: 625 full groups + rows 2500..2502; G = 8: 312 + 7 rows) is ONE interval, committed by
  `tri_tail_row` per row exactly as `tri`'s tail loop.
- The ticket's release (after the commit's last write to R and out) and acquire (before the next commit's first
  read) publish R and out; nothing else is shared.
- `out` advances through R[lo], .., R[hi-1] in increasing i, interval after interval: the outer chain from its seed
  over every completed row in order -- no per-group subtotal, no reseed (review D/1's `[1e16, -1e16, 1]`).
- The interval sequence is exactly `tri`'s call sequence, each call on the values the previous ones left, so every
  row chain receives the serial scatter's operand sequence. The producer's kernel is the emitted one (pure, total:
  matern52's sqrt / exp / divide); the producer's stores are all kept (every named pool cell is written).

## Raw-bit identity (`p1_commit.exe id 3`)

Per case the commit's R (every row chain) and `out` against, by raw bits: the serial scatter at the same G, the
serial scatter at G = 4 (the emitted default), and the logical walk (each row over j ascending through the
canonical cell, then the outer chain); and the pool the commit's claims produced against the emitted
`schedule(dynamic)` producer's (the commit's pool is poisoned with 0xA5 first). Extents n in {1, 2, 3, 4, 5, 7, 8, 9,
13, 23, 101, 2503} (ragged tails for every G; teams clamped to the interval count, so n = 13, G = 8 runs a team
of 2); G in {1, 4, 8}; teams T in {1, 3, 8}; 12 repetitions per small-n case, 3 at n = 2503. Families:

| family | f | inner kernel | outer kernel | seeds (inner / outer) | compared |
|---|---|---|---|---|---|
| pk | x*x (pairkernel) | builtin + (VADD) | + | 0.0 / 0.0 | raw bits |
| nc | x*x - 0.3 | 0.5*a + z (non-commutative) | 0.75*a - r (non-commutative) | -0.0 / 0.75 | raw bits |
| cx | (x - 0.5) * 1e16 + x (cancellation) | builtin + (VADD) | + | 0.0 / 1.0 | raw bits |
| nan1 | one fixed NaN payload where x > 0.9 (the diagonal included), else x | + | 0.5*a + r | -0.0 / -0.0 | raw bits |
| nanN | a NaN carrying x's low mantissa bits where x > 0.9, else x | + | 0.5*a + r | -0.0 / -0.0 | NaN-equivalence (raw bits on every non-NaN value) |

Result: OMP_NUM_THREADS=8 (teams 1 / 3 / 8 requested and observed): **6075 cases, 0 mismatches**; with
OMP_THREAD_LIMIT=2 (requested 3 / 8, ACTUAL team 2): **6075 cases, 0 mismatches**. The timed modes also print
the checksum's raw bits, an FNV-1a hash of R and of the pool: every mode (b at G = 1 / 4 / 8, c, d / di at G = 1 /
4 / 8, every team setting) printed checksum bits `41414b26e03cc681`, R `22254f0d378f20c0`, pool `3746493d268ca13`
(the emitted programs print the same `checksum = 2266701.75185472`).

THE NaN-PAYLOAD CONTRACT (review D/3: state it). Raw-bit identity holds for every non-NaN value and for NaN-ness;
a NaN's PAYLOAD is identical when at most one payload can meet another in a chain (`nan1`). With several payloads
(`nanN`) the payload of NaN + NaN is the operand the compiler placed first, and g++ commutes a builtin `+` per code
context: the SERIAL scatter already disagrees with itself across G (30,537 of 87,804 values) and with the walk
(59,481); the commit vs the serial scatter at the same G disagree on 171. This is a property of the strict forms
today, not of the schedule. See observation 2 below for the `orbit-scatter` block's wording.

## Instruments (`p1_commit.exe di G`)

rdtsc per interval, calibrated against steady_clock over the region by the master thread: per-interval
production (claim .. last row stored), ticket wait (produced .. ticket acquired), commit service (acquired ..
released); their sums (core-time: P, the summed waits, S); `wait_frac_core` = summed waits / (actual team x
region wall) -- the brief's 20% denominator; the longest single predecessor stall and its interval; intervals that
waited at all; spin-limit yields; handoffs (intervals committed by a different thread than their predecessor);
summed thread startup and drain; production medians / maxima / first-8 / last-8 means; the actual team. The
analysis adds the run's own lower bound max(S, (P + S) / team) beside the measured region wall (review D/2's bound
with this run's P and S instead of the brief's 43.2 core-ms).

## Observations (out of this item's scope; recorded for the orchestrator, nothing changed)

1. **The unseeded inner fold is emitted SEEDED with `0.0`.** `bench/kernels/pairkernel/pairkernel.blade` line 43
   spells the row fold UNSEEDED: `reduce(method_for(range<Pts>) <@> lambda(j) -> kmat(i, j) * kmat(i, j), (+))`.
   Both strict forms of it carry a `0.0` seed:
   - the walk's row callable `__lambda_80`: `pairkernel_repro_emit.cpp` line 230 (and
     `pairkernel_repro_rowslots_emit.cpp` line 230, the callable the G35 row slots call at line 315):
     `double __ret1073741824 = 0.0;`, then every j folded onto it -- no head peel, no empty-row check;
   - the scatter: `pairkernel_repro_emit.cpp` line 305 passes `double(0.0)` as the row chains' seed
     (`scatter2<4, 64, true>(.., (size_t)2503, double(0.0), __v60, ..)`).

   Contrast the program's other unseeded fold, `reduce(pts, (+))` over a row VIEW (`__lambda_85`, line 248, and
   its inline copy at line 312): it is emitted with the head peel and `blade_rt::panic("BL8003", ..)` on an empty
   row. So an unseeded fold over a DEFERRED row map (this shape) loses its empty-row BL8003: over an empty row it
   would give `0.0` instead of refusing, and an all-`-0.0` row would fold to `+0.0` instead of `-0.0`. The two
   lanes here agree with each other (the scatter was built to match the walk), so it is a seed-semantics question
   of the lowering, not of the scatter. pairkernel's rows are never empty and its terms are squares (never `-0.0`),
   so the bench is unaffected. Not checked: where in lowering the seed is supplied, and whether the interpreter
   and the LLVM lane do the same.

2. **The `orbit-scatter` block's "NaN payload included" wording.** From `tests/OrbitScatterTests.fs` (read, not
   run): every NaN witness is the ONE-payload kind (this prototype's `nan1`):
   - the emitted-form legs' NaN pools (lines 94-100, 130-137, 496-505) come from `xDecl`'s
     `sqrt(Float64(s) - 5.0)` term on ONE row: an invalid sqrt gives the platform's default quiet NaN, and the
     products / sums that carry it keep that one payload, so NaN + NaN has one answer whatever the operand order;
   - the header-level pull leg's comment says so outright (line 401: "one NaN payload -- which payload survives
     two NaNs is outside the contract"), and the emitted pull leg too (lines 454-455: "a controlled NaN -- one
     payload, from the pool's own invalid sqrt").
   So the pass lines' "NaN payload and -0.0 included" (line 618) is true of its witnesses and consistent with the
   contract above. Its comments already scope it; the PASS text alone does not say "one payload".

## Measurement (2026-10-21, the orchestrator's quiet window)

### Protocol

```text
C:\Python314\python.exe -I bench\notes\round3\prototypes\p1-commit\run_matrix.py <scratch>\p1_commit.exe <scratch>\p1-ab 21
```

21 interleaved rounds (every configuration once per round, shuffled, one process each; `OMP_DYNAMIC=false`;
the serial arm pinned to CPU 2 as the harness pins it, the threaded arms unbound as the harness runs mt8) of 14
configurations x 4 team settings (T1, T3, T8, R2 = `OMP_NUM_THREADS=8 OMP_THREAD_LIMIT=2`): (a) the producer alone,
(a2:G) the producer through the claim loop alone, (b:G) + the serial scatter, (c) + the G35 row slots, (d:G) + the
commit, (di:G) + the commit instrumented, G in {1, 4, 8}. `summary.md` gets the medians with bootstrap CIs, the
paired per-round differences (b:4 - d:G, b:4 - c, d:G - a2:G, ...), the instrument medians with the bound, the
identity check of every run against the b:4 reference, and the decision rule at T8: TAKE only if the paired CI's
lower bound of b:4 - d:G is >= 1.5 ms AND the median `wait_frac_core` < 0.20; otherwise DROP (RESULTS 17.6, the
G35 / G44 rows).

The run: the binary built from this commit's `p1_commit.cpp` with the build line above, 22:39:43 - 22:40:54
(-04:00), 1176 processes. The full output is `ab-2026-10-21/` (`summary.md`, `raw.csv`, `meta.json`).

### Box load

`Get-Counter '\Processor(_Total)\% Processor Time'`, 10 one-second samples: BEFORE (22:39:36) average **7.4%**
(min 2.7, max 11.8); AFTER (22:41:11) average **4.4%** (min 1.4, max 9.8). The window's load was four read-only
Codex review processes and the usual node / python editor helpers; no dotnet / g++ / Blade test process. The
threaded arms are unbound (as the harness runs mt8: libgomp cannot bind on Windows), so they share the 16
logical CPUs with that load.

### Identity

**1176 runs, 0 mismatches / failed runs**: every b / c / d / di run printed the b:4 reference's checksum bits
`41414b26e03cc681`, R hash `22254f0d378f20c0` and pool hash `3746493d268ca13`, and every a / a2 run the same pool
hash, in every team setting (the restricted team observed at actual = 2).

### End-to-end ms (the emitted program's own timer), median [95% bootstrap CI], 21 rounds

| team | a (producer) | a2:4 (claims) | b:4 (scatter, today) | c (G35 row slots) | d:1 | d:4 | d:8 |
|---|---|---|---|---|---|---|---|
| T1 (pinned) | 28.17 [27.75, 28.35] | 28.34 [28.12, 28.91] | 29.91 [29.41, 30.09] | 38.39 [37.59, 39.11] | 30.60 [30.32, 30.87] | 29.13 [28.86, 29.26] | 28.93 [28.69, 29.19] |
| T3 | 11.85 [11.79, 12.06] | 12.08 [12.00, 12.23] | 13.54 [13.49, 13.81] | 17.79 [17.52, 18.28] | 12.63 [12.59, 12.71] | 12.45 [12.31, 12.70] | 12.27 [12.16, 12.47] |
| **T8** | 6.80 [6.64, 7.14] | 6.64 [6.32, 6.99] | **8.38 [8.11, 8.78]** | 9.81 [9.53, 10.24] | 8.54 [8.25, 9.07] | **7.98 [6.90, 8.56]** | **8.15 [7.47, 8.41]** |
| R2 (actual 2) | 15.97 [15.73, 16.11] | 17.09 [16.93, 17.73] | 17.89 [17.71, 18.84] | 23.79 [23.64, 24.50] | 17.25 [17.00, 17.46] | 17.42 [17.26, 18.13] | 17.35 [17.19, 17.63] |

(b:1 / b:8, a2:1 / a2:8 and the di arms: `ab-2026-10-21/summary.md`.)

### Paired per-round differences (ms), median [95% bootstrap CI of the median]

| team | b:4 - d:1 | b:4 - d:4 | b:4 - d:8 | b:4 - c | b:4 - a (scatter cost) | d:4 - a2:4 (commit cost) |
|---|---|---|---|---|---|---|
| T1 | -0.685 [-1.139, -0.534] | +0.653 [+0.200, +1.119] | +0.646 [+0.147, +1.253] | -8.118 [-9.052, -7.715] | +1.793 [+1.226, +2.076] | +0.816 [+0.378, +0.990] |
| T3 | +1.061 [+0.774, +1.221] | +1.231 [+0.895, +1.454] | +1.356 [+0.953, +1.458] | -4.255 [-4.520, -3.932] | +1.699 [+1.597, +1.967] | +0.467 [+0.305, +0.539] |
| **T8** | **-0.025 [-0.476, +0.384]** | **+0.256 [-0.167, +1.608]** | **+0.397 [-0.059, +1.496]** | -1.402 [-2.004, -1.056] | +1.714 [+1.216, +2.001] | +1.094 [+0.040, +2.144] |
| R2 | +0.591 [+0.207, +1.137] | +0.428 [+0.162, +0.770] | +0.567 [+0.300, +0.834] | -6.057 [-6.637, -5.750] | +2.097 [+1.605, +2.947] | +0.293 [+0.182, +0.566] |

### Instruments at T8 (di:G, medians over 21 rounds; core-time sums)

| G | intervals | region wall | P (production) | S (commit service) | summed waits | **wait_frac_core** | longest stall | handoffs | bound max(S, (P+S)/8) | region / bound |
|---|---|---|---|---|---|---|---|---|---|---|
| 1 | 2503 | 7.78 ms | 42.7 ms | 2.60 ms | 13.6 ms | **0.211** | 73 us | 2502 | 5.66 ms | 1.29 |
| 4 | 626 | 7.41 ms | 45.2 ms | 1.40 ms | 10.5 ms | **0.153** | 102 us | 625 | 5.84 ms | 1.20 |
| 8 | 313 | 7.15 ms | 42.5 ms | 1.41 ms | 9.5 ms | **0.155** | 157 us | 312 | 5.47 ms | 1.20 |

At T1 S is 0.94 ms (G = 4) and 0.83 ms (G = 8): on eight cores the same commits cost about 1.5x more (R moves
between cores with every ticket: every interval is a handoff). At T3 and R2 the summed waits are below 2%.

### Against review D/2's lower bound

The brief's bound, max(S, (43.2 + S) / 8) = 5.5-5.6 ms end-to-end, assumed S = 0.78-1.25 ms. Measured at T8, G = 4:
P = 45.2 core-ms, S = 1.40 ms, so this run's own bound for the REGION is 5.84 ms. The region took 7.41 ms
(1.20x the bound) and the program 7.98 ms end-to-end (the region plus pts, the input checksum and the 25 MB pool's
first touch). The 1.6 ms between the region and its bound is summed waits (10.5 core-ms / 8 = 1.3 ms), startup
and drain.

### The T8 distribution is BIMODAL

Per-round T8 times (ms):

```text
b:4   8.23 7.51 8.38 8.26 8.92 8.92 9.67 8.91 7.94 8.78 8.33 7.93 9.27 7.69 7.99 8.39 8.73 8.54 7.76 8.11 10.23
d:4   8.49 7.67 7.02 8.64 6.55 8.87 6.45 6.90 8.57 9.09 6.73 6.07 8.44 7.89 6.09 8.56 8.47 8.56 7.98 6.22 8.68
a     7.01 8.04 6.74 8.02 6.20 7.06 7.54 6.23 6.80 7.25 6.64 7.10 7.27 6.73 6.01 6.55 6.66 7.60 6.05 6.05 7.14
```

d:4 lands in a FAST mode (6.07-7.02 ms in 8 of 21 rounds, close to the producer alone) or a SLOW mode (7.67-9.09 ms,
no better than the scatter). The instrumented arm shows the same split, and the waits are what separate the
modes: the fast di:4 runs have wait_frac_core 0.03-0.12 (6.06 ms at 0.034, 6.17 ms at 0.051), the slow ones mostly
0.14-0.24 (two slow runs, 7.50 and 8.78 ms, sit at 0.09: waits are not the whole story). di:8 is the same (5.87 ms at 0.033, about 8.5 ms at 0.21). It is a hypothesis, not measured, that the
slow mode is the unbound team's placement: one producer sharing a core with another (or with the load) holds the
ticket while the other seven wait behind it (head-of-line blocking; the predecessor stall the instruments
record). b:4 shows no such split (7.5-10.2, unimodal), because its scatter is serial after a dynamic producer that
can absorb a slow thread.

### Decision (the rule, at T8)

- G = 1: b:4 - d:1 = -0.025 ms [-0.476, +0.384], wait_frac_core 0.211 -> **DROP** (fails both conditions).
- G = 4: b:4 - d:4 = +0.256 ms [-0.167, +1.608], wait_frac_core 0.153 -> **DROP by the rule, INDICATIVE**: the lower
  bound is far from 1.5 ms (it is below zero), but the CI's upper end (+1.608) crosses 1.5 ms because of the
  bimodality above.
- G = 8: b:4 - d:8 = +0.397 ms [-0.059, +1.496], wait_frac_core 0.155 -> **DROP** (the upper end stops just short of
  1.5 ms; same bimodality).

So no G meets the rule: the planner item does not follow from this window. The DROP is not marginal in its
median (0.26-0.40 ms against a 1.5 ms requirement), but at G = 4 and G = 8 it rests on a bimodal T8 distribution
that is placement-dependent. In its fast mode the commit is at about 6.1-6.9 ms, which is at or under the expert
6.21 / 6.42. A placement-controlled re-run, or a commit that does not stall behind a slow producer, could change
the reading. That is the INDICATIVE label the orchestrator asked for; the record line for RESULTS 17.6 and the G35 /
G44 rows would read: "the producer-fused commit measured at 7.98 ms (G = 4; median, 21 rounds, mt8 repro) vs the
scatter's 8.38 ms: not taken (b - d = +0.26 ms [-0.17, +1.61]; waits 15% of team x wall; bimodal, fast mode about
6.1-6.9 ms)".

Outside the rule, but measured: the commit beats the scatter at every other team size: T1 +0.65 ms at G = 4, T3
+1.23 ms [+0.90, +1.45], R2 +0.43 ms. The G35 row slots lose to the scatter everywhere (-1.4 ms at T8), which
confirms W2-0a's precedence.
