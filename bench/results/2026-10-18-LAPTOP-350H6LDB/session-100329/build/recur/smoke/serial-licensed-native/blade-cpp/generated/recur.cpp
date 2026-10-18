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
double __lambda_5015(X i);
double __lambda_5016(double __zl, double __zr);
double __lambda_5017(double __zl, double __zr);
double __lambda_5018(double __zl, double __zr);
double __lambda_5019(double __zl, double __zr);
double __lambda_5020(double __zl, double __zr);
double __lambda_5021(double __zl, double __zr);
double __lambda_5022(double __zl, double __zr);
double __lambda_5023(double __zl, double __zr);
double __lambda_5024(double __bx);
double __lambda_5025(double __bx);
double __lambda_5026(double __zl, double __zr);
double __lambda_5027(double __bx);
double __lambda_5028(double __bx);
double __lambda_5029(double __zl, double __zr);
double __lambda_5030(double __bx);
double __lambda_5031(double __bx);
double __lambda_5032(double __zl, double __zr);
double __lambda_5033(double __bx);
double __lambda_5034(double __zl, double __zr);
double __lambda_5035(double __zl, double __zr);
double __lambda_5036(double __zl, double __zr);
double __lambda_5037(double __bx);
double __lambda_5038(double __bx);
double __lambda_5039(double __bx);
double __lambda_5040(double __zl, double __zr);
double __lambda_5041(double __bx);
double __lambda_5042(double __bx);
double __lambda_5043(double __bx);
double __lambda_5044(double __zl, double __zr);
double __lambda_5045(double __zl, double __zr);
double __lambda_5046(double __bx);
double __lambda_5047(double __bx);
double __lambda_5048(double __bx);
double __lambda_5049(double __zl, double __zr);
double __lambda_5050(double __bx);
double __lambda_5051(double __bx);
double __lambda_5052(double __bx);
double __lambda_5053(double __zl, double __zr);
double __lambda_5054(double __zl, double __zr);
double __lambda_5055(double __zl, double __zr);
double __lambda_5056(double __bx);
double __lambda_5057(double __bx);
double __lambda_5058(double __bx);
double __lambda_5059(double __zl, double __zr);
double __lambda_5060(double __bx);
double __lambda_5061(double __bx);
double __lambda_5062(double __bx);
double __lambda_5063(double __zl, double __zr);
double __lambda_5064(double __zl, double __zr);
double __lambda_5065(double __bx);
double __lambda_5066(double __bx);
double __lambda_5067(double __bx);
double __lambda_5068(double __zl, double __zr);
double __lambda_5069(double __bx);
double __lambda_5070(double __bx);
double __lambda_5071(double __bx);
double __lambda_5072(double __bx);
double __lambda_5073(double __zl, double __zr);
double __lambda_5074(double __zl, double __zr);
double __lambda_5075(double __zl, double __zr);
double __lambda_5076(double __bx);
double __lambda_5077(double __bx);
double __lambda_5078(double __bx);
double __lambda_5079(double __zl, double __zr);
double __lambda_5080(double __bx);
double __lambda_5081(double __bx);
double __lambda_5082(double __bx);
double __lambda_5083(double __zl, double __zr);
double __lambda_5084(double __zl, double __zr);
double __lambda_5085(double __bx);
double __lambda_5086(double __bx);
double __lambda_5087(double __bx);
double __lambda_5088(double __zl, double __zr);
double __lambda_5089(double __bx);
double __lambda_5090(double __bx);
double __lambda_5091(double __bx);
double __lambda_5092(double __zl, double __zr);
double __lambda_5093(double __zl, double __zr);
double __lambda_5094(double __zl, double __zr);
double __lambda_5095(double __bx);
double __lambda_5096(double __bx);
double __lambda_5097(double __bx);
double __lambda_5098(double __zl, double __zr);
double __lambda_5099(double __bx);
double __lambda_5100(double __bx);
double __lambda_5101(double __bx);
double __lambda_5102(double __zl, double __zr);
double __lambda_5103(double __zl, double __zr);
double __lambda_5104(double __bx);
double __lambda_5105(double __bx);
double __lambda_5106(double __bx);
double __lambda_5107(double __zl, double __zr);
double __lambda_5108(double __bx);
double __lambda_5109(double __bx);
double __lambda_5110(double __bx);
double __lambda_5116(Array<double, 1> row);
double __lambda_5119(double a, double b);
double __lambda_5122(double a, double b);
double __lambda_5132(double __zl, double __fz5129, double __fz5130, double __fz5131);
double __lambda_5134(double __zl, double __fz5133);
double __lambda_5136(double __zl, double __fz5135);
double __lambda_5138(double __zl, double __fz5137);
double __lambda_5233(double __zl, double __fz5229, double __fz5230, double __fz5231, double __fz5232);

double gen(int64_t k, int64_t seed, double lo, double hi) {
    BLADE_FRAME("gen", nullptr, 0);
    int64_t __v7 = ((((k + seed) * 1103515245L) + 12345L) % 2147483648L);
    int64_t __v8 = ((__v7 * ((__v7 / 4096L) + 1L)) % 2147483648L);
    int64_t __v9 = (((__v8 * 1103515245L) + 12345L) % 2147483648L);
    int64_t __v10 = (((__v9 * __v9) / 65536L) % 2147483648L);
    int64_t __v11 = (((__v10 * 1103515245L) + 12345L) % 2147483648L);
    return (lo + ((hi - lo) * (static_cast<double>(__v11) / 2147483648.0)));
}

Array<double, 1> inputs() {
    BLADE_FRAME("inputs", nullptr, 0);

    static constexpr const size_t __ret1073741824_extents[1] = { 211 };

    Array<double, 1> __ret1073741824 = { allocate<typename promote<double, 1>::type, nullptr>(__ret1073741824_extents), __ret1073741824_extents };

    for (size_t __i0 = 0; __i0 < 211; __i0++) {
        int64_t i = __i0;
        __ret1073741824[__i0] = gen(static_cast<int64_t>(i), 10000019L, 0.0, 2.0);
    }
    return __ret1073741824;
}

Array<double, 1> rhs(Array<double, 1> u) {
    BLADE_FRAME("rhs", nullptr, 0);
    if ((int64_t)(u.extents[0]) != (int64_t)(u.extents[0])) {
        std::cerr << "Blade runtime: co-iteration over 'u' and 'u' needs equal extents on axis 0, but 'u' has " << u.extents[0] << " and 'u' has " << u.extents[0] << " -- the walk is bounded by the first operand, so the shorter one would be read past its end" << std::endl;
        blade_rt::panic("BL8011", "co-iteration extent mismatch", "C:\\Users\\cdupu\\Documents\\GitHub\\Blade\\.claude\\worktrees\\bench-fortran\\bench\\results\\2026-10-18-LAPTOP-350H6LDB\\session-100329\\build\\recur\\smoke\\serial-licensed-native\\blade-cpp\\src\\recur.blade", 28);
    }
    if ((int64_t)(u.extents[0]) != (int64_t)(u.extents[0])) {
        std::cerr << "Blade runtime: co-iteration over 'u' and 'u' needs equal extents on axis 0, but 'u' has " << u.extents[0] << " and 'u' has " << u.extents[0] << " -- the walk is bounded by the first operand, so the shorter one would be read past its end" << std::endl;
        blade_rt::panic("BL8011", "co-iteration extent mismatch", "C:\\Users\\cdupu\\Documents\\GitHub\\Blade\\.claude\\worktrees\\bench-fortran\\bench\\results\\2026-10-18-LAPTOP-350H6LDB\\session-100329\\build\\recur\\smoke\\serial-licensed-native\\blade-cpp\\src\\recur.blade", 28);
    }
    if ((int64_t)(u.extents[0]) != (int64_t)(u.extents[0])) {
        std::cerr << "Blade runtime: co-iteration over 'u' and 'u' needs equal extents on axis 0, but 'u' has " << u.extents[0] << " and 'u' has " << u.extents[0] << " -- the walk is bounded by the first operand, so the shorter one would be read past its end" << std::endl;
        blade_rt::panic("BL8011", "co-iteration extent mismatch", "C:\\Users\\cdupu\\Documents\\GitHub\\Blade\\.claude\\worktrees\\bench-fortran\\bench\\results\\2026-10-18-LAPTOP-350H6LDB\\session-100329\\build\\recur\\smoke\\serial-licensed-native\\blade-cpp\\src\\recur.blade", 28);
    }

    static constexpr const size_t __ret1073741825_extents[1] = { 211 };

    Array<double, 1> __ret1073741825 = { allocate<typename promote<double, 1>::type, nullptr>(__ret1073741825_extents), __ret1073741825_extents };

    // flat elementwise: 211 cells [211] (pool DFS order == nest order)
    {
        double* BLADE_RESTRICT __fp___ret1073741825 = nested_array_utilities::pool_base(__ret1073741825.data);
        const double* BLADE_RESTRICT __fp_u = nested_array_utilities::pool_base(u.data);
        const size_t __fp_cells = 211UL;
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
        blade_rt::panic("BL8011", "co-iteration extent mismatch", "C:\\Users\\cdupu\\Documents\\GitHub\\Blade\\.claude\\worktrees\\bench-fortran\\bench\\results\\2026-10-18-LAPTOP-350H6LDB\\session-100329\\build\\recur\\smoke\\serial-licensed-native\\blade-cpp\\src\\recur.blade", 31);
    }

    static constexpr const size_t __v5234_extents[1] = { 211 };

    Array<double, 1> __v5234 = { allocate<typename promote<double, 1>::type, nullptr>(__v5234_extents), __v5234_extents };

    // flat elementwise: 211 cells [211] (pool DFS order == nest order)
    {
        double* BLADE_RESTRICT __fp___v5234 = nested_array_utilities::pool_base(__v5234.data);
        const double* BLADE_RESTRICT __fp_u = nested_array_utilities::pool_base(u.data);
        const double* BLADE_RESTRICT __fp___v60 = nested_array_utilities::pool_base(__v60.data);
        const size_t __fp_cells = 211UL;
        BLADE_IVDEP
        for (size_t __fk = 0; __fk < __fp_cells; __fk++)
            __fp___v5234[__fk] = (__fp_u[__fk] + (0.005 * __fp___v60[__fk]));
    }
    auto __v70 = rhs(__v5234);
    if ((int64_t)(__v70.extents[0]) != (int64_t)(u.extents[0])) {
        std::cerr << "Blade runtime: co-iteration over 'u' and '__v70' needs equal extents on axis 0, but 'u' has " << u.extents[0] << " and '__v70' has " << __v70.extents[0] << " -- the walk is bounded by the first operand, so the shorter one would be read past its end" << std::endl;
        blade_rt::panic("BL8011", "co-iteration extent mismatch", "C:\\Users\\cdupu\\Documents\\GitHub\\Blade\\.claude\\worktrees\\bench-fortran\\bench\\results\\2026-10-18-LAPTOP-350H6LDB\\session-100329\\build\\recur\\smoke\\serial-licensed-native\\blade-cpp\\src\\recur.blade", 31);
    }

    static constexpr const size_t __v5235_extents[1] = { 211 };

    Array<double, 1> __v5235 = { allocate<typename promote<double, 1>::type, nullptr>(__v5235_extents), __v5235_extents };

    // flat elementwise: 211 cells [211] (pool DFS order == nest order)
    {
        double* BLADE_RESTRICT __fp___v5235 = nested_array_utilities::pool_base(__v5235.data);
        const double* BLADE_RESTRICT __fp_u = nested_array_utilities::pool_base(u.data);
        const double* BLADE_RESTRICT __fp___v70 = nested_array_utilities::pool_base(__v70.data);
        const size_t __fp_cells = 211UL;
        BLADE_IVDEP
        for (size_t __fk = 0; __fk < __fp_cells; __fk++)
            __fp___v5235[__fk] = (__fp_u[__fk] + (0.005 * __fp___v70[__fk]));
    }
    auto __v80 = rhs(__v5235);
    if ((int64_t)(__v80.extents[0]) != (int64_t)(u.extents[0])) {
        std::cerr << "Blade runtime: co-iteration over 'u' and '__v80' needs equal extents on axis 0, but 'u' has " << u.extents[0] << " and '__v80' has " << __v80.extents[0] << " -- the walk is bounded by the first operand, so the shorter one would be read past its end" << std::endl;
        blade_rt::panic("BL8011", "co-iteration extent mismatch", "C:\\Users\\cdupu\\Documents\\GitHub\\Blade\\.claude\\worktrees\\bench-fortran\\bench\\results\\2026-10-18-LAPTOP-350H6LDB\\session-100329\\build\\recur\\smoke\\serial-licensed-native\\blade-cpp\\src\\recur.blade", 31);
    }

    static constexpr const size_t __v5236_extents[1] = { 211 };

    Array<double, 1> __v5236 = { allocate<typename promote<double, 1>::type, nullptr>(__v5236_extents), __v5236_extents };

    // flat elementwise: 211 cells [211] (pool DFS order == nest order)
    {
        double* BLADE_RESTRICT __fp___v5236 = nested_array_utilities::pool_base(__v5236.data);
        const double* BLADE_RESTRICT __fp_u = nested_array_utilities::pool_base(u.data);
        const double* BLADE_RESTRICT __fp___v80 = nested_array_utilities::pool_base(__v80.data);
        const size_t __fp_cells = 211UL;
        BLADE_IVDEP
        for (size_t __fk = 0; __fk < __fp_cells; __fk++)
            __fp___v5236[__fk] = (__fp_u[__fk] + (0.01 * __fp___v80[__fk]));
    }
    auto __v90 = rhs(__v5236);
    if ((int64_t)(__v60.extents[0]) != (int64_t)(u.extents[0])) {
        std::cerr << "Blade runtime: co-iteration over 'u' and '__v60' needs equal extents on axis 0, but 'u' has " << u.extents[0] << " and '__v60' has " << __v60.extents[0] << " -- the walk is bounded by the first operand, so the shorter one would be read past its end" << std::endl;
        blade_rt::panic("BL8011", "co-iteration extent mismatch", "C:\\Users\\cdupu\\Documents\\GitHub\\Blade\\.claude\\worktrees\\bench-fortran\\bench\\results\\2026-10-18-LAPTOP-350H6LDB\\session-100329\\build\\recur\\smoke\\serial-licensed-native\\blade-cpp\\src\\recur.blade", 31);
    }
    if ((int64_t)(__v70.extents[0]) != (int64_t)(u.extents[0])) {
        std::cerr << "Blade runtime: co-iteration over 'u' and '__v70' needs equal extents on axis 0, but 'u' has " << u.extents[0] << " and '__v70' has " << __v70.extents[0] << " -- the walk is bounded by the first operand, so the shorter one would be read past its end" << std::endl;
        blade_rt::panic("BL8011", "co-iteration extent mismatch", "C:\\Users\\cdupu\\Documents\\GitHub\\Blade\\.claude\\worktrees\\bench-fortran\\bench\\results\\2026-10-18-LAPTOP-350H6LDB\\session-100329\\build\\recur\\smoke\\serial-licensed-native\\blade-cpp\\src\\recur.blade", 31);
    }
    if ((int64_t)(__v80.extents[0]) != (int64_t)(u.extents[0])) {
        std::cerr << "Blade runtime: co-iteration over 'u' and '__v80' needs equal extents on axis 0, but 'u' has " << u.extents[0] << " and '__v80' has " << __v80.extents[0] << " -- the walk is bounded by the first operand, so the shorter one would be read past its end" << std::endl;
        blade_rt::panic("BL8011", "co-iteration extent mismatch", "C:\\Users\\cdupu\\Documents\\GitHub\\Blade\\.claude\\worktrees\\bench-fortran\\bench\\results\\2026-10-18-LAPTOP-350H6LDB\\session-100329\\build\\recur\\smoke\\serial-licensed-native\\blade-cpp\\src\\recur.blade", 31);
    }
    if ((int64_t)(__v90.extents[0]) != (int64_t)(u.extents[0])) {
        std::cerr << "Blade runtime: co-iteration over 'u' and '__v90' needs equal extents on axis 0, but 'u' has " << u.extents[0] << " and '__v90' has " << __v90.extents[0] << " -- the walk is bounded by the first operand, so the shorter one would be read past its end" << std::endl;
        blade_rt::panic("BL8011", "co-iteration extent mismatch", "C:\\Users\\cdupu\\Documents\\GitHub\\Blade\\.claude\\worktrees\\bench-fortran\\bench\\results\\2026-10-18-LAPTOP-350H6LDB\\session-100329\\build\\recur\\smoke\\serial-licensed-native\\blade-cpp\\src\\recur.blade", 31);
    }

    static constexpr const size_t __ret1073741826_extents[1] = { 211 };

    Array<double, 1> __ret1073741826 = { allocate<typename promote<double, 1>::type, nullptr>(__ret1073741826_extents), __ret1073741826_extents };

    // flat elementwise: 211 cells [211] (pool DFS order == nest order)
    {
        double* BLADE_RESTRICT __fp___ret1073741826 = nested_array_utilities::pool_base(__ret1073741826.data);
        const double* BLADE_RESTRICT __fp_u = nested_array_utilities::pool_base(u.data);
        const double* BLADE_RESTRICT __fp___v60 = nested_array_utilities::pool_base(__v60.data);
        const double* BLADE_RESTRICT __fp___v70 = nested_array_utilities::pool_base(__v70.data);
        const double* BLADE_RESTRICT __fp___v80 = nested_array_utilities::pool_base(__v80.data);
        const double* BLADE_RESTRICT __fp___v90 = nested_array_utilities::pool_base(__v90.data);
        const size_t __fp_cells = 211UL;
        BLADE_IVDEP
        for (size_t __fk = 0; __fk < __fp_cells; __fk++)
            __fp___ret1073741826[__fk] = (__fp_u[__fk] + ((0.01 / 6.0) * (((__fp___v60[__fk] + (2.0 * __fp___v70[__fk])) + (2.0 * __fp___v80[__fk])) + __fp___v90[__fk])));
    }
    deallocate<typename promote<double, 1>::type, nullptr>(__v90.data, __v90.extents);
    deallocate<typename promote<double, 1>::type, nullptr>(__v5236.data, __v5236.extents);
    deallocate<typename promote<double, 1>::type, nullptr>(__v80.data, __v80.extents);
    deallocate<typename promote<double, 1>::type, nullptr>(__v5235.data, __v5235.extents);
    deallocate<typename promote<double, 1>::type, nullptr>(__v70.data, __v70.extents);
    deallocate<typename promote<double, 1>::type, nullptr>(__v5234.data, __v5234.extents);
    deallocate<typename promote<double, 1>::type, nullptr>(__v60.data, __v60.extents);
    return __ret1073741826;
}

double kernel(Array<double, 1> u0) {
    BLADE_FRAME("kernel", nullptr, 0);
    static constexpr const size_t __v4996_extents[2] = {11, 211};
    Array<double, 2> __v4996 = { allocate<typename promote<double, 2>::type, nullptr>(__v4996_extents), __v4996_extents };
    static const double __v4996__init[2321] = { 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0 };
    std::copy_n(__v4996__init, 2321, nested_array_utilities::pool_base(__v4996.data));
    static constexpr const size_t __v4997_extents[1] = { 211 };
    Array<double, 1> __v4997 = { allocate<typename promote<double, 1>::type, nullptr>(__v4997_extents), __v4997_extents };
    size_t __v4997_n = count_leaves<typename promote<double, 1>::type, nullptr>(__v4997_extents);
    std::copy_n(pool_base(u0.data), __v4997_n, pool_base(__v4997.data));
    for (int64_t __k4998 = 0L; __k4998 < 211L; __k4998++) {
        __v4996[0L][__k4998] = __v4997[__k4998];
    }
    for (int64_t __k4999 = 1L; __k4999 < 11L; __k4999++) {
        Array<double, 1> __v5000 = { __v4996.data[(__k4999 - 1L)], __v4996.extents + 1 };
        Array<double, 1> __v5001 = step(__v5000);
        for (int64_t __k5002 = 0L; __k5002 < 211L; __k5002++) {
            __v4996[__k4999][__k5002] = __v5001[__k5002];
        }
        deallocate<typename promote<double, 1>::type, nullptr>(__v5001.data, __v5001.extents);
    }
    Array<double, 2> __v5003 = __v4996;
    auto __wrap_5119___ret1073741827 = [&](double a, double b) { return __lambda_5119(a, b); };
    double __ret1073741827 = 0.0;
    /* reduce over computation: accumulator loop (omp simd reduction, licensed reorder, 1 operand stream) */
    {
        const size_t __rhi = 11;
        BLADE_OMP_SIMD_REDUCTION(+:__ret1073741827)
        for (size_t __i0 = 0; __i0 < __rhi; __i0++) {
            Array<double, 1> __v5003____i0 = { __v5003.data[__i0], __v5003.extents + 1 };
            __ret1073741827 = __ret1073741827 + [&]() { const size_t __pn = __v5003____i0.extents[0]; double __ps = 0; BLADE_OMP_SIMD_REDUCTION(+:__ps) for (size_t __pt = 0; __pt < __pn; __pt++) {     __ps = __ps + __v5003____i0[__pt] * __v5003____i0[__pt]; } return __ps; }();
        }
    }
    deallocate<typename promote<double, 1>::type, nullptr>(__v4997.data, __v4997.extents);
    deallocate<typename promote<double, 2>::type, nullptr>(__v4996.data, __v4996.extents);
    return __ret1073741827;
}

double __lambda_5015(X i) {
    BLADE_FRAME("lambda in inputs", nullptr, 0);
    return gen(static_cast<int64_t>(i), 10000019L, 0.0, 2.0);
}

double __lambda_5016(double __zl, double __zr) {
    return (__zl - __zr);
}

double __lambda_5017(double __zl, double __zr) {
    return (__zl * __zr);
}

double __lambda_5018(double __zl, double __zr) {
    return (__zl * __zr);
}

double __lambda_5019(double __zl, double __zr) {
    return (__zl * __zr);
}

double __lambda_5020(double __zl, double __zr) {
    return (__zl * __zr);
}

double __lambda_5021(double __zl, double __zr) {
    return (__zl * __zr);
}

double __lambda_5022(double __zl, double __zr) {
    return (__zl * __zr);
}

double __lambda_5023(double __zl, double __zr) {
    return (__zl + __zr);
}

double __lambda_5024(double __bx) {
    return (0.005 * __bx);
}

double __lambda_5025(double __bx) {
    return (0.005 * __bx);
}

double __lambda_5026(double __zl, double __zr) {
    return (__zl + __zr);
}

double __lambda_5027(double __bx) {
    return (0.005 * __bx);
}

double __lambda_5028(double __bx) {
    return (0.005 * __bx);
}

double __lambda_5029(double __zl, double __zr) {
    return (__zl + __zr);
}

double __lambda_5030(double __bx) {
    return (0.01 * __bx);
}

double __lambda_5031(double __bx) {
    return (0.01 * __bx);
}

double __lambda_5032(double __zl, double __zr) {
    return (__zl + __zr);
}

double __lambda_5033(double __bx) {
    return ((0.01 / 6.0) * __bx);
}

double __lambda_5034(double __zl, double __zr) {
    return (__zl + __zr);
}

double __lambda_5035(double __zl, double __zr) {
    return (__zl + __zr);
}

double __lambda_5036(double __zl, double __zr) {
    return (__zl + __zr);
}

double __lambda_5037(double __bx) {
    return (2.0 * __bx);
}

double __lambda_5038(double __bx) {
    return (2.0 * __bx);
}

double __lambda_5039(double __bx) {
    return (2.0 * __bx);
}

double __lambda_5040(double __zl, double __zr) {
    return (__zl + __zr);
}

double __lambda_5041(double __bx) {
    return (2.0 * __bx);
}

double __lambda_5042(double __bx) {
    return (2.0 * __bx);
}

double __lambda_5043(double __bx) {
    return (2.0 * __bx);
}

double __lambda_5044(double __zl, double __zr) {
    return (__zl + __zr);
}

double __lambda_5045(double __zl, double __zr) {
    return (__zl + __zr);
}

double __lambda_5046(double __bx) {
    return (2.0 * __bx);
}

double __lambda_5047(double __bx) {
    return (2.0 * __bx);
}

double __lambda_5048(double __bx) {
    return (2.0 * __bx);
}

double __lambda_5049(double __zl, double __zr) {
    return (__zl + __zr);
}

double __lambda_5050(double __bx) {
    return (2.0 * __bx);
}

double __lambda_5051(double __bx) {
    return (2.0 * __bx);
}

double __lambda_5052(double __bx) {
    return (2.0 * __bx);
}

double __lambda_5053(double __zl, double __zr) {
    return (__zl + __zr);
}

double __lambda_5054(double __zl, double __zr) {
    return (__zl + __zr);
}

double __lambda_5055(double __zl, double __zr) {
    return (__zl + __zr);
}

double __lambda_5056(double __bx) {
    return (2.0 * __bx);
}

double __lambda_5057(double __bx) {
    return (2.0 * __bx);
}

double __lambda_5058(double __bx) {
    return (2.0 * __bx);
}

double __lambda_5059(double __zl, double __zr) {
    return (__zl + __zr);
}

double __lambda_5060(double __bx) {
    return (2.0 * __bx);
}

double __lambda_5061(double __bx) {
    return (2.0 * __bx);
}

double __lambda_5062(double __bx) {
    return (2.0 * __bx);
}

double __lambda_5063(double __zl, double __zr) {
    return (__zl + __zr);
}

double __lambda_5064(double __zl, double __zr) {
    return (__zl + __zr);
}

double __lambda_5065(double __bx) {
    return (2.0 * __bx);
}

double __lambda_5066(double __bx) {
    return (2.0 * __bx);
}

double __lambda_5067(double __bx) {
    return (2.0 * __bx);
}

double __lambda_5068(double __zl, double __zr) {
    return (__zl + __zr);
}

double __lambda_5069(double __bx) {
    return (2.0 * __bx);
}

double __lambda_5070(double __bx) {
    return (2.0 * __bx);
}

double __lambda_5071(double __bx) {
    return (2.0 * __bx);
}

double __lambda_5072(double __bx) {
    return ((0.01 / 6.0) * __bx);
}

double __lambda_5073(double __zl, double __zr) {
    return (__zl + __zr);
}

double __lambda_5074(double __zl, double __zr) {
    return (__zl + __zr);
}

double __lambda_5075(double __zl, double __zr) {
    return (__zl + __zr);
}

double __lambda_5076(double __bx) {
    return (2.0 * __bx);
}

double __lambda_5077(double __bx) {
    return (2.0 * __bx);
}

double __lambda_5078(double __bx) {
    return (2.0 * __bx);
}

double __lambda_5079(double __zl, double __zr) {
    return (__zl + __zr);
}

double __lambda_5080(double __bx) {
    return (2.0 * __bx);
}

double __lambda_5081(double __bx) {
    return (2.0 * __bx);
}

double __lambda_5082(double __bx) {
    return (2.0 * __bx);
}

double __lambda_5083(double __zl, double __zr) {
    return (__zl + __zr);
}

double __lambda_5084(double __zl, double __zr) {
    return (__zl + __zr);
}

double __lambda_5085(double __bx) {
    return (2.0 * __bx);
}

double __lambda_5086(double __bx) {
    return (2.0 * __bx);
}

double __lambda_5087(double __bx) {
    return (2.0 * __bx);
}

double __lambda_5088(double __zl, double __zr) {
    return (__zl + __zr);
}

double __lambda_5089(double __bx) {
    return (2.0 * __bx);
}

double __lambda_5090(double __bx) {
    return (2.0 * __bx);
}

double __lambda_5091(double __bx) {
    return (2.0 * __bx);
}

double __lambda_5092(double __zl, double __zr) {
    return (__zl + __zr);
}

double __lambda_5093(double __zl, double __zr) {
    return (__zl + __zr);
}

double __lambda_5094(double __zl, double __zr) {
    return (__zl + __zr);
}

double __lambda_5095(double __bx) {
    return (2.0 * __bx);
}

double __lambda_5096(double __bx) {
    return (2.0 * __bx);
}

double __lambda_5097(double __bx) {
    return (2.0 * __bx);
}

double __lambda_5098(double __zl, double __zr) {
    return (__zl + __zr);
}

double __lambda_5099(double __bx) {
    return (2.0 * __bx);
}

double __lambda_5100(double __bx) {
    return (2.0 * __bx);
}

double __lambda_5101(double __bx) {
    return (2.0 * __bx);
}

double __lambda_5102(double __zl, double __zr) {
    return (__zl + __zr);
}

double __lambda_5103(double __zl, double __zr) {
    return (__zl + __zr);
}

double __lambda_5104(double __bx) {
    return (2.0 * __bx);
}

double __lambda_5105(double __bx) {
    return (2.0 * __bx);
}

double __lambda_5106(double __bx) {
    return (2.0 * __bx);
}

double __lambda_5107(double __zl, double __zr) {
    return (__zl + __zr);
}

double __lambda_5108(double __bx) {
    return (2.0 * __bx);
}

double __lambda_5109(double __bx) {
    return (2.0 * __bx);
}

double __lambda_5110(double __bx) {
    return (2.0 * __bx);
}

double __lambda_5116(Array<double, 1> row) {
    BLADE_FRAME("lambda in kernel", nullptr, 0);
    return [&]() { const size_t __pn = row.extents[0]; double __ps = 0; BLADE_OMP_SIMD_REDUCTION(+:__ps) for (size_t __pt = 0; __pt < __pn; __pt++) {     __ps = __ps + row[__pt] * row[__pt]; } return __ps; }();
}

double __lambda_5119(double a, double b) {
    return (a + b);
}

double __lambda_5122(double a, double b) {
    return (a + b);
}

double __lambda_5132(double __zl, double __fz5129, double __fz5130, double __fz5131) {
    return (__zl - ((__fz5129 * __fz5130) * __fz5131));
}

double __lambda_5134(double __zl, double __fz5133) {
    return (__zl + (0.005 * __fz5133));
}

double __lambda_5136(double __zl, double __fz5135) {
    return (__zl + (0.005 * __fz5135));
}

double __lambda_5138(double __zl, double __fz5137) {
    return (__zl + (0.01 * __fz5137));
}

double __lambda_5233(double __zl, double __fz5229, double __fz5230, double __fz5231, double __fz5232) {
    return (__zl + ((0.01 / 6.0) * (((__fz5229 + (2.0 * __fz5230)) + (2.0 * __fz5231)) + __fz5232)));
}

int main() {
    cout << std::setprecision(15);
    cout << std::boolalpha;
    blade_rt::typed_float_print(cout);
    auto __blade_t0 = TIME;

    try {
    Array<double, 1> __v5011 = inputs();
    std::tuple<double, double> __tup_5012 = std::make_tuple(kernel(__v5011), [&]() { auto __wrap_5122 = [&](double a, double b) { return __lambda_5122(a, b); }; const size_t __rn = 211; double __r = __v5011[0]; const size_t __rlo = 1; const size_t __rcnt = (__rn > __rlo) ? (__rn - __rlo) : (size_t)0; if (__rcnt < (size_t)8) { for (size_t __ri = __rlo; __ri < __rn; __ri++) { __r = __wrap_5122(__r, __v5011[__ri]); } return __r; } double __rlane0 = __v5011[__rlo + 0]; double __rlane1 = __v5011[__rlo + 1]; double __rlane2 = __v5011[__rlo + 2]; double __rlane3 = __v5011[__rlo + 3]; double __rlane4 = __v5011[__rlo + 4]; double __rlane5 = __v5011[__rlo + 5]; double __rlane6 = __v5011[__rlo + 6]; double __rlane7 = __v5011[__rlo + 7]; size_t __ri = __rlo + 8; for (; __ri + 8 <= __rn; __ri += 8) {     __rlane0 = __wrap_5122(__rlane0, __v5011[__ri + 0]);     __rlane1 = __wrap_5122(__rlane1, __v5011[__ri + 1]);     __rlane2 = __wrap_5122(__rlane2, __v5011[__ri + 2]);     __rlane3 = __wrap_5122(__rlane3, __v5011[__ri + 3]);     __rlane4 = __wrap_5122(__rlane4, __v5011[__ri + 4]);     __rlane5 = __wrap_5122(__rlane5, __v5011[__ri + 5]);     __rlane6 = __wrap_5122(__rlane6, __v5011[__ri + 6]);     __rlane7 = __wrap_5122(__rlane7, __v5011[__ri + 7]); } if (__ri < __rn) { __rlane0 = __wrap_5122(__rlane0, __v5011[__ri]); __ri++; } if (__ri < __rn) { __rlane1 = __wrap_5122(__rlane1, __v5011[__ri]); __ri++; } if (__ri < __rn) { __rlane2 = __wrap_5122(__rlane2, __v5011[__ri]); __ri++; } if (__ri < __rn) { __rlane3 = __wrap_5122(__rlane3, __v5011[__ri]); __ri++; } if (__ri < __rn) { __rlane4 = __wrap_5122(__rlane4, __v5011[__ri]); __ri++; } if (__ri < __rn) { __rlane5 = __wrap_5122(__rlane5, __v5011[__ri]); __ri++; } if (__ri < __rn) { __rlane6 = __wrap_5122(__rlane6, __v5011[__ri]); __ri++; } __rlane0 = __wrap_5122(__rlane0, __rlane1); __rlane0 = __wrap_5122(__rlane0, __rlane2); __rlane0 = __wrap_5122(__rlane0, __rlane3); __rlane0 = __wrap_5122(__rlane0, __rlane4); __rlane0 = __wrap_5122(__rlane0, __rlane5); __rlane0 = __wrap_5122(__rlane0, __rlane6); __rlane0 = __wrap_5122(__rlane0, __rlane7); __r = __wrap_5122(__r, __rlane0); return __r; }());
    
    double checksum = std::get<0>(__tup_5012);
    
    double input_checksum = std::get<1>(__tup_5012);
    

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