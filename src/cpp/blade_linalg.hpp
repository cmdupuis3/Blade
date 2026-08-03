#pragma once
// blade_linalg.hpp
// Blade DSL Runtime Support Library — dense linear-algebra dispatch layer.
//
// Phases 5 / 5b / 5c of docs/plan-cpp-perf-exploitation.md. Every dense
// contraction the compiler recognises (`gram`, `matmul`, `dot`, `gemv`) emits
// ONE call into this header instead of an inline loop nest or an inline
// `cblas_*` call.
//
// -------------------------------------------------------------------------
// THIS HEADER IS BLAS-ONLY. IT HAS NO NATIVE FALLBACKS. (Phase 5c)
// -------------------------------------------------------------------------
// It compiles ONLY under `-DBLADE_HAS_BLAS`; the guard below makes that a hard
// error rather than a silent divergence. That is not a limitation, it is the
// architecture:
//
//   * The Blade compiler knows AT ITS OWN COMPILE TIME whether BLAS will be
//     available (`LinAlgPatterns.blasAvailable` — the single gate, shared with
//     Build.fs). When it is not, no route is classified as routed, so no call
//     into this header is emitted and the `#include` is never written.
//   * The native math therefore comes from Blade's PRE-EXISTING emission
//     paths: gram's and matmul's own scalar loops, and for dot/gemv the
//     ordinary loop-nest emitters. Those are the paths the interpreter and
//     pinned-oracle differentials have always covered.
//   * So byte-identity between "BLAS off" and the interpreter is STRUCTURAL —
//     there is exactly one copy of the native arithmetic in the whole system,
//     the one the differentials test. Hand-written fallbacks here would be a
//     SECOND copy, and their agreement would be an obligation maintained by
//     discipline rather than by construction.
//
// Seeing this file's `#error` means an emit and a compile disagreed about the
// gate — e.g. a `.cpp` emitted with BLAS on, compiled somewhere with it off.
// Failing loudly is the intent; the alternative is a program that silently
// changes which arithmetic it runs.
//
// BLAS is explicitly ALLOWED to differ in the last ULP, which is why the gate
// is default-OFF and why Blade's own loops remain the verification truth.
//
// -------------------------------------------------------------------------
// LAYOUT / STAGING — see blade_linalg_views.hpp
// -------------------------------------------------------------------------
// The contiguity probe (`row_major_base`) and the two staging views live in
// `blade_linalg_views.hpp`, which this header includes. They are pure pointer
// logic with no BLAS dependency, and they are split out for exactly that
// reason: `cpp/linalg_probe_tests.cpp` exercises them on machines with no BLAS
// at all, while every ENTRY POINT below stays behind the guard. That header
// also carries the LAYOUT CONTRACT and the rectangular-rows PRECONDITION, and
// documents why the probe's `pool_cells` capacity bound is not redundant with
// its row-major geometry test (Phase 5d).
//
// Each adapter below therefore takes, per skeleton operand, that operand's
// allocated pool leaf count. The emitter supplies it (CodeGen's
// `denseCellCountExpr`) because nothing reachable from a `double**` can.
//
// SCOPE: f64 only. Complex (zherk/zgemm) and f32 (ssyrk/sgemm) keep the
// compiler's scalar loops. Entry points, by the plan phase that added them:
//   Phase 5  — blade_gemm / blade_syrk cores; blade_gram_same,
//              blade_gram_distinct, blade_matmul adapters (L3).
//   Phase 5b — blade_dot (L1 reduction), blade_gemv (L2), blade_symv (L2,
//              packed symmetric — layout proven, no surface form reaches it
//              yet; see its own comment).
// Still out of scope: nrm2 (no sqrt-shape matching), axpy/scal (deliberately
// NATIVE — bandwidth-bound, the Phase 3 flat loop already vectorises them), and
// LAPACK (Phase 6).

#include "blade_linalg_views.hpp"

#ifndef BLADE_HAS_BLAS
#error "blade_linalg.hpp requires -DBLADE_HAS_BLAS: the Blade compiler emits native loops when BLAS is unavailable; this call should not have been emitted"
#endif

#include <cstddef>
#include <vector>
#include <cblas.h>

namespace blade_linalg {

    // ========================================================================
    // Level-1 / Level-2 cores — BLAS-shaped, flat pointers
    // ========================================================================

    /// s = seed + x . y  over n elements, unit stride.
    ///
    /// The SEED is a parameter rather than an assumed zero because the call
    /// replaces `reduce`'s fused fold, whose accumulator starts at the `(+)`
    /// identity `0.0` OR at a user `init` (`reduce(c, op, init)`). The gate-off
    /// path — Blade's own fold nest — starts from the same value, so the two
    /// arms agree on what is being computed, and differ only in association
    /// order (which is the whole point of calling BLAS).
    ///
    /// Operands are the pointers the loop path subscripts, not copies: a rank-1
    /// Blade array's `.data` IS its pool, and `Array<T,1>::operator[]` is
    /// `data[i]`.
    inline double blade_dot(size_t n,
                            const double* __restrict__ x,
                            const double* __restrict__ y,
                            double seed) {
        return seed + cblas_ddot((blasint)n, x, 1, y, 1);
    }

    /// y(m) = A(m x n) * x(n), A given as a Blade ROW SKELETON.
    ///
    /// The call replaces a per-row apply whose kernel body is
    /// `prodsum(row, x)`; when the gate is off, that nest is emitted as it
    /// always was (rows ascending, one accumulator per row seeded +0.0,
    /// contracted axis ascending through `A.data[i][t]`).
    ///
    /// BLAS needs contiguous storage, so this runs the same contiguity PROBE
    /// the L3 adapters use and stages only if the probe refuses. A fresh dense
    /// Blade pool always passes, so the common case stages nothing. `Acells` is
    /// A's allocated pool leaf count (see `row_major_base`); `x` and `y` are
    /// rank-1 pools handed over directly, with no skeleton and hence no probe.
    inline void blade_gemv(size_t m, size_t n,
                           double** Arows, size_t Acells,
                           const double* __restrict__ x,
                           double* __restrict__ y) {
        in_view A(Arows, m, n, Acells);
        cblas_dgemv(CblasRowMajor, CblasNoTrans,
                    (blasint)m, (blasint)n, 1.0, A.p, (blasint)n,
                    x, 1, 0.0, y, 1);
    }

    /// y(n) = A(n x n) * x(n) for a SYMMETRIC A held in PACKED storage.
    ///
    /// LAYOUT (verified empirically against the real allocator — see the plan's
    /// Phase 5b section). Blade's rank-2 sym-compact pool, produced by
    /// `allocate<double**, {1,1}>`, holds row i's logical entries j = i..n-1
    /// contiguously, rows in ascending order:
    ///
    ///     ap = A(0,0) A(0,1) ... A(0,n-1) A(1,1) ... A(1,n-1) ... A(n-1,n-1)
    ///
    /// which is EXACTLY cblas's row-major UPPER packed order, cell for cell,
    /// with the diagonal included and no dead entries. The probe checked
    /// n = 1..7: pool order, `count_leaves` cardinality n(n+1)/2, and the row
    /// offsets i*n - i(i-1)/2 all agree. So this route needs ZERO staging —
    /// `pool_base(S.data)` is a valid `AP` argument as it stands.
    ///
    /// NO EMISSION SITE YET, and that is a statement about the SURFACE, not a
    /// gap here: Blade cannot currently express a sym-compact matvec at all.
    /// Peeling a rank-2 compact group into rank-1 fibers is refused at
    /// typecheck (BL4004 — a rank-k compact group is ONE index slot spanning k
    /// dimensions, not a stack of rows), and `reduce`/`prodsum` over compact
    /// storage is refused for the canonical-vs-mirrored fold ambiguity. Once a
    /// surface form exists, this is the route it lands on; until then the entry
    /// point plus the proven layout IS the deliverable.
    ///
    /// (Whatever native path such a surface form eventually gets will be an
    /// EMITTED nest like every other gate-off path, not a fallback here — see
    /// the header comment.)
    inline void blade_symv(size_t n,
                           const double* __restrict__ ap,
                           const double* __restrict__ x,
                           double* __restrict__ y) {
        cblas_dspmv(CblasRowMajor, CblasUpper,
                    (blasint)n, 1.0, ap, x, 1, 0.0, y, 1);
    }

    // ========================================================================
    // Level-3 cores — BLAS-shaped, flat pointers
    // ========================================================================

    /// C(m x n) = alpha * op(A) * op(B) + beta * C, row-major, leading
    /// dimensions in elements.
    inline void blade_gemm(bool transA, bool transB,
                           size_t m, size_t n, size_t k,
                           double alpha,
                           const double* __restrict__ A, size_t lda,
                           const double* __restrict__ B, size_t ldb,
                           double beta,
                           double* __restrict__ C, size_t ldc) {
        cblas_dgemm(CblasRowMajor,
                    transA ? CblasTrans : CblasNoTrans,
                    transB ? CblasTrans : CblasNoTrans,
                    (blasint)m, (blasint)n, (blasint)k,
                    alpha, A, (blasint)lda, B, (blasint)ldb,
                    beta, C, (blasint)ldc);
    }

    /// C(n x n), one triangle only, = alpha * A * A^T (trans = false) or
    /// alpha * A^T * A (trans = true), + beta * C. DENSE row-major C with
    /// leading dimension ldc.
    ///
    /// Emission does not use this entry point directly — `blade_gram_same`
    /// owns the same-array route because Blade's symmetric output is PACKED
    /// (see below) — but it is the documented dispatch target for a future
    /// dense-symmetric output and for the packed-storage routes the plan
    /// sketches, and it is what `blade_gram_same` calls.
    inline void blade_syrk(bool upper, bool trans,
                           size_t n, size_t k, double alpha,
                           const double* __restrict__ A, size_t lda,
                           double beta,
                           double* __restrict__ C, size_t ldc) {
        cblas_dsyrk(CblasRowMajor,
                    upper ? CblasUpper : CblasLower,
                    trans ? CblasTrans : CblasNoTrans,
                    (blasint)n, (blasint)k, alpha, A, (blasint)lda,
                    beta, C, (blasint)ldc);
    }

    // ========================================================================
    // Blade adapters — skeleton in, skeleton out
    // ========================================================================

    /// gram(A, A) = A * A^T for a real m x n operand, written into Blade's
    /// LEFT-JUSTIFIED upper-triangular symmetric storage: row i holds the
    /// logical entries j = i .. m-1 at `C[i][j - i]`. The lower triangle is
    /// recovered lazily on read, so only the triangle is computed.
    ///
    /// C IS STAGED, unavoidably: `cblas_dsyrk` needs a dense square C with a
    /// leading dimension, so a full m x m buffer is written and then repacked
    /// into Blade's left-justified triangle. (This is exactly what the pre-shim
    /// inline BLAS branch did; the gate-off path never gets here — it writes
    /// the packed triangle directly from `materializeGramForm`'s own loops,
    /// with no staging at all.)
    ///
    /// `Acells` is A's allocated pool leaf count (see `row_major_base`). C needs
    /// none: the packed triangle is written through `Crows[i][jr]` with
    /// jr < m-i, which is the packed row's own length, so C never goes through
    /// a view.
    inline void blade_gram_same(size_t m, size_t n, double** Arows, size_t Acells,
                                double** Crows) {
        in_view A(Arows, m, n, Acells);
        std::vector<double> Cfull(m * m);
        blade_syrk(/*upper*/true, /*trans*/false, m, n, 1.0, A.p, n, 0.0, Cfull.data(), m);
        for (size_t i = 0; i < m; i++)
            for (size_t jr = 0; jr < m - i; jr++)
                Crows[i][jr] = Cfull[i * m + i + jr];
    }

    /// gram(A, B) = A * B^T for distinct real operands: A is m x n, B is p x n,
    /// C is a dense m x p Blade array. The `*cells` arguments are each operand's
    /// allocated pool leaf count (see `row_major_base`).
    inline void blade_gram_distinct(size_t m, size_t n, size_t p,
                                    double** Arows, size_t Acells,
                                    double** Brows, size_t Bcells,
                                    double** Crows, size_t Ccells) {
        in_view A(Arows, m, n, Acells);
        in_view B(Brows, p, n, Bcells);
        out_view C(Crows, m, p, Ccells);
        blade_gemm(/*transA*/false, /*transB*/true, m, p, n,
                   1.0, A.p, n, B.p, n, 0.0, C.p, p);
        C.flush();
    }

    /// matmul(A, B) = A * B for real operands: A is m x k, B is k x n, C is a
    /// dense m x n Blade array. The first-class `math.matmul` intrinsic's one
    /// emission target. The `*cells` arguments are each operand's allocated pool
    /// leaf count (see `row_major_base`).
    inline void blade_matmul(size_t m, size_t k, size_t n,
                             double** Arows, size_t Acells,
                             double** Brows, size_t Bcells,
                             double** Crows, size_t Ccells) {
        in_view A(Arows, m, k, Acells);
        in_view B(Brows, k, n, Bcells);
        out_view C(Crows, m, n, Ccells);
        blade_gemm(/*transA*/false, /*transB*/false, m, n, k,
                   1.0, A.p, k, B.p, n, 0.0, C.p, n);
        C.flush();
    }

} // namespace blade_linalg
