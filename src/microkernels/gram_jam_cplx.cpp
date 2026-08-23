// gram_jam_cplx.cpp -- the shipped jam at R=2..8 on COMPLEX and on FLOAT32.
//
// NOTE the premise this file was written under was FALSE.  It relied on a
// stale materializeGramForm comment claiming complex/float32 sit "outside the
// shim's f64 domain" and therefore always take the native nest.  They do not:
// precisionOf maps s/c/z and blade_linalg.hpp defines all four precision
// entry points, so complex routes to BLAS exactly like f64 does.  What this
// instrument measured anyway turned out to decide the emitter: complex peaks
// at 1.23x, REGRESSES at R >= 6, and is not bit-identical at R=2 -- which is
// why a3837e6 makes complex decline the jam entirely.
//
// Complex also changes the register budget: one std::complex<double>
// accumulator is 2 doubles, so R=5 costs 10 scalar registers of accumulator,
// and conj_scalar() is a real sign flip rather than the identity.
//
// -DELEM_CPLX (default) : std::complex<double>
// -DELEM_CPLXF          : std::complex<float>

#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <chrono>
#include <algorithm>
#include <vector>
#include <complex>

#ifndef MM
#define MM 301
#endif
#ifndef NN
#define NN 257
#endif
#ifndef PP
#define PP 303
#endif

#ifdef ELEM_CPLXF
typedef std::complex<float> ELEM;
typedef float REAL;
#define ELEMNAME "complex<float>"
#else
typedef std::complex<double> ELEM;
typedef double REAL;
#define ELEMNAME "complex<double>"
#endif

#define BR __restrict__

namespace nested_array_utilities {
    template <typename T> inline T conj_scalar(const T& x) { return x; }
    template <typename T> inline std::complex<T> conj_scalar(const std::complex<T>& x) { return std::conj(x); }
}
using nested_array_utilities::conj_scalar;

typedef std::chrono::high_resolution_clock clk;
static double secs(clk::time_point a, clk::time_point b) {
    return 1e-9 * std::chrono::duration_cast<std::chrono::nanoseconds>(b - a).count();
}

static ELEM* poolA; static ELEM* poolB; static ELEM* Cpool; static ELEM* poolBT;
static ELEM** A; static ELEM** B; static ELEM** C;

static void setup() {
    poolA = (ELEM*)malloc(sizeof(ELEM) * (size_t)MM * NN);
    poolB = (ELEM*)malloc(sizeof(ELEM) * (size_t)PP * NN);
    poolBT= (ELEM*)malloc(sizeof(ELEM) * (size_t)NN * PP);
    Cpool = (ELEM*)malloc(sizeof(ELEM) * (size_t)MM * PP);
    A = (ELEM**)malloc(sizeof(ELEM*) * MM);
    B = (ELEM**)malloc(sizeof(ELEM*) * PP);
    C = (ELEM**)malloc(sizeof(ELEM*) * MM);
    unsigned long long s = 0x243F6A8885A308D3ULL;
#define RND ((s ^= s << 13, s ^= s >> 7, s ^= s << 17), (REAL)((double)(s >> 11) * (1.0 / 9007199254740992.0) + 0.5))
    for (long i = 0; i < MM; i++) { A[i] = poolA + (size_t)i * NN; for (long k = 0; k < NN; k++) A[i][k] = ELEM(RND, RND); }
    for (long j = 0; j < PP; j++) { B[j] = poolB + (size_t)j * NN; for (long k = 0; k < NN; k++) B[j][k] = ELEM(RND, RND); }
    for (long i = 0; i < MM; i++) { C[i] = Cpool + (size_t)i * PP; }
    memset(Cpool, 0, sizeof(ELEM) * (size_t)MM * PP);
}
static void packBT() {
    for (long j = 0; j < PP; j++) for (long k = 0; k < NN; k++) poolBT[(size_t)k * PP + j] = B[j][k];
}

static void k_base() {
    for (size_t __gi = 0; __gi < MM; __gi++) {
        const ELEM* BR __growi = &A[__gi][0];
        for (size_t __gj = 0; __gj < PP; __gj++) {
            const ELEM* BR __growj = &B[__gj][0];
            ELEM __gacc = ELEM();
            for (size_t __gk = 0; __gk < NN; __gk++) __gacc += __growi[__gk] * conj_scalar(__growj[__gk]);
            C[__gi][__gj] = __gacc;
        }
    }
}

#define JD(r)  const ELEM* BR __growj##r = &B[__gj + r][0]; ELEM __gacc##r = ELEM();
#define JA(r)  __gacc##r += __growi[__gk] * conj_scalar(__growj##r[__gk]);
#define JS(r)  C[__gi][__gj + r] = __gacc##r;
#define MKJAM(R, DECLS, ACCS, STORES)                                        \
static void k_jam##R() {                                                     \
    for (size_t __gi = 0; __gi < MM; __gi++) {                               \
        const ELEM* BR __growi = &A[__gi][0];                                \
        size_t __gj = 0;                                                     \
        for (; __gj + R <= PP; __gj += R) {                                   \
            DECLS                                                            \
            for (size_t __gk = 0; __gk < NN; __gk++) { ACCS }                \
            STORES                                                           \
        }                                                                    \
        for (; __gj < PP; __gj++) {                                          \
            const ELEM* BR __growj = &B[__gj][0];                            \
            ELEM __gacc = ELEM();                                            \
            for (size_t __gk = 0; __gk < NN; __gk++) __gacc += __growi[__gk] * conj_scalar(__growj[__gk]); \
            C[__gi][__gj] = __gacc;                                          \
        }                                                                    \
    }                                                                        \
}
#define JD2 JD(0) JD(1)
#define JA2 JA(0) JA(1)
#define JS2 JS(0) JS(1)
#define JD3 JD2 JD(2)
#define JA3 JA2 JA(2)
#define JS3 JS2 JS(2)
#define JD4 JD3 JD(3)
#define JA4 JA3 JA(3)
#define JS4 JS3 JS(3)
#define JD5 JD4 JD(4)
#define JA5 JA4 JA(4)
#define JS5 JS4 JS(4)
#define JD6 JD5 JD(5)
#define JA6 JA5 JA(5)
#define JS6 JS5 JS(5)
#define JD8 JD6 JD(6) JD(7)
#define JA8 JA6 JA(6) JA(7)
#define JS8 JS6 JS(6) JS(7)
MKJAM(2, JD2, JA2, JS2)
MKJAM(3, JD3, JA3, JS3)
MKJAM(4, JD4, JA4, JS4)
MKJAM(5, JD5, JA5, JS5)
MKJAM(6, JD6, JA6, JS6)
MKJAM(8, JD8, JA8, JS8)

#define PD(r)  ELEM __pacc##r = ELEM();
#define PA(r)  __pacc##r += av * conj_scalar(__bt[r]);
#define PS(r)  C[__gi][__gj + r] = __pacc##r;
#define MKPACK(N, DECLS, ACCS, STORES)                                       \
static void k_packT##N() {                                                   \
    packBT();                                                                \
    const ELEM* BR BT = poolBT;                                              \
    for (size_t __gi = 0; __gi < MM; __gi++) {                               \
        const ELEM* BR __growi = &A[__gi][0];                                \
        size_t __gj = 0;                                                     \
        for (; __gj + N <= PP; __gj += N) {                                   \
            DECLS                                                            \
            for (size_t __gk = 0; __gk < NN; __gk++) {                       \
                const ELEM av = __growi[__gk];                               \
                const ELEM* BR __bt = BT + __gk * (size_t)PP + __gj;         \
                ACCS                                                         \
            }                                                                \
            STORES                                                           \
        }                                                                    \
        for (; __gj < PP; __gj++) {                                          \
            const ELEM* BR __growj = &B[__gj][0];                            \
            ELEM __gacc = ELEM();                                            \
            for (size_t __gk = 0; __gk < NN; __gk++) __gacc += __growi[__gk] * conj_scalar(__growj[__gk]); \
            C[__gi][__gj] = __gacc;                                          \
        }                                                                    \
    }                                                                        \
}
#define PD4 PD(0) PD(1) PD(2) PD(3)
#define PA4 PA(0) PA(1) PA(2) PA(3)
#define PS4 PS(0) PS(1) PS(2) PS(3)
#define PD8 PD4 PD(4) PD(5) PD(6) PD(7)
#define PA8 PA4 PA(4) PA(5) PA(6) PA(7)
#define PS8 PS4 PS(4) PS(5) PS(6) PS(7)
MKPACK(4, PD4, PA4, PS4)
MKPACK(8, PD8, PA8, PS8)

static double calib_clock() {
    const long iters = 100000000L;
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
    {"base", k_base}, {"jam2", k_jam2}, {"jam3", k_jam3}, {"jam4", k_jam4},
    {"jam5", k_jam5}, {"jam6", k_jam6}, {"jam8", k_jam8},
    {"packT4", k_packT4}, {"packT8", k_packT8},
};
static const int NK = sizeof(kernels) / sizeof(kernels[0]);

int main(int argc, char** argv) {
    int rounds = argc > 1 ? atoi(argv[1]) : 5;
    int reps   = argc > 2 ? atoi(argv[2]) : 3;
    setup();
    double hz = calib_clock();
    std::vector<std::vector<double> > t(NK);
    size_t bytes = sizeof(ELEM) * (size_t)MM * PP;
    ELEM* ref = (ELEM*)malloc(bytes);
    std::vector<int> bitok(NK, 0);
    for (int i = 0; i < NK; i++) {
        memset(Cpool, 0xA5, bytes);
        kernels[i].fn(); asm volatile("" ::: "memory");
        if (i == 0) { memcpy(ref, Cpool, bytes); bitok[0] = 1; }
        else bitok[i] = (memcmp(ref, Cpool, bytes) == 0);
    }
    for (int r = 0; r < rounds; r++) for (int i = 0; i < NK; i++) for (int q = 0; q < reps; q++) {
        auto t0 = clk::now(); kernels[i].fn(); asm volatile("" ::: "memory"); auto t1 = clk::now();
        t[i].push_back(secs(t0, t1));
    }
    printf("# elem=%s m=%d n=%d p=%d  clock=%.3f GHz  cmacs=%.5g\n",
           ELEMNAME, MM, NN, PP, hz / 1e9, (double)MM * NN * PP);
    printf("%-9s %11s %9s %9s %9s\n", "kernel", "median_s", "cyc/cMAC", "vs_base", "bitwise");
    double base = 0;
    for (int i = 0; i < NK; i++) {
        std::sort(t[i].begin(), t[i].end());
        double m = t[i][t[i].size() / 2];
        if (i == 0) base = m;
        double macs = (double)MM * NN * PP;
        printf("%-9s %11.6f %9.4f %9.4f %9s\n", kernels[i].name, m, m * hz / macs, base / m, bitok[i] ? "yes" : "NO");
    }
    return 0;
}
