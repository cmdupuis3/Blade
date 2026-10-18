// blade_packed_gemm.hpp -- the native (BLAS-off) matmul kernel, packed.
//
// C = A * B for dense row-skeleton operands (A: m x k, B: k x n, C: m x n),
// GotoBLAS-style: B is packed into NR-wide column panels and A into MR-tall
// row panels, K is blocked by KC, and an MR x NR register tile accumulates.
//
// BYTE-IDENTITY with the i-t-j loop it replaces (CodeGenExpr.fs,
// materializeMatmulForm) and with Interp/ArrayOps.matmulArray: every output
// cell starts at 0 and adds a[i][t] * b[t][j] for t ASCENDING. K-blocking
// stores the partial into C and reloads it for the next block, which changes
// no value; zero padding only feeds cells that are never stored. The tile is
// written with GCC vector extensions, NOT FMA intrinsics, so contraction
// follows -ffp-contract exactly as the loop's does: mul-then-add under the
// differential gates' `=off`, fused under the user default `=fast`.
// Measured bitwise against the loop under both, at every shape tested.
//
// Measured (Zen 3, single thread, cycles per MAC, loop vs packed):
//   61x2003x61  0.62 -> 0.30    203x157x211  0.48 -> 0.17
//   257x255x253 0.49 -> 0.17    1003x1001x997 0.62 -> 0.18   (~67% of FMA peak)
// and 1.4-2.4x over the threaded loop at 8 threads. Below the crossover the
// fixed costs (packing, edge tiles) lose to the loop, which then runs instead.
// WHETHER a shape is worth the packed path is decided at COMPILE time, on the
// literal extents, by ONE predicate: CodeGenExpr.packedGemmWorth (the emitter
// calls dgemm_nn only above it and emits the loop otherwise). This header
// carries no copy of the rule to drift from it.
//
// Only GCC/Clang get the packed path (vector extensions); anything else --
// cl.exe as nvcc's host compiler -- gets a dgemm_nn that IS the loop.
#pragma once
#include <cstddef>
#include <cstring>
#include <cstdint>
#include <algorithm>
#include "blade_tiles.h"
#ifdef _OPENMP
#include <omp.h>
#endif

namespace blade_pgemm {

#if defined(__GNUC__) || defined(__clang__)

constexpr size_t MR = 6;      // tile rows: 6 x 2 accumulators = 12 of 16 YMM
constexpr size_t NR = 8;      // tile cols: two 4-double vectors
constexpr size_t KC = 256;    // K block: an A micro-panel (12 KB) + a B micro-panel (16 KB) in L1
constexpr size_t MC = 96;     // row block: the packed A block (192 KB) in L2
constexpr size_t NC = 2048;   // column block: the packed B panel (4 MB) in L3

typedef double v4d __attribute__((vector_size(32)));

/// Grow-only 64-byte-aligned scratch, one per thread (thread_local below):
/// reallocating per call measured as THE mid-size cost (65^3: 0.60x with a
/// fresh allocation, 1.68x reused). Held for the thread's lifetime.
struct Scratch {
    double* raw = nullptr;
    double* p = nullptr;
    size_t cap = 0;
    ~Scratch() { delete[] raw; }
    double* get(size_t need) {
        if (need > cap) {
            delete[] raw;
            raw = new double[need + 8];
            p = reinterpret_cast<double*>((reinterpret_cast<uintptr_t>(raw) + 63) & ~uintptr_t(63));
            cap = need;
        }
        return p;
    }
};

inline void pack_a(double* const* A, size_t ic, size_t mc, size_t pc, size_t kc, double* ap) {
    for (size_t ir = 0; ir < mc; ir += MR) {
        const size_t mr = std::min(MR, mc - ir);
        for (size_t p = 0; p < kc; p++) {
            size_t r = 0;
            for (; r < mr; r++) ap[r] = A[ic + ir + r][pc + p];
            for (; r < MR; r++) ap[r] = 0.0;
            ap += MR;
        }
    }
}

inline void pack_b_panel(double* const* B, size_t pc, size_t kc, size_t j0, size_t nr, double* dst) {
    for (size_t p = 0; p < kc; p++) {
        const double* src = &B[pc + p][j0];
        size_t q = 0;
        for (; q < nr; q++) dst[q] = src[q];
        for (; q < NR; q++) dst[q] = 0.0;
        dst += NR;
    }
}

/// One MR x NR tile over one K block. `first` seeds from zero (the loop's
/// `T()`); later blocks continue from the partial stored in C.
inline __attribute__((always_inline))
void tile(size_t kc, const double* ap, const double* bp, double* const* C,
          size_t i0, size_t j0, size_t mr, size_t nr, bool first) {
    v4d c[MR][2];
    const bool full = (mr == MR && nr == NR);
    if (first) {
        for (size_t r = 0; r < MR; r++) { c[r][0] = (v4d){0, 0, 0, 0}; c[r][1] = (v4d){0, 0, 0, 0}; }
    } else if (full) {
        for (size_t r = 0; r < MR; r++) {
            std::memcpy(&c[r][0], &C[i0 + r][j0], 32);
            std::memcpy(&c[r][1], &C[i0 + r][j0 + 4], 32);
        }
    } else {
        double t[MR][NR] = {};
        for (size_t r = 0; r < mr; r++) for (size_t q = 0; q < nr; q++) t[r][q] = C[i0 + r][j0 + q];
        for (size_t r = 0; r < MR; r++) { std::memcpy(&c[r][0], &t[r][0], 32); std::memcpy(&c[r][1], &t[r][4], 32); }
    }
    for (size_t p = 0; p < kc; p++) {
        v4d b0, b1;
        std::memcpy(&b0, bp + p * NR, 32);
        std::memcpy(&b1, bp + p * NR + 4, 32);
        for (size_t r = 0; r < MR; r++) {
            const double a = ap[p * MR + r];
            const v4d av = (v4d){a, a, a, a};
            c[r][0] += av * b0;
            c[r][1] += av * b1;
        }
    }
    if (full) {
        for (size_t r = 0; r < MR; r++) {
            std::memcpy(&C[i0 + r][j0], &c[r][0], 32);
            std::memcpy(&C[i0 + r][j0 + 4], &c[r][1], 32);
        }
    } else {
        double t[MR][NR];
        for (size_t r = 0; r < MR; r++) { std::memcpy(&t[r][0], &c[r][0], 32); std::memcpy(&t[r][4], &c[r][1], 32); }
        for (size_t r = 0; r < mr; r++) for (size_t q = 0; q < nr; q++) C[i0 + r][j0 + q] = t[r][q];
    }
}

/// C = A * B. `threaded` is the emitter's thread-emission knob
/// (BLADE_OMP_THREADS): false keeps the whole call on the calling thread.
/// Rows of C are split across threads; no cell's summation is, so the
/// result is the same for every team size.
inline void dgemm_nn(size_t m, size_t k, size_t n,
                     double* const* A, double* const* B, double* const* C, bool threaded) {
    if (m == 0 || n == 0) return;
    if (k == 0) {
        for (size_t i = 0; i < m; i++) for (size_t j = 0; j < n; j++) C[i][j] = 0.0;
        return;
    }
    static thread_local Scratch bscratch;
    const size_t npad = (std::min(NC, n) + NR - 1) / NR * NR;
    double* bp = bscratch.get(std::min(KC, k) * npad);
    // Team: at least ~1M MACs per thread -- below that fork/join and SMT
    // contention cost more than the split buys.
    size_t T = 1;
#ifdef _OPENMP
    if (threaded && !omp_in_parallel())
        T = std::max<size_t>(1, std::min<size_t>((size_t)omp_get_max_threads(), (m * n * k) >> 20));
#else
    (void)threaded;
#endif
    // Row blocks: a multiple of T of them (balanced rounds), each <= MC rows.
    const size_t nblk = T * ((m + T * MC - 1) / (T * MC));
    const size_t mcEff = std::max(MR, ((m + nblk - 1) / nblk + MR - 1) / MR * MR);
    for (size_t jc = 0; jc < n; jc += NC) {
        const size_t nc = std::min(NC, n - jc);
        for (size_t pc = 0; pc < k; pc += KC) {
            const size_t kc = std::min(KC, k - pc);
            const bool first = (pc == 0);
#ifdef _OPENMP
#pragma omp parallel num_threads((int)T) if (T > 1)
#endif
            {
#ifdef _OPENMP
#pragma omp for schedule(static)
#endif
                for (size_t jr = 0; jr < nc; jr += NR)
                    pack_b_panel(B, pc, kc, jc + jr, std::min(NR, nc - jr), bp + jr * kc);
                static thread_local Scratch ascratch;
                double* ap = ascratch.get((mcEff + MR) * kc);
#ifdef _OPENMP
#pragma omp for schedule(static)
#endif
                for (size_t ic = 0; ic < m; ic += mcEff) {
                    const size_t mc = std::min(mcEff, m - ic);
                    pack_a(A, ic, mc, pc, kc, ap);
                    for (size_t jr = 0; jr < nc; jr += NR) {
                        const size_t nr = std::min(NR, nc - jr);
                        for (size_t ir = 0; ir < mc; ir += MR)
                            tile(kc, ap + ir * kc, bp + jr * kc, C, ic + ir, jc + jr,
                                 std::min(MR, mc - ir), nr, first);
                    }
                }
            }
        }
    }
}

// ---------------------------------------------------------------------------
// Packed-triangle SYRK: C(i, j) = sum_s X(i, s) X(j, s) over Blade's packed
// upper triangle (i + S <= j < p), the BLAS-off arm of the same-array `comm`
// row contraction (LinAlgPatterns.(|BlasL3|_|), dispatched by
// CodeGenLoopNest.tryGenSyrkDispatch). The arithmetic is blade_tiles.h's
// (shared with the LLVM lane's future driver); this is the C++ driver: the
// blocking, the packing scratch and the thread team.
//
// BITWISE the serial per-cell prodsum (`double __ps = 0; for t ascending:
// __ps += X[i][t] * X[j][t]`) under the same -ffp-contract: each cell is one
// ascending chain, K blocks store and reload the exact partial, cells are split
// across threads but sums never are (same bits for every team size). Proven
// under =off, verified under =fast by cpp/syrk_tile_tests.cpp.
//
// Blocking (Zen 3: 32 KB L1d, 512 KB L2, 32 MB L3 per CCD):
//   KC  the sample block, BALANCED: nkb = ceil(k / TRI_KC_MAX) blocks of
//       ceil(k / nkb) samples (k = 1001 -> 3 x 334, never 2 x 336 + 329), so the
//       last block is never a runt. The A micro-panel (6 x KC: 16 KB at 334)
//       stays in L1 for a whole row panel, beside the B lines streaming through.
//   NC  the column block packed as B: NC x KC (2048 x 334: 5.5 MB) in L3.
// ORDER: a row panel (6 rows) is OUTER and the column panels from its diagonal
// rightward are INNER -- the expert Fortran's order transposed (its column
// panel outer, row panels inner). It keeps the A micro-panel resident and makes
// the output traffic six SEQUENTIAL row streams (row i's cells left to right),
// which the prefetchers follow; the GotoBLAS order (B micro-panel resident, A
// block streamed from L2) walks 96 rows' output at once and measured ~10-15%
// slower at 1601 x 1001 (scratch sweep, loaded host). The B panel streams from
// L3, as the expert's A panels do.
//
// WHETHER a shape is worth it is decided at COMPILE time on literal extents by
// CodeGenExprSupport.packedTriWorth; the header carries no copy of the rule.
// ---------------------------------------------------------------------------

constexpr size_t TRI_KC_MAX = 336;
constexpr size_t TRI_NC = 2048;

/// The balanced sample block for k samples under a cap of kcMax.
inline size_t tri_kc(size_t k, size_t kcMax) {
    const size_t nkb = (k + kcMax - 1) / kcMax;
    return (k + nkb - 1) / nkb;
}

/// C = X X^T over the packed triangle. X: p row pointers, each k contiguous
/// samples. C: p row pointers, C[i] pointing at cell (i, i + S) (Blade's
/// left-justified packed rows; C[i][jr] = cell (i, i + S + jr)). `threaded` is
/// the emitter's thread knob AND the source's omp licence: false keeps the
/// whole call on the calling thread. kcMax is the sample-block cap (the default
/// is the tuned value; the tests shrink it to force several K blocks).
inline void dsyrk_tri(size_t p, size_t k, double* const* X, double* const* C, size_t S,
                      bool threaded, size_t kcMax = TRI_KC_MAX) {
    if (p <= S) return;
    if (k == 0) {
        // The empty sum: every cell is the seed, +0.0.
        for (size_t i = 0; i + S < p; i++)
            for (size_t jr = 0; jr < p - i - S; jr++) C[i][jr] = 0.0;
        return;
    }
    const size_t kcn = tri_kc(k, kcMax == 0 ? TRI_KC_MAX : kcMax);
    static thread_local Scratch bscratch;
    // Team: at least ~1M FMAs per thread (dgemm_nn's rule).
    size_t T = 1;
#ifdef _OPENMP
    if (threaded && !omp_in_parallel())
        T = std::max<size_t>(1, std::min<size_t>((size_t)omp_get_max_threads(), (p * p / 2 * k) >> 20));
#else
    (void)threaded;
    (void)T;
#endif
    for (size_t jc = 0; jc < p; jc += TRI_NC) {
        const size_t nc = std::min(TRI_NC, p - jc);
        const size_t npad = (nc + NR - 1) / NR * NR;
        const size_t jEnd = jc + nc;
        // rows that meet this column block: i + S <= jEnd - 1
        const size_t iTop = std::min(p, jEnd - S);
        double* bp = bscratch.get(kcn * npad);
        for (size_t pc = 0; pc < k; pc += kcn) {      // ascending: the chain order
            const size_t kc = std::min(kcn, k - pc);
            const int first = (pc == 0);
            auto packB = [&](size_t jr) {
                const double* src[NR];
                const size_t rows = std::min(NR, nc - jr);
                for (size_t q = 0; q < rows; q++) src[q] = X[jc + jr + q] + pc;
                blade_tile_pack_t(src, rows, NR, kc, bp + jr * kc);
            };
            // One row panel (6 rows) across the column block, from its diagonal
            // rightward. `ap` is the executing thread's own A micro-panel.
            auto rowPanel = [&](size_t i0, double* ap) {
                const size_t mr = std::min(MR, iTop - i0);
                const double* src[MR];
                for (size_t r = 0; r < mr; r++) src[r] = X[i0 + r] + pc;
                blade_tile_pack_t(src, mr, MR, kc, ap);
                double* crow[MR];
                for (size_t r = 0; r < MR; r++) crow[r] = (r < mr) ? C[i0 + r] : nullptr;
                // first column panel reaching row i0's diagonal (i0 + S)
                const size_t jlo = (i0 + S > jc) ? (i0 + S - jc) / NR * NR : 0;
                for (size_t jr = jlo; jr < nc; jr += NR) {
                    const size_t j0 = jc + jr, nr = std::min(NR, nc - jr);
                    if (mr == MR && nr == NR && j0 >= i0 + (MR - 1) + S)
                        blade_tri_tile_full(kc, ap, bp + jr * kc, crow, j0 - i0 - S, first);
                    else
                        blade_tri_tile_edge(kc, ap, bp + jr * kc, crow, i0, j0, mr, nr, S, first);
                }
            };
            static thread_local Scratch ascratch;     // private per executing thread
#ifdef _OPENMP
            if (T > 1) {
                // Row panels under a team: dynamic (the triangle makes upper
                // panels longer). Each cell has ONE owner per K block and the K
                // blocks are sequential, so the bits never depend on T.
#pragma omp parallel num_threads((int)T)
                {
#pragma omp for schedule(static)
                    for (size_t jr = 0; jr < nc; jr += NR) packB(jr);
                    double* ap = ascratch.get(MR * kc);
#pragma omp for schedule(dynamic, 1)
                    for (size_t i0 = 0; i0 < iTop; i0 += MR) rowPanel(i0, ap);
                }
                continue;
            }
#endif
            // Serial: no OpenMP runtime call at all (an `if(false)` parallel
            // region still costs ~17 us per K block -- measured to dominate
            // below ~50^2 x 50).
            for (size_t jr = 0; jr < nc; jr += NR) packB(jr);
            double* ap = ascratch.get(MR * kc);
            for (size_t i0 = 0; i0 < iTop; i0 += MR) rowPanel(i0, ap);
        }
    }
}


#else   // not GCC/Clang (cl.exe: the memcheck MSVC fallback, nvcc's host half)

// No vector extensions, but the emitter calls dgemm_nn on the SHAPE, not the
// compiler, so this must still compute C: it is the emitted i-t-j loop,
// verbatim in arithmetic (per cell: T() then ascending t), serial.
inline void dgemm_nn(size_t m, size_t k, size_t n,
                     double* const* A, double* const* B, double* const* C, bool) {
    for (size_t i = 0; i < m; i++) {
        double* c = C[i];
        for (size_t j = 0; j < n; j++) c[j] = double();
        for (size_t t = 0; t < k; t++) {
            const double a = A[i][t];
            const double* b = B[t];
            for (size_t j = 0; j < n; j++) c[j] += a * b[j];
        }
    }
}

// The packed-triangle SYRK, likewise the loop it replaces: per cell a +0.0
// seed then ascending s, the product in source factor order, serial.
inline void dsyrk_tri(size_t p, size_t k, double* const* X, double* const* C, size_t S,
                      bool, size_t = 0, size_t = 0) {
    for (size_t i = 0; i + S < p; i++)
        for (size_t jr = 0; jr < p - i - S; jr++) {
            const double* a = X[i];
            const double* b = X[i + S + jr];
            double acc = 0;
            for (size_t s = 0; s < k; s++) acc += a[s] * b[s];
            C[i][jr] = acc;
        }
}

#endif

}  // namespace blade_pgemm
