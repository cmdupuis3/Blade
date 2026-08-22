// k5.cpp -- mirror-transpose block kernel for the Blade packed symmetric layout.
//
// Driver: full-domain mirror fold  total = sum_{(i,j) in [0,n)^2} f(S(i,j), S(j,i))
//   symmetric    : f = (+)  ->  oracle: 2 * (2*offDiagSum + diagSum)
//   antisymmetric: f = (*)  ->  oracle: -2 * sum of squares of stored cells
//
// Arms:
//   ref      naive double loop, canonicalizing reads (what Blade emits today), no licence
//   refL     same, with an explicit vectorization licence (#pragma omp simd reduction)
//   armA     BxB blocked, still canonicalizing reads per cell, no licence
//   armAL    same, licensed
//   armB<1>  BxB blocked over 4x4 tiles; off-diagonal tile loaded with 4 contiguous
//            vector loads and transposed IN REGISTERS to yield the mirrored orientation
//   armB<0>  identical loads/FLOPs, transpose elided -> isolates transpose cost
//
// Also: decompact (packed -> dense n*n) reference vs the same tile-transpose kernel,
// compared BITWISE (no arithmetic at all beyond exact negation).

#include <immintrin.h>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <cstdint>
#include <chrono>
#include <algorithm>

typedef unsigned long long u64;
typedef std::size_t sz;

static sz *RB  = 0;   // symmetric      row bases:  i*(2n-i+1)/2
static sz *RBA = 0;   // antisymmetric  row bases:  i*(2n-i-1)/2

static inline double hsum(__m256d v){
    __m128d lo = _mm256_castpd256_pd128(v);
    __m128d hi = _mm256_extractf128_pd(v, 1);
    lo = _mm_add_pd(lo, hi);
    __m128d h  = _mm_unpackhi_pd(lo, lo);
    lo = _mm_add_sd(lo, h);
    return _mm_cvtsd_f64(lo);
}

// AVX2 4x4 double transpose: 8 ops (4 unpack + 4 permute2f128)
#define T4(r0,r1,r2,r3, o0,o1,o2,o3) do {              \
    __m256d _a = _mm256_unpacklo_pd(r0, r1);           \
    __m256d _b = _mm256_unpackhi_pd(r0, r1);           \
    __m256d _c = _mm256_unpacklo_pd(r2, r3);           \
    __m256d _d = _mm256_unpackhi_pd(r2, r3);           \
    o0 = _mm256_permute2f128_pd(_a, _c, 0x20);         \
    o1 = _mm256_permute2f128_pd(_b, _d, 0x20);         \
    o2 = _mm256_permute2f128_pd(_a, _c, 0x31);         \
    o3 = _mm256_permute2f128_pd(_b, _d, 0x31);         \
} while(0)

// ---------------------------------------------------------------- SYMMETRIC

__attribute__((noinline))
double ref_sym(const double* P, sz n){
    double t = 0.0;
    for (sz i = 0; i < n; i++)
        for (sz j = 0; j < n; j++){
            sz a = (i <= j) ? RB[i] + (j - i) : RB[j] + (i - j);
            sz b = (j <= i) ? RB[j] + (i - j) : RB[i] + (j - i);
            t += P[a] + P[b];
        }
    return t;
}

__attribute__((noinline))
double refL_sym(const double* P, sz n){
    double t = 0.0;
    for (sz i = 0; i < n; i++){
        #pragma omp simd reduction(+:t)
        for (sz j = 0; j < n; j++){
            sz a = (i <= j) ? RB[i] + (j - i) : RB[j] + (i - j);
            sz b = (j <= i) ? RB[j] + (i - j) : RB[i] + (j - i);
            t += P[a] + P[b];
        }
    }
    return t;
}

__attribute__((noinline))
double armA_sym(const double* P, sz n, sz Bs){
    double t = 0.0;
    for (sz II = 0; II < n; II += Bs)
        for (sz JJ = 0; JJ < n; JJ += Bs){
            sz ie = std::min(II + Bs, n), je = std::min(JJ + Bs, n);
            for (sz i = II; i < ie; i++)
                for (sz j = JJ; j < je; j++){
                    sz a = (i <= j) ? RB[i] + (j - i) : RB[j] + (i - j);
                    sz b = (j <= i) ? RB[j] + (i - j) : RB[i] + (j - i);
                    t += P[a] + P[b];
                }
        }
    return t;
}

__attribute__((noinline))
double armAL_sym(const double* P, sz n, sz Bs){
    double t = 0.0;
    for (sz II = 0; II < n; II += Bs)
        for (sz JJ = 0; JJ < n; JJ += Bs){
            sz ie = std::min(II + Bs, n), je = std::min(JJ + Bs, n);
            for (sz i = II; i < ie; i++){
                #pragma omp simd reduction(+:t)
                for (sz j = JJ; j < je; j++){
                    sz a = (i <= j) ? RB[i] + (j - i) : RB[j] + (i - j);
                    sz b = (j <= i) ? RB[j] + (i - j) : RB[i] + (j - i);
                    t += P[a] + P[b];
                }
            }
        }
    return t;
}

template<int DOT> __attribute__((noinline))
double armB_sym(const double* P, sz n, sz Bs){
    const sz n4 = n & ~(sz)3;
    __m256d accA = _mm256_setzero_pd();
    __m256d accB = _mm256_setzero_pd();
    double scal = 0.0;

    for (sz II = 0; II < n4; II += Bs)
        for (sz JJ = II; JJ < n4; JJ += Bs){
            const sz ie = std::min(II + Bs, n4), je = std::min(JJ + Bs, n4);
            for (sz r = II; r < ie; r += 4){
                const sz cs = (JJ > r) ? JJ : r;
                for (sz c = cs; c < je; c += 4){
                    if (c == r){
                        // diagonal 4x4 tile: its own transpose, triangular. Peel scalar.
                        for (int a = 0; a < 4; a++)
                            for (int b = 0; b < 4; b++){
                                sz i = r + (sz)a, j = r + (sz)b;
                                sz id = (i <= j) ? RB[i] + (j - i) : RB[j] + (i - j);
                                scal += 2.0 * P[id];
                            }
                    } else {
                        // off-diagonal tile: 4 contiguous loads, no gather
                        __m256d m0 = _mm256_loadu_pd(P + RB[r+0] + (c - r - 0));
                        __m256d m1 = _mm256_loadu_pd(P + RB[r+1] + (c - r - 1));
                        __m256d m2 = _mm256_loadu_pd(P + RB[r+2] + (c - r - 2));
                        __m256d m3 = _mm256_loadu_pd(P + RB[r+3] + (c - r - 3));
                        __m256d s  = _mm256_add_pd(_mm256_add_pd(m0, m1),
                                                   _mm256_add_pd(m2, m3));
                        accA = _mm256_add_pd(accA, s);      // tile (r,c): f=(+) -> 2*M
                        if (DOT){
                            __m256d t0,t1,t2,t3;
                            T4(m0,m1,m2,m3, t0,t1,t2,t3);   // mirrored orientation
                            __m256d s2 = _mm256_add_pd(_mm256_add_pd(t0, t1),
                                                       _mm256_add_pd(t2, t3));
                            accB = _mm256_add_pd(accB, s2); // tile (c,r): f=(+) -> 2*M^T
                        } else {
                            accB = _mm256_add_pd(accB, s);
                        }
                    }
                }
            }
        }
    // ragged tail: rows >= n4 (all cols), and cols >= n4 (rows < n4)
    for (sz i = n4; i < n; i++)
        for (sz j = 0; j < n; j++){
            sz id = (i <= j) ? RB[i] + (j - i) : RB[j] + (i - j);
            scal += 2.0 * P[id];
        }
    for (sz i = 0; i < n4; i++)
        for (sz j = n4; j < n; j++){
            sz id = (i <= j) ? RB[i] + (j - i) : RB[j] + (i - j);
            scal += 2.0 * P[id];
        }
    return 2.0 * (hsum(accA) + hsum(accB)) + scal;
}

// ------------------------------------------------------------ ANTISYMMETRIC
// stored strictly upper; S(j,i) = -S(i,j) for i<j; S(i,i) = 0.

static inline double a_read(const double* P, sz i, sz j){
    if (i == j) return 0.0;
    if (i < j)  return  P[RBA[i] + (j - i - 1)];
    return             -P[RBA[j] + (i - j - 1)];
}

__attribute__((noinline))
double ref_anti(const double* P, sz n){
    double t = 0.0;
    for (sz i = 0; i < n; i++)
        for (sz j = 0; j < n; j++)
            t += a_read(P,i,j) * a_read(P,j,i);
    return t;
}

__attribute__((noinline))
double refL_anti(const double* P, sz n){
    double t = 0.0;
    for (sz i = 0; i < n; i++){
        #pragma omp simd reduction(+:t)
        for (sz j = 0; j < n; j++){
            double a = (i == j) ? 0.0 : (i < j ?  P[RBA[i] + (j - i - 1)]
                                              : -P[RBA[j] + (i - j - 1)]);
            double b = (j == i) ? 0.0 : (j < i ?  P[RBA[j] + (i - j - 1)]
                                              : -P[RBA[i] + (j - i - 1)]);
            t += a * b;
        }
    }
    return t;
}

__attribute__((noinline))
double armA_anti(const double* P, sz n, sz Bs){
    double t = 0.0;
    for (sz II = 0; II < n; II += Bs)
        for (sz JJ = 0; JJ < n; JJ += Bs){
            sz ie = std::min(II + Bs, n), je = std::min(JJ + Bs, n);
            for (sz i = II; i < ie; i++)
                for (sz j = JJ; j < je; j++)
                    t += a_read(P,i,j) * a_read(P,j,i);
        }
    return t;
}

__attribute__((noinline))
double armAL_anti(const double* P, sz n, sz Bs){
    double t = 0.0;
    for (sz II = 0; II < n; II += Bs)
        for (sz JJ = 0; JJ < n; JJ += Bs){
            sz ie = std::min(II + Bs, n), je = std::min(JJ + Bs, n);
            for (sz i = II; i < ie; i++){
                #pragma omp simd reduction(+:t)
                for (sz j = JJ; j < je; j++){
                    double a = (i == j) ? 0.0 : (i < j ?  P[RBA[i] + (j - i - 1)]
                                                      : -P[RBA[j] + (i - j - 1)]);
                    double b = (j == i) ? 0.0 : (j < i ?  P[RBA[j] + (i - j - 1)]
                                                      : -P[RBA[i] + (j - i - 1)]);
                    t += a * b;
                }
            }
        }
    return t;
}

template<int DOT> __attribute__((noinline))
double armB_anti(const double* P, sz n, sz Bs){
    const sz n4 = n & ~(sz)3;
    __m256d accA = _mm256_setzero_pd();
    __m256d accB = _mm256_setzero_pd();
    double scal = 0.0;

    for (sz II = 0; II < n4; II += Bs)
        for (sz JJ = II; JJ < n4; JJ += Bs){
            const sz ie = std::min(II + Bs, n4), je = std::min(JJ + Bs, n4);
            for (sz r = II; r < ie; r += 4){
                const sz cs = (JJ > r) ? JJ : r;
                for (sz c = cs; c < je; c += 4){
                    if (c == r){
                        for (int a = 0; a < 4; a++)
                            for (int b = 0; b < 4; b++){
                                if (a == b) continue;          // diagonal reads 0
                                sz i = r + (sz)a, j = r + (sz)b;
                                sz id = (i < j) ? RBA[i] + (j - i - 1)
                                                : RBA[j] + (i - j - 1);
                                double x = P[id];
                                scal -= x * x;                 // S(i,j)*S(j,i) = -x^2
                            }
                    } else {
                        __m256d m0 = _mm256_loadu_pd(P + RBA[r+0] + (c - r - 1));
                        __m256d m1 = _mm256_loadu_pd(P + RBA[r+1] + (c - r - 2));
                        __m256d m2 = _mm256_loadu_pd(P + RBA[r+2] + (c - r - 3));
                        __m256d m3 = _mm256_loadu_pd(P + RBA[r+3] + (c - r - 4));
                        accA = _mm256_fnmadd_pd(m0, m0, accA);
                        accA = _mm256_fnmadd_pd(m1, m1, accA);
                        accA = _mm256_fnmadd_pd(m2, m2, accA);
                        accA = _mm256_fnmadd_pd(m3, m3, accA);
                        if (DOT){
                            __m256d t0,t1,t2,t3;
                            T4(m0,m1,m2,m3, t0,t1,t2,t3);
                            accB = _mm256_fnmadd_pd(t0, t0, accB);
                            accB = _mm256_fnmadd_pd(t1, t1, accB);
                            accB = _mm256_fnmadd_pd(t2, t2, accB);
                            accB = _mm256_fnmadd_pd(t3, t3, accB);
                        } else {
                            accB = _mm256_fnmadd_pd(m0, m0, accB);
                            accB = _mm256_fnmadd_pd(m1, m1, accB);
                            accB = _mm256_fnmadd_pd(m2, m2, accB);
                            accB = _mm256_fnmadd_pd(m3, m3, accB);
                        }
                    }
                }
            }
        }
    for (sz i = n4; i < n; i++)
        for (sz j = 0; j < n; j++) scal += a_read(P,i,j) * a_read(P,j,i);
    for (sz i = 0; i < n4; i++)
        for (sz j = n4; j < n; j++) scal += a_read(P,i,j) * a_read(P,j,i);
    return hsum(accA) + hsum(accB) + scal;
}

// ------------------------------------------------------------- DECOMPACT
// packed -> dense row-major n*n.  Bitwise-comparable: no arithmetic but negation.

__attribute__((noinline))
void decomp_ref_sym(const double* P, sz n, double* D){
    for (sz i = 0; i < n; i++)
        for (sz j = 0; j < n; j++)
            D[i*n + j] = (i <= j) ? P[RB[i] + (j - i)] : P[RB[j] + (i - j)];
}

__attribute__((noinline))
void decomp_armB_sym(const double* P, sz n, double* D, sz Bs){
    const sz n4 = n & ~(sz)3;
    for (sz II = 0; II < n4; II += Bs)
        for (sz JJ = II; JJ < n4; JJ += Bs){
            const sz ie = std::min(II + Bs, n4), je = std::min(JJ + Bs, n4);
            for (sz r = II; r < ie; r += 4){
                const sz cs = (JJ > r) ? JJ : r;
                for (sz c = cs; c < je; c += 4){
                    if (c == r){
                        for (int a = 0; a < 4; a++)
                            for (int b = 0; b < 4; b++){
                                sz i = r+(sz)a, j = r+(sz)b;
                                D[i*n+j] = (i<=j) ? P[RB[i]+(j-i)] : P[RB[j]+(i-j)];
                            }
                    } else {
                        __m256d m0 = _mm256_loadu_pd(P + RB[r+0] + (c - r - 0));
                        __m256d m1 = _mm256_loadu_pd(P + RB[r+1] + (c - r - 1));
                        __m256d m2 = _mm256_loadu_pd(P + RB[r+2] + (c - r - 2));
                        __m256d m3 = _mm256_loadu_pd(P + RB[r+3] + (c - r - 3));
                        _mm256_storeu_pd(D + (r+0)*n + c, m0);
                        _mm256_storeu_pd(D + (r+1)*n + c, m1);
                        _mm256_storeu_pd(D + (r+2)*n + c, m2);
                        _mm256_storeu_pd(D + (r+3)*n + c, m3);
                        __m256d t0,t1,t2,t3; T4(m0,m1,m2,m3, t0,t1,t2,t3);
                        _mm256_storeu_pd(D + (c+0)*n + r, t0);
                        _mm256_storeu_pd(D + (c+1)*n + r, t1);
                        _mm256_storeu_pd(D + (c+2)*n + r, t2);
                        _mm256_storeu_pd(D + (c+3)*n + r, t3);
                    }
                }
            }
        }
    for (sz i = n4; i < n; i++)
        for (sz j = 0; j < n; j++)
            D[i*n+j] = (i<=j) ? P[RB[i]+(j-i)] : P[RB[j]+(i-j)];
    for (sz i = 0; i < n4; i++)
        for (sz j = n4; j < n; j++)
            D[i*n+j] = (i<=j) ? P[RB[i]+(j-i)] : P[RB[j]+(i-j)];
}

__attribute__((noinline))
void decomp_ref_anti(const double* P, sz n, double* D){
    for (sz i = 0; i < n; i++)
        for (sz j = 0; j < n; j++)
            D[i*n + j] = a_read(P, i, j);
}

__attribute__((noinline))
void decomp_armB_anti(const double* P, sz n, double* D, sz Bs){
    const sz n4 = n & ~(sz)3;
    const __m256d NEG = _mm256_set1_pd(-0.0);
    for (sz II = 0; II < n4; II += Bs)
        for (sz JJ = II; JJ < n4; JJ += Bs){
            const sz ie = std::min(II + Bs, n4), je = std::min(JJ + Bs, n4);
            for (sz r = II; r < ie; r += 4){
                const sz cs = (JJ > r) ? JJ : r;
                for (sz c = cs; c < je; c += 4){
                    if (c == r){
                        for (int a = 0; a < 4; a++)
                            for (int b = 0; b < 4; b++)
                                D[(r+(sz)a)*n + r+(sz)b] = a_read(P, r+(sz)a, r+(sz)b);
                    } else {
                        __m256d m0 = _mm256_loadu_pd(P + RBA[r+0] + (c - r - 1));
                        __m256d m1 = _mm256_loadu_pd(P + RBA[r+1] + (c - r - 2));
                        __m256d m2 = _mm256_loadu_pd(P + RBA[r+2] + (c - r - 3));
                        __m256d m3 = _mm256_loadu_pd(P + RBA[r+3] + (c - r - 4));
                        _mm256_storeu_pd(D + (r+0)*n + c, m0);
                        _mm256_storeu_pd(D + (r+1)*n + c, m1);
                        _mm256_storeu_pd(D + (r+2)*n + c, m2);
                        _mm256_storeu_pd(D + (r+3)*n + c, m3);
                        __m256d t0,t1,t2,t3; T4(m0,m1,m2,m3, t0,t1,t2,t3);
                        _mm256_storeu_pd(D + (c+0)*n + r, _mm256_xor_pd(t0, NEG));
                        _mm256_storeu_pd(D + (c+1)*n + r, _mm256_xor_pd(t1, NEG));
                        _mm256_storeu_pd(D + (c+2)*n + r, _mm256_xor_pd(t2, NEG));
                        _mm256_storeu_pd(D + (c+3)*n + r, _mm256_xor_pd(t3, NEG));
                    }
                }
            }
        }
    for (sz i = n4; i < n; i++)
        for (sz j = 0; j < n; j++) D[i*n+j] = a_read(P,i,j);
    for (sz i = 0; i < n4; i++)
        for (sz j = n4; j < n; j++) D[i*n+j] = a_read(P,i,j);
}

// ------------------------------------------------------------------- DRIVER

static u64 rngst = 88172645463325252ULL;
static inline double nextval(){
    rngst ^= rngst << 13; rngst ^= rngst >> 7; rngst ^= rngst << 17;
    return (double)((int)(rngst % 17) - 8);   // exact small integers in [-8,8]
}

typedef std::chrono::steady_clock clk;
static double secs(clk::time_point a, clk::time_point b){
    return std::chrono::duration<double>(b - a).count();
}

struct Res { const char* name; double val; double ns_cell; };

#define RUN(NM, EXPR) { double v_=0; double best_=1e30; \
    for(int q_=0;q_<reps;q_++){ clk::time_point a_=clk::now(); v_=(EXPR); \
      clk::time_point b_=clk::now(); double d_=secs(a_,b_); if(d_<best_) best_=d_; } \
    R[nr++] = Res{NM, v_, best_*1e9/(double)cells}; }

int main(int argc, char** argv){
    sz n     = (argc > 1) ? (sz)atoll(argv[1]) : 2003;
    int reps = (argc > 2) ? atoi(argv[2]) : 5;
    int dodec= (argc > 3) ? atoi(argv[3]) : 1;

    const sz cells = n * n;
    RB  = (sz*)malloc(n * sizeof(sz));
    RBA = (sz*)malloc(n * sizeof(sz));
    for (sz i = 0; i < n; i++){
        RB[i]  = i * (2*n - i + 1) / 2;
        RBA[i] = i * (2*n - i - 1) / 2;
    }
    const sz nsym = n*(n+1)/2, nanti = n*(n-1)/2;

    printf("n = %llu   cells = %llu   sym pool = %llu   anti pool = %llu   reps = %d\n",
           (unsigned long long)n, (unsigned long long)cells,
           (unsigned long long)nsym, (unsigned long long)nanti, reps);

    // ================= SYMMETRIC =================
    {
        double* P = (double*)malloc(nsym * sizeof(double));
        for (sz k = 0; k < nsym; k++) P[k] = nextval();

        double off = 0.0, dia = 0.0;
        for (sz i = 0; i < n; i++){
            dia += P[RB[i]];
            for (sz j = i+1; j < n; j++) off += P[RB[i] + (j - i)];
        }
        double oracle = 2.0 * (2.0*off + dia);

        Res R[8]; int nr = 0;
        RUN("ref",        ref_sym(P,n))
        RUN("refL",       refL_sym(P,n))
        RUN("armA B=16",  armA_sym(P,n,16))
        RUN("armAL B=16", armAL_sym(P,n,16))
        RUN("armB B=8",   (armB_sym<1>(P,n,8)))
        RUN("armB B=16",  (armB_sym<1>(P,n,16)))
        RUN("armB B=32",  (armB_sym<1>(P,n,32)))
        RUN("armB-noT16", (armB_sym<0>(P,n,16)))

        printf("\n== SYMMETRIC  f=(+)   oracle = %.1f\n", oracle);
        printf("%-12s %20s %10s %8s\n","arm","value","ns/cell","vs oracle");
        for (int k = 0; k < nr; k++)
            printf("%-12s %20.1f %10.3f %8s\n", R[k].name, R[k].val, R[k].ns_cell,
                   (R[k].val == oracle) ? "BITWISE" : "DIFF");
        free(P);
    }

    // ================= ANTISYMMETRIC =================
    {
        double* P = (double*)malloc(nanti * sizeof(double));
        for (sz k = 0; k < nanti; k++) P[k] = nextval();

        double ss = 0.0;
        for (sz k = 0; k < nanti; k++) ss += P[k]*P[k];
        double oracle = -2.0 * ss;

        Res R[8]; int nr = 0;
        RUN("ref",        ref_anti(P,n))
        RUN("refL",       refL_anti(P,n))
        RUN("armA B=16",  armA_anti(P,n,16))
        RUN("armAL B=16", armAL_anti(P,n,16))
        RUN("armB B=8",   (armB_anti<1>(P,n,8)))
        RUN("armB B=16",  (armB_anti<1>(P,n,16)))
        RUN("armB B=32",  (armB_anti<1>(P,n,32)))
        RUN("armB-noT16", (armB_anti<0>(P,n,16)))

        printf("\n== ANTISYMMETRIC  f=(*)   oracle = %.1f\n", oracle);
        printf("%-12s %20s %10s %8s\n","arm","value","ns/cell","vs oracle");
        for (int k = 0; k < nr; k++)
            printf("%-12s %20.1f %10.3f %8s\n", R[k].name, R[k].val, R[k].ns_cell,
                   (R[k].val == oracle) ? "BITWISE" : "DIFF");
        free(P);
    }

    // ================= DECOMPACT =================
    if (dodec && n <= 3000){
        double* Ps = (double*)malloc(nsym  * sizeof(double));
        double* Pa = (double*)malloc(nanti * sizeof(double));
        for (sz k = 0; k < nsym;  k++) Ps[k] = nextval();
        for (sz k = 0; k < nanti; k++) Pa[k] = nextval();
        double* D1 = (double*)malloc(cells * sizeof(double));
        double* D2 = (double*)malloc(cells * sizeof(double));
        printf("\n== DECOMPACT (packed -> dense %llux%llu)\n",
               (unsigned long long)n,(unsigned long long)n);
        printf("%-22s %10s %8s\n","arm","ns/cell","bitwise");
        double b;
        b=1e30; for(int q=0;q<reps;q++){clk::time_point a0=clk::now(); decomp_ref_sym(Ps,n,D1); clk::time_point a1=clk::now(); b=std::min(b,secs(a0,a1));}
        printf("%-22s %10.3f %8s\n","sym  ref", b*1e9/(double)cells, "-");
        b=1e30; for(int q=0;q<reps;q++){clk::time_point a0=clk::now(); decomp_armB_sym(Ps,n,D2,16); clk::time_point a1=clk::now(); b=std::min(b,secs(a0,a1));}
        printf("%-22s %10.3f %8s\n","sym  armB(transpose)", b*1e9/(double)cells,
               memcmp(D1,D2,cells*sizeof(double))==0 ? "OK" : "MISMATCH");
        b=1e30; for(int q=0;q<reps;q++){clk::time_point a0=clk::now(); decomp_ref_anti(Pa,n,D1); clk::time_point a1=clk::now(); b=std::min(b,secs(a0,a1));}
        printf("%-22s %10.3f %8s\n","anti ref", b*1e9/(double)cells, "-");
        b=1e30; for(int q=0;q<reps;q++){clk::time_point a0=clk::now(); decomp_armB_anti(Pa,n,D2,16); clk::time_point a1=clk::now(); b=std::min(b,secs(a0,a1));}
        printf("%-22s %10.3f %8s\n","anti armB(transpose)", b*1e9/(double)cells,
               memcmp(D1,D2,cells*sizeof(double))==0 ? "OK" : "MISMATCH");
        free(Ps); free(Pa); free(D1); free(D2);
    }
    free(RB); free(RBA);
    return 0;
}
