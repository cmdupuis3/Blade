// gram_jam_vec.cpp -- adversarial extension of src/microkernels/gram_jam_width.cpp
//
// Question: correction 16 says gcc TRANSPOSES the R x 4 tile and runs R-lane
// vaddpd, so the binding resource is shuffle throughput.  If the compiler is
// going to vectorize across the OUTPUT axis anyway, why is the emitter making
// it pay a transpose?  Pack B into j-major (B^T) ONCE per gram -- amortized over
// all m rows -- and the same output-axis vectorization needs ZERO shuffles:
// broadcast A[i][k], load 4 consecutive j from BT[k], vaddpd.
//
// Every lane still holds ONE output cell summing in ascending k, so the packed
// form is BITWISE identical to the reference nest -- no licence needed.
//
// Arms:
//   base       pre-jam emission (reference for bitwise + speed)
//   base_fma   contraction control: MUST read NO on the bitwise column
//   base_ser   cross-cell-overlap control (correction 15)
//   jamR       the SHIPPED emission text at width R
//   packT_N    B packed j-major, N output cells per tile, plain C (emitter-shaped)
//   packTi_N   the same, hand-written AVX2 intrinsics (beat-the-compiler control)
//   packTIBxN  packed j-major + IB i-rows blocked (cuts B traffic IB-fold)
//   kfold4     LICENSED k-lane reassociated fold (reads NO; measures the licence)
//
// Shapes compile-time: -DMM -DNN -DPP.  Element type: -DELEM_FLOAT.
// Operands full-mantissa by default (correction 17); -DDYADIC reproduces the
// blind fixture and every arm then reads yes, which is the trap.

#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <chrono>
#include <algorithm>
#include <vector>
#include <string>
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

// ---------------------------------------------------------------- operands
static ELEM* poolA; static ELEM* poolB; static ELEM* Cpool; static ELEM* poolBT;
static ELEM** A; static ELEM** B; static ELEM** C;

static void setup() {
    poolA = (ELEM*)malloc(sizeof(ELEM) * (size_t)MM * NN);
    poolB = (ELEM*)malloc(sizeof(ELEM) * (size_t)PP * NN);
    poolBT= (ELEM*)malloc(sizeof(ELEM) * (size_t)NN * PP + 64);
    Cpool = (ELEM*)malloc(sizeof(ELEM) * (size_t)MM * PP);
    A = (ELEM**)malloc(sizeof(ELEM*) * MM);
    B = (ELEM**)malloc(sizeof(ELEM*) * PP);
    C = (ELEM**)malloc(sizeof(ELEM*) * MM);
    unsigned long long s = 0x243F6A8885A308D3ULL;
#define RND ((s ^= s << 13, s ^= s >> 7, s ^= s << 17), (ELEM)((double)(s >> 11) * (1.0 / 9007199254740992.0) + 0.5))
    for (long i = 0; i < MM; i++) { A[i] = poolA + (size_t)i * NN;
        for (long k = 0; k < NN; k++)
#ifdef DYADIC
            A[i][k] = (ELEM)(1.0 + 0.5 * ((i * 7 + k) % 11));
#else
            A[i][k] = RND;
#endif
    }
    for (long j = 0; j < PP; j++) { B[j] = poolB + (size_t)j * NN;
        for (long k = 0; k < NN; k++)
#ifdef DYADIC
            B[j][k] = (ELEM)(0.25 + 0.125 * ((j * 5 + k) % 13));
#else
            B[j][k] = RND;
#endif
    }
    for (long i = 0; i < MM; i++) { C[i] = Cpool + (size_t)i * PP; }
    memset(Cpool, 0, sizeof(ELEM) * (size_t)MM * PP);
}

// the pack: BT[k][j] = B[j][k].  Cost is n*p, amortized over m output rows.
static inline void packBT() {
    for (long j = 0; j < PP; j++)
        for (long k = 0; k < NN; k++)
            poolBT[(size_t)k * PP + j] = B[j][k];
}

// ------------------------------------------------------- reference arms
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
static void k_base_fma() {
    for (size_t __gi = 0; __gi < MM; __gi++) {
        const ELEM* BR __growi = &A[__gi][0];
        for (size_t __gj = 0; __gj < PP; __gj++) {
            const ELEM* BR __growj = &B[__gj][0];
            ELEM __gacc = ELEM();
            for (size_t __gk = 0; __gk < NN; __gk++) __gacc = std::fma(__growi[__gk], conj_scalar(__growj[__gk]), __gacc);
            C[__gi][__gj] = __gacc;
        }
    }
}
// SECOND REFERENCE.  Identical source text to k_base, contraction suppressed.
// It exists because gcc's chosen strategy for k_base is "vmulpd, then four
// in-order vaddsd" for the k-block body -- which exposes no a*b+c and so is not
// contracted -- but the SCALAR REMAINDER of that same vectorization (n mod 4
// elements) IS contracted, `vfmadd231sd`.  So the reference emission's own bits
// depend on n mod 4, and any arm that vectorizes differently has to be compared
// against both.
__attribute__((optimize("fp-contract=off")))
static void k_base_nc() {
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
static void k_base_serial() {
    for (size_t __gi = 0; __gi < MM; __gi++) {
        const ELEM* BR __growi = &A[__gi][0];
        ELEM prev = ELEM();
        for (size_t __gj = 0; __gj < PP; __gj++) {
            const ELEM* BR __growj = &B[__gj][0];
            ELEM __gacc = prev - prev;
            for (size_t __gk = 0; __gk < NN; __gk++) __gacc += __growi[__gk] * conj_scalar(__growj[__gk]);
            C[__gi][__gj] = __gacc; prev = __gacc;
        }
    }
}

// ------------------------------------------------------- the shipped jam
#define JAMDECL(r)  const ELEM* BR __growj##r = &B[__gj + r][0]; ELEM __gacc##r = ELEM();
#define JAMACC(r)   __gacc##r += __growi[__gk] * conj_scalar(__growj##r[__gk]);
#define JAMSTORE(r) C[__gi][__gj + r] = __gacc##r;
#define MAKEJAM(R, DECLS, ACCS, STORES)                                    \
static void k_jam##R() {                                                   \
    for (size_t __gi = 0; __gi < MM; __gi++) {                             \
        const ELEM* BR __growi = &A[__gi][0];                              \
        size_t __gj = 0;                                                   \
        for (; __gj + R <= PP; __gj += R) {                                \
            DECLS                                                          \
            for (size_t __gk = 0; __gk < NN; __gk++) { ACCS }              \
            STORES                                                         \
        }                                                                  \
        for (; __gj < PP; __gj++) {                                        \
            const ELEM* BR __growj = &B[__gj][0];                          \
            ELEM __gacc = ELEM();                                          \
            for (size_t __gk = 0; __gk < NN; __gk++) __gacc += __growi[__gk] * conj_scalar(__growj[__gk]); \
            C[__gi][__gj] = __gacc;                                        \
        }                                                                  \
    }                                                                      \
}
#define D2 JAMDECL(0) JAMDECL(1)
#define A2 JAMACC(0) JAMACC(1)
#define S2 JAMSTORE(0) JAMSTORE(1)
#define D4 D2 JAMDECL(2) JAMDECL(3)
#define A4 A2 JAMACC(2) JAMACC(3)
#define S4 S2 JAMSTORE(2) JAMSTORE(3)
#define D5 D4 JAMDECL(4)
#define A5 A4 JAMACC(4)
#define S5 S4 JAMSTORE(4)
#define D6 D5 JAMDECL(5)
#define A6 A5 JAMACC(5)
#define S6 S5 JAMSTORE(5)
#define D8 D6 JAMDECL(6) JAMDECL(7)
#define A8 A6 JAMACC(6) JAMACC(7)
#define S8 S6 JAMSTORE(6) JAMSTORE(7)
MAKEJAM(2, D2, A2, S2)
MAKEJAM(3, D2 JAMDECL(2), A2 JAMACC(2), S2 JAMSTORE(2))
MAKEJAM(4, D4, A4, S4)
MAKEJAM(5, D5, A5, S5)
MAKEJAM(6, D6, A6, S6)
MAKEJAM(8, D8, A8, S8)

// jam5 with vectorization off inside the function (correction 16's control)
__attribute__((optimize("no-tree-vectorize")))
static void k_jam5_novec() {
    for (size_t __gi = 0; __gi < MM; __gi++) {
        const ELEM* BR __growi = &A[__gi][0];
        size_t __gj = 0;
        for (; __gj + 5 <= PP; __gj += 5) {
            D5
            for (size_t __gk = 0; __gk < NN; __gk++) { A5 }
            S5
        }
        for (; __gj < PP; __gj++) {
            const ELEM* BR __growj = &B[__gj][0];
            ELEM __gacc = ELEM();
            for (size_t __gk = 0; __gk < NN; __gk++) __gacc += __growi[__gk] * conj_scalar(__growj[__gk]);
            C[__gi][__gj] = __gacc;
        }
    }
}

// ------------------------------------------------- packed j-major arms
// BT[k][j] contiguous in j, so N consecutive output cells vectorize with NO
// shuffle at all.  Each cell keeps its own ascending-k sum -> bitwise.
#define PDECL(r)  ELEM __pacc##r = ELEM();
#define PACC(r)   __pacc##r += av * conj_scalar(__bt[r]);
#define PSTORE(r) __crow[__gj + r] = __pacc##r;
#define MAKEPACK(N, DECLS, ACCS, STORES)                                   \
static void k_packT##N() {                                                 \
    packBT();                                                              \
    const ELEM* BR BT = poolBT;                                            \
    for (size_t __gi = 0; __gi < MM; __gi++) {                             \
        const ELEM* BR __growi = &A[__gi][0];                              \
        ELEM* BR __crow = C[__gi];                                         \
        size_t __gj = 0;                                                   \
        for (; __gj + N <= PP; __gj += N) {                                \
            DECLS                                                          \
            for (size_t __gk = 0; __gk < NN; __gk++) {                     \
                const ELEM av = __growi[__gk];                             \
                const ELEM* BR __bt = BT + __gk * (size_t)PP + __gj;       \
                ACCS                                                       \
            }                                                              \
            STORES                                                         \
        }                                                                  \
        for (; __gj < PP; __gj++) {                                        \
            const ELEM* BR __growj = &B[__gj][0];                          \
            ELEM __gacc = ELEM();                                          \
            for (size_t __gk = 0; __gk < NN; __gk++) __gacc += __growi[__gk] * conj_scalar(__growj[__gk]); \
            __crow[__gj] = __gacc;                                         \
        }                                                                  \
    }                                                                      \
}
#define PD4 PDECL(0) PDECL(1) PDECL(2) PDECL(3)
#define PA4 PACC(0) PACC(1) PACC(2) PACC(3)
#define PS4 PSTORE(0) PSTORE(1) PSTORE(2) PSTORE(3)
#define PD8 PD4 PDECL(4) PDECL(5) PDECL(6) PDECL(7)
#define PA8 PA4 PACC(4) PACC(5) PACC(6) PACC(7)
#define PS8 PS4 PSTORE(4) PSTORE(5) PSTORE(6) PSTORE(7)
#define PD16 PD8 PDECL(8) PDECL(9) PDECL(10) PDECL(11) PDECL(12) PDECL(13) PDECL(14) PDECL(15)
#define PA16 PA8 PACC(8) PACC(9) PACC(10) PACC(11) PACC(12) PACC(13) PACC(14) PACC(15)
#define PS16 PS8 PSTORE(8) PSTORE(9) PSTORE(10) PSTORE(11) PSTORE(12) PSTORE(13) PSTORE(14) PSTORE(15)
#define PD24 PD16 PDECL(16) PDECL(17) PDECL(18) PDECL(19) PDECL(20) PDECL(21) PDECL(22) PDECL(23)
#define PA24 PA16 PACC(16) PACC(17) PACC(18) PACC(19) PACC(20) PACC(21) PACC(22) PACC(23)
#define PS24 PS16 PSTORE(16) PSTORE(17) PSTORE(18) PSTORE(19) PSTORE(20) PSTORE(21) PSTORE(22) PSTORE(23)
MAKEPACK(4, PD4, PA4, PS4)
MAKEPACK(8, PD8, PA8, PS8)
MAKEPACK(16, PD16, PA16, PS16)
MAKEPACK(24, PD24, PA24, PS24)

// packed + i-blocked: IB rows of A at once, so BT is streamed once per IB rows.
#define IBDECL(ib, r) ELEM __qacc##ib##_##r = ELEM();
#define IBACC(ib, r)  __qacc##ib##_##r += av##ib * conj_scalar(__bt[r]);
#define IBSTORE(ib,r) C[__gi + ib][__gj + r] = __qacc##ib##_##r;
#define MAKEIB(IB, N, DECLS, LOADS, ACCS, STORES)                          \
static void k_packT##IB##x##N() {                                          \
    packBT();                                                              \
    const ELEM* BR BT = poolBT;                                            \
    size_t __gi = 0;                                                       \
    for (; __gi + IB <= MM; __gi += IB) {                                  \
        size_t __gj = 0;                                                   \
        for (; __gj + N <= PP; __gj += N) {                                \
            DECLS                                                          \
            for (size_t __gk = 0; __gk < NN; __gk++) {                     \
                LOADS                                                      \
                const ELEM* BR __bt = BT + __gk * (size_t)PP + __gj;       \
                ACCS                                                       \
            }                                                              \
            STORES                                                         \
        }                                                                  \
        for (; __gj < PP; __gj++)                                          \
            for (size_t z = 0; z < IB; z++) {                              \
                const ELEM* BR __growj = &B[__gj][0];                      \
                const ELEM* BR __growi = &A[__gi + z][0];                  \
                ELEM __gacc = ELEM();                                      \
                for (size_t __gk = 0; __gk < NN; __gk++) __gacc += __growi[__gk] * conj_scalar(__growj[__gk]); \
                C[__gi + z][__gj] = __gacc;                                \
            }                                                              \
    }                                                                      \
    for (; __gi < MM; __gi++) {                                            \
        const ELEM* BR __growi = &A[__gi][0];                              \
        for (size_t __gj = 0; __gj < PP; __gj++) {                         \
            const ELEM* BR __growj = &B[__gj][0];                          \
            ELEM __gacc = ELEM();                                          \
            for (size_t __gk = 0; __gk < NN; __gk++) __gacc += __growi[__gk] * conj_scalar(__growj[__gk]); \
            C[__gi][__gj] = __gacc;                                        \
        }                                                                  \
    }                                                                      \
}
#define L2 const ELEM av0 = A[__gi][__gk]; const ELEM av1 = A[__gi+1][__gk];
#define L4 L2 const ELEM av2 = A[__gi+2][__gk]; const ELEM av3 = A[__gi+3][__gk];
#define QD8(ib) IBDECL(ib,0) IBDECL(ib,1) IBDECL(ib,2) IBDECL(ib,3) IBDECL(ib,4) IBDECL(ib,5) IBDECL(ib,6) IBDECL(ib,7)
#define QA8(ib) IBACC(ib,0) IBACC(ib,1) IBACC(ib,2) IBACC(ib,3) IBACC(ib,4) IBACC(ib,5) IBACC(ib,6) IBACC(ib,7)
#define QS8(ib) IBSTORE(ib,0) IBSTORE(ib,1) IBSTORE(ib,2) IBSTORE(ib,3) IBSTORE(ib,4) IBSTORE(ib,5) IBSTORE(ib,6) IBSTORE(ib,7)
#define QD4(ib) IBDECL(ib,0) IBDECL(ib,1) IBDECL(ib,2) IBDECL(ib,3)
#define QA4(ib) IBACC(ib,0) IBACC(ib,1) IBACC(ib,2) IBACC(ib,3)
#define QS4(ib) IBSTORE(ib,0) IBSTORE(ib,1) IBSTORE(ib,2) IBSTORE(ib,3)
MAKEIB(2, 8, QD8(0) QD8(1), L2, QA8(0) QA8(1), QS8(0) QS8(1))
MAKEIB(4, 4, QD4(0) QD4(1) QD4(2) QD4(3), L4, QA4(0) QA4(1) QA4(2) QA4(3), QS4(0) QS4(1) QS4(2) QS4(3))
MAKEIB(4, 8, QD8(0) QD8(1) QD8(2) QD8(3), L4, QA8(0) QA8(1) QA8(2) QA8(3), QS8(0) QS8(1) QS8(2) QS8(3))

// ---- the same packed arms with CONTRACTION SUPPRESSED --------------------
// gcc contracts the packed body to vfmadd231pd at -ffp-contract=fast (verified
// in the .s), which is bit-changing against the reference nest -- gcc happens
// NOT to contract the reference, because its chosen strategy there (vmulpd then
// four in-order vaddsd) never exposes an a*b+c.  So the packed form is only a
// DEFAULT-ON candidate if contraction is suppressed.  #pragma STDC FP_CONTRACT
// is ignored by g++ (tested), and #pragma GCC optimize is illegal in a function
// body, so the only in-source lever is a FUNCTION attribute -- i.e. the emitter
// would have to hoist the nest into its own function, which plan-unroll-and-jam
// §1 already costed.  clang's equivalent, #pragma clang fp contract(off), IS
// legal in a body.
#define MAKEPACKNC(N, DECLS, ACCS, STORES)                                 \
__attribute__((optimize("fp-contract=off")))                               \
static void k_packTnc##N() {                                               \
    packBT();                                                              \
    const ELEM* BR BT = poolBT;                                            \
    for (size_t __gi = 0; __gi < MM; __gi++) {                             \
        const ELEM* BR __growi = &A[__gi][0];                              \
        ELEM* BR __crow = C[__gi];                                         \
        size_t __gj = 0;                                                   \
        for (; __gj + N <= PP; __gj += N) {                                \
            DECLS                                                          \
            for (size_t __gk = 0; __gk < NN; __gk++) {                     \
                const ELEM av = __growi[__gk];                             \
                const ELEM* BR __bt = BT + __gk * (size_t)PP + __gj;       \
                ACCS                                                       \
            }                                                              \
            STORES                                                         \
        }                                                                  \
        for (; __gj < PP; __gj++) {                                        \
            const ELEM* BR __growj = &B[__gj][0];                          \
            ELEM __gacc = ELEM();                                          \
            for (size_t __gk = 0; __gk < NN; __gk++) __gacc += __growi[__gk] * conj_scalar(__growj[__gk]); \
            __crow[__gj] = __gacc;                                         \
        }                                                                  \
    }                                                                      \
}
MAKEPACKNC(4, PD4, PA4, PS4)
MAKEPACKNC(8, PD8, PA8, PS8)
MAKEPACKNC(16, PD16, PA16, PS16)
MAKEPACKNC(24, PD24, PA24, PS24)

// contraction-suppressed AND i-blocked
#define MAKEIBNC(IB, N, DECLS, LOADS, ACCS, STORES)                        \
__attribute__((optimize("fp-contract=off")))                               \
static void k_packTnc##IB##x##N() {                                        \
    packBT();                                                              \
    const ELEM* BR BT = poolBT;                                            \
    size_t __gi = 0;                                                       \
    for (; __gi + IB <= MM; __gi += IB) {                                  \
        size_t __gj = 0;                                                   \
        for (; __gj + N <= PP; __gj += N) {                                \
            DECLS                                                          \
            for (size_t __gk = 0; __gk < NN; __gk++) {                     \
                LOADS                                                      \
                const ELEM* BR __bt = BT + __gk * (size_t)PP + __gj;       \
                ACCS                                                       \
            }                                                              \
            STORES                                                         \
        }                                                                  \
        for (; __gj < PP; __gj++)                                          \
            for (size_t z = 0; z < IB; z++) {                              \
                const ELEM* BR __growj = &B[__gj][0];                      \
                const ELEM* BR __growi = &A[__gi + z][0];                  \
                ELEM __gacc = ELEM();                                      \
                for (size_t __gk = 0; __gk < NN; __gk++) __gacc += __growi[__gk] * conj_scalar(__growj[__gk]); \
                C[__gi + z][__gj] = __gacc;                                \
            }                                                              \
    }                                                                      \
    for (; __gi < MM; __gi++) {                                            \
        const ELEM* BR __growi = &A[__gi][0];                              \
        for (size_t __gj = 0; __gj < PP; __gj++) {                         \
            const ELEM* BR __growj = &B[__gj][0];                          \
            ELEM __gacc = ELEM();                                          \
            for (size_t __gk = 0; __gk < NN; __gk++) __gacc += __growi[__gk] * conj_scalar(__growj[__gk]); \
            C[__gi][__gj] = __gacc;                                        \
        }                                                                  \
    }                                                                      \
}
MAKEIBNC(2, 8, QD8(0) QD8(1), L2, QA8(0) QA8(1), QS8(0) QS8(1))
MAKEIBNC(4, 8, QD8(0) QD8(1) QD8(2) QD8(3), L4, QA8(0) QA8(1) QA8(2) QA8(3), QS8(0) QS8(1) QS8(2) QS8(3))

// -------------------------------------------- intrinsics control (double only)
#ifndef ELEM_FLOAT
// 4 YMM accumulators = 16 output cells, explicit vmulpd+vaddpd (NO fma, so the
// arithmetic matches the reference bit for bit).
static void k_packTi16() {
    packBT();
    const double* BR BT = poolBT;
    for (size_t gi = 0; gi < MM; gi++) {
        const double* BR ai = &A[gi][0];
        double* BR crow = C[gi];
        size_t gj = 0;
        for (; gj + 16 <= PP; gj += 16) {
            __m256d a0 = _mm256_setzero_pd(), a1 = _mm256_setzero_pd();
            __m256d a2 = _mm256_setzero_pd(), a3 = _mm256_setzero_pd();
            for (size_t gk = 0; gk < NN; gk++) {
                __m256d av = _mm256_broadcast_sd(ai + gk);
                const double* bt = BT + gk * (size_t)PP + gj;
                a0 = _mm256_add_pd(a0, _mm256_mul_pd(av, _mm256_loadu_pd(bt)));
                a1 = _mm256_add_pd(a1, _mm256_mul_pd(av, _mm256_loadu_pd(bt + 4)));
                a2 = _mm256_add_pd(a2, _mm256_mul_pd(av, _mm256_loadu_pd(bt + 8)));
                a3 = _mm256_add_pd(a3, _mm256_mul_pd(av, _mm256_loadu_pd(bt + 12)));
            }
            _mm256_storeu_pd(crow + gj, a0); _mm256_storeu_pd(crow + gj + 4, a1);
            _mm256_storeu_pd(crow + gj + 8, a2); _mm256_storeu_pd(crow + gj + 12, a3);
        }
        for (; gj < PP; gj++) {
            const double* BR bj = &B[gj][0];
            double acc = 0.0;
            for (size_t gk = 0; gk < NN; gk++) acc += ai[gk] * conj_scalar(bj[gk]);
            crow[gj] = acc;
        }
    }
}
// same but with FMA -- NOT bitwise, measures what contraction would buy here
static void k_packTi16_fma() {
    packBT();
    const double* BR BT = poolBT;
    for (size_t gi = 0; gi < MM; gi++) {
        const double* BR ai = &A[gi][0];
        double* BR crow = C[gi];
        size_t gj = 0;
        for (; gj + 16 <= PP; gj += 16) {
            __m256d a0 = _mm256_setzero_pd(), a1 = _mm256_setzero_pd();
            __m256d a2 = _mm256_setzero_pd(), a3 = _mm256_setzero_pd();
            for (size_t gk = 0; gk < NN; gk++) {
                __m256d av = _mm256_broadcast_sd(ai + gk);
                const double* bt = BT + gk * (size_t)PP + gj;
                a0 = _mm256_fmadd_pd(av, _mm256_loadu_pd(bt), a0);
                a1 = _mm256_fmadd_pd(av, _mm256_loadu_pd(bt + 4), a1);
                a2 = _mm256_fmadd_pd(av, _mm256_loadu_pd(bt + 8), a2);
                a3 = _mm256_fmadd_pd(av, _mm256_loadu_pd(bt + 12), a3);
            }
            _mm256_storeu_pd(crow + gj, a0); _mm256_storeu_pd(crow + gj + 4, a1);
            _mm256_storeu_pd(crow + gj + 8, a2); _mm256_storeu_pd(crow + gj + 12, a3);
        }
        for (; gj < PP; gj++) {
            const double* BR bj = &B[gj][0];
            double acc = 0.0;
            for (size_t gk = 0; gk < NN; gk++) acc = std::fma(ai[gk], conj_scalar(bj[gk]), acc);
            crow[gj] = acc;
        }
    }
}
#endif

// ------------------------------------------- LICENSED k-lane fold (reads NO)
// what BLADE_FP_REASSOC's fpReassocLaneStmts does: split ONE cell's fold across
// 4 lane accumulators.  Reassociates, so it needs the licence.  Included to test
// the README claim that the licensed fold split measures 1.00x on the real shape.
static void k_kfold4() {
    for (size_t gi = 0; gi < MM; gi++) {
        const ELEM* BR ai = &A[gi][0];
        for (size_t gj = 0; gj < PP; gj++) {
            const ELEM* BR bj = &B[gj][0];
            ELEM l0 = ELEM(), l1 = ELEM(), l2 = ELEM(), l3 = ELEM();
            size_t gk = 0;
            for (; gk + 4 <= NN; gk += 4) {
                l0 += ai[gk+0] * conj_scalar(bj[gk+0]);
                l1 += ai[gk+1] * conj_scalar(bj[gk+1]);
                l2 += ai[gk+2] * conj_scalar(bj[gk+2]);
                l3 += ai[gk+3] * conj_scalar(bj[gk+3]);
            }
            ELEM acc = (l0 + l1) + (l2 + l3);
            for (; gk < NN; gk++) acc += ai[gk] * conj_scalar(bj[gk]);
            C[gi][gj] = acc;
        }
    }
}

// ------------------------------------------------------------- calibration
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

// ------------------------------------------------------------------ driver
struct K { const char* name; void (*fn)(); };
static K kernels[] = {
    {"base",       k_base},
    {"base_nc",    k_base_nc},
    {"base_fma",   k_base_fma},
    {"base_ser",   k_base_serial},
    {"jam2",       k_jam2},
    {"jam3",       k_jam3},
    {"jam4",       k_jam4},
    {"jam5",       k_jam5},
    {"jam6",       k_jam6},
    {"jam8",       k_jam8},
    {"jam5_novec", k_jam5_novec},
    {"packT4",     k_packT4},
    {"packT8",     k_packT8},
    {"packT16",    k_packT16},
    {"packT24",    k_packT24},
    {"packT2x8",   k_packT2x8},
    {"packT4x4",   k_packT4x4},
    {"packT4x8",   k_packT4x8},
    {"packTnc4",   k_packTnc4},
    {"packTnc8",   k_packTnc8},
    {"packTnc16",  k_packTnc16},
    {"packTnc24",  k_packTnc24},
    {"packTnc2x8", k_packTnc2x8},
    {"packTnc4x8", k_packTnc4x8},
#ifndef ELEM_FLOAT
    {"packTi16",   k_packTi16},
    {"packTi16f",  k_packTi16_fma},
#endif
    {"kfold4",     k_kfold4},
};
static const int NK = sizeof(kernels) / sizeof(kernels[0]);

int main(int argc, char** argv) {
    int rounds = argc > 1 ? atoi(argv[1]) : 5;
    int reps   = argc > 2 ? atoi(argv[2]) : 3;
    const char* filt = argc > 3 ? argv[3] : 0;  // comma list, or null = all
    setup();
    double hz = calib_clock();
    std::vector<int> sel;
    for (int i = 0; i < NK; i++) {
        if (!filt) { sel.push_back(i); continue; }
        std::string f = std::string(",") + filt + ",";
        if (i == 0 || f.find(std::string(",") + kernels[i].name + ",") != std::string::npos) sel.push_back(i);
    }
    std::vector<std::vector<double> > t(NK);
    size_t bytes = sizeof(ELEM) * (size_t)MM * PP;
    ELEM* ref = (ELEM*)malloc(bytes);
    ELEM* refnc = (ELEM*)malloc(bytes);
    std::vector<int> bitok(NK, 0), bitnc(NK, 0);
    // both references first, whatever the filter says
    memset(Cpool, 0xA5, bytes); k_base();    asm volatile("" ::: "memory"); memcpy(ref, Cpool, bytes);
    memset(Cpool, 0xA5, bytes); k_base_nc(); asm volatile("" ::: "memory"); memcpy(refnc, Cpool, bytes);
    for (size_t z = 0; z < sel.size(); z++) {
        int i = sel[z];
        memset(Cpool, 0xA5, bytes);
        kernels[i].fn();
        asm volatile("" ::: "memory");
        bitok[i] = (memcmp(ref,   Cpool, bytes) == 0);
        bitnc[i] = (memcmp(refnc, Cpool, bytes) == 0);
    }
    for (int r = 0; r < rounds; r++)
        for (size_t z = 0; z < sel.size(); z++) {
            int i = sel[z];
            for (int q = 0; q < reps; q++) {
                auto t0 = clk::now(); kernels[i].fn(); asm volatile("" ::: "memory"); auto t1 = clk::now();
                t[i].push_back(secs(t0, t1));
            }
        }
    double checksum = 0; for (size_t z = 0; z < (size_t)MM * PP; z += 997) checksum += (double)Cpool[z];
    printf("# elem=%s m=%d n=%d p=%d  clock=%.3f GHz  macs=%.5g  chk=%.6g\n",
           ELEMNAME, MM, NN, PP, hz / 1e9, (double)MM * NN * PP, checksum);
    printf("%-11s %11s %9s %9s %9s %8s %8s\n", "kernel", "median_s", "cyc/MAC", "GMAC/s", "vs_base", "==base", "==basenc");
    double base = 0;
    for (size_t z = 0; z < sel.size(); z++) {
        int i = sel[z];
        std::sort(t[i].begin(), t[i].end());
        double m = t[i][t[i].size() / 2];
        if (i == 0) base = m;
        double macs = (double)MM * NN * PP;
        printf("%-11s %11.6f %9.4f %9.4f %9.4f %8s %8s\n", kernels[i].name, m,
               m * hz / macs, macs / m / 1e9, base / m,
               bitok[i] ? "yes" : "NO", bitnc[i] ? "yes" : "NO");
    }
    return 0;
}
