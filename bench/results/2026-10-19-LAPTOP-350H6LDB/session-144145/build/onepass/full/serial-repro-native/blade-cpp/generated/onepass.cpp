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
static blade_rr::AtExit __blade_rr_at_exit{ "onepass", "0.20.0", false, __blade_rr_inputs, 0, nullptr };



double gen(int64_t k, int64_t seed, double lo, double hi);
Array<double, 1> inputs();
double kernel(Array<double, 1> x);
double __lambda_49(Ix i);
double __lambda_50(double v);
double __lambda_51(double v);
double __lambda_52(double v);
double __lambda_53(double v);
double __lambda_56(double a, double b);
double __lambda_59(double a, double b);

double gen(int64_t k, int64_t seed, double lo, double hi) {
    int64_t __v6 = ((((k + seed) * 1103515245L) + 12345L) % 2147483648L);
    int64_t __v7 = ((__v6 * ((__v6 / 4096L) + 1L)) % 2147483648L);
    int64_t __v8 = (((__v7 * 1103515245L) + 12345L) % 2147483648L);
    int64_t __v9 = (((__v8 * __v8) / 65536L) % 2147483648L);
    int64_t __v10 = (((__v9 * 1103515245L) + 12345L) % 2147483648L);
    return (lo + ((hi - lo) * (blade_arith::i64_to_f64(__v10) / 2147483648.0)));
}

Array<double, 1> inputs() {
    BLADE_FRAME("inputs", nullptr, 0);

    static constexpr const size_t __ret1073741824_extents[1] = { 5000011 };

    Array<double, 1> __ret1073741824 = { allocate<typename promote<double, 1>::type, nullptr>(__ret1073741824_extents), __ret1073741824_extents };

    // [omp] requested but emitted serial: BLADE_OMP_THREADS=1 -- this build emits no thread-level OpenMP (the omp licence in source is unchanged; omp simd is unaffected)
    for (size_t __i0 = 0; __i0 < 5000011; __i0++) {
        int64_t i = __i0;
        __ret1073741824[__i0] = gen(static_cast<int64_t>(i), 10000019L, 0.0, 2.0);
    }
    return __ret1073741824;
}

double kernel(Array<double, 1> x) {
    BLADE_FRAME("kernel", nullptr, 0);
    auto __wrap_56___v39 = [&](double a, double b) { return __lambda_56(a, b); };
    double __v39_0 = 0.0;
    double __v39_1 = 0.0;
    double __v39_2 = 0.0;
    double __v39_3 = 0.0;
    for (size_t __i0 = 0; __i0 < 5000011; __i0++) {
        double x____i0 = x[__i0];
        __v39_0 = __wrap_56___v39(__v39_0, x____i0);
        __v39_1 = __wrap_56___v39(__v39_1, (x____i0 * x____i0));
        __v39_2 = __wrap_56___v39(__v39_2, ((x____i0 * x____i0) * x____i0));
        __v39_3 = __wrap_56___v39(__v39_3, (((x____i0 * x____i0) * x____i0) * x____i0));
    }
    auto __v39 = std::make_tuple(__v39_0, __v39_1, __v39_2, __v39_3);
    auto& __v40 = __v39_0;
    auto& __v41 = __v39_1;
    auto& __v42 = __v39_2;
    auto& __v43 = __v39_3;
    return (((__v40 + (2.0 * __v41)) + (3.0 * __v42)) + (4.0 * __v43));
}

double __lambda_49(Ix i) {
    return gen(static_cast<int64_t>(i), 10000019L, 0.0, 2.0);
}

double __lambda_50(double v) {
    return v;
}

double __lambda_51(double v) {
    return (v * v);
}

double __lambda_52(double v) {
    return ((v * v) * v);
}

double __lambda_53(double v) {
    return (((v * v) * v) * v);
}

double __lambda_56(double a, double b) {
    return (a + b);
}

double __lambda_59(double a, double b) {
    return (a + b);
}

int main() {
    cout << std::setprecision(15);
    cout << std::boolalpha;
    blade_rt::typed_float_print(cout);
    auto __blade_t0 = TIME;

    try {
    Array<double, 1> __v45 = inputs();
    std::tuple<double, double> __tup_46 = std::make_tuple(kernel(__v45), [&]() { auto __wrap_59 = [&](double a, double b) { return __lambda_59(a, b); }; double __r = __v45[0]; for (size_t __ri = 1; __ri < 5000011; __ri++) { __r = __wrap_59(__r, __v45[__ri]); } return __r; }());
    
    double checksum = std::get<0>(__tup_46);
    
    double input_checksum = std::get<1>(__tup_46);
    

    auto __blade_t1 = TIME;
    double __blade_elapsed = 1e-9 * TIME_DIFF;
    cout << "onepass completed in " << __blade_elapsed << "s" << endl;

    // Print results for verification
    cout << "checksum = " << checksum << endl;
    cout << "input_checksum = " << input_checksum << endl;

    return 0;
    } catch (const std::exception& e) { blade_rt::panic_exception(e); }
      catch (...) { blade_rt::panic("BL8005", "unknown exception", nullptr, 0); }
}