/* Blade LLVM lane runtime shim -- the C half of the BLADE_LLVM back end.
 *
 * PLAIN C ON PURPOSE. The .ll that `src/EmitLlvm.fs` writes calls these by
 * symbol across a fixed C ABI; a C++ TU would mangle them and drag in the
 * iostreams the LLVM lane exists to skip. One TU, compiled once per build
 * directory with `clang -c -O2` (Build.compileLlvmProgram) and linked into
 * every program the lane produces.
 *
 * THE LOAD-BEARING CONTRACT IS THE OUTPUT BYTES. Every corpus EXPECT pin is a
 * byte pin against what the C++ lane prints, so these printers must reproduce
 *     cout << setprecision(15) << boolalpha
 * exactly (src/Interp/CppFormat.fs is the same contract stated for the
 * interpreter, and is the reference for every rule below):
 *   - double: printf "%.15g" -- 15 significant digits, scientific iff the
 *     decimal exponent is < -4 or >= 15, trailing zeros stripped, two-digit
 *     minimum exponent. Verified byte-identical against a ucrt64 g++ iostream
 *     probe over the interesting bit patterns (0, -0, denormals, 1e15/1e16
 *     boundary, DBL_MIN/DBL_MAX, float-promoted values).
 *   - NaN prints "nan" with NO sign and no payload, which is what ucrt
 *     iostreams do and what printf does NOT (it emits "-nan" for a negative
 *     NaN) -- hence the explicit isnan arm.
 *   - +/-infinity print "inf" / "-inf"; -0.0 prints "-0" (printf already does).
 *   - bool prints "true"/"false" (boolalpha).
 *   - int64 prints plain decimal, INT64_MIN included.
 *
 * The allocators are calloc-shaped BY CONTRACT, not by convenience: recursive
 * arrays read past the built prefix and rely on those cells being zero.
 */

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <math.h>
#include <time.h>

#if defined(_WIN32)
#include <malloc.h> /* _aligned_malloc / _aligned_free */
#endif

#if defined(_MSC_VER)
#define BLADE_NORETURN __declspec(noreturn)
#else
#define BLADE_NORETURN __attribute__((noreturn))
#endif

/* ---- failure ---------------------------------------------------------- */

/* Mirrors blade_rt::panic's shape: the caller passes the whole rendered
 * line ("error[BL8002]: ..."), it goes to stderr, and the process exits 1.
 * No shadow-frame trace: the LLVM lane emits no BLADE_FRAME scaffolding. */
BLADE_NORETURN void blade_panic(const char *msg) {
    fputs(msg ? msg : "error[BL8000]: blade: unknown failure", stderr);
    fputc('\n', stderr);
    fflush(stderr);
    exit(1);
}

/* ---- allocation ------------------------------------------------------- */

/* EVERY pool is 64-byte aligned, and that is a CONTRACT the emitted IR relies
 * on: `src/EmitLlvm.fs` declares
 *     declare noalias align 64 ptr @blade_alloc_cells(...)
 * so LLVM is entitled to assume the returned pointer's low six bits are zero
 * (and to fold away the runtime alignment checks its vectorizer would
 * otherwise emit, and to use aligned moves). If this allocator ever stops
 * honouring it, that declaration is a miscompile, not a missed optimization.
 * 64 is a cache line on every target this lane builds for, which is also what
 * keeps two pools from sharing one line.
 *
 * The pointer must be released through `blade_free`, which knows the platform
 * pairing (`_aligned_free` on Windows, plain `free` elsewhere) -- mixing them
 * is undefined. */
#define BLADE_POOL_ALIGN 64

static void *blade_alloc_aligned_zeroed(size_t bytes) {
    void *p = NULL;
    /* A zero-cell pool still gets storage: the emitted code may hold the
     * pointer even when no loop iteration ever dereferences it, and a NULL
     * would make that harmless hold undefined. */
    size_t want = bytes ? bytes : (size_t)BLADE_POOL_ALIGN;
    /* Round UP to the alignment: C11 aligned_alloc requires a size that is a
     * multiple of the alignment, and rounding is harmless for the others. */
    size_t rounded = (want + (size_t)(BLADE_POOL_ALIGN - 1))
                     & ~(size_t)(BLADE_POOL_ALIGN - 1);
    if (rounded < want) blade_panic("error[BL8006]: allocation size overflow");
#if defined(_WIN32)
    p = _aligned_malloc(rounded, (size_t)BLADE_POOL_ALIGN);
#elif defined(__STDC_VERSION__) && __STDC_VERSION__ >= 201112L
    p = aligned_alloc((size_t)BLADE_POOL_ALIGN, rounded);
#else
    if (posix_memalign(&p, (size_t)BLADE_POOL_ALIGN, rounded) != 0) p = NULL;
#endif
    if (!p) blade_panic("error[BL8006]: out of memory");
    /* calloc semantics are load-bearing, not convenient -- a recursive array
     * reads past its built prefix and the language promises zeros there. The
     * aligned allocators do not zero, so this memset IS the guarantee. */
    memset(p, 0, rounded);
    return p;
}

/* `n * elemBytes`, with the multiplication checked: an extent product that
 * overflows size_t would otherwise allocate a small pool and let every
 * subsequent write run off the end. */
static size_t blade_pool_bytes(long long n, long long elemBytes) {
    size_t cells, each;
    if (n < 0 || elemBytes <= 0) blade_panic("error[BL8006]: negative allocation length");
    cells = (size_t)n;
    each = (size_t)elemBytes;
    if (cells != 0 && cells > (size_t)-1 / each)
        blade_panic("error[BL8006]: allocation size overflow");
    return cells * each;
}

void *blade_alloc_f64(long long n) {
    return blade_alloc_aligned_zeroed(blade_pool_bytes(n, (long long)sizeof(double)));
}

void *blade_alloc_i64(long long n) {
    return blade_alloc_aligned_zeroed(blade_pool_bytes(n, (long long)sizeof(long long)));
}

/* The general dense-array pool: `n` cells of `cellBytes` each, zeroed and
 * 64-byte aligned. Sized in BYTES because a Bool pool stores one byte per cell
 * (i1 has no settled memory layout to share across the C boundary) while every
 * other element type stores eight. */
void *blade_alloc_cells(long long n, long long cellBytes) {
    return blade_alloc_aligned_zeroed(blade_pool_bytes(n, cellBytes));
}

/* NULL is a NO-OP by contract, not by luck: the emitter's scope-exit frees
 * load a tracking slot that holds null whenever the allocation's branch was
 * not taken this execution, or the pool escaped its scope (`keepPool`). */
void blade_free(void *p) {
    if (!p) return;
#if defined(_WIN32)
    _aligned_free(p);
#else
    free(p);
#endif
}

/* ---- clock ------------------------------------------------------------ */

/* Seconds, monotone enough for the "<name> completed in <t>s" line. The C++
 * lane uses std::chrono::high_resolution_clock; both are wall-clock
 * measurements, so the VALUE differs run to run in either lane and only the
 * line's shape is pinned. */
double blade_now(void) {
    struct timespec ts;
    if (timespec_get(&ts, TIME_UTC) == TIME_UTC)
        return (double)ts.tv_sec + 1e-9 * (double)ts.tv_nsec;
    return (double)clock() / (double)CLOCKS_PER_SEC;
}

/* ---- printing --------------------------------------------------------- */

/* `cout << setprecision(15) << x` for a double, into `buf`. See the header
 * comment for why nan/inf are handled before printf gets a chance. */
static void blade_fmt_f64(char *buf, size_t cap, double v) {
    if (isnan(v)) { snprintf(buf, cap, "nan"); return; }
    if (isinf(v)) { snprintf(buf, cap, v < 0.0 ? "-inf" : "inf"); return; }
    snprintf(buf, cap, "%.15g", v);
}

void blade_print_i64(const char *name, long long v) {
    printf("%s = %lld\n", name, v);
}

void blade_print_f64(const char *name, double v) {
    char buf[64];
    blade_fmt_f64(buf, sizeof buf, v);
    printf("%s = %s\n", name, buf);
}

void blade_print_bool(const char *name, int v) {
    printf("%s = %s\n", name, v ? "true" : "false");
}

void blade_print_str(const char *name, const char *v) {
    printf("%s = %s\n", name, v ? v : "");
}

/* ---- unlabelled output (array elements and brackets) ------------------- */

/* An ARRAY line is assembled piecewise -- `name = [`, elements, separators,
 * `]` -- because its loop structure lives in the emitted IR, not here. Each
 * value goes through the SAME formatter the labelled printers use, so a
 * double inside an array and the same double as a scalar binding print
 * identical bytes (which is what the EXPECT pins compare). No newline: the
 * caller emits "\n" as its final piece. */
void blade_out_str(const char *s) { fputs(s ? s : "", stdout); }

void blade_out_i64(long long v) { printf("%lld", v); }

void blade_out_f64(double v) {
    char buf[64];
    blade_fmt_f64(buf, sizeof buf, v);
    fputs(buf, stdout);
}

void blade_out_bool(int v) { fputs(v ? "true" : "false", stdout); }

/* The timing line genMainWrapper emits:
 *     cout << "<name> completed in " << elapsed << "s" << endl; */
void blade_print_completed(const char *name, double seconds) {
    char buf[64];
    blade_fmt_f64(buf, sizeof buf, seconds);
    printf("%s completed in %ss\n", name, buf);
}
