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
//          orb_wreath_tests --specs            the menu's specs, one per line
//                                              (consumed by the F# cross-diff)
//
//  A spec is the level list innermost-first, matching the type's own spelling:
//  "2-,2+" is OrbIdx<[(2,-),(2,+)],n>, the Riemann shape.  The menu itself is
//  a generated CLOSURE (see the comment at menu()), not a curated list.
// =============================================================================

#include "orbit_wreath_utilities.hpp"

#include <algorithm>
#include <chrono>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <functional>
#include <set>
#include <string>
#include <utility>
#include <vector>

using namespace orbit_wreath_utilities;

// -----------------------------------------------------------------------------
// Type-erased handle on one instantiated class, so the menu can be a table.
// -----------------------------------------------------------------------------

using Visitor = std::function<void(const int*, int64_t)>;

struct Cfg {
    std::string spec;
    int         axes;
    int64_t   (*count)(int);
    bool      (*visit)(int, const Visitor&);
    int       (*canon)(const int*, int*);
    int64_t   (*rank)(const int*, int);
    bool      (*unrank)(int64_t, int, int*);
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
        &orb_unrank<Ls...>
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
// --dump
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

// -----------------------------------------------------------------------------

int main(int argc, char** argv) {
    if (argc >= 2 && std::strcmp(argv[1], "--dump") == 0) {
        if (argc < 4) {
            std::fprintf(stderr, "usage: OrbWreathTest --dump \"<spec>\" <n>\n");
            return 2;
        }
        return dump(argv[2], std::atoi(argv[3]));
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

    const auto t1 = std::chrono::steady_clock::now();
    const double secs = std::chrono::duration<double>(t1 - t0).count();
    std::printf("\n=== %d passed, %d failed  (%.1f s) ===\n", nPass, nFail, secs);
    return nFail > 0 ? 1 : 0;
}
