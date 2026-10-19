/* llvm_tiles_rawbit.c -- CROSS-LANE raw-bit test of the shared tile cores
 * (bench/notes/microkernel-sketches-review.md items M6 / M7 / M8): the
 * packed-triangle SYRK, the antisym pair over the STRICT triangle and the
 * rank-3 simplex.
 *
 * ONE source, two programs (tests/LlvmTests.fs runLlvmTilesRawBitTests,
 * `blade test llvm tiles`):
 *   - built by CLANG as C and linked with the REAL tile object
 *     (Build.buildLlvmTilesObject: the object the LLVM lane links, same key,
 *     same flags), calling blade_dsyrk_tri_f64 / blade_dantisym_tri_f64 /
 *     blade_dsym3_tri_f64 with the argument recipes the emitter uses (leading
 *     dims from the dense pool, the emitter's kc cap and scratch sizes, the
 *     scale as a mode + value);
 *   - built by G++ as C++ with the C++ lane's flags, calling
 *     blade_pgemm::dsyrk_tri / dantisym_tri / dsym3_tri over row pointers
 *     (and, for syrk, the C++ lane's emitted scale post-pass).
 * Each program self-checks every cell BITWISE against the serial per-cell
 * chain compiled in the same TU and mode (plain `acc += a * b` under
 * -ffp-contract=off; under =fast an explicit fma chain, BLADE_TEST_FMA_REF:
 * `fma(a, b, acc)` for the dots, `fma(a * b, c, acc)` for the simplex), then
 * prints one hash line per case over the raw bytes of the packed pool. The
 * runner diffs the two programs' hash lines: equal lines = equal bits.
 *
 * The cases come from llvm_tiles_cases.h, which the runner writes from the
 * EMITTER's own numbers (CodeGenExprSupport.syrkTileKc,
 * EmitLlvm.syrkTileScratchCells / antisymTileScratchCells /
 * sym3TileScratchCells), so the C side is driven exactly as an emitted call
 * would drive it -- plus deliberately short scratches (0) to exercise each
 * core's scalar fallback, which must give the same bits.
 *
 * Output: `[FAIL]: <case> ...` lines, `CASE <id> ... <hash>` lines (syrk:
 * `CASE <n>`, antisym: `CASE A<n>`, simplex: `CASE S<n>`), then
 * `LLVM TILES RAWBIT (<lane>): <passed>/<total> passed`.
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
void blade_dantisym_tri_f64(int64_t p, int64_t n, const double* z, int64_t ldi, int64_t ldc,
                            int64_t cx, int64_t cy, double* c, int64_t variant, int64_t nc_max,
                            int64_t scale_mode, double scale,
                            double* scratch, int64_t scratch_cells);
void blade_dsym3_tri_f64(int64_t p, int64_t n, const double* x, int64_t ldx,
                         double* c, int64_t kc_max,
                         int64_t scale_mode, double scale,
                         double* scratch, int64_t scratch_cells);
#endif

/* syrk: { p, k, kc cap, scratch cells (emitter's), scale mode, scale, data kind } */
typedef struct { long long p, k, cap, scratch, mode; double scale; int data; } tile_case;
/* antisym: { p, n, components, cx, cy, variant (0 = 6x8, 1 = 4x4), nc cap
 * (0 = the driver's), scratch cells (emitter's), scale mode, scale, data } */
typedef struct { long long p, n, comps, cx, cy, variant, ncmax, scratch, mode; double scale; int data; } anti_case;
/* simplex: { p, n, kc cap, scratch cells (emitter's), scale mode, scale, data } */
typedef struct { long long p, n, cap, scratch, mode; double scale; int data; } sym3_case;
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
 * cancellation, 3 signed zeros + tiny, 4 subnormal PAIR products, 5 signed
 * values with occasional +-Inf, 6 subnormal TRIPLE products */
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
    case 4: return (unit() - 0.5) * 1e-160;
    case 5: {
        const unsigned long long u = next_u64() % 60;
        if (u == 0) return INFINITY;
        if (u == 1) return -INFINITY;
        return unit() * 2.0 - 1.0;
    }
    default: return (unit() - 0.5) * 1e-105;
    }
}

static double scaled(double v, long long mode, double s) {
    return mode == 1 ? v / s : mode == 2 ? v * s : v;
}

/* The serial chains (references), in this TU's contraction mode. */
static double dot2(const double* a, const double* b, size_t n) {
    double acc = 0.0;
    size_t s;
    for (s = 0; s < n; s++) {
#ifdef BLADE_TEST_FMA_REF
        acc = fma(a[s], b[s], acc);
#else
        acc += a[s] * b[s];
#endif
    }
    return acc;
}
static double dot3(const double* a, const double* b, const double* c, size_t n) {
    double acc = 0.0;
    size_t s;
    for (s = 0; s < n; s++) {
#ifdef BLADE_TEST_FMA_REF
        const double ab = a[s] * b[s];
        acc = fma(ab, c[s], acc);
#else
        acc += a[s] * b[s] * c[s];
#endif
    }
    return acc;
}

static const unsigned long long GUARD = 0x7FF4DEADBEEF1234ULL;
#define PAD 16

static double* guarded(size_t cells) {
    double* buf = (double*)malloc((cells + 2 * PAD) * sizeof(double));
    size_t t;
    for (t = 0; t < cells + 2 * PAD; t++) memcpy(&buf[t], &GUARD, 8);
    return buf;
}
static size_t guard_damage(const double* buf, size_t cells) {
    size_t g, bad = 0;
    for (g = 0; g < PAD; g++) {
        if (memcmp(&buf[g], &GUARD, 8) != 0) bad++;
        if (memcmp(&buf[PAD + cells + g], &GUARD, 8) != 0) bad++;
    }
    return bad;
}
static unsigned long long fnv(const double* c, size_t cells) {
    unsigned long long h = 1469598103934665603ULL;
    size_t t;
    for (t = 0; t < cells * sizeof(double); t++) {
        h ^= ((const unsigned char*)c)[t];
        h *= 1099511628211ULL;
    }
    return h;
}

static size_t row_base(size_t p, size_t i) { return i * (2 * p - i + 1) / 2; }

static int run_syrk(size_t ci) {
    const tile_case tc = tile_cases[ci];
    const size_t p = (size_t)tc.p, k = (size_t)tc.k;
    const size_t cells = p * (p + 1) / 2;
    double* x = (double*)malloc((p * k + 1) * sizeof(double));
    double* c = (double*)malloc((cells + 2) * sizeof(double));
    double* ref = (double*)malloc((cells + 1) * sizeof(double));
    size_t i, j, t, bad = 0;
    g_s = 0x5EED0000ULL + ci * 7919ULL;
    for (t = 0; t < p * k; t++) x[t] = gen(tc.data);
    for (t = 0; t < cells + 2; t++) c[t] = 12345.0;     /* never-written sentinel */
    for (i = 0; i < p; i++)
        for (j = i; j < p; j++)
            ref[row_base(p, i) + j - i] = scaled(dot2(x + i * k, x + j * k, k), tc.mode, tc.scale);
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
    if (bad != 0)
        printf("[FAIL]: syrk case %d (p=%lld k=%lld cap=%lld mode=%lld data=%d): %zu cells differ from the serial chain\n",
               (int)ci, tc.p, tc.k, tc.cap, tc.mode, tc.data, bad);
    printf("CASE %d p=%lld k=%lld cap=%lld mode=%lld data=%d %016llx\n",
           (int)ci, tc.p, tc.k, tc.cap, tc.mode, tc.data, fnv(c, cells));
    free(x); free(c); free(ref);
    return bad == 0;
}

static int run_anti(size_t ci) {
    const anti_case tc = anti_cases[ci];
    const size_t p = (size_t)tc.p, n = (size_t)tc.n, C = (size_t)tc.comps;
    const size_t cx = (size_t)tc.cx, cy = (size_t)tc.cy;
    const size_t cells = p > 1 ? p * (p - 1) / 2 : 0;
    double* z = (double*)malloc((p * C * n + 1) * sizeof(double));
    double* buf = guarded(cells);
    double* out = buf + PAD;
    double* ref = (double*)malloc((cells + 1) * sizeof(double));
    size_t i, j, t, off = 0, bad = 0;
    g_s = 0xA1170000ULL + ci * 7919ULL;
    for (t = 0; t < p * C * n; t++) z[t] = gen(tc.data);
    for (i = 0; i + 1 < p; i++)
        for (j = i + 1; j < p; j++) {
            const double P = dot2(z + i * C * n + cx * n, z + j * C * n + cy * n, n);
            const double Q = dot2(z + j * C * n + cx * n, z + i * C * n + cy * n, n);
            ref[off++] = scaled(P - Q, tc.mode, tc.scale);
        }
#ifdef __cplusplus
    {
        double** mid = (double**)malloc((p * C + 1) * sizeof(double*));
        double*** Z = (double***)malloc((p + 1) * sizeof(double**));
        for (i = 0; i < p; i++) {
            for (t = 0; t < C; t++) mid[i * C + t] = z + i * C * n + t * n;
            Z[i] = mid + i * C;
        }
        blade_pgemm::dantisym_tri(p, n, Z, cx, cy, out, (int)tc.mode, tc.scale, false,
                                  (int)tc.variant, (size_t)tc.ncmax);
        free(mid); free(Z);
    }
#else
    {
        double* scratch = tc.scratch > 0 ? (double*)malloc((size_t)tc.scratch * sizeof(double)) : NULL;
        blade_dantisym_tri_f64((int64_t)p, (int64_t)n, z, (int64_t)(C * n), (int64_t)n,
                               (int64_t)cx, (int64_t)cy, out, (int64_t)tc.variant, (int64_t)tc.ncmax,
                               (int64_t)tc.mode, tc.scale, scratch, (int64_t)tc.scratch);
        free(scratch);
    }
#endif
    for (t = 0; t < cells; t++)
        if (memcmp(&out[t], &ref[t], sizeof(double)) != 0) bad++;
    bad += guard_damage(buf, cells);
    if (bad != 0)
        printf("[FAIL]: antisym case A%d (p=%lld n=%lld c=(%lld,%lld) v=%lld nc<=%lld scratch=%lld mode=%lld data=%d): %zu cells/guards differ\n",
               (int)ci, tc.p, tc.n, tc.cx, tc.cy, tc.variant, tc.ncmax, tc.scratch, tc.mode, tc.data, bad);
    printf("CASE A%d p=%lld n=%lld c=(%lld,%lld) v=%lld nc=%lld mode=%lld data=%d %016llx\n",
           (int)ci, tc.p, tc.n, tc.cx, tc.cy, tc.variant, tc.ncmax, tc.mode, tc.data, fnv(out, cells));
    free(z); free(buf); free(ref);
    return bad == 0;
}

static int run_sym3(size_t ci) {
    const sym3_case tc = sym3_cases[ci];
    const size_t p = (size_t)tc.p, n = (size_t)tc.n;
    const size_t cells = p * (p + 1) * (p + 2) / 6;
    double* x = (double*)malloc((p * n + 1) * sizeof(double));
    double* buf = guarded(cells);
    double* out = buf + PAD;
    double* ref = (double*)malloc((cells + 1) * sizeof(double));
    size_t i, j, k, t, off = 0, bad = 0;
    g_s = 0x5A3E0000ULL + ci * 7919ULL;
    for (t = 0; t < p * n; t++) x[t] = gen(tc.data);
    /* the DFS (ascending-lex) order IS the packed pool order */
    for (i = 0; i < p; i++)
        for (j = i; j < p; j++)
            for (k = j; k < p; k++)
                ref[off++] = scaled(dot3(x + i * n, x + j * n, x + k * n, n), tc.mode, tc.scale);
#ifdef __cplusplus
    {
        double** xr = (double**)malloc((p + 1) * sizeof(double*));
        for (i = 0; i < p; i++) xr[i] = x + i * n;
        blade_pgemm::dsym3_tri(p, n, xr, out, (int)tc.mode, tc.scale, false, (size_t)tc.cap);
        free(xr);
    }
#else
    {
        double* scratch = tc.scratch > 0 ? (double*)malloc((size_t)tc.scratch * sizeof(double)) : NULL;
        blade_dsym3_tri_f64((int64_t)p, (int64_t)n, x, (int64_t)n, out, (int64_t)tc.cap,
                            (int64_t)tc.mode, tc.scale, scratch, (int64_t)tc.scratch);
        free(scratch);
    }
#endif
    for (t = 0; t < cells; t++)
        if (memcmp(&out[t], &ref[t], sizeof(double)) != 0) bad++;
    bad += guard_damage(buf, cells);
    if (bad != 0)
        printf("[FAIL]: simplex case S%d (p=%lld n=%lld cap=%lld scratch=%lld mode=%lld data=%d): %zu cells/guards differ\n",
               (int)ci, tc.p, tc.n, tc.cap, tc.scratch, tc.mode, tc.data, bad);
    printf("CASE S%d p=%lld n=%lld cap=%lld mode=%lld data=%d %016llx\n",
           (int)ci, tc.p, tc.n, tc.cap, tc.mode, tc.data, fnv(out, cells));
    free(x); free(buf); free(ref);
    return bad == 0;
}

int main(void) {
    int total = 0, passed = 0;
    int ps = 0, pa = 0, p3 = 0;
    size_t ci;
    const size_t ns = sizeof(tile_cases) / sizeof(tile_cases[0]);
    const size_t na = sizeof(anti_cases) / sizeof(anti_cases[0]);
    const size_t n3 = sizeof(sym3_cases) / sizeof(sym3_cases[0]);
    for (ci = 0; ci < ns; ci++) { int ok = run_syrk(ci); ps += ok; passed += ok; total++; }
    for (ci = 0; ci < na; ci++) { int ok = run_anti(ci); pa += ok; passed += ok; total++; }
    for (ci = 0; ci < n3; ci++) { int ok = run_sym3(ci); p3 += ok; passed += ok; total++; }
    printf("SUBTOTALS (%s): syrk %d/%d, antisym %d/%d, simplex %d/%d\n",
           LANE, ps, (int)ns, pa, (int)na, p3, (int)n3);
    printf("LLVM TILES RAWBIT (%s): %d/%d passed\n", LANE, passed, total);
    return passed == total ? 0 : 1;
}
