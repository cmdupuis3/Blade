| kernel | serial build | mt1 | mt2 | mt3 | mt4 | mt4 vs serial | mt4 vs mt1 | mt1 overhead | max IQR/med | checksum rel spread |
|---|---|---|---|---|---|---|---|---|---|---|
| antisym | 46.59 | 45.47 | 24.73 | 18.46 | 13.31 | 3.50x | 3.42x | 0.98 | 40.8% | 3.5e-15 |
| band | 2.80 | 3.05 | 1.68 | 1.83 | 1.45 | 1.94x | 2.11x | 1.09 | 48.8% | 2.7e-15 |
| comoment3 | 14.22 | 13.91 | 7.86 | 5.44 | 4.37 | 3.25x | 3.18x | 0.98 | 10.8% | 3.8e-15 |
| dot | 41.11 | 44.65 | 22.76 | 15.92 | 14.62 | 2.81x | 3.05x | 1.09 | 20.3% | 1.5e-14 |
| fusedchain | 11.58 | 15.07 | 8.05 | 5.82 | 5.07 | 2.29x | 2.97x | 1.30 | 32.3% | 6.5e-15 |
| gram | 91.87 | 94.69 | 51.98 | 36.49 | 26.24 | 3.50x | 3.61x | 1.03 | 10.6% | 0.0e+00 |
| haloadjoint | 85.01 | 88.65 | 72.79 | 71.15 | 65.35 | 1.30x | 1.36x | 1.04 | 22.0% | 1.1e-15 |
| onepass | 30.71 | 39.25 | 20.72 | 16.34 | 12.90 | 2.38x | 3.04x | 1.28 | 20.6% | 9.0e-15 |
| pairkernel | 44.23 | 44.58 | 23.05 | 16.64 | 13.10 | 3.37x | 3.40x | 1.01 | 9.9% | 0.0e+00 |
| recur | 26.09 | 26.82 | 17.22 | 13.06 | 12.16 | 2.15x | 2.20x | 1.03 | 21.1% | 0.0e+00 |
| saxpy | 35.93 | 39.39 | 22.09 | 16.88 | 14.20 | 2.53x | 2.77x | 1.10 | 16.6% | 2.7e-15 |
| stencil | 31.08 | 38.77 | 20.42 | 16.40 | 13.60 | 2.28x | 2.85x | 1.25 | 21.2% | 0.0e+00 |
| twofir | 30.98 | 36.31 | 20.57 | 17.04 | 12.00 | 2.58x | 3.03x | 1.17 | 23.3% | 0.0e+00 |

### antisym
- [fold-form v1] __v34: applied [fold form: antisym tile sequential 6x8 (P pass, spill, Q pass), whole chain per tile, row groups of 4 (cache budget 200 KiB of 256 KiB; the team-eligible-tile default: the omp licence, thread emission and the driver's static work
- [fold-form v1] __v63: applied [fold form: orbit-class fold (rank 2, 2 classes); thread team over the first-coordinate rows, per-thread class sums combined in thread order; one pass over the 720600-cell AntisymIdx<2, 1201> pool of `__v34` instead of 1201^2 logi
- [fold-form v1] __v98: applied [fold form: threaded(parallel for simd reduction); reduce over a materialized array (Path A)]

### band
- [fold-form v1] __v53 @ 41:1: applied [fold form: threaded(fixed blocks over the diagonal index k, fixed-order combine; simd within each block); reduce over a closed-form domain (Band: band walked by diagonal, k = i - j in [-101, 101], simd on the i run)]

### comoment3
- [fold-form v1] __v90: applied [fold form: orbit-class fold (rank 3, 3 classes); thread team over the first-coordinate rows, per-thread class sums combined in thread order; one pass over the 848046-cell SymIdx<3, 171> pool of `__v29` instead of 171^3 logical re
- [fold-form v1] __v123: applied [fold form: threaded(Path B chunks, simd-reduction within each chunk); reduce over a deferred computation]

### dot
- [fold-form v1] __v37 @ 27:1: applied [fold form: threaded(Path B chunks, simd K-lanes(8) dot per chunk); reduce over a deferred computation (one level, Path B)]
- [fold-form v1] __v60: applied [fold form: threaded(Path B joined chunks, 3 builtin + legs, simd-reduction within each chunk); reduction join of 3 legs (one traversal, one team)]
- [omp v1] __v60: applied [omp parallel num_threads(T) + fixed-order combine; threaded reduction join (Path B joined chunks, G7): one team over the shared traversal, one omp simd reduction(+) over the 3 private accumulators within each chunk; each leg's partials

### fusedchain
- [fold-form v1] __v42 @ 34:1: applied [fold form: threaded(Path B chunks, simd-reduction within each chunk); reduce over a deferred computation]
- [fold-form v1] __v59: applied [fold form: threaded(Path B joined chunks, 2 builtin + legs, simd-reduction within each chunk); reduction join of 2 legs (one traversal, one team)]
- [omp v1] __v59: applied [omp parallel num_threads(T) + fixed-order combine; threaded reduction join (Path B joined chunks, G7): one team over the shared traversal, one omp simd reduction(+) over the 2 private accumulators within each chunk; each leg's partials

### gram
- [fold-form v1] __v56: applied [fold form: orbit-class fold (rank 2, 2 classes); thread team over the first-coordinate rows, per-thread class sums combined in thread order; one pass over the 1282401-cell SymIdx<2, 1601> pool of `__v27` instead of 1601^2 logical
- [fold-form v1] __v85: applied [fold form: threaded(Path B chunks, simd-reduction within each chunk); reduce over a deferred computation]

### haloadjoint
- [fold-form v1] __v156 @ 60:1: applied [fold form: threaded(Path B chunks, simd K-lanes(8) self-dot per chunk); reduce over a deferred computation (one level, Path B)]
- [fold-form v1] __v165 @ 60:1: applied [fold form: threaded(Path B chunks, simd K-lanes(8) self-dot per chunk); reduce over a deferred computation (one level, Path B)]
- [fold-form v1] __v236 @ 76:1: applied [fold form: threaded(Path B joined chunks, 2 builtin + legs, simd-reduction within each chunk); reduction join of 2 legs (one traversal, one team)]
- [omp v1] __v236 @ 76:1: applied [omp parallel num_threads(T) + fixed-order combine; threaded reduction join (Path B joined chunks, G7): one team over the shared traversal, one omp simd reduction(+) over the 2 private accumulators within each chunk; each leg's 

### onepass
- [fold-form v1] __v41 @ 32:1: applied [fold form: threaded(Path B joined chunks, 4 builtin + legs, simd-reduction within each chunk); fused fold over 4 leaves (one traversal, one team)]
- [omp v1] __v41 @ 32:1: applied [omp parallel num_threads(T) + fixed-order combine; threaded reduction join (Path B joined chunks, G7): one team over the shared traversal, one omp simd reduction(+) over the 4 private accumulators within each chunk; each leg's p
- [fold-form v1] __v70: applied [fold form: threaded(Path B joined chunks, 5 builtin + legs, simd-reduction within each chunk); reduction join of 5 legs (one traversal, one team)]
- [omp v1] __v70: applied [omp parallel num_threads(T) + fixed-order combine; threaded reduction join (Path B joined chunks, G7): one team over the shared traversal, one omp simd reduction(+) over the 5 private accumulators within each chunk; each leg's partials

### pairkernel
- [omp v1] _(checksum,input_checksum): declined -- compact row classes: the fold's terms were summed per row inside the producer's nest (its team, when it has one); the row combine is a serial pass over the row partials [[omp] requested but emitted serial]
- [fold-form v1] __v91: applied [fold form: threaded(Path B chunks, simd-reduction within each chunk); reduce over a deferred computation]

### recur
- [rec-step-parallel v1] kernel: applied [the `where omp` request of `lambda in step` (the step's final map) names the fused cell loop: every worker reads only its own cell of strictly earlier rows, so the C++ lane may run the recursion's steps on one persistent
- [cell-epilogue v1] kernel: declined -- the step is threaded (rec-step-parallel): the row chain cannot advance per cell across workers; the row is folded per worker chunk inside the team or on complete rows after it [the row's prodsum runs on the finished row a
- [rec-step-parallel v1] kernel @ 46:1: applied [applied -- persistent team; licensed row partials per worker chunk (self-dot K-lanes), combined ascending per row; time chain in order; the `where omp` request of `lambda in step` (the step's final map)]

### saxpy
- [epilogue-legs v1] _(checksum,input_checksum): declined -- the producer runs a thread team; the licensed legs x-sum, y-sum keep their own pass (v1 builds no per-chunk leg partial: the standalone fold's chunk partition and combine are not shown to be the produc
- [fold-form v1] __v38 @ 25:1: applied [fold form: threaded(Path B chunks, simd K-lanes(8) self-dot per chunk, fed behind the producer's stores); reduce over a deferred computation (rank-1 producer epilogue)]

### stencil
- [fold-form v1] __v50 @ 36:1: applied [fold form: threaded(parallel for simd reduction); reduce over a materialized array (Path A)]
- [fold-form v1] __v77: applied [fold form: threaded(Path B chunks, blade_fold::sum<double, 8> per row: licensed row-view folds, unseeded: head-peeled, row[0] + sum(row[1..n)); no omp simd on the row loop); reduce over a deferred computation (one level, Path B p

### twofir
- [fold-form v1] __v42 @ 35:1: applied [fold form: threaded(Path B chunks, simd K-lanes(8) self-dot per chunk); reduce over a deferred computation (one level, Path B)]
- [fold-form v1] __v51 @ 35:1: applied [fold form: threaded(Path B chunks, simd K-lanes(8) self-dot per chunk); reduce over a deferred computation (one level, Path B)]
- [fold-form v1] __v65 @ 50:1: applied [fold form: threaded(Path B chunks, simd K-lanes(8) sum per chunk); reduce over a deferred computation (one level, Path B)]

