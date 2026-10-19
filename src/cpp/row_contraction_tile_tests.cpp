// row_contraction_tile_tests.cpp -- RAW-BIT tests of the antisym-pair tile
// (blade_tiles.h blade_anti_tile_6x8 / _4x4, driver blade_pgemm::dantisym_tri)
// and the rank-3 simplex tile (W panel + the gram core, driver
// blade_pgemm::dsym3_tri) against the serial per-cell forms they replace,
// compiled in the SAME translation unit and the SAME -ffp-contract mode (the
// runner, tests/LinAlgTests.fs runRowContractionTileTests, builds this file
// twice: -ffp-contract=off -- bitwise by construction -- and =fast with
// BLADE_TEST_FMA_REF, where the reference is the one-fma-per-step chain).
//
// References (the emitted repro forms, verbatim, under =off):
//   antisym  P = 0; P += a0[t] * b1[t];  Q = 0; Q += b0[t] * a1[t];  L = P - Q
//   simplex  S = 0; S += a[t] * b[t] * c[t]                (= (a*b)*c, then +)
// then the kernel's scale (`/ d`, `* s`) on the finished value. Under =fast:
//   P = fma(a0, b1, P), Q = fma(b0, a1, Q), S = fma(a*b, c, S)   (std::fma).
// Every packed cell is compared by memcmp (bits), and guard cells on both
// sides of the pool must survive, for:
//   antisym  p = 0..30 x n in {0,1,2,3,5,8,13,17} (the strict empty last row,
//            p = 0/1 empty pools), every p mod 24 to 72, p = 101/193/401,
//            several column blocks (small NC), component pairs (0,1) (1,0)
//            (2,0) (1,1), scale none / div / mul / div-by-zero, both
//            schedules (sequential 6x8, paired 4x4), serial and threaded;
//   simplex  p = 0..30 (the short rows near k = p, every equality stratum
//            i = j = k, i = j < k, i < j = k, i < j < k), every p mod 24 to 72
//            with small K caps (stored-partial reload), KC-1 / KC / KC+1,
//            p = 97/101/171, scales as above, serial and threaded;
//   data: random signed, heavy cancellation, signed zeros, subnormal
//   products, Inf.
// Output: one `[FAIL]: <name>` line per failure, then
//   ROW TILE TESTS: <passed>/<total> passed
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

enum Data { Random, Cancel, Zeros, Subnormal, NonFinite };

double gen(Rng& r, Data d) {
    switch (d) {
    case Random: return r.unit() * 2.0 - 1.0;
    case Cancel: {
        const double big = (r.next() & 1) ? 1e8 : -1e8;
        return (r.next() % 3 == 0) ? big * (1.0 + r.unit()) : r.unit() * 1e-3;
    }
    case Zeros: {
        const unsigned long long u = r.next() % 4;
        return u == 0 ? 0.0 : u == 1 ? -0.0 : (r.unit() - 0.5) * 1e-300;
    }
    case Subnormal: return (r.unit() - 0.5) * 1e-105;   // pair/triple products land subnormal
    case NonFinite: {
        const unsigned long long u = r.next() % 60;
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
    case Subnormal: return "subnormal"; case NonFinite: return "nonfinite";
    }
    return "?";
}

// ---- the serial references ----
#ifdef BLADE_TEST_FMA_REF
__attribute__((noinline)) double dot2(const double* a, const double* b, size_t n) {
    double ps = 0;
    for (size_t t = 0; t < n; t++) ps = std::fma(a[t], b[t], ps);
    return ps;
}
__attribute__((noinline)) double dot3(const double* a, const double* b, const double* c, size_t n) {
    double ps = 0;
    for (size_t t = 0; t < n; t++) {
        const double ab = a[t] * b[t];
        ps = std::fma(ab, c[t], ps);
    }
    return ps;
}
#else
__attribute__((noinline)) double dot2(const double* a, const double* b, size_t n) {
    double __ps = 0;
    for (size_t __pt = 0; __pt < n; __pt++) { __ps += a[__pt] * b[__pt]; }
    return __ps;
}
__attribute__((noinline)) double dot3(const double* a, const double* b, const double* c, size_t n) {
    double __ps = 0;
    for (size_t __pt = 0; __pt < n; __pt++) { __ps += a[__pt] * b[__pt] * c[__pt]; }
    return __ps;
}
#endif

__attribute__((noinline)) double scaled(double v, int sk, double sc) {
    return sk == 1 ? v / sc : sk == 2 ? v * sc : v;
}

const unsigned long long GUARD = 0x7FF4DEADBEEF1234ULL;
const size_t PAD = 16;

bool guardsOk(const std::vector<double>& pool, size_t cells) {
    for (size_t g = 0; g < PAD; g++)
        if (std::memcmp(&pool[g], &GUARD, 8) != 0 || std::memcmp(&pool[PAD + cells + g], &GUARD, 8) != 0) {
            std::printf("  guard cell %zu overwritten\n", g);
            return false;
        }
    return true;
}

// ---- antisym ----
bool run_anti(size_t p, size_t n, size_t cx, size_t cy, int sk, double sc, Data d, bool th,
              int variant, size_t ncMax, unsigned long long seed, size_t kcMax = 0) {
    Rng r(seed * 1000003ULL + p * 131 + n * 7 + cx * 3 + cy);
    const size_t C = 3;
    std::vector<std::vector<double>> rows(p * C, std::vector<double>(n + 1));
    std::vector<std::vector<double*>> mid(p, std::vector<double*>(C));
    std::vector<double**> Z(p + 1, nullptr);
    for (size_t i = 0; i < p; i++) {
        for (size_t c = 0; c < C; c++) {
            for (size_t t = 0; t < n; t++) rows[i * C + c][t] = gen(r, d);
            mid[i][c] = rows[i * C + c].data();
        }
        Z[i] = mid[i].data();
    }
    const size_t cells = (p > 1) ? p * (p - 1) / 2 : 0;
    std::vector<double> pool(cells + 2 * PAD);
    for (auto& v : pool) std::memcpy(&v, &GUARD, 8);
    blade_pgemm::dantisym_tri(p, n, Z.data(), cx, cy, pool.data() + PAD, sk, sc, th, variant, ncMax, kcMax);
    bool ok = true;
    size_t off = 0;
    for (size_t i = 0; i + 1 < p && ok; i++)
        for (size_t j = i + 1; j < p; j++, off++) {
            const double P = dot2(Z[i][cx], Z[j][cy], n);
            const double Q = dot2(Z[j][cx], Z[i][cy], n);
            const double want = scaled(P - Q, sk, sc);
            const double got = pool[PAD + off];
            if (std::memcmp(&want, &got, 8) != 0) {
                std::printf("  cell (%zu, %zu): want %a got %a\n", i, j, want, got);
                ok = false;
                break;
            }
        }
    return ok && guardsOk(pool, cells);
}

void anti(size_t p, size_t n, size_t cx, size_t cy, int sk, double sc, Data d, bool th, int variant,
          size_t ncMax, unsigned long long seed, size_t kcMax = 0) {
    char buf[200];
    std::snprintf(buf, sizeof buf, "antisym %s p=%zu n=%zu c=(%zu,%zu) scale=%d/%g %s %s nc<=%zu kc<=%zu",
                  variant == 1 ? "4x4" : "6x8", p, n, cx, cy, sk, sc, dataName(d),
                  th ? "threaded" : "serial", ncMax, kcMax);
    check(run_anti(p, n, cx, cy, sk, sc, d, th, variant, ncMax, seed, kcMax), buf);
}

// ---- simplex ----
bool run_sym3(size_t p, size_t n, int sk, double sc, Data d, bool th, size_t kcMax,
              unsigned long long seed) {
    Rng r(seed * 1000003ULL + p * 131 + n * 7);
    std::vector<std::vector<double>> rows(p, std::vector<double>(n + 1));
    std::vector<double*> X(p + 1, nullptr);
    for (size_t i = 0; i < p; i++) {
        for (size_t t = 0; t < n; t++) rows[i][t] = gen(r, d);
        X[i] = rows[i].data();
    }
    const size_t cells = p * (p + 1) * (p + 2) / 6;
    std::vector<double> pool(cells + 2 * PAD);
    for (auto& v : pool) std::memcpy(&v, &GUARD, 8);
    blade_pgemm::dsym3_tri(p, n, X.data(), pool.data() + PAD, sk, sc, th, kcMax);
    bool ok = true;
    size_t off = 0;
    for (size_t i = 0; i < p && ok; i++)
        for (size_t j = i; j < p && ok; j++) {
            if (blade_pgemm::sym3_row_base(p, i, j) != off) {
                std::printf("  row base (%zu, %zu) = %zu, DFS offset %zu\n", i, j,
                            blade_pgemm::sym3_row_base(p, i, j), off);
                ok = false;
                break;
            }
            for (size_t k = j; k < p; k++, off++) {
                const double want = scaled(dot3(X[i], X[j], X[k], n), sk, sc);
                const double got = pool[PAD + off];
                if (std::memcmp(&want, &got, 8) != 0) {
                    std::printf("  cell (%zu, %zu, %zu): want %a got %a\n", i, j, k, want, got);
                    ok = false;
                    break;
                }
            }
        }
    return ok && guardsOk(pool, cells);
}

void sym3(size_t p, size_t n, int sk, double sc, Data d, bool th, size_t kcMax, unsigned long long seed) {
    char buf[200];
    std::snprintf(buf, sizeof buf, "simplex p=%zu n=%zu scale=%d/%g %s %s kc<=%zu", p, n, sk, sc,
                  dataName(d), th ? "threaded" : "serial", kcMax);
    check(run_sym3(p, n, sk, sc, d, th, kcMax, seed), buf);
}

}  // namespace

int main() {
    const size_t KC = blade_pgemm::TRI_KC_MAX;
    // ===== antisym =====
    for (int v = 0; v <= 1; v++) {
        // 1. Small p (the strict empty last row; p = 0, 1 empty pools), small n.
        for (size_t p = 0; p <= 30; p++)
            for (size_t n : {0u, 1u, 2u, 3u, 5u, 8u, 13u, 17u})
                for (Data d : {Random, Cancel})
                    anti(p, n, 0, 1, 0, 0.0, d, false, v, 0, 1);
        // 2. Data classes and component pairs.
        for (size_t n = 0; n <= 17; n++)
            for (Data d : {Random, Zeros, Subnormal, NonFinite}) {
                anti(9 + n, n, 0, 1, 0, 0.0, d, false, v, 0, 2);
                anti(9 + n, n, 1, 0, 0, 0.0, d, false, v, 0, 3);
            }
        anti(37, 23, 2, 0, 0, 0.0, Cancel, false, v, 0, 4);
        anti(37, 23, 1, 1, 0, 0.0, Cancel, false, v, 0, 4);
        // 3. Scales: divide, multiply, divide by zero (the source must still divide).
        for (size_t p : {1u, 2u, 13u, 29u, 61u}) {
            anti(p, 41, 0, 1, 1, 401.0, Random, false, v, 0, 5);
            anti(p, 41, 0, 1, 2, 0.25, Cancel, false, v, 0, 5);
            anti(p, 41, 0, 1, 1, 0.0, Random, false, v, 0, 5);
            anti(p, 0, 0, 1, 1, 0.0, Random, false, v, 0, 5);   // (+0 - +0) / 0
        }
        // 4. Every p mod 24 over two periods; several column blocks (small NC).
        for (size_t p = 24; p < 72; p++) {
            anti(p, 19, 0, 1, 0, 0.0, Cancel, false, v, 0, 6);
            anti(p, 33, 0, 1, 0, 0.0, Random, false, v, 16, 7);
        }
        // 5. Larger p, longer chains (KC-scale n: the whole chain in one visit).
        for (size_t p : {101u, 193u})
            for (size_t n : {KC - 1, KC + 1, (size_t)401, (size_t)600})
                anti(p, n, 0, 1, 0, 0.0, Cancel, false, v, 0, 8);
        // 6. Threaded: the same bits for every team.
        for (size_t p : {61u, 193u, 401u}) {
            anti(p, 300, 0, 1, 0, 0.0, Cancel, true, v, 0, 9);
            anti(p, 41, 0, 1, 1, 7.0, Random, true, v, 24, 10);
        }
        // 7. SAMPLE BLOCKS inside a tile visit (kcMax): the P and Q partials
        //    spilled and reloaded exactly between balanced blocks -- forced
        //    small (7: ragged chains of 2-3 blocks), the tuned sizes (128,
        //    168) over the bench chain (401 = 4 x 101 balanced), a cap above n
        //    (one block), every data class, scales, several column blocks,
        //    threaded.
        for (size_t p = 0; p <= 30; p++)
            for (size_t n : {0u, 1u, 7u, 8u, 13u, 17u, 29u})
                anti(p, n, 0, 1, 0, 0.0, Cancel, false, v, 0, 30, 7);
        for (size_t n = 0; n <= 17; n++)
            for (Data d : {Random, Zeros, Subnormal, NonFinite})
                anti(9 + n, n, 1, 0, 0, 0.0, d, false, v, 0, 31, 5);
        for (size_t p = 24; p < 72; p++) {
            anti(p, 19, 0, 1, 1, 3.0, Cancel, false, v, 0, 32, 7);
            anti(p, 33, 0, 1, 2, 0.25, Random, false, v, 16, 33, 16);
        }
        for (size_t p : {61u, 101u, 193u})
            for (size_t n : {127u, 128u, 129u, 401u, 600u})
                for (size_t kc : {100u, 128u, 168u, 1000u})
                    anti(p, n, 0, 1, 0, 0.0, Cancel, false, v, 0, 34, kc);
        for (size_t p : {61u, 401u}) {
            anti(p, 401, 0, 1, 0, 0.0, Cancel, true, v, 0, 35, 128);
            anti(p, 300, 0, 1, 1, 7.0, Random, true, v, 24, 36, 100);
        }
    }
    // ===== simplex =====
    // 1. Small p: short rows near k = p, every equality stratum.
    for (size_t p = 0; p <= 30; p++)
        for (size_t n : {0u, 1u, 2u, 3u, 5u, 8u, 13u, 17u})
            for (Data d : {Random, Cancel})
                sym3(p, n, 0, 0.0, d, false, KC, 11);
    for (size_t n = 0; n <= 17; n++)
        for (Data d : {Random, Zeros, Subnormal, NonFinite})
            sym3(9 + n, n, 0, 0.0, d, false, KC, 12);
    // 2. Scales.
    for (size_t p : {1u, 2u, 13u, 29u, 61u}) {
        sym3(p, 67, 1, 201.0, Random, false, KC, 13);
        sym3(p, 67, 2, 0.125, Cancel, false, KC, 13);
        sym3(p, 67, 1, 0.0, Random, false, KC, 13);
        sym3(p, 0, 1, 0.0, Random, false, KC, 13);           // +0 / 0
    }
    // 3. Every p mod 24 with K blocks forced (the unscaled-partial reload), scaled.
    for (size_t p = 24; p < 72; p++) {
        sym3(p, 19, 1, 3.0, Cancel, false, 7, 14);           // 3 K blocks of 7/6/6
        sym3(p, 33, 0, 0.0, Random, false, 16, 15);          // 3 blocks of 11
    }
    // 4. KC - 1 / KC / KC + 1 around the default cap.
    for (size_t n : {KC - 1, KC, KC + 1, 2 * KC + 1})
        for (size_t p : {29u, 37u})
            sym3(p, n, 1, 201.0, Cancel, false, KC, 16);
    // 5. Larger p, the bench shape.
    for (size_t p : {97u, 101u, 171u})
        sym3(p, 201, 1, 201.0, Random, false, KC, 17);
    // 6. Threaded.
    for (size_t p : {61u, 101u}) {
        sym3(p, 201, 1, 201.0, Cancel, true, KC, 18);
        sym3(p, 41, 2, 0.5, Random, true, 16, 19);
    }
    // 7. The top-aligned k panels (the ragged panel is k < p mod 8, skipped by
    //    every prefix past it) with the scale FUSED into the one block's
    //    store: every p mod 8 twice over, one K block, both tails, and the
    //    bench shape with a divide by zero.
    for (size_t p = 24; p < 41; p++) {
        sym3(p, 11, 1, 11.0, Cancel, false, KC, 40);
        sym3(p, 12, 2, 0.5, Random, false, KC, 41);
    }
    sym3(171, 201, 1, 0.0, Random, false, KC, 42);
    sym3(171, 201, 1, 201.0, Cancel, true, KC, 43);
#ifdef BLADE_TEST_FMA_REF
    std::printf("reference: one std::fma per step (contract=fast)\n");
#else
    std::printf("reference: the emitted serial forms (contract=off)\n");
#endif
    std::printf("ROW TILE TESTS: %d/%d passed\n", g_passed, g_total);
    return g_passed == g_total ? 0 : 1;
}
