#ifndef B07_ABI_H
#define B07_ABI_H
#include <stddef.h>
#include <stdint.h>

/* Diagnostic ABI only. No ownership crosses to the C driver's allocator. */
enum { B07_OK=0, B07_BAD_STATE=1, B07_FAILED=2, B07_N=2053,
       B07_EMPTY=0, B07_READY=1, B07_RELEASED=2 };
typedef struct B07Result {
    int64_t abi_version, n;
    double *x, *y, *p, *q;
    double checksum, input_checksum;
    int32_t status, ownership;
} B07Result;

#ifdef __cplusplus
#define B07_ASSERT static_assert
extern "C" {
#else
#define B07_ASSERT _Static_assert
#endif
B07_ASSERT(sizeof(double)==8 && sizeof(void*)==8, "B07 requires the reviewed x64 ABI");
B07_ASSERT(sizeof(B07Result)==72 && offsetof(B07Result,x)==16
           && offsetof(B07Result,q)==40 && offsetof(B07Result,checksum)==48
           && offsetof(B07Result,input_checksum)==56 && offsetof(B07Result,status)==64
           && offsetof(B07Result,ownership)==68, "B07 result layout mismatch");

/* Each definition lives in a DIFFERENT native object from its caller. No pure,
   readonly, restrict, nofree or other optimizer-visible effect promises. */
int b07_pm52_task(B07Result*);
int b07_pm52_cleanup(B07Result*);
int64_t b07_task_size(void);
int b07_can_begin(const B07Result*);
int b07_validate_owned(const B07Result*);
void b07_publish(B07Result*, double*, double*, double*, double*, int64_t, double, double);
void b07_mark_released(B07Result*);
/* Referenced only by the separately identified prepared-mutation task. */
void b07_mutate_prepared(double*, double*, int64_t);
/* Separate native observer object: each invocation must inspect actual storage. */
int b07_observe(const B07Result*, int, int, int*, int*);
#ifdef __cplusplus
}
#endif
#undef B07_ASSERT
#endif
