#pragma once
// blade_linalg_views.hpp
// Blade DSL Runtime Support Library -- skeleton <-> flat resolution for the
// dense linear-algebra dispatch layer.
//
// Everything here is PURE POINTER LOGIC over `double**` row skeletons: a
// contiguity probe and two staging views. None of it calls, mentions, or
// needs BLAS. Its sibling `blade_linalg.hpp` is the opposite -- every entry
// point there is a cblas call, and that header `#error`s without
// `-DBLADE_HAS_BLAS`. Splitting the two lets `cpp/linalg_probe_tests.cpp` run
// the probe's regression suite on a machine with no BLAS at all, without
// weakening the guarantee that every shim entry point is loudly BLAS-only:
// the preprocessor reads a header to the end whatever the includer wants, so
// an `#error` anywhere in `blade_linalg.hpp` fires for any include of it, and
// no escape-hatch macro can carve out an exception within one file.
//
// LAYOUT CONTRACT. Blade arrays are row-pointer skeletons over a single
// contiguous DFS-ordered pool (nested_array_utilities::allocate). For a
// DENSE rank-2 array `rows[i] == pool + i * trailing_extent`, so the pool
// base can be handed to BLAS directly with `ld = trailing extent` and no
// staging copy. That is not universally true (compact/triangular pools,
// ragged rows, sub-views), so the adapters PROBE for it (`row_major_base`)
// and stage a copy only when the probe fails, reading `rows[i][k]` -- the
// identical subscript Blade's own emission uses.
//
// PRECONDITION -- RECTANGULAR ROWS (both arms, not only the probe). Every
// adapter requires that each of an operand's `m` rows has at least `ld`
// valid cells, a property of the operand's STORAGE CLASS that nothing
// reachable from a `double**` can certify: row pointers give row STARTS,
// never row LENGTHS. The staging fallback is therefore just as out of bounds
// on a non-rectangular operand as a BLAS call would be -- by design, since it
// uses exactly the subscript Blade's own scalar loops use. Blade's
// typechecker enforces the precondition: a compact operand is refused before
// it can reach a linalg route (BL4004).
//
// The probe additionally applies a CAPACITY bound (`pool_cells`) on top of
// the rectangular-rows precondition; see `row_major_base` for why that bound
// is not redundant with the row-major geometry check.

#include <cstddef>
#include <vector>
// BLADE_RESTRICT, used by blade_linalg.hpp's adapter signatures. That header
// includes this one and not nested_array_utilities.hpp, so the macro has to
// arrive here -- and this header is separately compilable (linalg_probe_tests).
#include "blade_portability.hpp"

namespace blade_linalg {

    // Skeleton <-> flat resolution

    /// The contiguity probe. Returns the pool base iff (geometry) the `m`
    /// rows are exactly `base + i * ld`, and (capacity) the pool holds the
    /// `m * ld` cells that layout spans, per the caller-supplied
    /// `pool_cells`; otherwise nullptr, telling the caller to stage a copy.
    /// O(m) against an O(m*n*k) contraction, so it is free. `pool_cells` is
    /// the operand pool's allocated leaf count
    /// (`nested_array_utilities::count_leaves` over its storage class, `m *
    /// ld` when dense); a caller that does not know it passes 0, which
    /// refuses (CodeGen.denseCellCountExpr never guesses).
    ///
    /// The capacity bound is NOT redundant with the geometry: row pointers
    /// give row STARTS, never row LENGTHS, so a pool shorter than the m x ld
    /// window can still lay rows out at exactly `base + i * ld`. The n = 2
    /// packed-symmetric skeleton is that case: row 0 (2 cells) at pool+0, row
    /// 1 (1 cell) at pool+2 in a 3-cell pool satisfies `rows[i] == base + i *
    /// 2` for ld = 2, so geometry alone ACCEPTS -- after which a BLAS call
    /// with m = ld = 2 reads pool[3], one cell past the pool. A dense 2x2 has
    /// identical row starts and 4 cells, so cell count is the only thing that
    /// separates the two (n >= 3 packed pools fail the geometry unaided).
    ///
    /// Refusing is always safe; accepting is safe because `base[i*ld+k]` is
    /// then both the same object as `rows[i][k]` and inside the pool -- but
    /// not necessarily the LOGICAL element (i, k), which is the
    /// rectangular-rows precondition above and this probe cannot check.
    template <typename T>
    inline T* row_major_base(T** rows, size_t m, size_t ld, size_t pool_cells) {
        if (m == 0 || rows == nullptr) return nullptr;
        // Cell-count bound first: cheap scalar test that saves the O(m) walk
        // on the refusing path; cannot overflow since m, ld are extents of
        // an allocated array, so m * ld is at most its dense cell count.
        if (m * ld > pool_cells) return nullptr;
        T* base = rows[0];
        if (base == nullptr) return nullptr;
        for (size_t i = 1; i < m; i++)
            if (rows[i] != base + i * ld) return nullptr;
        return base;
    }

    /// Copy an m x ld logical window from a row skeleton into a contiguous
    /// buffer, reading `rows[i][k]` -- the same subscript Blade's scalar
    /// loops use, so a staged operand is value-identical to those loops.
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

    /// A read-only operand resolved to contiguous storage. Zero-copy when
    /// the skeleton is row-major contiguous over a big-enough pool
    /// (`pool_cells`, see `row_major_base`; 0 when uncertifiable), staged
    /// otherwise. Templated so one implementation serves every BLAS
    /// precision (`float`, `double`, `std::complex<float/double>`).
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

    /// A write-only result resolved to contiguous storage: zero-copy when
    /// the skeleton is row-major contiguous over a big-enough pool (true for
    /// every fresh dense output), else a staging buffer that `flush()`
    /// scatters back. Not read-initialised -- all routines using it
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
