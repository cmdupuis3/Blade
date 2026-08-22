/* single-core memory bandwidth roof, for calibrating the symv result.
   read-only sum with 8 YMM accumulators == the same access shape as the
   packed S stream. */
#include <stdio.h>
#include <stdlib.h>
#include <time.h>
#include <stdint.h>
#include <immintrin.h>
#include <malloc.h>

static double now_s(void) {
    struct timespec ts; clock_gettime(CLOCK_MONOTONIC, &ts);
    return (double)ts.tv_sec + 1e-9 * (double)ts.tv_nsec;
}
static inline double hsum256(__m256d v) {
    __m128d lo = _mm256_castpd256_pd128(v), hi = _mm256_extractf128_pd(v, 1);
    lo = _mm_add_pd(lo, hi);
    return _mm_cvtsd_f64(_mm_add_sd(lo, _mm_unpackhi_pd(lo, lo)));
}

int main(int argc, char **argv)
{
    size_t nd = (argc > 1) ? (size_t)strtoull(argv[1], NULL, 10) : (18045028u);
    int reps = (argc > 2) ? atoi(argv[2]) : 4;
    double *a = (double *)_aligned_malloc(nd * sizeof(double), 64);
    double *b = (double *)_aligned_malloc(nd * sizeof(double), 64);
    if (!a || !b) { printf("alloc failed\n"); return 2; }
    for (size_t i = 0; i < nd; ++i) { a[i] = (double)(i & 7); b[i] = 0.0; }
    double bytes = (double)nd * 8.0;
    printf("buffer %.1f MB, reps %d\n", bytes / 1048576.0, reps);

    double best = 1e30, s = 0;
    for (int r = 0; r < reps; ++r) {
        double t0 = now_s();
        __m256d acc[8];
        for (int t = 0; t < 8; ++t) acc[t] = _mm256_setzero_pd();
        size_t i = 0;
        for (; i + 32 <= nd; i += 32)
            for (int t = 0; t < 8; ++t)
                acc[t] = _mm256_add_pd(acc[t], _mm256_loadu_pd(a + i + 4 * t));
        __m256d z = _mm256_add_pd(_mm256_add_pd(_mm256_add_pd(acc[0], acc[1]),
                                                _mm256_add_pd(acc[2], acc[3])),
                                  _mm256_add_pd(_mm256_add_pd(acc[4], acc[5]),
                                                _mm256_add_pd(acc[6], acc[7])));
        double v = hsum256(z);
        for (; i < nd; ++i) v += a[i];
        double t1 = now_s();
        s += v;
        if (t1 - t0 < best) best = t1 - t0;
    }
    printf("  pure READ            : %8.3f ms  %8.2f GB/s\n", best * 1e3, bytes / best / 1e9);

    /* read A + read/modify/write B -- the shape of the fused symv row */
    best = 1e30;
    for (int r = 0; r < reps; ++r) {
        double t0 = now_s();
        const __m256d k = _mm256_set1_pd(1.0000001);
        for (size_t i = 0; i + 4 <= nd; i += 4)
            _mm256_storeu_pd(b + i, _mm256_fmadd_pd(_mm256_loadu_pd(a + i), k,
                                                    _mm256_loadu_pd(b + i)));
        double t1 = now_s();
        if (t1 - t0 < best) best = t1 - t0;
    }
    printf("  READ + RMW (3 streams): %8.3f ms  %8.2f GB/s\n", best * 1e3,
           3.0 * bytes / best / 1e9);
    printf("  (sink %.1f)\n", s == 1.5 ? s : 0.0);
    _aligned_free(a); _aligned_free(b);
    return 0;
}
