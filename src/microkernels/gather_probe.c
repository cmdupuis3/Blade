/* gather_probe.c -- adversarial test of README correction 8.
 *
 * "The mirrored read emits vgather."  ->  "Neither gcc nor clang emits
 *  vgatherqpd on znver3 at all (Zen tuning disables it). What appears is a
 *  HAND-ROLLED gather -- extract indices, 4 scalar loads, vinsertf128, ~10 ops
 *  per vector.  SAME DISEASE, DIFFERENT ENCODING."
 *
 * The observation ("no vgather is emitted") and the conclusion ("same disease")
 * are separable.  If an EXPLICIT _mm256_i32gather_pd beats the hand-rolled
 * sequence the compiler produces, the conclusion is wrong even though the
 * observation is right -- and there is a microkernel in it.  If it loses, the
 * compiler's refusal is a correct cost model and the conclusion is confirmed
 * with harder evidence than "no vgather appeared".
 *
 * Arms, all with 4 independent accumulator chains so none is latency-starved:
 *   auto32/auto64  plain  acc += P[idx[i]]  -- what the compiler chooses
 *   hand           explicit 4 scalar loads + _mm256_set_pd (the sequence gcc
 *                  actually emits, written by hand so it can be timed alone)
 *   g32/g64        explicit _mm256_i32gather_pd / _mm256_i64gather_pd
 *   synth          NO INDEX ARRAY AT ALL: the packed-triangle mirror column has
 *                  indices idx(j+1)-idx(j) = n-j-1, an arithmetic progression of
 *                  strides, so the addresses are recoverable with two adds.
 *                  This removes 4 B/element of index traffic that every gather
 *                  form, hardware or hand-rolled, has to pay.
 *
 * The index pattern is the REAL one: the canonicalizing mirrored read
 * P[RB[j] + (i-j)] that mirror_transpose.cpp's ref_sym performs.
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

/* ------- arms.  m = number of gathered elements, must be a multiple of 16 ---- */
__attribute__((noinline))
static double a_auto32(const double* BR P,const int32_t* BR idx,size_t m){
    double a0=0,a1=0,a2=0,a3=0;
    for(size_t i=0;i+4<=m;i+=4){
        a0+=P[idx[i]]; a1+=P[idx[i+1]]; a2+=P[idx[i+2]]; a3+=P[idx[i+3]];
    }
    return (a0+a1)+(a2+a3);
}
__attribute__((noinline))
static double a_auto64(const double* BR P,const int64_t* BR idx,size_t m){
    double a0=0,a1=0,a2=0,a3=0;
    for(size_t i=0;i+4<=m;i+=4){
        a0+=P[idx[i]]; a1+=P[idx[i+1]]; a2+=P[idx[i+2]]; a3+=P[idx[i+3]];
    }
    return (a0+a1)+(a2+a3);
}
__attribute__((noinline))
static double a_hand(const double* BR P,const int32_t* BR idx,size_t m){
    __m256d acc[4]; for(int t=0;t<4;t++) acc[t]=_mm256_setzero_pd();
    for(size_t i=0;i+16<=m;i+=16)
        for(int t=0;t<4;t++){
            const int32_t* q=idx+i+4*t;
            __m256d v=_mm256_set_pd(P[q[3]],P[q[2]],P[q[1]],P[q[0]]);
            acc[t]=_mm256_add_pd(acc[t],v);
        }
    __m256d z=_mm256_add_pd(_mm256_add_pd(acc[0],acc[1]),_mm256_add_pd(acc[2],acc[3]));
    return hsum256(z);
}
__attribute__((noinline))
static double a_g32(const double* BR P,const int32_t* BR idx,size_t m){
    __m256d acc[4]; for(int t=0;t<4;t++) acc[t]=_mm256_setzero_pd();
    for(size_t i=0;i+16<=m;i+=16)
        for(int t=0;t<4;t++){
            __m128i vi=_mm_loadu_si128((const __m128i*)(idx+i+4*t));
            acc[t]=_mm256_add_pd(acc[t],_mm256_i32gather_pd(P,vi,8));
        }
    __m256d z=_mm256_add_pd(_mm256_add_pd(acc[0],acc[1]),_mm256_add_pd(acc[2],acc[3]));
    return hsum256(z);
}
__attribute__((noinline))
static double a_g64(const double* BR P,const int64_t* BR idx,size_t m){
    __m256d acc[4]; for(int t=0;t<4;t++) acc[t]=_mm256_setzero_pd();
    for(size_t i=0;i+16<=m;i+=16)
        for(int t=0;t<4;t++){
            __m256i vi=_mm256_loadu_si256((const __m256i*)(idx+i+4*t));
            acc[t]=_mm256_add_pd(acc[t],_mm256_i64gather_pd(P,vi,8));
        }
    __m256d z=_mm256_add_pd(_mm256_add_pd(acc[0],acc[1]),_mm256_add_pd(acc[2],acc[3]));
    return hsum256(z);
}
/* index-free: walk the mirror column of row i, j = 0..i-1,
 *   idx(j) = RB[j] + (i-j),   idx(j+1)-idx(j) = n-j-1
 * so the address advances by a linearly decreasing stride: two adds, no loads. */
__attribute__((noinline))
static double a_synth(const double* BR P,size_t n,size_t i,size_t count){
    double a0=0,a1=0,a2=0,a3=0;
    size_t p=i;                    /* RB[0] + i - 0 */
    size_t st=n-1;                 /* first delta */
    size_t j=0;
    for(; j+4<=count; j+=4){
        a0+=P[p]; p+=st; st--;
        a1+=P[p]; p+=st; st--;
        a2+=P[p]; p+=st; st--;
        a3+=P[p]; p+=st; st--;
    }
    for(; j<count; j++){ a0+=P[p]; p+=st; st--; }
    return (a0+a1)+(a2+a3);
}

/* ================================ driver ================================= */
#define REPS 11
int main(int argc,char**argv){
    int reps=(argc>1)?atoi(argv[1]):REPS; if(reps>REPS) reps=REPS;
    printf("=== gather_probe: correction 8 ===\ncompiler %s   median of %d\n",__VERSION__,reps);
    printf("index pattern = packed-triangle mirror column  idx(j)=RB[j]+(i-j)\n\n");
    printf("%8s %10s %11s %11s %11s %11s %11s %11s %9s\n",
           "n","P bytes","auto32 ns/e","auto64 ns/e","hand ns/e","g32 ns/e","g64 ns/e","synth ns/e","g32/hand");
    const size_t ns[4]={61,701,2003,6007};
    for(int q=0;q<4;q++){
        size_t n=ns[q], cells=n*(n+1)/2;
        double* P=(double*)_mm_malloc(cells*sizeof(double),64);
        if(!P){ printf("alloc fail\n"); continue; }
        for(size_t t=0;t<cells;t++) P[t]=(double)(t&1023)*0.5+0.25;
        /* build the index list: for every row i, the mirror column j=0..i-1 */
        size_t m=0; for(size_t i=0;i<n;i++) m+=i;
        int32_t* i32=(int32_t*)_mm_malloc((m+16)*sizeof(int32_t),64);
        int64_t* i64=(int64_t*)_mm_malloc((m+16)*sizeof(int64_t),64);
        size_t w=0;
        for(size_t i=0;i<n;i++)
            for(size_t j=0;j<i;j++){
                size_t id=j*(2*n-j+1)/2 + (i-j);
                i32[w]=(int32_t)id; i64[w]=(int64_t)id; w++;
            }
        for(size_t t=m;t<m+16;t++){ i32[t]=i32[m?m-1:0]; i64[t]=i64[m?m-1:0]; }
        size_t mm=(m/16)*16;
        if(mm==0){ _mm_free(P);_mm_free(i32);_mm_free(i64); continue; }
        /* correctness: every arm must agree to the last bit for the pure sum
           when it visits the same elements in the same 4-way split */
        double r_hand=a_hand(P,i32,mm), r_g32=a_g32(P,i32,mm), r_g64=a_g64(P,i64,mm);
        int ok = (memcmp(&r_hand,&r_g32,8)==0) && (memcmp(&r_hand,&r_g64,8)==0);
        double t1[REPS],t2[REPS],t3[REPS],t4[REPS],t5[REPS],t6[REPS];
        double sink=0;
        int inner = (cells*8 < 400000) ? 32 : 1;
        for(int r=0;r<reps;r++){
            OBSERVE(P);BARRIER(); double s=now_s();
            for(int u=0;u<inner;u++){ OBSERVE(P);OBSERVE(i32); sink+=a_auto32(P,i32,mm); OBSERVE(&sink); } t1[r]=(now_s()-s)/inner; BARRIER();
            s=now_s(); for(int u=0;u<inner;u++){ OBSERVE(P);OBSERVE(i64); sink+=a_auto64(P,i64,mm); OBSERVE(&sink); } t2[r]=(now_s()-s)/inner; BARRIER();
            s=now_s(); for(int u=0;u<inner;u++){ OBSERVE(P);OBSERVE(i32); sink+=a_hand(P,i32,mm); OBSERVE(&sink); }   t3[r]=(now_s()-s)/inner; BARRIER();
            s=now_s(); for(int u=0;u<inner;u++){ OBSERVE(P);OBSERVE(i32); sink+=a_g32(P,i32,mm); OBSERVE(&sink); }    t4[r]=(now_s()-s)/inner; BARRIER();
            s=now_s(); for(int u=0;u<inner;u++){ OBSERVE(P);OBSERVE(i64); sink+=a_g64(P,i64,mm); OBSERVE(&sink); }    t5[r]=(now_s()-s)/inner; BARRIER();
            s=now_s(); for(int u=0;u<inner;u++){ double z=0; OBSERVE(P);
                          for(size_t i=1;i<n;i++) z+=a_synth(P,n,i,i); sink+=z; OBSERVE(&sink); }
                        t6[r]=(now_s()-s)/inner; BARRIER();
        }
        double e=(double)mm;
        printf("%8zu %10.0f %11.4f %11.4f %11.4f %11.4f %11.4f %11.4f %8.3fx  %s\n",
               n,(double)cells*8.0,
               median(t1,reps)*1e9/e, median(t2,reps)*1e9/e, median(t3,reps)*1e9/e,
               median(t4,reps)*1e9/e, median(t5,reps)*1e9/e, median(t6,reps)*1e9/e,
               median(t4,reps)/median(t3,reps), ok?"bitwise-agree":"*** DISAGREE ***");
        if(sink==1.25) printf("x");
        _mm_free(P);_mm_free(i32);_mm_free(i64);
    }
    printf("\nlower ns/element is better.  g32/hand < 1 would REFUTE correction 8's\n"
           "conclusion that the hardware gather is 'the same disease'.\n");
    return 0;
}
