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

    template<typename TYPE, const size_t SYMM[] = nullptr, const size_t DEPTH = 0>
    constexpr TYPE allocate(const size_t extents[], const size_t lastIndex = 0) {
        typedef typename std::remove_pointer<TYPE>::type DTYPE;
        TYPE array;

        if constexpr ((bool)SYMM && DEPTH > 0 && SYMM[DEPTH-1] == SYMM[DEPTH]) {
            array = new DTYPE[extents[DEPTH] - lastIndex];
            if constexpr (std::is_pointer<DTYPE>::value) {
                for (size_t i = 0; i < extents[DEPTH] - lastIndex; i++) {
                    if constexpr ((bool)SYMM && SYMM[DEPTH] == SYMM[DEPTH + 1]) {
                        array[i] = allocate<DTYPE, SYMM, DEPTH + 1>(extents, i + lastIndex);
                    } else {
                        array[i] = allocate<DTYPE, SYMM, DEPTH + 1>(extents);
                    }
                }
            }
        } else {
            array = new DTYPE[extents[DEPTH]];
            if constexpr (std::is_pointer<DTYPE>::value) {
                for (size_t i = 0; i < extents[DEPTH]; i++) {
                    if constexpr ((bool)SYMM && SYMM[DEPTH] == SYMM[DEPTH + 1]) {
                        array[i] = allocate<DTYPE, SYMM, DEPTH + 1>(extents, i);
                    } else {
                        array[i] = allocate<DTYPE, SYMM, DEPTH + 1>(extents);
                    }
                }
            }
        }
        return array;
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

    // Allocate antisymmetric array: strict i < j, so n-1 elements in first row,
    // n-2 in second, etc. Total: n(n-1)/2
    // Same nested pointer structure as symmetric but with -1 offset at each level.
    template<typename TYPE, const size_t DEPTH = 0>
    constexpr TYPE allocate_antisym(const size_t extents[], const size_t lastIndex = 0) {
        typedef typename std::remove_pointer<TYPE>::type DTYPE;
        TYPE array;

        if constexpr (DEPTH == 0) {
            // Outermost: full extent (rows)
            array = new DTYPE[extents[DEPTH]];
            if constexpr (std::is_pointer<DTYPE>::value) {
                for (size_t i = 0; i < extents[DEPTH]; i++) {
                    array[i] = allocate_antisym<DTYPE, DEPTH + 1>(extents, i + 1);
                }
            }
        } else {
            // Inner: strict bound, extent - lastIndex elements
            size_t len = (extents[DEPTH] > lastIndex) ? extents[DEPTH] - lastIndex : 0;
            array = new DTYPE[len];
            if constexpr (std::is_pointer<DTYPE>::value) {
                for (size_t i = 0; i < len; i++) {
                    array[i] = allocate_antisym<DTYPE, DEPTH + 1>(extents, i + lastIndex);
                }
            }
        }
        return array;
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
