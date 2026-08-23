// gram_iblock.cpp -- attributing the packed form's win.
//
// gram_jam_vec.cpp found that packing B j-major and blocking 4 i-rows gets
// 6.7-7.9x where the shipped 1 x 5 jam gets 3.5x.  Two separate mechanisms are
// mixed in there:
//   (1) PACKING removes the shuffle network correction 16 identified, and
//   (2) I-BLOCKING cuts B-operand traffic IB-fold and raises the MAC-per-load
//       ratio from R/(1+R) to IB*R/(IB+R).
// (2) needs NO scratch buffer, NO pack pass, and NO layout choice -- it is a
// pure text change to the same nest, exactly like the shipped jam.  So if (2)
// carries most of the win it is a far cheaper emitter target than (1).
//
// Every arm keeps one named accumulator per output cell summing in ascending k,
// so all of them are bitwise against the reference nest by the same argument the
// shipped jam uses.  The `==base` column is the check, not the argument.

#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <chrono>
#include <algorithm>
#include <vector>
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

#ifdef ELEM_FLOAT
typedef float ELEM;
#define ELEMNAME "float"
#else
typedef double ELEM;
#define ELEMNAME "double"
#endif

#define BR __restrict__
static inline ELEM conj_scalar(ELEM x) { return x; }
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
#define RND ((s ^= s << 13, s ^= s >> 7, s ^= s << 17), (ELEM)((double)(s >> 11) * (1.0 / 9007199254740992.0) + 0.5))
    for (long i = 0; i < MM; i++) { A[i] = poolA + (size_t)i * NN; for (long k = 0; k < NN; k++) A[i][k] = RND; }
    for (long j = 0; j < PP; j++) { B[j] = poolB + (size_t)j * NN; for (long k = 0; k < NN; k++) B[j][k] = RND; }
    for (long i = 0; i < MM; i++) C[i] = Cpool + (size_t)i * PP;
    memset(Cpool, 0, sizeof(ELEM) * (size_t)MM * PP);
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

// ---- the shipped 1 x R jam ------------------------------------------------
#define JD(r) const ELEM* BR __growj##r = &B[__gj + r][0]; ELEM __gacc##r = ELEM();
#define JA(r) __gacc##r += __growi[__gk] * conj_scalar(__growj##r[__gk]);
#define JS(r) C[__gi][__gj + r] = __gacc##r;
#define MKJAM(R, DECLS, ACCS, STORES)                                         \
static void k_jam##R() {                                                      \
    for (size_t __gi = 0; __gi < MM; __gi++) {                                \
        const ELEM* BR __growi = &A[__gi][0];                                 \
        size_t __gj = 0;                                                      \
        for (; __gj + R <= PP; __gj += R) {                                     \
            DECLS                                                             \
            for (size_t __gk = 0; __gk < NN; __gk++) { ACCS }                 \
            STORES                                                            \
        }                                                                     \
        for (; __gj < PP; __gj++) {                                           \
            const ELEM* BR __growj = &B[__gj][0];                             \
            ELEM __gacc = ELEM();                                             \
            for (size_t __gk = 0; __gk < NN; __gk++) __gacc += __growi[__gk] * conj_scalar(__growj[__gk]); \
            C[__gi][__gj] = __gacc;                                           \
        }                                                                     \
    }                                                                         \
}
MKJAM(5, JD(0) JD(1) JD(2) JD(3) JD(4), JA(0) JA(1) JA(2) JA(3) JA(4), JS(0) JS(1) JS(2) JS(3) JS(4))

// ---- the IB x R jam: block BOTH loop axes, no packing, no scratch ---------
// IB * R named accumulators; IB + R row pointers.  MAC-per-load is
// IB*R/(IB+R) against the shipped jam's R/(1+R).
#define QROW(u)   const ELEM* BR __qa##u = &A[__gi + u][0];
#define QCOL(v)   const ELEM* BR __qb##v = &B[__gj + v][0];
#define QDEC(u,v) ELEM __q##u##_##v = ELEM();
#define QLD(u)    const ELEM __av##u = __qa##u[__gk];
#define QACC(u,v) __q##u##_##v += __av##u * conj_scalar(__qb##v[__gk]);
#define QST(u,v)  C[__gi + u][__gj + v] = __q##u##_##v;
#define MKIB(IB, R, ROWS, COLS, DECS, LDS, ACCS, STS)                          \
static void k_ib##IB##x##R() {                                                 \
    size_t __gi = 0;                                                           \
    for (; __gi + IB <= MM; __gi += IB) {                                       \
        ROWS                                                                   \
        size_t __gj = 0;                                                       \
        for (; __gj + R <= PP; __gj += R) {                                      \
            COLS DECS                                                          \
            for (size_t __gk = 0; __gk < NN; __gk++) { LDS ACCS }              \
            STS                                                                \
        }                                                                      \
        for (; __gj < PP; __gj++) {                                            \
            const ELEM* BR __growj = &B[__gj][0];                              \
            for (size_t u = 0; u < IB; u++) {                                   \
                const ELEM* BR __growi = &A[__gi + u][0];                      \
                ELEM __gacc = ELEM();                                          \
                for (size_t __gk = 0; __gk < NN; __gk++) __gacc += __growi[__gk] * conj_scalar(__growj[__gk]); \
                C[__gi + u][__gj] = __gacc;                                    \
            }                                                                  \
        }                                                                      \
    }                                                                          \
    for (; __gi < MM; __gi++) {                                                \
        const ELEM* BR __growi = &A[__gi][0];                                  \
        for (size_t __gj = 0; __gj < PP; __gj++) {                             \
            const ELEM* BR __growj = &B[__gj][0];                              \
            ELEM __gacc = ELEM();                                              \
            for (size_t __gk = 0; __gk < NN; __gk++) __gacc += __growi[__gk] * conj_scalar(__growj[__gk]); \
            C[__gi][__gj] = __gacc;                                            \
        }                                                                      \
    }                                                                          \
}

#define R2(M) M(0) M(1)
#define R3(M) R2(M) M(2)
#define R4(M) R3(M) M(3)
#define R5(M) R4(M) M(4)
#define R6(M) R5(M) M(5)
#define R8(M) R6(M) M(6) M(7)
#define P2(M,u) M(u,0) M(u,1)
#define P3(M,u) P2(M,u) M(u,2)
#define P4(M,u) P3(M,u) M(u,3)
#define P5(M,u) P4(M,u) M(u,4)
#define P6(M,u) P5(M,u) M(u,5)
#define P8(M,u) P6(M,u) M(u,6) M(u,7)

MKIB(2, 2, R2(QROW), R2(QCOL), P2(QDEC,0) P2(QDEC,1), R2(QLD), P2(QACC,0) P2(QACC,1), P2(QST,0) P2(QST,1))
MKIB(2, 4, R2(QROW), R4(QCOL), P4(QDEC,0) P4(QDEC,1), R2(QLD), P4(QACC,0) P4(QACC,1), P4(QST,0) P4(QST,1))
MKIB(2, 5, R2(QROW), R5(QCOL), P5(QDEC,0) P5(QDEC,1), R2(QLD), P5(QACC,0) P5(QACC,1), P5(QST,0) P5(QST,1))
MKIB(2, 6, R2(QROW), R6(QCOL), P6(QDEC,0) P6(QDEC,1), R2(QLD), P6(QACC,0) P6(QACC,1), P6(QST,0) P6(QST,1))
MKIB(2, 8, R2(QROW), R8(QCOL), P8(QDEC,0) P8(QDEC,1), R2(QLD), P8(QACC,0) P8(QACC,1), P8(QST,0) P8(QST,1))
MKIB(3, 3, R3(QROW), R3(QCOL), P3(QDEC,0) P3(QDEC,1) P3(QDEC,2), R3(QLD),
     P3(QACC,0) P3(QACC,1) P3(QACC,2), P3(QST,0) P3(QST,1) P3(QST,2))
MKIB(3, 5, R3(QROW), R5(QCOL), P5(QDEC,0) P5(QDEC,1) P5(QDEC,2), R3(QLD),
     P5(QACC,0) P5(QACC,1) P5(QACC,2), P5(QST,0) P5(QST,1) P5(QST,2))
MKIB(4, 2, R4(QROW), R2(QCOL), P2(QDEC,0) P2(QDEC,1) P2(QDEC,2) P2(QDEC,3), R4(QLD),
     P2(QACC,0) P2(QACC,1) P2(QACC,2) P2(QACC,3), P2(QST,0) P2(QST,1) P2(QST,2) P2(QST,3))
MKIB(4, 3, R4(QROW), R3(QCOL), P3(QDEC,0) P3(QDEC,1) P3(QDEC,2) P3(QDEC,3), R4(QLD),
     P3(QACC,0) P3(QACC,1) P3(QACC,2) P3(QACC,3), P3(QST,0) P3(QST,1) P3(QST,2) P3(QST,3))
MKIB(4, 4, R4(QROW), R4(QCOL), P4(QDEC,0) P4(QDEC,1) P4(QDEC,2) P4(QDEC,3), R4(QLD),
     P4(QACC,0) P4(QACC,1) P4(QACC,2) P4(QACC,3), P4(QST,0) P4(QST,1) P4(QST,2) P4(QST,3))
MKIB(4, 5, R4(QROW), R5(QCOL), P5(QDEC,0) P5(QDEC,1) P5(QDEC,2) P5(QDEC,3), R4(QLD),
     P5(QACC,0) P5(QACC,1) P5(QACC,2) P5(QACC,3), P5(QST,0) P5(QST,1) P5(QST,2) P5(QST,3))
MKIB(6, 4, R6(QROW), R4(QCOL), P4(QDEC,0) P4(QDEC,1) P4(QDEC,2) P4(QDEC,3) P4(QDEC,4) P4(QDEC,5), R6(QLD),
     P4(QACC,0) P4(QACC,1) P4(QACC,2) P4(QACC,3) P4(QACC,4) P4(QACC,5),
     P4(QST,0) P4(QST,1) P4(QST,2) P4(QST,3) P4(QST,4) P4(QST,5))
MKIB(8, 2, R8(QROW), R2(QCOL), P2(QDEC,0) P2(QDEC,1) P2(QDEC,2) P2(QDEC,3) P2(QDEC,4) P2(QDEC,5) P2(QDEC,6) P2(QDEC,7), R8(QLD),
     P2(QACC,0) P2(QACC,1) P2(QACC,2) P2(QACC,3) P2(QACC,4) P2(QACC,5) P2(QACC,6) P2(QACC,7),
     P2(QST,0) P2(QST,1) P2(QST,2) P2(QST,3) P2(QST,4) P2(QST,5) P2(QST,6) P2(QST,7))

// ---- packed j-major, contraction suppressed, 4 i-rows (the ceiling) -------
__attribute__((optimize("fp-contract=off")))
static void k_packnc4x8() {
    for (long j = 0; j < PP; j++) for (long k = 0; k < NN; k++) poolBT[(size_t)k * PP + j] = B[j][k];
    const ELEM* BR BT = poolBT;
    size_t __gi = 0;
    for (; __gi + 4 <= MM; __gi += 4) {
        size_t __gj = 0;
        for (; __gj + 8 <= PP; __gj += 8) {
            ELEM q[4][8];
            for (int u = 0; u < 4; u++) for (int v = 0; v < 8; v++) q[u][v] = ELEM();
            for (size_t __gk = 0; __gk < NN; __gk++) {
                const ELEM* BR bt = BT + __gk * (size_t)PP + __gj;
                for (int u = 0; u < 4; u++) {
                    const ELEM av = A[__gi + u][__gk];
                    for (int v = 0; v < 8; v++) q[u][v] += av * conj_scalar(bt[v]);
                }
            }
            for (int u = 0; u < 4; u++) for (int v = 0; v < 8; v++) C[__gi + u][__gj + v] = q[u][v];
        }
        for (; __gj < PP; __gj++)
            for (int u = 0; u < 4; u++) {
                const ELEM* BR bj = &B[__gj][0]; const ELEM* BR ai = &A[__gi + u][0];
                ELEM acc = ELEM();
                for (size_t __gk = 0; __gk < NN; __gk++) acc += ai[__gk] * conj_scalar(bj[__gk]);
                C[__gi + u][__gj] = acc;
            }
    }
    for (; __gi < MM; __gi++) {
        const ELEM* BR ai = &A[__gi][0];
        for (size_t __gj = 0; __gj < PP; __gj++) {
            const ELEM* BR bj = &B[__gj][0];
            ELEM acc = ELEM();
            for (size_t __gk = 0; __gk < NN; __gk++) acc += ai[__gk] * conj_scalar(bj[__gk]);
            C[__gi][__gj] = acc;
        }
    }
}

// Same kernel, but the BT scratch is ALLOCATED AND FREED INSIDE the timed
// region -- which is what an emitter would have to do, and which the README's
// own "allocation is usually the dominant term" prior says must be measured
// rather than assumed.
__attribute__((optimize("fp-contract=off")))
static void k_packnc4x8_alloc() {
    ELEM* BTbuf = (ELEM*)malloc(sizeof(ELEM) * (size_t)NN * PP);
    for (long j = 0; j < PP; j++) for (long k = 0; k < NN; k++) BTbuf[(size_t)k * PP + j] = B[j][k];
    const ELEM* BR BT = BTbuf;
    size_t __gi = 0;
    for (; __gi + 4 <= MM; __gi += 4) {
        size_t __gj = 0;
        for (; __gj + 8 <= PP; __gj += 8) {
            ELEM q[4][8];
            for (int u = 0; u < 4; u++) for (int v = 0; v < 8; v++) q[u][v] = ELEM();
            for (size_t __gk = 0; __gk < NN; __gk++) {
                const ELEM* BR bt = BT + __gk * (size_t)PP + __gj;
                for (int u = 0; u < 4; u++) {
                    const ELEM av = A[__gi + u][__gk];
                    for (int v = 0; v < 8; v++) q[u][v] += av * conj_scalar(bt[v]);
                }
            }
            for (int u = 0; u < 4; u++) for (int v = 0; v < 8; v++) C[__gi + u][__gj + v] = q[u][v];
        }
        for (; __gj < PP; __gj++)
            for (int u = 0; u < 4; u++) {
                const ELEM* BR bj = &B[__gj][0]; const ELEM* BR ai = &A[__gi + u][0];
                ELEM acc = ELEM();
                for (size_t __gk = 0; __gk < NN; __gk++) acc += ai[__gk] * conj_scalar(bj[__gk]);
                C[__gi + u][__gj] = acc;
            }
    }
    for (; __gi < MM; __gi++) {
        const ELEM* BR ai = &A[__gi][0];
        for (size_t __gj = 0; __gj < PP; __gj++) {
            const ELEM* BR bj = &B[__gj][0];
            ELEM acc = ELEM();
            for (size_t __gk = 0; __gk < NN; __gk++) acc += ai[__gk] * conj_scalar(bj[__gk]);
            C[__gi][__gj] = acc;
        }
    }
    free(BTbuf);
}

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

struct K { const char* name; void (*fn)(); int ib; int r; };
static K kernels[] = {
    {"base",      k_base,       1, 1},
    {"jam1x5",    k_jam5,       1, 5},
    {"ib2x2",     k_ib2x2,      2, 2},
    {"ib2x4",     k_ib2x4,      2, 4},
    {"ib2x5",     k_ib2x5,      2, 5},
    {"ib2x6",     k_ib2x6,      2, 6},
    {"ib2x8",     k_ib2x8,      2, 8},
    {"ib3x3",     k_ib3x3,      3, 3},
    {"ib3x5",     k_ib3x5,      3, 5},
    {"ib4x2",     k_ib4x2,      4, 2},
    {"ib4x3",     k_ib4x3,      4, 3},
    {"ib4x4",     k_ib4x4,      4, 4},
    {"ib4x5",     k_ib4x5,      4, 5},
    {"ib6x4",     k_ib6x4,      6, 4},
    {"ib8x2",     k_ib8x2,      8, 2},
    {"packnc4x8", k_packnc4x8,  4, 8},
    {"pack+alloc", k_packnc4x8_alloc, 4, 8},
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
    printf("# elem=%s m=%d n=%d p=%d  clock=%.3f GHz\n", ELEMNAME, MM, NN, PP, hz / 1e9);
    printf("%-10s %5s %11s %9s %9s %9s %8s\n", "kernel", "MAC/ld", "median_s", "cyc/MAC", "GMAC/s", "vs_base", "==base");
    double base = 0;
    for (int i = 0; i < NK; i++) {
        std::sort(t[i].begin(), t[i].end());
        double m = t[i][t[i].size() / 2];
        if (i == 0) base = m;
        double macs = (double)MM * NN * PP;
        double ratio = (double)(kernels[i].ib * kernels[i].r) / (kernels[i].ib + kernels[i].r);
        printf("%-10s %5.2f %11.6f %9.4f %9.4f %9.4f %8s\n", kernels[i].name, ratio, m,
               m * hz / macs, macs / m / 1e9, base / m, bitok[i] ? "yes" : "NO");
    }
    return 0;
}
