// syrk_tile_tests.cpp -- RAW-BIT tests of the packed-triangle SYRK tile
// (blade_tiles.h core + blade_pgemm::dsyrk_tri driver) against the serial
// per-cell prodsum it replaces, compiled in the SAME translation unit and the
// SAME -ffp-contract mode (the runner, tests/SyrkTileTests.fs, builds this file
// twice: -ffp-contract=off -- bitwise by construction -- and =fast, the user
// default, where both sides must contract alike).
//
// The reference is the emitted serial form verbatim:
//     double __ps = 0; for (t ascending) __ps += X[i][t] * X[j][t];
// Every packed cell is compared by memcmp (bits, not values), the pool's guard
// cells before/after and the slack never touched, for:
//   p = 0..30 and every p mod 24 up to 72, plus 97, 101, 193 and an NC edge
//     (2049, 2057: a second column block);
//   k = 0..17, KC-1 / KC / KC+1 around both the default cap and small caps that
//     force several K blocks (the stored-partial reload path);
//   S = 0 (inclusive SymIdx) and S = 1 (strict);
//   serial and threaded (every team size gives the same bits);
//   data: random signed, heavy cancellation, signed zeros, subnormals, Inf/NaN.
// Output: one `[FAIL]: <name>` line per failure, then
//   SYRK TILE TESTS: <passed>/<total> passed
#include "blade_packed_gemm.hpp"
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <cmath>
#include <vector>
#include <string>
#include <limits>

namespace {

int g_total = 0, g_passed = 0;

void check(bool ok, const std::string& name) {
    g_total++;
    if (ok) g_passed++;
    else std::printf("[FAIL]: %s\n", name.c_str());
}

// Deterministic generator (splitmix64).
struct Rng {
    unsigned long long s;
    explicit Rng(unsigned long long seed) : s(seed) {}
    unsigned long long next() {
        unsigned long long z = (s += 0x9E3779B97F4A7C15ULL);
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9ULL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBULL;
        return z ^ (z >> 31);
    }
    double unit() { return (double)(next() >> 11) * (1.0 / 9007199254740992.0); }
};

enum Data { Random, Cancel, Zeros, Subnormal, NonFinite, Positive };

double gen(Rng& r, Data d) {
    switch (d) {
    case Random: return r.unit() * 2.0 - 1.0;
    case Positive: return r.unit();
    case Cancel: {
        // large terms of both signs plus small ones: the order of the chain
        // decides the low bits.
        const double big = (r.next() & 1) ? 1e8 : -1e8;
        return (r.next() % 3 == 0) ? big * (1.0 + r.unit()) : r.unit() * 1e-3;
    }
    case Zeros: {
        const unsigned long long u = r.next() % 4;
        return u == 0 ? 0.0 : u == 1 ? -0.0 : (r.unit() - 0.5) * 1e-300;
    }
    case Subnormal: return (r.unit() - 0.5) * 1e-160;   // products land subnormal
    case NonFinite: {
        const unsigned long long u = r.next() % 50;
        if (u == 0) return std::numeric_limits<double>::infinity();
        if (u == 1) return -std::numeric_limits<double>::infinity();
        return r.unit() * 2.0 - 1.0;
    }
    }
    return 0.0;
}

const char* dataName(Data d) {
    switch (d) {
    case Random: return "random"; case Cancel: return "cancel"; case Zeros: return "zeros";
    case Subnormal: return "subnormal"; case NonFinite: return "nonfinite"; case Positive: return "positive";
    }
    return "?";
}

// The serial references.
//
// -ffp-contract=off (the differential gates' mode): Blade's emitted repro
// prodsum, verbatim -- every step is round(round(a*b) + acc) -- and the tile
// must equal it bit for bit.
//
// -ffp-contract=fast (the runner defines BLADE_TEST_FMA_REF): the reference is
// the FULLY CONTRACTED ascending chain, one fma per step, spelled with std::fma
// so no compiler decision can move it. NOT the plain loop: under =fast g++
// vectorizes `__ps += a[t] * b[t]` as an in-order reduction (vmulpd products,
// then four scalar vaddsd in order) and fuses only the scalar tail, so the
// loop's bits under =fast are a per-loop vectorizer artifact, not one chain.
// The tile's vector-extension `c += a * b` contracts every step (vfmadd231pd),
// so under =fast it IS the one-fma-per-step chain. `plain_dot` is kept to
// MEASURE how far the old per-cell loop sits from the tile (reported, not
// gated).
__attribute__((noinline))
double plain_dot(const double* a, const double* b, size_t k) {
    double __ps = 0;
    for (size_t __pt = 0; __pt < k; __pt++) { __ps += a[__pt] * b[__pt]; }
    return __ps;
}

#ifdef BLADE_TEST_FMA_REF
__attribute__((noinline))
double serial_dot(const double* a, const double* b, size_t k) {
    double __ps = 0;
    for (size_t __pt = 0; __pt < k; __pt++) __ps = std::fma(a[__pt], b[__pt], __ps);
    return __ps;
}
#else
double serial_dot(const double* a, const double* b, size_t k) { return plain_dot(a, b, k); }
#endif

long long g_plain_cells = 0, g_plain_diff = 0;
double g_plain_maxrel = 0.0;

const unsigned long long GUARD = 0x7FF4DEADBEEF1234ULL;   // a signalling-NaN pattern

__attribute__((noinline)) double scaled(double v, int sk, double sc) {
    return sk == 1 ? v / sc : sk == 2 ? v * sc : v;
}

// G = row panels per group (dsyrk_tri's rowGroup: the driver's visiting
// order, never a bit); sk / sc = the kernel's scalar tail fused into the
// last K block's store (the reference applies the same one operation to the
// finished serial chain).
bool run_case(size_t p, size_t k, size_t S, Data d, bool threaded, size_t kcMax,
              unsigned long long seed, size_t G = 1, int sk = 0, double sc = 0.0) {
    Rng r(seed * 1000003ULL + p * 131 + k * 7 + S);
    // X: p rows, each k samples, separately allocated (a row skeleton, as Blade's).
    std::vector<std::vector<double>> rows(p, std::vector<double>(k + 1));
    std::vector<double*> X(p + 1, nullptr);
    for (size_t i = 0; i < p; i++) {
        for (size_t t = 0; t < k; t++) rows[i][t] = gen(r, d);
        X[i] = rows[i].data();
    }
    // C: the packed triangle, left-justified rows, guard cells on both sides.
    const size_t cells = (p > S) ? (S ? p * (p - 1) / 2 : p * (p + 1) / 2) : 0;
    const size_t pad = 16;
    std::vector<double> pool(cells + 2 * pad);
    for (auto& v : pool) std::memcpy(&v, &GUARD, 8);
    std::vector<double*> C(p + 1, nullptr);
    size_t off = 0;
    for (size_t i = 0; i + S < p; i++) { C[i] = pool.data() + pad + off; off += p - i - S; }
    blade_pgemm::dsyrk_tri(p, k, X.data(), C.data(), S, threaded, kcMax, sk, sc, G);
    bool ok = true;
    for (size_t i = 0; i + S < p && ok; i++)
        for (size_t j = i + S; j < p; j++) {
            const double want = scaled(serial_dot(X[i], X[j], k), sk, sc);
            const double got = C[i][j - i - S];
            const double plain = scaled(plain_dot(X[i], X[j], k), sk, sc);
            if (std::isfinite(plain) && std::isfinite(got)) {
                g_plain_cells++;
                if (std::memcmp(&plain, &got, 8) != 0) {
                    g_plain_diff++;
                    const double den = std::fabs(plain) > 0 ? std::fabs(plain) : 1.0;
                    const double rel = std::fabs(plain - got) / den;
                    if (d != Cancel && rel > g_plain_maxrel) g_plain_maxrel = rel;
                }
            }
            if (std::memcmp(&want, &got, 8) != 0) {
                std::printf("  cell (%zu, %zu): want %a got %a\n", i, j, want, got);
                ok = false;
                break;
            }
        }
    for (size_t g = 0; g < pad; g++) {
        if (std::memcmp(&pool[g], &GUARD, 8) != 0 || std::memcmp(&pool[pad + cells + g], &GUARD, 8) != 0) {
            std::printf("  guard cell %zu overwritten\n", g);
            ok = false;
            break;
        }
    }
    return ok;
}

// ---- the gram(A, A) NODE route ----------------------------------------------
// The BLAS-off gram node used to emit the triangular gram jam (CodeGenExpr
// materializeGramForm): per row i, cells j = i.. in tiles of R = 6 named
// accumulators, `__gacc_r += A[i][k] * conj_scalar(A[j][k])` for k ascending,
// then a one-cell remainder. That is reproduced here VERBATIM in arithmetic
// (conj_scalar is the identity on a real element, as in
// nested_array_utilities.hpp) so the tile is checked against the exact code it
// replaced: gated under =off, measured under =fast (where g++ may fuse the
// jam's and the tile's steps differently, exactly as for the plain loop).
inline double conj_scalar(double x) { return x; }

__attribute__((noinline))
void gram_jam_ref(size_t m, size_t n, double* const* A, double* const* G) {
    const size_t triR = 6;
    for (size_t gi = 0; gi < m; gi++) {
        const double* growi = &A[gi][0];
        const size_t gspan = m - gi;
        size_t gjr = 0;
        for (; gjr + triR <= gspan; gjr += triR) {
            const double* gj[6];
            for (size_t r = 0; r < 6; r++) gj[r] = &A[gi + gjr + r][0];
            double a0 = double(), a1 = double(), a2 = double(), a3 = double(), a4 = double(), a5 = double();
            for (size_t gk = 0; gk < n; gk++) {
                a0 += growi[gk] * conj_scalar(gj[0][gk]);
                a1 += growi[gk] * conj_scalar(gj[1][gk]);
                a2 += growi[gk] * conj_scalar(gj[2][gk]);
                a3 += growi[gk] * conj_scalar(gj[3][gk]);
                a4 += growi[gk] * conj_scalar(gj[4][gk]);
                a5 += growi[gk] * conj_scalar(gj[5][gk]);
            }
            G[gi][gjr + 0] = a0; G[gi][gjr + 1] = a1; G[gi][gjr + 2] = a2;
            G[gi][gjr + 3] = a3; G[gi][gjr + 4] = a4; G[gi][gjr + 5] = a5;
        }
        for (; gjr < gspan; gjr++) {
            const double* growj = &A[gi + gjr][0];
            double gacc = double();
            for (size_t gk = 0; gk < n; gk++) gacc += growi[gk] * conj_scalar(growj[gk]);
            G[gi][gjr] = gacc;
        }
    }
}

long long g_jam_cells = 0, g_jam_diff = 0;

// One gram(A, A) node shape: the call exactly as the node emits it
// (`dsyrk_tri(m, n, A.data, G.data, 0, threaded, 336)`), every packed cell
// against the serial chain (gated) and against the jam it replaced (gated
// under =off).
bool run_gram_case(size_t m, size_t n, Data d, bool threaded, unsigned long long seed, size_t G = 1) {
    Rng r(seed * 7919ULL + m * 31 + n);
    std::vector<double> pool(m * n + 1);                  // a dense, contiguous pool, as allocate<> builds
    std::vector<double*> A(m + 1, nullptr);
    for (size_t i = 0; i < m; i++) {
        A[i] = pool.data() + i * n;
        for (size_t t = 0; t < n; t++) A[i][t] = gen(r, d);
    }
    const size_t cells = m * (m + 1) / 2;
    std::vector<double> gt(cells + 1), gj(cells + 1);
    std::vector<double*> Gt(m + 1, nullptr), Gj(m + 1, nullptr);
    size_t off = 0;
    for (size_t i = 0; i < m; i++) { Gt[i] = gt.data() + off; Gj[i] = gj.data() + off; off += m - i; }
    blade_pgemm::dsyrk_tri(m, n, A.data(), Gt.data(), 0, threaded, 336, 0, 0.0, G);
    gram_jam_ref(m, n, A.data(), Gj.data());
    bool ok = true;
    for (size_t i = 0; i < m && ok; i++)
        for (size_t jr = 0; jr < m - i; jr++) {
            const double got = Gt[i][jr];
            const double want = serial_dot(A[i], A[i + jr], n);
            const double jam = Gj[i][jr];
            g_jam_cells++;
            const bool jamSame = std::memcmp(&jam, &got, 8) == 0;
            if (!jamSame) g_jam_diff++;
            if (std::memcmp(&want, &got, 8) != 0) {
                std::printf("  gram cell (%zu, %zu): chain %a tile %a\n", i, i + jr, want, got);
                ok = false;
                break;
            }
#ifndef BLADE_TEST_FMA_REF
            if (!jamSame) {
                std::printf("  gram cell (%zu, %zu): jam %a tile %a\n", i, i + jr, jam, got);
                ok = false;
                break;
            }
#endif
        }
    return ok;
}

std::string caseName(size_t p, size_t k, size_t S, Data d, bool th, size_t kcMax, size_t G, int sk, double sc) {
    char buf[200];
    std::snprintf(buf, sizeof buf, "p=%zu k=%zu S=%zu %s %s kc<=%zu G=%zu scale=%d/%g", p, k, S, dataName(d),
                  th ? "threaded" : "serial", kcMax, G, sk, sc);
    return buf;
}

void sweep(size_t p, size_t k, size_t S, Data d, bool th, size_t kcMax, unsigned long long seed) {
    check(run_case(p, k, S, d, th, kcMax, seed), caseName(p, k, S, d, th, kcMax, 1, 0, 0.0));
}

void sweepG(size_t p, size_t k, size_t S, Data d, bool th, size_t kcMax, unsigned long long seed, size_t G,
            int sk, double sc) {
    check(run_case(p, k, S, d, th, kcMax, seed, G, sk, sc), caseName(p, k, S, d, th, kcMax, G, sk, sc));
}

}  // namespace

int main() {
    const size_t KC = blade_pgemm::TRI_KC_MAX;
    // 0. Row-panel GROUPS (rowGroup = 2, 4, 5, 8) and the FUSED scalar tail:
    //    the driver's visiting order and the last block's store, against the
    //    scaled serial chain. Every p mod 24 (a group straddles the ragged last
    //    row panel and the top-aligned ragged first column panel), p below one
    //    group, several K blocks (the tail must wait for the last), both S,
    //    divide / multiply / divide by zero, threaded groups, the NC edge.
    for (size_t S = 0; S <= 1; S++) {
        for (size_t G : {2u, 4u, 5u, 8u})
            for (size_t p = 0; p <= 30; p++)
                for (size_t k : {0u, 3u, 17u})
                    sweepG(p, k, S, Cancel, false, KC, 20, G, 0, 0.0);
        for (size_t p = 24; p < 72; p++) {
            sweepG(p, 19, S, Cancel, false, 7, 21, 4, 1, 3.0);     // 3 K blocks, divide once at the end
            sweepG(p, 33, S, Random, false, 16, 22, 2, 2, 0.25);   // 3 blocks, multiply
            sweepG(p, 11, S, Zeros, false, KC, 23, 8, 1, 0.0);     // divide by zero (must still divide)
        }
        for (size_t p : {16u, 29u, 61u, 97u}) {
            sweepG(p, 701, S, Cancel, false, KC, 24, 4, 1, 401.0);
            sweepG(p, 4, S, Random, false, KC, 24, 4, 2, 0.5);
            sweepG(p, 0, S, Random, false, KC, 24, 4, 1, 0.0);     // the empty sum: +0 / 0
        }
        for (size_t p : {101u, 193u, 200u})
            sweepG(p, 257, S, Random, false, KC, 25, 8, 0, 0.0);
        for (size_t p : {61u, 193u, 401u}) {
            sweepG(p, 300, S, Cancel, true, KC, 26, 4, 1, 7.0);
            sweepG(p, 41, S, Random, true, 16, 27, 3, 0, 0.0);
        }
        sweepG(2057, 3, S, Random, false, KC, 28, 4, 0, 0.0);
        sweepG(2057, 21, S, Cancel, true, 8, 29, 4, 2, 0.125);
    }
    // 1. Small p, small k, both S, every data class, serial: every edge shape
    //    of a 6 x 8 tile against a ragged triangle.
    for (size_t S = 0; S <= 1; S++)
        for (size_t p = 0; p <= 30; p++)
            for (size_t k : {0u, 1u, 2u, 3u, 5u, 8u, 13u, 17u})
                for (Data d : {Random, Cancel})
                    sweep(p, k, S, d, false, KC, 1);
    for (size_t S = 0; S <= 1; S++)
        for (size_t k = 0; k <= 17; k++)
            for (Data d : {Random, Zeros, Subnormal, NonFinite})
                sweep(9 + k, k, S, d, false, KC, 2);
    // 2. Every p mod 24 (lcm of MR and NR) over two periods, with a K block split
    //    (small caps force the stored-partial reload).
    for (size_t S = 0; S <= 1; S++)
        for (size_t p = 24; p < 72; p++) {
            sweep(p, 19, S, Cancel, false, 7, 3);      // 3 K blocks of 7/6/6
            sweep(p, 33, S, Random, false, 16, 4);     // 3 blocks of 11
        }
    // 3. KC - 1 / KC / KC + 1 around the default cap, and the balanced split.
    for (size_t S = 0; S <= 1; S++)
        for (size_t k : {KC - 1, KC, KC + 1, 2 * KC, 2 * KC + 1, (size_t)1001})
            for (size_t p : {29u, 61u, 97u})
                sweep(p, k, S, Cancel, false, KC, 5);
    // 4. Larger p: many row panels, ragged last tiles.
    for (size_t S = 0; S <= 1; S++)
        for (size_t p : {101u, 193u, 200u})
            sweep(p, 257, S, Random, false, KC, 6);
    // 5. Threaded: the same bits for every team (cells split, sums never).
    for (size_t S = 0; S <= 1; S++)
        for (size_t p : {61u, 193u, 401u}) {
            sweep(p, 300, S, Cancel, true, KC, 7);
            sweep(p, 41, S, Random, true, 16, 8);
        }
    // 6. A second column block (TRI_NC = 2048): rows above the block meet it.
    for (size_t S = 0; S <= 1; S++)
        for (size_t p : {2049u, 2057u})
            sweep(p, 3, S, Random, false, KC, 9);
    sweep(2057, 21, 0, Cancel, true, 8, 10);
    // 7. The gram(A, A) NODE route: the shapes the jam was measured on
    //    (301 x 257, 61 x 2003, 1003 x 61), the crossover edge (16 x 4, 16 x 5,
    //    24 x 8), ragged panels and multi-K-block chains, every data class.
    {
        struct G { size_t m, n; };
        const G shapes[] = { {16, 4}, {16, 5}, {17, 7}, {24, 8}, {29, 1001}, {61, 31}, {61, 2003},
                             {97, 513}, {101, 337}, {301, 257}, {1003, 61} };
        unsigned long long seed = 100;
        for (const G& g : shapes)
            for (Data d : {Random, Cancel, Zeros, Subnormal, NonFinite, Positive}) {
                if (g.m * g.n > 200000 && d != Random && d != Cancel) continue;
                for (bool th : {false, true})
                    for (size_t G : {1u, 4u}) {
                        char buf[128];
                        std::snprintf(buf, sizeof buf, "gram node m=%zu n=%zu %s %s G=%zu", g.m, g.n, dataName(d),
                                      th ? "threaded" : "serial", G);
                        check(run_gram_case(g.m, g.n, d, th, seed++, G), buf);
                    }
            }
    }
    std::printf("gram node: tile vs the triangular gram jam it replaced: %lld of %lld cells differ in bits%s\n",
                g_jam_diff, g_jam_cells,
#ifdef BLADE_TEST_FMA_REF
                " (contract=fast: measured, not gated)"
#else
                " (contract=off: gated, must be 0)"
#endif
    );
#ifdef BLADE_TEST_FMA_REF
    std::printf("reference: one std::fma per step (contract=fast)\n");
#else
    std::printf("reference: the emitted serial prodsum loop (contract=off)\n");
#endif
    std::printf("tile vs the plain per-cell loop in this build: %lld of %lld finite cells differ in bits, max relative %.3g (cancellation data excluded)\n",
                g_plain_diff, g_plain_cells, g_plain_maxrel);
    std::printf("SYRK TILE TESTS: %d/%d passed\n", g_passed, g_total);
    return g_passed == g_total ? 0 : 1;
}
