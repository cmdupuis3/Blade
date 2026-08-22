/* ujam.c -- unroll-and-jam the OUTPUT axis around an in-nest fold.
 *
 * Driver: C[i][j] = sum_t A[t][i] * B[t][j],  A is d x m, B is d x n, C is m x n.
 * (A == B gives the gram/syrk shape; output is DENSE -- this is not the packed former.)
 *
 * TWO orthogonal axes are under test:
 *   SCHEDULE     : ref (one scalar acc)  |  jam-j  |  jam-i-x-j  |  fold-split
 *   CONTRACTION  : mul+add (2 roundings) |  fma (1 rounding)
 * They must be separated, because on znver3 gcc's AVOID_256FMA_CHAINS tuning
 * refuses to contract the SINGLE-accumulator reduction (it emits vmulsd+vaddsd
 * even at -ffp-contract=fast) but happily contracts the fold-split form.  An
 * emitter that hand-writes FMA into the jammed kernel would therefore change the
 * result bits WITHOUT reassociating anything.  So this file compiles with
 * -ffp-contract=off and every arm's contraction is explicit.
 *
 * Arms (m = mul+add, f = fused):
 *   ref   : the shape Blade emits -- 2-level nest over (i,j), innermost loop over t
 *           with ONE scalar accumulator ("double __ps = 0; for (__pt) __ps += a*b;").
 *   refF  : same schedule, fused.  The reference for the fused arms.
 *   A4/A8 : jam over j only, R_j = 4 / 8.  Shared broadcast of A[t][i], vector loads
 *           of B[t][j..j+R).  BITWISE vs ref.
 *   B28   : jam 2 x 8 register tile.   BITWISE vs ref.
 *   B48   : jam 4 x 8 register tile.   BITWISE vs ref.
 *   A8f   : jam j x8, fused.           BITWISE vs refF.
 *   B48f  : jam 4 x 8, fused.          BITWISE vs refF.
 *   C4/C8 : the LICENCE arm -- multi-accumulator applied to the FOLD, S = 4 / 8.
 *           REASSOCIATES; bitwise vs nothing.
 *   C8f   : fold split, fused (what gcc actually produces from C-style source).
 *
 * Every arm computes the same set of (i,j,t) products.  A/B keep each cell's
 * addition order (ascending t, one chain per cell); C/D do not.
 *
 * CLI:  ./u1 peak
 *       ./u1 verify <m> <n> <d>          (small-int inputs AND random doubles)
 *       ./u1 bench  <m> <n> <d> <reps>
 *       ./u1 dot    <d> <reps>
 */
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <math.h>
#include <time.h>
#include <stdint.h>
#include <immintrin.h>

static double now(void){ struct timespec ts; clock_gettime(CLOCK_MONOTONIC,&ts);
                         return (double)ts.tv_sec + 1e-9*(double)ts.tv_nsec; }
#define BARRIER() __asm__ __volatile__("" ::: "memory")

#define MADD(a,b,c) _mm256_add_pd(_mm256_mul_pd((a),(b)), (c))   /* 2 roundings */
#define FMADD(a,b,c) _mm256_fmadd_pd((a),(b),(c))                /* 1 rounding  */

/* ---------------------------------------------------------------- reference */
__attribute__((noinline))
static void ref_range(const double* A, const double* B, double* C,
                      int m, int n, int d, int i0, int i1, int j0, int j1)
{
    for (int i = i0; i < i1; i++)
        for (int j = j0; j < j1; j++) {
            double acc = 0.0;
            for (int t = 0; t < d; t++)
                acc += A[(size_t)t*m + i] * B[(size_t)t*n + j];
            C[(size_t)i*n + j] = acc;
        }
}
__attribute__((noinline))
static void refF_range(const double* A, const double* B, double* C,
                       int m, int n, int d, int i0, int i1, int j0, int j1)
{
    for (int i = i0; i < i1; i++)
        for (int j = j0; j < j1; j++) {
            double acc = 0.0;
            for (int t = 0; t < d; t++)
                acc = __builtin_fma(A[(size_t)t*m + i], B[(size_t)t*n + j], acc);
            C[(size_t)i*n + j] = acc;
        }
}
__attribute__((noinline))
static void k_ref (const double* A,const double* B,double* C,int m,int n,int d)
{ ref_range (A,B,C,m,n,d,0,m,0,n); }
__attribute__((noinline))
static void k_refF(const double* A,const double* B,double* C,int m,int n,int d)
{ refF_range(A,B,C,m,n,d,0,m,0,n); }

/* --------------------------------------------- Arm A: jam over j only ------ */
#define DEF_A4(NAME, OP, TAIL)                                                 \
__attribute__((noinline))                                                      \
static void NAME(const double* restrict A, const double* restrict B,           \
                 double* restrict C, int m, int n, int d)                      \
{                                                                              \
    int nj = n & ~3;                                                           \
    for (int i = 0; i < m; i++)                                                \
        for (int j = 0; j < nj; j += 4) {                                      \
            __m256d c0 = _mm256_setzero_pd();                                  \
            const double* ap = A + i; const double* bp = B + j;                \
            for (int t = 0; t < d; t++) {                                      \
                c0 = OP(_mm256_broadcast_sd(ap), _mm256_loadu_pd(bp), c0);     \
                ap += m; bp += n;                                              \
            }                                                                  \
            _mm256_storeu_pd(C + (size_t)i*n + j, c0);                         \
        }                                                                      \
    if (nj < n) TAIL(A,B,C,m,n,d,0,m,nj,n);                                    \
}
#define DEF_A8(NAME, OP, TAIL)                                                 \
__attribute__((noinline))                                                      \
static void NAME(const double* restrict A, const double* restrict B,           \
                 double* restrict C, int m, int n, int d)                      \
{                                                                              \
    int nj = n & ~7;                                                           \
    for (int i = 0; i < m; i++)                                                \
        for (int j = 0; j < nj; j += 8) {                                      \
            __m256d c0 = _mm256_setzero_pd(), c1 = _mm256_setzero_pd();        \
            const double* ap = A + i; const double* bp = B + j;                \
            for (int t = 0; t < d; t++) {                                      \
                __m256d a0 = _mm256_broadcast_sd(ap);                          \
                c0 = OP(a0, _mm256_loadu_pd(bp),   c0);                        \
                c1 = OP(a0, _mm256_loadu_pd(bp+4), c1);                        \
                ap += m; bp += n;                                              \
            }                                                                  \
            _mm256_storeu_pd(C + (size_t)i*n + j,   c0);                       \
            _mm256_storeu_pd(C + (size_t)i*n + j+4, c1);                       \
        }                                                                      \
    if (nj < n) TAIL(A,B,C,m,n,d,0,m,nj,n);                                    \
}
DEF_A4(k_A4,  MADD,  ref_range)
DEF_A8(k_A8,  MADD,  ref_range)
DEF_A8(k_A8f, FMADD, refF_range)

/* ------------------------------------- Arm B: R_i x 8 register tile -------- */
#define DEF_B48(NAME, OP, TAIL)                                                \
__attribute__((noinline))                                                      \
static void NAME(const double* restrict A, const double* restrict B,           \
                 double* restrict C, int m, int n, int d)                      \
{                                                                              \
    int mi = m & ~3, nj = n & ~7;                                              \
    for (int i = 0; i < mi; i += 4)                                            \
        for (int j = 0; j < nj; j += 8) {                                      \
            __m256d c00=_mm256_setzero_pd(), c01=_mm256_setzero_pd();          \
            __m256d c10=_mm256_setzero_pd(), c11=_mm256_setzero_pd();          \
            __m256d c20=_mm256_setzero_pd(), c21=_mm256_setzero_pd();          \
            __m256d c30=_mm256_setzero_pd(), c31=_mm256_setzero_pd();          \
            const double* ap = A + i; const double* bp = B + j;                \
            for (int t = 0; t < d; t++) {                                      \
                __m256d b0 = _mm256_loadu_pd(bp), b1 = _mm256_loadu_pd(bp+4);  \
                __m256d a  = _mm256_broadcast_sd(ap);                          \
                c00 = OP(a,b0,c00); c01 = OP(a,b1,c01);                        \
                a   = _mm256_broadcast_sd(ap+1);                               \
                c10 = OP(a,b0,c10); c11 = OP(a,b1,c11);                        \
                a   = _mm256_broadcast_sd(ap+2);                               \
                c20 = OP(a,b0,c20); c21 = OP(a,b1,c21);                        \
                a   = _mm256_broadcast_sd(ap+3);                               \
                c30 = OP(a,b0,c30); c31 = OP(a,b1,c31);                        \
                ap += m; bp += n;                                              \
            }                                                                  \
            double* cp = C + (size_t)i*n + j;                                  \
            _mm256_storeu_pd(cp,       c00); _mm256_storeu_pd(cp+4,     c01);  \
            _mm256_storeu_pd(cp+n,     c10); _mm256_storeu_pd(cp+n+4,   c11);  \
            _mm256_storeu_pd(cp+2*n,   c20); _mm256_storeu_pd(cp+2*n+4, c21);  \
            _mm256_storeu_pd(cp+3*n,   c30); _mm256_storeu_pd(cp+3*n+4, c31);  \
        }                                                                      \
    if (nj < n) TAIL(A,B,C,m,n,d,0,mi,nj,n);                                   \
    if (mi < m) TAIL(A,B,C,m,n,d,mi,m,0,n);                                    \
}
#define DEF_B28(NAME, OP, TAIL)                                                \
__attribute__((noinline))                                                      \
static void NAME(const double* restrict A, const double* restrict B,           \
                 double* restrict C, int m, int n, int d)                      \
{                                                                              \
    int mi = m & ~1, nj = n & ~7;                                              \
    for (int i = 0; i < mi; i += 2)                                            \
        for (int j = 0; j < nj; j += 8) {                                      \
            __m256d c00=_mm256_setzero_pd(), c01=_mm256_setzero_pd();          \
            __m256d c10=_mm256_setzero_pd(), c11=_mm256_setzero_pd();          \
            const double* ap = A + i; const double* bp = B + j;                \
            for (int t = 0; t < d; t++) {                                      \
                __m256d b0 = _mm256_loadu_pd(bp), b1 = _mm256_loadu_pd(bp+4);  \
                __m256d a  = _mm256_broadcast_sd(ap);                          \
                c00 = OP(a,b0,c00); c01 = OP(a,b1,c01);                        \
                a   = _mm256_broadcast_sd(ap+1);                               \
                c10 = OP(a,b0,c10); c11 = OP(a,b1,c11);                        \
                ap += m; bp += n;                                              \
            }                                                                  \
            double* cp = C + (size_t)i*n + j;                                  \
            _mm256_storeu_pd(cp,   c00); _mm256_storeu_pd(cp+4,   c01);        \
            _mm256_storeu_pd(cp+n, c10); _mm256_storeu_pd(cp+n+4, c11);        \
        }                                                                      \
    if (nj < n) TAIL(A,B,C,m,n,d,0,mi,nj,n);                                   \
    if (mi < m) TAIL(A,B,C,m,n,d,mi,m,0,n);                                    \
}
DEF_B28(k_B28,  MADD,  ref_range)
DEF_B48(k_B48,  MADD,  ref_range)
DEF_B48(k_B48f, FMADD, refF_range)

/* ------------------------------------ Arm C: split the FOLD (needs a licence) */
__attribute__((noinline))
static void k_C4(const double* restrict A, const double* restrict B,
                 double* restrict C, int m, int n, int d)
{
    int dt = d & ~3;
    for (int i = 0; i < m; i++)
        for (int j = 0; j < n; j++) {
            double a0=0.0,a1=0.0,a2=0.0,a3=0.0;
            const double* ap = A + i;
            const double* bp = B + j;
            for (int t = 0; t < dt; t += 4) {
                a0 += ap[(size_t)(t  )*m] * bp[(size_t)(t  )*n];
                a1 += ap[(size_t)(t+1)*m] * bp[(size_t)(t+1)*n];
                a2 += ap[(size_t)(t+2)*m] * bp[(size_t)(t+2)*n];
                a3 += ap[(size_t)(t+3)*m] * bp[(size_t)(t+3)*n];
            }
            for (int t = dt; t < d; t++) a0 += ap[(size_t)t*m] * bp[(size_t)t*n];
            C[(size_t)i*n + j] = (a0 + a1) + (a2 + a3);
        }
}
#define DEF_C8(NAME, FUSE)                                                      \
__attribute__((noinline))                                                       \
static void NAME(const double* restrict A, const double* restrict B,            \
                 double* restrict C, int m, int n, int d)                       \
{                                                                               \
    int dt = d & ~7;                                                            \
    for (int i = 0; i < m; i++)                                                 \
        for (int j = 0; j < n; j++) {                                           \
            double s0=0,s1=0,s2=0,s3=0,s4=0,s5=0,s6=0,s7=0;                     \
            const double* ap = A + i;                                           \
            const double* bp = B + j;                                           \
            for (int t = 0; t < dt; t += 8) {                                   \
                s0 = FUSE(ap[(size_t)(t  )*m], bp[(size_t)(t  )*n], s0);        \
                s1 = FUSE(ap[(size_t)(t+1)*m], bp[(size_t)(t+1)*n], s1);        \
                s2 = FUSE(ap[(size_t)(t+2)*m], bp[(size_t)(t+2)*n], s2);        \
                s3 = FUSE(ap[(size_t)(t+3)*m], bp[(size_t)(t+3)*n], s3);        \
                s4 = FUSE(ap[(size_t)(t+4)*m], bp[(size_t)(t+4)*n], s4);        \
                s5 = FUSE(ap[(size_t)(t+5)*m], bp[(size_t)(t+5)*n], s5);        \
                s6 = FUSE(ap[(size_t)(t+6)*m], bp[(size_t)(t+6)*n], s6);        \
                s7 = FUSE(ap[(size_t)(t+7)*m], bp[(size_t)(t+7)*n], s7);        \
            }                                                                   \
            for (int t = dt; t < d; t++)                                        \
                s0 = FUSE(ap[(size_t)t*m], bp[(size_t)t*n], s0);                \
            C[(size_t)i*n + j] = ((s0+s1)+(s2+s3)) + ((s4+s5)+(s6+s7));         \
        }                                                                       \
}
#define SMADD(a,b,c) ((a)*(b) + (c))
DEF_C8(k_C8,  SMADD)
DEF_C8(k_C8f, __builtin_fma)

/* --------------- dot product (m=n=1): the shape where output-axis jam is
 * IMPOSSIBLE.  Here the fold axis is contiguous, so the licence buys SIMD too. */
__attribute__((noinline))
static double k_ref_dot(const double* restrict A, const double* restrict B, int d)
{ double acc = 0.0; for (int t = 0; t < d; t++) acc += A[t]*B[t]; return acc; }
__attribute__((noinline))
static double k_C4_dot(const double* restrict A, const double* restrict B, int d)
{
    double a0=0,a1=0,a2=0,a3=0; int t=0;
    for (; t+3 < d; t+=4) { a0+=A[t]*B[t]; a1+=A[t+1]*B[t+1];
                            a2+=A[t+2]*B[t+2]; a3+=A[t+3]*B[t+3]; }
    double acc=(a0+a1)+(a2+a3);
    for (; t<d; t++) acc += A[t]*B[t];
    return acc;
}
#define DEF_DOT(NAME, OP)                                                       \
__attribute__((noinline))                                                       \
static double NAME(const double* restrict A, const double* restrict B, int d)   \
{                                                                               \
    __m256d s0=_mm256_setzero_pd(), s1=_mm256_setzero_pd();                     \
    __m256d s2=_mm256_setzero_pd(), s3=_mm256_setzero_pd();                     \
    int t = 0;                                                                  \
    for (; t + 15 < d; t += 16) {                                               \
        s0 = OP(_mm256_loadu_pd(A+t),    _mm256_loadu_pd(B+t),    s0);          \
        s1 = OP(_mm256_loadu_pd(A+t+4),  _mm256_loadu_pd(B+t+4),  s1);          \
        s2 = OP(_mm256_loadu_pd(A+t+8),  _mm256_loadu_pd(B+t+8),  s2);          \
        s3 = OP(_mm256_loadu_pd(A+t+12), _mm256_loadu_pd(B+t+12), s3);          \
    }                                                                           \
    __m256d s = _mm256_add_pd(_mm256_add_pd(s0,s1), _mm256_add_pd(s2,s3));      \
    double v[4]; _mm256_storeu_pd(v, s);                                        \
    double acc = (v[0]+v[1])+(v[2]+v[3]);                                       \
    for (; t < d; t++) acc += A[t]*B[t];                                        \
    return acc;                                                                 \
}
DEF_DOT(k_D_dot,  MADD)
DEF_DOT(k_Df_dot, FMADD)

/* ------------------------------------------------------------------ harness */
typedef void (*kern_t)(const double*, const double*, double*, int,int,int);
typedef struct { const char* name; kern_t f; int fused; int reassoc; } arm_t;
static arm_t ARMS[] = {
    { "ref   Blade shape",   k_ref,  0, 0 },
    { "refF  Blade+fma",     k_refF, 1, 0 },
    { "A4    jam j x4",      k_A4,   0, 0 },
    { "A8    jam j x8",      k_A8,   0, 0 },
    { "B28   jam 2x8",       k_B28,  0, 0 },
    { "B48   jam 4x8",       k_B48,  0, 0 },
    { "A8f   jam j x8 fma",  k_A8f,  1, 0 },
    { "B48f  jam 4x8  fma",  k_B48f, 1, 0 },
    { "C4    fold split x4", k_C4,   0, 1 },
    { "C8    fold split x8", k_C8,   0, 1 },
    { "C8f   fold split fma",k_C8f,  1, 1 },
};
#define NARMS ((int)(sizeof(ARMS)/sizeof(ARMS[0])))

static uint64_t rs = 0x9E3779B97F4A7C15ull;
static double rnd(void){ rs ^= rs<<13; rs ^= rs>>7; rs ^= rs<<17;
                         return (double)((rs>>11) * (1.0/9007199254740992.0)) - 0.5; }
static void fill_int(double* p, size_t n, int seed)
{   for (size_t k = 0; k < n; k++) p[k] = (double)((int)((k*7 + seed*13) % 5) - 2); }
static void fill_rnd(double* p, size_t n){ for (size_t k=0;k<n;k++) p[k]=rnd(); }
static double checksum(const double* p, size_t n)
{ double s=0; for (size_t k=0;k<n;k++) s += p[k]*(double)((k&7)+1); return s; }

/* in-process FMA peak probe: 8 independent YMM chains */
static double probe_peak(void)
{
    __m256d a=_mm256_set1_pd(1.0000001), b=_mm256_set1_pd(0.9999999);
    __m256d c0=a,c1=b,c2=a,c3=b,c4=a,c5=b,c6=a,c7=b;
    const long iters = 20000000L;
    BARRIER();
    double t0 = now();
    for (long k = 0; k < iters; k++) {
        c0=_mm256_fmadd_pd(a,b,c0); c1=_mm256_fmadd_pd(a,b,c1);
        c2=_mm256_fmadd_pd(a,b,c2); c3=_mm256_fmadd_pd(a,b,c3);
        c4=_mm256_fmadd_pd(a,b,c4); c5=_mm256_fmadd_pd(a,b,c5);
        c6=_mm256_fmadd_pd(a,b,c6); c7=_mm256_fmadd_pd(a,b,c7);
    }
    double t1 = now();
    BARRIER();
    __m256d s=_mm256_add_pd(_mm256_add_pd(_mm256_add_pd(c0,c1),_mm256_add_pd(c2,c3)),
                            _mm256_add_pd(_mm256_add_pd(c4,c5),_mm256_add_pd(c6,c7)));
    double v[4]; _mm256_storeu_pd(v,s);
    fprintf(stderr, "(peak probe sink %.3f)\n", v[0]+v[1]+v[2]+v[3]);
    return (double)iters*8.0*4.0*2.0/(t1-t0)/1e9;
}

int main(int argc, char** argv)
{
    const char* mode = argc>1 ? argv[1] : "bench";

    if (!strcmp(mode,"peak")) { printf("FMA peak: %.2f GF/s\n", probe_peak()); return 0; }

    if (!strcmp(mode,"dot")) {
        int d = argc>2?atoi(argv[2]):100003; int reps = argc>3?atoi(argv[3]):200;
        double* A=_mm_malloc((size_t)d*8,64); double* B=_mm_malloc((size_t)d*8,64);
        fill_rnd(A,d); fill_rnd(B,d);
        double r0=0,r1=0,r2=0,r3=0; double t0,t1,tr,tc,td,tf;
        BARRIER(); t0=now(); for(int k=0;k<reps;k++){ r0=k_ref_dot(A,B,d); BARRIER(); } t1=now(); tr=t1-t0;
        BARRIER(); t0=now(); for(int k=0;k<reps;k++){ r1=k_C4_dot(A,B,d);  BARRIER(); } t1=now(); tc=t1-t0;
        BARRIER(); t0=now(); for(int k=0;k<reps;k++){ r2=k_D_dot(A,B,d);   BARRIER(); } t1=now(); td=t1-t0;
        BARRIER(); t0=now(); for(int k=0;k<reps;k++){ r3=k_Df_dot(A,B,d);  BARRIER(); } t1=now(); tf=t1-t0;
        double fl = 2.0*(double)d*(double)reps;
        printf("dot (m=n=1) d=%d reps=%d   [output-axis jam is IMPOSSIBLE here]\n", d, reps);
        printf("  ref  scalar 1 acc  %8.3f GF/s  val %.17g\n", fl/tr/1e9, r0);
        printf("  C4   scalar 4 acc  %8.3f GF/s  val %.17g  bitwise=%d  x%.2f\n", fl/tc/1e9, r1, r1==r0, tr/tc);
        printf("  D    ymm 4 acc     %8.3f GF/s  val %.17g  bitwise=%d  x%.2f\n", fl/td/1e9, r2, r2==r0, tr/td);
        printf("  Df   ymm 4 acc fma %8.3f GF/s  val %.17g  bitwise=%d  x%.2f\n", fl/tf/1e9, r3, r3==r0, tr/tf);
        return 0;
    }

    int m = argc>2?atoi(argv[2]):264;
    int n = argc>3?atoi(argv[3]):264;
    int d = argc>4?atoi(argv[4]):520;
    size_t szA=(size_t)d*m, szB=(size_t)d*n, szC=(size_t)m*n;
    double* A=_mm_malloc(szA*8,64); double* B=_mm_malloc(szB*8,64);
    double* C=_mm_malloc(szC*8,64);
    double* R=_mm_malloc(szC*8,64); double* RF=_mm_malloc(szC*8,64);
    if(!A||!B||!C||!R||!RF){ fprintf(stderr,"alloc fail\n"); return 1; }

    if (!strcmp(mode,"verify")) {
        int bad = 0;
        for (int pass = 0; pass < 2; pass++) {
            if (pass==0){ fill_int(A,szA,1); fill_int(B,szB,2); }
            else        { fill_rnd(A,szA);   fill_rnd(B,szB);   }
            k_ref (A,B,R, m,n,d);
            k_refF(A,B,RF,m,n,d);
            if (pass==0) {
                int oops=0;
                for (int i=0;i<m;i+=7) for (int j=0;j<n;j+=5){
                    long double s=0.0L;
                    for (int t=0;t<d;t++)
                        s += (long double)A[(size_t)t*m+i]*(long double)B[(size_t)t*n+j];
                    if ((double)s != R[(size_t)i*n+j]) oops++;
                }
                printf("oracle (long double, exact-int inputs): %s\n",
                       oops?"MISMATCH":"exact");
                if(oops) bad=1;
            }
            printf("--- %s inputs ---\n", pass?"random double":"small integer");
            for (int a = 2; a < NARMS; a++) {
                const double* ref = ARMS[a].fused ? RF : R;
                memset(C,0xCD,szC*8);
                ARMS[a].f(A,B,C,m,n,d);
                int bw = (memcmp(C,ref,szC*8)==0);
                double maxrel=0;
                for (size_t k=0;k<szC;k++){
                    double x=C[k], y=ref[k];
                    if (x!=y){ double e=fabs(x-y); double r=(fabs(y)>0)?e/fabs(y):e;
                               if(r>maxrel)maxrel=r; }
                }
                printf("  %-20s vs %-4s bitwise=%-3s maxrel=%-9.3g %s\n",
                       ARMS[a].name, ARMS[a].fused?"refF":"ref", bw?"YES":"no", maxrel,
                       ARMS[a].reassoc ? "(reassociates - LICENCE arm)"
                                       : "(order-preserving - must be bitwise)");
                if (!bw && !ARMS[a].reassoc) bad = 1;
            }
        }
        printf("VERIFY: %s\n", bad?"FAIL (an order-preserving arm drifted)":"OK");
        return bad;
    }

    int reps = argc>5?atoi(argv[5]):3;
    fill_int(A,szA,1); fill_int(B,szB,2);
    double peak = probe_peak();
    double flops = 2.0*(double)m*(double)n*(double)d;
    printf("shape m=%d n=%d d=%d reps=%d  flops/call=%.2f M  peak=%.2f GF/s\n",
           m,n,d,reps,flops/1e6,peak);
    printf("%-21s %9s %8s %8s %14s\n","arm","GF/s","%peak","x ref","checksum");
    double gref = 1;
    for (int a = 0; a < NARMS; a++) {
        memset(C,0xCD,szC*8);   /* poison: a dead arm shows garbage, not 0 */
        ARMS[a].f(A,B,C,m,n,d);            /* warm */
        BARRIER();
        double best = 1e30;
        for (int k = 0; k < reps; k++) {
            BARRIER();
            double t0 = now();
            ARMS[a].f(A,B,C,m,n,d);
            double t1 = now();
            BARRIER();
            if (t1-t0 < best) best = t1-t0;
        }
        double gf = flops/best/1e9;
        if (a==0) gref = gf;
        printf("%-21s %9.3f %7.1f%% %8.2f %14.1f\n",
               ARMS[a].name, gf, 100.0*gf/peak, gf/gref, checksum(C,szC));
    }
    return 0;
}
