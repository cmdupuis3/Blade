/* compaction.c -- SIMD stream compaction (the Blade mask()/compound() WHERE idiom)
 *
 * Model of Blade's flagship filter:
 *     let qc_ok = mask(r_qc, lambda(q) -> q == 0)   // materialized boolean array
 *     let good  = compound(r_temp, qc_ok)           // compaction: this kernel
 *
 * so the input is a data array a[n] plus a MATERIALIZED canonical boolean mask
 * m[n] (bytes exactly 0 or 1), and the kernel writes the selected elements
 * contiguously to out[] and returns the count.
 *
 * ARMS (doubles, 64-bit):
 *   R  reference : for i: if (m[i]) out[k++] = a[i];      (what the emitter does)
 *   Rb           : same, with gcc if-conversion disabled (guaranteed-branchy)
 *   A  branchless: for i: out[k] = a[i]; k += m[i];       (always store)
 *   B  simd      : 4-wide, pext -> 4-bit mask -> 16-entry permute table
 *   Z  simd+skip : B plus a "whole vector unselected" store-skip branch
 *
 * ARMS (int32/float, 32-bit): R32 / A32 / C (8-wide, 256-entry table)
 *
 * Build: gcc -O3 -march=native -ffp-contract=fast -o compaction.exe compaction.c
 * Run  : ./compaction.exe [verify|bench|both] [N]
 */
#include <stdio.h>
#include <stdlib.h>
#include <stdint.h>
#include <string.h>
#include <time.h>
#include <math.h>
#include <immintrin.h>

/* ------------------------------------------------------------------ rng */
static uint64_t rs = 0x9E3779B97F4A7C15ull;
static inline uint64_t rnd64(void){ rs^=rs<<13; rs^=rs>>7; rs^=rs<<17; return rs; }
static inline double  rnd01(void){ return (double)(rnd64()>>11)*(1.0/9007199254740992.0); }
static void rseed(uint64_t s){ rs = s ? s : 0x9E3779B97F4A7C15ull; }

/* ------------------------------------------------------------- allocation */
static void* xalloc(size_t bytes){
#ifdef _WIN32
    void* p = _aligned_malloc(bytes, 64);
#else
    void* p = aligned_alloc(64, (bytes+63)&~(size_t)63);
#endif
    if(!p){ fprintf(stderr,"alloc %zu failed\n",bytes); exit(2);} return p;
}
static void xfree(void*p){
#ifdef _WIN32
    _aligned_free(p);
#else
    free(p);
#endif
}

/* ------------------------------------------------------------ mask makers */
static void gen_mask_random(uint8_t*m, size_t n, double p){
    if(p<=0.0){ memset(m,0,n); return; }
    if(p>=1.0){ memset(m,1,n); return; }
    for(size_t i=0;i<n;i++) m[i] = (rnd01()<p) ? 1u : 0u;
}
/* two-state Markov chain: geometric run lengths, mean Ls selected / Lu not,
 * with Ls+Lu ~ 128 so the branch predictor sees long runs. */
static void gen_mask_clustered(uint8_t*m, size_t n, double p){
    if(p<=0.0){ memset(m,0,n); return; }
    if(p>=1.0){ memset(m,1,n); return; }
    double Ls = 128.0*p;       if(Ls<1.5) Ls=1.5;
    double Lu = 128.0*(1.0-p); if(Lu<1.5) Lu=1.5;
    int s = (rnd01()<p)?1:0;
    for(size_t i=0;i<n;i++){
        m[i]=(uint8_t)s;
        double sw = s ? 1.0/Ls : 1.0/Lu;
        if(rnd01()<sw) s^=1;
    }
}

/* ================================================================== TABLES */
/* Doubles: _mm256_permute4x64_pd takes an IMMEDIATE control, so it cannot be
 * driven by a runtime table index.  The AVX2 way is to reinterpret the __m256d
 * as 8x int32 and use _mm256_permutevar8x32_epi32 with a VECTOR index, moving
 * each selected double as its two 32-bit halves.  16 entries x 32 B = 512 B. */
static __m256i TBLD[16];
/* 32-bit: 256 entries x 32 B = 8 KB (fits L1D=32KB, 128 lines). */
static __m256i TBLI[256];

static void build_tables(void){
    for(int mm=0; mm<16; ++mm){
        int32_t ix[8]; int p=0;
        for(int lane=0; lane<4; ++lane)
            if(mm & (1<<lane)){ ix[2*p]=2*lane; ix[2*p+1]=2*lane+1; ++p; }
        for(; p<4; ++p){ ix[2*p]=0; ix[2*p+1]=1; }        /* filler: lane 0 */
        TBLD[mm] = _mm256_loadu_si256((const __m256i*)ix);
    }
    for(int mm=0; mm<256; ++mm){
        int32_t ix[8]; int p=0;
        for(int lane=0; lane<8; ++lane) if(mm & (1<<lane)) ix[p++]=lane;
        for(; p<8; ++p) ix[p]=0;                           /* filler: lane 0 */
        TBLI[mm] = _mm256_loadu_si256((const __m256i*)ix);
    }
}

/* ============================================================ DOUBLE ARMS */
__attribute__((noinline))
static size_t cmp_ref(const double* restrict a, const uint8_t* restrict m,
                      size_t n, double* restrict out){
    size_t k=0;
    for(size_t i=0;i<n;i++) if(m[i]) out[k++]=a[i];
    return k;
}

__attribute__((noinline,optimize("no-if-conversion","no-if-conversion2","no-tree-loop-if-convert")))
static size_t cmp_ref_branchy(const double* restrict a, const uint8_t* restrict m,
                              size_t n, double* restrict out){
    size_t k=0;
    for(size_t i=0;i<n;i++) if(m[i]) out[k++]=a[i];
    return k;
}

__attribute__((noinline))
static size_t cmp_bfree(const double* restrict a, const uint8_t* restrict m,
                        size_t n, double* restrict out){
    size_t k=0;
    for(size_t i=0;i<n;i++){ out[k]=a[i]; k += (size_t)m[i]; }
    return k;
}

__attribute__((noinline))
static size_t cmp_simd(const double* restrict a, const uint8_t* restrict m,
                       size_t n, double* restrict out){
    size_t i=0,k=0;
    for(; i+4<=n; i+=4){
        __m256d v = _mm256_loadu_pd(a+i);
        uint32_t w; memcpy(&w, m+i, 4);
        unsigned bm = (unsigned)_pext_u32(w, 0x01010101u);
        __m256i pv = _mm256_permutevar8x32_epi32(_mm256_castpd_si256(v), TBLD[bm]);
        _mm256_storeu_pd(out+k, _mm256_castsi256_pd(pv));
        k += (unsigned)__builtin_popcount(bm);
    }
    for(; i<n; i++){ out[k]=a[i]; k += (size_t)m[i]; }   /* branchless tail */
    return k;
}

__attribute__((noinline))
static size_t cmp_simd_skip(const double* restrict a, const uint8_t* restrict m,
                            size_t n, double* restrict out){
    size_t i=0,k=0;
    for(; i+4<=n; i+=4){
        uint32_t w; memcpy(&w, m+i, 4);
        unsigned bm = (unsigned)_pext_u32(w, 0x01010101u);
        if(bm){
            __m256d v = _mm256_loadu_pd(a+i);
            __m256i pv = _mm256_permutevar8x32_epi32(_mm256_castpd_si256(v), TBLD[bm]);
            _mm256_storeu_pd(out+k, _mm256_castsi256_pd(pv));
            k += (unsigned)__builtin_popcount(bm);
        }
    }
    for(; i<n; i++){ out[k]=a[i]; k += (size_t)m[i]; }
    return k;
}

/* ============================================================= INT32 ARMS */
__attribute__((noinline))
static size_t cmp32_ref(const int32_t* restrict a, const uint8_t* restrict m,
                        size_t n, int32_t* restrict out){
    size_t k=0;
    for(size_t i=0;i<n;i++) if(m[i]) out[k++]=a[i];
    return k;
}
__attribute__((noinline))
static size_t cmp32_bfree(const int32_t* restrict a, const uint8_t* restrict m,
                          size_t n, int32_t* restrict out){
    size_t k=0;
    for(size_t i=0;i<n;i++){ out[k]=a[i]; k += (size_t)m[i]; }
    return k;
}
__attribute__((noinline))
static size_t cmp32_simd(const int32_t* restrict a, const uint8_t* restrict m,
                         size_t n, int32_t* restrict out){
    size_t i=0,k=0;
    for(; i+8<=n; i+=8){
        __m256i v = _mm256_loadu_si256((const __m256i*)(a+i));
        uint64_t w; memcpy(&w, m+i, 8);
        unsigned bm = (unsigned)_pext_u64(w, 0x0101010101010101ull);
        __m256i pv = _mm256_permutevar8x32_epi32(v, TBLI[bm]);
        _mm256_storeu_si256((__m256i*)(out+k), pv);
        k += (unsigned)__builtin_popcount(bm);
    }
    for(; i<n; i++){ out[k]=a[i]; k += (size_t)m[i]; }
    return k;
}

/* ================================================================== TIMER */
static double now_s(void){
    struct timespec ts; clock_gettime(CLOCK_MONOTONIC,&ts);
    return (double)ts.tv_sec + 1e-9*(double)ts.tv_nsec;
}
static int dcmp(const void*x,const void*y){
    double a=*(const double*)x,b=*(const double*)y; return (a>b)-(a<b);
}

/* =============================================================== VERIFY */
static uint64_t sink_u64 = 0;

typedef size_t (*dfn)(const double*,const uint8_t*,size_t,double*);
typedef size_t (*ifn)(const int32_t*,const uint8_t*,size_t,int32_t*);

static const char* DNAME[5] = {"R","Rb","A","B","Z"};
static dfn         DFN[5];
static const char* INAME[3] = {"R32","A32","C"};
static ifn         IFN[3];

static int verify_one(double*a,int32_t*a32,uint8_t*m,size_t n,
                      double*ref,double*tst,int32_t*ref32,int32_t*tst32,
                      const char*tag){
    int bad=0;
    size_t truth=0; for(size_t i=0;i<n;i++) truth += m[i];

    memset(ref,0xAB,(n+8)*sizeof(double));
    size_t kR = DFN[0](a,m,n,ref);
    if(kR!=truth){ printf("  FAIL %s: ref count %zu != %zu\n",tag,kR,truth); bad=1; }
    /* independent oracle on the payload */
    for(size_t i=0,k=0;i<n;i++) if(m[i]){
        if(ref[k]!=a[i]){ printf("  FAIL %s: ref payload @%zu\n",tag,k); bad=1; break; }
        k++;
    }

    for(int s=1;s<5;s++){
        memset(tst,0xCD,(n+8)*sizeof(double));
        size_t k = DFN[s](a,m,n,tst);
        if(k!=kR){ printf("  FAIL %s arm %s: count %zu != %zu\n",tag,DNAME[s],k,kR); bad=1; continue; }
        if(k && memcmp(ref,tst,k*sizeof(double))!=0){
            printf("  FAIL %s arm %s: payload memcmp\n",tag,DNAME[s]); bad=1;
        }
    }
    /* int32 arms */
    memset(ref32,0xAB,(n+8)*sizeof(int32_t));
    size_t k32R = IFN[0](a32,m,n,ref32);
    if(k32R!=truth){ printf("  FAIL %s: ref32 count\n",tag); bad=1; }
    for(int s=1;s<3;s++){
        memset(tst32,0xCD,(n+8)*sizeof(int32_t));
        size_t k = IFN[s](a32,m,n,tst32);
        if(k!=k32R){ printf("  FAIL %s arm %s: count %zu != %zu\n",tag,INAME[s],k,k32R); bad=1; continue; }
        if(k && memcmp(ref32,tst32,k*sizeof(int32_t))!=0){
            printf("  FAIL %s arm %s: payload memcmp\n",tag,INAME[s]); bad=1;
        }
    }
    return bad;
}

static int run_verify(void){
    const size_t NS[] = {0,1,2,3,4,5,6,7,8,9,15,16,17,31,32,33,63,64,65,127,255,1000,1001,4095,100003};
    const int    NN = (int)(sizeof(NS)/sizeof(NS[0]));
    const double PS[] = {0.0, 0.01, 0.10, 0.25, 0.50, 0.75, 0.90, 0.99, 1.0};
    const int    NP = (int)(sizeof(PS)/sizeof(PS[0]));
    size_t nmax = NS[NN-1];
    double *a  = xalloc((nmax+8)*sizeof(double));
    double *rf = xalloc((nmax+8)*sizeof(double));
    double *tt = xalloc((nmax+8)*sizeof(double));
    int32_t*a32= xalloc((nmax+8)*sizeof(int32_t));
    int32_t*r32= xalloc((nmax+8)*sizeof(int32_t));
    int32_t*t32= xalloc((nmax+8)*sizeof(int32_t));
    uint8_t*m  = xalloc(nmax+8);
    int bad=0, cases=0;
    char tag[128];
    rseed(12345);
    for(size_t i=0;i<nmax+8;i++){ a[i]= (double)(int64_t)rnd64()*1e-9; a32[i]=(int32_t)rnd64(); }
    for(int in=0; in<NN; ++in){
        size_t n = NS[in];
        for(int ip=0; ip<NP; ++ip){
            double p = PS[ip];
            gen_mask_random(m,n,p);
            snprintf(tag,sizeof tag,"n=%zu p=%.2f rand",n,p);
            bad += verify_one(a,a32,m,n,rf,tt,r32,t32,tag); cases++;
            gen_mask_clustered(m,n,p);
            snprintf(tag,sizeof tag,"n=%zu p=%.2f clus",n,p);
            bad += verify_one(a,a32,m,n,rf,tt,r32,t32,tag); cases++;
        }
        /* explicit edge masks */
        memset(m,0,n); snprintf(tag,sizeof tag,"n=%zu ALL-ZERO",n);
        bad += verify_one(a,a32,m,n,rf,tt,r32,t32,tag); cases++;
        memset(m,1,n); snprintf(tag,sizeof tag,"n=%zu ALL-ONE",n);
        bad += verify_one(a,a32,m,n,rf,tt,r32,t32,tag); cases++;
        /* alternating (worst case for the predictor at 50%) */
        for(size_t i=0;i<n;i++) m[i]=(uint8_t)(i&1);
        snprintf(tag,sizeof tag,"n=%zu ALT",n);
        bad += verify_one(a,a32,m,n,rf,tt,r32,t32,tag); cases++;
        /* single selected element at every position, small n only */
        if(n && n<=65){
            for(size_t j=0;j<n;j++){
                memset(m,0,n); m[j]=1;
                snprintf(tag,sizeof tag,"n=%zu single@%zu",n,j);
                bad += verify_one(a,a32,m,n,rf,tt,r32,t32,tag); cases++;
            }
        }
    }
    printf("VERIFY: %d cases, %d failures  -> %s\n", cases, bad, bad? "RED":"ALL BITWISE IDENTICAL");
    xfree(a);xfree(rf);xfree(tt);xfree(a32);xfree(r32);xfree(t32);xfree(m);
    return bad;
}

/* ================================================================ BENCH */
static uint64_t checksum_d(const double*o,size_t k){
    uint64_t h=1469598103934665603ull;
    for(size_t i=0;i<k;i++){ uint64_t b; memcpy(&b,&o[i],8); h^=b; h*=1099511628211ull; }
    return h;
}
static uint64_t checksum_i(const int32_t*o,size_t k){
    uint64_t h=1469598103934665603ull;
    for(size_t i=0;i<k;i++){ h^=(uint64_t)(uint32_t)o[i]; h*=1099511628211ull; }
    return h;
}

#define REPS 9
static double med_of(double*t,int n){ qsort(t,n,sizeof(double),dcmp); return t[n/2]; }

static void run_bench(size_t N){
    const double PS[] = {0.01,0.10,0.25,0.50,0.75,0.90,0.99};
    const int NP = (int)(sizeof(PS)/sizeof(PS[0]));
    double *a  = xalloc((N+8)*sizeof(double));
    double *o  = xalloc((N+8)*sizeof(double));
    uint8_t*m  = xalloc(N+8);
    rseed(777);
    for(size_t i=0;i<N+8;i++) a[i] = (double)(int64_t)rnd64()*1e-9;

    printf("\n=== 64-bit (double), N = %zu (%.1f MB data) ===\n", N, N*8.0/1048576.0);
    printf("ns per INPUT element (median of %d reps), and logical GB/s\n\n", REPS);
    printf("| pattern | p_req | p_act | R ns | Rb ns | A ns | B ns | Z ns | R GB/s | A GB/s | B GB/s | Z GB/s | R/A | R/B | A/B |\n");
    printf("|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|\n");
    for(int pat=0; pat<2; ++pat){
        for(int ip=0; ip<NP; ++ip){
            double p=PS[ip];
            if(pat==0) gen_mask_random(m,N,p); else gen_mask_clustered(m,N,p);
            size_t sel=0; for(size_t i=0;i<N;i++) sel+=m[i];
            double pact=(double)sel/(double)N;
            double t[5][REPS]; size_t kk[5]; uint64_t cs[5];
            for(int s=0;s<5;s++){ kk[s]=DFN[s](a,m,N,o); cs[s]=0; }  /* warm + fault-in */
            for(int r=0;r<REPS;r++)
                for(int s=0;s<5;s++){
                    asm volatile("" ::: "memory");
                    double t0=now_s();
                    size_t k=DFN[s](a,m,N,o);
                    asm volatile("" ::: "memory");
                    double t1=now_s();
                    t[s][r]=t1-t0; kk[s]=k; sink_u64+=k;
                    if(r==REPS-1) cs[s]=checksum_d(o,k);
                }
            double ns[5],gb[5];
            double bytes = (double)N*8.0 + (double)N*1.0 + (double)sel*8.0;
            for(int s=0;s<5;s++){ double tm=med_of(t[s],REPS); ns[s]=tm*1e9/(double)N; gb[s]=bytes/tm/1e9; }
            for(int s=1;s<5;s++) if(kk[s]!=kk[0]||cs[s]!=cs[0]) printf("  !! BENCH MISMATCH arm %s\n",DNAME[s]);
            printf("| %s | %.2f | %.4f | %.3f | %.3f | %.3f | %.3f | %.3f | %.1f | %.1f | %.1f | %.1f | %.2fx | %.2fx | %.2fx |\n",
                   pat? "clustered":"random", p, pact,
                   ns[0],ns[1],ns[2],ns[3],ns[4],
                   gb[0],gb[2],gb[3],gb[4],
                   ns[0]/ns[2], ns[0]/ns[3], ns[2]/ns[3]);
            fflush(stdout);
        }
    }

    /* ---- int32 phase, reusing the same allocations ---- */
    int32_t*a32=(int32_t*)a; int32_t*o32=(int32_t*)o;
    rseed(999);
    for(size_t i=0;i<N+8;i++) a32[i]=(int32_t)rnd64();
    printf("\n=== 32-bit (int32/float), N = %zu (%.1f MB data) ===\n", N, N*4.0/1048576.0);
    printf("| pattern | p_req | p_act | R32 ns | A32 ns | C ns | R32 GB/s | C GB/s | R/A | R/C | A/C |\n");
    printf("|---|---|---|---|---|---|---|---|---|---|---|\n");
    for(int pat=0; pat<2; ++pat){
        for(int ip=0; ip<NP; ++ip){
            double p=PS[ip];
            if(pat==0) gen_mask_random(m,N,p); else gen_mask_clustered(m,N,p);
            size_t sel=0; for(size_t i=0;i<N;i++) sel+=m[i];
            double pact=(double)sel/(double)N;
            double t[3][REPS]; size_t kk[3]; uint64_t cs[3];
            for(int s=0;s<3;s++){ kk[s]=IFN[s](a32,m,N,o32); cs[s]=0; }
            for(int r=0;r<REPS;r++)
                for(int s=0;s<3;s++){
                    asm volatile("" ::: "memory");
                    double t0=now_s();
                    size_t k=IFN[s](a32,m,N,o32);
                    asm volatile("" ::: "memory");
                    double t1=now_s();
                    t[s][r]=t1-t0; kk[s]=k; sink_u64+=k;
                    if(r==REPS-1) cs[s]=checksum_i(o32,k);
                }
            double ns[3],gb[3];
            double bytes=(double)N*4.0+(double)N*1.0+(double)sel*4.0;
            for(int s=0;s<3;s++){ double tm=med_of(t[s],REPS); ns[s]=tm*1e9/(double)N; gb[s]=bytes/tm/1e9; }
            for(int s=1;s<3;s++) if(kk[s]!=kk[0]||cs[s]!=cs[0]) printf("  !! BENCH MISMATCH arm %s\n",INAME[s]);
            printf("| %s | %.2f | %.4f | %.3f | %.3f | %.3f | %.1f | %.1f | %.2fx | %.2fx | %.2fx |\n",
                   pat?"clustered":"random", p, pact, ns[0],ns[1],ns[2], gb[0],gb[2],
                   ns[0]/ns[1], ns[0]/ns[2], ns[1]/ns[2]);
            fflush(stdout);
        }
    }
    printf("\nanti-DCE sink = %llu\n", (unsigned long long)sink_u64);
    xfree(a);xfree(o);xfree(m);
}

int main(int argc,char**argv){
    DFN[0]=cmp_ref; DFN[1]=cmp_ref_branchy; DFN[2]=cmp_bfree; DFN[3]=cmp_simd; DFN[4]=cmp_simd_skip;
    IFN[0]=cmp32_ref; IFN[1]=cmp32_bfree; IFN[2]=cmp32_simd;
    build_tables();
    const char* mode = argc>1? argv[1] : "both";
    size_t N = argc>2? (size_t)strtoull(argv[2],0,10) : 3999991u;
    int bad=0;
    if(strcmp(mode,"bench")!=0) bad = run_verify();
    if(strcmp(mode,"verify")!=0) run_bench(N);
    return bad?1:0;
}
