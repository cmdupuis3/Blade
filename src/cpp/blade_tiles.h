/* blade_tiles.h -- the PURE arithmetic core of Blade's packed-triangle tiles.
 *
 * C99 AND C++ (no templates, no allocation, no threads, no TLS), so ONE copy of
 * the arithmetic serves both lanes: the C++ lane's driver (blade_packed_gemm.hpp,
 * blade_pgemm::dsyrk_tri) includes it today; the LLVM lane's serial driver
 * (bench/notes/microkernel-sketches-review.md item M6) is meant to compile the
 * same header as a C translation unit. Everything here works on caller-provided
 * buffers and row pointers, so neither driver's array representation leaks in:
 * a driver hands the pack the W source ROWS it wants and the tile the MR output
 * ROW POINTERS it owns.
 *
 * THE OPERATION. Same-array row contraction over Blade's packed triangle,
 *     C(i, j) = sum_s X(i, s) * X(j, s),   i + S <= j < p,
 * S = 0 the inclusive (SymIdx) triangle, S = 1 the strict one. Output row i is
 * LEFT-JUSTIFIED row-major: crow_i[j - i - S] is cell (i, j) -- exactly the row
 * footprint `allocate<>` builds for a packed rank-2 group, so the tile writes the
 * pool in place.
 *
 * THE ARITHMETIC CONTRACT (bench/notes/phase2-microkernel-plan.md section 3).
 * Every output cell is ONE chain: seeded +0.0 (the serial prodsum's `__ps = 0`),
 * then `acc += X(i, s) * X(j, s)` for s ASCENDING, the product in source factor
 * order (row i -- the broadcast -- first). The sample loop runs OUTSIDE the
 * register tile and is never peeled, split or padded; a K block stores the exact
 * binary64 partial and the next block reloads it as its initial accumulator
 * (never `C += block`), which adds no rounding. Hence:
 *   - under -ffp-contract=off the tile is bitwise the serial per-cell dot by
 *     construction (no operation may fuse in either);
 *   - under -ffp-contract=fast it is written in VECTOR EXTENSIONS (`c += a * b`),
 *     never FMA intrinsics, so contraction is governed by the same flag as the
 *     scalar `__ps += a[t] * b[t]` -- both fuse, verified per compiler by the
 *     raw-bit tests (cpp/syrk_tile_tests.cpp) and a vfmadd count.
 * Zero padding (rows past the edge, columns past p) feeds only lanes that are
 * computed and never stored; below-diagonal lanes of a tile that straddles the
 * diagonal are likewise computed and never stored.
 *
 * TILE: MR = 6 broadcast rows (the smaller index i) x NR = 8 vector lanes (the
 * larger index j, contiguous in the output row): 12 ymm accumulators + 2 B
 * vectors + 1 broadcast = 15 of 16 ymm, 8 loads per 12 vector FMAs (0.67 per
 * FMA, vs 2 for the per-cell dot). The expert Fortran (bench/kernels/gram/
 * expert.f90) vectorizes over i because ITS triangle is column-packed; Blade's
 * is row-packed, so the same tile is transposed onto j.
 */
#ifndef BLADE_TILES_H
#define BLADE_TILES_H

#include <stddef.h>
#include <string.h>

#define BLADE_TILE_MR 6
#define BLADE_TILE_NR 8

#if defined(__GNUC__) || defined(__clang__)
#define BLADE_TILES_VECTOR 1

typedef double blade_tile_v4d __attribute__((vector_size(32)));

#define BLADE_TILE_INLINE static inline __attribute__((always_inline))

/* Transposing pack: dst[s * W + r] = src[r][s] for r < rows, s < kc; rows
 * rows..W-1 are zero (padding lanes: computed, never stored). `src[r]` already
 * points at the block's first sample (the driver adds pc). */
static inline void blade_tile_pack_t(const double* const* src, size_t rows, size_t W,
                                     size_t kc, double* dst) {
    size_t r, s;
    for (r = 0; r < rows; r++) {
        const double* row = src[r];
        for (s = 0; s < kc; s++) dst[s * W + r] = row[s];
    }
    for (r = rows; r < W; r++)
        for (s = 0; s < kc; s++) dst[s * W + r] = 0.0;
}

/* The s loop: c[r][h] += A(r, s) * B(s, 4h..4h+3) for s ascending. ap is the
 * s-major MR-wide A micro-panel, bp the s-major NR-wide B micro-panel. */
BLADE_TILE_INLINE void blade_tri_kloop(size_t kc, const double* ap, const double* bp,
                                       blade_tile_v4d c[BLADE_TILE_MR][2]) {
    size_t s, r;
    for (s = 0; s < kc; s++) {
        blade_tile_v4d b0, b1;
        memcpy(&b0, bp + s * BLADE_TILE_NR, 32);
        memcpy(&b1, bp + s * BLADE_TILE_NR + 4, 32);
        for (r = 0; r < BLADE_TILE_MR; r++) {
            const double a = ap[s * BLADE_TILE_MR + r];
            const blade_tile_v4d av = {a, a, a, a};
            c[r][0] += av * b0;              /* X(i, s) * X(j, s): source factor order */
            c[r][1] += av * b1;
        }
    }
}

/* INTERIOR tile: all MR x NR lanes real (mr = MR, nr = NR and the tile lies on
 * or above the diagonal band, j0 >= i0 + MR - 1 + S). crow[r] points at cell
 * (i0 + r, i0 + r + S); d = j0 - i0 - S, so cell (i0 + r, j0 + q) is
 * crow[r][d - r + q] with d - r >= 0 for every row. Direct vector reload and
 * store, no masks anywhere. */
BLADE_TILE_INLINE void blade_tri_tile_full(size_t kc, const double* ap, const double* bp,
                                           double* const* crow, size_t d, int first) {
    blade_tile_v4d c[BLADE_TILE_MR][2];
    size_t r;
    if (first) {
        for (r = 0; r < BLADE_TILE_MR; r++) {
            const blade_tile_v4d z = {0.0, 0.0, 0.0, 0.0};
            c[r][0] = z; c[r][1] = z;
        }
    } else {
        for (r = 0; r < BLADE_TILE_MR; r++) {
            const double* src = crow[r] + (d - r);
            memcpy(&c[r][0], src, 32);
            memcpy(&c[r][1], src + 4, 32);
        }
    }
    blade_tri_kloop(kc, ap, bp, c);
    for (r = 0; r < BLADE_TILE_MR; r++) {
        double* dst = crow[r] + (d - r);
        memcpy(dst, &c[r][0], 32);
        memcpy(dst + 4, &c[r][1], 32);
    }
}

/* EDGE tile: a ragged last row panel (mr < MR), a ragged last column panel
 * (nr < NR), or a tile straddling the diagonal. Lane (r, q) is cell
 * (i0 + r, j0 + q), REAL iff r < mr, q < nr and j0 + q >= i0 + r + S. A row whose
 * lanes are all real (nr = NR and j0 >= i0 + r + S) takes direct vector
 * reload/store; any other row is masked lane by lane. An offset is formed only
 * for a real lane, so no out-of-range pointer is ever computed; crow[r] is never
 * read for r >= mr. */
BLADE_TILE_INLINE void blade_tri_tile_edge(size_t kc, const double* ap, const double* bp,
                                           double* const* crow, size_t i0, size_t j0,
                                           size_t mr, size_t nr, size_t S, int first) {
    blade_tile_v4d c[BLADE_TILE_MR][2];
    size_t r, q;
    for (r = 0; r < BLADE_TILE_MR; r++) {
        double t[BLADE_TILE_NR] = {0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0};
        if (!first && r < mr) {
            const size_t i = i0 + r;
            if (nr == BLADE_TILE_NR && j0 >= i + S) {
                memcpy(t, crow[r] + (j0 - i - S), 64);
            } else {
                for (q = 0; q < nr; q++)
                    if (j0 + q >= i + S) t[q] = crow[r][j0 + q - i - S];
            }
        }
        memcpy(&c[r][0], t, 32);
        memcpy(&c[r][1], t + 4, 32);
    }
    blade_tri_kloop(kc, ap, bp, c);
    for (r = 0; r < mr; r++) {
        const size_t i = i0 + r;
        double t[BLADE_TILE_NR];
        memcpy(t, &c[r][0], 32);
        memcpy(t + 4, &c[r][1], 32);
        if (nr == BLADE_TILE_NR && j0 >= i + S) {
            memcpy(crow[r] + (j0 - i - S), t, 64);
        } else {
            for (q = 0; q < nr; q++)
                if (j0 + q >= i + S) crow[r][j0 + q - i - S] = t[q];
        }
    }
}

/* ===========================================================================
 * The kernel's SCALAR TAIL, applied to a FINISHED cell: the source's own
 * `<sum> / d` or `<sum> * s`, once, never folded into an operand, never a
 * reciprocal. A vector divide/multiply by a broadcast is correctly rounded per
 * lane, i.e. the scalar operation lane by lane. Kind is explicit -- a real
 * division by zero must still divide.
 * ======================================================================== */
#define BLADE_TILE_SCALE_NONE 0
#define BLADE_TILE_SCALE_DIV 1
#define BLADE_TILE_SCALE_MUL 2

BLADE_TILE_INLINE double blade_tile_scale1(double v, int sk, double sc) {
    return sk == BLADE_TILE_SCALE_DIV ? v / sc : sk == BLADE_TILE_SCALE_MUL ? v * sc : v;
}

BLADE_TILE_INLINE blade_tile_v4d blade_tile_scale4(blade_tile_v4d v, int sk, double sc) {
    const blade_tile_v4d s = {sc, sc, sc, sc};
    return sk == BLADE_TILE_SCALE_DIV ? v / s : sk == BLADE_TILE_SCALE_MUL ? v * s : v;
}

/* ===========================================================================
 * THE ANTISYM PAIR (bench/notes/microkernel-sketches-review.md M7).
 *     L(i, j) = P(i, j) - Q(i, j),   i < j < p   (the STRICT triangle, S = 1)
 *     P = sum_s X(i, s) * Y(j, s)    the source's prodsum(a(cx), b(cy))
 *     Q = sum_s X(j, s) * Y(i, s)    the source's prodsum(b(cx), a(cy))
 * X(r, s) = Z(r, cx, s), Y(r, s) = Z(r, cy, s). Each of P and Q is ONE chain
 * per cell -- seeded +0.0, ascending s, the product in SOURCE factor order
 * (P: the broadcast X(i) first; Q: the VECTOR X(j) first) -- and the
 * subtraction happens ONCE per cell after both chains complete (never a
 * per-s or per-block difference, which would re-associate). The whole sample
 * chain runs inside one tile visit (no K blocking, so no partial is ever
 * stored), which is the serial form's operation sequence verbatim: bitwise
 * under -ffp-contract=off by construction, and the one-fma-per-step chain
 * under =fast (both verified by cpp/row_contraction_tile_tests.cpp).
 *
 * Output: row i of the strict packed triangle is crow_i[j - i - 1].
 * ======================================================================== */

/* The Q pass's s loop: c[r][h] += B(s, 4h..4h+3) * A(r, s) -- the VECTOR
 * operand first (Q's source product is X(j, s) * Y(i, s)). */
BLADE_TILE_INLINE void blade_tri_kloop_vb(size_t kc, const double* ap, const double* bp,
                                          blade_tile_v4d c[BLADE_TILE_MR][2]) {
    size_t s, r;
    for (s = 0; s < kc; s++) {
        blade_tile_v4d b0, b1;
        memcpy(&b0, bp + s * BLADE_TILE_NR, 32);
        memcpy(&b1, bp + s * BLADE_TILE_NR + 4, 32);
        for (r = 0; r < BLADE_TILE_MR; r++) {
            const double a = ap[s * BLADE_TILE_MR + r];
            const blade_tile_v4d av = {a, a, a, a};
            c[r][0] += b0 * av;              /* X(j, s) * Y(i, s): source factor order */
            c[r][1] += b1 * av;
        }
    }
}

/* Store P - Q (then the scale) for the real lanes of one row of a tile: lane
 * q is cell (i, j0 + q), real iff q < nr and j0 + q >= i + 1. A row whose
 * lanes are all real takes the vector path; an offset is formed only for a
 * real lane. NR = 8 (pv/qv hold two vectors) or 4 (one). */
BLADE_TILE_INLINE void blade_anti_store_row(double* crow, size_t i, size_t j0, size_t nr,
                                            size_t NRW, const double* pr, const double* qr,
                                            int sk, double sc) {
    size_t q;
    if (nr == NRW && j0 >= i + 1) {
        double* dst = crow + (j0 - i - 1);
        for (q = 0; q < NRW; q += 4) {
            blade_tile_v4d pv, qv, lv;
            memcpy(&pv, pr + q, 32);
            memcpy(&qv, qr + q, 32);
            lv = blade_tile_scale4(pv - qv, sk, sc);
            memcpy(dst + q, &lv, 32);
        }
    } else {
        for (q = 0; q < nr; q++)
            if (j0 + q >= i + 1) crow[j0 + q - i - 1] = blade_tile_scale1(pr[q] - qr[q], sk, sc);
    }
}

/* SEQUENTIAL 6 x 8 (the reviewed default): the gram tile's register budget
 * (12 accumulators + 2 B vectors + 1 broadcast) run TWICE -- P with the
 * X(i) / Y(j) panels, spilled to 48 stack doubles, then Q with Y(i) / X(j) --
 * and one subtraction per real lane. axp/ayp: s-major 6-wide X(i) / Y(i)
 * panels; bxp/byp: s-major 8-wide X(j) / Y(j) panels; crow[r] for r < mr. */
BLADE_TILE_INLINE void blade_anti_tile_6x8(size_t n, const double* axp, const double* ayp,
                                           const double* bxp, const double* byp,
                                           double* const* crow, size_t i0, size_t j0,
                                           size_t mr, size_t nr, int sk, double sc) {
    blade_tile_v4d c[BLADE_TILE_MR][2];
    double P[BLADE_TILE_MR][BLADE_TILE_NR];
    double Q[BLADE_TILE_NR];
    size_t r;
    for (r = 0; r < BLADE_TILE_MR; r++) {
        const blade_tile_v4d z = {0.0, 0.0, 0.0, 0.0};
        c[r][0] = z; c[r][1] = z;
    }
    blade_tri_kloop(n, axp, byp, c);         /* P: X(i, s) * Y(j, s) */
    for (r = 0; r < BLADE_TILE_MR; r++) {
        const blade_tile_v4d z = {0.0, 0.0, 0.0, 0.0};
        memcpy(P[r], &c[r][0], 32);
        memcpy(P[r] + 4, &c[r][1], 32);
        c[r][0] = z; c[r][1] = z;
    }
    blade_tri_kloop_vb(n, ayp, bxp, c);      /* Q: X(j, s) * Y(i, s) */
    for (r = 0; r < mr; r++) {
        memcpy(Q, &c[r][0], 32);
        memcpy(Q + 4, &c[r][1], 32);
        blade_anti_store_row(crow[r], i0 + r, j0, nr, BLADE_TILE_NR, P[r], Q, sk, sc);
    }
}

/* PAIRED 4 x 4 (the measured alternative, BLADE_ANTISYM_TILE=4x4): P and Q
 * in ONE pass -- 4 + 4 vector accumulators, the two 4-wide B vectors X(j) and
 * Y(j), 8 broadcasts per s (10 loads per 8 FMAs) and no spill. Same chains,
 * same factor order, same single subtraction. Panels 4-wide, s-major. */
#define BLADE_ANTI4_MR 4
#define BLADE_ANTI4_NR 4

BLADE_TILE_INLINE void blade_anti_tile_4x4(size_t n, const double* axp, const double* ayp,
                                           const double* bxp, const double* byp,
                                           double* const* crow, size_t i0, size_t j0,
                                           size_t mr, size_t nr, int sk, double sc) {
    blade_tile_v4d pc[BLADE_ANTI4_MR], qc[BLADE_ANTI4_MR];
    double P[BLADE_ANTI4_NR], Q[BLADE_ANTI4_NR];
    size_t r, s;
    for (r = 0; r < BLADE_ANTI4_MR; r++) {
        const blade_tile_v4d z = {0.0, 0.0, 0.0, 0.0};
        pc[r] = z; qc[r] = z;
    }
    for (s = 0; s < n; s++) {
        blade_tile_v4d xj, yj;
        memcpy(&xj, bxp + s * BLADE_ANTI4_NR, 32);
        memcpy(&yj, byp + s * BLADE_ANTI4_NR, 32);
        for (r = 0; r < BLADE_ANTI4_MR; r++) {
            const double xa = axp[s * BLADE_ANTI4_MR + r];
            const double ya = ayp[s * BLADE_ANTI4_MR + r];
            const blade_tile_v4d xv = {xa, xa, xa, xa};
            const blade_tile_v4d yv = {ya, ya, ya, ya};
            pc[r] += xv * yj;                /* P: X(i, s) * Y(j, s) */
            qc[r] += xj * yv;                /* Q: X(j, s) * Y(i, s) */
        }
    }
    for (r = 0; r < mr; r++) {
        memcpy(P, &pc[r], 32);
        memcpy(Q, &qc[r], 32);
        blade_anti_store_row(crow[r], i0 + r, j0, nr, BLADE_ANTI4_NR, P, Q, sk, sc);
    }
}

/* ===========================================================================
 * THE RANK-3 SIMPLEX (review M8): T(i, j, k) = (sum_s X(i,s) X(j,s) X(k,s)),
 * i <= j <= k < p, then the kernel's scale. The serial chain's term is
 * `(X(i,s) * X(j,s)) * X(k,s)` -- C++'s left-to-right product, the emitted
 * `__ps += a[t] * b[t] * c[t]` -- so its FIRST product reads only the two
 * outer levels: the W panel W(r, s) = round(X(i, s) * X(j0 + r, s)) is that
 * product formed ONCE per (i, j) prefix and reused over every k panel. The
 * per-term arithmetic is unchanged (round(a*b), then times c, then add --
 * or, under =fast, round(a*b) then one fma, as the serial loop contracts),
 * so the tile is the gram tile itself over (row = j, column = k): the driver
 * hands blade_tri_tile_full / _edge the W panel as A and an X(k) panel as B,
 * with the row pointers of the (i, j) rows of the packed simplex.
 * ======================================================================== */

/* W panel: dst[s * MR + r] = X(i, s) * X(j_r, s) for r < rows (factor order
 * i first), zero past rows (padding lanes: computed, never stored). xi and
 * xj[r] already point at the block's first sample. */
static inline void blade_sym3_pack_w(const double* xi, const double* const* xj, size_t rows,
                                     size_t kc, double* dst) {
    size_t r, s;
    for (r = 0; r < rows; r++) {
        const double* row = xj[r];
        for (s = 0; s < kc; s++) dst[s * BLADE_TILE_MR + r] = xi[s] * row[s];
    }
    for (r = rows; r < BLADE_TILE_MR; r++)
        for (s = 0; s < kc; s++) dst[s * BLADE_TILE_MR + r] = 0.0;
}

#endif /* __GNUC__ || __clang__ */

#endif /* BLADE_TILES_H */
