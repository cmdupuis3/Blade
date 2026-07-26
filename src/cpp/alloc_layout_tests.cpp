// alloc_layout_tests.cpp
// ============================================================================
// Standalone runtime-layout tests for the contiguous-backing allocate<>.
//
// WHY THESE EXIST: the Blade test harness checks computed VALUES (read back
// through arr[i][j][k]). Those pass identically whether the backing store is
// one contiguous pool or many piecewise allocations — so they cannot detect a
// layout regression. These tests check the layout invariants directly:
//
//   (1) CARDINALITY   — count_leaves matches the closed form per index type
//   (2) CONTIGUITY     — every leaf element lives in ONE pool, addresses
//                        strictly increasing in DFS order with no gaps/overlaps
//   (3) ROUND-TRIP     — values written in iteration order read back correctly
//
// These are the properties the CUDA streaming design depends on (a leaf span
// must be a contiguous, cudaMemcpy-able slice). Run via `blade test alloc`,
// which compiles and executes this file against the runtime headers.
//
// OUTPUT CONTRACT: prints one line per check as "  [PASS]: <name>" or
// "  [FAIL]: <name>", then a final "ALLOC TESTS: <p>/<n> passed". Exit code 0
// iff all pass. The
// harness parses the final line and the exit code.
// ============================================================================

#include <cstdio>
#include <cstddef>
#include <new>
#include <vector>
#include "nested_array_utilities.hpp"
#include "nested_array_types.hpp"
#include "linearized_storage.hpp"

using namespace nested_array_utilities;

// ---------------------------------------------------------------------------
// Array-allocation accounting (for the deallocate<> balance tests)
// ---------------------------------------------------------------------------
//
// Replacing the global array operators is legal here because this is a
// standalone binary. It gives the one property the teardown tests need and that
// values cannot show: that every heap block allocate<> made — the pool AND each
// interior pointer row, whose count depends on the per-level span formula — is
// handed back exactly once.
//
// Counting is TU-WIDE: any new[]/delete[] anywhere in the program moves these,
// including test scaffolding. So the assertions never read the counter as an
// absolute; each snapshots g_live_arrays immediately before its allocate and
// compares after the paired deallocate. g_total_arrays only ever grows, and is
// used to prove an allocation really happened (so a "no leak" verdict cannot be
// vacuously true).
//
// We delegate to the SINGLE-object operators (which we do not replace) rather
// than to malloc/free: that is precisely what the default operator new[] does,
// so the blocks stay in the heap the default would have used and no cross-
// library new[]/delete[] pairing can be mismatched. BOTH the sized and unsized
// operator delete[] must be replaced — for trivially-destructible element types
// g++ emits the C++14 SIZED form, so replacing only the unsized one would drop
// decrements and every balance test would report a phantom leak.
static size_t g_live_arrays = 0;
static size_t g_total_arrays = 0;

void* operator new[](std::size_t sz) {
    void* p = ::operator new(sz);   // throws std::bad_alloc on failure
    g_live_arrays++;
    g_total_arrays++;
    return p;
}
void operator delete[](void* p) noexcept {
    if (p) g_live_arrays--;
    ::operator delete(p);
}
void operator delete[](void* p, std::size_t) noexcept {
    if (p) g_live_arrays--;
    ::operator delete(p);
}

static int g_pass = 0;
static int g_total = 0;

static void check(const char* name, bool ok) {
    g_total++;
    if (ok) { g_pass++; printf("  [PASS]: %s\n", name); }
    else    {           printf("  [FAIL]: %s\n", name); }
}

// Closed-form binomial C(a, b) for cardinality expectations.
static size_t binom(size_t a, size_t b) {
    if (b > a) return 0;
    size_t r = 1;
    for (size_t i = 0; i < b; i++) { r = r * (a - i) / (i + 1); }
    return r;
}

// ---------------------------------------------------------------------------
// Contiguity walker: collect leaf-row (base,len) pairs in DFS iteration order
// and assert they tile a single pool with no gaps or overlaps. We compute the
// expected leaf rows directly from the index-type rule, then read the actual
// base addresses from the built array and check adjacency.
// ---------------------------------------------------------------------------

// Rectangular: rows are arr[i][j] for all i,j; each length extents[2].
static bool check_contiguous_rect3(double*** a, const size_t ext[3]) {
    double* prev = nullptr; size_t prevLen = 0; bool ok = true;
    for (size_t i = 0; i < ext[0]; i++)
        for (size_t j = 0; j < ext[1]; j++) {
            double* base = &a[i][j][0];
            size_t len = ext[2];
            if (prev && base != prev + prevLen) ok = false;
            prev = base; prevLen = len;
        }
    return ok;
}

// Symmetric {1,1,1}: rows arr[i][j], i in [0,n), j in [0,n-i), len = n-i-j.
static bool check_contiguous_sym3(double*** a, size_t n) {
    double* prev = nullptr; size_t prevLen = 0; bool ok = true;
    for (size_t i = 0; i < n; i++)
        for (size_t j = 0; j < n - i; j++) {
            double* base = &a[i][j][0];
            size_t len = n - i - j;
            if (prev && base != prev + prevLen) ok = false;
            prev = base; prevLen = len;
        }
    return ok;
}

// Mixed {1,1,2}: rows arr[i][j], i in [0,n), j in [0,n-i), len = n (free dim).
static bool check_contiguous_mixed3(double*** a, size_t n) {
    double* prev = nullptr; size_t prevLen = 0; bool ok = true;
    for (size_t i = 0; i < n; i++)
        for (size_t j = 0; j < n - i; j++) {
            double* base = &a[i][j][0];
            size_t len = n;
            if (prev && base != prev + prevLen) ok = false;
            prev = base; prevLen = len;
        }
    return ok;
}

int main() {
    // ----- Rectangular 3x3x3 -----
    {
        static const size_t ext[3] = {3, 3, 3};
        using T3 = promote<double, 3>::type;
        size_t card = count_leaves<T3, nullptr>(ext);
        check("rect3_cardinality", card == 27);
        T3 a = allocate<T3, nullptr>(ext);
        double c = 0;
        for (size_t i=0;i<3;i++) for(size_t j=0;j<3;j++) for(size_t k=0;k<3;k++) a[i][j][k]=c++;
        bool rt = true;
        c = 0;
        for (size_t i=0;i<3;i++) for(size_t j=0;j<3;j++) for(size_t k=0;k<3;k++) if(a[i][j][k]!=c++) rt=false;
        check("rect3_roundtrip", rt);
        check("rect3_contiguous", check_contiguous_rect3(a, ext));
    }

    // ----- Non-cube rectangular 2x3x4 -----
    {
        static const size_t ext[3] = {2, 3, 4};
        using T3 = promote<double, 3>::type;
        size_t card = count_leaves<T3, nullptr>(ext);
        check("rect_noncube_cardinality", card == 24);
        T3 a = allocate<T3, nullptr>(ext);
        check("rect_noncube_contiguous", check_contiguous_rect3(a, ext));
    }

    // ----- Symmetric {1,1,1} n=3 : C(5,3)=10 -----
    {
        static const size_t ext[3] = {3, 3, 3};
        static constexpr const size_t symm[3] = {1, 1, 1};
        using T3 = promote<double, 3>::type;
        size_t card = count_leaves<T3, symm>(ext);
        check("sym3_cardinality", card == binom(3+3-1, 3) && card == 10);
        T3 a = allocate<T3, symm>(ext);
        double c = 0;
        for (size_t i=0;i<3;i++) for(size_t j=0;j<3-i;j++) for(size_t k=0;k<3-i-j;k++) a[i][j][k]=c++;
        bool rt = true; c = 0;
        for (size_t i=0;i<3;i++) for(size_t j=0;j<3-i;j++) for(size_t k=0;k<3-i-j;k++) if(a[i][j][k]!=c++) rt=false;
        check("sym3_roundtrip", rt);
        check("sym3_contiguous", check_contiguous_sym3(a, 3));
    }

    // ----- Symmetric {1,1,1} n=5 : C(7,3)=35 (larger, exercises deeper shrink)
    {
        static const size_t ext[3] = {5, 5, 5};
        static constexpr const size_t symm[3] = {1, 1, 1};
        using T3 = promote<double, 3>::type;
        size_t card = count_leaves<T3, symm>(ext);
        check("sym3_n5_cardinality", card == binom(5+3-1, 3) && card == 35);
        T3 a = allocate<T3, symm>(ext);
        check("sym3_n5_contiguous", check_contiguous_sym3(a, 5));
    }

    // ----- Symmetric rank-2 {1,1} n=4 : C(5,2)=10 -----
    {
        static const size_t ext[2] = {4, 4};
        static constexpr const size_t symm[2] = {1, 1};
        using T2 = promote<double, 2>::type;
        size_t card = count_leaves<T2, symm>(ext);
        check("sym2_cardinality", card == binom(4+2-1, 2) && card == 10);
        T2 a = allocate<T2, symm>(ext);
        // rank-2 contiguity: rows arr[i], i in [0,n), len = n-i
        double* prev = nullptr; size_t prevLen = 0; bool ok = true;
        for (size_t i=0;i<4;i++) { double* base=&a[i][0]; size_t len=4-i;
            if (prev && base != prev+prevLen) ok=false; prev=base; prevLen=len; }
        check("sym2_contiguous", ok);
    }

    // ----- Symmetric rank-4 {1,1,1,1} n=3 : C(6,4)=15 (factorial-savings case)
    {
        static const size_t ext[4] = {3, 3, 3, 3};
        static constexpr const size_t symm[4] = {1, 1, 1, 1};
        using T4 = promote<double, 4>::type;
        size_t card = count_leaves<T4, symm>(ext);
        check("sym4_cardinality", card == binom(3+4-1, 4) && card == 15);
    }

    // ----- Mixed {1,1,2} n=3 : C(4,2)*3 = 18 -----
    {
        static const size_t ext[3] = {3, 3, 3};
        static constexpr const size_t symm[3] = {1, 1, 2};
        using T3 = promote<double, 3>::type;
        size_t card = count_leaves<T3, symm>(ext);
        check("mixed3_cardinality", card == binom(3+2-1, 2) * 3 && card == 18);
        T3 a = allocate<T3, symm>(ext);
        double c = 0;
        for (size_t i=0;i<3;i++) for(size_t j=0;j<3-i;j++) for(size_t k=0;k<3;k++) a[i][j][k]=c++;
        bool rt = true; c = 0;
        for (size_t i=0;i<3;i++) for(size_t j=0;j<3-i;j++) for(size_t k=0;k<3;k++) if(a[i][j][k]!=c++) rt=false;
        check("mixed3_roundtrip", rt);
        check("mixed3_contiguous", check_contiguous_mixed3(a, 3));
    }

    // ----- Antisymmetric: strict i<j<...<k, cardinality C(n,r) -----
    // Antisym is now the unified recurrence with an all-ones SYMM mask and
    // DIAGONALS=false (the former count_antisym/allocate_antisym were retired).
    // Guards the rank>=3 correctness (strict count gives C(n,r), not the
    // symmetric over-count). Contiguity checked at rank 2.
    {
        static const size_t ext[2] = {4, 4};
        static constexpr const size_t aMask2[2] = {1, 1};
        using T2 = promote<double, 2>::type;
        size_t card = count_leaves<T2, aMask2, false>(ext);
        check("antisym2_n4_cardinality", card == binom(4, 2) && card == 6);
        T2 a = allocate<T2, aMask2, false>(ext);
        // rows arr[i], i in [0,n), strict row length n-(i+1)
        double v = 0;
        for (size_t i=0;i<4;i++){ size_t len=4-(i+1); for(size_t j=0;j<len;j++) a[i][j]=v++; }
        bool rt = true; v = 0;
        for (size_t i=0;i<4;i++){ size_t len=4-(i+1); for(size_t j=0;j<len;j++) if(a[i][j]!=v++) rt=false; }
        check("antisym2_n4_roundtrip", rt);
        double* prev=nullptr; size_t pl=0; bool ok=true;
        for (size_t i=0;i<4;i++){ size_t len=4-(i+1); if(len==0) continue;
            double* base=&a[i][0]; if(prev && base!=prev+pl) ok=false; prev=base; pl=len; }
        check("antisym2_n4_contiguous", ok);
    }
    {
        static const size_t ext[3] = {4, 4, 4};
        static constexpr const size_t aMask3[3] = {1, 1, 1};
        using T3 = promote<double, 3>::type;
        size_t card = count_leaves<T3, aMask3, false>(ext);
        check("antisym3_n4_cardinality", card == binom(4, 3) && card == 4);
    }
    {
        static const size_t ext[3] = {5, 5, 5};
        static constexpr const size_t aMask3[3] = {1, 1, 1};
        using T3 = promote<double, 3>::type;
        size_t card = count_leaves<T3, aMask3, false>(ext);
        check("antisym3_n5_cardinality", card == binom(5, 3) && card == 10);
    }
    {
        static const size_t ext[4] = {5, 5, 5, 5};
        static constexpr const size_t aMask4[4] = {1, 1, 1, 1};
        using T4 = promote<double, 4>::type;
        size_t card = count_leaves<T4, aMask4, false>(ext);
        check("antisym4_n5_cardinality", card == binom(5, 4) && card == 5);
    }

    // ----- Array<T,N> wrapper path (the form codegen actually emits) -----
    {
        static const size_t ext[2] = {2, 4};
        using T2 = promote<double, 2>::type;
        Array<double, 2> m = { allocate<T2, nullptr>(ext), ext };
        for (size_t i=0;i<2;i++) for(size_t j=0;j<4;j++) m[i][j]=(double)(i*10+j);
        check("wrapper_indexing", m[1][3]==13 && m[0][0]==0 && m[1][0]==10);
        // wrapper-path contiguity: the flat block underlying m
        bool ok = (&m[1][0] == &m[0][0] + 4);
        check("wrapper_contiguous", ok);
    }

    // ----- Linearized storage: linearize/unlinearize bijection -----
    // Guards the flat device-oriented addressing scheme. Three properties:
    //   (a) symmetric linearize matches the nested allocator's DFS storage order
    //   (b) round-trip unlinearize . linearize == id (both symmetry classes)
    //   (c) antisymmetric cardinality and order match strict-tuple enumeration
    {
        using namespace linearized_storage;
        // (a) symmetric linearize == DFS order, r=3 n=4
        {
            size_t expected = 0; bool ok = true;
            for (size_t i=0;i<4;i++) for(size_t j=i;j<4;j++) for(size_t k=j;k<4;k++){
                std::array<size_t,3> t={i,j,k};
                if (symmetric::linearize<3>(t,4) != expected) ok=false;
                expected++;
            }
            check("lin_sym3_matches_dfs", ok && expected==20);
        }
        // (b) symmetric round-trip, a few ranks
        {
            bool ok=true;
            for(size_t i=0;i<8;i++)for(size_t j=i;j<8;j++)for(size_t k=j;k<8;k++){
                std::array<size_t,3> t={i,j,k};
                if(symmetric::unlinearize<3>(symmetric::linearize<3>(t,8),8)!=t) ok=false;
            }
            check("lin_sym3_roundtrip", ok);
        }
        {
            bool ok=true;
            for(size_t i=0;i<6;i++)for(size_t j=i;j<6;j++)for(size_t k=j;k<6;k++)for(size_t l=k;l<6;l++){
                std::array<size_t,4> t={i,j,k,l};
                if(symmetric::unlinearize<4>(symmetric::linearize<4>(t,6),6)!=t) ok=false;
            }
            check("lin_sym4_roundtrip", ok);
        }
        // (b') symmetric forward sweep linearize(unlinearize(tid))==tid
        {
            size_t card = symmetric::cardinality(16,3); bool ok=true;
            for(size_t tid=0; tid<card; tid++)
                if(symmetric::linearize<3>(symmetric::unlinearize<3>(tid,16),16)!=tid) ok=false;
            check("lin_sym3_forward_sweep", ok);
        }
        // (c) antisymmetric linearize == strict-DFS order + round-trip + cardinality
        {
            size_t expected=0; bool ok=true;
            for(size_t i=0;i<8;i++)for(size_t j=i+1;j<8;j++)for(size_t k=j+1;k<8;k++){
                std::array<size_t,3> t={i,j,k};
                if(antisymmetric::linearize<3>(t,8)!=expected) ok=false;
                if(antisymmetric::unlinearize<3>(expected,8)!=t) ok=false;
                expected++;
            }
            check("lin_anti3_dfs_roundtrip", ok && expected==antisymmetric::cardinality(8,3) && expected==56);
        }
        {
            size_t expected=0; bool ok=true;
            for(size_t i=0;i<10;i++)for(size_t j=i+1;j<10;j++)for(size_t k=j+1;k<10;k++)for(size_t l=k+1;l<10;l++){
                std::array<size_t,4> t={i,j,k,l};
                if(antisymmetric::linearize<4>(t,10)!=expected) ok=false;
                if(antisymmetric::unlinearize<4>(expected,10)!=t) ok=false;
                expected++;
            }
            check("lin_anti4_dfs_roundtrip", ok && expected==antisymmetric::cardinality(10,4) && expected==210);
        }
    }

    // pool_base: flat backing-pool extraction from a skeleton (CUDA streaming
    // forward/inverse transform primitive). The pool is contiguous in DFS order;
    // pool_base(skeleton) must reach pool+0 and the flat walk must match nested.
    {
        size_t e1[] = {5};
        auto a1 = nested_array_utilities::allocate<double*>(e1);
        for (size_t i = 0; i < 5; i++) a1[i] = (double)(i + 1);
        double* p1 = nested_array_utilities::pool_base(a1);
        bool ok = true;
        for (size_t i = 0; i < 5; i++) if (p1[i] != a1[i]) ok = false;
        check("pool_base_rank1_flat_matches_nested", ok);
    }
    {
        size_t e2[] = {3, 4};
        auto a2 = nested_array_utilities::allocate<double**>(e2);
        double v = 1.0;
        for (size_t i = 0; i < 3; i++) for (size_t j = 0; j < 4; j++) a2[i][j] = v++;
        double* p2 = nested_array_utilities::pool_base(a2);
        bool ok = true; size_t k = 0;
        for (size_t i = 0; i < 3; i++) for (size_t j = 0; j < 4; j++) if (p2[k++] != a2[i][j]) ok = false;
        // also: pool contiguous 1..12 in DFS order
        for (size_t i = 0; i < 12; i++) if (p2[i] != (double)(i + 1)) ok = false;
        check("pool_base_rank2_dfs_contiguous", ok);
    }

    // =======================================================================
    // Teardown: deallocate<> / deallocate_strict<> balance
    // =======================================================================
    //
    // (4) BALANCE — every block allocate<> made is freed exactly once.
    //
    // This is the property the layout invariants make non-obvious. The interior
    // pointer rows that exist are decided by the per-level span formula (each
    // level's bound depends on the seed threaded from its parent), so a teardown
    // that does not replay the SAME recurrence either misses rows (leak: counter
    // ends high) or invents them (heap abort). And the leaf T* rows are pool
    // slices, not blocks — the classic error is to free them, so the round-trips
    // below also write and read back the elements, then reallocate, which is
    // where an interior/double free shows up.
    //
    // Each block: snapshot -> allocate -> write+verify -> deallocate -> the
    // counter is back where it started.

    // ----- Dense rank 1: no skeleton at all (data IS the pool) -----
    {
        static const size_t ext[1] = {5};
        using T1 = promote<double, 1>::type;
        size_t snap = g_live_arrays;
        T1 a = allocate<T1, nullptr>(ext);
        bool rt = true;
        for (size_t i=0;i<5;i++) a[i] = (double)(i+1);
        for (size_t i=0;i<5;i++) if (a[i] != (double)(i+1)) rt = false;
        deallocate<T1, nullptr>(a, ext);
        check("dealloc_rank1_balanced", rt && g_live_arrays == snap);
    }

    // ----- Dense rank 2 -----
    {
        static const size_t ext[2] = {3, 4};
        using T2 = promote<double, 2>::type;
        size_t snap = g_live_arrays;
        T2 a = allocate<T2, nullptr>(ext);
        double c = 0; bool rt = true;
        for (size_t i=0;i<3;i++) for(size_t j=0;j<4;j++) a[i][j]=c++;
        c = 0;
        for (size_t i=0;i<3;i++) for(size_t j=0;j<4;j++) if(a[i][j]!=c++) rt=false;
        deallocate<T2, nullptr>(a, ext);
        check("dealloc_rank2_balanced", rt && g_live_arrays == snap);
    }

    // ----- Dense rank 3 (non-cube: two interior levels) -----
    {
        static const size_t ext[3] = {2, 3, 4};
        using T3 = promote<double, 3>::type;
        size_t snap = g_live_arrays;
        T3 a = allocate<T3, nullptr>(ext);
        double c = 0; bool rt = true;
        for (size_t i=0;i<2;i++) for(size_t j=0;j<3;j++) for(size_t k=0;k<4;k++) a[i][j][k]=c++;
        c = 0;
        for (size_t i=0;i<2;i++) for(size_t j=0;j<3;j++) for(size_t k=0;k<4;k++) if(a[i][j][k]!=c++) rt=false;
        deallocate<T3, nullptr>(a, ext);
        check("dealloc_rank3_balanced", rt && g_live_arrays == snap);
    }

    // ----- Dense rank 4 (three interior levels) -----
    {
        static const size_t ext[4] = {2, 2, 3, 3};
        using T4 = promote<double, 4>::type;
        size_t snap = g_live_arrays;
        T4 a = allocate<T4, nullptr>(ext);
        double c = 0; bool rt = true;
        for (size_t i=0;i<2;i++) for(size_t j=0;j<2;j++) for(size_t k=0;k<3;k++) for(size_t l=0;l<3;l++) a[i][j][k][l]=c++;
        c = 0;
        for (size_t i=0;i<2;i++) for(size_t j=0;j<2;j++) for(size_t k=0;k<3;k++) for(size_t l=0;l<3;l++) if(a[i][j][k][l]!=c++) rt=false;
        deallocate<T4, nullptr>(a, ext);
        check("dealloc_rank4_balanced", rt && g_live_arrays == snap);
    }

    // ----- Symmetric {1,1} n=4 (shrinking rows, diagonal kept) -----
    {
        static const size_t ext[2] = {4, 4};
        static constexpr const size_t symm[2] = {1, 1};
        using T2 = promote<double, 2>::type;
        size_t snap = g_live_arrays;
        T2 a = allocate<T2, symm>(ext);
        double c = 0; bool rt = true;
        for (size_t i=0;i<4;i++) for(size_t j=0;j<4-i;j++) a[i][j]=c++;
        c = 0;
        for (size_t i=0;i<4;i++) for(size_t j=0;j<4-i;j++) if(a[i][j]!=c++) rt=false;
        deallocate<T2, symm>(a, ext);
        check("dealloc_sym2_balanced", rt && g_live_arrays == snap);
    }

    // ----- Symmetric {1,1,1} n=3: the interior rows themselves shrink -----
    {
        static const size_t ext[3] = {3, 3, 3};
        static constexpr const size_t symm[3] = {1, 1, 1};
        using T3 = promote<double, 3>::type;
        size_t snap = g_live_arrays;
        T3 a = allocate<T3, symm>(ext);
        double c = 0; bool rt = true;
        for (size_t i=0;i<3;i++) for(size_t j=0;j<3-i;j++) for(size_t k=0;k<3-i-j;k++) a[i][j][k]=c++;
        c = 0;
        for (size_t i=0;i<3;i++) for(size_t j=0;j<3-i;j++) for(size_t k=0;k<3-i-j;k++) if(a[i][j][k]!=c++) rt=false;
        deallocate<T3, symm>(a, ext);
        check("dealloc_sym3_balanced", rt && g_live_arrays == snap);
    }

    // ----- Antisymmetric rank 2 (DIAGONALS=false: strict seed, shorter rows) --
    {
        static const size_t ext[2] = {4, 4};
        static constexpr const size_t aMask2[2] = {1, 1};
        using T2 = promote<double, 2>::type;
        size_t snap = g_live_arrays;
        T2 a = allocate<T2, aMask2, false>(ext);
        double v = 0; bool rt = true;
        for (size_t i=0;i<4;i++){ size_t len=4-(i+1); for(size_t j=0;j<len;j++) a[i][j]=v++; }
        v = 0;
        for (size_t i=0;i<4;i++){ size_t len=4-(i+1); for(size_t j=0;j<len;j++) if(a[i][j]!=v++) rt=false; }
        deallocate<T2, aMask2, false>(a, ext);
        check("dealloc_antisym2_balanced", rt && g_live_arrays == snap);
    }

    // ----- Antisymmetric rank 3 n=5: C(5,3)=10, and the interior level ends in
    // a ZERO-LENGTH row (i=4) — `new DTYPE[0]` is a real block, so the teardown
    // has to visit it too.
    {
        static const size_t ext[3] = {5, 5, 5};
        static constexpr const size_t aMask3[3] = {1, 1, 1};
        using T3 = promote<double, 3>::type;
        size_t snap = g_live_arrays;
        T3 a = allocate<T3, aMask3, false>(ext);
        double v = 0; bool rt = true;
        for (size_t i=0;i<5;i++) for(size_t j=0;j<5-(i+1);j++) for(size_t k=0;k<5-(i+j+2);k++) a[i][j][k]=v++;
        bool cardOk = (v == 10);
        v = 0;
        for (size_t i=0;i<5;i++) for(size_t j=0;j<5-(i+1);j++) for(size_t k=0;k<5-(i+j+2);k++) if(a[i][j][k]!=v++) rt=false;
        deallocate<T3, aMask3, false>(a, ext);
        check("dealloc_antisym3_balanced", rt && cardOk && g_live_arrays == snap);
    }

    // ----- Per-group strict: SYMM={1,2,2} STRICT={0,1,1} (the compact-residual
    // shape — dense freed axis, strict residual pair). total = 2*(3+2+1+0) = 12.
    {
        static const size_t ext[3] = {2, 4, 4};
        static constexpr const size_t symm[3]   = {1, 2, 2};
        static constexpr const size_t strict[3] = {0, 1, 1};
        using T3 = promote<double, 3>::type;
        size_t snap = g_live_arrays;
        size_t card = count_leaves_strict<T3, symm, strict>(ext);
        T3 a = allocate_strict<T3, symm, strict>(ext);
        double v = 0; bool rt = true;
        for (size_t i=0;i<2;i++) for(size_t j=0;j<4;j++) for(size_t k=0;k<4-(j+1);k++) a[i][j][k]=v++;
        bool cardOk = (card == 12 && v == 12);
        v = 0;
        for (size_t i=0;i<2;i++) for(size_t j=0;j<4;j++) for(size_t k=0;k<4-(j+1);k++) if(a[i][j][k]!=v++) rt=false;
        deallocate_strict<T3, symm, strict>(a, ext);
        check("dealloc_strict_mixed_balanced", rt && cardOk && g_live_arrays == snap);
    }

    // ----- Degenerate total==0: strict rank 3 with extent < rank (C(2,3)=0).
    // allocate still makes a 1-element sentinel pool, but with no leaves there is
    // no [0] spine to walk, so deallocate frees the skeleton rows and leaks that
    // one element BY DESIGN. Requirement: no crash, and the leak is exactly the
    // sentinel (not any skeleton row).
    {
        static const size_t ext[3] = {2, 2, 2};
        static constexpr const size_t aMask3[3] = {1, 1, 1};
        using T3 = promote<double, 3>::type;
        size_t snap = g_live_arrays;
        size_t tsnap = g_total_arrays;
        size_t card = count_leaves<T3, aMask3, false>(ext);
        T3 a = allocate<T3, aMask3, false>(ext);
        deallocate<T3, aMask3, false>(a, ext);
        size_t leaked = g_live_arrays - snap;
        // g_total_arrays proves blocks were really allocated, so "leaked <= 1"
        // cannot pass vacuously.
        check("dealloc_degenerate_zero_total", card == 0 && g_total_arrays > tsnap && leaked <= 1);
    }

    // ----- Aliasing guard: allocate, free, allocate the SAME shape again. If the
    // teardown had freed the leaf rows (pool slices), the heap would be corrupt
    // and the second skeleton would alias live blocks; a full write/read sweep of
    // the reused array is what catches it.
    {
        static const size_t ext[2] = {4, 5};
        using T2 = promote<double, 2>::type;
        size_t snap = g_live_arrays;
        T2 a = allocate<T2, nullptr>(ext);
        for (size_t i=0;i<4;i++) for(size_t j=0;j<5;j++) a[i][j]=(double)(i*5+j);
        deallocate<T2, nullptr>(a, ext);
        T2 b = allocate<T2, nullptr>(ext);
        bool rt = true;
        for (size_t i=0;i<4;i++) for(size_t j=0;j<5;j++) b[i][j]=(double)(100+i*5+j);
        for (size_t i=0;i<4;i++) for(size_t j=0;j<5;j++) if(b[i][j]!=(double)(100+i*5+j)) rt=false;
        // the reused pool must still be one contiguous span
        bool contig = (&b[1][0] == &b[0][0] + 5) && (&b[3][0] == &b[0][0] + 15);
        deallocate<T2, nullptr>(b, ext);
        check("dealloc_realloc_heap_intact", rt && contig && g_live_arrays == snap);
    }

    printf("ALLOC TESTS: %d/%d passed\n", g_pass, g_total);
    return (g_pass == g_total) ? 0 : 1;
}
