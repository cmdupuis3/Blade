// The ORDERED ORBIT SCATTER (src/OrbitFold.fs, planStrict; round-2 item A2).
//
// A logical walk over a packed symmetric pool -- r nested folds over range<I>
// whose innermost term is f(S(v_1, .., v_r)) -- computed in ONE pass over the
// pool while every chain of the source (each row's fold, each pair's leaf
// fold, the outer fold) receives EXACTLY its source operand sequence, in its
// source order, from its own seed, through its own kernel applied
// accumulator-first. Nothing is reassociated: lanes below are DISTINCT
// chains, never one chain split, so no reorder licence is consumed and no
// `omp simd` / reduction / ivdep pragma may ever be placed on these loops.
// Bitwise the walk under BLADE_FP_REPRO=1 BLADE_FP_CONTRACT=off (the corpus /
// `blade test orbit-scatter` pins it); under -ffp-contract=fast the compiler
// may contract differently in the two forms (the walk does so per context too).
//
// Pool layout (nested_array_utilities, lex order of canonical tuples): a
// SymIdx<2, m> triangle's row r starts at tri_row(m, r); a SymIdx<3, p> pool is
// p consecutive slices, slice i the SymIdx<2, p - i> triangle of (i, i + r,
// i + c), r <= c.
//
// G (row group) and B (column block) are SPEED-ONLY (BLADE_TILE_TUNE scatter_g
// / scatter_b): the order proof holds for any group sizes, so no bit depends
// on them.
#ifndef BLADE_ORBIT_SCATTER_HPP
#define BLADE_ORBIT_SCATTER_HPP

#include <cstddef>
#include <cstring>
#include <algorithm>
#include <type_traits>

namespace blade_scatter {

// First pool cell of row r of a packed SymIdx<2, m> triangle.
inline size_t tri_row(size_t m, size_t r) { return r * m - (r * (r - 1)) / 2; }

#if defined(__GNUC__) || defined(__clang__)
#define BLADE_SCATTER_VECTOR 1
typedef double v4d __attribute__((vector_size(32)));
#endif

// The G-row-group scatter over ONE packed triangle of extent m whose row r
// starts at S + tri_row(m, r): chains R[0 .. m) (seeded by the caller) are fed
//   * rows i0 .. i0+G-1 together: their G x G diagonal block in block-lex
//     order (cell (a, b) feeds R[i0+a] and, a != b, R[i0+b]); then per column
//     j >= i0+G ascending, in blocks of B columns: the G row chains each take
//     their own term z(i0+m, j), and column chain R[j] takes z(i0, j) ..
//     z(i0+G-1, j) in row order;
//   * a tail of fewer than G rows one row at a time (the G = 1 form).
// So R[r] receives rows 0..r-1's column-r terms ascending, then its own row's
// terms ascending: logical row r in order (any group sizes).
// THIRD (rank-3 slices): every term z(r, c) of a row r >= 1 is ALSO fed to the
// chain at H + tri_row(hn, hRow0 + r) + (c - r) -- the pair chain (hRow0 + r,
// hRow0 + c) -- from the SAME term (the pool is read once).
// VADD: the row chains' kernel is the builtin `+` on double and G % 4 == 0 --
// one vertical vector add per column for every 4 row chains (lane m is chain
// R[i0+m]; still one add per chain per column, in column order).
template <int G, int B, bool VADD, bool THIRD, class T, class E, class F, class K>
inline void tri(const E* S, size_t m, T* R, T* H, size_t hRow0, size_t hn, const F& f, const K& k) {
    alignas(64) T Z[G][B];
    size_t i0 = 0;
    for (; i0 + (size_t)G <= m; i0 += G) {
        size_t rs[G];
        size_t hrs[G];
        for (int a = 0; a < G; a++) rs[a] = tri_row(m, i0 + a);
        if (THIRD) for (int a = 0; a < G; a++) hrs[a] = tri_row(hn, hRow0 + i0 + a);
        // (1) the diagonal block, block-lex
        for (int a = 0; a < G; a++)
            for (int b = a; b < G; b++) {
                const T z = f(S[rs[a] + (size_t)(b - a)]);
                R[i0 + a] = k(R[i0 + a], z);
                if (a != b) R[i0 + b] = k(R[i0 + b], z);
                if (THIRD && i0 + a >= 1) H[hrs[a] + (size_t)(b - a)] = k(H[hrs[a] + (size_t)(b - a)], z);
            }
        T rc[G];
        for (int a = 0; a < G; a++) rc[a] = R[i0 + a];
        for (size_t jb = i0 + G; jb < m; jb += B) {
            const size_t len = std::min<size_t>((size_t)B, m - jb);
            // (2a) the term map: G contiguous runs of the pool
            for (int a = 0; a < G; a++) {
                const E* src = S + rs[a] + (jb - (i0 + a));
                for (size_t jj = 0; jj < len; jj++) Z[a][jj] = f(src[jj]);
            }
            // (2b) the column chains: lane jj is the distinct chain R[jb + jj],
            // fed its G terms in row order
            for (size_t jj = 0; jj < len; jj++) {
                T c = R[jb + jj];
                for (int a = 0; a < G; a++) c = k(c, Z[a][jj]);
                R[jb + jj] = c;
            }
            // (2c) the row chains: one application per chain per column, in
            // column order
#ifdef BLADE_SCATTER_VECTOR
            if constexpr (VADD && G % 4 == 0 && std::is_same<T, double>::value) {
                v4d acc[G / 4];
                std::memcpy(acc, rc, sizeof acc);
                for (size_t jj = 0; jj < len; jj++)
                    for (int q = 0; q < G / 4; q++) {
                        v4d t = { Z[4 * q][jj], Z[4 * q + 1][jj], Z[4 * q + 2][jj], Z[4 * q + 3][jj] };
                        acc[q] = acc[q] + t;
                    }
                std::memcpy(rc, acc, sizeof acc);
            } else
#endif
            {
                for (size_t jj = 0; jj < len; jj++)
                    for (int a = 0; a < G; a++) rc[a] = k(rc[a], Z[a][jj]);
            }
            // (2d) the third event (rank-3 slices), from the same term block
            if (THIRD) {
                for (int a = 0; a < G; a++) {
                    if (i0 + a < 1) continue;
                    T* dst = H + hrs[a] + (jb - (i0 + a));
                    for (size_t jj = 0; jj < len; jj++) dst[jj] = k(dst[jj], Z[a][jj]);
                }
            }
        }
        for (int a = 0; a < G; a++) R[i0 + a] = rc[a];
    }
    // the tail: one row at a time (diagonal cell, then its columns ascending)
    for (; i0 < m; i0++) {
        const E* row = S + tri_row(m, i0);
        T* hrow = THIRD ? H + tri_row(hn, hRow0 + i0) : nullptr;
        const bool third = THIRD && i0 >= 1;
        for (size_t j = i0; j < m; j++) {
            const T z = f(row[j - i0]);
            R[i0] = k(R[i0], z);
            if (j != i0) R[j] = k(R[j], z);
            if (third) hrow[j - i0] = k(hrow[j - i0], z);
        }
    }
}

// Rank 2: fold_out(so, [ fold_in(si, [ f(S(i, j)) for j ]) for i ]).
template <int G, int B, bool VADD, class T, class E, class F, class KI, class KO>
inline T scatter2(const E* S, size_t n, T si, T so, const F& f, const KI& ki, const KO& ko) {
    T* R = new T[n];
    for (size_t i = 0; i < n; i++) R[i] = si;
    tri<G, B, VADD, false>(S, n, R, (T*)nullptr, 0, 0, f, ki);
    T out = so;
    for (size_t i = 0; i < n; i++) out = ko(out, R[i]);
    delete[] R;
    return out;
}

// Rank 3: fold_out(so, [ fold_mid(sm, [ fold_leaf(sl, [ f(S(i, j, k)) for k ])
// for j ]) for i ]). The leaf chains are shared per unordered pair (H, packed
// SymIdx<2, p>): slice i's group scatter feeds H row i (chains H[i, i + r]) and,
// by the third event, H[j, k] for j > i. Then the middle and outer folds are
// the rank-2 scatter over the retained H with f = identity.
template <int G, int B, bool VADDL, bool VADDM, class T, class E, class F, class KL, class KM, class KO>
inline T scatter3(const E* S, size_t p, T sl, T sm, T so, const F& f, const KL& kl, const KM& km, const KO& ko) {
    const size_t hc = p * (p + 1) / 2;
    T* H = new T[hc];
    for (size_t c = 0; c < hc; c++) H[c] = sl;
    size_t slice = 0;
    for (size_t i = 0; i < p; i++) {
        const size_t m = p - i;
        tri<G, B, VADDL, true>(S + slice, m, H + tri_row(p, i), H, i, p, f, kl);
        slice += m * (m + 1) / 2;
    }
    T out = scatter2<G, B, VADDM>(H, p, sm, so, [](T x) -> T { return x; }, km, ko);
    delete[] H;
    return out;
}

} // namespace blade_scatter

#endif // BLADE_ORBIT_SCATTER_HPP
