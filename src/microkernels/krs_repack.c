/* krs_repack.c -- measure the one UNMEASURED number in the microkernels README.
 *
 * Correction 4 says of the KRS former's ragged tail:
 *
 *   "the dominant term is *panel misalignment*, not raggedness -- row blocks
 *    start at arbitrary j0 while packed panels are pinned to multiples of 8.
 *    Repacking `A` per `i` with panel origin at `k = i` is worth an estimated
 *    further 1.3-1.5x."
 *
 * "estimated" is the only unmeasured figure in a document whose authority is
 * that it measures things.  This kernel measures it.  Arms (all reproduce
 * krs_former.c's Arm B exactly except for where packed panels begin):
 *
 *   B   shipped KRS.  A packed ONCE into NR-wide panels pinned to k = 0.
 *   P   correction 4 as literally written: A repacked per `i` with panel
 *       origin at k = i.  Repacking is INSIDE the timed region, because that
 *       is what the proposal costs.
 *   Pf  the same, with every per-i packing PREBUILT before the clock starts.
 *       Not a shippable schedule -- it is the mechanism-only upper bound, i.e.
 *       what origin-at-i would be worth if packing were free.
 *   Q   the alignment the proposal was actually reaching for: 8 phase-shifted
 *       packings built once (cost 8x ONE pack, still O(n*d)), so the panel for
 *       every row block starts exactly at j0.  Head misalignment is ZERO.
 *       This is the arm to build if any of them is.
 *
 * All four visit cells in the same t order with the same KC blocking, so they
 * are BITWISE identical to each other; only the reference nest differs.  The
 * live control for that column is `ref_alt`, which reassociates the triple
 * product to r[i]*(r[j]*r[k]) and MUST read NO on full-mantissa operands
 * (README correction 17).
 *
 *   gcc -O3 -march=native -ffp-contract=fast -o krsrp.exe krs_repack.c -lm
 *   ./krsrp.exe verify <n> <d> <KC>
 *   ./krsrp.exe bench  <n> <d> <reps> <KC>
 *   ./krsrp.exe waste  <n>              # lane accounting only, no timing
 */
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <math.h>
#include <immintrin.h>
#include <windows.h>

#define MR 6
#define NR 8

static double wall(void){
    LARGE_INTEGER f,c; QueryPerformanceFrequency(&f); QueryPerformanceCounter(&c);
    return (double)c.QuadPart/(double)f.QuadPart;
}
static size_t* make_base(int n, size_t* total){
    size_t* base = (size_t*)malloc((size_t)n*n*sizeof(size_t));
    size_t cur = 0;
    for (int i=0;i<n;i++)
        for (int j=i;j<n;j++){ base[(size_t)i*n+j] = cur; cur += (size_t)(n-j); }
    *total = cur; return base;
}

/* ------------------------------------------------------------- references */
static void ref_r3(const double* restrict A, int d, int n,
                   const size_t* restrict base, double* restrict C){
    for (int i=0;i<n;i++) for (int j=i;j<n;j++){
        double* crow = C + base[(size_t)i*n+j] - j;
        for (int k=j;k<n;k++){
            double acc = 0.0;
            for (int t=0;t<d;t++){ const double* r = A + (size_t)t*n; acc += r[i]*r[j]*r[k]; }
            crow[k] += acc;
        }
    }
}
/* LIVE CONTROL for the bitwise column: same value, other association. */
static void ref_alt(const double* restrict A, int d, int n,
                    const size_t* restrict base, double* restrict C){
    for (int i=0;i<n;i++) for (int j=i;j<n;j++){
        double* crow = C + base[(size_t)i*n+j] - j;
        for (int k=j;k<n;k++){
            double acc = 0.0;
            for (int t=0;t<d;t++){ const double* r = A + (size_t)t*n; acc += r[i]*(r[j]*r[k]); }
            crow[k] += acc;
        }
    }
}

/* ------------------------------------------------- the 6x8 micro-kernel  */
static inline void micro_6x8(int kc, const double* restrict Gp,
                             const double* restrict Ap, double* restrict acc){
    __m256d c00=_mm256_setzero_pd(), c01=_mm256_setzero_pd();
    __m256d c10=_mm256_setzero_pd(), c11=_mm256_setzero_pd();
    __m256d c20=_mm256_setzero_pd(), c21=_mm256_setzero_pd();
    __m256d c30=_mm256_setzero_pd(), c31=_mm256_setzero_pd();
    __m256d c40=_mm256_setzero_pd(), c41=_mm256_setzero_pd();
    __m256d c50=_mm256_setzero_pd(), c51=_mm256_setzero_pd();
    for (int t=0;t<kc;t++){
        const double* a = Ap + (size_t)t*NR;
        const double* g = Gp + (size_t)t*MR;
        __m256d b0 = _mm256_load_pd(a), b1 = _mm256_load_pd(a+4), gv;
        gv=_mm256_broadcast_sd(g+0); c00=_mm256_fmadd_pd(gv,b0,c00); c01=_mm256_fmadd_pd(gv,b1,c01);
        gv=_mm256_broadcast_sd(g+1); c10=_mm256_fmadd_pd(gv,b0,c10); c11=_mm256_fmadd_pd(gv,b1,c11);
        gv=_mm256_broadcast_sd(g+2); c20=_mm256_fmadd_pd(gv,b0,c20); c21=_mm256_fmadd_pd(gv,b1,c21);
        gv=_mm256_broadcast_sd(g+3); c30=_mm256_fmadd_pd(gv,b0,c30); c31=_mm256_fmadd_pd(gv,b1,c31);
        gv=_mm256_broadcast_sd(g+4); c40=_mm256_fmadd_pd(gv,b0,c40); c41=_mm256_fmadd_pd(gv,b1,c41);
        gv=_mm256_broadcast_sd(g+5); c50=_mm256_fmadd_pd(gv,b0,c50); c51=_mm256_fmadd_pd(gv,b1,c51);
    }
    _mm256_store_pd(acc+ 0,c00); _mm256_store_pd(acc+ 4,c01);
    _mm256_store_pd(acc+ 8,c10); _mm256_store_pd(acc+12,c11);
    _mm256_store_pd(acc+16,c20); _mm256_store_pd(acc+20,c21);
    _mm256_store_pd(acc+24,c30); _mm256_store_pd(acc+28,c31);
    _mm256_store_pd(acc+32,c40); _mm256_store_pd(acc+36,c41);
    _mm256_store_pd(acc+40,c50); _mm256_store_pd(acc+44,c51);
}

/* pack one phase: Ap[(p*d + t)*NR + l] = A[t][phi + NR*p + l]  (0 past n) */
static void pack_phase(const double* restrict A, int d, int n, int phi,
                       int np, double* restrict Ap){
    for (int p=0;p<np;p++)
        for (int t=0;t<d;t++){
            const double* r = A + (size_t)t*n;
            double* dst = Ap + ((size_t)p*d + t)*NR;
            for (int l=0;l<NR;l++){ int k = phi + p*NR + l; dst[l] = (k>=0 && k<n)? r[k] : 0.0; }
        }
}

/* Shared inner body: given a packed slab whose panel g covers k in
 * [org + NR*g, org + NR*g + NR), run the KRS schedule for prefix i.       */
static void krs_prefix(const double* restrict A, int d, int n, int KC,
                       const size_t* restrict base, double* restrict C,
                       int i, int org, const double* restrict Ap, int np,
                       double* restrict Gp, double* restrict acc)
{
    for (int t0=0;t0<d;t0+=KC){
        int kc = (d-t0 < KC) ? (d-t0) : KC;
        for (int j0=i;j0<n;j0+=MR){
            int mr = (n-j0 < MR) ? (n-j0) : MR;
            for (int tt=0;tt<kc;tt++){
                const double* r = A + (size_t)(t0+tt)*n;
                double ai = r[i];
                double* g = Gp + (size_t)tt*MR;
                int m=0;
                for (; m<mr; m++) g[m] = ai * r[j0+m];
                for (; m<MR; m++) g[m] = 0.0;
            }
            int p0 = (j0 - org) / NR;          /* first panel that can hold k=j0 */
            if (p0 < 0) p0 = 0;
            for (int p = p0; p < np; p++){
                int kb = org + p*NR;
                if (kb + NR <= j0) continue;   /* entirely below the diagonal */
                if (kb >= n) break;
                micro_6x8(kc, Gp, Ap + ((size_t)p*d + t0)*NR, acc);
                if (kb >= j0 + mr - 1 && kb + NR <= n){
                    for (int m=0;m<mr;m++){
                        double* crow = C + base[(size_t)i*n + j0+m] - (j0+m);
                        double* q = crow + kb;
                        _mm256_storeu_pd(q,   _mm256_add_pd(_mm256_loadu_pd(q),   _mm256_load_pd(acc+m*NR)));
                        _mm256_storeu_pd(q+4, _mm256_add_pd(_mm256_loadu_pd(q+4), _mm256_load_pd(acc+m*NR+4)));
                    }
                } else {
                    for (int m=0;m<mr;m++){
                        int j = j0+m;
                        double* crow = C + base[(size_t)i*n + j] - j;
                        for (int l=0;l<NR;l++){ int k = kb+l; if (k>=j && k<n) crow[k] += acc[m*NR+l]; }
                    }
                }
            }
        }
    }
}

/* ---- Arm B: shipped -- one packing pinned to k = 0 --------------------- */
static void krs_B(const double* restrict A, int d, int n,
                  const size_t* restrict base, double* restrict C, int KC){
    const int np = (n + NR - 1)/NR;
    double* Ap = (double*)_mm_malloc((size_t)np*d*NR*sizeof(double), 64);
    pack_phase(A,d,n,0,np,Ap);
    double* Gp = (double*)_mm_malloc((size_t)KC*MR*sizeof(double), 64);
    double acc[MR*NR] __attribute__((aligned(64)));
    for (int i=0;i<n;i++) krs_prefix(A,d,n,KC,base,C,i,0,Ap,np,Gp,acc);
    _mm_free(Gp); _mm_free(Ap);
}

/* ---- Arm P: correction 4 literally -- repack per i, origin k = i ------- */
static void krs_P(const double* restrict A, int d, int n,
                  const size_t* restrict base, double* restrict C, int KC){
    const int npmax = (n + NR - 1)/NR;
    double* Ap = (double*)_mm_malloc((size_t)npmax*d*NR*sizeof(double), 64);
    double* Gp = (double*)_mm_malloc((size_t)KC*MR*sizeof(double), 64);
    double acc[MR*NR] __attribute__((aligned(64)));
    for (int i=0;i<n;i++){
        int np = (n - i + NR - 1)/NR;
        pack_phase(A,d,n,i,np,Ap);                 /* the proposal's cost */
        krs_prefix(A,d,n,KC,base,C,i,i,Ap,np,Gp,acc);
    }
    _mm_free(Gp); _mm_free(Ap);
}

/* ---- Arm Pf: origin k = i, packing PREBUILT (mechanism upper bound) ---- */
static void krs_Pf(const double* restrict A, int d, int n,
                   const size_t* restrict base, double* restrict C, int KC,
                   double* const* pre, const int* npv){
    double* Gp = (double*)_mm_malloc((size_t)KC*MR*sizeof(double), 64);
    double acc[MR*NR] __attribute__((aligned(64)));
    for (int i=0;i<n;i++) krs_prefix(A,d,n,KC,base,C,i,i,pre[i],npv[i],Gp,acc);
    _mm_free(Gp);
}

/* ---- Arm Q: 8 phase-shifted packings; every block panel starts at j0 --- */
static void krs_Q(const double* restrict A, int d, int n,
                  const size_t* restrict base, double* restrict C, int KC){
    const int np = (n + NR - 1)/NR + 1;
    double* Ph[NR];
    for (int f=0; f<NR; f++){
        Ph[f] = (double*)_mm_malloc((size_t)np*d*NR*sizeof(double), 64);
        pack_phase(A,d,n,f,np,Ph[f]);
    }
    double* Gp = (double*)_mm_malloc((size_t)KC*MR*sizeof(double), 64);
    double acc[MR*NR] __attribute__((aligned(64)));
    /* j0 varies inside krs_prefix, so Q needs its own loop nest: one phase per
     * row block.  Everything else is identical to krs_prefix. */
    for (int i=0;i<n;i++)
    for (int t0=0;t0<d;t0+=KC){
        int kc = (d-t0 < KC) ? (d-t0) : KC;
        for (int j0=i;j0<n;j0+=MR){
            int mr = (n-j0 < MR) ? (n-j0) : MR;
            for (int tt=0;tt<kc;tt++){
                const double* r = A + (size_t)(t0+tt)*n;
                double ai = r[i];
                double* g = Gp + (size_t)tt*MR;
                int m=0;
                for (; m<mr; m++) g[m] = ai * r[j0+m];
                for (; m<MR; m++) g[m] = 0.0;
            }
            int f = j0 % NR;
            const double* Ap = Ph[f];
            int pstart = (j0 - f)/NR;                   /* panel starting at j0 */
            for (int p = pstart; p < np; p++){
                int kb = f + p*NR;
                if (kb >= n) break;
                micro_6x8(kc, Gp, Ap + ((size_t)p*d + t0)*NR, acc);
                if (kb >= j0 + mr - 1 && kb + NR <= n){
                    for (int m=0;m<mr;m++){
                        double* crow = C + base[(size_t)i*n + j0+m] - (j0+m);
                        double* q = crow + kb;
                        _mm256_storeu_pd(q,   _mm256_add_pd(_mm256_loadu_pd(q),   _mm256_load_pd(acc+m*NR)));
                        _mm256_storeu_pd(q+4, _mm256_add_pd(_mm256_loadu_pd(q+4), _mm256_load_pd(acc+m*NR+4)));
                    }
                } else {
                    for (int m=0;m<mr;m++){
                        int j = j0+m;
                        double* crow = C + base[(size_t)i*n + j] - j;
                        for (int l=0;l<NR;l++){ int k = kb+l; if (k>=j && k<n) crow[k] += acc[m*NR+l]; }
                    }
                }
            }
        }
    }
    _mm_free(Gp);
    for (int f=0;f<NR;f++) _mm_free(Ph[f]);
}

/* ------------------------------------------------------- lane accounting */
static void lane_waste(int n){
    long long useful = (long long)n*(n+1)*(n+2)/6;
    long long cB=0,cP=0,cQ=0;
    int npg = (n+NR-1)/NR;
    for (int i=0;i<n;i++) for (int j0=i;j0<n;j0+=MR){
        for (int p=j0/NR;p<npg;p++) cB += MR*NR;
        int npi = (n-i+NR-1)/NR;
        for (int p=(j0-i)/NR;p<npi;p++){ int kb=i+p*NR; if(kb>=n) break; if (kb+NR<=j0) continue; cP += MR*NR; }
        int f=j0%NR;
        for (int p=(j0-f)/NR;;p++){ int kb=f+p*NR; if(kb>=n) break; cQ += MR*NR; }
    }
    printf("n=%d  useful cells=%lld  MR=%d NR=%d\n", n, useful, MR, NR);
    printf("  B (origin 0)  computed=%lld  extra=%.1f%%\n", cB, 100.0*(cB-useful)/useful);
    printf("  P (origin i)  computed=%lld  extra=%.1f%%\n", cP, 100.0*(cP-useful)/useful);
    printf("  Q (origin j0) computed=%lld  extra=%.1f%%\n", cQ, 100.0*(cQ-useful)/useful);
    printf("  README's rule of thumb 18.2/n = %.1f%%\n", 100.0*18.2/n);
}

/* ------------------------------------------------------------------ main */
static unsigned long long rngs = 0x853c49e6748fea9bULL;
static double urand(void){
    rngs = rngs*6364136223846793005ULL + 1442695040888963407ULL;
    return (double)((rngs>>11) & ((1ULL<<53)-1)) / (double)(1ULL<<53);
}
/* FULL-MANTISSA operands: correction 17.  Small ints cannot round. */
static void fill_real(double* A, size_t nelem){
    for (size_t i=0;i<nelem;i++) A[i] = 2.0*urand() - 1.0;
}
static void cmp(const char* nm, const double* x, const double* y, size_t nz){
    double peak=0, maxabs=0; size_t nbit=0;
    for (size_t i=0;i<nz;i++){ double m=fabs(y[i]); if(m>peak) peak=m; }
    for (size_t i=0;i<nz;i++){
        if (x[i]==y[i]) { nbit++; continue; }
        double e=fabs(x[i]-y[i]); if (e>maxabs) maxabs=e;
    }
    printf("    %-10s normrel=%.3e  bitwise=%zu/%zu %s\n", nm, peak>0?maxabs/peak:0.0,
           nbit, nz, nbit==nz ? "(yes)" : "(NO)");
}

int main(int argc, char** argv){
    const char* mode = (argc>1)? argv[1] : "bench";
    if (!strcmp(mode,"waste")){ lane_waste(argc>2?atoi(argv[2]):61); return 0; }

    int n    = (argc>2)? atoi(argv[2]) : 61;
    int d    = (argc>3)? atoi(argv[3]) : 2003;
    int reps = (argc>4)? atoi(argv[4]) : 7;
    int KC   = (argc>5)? atoi(argv[5]) : 64;
    if (!strcmp(mode,"verify")) { reps = 1; KC = (argc>4)? atoi(argv[4]) : 64; }

    size_t total; size_t* base = make_base(n,&total);
    double* A  = (double*)_mm_malloc((size_t)d*n*sizeof(double),64);
    double* C0 = (double*)_mm_malloc(total*sizeof(double),64);
    double* C1 = (double*)_mm_malloc(total*sizeof(double),64);
    double* CB = (double*)_mm_malloc(total*sizeof(double),64);
    fill_real(A,(size_t)d*n);

    /* prebuilt per-i packings for arm Pf */
    int* npv = (int*)malloc(n*sizeof(int));
    double** pre = (double**)malloc(n*sizeof(double*));
    size_t prebytes = 0;
    for (int i=0;i<n;i++){ npv[i] = (n-i+NR-1)/NR; prebytes += (size_t)npv[i]*d*NR*8; }
    int have_pf = (prebytes < (size_t)900*1024*1024);
    if (have_pf) for (int i=0;i<n;i++){
        pre[i] = (double*)_mm_malloc((size_t)npv[i]*d*NR*sizeof(double), 64);
        pack_phase(A,d,n,i,npv[i],pre[i]);
    }

    if (!strcmp(mode,"verify")){
        printf("verify n=%d d=%d KC=%d cells=%zu  (full-mantissa operands)\n", n,d,KC,total);
        memset(C0,0,total*8); ref_r3 (A,d,n,base,C0);
        memset(C1,0,total*8); ref_alt(A,d,n,base,C1); cmp("ref_alt(control, must be NO)",C1,C0,total);
        memset(CB,0,total*8); krs_B (A,d,n,base,CB,KC); cmp("B vs ref",CB,C0,total);
        memset(C1,0,total*8); krs_P (A,d,n,base,C1,KC); cmp("P vs B",C1,CB,total);
        memset(C1,0,total*8); krs_Q (A,d,n,base,C1,KC); cmp("Q vs B",C1,CB,total);
        if (have_pf){ memset(C1,0,total*8); krs_Pf(A,d,n,base,C1,KC,pre,npv); cmp("Pf vs B",C1,CB,total); }
        lane_waste(n);
        return 0;
    }

    printf("bench n=%d d=%d reps=%d KC=%d cells=%zu (%.2f MB)  prebuilt=%s (%.1f MB)\n",
           n,d,reps,KC,total,total*8.0/1048576.0, have_pf?"yes":"no", prebytes/1048576.0);
    struct { const char* nm; int id; } arms[] = {{"B  shipped (origin 0) ",0},
                                                 {"P  origin i, repack   ",1},
                                                 {"Pf origin i, prebuilt ",2},
                                                 {"Q  origin j0, 8-phase ",3}};
    double med[4];
    double* samples = (double*)malloc(sizeof(double)*reps);
    for (int a=0;a<4;a++){
        if (arms[a].id==2 && !have_pf){ med[a]=0; continue; }
        for (int r=0;r<reps;r++){
            memset(C1,0,total*sizeof(double));
            __asm__ __volatile__("" ::: "memory");
            double t0=wall();
            switch(arms[a].id){
                case 0: krs_B (A,d,n,base,C1,KC); break;
                case 1: krs_P (A,d,n,base,C1,KC); break;
                case 2: krs_Pf(A,d,n,base,C1,KC,pre,npv); break;
                case 3: krs_Q (A,d,n,base,C1,KC); break;
            }
            double t1=wall();
            __asm__ __volatile__("" ::: "memory");
            samples[r]=t1-t0;
        }
        for (int x=0;x<reps;x++) for (int y=x+1;y<reps;y++)
            if (samples[y]<samples[x]){ double s=samples[x]; samples[x]=samples[y]; samples[y]=s; }
        med[a]=samples[reps/2];
        double chk=0; for (size_t z=0;z<total;z+=(total/9973)+1) chk += C1[z];
        printf("  %s  median=%.5f s  %8.2f ns/useful-cell   %6.3fx vs B   chk=%.6g\n",
               arms[a].nm, med[a], med[a]*1e9/(double)total, med[0]/med[a], chk);
        fflush(stdout);
    }
    return 0;
}
