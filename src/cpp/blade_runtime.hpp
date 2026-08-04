// Blade runtime error support: shadow call stack + panic. Host-only;
// device compilation sees no-op stubs.
//
// The shadow call stack is a thread_local array of Frames (correct under
// OpenMP) pushed/popped by an RAII Scope at each Blade function-body entry
// (BLADE_FRAME). On failure, blade_rt::panic prints an `error[BLxxxx]:`
// line, the failing source location (when carried), and the Blade call
// stack (innermost first), then exits(1).
//
// __CUDA_ARCH__ is defined ONLY during nvcc's device passes: host passes get
// the real implementation, device passes get a no-op BLADE_FRAME macro and
// no blade_rt symbols.
#pragma once
#include <iostream>
#include <cstdlib>
#if !defined(__CUDA_ARCH__)
namespace blade_rt {
  struct Frame { const char* fn; const char* file; int line; };
  // 65 slots, not 64: slots 0..63 are the trace, slot 64 is a write-only
  // scratch sink for overflow frames (see Scope below). panic never reads it.
  inline thread_local Frame stack[65];
  inline thread_local int   depth = 0;
  struct Scope {
    Scope(const char* fn, const char* file, int line) {
      // BRANCHLESS on purpose: the obvious `if (depth < 64) stack[depth] = ...`
      // costs 13-27 ns PER ELEMENT in a loop over an inlined kernel. NOT the
      // emulated-TLS lookup (GCC hoists it out of the loop) -- it is the
      // branch on a value reloaded from TLS-opaque memory each iteration,
      // which the compiler cannot prove doesn't alias the user pool, forcing
      // a loop-carried load->branch->store->load chain. Clamping to a
      // scratch slot compiles to a `cmov` instead: ~0.02 ns/element.
      //
      // Observably IDENTICAL to the guarded form: slots 0..63 get the same
      // frames, overflow frames land in slot 64 (never read by panic). The
      // interpreter twin (src/Interp/Core.fs InterpState.FrameNames) pins
      // that window, staying correct.
      stack[depth < 64 ? depth : 64] = {fn, file, line};
      ++depth;
    }
    ~Scope() { --depth; }
  };
  // INVARIANT, load-bearing: the ONLY reader of the shadow stack, called
  // ONLY from generated code. resolveShadowFrames (CodeGen) omits the frame
  // for kernel bodies that reach no panic, from generated text alone, so a
  // panic call added to another runtime header would silently cost those
  // kernels their trace frame -- add one only with a matching
  // resolveShadowFrames rule (tripwire in tests/Test_Diagnostics.fs).
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
