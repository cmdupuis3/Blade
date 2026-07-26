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

    // Leading-prefix partial indexing, residual rank == 1 (formalism 4.5
    // currying table: "1D = regular index"). Pinning J = RP-1 leading
    // coordinates degenerates the residual to a DENSE window: the present
    // cells sharing the prefix are contiguous in the lex-sorted parent buffer
    // ([lo, hi) via prefix_range), and their enumeration over the single free
    // axis IS that window's cell order. So the result is a plain Array<T, 1>
    // that SHARES the parent's data (no copy) -- data = parent.data + lo, and
    // extent = hi - lo (count of valid free-axis values at this prefix).
    //
    // The extents pointer is heap-allocated (same GC-deferred lifetime policy
    // as make_partial_compound's sub-index): Array<T,1> carries `const
    // size_t*`, and a window's length is a runtime value with no natural
    // static home.
    //
    // Trailing regular dimensions do not fit this helper's rank-1 result
    // (Array<T,1> cannot carry a per-cell trailing block); the compiler
    // routes that case to make_partial_window_trail (rank-2 shared window),
    // so trailing_stride == 1 whenever this is reachable from generated code.
    template<typename T, size_t RP, size_t J>
    Array<T, 1> make_partial_window(const Compound<T, RP>& parent,
                                    const std::array<size_t, J>& pinned) {
        static_assert(RP - J == 1, "make_partial_window: residual rank must be exactly 1 (use make_partial_compound for rank >= 2)");
        auto* pidx = parent.idx;
        std::array<size_t, RP> pfx{};
        for (size_t d = 0; d < J; d++) pfx[d] = pinned[d];
        auto range = pidx->prefix_range(pfx, J);
        size_t* ext = new size_t[1]{ range.second - range.first };
        return Array<T, 1>{ parent.data + range.first * parent.trailing_stride, ext };
    }

    // Scattered (non-prefix) partial indexing, residual rank >= 2. The pinned
    // coordinates occupy ARBITRARY positions (a full-arity wildcard tuple like
    // B((a, _, c, _)) whose pinned axes are not a leading prefix), so the
    // surviving cells are NOT contiguous in the parent's lex order and a
    // shared-window residual is impossible. This is the deep-copy gather:
    //   1. Build the residual sub-mask over the free axes (parent mask
    //      evaluated at the pinned coordinates), preserving free-axis lex
    //      order, and materialize its compound_index_t.
    //   2. Allocate a fresh buffer of cardinality * trailing_stride and copy
    //      each present residual cell's block from the parent at the
    //      recombined full tuple.
    // O(prod(free extents)) mask scan + O(cardinality * trailing_stride) copy
    // -- the unavoidable reconstitution cost of formalism 4.5.
    //
    // pinnedVals[i] is the coordinate pinned at parent axis pinnedPos[i];
    // pinnedPos must be strictly increasing (codegen emits it in axis order).
    template<typename T, size_t RP, size_t NPIN>
    Compound<T, RP - NPIN> make_partial_compound_gather(const Compound<T, RP>& parent,
                                                        const std::array<size_t, NPIN>& pinnedVals,
                                                        const std::array<size_t, NPIN>& pinnedPos) {
        constexpr size_t RR = RP - NPIN;
        static_assert(RR >= 2, "make_partial_compound_gather: residual rank must be >= 2 (use make_partial_gather_dense for rank 1)");
        auto* pidx = parent.idx;
        std::array<bool, RP> axisPinned{};
        std::array<size_t, RP> full{};
        for (size_t i = 0; i < NPIN; i++) { axisPinned[pinnedPos[i]] = true; full[pinnedPos[i]] = pinnedVals[i]; }
        std::array<size_t, RR> freePos{};
        { size_t f = 0; for (size_t d = 0; d < RP; d++) if (!axisPinned[d]) freePos[f++] = d; }
        std::array<size_t, RR> subext{};
        for (size_t d = 0; d < RR; d++) subext[d] = pidx->extents[freePos[d]];
        size_t subtotal = 1;
        for (size_t d = 0; d < RR; d++) subtotal *= subext[d];
        std::vector<bool> submask(subtotal, false);
        std::array<size_t, RR> fc{};
        for (size_t flat = 0; flat < subtotal; flat++) {
            size_t rem = flat;
            for (size_t d = RR; d-- > 0; ) { fc[d] = rem % subext[d]; rem /= subext[d]; }
            for (size_t d = 0; d < RR; d++) full[freePos[d]] = fc[d];
            size_t poff = 0;
            for (size_t d = 0; d < RP; d++) poff = poff * pidx->extents[d] + full[d];
            submask[flat] = pidx->mask[poff];
        }
        auto* sidx = new ::compound_index_t<RR>("__partial_gather", subext, submask);
        T* buf = new T[sidx->cardinality * parent.trailing_stride];
        for (size_t r = 0; r < sidx->cardinality; r++) {
            auto rc = sidx->unhash(r);
            for (size_t d = 0; d < RR; d++) full[freePos[d]] = rc[d];
            size_t prank = pidx->linearize(full);
            for (size_t t = 0; t < parent.trailing_stride; t++)
                buf[r * parent.trailing_stride + t] = parent.data[prank * parent.trailing_stride + t];
        }
        return Compound<T, RR>{ buf, sidx, parent.trailing_stride };
    }

    // Scattered (non-prefix) partial indexing, residual rank == 1. Same
    // gather semantics as make_partial_compound_gather, but the residual
    // degenerates to a dense Idx over the ONE free axis: the result is an
    // Array<T, 1> holding a fresh copy of the valid cells' values in free-axis
    // order, with a heap-allocated extent (count of valid values). Handles
    // both the leading/interior single-free-axis wildcard forms (B((_, b)),
    // B((a, _, c))) whose surviving cells are non-contiguous in the parent.
    //
    // Trailing dims route to make_partial_gather_dense_trail (rank-2 block
    // gather); trailing_stride == 1 in generated code reaching this helper.
    template<typename T, size_t RP, size_t NPIN>
    Array<T, 1> make_partial_gather_dense(const Compound<T, RP>& parent,
                                          const std::array<size_t, NPIN>& pinnedVals,
                                          const std::array<size_t, NPIN>& pinnedPos) {
        static_assert(RP - NPIN == 1, "make_partial_gather_dense: residual rank must be exactly 1");
        auto* pidx = parent.idx;
        std::array<bool, RP> axisPinned{};
        std::array<size_t, RP> full{};
        for (size_t i = 0; i < NPIN; i++) { axisPinned[pinnedPos[i]] = true; full[pinnedPos[i]] = pinnedVals[i]; }
        size_t freeD = 0;
        for (size_t d = 0; d < RP; d++) if (!axisPinned[d]) { freeD = d; break; }
        std::vector<size_t> ranks;
        for (size_t v = 0; v < pidx->extents[freeD]; v++) {
            full[freeD] = v;
            size_t poff = 0;
            for (size_t d = 0; d < RP; d++) poff = poff * pidx->extents[d] + full[d];
            if (pidx->mask[poff]) ranks.push_back(pidx->linearize(full));
        }
        T* buf = new T[ranks.size() > 0 ? ranks.size() : 1];
        size_t* ext = new size_t[1]{ ranks.size() };
        for (size_t i = 0; i < ranks.size(); i++)
            buf[i] = parent.data[ranks[i] * parent.trailing_stride];
        return Array<T, 1>{ buf, ext };
    }

    // Leading-prefix partial indexing, residual rank == 1, WITH one trailing
    // regular dimension (Array<T like CompoundIdx<mask>, Idx<...>>). The
    // window of present cells sharing the pinned prefix is contiguous in the
    // lex-sorted compact buffer, and each cell owns a contiguous trailing
    // block of trailing_stride elements -- so the whole slice is contiguous
    // and the DATA is shared with no copy (this is exactly why a free
    // trailing dim is cheap: interior wildcards restructure, trailing ones
    // do not). The result is a rank-2 dense array {window cells, trailing
    // extent}; the only allocation is representation glue -- Array<T,2>'s
    // row-pointer table (one pointer per cell into the shared block) and the
    // 2-entry extents. Single trailing dim only: the wrapper stores just the
    // trailing PRODUCT (trailing_stride), so multi-trailing has no per-dim
    // extents to expose (consistent with the compiler-wide gate).
    template<typename T, size_t RP, size_t J>
    Array<T, 2> make_partial_window_trail(const Compound<T, RP>& parent,
                                          const std::array<size_t, J>& pinned) {
        static_assert(RP - J == 1, "make_partial_window_trail: residual rank must be exactly 1 (use make_partial_compound for rank >= 2)");
        auto* pidx = parent.idx;
        std::array<size_t, RP> pfx{};
        for (size_t d = 0; d < J; d++) pfx[d] = pinned[d];
        auto range = pidx->prefix_range(pfx, J);
        size_t cnt = range.second - range.first;
        size_t trail = parent.trailing_stride;
        T* base = parent.data + range.first * trail;
        T** rows = new T*[cnt > 0 ? cnt : 1];
        for (size_t i = 0; i < cnt; i++) rows[i] = base + i * trail;
        size_t* ext = new size_t[2]{ cnt, trail };
        return Array<T, 2>{ rows, ext };
    }

    // Scattered (non-prefix) partial indexing, residual rank == 1, WITH one
    // trailing regular dimension. The surviving cells are non-contiguous, so
    // this is the deep-copy gather of make_partial_gather_dense generalized
    // to copy each cell's whole trailing BLOCK (trailing_stride elements)
    // rather than a single scalar. Result: rank-2 dense {valid cells,
    // trailing extent}, freshly allocated (contiguous pool + row table).
    template<typename T, size_t RP, size_t NPIN>
    Array<T, 2> make_partial_gather_dense_trail(const Compound<T, RP>& parent,
                                                const std::array<size_t, NPIN>& pinnedVals,
                                                const std::array<size_t, NPIN>& pinnedPos) {
        static_assert(RP - NPIN == 1, "make_partial_gather_dense_trail: residual rank must be exactly 1");
        auto* pidx = parent.idx;
        std::array<bool, RP> axisPinned{};
        std::array<size_t, RP> full{};
        for (size_t i = 0; i < NPIN; i++) { axisPinned[pinnedPos[i]] = true; full[pinnedPos[i]] = pinnedVals[i]; }
        size_t freeD = 0;
        for (size_t d = 0; d < RP; d++) if (!axisPinned[d]) { freeD = d; break; }
        std::vector<size_t> ranks;
        for (size_t v = 0; v < pidx->extents[freeD]; v++) {
            full[freeD] = v;
            size_t poff = 0;
            for (size_t d = 0; d < RP; d++) poff = poff * pidx->extents[d] + full[d];
            if (pidx->mask[poff]) ranks.push_back(pidx->linearize(full));
        }
        size_t cnt = ranks.size();
        size_t trail = parent.trailing_stride;
        T* pool = new T[(cnt > 0 ? cnt : 1) * trail];
        T** rows = new T*[cnt > 0 ? cnt : 1];
        for (size_t i = 0; i < cnt; i++) {
            for (size_t t = 0; t < trail; t++)
                pool[i * trail + t] = parent.data[ranks[i] * trail + t];
            rows[i] = pool + i * trail;
        }
        size_t* ext = new size_t[2]{ cnt, trail };
        return Array<T, 2>{ rows, ext };
    }

    // =========================================================================
    // Wrapper-shaped teardown — ONE routine per PRODUCER shape
    // =========================================================================
    //
    // The dense family (deallocate<> / deallocate_strict<> in
    // nested_array_utilities.hpp) can be a single pair of entry points because
    // allocate<> is the single dense producer and its layout is recoverable from
    // (TYPE, SYMM, DIAGONALS, extents). Ragged and Compound have no such
    // property: the SAME wrapper type is handed back by producers with three
    // different ownership stories, and the wrapper stores no ownership bit.
    //
    //   OWNS EVERYTHING   compound(dense, mask), a provider's load_compound:
    //                     fresh compact buffer AND a freshly built index.
    //   OWNS THE DATA     a shape-preserving map over a ragged/compound input:
    //                     fresh pool (+ fresh row table) over BORROWED shape
    //                     metadata — the input's lens/offsets/extents, or the
    //                     input's compound_index_t.
    //   OWNS ONLY GLUE    make_partial_compound / make_partial_window /
    //                     make_partial_window_trail: data is a contiguous SLICE
    //                     of the parent buffer (that is the whole point of the
    //                     lex-sorted layout), and the only fresh allocations are
    //                     the sub-index / extents / row table.
    //   plus the GATHERS  make_partial_compound_gather / _gather_dense /
    //                     _gather_dense_trail: scattered pins cannot alias, so
    //                     these deep-copy and own their buffer as well as glue.
    //
    // So the routines below are named for the PRODUCER, not for the type, and
    // there is exactly one per make_partial_* helper above. Calling the wrong
    // one is a heap error, which is why none of them tries to be clever: each
    // frees precisely the allocations its documented producer made.
    //
    // Every routine takes a NON-CONST reference and nulls what it freed. That
    // costs two stores at a scope exit and makes a duplicated free a no-op
    // (`delete nullptr` / `delete[] nullptr`) instead of heap corruption —
    // cheap insurance in a compiler whose free sites are emitted, not written.
    // Members that were BORROWED are deliberately left intact, so a stale read
    // through them is still a diagnosable use-after-free of the owner rather
    // than of this wrapper.

    // Owning-ragged wrapper: pool + row table, shape metadata BORROWED. The
    // pool is passed explicitly for the reason given on
    // deallocate_ragged_storage (rows[0] is not a reliable pool base).
    // `extents` / `lens` / `offsets` are left untouched — they belong to the
    // input this result was shaped from.
    template<typename T>
    void deallocate_ragged(Ragged<T>& r, T* pool) {
        deallocate_ragged_storage<T>(r.data, pool);
        r.data = nullptr;
    }

    // Owning-ragged wrapper whose rows are each their OWN block (group_by
    // layout). `nrows` is normally `r.extents[0]`, but is taken explicitly
    // because a caller may hold the count when the extents table does not
    // describe the row table's length.
    template<typename T>
    void deallocate_ragged_rows(Ragged<T>& r, size_t nrows) {
        deallocate_ragged_rows_owned<T>(r.data, nrows);
        r.data = nullptr;
    }

    // Compound owning BOTH buffer and index: `compound(dense, mask)` and the
    // provider compound read. NEVER pass a partial/window view here — its data
    // is a slice of the parent buffer and `delete[]` on it is an interior free.
    template<typename T, size_t RANK>
    void deallocate_compound(Compound<T, RANK>& c) {
        deallocate_compound_storage(c.data, c.idx);
        c.data = nullptr;
        c.idx = nullptr;
    }

    // Compound owning ONLY its buffer: the elementwise-map output, which shares
    // the input compound's index (identical mask by construction) and its
    // trailing_stride. The index outlives this result, so it is left alone.
    template<typename T, size_t RANK>
    void deallocate_compound_shared_index(Compound<T, RANK>& c) {
        deallocate_compound_data_only(c.data);
        c.data = nullptr;
    }

    // Compound owning ONLY its index: the make_partial_compound residual. Data
    // is `parent.data + lo * trailing_stride`, a window into the parent's
    // buffer — freeing it corrupts the parent. Must run BEFORE the parent's own
    // teardown only in the sense that it must not run after the parent's buffer
    // is gone AND still be read; the index itself is independent.
    template<typename T, size_t RANK>
    void deallocate_compound_view(Compound<T, RANK>& c) {
        deallocate_compound_index_only(c.idx);
        c.idx = nullptr;
    }

    // make_partial_window residual: Array<T,1> sharing the parent's data, with
    // a heap `size_t[1]` extent as its only allocation.
    template<typename T>
    void deallocate_window_view(Array<T, 1>& a) {
        delete[] a.extents;
        a.extents = nullptr;
    }

    // make_partial_window_trail residual: Array<T,2> whose row table and
    // 2-entry extents are fresh but whose ELEMENTS are the parent's contiguous
    // block. `a.data` is the row table (T**), not the pool, so `delete[] a.data`
    // frees exactly the table.
    template<typename T>
    void deallocate_window_trail_view(Array<T, 2>& a) {
        delete[] a.data;
        delete[] a.extents;
        a.data = nullptr;
        a.extents = nullptr;
    }

    // make_partial_gather_dense residual: Array<T,1> that OWNS both its copied
    // buffer (rank 1, so `a.data` IS the buffer) and its extent.
    template<typename T>
    void deallocate_gather_dense(Array<T, 1>& a) {
        delete[] a.data;
        delete[] a.extents;
        a.data = nullptr;
        a.extents = nullptr;
    }

    // make_partial_gather_dense_trail residual: Array<T,2> owning a fresh pool,
    // its row table, and its extents. The pool is not returned separately, so it
    // is recovered as `a.data[0]` — valid because the producer writes
    // `rows[i] = pool + i * trail` in order, making row 0 the pool base.
    //
    // BOUNDED LEAK, cnt == 0: the producer still allocates a 1-slot pool and
    // table so no pointer is null, but leaves `rows[0]` UNINITIALIZED, so the
    // pool base is unrecoverable. We free the table and extents and leak that
    // one block rather than `delete[]` an indeterminate pointer — the same
    // trade deallocate<> makes for its degenerate total == 0 sentinel. Bound:
    // one `trail * sizeof(T)` block per empty gather, once.
    template<typename T>
    void deallocate_gather_dense_trail(Array<T, 2>& a) {
        if (a.extents && a.extents[0] > 0) delete[] a.data[0];
        delete[] a.data;
        delete[] a.extents;
        a.data = nullptr;
        a.extents = nullptr;
    }

}  // namespace nested_array_utilities
