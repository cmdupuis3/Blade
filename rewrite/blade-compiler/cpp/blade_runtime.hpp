// Blade runtime error support: shadow call stack + panic. Host-only;
// device compilation sees no-op stubs.
//
// The shadow call stack is a thread_local array of Frames pushed/popped by an
// RAII Scope at each Blade function-body entry (BLADE_FRAME). On a runtime
// failure, blade_rt::panic prints an `error[BLxxxx]:` line, the failing source
// location (when a span is carried), and the Blade call stack (innermost
// first), then exits(1). thread_local storage keeps it correct under OpenMP.
//
// __CUDA_ARCH__ is defined ONLY during nvcc's device passes; host passes (incl.
// nvcc's host pass over a .cu) get the real implementation, device passes get a
// no-op BLADE_FRAME macro and no blade_rt symbols (panic is never called from
// device code — the guard/abort sites it replaces were host-only).
#pragma once
#include <iostream>
#include <cstdlib>
#if !defined(__CUDA_ARCH__)
namespace blade_rt {
  struct Frame { const char* fn; const char* file; int line; };
  inline thread_local Frame stack[64];
  inline thread_local int   depth = 0;
  struct Scope {
    Scope(const char* fn, const char* file, int line) {
      if (depth < 64) stack[depth] = {fn, file, line};
      ++depth;
    }
    ~Scope() { --depth; }
  };
  [[noreturn]] inline void panic(const char* code, const char* msg,
                                 const char* file, int line) {
    std::cerr << "error[" << code << "]: " << msg << "\n";
    if (file && line > 0) std::cerr << "  --> " << file << ":" << line << "\n";
    int d = depth < 64 ? depth : 64;
    for (int i = d - 1; i >= 0; --i) {
      std::cerr << "  at " << stack[i].fn;
      if (stack[i].file && stack[i].line > 0)
        std::cerr << " (" << stack[i].file << ":" << stack[i].line << ")";
      std::cerr << "\n";
    }
    std::exit(1);
  }
}
#define BLADE_FRAME(fn, file, line) blade_rt::Scope __blade_frame_(fn, file, line)
#else
#define BLADE_FRAME(fn, file, line)
#endif
