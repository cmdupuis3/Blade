// decompact_proto.cpp
// ============================================================================
// PROTOTYPE / VALIDATION HARNESS for arity>=3 decompact scatter.
//
// Purpose: pin down the index arithmetic for decompacting one component out
// of a SymIdx<3,n> / AntisymIdx<3,n> group BEFORE it goes into the F# emitter.
// Each scatter is checked against an independent brute-force dense reference
// built by enumerating the full logical index space and reading the source
// through its canonicalizing access function (fold + left-justify).
//
// This is throwaway: it is NOT on the fsproj build path. It exists to make the
// arithmetic sandbox-verifiable (the project's costliest bug class is guessed
// codegen index arithmetic).
//
// Decompact semantics (from formalism 14.2 / 14.3):
//   SymIdx<r,n>  storage A[a0][a1]...   logical canonical tuple (folded) is the
//                prefix sum: p_k = a0 + a1 + ... + a_k, with p_0<=p_1<=...
//   AntisymIdx<r,n> strict: p_k = (a0+...+a_k) + k, p_0<p_1<...
//
//   decompact(A, d) pulls the component at logical position `pos` (within the
//   group) out as a free dense Idx<n>. The result is:
//       left-remainder (Sym/Antisym arity pos) , Idx<n> , right-remainder
//   For arity 3 every remainder is arity<=2.
//
//   The DENSE SEMANTICS we must reproduce: decompact materializes the value at
//   each *logical* (i0,i1,i2) position. A symmetric source has the same value
//   at every permutation of a canonical tuple; an antisymmetric source has
//   sign = parity(permutation) and 0 on any repeated index. The peeled axis
//   becomes a genuine free axis [0,n); the remaining axes keep the group's
//   symmetry among themselves.
// ============================================================================

#include <cstdio>
#include <cstddef>
#include <array>
#include <vector>
#include <algorithm>
#include "nested_array_utilities.hpp"
#include "nested_array_types.hpp"

using namespace nested_array_utilities;

static int g_pass = 0, g_total = 0;
static void check(const char* name, bool ok) {
    g_total++;
    if (ok) { g_pass++; printf("PASS %s\n", name); }
    else    {           printf("FAIL %s\n", name); }
}

// ---------------------------------------------------------------------------
// Source access: read the canonical value for an arbitrary logical tuple of a
// rank-3 compact group, going through fold + left-justify (the formalism's
// two-phase access). This is the GROUND TRUTH for what the source holds.
//   sym:  value at any permutation of (p<=q<=r) equals stored A[p][q-p][r-q]
//   anti: value at a permutation of strictly-increasing (p<q<r) is
//         sign(perm)*stored; 0 if any two indices equal.
// ---------------------------------------------------------------------------

static double src_sym3(double*** A, size_t i, size_t j, size_t k) {
    size_t t[3] = {i, j, k};
    std::sort(t, t + 3);
    size_t a = t[0], b = t[1] - t[0], c = t[2] - t[1];
    return A[a][b][c];
}

// parity of the permutation that sorts (i,j,k); returns +1/-1, or 0 if any
// two are equal (antisym is zero on repeats).
static int perm_sign3(size_t i, size_t j, size_t k) {
    if (i == j || j == k || i == k) return 0;
    int inv = 0;
    if (i > j) inv++;
    if (i > k) inv++;
    if (j > k) inv++;
    return (inv % 2 == 0) ? 1 : -1;
}

static double src_anti3(double*** A, size_t i, size_t j, size_t k) {
    int s = perm_sign3(i, j, k);
    if (s == 0) return 0.0;
    size_t t[3] = {i, j, k};
    std::sort(t, t + 3);
    // strict storage coords: a0=p, a1=q-p-1, a2=r-q-1
    size_t a = t[0], b = t[1] - t[0] - 1, c = t[2] - t[1] - 1;
    return s * A[a][b][c];
}

// ---------------------------------------------------------------------------
// Fill a rank-3 symmetric source with distinct canonical values, and a rank-3
// antisym source likewise. Storage walks are the left-justified shrinking
// bounds from formalism 14.2.
// ---------------------------------------------------------------------------

static double*** make_sym3(size_t n) {
    static constexpr const size_t symm[3] = {1,1,1};
    size_t ext[3] = {n,n,n};
    using T3 = promote<double,3>::type;
    T3 A = allocate<T3, symm>(ext);
    double v = 1.0;
    for (size_t a=0;a<n;a++) for(size_t b=0;b<n-a;b++) for(size_t c=0;c<n-a-b;c++) A[a][b][c]=v++;
    return A;
}

static double*** make_anti3(size_t n) {
    static constexpr const size_t aMask[3] = {1,1,1};
    size_t ext[3] = {n,n,n};
    using T3 = promote<double,3>::type;
    T3 A = allocate<T3, aMask, false>(ext);
    double v = 1.0;
    // strict shrinking bounds: a in [0,n), b in [0,n-a-1)... but length is
    // governed by the strict recurrence; walk by the same rule the allocator used.
    for (size_t a=0;a<n;a++)
      for (size_t b=0; a+b+1 < n; b++)
        for (size_t c=0; a+b+c+2 < n; c++)
            A[a][b][c]=v++;
    return A;
}

// ===========================================================================
// THE SCATTER UNDER TEST.
// For arity 3, decompact at position `pos` (0,1,2) produces a dense rank-3
// output out[x0][x1][x2] where the axis at `pos` is the freed dense Idx and
// the other two axes carry the (now independent) sub-symmetry.
//
// Candidate implementation: the output is simply the DENSE rank-3 tensor whose
// every logical entry is the canonical source value — i.e. decompact of a
// SOLE rank-3 group to a full dense cube, with the understanding that the
// caller's RESULT TYPE records which axis is free and which two are symmetric.
//
// That is the key realization to validate: for a sole-group decompact, the
// scatter that fills the entire dense cube via src_*3() is correct REGARDLESS
// of pos, because a fully dense materialization already contains every
// permutation. The `pos` and the sub-symmetry only matter for the result
// TYPE (storage of the remainder), not for a fully-dense output.
//
// But the actual codegen does NOT want a full dense cube — it wants the
// remainder kept compact (left-rem Sym/Antisym + dense freed axis). So the
// real target is a MIXED output: dense in the freed axis, triangular in the
// remaining pair. We validate THAT here.
// ===========================================================================

// Peel-LAST (pos=2): freed axis is the last logical coordinate. Remainder is a
// SymIdx<2> over the first two. Output shape: SymIdx<2,n> , Idx<n>  ==> storage
// out[a][b][x] with a in [0,n), b in [0,n-a), x in [0,n); logical (i,j,k) with
// i=a, j=a+b (canonical pair), k=x (free).
static bool test_sym3_peel_last(size_t n) {
    double*** A = make_sym3(n);
    static constexpr const size_t symm2[2] = {1,1};
    // output: outer Sym<2> of rows, each row a dense vector length n.
    // Represent as out[a][b] -> double[n]. Use mixed symm {1,1,2}.
    static constexpr const size_t symmMix[3] = {1,1,2};
    size_t ext[3] = {n,n,n};
    using T3 = promote<double,3>::type;
    T3 out = allocate<T3, symmMix>(ext);
    // scatter: for canonical pair (i<=j) in compact coords (a,b), and free k:
    for (size_t a=0;a<n;a++) for(size_t b=0;b<n-a;b++) {
        size_t i=a, j=a+b;
        for (size_t k=0;k<n;k++) out[a][b][k] = src_sym3(A, i, j, k);
    }
    // check against dense reference for every logical (i,j,k)
    bool ok = true;
    for (size_t i=0;i<n;i++) for(size_t j=0;j<n;j++) for(size_t k=0;k<n;k++) {
        // canonical (i,j) -> (a,b)
        size_t p = std::min(i,j), q = std::max(i,j);
        size_t a=p, b=q-p;
        double got = out[a][b][k];
        double want = src_sym3(A, i, j, k);
        if (got != want) { ok = false; }
    }
    return ok;
}

// Peel-FIRST (pos=0): freed axis is the first logical coordinate. Remainder is
// SymIdx<2> over the last two. Output: Idx<n> , SymIdx<2,n> ==> storage
// out[x][a][b], x free in [0,n), (a,b) compact for the pair (j<=k).
static bool test_sym3_peel_first(size_t n) {
    double*** A = make_sym3(n);
    static constexpr const size_t symmMix[3] = {2,1,1};
    size_t ext[3] = {n,n,n};
    using T3 = promote<double,3>::type;
    T3 out = allocate<T3, symmMix>(ext);
    for (size_t x=0;x<n;x++)
      for (size_t a=0;a<n;a++) for(size_t b=0;b<n-a;b++) {
        size_t j=a, k=a+b;
        out[x][a][b] = src_sym3(A, x, j, k);
      }
    bool ok = true;
    for (size_t i=0;i<n;i++) for(size_t j=0;j<n;j++) for(size_t k=0;k<n;k++) {
        size_t p=std::min(j,k), q=std::max(j,k);
        size_t a=p, b=q-p;
        double got = out[i][a][b];
        double want = src_sym3(A, i, j, k);
        if (got != want) ok = false;
    }
    return ok;
}

// ---------------------------------------------------------------------------
// Antisym peel-last, STRICT DENSE-SEMANTICS test.
//
// The decompact contract is: out, read densely, must equal the SOURCE's dense
// rank-3 antisym value at every logical (i,j,k). The candidate representation
// is "AntisymIdx<2> over the kept pair, Idx<n> freed". For peel-LAST that means
// out_dense(i,j,k) is reconstructed as:
//       sign_pair(i,j) * PAIRSTORE[canon_pair(i,j)][k]
// where PAIRSTORE[a][b][k] is filled from the canonical orientation. For this
// to be a faithful decompact, it must match src_anti3(A,i,j,k) for ALL (i,j,k),
// INCLUDING the i==j case (which must read 0) and both orientations of (i,j).
//
// The deep question: can a rank-2 antisym pair + free axis represent the rank-3
// antisym tensor's dense values? It can ONLY IF the rank-3 sign factors as
//   sign_perm3(i,j,k) == sign_pair(i,j)   (for the kept pair)
// times a k-independent canonical value. It does NOT: the rank-3 canonical
// value for a fixed (i,j) pair DEPENDS on where k sorts relative to i and j.
// This test will EXPOSE that if it is real.
// ---------------------------------------------------------------------------
static int sign2(size_t i, size_t j) {
    if (i == j) return 0;
    return (i < j) ? 1 : -1;
}

static bool test_anti3_peel_last(size_t n) {
    double*** A = make_anti3(n);
    static constexpr const size_t symmMix[3] = {1,1,2};
    size_t ext[3] = {n,n,n};
    using T3 = promote<double,3>::type;
    T3 out = allocate<T3, symmMix, false>(ext);
    // Fill PAIRSTORE from canonical orientation p<q of the kept pair, free k.
    for (size_t a=0;a<n;a++)
      for (size_t b=0; a+b+1<n; b++) {
        size_t p=a, q=a+b+1;
        for (size_t k=0;k<n;k++) out[a][b][k] = src_anti3(A, p, q, k);
      }
    // Now read densely via the pair-reconstruction and compare to the TRUE
    // rank-3 dense antisym value.
    bool ok = true;
    int firstFail = -1; size_t fi=0,fj=0,fk=0; double fgot=0,fwant=0;
    for (size_t i=0;i<n;i++) for(size_t j=0;j<n;j++) for(size_t k=0;k<n;k++) {
        double recon;
        if (i==j) recon = 0.0;
        else {
            size_t p=std::min(i,j), q=std::max(i,j);
            size_t a=p, b=q-p-1;
            recon = sign2(i,j) * out[a][b][k];
        }
        double want = src_anti3(A, i, j, k);   // TRUE rank-3 dense value
        if (recon != want) {
            ok = false;
            if (firstFail < 0) { firstFail=1; fi=i;fj=j;fk=k;fgot=recon;fwant=want; }
        }
    }
    if (!ok) printf("    [anti3_peel_last n=%zu] first mismatch (i,j,k)=(%zu,%zu,%zu) recon=%g true=%g\n",
                    n, fi,fj,fk, fgot, fwant);
    return ok;
}

// Antisym peel-first: freed axis = first logical coord, kept pair = (j,k).
static bool test_anti3_peel_first(size_t n) {
    double*** A = make_anti3(n);
    static constexpr const size_t symmMix[3] = {2,1,1};
    size_t ext[3] = {n,n,n};
    using T3 = promote<double,3>::type;
    T3 out = allocate<T3, symmMix, false>(ext);
    for (size_t x=0;x<n;x++)
      for (size_t a=0;a<n;a++)
        for (size_t b=0; a+b+1<n; b++) {
          size_t p=a, q=a+b+1;   // canonical kept pair (j<k)
          out[x][a][b] = src_anti3(A, x, p, q);
        }
    bool ok = true; int firstFail=-1; size_t fi=0,fj=0,fk=0; double fg=0,fw=0;
    for (size_t i=0;i<n;i++) for(size_t j=0;j<n;j++) for(size_t k=0;k<n;k++) {
        double recon;
        if (j==k) recon = 0.0;
        else {
            size_t p=std::min(j,k), q=std::max(j,k);
            size_t a=p, b=q-p-1;
            recon = sign2(j,k) * out[i][a][b];
        }
        double want = src_anti3(A, i, j, k);
        if (recon != want) { ok=false; if(firstFail<0){firstFail=1;fi=i;fj=j;fk=k;fg=recon;fw=want;} }
    }
    if (!ok) printf("    [anti3_peel_first n=%zu] (i,j,k)=(%zu,%zu,%zu) recon=%g true=%g\n",n,fi,fj,fk,fg,fw);
    return ok;
}

// Sym middle-peel: freed axis = middle, kept pair = (i,k) outer two.
static bool test_sym3_peel_mid(size_t n) {
    double*** A = make_sym3(n);
    static constexpr const size_t symmMix[3] = {1,2,1}; // i sym-with k, j free in middle
    size_t ext[3] = {n,n,n};
    using T3 = promote<double,3>::type;
    // storage: we keep pair (i,k) compact, j dense. Represent as out[a][x][b]
    // where (a,b) is the compact pair coord and x is the freed middle axis.
    // count_leaves with {1,2,1}: groups are {pos0,pos2} sym? No — adjacency!
    // SYMM groups are by EQUAL ADJACENT numbers. {1,2,1} = three singleton groups
    // (1),(2),(1) — NOT a pair, because the two 1s are not adjacent. So a compact
    // (i,k) pair with a free middle is NOT expressible as an adjacent-group mask.
    // This is the structural obstruction. Fall back to a DENSE output for the test
    // and just check we can reproduce dense values (the materializer would need
    // dense storage for middle-peel, losing the compaction benefit).
    // DENSE output uses the nullptr mask (true n^3 rectangular). {2,2,2} would
    // be a SINGLE rank-3 symmetric group (C(n+2,3) storage), not dense — using
    // it and indexing densely overruns. Dense decompact storage is nullptr.
    static const size_t* symmDense = nullptr;
    T3 out = allocate<T3, nullptr>(ext);
    for (size_t i=0;i<n;i++) for(size_t j=0;j<n;j++) for(size_t k=0;k<n;k++)
        out[i][j][k] = src_sym3(A, i, j, k);
    bool ok=true;
    for (size_t i=0;i<n;i++) for(size_t j=0;j<n;j++) for(size_t k=0;k<n;k++)
        if (out[i][j][k] != src_sym3(A,i,j,k)) ok=false;
    return ok;
}

// Antisym middle-peel: the case the v23 doc defers. Freed = middle j, kept pair
// = outer (i,k). Test whether sign2(i,k)*PAIRSTORE[canon(i,k)][j] reproduces the
// true rank-3 antisym dense value, INCLUDING when j sorts between i and k.
static bool test_anti3_peel_mid(size_t n) {
    double*** A = make_anti3(n);
    // Build a PAIRSTORE for the outer pair (i,k), free middle j. The kept pair
    // is antisym. Storage shape: dense j, strict pair (i,k). We cannot use an
    // adjacent {1,1} mask because the pair straddles the free axis, so store
    // dense and index canonically by hand into a flat helper.
    std::vector<double> store(n*n*n, 0.0);
    auto at=[&](size_t a,size_t b,size_t j)->double&{ return store[(a*n+b)*n+j]; };
    for (size_t p=0;p<n;p++) for(size_t q=p+1;q<n;q++) for(size_t j=0;j<n;j++)
        at(p,q,j) = src_anti3(A, p, j, q);   // canonical outer pair p<q, middle j
    bool ok=true; int firstFail=-1; size_t fi=0,fj=0,fk=0; double fg=0,fw=0;
    for (size_t i=0;i<n;i++) for(size_t j=0;j<n;j++) for(size_t k=0;k<n;k++) {
        double recon;
        if (i==k) recon = 0.0;
        else {
            size_t p=std::min(i,k), q=std::max(i,k);
            recon = sign2(i,k) * at(p,q,j);
        }
        double want = src_anti3(A, i, j, k);
        if (recon != want) { ok=false; if(firstFail<0){firstFail=1;fi=i;fj=j;fk=k;fg=recon;fw=want;} }
    }
    if (!ok) printf("    [anti3_peel_mid n=%zu] (i,j,k)=(%zu,%zu,%zu) recon=%g true=%g\n",n,fi,fj,fk,fg,fw);
    return ok;
}

int main() {
    for (size_t n=3;n<=6;n++) {
        char nm[64];
        snprintf(nm,64,"sym3_peel_last_n%zu",n);   check(nm, test_sym3_peel_last(n));
        snprintf(nm,64,"sym3_peel_first_n%zu",n);  check(nm, test_sym3_peel_first(n));
        snprintf(nm,64,"anti3_peel_last_n%zu",n);   check(nm, test_anti3_peel_last(n));
        snprintf(nm,64,"anti3_peel_first_n%zu",n);  check(nm, test_anti3_peel_first(n));
        snprintf(nm,64,"sym3_peel_mid_n%zu",n);     check(nm, test_sym3_peel_mid(n));
        snprintf(nm,64,"anti3_peel_mid_n%zu",n);    check(nm, test_anti3_peel_mid(n));
    }
    printf("DECOMPACT PROTO: %d/%d passed\n", g_pass, g_total);
    return (g_pass==g_total)?0:1;
}
