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
//   NO PRODUCT IS EVER FUSED INTO ITS ADD, in any build: each step is
//   `p = e(i)` ROUNDED, then `acc + p` (BLADE_FOLD_ROUND, an empty asm barrier
//   on p -- no instruction, it only makes p opaque so no FMA can absorb it).
//   That is exactly the arithmetic of the same fold over a MATERIALIZED
//   product (`reduce(a * b, (+))` once `a * b` is stored, the shape a generic
//   function instance lowers to) and of the repro chain under
//   -ffp-contract=off, so the tree's bits are one function of (data, T, K, n)
//   under every BLADE_FP_CONTRACT mode. Two earlier spellings failed that:
//   the bare `acc + x * y` (g++ 16 fused the block loop but not the vector
//   remainder or the scalar tail, and inlined copies differently per call
//   site), and an explicit fma per step under the contracting build (a
//   generic instance folding its stored `a * b` then disagreed with the same
//   spelling at top level: corpus functions/168, Float32 n = 3).
//   For n < W there is no vector at all and the result IS the serial chain
//   from +0 of rounded products -- for n >= 1 the plain left fold
//   ((p0 + p1) + p2) ..., bit for bit.
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

// BLADE_FOLD_ROUND(v): force `v` (a scalar or a 32-byte vector) to be a
// finished, rounded value, so no FMA can absorb the multiply that produced it.
// Only needed where the target HAS fused multiply-add (`__FMA__`, which also
// implies the AVX registers the "x" constraint names for a 32-byte vector);
// without FMA nothing can fuse and the barrier is a no-op. GCC and clang only
// (MSVC does not contract by default).
#if (defined(__GNUC__) || defined(__clang__)) && defined(__FMA__) && (defined(__x86_64__) || defined(__i386__))
#define BLADE_FOLD_ROUND(v) __asm__("" : "+x"(v))
#else
#define BLADE_FOLD_ROUND(v) ((void)0)
#endif

// One step `acc + x * y` with the product rounded first (see the contract).
#define BLADE_FOLD_MADD(x, y, acc) (::blade_fold::rmadd((x), (y), (acc)))

namespace blade_fold {

template <typename T>
__attribute__((always_inline)) inline T rmadd(T x, T y, T acc) {
    T p = x * y;
    BLADE_FOLD_ROUND(p);
    return acc + p;
}

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
// (BLADE_FOLD_MADD: the rounded product, then the add); the skeleton itself
// never multiplies.
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

// The STREAMING form of the same tree (P1; see "STREAMING STATE" below): the
// B chains live in a state that whole blocks are fed into, then one finish.
template <typename T, int K> struct lane_state { T acc[K * width<T>::value]; };

template <typename T, int K>
inline lane_state<T, K> lane_init() {
    lane_state<T, K> s;
    for (int c = 0; c < K * width<T>::value; c++) s.acc[c] = T(0);
    return s;
}

template <typename T, int K, typename Step>
inline void feed(lane_state<T, K>& s, size_t nblocks, const Step& step) {
    constexpr int B = K * width<T>::value;
    for (size_t b = 0; b < nblocks; b++)
        for (int c = 0; c < B; c++) s.acc[c] = step(s.acc[c], b * (size_t)B + c);
}

template <typename T, int K, typename Step>
inline T finish(const lane_state<T, K>& s0, size_t rem, const Step& step) {
    constexpr int W = width<T>::value;
    constexpr int B = K * W;
    lane_state<T, K> s = s0;
    size_t i = 0;
    for (; rem - i >= (size_t)B; i += B)
        for (int c = 0; c < B; c++) s.acc[c] = step(s.acc[c], i + c);
    for (; rem - i >= (size_t)W; i += W)
        for (int w = 0; w < W; w++) s.acc[w] = step(s.acc[w], i + w);
    for (int k = 1; k < K; k++)
        for (int w = 0; w < W; w++) s.acc[w] = s.acc[w] + s.acc[k * W + w];
    T r = s.acc[0];
    for (int w = 1; w < W; w++) r = r + s.acc[w];
    for (; i < rem; i++) r = step(r, i);
    return r;
}

template <typename T, int K>
inline void feed_sq(lane_state<T, K>& s, const T* p, size_t nblocks) {
    feed<T, K>(s, nblocks, [&](T acc, size_t i) { const T x = p[i]; return BLADE_FOLD_MADD(x, x, acc); });
}
template <typename T, int K>
inline T finish_sq(const lane_state<T, K>& s, const T* tail, size_t rem) {
    return finish<T, K>(s, rem, [&](T acc, size_t i) { const T x = tail[i]; return BLADE_FOLD_MADD(x, x, acc); });
}
template <typename T, int K>
inline void feed_sum(lane_state<T, K>& s, const T* p, size_t nblocks) {
    feed<T, K>(s, nblocks, [&](T acc, size_t i) { return acc + p[i]; });
}
template <typename T, int K>
inline T finish_sum(const lane_state<T, K>& s, const T* tail, size_t rem) {
    return finish<T, K>(s, rem, [&](T acc, size_t i) { return acc + tail[i]; });
}

} // namespace model

// ---------------------------------------------------------------------------
// STREAMING STATE (P1, round-2 cell epilogues). The SAME tree as `sq` / `sum`,
// split so a producer can feed it right behind its own stores:
//
//   lane_state<T, K> s = lane_init<T, K>();
//   feed_sq(s, p, nb);              // consumes nb WHOLE blocks of B = K * W
//   ...                             // (any number of feeds, ascending, contiguous)
//   T r = finish_sq(s, tail, rem);  // the last rem (ANY size) elements
//
// INVARIANT (raw-bit tested in cpp/fold_lanes_tests.cpp against `model::` and
// against the one-shot form): for a contiguous sweep a[0 .. n) with
// n = nb * B + rem,
//      finish_sq(feed_sq(s0, a, nb), a + nb * B, rem) == sq<T, K>(a, n)
// bit for bit, for ANY rem (not only rem < B), under every contraction mode.
// Why: the one-shot tree hands every WHOLE block of B to the K accumulators in
// order, then every remaining whole VECTOR to v0, then combines. A feed is
// exactly some prefix of those whole-block steps; finish FIRST runs the
// residual floor(rem / B) whole blocks (n = 289 after a 256-element feed: 33
// remain = one 32-block + 1, and the block must go to all K accumulators, not
// to v0 as vectors), then the whole-vector remainder through v0, then the
// ascending accumulator combine, the ascending lane combine and the < W scalar
// tail -- the same steps in the same order. Products are rounded
// (BLADE_FOLD_ROUND) exactly as in `sq`, so no step can fuse.
//
// B is ELEMENT-WIDTH DEPENDENT: 32 for double with K = 8, 64 for float with
// K = 8 (16 / 32 with K = 4). A producer that feeds in blocks of E elements
// must have E % lane_block<T, K>::value == 0 (the emitter checks it; feeding a
// partial block is a contract violation the API cannot express: nblocks counts
// whole blocks).
// ---------------------------------------------------------------------------
template <typename T, int K> struct lane_block { static constexpr size_t value = (size_t)K * (32 / sizeof(T)); };

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

/// One lane-wise step `acc + x * y`, the product rounded first (the contract):
/// vmulp* then vaddp*, never a vfmadd, whatever -ffp-contract says.
template <typename V>
__attribute__((always_inline)) inline V madd(V x, V y, V acc) {
    V p = x * y;
    BLADE_FOLD_ROUND(p);
    return acc + p;
}

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

/// sum_t (a[t] * b[t]) * c[t], source factor order: both products rounded,
/// the second by madd before its add, under every contraction mode
template <typename T, int K>
BLADE_FOLD_NOINLINE T dot3(const T* a, const T* b, const T* c, size_t n) {
    typedef typename detail::vec<T>::type V;
    return detail::fold<T, K>(n,
        [&](V acc, size_t i) { return detail::madd(detail::load<T>(a + i) * detail::load<T>(b + i), detail::load<T>(c + i), acc); },
        [&](T acc, size_t i) { return BLADE_FOLD_MADD(a[i] * b[i], c[i], acc); });
}

// --- streaming state (P1; contract in the STREAMING STATE block above) ------
// K NAMED vector accumulators (v4..v7 stay zero and unused when K = 4). The
// feeds are always_inline (they sit in the producer's block loop); finish is
// out of line like the one-shot forms.
template <typename T, int K>
struct lane_state {
    typedef typename detail::vec<T>::type V;
    V v0, v1, v2, v3, v4, v5, v6, v7;
};

template <typename T, int K>
__attribute__((always_inline)) inline lane_state<T, K> lane_init() {
    static_assert(K == 4 || K == 8, "blade_fold: K is 4 or 8");
    lane_state<T, K> s;
    s.v0 = detail::zero<T>();
    s.v1 = s.v0; s.v2 = s.v0; s.v3 = s.v0; s.v4 = s.v0; s.v5 = s.v0; s.v6 = s.v0; s.v7 = s.v0;
    return s;
}

namespace detail {

/// nb whole blocks of B = K * W into the K accumulators, in order -- the
/// one-shot skeleton's block loop, verbatim.
template <typename T, int K, typename VStep>
__attribute__((always_inline)) inline void feed_blocks(lane_state<T, K>& s, size_t nb, const VStep& vstep) {
    typedef typename vec<T>::type V;
    constexpr size_t W = 32 / sizeof(T);
    constexpr size_t B = (size_t)K * W;
    V v0 = s.v0, v1 = s.v1, v2 = s.v2, v3 = s.v3;
    V v4 = s.v4, v5 = s.v5, v6 = s.v6, v7 = s.v7;
    size_t i = 0;
    if (K == 8) {
        for (size_t b = 0; b < nb; b++, i += B) {
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
        for (size_t b = 0; b < nb; b++, i += B) {
            v0 = vstep(v0, i);
            v1 = vstep(v1, i + W);
            v2 = vstep(v2, i + 2 * W);
            v3 = vstep(v3, i + 3 * W);
        }
    }
    s.v0 = v0; s.v1 = v1; s.v2 = v2; s.v3 = v3;
    s.v4 = v4; s.v5 = v5; s.v6 = v6; s.v7 = v7;
}

/// The last rem elements (ANY rem): residual whole blocks first, then whole
/// vectors through v0, the ascending combines, the scalar tail -- the rest of
/// the one-shot skeleton, verbatim.
template <typename T, int K, typename VStep, typename SStep>
__attribute__((always_inline)) inline T finish_blocks(const lane_state<T, K>& s0, size_t rem, const VStep& vstep, const SStep& sstep) {
    typedef typename vec<T>::type V;
    constexpr size_t W = 32 / sizeof(T);
    constexpr size_t B = (size_t)K * W;
    lane_state<T, K> s = s0;
    feed_blocks<T, K>(s, rem / B, vstep);
    size_t i = (rem / B) * B;
    V v0 = s.v0;
    for (; rem - i >= W; i += W) v0 = vstep(v0, i);
    v0 = v0 + s.v1;
    v0 = v0 + s.v2;
    v0 = v0 + s.v3;
    if (K == 8) {
        v0 = v0 + s.v4;
        v0 = v0 + s.v5;
        v0 = v0 + s.v6;
        v0 = v0 + s.v7;
    }
    T lanes[W];
    std::memcpy(lanes, &v0, 32);
    T r = lanes[0];
    for (size_t w = 1; w < W; w++) r = r + lanes[w];
    for (; i < rem; i++) r = sstep(r, i);
    return r;
}

} // namespace detail

/// Feed nb whole blocks (nb * lane_block<T, K>::value elements from p) of a
/// self-dot into s.
template <typename T, int K>
__attribute__((always_inline)) inline void feed_sq(lane_state<T, K>& s, const T* p, size_t nblocks) {
    typedef typename detail::vec<T>::type V;
    detail::feed_blocks<T, K>(s, nblocks,
        [&](V acc, size_t i) { const V x = detail::load<T>(p + i); return detail::madd(x, x, acc); });
}

/// The self-dot's value after the last rem elements (from tail).
template <typename T, int K>
BLADE_FOLD_NOINLINE T finish_sq(const lane_state<T, K>& s, const T* tail, size_t rem) {
    typedef typename detail::vec<T>::type V;
    return detail::finish_blocks<T, K>(s, rem,
        [&](V acc, size_t i) { const V x = detail::load<T>(tail + i); return detail::madd(x, x, acc); },
        [&](T acc, size_t i) { const T x = tail[i]; return BLADE_FOLD_MADD(x, x, acc); });
}

/// Feed nb whole blocks of a plain sum into s.
template <typename T, int K>
__attribute__((always_inline)) inline void feed_sum(lane_state<T, K>& s, const T* p, size_t nblocks) {
    typedef typename detail::vec<T>::type V;
    detail::feed_blocks<T, K>(s, nblocks,
        [&](V acc, size_t i) { return acc + detail::load<T>(p + i); });
}

/// The plain sum's value after the last rem elements (from tail).
template <typename T, int K>
BLADE_FOLD_NOINLINE T finish_sum(const lane_state<T, K>& s, const T* tail, size_t rem) {
    typedef typename detail::vec<T>::type V;
    return detail::finish_blocks<T, K>(s, rem,
        [&](V acc, size_t i) { return acc + detail::load<T>(tail + i); },
        [&](T acc, size_t i) { return acc + tail[i]; });
}

#else  // no vector extensions (MSVC): the model IS the implementation

template <typename T, int K> inline T sum(const T* a, size_t n) { return model::sum<T, K>(a, n); }
template <typename T, int K> inline T dot(const T* a, const T* b, size_t n) { return model::dot<T, K>(a, b, n); }
template <typename T, int K> inline T sq(const T* a, size_t n) { return model::sq<T, K>(a, n); }
template <typename T, int K> inline T dot3(const T* a, const T* b, const T* c, size_t n) { return model::dot3<T, K>(a, b, c, n); }
template <typename T, int K> using lane_state = model::lane_state<T, K>;
template <typename T, int K> inline lane_state<T, K> lane_init() { return model::lane_init<T, K>(); }
template <typename T, int K> inline void feed_sq(lane_state<T, K>& s, const T* p, size_t nb) { model::feed_sq<T, K>(s, p, nb); }
template <typename T, int K> inline T finish_sq(const lane_state<T, K>& s, const T* t, size_t rem) { return model::finish_sq<T, K>(s, t, rem); }
template <typename T, int K> inline void feed_sum(lane_state<T, K>& s, const T* p, size_t nb) { model::feed_sum<T, K>(s, p, nb); }
template <typename T, int K> inline T finish_sum(const lane_state<T, K>& s, const T* t, size_t rem) { return model::finish_sum<T, K>(s, t, rem); }

#endif

} // namespace blade_fold

#endif // BLADE_FOLD_LANES_HPP
