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
#include "index_types.h"   // compound_index_t (+ tabulated bases) for Compound<T,RANK>

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

    // Compound array wrapper -- a masked product space (formalism 4.5). A
    // CompoundIdx covers ONLY its mutually-masked dimensions: all RANK of them
    // form one unstructured grid, the mask selects which RANK-tuples are valid,
    // and compound_index_t maps a valid tuple to a flat rank in [0, cardinality).
    // Storage is a single flat buffer of the `cardinality` valid elements, in
    // the index's canonical (lex) rank order. RANK (not "arity") is the number
    // of mask dimensions; a compound has no symmetric-group structure.
    //
    // Any OTHER dimensions of the array (e.g. a dense time axis) are SEPARATE
    // index types in the array's index list, composed by the normal array
    // machinery -- they are deliberately NOT folded into this wrapper.
    //
    // Non-owning: `data` and `idx` are caller-allocated (the construction
    // sequence + a per-mask compound_index_t); this bundles them so a compound
    // array is a first-class value. `idx` is global-namespace (index_types.h),
    // hence the `::` qualification from inside this namespace.
    template<typename T, size_t RANK>
    struct Compound {
        T* data;                              // flat backing, size = cardinality * trailing_stride
        ::compound_index_t<RANK>* idx;        // non-owning: linearize / unhash / cardinality (over the RANK leading masked dims)
        size_t trailing_stride = 1;           // product of the regular trailing extents; 1 when the mask covers all dims

        // Leading-tuple access. For an all-dims compound (trailing_stride == 1)
        // `lead` is the whole coordinate and trail_offset is 0. For a partial
        // compound `lead` selects a present leading cell and trail_offset is the
        // flattened trailing coordinate within that cell's contiguous block.
        // const because mutation goes through the data pointer, not the wrapper
        // (mirrors Array<T,N>::operator[] const returning a mutable ref).
        T& operator()(const std::array<size_t, RANK>& lead, size_t trail_offset = 0) const {
            return data[idx->linearize(lead) * trailing_stride + trail_offset];
        }

        // Trailing-block base pointer for a resolved lead tuple. This is the
        // sub-view case: a full compound index B((i,j)) on an array that still
        // has trailing regular dimensions (Array<T like CompoundIdx<mask>,
        // Idx<...>>) resolves the compound axis to ONE present cell, whose
        // trailing block is the contiguous span of `trailing_stride` elements at
        // data + linearize(lead)*trailing_stride. The caller indexes that block
        // with ordinary [t] subscripts over the trailing Idx dims. For an
        // all-dims compound (trailing_stride == 1) this points at the single
        // scalar cell; operator() is the right accessor there instead.
        T* row(const std::array<size_t, RANK>& lead) const {
            return data + idx->linearize(lead) * trailing_stride;
        }

        // Total stored elements: present leading cells (mask popcount) times the
        // trailing block size. Equals the mask popcount in the all-dims case.
        size_t size() const { return idx->cardinality * trailing_stride; }
    };

    // Leading-prefix partial indexing of a compound (formalism 4.5), residual
    // rank >= 2 (a residual CompoundIdx). Given a rank-RP parent compound and a
    // J-length leading coordinate prefix (J < RP, RP - J >= 2), produce the
    // residual Compound<T, RP-J> whose valid cells are the parent's cells sharing
    // that prefix. Because the parent's rank<->tuple table is lex-sorted, those
    // cells form a CONTIGUOUS window [lo, hi) in the parent buffer (found by
    // prefix_range), and the residual's own lex enumeration over the free axes
    // agrees cell-for-cell with that window. So the residual SHARES the parent's
    // data (data + lo*trailing_stride) with NO copy; only a fresh sub-index over
    // the free-axis sub-mask is materialized. The sub-index is heap-allocated and
    // its lifetime is that of the residual array that borrows it (GC deferred).
    //
    // (The residual-rank-1 case degenerates to a dense Idx window and is handled
    // as a plain Array<T,1> upstream, NOT here -- a rank-1 Compound is not a
    // valid API-level type.)
    template<typename T, size_t RP, size_t J>
    Compound<T, RP - J> make_partial_compound(const Compound<T, RP>& parent,
                                              const std::array<size_t, J>& pinned) {
        constexpr size_t RR = RP - J;
        auto* pidx = parent.idx;
        // Window [lo, hi) of present cells sharing the pinned J-prefix.
        std::array<size_t, RP> pfx{};
        for (size_t d = 0; d < J; d++) pfx[d] = pinned[d];
        auto range = pidx->prefix_range(pfx, J);
        size_t lo = range.first;
        // Sub-extents = parent extents for the free (trailing RR) axes.
        std::array<size_t, RR> subext{};
        for (size_t d = 0; d < RR; d++) subext[d] = pidx->extents[J + d];
        // Sub-mask over the free axes at the pinned prefix, in row-major (lex)
        // order: submask[free] = parent.mask[ pinned ++ free ].
        size_t subtotal = 1;
        for (size_t d = 0; d < RR; d++) subtotal *= subext[d];
        std::vector<bool> submask(subtotal, false);
        std::array<size_t, RR> fc{};
        for (size_t flat = 0; flat < subtotal; flat++) {
            size_t rem = flat;
            for (size_t d = RR; d-- > 0; ) { fc[d] = rem % subext[d]; rem /= subext[d]; }
            size_t poff = 0;
            for (size_t d = 0; d < J; d++)  poff = poff * pidx->extents[d]     + pinned[d];
            for (size_t d = 0; d < RR; d++) poff = poff * pidx->extents[J + d] + fc[d];
            submask[flat] = pidx->mask[poff];
        }
        auto* sidx = new ::compound_index_t<RR>("__partial", subext, submask);
        return Compound<T, RR>{ parent.data + lo * parent.trailing_stride, sidx, parent.trailing_stride };
    }

}  // namespace nested_array_utilities
