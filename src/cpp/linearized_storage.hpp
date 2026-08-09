#pragma once
// linearized_storage.hpp
// Blade DSL Runtime Support Library -- flat linearized storage
//
// PURPOSE. An ALTERNATIVE storage representation to the nested
// pointer-skeleton arrays in nested_array_utilities.hpp: that header (HOST)
// is a contiguous data pool plus a T*****-style pointer skeleton, addressed
// as arr[i][j][k] via pointer dereference chains, isomorphic to Blade
// source. This header (DEVICE-ORIENTED) is a BARE flat pool of
// `cardinality` elements with no skeleton, addressed by arithmetic --
// linearize(tuple) -> flat offset and unlinearize(offset) -> tuple.
//
// Why a separate scheme: a host-built pointer skeleton holds host addresses,
// meaningless after cudaMemcpy to a device, which wants a flat block indexed
// by arithmetic instead. The flat allocate here is just `new T[cardinality]`
// -- no skeleton. The flat offset from linearize() is IDENTICAL to the DFS
// storage order nested_array_utilities.hpp's contiguous pool lays down
// (verified against that allocator's traversal): the two schemes agree on
// canonical order, differing only in whether a pointer skeleton sits on top.
//
// RANKING SCHEME. Canonical order is sorted tuples in ascending-lex (the
// allocator's DFS order): for r=3 n=4, (0,0,0),(0,0,1),...,(0,3,3),
// (1,1,1),...,(3,3,3).
//   Symmetric      (SymIdx<r,n>):     i_0 <= i_1 <= ... <= i_{r-1}, card C(n+r-1, r)
//   Antisymmetric  (AntisymIdx<r,n>): i_0 <  i_1 <  ... <  i_{r-1}, card C(n, r)
// linearize sums r binomial terms in closed form via the hockey-stick
// identity (no O(n) inner loop): O(r), independent of n. unlinearize inverts
// coordinate-by-coordinate via BISECTION over the monotone prefix-count (not
// a linear scan): O(r * log n), the per-thread cost a GPU thread pays at
// kernel entry to recover its tuple from its flat thread id.
//
// COST NOTE: op counts are dominated by integer DIVISION inside the binomial
// evaluation, comparatively expensive on GPUs, so raw op count understates
// real device cost. Whether O(r log n) with division-heavy inner work beats
// a precomputed unranking table in shared memory is an empirical question
// per (r, n), not settled here. TEARDOWN: the flat pool is a single
// `delete[]`, no skeleton to free; generated programs currently do not free
// (consistent with the runtime).

#include <cstddef>
#include <array>

namespace linearized_storage {

    // Shared combinatorial helpers.

    // Binomial coefficient C(a, b). Uses the symmetric reduction b = min(b,a-b)
    // and incremental multiply/divide to limit intermediate growth.
    constexpr size_t binom(size_t a, size_t b) {
        if (b > a) return 0;
        if (b == 0 || b == a) return 1;
        if (b > a - b) b = a - b;
        size_t r = 1;
        for (size_t i = 0; i < b; i++) {
            r = r * (a - i) / (i + 1);
        }
        return r;
    }

    // Symmetric: i_0 <= i_1 <= ... <= i_{r-1}, entries in [0, n). Maps to
    // strictly-increasing via d_j = i_j + j (multiset combinadic); offsets
    // are computed directly on the sorted tuple using the count of sorted
    // suffixes, closed-form via hockey-stick.

    namespace symmetric {

        // Count of sorted-with-repetition suffixes of length `rem`, leading
        // coordinate in [lo, hi), each entry in [., n): closed form
        // sum_{v=lo}^{hi-1} C((n-v)+rem-1, rem), collapsed by hockey-stick
        // to a single pair of binomials.
        constexpr size_t block_count(size_t lo, size_t hi, size_t n, size_t rem) {
            if (rem == 0) return (hi > lo) ? (hi - lo) : 0;  // length-0 suffix
            if (hi <= lo) return 0;
            size_t a = n - hi + 1;   // low w in the telescoped sum
            size_t b = n - lo;       // high w
            size_t hi_term = binom(b + rem, rem + 1);
            size_t lo_term = (a >= 1) ? binom(a + rem - 1, rem + 1) : 0;
            return hi_term - lo_term;
        }

        // Cardinality: C(n + r - 1, r).
        constexpr size_t cardinality(size_t n, size_t r) {
            return binom(n + r - 1, r);
        }

        // linearize: sorted tuple -> flat offset. O(r), independent of n.
        template<size_t R>
        constexpr size_t linearize(const std::array<size_t, R>& idx, size_t n) {
            size_t offset = 0;
            size_t lo = 0;
            for (size_t p = 0; p < R; p++) {
                size_t rem = R - p - 1;
                offset += block_count(lo, idx[p], n, rem);
                lo = idx[p];
            }
            return offset;
        }

        // unlinearize: flat offset -> sorted tuple. O(r log n) via bisection.
        template<size_t R>
        std::array<size_t, R> unlinearize(size_t offset, size_t n) {
            std::array<size_t, R> idx{};
            size_t lo = 0;
            for (size_t p = 0; p < R; p++) {
                size_t rem = R - p - 1;
                // Largest c in [lo, n) with cumulative prefix count <= offset.
                size_t loB = lo, hiB = n;
                while (loB < hiB) {
                    size_t mid = loB + (hiB - loB) / 2;
                    size_t cum = block_count(lo, mid + 1, n, rem);
                    if (cum <= offset) loB = mid + 1;
                    else hiB = mid;
                }
                size_t c = loB;
                offset -= block_count(lo, c, n, rem);
                idx[p] = c;
                lo = c;
            }
            return idx;
        }

        // Flat allocation: bare pool of `cardinality(n,r)` elements, no skeleton.
        template<typename T>
        T* allocate(size_t n, size_t r) {
            size_t card = cardinality(n, r);
            return new T[card > 0 ? card : 1];
        }

    }  // namespace symmetric

    // Antisymmetric: i_0 < i_1 < ... < i_{r-1}, entries in [0, n). Strict
    // combinadic (no +j shift; strictness is intrinsic), same ascending-lex
    // canonical order restricted to strictly increasing tuples.

    namespace antisymmetric {

        // Count of strictly-increasing suffixes of length `rem`, leading
        // coordinate in [lo, hi), each entry in [., n). A strict suffix
        // starting at v has C(n-v-1, rem) completions, summed over v in
        // [lo, hi): sum_{v=lo}^{hi-1} C(n-v-1, rem) = (w = n-v-1)
        // sum_{w=n-hi}^{n-lo-1} C(w, rem), collapsing by hockey-stick to
        // C(n-lo, rem+1) - C(n-hi, rem+1).
        constexpr size_t block_count(size_t lo, size_t hi, size_t n, size_t rem) {
            if (rem == 0) return (hi > lo) ? (hi - lo) : 0;
            if (hi <= lo) return 0;
            size_t hi_term = binom(n - lo, rem + 1);
            size_t lo_term = binom(n - hi, rem + 1);
            return hi_term - lo_term;
        }

        // Cardinality: C(n, r).
        constexpr size_t cardinality(size_t n, size_t r) {
            return binom(n, r);
        }

        // linearize: strictly-increasing tuple -> flat offset. O(r).
        template<size_t R>
        constexpr size_t linearize(const std::array<size_t, R>& idx, size_t n) {
            size_t offset = 0;
            size_t lo = 0;
            for (size_t p = 0; p < R; p++) {
                size_t rem = R - p - 1;
                offset += block_count(lo, idx[p], n, rem);
                lo = idx[p] + 1;   // strict: next coordinate must exceed idx[p]
            }
            return offset;
        }

        // unlinearize: flat offset -> strictly-increasing tuple. O(r log n).
        template<size_t R>
        std::array<size_t, R> unlinearize(size_t offset, size_t n) {
            std::array<size_t, R> idx{};
            size_t lo = 0;
            for (size_t p = 0; p < R; p++) {
                size_t rem = R - p - 1;
                size_t loB = lo, hiB = n;
                while (loB < hiB) {
                    size_t mid = loB + (hiB - loB) / 2;
                    size_t cum = block_count(lo, mid + 1, n, rem);
                    if (cum <= offset) loB = mid + 1;
                    else hiB = mid;
                }
                size_t c = loB;
                offset -= block_count(lo, c, n, rem);
                idx[p] = c;
                lo = c + 1;        // strict
            }
            return idx;
        }

        template<typename T>
        T* allocate(size_t n, size_t r) {
            size_t card = cardinality(n, r);
            return new T[card > 0 ? card : 1];
        }

    }  // namespace antisymmetric

}  // namespace linearized_storage
