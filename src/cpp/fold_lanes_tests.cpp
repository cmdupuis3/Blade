// fold_lanes_tests.cpp -- RAW-BIT tests of the explicit K-lane vector fold
// forms (blade_fold_lanes.hpp: sum / dot / sq / dot3, K = 4 and 8, double and
// float) against the scalar model of the lane tree the header documents,
// compiled in the SAME translation unit (the runner, tests/LinAlgTests.fs
// runFoldLanesTests, builds this file twice: -ffp-contract=off and =fast, the
// user default). The contract is the SAME in both: every product is rounded
// before its add, so the forms equal the model -- and each other across the
// two builds -- bit for bit, and the runner's census checks that no vfmadd
// appears in any blade_fold function of either build.
//
// Covered:
//   n = 0..35 (every block / vector / scalar tail combination for K = 4 and 8
//     at W = 4 and 8), 777 and 20011;
//   unaligned starts: the operands begin 0..7 elements into a 64-byte-aligned
//     buffer, and the cells past n are NaN so an over-read poisons the result;
//   same-pointer operands: dot(a, a) is bitwise sq(a) (one tree) and bitwise
//     the model;
//   the Path B partition: thread t's chunk of a cnt-element sweep through
//     blade_fold::chunk_lo, the threaded fold (an OpenMP team of nt) against
//     its serial twin (the same chunks walked by one thread, combined in the
//     same job order) for team sizes 1..16 around cnt = 0, 1, nt-1, nt, nt+1;
//   data: random signed, heavy cancellation, signed zeros, subnormal products,
//     Inf/NaN, positive.
// Output: one `[FAIL]: <name>` line per failure, a census line, then
//   FOLD LANES TESTS: <passed>/<total> passed
#include "blade_fold_lanes.hpp"
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <cmath>
#include <cstdint>
#include <vector>
#include <string>
#include <limits>
#ifdef _OPENMP
#include <omp.h>
#endif

namespace {

int g_total = 0, g_passed = 0;
long long g_vec_vs_serial_differ = 0, g_vec_vs_serial_cells = 0;

void check(bool ok, const std::string& name) {
    g_total++;
    if (ok) g_passed++;
    else std::printf("[FAIL]: %s\n", name.c_str());
}

template <typename T> bool bits_equal(T x, T y) { return std::memcmp(&x, &y, sizeof(T)) == 0; }

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

enum Data { Random, Cancel, Zeros, Subnormal, NonFinite, Positive, DataCount };

const char* dataName(Data d) {
    switch (d) {
    case Random: return "random"; case Cancel: return "cancel"; case Zeros: return "zeros";
    case Subnormal: return "subnormal"; case NonFinite: return "nonfinite"; case Positive: return "positive";
    default: return "?";
    }
}

template <typename T> T gen(Rng& r, Data d) {
    switch (d) {
    case Random: return (T)(r.unit() * 2.0 - 1.0);
    case Positive: return (T)r.unit();
    case Cancel: {
        const double big = (r.next() & 1) ? 1e8 : -1e8;
        return (T)((r.next() % 3 == 0) ? big * (1.0 + r.unit()) : r.unit() * 1e-3);
    }
    case Zeros: {
        const unsigned long long u = r.next() % 4;
        return u == 0 ? (T)0.0 : u == 1 ? (T)-0.0 : (T)((r.unit() - 0.5) * 1e-30);
    }
    case Subnormal: return sizeof(T) == 8 ? (T)((r.unit() - 0.5) * 1e-160) : (T)((r.unit() - 0.5) * 1e-20);
    case NonFinite: {
        const unsigned long long u = r.next() % 50;
        if (u == 0) return std::numeric_limits<T>::infinity();
        if (u == 1) return -std::numeric_limits<T>::infinity();
        return (T)(r.unit() * 2.0 - 1.0);
    }
    default: return (T)0;
    }
}

// ---------------------------------------------------------------------------
// The reference: the header's scalar model, whose step follows the same
// BLADE_FP_CONTRACT_FAST define as the vector forms. OUT OF LINE, like the
// forms under test, so neither side's arithmetic is reshaped by the harness
// loop around it (g++ 16 at -O3 SLP-vectorized an inlined tail's products
// away from their adds, which is exactly the fusion drift the define exists
// to rule out).
// ---------------------------------------------------------------------------
template <typename T, int K> __attribute__((noinline)) T ref_sum(const T* a, size_t n) { return blade_fold::model::sum<T, K>(a, n); }
template <typename T, int K> __attribute__((noinline)) T ref_dot(const T* a, const T* b, size_t n) { return blade_fold::model::dot<T, K>(a, b, n); }
template <typename T, int K> __attribute__((noinline)) T ref_sq(const T* a, size_t n) { return blade_fold::model::sq<T, K>(a, n); }
template <typename T, int K> __attribute__((noinline)) T ref_dot3(const T* a, const T* b, const T* c, size_t n) { return blade_fold::model::dot3<T, K>(a, b, c, n); }

// The sum of MATERIALIZED products: what `reduce(a * b, (+))` computes once
// `a * b` is stored (a generic function instance's lowering; corpus
// functions/168). For n < W the vector form must reproduce it bit for bit.
template <typename T> __attribute__((noinline)) T serial_dot(const T* a, const T* b, size_t n) {
    T s = 0;
    std::vector<T> p(n);
    for (size_t t = 0; t < n; t++) p[t] = a[t] * b[t];
    volatile T sink = 0; (void)sink;
    for (size_t t = 0; t < n; t++) { T v = p[t]; BLADE_FOLD_ROUND(v); s = s + v; }
    return s;
}

// A 64-byte-aligned buffer of `cap` cells, every cell NaN until filled.
template <typename T> struct Buf {
    std::vector<T> store;
    T* base;
    explicit Buf(size_t cap) : store(cap + 64 / sizeof(T) + 1) {
        for (auto& x : store) x = std::numeric_limits<T>::quiet_NaN();
        base = reinterpret_cast<T*>((reinterpret_cast<uintptr_t>(store.data()) + 63) & ~uintptr_t(63));
    }
};

template <typename T, int K>
void run_forms(const char* tname) {
    const size_t cap = 20011 + 64;
    Buf<T> A(cap), B(cap), C(cap);
    std::vector<size_t> ns;
    for (size_t n = 0; n <= 35; n++) ns.push_back(n);
    ns.push_back(777);
    ns.push_back(20011);
    for (int di = 0; di < DataCount; di++) {
        const Data d = (Data)di;
        Rng r(0xC0FFEEULL + di * 7919ULL + K * 31ULL + sizeof(T));
        for (size_t off = 0; off < 8; off++) {
            for (size_t n : ns) {
                // Reset to NaN past the operands, fill exactly n cells.
                for (size_t i = 0; i < cap; i++) { A.base[i] = B.base[i] = C.base[i] = std::numeric_limits<T>::quiet_NaN(); }
                T* a = A.base + off; T* b = B.base + off; T* c = C.base + off;
                for (size_t i = 0; i < n; i++) { a[i] = gen<T>(r, d); b[i] = gen<T>(r, d); c[i] = gen<T>(r, d); }
                char tag[320];
                std::snprintf(tag, sizeof tag, "%s K=%d %s off=%zu n=%zu", tname, K, dataName(d), off, n);
                const std::string tag0 = tag;   // before the dot check appends its hex detail
                const T vs = blade_fold::sum<T, K>(a, n), rs = ref_sum<T, K>(a, n);
                check(bits_equal(vs, rs), std::string("sum ") + tag);
                const T vd = blade_fold::dot<T, K>(a, b, n), rd = ref_dot<T, K>(a, b, n);
                if (!bits_equal(vd, rd)) {
                    // Which side left the tree: the plain (uncontracted) model
                    // and the serial chain, as hex, beside the two compared.
                    const T pd = blade_fold::model::dot<T, K>(a, b, n), sd = serial_dot(a, b, n);
                    unsigned long long hv = 0, hr = 0, hp = 0, hs = 0;
                    std::memcpy(&hv, &vd, sizeof(T)); std::memcpy(&hr, &rd, sizeof(T));
                    std::memcpy(&hp, &pd, sizeof(T)); std::memcpy(&hs, &sd, sizeof(T));
                    std::snprintf(tag + std::strlen(tag), sizeof(tag) - std::strlen(tag),
                                  " vec=%llx ref=%llx plainmodel=%llx serial=%llx", hv, hr, hp, hs);
                }
                check(bits_equal(vd, rd), std::string("dot ") + tag);
                const T vq = blade_fold::sq<T, K>(a, n), rq = ref_sq<T, K>(a, n);
                check(bits_equal(vq, rq), std::string("sq ") + tag);
                const T vaa = blade_fold::dot<T, K>(a, a, n);
                check(bits_equal(vaa, vq), std::string("dot(a,a) == sq(a) ") + tag);
                const T v3 = blade_fold::dot3<T, K>(a, b, c, n), r3 = ref_dot3<T, K>(a, b, c, n);
                check(bits_equal(v3, r3), std::string("dot3 ") + tag0);
                // No over-read: a finite answer on finite data, whatever the tail.
                if (d == Positive) {
                    check(std::isfinite((double)vs) && std::isfinite((double)vd) && std::isfinite((double)vq) && std::isfinite((double)v3),
                          std::string("no over-read past n ") + tag);
                    // For n < W the vector form IS the serial chain from +0.
                    if (n < 32 / sizeof(T)) {
                        check(bits_equal(vd, serial_dot(a, b, n)), std::string("n < W is the serial chain ") + tag);
                    }
                }
                if (d == Random && n == 20011) {
                    const T sd = serial_dot(a, b, n);
                    g_vec_vs_serial_cells++;
                    if (!bits_equal(vd, sd)) g_vec_vs_serial_differ++;
                }
            }
        }
    }
}

// Path B partition: the threaded fold over chunk_lo chunks against its serial
// twin, bit for bit, for every team size the runtime may hand back.
template <int K>
void run_partition() {
    const size_t cap = 20011 + 16;
    std::vector<double> a(cap);
    Rng r(0xBADC0DEULL + K);
    for (auto& x : a) x = r.unit() * 2.0 - 1.0;
    const int teams[] = { 1, 2, 3, 4, 5, 7, 8, 9, 12, 16 };
    for (int nt : teams) {
        std::vector<size_t> cnts = { 0, 1, (size_t)(nt > 1 ? nt - 1 : 0), (size_t)nt, (size_t)nt + 1, 101, 20011 };
        for (size_t cnt : cnts) {
            char tag[96];
            std::snprintf(tag, sizeof tag, "K=%d nt=%d cnt=%zu", K, nt, cnt);
            // Tiling: chunks are contiguous, exhaustive, balanced within one.
            bool tiled = blade_fold::chunk_lo(cnt, 0, (size_t)nt) == 0 && blade_fold::chunk_lo(cnt, (size_t)nt, (size_t)nt) == cnt;
            size_t minLen = SIZE_MAX, maxLen = 0;
            for (int t = 0; t < nt; t++) {
                const size_t lo = blade_fold::chunk_lo(cnt, (size_t)t, (size_t)nt), hi = blade_fold::chunk_lo(cnt, (size_t)t + 1, (size_t)nt);
                if (hi < lo) tiled = false;
                minLen = std::min(minLen, hi - lo); maxLen = std::max(maxLen, hi - lo);
            }
            check(tiled && maxLen - minLen <= 1, std::string("chunk_lo tiles [0, cnt) ") + tag);
            // Serial twin: walk the chunks in job order on one thread.
            double serial = 0.0;
            for (int t = 0; t < nt; t++) {
                const size_t lo = blade_fold::chunk_lo(cnt, (size_t)t, (size_t)nt), hi = blade_fold::chunk_lo(cnt, (size_t)t + 1, (size_t)nt);
                if (hi > lo) serial = serial + blade_fold::sq<double, K>(a.data() + lo, hi - lo);
            }
            // Threaded: a team of nt, each thread its chunk, combined in job order.
            std::vector<double> part(nt, 0.0);
            std::vector<char> had(nt, 0);
            int actual = nt;
#ifdef _OPENMP
            #pragma omp parallel num_threads(nt)
            {
                const int team = omp_get_num_threads();
                const int t = omp_get_thread_num();
                if (t == 0) actual = team;
                const size_t lo = blade_fold::chunk_lo(cnt, (size_t)t, (size_t)team), hi = blade_fold::chunk_lo(cnt, (size_t)t + 1, (size_t)team);
                if (hi > lo) { part[t] = blade_fold::sq<double, K>(a.data() + lo, hi - lo); had[t] = 1; }
            }
#else
            for (int t = 0; t < nt; t++) {
                const size_t lo = blade_fold::chunk_lo(cnt, (size_t)t, (size_t)nt), hi = blade_fold::chunk_lo(cnt, (size_t)t + 1, (size_t)nt);
                if (hi > lo) { part[t] = blade_fold::sq<double, K>(a.data() + lo, hi - lo); had[t] = 1; }
            }
#endif
            double threaded = 0.0;
            for (int t = 0; t < actual; t++) if (had[t]) threaded = threaded + part[t];
            // The twin walked nt chunks; if the runtime gave fewer threads the
            // partition differs by design (it is a function of the ACTUAL team),
            // so only a full team is compared bitwise.
            if (actual == nt) check(bits_equal(threaded, serial), std::string("threaded == serial-within-chunk ") + tag);
            else check(true, std::string("team smaller than requested, partition skipped ") + tag);
        }
    }
}

} // namespace

int main() {
    run_forms<double, 4>("f64");
    run_forms<double, 8>("f64");
    run_forms<float, 4>("f32");
    run_forms<float, 8>("f32");
    run_partition<4>();
    run_partition<8>();
    std::printf("lane tree vs the serial chain in this build: %lld of %lld random n=20011 dots differ in bits (expected: most -- the licence reassociates)\n",
                g_vec_vs_serial_differ, g_vec_vs_serial_cells);
#ifdef BLADE_FOLD_LANES_VECTOR
    std::printf("vector extensions: ON (blade_fold:: is the vector form, model:: the scalar tree); step: rounded product, then add (no fma)\n");
#else
    std::printf("vector extensions: OFF (blade_fold:: IS the scalar model; the comparison is a self-check)\n");
#endif
    std::printf("FOLD LANES TESTS: %d/%d passed\n", g_passed, g_total);
    return g_passed == g_total ? 0 : 1;
}
