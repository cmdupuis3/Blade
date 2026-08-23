/* align_probe.c -- adversarial test of the README's "Two properties worth
 * preserving in any emitter", second property:
 *
 *   "PACKED ROWS CANNOT BE COLLECTIVELY ALIGNED.  The row pitch shrinks by one
 *    every row, so at most 1/R of an R-row panel is ever 32-byte aligned.  Any
 *    emitter that assumes aligned packed row starts is wrong; these kernels use
 *    unaligned loads throughout, and that is a consequence of the layout, not
 *    laziness."
 *
 * The statement is arithmetically true.  The question is whether it COSTS
 * anything on Zen 3, where unaligned loads are famously cheap.  Three tests:
 *   [1] vmovapd vs vmovupd on IDENTICAL, aligned addresses  (encoding only)
 *   [2] vmovupd at every misalignment 0..7 doubles, at L1/L2/DRAM working sets
 *       (this is the only thing the packed layout actually forces)
 *   [3] the real kernel: packed syr over NATURAL packed storage vs a PADDED
 *       packed layout whose every row start is 32-byte aligned.  Padding costs
 *       <= 3 cells per row -- 0.15% of the array at n=2003 -- so if alignment
 *       mattered an emitter could simply buy it.  This prices the property.
 *
 * Also settles a claim in CLAUDE.md that this territory inherits:
 *   [4] "PACKED TRIANGULAR storage is IMMUNE to the power-of-two cache artifact
 *        (varying row pitch)."  Swept against a dense column walk on the same
 *        box, which is the artifact generator.
 */
#define _POSIX_C_SOURCE 200809L
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <stdint.h>
#include <math.h>
#include <time.h>
#include <immintrin.h>
#include "clangtimer.h"

#define OBSERVE(p) __asm__ __volatile__("" : : "r"(p) : "memory")
#define BARRIER()  __asm__ __volatile__("" : : : "memory")
#define BR __restrict

static double now_s(void){ struct timespec ts; clock_gettime(CLOCK_MONOTONIC,&ts);
                           return (double)ts.tv_sec+1e-9*(double)ts.tv_nsec; }
static int dcmp(const void*x,const void*y){ double a=*(const double*)x,b=*(const double*)y;
                                            return (a<b)?-1:((a>b)?1:0); }
static double median(double*v,int n){ qsort(v,n,sizeof(double),dcmp); return v[n/2]; }
static inline double hsum256(__m256d v){
    __m128d lo=_mm256_castpd256_pd128(v), hi=_mm256_extractf128_pd(v,1);
    lo=_mm_add_pd(lo,hi);
    return _mm_cvtsd_f64(_mm_add_sd(lo,_mm_unpackhi_pd(lo,lo)));
}
static uint64_t rs=0x9E3779B97F4A7C15ULL;
static inline uint64_t rnd64(void){ rs^=rs<<13; rs^=rs>>7; rs^=rs<<17; return rs; }
static inline double rnd_d(void){ return (double)(rnd64()>>11)*(1.0/9007199254740992.0)+0.5; }

static inline size_t rowbase(size_t n,size_t i){ return i*(2*n-i+1)/2; }

/* --------------- [1]/[2] raw load-form / misalignment ------------------- */
__attribute__((noinline))
static double sum_aligned(const double* BR a,size_t nd){
    __m256d acc[8]; for(int t=0;t<8;t++) acc[t]=_mm256_setzero_pd();
    for(size_t i=0;i+32<=nd;i+=32) for(int t=0;t<8;t++) acc[t]=_mm256_add_pd(acc[t],_mm256_load_pd(a+i+4*t));
    __m256d z=_mm256_setzero_pd(); for(int t=0;t<8;t++) z=_mm256_add_pd(z,acc[t]);
    return hsum256(z);
}
__attribute__((noinline))
static double sum_unaligned(const double* BR a,size_t nd){
    __m256d acc[8]; for(int t=0;t<8;t++) acc[t]=_mm256_setzero_pd();
    for(size_t i=0;i+32<=nd;i+=32) for(int t=0;t<8;t++) acc[t]=_mm256_add_pd(acc[t],_mm256_loadu_pd(a+i+4*t));
    __m256d z=_mm256_setzero_pd(); for(int t=0;t<8;t++) z=_mm256_add_pd(z,acc[t]);
    return hsum256(z);
}

/* --------------- [3] packed syr: natural vs padded-aligned -------------- */
/* natural packed: row i begins at rowbase(i), pitch n-i.  UNALIGNED loads. */
__attribute__((noinline))
static void syr_natural(size_t n,const double* BR x,double* BR C){
    for(size_t i=0;i<n;i++){
        double* BR p=C+rowbase(n,i)-i;
        __m256d v=_mm256_set1_pd(x[i]);
        size_t j=i;
        for(;j+4<=n;j+=4)
            _mm256_storeu_pd(p+j,_mm256_fmadd_pd(v,_mm256_loadu_pd(x+j),_mm256_loadu_pd(p+j)));
        for(;j<n;++j) p[j]=fma(x[i],x[j],p[j]);
    }
}
/* padded packed: row i begins at PB[i], rounded up to a multiple of 4 doubles,
 * so every ROW START (the C read-modify-write stream, the expensive one) is
 * 32-byte aligned.  x stays a single shared n-element vector and is therefore
 * still read UNALIGNED -- which is itself the point: p[j] and x[j] cannot both
 * be aligned for the same j unless i % 4 == 0, so alignment of the output row
 * is the ONLY thing an emitter can actually buy.  Padding costs <= 3 cells per
 * row. */
__attribute__((noinline))
static void syr_padded(size_t n,const size_t* BR PB,const double* BR x,double* BR C){
    for(size_t i=0;i<n;i++){
        double* BR p=C+PB[i];                     /* 32B aligned */
        __m256d v=_mm256_set1_pd(x[i]);
        size_t len=n-i, j=0;
        for(;j+4<=len;j+=4)
            _mm256_store_pd(p+j,_mm256_fmadd_pd(v,_mm256_loadu_pd(x+i+j),_mm256_load_pd(p+j)));
        for(;j<len;++j) p[j]=fma(x[i],x[i+j],p[j]);
    }
}
/* control: padded layout but UNALIGNED instruction forms -- isolates the
 * instruction encoding from the layout change (padding also moves addresses and
 * so redistributes cache sets). */
__attribute__((noinline))
static void syr_padded_u(size_t n,const size_t* BR PB,const double* BR x,double* BR C){
    for(size_t i=0;i<n;i++){
        double* BR p=C+PB[i];
        __m256d v=_mm256_set1_pd(x[i]);
        size_t len=n-i, j=0;
        for(;j+4<=len;j+=4)
            _mm256_storeu_pd(p+j,_mm256_fmadd_pd(v,_mm256_loadu_pd(x+i+j),_mm256_loadu_pd(p+j)));
        for(;j<len;++j) p[j]=fma(x[i],x[i+j],p[j]);
    }
}

/* --------------- [4] power-of-two: dense column vs packed --------------- */
__attribute__((noinline))
static double dense_colwalk(const double* BR M,size_t n){
    double a0=0,a1=0,a2=0,a3=0;
    for(size_t j=0;j<n;j+=4){
        for(size_t i=0;i<n;i++){
            const double* r=M+i*n+j;
            a0+=r[0]; a1+=r[1]; a2+=r[2]; a3+=r[3];
        }
    }
    return (a0+a1)+(a2+a3);
}
__attribute__((noinline))
static double packed_colwalk(const double* BR P,size_t n){
    /* the mirrored read: for each i, walk j=0..i-1 at P[rowbase(j)+(i-j)].
     * The stride shrinks by one every step -- the "varying row pitch". */
    double a0=0,a1=0,a2=0,a3=0;
    for(size_t i=1;i<n;i++){
        size_t p=i, st=n-1, j=0;
        for(;j+4<=i;j+=4){
            a0+=P[p]; p+=st; st--;
            a1+=P[p]; p+=st; st--;
            a2+=P[p]; p+=st; st--;
            a3+=P[p]; p+=st; st--;
        }
        for(;j<i;j++){ a0+=P[p]; p+=st; st--; }
    }
    return (a0+a1)+(a2+a3);
}
__attribute__((noinline))
static double packed_rowwalk(const double* BR P,size_t cells){
    __m256d acc[4]; for(int t=0;t<4;t++) acc[t]=_mm256_setzero_pd();
    size_t i=0;
    for(;i+16<=cells;i+=16) for(int t=0;t<4;t++) acc[t]=_mm256_add_pd(acc[t],_mm256_loadu_pd(P+i+4*t));
    __m256d z=_mm256_add_pd(_mm256_add_pd(acc[0],acc[1]),_mm256_add_pd(acc[2],acc[3]));
    double s=hsum256(z);
    for(;i<cells;i++) s+=P[i];
    return s;
}

#define REPS 11
static double tv[REPS];

int main(int argc,char**argv){
    int reps=(argc>1)?atoi(argv[1]):REPS; if(reps>REPS) reps=REPS;
    double sink=0;
    printf("=== align_probe: 'packed rows cannot be collectively aligned' ===\n");
    printf("compiler %s   median of %d\n",__VERSION__,reps);

    /* ---- [1] instruction form only, identical aligned addresses ---- */
    printf("\n[1] vmovapd vs vmovupd on IDENTICAL 64B-ALIGNED addresses\n");
    printf("    %10s %10s %12s %12s %9s\n","elems","MB","aligned ms","unaligned ms","u/a");
    { const size_t szs[3]={3095,124001,18045028};      /* ~L1, ~L2/L3, DRAM */
      for(int q=0;q<3;q++){
        size_t nd=szs[q]&~(size_t)31;
        double* a=(double*)_mm_malloc(nd*sizeof(double),64);
        for(size_t i=0;i<nd;i++) a[i]=rnd_d();
        int inner=(nd*8<1000000)?200:1;
        for(int r=0;r<reps;r++){ OBSERVE(a);BARRIER(); double s=now_s();
            for(int u=0;u<inner;u++) sink+=sum_aligned(a,nd); tv[r]=(now_s()-s)/inner; }
        double ma=median(tv,reps);
        for(int r=0;r<reps;r++){ OBSERVE(a);BARRIER(); double s=now_s();
            for(int u=0;u<inner;u++) sink+=sum_unaligned(a,nd); tv[r]=(now_s()-s)/inner; }
        double mu=median(tv,reps);
        printf("    %10zu %10.2f %12.5f %12.5f %8.4fx\n",nd,nd*8.0/1048576.0,ma*1e3,mu*1e3,mu/ma);
        _mm_free(a);
      } }

    /* ---- [2] vmovupd at every misalignment ---- */
    printf("\n[2] vmovupd at misalignment 0..7 doubles (the ONLY thing packed rows force)\n");
    printf("    %10s %8s","working set","");
    for(int off=0;off<8;off++) printf(" %7d",off);
    printf("   worst/best\n");
    { const size_t szs[3]={3095,124001,18045028};
      const char* lv[3]={"L1","L2/L3","DRAM"};
      for(int q=0;q<3;q++){
        size_t nd=(szs[q]&~(size_t)31);
        double* a=(double*)_mm_malloc((nd+8)*sizeof(double),64);
        for(size_t i=0;i<nd+8;i++) a[i]=rnd_d();
        int inner=(nd*8<1000000)?200:1;
        double m[8], best=1e30, worst=0;
        for(int off=0;off<8;off++){
            for(int r=0;r<reps;r++){ OBSERVE(a);BARRIER(); double s=now_s();
                for(int u=0;u<inner;u++) sink+=sum_unaligned(a+off,nd); tv[r]=(now_s()-s)/inner; }
            m[off]=median(tv,reps);
            if(m[off]<best) best=m[off];
            if(m[off]>worst) worst=m[off];
        }
        printf("    %10s %8s",lv[q],"");
        for(int off=0;off<8;off++) printf(" %7.3f",m[off]/best);
        printf("   %8.4fx\n",worst/best);
        _mm_free(a);
      } }

    /* ---- [3] the real kernel: natural vs padded-aligned packed ---- */
    printf("\n[3] PACKED SYR: natural (unaligned rows) vs PADDED (every row 32B-aligned)\n");
    printf("    %7s %12s %12s %11s %11s %11s %9s %9s %8s\n",
           "n","cells","pad cells","natural ms","padA ms","padU ms","nat/padA","padU/padA","pad cost");
    { const size_t ns[4]={311,701,2003,6007};
      for(int q=0;q<4;q++){
        size_t n=ns[q], cells=n*(n+1)/2;
        size_t* PB=(size_t*)malloc(n*sizeof(size_t));
        size_t off=0;
        for(size_t i=0;i<n;i++){ PB[i]=off; off += ((n-i)+3)&~(size_t)3; }
        size_t pcells=off;
        double* C =(double*)_mm_malloc(cells*sizeof(double),64);
        double* Cp=(double*)_mm_malloc(pcells*sizeof(double),64);
        double* x =(double*)_mm_malloc(n*sizeof(double),64);
        if(!C||!Cp||!x){ printf("alloc fail n=%zu\n",n); continue; }
        for(size_t i=0;i<n;i++) x[i]=rnd_d();
        memset(C,0,cells*sizeof(double)); memset(Cp,0,pcells*sizeof(double));
        int inner=(cells*8<1000000)?100:1;
        double tn[REPS],tpa[REPS],tpu[REPS];
        /* ROTATE the arm order every rep.  The three arms touch two 16 MB
         * buffers, so a fixed order gives whichever arm runs later a warm L3
         * and biases the ratio; rotation makes each arm lead 1/3 of the time. */
        for(int r=0;r<reps;r++){
          for(int o=0;o<3;o++){
            int which=(r+o)%3; double s0;
            if(which==0){ OBSERVE(C);BARRIER(); s0=now_s();
                for(int u=0;u<inner;u++) syr_natural(n,x,C); tn[r]=(now_s()-s0)/inner; OBSERVE(C); }
            else if(which==1){ OBSERVE(Cp);BARRIER(); s0=now_s();
                for(int u=0;u<inner;u++) syr_padded(n,PB,x,Cp); tpa[r]=(now_s()-s0)/inner; OBSERVE(Cp); }
            else { OBSERVE(Cp);BARRIER(); s0=now_s();
                for(int u=0;u<inner;u++) syr_padded_u(n,PB,x,Cp); tpu[r]=(now_s()-s0)/inner; OBSERVE(Cp); }
          }
        }
        double mn=median(tn,reps), mpa=median(tpa,reps), mpu=median(tpu,reps);
        printf("    %7zu %12zu %12zu %11.5f %11.5f %11.5f %8.4fx %8.4fx %7.3f%%\n",
               n,cells,pcells,mn*1e3,mpa*1e3,mpu*1e3,mn/mpa,mpu/mpa,
               100.0*((double)pcells-(double)cells)/(double)cells);
        sink+=C[0]+Cp[0];
        free(PB);_mm_free(C);_mm_free(Cp);_mm_free(x);
      } }

    /* ---- [4] power-of-two artifact: dense column vs packed ---- */
    printf("\n[4] POWER-OF-TWO CACHE ARTIFACT: dense column walk vs packed walks\n");
    printf("    %7s %12s %12s %14s %14s\n","n","dense ns/cell","dense rel","packed-col ns/c","packed-row ns/c");
    { const size_t ns[9]={1021,1024,1031,2039,2048,2053,4093,4096,4099};
      double dbase=0;
      for(int q=0;q<9;q++){
        size_t n=ns[q], cells=n*(n+1)/2;
        double* M=(double*)_mm_malloc(n*n*sizeof(double),64);
        double* P=(double*)_mm_malloc(cells*sizeof(double),64);
        if(!M||!P){ printf("alloc fail n=%zu\n",n); continue; }
        for(size_t i=0;i<(size_t)n*n;i++) M[i]=0.5;
        for(size_t i=0;i<cells;i++) P[i]=0.5;
        for(int r=0;r<reps;r++){ OBSERVE(M);BARRIER(); double s=now_s(); sink+=dense_colwalk(M,n); tv[r]=now_s()-s; }
        double md=median(tv,reps)/(double)(n*n)*1e9;
        for(int r=0;r<reps;r++){ OBSERVE(P);BARRIER(); double s=now_s(); sink+=packed_colwalk(P,n); tv[r]=now_s()-s; }
        double mc=median(tv,reps)/(double)(cells)*1e9;
        for(int r=0;r<reps;r++){ OBSERVE(P);BARRIER(); double s=now_s(); sink+=packed_rowwalk(P,cells); tv[r]=now_s()-s; }
        double mr=median(tv,reps)/(double)cells*1e9;
        if(q%3==0) dbase=md;
        printf("    %7zu %12.4f %11.3fx %14.4f %14.4f\n",n,md,md/dbase,mc,mr);
        _mm_free(M);_mm_free(P);
      } }

    printf("\n(sink %.6g)\n",sink);
    return 0;
}
