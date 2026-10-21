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
 *   - a double whose "%.15g" text is a bare digit run gains ".0" ("2.0",
 *     "-0.0"): the typed float spelling, blade_rt::float_put in the C++ lane.
 *   - bool prints "true"/"false" (boolalpha).
 *   - int64 prints plain decimal, INT64_MIN included.
 *
 * The allocators return UNINITIALIZED storage, exactly like the C++ lane's
 * `new T[cardinality]` (linearized_storage.hpp): both lanes emit from the same
 * IR, and every zero the language promises -- a recursive array's implicit
 * prefix, a guard's else arm -- is an EXPLICIT store that IR already contains,
 * so an allocator memset only re-wrote memory the emitted fill was about to
 * write again (measured at a full write pass per pool, ~13-20% of the whole
 * program on compact-map fixtures: plan-simplex-blocked-compute.md section 0b).
 */

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <math.h>
#include <time.h>
#include <stdint.h>

#if defined(_WIN32)
#include <malloc.h> /* _aligned_malloc / _aligned_free */
#endif

/* Membership hoisting's C core, shared with the C++ lane (the exports are at
 * the end of this file). Deployed beside the shim by EmitLlvm.deployShim and
 * hashed into the shim object's identity (Build.buildLlvmShim). */
#include "blade_member.h"

#if defined(_MSC_VER)
#define BLADE_NORETURN __declspec(noreturn)
#else
#define BLADE_NORETURN __attribute__((noreturn))
#endif

/* ---- failure ---------------------------------------------------------- */

/* The shadow call stack: the twin of blade_rt::stack / depth. The EMITTED IR
 * writes it -- inline loads and stores at each frame the emitter resolved as
 * able to reach a panic (EmitLlvm.resolveFrames), so a kernel inlined into a
 * hot loop pays a store, not a call -- and only the panics below read it.
 * Plain globals, not _Thread_local: this lane has no threads (every function
 * is `nosync`). 65 slots: 0..63 are the trace, slot 64 is the write-only sink
 * the emitter's branchless clamp sends overflow frames to, exactly as in
 * blade_rt::Scope. A frame is its source name; the C++ lane's file/line
 * fields are always (nullptr, 0), so they print nothing there either. */
const char *blade_frame_stack[65];
int blade_frame_depth = 0;

/* blade_rt::panic's trace, innermost first. */
static void blade_print_frames(void) {
    int d = blade_frame_depth < 64 ? blade_frame_depth : 64;
    for (int i = d - 1; i >= 0; --i) fprintf(stderr, "  at %s\n", blade_frame_stack[i]);
}

/* Mirrors blade_rt::panic's shape: the caller passes the whole rendered
 * line ("error[BL8002]: ..."), it goes to stderr, then the frame trace, and
 * the process exits 1. */
BLADE_NORETURN void blade_panic(const char *msg) {
    fputs(msg ? msg : "error[BL8000]: blade: unknown failure", stderr);
    fputc('\n', stderr);
    blade_print_frames();
    fflush(stderr);
    exit(1);
}

/* The located twin: blade_rt::panic's `  --> file:line` second line, when the
 * emitter knew where the faulting expression was (file null or line 0 when it
 * did not), before the trace. Byte-identical to the C++ lane's rendering. */
BLADE_NORETURN void blade_panic_at(const char *msg, const char *file, long long line) {
    fputs(msg, stderr);
    fputc('\n', stderr);
    if (file && line > 0) fprintf(stderr, "  --> %s:%lld\n", file, line);
    blade_print_frames();
    fflush(stderr);
    exit(1);
}

/* ---- the arithmetic contract ------------------------------------------- */

/* docs/formalism.md section 2.4. Twins of blade_rt::idiv / imod / ipow / f2i
 * and blade_arith::fpow (src/cpp/blade_runtime.hpp) and of the interpreter's
 * Numerics.computeReal / intPow64 / realPow / floatToInt64: same answers,
 * same codes, same messages. The emitter calls these only where a fault is
 * possible (a nonzero literal divisor other than -1 stays a plain sdiv). */
long long blade_idiv(long long a, long long b, const char *file, long long line) {
    if (b == 0) blade_panic_at("error[BL8013]: integer division by zero", file, line);
    /* MIN / -1 wraps to MIN; sdiv would be UB, x86 idiv traps. */
    if (b == -1) return (long long)(0ULL - (unsigned long long)a);
    return a / b;
}

long long blade_imod(long long a, long long b, const char *file, long long line) {
    if (b == 0) blade_panic_at("error[BL8013]: integer modulo by zero", file, line);
    if (b == -1) return 0;
    return a % b;
}

/* Exact modulo 2^64 (square-and-multiply in unsigned arithmetic); 0^0 = 1. */
long long blade_ipow(long long b, long long e, const char *file, long long line) {
    unsigned long long r = 1, x = (unsigned long long)b, n;
    if (e < 0) blade_panic_at("error[BL8013]: integer power with a negative exponent", file, line);
    n = (unsigned long long)e;
    while (n != 0) {
        if (n & 1ULL) r *= x;
        x *= x;
        n >>= 1;
    }
    return (long long)r;
}

/* Real `^`: x * x at an exponent of exactly 2, else the platform pow. */
double blade_fpow(double x, double e) { return e == 2.0 ? x * x : pow(x, e); }

/* Real `^` an INTEGER exponent: repeated squaring, the twin of
 * blade_arith::fpowi (blade_runtime.hpp) and StaticEval.floatPowInt -- the
 * same multiplies in the same order, so the lanes agree bit for bit. NOT
 * llvm.powi: its multiplication order is unspecified, its exponent is i32,
 * and on x86_64-w64-mingw32 clang lowers a run-time-exponent powi to a call
 * of libm pow (measured: it differs from this loop in most cases). */
double blade_fpowi(double x, long long e) {
    unsigned long long n = e < 0 ? 0ULL - (unsigned long long)e : (unsigned long long)e;
    double y = (n & 1ULL) ? x : 1.0;
    while ((n >>= 1) != 0) {
        x = x * x;
        if (n & 1ULL) y = y * x;
    }
    return e < 0 ? 1.0 / y : y;
}

/* Float -> Int64: truncation of a value the target holds; NaN / +-inf / out
 * of [-2^63, 2^63) panics (fptosi would be poison). Exact comparisons: both
 * bounds are powers of two. */
long long blade_f2i64(double x, const char *file, long long line) {
    if (!(x >= -9223372036854775808.0 && x < 9223372036854775808.0))
        blade_panic_at("error[BL8014]: float-to-integer conversion of NaN or an out-of-range value", file, line);
    return (long long)x;
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

/* ---- the retained-block cache (bench/notes/microkernel-sketches-review.md M1)
 *
 * The twin of the C++ lane's `blade_pool` (nested_array_utilities.hpp): the
 * UCRT heap returns a freed block of a few hundred KB to the OS, so the next
 * pool of the same size demand-faults all its pages again -- a time-stepping
 * recursive array allocates and frees the same row-sized temporaries every
 * step (bench recur: 8 x 160 KB per step, ~29.5 faults per allocation). A few
 * freed pools stay COMMITTED here and go back to the next request of the SAME
 * rounded byte size. Nothing a program computes changes: a reused pool is as
 * uninitialized as a fresh one (see the NO memset note below), and every cell
 * the language promises a value for is an explicit store in the IR.
 *
 * Every pool carries a BLADE_POOL_HDR-byte header in front of the payload
 * holding its rounded payload size, because `blade_free(void*)` has no size
 * parameter. The header is a whole alignment unit, so the payload keeps the
 * 64-byte alignment the `align 64` declaration promises; the TRUE base (the
 * pointer the platform allocator returned) is always `payload - HDR`, and only
 * pools this allocator made ever reach blade_free.
 *
 * Policy (measured, not semantics; review section 4.4): payloads of 16 KB ..
 * 4 MB, at most 16 slots and 8 MB retained, oldest evicted first, exact-size
 * matching, newest first. PLAIN GLOBALS, like the shadow stack above: this
 * lane has no threads (every emitted function is `nosync`); a future thread
 * runtime must make these _Thread_local. Retained blocks are freed by an
 * atexit hook.
 *
 * OFF under BLADE_POOL_CACHE_OFF (Build.fs defines it for every memcheck
 * build, and the shim object is keyed on its defines so the two never share
 * an object) and under AddressSanitizer: a cached free is no free to a leak
 * counter and would hide a use-after-free. Off, every free is a real free; the
 * header layout is the same either way. */
#define BLADE_POOL_HDR ((size_t)BLADE_POOL_ALIGN)

#if defined(__SANITIZE_ADDRESS__)
#  ifndef BLADE_POOL_CACHE_OFF
#    define BLADE_POOL_CACHE_OFF 1
#  endif
#elif defined(__has_feature)
#  if __has_feature(address_sanitizer)
#    ifndef BLADE_POOL_CACHE_OFF
#      define BLADE_POOL_CACHE_OFF 1
#    endif
#  endif
#endif
#if defined(BLADE_POOL_CACHE_OFF) && BLADE_POOL_CACHE_OFF
#  define BLADE_POOL_CACHE_ON 0
#else
#  define BLADE_POOL_CACHE_ON 1
#endif

#define BLADE_CACHE_MIN      ((size_t)16 << 10)
#define BLADE_CACHE_MAXBLOCK ((size_t)4 << 20)
#define BLADE_CACHE_BUDGET   ((size_t)8 << 20)
#define BLADE_CACHE_SLOTS    16

#if defined(BLADE_POOL_CACHE_STATS) && BLADE_POOL_CACHE_STATS
/* Test builds only: fresh (eligible miss) / reused / retained / evicted. */
static long long g_pool_stats[4];
#  define BLADE_POOL_COUNT(k) (g_pool_stats[(k)]++)
/* out[0..3] = fresh, reused, retained, evicted; out[4] = cache on (1) / off (0). */
void blade_pool_cache_stats(long long *out) {
    int k;
    for (k = 0; k < 4; k++) out[k] = g_pool_stats[k];
    out[4] = BLADE_POOL_CACHE_ON;
}
#else
#  define BLADE_POOL_COUNT(k) ((void)0)
#endif

static void *blade_raw_alloc(size_t bytes) {
    void *p = NULL;
#if defined(_WIN32)
    p = _aligned_malloc(bytes, (size_t)BLADE_POOL_ALIGN);
#elif defined(__STDC_VERSION__) && __STDC_VERSION__ >= 201112L
    p = aligned_alloc((size_t)BLADE_POOL_ALIGN, bytes);
#else
    if (posix_memalign(&p, (size_t)BLADE_POOL_ALIGN, bytes) != 0) p = NULL;
#endif
    return p;
}

static void blade_raw_free(void *raw) {
#if defined(_WIN32)
    _aligned_free(raw);
#else
    free(raw);
#endif
}

#if BLADE_POOL_CACHE_ON
static char  *g_cache_raw[BLADE_CACHE_SLOTS];   /* true bases (header included) */
static size_t g_cache_b[BLADE_CACHE_SLOTS];     /* rounded payload sizes */
static int    g_cache_n;
static size_t g_cache_bytes;
static int    g_cache_hooked;

static void blade_cache_evict_oldest(void) {
    blade_raw_free(g_cache_raw[0]);
    g_cache_bytes -= g_cache_b[0];
    memmove(&g_cache_raw[0], &g_cache_raw[1], (size_t)(g_cache_n - 1) * sizeof(char *));
    memmove(&g_cache_b[0], &g_cache_b[1], (size_t)(g_cache_n - 1) * sizeof(size_t));
    g_cache_n--;
    BLADE_POOL_COUNT(3);
}

static void blade_cache_flush(void) {
    while (g_cache_n > 0) blade_cache_evict_oldest();
}
#endif

static void *blade_alloc_aligned(size_t bytes) {
    char *raw;
    /* A zero-cell pool still gets storage: the emitted code may hold the
     * pointer even when no loop iteration ever dereferences it, and a NULL
     * would make that harmless hold undefined. */
    size_t want = bytes ? bytes : (size_t)BLADE_POOL_ALIGN;
    /* Round UP to the alignment: C11 aligned_alloc requires a size that is a
     * multiple of the alignment, and rounding is harmless for the others. */
    size_t rounded = (want + (size_t)(BLADE_POOL_ALIGN - 1))
                     & ~(size_t)(BLADE_POOL_ALIGN - 1);
    if (rounded < want || rounded + BLADE_POOL_HDR < rounded)
        blade_panic("error[BL8006]: allocation size overflow");
#if BLADE_POOL_CACHE_ON
    if (rounded >= BLADE_CACHE_MIN && rounded <= BLADE_CACHE_MAXBLOCK) {
        int k;
        for (k = g_cache_n - 1; k >= 0; k--) {          /* newest first: the hottest pages */
            if (g_cache_b[k] == rounded) {
                raw = g_cache_raw[k];
                memmove(&g_cache_raw[k], &g_cache_raw[k + 1], (size_t)(g_cache_n - 1 - k) * sizeof(char *));
                memmove(&g_cache_b[k], &g_cache_b[k + 1], (size_t)(g_cache_n - 1 - k) * sizeof(size_t));
                g_cache_n--;
                g_cache_bytes -= rounded;
                BLADE_POOL_COUNT(1);
                return raw + BLADE_POOL_HDR;
            }
        }
        BLADE_POOL_COUNT(0);
    }
#else
    if (rounded >= BLADE_CACHE_MIN && rounded <= BLADE_CACHE_MAXBLOCK) BLADE_POOL_COUNT(0);
#endif
    raw = (char *)blade_raw_alloc(rounded + BLADE_POOL_HDR);
#if BLADE_POOL_CACHE_ON
    if (!raw && g_cache_n > 0) {
        /* Out of memory with pools still retained: give them back, retry once. */
        blade_cache_flush();
        raw = (char *)blade_raw_alloc(rounded + BLADE_POOL_HDR);
    }
#endif
    if (!raw) blade_panic("error[BL8006]: out of memory");
    *(size_t *)raw = rounded;
    /* NO memset. Zeros the language promises (a recursive array's unbuilt
     * prefix, guard's else arm) are explicit stores in the emitted IR -- the
     * same contract the C++ lane's uninitialized `new T[]` pools live by. A
     * blanket zero here was one full write pass per pool inside the timed
     * region, and the emitter's fill was about to overwrite every cell of it
     * (every allocPool caller in src/EmitLlvm.fs fills the whole pool before
     * any read; see the invariant note on allocPool). A pool handed back by
     * the cache above is exactly as uninitialized, for the same reason. */
    return raw + BLADE_POOL_HDR;
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
    return blade_alloc_aligned(blade_pool_bytes(n, (long long)sizeof(double)));
}

void *blade_alloc_i64(long long n) {
    return blade_alloc_aligned(blade_pool_bytes(n, (long long)sizeof(long long)));
}

/* The general dense-array pool: `n` cells of `cellBytes` each, uninitialized,
 * 64-byte aligned. Sized in BYTES because a Bool pool stores one byte per cell
 * (i1 has no settled memory layout to share across the C boundary) while every
 * other element type stores eight. */
void *blade_alloc_cells(long long n, long long cellBytes) {
    return blade_alloc_aligned(blade_pool_bytes(n, cellBytes));
}

/* NULL is a NO-OP by contract, not by luck: the emitter's scope-exit frees
 * load a tracking slot that holds null whenever the allocation's branch was
 * not taken this execution, or the pool escaped its scope (`keepPool`). */
void blade_free(void *p) {
    char *raw;
    if (!p) return;
    raw = (char *)p - BLADE_POOL_HDR;
#if BLADE_POOL_CACHE_ON
    {
        size_t b = *(size_t *)raw;
        if (b >= BLADE_CACHE_MIN && b <= BLADE_CACHE_MAXBLOCK) {
            if (!g_cache_hooked) { g_cache_hooked = 1; atexit(blade_cache_flush); }
            while (g_cache_n > 0 && (g_cache_n == BLADE_CACHE_SLOTS || g_cache_bytes + b > BLADE_CACHE_BUDGET))
                blade_cache_evict_oldest();
            g_cache_raw[g_cache_n] = raw;
            g_cache_b[g_cache_n] = b;
            g_cache_n++;
            g_cache_bytes += b;
            BLADE_POOL_COUNT(2);
            return;
        }
    }
#endif
    blade_raw_free(raw);
}

/* ---- clock ------------------------------------------------------------ */

/* Seconds on a MONOTONIC clock, for the "<name> completed in <t>s" line. The
 * C++ lane uses std::chrono::steady_clock, which is QueryPerformanceCounter
 * on Windows; this reads the same counter so both lanes (and the Fortran
 * benchmark arms, which call QPC directly) time with one clock. Neither lane
 * pins the VALUE -- it differs run to run -- only the line's shape. */
#ifdef _WIN32
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#endif
double blade_now(void) {
#ifdef _WIN32
    static double period = 0.0;
    LARGE_INTEGER c;
    if (period == 0.0) {
        LARGE_INTEGER f;
        QueryPerformanceFrequency(&f);
        period = 1.0 / (double)f.QuadPart;
    }
    QueryPerformanceCounter(&c);
    return (double)c.QuadPart * period;
#else
    struct timespec ts;
    if (clock_gettime(CLOCK_MONOTONIC, &ts) == 0)
        return (double)ts.tv_sec + 1e-9 * (double)ts.tv_nsec;
    return (double)clock() / (double)CLOCKS_PER_SEC;
#endif
}

/* ---- printing --------------------------------------------------------- */

/* `cout << setprecision(15) << x` for a double, into `buf`. See the header
 * comment for why nan/inf are handled before printf gets a chance. */
static void blade_fmt_f64(char *buf, size_t cap, double v) {
    if (isnan(v)) snprintf(buf, cap, "nan");
    else if (isinf(v)) snprintf(buf, cap, v < 0.0 ? "-inf" : "inf");
    else {
        int n = snprintf(buf, cap, "%.15g", v);
        /* Typed float spelling: a bare digit run ("2", "-0") is an integer's
         * spelling, so a double gains ".0" -- the C++ lane's blade_rt::float_put
         * facet and the interpreter's CppFormat.markFloat apply the same rule. */
        if (n > 0 && (size_t)n + 3 <= cap) {
            int i = (buf[0] == '-') ? 1 : 0;
            int bare = n > i;
            for (int k = i; k < n; ++k)
                if (buf[k] < '0' || buf[k] > '9') { bare = 0; break; }
            if (bare) { buf[n] = '.'; buf[n + 1] = '0'; buf[n + 2] = '\0'; }
        }
    }
    /* BLADE_PRINT_BITS (a RUN-TIME test instrument, read once): append the
     * raw IEEE-754 bit pattern, " #<16 hex digits>" -- the C++ lane's
     * blade_rt::float_put appends the same text. */
    static int bits_mode = -1;
    if (bits_mode < 0) {
        const char *e = getenv("BLADE_PRINT_BITS");
        bits_mode = (e && *e && strcmp(e, "0") != 0 && strcmp(e, "off") != 0 && strcmp(e, "OFF") != 0
                     && strcmp(e, "false") != 0) ? 1 : 0;
    }
    if (bits_mode) {
        unsigned long long u;
        memcpy(&u, &v, sizeof u);
        size_t used = strlen(buf);
        if (used + 20 < cap) snprintf(buf + used, cap - used, " #%016llx", u);
    }
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

/* ---- the rank-1 compound's keyed read (bench gap G9) --------------------- */

/* The kept ORDINAL of source position `key` in the ascending kept-position
 * table `pos[0, m)` that `compound(dense, mask)` built (EmitLlvm's
 * emitCompoundInit), or -1 when the position was not kept: the twin of
 * compound_index_t<1>::linearize (src/cpp/index_types.h), the same binary
 * search over the same table, so both lanes resolve a key identically and
 * the emitter raises the same BL8006 on -1.
 *
 * Claims the emitter's declaration makes about this symbol (EmitLlvm
 * grpShimArgRead): it reads only memory reachable from `pos`, writes nothing,
 * touches no global, does no I/O, allocates and frees nothing, has no threads
 * or atomics, and returns on every input (a bounded bisection). */
long long blade_compact1_rank(const long long *pos, long long m, long long key) {
    long long a = 0, b = m;
    while (a < b) {
        long long mid = a + (b - a) / 2;
        if (pos[mid] < key) a = mid + 1; else b = mid;
    }
    return (a < m && pos[a] == key) ? a : -1;
}

/* ---- membership hoisting (round 3, B-T1) ---------------------------------- */

/* The LLVM lane's three entry points over blade_member.h -- the SAME chooser,
 * build and probe the C++ lane includes, so the two back ends pick the same
 * representation for the same (m, min, max, forced) and answer the same
 * booleans (blade_member.h states the contract). The structure lives in the
 * CALLER's storage: the emitter reserves an entry-block `alloca [128 x i8],
 * align 8` (BLADE_MEMBER_SLOT_BYTES below; the typedef refuses to compile if
 * the struct ever outgrows it), so no export allocates the struct itself and
 * none can fail: a build whose own tables cannot be allocated degrades to a
 * representation that needs none.
 *
 * Claims the emitter's declarations make (EmitLlvm shimTable):
 *   - blade_member_init_i64: reads cells[0, m), writes the slot, allocates
 *     the tables (and frees a half-made pair when an allocation fails, so it
 *     is NOT nofree), returns on every input (counted loops; an insertion
 *     that ends at an empty slot, the load being <= 1/2)   -> grpShimFree;
 *   - blade_member_has_i64: reads only (the slot and the tables it points
 *     to), writes nothing, no I/O, no threads, always returns -> memory(read);
 *   - blade_member_fini_i64: frees the tables                 -> grpShimFree;
 *   - blade_member_note_llvm: the build's report (below)      -> grpExternReturns. */
#define BLADE_MEMBER_SLOT_BYTES 128
typedef char blade_member_slot_fits[(sizeof(blade_member_i64) <= BLADE_MEMBER_SLOT_BYTES) ? 1 : -1];

void blade_member_init_i64(void *t, const int64_t *cells, long long m, long long forced) {
    blade_member_build_i64((blade_member_i64 *)t, cells, (int64_t)m, (int)forced);
}

int blade_member_has_i64(const void *t, long long x) {
    return blade_member_test_i64((const blade_member_i64 *)t, (int64_t)x);
}

void blade_member_fini_i64(void *t) {
    blade_member_free_i64((blade_member_i64 *)t);
}

/* The build's report (blade_member_note_i64: the actual representation beside
 * the preferred one, and why they differ), made by the emitter right after
 * each init -- a `[member] ...` line on stderr under BLADE_MEMBER_VERBOSE=1,
 * the same text the C++ lane prints (this lane writes no run record). Does
 * I/O, always returns -> grpExternReturns. */
void blade_member_note_llvm(const void *t, const char *host, const char *set) {
    blade_member_note_i64((const blade_member_i64 *)t, host, set);
}
