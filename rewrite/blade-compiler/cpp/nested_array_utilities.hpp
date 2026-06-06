#pragma once
// nested_array_utilities.hpp
// Blade DSL Runtime Support Library

#include <algorithm>
#include <cstddef>
#include <functional>
#include <tuple>
#include <type_traits>
#include <utility>  // for std::index_sequence_for

namespace nested_array_utilities {

    template<typename TYPE>
    constexpr const size_t get_rank() {
        if constexpr (std::is_pointer<TYPE>::value) {
            return 1 + get_rank<typename std::remove_pointer<TYPE>::type>();
        } else {
            return 0;
        }
    }

    template<typename TYPE, const size_t rank, const size_t depth = 0>
    constexpr auto promote_impl() {
        if constexpr (depth < rank) {
            return promote_impl<typename std::add_pointer<TYPE>::type, rank, depth + 1>();
        } else if constexpr (depth == rank) {
            TYPE dummy = {0};
            return dummy;
        } else {
            return;
        }
    }

    template<typename TYPE, const size_t rank, const size_t depth = 0>
    class promote {
    public:
        typedef decltype(promote_impl<TYPE, rank>()) type;
    };

    // strip_ptr<T>: peel all pointer levels to the underlying scalar element
    // type (T*** -> T). Used by the contiguous allocator to type the single
    // backing pool.
    template<typename TYPE, bool IsPtr = std::is_pointer<TYPE>::value>
    struct strip_ptr;
    template<typename TYPE>
    struct strip_ptr<TYPE, false> { using type = TYPE; };
    template<typename TYPE>
    struct strip_ptr<TYPE, true> {
        using type = typename strip_ptr<typename std::remove_pointer<TYPE>::type>::type;
    };

    // pool_base<TYPE>: recover the contiguous backing-pool base pointer from a
    // promote<T,N>::type skeleton. allocate<> places ALL scalars in one pool in
    // DFS order; the first leaf row sits at pool + 0, and interior rows are just
    // pointer arrays into that pool. Descending [0] to the first scalar and
    // taking its address therefore yields the pool base, from which the whole
    // array is contiguous (cardinality elements, DFS order). This is the forward-
    // transform primitive for CUDA streaming: cudaMemcpy(d_buf, pool_base(arr),
    // cardinality * sizeof(T), H2D) copies the entire pool; the inverse uses it
    // as the D2H destination. Rank-generic (compile-time recursion on pointer
    // depth) so generated code calls pool_base(arr.data) regardless of rank.
    //
    // NOTE: relies on allocate<>'s single-contiguous-pool invariant. A skeleton
    // NOT produced by allocate<> (hypothetical non-contiguous backing) would
    // break this; all Blade arrays go through allocate<>, so the invariant holds.
    //
    // FUTURE / RaggedIdx: this is correct ONLY for closed, contiguously-allocated
    // arrays. A RaggedIdx array is OPEN (per-row materialization via
    // IRRaggedLookup, GC-tracked) and is NOT guaranteed to be one contiguous pool
    // in DFS order — so pool_base must NOT be used for ragged backing. Ragged GPU
    // streaming will need a different forward transform: gather the rows into a
    // contiguous staging buffer first, and carry the per-row lengths across the
    // extern "C" boundary as a separate `const size_t*`. Far off (gated behind
    // symmetric-kernel support and wanting ragged on the GPU at all), but the
    // assumption is recorded here so no one treats pool_base as universal.
    template<typename PTR>
    typename strip_ptr<PTR>::type* pool_base(PTR skeleton) {
        if constexpr (std::is_pointer<typename std::remove_pointer<PTR>::type>::value) {
            // Interior level: descend into row 0 (which points at the pool base).
            return pool_base(skeleton[0]);
        } else {
            // Leaf level: skeleton is T*, &skeleton[0] is the pool base.
            return &skeleton[0];
        }
    }

    // ========================================================================
    // allocate<>: contiguous-backing allocation
    // ========================================================================
    //
    // This is the index metamorphism of formalism §2.7 (double metamorphism
    // with feedback) with the DATA phases (3-5) elided: we run the INDEX loop
    // purely to discover the array's shape, allocating one slot per emitted
    // leaf. index-cata structures the remaining space (the per-level bound
    // shrink); index-ana emits each child slot; the feedback `i + lastIndex`
    // threads the current index to condition the next level.
    //
    // LAYOUT: a single contiguous data pool (alloc #1) holds every scalar
    // element in depth-first iteration order; a pointer skeleton (alloc #2)
    // is laid over it so the nested `arr[i][j][k]` interface is unchanged.
    // The leaf data rows point into `pool + offset`; interior rows are small
    // pointer arrays. This replaces the previous piecewise `new T[]` per leaf,
    // which produced a non-contiguous layout that could not be sliced into
    // cudaMemcpy-able spans.
    //
    // PER-LEVEL SPAN: the child count at each level is computed by FORMULA from
    // the prefix indices — `extents[DEPTH]` (free) or `extents[DEPTH]-lastIndex`
    // (in symmetry group). This formula path is valid for rectangular,
    // symmetric, and (via the sibling allocate_antisym) antisymmetric index
    // types. SEAM: tree/graph/compound index types (extensions §2.3-2.4) do NOT
    // have formula-computable per-level spans — they supply a precomputed
    // subtree-size / offset table and will route through a separate placement
    // path. The pool-and-skeleton substrate itself is universal (extensions
    // §2.3.6); only this span computation forks.
    //
    // TEARDOWN CONTRACT (not yet built — see CodeGen scope-tracker TODO): the
    // pool is ONE `delete[]`; the skeleton is the interior pointer rows, freed
    // bottom-up. This differs from naive recursive per-leaf frees. Generated
    // programs currently do not free (pre-existing leak, unchanged here).

    // Phase A — count_leaves: total SCALAR element count under this subtree.
    // Same recursion shape as the skeleton walk, so the count provably matches
    // the allocation traversal (no formula-vs-traversal drift).
    template<typename TYPE, const size_t SYMM[] = nullptr, const size_t DEPTH = 0>
    constexpr size_t count_leaves(const size_t extents[], const size_t lastIndex = 0) {
        typedef typename std::remove_pointer<TYPE>::type DTYPE;

        size_t n;
        if constexpr ((bool)SYMM && DEPTH > 0 && SYMM[DEPTH-1] == SYMM[DEPTH]) {
            n = extents[DEPTH] - lastIndex;
        } else {
            n = extents[DEPTH];
        }

        if constexpr (!std::is_pointer<DTYPE>::value) {
            return n;  // leaf data row contributes n scalars
        } else {
            size_t total = 0;
            for (size_t i = 0; i < n; i++) {
                if constexpr ((bool)SYMM && SYMM[DEPTH] == SYMM[DEPTH + 1]) {
                    if constexpr ((bool)SYMM && DEPTH > 0 && SYMM[DEPTH-1] == SYMM[DEPTH])
                        total += count_leaves<DTYPE, SYMM, DEPTH + 1>(extents, i + lastIndex);
                    else
                        total += count_leaves<DTYPE, SYMM, DEPTH + 1>(extents, i);
                } else {
                    total += count_leaves<DTYPE, SYMM, DEPTH + 1>(extents, 0);
                }
            }
            return total;
        }
    }

    // Phase C — build_skeleton: build pointer rows (alloc #2); leaf data rows
    // point into `pool`. `offset` is threaded by reference so leaf placement
    // order equals the DFS traversal order (the canonical storage coordinate
    // system that linearize/unlinearize will later have to agree with).
    template<typename TYPE, const size_t SYMM[] = nullptr, const size_t DEPTH = 0>
    TYPE build_skeleton(const size_t extents[],
                        typename strip_ptr<TYPE>::type* pool,
                        size_t& offset,
                        const size_t lastIndex = 0) {
        typedef typename std::remove_pointer<TYPE>::type DTYPE;

        size_t n;
        if constexpr ((bool)SYMM && DEPTH > 0 && SYMM[DEPTH-1] == SYMM[DEPTH]) {
            n = extents[DEPTH] - lastIndex;
        } else {
            n = extents[DEPTH];
        }

        if constexpr (!std::is_pointer<DTYPE>::value) {
            // Leaf data row: point into the contiguous pool, advance offset.
            TYPE row = pool + offset;
            offset += n;
            return row;
        } else {
            // Interior pointer row (alloc #2).
            TYPE row = new DTYPE[n];
            for (size_t i = 0; i < n; i++) {
                if constexpr ((bool)SYMM && SYMM[DEPTH] == SYMM[DEPTH + 1]) {
                    if constexpr ((bool)SYMM && DEPTH > 0 && SYMM[DEPTH-1] == SYMM[DEPTH])
                        row[i] = build_skeleton<DTYPE, SYMM, DEPTH + 1>(extents, pool, offset, i + lastIndex);
                    else
                        row[i] = build_skeleton<DTYPE, SYMM, DEPTH + 1>(extents, pool, offset, i);
                } else {
                    row[i] = build_skeleton<DTYPE, SYMM, DEPTH + 1>(extents, pool, offset, 0);
                }
            }
            return row;
        }
    }

    // Top-level entry. Signature compatible with the prior allocate<> (the
    // optional trailing lastIndex is accepted and ignored at the top level;
    // internal recursion threads it). Call sites are unchanged.
    template<typename TYPE, const size_t SYMM[] = nullptr, const size_t DEPTH = 0>
    TYPE allocate(const size_t extents[], const size_t /*lastIndex*/ = 0) {
        using SCALAR = typename strip_ptr<TYPE>::type;
        size_t total = count_leaves<TYPE, SYMM, 0>(extents, 0);
        SCALAR* pool = new SCALAR[total];                                 // alloc #1
        size_t offset = 0;
        return build_skeleton<TYPE, SYMM, 0>(extents, pool, offset, 0);   // alloc #2
    }

    template<typename TYPE, const size_t SYMM[] = nullptr, const size_t DEPTH = 0>
    constexpr void fill_random(TYPE array_in, const size_t extents[], int mod_in, size_t lastIndex = 0) {
        typedef typename std::remove_pointer<TYPE>::type DTYPE;

        if constexpr ((bool)SYMM && DEPTH > 0 && SYMM[DEPTH - 1] == SYMM[DEPTH]) {
            if constexpr (std::is_pointer<DTYPE>::value) {
                for (size_t i = 0; i < extents[DEPTH] - lastIndex; i++) {
                    if constexpr ((bool)SYMM && SYMM[DEPTH] == SYMM[DEPTH + 1]) {
                        fill_random<DTYPE, SYMM, DEPTH + 1>(array_in[i], extents, mod_in, i + lastIndex);
                    } else {
                        fill_random<DTYPE, SYMM, DEPTH + 1>(array_in[i], extents, mod_in);
                    }
                }
            } else {
                for (size_t i = 0; i < extents[DEPTH] - lastIndex; i++) {
                    array_in[i] = rand() % mod_in;
                }
            }
        } else {
            if constexpr (std::is_pointer<DTYPE>::value) {
                for (size_t i = 0; i < extents[DEPTH]; i++) {
                    if constexpr ((bool)SYMM && SYMM[DEPTH] == SYMM[DEPTH + 1]) {
                        fill_random<DTYPE, SYMM, DEPTH + 1>(array_in[i], extents, mod_in, i);
                    } else {
                        fill_random<DTYPE, SYMM, DEPTH + 1>(array_in[i], extents, mod_in);
                    }
                }
            } else {
                for (size_t i = 0; i < extents[DEPTH]; i++) {
                    array_in[i] = rand() % mod_in;
                }
            }
        }
    }

    template<typename TYPE, const size_t SYMM[] = nullptr, const size_t DEPTH = 0>
    constexpr void fill_value(TYPE array_in, const size_t extents[], 
                              typename std::remove_pointer<TYPE>::type value, size_t lastIndex = 0) {
        typedef typename std::remove_pointer<TYPE>::type DTYPE;

        if constexpr ((bool)SYMM && DEPTH > 0 && SYMM[DEPTH - 1] == SYMM[DEPTH]) {
            if constexpr (std::is_pointer<DTYPE>::value) {
                for (size_t i = 0; i < extents[DEPTH] - lastIndex; i++) {
                    if constexpr ((bool)SYMM && SYMM[DEPTH] == SYMM[DEPTH + 1]) {
                        fill_value<DTYPE, SYMM, DEPTH + 1>(array_in[i], extents, value, i + lastIndex);
                    } else {
                        fill_value<DTYPE, SYMM, DEPTH + 1>(array_in[i], extents, value);
                    }
                }
            } else {
                for (size_t i = 0; i < extents[DEPTH] - lastIndex; i++) {
                    array_in[i] = value;
                }
            }
        } else {
            if constexpr (std::is_pointer<DTYPE>::value) {
                for (size_t i = 0; i < extents[DEPTH]; i++) {
                    if constexpr ((bool)SYMM && SYMM[DEPTH] == SYMM[DEPTH + 1]) {
                        fill_value<DTYPE, SYMM, DEPTH + 1>(array_in[i], extents, value, i);
                    } else {
                        fill_value<DTYPE, SYMM, DEPTH + 1>(array_in[i], extents, value);
                    }
                }
            } else {
                for (size_t i = 0; i < extents[DEPTH]; i++) {
                    array_in[i] = value;
                }
            }
        }
    }


    // =========================================================================
    // Antisymmetric array support
    // =========================================================================

    // Allocate antisymmetric array: strict i < j < ... < k (no repetition).
    // Cardinality = C(n, r) per formalism (AntisymIdx<r,n>). Contiguous backing:
    // one data pool (alloc #1) + pointer skeleton (alloc #2), same pattern as
    // allocate<> above. The strict-simplex recurrence starts each level's index
    // at prev+1 (vs. symmetric's prev), so no diagonal is stored.
    //
    // NOTE (correctness fix): the previous version used a per-level bound of
    // `extents[DEPTH] - lastIndex` at every level INCLUDING the leaf, which is
    // the symmetric (<=) count, not the strict (<) count. That over-counted at
    // rank >= 3 (e.g. rank-3 n=4 gave 10 instead of C(4,3)=4); rank-2 happened
    // to be correct. The strict `start = prev+1` recurrence below matches the
    // documented C(n,r) cardinality at all ranks.
    //
    // SEAM / TEARDOWN: same as allocate<> — pool is one delete[], skeleton rows
    // freed bottom-up; not yet built (pre-existing leak). Tree/graph antisym
    // (commutative children, extensions §2.3.8 open question) is future work.

    // Phase A — count strict-simplex leaves. `start` = first allowed index here.
    template<typename TYPE, const size_t DEPTH = 0>
    constexpr size_t count_antisym(const size_t extents[], size_t start = 0) {
        typedef typename std::remove_pointer<TYPE>::type DTYPE;
        size_t n = extents[DEPTH];
        size_t cnt = (n > start) ? n - start : 0;          // indices [start, n)
        if constexpr (!std::is_pointer<DTYPE>::value) {
            return cnt;                                     // leaf row length
        } else {
            size_t total = 0;
            for (size_t idx = start; idx < n; idx++)
                total += count_antisym<DTYPE, DEPTH + 1>(extents, idx + 1);  // strict
            return total;
        }
    }

    // Phase C — build skeleton over pool; leaf rows point into `pool`.
    // `offset` threaded by reference => leaf placement order == DFS order.
    template<typename TYPE, const size_t DEPTH = 0>
    TYPE build_antisym(const size_t extents[],
                       typename strip_ptr<TYPE>::type* pool,
                       size_t& offset,
                       size_t start = 0) {
        typedef typename std::remove_pointer<TYPE>::type DTYPE;
        size_t n = extents[DEPTH];
        size_t cnt = (n > start) ? n - start : 0;
        if constexpr (!std::is_pointer<DTYPE>::value) {
            TYPE row = pool + offset;                       // leaf data row
            offset += cnt;
            return row;
        } else {
            TYPE row = new DTYPE[cnt];
            size_t local = 0;
            for (size_t idx = start; idx < n; idx++)
                row[local++] = build_antisym<DTYPE, DEPTH + 1>(extents, pool, offset, idx + 1);
            return row;
        }
    }

    // Top-level entry. Signature compatible with the prior allocate_antisym
    // (optional trailing lastIndex accepted and ignored at top level).
    template<typename TYPE, const size_t DEPTH = 0>
    TYPE allocate_antisym(const size_t extents[], const size_t /*lastIndex*/ = 0) {
        using SCALAR = typename strip_ptr<TYPE>::type;
        size_t total = count_antisym<TYPE, 0>(extents, 0);
        // Guard the degenerate total==0 case (e.g. rank > n): allocate a
        // 1-element pool so `new SCALAR[0]`-then-deref is never relied upon.
        SCALAR* pool = new SCALAR[total > 0 ? total : 1];   // alloc #1
        size_t offset = 0;
        return build_antisym<TYPE, 0>(extents, pool, offset, 0);  // alloc #2
    }

    // =========================================================================
    // Index canonicalization wrappers
    // =========================================================================

    // Symmetric canonicalization: (i,j) -> (min(i,j), max(i,j))
    inline void sym_canonical(size_t i, size_t j, size_t& ci, size_t& cj) {
        ci = (i <= j) ? i : j;
        cj = (i <= j) ? j : i;
    }

    // Antisymmetric canonicalization: (i,j) -> (min(i,j), max(i,j)), sign = +1 or -1
    // Returns -1 if swapped (odd permutation), +1 if not
    inline int antisym_canonical(size_t i, size_t j, size_t& ci, size_t& cj) {
        if (i < j) { ci = i; cj = j; return 1; }
        else if (i > j) { ci = j; cj = i; return -1; }
        else { ci = i; cj = j; return 0; }  // diagonal: value is zero
    }

    // Hermitian canonicalization: (i,j) -> (min(i,j), max(i,j)), needs_conj flag
    // For Hermitian: A(i,j) = conj(A(j,i)), so access with j<i needs conjugation
    inline bool hermitian_canonical(size_t i, size_t j, size_t& ci, size_t& cj) {
        if (i <= j) { ci = i; cj = j; return false; }  // no conjugation needed
        else { ci = j; cj = i; return true; }           // needs conjugation
    }

    // =========================================================================
    // tuple_hasher: std::hash specialization for std::tuple<...>
    // =========================================================================
    //
    // The standard library does not provide std::hash<std::tuple<...>>.
    // This hasher uses the canonical boost-style hash-combine recipe:
    //   seed ^= hash(elem) + 0x9e3779b9 + (seed << 6) + (seed >> 2)
    // applied across all tuple elements via C++17 fold expression.
    //
    // Used by compound group_keys (multi-key SQL-style grouping) where the
    // bucket dispatch is an unordered_map keyed by a tuple of component
    // key values. Each component must itself be hashable via std::hash.
    struct tuple_hasher {
        template <typename... Ts>
        std::size_t operator()(const std::tuple<Ts...>& t) const noexcept {
            return hash_combine_tuple(t, std::index_sequence_for<Ts...>{});
        }
    private:
        template <typename Tuple, std::size_t... I>
        static std::size_t hash_combine_tuple(const Tuple& t, std::index_sequence<I...>) noexcept {
            std::size_t seed = 0;
            ((seed ^= std::hash<std::tuple_element_t<I, Tuple>>{}(std::get<I>(t))
                       + 0x9e3779b9 + (seed << 6) + (seed >> 2)), ...);
            return seed;
        }
    };

} // namespace nested_array_utilities

using namespace nested_array_utilities;
