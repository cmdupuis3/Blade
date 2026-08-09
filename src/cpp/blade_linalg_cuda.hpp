#pragma once
// blade_linalg_cuda.hpp
// Blade DSL Runtime Support Library -- DEVICE (cuBLAS) linear-algebra dispatch.
//
// Round D of docs/plan-cpp-perf-exploitation.md. The device sibling of
// `blade_linalg.hpp`: same routes, same argument lists, same Blade storage
// classes -- a different machine underneath.
//
// -------------------------------------------------------------------------
// THE TWO HALVES OF THIS FILE, AND WHY IT IS ONE FILE
// -------------------------------------------------------------------------
// A Blade program's HOST `.cpp` is compiled by g++ (mingw-w64 on Windows), and
// g++ cannot include `<cuda_runtime.h>` / `<cublas_v2.h>` there -- the CUDA
// headers are an MSVC-only surface on Windows and pull in nvcc's own front-end
// assumptions everywhere. So the boundary is the one the `where cuda` emitters
// already use: DEFINITIONS live in a `.cu` compiled by nvcc, and the host side
// sees only `extern "C"` PROTOTYPES.
//
// Both halves are here, selected by `__CUDACC__`:
//
//   * host arm (g++ / cl, no `__CUDACC__`): 12 `extern "C"` declarations and
//     nothing else. No CUDA header is named, so this costs an ordinary C++
//     compile exactly one file read.
//   * device arm (nvcc, `__CUDACC__` defined): the same 12 entry points as
//     definitions, plus the handle, the transfers and the swap table.
//
// One file rather than two because the SIGNATURES must agree exactly and the
// only way to guarantee that is to write them once. `Build.fs` writes a
// two-line `<stem>_cublas.cu` next to the generated `.cpp` -- it does nothing
// but `#include` this header -- compiles it with nvcc into a shared library, and
// links that library into the g++ host build. (Windows: an MSVC-ABI DLL that
// MinGW links by reading its export table, the proven cross-ABI pattern
// `compileCudaMpiHybrid` already uses; the C-ABI wrapper calls ARE the
// boundary.)
//
// -------------------------------------------------------------------------
// WHAT IS ROUTED HERE, AND WHAT DELIBERATELY IS NOT
// -------------------------------------------------------------------------
// L3 ONLY: `gram(A, A)`, `gram(A, B)` and `matmul(A, B)`. `dot` and `gemv`
// route Native under this backend and that is a decision, not a gap -- see the
// `Gemv`/`Dot` CudaBlas rows in `LinAlgPatterns.policy`. v1 offloads PER CALL
// (device alloc, H2D, kernel, D2H, free), so a shape whose transferred bytes
// and flops are the same order can only lose: L1/L2 move O(n) / O(mn) bytes to
// do O(n) / O(mn) flops, while L3 moves O(mn + nk) bytes to do O(mnk).
//
// The eigensolver sibling is cuSOLVER, not cuBLAS -- a separate library with a
// separate handle and an explicit workspace query. Not v1.
//
// -------------------------------------------------------------------------
// COLUMN-MAJOR: THE SWAP IS A REINTERPRETATION, NOT A TRANSPOSE
// -------------------------------------------------------------------------
// cuBLAS is column-major with no row-major mode. Blade's dense rank-2 pools are
// row-major with ld = trailing extent. The two are the SAME BYTES:
//
//     a row-major m x n pool with ld = n, read column-major with ld = n,
//     IS the n x m matrix A^T.
//
// So nothing is transposed and nothing is copied on the device. Every route
// below computes the COLUMN-MAJOR view of its result, C~ = C^T, out of the
// column-major views of its operands, A~ = A^T and B~ = B^T. Writing C~ into
// the output pool leaves exactly the row-major C the caller asked for.
//
// The general rule for gemm falls straight out of that, and is mechanical:
//
//     cblas row-major (opA, opB, m, n, k, A, lda, B, ldb, C, ldc)
//        ==  cublas   (opB, opA, n, m, k, B, ldb, A, lda, C, ldc)
//
// -- swap the two operands, and each op flag travels WITH its operand; swap
// m and n; keep k and every leading dimension unchanged.
//
// ============================ THE SWAP TABLE =============================
// Every row verified at runtime against the host result on a GTX 1650
// (see the plan's Round D section for the measured deltas). "verified" here
// means values within tolerance of the same route's `blade_linalg.hpp` output,
// NOT byte-identity: different hardware accumulates in a different order.
//
//  route                  Blade (row-major)        cuBLAS call (column-major)
//  ---------------------- ------------------------ ---------------------------
//  matmul_{s,d,c,z}       C(m,n) = A(m,k)*B(k,n)   gemm(N, N, n, m, k,
//                                                       B, ldb=n,
//                                                       A, lda=k,
//                                                       C, ldc=n)
//                         because C~ = B~*A~
//
//  gram_distinct_{s,d}    C(m,p) = A(m,n)*B(p,n)^T gemm(T, N, p, m, n,
//  gram_distinct_{c,z}    C(m,p) = A(m,n)*B(p,n)^H gemm(C, N, p, m, n,
//                                                       B, ldb=n,
//                                                       A, lda=n,
//                                                       C, ldc=p)
//                         because C~ = (B~)^{T|H}*A~, and (B~)^H = conj(B)
//                         is exactly the (B^H)^T the transpose of the product
//                         asks for. The real arm is the same statement with
//                         conjugation dropped.
//
//  gram_same_{s,d}        C(m,m) = A(m,n)*A^T      syrk(LOWER, T, m, n,
//                         upper triangle                A, lda=n, Cfull, ldc=m)
//  gram_same_{c,z}        C(m,m) = A(m,n)*A^H      herk(LOWER, C, m, n,
//                         upper triangle                A, lda=n, Cfull, ldc=m)
//
// ------------------------ THE gram_same TRAP ROW -------------------------
// TWO things flip together here and they are easy to get half right.
//
// (1) THE FILL MODE FLIPS. Row-major element (i, j) of an m x m buffer sits at
//     offset i*m + j; column-major element (r, c) sits at offset c*m + r. So
//     row-major (i, j) IS column-major (j, i), and the row-major UPPER triangle
//     (i <= j) is the column-major LOWER triangle (r >= c) -- the same memory,
//     named differently. Blade wants the upper triangle, so cuBLAS is asked for
//     CUBLAS_FILL_MODE_LOWER. Asking for UPPER would fill the other half and
//     leave the half that gets repacked untouched.
//
// (2) THE OP FLIPS, AND FOR COMPLEX IT CONJUGATES.
//     REAL: C~ = C^T = C (symmetric), and C = A*A^T = (A~)^T*A~, so op = T.
//     COMPLEX: C is HERMITIAN, so C~ = C^T = conj(C) -- NOT C. Expanding,
//         C~ = conj(A*A^H) = conj(A)*A^T = (A~)^H*(A~),
//     which is herk with op = CUBLAS_OP_C.
//
// (3) AND THE TWO CANCEL ON READ-BACK, which is the part that is worth stating
//     because it looks like a missing conjugation. cuBLAS writes C~ = conj(C)
//     into the column-major lower triangle. The repack loop then reads
//     row-major (i, j) for i <= j, which is column-major (j, i) -- inside that
//     written triangle -- and finds
//         C~(j, i) = conj(C)(j, i) = conj(C(j, i)) = conj(conj(C(i, j)))
//                  = C(i, j)
//     using C's own Hermitian symmetry. So the repack is byte-for-byte the host
//     shim's and needs no conj. Binding this to `syrk` instead, or "fixing" the
//     apparent conjugation, both produce plausible output that is wrong -- the
//     same failure mode as `zsyrk`-for-`zherk` one level up. The runtime check
//     against the host result is what makes this a measurement and not an
//     argument.
//
// -------------------------------------------------------------------------
// STAGING AND SKELETONS
// -------------------------------------------------------------------------
// The entry points take Blade ROW SKELETONS (`T**`) plus each operand's
// allocated pool leaf count, EXACTLY as the host adapters do -- the emitter
// spells one argument list for both backends. Resolution to a contiguous host
// buffer reuses `blade_linalg_views.hpp` (the shared contiguity probe and its
// capacity bound, Phase 5d), so a dense Blade pool is handed over with no host
// copy at all and only the H2D transfer is paid.
//
// -------------------------------------------------------------------------
// HANDLE LIFECYCLE
// -------------------------------------------------------------------------
// ONE `cublasHandle_t` per program: a function-local static, created on the
// FIRST routed call (so a program that never dispatches never touches the
// driver) and destroyed during static destruction at exit. Function-local
// statics give thread-safe initialisation for free, and put the resource in the
// shim rather than in a codegen prologue the host path has no analogue of --
// which is why landing this backend required no change to program assembly.
// The destructor deliberately ignores `cublasDestroy`'s status: at static
// destruction time the CUDA runtime may already be tearing down, and a
// diagnostic there would be noise on a program that has already produced its
// output.
//
// -------------------------------------------------------------------------
// FAILURE
// -------------------------------------------------------------------------
// Every CUDA and cuBLAS status is checked, and a failure ABORTS with the API
// name and the status on stderr. It is not recoverable and it must not be
// silent: the alternative is a program that prints an uninitialised pool. A
// build that emits these calls has already declared the device is available.

#include <cstddef>
#include <complex>

#ifndef __CUDACC__
// =========================================================================
// HOST ARM -- declarations only. Compiled by g++ / cl as part of the ordinary
// Blade program. Names no CUDA header, so it adds no dependency to the host
// compile beyond the link against the nvcc-built shim library.
// =========================================================================

extern "C" {

// gram(A, A): A is m x n, C is the m x m PACKED UPPER triangle (Blade's own
// storage -- row i holds logical entries j = i..m-1 at C[i][j - i]).
void blade_cuda_gram_same_s(size_t m, size_t n, float** Arows, size_t Acells, float** Crows);
void blade_cuda_gram_same_d(size_t m, size_t n, double** Arows, size_t Acells, double** Crows);
void blade_cuda_gram_same_c(size_t m, size_t n, std::complex<float>** Arows, size_t Acells, std::complex<float>** Crows);
void blade_cuda_gram_same_z(size_t m, size_t n, std::complex<double>** Arows, size_t Acells, std::complex<double>** Crows);

// gram(A, B): A is m x n, B is p x n, C is a DENSE m x p pool.
void blade_cuda_gram_distinct_s(size_t m, size_t n, size_t p, float** Arows, size_t Acells, float** Brows, size_t Bcells, float** Crows, size_t Ccells);
void blade_cuda_gram_distinct_d(size_t m, size_t n, size_t p, double** Arows, size_t Acells, double** Brows, size_t Bcells, double** Crows, size_t Ccells);
void blade_cuda_gram_distinct_c(size_t m, size_t n, size_t p, std::complex<float>** Arows, size_t Acells, std::complex<float>** Brows, size_t Bcells, std::complex<float>** Crows, size_t Ccells);
void blade_cuda_gram_distinct_z(size_t m, size_t n, size_t p, std::complex<double>** Arows, size_t Acells, std::complex<double>** Brows, size_t Bcells, std::complex<double>** Crows, size_t Ccells);

// matmul(A, B): A is m x k, B is k x n, C is a DENSE m x n pool.
void blade_cuda_matmul_s(size_t m, size_t k, size_t n, float** Arows, size_t Acells, float** Brows, size_t Bcells, float** Crows, size_t Ccells);
void blade_cuda_matmul_d(size_t m, size_t k, size_t n, double** Arows, size_t Acells, double** Brows, size_t Bcells, double** Crows, size_t Ccells);
void blade_cuda_matmul_c(size_t m, size_t k, size_t n, std::complex<float>** Arows, size_t Acells, std::complex<float>** Brows, size_t Bcells, std::complex<float>** Crows, size_t Ccells);
void blade_cuda_matmul_z(size_t m, size_t k, size_t n, std::complex<double>** Arows, size_t Acells, std::complex<double>** Brows, size_t Bcells, std::complex<double>** Crows, size_t Ccells);

} // extern "C"

#else
// =========================================================================
// DEVICE ARM -- compiled by nvcc into the companion `.cu` translation unit.
// =========================================================================

#include <cuda_runtime.h>
#include <cublas_v2.h>
#include <cstdio>
#include <cstdlib>
#include <vector>

#include "blade_linalg_views.hpp"

// Windows: the shim is built as a DLL and the host program links its export
// table directly (MinGW reads DLL exports; the extern "C" boundary is what
// keeps the two ABIs apart). Everywhere else, ordinary default visibility.
#if defined(_WIN32)
#define BLADE_CUDA_API extern "C" __declspec(dllexport)
#else
#define BLADE_CUDA_API extern "C"
#endif

namespace blade_cuda_detail {

    // --------------------------------------------------------------------
    // Failure: loud and terminal. See the header note.
    // --------------------------------------------------------------------
    inline void fail(const char* api, const char* detail, int status) {
        std::fprintf(stderr,
                     "blade: cuBLAS dispatch failed in %s (status %d%s%s)\n",
                     api, status,
                     (detail && *detail) ? ": " : "",
                     (detail && *detail) ? detail : "");
        std::fflush(stderr);
        std::abort();
    }

    inline void ck(cudaError_t e, const char* api) {
        if (e != cudaSuccess) fail(api, cudaGetErrorString(e), (int)e);
    }

    inline void ck(cublasStatus_t s, const char* api) {
        if (s != CUBLAS_STATUS_SUCCESS) fail(api, "", (int)s);
    }

    // --------------------------------------------------------------------
    // The per-program handle. Created on the first routed call, destroyed at
    // exit; see the header note on why it lives here and not in a codegen
    // prologue.
    // --------------------------------------------------------------------
    struct HandleOwner {
        cublasHandle_t h;
        HandleOwner() : h(nullptr) { ck(cublasCreate(&h), "cublasCreate"); }
        ~HandleOwner() { if (h) cublasDestroy(h); }   // status ignored: see note
        HandleOwner(const HandleOwner&) = delete;
        HandleOwner& operator=(const HandleOwner&) = delete;
    };

    inline cublasHandle_t handle() {
        static HandleOwner owner;
        return owner.h;
    }

    // --------------------------------------------------------------------
    // A device buffer with a scope.
    // --------------------------------------------------------------------
    template <typename T>
    struct dbuf {
        void* p;
        explicit dbuf(size_t cells) : p(nullptr) {
            if (cells != 0) ck(cudaMalloc(&p, cells * sizeof(T)), "cudaMalloc");
        }
        ~dbuf() { if (p) cudaFree(p); }
        T* get() const { return static_cast<T*>(p); }
        dbuf(const dbuf&) = delete;
        dbuf& operator=(const dbuf&) = delete;
    };

    template <typename T>
    inline void h2d(T* dst, const T* src, size_t cells) {
        if (cells != 0)
            ck(cudaMemcpy(dst, src, cells * sizeof(T), cudaMemcpyHostToDevice), "cudaMemcpy H2D");
    }

    template <typename T>
    inline void d2h(T* dst, const T* src, size_t cells) {
        if (cells != 0)
            ck(cudaMemcpy(dst, src, cells * sizeof(T), cudaMemcpyDeviceToHost), "cudaMemcpy D2H");
    }

    // --------------------------------------------------------------------
    // Precision dispatch. Spelled out per precision rather than generated,
    // for the reason `blade_linalg.hpp` gives: the cuBLAS signatures are NOT
    // uniform -- `herk` takes REAL alpha/beta (a Hermitian update cannot scale
    // by a complex number and stay Hermitian) where `gemm` takes complex ones
    // by pointer, and the complex arrays cross as `cuComplex` /
    // `cuDoubleComplex`. Those asymmetries are exactly where a wrong binding
    // would hide.
    //
    // `std::complex<T>` and `cuComplex`/`cuDoubleComplex` are both "two T in
    // sequence" -- the standard guarantees the former's layout -- so the casts
    // below are reinterpretations, not conversions.
    //
    // EXTENTS CROSS AS `int`. cuBLAS's classic API takes `int` dimensions and
    // leading dimensions; a Blade extent is `size_t`. The narrowing is checked
    // by `check_int` at the entry points, which aborts rather than silently
    // wrapping a >2^31 extent into a negative dimension.
    // --------------------------------------------------------------------

    inline int check_int(size_t v, const char* what) {
        if (v > 2147483647u) fail("blade_cuda dimension check", what, (int)-1);
        return (int)v;
    }

    // ---- gemm: C = op(A) * op(B), all column-major ----------------------
    inline void gemm(cublasOperation_t ta, cublasOperation_t tb,
                     int m, int n, int k,
                     const float* A, int lda, const float* B, int ldb,
                     float* C, int ldc) {
        const float alpha = 1.0f, beta = 0.0f;
        ck(cublasSgemm(handle(), ta, tb, m, n, k, &alpha, A, lda, B, ldb, &beta, C, ldc), "cublasSgemm");
    }

    inline void gemm(cublasOperation_t ta, cublasOperation_t tb,
                     int m, int n, int k,
                     const double* A, int lda, const double* B, int ldb,
                     double* C, int ldc) {
        const double alpha = 1.0, beta = 0.0;
        ck(cublasDgemm(handle(), ta, tb, m, n, k, &alpha, A, lda, B, ldb, &beta, C, ldc), "cublasDgemm");
    }

    inline void gemm(cublasOperation_t ta, cublasOperation_t tb,
                     int m, int n, int k,
                     const std::complex<float>* A, int lda, const std::complex<float>* B, int ldb,
                     std::complex<float>* C, int ldc) {
        const cuComplex alpha = make_cuComplex(1.0f, 0.0f), beta = make_cuComplex(0.0f, 0.0f);
        ck(cublasCgemm(handle(), ta, tb, m, n, k, &alpha,
                       reinterpret_cast<const cuComplex*>(A), lda,
                       reinterpret_cast<const cuComplex*>(B), ldb, &beta,
                       reinterpret_cast<cuComplex*>(C), ldc), "cublasCgemm");
    }

    inline void gemm(cublasOperation_t ta, cublasOperation_t tb,
                     int m, int n, int k,
                     const std::complex<double>* A, int lda, const std::complex<double>* B, int ldb,
                     std::complex<double>* C, int ldc) {
        const cuDoubleComplex alpha = make_cuDoubleComplex(1.0, 0.0), beta = make_cuDoubleComplex(0.0, 0.0);
        ck(cublasZgemm(handle(), ta, tb, m, n, k, &alpha,
                       reinterpret_cast<const cuDoubleComplex*>(A), lda,
                       reinterpret_cast<const cuDoubleComplex*>(B), ldb, &beta,
                       reinterpret_cast<cuDoubleComplex*>(C), ldc), "cublasZgemm");
    }

    /// The ADJOINT op flag for the second factor of a distinct gram. REAL is a
    /// plain transpose; COMPLEX must conjugate, because Blade's own scalar loop
    /// conjugates B's element (`A[i][k] * conj_scalar(B[j][k])`), i.e. it
    /// computes A*B^H. `CUBLAS_OP_T` at a complex precision would silently
    /// compute a different matrix -- the same trap `CblasConjTrans` answers on
    /// the host side.
    template <typename T> struct adjoint_op;
    template <> struct adjoint_op<float>                { static cublasOperation_t v() { return CUBLAS_OP_T; } };
    template <> struct adjoint_op<double>               { static cublasOperation_t v() { return CUBLAS_OP_T; } };
    template <> struct adjoint_op<std::complex<float>>  { static cublasOperation_t v() { return CUBLAS_OP_C; } };
    template <> struct adjoint_op<std::complex<double>> { static cublasOperation_t v() { return CUBLAS_OP_C; } };

    // ---- rank-k update: syrk (real) / herk (complex) --------------------
    //
    // The op flag is baked in per precision rather than passed, because it is
    // NOT a free parameter: it is the second half of the swap (see the trap row
    // in the header). Real needs `T`, complex needs `C`, and a call site that
    // could choose would be a call site that could choose wrong.
    inline void rank_k(cublasFillMode_t uplo, int n, int k,
                       const float* A, int lda, float* C, int ldc) {
        const float alpha = 1.0f, beta = 0.0f;
        ck(cublasSsyrk(handle(), uplo, CUBLAS_OP_T, n, k, &alpha, A, lda, &beta, C, ldc), "cublasSsyrk");
    }

    inline void rank_k(cublasFillMode_t uplo, int n, int k,
                       const double* A, int lda, double* C, int ldc) {
        const double alpha = 1.0, beta = 0.0;
        ck(cublasDsyrk(handle(), uplo, CUBLAS_OP_T, n, k, &alpha, A, lda, &beta, C, ldc), "cublasDsyrk");
    }

    inline void rank_k(cublasFillMode_t uplo, int n, int k,
                       const std::complex<float>* A, int lda, std::complex<float>* C, int ldc) {
        // herk: REAL alpha/beta.
        const float alpha = 1.0f, beta = 0.0f;
        ck(cublasCherk(handle(), uplo, CUBLAS_OP_C, n, k, &alpha,
                       reinterpret_cast<const cuComplex*>(A), lda, &beta,
                       reinterpret_cast<cuComplex*>(C), ldc), "cublasCherk");
    }

    inline void rank_k(cublasFillMode_t uplo, int n, int k,
                       const std::complex<double>* A, int lda, std::complex<double>* C, int ldc) {
        const double alpha = 1.0, beta = 0.0;
        ck(cublasZherk(handle(), uplo, CUBLAS_OP_C, n, k, &alpha,
                       reinterpret_cast<const cuDoubleComplex*>(A), lda, &beta,
                       reinterpret_cast<cuDoubleComplex*>(C), ldc), "cublasZherk");
    }

    // ====================================================================
    // The three routes, one template each. Identical in shape to their
    // `blade_linalg.hpp` counterparts: resolve the skeletons through the
    // shared views, run one library call, write Blade's storage.
    // ====================================================================

    /// gram(A, A) -> Blade's PACKED UPPER triangle.
    ///
    /// C is staged as a dense m x m buffer for the same unavoidable reason the
    /// host arm stages one: syrk/herk want a square C with a leading dimension,
    /// and Blade's result is a left-justified triangle. The repack loop is
    /// byte-for-byte the host shim's -- see the trap row for why it needs no
    /// conjugation on the complex arm.
    template <typename T>
    void gram_same_impl(size_t m, size_t n, T** Arows, size_t Acells, T** Crows) {
        if (m == 0) return;
        const int mi = check_int(m, "gram_same m");
        const int ni = check_int(n, "gram_same n");
        blade_linalg::in_view<T> A(Arows, m, n, Acells);
        dbuf<T> dA(m * n);
        dbuf<T> dC(m * m);
        h2d(dA.get(), A.p, m * n);
        // beta = 0 means C is not read, so this memset is belt-and-braces; it
        // costs nothing next to the two transfers and keeps the untouched
        // triangle deterministic under a debugger.
        ck(cudaMemset(dC.get(), 0, m * m * sizeof(T)), "cudaMemset");
        if (n != 0)
            rank_k(CUBLAS_FILL_MODE_LOWER, mi, ni, dA.get(), ni, dC.get(), mi);
        std::vector<T> Cfull(m * m);
        d2h(Cfull.data(), dC.get(), m * m);
        for (size_t i = 0; i < m; i++)
            for (size_t jr = 0; jr < m - i; jr++)
                Crows[i][jr] = Cfull[i * m + i + jr];
    }

    /// gram(A, B) -> dense. REAL is A*B^T, COMPLEX is A*B^H.
    template <typename T>
    void gram_distinct_impl(size_t m, size_t n, size_t p,
                            T** Arows, size_t Acells,
                            T** Brows, size_t Bcells,
                            T** Crows, size_t Ccells) {
        if (m == 0 || p == 0) return;
        const int mi = check_int(m, "gram_distinct m");
        const int ni = check_int(n, "gram_distinct n");
        const int pi = check_int(p, "gram_distinct p");
        blade_linalg::in_view<T> A(Arows, m, n, Acells);
        blade_linalg::in_view<T> B(Brows, p, n, Bcells);
        blade_linalg::out_view<T> C(Crows, m, p, Ccells);
        dbuf<T> dA(m * n);
        dbuf<T> dB(p * n);
        dbuf<T> dC(m * p);
        h2d(dA.get(), A.p, m * n);
        h2d(dB.get(), B.p, p * n);
        ck(cudaMemset(dC.get(), 0, m * p * sizeof(T)), "cudaMemset");
        if (n != 0)
            // C~(p x m) = (B~)^{T|H}(p x n) * A~(n x m).
            gemm(adjoint_op<T>::v(), CUBLAS_OP_N, pi, mi, ni,
                 dB.get(), ni, dA.get(), ni, dC.get(), pi);
        d2h(C.p, dC.get(), m * p);
        C.flush();
    }

    /// matmul(A, B) -> dense. Plain product; no transpose, no conjugation.
    template <typename T>
    void matmul_impl(size_t m, size_t k, size_t n,
                     T** Arows, size_t Acells,
                     T** Brows, size_t Bcells,
                     T** Crows, size_t Ccells) {
        if (m == 0 || n == 0) return;
        const int mi = check_int(m, "matmul m");
        const int ki = check_int(k, "matmul k");
        const int ni = check_int(n, "matmul n");
        blade_linalg::in_view<T> A(Arows, m, k, Acells);
        blade_linalg::in_view<T> B(Brows, k, n, Bcells);
        blade_linalg::out_view<T> C(Crows, m, n, Ccells);
        dbuf<T> dA(m * k);
        dbuf<T> dB(k * n);
        dbuf<T> dC(m * n);
        h2d(dA.get(), A.p, m * k);
        h2d(dB.get(), B.p, k * n);
        ck(cudaMemset(dC.get(), 0, m * n * sizeof(T)), "cudaMemset");
        if (k != 0)
            // C~(n x m) = B~(n x k) * A~(k x m).
            gemm(CUBLAS_OP_N, CUBLAS_OP_N, ni, mi, ki,
                 dB.get(), ni, dA.get(), ki, dC.get(), ni);
        d2h(C.p, dC.get(), m * n);
        C.flush();
    }

} // namespace blade_cuda_detail

// =========================================================================
// The 12 entry points. Thin, and deliberately so: everything above is shared
// by all four precisions, and these exist to give the boundary an unmangled
// name per (route x precision) -- the same naming discipline the host shim
// uses, so a mis-dispatch is visible in the EMITTED text rather than only in
// values that would agree to a ULP either way.
// =========================================================================

BLADE_CUDA_API void blade_cuda_gram_same_s(size_t m, size_t n, float** A, size_t Ac, float** C)
{ blade_cuda_detail::gram_same_impl<float>(m, n, A, Ac, C); }
BLADE_CUDA_API void blade_cuda_gram_same_d(size_t m, size_t n, double** A, size_t Ac, double** C)
{ blade_cuda_detail::gram_same_impl<double>(m, n, A, Ac, C); }
BLADE_CUDA_API void blade_cuda_gram_same_c(size_t m, size_t n, std::complex<float>** A, size_t Ac, std::complex<float>** C)
{ blade_cuda_detail::gram_same_impl<std::complex<float>>(m, n, A, Ac, C); }
BLADE_CUDA_API void blade_cuda_gram_same_z(size_t m, size_t n, std::complex<double>** A, size_t Ac, std::complex<double>** C)
{ blade_cuda_detail::gram_same_impl<std::complex<double>>(m, n, A, Ac, C); }

BLADE_CUDA_API void blade_cuda_gram_distinct_s(size_t m, size_t n, size_t p, float** A, size_t Ac, float** B, size_t Bc, float** C, size_t Cc)
{ blade_cuda_detail::gram_distinct_impl<float>(m, n, p, A, Ac, B, Bc, C, Cc); }
BLADE_CUDA_API void blade_cuda_gram_distinct_d(size_t m, size_t n, size_t p, double** A, size_t Ac, double** B, size_t Bc, double** C, size_t Cc)
{ blade_cuda_detail::gram_distinct_impl<double>(m, n, p, A, Ac, B, Bc, C, Cc); }
BLADE_CUDA_API void blade_cuda_gram_distinct_c(size_t m, size_t n, size_t p, std::complex<float>** A, size_t Ac, std::complex<float>** B, size_t Bc, std::complex<float>** C, size_t Cc)
{ blade_cuda_detail::gram_distinct_impl<std::complex<float>>(m, n, p, A, Ac, B, Bc, C, Cc); }
BLADE_CUDA_API void blade_cuda_gram_distinct_z(size_t m, size_t n, size_t p, std::complex<double>** A, size_t Ac, std::complex<double>** B, size_t Bc, std::complex<double>** C, size_t Cc)
{ blade_cuda_detail::gram_distinct_impl<std::complex<double>>(m, n, p, A, Ac, B, Bc, C, Cc); }

BLADE_CUDA_API void blade_cuda_matmul_s(size_t m, size_t k, size_t n, float** A, size_t Ac, float** B, size_t Bc, float** C, size_t Cc)
{ blade_cuda_detail::matmul_impl<float>(m, k, n, A, Ac, B, Bc, C, Cc); }
BLADE_CUDA_API void blade_cuda_matmul_d(size_t m, size_t k, size_t n, double** A, size_t Ac, double** B, size_t Bc, double** C, size_t Cc)
{ blade_cuda_detail::matmul_impl<double>(m, k, n, A, Ac, B, Bc, C, Cc); }
BLADE_CUDA_API void blade_cuda_matmul_c(size_t m, size_t k, size_t n, std::complex<float>** A, size_t Ac, std::complex<float>** B, size_t Bc, std::complex<float>** C, size_t Cc)
{ blade_cuda_detail::matmul_impl<std::complex<float>>(m, k, n, A, Ac, B, Bc, C, Cc); }
BLADE_CUDA_API void blade_cuda_matmul_z(size_t m, size_t k, size_t n, std::complex<double>** A, size_t Ac, std::complex<double>** B, size_t Bc, std::complex<double>** C, size_t Cc)
{ blade_cuda_detail::matmul_impl<std::complex<double>>(m, k, n, A, Ac, B, Bc, C, Cc); }

#endif // __CUDACC__
