/* llvm_tiles_rawbit.c -- CROSS-LANE raw-bit test of the packed-triangle SYRK
 * tile (bench/notes/microkernel-sketches-review.md item M6).
 *
 * ONE source, two programs (tests/LlvmTests.fs runLlvmTilesRawBitTests,
 * `blade test llvm tiles`):
 *   - built by CLANG as C and linked with the REAL tile object
 *     (Build.buildLlvmTilesObject: the object the LLVM lane links, same key,
 *     same flags), calling blade_dsyrk_tri_f64 with the argument recipe the
 *     emitter uses (ldx = k, strict 0, the emitter's kc cap and scratch size,
 *     the scale as a mode + value);
 *   - built by G++ as C++ with the C++ lane's flags, calling
 *     blade_pgemm::dsyrk_tri over row pointers, then the C++ lane's scale
 *     post-pass (`C[i][j] /= s`, `*= s`).
 * Each program self-checks every cell BITWISE against the serial per-cell
 * chain compiled in the same TU and mode (plain `acc += a * b` under
 * -ffp-contract=off, an explicit fma chain under =fast: BLADE_TEST_FMA_REF),
 * then prints one hash line per case over the raw bytes of the packed pool.
 * The runner diffs the two programs' hash lines: equal lines = equal bits.
 *
 * The cases come from llvm_tiles_cases.h, which the runner writes from the
 * EMITTER's own numbers (CodeGenExprSupport.syrkTileKc /
 * EmitLlvm.syrkTileScratchCells), so the C side is driven exactly as an
 * emitted call would drive it -- plus a deliberately short scratch (0) to
 * exercise the core's scalar fallback, which must give the same bits.
 *
 * Output: `[FAIL]: <case> ...` lines, `CASE <id> p k cap mode data <hash>`
 * lines, then `LLVM TILES RAWBIT (<lane>): <passed>/<total> passed`.
 */
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <stdint.h>
#include <math.h>

#ifdef __cplusplus
#include "blade_packed_gemm.hpp"
#define LANE "cpp"
#else
#define LANE "llvm"
void blade_dsyrk_tri_f64(int64_t p, int64_t k, const double* x, int64_t ldx,
                         double* c, int64_t strict, int64_t kc_max,
                         int64_t scale_mode, double scale,
                         double* scratch, int64_t scratch_cells);
#endif

/* { p, k, kc cap, scratch cells (emitter's), scale mode, scale, data kind } */
typedef struct { long long p, k, cap, scratch, mode; double scale; int data; } tile_case;
#include "llvm_tiles_cases.h"

static unsigned long long g_s;
static unsigned long long next_u64(void) {
    unsigned long long z = (g_s += 0x9E3779B97F4A7C15ULL);
    z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9ULL;
    z = (z ^ (z >> 27)) * 0x94D049BB133111EBULL;
    return z ^ (z >> 31);
}
static double unit(void) { return (double)(next_u64() >> 11) * (1.0 / 9007199254740992.0); }

/* 0 random signed, 1 positive unit (the bench's lcg range), 2 heavy
 * cancellation, 3 signed zeros + tiny, 4 subnormal products */
static double gen(int d) {
    switch (d) {
    case 0: return unit() * 2.0 - 1.0;
    case 1: return unit();
    case 2: {
        const double big = (next_u64() & 1) ? 1e8 : -1e8;
        return (next_u64() % 3 == 0) ? big * (1.0 + unit()) : unit() * 1e-3;
    }
    case 3: {
        const unsigned long long u = next_u64() % 4;
        return u == 0 ? 0.0 : u == 1 ? -0.0 : (unit() - 0.5) * 1e-300;
    }
    default: return (unit() - 0.5) * 1e-160;
    }
}

static size_t row_base(size_t p, size_t i) { return i * (2 * p - i + 1) / 2; }

int main(void) {
    int total = 0, passed = 0;
    size_t ci;
    for (ci = 0; ci < sizeof(tile_cases) / sizeof(tile_cases[0]); ci++) {
        const tile_case tc = tile_cases[ci];
        const size_t p = (size_t)tc.p, k = (size_t)tc.k;
        const size_t cells = p * (p + 1) / 2;
        double* x = (double*)malloc((p * k + 1) * sizeof(double));
        double* c = (double*)malloc((cells + 2) * sizeof(double));
        double* ref = (double*)malloc((cells + 1) * sizeof(double));
        size_t i, j, s, t, bad = 0;
        unsigned long long h = 1469598103934665603ULL;
        g_s = 0x5EED0000ULL + ci * 7919ULL;
        for (t = 0; t < p * k; t++) x[t] = gen(tc.data);
        for (t = 0; t < cells + 2; t++) c[t] = 12345.0;     /* never-written sentinel */
        /* the serial per-cell chain, then the scalar tail */
        for (i = 0; i < p; i++)
            for (j = i; j < p; j++) {
                double acc = 0.0;
                for (s = 0; s < k; s++) {
#ifdef BLADE_TEST_FMA_REF
                    acc = fma(x[i * k + s], x[j * k + s], acc);
#else
                    acc += x[i * k + s] * x[j * k + s];
#endif
                }
                if (tc.mode == 1) acc = acc / tc.scale;
                else if (tc.mode == 2) acc = acc * tc.scale;
                ref[row_base(p, i) + j - i] = acc;
            }
#ifdef __cplusplus
        {
            double** xr = (double**)malloc((p + 1) * sizeof(double*));
            double** cr = (double**)malloc((p + 1) * sizeof(double*));
            for (i = 0; i < p; i++) { xr[i] = x + i * k; cr[i] = c + row_base(p, i); }
            blade_pgemm::dsyrk_tri(p, k, xr, cr, 0, false, (size_t)tc.cap);
            /* the C++ lane's emitted scale post-pass */
            if (tc.mode == 1) {
                for (i = 0; i < p; i++) for (j = 0; j < p - i; j++) cr[i][j] /= (tc.scale);
            } else if (tc.mode == 2) {
                for (i = 0; i < p; i++) for (j = 0; j < p - i; j++) cr[i][j] *= (tc.scale);
            }
            free(xr); free(cr);
        }
#else
        {
            double* scratch = tc.scratch > 0 ? (double*)malloc((size_t)tc.scratch * sizeof(double)) : NULL;
            blade_dsyrk_tri_f64((int64_t)p, (int64_t)k, x, (int64_t)k, c, 0, (int64_t)tc.cap,
                                (int64_t)tc.mode, tc.scale, scratch, (int64_t)tc.scratch);
            free(scratch);
        }
#endif
        for (t = 0; t < cells; t++)
            if (memcmp(&c[t], &ref[t], sizeof(double)) != 0) bad++;
        if (c[cells] != 12345.0 || c[cells + 1] != 12345.0) bad++;
        total++;
        if (bad == 0) passed++;
        else printf("[FAIL]: case %d (p=%lld k=%lld cap=%lld mode=%lld data=%d): %zu cells differ from the serial chain\n",
                    (int)ci, tc.p, tc.k, tc.cap, tc.mode, tc.data, bad);
        for (t = 0; t < cells * sizeof(double); t++) {
            h ^= ((const unsigned char*)c)[t];
            h *= 1099511628211ULL;
        }
        printf("CASE %d p=%lld k=%lld cap=%lld mode=%lld data=%d %016llx\n",
               (int)ci, tc.p, tc.k, tc.cap, tc.mode, tc.data, h);
        free(x); free(c); free(ref);
    }
    printf("LLVM TILES RAWBIT (%s): %d/%d passed\n", LANE, passed, total);
    return passed == total ? 0 : 1;
}
