# Generator check at full sizes (methodology review M6)

Produced by `bench/validation/generator_check.py` (numpy 2.4.6, `C:\Python314\python.exe -P`), generator variant **R5** (benchgen.py). Every number is a measurement on the exact suite inputs at the specs' FULL sizes (maskagg at its revised n = 1,000,003). Yardsticks: closed forms and numpy PCG64.

## Reference values (variant R5, seed 10000019)

| k | final integer state | u |
|---|---|---|
| 0 | 408748897 | np.float64(0.19033853756263852) |
| 1 | 189020261 | np.float64(0.08801941806450486) |
| 2 | 1945512958 | np.float64(0.9059500684961677) |

x(k) = gen(k, 10000019, -1, 1), k = 0..9: sum = -3.1010268041864038, sum of squares = 3.4272198544854002

Check: matches the cross-language pins [408748897, 189020261, 1945512958]

Blade, both lanes (C++ and LLVM, 2026-10-17, `<scratchpad>/p/g10.blade`): final states 408748897, 189020261, 1945512958; 10-cell [-1, 1) selfcheck sum = -3.1010268041864, sum of squares = 3.4272198544854 (15 significant digits).

## Before / after: R3 (the 2026-10-17 morning formula) vs R5 (adopted, 'variant D') vs PCG64

R3 = affine, quadratic, affine. R5 = R3 followed by a middle-square round and a final affine round. The detailed sections below this table measure R5 only. Rerun with BENCH_GENERATOR=R3 to reproduce the R3 column in full.

| metric | R3 | R5 | PCG64 |
|---|---|---|---|
| P(bit b of the final state = 1), b = 0..3 (iid 0.5) | 0.750 0.375 0.437 0.531 | 0.500 0.500 0.501 0.501 | n/a |
| max |autocorr|, lags 1..1.2e6 (iid max ~0.005) | 0.2685 | 0.0046 | 0.0043 |
| maskagg run ratio P(L>=k)/iid, k=3..9 | 0.91 1.02 1.11 1.28 1.29 1.34 1.25 | 0.99 1.00 0.99 0.99 0.99 1.00 1.00 | 1.00 1.00 1.01 1.00 0.99 0.97 0.96 |
| gram row pairs beyond 6 sd (of 1,280,800) | 570 | 0 | 0 |
| gram max |row r| | 0.344 | 0.150 | 0.144 |
| stencil lap excess kurtosis (iid -0.780) | -0.7421 | -0.7806 | -0.7818 |

R3's defects, all measured above: (1) isolated long lags strongly correlated (u(k) vs u(k+981991): r = -0.27, the same in every 1001-cell window; lag 1081146: +0.20), from the affine first round (h1(k+D) = h1(k) + D*A mod 2^31) surviving one quadratic mix; (2) as a consequence, 570 gram row pairs correlated beyond 6 sd (e.g. rows 84/719, 635 rows apart, r = 0.34); (3) maskagg run lengths not geometric (runs of 6-9 about 30% too frequent) although every lag-1..12 autocorrelation of the indicator is ~0; (4) higher-order dependence between stencil neighbours (lap kurtosis -0.742 vs -0.780); (5) low-bit bias of h3 (bit 0 set in 75% of cells, bits 1-3 also biased), harmless (<= 2^-27 of u). R5 removes all five: every row of the table is at PCG64's level, and R5's low bits are balanced.

R5 = R3 followed by `h4 = (h3 * h3 div 65536) mod 2^31; h5 = (h4 * 1103515245 + 12345) mod 2^31; u = h5 / 2^31`. It is exact in int64 (h3^2 < 2^62) and uses only + * div mod (Blade has no bitwise operators). Cost: two more integer multiply/shift/mask rounds per cell, identical in every arm.

## Marginals of every full-size input array (U(0,1): mean 0.5, var 0.083333)

| array | cells | mean u | var u | P(bit b = 1), b = 0..7 of the final state |
|---|---|---|---|---|
| fusedchain a | 2000003 | 0.499757 | 0.083310 | 0.501 0.500 0.500 0.501 0.499 0.500 0.500 0.500 |
| onepass x | 5000011 | 0.499856 | 0.083316 | 0.500 0.500 0.501 0.500 0.500 0.500 0.500 0.500 |
| stencil u | 3006503 | 0.499817 | 0.083328 | 0.501 0.500 0.501 0.500 0.500 0.500 0.500 0.500 |
| recur u0 | 20011 | 0.501075 | 0.082992 | 0.496 0.500 0.503 0.497 0.501 0.504 0.496 0.499 |
| maskagg x | 1000003 | 0.499661 | 0.083343 | 0.500 0.500 0.501 0.501 0.499 0.500 0.500 0.500 |
| dot a | 3000017 | 0.499824 | 0.083328 | 0.501 0.500 0.501 0.500 0.500 0.500 0.500 0.500 |
| dot b | 3000017 | 0.499731 | 0.083320 | 0.501 0.500 0.500 0.500 0.500 0.500 0.500 0.500 |
| saxpy x | 2000003 | 0.499757 | 0.083310 | 0.501 0.500 0.500 0.501 0.499 0.500 0.500 0.500 |
| saxpy y | 2000003 | 0.499600 | 0.083311 | 0.500 0.500 0.500 0.501 0.500 0.500 0.500 0.500 |
| gram X | 1602601 | 0.499703 | 0.083326 | 0.500 0.500 0.500 0.501 0.499 0.500 0.500 0.500 |
| comoment3 X | 34371 | 0.501033 | 0.082951 | 0.498 0.501 0.503 0.499 0.500 0.504 0.498 0.497 |
| antisym X | 481601 | 0.499374 | 0.083306 | 0.499 0.500 0.501 0.501 0.499 0.502 0.500 0.500 |
| antisym Y | 481601 | 0.499689 | 0.083333 | 0.501 0.500 0.500 0.501 0.499 0.500 0.499 0.499 |
| pairkernel x | 2503 | 0.506045 | 0.081778 | 0.503 0.509 0.496 0.502 0.495 0.518 0.491 0.504 |
| pairkernel y | 2503 | 0.489320 | 0.083474 | 0.499 0.494 0.503 0.478 0.508 0.485 0.495 0.503 |
| pairkernel z | 2503 | 0.501588 | 0.084388 | 0.521 0.494 0.489 0.503 0.516 0.479 0.496 0.502 |

Verdict: OK -- every mean and variance matches U(0,1) within 6 standard errors.

Verdict: OK -- the low bits are balanced.

## Cross-array correlation between one kernel's inputs (iid: |r| ~ 1/sqrt(N))

| kernel | arrays | N | r | |r| sqrt(N) |
|---|---|---|---|---|
| dot | a, b | 3000017 | +0.000554 | 0.96 |
| saxpy | x, y | 2000003 | +0.001047 | 1.48 |
| antisym | X, Y | 481601 | +0.000820 | 0.57 |
| pairkernel | x, y | 2503 | +0.019151 | 0.96 |
| pairkernel | x, z | 2503 | +0.061577 | 3.08 |
| pairkernel | y, z | 2503 | +0.036508 | 1.83 |
| (seed pair j=1,3, long) | segments 1, 3 | 3000017 | -0.000760 | 1.32 |
| (seed pair j=2,3, long) | segments 2, 3 | 3000017 | +0.000078 | 0.13 |

Verdict: OK -- largest |r| sqrt(N) = 3.08: the inputs of one kernel are uncorrelated (they are non-overlapping segments of one sequence, 10000019 apart; the two 'long' rows repeat pairkernel's seed pairs over 3M cells, where any structural correlation would show).

## Lag structure of one long sequence (FFT autocorrelation of 2^21 cells)

Largest |autocorrelation| over lags 1..1200000; PCG64's largest over the same lags: 0.0043.

| lag | autocorrelation |
|---|---|
| 1182879 | +0.0046 |
| 1141558 | -0.0045 |
| 1004699 | -0.0043 |
| 1173433 | -0.0043 |
| 1136323 | +0.0042 |
| 1161786 | +0.0042 |

Kernel-relevant lags (stencil neighbours 1, 2003, 4006; row lengths 201, 401, 1001): 1: +0.0006, 2: -0.0001, 3: +0.0004, 4: -0.0003, 8: -0.0002, 201: +0.0010, 401: +0.0006, 1001: +0.0013, 2003: -0.0006, 4006: +0.0000 (worst 1.90 sd)

Verdict: OK -- no lag stands out (max |r| 0.0046, PCG64 0.0043).

## 2-D inputs: stride autocorrelations and gram's row-row correlations

| array | shape | lag-1 row | lag-1 column | max |lag 2..8| along row | 1/sqrt(cells) |
|---|---|---|---|---|---|
| gram X | 1601 x 1001 | +0.00025 | +0.00186 | 0.00156 | 0.00079 |
| comoment3 X | 171 x 201 | +0.00593 | -0.00644 | 0.00703 | 0.00539 |
| antisym X | 1201 x 401 | -0.00102 | +0.00082 | 0.00224 | 0.00144 |
| antisym Y | 1201 x 401 | -0.00105 | -0.00077 | 0.00319 | 0.00144 |
| stencil u | 1501 x 2003 | +0.00033 | +0.00003 | 0.00134 | 0.00058 |

Verdict: OK -- largest stride autocorrelation 2.35 sd.

gram X row-row correlations over 1280800 pairs: sd 0.03162 (iid 0.03161), max |r| 0.1498 (PCG64 0.1435), pairs beyond 6 sd: 0 (PCG64 0).

Verdict: OK -- gram's rows are mutually uncorrelated like iid rows.

## maskagg: acceptance and run lengths (n = 1,000,003, x in [0, 2), keep x > 1.0)

accepted m = 499211 (rate 0.499210; iid sd 0.000500)

Ratio observed / iid of P(run length >= k), k = 2, 3, 4, 5, 6, 7, 8, 9, 10:

| runs of | runs | mean length (iid 2) | k=2 | k=3 | k=4 | k=5 | k=6 | k=7 | k=8 | k=9 | k=10 |
|---|---|---|---|---|---|---|---|---|---|---|---|
| accepted | 250099 | 1.9961 | 1.000 | 0.992 | 0.995 | 0.994 | 0.986 | 0.992 | 1.004 | 0.996 | 0.944 |
| rejected | 250100 | 2.0024 | 0.999 | 1.001 | 1.005 | 1.011 | 1.017 | 1.015 | 1.027 | 1.041 | 1.022 |
| PCG64 accepted | 249900 | 2.0018 | 1.001 | 1.003 | 1.005 | 1.010 | 1.003 | 0.988 | 0.973 | 0.961 | 0.985 |

Verdict: OK -- acceptance rate and run lengths are those of iid fair coin flips.

## stencil output lap (full size 1501 x 2003)

mean -0.000001 (iid 0), variance 1.666285 (iid 1.666667), skewness -0.0008 (iid 0), excess kurtosis -0.7806 (exact iid -0.7800; PCG64 fields -0.7818, -0.7790, -0.7810), integer-valued cells 0.0e+00

percentiles 0.1/1/25/50/75/99/99.9: -3.1363, -2.6307, -1.0093, +0.0007, +1.0083, +2.6284, +3.1363

Verdict: OK -- the Laplacian has the iid distribution (continuous, centred, right variance and shape).

## pairkernel: pair-distance histogram (n = 2503 points, unit cube)

pairs 3131253; mean distance 0.661246 (exact unit-cube mean 0.661707; PCG64 points 0.660753); min 0.00479; max 1.60025

| distance bin | count | PCG64 count |
|---|---|---|
| 0.000-0.124 | 21612 | 21341 |
| 0.124-0.247 | 127343 | 125496 |
| 0.247-0.371 | 276740 | 277764 |
| 0.371-0.495 | 422226 | 425957 |
| 0.495-0.619 | 523822 | 527037 |
| 0.619-0.742 | 557071 | 557281 |
| 0.742-0.866 | 512667 | 507145 |
| 0.866-0.990 | 382927 | 382998 |
| 0.990-1.113 | 203263 | 203627 |
| 1.113-1.237 | 79267 | 78692 |
| 1.237-1.361 | 20998 | 20663 |
| 1.361-1.485 | 3085 | 3034 |
| 1.485-1.608 | 232 | 214 |
| 1.608-1.732 | 0 | 4 |

K over the pairs: mean 0.56898, sd 0.19445, fraction K < 1e-3 0.0000, fraction K > 0.9 0.0528. Largest relative bin difference against the PCG64 point set: 0.017. The point-set noise is ~1/sqrt(2503) = 0.02 per bin, more in the sparse bins.

Verdict: OK -- the distance distribution is that of uniform points in the unit cube (Robbins' mean; bin counts agree with an independent PCG64 point set); K spans its range.

