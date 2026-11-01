/* Linux stand-in for the two Win32 timer calls bench_support.f90 binds (QueryPerformanceCounter / Frequency), over CLOCK_MONOTONIC in ns. Linked by build_gfortran.sh / build_ifx.sh. */
#include <time.h>
int QueryPerformanceCounter(long long *cnt) {
    struct timespec ts; clock_gettime(CLOCK_MONOTONIC, &ts);
    *cnt = (long long)ts.tv_sec * 1000000000LL + ts.tv_nsec; return 1;
}
int QueryPerformanceFrequency(long long *freq) { *freq = 1000000000LL; return 1; }
