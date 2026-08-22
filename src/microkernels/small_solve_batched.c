/* batched.c -- Arm C: B independent small dense solves.
 *
 * Layouts
 *   AoS   : system b at A[b*n*n + r*n + c]              (each matrix contiguous)
 *   SoA   : A[(r*n+c)*B + b]                            (full interleave)
 *   BLK4  : A[(b/4)*n*n*4 + (r*n+c)*4 + (b%4)]          (interleave in groups of 4)
 *
 * Arms
 *   H     AoS, heap-allocating runtime-n LU per system   (the emitted-code model)
 *   S     AoS, scalar fixed-size Cholesky
 *   Slu   AoS, scalar fixed-size LU + partial pivoting
 *   G     AoS, gather 4 systems into YMM on the fly, vector Cholesky, scatter back
 *         (identical arithmetic to V/K -- isolates SHUFFLE cost from SIMD gain)
 *   V     SoA, vector Cholesky, 4 systems per YMM, zero shuffles
 *   K     BLK4, vector Cholesky, zero shuffles, one contiguous stream
 *   Klu   BLK4, vector LU with blend-based partial pivoting
 *   Xf    cost of transposing AoS -> BLK4 once
 *
 * usage: batched.exe [B]
 */
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <math.h>
#include <immintrin.h>
#include <windows.h>

#define BARRIER() asm volatile("" ::: "memory")
static double now_s(void){
    LARGE_INTEGER f,c; QueryPerformanceFrequency(&f); QueryPerformanceCounter(&c);
    return (double)c.QuadPart/(double)f.QuadPart;
}
static unsigned long long rs = 0x243F6A8885A308D3ull;
static double rnd(void){ rs^=rs<<13; rs^=rs>>7; rs^=rs<<17;
    return ((double)(rs>>11)*(1.0/9007199254740992.0))*2.0-1.0; }

/* ---------------------------------------------------- the emitted-code model */
__attribute__((noinline,noclone))
static int ref_heap(const double *A,const double *b,double *x,int n)
{
    double *M=(double*)malloc((size_t)n*n*sizeof(double));
    double *r=(double*)malloc((size_t)n*sizeof(double));
    if(!M||!r){free(M);free(r);return -1;}
    memcpy(M,A,(size_t)n*n*sizeof(double)); memcpy(r,b,(size_t)n*sizeof(double));
    for(int k=0;k<n;k++){
        int p=k; double mx=fabs(M[k*n+k]);
        for(int i=k+1;i<n;i++){double v=fabs(M[i*n+k]); if(v>mx){mx=v;p=i;}}
        if(mx==0.0){free(M);free(r);return 1;}
        if(p!=k){ for(int j=0;j<n;j++){double t=M[k*n+j];M[k*n+j]=M[p*n+j];M[p*n+j]=t;}
                  double t=r[k];r[k]=r[p];r[p]=t; }
        double inv=1.0/M[k*n+k];
        for(int i=k+1;i<n;i++){ double f=M[i*n+k]*inv;
            for(int j=k+1;j<n;j++) M[i*n+j]-=f*M[k*n+j];
            r[i]-=f*r[k]; }
    }
    for(int i=n-1;i>=0;i--){ double s=r[i];
        for(int j=i+1;j<n;j++) s-=M[i*n+j]*x[j];
        x[i]=s/M[i*n+i]; }
    free(M); free(r); return 0;
}

/* ------------------------------------------------- scalar fixed-size kernels */
#define SCHOL(N)                                                              \
static inline __attribute__((always_inline))                                  \
int schol_##N(const double *restrict A,const double *restrict b,double *restrict x){ \
    double L[N*N], y[N];                                                      \
    for(int j=0;j<N;j++){                                                     \
        double s=A[j*N+j];                                                    \
        for(int k=0;k<j;k++) s-=L[j*N+k]*L[j*N+k];                            \
        if(!(s>0.0)) return 1;                                                \
        double d=sqrt(s), id=1.0/d; L[j*N+j]=d;                               \
        for(int i=j+1;i<N;i++){ double t=A[i*N+j];                            \
            for(int k=0;k<j;k++) t-=L[i*N+k]*L[j*N+k];                        \
            L[i*N+j]=t*id; }                                                  \
    }                                                                         \
    for(int i=0;i<N;i++){ double s=b[i];                                      \
        for(int k=0;k<i;k++) s-=L[i*N+k]*y[k];                                \
        y[i]=s/L[i*N+i]; }                                                    \
    for(int i=N-1;i>=0;i--){ double s=y[i];                                   \
        for(int k=i+1;k<N;k++) s-=L[k*N+i]*x[k];                              \
        x[i]=s/L[i*N+i]; }                                                    \
    return 0; }

#define SLU(N)                                                                \
static inline __attribute__((always_inline))                                  \
int slu_##N(const double *restrict A,const double *restrict b,double *restrict x){ \
    double M[N*N], r[N];                                                      \
    for(int i=0;i<N*N;i++) M[i]=A[i];                                         \
    for(int i=0;i<N;i++) r[i]=b[i];                                           \
    for(int k=0;k<N;k++){                                                     \
        int p=k; double mx=fabs(M[k*N+k]);                                    \
        for(int i=k+1;i<N;i++){ double v=fabs(M[i*N+k]); if(v>mx){mx=v;p=i;} }\
        if(mx==0.0) return 1;                                                 \
        if(p!=k){ for(int j=0;j<N;j++){double t=M[k*N+j];M[k*N+j]=M[p*N+j];M[p*N+j]=t;} \
                  double t=r[k];r[k]=r[p];r[p]=t; }                           \
        double inv=1.0/M[k*N+k];                                              \
        for(int i=k+1;i<N;i++){ double f=M[i*N+k]*inv;                        \
            for(int j=k+1;j<N;j++) M[i*N+j]-=f*M[k*N+j];                      \
            r[i]-=f*r[k]; }                                                   \
    }                                                                         \
    for(int i=N-1;i>=0;i--){ double s=r[i];                                   \
        for(int j=i+1;j<N;j++) s-=M[i*N+j]*x[j];                              \
        x[i]=s/M[i*N+i]; }                                                    \
    return 0; }

/* --------------------------------------------------------- vector kernels */
/* Operate on Av[N*N] / bv[N] of __m256d = 4 systems in lockstep. The arithmetic
   is op-for-op the scalar kernel above, so results are expected BITWISE equal. */
#define VCHOL_CORE(N)                                                         \
    __m256d L[N*N], y[N];                                                     \
    const __m256d one=_mm256_set1_pd(1.0);                                    \
    for(int j=0;j<N;j++){                                                     \
        __m256d s=Av[j*N+j];                                                  \
        for(int k=0;k<j;k++) s=_mm256_fnmadd_pd(L[j*N+k],L[j*N+k],s);         \
        __m256d d=_mm256_sqrt_pd(s), id=_mm256_div_pd(one,d);                 \
        L[j*N+j]=d;                                                           \
        for(int i=j+1;i<N;i++){ __m256d t=Av[i*N+j];                          \
            for(int k=0;k<j;k++) t=_mm256_fnmadd_pd(L[i*N+k],L[j*N+k],t);     \
            L[i*N+j]=_mm256_mul_pd(t,id); }                                   \
    }                                                                         \
    for(int i=0;i<N;i++){ __m256d s=bv[i];                                    \
        for(int k=0;k<i;k++) s=_mm256_fnmadd_pd(L[i*N+k],y[k],s);             \
        y[i]=_mm256_div_pd(s,L[i*N+i]); }                                     \
    for(int i=N-1;i>=0;i--){ __m256d s=y[i];                                  \
        for(int k=i+1;k<N;k++) s=_mm256_fnmadd_pd(L[k*N+i],xv[k],s);          \
        xv[i]=_mm256_div_pd(s,L[i*N+i]); }

/* LU with per-lane partial pivoting. Lanes diverge, so the pivot is a BLEND:
   in SIMD there is no branch to lose, which is exactly the opposite of scalar. */
#define VLU_CORE(N)                                                           \
    __m256d M[N*N], r[N];                                                     \
    const __m256d one=_mm256_set1_pd(1.0), sgn=_mm256_set1_pd(-0.0);          \
    for(int i=0;i<N*N;i++) M[i]=Av[i];                                        \
    for(int i=0;i<N;i++) r[i]=bv[i];                                          \
    for(int k=0;k<N;k++){                                                     \
        for(int i=k+1;i<N;i++){                                               \
            __m256d ai=_mm256_andnot_pd(sgn,M[i*N+k]);                        \
            __m256d ak=_mm256_andnot_pd(sgn,M[k*N+k]);                        \
            __m256d ms=_mm256_cmp_pd(ai,ak,_CMP_GT_OQ);                       \
            for(int j=k;j<N;j++){ __m256d a1=M[k*N+j],a2=M[i*N+j];            \
                M[k*N+j]=_mm256_blendv_pd(a1,a2,ms);                          \
                M[i*N+j]=_mm256_blendv_pd(a2,a1,ms); }                        \
            __m256d t1=r[k],t2=r[i];                                          \
            r[k]=_mm256_blendv_pd(t1,t2,ms); r[i]=_mm256_blendv_pd(t2,t1,ms); \
        }                                                                     \
        __m256d inv=_mm256_div_pd(one,M[k*N+k]);                              \
        for(int i=k+1;i<N;i++){ __m256d f=_mm256_mul_pd(M[i*N+k],inv);        \
            for(int j=k+1;j<N;j++) M[i*N+j]=_mm256_fnmadd_pd(f,M[k*N+j],M[i*N+j]); \
            r[i]=_mm256_fnmadd_pd(f,r[k],r[i]); }                             \
    }                                                                         \
    for(int i=N-1;i>=0;i--){ __m256d s=r[i];                                  \
        for(int j=i+1;j<N;j++) s=_mm256_fnmadd_pd(M[i*N+j],xv[j],s);          \
        xv[i]=_mm256_div_pd(s,M[i*N+i]); }

/* ---------------------------------------------------------- batch drivers */
#define GENB(N)                                                               \
SCHOL(N) SLU(N)                                                               \
__attribute__((noinline,noclone))                                             \
static void b_heap_##N(const double*A,const double*b,double*x,size_t B){       \
    for(size_t s=0;s<B;s++) ref_heap(A+s*N*N,b+s*N,x+s*N,N); }                \
__attribute__((noinline,noclone))                                             \
static void b_schol_##N(const double*A,const double*b,double*x,size_t B){      \
    for(size_t s=0;s<B;s++) schol_##N(A+s*N*N,b+s*N,x+s*N); }                 \
__attribute__((noinline,noclone))                                             \
static void b_slu_##N(const double*A,const double*b,double*x,size_t B){        \
    for(size_t s=0;s<B;s++) slu_##N(A+s*N*N,b+s*N,x+s*N); }                   \
/* G: AoS in, hand-rolled 4-way gather, vector solve, scatter out */          \
__attribute__((noinline,noclone))                                             \
static void b_gath_##N(const double*A,const double*b,double*x,size_t B){       \
    for(size_t s=0;s<B;s+=4){                                                 \
        __m256d Av[N*N], bv[N], xv[N];                                        \
        const double *p0=A+(s+0)*N*N,*p1=A+(s+1)*N*N,*p2=A+(s+2)*N*N,*p3=A+(s+3)*N*N; \
        for(int e=0;e<N*N;e++) Av[e]=_mm256_set_pd(p3[e],p2[e],p1[e],p0[e]);  \
        const double *q0=b+(s+0)*N,*q1=b+(s+1)*N,*q2=b+(s+2)*N,*q3=b+(s+3)*N; \
        for(int e=0;e<N;e++)   bv[e]=_mm256_set_pd(q3[e],q2[e],q1[e],q0[e]);  \
        { VCHOL_CORE(N) }                                                     \
        double t[4];                                                          \
        for(int e=0;e<N;e++){ _mm256_storeu_pd(t,xv[e]);                      \
            x[(s+0)*N+e]=t[0]; x[(s+1)*N+e]=t[1];                             \
            x[(s+2)*N+e]=t[2]; x[(s+3)*N+e]=t[3]; }                           \
    } }                                                                       \
/* V: full SoA, contiguous loads, zero shuffles */                            \
__attribute__((noinline,noclone))                                             \
static void b_soa_##N(const double*A,const double*b,double*x,size_t B){        \
    for(size_t s=0;s<B;s+=4){                                                 \
        __m256d Av[N*N], bv[N], xv[N];                                        \
        for(int e=0;e<N*N;e++) Av[e]=_mm256_loadu_pd(A+(size_t)e*B+s);        \
        for(int e=0;e<N;e++)   bv[e]=_mm256_loadu_pd(b+(size_t)e*B+s);        \
        { VCHOL_CORE(N) }                                                     \
        for(int e=0;e<N;e++) _mm256_storeu_pd(x+(size_t)e*B+s,xv[e]);         \
    } }                                                                       \
/* K: interleaved in groups of 4, zero shuffles AND one sequential stream */  \
__attribute__((noinline,noclone))                                             \
static void b_blk_##N(const double*A,const double*b,double*x,size_t B){        \
    for(size_t g=0;g<B/4;g++){                                                \
        __m256d Av[N*N], bv[N], xv[N];                                        \
        const double *pa=A+g*(size_t)(N*N*4), *pb=b+g*(size_t)(N*4);          \
        double *px=x+g*(size_t)(N*4);                                         \
        for(int e=0;e<N*N;e++) Av[e]=_mm256_loadu_pd(pa+e*4);                 \
        for(int e=0;e<N;e++)   bv[e]=_mm256_loadu_pd(pb+e*4);                 \
        { VCHOL_CORE(N) }                                                     \
        for(int e=0;e<N;e++) _mm256_storeu_pd(px+e*4,xv[e]);                  \
    } }                                                                       \
__attribute__((noinline,noclone))                                             \
static void b_blklu_##N(const double*A,const double*b,double*x,size_t B){      \
    for(size_t g=0;g<B/4;g++){                                                \
        __m256d Av[N*N], bv[N], xv[N];                                        \
        const double *pa=A+g*(size_t)(N*N*4), *pb=b+g*(size_t)(N*4);          \
        double *px=x+g*(size_t)(N*4);                                         \
        for(int e=0;e<N*N;e++) Av[e]=_mm256_loadu_pd(pa+e*4);                 \
        for(int e=0;e<N;e++)   bv[e]=_mm256_loadu_pd(pb+e*4);                 \
        { VLU_CORE(N) }                                                       \
        for(int e=0;e<N;e++) _mm256_storeu_pd(px+e*4,xv[e]);                  \
    } }

GENB(2) GENB(3) GENB(4) GENB(6) GENB(8)

typedef void (*bfn)(const double*,const double*,double*,size_t);
#define PICK(name) \
static bfn pick_##name(int n){ switch(n){case 2:return b_##name##_2;case 3:return b_##name##_3; \
    case 4:return b_##name##_4;case 6:return b_##name##_6;default:return b_##name##_8;} }
PICK(heap) PICK(schol) PICK(slu) PICK(gath) PICK(soa) PICK(blk) PICK(blklu)

/* ------------------------------------------------------------- layout xf */
__attribute__((noinline,noclone))
static void aos_to_blk(const double*A,const double*b,double*Ab,double*bb,size_t B,int n)
{
    for(size_t g=0; g<B/4; g++)
        for(int l=0;l<4;l++){
            size_t s=g*4+l;
            for(int e=0;e<n*n;e++) Ab[g*(size_t)(n*n*4)+e*4+l]=A[s*(size_t)(n*n)+e];
            for(int e=0;e<n;e++)   bb[g*(size_t)(n*4)+e*4+l]  =b[s*(size_t)n+e];
        }
}
static void aos_to_soa(const double*A,const double*b,double*As,double*bs,size_t B,int n)
{
    for(size_t s=0;s<B;s++){
        for(int e=0;e<n*n;e++) As[(size_t)e*B+s]=A[s*(size_t)(n*n)+e];
        for(int e=0;e<n;e++)   bs[(size_t)e*B+s]=b[s*(size_t)n+e];
    }
}

/* ------------------------------------------------------------ verification */
static double residual_1(const double*A,const double*b,const double*x,int n)
{
    long double rmax=0,na=0,xm=0;
    for(int i=0;i<n;i++){ long double s=-(long double)b[i],rr=0;
        for(int j=0;j<n;j++){ s+=(long double)A[i*n+j]*(long double)x[j];
                              rr+=fabsl((long double)A[i*n+j]); }
        if(fabsl(s)>rmax)rmax=fabsl(s); if(rr>na)na=rr; }
    for(int i=0;i<n;i++) if(fabsl((long double)x[i])>xm) xm=fabsl((long double)x[i]);
    return (na==0||xm==0)?0.0:(double)(rmax/(na*xm));
}

static double *Aaos,*baos,*xaos,*Asoa,*bsoa,*xsoa,*Ablk,*bblk,*xblk,*xtmp;

int main(int argc,char**argv)
{
    size_t B = (argc>1)? (size_t)strtoull(argv[1],0,10) : 32768;
    B = (B/4)*4;
    const int ns[5]={2,3,4,6,8};
    const int reps=9;

    Aaos=malloc(B*64*sizeof(double)); baos=malloc(B*8*sizeof(double)); xaos=malloc(B*8*sizeof(double));
    Asoa=malloc(B*64*sizeof(double)); bsoa=malloc(B*8*sizeof(double)); xsoa=malloc(B*8*sizeof(double));
    Ablk=malloc(B*64*sizeof(double)); bblk=malloc(B*8*sizeof(double)); xblk=malloc(B*8*sizeof(double));
    xtmp=malloc(B*8*sizeof(double));
    if(!Aaos||!Asoa||!Ablk||!xtmp){ printf("oom\n"); return 1; }

    printf("# BATCHED  B=%zu systems, ns per solve, best of %d passes\n",B,reps);
    printf("# AoS working set: n=8 -> %.1f MB\n", (double)B*64*8/1048576.0);
    printf("%2s %9s %9s %9s %9s %9s %9s %9s %8s | %6s %6s %6s %6s\n",
           "n","H heap","S chol","Slu LU","G gather","V SoA","K blk4","Klu blk4","Xf conv",
           "H/K","S/K","G/K","H/Klu");
    double chk=0;
    double maxres[7]={0,0,0,0,0,0,0}; long long bitmm[7]={0,0,0,0,0,0,0}, bittot=0;

    for(int t=0;t<5;t++){
        int n=ns[t];
        rs = 0xC0FFEE1234567ull ^ (unsigned)n;
        /* well-conditioned SPD: A = M M^T + n I */
        for(size_t s=0;s<B;s++){
            double Mt[64];
            for(int i=0;i<n*n;i++) Mt[i]=rnd();
            for(int i=0;i<n;i++) for(int j=0;j<n;j++){
                double v=0; for(int k=0;k<n;k++) v+=Mt[i*n+k]*Mt[j*n+k];
                Aaos[s*(size_t)(n*n)+i*n+j]=v+((i==j)?(double)n:0.0);
            }
            for(int i=0;i<n;i++) baos[s*(size_t)n+i]=rnd();
        }
        aos_to_soa(Aaos,baos,Asoa,bsoa,B,n);
        aos_to_blk(Aaos,baos,Ablk,bblk,B,n);

        double tm[8];
        #define TIME(idx, CALL) do{ double best=1e30; \
            for(int rp=0;rp<reps;rp++){ double t0=now_s(); BARRIER(); CALL; BARRIER(); \
                double t1=now_s(); double d=(t1-t0)/(double)B*1e9; if(d<best)best=d; } \
            tm[idx]=best; }while(0)

        TIME(0, pick_heap(n)(Aaos,baos,xaos,B));
        /* reference solutions for verification */
        memcpy(xtmp,xaos,B*(size_t)n*sizeof(double));
        for(size_t s=0;s<B;s+=(B/97+1)){ double r=residual_1(Aaos+s*(size_t)(n*n),baos+s*(size_t)n,xtmp+s*(size_t)n,n); if(r>maxres[0])maxres[0]=r; }

        TIME(1, pick_schol(n)(Aaos,baos,xaos,B));
        TIME(2, pick_slu(n)(Aaos,baos,xaos,B));
        TIME(3, pick_gath(n)(Aaos,baos,xaos,B));
        TIME(4, pick_soa(n)(Asoa,bsoa,xsoa,B));
        TIME(5, pick_blk(n)(Ablk,bblk,xblk,B));
        TIME(6, pick_blklu(n)(Ablk,bblk,xblk,B));
        TIME(7, aos_to_blk(Aaos,baos,Ablk,bblk,B,n));

        /* --- verify: rerun each arm once, check residual + bitwise vs scalar chol --- */
        pick_schol(n)(Aaos,baos,xaos,B);                 /* xaos = scalar Cholesky */
        pick_gath(n)(Aaos,baos,xtmp,B);
        pick_soa(n)(Asoa,bsoa,xsoa,B);
        pick_blk(n)(Ablk,bblk,xblk,B);
        for(size_t s=0;s<B;s++){
            const double *Ai=Aaos+s*(size_t)(n*n), *bi=baos+s*(size_t)n;
            const double *xs=xaos+s*(size_t)n;
            double r;
            if((s % (B/97+1))==0){
                r=residual_1(Ai,bi,xs,n); if(r>maxres[1])maxres[1]=r;
                r=residual_1(Ai,bi,xtmp+s*(size_t)n,n); if(r>maxres[3])maxres[3]=r;
                double xv[8];
                for(int e=0;e<n;e++) xv[e]=xsoa[(size_t)e*B+s];
                r=residual_1(Ai,bi,xv,n); if(r>maxres[4])maxres[4]=r;
                size_t g=s/4,l=s%4;
                for(int e=0;e<n;e++) xv[e]=xblk[g*(size_t)(n*4)+e*4+l];
                r=residual_1(Ai,bi,xv,n); if(r>maxres[5])maxres[5]=r;
            }
            if(memcmp(xtmp+s*(size_t)n,xs,(size_t)n*sizeof(double))==0) bitmm[3]++;
            {   int eq=1; for(int e=0;e<n;e++) if(xsoa[(size_t)e*B+s]!=xs[e]) {eq=0;break;}
                bitmm[4]+=eq; }
            {   size_t g=s/4,l=s%4; int eq=1;
                for(int e=0;e<n;e++) if(xblk[g*(size_t)(n*4)+e*4+l]!=xs[e]) {eq=0;break;}
                bitmm[5]+=eq; }
            bittot++;
        }
        { static long long prevG=0,prevV=0,prevK=0,prevT=0;
          printf("   [n=%d bitwise vs scalar chol: G=%lld/%lld V=%lld/%lld K=%lld/%lld]\n",
                 n,bitmm[3]-prevG,bittot-prevT,bitmm[4]-prevV,bittot-prevT,bitmm[5]-prevK,bittot-prevT);
          prevG=bitmm[3];prevV=bitmm[4];prevK=bitmm[5];prevT=bittot; }
        /* Klu residual */
        pick_blklu(n)(Ablk,bblk,xblk,B);
        for(size_t s=0;s<B;s+=(B/97+1)){
            const double *Ai=Aaos+s*(size_t)(n*n), *bi=baos+s*(size_t)n;
            double xv[8]; size_t g=s/4,l=s%4;
            for(int e=0;e<n;e++) xv[e]=xblk[g*(size_t)(n*4)+e*4+l];
            double r=residual_1(Ai,bi,xv,n); if(r>maxres[6])maxres[6]=r;
        }
        for(size_t s=0;s<B;s+=1024) chk+=xaos[s*(size_t)n]+xblk[s*(size_t)n];

        printf("%2d %9.2f %9.2f %9.2f %9.2f %9.2f %9.2f %9.2f %8.2f | %6.1f %6.2f %6.2f %6.1f\n",
               n,tm[0],tm[1],tm[2],tm[3],tm[4],tm[5],tm[6],tm[7],
               tm[0]/tm[5], tm[1]/tm[5], tm[3]/tm[5], tm[0]/tm[6]);
    }
    printf("checksum %.15g\n",chk);
    printf("\n# residual (max, sampled) : H=%.2e S=%.2e G=%.2e V=%.2e K=%.2e Klu=%.2e\n",
           maxres[0],maxres[1],maxres[3],maxres[4],maxres[5],maxres[6]);
    printf("# bitwise vs scalar fixed Cholesky, over %lld solves: G=%lld V=%lld K=%lld\n",
           bittot,bitmm[3],bitmm[4],bitmm[5]);
    return 0;
}
