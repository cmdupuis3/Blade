/* probe_chain_depth.c -- fills the hole in probe_accumulator_chains.c and tests
 * whether corrections 1 and 3 generalise past `double`.
 *
 * README correction 3: "Saturation needs ~6 (3-cycle latency x 2 pipes), so 8."
 * The shipped probe measures NACC = 1,2,4,8,12 -- it never measures 6, which is
 * exactly the predicted saturation point, and it never measures 5,7 either, so
 * the knee is asserted from a model rather than located.  Section A sweeps
 * NACC = 1..8,10,12,16.
 *
 * README correction 1: "[multi-accumulator] captures ZERO ... multi-accumulator
 * pays 1.71x when operands are L1-resident, 1.03x at L2, 1.00x at L3."  The
 * instrument that produced 1.71/1.03/1.00 is not in the repo (the shipped probe
 * is a FLAT fold, not a former nest, and reads 4.86x at L1).  Section B is a
 * reproducible former-nest version of that sweep, and runs it for `float` as
 * well as `double` -- float halves the bytes per operand and doubles the lanes
 * per register, so if the nest is operand-supply-bound the ratio must move.
 *
 * Extents are deliberately NON-power-of-two (CLAUDE.md benchmark discipline);
 * the shipped probe uses 16 KB / 512 KB / 8 MB / 128 MB exactly.
 *
 *   gcc -O3 -march=native -ffp-contract=fast -o pcd.exe probe_chain_depth.c -lm
 *   ./pcd.exe A      # accumulator sweep
 *   ./pcd.exe B      # former-nest multi-accumulator sweep
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
#define BARRIER_MEM()   __asm__ __volatile__("" ::: "memory")
#define BARRIER_VAL(v)  __asm__ __volatile__("" : "+x"(v) :: "memory")

static double wall(void){
    LARGE_INTEGER f,c; QueryPerformanceFrequency(&f); QueryPerformanceCounter(&c);
    return (double)c.QuadPart/(double)f.QuadPart;
}
static inline double hsum256(__m256d v){
    __m128d lo=_mm256_castpd256_pd128(v), hi=_mm256_extractf128_pd(v,1);
    __m128d s=_mm_add_pd(lo,hi), sh=_mm_unpackhi_pd(s,s);
    return _mm_cvtsd_f64(_mm_add_sd(s,sh));
}
static inline float hsum256f(__m256 v){
    __m128 lo=_mm256_castps256_ps128(v), hi=_mm256_extractf128_ps(v,1);
    __m128 s=_mm_add_ps(lo,hi);
    s=_mm_add_ps(s,_mm_movehl_ps(s,s));
    s=_mm_add_ss(s,_mm_shuffle_ps(s,s,1));
    return _mm_cvtss_f32(s);
}

/* in-process clock: a strictly dependent vaddsd chain, Zen 3 FADD latency 3 */
static double calib_hz(void){
    const long iters=100000000L; double x=1.0,c=1.0000001;
    double t0=wall();
    for (long i=0;i<iters;i+=4){
        __asm__ __volatile__("vaddsd %1, %0, %0":"+x"(x):"x"(c));
        __asm__ __volatile__("vaddsd %1, %0, %0":"+x"(x):"x"(c));
        __asm__ __volatile__("vaddsd %1, %0, %0":"+x"(x):"x"(c));
        __asm__ __volatile__("vaddsd %1, %0, %0":"+x"(x):"x"(c));
    }
    double t1=wall(); if (x==12345.0) printf("");
    return 3.0*iters/(t1-t0);
}

/* --------------------------------------------------------------- section A */
#define DEF_FLAT_D(NAME, NACC)                                                 \
NOINL static double NAME(const double * __restrict p, i64 cells){              \
    __m256d a[NACC];                                                           \
    for (int t=0;t<NACC;t++) a[t]=_mm256_setzero_pd();                         \
    const i64 W = 4*(NACC); i64 k = 0;                                         \
    for (; k + W <= cells; k += W)                                             \
        for (int t=0;t<NACC;t++) a[t]=_mm256_add_pd(a[t],_mm256_loadu_pd(p+k+4*t)); \
    double tail=0.0; for (; k<cells; k++) tail+=p[k];                          \
    __m256d s=a[0]; for (int t=1;t<NACC;t++) s=_mm256_add_pd(s,a[t]);          \
    return hsum256(s)+tail; }
DEF_FLAT_D(d1,1) DEF_FLAT_D(d2,2) DEF_FLAT_D(d3,3) DEF_FLAT_D(d4,4)
DEF_FLAT_D(d5,5) DEF_FLAT_D(d6,6) DEF_FLAT_D(d7,7) DEF_FLAT_D(d8,8)
DEF_FLAT_D(d10,10) DEF_FLAT_D(d12,12) DEF_FLAT_D(d16,16)

#define DEF_FLAT_F(NAME, NACC)                                                 \
NOINL static double NAME(const float * __restrict p, i64 cells){               \
    __m256 a[NACC];                                                            \
    for (int t=0;t<NACC;t++) a[t]=_mm256_setzero_ps();                         \
    const i64 W = 8*(NACC); i64 k = 0;                                         \
    for (; k + W <= cells; k += W)                                             \
        for (int t=0;t<NACC;t++) a[t]=_mm256_add_ps(a[t],_mm256_loadu_ps(p+k+8*t)); \
    float tail=0.0f; for (; k<cells; k++) tail+=p[k];                          \
    __m256 s=a[0]; for (int t=1;t<NACC;t++) s=_mm256_add_ps(s,a[t]);           \
    return (double)(hsum256f(s)+tail); }
DEF_FLAT_F(f1,1) DEF_FLAT_F(f2,2) DEF_FLAT_F(f4,4) DEF_FLAT_F(f6,6)
DEF_FLAT_F(f8,8) DEF_FLAT_F(f12,12) DEF_FLAT_F(f16,16)

typedef double (*fnd)(const double*, i64);
typedef double (*fnf)(const float*, i64);

/* Two cell counts per tier.  `pow2` is EXACTLY what probe_accumulator_chains.c
 * uses (16 KB / 512 KB / 8 MB / 128 MB, i.e. 2^k doubles).  `clean` is the
 * largest multiple of 960 below it -- 960 = lcm(4*NACC) over the swept widths,
 * so EVERY arm's vector loop divides the buffer exactly and the scalar
 * remainder (a strictly dependent `tail += p[k]` chain, up to 4*NACC-1 long)
 * disappears.  Any arm that improves from pow2 to clean was measuring its own
 * remainder, not its chain count. */
static void sectionA(double hz){
    struct { const char* nm; i64 pow2; } tiers[] = {
        {"L1   (16 KB)",       2048},
        {"L2  (512 KB)",      65536},
        {"L3    (8 MB)",    1048576},
        {"DRAM (128 MB)",  16777216},
    };
    struct { int n; fnd f; } dd[] = {{1,d1},{2,d2},{3,d3},{4,d4},{5,d5},{6,d6},{8,d8},{10,d10},{12,d12},{16,d16}};
    struct { int n; fnf f; } ff[] = {{1,f1},{2,f2},{4,f4},{6,f6},{8,f8},{12,f12},{16,f16}};
    const int ND = 10, NF = 7;
    printf("== Section A: flat contiguous (+) fold, accumulator sweep ==\n");
    printf("   clock %.3f GHz.  cyc/elem (ratio vs 1 accumulator).\n", hz/1e9);
    for (int t=0;t<4;t++){
        for (int variant=0; variant<2; variant++){
            i64 cells = tiers[t].pow2;
            if (variant==1) cells = (cells/960)*960;
            double* p = (double*)_mm_malloc((size_t)cells*8,64);
            float*  q = (float*)_mm_malloc((size_t)cells*2*4,64);
            for (i64 k=0;k<cells;k++)  p[k]=(double)(k&7)+0.5;
            for (i64 k=0;k<cells*2;k++) q[k]=(float)(k&7)+0.5f;
            i64 inner = 24000000LL/cells; if (inner<1) inner=1;
            printf("\n  %s  %-5s cells=%lld inner=%lld\n", tiers[t].nm,
                   variant? "clean":"pow2", (long long)cells, (long long)inner);
            printf("   double: ");
            double b=0, sink=0;
            for (int a=0;a<ND;a++){
                double best=1e30,v=0;
                for (int r=0;r<9;r++){
                    BARRIER_MEM(); double t0=wall();
                    for (i64 z=0;z<inner;z++){ v=dd[a].f(p,cells); BARRIER_VAL(v); BARRIER_MEM(); }
                    double t1=wall(); if ((t1-t0)/inner<best) best=(t1-t0)/inner;
                }
                double cyc = best*hz/(double)cells;
                if (dd[a].n==1) b=cyc;
                sink += v;
                printf("%d:%.3f(%.2fx) ", dd[a].n, cyc, b/cyc);
            }
            printf("\n   float : ");
            b=0;
            for (int a=0;a<NF;a++){
                double best=1e30,v=0;
                for (int r=0;r<9;r++){
                    BARRIER_MEM(); double t0=wall();
                    for (i64 z=0;z<inner;z++){ v=ff[a].f(q,cells*2); BARRIER_VAL(v); BARRIER_MEM(); }
                    double t1=wall(); if ((t1-t0)/inner<best) best=(t1-t0)/inner;
                }
                double cyc = best*hz/(double)(cells*2);
                if (ff[a].n==1) b=cyc;
                sink += v;
                printf("%d:%.3f(%.2fx) ", ff[a].n, cyc, b/cyc);
            }
            printf("  [sink=%.6g]\n", sink);
            fflush(stdout);
            _mm_free(p); _mm_free(q);
        }
    }
}

/* --------------------------------------------------------------- section B */
/* the naive rank-3 symmetric former nest, and the same nest with the t-fold
 * split across 8 independent accumulators -- correction 1's subject.          */
#define DEF_FORMER(T, SUF)                                                     \
NOINL static void ref_##SUF(const T* restrict A, int d, int n, T* restrict C){ \
    size_t o=0;                                                                \
    for (int i=0;i<n;i++) for (int j=i;j<n;j++){                               \
        for (int k=j;k<n;k++){                                                 \
            T acc=0;                                                           \
            for (int t=0;t<d;t++){ const T* r=A+(size_t)t*n; acc += r[i]*r[j]*r[k]; } \
            C[o+(size_t)(k-j)] += acc; }                                       \
        o += (size_t)(n-j); } }                                                \
NOINL static void acc8_##SUF(const T* restrict A, int d, int n, T* restrict C){\
    size_t o=0;                                                                \
    for (int i=0;i<n;i++) for (int j=i;j<n;j++){                               \
        for (int k=j;k<n;k++){                                                 \
            T a0=0,a1=0,a2=0,a3=0,a4=0,a5=0,a6=0,a7=0; int t=0;                \
            for (; t+8<=d; t+=8){                                              \
                const T* r0=A+(size_t)(t  )*n; const T* r1=A+(size_t)(t+1)*n;  \
                const T* r2=A+(size_t)(t+2)*n; const T* r3=A+(size_t)(t+3)*n;  \
                const T* r4=A+(size_t)(t+4)*n; const T* r5=A+(size_t)(t+5)*n;  \
                const T* r6=A+(size_t)(t+6)*n; const T* r7=A+(size_t)(t+7)*n;  \
                a0+=r0[i]*r0[j]*r0[k]; a1+=r1[i]*r1[j]*r1[k];                  \
                a2+=r2[i]*r2[j]*r2[k]; a3+=r3[i]*r3[j]*r3[k];                  \
                a4+=r4[i]*r4[j]*r4[k]; a5+=r5[i]*r5[j]*r5[k];                  \
                a6+=r6[i]*r6[j]*r6[k]; a7+=r7[i]*r7[j]*r7[k]; }                \
            for (; t<d; t++){ const T* r=A+(size_t)t*n; a0+=r[i]*r[j]*r[k]; }  \
            C[o+(size_t)(k-j)] += ((a0+a1)+(a2+a3))+((a4+a5)+(a6+a7)); }       \
        o += (size_t)(n-j); } }
DEF_FORMER(double, d)
DEF_FORMER(float, f)

static unsigned long long rs = 0x853c49e6748fea9bULL;
static double ur(void){ rs = rs*6364136223846793005ULL + 1442695040888963407ULL;
                        return (double)((rs>>11)&((1ULL<<53)-1))/(double)(1ULL<<53); }

static void sectionB(void){
    struct { const char* nm; int n; int d; int reps; } cases[] = {
        {"L1   ( 18 KB)", 23,   97, 25},
        {"L1/2 (128 KB)", 31,  521,  9},
        {"L2   (327 KB)", 41,  997,  5},
        {"L3   (1.9 MB)", 61, 4001,  3},
        {"L3   (6.1 MB)", 79, 9973,  3},
    };
    printf("\n== Section B: naive rank-3 former nest, 1 accumulator vs 8 ==\n");
    printf("   correction 1's subject.  ratio > 1 means multi-accumulator pays.\n");
    printf("   %-14s %6s %6s %10s %10s %8s | %10s %10s %8s\n",
           "tier","n","d","dbl ref s","dbl acc8 s","ratio","flt ref s","flt acc8 s","ratio");
    for (int c=0;c<5;c++){
        int n=cases[c].n, d=cases[c].d, reps=cases[c].reps;
        size_t cells=(size_t)n*(n+1)*(n+2)/6;
        double* Ad=(double*)_mm_malloc((size_t)d*n*8,64);
        float*  Af=(float*)_mm_malloc((size_t)d*n*4,64);
        double* Cd=(double*)_mm_malloc(cells*8,64);
        float*  Cf=(float*)_mm_malloc(cells*4,64);
        for (size_t z=0;z<(size_t)d*n;z++){ double v=2.0*ur()-1.0; Ad[z]=v; Af[z]=(float)v; }
        double bd=1e30,ad=1e30,bf=1e30,af=1e30;
        for (int r=0;r<reps;r++){
            memset(Cd,0,cells*8); BARRIER_MEM(); double t0=wall(); ref_d (Ad,d,n,Cd); double t1=wall(); BARRIER_MEM(); if (t1-t0<bd) bd=t1-t0;
            memset(Cd,0,cells*8); BARRIER_MEM(); t0=wall(); acc8_d(Ad,d,n,Cd); t1=wall(); BARRIER_MEM(); if (t1-t0<ad) ad=t1-t0;
            memset(Cf,0,cells*4); BARRIER_MEM(); t0=wall(); ref_f (Af,d,n,Cf); t1=wall(); BARRIER_MEM(); if (t1-t0<bf) bf=t1-t0;
            memset(Cf,0,cells*4); BARRIER_MEM(); t0=wall(); acc8_f(Af,d,n,Cf); t1=wall(); BARRIER_MEM(); if (t1-t0<af) af=t1-t0;
        }
        double chk=0; for (size_t z=0;z<cells;z+=997) chk+=Cd[z]+(double)Cf[z];
        printf("   %-14s %6d %6d %10.5f %10.5f %8.3f | %10.5f %10.5f %8.3f   [%.6g]\n",
               cases[c].nm,n,d,bd,ad,bd/ad,bf,af,bf/af,chk);
        fflush(stdout);
        _mm_free(Ad);_mm_free(Af);_mm_free(Cd);_mm_free(Cf);
    }
}

int main(int argc, char** argv){
    const char* m = argc>1? argv[1] : "AB";
    double hz = calib_hz();
    if (strchr(m,'A')) sectionA(hz);
    if (strchr(m,'B')) sectionB();
    return 0;
}
