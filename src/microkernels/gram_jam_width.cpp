// Microkernel harness for the dense-gram jam.
//
// Reproduces the memory layout and the exact statement text that
// CodeGenExpr.fs's materializeGramForm emits (row-pointer skeleton into one
// pool, contracted axis contiguous, conj_scalar() on the b operand), so the
// kernels below are what g++ actually sees in a Blade program -- only the tile
// width R and the accumulator form vary.
//
// Also calibrates the effective core clock with a dependent vaddsd chain
// (Zen 3 FADD latency = 3 cycles), so every result can be quoted in cycles.

#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <chrono>
#include <algorithm>
#include <vector>
#include <string>
#include <cmath>

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

// ---------------------------------------------------------------- operands
static double* poolA;
static double* poolB;
static double** A;   // MM rows of NN
static double** B;   // PP rows of NN
static double* Cpool;
static double** C;   // MM rows of PP

static void setup() {
    poolA = (double*)malloc(sizeof(double) * (size_t)MM * NN);
    poolB = (double*)malloc(sizeof(double) * (size_t)PP * NN);
    Cpool = (double*)malloc(sizeof(double) * (size_t)MM * PP);
    A = (double**)malloc(sizeof(double*) * MM);
    B = (double**)malloc(sizeof(double*) * PP);
    C = (double**)malloc(sizeof(double*) * MM);
    // OPERANDS: full-mantissa by DEFAULT, because the bitwise column is only
    // meaningful if the operands can actually round.  `-DDYADIC` reproduces the
    // gramdense.blade fixture
    // instead: 1.0+0.5*i and 0.25+0.125*j -- exact dyadic
    // rationals with a handful of significant bits, whose products and sums
    // are themselves exact.  Nothing rounds, so that fixture CANNOT observe an
    // FMA-induced bit change, so under -DDYADIC EVERY arm reports bitwise `yes`
    // including base_fma -- which is the proof that the flag is a trap and not a
    // stricter test.  Run the default first; use -DDYADIC only to demonstrate the
    // blindness.  The self-check for that: base_fma MUST read NO by default.
    unsigned long long s = 0x243F6A8885A308D3ULL;
#define RND ((s ^= s << 13, s ^= s >> 7, s ^= s << 17), (double)(s >> 11) * (1.0 / 9007199254740992.0) + 0.5)
    for (long i = 0; i < MM; i++) { A[i] = poolA + (size_t)i * NN;
        for (long k = 0; k < NN; k++)
#ifdef DYADIC
            A[i][k] = 1.0 + 0.5 * ((i * 7 + k) % 11);   // matches gramdense.blade
#else
            A[i][k] = RND;                              // DEFAULT: bit-sensitive
#endif
    }
    for (long j = 0; j < PP; j++) { B[j] = poolB + (size_t)j * NN;
        for (long k = 0; k < NN; k++)
#ifdef DYADIC
            B[j][k] = 0.25 + 0.125 * ((j * 5 + k) % 13);
#else
            B[j][k] = RND;
#endif
    }
    for (long i = 0; i < MM; i++) { C[i] = Cpool + (size_t)i * PP; }
    memset(Cpool, 0, sizeof(double) * (size_t)MM * PP);
}

// ------------------------------------------------------------- the kernels

// exactly what the pre-jam emitter produced
static void k_base() {
    for (size_t __gi = 0; __gi < MM; __gi++) {
        const double* BR __growi = &A[__gi][0];
        for (size_t __gj = 0; __gj < PP; __gj++) {
            const double* BR __growj = &B[__gj][0];
            double __gacc = double();
            for (size_t __gk = 0; __gk < NN; __gk++) {
                __gacc += __growi[__gk] * conj_scalar(__growj[__gk]);
            }
            C[__gi][__gj] = __gacc;
        }
    }
}

// the same, but with the multiply-add forced into a contracted FMA, i.e. the
// 4-cycle dependent chain a "naive serial accumulator" baseline would be
static void k_base_fma() {
    for (size_t __gi = 0; __gi < MM; __gi++) {
        const double* BR __growi = &A[__gi][0];
        for (size_t __gj = 0; __gj < PP; __gj++) {
            const double* BR __growj = &B[__gj][0];
            double __gacc = double();
            for (size_t __gk = 0; __gk < NN; __gk++) {
                __gacc = std::fma(__growi[__gk], conj_scalar(__growj[__gk]), __gacc);
            }
            C[__gi][__gj] = __gacc;
        }
    }
}

// MECHANISM CONTROL.  Identical to k_base except that each output cell's
// accumulator is seeded from the previous cell's result via (prev - prev),
// which is 0.0 for every finite prev -- same arithmetic, same bits, but a real
// data dependency that forbids the out-of-order window from overlapping
// adjacent cells.  If the baseline's sub-3-cycle/MAC behaviour at short folds
// really is cross-cell overlap, THIS kernel should sit at exactly 3.00
// cyc/MAC (the Zen 3 vaddsd latency) at every n.
static void k_base_serial() {
    for (size_t __gi = 0; __gi < MM; __gi++) {
        const double* BR __growi = &A[__gi][0];
        double prev = 0.0;
        for (size_t __gj = 0; __gj < PP; __gj++) {
            const double* BR __growj = &B[__gj][0];
            double __gacc = prev - prev;
            for (size_t __gk = 0; __gk < NN; __gk++) {
                __gacc += __growi[__gk] * conj_scalar(__growj[__gk]);
            }
            C[__gi][__gj] = __gacc;
            prev = __gacc;
        }
    }
}

// R-wide jam, written the way materializeGramForm writes it
#define JAMDECL(r)  const double* BR __growj##r = &B[__gj + r][0]; double __gacc##r = double();
#define JAMACC(r)   __gacc##r += __growi[__gk] * conj_scalar(__growj##r[__gk]);
#define JAMSTORE(r) C[__gi][__gj + r] = __gacc##r;

#define MAKEJAM(R, DECLS, ACCS, STORES)                                    \
static void k_jam##R() {                                                   \
    for (size_t __gi = 0; __gi < MM; __gi++) {                             \
        const double* BR __growi = &A[__gi][0];                            \
        size_t __gj = 0;                                                   \
        for (; __gj + R <= PP; __gj += R) {                                \
            DECLS                                                          \
            for (size_t __gk = 0; __gk < NN; __gk++) { ACCS }              \
            STORES                                                         \
        }                                                                  \
        for (; __gj < PP; __gj++) {                                        \
            const double* BR __growj = &B[__gj][0];                        \
            double __gacc = double();                                      \
            for (size_t __gk = 0; __gk < NN; __gk++) {                     \
                __gacc += __growi[__gk] * conj_scalar(__growj[__gk]);      \
            }                                                              \
            C[__gi][__gj] = __gacc;                                        \
        }                                                                  \
    }                                                                      \
}

MAKEJAM(2, JAMDECL(0) JAMDECL(1),
           JAMACC(0) JAMACC(1),
           JAMSTORE(0) JAMSTORE(1))
MAKEJAM(3, JAMDECL(0) JAMDECL(1) JAMDECL(2),
           JAMACC(0) JAMACC(1) JAMACC(2),
           JAMSTORE(0) JAMSTORE(1) JAMSTORE(2))
MAKEJAM(4, JAMDECL(0) JAMDECL(1) JAMDECL(2) JAMDECL(3),
           JAMACC(0) JAMACC(1) JAMACC(2) JAMACC(3),
           JAMSTORE(0) JAMSTORE(1) JAMSTORE(2) JAMSTORE(3))
MAKEJAM(5, JAMDECL(0) JAMDECL(1) JAMDECL(2) JAMDECL(3) JAMDECL(4),
           JAMACC(0) JAMACC(1) JAMACC(2) JAMACC(3) JAMACC(4),
           JAMSTORE(0) JAMSTORE(1) JAMSTORE(2) JAMSTORE(3) JAMSTORE(4))
MAKEJAM(6, JAMDECL(0) JAMDECL(1) JAMDECL(2) JAMDECL(3) JAMDECL(4) JAMDECL(5),
           JAMACC(0) JAMACC(1) JAMACC(2) JAMACC(3) JAMACC(4) JAMACC(5),
           JAMSTORE(0) JAMSTORE(1) JAMSTORE(2) JAMSTORE(3) JAMSTORE(4) JAMSTORE(5))
MAKEJAM(8, JAMDECL(0) JAMDECL(1) JAMDECL(2) JAMDECL(3) JAMDECL(4) JAMDECL(5) JAMDECL(6) JAMDECL(7),
           JAMACC(0) JAMACC(1) JAMACC(2) JAMACC(3) JAMACC(4) JAMACC(5) JAMACC(6) JAMACC(7),
           JAMSTORE(0) JAMSTORE(1) JAMSTORE(2) JAMSTORE(3) JAMSTORE(4) JAMSTORE(5) JAMSTORE(6) JAMSTORE(7))
MAKEJAM(12, JAMDECL(0) JAMDECL(1) JAMDECL(2) JAMDECL(3) JAMDECL(4) JAMDECL(5) JAMDECL(6) JAMDECL(7) JAMDECL(8) JAMDECL(9) JAMDECL(10) JAMDECL(11),
           JAMACC(0) JAMACC(1) JAMACC(2) JAMACC(3) JAMACC(4) JAMACC(5) JAMACC(6) JAMACC(7) JAMACC(8) JAMACC(9) JAMACC(10) JAMACC(11),
           JAMSTORE(0) JAMSTORE(1) JAMSTORE(2) JAMSTORE(3) JAMSTORE(4) JAMSTORE(5) JAMSTORE(6) JAMSTORE(7) JAMSTORE(8) JAMSTORE(9) JAMSTORE(10) JAMSTORE(11))

// ------------------------------------------------------------- calibration
// A strictly dependent chain of vaddsd.  Zen 3 FADD latency is 3 cycles, so
// seconds/op * 3 = seconds/cycle.
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
    return 3.0 * iters / secs(t0, t1);   // Hz
}

// ------------------------------------------------------------------ driver
struct K { const char* name; void (*fn)(); };
static K kernels[] = {
    {"base",     k_base},
    {"base_fma", k_base_fma},
    {"base_ser", k_base_serial},
    {"jam2",     k_jam2},
    {"jam3",     k_jam3},
    {"jam4",     k_jam4},
    {"jam5",     k_jam5},
    {"jam6",     k_jam6},
    {"jam8",     k_jam8},
    {"jam12",    k_jam12},
};
static const int NK = sizeof(kernels) / sizeof(kernels[0]);

int main(int argc, char** argv) {
    int rounds = argc > 1 ? atoi(argv[1]) : 5;
    int reps = argc > 2 ? atoi(argv[2]) : 3;
    setup();
    double hz = calib_clock();
    std::vector<std::vector<double>> t(NK);
    size_t bytes = sizeof(double) * (size_t)MM * PP;
    double* ref = (double*)malloc(bytes);
    int bitok[NK];
    for (int i = 0; i < NK; i++) {
        memset(Cpool, 0xA5, bytes);
        kernels[i].fn();
        if (i == 0) { memcpy(ref, Cpool, bytes); bitok[0] = 1; }
        else bitok[i] = (memcmp(ref, Cpool, bytes) == 0);
    }
    for (int r = 0; r < rounds; r++)
        for (int i = 0; i < NK; i++)
            for (int q = 0; q < reps; q++) {
                auto t0 = clk::now(); kernels[i].fn(); auto t1 = clk::now();
                t[i].push_back(secs(t0, t1));
            }
    printf("# m=%d n=%d p=%d   clock=%.3f GHz   macs=%.4g\n", MM, NN, PP, hz / 1e9,
           (double)MM * NN * PP);
    printf("%-9s %11s %9s %9s %9s %9s\n", "kernel", "median_s", "cyc/MAC", "GMAC/s", "vs_base", "bitwise");
    double base = 0;
    for (int i = 0; i < NK; i++) {
        std::sort(t[i].begin(), t[i].end());
        double m = t[i][t[i].size() / 2];
        if (i == 0) base = m;
        double macs = (double)MM * NN * PP;
        printf("%-9s %11.6f %9.4f %9.4f %9.4f %9s\n", kernels[i].name, m,
               m * hz / macs, macs / m / 1e9, base / m, bitok[i] ? "yes" : "NO");
    }
    return 0;
}
