/* sym3_gap_probe.c -- is README correction 14's `1e-6 max|lambda|` fallback
 * threshold in the right place?
 *
 * Correction 14 says: the naive closed-form symmetric 3x3 eigendecomposition
 * returns duplicate eigenvectors on DEGENERATE input, and the repair is
 * (a) orthogonalize structurally, (b) defer to Jacobi when the analytic gap is
 * below EIG3_GAP_TOL * max|lambda|, with EIG3_GAP_TOL = 1e-6.
 *
 * The diagnosis was tested on EXACTLY degenerate input.  A threshold, though,
 * is only right if accuracy degrades on the same side of it as the guard: too
 * tight and the closed form silently returns bad vectors just ABOVE the
 * threshold; too loose and the fallback eats the 4.2x on ordinary data.  This
 * probe sweeps the relative eigenvalue gap continuously across the threshold
 * and reports, per decade, what each arm's accuracy actually is and whether
 * the guard fired.
 *
 * It tests THE KERNEL'S OWN CODE: sym3_eigen.c is #included with its main()
 * renamed away, so eig3_closed / eig3_closed_h / eig3_auto / jacobi3 here are
 * literally the functions the README grades.
 *
 * Matrices: A = Q^T diag(2, 1+d, 1) Q with Q a long-double-orthonormalized
 * random rotation, so the DESCENDING-order gaps are gap_hi = 1-d and
 * gap_lo = d, scale = 2.  The guard therefore fires exactly when d < 2e-6.
 * Truth is the file's own 80-bit Jacobi (jacobi3_ld) -- an independent
 * high-precision reference, not another double pipeline.
 *
 * Also reported: the fallback rate on PHYSICALLY REALISTIC input.  Correction
 * 14 claims "0% fallback on generic input"; generic there means uniform
 * random.  Real symmetric 3x3 tensors in physics cluster near isotropy
 * (A = cI + small deviatoric part), which is precisely the degenerate corner.
 *
 * build: gcc -O3 -march=native -ffp-contract=fast -o gapprobe.exe sym3_gap_probe.c -lm
 * run:   ./gapprobe.exe sweep
 *        ./gapprobe.exe realistic
 */
#define main sym3_eigen_original_main
#include "sym3_eigen.c"   /* the shipped kernel itself -- no second copy to drift */
#undef main

#include <stdio.h>
#include <math.h>
#include <string.h>
#include <stdlib.h>

/* A = Q^T diag(l) Q for a caller-supplied spectrum, reusing the file's own
 * builder so the matrix construction is identical to the graded kernel's. */
static void build_gap(double *A, double d){
    double l[3]; l[0]=2.0; l[1]=1.0+d; l[2]=1.0;
    build(A,l);
}

/* Physically realistic near-isotropic tensor: A = c*I + eps*D, D a random
 * traceless-ish symmetric deviator.  `aniso` = eps/c is the anisotropy. */
static void build_iso(double *A, double aniso){
    double D[9];
    for(int i=0;i<3;i++) for(int j=i;j<3;j++){ double v=rnd(); D[i*3+j]=v; D[j*3+i]=v; }
    double tr=(D[0]+D[4]+D[8])/3.0;
    D[0]-=tr; D[4]-=tr; D[8]-=tr;
    for(int i=0;i<9;i++) A[i]= (i%4==0 ? 1.0 : 0.0) + aniso*D[i];
    A[3]=A[1]; A[6]=A[2]; A[7]=A[5];
}

typedef struct { double orth, resid, valerr; int nfb; } stat_t;
static void acc(stat_t*s,double o,double r,double v){
    if(o>s->orth)s->orth=o; if(r>s->resid)s->resid=r; if(v>s->valerr)s->valerr=v;
}

static void run_one(const double*A, stat_t*sC, stat_t*sCh, stat_t*sCf, stat_t*sJ){
    double w[3],V[9]; long double wt[3],Vt[9];
    jacobi3_ld(A,wt,Vt);
    eig3_closed  (A,w,V); acc(sC ,eig_orth(V),eig_resid(A,w,V),eig_val_err(A,w,wt));
    eig3_closed_h(A,w,V); acc(sCh,eig_orth(V),eig_resid(A,w,V),eig_val_err(A,w,wt));
    int rc=eig3_auto(A,w,V); if(rc==2) sCf->nfb++;
                          acc(sCf,eig_orth(V),eig_resid(A,w,V),eig_val_err(A,w,wt));
    jacobi3      (A,w,V); acc(sJ ,eig_orth(V),eig_resid(A,w,V),eig_val_err(A,w,wt));
}

int main(int argc,char**argv){
    const char*mode=(argc>1)?argv[1]:"sweep";
    const int T=(argc>2)?atoi(argv[2]):4000;

    if(!strcmp(mode,"sweep")){
        printf("GAP SWEEP: A = Q^T diag(2, 1+d, 1) Q.  scale=2, so the shipped guard\n");
        printf("           EIG3_GAP_TOL=%g fires exactly when d < %g.\n", EIG3_GAP_TOL, 2*EIG3_GAP_TOL);
        printf("           %d matrices per row.  Worst-case over the row.\n\n", T);
        printf("  %-10s | %-21s | %-21s | %-21s | %s\n",
               "gap d","C  naive closed","Ch hybrid (no guard)","Cf guarded (shipped)","J jacobi");
        printf("  %-10s | %9s %11s | %9s %11s | %9s %11s %5s | %9s\n",
               "","orth","resid","orth","resid","orth","resid","fb%","orth");
        for(int e=1; e<=16; ++e){
            double d=pow(10.0,-(double)e);
            stat_t sC={0,0,0,0},sCh={0,0,0,0},sCf={0,0,0,0},sJ={0,0,0,0};
            rs=0x243F6A8885A308D3ull ^ (unsigned long long)e;
            for(int t=0;t<T;t++){ double A[9]; build_gap(A,d); run_one(A,&sC,&sCh,&sCf,&sJ); }
            printf("  1e-%-7d | %9.2e %11.2e | %9.2e %11.2e | %9.2e %11.2e %4.0f%% | %9.2e%s\n",
                   e, sC.orth,sC.resid, sCh.orth,sCh.resid, sCf.orth,sCf.resid,
                   100.0*sCf.nfb/T, sJ.orth,
                   (d < 2*EIG3_GAP_TOL) ? "   <- guard region" : "");
        }
        printf("\n  d=0 (exactly degenerate pair):\n");
        {
            stat_t sC={0,0,0,0},sCh={0,0,0,0},sCf={0,0,0,0},sJ={0,0,0,0};
            rs=0xFEEDBEEFull;
            for(int t=0;t<T;t++){ double A[9]; build_gap(A,0.0); run_one(A,&sC,&sCh,&sCf,&sJ); }
            printf("  %-10s | %9.2e %11.2e | %9.2e %11.2e | %9.2e %11.2e %4.0f%% | %9.2e\n",
                   "0", sC.orth,sC.resid, sCh.orth,sCh.resid, sCf.orth,sCf.resid,
                   100.0*sCf.nfb/T, sJ.orth);
        }
        return 0;
    }

    if(!strcmp(mode,"realistic")){
        printf("FALLBACK RATE on physically realistic near-isotropic tensors\n");
        printf("  A = I + aniso*D, D a random traceless symmetric deviator.\n");
        printf("  Correction 14 claims a 0%% fallback rate on 'generic input'; generic\n");
        printf("  there is uniform random, whose eigenvalues are well separated by\n");
        printf("  construction.  Isotropic-ish stress is the ordinary physics case.\n\n");
        printf("  %-12s %8s %12s %12s %12s\n","anisotropy","fb %","Cf orth","Ch orth","Cf resid");
        for(int e=0;e<=16;++e){
            double a=pow(10.0,-(double)e);
            stat_t sC={0,0,0,0},sCh={0,0,0,0},sCf={0,0,0,0},sJ={0,0,0,0};
            rs=0x9E3779B97F4A7C15ull ^ (unsigned long long)e;
            for(int t=0;t<T;t++){ double A[9]; build_iso(A,a); run_one(A,&sC,&sCh,&sCf,&sJ); }
            printf("  1e-%-9d %7.1f%% %12.2e %12.2e %12.2e\n",
                   e, 100.0*sCf.nfb/T, sCf.orth, sCh.orth, sCf.resid);
        }
        printf("\n  uniform-random symmetric (correction 14's 'generic input') control:\n");
        {
            stat_t sC={0,0,0,0},sCh={0,0,0,0},sCf={0,0,0,0},sJ={0,0,0,0};
            rs=0x1234567890ABCDEFull;
            for(int t=0;t<T;t++){ double A[9]; gencase(A,0); run_one(A,&sC,&sCh,&sCf,&sJ); }
            printf("  %-12s %7.1f%% %12.2e %12.2e %12.2e\n","random sym",
                   100.0*sCf.nfb/T, sCf.orth, sCh.orth, sCf.resid);
        }
        return 0;
    }
    printf("usage: %s sweep|realistic [trials]\n",argv[0]);
    return 1;
}
