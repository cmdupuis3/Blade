/* k4: packed symmetric matrix-vector product (fused dot + axpy)
 *
 * Blade packed rank-2 symmetric layout, extent n:
 *   C(n+1,2) cells, ascending-lex.  Row i starts at rowBase(i)=i*(2n-i+1)/2
 *   and covers j in [i,n) contiguously; first cell of the row is the diagonal.
 *
 * y = S*x with S symmetric.  Each STORED cell is read once, used twice:
 *   y[i] += S(i,j)*x[j]      (reduction over the row -> needs many accumulators)
 *   y[j] += S(i,j)*x[i]      (axpy over the row, x[i] broadcast)
 * Diagonal contributes once.  Trick used everywhere below: run BOTH halves over
 * the full row [i,n) (so the two streams span the same contiguous range) and then
 * remove the double-counted diagonal with  y[i] -= S(i,i)*x[i].
 */
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <stdint.h>
#include <math.h>
#include <time.h>
#include <immintrin.h>

#if defined(_WIN32)
#include <malloc.h>
#define XALLOC(sz)  _aligned_malloc((sz), 64)
#define XFREE(p)    _aligned_free((p))
#else
#define XALLOC(sz)  aligned_alloc(64, (((sz)+63)/64)*64)
#define XFREE(p)    free((p))
#endif

static inline size_t rowBase(size_t i, size_t n) { return i * (2 * n - i + 1) / 2; }

static double now_s(void) {
    struct timespec ts;
    clock_gettime(CLOCK_MONOTONIC, &ts);
    return (double)ts.tv_sec + 1e-9 * (double)ts.tv_nsec;
}

static inline double hsum256(__m256d v) {
    __m128d lo = _mm256_castpd256_pd128(v);
    __m128d hi = _mm256_extractf128_pd(v, 1);
    lo = _mm_add_pd(lo, hi);
    __m128d s = _mm_add_sd(lo, _mm_unpackhi_pd(lo, lo));
    return _mm_cvtsd_f64(s);
}

/* ------------------------------------------------------------------ *
 * 0. REFERENCE: dense n x n symmetric matrix, plain y = M*x, scalar.
 * ------------------------------------------------------------------ */
void dense_ref(const double *M, const double *x, double *y, size_t n)
{
    for (size_t i = 0; i < n; ++i) {
        const double *r = M + i * n;
        double s = 0.0;
        for (size_t j = 0; j < n; ++j) s += r[j] * x[j];
        y[i] = s;
    }
}

/* ------------------------------------------------------------------ *
 * 0b. DENSE, OPTIMIZED: 4 YMM accumulators per row (fair timing rival).
 *     Reads n*n cells; 1 FMA per cell.
 * ------------------------------------------------------------------ */
void dense_opt(const double *M, const double *x, double *y, size_t n)
{
    for (size_t i = 0; i < n; ++i) {
        const double *r = M + i * n;
        __m256d a0 = _mm256_setzero_pd(), a1 = _mm256_setzero_pd();
        __m256d a2 = _mm256_setzero_pd(), a3 = _mm256_setzero_pd();
        size_t j = 0;
        for (; j + 16 <= n; j += 16) {
            a0 = _mm256_fmadd_pd(_mm256_loadu_pd(r + j),      _mm256_loadu_pd(x + j),      a0);
            a1 = _mm256_fmadd_pd(_mm256_loadu_pd(r + j + 4),  _mm256_loadu_pd(x + j + 4),  a1);
            a2 = _mm256_fmadd_pd(_mm256_loadu_pd(r + j + 8),  _mm256_loadu_pd(x + j + 8),  a2);
            a3 = _mm256_fmadd_pd(_mm256_loadu_pd(r + j + 12), _mm256_loadu_pd(x + j + 12), a3);
        }
        for (; j + 4 <= n; j += 4)
            a0 = _mm256_fmadd_pd(_mm256_loadu_pd(r + j), _mm256_loadu_pd(x + j), a0);
        double s = hsum256(_mm256_add_pd(_mm256_add_pd(a0, a1), _mm256_add_pd(a2, a3)));
        for (; j < n; ++j) s += r[j] * x[j];
        y[i] = s;
    }
}

/* ------------------------------------------------------------------ *
 * ARM A: packed, naive.  Scalar, ONE accumulator chain for the dot half.
 * ------------------------------------------------------------------ */
void symv_packed_A(const double *S, const double *x, double *y, size_t n)
{
    for (size_t i = 0; i < n; ++i) {
        const double *p = S + rowBase(i, n);
        const double *xr = x + i;
        double *yr = y + i;
        size_t len = n - i;
        double xi = x[i];
        double acc = 0.0;                    /* single chain: 4-cycle FMA latency */
        for (size_t k = 0; k < len; ++k) {
            double s = p[k];
            acc  += s * xr[k];
            yr[k] += s * xi;
        }
        yr[0] += acc - p[0] * xi;            /* undo double-counted diagonal */
    }
}

/* ------------------------------------------------------------------ *
 * ARM B: fused dot+axpy, 4 YMM accumulators, S_row loaded ONCE per cell
 *        and consumed by both halves.
 *
 * Per 16-double step: 4 loads of S; 4 FMAs into a0..a3 (x from a memory
 * operand); 4 loads of y, 4 FMAs with the x[i] broadcast, 4 stores.
 * Live: a0..a3 (4) + vxi (1) + s0..s3 (4) + y transient (<=4) = <=13 YMM.
 * ------------------------------------------------------------------ */
void symv_packed_B(const double *S, const double *x, double *y, size_t n)
{
    for (size_t i = 0; i < n; ++i) {
        const double *p  = S + rowBase(i, n);
        const double *xr = x + i;
        double *yr = y + i;
        size_t len = n - i;
        const double xi = x[i];
        const __m256d vxi = _mm256_set1_pd(xi);
        __m256d a0 = _mm256_setzero_pd(), a1 = _mm256_setzero_pd();
        __m256d a2 = _mm256_setzero_pd(), a3 = _mm256_setzero_pd();
        size_t k = 0;
        for (; k + 16 <= len; k += 16) {
            __m256d s0 = _mm256_loadu_pd(p + k);
            __m256d s1 = _mm256_loadu_pd(p + k + 4);
            __m256d s2 = _mm256_loadu_pd(p + k + 8);
            __m256d s3 = _mm256_loadu_pd(p + k + 12);
            a0 = _mm256_fmadd_pd(s0, _mm256_loadu_pd(xr + k),      a0);
            a1 = _mm256_fmadd_pd(s1, _mm256_loadu_pd(xr + k + 4),  a1);
            a2 = _mm256_fmadd_pd(s2, _mm256_loadu_pd(xr + k + 8),  a2);
            a3 = _mm256_fmadd_pd(s3, _mm256_loadu_pd(xr + k + 12), a3);
            _mm256_storeu_pd(yr + k,      _mm256_fmadd_pd(s0, vxi, _mm256_loadu_pd(yr + k)));
            _mm256_storeu_pd(yr + k + 4,  _mm256_fmadd_pd(s1, vxi, _mm256_loadu_pd(yr + k + 4)));
            _mm256_storeu_pd(yr + k + 8,  _mm256_fmadd_pd(s2, vxi, _mm256_loadu_pd(yr + k + 8)));
            _mm256_storeu_pd(yr + k + 12, _mm256_fmadd_pd(s3, vxi, _mm256_loadu_pd(yr + k + 12)));
        }
        for (; k + 4 <= len; k += 4) {
            __m256d s0 = _mm256_loadu_pd(p + k);
            a0 = _mm256_fmadd_pd(s0, _mm256_loadu_pd(xr + k), a0);
            _mm256_storeu_pd(yr + k, _mm256_fmadd_pd(s0, vxi, _mm256_loadu_pd(yr + k)));
        }
        double dot = hsum256(_mm256_add_pd(_mm256_add_pd(a0, a1), _mm256_add_pd(a2, a3)));
        for (; k < len; ++k) { double s = p[k]; dot += s * xr[k]; yr[k] += s * xi; }
        yr[0] += dot - p[0] * xi;
    }
}

/* ------------------------------------------------------------------ *
 * ARM B8: same shape, 8 YMM accumulators (32 doubles/step) for the dot
 *         half -- 8 independent FMA chains, the depth this machine wants.
 * ------------------------------------------------------------------ */
void symv_packed_B8(const double *S, const double *x, double *y, size_t n)
{
    for (size_t i = 0; i < n; ++i) {
        const double *p  = S + rowBase(i, n);
        const double *xr = x + i;
        double *yr = y + i;
        size_t len = n - i;
        const double xi = x[i];
        const __m256d vxi = _mm256_set1_pd(xi);
        __m256d a[8];
        for (int t = 0; t < 8; ++t) a[t] = _mm256_setzero_pd();
        size_t k = 0;
        for (; k + 32 <= len; k += 32) {
            for (int t = 0; t < 8; ++t) {
                __m256d s = _mm256_loadu_pd(p + k + 4 * t);
                a[t] = _mm256_fmadd_pd(s, _mm256_loadu_pd(xr + k + 4 * t), a[t]);
                _mm256_storeu_pd(yr + k + 4 * t,
                                 _mm256_fmadd_pd(s, vxi, _mm256_loadu_pd(yr + k + 4 * t)));
            }
        }
        for (; k + 4 <= len; k += 4) {
            __m256d s = _mm256_loadu_pd(p + k);
            a[0] = _mm256_fmadd_pd(s, _mm256_loadu_pd(xr + k), a[0]);
            _mm256_storeu_pd(yr + k, _mm256_fmadd_pd(s, vxi, _mm256_loadu_pd(yr + k)));
        }
        __m256d t0 = _mm256_add_pd(_mm256_add_pd(a[0], a[1]), _mm256_add_pd(a[2], a[3]));
        __m256d t1 = _mm256_add_pd(_mm256_add_pd(a[4], a[5]), _mm256_add_pd(a[6], a[7]));
        double dot = hsum256(_mm256_add_pd(t0, t1));
        for (; k < len; ++k) { double s = p[k]; dot += s * xr[k]; yr[k] += s * xi; }
        yr[0] += dot - p[0] * xi;
    }
}

/* one packed row, 2 accumulators -- used for the tail of the blocked arms */
static void symv_row_single(const double *S, const double *x, double *y, size_t n, size_t i)
{
    const double *p  = S + rowBase(i, n);
    const double *xr = x + i;
    double *yr = y + i;
    size_t len = n - i;
    const double xi = x[i];
    const __m256d vxi = _mm256_set1_pd(xi);
    __m256d a0 = _mm256_setzero_pd(), a1 = _mm256_setzero_pd();
    size_t k = 0;
    for (; k + 8 <= len; k += 8) {
        __m256d s0 = _mm256_loadu_pd(p + k), s1 = _mm256_loadu_pd(p + k + 4);
        a0 = _mm256_fmadd_pd(s0, _mm256_loadu_pd(xr + k), a0);
        a1 = _mm256_fmadd_pd(s1, _mm256_loadu_pd(xr + k + 4), a1);
        _mm256_storeu_pd(yr + k,     _mm256_fmadd_pd(s0, vxi, _mm256_loadu_pd(yr + k)));
        _mm256_storeu_pd(yr + k + 4, _mm256_fmadd_pd(s1, vxi, _mm256_loadu_pd(yr + k + 4)));
    }
    double dot = hsum256(_mm256_add_pd(a0, a1));
    for (; k < len; ++k) { double s = p[k]; dot += s * xr[k]; yr[k] += s * xi; }
    yr[0] += dot - p[0] * xi;
}

/* ------------------------------------------------------------------ *
 * ARM B2: two packed rows at a time.  Rows i and i+1 overlap on j in
 * [i+1,n): one read-modify-write of y[j] serves both axpy halves.
 * 4+4 = 8 accumulator chains; 2 broadcasts; 2 S streams.
 * Bookkeeping: the j-loop starts at i+1, so row i diagonal is OUTSIDE
 * the loop (add it) and row i+1 diagonal is the loop first cell (subtract).
 * ------------------------------------------------------------------ */
void symv_packed_B2(const double *S, const double *x, double *y, size_t n)
{
    size_t i = 0;
    for (; i + 2 <= n; i += 2) {
        const double *p0 = S + rowBase(i, n);
        const double *p1 = S + rowBase(i + 1, n);
        const double xi0 = x[i], xi1 = x[i + 1];
        const __m256d v0 = _mm256_set1_pd(xi0), v1 = _mm256_set1_pd(xi1);
        const double *q0 = p0 + 1;              /* row i,   j starting at i+1 */
        const double *q1 = p1;                  /* row i+1, j starting at i+1 */
        const double *xr = x + i + 1;
        double *yr = y + i + 1;
        size_t len = n - (i + 1);
        __m256d a0 = _mm256_setzero_pd(), a1 = _mm256_setzero_pd();
        __m256d a2 = _mm256_setzero_pd(), a3 = _mm256_setzero_pd();
        __m256d b0 = _mm256_setzero_pd(), b1 = _mm256_setzero_pd();
        __m256d b2 = _mm256_setzero_pd(), b3 = _mm256_setzero_pd();
        size_t k = 0;
        for (; k + 16 <= len; k += 16) {
            __m256d s0 = _mm256_loadu_pd(q0 + k),      t0 = _mm256_loadu_pd(q1 + k);
            __m256d s1 = _mm256_loadu_pd(q0 + k + 4),  t1 = _mm256_loadu_pd(q1 + k + 4);
            __m256d xv0 = _mm256_loadu_pd(xr + k), xv1 = _mm256_loadu_pd(xr + k + 4);
            a0 = _mm256_fmadd_pd(s0, xv0, a0);  b0 = _mm256_fmadd_pd(t0, xv0, b0);
            a1 = _mm256_fmadd_pd(s1, xv1, a1);  b1 = _mm256_fmadd_pd(t1, xv1, b1);
            __m256d y0 = _mm256_loadu_pd(yr + k), y1 = _mm256_loadu_pd(yr + k + 4);
            y0 = _mm256_fmadd_pd(s0, v0, y0);  y0 = _mm256_fmadd_pd(t0, v1, y0);
            y1 = _mm256_fmadd_pd(s1, v0, y1);  y1 = _mm256_fmadd_pd(t1, v1, y1);
            _mm256_storeu_pd(yr + k, y0);  _mm256_storeu_pd(yr + k + 4, y1);

            __m256d s2 = _mm256_loadu_pd(q0 + k + 8),  t2 = _mm256_loadu_pd(q1 + k + 8);
            __m256d s3 = _mm256_loadu_pd(q0 + k + 12), t3 = _mm256_loadu_pd(q1 + k + 12);
            __m256d xv2 = _mm256_loadu_pd(xr + k + 8), xv3 = _mm256_loadu_pd(xr + k + 12);
            a2 = _mm256_fmadd_pd(s2, xv2, a2);  b2 = _mm256_fmadd_pd(t2, xv2, b2);
            a3 = _mm256_fmadd_pd(s3, xv3, a3);  b3 = _mm256_fmadd_pd(t3, xv3, b3);
            __m256d y2 = _mm256_loadu_pd(yr + k + 8), y3 = _mm256_loadu_pd(yr + k + 12);
            y2 = _mm256_fmadd_pd(s2, v0, y2);  y2 = _mm256_fmadd_pd(t2, v1, y2);
            y3 = _mm256_fmadd_pd(s3, v0, y3);  y3 = _mm256_fmadd_pd(t3, v1, y3);
            _mm256_storeu_pd(yr + k + 8, y2);  _mm256_storeu_pd(yr + k + 12, y3);
        }
        double d0 = hsum256(_mm256_add_pd(_mm256_add_pd(a0, a1), _mm256_add_pd(a2, a3)));
        double d1 = hsum256(_mm256_add_pd(_mm256_add_pd(b0, b1), _mm256_add_pd(b2, b3)));
        for (; k < len; ++k) {
            double s = q0[k], t = q1[k];
            d0 += s * xr[k];
            d1 += t * xr[k];
            yr[k] += s * xi0 + t * xi1;
        }
        y[i]     += d0 + p0[0] * xi0;   /* row i diagonal was outside the loop */
        y[i + 1] += d1 - p1[0] * xi1;   /* row i+1 diagonal was double-counted */
    }
    for (; i < n; ++i) symv_row_single(S, x, y, n, i);
}

/* ------------------------------------------------------------------ *
 * ARM B4: four packed rows at a time (2 accumulator chains per row = 8).
 * y[j] read-modify-written once per 4 rows instead of 4 times.
 * ------------------------------------------------------------------ */
void symv_packed_B4(const double *S, const double *x, double *y, size_t n)
{
    size_t i = 0;
    for (; i + 4 <= n; i += 4) {
        const double *p[4];
        double xi[4];
        __m256d vx[4];
        for (int r = 0; r < 4; ++r) {
            p[r] = S + rowBase(i + r, n);
            xi[r] = x[i + r];
            vx[r] = _mm256_set1_pd(xi[r]);
        }
        /* main span: j in [i+3, n).  Row i+r contributes from offset (i+3)-(i+r) = 3-r. */
        const double *q[4];
        for (int r = 0; r < 4; ++r) q[r] = p[r] + (3 - r);
        const double *xr = x + i + 3;
        double *yr = y + i + 3;
        size_t len = n - (i + 3);
        __m256d a[8];
        for (int t = 0; t < 8; ++t) a[t] = _mm256_setzero_pd();
        size_t k = 0;
        for (; k + 8 <= len; k += 8) {
            __m256d xv0 = _mm256_loadu_pd(xr + k), xv1 = _mm256_loadu_pd(xr + k + 4);
            __m256d y0 = _mm256_loadu_pd(yr + k), y1 = _mm256_loadu_pd(yr + k + 4);
            for (int r = 0; r < 4; ++r) {
                __m256d s0 = _mm256_loadu_pd(q[r] + k);
                __m256d s1 = _mm256_loadu_pd(q[r] + k + 4);
                a[2 * r]     = _mm256_fmadd_pd(s0, xv0, a[2 * r]);
                a[2 * r + 1] = _mm256_fmadd_pd(s1, xv1, a[2 * r + 1]);
                y0 = _mm256_fmadd_pd(s0, vx[r], y0);
                y1 = _mm256_fmadd_pd(s1, vx[r], y1);
            }
            _mm256_storeu_pd(yr + k, y0);
            _mm256_storeu_pd(yr + k + 4, y1);
        }
        double d[4];
        for (int r = 0; r < 4; ++r) d[r] = hsum256(_mm256_add_pd(a[2 * r], a[2 * r + 1]));
        for (; k < len; ++k) {
            double acc = 0.0;
            for (int r = 0; r < 4; ++r) {
                double s = q[r][k];
                d[r] += s * xr[k];
                acc  += s * xi[r];
            }
            yr[k] += acc;
        }
        /* head triangle: cells with j in [i, i+3), not covered by the main span */
        for (int r = 0; r < 4; ++r) {
            double dd = d[r];
            for (size_t j = i + r; j < i + 3; ++j) {
                double s = p[r][j - (i + r)];
                dd += s * x[j];
                if (j > i + r) y[j] += s * xi[r];
            }
            y[i + r] += dd;
        }
        /* the main span starts at j=i+3, where row i+3 diagonal sits; its own
           axpy pass double-counted it. */
        y[i + 3] -= p[3][0] * xi[3];
    }
    for (; i < n; ++i) symv_row_single(S, x, y, n, i);
}

/* ================================================================== *
 * Harness
 * ================================================================== */
static uint64_t rng_s = 0x243F6A8885A308D3ull;
static inline uint64_t rnd(void) {
    rng_s ^= rng_s << 13; rng_s ^= rng_s >> 7; rng_s ^= rng_s << 17; return rng_s;
}

typedef void (*packed_fn)(const double *, const double *, double *, size_t);
typedef void (*dense_fn)(const double *, const double *, double *, size_t);

static void fill_int(double *S, size_t cells, double *x, size_t n) {
    rng_s = 0x243F6A8885A308D3ull;
    for (size_t t = 0; t < cells; ++t) S[t] = (double)((int)(rnd() % 9) - 4);
    for (size_t j = 0; j < n; ++j)     x[j] = (double)((int)(rnd() % 9) - 4);
}

static void expand(const double *S, double *M, size_t n) {
    for (size_t i = 0; i < n; ++i) {
        const double *p = S + rowBase(i, n);
        for (size_t j = i; j < n; ++j) { M[i * n + j] = p[j - i]; M[j * n + i] = p[j - i]; }
    }
}

int main(int argc, char **argv)
{
    size_t n = (argc > 1) ? (size_t)strtoull(argv[1], NULL, 10) : 2003;
    int reps = (argc > 2) ? atoi(argv[2]) : 5;
    int do_dense = (argc > 3) ? atoi(argv[3]) : 1;

    size_t cells = n * (n + 1) / 2;
    printf("n=%zu  packed cells=%zu (%.1f MB)  dense=%.1f MB  reps=%d\n",
           n, cells, (double)cells * 8.0 / 1048576.0,
           (double)n * (double)n * 8.0 / 1048576.0, reps);

    double *S = (double *)XALLOC(cells * sizeof(double));
    double *x = (double *)XALLOC(n * sizeof(double));
    double *yref = (double *)XALLOC(n * sizeof(double));
    double *yt = (double *)XALLOC(n * sizeof(double));
    double *M = do_dense ? (double *)XALLOC(n * n * sizeof(double)) : NULL;
    if (!S || !x || !yref || !yt || (do_dense && !M)) { printf("ALLOC FAILED\n"); return 2; }

    struct { const char *name; packed_fn f; } arms[] = {
        { "A  packed naive (1 acc)", symv_packed_A },
        { "B  packed fused 4-acc",   symv_packed_B },
        { "B8 packed fused 8-acc",   symv_packed_B8 },
        { "B2 packed 2-row block",   symv_packed_B2 },
        { "B4 packed 4-row block",   symv_packed_B4 },
    };
    const int NA = (int)(sizeof(arms) / sizeof(arms[0]));

    /* -------- correctness pass 1: exactly-representable small integers.
       |S|<=4, |x|<=4, n<=8192 -> every partial sum is an integer < 2^53, so
       every association order gives BITWISE identical results. -------- */
    fill_int(S, cells, x, n);
    if (do_dense) { expand(S, M, n); dense_ref(M, x, yref, n); }
    else { memset(yref, 0, n * sizeof(double)); symv_packed_A(S, x, yref, n); }

    printf("\n-- correctness (integer inputs, BITWISE vs dense reference) --\n");
    int allok = 1;
    for (int a = 0; a < NA; ++a) {
        memset(yt, 0, n * sizeof(double));
        arms[a].f(S, x, yt, n);
        int bad = 0; size_t firstbad = 0;
        for (size_t i = 0; i < n; ++i)
            if (memcmp(&yt[i], &yref[i], sizeof(double)) != 0) { if (!bad) firstbad = i; ++bad; }
        printf("  %-24s : %s", arms[a].name, bad ? "BITWISE MISMATCH" : "bitwise exact");
        if (bad) { printf("  (%d of %zu differ; first i=%zu got %.17g want %.17g)",
                          bad, n, firstbad, yt[firstbad], yref[firstbad]); allok = 0; }
        printf("\n");
    }
    if (do_dense) {
        memset(yt, 0, n * sizeof(double));
        dense_opt(M, x, yt, n);
        int bad = 0;
        for (size_t i = 0; i < n; ++i) if (memcmp(&yt[i], &yref[i], sizeof(double))) ++bad;
        printf("  %-24s : %s\n", "dense_opt (4 acc)", bad ? "BITWISE MISMATCH" : "bitwise exact");
        if (bad) allok = 0;
    }

    /* -------- correctness pass 2: random doubles, relative error -------- */
    for (size_t t = 0; t < cells; ++t) S[t] = (double)(int64_t)(rnd() >> 11) / 9.007199254740992e15;
    for (size_t j = 0; j < n; ++j)     x[j] = (double)(int64_t)(rnd() >> 11) / 9.007199254740992e15;
    if (do_dense) { expand(S, M, n); dense_ref(M, x, yref, n); }
    else { memset(yref, 0, n * sizeof(double)); symv_packed_A(S, x, yref, n); }
    double nrm = 0.0;
    for (size_t i = 0; i < n; ++i) nrm = fmax(nrm, fabs(yref[i]));
    printf("\n-- correctness (random doubles in [-1,1], ||y||inf=%.4g) --\n", nrm);
    for (int a = 0; a < NA; ++a) {
        memset(yt, 0, n * sizeof(double));
        arms[a].f(S, x, yt, n);
        double me = 0.0;
        for (size_t i = 0; i < n; ++i) me = fmax(me, fabs(yt[i] - yref[i]));
        printf("  %-24s : max abs err %.3e  -> rel %.3e %s\n",
               arms[a].name, me, me / nrm, (me / nrm > 1e-13) ? "  ** LOOSE **" : "");
        if (me / nrm > 1e-13) allok = 0;
    }

    /* -------- timing -------- */
    fill_int(S, cells, x, n);
    if (do_dense) expand(S, M, n);

    printf("\n-- timing (min of %d; ns/cell over PACKED cells; GB/s counts matrix bytes) --\n", reps);
    printf("  %-24s %10s %10s %10s %10s\n", "arm", "ms", "ns/cell", "GB/s", "GFLOP/s");
    double t[8], bestB = 1e30;
    for (int a = 0; a < NA; ++a) {
        double best = 1e30;
        for (int r = 0; r < reps; ++r) {
            memset(yt, 0, n * sizeof(double));
            double t0 = now_s();
            arms[a].f(S, x, yt, n);
            double t1 = now_s();
            if (t1 - t0 < best) best = t1 - t0;
        }
        t[a] = best;
        printf("  %-24s %10.3f %10.3f %10.2f %10.2f\n", arms[a].name, best * 1e3,
               best * 1e9 / (double)cells,
               (double)cells * 8.0 / best / 1e9,
               (double)(4 * cells) / best / 1e9);   /* 2 FMA = 4 flop per stored cell */
        if (a > 0 && best < bestB) bestB = best;
    }
    if (do_dense) {
        struct { const char *name; dense_fn f; } d[] = {
            { "dense_ref (scalar)", dense_ref }, { "dense_opt (4 acc)", dense_opt } };
        double bestDense = 1e30;
        for (int a = 0; a < 2; ++a) {
            double best = 1e30;
            for (int r = 0; r < reps; ++r) {
                double t0 = now_s(); d[a].f(M, x, yt, n); double t1 = now_s();
                if (t1 - t0 < best) best = t1 - t0;
            }
            printf("  %-24s %10.3f %10.3f %10.2f %10.2f\n", d[a].name, best * 1e3,
                   best * 1e9 / (double)cells,
                   (double)n * (double)n * 8.0 / best / 1e9,
                   (double)(2 * n * n) / best / 1e9);
            if (a == 1) bestDense = best;
        }
        printf("\n  best packed vs dense_opt : time ratio %.3f  -> packed is %.2fx faster\n",
               bestB / bestDense, bestDense / bestB);
    }
    printf("  arm A vs best packed     : %.2fx\n", t[0] / bestB);
    printf("  RESULT: %s\n", allok ? "ALL ARMS CORRECT" : "SOME ARM WRONG");

    XFREE(S); XFREE(x); XFREE(yref); XFREE(yt); if (M) XFREE(M);
    return allok ? 0 : 1;
}
