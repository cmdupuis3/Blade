/* syrk_orientation.c -- ADVERSARIAL re-measure of packed_syr_syrk.c's 13.2x
 * headline in the operand orientation Blade actually emits.
 *
 * src/CodeGenExpr.fs:2328 materializeGramForm:
 *     result[i][j] = sum_k A[i][k] * conj(B[j][k])
 * so the CONTRACTED axis k is CONTIGUOUS in both operands, and for
 * gram(A,A) the output is a PACKED UPPER TRIANGLE.  That is the shape a
 * Blade program produces.
 *
 * packed_syr_syrk.c instead indexes its operand as  X[s*n + j], i.e. the
 * contracted (sample) axis is the LEADING one and the OUTPUT axis is
 * contiguous -- SAMPLE-MAJOR.  Its 13.2x compares
 *      (i) k repeated rank-1 passes over the whole packed C   [baseline]
 *   vs (ii) a register-blocked schedule that holds a C tile   [numerator]
 * both in that orientation.
 *
 * Two independent things are therefore under test:
 *   Q1  does the 13.2x transfer to the contraction-contiguous orientation?
 *   Q2  is "k repeated rank-1 passes" a baseline Blade would ever emit?
 *       (it is not: the emitter's nest keeps the accumulator in a register)
 *
 * LAYOUTS (same logical values, X[s*n+i] == Y[i*k+s]):
 *   SM  X : k rows of n     -- packed_syr_syrk.c's assumption
 *   CC  Y : n rows of k     -- materializeGramForm's emission
 *
 * BITWISE: operands are FULL-MANTISSA random doubles (correction 17), and
 * cc_dot_fma is the deliberately-bit-changing control that MUST read NO.
 */
#define _POSIX_C_SOURCE 200809L
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <stdint.h>
#include <math.h>
#include <time.h>
#include <immintrin.h>
#include "clangtimer.h"

#define OBSERVE(p) __asm__ __volatile__("" : : "r"(p) : "memory")
#define BARRIER()  __asm__ __volatile__("" : : : "memory")
#define BR __restrict

static inline size_t rowbase(int n,int i){ return (size_t)i*(size_t)(2*n - i + 1)/2u; }
static inline size_t packed_size(int n){ return (size_t)n*(size_t)(n+1)/2u; }
static inline int imin(int a,int b){ return a<b?a:b; }
static inline int imax(int a,int b){ return a>b?a:b; }
static double now_s(void){ struct timespec ts; clock_gettime(CLOCK_MONOTONIC,&ts);
                           return (double)ts.tv_sec + 1e-9*(double)ts.tv_nsec; }
static int dcmp(const void*x,const void*y){ double a=*(const double*)x,b=*(const double*)y;
                                            return (a<b)?-1:((a>b)?1:0); }
/* the emitter's real spelling; identity on reals */
static inline double conj_scalar(double x){ return x; }

/* ============================ SM-orientation arms ======================== */
/* verbatim schedule of packed_syr_syrk.c's syr_B4 (its arm C-(i) building block) */
static void syr_B4(int n, double alpha, const double* BR x, double* BR C){
    int i=0;
    for(; i+4<=n; i+=4){
        double* BR p0 = C + rowbase(n,i)   - (size_t)i;
        double* BR p1 = C + rowbase(n,i+1) - (size_t)(i+1);
        double* BR p2 = C + rowbase(n,i+2) - (size_t)(i+2);
        double* BR p3 = C + rowbase(n,i+3) - (size_t)(i+3);
        double t0=alpha*x[i], t1=alpha*x[i+1], t2=alpha*x[i+2], t3=alpha*x[i+3];
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
        double t=alpha*x[i]; double* BR p=C+rowbase(n,i)-(size_t)i;
        for(int j=i;j<n;++j) p[j]=fma(t,x[j],p[j]);
    }
}
/* arm C-(i): k repeated rank-1 passes */
__attribute__((noinline))
static void sm_repeated(int n,int k,const double* BR X,double* BR C){
    for(int s=0;s<k;s++) syr_B4(n,1.0,X+(size_t)s*n,C);
}
/* arm C-(ii-a1): verbatim syrk_blocked_a1 */
__attribute__((noinline))
static void sm_blocked_a1(int n,int k,const double* BR X,double* BR C,int Jb){
    const int nfull=(n/4)*4;
    for(int jb=0;jb<n;jb+=Jb){
        const int jend=imin(jb+Jb,n);
        for(int i0=0;i0<jend && i0<nfull;i0+=4){
            double* BR p0=C+rowbase(n,i0)  -(size_t)i0;
            double* BR p1=C+rowbase(n,i0+1)-(size_t)(i0+1);
            double* BR p2=C+rowbase(n,i0+2)-(size_t)(i0+2);
            double* BR p3=C+rowbase(n,i0+3)-(size_t)(i0+3);
            double* pr[4]; pr[0]=p0; pr[1]=p1; pr[2]=p2; pr[3]=p3;
            int ra=imax(jb,i0), rb=imin(jend,i0+3);
            for(int j=ra;j<rb;++j)
                for(int r=0;r<4 && i0+r<=j;++r){
                    double c=pr[r][j];
                    for(int s=0;s<k;s++){ const double* xs=X+(size_t)s*n; c=fma(xs[i0+r],xs[j],c); }
                    pr[r][j]=c;
                }
            int j=imax(jb,i0+3);
            for(;j+8<=jend;j+=8){
                __m256d c00=_mm256_loadu_pd(p0+j), c01=_mm256_loadu_pd(p0+j+4);
                __m256d c10=_mm256_loadu_pd(p1+j), c11=_mm256_loadu_pd(p1+j+4);
                __m256d c20=_mm256_loadu_pd(p2+j), c21=_mm256_loadu_pd(p2+j+4);
                __m256d c30=_mm256_loadu_pd(p3+j), c31=_mm256_loadu_pd(p3+j+4);
                for(int s=0;s<k;s++){
                    const double* xs=X+(size_t)s*n;
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
            for(;j+4<=jend;j+=4){
                __m256d c0=_mm256_loadu_pd(p0+j), c1=_mm256_loadu_pd(p1+j);
                __m256d c2=_mm256_loadu_pd(p2+j), c3=_mm256_loadu_pd(p3+j);
                for(int s=0;s<k;s++){
                    const double* xs=X+(size_t)s*n;
                    __m256d xv=_mm256_loadu_pd(xs+j);
                    c0=_mm256_fmadd_pd(_mm256_broadcast_sd(xs+i0),  xv,c0);
                    c1=_mm256_fmadd_pd(_mm256_broadcast_sd(xs+i0+1),xv,c1);
                    c2=_mm256_fmadd_pd(_mm256_broadcast_sd(xs+i0+2),xv,c2);
                    c3=_mm256_fmadd_pd(_mm256_broadcast_sd(xs+i0+3),xv,c3);
                }
                _mm256_storeu_pd(p0+j,c0); _mm256_storeu_pd(p1+j,c1);
                _mm256_storeu_pd(p2+j,c2); _mm256_storeu_pd(p3+j,c3);
            }
            for(;j<jend;++j)
                for(int r=0;r<4;r++){
                    double c=pr[r][j];
                    for(int s=0;s<k;s++){ const double* xs=X+(size_t)s*n; c=fma(xs[i0+r],xs[j],c); }
                    pr[r][j]=c;
                }
        }
        for(int i=nfull;i<n && i<jend;++i){
            double* BR pi=C+rowbase(n,i)-(size_t)i;
            for(int j=imax(jb,i);j<jend;++j){
                double c=pi[j];
                for(int s=0;s<k;s++){ const double* xs=X+(size_t)s*n; c=fma(xs[i],xs[j],c); }
                pi[j]=c;
            }
        }
    }
}

/* ============================ CC-orientation arms ======================== */
/* THE BASELINE BLADE ACTUALLY EMITS.  materializeGramForm's nest text,
 * restricted to the upper triangle (sameArray). */
__attribute__((noinline))
static void cc_dot(int n,int k,const double* BR Y,double* BR C){
    for(int i=0;i<n;i++){
        const double* BR ri=Y+(size_t)i*k;
        double* BR p=C+rowbase(n,i)-(size_t)i;
        for(int j=i;j<n;j++){
            const double* BR rj=Y+(size_t)j*k;
            double acc=0.0;
            for(int s=0;s<k;s++) acc += ri[s]*conj_scalar(rj[s]);
            p[j]=acc;
        }
    }
}
/* CONTROL that must read NO: same schedule, contracted FMA. */
__attribute__((noinline))
static void cc_dot_fma(int n,int k,const double* BR Y,double* BR C){
    for(int i=0;i<n;i++){
        const double* BR ri=Y+(size_t)i*k;
        double* BR p=C+rowbase(n,i)-(size_t)i;
        for(int j=i;j<n;j++){
            const double* BR rj=Y+(size_t)j*k;
            double acc=0.0;
            for(int s=0;s<k;s++) acc = fma(ri[s], conj_scalar(rj[s]), acc);
            p[j]=acc;
        }
    }
}
/* THE SHIPPED TRANSFORM: R-wide unroll-and-jam of the output axis j. */
#define MKJAM(R)                                                             \
__attribute__((noinline)) static void cc_jam##R(int n,int k,const double* BR Y,double* BR C){          \
    for(int i=0;i<n;i++){                                                    \
        const double* BR ri=Y+(size_t)i*k;                                   \
        double* BR p=C+rowbase(n,i)-(size_t)i;                               \
        int j=i;                                                             \
        for(; j+R<=n; j+=R){                                                 \
            const double* rj[R]; double acc[R];                              \
            for(int r=0;r<R;r++){ rj[r]=Y+(size_t)(j+r)*k; acc[r]=0.0; }     \
            for(int s=0;s<k;s++)                                             \
                for(int r=0;r<R;r++) acc[r] += ri[s]*conj_scalar(rj[r][s]);  \
            for(int r=0;r<R;r++) p[j+r]=acc[r];                              \
        }                                                                    \
        for(; j<n; j++){                                                     \
            const double* BR rj=Y+(size_t)j*k;                               \
            double a=0.0;                                                    \
            for(int s=0;s<k;s++) a += ri[s]*conj_scalar(rj[s]);              \
            p[j]=a;                                                          \
        }                                                                    \
    }                                                                        \
}
MKJAM(4)
MKJAM(5)
MKJAM(6)
MKJAM(8)

/* blocked transpose CC -> SM, then the SM blocked kernel.  The transpose is
 * INSIDE the timed region, exactly the honest accounting krs_former.c uses. */
__attribute__((noinline))
static void transpose_cc_to_sm(int n,int k,const double* BR Y,double* BR X){
    const int TB=32;
    for(int i0=0;i0<n;i0+=TB){
        int ie=imin(i0+TB,n);
        for(int s0=0;s0<k;s0+=TB){
            int se=imin(s0+TB,k);
            for(int i=i0;i<ie;i++){
                const double* BR yr=Y+(size_t)i*k;
                for(int s=s0;s<se;s++) X[(size_t)s*n+i]=yr[s];
            }
        }
    }
}
static double* g_scratch;   /* preallocated k*n scratch for the transpose arm */
__attribute__((noinline))
static void cc_xpose_blocked(int n,int k,const double* BR Y,double* BR C,int Jb){
    transpose_cc_to_sm(n,k,Y,g_scratch);
    sm_blocked_a1(n,k,g_scratch,C,Jb);
}


/* ======================= NEW: bitwise blocked schedule ===================
 * packed_syr_syrk.c's blocked arm verifies "bitwise" only against ITS OWN
 * reference, which is also written with fma().  Against the nest Blade
 * ACTUALLY emits -- `__gacc += a*b`, which gcc 15.2 compiles to vmulpd + an
 * in-order vaddsd chain, NOT to vfmadd -- the fma() form is a bit-changing
 * transform (correction 10's true half).  This arm is the same register-blocked
 * schedule with every _mm256_fmadd_pd split into _mm256_mul_pd +
 * _mm256_add_pd, so each cell's sample sum is the identical sequence of
 * roundings in the identical ascending order.  It should read `yes` where the
 * fma form reads `NO`.  The question it answers: what does byte-exactness cost
 * on this schedule? */
/* The attribute is LOAD-BEARING: at -ffp-contract=fast (Blade's default,
 * BLADE_FP_CONTRACT) gcc 15.2 re-fuses explicit _mm256_mul_pd + _mm256_add_pd
 * INTRINSICS back into vfmadd231pd, so writing the kernel with separate mul and
 * add is not by itself enough to keep it byte-exact.  gcc honours a per-function
 * override; clang 22.1.8 does NOT (its -ffp-contract= command-line flag
 * overrides #pragma clang fp contract(off)). */
#if defined(__GNUC__) && !defined(__clang__)
__attribute__((noinline, optimize("O3","fp-contract=off")))
#else
__attribute__((noinline))
#endif
static void sm_blocked_muladd(int n,int k,const double* BR X,double* BR C,int Jb){
    const int nfull=(n/4)*4;
    for(int jb=0;jb<n;jb+=Jb){
        const int jend=imin(jb+Jb,n);
        for(int i0=0;i0<jend && i0<nfull;i0+=4){
            double* BR p0=C+rowbase(n,i0)  -(size_t)i0;
            double* BR p1=C+rowbase(n,i0+1)-(size_t)(i0+1);
            double* BR p2=C+rowbase(n,i0+2)-(size_t)(i0+2);
            double* BR p3=C+rowbase(n,i0+3)-(size_t)(i0+3);
            double* pr[4]; pr[0]=p0; pr[1]=p1; pr[2]=p2; pr[3]=p3;
            int ra=imax(jb,i0), rb=imin(jend,i0+3);
            for(int j=ra;j<rb;++j)
                for(int r=0;r<4 && i0+r<=j;++r){
                    double c=pr[r][j];
                    for(int s=0;s<k;s++){ const double* xs=X+(size_t)s*n; c += xs[i0+r]*xs[j]; }
                    pr[r][j]=c;
                }
            int j=imax(jb,i0+3);
            for(;j+8<=jend;j+=8){
                __m256d c00=_mm256_loadu_pd(p0+j), c01=_mm256_loadu_pd(p0+j+4);
                __m256d c10=_mm256_loadu_pd(p1+j), c11=_mm256_loadu_pd(p1+j+4);
                __m256d c20=_mm256_loadu_pd(p2+j), c21=_mm256_loadu_pd(p2+j+4);
                __m256d c30=_mm256_loadu_pd(p3+j), c31=_mm256_loadu_pd(p3+j+4);
                for(int s=0;s<k;s++){
                    const double* xs=X+(size_t)s*n;
                    __m256d xv0=_mm256_loadu_pd(xs+j), xv1=_mm256_loadu_pd(xs+j+4);
                    __m256d a0=_mm256_broadcast_sd(xs+i0);
                    c00=_mm256_add_pd(c00,_mm256_mul_pd(a0,xv0)); c01=_mm256_add_pd(c01,_mm256_mul_pd(a0,xv1));
                    __m256d a1=_mm256_broadcast_sd(xs+i0+1);
                    c10=_mm256_add_pd(c10,_mm256_mul_pd(a1,xv0)); c11=_mm256_add_pd(c11,_mm256_mul_pd(a1,xv1));
                    __m256d a2=_mm256_broadcast_sd(xs+i0+2);
                    c20=_mm256_add_pd(c20,_mm256_mul_pd(a2,xv0)); c21=_mm256_add_pd(c21,_mm256_mul_pd(a2,xv1));
                    __m256d a3=_mm256_broadcast_sd(xs+i0+3);
                    c30=_mm256_add_pd(c30,_mm256_mul_pd(a3,xv0)); c31=_mm256_add_pd(c31,_mm256_mul_pd(a3,xv1));
                }
                _mm256_storeu_pd(p0+j,c00); _mm256_storeu_pd(p0+j+4,c01);
                _mm256_storeu_pd(p1+j,c10); _mm256_storeu_pd(p1+j+4,c11);
                _mm256_storeu_pd(p2+j,c20); _mm256_storeu_pd(p2+j+4,c21);
                _mm256_storeu_pd(p3+j,c30); _mm256_storeu_pd(p3+j+4,c31);
            }
            for(;j+4<=jend;j+=4){
                __m256d c0=_mm256_loadu_pd(p0+j), c1=_mm256_loadu_pd(p1+j);
                __m256d c2=_mm256_loadu_pd(p2+j), c3=_mm256_loadu_pd(p3+j);
                for(int s=0;s<k;s++){
                    const double* xs=X+(size_t)s*n;
                    __m256d xv=_mm256_loadu_pd(xs+j);
                    c0=_mm256_add_pd(c0,_mm256_mul_pd(_mm256_broadcast_sd(xs+i0),  xv));
                    c1=_mm256_add_pd(c1,_mm256_mul_pd(_mm256_broadcast_sd(xs+i0+1),xv));
                    c2=_mm256_add_pd(c2,_mm256_mul_pd(_mm256_broadcast_sd(xs+i0+2),xv));
                    c3=_mm256_add_pd(c3,_mm256_mul_pd(_mm256_broadcast_sd(xs+i0+3),xv));
                }
                _mm256_storeu_pd(p0+j,c0); _mm256_storeu_pd(p1+j,c1);
                _mm256_storeu_pd(p2+j,c2); _mm256_storeu_pd(p3+j,c3);
            }
            for(;j<jend;++j)
                for(int r=0;r<4;r++){
                    double c=pr[r][j];
                    for(int s=0;s<k;s++){ const double* xs=X+(size_t)s*n; c += xs[i0+r]*xs[j]; }
                    pr[r][j]=c;
                }
        }
        for(int i=nfull;i<n && i<jend;++i){
            double* BR pi=C+rowbase(n,i)-(size_t)i;
            for(int j=imax(jb,i);j<jend;++j){
                double c=pi[j];
                for(int s=0;s<k;s++){ const double* xs=X+(size_t)s*n; c += xs[i]*xs[j]; }
                pi[j]=c;
            }
        }
    }
}
__attribute__((noinline))
static void cc_xpose_blocked_ma(int n,int k,const double* BR Y,double* BR C,int Jb){
    transpose_cc_to_sm(n,k,Y,g_scratch);
    sm_blocked_muladd(n,k,g_scratch,C,Jb);
}

/* CC-native register-blocked dot product: 2x4 output tile, 4-lane partial sums,
 * horizontal reduce at the end.  This SPLITS each cell's sum across 4 lanes, so
 * it is a REASSOCIATION -- licence-gated, NOT bitwise.  Included to price the
 * alternative to the transpose. */
__attribute__((noinline))
static void cc_blocked_hsum(int n,int k,const double* BR Y,double* BR C){
    const int kv=(k/4)*4;
    for(int i0=0;i0<n;i0+=2){
        int mi=imin(2,n-i0);
        double* BR p0=C+rowbase(n,i0)-(size_t)i0;
        double* BR p1=(mi>1)?C+rowbase(n,i0+1)-(size_t)(i0+1):p0;
        const double* BR y0=Y+(size_t)i0*k;
        const double* BR y1=(mi>1)?Y+(size_t)(i0+1)*k:y0;
        int j=i0;
        for(; j+4<=n; j+=4){
            __m256d a[2][4];
            for(int u=0;u<2;u++) for(int v=0;v<4;v++) a[u][v]=_mm256_setzero_pd();
            const double* BR z0=Y+(size_t)(j  )*k; const double* BR z1=Y+(size_t)(j+1)*k;
            const double* BR z2=Y+(size_t)(j+2)*k; const double* BR z3=Y+(size_t)(j+3)*k;
            for(int s=0;s<kv;s+=4){
                __m256d v0=_mm256_loadu_pd(y0+s), v1=_mm256_loadu_pd(y1+s);
                __m256d w0=_mm256_loadu_pd(z0+s), w1=_mm256_loadu_pd(z1+s);
                __m256d w2=_mm256_loadu_pd(z2+s), w3=_mm256_loadu_pd(z3+s);
                a[0][0]=_mm256_fmadd_pd(v0,w0,a[0][0]); a[0][1]=_mm256_fmadd_pd(v0,w1,a[0][1]);
                a[0][2]=_mm256_fmadd_pd(v0,w2,a[0][2]); a[0][3]=_mm256_fmadd_pd(v0,w3,a[0][3]);
                a[1][0]=_mm256_fmadd_pd(v1,w0,a[1][0]); a[1][1]=_mm256_fmadd_pd(v1,w1,a[1][1]);
                a[1][2]=_mm256_fmadd_pd(v1,w2,a[1][2]); a[1][3]=_mm256_fmadd_pd(v1,w3,a[1][3]);
            }
            const double* zz[4]; zz[0]=z0; zz[1]=z1; zz[2]=z2; zz[3]=z3;
            for(int u=0;u<mi;u++){
                const double* BR yy=(u==0)?y0:y1;
                double* BR pp=(u==0)?p0:p1;
                for(int v=0;v<4;v++){
                    if(i0+u> j+v) continue;               /* below the diagonal */
                    __m256d t=a[u][v];
                    __m128d lo=_mm256_castpd256_pd128(t), hi=_mm256_extractf128_pd(t,1);
                    lo=_mm_add_pd(lo,hi);
                    double acc=_mm_cvtsd_f64(_mm_add_sd(lo,_mm_unpackhi_pd(lo,lo)));
                    for(int s=kv;s<k;s++) acc += yy[s]*zz[v][s];
                    pp[j+v]=acc;
                }
            }
        }
        for(; j<n; j++){
            const double* BR zj=Y+(size_t)j*k;
            for(int u=0;u<mi;u++){
                if(i0+u>j) continue;
                const double* BR yy=(u==0)?y0:y1; double* BR pp=(u==0)?p0:p1;
                double acc=0.0;
                for(int s=0;s<k;s++) acc += yy[s]*zj[s];
                pp[j]=acc;
            }
        }
    }
}

/* ================================ harness =============================== */
static void fill_rand_full(double* v, size_t m, unsigned long long seed){
    unsigned long long s=seed?seed:0x243F6A8885A308D3ULL;
    for(size_t i=0;i<m;i++){
        s^=s<<13; s^=s>>7; s^=s<<17;
        v[i] = (double)(s>>11)*(1.0/9007199254740992.0) + 0.5;   /* [0.5,1.5), full mantissa */
    }
}
static uint64_t bithash(const void*p,size_t nb){
    const uint8_t*b=(const uint8_t*)p; uint64_t h=1469598103934665603ULL;
    for(size_t i=0;i<nb;i++){ h^=b[i]; h*=1099511628211ULL; }
    return h;
}

#define NARM 11
static double *SMX, *CCY, *Cbuf, *Cref;

static void run_arm(int id,int n,int k,int Jb){
    const double* SM=SMX; const double* CC=CCY;
    switch(id){
        case 0: sm_repeated(n,k,SM,Cbuf); break;
        case 1: sm_blocked_a1(n,k,SM,Cbuf,Jb); break;
        case 2: cc_dot(n,k,CC,Cbuf); break;
        case 3: cc_dot_fma(n,k,CC,Cbuf); break;
        case 4: cc_jam4(n,k,CC,Cbuf); break;
        case 5: cc_jam5(n,k,CC,Cbuf); break;
        case 6: cc_jam6(n,k,CC,Cbuf); break;
        case 7: cc_jam8(n,k,CC,Cbuf); break;
        case 8: cc_xpose_blocked(n,k,CC,Cbuf,Jb); break;
        case 9: cc_blocked_hsum(n,k,CC,Cbuf); break;
        case 10: cc_xpose_blocked_ma(n,k,CC,Cbuf,Jb); break;
    }
}
static const char* ARMN[NARM]={
    "SM_repeated  (README baseline)","SM_blocked_a1(README numerator)",
    "CC_dot       (BLADE EMITS THIS)","CC_dot_fma   (bit control: must=NO)",
    "CC_jam4","CC_jam5","CC_jam6","CC_jam8",
    "CC_xpose+blocked (bitwise)","CC_blocked_hsum  (NEEDS REASSOC LICENCE)",
    "CC_xpose+blocked MULADD  <-- NEW"};
static const int ARMZERO[NARM]={1,1,0,0,0,0,0,0,1,0,1};  /* needs C zeroed first */

static void bench(int n,int k,int Jb,int reps){
    size_t P=packed_size(n);
    SMX =(double*)_mm_malloc(sizeof(double)*(size_t)n*k,64);
    CCY =(double*)_mm_malloc(sizeof(double)*(size_t)n*k,64);
    g_scratch=(double*)_mm_malloc(sizeof(double)*(size_t)n*k,64);
    Cbuf=(double*)_mm_malloc(sizeof(double)*P,64);
    Cref=(double*)_mm_malloc(sizeof(double)*P,64);
    if(!SMX||!CCY||!g_scratch||!Cbuf||!Cref){ printf("alloc fail n=%d k=%d\n",n,k); return; }
    fill_rand_full(CCY,(size_t)n*k,0x9E3779B97F4A7C15ULL ^ (unsigned long long)n);
    for(int i=0;i<n;i++) for(int s=0;s<k;s++) SMX[(size_t)s*n+i]=CCY[(size_t)i*k+s];

    /* reference = the arm Blade emits */
    memset(Cref,0,sizeof(double)*P); cc_dot(n,k,CCY,Cref);
    uint64_t href=bithash(Cref,sizeof(double)*P);

    int bitok[NARM]; double maxrel[NARM]; size_t ndiff[NARM]; int dj[NARM], di[NARM];
    for(int a=0;a<NARM;a++){
        memset(Cbuf,0xA5,sizeof(double)*P);
        if(ARMZERO[a]) memset(Cbuf,0,sizeof(double)*P);
        run_arm(a,n,k,Jb);
        bitok[a]=(bithash(Cbuf,sizeof(double)*P)==href);
        double mr=0; size_t nd=0; di[a]=-1; dj[a]=-1;
        for(size_t q=0;q<P;q++){
            if(memcmp(&Cbuf[q],&Cref[q],8)){
                if(!nd){ int ii=0; while(ii+1<n && rowbase(n,ii+1)<=q) ii++;
                         di[a]=ii; dj[a]=(int)(q-rowbase(n,ii))+ii; }
                nd++;
            }
            double d=fabs(Cbuf[q]-Cref[q]), m=fabs(Cref[q]);
            double r=(m>0)?d/m:d; if(r>mr) mr=r; }
        maxrel[a]=mr; ndiff[a]=nd;
    }
    double* t[NARM];
    for(int a=0;a<NARM;a++) t[a]=(double*)malloc(sizeof(double)*reps);
    for(int r=0;r<reps;r++)
        for(int a=0;a<NARM;a++){
            if(ARMZERO[a]) memset(Cbuf,0,sizeof(double)*P);
            OBSERVE(Cbuf); OBSERVE(SMX); OBSERVE(CCY); BARRIER();
            double t0=now_s(); run_arm(a,n,k,Jb); double dt=now_s()-t0;
            OBSERVE(Cbuf); BARRIER();
            t[a][r]=dt;
        }
    double med[NARM];
    for(int a=0;a<NARM;a++){ qsort(t[a],reps,sizeof(double),dcmp); med[a]=t[a][reps/2]; }
    double macs=(double)P*(double)k;
    printf("\n  n=%d  k=%d  Jb=%d  cells=%zu (C=%.1f MB)  operand=%.1f MB  MACs=%.4g  median of %d\n",
           n,k,Jb,P,sizeof(double)*P/1048576.0,(double)n*k*8/1048576.0,macs,reps);
    printf("    %-34s %11s %10s %10s %10s %10s %s\n",
           "arm","median_s","GMAC/s","vs CC_dot","vs SMrep","maxrel","bitwise");
    for(int a=0;a<NARM;a++)
        printf("    %-34s %11.6f %10.3f %9.3fx %9.3fx %10.2e %s\n",
               ARMN[a],med[a],macs/med[a]/1e9,med[2]/med[a],med[0]/med[a],
               maxrel[a], bitok[a]?"yes":"NO");
    double bestbit = med[10];
    for(int a=4;a<=7;a++) if(med[a]<bestbit) bestbit=med[a];
    printf("    >> README headline reproduction (SM_repeated / SM_blocked_a1)  = %.2fx\n",
           med[0]/med[1]);
    printf("    >> SM_blocked_a1 against what Blade emits (CC_dot)             = %.2fx  [free-lunch fantasy: ignores the layout]\n",
           med[2]/med[1]);
    printf("    >> BEST BITWISE arm against what Blade emits                   = %.2fx\n",
           med[2]/bestbit);
    for(int a=0;a<NARM;a++) free(t[a]);
    _mm_free(SMX);_mm_free(CCY);_mm_free(g_scratch);_mm_free(Cbuf);_mm_free(Cref);
}

int main(int argc,char**argv){
    int reps = (argc>1)?atoi(argv[1]):11;
    int only = (argc>2)?atoi(argv[2]):0;
    printf("=== syrk_orientation: packed symmetric gram, SM vs CC operand layout ===\n");
    printf("compiler %s   operands: full-mantissa random doubles in [0.5,1.5)\n",__VERSION__);
    if(only==0 || only==1) bench(2003, 256, 256, reps);  /* packed_syr_syrk.c's headline point */
    if(only==0 || only==2) bench( 701, 257, 256, reps);
    if(only==0 || only==3) bench( 257,2003, 256, reps);  /* tall-thin: few features, many samples */
    if(only==0 || only==4) bench(  61,2003, 61,  reps);  /* Blade's actual comoment3 extent */
    return 0;
}
