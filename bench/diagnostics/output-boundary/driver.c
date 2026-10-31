/* Benchmark-only boundary proof. One opaque task call/interval per process.
   Compile this TU separately to a native object with -fno-lto. */
#include "b07_abi.h"
#include <math.h>
#include <stdio.h>
#include <string.h>
#ifndef _WIN32
#error B07 currently specifies the reviewed Windows QPC toolchain only
#endif
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#ifndef B07_EXPECT_PREPARED_MUTATION
#define B07_EXPECT_PREPARED_MUTATION 0
#endif
static int prepared_calls=0, prepared_failed=0;
static int assertions=0, failures=0;
static void check(int truth) { ++assertions; if (!truth) ++failures; }

int b07_can_begin(const B07Result* r) {
    return r && r->abi_version==0 && r->n==0 && !r->x && !r->y && !r->p && !r->q
        && r->checksum==0.0 && r->input_checksum==0.0 && r->status==B07_EMPTY && r->ownership==B07_EMPTY;
}
int b07_validate_owned(const B07Result* r) {
    if (!r || r->abi_version!=1 || r->n!=B07_N || r->status!=B07_READY || r->ownership!=B07_READY) return 0;
    uintptr_t a[4]={(uintptr_t)r->x,(uintptr_t)r->y,(uintptr_t)r->p,(uintptr_t)r->q};
    const uintptr_t bytes=B07_N*sizeof(double), object=(uintptr_t)r;
    if (object>UINTPTR_MAX-sizeof(*r)) return 0;
    for (int i=0;i<4;++i) {
        if (!a[i] || a[i]%_Alignof(double) || a[i]>UINTPTR_MAX-bytes) return 0;
        if (a[i]<object+sizeof(*r) && object<a[i]+bytes) return 0;
        for (int j=0;j<i;++j) if (a[i]<a[j]+bytes && a[j]<a[i]+bytes) return 0;
    }
    return 1;
}
void b07_publish(B07Result* r,double* x,double* y,double* p,double* q,int64_t n,double cs,double ics) {
    /* Values, not deferred expressions. The separately compiled task must
       finish their arithmetic to supply this ABI call before returning. */
    r->abi_version=1; r->n=n; r->x=x; r->y=y; r->p=p; r->q=q;
    r->checksum=cs; r->input_checksum=ics; r->status=B07_READY; r->ownership=B07_READY;
}
void b07_mark_released(B07Result* r) {
    r->x=r->y=r->p=r->q=NULL; r->n=0; r->checksum=r->input_checksum=0.0;
    r->status=B07_RELEASED; r->ownership=B07_RELEASED;
}
void b07_mutate_prepared(double* x,double* y,int64_t n) {
    ++prepared_calls;
    if (!x || !y || n!=B07_N) { prepared_failed=1; return; }
    x[B07_N/2]=x[B07_N/2]+0.125;
}
static double input(int k,uint64_t seed) {
    const uint64_t mask=UINT64_C(2147483647), a=UINT64_C(1103515245);
    uint64_t h=(((uint64_t)k+seed)*a+12345)&mask;
    h=(h*(h/4096+1))&mask; h=(h*a+12345)&mask;
    h=((h*h)/65536)&mask; h=(h*a+12345)&mask;
    return (double)h/2147483648.0;
}
static double expected_x(int k) {
    double x=input(k,10000019);
    return B07_EXPECT_PREPARED_MUTATION && k==B07_N/2 ? x+0.125 : x;
}
static int close_value(double got,double truth) {
    return isfinite(got) && fabs(got-truth)<=1e-12*fmax(1.0,fabs(truth));
}
static int absolute_path(const char* s) {
    size_t n=strlen(s);
    return (n>=3 && s[1]==':' && (s[2]=='/' || s[2]=='\\'))
        || (n>=2 && s[0]=='\\' && s[1]=='\\');
}
static void array_json(FILE* f,const double* p) {
    fputc('[',f);
    for (int k=0;k<B07_N;++k) {
        if (k) fputc(',',f);
        if (isfinite(p[k])) fprintf(f,"%.17g",p[k]); else fputs("null",f);
    }
    fputc(']',f);
}
int main(int argc,char** argv) {
    if (argc!=2 || !absolute_path(argv[1])) return 2;
    LARGE_INTEGER hz,t0,t1,t2,t3;
    B07Result result={0};
    if (!QueryPerformanceFrequency(&hz) || hz.QuadPart<=0 || b07_task_size()!=sizeof(result)) return 2;
    if (!QueryPerformanceCounter(&t0)) return 2;
    int task_status=b07_pm52_task(&result);
    if (!QueryPerformanceCounter(&t1)) return 2;
    /* Neither full observation, mutation probes, reporting nor cleanup precede t1. */
    if (task_status!=B07_OK || !b07_validate_owned(&result)) return 1;
    double cs=result.checksum, ics=result.input_checksum;
    double reference_cs=0.0,sx=0.0,sy=0.0;
    for (int k=0;k<B07_N;++k) {
        double x=expected_x(k),y=input(k,20000038),p=1.5*x+y;
        reference_cs=reference_cs+p*p; sx=sx+x; sy=sy+y;
    }
    int pc=0,qc=0;
    int canonical_bad=b07_observe(&result,1,B07_EXPECT_PREPARED_MUTATION,&pc,&qc);
    check(canonical_bad==0 && pc==B07_N && qc==B07_N);
    check(close_value(cs,reference_cs) && close_value(ics,sx+sy));
    check(!prepared_failed && prepared_calls==B07_EXPECT_PREPARED_MUTATION);

    /* Post-stop input mutation is a DISTINCT witness, not pre-producer mutation. */
    double saved_x=result.x[0], saved_y=result.y[B07_N-1];
    result.x[0]=saved_x+0.125; result.y[B07_N-1]=saved_y+0.25;
    int post_bad=b07_observe(&result,0,B07_EXPECT_PREPARED_MUTATION,&pc,&qc);
    int post_ok=post_bad==0 && pc==B07_N && qc==B07_N && result.checksum==cs && result.input_checksum==ics;
    check(post_ok);
    result.x[0]=saved_x; result.y[B07_N-1]=saved_y;
    double saved=result.p[B07_N/2]; result.p[B07_N/2]=saved+1.0;
    int p_bad=b07_observe(&result,0,B07_EXPECT_PREPARED_MUTATION,&pc,&qc);
    int p_detected=p_bad==1 && pc==B07_N && qc==B07_N;
    check(p_detected); result.p[B07_N/2]=saved;
    saved=result.q[B07_N-1]; result.q[B07_N-1]=saved+1.0;
    int q_bad=b07_observe(&result,0,B07_EXPECT_PREPARED_MUTATION,&pc,&qc);
    int q_detected=q_bad==1 && pc==B07_N && qc==B07_N;
    check(q_detected); result.q[B07_N-1]=saved;
    int restored=b07_observe(&result,1,B07_EXPECT_PREPARED_MUTATION,&pc,&qc)==0 && pc==B07_N && qc==B07_N;
    check(restored);

    FILE* f=fopen(argv[1],"wb");
    if (!f) { b07_pm52_cleanup(&result); return 2; }
    /* Streaming arrays before release avoids retained observer copies. */
    fputs("{\"schema\":1,\"contract\":\"B07-PM52-opaque-task-v1\",\"n\":2053,\"p\":",f);
    array_json(f,result.p); fputs(",\"q\":",f); array_json(f,result.q);
    if (!QueryPerformanceCounter(&t2)) { fclose(f); b07_pm52_cleanup(&result); return 2; }
    B07Result wrong=result; wrong.n=B07_N-1;
    int bad_n=b07_pm52_cleanup(&wrong)!=B07_OK; check(bad_n);
    wrong=result; wrong.abi_version=99;
    int bad_abi=b07_pm52_cleanup(&wrong)!=B07_OK; check(bad_abi);
    wrong=result; wrong.q=wrong.p;
    int bad_alias=b07_pm52_cleanup(&wrong)!=B07_OK; check(bad_alias);
    wrong=result; wrong.x=NULL;
    int bad_null=b07_pm52_cleanup(&wrong)!=B07_OK; check(bad_null);
    int release_status=b07_pm52_cleanup(&result); check(release_status==B07_OK);
    int duplicate=b07_pm52_cleanup(&result)!=B07_OK; check(duplicate);
    int cleared=!result.x && !result.y && !result.p && !result.q && result.n==0
        && result.status==B07_RELEASED && result.ownership==B07_RELEASED;
    check(cleared);
    if (!QueryPerformanceCounter(&t3)) { fclose(f); return 2; }
    fputs(",\"checksum\":",f);
    if (isfinite(cs)) fprintf(f,"%.17g",cs); else fputs("null",f);
    fputs(",\"input_checksum\":",f);
    if (isfinite(ics)) fprintf(f,"%.17g",ics); else fputs("null",f);
    fprintf(f,",\"task_status\":%d,"
        "\"task_calls\":1,\"timed_intervals\":1,\"observer_calls\":5,\"cleanup_calls\":6,"
        "\"abi_size\":%zu,\"prepared_mutation\":%s,\"prepared_calls\":%d,"
        "\"canonical_bad\":%d,\"p_count\":%d,\"q_count\":%d,\"post_input_mutation_pass\":%s,"
        "\"p_corruption_detected\":%s,\"q_corruption_detected\":%s,\"restored_pass\":%s,"
        "\"bad_n_rejected\":%s,\"bad_abi_rejected\":%s,\"alias_rejected\":%s,\"null_rejected\":%s,"
        "\"cleanup_status\":%d,\"duplicate_cleanup_rejected\":%s,\"descriptors_cleared\":%s,"
        "\"assertions\":%d,\"failed\":%d,\"opaque_task_interval_s\":%.17g,\"observer_s\":%.17g,\"cleanup_s\":%.17g,"
        "\"compiler_enclosure_established\":null,\"performance_eligible\":false}\n",
        task_status,sizeof(result),B07_EXPECT_PREPARED_MUTATION?"true":"false",prepared_calls,
        canonical_bad,pc,qc,post_ok?"true":"false",p_detected?"true":"false",q_detected?"true":"false",restored?"true":"false",
        bad_n?"true":"false",bad_abi?"true":"false",bad_alias?"true":"false",bad_null?"true":"false",release_status,
        duplicate?"true":"false",cleared?"true":"false",assertions,failures,
        (double)(t1.QuadPart-t0.QuadPart)/hz.QuadPart,(double)(t2.QuadPart-t1.QuadPart)/hz.QuadPart,
        (double)(t3.QuadPart-t2.QuadPart)/hz.QuadPart);
    int io_bad=ferror(f); if (fclose(f)!=0) io_bad=1;
    return failures || io_bad ? 1 : 0;
}
