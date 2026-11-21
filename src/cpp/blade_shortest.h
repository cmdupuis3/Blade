/* blade_shortest.h -- the DISPLAY print form's number spelling (deep review
 * P3-26; src/PrintForm.fs): a Float64 / Float32 at the SHORTEST decimal that
 * parses back to the same bits at its own width, always visibly a float.
 *
 * ONE C text for both compiled lanes: blade_runtime.hpp includes it (the C++
 * lane's float_put facet and fmt_f32 call it under the display form) and so
 * does blade_llvm_shim.c (blade_fmt_f64 / blade_fmt_f32). The interpreter's
 * twin is Interp/CppFormat.formatDisplayF64 / formatDisplayF32 (.NET's
 * shortest round-trip "R" digits laid out by the same rule); `blade test
 * print-form` compares the three lanes over a battery of values.
 *
 * DIGITS. The shortest p in [1, 17] (a float: [1, 9]) for which SOME p-digit
 * decimal parses back to the value (blade_shortest_try: the correctly rounded
 * one, else its neighbour across the value): that is monotone in p (a p-digit
 * decimal is a (p + 1)-digit one), so a binary search finds it; the decimal
 * chosen is the nearest that reads back -- the digits std::to_chars and .NET's
 * "R" choose.
 *
 * LAYOUT. The display-frame JSON rule (blade_display::jsonfloat,
 * CppFormat.assembleShortest): fixed notation for a decimal exponent x in
 * [-4, 17), scientific `d[.ddd]e{+|-}dd` (two exponent digits at least)
 * otherwise. Then the typed float spelling every print form keeps: a bare
 * digit run gains ".0" (`2.0`, `100.0`), a zero is `0.0` / `-0.0`, a NaN is
 * `nan`, an infinity `inf` / `-inf`. Complex values print their parts so.
 *
 * C99 and C++ alike; every function static inline. */
#ifndef BLADE_SHORTEST_H
#define BLADE_SHORTEST_H

#include <math.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

/* The layout of a "%.{p-1}e" rendering `sci` (finite, nonzero; sign
 * included) into `out` (cap >= 40), then the float marker. */
static inline void blade_shortest_layout(const char *sci, char *out, size_t cap) {
    char digits[32];
    size_t nd = 0, k = 0;
    int neg = 0;
    if (sci[0] == '-') { neg = 1; k = 1; }
    while (sci[k] && sci[k] != 'e' && sci[k] != 'E') {
        if (sci[k] >= '0' && sci[k] <= '9' && nd + 1 < sizeof digits) digits[nd++] = sci[k];
        ++k;
    }
    while (nd > 1 && digits[nd - 1] == '0') --nd;
    digits[nd] = '\0';
    int x = (sci[k] == 'e' || sci[k] == 'E') ? atoi(sci + k + 1) : 0;
    size_t o = 0;
#define BLADE_SH_PUT(ch) do { if (o + 1 < cap) out[o++] = (ch); } while (0)
    if (neg) BLADE_SH_PUT('-');
    if (x < -4 || x >= 17) {
        BLADE_SH_PUT(digits[0]);
        if (nd > 1) {
            BLADE_SH_PUT('.');
            for (size_t i = 1; i < nd; ++i) BLADE_SH_PUT(digits[i]);
        }
        BLADE_SH_PUT('e');
        BLADE_SH_PUT(x < 0 ? '-' : '+');
        int a = x < 0 ? -x : x;
        char eb[8];
        int en = snprintf(eb, sizeof eb, "%02d", a);
        for (int i = 0; i < en; ++i) BLADE_SH_PUT(eb[i]);
    } else if (x >= 0) {
        size_t intLen = (size_t)x + 1;
        for (size_t i = 0; i < intLen; ++i) BLADE_SH_PUT(i < nd ? digits[i] : '0');
        if (nd > intLen) {
            BLADE_SH_PUT('.');
            for (size_t i = intLen; i < nd; ++i) BLADE_SH_PUT(digits[i]);
        } else {
            /* a bare digit run is an integer's spelling: the float marker */
            BLADE_SH_PUT('.');
            BLADE_SH_PUT('0');
        }
    } else {
        BLADE_SH_PUT('0');
        BLADE_SH_PUT('.');
        for (int i = 0; i < -x - 1; ++i) BLADE_SH_PUT('0');
        for (size_t i = 0; i < nd; ++i) BLADE_SH_PUT(digits[i]);
    }
#undef BLADE_SH_PUT
    out[o < cap ? o : cap - 1] = '\0';
}

/* Is there a p-digit decimal that reads back to `v`? The correctly rounded
 * one is the nearest; when it misses -- at a power of two the rounding
 * interval is asymmetric (Codex review: 2^-44 = 5.684341886080802e-14, whose
 * nearest 16-digit decimal does not read back) -- the only other candidate
 * is its neighbour on the OTHER side of `v` (one unit in the last digit:
 * anything between it and `v` would be nearer than the nearest). On success
 * the decimal's "%.{p-1}e" text is in `sci`. `is_float`: read back with
 * strtof. */
static inline int blade_shortest_try(double v, int is_float, int p, char *sci, size_t cap) {
    snprintf(sci, cap, "%.*e", p - 1, v);
    if (is_float ? (strtof(sci, NULL) == (float)v) : (strtod(sci, NULL) == v)) return 1;
    /* the mantissa as a p-digit integer M and the decimal exponent X */
    const char *s = sci;
    int neg = 0;
    if (*s == '-') { neg = 1; ++s; }
    unsigned long long m = 0;
    for (; *s && *s != 'e' && *s != 'E'; ++s)
        if (*s >= '0' && *s <= '9') m = m * 10ull + (unsigned long long)(*s - '0');
    int x = (*s == 'e' || *s == 'E') ? atoi(s + 1) : 0;
    unsigned long long lo = 1, hi;
    for (int k = 1; k < p; ++k) lo *= 10ull;   /* 10^(p-1) */
    hi = lo * 10ull;                            /* 10^p */
    double back = strtod(sci, NULL);
    /* the neighbour on the other side of v from the rounded decimal */
    int up = ((neg ? -back : back) < (neg ? -v : v)) ? 1 : 0;
    unsigned long long m2 = up ? m + 1 : m - 1;
    int x2 = x;
    if (m2 >= hi) { m2 = lo; x2 = x + 1; }
    else if (m2 < lo) { m2 = hi - 1; x2 = x - 1; }
    char digits[24];
    snprintf(digits, sizeof digits, "%llu", m2);
    size_t o = 0;
    if (neg && o + 1 < cap) sci[o++] = '-';
    for (size_t k = 0; digits[k] && o + 1 < cap; ++k) {
        sci[o++] = digits[k];
        if (k == 0 && digits[1] && o + 1 < cap) sci[o++] = '.';
    }
    if (o + 8 < cap) o += (size_t)snprintf(sci + o, cap - o, "e%+03d", x2);
    sci[o < cap ? o : cap - 1] = '\0';
    return is_float ? (strtof(sci, NULL) == (float)v) : (strtod(sci, NULL) == v);
}

/* The display spelling of a double into `out` (cap >= 40). */
static inline void blade_fmt_display_f64(char *out, size_t cap, double v) {
    if (isnan(v)) { snprintf(out, cap, "nan"); return; }
    if (isinf(v)) { snprintf(out, cap, v < 0.0 ? "-inf" : "inf"); return; }
    if (v == 0.0) { snprintf(out, cap, signbit(v) ? "-0.0" : "0.0"); return; }
    char sci[48];
    int lo = 1, hi = 17;
    while (lo < hi) {
        int mid = (lo + hi) / 2;
        if (blade_shortest_try(v, 0, mid, sci, sizeof sci)) hi = mid; else lo = mid + 1;
    }
    blade_shortest_try(v, 0, lo, sci, sizeof sci);
    blade_shortest_layout(sci, out, cap);
}

/* The display spelling of a float: its OWN shortest digits (0.1f is 0.1,
 * 1.0f / 3.0f is 0.33333334), not its double expansion's. */
static inline void blade_fmt_display_f32(char *out, size_t cap, float v) {
    if (isnan(v)) { snprintf(out, cap, "nan"); return; }
    if (isinf(v)) { snprintf(out, cap, v < 0.0f ? "-inf" : "inf"); return; }
    if (v == 0.0f) { snprintf(out, cap, signbit(v) ? "-0.0" : "0.0"); return; }
    char sci[48];
    int lo = 1, hi = 9;
    while (lo < hi) {
        int mid = (lo + hi) / 2;
        if (blade_shortest_try((double)v, 1, mid, sci, sizeof sci)) hi = mid; else lo = mid + 1;
    }
    blade_shortest_try((double)v, 1, lo, sci, sizeof sci);
    blade_shortest_layout(sci, out, cap);
}
#endif /* BLADE_SHORTEST_H */
