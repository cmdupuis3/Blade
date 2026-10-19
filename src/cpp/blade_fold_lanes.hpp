// blade_fold_lanes.hpp -- the EXPLICIT K-lane VECTOR form of a licensed
// dot-class fold (bench/notes/microkernel-sketches-review.md items M9 and M14;
// gap register G25 / G26), and the ONE chunk-partition function the threaded
// (Path B) fold and its serial-within-chunk twin both spell.
//
// WHAT IT REPLACES. The C++ lane used to spend a dot-class fold's reorder
// licence as `#pragma omp simd reduction(+:acc)` over ONE accumulator. GCC
// vectorizes that to one ymm accumulator and no interleave, so the loop-carried
// `vaddpd` (3-4 cycles) caps the fold at 4 lanes per 3 cycles whatever the
// FMA units could do -- the 1.33 lanes/cycle of G26. The forms below carry K
// independent vector accumulators (K = 4 or 8, 32-byte vectors), which is the
// shape the LLVM lane's vectorizer already picks for the same fold.
//
// THE ARITHMETIC CONTRACT (fixed; the value is a function of the data, the
// element type, K, n and the build's contraction mode ALONE -- never of the
// compiler's own fusion choices, the vector ISA, or the thread count):
//
//   W = 32 / sizeof(T) lanes per vector (4 for double, 8 for float); B = K * W.
//   Chain c (0 <= c < B) starts at +0 and owns elements c, c + B, c + 2B, ...
//   while a WHOLE block of B elements remains:        acc[c] = acc[c] + e(i)
//   Then while a whole VECTOR of W elements remains, lanes 0..W-1 (chains
//   0..W-1, accumulator 0) take the next W elements the same way.
//   Then the accumulators fold into accumulator 0 in ASCENDING order, lane by
//   lane:   v0 = v0 + v1; v0 = v0 + v2; ... v0 = v0 + v(K-1)
//   Then the lanes of accumulator 0 fold ascending into a scalar:
//           r = v0[0]; r = r + v0[1]; ... r = r + v0[W-1]
//   Then the remaining (< W) elements extend that scalar chain ascending:
//           r = r + e(i)
//   where e(i) is the element term: a[i] * b[i] (dot), a[i] * a[i] (self-dot,
//   ONE load per element), (a[i] * b[i]) * c[i] (three streams, source factor
//   order), or a[i] (a plain sum).
//   CONTRACTION is decided by the BUILD, not by the optimizer's context: under
//   a contracting build (Build.fs passes -DBLADE_FP_CONTRACT_FAST=1 whenever
//   BLADE_FP_CONTRACT is not `off`, i.e. by default) every `acc + x * y` step
//   is ONE fused multiply-add (std::fma / the packed FMA intrinsic; dot3's
//   first product is rounded, its second fused with the add); under =off (the
//   differential gates' mode) every step is the plain `acc + x * y`, two
//   roundings, and no FMA instruction exists in the build. Written as the
//   bare expression, g++ 16 fused the main block loop and left the vector
//   remainder and the scalar tail UNFUSED (vmulpd + vaddpd) -- "contract as
//   the build allows" was not one tree -- hence the explicit step.
//   For n < W there is no vector at all and the result IS the serial chain
//   from +0 under the same step rule, bit for bit.
//
// The scalar model of that tree is `blade_fold::model::` (B scalar chains, the
// same combine order, the same step rule); the vector forms are
// `blade_fold::`. On a compiler without GCC vector extensions `blade_fold::` IS
// the model. The raw-bit tests (cpp/fold_lanes_tests.cpp, `blade test
// fold-lanes`) compare the two under both contraction modes.
//
// LICENCE. These forms REASSOCIATE the fold: the emitter calls them only on a
// licensed fold (`foldReorderLicensed`, i.e. a builtin `+` body or declared
// comm) and never under BLADE_FP_REPRO=1, which keeps the serial ascending
// chain at every site.
//
// THREADS. `chunk_lo` is the Path B partition: thread t of nt owns
// [chunk_lo(cnt, t, nt), chunk_lo(cnt, t + 1, nt)) of a cnt-element sweep. Both
// the threaded fold and its serial twin (the raw-bit test's model of the
// threaded fold) call this one function, so the chunk boundaries -- and with
// them the per-chunk lane trees and the fixed job-order combine -- are
// identical by construction. No OpenMP in this header: the team is the
// emitter's.
#ifndef BLADE_FOLD_LANES_HPP
#define BLADE_FOLD_LANES_HPP

#include <cstddef>
#include <cstring>
#include <cmath>

// The contracted build's step. BLADE_FP_CONTRACT_FAST comes from Build.fs
// (every mode but `off`); the packed intrinsic additionally needs the target
// to have FMA (`__FMA__`, i.e. -march=native on any FMA-capable x86). Without
// it std::fma is a libm call per step -- correct and slow, reachable only by a
// contracting build without -march, which Blade never produces.
#if defined(BLADE_FP_CONTRACT_FAST) && BLADE_FP_CONTRACT_FAST
#define BLADE_FOLD_FUSED 1
#define BLADE_FOLD_MADD(x, y, acc) std::fma((x), (y), (acc))
#else
#define BLADE_FOLD_FUSED 0
#define BLADE_FOLD_MADD(x, y, acc) ((acc) + (x) * (y))
#endif

namespace blade_fold {

/// Thread t's chunk start in a cnt-element sweep split over nt threads:
/// (cnt * t) / nt. Thread t's chunk is [chunk_lo(cnt, t, nt), chunk_lo(cnt,
/// t + 1, nt)); the chunks tile [0, cnt) exactly, differ in size by at most
/// one, and depend on (cnt, nt) alone.
inline size_t chunk_lo(size_t cnt, size_t t, size_t nt) {
    return (cnt * t) / nt;
}

// ---------------------------------------------------------------------------
// The scalar model: B = K * W named chains, as the contract above spells them.
// Always compiled (it is the reference the raw-bit tests compare against, and
// the implementation where no vector extension exists).
// ---------------------------------------------------------------------------
namespace model {

template <typename T> struct width { static constexpr int value = (int)(32 / sizeof(T)); };

// `step(acc, i)` returns the chain's next value from its current one
// (BLADE_FOLD_MADD: the plain `acc + x * y` or one std::fma, by build); the
// skeleton itself never multiplies.
template <typename T, int K, typename Step>
inline T fold(size_t n, const Step& step) {
    constexpr int W = width<T>::value;
    constexpr int B = K * W;
    T acc[B];
    for (int c = 0; c < B; c++) acc[c] = T(0);
    size_t i = 0;
    for (; n - i >= (size_t)B; i += B) {
        for (int c = 0; c < B; c++) acc[c] = step(acc[c], i + c);
    }
    for (; n - i >= (size_t)W; i += W) {
        for (int w = 0; w < W; w++) acc[w] = step(acc[w], i + w);
    }
    for (int k = 1; k < K; k++) {
        for (int w = 0; w < W; w++) acc[w] = acc[w] + acc[k * W + w];
    }
    T r = acc[0];
    for (int w = 1; w < W; w++) r = r + acc[w];
    for (; i < n; i++) r = step(r, i);
    return r;
}

template <typename T, int K>
inline T sum(const T* a, size_t n) {
    return fold<T, K>(n, [&](T acc, size_t i) { return acc + a[i]; });
}
template <typename T, int K>
inline T dot(const T* a, const T* b, size_t n) {
    return fold<T, K>(n, [&](T acc, size_t i) { return BLADE_FOLD_MADD(a[i], b[i], acc); });
}
template <typename T, int K>
inline T sq(const T* a, size_t n) {
    return fold<T, K>(n, [&](T acc, size_t i) { const T x = a[i]; return BLADE_FOLD_MADD(x, x, acc); });
}
template <typename T, int K>
inline T dot3(const T* a, const T* b, const T* c, size_t n) {
    return fold<T, K>(n, [&](T acc, size_t i) { return BLADE_FOLD_MADD(a[i] * b[i], c[i], acc); });
}

} // namespace model

// ---------------------------------------------------------------------------
// The vector forms (GCC / clang vector extensions). Unaligned loads through
// memcpy (the portable spelling; it compiles to one vmovupd), 32-byte vectors
// whatever -march says, so the association is fixed across ISAs.
// ---------------------------------------------------------------------------
#if defined(__GNUC__) || defined(__clang__)
#define BLADE_FOLD_LANES_VECTOR 1
// NOT inlined into the call site, on purpose: out of line, the tree is
// compiled once per (T, K) and its arithmetic cannot be reshaped by the
// code around a call (the optimizer SLP-packs an inlined tail's products
// away from their adds when a sibling loop invites it), so the bits are a
// function of (data, T, K, n, build mode) alone -- which is what lets the
// raw-bit test compare it with a model at all. One call per fold is noise
// against a 100+-element sweep.
#define BLADE_FOLD_NOINLINE __attribute__((noinline))
#if BLADE_FOLD_FUSED && defined(__FMA__)
#include <immintrin.h>
#endif

namespace detail {

template <typename T> struct vec;
template <> struct vec<double> { typedef double type __attribute__((vector_size(32))); };
template <> struct vec<float>  { typedef float  type __attribute__((vector_size(32))); };

template <typename T>
__attribute__((always_inline)) inline typename vec<T>::type load(const T* p) {
    typename vec<T>::type v;
    std::memcpy(&v, p, 32);
    return v;
}

template <typename T>
__attribute__((always_inline)) inline typename vec<T>::type zero() {
    typename vec<T>::type v;
    const T z[32 / sizeof(T)] = {};
    std::memcpy(&v, z, 32);
    return v;
}

/// One lane-wise step `acc + x * y`: the packed FMA under the contracted build
/// (the intrinsic, so the remainder loop fuses exactly as the block loop
/// does), the plain product-then-add otherwise.
#if BLADE_FOLD_FUSED && defined(__FMA__)
__attribute__((always_inline)) inline vec<double>::type madd(vec<double>::type x, vec<double>::type y, vec<double>::type acc) {
    return (vec<double>::type)_mm256_fmadd_pd((__m256d)x, (__m256d)y, (__m256d)acc);
}
__attribute__((always_inline)) inline vec<float>::type madd(vec<float>::type x, vec<float>::type y, vec<float>::type acc) {
    return (vec<float>::type)_mm256_fmadd_ps((__m256)x, (__m256)y, (__m256)acc);
}
#else
template <typename V>
__attribute__((always_inline)) inline V madd(V x, V y, V acc) { return acc + x * y; }
#endif

/// The lane-tree skeleton. `vstep(acc, i)` is one vector step (acc + e(i ..
/// i + W - 1) as the build contracts it), `sstep(acc, i)` the scalar step for
/// the tail. K NAMED vector accumulators, never an array indexed in the hot
/// loop: an indexed array can defeat register allocation and land every
/// update in memory.
template <typename T, int K, typename VStep, typename SStep>
__attribute__((always_inline)) inline T fold(size_t n, const VStep& vstep, const SStep& sstep) {
    typedef typename vec<T>::type V;
    constexpr size_t W = 32 / sizeof(T);
    constexpr size_t B = (size_t)K * W;
    static_assert(K == 4 || K == 8, "blade_fold: K is 4 or 8");
    V v0 = zero<T>(), v1 = v0, v2 = v0, v3 = v0;
    V v4 = v0, v5 = v0, v6 = v0, v7 = v0;
    size_t i = 0;
    if (K == 8) {
        for (; n - i >= B; i += B) {
            v0 = vstep(v0, i);
            v1 = vstep(v1, i + W);
            v2 = vstep(v2, i + 2 * W);
            v3 = vstep(v3, i + 3 * W);
            v4 = vstep(v4, i + 4 * W);
            v5 = vstep(v5, i + 5 * W);
            v6 = vstep(v6, i + 6 * W);
            v7 = vstep(v7, i + 7 * W);
        }
    } else {
        for (; n - i >= B; i += B) {
            v0 = vstep(v0, i);
            v1 = vstep(v1, i + W);
            v2 = vstep(v2, i + 2 * W);
            v3 = vstep(v3, i + 3 * W);
        }
    }
    for (; n - i >= W; i += W) v0 = vstep(v0, i);
    v0 = v0 + v1;
    v0 = v0 + v2;
    v0 = v0 + v3;
    if (K == 8) {
        v0 = v0 + v4;
        v0 = v0 + v5;
        v0 = v0 + v6;
        v0 = v0 + v7;
    }
    T lanes[W];
    std::memcpy(lanes, &v0, 32);
    T r = lanes[0];
    for (size_t w = 1; w < W; w++) r = r + lanes[w];
    for (; i < n; i++) r = sstep(r, i);
    return r;
}

} // namespace detail

/// sum_t a[t]
template <typename T, int K>
BLADE_FOLD_NOINLINE T sum(const T* a, size_t n) {
    typedef typename detail::vec<T>::type V;
    return detail::fold<T, K>(n,
        [&](V acc, size_t i) { return acc + detail::load<T>(a + i); },
        [&](T acc, size_t i) { return acc + a[i]; });
}

/// sum_t a[t] * b[t] (two streams; `a == b` is legal and reads twice -- the
/// emitter calls `sq` for a self-dot it can see)
template <typename T, int K>
BLADE_FOLD_NOINLINE T dot(const T* a, const T* b, size_t n) {
    typedef typename detail::vec<T>::type V;
    return detail::fold<T, K>(n,
        [&](V acc, size_t i) { return detail::madd(detail::load<T>(a + i), detail::load<T>(b + i), acc); },
        [&](T acc, size_t i) { return BLADE_FOLD_MADD(a[i], b[i], acc); });
}

/// sum_t a[t] * a[t], one load per element
template <typename T, int K>
BLADE_FOLD_NOINLINE T sq(const T* a, size_t n) {
    typedef typename detail::vec<T>::type V;
    return detail::fold<T, K>(n,
        [&](V acc, size_t i) { const V x = detail::load<T>(a + i); return detail::madd(x, x, acc); },
        [&](T acc, size_t i) { const T x = a[i]; return BLADE_FOLD_MADD(x, x, acc); });
}

/// sum_t (a[t] * b[t]) * c[t], source factor order: the first product rounded,
/// the second fused with the add under the contracted build
template <typename T, int K>
BLADE_FOLD_NOINLINE T dot3(const T* a, const T* b, const T* c, size_t n) {
    typedef typename detail::vec<T>::type V;
    return detail::fold<T, K>(n,
        [&](V acc, size_t i) { return detail::madd(detail::load<T>(a + i) * detail::load<T>(b + i), detail::load<T>(c + i), acc); },
        [&](T acc, size_t i) { return BLADE_FOLD_MADD(a[i] * b[i], c[i], acc); });
}

#else  // no vector extensions (MSVC): the model IS the implementation

template <typename T, int K> inline T sum(const T* a, size_t n) { return model::sum<T, K>(a, n); }
template <typename T, int K> inline T dot(const T* a, const T* b, size_t n) { return model::dot<T, K>(a, b, n); }
template <typename T, int K> inline T sq(const T* a, size_t n) { return model::sq<T, K>(a, n); }
template <typename T, int K> inline T dot3(const T* a, const T* b, const T* c, size_t n) { return model::dot3<T, K>(a, b, c, n); }

#endif

} // namespace blade_fold

#endif // BLADE_FOLD_LANES_HPP
