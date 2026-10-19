#pragma once
// nested_array_utilities.hpp
// Blade DSL Runtime Support Library

#include <algorithm>
#include <complex>     // for std::complex / std::conj (conj_scalar, conjugate_pool)
#include <cstddef>
#include <functional>
#include <tuple>
#include <type_traits>
#include <utility>  // for std::index_sequence_for
// BLADE_RESTRICT / BLADE_IVDEP. Included from here (rather than left to the
// generated program's include list) because EVERY generated .cpp reaches this
// header, through one include list or the other, before any emitted loop nest
// -- and so do the standalone cpp/*_tests.cpp harnesses.
#include "blade_portability.hpp"
#include <cstdio>
#include <new>

// ---------------------------------------------------------------------------
// blade_pool -- the retained-block cache behind every scalar pool allocate<>
// makes (bench/notes/microkernel-sketches-review.md M1; plan S7 / 4.5a).
//
// WHY. The UCRT/NT heap hands a freed block of a few hundred KB back to the
// OS, so the next allocation of the same size demand-faults every page again.
// A time-stepping recursive array allocates and frees the same handful of
// row-sized temporaries on every step (bench recur: 8 x 160 KB per step, ~29.5
// faults per allocation, ~91k faults, most of the 117-134 ms), and an isolated
// probe with the buffers kept was 6x faster. Keeping a few freed pools
// COMMITTED and handing one back to the next allocation of the SAME element
// type and cell count removes those faults without changing what any program
// computes.
//
// CONTRACT (what keeps it exact-arithmetic and ownership-neutral):
//   * exact-size reuse only: a pool is handed back to a request for the same
//     element type and the same cell count it was released with. The released
//     count is the one allocate<> passed (`total > 0 ? total : 1`), recomputed
//     by deallocate<> from the same extents, so a hit is never smaller than
//     the request;
//   * contents are NOT cleared, exactly as `new T[n]` leaves them
//     uninitialized for an arithmetic T: every cell the language promises a
//     value for is an explicit store in the emitted code (the same contract
//     the LLVM lane's blade_alloc_cells lives by);
//   * new[] / delete[] pairing is kept per type: a cached block is a live
//     `new T[cells]` array, handed out again as that T, and finally released
//     with that T's delete[] (a typed deleter rides each slot);
//   * arithmetic element types only (`new T[]` of a class type runs
//     constructors, e.g. std::complex zero-initializes);
//   * blocks of 16 KB .. 4 MB; at most 16 slots and 8 MB per THREAD, one
//     budget across all element types (oldest slot evicted first). Both
//     numbers are measured policy (review section 4.4), not semantics;
//   * per-thread (thread_local): no locking, and a block freed on another
//     thread simply lands in that thread's cache (any thread may delete[] a
//     new[] block). A thread's cache is freed at thread exit; after that the
//     thread's releases go straight to delete[];
//   * OFF under BLADE_POOL_CACHE_OFF (Build.fs passes it to every memcheck
//     compile: a cached free is invisible to the leak counter) and under any
//     AddressSanitizer build (a cached free would hide a use-after-free).
//
// BLADE_POOL_CACHE_STATS=1 (test builds only) counts fresh / reused / retained
// blocks process-wide and prints one `[blade-pool] ...` line on stderr at exit.
// ---------------------------------------------------------------------------
#if defined(__SANITIZE_ADDRESS__)
#  ifndef BLADE_POOL_CACHE_OFF
#    define BLADE_POOL_CACHE_OFF 1
#  endif
#elif defined(__has_feature)
#  if __has_feature(address_sanitizer)
#    ifndef BLADE_POOL_CACHE_OFF
#      define BLADE_POOL_CACHE_OFF 1
#    endif
#  endif
#endif
#if defined(BLADE_POOL_CACHE_STATS) && BLADE_POOL_CACHE_STATS
#include <atomic>
#endif

namespace blade_pool {

#if defined(BLADE_POOL_CACHE_STATS) && BLADE_POOL_CACHE_STATS
    // Process-wide counters (atomics: OpenMP workers allocate too).
    //   fresh    -- eligible-size allocations served by new[] (a miss)
    //   reused   -- eligible-size allocations served from the cache (a hit)
    //   retained -- releases kept in the cache instead of delete[]
    //   evicted  -- cached blocks delete[]d to make room or at thread exit
    struct Stats {
        std::atomic<long long> fresh{0}, reused{0}, retained{0}, evicted{0};
        ~Stats() {
            std::fprintf(stderr, "[blade-pool] cache=%s fresh=%lld reused=%lld retained=%lld evicted=%lld\n",
#if defined(BLADE_POOL_CACHE_OFF) && BLADE_POOL_CACHE_OFF
                         "off",
#else
                         "on",
#endif
                         fresh.load(), reused.load(), retained.load(), evicted.load());
        }
    };
    inline Stats g_stats;
#  define BLADE_POOL_COUNT(field) (::blade_pool::g_stats.field.fetch_add(1, std::memory_order_relaxed))
#else
#  define BLADE_POOL_COUNT(field) ((void)0)
#endif

    // Policy constants (bytes). kMaxBlock <= kBudget, so a block that passes
    // the size test always fits once the oldest slots are evicted.
    constexpr size_t kMinBytes = size_t(16) << 10;   // below this the heap's front end keeps pages warm anyway
    constexpr size_t kMaxBlock = size_t(4) << 20;    // a whole trajectory (48 MB) is never retained
    constexpr size_t kBudget   = size_t(8) << 20;    // per thread, ALL element types together
    constexpr size_t kSlots    = 16;

    // Is a pool of `cells` T's in the retained size band? (Overflow-safe: a
    // product that would overflow is far above kMaxBlock.)
    template<class T>
    constexpr bool eligible(size_t cells) {
        return cells >= kMinBytes / sizeof(T) && cells <= kMaxBlock / sizeof(T);
    }

#if defined(BLADE_POOL_CACHE_OFF) && BLADE_POOL_CACHE_OFF
    template<class T> inline T* alloc(size_t cells) {
        if constexpr (std::is_arithmetic<T>::value) { if (eligible<T>(cells)) BLADE_POOL_COUNT(fresh); }
        return new T[cells];
    }
    template<class T> inline void release(T* p, size_t /*cells*/) { delete[] p; }
    inline void flush() {}
#else
    // One type tag per element type: the address of a per-type variable.
    template<class T> struct TypeTag { static inline const char id = 0; };
    template<class T> inline void delete_block(void* p) { delete[] static_cast<T*>(p); }

    struct Slot { void* p; size_t cells; size_t bytes; const char* type; void (*del)(void*); };
    // Trivially destructible and constant-initialized, so it is usable at any
    // point of the thread's life -- including after the Reaper below ran
    // (`dead` then routes every release to delete[]).
    struct Cache { Slot s[kSlots]; size_t n; size_t bytes; bool dead; };
    inline thread_local Cache tl_cache{};

    inline void evict_oldest(Cache& c) {
        c.s[0].del(c.s[0].p);
        c.bytes -= c.s[0].bytes;
        for (size_t m = 1; m < c.n; m++) c.s[m - 1] = c.s[m];
        c.n--;
        BLADE_POOL_COUNT(evicted);
    }
    // Free every block this thread retains (out-of-memory retry; thread exit).
    inline void flush() {
        Cache& c = tl_cache;
        while (c.n > 0) evict_oldest(c);
    }
    // Registered on the first retain of each thread: frees the thread's cache
    // when the thread ends (the main thread's runs at exit, before statics).
    struct Reaper { ~Reaper() { flush(); tl_cache.dead = true; } };

    template<class T> inline T* alloc(size_t cells) {
        if constexpr (std::is_arithmetic<T>::value) {
            if (eligible<T>(cells)) {
                Cache& c = tl_cache;
                const char* type = &TypeTag<T>::id;
                for (size_t k = c.n; k-- > 0; ) {            // newest first: the hottest pages
                    if (c.s[k].type == type && c.s[k].cells == cells) {
                        T* r = static_cast<T*>(c.s[k].p);
                        c.bytes -= c.s[k].bytes;
                        for (size_t m = k + 1; m < c.n; m++) c.s[m - 1] = c.s[m];
                        c.n--;
                        BLADE_POOL_COUNT(reused);
                        return r;
                    }
                }
                BLADE_POOL_COUNT(fresh);
                if (c.n > 0) {
                    // Out of memory with blocks still retained: give them back
                    // and try once more before letting bad_alloc propagate.
                    try { return new T[cells]; }
                    catch (const std::bad_alloc&) { flush(); }
                }
            }
        }
        return new T[cells];
    }

    template<class T> inline void release(T* p, size_t cells) {
        if constexpr (std::is_arithmetic<T>::value) {
            if (p && eligible<T>(cells)) {
                Cache& c = tl_cache;
                if (!c.dead) {
                    static thread_local Reaper reaper;   // constructed (and its destructor registered) once per thread
                    (void)reaper;
                    const size_t b = cells * sizeof(T);
                    while (c.n > 0 && (c.n == kSlots || c.bytes + b > kBudget)) evict_oldest(c);
                    c.s[c.n++] = Slot{ p, cells, b, &TypeTag<T>::id, &delete_block<T> };
                    c.bytes += b;
                    BLADE_POOL_COUNT(retained);
                    return;
                }
            }
        }
        delete[] p;
    }
#endif
} // namespace blade_pool

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

    // strip_ptr<T>: peel all pointer levels to the underlying scalar element type (T*** -> T). Used by the contiguous allocator to type the single backing pool.
    template<typename TYPE, bool IsPtr = std::is_pointer<TYPE>::value>
    struct strip_ptr;
    template<typename TYPE>
    struct strip_ptr<TYPE, false> { using type = TYPE; };
    template<typename TYPE>
    struct strip_ptr<TYPE, true> {
        using type = typename strip_ptr<typename std::remove_pointer<TYPE>::type>::type;
    };

    // pool_base<TYPE>: recover the contiguous backing-pool base pointer from a promote<T,N>::type skeleton. allocate<>
    // places ALL scalars in one pool in DFS order; the first leaf row sits at pool + 0, and interior rows are just
    // pointer arrays into that pool. Descending [0] to the first scalar and taking its address therefore yields the pool
    // base, from which the whole array is contiguous (cardinality elements, DFS order). This is the forward-transform
    // primitive for CUDA streaming: cudaMemcpy(d_buf, pool_base(arr), cardinality * sizeof(T), H2D) copies the entire
    // pool; the inverse uses it as the D2H destination. Rank-generic (compile-time recursion on pointer depth) so
    // generated code calls pool_base(arr.data) regardless of rank.
    //
    // Relies on allocate<>'s single-contiguous-pool invariant; all Blade arrays go through allocate<>, so it holds.
    //
    // FUTURE / RaggedIdx: correct ONLY for closed, contiguously-allocated arrays. A RaggedIdx array is OPEN (per-row
    // materialization via IRRaggedLookup, GC-tracked) and is NOT guaranteed to be one contiguous pool in DFS order, so
    // pool_base must NOT be used for ragged backing. Ragged GPU streaming will need a different forward transform:
    // gather the rows into a contiguous staging buffer first, and carry the per-row lengths across the extern "C"
    // boundary as a separate `const size_t*`.
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

    // fallback_copy<>: the <|:> allocated-fallback read (formalism 2.6).
    //
    // dst[i...] = a[i...] where a's pointer chain to the cell is fully non-null, else b[i...]. The allocation check runs
    // PER CURRY LEVEL while descending, so a missing subtree (nullptr at any interior level, or a null leaf row) falls
    // back to b's whole corresponding subtree.
    //
    // Compiler-built arrays are fully allocated (allocate<> never leaves a null row), for which this degenerates to a
    // copy of `a`; partially-allocated arrays enter from the C++-level partial-depth allocation API (user-managed
    // sparsity -- Blade is not a sparse-tensor system, formalism Sec 9). dst and b must be fully allocated over the shared
    // extents.
    //
    // T and N are explicit at the call site (promote<> in a parameter position is non-deducible):
    // fallback_copy<double, 2>(dst, a, b, ext).
    template<typename T, size_t N>
    void fallback_copy(typename promote<T, N>::type dst,
                       typename promote<T, N>::type a,   // may be null at any level
                       typename promote<T, N>::type b,
                       const size_t* ext) {
        if constexpr (N == 1) {
            for (size_t i = 0; i < ext[0]; i++)
                dst[i] = a ? a[i] : b[i];
        } else {
            for (size_t i = 0; i < ext[0]; i++)
                fallback_copy<T, N - 1>(dst[i],
                                        a ? a[i] : (typename promote<T, N - 1>::type) nullptr,
                                        b[i], ext + 1);
        }
    }

    // allocate<>: contiguous-backing allocation. This is the index metamorphism of formalism Sec 2.7 (double metamorphism
    // with feedback) with the DATA phases (3-5) elided: we run the INDEX loop purely to discover the array's shape,
    // allocating one slot per emitted leaf. index-cata structures the remaining space (the per-level bound shrink);
    // index-ana emits each child slot; the feedback `i + lastIndex` threads the current index to condition the next level.
    //
    // LAYOUT: a single contiguous data pool (alloc #1) holds every scalar element in depth-first iteration order; a
    // pointer skeleton (alloc #2) is laid over it so the nested `arr[i][j][k]` interface is unchanged. The leaf data
    // rows point into `pool + offset`; interior rows are small pointer arrays.
    //
    // PER-LEVEL SPAN: the child count at each level is computed by FORMULA from the prefix indices -- `extents[DEPTH]`
    // (free) or `extents[DEPTH]-lastIndex` (in symmetry group). Valid for rectangular, symmetric, and (via the sibling
    // allocate_antisym) antisymmetric index types. SEAM: tree/graph/compound index types (extensions Sec 2.3-2.4) do NOT
    // have formula-computable per-level spans -- they supply a precomputed subtree-size / offset table and route
    // through a separate placement path. The pool-and-skeleton substrate itself is universal; only this span
    // computation forks.
    //
    // TEARDOWN CONTRACT (see deallocate / deallocate_strict below): the pool is ONE `delete[]`; the skeleton is the
    // interior pointer rows, freed bottom-up. This differs from naive recursive per-leaf frees, which are a
    // HEAP-CORRUPTION bug here: a leaf row is `pool + offset`, a slice of alloc #1 and not a heap block of its own, so
    // `delete[]`-ing it is an interior free (and a double free once the pool itself is released). The teardown must
    // therefore know the same per-level span formula the build used -- hence destroy_skeleton mirrors build_skeleton
    // branch for branch. Wiring generated code to call deallocate at scope exit is the open CodeGen scope-tracker step,
    // so generated programs still do not free.

    // Phase A -- count_leaves: total SCALAR element count under this subtree. Same recursion shape as the skeleton
    // walk, so the count provably matches the allocation traversal (no formula-vs-traversal drift).
    //
    // DIAGONALS (default true): whether a symmetric group keeps its diagonal.
    //   true  -> inclusive (i <= j): symmetric / Hermitian storage.
    //   false -> strict   (i <  j): antisymmetric storage (no diagonal, each row one shorter). The single difference
    //            from the inclusive recurrence is the child SEED within a group: inclusive passes `i + lastIndex`
    //            (child may equal parent), strict passes `i + lastIndex + 1` (child must exceed parent). The shorter
    //            rows then fall out automatically from the larger incoming lastIndex at the next level (bound stays
    //            extents-lastIndex).
    template<typename TYPE, const size_t SYMM[] = nullptr, bool DIAGONALS = true, const size_t DEPTH = 0>
    constexpr size_t count_leaves(const size_t extents[], const size_t lastIndex = 0) {
        typedef typename std::remove_pointer<TYPE>::type DTYPE;

        size_t n;
        // MSVC portability: must NOT write `(bool)SYMM` directly in an `if constexpr` when SYMM is the address of a
        // function-local `static constexpr` array -- MSVC refuses to treat that pointer as a core-constant-expression
        // (error C2131) even when nullptr. Capturing "is there a symmetry array" in a local `constexpr bool` first lets
        // MSVC evaluate the branch without demanding the pointer itself be a constant. g++ accepts the direct form too.
        constexpr bool hasSymm = (SYMM != nullptr);
        // Strict offset: 0 for inclusive (diagonal kept), 1 for strict (dropped).
        constexpr size_t strictOff = DIAGONALS ? 0 : 1;
        if constexpr (hasSymm && DEPTH > 0 && SYMM[DEPTH-1] == SYMM[DEPTH]) {
            n = extents[DEPTH] - lastIndex;
        } else {
            n = extents[DEPTH];
        }

        if constexpr (!std::is_pointer<DTYPE>::value) {
            return n;  // leaf data row contributes n scalars
        } else {
            size_t total = 0;
            for (size_t i = 0; i < n; i++) {
                if constexpr (hasSymm && SYMM[DEPTH] == SYMM[DEPTH + 1]) {
                    if constexpr (hasSymm && DEPTH > 0 && SYMM[DEPTH-1] == SYMM[DEPTH])
                        total += count_leaves<DTYPE, SYMM, DIAGONALS, DEPTH + 1>(extents, i + lastIndex + strictOff);
                    else
                        total += count_leaves<DTYPE, SYMM, DIAGONALS, DEPTH + 1>(extents, i + strictOff);
                } else {
                    total += count_leaves<DTYPE, SYMM, DIAGONALS, DEPTH + 1>(extents, 0);
                }
            }
            return total;
        }
    }

    // Phase C -- build_skeleton: build pointer rows (alloc #2); leaf data rows point into `pool`. `offset` is threaded
    // by reference so leaf placement order equals the DFS traversal order (the canonical storage coordinate system
    // that linearize/unlinearize will later have to agree with).
    //
    // DIAGONALS: see count_leaves. false = strict (antisym, diagonal dropped); the only change is the child seed (+1
    // within a group).
    template<typename TYPE, const size_t SYMM[] = nullptr, bool DIAGONALS = true, const size_t DEPTH = 0>
    TYPE build_skeleton(const size_t extents[],
                        typename strip_ptr<TYPE>::type* pool,
                        size_t& offset,
                        const size_t lastIndex = 0) {
        typedef typename std::remove_pointer<TYPE>::type DTYPE;

        size_t n;
        // MSVC portability: see the note in count_leaves.
        constexpr bool hasSymm = (SYMM != nullptr);
        constexpr size_t strictOff = DIAGONALS ? 0 : 1;
        if constexpr (hasSymm && DEPTH > 0 && SYMM[DEPTH-1] == SYMM[DEPTH]) {
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
                if constexpr (hasSymm && SYMM[DEPTH] == SYMM[DEPTH + 1]) {
                    if constexpr (hasSymm && DEPTH > 0 && SYMM[DEPTH-1] == SYMM[DEPTH])
                        row[i] = build_skeleton<DTYPE, SYMM, DIAGONALS, DEPTH + 1>(extents, pool, offset, i + lastIndex + strictOff);
                    else
                        row[i] = build_skeleton<DTYPE, SYMM, DIAGONALS, DEPTH + 1>(extents, pool, offset, i + strictOff);
                } else {
                    row[i] = build_skeleton<DTYPE, SYMM, DIAGONALS, DEPTH + 1>(extents, pool, offset, 0);
                }
            }
            return row;
        }
    }

    // Top-level entry. Signature compatible with the prior allocate<> (the optional trailing lastIndex is accepted and
    // ignored at the top level; internal recursion threads it).
    //
    // DIAGONALS (default true): false selects strict (antisymmetric) storage -- a single all-grouped SYMM mask
    // {1,1,...} plus DIAGONALS=false reproduces the former allocate_antisym byte-for-byte. Blade always emits a single
    // symmetry group per storage block (the multi-group SYMM machinery is vestigial from Blade's POV but retained for
    // standalone C++ testing).
    template<typename TYPE, const size_t SYMM[] = nullptr, bool DIAGONALS = true, const size_t DEPTH = 0>
    TYPE allocate(const size_t extents[], const size_t /*lastIndex*/ = 0) {
        using SCALAR = typename strip_ptr<TYPE>::type;
        size_t total = count_leaves<TYPE, SYMM, DIAGONALS, 0>(extents, 0);
        // Degenerate total==0 (e.g. strict storage with rank > n): allocate a 1-element pool so a later deref is never on a zero-length buffer.
        // Through the retained-block cache (blade_pool, above); deallocate<> recomputes this same count.
        SCALAR* pool = blade_pool::alloc<SCALAR>(total > 0 ? total : 1);   // alloc #1
        size_t offset = 0;
        return build_skeleton<TYPE, SYMM, DIAGONALS, 0>(extents, pool, offset, 0);   // alloc #2
    }

    // PER-GROUP-STRICT allocation (mixed strictness across groups).
    //
    // The global `DIAGONALS` flag above is all-or-nothing: every symmetry group in the storage is strict, or none is.
    // That cannot express a layout that is strict in SOME groups and inclusive/dense in others -- e.g. the
    // compact-residual decompaction shape
    //     Idx<n> -> AntisymIdx<2,n>      (freed dense axis, strict residual pair)
    //     SYMM = {1,2,2}, per-group strict = {dense, strict}
    // which arises when an antisymmetric group is fissioned and a residual antisymmetric sub-group survives.
    //
    // These overloads take a companion STRICT[] array parallel to SYMM[]: STRICT[d] != 0 means the group at depth d
    // drops its diagonal (i<j); 0 means inclusive (i<=j) or dense. The strict offset is keyed at the CURRENT depth's
    // group, so each group's strictness is independent. Strictness only affects the child SEED within a group (the +1
    // that makes the next coordinate exceed, not equal, the parent); at a group boundary the seed is 0 regardless, so a
    // STRICT flag on a non-grouped (dense / freed) axis is a harmless no-op.
    //
    // Relationship to the global flag (verified): STRICT all-zero reproduces the inclusive (symmetric) count; STRICT
    // all-one on a single all-grouped mask reproduces the global-antisym count -- a strict generalization; existing
    // single-class call sites keep using the DIAGONALS overload above.
    //
    // Sign is NOT handled here -- it lives entirely in the read path (canon_* transform) / the transpose primitive.
    // This allocator is storage-only.

    template<typename TYPE, const size_t SYMM[], const size_t STRICT[], const size_t DEPTH = 0>
    constexpr size_t count_leaves_strict(const size_t extents[], const size_t lastIndex = 0) {
        typedef typename std::remove_pointer<TYPE>::type DTYPE;
        constexpr bool hasSymm = (SYMM != nullptr);
        constexpr size_t strictOff = (STRICT != nullptr && STRICT[DEPTH]) ? 1 : 0;
        size_t n;
        if constexpr (hasSymm && DEPTH > 0 && SYMM[DEPTH-1] == SYMM[DEPTH]) {
            n = extents[DEPTH] - lastIndex;
        } else {
            n = extents[DEPTH];
        }
        if constexpr (!std::is_pointer<DTYPE>::value) {
            return n;
        } else {
            size_t total = 0;
            for (size_t i = 0; i < n; i++) {
                if constexpr (hasSymm && SYMM[DEPTH] == SYMM[DEPTH + 1]) {
                    if constexpr (hasSymm && DEPTH > 0 && SYMM[DEPTH-1] == SYMM[DEPTH])
                        total += count_leaves_strict<DTYPE, SYMM, STRICT, DEPTH + 1>(extents, i + lastIndex + strictOff);
                    else
                        total += count_leaves_strict<DTYPE, SYMM, STRICT, DEPTH + 1>(extents, i + strictOff);
                } else {
                    total += count_leaves_strict<DTYPE, SYMM, STRICT, DEPTH + 1>(extents, 0);
                }
            }
            return total;
        }
    }

    template<typename TYPE, const size_t SYMM[], const size_t STRICT[], const size_t DEPTH = 0>
    TYPE build_skeleton_strict(const size_t extents[],
                               typename strip_ptr<TYPE>::type* pool,
                               size_t& offset,
                               const size_t lastIndex = 0) {
        typedef typename std::remove_pointer<TYPE>::type DTYPE;
        constexpr bool hasSymm = (SYMM != nullptr);
        constexpr size_t strictOff = (STRICT != nullptr && STRICT[DEPTH]) ? 1 : 0;
        size_t n;
        if constexpr (hasSymm && DEPTH > 0 && SYMM[DEPTH-1] == SYMM[DEPTH]) {
            n = extents[DEPTH] - lastIndex;
        } else {
            n = extents[DEPTH];
        }
        if constexpr (!std::is_pointer<DTYPE>::value) {
            TYPE row = pool + offset;
            offset += n;
            return row;
        } else {
            TYPE row = new DTYPE[n];
            for (size_t i = 0; i < n; i++) {
                if constexpr (hasSymm && SYMM[DEPTH] == SYMM[DEPTH + 1]) {
                    if constexpr (hasSymm && DEPTH > 0 && SYMM[DEPTH-1] == SYMM[DEPTH])
                        row[i] = build_skeleton_strict<DTYPE, SYMM, STRICT, DEPTH + 1>(extents, pool, offset, i + lastIndex + strictOff);
                    else
                        row[i] = build_skeleton_strict<DTYPE, SYMM, STRICT, DEPTH + 1>(extents, pool, offset, i + strictOff);
                } else {
                    row[i] = build_skeleton_strict<DTYPE, SYMM, STRICT, DEPTH + 1>(extents, pool, offset, 0);
                }
            }
            return row;
        }
    }

    template<typename TYPE, const size_t SYMM[], const size_t STRICT[]>
    TYPE allocate_strict(const size_t extents[]) {
        using SCALAR = typename strip_ptr<TYPE>::type;
        size_t total = count_leaves_strict<TYPE, SYMM, STRICT, 0>(extents, 0);
        SCALAR* pool = blade_pool::alloc<SCALAR>(total > 0 ? total : 1);
        size_t offset = 0;
        return build_skeleton_strict<TYPE, SYMM, STRICT, 0>(extents, pool, offset, 0);
    }

    // deallocate<> / deallocate_strict<> -- the teardown of allocate<>.
    //
    // The exact inverse of the two allocation families, and ONLY those two: one `delete[]` for the contiguous scalar
    // pool (alloc #1), plus a bottom-up `delete[]` of the interior pointer rows (alloc #2). The leaf `T*` rows are NOT
    // freed -- they are `pool + offset` slices, so freeing them would be an interior free of the pool and then a
    // double free when the pool goes.
    //
    // Because the surviving row set is defined by the per-level span formula (each level's `n` depends on the seed
    // threaded down from its parent), the teardown cannot walk "the whole rectangle" -- it must replay the SAME
    // recurrence the build ran. destroy_skeleton is therefore a branch-for-branch mirror of build_skeleton (and
    // destroy_skeleton_strict of build_skeleton_strict); any divergence frees rows that do not exist or leaks rows
    // that do. Keep them edited in lockstep.
    //
    // ORDER: children first, then the parent row -- the parent still has to be readable to reach its children.
    // `pool_base` likewise must run BEFORE the skeleton is destroyed, since it recovers the pool by walking the [0] spine.

    // destroy_skeleton: free the interior pointer rows of one subtree, bottom-up. Leaf level is a deliberate no-op
    // (pool slice). `lastIndex` is the seed threaded by the caller, exactly as in build_skeleton -- it selects this
    // level's span, hence which children exist.
    template<typename TYPE, const size_t SYMM[] = nullptr, bool DIAGONALS = true, const size_t DEPTH = 0>
    void destroy_skeleton(TYPE skeleton, const size_t extents[], const size_t lastIndex = 0) {
        typedef typename std::remove_pointer<TYPE>::type DTYPE;

        size_t n;
        // MSVC portability + strict offset: see count_leaves / build_skeleton.
        constexpr bool hasSymm = (SYMM != nullptr);
        constexpr size_t strictOff = DIAGONALS ? 0 : 1;
        if constexpr (hasSymm && DEPTH > 0 && SYMM[DEPTH-1] == SYMM[DEPTH]) {
            n = extents[DEPTH] - lastIndex;
        } else {
            n = extents[DEPTH];
        }

        if constexpr (!std::is_pointer<DTYPE>::value) {
            // Leaf data row: a slice of the pool, never a heap block. Nothing to free here; the pool is released once, by deallocate.
            (void)n;
            return;
        } else {
            // Interior pointer row (alloc #2): recurse into the children that the build created, then free this row.
            for (size_t i = 0; i < n; i++) {
                if constexpr (hasSymm && SYMM[DEPTH] == SYMM[DEPTH + 1]) {
                    if constexpr (hasSymm && DEPTH > 0 && SYMM[DEPTH-1] == SYMM[DEPTH])
                        destroy_skeleton<DTYPE, SYMM, DIAGONALS, DEPTH + 1>(skeleton[i], extents, i + lastIndex + strictOff);
                    else
                        destroy_skeleton<DTYPE, SYMM, DIAGONALS, DEPTH + 1>(skeleton[i], extents, i + strictOff);
                } else {
                    destroy_skeleton<DTYPE, SYMM, DIAGONALS, DEPTH + 1>(skeleton[i], extents, 0);
                }
            }
            delete[] skeleton;
        }
    }

    // Top-level teardown. Mirror of allocate<>: same TYPE / SYMM / DIAGONALS, and the same `extents` the allocation was given (passing different extents frees the wrong row set).
    template<typename TYPE, const size_t SYMM[] = nullptr, bool DIAGONALS = true>
    void deallocate(TYPE data, const size_t extents[]) {
        typedef typename std::remove_pointer<TYPE>::type DTYPE;
        using SCALAR = typename strip_ptr<TYPE>::type;

        if constexpr (!std::is_pointer<DTYPE>::value) {
            // Rank 1: there is no skeleton -- build_skeleton returned `pool + 0`, so `data` IS the pool base. One release
            // and we are done (also covers the degenerate extent-0 case); the count is allocate's `total > 0 ? total : 1`.
            if (extents) blade_pool::release(data, extents[0] > 0 ? extents[0] : size_t(1));
            else delete[] data;
        } else {
            size_t total = count_leaves<TYPE, SYMM, DIAGONALS, 0>(extents, 0);
            // Recover the pool BEFORE the skeleton is torn down: pool_base walks the [0] spine, which requires the rows
            // to still be live. The leftmost spine carries the smallest seed and therefore the largest span at every
            // level, so total > 0 guarantees it is walkable and its leaf sits at pool + 0.
            SCALAR* pool = (total > 0) ? pool_base(data) : nullptr;
            destroy_skeleton<TYPE, SYMM, DIAGONALS, 0>(data, extents, 0);
            if (pool) blade_pool::release(pool, total);   // the count allocate<> passed (total > 0 here)
            // BOUNDED LEAK, total == 0 (e.g. strict storage with rank > extent): allocate still made a 1-element
            // sentinel pool so a stray deref is never on a zero-length buffer, but with no leaves there is no [0]
            // spine to walk (skeleton[0] would be out of bounds) and the sentinel's address is unrecoverable from the
            // skeleton alone. We free every skeleton row and intentionally leak that one element rather than read out
            // of bounds to find it. Bound: one sizeof(SCALAR) block per empty array, once.
        }
    }

    // Per-group-strict teardown: mirror of build_skeleton_strict (strictness read at the CURRENT depth from STRICT[], so each group is independent).
    template<typename TYPE, const size_t SYMM[], const size_t STRICT[], const size_t DEPTH = 0>
    void destroy_skeleton_strict(TYPE skeleton, const size_t extents[], const size_t lastIndex = 0) {
        typedef typename std::remove_pointer<TYPE>::type DTYPE;
        constexpr bool hasSymm = (SYMM != nullptr);
        constexpr size_t strictOff = (STRICT != nullptr && STRICT[DEPTH]) ? 1 : 0;
        size_t n;
        if constexpr (hasSymm && DEPTH > 0 && SYMM[DEPTH-1] == SYMM[DEPTH]) {
            n = extents[DEPTH] - lastIndex;
        } else {
            n = extents[DEPTH];
        }
        if constexpr (!std::is_pointer<DTYPE>::value) {
            (void)n;   // leaf row is a pool slice -- nothing to free
            return;
        } else {
            for (size_t i = 0; i < n; i++) {
                if constexpr (hasSymm && SYMM[DEPTH] == SYMM[DEPTH + 1]) {
                    if constexpr (hasSymm && DEPTH > 0 && SYMM[DEPTH-1] == SYMM[DEPTH])
                        destroy_skeleton_strict<DTYPE, SYMM, STRICT, DEPTH + 1>(skeleton[i], extents, i + lastIndex + strictOff);
                    else
                        destroy_skeleton_strict<DTYPE, SYMM, STRICT, DEPTH + 1>(skeleton[i], extents, i + strictOff);
                } else {
                    destroy_skeleton_strict<DTYPE, SYMM, STRICT, DEPTH + 1>(skeleton[i], extents, 0);
                }
            }
            delete[] skeleton;
        }
    }

    // Top-level teardown for allocate_strict. Same total == 0 sentinel-leak semantics as deallocate (see there).
    template<typename TYPE, const size_t SYMM[], const size_t STRICT[]>
    void deallocate_strict(TYPE data, const size_t extents[]) {
        typedef typename std::remove_pointer<TYPE>::type DTYPE;
        using SCALAR = typename strip_ptr<TYPE>::type;

        if constexpr (!std::is_pointer<DTYPE>::value) {
            // rank 1: data IS the pool
            if (extents) blade_pool::release(data, extents[0] > 0 ? extents[0] : size_t(1));
            else delete[] data;
        } else {
            size_t total = count_leaves_strict<TYPE, SYMM, STRICT, 0>(extents, 0);
            SCALAR* pool = (total > 0) ? pool_base(data) : nullptr;
            destroy_skeleton_strict<TYPE, SYMM, STRICT, 0>(data, extents, 0);
            if (pool) blade_pool::release(pool, total);
        }
    }

    // deallocate_ragged* / deallocate_compound* -- teardown for the NON-dense layouts (CSR-ish ragged, masked-product
    // compound). Pointer-level primitives, deliberately NOT one "free this wrapper" entry point: a Ragged<T> or
    // Compound<T,RANK> VALUE says nothing about which of its members it owns (the identical wrapper shape is produced
    // by an owning constructor, a shape-preserving map with BORROWED side tables, or a pure view with everything
    // borrowed). Ownership is knowledge the PRODUCER has and the value does not carry, so the producer names the
    // routine and each routine frees exactly what its name says -- nothing is inferred at runtime.
    //
    // Same doctrine as deallocate<> above: unproven stays leaked. A leak is a bug we already have; a double free -- or
    // a free of storage another wrapper still reads -- is a crash or, worse, silently changed numbers.
    //
    // The wrapper-shaped convenience layer (deallocate_ragged / deallocate_compound / the six view-and-gather
    // routines) lives in nested_array_types.hpp, which is where Ragged / Compound / Array are declared; it delegates
    // to these.

    // Owning-ragged storage: one contiguous element pool plus its row-pointer table, and NOTHING else. This is the
    // shape a shape-preserving ragged producer emits (`<n>__pool = new T[offsets[nrows]]`, `<n>__rows = new
    // T*[nrows]`), whose Ragged<T> wrapper then BORROWS the input's extents / lens / offsets -- freeing those here
    // would kill the input's shape metadata while the input is still live.
    //
    // The rows are pool SLICES (`pool + offsets[g]`), so a per-row `delete[]` would be an interior free and then a
    // double free once the pool goes -- the same hazard destroy_skeleton avoids for dense leaf rows. For the opposite
    // layout (each row its own block) use deallocate_ragged_rows_owned.
    //
    // `pool` is passed EXPLICITLY rather than recovered from `rows[0]`: a zero-row ragged leaves `rows[0]`
    // uninitialized, and a producer that filled its rows in some non-DFS order would leave a different slice there.
    template<typename T>
    void deallocate_ragged_storage(T** rows, T* pool) {
        delete[] pool;   // delete[] nullptr is a no-op
        delete[] rows;
    }

    // Per-row-owned jagged rows: each row is its OWN `new T[len]` block (the group_by layout -- `out[g] = new T[sz]`
    // under a `new T*[ngroups]` table), NOT a slice of a shared pool. Exactly the opposite hazard to
    // deallocate_ragged_storage: here, freeing only the table leaks every row.
    //
    // `nrows` must be the count the producer sized the table with -- the table itself carries no length, so a short
    // count leaks the tail and a long one reads past the end. A null row entry is skipped (`delete[] nullptr`).
    template<typename T>
    void deallocate_ragged_rows_owned(T** rows, size_t nrows) {
        if (!rows) return;
        for (size_t i = 0; i < nrows; i++) delete[] rows[i];
        delete[] rows;
    }

    // Ragged SIDE TABLES, for the producer that heap-allocates them. Opt-in and separate on purpose: every ragged
    // producer in Blade today either SHARES its input's tables or emits them as `static constexpr` / stack arrays, and
    // handing a borrowed or static table to a `delete[]` is a heap abort, not a diagnosable error. Any argument may be
    // null (skipped).
    inline void deallocate_ragged_tables(const size_t* lens,
                                         const size_t* offsets,
                                         const size_t* extents) {
        delete[] lens;
        delete[] offsets;
        delete[] extents;
    }

    // Compound storage: the flat compact buffer plus the index that gives it meaning. IDXT is a template parameter so
    // this layer needs no knowledge of compound_index_t -- that type lives in index_types.h, which sits ABOVE this
    // header; IDXT is deduced at the call site, where it is complete.
    //
    // `delete idx` -- the SINGLE-object form -- is the correct teardown precisely because abstract_idx_t declares a
    // virtual destructor: the derived compound_index_t's rank_to_tuple / tuple_to_rank / mask are reclaimed by the
    // ordinary member-destructor chain even when the static type here is a base pointer. `delete[] idx` would be UB.
    //
    // ONLY for a compound that owns BOTH. A partial/window view aliases its parent's data (see
    // deallocate_compound_index_only); a map output shares its input's index (see deallocate_compound_data_only).
    template<typename T, typename IDXT>
    void deallocate_compound_storage(T* data, IDXT* idx) {
        delete[] data;
        delete idx;
    }

    // Compound data only -- the map-output shape: a fresh compact buffer over an index BORROWED from the input
    // compound (same mask, so the output cannot own it). Freeing the index here would leave the input holding a dangling `.idx`.
    template<typename T>
    void deallocate_compound_data_only(T* data) {
        delete[] data;
    }

    // Compound index only -- the view shape: a residual/window whose DATA is a slice of its parent's buffer (no copy)
    // but whose sub-index was freshly materialized over the free-axis sub-mask. Freeing the data here would be an
    // interior free of the parent's buffer.
    template<typename IDXT>
    void deallocate_compound_index_only(IDXT* idx) {
        delete idx;
    }

    template<typename TYPE, const size_t SYMM[] = nullptr, const size_t DEPTH = 0>
    constexpr void fill_random(TYPE array_in, const size_t extents[], int mod_in, size_t lastIndex = 0) {
        typedef typename std::remove_pointer<TYPE>::type DTYPE;
        constexpr bool hasSymm = (SYMM != nullptr);  // MSVC portability (see count_leaves)

        if constexpr (hasSymm && DEPTH > 0 && SYMM[DEPTH - 1] == SYMM[DEPTH]) {
            if constexpr (std::is_pointer<DTYPE>::value) {
                for (size_t i = 0; i < extents[DEPTH] - lastIndex; i++) {
                    if constexpr (hasSymm && SYMM[DEPTH] == SYMM[DEPTH + 1]) {
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
                    if constexpr (hasSymm && SYMM[DEPTH] == SYMM[DEPTH + 1]) {
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
        constexpr bool hasSymm = (SYMM != nullptr);  // MSVC portability (see count_leaves)

        if constexpr (hasSymm && DEPTH > 0 && SYMM[DEPTH - 1] == SYMM[DEPTH]) {
            if constexpr (std::is_pointer<DTYPE>::value) {
                for (size_t i = 0; i < extents[DEPTH] - lastIndex; i++) {
                    if constexpr (hasSymm && SYMM[DEPTH] == SYMM[DEPTH + 1]) {
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
                    if constexpr (hasSymm && SYMM[DEPTH] == SYMM[DEPTH + 1]) {
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


    // Antisymmetric array support: allocate<TYPE, SYMM, false>. Antisymmetric storage is the same allocate<>/
    // count_leaves/build_skeleton recurrence driven with DIAGONALS = false (strict simplex: each level's index starts
    // at prev+1, dropping the diagonal) and a single all-grouped SYMM mask {1,1,...}, yielding the documented C(n,r)
    // cardinality:
    //
    //   allocate<promote<T,r>::type, MASK_all_ones, false>(extents)
    //
    // Antisym is thus "a symmetric grouping that happens to be strict" -- same contiguous pool + pointer skeleton,
    // same teardown.

    // Index canonicalization wrappers.

    // Symmetric canonicalization: (i,j) -> (min(i,j), max(i,j))
    inline void sym_canonical(size_t i, size_t j, size_t& ci, size_t& cj) {
        ci = (i <= j) ? i : j;
        cj = (i <= j) ? j : i;
    }

    // Antisymmetric canonicalization: (i,j) -> (min(i,j), max(i,j)), sign = +1 or -1. Returns -1 if swapped (odd permutation), +1 if not.
    inline int antisym_canonical(size_t i, size_t j, size_t& ci, size_t& cj) {
        if (i < j) { ci = i; cj = j; return 1; }
        else if (i > j) { ci = j; cj = i; return -1; }
        else { ci = i; cj = j; return 0; }  // diagonal: value is zero
    }

    // Hermitian canonicalization: (i,j) -> (min(i,j), max(i,j)), needs_conj flag. A(i,j) = conj(A(j,i)), so access with j<i needs conjugation.
    inline bool hermitian_canonical(size_t i, size_t j, size_t& ci, size_t& cj) {
        if (i <= j) { ci = i; cj = j; return false; }  // no conjugation needed
        else { ci = j; cj = i; return true; }           // needs conjugation
    }

    // Whole-array elementwise transforms (negate / conjugate). These realize the CHEAP intra-group transposes:
    // swapping two dimensions inside one symmetry group is, on storage, a uniform per-scalar transform --
    // antisymmetric -> global negation (any transposition is odd parity), Hermitian -> global conjugation. The
    // transform is STORAGE-SHAPE-INVARIANT: negating/conjugating the canonical element negates/conjugates each of its
    // logical images, so the symmetry relation is preserved without touching the skeleton. Because every array
    // reaching here has compact storage, it is one CONTIGUOUS scalar pool in DFS order, so the transform is a
    // straight flat loop over the pool -- no skeleton traversal, no per-class branching.
    //
    // Type-correctness (which SYMM, the resulting nested view) is handled by the CALLER: it allocates the destination
    // via the normal allocate path (same shape/SYMM as the source) and passes the two pool bases plus the element
    // count. These routines are dumb T*->T* loops with no storage knowledge.

    // conj_scalar: std::conj for complex element types; the identity for reals. (std::conj(double) would return std::complex<double>, breaking assignment back into a real pool.)
    template<typename T>
    inline T conj_scalar(const T& x) { return x; }                    // real: identity
    template<typename T>
    inline std::complex<T> conj_scalar(const std::complex<T>& x) { return std::conj(x); }

    // negate_pool: dst[i] = -src[i] over the contiguous pool. dst and src share shape (same cardinality n); n is supplied by the caller.
    template<typename T>
    void negate_pool(T* dst, const T* src, size_t n) {
        for (size_t i = 0; i < n; i++) dst[i] = -src[i];
    }

    // conjugate_pool: dst[i] = conj(src[i]) over the contiguous pool.
    template<typename T>
    void conjugate_pool(T* dst, const T* src, size_t n) {
        for (size_t i = 0; i < n; i++) dst[i] = conj_scalar(src[i]);
    }

    // canon_access: lazy canonicalize-and-transform read of one compact group. The runtime half of the
    // lazy-sign-on-read access path (formalism 4.16, 14.2-14.3). Reading a compact-group array at an ARBITRARY index
    // sub-tuple (not necessarily canonical) is three phases, per group:
    //
    //   (1) FOLD       sort the sub-tuple, tracking swap parity. For STRICT (antisymmetric) groups, a repeated index
    //                  means the value is not stored -> implicit zero.
    //   (2) LEFT-JUSTIFY  sorted tuple -> storage coords by cumulative subtraction; strict groups subtract an extra
    //                  +k at position k (each row one shorter).
    //   (3) TRANSFORM  apply to the fetched canonical value given the parity: symmetric -> identity, antisymmetric ->
    //                  negate on odd, Hermitian -> conjugate on odd (conj_scalar is identity on reals, so
    //                  Hermitian-of-real is symmetric automatically).
    //
    // The CALLER supplies the per-group strictness (STRICT) and a transform policy. The read path itself never
    // branches on symmetry class -- it folds, fetches, transforms.
    //
    // COST: the fold is O(R^2) inversion counting + a sort, paid only on RANDOM access. Iteration-context reads are
    // canonical by construction and bypass canon_access entirely, keeping the bulk-compute hot path zero-overhead.

    // Transform policies. Selected by the caller from the index type's ReadTransformBehavior; share one fold/fetch path.
    enum class ReadTransform { Identity, NegateOnSwap, ConjugateOnSwap };

    // Compare-exchange: order (a, b) ascending, return 1 iff it swapped. An exchange of an out-of-order ADJACENT pair
    // removes exactly one inversion, so the XOR of a sorting network's swap flags is the permutation's inversion
    // parity -- the parity the generic fold below counts -- and ties never swap, as that fold never counts them.
    inline int canon_cswap(size_t& a, size_t& b) {
        const int sw = a > b;
        const size_t lo = sw ? b : a, hi = sw ? a : b;   // selects, no branch
        a = lo; b = hi;
        return sw;
    }

    // Fold an R-tuple in place: sort ascending, return swap parity (0 even, 1 odd). For strict groups, set `zero` if any two entries are equal.
    //
    // Ranks 2 and 3 (every compact read the corpus and the bench make) run a fixed compare-exchange network -- one
    // exchange at r = 2, (0,1)(1,2)(0,1) at r = 3, the network the LLVM lane's canonRead emits -- so the read is
    // straight-line integer code with no out-of-line std::sort (bench G3b: the C++ checksum read was 4.9x the LLVM
    // lane's). Same sorted tuple, same parity, same zero flag as the generic path, which higher ranks keep.
    template<size_t R>
    inline int canon_fold(std::array<size_t, R>& idx, bool strict, bool& zero) {
        if constexpr (R == 2) {
            const int p = canon_cswap(idx[0], idx[1]);
            zero = strict && idx[0] == idx[1];
            return zero ? 0 : p;
        } else if constexpr (R == 3) {
            int p = canon_cswap(idx[0], idx[1]);
            p ^= canon_cswap(idx[1], idx[2]);
            p ^= canon_cswap(idx[0], idx[1]);
            zero = strict && (idx[0] == idx[1] || idx[1] == idx[2]);   // sorted: a repeat is adjacent
            return zero ? 0 : p;
        } else {
            zero = false;
            if (strict) {
                for (size_t a = 0; a < R; a++)
                    for (size_t b = a + 1; b < R; b++)
                        if (idx[a] == idx[b]) { zero = true; return 0; }
            }
            int inv = 0;
            for (size_t a = 0; a < R; a++)
                for (size_t b = a + 1; b < R; b++)
                    if (idx[a] > idx[b]) inv++;
            std::sort(idx.begin(), idx.end());
            return inv & 1;
        }
    }

    // FLAT canonical offsets into one packed pool (allocate<>'s DFS order; pool_base gives the base): the position
    // build_skeleton gives each canonical cell, checked exhaustively by cpp/alloc_layout_tests.cpp
    // ("canon_flat_offsets"). NOT the emitted random-access read: the lazy compact read keeps the nested walk
    // `t[c0][c1][c2]` after the network fold above, because the flat form measured SLOWER on the bench shapes
    // (review M2; CodeGenExpr.lazyCompactRead has the numbers) -- two L1-resident row-pointer loads are cheaper than
    // the binomial arithmetic per cell. Kept for code that walks a pool by offset (the planned packed tile drivers,
    // review M5/M8). Preconditions: the coordinates are sorted, and (n + 2)^3 fits a size_t, so no binomial
    // numerator below can overflow (n <= 2,000,000 on 64 bits).
    //
    //   inclusive rank 2, a <= b < n:      a(2n - a + 1)/2 + (b - a)
    //   strict    rank 2, a <  b < n:      a(2n - a - 1)/2 + (b - a - 1)
    //   inclusive rank 3, a <= b <= c < n: T3(n) - T3(n - a) + T2(n - a) - T2(n - b) + (c - b),
    //                                      T2(m) = m(m+1)/2, T3(m) = m(m+1)(m+2)/6
    //   strict    rank 3, a < b < c < n:   the inclusive rank-3 offset of (a, b-1, c-2) at extent n-2 -- the strict
    //                                      simplex IS the inclusive one two smaller (each level's seed is one higher)
    inline size_t canon_t2(size_t m) { return m * (m + 1) / 2; }
    inline size_t canon_t3(size_t m) { return m * (m + 1) * (m + 2) / 6; }
    template<bool STRICT>
    inline size_t canon_off2(size_t a, size_t b, size_t n) {
        if constexpr (STRICT) return a * (2 * n - a - 1) / 2 + (b - a - 1);
        else                  return a * (2 * n - a + 1) / 2 + (b - a);
    }
    inline size_t canon_off3_incl(size_t a, size_t b, size_t c, size_t n) {
        return canon_t3(n) - canon_t3(n - a) + canon_t2(n - a) - canon_t2(n - b) + (c - b);
    }
    template<bool STRICT>
    inline size_t canon_off3(size_t a, size_t b, size_t c, size_t n) {
        if constexpr (STRICT) return canon_off3_incl(a, b - 1, c - 2, n - 2);
        else                  return canon_off3_incl(a, b, c, n);
    }

    // canon_count(m, r, strict): canonical r-tuples over m values -- C(m, r) strict, C(m + r - 1, r) inclusive -- the
    // size of a packed pool, and (as total - canon_count(n - i, ...)) the pool offset where the canonical tuples with
    // leading coordinate i begin, which is how the threaded orbit-class fold (src/OrbitFold.fs, bench G32) hands each
    // thread its rows without walking the rows before them. Step k leaves c = C(top - r + k, k) exactly (so C(top, r)
    // at k = r), and the intermediate product is at most r times the final count.
    inline size_t canon_count(size_t m, size_t r, bool strict) {
        const size_t top = strict ? m : m + r - 1;
        if (top < r) return 0;
        size_t c = 1;
        for (size_t k = 1; k <= r; k++) c = c * (top - r + k) / k;
        return c;
    }

    // Left-justify a sorted R-tuple to storage coords. strict -> extra -k.
    template<size_t R>
    inline std::array<size_t, R> canon_left_justify(const std::array<size_t, R>& p, bool strict) {
        std::array<size_t, R> c{};
        c[0] = p[0];
        for (size_t k = 1; k < R; k++) c[k] = p[k] - p[k-1] - (strict ? 1u : 0u);
        return c;
    }

    // Apply the transform policy to a fetched value given the swap parity.
    template<typename T>
    inline T canon_transform(const T& val, int parity, ReadTransform tf) {
        switch (tf) {
            case ReadTransform::Identity:        return val;
            case ReadTransform::NegateOnSwap:    return parity ? T(-val) : val;
            case ReadTransform::ConjugateOnSwap: return parity ? conj_scalar(val) : val;
        }
        return val;
    }

    // tuple_hasher: std::hash specialization for std::tuple<...>. The standard library does not provide
    // std::hash<std::tuple<...>>. This hasher uses the canonical boost-style hash-combine recipe:
    //   seed ^= hash(elem) + 0x9e3779b9 + (seed << 6) + (seed >> 2)
    // applied across all tuple elements via C++17 fold expression. Used by compound group_keys (multi-key SQL-style
    // grouping) where the bucket dispatch is an unordered_map keyed by a tuple of component key values.
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
