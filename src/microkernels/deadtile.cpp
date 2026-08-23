// deadtile.cpp -- is a dead jam tile (p < R) a REGRESSION?
//
// When this was written the emitter shipped a fixed R=5, so at p = 3 the
// emitted tile guard was `for (; __gj + 5 <= 3; __gj += 5)` -- provably false,
// the whole tile body dead code.  a3837e6 now derives R from a literal p, so a
// dead tile can only arise on the RUNTIME-EXTENT path (fixed R=5); this
// instrument answers whether that residual case costs anything.  The question is whether emitting it costs
// anything anyway: extra text for gcc to fold, a differently shaped CFG, a
// different inlining/unrolling decision in the surviving remainder.
//
// 51 interleaved samples per arm, min/median/max reported, because the effect
// being tested for is at the 1% level and a single median cannot carry it.

#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <chrono>
#include <algorithm>
#include <vector>

#ifndef MM
#define MM 4001
#endif
#ifndef NN
#define NN 257
#endif
#define PMAX 8
#define BR __restrict__
typedef double ELEM;
static inline ELEM conj_scalar(ELEM x) { return x; }
typedef std::chrono::high_resolution_clock clk;
static double secs(clk::time_point a, clk::time_point b) {
    return 1e-9 * std::chrono::duration_cast<std::chrono::nanoseconds>(b - a).count();
}
static ELEM* poolA; static ELEM* poolB; static ELEM* Cpool;
static ELEM** A; static ELEM** B; static ELEM** C;
static void setup() {
    poolA = (ELEM*)malloc(sizeof(ELEM) * (size_t)MM * NN);
    poolB = (ELEM*)malloc(sizeof(ELEM) * (size_t)PMAX * NN);
    Cpool = (ELEM*)malloc(sizeof(ELEM) * (size_t)MM * PMAX);
    A = (ELEM**)malloc(sizeof(ELEM*) * MM);
    B = (ELEM**)malloc(sizeof(ELEM*) * PMAX);
    C = (ELEM**)malloc(sizeof(ELEM*) * MM);
    unsigned long long s = 0x243F6A8885A308D3ULL;
#define RND ((s ^= s << 13, s ^= s >> 7, s ^= s << 17), (ELEM)((double)(s >> 11) * (1.0 / 9007199254740992.0) + 0.5))
    for (long i = 0; i < MM; i++) { A[i] = poolA + (size_t)i * NN; for (long k = 0; k < NN; k++) A[i][k] = RND; }
    for (long j = 0; j < PMAX; j++) { B[j] = poolB + (size_t)j * NN; for (long k = 0; k < NN; k++) B[j][k] = RND; }
    for (long i = 0; i < MM; i++) C[i] = Cpool + (size_t)i * PMAX;
    memset(Cpool, 0, sizeof(ELEM) * (size_t)MM * PMAX);
}
template <int P>
static void a_base() {
    for (size_t __gi = 0; __gi < MM; __gi++) {
        const ELEM* BR __growi = &A[__gi][0];
        for (size_t __gj = 0; __gj < P; __gj++) {
            const ELEM* BR __growj = &B[__gj][0];
            ELEM __gacc = ELEM();
            for (size_t __gk = 0; __gk < NN; __gk++) __gacc += __growi[__gk] * conj_scalar(__growj[__gk]);
            C[__gi][__gj] = __gacc;
        }
    }
}
#define JD(r) const ELEM* BR __growj##r = &B[__gj + r][0]; ELEM __gacc##r = ELEM();
#define JA(r) __gacc##r += __growi[__gk] * conj_scalar(__growj##r[__gk]);
#define JS(r) C[__gi][__gj + r] = __gacc##r;
#define MKJAM(R, D, AA, S)                                                     \
template <int P> static void a_jam##R() {                                      \
    for (size_t __gi = 0; __gi < MM; __gi++) {                                 \
        const ELEM* BR __growi = &A[__gi][0];                                  \
        size_t __gj = 0;                                                       \
        for (; __gj + R <= P; __gj += R) {                                       \
            D                                                                  \
            for (size_t __gk = 0; __gk < NN; __gk++) { AA }                    \
            S                                                                  \
        }                                                                      \
        for (; __gj < P; __gj++) {                                             \
            const ELEM* BR __growj = &B[__gj][0];                              \
            ELEM __gacc = ELEM();                                              \
            for (size_t __gk = 0; __gk < NN; __gk++) __gacc += __growi[__gk] * conj_scalar(__growj[__gk]); \
            C[__gi][__gj] = __gacc;                                            \
        }                                                                      \
    }                                                                          \
}
MKJAM(3, JD(0) JD(1) JD(2), JA(0) JA(1) JA(2), JS(0) JS(1) JS(2))
MKJAM(4, JD(0) JD(1) JD(2) JD(3), JA(0) JA(1) JA(2) JA(3), JS(0) JS(1) JS(2) JS(3))
MKJAM(5, JD(0) JD(1) JD(2) JD(3) JD(4), JA(0) JA(1) JA(2) JA(3) JA(4), JS(0) JS(1) JS(2) JS(3) JS(4))

static double calib_clock() {
    const long iters = 100000000L;
    double x = 1.0, c = 1.0000001;
    auto t0 = clk::now();
    for (long i = 0; i < iters; i += 4) {
        asm volatile("vaddsd %1, %0, %0" : "+x"(x) : "x"(c));
        asm volatile("vaddsd %1, %0, %0" : "+x"(x) : "x"(c));
        asm volatile("vaddsd %1, %0, %0" : "+x"(x) : "x"(c));
        asm volatile("vaddsd %1, %0, %0" : "+x"(x) : "x"(c));
    }
    auto t1 = clk::now();
    if (x == 12345.0) printf("");
    return 3.0 * iters / secs(t0, t1);
}
static double HZ;
static int NS = 51;
struct Arm { const char* nm; void (*fn)(); std::vector<double> t; };
template <int P>
static void go() {
    Arm arms[] = { {"base", a_base<P>, {}}, {"jam3", a_jam3<P>, {}},
                   {"jam4", a_jam4<P>, {}}, {"jam5(SHIPPED)", a_jam5<P>, {}} };
    const int NA = 4;
    for (int s = 0; s < NS; s++)
        for (int i = 0; i < NA; i++) {
            auto t0 = clk::now(); arms[i].fn(); asm volatile("" ::: "memory"); auto t1 = clk::now();
            arms[i].t.push_back(secs(t0, t1));
        }
    double bmed = 0;
    for (int i = 0; i < NA; i++) {
        std::sort(arms[i].t.begin(), arms[i].t.end());
        double lo = arms[i].t.front(), md = arms[i].t[NS / 2], hi = arms[i].t.back();
        if (i == 0) bmed = md;
        printf("p=%-2d %-14s min %.6f  med %.6f  max %.6f   vs_base %.4f\n",
               P, arms[i].nm, lo, md, hi, bmed / md);
    }
    printf("\n"); fflush(stdout);
}
int main(int argc, char** argv) {
    if (argc > 1) NS = atoi(argv[1]);
    setup(); HZ = calib_clock();
    printf("# m=%d n=%d  clock=%.3f GHz  %d interleaved samples/arm\n", MM, NN, HZ / 1e9, NS);
    go<2>(); go<3>(); go<4>();
    return 0;
}
