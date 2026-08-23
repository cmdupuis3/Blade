// jam_lane_model.cpp -- what actually binds the unroll-and-jam knee.
//
// README correction 16 says: "The jam's knee is SHUFFLE THROUGHPUT, not
// accumulator count.  gcc transposes the R x 4 tile and runs R-lane vaddpd in
// k-order, so the binding resource is shuffles per iteration (R=4: 16, R=6: 23,
// R=8: 32).  That puts the peak at R=5-6 and makes it fall back by 8."
//
// This kernel tests that mechanism claim directly.  Static analysis of gcc
// 15.2's own emission (see REPORT.md) says the per-iteration budget is
//
//     recurrence II = 12 cycles at EVERY R   (4 loop-carried vaddpd x 3 cyc,
//                                             i.e. exactly 3 cyc per k -- the
//                                             fold's own latency, unchanged)
//     port II       = 10 / 11.5 / 13.5 / 20 / 30 cycles at R = 4/5/6/8/12
//
// so cyc/MAC = max(3/R, portII/(4R)).  The first term FALLS with R, the second
// is FLAT per MAC (~0.6).  A flat resource cannot make a peak: the knee is the
// CROSSOVER, and shuffles only set the FLOOR.
//
// The decisive experiment: pack B into R-wide panels so the R-lane accumulator
// needs NO transpose at all.  Then
//   * at R=4 the chain still binds, so jamT4 should be NO FASTER than jam4
//     (refuting "shuffles bind at R=4"), and
//   * at R>=8 the ports were binding, so jamT8/jamT12/jamT16 should keep
//     improving well past the R=5-6 "knee" (refuting "peak at R=5-6" as a
//     property of the transform rather than of gcc's transpose).
//
// Every arm accumulates lane = OUTPUT CELL j with k strictly ascending, so all
// of them are bitwise-identical to the scalar base by construction; the
// panel-packed arms differ only in how the R operands reach the lanes.
//
// Operands are FULL-MANTISSA random (correction 17).  base_fma is the live
// control and MUST read NO on gcc; if it reads yes the column is inert.
//
//   g++ -O3 -march=native -ffp-contract=fast -std=c++17 -o jlm.exe jam_lane_model.cpp
//   ./jlm.exe <rounds> <reps>
//   -DMM=301 -DNN=257 -DPP=303 to change the shape.

#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <chrono>
#include <algorithm>
#include <vector>
#include <cmath>
#include <immintrin.h>

#ifndef MM
#define MM 301
#endif
#ifndef NN
#define NN 257
#endif
#ifndef PP
#define PP 303
#endif

#define BR __restrict__
static inline double conj_scalar(double x) { return x; }

typedef std::chrono::high_resolution_clock clk;
static double secs(clk::time_point a, clk::time_point b) {
    return 1e-9 * std::chrono::duration_cast<std::chrono::nanoseconds>(b - a).count();
}

static double* poolA; static double* poolB; static double* Cpool;
static double** A; static double** B; static double** C;
static double* Bp4;  static double* Bp8; static double* Bp12; static double* Bp16;

static void pack_into(double* dst, int RW) {
    int np = (PP + RW - 1) / RW;
    for (int p = 0; p < np; p++)
        for (int k = 0; k < NN; k++)
            for (int l = 0; l < RW; l++) {
                int j = p * RW + l;
                dst[((size_t)p * NN + k) * RW + l] = (j < PP) ? B[j][k] : 0.0;
            }
}

static void setup() {
    poolA = (double*)_mm_malloc(sizeof(double) * (size_t)MM * NN, 64);
    poolB = (double*)_mm_malloc(sizeof(double) * (size_t)PP * NN, 64);
    Cpool = (double*)_mm_malloc(sizeof(double) * (size_t)MM * PP, 64);
    A = (double**)malloc(sizeof(double*) * MM);
    B = (double**)malloc(sizeof(double*) * PP);
    C = (double**)malloc(sizeof(double*) * MM);
    unsigned long long s = 0x243F6A8885A308D3ULL;
#define RND ((s ^= s << 13, s ^= s >> 7, s ^= s << 17), (double)(s >> 11) * (1.0 / 9007199254740992.0) + 0.5)
    for (long i = 0; i < MM; i++) { A[i] = poolA + (size_t)i * NN;
        for (long k = 0; k < NN; k++) A[i][k] = RND; }
    for (long j = 0; j < PP; j++) { B[j] = poolB + (size_t)j * NN;
        for (long k = 0; k < NN; k++) B[j][k] = RND; }
    for (long i = 0; i < MM; i++) C[i] = Cpool + (size_t)i * PP;
    memset(Cpool, 0, sizeof(double) * (size_t)MM * PP);
    Bp4  = (double*)_mm_malloc(sizeof(double) * (size_t)((PP+3)/4)  * NN * 4,  64);
    Bp8  = (double*)_mm_malloc(sizeof(double) * (size_t)((PP+7)/8)  * NN * 8,  64);
    Bp12 = (double*)_mm_malloc(sizeof(double) * (size_t)((PP+11)/12)* NN * 12, 64);
    Bp16 = (double*)_mm_malloc(sizeof(double) * (size_t)((PP+15)/16)* NN * 16, 64);
    pack_into(Bp4,4); pack_into(Bp8,8); pack_into(Bp12,12); pack_into(Bp16,16);
}

// ------------------------------------------------------ compiler-chosen arms
static void k_base() {
    for (size_t gi = 0; gi < MM; gi++) {
        const double* BR ri = &A[gi][0];
        for (size_t gj = 0; gj < PP; gj++) {
            const double* BR rj = &B[gj][0];
            double acc = double();
            for (size_t gk = 0; gk < NN; gk++) acc += ri[gk] * conj_scalar(rj[gk]);
            C[gi][gj] = acc;
        }
    }
}
static void k_base_fma() {   // LIVE CONTROL: must differ in bits from k_base
    for (size_t gi = 0; gi < MM; gi++) {
        const double* BR ri = &A[gi][0];
        for (size_t gj = 0; gj < PP; gj++) {
            const double* BR rj = &B[gj][0];
            double acc = double();
            for (size_t gk = 0; gk < NN; gk++) acc = std::fma(ri[gk], conj_scalar(rj[gk]), acc);
            C[gi][gj] = acc;
        }
    }
}
#define JD(r)  const double* BR rj##r = &B[gj + r][0]; double acc##r = double();
#define JA(r)  acc##r += ri[gk] * conj_scalar(rj##r[gk]);
#define JS(r)  C[gi][gj + r] = acc##r;
#define MAKEJAM(R, D, AC, S)                                                \
static void k_jam##R() {                                                    \
    for (size_t gi = 0; gi < MM; gi++) {                                    \
        const double* BR ri = &A[gi][0];                                    \
        size_t gj = 0;                                                      \
        for (; gj + R <= PP; gj += R) {                                     \
            D                                                               \
            for (size_t gk = 0; gk < NN; gk++) { AC }                       \
            S                                                               \
        }                                                                   \
        for (; gj < PP; gj++) {                                             \
            const double* BR rj = &B[gj][0]; double acc = double();         \
            for (size_t gk = 0; gk < NN; gk++) acc += ri[gk] * conj_scalar(rj[gk]); \
            C[gi][gj] = acc;                                                \
        }                                                                   \
    }                                                                       \
}
MAKEJAM(4,  JD(0)JD(1)JD(2)JD(3),                     JA(0)JA(1)JA(2)JA(3),                     JS(0)JS(1)JS(2)JS(3))
MAKEJAM(5,  JD(0)JD(1)JD(2)JD(3)JD(4),                JA(0)JA(1)JA(2)JA(3)JA(4),                JS(0)JS(1)JS(2)JS(3)JS(4))
MAKEJAM(6,  JD(0)JD(1)JD(2)JD(3)JD(4)JD(5),           JA(0)JA(1)JA(2)JA(3)JA(4)JA(5),           JS(0)JS(1)JS(2)JS(3)JS(4)JS(5))
MAKEJAM(8,  JD(0)JD(1)JD(2)JD(3)JD(4)JD(5)JD(6)JD(7), JA(0)JA(1)JA(2)JA(3)JA(4)JA(5)JA(6)JA(7), JS(0)JS(1)JS(2)JS(3)JS(4)JS(5)JS(6)JS(7))
MAKEJAM(12, JD(0)JD(1)JD(2)JD(3)JD(4)JD(5)JD(6)JD(7)JD(8)JD(9)JD(10)JD(11),
            JA(0)JA(1)JA(2)JA(3)JA(4)JA(5)JA(6)JA(7)JA(8)JA(9)JA(10)JA(11),
            JS(0)JS(1)JS(2)JS(3)JS(4)JS(5)JS(6)JS(7)JS(8)JS(9)JS(10)JS(11))
MAKEJAM(16, JD(0)JD(1)JD(2)JD(3)JD(4)JD(5)JD(6)JD(7)JD(8)JD(9)JD(10)JD(11)JD(12)JD(13)JD(14)JD(15),
            JA(0)JA(1)JA(2)JA(3)JA(4)JA(5)JA(6)JA(7)JA(8)JA(9)JA(10)JA(11)JA(12)JA(13)JA(14)JA(15),
            JS(0)JS(1)JS(2)JS(3)JS(4)JS(5)JS(6)JS(7)JS(8)JS(9)JS(10)JS(11)JS(12)JS(13)JS(14)JS(15))

// ------------------------------------------ panel-packed arms: NO transpose
// lane l of group g holds output cell j = p*RW + 4g + l, k strictly ascending,
// separate vmulpd + vaddpd (NOT fma) so the bits match the scalar base exactly.
// Blocking contraction portably: gcc honours the per-function attribute, clang
// fuses the two intrinsics in the backend regardless of `#pragma clang fp
// contract(off)`, so it needs a zero-instruction asm tie on the product.  Both
// forms are verified in the disassembly (jamT bodies contain no vfmadd*).
#if defined(__clang__)
#define NOCONTRACT_ATTR
#define NOCONTRACT_PRAGMA
#define NOFUSE(v) asm("" : "+x"(v))
#else
#define NOCONTRACT_ATTR __attribute__((optimize("fp-contract=off")))
#define NOCONTRACT_PRAGMA
#define NOFUSE(v) ((void)0)
#endif
#define MAKEJAMT(RW, NG)                                                        \
NOCONTRACT_ATTR                                                                 \
static void k_jamT##RW() {                                                      \
    NOCONTRACT_PRAGMA                                                           \
    const int np = (PP + RW - 1) / RW;                                          \
    for (size_t gi = 0; gi < MM; gi++) {                                        \
        const double* BR ri = &A[gi][0];                                        \
        for (int p = 0; p < np; p++) {                                          \
            const double* BR bp = Bp##RW + (size_t)p * NN * RW;                 \
            __m256d a[NG];                                                      \
            for (int g = 0; g < NG; g++) a[g] = _mm256_setzero_pd();            \
            for (size_t gk = 0; gk < NN; gk++) {                                \
                __m256d av = _mm256_broadcast_sd(ri + gk);                      \
                const double* q = bp + gk * RW;                                 \
                for (int g = 0; g < NG; g++) {                                  \
                    __m256d pr = _mm256_mul_pd(av, _mm256_load_pd(q + 4*g));    \
                    NOFUSE(pr);                                                 \
                    a[g] = _mm256_add_pd(a[g], pr);                             \
                }                                                               \
            }                                                                   \
            double out[RW] __attribute__((aligned(64)));                        \
            for (int g = 0; g < NG; g++) _mm256_store_pd(out + 4*g, a[g]);      \
            for (int l = 0; l < RW; l++) { int j = p*RW + l;                    \
                if (j < PP) C[gi][j] = out[l]; }                                \
        }                                                                       \
    }                                                                           \
}
MAKEJAMT(4,1)
MAKEJAMT(8,2)
MAKEJAMT(12,3)
MAKEJAMT(16,4)

// ---------------------------------------------------------------- clock
static double calib_clock() {
    const long iters = 200000000L;
    double x = 1.0, c = 1.0000001;
    auto t0 = clk::now();
    for (long i = 0; i < iters; i += 8) {
        asm volatile("vaddsd %1, %0, %0" : "+x"(x) : "x"(c));
        asm volatile("vaddsd %1, %0, %0" : "+x"(x) : "x"(c));
        asm volatile("vaddsd %1, %0, %0" : "+x"(x) : "x"(c));
        asm volatile("vaddsd %1, %0, %0" : "+x"(x) : "x"(c));
        asm volatile("vaddsd %1, %0, %0" : "+x"(x) : "x"(c));
        asm volatile("vaddsd %1, %0, %0" : "+x"(x) : "x"(c));
        asm volatile("vaddsd %1, %0, %0" : "+x"(x) : "x"(c));
        asm volatile("vaddsd %1, %0, %0" : "+x"(x) : "x"(c));
    }
    auto t1 = clk::now();
    if (x == 12345.0) printf("");
    return 3.0 * iters / secs(t0, t1);
}

struct K { const char* name; void (*fn)(); };
static K kernels[] = {
    {"base",    k_base},   {"base_fma", k_base_fma},
    {"jam4",    k_jam4},   {"jam5",  k_jam5},  {"jam6",  k_jam6},
    {"jam8",    k_jam8},   {"jam12", k_jam12}, {"jam16", k_jam16},
    {"jamT4",   k_jamT4},  {"jamT8", k_jamT8}, {"jamT12", k_jamT12}, {"jamT16", k_jamT16},
};
static const int NK = sizeof(kernels)/sizeof(kernels[0]);

int main(int argc, char** argv) {
    int rounds = argc > 1 ? atoi(argv[1]) : 4;
    int reps   = argc > 2 ? atoi(argv[2]) : 3;
    setup();
    double hz = calib_clock();
    size_t bytes = sizeof(double) * (size_t)MM * PP;
    double* ref = (double*)malloc(bytes);
    int bitok[NK];
    for (int i = 0; i < NK; i++) {
        memset(Cpool, 0xA5, bytes);
        asm volatile("" ::: "memory");
        kernels[i].fn();
        asm volatile("" ::: "memory");
        if (i == 0) { memcpy(ref, Cpool, bytes); bitok[0] = 1; }
        else {
            bitok[i] = (memcmp(ref, Cpool, bytes) == 0);
#ifdef DUMPBITS
            if (!bitok[i]) { long nd=0, first=-1;
                for (size_t z=0; z<(size_t)MM*PP; z++) if (memcmp(ref+z,Cpool+z,8)) { if(first<0) first=(long)z; nd++; }
                printf("  %-8s differs in %ld/%d cells, first at %ld (i=%ld j=%ld): ref=%.17g got=%.17g ulp=%.3e",
                       kernels[i].name, nd, MM*PP, first, first/PP, first%PP, ref[first], Cpool[first],
                       ref[first]-Cpool[first]);
                putchar(10); }
#endif
        }
    }
    std::vector<std::vector<double>> t(NK);
    for (int r = 0; r < rounds; r++)
        for (int i = 0; i < NK; i++)
            for (int q = 0; q < reps; q++) {
                asm volatile("" ::: "memory");
                auto t0 = clk::now(); kernels[i].fn(); auto t1 = clk::now();
                asm volatile("" ::: "memory");
                t[i].push_back(secs(t0, t1));
            }
    double chk = 0.0;
    for (size_t z = 0; z < (size_t)MM*PP; z += 9973) chk += Cpool[z];
    double macs = (double)MM * NN * PP;
    printf("# m=%d n=%d p=%d  clock=%.3f GHz  macs=%.4g  samples=%d  chk=%.10g\n",
           MM, NN, PP, hz/1e9, macs, rounds*reps, chk);
    printf("%-9s %11s %9s %9s %9s %9s\n", "kernel","median_s","cyc/MAC","GMAC/s","vs_base","bitwise");
    double base = 0;
    for (int i = 0; i < NK; i++) {
        std::sort(t[i].begin(), t[i].end());
        double m = t[i][t[i].size()/2];
        if (i == 0) base = m;
        printf("%-9s %11.6f %9.4f %9.4f %9.4f %9s\n", kernels[i].name, m,
               m*hz/macs, macs/m/1e9, base/m, bitok[i] ? "yes" : "NO");
    }
    return 0;
}
