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
static blade_rr::AtExit __blade_rr_at_exit{ "maskagg", "0.20.0", false, __blade_rr_inputs, 0, nullptr };



double gen(int64_t k, int64_t seed, double lo, double hi);
double __lambda_31(Ix i);
bool __lambda_32(double v);
double __lambda_33(double __zl, double __zr);
double __lambda_36(double a, double b);
double __lambda_39(double a, double b);

double gen(int64_t k, int64_t seed, double lo, double hi) {
    BLADE_FRAME("gen", nullptr, 0);
    int64_t __v6 = ((((k + seed) * 1103515245L) + 12345L) % 2147483648L);
    int64_t __v7 = ((__v6 * ((__v6 / 4096L) + 1L)) % 2147483648L);
    int64_t __v8 = (((__v7 * 1103515245L) + 12345L) % 2147483648L);
    int64_t __v9 = (((__v8 * __v8) / 65536L) % 2147483648L);
    int64_t __v10 = (((__v9 * 1103515245L) + 12345L) % 2147483648L);
    return (lo + ((hi - lo) * (static_cast<double>(__v10) / 2147483648.0)));
}

double __lambda_31(Ix i) {
    BLADE_FRAME("lambda in x", nullptr, 0);
    return gen(static_cast<int64_t>(i), 10000019L, 0.0, 2.0);
}

bool __lambda_32(double v) {
    return (v > 1.0);
}

double __lambda_33(double __zl, double __zr) {
    return (__zl * __zr);
}

double __lambda_36(double a, double b) {
    return (a + b);
}

double __lambda_39(double a, double b) {
    return (a + b);
}

int main() {
    cout << std::setprecision(15);
    cout << std::boolalpha;
    blade_rt::typed_float_print(cout);
    auto __blade_t0 = TIME;

    try {
    
    static constexpr const size_t x_extents[1] = { 1000003 };
    
    Array<double, 1> x = { allocate<typename promote<double, 1>::type, nullptr>(x_extents), x_extents };
    
    for (size_t __i0 = 0; __i0 < 1000003; __i0++) {
        int64_t i = __i0;
        x[__i0] = gen(static_cast<int64_t>(i), 10000019L, 0.0, 2.0);
    }
    
    // mask: count + compact
    auto __wrap_32_keep = [&](double v) { return __lambda_32(v); };
    static constexpr const size_t keep_extents[1] = { 1000003 };
    Array<bool, 1> keep = { new bool[1000003], keep_extents };
    for (size_t __mi = 0; __mi < 1000003; __mi++) {
        double __keep_x = x[__mi];
        keep[__mi] = __wrap_32_keep(__keep_x);
    }
    
    std::array<size_t, 1> y_idx_extents = { keep.extents[0] };
    size_t y_idx_grid = keep.extents[0];
    bool* y_idx_pool = nested_array_utilities::pool_base(keep.data);
    std::vector<bool> y_idx_maskvec(y_idx_pool, y_idx_pool + y_idx_grid);
    compound_index_t<1>* y_idx = new compound_index_t<1>("y_idx", y_idx_extents, y_idx_maskvec);
    size_t y_trail = 1;
    double* y_densepool = nested_array_utilities::pool_base(x.data);
    double* y_compact = new double[y_idx->cardinality * y_trail + 1];
    { size_t y_r = 0; for (size_t y_c = 0; y_c < y_idx_grid; y_c++) { y_compact[y_r] = y_densepool[y_c]; y_r += (size_t)(!!y_idx_maskvec[y_c]); } }
    nested_array_utilities::Compound<double, 1> y { y_compact, y_idx, y_trail };
    
    
    nested_array_utilities::Compound<double, 1> __v40 = { new double[y.idx->cardinality * y.trailing_stride], y.idx, y.trailing_stride };
    
    for (size_t __i0 = 0; __i0 < y.idx->cardinality; __i0++) {
        double y____i0 = y.data[__i0];
        __v40.data[__i0] = (y____i0 * y____i0);
    }
    double checksum = ([&]() { if ((__v40.idx->cardinality * __v40.trailing_stride) == 0) { blade_rt::panic("BL8003", "reduce: empty array, no reduction possible", "C:\\Users\\cdupu\\Documents\\GitHub\\Blade\\.claude\\worktrees\\bench-fortran\\bench\\results\\2026-10-18-LAPTOP-350H6LDB\\session-130438\\build\\maskagg\\full\\serial-repro-native\\blade-cpp\\src\\maskagg.blade", 28); } auto __wrap_36 = [&](double a, double b) { return __lambda_36(a, b); }; double __r = __v40.data[0]; for (size_t __ri = 1; __ri < (__v40.idx->cardinality * __v40.trailing_stride); __ri++) { __r = __wrap_36(__r, __v40.data[__ri]); } return __r; }() + static_cast<double>((int64_t)(y.idx->cardinality)));
    
    auto __wrap_39_input_checksum = [&](double a, double b) { return __lambda_39(a, b); };
    // reduce: accumulator loop, eager
    double input_checksum = x[0];
    for (size_t __ri = 1; __ri < 1000003; __ri++) {
        input_checksum = __wrap_39_input_checksum(input_checksum, x[__ri]);
    }
    

    auto __blade_t1 = TIME;
    double __blade_elapsed = 1e-9 * TIME_DIFF;
    cout << "maskagg completed in " << __blade_elapsed << "s" << endl;

    // Print results for verification
    cout << "checksum = " << checksum << endl;
    cout << "input_checksum = " << input_checksum << endl;

    return 0;
    } catch (const std::exception& e) { blade_rt::panic_exception(e); }
      catch (...) { blade_rt::panic("BL8005", "unknown exception", nullptr, 0); }
}