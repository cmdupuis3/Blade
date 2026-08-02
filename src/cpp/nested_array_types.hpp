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

    // Wrapper for a SPARSE array (formalism 3.5): the leading RANK dims are an
    // explicit key enumeration (sparse_index_t), storage is one flat buffer of
    // the cardinality valid elements in KEY order (the order the keys were
    // given, not sorted -- iteration order is key order). Field-for-field twin
    // of Compound, deliberately a separate type rather than a generalization:
    // the two differ in every non-trivial operation (construction, partial
    // indexing, deallocation partners), and a shared template would force every
    // existing Compound emission site through a rename for zero behavioral
    // gain. Trailing regular dims compose exactly as in Compound.
    //
    // Non-owning: `data` and `idx` are caller-allocated; ownership stories are
    // per-producer, mirrored from the Compound taxonomy (see the teardown
    // section below).
    template<typename T, size_t RANK>
    struct Sparse {
        T* data;                              // flat backing, size = cardinality * trailing_stride
        ::sparse_index_t<RANK>* idx;          // non-owning: linearize / unhash / cardinality over the key set
        size_t trailing_stride = 1;           // product of the regular trailing extents; 1 when keys cover all dims

        // Full-key access; a missing key throws (unordered_map::at) -- absent
        // keys have no storage, mirroring the compound missing-cell contract.
        T& operator()(const std::array<size_t, RANK>& key, size_t trail_offset = 0) const {
            return data[idx->linearize(key) * trailing_stride + trail_offset];
        }

        // Trailing-block base pointer for a resolved key (the row sub-view when
        // trailing regular dims exist; see Compound::row for the contract).
        T* row(const std::array<size_t, RANK>& key) const {
            return data + idx->linearize(key) * trailing_stride;
        }

        size_t size() const { return idx->cardinality * trailing_stride; }
    };

    // =========================================================================
    // Sparse partial indexing — always a gather
    // =========================================================================
    //
    // A sparse key table is kept in GIVEN order (see sparse_index_t), so there
    // is no lex-sorted contiguity to exploit and no prefix/window family: every
    // partial (wildcard) index is the deep-copy gather, regardless of whether
    // the pinned axes happen to form a leading prefix. One pass over the
    // parent's entry list in key order keeps the entries whose pinned axes
    // match; the residual keys are those entries' free-axis coordinates, which
    // are automatically distinct (the pinned axes are fixed and full keys are
    // distinct) and inherit the parent's key order. O(cardinality) scan +
    // O(matches * trailing_stride) copy.
    //
    // pinnedVals[i] is the coordinate pinned at parent axis pinnedPos[i];
    // pinnedPos must be strictly increasing (codegen emits it in axis order).

    // Residual rank >= 2: a residual Sparse over the free axes.
    template<typename T, size_t RP, size_t NPIN>
    Sparse<T, RP - NPIN> make_partial_sparse_gather(const Sparse<T, RP>& parent,
                                                    const std::array<size_t, NPIN>& pinnedVals,
                                                    const std::array<size_t, NPIN>& pinnedPos) {
        constexpr size_t RR = RP - NPIN;
        static_assert(RR >= 2, "make_partial_sparse_gather: residual rank must be >= 2 (use make_sparse_gather_dense for rank 1)");
        auto* pidx = parent.idx;
        std::array<bool, RP> axisPinned{};
        for (size_t i = 0; i < NPIN; i++) axisPinned[pinnedPos[i]] = true;
        std::array<size_t, RR> freePos{};
        { size_t f = 0; for (size_t d = 0; d < RP; d++) if (!axisPinned[d]) freePos[f++] = d; }
        std::vector<std::array<size_t, RR>> subkeys;
        std::vector<size_t> ranks;
        for (size_t r = 0; r < pidx->cardinality; r++) {
            const auto& key = pidx->rank_to_tuple[r];
            bool match = true;
            for (size_t i = 0; i < NPIN; i++)
                if (key[pinnedPos[i]] != pinnedVals[i]) { match = false; break; }
            if (!match) continue;
            std::array<size_t, RR> sub{};
            for (size_t d = 0; d < RR; d++) sub[d] = key[freePos[d]];
            subkeys.push_back(sub);
            ranks.push_back(r);
        }
        auto* sidx = new ::sparse_index_t<RR>("__partial_sparse", std::move(subkeys));
        T* buf = new T[(sidx->cardinality > 0 ? sidx->cardinality : 1) * parent.trailing_stride];
        for (size_t i = 0; i < ranks.size(); i++)
            for (size_t t = 0; t < parent.trailing_stride; t++)
                buf[i * parent.trailing_stride + t] = parent.data[ranks[i] * parent.trailing_stride + t];
        return Sparse<T, RR>{ buf, sidx, parent.trailing_stride };
    }

    // Residual rank == 1: the single free axis degenerates to a dense Idx.
    // Result is an Array<T,1> of the matching entries' values in key order,
    // with a heap-allocated extent (match count). trailing_stride == 1 in
    // generated code reaching this helper (trailing dims route to _trail).
    template<typename T, size_t RP, size_t NPIN>
    Array<T, 1> make_sparse_gather_dense(const Sparse<T, RP>& parent,
                                         const std::array<size_t, NPIN>& pinnedVals,
                                         const std::array<size_t, NPIN>& pinnedPos) {
        static_assert(RP - NPIN == 1, "make_sparse_gather_dense: residual rank must be exactly 1");
        auto* pidx = parent.idx;
        std::vector<size_t> ranks;
        for (size_t r = 0; r < pidx->cardinality; r++) {
            const auto& key = pidx->rank_to_tuple[r];
            bool match = true;
            for (size_t i = 0; i < NPIN; i++)
                if (key[pinnedPos[i]] != pinnedVals[i]) { match = false; break; }
            if (match) ranks.push_back(r);
        }
        T* buf = new T[ranks.size() > 0 ? ranks.size() : 1];
        size_t* ext = new size_t[1]{ ranks.size() };
        for (size_t i = 0; i < ranks.size(); i++)
            buf[i] = parent.data[ranks[i] * parent.trailing_stride];
        return Array<T, 1>{ buf, ext };
    }

    // Residual rank == 1 WITH one trailing regular dimension: gather each
    // matching entry's whole trailing block. Result: rank-2 dense {matches,
    // trailing extent}, freshly allocated (contiguous pool + row table).
    template<typename T, size_t RP, size_t NPIN>
    Array<T, 2> make_sparse_gather_dense_trail(const Sparse<T, RP>& parent,
                                               const std::array<size_t, NPIN>& pinnedVals,
                                               const std::array<size_t, NPIN>& pinnedPos) {
        static_assert(RP - NPIN == 1, "make_sparse_gather_dense_trail: residual rank must be exactly 1");
        auto* pidx = parent.idx;
        std::vector<size_t> ranks;
        for (size_t r = 0; r < pidx->cardinality; r++) {
            const auto& key = pidx->rank_to_tuple[r];
            bool match = true;
            for (size_t i = 0; i < NPIN; i++)
                if (key[pinnedPos[i]] != pinnedVals[i]) { match = false; break; }
            if (match) ranks.push_back(r);
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
    //   plus the GATHERS  make_partial_sparse_gather / make_sparse_gather_dense
    //                     / _dense_trail: a gather cannot alias its parent, so
    //                     these deep-copy and own their buffer as well as glue.
    //
    // (The compound partial-view family — make_partial_compound / _window /
    // _window_trail and the compound gathers — was removed with the compound
    // flat-subscript conversion: partial reads are a SparseIdx feature now,
    // and sparse partials are always gathers.)
    //
    // So the routines below are named for the PRODUCER, not for the type, and
    // there is exactly one per producer helper above. Calling the wrong
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

    // Sparse owning BOTH buffer and index: the sparse(values, keys) builder,
    // a provider sparse read, and the make_partial_sparse_gather residual
    // (sparse partials always deep-copy -- there is no view/window shape in
    // the sparse family, so no deallocate_sparse_view exists). The storage
    // helpers are the IDXT-generic compound ones; only the wrapper differs.
    template<typename T, size_t RANK>
    void deallocate_sparse(Sparse<T, RANK>& s) {
        deallocate_compound_storage(s.data, s.idx);
        s.data = nullptr;
        s.idx = nullptr;
    }

    // Sparse owning ONLY its buffer: the elementwise-map output, which shares
    // the input sparse's index (identical key set by construction) and its
    // trailing_stride.
    template<typename T, size_t RANK>
    void deallocate_sparse_shared_index(Sparse<T, RANK>& s) {
        deallocate_compound_data_only(s.data);
        s.data = nullptr;
    }

    // Sparse owning ONLY its index: the range<SparseIdx<keys>> loop driver,
    // whose index is freshly built from the keys array but which never owns a
    // data buffer of its own (the loop writes into a separately allocated
    // output).
    template<typename T, size_t RANK>
    void deallocate_sparse_index_only_wrapper(Sparse<T, RANK>& s) {
        deallocate_compound_index_only(s.idx);
        s.idx = nullptr;
    }

    // Gather-dense residual (make_sparse_gather_dense): Array<T,1> that OWNS both its copied
    // buffer (rank 1, so `a.data` IS the buffer) and its extent.
    template<typename T>
    void deallocate_gather_dense(Array<T, 1>& a) {
        delete[] a.data;
        delete[] a.extents;
        a.data = nullptr;
        a.extents = nullptr;
    }

    // Gather-dense-trail residual (make_sparse_gather_dense_trail): Array<T,2> owning a fresh pool,
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
