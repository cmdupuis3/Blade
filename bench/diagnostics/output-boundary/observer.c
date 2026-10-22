/* Compile as a distinct native object without LTO. The driver cannot see this
   implementation when issuing the post-t1 corrupt/check/restore calls. */
#include "b07_abi.h"
#include <math.h>

static double r5(int k,uint64_t seed) {
    const uint64_t mask=UINT64_C(2147483647), a=UINT64_C(1103515245);
    uint64_t h=(((uint64_t)k+seed)*a+12345)&mask;
    h=(h*(h/4096+1))&mask; h=(h*a+12345)&mask;
    h=((h*h)/65536)&mask; h=(h*a+12345)&mask;
    return (double)h/2147483648.0;
}
static int close_value(double value,double truth) {
    return isfinite(value) && fabs(value-truth)<=1e-12*fmax(1.0,fabs(truth));
}
int b07_observe(const B07Result* r,int inputs,int prepared,int* p_count,int* q_count) {
    int bad=0; *p_count=*q_count=0;
    /* No short circuit: each valid task result supplies two complete buffers. */
    for (int k=0;k<B07_N;++k) {
        double x=r5(k,10000019), y=r5(k,20000038);
        if (prepared && k==B07_N/2) x=x+0.125;
        double p=1.5*x+y, q=p*p;
        double actual_p=r->p[k], actual_q=r->q[k];
        ++*p_count; ++*q_count;
        if (!close_value(actual_p,p)) ++bad;
        if (!close_value(actual_q,q)) ++bad;
        if (inputs) {
            if (!close_value(r->x[k],x)) ++bad;
            if (!close_value(r->y[k],y)) ++bad;
        }
    }
    return bad;
}
