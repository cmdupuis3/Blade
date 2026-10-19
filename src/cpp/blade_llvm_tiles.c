/* blade_llvm_tiles.c -- the LLVM lane's serial driver over blade_tiles.h.
 *
 * bench/notes/microkernel-sketches-review.md item M6 (sketch S12): ONE
 * arithmetic core, two drivers. The C++ lane's driver is
 * blade_pgemm::dsyrk_tri (blade_packed_gemm.hpp: thread team, thread_local
 * scratch); this is the LLVM lane's -- plain C, SERIAL, and allocation-free.
 * The tile arithmetic is NOT restated here: both drivers include the same
 * blade_tiles.h, so the 6 x 8 register tile, the transposing pack and the
 * stored-partial reload are one text.
 *
 * BUILD. Compiled by clang once per (source, header, clang, flags, target)
 * KEY -- Build.buildLlvmTilesObject names the object after that key, so a
 * contraction-mode or -march switch in the SAME build directory links a
 * different object, never a stale one -- and linked only into programs whose
 * `.ll` calls it. Flags: the lane's own `-O3 -march -fno-math-errno -fwrapv`
 * plus `-ffp-contract=fast|off` matching the lane's per-instruction
 * `contract` policy (EmitLlvm.tilesContractFlag), so the tile contracts
 * exactly where the IR it replaces would.
 *
 * THE ABI (EmitLlvm declares it; the declaration's attributes are claims
 * about THIS file, so keep them true):
 *   - reads x, reads and writes c and scratch, touches NO other memory: no
 *     globals, no allocation, no I/O, no errno  -> memory(argmem: readwrite);
 *   - no threads, no atomics, no locks          -> nosync;
 *   - no free                                    -> nofree;
 *   - every loop is counted, nothing panics or exits -> willreturn,
 *     mustprogress (a scratch too small for the tile -- a caller bug -- takes
 *     the scalar chain below, it does not abort);
 *   - C, no exceptions                           -> nounwind;
 *   - x is only read -> `readonly`; no pointer is retained -> captures(none).
 *
 * THE ENTRY POINTS. blade_dsyrk_tri_f64 (M6, the packed-triangle SYRK) was
 * the first; blade_dantisym_tri_f64 (M7, the antisym pair over the STRICT
 * triangle) and blade_dsym3_tri_f64 (M8, the rank-3 simplex) follow the same
 * discipline below -- i64 extents, const double* operands with leading dims,
 * the flat output pool, the scale as a mode + value, a caller-provided
 * scratch with its capacity, the exact serial chain when the scratch is
 * short -- and the same ABI claims, so one attribute group serves all three.
 *
 * THE OPERATION (the same as dsyrk_tri's, see blade_tiles.h for the
 * arithmetic contract): C(i, j) = sum_s X(i, s) X(j, s) for i + S <= j < p,
 * over Blade's packed triangle -- the LLVM lane's pool is ONE flat buffer,
 * row i starting at cell row_base(i) = i(2p - i + 1)/2 - i S, row-major
 * left-justified (cell (i, j) at row_base(i) + j - i - S). X is a dense
 * row-major p x k pool with row stride ldx. Every cell is ONE chain, +0.0
 * then `acc += X(i, s) * X(j, s)` for s ascending, then the kernel's scalar
 * tail, if any, applied to the FINISHED sum exactly as the per-cell nest
 * applies it (`/ d` or `* s`; never a reciprocal) -- by the last K block's
 * tile as it stores the cell (blade_tiles.h), so the triangle is written
 * once. Bitwise the per-cell serial prodsum under -ffp-contract=off; the
 * one-fma-per-step chain under =fast, the same bits as the C++ lane's tile
 * (`blade test llvm-tiles`).
 *
 * SPEED-ONLY ARGUMENTS (no bit depends on them; BLADE_TILE_TUNE): the syrk
 * core's `row_group` (row panels visited together against each column panel,
 * the C++ driver's rowGroup) and the antisym core's `kc_max` (the sample
 * block inside a tile visit). Column / k panels are top-aligned in every
 * core (blade_tiles.h "PANEL ALIGNMENT").
 */
#include <stddef.h>
#include <stdint.h>
#include "blade_tiles.h"

/* Blocking: the C++ driver's (blade_packed_gemm.hpp TRI_NC / tri_kc). The
 * blocking cannot change a bit -- each cell is one ascending chain whatever
 * the blocks -- so these are speed choices only; kc_max comes from the
 * emitter (CodeGenExprSupport.syrkTileKcMax), the one statement of the cap. */
#define BLADE_LLVM_TRI_NC 2048

static size_t blade_llvm_tri_kc(size_t k, size_t kc_max) {
    const size_t nkb = (k + kc_max - 1) / kc_max;
    return (k + nkb - 1) / nkb;
}

/* Packed row base: the first cell of row i. */
static size_t blade_llvm_tri_row(size_t p, size_t i, size_t S) {
    return i * (2 * p - i + 1) / 2 - i * S;
}

/* Row panels per GROUP, cap (the C++ driver's TRI_G_MAX): the A scratch
 * holds G micro-panels. */
#define BLADE_LLVM_TRI_G_MAX 8

static size_t blade_llvm_tri_group(int64_t g) {
    if (g < 1) return 1;
    if (g > BLADE_LLVM_TRI_G_MAX) return BLADE_LLVM_TRI_G_MAX;
    return (size_t)g;
}

/* Scratch the tile needs, in doubles: the B panel (KC x NC padded to NR) plus
 * the A scratch (G micro-panels of MR x KC). The emitter computes the SAME
 * number from the literal extents (EmitLlvm.syrkTileScratchCells) and passes
 * the capacity it allocated; this is the check, not the source of truth. */
static size_t blade_llvm_tri_scratch(size_t p, size_t kcn, size_t G) {
    const size_t nc = p < BLADE_LLVM_TRI_NC ? p : BLADE_LLVM_TRI_NC;
    const size_t npad = (nc + BLADE_TILE_NR - 1) / BLADE_TILE_NR * BLADE_TILE_NR;
    return kcn * npad + G * BLADE_TILE_MR * kcn;
}

/* The scalar tail over the finished triangle. mode 0: none; 1: divide by s;
 * 2: multiply by s. Cell order is irrelevant (each cell is independent).
 * Used by the empty-sum and scalar-chain arms; the tile arms apply the same
 * operation in the last K block's store (blade_tiles.h). */
static void blade_llvm_tri_scale(double* c, size_t cells, int64_t mode, double s) {
    size_t t;
    if (mode == 1) {
        for (t = 0; t < cells; t++) c[t] = c[t] / s;
    } else if (mode == 2) {
        for (t = 0; t < cells; t++) c[t] = c[t] * s;
    }
}

/* row_group: row panels visited together against each column panel (the C++
 * driver's rowGroup; G A micro-panels in the scratch, each column panel read
 * once from L3 and G times from L1). Speed only: no bit depends on it. */
void blade_dsyrk_tri_f64(int64_t p_, int64_t k_, const double* x, int64_t ldx_,
                         double* c, int64_t strict, int64_t kc_max_, int64_t row_group,
                         int64_t scale_mode, double scale,
                         double* scratch, int64_t scratch_cells) {
    const size_t p = p_ > 0 ? (size_t)p_ : 0;
    const size_t k = k_ > 0 ? (size_t)k_ : 0;
    const size_t ldx = ldx_ > 0 ? (size_t)ldx_ : 0;
    const size_t S = strict != 0 ? 1 : 0;
    const size_t kc_max = kc_max_ > 0 ? (size_t)kc_max_ : 336;
    const size_t G = blade_llvm_tri_group(row_group);
    const int sk = (int)scale_mode;
    size_t cells, i, jc, pc;
    if (p <= S) return;
    cells = blade_llvm_tri_row(p, p - S, S);   /* = C(p + 1 - S, 2) */
    if (k == 0) {
        /* The empty sum: every cell is the seed, +0.0 (then the tail). */
        for (i = 0; i < cells; i++) c[i] = 0.0;
        blade_llvm_tri_scale(c, cells, scale_mode, scale);
        return;
    }
#if defined(BLADE_TILES_VECTOR)
    {
        const size_t kcn = blade_llvm_tri_kc(k, kc_max);
        if (scratch != NULL && scratch_cells >= 0
            && (size_t)scratch_cells >= blade_llvm_tri_scratch(p, kcn, G)) {
            for (jc = 0; jc < p; jc += BLADE_LLVM_TRI_NC) {
                const size_t nc = (p - jc) < BLADE_LLVM_TRI_NC ? (p - jc) : BLADE_LLVM_TRI_NC;
                const size_t nq = blade_tile_npanels(nc, BLADE_TILE_NR);   /* the ragged panel FIRST */
                const size_t jEnd = jc + nc;
                const size_t iTop = (jEnd - S) < p ? (jEnd - S) : p;
                double* bp = scratch;
                double* ap = scratch + kcn * nq * BLADE_TILE_NR;
                for (pc = 0; pc < k; pc += kcn) {          /* ascending: the chain order */
                    const size_t kc = (k - pc) < kcn ? (k - pc) : kcn;
                    const int first = (pc == 0);
                    const int skb = (pc + kc >= k) ? sk : BLADE_TILE_SCALE_NONE;   /* the tail: last block */
                    size_t q, i0g, r, t;
                    for (q = 0; q < nq; q++) {
                        const double* src[BLADE_TILE_NR];
                        const size_t lo = blade_tile_panel_lo(jc, nc, BLADE_TILE_NR, q);
                        const size_t rows = blade_tile_panel_hi(jc, nc, BLADE_TILE_NR, q) - lo;
                        for (t = 0; t < rows; t++) src[t] = x + (lo + t) * ldx + pc;
                        blade_tile_pack_t(src, rows, BLADE_TILE_NR, kc, bp + q * BLADE_TILE_NR * kc);
                    }
                    /* One group of up to G row panels against each column panel
                     * from the group's first diagonal rightward. */
                    for (i0g = 0; i0g < iTop; i0g += G * BLADE_TILE_MR) {
                        const size_t rem = (iTop - i0g + BLADE_TILE_MR - 1) / BLADE_TILE_MR;
                        const size_t ng = rem < G ? rem : G;
                        double* crow[BLADE_LLVM_TRI_G_MAX][BLADE_TILE_MR];
                        size_t mrs[BLADE_LLVM_TRI_G_MAX];
                        size_t g, q0;
                        for (g = 0; g < ng; g++) {
                            const size_t i0 = i0g + g * BLADE_TILE_MR;
                            const size_t mr = (iTop - i0) < BLADE_TILE_MR ? (iTop - i0) : BLADE_TILE_MR;
                            const double* src[BLADE_TILE_MR];
                            for (r = 0; r < mr; r++) src[r] = x + (i0 + r) * ldx + pc;
                            blade_tile_pack_t(src, mr, BLADE_TILE_MR, kc, ap + g * BLADE_TILE_MR * kc);
                            /* row pointers at cell (i, i + S); never formed past the
                             * triangle (rows >= mr stay NULL and are never read) */
                            for (r = 0; r < BLADE_TILE_MR; r++)
                                crow[g][r] = (r < mr) ? c + blade_llvm_tri_row(p, i0 + r, S) : NULL;
                            mrs[g] = mr;
                        }
                        q0 = blade_tile_panel_first(jc, nc, BLADE_TILE_NR, i0g + S);
                        for (q = q0; q < nq; q++) {
                            const size_t j0 = blade_tile_panel_lo(jc, nc, BLADE_TILE_NR, q);
                            const size_t jhi = blade_tile_panel_hi(jc, nc, BLADE_TILE_NR, q);
                            const size_t nr = jhi - j0;
                            const double* bq = bp + q * BLADE_TILE_NR * kc;
                            for (g = 0; g < ng; g++) {
                                const size_t i0 = i0g + g * BLADE_TILE_MR, mr = mrs[g];
                                if (jhi <= i0 + S) continue;       /* wholly left of this row panel's diagonal */
                                if (mr == BLADE_TILE_MR && nr == BLADE_TILE_NR
                                    && j0 >= i0 + (BLADE_TILE_MR - 1) + S)
                                    blade_tri_tile_full(kc, ap + g * BLADE_TILE_MR * kc, bq, crow[g],
                                                        j0 - i0 - S, first, skb, scale);
                                else
                                    blade_tri_tile_edge(kc, ap + g * BLADE_TILE_MR * kc, bq, crow[g],
                                                        i0, j0, mr, nr, S, first, skb, scale);
                            }
                        }
                    }
                }
            }
            return;
        }
    }
#else
    (void)kc_max; (void)scratch; (void)scratch_cells; (void)jc; (void)pc; (void)G; (void)sk;
#endif
    /* No vector extensions, or a scratch too small: the per-cell loop the
     * tile replaces, verbatim in arithmetic (a +0.0 seed, s ascending, the
     * product in source factor order). Same bits as the tile, by its
     * contract, under either contraction mode. */
    for (i = 0; i + S < p; i++) {
        const double* a = x + i * ldx;
        double* crow = c + blade_llvm_tri_row(p, i, S);
        size_t jr;
        for (jr = 0; jr < p - i - S; jr++) {
            const double* b = x + (i + S + jr) * ldx;
            double acc = 0.0;
            size_t s;
            for (s = 0; s < k; s++) acc += a[s] * b[s];
            crow[jr] = acc;
        }
    }
    blade_llvm_tri_scale(c, cells, scale_mode, scale);
}

/* ===========================================================================
 * THE ANTISYM PAIR (review M7) -- the LLVM lane's driver over
 * blade_anti_tile_6x8 / _4x4, the C++ lane's blade_pgemm::dantisym_tri made
 * serial and allocation-free.
 *     L(i, j) = P(i, j) - Q(i, j),   i < j < p   (the STRICT triangle)
 *     P = sum_s X(i, s) Y(j, s),  Q = sum_s X(j, s) Y(i, s)
 * X(r, s) = Z(r, cx, s), Y(r, s) = Z(r, cy, s) over a dense row-major (p, c, n)
 * pool: Z(i, comp, s) = z[i * ldi + comp * ldc + s] (the emitter passes
 * ldi = c * n, ldc = n). Output: the strict packed pool, row i at
 * i (2p - i - 1) / 2, cell (i, j) at row + j - i - 1 -- the LLVM lane's
 * GAnti(2, p) offset. Each of P and Q is ONE chain per cell (seeded +0.0,
 * s ascending, source factor order -- P with the broadcast X(i) first, Q with
 * the vector X(j) first), run WHOLE inside one tile visit (no K blocking, so
 * no partial is ever stored), the subtraction once per cell after both, then
 * the scale: bitwise the serial form under -ffp-contract=off, the
 * one-fma-per-step chains under =fast, the C++ tile's bits in both modes.
 *
 * variant: 0 = the sequential 6 x 8 (P pass, 48-double spill, Q pass),
 * 1 = the paired 4 x 4 (P and Q in one pass) -- BLADE_ANTISYM_TILE's two
 * schedules, same bits. nc_max: 0 = the C++ driver's column block (anti_nc,
 * ~8 MB of B panels), else a cap (the raw-bit test forces several blocks).
 * ======================================================================== */

/* The column block: the C++ driver's anti_nc (speed only; no bit depends on
 * it). W = the tile's NR. */
static size_t blade_llvm_anti_nc(size_t n, size_t W) {
    const size_t cap = ((size_t)8 << 20) / sizeof(double) / 2;   /* doubles per side */
    size_t nc = (n == 0) ? 4096 : cap / n;
    nc = nc / W * W;
    if (nc > 4096) nc = 4096;
    if (nc < W) nc = W;
    return nc;
}

/* Strict packed row base: the first cell (i, i + 1) of row i. */
static size_t blade_llvm_anti_row(size_t p, size_t i) {
    return i * (2 * p - i - 1) / 2;
}

/* Scratch, in doubles: the X(j) and Y(j) panels (n x the widest padded
 * column block each -- min(NCb, p rounded up to RN)) plus the X(i) and Y(i)
 * row panels (RM x n each). The emitter computes the SAME number
 * (EmitLlvm.antisymTileScratchCells); this is the check. */
static size_t blade_llvm_anti_scratch(size_t p, size_t n, size_t ncb, size_t RM, size_t RN) {
    const size_t ppad = (p + RN - 1) / RN * RN;
    const size_t w = ncb < ppad ? ncb : ppad;
    return 2 * (n * w) + 2 * (RM * n);
}

static double blade_llvm_scale1(double v, int64_t mode, double s) {
    return mode == 1 ? v / s : mode == 2 ? v * s : v;
}

/* kc_max: the sample-block cap inside a tile visit (blade_tiles.h
 * blade_anti_kc; 0 = the whole chain in one block). Speed only. */
void blade_dantisym_tri_f64(int64_t p_, int64_t n_, const double* z, int64_t ldi_, int64_t ldc_,
                            int64_t cx_, int64_t cy_, double* c, int64_t variant, int64_t nc_max_,
                            int64_t kc_max_, int64_t scale_mode, double scale,
                            double* scratch, int64_t scratch_cells) {
    const size_t p = p_ > 0 ? (size_t)p_ : 0;
    const size_t n = n_ > 0 ? (size_t)n_ : 0;
    const size_t ldi = ldi_ > 0 ? (size_t)ldi_ : 0;
    const size_t ldc = ldc_ > 0 ? (size_t)ldc_ : 0;
    const size_t cx = cx_ > 0 ? (size_t)cx_ : 0;
    const size_t cy = cy_ > 0 ? (size_t)cy_ : 0;
    const size_t kc_max = kc_max_ > 0 ? (size_t)kc_max_ : 0;
    size_t i, j, off;
    if (p <= 1) return;                       /* the strict triangle is empty */
#if defined(BLADE_TILES_VECTOR)
    {
        const int paired = (variant == 1);
        const size_t RM = paired ? (size_t)BLADE_ANTI4_MR : (size_t)BLADE_TILE_MR;
        const size_t RN = paired ? (size_t)BLADE_ANTI4_NR : (size_t)BLADE_TILE_NR;
        const int sk = (int)scale_mode;
        size_t NCb = blade_llvm_anti_nc(n, RN);
        if (nc_max_ > 0) {
            const size_t ncm = (size_t)nc_max_ / RN * RN;
            if (ncm < NCb) NCb = ncm;
            if (NCb < RN) NCb = RN;
        }
        if (scratch != NULL && scratch_cells >= 0
            && (size_t)scratch_cells >= blade_llvm_anti_scratch(p, n, NCb, RM, RN)) {
            const size_t ppad = (p + RN - 1) / RN * RN;
            const size_t wmax = NCb < ppad ? NCb : ppad;
            double* bxp = scratch;
            double* byp = bxp + n * wmax;
            double* axp = byp + n * wmax;
            double* ayp = axp + RM * n;
            size_t jc;
            for (jc = 0; jc < p; jc += NCb) {
                const size_t nc = (p - jc) < NCb ? (p - jc) : NCb;
                const size_t nq = blade_tile_npanels(nc, RN);          /* the ragged panel FIRST */
                const size_t jEnd = jc + nc;
                const size_t iTop = (jEnd - 1) < p ? (jEnd - 1) : p;   /* rows meeting the block */
                size_t q, i0, t, r;
                for (q = 0; q < nq; q++) {
                    const double* sx[BLADE_TILE_NR];
                    const double* sy[BLADE_TILE_NR];
                    const size_t lo = blade_tile_panel_lo(jc, nc, RN, q);
                    const size_t rows = blade_tile_panel_hi(jc, nc, RN, q) - lo;
                    for (t = 0; t < rows; t++) {
                        sx[t] = z + (lo + t) * ldi + cx * ldc;
                        sy[t] = z + (lo + t) * ldi + cy * ldc;
                    }
                    blade_tile_pack_t(sx, rows, RN, n, bxp + q * RN * n);
                    blade_tile_pack_t(sy, rows, RN, n, byp + q * RN * n);
                }
                for (i0 = 0; i0 < iTop; i0 += RM) {
                    const size_t mr = (iTop - i0) < RM ? (iTop - i0) : RM;
                    const double* sx[BLADE_TILE_MR];
                    const double* sy[BLADE_TILE_MR];
                    double* crow[BLADE_TILE_MR];
                    size_t q0;
                    for (r = 0; r < mr; r++) {
                        sx[r] = z + (i0 + r) * ldi + cx * ldc;
                        sy[r] = z + (i0 + r) * ldi + cy * ldc;
                    }
                    blade_tile_pack_t(sx, mr, RM, n, axp);
                    blade_tile_pack_t(sy, mr, RM, n, ayp);
                    for (r = 0; r < BLADE_TILE_MR; r++)
                        crow[r] = (r < mr) ? c + blade_llvm_anti_row(p, i0 + r) : NULL;
                    /* first column panel reaching row i0's first cell (i0 + 1) */
                    q0 = blade_tile_panel_first(jc, nc, RN, i0 + 1);
                    for (q = q0; q < nq; q++) {
                        const size_t j0 = blade_tile_panel_lo(jc, nc, RN, q);
                        const size_t nr = blade_tile_panel_hi(jc, nc, RN, q) - j0;
                        if (paired)
                            blade_anti_tile_4x4(n, axp, ayp, bxp + q * RN * n, byp + q * RN * n,
                                                crow, i0, j0, mr, nr, sk, scale, kc_max);
                        else
                            blade_anti_tile_6x8(n, axp, ayp, bxp + q * RN * n, byp + q * RN * n,
                                                crow, i0, j0, mr, nr, sk, scale, kc_max);
                    }
                }
            }
            return;
        }
    }
#else
    (void)variant; (void)nc_max_; (void)kc_max; (void)scratch; (void)scratch_cells;
#endif
    /* No vector extensions, or a scratch too small: the per-cell loop the
     * tile replaces, verbatim in arithmetic -- P then Q, each a +0.0 seed and
     * s ascending in source factor order, one subtraction, the scale. */
    off = 0;
    for (i = 0; i + 1 < p; i++) {
        const double* xi = z + i * ldi + cx * ldc;
        const double* yi = z + i * ldi + cy * ldc;
        for (j = i + 1; j < p; j++) {
            const double* xj = z + j * ldi + cx * ldc;
            const double* yj = z + j * ldi + cy * ldc;
            double P = 0.0, Q = 0.0;
            size_t s;
            for (s = 0; s < n; s++) P += xi[s] * yj[s];
            for (s = 0; s < n; s++) Q += xj[s] * yi[s];
            c[off++] = blade_llvm_scale1(P - Q, scale_mode, scale);
        }
    }
}

/* ===========================================================================
 * THE RANK-3 SIMPLEX (review M8) -- the LLVM lane's driver over
 * blade_sym3_pack_w + the gram tile, the C++ lane's blade_pgemm::dsym3_tri
 * made serial and allocation-free.
 *     T(i, j, k) = sum_s X(i, s) X(j, s) X(k, s),   i <= j <= k < p,
 * then the scale. X is a dense row-major p x n pool with row stride ldx.
 * Output: the packed SymIdx<3, p> pool in ascending-lex (DFS) order -- the
 * (i, j) row starts at C(p+2, 3) - C(p+2-i, 3) + C(p+1-i, 2) - C(p+1-j, 2)
 * (falling binomials), cell k at row + k - j -- the LLVM lane's GSym(3, p)
 * offset. The serial chain's term is `(a*b)*c`, so W(r, s) =
 * round(X(i, s) * X(j0 + r, s)) is that first product formed ONCE per (i, 6-row
 * j block) and the 6 x 8 tile runs over k with W as the broadcast panel; K
 * blocks (balanced, <= kc_max) store and reload the exact UNSCALED partial
 * and the scale is applied once per cell after the last block. Bitwise the
 * serial form under -ffp-contract=off; `fma(a*b, c, acc)` per step under
 * =fast -- the C++ tile's bits in both modes.
 * ======================================================================== */

static size_t blade_llvm_c3(size_t e) { return e * (e - 1) * (e - 2) / 6; }
static size_t blade_llvm_c2(size_t e) { return e * (e - 1) / 2; }

/* Packed row base: the first cell (i, j, j) of row (i, j). */
static size_t blade_llvm_sym3_row(size_t p, size_t i, size_t j) {
    return blade_llvm_c3(p + 2) - blade_llvm_c3(p + 2 - i) + blade_llvm_c2(p + 1 - i) - blade_llvm_c2(p + 1 - j);
}

/* Scratch, in doubles: every X(k) row packed once into NR-wide s-major
 * panels (ceil(p / NR) * NR * n) plus the W panel (MR x KC). The emitter
 * computes the SAME number (EmitLlvm.sym3TileScratchCells); this is the check. */
static size_t blade_llvm_sym3_scratch(size_t p, size_t n, size_t kcn) {
    const size_t nq = (p + BLADE_TILE_NR - 1) / BLADE_TILE_NR;
    return nq * BLADE_TILE_NR * n + BLADE_TILE_MR * kcn;
}

void blade_dsym3_tri_f64(int64_t p_, int64_t n_, const double* x, int64_t ldx_,
                         double* c, int64_t kc_max_,
                         int64_t scale_mode, double scale,
                         double* scratch, int64_t scratch_cells) {
    const size_t p = p_ > 0 ? (size_t)p_ : 0;
    const size_t n = n_ > 0 ? (size_t)n_ : 0;
    const size_t ldx = ldx_ > 0 ? (size_t)ldx_ : 0;
    const size_t kc_max = kc_max_ > 0 ? (size_t)kc_max_ : 336;
    size_t cells, i, j, k, off;
    if (p == 0) return;
    cells = blade_llvm_c3(p + 2);             /* = C(p + 2, 3) */
    if (n == 0) {
        /* The empty sum: every cell is the seed, +0.0 (then the tail). */
        for (i = 0; i < cells; i++) c[i] = 0.0;
        blade_llvm_tri_scale(c, cells, scale_mode, scale);
        return;
    }
#if defined(BLADE_TILES_VECTOR)
    {
        const size_t kcn = blade_llvm_tri_kc(n, kc_max);
        const size_t nq = blade_tile_npanels(p, BLADE_TILE_NR);   /* k panels, the ragged one FIRST */
        const int sk = (int)scale_mode;
        if (scratch != NULL && scratch_cells >= 0
            && (size_t)scratch_cells >= blade_llvm_sym3_scratch(p, n, kcn)) {
            double* xk = scratch;
            double* w = scratch + nq * BLADE_TILE_NR * n;
            size_t q, j0, pc, r;
            for (q = 0; q < nq; q++) {                 /* panel q: n x NR, s-major */
                const double* src[BLADE_TILE_NR];
                const size_t lo = blade_tile_panel_lo(0, p, BLADE_TILE_NR, q);
                const size_t rows = blade_tile_panel_hi(0, p, BLADE_TILE_NR, q) - lo;
                for (r = 0; r < rows; r++) src[r] = x + (lo + r) * ldx;
                blade_tile_pack_t(src, rows, BLADE_TILE_NR, n, xk + q * BLADE_TILE_NR * n);
            }
            /* One i: its whole sub-simplex, prefix blocks of MR j rows. The
             * scale is the last K block's tile store. */
            for (i = 0; i < p; i++) {
                for (j0 = i; j0 < p; j0 += BLADE_TILE_MR) {
                    const size_t mr = (p - j0) < BLADE_TILE_MR ? (p - j0) : BLADE_TILE_MR;
                    const size_t qlo = blade_tile_panel_first(0, p, BLADE_TILE_NR, j0);
                    double* crow[BLADE_TILE_MR];
                    const double* xj[BLADE_TILE_MR];
                    for (r = 0; r < BLADE_TILE_MR; r++)
                        crow[r] = (r < mr) ? c + blade_llvm_sym3_row(p, i, j0 + r) : NULL;
                    for (pc = 0; pc < n; pc += kcn) {          /* ascending: the chain order */
                        const size_t kc = (n - pc) < kcn ? (n - pc) : kcn;
                        const int first = (pc == 0);
                        const int skb = (pc + kc >= n) ? sk : BLADE_TILE_SCALE_NONE;
                        for (r = 0; r < mr; r++) xj[r] = x + (j0 + r) * ldx + pc;
                        blade_sym3_pack_w(x + i * ldx + pc, xj, mr, kc, w);
                        for (q = qlo; q < nq; q++) {
                            const size_t k0 = blade_tile_panel_lo(0, p, BLADE_TILE_NR, q);
                            const size_t nr = blade_tile_panel_hi(0, p, BLADE_TILE_NR, q) - k0;
                            const double* bp = xk + q * BLADE_TILE_NR * n + pc * BLADE_TILE_NR;
                            if (mr == BLADE_TILE_MR && nr == BLADE_TILE_NR && k0 >= j0 + (BLADE_TILE_MR - 1))
                                blade_tri_tile_full(kc, w, bp, crow, k0 - j0, first, skb, scale);
                            else
                                blade_tri_tile_edge(kc, w, bp, crow, j0, k0, mr, nr, 0, first, skb, scale);
                        }
                    }
                }
            }
            return;
        }
    }
#else
    (void)kc_max; (void)scratch; (void)scratch_cells;
#endif
    /* No vector extensions, or a scratch too small: the per-cell loop the
     * tile replaces, verbatim in arithmetic (a +0.0 seed, s ascending, the
     * product `(a*b)*c` in source factor order), then the scale. */
    off = 0;
    for (i = 0; i < p; i++) {
        const double* a = x + i * ldx;
        for (j = i; j < p; j++) {
            const double* b = x + j * ldx;
            for (k = j; k < p; k++) {
                const double* d = x + k * ldx;
                double acc = 0.0;
                size_t s;
                for (s = 0; s < n; s++) acc += a[s] * b[s] * d[s];
                c[off++] = blade_llvm_scale1(acc, scale_mode, scale);
            }
        }
    }
}
