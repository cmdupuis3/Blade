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
    // fallback_copy<>: the <|:> allocated-fallback read (formalism 2.6)
    // ========================================================================
    //
    // dst[i...] = a[i...] where a's pointer chain to the cell is fully
    // non-null, else b[i...]. The allocation check runs PER CURRY LEVEL while
    // descending, so a missing subtree (nullptr at any interior level, or a
    // null leaf row) falls back to b's whole corresponding subtree.
    //
    // Compiler-built arrays are fully allocated (allocate<> never leaves a
    // null row), for which this degenerates to a copy of `a`; partially-
    // allocated arrays enter from the C++-level partial-depth allocation API
    // (user-managed sparsity — Blade is not a sparse-tensor system, formalism
    // §9). dst and b must be fully allocated over the shared extents.
    //
    // T and N are explicit at the call site (promote<> in a parameter
    // position is non-deducible): fallback_copy<double, 2>(dst, a, b, ext).
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
    //
    // DIAGONALS (default true): whether a symmetric group keeps its diagonal.
    //   true  -> inclusive (i <= j): symmetric / Hermitian storage.
    //   false -> strict   (i <  j): antisymmetric storage (no diagonal, each
    //            row one shorter). The single difference from the inclusive
    //            recurrence is the child SEED within a group: inclusive passes
    //            `i + lastIndex` (child may equal parent), strict passes
    //            `i + lastIndex + 1` (child must exceed parent). The shorter
    //            rows then fall out automatically from the larger incoming
    //            lastIndex at the next level (bound stays extents-lastIndex).
    //            Verified byte-identical to the former count_antisym at ranks 2-4.
    template<typename TYPE, const size_t SYMM[] = nullptr, bool DIAGONALS = true, const size_t DEPTH = 0>
    constexpr size_t count_leaves(const size_t extents[], const size_t lastIndex = 0) {
        typedef typename std::remove_pointer<TYPE>::type DTYPE;

        size_t n;
        // NOTE (MSVC portability): we must NOT write `(bool)SYMM` directly in an
        // `if constexpr` when SYMM is the address of a function-local
        // `static constexpr` array — MSVC refuses to treat that pointer as a
        // core-constant-expression (error C2131, "unevaluable pointer value"),
        // even when the value is nullptr. Capturing "is there a symmetry array"
        // in a local `constexpr bool` first lets MSVC evaluate the branch
        // condition without demanding the pointer itself be a constant. g++
        // accepted the direct form too, so this is portable.
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

    // Phase C — build_skeleton: build pointer rows (alloc #2); leaf data rows
    // point into `pool`. `offset` is threaded by reference so leaf placement
    // order equals the DFS traversal order (the canonical storage coordinate
    // system that linearize/unlinearize will later have to agree with).
    //
    // DIAGONALS: see count_leaves. false = strict (antisym, diagonal dropped);
    // the only change is the child seed (+1 within a group). The leaf-placement
    // order is byte-identical to the former build_antisym (verified ranks 2-4).
    template<typename TYPE, const size_t SYMM[] = nullptr, bool DIAGONALS = true, const size_t DEPTH = 0>
    TYPE build_skeleton(const size_t extents[],
                        typename strip_ptr<TYPE>::type* pool,
                        size_t& offset,
                        const size_t lastIndex = 0) {
        typedef typename std::remove_pointer<TYPE>::type DTYPE;

        size_t n;
        // MSVC portability: see the note in count_leaves — capture symmetry
        // presence in a constexpr bool rather than `(bool)SYMM` on a possibly
        // function-local-static pointer.
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

    // Top-level entry. Signature compatible with the prior allocate<> (the
    // optional trailing lastIndex is accepted and ignored at the top level;
    // internal recursion threads it). Call sites are unchanged.
    //
    // DIAGONALS (default true): false selects strict (antisymmetric) storage —
    // a single all-grouped SYMM mask {1,1,...} plus DIAGONALS=false reproduces
    // the former allocate_antisym byte-for-byte (count and DFS layout verified
    // at ranks 2-4). Blade always emits a single symmetry group per storage
    // block (the multi-group SYMM machinery is vestigial from Blade's POV but
    // retained for standalone C++ testing).
    template<typename TYPE, const size_t SYMM[] = nullptr, bool DIAGONALS = true, const size_t DEPTH = 0>
    TYPE allocate(const size_t extents[], const size_t /*lastIndex*/ = 0) {
        using SCALAR = typename strip_ptr<TYPE>::type;
        size_t total = count_leaves<TYPE, SYMM, DIAGONALS, 0>(extents, 0);
        // Degenerate total==0 (e.g. strict storage with rank > n): allocate a
        // 1-element pool so a later deref is never on a zero-length buffer.
        SCALAR* pool = new SCALAR[total > 0 ? total : 1];                 // alloc #1
        size_t offset = 0;
        return build_skeleton<TYPE, SYMM, DIAGONALS, 0>(extents, pool, offset, 0);   // alloc #2
    }

    // =========================================================================
    // PER-GROUP-STRICT allocation (mixed strictness across groups)
    // =========================================================================
    //
    // The global `DIAGONALS` flag above is all-or-nothing: every symmetry group
    // in the storage is strict, or none is. That cannot express a layout that is
    // strict in SOME groups and inclusive/dense in others — e.g. the
    // compact-residual decompaction shape
    //     Idx<n> -> AntisymIdx<2,n>      (freed dense axis, strict residual pair)
    //     SYMM = {1,2,2}, per-group strict = {dense, strict}
    // which arises when an antisymmetric group is fissioned and a residual
    // antisymmetric sub-group survives.
    //
    // These overloads take a companion STRICT[] array parallel to SYMM[]:
    // STRICT[d] != 0 means the group at depth d drops its diagonal (i<j); 0
    // means inclusive (i<=j) or dense. The strict offset is keyed at the CURRENT
    // depth's group, so each group's strictness is independent. Strictness only
    // affects the child SEED within a group (the +1 that makes the next
    // coordinate exceed, not equal, the parent); at a group boundary the seed is
    // 0 regardless, so a STRICT flag on a non-grouped (dense / freed) axis is a
    // harmless no-op.
    //
    // Relationship to the global flag (verified): STRICT all-zero reproduces the
    // inclusive (symmetric) count; STRICT all-one on a single all-grouped mask
    // reproduces the global-antisym count. So this is a strict generalization;
    // the existing single-class call sites are unchanged (they keep using the
    // DIAGONALS overload above). Only mixed-strictness outputs use these.
    //
    // Sign is NOT handled here — it lives entirely in the read path (canon_*
    // transform) / the transpose primitive. This allocator is storage-only.

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
        SCALAR* pool = new SCALAR[total > 0 ? total : 1];
        size_t offset = 0;
        return build_skeleton_strict<TYPE, SYMM, STRICT, 0>(extents, pool, offset, 0);
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


    // =========================================================================
    // Antisymmetric array support — UNIFIED into allocate<TYPE, SYMM, false>
    // =========================================================================
    //
    // Antisymmetric storage is no longer a separate code path. It is the unified
    // allocate<>/count_leaves/build_skeleton recurrence driven with DIAGONALS =
    // false (strict simplex: each level's index starts at prev+1, dropping the
    // diagonal) and a single all-grouped SYMM mask {1,1,...}. This yields the
    // documented C(n,r) cardinality and the identical DFS leaf-placement order
    // the former standalone allocate_antisym produced (verified byte-identical at
    // ranks 2-4 against the prior strict recurrence before removal).
    //
    //   allocate<promote<T,r>::type, MASK_all_ones, false>(extents)
    //
    // Antisym is thus "a symmetric grouping that happens to be strict" — same
    // contiguous pool + pointer skeleton, same teardown. The previous
    // count_antisym/build_antisym/allocate_antisym entry points were retired once
    // the unification was confirmed in the test suite.

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
    // Whole-array elementwise transforms (negate / conjugate)
    // =========================================================================
    //
    // These realize the CHEAP intra-group transposes: swapping two dimensions
    // inside one symmetry group is, on storage, a uniform per-scalar transform
    // — antisymmetric -> global negation (any transposition is odd parity),
    // Hermitian -> global conjugation. The transform is STORAGE-SHAPE-INVARIANT:
    // negating/conjugating the canonical element negates/conjugates each of its
    // logical images, so the symmetry relation is preserved without touching the
    // skeleton. Because every array reaching here has compact (symmetry-like)
    // storage, it is one CONTIGUOUS scalar pool in DFS order (allocate<>'s
    // invariant), so the transform is a straight flat loop over the pool — no
    // skeleton traversal, no per-class branching.
    //
    // Type-correctness (which SYMM, the resulting nested view) is handled by the
    // CALLER: it allocates the destination via the normal allocate path (same
    // shape/SYMM as the source, so the result's Blade type is identical) and
    // passes the two pool bases plus the element count. These routines are dumb
    // T*->T* loops with no storage knowledge.

    // conj_scalar: std::conj for complex element types; the identity for reals.
    // (std::conj(double) would return std::complex<double>, breaking assignment
    // back into a real pool — mirrors the IRConj real-vs-complex handling.)
    template<typename T>
    inline T conj_scalar(const T& x) { return x; }                    // real: identity
    template<typename T>
    inline std::complex<T> conj_scalar(const std::complex<T>& x) { return std::conj(x); }

    // negate_pool: dst[i] = -src[i] over the contiguous pool. dst and src share
    // shape (same cardinality n); n is supplied by the caller (count_leaves /
    // count_antisym for the source's storage class).
    template<typename T>
    void negate_pool(T* dst, const T* src, size_t n) {
        for (size_t i = 0; i < n; i++) dst[i] = -src[i];
    }

    // conjugate_pool: dst[i] = conj(src[i]) over the contiguous pool.
    template<typename T>
    void conjugate_pool(T* dst, const T* src, size_t n) {
        for (size_t i = 0; i < n; i++) dst[i] = conj_scalar(src[i]);
    }

    // =========================================================================
    // canon_access: lazy canonicalize-and-transform read of one compact group
    // =========================================================================
    //
    // The runtime half of the lazy-sign-on-read access path (formalism 4.16,
    // 14.2-14.3). Reading a compact-group array at an ARBITRARY index sub-tuple
    // (not necessarily canonical) is three phases, per group:
    //
    //   (1) FOLD       sort the sub-tuple, tracking swap parity. For STRICT
    //                  (antisymmetric) groups, a repeated index means the value
    //                  is not stored -> implicit zero.
    //   (2) LEFT-JUSTIFY  sorted tuple -> storage coords by cumulative
    //                  subtraction; strict groups subtract an extra +k at
    //                  position k (each row one shorter).
    //   (3) TRANSFORM  apply to the fetched canonical value given the parity:
    //                  symmetric -> identity, antisymmetric -> negate on odd,
    //                  Hermitian -> conjugate on odd (conj_scalar is identity on
    //                  reals, so Hermitian-of-real is symmetric automatically).
    //
    // The CALLER supplies the per-group strictness (STRICT) and a transform
    // policy. The read path itself never branches on symmetry class — it folds,
    // fetches, transforms. Verified (canon_access_proto) for antisym ranks 2-4
    // and Hermitian rank 2 (complex + real) against dense references.
    //
    // COST: the fold is O(R^2) inversion counting + a sort, paid only on RANDOM
    // access. Iteration-context reads are canonical by construction and bypass
    // canon_access entirely (the codegen migration distinguishes the two so the
    // bulk-compute hot path stays zero-overhead, per the 14.2 cost-model note).

    // Transform policies. Selected by the caller from the index type's
    // ReadTransformBehavior; share one fold/fetch code path.
    enum class ReadTransform { Identity, NegateOnSwap, ConjugateOnSwap };

    // Fold an R-tuple in place: sort ascending, return swap parity (0 even,
    // 1 odd). For strict groups, set `zero` if any two entries are equal.
    template<size_t R>
    inline int canon_fold(std::array<size_t, R>& idx, bool strict, bool& zero) {
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
