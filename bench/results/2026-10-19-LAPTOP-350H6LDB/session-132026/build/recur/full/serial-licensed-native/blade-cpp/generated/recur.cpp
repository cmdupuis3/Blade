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
using T = int64_t;

using X = int64_t;


// Run record (BLADE_RUN_RECORD): the input manifest this program was compiled against,
// written with what the run observed when the process exits (blade_run_record.hpp).
static const blade_rr::Input __blade_rr_inputs[] = {
    { nullptr, nullptr, nullptr, nullptr, nullptr, nullptr, nullptr, nullptr, nullptr, nullptr }
};
static blade_rr::AtExit __blade_rr_at_exit{ "recur", "0.20.0", false, __blade_rr_inputs, 0, nullptr };



double gen(int64_t k, int64_t seed, double lo, double hi);
Array<double, 1> inputs();
Array<double, 1> rhs(Array<double, 1> u);
Array<double, 1> step(Array<double, 1> u);
double kernel(Array<double, 1> u0);
double __lambda_382(X i);
double __lambda_383(double __zl, double __zr);
double __lambda_384(double __zl, double __zr);
double __lambda_385(double __zl, double __zr);
double __lambda_386(double __zl, double __zr);
double __lambda_387(double __zl, double __zr);
double __lambda_388(double __zl, double __zr);
double __lambda_389(double __zl, double __zr);
double __lambda_390(double __zl, double __zr);
double __lambda_391(double __bx);
double __lambda_392(double __bx);
double __lambda_393(double __zl, double __zr);
double __lambda_394(double __bx);
double __lambda_395(double __bx);
double __lambda_396(double __zl, double __zr);
double __lambda_397(double __bx);
double __lambda_398(double __bx);
double __lambda_399(double __zl, double __zr);
double __lambda_400(double __bx);
double __lambda_401(double __zl, double __zr);
double __lambda_402(double __zl, double __zr);
double __lambda_403(double __zl, double __zr);
double __lambda_404(double __bx);
double __lambda_405(double __bx);
double __lambda_406(double __bx);
double __lambda_407(double __zl, double __zr);
double __lambda_408(double __bx);
double __lambda_409(double __bx);
double __lambda_410(double __bx);
double __lambda_411(double __zl, double __zr);
double __lambda_412(double __zl, double __zr);
double __lambda_413(double __bx);
double __lambda_414(double __bx);
double __lambda_415(double __bx);
double __lambda_416(double __zl, double __zr);
double __lambda_417(double __bx);
double __lambda_418(double __bx);
double __lambda_419(double __bx);
double __lambda_420(double __zl, double __zr);
double __lambda_421(double __zl, double __zr);
double __lambda_422(double __zl, double __zr);
double __lambda_423(double __bx);
double __lambda_424(double __bx);
double __lambda_425(double __bx);
double __lambda_426(double __zl, double __zr);
double __lambda_427(double __bx);
double __lambda_428(double __bx);
double __lambda_429(double __bx);
double __lambda_430(double __zl, double __zr);
double __lambda_431(double __zl, double __zr);
double __lambda_432(double __bx);
double __lambda_433(double __bx);
double __lambda_434(double __bx);
double __lambda_435(double __zl, double __zr);
double __lambda_436(double __bx);
double __lambda_437(double __bx);
double __lambda_438(double __bx);
double __lambda_439(double __bx);
double __lambda_440(double __zl, double __zr);
double __lambda_441(double __zl, double __zr);
double __lambda_442(double __zl, double __zr);
double __lambda_443(double __bx);
double __lambda_444(double __bx);
double __lambda_445(double __bx);
double __lambda_446(double __zl, double __zr);
double __lambda_447(double __bx);
double __lambda_448(double __bx);
double __lambda_449(double __bx);
double __lambda_450(double __zl, double __zr);
double __lambda_451(double __zl, double __zr);
double __lambda_452(double __bx);
double __lambda_453(double __bx);
double __lambda_454(double __bx);
double __lambda_455(double __zl, double __zr);
double __lambda_456(double __bx);
double __lambda_457(double __bx);
double __lambda_458(double __bx);
double __lambda_459(double __zl, double __zr);
double __lambda_460(double __zl, double __zr);
double __lambda_461(double __zl, double __zr);
double __lambda_462(double __bx);
double __lambda_463(double __bx);
double __lambda_464(double __bx);
double __lambda_465(double __zl, double __zr);
double __lambda_466(double __bx);
double __lambda_467(double __bx);
double __lambda_468(double __bx);
double __lambda_469(double __zl, double __zr);
double __lambda_470(double __zl, double __zr);
double __lambda_471(double __bx);
double __lambda_472(double __bx);
double __lambda_473(double __bx);
double __lambda_474(double __zl, double __zr);
double __lambda_475(double __bx);
double __lambda_476(double __bx);
double __lambda_477(double __bx);
double __lambda_483(Array<double, 1> row);
double __lambda_486(double a, double b);
double __lambda_489(double a, double b);
double __lambda_499(double __zl, double __fz496, double __fz497, double __fz498);
double __lambda_501(double __zl, double __fz500);
double __lambda_503(double __zl, double __fz502);
double __lambda_505(double __zl, double __fz504);
double __lambda_600(double __zl, double __fz596, double __fz597, double __fz598, double __fz599);

double gen(int64_t k, int64_t seed, double lo, double hi) {
    int64_t __v7 = ((((k + seed) * 1103515245L) + 12345L) % 2147483648L);
    int64_t __v8 = ((__v7 * ((__v7 / 4096L) + 1L)) % 2147483648L);
    int64_t __v9 = (((__v8 * 1103515245L) + 12345L) % 2147483648L);
    int64_t __v10 = (((__v9 * __v9) / 65536L) % 2147483648L);
    int64_t __v11 = (((__v10 * 1103515245L) + 12345L) % 2147483648L);
    return (lo + ((hi - lo) * (blade_arith::i64_to_f64(__v11) / 2147483648.0)));
}

Array<double, 1> inputs() {
    BLADE_FRAME("inputs", nullptr, 0);

    static constexpr const size_t __ret1073741824_extents[1] = { 20011 };

    Array<double, 1> __ret1073741824 = { allocate<typename promote<double, 1>::type, nullptr>(__ret1073741824_extents), __ret1073741824_extents };

    for (size_t __i0 = 0; __i0 < 20011; __i0++) {
        int64_t i = __i0;
        __ret1073741824[__i0] = gen(static_cast<int64_t>(i), 10000019L, 0.0, 2.0);
    }
    return __ret1073741824;
}

Array<double, 1> rhs(Array<double, 1> u) {
    BLADE_FRAME("rhs", nullptr, 0);
    if ((int64_t)(u.extents[0]) != (int64_t)(u.extents[0])) {
        std::cerr << "Blade runtime: co-iteration over 'u' and 'u' needs equal extents on axis 0, but 'u' has " << u.extents[0] << " and 'u' has " << u.extents[0] << " -- the walk is bounded by the first operand, so the shorter one would be read past its end" << std::endl;
        blade_rt::panic("BL8011", "co-iteration extent mismatch", "C:\\Users\\cdupu\\Documents\\GitHub\\Blade\\.claude\\worktrees\\bench-fortran\\bench\\results\\2026-10-19-LAPTOP-350H6LDB\\session-132026\\build\\recur\\full\\serial-licensed-native\\blade-cpp\\src\\recur.blade", 28);
    }
    if ((int64_t)(u.extents[0]) != (int64_t)(u.extents[0])) {
        std::cerr << "Blade runtime: co-iteration over 'u' and 'u' needs equal extents on axis 0, but 'u' has " << u.extents[0] << " and 'u' has " << u.extents[0] << " -- the walk is bounded by the first operand, so the shorter one would be read past its end" << std::endl;
        blade_rt::panic("BL8011", "co-iteration extent mismatch", "C:\\Users\\cdupu\\Documents\\GitHub\\Blade\\.claude\\worktrees\\bench-fortran\\bench\\results\\2026-10-19-LAPTOP-350H6LDB\\session-132026\\build\\recur\\full\\serial-licensed-native\\blade-cpp\\src\\recur.blade", 28);
    }
    if ((int64_t)(u.extents[0]) != (int64_t)(u.extents[0])) {
        std::cerr << "Blade runtime: co-iteration over 'u' and 'u' needs equal extents on axis 0, but 'u' has " << u.extents[0] << " and 'u' has " << u.extents[0] << " -- the walk is bounded by the first operand, so the shorter one would be read past its end" << std::endl;
        blade_rt::panic("BL8011", "co-iteration extent mismatch", "C:\\Users\\cdupu\\Documents\\GitHub\\Blade\\.claude\\worktrees\\bench-fortran\\bench\\results\\2026-10-19-LAPTOP-350H6LDB\\session-132026\\build\\recur\\full\\serial-licensed-native\\blade-cpp\\src\\recur.blade", 28);
    }

    static constexpr const size_t __ret1073741825_extents[1] = { 20011 };

    Array<double, 1> __ret1073741825 = { allocate<typename promote<double, 1>::type, nullptr>(__ret1073741825_extents), __ret1073741825_extents };

    // flat elementwise: 20011 cells [20011] (pool DFS order == nest order)
    {
        double* BLADE_RESTRICT __fp___ret1073741825 = nested_array_utilities::pool_base(__ret1073741825.data);
        const double* BLADE_RESTRICT __fp_u = nested_array_utilities::pool_base(u.data);
        const size_t __fp_cells = 20011UL;
        BLADE_IVDEP
        for (size_t __fk = 0; __fk < __fp_cells; __fk++)
            __fp___ret1073741825[__fk] = (__fp_u[__fk] - ((__fp_u[__fk] * __fp_u[__fk]) * __fp_u[__fk]));
    }
    return __ret1073741825;
}

Array<double, 1> step(Array<double, 1> u) {
    BLADE_FRAME("step", nullptr, 0);
    auto __v60 = rhs(u);
    if ((int64_t)(__v60.extents[0]) != (int64_t)(u.extents[0])) {
        std::cerr << "Blade runtime: co-iteration over 'u' and '__v60' needs equal extents on axis 0, but 'u' has " << u.extents[0] << " and '__v60' has " << __v60.extents[0] << " -- the walk is bounded by the first operand, so the shorter one would be read past its end" << std::endl;
        blade_rt::panic("BL8011", "co-iteration extent mismatch", "C:\\Users\\cdupu\\Documents\\GitHub\\Blade\\.claude\\worktrees\\bench-fortran\\bench\\results\\2026-10-19-LAPTOP-350H6LDB\\session-132026\\build\\recur\\full\\serial-licensed-native\\blade-cpp\\src\\recur.blade", 31);
    }

    static constexpr const size_t __v619_extents[1] = { 20011 };

    Array<double, 1> __v619 = { allocate<typename promote<double, 1>::type, nullptr>(__v619_extents), __v619_extents };

    // flat elementwise: 20011 cells [20011] (pool DFS order == nest order)
    {
        double* BLADE_RESTRICT __fp___v619 = nested_array_utilities::pool_base(__v619.data);
        const double* BLADE_RESTRICT __fp_u = nested_array_utilities::pool_base(u.data);
        const double* BLADE_RESTRICT __fp___v60 = nested_array_utilities::pool_base(__v60.data);
        const size_t __fp_cells = 20011UL;
        BLADE_IVDEP
        for (size_t __fk = 0; __fk < __fp_cells; __fk++)
            __fp___v619[__fk] = (__fp_u[__fk] + (0.005 * __fp___v60[__fk]));
    }
    auto __v70 = rhs(__v619);
    if ((int64_t)(__v70.extents[0]) != (int64_t)(u.extents[0])) {
        std::cerr << "Blade runtime: co-iteration over 'u' and '__v70' needs equal extents on axis 0, but 'u' has " << u.extents[0] << " and '__v70' has " << __v70.extents[0] << " -- the walk is bounded by the first operand, so the shorter one would be read past its end" << std::endl;
        blade_rt::panic("BL8011", "co-iteration extent mismatch", "C:\\Users\\cdupu\\Documents\\GitHub\\Blade\\.claude\\worktrees\\bench-fortran\\bench\\results\\2026-10-19-LAPTOP-350H6LDB\\session-132026\\build\\recur\\full\\serial-licensed-native\\blade-cpp\\src\\recur.blade", 31);
    }

    static constexpr const size_t __v620_extents[1] = { 20011 };

    Array<double, 1> __v620 = { allocate<typename promote<double, 1>::type, nullptr>(__v620_extents), __v620_extents };

    // flat elementwise: 20011 cells [20011] (pool DFS order == nest order)
    {
        double* BLADE_RESTRICT __fp___v620 = nested_array_utilities::pool_base(__v620.data);
        const double* BLADE_RESTRICT __fp_u = nested_array_utilities::pool_base(u.data);
        const double* BLADE_RESTRICT __fp___v70 = nested_array_utilities::pool_base(__v70.data);
        const size_t __fp_cells = 20011UL;
        BLADE_IVDEP
        for (size_t __fk = 0; __fk < __fp_cells; __fk++)
            __fp___v620[__fk] = (__fp_u[__fk] + (0.005 * __fp___v70[__fk]));
    }
    auto __v80 = rhs(__v620);
    if ((int64_t)(__v80.extents[0]) != (int64_t)(u.extents[0])) {
        std::cerr << "Blade runtime: co-iteration over 'u' and '__v80' needs equal extents on axis 0, but 'u' has " << u.extents[0] << " and '__v80' has " << __v80.extents[0] << " -- the walk is bounded by the first operand, so the shorter one would be read past its end" << std::endl;
        blade_rt::panic("BL8011", "co-iteration extent mismatch", "C:\\Users\\cdupu\\Documents\\GitHub\\Blade\\.claude\\worktrees\\bench-fortran\\bench\\results\\2026-10-19-LAPTOP-350H6LDB\\session-132026\\build\\recur\\full\\serial-licensed-native\\blade-cpp\\src\\recur.blade", 31);
    }

    static constexpr const size_t __v621_extents[1] = { 20011 };

    Array<double, 1> __v621 = { allocate<typename promote<double, 1>::type, nullptr>(__v621_extents), __v621_extents };

    // flat elementwise: 20011 cells [20011] (pool DFS order == nest order)
    {
        double* BLADE_RESTRICT __fp___v621 = nested_array_utilities::pool_base(__v621.data);
        const double* BLADE_RESTRICT __fp_u = nested_array_utilities::pool_base(u.data);
        const double* BLADE_RESTRICT __fp___v80 = nested_array_utilities::pool_base(__v80.data);
        const size_t __fp_cells = 20011UL;
        BLADE_IVDEP
        for (size_t __fk = 0; __fk < __fp_cells; __fk++)
            __fp___v621[__fk] = (__fp_u[__fk] + (0.01 * __fp___v80[__fk]));
    }
    auto __v90 = rhs(__v621);
    if ((int64_t)(__v60.extents[0]) != (int64_t)(u.extents[0])) {
        std::cerr << "Blade runtime: co-iteration over 'u' and '__v60' needs equal extents on axis 0, but 'u' has " << u.extents[0] << " and '__v60' has " << __v60.extents[0] << " -- the walk is bounded by the first operand, so the shorter one would be read past its end" << std::endl;
        blade_rt::panic("BL8011", "co-iteration extent mismatch", "C:\\Users\\cdupu\\Documents\\GitHub\\Blade\\.claude\\worktrees\\bench-fortran\\bench\\results\\2026-10-19-LAPTOP-350H6LDB\\session-132026\\build\\recur\\full\\serial-licensed-native\\blade-cpp\\src\\recur.blade", 31);
    }
    if ((int64_t)(__v70.extents[0]) != (int64_t)(u.extents[0])) {
        std::cerr << "Blade runtime: co-iteration over 'u' and '__v70' needs equal extents on axis 0, but 'u' has " << u.extents[0] << " and '__v70' has " << __v70.extents[0] << " -- the walk is bounded by the first operand, so the shorter one would be read past its end" << std::endl;
        blade_rt::panic("BL8011", "co-iteration extent mismatch", "C:\\Users\\cdupu\\Documents\\GitHub\\Blade\\.claude\\worktrees\\bench-fortran\\bench\\results\\2026-10-19-LAPTOP-350H6LDB\\session-132026\\build\\recur\\full\\serial-licensed-native\\blade-cpp\\src\\recur.blade", 31);
    }
    if ((int64_t)(__v80.extents[0]) != (int64_t)(u.extents[0])) {
        std::cerr << "Blade runtime: co-iteration over 'u' and '__v80' needs equal extents on axis 0, but 'u' has " << u.extents[0] << " and '__v80' has " << __v80.extents[0] << " -- the walk is bounded by the first operand, so the shorter one would be read past its end" << std::endl;
        blade_rt::panic("BL8011", "co-iteration extent mismatch", "C:\\Users\\cdupu\\Documents\\GitHub\\Blade\\.claude\\worktrees\\bench-fortran\\bench\\results\\2026-10-19-LAPTOP-350H6LDB\\session-132026\\build\\recur\\full\\serial-licensed-native\\blade-cpp\\src\\recur.blade", 31);
    }
    if ((int64_t)(__v90.extents[0]) != (int64_t)(u.extents[0])) {
        std::cerr << "Blade runtime: co-iteration over 'u' and '__v90' needs equal extents on axis 0, but 'u' has " << u.extents[0] << " and '__v90' has " << __v90.extents[0] << " -- the walk is bounded by the first operand, so the shorter one would be read past its end" << std::endl;
        blade_rt::panic("BL8011", "co-iteration extent mismatch", "C:\\Users\\cdupu\\Documents\\GitHub\\Blade\\.claude\\worktrees\\bench-fortran\\bench\\results\\2026-10-19-LAPTOP-350H6LDB\\session-132026\\build\\recur\\full\\serial-licensed-native\\blade-cpp\\src\\recur.blade", 31);
    }

    static constexpr const size_t __ret1073741826_extents[1] = { 20011 };

    Array<double, 1> __ret1073741826 = { allocate<typename promote<double, 1>::type, nullptr>(__ret1073741826_extents), __ret1073741826_extents };

    // flat elementwise: 20011 cells [20011] (pool DFS order == nest order)
    {
        double* BLADE_RESTRICT __fp___ret1073741826 = nested_array_utilities::pool_base(__ret1073741826.data);
        const double* BLADE_RESTRICT __fp_u = nested_array_utilities::pool_base(u.data);
        const double* BLADE_RESTRICT __fp___v60 = nested_array_utilities::pool_base(__v60.data);
        const double* BLADE_RESTRICT __fp___v70 = nested_array_utilities::pool_base(__v70.data);
        const double* BLADE_RESTRICT __fp___v80 = nested_array_utilities::pool_base(__v80.data);
        const double* BLADE_RESTRICT __fp___v90 = nested_array_utilities::pool_base(__v90.data);
        const size_t __fp_cells = 20011UL;
        BLADE_IVDEP
        for (size_t __fk = 0; __fk < __fp_cells; __fk++)
            __fp___ret1073741826[__fk] = (__fp_u[__fk] + ((0.01 / 6.0) * (((__fp___v60[__fk] + (2.0 * __fp___v70[__fk])) + (2.0 * __fp___v80[__fk])) + __fp___v90[__fk])));
    }
    deallocate<typename promote<double, 1>::type, nullptr>(__v90.data, __v90.extents);
    deallocate<typename promote<double, 1>::type, nullptr>(__v621.data, __v621.extents);
    deallocate<typename promote<double, 1>::type, nullptr>(__v80.data, __v80.extents);
    deallocate<typename promote<double, 1>::type, nullptr>(__v620.data, __v620.extents);
    deallocate<typename promote<double, 1>::type, nullptr>(__v70.data, __v70.extents);
    deallocate<typename promote<double, 1>::type, nullptr>(__v619.data, __v619.extents);
    deallocate<typename promote<double, 1>::type, nullptr>(__v60.data, __v60.extents);
    return __ret1073741826;
}

double kernel(Array<double, 1> u0) {
    BLADE_FRAME("kernel", nullptr, 0);
    auto __v611 = 0.0;
    static constexpr const size_t __v363_extents[2] = { 301, 20011 };
    Array<double, 2> __v363 = { allocate<typename promote<double, 2>::type, nullptr>(__v363_extents), __v363_extents };
    // __v363: every cell is written by the recursion before it is read -- no zero fill (formalism 7.5)
    for (int64_t __k365 = 0L; __k365 < 20011L; __k365++) {
        double __v601 = u0[__k365];
        __v363[0L][__k365] = __v601;
    }
    auto __v616 = Array<double, 1>{ __v363.data[0L], __v363.extents + 1 };
    auto __v617 = blade_fold::sq<double, 8>(__v616.data, __v616.extents[0]);
    __v611 = (__v611 + __v617);
    for (int64_t __k366 = 1L; __k366 < 301L; __k366++) {
        Array<double, 1> __v367 = { __v363.data[(__k366 - 1L)], __v363.extents + 1 };
        for (int64_t __k369 = 0L; __k369 < 20011L; __k369++) {
            double __v602 = __v367[__k369];
            double __v603 = (__v602 - ((__v602 * __v602) * __v602));
            double __v604 = (__v602 + (0.005 * __v603));
            double __v605 = (__v604 - ((__v604 * __v604) * __v604));
            double __v606 = (__v602 + (0.005 * __v605));
            double __v607 = (__v606 - ((__v606 * __v606) * __v606));
            double __v608 = (__v602 + (0.01 * __v607));
            double __v609 = (__v608 - ((__v608 * __v608) * __v608));
            double __v610 = (__v602 + ((0.01 / 6.0) * (((__v603 + (2.0 * __v605)) + (2.0 * __v607)) + __v609)));
            __v363[__k366][__k369] = __v610;
        }
        Array<double, 1> __v612 = { __v363.data[__k366], __v363.extents + 1 };
        double __v613 = blade_fold::sq<double, 8>(__v612.data, __v612.extents[0]);
        __v611 = (__v611 + __v613);
    }
    Array<double, 2> __v370 = __v363;
    auto __retv1073741827 = __v611;
    deallocate<typename promote<double, 2>::type, nullptr>(__v363.data, __v363.extents);
    return __retv1073741827;
}

double __lambda_382(X i) {
    return gen(static_cast<int64_t>(i), 10000019L, 0.0, 2.0);
}

double __lambda_383(double __zl, double __zr) {
    return (__zl - __zr);
}

double __lambda_384(double __zl, double __zr) {
    return (__zl * __zr);
}

double __lambda_385(double __zl, double __zr) {
    return (__zl * __zr);
}

double __lambda_386(double __zl, double __zr) {
    return (__zl * __zr);
}

double __lambda_387(double __zl, double __zr) {
    return (__zl * __zr);
}

double __lambda_388(double __zl, double __zr) {
    return (__zl * __zr);
}

double __lambda_389(double __zl, double __zr) {
    return (__zl * __zr);
}

double __lambda_390(double __zl, double __zr) {
    return (__zl + __zr);
}

double __lambda_391(double __bx) {
    return (0.005 * __bx);
}

double __lambda_392(double __bx) {
    return (0.005 * __bx);
}

double __lambda_393(double __zl, double __zr) {
    return (__zl + __zr);
}

double __lambda_394(double __bx) {
    return (0.005 * __bx);
}

double __lambda_395(double __bx) {
    return (0.005 * __bx);
}

double __lambda_396(double __zl, double __zr) {
    return (__zl + __zr);
}

double __lambda_397(double __bx) {
    return (0.01 * __bx);
}

double __lambda_398(double __bx) {
    return (0.01 * __bx);
}

double __lambda_399(double __zl, double __zr) {
    return (__zl + __zr);
}

double __lambda_400(double __bx) {
    return ((0.01 / 6.0) * __bx);
}

double __lambda_401(double __zl, double __zr) {
    return (__zl + __zr);
}

double __lambda_402(double __zl, double __zr) {
    return (__zl + __zr);
}

double __lambda_403(double __zl, double __zr) {
    return (__zl + __zr);
}

double __lambda_404(double __bx) {
    return (2.0 * __bx);
}

double __lambda_405(double __bx) {
    return (2.0 * __bx);
}

double __lambda_406(double __bx) {
    return (2.0 * __bx);
}

double __lambda_407(double __zl, double __zr) {
    return (__zl + __zr);
}

double __lambda_408(double __bx) {
    return (2.0 * __bx);
}

double __lambda_409(double __bx) {
    return (2.0 * __bx);
}

double __lambda_410(double __bx) {
    return (2.0 * __bx);
}

double __lambda_411(double __zl, double __zr) {
    return (__zl + __zr);
}

double __lambda_412(double __zl, double __zr) {
    return (__zl + __zr);
}

double __lambda_413(double __bx) {
    return (2.0 * __bx);
}

double __lambda_414(double __bx) {
    return (2.0 * __bx);
}

double __lambda_415(double __bx) {
    return (2.0 * __bx);
}

double __lambda_416(double __zl, double __zr) {
    return (__zl + __zr);
}

double __lambda_417(double __bx) {
    return (2.0 * __bx);
}

double __lambda_418(double __bx) {
    return (2.0 * __bx);
}

double __lambda_419(double __bx) {
    return (2.0 * __bx);
}

double __lambda_420(double __zl, double __zr) {
    return (__zl + __zr);
}

double __lambda_421(double __zl, double __zr) {
    return (__zl + __zr);
}

double __lambda_422(double __zl, double __zr) {
    return (__zl + __zr);
}

double __lambda_423(double __bx) {
    return (2.0 * __bx);
}

double __lambda_424(double __bx) {
    return (2.0 * __bx);
}

double __lambda_425(double __bx) {
    return (2.0 * __bx);
}

double __lambda_426(double __zl, double __zr) {
    return (__zl + __zr);
}

double __lambda_427(double __bx) {
    return (2.0 * __bx);
}

double __lambda_428(double __bx) {
    return (2.0 * __bx);
}

double __lambda_429(double __bx) {
    return (2.0 * __bx);
}

double __lambda_430(double __zl, double __zr) {
    return (__zl + __zr);
}

double __lambda_431(double __zl, double __zr) {
    return (__zl + __zr);
}

double __lambda_432(double __bx) {
    return (2.0 * __bx);
}

double __lambda_433(double __bx) {
    return (2.0 * __bx);
}

double __lambda_434(double __bx) {
    return (2.0 * __bx);
}

double __lambda_435(double __zl, double __zr) {
    return (__zl + __zr);
}

double __lambda_436(double __bx) {
    return (2.0 * __bx);
}

double __lambda_437(double __bx) {
    return (2.0 * __bx);
}

double __lambda_438(double __bx) {
    return (2.0 * __bx);
}

double __lambda_439(double __bx) {
    return ((0.01 / 6.0) * __bx);
}

double __lambda_440(double __zl, double __zr) {
    return (__zl + __zr);
}

double __lambda_441(double __zl, double __zr) {
    return (__zl + __zr);
}

double __lambda_442(double __zl, double __zr) {
    return (__zl + __zr);
}

double __lambda_443(double __bx) {
    return (2.0 * __bx);
}

double __lambda_444(double __bx) {
    return (2.0 * __bx);
}

double __lambda_445(double __bx) {
    return (2.0 * __bx);
}

double __lambda_446(double __zl, double __zr) {
    return (__zl + __zr);
}

double __lambda_447(double __bx) {
    return (2.0 * __bx);
}

double __lambda_448(double __bx) {
    return (2.0 * __bx);
}

double __lambda_449(double __bx) {
    return (2.0 * __bx);
}

double __lambda_450(double __zl, double __zr) {
    return (__zl + __zr);
}

double __lambda_451(double __zl, double __zr) {
    return (__zl + __zr);
}

double __lambda_452(double __bx) {
    return (2.0 * __bx);
}

double __lambda_453(double __bx) {
    return (2.0 * __bx);
}

double __lambda_454(double __bx) {
    return (2.0 * __bx);
}

double __lambda_455(double __zl, double __zr) {
    return (__zl + __zr);
}

double __lambda_456(double __bx) {
    return (2.0 * __bx);
}

double __lambda_457(double __bx) {
    return (2.0 * __bx);
}

double __lambda_458(double __bx) {
    return (2.0 * __bx);
}

double __lambda_459(double __zl, double __zr) {
    return (__zl + __zr);
}

double __lambda_460(double __zl, double __zr) {
    return (__zl + __zr);
}

double __lambda_461(double __zl, double __zr) {
    return (__zl + __zr);
}

double __lambda_462(double __bx) {
    return (2.0 * __bx);
}

double __lambda_463(double __bx) {
    return (2.0 * __bx);
}

double __lambda_464(double __bx) {
    return (2.0 * __bx);
}

double __lambda_465(double __zl, double __zr) {
    return (__zl + __zr);
}

double __lambda_466(double __bx) {
    return (2.0 * __bx);
}

double __lambda_467(double __bx) {
    return (2.0 * __bx);
}

double __lambda_468(double __bx) {
    return (2.0 * __bx);
}

double __lambda_469(double __zl, double __zr) {
    return (__zl + __zr);
}

double __lambda_470(double __zl, double __zr) {
    return (__zl + __zr);
}

double __lambda_471(double __bx) {
    return (2.0 * __bx);
}

double __lambda_472(double __bx) {
    return (2.0 * __bx);
}

double __lambda_473(double __bx) {
    return (2.0 * __bx);
}

double __lambda_474(double __zl, double __zr) {
    return (__zl + __zr);
}

double __lambda_475(double __bx) {
    return (2.0 * __bx);
}

double __lambda_476(double __bx) {
    return (2.0 * __bx);
}

double __lambda_477(double __bx) {
    return (2.0 * __bx);
}

double __lambda_483(Array<double, 1> row) {
    BLADE_FRAME("lambda in kernel", nullptr, 0);
    return blade_fold::sq<double, 8>(row.data, row.extents[0]);
}

double __lambda_486(double a, double b) {
    return (a + b);
}

double __lambda_489(double a, double b) {
    return (a + b);
}

double __lambda_499(double __zl, double __fz496, double __fz497, double __fz498) {
    return (__zl - ((__fz496 * __fz497) * __fz498));
}

double __lambda_501(double __zl, double __fz500) {
    return (__zl + (0.005 * __fz500));
}

double __lambda_503(double __zl, double __fz502) {
    return (__zl + (0.005 * __fz502));
}

double __lambda_505(double __zl, double __fz504) {
    return (__zl + (0.01 * __fz504));
}

double __lambda_600(double __zl, double __fz596, double __fz597, double __fz598, double __fz599) {
    return (__zl + ((0.01 / 6.0) * (((__fz596 + (2.0 * __fz597)) + (2.0 * __fz598)) + __fz599)));
}

int main() {
    cout << std::setprecision(15);
    cout << std::boolalpha;
    blade_rt::typed_float_print(cout);
    auto __blade_t0 = TIME;

    try {
    Array<double, 1> __v378 = inputs();
    std::tuple<double, double> __tup_379 = std::make_tuple(kernel(__v378), [&]() { auto __wrap_489 = [&](double a, double b) { return __lambda_489(a, b); }; const size_t __rn = 20011; double __r = __v378[0]; const size_t __rlo = 1; const size_t __rcnt = (__rn > __rlo) ? (__rn - __rlo) : (size_t)0; if (__rcnt < (size_t)8) { for (size_t __ri = __rlo; __ri < __rn; __ri++) { __r = __wrap_489(__r, __v378[__ri]); } return __r; } double __rlane0 = __v378[__rlo + 0]; double __rlane1 = __v378[__rlo + 1]; double __rlane2 = __v378[__rlo + 2]; double __rlane3 = __v378[__rlo + 3]; double __rlane4 = __v378[__rlo + 4]; double __rlane5 = __v378[__rlo + 5]; double __rlane6 = __v378[__rlo + 6]; double __rlane7 = __v378[__rlo + 7]; size_t __ri = __rlo + 8; for (; __ri + 8 <= __rn; __ri += 8) {     __rlane0 = __wrap_489(__rlane0, __v378[__ri + 0]);     __rlane1 = __wrap_489(__rlane1, __v378[__ri + 1]);     __rlane2 = __wrap_489(__rlane2, __v378[__ri + 2]);     __rlane3 = __wrap_489(__rlane3, __v378[__ri + 3]);     __rlane4 = __wrap_489(__rlane4, __v378[__ri + 4]);     __rlane5 = __wrap_489(__rlane5, __v378[__ri + 5]);     __rlane6 = __wrap_489(__rlane6, __v378[__ri + 6]);     __rlane7 = __wrap_489(__rlane7, __v378[__ri + 7]); } if (__ri < __rn) { __rlane0 = __wrap_489(__rlane0, __v378[__ri]); __ri++; } if (__ri < __rn) { __rlane1 = __wrap_489(__rlane1, __v378[__ri]); __ri++; } if (__ri < __rn) { __rlane2 = __wrap_489(__rlane2, __v378[__ri]); __ri++; } if (__ri < __rn) { __rlane3 = __wrap_489(__rlane3, __v378[__ri]); __ri++; } if (__ri < __rn) { __rlane4 = __wrap_489(__rlane4, __v378[__ri]); __ri++; } if (__ri < __rn) { __rlane5 = __wrap_489(__rlane5, __v378[__ri]); __ri++; } if (__ri < __rn) { __rlane6 = __wrap_489(__rlane6, __v378[__ri]); __ri++; } __rlane0 = __wrap_489(__rlane0, __rlane1); __rlane0 = __wrap_489(__rlane0, __rlane2); __rlane0 = __wrap_489(__rlane0, __rlane3); __rlane0 = __wrap_489(__rlane0, __rlane4); __rlane0 = __wrap_489(__rlane0, __rlane5); __rlane0 = __wrap_489(__rlane0, __rlane6); __rlane0 = __wrap_489(__rlane0, __rlane7); __r = __wrap_489(__r, __rlane0); return __r; }());
    
    double checksum = std::get<0>(__tup_379);
    
    double input_checksum = std::get<1>(__tup_379);
    

    auto __blade_t1 = TIME;
    double __blade_elapsed = 1e-9 * TIME_DIFF;
    cout << "recur completed in " << __blade_elapsed << "s" << endl;

    // Print results for verification
    cout << "checksum = " << checksum << endl;
    cout << "input_checksum = " << input_checksum << endl;

    return 0;
    } catch (const std::exception& e) { blade_rt::panic_exception(e); }
      catch (...) { blade_rt::panic("BL8005", "unknown exception", nullptr, 0); }
}