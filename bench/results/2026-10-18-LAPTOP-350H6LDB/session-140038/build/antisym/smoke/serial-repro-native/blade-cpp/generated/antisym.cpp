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

using Part = int64_t;

using Comp = int64_t;

using Obs = int64_t;


// Run record (BLADE_RUN_RECORD): the input manifest this program was compiled against,
// written with what the run observed when the process exits (blade_run_record.hpp).
static const blade_rr::Input __blade_rr_inputs[] = {
    { nullptr, nullptr, nullptr, nullptr, nullptr, nullptr, nullptr, nullptr, nullptr, nullptr }
};
static blade_rr::AtExit __blade_rr_at_exit{ "antisym", "0.20.0", false, __blade_rr_inputs, 0, nullptr };

static constexpr const size_t __v34_extents_anti[2] = {1, 1};


double lcg(int64_t k, int64_t seed);
double __lambda_98(Part i, Comp c, Obs s, int64_t& n);
double __lambda_99(Array<double, 2> a, Array<double, 2> b);
double __lambda_100(Part j, Part& i, Array<double, 2>& l);
double __lambda_103(double a, double b);
double __lambda_104(Part i, Array<double, 2>& l);
double __lambda_105(double u, double v);
double __lambda_108(double a, double b);
double __lambda_109(Array<double, 1> __pfrow83);
double __lambda_112(double a, double b);
double __lambda_113(Array<double, 1> __pfrow82);
double __lambda_114(double u, double v);

double lcg(int64_t k, int64_t seed) {
    BLADE_FRAME("lcg", nullptr, 0);
    int64_t __v8 = ((((k + seed) * 1103515245L) + 12345L) % 2147483648L);
    int64_t __v9 = ((__v8 * ((__v8 / 4096L) + 1L)) % 2147483648L);
    int64_t __v10 = (((__v9 * 1103515245L) + 12345L) % 2147483648L);
    int64_t __v11 = (((__v10 * __v10) / 65536L) % 2147483648L);
    int64_t __v12 = (((__v11 * 1103515245L) + 12345L) % 2147483648L);
    return (static_cast<double>(__v12) / 2147483648.0);
}

double __lambda_98(Part i, Comp c, Obs s, int64_t& n) {
    BLADE_FRAME("lambda in z", nullptr, 0);
    return lcg(((i * n) + s), (10000019L * (c + 1L)));
}

double __lambda_99(Array<double, 2> a, Array<double, 2> b) {
    return ([&]() { double __ps = 0; for (size_t __pt = 0; __pt < 11; __pt++) { __ps += Array<double, 1>{ a.data[0L], a.extents + 1 }[__pt] * Array<double, 1>{ b.data[1L], b.extents + 1 }[__pt]; } return __ps; }() - [&]() { double __ps = 0; for (size_t __pt = 0; __pt < 11; __pt++) { __ps += Array<double, 1>{ b.data[0L], b.extents + 1 }[__pt] * Array<double, 1>{ a.data[1L], a.extents + 1 }[__pt]; } return __ps; }());
}

double __lambda_100(Part j, Part& i, Array<double, 2>& l) {
    BLADE_FRAME("lambda in lambda in ss", nullptr, 0);
    return (([&]() -> double { std::array<size_t,2> __g0 = { (size_t)(i), (size_t)(j) }; bool __z0; int __p0 = nested_array_utilities::canon_fold<2>(__g0, true, __z0); if (__z0) return double(); auto __c0 = nested_array_utilities::canon_left_justify<2>(__g0, true); double __v = l[__c0[0]][__c0[1]]; double __tv0 = nested_array_utilities::canon_transform<double>(__v, __p0, nested_array_utilities::ReadTransform::NegateOnSwap); return __tv0; }()) * ([&]() -> double { std::array<size_t,2> __g0 = { (size_t)(i), (size_t)(j) }; bool __z0; int __p0 = nested_array_utilities::canon_fold<2>(__g0, true, __z0); if (__z0) return double(); auto __c0 = nested_array_utilities::canon_left_justify<2>(__g0, true); double __v = l[__c0[0]][__c0[1]]; double __tv0 = nested_array_utilities::canon_transform<double>(__v, __p0, nested_array_utilities::ReadTransform::NegateOnSwap); return __tv0; }()));
}

double __lambda_103(double a, double b) {
    return (a + b);
}

double __lambda_104(Part i, Array<double, 2>& l) {
    BLADE_FRAME("lambda in ss", nullptr, 0);
    auto __wrap_103___ret1073741824 = [&](double a, double b) { return __lambda_103(a, b); };
    double __ret1073741824 = 0.0;
    for (size_t __i0 = 0; __i0 < 23; __i0++) {
        int64_t j = __i0;
        __ret1073741824 = __wrap_103___ret1073741824(__ret1073741824, (([&]() -> double { std::array<size_t,2> __g0 = { (size_t)(i), (size_t)(j) }; bool __z0; int __p0 = nested_array_utilities::canon_fold<2>(__g0, true, __z0); if (__z0) return double(); auto __c0 = nested_array_utilities::canon_left_justify<2>(__g0, true); double __v = l[__c0[0]][__c0[1]]; double __tv0 = nested_array_utilities::canon_transform<double>(__v, __p0, nested_array_utilities::ReadTransform::NegateOnSwap); return __tv0; }()) * ([&]() -> double { std::array<size_t,2> __g0 = { (size_t)(i), (size_t)(j) }; bool __z0; int __p0 = nested_array_utilities::canon_fold<2>(__g0, true, __z0); if (__z0) return double(); auto __c0 = nested_array_utilities::canon_left_justify<2>(__g0, true); double __v = l[__c0[0]][__c0[1]]; double __tv0 = nested_array_utilities::canon_transform<double>(__v, __p0, nested_array_utilities::ReadTransform::NegateOnSwap); return __tv0; }())));
    }
    return __ret1073741824;
}

double __lambda_105(double u, double v) {
    return (u + v);
}

double __lambda_108(double a, double b) {
    return (a + b);
}

double __lambda_109(Array<double, 1> __pfrow83) {
    BLADE_FRAME("lambda in isum", nullptr, 0);
    return [&]() { if (__pfrow83.extents[0] == 0) { blade_rt::panic("BL8003", "reduce: empty array, no reduction possible", nullptr, 0); } auto __wrap_108 = [&](double a, double b) { return __lambda_108(a, b); }; double __r = __pfrow83[0]; for (size_t __ri = 1; __ri < __pfrow83.extents[0]; __ri++) { __r = __wrap_108(__r, __pfrow83[__ri]); } return __r; }();
}

double __lambda_112(double a, double b) {
    return (a + b);
}

double __lambda_113(Array<double, 1> __pfrow82) {
    BLADE_FRAME("lambda in isum", nullptr, 0);
    return [&]() { if (__pfrow82.extents[0] == 0) { blade_rt::panic("BL8003", "reduce: empty array, no reduction possible", nullptr, 0); } auto __wrap_112 = [&](double a, double b) { return __lambda_112(a, b); }; double __r = __pfrow82[0]; for (size_t __ri = 1; __ri < __pfrow82.extents[0]; __ri++) { __r = __wrap_112(__r, __pfrow82[__ri]); } return __r; }();
}

double __lambda_114(double u, double v) {
    return (u + v);
}

int main() {
    cout << std::setprecision(15);
    cout << std::boolalpha;
    blade_rt::typed_float_print(cout);
    auto __blade_t0 = TIME;

    try {
    int64_t p = 23L;
    
    int64_t n = 11L;
    
    
    static constexpr const size_t __v26_extents[3] = { 23, 2, 11 };
    
    Array<double, 3> __v26 = { allocate<typename promote<double, 3>::type, nullptr>(__v26_extents), __v26_extents };
    
    // [omp] requested but emitted serial: BLADE_OMP_THREADS=1 -- this build emits no thread-level OpenMP (the omp licence in source is unchanged; omp simd is unaffected)
    for (size_t __i0 = 0; __i0 < 23; __i0++) {
        int64_t i = __i0;
        for (size_t __i1 = 0; __i1 < 2; __i1++) {
            int64_t c = __i1;
            double* BLADE_RESTRICT __orow___v26 = __v26[__i0][__i1];
            BLADE_IVDEP
        for (size_t __i2 = 0; __i2 < 11; __i2++) {
                int64_t s = __i2;
                __orow___v26[__i2] = lcg(((i * n) + s), (10000019L * (c + 1L)));
            }
        }
    }
    
    static constexpr const size_t __v34_extents[2] = { 23, 23 };
    
    Array<double, 2> __v34 = { allocate<typename promote<double, 2>::type, __v34_extents_anti, false>(__v34_extents), __v34_extents };
    
    double* __opool___v34 = nested_array_utilities::pool_base(__v34.data);
    // [omp] requested but emitted serial: BLADE_OMP_THREADS=1 -- this build emits no thread-level OpenMP (the omp licence in source is unchanged; omp simd is unaffected)
    for (size_t __i0 = 0; __i0 < 23; __i0++) {
        const size_t __oend___v34_0 = 23 - __i0;
        const size_t __ooff___v34_0 = (253) - ((__oend___v34_0 * (__oend___v34_0 - 1)) / 2);
        Array<double, 2> __v26____i0 = { __v26.data[__i0], __v26.extents + 1 };
        double* BLADE_RESTRICT __orow___v34 = __opool___v34 + __ooff___v34_0;
        // [ivdep] declined: kernel body contains an inner loop (prodsum/reduce/contains/nested apply), so this header cannot vectorize
        for (size_t __i1 = 0; __i1 < 23 - __i0 - 1; __i1++) {
            Array<double, 2> __v26____i1 = { __v26.data[__i1 + __i0 + 1], __v26.extents + 1 };
            __orow___v34[__i1] = ([&]() { double __ps = 0; for (size_t __pt = 0; __pt < 11; __pt++) { __ps += Array<double, 1>{ __v26____i0.data[0L], __v26____i0.extents + 1 }[__pt] * Array<double, 1>{ __v26____i1.data[1L], __v26____i1.extents + 1 }[__pt]; } return __ps; }() - [&]() { double __ps = 0; for (size_t __pt = 0; __pt < 11; __pt++) { __ps += Array<double, 1>{ __v26____i1.data[0L], __v26____i1.extents + 1 }[__pt] * Array<double, 1>{ __v26____i0.data[1L], __v26____i0.extents + 1 }[__pt]; } return __ps; }());
        }
    }
    auto __wrap_105___v61 = [&](double u, double v) { return __lambda_105(u, v); };
    double __v61 = 0.0;
    // [omp] requested but emitted serial: BLADE_OMP_THREADS=1 -- this build emits no thread-level OpenMP (the omp licence in source is unchanged; omp simd is unaffected)
    for (size_t __i0 = 0; __i0 < 23; __i0++) {
        int64_t i = __i0;
        __v61 = __wrap_105___v61(__v61, __lambda_104(i, __v34));
    }
    
    static constexpr const size_t __v88_extents[2] = { 23, 2 };
    
    Array<double, 2> __v88 = { allocate<typename promote<double, 2>::type, nullptr>(__v88_extents), __v88_extents };
    
    for (size_t __i0 = 0; __i0 < 23; __i0++) {
        Array<double, 2> __v26____i0 = { __v26.data[__i0], __v26.extents + 1 };
        double* BLADE_RESTRICT __orow___v88 = __v88[__i0];
        // [ivdep] declined: kernel body contains an inner loop (prodsum/reduce/contains/nested apply), so this header cannot vectorize
        for (size_t __i1 = 0; __i1 < 2; __i1++) {
            Array<double, 1> __v26____i0____i1 = { __v26____i0.data[__i1], __v26____i0.extents + 1 };
            __orow___v88[__i1] = [&]() { if (__v26____i0____i1.extents[0] == 0) { blade_rt::panic("BL8003", "reduce: empty array, no reduction possible", nullptr, 0); } auto __wrap_108 = [&](double a, double b) { return __lambda_108(a, b); }; double __r = __v26____i0____i1[0]; for (size_t __ri = 1; __ri < __v26____i0____i1.extents[0]; __ri++) { __r = __wrap_108(__r, __v26____i0____i1[__ri]); } return __r; }();
        }
    }
    
    static constexpr const size_t __v115_extents[1] = { 23 };
    
    Array<double, 1> __v115 = { allocate<typename promote<double, 1>::type, nullptr>(__v115_extents), __v115_extents };
    
    // row-fold jam: reduce over rows, 6 rows per tile, one accumulator per row (bitwise: each row keeps its own ascending fold)
    {
        const size_t __jm = 23;
        const size_t __jn = 2;
        auto __wrap_112 = [&](double a, double b) { return __lambda_112(a, b); };
        size_t __i0 = 0;
        for (; __i0 + 6 <= __jm; __i0 += 6) {
            const double* BLADE_RESTRICT __jr0_0 = &__v88[__i0 + 0][0];
            const double* BLADE_RESTRICT __jr0_1 = &__v88[__i0 + 1][0];
            const double* BLADE_RESTRICT __jr0_2 = &__v88[__i0 + 2][0];
            const double* BLADE_RESTRICT __jr0_3 = &__v88[__i0 + 3][0];
            const double* BLADE_RESTRICT __jr0_4 = &__v88[__i0 + 4][0];
            const double* BLADE_RESTRICT __jr0_5 = &__v88[__i0 + 5][0];
            double __ja0 = __jr0_0[0];
            double __ja1 = __jr0_1[0];
            double __ja2 = __jr0_2[0];
            double __ja3 = __jr0_3[0];
            double __ja4 = __jr0_4[0];
            double __ja5 = __jr0_5[0];
            for (size_t __jk = 1; __jk < __jn; __jk++) {
                __ja0 = __wrap_112(__ja0, __jr0_0[__jk]);
                __ja1 = __wrap_112(__ja1, __jr0_1[__jk]);
                __ja2 = __wrap_112(__ja2, __jr0_2[__jk]);
                __ja3 = __wrap_112(__ja3, __jr0_3[__jk]);
                __ja4 = __wrap_112(__ja4, __jr0_4[__jk]);
                __ja5 = __wrap_112(__ja5, __jr0_5[__jk]);
            }
            __v115[__i0 + 0] = __ja0;
            __v115[__i0 + 1] = __ja1;
            __v115[__i0 + 2] = __ja2;
            __v115[__i0 + 3] = __ja3;
            __v115[__i0 + 4] = __ja4;
            __v115[__i0 + 5] = __ja5;
        }
        for (; __i0 < __jm; __i0++) {
            const double* BLADE_RESTRICT __jr0_0 = &__v88[__i0][0];
            double __ja0 = __jr0_0[0];
            for (size_t __jk = 1; __jk < __jn; __jk++) {
                __ja0 = __wrap_112(__ja0, __jr0_0[__jk]);
            }
            __v115[__i0] = __ja0;
        }
    }
    auto __wrap_114___v94 = [&](double u, double v) { return __lambda_114(u, v); };
    // [omp] requested but emitted serial: BLADE_FP_REPRO=1 -- this build keeps the serial association of every fold (the reorder licence in source is unchanged)
    // reduce: accumulator loop, eager
    double __v94 = 0.0;
    for (size_t __ri = 0; __ri < 23; __ri++) {
        __v94 = __wrap_114___v94(__v94, __v115[__ri]);
    }
    std::tuple<double, double> __tup_95 = std::make_tuple(__v61, __v94);
    
    double checksum = std::get<0>(__tup_95);
    
    double input_checksum = std::get<1>(__tup_95);
    

    auto __blade_t1 = TIME;
    double __blade_elapsed = 1e-9 * TIME_DIFF;
    cout << "antisym completed in " << __blade_elapsed << "s" << endl;

    // Print results for verification
    cout << "checksum = " << checksum << endl;
    cout << "input_checksum = " << input_checksum << endl;

    return 0;
    } catch (const std::exception& e) { blade_rt::panic_exception(e); }
      catch (...) { blade_rt::panic("BL8005", "unknown exception", nullptr, 0); }
}