// Blade runtime error support: shadow call stack + panic, plus the scalar
// math the interpreter cannot borrow from libm (lgamma, digamma). Host-only;
// device compilation sees no-op stubs.
//
// The shadow call stack is a thread_local array of Frames (correct under
// OpenMP) pushed/popped by an RAII Scope at each Blade function-body entry
// (BLADE_FRAME). On failure, blade_rt::panic prints an `error[BLxxxx]:`
// line, the failing source location (when carried), and the Blade call
// stack (innermost first), then ends the process with status 1 -- once, even
// when several OpenMP workers fail together (see panic).
//
// __CUDA_ARCH__ is defined ONLY during nvcc's device passes: host passes get
// the real implementation, device passes get a no-op BLADE_FRAME macro and
// no blade_rt symbols.
#pragma once
#include <iostream>
#include <cstdlib>
#include <cmath>
#include <cstdint>
#include <cstdio>
#include <atomic>
#include <type_traits>
#include <locale>
#if !defined(__CUDA_ARCH__)
// ---- libm, evaluated at RUN TIME (docs/formalism.md section 2.4) ----------
//
// The contract: a transcendental intrinsic's value is the PLATFORM libm's,
// computed when the program runs -- the value the interpreter gets by
// P/Invoking the same library (src/Interp/Numerics.fs, mathBackend). g++
// broke that on constant arguments: `std::sin(7.35615)` is the builtin
// `sin`, which g++ folds at compile time through MPFR (correctly rounded),
// and ucrt's sin is 1 ulp off there, so `(sin(7.35615) - 0.87862006801244397)
// * 1e17` printed 0 compiled and 11.1 interpreted -- and a literal disagreed
// with the same value routed through an array inside ONE executable.
//
// The mechanism: each function is re-declared under a Blade-owned C name
// whose ASSEMBLER name is the libm symbol. g++ recognizes builtins by the
// declared identifier, so `blade_libm_sin` is never folded, sincos-combined
// or strength-reduced -- but it is declared `const`, so the optimizer still
// hoists a loop-invariant call and shares a repeated one, which
// -fno-builtin-sin (the flag alternative) would have lost along with the
// fold. `const` asserts no errno write: the same promise -fno-math-errno
// already makes for every build. __USER_LABEL_PREFIX__ keeps the symbol
// right where C symbols carry a leading underscore.
//
// Overloads fix the OPERAND domain, which libstdc++ leaves to builtins: its
// float and integral std::sin overloads are inline wrappers over
// __builtin_sinf / __builtin_sin, which no flag reaches. Here a Float32
// operand is evaluated by the double function and rounded ONCE to float (the
// interpreter rounds the same way), and an integral one widens to double.
// sqrt/floor/ceil/fabs/fma are NOT routed here: they are correctly rounded,
// so a compile-time fold IS the run-time value, and sqrt must stay the
// builtin the vectorizer knows.
//
// MSVC (the nvcc host compiler, the ASan fallback) has no asm labels; there
// the names forward to std::, and folding is whatever cl.exe does (the
// documented exception).
//
// COMPLEX intrinsics are inside the contract too (below the real ones): the
// <complex> functions forward to __builtin_cexp & co., which g++ folds through
// MPC on a constant operand exactly as it folds the real builtins through
// MPFR -- measured: a constant atan(0.5i) came out 1 ulp off the library's
// run-time value. The same asm-label barrier binds the C99 complex functions
// (cexp, clog, csqrt, csin, ..., catan, cpow) under Blade-owned names.
#include <complex>
#if defined(__GNUC__)
#define BLADE_LIBM_STR2(x) #x
#define BLADE_LIBM_STR(x) BLADE_LIBM_STR2(x)
#define BLADE_LIBM_SYM(n) BLADE_LIBM_STR(__USER_LABEL_PREFIX__) #n
#define BLADE_LIBM_DECL1(n) \
  extern "C" double blade_libm_##n(double) __asm__(BLADE_LIBM_SYM(n)) __attribute__((const, nothrow));
#define BLADE_LIBM_DECL2(n) \
  extern "C" double blade_libm_##n(double, double) __asm__(BLADE_LIBM_SYM(n)) __attribute__((const, nothrow));
#else
#define BLADE_LIBM_DECL1(n) inline double blade_libm_##n(double x) { return std::n(x); }
#define BLADE_LIBM_DECL2(n) inline double blade_libm_##n(double a, double b) { return std::n(a, b); }
#endif
BLADE_LIBM_DECL1(exp) BLADE_LIBM_DECL1(log) BLADE_LIBM_DECL1(log10)
BLADE_LIBM_DECL1(sin) BLADE_LIBM_DECL1(cos) BLADE_LIBM_DECL1(tan)
BLADE_LIBM_DECL1(sinh) BLADE_LIBM_DECL1(cosh) BLADE_LIBM_DECL1(tanh)
BLADE_LIBM_DECL1(asin) BLADE_LIBM_DECL1(acos) BLADE_LIBM_DECL1(atan)
BLADE_LIBM_DECL2(atan2) BLADE_LIBM_DECL2(pow)
// The emitted spelling: blade_libm::sin(x). double -> double; float ->
// float, rounded once; any other arithmetic operand (the integral ones)
// widens to double. A non-template exact match beats the template, so the
// template only ever catches what the two plain overloads do not.
namespace blade_libm {
#define BLADE_LIBM_WRAP1(n)                                                     \
  inline double n(double x) { return blade_libm_##n(x); }                       \
  inline float n(float x) { return static_cast<float>(blade_libm_##n(x)); }     \
  template <typename T> inline double n(T x) { return blade_libm_##n(static_cast<double>(x)); }
#define BLADE_LIBM_WRAP2(n)                                                               \
  inline double n(double a, double b) { return blade_libm_##n(a, b); }                    \
  inline float n(float a, float b) { return static_cast<float>(blade_libm_##n(a, b)); }  \
  template <typename A, typename B> inline double n(A a, B b) {                           \
    return blade_libm_##n(static_cast<double>(a), static_cast<double>(b)); }
  BLADE_LIBM_WRAP1(exp) BLADE_LIBM_WRAP1(log) BLADE_LIBM_WRAP1(log10)
  BLADE_LIBM_WRAP1(sin) BLADE_LIBM_WRAP1(cos) BLADE_LIBM_WRAP1(tan)
  BLADE_LIBM_WRAP1(sinh) BLADE_LIBM_WRAP1(cosh) BLADE_LIBM_WRAP1(tanh)
  BLADE_LIBM_WRAP1(asin) BLADE_LIBM_WRAP1(acos) BLADE_LIBM_WRAP1(atan)
  BLADE_LIBM_WRAP2(atan2)
#undef BLADE_LIBM_WRAP1
#undef BLADE_LIBM_WRAP2
}

// ---- complex intrinsics, evaluated at RUN TIME (same contract) -------------
//
// complex<double>: the C99 function itself (cexp, clog, csqrt, csin, ccos,
// ctan, csinh, ccosh, ctanh, casin, cacos, catan, cpow), bound by asm label
// so g++ never sees a builtin -- the value std::exp(complex) & co. compute at
// run time (libstdc++ forwards them to exactly these), minus the fold.
// Marshalled through __real__/__imag__, not libstdc++'s __rep(), so libc++
// (the clang64 memcheck profile) takes the same path.
//
// complex<float>: evaluated in double and each component rounded ONCE to
// float, the rule the real Float32 overloads follow (and the interpreter's
// complex arithmetic, which is double throughout).
//
// pow: the three libstdc++ overloads Blade's `^` reaches, re-spelled over the
// barrier functions with the libstdc++ algorithm unchanged --
//   pow(complex, complex) = cpow
//   pow(complex, real)    = x real & > 0 ? pow(re, y)
//                           : polar(exp(y * log(x).re), y * log(x).im)
//   pow(real, complex)    = x > 0 ? polar(pow(x, y.re), y.im * log(x))
//                           : cpow(complex(x), y)
// with polar(r, t) = (r * cos(t), r * sin(t)). Codegen casts an integer
// operand to the component type first, which is where libstdc++'s
// __promote_2 overload sends it too.
#if defined(__GNUC__)
#define BLADE_CLIBM_DECL1(n) \
  extern "C" __complex__ double blade_libm_c##n(__complex__ double) __asm__(BLADE_LIBM_SYM(c##n)) __attribute__((const, nothrow));
BLADE_CLIBM_DECL1(exp) BLADE_CLIBM_DECL1(log) BLADE_CLIBM_DECL1(sqrt)
BLADE_CLIBM_DECL1(sin) BLADE_CLIBM_DECL1(cos) BLADE_CLIBM_DECL1(tan)
BLADE_CLIBM_DECL1(sinh) BLADE_CLIBM_DECL1(cosh) BLADE_CLIBM_DECL1(tanh)
BLADE_CLIBM_DECL1(asin) BLADE_CLIBM_DECL1(acos) BLADE_CLIBM_DECL1(atan)
extern "C" __complex__ double blade_libm_cpow(__complex__ double, __complex__ double) __asm__(BLADE_LIBM_SYM(cpow)) __attribute__((const, nothrow));
#undef BLADE_CLIBM_DECL1
namespace blade_libm {
  inline __complex__ double c99_(std::complex<double> z) {
    __complex__ double c; __real__ c = z.real(); __imag__ c = z.imag(); return c; }
  inline std::complex<double> cxx_(__complex__ double c) {
    return std::complex<double>(__real__ c, __imag__ c); }
#define BLADE_CLIBM_WRAP1(n) \
  inline std::complex<double> n(std::complex<double> z) { return cxx_(blade_libm_c##n(c99_(z))); }
  BLADE_CLIBM_WRAP1(exp) BLADE_CLIBM_WRAP1(log) BLADE_CLIBM_WRAP1(sqrt)
  BLADE_CLIBM_WRAP1(sin) BLADE_CLIBM_WRAP1(cos) BLADE_CLIBM_WRAP1(tan)
  BLADE_CLIBM_WRAP1(sinh) BLADE_CLIBM_WRAP1(cosh) BLADE_CLIBM_WRAP1(tanh)
  BLADE_CLIBM_WRAP1(asin) BLADE_CLIBM_WRAP1(acos) BLADE_CLIBM_WRAP1(atan)
#undef BLADE_CLIBM_WRAP1
  inline std::complex<double> pow(std::complex<double> x, std::complex<double> y) {
    return cxx_(blade_libm_cpow(c99_(x), c99_(y))); }
}
#else
namespace blade_libm {
#define BLADE_CLIBM_WRAP1(n) \
  inline std::complex<double> n(std::complex<double> z) { return std::n(z); }
  BLADE_CLIBM_WRAP1(exp) BLADE_CLIBM_WRAP1(log) BLADE_CLIBM_WRAP1(sqrt)
  BLADE_CLIBM_WRAP1(sin) BLADE_CLIBM_WRAP1(cos) BLADE_CLIBM_WRAP1(tan)
  BLADE_CLIBM_WRAP1(sinh) BLADE_CLIBM_WRAP1(cosh) BLADE_CLIBM_WRAP1(tanh)
  BLADE_CLIBM_WRAP1(asin) BLADE_CLIBM_WRAP1(acos) BLADE_CLIBM_WRAP1(atan)
#undef BLADE_CLIBM_WRAP1
  inline std::complex<double> pow(std::complex<double> x, std::complex<double> y) { return std::pow(x, y); }
}
#endif
namespace blade_libm {
  inline std::complex<double> polar_(double r, double t) {
    return std::complex<double>(r * blade_libm_cos(t), r * blade_libm_sin(t)); }
  inline std::complex<double> pow(std::complex<double> x, double y) {
    if (x.imag() == 0.0 && x.real() > 0.0) return std::complex<double>(blade_libm_pow(x.real(), y));
    std::complex<double> t = blade_libm::log(x);
    return polar_(blade_libm_exp(y * t.real()), y * t.imag()); }
  inline std::complex<double> pow(double x, std::complex<double> y) {
    return x > 0.0 ? polar_(blade_libm_pow(x, y.real()), y.imag() * blade_libm_log(x))
                   : blade_libm::pow(std::complex<double>(x), y); }
  // complex<float>: double evaluation, each component rounded once.
  inline std::complex<float> narrow_(std::complex<double> r) {
    return std::complex<float>(static_cast<float>(r.real()), static_cast<float>(r.imag())); }
#define BLADE_CLIBM_WRAPF(n) \
  inline std::complex<float> n(std::complex<float> z) { return narrow_(n(std::complex<double>(z))); }
  BLADE_CLIBM_WRAPF(exp) BLADE_CLIBM_WRAPF(log) BLADE_CLIBM_WRAPF(sqrt)
  BLADE_CLIBM_WRAPF(sin) BLADE_CLIBM_WRAPF(cos) BLADE_CLIBM_WRAPF(tan)
  BLADE_CLIBM_WRAPF(sinh) BLADE_CLIBM_WRAPF(cosh) BLADE_CLIBM_WRAPF(tanh)
  BLADE_CLIBM_WRAPF(asin) BLADE_CLIBM_WRAPF(acos) BLADE_CLIBM_WRAPF(atan)
#undef BLADE_CLIBM_WRAPF
  inline std::complex<float> pow(std::complex<float> x, std::complex<float> y) {
    return narrow_(pow(std::complex<double>(x), std::complex<double>(y))); }
  inline std::complex<float> pow(std::complex<float> x, float y) {
    return narrow_(pow(std::complex<double>(x), static_cast<double>(y))); }
  inline std::complex<float> pow(float x, std::complex<float> y) {
    return narrow_(pow(static_cast<double>(x), std::complex<double>(y))); }
}

// The PANIC-FREE half of the arithmetic contract (docs/formalism.md section
// 2.4). Nothing in this namespace may reach blade_rt::panic: codegen lists
// `blade_arith::` in panicFreeNamespaces, so a kernel that calls only these
// keeps its shadow frame elided. The faulting forms live in blade_rt below.
namespace blade_arith {
  // b^e for e >= 0, exact modulo 2^w: square-and-multiply in the UNSIGNED
  // type, whose wraparound is defined, then back to T -- the two's-complement
  // residue every lane computes (any multiplication order gives the same
  // residue, so the interpreter's loop need not match this one step for
  // step). 0^0 = 1. A literal exponent constant-propagates: ipow_nn64(b, 2)
  // inlines to b * b.
  template <typename T> inline T ipow_nn(T b, T e) {
    using U = std::make_unsigned_t<T>;
    U r = 1, x = static_cast<U>(b);
    U n = static_cast<U>(e);
    while (n != 0) {
      if (n & 1u) r = static_cast<U>(r * x);
      x = static_cast<U>(x * x);
      n >>= 1;
    }
    return static_cast<T>(r);
  }
  // Real `^`: the platform libm's pow, except that an exponent of exactly 2
  // is x * x -- one correctly rounded multiply, and what a literal `x ^ 2`
  // constant-propagates to, so the idiom costs no call (g++ used to get the
  // same product by folding pow(x, 2.0) as a builtin; blade_libm_pow, which
  // keeps every OTHER pow call honest, is not a builtin). The interpreter's
  // Numerics.realPow runs the same test on the value, so a non-literal 2
  // agrees too.
  inline double fpow(double x, double e) { return e == 2.0 ? x * x : blade_libm_pow(x, e); }
  // Rounded ONCE to float: the Float32 `^` (a double pow stored straight into a
  // float was an implicit narrowing, -Werror=float-conversion).
  inline float fpowf(double x, double e) { return static_cast<float>(fpow(x, e)); }
  // NON-template entry points are what codegen emits: the shadow-frame scan
  // (CodeGen.scanBodyCalls) reads `f<T>(` as a call through a value, which
  // would cost the kernel its frame elision.
  inline int64_t ipow_nn64(int64_t b, int64_t e) { return ipow_nn<int64_t>(b, e); }
  inline int32_t ipow_nn32(int32_t b, int32_t e) { return ipow_nn<int32_t>(b, e); }
}
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
  // The failure that ended the run, for the run record (blade_run_record.hpp
  // reads these at exit): empty code = the program exited normally.
  inline const char* exit_code = "";
  inline const char* exit_message = "";
  // panic leaves through std::_Exit: no static destructors and no atexit
  // handlers -- tearing down iostreams, the OpenMP runtime or a provider
  // library under worker threads that are still running is what made the old
  // std::exit unsafe. What must still happen on the way out registers here
  // (during static initialization, which is single-threaded) and runs in
  // registration order: the run record's writer (blade_run_record.hpp) and a
  // netcdf program's library finalize (CodeGen.netcdfRegisterLines). A
  // private registry rather than at_quick_exit, which not every C++ runtime
  // Blade targets provides.
  inline void (*failure_exit_hooks[8])() = {};
  inline void on_failure_exit(void (*f)()) {
    for (auto& h : failure_exit_hooks) if (!h) { h = f; return; }
  }
  //
  // `panicking` is set by the FIRST panic. OpenMP workers routinely fail
  // together (every iteration of a parallel loop dividing by the same zero),
  // and exiting from several threads at once is undefined -- the old
  // std::exit ran the static destructors twice, concurrently. So exactly one
  // failure reports and exits; any other parks here until the process ends
  // under it (the atomic load is the forward-progress side effect an empty
  // spin would lack).
  inline std::atomic<int> panicking{0};
  [[noreturn]] inline void panic(const char* code, const char* msg,
                                 const char* file, int line) {
    if (panicking.exchange(1) != 0) { for (;;) (void)panicking.load(); }
    exit_code = code;
    exit_message = msg;
    std::cerr << "error[" << code << "]: " << msg << "\n";
    if (file && line > 0) std::cerr << "  --> " << file << ":" << line << "\n";
    int d = depth < 64 ? depth : 64;
    for (int i = d - 1; i >= 0; --i) {
      std::cerr << "  at " << stack[i].fn;
      if (stack[i].file && stack[i].line > 0)
        std::cerr << " (" << stack[i].file << ":" << stack[i].line << ")";
      std::cerr << "\n";
    }
    // Everything printed before the failure is kept (std::exit flushed it
    // through the static destructors _Exit skips), then the hooks.
    std::cerr.flush();
    std::cout.flush();
    std::fflush(nullptr);
    for (auto h : failure_exit_hooks) if (h) h();
    std::fflush(nullptr);
    std::_Exit(1);
  }
  // The entry point a Blade-built DLL fails through (blade_dll_panic.hpp).
  // The DLL is its own image and must not carry a second copy of this
  // header's state, so the host binds this function into it at static
  // initialization (codegen's dllPanicBindLines) and a DLL-side failure runs
  // THIS executable's panic: its hooks, its run record, its single exit. No
  // trace frame is lost: a body that calls into a DLL names an extern "C"
  // symbol the shadow-frame analysis cannot follow, so it keeps its frame.
  [[noreturn]] inline void dll_panic(const char* code, const char* msg) {
    panic(code, msg, nullptr, 0);
  }

  // ---- Typed float spelling: a floating value prints with a decimal point.
  // `cout << 2.0` under setprecision(15) writes "2", the spelling of the
  // Int64 2; Blade, like F#, keeps the decimal as the float/int
  // differentiator in output as in source, so a defaultfloat rendering that
  // is a bare digit run gains ".0" (`2.0`, `-3.0`, `0.0`, `-0.0`). Exponent
  // forms (`1e+20`) and `nan`/`inf` cannot be an integer's spelling and pass
  // through unchanged. Installed on cout by every generated main
  // (typed_float_print) as a num_put facet, so EVERY double reaching cout
  // gets it -- scalar bindings, array cells, struct fields, and each
  // std::complex component (operator<< for complex formats through a stream
  // imbued with cout's locale); a float promotes to double before reaching
  // the facet. Other streams (display-frame JSON, provider writers) are not
  // imbued and keep their own number rules. The interpreter's twin is
  // src/Interp/CppFormat.fs markFloat; the LLVM lane's is blade_fmt_f64.
  struct float_put : std::num_put<char> {
    using base = std::num_put<char>;
    using iter_type = base::iter_type;
    using base::do_put;  // keep the integral/bool/pointer overloads visible
  protected:
    // A fixed sink for one rendering; overflow (never reached at the
    // precisions marked here) leaves the iterator failed and the text cut.
    struct sink : std::streambuf {
      char b[128];
      sink() { setp(b, b + sizeof b); }
      std::size_t size() const { return static_cast<std::size_t>(pptr() - pbase()); }
    };
    template <class F>
    iter_type put_marked(iter_type out, std::ios_base& io, char fill, F v) const {
      // Only the defaultfloat layout drops the point; fixed/scientific/
      // hexfloat and showpoint keep theirs, and a huge precision is left
      // to the base facet rather than the fixed sink.
      if ((io.flags() & (std::ios_base::floatfield | std::ios_base::showpoint)) != 0
          || io.precision() > 64)
        return base::do_put(out, io, fill, v);
      const std::streamsize w = io.width(0);
      sink s;
      base::do_put(iter_type(&s), io, fill, v);
      std::size_t n = s.size();
      std::size_t i = (n > 0 && (s.b[0] == '-' || s.b[0] == '+')) ? 1 : 0;
      bool bare = n > i;
      for (std::size_t k = i; k < n; ++k)
        if (s.b[k] < '0' || s.b[k] > '9') { bare = false; break; }
      if (bare) { s.b[n++] = '.'; s.b[n++] = '0'; }
      // Re-apply the width the rendering was taken without.
      std::size_t pad = w > static_cast<std::streamsize>(n) ? static_cast<std::size_t>(w) - n : 0;
      const auto adjust = io.flags() & std::ios_base::adjustfield;
      std::size_t k = 0;
      if (pad && adjust == std::ios_base::internal && i == 1) *out++ = s.b[k++];
      if (pad && adjust != std::ios_base::left) for (; pad; --pad) *out++ = fill;
      for (; k < n; ++k) *out++ = s.b[k];
      for (; pad; --pad) *out++ = fill;
      return out;
    }
    iter_type do_put(iter_type out, std::ios_base& io, char fill, double v) const override {
      return put_marked(out, io, fill, v);
    }
    iter_type do_put(iter_type out, std::ios_base& io, char fill, long double v) const override {
      return put_marked(out, io, fill, v);
    }
  };
  inline void typed_float_print(std::ostream& os) {
    os.imbue(std::locale(os.getloc(), new float_put));
  }

  // ---- The arithmetic contract's FAULTS (docs/formalism.md section 2.4,
  // "Arithmetic semantics"). Each is transcribed into the interpreter
  // (src/Interp/Numerics.fs computeReal / intPow / evalCast) and the LLVM
  // lane's shim (src/cpp/blade_llvm_shim.c blade_idiv / blade_imod /
  // blade_ipow / blade_f2i64) with the SAME code and the SAME message: the
  // three lanes must fail identically, not merely all fail.
  //
  // Codegen emits these only where a fault is possible: a nonzero literal
  // divisor other than -1 stays a plain `/`, and a literal nonnegative
  // exponent calls the panic-free blade_arith::ipow_nn directly (so such a
  // kernel keeps its shadow-frame elision -- CodeGen.panicFreeNamespaces).
  //
  // `file` / `line` are the SOURCE position of the `/`, `%`, `^` or cast
  // (the IR node's SrcLoc), so the panic can say `--> file:line` like every
  // other located guard. They cost nothing on the hot path: two constants
  // that only the cold [[noreturn]] branch ever materializes.
  //
  // Integer `/` and `%` truncate toward zero; a zero divisor panics BL8013.
  // MIN / -1 WRAPS to MIN and MIN % -1 is 0, the two's-complement answers
  // -fwrapv gives every other integer op -- in C++ both are UB, and x86's
  // idiv traps on them, so the -1 arm is explicit rather than left to `/`.
  template <typename T> inline T idiv(T a, T b, const char* file = nullptr, int line = 0) {
    if (b == 0) panic("BL8013", "integer division by zero", file, line);
    if (b == T(-1))
      return static_cast<T>(static_cast<std::make_unsigned_t<T>>(0) -
                            static_cast<std::make_unsigned_t<T>>(a));
    return a / b;
  }
  template <typename T> inline T imod(T a, T b, const char* file = nullptr, int line = 0) {
    if (b == 0) panic("BL8013", "integer modulo by zero", file, line);
    if (b == T(-1)) return T(0);
    return a % b;
  }
  // Integer `^`: exact (see blade_arith::ipow_nn); a negative exponent has
  // no integer answer and panics BL8013.
  template <typename T> inline T ipow(T b, T e, const char* file = nullptr, int line = 0) {
    if (e < 0) panic("BL8013", "integer power with a negative exponent", file, line);
    return blade_arith::ipow_nn<T>(b, e);
  }
  // Float -> integer conversion (`Int64(floor(x))`, `Int32(ceil(x))`, ...):
  // truncation toward zero of a value the target can hold. NaN, +-inf and
  // anything outside [-2^(w-1), 2^(w-1)) panic BL8014 -- static_cast is UB
  // there (x86 answers the INT_MIN sentinel), and a NaN bin index is a bug
  // the program should hear about, not a 0 or a clamp it should compute on.
  // The comparisons are exact: both bounds are powers of two.
  template <typename T> inline T f2i(double x, const char* file = nullptr, int line = 0) {
    constexpr double lim = static_cast<double>(std::make_unsigned_t<T>(1) << (sizeof(T) * 8 - 1));
    if (!(x >= -lim && x < lim))
      panic("BL8014", "float-to-integer conversion of NaN or an out-of-range value", file, line);
    return static_cast<T>(x);
  }
  inline int64_t idiv64(int64_t a, int64_t b, const char* file = nullptr, int line = 0) { return idiv<int64_t>(a, b, file, line); }
  inline int32_t idiv32(int32_t a, int32_t b, const char* file = nullptr, int line = 0) { return idiv<int32_t>(a, b, file, line); }
  inline int64_t imod64(int64_t a, int64_t b, const char* file = nullptr, int line = 0) { return imod<int64_t>(a, b, file, line); }
  inline int32_t imod32(int32_t a, int32_t b, const char* file = nullptr, int line = 0) { return imod<int32_t>(a, b, file, line); }
  inline int64_t ipow64(int64_t b, int64_t e, const char* file = nullptr, int line = 0) { return ipow<int64_t>(b, e, file, line); }
  inline int32_t ipow32(int32_t b, int32_t e, const char* file = nullptr, int line = 0) { return ipow<int32_t>(b, e, file, line); }
  inline int64_t f2i64(double x, const char* file = nullptr, int line = 0) { return f2i<int64_t>(x, file, line); }
  inline int32_t f2i32(double x, const char* file = nullptr, int line = 0) { return f2i<int32_t>(x, file, line); }

  // ---- lgamma(x) = log Gamma(x), x > 0. Lanczos approximation, g = 7, n = 9.
  //
  // HAND-ROLLED ON PURPOSE; this is deliberately NOT std::lgamma. The
  // tree-walking interpreter (src/Interp/Numerics.fs) must reproduce every
  // double these binaries print, BIT FOR BIT. It manages that for the other
  // intrinsics by P/Invoking the very ucrtbase.dll that MinGW's libstdc++
  // forwards <cmath> to -- a trick with no counterpart here, because .NET has
  // no gamma function at all to fall back on and the ucrt/glibc lgamma
  // implementations are not the same function. A series written out in plain
  // sequential double arithmetic is the only form BOTH sides can execute
  // identically, so this function is transcribed statement for statement into
  // Interp/Numerics.fs `lgammaLanczos`. KEEP THE TWO IN LOCKSTEP: same
  // coefficients, same association, no reassociation, no reordering. (Byte
  // identity also needs FMA contraction off, which is what the differential
  // gates already pin -- BLADE_FP_CONTRACT=off, see src/Build.fs:38.)
  //
  // DOMAIN: x > 0 only. `!(x > 0.0)` also catches NaN. Log-densities (Gamma /
  // Poisson / Beta, the callers this exists for) never need a non-positive
  // argument, so the reflection formula that would extend it to x < 0 is
  // deliberately absent: one fewer thing to keep bit-identical, and a
  // non-positive argument is a caller bug that panics rather than seeping
  // through as a silent NaN.
  //
  // The panic is also why a generated body calling this KEEPS its shadow
  // frame: `blade_rt::` is deliberately absent from CodeGen's
  // panicFreeNamespaces (CodeGen.fs:15178), so the frame analysis classifies
  // such a body as UNKNOWN and resolveShadowFrames leaves its BLADE_FRAME in
  // place. That is the reason this must live HERE and nowhere else -- the
  // tripwire in tests/Test_Diagnostics.fs fails if any other src/cpp header
  // names blade_rt::panic.
  inline double lgamma(double x) {
    if (!(x > 0.0))
      panic("BL8008", "lgamma: argument must be positive", nullptr, 0);
    // Gamma(1) = Gamma(2) = 1 exactly; the series lands a few ulp off zero.
    // The same two comparisons run on the interpreter side, so this stays
    // exact on both.
    if (x == 1.0 || x == 2.0) return 0.0;
    // The series is usually written over z = x - 1 with denominators (z + k);
    // it is written over x here instead (same values, z + k == x + (k-1)),
    // because forming `(x - 1) + k` cancels catastrophically for small x --
    // (1e-8 - 1) + 1 is not 1e-8 -- and costs ~9 digits below x ~ 1e-6.
    double s = 0.99999999999980993;
    s += 676.5203681218851     / x;
    s += -1259.1392167224028   / (x + 1.0);
    s += 771.32342877765313    / (x + 2.0);
    s += -176.61502916214059   / (x + 3.0);
    s += 12.507343278686905    / (x + 4.0);
    s += -0.13857109526572012  / (x + 5.0);
    s += 9.9843695780195716e-6 / (x + 6.0);
    s += 1.5056327351493116e-7 / (x + 7.0);
    const double t = x + 6.5;   // (x - 1) + g + 0.5, with g = 7
    // 0.9189385332046727 = log(2*pi) / 2
    return 0.9189385332046727 + (x - 0.5) * std::log(t) - t + std::log(s);
  }

  // ---- digamma(x) = psi(x) = d/dx log Gamma(x), x > 0.
  //
  // HAND-ROLLED for the same reason lgamma above is, and under the same
  // contract: transcribed statement for statement into Interp/Numerics.fs
  // `digammaSeries`, and the two must stay in LOCKSTEP -- same constants,
  // same association, no reassociation, no reordering. Neither ucrtbase nor
  // .NET has a digamma to borrow, so a series in plain sequential double
  // arithmetic is again the only form both evaluators can run identically.
  //
  // METHOD: recurrence down to the asymptotic regime, then the Stirling-type
  // asymptotic series. psi(x) = psi(x+1) - 1/x is applied until x >= 10,
  // accumulating the shifts in `r`; then
  //
  //   psi(x) ~ log(x) - 1/(2x) - sum_{n>=1} B_{2n} / (2n x^{2n})
  //
  // truncated after n = 7. The seven coefficients B_{2n}/(2n) are, with
  // B_2..B_14 = 1/6, -1/30, 1/42, -1/30, 5/66, -691/2730, 7/6:
  //
  //   n=1  B_2 /2  =  1/12          n=5  B_10/10 =  1/132
  //   n=2  B_4 /4  = -1/120         n=6  B_12/12 = -691/32760
  //   n=3  B_6 /6  =  1/252         n=7  B_14/14 =  1/12
  //   n=4  B_8 /8  = -1/240
  //
  // WHY x >= 10 AND SEVEN TERMS: the series is asymptotic, so the pair is a
  // measured choice, not a convention. Against a long-double reference
  // (same series, shifted to x >= 40) over 206,001 probes -- a 200,000-point
  // linear sweep of (0, 100] and a 6,001-point geometric sweep of
  // 1e-30..1e30, scored as |err| / max(|psi|, 1) so the metric stays
  // meaningful across psi's zero at x = 1.4616... -- the worst error is:
  //
  //   shift to  4 terms   5 terms   6 terms   7 terms
  //   x >= 6    ...       8.8e-12   9.3e-13   1.3e-13
  //   x >= 8    ...       2.9e-13   1.8e-14   2.1e-15
  //   x >= 10   ...       2.1e-14   1.8e-15   1.1e-15   <-- chosen
  //
  // 1.1e-15 is the double-roundoff floor of the recurrence itself (the
  // accumulated -1/x cancels against log(x) to about one digit near x ~ 1.2),
  // so more terms or a larger shift buy nothing. The customary "shift to
  // x >= 6" would have left ~1e-13, two orders short.
  //
  // FMA-FREE BY CONSTRUCTION: the series is summed as `c / p` with p stepped
  // by `p = p * x2`, never as the Horner `s = s * f + c`. Both forms measure
  // identically (1.130e-15 worst, same argument), but this one contains no
  // multiply-add pair anywhere, so no contraction setting on either side can
  // perturb it -- the byte-identity claim does not rest on -ffp-contract=off
  // for this function. (lgamma above is FMA-free for the same reason: its
  // terms are add-of-divide.)
  //
  // Nothing is pinned at special points, unlike lgamma's exact zeros at
  // x = 1 and x = 2: psi has no rational value at any convenient argument
  // (psi(1) = -gamma), so there is nothing exact to return.
  //
  // DOMAIN: x > 0, `!(x > 0.0)` so NaN is refused too, panicking with the
  // same BL8008 lgamma uses. Same reasoning: a non-positive argument here is
  // a caller bug, and a silent NaN would surface much later as an
  // unexplained NaN gradient. The reflection formula is deliberately absent.
  inline double digamma(double x) {
    if (!(x > 0.0))
      panic("BL8008", "digamma: argument must be positive", nullptr, 0);
    // psi(x) = psi(x+1) - 1/x, applied until the asymptotic series is good.
    double r = 0.0;
    while (x < 10.0) { r -= 1.0 / x; x += 1.0; }
    const double x2 = x * x;
    double p = x2;                                  // x^2, then x^4, x^6, ...
    double s =      (1.0 / 12.0)      / p;  p = p * x2;
    s = s +        (-1.0 / 120.0)     / p;  p = p * x2;
    s = s +         (1.0 / 252.0)     / p;  p = p * x2;
    s = s +        (-1.0 / 240.0)     / p;  p = p * x2;
    s = s +         (1.0 / 132.0)     / p;  p = p * x2;
    s = s +      (-691.0 / 32760.0)   / p;  p = p * x2;
    s = s +         (1.0 / 12.0)      / p;
    return r + std::log(x) - 0.5 / x - s;
  }
}
#define BLADE_FRAME(fn, file, line) blade_rt::Scope __blade_frame_(fn, file, line)
#else
#define BLADE_FRAME(fn, file, line)
#endif
