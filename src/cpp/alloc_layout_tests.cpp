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
// absolute; each snapshots the live count immediately before its allocate and
// compares after the paired deallocate. The total only ever grows, and is used
// to prove an allocation really happened (so a "no leak" verdict cannot be
// vacuously true).
//
// Tests read both counters through live_arrays() / total_arrays() below, NEVER
// through these variables directly — see the note there.
//
// We delegate to the SINGLE-object operators (which we do not replace) rather
// than to malloc/free: that is precisely what the default operator new[] does,
// so the blocks stay in the heap the default would have used and no cross-
// library new[]/delete[] pairing can be mismatched. BOTH the sized and unsized
// operator delete[] must be replaced — for trivially-destructible element types
// g++ emits the C++14 SIZED form, so replacing only the unsized one would drop
// decrements and every balance test would report a phantom leak.
//
// That deliberate array-to-object delegation is also why building this file
// with -Wall raises -Wmismatched-new-delete ("operator delete called on pointer
// returned from operator new[]") wherever the optimizer can inline a whole
// new[]/delete[] pair into one place — currently the ragged teardowns, whose
// call graph is a straight line. It is a false positive about the scaffolding,
// not about the runtime under test. The harness compiles with plain
// `-std=c++17 -O2` (tests/AllocTests.fs), so the gate never sees it.
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

// READ THE COUNTERS ONLY THROUGH THESE. A call through a volatile function
// pointer is opaque: the optimizer cannot inline it, cannot prove what it
// touches, and therefore cannot move allocation traffic across it.
//
// That matters because GCC treats `operator new[]` / `operator delete[]` as
// malloc-like — free to sink, hoist, coalesce, or omit the call entirely
// (C++14 grants the omission explicitly, as a carve-out from the as-if rule).
// Sound for the DEFAULT operators; not for these replaced ones, whose counter
// updates are the whole point. Reading `g_live_arrays` directly, the increments
// for three `new size_t[...]` in a row were observed AFTER a later statement
// that should have followed them: at -O2 the block-boundary balance still came
// out right (which is why the dense tests above never needed this) but every
// "+N still live" assertion read short. The ragged/compound tests below must
// assert exactly that — "the teardown freed the owned blocks and left the
// borrowed ones alive" is not expressible as a net balance — so they pin the
// allocation traffic with these instead. Verified 56/56 at -O0/-O1/-O2/-O3;
// without them, -O1 and -O2 fail.
static size_t live_arrays_impl()  { return g_live_arrays; }
static size_t total_arrays_impl() { return g_total_arrays; }
static size_t (* volatile live_arrays_fp)()  = &live_arrays_impl;
static size_t (* volatile total_arrays_fp)() = &total_arrays_impl;
static size_t live_arrays()  { return live_arrays_fp(); }
static size_t total_arrays() { return total_arrays_fp(); }

static int g_pass = 0;
static int g_total = 0;

static void check(const char* name, bool ok) {
    g_total++;
    if (ok) { g_pass++; printf("  [PASS]: %s\n", name); }
    else    {           printf("  [FAIL]: %s\n", name); }
}

// ---------------------------------------------------------------------------
// compound_index_t destruction accounting (for the deallocate_compound tests)
// ---------------------------------------------------------------------------
//
// The array counters above cannot see the index: `new compound_index_t<R>(...)`
// is a SINGLE-object new, and its internal rank_to_tuple / tuple_to_rank / mask
// go through std::allocator, i.e. plain `::operator new` too. So the property
// "deallocate_compound really ran `delete idx`, and it dispatched to the derived
// destructor" needs its own witness.
//
// This subclass is that witness, and it is also the exact scenario the runtime
// relies on: the Compound wrapper holds a `compound_index_t<RANK>*`, teardown
// deletes through THAT static type, and the derived destructor must still run —
// which it does only because abstract_idx_t declares its destructor virtual. If
// that `virtual` were ever dropped, g_live_cidx would not return to its
// snapshot here (and the real index's tables would leak silently). Reclamation
// of the tables themselves is the ordinary member-destructor chain and is not
// separately observable without replacing global operator new, which is not
// worth the cross-library new/delete pairing hazard on this platform.
static size_t g_live_cidx = 0;

template<size_t R>
struct counted_cidx : compound_index_t<R> {
    counted_cidx(std::string n, std::array<size_t, R> e, std::vector<bool> m)
        : compound_index_t<R>(std::move(n), e, std::move(m)) { g_live_cidx++; }
    ~counted_cidx() override { g_live_cidx--; }
};

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
        size_t snap = live_arrays();
        T1 a = allocate<T1, nullptr>(ext);
        bool rt = true;
        for (size_t i=0;i<5;i++) a[i] = (double)(i+1);
        for (size_t i=0;i<5;i++) if (a[i] != (double)(i+1)) rt = false;
        deallocate<T1, nullptr>(a, ext);
        check("dealloc_rank1_balanced", rt && live_arrays() == snap);
    }

    // ----- Dense rank 2 -----
    {
        static const size_t ext[2] = {3, 4};
        using T2 = promote<double, 2>::type;
        size_t snap = live_arrays();
        T2 a = allocate<T2, nullptr>(ext);
        double c = 0; bool rt = true;
        for (size_t i=0;i<3;i++) for(size_t j=0;j<4;j++) a[i][j]=c++;
        c = 0;
        for (size_t i=0;i<3;i++) for(size_t j=0;j<4;j++) if(a[i][j]!=c++) rt=false;
        deallocate<T2, nullptr>(a, ext);
        check("dealloc_rank2_balanced", rt && live_arrays() == snap);
    }

    // ----- Dense rank 3 (non-cube: two interior levels) -----
    {
        static const size_t ext[3] = {2, 3, 4};
        using T3 = promote<double, 3>::type;
        size_t snap = live_arrays();
        T3 a = allocate<T3, nullptr>(ext);
        double c = 0; bool rt = true;
        for (size_t i=0;i<2;i++) for(size_t j=0;j<3;j++) for(size_t k=0;k<4;k++) a[i][j][k]=c++;
        c = 0;
        for (size_t i=0;i<2;i++) for(size_t j=0;j<3;j++) for(size_t k=0;k<4;k++) if(a[i][j][k]!=c++) rt=false;
        deallocate<T3, nullptr>(a, ext);
        check("dealloc_rank3_balanced", rt && live_arrays() == snap);
    }

    // ----- Dense rank 4 (three interior levels) -----
    {
        static const size_t ext[4] = {2, 2, 3, 3};
        using T4 = promote<double, 4>::type;
        size_t snap = live_arrays();
        T4 a = allocate<T4, nullptr>(ext);
        double c = 0; bool rt = true;
        for (size_t i=0;i<2;i++) for(size_t j=0;j<2;j++) for(size_t k=0;k<3;k++) for(size_t l=0;l<3;l++) a[i][j][k][l]=c++;
        c = 0;
        for (size_t i=0;i<2;i++) for(size_t j=0;j<2;j++) for(size_t k=0;k<3;k++) for(size_t l=0;l<3;l++) if(a[i][j][k][l]!=c++) rt=false;
        deallocate<T4, nullptr>(a, ext);
        check("dealloc_rank4_balanced", rt && live_arrays() == snap);
    }

    // ----- Symmetric {1,1} n=4 (shrinking rows, diagonal kept) -----
    {
        static const size_t ext[2] = {4, 4};
        static constexpr const size_t symm[2] = {1, 1};
        using T2 = promote<double, 2>::type;
        size_t snap = live_arrays();
        T2 a = allocate<T2, symm>(ext);
        double c = 0; bool rt = true;
        for (size_t i=0;i<4;i++) for(size_t j=0;j<4-i;j++) a[i][j]=c++;
        c = 0;
        for (size_t i=0;i<4;i++) for(size_t j=0;j<4-i;j++) if(a[i][j]!=c++) rt=false;
        deallocate<T2, symm>(a, ext);
        check("dealloc_sym2_balanced", rt && live_arrays() == snap);
    }

    // ----- Symmetric {1,1,1} n=3: the interior rows themselves shrink -----
    {
        static const size_t ext[3] = {3, 3, 3};
        static constexpr const size_t symm[3] = {1, 1, 1};
        using T3 = promote<double, 3>::type;
        size_t snap = live_arrays();
        T3 a = allocate<T3, symm>(ext);
        double c = 0; bool rt = true;
        for (size_t i=0;i<3;i++) for(size_t j=0;j<3-i;j++) for(size_t k=0;k<3-i-j;k++) a[i][j][k]=c++;
        c = 0;
        for (size_t i=0;i<3;i++) for(size_t j=0;j<3-i;j++) for(size_t k=0;k<3-i-j;k++) if(a[i][j][k]!=c++) rt=false;
        deallocate<T3, symm>(a, ext);
        check("dealloc_sym3_balanced", rt && live_arrays() == snap);
    }

    // ----- Antisymmetric rank 2 (DIAGONALS=false: strict seed, shorter rows) --
    {
        static const size_t ext[2] = {4, 4};
        static constexpr const size_t aMask2[2] = {1, 1};
        using T2 = promote<double, 2>::type;
        size_t snap = live_arrays();
        T2 a = allocate<T2, aMask2, false>(ext);
        double v = 0; bool rt = true;
        for (size_t i=0;i<4;i++){ size_t len=4-(i+1); for(size_t j=0;j<len;j++) a[i][j]=v++; }
        v = 0;
        for (size_t i=0;i<4;i++){ size_t len=4-(i+1); for(size_t j=0;j<len;j++) if(a[i][j]!=v++) rt=false; }
        deallocate<T2, aMask2, false>(a, ext);
        check("dealloc_antisym2_balanced", rt && live_arrays() == snap);
    }

    // ----- Antisymmetric rank 3 n=5: C(5,3)=10, and the interior level ends in
    // a ZERO-LENGTH row (i=4) — `new DTYPE[0]` is a real block, so the teardown
    // has to visit it too.
    {
        static const size_t ext[3] = {5, 5, 5};
        static constexpr const size_t aMask3[3] = {1, 1, 1};
        using T3 = promote<double, 3>::type;
        size_t snap = live_arrays();
        T3 a = allocate<T3, aMask3, false>(ext);
        double v = 0; bool rt = true;
        for (size_t i=0;i<5;i++) for(size_t j=0;j<5-(i+1);j++) for(size_t k=0;k<5-(i+j+2);k++) a[i][j][k]=v++;
        bool cardOk = (v == 10);
        v = 0;
        for (size_t i=0;i<5;i++) for(size_t j=0;j<5-(i+1);j++) for(size_t k=0;k<5-(i+j+2);k++) if(a[i][j][k]!=v++) rt=false;
        deallocate<T3, aMask3, false>(a, ext);
        check("dealloc_antisym3_balanced", rt && cardOk && live_arrays() == snap);
    }

    // ----- Per-group strict: SYMM={1,2,2} STRICT={0,1,1} (the compact-residual
    // shape — dense freed axis, strict residual pair). total = 2*(3+2+1+0) = 12.
    {
        static const size_t ext[3] = {2, 4, 4};
        static constexpr const size_t symm[3]   = {1, 2, 2};
        static constexpr const size_t strict[3] = {0, 1, 1};
        using T3 = promote<double, 3>::type;
        size_t snap = live_arrays();
        size_t card = count_leaves_strict<T3, symm, strict>(ext);
        T3 a = allocate_strict<T3, symm, strict>(ext);
        double v = 0; bool rt = true;
        for (size_t i=0;i<2;i++) for(size_t j=0;j<4;j++) for(size_t k=0;k<4-(j+1);k++) a[i][j][k]=v++;
        bool cardOk = (card == 12 && v == 12);
        v = 0;
        for (size_t i=0;i<2;i++) for(size_t j=0;j<4;j++) for(size_t k=0;k<4-(j+1);k++) if(a[i][j][k]!=v++) rt=false;
        deallocate_strict<T3, symm, strict>(a, ext);
        check("dealloc_strict_mixed_balanced", rt && cardOk && live_arrays() == snap);
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
        size_t snap = live_arrays();
        size_t tsnap = total_arrays();
        size_t card = count_leaves<T3, aMask3, false>(ext);
        T3 a = allocate<T3, aMask3, false>(ext);
        deallocate<T3, aMask3, false>(a, ext);
        size_t leaked = live_arrays() - snap;
        // total_arrays() proves blocks were really allocated, so "leaked <= 1"
        // cannot pass vacuously.
        check("dealloc_degenerate_zero_total", card == 0 && total_arrays() > tsnap && leaked <= 1);
    }

    // ----- Aliasing guard: allocate, free, allocate the SAME shape again. If the
    // teardown had freed the leaf rows (pool slices), the heap would be corrupt
    // and the second skeleton would alias live blocks; a full write/read sweep of
    // the reused array is what catches it.
    {
        static const size_t ext[2] = {4, 5};
        using T2 = promote<double, 2>::type;
        size_t snap = live_arrays();
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
        check("dealloc_realloc_heap_intact", rt && contig && live_arrays() == snap);
    }

    // =======================================================================
    // Teardown: ragged / compound (the NON-dense layouts)
    // =======================================================================
    //
    // (5) OWNERSHIP — each routine frees exactly the blocks its documented
    // producer allocated, and NOTHING it borrowed.
    //
    // The dense tests above could assert "the counter returns to its snapshot"
    // because allocate<> owns everything it touches. Here that assertion would
    // be actively WRONG for most shapes: a shape-preserving ragged map borrows
    // its input's lens/offsets/extents, a compound map borrows its input's
    // index, and every prefix view borrows the parent's data buffer. So each
    // block below allocates the borrowed part DELIBERATELY on the heap, frees
    // only the owned part, and asserts the counter lands on
    // `snapshot + <borrowed blocks>` — a plain return-to-snapshot would mean
    // the teardown over-freed. The borrowed part is then released explicitly to
    // bring the counter home, which also proves it was still a live block.

    // ----- Owning ragged storage: fresh pool + fresh row table over BORROWED
    // lens/offsets/extents (the shape-preserving elementwise-map output).
    {
        const size_t nrows = 4;
        const size_t rowLens[4] = {3, 1, 4, 2};
        size_t snap = live_arrays();
        // The three tables stand in for metadata the producer SHARES with its
        // input. On the heap here only so the counter can witness that the
        // storage teardown leaves them alone.
        size_t* lens = new size_t[nrows];
        size_t* offs = new size_t[nrows + 1];
        size_t* ext  = new size_t[1];
        ext[0] = nrows; offs[0] = 0;
        for (size_t i = 0; i < nrows; i++) { lens[i] = rowLens[i]; offs[i + 1] = offs[i] + rowLens[i]; }
        const size_t total = offs[nrows];

        double* pool  = new double[total];
        double** rows = new double*[nrows];
        for (size_t g = 0; g < nrows; g++) rows[g] = pool + offs[g];
        Ragged<double> r = { rows, ext, lens, offs };
        double v = 0; bool rt = true;
        for (size_t g = 0; g < nrows; g++) for (size_t k = 0; k < lens[g]; k++) r[g][k] = v++;
        v = 0;
        for (size_t g = 0; g < nrows; g++) for (size_t k = 0; k < r.lens[g]; k++) if (r[g][k] != v++) rt = false;
        bool rowsContig = (&r[1][0] == &r[0][0] + 3) && (&r[3][0] == &r[0][0] + 8);

        deallocate_ragged(r, pool);
        // Exactly the three borrowed tables survive, still readable and correct.
        bool kept = (live_arrays() == snap + 3);
        bool tablesIntact = (ext[0] == nrows && lens[2] == 4 && offs[nrows] == total && total == 10);
        check("dealloc_ragged_storage_shared_tables_kept",
              rt && rowsContig && kept && tablesIntact && r.data == nullptr);

        deallocate_ragged_tables(lens, offs, ext);
        check("dealloc_ragged_tables_balanced", live_arrays() == snap);
    }

    // ----- Ragged aliasing guard: free, then rebuild the SAME shape and sweep
    // it. If the teardown had freed the ROWS individually (they are pool
    // slices, not blocks) the heap would be corrupt and the second pool would
    // alias live storage — the write/read sweep is what surfaces it.
    {
        static const size_t lens2[3]  = {2, 3, 1};
        static const size_t offs2[4]  = {0, 2, 5, 6};
        static const size_t ext2[1]   = {3};
        size_t snap = live_arrays();
        for (int pass = 0; pass < 2; pass++) {
            double* pool  = new double[6];
            double** rows = new double*[3];
            for (size_t g = 0; g < 3; g++) rows[g] = pool + offs2[g];
            Ragged<double> r = { rows, ext2, lens2, offs2 };
            for (size_t g = 0; g < 3; g++) for (size_t k = 0; k < lens2[g]; k++) r[g][k] = (double)(pass * 100 + offs2[g] + k);
            bool ok = true;
            for (size_t g = 0; g < 3; g++) for (size_t k = 0; k < lens2[g]; k++)
                if (r[g][k] != (double)(pass * 100 + offs2[g] + k)) ok = false;
            if (!ok) { check("dealloc_ragged_realloc_heap_intact", false); break; }
            deallocate_ragged(r, pool);
            if (pass == 1) check("dealloc_ragged_realloc_heap_intact", live_arrays() == snap);
        }
    }

    // ----- Per-row-owned jagged rows (the group_by layout): each row its own
    // block, so the table alone is not enough. Includes a zero-length row,
    // which is a real `new double[0]` block the walk must still visit.
    {
        const size_t ng = 3;
        const size_t sz[3] = {2, 0, 5};
        size_t snap = live_arrays();
        double** rows = new double*[ng];
        for (size_t g = 0; g < ng; g++) rows[g] = new double[sz[g]];
        double v = 0; bool rt = true;
        for (size_t g = 0; g < ng; g++) for (size_t k = 0; k < sz[g]; k++) rows[g][k] = v++;
        v = 0;
        for (size_t g = 0; g < ng; g++) for (size_t k = 0; k < sz[g]; k++) if (rows[g][k] != v++) rt = false;
        bool allocated = (live_arrays() == snap + 4);   // table + 3 rows
        deallocate_ragged_rows_owned(rows, ng);
        check("dealloc_ragged_rows_owned_balanced", rt && allocated && live_arrays() == snap);
    }

    // ----- Owning compound: fresh compact buffer AND a freshly built index
    // (`compound(dense, mask)` / the provider compound read). Both must go,
    // and the index must go through its VIRTUAL destructor.
    //
    // Mask over a 3x4 grid, present cells (0,0) (0,3) (1,1) (1,2) (2,0), so
    // cardinality 5 and the lex-sorted rank order is exactly that listing.
    {
        std::array<size_t, 2> ext = {3, 4};
        std::vector<bool> mask(12, false);
        mask[0] = mask[3] = mask[5] = mask[6] = mask[8] = true;
        size_t snap = live_arrays();
        size_t csnap = g_live_cidx;
        auto* idx = new counted_cidx<2>("owning", ext, mask);
        bool cardOk = (idx->cardinality == 5);
        Compound<double, 2> c = { new double[idx->cardinality], idx, 1 };
        for (size_t r = 0; r < idx->cardinality; r++) c.data[r] = (double)(r + 1);
        bool rt = (c({0,0}) == 1 && c({0,3}) == 2 && c({1,1}) == 3 && c({1,2}) == 4 && c({2,0}) == 5);
        bool live = (g_live_cidx == csnap + 1);
        deallocate_compound(c);
        check("dealloc_compound_balanced",
              cardOk && rt && live && live_arrays() == snap && g_live_cidx == csnap
              && c.data == nullptr && c.idx == nullptr);
    }

    // ----- Compound map output: owns its buffer, SHARES the input's index.
    // Freeing the index here would leave the input holding a dangler, so the
    // counter must land on snapshot + the still-live input.
    {
        std::array<size_t, 2> ext = {3, 4};
        std::vector<bool> mask(12, false);
        mask[0] = mask[3] = mask[5] = mask[6] = mask[8] = true;
        size_t snap = live_arrays();
        size_t csnap = g_live_cidx;
        auto* idx = new counted_cidx<2>("shared", ext, mask);
        Compound<double, 2> in = { new double[idx->cardinality], idx, 1 };
        for (size_t r = 0; r < idx->cardinality; r++) in.data[r] = (double)(r + 1);
        // The map output: fresh buffer, borrowed idx + trailing_stride.
        Compound<double, 2> out = { new double[in.idx->cardinality * in.trailing_stride], in.idx, in.trailing_stride };
        for (size_t r = 0; r < out.idx->cardinality; r++) out.data[r] = in.data[r] * 10;
        bool rt = (out({1,2}) == 40 && out({2,0}) == 50);
        deallocate_compound_shared_index(out);
        // Input untouched: its buffer AND its index are still live.
        bool inputAlive = (live_arrays() == snap + 1) && (g_live_cidx == csnap + 1)
                          && in({1,1}) == 3 && in.idx->cardinality == 5;
        deallocate_compound(in);
        check("dealloc_compound_shared_index_keeps_input",
              rt && inputAlive && out.data == nullptr && out.idx == idx
              && live_arrays() == snap && g_live_cidx == csnap);
    }

    // ----- Gather compound (scattered pin, residual rank >= 2): the deep-copy
    // path, so the residual OWNS both its buffer and its sub-index and takes
    // the full deallocate_compound.
    //
    // Parent 2x3x2 fully present (cardinality 12, rank == i*6 + j*2 + k).
    // Pinning axis 1 to 1 is NOT a prefix, so the survivors are scattered.
    {
        std::array<size_t, 3> pext = {2, 3, 2};
        std::vector<bool> pmask(12, true);
        size_t snap = live_arrays();
        size_t csnap = g_live_cidx;
        auto* pidx = new counted_cidx<3>("parent", pext, pmask);
        Compound<double, 3> P = { new double[pidx->cardinality], pidx, 1 };
        for (size_t r = 0; r < pidx->cardinality; r++) P.data[r] = (double)r;
        auto R = make_partial_compound_gather<double, 3, 1>(
                     P, std::array<size_t, 1>{1}, std::array<size_t, 1>{1});
        // Free axes are 0 and 2; the four survivors are parent ranks 2,3,8,9.
        bool gathered = (R.idx->cardinality == 4)
                        && R({0,0}) == 2 && R({0,1}) == 3 && R({1,0}) == 8 && R({1,1}) == 9
                        && R.data != P.data;
        deallocate_compound(R);
        // Parent buffer untouched by the residual's teardown.
        bool parentIntact = (P.data[2] == 2 && P.data[9] == 9) && (live_arrays() == snap + 1);
        deallocate_compound(P);
        check("dealloc_compound_gather_balanced",
              gathered && parentIntact && live_arrays() == snap && g_live_cidx == csnap);
    }

    // ----- Views-only teardown, rank-1 prefix window: data is a SLICE of the
    // parent buffer; the single heap `size_t[1]` extent is the only thing the
    // view owns. Freeing `w.data` would be an interior free of the parent.
    // Prefix {1} selects parent ranks [2,4), i.e. cells (1,1) and (1,2).
    {
        std::array<size_t, 2> ext = {3, 4};
        std::vector<bool> mask(12, false);
        mask[0] = mask[3] = mask[5] = mask[6] = mask[8] = true;
        size_t snap = live_arrays();
        size_t csnap = g_live_cidx;
        auto* idx = new counted_cidx<2>("winparent", ext, mask);
        Compound<double, 2> B = { new double[idx->cardinality], idx, 1 };
        for (size_t r = 0; r < idx->cardinality; r++) B.data[r] = (double)(r + 1);

        size_t vsnap = live_arrays();                 // parent already live
        Array<double, 1> w = make_partial_window<double, 2, 1>(B, std::array<size_t, 1>{1});
        bool aliased = (w.data == B.data + 2) && (w.extents[0] == 2)
                       && w[0] == 3 && w[1] == 4
                       && (live_arrays() == vsnap + 1);   // ONLY the extent is fresh
        // Writing through the view must be visible in the parent (it is one buffer).
        w[0] = 33;
        bool writesThrough = (B({1,1}) == 33);
        deallocate_window_view(w);
        bool parentIntact = (live_arrays() == vsnap) && B({2,0}) == 5 && B({0,0}) == 1;
        deallocate_compound(B);
        check("dealloc_window_view_keeps_parent_data",
              aliased && writesThrough && parentIntact && w.extents == nullptr
              && live_arrays() == snap && g_live_cidx == csnap);
    }

    // ----- Views-only teardown, residual-compound window: shares the parent's
    // data, owns ONLY the freshly materialized sub-index. Prefix {1} on the
    // 2x3x2 parent gives the contiguous window starting at parent rank 6.
    {
        std::array<size_t, 3> pext = {2, 3, 2};
        std::vector<bool> pmask(12, true);
        size_t snap = live_arrays();
        size_t csnap = g_live_cidx;
        auto* pidx = new counted_cidx<3>("viewparent", pext, pmask);
        Compound<double, 3> P = { new double[pidx->cardinality], pidx, 1 };
        for (size_t r = 0; r < pidx->cardinality; r++) P.data[r] = (double)r;
        auto R = make_partial_compound<double, 3, 1>(P, std::array<size_t, 1>{1});
        bool shared = (R.data == P.data + 6) && (R.idx->cardinality == 6)
                      && R({0,0}) == 6 && R({2,1}) == 11
                      && (const void*)R.idx != (const void*)P.idx;   // distinct RANKs
        deallocate_compound_view(R);   // sub-index only — data belongs to P
        bool parentIntact = (P.data[6] == 6 && P.data[11] == 11) && (live_arrays() == snap + 1);
        deallocate_compound(P);
        check("dealloc_compound_view_keeps_parent_data",
              shared && parentIntact && R.idx == nullptr
              && live_arrays() == snap && g_live_cidx == csnap);
    }

    // ----- Trailing-dim window view: row table + 2-entry extents are fresh,
    // the ELEMENTS are the parent's contiguous block. Two owned blocks, zero
    // copied elements. Parent trailing_stride 3, prefix {1} -> 2 cells at
    // parent offset 2*3.
    {
        std::array<size_t, 2> ext = {3, 4};
        std::vector<bool> mask(12, false);
        mask[0] = mask[3] = mask[5] = mask[6] = mask[8] = true;
        size_t snap = live_arrays();
        size_t csnap = g_live_cidx;
        auto* idx = new counted_cidx<2>("trailparent", ext, mask);
        const size_t trail = 3;
        Compound<double, 2> B = { new double[idx->cardinality * trail], idx, trail };
        for (size_t r = 0; r < idx->cardinality * trail; r++) B.data[r] = (double)r;

        size_t vsnap = live_arrays();
        Array<double, 2> wt = make_partial_window_trail<double, 2, 1>(B, std::array<size_t, 1>{1});
        bool shape = (wt.extents[0] == 2 && wt.extents[1] == trail)
                     && (&wt[0][0] == B.data + 6) && (&wt[1][0] == B.data + 9)
                     && wt[0][0] == 6 && wt[1][2] == 11
                     && (live_arrays() == vsnap + 2);   // row table + extents only
        deallocate_window_trail_view(wt);
        bool parentIntact = (live_arrays() == vsnap) && B.data[6] == 6 && B.data[11] == 11;
        deallocate_compound(B);
        check("dealloc_window_trail_view_keeps_parent_data",
              shape && parentIntact && wt.data == nullptr && wt.extents == nullptr
              && live_arrays() == snap && g_live_cidx == csnap);
    }

    // ----- Scattered gather to a dense rank-1 residual: owns its copied buffer
    // AND its extent (two blocks). Pinning axis 1 to 0 is not a prefix; the
    // survivors are cells (0,0) and (2,0), parent ranks 0 and 4.
    {
        std::array<size_t, 2> ext = {3, 4};
        std::vector<bool> mask(12, false);
        mask[0] = mask[3] = mask[5] = mask[6] = mask[8] = true;
        size_t snap = live_arrays();
        size_t csnap = g_live_cidx;
        auto* idx = new counted_cidx<2>("gdparent", ext, mask);
        Compound<double, 2> B = { new double[idx->cardinality], idx, 1 };
        for (size_t r = 0; r < idx->cardinality; r++) B.data[r] = (double)(r + 1);

        size_t vsnap = live_arrays();
        Array<double, 1> gd = make_partial_gather_dense<double, 2, 1>(
                                  B, std::array<size_t, 1>{0}, std::array<size_t, 1>{1});
        bool copied = (gd.extents[0] == 2) && gd[0] == 1 && gd[1] == 5
                      && (gd.data != B.data) && (live_arrays() == vsnap + 2);
        gd[0] = 99;                                   // a copy: parent must not move
        bool isCopy = (B.data[0] == 1);
        deallocate_gather_dense(gd);
        bool parentIntact = (live_arrays() == vsnap) && B({2,0}) == 5;
        deallocate_compound(B);
        check("dealloc_gather_dense_balanced",
              copied && isCopy && parentIntact && gd.data == nullptr
              && live_arrays() == snap && g_live_cidx == csnap);
    }

    // ----- Scattered gather WITH a trailing dim: fresh pool + row table +
    // extents (three blocks). The pool is not handed back separately, so the
    // teardown recovers it as row 0 — the property this check pins.
    {
        std::array<size_t, 2> ext = {3, 4};
        std::vector<bool> mask(12, false);
        mask[0] = mask[3] = mask[5] = mask[6] = mask[8] = true;
        size_t snap = live_arrays();
        size_t csnap = g_live_cidx;
        auto* idx = new counted_cidx<2>("gdtparent", ext, mask);
        const size_t trail = 3;
        Compound<double, 2> B = { new double[idx->cardinality * trail], idx, trail };
        for (size_t r = 0; r < idx->cardinality * trail; r++) B.data[r] = (double)r;

        size_t vsnap = live_arrays();
        Array<double, 2> gt = make_partial_gather_dense_trail<double, 2, 1>(
                                  B, std::array<size_t, 1>{0}, std::array<size_t, 1>{1});
        // Survivors are parent ranks 0 and 4; their trailing blocks are
        // [0,1,2] and [12,13,14].
        bool copied = (gt.extents[0] == 2 && gt.extents[1] == trail)
                      && gt[0][0] == 0 && gt[0][2] == 2 && gt[1][0] == 12 && gt[1][2] == 14
                      && (&gt[1][0] == &gt[0][0] + trail)      // one pool, row 0 is its base
                      && (live_arrays() == vsnap + 3);         // pool + rows + extents
        deallocate_gather_dense_trail(gt);
        bool parentIntact = (live_arrays() == vsnap) && B.data[12] == 12;
        deallocate_compound(B);
        check("dealloc_gather_dense_trail_balanced",
              copied && parentIntact && gt.data == nullptr
              && live_arrays() == snap && g_live_cidx == csnap);
    }

    // ----- Degenerate EMPTY gather-with-trail: no survivor, so the producer's
    // 1-slot sentinel pool is unrecoverable (it writes no rows, leaving
    // `rows[0]` indeterminate) and is leaked BY DESIGN — the same trade
    // deallocate<> makes for its total == 0 sentinel. Requirement: no crash,
    // the row table and extents ARE freed, and the leak is that one block.
    //
    // Mask has only (0,0) present, so pinning axis 1 to 2 leaves nothing.
    {
        std::array<size_t, 2> ext = {3, 4};
        std::vector<bool> emptyMask(12, false);
        emptyMask[0] = true;
        size_t snap = live_arrays();
        size_t csnap = g_live_cidx;
        auto* eidx = new counted_cidx<2>("emptygather", ext, emptyMask);
        const size_t trail = 2;
        Compound<double, 2> E = { new double[eidx->cardinality * trail], eidx, trail };
        E.data[0] = 7; E.data[1] = 8;

        size_t esnap = live_arrays();
        Array<double, 2> gz = make_partial_gather_dense_trail<double, 2, 1>(
                                  E, std::array<size_t, 1>{2}, std::array<size_t, 1>{1});
        bool empty = (gz.extents[0] == 0) && (live_arrays() == esnap + 3);
        deallocate_gather_dense_trail(gz);
        size_t leaked = live_arrays() - esnap;    // the sentinel pool, and only it
        bool parentIntact = (E.data[0] == 7 && E.data[1] == 8);
        deallocate_compound(E);
        check("dealloc_gather_dense_trail_empty_sentinel",
              empty && parentIntact && leaked == 1
              && live_arrays() == snap + 1 && g_live_cidx == csnap);
    }

    printf("ALLOC TESTS: %d/%d passed\n", g_pass, g_total);
    return (g_pass == g_total) ? 0 : 1;
}
