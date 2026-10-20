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

#include "blade_packed_gemm.hpp"
using Var = int64_t;

using Obs = int64_t;


// Run record (BLADE_RUN_RECORD): the input manifest this program was compiled against,
// written with what the run observed when the process exits (blade_run_record.hpp).
static const blade_rr::Input __blade_rr_inputs[] = {
    { nullptr, nullptr, nullptr, nullptr, nullptr, nullptr, nullptr, nullptr, nullptr, nullptr }
};
static blade_rr::AtExit __blade_rr_at_exit{ "comoment3", "0.20.0", false, __blade_rr_inputs, 0, nullptr };

static constexpr const size_t __v29_symm[3] = {1, 1, 1};


double lcg(int64_t k, int64_t seed);
double __lambda_102(Var i, Obs s, int64_t& n);
double __lambda_103(Array<double, 1> a, Array<double, 1> b, Array<double, 1> c, int64_t& n);
double __lambda_104(Var k, Var& i, Var& j, Array<double, 3>& t);
double __lambda_107(double a, double b);
double __lambda_108(Var j, Var& i, Array<double, 3>& t);
double __lambda_111(double a, double b);
double __lambda_112(Var i, Array<double, 3>& t);
double __lambda_113(double u, double v);
double __lambda_116(double a, double b);
double __lambda_117(Array<double, 1> __pfrow93);
double __lambda_118(double u, double v);

double lcg(int64_t k, int64_t seed) {
    int64_t __v7 = ((((k + seed) * 1103515245L) + 12345L) % 2147483648L);
    int64_t __v8 = ((__v7 * ((__v7 / 4096L) + 1L)) % 2147483648L);
    int64_t __v9 = (((__v8 * 1103515245L) + 12345L) % 2147483648L);
    int64_t __v10 = (((__v9 * __v9) / 65536L) % 2147483648L);
    int64_t __v11 = (((__v10 * 1103515245L) + 12345L) % 2147483648L);
    return (blade_arith::i64_to_f64(__v11) / 2147483648.0);
}

double __lambda_102(Var i, Obs s, int64_t& n) {
    return lcg(((i * n) + s), 10000019L);
}

double __lambda_103(Array<double, 1> a, Array<double, 1> b, Array<double, 1> c, int64_t& n) {
    return ([&]() { double __ps = 0; for (size_t __pt = 0; __pt < 201; __pt++) { __ps += a[__pt] * b[__pt] * c[__pt]; } return __ps; }() / blade_arith::i64_to_f64(n));
}

double __lambda_104(Var k, Var& i, Var& j, Array<double, 3>& t) {
    BLADE_FRAME("lambda in lambda in lambda in ss", nullptr, 0);
    return ([&]() { const auto __bo = ([&]() -> double { std::array<size_t,3> __g0 = { (size_t)(i), (size_t)(j), (size_t)(k) }; bool __z0; int __p0 = nested_array_utilities::canon_fold<3>(__g0, false, __z0); if (__z0) return double(); auto __c0 = nested_array_utilities::canon_left_justify<3>(__g0, false); double __v = t[__c0[0]][__c0[1]][__c0[2]]; double __tv0 = nested_array_utilities::canon_transform<double>(__v, __p0, nested_array_utilities::ReadTransform::Identity); return __tv0; }()); return (__bo * __bo); }());
}

double __lambda_107(double a, double b) {
    return (a + b);
}

double __lambda_108(Var j, Var& i, Array<double, 3>& t) {
    BLADE_FRAME("lambda in lambda in ss", nullptr, 0);
    auto __wrap_107___ret1073741824 = [&](double a, double b) { return __lambda_107(a, b); };
    double __ret1073741824 = 0.0;
    for (size_t __i0 = 0; __i0 < 171; __i0++) {
        int64_t k = __i0;
        __ret1073741824 = __wrap_107___ret1073741824(__ret1073741824, ([&]() { const auto __bo = ([&]() -> double { std::array<size_t,3> __g0 = { (size_t)(i), (size_t)(j), (size_t)(k) }; bool __z0; int __p0 = nested_array_utilities::canon_fold<3>(__g0, false, __z0); if (__z0) return double(); auto __c0 = nested_array_utilities::canon_left_justify<3>(__g0, false); double __v = t[__c0[0]][__c0[1]][__c0[2]]; double __tv0 = nested_array_utilities::canon_transform<double>(__v, __p0, nested_array_utilities::ReadTransform::Identity); return __tv0; }()); return (__bo * __bo); }()));
    }
    return __ret1073741824;
}

double __lambda_111(double a, double b) {
    return (a + b);
}

double __lambda_112(Var i, Array<double, 3>& t) {
    BLADE_FRAME("lambda in ss", nullptr, 0);
    auto __wrap_111___ret1073741826 = [&](double a, double b) { return __lambda_111(a, b); };
    double __ret1073741826 = 0.0;
    for (size_t __i0 = 0; __i0 < 171; __i0++) {
        int64_t j = __i0;
        __ret1073741826 = __wrap_111___ret1073741826(__ret1073741826, __lambda_108(j, i, t));
    }
    return __ret1073741826;
}

double __lambda_113(double u, double v) {
    return (u + v);
}

double __lambda_116(double a, double b) {
    return (a + b);
}

double __lambda_117(Array<double, 1> __pfrow93) {
    BLADE_FRAME("lambda in _(checksum,input_checksum)", nullptr, 0);
    return [&]() { if (__pfrow93.extents[0] == 0) { blade_rt::panic("BL8003", "reduce: empty array, no reduction possible", nullptr, 0); } auto __wrap_116 = [&](double a, double b) { return __lambda_116(a, b); }; double __r = __pfrow93[0]; for (size_t __ri = 1; __ri < __pfrow93.extents[0]; __ri++) { __r = __wrap_116(__r, __pfrow93[__ri]); } return __r; }();
}

double __lambda_118(double u, double v) {
    return (u + v);
}

int main() {
    cout << std::setprecision(15);
    cout << std::boolalpha;
    blade_rt::typed_float_print(cout);
    auto __blade_t0 = TIME;

    try {
    int64_t p = 171L;
    
    int64_t n = 201L;
    
    
    static constexpr const size_t __v21_extents[2] = { 171, 201 };
    
    Array<double, 2> __v21 = { allocate<typename promote<double, 2>::type, nullptr>(__v21_extents), __v21_extents };
    
    BLADE_OMP_PARALLEL_FOR
for (size_t __i0 = 0; __i0 < 171; __i0++) {
        int64_t i = __i0;
        double* BLADE_RESTRICT __orow___v21 = __v21[__i0];
        BLADE_IVDEP
    for (size_t __i1 = 0; __i1 < 201; __i1++) {
            int64_t s = __i1;
            __orow___v21[__i1] = lcg(((i * n) + s), 10000019L);
        }
    }
    
    static constexpr const size_t __v29_extents[3] = { 171, 171, 171 };
    
    Array<double, 3> __v29 = { allocate<typename promote<double, 3>::type, __v29_symm>(__v29_extents), __v29_extents };
    
    /* comm nest: rank-3 simplex tile (blade_packed_gemm.hpp dsym3_tri, W panel + 6x8, KC=201), per cell the serial prodsum */ blade_pgemm::dsym3_tri((size_t)(171), (size_t)(201), __v21.data, nested_array_utilities::pool_base(__v29.data), 1, (double)(blade_arith::i64_to_f64(n)), true, (size_t)336);
    auto __wrap_113___v88 = [&](double u, double v) { return __lambda_113(u, v); };
    double __v88 = 0.0;
    // [omp] requested but emitted serial: BLADE_FP_REPRO=1 -- this build keeps the serial association of every fold (the reorder licence in source is unchanged)
    for (size_t __i0 = 0; __i0 < 171; __i0++) {
        int64_t i = __i0;
        __v88 = __wrap_113___v88(__v88, __lambda_112(i, __v29));
    }
    auto __wrap_118___v119 = [&](double u, double v) { return __lambda_118(u, v); };
    double __v119 = 0.0;
    // [omp] requested but emitted serial: BLADE_FP_REPRO=1 -- this build keeps the serial association of every fold (the reorder licence in source is unchanged)
    for (size_t __i0 = 0; __i0 < 171; __i0++) {
        Array<double, 1> __v21____i0 = { __v21.data[__i0], __v21.extents + 1 };
        __v119 = __wrap_118___v119(__v119, [&]() { if (__v21____i0.extents[0] == 0) { blade_rt::panic("BL8003", "reduce: empty array, no reduction possible", nullptr, 0); } auto __wrap_116 = [&](double a, double b) { return __lambda_116(a, b); }; double __r = __v21____i0[0]; for (size_t __ri = 1; __ri < __v21____i0.extents[0]; __ri++) { __r = __wrap_116(__r, __v21____i0[__ri]); } return __r; }());
    }
    std::tuple<double, double> __tup_99 = std::make_tuple(__v88, __v119);
    
    double checksum = std::get<0>(__tup_99);
    
    double input_checksum = std::get<1>(__tup_99);
    

    auto __blade_t1 = TIME;
    double __blade_elapsed = 1e-9 * TIME_DIFF;
    cout << "comoment3 completed in " << __blade_elapsed << "s" << endl;

    // Print results for verification
    cout << "checksum = " << checksum << endl;
    cout << "input_checksum = " << input_checksum << endl;

    return 0;
    } catch (const std::exception& e) { blade_rt::panic_exception(e); }
      catch (...) { blade_rt::panic("BL8005", "unknown exception", nullptr, 0); }
}