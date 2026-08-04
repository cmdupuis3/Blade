// blade_portability.hpp
//
// Compiler-portable spellings for the optimization annotations codegen emits
// into generated programs. This header is the ONLY place either spelling is
// chosen; CodeGen.fs emits `BLADE_RESTRICT` / `BLADE_IVDEP` and never the bare
// GCC keyword or pragma.
//
// THE PATTERN (see docs/plan-cpp-perf-exploitation.md, "Round D completion"):
// all compiler-specific syntax in EMITTED code goes through a macro defined
// here. Codegen emits only macro spellings; a new toolchain is a new `#elif`
// branch in this file and zero codegen changes.
//
// WHY THIS EXISTS: Phases 1 and 3 were written against g++ and emitted
// `__restrict__` on peeled row/pool pointers plus `#pragma GCC ivdep` on the
// innermost header. `__restrict__` is not an MSVC keyword, so cl.exe parses
//     double* __restrict__ __fp_R = pool_base(R.data);
// as a declaration of a POINTER VARIABLE NAMED `__restrict__`, then chokes on
// the identifier that follows:
//     error C2373: '__restrict__': redefinition; different type modifiers
//     error C2146: syntax error: missing ';' before identifier '__fp_R'
// and `#pragma GCC ivdep` is `warning C4068: unknown pragma`. Every generated
// program was therefore uncompilable by MSVC, which is what the Windows CUDA
// path uses: Build.compileCudaSplit drives cl.exe through nvcc for BOTH halves
// (one ABI across the two objects), so the host `.cpp` -- the oracle half of
// every cuda differential -- never built and `blade test cuda`'s kernel block
// failed wholesale on Windows. g++ paths were unaffected, which is why this
// survived: nothing on Linux, and no CPU test on Windows (they use g++ too),
// ever compiled a generated `.cpp` with cl.
#pragma once

// ---------------------------------------------------------------------------
// BLADE_RESTRICT -- restrict qualifier on a pointer declaration.
//
//  * nvcc's DEVICE pass accepts `__restrict__` regardless of host compiler, and
//    on Windows it also has _MSC_VER defined -- so __CUDA_ARCH__ is tested
//    FIRST, otherwise device code would get MSVC's spelling. (No emitted `.cu`
//    includes this header today; the ordering is the rule, not a live fix.)
//  * MSVC spells it `__restrict` and defines neither __GNUC__ nor __clang__
//    (clang-cl defines both, and takes the GCC spelling below).
//  * An unknown compiler gets nothing. restrict is a pure optimization hint;
//    dropping it costs speed, never correctness.
//
// `#ifndef`-guarded so an explicit `-DBLADE_RESTRICT=...` on a build line wins
// silently rather than colliding; `#pragma once` already makes the header
// itself idempotent.
#ifndef BLADE_RESTRICT
  #if defined(__CUDA_ARCH__)
    #define BLADE_RESTRICT __restrict__
  #elif defined(_MSC_VER) && !defined(__clang__)
    #define BLADE_RESTRICT __restrict
  #elif defined(__GNUC__) || defined(__clang__)
    #define BLADE_RESTRICT __restrict__
  #else
    #define BLADE_RESTRICT
  #endif
#endif

// ---------------------------------------------------------------------------
// BLADE_IVDEP -- "this loop has no loop-carried dependence", emitted on the
// line immediately before a `for` header and nowhere else (an OpenMP construct
// rejects any intervening line between itself and its loop, so at most one of
// the two ever lands on a given header).
//
// It MUST carry `_Pragma` rather than be `#ifdef`'d at the emission site: a
// `#pragma` line cannot be produced by a macro, and `_Pragma` is the only form
// that can. Under GCC it destringizes to exactly the `#pragma GCC ivdep` that
// was emitted before, so preprocessed g++ output is token-identical.
//
// GCC-only on purpose. clang does not implement `GCC ivdep` (its spelling is
// `#pragma clang loop vectorize(assume_safety)`) and MSVC does not either
// (C4068); both already ignored the raw emission, so an empty expansion
// preserves exactly the behaviour they had, minus the warning. Wiring up the
// clang spelling would be a behaviour change, not a port, so it is left to
// whoever measures it -- which is why the two guards differ.
//
// The proof obligation that licenses the assertion is discharged at the
// emission site (CodeGen.fs `ivdepEligible` and the flat-elementwise fast
// path), not here -- this header only picks the spelling.
#ifndef BLADE_IVDEP
  #if defined(__GNUC__) && !defined(__clang__)
    #define BLADE_IVDEP _Pragma("GCC ivdep")
  #else
    #define BLADE_IVDEP
  #endif
#endif
