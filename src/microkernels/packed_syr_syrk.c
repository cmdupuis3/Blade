/* k6.c -- packed symmetric rank-1 update (syr) and its rank-k / streaming extension.
 *
 * Packed symmetric layout (Blade): rank-2 symmetric, extent n, C(n+1,2) cells,
 * ascending-lex.  Row i starts at rowbase(i) = i*(2n-i+1)/2 and covers j in [i,n).
 * Cell (i,j), i<=j, lives at C[rowbase(i) + (j-i)].
 *
 * Trick used throughout: pi = C + rowbase(i) - i, so cell (i,j) == pi[j].
 * (rowbase(i) >= i for all 0<=i<n, so pi never points before C.)
 *
 * All FP is done with explicit fma() / _mm256_fmadd_pd so every arm executes the
 * same sequence of roundings -- bitwise comparison is meaningful even for
 * non-exactly-representable inputs.
 */
#define _POSIX_C_SOURCE 200809L
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <stdint.h>
#include <math.h>
#include <time.h>
#include <immintrin.h>

/* Timing hygiene: force the compiler to treat the buffer as observed and to keep
 * every timed call inside the repetition loop.  (These kernels accumulate INTO C, so
 * they are not idempotent and were never hoistable -- this is belt and braces, and
 * the k-linearity of schedule (i) in the results confirms nothing was elided.) */
#define OBSERVE(p) __asm__ __volatile__("" : : "r"(p) : "memory")
#define BARRIER()  __asm__ __volatile__("" : : : "memory")

static inline size_t rowbase(int n, int i){ return (size_t)i*(size_t)(2*n - i + 1)/2u; }
static inline size_t packed_size(int n){ return (size_t)n*(size_t)(n+1)/2u; }
static inline int imin(int a,int b){ return a<b?a:b; }
static inline int imax(int a,int b){ return a>b?a:b; }

static double now_s(void){ struct timespec ts; clock_gettime(CLOCK_MONOTONIC,&ts); return (double)ts.tv_sec + 1e-9*(double)ts.tv_nsec; }

static uint64_t bithash(const void* p, size_t nbytes){
    const uint8_t* b = (const uint8_t*)p; uint64_t h = 1469598103934665603ULL;
    for(size_t i=0;i<nbytes;i++){ h ^= b[i]; h *= 1099511628211ULL; }
    return h;
}

/* ---------------- reference ---------------- */

/* dense n x n, full square, C += alpha*x*x^T */
static void syr_dense_ref(int n, double alpha, const double* x, double* D){
    for(int i=0;i<n;i++){
        double t = alpha*x[i];
        for(int j=0;j<n;j++) D[(size_t)i*n+j] = fma(t, x[j], D[(size_t)i*n+j]);
    }
}

/* naive packed triangular reference */
static void syr_packed_ref(int n, double alpha, const double* x, double* C){
    for(int i=0;i<n;i++){
        double t = alpha*x[i];
        size_t b = rowbase(n,i);
        for(int j=i;j<n;j++) C[b + (size_t)(j-i)] = fma(t, x[j], C[b + (size_t)(j-i)]);
    }
}

/* ---------------- Arm A: straightforward packed row loop (compiler vectorizes) --- */
static void syr_A(int n, double alpha, const double* restrict x, double* restrict C){
    for(int i=0;i<n;i++){
        double t = alpha*x[i];
        double* restrict p = C + rowbase(n,i) - (size_t)i;
        for(int j=i;j<n;j++) p[j] = fma(t, x[j], p[j]);
    }
}

/* ---------------- Arm B: explicit AVX2, R rows per pass (x reuse across rows) ---- */

static void syr_B1(int n, double alpha, const double* restrict x, double* restrict C){
    for(int i=0;i<n;i++){
        double t = alpha*x[i];
        double* restrict p = C + rowbase(n,i) - (size_t)i;
        __m256d v = _mm256_set1_pd(t);
        int j=i;
        for(; j+4<=n; j+=4)
            _mm256_storeu_pd(p+j, _mm256_fmadd_pd(v, _mm256_loadu_pd(x+j), _mm256_loadu_pd(p+j)));
        for(; j<n; ++j) p[j] = fma(t, x[j], p[j]);
    }
}

static void syr_B2(int n, double alpha, const double* restrict x, double* restrict C){
    int i=0;
    for(; i+2<=n; i+=2){
        double* restrict p0 = C + rowbase(n,i)   - (size_t)i;
        double* restrict p1 = C + rowbase(n,i+1) - (size_t)(i+1);
        double t0=alpha*x[i], t1=alpha*x[i+1];
        p0[i] = fma(t0, x[i], p0[i]);                       /* ragged head: j=i, only row 0 */
        __m256d v0=_mm256_set1_pd(t0), v1=_mm256_set1_pd(t1);
        int j=i+1;
        for(; j+4<=n; j+=4){
            __m256d xv=_mm256_loadu_pd(x+j);
            _mm256_storeu_pd(p0+j, _mm256_fmadd_pd(v0,xv,_mm256_loadu_pd(p0+j)));
            _mm256_storeu_pd(p1+j, _mm256_fmadd_pd(v1,xv,_mm256_loadu_pd(p1+j)));
        }
        for(; j<n; ++j){ double xj=x[j]; p0[j]=fma(t0,xj,p0[j]); p1[j]=fma(t1,xj,p1[j]); }
    }
    for(; i<n; ++i){
        double t=alpha*x[i]; double* restrict p=C+rowbase(n,i)-(size_t)i;
        for(int j=i;j<n;++j) p[j]=fma(t,x[j],p[j]);
    }
}

static void syr_B4(int n, double alpha, const double* restrict x, double* restrict C){
    int i=0;
    for(; i+4<=n; i+=4){
        double* restrict p0 = C + rowbase(n,i)   - (size_t)i;
        double* restrict p1 = C + rowbase(n,i+1) - (size_t)(i+1);
        double* restrict p2 = C + rowbase(n,i+2) - (size_t)(i+2);
        double* restrict p3 = C + rowbase(n,i+3) - (size_t)(i+3);
        double t0=alpha*x[i], t1=alpha*x[i+1], t2=alpha*x[i+2], t3=alpha*x[i+3];
        /* ragged head: columns i, i+1, i+2 (1, 2, 3 active rows) */
        p0[i]   = fma(t0, x[i],   p0[i]);
        p0[i+1] = fma(t0, x[i+1], p0[i+1]); p1[i+1] = fma(t1, x[i+1], p1[i+1]);
        p0[i+2] = fma(t0, x[i+2], p0[i+2]); p1[i+2] = fma(t1, x[i+2], p1[i+2]);
        p2[i+2] = fma(t2, x[i+2], p2[i+2]);
        __m256d v0=_mm256_set1_pd(t0), v1=_mm256_set1_pd(t1),
                v2=_mm256_set1_pd(t2), v3=_mm256_set1_pd(t3);
        int j=i+3;
        for(; j+4<=n; j+=4){
            __m256d xv=_mm256_loadu_pd(x+j);
            _mm256_storeu_pd(p0+j, _mm256_fmadd_pd(v0,xv,_mm256_loadu_pd(p0+j)));
            _mm256_storeu_pd(p1+j, _mm256_fmadd_pd(v1,xv,_mm256_loadu_pd(p1+j)));
            _mm256_storeu_pd(p2+j, _mm256_fmadd_pd(v2,xv,_mm256_loadu_pd(p2+j)));
            _mm256_storeu_pd(p3+j, _mm256_fmadd_pd(v3,xv,_mm256_loadu_pd(p3+j)));
        }
        for(; j<n; ++j){ double xj=x[j];
            p0[j]=fma(t0,xj,p0[j]); p1[j]=fma(t1,xj,p1[j]);
            p2[j]=fma(t2,xj,p2[j]); p3[j]=fma(t3,xj,p3[j]); }
    }
    for(; i<n; ++i){
        double t=alpha*x[i]; double* restrict p=C+rowbase(n,i)-(size_t)i;
        for(int j=i;j<n;++j) p[j]=fma(t,x[j],p[j]);
    }
}

/* Arm B8: 8 rows per pass -- 8 broadcasts + 1 x vector + 8 in-flight C vectors. */
static void syr_B8(int n, double alpha, const double* restrict x, double* restrict C){
    int i=0;
    for(; i+8<=n; i+=8){
        double* p[8]; double t[8]; __m256d v[8];
        for(int r=0;r<8;r++){ p[r]=C+rowbase(n,i+r)-(size_t)(i+r); t[r]=alpha*x[i+r]; v[r]=_mm256_set1_pd(t[r]); }
        for(int j=i;j<i+7;j++)                      /* ragged head: 7 columns */
            for(int r=0; r<8 && i+r<=j; r++) p[r][j]=fma(t[r],x[j],p[r][j]);
        int j=i+7;
        for(; j+4<=n; j+=4){
            __m256d xv=_mm256_loadu_pd(x+j);
            for(int r=0;r<8;r++)
                _mm256_storeu_pd(p[r]+j, _mm256_fmadd_pd(v[r],xv,_mm256_loadu_pd(p[r]+j)));
        }
        for(; j<n; ++j){ double xj=x[j]; for(int r=0;r<8;r++) p[r][j]=fma(t[r],xj,p[r][j]); }
    }
    for(; i<n; ++i){
        double tt=alpha*x[i]; double* restrict q=C+rowbase(n,i)-(size_t)i;
        for(int j=i;j<n;++j) q[j]=fma(tt,x[j],q[j]);
    }
}

/* ---------------- Arm C-(i): rank-k as k separate rank-1 passes over C ---------- */
static void syrk_repeated(int n, int k, double alpha, const double* restrict X, double* restrict C){
    for(int s=0;s<k;s++) syr_B4(n, alpha, X + (size_t)s*n, C);
}

/* ---------------- Arm C-(ii): blocked -- hold a C tile in registers, stream k ---- */
/* Register tile: 4 packed rows x 8 columns = 8 YMM accumulators,
 * + 2 YMM for the x_s column vector + up to 4 YMM broadcasts = 14 of 16 YMM.
 * Column blocking (Jb) keeps the k x Jb slice of X L2-resident across row panels.
 * Accumulation order per cell is s = 0..k-1 ascending -- identical to schedule (i).
 */
static void syrk_blocked(int n, int k, double alpha, const double* restrict X,
                         double* restrict C, int Jb){
    const int nfull = (n/4)*4;
    for(int jb=0; jb<n; jb+=Jb){
        const int jend = imin(jb+Jb, n);
        for(int i0=0; i0<jend && i0<nfull; i0+=4){
            double* restrict p0 = C + rowbase(n,i0)   - (size_t)i0;
            double* restrict p1 = C + rowbase(n,i0+1) - (size_t)(i0+1);
            double* restrict p2 = C + rowbase(n,i0+2) - (size_t)(i0+2);
            double* restrict p3 = C + rowbase(n,i0+3) - (size_t)(i0+3);
            double* pr[4]; pr[0]=p0; pr[1]=p1; pr[2]=p2; pr[3]=p3;
            /* ragged columns (only when the panel touches the diagonal block) */
            int ra = imax(jb, i0), rb = imin(jend, i0+3);
            for(int j=ra; j<rb; ++j)
                for(int r=0; r<4 && i0+r<=j; ++r){
                    double c = pr[r][j];
                    for(int s=0;s<k;s++){ const double* xs=X+(size_t)s*n; c = fma(alpha*xs[i0+r], xs[j], c); }
                    pr[r][j]=c;
                }
            int j = imax(jb, i0+3);
            for(; j+8<=jend; j+=8){
                __m256d c00=_mm256_loadu_pd(p0+j), c01=_mm256_loadu_pd(p0+j+4);
                __m256d c10=_mm256_loadu_pd(p1+j), c11=_mm256_loadu_pd(p1+j+4);
                __m256d c20=_mm256_loadu_pd(p2+j), c21=_mm256_loadu_pd(p2+j+4);
                __m256d c30=_mm256_loadu_pd(p3+j), c31=_mm256_loadu_pd(p3+j+4);
                for(int s=0;s<k;s++){
                    const double* xs = X + (size_t)s*n;
                    __m256d xv0=_mm256_loadu_pd(xs+j), xv1=_mm256_loadu_pd(xs+j+4);
                    __m256d a0=_mm256_set1_pd(alpha*xs[i0]);
                    c00=_mm256_fmadd_pd(a0,xv0,c00); c01=_mm256_fmadd_pd(a0,xv1,c01);
                    __m256d a1=_mm256_set1_pd(alpha*xs[i0+1]);
                    c10=_mm256_fmadd_pd(a1,xv0,c10); c11=_mm256_fmadd_pd(a1,xv1,c11);
                    __m256d a2=_mm256_set1_pd(alpha*xs[i0+2]);
                    c20=_mm256_fmadd_pd(a2,xv0,c20); c21=_mm256_fmadd_pd(a2,xv1,c21);
                    __m256d a3=_mm256_set1_pd(alpha*xs[i0+3]);
                    c30=_mm256_fmadd_pd(a3,xv0,c30); c31=_mm256_fmadd_pd(a3,xv1,c31);
                }
                _mm256_storeu_pd(p0+j,c00); _mm256_storeu_pd(p0+j+4,c01);
                _mm256_storeu_pd(p1+j,c10); _mm256_storeu_pd(p1+j+4,c11);
                _mm256_storeu_pd(p2+j,c20); _mm256_storeu_pd(p2+j+4,c21);
                _mm256_storeu_pd(p3+j,c30); _mm256_storeu_pd(p3+j+4,c31);
            }
            for(; j+4<=jend; j+=4){
                __m256d c0=_mm256_loadu_pd(p0+j), c1=_mm256_loadu_pd(p1+j);
                __m256d c2=_mm256_loadu_pd(p2+j), c3=_mm256_loadu_pd(p3+j);
                for(int s=0;s<k;s++){
                    const double* xs = X + (size_t)s*n;
                    __m256d xv=_mm256_loadu_pd(xs+j);
                    c0=_mm256_fmadd_pd(_mm256_set1_pd(alpha*xs[i0]),  xv,c0);
                    c1=_mm256_fmadd_pd(_mm256_set1_pd(alpha*xs[i0+1]),xv,c1);
                    c2=_mm256_fmadd_pd(_mm256_set1_pd(alpha*xs[i0+2]),xv,c2);
                    c3=_mm256_fmadd_pd(_mm256_set1_pd(alpha*xs[i0+3]),xv,c3);
                }
                _mm256_storeu_pd(p0+j,c0); _mm256_storeu_pd(p1+j,c1);
                _mm256_storeu_pd(p2+j,c2); _mm256_storeu_pd(p3+j,c3);
            }
            for(; j<jend; ++j)
                for(int r=0;r<4;r++){
                    double c=pr[r][j];
                    for(int s=0;s<k;s++){ const double* xs=X+(size_t)s*n; c=fma(alpha*xs[i0+r], xs[j], c); }
                    pr[r][j]=c;
                }
        }
        for(int i=nfull; i<n && i<jend; ++i){          /* leftover rows (n mod 4) */
            double* restrict pi = C + rowbase(n,i) - (size_t)i;
            for(int j=imax(jb,i); j<jend; ++j){
                double c=pi[j];
                for(int s=0;s<k;s++){ const double* xs=X+(size_t)s*n; c=fma(alpha*xs[i], xs[j], c); }
                pi[j]=c;
            }
        }
    }
}

/* Arm C-(ii-a1): the alpha == 1 specialization.  Blade's symmetric former accumulates
 * with unit weight, so the per-sample scalar `alpha*x_s[i]` (4 vmulsd + 4 vbroadcastsd
 * per 8 FMAs in the general kernel) collapses to a bare broadcast.  Because 1.0*v == v
 * exactly, this stays BITWISE identical to the general kernel called with alpha=1. */
static void syrk_blocked_a1(int n, int k, const double* restrict X,
                            double* restrict C, int Jb){
    const int nfull = (n/4)*4;
    for(int jb=0; jb<n; jb+=Jb){
        const int jend = imin(jb+Jb, n);
        for(int i0=0; i0<jend && i0<nfull; i0+=4){
            double* restrict p0 = C + rowbase(n,i0)   - (size_t)i0;
            double* restrict p1 = C + rowbase(n,i0+1) - (size_t)(i0+1);
            double* restrict p2 = C + rowbase(n,i0+2) - (size_t)(i0+2);
            double* restrict p3 = C + rowbase(n,i0+3) - (size_t)(i0+3);
            double* pr[4]; pr[0]=p0; pr[1]=p1; pr[2]=p2; pr[3]=p3;
            int ra = imax(jb, i0), rb = imin(jend, i0+3);
            for(int j=ra; j<rb; ++j)
                for(int r=0; r<4 && i0+r<=j; ++r){
                    double c = pr[r][j];
                    for(int s=0;s<k;s++){ const double* xs=X+(size_t)s*n; c = fma(xs[i0+r], xs[j], c); }
                    pr[r][j]=c;
                }
            int j = imax(jb, i0+3);
            for(; j+8<=jend; j+=8){
                __m256d c00=_mm256_loadu_pd(p0+j), c01=_mm256_loadu_pd(p0+j+4);
                __m256d c10=_mm256_loadu_pd(p1+j), c11=_mm256_loadu_pd(p1+j+4);
                __m256d c20=_mm256_loadu_pd(p2+j), c21=_mm256_loadu_pd(p2+j+4);
                __m256d c30=_mm256_loadu_pd(p3+j), c31=_mm256_loadu_pd(p3+j+4);
                for(int s=0;s<k;s++){
                    const double* xs = X + (size_t)s*n;
                    __m256d xv0=_mm256_loadu_pd(xs+j), xv1=_mm256_loadu_pd(xs+j+4);
                    __m256d a0=_mm256_broadcast_sd(xs+i0);
                    c00=_mm256_fmadd_pd(a0,xv0,c00); c01=_mm256_fmadd_pd(a0,xv1,c01);
                    __m256d a1=_mm256_broadcast_sd(xs+i0+1);
                    c10=_mm256_fmadd_pd(a1,xv0,c10); c11=_mm256_fmadd_pd(a1,xv1,c11);
                    __m256d a2=_mm256_broadcast_sd(xs+i0+2);
                    c20=_mm256_fmadd_pd(a2,xv0,c20); c21=_mm256_fmadd_pd(a2,xv1,c21);
                    __m256d a3=_mm256_broadcast_sd(xs+i0+3);
                    c30=_mm256_fmadd_pd(a3,xv0,c30); c31=_mm256_fmadd_pd(a3,xv1,c31);
                }
                _mm256_storeu_pd(p0+j,c00); _mm256_storeu_pd(p0+j+4,c01);
                _mm256_storeu_pd(p1+j,c10); _mm256_storeu_pd(p1+j+4,c11);
                _mm256_storeu_pd(p2+j,c20); _mm256_storeu_pd(p2+j+4,c21);
                _mm256_storeu_pd(p3+j,c30); _mm256_storeu_pd(p3+j+4,c31);
            }
            for(; j+4<=jend; j+=4){
                __m256d c0=_mm256_loadu_pd(p0+j), c1=_mm256_loadu_pd(p1+j);
                __m256d c2=_mm256_loadu_pd(p2+j), c3=_mm256_loadu_pd(p3+j);
                for(int s=0;s<k;s++){
                    const double* xs = X + (size_t)s*n;
                    __m256d xv=_mm256_loadu_pd(xs+j);
                    c0=_mm256_fmadd_pd(_mm256_broadcast_sd(xs+i0),  xv,c0);
                    c1=_mm256_fmadd_pd(_mm256_broadcast_sd(xs+i0+1),xv,c1);
                    c2=_mm256_fmadd_pd(_mm256_broadcast_sd(xs+i0+2),xv,c2);
                    c3=_mm256_fmadd_pd(_mm256_broadcast_sd(xs+i0+3),xv,c3);
                }
                _mm256_storeu_pd(p0+j,c0); _mm256_storeu_pd(p1+j,c1);
                _mm256_storeu_pd(p2+j,c2); _mm256_storeu_pd(p3+j,c3);
            }
            for(; j<jend; ++j)
                for(int r=0;r<4;r++){
                    double c=pr[r][j];
                    for(int s=0;s<k;s++){ const double* xs=X+(size_t)s*n; c=fma(xs[i0+r], xs[j], c); }
                    pr[r][j]=c;
                }
        }
        for(int i=nfull; i<n && i<jend; ++i){
            double* restrict pi = C + rowbase(n,i) - (size_t)i;
            for(int j=imax(jb,i); j<jend; ++j){
                double c=pi[j];
                for(int s=0;s<k;s++){ const double* xs=X+(size_t)s*n; c=fma(xs[i], xs[j], c); }
                pi[j]=c;
            }
        }
    }
}

/* ---------------- harness ---------------- */

typedef void (*syr_fn)(int,double,const double*,double*);
typedef struct { const char* name; syr_fn f; } Arm;

static void fill_int(double* v, int n, unsigned seed){
    unsigned s = seed?seed:1u;
    for(int i=0;i<n;i++){ s = s*1664525u + 1013904223u; v[i] = (double)(int)((s>>16)%17) - 8.0; } /* -8..8, exact */
}
static void fill_rand(double* v, int n, unsigned seed){
    unsigned s = seed?seed:1u;
    for(int i=0;i<n;i++){ s = s*1664525u + 1013904223u; v[i] = ((double)(s>>8) / 8388608.0) - 1.0; }
}

static int verify_small(int n){
    size_t P = packed_size(n);
    double *x = (double*)_mm_malloc(sizeof(double)*(size_t)n, 64);
    double *D = (double*)calloc((size_t)n*n, sizeof(double));
    double *R = (double*)_mm_malloc(sizeof(double)*P, 64);
    double *T = (double*)_mm_malloc(sizeof(double)*P, 64);
    if(!x||!D||!R||!T){ printf("alloc fail\n"); return 1; }
    fill_int(x,n,12345u);
    double alpha = 3.0;
    memset(R,0,sizeof(double)*P);
    syr_dense_ref(n, alpha, x, D);
    syr_packed_ref(n, alpha, x, R);
    int bad=0;
    for(int i=0;i<n && !bad;i++) for(int j=i;j<n;j++){
        double a = R[rowbase(n,i)+(size_t)(j-i)], b = D[(size_t)i*n+j];
        if(memcmp(&a,&b,8)){ printf("  DENSE-vs-PACKED mismatch n=%d (%d,%d) %g vs %g\n",n,i,j,a,b); bad=1; break; }
    }
    if(!bad) printf("  n=%4d  packed-ref == dense-ref over the upper triangle: BITWISE OK\n", n);
    const Arm arms[] = {{"A ",syr_A},{"B1",syr_B1},{"B2",syr_B2},{"B4",syr_B4},{"B8",syr_B8}};
    for(size_t a=0;a<sizeof(arms)/sizeof(arms[0]);a++){
        memset(T,0,sizeof(double)*P);
        arms[a].f(n, alpha, x, T);
        int mm = memcmp(T,R,sizeof(double)*P);
        printf("  n=%4d  arm %s vs packed-ref: %s\n", n, arms[a].name, mm? "*** MISMATCH ***":"BITWISE OK");
        if(mm) bad=1;
    }
    _mm_free(x); free(D); _mm_free(R); _mm_free(T);
    return bad;
}

static int verify_rankk(int n, int k, int Jb, int exact){
    size_t P = packed_size(n);
    double *X = (double*)_mm_malloc(sizeof(double)*(size_t)n*k, 64);
    double *R = (double*)_mm_malloc(sizeof(double)*P, 64);
    double *T = (double*)_mm_malloc(sizeof(double)*P, 64);
    if(!X||!R||!T){ printf("alloc fail\n"); return 1; }
    if(exact) fill_int(X, n*k, 777u); else fill_rand(X, n*k, 777u);
    double alpha = 2.0;
    memset(R,0,sizeof(double)*P); syrk_repeated(n,k,alpha,X,R);
    memset(T,0,sizeof(double)*P); syrk_blocked (n,k,alpha,X,T,Jb);
    int mm = memcmp(T,R,sizeof(double)*P);
    double maxrel=0; size_t nz=0;
    if(mm){ for(size_t q=0;q<P;q++){ double d=fabs(T[q]-R[q]); double m=fabs(R[q]);
              if(d!=0){ nz++; double r = m>0? d/m : d; if(r>maxrel) maxrel=r; } } }
    printf("  n=%4d k=%3d Jb=%3d inputs=%-13s : (ii)blocked vs (i)repeated: %s",
           n,k,Jb, exact?"exact-int":"random-double", mm?"*** DIFFERS ***":"BITWISE OK");
    if(mm) printf("  (%zu/%zu cells differ, max rel %.3e)", nz, P, maxrel);
    printf("\n");
    _mm_free(X); _mm_free(R); _mm_free(T);
    return mm?1:0;
}

/* The machine is shared, so absolute times are contaminated by whatever else is
 * running.  Two defenses: (1) best-of-N (the minimum converges to the uncontended
 * time), (2) round-robin interleaving of the arms, INCLUDING a plain read-modify-write
 * stream over an identically sized buffer, so the roofline is sampled in the same
 * time window as the kernels it is the ceiling for. */
static void bench_rank1(int n, int reps){
    size_t P = packed_size(n);
    double *x = (double*)_mm_malloc(sizeof(double)*(size_t)n, 64);
    double *C = (double*)_mm_malloc(sizeof(double)*P, 64);
    if(!x||!C){ printf("alloc fail n=%d\n",n); return; }
    fill_int(x,n,999u);
    const Arm arms[] = {{"RMW stream (roofline)",NULL},{"ref (naive packed)",syr_packed_ref},
                        {"A   (packed auto-vec)",syr_A},
                        {"B1  (avx2, 1 row)",syr_B1},{"B2  (avx2, 2 rows)",syr_B2},
                        {"B4  (avx2, 4 rows)",syr_B4},{"B8  (avx2, 8 rows)",syr_B8}};
    const int NA = (int)(sizeof(arms)/sizeof(arms[0]));
    double best[8]; for(int a=0;a<NA;a++) best[a]=1e30;
    uint64_t h[8];
    /* correctness pass: every real arm must reproduce the reference bit for bit */
    for(int a=1;a<NA;a++){ memset(C,0,sizeof(double)*P); arms[a].f(n,1.0,x,C); h[a]=bithash(C,sizeof(double)*P); }
    /* timing pass: round-robin */
    for(int r=0;r<reps;r++)
        for(int a=0;a<NA;a++){
            OBSERVE(C); OBSERVE(x); double t0=now_s();
            if(a==0){ double c1=1.0000001, c2=1e-30; for(size_t q=0;q<P;q++) C[q]=fma(c1,C[q],c2); }
            else arms[a].f(n,1.0,x,C);
            OBSERVE(C); double dt=now_s()-t0; if(dt<best[a]) best[a]=dt;
        }
    printf("\n  n=%d  cells=%zu  C=%.1f MB   (traffic model: 16 B/cell, read+write)\n",
           n, P, sizeof(double)*P/1048576.0);
    printf("    %-22s %10s %10s %9s %9s   %s\n","arm","best ms","ns/cell","GB/s","%roof","bits");
    for(int a=0;a<NA;a++)
        printf("    %-22s %10.3f %10.3f %9.2f %8.0f%%   %s\n", arms[a].name, best[a]*1e3,
               best[a]*1e9/(double)P, 16.0*(double)P/best[a]/1e9, 100.0*best[0]/best[a],
               a==0? "-" : (h[a]==h[1]?"OK":"*** DIFFERS ***"));
    _mm_free(x); _mm_free(C);
}

/* Single-core roofline: pure read-modify-write stream, the same 16 B/element of DRAM
 * traffic the packed syr generates.  This -- not the all-core DDR4 figure -- is the
 * ceiling a single-threaded syr can possibly reach. */
static void bw_roofline(size_t nelem, int reps, const char* tag){
    double* y = (double*)_mm_malloc(nelem*sizeof(double), 64);
    if(!y){ printf("  roofline alloc fail\n"); return; }
    memset(y,0,nelem*sizeof(double));
    double a = 1.0000001, b = 1e-9, best=1e30;
    for(int r=0;r<reps;r++){
        OBSERVE(y); double t0=now_s();
        for(size_t i=0;i<nelem;i++) y[i] = fma(a, y[i], b);
        OBSERVE(y); double dt=now_s()-t0; if(dt<best) best=dt;
    }
    printf("  RMW-stream roofline %-10s  %8.1f MB   %8.3f ms   %7.2f GB/s   (sink %.3f)\n",
           tag, nelem*8.0/1048576.0, best*1e3, 16.0*(double)nelem/best/1e9, y[nelem/2]);
    _mm_free(y);
}

static void bench_rankk(int n, const int* ks, int nk, int Jb){
    size_t P = packed_size(n);
    double *C = (double*)_mm_malloc(sizeof(double)*P, 64);
    if(!C){ printf("alloc fail\n"); return; }
    printf("\n  rank-k, n=%d  cells=%zu  C=%.1f MB  Jb=%d\n", n, P, sizeof(double)*P/1048576.0, Jb);
    printf("    %5s %11s %11s %11s %9s %10s %10s %6s\n",
           "k","(i) rep ms","(ii) blk ms","(ii-a1) ms","speedup","(i) GB/s","(ii-a1)GF/s","bits");
    for(int q=0;q<nk;q++){
        int k = ks[q];
        double *X = (double*)_mm_malloc(sizeof(double)*(size_t)n*k, 64);
        if(!X){ printf("alloc fail k=%d\n",k); continue; }
        fill_int(X, n*k, 4242u);
        double ti=1e30, tii=1e30, ta1=1e30;
        uint64_t hi=0,hii=0,ha1=0;
        for(int r=0;r<3;r++){                      /* interleaved, best-of-3 */
            memset(C,0,sizeof(double)*P); OBSERVE(C); OBSERVE(X);
            double t0=now_s(); syrk_repeated(n,k,1.0,X,C); OBSERVE(C); double d=now_s()-t0; if(d<ti) ti=d;
            hi = bithash(C,sizeof(double)*P);
            memset(C,0,sizeof(double)*P); OBSERVE(C); OBSERVE(X);
            t0=now_s(); syrk_blocked(n,k,1.0,X,C,Jb); OBSERVE(C); d=now_s()-t0; if(d<tii) tii=d;
            hii = bithash(C,sizeof(double)*P);
            memset(C,0,sizeof(double)*P); OBSERVE(C); OBSERVE(X);
            t0=now_s(); syrk_blocked_a1(n,k,X,C,Jb); OBSERVE(C); d=now_s()-t0; if(d<ta1) ta1=d;
            ha1 = bithash(C,sizeof(double)*P);
        }
        double flops = 2.0*(double)P*(double)k;
        printf("    %5d %11.2f %11.2f %11.2f %8.2fx %10.2f %10.2f %6s\n",
               k, ti*1e3, tii*1e3, ta1*1e3, ti/ta1, 16.0*(double)P*k/ti/1e9, flops/ta1/1e9,
               (hi==hii && hi==ha1)?"OK":"DIFFER");
        _mm_free(X);
    }
    _mm_free(C);
}

/* How big may the column block Jb be?  The inner s-loop touches the k x Jb slice of X;
 * it is re-read once per row panel, so it must stay L2-resident (k*Jb*8 bytes). */
static void sweep_jb(int n, int k){
    size_t P = packed_size(n);
    double *C = (double*)_mm_malloc(sizeof(double)*P, 64);
    double *X = (double*)_mm_malloc(sizeof(double)*(size_t)n*k, 64);
    if(!C||!X){ printf("alloc fail\n"); return; }
    fill_int(X, n*k, 4242u);
    printf("\n  Jb sweep, n=%d k=%d  (X slice = k*Jb*8 bytes must stay in L2 = 512 KB)\n", n, k);
    printf("    %6s %12s %10s %10s\n","Jb","slice KB","ms","GF/s");
    const int jbs[] = {32,64,128,256,512,1024,4096};
    for(size_t q=0;q<sizeof(jbs)/sizeof(jbs[0]);q++){
        int Jb = jbs[q]; double best=1e30;
        for(int r=0;r<3;r++){ memset(C,0,sizeof(double)*P); OBSERVE(C); OBSERVE(X);
            double t0=now_s(); syrk_blocked(n,k,1.0,X,C,Jb); OBSERVE(C); double d=now_s()-t0; if(d<best) best=d; }
        printf("    %6d %12.0f %10.2f %10.2f\n", Jb, (double)k*Jb*8.0/1024.0, best*1e3,
               2.0*(double)P*(double)k/best/1e9);
    }
    _mm_free(C); _mm_free(X);
}

int main(int argc, char** argv){
    int fast = (argc>1 && strcmp(argv[1],"--fast")==0);
    printf("=== k6: packed symmetric syr / syrk ===\n\n[1] correctness (bitwise)\n");
    int bad=0;
    bad |= verify_small(1); bad |= verify_small(5); bad |= verify_small(61);
    bad |= verify_small(257); bad |= verify_small(1001);
    printf("\n[2] rank-k schedule equivalence\n");
    bad |= verify_rankk(257, 33, 128, 1);
    bad |= verify_rankk(1001, 64, 256, 1);
    bad |= verify_rankk(2003, 16, 256, 1);
    verify_rankk(257, 33, 128, 0);              /* informational: random doubles */
    verify_rankk(1001, 64, 256, 0);
    printf("\n[3] single-core memory roofline (same 16 B/elem traffic shape)\n");
    bw_roofline(  262144, 20, "(L2, 2MB)");
    bw_roofline( 1310720, 15, "(L3, 10MB)");
    bw_roofline(18045028,  8, "(DRAM,138MB)");
    printf("\n[4] rank-1 bandwidth  (arms interleaved round-robin, best-of-N)\n");
    bench_rank1(701, 40);
    bench_rank1(2003, 20);
    if(!fast) bench_rank1(6007, 8);
    printf("\n[5] rank-k schedules\n");
    { int ks[] = {1,2,4,8,16,32,64,256}; bench_rankk(2003, ks, 8, 256); }
    if(!fast){ int ks[] = {1,2,4,8,16,64}; bench_rankk(6007, ks, 6, 256); }
    printf("\n[6] column-block tuning\n");
    sweep_jb(2003, 256);
    sweep_jb(2003, 64);
    printf("\n%s\n", bad? "*** SOME CHECKS FAILED ***" : "all correctness checks passed");
    return bad;
}
