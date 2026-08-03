// linalg_probe_tests.cpp
// ============================================================================
// Standalone runtime tests for `blade_linalg::row_major_base` — the contiguity
// probe the dispatch layer uses to decide whether a rank-2 row skeleton can be
// handed to BLAS as a bare pool base with `ld = trailing extent`, or whether it
// must be staged through a copy.
//
// WHY THESE EXIST, AND WHY C++. `blade test linalg`'s other block asserts on
// EMITTED TEXT: that gram/matmul/dot/gemv reach `blade_linalg::` at all. It
// cannot see what the probe then decides at runtime, and neither can any value
// test — the probe's two arms (zero-copy and staged) are value-identical by
// construction, so a probe that accepts when it should refuse produces correct
// output right up until it reads past the end of a pool. The property under
// test is a C++ runtime invariant about pointer arithmetic, so it is tested in
// C++, against the SHIPPED headers, exactly like `blade test alloc`.
//
// WHY IT INCLUDES blade_linalg_views.hpp AND NOT blade_linalg.hpp. The probe
// and the staging views are pure pointer logic with no BLAS dependency, and
// they live in their own header for exactly this reason. `blade_linalg.hpp` is
// the cblas-calling half and `#error`s without `-DBLADE_HAS_BLAS` (Phase 5c:
// the Blade compiler emits native loops rather than calls when BLAS is
// unavailable, so an include of it without the define means an emit and a
// compile disagreed). Including the views header keeps these tests runnable on
// a machine with no BLAS at all, while leaving that guarantee untouched.
//
// THE DEFECT THESE PIN (docs/plan-cpp-perf-exploitation.md, Phase 5d). The
// original probe tested row-major GEOMETRY only — `rows[i] == base + i*ld` for
// every row. Row pointers give row STARTS and never row LENGTHS, so a pool
// SHORTER than the m x ld window can satisfy that test. The n = 2
// packed-symmetric skeleton is exactly such a pool: left-justified upper
// triangular storage puts row 0 (2 cells) at pool+0 and row 1 (1 cell) at
// pool+2 inside a 3-cell pool, which is bit-for-bit the same row-start pattern
// a DENSE 2x2 (4 cells) has. The geometry accepted it; a BLAS call with
// m = ld = 2 would then read pool[3], one cell past the pool. n >= 3 packed
// pools fail the geometry unaided (n = 3 puts row 2 at pool+5, not pool+6), so
// n = 2 was the sole degenerate size — which is what kept it latent.
//
// The fix is the `pool_cells` capacity argument: the emitter supplies each
// operand's allocated leaf count and the probe refuses when `m*ld` exceeds it.
// The cases below therefore pin BOTH halves — that the packed n = 2 skeleton is
// refused, AND (case `..._geometry_alone_accepts`) that it is the CAPACITY
// doing the refusing, so this file cannot rot into a vacuous pass if the packed
// layout ever changes out from under it.
//
// NOT TESTED HERE, DELIBERATELY: constructing an `in_view` over the packed n = 2
// skeleton. The staging fallback reads `rows[i][k]` for k < ld, so it reads
// pool[3] too — the adapters' rectangular-rows precondition (see the LAYOUT
// CONTRACT in blade_linalg_views.hpp) is upstream of the probe and is enforced
// by the typechecker, not here. Reading out of bounds to prove we read out of
// bounds is not a test.
//
// OUTPUT CONTRACT: one line per check as "  [PASS]: <name>" or "  [FAIL]:
// <name>", then a final "LINALG PROBE TESTS: <p>/<n> passed". Exit code 0 iff
// all pass. The harness (tests/LinAlgTests.fs) parses the final line and the
// exit code.
// ============================================================================

#include <cstdio>
#include <cstddef>
#include "nested_array_utilities.hpp"
#include "blade_linalg_views.hpp"

using namespace nested_array_utilities;
using blade_linalg::row_major_base;

static int g_pass = 0, g_total = 0;
static void check(const char* name, bool ok) {
    g_total++;
    if (ok) { g_pass++; printf("  [PASS]: %s\n", name); }
    else    {           printf("  [FAIL]: %s\n", name); }
}

using T2 = promote<double, 2>::type;   // double**

int main() {
    // ------------------------------------------------------------------
    // Dense operands: the probe must still accept, and must still return
    // the pool base itself (the zero-copy path is the whole point).
    // ------------------------------------------------------------------
    {
        static const size_t ext[2] = {5, 3};
        T2 a = allocate<T2, nullptr>(ext);
        size_t cells = count_leaves<T2, nullptr>(ext);
        check("dense_5x3_cardinality", cells == 15);
        check("dense_5x3_probe_accepts",
              row_major_base(a, 5, 3, cells) == pool_base(a));
        // The emitter's conservative arm: an operand whose storage class it
        // cannot settle is emitted with cell count 0, which must refuse even
        // though the geometry is perfect.
        check("dense_5x3_unknown_capacity_refuses",
              row_major_base(a, 5, 3, 0) == nullptr);
        deallocate<T2, nullptr>(a, ext);
    }

    // The dense 2x2 — same row starts as the packed n = 2 pool below, one more
    // cell. This is the case the fix must NOT break, and the reason a capacity
    // bound (rather than some sharper geometric test) is the only way to tell
    // the two apart.
    {
        static const size_t ext[2] = {2, 2};
        T2 a = allocate<T2, nullptr>(ext);
        size_t cells = count_leaves<T2, nullptr>(ext);
        check("dense_2x2_cardinality", cells == 4);
        check("dense_2x2_rows_at_0_and_2",
              a[0] == pool_base(a) && a[1] == pool_base(a) + 2);
        check("dense_2x2_probe_accepts",
              row_major_base(a, 2, 2, cells) == pool_base(a));
        deallocate<T2, nullptr>(a, ext);
    }

    // ------------------------------------------------------------------
    // The degenerate case: n = 2 packed symmetric.
    // ------------------------------------------------------------------
    {
        static const size_t ext[2] = {2, 2};
        static constexpr const size_t symm[2] = {1, 1};
        T2 a = allocate<T2, symm>(ext);
        size_t cells = count_leaves<T2, symm>(ext);

        // The premises of the false accept, pinned so the regression below
        // cannot go vacuous: a 3-cell pool whose row starts are pool+0 and
        // pool+2 — indistinguishable from the dense 2x2 above.
        check("packed_n2_cardinality", cells == 3);
        check("packed_n2_rows_at_0_and_2",
              a[0] == pool_base(a) && a[1] == pool_base(a) + 2);

        // Pre-fix behaviour, reproduced by handing the probe the capacity a
        // DENSE 2x2 would have had: the geometry alone accepts. Nothing is
        // dereferenced through the returned pointer — that read is the bug.
        check("packed_n2_geometry_alone_accepts",
              row_major_base(a, 2, 2, 4) == pool_base(a));

        // THE REGRESSION. With the operand's real cell count, the probe must
        // refuse: the m x ld window is 4 cells and the pool holds 3.
        check("packed_n2_probe_refuses_at_true_capacity",
              row_major_base(a, 2, 2, cells) == nullptr);

        deallocate<T2, symm>(a, ext);
    }

    // n >= 3 packed pools were already refused by the geometry on its own;
    // keep that true independently of the capacity bound (capacity is passed
    // generously here, so only the geometry can be doing the work).
    {
        static const size_t ext[2] = {5, 5};
        static constexpr const size_t symm[2] = {1, 1};
        T2 a = allocate<T2, symm>(ext);
        check("packed_n5_cardinality", (count_leaves<T2, symm>(ext)) == 15);
        check("packed_n5_geometry_alone_refuses",
              row_major_base(a, 5, 5, 25) == nullptr);
        deallocate<T2, symm>(a, ext);
    }

    // n = 1 packed symmetric is a 1-cell pool and a 1x1 window: sound, and the
    // probe must not refuse it (m == 1 never enters the geometry loop, so only
    // the capacity bound is in play).
    {
        static const size_t ext[2] = {1, 1};
        static constexpr const size_t symm[2] = {1, 1};
        T2 a = allocate<T2, symm>(ext);
        size_t cells = count_leaves<T2, symm>(ext);
        check("packed_n1_cardinality", cells == 1);
        check("packed_n1_probe_accepts",
              row_major_base(a, 1, 1, cells) == pool_base(a));
        deallocate<T2, symm>(a, ext);
    }

    // ------------------------------------------------------------------
    // The views forward the capacity: a refused probe must fall to staging,
    // and staging must still reproduce the operand's values exactly.
    // ------------------------------------------------------------------
    {
        static const size_t ext[2] = {2, 3};
        T2 a = allocate<T2, nullptr>(ext);
        for (size_t i = 0; i < 2; i++)
            for (size_t k = 0; k < 3; k++) a[i][k] = double(10 * i + k);

        blade_linalg::in_view zero_copy(a, 2, 3, count_leaves<T2, nullptr>(ext));
        check("in_view_zero_copy_when_capacity_known",
              zero_copy.p == pool_base(a) && zero_copy.buf.empty());

        // Capacity 0 (unknown storage class) -> staged, not aliased, and
        // value-identical. Safe to stage here because the operand is dense;
        // see the file header on why the packed skeleton is not staged.
        blade_linalg::in_view staged(a, 2, 3, 0);
        bool same = staged.p != pool_base(a) && staged.buf.size() == 6;
        for (size_t i = 0; i < 2 && same; i++)
            for (size_t k = 0; k < 3 && same; k++)
                if (staged.p[i * 3 + k] != a[i][k]) same = false;
        check("in_view_stages_when_capacity_unknown", same);

        deallocate<T2, nullptr>(a, ext);
    }

    // A fresh dense OUTPUT pool must stay zero-copy (flush() becomes a no-op);
    // an out_view with an unknown capacity must stage and scatter back.
    {
        static const size_t ext[2] = {2, 2};
        T2 c = allocate<T2, nullptr>(ext);
        {
            blade_linalg::out_view ov(c, 2, 2, count_leaves<T2, nullptr>(ext));
            check("out_view_zero_copy_on_fresh_dense_pool",
                  ov.p == pool_base(c) && ov.rows == nullptr);
            ov.p[0] = 1.0; ov.p[1] = 2.0; ov.p[2] = 3.0; ov.p[3] = 4.0;
            ov.flush();
        }
        check("out_view_zero_copy_values_land",
              c[0][0] == 1.0 && c[0][1] == 2.0 && c[1][0] == 3.0 && c[1][1] == 4.0);
        {
            blade_linalg::out_view ov(c, 2, 2, 0);
            bool staged = ov.p != pool_base(c) && ov.rows == c;
            ov.p[0] = 5.0; ov.p[1] = 6.0; ov.p[2] = 7.0; ov.p[3] = 8.0;
            ov.flush();
            check("out_view_stages_and_flushes_when_capacity_unknown",
                  staged && c[0][0] == 5.0 && c[0][1] == 6.0
                         && c[1][0] == 7.0 && c[1][1] == 8.0);
        }
        deallocate<T2, nullptr>(c, ext);
    }

    printf("LINALG PROBE TESTS: %d/%d passed\n", g_pass, g_total);
    return (g_pass == g_total) ? 0 : 1;
}
