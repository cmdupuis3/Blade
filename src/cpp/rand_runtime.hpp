// Blade `rand` module runtime -- deterministic, cross-compiler-stable RNG.
//
// The `rand` module cannot be expressed in Blade source (the language has no
// unsigned integers and no bitwise operators), so the compiler emits calls into
// this header. std::mt19937_64 supplies the raw 64-bit stream (bit-exact per the
// C++ standard, identical across libstdc++/libc++/MSVC); the [0,1) mapping and
// the normal transform are implemented HERE rather than via std::uniform_real_
// distribution / std::normal_distribution (both implementation-defined), so a
// corpus EXPECT pinned once stays valid on any toolchain.
//
// API surface (called from generated main()):
//   blade_rand::uniform    (double* out, size_t n, int64_t key)                  -- U[0,1)
//   blade_rand::normal     (double* out, size_t n, int64_t key)                  -- N(0,1)
//   blade_rand::exponential(double* out, size_t n, int64_t key, double rate)     -- Exp(rate)
//   blade_rand::gamma      (double* out, size_t n, int64_t key, double sh, double rt)
//   blade_rand::poisson    (double* out, size_t n, int64_t key, double lam)
//   blade_rand::bernoulli  (double* out, size_t n, int64_t key, double p)
//   blade_rand::beta       (double* out, size_t n, int64_t key, double a, double b)
//   blade_rand::categorical(int64_t* out, size_t n, int64_t key, const double* w, size_t k)
//
// ELEMENT TYPE. Every fill EXCEPT `categorical` writes `double`, including the
// two integer-valued families (poisson counts, bernoulli 0/1). That is
// deliberate: one `double*` out-pointer contract keeps the codegen seam
// (genRandGenBinding allocates a dense Float64 pool and hands over pool_base)
// and the interpreter mirror (RandMirror's `float[]`) uniform across those
// families, at the cost of an exactly-representable integer round-trip. Counts
// below 2^53 are exact, so nothing is lost numerically there.
//
// `categorical` is the exception, and writes `int64_t`. Its output is not a
// measurement that happens to be integral -- it is a SUBSCRIPT, and the whole
// point of drawing it is to index the array the weights came from. A Float64
// index would need a coercion the rand surface does not have, so this family
// carries its own out-pointer type. The seam that made this cheap is that
// codegen was already element-type-generic: genRandGenBinding allocates
// `Array<elemTypeToCpp(ElemType), rank>`, so the checker choosing
// `IRTScalar ETInt64` for this one family is what selects an `int64_t` pool,
// and the fill signature follows. The interpreter mirror correspondingly
// returns `int64[]` into an SInt store rather than `float[]`/SFloat.
//
// PARAMETERS are runtime doubles: the Blade surface accepts any Float64-typed
// expression for rate/shape/lam/p/a/b (only the SHAPE must be static). They are
// passed after the key so the (out, n, key) prefix stays identical everywhere.
// `categorical` instead takes an ARRAY parameter -- a pointer to the rank-1
// Float64 weights pool plus its (static, checker-pinned) length -- passed in
// the same position, after the key.
//
// `key` is the stream key: same key => same sequence; nearby keys decorrelate
// (SplitMix64 finalizer). The key-first signature is the seam for a future
// counter-based (Philox-style) backend -- only these function bodies change.
//
// EVERY transform below is hand-rolled and consumes the per-call mt19937_64
// stream STRICTLY SEQUENTIALLY, one `next_uniform`/`next_normal` at a time, with
// no buffering, no caching and no std::*_distribution anywhere. That is what
// lets src/Interp/RandMirror.fs replicate each draw operation-for-operation and
// keeps interpreter output byte-identical to the compiled binary's. Rejection
// loops are legal (and used by `gamma`) precisely because the accept/reject
// decision is itself a deterministic function of the stream.
#pragma once
#include <cstdint>
#include <cstddef>
#include <cmath>
#include <random>
#include <vector>

namespace blade_rand {

// SplitMix64 finalizer: decorrelates nearby keys before seeding the engine.
inline uint64_t mix64(uint64_t z) {
    z += 0x9E3779B97F4A7C15ULL;
    z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9ULL;
    z = (z ^ (z >> 27)) * 0x94D049BB133111EBULL;
    return z ^ (z >> 31);
}

// Top 53 bits of a 64-bit word, scaled to [0, 1). Explicit casts keep the
// int->double conversion clear of -Werror=narrowing / -Werror=float-conversion.
inline double bits_to_unit(uint64_t x) {
    return static_cast<double>(x >> 11) * (1.0 / 9007199254740992.0); // 2^-53
}

inline double next_uniform(std::mt19937_64& g) {
    return bits_to_unit(g());
}

// Box-Muller (our own; NOT std::normal_distribution). Two uniforms -> one
// standard normal. u1 is floored away from 0 so log(u1) stays finite.
inline double next_normal(std::mt19937_64& g) {
    const double two_pi = 6.283185307179586476925286766559;
    double u1 = next_uniform(g);
    double u2 = next_uniform(g);
    if (u1 < (1.0 / 9007199254740992.0)) u1 = (1.0 / 9007199254740992.0);
    return std::sqrt(-2.0 * std::log(u1)) * std::cos(two_pi * u2);
}

// Exp(rate) by inverse CDF: -log(1-u)/rate. ONE uniform per draw. u in [0,1)
// => 1-u in (0,1], so log() is finite without a floor (the u==0 endpoint that
// would be the singular one is unreachable from the OPEN end of the interval).
inline double next_exponential(std::mt19937_64& g, double rate) {
    double u = next_uniform(g);
    return -std::log(1.0 - u) / rate;
}

// Gamma(shape, 1) for shape >= 1 -- Marsaglia-Tsang (2000) squeeze. Each
// iteration consumes one normal (= two uniforms) and, when v > 0, one further
// uniform; a v <= 0 rejection consumes ONLY the normal and retries. The cheap
// polynomial squeeze is tried first and the log test is the fallback, exactly
// as published. This ordering is part of the mirror contract: RandMirror.fs
// must branch on the same conditions in the same sequence or the two streams
// desynchronize after the first rejection.
inline double next_gamma_ge1(std::mt19937_64& g, double shape) {
    const double d = shape - (1.0 / 3.0);
    const double c = 1.0 / std::sqrt(9.0 * d);
    for (;;) {
        double x = next_normal(g);
        double v = 1.0 + c * x;
        if (v <= 0.0) continue;
        v = v * v * v;
        double u = next_uniform(g);
        double x2 = x * x;
        if (u < 1.0 - 0.0331 * x2 * x2) return d * v;
        if (std::log(u) < 0.5 * x2 + d * (1.0 - v + std::log(v))) return d * v;
    }
}

// Gamma(shape, rate) for any shape > 0, rate > 0. shape < 1 uses the standard
// Marsaglia-Tsang BOOST: draw Gamma(shape+1, 1) and scale by u^(1/shape). Draw
// order is gamma-THEN-uniform (the mirror replicates it verbatim). `rate` is an
// inverse-scale, applied last by division.
inline double next_gamma(std::mt19937_64& g, double shape, double rate) {
    if (shape < 1.0) {
        double gg = next_gamma_ge1(g, shape + 1.0);
        double u = next_uniform(g);
        return gg * std::pow(u, 1.0 / shape) / rate;
    }
    return next_gamma_ge1(g, shape) / rate;
}

// Poisson(lam) by Knuth's product-of-uniforms: multiply U[0,1) draws until the
// running product drops to or below e^-lam; the number of multiplications after
// the first is the variate.
//
// COST: the expected number of uniforms is lam + 1, i.e. O(lam) per draw, so an
// `n`-element fill is O(n * lam). This is fine for the moderate lam the P2
// surface targets and is intentionally the simplest algorithm with an exactly
// reproducible stream. For large lam a PTRS/transformed-rejection method would
// be needed -- and would be a separate, separately-mirrored function, since
// swapping the algorithm changes every pinned draw.
//
// TERMINATION at large lam: e^-lam underflows to +0 for lam > ~745, but the
// product of uniforms also underflows to exactly +0 in finitely many steps, so
// `p <= L` still fires; the loop cannot spin forever. lam == 0 gives L == 1.0
// and terminates on the first draw with k == 0, which is correct.
inline double next_poisson(std::mt19937_64& g, double lam) {
    const double L = std::exp(-lam);
    double p = 1.0;
    double k = 0.0;
    for (;;) {
        p *= next_uniform(g);
        if (p <= L) return k;
        k += 1.0;
    }
}

// Bernoulli(p): ONE uniform, 1.0 iff u < p. Returned as a double (see the
// element-type note in the header comment). Note this transform involves no
// libm call at all -- only a comparison -- so it is the one family whose draws
// are bit-identical between mirror and binary by construction.
inline double next_bernoulli(std::mt19937_64& g, double p) {
    return next_uniform(g) < p ? 1.0 : 0.0;
}

// Beta(a, b) = g1 / (g1 + g2) with g1 ~ Gamma(a,1), g2 ~ Gamma(b,1), drawn in
// that order. The s <= 0 guard catches the degenerate case where both gammas
// underflow to 0 (only reachable for very small a and b); 0.0 is returned
// rather than a NaN so the fill stays printable.
inline double next_beta(std::mt19937_64& g, double a, double b) {
    double g1 = next_gamma(g, a, 1.0);
    double g2 = next_gamma(g, b, 1.0);
    double s = g1 + g2;
    if (s <= 0.0) return 0.0;
    return g1 / s;
}

inline void uniform(double* out, size_t n, int64_t key) {
    std::mt19937_64 g(mix64(static_cast<uint64_t>(key)));
    for (size_t i = 0; i < n; ++i) out[i] = next_uniform(g);
}

inline void normal(double* out, size_t n, int64_t key) {
    std::mt19937_64 g(mix64(static_cast<uint64_t>(key)));
    for (size_t i = 0; i < n; ++i) out[i] = next_normal(g);
}

inline void exponential(double* out, size_t n, int64_t key, double rate) {
    std::mt19937_64 g(mix64(static_cast<uint64_t>(key)));
    for (size_t i = 0; i < n; ++i) out[i] = next_exponential(g, rate);
}

inline void gamma(double* out, size_t n, int64_t key, double shape, double rate) {
    std::mt19937_64 g(mix64(static_cast<uint64_t>(key)));
    for (size_t i = 0; i < n; ++i) out[i] = next_gamma(g, shape, rate);
}

inline void poisson(double* out, size_t n, int64_t key, double lam) {
    std::mt19937_64 g(mix64(static_cast<uint64_t>(key)));
    for (size_t i = 0; i < n; ++i) out[i] = next_poisson(g, lam);
}

inline void bernoulli(double* out, size_t n, int64_t key, double p) {
    std::mt19937_64 g(mix64(static_cast<uint64_t>(key)));
    for (size_t i = 0; i < n; ++i) out[i] = next_bernoulli(g, p);
}

inline void beta(double* out, size_t n, int64_t key, double a, double b) {
    std::mt19937_64 g(mix64(static_cast<uint64_t>(key)));
    for (size_t i = 0; i < n; ++i) out[i] = next_beta(g, a, b);
}

// Categorical(w): an INDEX in [0, k) with P(i) = w_i / sum(w). The weights need
// not be normalized. Unlike every other family this one has no `next_*` helper:
// the normalized cumulative scan is loop-invariant, so it is computed ONCE per
// fill and shared by all `n` draws, and a per-draw helper would either recompute
// it or need the scan threaded through it.
//
// DRAW BUDGET: exactly ONE uniform per element, unconditionally -- including the
// degenerate branch below, which still draws before returning. That keeps the
// stream position a function of `n` alone, so the mirror stays in step no matter
// what the weights are.
//
// WEIGHT VALIDATION follows the wave-1 convention exactly: these fills never
// panic and never validate (gamma with shape <= 0 does not check, it just lets
// the arithmetic produce what it produces), and the one guard that exists --
// beta's `s <= 0` -> 0.0 -- is there to keep the output PRINTABLE rather than to
// report an error. Two guards here are of that same kind:
//   * A negative or NaN weight contributes 0 to the scan (`w[i] > 0.0` is false
//     for both). This is not error reporting; a non-monotone cumulative array
//     would make the inverse-CDF walk meaningless, so clamping is what gives the
//     walk a defined answer at all. A negative weight is therefore silently read
//     as zero probability.
//   * If the total is not positive (all weights zero/negative/NaN), every draw
//     returns index 0. Like beta's guard this keeps the fill printable and
//     in-range instead of producing a NaN or an out-of-bounds subscript.
// Neither case is diagnosed. Callers wanting rejection must check the weights in
// Blade before the call.
//
// SCALE INVARIANCE: scaling every weight by a power of two leaves the draws
// BIT-identical (the scan and the division by the total scale exactly), which is
// what the corpus scale-invariance test pins. For a general scale factor the
// draws agree up to the rounding of the scan, as with any float reduction.
inline void categorical(int64_t* out, size_t n, int64_t key, const double* w, size_t k) {
    std::mt19937_64 g(mix64(static_cast<uint64_t>(key)));
    // One-time cumulative scan, running sum left to right (the mirror sums in
    // this same order -- a different association would round differently).
    std::vector<double> cum(k);
    double acc = 0.0;
    for (size_t i = 0; i < k; ++i) {
        acc += (w[i] > 0.0) ? w[i] : 0.0;
        cum[i] = acc;
    }
    const double total = acc;
    const bool degenerate = !(total > 0.0);
    if (!degenerate) {
        for (size_t i = 0; i < k; ++i) cum[i] /= total;
    }
    // After normalization cum[k-1] == 1.0 exactly and every u is < 1.0, so the
    // walk always finds a j; the `j + 1 < k` bound is a belt-and-braces clamp.
    // Zero-weight indices are unreachable: they leave cum flat, and the strict
    // `u >= cum[j]` step walks past every flat run.
    for (size_t i = 0; i < n; ++i) {
        double u = next_uniform(g);
        if (degenerate) { out[i] = 0; continue; }
        size_t j = 0;
        while (j + 1 < k && u >= cum[j]) ++j;
        out[i] = static_cast<int64_t>(j);
    }
}

} // namespace blade_rand
