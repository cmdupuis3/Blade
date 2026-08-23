// small_p2.cpp -- originally generated (17 near-identical p-cases); the
// generator was never committed, so THIS FILE is the source of record.
//
// The dense-gram jam SHIPPED with a fixed tile width R=5 and a scalar
// remainder; this instrument swept R against p, showed a fixed width gets
// 41-100% of the best available R with its worst cases at exactly the corpus
// extents, and a3837e6 then made the emitter DERIVE R from a literal p.  The
// fixed R=5 arm below is therefore the emitter's RUNTIME-EXTENT path (the one
// place a width cannot be derived), and the historical pre-a3837e6 emission.
//
// m and n are large so the nest is what is timed, not the call.  Accumulators
// are separate named locals (the array form spills -- the emitter's own rule).

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
#define PMAX 64
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

template <int P>
static void a_jam2() {
    for (size_t __gi = 0; __gi < MM; __gi++) {
        const ELEM* BR __growi = &A[__gi][0];
        size_t __gj = 0;
        for (; __gj + 2 <= P; __gj += 2) {
            const ELEM* BR __growj0 = &B[__gj + 0][0]; ELEM __gacc0 = ELEM();
            const ELEM* BR __growj1 = &B[__gj + 1][0]; ELEM __gacc1 = ELEM();
            for (size_t __gk = 0; __gk < NN; __gk++) {
                __gacc0 += __growi[__gk] * conj_scalar(__growj0[__gk]);
                __gacc1 += __growi[__gk] * conj_scalar(__growj1[__gk]);
            }
            C[__gi][__gj + 0] = __gacc0;
            C[__gi][__gj + 1] = __gacc1;
        }
        for (; __gj < P; __gj++) {
            const ELEM* BR __growj = &B[__gj][0];
            ELEM __gacc = ELEM();
            for (size_t __gk = 0; __gk < NN; __gk++) __gacc += __growi[__gk] * conj_scalar(__growj[__gk]);
            C[__gi][__gj] = __gacc;
        }
    }
}

template <int P>
static void a_jam3() {
    for (size_t __gi = 0; __gi < MM; __gi++) {
        const ELEM* BR __growi = &A[__gi][0];
        size_t __gj = 0;
        for (; __gj + 3 <= P; __gj += 3) {
            const ELEM* BR __growj0 = &B[__gj + 0][0]; ELEM __gacc0 = ELEM();
            const ELEM* BR __growj1 = &B[__gj + 1][0]; ELEM __gacc1 = ELEM();
            const ELEM* BR __growj2 = &B[__gj + 2][0]; ELEM __gacc2 = ELEM();
            for (size_t __gk = 0; __gk < NN; __gk++) {
                __gacc0 += __growi[__gk] * conj_scalar(__growj0[__gk]);
                __gacc1 += __growi[__gk] * conj_scalar(__growj1[__gk]);
                __gacc2 += __growi[__gk] * conj_scalar(__growj2[__gk]);
            }
            C[__gi][__gj + 0] = __gacc0;
            C[__gi][__gj + 1] = __gacc1;
            C[__gi][__gj + 2] = __gacc2;
        }
        for (; __gj < P; __gj++) {
            const ELEM* BR __growj = &B[__gj][0];
            ELEM __gacc = ELEM();
            for (size_t __gk = 0; __gk < NN; __gk++) __gacc += __growi[__gk] * conj_scalar(__growj[__gk]);
            C[__gi][__gj] = __gacc;
        }
    }
}

template <int P>
static void a_jam4() {
    for (size_t __gi = 0; __gi < MM; __gi++) {
        const ELEM* BR __growi = &A[__gi][0];
        size_t __gj = 0;
        for (; __gj + 4 <= P; __gj += 4) {
            const ELEM* BR __growj0 = &B[__gj + 0][0]; ELEM __gacc0 = ELEM();
            const ELEM* BR __growj1 = &B[__gj + 1][0]; ELEM __gacc1 = ELEM();
            const ELEM* BR __growj2 = &B[__gj + 2][0]; ELEM __gacc2 = ELEM();
            const ELEM* BR __growj3 = &B[__gj + 3][0]; ELEM __gacc3 = ELEM();
            for (size_t __gk = 0; __gk < NN; __gk++) {
                __gacc0 += __growi[__gk] * conj_scalar(__growj0[__gk]);
                __gacc1 += __growi[__gk] * conj_scalar(__growj1[__gk]);
                __gacc2 += __growi[__gk] * conj_scalar(__growj2[__gk]);
                __gacc3 += __growi[__gk] * conj_scalar(__growj3[__gk]);
            }
            C[__gi][__gj + 0] = __gacc0;
            C[__gi][__gj + 1] = __gacc1;
            C[__gi][__gj + 2] = __gacc2;
            C[__gi][__gj + 3] = __gacc3;
        }
        for (; __gj < P; __gj++) {
            const ELEM* BR __growj = &B[__gj][0];
            ELEM __gacc = ELEM();
            for (size_t __gk = 0; __gk < NN; __gk++) __gacc += __growi[__gk] * conj_scalar(__growj[__gk]);
            C[__gi][__gj] = __gacc;
        }
    }
}

template <int P>
static void a_jam5() {
    for (size_t __gi = 0; __gi < MM; __gi++) {
        const ELEM* BR __growi = &A[__gi][0];
        size_t __gj = 0;
        for (; __gj + 5 <= P; __gj += 5) {
            const ELEM* BR __growj0 = &B[__gj + 0][0]; ELEM __gacc0 = ELEM();
            const ELEM* BR __growj1 = &B[__gj + 1][0]; ELEM __gacc1 = ELEM();
            const ELEM* BR __growj2 = &B[__gj + 2][0]; ELEM __gacc2 = ELEM();
            const ELEM* BR __growj3 = &B[__gj + 3][0]; ELEM __gacc3 = ELEM();
            const ELEM* BR __growj4 = &B[__gj + 4][0]; ELEM __gacc4 = ELEM();
            for (size_t __gk = 0; __gk < NN; __gk++) {
                __gacc0 += __growi[__gk] * conj_scalar(__growj0[__gk]);
                __gacc1 += __growi[__gk] * conj_scalar(__growj1[__gk]);
                __gacc2 += __growi[__gk] * conj_scalar(__growj2[__gk]);
                __gacc3 += __growi[__gk] * conj_scalar(__growj3[__gk]);
                __gacc4 += __growi[__gk] * conj_scalar(__growj4[__gk]);
            }
            C[__gi][__gj + 0] = __gacc0;
            C[__gi][__gj + 1] = __gacc1;
            C[__gi][__gj + 2] = __gacc2;
            C[__gi][__gj + 3] = __gacc3;
            C[__gi][__gj + 4] = __gacc4;
        }
        for (; __gj < P; __gj++) {
            const ELEM* BR __growj = &B[__gj][0];
            ELEM __gacc = ELEM();
            for (size_t __gk = 0; __gk < NN; __gk++) __gacc += __growi[__gk] * conj_scalar(__growj[__gk]);
            C[__gi][__gj] = __gacc;
        }
    }
}

template <int P>
static void a_jam6() {
    for (size_t __gi = 0; __gi < MM; __gi++) {
        const ELEM* BR __growi = &A[__gi][0];
        size_t __gj = 0;
        for (; __gj + 6 <= P; __gj += 6) {
            const ELEM* BR __growj0 = &B[__gj + 0][0]; ELEM __gacc0 = ELEM();
            const ELEM* BR __growj1 = &B[__gj + 1][0]; ELEM __gacc1 = ELEM();
            const ELEM* BR __growj2 = &B[__gj + 2][0]; ELEM __gacc2 = ELEM();
            const ELEM* BR __growj3 = &B[__gj + 3][0]; ELEM __gacc3 = ELEM();
            const ELEM* BR __growj4 = &B[__gj + 4][0]; ELEM __gacc4 = ELEM();
            const ELEM* BR __growj5 = &B[__gj + 5][0]; ELEM __gacc5 = ELEM();
            for (size_t __gk = 0; __gk < NN; __gk++) {
                __gacc0 += __growi[__gk] * conj_scalar(__growj0[__gk]);
                __gacc1 += __growi[__gk] * conj_scalar(__growj1[__gk]);
                __gacc2 += __growi[__gk] * conj_scalar(__growj2[__gk]);
                __gacc3 += __growi[__gk] * conj_scalar(__growj3[__gk]);
                __gacc4 += __growi[__gk] * conj_scalar(__growj4[__gk]);
                __gacc5 += __growi[__gk] * conj_scalar(__growj5[__gk]);
            }
            C[__gi][__gj + 0] = __gacc0;
            C[__gi][__gj + 1] = __gacc1;
            C[__gi][__gj + 2] = __gacc2;
            C[__gi][__gj + 3] = __gacc3;
            C[__gi][__gj + 4] = __gacc4;
            C[__gi][__gj + 5] = __gacc5;
        }
        for (; __gj < P; __gj++) {
            const ELEM* BR __growj = &B[__gj][0];
            ELEM __gacc = ELEM();
            for (size_t __gk = 0; __gk < NN; __gk++) __gacc += __growi[__gk] * conj_scalar(__growj[__gk]);
            C[__gi][__gj] = __gacc;
        }
    }
}

template <int P>
static void a_jam7() {
    for (size_t __gi = 0; __gi < MM; __gi++) {
        const ELEM* BR __growi = &A[__gi][0];
        size_t __gj = 0;
        for (; __gj + 7 <= P; __gj += 7) {
            const ELEM* BR __growj0 = &B[__gj + 0][0]; ELEM __gacc0 = ELEM();
            const ELEM* BR __growj1 = &B[__gj + 1][0]; ELEM __gacc1 = ELEM();
            const ELEM* BR __growj2 = &B[__gj + 2][0]; ELEM __gacc2 = ELEM();
            const ELEM* BR __growj3 = &B[__gj + 3][0]; ELEM __gacc3 = ELEM();
            const ELEM* BR __growj4 = &B[__gj + 4][0]; ELEM __gacc4 = ELEM();
            const ELEM* BR __growj5 = &B[__gj + 5][0]; ELEM __gacc5 = ELEM();
            const ELEM* BR __growj6 = &B[__gj + 6][0]; ELEM __gacc6 = ELEM();
            for (size_t __gk = 0; __gk < NN; __gk++) {
                __gacc0 += __growi[__gk] * conj_scalar(__growj0[__gk]);
                __gacc1 += __growi[__gk] * conj_scalar(__growj1[__gk]);
                __gacc2 += __growi[__gk] * conj_scalar(__growj2[__gk]);
                __gacc3 += __growi[__gk] * conj_scalar(__growj3[__gk]);
                __gacc4 += __growi[__gk] * conj_scalar(__growj4[__gk]);
                __gacc5 += __growi[__gk] * conj_scalar(__growj5[__gk]);
                __gacc6 += __growi[__gk] * conj_scalar(__growj6[__gk]);
            }
            C[__gi][__gj + 0] = __gacc0;
            C[__gi][__gj + 1] = __gacc1;
            C[__gi][__gj + 2] = __gacc2;
            C[__gi][__gj + 3] = __gacc3;
            C[__gi][__gj + 4] = __gacc4;
            C[__gi][__gj + 5] = __gacc5;
            C[__gi][__gj + 6] = __gacc6;
        }
        for (; __gj < P; __gj++) {
            const ELEM* BR __growj = &B[__gj][0];
            ELEM __gacc = ELEM();
            for (size_t __gk = 0; __gk < NN; __gk++) __gacc += __growi[__gk] * conj_scalar(__growj[__gk]);
            C[__gi][__gj] = __gacc;
        }
    }
}

template <int P>
static void a_jam8() {
    for (size_t __gi = 0; __gi < MM; __gi++) {
        const ELEM* BR __growi = &A[__gi][0];
        size_t __gj = 0;
        for (; __gj + 8 <= P; __gj += 8) {
            const ELEM* BR __growj0 = &B[__gj + 0][0]; ELEM __gacc0 = ELEM();
            const ELEM* BR __growj1 = &B[__gj + 1][0]; ELEM __gacc1 = ELEM();
            const ELEM* BR __growj2 = &B[__gj + 2][0]; ELEM __gacc2 = ELEM();
            const ELEM* BR __growj3 = &B[__gj + 3][0]; ELEM __gacc3 = ELEM();
            const ELEM* BR __growj4 = &B[__gj + 4][0]; ELEM __gacc4 = ELEM();
            const ELEM* BR __growj5 = &B[__gj + 5][0]; ELEM __gacc5 = ELEM();
            const ELEM* BR __growj6 = &B[__gj + 6][0]; ELEM __gacc6 = ELEM();
            const ELEM* BR __growj7 = &B[__gj + 7][0]; ELEM __gacc7 = ELEM();
            for (size_t __gk = 0; __gk < NN; __gk++) {
                __gacc0 += __growi[__gk] * conj_scalar(__growj0[__gk]);
                __gacc1 += __growi[__gk] * conj_scalar(__growj1[__gk]);
                __gacc2 += __growi[__gk] * conj_scalar(__growj2[__gk]);
                __gacc3 += __growi[__gk] * conj_scalar(__growj3[__gk]);
                __gacc4 += __growi[__gk] * conj_scalar(__growj4[__gk]);
                __gacc5 += __growi[__gk] * conj_scalar(__growj5[__gk]);
                __gacc6 += __growi[__gk] * conj_scalar(__growj6[__gk]);
                __gacc7 += __growi[__gk] * conj_scalar(__growj7[__gk]);
            }
            C[__gi][__gj + 0] = __gacc0;
            C[__gi][__gj + 1] = __gacc1;
            C[__gi][__gj + 2] = __gacc2;
            C[__gi][__gj + 3] = __gacc3;
            C[__gi][__gj + 4] = __gacc4;
            C[__gi][__gj + 5] = __gacc5;
            C[__gi][__gj + 6] = __gacc6;
            C[__gi][__gj + 7] = __gacc7;
        }
        for (; __gj < P; __gj++) {
            const ELEM* BR __growj = &B[__gj][0];
            ELEM __gacc = ELEM();
            for (size_t __gk = 0; __gk < NN; __gk++) __gacc += __growi[__gk] * conj_scalar(__growj[__gk]);
            C[__gi][__gj] = __gacc;
        }
    }
}

template <int P>
static void a_jam9() {
    for (size_t __gi = 0; __gi < MM; __gi++) {
        const ELEM* BR __growi = &A[__gi][0];
        size_t __gj = 0;
        for (; __gj + 9 <= P; __gj += 9) {
            const ELEM* BR __growj0 = &B[__gj + 0][0]; ELEM __gacc0 = ELEM();
            const ELEM* BR __growj1 = &B[__gj + 1][0]; ELEM __gacc1 = ELEM();
            const ELEM* BR __growj2 = &B[__gj + 2][0]; ELEM __gacc2 = ELEM();
            const ELEM* BR __growj3 = &B[__gj + 3][0]; ELEM __gacc3 = ELEM();
            const ELEM* BR __growj4 = &B[__gj + 4][0]; ELEM __gacc4 = ELEM();
            const ELEM* BR __growj5 = &B[__gj + 5][0]; ELEM __gacc5 = ELEM();
            const ELEM* BR __growj6 = &B[__gj + 6][0]; ELEM __gacc6 = ELEM();
            const ELEM* BR __growj7 = &B[__gj + 7][0]; ELEM __gacc7 = ELEM();
            const ELEM* BR __growj8 = &B[__gj + 8][0]; ELEM __gacc8 = ELEM();
            for (size_t __gk = 0; __gk < NN; __gk++) {
                __gacc0 += __growi[__gk] * conj_scalar(__growj0[__gk]);
                __gacc1 += __growi[__gk] * conj_scalar(__growj1[__gk]);
                __gacc2 += __growi[__gk] * conj_scalar(__growj2[__gk]);
                __gacc3 += __growi[__gk] * conj_scalar(__growj3[__gk]);
                __gacc4 += __growi[__gk] * conj_scalar(__growj4[__gk]);
                __gacc5 += __growi[__gk] * conj_scalar(__growj5[__gk]);
                __gacc6 += __growi[__gk] * conj_scalar(__growj6[__gk]);
                __gacc7 += __growi[__gk] * conj_scalar(__growj7[__gk]);
                __gacc8 += __growi[__gk] * conj_scalar(__growj8[__gk]);
            }
            C[__gi][__gj + 0] = __gacc0;
            C[__gi][__gj + 1] = __gacc1;
            C[__gi][__gj + 2] = __gacc2;
            C[__gi][__gj + 3] = __gacc3;
            C[__gi][__gj + 4] = __gacc4;
            C[__gi][__gj + 5] = __gacc5;
            C[__gi][__gj + 6] = __gacc6;
            C[__gi][__gj + 7] = __gacc7;
            C[__gi][__gj + 8] = __gacc8;
        }
        for (; __gj < P; __gj++) {
            const ELEM* BR __growj = &B[__gj][0];
            ELEM __gacc = ELEM();
            for (size_t __gk = 0; __gk < NN; __gk++) __gacc += __growi[__gk] * conj_scalar(__growj[__gk]);
            C[__gi][__gj] = __gacc;
        }
    }
}

template <int P>
static void a_jam10() {
    for (size_t __gi = 0; __gi < MM; __gi++) {
        const ELEM* BR __growi = &A[__gi][0];
        size_t __gj = 0;
        for (; __gj + 10 <= P; __gj += 10) {
            const ELEM* BR __growj0 = &B[__gj + 0][0]; ELEM __gacc0 = ELEM();
            const ELEM* BR __growj1 = &B[__gj + 1][0]; ELEM __gacc1 = ELEM();
            const ELEM* BR __growj2 = &B[__gj + 2][0]; ELEM __gacc2 = ELEM();
            const ELEM* BR __growj3 = &B[__gj + 3][0]; ELEM __gacc3 = ELEM();
            const ELEM* BR __growj4 = &B[__gj + 4][0]; ELEM __gacc4 = ELEM();
            const ELEM* BR __growj5 = &B[__gj + 5][0]; ELEM __gacc5 = ELEM();
            const ELEM* BR __growj6 = &B[__gj + 6][0]; ELEM __gacc6 = ELEM();
            const ELEM* BR __growj7 = &B[__gj + 7][0]; ELEM __gacc7 = ELEM();
            const ELEM* BR __growj8 = &B[__gj + 8][0]; ELEM __gacc8 = ELEM();
            const ELEM* BR __growj9 = &B[__gj + 9][0]; ELEM __gacc9 = ELEM();
            for (size_t __gk = 0; __gk < NN; __gk++) {
                __gacc0 += __growi[__gk] * conj_scalar(__growj0[__gk]);
                __gacc1 += __growi[__gk] * conj_scalar(__growj1[__gk]);
                __gacc2 += __growi[__gk] * conj_scalar(__growj2[__gk]);
                __gacc3 += __growi[__gk] * conj_scalar(__growj3[__gk]);
                __gacc4 += __growi[__gk] * conj_scalar(__growj4[__gk]);
                __gacc5 += __growi[__gk] * conj_scalar(__growj5[__gk]);
                __gacc6 += __growi[__gk] * conj_scalar(__growj6[__gk]);
                __gacc7 += __growi[__gk] * conj_scalar(__growj7[__gk]);
                __gacc8 += __growi[__gk] * conj_scalar(__growj8[__gk]);
                __gacc9 += __growi[__gk] * conj_scalar(__growj9[__gk]);
            }
            C[__gi][__gj + 0] = __gacc0;
            C[__gi][__gj + 1] = __gacc1;
            C[__gi][__gj + 2] = __gacc2;
            C[__gi][__gj + 3] = __gacc3;
            C[__gi][__gj + 4] = __gacc4;
            C[__gi][__gj + 5] = __gacc5;
            C[__gi][__gj + 6] = __gacc6;
            C[__gi][__gj + 7] = __gacc7;
            C[__gi][__gj + 8] = __gacc8;
            C[__gi][__gj + 9] = __gacc9;
        }
        for (; __gj < P; __gj++) {
            const ELEM* BR __growj = &B[__gj][0];
            ELEM __gacc = ELEM();
            for (size_t __gk = 0; __gk < NN; __gk++) __gacc += __growi[__gk] * conj_scalar(__growj[__gk]);
            C[__gi][__gj] = __gacc;
        }
    }
}

template <int P>
static void a_jam12() {
    for (size_t __gi = 0; __gi < MM; __gi++) {
        const ELEM* BR __growi = &A[__gi][0];
        size_t __gj = 0;
        for (; __gj + 12 <= P; __gj += 12) {
            const ELEM* BR __growj0 = &B[__gj + 0][0]; ELEM __gacc0 = ELEM();
            const ELEM* BR __growj1 = &B[__gj + 1][0]; ELEM __gacc1 = ELEM();
            const ELEM* BR __growj2 = &B[__gj + 2][0]; ELEM __gacc2 = ELEM();
            const ELEM* BR __growj3 = &B[__gj + 3][0]; ELEM __gacc3 = ELEM();
            const ELEM* BR __growj4 = &B[__gj + 4][0]; ELEM __gacc4 = ELEM();
            const ELEM* BR __growj5 = &B[__gj + 5][0]; ELEM __gacc5 = ELEM();
            const ELEM* BR __growj6 = &B[__gj + 6][0]; ELEM __gacc6 = ELEM();
            const ELEM* BR __growj7 = &B[__gj + 7][0]; ELEM __gacc7 = ELEM();
            const ELEM* BR __growj8 = &B[__gj + 8][0]; ELEM __gacc8 = ELEM();
            const ELEM* BR __growj9 = &B[__gj + 9][0]; ELEM __gacc9 = ELEM();
            const ELEM* BR __growj10 = &B[__gj + 10][0]; ELEM __gacc10 = ELEM();
            const ELEM* BR __growj11 = &B[__gj + 11][0]; ELEM __gacc11 = ELEM();
            for (size_t __gk = 0; __gk < NN; __gk++) {
                __gacc0 += __growi[__gk] * conj_scalar(__growj0[__gk]);
                __gacc1 += __growi[__gk] * conj_scalar(__growj1[__gk]);
                __gacc2 += __growi[__gk] * conj_scalar(__growj2[__gk]);
                __gacc3 += __growi[__gk] * conj_scalar(__growj3[__gk]);
                __gacc4 += __growi[__gk] * conj_scalar(__growj4[__gk]);
                __gacc5 += __growi[__gk] * conj_scalar(__growj5[__gk]);
                __gacc6 += __growi[__gk] * conj_scalar(__growj6[__gk]);
                __gacc7 += __growi[__gk] * conj_scalar(__growj7[__gk]);
                __gacc8 += __growi[__gk] * conj_scalar(__growj8[__gk]);
                __gacc9 += __growi[__gk] * conj_scalar(__growj9[__gk]);
                __gacc10 += __growi[__gk] * conj_scalar(__growj10[__gk]);
                __gacc11 += __growi[__gk] * conj_scalar(__growj11[__gk]);
            }
            C[__gi][__gj + 0] = __gacc0;
            C[__gi][__gj + 1] = __gacc1;
            C[__gi][__gj + 2] = __gacc2;
            C[__gi][__gj + 3] = __gacc3;
            C[__gi][__gj + 4] = __gacc4;
            C[__gi][__gj + 5] = __gacc5;
            C[__gi][__gj + 6] = __gacc6;
            C[__gi][__gj + 7] = __gacc7;
            C[__gi][__gj + 8] = __gacc8;
            C[__gi][__gj + 9] = __gacc9;
            C[__gi][__gj + 10] = __gacc10;
            C[__gi][__gj + 11] = __gacc11;
        }
        for (; __gj < P; __gj++) {
            const ELEM* BR __growj = &B[__gj][0];
            ELEM __gacc = ELEM();
            for (size_t __gk = 0; __gk < NN; __gk++) __gacc += __growi[__gk] * conj_scalar(__growj[__gk]);
            C[__gi][__gj] = __gacc;
        }
    }
}

template <int P>
static void a_jam16() {
    for (size_t __gi = 0; __gi < MM; __gi++) {
        const ELEM* BR __growi = &A[__gi][0];
        size_t __gj = 0;
        for (; __gj + 16 <= P; __gj += 16) {
            const ELEM* BR __growj0 = &B[__gj + 0][0]; ELEM __gacc0 = ELEM();
            const ELEM* BR __growj1 = &B[__gj + 1][0]; ELEM __gacc1 = ELEM();
            const ELEM* BR __growj2 = &B[__gj + 2][0]; ELEM __gacc2 = ELEM();
            const ELEM* BR __growj3 = &B[__gj + 3][0]; ELEM __gacc3 = ELEM();
            const ELEM* BR __growj4 = &B[__gj + 4][0]; ELEM __gacc4 = ELEM();
            const ELEM* BR __growj5 = &B[__gj + 5][0]; ELEM __gacc5 = ELEM();
            const ELEM* BR __growj6 = &B[__gj + 6][0]; ELEM __gacc6 = ELEM();
            const ELEM* BR __growj7 = &B[__gj + 7][0]; ELEM __gacc7 = ELEM();
            const ELEM* BR __growj8 = &B[__gj + 8][0]; ELEM __gacc8 = ELEM();
            const ELEM* BR __growj9 = &B[__gj + 9][0]; ELEM __gacc9 = ELEM();
            const ELEM* BR __growj10 = &B[__gj + 10][0]; ELEM __gacc10 = ELEM();
            const ELEM* BR __growj11 = &B[__gj + 11][0]; ELEM __gacc11 = ELEM();
            const ELEM* BR __growj12 = &B[__gj + 12][0]; ELEM __gacc12 = ELEM();
            const ELEM* BR __growj13 = &B[__gj + 13][0]; ELEM __gacc13 = ELEM();
            const ELEM* BR __growj14 = &B[__gj + 14][0]; ELEM __gacc14 = ELEM();
            const ELEM* BR __growj15 = &B[__gj + 15][0]; ELEM __gacc15 = ELEM();
            for (size_t __gk = 0; __gk < NN; __gk++) {
                __gacc0 += __growi[__gk] * conj_scalar(__growj0[__gk]);
                __gacc1 += __growi[__gk] * conj_scalar(__growj1[__gk]);
                __gacc2 += __growi[__gk] * conj_scalar(__growj2[__gk]);
                __gacc3 += __growi[__gk] * conj_scalar(__growj3[__gk]);
                __gacc4 += __growi[__gk] * conj_scalar(__growj4[__gk]);
                __gacc5 += __growi[__gk] * conj_scalar(__growj5[__gk]);
                __gacc6 += __growi[__gk] * conj_scalar(__growj6[__gk]);
                __gacc7 += __growi[__gk] * conj_scalar(__growj7[__gk]);
                __gacc8 += __growi[__gk] * conj_scalar(__growj8[__gk]);
                __gacc9 += __growi[__gk] * conj_scalar(__growj9[__gk]);
                __gacc10 += __growi[__gk] * conj_scalar(__growj10[__gk]);
                __gacc11 += __growi[__gk] * conj_scalar(__growj11[__gk]);
                __gacc12 += __growi[__gk] * conj_scalar(__growj12[__gk]);
                __gacc13 += __growi[__gk] * conj_scalar(__growj13[__gk]);
                __gacc14 += __growi[__gk] * conj_scalar(__growj14[__gk]);
                __gacc15 += __growi[__gk] * conj_scalar(__growj15[__gk]);
            }
            C[__gi][__gj + 0] = __gacc0;
            C[__gi][__gj + 1] = __gacc1;
            C[__gi][__gj + 2] = __gacc2;
            C[__gi][__gj + 3] = __gacc3;
            C[__gi][__gj + 4] = __gacc4;
            C[__gi][__gj + 5] = __gacc5;
            C[__gi][__gj + 6] = __gacc6;
            C[__gi][__gj + 7] = __gacc7;
            C[__gi][__gj + 8] = __gacc8;
            C[__gi][__gj + 9] = __gacc9;
            C[__gi][__gj + 10] = __gacc10;
            C[__gi][__gj + 11] = __gacc11;
            C[__gi][__gj + 12] = __gacc12;
            C[__gi][__gj + 13] = __gacc13;
            C[__gi][__gj + 14] = __gacc14;
            C[__gi][__gj + 15] = __gacc15;
        }
        for (; __gj < P; __gj++) {
            const ELEM* BR __growj = &B[__gj][0];
            ELEM __gacc = ELEM();
            for (size_t __gk = 0; __gk < NN; __gk++) __gacc += __growi[__gk] * conj_scalar(__growj[__gk]);
            C[__gi][__gj] = __gacc;
        }
    }
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
static int ROUNDS = 5, REPS = 3;
static double HZ;
static ELEM* refbuf; static size_t cbytes;
static double timeit(void (*fn)(), int* bitok, bool isref) {
    memset(Cpool, 0xA5, cbytes);
    fn(); asm volatile("" ::: "memory");
    if (isref) { memcpy(refbuf, Cpool, cbytes); *bitok = 1; }
    else *bitok = (memcmp(refbuf, Cpool, cbytes) == 0);
    std::vector<double> t;
    for (int r = 0; r < ROUNDS; r++) for (int q = 0; q < REPS; q++) {
        auto t0 = clk::now(); fn(); asm volatile("" ::: "memory"); auto t1 = clk::now();
        t.push_back(secs(t0, t1));
    }
    std::sort(t.begin(), t.end());
    return t[t.size() / 2];
}
template <int P> void sweep_p();

template <> void sweep_p<3>() {
    const int P = 3;
    double macs = (double)MM * NN * P;
    int b;
    double base = timeit(a_base<P>, &b, true);
    printf("p=%-3d %-7s %10.6f %8.4f %8.4f %6s\n", P, "base", base, base * HZ / macs, 1.0, "yes");
    struct { const char* nm; void (*fn)(); int r; } arms[] = {
        {"jam2", a_jam2<3>, 2},
        {"jam3", a_jam3<3>, 3},
        {"jam4", a_jam4<3>, 4},
        {"jam5", a_jam5<3>, 5},
        {"jam6", a_jam6<3>, 6},
        {"jam7", a_jam7<3>, 7},
        {"jam8", a_jam8<3>, 8},
        {"jam9", a_jam9<3>, 9},
        {"jam10", a_jam10<3>, 10},
        {"jam12", a_jam12<3>, 12},
        {"jam16", a_jam16<3>, 16}
    };
    double best = 0; const char* bestnm = "base"; double at5 = 0;
    for (size_t z = 0; z < sizeof(arms)/sizeof(arms[0]); z++) {
        double m = timeit(arms[z].fn, &b, false);
        double sp = base / m;
        if (sp > best) { best = sp; bestnm = arms[z].nm; }
        if (arms[z].r == 5) at5 = sp;
        printf("p=%-3d %-7s %10.6f %8.4f %8.4f %6s%s\n", P, arms[z].nm, m, m * HZ / macs, sp,
               b ? "yes" : "NO", (P % arms[z].r == 0) ? "   <- R|p" : "");
    }
    printf("p=%-3d BEST=%s %.3fx   fixed R=5 gets %.3fx  (%.0f%% of best)\n\n",
           P, bestnm, best, at5, at5 > 0 ? 100.0 * at5 / best : 0.0);
    fflush(stdout);
}

template <> void sweep_p<5>() {
    const int P = 5;
    double macs = (double)MM * NN * P;
    int b;
    double base = timeit(a_base<P>, &b, true);
    printf("p=%-3d %-7s %10.6f %8.4f %8.4f %6s\n", P, "base", base, base * HZ / macs, 1.0, "yes");
    struct { const char* nm; void (*fn)(); int r; } arms[] = {
        {"jam2", a_jam2<5>, 2},
        {"jam3", a_jam3<5>, 3},
        {"jam4", a_jam4<5>, 4},
        {"jam5", a_jam5<5>, 5},
        {"jam6", a_jam6<5>, 6},
        {"jam7", a_jam7<5>, 7},
        {"jam8", a_jam8<5>, 8},
        {"jam9", a_jam9<5>, 9},
        {"jam10", a_jam10<5>, 10},
        {"jam12", a_jam12<5>, 12},
        {"jam16", a_jam16<5>, 16}
    };
    double best = 0; const char* bestnm = "base"; double at5 = 0;
    for (size_t z = 0; z < sizeof(arms)/sizeof(arms[0]); z++) {
        double m = timeit(arms[z].fn, &b, false);
        double sp = base / m;
        if (sp > best) { best = sp; bestnm = arms[z].nm; }
        if (arms[z].r == 5) at5 = sp;
        printf("p=%-3d %-7s %10.6f %8.4f %8.4f %6s%s\n", P, arms[z].nm, m, m * HZ / macs, sp,
               b ? "yes" : "NO", (P % arms[z].r == 0) ? "   <- R|p" : "");
    }
    printf("p=%-3d BEST=%s %.3fx   fixed R=5 gets %.3fx  (%.0f%% of best)\n\n",
           P, bestnm, best, at5, at5 > 0 ? 100.0 * at5 / best : 0.0);
    fflush(stdout);
}

template <> void sweep_p<6>() {
    const int P = 6;
    double macs = (double)MM * NN * P;
    int b;
    double base = timeit(a_base<P>, &b, true);
    printf("p=%-3d %-7s %10.6f %8.4f %8.4f %6s\n", P, "base", base, base * HZ / macs, 1.0, "yes");
    struct { const char* nm; void (*fn)(); int r; } arms[] = {
        {"jam2", a_jam2<6>, 2},
        {"jam3", a_jam3<6>, 3},
        {"jam4", a_jam4<6>, 4},
        {"jam5", a_jam5<6>, 5},
        {"jam6", a_jam6<6>, 6},
        {"jam7", a_jam7<6>, 7},
        {"jam8", a_jam8<6>, 8},
        {"jam9", a_jam9<6>, 9},
        {"jam10", a_jam10<6>, 10},
        {"jam12", a_jam12<6>, 12},
        {"jam16", a_jam16<6>, 16}
    };
    double best = 0; const char* bestnm = "base"; double at5 = 0;
    for (size_t z = 0; z < sizeof(arms)/sizeof(arms[0]); z++) {
        double m = timeit(arms[z].fn, &b, false);
        double sp = base / m;
        if (sp > best) { best = sp; bestnm = arms[z].nm; }
        if (arms[z].r == 5) at5 = sp;
        printf("p=%-3d %-7s %10.6f %8.4f %8.4f %6s%s\n", P, arms[z].nm, m, m * HZ / macs, sp,
               b ? "yes" : "NO", (P % arms[z].r == 0) ? "   <- R|p" : "");
    }
    printf("p=%-3d BEST=%s %.3fx   fixed R=5 gets %.3fx  (%.0f%% of best)\n\n",
           P, bestnm, best, at5, at5 > 0 ? 100.0 * at5 / best : 0.0);
    fflush(stdout);
}

template <> void sweep_p<7>() {
    const int P = 7;
    double macs = (double)MM * NN * P;
    int b;
    double base = timeit(a_base<P>, &b, true);
    printf("p=%-3d %-7s %10.6f %8.4f %8.4f %6s\n", P, "base", base, base * HZ / macs, 1.0, "yes");
    struct { const char* nm; void (*fn)(); int r; } arms[] = {
        {"jam2", a_jam2<7>, 2},
        {"jam3", a_jam3<7>, 3},
        {"jam4", a_jam4<7>, 4},
        {"jam5", a_jam5<7>, 5},
        {"jam6", a_jam6<7>, 6},
        {"jam7", a_jam7<7>, 7},
        {"jam8", a_jam8<7>, 8},
        {"jam9", a_jam9<7>, 9},
        {"jam10", a_jam10<7>, 10},
        {"jam12", a_jam12<7>, 12},
        {"jam16", a_jam16<7>, 16}
    };
    double best = 0; const char* bestnm = "base"; double at5 = 0;
    for (size_t z = 0; z < sizeof(arms)/sizeof(arms[0]); z++) {
        double m = timeit(arms[z].fn, &b, false);
        double sp = base / m;
        if (sp > best) { best = sp; bestnm = arms[z].nm; }
        if (arms[z].r == 5) at5 = sp;
        printf("p=%-3d %-7s %10.6f %8.4f %8.4f %6s%s\n", P, arms[z].nm, m, m * HZ / macs, sp,
               b ? "yes" : "NO", (P % arms[z].r == 0) ? "   <- R|p" : "");
    }
    printf("p=%-3d BEST=%s %.3fx   fixed R=5 gets %.3fx  (%.0f%% of best)\n\n",
           P, bestnm, best, at5, at5 > 0 ? 100.0 * at5 / best : 0.0);
    fflush(stdout);
}

template <> void sweep_p<8>() {
    const int P = 8;
    double macs = (double)MM * NN * P;
    int b;
    double base = timeit(a_base<P>, &b, true);
    printf("p=%-3d %-7s %10.6f %8.4f %8.4f %6s\n", P, "base", base, base * HZ / macs, 1.0, "yes");
    struct { const char* nm; void (*fn)(); int r; } arms[] = {
        {"jam2", a_jam2<8>, 2},
        {"jam3", a_jam3<8>, 3},
        {"jam4", a_jam4<8>, 4},
        {"jam5", a_jam5<8>, 5},
        {"jam6", a_jam6<8>, 6},
        {"jam7", a_jam7<8>, 7},
        {"jam8", a_jam8<8>, 8},
        {"jam9", a_jam9<8>, 9},
        {"jam10", a_jam10<8>, 10},
        {"jam12", a_jam12<8>, 12},
        {"jam16", a_jam16<8>, 16}
    };
    double best = 0; const char* bestnm = "base"; double at5 = 0;
    for (size_t z = 0; z < sizeof(arms)/sizeof(arms[0]); z++) {
        double m = timeit(arms[z].fn, &b, false);
        double sp = base / m;
        if (sp > best) { best = sp; bestnm = arms[z].nm; }
        if (arms[z].r == 5) at5 = sp;
        printf("p=%-3d %-7s %10.6f %8.4f %8.4f %6s%s\n", P, arms[z].nm, m, m * HZ / macs, sp,
               b ? "yes" : "NO", (P % arms[z].r == 0) ? "   <- R|p" : "");
    }
    printf("p=%-3d BEST=%s %.3fx   fixed R=5 gets %.3fx  (%.0f%% of best)\n\n",
           P, bestnm, best, at5, at5 > 0 ? 100.0 * at5 / best : 0.0);
    fflush(stdout);
}

template <> void sweep_p<9>() {
    const int P = 9;
    double macs = (double)MM * NN * P;
    int b;
    double base = timeit(a_base<P>, &b, true);
    printf("p=%-3d %-7s %10.6f %8.4f %8.4f %6s\n", P, "base", base, base * HZ / macs, 1.0, "yes");
    struct { const char* nm; void (*fn)(); int r; } arms[] = {
        {"jam2", a_jam2<9>, 2},
        {"jam3", a_jam3<9>, 3},
        {"jam4", a_jam4<9>, 4},
        {"jam5", a_jam5<9>, 5},
        {"jam6", a_jam6<9>, 6},
        {"jam7", a_jam7<9>, 7},
        {"jam8", a_jam8<9>, 8},
        {"jam9", a_jam9<9>, 9},
        {"jam10", a_jam10<9>, 10},
        {"jam12", a_jam12<9>, 12},
        {"jam16", a_jam16<9>, 16}
    };
    double best = 0; const char* bestnm = "base"; double at5 = 0;
    for (size_t z = 0; z < sizeof(arms)/sizeof(arms[0]); z++) {
        double m = timeit(arms[z].fn, &b, false);
        double sp = base / m;
        if (sp > best) { best = sp; bestnm = arms[z].nm; }
        if (arms[z].r == 5) at5 = sp;
        printf("p=%-3d %-7s %10.6f %8.4f %8.4f %6s%s\n", P, arms[z].nm, m, m * HZ / macs, sp,
               b ? "yes" : "NO", (P % arms[z].r == 0) ? "   <- R|p" : "");
    }
    printf("p=%-3d BEST=%s %.3fx   fixed R=5 gets %.3fx  (%.0f%% of best)\n\n",
           P, bestnm, best, at5, at5 > 0 ? 100.0 * at5 / best : 0.0);
    fflush(stdout);
}

template <> void sweep_p<10>() {
    const int P = 10;
    double macs = (double)MM * NN * P;
    int b;
    double base = timeit(a_base<P>, &b, true);
    printf("p=%-3d %-7s %10.6f %8.4f %8.4f %6s\n", P, "base", base, base * HZ / macs, 1.0, "yes");
    struct { const char* nm; void (*fn)(); int r; } arms[] = {
        {"jam2", a_jam2<10>, 2},
        {"jam3", a_jam3<10>, 3},
        {"jam4", a_jam4<10>, 4},
        {"jam5", a_jam5<10>, 5},
        {"jam6", a_jam6<10>, 6},
        {"jam7", a_jam7<10>, 7},
        {"jam8", a_jam8<10>, 8},
        {"jam9", a_jam9<10>, 9},
        {"jam10", a_jam10<10>, 10},
        {"jam12", a_jam12<10>, 12},
        {"jam16", a_jam16<10>, 16}
    };
    double best = 0; const char* bestnm = "base"; double at5 = 0;
    for (size_t z = 0; z < sizeof(arms)/sizeof(arms[0]); z++) {
        double m = timeit(arms[z].fn, &b, false);
        double sp = base / m;
        if (sp > best) { best = sp; bestnm = arms[z].nm; }
        if (arms[z].r == 5) at5 = sp;
        printf("p=%-3d %-7s %10.6f %8.4f %8.4f %6s%s\n", P, arms[z].nm, m, m * HZ / macs, sp,
               b ? "yes" : "NO", (P % arms[z].r == 0) ? "   <- R|p" : "");
    }
    printf("p=%-3d BEST=%s %.3fx   fixed R=5 gets %.3fx  (%.0f%% of best)\n\n",
           P, bestnm, best, at5, at5 > 0 ? 100.0 * at5 / best : 0.0);
    fflush(stdout);
}

template <> void sweep_p<11>() {
    const int P = 11;
    double macs = (double)MM * NN * P;
    int b;
    double base = timeit(a_base<P>, &b, true);
    printf("p=%-3d %-7s %10.6f %8.4f %8.4f %6s\n", P, "base", base, base * HZ / macs, 1.0, "yes");
    struct { const char* nm; void (*fn)(); int r; } arms[] = {
        {"jam2", a_jam2<11>, 2},
        {"jam3", a_jam3<11>, 3},
        {"jam4", a_jam4<11>, 4},
        {"jam5", a_jam5<11>, 5},
        {"jam6", a_jam6<11>, 6},
        {"jam7", a_jam7<11>, 7},
        {"jam8", a_jam8<11>, 8},
        {"jam9", a_jam9<11>, 9},
        {"jam10", a_jam10<11>, 10},
        {"jam12", a_jam12<11>, 12},
        {"jam16", a_jam16<11>, 16}
    };
    double best = 0; const char* bestnm = "base"; double at5 = 0;
    for (size_t z = 0; z < sizeof(arms)/sizeof(arms[0]); z++) {
        double m = timeit(arms[z].fn, &b, false);
        double sp = base / m;
        if (sp > best) { best = sp; bestnm = arms[z].nm; }
        if (arms[z].r == 5) at5 = sp;
        printf("p=%-3d %-7s %10.6f %8.4f %8.4f %6s%s\n", P, arms[z].nm, m, m * HZ / macs, sp,
               b ? "yes" : "NO", (P % arms[z].r == 0) ? "   <- R|p" : "");
    }
    printf("p=%-3d BEST=%s %.3fx   fixed R=5 gets %.3fx  (%.0f%% of best)\n\n",
           P, bestnm, best, at5, at5 > 0 ? 100.0 * at5 / best : 0.0);
    fflush(stdout);
}

template <> void sweep_p<12>() {
    const int P = 12;
    double macs = (double)MM * NN * P;
    int b;
    double base = timeit(a_base<P>, &b, true);
    printf("p=%-3d %-7s %10.6f %8.4f %8.4f %6s\n", P, "base", base, base * HZ / macs, 1.0, "yes");
    struct { const char* nm; void (*fn)(); int r; } arms[] = {
        {"jam2", a_jam2<12>, 2},
        {"jam3", a_jam3<12>, 3},
        {"jam4", a_jam4<12>, 4},
        {"jam5", a_jam5<12>, 5},
        {"jam6", a_jam6<12>, 6},
        {"jam7", a_jam7<12>, 7},
        {"jam8", a_jam8<12>, 8},
        {"jam9", a_jam9<12>, 9},
        {"jam10", a_jam10<12>, 10},
        {"jam12", a_jam12<12>, 12},
        {"jam16", a_jam16<12>, 16}
    };
    double best = 0; const char* bestnm = "base"; double at5 = 0;
    for (size_t z = 0; z < sizeof(arms)/sizeof(arms[0]); z++) {
        double m = timeit(arms[z].fn, &b, false);
        double sp = base / m;
        if (sp > best) { best = sp; bestnm = arms[z].nm; }
        if (arms[z].r == 5) at5 = sp;
        printf("p=%-3d %-7s %10.6f %8.4f %8.4f %6s%s\n", P, arms[z].nm, m, m * HZ / macs, sp,
               b ? "yes" : "NO", (P % arms[z].r == 0) ? "   <- R|p" : "");
    }
    printf("p=%-3d BEST=%s %.3fx   fixed R=5 gets %.3fx  (%.0f%% of best)\n\n",
           P, bestnm, best, at5, at5 > 0 ? 100.0 * at5 / best : 0.0);
    fflush(stdout);
}

template <> void sweep_p<13>() {
    const int P = 13;
    double macs = (double)MM * NN * P;
    int b;
    double base = timeit(a_base<P>, &b, true);
    printf("p=%-3d %-7s %10.6f %8.4f %8.4f %6s\n", P, "base", base, base * HZ / macs, 1.0, "yes");
    struct { const char* nm; void (*fn)(); int r; } arms[] = {
        {"jam2", a_jam2<13>, 2},
        {"jam3", a_jam3<13>, 3},
        {"jam4", a_jam4<13>, 4},
        {"jam5", a_jam5<13>, 5},
        {"jam6", a_jam6<13>, 6},
        {"jam7", a_jam7<13>, 7},
        {"jam8", a_jam8<13>, 8},
        {"jam9", a_jam9<13>, 9},
        {"jam10", a_jam10<13>, 10},
        {"jam12", a_jam12<13>, 12},
        {"jam16", a_jam16<13>, 16}
    };
    double best = 0; const char* bestnm = "base"; double at5 = 0;
    for (size_t z = 0; z < sizeof(arms)/sizeof(arms[0]); z++) {
        double m = timeit(arms[z].fn, &b, false);
        double sp = base / m;
        if (sp > best) { best = sp; bestnm = arms[z].nm; }
        if (arms[z].r == 5) at5 = sp;
        printf("p=%-3d %-7s %10.6f %8.4f %8.4f %6s%s\n", P, arms[z].nm, m, m * HZ / macs, sp,
               b ? "yes" : "NO", (P % arms[z].r == 0) ? "   <- R|p" : "");
    }
    printf("p=%-3d BEST=%s %.3fx   fixed R=5 gets %.3fx  (%.0f%% of best)\n\n",
           P, bestnm, best, at5, at5 > 0 ? 100.0 * at5 / best : 0.0);
    fflush(stdout);
}

template <> void sweep_p<15>() {
    const int P = 15;
    double macs = (double)MM * NN * P;
    int b;
    double base = timeit(a_base<P>, &b, true);
    printf("p=%-3d %-7s %10.6f %8.4f %8.4f %6s\n", P, "base", base, base * HZ / macs, 1.0, "yes");
    struct { const char* nm; void (*fn)(); int r; } arms[] = {
        {"jam2", a_jam2<15>, 2},
        {"jam3", a_jam3<15>, 3},
        {"jam4", a_jam4<15>, 4},
        {"jam5", a_jam5<15>, 5},
        {"jam6", a_jam6<15>, 6},
        {"jam7", a_jam7<15>, 7},
        {"jam8", a_jam8<15>, 8},
        {"jam9", a_jam9<15>, 9},
        {"jam10", a_jam10<15>, 10},
        {"jam12", a_jam12<15>, 12},
        {"jam16", a_jam16<15>, 16}
    };
    double best = 0; const char* bestnm = "base"; double at5 = 0;
    for (size_t z = 0; z < sizeof(arms)/sizeof(arms[0]); z++) {
        double m = timeit(arms[z].fn, &b, false);
        double sp = base / m;
        if (sp > best) { best = sp; bestnm = arms[z].nm; }
        if (arms[z].r == 5) at5 = sp;
        printf("p=%-3d %-7s %10.6f %8.4f %8.4f %6s%s\n", P, arms[z].nm, m, m * HZ / macs, sp,
               b ? "yes" : "NO", (P % arms[z].r == 0) ? "   <- R|p" : "");
    }
    printf("p=%-3d BEST=%s %.3fx   fixed R=5 gets %.3fx  (%.0f%% of best)\n\n",
           P, bestnm, best, at5, at5 > 0 ? 100.0 * at5 / best : 0.0);
    fflush(stdout);
}

template <> void sweep_p<16>() {
    const int P = 16;
    double macs = (double)MM * NN * P;
    int b;
    double base = timeit(a_base<P>, &b, true);
    printf("p=%-3d %-7s %10.6f %8.4f %8.4f %6s\n", P, "base", base, base * HZ / macs, 1.0, "yes");
    struct { const char* nm; void (*fn)(); int r; } arms[] = {
        {"jam2", a_jam2<16>, 2},
        {"jam3", a_jam3<16>, 3},
        {"jam4", a_jam4<16>, 4},
        {"jam5", a_jam5<16>, 5},
        {"jam6", a_jam6<16>, 6},
        {"jam7", a_jam7<16>, 7},
        {"jam8", a_jam8<16>, 8},
        {"jam9", a_jam9<16>, 9},
        {"jam10", a_jam10<16>, 10},
        {"jam12", a_jam12<16>, 12},
        {"jam16", a_jam16<16>, 16}
    };
    double best = 0; const char* bestnm = "base"; double at5 = 0;
    for (size_t z = 0; z < sizeof(arms)/sizeof(arms[0]); z++) {
        double m = timeit(arms[z].fn, &b, false);
        double sp = base / m;
        if (sp > best) { best = sp; bestnm = arms[z].nm; }
        if (arms[z].r == 5) at5 = sp;
        printf("p=%-3d %-7s %10.6f %8.4f %8.4f %6s%s\n", P, arms[z].nm, m, m * HZ / macs, sp,
               b ? "yes" : "NO", (P % arms[z].r == 0) ? "   <- R|p" : "");
    }
    printf("p=%-3d BEST=%s %.3fx   fixed R=5 gets %.3fx  (%.0f%% of best)\n\n",
           P, bestnm, best, at5, at5 > 0 ? 100.0 * at5 / best : 0.0);
    fflush(stdout);
}

template <> void sweep_p<18>() {
    const int P = 18;
    double macs = (double)MM * NN * P;
    int b;
    double base = timeit(a_base<P>, &b, true);
    printf("p=%-3d %-7s %10.6f %8.4f %8.4f %6s\n", P, "base", base, base * HZ / macs, 1.0, "yes");
    struct { const char* nm; void (*fn)(); int r; } arms[] = {
        {"jam2", a_jam2<18>, 2},
        {"jam3", a_jam3<18>, 3},
        {"jam4", a_jam4<18>, 4},
        {"jam5", a_jam5<18>, 5},
        {"jam6", a_jam6<18>, 6},
        {"jam7", a_jam7<18>, 7},
        {"jam8", a_jam8<18>, 8},
        {"jam9", a_jam9<18>, 9},
        {"jam10", a_jam10<18>, 10},
        {"jam12", a_jam12<18>, 12},
        {"jam16", a_jam16<18>, 16}
    };
    double best = 0; const char* bestnm = "base"; double at5 = 0;
    for (size_t z = 0; z < sizeof(arms)/sizeof(arms[0]); z++) {
        double m = timeit(arms[z].fn, &b, false);
        double sp = base / m;
        if (sp > best) { best = sp; bestnm = arms[z].nm; }
        if (arms[z].r == 5) at5 = sp;
        printf("p=%-3d %-7s %10.6f %8.4f %8.4f %6s%s\n", P, arms[z].nm, m, m * HZ / macs, sp,
               b ? "yes" : "NO", (P % arms[z].r == 0) ? "   <- R|p" : "");
    }
    printf("p=%-3d BEST=%s %.3fx   fixed R=5 gets %.3fx  (%.0f%% of best)\n\n",
           P, bestnm, best, at5, at5 > 0 ? 100.0 * at5 / best : 0.0);
    fflush(stdout);
}

template <> void sweep_p<20>() {
    const int P = 20;
    double macs = (double)MM * NN * P;
    int b;
    double base = timeit(a_base<P>, &b, true);
    printf("p=%-3d %-7s %10.6f %8.4f %8.4f %6s\n", P, "base", base, base * HZ / macs, 1.0, "yes");
    struct { const char* nm; void (*fn)(); int r; } arms[] = {
        {"jam2", a_jam2<20>, 2},
        {"jam3", a_jam3<20>, 3},
        {"jam4", a_jam4<20>, 4},
        {"jam5", a_jam5<20>, 5},
        {"jam6", a_jam6<20>, 6},
        {"jam7", a_jam7<20>, 7},
        {"jam8", a_jam8<20>, 8},
        {"jam9", a_jam9<20>, 9},
        {"jam10", a_jam10<20>, 10},
        {"jam12", a_jam12<20>, 12},
        {"jam16", a_jam16<20>, 16}
    };
    double best = 0; const char* bestnm = "base"; double at5 = 0;
    for (size_t z = 0; z < sizeof(arms)/sizeof(arms[0]); z++) {
        double m = timeit(arms[z].fn, &b, false);
        double sp = base / m;
        if (sp > best) { best = sp; bestnm = arms[z].nm; }
        if (arms[z].r == 5) at5 = sp;
        printf("p=%-3d %-7s %10.6f %8.4f %8.4f %6s%s\n", P, arms[z].nm, m, m * HZ / macs, sp,
               b ? "yes" : "NO", (P % arms[z].r == 0) ? "   <- R|p" : "");
    }
    printf("p=%-3d BEST=%s %.3fx   fixed R=5 gets %.3fx  (%.0f%% of best)\n\n",
           P, bestnm, best, at5, at5 > 0 ? 100.0 * at5 / best : 0.0);
    fflush(stdout);
}

template <> void sweep_p<24>() {
    const int P = 24;
    double macs = (double)MM * NN * P;
    int b;
    double base = timeit(a_base<P>, &b, true);
    printf("p=%-3d %-7s %10.6f %8.4f %8.4f %6s\n", P, "base", base, base * HZ / macs, 1.0, "yes");
    struct { const char* nm; void (*fn)(); int r; } arms[] = {
        {"jam2", a_jam2<24>, 2},
        {"jam3", a_jam3<24>, 3},
        {"jam4", a_jam4<24>, 4},
        {"jam5", a_jam5<24>, 5},
        {"jam6", a_jam6<24>, 6},
        {"jam7", a_jam7<24>, 7},
        {"jam8", a_jam8<24>, 8},
        {"jam9", a_jam9<24>, 9},
        {"jam10", a_jam10<24>, 10},
        {"jam12", a_jam12<24>, 12},
        {"jam16", a_jam16<24>, 16}
    };
    double best = 0; const char* bestnm = "base"; double at5 = 0;
    for (size_t z = 0; z < sizeof(arms)/sizeof(arms[0]); z++) {
        double m = timeit(arms[z].fn, &b, false);
        double sp = base / m;
        if (sp > best) { best = sp; bestnm = arms[z].nm; }
        if (arms[z].r == 5) at5 = sp;
        printf("p=%-3d %-7s %10.6f %8.4f %8.4f %6s%s\n", P, arms[z].nm, m, m * HZ / macs, sp,
               b ? "yes" : "NO", (P % arms[z].r == 0) ? "   <- R|p" : "");
    }
    printf("p=%-3d BEST=%s %.3fx   fixed R=5 gets %.3fx  (%.0f%% of best)\n\n",
           P, bestnm, best, at5, at5 > 0 ? 100.0 * at5 / best : 0.0);
    fflush(stdout);
}

template <> void sweep_p<32>() {
    const int P = 32;
    double macs = (double)MM * NN * P;
    int b;
    double base = timeit(a_base<P>, &b, true);
    printf("p=%-3d %-7s %10.6f %8.4f %8.4f %6s\n", P, "base", base, base * HZ / macs, 1.0, "yes");
    struct { const char* nm; void (*fn)(); int r; } arms[] = {
        {"jam2", a_jam2<32>, 2},
        {"jam3", a_jam3<32>, 3},
        {"jam4", a_jam4<32>, 4},
        {"jam5", a_jam5<32>, 5},
        {"jam6", a_jam6<32>, 6},
        {"jam7", a_jam7<32>, 7},
        {"jam8", a_jam8<32>, 8},
        {"jam9", a_jam9<32>, 9},
        {"jam10", a_jam10<32>, 10},
        {"jam12", a_jam12<32>, 12},
        {"jam16", a_jam16<32>, 16}
    };
    double best = 0; const char* bestnm = "base"; double at5 = 0;
    for (size_t z = 0; z < sizeof(arms)/sizeof(arms[0]); z++) {
        double m = timeit(arms[z].fn, &b, false);
        double sp = base / m;
        if (sp > best) { best = sp; bestnm = arms[z].nm; }
        if (arms[z].r == 5) at5 = sp;
        printf("p=%-3d %-7s %10.6f %8.4f %8.4f %6s%s\n", P, arms[z].nm, m, m * HZ / macs, sp,
               b ? "yes" : "NO", (P % arms[z].r == 0) ? "   <- R|p" : "");
    }
    printf("p=%-3d BEST=%s %.3fx   fixed R=5 gets %.3fx  (%.0f%% of best)\n\n",
           P, bestnm, best, at5, at5 > 0 ? 100.0 * at5 / best : 0.0);
    fflush(stdout);
}

template <> void sweep_p<40>() {
    const int P = 40;
    double macs = (double)MM * NN * P;
    int b;
    double base = timeit(a_base<P>, &b, true);
    printf("p=%-3d %-7s %10.6f %8.4f %8.4f %6s\n", P, "base", base, base * HZ / macs, 1.0, "yes");
    struct { const char* nm; void (*fn)(); int r; } arms[] = {
        {"jam2", a_jam2<40>, 2},
        {"jam3", a_jam3<40>, 3},
        {"jam4", a_jam4<40>, 4},
        {"jam5", a_jam5<40>, 5},
        {"jam6", a_jam6<40>, 6},
        {"jam7", a_jam7<40>, 7},
        {"jam8", a_jam8<40>, 8},
        {"jam9", a_jam9<40>, 9},
        {"jam10", a_jam10<40>, 10},
        {"jam12", a_jam12<40>, 12},
        {"jam16", a_jam16<40>, 16}
    };
    double best = 0; const char* bestnm = "base"; double at5 = 0;
    for (size_t z = 0; z < sizeof(arms)/sizeof(arms[0]); z++) {
        double m = timeit(arms[z].fn, &b, false);
        double sp = base / m;
        if (sp > best) { best = sp; bestnm = arms[z].nm; }
        if (arms[z].r == 5) at5 = sp;
        printf("p=%-3d %-7s %10.6f %8.4f %8.4f %6s%s\n", P, arms[z].nm, m, m * HZ / macs, sp,
               b ? "yes" : "NO", (P % arms[z].r == 0) ? "   <- R|p" : "");
    }
    printf("p=%-3d BEST=%s %.3fx   fixed R=5 gets %.3fx  (%.0f%% of best)\n\n",
           P, bestnm, best, at5, at5 > 0 ? 100.0 * at5 / best : 0.0);
    fflush(stdout);
}

int main(int argc, char** argv) {
    if (argc > 1) ROUNDS = atoi(argv[1]);
    if (argc > 2) REPS = atoi(argv[2]);
    setup(); HZ = calib_clock();
    cbytes = sizeof(ELEM) * (size_t)MM * PMAX;
    refbuf = (ELEM*)malloc(cbytes);
    printf("# m=%d n=%d  clock=%.3f GHz\n", MM, NN, HZ / 1e9);
    printf("%-6s %-7s %10s %8s %8s %6s\n", "p", "kernel", "median_s", "cyc/MAC", "vs_base", "bitwise");
    sweep_p<3>();
    sweep_p<5>();
    sweep_p<6>();
    sweep_p<7>();
    sweep_p<8>();
    sweep_p<9>();
    sweep_p<10>();
    sweep_p<11>();
    sweep_p<12>();
    sweep_p<13>();
    sweep_p<15>();
    sweep_p<16>();
    sweep_p<18>();
    sweep_p<20>();
    sweep_p<24>();
    sweep_p<32>();
    sweep_p<40>();
    return 0;
}
