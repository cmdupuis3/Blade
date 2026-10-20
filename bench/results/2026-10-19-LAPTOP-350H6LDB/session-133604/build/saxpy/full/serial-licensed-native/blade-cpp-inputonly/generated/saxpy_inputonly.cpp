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

using Ix = int64_t;


// Run record (BLADE_RUN_RECORD): the input manifest this program was compiled against,
// written with what the run observed when the process exits (blade_run_record.hpp).
static const blade_rr::Input __blade_rr_inputs[] = {
    { nullptr, nullptr, nullptr, nullptr, nullptr, nullptr, nullptr, nullptr, nullptr, nullptr }
};
static blade_rr::AtExit __blade_rr_at_exit{ "saxpy_inputonly", "0.20.0", false, __blade_rr_inputs, 0, nullptr };



double gen(int64_t k, int64_t seed, double lo, double hi);
Array<double, 1> inputs(int64_t seed);
double __lambda_25(Ix i, int64_t& seed);
double __lambda_28(double a, double b);
double __lambda_31(double a, double b);

double gen(int64_t k, int64_t seed, double lo, double hi) {
    int64_t __v6 = ((((k + seed) * 1103515245L) + 12345L) % 2147483648L);
    int64_t __v7 = ((__v6 * ((__v6 / 4096L) + 1L)) % 2147483648L);
    int64_t __v8 = (((__v7 * 1103515245L) + 12345L) % 2147483648L);
    int64_t __v9 = (((__v8 * __v8) / 65536L) % 2147483648L);
    int64_t __v10 = (((__v9 * 1103515245L) + 12345L) % 2147483648L);
    return (lo + ((hi - lo) * (blade_arith::i64_to_f64(__v10) / 2147483648.0)));
}

Array<double, 1> inputs(int64_t seed) {
    BLADE_FRAME("inputs", nullptr, 0);

    static constexpr const size_t __ret1073741824_extents[1] = { 2000003 };

    Array<double, 1> __ret1073741824 = { allocate<typename promote<double, 1>::type, nullptr>(__ret1073741824_extents), __ret1073741824_extents };

    // [omp] requested but emitted serial: BLADE_OMP_THREADS=1 -- this build emits no thread-level OpenMP (the omp licence in source is unchanged; omp simd is unaffected)
    for (size_t __i0 = 0; __i0 < 2000003; __i0++) {
        int64_t i = __i0;
        __ret1073741824[__i0] = gen(static_cast<int64_t>(i), seed, 0.0, 1.0);
    }
    return __ret1073741824;
}

double __lambda_25(Ix i, int64_t& seed) {
    return gen(static_cast<int64_t>(i), seed, 0.0, 1.0);
}

double __lambda_28(double a, double b) {
    return (a + b);
}

double __lambda_31(double a, double b) {
    return (a + b);
}

int main() {
    cout << std::setprecision(15);
    cout << std::boolalpha;
    blade_rt::typed_float_print(cout);
    auto __blade_t0 = TIME;

    try {
    Array<double, 1> __v20 = inputs(10000019L);
    Array<double, 1> __v21 = inputs(20000038L);
    std::tuple<double, double> __tup_22 = std::make_tuple(0.0, ([&]() { auto __wrap_28 = [&](double a, double b) { return __lambda_28(a, b); }; const size_t __rn = 2000003; double __r = __v20[0]; const size_t __rlo = 1; const size_t __rcnt = (__rn > __rlo) ? (__rn - __rlo) : (size_t)0; if (__rcnt < (size_t)8) { for (size_t __ri = __rlo; __ri < __rn; __ri++) { __r = __wrap_28(__r, __v20[__ri]); } return __r; } double __rlane0 = __v20[__rlo + 0]; double __rlane1 = __v20[__rlo + 1]; double __rlane2 = __v20[__rlo + 2]; double __rlane3 = __v20[__rlo + 3]; double __rlane4 = __v20[__rlo + 4]; double __rlane5 = __v20[__rlo + 5]; double __rlane6 = __v20[__rlo + 6]; double __rlane7 = __v20[__rlo + 7]; size_t __ri = __rlo + 8; for (; __ri + 8 <= __rn; __ri += 8) {     __rlane0 = __wrap_28(__rlane0, __v20[__ri + 0]);     __rlane1 = __wrap_28(__rlane1, __v20[__ri + 1]);     __rlane2 = __wrap_28(__rlane2, __v20[__ri + 2]);     __rlane3 = __wrap_28(__rlane3, __v20[__ri + 3]);     __rlane4 = __wrap_28(__rlane4, __v20[__ri + 4]);     __rlane5 = __wrap_28(__rlane5, __v20[__ri + 5]);     __rlane6 = __wrap_28(__rlane6, __v20[__ri + 6]);     __rlane7 = __wrap_28(__rlane7, __v20[__ri + 7]); } if (__ri < __rn) { __rlane0 = __wrap_28(__rlane0, __v20[__ri]); __ri++; } if (__ri < __rn) { __rlane1 = __wrap_28(__rlane1, __v20[__ri]); __ri++; } if (__ri < __rn) { __rlane2 = __wrap_28(__rlane2, __v20[__ri]); __ri++; } if (__ri < __rn) { __rlane3 = __wrap_28(__rlane3, __v20[__ri]); __ri++; } if (__ri < __rn) { __rlane4 = __wrap_28(__rlane4, __v20[__ri]); __ri++; } if (__ri < __rn) { __rlane5 = __wrap_28(__rlane5, __v20[__ri]); __ri++; } if (__ri < __rn) { __rlane6 = __wrap_28(__rlane6, __v20[__ri]); __ri++; } __rlane0 = __wrap_28(__rlane0, __rlane1); __rlane0 = __wrap_28(__rlane0, __rlane2); __rlane0 = __wrap_28(__rlane0, __rlane3); __rlane0 = __wrap_28(__rlane0, __rlane4); __rlane0 = __wrap_28(__rlane0, __rlane5); __rlane0 = __wrap_28(__rlane0, __rlane6); __rlane0 = __wrap_28(__rlane0, __rlane7); __r = __wrap_28(__r, __rlane0); return __r; }() + [&]() { auto __wrap_31 = [&](double a, double b) { return __lambda_31(a, b); }; const size_t __rn = 2000003; double __r = __v21[0]; const size_t __rlo = 1; const size_t __rcnt = (__rn > __rlo) ? (__rn - __rlo) : (size_t)0; if (__rcnt < (size_t)8) { for (size_t __ri = __rlo; __ri < __rn; __ri++) { __r = __wrap_31(__r, __v21[__ri]); } return __r; } double __rlane0 = __v21[__rlo + 0]; double __rlane1 = __v21[__rlo + 1]; double __rlane2 = __v21[__rlo + 2]; double __rlane3 = __v21[__rlo + 3]; double __rlane4 = __v21[__rlo + 4]; double __rlane5 = __v21[__rlo + 5]; double __rlane6 = __v21[__rlo + 6]; double __rlane7 = __v21[__rlo + 7]; size_t __ri = __rlo + 8; for (; __ri + 8 <= __rn; __ri += 8) {     __rlane0 = __wrap_31(__rlane0, __v21[__ri + 0]);     __rlane1 = __wrap_31(__rlane1, __v21[__ri + 1]);     __rlane2 = __wrap_31(__rlane2, __v21[__ri + 2]);     __rlane3 = __wrap_31(__rlane3, __v21[__ri + 3]);     __rlane4 = __wrap_31(__rlane4, __v21[__ri + 4]);     __rlane5 = __wrap_31(__rlane5, __v21[__ri + 5]);     __rlane6 = __wrap_31(__rlane6, __v21[__ri + 6]);     __rlane7 = __wrap_31(__rlane7, __v21[__ri + 7]); } if (__ri < __rn) { __rlane0 = __wrap_31(__rlane0, __v21[__ri]); __ri++; } if (__ri < __rn) { __rlane1 = __wrap_31(__rlane1, __v21[__ri]); __ri++; } if (__ri < __rn) { __rlane2 = __wrap_31(__rlane2, __v21[__ri]); __ri++; } if (__ri < __rn) { __rlane3 = __wrap_31(__rlane3, __v21[__ri]); __ri++; } if (__ri < __rn) { __rlane4 = __wrap_31(__rlane4, __v21[__ri]); __ri++; } if (__ri < __rn) { __rlane5 = __wrap_31(__rlane5, __v21[__ri]); __ri++; } if (__ri < __rn) { __rlane6 = __wrap_31(__rlane6, __v21[__ri]); __ri++; } __rlane0 = __wrap_31(__rlane0, __rlane1); __rlane0 = __wrap_31(__rlane0, __rlane2); __rlane0 = __wrap_31(__rlane0, __rlane3); __rlane0 = __wrap_31(__rlane0, __rlane4); __rlane0 = __wrap_31(__rlane0, __rlane5); __rlane0 = __wrap_31(__rlane0, __rlane6); __rlane0 = __wrap_31(__rlane0, __rlane7); __r = __wrap_31(__r, __rlane0); return __r; }()));
    
    double checksum = std::get<0>(__tup_22);
    
    double input_checksum = std::get<1>(__tup_22);
    

    auto __blade_t1 = TIME;
    double __blade_elapsed = 1e-9 * TIME_DIFF;
    cout << "saxpy_inputonly completed in " << __blade_elapsed << "s" << endl;

    // Print results for verification
    cout << "checksum = " << checksum << endl;
    cout << "input_checksum = " << input_checksum << endl;

    return 0;
    } catch (const std::exception& e) { blade_rt::panic_exception(e); }
      catch (...) { blade_rt::panic("BL8005", "unknown exception", nullptr, 0); }
}