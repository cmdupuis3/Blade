/* clang64's mingw headers redirect clock_gettime to a clock_gettime64 symbol
 * that its CRT does not export.  Substitute QueryPerformanceCounter, which is
 * what mingw's clock_gettime is implemented over anyway. */
#if defined(__clang__) && defined(_WIN32)
#include <windows.h>
static int blade_cgt(int id, struct timespec* ts){
    (void)id; static LARGE_INTEGER f; LARGE_INTEGER c;
    if(!f.QuadPart) QueryPerformanceFrequency(&f);
    QueryPerformanceCounter(&c);
    ts->tv_sec  = (time_t)(c.QuadPart / f.QuadPart);
    ts->tv_nsec = (long)((c.QuadPart % f.QuadPart) * 1000000000LL / f.QuadPart);
    return 0;
}
#undef CLOCK_MONOTONIC
#define CLOCK_MONOTONIC 1
#define clock_gettime blade_cgt
#endif
