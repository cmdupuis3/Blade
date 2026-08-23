/* segmented_scan_safe.c -- does a numerically SAFE segmented scan recover the
 * vectorized win that README correction 12 declares "unusable"?
 *
 * Correction 12 rejects the whole prefix-sum family because
 *     out[g] = S[off[g+1]] - S[off[g]]
 * differences against the GLOBAL prefix, so a late short segment cancels away
 * its own significant digits.  That diagnosis is correct.  The conclusion
 * ("unusable") skips the standard repair: a SEGMENTED (reset) scan, whose
 * running sum is re-zeroed at every segment head, so the value standing at a
 * segment's last element IS that segment's sum.  Nothing is ever subtracted,
 * so the cancellation is not merely bounded -- it does not exist.
 *
 * ARMS (all produce out[0..G) = the sum of each segment)
 *   ref_ld     REFERENCE.  Per-segment sequential sum in long double with
 *              Neumaier compensation (x87, 64-bit mantissa, ~1e-19 relative).
 *              Independent of every double pipeline below.
 *   ref_sc     per-segment sequential sum in double.  The safe baseline and
 *              what Blade emits today.
 *   pfx_mat    THE TRAP, literal form: materialize S[0..N], then difference.
 *   pfx_run    THE TRAP, fair form: carry the running global prefix and
 *              snapshot at boundaries.  No S array, so it has the repair's
 *              memory shape and the speed comparison isolates arithmetic.
 *   seg_seq    THE REPAIR, scalar: reset the running sum at each head.
 *   seg_simd4  THE REPAIR, vectorized: 4-lane AVX2 segmented inclusive scan
 *              (2-step Sklansky with head-flag propagation), flags built on
 *              the fly from `off`, segment sums extracted at segment ends.
 *              No S array and no differencing.
 *
 * BITWISE COLUMN -- verified on FULL-MANTISSA random doubles, with a
 * bit-changing control that MUST read NO (README correction 17: a fixture
 * whose operands cannot round makes a bitwise column inert):
 *   seg_seq   vs ref_sc  -> MUST read YES (identical summation order)
 *   pfx_run   vs ref_sc  -> MUST read NO  (the control arm)
 * Build with -DDYADIC to see the column go inert on small-integer data, the
 * way correction 12's own fixture did.
 *
 * build: gcc -O3 -march=native -ffp-contract=fast -o segsafe.exe segmented_scan_safe.c
 * run:   ./segsafe.exe verify
 *        ./segsafe.exe accuracy
 *        ./segsafe.exe bench [N] [reps]
 */
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <stdint.h>
#include <math.h>
#include <immintrin.h>
#include <windows.h>

typedef int idx_t;

static double now_s(void){
    LARGE_INTEGER f,c; QueryPerformanceFrequency(&f); QueryPerformanceCounter(&c);
    return (double)c.QuadPart/(double)f.QuadPart;
}
#define BARRIER() __asm__ __volatile__("" ::: "memory")

static uint64_t rng_s = 0x243F6A8885A308D3ull;
static uint64_t rnd64(void){
    rng_s ^= rng_s<<13; rng_s ^= rng_s>>7; rng_s ^= rng_s<<17; return rng_s;
}
/* FULL-MANTISSA random double in (-1,1): every mantissa bit populated, so
 * sums round.  The opposite of the dyadic fixture correction 17 indicts. */
static double rndfull(void){
    uint64_t m = rnd64();
    double x = (double)(int64_t)(m >> 11) * (1.0/9007199254740992.0);
    return (m & 1) ? x : -x;
}
#ifdef DYADIC
/* deliberately inert operands: small integers, every partial sum exact */
static double rndgen(void){ return (double)((int)(rnd64() % 9) - 4); }
#else
static double rndgen(void){ return rndfull(); }
#endif

/* ============================ REFERENCE ============================= */
__attribute__((noinline))
static void ref_ld(const double *v, const idx_t *off, int G, long double *out){
    for(int g=0; g<G; ++g){
        long double s=0.0L, c=0.0L;
        for(idx_t i=off[g]; i<off[g+1]; ++i){
            long double x=(long double)v[i], t=s+x;
            if(fabsl(s)>=fabsl(x)) c += (s-t)+x; else c += (x-t)+s;
            s=t;
        }
        out[g]=s+c;
    }
}

/* ===================== ARM ref_sc : safe baseline ==================== */
__attribute__((noinline))
static void ref_sc(const double *v, const idx_t *off, int G, double *out){
    for(int g=0; g<G; ++g){
        double s=0.0;
        for(idx_t i=off[g]; i<off[g+1]; ++i) s+=v[i];
        out[g]=s;
    }
}

/* ============== ARM pfx_mat : THE TRAP, literal form ================ */
__attribute__((noinline))
static void pfx_mat(const double *v, const idx_t *off, int G, idx_t N,
                    double *S, double *out){
    S[0]=0.0;
    for(idx_t i=0;i<N;++i) S[i+1]=S[i]+v[i];
    for(int g=0; g<G; ++g) out[g]=S[off[g+1]]-S[off[g]];
}

/* ============== ARM pfx_run : THE TRAP, fair form =================== */
__attribute__((noinline))
static void pfx_run(const double *v, const idx_t *off, int G, double *out){
    double P=0.0;
    idx_t i=0;
    for(int g=0; g<G; ++g){
        double Pstart=P;
        idx_t e=off[g+1];
        for(; i<e; ++i) P+=v[i];
        out[g]=P-Pstart;          /* the cancellation */
    }
}

/* ============ ARM seg_seq : THE REPAIR, scalar reset scan =========== */
__attribute__((noinline))
static void seg_seq(const double *v, const idx_t *off, int G, double *out){
    double run;
    idx_t i=0;
    for(int g=0; g<G; ++g){
        run=0.0;                  /* the reset -- this is the entire repair */
        idx_t e=off[g+1];
        for(; i<e; ++i) run+=v[i];
        out[g]=run;
    }
}

/* ========= ARM seg_simd4 : THE REPAIR, 4-lane segmented scan ========= */
static inline __m256d shift1(__m256d x){      /* [0, x0, x1, x2] */
    __m256d p = _mm256_permute4x64_pd(x, _MM_SHUFFLE(2,1,0,0));
    return _mm256_blend_pd(p, _mm256_setzero_pd(), 0x1);
}
static inline __m256d shift2(__m256d x){      /* [0, 0, x0, x1] */
    __m256d p = _mm256_permute4x64_pd(x, _MM_SHUFFLE(1,0,0,0));
    return _mm256_blend_pd(p, _mm256_setzero_pd(), 0x3);
}

/* lane 3 of x, without a round trip through memory (a stack store followed by
 * a scalar reload is a store-to-load-forwarding stall, and this is on the
 * critical path of every window) */
static inline double lane3(__m256d x){
    return _mm256_cvtsd_f64(_mm256_permute4x64_pd(x, _MM_SHUFFLE(3,3,3,3)));
}

__attribute__((noinline))
static void seg_simd4(const double *v, const idx_t *off, int G, idx_t N, double *out){
    double carry = 0.0;
    int gnext = 1;     /* next segment HEAD (off[1..G-1]) not yet consumed */
    int gout  = 0;     /* next segment whose sum must be emitted            */
    idx_t i = 0;
    double lanes[4];

    for(; i + 4 <= N; i += 4){
        __m256d x = _mm256_loadu_pd(v+i);
        /* Head flags are built IN REGISTERS by comparing the lane-index
         * vector against each boundary that lands in this window.  The
         * obvious form -- write a uint64_t hf[4] then vector-load it -- costs
         * a store-forwarding stall per window, which is most of the cost once
         * segments are short enough that every window has a boundary. */
        __m256i idxv = _mm256_setr_epi64x(i, i+1, i+2, i+3);
        __m256i flg  = _mm256_setzero_si256();
        int any = 0;
        while(gnext < G && off[gnext] < i+4){
            if(off[gnext] >= i){
                flg = _mm256_or_si256(flg,
                        _mm256_cmpeq_epi64(idxv, _mm256_set1_epi64x(off[gnext])));
                any = 1;
            }
            ++gnext;
        }
        if(any){
            __m256d fl = _mm256_castsi256_pd(flg);
            __m256d s1 = shift1(x), f1 = shift1(fl);
            x  = _mm256_add_pd(x, _mm256_andnot_pd(fl, s1));
            fl = _mm256_or_pd(fl, f1);
            __m256d s2 = shift2(x), f2 = shift2(fl);
            x  = _mm256_add_pd(x, _mm256_andnot_pd(fl, s2));
            fl = _mm256_or_pd(fl, f2);
            x  = _mm256_add_pd(x, _mm256_andnot_pd(fl, _mm256_set1_pd(carry)));
        } else {
            x = _mm256_add_pd(x, shift1(x));
            x = _mm256_add_pd(x, shift2(x));
            x = _mm256_add_pd(x, _mm256_set1_pd(carry));
        }
        carry = lane3(x);
        /* Emit only when a segment END actually lands here, so the vector
         * store is paid on emitting windows only, never on the common one. */
        if(gout < G && off[gout+1]-1 < i+4){
            _mm256_storeu_pd(lanes, x);
            while(gout < G && off[gout+1]-1 < i+4 && off[gout+1] <= N){
                idx_t e = off[gout+1];
                if(e <= off[gout]) { out[gout] = 0.0; ++gout; continue; }
                if(e-1 < i) break;
                out[gout] = lanes[e-1-i];
                ++gout;
            }
        }
    }
    /* scalar tail, same reset semantics */
    for(; i<N; ++i){
        while(gnext < G && off[gnext]==i){ carry = 0.0; ++gnext; }
        carry += v[i];
        while(gout < G && off[gout+1]-1 == i){
            out[gout] = (off[gout+1] > off[gout]) ? carry : 0.0;
            ++gout;
        }
    }
    while(gout < G){ out[gout] = 0.0; ++gout; }
}

/* ===== ARM pfx_simd4 : THE TRAP, vectorized -- the honest speed rival =====
 * Identical Sklansky structure to seg_simd4 with the reset REMOVED: one
 * global prefix, differenced at the boundaries.  This is the arm that makes
 * "does safety cost speed?" a fair question, because seg_simd4 differs from
 * it ONLY by the head-flag logic.  It still needs the scan values at both
 * boundaries, so it carries the same emit machinery. */
__attribute__((noinline))
static void pfx_simd4(const double *v, const idx_t *off, int G, idx_t N,
                      double *S, double *out){
    __m256d carry = _mm256_setzero_pd();
    idx_t i = 0;
    double run = 0.0;
    S[0] = 0.0;
    for(; i + 4 <= N; i += 4){
        __m256d x = _mm256_loadu_pd(v+i);
        x = _mm256_add_pd(x, shift1(x));
        x = _mm256_add_pd(x, shift2(x));
        x = _mm256_add_pd(x, carry);
        _mm256_storeu_pd(S+i+1, x);
        carry = _mm256_set1_pd(lane3(x));
    }
    run = (i>0)? S[i] : 0.0;
    for(; i<N; ++i){ run += v[i]; S[i+1] = run; }
    for(int g=0; g<G; ++g) out[g] = S[off[g+1]] - S[off[g]];
}

/* ============================ harness ============================== */
typedef struct { double maxrel; int worstg; double got, want; } acc_t;

static acc_t accuracy(const double *o, const long double *r, int G){
    acc_t a; a.maxrel=0; a.worstg=-1; a.got=0; a.want=0;
    for(int g=0; g<G; ++g){
        long double w = r[g];
        long double d = fabsl((long double)o[g] - w);
        long double rel = (fabsl(w) > 0.0L) ? d/fabsl(w) : (d>0.0L ? 1.0L : 0.0L);
        if((double)rel > a.maxrel){ a.maxrel=(double)rel; a.worstg=g; a.got=o[g]; a.want=(double)w; }
    }
    return a;
}
static int bitsame(const double *a, const double *b, int G){
    return memcmp(a,b,(size_t)G*sizeof(double))==0;
}

/* ---- input patterns ---- */
/* 0 uniform    : equal-length segments, full-mantissa values         (control)
 * 1 late-short : one huge same-sign leading segment builds an enormous global
 *                prefix; the remaining segments are 4 elements of tiny values.
 *                THE adversarial case correction 12 describes.
 * 2 mixed-mag  : each segment mixes 1e8 and 1e-8 magnitudes.
 */
static void gen(double *v, idx_t *off, idx_t N, int G, int pat){
    if(pat==0){
        idx_t per = N/G;
        for(int g=0; g<=G; ++g) off[g] = (g==G)? N : g*per;
        for(idx_t i=0;i<N;++i) v[i]=rndgen();
    } else if(pat==1){
        idx_t tail = 4*(G-1);
        if(tail >= N) tail = N/2;
        idx_t head = N - tail;
        off[0]=0; off[1]=head;
        for(int g=2; g<=G; ++g){ idx_t p = off[g-1]+4; off[g] = (p>N||g==G)? N : p; }
        for(idx_t i=0;i<head;++i)  v[i] = 1.0e6*(1.0+0.25*fabs(rndgen()));   /* same sign, large */
        for(idx_t i=head;i<N;++i)  v[i] = 1.0e-6*rndgen();                   /* tiny */
    } else {
        idx_t per = N/G;
        for(int g=0; g<=G; ++g) off[g] = (g==G)? N : g*per;
        for(idx_t i=0;i<N;++i) v[i] = ((i&3)<2 ? 1.0e8 : 1.0e-8) * rndgen();
    }
}
static const char *patname[3]={"uniform (control)","late-short (adversarial)","mixed-magnitude"};

static int cmpd(const void*a,const void*b){ double x=*(const double*)a,y=*(const double*)b; return x<y?-1:x>y; }
static double median(double *t,int n){ qsort(t,n,sizeof(double),cmpd); return t[n/2]; }

int main(int argc, char **argv){
    const char *mode = (argc>1)? argv[1] : "verify";

#ifdef DYADIC
    printf("*** -DDYADIC: small-integer operands, every partial sum exact.\n");
    printf("*** The bitwise column below is INERT by construction (correction 17).\n\n");
#endif

    if(!strcmp(mode,"verify") || !strcmp(mode,"accuracy")){
        idx_t N = 1<<20; N += 4099;          /* non-power-of-two, per CLAUDE.md */
        int Gs[] = {17, 1021, 65537};
        for(int gi=0; gi<3; ++gi){
            int G = Gs[gi]; if(G > N/8) continue;
            double *v=_mm_malloc((size_t)N*8,64);
            idx_t  *off=_mm_malloc((size_t)(G+1)*sizeof(idx_t),64);
            double *S=_mm_malloc((size_t)(N+1)*8,64);
            double *o1=_mm_malloc((size_t)G*8,64), *o2=_mm_malloc((size_t)G*8,64);
            double *o3=_mm_malloc((size_t)G*8,64), *o4=_mm_malloc((size_t)G*8,64);
            double *o5=_mm_malloc((size_t)G*8,64), *o6=_mm_malloc((size_t)G*8,64);
            long double *rl=malloc((size_t)G*sizeof(long double));
            for(int pat=0; pat<3; ++pat){
                rng_s = 0x243F6A8885A308D3ull ^ (uint64_t)(G*31+pat);
                gen(v,off,N,G,pat);
                ref_ld(v,off,G,rl);
                ref_sc(v,off,G,o1);
                pfx_mat(v,off,G,N,S,o2);
                pfx_run(v,off,G,o3);
                seg_seq(v,off,G,o4);
                seg_simd4(v,off,G,N,o5);
                pfx_simd4(v,off,G,N,S,o6);
                acc_t a1=accuracy(o1,rl,G), a2=accuracy(o2,rl,G),
                      a3=accuracy(o3,rl,G), a4=accuracy(o4,rl,G), a5=accuracy(o5,rl,G),
                      a6=accuracy(o6,rl,G);
                printf("N=%d G=%-6d  %-26s\n", (int)N, G, patname[pat]);
                printf("   %-26s max rel err %10.3e\n","ref_sc  (safe baseline)",a1.maxrel);
                printf("   %-26s max rel err %10.3e   %s\n","pfx_mat (TRAP literal)",a2.maxrel,
                       a2.maxrel>1e-9?"<-- DIGITS LOST":"");
                printf("   %-26s max rel err %10.3e   %s\n","pfx_run (TRAP fair)",a3.maxrel,
                       a3.maxrel>1e-9?"<-- DIGITS LOST":"");
                printf("   %-26s max rel err %10.3e\n","seg_seq (REPAIR scalar)",a4.maxrel);
                printf("   %-26s max rel err %10.3e\n","seg_simd4 (REPAIR simd)",a5.maxrel);
                printf("   %-26s max rel err %10.3e   %s\n","pfx_simd4 (TRAP simd)",a6.maxrel,
                       a6.maxrel>1e-9?"<-- DIGITS LOST":"");
                printf("   bitwise vs ref_sc:  seg_seq=%-4s  seg_simd4=%-4s  pfx_run=%-4s (control MUST be NO)\n\n",
                       bitsame(o4,o1,G)?"yes":"NO", bitsame(o5,o1,G)?"yes":"NO", bitsame(o3,o1,G)?"yes":"NO");
            }
            _mm_free(v);_mm_free(off);_mm_free(S);_mm_free(o1);_mm_free(o2);
            _mm_free(o3);_mm_free(o4);_mm_free(o5);_mm_free(o6);free(rl);
        }
        return 0;
    }

    if(!strcmp(mode,"bench")){
        idx_t N = (argc>2)? atoi(argv[2]) : (1<<20)+4099;
        int reps = (argc>3)? atoi(argv[3]) : 13;
        int Gs[] = {17, 1021, 16381, 131071, 262143, 350891, 525337};
        printf("bench N=%d, medians over %d interleaved reps, ns/element\n", (int)N, reps);
        printf("  %-8s %9s %9s %9s %9s %9s %9s  %s\n","G",
               "ref_sc","pfx_mat","pfx_run","seg_seq","pfx_simd4","seg_simd4",
               "safe/trap  best-safe/best-trap");
        for(int gi=0; gi<7; ++gi){
            int G=Gs[gi]; if(G > N/2) continue;
            double *v=_mm_malloc((size_t)N*8,64);
            idx_t  *off=_mm_malloc((size_t)(G+1)*sizeof(idx_t),64);
            double *S=_mm_malloc((size_t)(N+1)*8,64);
            double *o=_mm_malloc((size_t)G*8,64);
            rng_s = 0xABCDEF0123456789ull ^ (uint64_t)G;
            gen(v,off,N,G,0);
            double t1[64],t2[64],t3[64],t4[64],t5[64],t6[64];
            double sink=0.0;
            for(int r=0;r<reps;++r){
                double a,b;
                a=now_s(); BARRIER(); ref_sc(v,off,G,o);        BARRIER(); b=now_s(); t1[r]=b-a; sink+=o[G/2];
                a=now_s(); BARRIER(); pfx_mat(v,off,G,N,S,o);   BARRIER(); b=now_s(); t2[r]=b-a; sink+=o[G/2];
                a=now_s(); BARRIER(); pfx_run(v,off,G,o);       BARRIER(); b=now_s(); t3[r]=b-a; sink+=o[G/2];
                a=now_s(); BARRIER(); seg_seq(v,off,G,o);       BARRIER(); b=now_s(); t4[r]=b-a; sink+=o[G/2];
                a=now_s(); BARRIER(); pfx_simd4(v,off,G,N,S,o); BARRIER(); b=now_s(); t6[r]=b-a; sink+=o[G/2];
                a=now_s(); BARRIER(); seg_simd4(v,off,G,N,o);   BARRIER(); b=now_s(); t5[r]=b-a; sink+=o[G/2];
            }
            double m1=median(t1,reps)/N*1e9, m2=median(t2,reps)/N*1e9,
                   m3=median(t3,reps)/N*1e9, m4=median(t4,reps)/N*1e9,
                   m5=median(t5,reps)/N*1e9, m6=median(t6,reps)/N*1e9;
            double bestsafe = (m4<m5)?m4:m5;
            double besttrap = (m2<m3)?m2:m3; if(m6<besttrap) besttrap=m6;
            printf("  %-8d %9.4f %9.4f %9.4f %9.4f %9.4f %9.4f  %6.2fx    %6.2fx  [sink %.3g]\n",
                   G,m1,m2,m3,m4,m6,m5, m6/m5, besttrap/bestsafe, sink);
            _mm_free(v);_mm_free(off);_mm_free(S);_mm_free(o);
        }
        return 0;
    }
    printf("usage: %s verify|accuracy|bench [N] [reps]\n", argv[0]);
    return 1;
}
