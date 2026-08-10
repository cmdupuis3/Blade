// Blade memcheck runtime: exports allocator statistics from an
// AddressSanitizer build so a harness can audit a generated program's
// alloc/free discipline. Deployed alongside the other runtime headers, but
// INCLUDED only by programs generated under BLADE_MEMCHECK=1 (Build.fs then
// compiles them with ASan); a default build never includes this file.
//
// Report protocol: exactly one line on stderr at process exit --
//   BLADE-MEMCHECK: baseline_bytes=<B> final_bytes=<F> outstanding_bytes=<D>
//   outstanding_blocks=<N> allocs=<A> frees=<R> asan=<0|1>
// where D = F - B. `outstanding_bytes` counts bytes allocated after
// instrumentation start and never freed: module-level bindings (deliberately
// leaked -- see the "Deterministic deallocation" block in CodeGen.fs) plus
// any escape from the scope-exit free discipline. A per-call or per-iteration
// leak shows up as outstanding_bytes scaling with the trip count, far above
// the module-binding footprint; the census harness separates the two by size.
//
// Accounting uses __sanitizer_install_malloc_and_free_hooks with our own
// atomic counters rather than __sanitizer_get_current_allocated_bytes,
// because the MSVC ASan runtime's builtin byte counter does not see
// operator new[] allocations -- which is exactly what allocate<> uses for
// every Blade array pool (measured 2026-08-09: `new double[100000]` moved
// the hook accounting by 800000 bytes and the builtin counter by 0).
//
// Known skews, all bounded and baseline-cancelled where possible:
//  - a block allocated BEFORE the hooks install but freed before exit is
//    subtracted without ever having been added (reads a few bytes low);
//  - CRT-internal lazy allocations are suppressed by forcing unbuffered
//    stdio before the baseline sample (which also makes output survive an
//    ASan hard abort);
//  - the OpenMP thread pool allocates on the first parallel region and lives
//    until exit, so the constructor spins it up before sampling.
#pragma once
#include <cstdio>
#include <cstddef>
#include <atomic>

// ASan detection across the three compilers that can build generated code:
// MSVC and GCC define __SANITIZE_ADDRESS__, clang answers __has_feature.
#if defined(__SANITIZE_ADDRESS__)
#  define BLADE_MEMCHECK_HAS_ASAN 1
#elif defined(__has_feature)
#  if __has_feature(address_sanitizer)
#    define BLADE_MEMCHECK_HAS_ASAN 1
#  endif
#endif
#ifndef BLADE_MEMCHECK_HAS_ASAN
#  define BLADE_MEMCHECK_HAS_ASAN 0
#endif

#if BLADE_MEMCHECK_HAS_ASAN
// Sanitizer allocator interface -- exported by every ASan runtime (MSVC
// clang_rt.asan_dynamic, GCC libasan, clang compiler-rt). Declared directly
// instead of via <sanitizer/allocator_interface.h>, which MinGW toolchains
// do not ship.
extern "C" size_t __sanitizer_get_allocated_size(const volatile void* p);
extern "C" int __sanitizer_install_malloc_and_free_hooks(
    void (*malloc_hook)(const volatile void*, size_t),
    void (*free_hook)(const volatile void*));
#endif

#if defined(_OPENMP)
#include <omp.h>
#endif

namespace blade_memcheck {

// Zero-initialized statics: constant-initialized, so the hooks can fire
// safely the instant they are installed regardless of static-init order.
static std::atomic<long long> g_live_bytes;
static std::atomic<long long> g_live_blocks;
static std::atomic<long long> g_allocs;
static std::atomic<long long> g_frees;

#if BLADE_MEMCHECK_HAS_ASAN
inline void blade_mc_malloc_hook(const volatile void*, size_t size) {
    g_live_bytes.fetch_add((long long)size);
    g_live_blocks.fetch_add(1);
    g_allocs.fetch_add(1);
}
inline void blade_mc_free_hook(const volatile void* p) {
    // Hooks run before the runtime's own bookkeeping, so the block is still
    // live here and its exact requested size is queryable.
    g_live_bytes.fetch_sub((long long)__sanitizer_get_allocated_size(p));
    g_live_blocks.fetch_sub(1);
    g_frees.fetch_add(1);
}
#endif

struct Report {
    long long baseline_bytes;
    long long baseline_blocks;
    Report() {
        // Unbuffered stdio BEFORE anything else: stream buffers otherwise
        // allocate lazily on the first print (inside the measurement window)
        // and never free.
        setvbuf(stdout, nullptr, _IONBF, 0);
        setvbuf(stderr, nullptr, _IONBF, 0);
#if BLADE_MEMCHECK_HAS_ASAN
        __sanitizer_install_malloc_and_free_hooks(blade_mc_malloc_hook,
                                                  blade_mc_free_hook);
#endif
#if defined(_OPENMP)
        #pragma omp parallel
        { volatile int blade_mc_warmup = 0; (void)blade_mc_warmup; }
#endif
        baseline_bytes  = g_live_bytes.load();
        baseline_blocks = g_live_blocks.load();
    }
    ~Report() {
        // Static destructors run in reverse construction order; this object
        // is constructed first in the TU (the header is included before any
        // generated code), so this samples LAST -- after main returned and
        // after every later static's destructor released its storage.
        long long fin_bytes  = g_live_bytes.load();
        long long fin_blocks = g_live_blocks.load();
        fprintf(stderr,
                "BLADE-MEMCHECK: baseline_bytes=%lld final_bytes=%lld "
                "outstanding_bytes=%lld outstanding_blocks=%lld "
                "allocs=%lld frees=%lld asan=%d\n",
                baseline_bytes, fin_bytes, fin_bytes - baseline_bytes,
                fin_blocks - baseline_blocks,
                g_allocs.load(), g_frees.load(), (int)BLADE_MEMCHECK_HAS_ASAN);
    }
};

// One generated program is one TU, so internal linkage suffices; the object
// exists only for its constructor/destructor bracketing of main().
static Report blade_mc_report;

} // namespace blade_memcheck
