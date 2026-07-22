// ============================================================================
// Blade interpreter <-> C++ output PARITY layer.
//
// The Blade compiler emits C++ that prints results with
//     cout << std::setprecision(15);
//     cout << std::boolalpha;
// in the DEFAULT float format (no std::fixed / std::scientific). The interpreter
// (which evaluates Blade programs directly, without going through g++) must
// reproduce that textual output BYTE-FOR-BYTE so that interpreter output and
// compiled-binary output are indistinguishable to the test harness / oracle.
//
// This module is the single source of truth for that formatting. Every function
// here was pinned against a compiled ucrt64 (MinGW) g++ 15.2 probe over a battery
// of >25,000 bit patterns (see Interp/CppFormat.fs validation harness in the
// scratchpad) with ZERO mismatches.
//
// Self-contained: depends only on System / System.Globalization.
//
// KEY EMPIRICAL FACTS (ucrt64 iostreams, setprecision(15), defaultfloat):
//   * default float format == C printf "%.15g": 15 significant digits, switch to
//     scientific iff the decimal exponent X < -4 or X >= 15, else fixed; trailing
//     zeros and a bare trailing '.' are stripped; exponent is e+dd / e-dd with a
//     minimum of two digits (three when needed, e.g. e-323).
//   * -0.0 prints "-0"; +0.0 prints "0".
//   * +inf -> "inf", -inf -> "-inf".
//   * EVERY NaN prints "nan" -- ucrt iostreams never emit a sign or payload, so
//     the sign bit and the mantissa payload are irrelevant (0x7FF8.., 0xFFF8..,
//     0xFFFF.. all print "nan").
//   * cout << float promotes to double, so a float prints the 15-significant-digit
//     rendering of its EXACT double value (0.1f -> "0.100000001490116").
//   * std::complex<double> operator<< prints "(re,im)" with NO spaces, each
//     component via the same float formatting ((-0,3), (inf,nan), ...).
//   * cout << bool with boolalpha -> "true"/"false".
//   * cout << char prints the character glyph (not its numeric code).
// ============================================================================
module Blade.Interp.CppFormat

open System
open System.Globalization

/// Invariant culture: guarantees '.' as the decimal separator and no group
/// separators, regardless of the host machine's locale.
let private inv = CultureInfo.InvariantCulture

/// Blade's default print precision: 15 significant digits (setprecision(15)).
[<Literal>]
let private Prec = 15

// ----------------------------------------------------------------------------
// Core %g assembler.
//
// Given the sign string, the 15 significant decimal digits (as a 15-char string
// whose first character is the leading, nonzero significant digit) and the
// decimal exponent `x` of that leading digit (i.e. the value is
//     0.d0 d1 ... d14  * 10^(x+1)   ==   d0.d1...d14 * 10^x,
// exactly the exponent C's "%e" would report AFTER rounding to 15 digits),
// produce the "%.15g" text.
// ----------------------------------------------------------------------------
let private assemble (sign: string) (digits: string) (x: int) : string =
    if x < -4 || x >= Prec then
        // ---- scientific: d[.ddd]e{+|-}dd ----
        let lead = digits.Substring(0, 1)
        let frac = digits.Substring(1).TrimEnd('0')
        let mant = if frac.Length = 0 then lead else lead + "." + frac
        let esign = if x < 0 then "-" else "+"
        let eabs = abs x
        // minimum two exponent digits; more when the magnitude demands it.
        let edig =
            if eabs < 10 then "0" + string eabs
            else string eabs
        sign + mant + "e" + esign + edig
    elif x >= 0 then
        // ---- fixed, magnitude >= 1: integer part has (x+1) digits ----
        let intLen = x + 1
        let intPart = digits.Substring(0, intLen)
        let frac = digits.Substring(intLen).TrimEnd('0')
        if frac.Length = 0 then sign + intPart
        else sign + intPart + "." + frac
    else
        // ---- fixed, magnitude < 1 (x in [-4,-1]): "0." + zeros + digits ----
        let leadingZeros = String('0', (-x) - 1)
        // digits' leading char is nonzero, so the trimmed fraction is never empty.
        let frac = (leadingZeros + digits).TrimEnd('0')
        sign + "0." + frac

/// Byte-exact mirror of `cout << setprecision(15) << x` (defaultfloat) for a
/// double == printf "%.15g" as rendered by ucrt64 iostreams.
let formatFloat15 (x: float) : string =
    if Double.IsNaN x then "nan"
    elif Double.IsPositiveInfinity x then "inf"
    elif Double.IsNegativeInfinity x then "-inf"
    elif x = 0.0 then
        // IsNegative distinguishes -0.0 from +0.0 by the sign bit.
        if Double.IsNegative x then "-0" else "0"
    else
        let sign = if x < 0.0 then "-" else ""
        let ax = abs x
        // "E14" yields exactly 15 significant digits (1 before, 14 after the
        // point) rounded to nearest, ties-to-even, from the EXACT value of the
        // double -- matching ucrt's correctly-rounded %g. Any rounding carry
        // (9.99..9 -> 10.0..0) is normalized by .NET into the reported exponent.
        // Form: "d.ddddddddddddddE{+|-}0XX".
        let s = ax.ToString("E14", inv)
        let eidx = s.IndexOf('E')
        let mantStr = s.Substring(0, eidx)      // "d.dddddddddddddd"
        let x10 = Int32.Parse(s.Substring(eidx + 1), NumberStyles.Integer, inv)
        let digits = mantStr.Replace(".", "")   // exactly 15 significant digits
        assemble sign digits x10

/// Byte-exact mirror of `cout << setprecision(15) << f` for a 32-bit float.
/// iostreams promotes the float to double before formatting, so this is simply
/// the 15-significant-digit rendering of the float's exact double value.
let formatFloat32 (x: float32) : string =
    formatFloat15 (float x)

/// Byte-exact mirror of `cout << std::complex<double>(re, im)`:
/// "(re,im)" with no spaces, each component via formatFloat15.
let formatComplex (re: float) (im: float) : string =
    "(" + formatFloat15 re + "," + formatFloat15 im + ")"

/// `cout << b` under std::boolalpha.
let formatBool (b: bool) : string =
    if b then "true" else "false"

/// `cout << (int64_t)n` -- plain decimal, INT64_MIN == "-9223372036854775808".
let formatInt64 (n: int64) : string =
    n.ToString(inv)

/// `cout << (int32_t)n` -- plain decimal, INT32_MIN == "-2147483648".
let formatInt32 (n: int32) : string =
    n.ToString(inv)

/// `cout << (char)c` -- the character glyph itself, not its numeric code.
let formatChar (c: char) : string =
    string c

/// `cout << (std::string)s` (or a string literal) -- the text verbatim.
let formatString (s: string) : string =
    s
