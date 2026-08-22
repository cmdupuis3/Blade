/* KRS (Khatri-Rao Simplex) former, rank 3.
 *
 *   C(i<=j<=k) = sum_t A[t][i]*A[t][j]*A[t][k],  A is (d x n) row-major,
 *   C packed symmetric, ascending canonical tuples, ascending-lex order.
 *
 * Packed layout: for a canonical prefix (i,j) the cells k in [j,n) form ONE
 * contiguous run of length n-j at offset base[i][j].  sum over prefixes of
 * (n-j) == C(n+2,3) exactly.
 *
 * Arms:
 *   R0  reference  : naive 3-deep triangular nest, single scalar accumulator.
 *   A   multi-acc  : same nest, t-fold split into 8 independent accumulators.
 *   V   blade-like : per prefix, vectorize over the contiguous tail k,
 *                    ONE accumulator chain (reproduces the ~12-15 GF model).
 *   B   KRS        : G[t,(i,j)] = A[t][i]*A[t][j]; register-blocked 6x8
 *                    rank-1 update over (prefix-block x ragged tail).
 */
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <math.h>
#include <time.h>
#include <immintrin.h>

static double wall(void){
    struct timespec ts;
    clock_gettime(CLOCK_MONOTONIC, &ts);
    return (double)ts.tv_sec + 1e-9*(double)ts.tv_nsec;
}

/* ---------------------------------------------------------------- utility */

static size_t prefixes_r2(long n){ return (size_t)n*(n+1)/2; }

/* base[i*n+j] = pool offset of cell (i,j,j) ; cell (i,j,k) is base + (k-j) */
static size_t* make_base(int n, size_t* total){
    size_t* base = (size_t*)malloc((size_t)n*n*sizeof(size_t));
    size_t cur = 0;
    for (int i=0;i<n;i++)
        for (int j=i;j<n;j++){ base[(size_t)i*n+j] = cur; cur += (size_t)(n-j); }
    *total = cur;
    return base;
}

/* ------------------------------------------------------------ R0 reference */
static void ref_r3(const double* restrict A, int d, int n,
                   const size_t* restrict base, double* restrict C)
{
    for (int i=0;i<n;i++)
    for (int j=i;j<n;j++){
        double* crow = C + base[(size_t)i*n+j] - j;   /* crow[k] == cell (i,j,k) */
        for (int k=j;k<n;k++){
            double acc = 0.0;
            for (int t=0;t<d;t++){
                const double* r = A + (size_t)t*n;
                acc += r[i]*r[j]*r[k];
            }
            crow[k] += acc;
        }
    }
}

/* ------------------------------------ Arm A: 8 independent accumulators   */
static void armA_r3(const double* restrict A, int d, int n,
                    const size_t* restrict base, double* restrict C)
{
    for (int i=0;i<n;i++)
    for (int j=i;j<n;j++){
        double* crow = C + base[(size_t)i*n+j] - j;
        for (int k=j;k<n;k++){
            double a0=0,a1=0,a2=0,a3=0,a4=0,a5=0,a6=0,a7=0;
            int t=0;
            for (; t+8<=d; t+=8){
                const double* r0 = A + (size_t)(t  )*n;
                const double* r1 = A + (size_t)(t+1)*n;
                const double* r2 = A + (size_t)(t+2)*n;
                const double* r3 = A + (size_t)(t+3)*n;
                const double* r4 = A + (size_t)(t+4)*n;
                const double* r5 = A + (size_t)(t+5)*n;
                const double* r6 = A + (size_t)(t+6)*n;
                const double* r7 = A + (size_t)(t+7)*n;
                a0 += r0[i]*r0[j]*r0[k];
                a1 += r1[i]*r1[j]*r1[k];
                a2 += r2[i]*r2[j]*r2[k];
                a3 += r3[i]*r3[j]*r3[k];
                a4 += r4[i]*r4[j]*r4[k];
                a5 += r5[i]*r5[j]*r5[k];
                a6 += r6[i]*r6[j]*r6[k];
                a7 += r7[i]*r7[j]*r7[k];
            }
            for (; t<d; t++){
                const double* r = A + (size_t)t*n;
                a0 += r[i]*r[j]*r[k];
            }
            crow[k] += ((a0+a1)+(a2+a3)) + ((a4+a5)+(a6+a7));
        }
    }
}

/* ------- Arm V: vectorize the contiguous tail, ONE accumulator chain ----- */
static void armV_r3(const double* restrict A, int d, int n,
                    const size_t* restrict base, double* restrict C)
{
    for (int i=0;i<n;i++)
    for (int j=i;j<n;j++){
        double* crow = C + base[(size_t)i*n+j] - j;
        int k = j;
        for (; k+4<=n; k+=4){
            __m256d acc = _mm256_setzero_pd();
            for (int t=0;t<d;t++){
                const double* r = A + (size_t)t*n;
                __m256d g = _mm256_set1_pd(r[i]*r[j]);
                acc = _mm256_fmadd_pd(g, _mm256_loadu_pd(r+k), acc);
            }
            _mm256_storeu_pd(crow+k,
                _mm256_add_pd(_mm256_loadu_pd(crow+k), acc));
        }
        for (; k<n; k++){
            double acc = 0.0;
            for (int t=0;t<d;t++){
                const double* r = A + (size_t)t*n;
                acc += r[i]*r[j]*r[k];
            }
            crow[k] += acc;
        }
    }
}

/* ---- Arm T: t-outer rank-1 update, accumulator lives in MEMORY ---------
 * This is the shape a structure-first compiler emits from the loop-object:
 * stream t on the outside, and for each canonical prefix splat g and FMA
 * along the contiguous tail straight into the pool.  Every cell is a
 * load-fma-store to C each t, so C (not the FMA chain) sets the rate.  */
static void armT_r3(const double* restrict A, int d, int n,
                    const size_t* restrict base, double* restrict C)
{
    for (int t=0;t<d;t++){
        const double* r = A + (size_t)t*n;
        for (int i=0;i<n;i++){
            double ai = r[i];
            for (int j=i;j<n;j++){
                double g = ai*r[j];
                __m256d gv = _mm256_set1_pd(g);
                double* crow = C + base[(size_t)i*n+j] - j;
                int k=j;
                for (; k+4<=n; k+=4)
                    _mm256_storeu_pd(crow+k,
                        _mm256_fmadd_pd(gv, _mm256_loadu_pd(r+k),
                                        _mm256_loadu_pd(crow+k)));
                for (; k<n; k++) crow[k] += g*r[k];
            }
        }
    }
}

/* ----------------------------- Arm B: KRS, 6x8 register-blocked kernel --- */
#define MR 6
#define NR 8

/* 12 accumulator YMM + 2 operand YMM + 1 broadcast = 15 of 16 architectural
 * YMM registers; 12 independent FMA chains >= the 8 needed to cover the
 * ~4-cycle FMA latency at 2 FMA/cycle. */
static inline void micro_6x8(int kc,
                             const double* restrict Gp,
                             const double* restrict Ap,
                             double* restrict acc)
{
    __m256d c00=_mm256_setzero_pd(), c01=_mm256_setzero_pd();
    __m256d c10=_mm256_setzero_pd(), c11=_mm256_setzero_pd();
    __m256d c20=_mm256_setzero_pd(), c21=_mm256_setzero_pd();
    __m256d c30=_mm256_setzero_pd(), c31=_mm256_setzero_pd();
    __m256d c40=_mm256_setzero_pd(), c41=_mm256_setzero_pd();
    __m256d c50=_mm256_setzero_pd(), c51=_mm256_setzero_pd();
    for (int t=0;t<kc;t++){
        const double* a = Ap + (size_t)t*NR;
        const double* g = Gp + (size_t)t*MR;
        __m256d b0 = _mm256_load_pd(a);
        __m256d b1 = _mm256_load_pd(a+4);
        __m256d gv;
        gv=_mm256_broadcast_sd(g+0); c00=_mm256_fmadd_pd(gv,b0,c00); c01=_mm256_fmadd_pd(gv,b1,c01);
        gv=_mm256_broadcast_sd(g+1); c10=_mm256_fmadd_pd(gv,b0,c10); c11=_mm256_fmadd_pd(gv,b1,c11);
        gv=_mm256_broadcast_sd(g+2); c20=_mm256_fmadd_pd(gv,b0,c20); c21=_mm256_fmadd_pd(gv,b1,c21);
        gv=_mm256_broadcast_sd(g+3); c30=_mm256_fmadd_pd(gv,b0,c30); c31=_mm256_fmadd_pd(gv,b1,c31);
        gv=_mm256_broadcast_sd(g+4); c40=_mm256_fmadd_pd(gv,b0,c40); c41=_mm256_fmadd_pd(gv,b1,c41);
        gv=_mm256_broadcast_sd(g+5); c50=_mm256_fmadd_pd(gv,b0,c50); c51=_mm256_fmadd_pd(gv,b1,c51);
    }
    _mm256_store_pd(acc+ 0,c00); _mm256_store_pd(acc+ 4,c01);
    _mm256_store_pd(acc+ 8,c10); _mm256_store_pd(acc+12,c11);
    _mm256_store_pd(acc+16,c20); _mm256_store_pd(acc+20,c21);
    _mm256_store_pd(acc+24,c30); _mm256_store_pd(acc+28,c31);
    _mm256_store_pd(acc+32,c40); _mm256_store_pd(acc+36,c41);
    _mm256_store_pd(acc+40,c50); _mm256_store_pd(acc+44,c51);
}

/* 4x8 variant: 8 accumulator YMM + 2 operand + 1 broadcast = 11 regs.
 * Tests the claim that ~8 chains already saturate; costs operand reuse
 * (6 loads per 8 FMAs, vs 8 loads per 12 FMAs for the 6x8 tile). */
static inline void micro_4x8(int kc,
                             const double* restrict Gp,
                             const double* restrict Ap,
                             double* restrict acc)
{
    __m256d c00=_mm256_setzero_pd(), c01=_mm256_setzero_pd();
    __m256d c10=_mm256_setzero_pd(), c11=_mm256_setzero_pd();
    __m256d c20=_mm256_setzero_pd(), c21=_mm256_setzero_pd();
    __m256d c30=_mm256_setzero_pd(), c31=_mm256_setzero_pd();
    for (int t=0;t<kc;t++){
        const double* a = Ap + (size_t)t*NR;
        const double* g = Gp + (size_t)t*MR;
        __m256d b0 = _mm256_load_pd(a);
        __m256d b1 = _mm256_load_pd(a+4);
        __m256d gv;
        gv=_mm256_broadcast_sd(g+0); c00=_mm256_fmadd_pd(gv,b0,c00); c01=_mm256_fmadd_pd(gv,b1,c01);
        gv=_mm256_broadcast_sd(g+1); c10=_mm256_fmadd_pd(gv,b0,c10); c11=_mm256_fmadd_pd(gv,b1,c11);
        gv=_mm256_broadcast_sd(g+2); c20=_mm256_fmadd_pd(gv,b0,c20); c21=_mm256_fmadd_pd(gv,b1,c21);
        gv=_mm256_broadcast_sd(g+3); c30=_mm256_fmadd_pd(gv,b0,c30); c31=_mm256_fmadd_pd(gv,b1,c31);
    }
    _mm256_store_pd(acc+ 0,c00); _mm256_store_pd(acc+ 4,c01);
    _mm256_store_pd(acc+ 8,c10); _mm256_store_pd(acc+12,c11);
    _mm256_store_pd(acc+16,c20); _mm256_store_pd(acc+20,c21);
    _mm256_store_pd(acc+24,c30); _mm256_store_pd(acc+28,c31);
}

/* micro-kernel call count and computed-lane count, for honest flop accounting */
static void krs_shape(int n, int d, int KC, int mrb, size_t* ncalls, size_t* computed){
    int np = (n+NR-1)/NR;
    size_t calls = 0;
    for (int i=0;i<n;i++)
        for (int j0=i;j0<n;j0+=mrb)
            calls += (size_t)(np - j0/NR);
    int ntb = (d + KC - 1)/KC;
    *ncalls   = calls * (size_t)ntb;
    *computed = calls * (size_t)mrb*NR;   /* cell-visits per full pass over t */
}

static void krs_r3(const double* restrict A, int d, int n,
                   const size_t* restrict base, double* restrict C, int KC, int mrb)
{
    const int np = (n + NR - 1)/NR;

    /* pack A once into NR-wide k-panels, zero-padded past n:
     *   Ap[(p*d + t)*NR + l] = A[t][8p+l]
     * t is unit-stride within a panel, so the micro-kernel streams it. */
    double* Ap = (double*)_mm_malloc((size_t)np*d*NR*sizeof(double), 64);
    for (int p=0;p<np;p++)
        for (int t=0;t<d;t++){
            const double* r = A + (size_t)t*n;
            double* dst = Ap + ((size_t)p*d + t)*NR;
            for (int l=0;l<NR;l++){ int k = p*NR+l; dst[l] = (k<n)? r[k] : 0.0; }
        }

    double* Gp = (double*)_mm_malloc((size_t)KC*MR*sizeof(double), 64);
    double acc[MR*NR] __attribute__((aligned(64)));

    /* i outermost keeps the C_i sub-pool (<= C(n-i+1,2) cells) resident across
     * the t-blocking passes; t-blocking keeps the Ap slab (KC*n) in L2. */
    for (int i=0;i<n;i++){
        for (int t0=0;t0<d;t0+=KC){
            int kc = (d-t0 < KC) ? (d-t0) : KC;
            for (int j0=i;j0<n;j0+=mrb){
                int mr = (n-j0 < mrb) ? (n-j0) : mrb;

                /* Khatri-Rao panel for this prefix block, materialized per
                 * (prefix-block, t-block): KC*MR doubles = 3 KB at KC=64. */
                for (int tt=0;tt<kc;tt++){
                    const double* r = A + (size_t)(t0+tt)*n;
                    double ai = r[i];
                    double* g = Gp + (size_t)tt*MR;
                    int m=0;
                    for (; m<mr; m++) g[m] = ai * r[j0+m];
                    for (; m<mrb; m++) g[m] = 0.0;
                }

                for (int p = j0/NR; p < np; p++){
                    if (mrb == 6) micro_6x8(kc, Gp, Ap + ((size_t)p*d + t0)*NR, acc);
                    else          micro_4x8(kc, Gp, Ap + ((size_t)p*d + t0)*NR, acc);
                    int kb = p*NR;
                    if (kb >= j0 + mr - 1 && kb + NR <= n){
                        /* interior: every lane valid for every row */
                        for (int m=0;m<mr;m++){
                            double* crow = C + base[(size_t)i*n + j0+m] - (j0+m);
                            double* q = crow + kb;
                            _mm256_storeu_pd(q,   _mm256_add_pd(_mm256_loadu_pd(q),   _mm256_load_pd(acc+m*NR)));
                            _mm256_storeu_pd(q+4, _mm256_add_pd(_mm256_loadu_pd(q+4), _mm256_load_pd(acc+m*NR+4)));
                        }
                    } else {
                        /* boundary: ragged head (k<j) and tail (k>=n) */
                        for (int m=0;m<mr;m++){
                            int j = j0+m;
                            double* crow = C + base[(size_t)i*n + j] - j;
                            for (int l=0;l<NR;l++){
                                int k = kb+l;
                                if (k>=j && k<n) crow[k] += acc[m*NR+l];
                            }
                        }
                    }
                }
            }
        }
    }
    _mm_free(Gp);
    _mm_free(Ap);
}

/* ------------------------------------------------------------- peak probe */
static double peak_probe(void){
    __m256d a0=_mm256_set1_pd(1.0),a1=_mm256_set1_pd(1.1),a2=_mm256_set1_pd(1.2);
    __m256d a3=_mm256_set1_pd(1.3),a4=_mm256_set1_pd(1.4),a5=_mm256_set1_pd(1.5);
    __m256d a6=_mm256_set1_pd(1.6),a7=_mm256_set1_pd(1.7),a8=_mm256_set1_pd(1.8);
    __m256d a9=_mm256_set1_pd(1.9),aA=_mm256_set1_pd(2.0),aB=_mm256_set1_pd(2.1);
    __m256d x=_mm256_set1_pd(1.0000000001), y=_mm256_set1_pd(0.9999999999);
    const long iters = 20000000L;
    double t0 = wall();
    for (long i=0;i<iters;i++){
        a0=_mm256_fmadd_pd(x,y,a0); a1=_mm256_fmadd_pd(x,y,a1); a2=_mm256_fmadd_pd(x,y,a2);
        a3=_mm256_fmadd_pd(x,y,a3); a4=_mm256_fmadd_pd(x,y,a4); a5=_mm256_fmadd_pd(x,y,a5);
        a6=_mm256_fmadd_pd(x,y,a6); a7=_mm256_fmadd_pd(x,y,a7); a8=_mm256_fmadd_pd(x,y,a8);
        a9=_mm256_fmadd_pd(x,y,a9); aA=_mm256_fmadd_pd(x,y,aA); aB=_mm256_fmadd_pd(x,y,aB);
    }
    double t1 = wall();
    __m256d s = _mm256_add_pd(_mm256_add_pd(_mm256_add_pd(a0,a1),_mm256_add_pd(a2,a3)),
                _mm256_add_pd(_mm256_add_pd(_mm256_add_pd(a4,a5),_mm256_add_pd(a6,a7)),
                              _mm256_add_pd(_mm256_add_pd(a8,a9),_mm256_add_pd(aA,aB))));
    double sink[4]; _mm256_storeu_pd(sink,s);
    double gf = (double)iters*12.0*4.0*2.0/(t1-t0)/1e9;
    if (sink[0] == 12345.6789) printf(" ");   /* keep it alive */
    return gf;
}

/* ------------------------------------------------------------------- main */
static unsigned long long rngs = 0x853c49e6748fea9bULL;
static double urand(void){
    rngs = rngs*6364136223846793005ULL + 1442695040888963407ULL;
    return (double)((rngs>>11) & ((1ULL<<53)-1)) / (double)(1ULL<<53);
}

static void fill_int(double* A, size_t nelem){
    for (size_t i=0;i<nelem;i++) A[i] = (double)((int)(urand()*9.0) - 4);
}
static void fill_real(double* A, size_t nelem){
    for (size_t i=0;i<nelem;i++) A[i] = 2.0*urand() - 1.0;
}

typedef struct {
    double maxrel;   /* elementwise, restricted to cells >= 1e-3 of peak magnitude */
    double normrel;  /* ||x-y||_inf / ||y||_inf : the honest normwise measure     */
    double maxabs;
    size_t nbit; size_t ncmp;
} cmp_t;
static cmp_t compare(const double* x, const double* y, size_t nz){
    cmp_t c; c.maxrel=0.0; c.normrel=0.0; c.maxabs=0.0; c.nbit=0; c.ncmp=nz;
    double peak = 0.0;
    for (size_t i=0;i<nz;i++){ double m=fabs(y[i]); if (m>peak) peak=m; }
    double floor_ = peak*1e-3;
    for (size_t i=0;i<nz;i++){
        double a=x[i], b=y[i];
        if (a==b){ c.nbit++; continue; }
        double ae = fabs(a-b);
        if (ae > c.maxabs) c.maxabs = ae;
        if (fabs(b) >= floor_){
            double re = ae/fabs(b);
            if (re > c.maxrel) c.maxrel = re;
        }
    }
    c.normrel = (peak>0.0)? c.maxabs/peak : 0.0;
    return c;
}

int main(int argc, char** argv){
    const char* mode = (argc>1)? argv[1] : "verify";

    if (!strcmp(mode,"peak")){
        double g1 = peak_probe();
        printf("peak_probe: %.2f GFLOP/s (12 chains, 256-bit FMA, no memory)\n", g1);
        printf("implied clock at 16 flops/cycle: %.3f GHz\n", g1/16.0);
        return 0;
    }

    if (!strcmp(mode,"verify")){
        int n = (argc>2)? atoi(argv[2]) : 96;
        int d = (argc>3)? atoi(argv[3]) : 257;
        int KC= (argc>4)? atoi(argv[4]) : 64;
        int MRSEL = (argc>5)? atoi(argv[5]) : 6;
        size_t total; size_t* base = make_base(n,&total);
        double* A  = (double*)_mm_malloc((size_t)d*n*sizeof(double),64);
        double* C0 = (double*)_mm_malloc(total*sizeof(double),64);
        double* C1 = (double*)_mm_malloc(total*sizeof(double),64);
        printf("verify n=%d d=%d KC=%d cells=%zu (%.2f MB)\n",
               n,d,KC,total,total*8.0/1048576.0);
        for (int pass=0; pass<2; pass++){
            if (pass==0) fill_int(A,(size_t)d*n); else fill_real(A,(size_t)d*n);
            const char* lbl = pass? "random doubles" : "small ints (exact)";
            memset(C0,0,total*sizeof(double)); ref_r3(A,d,n,base,C0);
            memset(C1,0,total*sizeof(double)); armA_r3(A,d,n,base,C1);
            cmp_t ca = compare(C1,C0,total);
            memset(C1,0,total*sizeof(double)); armV_r3(A,d,n,base,C1);
            cmp_t cv = compare(C1,C0,total);
            memset(C1,0,total*sizeof(double)); armT_r3(A,d,n,base,C1);
            cmp_t ct = compare(C1,C0,total);
            memset(C1,0,total*sizeof(double)); krs_r3(A,d,n,base,C1,KC,MRSEL);
            cmp_t cb = compare(C1,C0,total);
            printf("  [%s]\n", lbl);
            printf("    ArmA  maxrel=%.3e normrel=%.3e maxabs=%.3e bitwise=%zu/%zu\n", ca.maxrel,ca.normrel,ca.maxabs,ca.nbit,ca.ncmp);
            printf("    ArmV  maxrel=%.3e normrel=%.3e maxabs=%.3e bitwise=%zu/%zu\n", cv.maxrel,cv.normrel,cv.maxabs,cv.nbit,cv.ncmp);
            printf("    ArmT  maxrel=%.3e normrel=%.3e maxabs=%.3e bitwise=%zu/%zu\n", ct.maxrel,ct.normrel,ct.maxabs,ct.nbit,ct.ncmp);
            printf("    ArmB  maxrel=%.3e normrel=%.3e maxabs=%.3e bitwise=%zu/%zu\n", cb.maxrel,cb.normrel,cb.maxabs,cb.nbit,cb.ncmp);
        }
        return 0;
    }

    if (!strcmp(mode,"bench")){
        int n    = (argc>2)? atoi(argv[2]) : 192;
        int d    = (argc>3)? atoi(argv[3]) : 512;
        int reps = (argc>4)? atoi(argv[4]) : 3;
        int KC   = (argc>5)? atoi(argv[5]) : 64;
        int MRSEL= (argc>7)? atoi(argv[7]) : 6;
        const char* arms = (argc>6)? argv[6] : "RAVB";
        size_t total; size_t* base = make_base(n,&total);
        double* A = (double*)_mm_malloc((size_t)d*n*sizeof(double),64);
        double* C = (double*)_mm_malloc(total*sizeof(double),64);
        fill_int(A,(size_t)d*n);
        size_t ncalls, computed; krs_shape(n,d,KC,MRSEL,&ncalls,&computed);
        double alg = 3.0*(double)total*(double)d;
        double prefixes = (double)prefixes_r2(n);
        double hwB = 2.0*(double)computed*(double)d + prefixes*(double)d;
        /* exact issued-flop counts per arm (see report) */
        double vmul = 0.0;
        for (int i=0;i<n;i++) for (int j=i;j<n;j++){ int L=n-j; vmul += L/4 + L%4; }
        double issV = 2.0*(double)total*(double)d + vmul*(double)d;
        double issT = 2.0*(double)total*(double)d + prefixes*(double)d;
        /* self-normalising peak probe: this host throttles under sibling load,
         * so % of peak is only meaningful against a concurrently measured peak */
        double pk = peak_probe();
        { double p2 = peak_probe(); if (p2 > pk) pk = p2; }
        printf("  concurrent AVX2 peak probe: %.2f GFLOP/s (%.2f GHz implied)\n", pk, pk/16.0);
        printf("bench n=%d d=%d reps=%d KC=%d cells=%zu (%.2f MB) prefixes=%.0f\n",
               n,d,reps,KC,total,total*8.0/1048576.0,prefixes);
        printf("  alg flops = 3*cells*d = %.3f GFLOP ; ArmB issued = %.3f GFLOP (lane waste %.1f%%)\n",
               alg/1e9, hwB/1e9, 100.0*((double)computed-(double)total)/(double)total);
        for (const char* a=arms; *a; a++){
            double best = 1e30; const char* name=""; double iss = alg;
            for (int r=0;r<reps;r++){
                memset(C,0,total*sizeof(double));
                __asm__ __volatile__("" ::: "memory");   /* no hoisting out of the rep loop */
                double t0=wall();
                switch(*a){
                    case 'R': ref_r3 (A,d,n,base,C);    name="R0 reference     "; iss=alg;  break;
                    case 'A': armA_r3(A,d,n,base,C);    name="A  multi-acc x8  "; iss=alg;  break;
                    case 'V': armV_r3(A,d,n,base,C);    name="V  vec-k 1 chain "; iss=issV; break;
                    case 'T': armT_r3(A,d,n,base,C);    name="T  t-outer rank-1"; iss=issT; break;
                    case 'B': krs_r3 (A,d,n,base,C,KC,MRSEL); name="B  KRS reg-block "; iss=hwB; break;
                    default: continue;
                }
                double t1=wall();
                __asm__ __volatile__("" ::: "memory");
                if (t1-t0 < best) best = t1-t0;
            }
            if (best > 1e29) continue;
            /* observe the result so nothing can be dead-code eliminated */
            double chk = 0.0;
            for (size_t z=0; z<total; z += (total/9973)+1) chk += C[z];
            double nspc = best*1e9/(double)total;
            printf("  %s  t=%.4f s  %8.2f ns/cell   alg %6.2f GF/s   issued %6.2f GF/s  (%4.1f%% of peak)  chk=%.6g\n",
                   name, best, nspc, alg/best/1e9, iss/best/1e9, 100.0*(iss/best)/(pk*1e9), chk);
            fflush(stdout);
        }
        return 0;
    }
    fprintf(stderr,"usage: krs [peak|verify n d KC|bench n d reps KC arms]\n");
    return 1;
}
