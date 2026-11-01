/* Linux stand-in for the two Win32 calls bench_support.f90 binds: CLOCK_MONOTONIC in ns. */
#include <time.h>
int QueryPerformanceCounter(long long *cnt) {
    struct timespec ts; clock_gettime(CLOCK_MONOTONIC, &ts);
    *cnt = (long long)ts.tv_sec * 1000000000LL + ts.tv_nsec; return 1;
}
int QueryPerformanceFrequency(long long *freq) { *freq = 1000000000LL; return 1; }
