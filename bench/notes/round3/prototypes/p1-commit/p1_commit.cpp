// W3-3 (round 3, wave 3) -- D/P1: the PRODUCER-FUSED ORDERED COMMIT for the
// strict mt8 rank-2 orbit walk (pairkernel's checksum), as a costed standalone
// prototype. NOT a compiler change: the only shared code is the serial
// scatter's own group body, `blade_scatter::tri_group` / `tri_tail_row`
// (factored out of `tri` in src/cpp/blade_orbit_scatter.hpp, behaviour
// unchanged), which this file calls one interval at a time behind a ticket.
//
// Source of every non-commit piece: `blade emit bench/kernels/pairkernel/
// pairkernel.blade` at 361504b6 under BLADE_FP_REPRO=1 BLADE_FP_CONTRACT=off
// with threads (pairkernel_repro_emit.cpp beside this file: the threaded
// `comm` producer of `kmat`, schedule(dynamic) over rows, then the serial
// ordered scatter `scatter2<4, 64, true>` for `ss`) and the same under
// BLADE_ORBIT_PULL=0 BLADE_FOLD_ROW_SLOTS=prefer (pairkernel_repro_rowslots_
// emit.cpp: the G35 row slots). lcg / matern52 / the cell expression / the
// logical-walk row callable are copied verbatim (n generalized only where the
// identity mode needs another extent).
//
// Build (PowerShell, ucrt64 first on PATH; the flags are Build.fs optFlags
// under BLADE_FP_CONTRACT=off + the safety flags + -fopenmp):
//   g++ -std=c++17 -O3 -march=native -ffp-contract=off -fno-math-errno -fwrapv
//       -fopenmp -Werror=float-conversion -Werror=narrowing -I src/cpp
//       -o p1_commit.exe bench/notes/round3/prototypes/p1-commit/p1_commit.cpp
//
// Modes (argv[1]; G = argv[2] in {1, 4, 8}; the team is OMP_NUM_THREADS /
// OMP_THREAD_LIMIT exactly as for the emitted program):
//   a      the producer alone (control): pts, kmat, the input checksum
//   a2 G   the producer through the commit's G-row claim loop, no consumer
//          (the production-schedule control for d)
//   b G    the producer + today's default: the serial ordered scatter
//          (scatter2<G, 64, true>; G = 4 is the emitted default)
//   c      the producer + the G35 row slots (the emitted form, verbatim)
//   d G    the producer-fused ticket commit
//   di G   d with the instruments (rdtsc per group: production, wait,
//          commit service; actual team, handoffs, longest stall, drain)
//   id     the raw-bit identity matrix (see identity_main)
// Every timed mode prints `pairkernel completed in <t>s` (the emitted
// program's own timer: the whole of main's computation), the checksum, its
// raw bits, and an FNV-1a hash of the row-chain array R where one exists.
#include <cstdint>
#include <cstdlib>
#include <cstring>
#include <cmath>
#include <complex>
#include <functional>
#include <tuple>
#include <variant>
#include <string>
#include <iostream>
#include <iomanip>
#include <chrono>
#include <algorithm>
#include <numeric>
#include <vector>
#include <unordered_map>
#include <unordered_set>
#include <atomic>
#include <thread>
#include <cstdio>
#include <omp.h>
#include <immintrin.h>
#include <x86intrin.h>
#include "nested_array_utilities.hpp"
#include "nested_array_types.hpp"
#include "orbit_wreath_utilities.hpp"
#include "rand_runtime.hpp"
#include <exception>
#include "blade_runtime.hpp"
#include "blade_orbit_scatter.hpp"
#include "blade_fold_lanes.hpp"
using std::cout;
using std::endl;

using Pts = int64_t;
using Dim = int64_t;
static constexpr const size_t __v33_symm[2] = {1, 1};

// ---------------------------------------------------------------------------
// Verbatim from the emission.
// ---------------------------------------------------------------------------
double lcg(int64_t k, int64_t seed) {
    int64_t __v6 = ((((k + seed) * 1103515245L) + 12345L) % 2147483648L);
    int64_t __v7 = ((__v6 * ((__v6 / 4096L) + 1L)) % 2147483648L);
    int64_t __v8 = (((__v7 * 1103515245L) + 12345L) % 2147483648L);
    int64_t __v9 = (((__v8 * __v8) / 65536L) % 2147483648L);
    int64_t __v10 = (((__v9 * 1103515245L) + 12345L) % 2147483648L);
    return (static_cast<double>(__v10) / 2147483648.0);
}

double matern52(double r2) {
    auto __v14 = (std::sqrt((5.0 * r2)) / 0.7);
    return (((1.0 + __v14) + ((__v14 * __v14) / 3.0)) * blade_libm::exp(-(__v14)));
}

double __lambda_79(double a, double b) { return (a + b); }
double __lambda_81(double u, double v) { return (u + v); }
double __lambda_84(double a, double b) { return (a + b); }
double __lambda_86(double u, double v) { return (u + v); }

// The G35 row callable (the logical walk of one row through the
// canonicalizing accessor), verbatim.
double __lambda_80(Pts i, Array<double, 2>& kmat) {
    BLADE_FRAME("lambda in ss", nullptr, 0);
    auto __wrap_79___ret1073741824 = [&](double a, double b) { return __lambda_79(a, b); };
    double __ret1073741824 = 0.0;
    for (size_t __i0 = 0; __i0 < 2503; __i0++) {
        int64_t j = __i0;
        __ret1073741824 = __wrap_79___ret1073741824(__ret1073741824, ([&]() { const auto __bo = ([&]() -> double { std::array<size_t,2> __g0 = { (size_t)(i), (size_t)(j) }; bool __z0; int __p0 = nested_array_utilities::canon_fold<2>(__g0, false, __z0); if (__z0) return double(); auto __c0 = nested_array_utilities::canon_left_justify<2>(__g0, false); double __v = kmat[__c0[0]][__c0[1]]; double __tv0 = nested_array_utilities::canon_transform<double>(__v, __p0, nested_array_utilities::ReadTransform::Identity); return __tv0; }()); return (__bo * __bo); }()));
    }
    return __ret1073741824;
}

// ---------------------------------------------------------------------------
// The program state (the emission's __v24 = pts, __v33 = kmat).
// ---------------------------------------------------------------------------
struct State {
    size_t n = 0;
    size_t pts_ext[2] = {0, 3};
    size_t kmat_ext[2] = {0, 0};
    Array<double, 2> pts;
    Array<double, 2> kmat;
    double* pool = nullptr;
};

static void alloc_state(State& s, size_t n) {
    s.n = n;
    s.pts_ext[0] = n; s.pts_ext[1] = 3;
    s.kmat_ext[0] = n; s.kmat_ext[1] = n;
    s.pts = { allocate<typename promote<double, 2>::type, nullptr>(s.pts_ext), s.pts_ext };
    s.kmat = { allocate<typename promote<double, 2>::type, __v33_symm>(s.kmat_ext), s.kmat_ext };
    s.pool = nested_array_utilities::pool_base(s.kmat.data);
}

// The input map, verbatim (n generalized).
static void make_pts(State& s) {
    Array<double, 2>& __v24 = s.pts;
    const size_t n = s.n;
    BLADE_OMP_PARALLEL_FOR
for (size_t __i0 = 0; __i0 < n; __i0++) {
        int64_t i = __i0;
        double* BLADE_RESTRICT __orow___v24 = __v24[__i0];
        BLADE_IVDEP
    for (size_t __i1 = 0; __i1 < 3; __i1++) {
            int64_t d = __i1;
            __orow___v24[__i1] = lcg(i, (10000019L * (d + 1L)));
        }
    }
}

// ONE producer row of kmat: the emitted nest's inner loop, verbatim (the
// offset arithmetic with n generalized: 2504 = n + 1, 3133756 = n(n+1)/2).
static inline void produce_row(State& s, size_t __i0) {
    Array<double, 2>& __v24 = s.pts;
    double* __opool___v33 = s.pool;
    const size_t n = s.n;
    const size_t __oend___v33_0 = (n + 1) - __i0;
    const size_t __ooff___v33_0 = (n * (n + 1) / 2) - ((__oend___v33_0 * (__oend___v33_0 - 1)) / 2);
    Array<double, 1> __v24____i0 = { __v24.data[__i0], __v24.extents + 1 };
    double* BLADE_RESTRICT __orow___v33 = __opool___v33 + __ooff___v33_0;
    BLADE_IVDEP
    for (size_t __i1 = 0; __i1 < n - __i0; __i1++) {
        Array<double, 1> __v24____i1 = { __v24.data[__i1 + __i0], __v24.extents + 1 };
        __orow___v33[__i1] = ([&]() { auto __v29 = (__v24____i0[0L] - __v24____i1[0L]); return ([&]() { auto __v30 = (__v24____i0[1L] - __v24____i1[1L]); return ([&]() { auto __v31 = (__v24____i0[2L] - __v24____i1[2L]); return matern52((((__v29 * __v29) + (__v30 * __v30)) + (__v31 * __v31))); }()); }()); }());
    }
}

// The emitted producer: omp parallel for schedule(dynamic) over rows.
static void produce_dynamic(State& s) {
    const size_t n = s.n;
    BLADE_OMP_PARALLEL_FOR_DYNAMIC
for (size_t __i0 = 0; __i0 < n; __i0++) produce_row(s, __i0);
}

// The input checksum, verbatim.
static double input_checksum(State& s) {
    Array<double, 2>& __v24 = s.pts;
    auto __wrap_86___v87 = [&](double u, double v) { return __lambda_86(u, v); };
    double __v87 = 0.0;
    for (size_t __i0 = 0; __i0 < s.n; __i0++) {
        Array<double, 1> __v24____i0 = { __v24.data[__i0], __v24.extents + 1 };
        __v87 = __wrap_86___v87(__v87, [&]() { if (__v24____i0.extents[0] == 0) { blade_rt::panic("BL8003", "reduce: empty array, no reduction possible", nullptr, 0); } auto __wrap_84 = [&](double a, double b) { return __lambda_84(a, b); }; double __r = __v24____i0[0]; for (size_t __ri = 1; __ri < __v24____i0.extents[0]; __ri++) { __r = __wrap_84(__r, __v24____i0[__ri]); } return __r; }());
    }
    return __v87;
}

// ---------------------------------------------------------------------------
// The commit schedule.
// ---------------------------------------------------------------------------
struct alignas(64) PaddedAtomic {
    std::atomic<size_t> v{0};
    char pad[64 - sizeof(std::atomic<size_t>)];
};

// What one commit region did. Times in ns (rdtsc, calibrated against
// steady_clock over the region by the master thread).
struct CommitStats {
    int requested = 1;
    int actual = 1;
    size_t ng = 0;
    double wall_ns = 0;      // region wall (master: before the region .. after its barrier)
    double prod_ns = 0;      // summed per-group production (claim .. last row stored)
    double wait_ns = 0;      // summed per-group ticket wait (produced .. ticket acquired)
    double commit_ns = 0;    // summed commit service S (ticket acquired .. released)
    double startup_ns = 0;   // summed (thread's first claim - region start)
    double drain_ns = 0;     // summed (region end - thread's exit from the claim loop)
    double max_wait_ns = 0;  // the longest single predecessor stall
    size_t max_wait_g = 0;
    size_t waited = 0;       // groups whose ticket was not yet theirs on arrival
    size_t yields = 0;       // spin-limit yields, summed
    size_t handoffs = 0;     // commits whose committer differs from the previous group's
    double prod_med = 0, prod_max = 0, commit_med = 0, commit_max = 0, wait_med = 0;
    double prod_first8 = 0, prod_last8 = 0;  // mean production of the first / last 8 groups
};

static inline uint64_t tsc() { return __rdtsc(); }

static constexpr unsigned SPIN_LIMIT = 2048;  // _mm_pause spins before each yield

// Rank 2 through the producer-fused ordered commit:
//   prelude: R[0..n) = si; out = so; ticket = 0; claim = 0
//   #pragma omp parallel num_threads(T)
//     for (g = claim.fetch_add(1, relaxed); g < ng; g = claim.fetch_add(1, relaxed)):
//         produce pool rows [gG, min(gG + G, n))        // every row the interval reads
//         wait until ticket.load(acquire) == g           // _mm_pause, yield after SPIN_LIMIT
//         full group (gG + G <= n): tri_group<G, B, VADD, false>(S, n, R, .., gG, f, ki)
//         tail interval (the last, < G rows):  tri_tail_row<false> per row, ascending
//         for i in [gG, min(gG + G, n)): out = ko(out, R[i])   // ONE outer accumulator
//         ticket.store(g + 1, release)
// The interval sequence is exactly tri's call sequence (full groups at
// i0 = 0, G, 2G, .., then the tail rows), each call on the values the
// previous calls left (the ticket's release / acquire publishes R and out;
// the interval's pool rows were stored by the committing thread itself, in
// program order before its commit). So every row chain receives the serial
// scatter's operand sequence from its seed, and the outer chain receives
// R[0], R[1], .. in increasing i from its seed: scatter2's tail, with no
// subtotal and no reseed. Claims are explicit ascending canonical intervals,
// so ticket g waits only on intervals < g, each already claimed by a running
// thread (progress assumes runnable workers eventually run).
template <int G, int B, bool VADD, bool INSTR, class P, class F, class KI, class KO>
double commit2(double* S, size_t n, double si, double so, const P& produce, const F& f, const KI& ki, const KO& ko,
               int nthreads, double* Rcopy, CommitStats* st) {
    double* R = new double[n > 0 ? n : 1];
    for (size_t i = 0; i < n; i++) R[i] = si;
    double out = so;
    const size_t nfull = n / (size_t)G;
    const size_t ng = nfull + ((n % (size_t)G) ? 1 : 0);
    if (nthreads < 1) nthreads = 1;
    if (ng > 0 && (size_t)nthreads > ng) nthreads = (int)ng;
    PaddedAtomic claim, ticket;
    // instruments (INSTR only): per group, written by its one claimer
    std::vector<uint64_t> t_claim, t_prod, t_acq, t_rel;
    std::vector<int> who;
    std::vector<uint32_t> nyield;
    std::vector<uint64_t> t_enter, t_exit;
    if (INSTR) {
        t_claim.assign(ng, 0); t_prod.assign(ng, 0); t_acq.assign(ng, 0); t_rel.assign(ng, 0);
        who.assign(ng, -1); nyield.assign(ng, 0);
        t_enter.assign(nthreads, 0); t_exit.assign(nthreads, 0);
    }
    int actual = 1;
    const auto c0 = std::chrono::steady_clock::now();
    const uint64_t r0 = tsc();
    #pragma omp parallel num_threads(nthreads)
    {
        const int tn = omp_get_thread_num();
        if (tn == 0) actual = omp_get_num_threads();
        if (INSTR) t_enter[tn] = tsc();
        for (size_t g = claim.v.fetch_add(1, std::memory_order_relaxed); g < ng;
             g = claim.v.fetch_add(1, std::memory_order_relaxed)) {
            const size_t lo = g * (size_t)G;
            const size_t hi = std::min(lo + (size_t)G, n);
            if (INSTR) t_claim[g] = tsc();
            for (size_t i = lo; i < hi; i++) produce(i);
            if (INSTR) t_prod[g] = tsc();
            if (ticket.v.load(std::memory_order_acquire) != g) {
                unsigned spins = 0;
                uint32_t ny = 0;
                while (ticket.v.load(std::memory_order_acquire) != g) {
                    _mm_pause();
                    if (++spins >= SPIN_LIMIT) { spins = 0; ny++; std::this_thread::yield(); }
                }
                if (INSTR) nyield[g] = ny + 1;  // +1 marks "waited"
            }
            if (INSTR) t_acq[g] = tsc();
            if (g < nfull) blade_scatter::tri_group<G, B, VADD, false>((const double*)S, n, R, (double*)nullptr, 0, 0, lo, f, ki);
            else for (size_t i = lo; i < hi; i++) blade_scatter::tri_tail_row<false>((const double*)S, n, R, (double*)nullptr, 0, 0, i, f, ki);
            for (size_t i = lo; i < hi; i++) out = ko(out, R[i]);
            if (INSTR) { t_rel[g] = tsc(); who[g] = tn; }
            ticket.v.store(g + 1, std::memory_order_release);
        }
        if (INSTR) t_exit[tn] = tsc();
    }
    const uint64_t r1 = tsc();
    const auto c1 = std::chrono::steady_clock::now();
    if (st) {
        const double wall = (double)std::chrono::duration_cast<std::chrono::nanoseconds>(c1 - c0).count();
        const double tpn = (double)(r1 - r0) / (wall > 0 ? wall : 1.0);  // ticks per ns
        st->requested = nthreads;
        st->actual = actual;
        st->ng = ng;
        st->wall_ns = wall;
        if (INSTR) {
            std::vector<double> pv(ng), cv(ng), wv(ng);
            for (size_t g = 0; g < ng; g++) {
                pv[g] = (double)(t_prod[g] - t_claim[g]) / tpn;
                wv[g] = (double)(t_acq[g] - t_prod[g]) / tpn;
                cv[g] = (double)(t_rel[g] - t_acq[g]) / tpn;
                st->prod_ns += pv[g];
                st->wait_ns += wv[g];
                st->commit_ns += cv[g];
                if (wv[g] > st->max_wait_ns) { st->max_wait_ns = wv[g]; st->max_wait_g = g; }
                if (nyield[g]) { st->waited++; st->yields += nyield[g] - 1; }
                if (g > 0 && who[g] != who[g - 1]) st->handoffs++;
            }
            for (int t = 0; t < actual; t++) {
                if (t_enter[t]) st->startup_ns += (double)(t_enter[t] - r0) / tpn;
                if (t_exit[t]) st->drain_ns += (double)(r1 - t_exit[t]) / tpn;
            }
            const size_t k = std::min<size_t>(8, ng);
            for (size_t g = 0; g < k; g++) { st->prod_first8 += pv[g] / (double)k; st->prod_last8 += pv[ng - 1 - g] / (double)k; }
            auto med = [](std::vector<double> v) { if (v.empty()) return 0.0; std::nth_element(v.begin(), v.begin() + v.size() / 2, v.end()); return v[v.size() / 2]; };
            st->prod_med = med(pv); st->commit_med = med(cv); st->wait_med = med(wv);
            st->prod_max = ng ? *std::max_element(pv.begin(), pv.end()) : 0;
            st->commit_max = ng ? *std::max_element(cv.begin(), cv.end()) : 0;
        }
    }
    if (Rcopy) std::memcpy(Rcopy, R, n * sizeof(double));
    delete[] R;
    return out;
}

// The production-schedule control (a2): the same claim loop, no ticket, no consumer.
template <int G, class P>
void claims_only(size_t n, const P& produce, int nthreads) {
    const size_t ng = (n + (size_t)G - 1) / (size_t)G;
    if (nthreads < 1) nthreads = 1;
    if (ng > 0 && (size_t)nthreads > ng) nthreads = (int)ng;
    PaddedAtomic claim;
    #pragma omp parallel num_threads(nthreads)
    {
        for (size_t g = claim.v.fetch_add(1, std::memory_order_relaxed); g < ng;
             g = claim.v.fetch_add(1, std::memory_order_relaxed)) {
            const size_t lo = g * (size_t)G;
            const size_t hi = std::min(lo + (size_t)G, n);
            for (size_t i = lo; i < hi; i++) produce(i);
        }
    }
}

// The serial scatter with its R exposed (scatter2's body, verbatim, R copied out).
template <int G, int B, bool VADD, class F, class KI, class KO>
double scatter2_R(const double* S, size_t n, double si, double so, const F& f, const KI& ki, const KO& ko, double* Rcopy) {
    double* R = new double[n > 0 ? n : 1];
    for (size_t i = 0; i < n; i++) R[i] = si;
    blade_scatter::tri<G, B, VADD, false>(S, n, R, (double*)nullptr, 0, 0, f, ki);
    double out = so;
    for (size_t i = 0; i < n; i++) out = ko(out, R[i]);
    if (Rcopy) std::memcpy(Rcopy, R, n * sizeof(double));
    delete[] R;
    return out;
}

static uint64_t fnv(const void* p, size_t bytes) {
    const unsigned char* b = (const unsigned char*)p;
    uint64_t h = 1469598103934665603ull;
    for (size_t i = 0; i < bytes; i++) { h ^= b[i]; h *= 1099511628211ull; }
    return h;
}
static uint64_t bits(double x) { uint64_t u; std::memcpy(&u, &x, 8); return u; }

// ---------------------------------------------------------------------------
// The timed modes: main's computation, as the emitted program times it.
// ---------------------------------------------------------------------------
template <int G>
static int timed_main(const std::string& mode) {
    cout << std::setprecision(15);
    const size_t n = 2503;
    auto t0 = std::chrono::steady_clock::now();
    State s;
    alloc_state(s, n);
    make_pts(s);
    const int T = blade_omp_get_max_threads();
    auto kin = [](double __a, double __b) -> double { return __a + __b; };  // __sk1___v60
    auto kout = [](double __a, double __b) -> double { return __a + __b; }; // __sk0___v60
    auto f = [&](double __sx) -> double { return (__sx * __sx); };          // __sf___v60
    std::vector<double> R(n);
    bool haveR = false;
    double __v60 = 0.0;
    CommitStats st;
    bool instr = false;
    if (mode == "a") {
        produce_dynamic(s);
    } else if (mode == "a2") {
        claims_only<G>(n, [&](size_t i) { produce_row(s, i); }, T);
    } else if (mode == "b") {
        produce_dynamic(s);
        const double* __spool___v60 = nested_array_utilities::pool_base(s.kmat.data);
        __v60 = scatter2_R<G, 64, true>(__spool___v60, n, double(0.0), __v60, f, kin, kout, R.data());
        haveR = true;
    } else if (mode == "c") {
        produce_dynamic(s);
        Array<double, 2>& __v33 = s.kmat;
        auto __wrap_81___v60 = [&](double u, double v) { return __lambda_81(u, v); };
        const size_t __rn___v60 = 2503;
        int __rT___v60 = blade_omp_get_max_threads();
        if ((size_t)__rT___v60 > __rn___v60) __rT___v60 = (int)__rn___v60;
        if (__rT___v60 < 1) __rT___v60 = 1;
        double* __rslot___v60 = new double[__rn___v60];
        BLADE_OMP_PARALLEL_NUM_THREADS(__rT___v60)
        {
            const int __rnt = blade_omp_get_num_threads();
            const int __rt = blade_omp_get_thread_num();
            const size_t __rlo = blade_fold::chunk_lo(__rn___v60, (size_t)__rt, (size_t)__rnt);
            const size_t __rhi = blade_fold::chunk_lo(__rn___v60, (size_t)__rt + 1, (size_t)__rnt);
            for (size_t __i0 = __rlo; __i0 < __rhi; __i0++) {
                int64_t i = __i0;
                __rslot___v60[__i0] = __lambda_80(i, __v33);
            }
        }
        for (size_t __ri = 0; __ri < __rn___v60; __ri++) __v60 = __wrap_81___v60(__v60, __rslot___v60[__ri]);
        std::memcpy(R.data(), __rslot___v60, n * sizeof(double));
        haveR = true;
        delete[] __rslot___v60;
    } else if (mode == "d" || mode == "di") {
        instr = mode == "di";
        auto prod = [&](size_t i) { produce_row(s, i); };
        if (instr) __v60 = commit2<G, 64, true, true>(s.pool, n, 0.0, __v60, prod, f, kin, kout, T, R.data(), &st);
        else __v60 = commit2<G, 64, true, false>(s.pool, n, 0.0, __v60, prod, f, kin, kout, T, R.data(), &st);
        haveR = true;
    } else {
        std::cerr << "unknown mode " << mode << "\n";
        return 2;
    }
    const double __v87 = input_checksum(s);
    auto t1 = std::chrono::steady_clock::now();
    const double el = 1e-9 * (double)std::chrono::duration_cast<std::chrono::nanoseconds>(t1 - t0).count();
    cout << "pairkernel completed in " << std::setprecision(9) << el << "s" << endl << std::setprecision(15);
    cout << "mode = " << mode << " G = " << G << " T = " << T << endl;
    cout << "checksum = " << __v60 << " bits=" << std::hex << bits(__v60) << std::dec << endl;
    cout << "input_checksum = " << __v87 << endl;
    cout << "pool_fnv = " << std::hex << fnv(s.pool, n * (n + 1) / 2 * sizeof(double)) << std::dec << endl;
    if (haveR) cout << "R_fnv = " << std::hex << fnv(R.data(), n * sizeof(double)) << std::dec << endl;
    if (mode == "d" || mode == "di") cout << "actual_team = " << st.actual << " requested = " << st.requested << " ng = " << st.ng << " region_ns = " << st.wall_ns << endl;
    if (instr) {
        const double denom = (double)st.actual * st.wall_ns;
        cout << std::setprecision(6)
             << "instr: actual=" << st.actual << " ng=" << st.ng << " wall_ns=" << st.wall_ns
             << " prod_ns=" << st.prod_ns << " commit_ns=" << st.commit_ns << " wait_ns=" << st.wait_ns
             << " startup_ns=" << st.startup_ns << " drain_ns=" << st.drain_ns
             << " wait_frac_core=" << (denom > 0 ? st.wait_ns / denom : 0)
             << " max_wait_ns=" << st.max_wait_ns << " max_wait_g=" << st.max_wait_g
             << " waited=" << st.waited << " yields=" << st.yields << " handoffs=" << st.handoffs
             << " prod_med=" << st.prod_med << " prod_max=" << st.prod_max
             << " prod_first8=" << st.prod_first8 << " prod_last8=" << st.prod_last8
             << " commit_med=" << st.commit_med << " commit_max=" << st.commit_max
             << " wait_med=" << st.wait_med << endl;
    }
    return 0;
}

// ---------------------------------------------------------------------------
// The raw-bit identity matrix.
// ---------------------------------------------------------------------------
// For every extent n, consumer kernel family, commit G and team, the commit's
// R and out must equal -- by raw bits -- (1) the serial scatter at the same G,
// (2) the serial scatter at G = 4 (the emitted default: the per-chain
// sequences do not depend on G), (3) the logical walk (each row's chain over
// j ascending from its seed through the canonical cell (min, max), then the
// outer chain over i ascending); and the pool the commit's claims produced
// must equal the emitted schedule(dynamic) producer's. Families:
//   pk   the pairkernel checksum: f = x*x, builtin + both levels, seeds 0.0 / 0.0, VADD
//   nc   NON-COMMUTATIVE, nonzero / signed-zero seeds: f = x*x - 0.3,
//        ki(a, z) = 0.5*a + z, ko(a, r) = 0.75*a - r, si = -0.0, so = 0.75
//   cx   cancellation: f = (x - 0.5) * 1e16 (+ x for the low bits), builtin +, si = 0.0, so = 1.0
//   nan1 ONE NaN payload: f(x) = a fixed quiet NaN where x > 0.9 (the
//        diagonal, 1.0, among them), else x; ki = +, ko = 0.5*a + r; seeds
//        -0.0 / -0.0 -- raw bits (every NaN + NaN / NaN + x has one answer)
//   nanN MANY NaN payloads (x's low mantissa bits), else as nan1 -- compared
//        under NaN-EQUIVALENCE (raw bits on every non-NaN value): the payload
//        of NaN + NaN is whichever operand the compiler placed first, and g++
//        commutes a builtin `+` per code context, so the serial scatter at
//        G = 1 vs G = 4 vs the walk already disagree on payloads (counted and
//        printed, informational). THE NaN-PAYLOAD CONTRACT this prototype
//        states: NaN-ness and every non-NaN bit are identical; a payload is
//        identical when at most one payload can meet another in a chain.
// Teams: OMP_NUM_THREADS / OMP_THREAD_LIMIT as set by the caller, AND
// omp_set_num_threads(1 / 3 / 8) inside this process; each case repeated.
struct Fail { int count = 0; };

static double nan_payload(double x) {
    uint64_t u = bits(x) & 0x0007ffffffffffffull;
    u |= 0x7ff8000000000000ull;
    if ((u & 0x0007ffffffffffffull) == 0) u |= 1;
    double r; std::memcpy(&r, &u, 8); return r;
}

// Payload disagreements of the multi-payload NaN family (informational: the
// family is compared under NaN-equivalence): commit vs scatter (same G),
// commit vs walk, scatter G vs scatter 4, scatter vs walk; over `values`
// compared values (R and out).
struct NanPay { size_t c_vs_s = 0, c_vs_w = 0, s_vs_s4 = 0, s_vs_w = 0, values = 0; };
static NanPay nanpay;

template <int G, bool VADD, class F, class KI, class KO>
static void check_case(State& s, const char* fam, bool nanEquiv, const F& f, const KI& ki, const KO& ko, double si, double so,
                       int T, const std::vector<unsigned char>& pool_ref, Fail& fail, size_t& cases, int& team_seen) {
    const size_t n = s.n;
    const size_t cells = n * (n + 1) / 2;
    // reference 1: the serial scatter at G over the dynamic producer's pool
    std::vector<double> Rs(n), Rs4(n), Rw(n), Rc(n);
    std::memcpy(s.pool, pool_ref.data(), cells * sizeof(double));
    const double outs = scatter2_R<G, 64, VADD>(s.pool, n, si, so, f, ki, ko, Rs.data());
    const double outs4 = scatter2_R<4, 64, VADD>(s.pool, n, si, so, f, ki, ko, Rs4.data());
    // reference 3: the logical walk
    double outw = so;
    for (size_t i = 0; i < n; i++) {
        double r = si;
        for (size_t j = 0; j < n; j++) {
            const size_t a = std::min(i, j), b = std::max(i, j);
            r = ki(r, f(s.pool[blade_scatter::tri_row(n, a) + (b - a)]));
        }
        Rw[i] = r;
        outw = ko(outw, r);
    }
    // the commit, producing the pool itself (poisoned first)
    std::memset(s.pool, 0xA5, cells * sizeof(double));
    CommitStats st;
    const double outc = commit2<G, 64, VADD, false>(s.pool, n, si, so, [&](size_t i) { produce_row(s, i); }, f, ki, ko, T, Rc.data(), &st);
    team_seen = st.actual;
    cases++;
    // nanEquiv: raw bits for every non-NaN value, any NaN equal to any NaN
    // (the payload of NaN + NaN is the operand the compiler placed first, and
    // g++ commutes a builtin `+` freely, per code context: see nanpay below)
    auto eq = [&](double x, double y) { return nanEquiv && std::isnan(x) && std::isnan(y) ? true : bits(x) == bits(y); };
    auto same = [&](const std::vector<double>& x, const std::vector<double>& y) {
        for (size_t i = 0; i < x.size(); i++) if (!eq(x[i], y[i])) return false;
        return true;
    };
    if (nanEquiv) {
        auto pay = [](const std::vector<double>& x, const std::vector<double>& y) {
            size_t d = 0; for (size_t i = 0; i < x.size(); i++) if (bits(x[i]) != bits(y[i])) d++; return d; };
        nanpay.c_vs_s += pay(Rc, Rs) + (bits(outc) != bits(outs));
        nanpay.c_vs_w += pay(Rc, Rw) + (bits(outc) != bits(outw));
        nanpay.s_vs_s4 += pay(Rs, Rs4) + (bits(outs) != bits(outs4));
        nanpay.s_vs_w += pay(Rs, Rw) + (bits(outs) != bits(outw));
        nanpay.values += n + 1;
    }
    const bool okPool = std::memcmp(s.pool, pool_ref.data(), cells * sizeof(double)) == 0;
    const bool ok = okPool && same(Rc, Rs) && same(Rc, Rs4) && same(Rc, Rw)
        && eq(outc, outs) && eq(outc, outs4) && eq(outc, outw);
    if (!ok) {
        fail.count++;
        std::cout << "MISMATCH fam=" << fam << " n=" << n << " G=" << G << " T=" << T << " actual=" << st.actual
                  << " pool=" << okPool << " R_vs_scatter=" << same(Rc, Rs) << " R_vs_scatter4=" << same(Rc, Rs4)
                  << " R_vs_walk=" << same(Rc, Rw) << std::hex << " out_c=" << bits(outc) << " out_s=" << bits(outs)
                  << " out_s4=" << bits(outs4) << " out_w=" << bits(outw) << std::dec << "\n";
    }
}

template <int G>
static void check_family_set(State& s, int T, const std::vector<unsigned char>& pool_ref, Fail& fail, size_t& cases, int& team) {
    auto add = [](double a, double b) -> double { return a + b; };
    check_case<G, true>(s, "pk", false, [](double x) -> double { return x * x; }, add, add, 0.0, 0.0, T, pool_ref, fail, cases, team);
    check_case<G, false>(s, "nc", false, [](double x) -> double { return x * x - 0.3; },
                         [](double a, double z) -> double { return 0.5 * a + z; },
                         [](double a, double r) -> double { return 0.75 * a - r; }, -0.0, 0.75, T, pool_ref, fail, cases, team);
    check_case<G, true>(s, "cx", false, [](double x) -> double { return (x - 0.5) * 1e16 + x; }, add, add, 0.0, 1.0, T, pool_ref, fail, cases, team);
    // ONE payload: NaN + NaN and NaN + finite have one answer whatever the operand order -> raw bits
    check_case<G, false>(s, "nan1", false, [](double x) -> double { return x > 0.9 ? nan_payload(0.3) : x; }, add,
                         [](double a, double r) -> double { return 0.5 * a + r; }, -0.0, -0.0, T, pool_ref, fail, cases, team);
    // MANY payloads: compared under NaN-equivalence, payload disagreements counted (nanpay)
    check_case<G, false>(s, "nanN", true, [](double x) -> double { return x > 0.9 ? nan_payload(x) : x; }, add,
                         [](double a, double r) -> double { return 0.5 * a + r; }, -0.0, -0.0, T, pool_ref, fail, cases, team);
}

static int identity_main(int reps) {
    const size_t ns[] = {1, 2, 3, 4, 5, 7, 8, 9, 13, 23, 101, 2503};
    const int teams[] = {1, 3, 8};
    Fail fail;
    size_t cases = 0;
    std::cout << "identity: OMP_NUM_THREADS=" << (getenv("OMP_NUM_THREADS") ? getenv("OMP_NUM_THREADS") : "(unset)")
              << " OMP_THREAD_LIMIT=" << (getenv("OMP_THREAD_LIMIT") ? getenv("OMP_THREAD_LIMIT") : "(unset)") << "\n";
    for (size_t n : ns) {
        State s;
        alloc_state(s, n);
        make_pts(s);
        produce_dynamic(s);
        const size_t cells = n * (n + 1) / 2;
        std::vector<unsigned char> pool_ref(cells * sizeof(double));
        std::memcpy(pool_ref.data(), s.pool, pool_ref.size());
        for (int T : teams) {
            int seen1 = 0, seen4 = 0, seen8 = 0;
            const int r = n == 2503 ? reps : reps * 4;
            for (int k = 0; k < r; k++) {
                check_family_set<1>(s, T, pool_ref, fail, cases, seen1);
                check_family_set<4>(s, T, pool_ref, fail, cases, seen4);
                check_family_set<8>(s, T, pool_ref, fail, cases, seen8);
            }
            std::cout << "  n=" << n << " T=" << T << " actual team (G=1/4/8): " << seen1 << "/" << seen4 << "/" << seen8 << "\n";
        }
    }
    std::cout << "identity: " << cases << " cases, " << fail.count << " mismatches\n";
    std::cout << "nanN payload disagreements (informational, of " << nanpay.values << " values): commit vs scatter(G) "
              << nanpay.c_vs_s << ", commit vs walk " << nanpay.c_vs_w << ", scatter(G) vs scatter(4) " << nanpay.s_vs_s4
              << ", scatter(G) vs walk " << nanpay.s_vs_w << "\n";
    return fail.count == 0 ? 0 : 1;
}

#ifdef _WIN32
extern "C" __declspec(dllimport) void* __stdcall GetCurrentProcess(void);
extern "C" __declspec(dllimport) int __stdcall SetProcessAffinityMask(void*, unsigned long long);
#endif

int main(int argc, char** argv) {
    // P1_AFFINITY=<mask>: pin the process before anything runs (the harness
    // pins its serial arms to CPU 2: config.json serial_affinity_cpu)
#ifdef _WIN32
    if (const char* a = std::getenv("P1_AFFINITY")) SetProcessAffinityMask(GetCurrentProcess(), std::strtoull(a, nullptr, 0));
#endif
    try {
        const std::string mode = argc > 1 ? argv[1] : "d";
        if (mode == "id") return identity_main(argc > 2 ? std::atoi(argv[2]) : 3);
        const int G = argc > 2 ? std::atoi(argv[2]) : 4;
        switch (G) {
            case 1: return timed_main<1>(mode);
            case 4: return timed_main<4>(mode);
            case 8: return timed_main<8>(mode);
            default: std::cerr << "G must be 1, 4 or 8\n"; return 2;
        }
    } catch (const std::exception& e) { blade_rt::panic_exception(e); }
      catch (...) { blade_rt::panic("BL8005", "unknown exception", nullptr, 0); }
}
