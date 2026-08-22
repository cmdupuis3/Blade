/* segfold.c -- fused segmented reduction (group_by + reduce) microkernel.
 *
 * CSR-shaped data: values v[0..N), boundaries off[0..G] ascending, off[0]==0,
 * off[G]==N.  out[g] = sum(v[off[g] .. off[g+1])).  Empty segments -> +0.0.
 *
 * Arms
 *   REF_G  two-pass, ONE malloc PER GROUP  (what Blade emits today)
 *   REF_P  two-pass, ONE CSR pool          (allocation fix only, still 2 passes)
 *   A      fused, scalar, 1 accumulator per segment
 *   B      fused, length-classed multi-accumulator (4 / 2 / 1 chains)
 *   C      fused, VECTOR-STREAMING: 4 doubles at a time regardless of segment
 *          boundaries, boundary-masked split  (approach (i))
 *   D      fused, ADAPTIVE per-segment dispatch: scalar / 1 YMM / 4 YMM
 *   RG     REF_G but the materialize pass is a PERMUTED gather (realistic
 *          group_by: source rows are not in CSR order)
 *   AP     fused single-pass permuted gather (fusion applied to the real case)
 *
 * build: gcc -O3 -march=native -ffp-contract=fast -o segfold.exe segfold.c
 * run:   ./segfold.exe verify
 *        ./segfold.exe bench [N] [reps]
 *        ./segfold.exe cache
 */
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <stdint.h>
#include <immintrin.h>
#include <windows.h>

typedef int idx_t;

/* ------------------------------------------------------------------ util */
static double now_s(void){
    LARGE_INTEGER f,c; QueryPerformanceFrequency(&f); QueryPerformanceCounter(&c);
    return (double)c.QuadPart/(double)f.QuadPart;
}
#define BARRIER() __asm__ __volatile__("" ::: "memory")

static inline double hsum256(__m256d x){
    __m128d lo = _mm256_castpd256_pd128(x);
    __m128d hi = _mm256_extractf128_pd(x,1);
    lo = _mm_add_pd(lo,hi);
    __m128d h  = _mm_unpackhi_pd(lo,lo);
    return _mm_cvtsd_f64(_mm_add_sd(lo,h));
}

static const uint64_t MLO[5][4] = {
    {0,0,0,0},
    {~0ull,0,0,0},
    {~0ull,~0ull,0,0},
    {~0ull,~0ull,~0ull,0},
    {~0ull,~0ull,~0ull,~0ull}
};
static const uint64_t MHI[5][4] = {
    {~0ull,~0ull,~0ull,~0ull},
    {0,~0ull,~0ull,~0ull},
    {0,0,~0ull,~0ull},
    {0,0,0,~0ull},
    {0,0,0,0}
};
static inline __m256d mlo(int k){ return _mm256_castsi256_pd(_mm256_loadu_si256((const __m256i*)MLO[k])); }
static inline __m256d mhi(int k){ return _mm256_castsi256_pd(_mm256_loadu_si256((const __m256i*)MHI[k])); }

/* ---------------------------------------------------------------- arms */

/* REF_G: Blade today -- one new T[sz] per group, materialize, then reduce. */
__attribute__((noinline))
static void ref_gmalloc(const double *v, const idx_t *off, int G, double *out){
    if(G<=0) return;
    double **bufs = (double**)malloc((size_t)G*sizeof(double*));
    for(int g=0; g<G; g++){
        int b=off[g], sz=off[g+1]-b;
        double *p = (double*)malloc((size_t)(sz>0?sz:1)*sizeof(double));
        for(int k=0;k<sz;k++) p[k]=v[b+k];
        bufs[g]=p;
    }
    for(int g=0; g<G; g++){
        int sz=off[g+1]-off[g]; const double *p=bufs[g]; double s=0.0;
        for(int k=0;k<sz;k++) s+=p[k];
        out[g]=s;
    }
    for(int g=0;g<G;g++) free(bufs[g]);
    free(bufs);
}

/* REF_P: the allocation fix ONLY -- one CSR pool of off[G] doubles, still 2 passes. */
__attribute__((noinline))
static void ref_pool(const double *v, const idx_t *off, int G, double *out){
    if(G<=0) return;
    int N = off[G];
    double *pool = (double*)malloc((size_t)(N>0?N:1)*sizeof(double));
    for(int i=0;i<N;i++) pool[i]=v[i];
    for(int g=0; g<G; g++){
        int b=off[g], e=off[g+1]; double s=0.0;
        for(int i=b;i<e;i++) s+=pool[i];
        out[g]=s;
    }
    free(pool);
}

/* A: fused, one accumulator. Serial FP chain: 3 cyc/elem on Zen 3 (vaddsd lat 3). */
__attribute__((noinline))
static void arm_a(const double *v, const idx_t *off, int G, double *out){
    for(int g=0; g<G; g++){
        int b=off[g], e=off[g+1]; double s=0.0;
        for(int i=b;i<e;i++) s+=v[i];
        out[g]=s;
    }
}

/* B: fused, length-classed scalar multi-accumulator.
 * 4 chains -> 3/4 = 0.75 cyc/elem issue-limited (0.5 cyc/elem hw floor,
 * 2 FP-add pipes), so 4 chains already clears the ~1.1 cyc/elem DRAM roof. */
__attribute__((noinline))
static void arm_b(const double *v, const idx_t *off, int G, double *out){
    for(int g=0; g<G; g++){
        int b=off[g], e=off[g+1], n=e-b;
        if(n >= 16){
            double a0=0,a1=0,a2=0,a3=0; int i=b, lim=b+(n & ~3);
            for(; i<lim; i+=4){ a0+=v[i]; a1+=v[i+1]; a2+=v[i+2]; a3+=v[i+3]; }
            for(; i<e; i++) a0+=v[i];
            out[g]=(a0+a1)+(a2+a3);
        } else if(n >= 4){
            double a0=0,a1=0; int i=b, lim=b+(n & ~1);
            for(; i<lim; i+=2){ a0+=v[i]; a1+=v[i+1]; }
            for(; i<e; i++) a0+=v[i];
            out[g]=a0+a1;
        } else {
            double s=0.0; for(int i=b;i<e;i++) s+=v[i];
            out[g]=s;
        }
    }
}

/* C: VECTOR-STREAMING with boundary masking (approach (i)).
 * One pass over v in 4-double chunks, independent of segment structure.
 * acc holds 4 lane-partials of the CURRENT open segment; carry holds scalars.
 * FLUSH closes segment g (and any run of empty segments) and resets both. */
__attribute__((noinline))
static void arm_c(const double *v, const idx_t *off, int G, double *out){
    if(G<=0) return;
    int N = off[G];
    __m256d acc = _mm256_setzero_pd();
    double carry = 0.0;
    int g = 0, i = 0;

#define FLUSH_TO(P) do{ \
        while(g<G && off[g+1] <= (P)){ out[g] = hsum256(acc) + carry; \
            acc=_mm256_setzero_pd(); carry=0.0; g++; } \
    }while(0)

    FLUSH_TO(0);                       /* leading empty segments */
    for(; i+4 <= N; i += 4){
        __m256d x = _mm256_loadu_pd(v+i);
        int e = off[g+1];
        if(e >= i+4){                  /* whole chunk inside segment g */
            acc = _mm256_add_pd(acc, x);
            if(e == i+4) FLUSH_TO(i+4);
        } else {                       /* boundary strictly inside the chunk */
            int k = e - i;             /* 1..3 lanes belong to g */
            acc = _mm256_add_pd(acc, _mm256_and_pd(x, mlo(k)));
            FLUSH_TO(e);
            int e2 = off[g+1];
            if(e2 >= i+4){             /* exactly one boundary: masked hi half */
                acc = _mm256_and_pd(x, mhi(k));
                if(e2 == i+4) FLUSH_TO(i+4);
            } else {                   /* >= 2 boundaries in one chunk: scalar walk */
                for(int j=e; j<i+4; j++){
                    FLUSH_TO(j);
                    carry += v[j];
                }
            }
        }
    }
    for(; i<N; i++){ FLUSH_TO(i); carry += v[i]; }
    while(g<G){ out[g] = hsum256(acc) + carry; acc=_mm256_setzero_pd(); carry=0.0; g++; }
#undef FLUSH_TO
}

/* D: ADAPTIVE per-segment dispatch.
 * n>=32 : 4 YMM accs = 16 elems / 4 vaddpd; 4 chains x lat 3 -> 0.1875 cyc/elem.
 * n>=8  : 1 YMM acc, 4 elems / 1 vaddpd, chain 3 cyc -> 0.75 cyc/elem.
 * n<8   : scalar chain; a vector prologue + hsum (>=10 cyc) never pays here. */
__attribute__((noinline))
static void arm_d(const double *v, const idx_t *off, int G, double *out){
    for(int g=0; g<G; g++){
        int b=off[g], e=off[g+1], n=e-b;
        if(n >= 32){
            __m256d a0=_mm256_setzero_pd(),a1=a0,a2=a0,a3=a0;
            int i=b, lim=b+(n & ~15);
            for(; i<lim; i+=16){
                a0=_mm256_add_pd(a0,_mm256_loadu_pd(v+i));
                a1=_mm256_add_pd(a1,_mm256_loadu_pd(v+i+4));
                a2=_mm256_add_pd(a2,_mm256_loadu_pd(v+i+8));
                a3=_mm256_add_pd(a3,_mm256_loadu_pd(v+i+12));
            }
            __m256d s=_mm256_add_pd(_mm256_add_pd(a0,a1),_mm256_add_pd(a2,a3));
            double r=hsum256(s);
            for(; i<e; i++) r+=v[i];
            out[g]=r;
        } else if(n >= 8){
            __m256d a0=_mm256_setzero_pd();
            int i=b, lim=b+(n & ~3);
            for(; i<lim; i+=4) a0=_mm256_add_pd(a0,_mm256_loadu_pd(v+i));
            double r=hsum256(a0);
            for(; i<e; i++) r+=v[i];
            out[g]=r;
        } else {
            double s=0.0; for(int i=b;i<e;i++) s+=v[i];
            out[g]=s;
        }
    }
}

/* E: STREAMING + 4 CHAINS. C's boundary handling, but whenever the open segment
 * has >= 16 elements left it runs a boundary-free 4xYMM block (4 independent
 * 3-cycle chains -> 0.1875 cyc/elem) and folds the 4 chains back into the single
 * carried accumulator at the end of the run.  Subsumes C and D. */
__attribute__((noinline))
static void arm_e(const double *v, const idx_t *off, int G, double *out){
    if(G<=0) return;
    int N = off[G];
    __m256d acc = _mm256_setzero_pd();
    double carry = 0.0;
    int g = 0, i = 0;

#define FLUSH_TO(P) do{ \
        while(g<G && off[g+1] <= (P)){ out[g] = hsum256(acc) + carry; \
            acc=_mm256_setzero_pd(); carry=0.0; g++; } \
    }while(0)

    while(i + 4 <= N){
        FLUSH_TO(i);                        /* close whatever ended at or before i */
        int e = off[g+1];
        if(e - i >= 16){                    /* boundary-free run: 4 chains */
            int lim = i + ((e - i) & ~15);
            __m256d a0=acc,a1=_mm256_setzero_pd(),a2=a1,a3=a1;
            for(; i<lim; i+=16){
                a0=_mm256_add_pd(a0,_mm256_loadu_pd(v+i));
                a1=_mm256_add_pd(a1,_mm256_loadu_pd(v+i+4));
                a2=_mm256_add_pd(a2,_mm256_loadu_pd(v+i+8));
                a3=_mm256_add_pd(a3,_mm256_loadu_pd(v+i+12));
            }
            acc=_mm256_add_pd(_mm256_add_pd(a0,a1),_mm256_add_pd(a2,a3));
            continue;
        }
        __m256d x = _mm256_loadu_pd(v+i);
        if(e >= i+4){                       /* whole chunk inside segment g */
            acc = _mm256_add_pd(acc, x);
            i += 4;
        } else {
            int k = e - i;
            acc = _mm256_add_pd(acc, _mm256_and_pd(x, mlo(k)));
            FLUSH_TO(e);
            if(off[g+1] >= i+4){            /* exactly one boundary in the chunk */
                acc = _mm256_and_pd(x, mhi(k));
            } else {                        /* >= 2 boundaries: scalar walk the tail */
                for(int j=e; j<i+4; j++){ FLUSH_TO(j); carry += v[j]; }
            }
            i += 4;
        }
    }
    for(; i<N; i++){ FLUSH_TO(i); carry += v[i]; }
    while(g<G){ out[g] = hsum256(acc) + carry; acc=_mm256_setzero_pd(); carry=0.0; g++; }
#undef FLUSH_TO
}

/* RG: reference where the materialize pass is a PERMUTED gather (real group_by). */
__attribute__((noinline))
static void ref_gmalloc_perm(const double *src, const idx_t *perm,
                             const idx_t *off, int G, double *out){
    if(G<=0) return;
    double **bufs = (double**)malloc((size_t)G*sizeof(double*));
    for(int g=0; g<G; g++){
        int b=off[g], sz=off[g+1]-b;
        double *p=(double*)malloc((size_t)(sz>0?sz:1)*sizeof(double));
        for(int k=0;k<sz;k++) p[k]=src[perm[b+k]];
        bufs[g]=p;
    }
    for(int g=0; g<G; g++){
        int sz=off[g+1]-off[g]; const double*p=bufs[g]; double s=0.0;
        for(int k=0;k<sz;k++) s+=p[k];
        out[g]=s;
    }
    for(int g=0;g<G;g++) free(bufs[g]);
    free(bufs);
}

/* AP: fused permuted gather, 4 chains, no materialization. */
__attribute__((noinline))
static void arm_ap(const double *src, const idx_t *perm,
                   const idx_t *off, int G, double *out){
    for(int g=0; g<G; g++){
        int b=off[g], e=off[g+1], n=e-b;
        if(n >= 16){
            double a0=0,a1=0,a2=0,a3=0; int i=b, lim=b+(n & ~3);
            for(; i<lim; i+=4){
                a0+=src[perm[i]]; a1+=src[perm[i+1]];
                a2+=src[perm[i+2]]; a3+=src[perm[i+3]];
            }
            for(; i<e; i++) a0+=src[perm[i]];
            out[g]=(a0+a1)+(a2+a3);
        } else {
            double s=0.0; for(int i=b;i<e;i++) s+=src[perm[i]];
            out[g]=s;
        }
    }
}

/* ------------------------------------------------------------- data gen */
static uint64_t rs=0x243F6A8885A308D3ull;
static inline uint64_t rnd(void){ rs^=rs<<13; rs^=rs>>7; rs^=rs<<17; return rs; }

static double val_at(int i){
    uint32_t h=(uint32_t)i*2654435761u; h^=h>>13; h*=0x85EBCA6Bu; h^=h>>16;
    return (double)((int)(h%17u)-8);     /* exactly representable small ints */
}

/* dist 0 uniform-large (N/G~1000), 1 uniform-small (N/G~4), 2 skewed */
static int build_off(int dist, int N, idx_t **poff){
    int G;
    idx_t *off;
    if(dist==0){ G = N/1000; if(G<1) G=1; }
    else if(dist==1){ G = N/4; if(G<1) G=1; }
    else { G = N/16; if(G<2) G=2; }
    off=(idx_t*)malloc((size_t)(G+1)*sizeof(idx_t));
    if(dist<2){
        int L=N/G; off[0]=0;
        for(int g=1; g<G; g++) off[g]=off[g-1]+L;
        off[G]=N;
    } else {
        rs=0x243F6A8885A308D3ull;
        idx_t *len=(idx_t*)malloc((size_t)G*sizeof(idx_t));
        long long S=0;
        for(int g=1; g<G; g++){
            uint64_t r=rnd();
            int L = ((r&15)==0) ? (int)(40+(r>>8)%80) : (int)((r>>4)%5);
            len[g]=L; S+=L;
        }
        if(S>=N){
            for(int g=G-1; g>=1 && S>=N; g--){ S-=len[g]; len[g]=0; }
        }
        len[0]=(idx_t)(N-S);                  /* one huge segment */
        off[0]=0; for(int g=0; g<G; g++) off[g+1]=off[g]+len[g];
        free(len);
    }
    *poff=off; return G;
}

static const char* dname(int d){
    return d==0? "uniform-large N/G~1000" : d==1? "uniform-small N/G~4" : "skewed (1 huge + many tiny/empty)";
}

/* --------------------------------------------------------------- verify */
static int cmp_bits(const double*a,const double*b,int n,const char*tag,const char*ref){
    for(int i=0;i<n;i++){
        uint64_t x,y; memcpy(&x,a+i,8); memcpy(&y,b+i,8);
        if(x!=y){ printf("  MISMATCH %s vs %s at g=%d: %.17g (%016llx) != %.17g (%016llx)\n",
            tag,ref,i,a[i],(unsigned long long)x,b[i],(unsigned long long)y); return 1; }
    }
    return 0;
}

static int verify_case(const char*name, const double*v, const idx_t*off, int G,
                       const idx_t*perm, const double*src){
    int n = G>0?G:1;
    double *r0=(double*)malloc((size_t)n*8), *r1=(double*)malloc((size_t)n*8);
    double *a=(double*)malloc((size_t)n*8),  *b=(double*)malloc((size_t)n*8);
    double *c=(double*)malloc((size_t)n*8),  *d=(double*)malloc((size_t)n*8);
    double *e=(double*)malloc((size_t)n*8),  *f=(double*)malloc((size_t)n*8);
    int bad=0;
    memset(r0,0xAA,(size_t)n*8); memset(r1,0xAA,(size_t)n*8);
    memset(a,0xAA,(size_t)n*8);  memset(b,0xAA,(size_t)n*8);
    memset(c,0xAA,(size_t)n*8);  memset(d,0xAA,(size_t)n*8);
    ref_gmalloc(v,off,G,r0);
    ref_pool   (v,off,G,r1);
    arm_a(v,off,G,a); arm_b(v,off,G,b); arm_c(v,off,G,c); arm_d(v,off,G,d);
    memset(e,0xAA,(size_t)n*8); arm_e(v,off,G,e);
    bad|=cmp_bits(r1,r0,G,"REF_P","REF_G");
    bad|=cmp_bits(a, r0,G,"A","REF_G");
    bad|=cmp_bits(b, r0,G,"B","REF_G");
    bad|=cmp_bits(c, r0,G,"C","REF_G");
    bad|=cmp_bits(d, r0,G,"D","REF_G");
    bad|=cmp_bits(e, r0,G,"E","REF_G");
    if(perm){
        memset(e,0xAA,(size_t)n*8); memset(f,0xAA,(size_t)n*8);
        ref_gmalloc_perm(src,perm,off,G,e);
        arm_ap(src,perm,off,G,f);
        bad|=cmp_bits(f,e,G,"AP","RG");
        bad|=cmp_bits(e,r0,G,"RG","REF_G");
    }
    printf("  %-46s G=%-8d %s\n", name, G, bad?"FAIL":"ok (bitwise)");
    free(r0);free(r1);free(a);free(b);free(c);free(d);free(e);free(f);
    return bad;
}

static void mk_perm(int N, idx_t *perm, double *src, const double *v){
    for(int i=0;i<N;i++) perm[i]=i;
    rs=0xB5026F5AA96619Eull;
    for(int i=N-1;i>0;i--){ int j=(int)(rnd()%(uint64_t)(i+1)); idx_t t=perm[i];perm[i]=perm[j];perm[j]=t; }
    for(int i=0;i<N;i++) src[perm[i]]=v[i];
}

static int run_verify(void){
    int bad=0;
    printf("VERIFY (small-integer values in [-8,8]; all sums exact -> bitwise comparison)\n");
    { idx_t off[1]={0}; double v[1]={1.0}; bad|=verify_case("G=0 (no segments)",v,off,0,NULL,NULL); }
    { idx_t off[2]={0,0}; double v[1]={1.0}; bad|=verify_case("G=1, empty (identity +0.0)",v,off,1,NULL,NULL); }
    { idx_t off[2]={0,1}; double v[1]={3.0}; bad|=verify_case("G=1, one element",v,off,1,NULL,NULL); }
    { idx_t off[6]={0,0,0,0,0,0}; double v[1]={1.0}; bad|=verify_case("all-empty G=5",v,off,5,NULL,NULL); }
    { idx_t off[12]={0,0,0,1,1,3,3,4,7,7,9,9};
      double v[9]; for(int i=0;i<9;i++) v[i]=val_at(i);
      bad|=verify_case("mixed empties + len 1,2,1,3,2 (sub-vector segs)",v,off,11,NULL,NULL); }
    { int N=64; double *v=(double*)malloc((size_t)N*8); for(int i=0;i<N;i++) v[i]=val_at(i);
      for(int L=1;L<=9;L++){
        int G=(N+L-1)/L; idx_t *off=(idx_t*)malloc((size_t)(G+1)*sizeof(idx_t));
        off[0]=0; for(int g=1;g<=G;g++){ int x=off[g-1]+L; off[g]= x>N?N:x; }
        char nm[64]; sprintf(nm,"phase sweep: uniform len=%d over N=64",L);
        bad|=verify_case(nm,v,off,G,NULL,NULL); free(off);
      }
      free(v); }
    { int N=1000000;
      double *v=(double*)malloc((size_t)N*8);
      double *src=(double*)malloc((size_t)N*8);
      idx_t *perm=(idx_t*)malloc((size_t)N*sizeof(idx_t));
      for(int i=0;i<N;i++) v[i]=val_at(i);
      mk_perm(N,perm,src,v);
      for(int d=0; d<3; d++){
          idx_t *off; int G=build_off(d,N,&off);
          char nm[96]; sprintf(nm,"dist %d: %s",d,dname(d));
          bad|=verify_case(nm,v,off,G,perm,src);
          free(off);
      }
      free(v);free(src);free(perm); }
    printf("VERIFY: %s\n", bad?"FAILURES":"ALL BITWISE IDENTICAL");
    return bad;
}

/* ---------------------------------------------------------------- bench */
typedef struct { const char*name; int kind; } armdesc;

static double bench_one(int kind, const double*v, const idx_t*off, int G,
                        const idx_t*perm, const double*src, double*out,
                        int reps, double *chk){
    double best=1e30, s=0;
    for(int r=0;r<reps;r++){
        BARRIER();
        double t0=now_s();
        switch(kind){
            case 0: ref_gmalloc(v,off,G,out); break;
            case 1: ref_pool(v,off,G,out); break;
            case 2: arm_a(v,off,G,out); break;
            case 3: arm_b(v,off,G,out); break;
            case 4: arm_c(v,off,G,out); break;
            case 5: arm_d(v,off,G,out); break;
            case 6: ref_gmalloc_perm(src,perm,off,G,out); break;
            case 7: arm_ap(src,perm,off,G,out); break;
            case 8: arm_e(v,off,G,out); break;
        }
        BARRIER();
        double t1=now_s();
        if(t1-t0<best) best=t1-t0;
        s=0; for(int g=0;g<G;g++) s+=out[g];
        BARRIER();
    }
    *chk=s; return best;
}

static void bench(int N,int reps,int cacheres){
    static const armdesc arms[9]={
        {"REF_G  2-pass, G mallocs   (Blade today)",0},
        {"REF_P  2-pass, 1 CSR pool  (alloc fix)  ",1},
        {"A      fused scalar 1-acc               ",2},
        {"B      fused scalar 4/2/1-acc           ",3},
        {"C      fused VECTOR-STREAM boundary-mask",4},
        {"D      fused ADAPTIVE 4xYMM/1xYMM/scalar",5},
        {"E      fused STREAM+4chain (C and D fused) ",8},
        {"RG     2-pass PERMUTED gather (realistic)",6},
        {"AP     fused PERMUTED gather, 4-acc     ",7},
    };
    double *v=(double*)malloc((size_t)N*8);
    double *src=(double*)malloc((size_t)N*8);
    idx_t *perm=(idx_t*)malloc((size_t)N*sizeof(idx_t));
    for(int i=0;i<N;i++) v[i]=val_at(i);
    mk_perm(N,perm,src,v);
    printf("\nBENCH N=%d (%.1f MB values) reps=%d  %s\n",N,N*8.0/1e6,reps,
        cacheres?"[cache-resident]":"[DRAM-resident]");
    for(int d=0; d<3; d++){
        idx_t *off; int G=build_off(d,N,&off);
        double *out=(double*)malloc((size_t)(G>0?G:1)*8);
        { long long ne=0,n1=0,mx=0; for(int q=0;q<G;q++){ int L=off[q+1]-off[q];
              if(L==0) ne++; if(L<4) n1++; if(L>mx) mx=L; }
          printf("\n  dist %d  %s   G=%d  mean len=%.1f  empty=%.1f%%  len<4=%.1f%%  max=%lld (%.1f%% of N)\n",
              d,dname(d),G,(double)N/(double)G,100.0*(double)ne/G,100.0*(double)n1/G,mx,100.0*(double)mx/(double)N); }
        printf("    %-42s %10s %10s %8s %9s\n","arm","ms","ns/elem","GB/s","vs REF_G");
        double base=0;
        for(int k=0;k<9;k++){
            double chk=0;
            double t=bench_one(arms[k].kind,v,off,G,perm,src,out,reps,&chk);
            if(k==0) base=t;
            double nspe=t*1e9/(double)N;
            double bytes = (double)N*8.0 + (double)G*8.0 + (double)(G+1)*4.0;
            printf("    %-42s %10.3f %10.4f %8.2f %9.2fx  chk=%.0f\n",
                arms[k].name, t*1e3, nspe, bytes/t/1e9, base/t, chk);
        }
        free(out); free(off);
    }
    free(v);free(src);free(perm);
}

int main(int argc,char**argv){
    if(argc>1 && !strcmp(argv[1],"verify")) return run_verify();
    if(argc>1 && !strcmp(argv[1],"cache")){ bench(262144,60,1); return 0; }
    int N = argc>2? atoi(argv[2]) : 4000000;
    int reps = argc>3? atoi(argv[3]) : 5;
    bench(N,reps,0);
    return 0;
}
