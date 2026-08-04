#pragma once
// blade_linalg_views.hpp
// Blade DSL Runtime Support Library — skeleton <-> flat resolution for the
// dense linear-algebra dispatch layer.
//
// -------------------------------------------------------------------------
// WHY THIS IS A SEPARATE HEADER (Phase 5d)
// -------------------------------------------------------------------------
// Everything here is PURE POINTER LOGIC over `double**` row skeletons: a
// contiguity probe and two staging views. None of it calls, mentions, or needs
// BLAS. Its sibling `blade_linalg.hpp` is the opposite — every entry point
// there is a cblas call, and since Phase 5c that header `#error`s without
// `-DBLADE_HAS_BLAS`, because the Blade compiler emits native loops instead of
// calls whenever BLAS is unavailable.
//
// Splitting the two is what lets `cpp/linalg_probe_tests.cpp` run the probe's
// regression suite on a machine with no BLAS at all, WITHOUT weakening the
// guarantee that every shim ENTRY POINT is loudly BLAS-only. A guard cannot
// simply be moved further down inside one file: the preprocessor reads a
// header to the end whatever the includer wants, so an `#error` anywhere in
// `blade_linalg.hpp` fires for any include of it. Two files is the only
// placement that actually separates "helpers usable without BLAS" from "entry
// points that require it", and it needs no escape-hatch macro to do it.
//
// -------------------------------------------------------------------------
// LAYOUT CONTRACT
// -------------------------------------------------------------------------
// Blade arrays are row-pointer skeletons over a single contiguous DFS-ordered
// pool (nested_array_utilities::allocate). For a DENSE rank-2 array that means
// `rows[i] == pool + i * trailing_extent`, so the pool base can be handed to
// BLAS directly with `ld = trailing extent` and NO staging copy. That is not
// universally true (compact/triangular pools, ragged rows, sub-views), so the
// adapters PROBE for it (`row_major_base`) and stage a copy only when the probe
// fails. The staging copy reads `rows[i][k]` — the identical subscript Blade's
// own emission uses — so a staged operand feeds the arithmetic exactly the
// values the loop path would have consumed, whatever the storage class.
//
// -------------------------------------------------------------------------
// PRECONDITION — RECTANGULAR ROWS (both arms, not only the probe)
// -------------------------------------------------------------------------
// Every adapter built on these views requires that each of an operand's `m`
// rows has at least `ld` valid cells, i.e. that its rank-2 storage is
// rectangular. That is a property of the operand's STORAGE CLASS, and nothing
// reachable from a `double**` can certify it: row pointers give row STARTS,
// never row LENGTHS. The staging fallback reads `rows[i][k]` for `k < ld` and
// is therefore just as out of bounds on a compact or row-ragged operand as a
// BLAS call would be — by design, since that subscript is exactly the one
// Blade's own scalar loops use, so this layer inherits their domain, no wider
// and no narrower. Blade's typechecker is what enforces the precondition: a
// compact operand is refused before it can reach a linalg route (BL4004).
//
// What the probe adds ON TOP of that precondition is a CAPACITY bound
// (`pool_cells`, supplied by the emitter from the operand's storage class), and
// it is not redundant: row starts alone can satisfy the row-major geometry over
// a pool that is too small for the `m * ld` window, so without the bound the
// zero-copy arm would hand BLAS a buffer it can read past. See the
// counterexample on `row_major_base` — the n = 2 packed-symmetric skeleton,
// whose row starts are indistinguishable from a dense 2x2's and whose pool is
// one cell shorter.

#include <cstddef>
#include <vector>

namespace blade_linalg {

    // ========================================================================
    // Skeleton <-> flat resolution
    // ========================================================================

    /// The contiguity probe. Returns the pool base iff BOTH
    ///   (geometry) the `m` rows of the skeleton are EXACTLY the row-major
    ///              layout `base + i * ld`, and
    ///   (capacity) the pool holds at least the `m * ld` cells that layout
    ///              spans, per the caller-supplied `pool_cells`;
    /// otherwise nullptr, which tells the caller to stage a copy. O(m) against
    /// an O(m*n*k) contraction, so it is free.
    ///
    /// `pool_cells` is the number of scalar cells the CALLER guarantees are
    /// live at `rows[0]` — the operand pool's allocated leaf count, which for a
    /// Blade array is `nested_array_utilities::count_leaves` over its storage
    /// class (`m * ld` when dense). A caller that does not know the storage
    /// class passes 0, which refuses; the emitter does exactly that rather than
    /// guess (CodeGen.denseCellCountExpr).
    ///
    /// WHY THE CAPACITY BOUND IS NOT REDUNDANT WITH THE GEOMETRY. Row pointers
    /// give row STARTS, never row LENGTHS, so a pool SHORTER than the m x ld
    /// window can still lay its rows out at exactly `base + i * ld`. The n = 2
    /// packed-symmetric skeleton is that case: left-justified upper-triangular
    /// storage puts row 0 (2 cells) at pool+0 and row 1 (1 cell) at pool+2 in a
    /// 3-cell pool, so `rows[i] == base + i * 2` holds for ld = 2 and the
    /// geometry alone ACCEPTS — after which a BLAS call with m = ld = 2 reads
    /// pool[3], one cell past the pool. A dense 2x2 has the identical row
    /// starts and 4 cells, so the cell count is the ONLY thing that separates
    /// the two. (n >= 3 packed pools fail the geometry unaided: n = 3 puts row 2
    /// at pool+5, not pool+6. n = 2 is the sole degenerate size, which is what
    /// made this a latent false accept rather than a visible one.)
    ///
    /// Refusing is always safe; accepting is safe because `base[i * ld + k]`
    /// for i < m, k < ld is then both the same object as `rows[i][k]`
    /// (geometry) and inside the pool (capacity). Note that accepting is safe,
    /// not meaningful: whether `rows[i][k]` is the LOGICAL element (i, k) is the
    /// rectangular-rows precondition documented above, which this probe does
    /// not and cannot check.
    template <typename T>
    inline T* row_major_base(T** rows, size_t m, size_t ld, size_t pool_cells) {
        if (m == 0 || rows == nullptr) return nullptr;
        // Cell-count bound first: it is the cheap scalar test, and on the
        // refusing path it saves the O(m) walk entirely. The multiply cannot
        // overflow for any pool that exists — m and ld are extents of an array
        // that was allocated, so m * ld is at most its dense cell count.
        if (m * ld > pool_cells) return nullptr;
        T* base = rows[0];
        if (base == nullptr) return nullptr;
        for (size_t i = 1; i < m; i++)
            if (rows[i] != base + i * ld) return nullptr;
        return base;
    }

    /// Copy an m x ld logical window out of a row skeleton into a contiguous
    /// buffer, reading `rows[i][k]` — the same subscript Blade's own scalar
    /// loops use, so a staged operand is value-identical to what those loops
    /// consume.
    template <typename T>
    inline void stage_in(T** rows, size_t m, size_t ld, T* dst) {
        for (size_t i = 0; i < m; i++)
            for (size_t k = 0; k < ld; k++)
                dst[i * ld + k] = rows[i][k];
    }

    /// The inverse: scatter a contiguous m x ld buffer back through a row
    /// skeleton.
    template <typename T>
    inline void stage_out(const T* src, size_t m, size_t ld, T** rows) {
        for (size_t i = 0; i < m; i++)
            for (size_t k = 0; k < ld; k++)
                rows[i][k] = src[i * ld + k];
    }

    /// A read-only operand resolved to contiguous storage. Zero-copy when the
    /// skeleton is already row-major contiguous over a pool big enough for the
    /// whole m x ld window; `pool_cells` is the operand's allocated leaf count
    /// (see `row_major_base`), 0 when the caller cannot certify one.
    ///
    /// TEMPLATED ON THE ELEMENT TYPE so the same staging logic serves every
    /// BLAS precision (`float`, `double`, `std::complex<float>`,
    /// `std::complex<double>`). Nothing here is precision-specific — it is
    /// pointer arithmetic and element copies — so one template is the honest
    /// spelling and four copies would be four chances to diverge.
    template <typename T>
    struct in_view {
        std::vector<T> buf;
        const T* p;
        in_view(T** rows, size_t m, size_t ld, size_t pool_cells) {
            T* base = row_major_base(rows, m, ld, pool_cells);
            if (base != nullptr) { p = base; return; }
            buf.resize(m * ld);
            if (m != 0 && ld != 0) stage_in(rows, m, ld, buf.data());
            p = buf.data();
        }
        in_view(const in_view&) = delete;
        in_view& operator=(const in_view&) = delete;
    };

    /// A write-only result resolved to contiguous storage. Zero-copy when the
    /// skeleton is already row-major contiguous over a pool big enough for the
    /// whole m x ld window (which every FRESH dense output pool is); otherwise a
    /// staging buffer that `flush()` scatters back. The buffer is NOT
    /// read-initialised from the skeleton — the routines that use it all
    /// overwrite (beta = 0). `pool_cells` as in `row_major_base`.
    template <typename T>
    struct out_view {
        std::vector<T> buf;
        T** rows;
        size_t m, ld;
        T* p;
        out_view(T** rows_, size_t m_, size_t ld_, size_t pool_cells)
            : rows(rows_), m(m_), ld(ld_) {
            T* base = row_major_base(rows_, m_, ld_, pool_cells);
            if (base != nullptr) { p = base; rows = nullptr; return; }
            buf.resize(m_ * ld_);
            p = buf.data();
        }
        void flush() {
            if (rows != nullptr && m != 0 && ld != 0) stage_out(buf.data(), m, ld, rows);
        }
        out_view(const out_view&) = delete;
        out_view& operator=(const out_view&) = delete;
    };

} // namespace blade_linalg
