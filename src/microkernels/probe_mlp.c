/* probe_mlp.c -- adversarial test of README corrections 6 and 7.
 *
 * Correction 6: "Blocking amortizes byte traffic. It does not -- measured
 * row-blocking (1.59-1.71x) EXCEEDS its own traffic model's 1.50x ceiling, so
 * the model cannot be the mechanism. It is MEMORY-LEVEL PARALLELISM: R
 * independent streams give R outstanding misses against L3 latency."
 *
 * Ruling out ONE model does not establish another.  Three tests here:
 *   [A] stream-count sweep, sequential -- where is the line-fill-buffer knee?
 *   [B] the SAME sweep with the hardware prefetcher DEFEATED (each stream walks
 *       its region in a random line permutation).  If the win is MLP it must
 *       largely survive; if it is prefetch it must collapse.
 *   [C] the row-blocking win itself, measured at FOUR working sets (L1, L2, L3,
 *       DRAM).  MLP predicts the win GROWS with miss latency and is ~1.0x when
 *       everything is L1-resident.  A uop/port-count mechanism predicts it is
 *       roughly FLAT across working sets.  This separates a third hypothesis
 *       the correction never considered.
 *   [D] the same row-blocking A/B with the row PANELS visited in random order.
 *
 * Correction 7: "28.5 GB/s read, 15-20 GB/s RMW, and the ~0.7 ratio between
 * them is what RMW-vs-read should cost."  Verified here by three independent
 * methods: 8-accumulator read (the README's own), best-of-R-stream read, and
 * RMW with and without non-temporal stores (which removes the RFO and so
 * changes the expected ratio from 2/3 to 1/1).
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
                           return (double)ts.tv_sec + 1e-9*(double)ts.tv_nsec; }
static int dcmp(const void*x,const void*y){ double a=*(const double*)x,b=*(const double*)y;
                                            return (a<b)?-1:((a>b)?1:0); }
static double median(double*v,int n){ qsort(v,n,sizeof(double),dcmp); return v[n/2]; }
static inline double hsum256(__m256d v){
    __m128d lo=_mm256_castpd256_pd128(v), hi=_mm256_extractf128_pd(v,1);
    lo=_mm_add_pd(lo,hi);
    return _mm_cvtsd_f64(_mm_add_sd(lo,_mm_unpackhi_pd(lo,lo)));
}
static uint64_t rs=0x243F6A8885A308D3ULL;
static inline uint64_t rnd64(void){ rs^=rs<<13; rs^=rs>>7; rs^=rs<<17; return rs; }

/* ================== [A]/[B]  R-independent-stream read sweep ============== */
/* nd doubles, split into R equal regions.  Each region is walked either
 * sequentially or in a random 64-byte-line permutation.  One YMM accumulator
 * per stream, so R independent dependency chains AND R independent miss
 * streams.  Only the DATA bytes are counted; the permutation index array is
 * read sequentially and adds 4 bytes per 64-byte line (6.25%), noted below. */
static double read_seq(const double* BR a, size_t nd, int R, double* sink){
    size_t per = (nd / (size_t)R) & ~(size_t)7;      /* whole 64B lines */
    __m256d acc[16];
    for(int r=0;r<R;r++) acc[r]=_mm256_setzero_pd();
    double t0=now_s();
    for(size_t o=0; o+8<=per; o+=8)
        for(int r=0;r<R;r++){
            const double* p=a+(size_t)r*per+o;
            acc[r]=_mm256_add_pd(acc[r],_mm256_loadu_pd(p));
            acc[r]=_mm256_add_pd(acc[r],_mm256_loadu_pd(p+4));
        }
    double t=now_s()-t0;
    __m256d z=_mm256_setzero_pd(); for(int r=0;r<R;r++) z=_mm256_add_pd(z,acc[r]);
    *sink += hsum256(z);
    return t;
}
static double read_perm(const double* BR a, const uint32_t* BR perm, size_t nd, int R, double* sink){
    size_t per = (nd / (size_t)R) & ~(size_t)7;
    size_t lines = per/8;
    __m256d acc[16];
    for(int r=0;r<R;r++) acc[r]=_mm256_setzero_pd();
    double t0=now_s();
    for(size_t o=0; o<lines; o++)
        for(int r=0;r<R;r++){
            const double* p=a+(size_t)r*per+(size_t)perm[(size_t)r*lines+o]*8;
            acc[r]=_mm256_add_pd(acc[r],_mm256_loadu_pd(p));
            acc[r]=_mm256_add_pd(acc[r],_mm256_loadu_pd(p+4));
        }
    double t=now_s()-t0;
    __m256d z=_mm256_setzero_pd(); for(int r=0;r<R;r++) z=_mm256_add_pd(z,acc[r]);
    *sink += hsum256(z);
    return t;
}

/* ======================= [C]/[D]  packed symv row blocking ================ */
static inline size_t rowBase(size_t i,size_t n){ return i*(2*n-i+1)/2; }

/* R=1: one row at a time.  y[j] is read-modify-written once PER ROW. */
static void symv_R1(const double* BR S,const double* BR x,double* BR y,size_t n){
    for(size_t i=0;i<n;i++){
        const double* BR p=S+rowBase(i,n)-i;
        double xi=x[i], acc=0.0;
        for(size_t j=i;j<n;j++){ double s=p[j]; acc=fma(s,x[j],acc); y[j]=fma(s,xi,y[j]); }
        y[i]+=acc; y[i]-=p[i]*xi;            /* undo the double-counted diagonal */
    }
}
/* R rows at a time: the R axpy contributions to y[j] are accumulated into ONE
 * scalar in the SAME ascending order, so this is bitwise-identical to R1 on the
 * axpy half AND does one RMW of y[j] instead of R.  That is exactly the
 * "traffic amortization" the correction is arguing about. */
#define MKSYMV(R)                                                              \
static void symv_R##R(const double* BR S,const double* BR x,double* BR y,size_t n){ \
    size_t i=0;                                                                \
    for(; i+R<=n; i+=R){                                                       \
        const double* p[R]; double xi[R], acc[R];                              \
        for(int r=0;r<R;r++){ p[r]=S+rowBase(i+r,n)-(i+r); xi[r]=x[i+r]; acc[r]=0.0; } \
        for(size_t j=i;j<i+R-1;j++)                                            \
            for(int r=0;r<R && i+r<=j;r++){                                    \
                double s=p[r][j]; acc[r]=fma(s,x[j],acc[r]); y[j]=fma(s,xi[r],y[j]); } \
        for(size_t j=i+R-1;j<n;j++){                                           \
            double xj=x[j], t=y[j];                                            \
            for(int r=0;r<R;r++){ double s=p[r][j];                            \
                acc[r]=fma(s,xj,acc[r]); t=fma(s,xi[r],t); }                   \
            y[j]=t;                                                            \
        }                                                                      \
        for(int r=0;r<R;r++){ y[i+r]+=acc[r]; y[i+r]-=p[r][i+r]*xi[r]; }       \
    }                                                                          \
    for(; i<n; i++){                                                           \
        const double* BR q=S+rowBase(i,n)-i;                                   \
        double xii=x[i], a=0.0;                                                \
        for(size_t j=i;j<n;j++){ double s=q[j]; a=fma(s,x[j],a); y[j]=fma(s,xii,y[j]); } \
        y[i]+=a; y[i]-=q[i]*xii;                                               \
    }                                                                          \
}
MKSYMV(2)
MKSYMV(4)
MKSYMV(8)

/* [D] the same R=1 / R=4 pair with the row PANELS visited in a random order.
 * Every cell is still touched exactly once, so the traffic is identical; only
 * the sequential-prefetch friendliness of the S stream changes. */
static void symv_R1_perm(const double* BR S,const double* BR x,double* BR y,size_t n,
                         const uint32_t* BR ord,size_t npan){
    for(size_t q=0;q<npan;q++){
        size_t i=ord[q];
        const double* BR p=S+rowBase(i,n)-i;
        double xi=x[i], acc=0.0;
        for(size_t j=i;j<n;j++){ double s=p[j]; acc=fma(s,x[j],acc); y[j]=fma(s,xi,y[j]); }
        y[i]+=acc; y[i]-=p[i]*xi;
    }
}
static void symv_R4_perm(const double* BR S,const double* BR x,double* BR y,size_t n,
                         const uint32_t* BR ord,size_t npan){
    for(size_t q=0;q<npan;q++){
        size_t i=ord[q]; if(i+4>n) continue;
        const double* p[4]; double xi[4], acc[4];
        for(int r=0;r<4;r++){ p[r]=S+rowBase(i+r,n)-(i+r); xi[r]=x[i+r]; acc[r]=0.0; }
        for(size_t j=i;j<i+3;j++)
            for(int r=0;r<4 && i+r<=j;r++){
                double s=p[r][j]; acc[r]=fma(s,x[j],acc[r]); y[j]=fma(s,xi[r],y[j]); }
        for(size_t j=i+3;j<n;j++){
            double xj=x[j], t=y[j];
            for(int r=0;r<4;r++){ double s=p[r][j]; acc[r]=fma(s,xj,acc[r]); t=fma(s,xi[r],t); }
            y[j]=t;
        }
        for(int r=0;r<4;r++){ y[i+r]+=acc[r]; y[i+r]-=p[r][i+r]*xi[r]; }
    }
}

/* ============================ [E]  roofline ============================== */
static double bw_read8(const double* BR a,size_t nd,double* sink){
    __m256d acc[8]; for(int t=0;t<8;t++) acc[t]=_mm256_setzero_pd();
    double t0=now_s();
    size_t i=0;
    for(;i+32<=nd;i+=32) for(int t=0;t<8;t++) acc[t]=_mm256_add_pd(acc[t],_mm256_loadu_pd(a+i+4*t));
    double t=now_s()-t0;
    __m256d z=_mm256_setzero_pd(); for(int q=0;q<8;q++) z=_mm256_add_pd(z,acc[q]);
    *sink += hsum256(z);
    return t;
}
static double bw_rmw(const double* BR a,double* BR b,size_t nd){
    const __m256d k=_mm256_set1_pd(1.0000001);
    double t0=now_s();
    for(size_t i=0;i+4<=nd;i+=4)
        _mm256_storeu_pd(b+i,_mm256_fmadd_pd(_mm256_loadu_pd(a+i),k,_mm256_loadu_pd(b+i)));
    return now_s()-t0;
}
static double bw_rmw_nt(const double* BR a,double* BR b,size_t nd){
    const __m256d k=_mm256_set1_pd(1.0000001);
    double t0=now_s();
    for(size_t i=0;i+4<=nd;i+=4)
        _mm256_stream_pd(b+i,_mm256_fmadd_pd(_mm256_loadu_pd(a+i),k,_mm256_loadu_pd(b+i)));
    _mm_sfence();
    return now_s()-t0;
}
static double bw_write(double* BR b,size_t nd){
    const __m256d v=_mm256_set1_pd(3.25);
    double t0=now_s();
    for(size_t i=0;i+4<=nd;i+=4) _mm256_storeu_pd(b+i,v);
    return now_s()-t0;
}
static double bw_write_nt(double* BR b,size_t nd){
    const __m256d v=_mm256_set1_pd(3.25);
    double t0=now_s();
    for(size_t i=0;i+4<=nd;i+=4) _mm256_stream_pd(b+i,v);
    _mm_sfence();
    return now_s()-t0;
}

/* ================================ driver ================================= */
#define REPS 11
static double tv[REPS];

int main(int argc,char**argv){
    int reps=(argc>1)?atoi(argv[1]):REPS; if(reps>REPS) reps=REPS;
    size_t nd=18045028;                                  /* 138 MB, DRAM-resident */
    double* a=(double*)_mm_malloc(nd*sizeof(double),64);
    double* b=(double*)_mm_malloc(nd*sizeof(double),64);
    if(!a||!b){ printf("alloc fail\n"); return 2; }
    for(size_t i=0;i<nd;i++){ a[i]=(double)(i&7)+0.5; b[i]=0.0; }
    double sink=0;

    printf("=== probe_mlp: corrections 6 and 7 ===\ncompiler %s   buffer %.1f MB   median of %d\n",
           __VERSION__, nd*8.0/1048576.0, reps);

    /* ---------------- [E] roofline, three methods ---------------- */
    printf("\n[E] SINGLE-CORE ROOFLINE (DRAM-resident, %.0f MB)\n", nd*8.0/1048576.0);
    printf("    %-34s %10s %10s %8s\n","method","ms","GB/s","lines/elem");
    for(int r=0;r<reps;r++) tv[r]=bw_read8(a,nd,&sink);
    double m_read8=median(tv,reps);
    printf("    %-34s %10.3f %10.2f %8s\n","read, 8 YMM acc (README's own)",m_read8*1e3,nd*8.0/m_read8/1e9,"1 R");
    for(int r=0;r<reps;r++) tv[r]=bw_rmw(a,b,nd);
    double m_rmw=median(tv,reps);
    printf("    %-34s %10.3f %10.2f %8s\n","RMW  a->b, normal stores",m_rmw*1e3,3.0*nd*8.0/m_rmw/1e9,"3 (RFO)");
    for(int r=0;r<reps;r++) tv[r]=bw_rmw_nt(a,b,nd);
    double m_rmwnt=median(tv,reps);
    printf("    %-34s %10.3f %10.2f %8s\n","RMW  a->b, NON-TEMPORAL stores",m_rmwnt*1e3,2.0*nd*8.0/m_rmwnt/1e9,"2 (no RFO)");
    for(int r=0;r<reps;r++) tv[r]=bw_write(b,nd);
    double m_wr=median(tv,reps);
    printf("    %-34s %10.3f %10.2f %8s\n","write only, normal stores",m_wr*1e3,2.0*nd*8.0/m_wr/1e9,"2 (RFO)");
    for(int r=0;r<reps;r++) tv[r]=bw_write_nt(b,nd);
    double m_wrnt=median(tv,reps);
    printf("    %-34s %10.3f %10.2f %8s\n","write only, NON-TEMPORAL stores",m_wrnt*1e3,1.0*nd*8.0/m_wrnt/1e9,"1 W");

    /* ---------------- [A] sequential stream-count sweep ---------------- */
    printf("\n[A] READ BANDWIDTH vs INDEPENDENT STREAM COUNT (sequential)\n");
    printf("    %4s %10s %10s %9s\n","R","ms","GB/s","vs R=1");
    double gseq[17];
    for(int R=1;R<=16;R++){
        for(int r=0;r<reps;r++) tv[r]=read_seq(a,nd,R,&sink);
        double m=median(tv,reps);
        size_t per=(nd/(size_t)R)&~(size_t)7; double bytes=(double)per*R*8.0;
        gseq[R]=bytes/m/1e9;
        printf("    %4d %10.3f %10.2f %8.2fx\n",R,m*1e3,gseq[R],gseq[R]/gseq[1]);
    }

    /* ---------------- [B] prefetch-defeated sweep ---------------- */
    printf("\n[B] SAME SWEEP, HARDWARE PREFETCHER DEFEATED (random 64B-line order per stream)\n");
    printf("    (index array adds 4 B per 64 B line = 6.25%% traffic, read sequentially, NOT counted)\n");
    printf("    %4s %10s %10s %9s %12s\n","R","ms","GB/s","vs R=1","seq/perm");
    size_t maxlines=nd/8;
    uint32_t* perm=(uint32_t*)_mm_malloc(maxlines*sizeof(uint32_t),64);
    if(!perm){ printf("perm alloc fail\n"); return 2; }
    double gper[17];
    for(int R=1;R<=16;R++){
        size_t per=(nd/(size_t)R)&~(size_t)7, lines=per/8;
        for(int r=0;r<R;r++){
            uint32_t* q=perm+(size_t)r*lines;
            for(size_t i=0;i<lines;i++) q[i]=(uint32_t)i;
            for(size_t i=lines;i>1;i--){ size_t j=(size_t)(rnd64()%i); uint32_t t=q[i-1]; q[i-1]=q[j]; q[j]=t; }
        }
        int rr = (reps>5)?5:reps;                      /* the random walk is slow */
        for(int r=0;r<rr;r++) tv[r]=read_perm(a,perm,nd,R,&sink);
        double m=median(tv,rr);
        double bytes=(double)per*R*8.0;
        gper[R]=bytes/m/1e9;
        printf("    %4d %10.3f %10.2f %8.2fx %11.2fx\n",R,m*1e3,gper[R],gper[R]/gper[1],gseq[R]/gper[R]);
    }
    _mm_free(perm);

    /* ---------------- [C] row-blocking win vs working set ---------------- */
    printf("\n[C] PACKED SYMV ROW-BLOCKING WIN vs WORKING SET\n");
    printf("    MLP predicts the win GROWS with miss latency (~1.0x when L1-resident).\n");
    printf("    A uop/port mechanism predicts it is FLAT across working sets.\n");
    printf("    %6s %10s %8s %10s %10s %10s %10s %8s %8s %8s %6s\n",
           "n","S bytes","level","R1 ms","R2 ms","R4 ms","R8 ms","R2/R1","R4/R1","R8/R1","bits");
    { const size_t ns[4]={79,311,1301,6007};
      const char* lv[4]={"L1","L2","L3","DRAM"};
      for(int q=0;q<4;q++){
        size_t n=ns[q], cells=n*(n+1)/2;
        double* S=(double*)_mm_malloc(cells*sizeof(double),64);
        double* x=(double*)_mm_malloc(n*sizeof(double),64);
        double* y=(double*)_mm_malloc(n*sizeof(double),64);
        double* yr=(double*)_mm_malloc(n*sizeof(double),64);
        for(size_t i=0;i<cells;i++) S[i]=(double)(rnd64()>>11)*(1.0/9007199254740992.0)+0.5;
        for(size_t i=0;i<n;i++) x[i]=(double)(rnd64()>>11)*(1.0/9007199254740992.0)+0.5;
        memset(yr,0,n*sizeof(double)); symv_R1(S,x,yr,n);
        int bits=1;
        { memset(y,0,n*sizeof(double)); symv_R2(S,x,y,n); if(memcmp(y,yr,n*sizeof(double))) bits=0;
          memset(y,0,n*sizeof(double)); symv_R4(S,x,y,n); if(memcmp(y,yr,n*sizeof(double))) bits=0;
          memset(y,0,n*sizeof(double)); symv_R8(S,x,y,n); if(memcmp(y,yr,n*sizeof(double))) bits=0; }
        double t1[REPS],t2[REPS],t4[REPS],t8[REPS];
        int inner = (cells<200000)? 64 : 1;            /* keep small cases off the timer floor */
        for(int r=0;r<reps;r++){
            memset(y,0,n*sizeof(double)); OBSERVE(y); BARRIER();
            double s0=now_s(); for(int u=0;u<inner;u++) symv_R1(S,x,y,n); t1[r]=(now_s()-s0)/inner; OBSERVE(y);
            memset(y,0,n*sizeof(double)); OBSERVE(y); BARRIER();
            s0=now_s(); for(int u=0;u<inner;u++) symv_R2(S,x,y,n); t2[r]=(now_s()-s0)/inner; OBSERVE(y);
            memset(y,0,n*sizeof(double)); OBSERVE(y); BARRIER();
            s0=now_s(); for(int u=0;u<inner;u++) symv_R4(S,x,y,n); t4[r]=(now_s()-s0)/inner; OBSERVE(y);
            memset(y,0,n*sizeof(double)); OBSERVE(y); BARRIER();
            s0=now_s(); for(int u=0;u<inner;u++) symv_R8(S,x,y,n); t8[r]=(now_s()-s0)/inner; OBSERVE(y);
        }
        double m1=median(t1,reps),m2=median(t2,reps),m4=median(t4,reps),m8=median(t8,reps);
        printf("    %6zu %10.0f %8s %10.4f %10.4f %10.4f %10.4f %7.3fx %7.3fx %7.3fx %6s\n",
               n,(double)cells*8.0,lv[q],m1*1e3,m2*1e3,m4*1e3,m8*1e3,m1/m2,m1/m4,m1/m8,
               bits?"yes":"NO");
        sink+=y[0];
        _mm_free(S);_mm_free(x);_mm_free(y);_mm_free(yr);
      }
    }

    /* ---------------- [D] blocking win with panel order randomized -------- */
    printf("\n[D] ROW-BLOCKING WIN, PANEL ORDER SEQUENTIAL vs RANDOM (n=6007, DRAM)\n");
    { size_t n=6007, cells=n*(n+1)/2;
      double* S=(double*)_mm_malloc(cells*sizeof(double),64);
      double* x=(double*)_mm_malloc(n*sizeof(double),64);
      double* y=(double*)_mm_malloc(n*sizeof(double),64);
      for(size_t i=0;i<cells;i++) S[i]=(double)(rnd64()>>11)*(1.0/9007199254740992.0)+0.5;
      for(size_t i=0;i<n;i++) x[i]=(double)(rnd64()>>11)*(1.0/9007199254740992.0)+0.5;
      size_t np1=n, np4=n/4;
      uint32_t* o1=(uint32_t*)_mm_malloc(np1*sizeof(uint32_t),64);
      uint32_t* o4=(uint32_t*)_mm_malloc(np4*sizeof(uint32_t),64);
      for(size_t i=0;i<np1;i++) o1[i]=(uint32_t)i;
      for(size_t i=np1;i>1;i--){ size_t j=(size_t)(rnd64()%i); uint32_t t=o1[i-1];o1[i-1]=o1[j];o1[j]=t; }
      for(size_t i=0;i<np4;i++) o4[i]=(uint32_t)(i*4);
      for(size_t i=np4;i>1;i--){ size_t j=(size_t)(rnd64()%i); uint32_t t=o4[i-1];o4[i-1]=o4[j];o4[j]=t; }
      double t1[REPS],t4[REPS],p1[REPS],p4[REPS];
      for(int r=0;r<reps;r++){
        memset(y,0,n*sizeof(double)); OBSERVE(y); BARRIER();
        double s0=now_s(); symv_R1(S,x,y,n); t1[r]=now_s()-s0; OBSERVE(y); BARRIER();
        memset(y,0,n*sizeof(double)); OBSERVE(y); BARRIER();
        s0=now_s(); symv_R4(S,x,y,n); t4[r]=now_s()-s0; OBSERVE(y); BARRIER();
        memset(y,0,n*sizeof(double)); OBSERVE(y); BARRIER();
        s0=now_s(); symv_R1_perm(S,x,y,n,o1,np1); p1[r]=now_s()-s0; OBSERVE(y); BARRIER();
        memset(y,0,n*sizeof(double)); OBSERVE(y); BARRIER();
        s0=now_s(); symv_R4_perm(S,x,y,n,o4,np4); p4[r]=now_s()-s0; OBSERVE(y); BARRIER();
      }
      double a1=median(t1,reps),a4=median(t4,reps),b1=median(p1,reps),b4=median(p4,reps);
      printf("    sequential panels : R1 %8.3f ms   R4 %8.3f ms   blocking win %6.3fx\n",a1*1e3,a4*1e3,a1/a4);
      printf("    random panels     : R1 %8.3f ms   R4 %8.3f ms   blocking win %6.3fx\n",b1*1e3,b4*1e3,b1/b4);
      printf("    prefetch tax      : R1 %6.3fx   R4 %6.3fx\n",b1/a1,b4/a4);
      printf("    >> if the win were PREFETCH it should collapse toward 1.00x on random panels\n");
      sink+=y[0];
      _mm_free(S);_mm_free(x);_mm_free(y);_mm_free(o1);_mm_free(o4);
    }

    printf("\n(sink %.6g)\n", sink);
    _mm_free(a);_mm_free(b);
    return 0;
}
