// blade_dll_panic.hpp -- ONE failure exit across a DLL boundary.
//
// Some of what Blade builds runs in a shared library of its own: nvcc -shared
// turns the cuBLAS shim (blade_linalg_cuda.hpp, Build.buildCublasDevice) and
// the mpi+cuda hybrid's kernels (Build.compileCudaMpiHybrid) into DLLs that
// the g++ host links by their export tables. A DLL is its own image, so it
// must NOT include blade_runtime.hpp to fail through the runtime's panic: that
// header's `inline` state -- the failure-exit hooks the run record and the
// memcheck report register, the once-flag, the exit code the record reads --
// would be a SECOND copy inside the DLL, holding none of the hooks the
// executable registered. A failure raised there would leave no run record
// (BLADE_RUN_RECORD / --run-record) and no memcheck line, and would not be
// the "exactly one failure reports" exit the runtime promises.
//
// So a DLL never names panic. It holds ONE function pointer, which the HOST
// binds during its static initialization to blade_rt::dll_panic
// (blade_runtime.hpp) through the binder the DLL exports -- a distinct name
// per DLL kind (blade_cuda_bind_panic, blade_kernels_bind_panic), so a
// program linking both resolves each unambiguously. Every DLL-side failure
// calls through it into the executable's own panic: its hooks, its exit
// code, its single _Exit. Codegen writes the binding (CodeGen.dllPanicBindLines).
//
// Unbound -- a standalone device program such as cublas_swap_tests.cu, which
// has no Blade host -- a failure is still loud and terminal: the same
// `error[BLxxxx]:` line on stderr, then abort.
//
// DLL-side only: include it from the DLL's single translation unit (the
// slot is `static`, one per image by construction). Host code never
// includes it; the host half is one prototype and one binding line.
#pragma once

#include <cstdio>
#include <cstdlib>

#if defined(_WIN32)
#define BLADE_DLL_EXPORT extern "C" __declspec(dllexport)
#else
#define BLADE_DLL_EXPORT extern "C" __attribute__((visibility("default")))
#endif

namespace blade_dll {
    typedef void (*panic_fn)(const char* code, const char* msg);

    // The host's panic, or null while unbound. One per DLL image.
    static panic_fn host_panic = nullptr;

    static inline void bind(panic_fn f) { host_panic = f; }

    // A DLL-side failure: the host's panic when bound (which does not
    // return -- it leaves through the host runtime's _Exit), else stderr +
    // abort. `msg` must outlive the call; the host's failure-exit hooks read
    // it before the process ends.
    [[noreturn]] static inline void fail(const char* code, const char* msg) {
        if (host_panic) host_panic(code, msg);
        std::fprintf(stderr, "error[%s]: %s\n", code, msg);
        std::fflush(stderr);
        std::abort();
    }

#ifdef __CUDACC__
    // A CUDA runtime call that did not succeed: BL8005, naming the call and
    // the runtime's own error string. One static buffer: the failure exit is
    // taken once, by the first failing thread (the host panic's once-flag).
    static inline void ck(cudaError_t e, const char* api) {
        if (e == cudaSuccess) return;
        static char buf[512];
        std::snprintf(buf, sizeof buf, "CUDA runtime call %s failed (status %d: %s)",
                      api, (int)e, cudaGetErrorString(e));
        fail("BL8005", buf);
    }
#endif
}
