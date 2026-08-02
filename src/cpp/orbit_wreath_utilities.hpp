#pragma once
// =============================================================================
//  orbit_wreath_utilities.hpp
//  Blade DSL Runtime Support Library -- OrbIdx wreath-class storage machinery
// =============================================================================
//
//  PROOF ARTIFACT.  This header is the C++ half of Phase 1 of
//  docs/plan-orbidx-bijections.md (`§4 Implementation sketch, phased`).  It is
//  not yet wired into codegen: nothing in src/ includes it.  Its job is to make
//  the four `OrbIdx` bijections *exist and be checkable* --
//  proofs/OrbWreathTest.cpp is the checker, and it validates every function
//  here against brute-force ground truth built in the same translation unit.
//
//  THE CLASS (docs/plan-orbit-index-types.md §2).  An OrbIdx class is a flat
//  list of levels over one extent `n`:
//
//      OrbIdx<[(r1,s1), (r2,s2), ..., (rd,sd)], n>
//
//  The list is OUTERMOST-LAST: level 1 is the innermost tie, level d the
//  outermost.  Level i groups `ri` sub-blocks of the level below and orders
//  them by their canonical keys in lexicographic order -- nondecreasing when
//  si = '+', strictly increasing when si = '-'.  The class's group is the
//  iterated wreath product  S_r1 wr S_r2 wr ... wr S_rd  acting on
//  `prod_i ri` raw axes; the sign list picks one of the 2^d characters that
//  group admits (§6 of the same doc).
//
//  In this header a level is a TYPE, `orb_level<R, Pos>`, and a class is a
//  parameter pack of them in the doc's order (outermost last):
//
//      OrbIdx<[(2,-),(2,+)],n>   (the Riemann shape)
//          ==  orb_level<2,false>, orb_level<2,true>
//
//  Everything that consults a sign or a level's structure does so through
//  `if constexpr` on a template parameter.  There is NO runtime branch on a
//  sign anywhere below, and in particular the equality ("diagonal") segment of
//  the traversal nest is `if constexpr (GE)` where GE is the enclosing level's
//  sign -- so a '-' level's instantiation contains no trace of it, not even a
//  skipped test (docs/plan-orbidx-bijections.md §2, "Codegen shape").
//
//  WHAT IS HERE
//    orb_cell_count<Levels...>(n)          §4 of plan-orbit-index-types.md
//    orb_visit<Levels...>(n, visitor)      §2 of plan-orbidx-bijections.md
//    orb_canon<Levels...>(tuple, out)      §5 of plan-orbit-index-types.md
//    orb_rank<Levels...>(canonical, n)     §3 of plan-orbidx-bijections.md
//    orb_unrank<Levels...>(r, n, out)      §3, the greedy inverse
//
//  HOUSE CONVENTIONS FOLLOWED
//    * Storage order is ascending-lex DFS, exactly as `build_skeleton`
//      (nested_array_utilities.hpp:217-264) lays out the pool: the traversal
//      order IS the allocation order.  "rank agrees with the §2 nest's visit
//      order" is a stated invariant with its own test, not an implementation
//      detail (plan-orbidx-bijections.md §3) -- a read->write roundtrip cannot
//      catch an order mismatch, so the test compares against an independent
//      brute-force oracle instead.
//    * Canonicalization mirrors `canon_fold` (nested_array_utilities.hpp:846):
//      count inversions with an O(R^2) double loop, then sort; a repeated key
//      at a strict level means the value is not stored -> implicit zero.
//    * The '+' -> strict reduction `s_j = k_j + (j-1)` in orb_rank realizes
//      the same strict<->weak correspondence `canon_left_justify` uses
//      (nested_array_utilities.hpp:861-868), in a DIFFERENT encoding: that
//      helper stores successive differences, this one applies a fixed
//      per-position shift.  Same bijection, not the same map.
//    * All cell/offset arithmetic is CHECKED.  §7.2 of
//      plan-orbit-index-types.md names silent int64 wraparound -- not stack
//      exhaustion -- as the failure mode to guard, because each level's output
//      is the next level's extent.  `binom_checked` below is a transcription of
//      `binomChecked` in proofs/OrbitEnum.fsx: the gcd reduction makes every
//      intermediate equal to C(m-r+i, i) <= C(m,r), so the multiply-then-divide
//      loop cannot wrap even TRANSIENTLY.  An overflow report here means the
//      true value exceeds int64, never that an intermediate did.
//
//  C++20, header-only, no dependency beyond <cstdint>/<cstddef>.
// =============================================================================

#include <cstddef>
#include <cstdint>

namespace orbit_wreath_utilities {

    // =========================================================================
    // Level and class description (compile time)
    // =========================================================================

    /// One wreath level: `R` sub-blocks tied with character `Pos`
    /// (true = '+', invariant/nondecreasing; false = '-', sgn/strictly
    /// increasing).  A level with R == 1 is the trivial group and a no-op at
    /// either sign; plan-orbit-index-types.md §7.2 normalizes those away before
    /// they reach here, but they are harmless if they do.
    template<int R, bool Pos>
    struct orb_level {
        static_assert(R >= 1, "orb_level: rank must be >= 1");
        static constexpr int  rank = R;
        static constexpr bool pos  = Pos;
    };

    /// Internal class representation: a list of levels, OUTERMOST-FIRST.
    /// (The public API takes them outermost-last, per the doc's spelling; the
    /// two orders are bridged by `detail::make_list`.)  `orb_list<>` is the
    /// scalar class `OrbIdx<[],n>` == `Idx<n>`: one axis, no tie.
    template<class... Ls>
    struct orb_list {};

    /// Sentinel returned by the checked-arithmetic paths.  Every quantity they
    /// compute is non-negative, so -1 is unambiguous.
    inline constexpr int64_t ORB_OVERFLOW = -1;

    namespace detail {

        // ---------------------------------------------------------------
        // Pack reversal: doc order (outermost LAST) -> internal (FIRST)
        // ---------------------------------------------------------------
        template<class Acc, class... Ls> struct rev;
        template<class... A>
        struct rev<orb_list<A...>> { using type = orb_list<A...>; };
        template<class... A, class L0, class... Rest>
        struct rev<orb_list<A...>, L0, Rest...> {
            using type = typename rev<orb_list<L0, A...>, Rest...>::type;
        };

        template<class... Ls>
        using make_list = typename rev<orb_list<>, Ls...>::type;

        // ---------------------------------------------------------------
        // Raw axis count: prod_i r_i (rank of the tensor the class describes)
        // ---------------------------------------------------------------
        template<class C> struct axes;
        template<> struct axes<orb_list<>> { static constexpr int value = 1; };
        template<class L, class... Rest>
        struct axes<orb_list<L, Rest...>> {
            static constexpr int value = L::rank * axes<orb_list<Rest...>>::value;
        };

        // ---------------------------------------------------------------
        // Checked non-negative int64 arithmetic
        // (proofs/OrbitEnum.fsx: addChecked / mulChecked / binomChecked)
        // ---------------------------------------------------------------

        inline int64_t gcd64(int64_t a, int64_t b) {
            while (b != 0) { int64_t t = a % b; a = b; b = t; }
            return a;
        }

        /// a + b, or ORB_OVERFLOW.  Both operands are expected non-negative;
        /// a negative result is reported as overflow so that a corrupted input
        /// cannot silently produce a plausible offset.
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

        /// Exact C(m, r) in int64, or ORB_OVERFLOW.
        ///
        /// The gcd reduction is the load-bearing part: at step i the
        /// accumulator equals C(m-r+i, i) <= C(m,r), so no intermediate ever
        /// exceeds the answer.  A report here therefore means the BINOMIAL
        /// overflows int64, not that the algorithm did.  (`binomI64`,
        /// IR.fs:2438-2447, is the unchecked version this replaces; its
        /// mid-loop division is exact but nothing checks the multiply.)
        inline int64_t binom_checked(int64_t m, int r) {
            if (r < 0)  return ORB_OVERFLOW;   // negative rank
            if (m < 0)  return ORB_OVERFLOW;   // negative extent
            if (static_cast<int64_t>(r) > m) return 0;
            int64_t acc = 1;
            for (int i = 1; i <= r; ++i) {
                const int64_t f = m - static_cast<int64_t>(r) + static_cast<int64_t>(i);
                const int64_t g = gcd64(f, static_cast<int64_t>(i));
                // (i/g) divides acc because gcd(i/g, f/g) = 1 and acc*f/i is
                // an integer -- so this division is exact, never a truncation.
                acc = mul_checked(acc / (static_cast<int64_t>(i) / g), f / g);
                if (acc < 0) return ORB_OVERFLOW;
            }
            return acc;
        }

        // ---------------------------------------------------------------
        // §4 cardinality fold
        //     M0 = n ;  Mi = C(M_{i-1} + ri - 1, ri)  if si = '+'
        //                    C(M_{i-1},          ri)  if si = '-'
        // Levels are folded INNERMOST-FIRST, so the recursion runs down the
        // internal (outermost-first) list to its tail and folds back out.
        // ---------------------------------------------------------------
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

        // ---------------------------------------------------------------
        // §2 traversal nest -- segment-peeled, branch-free, in stream order
        // ---------------------------------------------------------------
        //
        // A canonical tuple of a class with outermost level (R,s) over inner
        // class D is a sequence of R D-canonical keys
        //     K_0 <= K_1 <= ... <= K_{R-1}      (s = '+')
        //     K_0 <  K_1 <  ... <  K_{R-1}      (s = '-')
        // ordered by D-lex.  Because all keys have the same length, lex on the
        // flat tuple IS lex on the key sequence, so the whole traversal reduces
        // to two mutually recursive primitives:
        //
        //   all<C>      : every C-canonical tuple, ascending lex.
        //   above<C,GE> : every C-canonical tuple  > prev  (GE=false)
        //                 or >= prev (GE=true), ascending lex.
        //
        // `all` is a chain: K_0 from all<D>, then K_t from above<D, s> of
        // K_{t-1}.  `above` is where the peeling lives.  Split the target set
        // by the index t of the FIRST key that differs from `prev`:
        //
        //   * no differing key: K == prev.  This is the equality segment --
        //     the "diagonal" -- and it exists only when GE, i.e. only under a
        //     '+' level.  `if constexpr (GE)` erases it otherwise.
        //   * first difference at t: keys 0..t-1 are PINNED to prev (not loops
        //     at all, just a copy of the outer values), K_t ranges over
        //     above<D,false> of prev_t, and keys t+1..R-1 continue the chain
        //     with above<D, s>.
        //
        // ORDER.  If K first differs at t and K' first differs at t' > t, then
        // K'_t == prev_t < K_t, so K' < K: a LATER first difference is a
        // SMALLER tuple.  Ascending order is therefore
        //     equality, then t = R-1, R-2, ..., 0
        // which is exactly the E / B / A segment order of the reference
        // emitter `segmentedNestDepth2` in proofs/OrbitEnum.fsx:314-334, and
        // the depth-2 instantiation below reproduces it line for line.
        //
        // The recursion bottoms out at orb_list<> (one axis), where the two
        // primitives are a bare `for` with bounds `0`, `prev[0]`, `prev[0]+1`,
        // and `n`.  Every bound in the whole nest is a var, a var+1, or a
        // constant -- the vocabulary BoundDependencies/StrictOffset already
        // has.  No ternary, no data-dependent bound, no runtime test on a sign.
        //
        // The SEGMENT STRUCTURE (how many straight-line nests, and which
        // coordinates each pins) is entirely compile-time: it is the shape of
        // the `seg<T>` / `chain<T>` template recursion over the level list.
        // Only the bound VALUES are runtime.  Instantiated nest count is the
        // product over levels of the per-level segment count, and it grows
        // multiplicatively: measured visitor call sites (one class per TU,
        // -O2) are 3 for `2+,2+`, 18 for `2+,2+,2+`, 16 for `3+,3+`, 263 for
        // `3+,3+,3+`.  Fine at the §7.2 realistic ceiling (depth <= 3, r
        // mostly 2), but not "tens" in general -- deep all-3 classes get big.

        template<class C> struct nest;

        /// Base: the scalar class, one axis, no tie.
        template<> struct nest<orb_list<>> {
            static constexpr int AXES = 1;

            template<class F>
            static void all(int n, int* out, F&& sink) {
                for (int x = 0; x < n; ++x) { out[0] = x; sink(); }
            }

            /// Two straight-line loops selected at compile time, so the emitted
            /// bound is literally `prev[0]` or `prev[0] + 1` -- no ternary
            /// survives to runtime, and neither does the unselected loop.
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

            /// Continue the chain at key index T, given key T-1 already in
            /// `out`.  T == R terminates the nest and emits.
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

            /// Peeled segment T: keys 0..T-1 pinned (already copied by
            /// `above`), key T strictly above prev's, keys T+1.. free chain.
            /// Emitted in DESCENDING T so the union is ascending-lex.
            template<int T, class F>
            static void seg(int n, const int* prev, int* out, F&& sink) {
                inner::template above<false>(n, prev + T * LD, out + T * LD,
                                             [&]() { chain<T + 1>(n, out, sink); });
                if constexpr (T > 0) seg<T - 1>(n, prev, out, sink);
            }

            template<bool GE, class F>
            static void above(int n, const int* prev, int* out, F&& sink) {
                // Pin keys 0..R-2 once.  Segments run T = R-1 down to 0 and
                // segment T only ever writes keys >= T, so the copy made here
                // stays valid for every segment that follows.
                for (int i = 0; i < (R - 1) * LD; ++i) out[i] = prev[i];

                // The equality ("diagonal") segment.  Present only under a '+'
                // level; `if constexpr` means a '-' level's instantiation
                // contains no trace of it.
                if constexpr (GE) {
                    for (int i = (R - 1) * LD; i < R * LD; ++i) out[i] = prev[i];
                    sink();
                }

                seg<R - 1>(n, prev, out, sink);
            }
        };

        // ---------------------------------------------------------------
        // §5 canonicalization -- one sort per level, innermost first
        // ---------------------------------------------------------------

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

            /// Canonicalize `in` (AXES raw coordinates) into `out`, returning
            /// the accumulated character: +1, -1, or 0 for a zero-set tuple.
            /// `in` and `out` MAY alias: sub-blocks are canonicalized into a
            /// local buffer and `out` is written only at the end.
            static int f(const int* in, int* out) {
                int buf[AXES];
                int sign = 1;

                // Innermost first: canonicalize each sub-block, multiply the
                // characters, and short-circuit the zero set.
                for (int b = 0; b < R; ++b) {
                    const int s = inner::f(in + b * LD, buf + b * LD);
                    if (s == 0) return 0;
                    sign *= s;
                }

                // Inversion count over the ORIGINAL block order -- the parity
                // of the permutation that sorts them (canon_fold's recipe,
                // nested_array_utilities.hpp:846-859, lifted from scalars to
                // composite keys).
                int inv = 0;
                for (int a = 0; a < R; ++a) {
                    for (int b = a + 1; b < R; ++b) {
                        const int c = lexcmp(buf + a * LD, buf + b * LD, LD);
                        if constexpr (!POS) {
                            // §5 zero set: a '-' level kills tuples with two
                            // equal sub-blocks at that level.
                            if (c == 0) return 0;
                        }
                        if (c > 0) ++inv;
                    }
                }
                if constexpr (!POS) {
                    if (inv & 1) sign = -sign;
                }

                // Sort the R blocks by lex key (insertion sort; R is small and
                // compile-time known).
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

        // ---------------------------------------------------------------
        // §3 random-access pair -- rank / unrank, strictify-then-combinadic
        // ---------------------------------------------------------------
        //
        // A canonical tuple at a level is a sequence of R sub-keys, each a
        // level-below rank in [0, M).  The '+' case reduces to strict via
        //     s_j = k_j + j            (0-based per-position shift; the same
        //                               strict<->weak correspondence that
        //                               canon_left_justify encodes as gaps)
        // over the widened alphabet N = M + R - 1; the '-' case is already
        // strict over N = M.  A strictly increasing R-sequence over [0,N) is
        // then ranked by the standard LEX combinadic.  Counting the sequences
        // that share the first j coordinates and are smaller at position j and
        // collapsing the inner sum by hockey stick gives, with s_{-1} = -1,
        //
        //     rank = sum_{j=0}^{R-1}  C(N - s_{j-1} - 1, R-j) - C(N - s_j, R-j)
        //
        // -- 2R binomials, no O(N) loop.  Every term is <= C(N,R) = M_level, so
        // if the cell count fits in int64 the rank arithmetic cannot overflow;
        // it is still computed with the checked binomial, and a wrap-or-invalid
        // input reports ORB_OVERFLOW rather than returning a plausible offset.
        //
        // LEX, not colex.  The colex rank sum C(s_j, j) is extent-independent
        // and tempting, but it is the WRONG ORDER: pool_base linear copies,
        // genMpiNestSimplicial and the Zarr flat-range spec all assume
        // ascending-lex DFS, and §3 makes "rank order == the §2 nest's visit
        // order" an invariant with its own test.  Lex ranking depends on the
        // alphabet size, which is why `orb_rank` needs `n` (see the note on the
        // public signature below).

        template<class C> struct rnk;

        template<> struct rnk<orb_list<>> {
            static constexpr int AXES = 1;
            static int64_t rank(const int* t, int64_t n) {
                // Bounds check at the ONE place a raw coordinate enters rank
                // arithmetic. Without it, coordinate == n (the classic
                // off-by-one) strictifies onto the alphabet-size symbol and
                // ranks to a VALID NEIGHBORING cell -- a silent wrong offset,
                // not an error; every composite level above only normalizes
                // negatives. (Adversarial-review finding, 2026-08-01; the F#
                // sibling validates here too.)
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

            /// Position of a CANONICAL tuple in the orb_visit stream, or
            /// ORB_OVERFLOW on overflow or a non-canonical input.
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

            /// Greedy inverse of `rank`.  Returns false if `r` is out of range
            /// or the arithmetic overflows.  The per-position search is a
            /// binary search on the (monotone) partial-sum closed form rather
            /// than a linear scan, so cost is O(axes * log M) binomials -- this
            /// is the cold path (§1: decompaction, provider block maps, partial
            /// reads), but there is no reason for it to be O(M).
            static bool unrank(int64_t r, int64_t n, int* out) {
                const int64_t N = alphabet(n);
                if (N < 0 || r < 0) return false;

                int64_t s[R];
                int64_t rem  = r;
                int64_t prev = -1;
                for (int j = 0; j < R; ++j) {
                    // pre(v) = C(N-prev-1, R-j) - C(N-v, R-j) is the number of
                    // completions skipped by choosing s_j >= v; it is 0 at
                    // v = prev+1 and nondecreasing.  Take the largest v whose
                    // pre(v) still fits under `rem`.
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
                // Fixing all R positions determines the tuple, so a leftover
                // remainder means `r` was past the end of this level's pool.
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

    // =========================================================================
    // Public API.  `Levels...` is the doc's list, OUTERMOST-LAST:
    //     OrbIdx<[(2,-),(2,+)],n>  ->  orb_level<2,false>, orb_level<2,true>
    // =========================================================================

    /// Raw axis count of the class: prod_i r_i.  `orb_axes<>` is 1.
    template<class... Levels>
    inline constexpr int orb_axes = detail::axes<detail::make_list<Levels...>>::value;

    /// §4 cardinality fold, with exact overflow detection.
    /// Returns the number of stored cells, or ORB_OVERFLOW (-1) if the fold
    /// leaves int64 at any level, or if `n` is negative.  No intermediate ever
    /// wraps, even transiently (see `binom_checked`).
    ///
    /// EXTENT TYPE.  All four public entry points take the extent as `int`,
    /// because coordinates are `int` throughout (an extent past INT_MAX is
    /// unreachable by every other function here).  Internally the fold runs in
    /// int64 regardless: each level's OUTPUT is the next level's ground-set
    /// size, and those blow past int32 immediately.
    template<class... Levels>
    inline int64_t orb_cell_count(int n) {
        return detail::cells<detail::make_list<Levels...>>::f(static_cast<int64_t>(n));
    }

    /// §2 traversal: call `visitor(const int* coords, int64_t linear_index)`
    /// once per canonical tuple, in ascending-lex order, with `linear_index`
    /// counting 0, 1, 2, ...  `coords` points at a buffer of `orb_axes` ints
    /// owned by this call and reused between visits -- copy it if you keep it.
    ///
    /// Returns false -- visiting NOTHING -- iff `n` is negative, the same
    /// input `orb_cell_count` reports as ORB_OVERFLOW; the two entry points
    /// give one verdict on malformed extents rather than one diagnosing and
    /// the other silently emitting an empty stream (adversarial-review
    /// finding, 2026-08-01).  n == 0 is a well-formed empty class: true, no
    /// visits.
    ///
    /// This is the hot path: the emitted nest carries no strides, computes no
    /// offsets and tests nothing at runtime.  `linear_index` is a convenience
    /// for oracles and cold consumers; a pool walk should bump a pointer.
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

    /// §5 canonicalization.  Writes the canonical representative of `tuple`
    /// (orb_axes raw coordinates) to `out` and returns the accumulated
    /// character: +1, -1, or 0 when the tuple is in the zero set (two equal
    /// sub-blocks at a '-' level).  On a 0 return `out` is unspecified.
    /// `tuple` and `out` may alias.
    template<class... Levels>
    inline int orb_canon(const int* tuple, int* out) {
        return detail::canon<detail::make_list<Levels...>>::f(tuple, out);
    }

    /// §3 rank: position of a CANONICAL tuple in the `orb_visit` stream.
    /// Returns ORB_OVERFLOW (-1) on overflow or a non-canonical input.
    ///
    /// SIGNATURE NOTE.  The plan writes this as `orb_rank(canonical)`, but a
    /// LEX rank is not extent-free: (1,2) is rank 3 among the strict pairs over
    /// [0,4) and rank 4 over [0,5).  Only a colex rank would be extent-free,
    /// and §3 fixes the order as ascending-lex, so `n` is required.  It is
    /// passed rather than baked into the type because extents are runtime in
    /// Blade's C++ runtime everywhere else (see `extents[]` in
    /// nested_array_utilities.hpp).
    template<class... Levels>
    inline int64_t orb_rank(const int* canonical, int n) {
        return detail::rnk<detail::make_list<Levels...>>::rank(canonical, static_cast<int64_t>(n));
    }

    /// §3 unrank: inverse of `orb_rank`.  Writes the canonical tuple with
    /// position `r` into `out` (orb_axes ints) and returns true; returns false
    /// if `r` is out of range or the arithmetic overflows.
    template<class... Levels>
    inline bool orb_unrank(int64_t r, int n, int* out) {
        return detail::rnk<detail::make_list<Levels...>>::unrank(r, static_cast<int64_t>(n), out);
    }

} // namespace orbit_wreath_utilities
