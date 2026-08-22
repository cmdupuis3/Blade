/* k3.c -- multiplicity-weighted triangular fold over Blade packed symmetric pool.
 *
 * Layout (rank 2, extent n, symmetric):
 *   canonical ascending tuples, ascending-lex, contiguous.
 *   rowBase(i) = i*(2n - i + 1)/2 ; row i holds j in [i, n)  -> length n-i
 *   pool size  = n(n+1)/2 ; the FIRST cell of every row is the diagonal (i,i).
 *
 * Layout (rank 2, extent n, antisymmetric): strict, no diagonal.
 *   rowBaseA(i) = i*(2n - i - 1)/2 ; row i holds j in (i, n) -> length n-1-i
 *   pool size   = n(n-1)/2
 *
 * FULL-DOMAIN semantics: reduce(S, op) folds the LOGICAL n^r domain.
 * Class decomposition (rank 2): total = diagPartial (+) offPartial (+) offPartial.
 */
#include <stdio.h>
#include <stdlib.h>
#include <stdint.h>
#include <string.h>
#include <math.h>
#include <immintrin.h>
#include <windows.h>

typedef long long i64;
#define NOINL __attribute__((noinline))

static double now_s(void){
    LARGE_INTEGER f, c;
    QueryPerformanceFrequency(&f);
    QueryPerformanceCounter(&c);
    return (double)c.QuadPart / (double)f.QuadPart;
}

static inline i64 rowBase (i64 i, i64 n){ return i*(2*n - i + 1)/2; }  /* sym,  incl. diagonal */
static inline i64 rowBaseA(i64 i, i64 n){ return i*(2*n - i - 1)/2; }  /* antisym, strict      */

static inline double hsum256(__m256d v){
    __m128d lo = _mm256_castpd256_pd128(v);
    __m128d hi = _mm256_extractf128_pd(v, 1);
    __m128d s  = _mm_add_pd(lo, hi);
    __m128d sh = _mm_unpackhi_pd(s, s);
    return _mm_cvtsd_f64(_mm_add_sd(s, sh));
}
static inline double hmax256(__m256d v){
    __m128d lo = _mm256_castpd256_pd128(v);
    __m128d hi = _mm256_extractf128_pd(v, 1);
    __m128d s  = _mm_max_pd(lo, hi);
    __m128d sh = _mm_unpackhi_pd(s, s);
    return _mm_cvtsd_f64(_mm_max_sd(s, sh));
}

/* ---------------- data ---------------- */
static uint64_t sm64(uint64_t x){
    x += 0x9E3779B97F4A7C15ull;
    x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9ull;
    x = (x ^ (x >> 27)) * 0x94D049BB133111EBull;
    return x ^ (x >> 31);
}
/* small integers 0..7 as doubles: every partial sum stays an exact integer,
 * so ANY reassociation is bitwise identical. That is what makes the bitwise
 * check on (+) meaningful rather than a tolerance fudge. */
static void fill_int(double *p, i64 cells, uint64_t seed){
    for (i64 k = 0; k < cells; k++) p[k] = (double)(sm64(seed + (uint64_t)k) & 7u);
}
/* generic doubles in [0,1): used for max (where reassociation is exact for
 * ANY input) and for the antisym cancellation experiment. */
static void fill_real(double *p, i64 cells, uint64_t seed){
    for (i64 k = 0; k < cells; k++)
        p[k] = (double)(sm64(seed + (uint64_t)k) >> 11) * (1.0/9007199254740992.0);
}

/* ================= (+) SYMMETRIC ================= */

/* REFERENCE: the honest logical fold. Walk the full n x n square in row-major
 * logical order, read S(i,j) canonicalized via min/max. O(n^2), defines the answer. */
NOINL static double ref_sym_add(const double *p, i64 n){
    double acc = 0.0;
    for (i64 i = 0; i < n; i++){
        for (i64 j = 0; j < n; j++){
            i64 a = i < j ? i : j;
            i64 b = i < j ? j : i;
            acc += p[rowBase(a,n) + (b - a)];
        }
    }
    return acc;
}

/* ARM A: naive pool fold. ONE accumulator chain, per-cell weight test.
 * No -ffast-math, so gcc may not split the reduction: this really is a single
 * serial dependency chain of vaddsd, latency ~3-4 cycles per cell. */
NOINL static double armA_sym_add(const double *p, i64 n){
    double acc = 0.0;
    i64 k = 0;
    for (i64 i = 0; i < n; i++){
        for (i64 j = i; j < n; j++){
            double w = (j == i) ? 1.0 : 2.0;   /* per-cell class test */
            acc += w * p[k++];
        }
    }
    return acc;
}

/* ARM B: row-aligned, branch-free class split.
 *   The first cell of each row IS the diagonal -> peel it, no per-cell test.
 *   The rest of the row is entirely off-diagonal -> vector-fold it.
 * NACC YMM accumulators; horizontal reduction only at the very end. */
#define DEF_ARMB_ADD(NAME, NACC)                                              \
NOINL static double NAME(const double * __restrict p, i64 n){                       \
    __m256d a[NACC];                                                          \
    for (int t = 0; t < NACC; t++) a[t] = _mm256_setzero_pd();                \
    double diag = 0.0, offtail = 0.0;                                         \
    i64 base = 0;                                                             \
    const i64 W = 4*(NACC);                                                   \
    for (i64 i = 0; i < n; i++){                                              \
        diag += p[base];                       /* class: multiplicity 1 */    \
        const double * __restrict q = p + base + 1;                           \
        i64 m = n - i - 1, k = 0;              /* class: multiplicity 2 */    \
        for (; k + W <= m; k += W)                                            \
            for (int t = 0; t < NACC; t++)                                    \
                a[t] = _mm256_add_pd(a[t], _mm256_loadu_pd(q + k + 4*t));     \
        for (; k + 4 <= m; k += 4)                                            \
            a[0] = _mm256_add_pd(a[0], _mm256_loadu_pd(q + k));               \
        for (; k < m; k++) offtail += q[k];                                   \
        base += n - i;                                                        \
    }                                                                         \
    __m256d s = a[0];                                                         \
    for (int t = 1; t < NACC; t++) s = _mm256_add_pd(s, a[t]);                \
    double off = hsum256(s) + offtail;                                        \
    return diag + off + off;  /* combine each class multiplicity-many times */\
}
DEF_ARMB_ADD(armB4_sym_add, 4)   /* 4 YMM = 16 lanes, 4 dependency chains */
DEF_ARMB_ADD(armB8_sym_add, 8)   /* 8 YMM = 32 lanes, 8 dependency chains */

/* ARM C: no row structure at all. Fold the WHOLE pool contiguously with 8 YMM
 * accumulators, then correct the diagonal:  total = 2*sumAll - sumDiag
 * Only legal for ops with an inverse; it isolates the cost of the row-aligned
 * split (Arm B) from the cost of the fold itself. */
NOINL static double armC_sym_add(const double * __restrict p, i64 n){
    i64 cells = n*(n+1)/2;
    __m256d a0=_mm256_setzero_pd(),a1=a0,a2=a0,a3=a0,a4=a0,a5=a0,a6=a0,a7=a0;
    i64 k = 0;
    for (; k + 32 <= cells; k += 32){
        a0=_mm256_add_pd(a0,_mm256_loadu_pd(p+k   ));
        a1=_mm256_add_pd(a1,_mm256_loadu_pd(p+k+ 4));
        a2=_mm256_add_pd(a2,_mm256_loadu_pd(p+k+ 8));
        a3=_mm256_add_pd(a3,_mm256_loadu_pd(p+k+12));
        a4=_mm256_add_pd(a4,_mm256_loadu_pd(p+k+16));
        a5=_mm256_add_pd(a5,_mm256_loadu_pd(p+k+20));
        a6=_mm256_add_pd(a6,_mm256_loadu_pd(p+k+24));
        a7=_mm256_add_pd(a7,_mm256_loadu_pd(p+k+28));
    }
    double tail = 0.0;
    for (; k < cells; k++) tail += p[k];
    __m256d s = _mm256_add_pd(_mm256_add_pd(_mm256_add_pd(a0,a1),_mm256_add_pd(a2,a3)),
                              _mm256_add_pd(_mm256_add_pd(a4,a5),_mm256_add_pd(a6,a7)));
    double all = hsum256(s) + tail;
    double diag = 0.0;
    for (i64 i = 0; i < n; i++) diag += p[rowBase(i,n)];
    return all + all - diag;
}

/* ================= max SYMMETRIC ================= */
/* max is IDEMPOTENT: max(x,x)==x, so the multiplicity weights collapse to 1 and
 * the class combine degenerates to max(diagPartial, offPartial).
 * Reassociation is exact for any non-NaN input -> bitwise for real data. */
NOINL static double ref_sym_max(const double *p, i64 n){
    double acc = -INFINITY;
    for (i64 i = 0; i < n; i++)
        for (i64 j = 0; j < n; j++){
            i64 a = i < j ? i : j, b = i < j ? j : i;
            double v = p[rowBase(a,n) + (b - a)];
            if (v > acc) acc = v;
        }
    return acc;
}
NOINL static double armA_sym_max(const double *p, i64 n){
    double acc = -INFINITY;
    i64 k = 0;
    for (i64 i = 0; i < n; i++)
        for (i64 j = i; j < n; j++){
            double v = p[k++];
            (void)j;
            if (v > acc) acc = v;   /* weight irrelevant: idempotent */
        }
    return acc;
}
NOINL static double armB8_sym_max(const double * __restrict p, i64 n){
    __m256d a[8];
    __m256d NEG = _mm256_set1_pd(-INFINITY);
    for (int t=0;t<8;t++) a[t]=NEG;
    double diag = -INFINITY, offtail = -INFINITY;
    i64 base = 0;
    for (i64 i = 0; i < n; i++){
        double d = p[base];
        if (d > diag) diag = d;
        const double * __restrict q = p + base + 1;
        i64 m = n - i - 1, k = 0;
        for (; k + 32 <= m; k += 32)
            for (int t=0;t<8;t++) a[t]=_mm256_max_pd(a[t],_mm256_loadu_pd(q+k+4*t));
        for (; k + 4 <= m; k += 4) a[0]=_mm256_max_pd(a[0],_mm256_loadu_pd(q+k));
        for (; k < m; k++) if (q[k] > offtail) offtail = q[k];
        base += n - i;
    }
    __m256d s = a[0];
    for (int t=1;t<8;t++) s=_mm256_max_pd(s,a[t]);
    double off = hmax256(s);
    if (offtail > off) off = offtail;
    return diag > off ? diag : off;
}

/* ================= (+) ANTISYMMETRIC ================= */
/* Logical L(i,j) = 0 (i==j), +A[..] (i<j), -A[..] (i>j).
 * Full-domain fold must be EXACTLY 0. */
NOINL static double ref_anti_add(const double *p, i64 n){
    double acc = 0.0;
    for (i64 i = 0; i < n; i++)
        for (i64 j = 0; j < n; j++){
            if (i == j) continue;                       /* no stored diagonal */
            if (i < j) acc += p[rowBaseA(i,n) + (j - i - 1)];
            else       acc -= p[rowBaseA(j,n) + (i - j - 1)];
        }
    return acc;
}
/* Arm A: naive pool fold with the sign classes applied per cell. */
NOINL static double armA_anti_add(const double *p, i64 n){
    double acc = 0.0;
    i64 k = 0;
    for (i64 i = 0; i < n; i++)
        for (i64 j = i+1; j < n; j++){ (void)j; acc += p[k]; acc -= p[k]; k++; }
    return acc;
}
/* Arm B: one pool pass into 8 YMM accumulators -> offPartial; the class combine
 * is offPartial + (-offPartial). Structurally exact. The load-bearing check is
 * that offPartial matches an independent strict-upper-triangle reference: that
 * validates rowBaseA addressing. */
NOINL static double armB8_anti_partial(const double * __restrict p, i64 n){
    __m256d a[8]; for (int t=0;t<8;t++) a[t]=_mm256_setzero_pd();
    double tail = 0.0;
    i64 base = 0;
    for (i64 i = 0; i < n; i++){
        const double * __restrict q = p + base;
        i64 m = n - i - 1, k = 0;
        for (; k + 32 <= m; k += 32)
            for (int t=0;t<8;t++) a[t]=_mm256_add_pd(a[t],_mm256_loadu_pd(q+k+4*t));
        for (; k + 4 <= m; k += 4) a[0]=_mm256_add_pd(a[0],_mm256_loadu_pd(q+k));
        for (; k < m; k++) tail += q[k];
        base += n - i - 1;
    }
    __m256d s = a[0]; for (int t=1;t<8;t++) s=_mm256_add_pd(s,a[t]);
    return hsum256(s) + tail;
}
NOINL static double ref_anti_strict(const double *p, i64 n){   /* independent addressing */
    double acc = 0.0;
    for (i64 i = 0; i < n; i++)
        for (i64 j = i+1; j < n; j++)
            acc += p[rowBaseA(i,n) + (j - i - 1)];
    return acc;
}

/* ---------------- harness ---------------- */
static const char* bitsame(double a, double b){
    uint64_t x, y; memcpy(&x,&a,8); memcpy(&y,&b,8);
    return x==y ? "BITWISE" : "DIFFERS";
}
static void bits(double a, char*out){ uint64_t x; memcpy(&x,&a,8); sprintf(out,"%016llx",(unsigned long long)x); }

/* Barriers: the folds are pure and their arguments are loop-invariant, so
 * without these gcc hoists the whole call out of the rep loop and the min
 * over reps collapses to ~0. The memory clobber forces a re-read of the pool;
 * the "+x" constraint keeps the result live. */
#define BARRIER_MEM()   __asm__ __volatile__("" ::: "memory")
#define BARRIER_VAL(v)  __asm__ __volatile__("" : "+x"(v) :: "memory")

typedef double (*fold_fn)(const double*, i64);
static double timeit(fold_fn f, const double *p, i64 n, int reps, double *out){
    double best = 1e30;
    for (int r = 0; r < reps; r++){
        BARRIER_MEM();
        double t0 = now_s();
        double v = f(p, n);
        BARRIER_VAL(v);
        double t1 = now_s();
        *out = v;
        if (t1-t0 < best) best = t1-t0;
    }
    return best;
}
static void row(const char*name, double secs, i64 pool, i64 n, double base_ref, double base_A){
    double logical = (double)n*(double)n;
    printf("  %-22s %9.2f ms  %8.3f ns/pool  %8.3f ns/logical  %7.2f GB/s",
        name, secs*1e3, secs*1e9/(double)pool, secs*1e9/logical,
        (double)pool*8.0/secs/1e9);
    if (base_ref > 0) printf("   %6.2fx vs ref", base_ref/secs);
    if (base_A   > 0) printf("   %6.2fx vs A", base_A/secs);
    printf("\n");
}

static void run_n(i64 n, int reps){
    i64 cells  = n*(n+1)/2;
    i64 cellsA = n*(n-1)/2;
    printf("\n============ n = %lld   pool = %lld cells (%.1f MB)   logical = %lld ============\n",
        n, cells, (double)cells*8.0/1048576.0, n*n);

    double *p = (double*)_mm_malloc((size_t)cells*sizeof(double), 64);
    if (!p){ printf("alloc fail\n"); return; }
    fill_int(p, cells, 0x1234u);

    double vref=0,vA=0,vB4=0,vB8=0,vC=0;
    char b1[32],b2[32];
    printf("\n-- (+) symmetric, exactly-representable integer inputs --\n");
    double tref = timeit(ref_sym_add,   p, n, reps, &vref);
    double tA   = timeit(armA_sym_add,  p, n, reps, &vA);
    double tB4  = timeit(armB4_sym_add, p, n, reps, &vB4);
    double tB8  = timeit(armB8_sym_add, p, n, reps, &vB8);
    double tC   = timeit(armC_sym_add,  p, n, reps, &vC);
    bits(vref,b1);
    printf("  reference total = %.17g  [%s]\n", vref, b1);
    bits(vA,b2);  printf("  Arm A  = %.17g [%s]  %s\n", vA,  b2, bitsame(vref,vA));
    bits(vB4,b2); printf("  Arm B4 = %.17g [%s]  %s\n", vB4, b2, bitsame(vref,vB4));
    bits(vB8,b2); printf("  Arm B8 = %.17g [%s]  %s\n", vB8, b2, bitsame(vref,vB8));
    bits(vC,b2);  printf("  Arm C  = %.17g [%s]  %s\n", vC,  b2, bitsame(vref,vC));
    row("reference (n^2)", tref, cells, n, 0, 0);
    row("Arm A (1 chain)",  tA,  cells, n, tref, 0);
    row("Arm B4 (4 YMM)",   tB4, cells, n, tref, tA);
    row("Arm B8 (8 YMM)",   tB8, cells, n, tref, tA);
    row("Arm C (no rows)",  tC,  cells, n, tref, tA);

    printf("\n-- max symmetric, generic doubles in [0,1) --\n");
    fill_real(p, cells, 0xABCDu);
    double mref=0,mA=0,mB=0;
    double tmref = timeit(ref_sym_max,  p, n, reps, &mref);
    double tmA   = timeit(armA_sym_max, p, n, reps, &mA);
    double tmB   = timeit(armB8_sym_max,p, n, reps, &mB);
    bits(mref,b1); printf("  reference max = %.17g [%s]\n", mref, b1);
    bits(mA,b2); printf("  Arm A  = %.17g [%s]  %s\n", mA, b2, bitsame(mref,mA));
    bits(mB,b2); printf("  Arm B8 = %.17g [%s]  %s\n", mB, b2, bitsame(mref,mB));
    row("reference (n^2)", tmref, cells, n, 0, 0);
    row("Arm A (1 chain)", tmA,   cells, n, tmref, 0);
    row("Arm B8 (8 YMM)",  tmB,   cells, n, tmref, tmA);
    _mm_free(p);

    printf("\n-- (+) ANTISYMMETRIC (strict, no diagonal): pool = %lld cells --\n", cellsA);
    double *q = (double*)_mm_malloc((size_t)cellsA*sizeof(double), 64);
    if (!q){ printf("alloc fail\n"); return; }

    fill_int(q, cellsA, 0x77u);
    double zref = ref_anti_add(q, n);
    double zA   = armA_anti_add(q, n);
    double part = armB8_anti_partial(q, n);
    double pref = ref_anti_strict(q, n);
    double zB   = part + (-part);
    bits(zref,b1); printf("  integer inputs: reference full-domain = %.17g [%s]  exactly-zero=%s\n",
        zref, b1, zref==0.0 ? "YES" : "NO");
    printf("                  Arm A = %.17g   Arm B (class combine) = %.17g\n", zA, zB);
    bits(part,b1); bits(pref,b2);
    printf("  pool-partial check (validates rowBaseA addressing):\n");
    printf("      ArmB8 offPartial = %.17g [%s]\n      strict-tri ref   = %.17g [%s]   %s\n",
        part, b1, pref, b2, bitsame(pref,part));

    fill_real(q, cellsA, 0x99u);
    double zref2 = ref_anti_add(q, n);
    double part2 = armB8_anti_partial(q, n);
    bits(zref2,b1);
    printf("  generic doubles: reference full-domain = %.17g [%s]  exactly-zero=%s\n",
        zref2, b1, zref2==0.0 ? "YES" : "NO");
    printf("                   |ref| / |offPartial| = %.3e   (class combine is exactly 0 by construction)\n",
        part2 != 0.0 ? fabs(zref2)/fabs(part2) : 0.0);

    double tzref = 0, tzB = 0, dummy;
    { double t0=now_s(); for(int r=0;r<reps;r++){ BARRIER_MEM(); dummy = ref_anti_add(q,n); BARRIER_VAL(dummy); } double t1=now_s(); tzref=(t1-t0)/reps; }
    { double t0=now_s(); for(int r=0;r<reps;r++){ BARRIER_MEM(); dummy = armB8_anti_partial(q,n); BARRIER_VAL(dummy); } double t1=now_s(); tzB=(t1-t0)/reps; }
    row("anti reference (n^2)", tzref, cellsA, n, 0, 0);
    row("anti Arm B8 (pool)",   tzB,   cellsA, n, tzref, 0);
    _mm_free(q);
}

int main(int argc, char**argv){
    printf("k3: multiplicity-weighted triangular fold over Blade packed-symmetric storage\n");
    i64 ns[3] = {701, 2003, 6007};
    int reps[3] = {7, 5, 3};
    int lo = 0, hi = 3;
    if (argc > 1){ lo = atoi(argv[1]); hi = lo+1; }
    for (int t = lo; t < hi; t++) run_n(ns[t], reps[t]);
    return 0;
}
