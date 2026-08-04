#pragma once
// =============================================================================
//  orbit_wreath_utilities.hpp
//  Blade DSL Runtime Support Library -- OrbIdx wreath-class storage machinery
// =============================================================================
//
//  Not yet wired into codegen: nothing in src/ includes it. Its job is to make the four `OrbIdx` bijections exist and be
//  checkable -- src/cpp/orb_wreath_tests.cpp (run via `blade test orbwreath`) validates every function here against a
//  brute-force ground truth built in the same translation unit.
//  THE CLASS (docs/plan-orbit-index-types.md Sec 2). An OrbIdx class is a flat list of levels over one extent `n`:
//      OrbIdx<[(r1,s1), (r2,s2), ..., (rd,sd)], n>
//  The list is OUTERMOST-LAST: level 1 is the innermost tie, level d the outermost. Level i groups `ri` sub-blocks of
//  the level below and orders them by their canonical keys in lexicographic order -- nondecreasing when si = '+',
//  strictly increasing when si = '-'. The class's group is the iterated wreath product S_r1 wr S_r2 wr ... wr S_rd
//  acting on `prod_i ri` raw axes; the sign list picks one of the 2^d characters that group admits.
//  In this header a level is a TYPE, `orb_level<R, Pos>`, and a class is a parameter pack of them in the doc's order
//  (outermost last):
//      OrbIdx<[(2,-),(2,+)],n>   (the Riemann shape)
//          ==  orb_level<2,false>, orb_level<2,true>
//  Everything that consults a sign or a level's structure does so through `if constexpr` on a template parameter: there
//  is NO runtime branch on a sign anywhere below (the equality/"diagonal" segment of the traversal nest is
//  `if constexpr (GE)`, so a '-' level's instantiation contains no trace of it, not even a skipped test).
//  WHAT IS HERE
//    orb_cell_count<Levels...>(n)          Sec 4 of plan-orbit-index-types.md
//    orb_visit<Levels...>(n, visitor)      Sec 2 of plan-orbidx-bijections.md
//    orb_canon<Levels...>(tuple, out)      Sec 5 of plan-orbit-index-types.md
//    orb_rank<Levels...>(canonical, n)     Sec 3 of plan-orbidx-bijections.md
//    orb_unrank<Levels...>(r, n, out)      Sec 3, the greedy inverse
//  ... and the STORAGE LAYER built on top of those five (Sec 1: "two access paths, dual views"):
//    orb_read_checked<T,Levels...>         Sec 2 of plan-orbidx-decompaction.md
//    orb_read<T,Levels...>                 the same, with a precondition
//    orb_write_canonical<T,Levels...>      canonical-cell store
//    orb_skeleton<T,Levels...>             the nested-pointer dual view
//  HOUSE CONVENTIONS FOLLOWED
//    * Storage order is ascending-lex DFS, exactly as `build_skeleton` (nested_array_utilities.hpp:217-264) lays out
//      the pool: traversal order IS allocation order. "Rank agrees with the Sec 2 nest's visit order" is a stated
//      invariant with its own test -- a read->write roundtrip cannot catch an order mismatch, so the test compares
//      against an independent brute-force oracle instead.
//    * Canonicalization mirrors `canon_fold` (nested_array_utilities.hpp:846): count inversions with an O(R^2) double
//      loop, then sort; a repeated key at a strict level means the value is not stored -> implicit zero.
//    * The '+' -> strict reduction `s_j = k_j + (j-1)` in orb_rank realizes the same strict<->weak correspondence
//      `canon_left_justify` uses (nested_array_utilities.hpp:861-868), in a DIFFERENT encoding (that helper stores
//      successive differences, this one a fixed per-position shift): same bijection, not the same map.
//    * All cell/offset arithmetic is CHECKED. Sec 7.2 of plan-orbit-index-types.md names silent int64 wraparound -- not
//      stack exhaustion -- as the failure mode to guard, because each level's output is the next level's extent.
//      `binom_checked` is a transcription of `binomChecked` in proofs/OrbitEnum.fsx: the gcd reduction makes every
//      intermediate equal to C(m-r+i, i) <= C(m,r), so the multiply-then-divide loop cannot wrap even TRANSIENTLY. An
//      overflow report here means the true value exceeds int64, never that an intermediate did.
//  C++20, header-only, no dependency beyond <cstdint>/<cstddef>/<cassert>.
// =============================================================================

#include <cassert>
#include <cstddef>
#include <cstdint>

/// Diagnostic hook for the ONE precondition this header states (orb_read's in-domain tuple; see "THE DOMAIN CONTRACT"
/// below). Defined only if the translation unit has not already supplied one, so a harness that must EXERCISE the
/// violation path -- or an embedded build with no <cassert> -- can substitute its own without patching the header. The
/// default is a plain assert: diagnostic in a checked build, gone under NDEBUG. Either way, the violating call still
/// returns T(0) without ever indexing the pool.
#ifndef ORB_ASSERT
#  define ORB_ASSERT(cond, msg) assert((cond) && (msg))
#endif

namespace orbit_wreath_utilities {

    // Level and class description (compile time)

    /// One wreath level: `R` sub-blocks tied with character `Pos` (true = '+', invariant/nondecreasing; false = '-',
    /// sgn/strictly increasing). A level with R == 1 is the trivial group and a no-op at either sign;
    /// plan-orbit-index-types.md Sec 7.2 normalizes those away before they reach here, but they are harmless if they do.
    template<int R, bool Pos>
    struct orb_level {
        static_assert(R >= 1, "orb_level: rank must be >= 1");
        static constexpr int  rank = R;
        static constexpr bool pos  = Pos;
    };

    /// Internal class representation: a list of levels, OUTERMOST-FIRST. (The public API takes them outermost-last, per
    /// the doc's spelling; the two orders are bridged by `detail::make_list`.) `orb_list<>` is the scalar class
    /// `OrbIdx<[],n>` == `Idx<n>`: one axis, no tie.
    template<class... Ls>
    struct orb_list {};

    /// Sentinel returned by the checked-arithmetic paths. Every quantity they compute is non-negative, so -1 is unambiguous.
    inline constexpr int64_t ORB_OVERFLOW = -1;

    namespace detail {

        // Pack reversal: doc order (outermost LAST) -> internal (FIRST)
        template<class Acc, class... Ls> struct rev;
        template<class... A>
        struct rev<orb_list<A...>> { using type = orb_list<A...>; };
        template<class... A, class L0, class... Rest>
        struct rev<orb_list<A...>, L0, Rest...> {
            using type = typename rev<orb_list<L0, A...>, Rest...>::type;
        };

        template<class... Ls>
        using make_list = typename rev<orb_list<>, Ls...>::type;

        // Raw axis count: prod_i r_i (rank of the tensor the class describes)
        template<class C> struct axes;
        template<> struct axes<orb_list<>> { static constexpr int value = 1; };
        template<class L, class... Rest>
        struct axes<orb_list<L, Rest...>> {
            static constexpr int value = L::rank * axes<orb_list<Rest...>>::value;
        };

        // Checked non-negative int64 arithmetic (proofs/OrbitEnum.fsx: addChecked / mulChecked / binomChecked)

        inline int64_t gcd64(int64_t a, int64_t b) {
            while (b != 0) { int64_t t = a % b; a = b; b = t; }
            return a;
        }

        /// a + b, or ORB_OVERFLOW. Both operands are expected non-negative; a negative result is reported as overflow so that a corrupted input cannot silently produce a plausible offset.
        inline int64_t add_checked(int64_t a, int64_t b) {
            if (a < 0 || b < 0) return ORB_OVERFLOW;
            if (a > INT64_MAX - b) return ORB_OVERFLOW;
            return a + b;
        }

        /// a * b, or ORB_OVERFLOW.
        inline int64_t mul_checked(int64_t a, int64_t b) {
            if (a < 0 || b < 0) return ORB_OVERFLOW;
            if (a == 0 || b == 0) return 0;
            if (a > INT64_MAX / b) return ORB_OVERFLOW;
            return a * b;
        }

        /// Exact C(m, r) in int64, or ORB_OVERFLOW. The gcd reduction is load-bearing: at step i the accumulator equals
        /// C(m-r+i, i) <= C(m,r), so no intermediate ever exceeds the answer -- a report here means the BINOMIAL overflows
        /// int64, not that the algorithm did.
        inline int64_t binom_checked(int64_t m, int r) {
            if (r < 0)  return ORB_OVERFLOW;   // negative rank
            if (m < 0)  return ORB_OVERFLOW;   // negative extent
            if (static_cast<int64_t>(r) > m) return 0;
            int64_t acc = 1;
            for (int i = 1; i <= r; ++i) {
                const int64_t f = m - static_cast<int64_t>(r) + static_cast<int64_t>(i);
                const int64_t g = gcd64(f, static_cast<int64_t>(i));
                // (i/g) divides acc because gcd(i/g, f/g) = 1 and acc*f/i is an integer -- so this division is exact, never a truncation.
                acc = mul_checked(acc / (static_cast<int64_t>(i) / g), f / g);
                if (acc < 0) return ORB_OVERFLOW;
            }
            return acc;
        }

        // Sec 4 cardinality fold
        //     M0 = n ;  Mi = C(M_{i-1} + ri - 1, ri)  if si = '+'
        //                    C(M_{i-1},          ri)  if si = '-'
        // Levels are folded INNERMOST-FIRST, so the recursion runs down the internal (outermost-first) list to its tail
        // and folds back out.
        template<class C> struct cells;
        template<> struct cells<orb_list<>> {
            static int64_t f(int64_t n) { return n < 0 ? ORB_OVERFLOW : n; }
        };
        template<class L, class... Rest>
        struct cells<orb_list<L, Rest...>> {
            static int64_t f(int64_t n) {
                const int64_t m = cells<orb_list<Rest...>>::f(n);
                if (m < 0) return ORB_OVERFLOW;
                if constexpr (L::pos) {
                    const int64_t top = add_checked(m, static_cast<int64_t>(L::rank) - 1);
                    if (top < 0) return ORB_OVERFLOW;
                    return binom_checked(top, L::rank);
                } else {
                    return binom_checked(m, L::rank);
                }
            }
        };

        // Sec 2 traversal nest -- segment-peeled, branch-free, in stream order
        //
        // A canonical tuple of a class with outermost level (R,s) over inner class D is a sequence of R D-canonical keys
        //     K_0 <= K_1 <= ... <= K_{R-1}      (s = '+')
        //     K_0 <  K_1 <  ... <  K_{R-1}      (s = '-')
        // ordered by D-lex. Because all keys have the same length, lex on the flat tuple IS lex on the key sequence, so
        // the whole traversal reduces to two mutually recursive primitives:
        //
        //   all<C>      : every C-canonical tuple, ascending lex.
        //   above<C,GE> : every C-canonical tuple  > prev  (GE=false) or >= prev (GE=true), ascending lex.
        //
        // `all` is a chain: K_0 from all<D>, then K_t from above<D, s> of K_{t-1}. `above` is where the peeling lives.
        // Split the target set by the index t of the FIRST key that differs from `prev`:
        //
        //   * no differing key: K == prev. This is the equality segment -- the "diagonal" -- and it exists only when GE,
        //     i.e. only under a '+' level. `if constexpr (GE)` erases it otherwise.
        //   * first difference at t: keys 0..t-1 are PINNED to prev (not loops, just a copy of the outer values), K_t
        //     ranges over above<D,false> of prev_t, and keys t+1..R-1 continue the chain with above<D, s>.
        //
        // ORDER. If K first differs at t and K' first differs at t' > t, then K'_t == prev_t < K_t, so K' < K: a LATER
        // first difference is a SMALLER tuple. Ascending order is therefore "equality, then t = R-1, R-2, ..., 0", which
        // is exactly the E / B / A segment order of the reference emitter `segmentedNestDepth2` in
        // proofs/OrbitEnum.fsx:314-334, and the depth-2 instantiation below reproduces it line for line.
        //
        // The recursion bottoms out at orb_list<> (one axis), where the two primitives are a bare `for` with bounds `0`,
        // `prev[0]`, `prev[0]+1`, and `n`. Every bound in the whole nest is a var, a var+1, or a constant -- the
        // vocabulary BoundDependencies/StrictOffset already has. No ternary, no data-dependent bound, no runtime sign test.
        //
        // The SEGMENT STRUCTURE (how many straight-line nests, and which coordinates each pins) is entirely compile-time:
        // the shape of the `seg<T>` / `chain<T>` template recursion over the level list; only bound VALUES are runtime.
        // Instantiated nest count is the product over levels of the per-level segment count, and grows multiplicatively:
        // measured visitor call sites (one class per TU, -O2) are 3 for `2+,2+`, 18 for `2+,2+,2+`, 16 for `3+,3+`, 263
        // for `3+,3+,3+`. Fine at the Sec 7.2 realistic ceiling (depth <= 3, r mostly 2), but deep all-3 classes get big.

        template<class C> struct nest;

        /// Base: the scalar class, one axis, no tie.
        template<> struct nest<orb_list<>> {
            static constexpr int AXES = 1;

            template<class F>
            static void all(int n, int* out, F&& sink) {
                for (int x = 0; x < n; ++x) { out[0] = x; sink(); }
            }

            /// Two straight-line loops selected at compile time, so the emitted bound is literally `prev[0]` or `prev[0] + 1` -- no ternary survives to runtime, and neither does the unselected loop.
            template<bool GE, class F>
            static void above(int n, const int* prev, int* out, F&& sink) {
                if constexpr (GE) {
                    for (int x = prev[0]; x < n; ++x) { out[0] = x; sink(); }
                } else {
                    for (int x = prev[0] + 1; x < n; ++x) { out[0] = x; sink(); }
                }
            }
        };

        template<class L, class... Rest>
        struct nest<orb_list<L, Rest...>> {
            using inner = nest<orb_list<Rest...>>;
            static constexpr int  R    = L::rank;      // sub-blocks at this level
            static constexpr bool POS  = L::pos;       // '+' -> nondecreasing chain
            static constexpr int  LD   = inner::AXES;  // axes per sub-block
            static constexpr int  AXES = R * LD;

            /// Continue the chain at key index T, given key T-1 already in `out`. T == R terminates the nest and emits.
            template<int T, class F>
            static void chain(int n, int* out, F&& sink) {
                if constexpr (T >= R) {
                    (void)n; (void)out;
                    sink();
                } else {
                    inner::template above<POS>(n, out + (T - 1) * LD, out + T * LD,
                                               [&]() { chain<T + 1>(n, out, sink); });
                }
            }

            template<class F>
            static void all(int n, int* out, F&& sink) {
                inner::all(n, out, [&]() { chain<1>(n, out, sink); });
            }

            /// Peeled segment T: keys 0..T-1 pinned (already copied by `above`), key T strictly above prev's, keys T+1.. free chain. Emitted in DESCENDING T so the union is ascending-lex.
            template<int T, class F>
            static void seg(int n, const int* prev, int* out, F&& sink) {
                inner::template above<false>(n, prev + T * LD, out + T * LD,
                                             [&]() { chain<T + 1>(n, out, sink); });
                if constexpr (T > 0) seg<T - 1>(n, prev, out, sink);
            }

            template<bool GE, class F>
            static void above(int n, const int* prev, int* out, F&& sink) {
                // Pin keys 0..R-2 once. Segments run T = R-1 down to 0 and segment T only ever writes keys >= T, so the copy made here stays valid for every segment that follows.
                for (int i = 0; i < (R - 1) * LD; ++i) out[i] = prev[i];

                // The equality ("diagonal") segment. Present only under a '+' level; `if constexpr` means a '-' level's instantiation contains no trace of it.
                if constexpr (GE) {
                    for (int i = (R - 1) * LD; i < R * LD; ++i) out[i] = prev[i];
                    sink();
                }

                seg<R - 1>(n, prev, out, sink);
            }
        };

        // Sec 5 canonicalization -- one sort per level, innermost first

        /// Lexicographic comparison of two equal-length coordinate blocks.
        inline int lexcmp(const int* a, const int* b, int len) {
            for (int i = 0; i < len; ++i)
                if (a[i] != b[i]) return a[i] < b[i] ? -1 : 1;
            return 0;
        }

        template<class C> struct canon;

        template<> struct canon<orb_list<>> {
            static constexpr int AXES = 1;
            static int f(const int* in, int* out) { out[0] = in[0]; return 1; }
        };

        template<class L, class... Rest>
        struct canon<orb_list<L, Rest...>> {
            using inner = canon<orb_list<Rest...>>;
            static constexpr int  R    = L::rank;
            static constexpr bool POS  = L::pos;
            static constexpr int  LD   = inner::AXES;
            static constexpr int  AXES = R * LD;

            /// Canonicalize `in` (AXES raw coordinates) into `out`, returning the accumulated character: +1, -1, or 0 for
            /// a zero-set tuple. `in` and `out` MAY alias: sub-blocks are canonicalized into a local buffer and `out` is
            /// written only at the end.
            static int f(const int* in, int* out) {
                int buf[AXES];
                int sign = 1;

                // Innermost first: canonicalize each sub-block, multiply the characters, and short-circuit the zero set.
                for (int b = 0; b < R; ++b) {
                    const int s = inner::f(in + b * LD, buf + b * LD);
                    if (s == 0) return 0;
                    sign *= s;
                }

                // Inversion count over the ORIGINAL block order -- the parity of the permutation that sorts them (canon_fold's recipe, nested_array_utilities.hpp:846-859, lifted from scalars to composite keys).
                int inv = 0;
                for (int a = 0; a < R; ++a) {
                    for (int b = a + 1; b < R; ++b) {
                        const int c = lexcmp(buf + a * LD, buf + b * LD, LD);
                        if constexpr (!POS) {
                            // Sec 5 zero set: a '-' level kills tuples with two equal sub-blocks at that level.
                            if (c == 0) return 0;
                        }
                        if (c > 0) ++inv;
                    }
                }
                if constexpr (!POS) {
                    if (inv & 1) sign = -sign;
                }

                // Sort the R blocks by lex key (insertion sort; R is small and compile-time known).
                int idx[R];
                for (int i = 0; i < R; ++i) idx[i] = i;
                for (int i = 1; i < R; ++i) {
                    const int key = idx[i];
                    int j = i - 1;
                    while (j >= 0 && lexcmp(buf + idx[j] * LD, buf + key * LD, LD) > 0) {
                        idx[j + 1] = idx[j];
                        --j;
                    }
                    idx[j + 1] = key;
                }
                for (int b = 0; b < R; ++b)
                    for (int q = 0; q < LD; ++q)
                        out[b * LD + q] = buf[idx[b] * LD + q];

                return sign;
            }
        };

        // Sec 3 random-access pair -- rank / unrank, strictify-then-combinadic
        //
        // A canonical tuple at a level is a sequence of R sub-keys, each a level-below rank in [0, M). The '+' case
        // reduces to strict via s_j = k_j + j (0-based per-position shift; the same strict<->weak correspondence that
        // canon_left_justify encodes as gaps) over the widened alphabet N = M + R - 1; the '-' case is already strict
        // over N = M. A strictly increasing R-sequence over [0,N) is then ranked by the standard LEX combinadic: counting
        // the sequences that share the first j coordinates and are smaller at position j and collapsing the inner sum by
        // hockey stick gives, with s_{-1} = -1,
        //
        //     rank = sum_{j=0}^{R-1}  C(N - s_{j-1} - 1, R-j) - C(N - s_j, R-j)
        //
        // -- 2R binomials, no O(N) loop. Every term is <= C(N,R) = M_level, so if the cell count fits in int64 the rank
        // arithmetic cannot overflow; it is still computed with the checked binomial, and a wrap-or-invalid input reports
        // ORB_OVERFLOW rather than returning a plausible offset.
        //
        // LEX, not colex. The colex rank sum C(s_j, j) is extent-independent and tempting, but it is the WRONG ORDER:
        // pool_base linear copies, genMpiNestSimplicial and the Zarr flat-range spec all assume ascending-lex DFS, and Sec 3
        // makes "rank order == the Sec 2 nest's visit order" an invariant with its own test. Lex ranking depends on the
        // alphabet size, which is why `orb_rank` needs `n`.

        template<class C> struct rnk;

        template<> struct rnk<orb_list<>> {
            static constexpr int AXES = 1;
            static int64_t rank(const int* t, int64_t n) {
                // Bounds check at the ONE place a raw coordinate enters rank arithmetic. Without it, coordinate == n (the
                // classic off-by-one) strictifies onto the alphabet-size symbol and ranks to a VALID NEIGHBORING cell --
                // a silent wrong offset, not an error; every composite level above only normalizes negatives.
                const int64_t v = static_cast<int64_t>(t[0]);
                if (v < 0 || v >= n) return ORB_OVERFLOW;
                return v;
            }
            static bool unrank(int64_t r, int64_t n, int* out) {
                if (r < 0 || r >= n) return false;
                out[0] = static_cast<int>(r);
                return true;
            }
        };

        template<class L, class... Rest>
        struct rnk<orb_list<L, Rest...>> {
            using inner = rnk<orb_list<Rest...>>;
            static constexpr int  R    = L::rank;
            static constexpr bool POS  = L::pos;
            static constexpr int  LD   = inner::AXES;
            static constexpr int  AXES = R * LD;

            /// Alphabet size for this level's strict sequence, or ORB_OVERFLOW.
            static int64_t alphabet(int64_t n) {
                const int64_t m = cells<orb_list<Rest...>>::f(n);
                if (m < 0) return ORB_OVERFLOW;
                if constexpr (POS) {
                    return add_checked(m, static_cast<int64_t>(R) - 1);
                } else {
                    return m;
                }
            }

            /// Position of a CANONICAL tuple in the orb_visit stream, or ORB_OVERFLOW on overflow or a non-canonical input.
            static int64_t rank(const int* t, int64_t n) {
                const int64_t N = alphabet(n);
                if (N < 0) return ORB_OVERFLOW;

                int64_t s[R];
                for (int b = 0; b < R; ++b) {
                    const int64_t k = inner::rank(t + b * LD, n);
                    if (k < 0) return ORB_OVERFLOW;
                    if constexpr (POS) {
                        s[b] = k + static_cast<int64_t>(b);   // strictify
                    } else {
                        s[b] = k;
                    }
                }

                int64_t acc = 0;
                int64_t prev = -1;
                for (int j = 0; j < R; ++j) {
                    const int64_t a = binom_checked(N - prev - 1, R - j);
                    const int64_t b = binom_checked(N - s[j],     R - j);
                    if (a < 0 || b < 0) return ORB_OVERFLOW;
                    acc = add_checked(acc, a - b);
                    if (acc < 0) return ORB_OVERFLOW;   // also catches non-canonical
                    prev = s[j];
                }
                return acc;
            }

            /// Greedy inverse of `rank`. Returns false if `r` is out of range or the arithmetic overflows. The
            /// per-position search is a binary search on the (monotone) partial-sum closed form rather than a linear
            /// scan, so cost is O(axes * log M) binomials -- this is the cold path, but no reason for it to be O(M).
            static bool unrank(int64_t r, int64_t n, int* out) {
                const int64_t N = alphabet(n);
                if (N < 0 || r < 0) return false;

                int64_t s[R];
                int64_t rem  = r;
                int64_t prev = -1;
                for (int j = 0; j < R; ++j) {
                    // pre(v) = C(N-prev-1, R-j) - C(N-v, R-j) is the number of completions skipped by choosing s_j >= v; it is 0 at v = prev+1 and nondecreasing. Take the largest v whose pre(v) still fits under `rem`.
                    const int64_t base = binom_checked(N - prev - 1, R - j);
                    if (base < 0) return false;
                    int64_t lo = prev + 1;
                    int64_t hi = N - 1;
                    if (lo > hi) return false;
                    while (lo < hi) {
                        const int64_t mid = lo + (hi - lo + 1) / 2;
                        const int64_t c = binom_checked(N - mid, R - j);
                        if (c < 0) return false;
                        if (base - c <= rem) lo = mid; else hi = mid - 1;
                    }
                    const int64_t c = binom_checked(N - lo, R - j);
                    if (c < 0) return false;
                    rem -= (base - c);
                    if (rem < 0) return false;
                    s[j] = lo;
                    prev = lo;
                }
                // Fixing all R positions determines the tuple, so a leftover remainder means `r` was past the end of this level's pool.
                if (rem != 0) return false;

                for (int b = 0; b < R; ++b) {
                    int64_t k;
                    if constexpr (POS) {
                        k = s[b] - static_cast<int64_t>(b);   // un-strictify
                    } else {
                        k = s[b];
                    }
                    if (!inner::unrank(k, n, out + b * LD)) return false;
                }
                return true;
            }
        };

    } // namespace detail

    // Public API. `Levels...` is the doc's list, OUTERMOST-LAST:
    //     OrbIdx<[(2,-),(2,+)],n>  ->  orb_level<2,false>, orb_level<2,true>

    /// Raw axis count of the class: prod_i r_i. `orb_axes<>` is 1.
    template<class... Levels>
    inline constexpr int orb_axes = detail::axes<detail::make_list<Levels...>>::value;

    /// Sec 4 cardinality fold, with exact overflow detection. Returns the number of stored cells, or ORB_OVERFLOW (-1) if
    /// the fold leaves int64 at any level, or if `n` is negative. No intermediate ever wraps, even transiently.
    /// EXTENT TYPE: all four public entry points take the extent as `int` (coordinates are `int` throughout; an extent
    /// past INT_MAX is unreachable elsewhere). Internally the fold runs in int64 regardless, since each level's OUTPUT
    /// is the next level's ground-set size and those blow past int32 immediately.
    template<class... Levels>
    inline int64_t orb_cell_count(int n) {
        return detail::cells<detail::make_list<Levels...>>::f(static_cast<int64_t>(n));
    }

    /// Sec 2 traversal: call `visitor(const int* coords, int64_t linear_index)` once per canonical tuple, in ascending-lex
    /// order, with `linear_index` counting 0, 1, 2, ... `coords` points at a buffer of `orb_axes` ints owned by this
    /// call and reused between visits -- copy it if you keep it.
    ///
    /// Returns false -- visiting NOTHING -- iff `n` is negative, the same input `orb_cell_count` reports as
    /// ORB_OVERFLOW; the two entry points give one verdict on malformed extents. n == 0 is a well-formed empty class:
    /// true, no visits.
    ///
    /// This is the hot path: the emitted nest carries no strides, computes no offsets and tests nothing at runtime.
    /// `linear_index` is a convenience for oracles and cold consumers; a pool walk should bump a pointer.
    template<class... Levels, class V>
    inline bool orb_visit(int n, V&& visitor) {
        if (n < 0) return false;
        using C = detail::make_list<Levels...>;
        constexpr int A = detail::axes<C>::value;
        int coords[A];
        int64_t idx = 0;
        detail::nest<C>::all(n, coords, [&]() {
            visitor(static_cast<const int*>(coords), idx++);
        });
        return true;
    }

    /// Sec 5 canonicalization. Writes the canonical representative of `tuple` (orb_axes raw coordinates) to `out` and
    /// returns the accumulated character: +1, -1, or 0 when the tuple is in the zero set (two equal sub-blocks at a '-'
    /// level). On a 0 return `out` is unspecified. `tuple` and `out` may alias.
    template<class... Levels>
    inline int orb_canon(const int* tuple, int* out) {
        return detail::canon<detail::make_list<Levels...>>::f(tuple, out);
    }

    /// Sec 3 rank: position of a CANONICAL tuple in the `orb_visit` stream. Returns ORB_OVERFLOW (-1) on overflow or a
    /// non-canonical input.
    ///
    /// SIGNATURE NOTE. A LEX rank is not extent-free: (1,2) is rank 3 among the strict pairs over [0,4) and rank 4 over
    /// [0,5). Only a colex rank would be extent-free, and Sec 3 fixes the order as ascending-lex, so `n` is required. It is
    /// passed rather than baked into the type because extents are runtime in Blade's C++ runtime everywhere else.
    template<class... Levels>
    inline int64_t orb_rank(const int* canonical, int n) {
        return detail::rnk<detail::make_list<Levels...>>::rank(canonical, static_cast<int64_t>(n));
    }

    /// Sec 3 unrank: inverse of `orb_rank`. Writes the canonical tuple with position `r` into `out` (orb_axes ints) and returns true; returns false if `r` is out of range or the arithmetic overflows.
    template<class... Levels>
    inline bool orb_unrank(int64_t r, int n, int* out) {
        return detail::rnk<detail::make_list<Levels...>>::unrank(r, static_cast<int64_t>(n), out);
    }

    // STORAGE LAYER -- the read/write path and the nested-pointer dual view.
    // docs/plan-orbidx-bijections.md Sec 1 ("two access paths, dual views"); docs/plan-orbidx-decompaction.md Sec 2 (read semantics)
    //
    // Sec 1 names TWO access paths over ONE pool, both built here on the five functions above -- nothing below re-derives an
    // offset formula:
    //
    //   RANDOM ACCESS (cold path: decompaction, provider block maps, partial reads). orb_read / orb_read_checked /
    //     orb_write_canonical realize decompaction Sec 2 literally,
    //         dense[t] = 0                                    if canon(t) = 0
    //                  = chi(t) * pool[orb_rank(canon(t), n)]  otherwise
    //     i.e. canonicalize, rank, apply the character. The composition IS the definition.
    //
    //   TRAVERSAL (hot path). orb_skeleton is the nested-pointer half of the dual view: the same pool served through
    //     pointer rows so a consumer walks it by pointer-chasing with no stride arithmetic, exactly as build_skeleton
    //     (nested_array_utilities.hpp:217-264) does for the rectangular / simplex index types. The contiguous half of the
    //     dual view is the pool itself -- `base()` is the pool_base analog (:54-87), and walking it linearly IS the
    //     orb_visit stream, because rank order == visit order (Sec 3's invariant).
    //
    // THE DOMAIN CONTRACT. A raw tuple can fail in two completely different ways, and conflating them is the bug this
    // section avoids:
    //   * ZERO SET -- orb_canon returns character 0 (two equal sub-blocks at a '-' level). This is a VALUE, not an
    //     error: the dense tensor genuinely holds 0 there, no cell is stored, every consumer wants T(0). IN the domain.
    //   * OUT OF DOMAIN -- a digit outside [0,n), a negative extent, or an arithmetic overflow. A CONTRACT VIOLATION.
    //     Answering it with T(0) would alias it onto the zero set and make an off-by-one indistinguishable from a
    //     structural zero -- exactly the failure `rnk<orb_list<>>::rank`'s bounds check exists to stop (coordinate == n
    //     used to strictify onto the alphabet-size symbol and rank to a VALID NEIGHBOURING cell: a silent wrong offset).
    //
    // So the domain check is not folded into the value. `orb_read_checked` is the total function -- bool success plus an
    // out parameter, `out` left untouched on failure -- for any tuple whose provenance is not already trusted.
    // `orb_read` is the convenience wrapper whose PRECONDITION is an in-domain tuple; violating it trips ORB_ASSERT and
    // returns T(0) WITHOUT EVER INDEXING THE POOL. The value is unspecified; the memory access is not. A read that
    // silently reports "structural zero" for a corrupt index is the same class of bug as the rank off-by-one -- it
    // survives every roundtrip test, because a roundtrip writes and reads through the same wrong door. The two-function
    // split makes the caller say which one it wants, and the assert makes the wrong one loud where loudness is affordable.
    //
    // The explicit digit check in orb_read_checked/orb_write_canonical is DELIBERATELY REDUNDANT with orb_rank's bounds
    // check: the rank check is the backstop that keeps the memory-safety claim true even if this one is ever weakened;
    // this one makes the zero-set/out-of-domain distinction exact (a tuple like (5,5) at n=3 under a '-' level
    // canonicalizes to the zero set BEFORE any rank arithmetic could object, so without a digit check it would be
    // reported as an in-domain zero).
    //
    // SIGN APPLICATION AND T. chi is in {-1, 0, +1}. T must be constructible from 0 (the zero set) and -- only when the
    // class has at least one '-' level -- must support unary minus. `orb_has_minus_level` makes that an `if constexpr`,
    // so a '+'-only class (SymIdx-like) instantiates cleanly over a T with no negation at all. Conjugation is NOT
    // handled: Hermitian stays depth-1-only, outside this +-1 character system.

    /// True iff the class admits a -1 character, i.e. has at least one '-' level. Empty pack folds to false -- OrbIdx<[],n> == Idx<n> is '+'-only.
    template<class... Levels>
    inline constexpr bool orb_has_minus_level = (... || (!Levels::pos));

    /// decompaction Sec 2 read, total. Writes chi(t) * pool[rank(canon(t))] -- or T(0) on the zero set, with no pool access
    /// -- into `out` and returns true. Returns false, leaving `out` UNTOUCHED, iff the tuple is out of domain: `n`
    /// negative, some digit outside [0,n), or rank overflow.
    ///
    /// `tuple` is ANY raw tuple: digits need not be canonical or even ordered. `pool` may be null when the caller only
    /// wants the domain verdict; it is dereferenced only on the true-and-nonzero path.
    template<class T, class... Levels>
    inline bool orb_read_checked(const T* pool, const int* tuple, int n, T& out) {
        constexpr int A = orb_axes<Levels...>;
        if (n < 0) return false;
        for (int k = 0; k < A; ++k)
            if (tuple[k] < 0 || tuple[k] >= n) return false;

        int can[A];
        const int chi = orb_canon<Levels...>(tuple, can);
        if (chi == 0) {                 // zero set: a VALUE, and no stored cell
            out = T(0);
            return true;
        }
        const int64_t r = orb_rank<Levels...>(can, n);
        // Reached only when the rank arithmetic leaves int64 (an extent far past anything allocatable); the off-by-one case the rank bounds check also guards is already refused by the digit check above.
        if (r == ORB_OVERFLOW) return false;

        if constexpr (orb_has_minus_level<Levels...>) {
            out = (chi < 0) ? static_cast<T>(-pool[r]) : pool[r];
        } else {
            // chi is +1 for every canonical tuple of a '+'-only class, so T is never required to have unary minus here.
            out = pool[r];
        }
        return true;
    }

    /// decompaction Sec 2 read, with a PRECONDITION: every digit of `tuple` is in [0,n) and `n` >= 0. The zero set is in
    /// the domain and yields T(0).
    ///
    /// Violating the precondition trips ORB_ASSERT and returns T(0) without indexing `pool`. That T(0) is UNSPECIFIED,
    /// not a promise: it aliases the zero set on purpose-free grounds (there is no other total answer), which is why a
    /// caller that cannot vouch for its tuple must use orb_read_checked instead of comparing against 0.
    template<class T, class... Levels>
    inline T orb_read(const T* pool, const int* tuple, int n) {
        T v = T(0);
        const bool ok = orb_read_checked<T, Levels...>(pool, tuple, n, v);
        ORB_ASSERT(ok, "orb_read: tuple out of domain -- use orb_read_checked");
        return ok ? v : T(0);
    }

    /// Store `v` at `tuple`, which must be EXACTLY the canonical representative of its orbit: an orb_canon fixed point
    /// with character +1, digits in [0,n). Returns false -- writing nothing -- otherwise, i.e. for an out-of-domain
    /// tuple, a zero-set tuple (chi == 0), a mirrored tuple (chi == -1), a non-canonical tuple that happens to sit at an
    /// EVEN permutation (chi == +1 but not a fixed point: e.g. the 3-cycle (1,2,0) under a single (3,-) level), or a
    /// rank overflow.
    ///
    /// The fixed-point test is therefore load-bearing and separate from the character test -- chi == +1 does NOT imply
    /// canonical. The converse direction is redundant on purpose: chi == -1 needs an odd sort permutation, so a mirrored
    /// tuple is never a fixed point either and the two tests overlap there. The character test's UNIQUE job is the zero
    /// set, which IS a fixed point (canon leaves `out` unspecified at chi == 0, hence the character-first test order).
    ///
    /// NO MIRRORED WRITES IN v1. Storing through a non-canonical tuple would mean solving chi * pool[r] = v for pool[r],
    /// i.e. DIVIDING by the character -- well defined for signed arithmetic types but not for the general T this layer
    /// is otherwise agnostic about (unsigned, modular, saturating, monoid accumulators), and it silently loses the "the
    /// caller knew which cell it was touching" property a compaction well-definedness check needs. Deferred, on purpose.
    template<class T, class... Levels>
    inline bool orb_write_canonical(T* pool, const int* tuple, int n, T v) {
        constexpr int A = orb_axes<Levels...>;
        if (n < 0) return false;
        for (int k = 0; k < A; ++k)
            if (tuple[k] < 0 || tuple[k] >= n) return false;

        int can[A];
        if (orb_canon<Levels...>(tuple, can) != 1) return false;  // 0 or -1
        for (int k = 0; k < A; ++k)
            if (can[k] != tuple[k]) return false;                 // not a fixed point

        const int64_t r = orb_rank<Levels...>(can, n);
        if (r == ORB_OVERFLOW) return false;
        pool[r] = v;
        return true;
    }

    // orb_skeleton<T, Levels...> -- the NESTED-POINTER dual view.
    //
    // The wreath generalization of build_skeleton (nested_array_utilities.hpp:217-264), which lays a pointer skeleton
    // over ONE contiguous pool so that `arr[i][j][k]` costs no offset arithmetic and the DFS traversal order IS the
    // allocation order. Same substrate, same promise: the caller owns the pool (already laid out in orb_visit order),
    // this adds ONE arena allocation of node records, and navigation walks the canonical coordinate space axis by axis
    // by chasing pointers.
    //
    // WHERE THE CHILD COUNTS COME FROM: not a second bound formula, but the Sec 2 nest itself. `build` RUNS orb_visit and
    // records, at each node, the span of coordinate values the nest establishes there -- the Sec 2 peeling bound as
    // realized: every base-case loop is `for (x = lo; x < n; ++x)`, and a peeled segment's pinned coordinate contributes
    // the single value the neighbouring free segment's lower bound sits one above, so the values a node serves are one
    // contiguous ascending run: a node is (first value, length, where the children start). There is no bound logic in
    // this class that could drift from the traversal, because there is no bound logic in this class at all. The
    // contiguous-run property is CHECKED, not assumed: a gap, a descent, or a non-contiguous leaf row fails the build.
    //
    // DIVERGENCE FROM build_skeleton: it allocates the FORMULA trip count `extents[d] - lastIndex` and so keeps trailing
    // zero-length rows; this skeleton materializes only what the nest visits, so an empty subtree is not built and
    // `navigate` answers nullptr there, same as a zero-length row would. The two agree -- count == n - lo -- at EVERY
    // leaf row of EVERY class and at every node of a '+'-only class (every value extends). They differ only where a
    // deeper level's emptiness truncates the tail: `2-` at n = 4 serves i in [0,3) at the root, not [0,4), because i = 3
    // has no partner.
    //
    // ARENA LAYOUT. ONE `new node[N]`, carved into AXES regions by depth: region k holds every depth-k node in
    // ascending-lex order of its prefix. Children of a depth-k node are therefore a CONTIGUOUS run inside region k+1
    // (consecutive prefixes have consecutive children), which is why a node stores one child pointer instead of a
    // pointer array, and why the whole skeleton is one allocation and one delete[]. Depth-(AXES-1) nodes are the leaf
    // rows: `row` points into the CALLER's pool at the rank of their first cell, and their `count` cells are contiguous
    // from there -- the same "leaf rows are slices of the pool, not heap blocks of their own" invariant build_skeleton's
    // teardown contract rests on.
    //
    // N = sum over k of (number of distinct canonical prefixes of length k), reported as node_count(); arena_bytes() is
    // N * sizeof(node).
    //
    // LIFETIME. Copy is deleted (a copy would double-free the arena); the destructor frees, and `free()` is public for
    // explicit release. The pool is NOT owned: freeing the skeleton leaves it untouched, and destroying the pool first
    // dangles every leaf row -- the caller sequences the two, exactly as with allocate<>/deallocate.

    template<class T, class... Levels>
    class orb_skeleton {
    public:
        /// Raw axes of the class == depth of the node tree.
        static constexpr int AXES = orb_axes<Levels...>;

        /// One skeleton node. Depth < AXES-1: `kids` is a run of `count` child nodes, `row` is null. Depth == AXES-1 (a
        /// leaf row): `row` points into the pool at this row's first cell, `kids` is null. `lo` is the first coordinate
        /// value this node serves, so child index == coordinate - lo (build_skeleton recomputes bounds by formula at
        /// every use site; this caches the nest's own bounds so `navigate` needs no bound logic whatsoever).
        struct node {
            int   lo;
            int   count;
            node* kids;
            T*    row;
        };

        orb_skeleton() = default;
        ~orb_skeleton() { free(); }
        /// Copy is deleted -- two owners would double-free the arena. Move transfers it, so a skeleton can be returned or stored by value.
        orb_skeleton(const orb_skeleton&)            = delete;
        orb_skeleton& operator=(const orb_skeleton&) = delete;
        orb_skeleton(orb_skeleton&& o) noexcept
            : arena_(o.arena_), root_(o.root_), pool_(o.pool_),
              nodes_(o.nodes_), cells_(o.cells_), n_(o.n_) {
            o.arena_ = nullptr; o.root_ = nullptr; o.pool_ = nullptr;
            o.nodes_ = 0; o.cells_ = 0; o.n_ = 0;
        }
        orb_skeleton& operator=(orb_skeleton&& o) noexcept {
            if (this != &o) {
                free();
                arena_ = o.arena_; root_ = o.root_; pool_ = o.pool_;
                nodes_ = o.nodes_; cells_ = o.cells_; n_ = o.n_;
                o.arena_ = nullptr; o.root_ = nullptr; o.pool_ = nullptr;
                o.nodes_ = 0; o.cells_ = 0; o.n_ = 0;
            }
            return *this;
        }

        /// Build over an EXISTING pool of orb_cell_count<Levels...>(n) cells, already in (or about to be filled in)
        /// orb_visit order. Releases any previous build first, so rebuilding is safe.
        ///
        /// Returns false -- owning nothing -- iff `n` is negative or the contiguity invariants above are violated. A
        /// class with zero cells (n == 0, or a strict level wider than the extent) builds successfully into an EMPTY
        /// skeleton: no arena, null root, navigate always null. Throws whatever `new` throws; nothing is leaked if it does.
        bool build(int n, T* pool) {
            free();
            if (n < 0) return false;

            int     prev[AXES];
            int64_t per[AXES];
            for (int k = 0; k < AXES; ++k) { per[k] = 0; prev[k] = 0; }
            bool    firstCell = true;
            bool    dup       = false;
            int64_t cells     = 0;

            // Pass 1 -- how many nodes per depth. A depth-k node IS a distinct canonical prefix of length k; the stream
            // is ascending lex, so prefixes change exactly at the first differing axis and every depth strictly below
            // it starts a new node.
            const bool okv = orb_visit<Levels...>(n, [&](const int* t, int64_t) {
                int d = -1;
                if (!firstCell) {
                    d = 0;
                    while (d < AXES && t[d] == prev[d]) ++d;
                    if (d >= AXES) dup = true;      // stream repeated a tuple
                }
                for (int k = d + 1; k < AXES; ++k) ++per[k];
                for (int k = 0; k < AXES; ++k) prev[k] = t[k];
                firstCell = false;
                ++cells;
            });
            if (!okv || dup) return false;

            if (cells == 0) {                       // well-formed empty skeleton
                pool_  = pool;
                n_     = n;
                return true;
            }

            int64_t total = 0;
            for (int k = 0; k < AXES; ++k) total += per[k];
            // The ONE allocation. Nothing is committed to `this` until it succeeds, so a throwing `new` leaves the skeleton empty rather than half-built.
            node* const fresh = new node[static_cast<size_t>(total)];
            arena_ = fresh;
            nodes_ = total;
            pool_  = pool;
            n_     = n;
            cells_ = cells;

            node*   region[AXES];
            int64_t bump[AXES];
            {
                int64_t off = 0;
                for (int k = 0; k < AXES; ++k) {
                    region[k] = arena_ + off;
                    off      += per[k];
                    bump[k]   = 0;
                }
            }

            node* cur[AXES];
            for (int k = 0; k < AXES; ++k) cur[k] = nullptr;
            firstCell = true;
            bool ok   = true;

            // Pass 2 -- place the nodes. Same walk, so placement order is the stream order by construction.
            const bool okv2 = orb_visit<Levels...>(n, [&](const int* t, int64_t r) {
                if (!ok) return;
                int d = -1;
                if (!firstCell) {
                    d = 0;
                    while (d < AXES && t[d] == prev[d]) ++d;
                }
                for (int k = d + 1; k < AXES; ++k) {
                    node* nd = region[k] + bump[k]++;
                    nd->lo    = t[k];
                    nd->count = 0;
                    nd->kids  = nullptr;
                    nd->row   = nullptr;
                    if (k > 0) {
                        node* p = cur[k - 1];
                        if (p->count == 0) {
                            if (t[k - 1] != p->lo) { ok = false; return; }
                            p->kids = nd;                       // first child
                        } else if (t[k - 1] != p->lo + p->count) {
                            ok = false; return;                 // gap or descent
                        }
                        ++p->count;
                    }
                    cur[k] = nd;
                }
                node* lf = cur[AXES - 1];
                if (lf->count == 0) {
                    if (t[AXES - 1] != lf->lo) { ok = false; return; }
                    lf->row = pool + r;                         // slice of the pool
                } else if (t[AXES - 1] != lf->lo + lf->count) {
                    ok = false; return;                         // gap or descent
                } else if (lf->row + lf->count != pool + r) {
                    ok = false; return;                         // row not contiguous
                }
                ++lf->count;
                for (int k = 0; k < AXES; ++k) prev[k] = t[k];
                firstCell = false;
            });

            if (!okv2 || !ok) { free(); return false; }
            root_ = region[0];                                  // per[0] is always 1
            return true;
        }

        /// Release the arena. The pool is not touched (not owned). Safe on an unbuilt or already-freed skeleton, and safe to call before rebuild.
        void free() {
            delete[] arena_;
            arena_ = nullptr;
            root_  = nullptr;
            pool_  = nullptr;
            nodes_ = 0;
            cells_ = 0;
            n_     = 0;
        }

        /// Pointer to the cell of a CANONICAL tuple -- pure pointer-chasing, one subtract and one bounds test per axis,
        /// no stride and no rank arithmetic. Equals base() + orb_rank<Levels...>(tuple, n) whenever the tuple has a
        /// stored cell.
        ///
        /// Returns nullptr for any tuple with NO stored cell under this skeleton: out of range at some axis, in the
        /// zero set, or off the canonical set entirely (a non-canonical tuple leaves the visited span at the first axis
        /// where it stops being canonical, so it cannot land on some other orbit's cell). It does NOT canonicalize -- a
        /// mirrored tuple is not redirected to its representative; that is orb_read's job, keeping this zero-arithmetic.
        T* navigate(const int* canonicalTuple) const {
            const node* nd = root_;
            if (!nd) return nullptr;
            for (int k = 0; k < AXES - 1; ++k) {
                const int c = canonicalTuple[k] - nd->lo;
                if (c < 0 || c >= nd->count) return nullptr;
                nd = nd->kids + c;
            }
            const int c = canonicalTuple[AXES - 1] - nd->lo;
            if (c < 0 || c >= nd->count) return nullptr;
            return nd->row + c;
        }

        /// The contiguous half of the dual view: the pool base, exactly what pool_base (nested_array_utilities.hpp:54-87)
        /// recovers from a build_skeleton skeleton. Here it needs no recovery -- the caller supplied it -- and
        /// `base()[0 .. cells())` is the orb_visit stream in order, ready for a linear walk or one cudaMemcpy.
        T* base() const { return pool_; }

        /// Root node (depth 0), or null for an empty / unbuilt skeleton.
        const node* root() const { return root_; }

        /// Nodes in the arena, and its size in bytes.
        int64_t node_count()  const { return nodes_; }
        size_t  arena_bytes() const { return static_cast<size_t>(nodes_) * sizeof(node); }

        /// Cells reachable through the skeleton == orb_cell_count(n).
        int64_t cells()  const { return cells_; }
        int     extent() const { return n_; }

    private:
        node*   arena_ = nullptr;
        node*   root_  = nullptr;
        T*      pool_  = nullptr;
        int64_t nodes_ = 0;
        int64_t cells_ = 0;
        int     n_     = 0;
    };

} // namespace orbit_wreath_utilities
