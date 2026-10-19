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
 * THE OPERATION (the same as dsyrk_tri's, see blade_tiles.h for the
 * arithmetic contract): C(i, j) = sum_s X(i, s) X(j, s) for i + S <= j < p,
 * over Blade's packed triangle -- the LLVM lane's pool is ONE flat buffer,
 * row i starting at cell row_base(i) = i(2p - i + 1)/2 - i S, row-major
 * left-justified (cell (i, j) at row_base(i) + j - i - S). X is a dense
 * row-major p x k pool with row stride ldx. Every cell is ONE chain, +0.0
 * then `acc += X(i, s) * X(j, s)` for s ascending, then -- after the whole
 * triangle -- the kernel's scalar tail, if any, applied to the FINISHED sum
 * exactly as the per-cell nest applies it (`/ d` or `* s`; never a
 * reciprocal). Bitwise the per-cell serial prodsum under -ffp-contract=off;
 * the one-fma-per-step chain under =fast, the same bits as the C++ lane's
 * tile (`blade test llvm-tiles`).
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

/* Scratch the tile needs, in doubles: the B panel (KC x NC padded to NR) plus
 * the A micro-panel (MR x KC). The emitter computes the SAME number from the
 * literal extents (EmitLlvm.syrkTileScratchCells) and passes the capacity it
 * allocated; this is the check, not the source of truth. */
static size_t blade_llvm_tri_scratch(size_t p, size_t kcn) {
    const size_t nc = p < BLADE_LLVM_TRI_NC ? p : BLADE_LLVM_TRI_NC;
    const size_t npad = (nc + BLADE_TILE_NR - 1) / BLADE_TILE_NR * BLADE_TILE_NR;
    return kcn * npad + BLADE_TILE_MR * kcn;
}

/* The scalar tail over the finished triangle. mode 0: none; 1: divide by s;
 * 2: multiply by s. Cell order is irrelevant (each cell is independent). */
static void blade_llvm_tri_scale(double* c, size_t cells, int64_t mode, double s) {
    size_t t;
    if (mode == 1) {
        for (t = 0; t < cells; t++) c[t] = c[t] / s;
    } else if (mode == 2) {
        for (t = 0; t < cells; t++) c[t] = c[t] * s;
    }
}

void blade_dsyrk_tri_f64(int64_t p_, int64_t k_, const double* x, int64_t ldx_,
                         double* c, int64_t strict, int64_t kc_max_,
                         int64_t scale_mode, double scale,
                         double* scratch, int64_t scratch_cells) {
    const size_t p = p_ > 0 ? (size_t)p_ : 0;
    const size_t k = k_ > 0 ? (size_t)k_ : 0;
    const size_t ldx = ldx_ > 0 ? (size_t)ldx_ : 0;
    const size_t S = strict != 0 ? 1 : 0;
    const size_t kc_max = kc_max_ > 0 ? (size_t)kc_max_ : 336;
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
            && (size_t)scratch_cells >= blade_llvm_tri_scratch(p, kcn)) {
            for (jc = 0; jc < p; jc += BLADE_LLVM_TRI_NC) {
                const size_t nc = (p - jc) < BLADE_LLVM_TRI_NC ? (p - jc) : BLADE_LLVM_TRI_NC;
                const size_t npad = (nc + BLADE_TILE_NR - 1) / BLADE_TILE_NR * BLADE_TILE_NR;
                const size_t jEnd = jc + nc;
                const size_t iTop = (jEnd - S) < p ? (jEnd - S) : p;
                double* bp = scratch;
                double* ap = scratch + kcn * npad;
                for (pc = 0; pc < k; pc += kcn) {          /* ascending: the chain order */
                    const size_t kc = (k - pc) < kcn ? (k - pc) : kcn;
                    const int first = (pc == 0);
                    size_t jr, i0, r, q;
                    for (jr = 0; jr < nc; jr += BLADE_TILE_NR) {
                        const double* src[BLADE_TILE_NR];
                        const size_t rows = (nc - jr) < BLADE_TILE_NR ? (nc - jr) : BLADE_TILE_NR;
                        for (q = 0; q < rows; q++) src[q] = x + (jc + jr + q) * ldx + pc;
                        blade_tile_pack_t(src, rows, BLADE_TILE_NR, kc, bp + jr * kc);
                    }
                    for (i0 = 0; i0 < iTop; i0 += BLADE_TILE_MR) {
                        const size_t mr = (iTop - i0) < BLADE_TILE_MR ? (iTop - i0) : BLADE_TILE_MR;
                        const double* src[BLADE_TILE_MR];
                        double* crow[BLADE_TILE_MR];
                        size_t jlo;
                        for (r = 0; r < mr; r++) src[r] = x + (i0 + r) * ldx + pc;
                        blade_tile_pack_t(src, mr, BLADE_TILE_MR, kc, ap);
                        /* row pointers at cell (i, i + S); never formed past the
                         * triangle (rows >= mr stay NULL and are never read) */
                        for (r = 0; r < BLADE_TILE_MR; r++)
                            crow[r] = (r < mr) ? c + blade_llvm_tri_row(p, i0 + r, S) : NULL;
                        jlo = (i0 + S > jc) ? (i0 + S - jc) / BLADE_TILE_NR * BLADE_TILE_NR : 0;
                        for (jr = jlo; jr < nc; jr += BLADE_TILE_NR) {
                            const size_t j0 = jc + jr;
                            const size_t nr = (nc - jr) < BLADE_TILE_NR ? (nc - jr) : BLADE_TILE_NR;
                            if (mr == BLADE_TILE_MR && nr == BLADE_TILE_NR
                                && j0 >= i0 + (BLADE_TILE_MR - 1) + S)
                                blade_tri_tile_full(kc, ap, bp + jr * kc, crow, j0 - i0 - S, first);
                            else
                                blade_tri_tile_edge(kc, ap, bp + jr * kc, crow, i0, j0, mr, nr, S, first);
                        }
                    }
                }
            }
            blade_llvm_tri_scale(c, cells, scale_mode, scale);
            return;
        }
    }
#else
    (void)kc_max; (void)scratch; (void)scratch_cells; (void)jc; (void)pc;
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
