// =============================================================================
//  OrbWreathTest.cpp -- checker for src/cpp/orbit_wreath_utilities.hpp
//
//  Phase 1 of docs/plan-orbidx-bijections.md ships the C++ wreath-class storage
//  machinery; this file is its proof harness.  Every claim the header makes is
//  checked against ground truth built IN THIS FILE, never against the header
//  itself:
//
//    (a) orb_visit emits exactly the brute-force canonical set -- same content,
//        same ascending-lex order, same length -- where the brute force walks
//        all n^rank raw tuples, canonicalizes each, and dedups.  §3 of the plan
//        says outright that a read->write roundtrip cannot catch an order
//        mismatch (both sides shift together -- the antisym storage
//        post-mortem), so the oracle is independent by construction.
//    (b) orb_cell_count (the §4 closed-form fold) equals that stream's length.
//    (c) orb_rank(stream[i]) == i and orb_unrank(i) == stream[i]: the §3
//        bijection agrees with the §2 nest's visit order, the plan's one hard
//        constraint.
//    (d) sign spot checks on mixed-character classes.
//    (e) the §7.2 overflow wall diagnoses instead of wrapping.
//    (f) extent harmonization: a negative extent gets ONE verdict from every
//        entry point (count ORB_OVERFLOW, visit false, rank ORB_OVERFLOW,
//        unrank false), never a silent empty stream from one of them.
//    (g) the group-character oracle: for every element g of the class's wreath
//        group -- built HERE as explicit permutations with their characters,
//        the way proofs/OrbitEnum.fsx `buildWreath` does -- and every raw
//        tuple t,  canon(g.t) = chi(g) * canon(t).  This is the one check
//        that pins the canonicalizer's CHARACTER (not just its key) without
//        a second canonicalizer: a sign convention drift cannot survive it,
//        where the (d) spot checks only sample ~5 points.
//    (h) the STORAGE READ/WRITE path (plan-orbidx-decompaction.md §2): over a
//        pool filled through orb_visit with pool[i] = i+1, EVERY raw tuple of
//        the class is read and compared against chi * (rank+1) computed from
//        an in-file reference that shares no code with the header --
//        `ref_canon` below canonicalizes bottom-up with std::sort and CYCLE
//        parity, where the header recurses top-down with insertion sort and
//        INVERSION parity, and the rank comes from the brute stream's own
//        ordering, not from orb_rank.  Plus the write contract: accepted
//        exactly on canonical cells, and a rejected write provably touches no
//        memory.  Plus the domain contract: an out-of-range digit trips the
//        diagnostic hook and reads T(0) from a NULL pool -- if it touched
//        storage at all the harness would crash instead of reporting.
//    (i) the NESTED-POINTER dual view (plan-orbidx-bijections.md §1/§2):
//        orb_skeleton's leaves enumerate in exactly orb_visit order, navigate
//        lands on pool + rank for every canonical tuple and on nullptr for
//        every other raw tuple, the arena size is what build reports against
//        an independently counted node total, and hand-pinned nodes carry the
//        peeling bounds.
//
//  Also a Phase 0 anchor: the cardinalities OrbitEnum.fsx / the OrbIdx doc state
//  for the depth-1 and Riemann classes are asserted literally.
//
//  Build:  g++ -std=c++20 -O2 -o orb_wreath_tests src/cpp/orb_wreath_tests.cpp
//          (or `blade test orbwreath`, which compiles + runs the shipped copy
//          exactly like the alloc-layout suite -- tests/OrbWreathTests.fs)
//  Run:    orb_wreath_tests                    self-checks, exit 1 on any FAIL
//          orb_wreath_tests --dump "<spec>" n  the orb_visit stream, one tuple
//                                              per line, space-separated
//          orb_wreath_tests --read "<spec>" n  the DENSE view: every raw tuple
//                                              of the class over [0,n),
//                                              row-major with the LAST
//                                              coordinate varying fastest, as
//                                              "d0 d1 ... dk | v" where v is
//                                              orb_read against a pool filled
//                                              pool[i] = i+1 (int64 cells, so
//                                              "0" is the zero set and "-7" a
//                                              mirrored read).  Nothing else
//                                              goes to stdout.
//          orb_wreath_tests --specs            the menu's specs, one per line
//                                              (consumed by the F# cross-diff)
//
//  Unknown spec or negative extent: message on stderr, exit 2 (--dump/--read).
//
//  A spec is the level list innermost-first, matching the type's own spelling:
//  "2-,2+" is OrbIdx<[(2,-),(2,+)],n>, the Riemann shape.  The menu itself is
//  a generated CLOSURE (see the comment at menu()), not a curated list.
// =============================================================================

// The harness must EXERCISE orb_read's precondition-violation path, so it
// substitutes a COUNTING hook for the header's default assert: a violation is
// recorded rather than aborting, and section (h) then pins both halves of the
// contract -- that the hook fires exactly once per violation, and that the call
// still returns T(0) without ever indexing the pool.  Production callers keep
// the assert.  (The header defines ORB_ASSERT only #ifndef, for exactly this.)
static long g_orb_assert_hits = 0;
#define ORB_ASSERT(cond, msg) do { if (!(cond)) ++g_orb_assert_hits; } while (0)

#include "orbit_wreath_utilities.hpp"

#include <algorithm>
#include <chrono>
#include <cstddef>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <functional>
#include <new>
#include <set>
#include <string>
#include <utility>
#include <vector>

using namespace orbit_wreath_utilities;

// -----------------------------------------------------------------------------
// Arena accounting.  -fsanitize=address is NOT available on this toolchain
// (mingw-w64 ucrt64 ships no libasan/libubsan/liblsan -- `cannot find -lasan`),
// so the two claims asan would have covered are pinned directly instead, and
// more sharply than asan would have:
//
//   "ONE allocation for the node arrays"  -- every orb_skeleton::build must be
//        exactly one operator new[], and every free exactly one delete[].
//        asan cannot see the DIFFERENCE between one allocation and five.
//   "no leaks"                            -- the totals must balance at exit.
//
// orb_skeleton's arena is the only array-new in this program (std::vector,
// std::string and std::function all go through the scalar operator new), so
// these counters are effectively an arena-only ledger.  Section (i) measures
// DELTAS around build/free rather than absolutes, so a stray array-new
// elsewhere in libstdc++ could not turn into a spurious failure.
//
// The counters MUST be volatile.  Measured here (g++ 15.2, ucrt64): with plain
// statics the same source passes at -O0 and fails 100 checks at -O1/-O2, with
// the branch reporting operand values that make its own condition false.  GCC's
// allocation DCE (on from -O1) models replaceable global allocation functions
// as having no observable effect beyond the storage, so it happily keeps a
// counter read in a register across an inlined `operator delete[]` that
// increments it.  That is the standard's licence (C++14 N3664), not a bug --
// and the reason a counting allocator is normally a bad instrument.  `volatile`
// forces every increment and every read to be a real memory access, which
// restores agreement at every -O level; the delta checks below are pinned
// against the structural proof so a future toolchain cannot quietly re-break it.
//
// The other two things asan would have covered are checked WITHOUT the
// allocator, in skel_check: every node the DFS reaches lies inside
// [root, root + node_count), and the DFS visits every slot of that range
// exactly once -- N distinct nodes exactly tiling N slots is a proof that they
// came from ONE contiguous arena, independent of any allocation counting.
// -----------------------------------------------------------------------------

static volatile long long g_new_arr = 0;
static volatile long long g_del_arr = 0;

// (`x = x + 1` rather than `++x`: incrementing a volatile is deprecated in
// C++20 and -Wall -Wextra says so.)
void* operator new[](std::size_t sz) {
    g_new_arr = g_new_arr + 1;
    void* p = std::malloc(sz ? sz : 1);
    if (!p) throw std::bad_alloc();
    return p;
}
void operator delete[](void* p) noexcept {
    if (p) g_del_arr = g_del_arr + 1;
    std::free(p);
}
void operator delete[](void* p, std::size_t) noexcept {
    if (p) g_del_arr = g_del_arr + 1;
    std::free(p);
}

// -----------------------------------------------------------------------------
// Type-erased handle on one instantiated class, so the menu can be a table.
// -----------------------------------------------------------------------------

using Visitor = std::function<void(const int*, int64_t)>;

/// (i) runs per class through a function pointer so the menu stays a table;
/// defined below, forward-declared here because mk<> takes its address.
template<class... Ls>
static bool skel_check(int n, const std::vector<std::vector<int>>& want,
                       std::string& detail);

struct Cfg {
    std::string spec;
    int         axes;
    int64_t   (*count)(int);
    bool      (*visit)(int, const Visitor&);
    int       (*canon)(const int*, int*);
    int64_t   (*rank)(const int*, int);
    bool      (*unrank)(int64_t, int, int*);
    // (h) the storage layer.  T = double for the sweeps (needs unary minus and
    // exact small integers); T = long long for --read, so the printed values
    // are exact signed integers with no formatting judgement calls.
    double    (*read)(const double*, const int*, int);
    bool      (*read_ck)(const double*, const int*, int, double&);
    bool      (*write)(double*, const int*, int, double);
    long long (*read_i64)(const long long*, const int*, int);
    // (i) the nested-pointer dual view.
    bool      (*skel)(int, const std::vector<std::vector<int>>&, std::string&);
};

/// Spec string derived FROM the pack ("2-,2+", "1+", "[]"), never hand-written
/// beside it -- so a row's label cannot drift from the class it tests.
template<class... Ls>
static std::string spec_of() {
    if constexpr (sizeof...(Ls) == 0) {
        return "[]";
    } else {
        std::string s;
        ((s += (s.empty() ? "" : ",") + std::to_string(Ls::rank) + (Ls::pos ? "+" : "-")), ...);
        return s;
    }
}

template<class... Ls>
static Cfg mk() {
    return Cfg{
        spec_of<Ls...>(),
        orb_axes<Ls...>,
        &orb_cell_count<Ls...>,
        +[](int n, const Visitor& v) {
            return orb_visit<Ls...>(n, [&](const int* c, int64_t i) { v(c, i); });
        },
        &orb_canon<Ls...>,
        &orb_rank<Ls...>,
        &orb_unrank<Ls...>,
        &orb_read<double, Ls...>,
        &orb_read_checked<double, Ls...>,
        &orb_write_canonical<double, Ls...>,
        &orb_read<long long, Ls...>,
        &skel_check<Ls...>
    };
}

using L2p = orb_level<2, true>;
using L2m = orb_level<2, false>;
using L3p = orb_level<3, true>;
using L3m = orb_level<3, false>;

// -----------------------------------------------------------------------------
// The menu is a stated CLOSURE, not a curated list (adversarial-review
// follow-up, 2026-08-01: the empty class and rank-1 levels were exactly the
// rows a curated list forgot, and orb_rank's missing bounds check survived 149
// green checks in their absence).  Two generators cover it:
//
//   closure<D>   every class over the FULL level alphabet -- rank 1..3, both
//                signs -- up to depth D, including the empty class.
//   exact<D>     every sign pattern at exactly depth D over rank-2 levels
//                (full-alphabet depth 3 would multiply nest instantiations
//                and brute-force cost past the point of usefulness; rank 2 is
//                where §7.2 says real depth-3 classes live).
//
// menu() = closure<2> + exact<3> = 1 + 6 + 36 + 8 = 51 classes.  A class
// family not swept is now a statement about the BOUND (r <= 3 at d <= 2;
// r = 2 at d = 3), never an oversight inside it.  The depth-2 closure
// automatically contains the structural corner cases the old list argued for
// one by one: rank-1 above/below real levels, '+' rank-3 over a composite
// inner class (the only shape reaching chain<2> with a composite
// above<true>), and every sign mix.
// -----------------------------------------------------------------------------

template<int Depth, class... Prefix>
struct closure {
    static void add(std::vector<Cfg>& m) {
        m.push_back(mk<Prefix...>());
        if constexpr (Depth > 0) {
            closure<Depth - 1, Prefix..., orb_level<1, true >>::add(m);
            closure<Depth - 1, Prefix..., orb_level<1, false>>::add(m);
            closure<Depth - 1, Prefix..., orb_level<2, true >>::add(m);
            closure<Depth - 1, Prefix..., orb_level<2, false>>::add(m);
            closure<Depth - 1, Prefix..., orb_level<3, true >>::add(m);
            closure<Depth - 1, Prefix..., orb_level<3, false>>::add(m);
        }
    }
};

template<int Depth, class... Prefix>
struct exact {
    static void add(std::vector<Cfg>& m) {
        if constexpr (Depth == 0) {
            m.push_back(mk<Prefix...>());
        } else {
            exact<Depth - 1, Prefix..., orb_level<2, true >>::add(m);
            exact<Depth - 1, Prefix..., orb_level<2, false>>::add(m);
        }
    }
};

static const std::vector<Cfg>& menu() {
    static const std::vector<Cfg> m = [] {
        std::vector<Cfg> v;
        closure<2>::add(v);
        exact<3>::add(v);
        return v;
    }();
    return m;
}

static const Cfg* find_cfg(const char* spec) {
    for (const Cfg& c : menu())
        if (c.spec == spec) return &c;
    return nullptr;
}

// -----------------------------------------------------------------------------
// PASS/FAIL reporting (same shape as proofs/OrbitEnum.fsx's report)
// -----------------------------------------------------------------------------

static int nPass = 0;
static int nFail = 0;

static void report(const std::string& name, bool ok, const std::string& detail) {
    if (ok) ++nPass; else ++nFail;
    std::printf("%s  %-34s %s\n", ok ? "PASS" : "FAIL", name.c_str(), detail.c_str());
}

static std::string show(const std::vector<int>& t) {
    std::string s = "{";
    for (size_t i = 0; i < t.size(); ++i) {
        if (i) s += ",";
        s += std::to_string(t[i]);
    }
    return s + "}";
}

// -----------------------------------------------------------------------------
// Ground truth: every raw tuple, canonicalized, deduped, ascending lex.
// std::vector<int>'s operator< is lexicographical, and all tuples here have the
// same length, so std::set gives exactly the order §2 claims for the nest.
// -----------------------------------------------------------------------------

static std::vector<std::vector<int>> brute(const Cfg& c, int n) {
    std::set<std::vector<int>> seen;
    const int A = c.axes;
    int64_t total = 1;
    for (int i = 0; i < A; ++i) total *= n;
    std::vector<int> d(A), out(A);
    for (int64_t e = 0; e < total; ++e) {
        int64_t q = e;
        for (int j = A - 1; j >= 0; --j) { d[j] = static_cast<int>(q % n); q /= n; }
        if (c.canon(d.data(), out.data()) != 0) seen.insert(out);
    }
    return std::vector<std::vector<int>>(seen.begin(), seen.end());
}

// The nest's own output, plus a check that linear_index counts 0,1,2,...
// (visit returning false on a well-formed extent also fails the index check).
static std::vector<std::vector<int>> stream(const Cfg& c, int n, bool& idxOk) {
    std::vector<std::vector<int>> v;
    idxOk = true;
    if (!c.visit(n, [&](const int* p, int64_t i) {
            if (i != static_cast<int64_t>(v.size())) idxOk = false;
            v.emplace_back(p, p + c.axes);
        }))
        idxOk = false;
    return v;
}

// -----------------------------------------------------------------------------
// (a)+(b)+(c) for one (class, extent)
// -----------------------------------------------------------------------------

static void check_config(const Cfg& c, int n) {
    const std::string tag = std::string(c.spec) + " n=" + std::to_string(n);

    bool idxOk = false;
    const std::vector<std::vector<int>> got  = stream(c, n, idxOk);
    const std::vector<std::vector<int>> want = brute(c, n);

    // (a) exact stream match: content AND order.
    {
        bool ok = (got == want) && idxOk;
        std::string detail;
        if (ok) {
            detail = std::to_string(got.size()) + " cells, exact stream match";
        } else if (!idxOk) {
            detail = "linear_index is not 0,1,2,...";
        } else if (got.size() != want.size()) {
            detail = "got " + std::to_string(got.size()) + " cells, want "
                   + std::to_string(want.size());
        } else {
            size_t i = 0;
            while (i < got.size() && got[i] == want[i]) ++i;
            detail = "first divergence at " + std::to_string(i) + ": got "
                   + show(got[i]) + ", want " + show(want[i]);
        }
        report("visit==brute  " + tag, ok, detail);
    }

    // (b) the §4 closed form counts what the nest emits.
    {
        const int64_t m = c.count(n);
        const bool ok = (m == static_cast<int64_t>(want.size()));
        report("cell_count    " + tag, ok,
               ok ? ("fold=" + std::to_string(m))
                  : ("fold=" + std::to_string(m) + " brute="
                     + std::to_string(want.size())));
    }

    // (c) rank == visit position, and unrank inverts.
    {
        bool ok = true;
        std::string detail;
        std::vector<int> back(c.axes);
        for (size_t i = 0; i < want.size() && ok; ++i) {
            const int64_t r = c.rank(want[i].data(), n);
            if (r != static_cast<int64_t>(i)) {
                ok = false;
                detail = "rank" + show(want[i]) + " = " + std::to_string(r)
                       + ", want " + std::to_string(i);
                break;
            }
            if (!c.unrank(static_cast<int64_t>(i), n, back.data())) {
                ok = false;
                detail = "unrank(" + std::to_string(i) + ") failed";
                break;
            }
            if (back != want[i]) {
                ok = false;
                detail = "unrank(" + std::to_string(i) + ") = " + show(back)
                       + ", want " + show(want[i]);
                break;
            }
        }
        report("rank/unrank   " + tag, ok,
               ok ? (std::to_string(want.size()) + " cells round-trip") : detail);
    }

    // (c2) the rank DOMAIN, exhaustively: every tuple in the box
    // {-1, ..., n}^axes is either a stream cell (and ranks to its index) or
    // is refused with ORB_OVERFLOW.  No hand-picked probes: the box contains
    // every negative, every == n off-by-one, every non-canonical ordering --
    // in particular every ordered perturbation where ONLY the bounds check
    // stands between the tuple and a plausible neighbouring offset, the gap
    // orb_rank's original bug hid in.  The box only runs at the smallest
    // sweep extent: which side of each bound refuses is extent-independent,
    // (c) pins the interior at every extent, and (n+2)^axes at n = 3 caps
    // the cost at 5^9 ~ 2M probes for the largest menu class.
    if (n == 3) {
        int64_t box = 1;
        for (int i = 0; i < c.axes; ++i) box *= (n + 2);
        bool ok = true;
        std::string detail;
        int64_t cellsSeen = 0;
        std::vector<int> t(c.axes);
        for (int64_t e = 0; e < box && ok; ++e) {
            int64_t q = e;
            for (int j = c.axes - 1; j >= 0; --j) {
                t[j] = static_cast<int>(q % (n + 2)) - 1;
                q /= (n + 2);
            }
            const auto it = std::lower_bound(want.begin(), want.end(), t);
            const bool inStream = (it != want.end() && *it == t);
            const int64_t expect =
                inStream ? static_cast<int64_t>(it - want.begin()) : ORB_OVERFLOW;
            if (inStream) ++cellsSeen;
            const int64_t r = c.rank(t.data(), n);
            if (r != expect) {
                ok = false;
                detail = "rank" + show(t) + " = " + std::to_string(r)
                       + ", want " + std::to_string(expect);
            }
        }
        // The box must have visited the whole stream, or the sweep proved
        // less than it claims.
        if (ok && cellsSeen != static_cast<int64_t>(want.size())) {
            ok = false;
            detail = "box hit " + std::to_string(cellsSeen) + " of "
                   + std::to_string(want.size()) + " cells";
        }
        report("rank domain   " + tag, ok,
               ok ? (std::to_string(box) + " box probes, "
                     + std::to_string(cellsSeen) + " cells") : detail);
    }

    // (c3) the unrank DOMAIN, exhaustively: every index in [-2, M+1] either
    // inverts to stream[r] or is refused -- subsumes the out-of-range and
    // negative probes as the two ends of a swept interval.
    {
        bool ok = true;
        std::string detail;
        std::vector<int> back(c.axes);
        const int64_t M = static_cast<int64_t>(want.size());
        for (int64_t r = -2; r <= M + 1 && ok; ++r) {
            const bool got = c.unrank(r, n, back.data());
            const bool inRange = (r >= 0 && r < M);
            if (got != inRange) {
                ok = false;
                detail = "unrank(" + std::to_string(r) + ") "
                       + (got ? "accepted" : "refused") + ", want the opposite";
            } else if (inRange && back != want[r]) {
                ok = false;
                detail = "unrank(" + std::to_string(r) + ") = " + show(back)
                       + ", want " + show(want[r]);
            }
        }
        report("unrank domain " + tag, ok,
               ok ? ("[-2," + std::to_string(M + 1) + "] swept") : detail);
    }
}

// -----------------------------------------------------------------------------
// (d) canonicalization sign spot checks
// -----------------------------------------------------------------------------

template<class... Ls>
static void check_canon(const char* spec, std::vector<int> in,
                        int wantSign, std::vector<int> wantOut) {
    std::vector<int> out(in.size());
    const int s = orb_canon<Ls...>(in.data(), out.data());
    const bool ok = (s == wantSign) && (wantSign == 0 || out == wantOut);
    std::string detail = "sign " + std::to_string(s);
    if (s != 0) detail += " -> " + show(out);
    if (!ok) {
        detail += "; want sign " + std::to_string(wantSign);
        if (wantSign != 0) detail += " -> " + show(wantOut);
    }
    report(std::string(spec) + " canon " + show(in), ok, detail);
}

// -----------------------------------------------------------------------------
// (g) the group-character oracle
//
// The wreath group of the class, built HERE as explicit permutations exactly
// the way proofs/OrbitEnum.fsx `buildWreath` builds it -- G_0 trivial on one
// point, G_i = G_{i-1} wr S_{r_i} on deg*r_i points by
// (block b, offset x) -> (pi(b), g_b(x)) -- with the character carried along:
// chi(e) multiplies the sub-elements' characters and, at a '-' level, sgn(pi).
// The oracle then asserts, for EVERY group element g and EVERY raw tuple t,
//
//     canon(g.t) = chi(g) * canon(t)
//
// (same canonical key; sign scaled by chi; zero set closed under the action).
// This is the one check that pins the canonicalizer's CHARACTER without a
// second canonicalizer: the (d) spot checks sample ~5 points per harness, and
// a global sign-convention drift would sail through them and through every
// stream/rank check, none of which look at signs off the canonical set.
// -----------------------------------------------------------------------------

struct GElem {
    std::vector<int> perm;
    int              chi;
};

/// Parse a menu spec ("2-,2+", "1+", "[]") back into (rank, pos) levels,
/// innermost-first -- the same order the template pack spells.
static bool parse_spec(const char* spec, std::vector<std::pair<int, bool>>& out) {
    out.clear();
    if (std::strcmp(spec, "[]") == 0) return true;
    const char* p = spec;
    while (*p) {
        char* end = nullptr;
        const long r = std::strtol(p, &end, 10);
        if (end == p || r < 1) return false;
        if (*end != '+' && *end != '-') return false;
        out.emplace_back(static_cast<int>(r), *end == '+');
        p = end + 1;
        if (*p == ',') ++p;
        else if (*p) return false;
    }
    return true;
}

static void all_perms(int k, std::vector<std::vector<int>>& out) {
    out.clear();
    std::vector<int> cur(k);
    for (int i = 0; i < k; ++i) cur[i] = i;
    std::function<void(int)> go = [&](int i) {
        if (i == k) { out.push_back(cur); return; }
        for (int j = i; j < k; ++j) {
            std::swap(cur[i], cur[j]);
            go(i + 1);
            std::swap(cur[i], cur[j]);
        }
    };
    go(0);
}

/// +1 / -1 parity of a permutation, by inversion count.
static int perm_sign(const std::vector<int>& p) {
    int inv = 0;
    for (size_t i = 0; i < p.size(); ++i)
        for (size_t j = i + 1; j < p.size(); ++j)
            if (p[i] > p[j]) ++inv;
    return (inv & 1) ? -1 : 1;
}

static std::vector<GElem> build_signed_wreath(const std::vector<std::pair<int, bool>>& levels) {
    std::vector<GElem> g = { GElem{ { 0 }, 1 } };
    int deg = 1;
    for (const auto& [r, pos] : levels) {
        std::vector<std::vector<int>> pis;
        all_perms(r, pis);
        std::vector<GElem> next;
        std::vector<int> pick(r, 0);
        for (;;) {
            for (const std::vector<int>& pi : pis) {
                GElem e;
                e.perm.resize(static_cast<size_t>(deg) * r);
                e.chi = pos ? 1 : perm_sign(pi);
                for (int blk = 0; blk < r; ++blk) {
                    const GElem& gb = g[pick[blk]];
                    e.chi *= gb.chi;
                    for (int x = 0; x < deg; ++x)
                        e.perm[blk * deg + x] = pi[blk] * deg + gb.perm[x];
                }
                next.push_back(std::move(e));
            }
            int b = r - 1;
            while (b >= 0 && pick[b] == static_cast<int>(g.size()) - 1) { pick[b] = 0; --b; }
            if (b < 0) break;
            ++pick[b];
        }
        g = std::move(next);
        deg *= r;
    }
    return g;
}

static void check_chi_oracle(const Cfg& c, int64_t& pairs) {
    std::vector<std::pair<int, bool>> lv;
    if (!parse_spec(c.spec.c_str(), lv)) {
        report("chi-oracle    " + c.spec, false, "unparseable spec");
        return;
    }

    // |G| by the closed form prod_i |G_{i-1}|^{r_i} * r_i!, needed both to
    // validate the construction and to pick the extent.
    int64_t expect = 1;
    for (const auto& [r, pos] : lv) {
        (void)pos;
        int64_t fact = 1;
        for (int i = 2; i <= r; ++i) fact *= i;
        int64_t pw = 1;
        for (int i = 0; i < r; ++i) pw *= expect;
        expect = pw * fact;
    }

    // Extent by BUDGET, not by hand: the largest n in {4, 3, 2} whose full
    // sweep n^axes * |G| stays under 5M pairs.  Every menu class admits at
    // least n = 2 (worst case 3-,3-: 2^9 * 7776 ~ 4M), so nothing is
    // silently skipped -- the rule scales the sweep instead of dropping it.
    int n = 2;
    for (int cand : { 4, 3 }) {
        int64_t raw = 1;
        bool over = false;
        for (int i = 0; i < c.axes && !over; ++i) {
            raw *= cand;
            if (raw > 5'000'000) over = true;
        }
        if (!over && raw * expect <= 5'000'000) { n = cand; break; }
    }
    const std::string tag = c.spec + " n=" + std::to_string(n);

    const std::vector<GElem> G = build_signed_wreath(lv);
    if (static_cast<int64_t>(G.size()) != expect
        || static_cast<int>(G[0].perm.size()) != c.axes) {
        report("chi-oracle    " + tag, false,
               "group built wrong: |G|=" + std::to_string(G.size())
               + " want " + std::to_string(expect) + ", degree "
               + std::to_string(G[0].perm.size()) + " want " + std::to_string(c.axes));
        return;
    }

    const int A = c.axes;
    int64_t total = 1;
    for (int i = 0; i < A; ++i) total *= n;

    std::vector<int> t(A), u(A), k0(A), k1(A);
    bool ok = true;
    std::string detail;
    for (int64_t e = 0; e < total && ok; ++e) {
        int64_t q = e;
        for (int j = A - 1; j >= 0; --j) { t[j] = static_cast<int>(q % n); q /= n; }
        const int s0 = c.canon(t.data(), k0.data());
        for (const GElem& g : G) {
            for (int i = 0; i < A; ++i) u[i] = t[g.perm[i]];
            const int s1 = c.canon(u.data(), k1.data());
            ++pairs;
            const bool good = (s0 == 0) ? (s1 == 0)
                                        : (s1 == g.chi * s0 && k1 == k0);
            if (!good) {
                ok = false;
                detail = "t=" + show(t) + " g.t=" + show(u) + ": canon(t) sign "
                       + std::to_string(s0) + ", canon(g.t) sign " + std::to_string(s1)
                       + ", chi(g) " + std::to_string(g.chi);
                break;
            }
        }
    }
    report("chi-oracle    " + tag, ok,
           ok ? (std::to_string(total * static_cast<int64_t>(G.size()))
                 + " (g,t) pairs, |G|=" + std::to_string(G.size()))
              : detail);
}

// -----------------------------------------------------------------------------
// (h) the storage read/write path
//
// The reference canonicalizer used here shares NO code and NO shape with the
// header's:
//
//   header      compile-time template recursion over the level list,
//               OUTERMOST first, insertion sort on composite keys, character
//               from an O(R^2) INVERSION count.
//   ref_canon   runtime loop over the parsed spec, INNERMOST first, std::sort
//               on block indices, character from a CYCLE decomposition of the
//               sorting permutation.
//
// (inversions parity == cycle parity is a theorem, which is the point: two
// computations that must agree without sharing a line.)  The rank half of the
// reference is the position of the key in the brute stream (std::set order),
// never orb_rank -- so (h) can fail on a composition bug in orb_read even with
// orb_canon and orb_rank both correct, which is the whole reason it exists.
// -----------------------------------------------------------------------------

static int ref_canon(const std::vector<std::pair<int, bool>>& lv,
                     const int* in, int* out, int len) {
    for (int i = 0; i < len; ++i) out[i] = in[i];
    int chi  = 1;
    int span = 1;                        // coordinates per block at this level
    for (const auto& lvl : lv) {
        const int  r        = lvl.first;
        const bool pos      = lvl.second;
        const int  groupLen = span * r;
        for (int g = 0; g + groupLen <= len; g += groupLen) {
            const int* blk = out + g;
            int ord[32];
            for (int i = 0; i < r; ++i) ord[i] = i;
            const auto less = [&](int a, int b) {
                for (int q = 0; q < span; ++q) {
                    const int x = blk[a * span + q];
                    const int y = blk[b * span + q];
                    if (x != y) return x < y;
                }
                return false;
            };
            std::sort(ord, ord + r, less);
            if (!pos) {
                // §5 zero set: two equal blocks under a '-' level.
                for (int i = 0; i + 1 < r; ++i)
                    if (!less(ord[i], ord[i + 1])) return 0;
                // sgn = (-1)^(r - cycles) -- the cycle-count route to the same
                // parity the header gets by counting inversions.
                char seen[32] = { 0 };
                int  cycles   = 0;
                for (int i = 0; i < r; ++i) {
                    if (seen[i]) continue;
                    ++cycles;
                    int j = i;
                    while (!seen[j]) { seen[j] = 1; j = ord[j]; }
                }
                if (((r - cycles) & 1) != 0) chi = -chi;
            }
            int tmp[64];
            for (int i = 0; i < r; ++i)
                for (int q = 0; q < span; ++q)
                    tmp[i * span + q] = blk[ord[i] * span + q];
            for (int i = 0; i < groupLen; ++i) out[g + i] = tmp[i];
        }
        span = groupLen;
    }
    return chi;
}

static void check_read_write(const Cfg& c, int n,
                             const std::vector<std::vector<int>>& want) {
    const std::string tag = c.spec + " n=" + std::to_string(n);

    std::vector<std::pair<int, bool>> lv;
    if (!parse_spec(c.spec.c_str(), lv)) {
        report("read/write    " + tag, false, "unparseable spec");
        return;
    }

    const int    A = c.axes;
    const size_t M = want.size();

    // The pool is filled through orb_visit -- the traversal path -- and read
    // back through the random-access path, so the two §1 access paths are
    // pinned against each other and not just each against itself.
    std::vector<double> pool(M ? M : 1, 0.0);
    bool ok = c.visit(n, [&](const int*, int64_t i) {
        if (i >= 0 && static_cast<size_t>(i) < M) pool[static_cast<size_t>(i)] = static_cast<double>(i + 1);
    });
    std::string detail = ok ? "" : "visit refused a well-formed extent";

    // The write sweep starts from a copy of the same values, so a rejected
    // write that scribbles anywhere shows up as a changed cell afterwards.
    std::vector<double> scratch(pool);

    int64_t total = 1;
    for (int i = 0; i < A; ++i) total *= n;

    const long hits0 = g_orb_assert_hits;
    int64_t nZero = 0, nNeg = 0, nCell = 0;
    std::vector<int> zeroWitness;
    std::vector<int> t(A), key(A);

    for (int64_t e = 0; e < total && ok; ++e) {
        int64_t q = e;
        for (int j = A - 1; j >= 0; --j) { t[j] = static_cast<int>(q % n); q /= n; }

        const int chi = ref_canon(lv, t.data(), key.data(), A);
        size_t idx      = 0;
        bool   inStream = false;
        if (chi == 0) {
            ++nZero;
            if (zeroWitness.empty()) zeroWitness = t;
        } else {
            const auto it = std::lower_bound(want.begin(), want.end(), key);
            if (it == want.end() || *it != key) {
                ok = false;
                detail = "reference canon " + show(t) + " -> " + show(key)
                       + " is not a stream cell";
                break;
            }
            idx      = static_cast<size_t>(it - want.begin());
            inStream = (key == t);
        }
        if (chi < 0) ++nNeg;
        if (inStream) ++nCell;

        const double expect = (chi == 0) ? 0.0
                                         : static_cast<double>(chi) * static_cast<double>(idx + 1);

        const double got = c.read(pool.data(), t.data(), n);
        if (got != expect) {
            ok = false;
            detail = "read" + show(t) + " = " + std::to_string(got)
                   + ", want " + std::to_string(expect);
            break;
        }
        double outv = -12345.0;
        if (!c.read_ck(pool.data(), t.data(), n, outv) || outv != expect) {
            ok = false;
            detail = "read_checked" + show(t) + " disagrees with read";
            break;
        }
        // Write must refuse everything that is not EXACTLY a canonical cell:
        // mirrored (chi = -1), zero set (chi = 0), and non-canonical-but-even
        // (chi = +1, not a fixed point -- the 3-cycle case).
        if (!inStream && c.write(scratch.data(), t.data(), n, -999.0)) {
            ok = false;
            detail = "write accepted a non-canonical tuple " + show(t);
            break;
        }
    }

    if (ok && nCell != static_cast<int64_t>(M)) {
        ok = false;
        detail = "sweep found " + std::to_string(nCell) + " canonical cells, stream has "
               + std::to_string(M);
    }
    if (ok && scratch != pool) {
        ok = false;
        detail = "a REJECTED write modified the pool";
    }
    // Accepted writes land on exactly the right cell, and read them back.
    for (size_t i = 0; i < M && ok; ++i) {
        if (!c.write(scratch.data(), want[i].data(), n, -static_cast<double>(i + 1))) {
            ok = false;
            detail = "write refused the canonical cell " + show(want[i]);
        }
    }
    for (size_t i = 0; i < M && ok; ++i) {
        if (scratch[i] != -static_cast<double>(i + 1)) {
            ok = false;
            detail = "after writing cell " + std::to_string(i) + " the pool holds "
                   + std::to_string(scratch[i]);
        } else if (c.read(scratch.data(), want[i].data(), n) != -static_cast<double>(i + 1)) {
            ok = false;
            detail = "read-back of cell " + std::to_string(i) + " disagrees";
        }
    }
    if (ok && g_orb_assert_hits != hits0) {
        ok = false;
        detail = "an IN-DOMAIN read tripped the precondition hook";
    }
    report("read/write    " + tag, ok,
           ok ? (std::to_string(total) + " raw tuples: " + std::to_string(nCell)
                 + " cells, " + std::to_string(nNeg) + " mirrored, "
                 + std::to_string(nZero) + " zero-set")
              : detail);

    // --- the domain contract -------------------------------------------------
    // Drive each axis of each stream cell off both ends.  These are exactly the
    // tuples that would rank onto a VALID NEIGHBOURING cell if the bounds check
    // were missing, so "returns 0" is not enough: the pool pointer is NULL, so a
    // read that touches storage crashes the harness instead of reporting.
    if (ok) {
        bool    dok = true;
        int64_t probes = 0;
        std::string ddet;
        for (size_t i = 0; i < M && dok; ++i) {
            for (int k = 0; k < A && dok; ++k) {
                const int bad[2] = { -1, n };
                for (int b = 0; b < 2 && dok; ++b) {
                    std::vector<int> u = want[i];
                    u[k] = bad[b];
                    double outv = -12345.0;
                    const long before = g_orb_assert_hits;
                    if (c.read_ck(nullptr, u.data(), n, outv)) {
                        dok = false; ddet = "read_checked accepted " + show(u);
                    } else if (outv != -12345.0) {
                        dok = false; ddet = "read_checked wrote `out` on refusal for " + show(u);
                    } else if (g_orb_assert_hits != before) {
                        dok = false; ddet = "read_checked tripped the hook (it must not) for " + show(u);
                    } else if (c.read(nullptr, u.data(), n) != 0.0) {
                        dok = false; ddet = "read" + show(u) + " returned a nonzero value";
                    } else if (g_orb_assert_hits != before + 1) {
                        dok = false; ddet = "read" + show(u) + " did not trip the hook exactly once";
                    } else if (c.write(nullptr, u.data(), n, 1.0)) {
                        dok = false; ddet = "write accepted the out-of-range tuple " + show(u);
                    }
                    ++probes;
                }
            }
        }
        // The one case orb_rank's bounds check CANNOT backstop, and therefore
        // the sole reason the digit check exists: an all-out-of-range tuple
        // that CANONICALIZES INTO THE ZERO SET before any rank arithmetic could
        // object.  Under a '-' level every sub-block is equal here, so canon
        // returns character 0 -- and without the digit check that would be
        // reported as a perfectly in-domain structural zero.
        for (int b = 0; b < 2 && dok; ++b) {
            std::vector<int> u(A, b == 0 ? n : -1);
            double outv = -12345.0;
            const long before = g_orb_assert_hits;
            if (c.read_ck(nullptr, u.data(), n, outv)) {
                dok = false; ddet = "read_checked accepted the all-out-of-range " + show(u);
            } else if (outv != -12345.0) {
                dok = false; ddet = "read_checked wrote `out` for " + show(u);
            } else if (c.read(nullptr, u.data(), n) != 0.0
                       || g_orb_assert_hits != before + 1) {
                dok = false; ddet = "read" + show(u) + " did not trip the hook exactly once";
            } else if (c.write(nullptr, u.data(), n, 1.0)) {
                dok = false; ddet = "write accepted " + show(u);
            }
            ++probes;
        }
        // The zero set is IN the domain: T(0), no pool access, no diagnostic.
        if (dok && !zeroWitness.empty()) {
            const long before = g_orb_assert_hits;
            double outv = -12345.0;
            if (!c.read_ck(nullptr, zeroWitness.data(), n, outv) || outv != 0.0) {
                dok = false; ddet = "zero-set read_checked refused " + show(zeroWitness);
            } else if (c.read(nullptr, zeroWitness.data(), n) != 0.0) {
                dok = false; ddet = "zero-set read is not 0";
            } else if (g_orb_assert_hits != before) {
                dok = false; ddet = "the zero set tripped the precondition hook";
            }
        }
        report("read domain   " + tag, dok,
               dok ? (std::to_string(probes) + " out-of-range probes against a NULL pool"
                      + (zeroWitness.empty() ? ", no zero set" : ", zero set is in-domain"))
                   : ddet);
    }
}

// -----------------------------------------------------------------------------
// (i) the nested-pointer dual view
// -----------------------------------------------------------------------------

template<class... Ls>
static bool skel_check(int n, const std::vector<std::vector<int>>& want,
                       std::string& detail) {
    using Skel = orbit_wreath_utilities::orb_skeleton<double, Ls...>;
    using Node = typename Skel::node;
    constexpr int A = orb_axes<Ls...>;
    const size_t M = want.size();

    std::vector<double> pool(M ? M : 1, 0.0);
    for (size_t i = 0; i < M; ++i) pool[i] = static_cast<double>(i + 1);

    Skel sk;
    const long long an0 = g_new_arr;
    if (!sk.build(n, pool.data())) { detail = "build refused a well-formed extent"; return false; }
    const long long buildAllocs = g_new_arr - an0;
    if (sk.cells() != static_cast<int64_t>(M)) {
        detail = "cells=" + std::to_string(sk.cells()) + ", stream has " + std::to_string(M);
        return false;
    }
    if (sk.base() != pool.data()) { detail = "base() is not the pool base"; return false; }
    if (sk.extent() != n)         { detail = "extent() drifted"; return false; }

    // Node total, counted INDEPENDENTLY from the brute stream: a depth-k node
    // is a distinct canonical prefix of length k, so the arena holds exactly
    // sum_k (#distinct length-k prefixes) records.  build() must report that,
    // and arena_bytes() must be that times sizeof(node) -- "the arena size is
    // what build reports" with an outside witness for the number.
    int64_t expectNodes = 0;
    if (M > 0) {
        for (int k = 0; k < A; ++k) {
            int64_t p = 1;
            for (size_t i = 1; i < M; ++i) {
                bool diff = false;
                for (int j = 0; j < k && !diff; ++j)
                    if (want[i][j] != want[i - 1][j]) diff = true;
                if (diff) ++p;
            }
            expectNodes += p;
        }
    }
    if (sk.node_count() != expectNodes) {
        detail = "node_count=" + std::to_string(sk.node_count()) + ", prefixes give "
               + std::to_string(expectNodes);
        return false;
    }
    if (sk.arena_bytes() != static_cast<size_t>(expectNodes) * sizeof(Node)) {
        detail = "arena_bytes disagrees with node_count * sizeof(node)";
        return false;
    }
    // "ONE allocation for the node arrays": exactly one array-new, and none at
    // all when the class has no cells.
    const long long wantAllocs = (expectNodes > 0) ? 1 : 0;
    if (buildAllocs != wantAllocs) {
        detail = "build made " + std::to_string(buildAllocs) + " array allocations, want "
               + std::to_string(wantAllocs);
        return false;
    }

    // Leaves in DFS order must BE the orb_visit stream, cell for cell and
    // pointer for pointer -- the §1 claim that the traversal order is the
    // allocation order, checked rather than assumed.
    std::vector<std::vector<int>> leafT;
    std::vector<const double*>    leafP;
    std::vector<int>              acc;
    bool structOk = true;
    const Node* const arena0 = sk.root();
    const Node* const arenaN = arena0 + sk.node_count();
    // Slot occupancy: proof that the N nodes exactly TILE one contiguous block
    // of N records, i.e. that there is exactly one arena -- established from the
    // pointers themselves, with no allocator instrumentation involved.
    std::vector<char> slot(static_cast<size_t>(sk.node_count()), 0);
    // The §2 peeling bound, swept rather than hand-picked.  The header claims
    // the node span equals the nest's loop trip count n - lo at EVERY leaf row
    // of EVERY class (the last axis is never a pinned coordinate, its loop runs
    // to n, and every iteration emits a cell), and at EVERY node of a '+'-only
    // class (every value extends -- the all-equal completion is canonical).
    // Both are asserted here over the whole menu, so the seven hand-pinned
    // nodes in main() are illustrations of a swept invariant, not the invariant.
    constexpr bool hasMinus = orb_has_minus_level<Ls...>;
    bool boundOk = true;
    std::vector<int> boundBad;
    std::function<void(const Node*, int)> go = [&](const Node* nd, int depth) {
        if (!structOk) return;
        // Every node the walk reaches must live inside the ONE arena -- the
        // stand-in for the out-of-bounds check asan would have done.
        if (nd < arena0 || nd >= arenaN) { structOk = false; return; }
        if (slot[static_cast<size_t>(nd - arena0)]++) { structOk = false; return; }
        if ((depth == A - 1 || !hasMinus) && nd->count != n - nd->lo && boundOk) {
            boundOk  = false;
            boundBad = acc;
            boundBad.push_back(depth);
        }
        if (depth == A - 1) {
            if (nd->kids != nullptr) { structOk = false; return; }
            for (int i = 0; i < nd->count; ++i) {
                acc.push_back(nd->lo + i);
                leafT.push_back(acc);
                leafP.push_back(nd->row + i);
                acc.pop_back();
            }
        } else {
            if (nd->row != nullptr) { structOk = false; return; }
            for (int i = 0; i < nd->count; ++i) {
                acc.push_back(nd->lo + i);
                go(nd->kids + i, depth + 1);
                acc.pop_back();
            }
        }
    };
    if (sk.root()) go(sk.root(), 0);
    if (!structOk) {
        detail = "a node left the arena, was reached twice, or carries both a child run and a row";
        return false;
    }
    for (size_t i = 0; i < slot.size(); ++i)
        if (!slot[i]) {
            detail = "arena slot " + std::to_string(i) + " is unreachable -- the nodes do "
                     "not tile one contiguous block";
            return false;
        }
    if (!boundOk) {
        detail = "peeling bound: node under prefix/depth " + show(boundBad)
               + " has count != n - lo, which this class's shape forbids";
        return false;
    }
    if (leafT != want) {
        detail = "leaf DFS is not the orb_visit stream (" + std::to_string(leafT.size())
               + " leaves vs " + std::to_string(M) + ")";
        if (leafT.size() == M) {
            size_t i = 0;
            while (i < M && leafT[i] == want[i]) ++i;
            detail = "leaf " + std::to_string(i) + " is " + show(leafT[i])
                   + ", stream says " + show(want[i]);
        }
        return false;
    }
    for (size_t i = 0; i < M; ++i) {
        if (leafP[i] != pool.data() + i) {
            detail = "leaf " + std::to_string(i) + " points at pool+"
                   + std::to_string(leafP[i] - pool.data());
            return false;
        }
    }

    // navigate lands on pool + rank for every canonical tuple, and on nullptr
    // for every OTHER raw tuple in the box -- so a non-canonical tuple can
    // never be silently redirected onto some other orbit's cell.
    int64_t total = 1;
    for (int i = 0; i < A; ++i) total *= n;
    std::vector<int> t(A);
    for (int64_t e = 0; e < total; ++e) {
        int64_t q = e;
        for (int j = A - 1; j >= 0; --j) { t[j] = static_cast<int>(q % n); q /= n; }
        const auto it = std::lower_bound(want.begin(), want.end(), t);
        const bool in = (it != want.end() && *it == t);
        const double* expect = in ? pool.data() + (it - want.begin()) : nullptr;
        const double* got    = sk.navigate(t.data());
        if (got != expect) {
            detail = "navigate" + show(t) + (got ? " -> pool+" + std::to_string(got - pool.data())
                                                 : " -> null");
            detail += in ? ", want pool+" + std::to_string(it - want.begin()) : ", want null";
            return false;
        }
        if (in && got != pool.data() + orb_rank<Ls...>(t.data(), n)) {
            detail = "navigate" + show(t) + " disagrees with orb_rank";
            return false;
        }
    }

    // free() releases and resets; build() is re-entrant on the same object.
    const long long fn0 = g_new_arr, fd0 = g_del_arr;
    sk.free();
    if (g_new_arr != fn0 || g_del_arr - fd0 != wantAllocs) {
        detail = "free(): +" + std::to_string(g_new_arr - fn0) + " new[], +"
               + std::to_string(g_del_arr - fd0) + " delete[], want +0/+"
               + std::to_string(wantAllocs);
        return false;
    }
    if (sk.node_count() != 0 || sk.root() != nullptr || sk.base() != nullptr
        || sk.cells() != 0) {
        detail = "free() left state behind";
        return false;
    }
    if (sk.navigate(want.empty() ? t.data() : want[0].data()) != nullptr) {
        detail = "navigate on a freed skeleton is not null";
        return false;
    }
    const long long rn0 = g_new_arr;
    if (!sk.build(n, pool.data()) || sk.node_count() != expectNodes
        || g_new_arr - rn0 != wantAllocs) {
        detail = "rebuild after free disagrees with the first build";
        return false;
    }
    // The destructor releases this one on the way out; the running totals are
    // reconciled once, at the end of main.
    detail = std::to_string(M) + " leaves, " + std::to_string(expectNodes) + " nodes, "
           + std::to_string(sk.arena_bytes()) + " arena bytes, " + std::to_string(total)
           + " navigate probes, 1 alloc, bounds "
           + (hasMinus ? "n-lo on leaf rows" : "n-lo everywhere");
    return true;
}

/// Hand-pinned node: walk `prefix` from the root and assert the §2 peeling
/// span it serves.  `note` records WHY the number is what it is -- in
/// particular whether it is the full trip count n - lo or a tail truncated by a
/// deeper level's emptiness (see the header's "DIVERGENCE FROM build_skeleton").
template<class... Ls>
static void pin_node(const char* spec, int n, const std::vector<int>& prefix,
                     int wantLo, int wantCount, const char* note) {
    using Skel = orbit_wreath_utilities::orb_skeleton<double, Ls...>;
    const int64_t M = orb_cell_count<Ls...>(n);
    std::vector<double> pool(M > 0 ? static_cast<size_t>(M) : 1, 0.0);
    Skel sk;
    const bool built = sk.build(n, pool.data());
    const typename Skel::node* nd = built ? sk.root() : nullptr;
    for (size_t i = 0; i < prefix.size() && nd; ++i) {
        const int ci = prefix[i] - nd->lo;
        nd = (ci < 0 || ci >= nd->count) ? nullptr : nd->kids + ci;
    }
    const bool ok = (nd != nullptr) && nd->lo == wantLo && nd->count == wantCount;
    std::string d = nd ? ("lo=" + std::to_string(nd->lo) + " count=" + std::to_string(nd->count))
                       : std::string("no such node");
    if (!ok) d += "; want lo=" + std::to_string(wantLo) + " count=" + std::to_string(wantCount);
    d += "  [" + std::string(note) + "]";
    report(std::string(spec) + " node" + show(prefix) + " n=" + std::to_string(n), ok, d);
}

// -----------------------------------------------------------------------------
// --dump / --read
// -----------------------------------------------------------------------------

static int dump(const char* spec, int n) {
    const Cfg* c = find_cfg(spec);
    if (!c) { std::fprintf(stderr, "unknown spec: %s\n", spec); return 2; }
    if (n < 0) { std::fprintf(stderr, "bad extent: %d\n", n); return 2; }
    if (!c->visit(n, [&](const int* p, int64_t) {
            for (int i = 0; i < c->axes; ++i)
                std::printf(i ? " %d" : "%d", p[i]);
            std::printf("\n");
        })) {
        std::fprintf(stderr, "visit refused extent: %d\n", n);
        return 2;
    }
    return 0;
}

/// --read: the DENSE view of the class, one line per raw tuple.
///
///     d0 d1 ... dk | v
///
/// with the tuples enumerated row-major over [0,n)^axes -- LAST coordinate
/// varying fastest -- and `v` the orb_read value against a pool filled
/// pool[i] = i + 1 in orb_visit order.  Cells are int64, so `v` is exact:
/// "0" is the zero set, a negative value is a mirrored read, and a positive
/// value is 1 + the rank of the tuple's canonical representative.  This is
/// plan-orbidx-decompaction.md §2 printed out, and it is what an external
/// consumer diffs against its own decompaction.
static int read_dump(const char* spec, int n) {
    const Cfg* c = find_cfg(spec);
    if (!c) { std::fprintf(stderr, "unknown spec: %s\n", spec); return 2; }
    if (n < 0) { std::fprintf(stderr, "bad extent: %d\n", n); return 2; }

    const int64_t M = c->count(n);
    if (M == ORB_OVERFLOW) {
        std::fprintf(stderr, "cell count overflows: %s n=%d\n", spec, n);
        return 2;
    }
    std::vector<long long> pool(M > 0 ? static_cast<size_t>(M) : 1, 0);
    if (!c->visit(n, [&](const int*, int64_t i) {
            if (i >= 0 && i < M) pool[static_cast<size_t>(i)] = static_cast<long long>(i + 1);
        })) {
        std::fprintf(stderr, "visit refused extent: %d\n", n);
        return 2;
    }

    const int A = c->axes;
    int64_t total = 1;
    for (int i = 0; i < A; ++i) total *= n;
    std::vector<int> t(A);
    for (int64_t e = 0; e < total; ++e) {
        int64_t q = e;
        for (int j = A - 1; j >= 0; --j) { t[j] = static_cast<int>(q % n); q /= n; }
        for (int i = 0; i < A; ++i) std::printf(i ? " %d" : "%d", t[i]);
        std::printf(" | %lld\n", c->read_i64(pool.data(), t.data(), n));
    }
    return 0;
}

// -----------------------------------------------------------------------------

int main(int argc, char** argv) {
    if (argc >= 2 && std::strcmp(argv[1], "--dump") == 0) {
        if (argc < 4) {
            std::fprintf(stderr, "usage: OrbWreathTest --dump \"<spec>\" <n>\n");
            return 2;
        }
        return dump(argv[2], std::atoi(argv[3]));
    }
    if (argc >= 2 && std::strcmp(argv[1], "--read") == 0) {
        if (argc < 4) {
            std::fprintf(stderr, "usage: OrbWreathTest --read \"<spec>\" <n>\n");
            return 2;
        }
        return read_dump(argv[2], std::atoi(argv[3]));
    }
    if (argc >= 2 && std::strcmp(argv[1], "--specs") == 0) {
        // The menu's spec strings, one per line -- so external consumers (the
        // F# cross-diff in tests/OrbWreathTests.fs) enumerate the ACTUAL menu
        // instead of keeping a hand copy that can drift from it.
        for (const Cfg& c : menu())
            std::printf("%s\n", c.spec.c_str());
        return 0;
    }

    const auto t0 = std::chrono::steady_clock::now();

    // Pin the closure size: 1 empty + 6 depth-1 + 36 depth-2 + 8 exact-depth-3
    // rank-2.  A generator edit that silently shrinks the menu fails here.
    report("menu = closure(d<=2, r<=3) + exact(d=3, r=2)", menu().size() == 51,
           std::to_string(menu().size()) + " classes");

    std::printf("\n--- Phase 0 anchor: doc/OrbitEnum cardinalities ---\n");
    struct Card { const char* spec; int n; int64_t want; };
    static const Card cards[] = {
        { "[]",       4,    4 },   // OrbIdx<[],n> == Idx<n>
        { "1+",       4,    4 },   // rank-1 level: trivial group, a no-op
        { "1+,2+",    4,   10 },   // ... under a real level: same as "2+"
        { "2+",       4,   10 },   // SymIdx<2,4>
        { "2-",       4,    6 },   // AntisymIdx<2,4>
        { "3+",       3,   10 },
        { "3-",       4,    4 },
        { "2-,2+",    4,   21 },   // RiemannIdx<4> = 21 (formalism §3.4)
        { "2+,2+",    3,   21 },   // deduced func(A,A) class
        { "2+,2+",    4,   55 },
        { "2+,2-",    4,   45 },
        { "2-,2-",    4,   15 },
        { "3+,2+",    3,   55 },
        { "2+,3-",    3,   20 },
        { "2+,2+,2+", 4, 1540 },   // depth 3 vs 65536 dense
    };
    for (const Card& k : cards) {
        const Cfg* c = find_cfg(k.spec);
        if (!c) { report(std::string(k.spec) + " (spec not in menu)", false, "find_cfg returned null"); continue; }
        const int64_t got = c->count(k.n);
        report(std::string(k.spec) + " n=" + std::to_string(k.n),
               got == k.want,
               got == k.want ? ("cells=" + std::to_string(got))
                             : ("cells=" + std::to_string(got) + ", doc says "
                                + std::to_string(k.want)));
    }

    std::printf("\n--- (a)(b)(c) nest / fold / rank over the menu ---\n");
    for (const Cfg& c : menu())
        for (int n : { 3, 4, 5 })
            check_config(c, n);

    std::printf("\n--- (d) canonicalization sign spot checks ---\n");
    // "2-,2+": antisym within each pair, sym between the two pairs.
    check_canon<L2m, L2p>("2-,2+", { 2, 0, 1, 0 },  1, { 0, 1, 0, 2 });
    check_canon<L2m, L2p>("2-,2+", { 2, 0, 1, 2 }, -1, { 0, 2, 1, 2 });
    check_canon<L2m, L2p>("2-,2+", { 1, 1, 0, 2 },  0, {});
    // "2+,2-": sym within each pair, antisym between them.
    check_canon<L2p, L2m>("2+,2-", { 0, 1, 1, 0 },  0, {});
    check_canon<L2p, L2m>("2+,2-", { 1, 2, 0, 1 }, -1, { 0, 1, 1, 2 });
    // canon is idempotent with character +1 on its own output (OrbitEnum §5).
    {
        bool ok = true;
        std::vector<int> out;
        for (const Cfg& c : menu()) {
            bool idxOk = false;
            for (const std::vector<int>& t : stream(c, 4, idxOk)) {
                out.assign(c.axes, 0);
                std::vector<int> in = t;
                if (c.canon(in.data(), out.data()) != 1 || out != t) ok = false;
            }
        }
        report("canon idempotent, character +1", ok, "over every menu class at n=4");
    }

    std::printf("\n--- (e) §7.2 overflow wall: diagnose, never wrap ---\n");
    {
        const int64_t m = orb_cell_count<L2p, L2p, L2p>(1000);
        report("2+,2+,2+ n=1000 overflows", m == ORB_OVERFLOW,
               m == ORB_OVERFLOW ? "reported ORB_OVERFLOW"
                                 : ("wrapped to " + std::to_string(m)));
    }
    {
        // The last depth that still fits at n=1000 must fit (the wall is where
        // the doc's table puts it, not one level early).
        const int64_t m2 = orb_cell_count<L2p, L2p>(1000);
        report("2+,2+ n=1000 still fits", m2 == 125250375250LL,
               "cells=" + std::to_string(m2));
    }
    {
        const int64_t m = orb_cell_count<L2p, L2p, L2p>(360);
        const bool ok = (m > 2000000000000000000LL && m < 2400000000000000000LL);
        report("2+,2+,2+ n=360 is ~2.2e18", ok, "cells=" + std::to_string(m));
    }
    {
        const int64_t m = orb_cell_count<L2p, L2p, L2p, L2p>(360);
        report("2+^4 n=360 overflows", m == ORB_OVERFLOW,
               m == ORB_OVERFLOW ? "reported ORB_OVERFLOW"
                                 : ("wrapped to " + std::to_string(m)));
    }
    {
        // Degenerate but legal: a strict level wider than the extent stores
        // nothing, and the nest must emit nothing rather than misbehave.
        const int64_t m = orb_cell_count<L3m>(2);
        bool emitted = false;
        const bool v = orb_visit<L3m>(2, [&](const int*, int64_t) { emitted = true; });
        report("3- n=2 is empty", m == 0 && v && !emitted,
               "cells=" + std::to_string(m) + (emitted ? ", nest emitted!" : ", nest emitted nothing"));
    }

    std::printf("\n--- (f) extent harmonization: n < 0 is ONE verdict everywhere ---\n");
    {
        // A negative extent must be REFUSED by every entry point -- count says
        // ORB_OVERFLOW, and visit now says false rather than silently emitting
        // an empty stream while count diagnoses the same input.
        bool em = false;
        const bool v = orb_visit<L2p>(-1, [&](const int*, int64_t) { em = true; });
        int t2[2] = { 0, 1 };
        int out2[2];
        report("2+ n=-1: count/visit/rank/unrank all refuse",
               orb_cell_count<L2p>(-1) == ORB_OVERFLOW && !v && !em
               && orb_rank<L2p>(t2, -1) == ORB_OVERFLOW
               && !orb_unrank<L2p>(0, -1, out2), "");
    }
    {
        bool em = false;
        const bool v = orb_visit<L2m, L2p>(-1, [&](const int*, int64_t) { em = true; });
        int t4[4] = { 0, 1, 0, 2 };
        int out4[4];
        report("2-,2+ n=-1: count/visit/rank/unrank all refuse",
               orb_cell_count<L2m, L2p>(-1) == ORB_OVERFLOW && !v && !em
               && orb_rank<L2m, L2p>(t4, -1) == ORB_OVERFLOW
               && !orb_unrank<L2m, L2p>(0, -1, out4), "");
    }
    {
        // n == 0 stays well-formed: zero cells, visit true, nothing emitted.
        bool em = false;
        const bool v = orb_visit<L2p>(0, [&](const int*, int64_t) { em = true; });
        report("2+ n=0 is empty but well-formed",
               orb_cell_count<L2p>(0) == 0 && v && !em, "");
    }

    std::printf("\n--- (g) group-character oracle: canon(g.t) = chi(g)*canon(t) ---\n");
    {
        int64_t pairs = 0;
        for (const Cfg& c : menu())
            check_chi_oracle(c, pairs);
        report("chi-oracle sweep total", pairs > 0,
               std::to_string(pairs) + " (g,t) pairs: full wreath group x all raw tuples, whole menu");
    }

    std::printf("\n--- (h)(i) storage layer: read/write path and the dual view ---\n");
    for (const Cfg& c : menu())
        for (int n : { 3, 4 }) {
            // One brute stream per (class, extent), shared by both sections:
            // (h) needs it as the independent rank, (i) as the leaf oracle.
            const std::vector<std::vector<int>> want = brute(c, n);
            check_read_write(c, n, want);
            std::string detail;
            const bool ok = c.skel(n, want, detail);
            report("skeleton      " + c.spec + " n=" + std::to_string(n), ok, detail);
        }

    std::printf("\n--- (i) hand-pinned peeling bounds, and skeleton edge cases ---\n");
    // '+' classes: every value of every axis extends (the all-equal completion
    // is always canonical), so every node's span IS the full trip count n - lo.
    pin_node<L2p>("2+", 4, {},     0, 4, "root: i in [0,4), n - lo");
    pin_node<L2p>("2+", 4, { 1 },  1, 3, "leaf row j in [1,4), n - lo");
    pin_node<L3p>("3+", 4, { 1, 2 }, 2, 2, "leaf row k in [2,4), n - lo");
    // '-' classes: a leaf row is still the full trip count (the last axis is
    // never pinned and every iteration emits), but an INTERIOR span can be
    // truncated where the tail has no completion -- i = 3 has no partner.
    pin_node<L2m>("2-", 4, {},     0, 3, "root truncated: n - lo would be 4, i=3 has no partner");
    pin_node<L2m>("2-", 4, { 2 },  3, 1, "leaf row j in [3,4), n - lo");
    // Riemann shape: the second key's first axis is bounded by the first key's,
    // and its tail is truncated for the same reason.
    pin_node<L2m, L2p>("2-,2+", 4, { 0, 1 },    0, 3, "i2 >= i1 = 0, truncated: i2=3 has no partner");
    pin_node<L2m, L2p>("2-,2+", 4, { 0, 1, 0 }, 1, 3, "leaf row j2 in [1,4), n - lo");
    {
        // n == 0 and a strict level wider than the extent: zero cells, and the
        // skeleton is EMPTY rather than refused -- the same "well-formed empty"
        // verdict orb_visit gives (section (e)/(f)).
        std::vector<double> p(1, 0.0);
        orb_skeleton<double, L2p> s0;
        const bool b0 = s0.build(0, p.data());
        const bool e0 = b0 && s0.node_count() == 0 && s0.cells() == 0
                        && s0.arena_bytes() == 0 && s0.root() == nullptr;
        report("skeleton 2+ n=0 is empty, not refused", e0,
               b0 ? "built empty" : "build refused");

        orb_skeleton<double, L3m> s1;
        const bool b1 = s1.build(2, p.data());
        int probe[3] = { 0, 1, 2 };
        const bool e1 = b1 && s1.cells() == 0 && s1.navigate(probe) == nullptr;
        report("skeleton 3- n=2 is empty, navigate null", e1,
               b1 ? "built empty" : "build refused");

        // A negative extent gets the SAME verdict here as everywhere else.
        orb_skeleton<double, L2m, L2p> s2;
        const bool b2 = s2.build(-1, p.data());
        report("skeleton 2-,2+ n=-1 refused", !b2 && s2.node_count() == 0,
               b2 ? "build accepted a negative extent" : "refused, owns nothing");
    }
    {
        // Leak ledger.  Every skeleton built above has been destroyed by now
        // (all of them are scope-locals), so the arena new[]/delete[] totals
        // must balance exactly.  This is the -fsanitize=address substitute:
        // asan is unavailable on this toolchain (no libasan in mingw-w64
        // ucrt64), and for the specific claims -- ONE arena allocation per
        // build, every arena released -- counting is sharper than asan anyway.
        const bool ok = (g_new_arr == g_del_arr) && g_new_arr > 0;
        report("skeleton arena ledger balances", ok,
               std::to_string(g_new_arr) + " array new[], " + std::to_string(g_del_arr)
               + " delete[] (asan unavailable on this toolchain; counted instead)");
    }

    const auto t1 = std::chrono::steady_clock::now();
    const double secs = std::chrono::duration<double>(t1 - t0).count();
    std::printf("\n=== %d passed, %d failed  (%.1f s) ===\n", nPass, nFail, secs);
    return nFail > 0 ? 1 : 0;
}
