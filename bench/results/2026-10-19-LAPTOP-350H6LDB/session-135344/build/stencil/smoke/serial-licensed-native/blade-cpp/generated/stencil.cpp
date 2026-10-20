#include <cstdint>
#include <cstdlib>
#include <cmath>
#include <complex>
#include <functional>
#include <tuple>
#include <variant>
#include <string>
#include <iostream>
#include <iomanip>
#include <chrono>
#include <algorithm>
#include <numeric>
#include <vector>
#include <unordered_map>
#include <unordered_set>
#include <omp.h>
#include "nested_array_utilities.hpp"
#include "nested_array_types.hpp"
#include "orbit_wreath_utilities.hpp"
#include "rand_runtime.hpp"
#include <exception>
#include "blade_runtime.hpp"
#include "blade_run_record.hpp"
using std::cout;
using std::endl;

#include <sstream>  // blade_display::json1/json2/jsonnum
#include <cmath>    // blade_display::jsonval (non-finite -> null)
#include <charconv> // blade_display::jsonfloat (shortest round-trip)
#include <cstdlib>
#include <string>
#include <type_traits>
namespace blade_display {
// Display frames (docs/display-frames.md). Mirrors Blade.Display.Frame.
static int __blade_display_ord = 0;
static inline std::string __blade_display_esc(const std::string& s) {
    static const char* hx = "0123456789abcdef";
    std::string o; o.reserve(s.size() + 8);
    for (std::size_t i = 0; i < s.size(); ++i) {
        unsigned char c = (unsigned char)s[i];
        switch (c) {
        case '"': o += "\\\""; break;
        case '\\': o += "\\\\"; break;
        case '\n': o += "\\n"; break;
        case '\r': o += "\\r"; break;
        case '\t': o += "\\t"; break;
        case '\b': o += "\\b"; break;
        case '\f': o += "\\f"; break;
        default:
            if (c < 0x20) { o += "\\u00"; o += hx[(c >> 4) & 0xF]; o += hx[c & 0xF]; }
            else o += (char)c;
        }
    }
    return o;
}
// A user string as a quoted, escaped JSON string (display.json_string).
// Mirrors Blade.Display.Frame.jsonString.
static inline std::string jsonstr(const std::string& s) {
    return "\"" + __blade_display_esc(s) + "\"";
}
// JSON serialization of numeric arrays / scalars (display.json_array /
// display.json_num). A floating element goes out as its SHORTEST
// round-trip rendering (jsonfloat below) -- exact, where the print
// block's setprecision(15) would quantize a Float64 to 15 digits;
// Blade.Interp.CppFormat.formatFloatShortest is the byte-exact mirror,
// so the differential gate pins the two lanes together.
//
// jsonval is the per-element guard: JSON has no NaN/Infinity literal, so
// a non-finite value goes out as `null` (what plotly's own encoder
// writes) instead of the stream's bare `nan`/`inf`/`-inf`. Integral T
// always takes the finite branch. Mirrors Frame.jsonNumber -- and the
// test is on the VALUE, not on the rendered text, because the spelling
// of a non-finite is implementation-defined (`nan`, `-nan`, `NaN`,
// `1.#QNAN` across the standard libraries). isfinite is the portable
// predicate; matching the formatter's output would not be.
// Shortest round-trip rendering of a double, laid out like "%.17g": the
// fewest significant digits that parse back to the same double, fixed
// notation for decimal exponents in [-4, 17), scientific otherwise.
// Mirrors CppFormat.formatFloatShortest / assembleShortest byte for byte.
static inline std::string jsonfloat(double v) {
    char buf[64];
    auto r = std::to_chars(buf, buf + sizeof buf, v, std::chars_format::scientific);
    std::string s(buf, r.ptr);                     // [-]d[.ddd]e[+-]dd
    std::string sign; size_t p = 0;
    if (s[0] == '-') { sign = "-"; p = 1; }
    size_t e = s.find('e');
    std::string digits;
    for (size_t i = p; i < e; ++i) if (s[i] != '.') digits += s[i];
    while (digits.size() > 1 && digits.back() == '0') digits.pop_back();
    if (digits == "0") return sign + "0";
    int x = std::atoi(s.c_str() + e + 1);
    std::string out = sign;
    if (x < -4 || x >= 17) {
        out += digits[0];
        if (digits.size() > 1) { out += '.'; out += digits.substr(1); }
        out += 'e'; out += (x < 0 ? '-' : '+');
        int a = x < 0 ? -x : x;
        if (a < 10) out += '0';
        out += std::to_string(a);
    } else if (x >= 0) {
        size_t intLen = (size_t)x + 1;
        std::string padded = digits;
        if (padded.size() < intLen) padded.append(intLen - padded.size(), '0');
        out += padded.substr(0, intLen);
        if (padded.size() > intLen) { out += '.'; out += padded.substr(intLen); }
    } else {
        out += "0."; out.append((size_t)(-x) - 1, '0'); out += digits;
    }
    return out;
}
template <typename T>
static inline void jsonval(std::ostringstream& o, const T& x) {
    if (!std::isfinite((double)x)) { o << "null"; return; }
    if constexpr (std::is_floating_point_v<T>) o << jsonfloat((double)x); else o << x;
}
template <typename A>
static inline std::string json1(const A& a) {
    std::ostringstream o; o << '[';
    for (size_t i = 0; i < a.extents[0]; ++i) { if (i) o << ','; jsonval(o, a[i]); }
    o << ']'; return o.str();
}
template <typename A>
static inline std::string json2(const A& a) {
    std::ostringstream o; o << '[';
    for (size_t i = 0; i < a.extents[0]; ++i) {
        if (i) o << ','; o << '[';
        for (size_t j = 0; j < a.extents[1]; ++j) { if (j) o << ','; jsonval(o, a[i][j]); }
        o << ']';
    }
    o << ']'; return o.str();
}
template <typename T>
static inline std::string jsonnum(T x) {
    std::ostringstream o; jsonval(o, x); return o.str();
}
static inline bool emit(const char* head, bool quoted, const std::string& data,
                        const char* metaTail, const char* tag) {
    std::cout << "\x01" "blade-display" "\x01" << head;
    if (quoted) std::cout << '"' << __blade_display_esc(data) << '"';
    else std::cout << data;
    std::cout << ",\"meta\":{\"id\":\"" << tag << ++__blade_display_ord
              << "\"" << metaTail << "}}" << "\n";
    return true;
}
// display.emit_id: the same line with a RUNTIME meta.id in place of
// <tag><ordinal>, escaped like any other JSON string value. The ordinal
// counter is deliberately untouched -- Blade.Display.Frame.emitId does
// not consume one either, so an emit_id call never renumbers the plain
// emit frames around it in EITHER lane.
static inline bool emit_id(const char* head, bool quoted, const std::string& data,
                           const char* metaTail, const std::string& id) {
    std::cout << "\x01" "blade-display" "\x01" << head;
    if (quoted) std::cout << '"' << __blade_display_esc(data) << '"';
    else std::cout << data;
    std::cout << ",\"meta\":{\"id\":\"" << __blade_display_esc(id)
              << "\"" << metaTail << "}}" << "\n";
    return true;
}
}

#define TIME std::chrono::steady_clock::now()
#define TIME_DIFF std::chrono::duration_cast<std::chrono::nanoseconds>(__blade_t1 - __blade_t0).count()

#include "blade_fold_lanes.hpp"
using Y = int64_t;

using X = int64_t;


// Run record (BLADE_RUN_RECORD): the input manifest this program was compiled against,
// written with what the run observed when the process exits (blade_run_record.hpp).
static const blade_rr::Input __blade_rr_inputs[] = {
    { nullptr, nullptr, nullptr, nullptr, nullptr, nullptr, nullptr, nullptr, nullptr, nullptr }
};
static blade_rr::AtExit __blade_rr_at_exit{ "stencil", "0.20.0", false, __blade_rr_inputs, 0, nullptr };



double gen(int64_t k, int64_t seed, double lo, double hi);
Array<double, 2> inputs();
double kernel(Array<double, 2> u);
double __lambda_62(Y i, X j, int64_t& nx);
double __lambda_63(int64_t wr, int64_t wc, Array<double, 2>& u);
double __lambda_64(Array<double, 1> row);
double __lambda_65(double p, double q);
double __lambda_68(double a, double b);
double __lambda_69(Array<double, 1> __pfrow55);
double __lambda_72(double a, double b);

double gen(int64_t k, int64_t seed, double lo, double hi) {
    int64_t __v7 = ((((k + seed) * 1103515245L) + 12345L) % 2147483648L);
    int64_t __v8 = ((__v7 * ((__v7 / 4096L) + 1L)) % 2147483648L);
    int64_t __v9 = (((__v8 * 1103515245L) + 12345L) % 2147483648L);
    int64_t __v10 = (((__v9 * __v9) / 65536L) % 2147483648L);
    int64_t __v11 = (((__v10 * 1103515245L) + 12345L) % 2147483648L);
    return (lo + ((hi - lo) * (blade_arith::i64_to_f64(__v11) / 2147483648.0)));
}

Array<double, 2> inputs() {
    BLADE_FRAME("inputs", nullptr, 0);
    int64_t __v17 = static_cast<int64_t>(41L);

    static constexpr const size_t __ret1073741824_extents[2] = { 31, 41 };

    Array<double, 2> __ret1073741824 = { allocate<typename promote<double, 2>::type, nullptr>(__ret1073741824_extents), __ret1073741824_extents };

    // [omp] requested but emitted serial: BLADE_OMP_THREADS=1 -- this build emits no thread-level OpenMP (the omp licence in source is unchanged; omp simd is unaffected)
    for (size_t __i0 = 0; __i0 < 31; __i0++) {
        int64_t i = __i0;
        double* BLADE_RESTRICT __orow___ret1073741824 = __ret1073741824[__i0];
        BLADE_IVDEP
        for (size_t __i1 = 0; __i1 < 41; __i1++) {
            int64_t j = __i1;
            __orow___ret1073741824[__i1] = gen(((static_cast<int64_t>(i) * __v17) + static_cast<int64_t>(j)), 10000019L, 0.0, 1.0);
        }
    }
    return __ret1073741824;
}

double kernel(Array<double, 2> u) {
    BLADE_FRAME("kernel", nullptr, 0);
    if ((int64_t)(u.extents[0]) != 31LL) {
        std::cerr << "Blade runtime: the halo window over 'u' declares an inner extent of 31, but 'u' has runtime extent " << u.extents[0] << " on index slot 0 -- the window walk would read out of bounds (oversized halo) or silently emit fewer windows (undersized)" << std::endl;
        blade_rt::panic("BL8009", "halo extent mismatch", "C:\\Users\\cdupu\\Documents\\GitHub\\Blade\\.claude\\worktrees\\bench-fortran\\bench\\results\\2026-10-19-LAPTOP-350H6LDB\\session-135344\\build\\stencil\\smoke\\serial-licensed-native\\blade-cpp\\src\\stencil.blade", 32);
    }
    if ((int64_t)(u.extents[1]) != 41LL) {
        std::cerr << "Blade runtime: the halo window over 'u' declares an inner extent of 41, but 'u' has runtime extent " << u.extents[1] << " on index slot 1 -- the window walk would read out of bounds (oversized halo) or silently emit fewer windows (undersized)" << std::endl;
        blade_rt::panic("BL8009", "halo extent mismatch", "C:\\Users\\cdupu\\Documents\\GitHub\\Blade\\.claude\\worktrees\\bench-fortran\\bench\\results\\2026-10-19-LAPTOP-350H6LDB\\session-135344\\build\\stencil\\smoke\\serial-licensed-native\\blade-cpp\\src\\stencil.blade", 32);
    }

    static constexpr const size_t __v39_extents[2] = { 29, 39 };

    Array<double, 2> __v39 = { allocate<typename promote<double, 2>::type, nullptr>(__v39_extents), __v39_extents };

    // hot-row fold: `__hr___v48` holds the row fold of each row of `__v39`, computed right after the row is produced (Blade.Optimize.planHotRowFolds)
    static constexpr const size_t __hr___v48_extents[1] = { 29 };
    Array<double, 1> __hr___v48 = { allocate<typename promote<double, 1>::type, nullptr>(__hr___v48_extents), __hr___v48_extents };
    // [omp] requested but emitted serial: BLADE_OMP_THREADS=1 -- this build emits no thread-level OpenMP (the omp licence in source is unchanged; omp simd is unaffected)
    for (size_t __i0 = 0; __i0 < 29; __i0++) {
        int64_t wr = (__i0 + 1L);
        double* BLADE_RESTRICT __orow___v39 = __v39[__i0];
        BLADE_IVDEP
        for (size_t __i1 = 0; __i1 < 39; __i1++) {
            int64_t wc = (__i1 + 1L);
            __orow___v39[__i1] = ((((u[(wr + -(1L))][(wc + 0L)] + u[(wr + 1L)][(wc + 0L)]) + u[(wr + 0L)][(wc + -(1L))]) + u[(wr + 0L)][(wc + 1L)]) - (4.0 * u[(wr + 0L)][(wc + 0L)]));
        }
        {
            Array<double, 1> __v39__hr__i0 = { __orow___v39, __v39.extents + 1 };
            __hr___v48[__i0] = blade_fold::sq<double, 8>(__v39__hr__i0.data, __v39__hr__i0.extents[0]);
        }
    }
    // hot-row fold: the row values of `__v39`'s row map were computed behind each produced row into __hr___v48 (Blade.Optimize.planHotRowFolds); this is the outer combine over them, same order, same seed
    // reduce: comm-licensed reduction (builtin '+'), flat sweep
    // [omp] requested but emitted serial: BLADE_OMP_THREADS=1 -- this build emits no thread-level OpenMP (the omp licence in source is unchanged; omp simd is unaffected)
    const size_t __rn___v48 = 29;
    double __v48 = 0.0;
    BLADE_OMP_SIMD_REDUCTION(+:__v48)
    for (size_t __ri = 0; __ri < __rn___v48; __ri++) {
        __v48 = __v48 + __hr___v48[__ri];
    }
    auto __retv1073741825 = __v48;
    deallocate<typename promote<double, 2>::type, nullptr>(__v39.data, __v39.extents);
    deallocate<typename promote<double, 1>::type, nullptr>(__hr___v48.data, __hr___v48.extents);
    return __retv1073741825;
}

double __lambda_62(Y i, X j, int64_t& nx) {
    return gen(((static_cast<int64_t>(i) * nx) + static_cast<int64_t>(j)), 10000019L, 0.0, 1.0);
}

double __lambda_63(int64_t wr, int64_t wc, Array<double, 2>& u) {
    return ((((u[(wr + -(1L))][(wc + 0L)] + u[(wr + 1L)][(wc + 0L)]) + u[(wr + 0L)][(wc + -(1L))]) + u[(wr + 0L)][(wc + 1L)]) - (4.0 * u[(wr + 0L)][(wc + 0L)]));
}

double __lambda_64(Array<double, 1> row) {
    BLADE_FRAME("lambda in s", nullptr, 0);
    return blade_fold::sq<double, 8>(row.data, row.extents[0]);
}

double __lambda_65(double p, double q) {
    return (p + q);
}

double __lambda_68(double a, double b) {
    return (a + b);
}

double __lambda_69(Array<double, 1> __pfrow55) {
    BLADE_FRAME("lambda in _(checksum,input_checksum)", nullptr, 0);
    return [&]() { if (__pfrow55.extents[0] == 0) { blade_rt::panic("BL8003", "reduce: empty array, no reduction possible", nullptr, 0); } auto __wrap_68 = [&](double a, double b) { return __lambda_68(a, b); }; const size_t __rn = __pfrow55.extents[0]; double __r = __pfrow55[0]; const size_t __rlo = 1; const size_t __rcnt = (__rn > __rlo) ? (__rn - __rlo) : (size_t)0; if (__rcnt < (size_t)8) { for (size_t __ri = __rlo; __ri < __rn; __ri++) { __r = __wrap_68(__r, __pfrow55[__ri]); } return __r; } double __rlane0 = __pfrow55[__rlo + 0]; double __rlane1 = __pfrow55[__rlo + 1]; double __rlane2 = __pfrow55[__rlo + 2]; double __rlane3 = __pfrow55[__rlo + 3]; double __rlane4 = __pfrow55[__rlo + 4]; double __rlane5 = __pfrow55[__rlo + 5]; double __rlane6 = __pfrow55[__rlo + 6]; double __rlane7 = __pfrow55[__rlo + 7]; size_t __ri = __rlo + 8; for (; __ri + 8 <= __rn; __ri += 8) {     __rlane0 = __wrap_68(__rlane0, __pfrow55[__ri + 0]);     __rlane1 = __wrap_68(__rlane1, __pfrow55[__ri + 1]);     __rlane2 = __wrap_68(__rlane2, __pfrow55[__ri + 2]);     __rlane3 = __wrap_68(__rlane3, __pfrow55[__ri + 3]);     __rlane4 = __wrap_68(__rlane4, __pfrow55[__ri + 4]);     __rlane5 = __wrap_68(__rlane5, __pfrow55[__ri + 5]);     __rlane6 = __wrap_68(__rlane6, __pfrow55[__ri + 6]);     __rlane7 = __wrap_68(__rlane7, __pfrow55[__ri + 7]); } if (__ri < __rn) { __rlane0 = __wrap_68(__rlane0, __pfrow55[__ri]); __ri++; } if (__ri < __rn) { __rlane1 = __wrap_68(__rlane1, __pfrow55[__ri]); __ri++; } if (__ri < __rn) { __rlane2 = __wrap_68(__rlane2, __pfrow55[__ri]); __ri++; } if (__ri < __rn) { __rlane3 = __wrap_68(__rlane3, __pfrow55[__ri]); __ri++; } if (__ri < __rn) { __rlane4 = __wrap_68(__rlane4, __pfrow55[__ri]); __ri++; } if (__ri < __rn) { __rlane5 = __wrap_68(__rlane5, __pfrow55[__ri]); __ri++; } if (__ri < __rn) { __rlane6 = __wrap_68(__rlane6, __pfrow55[__ri]); __ri++; } __rlane0 = __wrap_68(__rlane0, __rlane1); __rlane0 = __wrap_68(__rlane0, __rlane2); __rlane0 = __wrap_68(__rlane0, __rlane3); __rlane0 = __wrap_68(__rlane0, __rlane4); __rlane0 = __wrap_68(__rlane0, __rlane5); __rlane0 = __wrap_68(__rlane0, __rlane6); __rlane0 = __wrap_68(__rlane0, __rlane7); __r = __wrap_68(__r, __rlane0); return __r; }();
}

double __lambda_72(double a, double b) {
    return (a + b);
}

int main() {
    cout << std::setprecision(15);
    cout << std::boolalpha;
    blade_rt::typed_float_print(cout);
    auto __blade_t0 = TIME;

    try {
    Array<double, 2> __v50 = inputs();
    auto __wrap_72___v73 = [&](double a, double b) { return __lambda_72(a, b); };
    double __v73 = 0.0;
    /* reduce over computation: accumulator loop (omp simd reduction, licensed reorder, 1 operand stream) */
    {
        const size_t __rhi = 31;
        BLADE_OMP_SIMD_REDUCTION(+:__v73)
        for (size_t __i0 = 0; __i0 < __rhi; __i0++) {
            Array<double, 1> __v50____i0 = { __v50.data[__i0], __v50.extents + 1 };
            __v73 = __v73 + [&]() { if (__v50____i0.extents[0] == 0) { blade_rt::panic("BL8003", "reduce: empty array, no reduction possible", nullptr, 0); } auto __wrap_68 = [&](double a, double b) { return __lambda_68(a, b); }; const size_t __rn = __v50____i0.extents[0]; double __r = __v50____i0[0]; const size_t __rlo = 1; const size_t __rcnt = (__rn > __rlo) ? (__rn - __rlo) : (size_t)0; if (__rcnt < (size_t)8) { for (size_t __ri = __rlo; __ri < __rn; __ri++) { __r = __wrap_68(__r, __v50____i0[__ri]); } return __r; } double __rlane0 = __v50____i0[__rlo + 0]; double __rlane1 = __v50____i0[__rlo + 1]; double __rlane2 = __v50____i0[__rlo + 2]; double __rlane3 = __v50____i0[__rlo + 3]; double __rlane4 = __v50____i0[__rlo + 4]; double __rlane5 = __v50____i0[__rlo + 5]; double __rlane6 = __v50____i0[__rlo + 6]; double __rlane7 = __v50____i0[__rlo + 7]; size_t __ri = __rlo + 8; for (; __ri + 8 <= __rn; __ri += 8) {     __rlane0 = __wrap_68(__rlane0, __v50____i0[__ri + 0]);     __rlane1 = __wrap_68(__rlane1, __v50____i0[__ri + 1]);     __rlane2 = __wrap_68(__rlane2, __v50____i0[__ri + 2]);     __rlane3 = __wrap_68(__rlane3, __v50____i0[__ri + 3]);     __rlane4 = __wrap_68(__rlane4, __v50____i0[__ri + 4]);     __rlane5 = __wrap_68(__rlane5, __v50____i0[__ri + 5]);     __rlane6 = __wrap_68(__rlane6, __v50____i0[__ri + 6]);     __rlane7 = __wrap_68(__rlane7, __v50____i0[__ri + 7]); } if (__ri < __rn) { __rlane0 = __wrap_68(__rlane0, __v50____i0[__ri]); __ri++; } if (__ri < __rn) { __rlane1 = __wrap_68(__rlane1, __v50____i0[__ri]); __ri++; } if (__ri < __rn) { __rlane2 = __wrap_68(__rlane2, __v50____i0[__ri]); __ri++; } if (__ri < __rn) { __rlane3 = __wrap_68(__rlane3, __v50____i0[__ri]); __ri++; } if (__ri < __rn) { __rlane4 = __wrap_68(__rlane4, __v50____i0[__ri]); __ri++; } if (__ri < __rn) { __rlane5 = __wrap_68(__rlane5, __v50____i0[__ri]); __ri++; } if (__ri < __rn) { __rlane6 = __wrap_68(__rlane6, __v50____i0[__ri]); __ri++; } __rlane0 = __wrap_68(__rlane0, __rlane1); __rlane0 = __wrap_68(__rlane0, __rlane2); __rlane0 = __wrap_68(__rlane0, __rlane3); __rlane0 = __wrap_68(__rlane0, __rlane4); __rlane0 = __wrap_68(__rlane0, __rlane5); __rlane0 = __wrap_68(__rlane0, __rlane6); __rlane0 = __wrap_68(__rlane0, __rlane7); __r = __wrap_68(__r, __rlane0); return __r; }();
        }
    }
    std::tuple<double, double> __tup_59 = std::make_tuple(kernel(__v50), __v73);
    
    double checksum = std::get<0>(__tup_59);
    
    double input_checksum = std::get<1>(__tup_59);
    

    auto __blade_t1 = TIME;
    double __blade_elapsed = 1e-9 * TIME_DIFF;
    cout << "stencil completed in " << __blade_elapsed << "s" << endl;

    // Print results for verification
    cout << "checksum = " << checksum << endl;
    cout << "input_checksum = " << input_checksum << endl;

    return 0;
    } catch (const std::exception& e) { blade_rt::panic_exception(e); }
      catch (...) { blade_rt::panic("BL8005", "unknown exception", nullptr, 0); }
}