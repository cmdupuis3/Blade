/* paired-triangle (RFP at register/tile scale) for the rank-2 symmetric former
 *   C(i<=j) = sum_t A(t,i)*A(t,j),  A is d x n row-major, C packed ascending-lex.
 *
 * ref   : obvious triangular loop
 * armA  : B-tiled, dense BxB microkernel on EVERY block, masked store on diagonal
 * armB  : same, except diagonal block i is PAIRED with diagonal block T-1-i and the
 *         two triangles are interlocked into one BxB square.
 */
#define _POSIX_C_SOURCE 199309L
#include <immintrin.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <stdint.h>
#include <math.h>
#include <time.h>

#define MR 6
#define NR 8
#define PT_BMAX 256

/* Anti-hoist guards: the kernels are pure-ish over loop-invariant arguments, so
 * without these the compiler is entitled to lift a whole timed call out of the
 * repetition loop and report a fake near-zero time. */
#define BARRIER()   __asm__ __volatile__("" ::: "memory")
#define OBSERVE(p)  __asm__ __volatile__("" :: "r"(p) : "memory")

static double now_s(void){ struct timespec ts; clock_gettime(CLOCK_MONOTONIC,&ts);
                           return (double)ts.tv_sec + 1e-9*(double)ts.tv_nsec; }

static inline size_t cidx(int n,int i,int j){
    return (size_t)i*(size_t)(2*n - i + 1)/2u + (size_t)(j-i);
}
static inline size_t ncells(int n){ return (size_t)n*(size_t)(n+1)/2u; }

/* ---------------- 6x8 register-blocked rank-1-update microkernel ---------------
 * 12 accumulator YMM (6 rows x 8 cols = 6 x 2 vectors)
 *  + 2 YMM for the column operand + 1 YMM broadcast = 15 of 16.
 * 12 INDEPENDENT dependency chains (>= 8 required: 2 FMA/cyc x 4 cyc latency).
 * The t-loop is the (single) reduction axis and is NOT reassociated: each output
 * cell accumulates strictly in ascending t, exactly like the reference.
 */
static void ukern6x8(int d, const double* __restrict Pa, const double* __restrict Pb,
                     int ldp, double* __restrict out, int ldout)
{
    __m256d c00=_mm256_setzero_pd(), c01=_mm256_setzero_pd();
    __m256d c10=_mm256_setzero_pd(), c11=_mm256_setzero_pd();
    __m256d c20=_mm256_setzero_pd(), c21=_mm256_setzero_pd();
    __m256d c30=_mm256_setzero_pd(), c31=_mm256_setzero_pd();
    __m256d c40=_mm256_setzero_pd(), c41=_mm256_setzero_pd();
    __m256d c50=_mm256_setzero_pd(), c51=_mm256_setzero_pd();
    for (int t=0;t<d;t++){
        __m256d B0=_mm256_load_pd(Pb+0);
        __m256d B1=_mm256_load_pd(Pb+4);
        __m256d a;
        a=_mm256_broadcast_sd(Pa+0); c00=_mm256_fmadd_pd(a,B0,c00); c01=_mm256_fmadd_pd(a,B1,c01);
        a=_mm256_broadcast_sd(Pa+1); c10=_mm256_fmadd_pd(a,B0,c10); c11=_mm256_fmadd_pd(a,B1,c11);
        a=_mm256_broadcast_sd(Pa+2); c20=_mm256_fmadd_pd(a,B0,c20); c21=_mm256_fmadd_pd(a,B1,c21);
        a=_mm256_broadcast_sd(Pa+3); c30=_mm256_fmadd_pd(a,B0,c30); c31=_mm256_fmadd_pd(a,B1,c31);
        a=_mm256_broadcast_sd(Pa+4); c40=_mm256_fmadd_pd(a,B0,c40); c41=_mm256_fmadd_pd(a,B1,c41);
        a=_mm256_broadcast_sd(Pa+5); c50=_mm256_fmadd_pd(a,B0,c50); c51=_mm256_fmadd_pd(a,B1,c51);
        Pa+=ldp; Pb+=ldp;
    }
    _mm256_storeu_pd(out+0*ldout+0,c00); _mm256_storeu_pd(out+0*ldout+4,c01);
    _mm256_storeu_pd(out+1*ldout+0,c10); _mm256_storeu_pd(out+1*ldout+4,c11);
    _mm256_storeu_pd(out+2*ldout+0,c20); _mm256_storeu_pd(out+2*ldout+4,c21);
    _mm256_storeu_pd(out+3*ldout+0,c30); _mm256_storeu_pd(out+3*ldout+4,c31);
    _mm256_storeu_pd(out+4*ldout+0,c40); _mm256_storeu_pd(out+4*ldout+4,c41);
    _mm256_storeu_pd(out+5*ldout+0,c50); _mm256_storeu_pd(out+5*ldout+4,c51);
}

/* full dense BxB block, S row-major with ld = B */
static void block_dense(int d,const double* Pa,const double* Pb,int B,double* S){
    for(int a0=0;a0<B;a0+=MR)
        for(int b0=0;b0<B;b0+=NR)
            ukern6x8(d, Pa+a0, Pb+b0, B, S+(size_t)a0*B+b0, B);
}

/* ------------------------------- packing --------------------------------- */
static void pack_panel(const double*A,int d,int n,int c0,int w,int B,double*P){
    for(int t=0;t<d;t++){
        int a=0;
        for(;a<w;a++) P[(size_t)t*B+a]=A[(size_t)t*n+c0+a];
        for(;a<B;a++) P[(size_t)t*B+a]=0.0;
    }
}

/* ------------------------------- reference -------------------------------- */
static void ref_former(const double*A,int d,int n,double*C){
    for(int i=0;i<n;i++)
        for(int j=i;j<n;j++){
            double s=0.0;
            for(int t=0;t<d;t++) s += A[(size_t)t*n+i]*A[(size_t)t*n+j];
            C[cidx(n,i,j)]=s;
        }
}

/* ------------------------- shared off-diagonal pass ----------------------- */
static void offdiag_pass(int d,int n,int B,int T,const double*panels,double*C,double*S){
    for(int I=0;I<T;I++){
        int i0=I*B, wI=(n-i0<B)?(n-i0):B;
        const double*PI=panels+(size_t)I*B*d;
        for(int J=I+1;J<T;J++){
            int j0=J*B, wJ=(n-j0<B)?(n-j0):B;
            const double*PJ=panels+(size_t)J*B*d;
            block_dense(d,PI,PJ,B,S);
            for(int a=0;a<wI;a++)
                for(int b=0;b<wJ;b++)
                    C[cidx(n,i0+a,j0+b)]=S[(size_t)a*B+b];
        }
    }
}

/* ---------------------- ARM A: masked dense diagonal ---------------------- */
static void diag_masked_one(int d,int n,int B,int I,const double*panels,double*C,double*S){
    int i0=I*B, w=(n-i0<B)?(n-i0):B;
    const double*PI=panels+(size_t)I*B*d;
    block_dense(d,PI,PI,B,S);                 /* full square: half the FLOPs wasted */
    for(int a=0;a<w;a++)
        for(int b=a;b<w;b++)
            C[cidx(n,i0+a,i0+b)]=S[(size_t)a*B+b];
}
static void diag_pass_masked(int d,int n,int B,int T,const double*panels,double*C,double*S){
    for(int I=0;I<T;I++) diag_masked_one(d,n,B,I,panels,C,S);
}

/* --------------------- ARM B: paired-triangle diagonal --------------------
 * Square S over block I index range:
 *    S[a][b], b>=a  <-  C_I (i0+a, i0+b)              triangle I, incl diagonal
 *    S[a][b], b< a  <-  C_Ip(i1+b, i1+a)              triangle Ip, STRICTLY upper
 * Register tiles fall into three classes; the staircase boundary is paid only on
 * the O(B/MR) tiles that straddle the diagonal (those are computed twice).
 * Triangle Ip loses its own diagonal in this packing (2 * B(B+1)/2 = B^2 + B > B^2),
 * so B diagonal cells are peeled as a separate, perfectly vectorizable pass.
 */
static void diag_paired(int d,int n,int B,int I,int Ip,const double*panels,double*C,double*S){
    int i0=I*B,  wI =(n-i0<B)?(n-i0):B;
    int i1=Ip*B, wIp=(n-i1<B)?(n-i1):B;
    const double*PI =panels+(size_t)I *B*d;
    const double*PIp=panels+(size_t)Ip*B*d;
    double tmpU[MR*NR], tmpL[MR*NR];

    for(int a0=0;a0<B;a0+=MR){
        for(int b0=0;b0<B;b0+=NR){
            if (b0 >= a0+MR-1){                       /* pure upper: all cells b>=a */
                ukern6x8(d,PI +a0,PI +b0,B,S+(size_t)a0*B+b0,B);
            } else if (b0+NR-1 < a0){                 /* pure lower: all cells b<a  */
                ukern6x8(d,PIp+a0,PIp+b0,B,S+(size_t)a0*B+b0,B);
            } else {                                  /* straddles the staircase    */
                ukern6x8(d,PI +a0,PI +b0,B,tmpU,NR);
                ukern6x8(d,PIp+a0,PIp+b0,B,tmpL,NR);
                for(int a=0;a<MR;a++)
                    for(int b=0;b<NR;b++)
                        S[(size_t)(a0+a)*B + (b0+b)] =
                            ((b0+b) >= (a0+a)) ? tmpU[a*NR+b] : tmpL[a*NR+b];
            }
        }
    }
    /* split the square into the two packed triangles */
    for(int a=0;a<B;a++){
        for(int b=a;b<B;b++)  if(a<wI  && b<wI ) C[cidx(n,i0+a,i0+b)]=S[(size_t)a*B+b];
        for(int b=0;b<a;b++)  if(b<wIp && a<wIp) C[cidx(n,i1+b,i1+a)]=S[(size_t)a*B+b];
    }
    /* Peel triangle Ip diagonal (2*B(B+1)/2 = B^2+B does not fit a BxB square).
     * B independent lanes, t ascending in every lane => same order as the
     * reference, still bit-exact, and B/4 independent YMM chains instead of one
     * scalar 4-cycle-latency chain.  (The naive scalar form of this loop cost
     * ~35% of the whole paired block -- see report.md.) */
    {
        double acc[PT_BMAX];
        for(int a=0;a<B;a++) acc[a]=0.0;
        for(int t=0;t<d;t++){
            const double* __restrict row = PIp + (size_t)t*B;
            for(int a=0;a<B;a++) acc[a] += row[a]*row[a];
        }
        for(int a=0;a<wIp;a++) C[cidx(n,i1+a,i1+a)]=acc[a];
    }
}
static void diag_pass_paired(int d,int n,int B,int T,const double*panels,double*C,double*S){
    for(int i=0;i<T/2;i++) diag_paired(d,n,B,i,T-1-i,panels,C,S);
    if (T&1) diag_masked_one(d,n,B,T/2,panels,C,S);   /* self-paired middle: peel */
}

/* --------------------------------- harness -------------------------------- */
static void fill_int(double*A,size_t m,unsigned seed){
    unsigned s=seed;
    for(size_t k=0;k<m;k++){ s=s*1664525u+1013904223u; A[k]=(double)(int)((s>>16)%9)-4.0; }
}
static void fill_rand(double*A,size_t m,unsigned seed){
    unsigned s=seed;
    for(size_t k=0;k<m;k++){ s=s*1664525u+1013904223u; A[k]=((double)(s>>8)/8388608.0)-1.0; }
}

static int cmp_bits(const double*X,const double*Y,size_t m,double*maxrel,size_t*badidx){
    size_t bad=0; *maxrel=0; *badidx=(size_t)-1;
    for(size_t k=0;k<m;k++){
        if(memcmp(&X[k],&Y[k],sizeof(double))!=0){
            if(!bad) *badidx=k;
            bad++;
            double den=fabs(X[k])>1.0?fabs(X[k]):1.0;
            double r=fabs(X[k]-Y[k])/den; if(r>*maxrel)*maxrel=r;
        }
    }
    return (int)(bad>0);
}

typedef struct { double* panels; double* S; int T; } Work;

static Work setup(const double*A,int d,int n,int B){
    Work w; w.T=(n+B-1)/B;
    w.panels=(double*)_mm_malloc((size_t)w.T*B*d*sizeof(double),64);
    w.S     =(double*)_mm_malloc((size_t)B*B*sizeof(double),64);
    for(int I=0;I<w.T;I++){
        int i0=I*B, wI=(n-i0<B)?(n-i0):B;
        pack_panel(A,d,n,i0,wI,B,w.panels+(size_t)I*B*d);
    }
    return w;
}

static void run_correct(int n,int d,int B,int randmode){
    size_t m=ncells(n);
    double*A=(double*)_mm_malloc((size_t)d*n*sizeof(double),64);
    if(randmode) fill_rand(A,(size_t)d*n,12345u); else fill_int(A,(size_t)d*n,7u);
    double*Cr=(double*)_mm_malloc(m*sizeof(double),64);
    double*Ca=(double*)_mm_malloc(m*sizeof(double),64);
    double*Cb=(double*)_mm_malloc(m*sizeof(double),64);
    memset(Ca,0x5a,m*sizeof(double)); memset(Cb,0x5a,m*sizeof(double));
    ref_former(A,d,n,Cr);
    Work w=setup(A,d,n,B);
    offdiag_pass(d,n,B,w.T,w.panels,Ca,w.S); diag_pass_masked(d,n,B,w.T,w.panels,Ca,w.S);
    offdiag_pass(d,n,B,w.T,w.panels,Cb,w.S); diag_pass_paired(d,n,B,w.T,w.panels,Cb,w.S);
    double ra,rb; size_t ia,ib;
    int ba=cmp_bits(Cr,Ca,m,&ra,&ia), bb=cmp_bits(Cr,Cb,m,&rb,&ib);
    printf("  n=%-5d d=%-4d B=%-3d T=%-3d %-5s last=%-3d  armA %-10s (maxrel %.2e)  armB %-10s (maxrel %.2e)\n",
           n,d,B,w.T,(w.T&1)?"odd":"even",n-(w.T-1)*B,
           ba?"DIFF":"bit-exact",ra, bb?"DIFF":"bit-exact",rb);
    if(ba) printf("      armA first diff at cell %zu\n",ia);
    if(bb) printf("      armB first diff at cell %zu\n",ib);
    _mm_free(A);_mm_free(Cr);_mm_free(Ca);_mm_free(Cb);_mm_free(w.panels);_mm_free(w.S);
}

static void run_time(int n,int d,int B,int reps,int with_ref){
    size_t m=ncells(n);
    double*A=(double*)_mm_malloc((size_t)d*n*sizeof(double),64);
    fill_int(A,(size_t)d*n,7u);
    double*C=(double*)_mm_malloc(m*sizeof(double),64);
    Work w=setup(A,d,n,B);
    double useful=2.0*(double)m*(double)d;
    double best;
    printf("\n  --- n=%d d=%d B=%d (T=%d, cells=%zu, useful=%.1f MFLOP) ---\n",n,d,B,w.T,m,useful*1e-6);
    printf("  %-26s %10s %10s %9s %8s\n","variant","ms","ns/cell","GFLOP/s","%peak");
    if(with_ref){
        best=1e30; for(int r=0;r<2;r++){ double t=now_s(); ref_former(A,d,n,C); t=now_s()-t; if(t<best)best=t; }
        printf("  %-26s %10.3f %10.3f %9.2f %8.1f\n","reference (triangular)",best*1e3,best*1e9/(double)m,useful/best*1e-9,100.0*useful/best/64e9);
    }
    best=1e30; for(int r=0;r<reps;r++){ double t=now_s(); offdiag_pass(d,n,B,w.T,w.panels,C,w.S); t=now_s()-t; if(t<best)best=t; }
    double toff=best;
    printf("  %-26s %10.3f %10s %9s %8s\n","  [shared off-diag pass]",toff*1e3,"-","-","-");
    best=1e30; for(int r=0;r<reps;r++){ double t=now_s(); diag_pass_masked(d,n,B,w.T,w.panels,C,w.S); t=now_s()-t; if(t<best)best=t; }
    double tdA=best;
    best=1e30; for(int r=0;r<reps;r++){ double t=now_s(); diag_pass_paired(d,n,B,w.T,w.panels,C,w.S); t=now_s()-t; if(t<best)best=t; }
    double tdB=best;
    printf("  %-26s %10.3f %10s %9s %8s\n","  [diag pass, armA masked]",tdA*1e3,"-","-","-");
    printf("  %-26s %10.3f %10s %9s %8s   (diag %.2fx)\n","  [diag pass, armB paired]",tdB*1e3,"-","-","-",tdA/tdB);
    double ta=toff+tdA, tb=toff+tdB;
    printf("  %-26s %10.3f %10.3f %9.2f %8.1f\n","armA dense+masked (total)",ta*1e3,ta*1e9/(double)m,useful/ta*1e-9,100.0*useful/ta/64e9);
    printf("  %-26s %10.3f %10.3f %9.2f %8.1f   (overall %.3fx)\n","armB paired  (total)",tb*1e3,tb*1e9/(double)m,useful/tb*1e-9,100.0*useful/tb/64e9,ta/tb);
    _mm_free(A);_mm_free(C);_mm_free(w.panels);_mm_free(w.S);
}

/* how many register tiles straddle the staircase, for a given B */
static int straddle_tiles(int B){
    int c=0;
    for(int a0=0;a0<B;a0+=MR)
        for(int b0=0;b0<B;b0+=NR)
            if(!(b0>=a0+MR-1) && !(b0+NR-1<a0)) c++;
    return c;
}

static int dcmp(const void*x,const void*y){ double a=*(const double*)x,b=*(const double*)y;
                                            return (a<b)?-1:((a>b)?1:0); }

/* Isolate the diagonal pass. A and B are timed ALTERNATELY inside one loop and
 * reduced by MEDIAN, so drift from other load on the box cancels between arms. */
static void sweep_diag(int n){
    enum { REPS = 15 };
    double va[REPS], vb[REPS];
    printf("\nDIAGONAL-PASS ISOLATION (n=%d, median of %d interleaved reps)\n",n,(int)REPS);
    printf("  %-4s %-5s %6s %8s %10s %10s %8s %8s %8s %8s\n",
           "B","d","tiles","stradl","armA ms","armB ms","meas x","flop x","A GF/s","B GF/s");
    int Bs[4]={24,48,96,192};
    int ds[3]={64,128,256};
    for(int bi=0;bi<4;bi++){
        int B=Bs[bi];
        int tiles=(B/MR)*(B/NR), st=straddle_tiles(B);
        for(int di=0;di<3;di++){
            int d=ds[di];
            double*A=(double*)_mm_malloc((size_t)d*n*sizeof(double),64);
            fill_int(A,(size_t)d*n,7u);
            double*C=(double*)_mm_malloc(ncells(n)*sizeof(double),64);
            Work w=setup(A,d,n,B);
            diag_pass_masked(d,n,B,w.T,w.panels,C,w.S);   /* warm */
            diag_pass_paired(d,n,B,w.T,w.panels,C,w.S);
            for(int r=0;r<REPS;r++){
                BARRIER();
                double t=now_s(); diag_pass_masked(d,n,B,w.T,w.panels,C,w.S); va[r]=now_s()-t;
                OBSERVE(C); BARRIER();
                t=now_s();        diag_pass_paired(d,n,B,w.T,w.panels,C,w.S); vb[r]=now_s()-t;
                OBSERVE(C); BARRIER();
            }
            qsort(va,REPS,sizeof(double),dcmp); qsort(vb,REPS,sizeof(double),dcmp);
            double tA=va[REPS/2], tB=vb[REPS/2];
            double fA=(double)w.T*(double)tiles*MR*NR*d*2.0;
            double fB=(double)(w.T/2)*(double)(tiles+st)*MR*NR*d*2.0
                    + (double)(w.T&1)*(double)tiles*MR*NR*d*2.0
                    + (double)(w.T/2)*B*d*2.0;
            printf("  %-4d %-5d %6d %8d %10.3f %10.3f %8.3f %8.3f %8.2f %8.2f\n",
                   B,d,tiles,st,tA*1e3,tB*1e3,tA/tB,fA/fB,fA/tA*1e-9,fB/tB*1e-9);
            _mm_free(A);_mm_free(C);_mm_free(w.panels);_mm_free(w.S);
        }
    }
}

/* DIAGNOSTIC: run the paired path with Ip == I, i.e. pair every diagonal block
 * with ITSELF.  The answer is garbage, but the instruction mix, tile
 * classification, double-computed straddle tiles, merge and scatter are all
 * identical to armB -- the ONLY difference is that a single panel now feeds both
 * operand streams (as it does in armA).  If the rate jumps back to armA levels,
 * the armB rate deficit is dual-panel cache pressure, not the staircase logic. */
static void selfpair_probe(int n,int d,int B){
    enum { REPS = 15 };
    double v[REPS];
    double*A=(double*)_mm_malloc((size_t)d*n*sizeof(double),64);
    fill_int(A,(size_t)d*n,7u);
    double*C=(double*)_mm_malloc(ncells(n)*sizeof(double),64);
    Work w=setup(A,d,n,B);
    int tiles=(B/MR)*(B/NR), st=straddle_tiles(B);
    for(int r=0;r<REPS;r++){
        double t=now_s();
        for(int i=0;i<w.T/2;i++) diag_paired(d,n,B,i,i,w.panels,C,w.S);
        v[r]=now_s()-t;
    }
    qsort(v,REPS,sizeof(double),dcmp);
    double tS=v[REPS/2];
    double fB=(double)(w.T/2)*(double)(tiles+st)*MR*NR*d*2.0 + (double)(w.T/2)*B*d*2.0;
    printf("  B=%-4d d=%-4d  self-paired (1 panel) %8.3f ms  -> %6.2f GF/s\n",
           B,d,tS*1e3,fB/tS*1e-9);
    _mm_free(A);_mm_free(C);_mm_free(w.panels);_mm_free(w.S);
}

/* Whole-problem A/B, interleaved + median. */
static void sweep_total(int n,int d,int B){
    enum { REPS = 11 };
    double va[REPS], vb[REPS];
    size_t m=ncells(n);
    double*A=(double*)_mm_malloc((size_t)d*n*sizeof(double),64);
    fill_int(A,(size_t)d*n,7u);
    double*C=(double*)_mm_malloc(m*sizeof(double),64);
    Work w=setup(A,d,n,B);
    double useful=2.0*(double)m*(double)d;
    for(int r=0;r<REPS;r++){
        BARRIER();
        double t=now_s();
        offdiag_pass(d,n,B,w.T,w.panels,C,w.S); diag_pass_masked(d,n,B,w.T,w.panels,C,w.S);
        va[r]=now_s()-t;
        OBSERVE(C); BARRIER();
        t=now_s();
        offdiag_pass(d,n,B,w.T,w.panels,C,w.S); diag_pass_paired(d,n,B,w.T,w.panels,C,w.S);
        vb[r]=now_s()-t;
        OBSERVE(C); BARRIER();
    }
    qsort(va,REPS,sizeof(double),dcmp); qsort(vb,REPS,sizeof(double),dcmp);
    double tA=va[REPS/2], tB=vb[REPS/2];
    double fdiag=2.0/((double)w.T+1.0);
    printf("  n=%-5d d=%-4d B=%-4d T=%-3d  armA %8.3f ms (%6.2f ns/cell, %5.2f GF/s, %4.1f%%peak)"
           "  armB %8.3f ms (%6.2f ns/cell, %5.2f GF/s, %4.1f%%peak)  ratio %.4f   diag-share %.1f%%\n",
           n,d,B,w.T,
           tA*1e3, tA*1e9/(double)m, useful/tA*1e-9, 100.0*useful/tA/64e9,
           tB*1e3, tB*1e9/(double)m, useful/tB*1e-9, 100.0*useful/tB/64e9,
           tA/tB, 100.0*fdiag);
    _mm_free(A);_mm_free(C);_mm_free(w.panels);_mm_free(w.S);
}

int main(int argc,char**argv){
    (void)argc;(void)argv;
    printf("paired-triangle microkernel  MR=%d NR=%d  (peak assumed 64.0 GFLOP/s @ 4.0 GHz)\n",MR,NR);
    printf("\nCORRECTNESS (integer-valued inputs in [-4,4], all sums exact => bitwise comparison)\n");
    run_correct(1008,100,48,0);   /* T odd,  exact edge  */
    run_correct(1000,100,48,0);   /* T odd,  ragged edge */
    run_correct( 960,100,48,0);   /* T even, exact edge  */
    run_correct(1000,100,24,0);   /* T even, ragged edge, smaller B */
    run_correct( 337, 53,24,0);   /* awkward everything  */
    printf("\nCORRECTNESS (random doubles in [-1,1]; identical t-order => still expect bitwise)\n");
    run_correct(1000,100,48,1);
    run_correct( 337, 53,24,1);
    printf("\nTIMING (best of a handful of reps)\n");
    run_time(768,128,48,10,1);
    sweep_diag(1536);
    puts("");
    puts("WHOLE-PROBLEM A/B (median of 11 interleaved reps)");
    sweep_total(1536,128,24);
    sweep_total(1536,128,48);
    sweep_total(1536,128,96);
    sweep_total(768,128,24);
    sweep_total(768,128,48);
    sweep_total(768,128,96);
    sweep_total(512,128,24);
    sweep_total(512,128,48);
    sweep_total(512,128,96);
    sweep_total(512,128,192);
    puts("");
    puts("SELF-PAIR PROBE (armB code path, single panel; wrong answer, right instruction mix)");
    selfpair_probe(1536,128,48);
    selfpair_probe(1536,128,96);
    selfpair_probe(1536,128,192);
    return 0;
}
