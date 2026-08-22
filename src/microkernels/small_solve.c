/* solve.c -- fixed-size small dense solve microkernels.
 *
 * Arms:
 *   R1  ref_heap   : LU w/ partial pivoting, runtime n, malloc workspace per call
 *                    (faithful model of the std::vector-allocating solve emit)
 *   R2  ref_stack  : IDENTICAL algorithm, VLA workspace (isolates the malloc cost)
 *   A   fix_lu     : n a compile-time constant, fully unrolled LU + partial pivoting
 *   A'  fix_lunp   : same, pivoting removed (isolates the pivot-branch cost)
 *   B   fix_chol   : n compile-time constant, unrolled LL^T Cholesky (SPD only)
 *   Ai  fix_lu_inl : arm A, always_inline (what a real emitter would do)
 *
 * All fixed arms are heap-free (stack/register workspace only).
 * usage: solve.exe acc | speed
 */
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <math.h>
#include <windows.h>

#define BARRIER() asm volatile("" ::: "memory")

static double now_s(void){
    LARGE_INTEGER f,c; QueryPerformanceFrequency(&f); QueryPerformanceCounter(&c);
    return (double)c.QuadPart/(double)f.QuadPart;
}

/* ------------------------------------------------------------------ rng */
static unsigned long long rs = 0x243F6A8885A308D3ull;
static double rnd(void){ /* uniform (-1,1) */
    rs ^= rs<<13; rs ^= rs>>7; rs ^= rs<<17;
    return ((double)(rs>>11) * (1.0/9007199254740992.0))*2.0 - 1.0;
}
static void rseed(unsigned long long s){ rs = s?s:1; }

/* ============================================================= REFERENCE */
/* n is a runtime value; workspace comes from malloc on every call. */
__attribute__((noinline,noclone))
static int ref_heap(const double *A, const double *b, double *x, int n)
{
    double *M = (double*)malloc((size_t)n*(size_t)n*sizeof(double));
    double *r = (double*)malloc((size_t)n*sizeof(double));
    if(!M||!r){ free(M); free(r); return -1; }
    memcpy(M, A, (size_t)n*(size_t)n*sizeof(double));
    memcpy(r, b, (size_t)n*sizeof(double));
    for(int k=0;k<n;k++){
        int p=k; double mx=fabs(M[k*n+k]);
        for(int i=k+1;i<n;i++){ double v=fabs(M[i*n+k]); if(v>mx){ mx=v; p=i; } }
        if(mx==0.0){ free(M); free(r); return 1; }
        if(p!=k){
            for(int j=0;j<n;j++){ double t=M[k*n+j]; M[k*n+j]=M[p*n+j]; M[p*n+j]=t; }
            double t=r[k]; r[k]=r[p]; r[p]=t;
        }
        double inv = 1.0/M[k*n+k];
        for(int i=k+1;i<n;i++){
            double f = M[i*n+k]*inv;
            for(int j=k+1;j<n;j++) M[i*n+j] -= f*M[k*n+j];
            r[i] -= f*r[k];
        }
    }
    for(int i=n-1;i>=0;i--){
        double s=r[i];
        for(int j=i+1;j<n;j++) s -= M[i*n+j]*x[j];
        x[i] = s/M[i*n+i];
    }
    free(M); free(r);
    return 0;
}

/* the same algorithm, still runtime n, but workspace is a fixed stack buffer.
   No malloc AND no VLA stack probe: this arm isolates the pure allocation cost. */
__attribute__((noinline,noclone))
static int ref_stack(const double *A, const double *b, double *x, int n)
{
    double M[64], r[8];
    memcpy(M, A, (size_t)n*(size_t)n*sizeof(double));
    memcpy(r, b, (size_t)n*sizeof(double));
    for(int k=0;k<n;k++){
        int p=k; double mx=fabs(M[k*n+k]);
        for(int i=k+1;i<n;i++){ double v=fabs(M[i*n+k]); if(v>mx){ mx=v; p=i; } }
        if(mx==0.0) return 1;
        if(p!=k){
            for(int j=0;j<n;j++){ double t=M[k*n+j]; M[k*n+j]=M[p*n+j]; M[p*n+j]=t; }
            double t=r[k]; r[k]=r[p]; r[p]=t;
        }
        double inv = 1.0/M[k*n+k];
        for(int i=k+1;i<n;i++){
            double f = M[i*n+k]*inv;
            for(int j=k+1;j<n;j++) M[i*n+j] -= f*M[k*n+j];
            r[i] -= f*r[k];
        }
    }
    for(int i=n-1;i>=0;i--){
        double s=r[i];
        for(int j=i+1;j<n;j++) s -= M[i*n+j]*x[j];
        x[i] = s/M[i*n+i];
    }
    return 0;
}

/* ======================================================= FIXED-SIZE ARMS */
/* Every loop bound below is a literal, so -O3 unrolls the whole nest and the
   workspace stays in registers / one fixed stack frame. Zero heap traffic. */

#define BODY_LU_PIV(N)                                                        \
    double M[N*N], r[N];                                                      \
    for(int i=0;i<N*N;i++) M[i]=A[i];                                         \
    for(int i=0;i<N;i++) r[i]=b[i];                                           \
    for(int k=0;k<N;k++){                                                     \
        int p=k; double mx=fabs(M[k*N+k]);                                    \
        for(int i=k+1;i<N;i++){ double v=fabs(M[i*N+k]); if(v>mx){mx=v;p=i;} }\
        if(mx==0.0) return 1;                                                 \
        if(p!=k){                                                             \
            for(int j=0;j<N;j++){ double t=M[k*N+j]; M[k*N+j]=M[p*N+j]; M[p*N+j]=t; } \
            double t=r[k]; r[k]=r[p]; r[p]=t;                                 \
        }                                                                     \
        double inv=1.0/M[k*N+k];                                              \
        for(int i=k+1;i<N;i++){                                               \
            double f=M[i*N+k]*inv;                                            \
            for(int j=k+1;j<N;j++) M[i*N+j]-=f*M[k*N+j];                      \
            r[i]-=f*r[k];                                                     \
        }                                                                     \
    }                                                                         \
    for(int i=N-1;i>=0;i--){                                                  \
        double s=r[i];                                                        \
        for(int j=i+1;j<N;j++) s-=M[i*N+j]*x[j];                              \
        x[i]=s/M[i*N+i];                                                      \
    }                                                                         \
    return 0;

#define BODY_LU_NOPIV(N)                                                      \
    double M[N*N], r[N];                                                      \
    for(int i=0;i<N*N;i++) M[i]=A[i];                                         \
    for(int i=0;i<N;i++) r[i]=b[i];                                           \
    for(int k=0;k<N;k++){                                                     \
        double inv=1.0/M[k*N+k];                                              \
        for(int i=k+1;i<N;i++){                                               \
            double f=M[i*N+k]*inv;                                            \
            for(int j=k+1;j<N;j++) M[i*N+j]-=f*M[k*N+j];                      \
            r[i]-=f*r[k];                                                     \
        }                                                                     \
    }                                                                         \
    for(int i=N-1;i>=0;i--){                                                  \
        double s=r[i];                                                        \
        for(int j=i+1;j<N;j++) s-=M[i*N+j]*x[j];                              \
        x[i]=s/M[i*N+i];                                                      \
    }                                                                         \
    return 0;

/* Exact branchless select: c ? a : b, via an integer bitmask. Bit-exact (it moves
   the operand's bits, it does not arithmetically blend them), and it gives gcc no
   branch to guess at. A plain `c?a:b` on doubles compiles to a BRANCH here, which
   is why this exists. */
static inline __attribute__((always_inline))
double dsel(int c, double a, double b){
    unsigned long long ua,ub,r; double d;
    __builtin_memcpy(&ua,&a,8); __builtin_memcpy(&ub,&b,8);
    unsigned long long m = (unsigned long long)0 - (unsigned long long)(c!=0);
    r = (ua & m) | (ub & ~m);
    __builtin_memcpy(&d,&r,8); return d;
}

/* Branchless partial pivoting: a max-bubble of unconditional conditional-swaps.
   The pivot INDEX is never a runtime value, so every subscript stays a literal
   after unrolling and gcc can keep the whole matrix in registers (SROA). The row
   finally sitting at k is still the argmax row, so the factorization is the same
   one arm A computes.  Columns < k are dead (the RHS is folded in as we go), so
   the swap starts at j = k. */
#define BODY_LU_BPIV(N)                                                       \
    double M[N*N], r[N];                                                      \
    for(int i=0;i<N*N;i++) M[i]=A[i];                                         \
    for(int i=0;i<N;i++) r[i]=b[i];                                           \
    for(int k=0;k<N;k++){                                                     \
        for(int i=k+1;i<N;i++){                                               \
            int sw = fabs(M[i*N+k]) > fabs(M[k*N+k]);                         \
            for(int j=k;j<N;j++){                                             \
                double a1=M[k*N+j], a2=M[i*N+j];                              \
                M[k*N+j] = sw?a2:a1; M[i*N+j] = sw?a1:a2;                     \
            }                                                                 \
            double t1=r[k],t2=r[i]; r[k]=sw?t2:t1; r[i]=sw?t1:t2;             \
        }                                                                     \
        if(M[k*N+k]==0.0) return 1;                                           \
        double inv=1.0/M[k*N+k];                                              \
        for(int i=k+1;i<N;i++){                                               \
            double f=M[i*N+k]*inv;                                            \
            for(int j=k+1;j<N;j++) M[i*N+j]-=f*M[k*N+j];                      \
            r[i]-=f*r[k];                                                     \
        }                                                                     \
    }                                                                         \
    for(int i=N-1;i>=0;i--){                                                  \
        double s=r[i];                                                        \
        for(int j=i+1;j<N;j++) s-=M[i*N+j]*x[j];                              \
        x[i]=s/M[i*N+i];                                                      \
    }                                                                         \
    return 0;

/* LL^T, lower triangle only; SPD needs no pivoting at all. */
#define BODY_CHOL(N)                                                          \
    double L[N*N];                                                            \
    for(int j=0;j<N;j++){                                                     \
        double s=A[j*N+j];                                                    \
        for(int k=0;k<j;k++) s-=L[j*N+k]*L[j*N+k];                            \
        if(!(s>0.0)) return 1;                                                \
        double d=sqrt(s), id=1.0/d;                                           \
        L[j*N+j]=d;                                                           \
        for(int i=j+1;i<N;i++){                                               \
            double t=A[i*N+j];                                                \
            for(int k=0;k<j;k++) t-=L[i*N+k]*L[j*N+k];                        \
            L[i*N+j]=t*id;                                                    \
        }                                                                     \
    }                                                                         \
    double y[N];                                                              \
    for(int i=0;i<N;i++){                                                     \
        double s=b[i];                                                        \
        for(int k=0;k<i;k++) s-=L[i*N+k]*y[k];                                \
        y[i]=s/L[i*N+i];                                                      \
    }                                                                         \
    for(int i=N-1;i>=0;i--){                                                  \
        double s=y[i];                                                        \
        for(int k=i+1;k<N;k++) s-=L[k*N+i]*x[k];                              \
        x[i]=s/L[i*N+i];                                                      \
    }                                                                         \
    return 0;

#define GEN(N)                                                                  \
__attribute__((noinline,noclone))                                               \
static int fix_lu_##N(const double *restrict A,const double *restrict b,double *restrict x){ BODY_LU_PIV(N) } \
__attribute__((noinline,noclone))                                               \
static int fix_lunp_##N(const double *restrict A,const double *restrict b,double *restrict x){ BODY_LU_NOPIV(N) } \
__attribute__((noinline,noclone))                                               \
static int fix_lubp_##N(const double *restrict A,const double *restrict b,double *restrict x){ BODY_LU_BPIV(N) } \
__attribute__((noinline,noclone))                                               \
static int fix_chol_##N(const double *restrict A,const double *restrict b,double *restrict x){ BODY_CHOL(N) } \
static inline __attribute__((always_inline))                                    \
int fix_lu_inl_##N(const double *restrict A,const double *restrict b,double *restrict x){ BODY_LU_PIV(N) } \
static inline __attribute__((always_inline))                                    \
int fix_chol_inl_##N(const double *restrict A,const double *restrict b,double *restrict x){ BODY_CHOL(N) }

GEN(2) GEN(3) GEN(4) GEN(6) GEN(8)

typedef int (*solvefn)(const double*,const double*,double*);
static solvefn pick_lu (int n){ switch(n){case 2:return fix_lu_2;case 3:return fix_lu_3;case 4:return fix_lu_4;case 6:return fix_lu_6;default:return fix_lu_8;} }
static solvefn pick_lunp(int n){ switch(n){case 2:return fix_lunp_2;case 3:return fix_lunp_3;case 4:return fix_lunp_4;case 6:return fix_lunp_6;default:return fix_lunp_8;} }
static solvefn pick_lubp(int n){ switch(n){case 2:return fix_lubp_2;case 3:return fix_lubp_3;case 4:return fix_lubp_4;case 6:return fix_lubp_6;default:return fix_lubp_8;} }
static solvefn pick_chol(int n){ switch(n){case 2:return fix_chol_2;case 3:return fix_chol_3;case 4:return fix_chol_4;case 6:return fix_chol_6;default:return fix_chol_8;} }

/* =================================================== long double oracle */
/* 80-bit LU with partial pivoting: truth for forward-error reporting. */
static int ld_solve(const double *A, const double *b, long double *x, int n)
{
    long double M[64], r[8];
    for(int i=0;i<n*n;i++) M[i]=(long double)A[i];
    for(int i=0;i<n;i++) r[i]=(long double)b[i];
    for(int k=0;k<n;k++){
        int p=k; long double mx=fabsl(M[k*n+k]);
        for(int i=k+1;i<n;i++){ long double v=fabsl(M[i*n+k]); if(v>mx){mx=v;p=i;} }
        if(mx==0.0L) return 1;
        if(p!=k){ for(int j=0;j<n;j++){ long double t=M[k*n+j]; M[k*n+j]=M[p*n+j]; M[p*n+j]=t; }
                  long double t=r[k]; r[k]=r[p]; r[p]=t; }
        long double inv=1.0L/M[k*n+k];
        for(int i=k+1;i<n;i++){
            long double f=M[i*n+k]*inv;
            for(int j=k+1;j<n;j++) M[i*n+j]-=f*M[k*n+j];
            r[i]-=f*r[k];
        }
    }
    for(int i=n-1;i>=0;i--){
        long double s=r[i];
        for(int j=i+1;j<n;j++) s-=M[i*n+j]*x[j];
        x[i]=s/M[i*n+i];
    }
    return 0;
}

/* cond_inf via an explicit long-double inverse (n<=8, cheap) */
static double cond_inf(const double *A, int n)
{
    long double Inv[64], col[8];
    for(int c=0;c<n;c++){
        double be[8]; for(int i=0;i<n;i++) be[i]= (i==c)?1.0:0.0;
        if(ld_solve(A,be,col,n)) return INFINITY;
        for(int i=0;i<n;i++) Inv[i*n+c]=col[i];
    }
    long double na=0, ni=0;
    for(int i=0;i<n;i++){ long double s=0,t=0;
        for(int j=0;j<n;j++){ s+=fabsl((long double)A[i*n+j]); t+=fabsl(Inv[i*n+j]); }
        if(s>na) na=s; if(t>ni) ni=t; }
    return (double)(na*ni);
}

/* relative residual ||Ax-b||_inf / (||A||_inf ||x||_inf), evaluated in 80-bit */
static double residual(const double *A,const double *b,const double *x,int n)
{
    long double rmax=0, na=0, xm=0;
    for(int i=0;i<n;i++){
        long double s=-(long double)b[i], rsum=0;
        for(int j=0;j<n;j++){ s+=(long double)A[i*n+j]*(long double)x[j];
                              rsum+=fabsl((long double)A[i*n+j]); }
        if(fabsl(s)>rmax) rmax=fabsl(s);
        if(rsum>na) na=rsum;
    }
    for(int i=0;i<n;i++) if(fabsl((long double)x[i])>xm) xm=fabsl((long double)x[i]);
    if(na==0||xm==0) return 0.0;
    return (double)(rmax/(na*xm));
}
static double fwderr(const double *x, const long double *xt, int n)
{
    long double e=0,m=0;
    for(int i=0;i<n;i++){ long double d=fabsl((long double)x[i]-xt[i]); if(d>e)e=d;
                          if(fabsl(xt[i])>m) m=fabsl(xt[i]); }
    return m==0?0.0:(double)(e/m);
}

/* ================================================= matrix generators */
enum { CASE_SPD=0, CASE_HILB=1, CASE_NEAR=2, CASE_GEN=3, NCASE=4 };
static const char *casename[NCASE]={"SPD well-cond","Hilbert SPD ill","near-singular SPD","random general"};

static void genmat(double *A,int n,int which)
{
    double Mt[64];
    switch(which){
    case CASE_SPD:
        for(int i=0;i<n*n;i++) Mt[i]=rnd();
        for(int i=0;i<n;i++) for(int j=0;j<n;j++){
            double s=0; for(int k=0;k<n;k++) s+=Mt[i*n+k]*Mt[j*n+k];
            A[i*n+j]=s + ((i==j)?(double)n:0.0);
        }
        break;
    case CASE_HILB:
        for(int i=0;i<n;i++) for(int j=0;j<n;j++) A[i*n+j]=1.0/(double)(i+j+1);
        break;
    case CASE_NEAR: { /* last factor row is a near-copy of the first: tiny eigenvalue */
        for(int i=0;i<n*n;i++) Mt[i]=rnd();
        for(int j=0;j<n;j++) Mt[(n-1)*n+j]=Mt[0*n+j]*(1.0+1e-9*rnd());
        for(int i=0;i<n;i++) for(int j=0;j<n;j++){
            double s=0; for(int k=0;k<n;k++) s+=Mt[i*n+k]*Mt[j*n+k];
            A[i*n+j]=s;
        }
        break; }
    default:
        for(int i=0;i<n*n;i++) A[i]=rnd();
        break;
    }
}

/* ============================================================= accuracy */
#define NARM 5
static const char *armname[NARM]={"R1/R2 reference LU","A  fixed LU argmax-piv",
                                 "A2 fixed LU branchless-piv","Ap fixed LU NO pivot",
                                 "B  fixed Cholesky"};
static void run_acc(void)
{
    const int ns[5]={2,3,4,6,8};
    long long bitmatch[NARM]={0,0,0,0,0}, bittot=0;
    printf("# ACCURACY (max over trials; resid = ||Ax-b||inf/(||A||inf ||x||inf), fwd = rel err vs 80-bit LU)\n");
    printf("%-18s %2s %10s", "case","n","cond_inf");
    for(int a=0;a<NARM;a++) printf(" | %-24s", armname[a]);
    printf("\n%-18s %2s %10s","","","");
    for(int a=0;a<NARM;a++) printf(" | %11s %12s","resid","fwd");
    printf("\n");
    for(int ci=0; ci<NCASE; ci++){
        for(int t=0;t<5;t++){
            int n=ns[t];
            int trials = (ci==CASE_HILB)?1:64;
            double mr[NARM]={0}, mf[NARM]={0};
            int fail[NARM]={0}, ok=0;
            double cond=0;
            rseed(0x9E3779B97F4A7C15ull ^ (unsigned)(n*131+ci));
            for(int it=0; it<trials; it++){
                double A[64], b[8], xs[NARM][8]; long double xt[8];
                genmat(A,n,ci);
                for(int i=0;i<n;i++) b[i]=rnd();
                if(it==0) cond=cond_inf(A,n);
                if(ld_solve(A,b,xt,n)) continue;
                ok++;
                int rc[NARM];
                rc[0]=ref_heap(A,b,xs[0],n);
                rc[1]=pick_lu(n)(A,b,xs[1]);
                rc[2]=pick_lubp(n)(A,b,xs[2]);
                rc[3]=pick_lunp(n)(A,b,xs[3]);
                rc[4]= (ci==CASE_GEN)? -1 : pick_chol(n)(A,b,xs[4]);
                for(int a=0;a<NARM;a++){
                    if(rc[a]<0){ fail[a]=-1; continue; }
                    if(rc[a]){ fail[a]++; continue; }
                    double r=residual(A,b,xs[a],n), f=fwderr(xs[a],xt,n);
                    if(r>mr[a])mr[a]=r; if(f>mf[a])mf[a]=f;
                    if(memcmp(xs[a],xs[0],(size_t)n*sizeof(double))==0) bitmatch[a]++;
                }
                bittot++;
            }
            printf("%-18s %2d %10.2e", casename[ci], n, cond);
            for(int a=0;a<NARM;a++){
                if(fail[a]<0) printf(" | %11s %12s","n/a","n/a");
                else if(fail[a]>=ok) printf(" | %11s %12s","REFUSED","-");
                else printf(" | %11.2e %12.2e%s", mr[a], mf[a], fail[a]? "*":" ");
            }
            printf("\n");
        }
    }
    printf("(REFUSED / * = kernel declined some or all trials: Cholesky rejects a matrix that is not numerically SPD)\n");
    printf("\n# BITWISE identity with the heap reference, over all %lld solves:\n", bittot);
    for(int a=0;a<NARM;a++)
        printf("   %-28s %6lld / %lld  (%.1f%%)\n", armname[a], bitmatch[a], bittot,
               100.0*(double)bitmatch[a]/(double)bittot);
}

/* ================================================================ speed */
#define NSYS 512
static double gA[NSYS*64], gb[NSYS*8], gx[NSYS*8];
static volatile int vn;

static double bench_ref(int heap,int reps,double *chk)
{
    double best=1e30;
    for(int rep=0; rep<reps; rep++){
        double t0=now_s(); BARRIER();
        for(int s=0;s<NSYS;s++){
            if(heap) ref_heap(&gA[s*64], &gb[s*8], &gx[s*8], vn);
            else     ref_stack(&gA[s*64], &gb[s*8], &gx[s*8], vn);
        }
        BARRIER(); double t1=now_s();
        double d=(t1-t0)/NSYS*1e9; if(d<best) best=d;
    }
    for(int s=0;s<NSYS;s++) *chk += gx[s*8];
    return best;
}
static double bench_fn(solvefn f,int reps,double *chk)
{
    double best=1e30;
    for(int rep=0; rep<reps; rep++){
        double t0=now_s(); BARRIER();
        for(int s=0;s<NSYS;s++) f(&gA[s*64], &gb[s*8], &gx[s*8]);
        BARRIER(); double t1=now_s();
        double d=(t1-t0)/NSYS*1e9; if(d<best) best=d;
    }
    for(int s=0;s<NSYS;s++) *chk += gx[s*8];
    return best;
}
#define BENCH_INL(N, FN)                                                     \
    do{ double best=1e30;                                                    \
        for(int rep=0;rep<reps;rep++){                                       \
            double t0=now_s(); BARRIER();                                    \
            for(int s=0;s<NSYS;s++) FN##_##N(&gA[s*64],&gb[s*8],&gx[s*8]);   \
            BARRIER(); double t1=now_s();                                    \
            double d=(t1-t0)/NSYS*1e9; if(d<best) best=d; }                  \
        for(int s=0;s<NSYS;s++) chk+=gx[s*8];                                \
        out=best; }while(0)

static void run_speed(void)
{
    const int ns[5]={2,3,4,6,8};
    const int reps=25;
    double chk=0;
    printf("# SPEED  ns per solve, best of %d passes over %d SPD systems (L1/L2 resident)\n",reps,NSYS);
    printf("%2s %9s %9s %9s %9s %9s %9s %9s %9s | %7s %7s %7s %7s\n",
           "n","R1 heap","R2 stk","A luPiv","A2 luBP","Ap luNP","B chol","Ai luInl","Bi cholI",
           "R1/A2","R1/B","R2/A2","R1/Bi");
    for(int t=0;t<5;t++){
        int n=ns[t]; vn=n;
        rseed(0xDEADBEEF12345ull ^ (unsigned)n);
        for(int s=0;s<NSYS;s++){ genmat(&gA[s*64],n,CASE_SPD); for(int i=0;i<n;i++) gb[s*8+i]=rnd(); }
        double r1=bench_ref(1,reps,&chk);
        double r2=bench_ref(0,reps,&chk);
        double a =bench_fn(pick_lu(n),reps,&chk);
        double a2=bench_fn(pick_lubp(n),reps,&chk);
        double ap=bench_fn(pick_lunp(n),reps,&chk);
        double b =bench_fn(pick_chol(n),reps,&chk);
        double out=0,ai=0,bi=0;
        switch(n){
        case 2: BENCH_INL(2,fix_lu_inl); ai=out; BENCH_INL(2,fix_chol_inl); bi=out; break;
        case 3: BENCH_INL(3,fix_lu_inl); ai=out; BENCH_INL(3,fix_chol_inl); bi=out; break;
        case 4: BENCH_INL(4,fix_lu_inl); ai=out; BENCH_INL(4,fix_chol_inl); bi=out; break;
        case 6: BENCH_INL(6,fix_lu_inl); ai=out; BENCH_INL(6,fix_chol_inl); bi=out; break;
        default:BENCH_INL(8,fix_lu_inl); ai=out; BENCH_INL(8,fix_chol_inl); bi=out; break;
        }
        printf("%2d %9.1f %9.1f %9.1f %9.1f %9.1f %9.1f %9.1f %9.1f | %7.2f %7.2f %7.2f %7.2f\n",
               n,r1,r2,a,a2,ap,b,ai,bi, r1/a2, r1/b, r2/a2, r1/bi);
    }
    printf("checksum %.15g\n", chk);
}

int main(int argc,char**argv)
{
    if(argc>1 && strcmp(argv[1],"speed")==0) run_speed();
    else if(argc>1 && strcmp(argv[1],"acc")==0) run_acc();
    else { run_acc(); printf("\n"); run_speed(); }
    return 0;
}
