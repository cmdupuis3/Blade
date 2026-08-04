// cublas_swap_tests.cu
// Round D of docs/plan-cpp-perf-exploitation.md -- the runtime verification of
// `blade_linalg_cuda.hpp`'s COLUMN-MAJOR SWAP TABLE.
//
// WHAT THIS PROVES, AND WHY IT HAS TO BE RUNTIME. The swap is an argument about
// memory reinterpretation ("a row-major m x n pool with ld = n IS the
// column-major n x m matrix A^T"), composed per route with transpose and
// conjugation flags, and -- for the same-array gram -- with an upper<->lower fill
// mode flip that interacts with the conjugation. Every one of those is exactly
// the kind of claim that is easy to state, easy to half-implement, and
// impossible to see in the values unless you compare against an independent
// reference: a wrong flag does not crash, it returns a plausible matrix.
//
// So each route is computed TWICE -- once through the shim's device entry point,
// once by a plain host triple loop transcribing Blade's OWN emitted arithmetic
// -- and the two are compared elementwise.
//
// TOLERANCE, NOT BYTE-IDENTITY, and that is not a weakening. cuBLAS accumulates
// in a different order (and on different hardware) than a serial host loop, so
// the last ULP is expected to differ; demanding byte-identity would be
// demanding the GPU reproduce a serial reduction, which no BLAS on any machine
// promises. A SWAP error, by contrast, is never a last-ULP difference: it
// transposes, conjugates or mis-triangulates the result, so it shows up
// thousands of ULPs out. The thresholds below are set far below any wrong-flag
// outcome and far above accumulation noise.
//
// SHAPES ARE DELIBERATELY NON-SQUARE AND ASYMMETRIC (m != n != p != k, values
// that are not symmetric in their indices). Square or symmetric fixtures make a
// transposed result equal to the correct one, i.e. they make the test vacuous
// for precisely the bug it exists to catch.
//
// STORAGE IS BUILT BY HAND rather than through `nested_array_utilities`: this
// probe's subject is the swap, and the shim only ever sees a `T**` row skeleton
// plus a pool capacity. That the real allocator produces those skeletons -- dense
// row-major, and left-justified packed upper for a symmetric result -- is pinned
// separately by `cpp/linalg_probe_tests.cpp` and by the corpus. Keeping the two
// concerns apart keeps a failure here unambiguous.
//
// Build (needs nvcc + a CUDA device; -O2 per the nvcc rule):
//     nvcc -std=c++17 -O2 -o cublas_swap_tests cublas_swap_tests.cu -lcublas
// Run: prints one line per (route x precision) and exits nonzero on any miss.

#include "blade_linalg_cuda.hpp"

#include <cstdio>
#include <cmath>
#include <complex>
#include <vector>

// ---------------------------------------------------------------------------
// Hand-built skeletons
// ---------------------------------------------------------------------------

/// A dense r x c row-major pool plus its row skeleton -- the shape
/// `nested_array_utilities::allocate` produces for a dense rank-2 Blade array.
template <typename T>
struct DensePool {
    std::vector<T> pool;
    std::vector<T*> rows;
    DensePool(size_t r, size_t c) : pool(r * c), rows(r) {
        for (size_t i = 0; i < r; i++) rows[i] = pool.data() + i * c;
    }
    T** skel() { return rows.data(); }
    size_t cells() const { return pool.size(); }
};

/// A LEFT-JUSTIFIED PACKED UPPER triangle: row i holds the logical entries
/// j = i..m-1 at `rows[i][j - i]`, rows ascending, in one m(m+1)/2 pool. This is
/// Blade's own symmetric/Hermitian rank-2 storage, and it is what
/// `blade_cuda_gram_same_*` writes through.
template <typename T>
struct PackedUpperPool {
    std::vector<T> pool;
    std::vector<T*> rows;
    explicit PackedUpperPool(size_t m) : pool(m * (m + 1) / 2), rows(m) {
        size_t off = 0;
        for (size_t i = 0; i < m; i++) { rows[i] = pool.data() + off; off += (m - i); }
    }
    T** skel() { return rows.data(); }
};

// ---------------------------------------------------------------------------
// Element construction and comparison, one spelling per family
// ---------------------------------------------------------------------------

template <typename T> struct elem;

template <> struct elem<float> {
    typedef float value;
    typedef float real;
    static float make(double re, double im) { (void)im; return (float)re; }
    static float conj(float v) { return v; }
    static double dist(float a, float b) { return std::fabs((double)a - (double)b); }
    static const char* letter() { return "s"; }
    static double tol() { return 1e-3; }        // float32: ~1e-7 relative on values ~1e3
};

template <> struct elem<double> {
    typedef double value;
    typedef double real;
    static double make(double re, double im) { (void)im; return re; }
    static double conj(double v) { return v; }
    static double dist(double a, double b) { return std::fabs(a - b); }
    static const char* letter() { return "d"; }
    static double tol() { return 1e-10; }
};

template <> struct elem<std::complex<float>> {
    typedef std::complex<float> value;
    typedef float real;
    static value make(double re, double im) { return value((float)re, (float)im); }
    static value conj(value v) { return std::conj(v); }
    static double dist(value a, value b) { return std::abs(std::complex<double>((double)(a.real() - b.real()), (double)(a.imag() - b.imag()))); }
    static const char* letter() { return "c"; }
    static double tol() { return 1e-3; }
};

template <> struct elem<std::complex<double>> {
    typedef std::complex<double> value;
    typedef double real;
    static value make(double re, double im) { return value(re, im); }
    static value conj(value v) { return std::conj(v); }
    static double dist(value a, value b) { return std::abs(a - b); }
    static const char* letter() { return "z"; }
    static double tol() { return 1e-10; }
};

/// A deterministic, index-ASYMMETRIC fixture value. `f(i, j) != f(j, i)` and the
/// imaginary part is not proportional to the real one, so a transpose, a missing
/// conjugation and a fill-mode error all change the answer.
template <typename T>
static typename elem<T>::value fill(size_t i, size_t j, int salt) {
    double re = 1.0 + 0.5 * (double)i - 0.25 * (double)j + 0.125 * (double)salt;
    double im = -0.75 + 0.375 * (double)j - 0.0625 * (double)i * (double)i;
    return elem<T>::make(re, im);
}

static int g_failed = 0;
static int g_total = 0;

static void report(const char* route, const char* letter, double worst, double tol) {
    bool ok = (worst <= tol) && !(worst != worst);   // NaN fails
    g_total++;
    if (!ok) g_failed++;
    std::printf("  [%s]: %s_%s   max|device - host| = %.3e   (tol %.1e)\n",
                ok ? "PASS" : "FAIL", route, letter, worst, tol);
}

// ---------------------------------------------------------------------------
// Route 1 -- matmul.  C(m x n) = A(m x k) * B(k x n), dense, no conjugation.
// ---------------------------------------------------------------------------
template <typename T>
static void check_matmul(size_t m, size_t k, size_t n,
                         void (*entry)(size_t, size_t, size_t, T**, size_t, T**, size_t, T**, size_t)) {
    typedef typename elem<T>::value V;
    DensePool<T> A(m, k), B(k, n), C(m, n);
    for (size_t i = 0; i < m; i++) for (size_t t = 0; t < k; t++) A.rows[i][t] = fill<T>(i, t, 0);
    for (size_t t = 0; t < k; t++) for (size_t j = 0; j < n; j++) B.rows[t][j] = fill<T>(t, j, 3);

    entry(m, k, n, A.skel(), A.cells(), B.skel(), B.cells(), C.skel(), C.cells());

    double worst = 0.0;
    for (size_t i = 0; i < m; i++)
        for (size_t j = 0; j < n; j++) {
            V acc = V();
            for (size_t t = 0; t < k; t++) acc = acc + A.rows[i][t] * B.rows[t][j];
            double d = elem<T>::dist(C.rows[i][j], acc);
            if (d > worst) worst = d;
        }
    report("matmul", elem<T>::letter(), worst, elem<T>::tol());
}

// ---------------------------------------------------------------------------
// Route 2 -- gram(A, B).  C(m x p) = A(m x n) * B(p x n)^{T|H}, dense.
// The reference transcribes Blade's own scalar loop verbatim, INCLUDING the
// conjugation of the SECOND factor -- which is what makes the complex arm a real
// check on `CUBLAS_OP_C` rather than on `CUBLAS_OP_T`.
// ---------------------------------------------------------------------------
template <typename T>
static void check_gram_distinct(size_t m, size_t n, size_t p,
                                void (*entry)(size_t, size_t, size_t, T**, size_t, T**, size_t, T**, size_t)) {
    typedef typename elem<T>::value V;
    DensePool<T> A(m, n), B(p, n), C(m, p);
    for (size_t i = 0; i < m; i++) for (size_t t = 0; t < n; t++) A.rows[i][t] = fill<T>(i, t, 0);
    for (size_t j = 0; j < p; j++) for (size_t t = 0; t < n; t++) B.rows[j][t] = fill<T>(j, t, 5);

    entry(m, n, p, A.skel(), A.cells(), B.skel(), B.cells(), C.skel(), C.cells());

    double worst = 0.0;
    for (size_t i = 0; i < m; i++)
        for (size_t j = 0; j < p; j++) {
            V acc = V();
            for (size_t t = 0; t < n; t++) acc = acc + A.rows[i][t] * elem<T>::conj(B.rows[j][t]);
            double d = elem<T>::dist(C.rows[i][j], acc);
            if (d > worst) worst = d;
        }
    report("gram_distinct", elem<T>::letter(), worst, elem<T>::tol());
}

// ---------------------------------------------------------------------------
// Route 3 -- gram(A, A).  THE TRAP ROW.
// C(m x m) = A(m x n) * A^{T|H}, written into Blade's PACKED UPPER triangle.
// The reference reads the packed cell `Crows[i][j - i]` for i <= j, so a
// fill-mode error (which would leave that triangle untouched) and a stray
// conjugation (which would return conj of the right answer off the diagonal)
// are both caught, and the diagonal's REALNESS on the complex arm is asserted
// separately -- it is the Hermitian signature that tells herk from syrk.
// ---------------------------------------------------------------------------
template <typename T>
static void check_gram_same(size_t m, size_t n,
                            void (*entry)(size_t, size_t, T**, size_t, T**)) {
    typedef typename elem<T>::value V;
    DensePool<T> A(m, n);
    PackedUpperPool<T> C(m);
    for (size_t i = 0; i < m; i++) for (size_t t = 0; t < n; t++) A.rows[i][t] = fill<T>(i, t, 0);

    entry(m, n, A.skel(), A.cells(), C.skel());

    double worst = 0.0;
    for (size_t i = 0; i < m; i++)
        for (size_t j = i; j < m; j++) {
            V acc = V();
            for (size_t t = 0; t < n; t++) acc = acc + A.rows[i][t] * elem<T>::conj(A.rows[j][t]);
            double d = elem<T>::dist(C.rows[i][j - i], acc);
            if (d > worst) worst = d;
        }
    report("gram_same", elem<T>::letter(), worst, elem<T>::tol());
}

int main() {
    // Non-square, non-power-of-two, mutually distinct.
    const size_t m = 5, n = 3, p = 4, k = 6;

    std::printf("cuBLAS swap-table verification (device vs host reference)\n");
    std::printf("  matmul        m=%zu k=%zu n=%zu\n", m, k, n);
    std::printf("  gram_distinct m=%zu n=%zu p=%zu\n", m, n, p);
    std::printf("  gram_same     m=%zu n=%zu\n\n", m, n);

    check_matmul<float>              (m, k, n, &blade_cuda_matmul_s);
    check_matmul<double>             (m, k, n, &blade_cuda_matmul_d);
    check_matmul<std::complex<float>>(m, k, n, &blade_cuda_matmul_c);
    check_matmul<std::complex<double>>(m, k, n, &blade_cuda_matmul_z);

    check_gram_distinct<float>               (m, n, p, &blade_cuda_gram_distinct_s);
    check_gram_distinct<double>              (m, n, p, &blade_cuda_gram_distinct_d);
    check_gram_distinct<std::complex<float>> (m, n, p, &blade_cuda_gram_distinct_c);
    check_gram_distinct<std::complex<double>>(m, n, p, &blade_cuda_gram_distinct_z);

    check_gram_same<float>               (m, n, &blade_cuda_gram_same_s);
    check_gram_same<double>              (m, n, &blade_cuda_gram_same_d);
    check_gram_same<std::complex<float>> (m, n, &blade_cuda_gram_same_c);
    check_gram_same<std::complex<double>>(m, n, &blade_cuda_gram_same_z);

    // The Hermitian signature, checked directly: A*A^H has a REAL diagonal
    // (entry (i,i) is the sum of |A(i,k)|^2), while A*A^T does not. This is the
    // one assertion that separates a correct `herk` binding from a `syrk` one
    // even if both happened to agree with a reference computed the same wrong
    // way -- so it is stated against the MATH, not against another loop.
    {
        DensePool<std::complex<double>> A(m, n);
        PackedUpperPool<std::complex<double>> C(m);
        for (size_t i = 0; i < m; i++)
            for (size_t t = 0; t < n; t++)
                A.rows[i][t] = fill<std::complex<double>>(i, t, 0);
        blade_cuda_gram_same_z(m, n, A.skel(), A.cells(), C.skel());
        double worstIm = 0.0, worstDiag = 0.0;
        for (size_t i = 0; i < m; i++) {
            std::complex<double> d = C.rows[i][0];            // logical (i, i)
            double norm2 = 0.0;
            for (size_t t = 0; t < n; t++) norm2 += std::norm(A.rows[i][t]);
            if (std::fabs(d.imag()) > worstIm) worstIm = std::fabs(d.imag());
            double e = std::fabs(d.real() - norm2);
            if (e > worstDiag) worstDiag = e;
        }
        report("gram_same/herm-diag-im", "z", worstIm, 1e-12);
        report("gram_same/herm-diag-re", "z", worstDiag, 1e-10);
    }

    // The summary line the F# harness parses. Same doctrine as
    // `linalg_probe_tests.cpp` and `alloc_layout_tests.cpp`: a binary that
    // aborted before running any check must not be readable as a vacuous pass,
    // so the harness requires this line before it accepts an exit 0.
    std::printf("\nCUBLAS SWAP TESTS: %d/%d passed\n", g_total - g_failed, g_total);
    return g_failed == 0 ? 0 : 1;
}
