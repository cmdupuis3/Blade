/* chains.c -- isolate accumulator-chain scaling from memory bandwidth.
 *
 * Flat contiguous (+) fold over `cells` doubles with NACC independent YMM
 * accumulators. No row structure, so row length never confounds the result.
 * Sweep NACC = 1,2,4,8,12 across L1 / L2 / L3 / DRAM resident buffers.
 *
 * Reads the roofline: in L1 the fold is latency-bound and should scale ~linearly
 * with NACC until vaddpd throughput (2/cycle) saturates; in DRAM it is
 * bandwidth-bound and NACC should stop mattering entirely.
 */
#include <stdio.h>
#include <stdlib.h>
#include <stdint.h>
#include <string.h>
#include <immintrin.h>
#include <windows.h>

typedef long long i64;
#define NOINL __attribute__((noinline))
#define BARRIER_MEM()   __asm__ __volatile__("" ::: "memory")
#define BARRIER_VAL(v)  __asm__ __volatile__("" : "+x"(v) :: "memory")

static double now_s(void){
    LARGE_INTEGER f,c; QueryPerformanceFrequency(&f); QueryPerformanceCounter(&c);
    return (double)c.QuadPart/(double)f.QuadPart;
}
static inline double hsum256(__m256d v){
    __m128d lo=_mm256_castpd256_pd128(v), hi=_mm256_extractf128_pd(v,1);
    __m128d s=_mm_add_pd(lo,hi), sh=_mm_unpackhi_pd(s,s);
    return _mm_cvtsd_f64(_mm_add_sd(s,sh));
}

#define DEF_FLAT(NAME, NACC)                                                   \
NOINL static double NAME(const double * __restrict p, i64 cells){              \
    __m256d a[NACC];                                                           \
    for (int t=0;t<NACC;t++) a[t]=_mm256_setzero_pd();                         \
    const i64 W = 4*(NACC);                                                    \
    i64 k = 0;                                                                 \
    for (; k + W <= cells; k += W)                                             \
        for (int t=0;t<NACC;t++)                                               \
            a[t]=_mm256_add_pd(a[t],_mm256_loadu_pd(p+k+4*t));                 \
    double tail=0.0;                                                           \
    for (; k<cells; k++) tail+=p[k];                                           \
    __m256d s=a[0];                                                            \
    for (int t=1;t<NACC;t++) s=_mm256_add_pd(s,a[t]);                          \
    return hsum256(s)+tail;                                                    \
}
DEF_FLAT(flat1 ,1)
DEF_FLAT(flat2 ,2)
DEF_FLAT(flat4 ,4)
DEF_FLAT(flat8 ,8)
DEF_FLAT(flat12,12)

/* scalar single-chain, for reference (what Arm A is) */
NOINL static double flat_scalar(const double * __restrict p, i64 cells){
    double acc=0.0;
    for (i64 k=0;k<cells;k++) acc+=p[k];
    return acc;
}

typedef double (*fn)(const double*, i64);

int main(void){
    struct { const char*name; i64 bytes; } tiers[] = {
        {"L1   (16 KB)",    16*1024},
        {"L2   (512 KB)",   512*1024},
        {"L3   (8 MB)",     8*1024*1024},
        {"DRAM (128 MB)",   128*1024*1024},
    };
    struct { const char*name; fn f; int nacc; } arms[] = {
        {"scalar", flat_scalar, 0},
        {"1 YMM",  flat1,  1},
        {"2 YMM",  flat2,  2},
        {"4 YMM",  flat4,  4},
        {"8 YMM",  flat8,  8},
        {"12 YMM", flat12, 12},
    };
    printf("flat contiguous (+) fold: accumulator-chain sweep\n");
    printf("(ns/cell, min of reps; ratio = speedup over the 1-YMM single chain)\n");

    for (int t=0;t<4;t++){
        i64 cells = tiers[t].bytes/8;
        double *p = (double*)_mm_malloc((size_t)cells*8, 64);
        if(!p){printf("alloc fail\n");return 1;}
        for (i64 k=0;k<cells;k++) p[k]=(double)(k&7);
        /* Batch the inner reps inside ONE timing window: at the L1 size a single
         * fold takes ~60 ns while two QueryPerformanceCounter calls cost more
         * than that, so per-rep timing would measure the clock, not the kernel. */
        i64 inner = 20000000LL/cells; if (inner<1) inner=1;
        int outer = 9;
        printf("\n%s  cells=%lld  inner=%lld  outer=%d\n", tiers[t].name, cells, inner, outer);
        double base=0;
        for (int a=0;a<6;a++){
            double best=1e30, v=0;
            for (int r=0;r<outer;r++){
                BARRIER_MEM();
                double t0=now_s();
                for (i64 q=0;q<inner;q++){
                    v = arms[a].f(p, cells);
                    BARRIER_VAL(v);
                    BARRIER_MEM();
                }
                double t1=now_s();
                if ((t1-t0)/(double)inner < best) best=(t1-t0)/(double)inner;
            }
            double nspc = best*1e9/(double)cells;
            if (arms[a].nacc==1) base=nspc;
            printf("   %-7s %8.4f ns/cell  %8.2f GB/s", arms[a].name, nspc, (double)cells*8.0/best/1e9);
            if (base>0 && arms[a].nacc!=1) printf("   %5.2fx vs 1YMM", base/nspc);
            printf("   [sum=%.0f]\n", v);
        }
        _mm_free(p);
    }
    return 0;
}
