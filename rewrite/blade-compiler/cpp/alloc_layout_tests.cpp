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
// OUTPUT CONTRACT: prints one line per check as "PASS <name>" or "FAIL <name>",
// then a final "ALLOC TESTS: <p>/<n> passed". Exit code 0 iff all pass. The
// harness parses the final line and the exit code.
// ============================================================================

#include <cstdio>
#include <cstddef>
#include <vector>
#include "nested_array_utilities.hpp"
#include "nested_array_types.hpp"
#include "linearized_storage.hpp"

using namespace nested_array_utilities;

static int g_pass = 0;
static int g_total = 0;

static void check(const char* name, bool ok) {
    g_total++;
    if (ok) { g_pass++; printf("PASS %s\n", name); }
    else    {           printf("FAIL %s\n", name); }
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
    // Guards the rank>=3 correctness fix (old code over-counted: gave 10 for
    // rank-3 n=4 instead of C(4,3)=4). Contiguity checked at rank 2.
    {
        static const size_t ext[2] = {4, 4};
        using T2 = promote<double, 2>::type;
        size_t card = count_antisym<T2>(ext);
        check("antisym2_n4_cardinality", card == binom(4, 2) && card == 6);
        T2 a = allocate_antisym<T2>(ext);
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
        using T3 = promote<double, 3>::type;
        size_t card = count_antisym<T3>(ext);
        check("antisym3_n4_cardinality", card == binom(4, 3) && card == 4);
    }
    {
        static const size_t ext[3] = {5, 5, 5};
        using T3 = promote<double, 3>::type;
        size_t card = count_antisym<T3>(ext);
        check("antisym3_n5_cardinality", card == binom(5, 3) && card == 10);
    }
    {
        static const size_t ext[4] = {5, 5, 5, 5};
        using T4 = promote<double, 4>::type;
        size_t card = count_antisym<T4>(ext);
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

    printf("ALLOC TESTS: %d/%d passed\n", g_pass, g_total);
    return (g_pass == g_total) ? 0 : 1;
}
