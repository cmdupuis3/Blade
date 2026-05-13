#pragma once
// nested_array_types.hpp
// Blade DSL: array wrapper types
//
// Replaces the bare-pointer-plus-sibling-globals representation with
// uniform wrapper structs that bundle data + shape together. This lets
// arrays be first-class values: passable to functions as a single
// argument, storable as a struct field without losing shape information,
// and uniformly indexed regardless of rectangular vs ragged shape.
//
// Phase D / v24 refactor:
//   - Rectangular: `Array<T, N>` carries a `promote<T, N>::type data` and
//     a `const size_t* extents` pointer (shape lives outside the wrapper
//     since extents are typically static-constexpr globals).
//   - Ragged: `Ragged<T>` carries `T** data` (row pointers), plus
//     `extents`, `lens`, and `offsets` (CSR-style) for shape.
//
// Indexing: both wrappers expose `operator[]` that forwards to the
// underlying data. So `arr[i]` works transparently — consumers don't
// need to write `arr.data[i]`.
//
// Storage ownership: wrapper does NOT own the underlying memory. The
// caller manages allocation and lifetime via the existing `allocate<>`
// machinery; the wrapper just bundles already-allocated pointers.

#include <cstddef>
#include "nested_array_utilities.hpp"

namespace nested_array_utilities {

    // Rectangular array wrapper. The data member is a typename
    // promote<T, N>::type which resolves to T*** ... * (N pointer levels).
    // operator[] returns the next-level-down: indexing an Array<T, N>
    // peels one level, returning the T** ... * inner type. For a fully-
    // indexed scalar element, the recursive operator[] chain bottoms out
    // at T directly.
    template<typename T, size_t N>
    struct Array {
        typename promote<T, N>::type data;
        const size_t* extents;

        // Forwarding indexing. Returns whatever the underlying pointer's
        // operator[] returns — for rank > 1, that's another pointer; for
        // rank 1, it's a T&.
        constexpr auto& operator[](size_t i) const { return data[i]; }
        constexpr auto& operator[](size_t i) { return data[i]; }

        // Implicit conversion to the underlying pointer type. Used by:
        // (1) producer-side rank-N construction patterns where rank-(N-1)
        //     wrappers are assigned into outer-array slots that have type
        //     T* (e.g. result[i] = result_i in Sequence/Replicate codegen)
        // (2) auto-print machinery that streams array-typed struct fields
        //     and bindings through cout — the conversion lets std's
        //     operator<<(const void*) overload print the pointer address
        //     when no specialized printer exists.
        // Removing this requires per-site changes (explicit .data on writes,
        // smart printers that skip arrays) which is deferred.
        constexpr operator typename promote<T, N>::type() const { return data; }
    };

    // A single row of a Ragged array. Bundles a row pointer with its
    // runtime length, so callers receiving a row can query `.len`
    // without holding a reference back to the parent Ragged.
    //
    // Minimal by design: just operator[] for indexing into the row, plus
    // an implicit decay to `T*` so existing code that consumes a row as
    // a raw pointer (the historic Ragged::operator[] return type) keeps
    // working without any callsite changes.
    //
    // Ownership / lifetime: `data` is a non-owning pointer into the
    // parent Ragged's backing array; `len` is a copy of `lens[i]` at
    // the time of construction. If the parent Ragged is mutated or
    // destroyed, the row's pointers become dangling — same hazard as
    // the previous `T*`-returning operator[].
    template<typename T>
    struct RaggedRow {
        T* data;
        size_t len;

        constexpr T& operator[](size_t i) const { return data[i]; }
        constexpr T& operator[](size_t i) { return data[i]; }

        // Implicit decay to raw pointer. Preserves source compatibility
        // with code written against the prior `Ragged::operator[] -> T*`.
        constexpr operator T*() const { return data; }
    };

    // Ragged array wrapper. Mirrors the existing CSR-style layout:
    //   - `data` is the row-pointer array (T**) — each row may have a
    //     different length.
    //   - `extents` carries the outer-dimension extent (`extents[0] = n`,
    //     the number of rows). Kept as a pointer for layout uniformity
    //     with Array<T, N>.
    //   - `lens[i]` is the length of row i.
    //   - `offsets[i]` is the offset into the flat backing array where
    //     row i begins; `offsets[n]` is the total element count.
    //
    // Indexing returns a `RaggedRow<T>` carrying both the row pointer
    // and its length. The wrapper implicitly converts to `T*` so callers
    // expecting the prior raw-pointer return type still compile; new
    // callers that want the row length can read it directly via `.len`.
    template<typename T>
    struct Ragged {
        T** data;
        const size_t* extents;
        const size_t* lens;
        const size_t* offsets;

        constexpr RaggedRow<T> operator[](size_t i) const { return RaggedRow<T>{data[i], lens[i]}; }
        constexpr RaggedRow<T> operator[](size_t i) { return RaggedRow<T>{data[i], lens[i]}; }

        // Implicit conversion to T**. Same rationale as Array<T,N> above.
        constexpr operator T**() const { return data; }
    };

}  // namespace nested_array_utilities
