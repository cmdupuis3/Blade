// Blade `rand` module runtime — deterministic, cross-compiler-stable RNG.
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
//   blade_rand::uniform(double* out, size_t n, int64_t key) — n draws ~ U[0,1)
//   blade_rand::normal (double* out, size_t n, int64_t key) — n draws ~ N(0,1)
//
// `key` is the stream key: same key => same sequence; nearby keys decorrelate
// (SplitMix64 finalizer). The key-first signature is the seam for a future
// counter-based (Philox-style) backend — only these function bodies change.
#pragma once
#include <cstdint>
#include <cstddef>
#include <cmath>
#include <random>

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

inline void uniform(double* out, size_t n, int64_t key) {
    std::mt19937_64 g(mix64(static_cast<uint64_t>(key)));
    for (size_t i = 0; i < n; ++i) out[i] = next_uniform(g);
}

inline void normal(double* out, size_t n, int64_t key) {
    std::mt19937_64 g(mix64(static_cast<uint64_t>(key)));
    for (size_t i = 0; i < n; ++i) out[i] = next_normal(g);
}

} // namespace blade_rand
