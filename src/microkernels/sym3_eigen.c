/* eig3.c -- symmetric 3x3 eigendecomposition: closed form vs Jacobi.
 *
 * Arms
 *   J   : cyclic Jacobi over runtime n with heap workspace (the emitted-code model)
 *   Jf  : the same Jacobi, n fixed at 3, stack workspace
 *   C   : closed form -- analytic eigenvalues from the characteristic cubic
 *         (trigonometric / Smith form), eigenvectors by cross products
 *   Ch  : closed form, HYBRID -- analytic eigenvalues, then the eigenvector of the
 *         BEST-SEPARATED eigenvalue by cross product, the second re-orthogonalized
 *         against it, the third as their cross product. Orthonormal by construction.
 *
 * Truth for accuracy: 80-bit cyclic Jacobi on the same (double-rounded) matrix.
 * usage: eig3.exe
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
static unsigned long long rs=0x243F6A8885A308D3ull;
static double rnd(void){ rs^=rs<<13; rs^=rs>>7; rs^=rs<<17;
    return ((double)(rs>>11)*(1.0/9007199254740992.0))*2.0-1.0; }

/* ==================================================== Jacobi, runtime n, heap */
__attribute__((noinline,noclone))
static int jacobi_heap(const double *A,double *w,double *V,int n)
{
    double *M=(double*)malloc((size_t)n*n*sizeof(double));
    double *Q=(double*)malloc((size_t)n*n*sizeof(double));
    if(!M||!Q){free(M);free(Q);return -1;}
    memcpy(M,A,(size_t)n*n*sizeof(double));
    for(int i=0;i<n;i++) for(int j=0;j<n;j++) Q[i*n+j]=(i==j)?1.0:0.0;
    for(int sweep=0; sweep<60; sweep++){
        double off=0;
        for(int p=0;p<n;p++) for(int q=p+1;q<n;q++) off+=M[p*n+q]*M[p*n+q];
        if(off<=1e-40) break;
        for(int p=0;p<n;p++) for(int q=p+1;q<n;q++){
            double apq=M[p*n+q];
            if(fabs(apq)<1e-300) continue;
            double theta=(M[q*n+q]-M[p*n+p])/(2.0*apq);
            double t=(theta>=0?1.0:-1.0)/(fabs(theta)+sqrt(theta*theta+1.0));
            double c=1.0/sqrt(t*t+1.0), s=t*c;
            /* stride-n column walk: this is the access pattern being modelled */
            for(int k=0;k<n;k++){
                double mkp=M[k*n+p], mkq=M[k*n+q];
                M[k*n+p]=c*mkp-s*mkq; M[k*n+q]=s*mkp+c*mkq;
            }
            for(int k=0;k<n;k++){
                double mpk=M[p*n+k], mqk=M[q*n+k];
                M[p*n+k]=c*mpk-s*mqk; M[q*n+k]=s*mpk+c*mqk;
            }
            for(int k=0;k<n;k++){
                double vkp=Q[k*n+p], vkq=Q[k*n+q];
                Q[k*n+p]=c*vkp-s*vkq; Q[k*n+q]=s*vkp+c*vkq;
            }
        }
    }
    for(int i=0;i<n;i++) w[i]=M[i*n+i];
    for(int i=0;i<n;i++) for(int j=0;j<n;j++) V[j*n+i]=Q[i*n+j]; /* V row j = evec j */
    free(M); free(Q); return 0;
}

/* same algorithm, n fixed at 3, stack workspace */
__attribute__((noinline,noclone))
static int jacobi3(const double *A,double *w,double *V)
{
    double M[9],Q[9];
    for(int i=0;i<9;i++) M[i]=A[i];
    for(int i=0;i<3;i++) for(int j=0;j<3;j++) Q[i*3+j]=(i==j)?1.0:0.0;
    for(int sweep=0; sweep<60; sweep++){
        double off=M[1]*M[1]+M[2]*M[2]+M[5]*M[5];
        if(off<=1e-40) break;
        for(int p=0;p<3;p++) for(int q=p+1;q<3;q++){
            double apq=M[p*3+q];
            if(fabs(apq)<1e-300) continue;
            double theta=(M[q*3+q]-M[p*3+p])/(2.0*apq);
            double t=(theta>=0?1.0:-1.0)/(fabs(theta)+sqrt(theta*theta+1.0));
            double c=1.0/sqrt(t*t+1.0), s=t*c;
            for(int k=0;k<3;k++){ double a=M[k*3+p],b=M[k*3+q];
                M[k*3+p]=c*a-s*b; M[k*3+q]=s*a+c*b; }
            for(int k=0;k<3;k++){ double a=M[p*3+k],b=M[q*3+k];
                M[p*3+k]=c*a-s*b; M[q*3+k]=s*a+c*b; }
            for(int k=0;k<3;k++){ double a=Q[k*3+p],b=Q[k*3+q];
                Q[k*3+p]=c*a-s*b; Q[k*3+q]=s*a+c*b; }
        }
    }
    for(int i=0;i<3;i++) w[i]=M[i*3+i];
    for(int i=0;i<3;i++) for(int j=0;j<3;j++) V[j*3+i]=Q[i*3+j];
    return 0;
}

/* 80-bit Jacobi: truth */
static void jacobi3_ld(const double *A, long double *w, long double *V)
{
    long double M[9],Q[9];
    for(int i=0;i<9;i++) M[i]=(long double)A[i];
    for(int i=0;i<3;i++) for(int j=0;j<3;j++) Q[i*3+j]=(i==j)?1.0L:0.0L;
    for(int sweep=0; sweep<200; sweep++){
        long double off=M[1]*M[1]+M[2]*M[2]+M[5]*M[5];
        if(off<=1e-60L) break;
        for(int p=0;p<3;p++) for(int q=p+1;q<3;q++){
            long double apq=M[p*3+q];
            if(fabsl(apq)<1e-4000L) continue;
            long double th=(M[q*3+q]-M[p*3+p])/(2.0L*apq);
            long double t=(th>=0?1.0L:-1.0L)/(fabsl(th)+sqrtl(th*th+1.0L));
            long double c=1.0L/sqrtl(t*t+1.0L), s=t*c;
            for(int k=0;k<3;k++){ long double a=M[k*3+p],b=M[k*3+q];
                M[k*3+p]=c*a-s*b; M[k*3+q]=s*a+c*b; }
            for(int k=0;k<3;k++){ long double a=M[p*3+k],b=M[q*3+k];
                M[p*3+k]=c*a-s*b; M[q*3+k]=s*a+c*b; }
            for(int k=0;k<3;k++){ long double a=Q[k*3+p],b=Q[k*3+q];
                Q[k*3+p]=c*a-s*b; Q[k*3+q]=s*a+c*b; }
        }
    }
    for(int i=0;i<3;i++) w[i]=M[i*3+i];
    for(int i=0;i<3;i++) for(int j=0;j<3;j++) V[j*3+i]=Q[i*3+j];
}

/* ============================================ closed-form eigenvalues (cubic) */
/* Trigonometric solution of the characteristic cubic. Returns e[0]>=e[1]>=e[2]. */
static inline __attribute__((always_inline))
void eig3_values(const double *A, double *e)
{
    const double a00=A[0],a01=A[1],a02=A[2],a11=A[4],a12=A[5],a22=A[8];
    double p1 = a01*a01 + a02*a02 + a12*a12;
    double q  = (a00+a11+a22)/3.0;
    if(p1 <= 0.0){ /* already diagonal */
        double d0=a00,d1=a11,d2=a22,t;
        if(d0<d1){t=d0;d0=d1;d1=t;} if(d1<d2){t=d1;d1=d2;d2=t;} if(d0<d1){t=d0;d0=d1;d1=t;}
        e[0]=d0;e[1]=d1;e[2]=d2; return;
    }
    double d0=a00-q, d1=a11-q, d2=a22-q;
    double p2 = d0*d0 + d1*d1 + d2*d2 + 2.0*p1;
    double p  = sqrt(p2/6.0);
    double ip = 1.0/p;
    /* B = (A - qI)/p ; r = det(B)/2 */
    double b00=d0*ip, b11=d1*ip, b22=d2*ip;
    double b01=a01*ip, b02=a02*ip, b12=a12*ip;
    double det = b00*(b11*b22 - b12*b12)
               - b01*(b01*b22 - b12*b02)
               + b02*(b01*b12 - b11*b02);
    double r = det*0.5;
    if(r<=-1.0) r=-1.0; else if(r>=1.0) r=1.0;   /* the clamp that keeps acos sane */
    double phi = acos(r)/3.0;
    e[0] = q + 2.0*p*cos(phi);
    e[2] = q + 2.0*p*cos(phi + 2.0*M_PI/3.0);
    e[1] = 3.0*q - e[0] - e[2];                  /* trace identity: exact-ish, cheap */
}

static inline void cross(const double*a,const double*b,double*c){
    c[0]=a[1]*b[2]-a[2]*b[1];
    c[1]=a[2]*b[0]-a[0]*b[2];
    c[2]=a[0]*b[1]-a[1]*b[0];
}
static inline double nrm2(const double*v){ return v[0]*v[0]+v[1]*v[1]+v[2]*v[2]; }

/* eigenvector for lambda: best of the three cross products of the rows of A-lambda I */
static inline __attribute__((always_inline))
void eig3_vec(const double *A, double lam, double *v)
{
    double r0[3]={A[0]-lam,A[1],A[2]};
    double r1[3]={A[3],A[4]-lam,A[5]};
    double r2[3]={A[6],A[7],A[8]-lam};
    double c0[3],c1[3],c2[3];
    cross(r0,r1,c0); cross(r1,r2,c1); cross(r2,r0,c2);
    double n0=nrm2(c0),n1=nrm2(c1),n2=nrm2(c2);
    const double *bst=c0; double nb=n0;
    if(n1>nb){bst=c1;nb=n1;}
    if(n2>nb){bst=c2;nb=n2;}
    if(nb<=0.0){ v[0]=1;v[1]=0;v[2]=0; return; }
    double s=1.0/sqrt(nb);
    v[0]=bst[0]*s; v[1]=bst[1]*s; v[2]=bst[2]*s;
}

/* C: naive closed form -- every eigenvector independently by cross product */
__attribute__((noinline,noclone))
static int eig3_closed(const double *A,double *w,double *V)
{
    eig3_values(A,w);
    eig3_vec(A,w[0],V+0);
    eig3_vec(A,w[1],V+3);
    eig3_vec(A,w[2],V+6);
    return 0;
}

/* Ch: hybrid. Take the eigenvector of whichever EXTREME eigenvalue is better
   separated, get the other extreme by cross product, re-orthogonalize it, and
   build the middle one as their cross product. Orthonormality is then structural
   rather than hoped-for, which is what the degenerate cases break in arm C. */
__attribute__((noinline,noclone))
static int eig3_vecs_h(const double *A,const double *w,double *V)
{
    double gap_hi = w[0]-w[1], gap_lo = w[1]-w[2];
    int ihi = (gap_hi >= gap_lo) ? 0 : 2;   /* better separated extreme */
    int ilo = 2-ihi;
    double *vhi=V+3*ihi, *vlo=V+3*ilo, *vmid=V+3;
    eig3_vec(A,w[ihi],vhi);
    eig3_vec(A,w[ilo],vlo);
    /* one Gram-Schmidt step: vlo <- vlo - (vlo.vhi) vhi */
    double d=vlo[0]*vhi[0]+vlo[1]*vhi[1]+vlo[2]*vhi[2];
    vlo[0]-=d*vhi[0]; vlo[1]-=d*vhi[1]; vlo[2]-=d*vhi[2];
    double nn=nrm2(vlo);
    if(nn>1e-300){ double s=1.0/sqrt(nn); vlo[0]*=s;vlo[1]*=s;vlo[2]*=s; }
    else { /* vhi and vlo collapsed: pick any vector orthogonal to vhi */
        double t[3]={1,0,0};
        if(fabs(vhi[0])>0.9){ t[0]=0;t[1]=1; }
        double p=t[0]*vhi[0]+t[1]*vhi[1]+t[2]*vhi[2];
        t[0]-=p*vhi[0];t[1]-=p*vhi[1];t[2]-=p*vhi[2];
        double s=1.0/sqrt(nrm2(t)); vlo[0]=t[0]*s;vlo[1]=t[1]*s;vlo[2]=t[2]*s;
    }
    if(ihi==0) cross(vlo,vhi,vmid); else cross(vhi,vlo,vmid);
    double s=1.0/sqrt(nrm2(vmid)); vmid[0]*=s;vmid[1]*=s;vmid[2]*=s;
    return 0;
}

/* Cf: the shape that is actually recommendable. The analytic eigenvalues lose
   ~half the mantissa when two roots collide (acos has an infinite derivative at
   r=+-1), so TRUST THEM ONLY WHEN THEY SAY THE ROOTS ARE WELL SEPARATED. A gap
   below EIG3_GAP_TOL * scale is exactly the regime where the closed form cannot
   certify itself, and there it defers to Jacobi. */
__attribute__((noinline,noclone))
static int eig3_closed_h(const double *A,double *w,double *V)
{
    eig3_values(A,w);
    return eig3_vecs_h(A,w,V);
}

#define EIG3_GAP_TOL 1e-6
/* returns 2 when it deferred to Jacobi (no counters: they would distort timing) */
__attribute__((noinline,noclone))
static int eig3_auto(const double *A,double *w,double *V)
{
    eig3_values(A,w);
    double scale=fabs(w[0]); if(fabs(w[2])>scale) scale=fabs(w[2]);
    if(scale>0.0 && ((w[0]-w[1]) < EIG3_GAP_TOL*scale || (w[1]-w[2]) < EIG3_GAP_TOL*scale)){
        jacobi3(A,w,V); return 2;
    }
    return eig3_vecs_h(A,w,V);
}

/* ============================================================ test matrices */
static void gram_schmidt_ld(long double *Q)
{
    for(int i=0;i<3;i++){
        for(int j=0;j<i;j++){
            long double d=0; for(int k=0;k<3;k++) d+=Q[i*3+k]*Q[j*3+k];
            for(int k=0;k<3;k++) Q[i*3+k]-=d*Q[j*3+k];
        }
        long double n=0; for(int k=0;k<3;k++) n+=Q[i*3+k]*Q[i*3+k];
        n=1.0L/sqrtl(n);
        for(int k=0;k<3;k++) Q[i*3+k]*=n;
    }
}
/* A = Q^T diag(l) Q, rounded to double */
static void build(double *A, const double l[3])
{
    long double Q[9];
    for(int i=0;i<9;i++) Q[i]=(long double)rnd();
    gram_schmidt_ld(Q);
    for(int i=0;i<3;i++) for(int j=0;j<3;j++){
        long double s=0;
        for(int k=0;k<3;k++) s+=Q[k*3+i]*(long double)l[k]*Q[k*3+j];
        A[i*3+j]=(double)s;
    }
    A[3]=A[1]; A[6]=A[2]; A[7]=A[5];   /* enforce exact symmetry after rounding */
}

enum { NC=7 };
static const char *cname[NC]={
    "random symmetric","random SPD","near-deg 1e-8","near-deg 1e-14",
    "exactly degenerate pair","triple degenerate","wide range 1e-10..1e10" };
static void gencase(double *A,int c)
{
    double l[3];
    switch(c){
    case 0: { for(int i=0;i<3;i++) for(int j=i;j<3;j++){ double v=rnd(); A[i*3+j]=v; A[j*3+i]=v; } return; }
    case 1: { double M[9]; for(int i=0;i<9;i++) M[i]=rnd();
              for(int i=0;i<3;i++) for(int j=0;j<3;j++){ double s=0;
                  for(int k=0;k<3;k++) s+=M[i*3+k]*M[j*3+k];
                  A[i*3+j]=s+((i==j)?3.0:0.0); }
              A[3]=A[1];A[6]=A[2];A[7]=A[5]; return; }
    case 2: l[0]=2.0; l[1]=1.0+1e-8; l[2]=1.0; break;
    case 3: l[0]=2.0; l[1]=1.0+1e-14; l[2]=1.0; break;
    case 4: l[0]=2.0; l[1]=1.0; l[2]=1.0; break;
    case 5: l[0]=1.0; l[1]=1.0; l[2]=1.0; break;
    default: l[0]=1e10; l[1]=1.0; l[2]=1e-10; break;
    }
    build(A,l);
}

/* ============================================================== metrics */
static double norm_inf(const double*A){
    double m=0; for(int i=0;i<3;i++){ double s=0; for(int j=0;j<3;j++) s+=fabs(A[i*3+j]);
        if(s>m)m=s; } return m; }
/* max_i ||A v_i - w_i v_i||_inf / ||A||_inf */
static double eig_resid(const double*A,const double*w,const double*V){
    double na=norm_inf(A), m=0;
    for(int i=0;i<3;i++){
        for(int r=0;r<3;r++){
            long double s=0;
            for(int c=0;c<3;c++) s+=(long double)A[r*3+c]*(long double)V[i*3+c];
            s-=(long double)w[i]*(long double)V[i*3+r];
            if((double)fabsl(s)>m) m=(double)fabsl(s);
        }
    }
    return na>0? m/na : m;
}
/* max |v_i . v_j| (i!=j) and max ||v_i||-1 */
static double eig_orth(const double*V){
    double m=0;
    for(int i=0;i<3;i++){
        long double n=0; for(int k=0;k<3;k++) n+=(long double)V[i*3+k]*(long double)V[i*3+k];
        double d=fabs((double)(n-1.0L)); if(d>m)m=d;
        for(int j=i+1;j<3;j++){
            long double s=0; for(int k=0;k<3;k++) s+=(long double)V[i*3+k]*(long double)V[j*3+k];
            if(fabs((double)s)>m) m=fabs((double)s);
        }
    }
    return m;
}
/* eigenvalue error relative to ||A|| (the meaningful normalization) */
static double eig_val_err(const double*A,const double*w,const long double*wt){
    double na=norm_inf(A); if(na==0) na=1;
    double ws[3]={w[0],w[1],w[2]}, m=0;
    long double t[3]={wt[0],wt[1],wt[2]};
    for(int i=0;i<3;i++) for(int j=i+1;j<3;j++){
        if(ws[j]>ws[i]){ double x=ws[i];ws[i]=ws[j];ws[j]=x; }
        if(t[j]>t[i]){ long double y=t[i];t[i]=t[j];t[j]=y; }
    }
    for(int i=0;i<3;i++){ double d=(double)fabsl((long double)ws[i]-t[i]); if(d/na>m) m=d/na; }
    return m;
}

/* ================================================================== main */
#define NSYS 4096
static double gA[NSYS*9], gw[NSYS*3], gV[NSYS*9];

int main(void)
{
    printf("# SYMMETRIC 3x3 EIGENDECOMPOSITION -- accuracy (max over 512 trials)\n");
    printf("%-24s | %-32s | %-32s | %-32s | %-32s\n","case","Jf  Jacobi (fixed 3)",
           "C   closed form, naive vectors","Ch  closed form, hybrid vectors",
           "Cf  closed form + gap fallback");
    printf("%-24s | %10s %10s %10s | %10s %10s %10s | %10s %10s %10s | %10s %10s %10s %6s\n",
           "","val_err","resid","orth","val_err","resid","orth","val_err","resid","orth",
           "val_err","resid","orth","fallbk");
    for(int c=0;c<NC;c++){
        rs=0x1234567ABCDEFull ^ (unsigned)c;
        double mv[4]={0,0,0,0}, mr[4]={0,0,0,0}, mo[4]={0,0,0,0};
        long long fb=0, nc=0;
        for(int it=0; it<512; it++){
            double A[9],w[3],V[9]; long double wt[3],Vt[9];
            gencase(A,c);
            jacobi3_ld(A,wt,Vt);
            jacobi3(A,w,V);
            { double v=eig_val_err(A,w,wt),r=eig_resid(A,w,V),o=eig_orth(V);
              if(v>mv[0])mv[0]=v; if(r>mr[0])mr[0]=r; if(o>mo[0])mo[0]=o; }
            eig3_closed(A,w,V);
            { double v=eig_val_err(A,w,wt),r=eig_resid(A,w,V),o=eig_orth(V);
              if(v>mv[1])mv[1]=v; if(r>mr[1])mr[1]=r; if(o>mo[1])mo[1]=o; }
            eig3_closed_h(A,w,V);
            { double v=eig_val_err(A,w,wt),r=eig_resid(A,w,V),o=eig_orth(V);
              if(v>mv[2])mv[2]=v; if(r>mr[2])mr[2]=r; if(o>mo[2])mo[2]=o; }
            if(eig3_auto(A,w,V)==2) fb++; nc++;
            { double v=eig_val_err(A,w,wt),r=eig_resid(A,w,V),o=eig_orth(V);
              if(v>mv[3])mv[3]=v; if(r>mr[3])mr[3]=r; if(o>mo[3])mo[3]=o; }
        }
        printf("%-24s |",cname[c]);
        for(int a=0;a<4;a++) printf(" %10.2e %10.2e %10.2e |",mv[a],mr[a],mo[a]);
        printf(" %5.1f%%\n", 100.0*(double)fb/(double)nc);
    }

    /* ------------------------------------------------------------- speed */
    printf("\n# SPEED  ns per decomposition, best of 25 passes over %d matrices\n",NSYS);
    rs=0xFEEDFACEull;
    for(int s=0;s<NSYS;s++) gencase(gA+s*9, (s&1)?1:0);
    const int reps=25; double chk=0;
    double tm[5];
    #define TE(idx,CALL) do{ double best=1e30; \
        for(int rp=0;rp<reps;rp++){ double t0=now_s(); BARRIER(); \
            for(int s=0;s<NSYS;s++) CALL; BARRIER(); double t1=now_s(); \
            double d=(t1-t0)/NSYS*1e9; if(d<best)best=d; } \
        for(int s=0;s<NSYS;s++) chk+=gw[s*3]; tm[idx]=best; }while(0)
    TE(0, jacobi_heap(gA+s*9,gw+s*3,gV+s*9,3));
    TE(1, jacobi3(gA+s*9,gw+s*3,gV+s*9));
    TE(2, eig3_closed(gA+s*9,gw+s*3,gV+s*9));
    TE(3, eig3_closed_h(gA+s*9,gw+s*3,gV+s*9));
    TE(4, eig3_auto(gA+s*9,gw+s*3,gV+s*9));
    printf("%12s %12s %12s %12s %12s | %8s %8s %8s\n",
           "J heap","Jf fixed","C closed","Ch hybrid","Cf auto","J/Ch","Jf/Ch","J/Cf");
    printf("%12.1f %12.1f %12.1f %12.1f %12.1f | %8.2f %8.2f %8.2f\n",
           tm[0],tm[1],tm[2],tm[3],tm[4], tm[0]/tm[3], tm[1]/tm[3], tm[0]/tm[4]);
    printf("checksum %.15g\n",chk);
    return 0;
}
