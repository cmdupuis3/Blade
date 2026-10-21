// RAW-BIT tests of the rank-3 ROW-OWNED PULL form (blade_orbit_scatter.hpp
// leaf3_pull / pull3; round 3 item D-P3) over the SHIPPED header, driven by
// `blade test orbit-scatter` (tests/OrbitScatterTests.fs), built with
// -fopenmp -ffp-contract=off -fwrapv.
//
// For every case the SPECIFICATION is the logical walk
//     out = fold_out(so, [ fold_mid(sm, [ fold_leaf(sl, [ f(S(i, j, k)) for k ])
//                                         for j ]) for i ])
// over the canonicalized reads of a packed SymIdx<3, p> pool. Every leaf
// chain the walk computes (both orders (i, j) and (j, i), which must agree
// bit for bit -- the CSE the scatter relies on) is compared, BY BIT PATTERN
// (memcmp), with the packed H of
//   * leaf3       -- the serial slice form (the ordered scatter's), and
//   * leaf3_pull  -- the pull form at T = 1, 3 and 8 (team OBSERVED: exactly
//                    min(T, p) members with dynamic teams off),
// and the totals of scatter3 and pull3 with the walk's total, also by bit
// pattern. Cases: p in {1, 2, 3, 4, 5, 7, 8, 9, 13, 23} (all-equal, two-equal
// and distinct coordinate triples; ragged group and column-block tails), row
// groups G in {1, 3, 4, 8} and column blocks B in {8, 64}; leaf / middle /
// outer kernels cycling the builtin `+` (vector row chains and scalar), the
// non-commutative `b - a * 0.5` and the order-sensitive max `b > a ? b : a`;
// cell functions x * x, x, |x| - 0.25; seeds +0 / -0 / 0.75 / -1.25; pools
// with -0.0 cells, repeated values and (one mode) NaN cells -- ONE NaN payload
// (the payload of a NaN that meets another NaN is outside the contract: g++
// may commute an IEEE add's operands, which keeps the value and not which
// payload survives); an Int64 pool with wrapping kernels; p = 171 (comoment3's
// extent) as a smoke case. Prints one [FAIL] line per mismatch and the summary
// "ORBIT PULL TESTS: X/Y passed" plus the observed teams.
#include "blade_orbit_scatter.hpp"

#include <cmath>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <vector>

using blade_scatter::tri_row;

static int g_total = 0;
static int g_passed = 0;
static int g_fail_lines = 0;
static int g_team_seen[3] = { 0, 0, 0 };     // observed team at T = 1, 3, 8
static bool g_team_ok = true;
static int g_forms_ok = 0;         // cases whose executed form matched
static int g_restricted_ok = 0;    // restricted-team cases (p >= 2) run serial-leaf3

static void check(bool ok, const char* what, size_t p, int G, int B, int mode, int t) {
    g_total++;
    if (ok) { g_passed++; return; }
    if (g_fail_lines++ < 40)
        std::printf("[FAIL] %s: p=%zu G=%d B=%d mode=%d T=%d\n", what, p, G, B, mode, t);
}

static size_t slice_start(size_t p, size_t i) {
    size_t acc = 0;
    for (size_t s = 0; s < i; s++) { const size_t m = p - s; acc += m * (m + 1) / 2; }
    return acc;
}

// the canonical cell (a <= b <= c): slice a, local (b - a, c - a)
static size_t canon_off(size_t p, size_t a, size_t b, size_t c) {
    if (a > b) std::swap(a, b);
    if (b > c) std::swap(b, c);
    if (a > b) std::swap(a, b);
    return slice_start(p, a) + tri_row(p - a, b - a) + (c - b);
}

template <class T>
static bool same_bits(const T& x, const T& y) { return std::memcmp(&x, &y, sizeof(T)) == 0; }

// The walk (the specification): its total, and its leaf chains into Hw
// (packed, from (a <= b)); `cse_ok` = every (j, i) leaf equals its (i, j) twin.
template <class T, class E, class F, class KL, class KM, class KO>
static T walk3(const E* S, size_t p, T sl, T sm, T so, const F& f, const KL& kl, const KM& km, const KO& ko,
               std::vector<T>& Hw, bool& cse_ok) {
    Hw.assign(p * (p + 1) / 2, sl);
    cse_ok = true;
    T out = so;
    for (size_t i = 0; i < p; i++) {
        T mid = sm;
        for (size_t j = 0; j < p; j++) {
            T leaf = sl;
            for (size_t k = 0; k < p; k++) leaf = kl(leaf, f(S[canon_off(p, i, j, k)]));
            if (i <= j) Hw[tri_row(p, i) + (j - i)] = leaf;
            else if (!same_bits(leaf, Hw[tri_row(p, j) + (i - j)])) cse_ok = false;
            mid = km(mid, leaf);
        }
        out = ko(out, mid);
    }
    return out;
}

static const double seeds[4] = { 0.0, -0.0, 0.75, -1.25 };

// the pool: mixed magnitudes and signs, exact repeats, -0.0 cells; mode 2
// adds NaN cells (one payload); mode 3 makes every cell -0.0
static std::vector<double> make_pool(size_t p, int mode) {
    const size_t cells = slice_start(p, p);
    std::vector<double> S(cells);
    volatile double zero = 0.0;
    const double qnan = zero / zero;
    for (size_t q = 0; q < cells; q++) {
        const uint64_t h = (uint64_t)(q * 2654435761u + 977u) % 1013u;
        double v = ((double)(h % 97) - 48.0) * 0.37 + 0.1 / (double)(q % 13 + 1);
        if (h % 5 == 0) v *= 1000.0;            // magnitudes 1e3 beside units
        if (h % 11 == 0) v = 1.5;               // exact repeats
        if (h % 17 == 0) v = -0.0;
        if (mode == 2 && h % 41 == 0) v = qnan;
        if (mode == 3) v = -0.0;
        S[q] = v;
    }
    return S;
}

// One configuration: kernel ids per level (0 = builtin +, 1 = b - a * 0.5,
// 2 = max), the cell function id, the seeds, the pool mode.
template <int G, int B>
static void run_case(size_t p, int kl_id, int km_id, int ko_id, int f_id, int si, int pool_mode, bool vector_add) {
    const std::vector<double> S = make_pool(p, pool_mode);
    auto kern = [](int id) {
        return [id](double a, double b) -> double {
            if (id == 0) return a + b;
            if (id == 1) return b - a * 0.5;
            return b > a ? b : a;
        };
    };
    auto kl = kern(kl_id);
    auto km = kern(km_id);
    auto ko = kern(ko_id);
    auto f = [f_id](double x) -> double {
        if (f_id == 0) return x * x;
        if (f_id == 1) return x;
        return std::fabs(x) - 0.25;
    };
    const double sl = pool_mode == 3 ? -0.0 : seeds[si % 4];
    const double sm = pool_mode == 3 ? -0.0 : seeds[(si + 1) % 4];
    const double so = pool_mode == 3 ? -0.0 : seeds[(si + 2) % 4];
    std::vector<double> Hw;
    bool cse_ok = false;
    const double wt = walk3(S.data(), p, sl, sm, so, f, kl, km, ko, Hw, cse_ok);
    check(cse_ok, "walk: leaf (j, i) == leaf (i, j)", p, G, B, pool_mode, 0);
    const size_t hc = p * (p + 1) / 2;
    // the serial slice form; VADD only for the builtin + (as the emitter)
    std::vector<double> Hs(hc, sl);
    double st;
    if (vector_add && kl_id == 0 && km_id == 0) {
        blade_scatter::leaf3<G, B, true>(S.data(), p, Hs.data(), f, kl);
        st = blade_scatter::scatter3<G, B, true, true>(S.data(), p, sl, sm, so, f, kl, km, ko);
    } else {
        blade_scatter::leaf3<G, B, false>(S.data(), p, Hs.data(), f, kl);
        st = blade_scatter::scatter3<G, B, false, false>(S.data(), p, sl, sm, so, f, kl, km, ko);
    }
    check(hc == 0 || std::memcmp(Hs.data(), Hw.data(), hc * sizeof(double)) == 0, "leaf3 H == walk leaves (bits)", p, G, B, pool_mode, 0);
    check(same_bits(st, wt), "scatter3 total == walk total (bits)", p, G, B, pool_mode, 0);
    const bool vadd = vector_add && kl_id == 0 && km_id == 0;
    // the leaf stage and the whole pull at a requested team, either variant
    auto do_leaf = [&](double* H, int T) -> blade_scatter::PullRun {
        auto obs = blade_scatter::no_observe{};
        return vadd ? blade_scatter::leaf3_pull<G, B, true>(S.data(), p, H, sl, f, kl, T, obs)
                    : blade_scatter::leaf3_pull<G, B, false>(S.data(), p, H, sl, f, kl, T, obs);
    };
    auto do_pull = [&](int T, blade_scatter::PullRun* run) -> double {
        return vadd ? blade_scatter::pull3<G, B, true, true>(S.data(), p, sl, sm, so, f, kl, km, ko, T, blade_scatter::no_observe{}, nullptr, run)
                    : blade_scatter::pull3<G, B, false, false>(S.data(), p, sl, sm, so, f, kl, km, ko, T, blade_scatter::no_observe{}, nullptr, run);
    };
    const int teams[3] = { 1, 3, 8 };
    for (int ti = 0; ti < 3; ti++) {
        const int T = teams[ti];
        std::vector<double> Hp(hc, 12345.0);     // overwritten: every chain seeded by its owner
        int seen_n[64] = { 0 };
        bool seen_t[64] = { false };
        auto obs = [&](int tn, int nt) { if (tn >= 0 && tn < 64) { seen_t[tn] = true; seen_n[tn] = nt; } };
        blade_scatter::PullRun lr, pr;
        double pt;
        if (vadd) {
            lr = blade_scatter::leaf3_pull<G, B, true>(S.data(), p, Hp.data(), sl, f, kl, T, obs);
            pt = blade_scatter::pull3<G, B, true, true>(S.data(), p, sl, sm, so, f, kl, km, ko, T, blade_scatter::no_observe{}, nullptr, &pr);
        } else {
            lr = blade_scatter::leaf3_pull<G, B, false>(S.data(), p, Hp.data(), sl, f, kl, T, obs);
            pt = blade_scatter::pull3<G, B, false, false>(S.data(), p, sl, sm, so, f, kl, km, ko, T, blade_scatter::no_observe{}, nullptr, &pr);
        }
        check(hc == 0 || std::memcmp(Hp.data(), Hw.data(), hc * sizeof(double)) == 0, "leaf3_pull H == walk leaves (bits)", p, G, B, pool_mode, T);
        check(same_bits(pt, wt), "pull3 total == walk total (bits)", p, G, B, pool_mode, T);
        int team = 0, members = 0;
        for (int t = 0; t < 64; t++) if (seen_t[t]) { members++; if (seen_n[t] > team) team = seen_n[t]; }
#ifdef _OPENMP
        const int want = (size_t)T < p ? T : (int)(p > 0 ? p : 1);
#else
        const int want = 1;
#endif
        if (p > 0) {
            const bool ok = team == want && members == want;
            check(ok, "observed team == min(T, p)", p, G, B, pool_mode, T);
            if (!ok) g_team_ok = false;
            if (p >= 8 && team > g_team_seen[ti]) g_team_seen[ti] = team;
            // the EXECUTED form: the pull on an actual team of two or more,
            // else the serial slice form (requested = min(T, p))
            const char* wantForm = want > 1 ? "row-pull" : "serial-leaf3";
            check(std::strcmp(lr.form, wantForm) == 0 && lr.actual == want && lr.requested == ((size_t)T < p ? T : (int)p),
                  "leaf3_pull executed form / requested / actual", p, G, B, pool_mode, T);
            check(std::strcmp(pr.form, wantForm) == 0 && pr.actual == want, "pull3 executed form / actual", p, G, B, pool_mode, T);
            if (std::strcmp(lr.form, wantForm) == 0) g_forms_ok++;
        }
    }
#ifdef _OPENMP
    // A RESTRICTED team: the request is 8, but an inactive nested region
    // (max-active-levels 1 inside a two-thread region) hands back a team of
    // ONE. The form is chosen on that ACTUAL team: the seeded serial leaf3,
    // the same bits, recorded as such.
    if (p > 0) {
        std::vector<double> Hr(hc, 12345.0);
        blade_scatter::PullRun rl, rp;
        double rt = 0.0;
        const int saved = omp_get_max_active_levels();
        omp_set_max_active_levels(1);
        #pragma omp parallel num_threads(2)
        {
            #pragma omp single
            {
                rl = do_leaf(Hr.data(), 8);
                rt = do_pull(8, &rp);
            }
        }
        omp_set_max_active_levels(saved);
        check(hc == 0 || std::memcmp(Hr.data(), Hw.data(), hc * sizeof(double)) == 0, "restricted team: leaf3_pull H == walk leaves (bits)", p, G, B, pool_mode, -1);
        check(same_bits(rt, wt), "restricted team: pull3 total == walk total (bits)", p, G, B, pool_mode, -1);
        const int req = p < 8 ? (int)p : 8;
        const bool ok = std::strcmp(rl.form, "serial-leaf3") == 0 && rl.actual == 1 && rl.requested == req
                        && std::strcmp(rp.form, "serial-leaf3") == 0 && rp.actual == 1;
        check(ok, "restricted team (requested 8, actual 1): executed form serial-leaf3", p, G, B, pool_mode, -1);
        if (ok && p >= 2) g_restricted_ok++;
    }
#endif
}
// Int64 pool, wrapping kernels (-fwrapv): the same identities.
template <int G, int B>
static void run_int_case(size_t p) {
    const size_t cells = slice_start(p, p);
    std::vector<int64_t> S(cells);
    for (size_t q = 0; q < cells; q++) S[q] = (int64_t)((q * 2654435761u) % 2003u) - 1001 + (q % 7 == 0 ? (int64_t)1 << 61 : 0);
    auto kl = [](int64_t a, int64_t b) -> int64_t { return a * 3 + b; };     // non-commutative, wraps
    auto km = [](int64_t a, int64_t b) -> int64_t { return b - a; };
    auto ko = [](int64_t a, int64_t b) -> int64_t { return a + b * 5; };
    auto f = [](int64_t x) -> int64_t { return x * x + 1; };
    std::vector<int64_t> Hw;
    bool cse_ok = false;
    const int64_t wt = walk3(S.data(), p, (int64_t)7, (int64_t)-3, (int64_t)11, f, kl, km, ko, Hw, cse_ok);
    check(cse_ok, "int64 walk: leaf (j, i) == leaf (i, j)", p, G, B, 9, 0);
    const size_t hc = p * (p + 1) / 2;
    for (int T : { 1, 3, 8 }) {
        std::vector<int64_t> Hp(hc, 0);
        blade_scatter::leaf3_pull<G, B, false>(S.data(), p, Hp.data(), (int64_t)7, f, kl, T, blade_scatter::no_observe{});
        check(hc == 0 || std::memcmp(Hp.data(), Hw.data(), hc * sizeof(int64_t)) == 0, "int64 leaf3_pull H == walk leaves", p, G, B, 9, T);
        const int64_t pt = blade_scatter::pull3<G, B, false, false>(S.data(), p, (int64_t)7, (int64_t)-3, (int64_t)11, f, kl, km, ko, T, blade_scatter::no_observe{});
        check(pt == wt, "int64 pull3 total == walk total", p, G, B, 9, T);
    }
    const int64_t st = blade_scatter::scatter3<G, B, false, false>(S.data(), p, (int64_t)7, (int64_t)-3, (int64_t)11, f, kl, km, ko);
    check(st == wt, "int64 scatter3 total == walk total", p, G, B, 9, 0);
}

template <int G, int B>
static void run_shape(size_t p) {
    // (kl, km, ko, f, seed index, pool mode, vector add)
    struct Cfg { int kl, km, ko, f, si, mode; bool vadd; };
    static const Cfg cfgs[] = {
        { 0, 0, 0, 0, 0, 0, true },     // the bench checksum: + + +, x * x, +0 seeds, VADD rows
        { 0, 0, 0, 0, 0, 0, false },    // the same with scalar row chains (a `where repro` +)
        { 1, 0, 1, 1, 2, 0, false },    // non-commutative leaf / outer, x, 0.75 / -1.25 / +0
        { 2, 1, 0, 2, 1, 0, false },    // max leaf (order-sensitive), |x| - 0.25, -0 seeds
        { 0, 2, 1, 0, 3, 2, false },    // NaN cells (one payload): + leaf, max middle
        { 2, 2, 2, 1, 3, 2, false },    // NaN cells through max at every level (non-propagating)
        { 0, 0, 0, 1, 0, 3, true },     // every cell -0.0, -0.0 seeds: the total keeps its sign
        { 1, 1, 1, 0, 2, 0, false },    // b - a * 0.5 at every level
    };
    for (const Cfg& c : cfgs) run_case<G, B>(p, c.kl, c.km, c.ko, c.f, c.si, c.mode, c.vadd);
}

int main() {
#ifdef _OPENMP
    omp_set_dynamic(0);
#endif
    const size_t ps[] = { 1, 2, 3, 4, 5, 7, 8, 9, 13, 23 };
    for (size_t p : ps) {
        run_shape<1, 64>(p);
        run_shape<3, 8>(p);
        run_shape<4, 64>(p);
        run_shape<8, 8>(p);
        run_int_case<4, 64>(p);
        run_int_case<3, 8>(p);
    }
    // comoment3's extent, smoke: the bench shape and a non-commutative leaf
    run_case<4, 64>(171, 0, 0, 0, 0, 0, 0, true);
    run_case<4, 64>(171, 1, 0, 2, 2, 2, 0, false);
    // THE RUNTIME FORM CENSUS (blade_run_record.hpp note_form): a named site
    // records the form it executed with the requested and actual team, one
    // row per distinct execution, repeats counted.
    {
        const size_t p = 13;
        const std::vector<double> S = make_pool(p, 0);
        auto f = [](double x) { return x * x; };
        auto k = [](double a, double b) { return a + b; };
        const double one = blade_scatter::pull3<4, 64, true, true>(S.data(), p, 0.0, 0.0, 0.0, f, k, k, k, 1, blade_scatter::no_observe{}, "census-probe");
        const double three = blade_scatter::pull3<4, 64, true, true>(S.data(), p, 0.0, 0.0, 0.0, f, k, k, k, 3, blade_scatter::no_observe{}, "census-probe");
        const double three2 = blade_scatter::pull3<4, 64, true, true>(S.data(), p, 0.0, 0.0, 0.0, f, k, k, k, 3, blade_scatter::no_observe{}, "census-probe");
        int serial = -1, pulled = -1;
        for (int i = 0; i < blade_rr::n_form_events; i++) {
            const blade_rr::FormEvent& e = blade_rr::form_events[i];
            if (std::strcmp(e.site, "census-probe") != 0) continue;
            if (std::strcmp(e.form, "serial-leaf3") == 0 && e.requested == 1 && e.actual == 1) serial = (int)e.calls;
            if (std::strcmp(e.form, "row-pull") == 0 && e.requested == 3 && e.actual == 3) pulled = (int)e.calls;
        }
        g_total++;
#ifdef _OPENMP
        const bool ok = serial == 1 && pulled == 2 && same_bits(one, three) && same_bits(three, three2);
#else
        const bool ok = serial >= 1 && same_bits(one, three) && same_bits(three, three2);
#endif
        if (ok) g_passed++;
        else std::printf("[FAIL] form census: serial-leaf3 calls %d (want 1), row-pull calls %d (want 2), totals equal %d\n",
                         serial, pulled, (int)(same_bits(one, three) && same_bits(three, three2)));
        std::printf("form census: census-probe serial-leaf3 (requested 1, actual 1) x%d, row-pull (requested 3, actual 3) x%d\n", serial, pulled);
    }
    std::printf("teams observed (largest, p >= 8): T=1 -> %d, T=3 -> %d, T=8 -> %d%s\n",
                g_team_seen[0], g_team_seen[1], g_team_seen[2], g_team_ok ? "" : " (a team mismatch is listed above)");
    std::printf("executed forms: %d case-team runs matched; restricted team (requested 8, actual 1, p >= 2): %d cases ran serial-leaf3\n",
                g_forms_ok, g_restricted_ok);
    std::printf("ORBIT PULL TESTS: %d/%d passed\n", g_passed, g_total);
    return g_passed == g_total ? 0 : 1;
}
