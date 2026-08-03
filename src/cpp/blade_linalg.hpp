#pragma once
// blade_linalg.hpp
// Blade DSL Runtime Support Library — dense linear-algebra dispatch layer.
//
// Phase 5 of docs/plan-cpp-perf-exploitation.md. Every dense contraction the
// compiler recognises (today: `gram`, `matmul`) emits ONE call into this
// header instead of an inline loop nest or an inline `cblas_*` call. The
// BLAS-or-native decision then lives in exactly two places that move together:
//
//   * this header's `#ifdef BLADE_HAS_BLAS`, and
//   * Build.fs, which defines `-DBLADE_HAS_BLAS` (plus the include/link flags)
//     exactly when the OpenBLAS resolution succeeds.
//
// Codegen no longer decides. A program that uses a linalg route always emits
// the same text; whether that text becomes `cblas_dgemm` or a native loop is a
// property of the BUILD, not of the source or of an environment variable read
// during code generation.
//
// -------------------------------------------------------------------------
// NUMERICS CONTRACT (load-bearing — do not "optimise" the native fallbacks)
// -------------------------------------------------------------------------
// Blade's interpreter differential (tests/InterpDiff.fs) and pinned-oracle
// differential (tests/DiffOracle.fs) require the compiled binary's printed
// output to be BYTE-IDENTICAL to a second evaluator. The native fallbacks
// therefore reproduce the exact loop structure and floating-point accumulation
// order of the scalar loops these routes replaced:
//
//   for i:  for j:  { double acc = 0.0;
//                     for t ascending: acc += A_element * B_element;
//                     C(i,j) = acc; }
//
// i.e. one local accumulator per output cell, seeded at +0.0, summed over the
// contracted axis in ASCENDING index order, with no blocking, no reordering,
// no partial sums and no FMA-visible restructuring at the source level.
// (`-ffp-contract` is a compiler flag the harnesses pin themselves; see
// Phase 0 of the plan.) `restrict` on the POINTER PARAMETERS is the one
// exploitation that changes no value — GCC honours it there (measured), and it
// is what lets these fallbacks vectorise as well as the inline loops did.
//
// BLAS, when linked, is explicitly ALLOWED to differ in the last ULP: that is
// why the OpenBLAS gate is default-off and why native is the verification
// truth.
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
// fails. The staging copy reads `rows[i][k]` — the identical subscript the
// pre-shim emission used — so a staged operand feeds the arithmetic exactly the
// values the old code did, whatever the operand's storage class.
//
// v1 scope: f64 only. Complex (zherk/zgemm) and f32 (ssyrk/sgemm) keep the
// compiler's scalar loops; L1/L2 routes and packed-symmetric BLAS storage are
// documented follow-ons (see the plan's Phase 5 / Phase 6 sections).

#include <cstddef>
#include <vector>

#ifdef BLADE_HAS_BLAS
#include <cblas.h>
#endif

namespace blade_linalg {

    // ========================================================================
    // Skeleton <-> flat resolution
    // ========================================================================

    /// The contiguity probe. Returns the pool base iff the `m` rows of the
    /// skeleton are EXACTLY the row-major layout `base + i * ld`; otherwise
    /// nullptr, which tells the caller to stage a copy. O(m) against an
    /// O(m*n*k) contraction, so it is free.
    ///
    /// Refusing is always safe; accepting is safe because `base[i * ld + k]`
    /// is then the same object as `rows[i][k]` by construction.
    template <typename T>
    inline T* row_major_base(T** rows, size_t m, size_t ld) {
        if (m == 0 || rows == nullptr) return nullptr;
        T* base = rows[0];
        if (base == nullptr) return nullptr;
        for (size_t i = 1; i < m; i++)
            if (rows[i] != base + i * ld) return nullptr;
        return base;
    }

    /// Copy an m x ld logical window out of a row skeleton into a contiguous
    /// buffer, reading `rows[i][k]` — the same subscript the pre-shim scalar
    /// loops used, so a staged operand is value-identical to what those loops
    /// consumed.
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
    /// skeleton is already row-major contiguous.
    struct in_view {
        std::vector<double> buf;
        const double* p;
        in_view(double** rows, size_t m, size_t ld) {
            double* base = row_major_base(rows, m, ld);
            if (base != nullptr) { p = base; return; }
            buf.resize(m * ld);
            if (m != 0 && ld != 0) stage_in(rows, m, ld, buf.data());
            p = buf.data();
        }
        in_view(const in_view&) = delete;
        in_view& operator=(const in_view&) = delete;
    };

    /// A write-only result resolved to contiguous storage. Zero-copy when the
    /// skeleton is already row-major contiguous (which every FRESH dense output
    /// pool is); otherwise a staging buffer that `flush()` scatters back. The
    /// buffer is NOT read-initialised from the skeleton — the routines below
    /// all overwrite (beta = 0).
    struct out_view {
        std::vector<double> buf;
        double** rows;
        size_t m, ld;
        double* p;
        out_view(double** rows_, size_t m_, size_t ld_)
            : rows(rows_), m(m_), ld(ld_) {
            double* base = row_major_base(rows_, m_, ld_);
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

    // ========================================================================
    // Level-3 cores — BLAS-shaped, flat pointers
    // ========================================================================

    /// C(m x n) = alpha * op(A) * op(B) + beta * C, row-major, leading
    /// dimensions in elements. Forwards to `cblas_dgemm` under the define;
    /// otherwise the contract-preserving native triple loop.
    inline void blade_gemm(bool transA, bool transB,
                           size_t m, size_t n, size_t k,
                           double alpha,
                           const double* __restrict__ A, size_t lda,
                           const double* __restrict__ B, size_t ldb,
                           double beta,
                           double* __restrict__ C, size_t ldc) {
#ifdef BLADE_HAS_BLAS
        cblas_dgemm(CblasRowMajor,
                    transA ? CblasTrans : CblasNoTrans,
                    transB ? CblasTrans : CblasNoTrans,
                    (blasint)m, (blasint)n, (blasint)k,
                    alpha, A, (blasint)lda, B, (blasint)ldb,
                    beta, C, (blasint)ldc);
#else
        // See the NUMERICS CONTRACT above: i, j, t-ascending, one local
        // accumulator seeded at +0.0. transA/transB arrive as literals from the
        // emission site, so the selects fold away after inlining.
        for (size_t i = 0; i < m; i++) {
            for (size_t j = 0; j < n; j++) {
                double acc = 0.0;
                for (size_t t = 0; t < k; t++)
                    acc += (transA ? A[t * lda + i] : A[i * lda + t])
                         * (transB ? B[j * ldb + t] : B[t * ldb + j]);
                double* c = C + i * ldc + j;
                // beta == 0 must not READ C (it may be uninitialised — the BLAS
                // rule), so the two cases are spelled out rather than folded
                // into one `alpha*acc + beta*(*c)`.
                if (beta == 0.0) *c = (alpha == 1.0) ? acc : alpha * acc;
                else             *c = ((alpha == 1.0) ? acc : alpha * acc) + beta * (*c);
            }
        }
#endif
    }

    /// C(n x n), one triangle only, = alpha * A * A^T (trans = false) or
    /// alpha * A^T * A (trans = true), + beta * C. DENSE row-major C with
    /// leading dimension ldc. Forwards to `cblas_dsyrk` under the define.
    ///
    /// v1 emission does not use this entry point directly — `blade_gram_same`
    /// owns the same-array route because Blade's symmetric output is PACKED
    /// (see below) — but it is the documented dispatch target for a future
    /// dense-symmetric output and for the packed-storage routes the plan
    /// sketches, and it is what `blade_gram_same` calls once BLAS is present.
    inline void blade_syrk(bool upper, bool trans,
                           size_t n, size_t k, double alpha,
                           const double* __restrict__ A, size_t lda,
                           double beta,
                           double* __restrict__ C, size_t ldc) {
#ifdef BLADE_HAS_BLAS
        cblas_dsyrk(CblasRowMajor,
                    upper ? CblasUpper : CblasLower,
                    trans ? CblasTrans : CblasNoTrans,
                    (blasint)n, (blasint)k, alpha, A, (blasint)lda,
                    beta, C, (blasint)ldc);
#else
        for (size_t i = 0; i < n; i++) {
            size_t jlo = upper ? i : 0;
            size_t jhi = upper ? n : i + 1;
            for (size_t j = jlo; j < jhi; j++) {
                double acc = 0.0;
                for (size_t t = 0; t < k; t++)
                    acc += (trans ? A[t * lda + i] : A[i * lda + t])
                         * (trans ? A[t * lda + j] : A[j * lda + t]);
                double* c = C + i * ldc + j;
                if (beta == 0.0) *c = (alpha == 1.0) ? acc : alpha * acc;
                else             *c = ((alpha == 1.0) ? acc : alpha * acc) + beta * (*c);
            }
        }
#endif
    }

    // ========================================================================
    // Blade adapters — skeleton in, skeleton out
    // ========================================================================

    /// gram(A, A) = A * A^T for a real m x n operand, written into Blade's
    /// LEFT-JUSTIFIED upper-triangular symmetric storage: row i holds the
    /// logical entries j = i .. m-1 at `C[i][j - i]`. The lower triangle is
    /// recovered lazily on read, so only the triangle is computed.
    ///
    /// The two arms differ in whether C is staged, and deliberately so:
    ///   * BLAS present — `cblas_dsyrk` needs a dense square C with a leading
    ///     dimension, so a full m x m buffer is staged and then repacked. That
    ///     is exactly what the pre-shim inline BLAS branch did.
    ///   * BLAS absent  — the packed triangle is written DIRECTLY, so a
    ///     BLAS-less build does precisely the work (and the accumulation order)
    ///     of the pre-shim scalar loop, with no extra allocation. The six-line
    ///     duplication against `blade_syrk`'s fallback buys that guarantee.
    inline void blade_gram_same(size_t m, size_t n, double** Arows, double** Crows) {
        in_view A(Arows, m, n);
#ifdef BLADE_HAS_BLAS
        std::vector<double> Cfull(m * m);
        blade_syrk(/*upper*/true, /*trans*/false, m, n, 1.0, A.p, n, 0.0, Cfull.data(), m);
        for (size_t i = 0; i < m; i++)
            for (size_t jr = 0; jr < m - i; jr++)
                Crows[i][jr] = Cfull[i * m + i + jr];
#else
        for (size_t i = 0; i < m; i++) {
            const double* __restrict__ ai = A.p + i * n;
            for (size_t jr = 0; jr < m - i; jr++) {
                const double* __restrict__ aj = A.p + (i + jr) * n;
                double acc = 0.0;
                for (size_t t = 0; t < n; t++)
                    acc += ai[t] * aj[t];
                Crows[i][jr] = acc;
            }
        }
#endif
    }

    /// gram(A, B) = A * B^T for distinct real operands: A is m x n, B is p x n,
    /// C is a dense m x p Blade array.
    inline void blade_gram_distinct(size_t m, size_t n, size_t p,
                                    double** Arows, double** Brows, double** Crows) {
        in_view A(Arows, m, n);
        in_view B(Brows, p, n);
        out_view C(Crows, m, p);
        blade_gemm(/*transA*/false, /*transB*/true, m, p, n,
                   1.0, A.p, n, B.p, n, 0.0, C.p, p);
        C.flush();
    }

    /// matmul(A, B) = A * B for real operands: A is m x k, B is k x n, C is a
    /// dense m x n Blade array. The first-class `math.matmul` intrinsic's one
    /// emission target.
    inline void blade_matmul(size_t m, size_t k, size_t n,
                             double** Arows, double** Brows, double** Crows) {
        in_view A(Arows, m, k);
        in_view B(Brows, k, n);
        out_view C(Crows, m, n);
        blade_gemm(/*transA*/false, /*transB*/false, m, n, k,
                   1.0, A.p, k, B.p, n, 0.0, C.p, n);
        C.flush();
    }

} // namespace blade_linalg
