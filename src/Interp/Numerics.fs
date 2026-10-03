// Blade tree-walking interpreter: scalar numerics (Milestone 1 foundation).
//
// Bit-exact reimplementation of the scalar arithmetic that CodeGen emits, so the
// interpreter's printed output byte-matches the g++ -std=c++17 -O2 binaries (the
// differential gate). Every rule here was pinned empirically against the actual
// toolchain (MSYS2 ucrt64 g++ 15.2 + .NET 10 on Windows). The mechanisms:
//
//   * Real binops mirror C++ usual-arithmetic-conversion promotion and integer
//     wraparound/truncation. The one non-obvious case: `int64 op float32` is
//     computed in `float` (C++ converts the int64 to float, not double), then
//     widened to the Float64 result type. cppArithElem encodes the C++
//     conversion; the Blade node type (IR.promoteElemType) applies afterward.
//   * Complex128 +,-,*,/ replicate libgcc __muldc3 (naive product + Annex-G NaN
//     recovery) and __divdc3 (Smith scaled division + recovery), verified
//     bit-for-bit incl. the NaN/inf recovery paths and NaN sign bits.
//   * complex-mixed-with-real uses std::complex's scalar overloads (component
//     scale / real-part-only add), not full complex arithmetic -- CodeGen leaves
//     a real operand un-promoted (coerceComplexOperand), so C++ resolves the
//     mixed overload. Diverges from full complex on signed-zero / non-finite.
//   * Scalar libm intrinsics: the compiled side calls the platform libm AT RUN
//     TIME through blade_libm:: (src/cpp/blade_runtime.hpp) -- a non-builtin
//     name g++ cannot constant-fold through MPFR, which it used to do for
//     literal arguments (docs/formalism.md section 2.4) -- and here both
//     g++'s libm and .NET Math.* bottom out in ucrtbase, so they are
//     bit-identical. The lone exception
//     is hypot (no .NET managed equivalent; naive sqrt(x*x+y*y) diverges) --
//     routed through the platform libm, which the `Ucrt` module below binds by
//     a logical name so the OS decides the file. Backend choice is a data table (mathBackend),
//     so any function can be re-pinned to the ucrt shim if a future battery
//     reveals a managed divergence.
//   * lgamma and digamma are the intrinsics with NEITHER escape hatch: .NET has
//     no gamma function to call, ucrtbase has no digamma at all, and no library
//     is shared with the compiled side. Codegen therefore emits neither
//     std::lgamma nor any libm psi -- both sides run the same hand-rolled series
//     (lgammaLanczos / digammaSeries here, blade_rt::lgamma / blade_rt::digamma
//     in src/cpp/blade_runtime.hpp), which must be kept identical by hand.
//
// Compiled inside Blade.fsproj after IR.fs/CodeGen.fs. Depends on Value.fs, the
// IR op discriminators (IRBinOp/IRUnaryOp, IR.fs:25/36), ElemType (Types.fs:285),
// and IR.promoteElemType (IR.fs:417, reused so the interpreter cannot drift from
// it). The bit-critical core (complex ops, math dispatch, cppArithElem) is
// written over plain float/int so it is testable standalone via dotnet fsi.
module Blade.Interp.Numerics

open System
open System.Reflection
open System.Runtime.InteropServices
open Blade.Types
open Blade.IR
open Blade.Interp.Value

/// Logical import name for the platform's libm. No file is called this on any
/// OS -- the resolver registered immediately below maps it to the real one.
/// The externs cannot name the real library directly because a DllImport
/// string is baked at compile time while the library differs per OS.
[<Literal>]
let private LibmLibrary = Blade.Platforms.libmImportName

// Bind `blade_libm` to whatever this OS calls its C-runtime math library
// (Blade.Platforms.libmName: ucrtbase.dll / libm.so.6 / libSystem.dylib).
//
// The resolver itself lives in Platforms.fs. It is per-ASSEMBLY and the
// runtime permits exactly one registration, so libm shares it with the netcdf
// provider's externs rather than the two racing for the single slot; see
// `Platforms.ensureNativeResolver`. Forced at this module's static
// initialization, which is ordered before any function below can run and
// therefore before the first extern call.
do Blade.Platforms.ensureNativeResolver ()

// Shims for the platform libm -- the exact library the locally compiled g++
// binary calls, whichever OS that is. On Windows MinGW ucrt64's libstdc++
// forwards <cmath> to ucrtbase, so calling ucrtbase directly is provably
// identical to the compiled binary; verified bit-for-bit over a 284-value
// battery for every function below. glibc and libSystem stand in the same
// relation to their platform's g++/clang++ output.
//
// Byte-identity is therefore a PER-PLATFORM claim, not a cross-platform one:
// what the differential gate asserts is that the interpreter and the LOCALLY
// compiled binary agree, because both bottom out in the same libm. Two
// different OSes may legitimately disagree in the last ULP of a transcendental
// -- that is a property of their libms, not a regression here.
//
// The module keeps its historical name (Windows/ucrtbase was the platform
// every rule here was pinned on); the binding it uses is `LibmLibrary` above.
module private Ucrt =
    [<DllImport(LibmLibrary, CallingConvention=CallingConvention.Cdecl)>] extern double exp(double x)
    [<DllImport(LibmLibrary, CallingConvention=CallingConvention.Cdecl)>] extern double log(double x)
    [<DllImport(LibmLibrary, CallingConvention=CallingConvention.Cdecl)>] extern double log10(double x)
    [<DllImport(LibmLibrary, CallingConvention=CallingConvention.Cdecl)>] extern double sqrt(double x)
    [<DllImport(LibmLibrary, CallingConvention=CallingConvention.Cdecl)>] extern double sin(double x)
    [<DllImport(LibmLibrary, CallingConvention=CallingConvention.Cdecl)>] extern double cos(double x)
    [<DllImport(LibmLibrary, CallingConvention=CallingConvention.Cdecl)>] extern double tan(double x)
    [<DllImport(LibmLibrary, CallingConvention=CallingConvention.Cdecl)>] extern double sinh(double x)
    [<DllImport(LibmLibrary, CallingConvention=CallingConvention.Cdecl)>] extern double cosh(double x)
    [<DllImport(LibmLibrary, CallingConvention=CallingConvention.Cdecl)>] extern double tanh(double x)
    [<DllImport(LibmLibrary, CallingConvention=CallingConvention.Cdecl)>] extern double asin(double x)
    [<DllImport(LibmLibrary, CallingConvention=CallingConvention.Cdecl)>] extern double acos(double x)
    [<DllImport(LibmLibrary, CallingConvention=CallingConvention.Cdecl)>] extern double atan(double x)
    [<DllImport(LibmLibrary, CallingConvention=CallingConvention.Cdecl)>] extern double floor(double x)
    [<DllImport(LibmLibrary, CallingConvention=CallingConvention.Cdecl)>] extern double ceil(double x)
    [<DllImport(LibmLibrary, CallingConvention=CallingConvention.Cdecl)>] extern double fabs(double x)
    [<DllImport(LibmLibrary, CallingConvention=CallingConvention.Cdecl)>] extern double pow(double x, double y)
    [<DllImport(LibmLibrary, CallingConvention=CallingConvention.Cdecl)>] extern double atan2(double y, double x)
    [<DllImport(LibmLibrary, CallingConvention=CallingConvention.Cdecl, EntryPoint="hypot")>] extern double hypot(double x, double y)
    // Only for the mingw-w64 catanh port below (its log1p call binds here).
    [<DllImport(LibmLibrary, CallingConvention=CallingConvention.Cdecl)>] extern double log1p(double x)

/// Per-intrinsic backend. `Managed` = .NET Math.* (exact/correctly-rounded and
/// identical to ucrt for these; cheaper, no marshalling). `Ucrt` = the
/// platform libm P/Invoke (provably what g++ calls -- ucrtbase.dll on Windows,
/// which is what the name records). `BladeRt` = neither library, but a
/// series hand-rolled IDENTICALLY here and in src/cpp/blade_runtime.hpp,
/// for the intrinsics where no shared library exists to borrow. The choice
/// is data -- flip an entry to Ucrt to eliminate any residual
/// managed-divergence risk for that function.
type MathBackend =
    | Managed
    | Ucrt
    | BladeRt

/// Backend selection. Transcendentals + pow/atan2/hypot default to Ucrt (the
/// zero-risk, provably-g++-identical path); the exact algebraic/rounding ops
/// (sqrt, floor, ceil) use Managed (IEEE-correctly-rounded => identical
/// everywhere, and avoid marshalling). hypot has NO managed equivalent, so it is
/// Ucrt unconditionally. lgamma and digamma have neither a managed equivalent
/// NOR a shared library with the compiled side (codegen emits blade_rt:: for
/// them) -- both sides run the same hand-rolled series, hence BladeRt.
let mathBackend : Map<string, MathBackend> =
    Map.ofList [
        "exp", Ucrt;  "log", Ucrt;   "log10", Ucrt
        "sin", Ucrt;  "cos", Ucrt;  "tan", Ucrt
        "sinh", Ucrt; "cosh", Ucrt;  "tanh", Ucrt
        "asin", Ucrt; "acos", Ucrt;  "atan", Ucrt
        "sqrt", Managed; "floor", Managed; "ceil", Managed
        "pow", Ucrt;  "atan2", Ucrt; "hypot", Ucrt
        "lgamma", BladeRt; "digamma", BladeRt ]

let private managed1 (name: string) (x: float) : float =
    match name with
    | "exp" -> Math.Exp x | "log" -> Math.Log x | "log10" -> Math.Log10 x
    | "sqrt" -> Math.Sqrt x
    | "sin" -> Math.Sin x | "cos" -> Math.Cos x | "tan" -> Math.Tan x
    | "sinh" -> Math.Sinh x | "cosh" -> Math.Cosh x | "tanh" -> Math.Tanh x
    | "asin" -> Math.Asin x | "acos" -> Math.Acos x | "atan" -> Math.Atan x
    | "floor" -> Math.Floor x | "ceil" -> Math.Ceiling x
    | "fabs" | "abs" -> Math.Abs x
    | _ -> nan

let private ucrt1 (name: string) (x: float) : float =
    match name with
    | "exp" -> Ucrt.exp x | "log" -> Ucrt.log x | "log10" -> Ucrt.log10 x
    | "sqrt" -> Ucrt.sqrt x
    | "sin" -> Ucrt.sin x | "cos" -> Ucrt.cos x | "tan" -> Ucrt.tan x
    | "sinh" -> Ucrt.sinh x | "cosh" -> Ucrt.cosh x | "tanh" -> Ucrt.tanh x
    | "asin" -> Ucrt.asin x | "acos" -> Ucrt.acos x | "atan" -> Ucrt.atan x
    | "floor" -> Ucrt.floor x | "ceil" -> Ucrt.ceil x
    | "fabs" | "abs" -> Ucrt.fabs x
    | _ -> nan

/// log Gamma(x) for x > 0: the Lanczos approximation (g = 7, n = 9),
/// transcribed statement for statement from `blade_rt::lgamma` in
/// src/cpp/blade_runtime.hpp -- read that comment for WHY this is hand-rolled
/// rather than a library call on either side (short version: .NET has no gamma
/// function at all, so there is no shared implementation to borrow the way
/// every other intrinsic here borrows ucrtbase). Same coefficients, same
/// association, same order: change one side and you MUST change the other, or
/// the interpreter stops being a byte-for-byte twin.
///
/// `log` is pinned to ucrtbase DIRECTLY rather than routed through `math1`,
/// because the C++ side calls std::log unconditionally -- what has to match is
/// the header, not the mathBackend entry for the separate `log(x)` intrinsic.
///
/// Domain: x > 0 (`not (x > 0.0)` also catches NaN). Non-positive PANICS, the
/// twin of the header's blade_rt::panic; the reflection formula is deliberately
/// absent, since log-densities never need it.
let lgammaLanczos (x: float) : float =
    if not (x > 0.0) then
        raise (InterpPanic("BL8008", "lgamma: argument must be positive", None, 0))
    // Gamma(1) = Gamma(2) = 1 exactly; the series lands a few ulp off zero.
    elif x = 1.0 || x = 2.0 then 0.0
    else
        // Denominators over x, not over z = x - 1: see the header's note on
        // the cancellation in `(x - 1) + k` for small x.
        let mutable s = 0.99999999999980993
        s <- s + 676.5203681218851     / x
        s <- s + -1259.1392167224028   / (x + 1.0)
        s <- s + 771.32342877765313    / (x + 2.0)
        s <- s + -176.61502916214059   / (x + 3.0)
        s <- s + 12.507343278686905    / (x + 4.0)
        s <- s + -0.13857109526572012  / (x + 5.0)
        s <- s + 9.9843695780195716e-6 / (x + 6.0)
        s <- s + 1.5056327351493116e-7 / (x + 7.0)
        let t = x + 6.5   // (x - 1) + g + 0.5, with g = 7
        // 0.9189385332046727 = log(2*pi) / 2
        0.9189385332046727 + (x - 0.5) * (Ucrt.log t) - t + (Ucrt.log s)

/// digamma(x) = psi(x) = d/dx log Gamma(x), for x > 0: recurrence down to the
/// asymptotic regime, then the Stirling-type asymptotic series. Transcribed
/// statement for statement from `blade_rt::digamma` in
/// src/cpp/blade_runtime.hpp -- read that comment for the method, for the
/// seven Bernoulli coefficients B_2n/(2n) and where they come from, and for
/// the measurements behind "shift to x >= 10, truncate after seven terms"
/// (worst 1.1e-15 over 206,001 probes, which is the recurrence's own
/// roundoff floor; the customary shift to x >= 6 leaves ~1e-13).
///
/// Same lockstep contract as lgammaLanczos above: same constants, same
/// association, same order, or the interpreter stops being a byte-for-byte
/// twin. The series is deliberately summed as `c / p` with `p <- p * x2`
/// rather than in Horner form, so there is no multiply-add pair on either
/// side and FMA contraction cannot come into it at all.
///
/// `log` is pinned to ucrtbase directly, for the same reason as in
/// lgammaLanczos: what must match is the header's std::log call, not the
/// mathBackend entry for the separate `log(x)` intrinsic.
///
/// Domain: x > 0 (`not (x > 0.0)` also catches NaN), panicking with the same
/// BL8008 as lgamma. Nothing is pinned at special points -- psi has no
/// rational value at any convenient argument, so unlike lgamma's exact zeros
/// at 1 and 2 there is nothing exact to return.
let digammaSeries (x: float) : float =
    if not (x > 0.0) then
        raise (InterpPanic("BL8008", "digamma: argument must be positive", None, 0))
    else
        // psi(x) = psi(x+1) - 1/x, applied until the asymptotic series is good.
        let mutable x = x
        let mutable r = 0.0
        while x < 10.0 do
            r <- r - 1.0 / x
            x <- x + 1.0
        let x2 = x * x
        let mutable p = x2                          // x^2, then x^4, x^6, ...
        let mutable s =    (1.0 / 12.0)     / p
        p <- p * x2
        s <- s +          (-1.0 / 120.0)    / p
        p <- p * x2
        s <- s +           (1.0 / 252.0)    / p
        p <- p * x2
        s <- s +          (-1.0 / 240.0)    / p
        p <- p * x2
        s <- s +           (1.0 / 132.0)    / p
        p <- p * x2
        s <- s +        (-691.0 / 32760.0)  / p
        p <- p * x2
        s <- s +           (1.0 / 12.0)     / p
        r + (Ucrt.log x) - 0.5 / x - s

/// The BladeRt backend's dispatch table (see lgammaLanczos / digammaSeries).
let private bladeRt1 (name: string) (x: float) : float =
    match name with
    | "lgamma" -> lgammaLanczos x
    | "digamma" -> digammaSeries x
    | _ -> nan

/// Apply a single-argument real intrinsic through its selected backend.
let math1 (name: string) (x: float) : float =
    match Map.tryFind name mathBackend with
    | Some Ucrt -> ucrt1 name x
    | Some BladeRt -> bladeRt1 name x
    | _ -> managed1 name x

/// b ^ e, matching CodeGen's `pow(l, r)` emission (unqualified `pow`).
let mathPow (b: float) (e: float) : float =
    match Map.tryFind "pow" mathBackend with
    | Some Ucrt -> Ucrt.pow(b, e)
    | _ -> Math.Pow(b, e)

/// atan2(y, x), backing complex `arg` and any surfaced atan2.
let mathAtan2 (y: float) (x: float) : float =
    match Map.tryFind "atan2" mathBackend with
    | Some Ucrt -> Ucrt.atan2(y, x)
    | _ -> Math.Atan2(y, x)

/// hypot(x, y): the std::abs(complex) backend. No managed equivalent that
/// matches; always ucrtbase.
let mathHypot (x: float) (y: float) : float = Ucrt.hypot(x, y)

// Complex128 arithmetic (bit-exact: libgcc __muldc3 / __divdc3).

let inline private isInf (x: float) = Double.IsInfinity x
let inline private isNan (x: float) = Double.IsNaN x
let inline private isFin (x: float) = not (Double.IsInfinity x) && not (Double.IsNaN x)
let inline private csign (m: float) (s: float) = Math.CopySign(m, s)
let private INF = Double.PositiveInfinity

/// (a+bi)*(c+di): naive product with C99 Annex-G NaN/inf recovery, exactly as
/// libgcc __muldc3 (which libstdc++'s complex<double> operator* lowers to).
let complexMul (a: float) (b: float) (c: float) (d: float) : float * float =
    let ac = a * c
    let bd = b * d
    let ad = a * d
    let bc = b * c
    let mutable x = ac - bd
    let mutable y = ad + bc
    if isNan x && isNan y then
        let mutable a = a
        let mutable b = b
        let mutable c = c
        let mutable d = d
        let mutable recalc = false
        if isInf a || isInf b then
            a <- csign (if isInf a then 1.0 else 0.0) a
            b <- csign (if isInf b then 1.0 else 0.0) b
            if isNan c then c <- csign 0.0 c
            if isNan d then d <- csign 0.0 d
            recalc <- true
        if isInf c || isInf d then
            c <- csign (if isInf c then 1.0 else 0.0) c
            d <- csign (if isInf d then 1.0 else 0.0) d
            if isNan a then a <- csign 0.0 a
            if isNan b then b <- csign 0.0 b
            recalc <- true
        if not recalc && (isInf ac || isInf bd || isInf ad || isInf bc) then
            if isNan a then a <- csign 0.0 a
            if isNan b then b <- csign 0.0 b
            if isNan c then c <- csign 0.0 c
            if isNan d then d <- csign 0.0 d
            recalc <- true
        if recalc then
            x <- INF * (a * c - b * d)
            y <- INF * (a * d + b * c)
    (x, y)

/// (a+bi)/(c+di): Smith's scaled division with libgcc __divdc3 recovery.
let complexDiv (a: float) (b: float) (c: float) (d: float) : float * float =
    let mutable x = 0.0
    let mutable y = 0.0
    if Math.Abs c < Math.Abs d then
        let ratio = c / d
        let denom = (c * ratio) + d
        x <- ((a * ratio) + b) / denom
        y <- ((b * ratio) - a) / denom
    else
        let ratio = d / c
        let denom = (d * ratio) + c
        x <- (a + (b * ratio)) / denom
        y <- (b - (a * ratio)) / denom
    if isNan x && isNan y then
        if c = 0.0 && d = 0.0 && (not (isNan a) || not (isNan b)) then
            x <- (csign INF c) * a
            y <- (csign INF c) * b
        elif (isInf a || isInf b) && isFin c && isFin d then
            let a = csign (if isInf a then 1.0 else 0.0) a
            let b = csign (if isInf b then 1.0 else 0.0) b
            x <- INF * (a * c + b * d)
            y <- INF * (b * c - a * d)
        elif (isInf c || isInf d) && isFin a && isFin b then
            let c = csign (if isInf c then 1.0 else 0.0) c
            let d = csign (if isInf d then 1.0 else 0.0) d
            x <- 0.0 * (a * c + b * d)
            y <- 0.0 * (b * c - a * d)
    (x, y)

/// |a+bi| = hypot(a, b) (std::abs(complex<double>) backend).
let complexAbs (re: float) (im: float) : float = mathHypot re im

/// arg(a+bi) = atan2(b, a) (std::arg(complex<double>) backend).
let complexArg (re: float) (im: float) : float = mathAtan2 im re

// complex + real: std::complex's mixed SCALAR overloads (verified).
// Applied when exactly one operand renders as complex in the emitted C++.
let private addCR a b s = (a + s, b)          // (a+bi) + s
let private subCR a b s = (a - s, b)          // (a+bi) - s
let private mulCR a b s = (a * s, b * s)      // (a+bi) * s
let private divCR a b s = (a / s, b / s)      // (a+bi) / s
let private addRC s a b = (s + a, b)          // s + (a+bi)
let private subRC s a b = (s - a, 0.0 - b)    // s - (a+bi)  (imag = +0 - b)
let private mulRC s a b = (s * a, s * b)      // s * (a+bi)
let private divRC s a b = complexDiv s 0.0 a b // s / (a+bi) : full __divdc3 of (s,0)/(a,b)

// Value <-> primitive coercions.

/// The scalar ElemType a Value represents (None for non-scalar values).
let scalarElem (v: Value) : ElemType option =
    match v with
    | VInt _ -> Some ETInt64
    | VInt32 _ -> Some ETInt32
    | VFloat _ -> Some ETFloat64
    | VFloat32 _ -> Some ETFloat32
    | VComplex _ -> Some ETComplex128
    | VBool _ -> Some ETBool
    | VString _ -> Some ETString
    | VChar _ -> Some ETInt32   // char literals lower to ETInt32 in this compiler
    | _ -> None

let private isComplexElem (et: ElemType) =
    match et with ETComplex64 | ETComplex128 -> true | _ -> false

let private asF64 (v: Value) : float =
    match v with
    | VFloat f -> f
    | VFloat32 f -> float f
    | VInt n -> float n
    | VInt32 n -> float n
    | VComplex (r, _) -> r
    | VBool b -> if b then 1.0 else 0.0
    | VChar c -> float (int c)
    | _ -> nan

let private asF32 (v: Value) : float32 =
    match v with
    | VFloat32 f -> f
    | VFloat f -> float32 f
    | VInt n -> float32 n
    | VInt32 n -> float32 n
    | VChar c -> float32 (int c)
    | _ -> nan |> float32

// int conversions from a float truncate toward zero (C++ (int)double).
let private asI64 (v: Value) : int64 =
    match v with
    | VInt n -> n
    | VInt32 n -> int64 n
    | VFloat f -> int64 f
    | VFloat32 f -> int64 (float f)
    | VBool b -> if b then 1L else 0L
    | VChar c -> int64 (int c)
    | _ -> 0L

let private asI32 (v: Value) : int32 =
    match v with
    | VInt32 n -> n
    | VInt n -> int32 n
    | VFloat f -> int32 f
    | VFloat32 f -> int32 (float f)
    | VBool b -> if b then 1 else 0
    | VChar c -> int32 c
    | _ -> 0

/// Coerce a value to complex components. A real operand becomes (v, 0.0), the
/// same widening CodeGen applies (coerceComplexOperand casts to the component
/// real type; the imaginary part is an implicit +0).
let private asComplex (v: Value) : float * float =
    match v with
    | VComplex (r, i) -> (r, i)
    | other -> (asF64 other, 0.0)

// C++ usual-arithmetic-conversion type: the type C++ evaluates a real binop
// in (distinct from the Blade node type, IR.promoteElemType). Ranks: Float64
// > Float32 > Int64 > Int32, higher-ranked operand wins -- where `int64 +
// float32` becomes `float`, then converts to the Float64 result.
let private numRank (et: ElemType) =
    match et with
    | ETFloat64 -> 5 | ETFloat32 -> 4 | ETInt64 -> 3 | ETInt32 -> 2 | _ -> 1

let cppArithElem (le: ElemType) (re: ElemType) : ElemType =
    if numRank le >= numRank re then le else re

// The arithmetic contract's FAULTS (docs/formalism.md section 2.4,
// "Arithmetic semantics"), twins of blade_rt::idiv / imod / ipow / f2i in
// src/cpp/blade_runtime.hpp and blade_idiv / blade_imod / blade_ipow /
// blade_f2i64 in src/cpp/blade_llvm_shim.c: same code, same message, so the
// three lanes fail IDENTICALLY. (Integer division by zero used to be BL8007
// here -- the singular-matrix code -- while the compiled program died with
// STATUS_INTEGER_DIVIDE_BY_ZERO and printed nothing.)
/// The panic's position: the faulting node's SrcLoc, rendered exactly as
/// Core.fs renders an IRConstraintCheck's span (file when named, line when
/// known) so blade_rt::panic's `  --> file:line` comes out byte-identical.
let private faultSite (loc: SrcLoc) : string option * int =
    let span = loc.Span
    let fileOpt = match span.File with Some f when f <> "" -> Some f | _ -> None
    (fileOpt, (if span.StartLine > 0 then span.StartLine else 0))

let private intFaultAt (loc: SrcLoc) (msg: string) : 'a =
    let (file, line) = faultSite loc
    raise (InterpPanic("BL8013", msg, file, line))

/// b ^ e over integers: exact modulo 2^64 (two's-complement wrap, the residue
/// blade_arith::ipow_nn computes; any multiplication order gives the same
/// residue). 0 ^ 0 = 1; a negative exponent panics BL8013 at `loc`.
let intPow64At (loc: SrcLoc) (b: int64) (e: int64) : int64 =
    if e < 0L then intFaultAt loc "integer power with a negative exponent"
    let mutable r = 1UL
    let mutable x = uint64 b
    let mutable n = uint64 e
    while n <> 0UL do
        if n &&& 1UL <> 0UL then r <- r * x
        x <- x * x
        n <- n >>> 1
    int64 r

/// The Int32 twin (exact modulo 2^32).
let intPow32At (loc: SrcLoc) (b: int32) (e: int32) : int32 =
    if e < 0 then intFaultAt loc "integer power with a negative exponent"
    let mutable r = 1u
    let mutable x = uint32 b
    let mutable n = uint32 e
    while n <> 0u do
        if n &&& 1u <> 0u then r <- r * x
        x <- x * x
        n <- n >>> 1
    int32 r

/// Real `^`: x * x at an exponent of exactly 2, else the platform libm pow
/// (blade_arith::fpow). The test is on the VALUE, so a computed 2 agrees with
/// the literal one codegen constant-propagates.
let realPow (b: float) (e: float) : float =
    if e = 2.0 then b * b else mathPow b e

/// Float -> integer conversion: truncation toward zero of a value the target
/// can hold; NaN / +-inf / out of [-2^(w-1), 2^(w-1)) panics BL8014
/// (blade_rt::f2i). Both bounds are powers of two, so the tests are exact.
let private f2iFaultAt (loc: SrcLoc) : 'a =
    let (file, line) = faultSite loc
    raise (InterpPanic("BL8014", "float-to-integer conversion of NaN or an out-of-range value", file, line))
let floatToInt64At (loc: SrcLoc) (x: float) : int64 =
    if not (x >= -9223372036854775808.0 && x < 9223372036854775808.0) then f2iFaultAt loc
    else int64 x
let floatToInt32At (loc: SrcLoc) (x: float) : int32 =
    if not (x >= -2147483648.0 && x < 2147483648.0) then f2iFaultAt loc
    else int32 x
/// The anonymous forms, for arithmetic the interpreter synthesizes itself.
let intPow64 (b: int64) (e: int64) : int64 = intPow64At SrcLoc.Nowhere b e
let intPow32 (b: int32) (e: int32) : int32 = intPow32At SrcLoc.Nowhere b e
let floatToInt64 (x: float) : int64 = floatToInt64At SrcLoc.Nowhere x
let floatToInt32 (x: float) : int32 = floatToInt32At SrcLoc.Nowhere x

/// Convert a computed value to a target scalar ElemType (the Blade node type).
/// Post-arithmetic this is either identity or a Float32->Float64 widening (exact)
/// or, for `^`, a double->int truncation.
let private convertTo (target: ElemType) (v: Value) : Value =
    match target with
    | ETInt32 -> VInt32 (asI32 v)
    | ETInt64 -> VInt (asI64 v)
    | ETFloat32 -> VFloat32 (asF32 v)
    | ETFloat64 -> VFloat (asF64 v)
    | ETComplex128 | ETComplex64 -> let (r, i) = asComplex v in VComplex (r, i)
    | _ -> v

/// Compute a real (non-complex, non-pow) binop in the C++ evaluation type,
/// producing a value of THAT type. Integer +,-,* wrap (two's complement) --
/// the C++ lane matches only because Build.optFlags passes `-fwrapv`; do not
/// switch this file to Checked arithmetic without changing both. / and %
/// truncate toward zero (matching C++ and F#'s int operators).
let private computeReal (loc: SrcLoc) (op: IRBinOp) (comp: ElemType) (l: Value) (r: Value) : Value =
    match comp with
    | ETInt32 ->
        let a = asI32 l
        let b = asI32 r
        match op with
        | IRAdd -> VInt32 (a + b)
        | IRSub -> VInt32 (a - b)
        | IRMul -> VInt32 (a * b)
        | IRDiv ->
            if b = 0 then intFaultAt loc "integer division by zero"
            elif b = -1 then VInt32 (0 - a)   // MIN / -1 wraps to MIN (.NET would throw)
            else VInt32 (a / b)
        | IRMod ->
            if b = 0 then intFaultAt loc "integer modulo by zero"
            elif b = -1 then VInt32 0
            else VInt32 (a % b)
        | _ -> VInt32 0
    | ETInt64 ->
        let a = asI64 l
        let b = asI64 r
        match op with
        | IRAdd -> VInt (a + b)
        | IRSub -> VInt (a - b)
        | IRMul -> VInt (a * b)
        | IRDiv ->
            if b = 0L then intFaultAt loc "integer division by zero"
            elif b = -1L then VInt (0L - a)
            else VInt (a / b)
        | IRMod ->
            if b = 0L then intFaultAt loc "integer modulo by zero"
            elif b = -1L then VInt 0L
            else VInt (a % b)
        | _ -> VInt 0L
    | ETFloat32 ->
        let a = asF32 l
        let b = asF32 r
        match op with
        | IRAdd -> VFloat32 (a + b)
        | IRSub -> VFloat32 (a - b)
        | IRMul -> VFloat32 (a * b)
        | IRDiv -> VFloat32 (a / b)
        | IRMod -> VFloat32 (a % b)   // unreachable for well-typed IR (C++ has no float %)
        | _ -> VFloat32 0.0f
    | _ (* ETFloat64 *) ->
        let a = asF64 l
        let b = asF64 r
        match op with
        | IRAdd -> VFloat (a + b)
        | IRSub -> VFloat (a - b)
        | IRMul -> VFloat (a * b)
        | IRDiv -> VFloat (a / b)
        | IRMod -> VFloat (a % b)     // unreachable for well-typed IR
        | _ -> VFloat 0.0

/// A complex intrinsic this module cannot reproduce bit-exactly yet.
///
/// A CAPABILITY gap, not a program fault, so it travels the interpreter's
/// unsupported channel (Run.fs maps it to ExitUnsupported=125, like
/// ArrayOps.ArrayOpUnsupported and Print.PrintUnsupported) rather than an
/// InterpPanic. The difference is load-bearing at both consumers: the interp
/// differential scores it SKIP-UNSUPPORTED instead of FAIL, and the REPL falls
/// back to g++ for that input instead of showing the user a BL8011 that reads
/// like a fault in their own program.
exception NumericsUnsupported of string

// Complex transcendental intrinsics (docs/formalism.md section 2.4): the
// compiled side calls the PLATFORM C library's complex functions at run time,
// behind blade_runtime.hpp's no-fold barrier (blade_libm's complex overloads
// bind cexp, clog, csqrt, csin, ccos, ctan, csinh, ccosh, ctanh, casin, cacos,
// catan, cpow by asm label -- the functions libstdc++'s std::exp(complex) & co.
// forward to under _GLIBCXX_USE_C99_COMPLEX). So "bit-identical" means
// reproducing THOSE functions, not libstdc++'s template closed forms (an
// earlier attempt ported the templates and found asin/acos off by an ulp on
// 7 of 10 operands and atan wrong on every signed zero: the template is not
// what runs).
//
// On Windows (MSYS2 ucrt64, g++ 16.2.0 Rev4) the link line (`-lmingwex` before
// `-lucrt`) binds every one of them to libmingwex's OWN objects (catan.o,
// casin.o, cacos.o, cexp.o, csin.o, ccos.o, ctan.o, csqrt.o, clog.o, cpow.o),
// not to ucrtbase's exports of the same names. Those objects are small and
// closed: their external calls are ucrtbase's log, log1p, atan2, hypot, exp,
// sin, cos (via libucrt_extra's sincos = sin then cos), sinh, cosh, pow,
// fmod (exact) and sqrt (correctly rounded), plus libgcc's __muldc3 /
// __divdc3. The ports below are transcribed from the objects' disassembly
// operation for operation, special-value arms included (the generic-x86-64
// builds have no FMA and the C sources no reassociation), and call the SAME
// ucrtbase functions through `Ucrt` -- bit-identical by construction, and
// pinned by a hex battery against g++ plus intrinsics/015, 022 and 023.
//
// Every other OS keeps the earlier behavior: best-effort textbook forms for
// exp/log/sqrt and `^` (NOT bit-verified), and a loud decline for the rest --
// glibc/libSystem complex functions are different algorithms, and
// bit-identity is a per-platform claim.

/// mingw-w64 `catanh` (libmingwex, ucrt64), transcribed from its object code.
/// Special-value arms first (fpclassify order: NaN/Inf/zero), then the three
/// magnitude regimes of the real part, all sharing the imaginary part
/// `0.5 * atan2(2y, (1 - x*x) - y*y)`.
let private mingwCatanh (x: float) (y: float) : float * float =
    let qnan = BitConverter.Int64BitsToDouble 0x7ff8000000000000L
    let halfPi = BitConverter.Int64BitsToDouble 0x3ff921fb54442d18L
    let eps = BitConverter.Int64BitsToDouble 0x3cb0000000000000L   // 2^-52
    let poleImag () = Math.CopySign (halfPi, y)
    if Double.IsNaN x then
        if Double.IsInfinity y then (Math.CopySign (0.0, x), poleImag ())
        else (qnan, qnan)
    elif Double.IsInfinity x then
        if Double.IsNaN y then (Math.CopySign (0.0, x), qnan)
        else (Math.CopySign (0.0, x), poleImag ())
    elif Double.IsNaN y then
        if x = 0.0 then (Math.CopySign (0.0, x), qnan) else (qnan, qnan)
    elif Double.IsInfinity y then
        (Math.CopySign (0.0, x), poleImag ())
    elif x = 0.0 && y = 0.0 then
        (x, y)
    else
        let i2 = y * y
        let (re, x2) =
            if eps >= abs x then
                (0.25 * Ucrt.log1p ((x * 4.0) / (i2 + 1.0)), x * x)
            else
                let x2 = x * x
                if not (eps < x2) then
                    let t = x / (i2 + 1.0)
                    (0.25 * Ucrt.log1p ((t + t + 1.0) * (t * 4.0)), x2)
                else
                    let n = (x + 1.0) * (x + 1.0) + i2
                    let d = (1.0 - x) * (1.0 - x) + i2
                    (0.25 * (Ucrt.log n - Ucrt.log d), x2)
        (re, 0.5 * Ucrt.atan2 (y + y, (1.0 - x2) - i2))

/// mingw-w64 `clog` (libmingwex, ucrt64), transcribed from its object code:
/// `(log(hypot(x, y)), atan2(y, x))` behind its own special-value arms.
let private mingwClog (x: float) (y: float) : float * float =
    let qnan = BitConverter.Int64BitsToDouble 0x7ff8000000000000L
    let main () = (Ucrt.log (Ucrt.hypot (x, y)), Ucrt.atan2 (y, x))
    if Double.IsNaN x then
        if Double.IsInfinity y then (Double.PositiveInfinity, qnan) else (qnan, qnan)
    elif Double.IsInfinity x then
        if Double.IsNaN y then (Double.PositiveInfinity, qnan) else main ()
    elif Double.IsNaN y then (qnan, qnan)
    elif x = 0.0 && y = 0.0 then
        // -1/|x| = -inf; the argument is +-0 for x = +0 and +-pi for x = -0.
        let im = if BitConverter.DoubleToInt64Bits x < 0L then Math.CopySign (Math.PI, y) else Math.CopySign (0.0, y)
        (-1.0 / abs x, im)
    else main ()

/// mingw-w64 `csqrt` (libmingwex, ucrt64), transcribed from its object code
/// (NOT libstdc++'s inline Kahan form, which `sqrt` above ports: this is the
/// C function casinh calls).
let private mingwCsqrt (x: float) (y: float) : float * float =
    let qnan = BitConverter.Int64BitsToDouble 0x7ff8000000000000L
    let inf = Double.PositiveInfinity
    if Double.IsNaN x then
        if Double.IsInfinity y then (inf, y) else (qnan, qnan)
    elif Double.IsInfinity x then
        if Double.IsInfinity y then (inf, y)
        elif Double.IsNaN y then
            if 0.0 > x then (qnan, Math.CopySign (inf, y)) else (x, qnan)
        elif 0.0 > x then (0.0, Math.CopySign (inf, y))
        else (x, Math.CopySign (0.0, y))
    elif y = 0.0 then
        if 0.0 > x then (0.0, Math.CopySign (Math.Sqrt (-x), y))
        else (abs (Math.Sqrt x), Math.CopySign (0.0, y))
    elif Double.IsInfinity y then (inf, y)
    elif Double.IsNaN y then (qnan, qnan)
    elif x = 0.0 then
        let r = Math.Sqrt (abs y * 0.5)
        (r, Math.CopySign (r, y))
    else
        let t = Ucrt.hypot (x, y)
        if x > 0.0 then
            let r = Math.Sqrt (t * 0.5 + 0.5 * x)
            (r, Math.CopySign ((y * 0.5) / r, y))
        else
            let s = Math.Sqrt (t * 0.5 - 0.5 * x)
            (abs ((y * 0.5) / s), Math.CopySign (s, y))

/// mingw-w64 `casinh` (libmingwex, ucrt64), transcribed from its object code.
/// Works on |x|, |y| and restores both signs at the end; four magnitude
/// regimes (huge -> clog + ln 2, |y| >= 1 or x not small -> the csqrt/clog
/// identity, |x| <= eps and x^2 <= eps -> log1p/atan2 series forms).
let private mingwCasinh (x: float) (y: float) : float * float =
    let qnan = BitConverter.Int64BitsToDouble 0x7ff8000000000000L
    let inf = Double.PositiveInfinity
    let halfPi = BitConverter.Int64BitsToDouble 0x3ff921fb54442d18L
    let quarterPi = BitConverter.Int64BitsToDouble 0x3fe921fb54442d18L
    let ln2 = BitConverter.Int64BitsToDouble 0x3fe62e42fefa39efL
    let big = BitConverter.Int64BitsToDouble 0x4330000000000000L   // 2^52
    let eps = BitConverter.Int64BitsToDouble 0x3cb0000000000000L   // 2^-52
    let finish (mr: float) (mi: float) = (Math.CopySign (mr, x), Math.CopySign (mi, y))
    if Double.IsNaN x then
        if y = 0.0 then (x, y)
        elif Double.IsInfinity y then (Math.CopySign (inf, x), qnan)
        else (x, qnan)
    elif Double.IsInfinity x then
        if Double.IsNaN y then (x, qnan)
        elif Double.IsInfinity y then (Math.CopySign (inf, x), Math.CopySign (quarterPi, y))
        else (x, Math.CopySign (0.0, y))
    elif Double.IsNaN y then (qnan, qnan)
    elif Double.IsInfinity y then (Math.CopySign (inf, x), Math.CopySign (halfPi, y))
    elif x = 0.0 && y = 0.0 then (x, y)
    else
        let ax = abs x
        let ay = abs y
        let general () =
            let a = (ax - ay) * (ay + ax) + 1.0
            let b = (ax + ax) * ay
            let (sr, si) = mingwCsqrt a b
            let (lr, li) = mingwClog (ax + sr) (ay + si)
            finish lr li
        if ax >= big || ay >= big then
            let (lr, li) = mingwClog ax ay
            finish (ln2 + lr) li
        elif 1.0 <= ay then general ()
        elif eps >= ax then
            let r = Math.Sqrt ((ay + 1.0) * (1.0 - ay))
            finish (Ucrt.log1p (ax / r)) (Ucrt.atan2 (ay, r))
        elif eps < x * x then general ()
        else
            let x2 = x * x
            let s = (ay + 1.0) * (1.0 - ay)
            let r = Math.Sqrt s
            let q = (x2 * 0.5) / r
            let re = Ucrt.log1p ((q + ax) / r)
            let im = Ucrt.atan2 ((r + ax) * ay, q + (r * ax + s))
            finish re im

/// mingw-w64 `casin`: -i * casinh(i z), i.e. casinh(-y + i x) -> (im, -re).
let private mingwCasin (x: float) (y: float) : float * float =
    let (hr, hi) = mingwCasinh (-y) x
    (hi, -hr)

let private isNegBit (v: float) = BitConverter.DoubleToInt64Bits v < 0L
let private qnanD = BitConverter.Int64BitsToDouble 0x7ff8000000000000L

/// mingw-w64 `cexp` (libmingwex, ucrt64), transcribed from its object code.
/// sincos there is ucrt's sin then cos (libucrt_extra's sincos.o).
let private mingwCexp (x: float) (y: float) : float * float =
    let inf = Double.PositiveInfinity
    if Double.IsNaN x then
        if y = 0.0 then (qnanD, y) else (qnanD, qnanD)
    elif Double.IsInfinity x then
        if y = 0.0 then (if x < 0.0 then (0.0, y) else (inf, y))
        elif Double.IsInfinity y || Double.IsNaN y then
            (if x < 0.0 then (0.0, Math.CopySign (0.0, y)) else (inf, qnanD))
        else
            let s = Ucrt.sin y
            let c = Ucrt.cos y
            let mag = if x < 0.0 then 0.0 else inf
            (Math.CopySign (mag, c), Math.CopySign (mag, s))
    elif y = 0.0 then
        let e = Ucrt.exp x
        let s = Ucrt.sin y
        let c = Ucrt.cos y
        if Double.IsInfinity e then (e, y) else (c * e, e * s)
    elif Double.IsInfinity y || Double.IsNaN y then (qnanD, qnanD)
    else
        let e = Ucrt.exp x
        let s = Ucrt.sin y
        let c = Ucrt.cos y
        if Double.IsInfinity e then (Math.CopySign (inf, c), Math.CopySign (inf, s))
        else (c * e, e * s)

/// mingw-w64 `csinh`, transcribed from its object code (csin.o).
let private mingwCsinh (x: float) (y: float) : float * float =
    let inf = Double.PositiveInfinity
    let yBad = Double.IsInfinity y || Double.IsNaN y
    if Double.IsNaN x then
        if y = 0.0 then (qnanD, y) else (qnanD, qnanD)
    elif Double.IsInfinity x then
        if y = 0.0 then (x, y)
        elif yBad then (inf, qnanD)
        else
            let s = Ucrt.sin y
            let c = Ucrt.cos y
            let re = Math.CopySign (inf, c)
            ((if isNegBit x then -re else re), Math.CopySign (inf, s))
    elif yBad then
        if x = 0.0 then (Math.CopySign (0.0, x), qnanD) else (qnanD, qnanD)
    else
        let ax = abs x
        let s = Ucrt.sin y
        let c = Ucrt.cos y
        let re = Ucrt.sinh ax * c
        let im = Ucrt.cosh ax * s
        ((if isNegBit x then -re else re), im)

/// mingw-w64 `ccosh`, transcribed from its object code (ccos.o).
let private mingwCcosh (x: float) (y: float) : float * float =
    let inf = Double.PositiveInfinity
    let yBad = Double.IsInfinity y || Double.IsNaN y
    if Double.IsNaN x then
        if y = 0.0 then (qnanD, y) else (qnanD, qnanD)
    elif Double.IsInfinity x then
        if y = 0.0 then (inf, y * Math.CopySign (1.0, x))
        elif yBad then (inf, qnanD)
        else
            let s = Ucrt.sin y
            let c = Ucrt.cos y
            (Math.CopySign (inf, c), Math.CopySign (1.0, x) * Math.CopySign (inf, s))
    elif yBad then
        if x = 0.0 then (qnanD, 0.0) else (qnanD, qnanD)
    else
        let s = Ucrt.sin y
        let c = Ucrt.cos y
        (Ucrt.cosh x * c, Ucrt.sinh x * s)

/// libgcc `__muldc3` (gcc 16.2, x86_64-w64-mingw32): (a + ib)(c + id) with
/// the C99 Annex G NaN recovery. The fast path is the plain four products,
/// x = ac - bd, y = ad + bc.
let private libgccMuldc3 (a0: float) (b0: float) (c0: float) (d0: float) : float * float =
    let ac = a0 * c0
    let bd = b0 * d0
    let ad = a0 * d0
    let bc = b0 * c0
    let x = ac - bd
    let y = ad + bc
    if Double.IsNaN x && Double.IsNaN y then
        let box (v: float) = Math.CopySign ((if Double.IsInfinity v then 1.0 else 0.0), v)
        let nz (v: float) = if Double.IsNaN v then Math.CopySign (0.0, v) else v
        let mutable a = a0
        let mutable b = b0
        let mutable c = c0
        let mutable d = d0
        let mutable recalc = false
        if Double.IsInfinity a || Double.IsInfinity b then
            a <- box a; b <- box b; c <- nz c; d <- nz d; recalc <- true
        if Double.IsInfinity c || Double.IsInfinity d then
            c <- box c; d <- box d; a <- nz a; b <- nz b; recalc <- true
        if not recalc && (Double.IsInfinity ac || Double.IsInfinity bd
                          || Double.IsInfinity ad || Double.IsInfinity bc) then
            a <- nz a; b <- nz b; c <- nz c; d <- nz d; recalc <- true
        if recalc then
            (Double.PositiveInfinity * (a * c - b * d), Double.PositiveInfinity * (a * d + b * c))
        else (x, y)
    else (x, y)

/// libgcc `__divdc3` (gcc 16.2): (a + ib) / (c + id), Smith's method with the
/// RBIG/RMIN scaling and the Annex G NaN recovery, transcribed from its object
/// code (constants: RBIG = DBL_MAX/2, RMIN = DBL_MIN, RMIN2 = DBL_EPSILON,
/// RMINSCAL = 2^52, RMAX2 = RBIG * RMIN2).
let private libgccDivdc3 (a0: float) (b0: float) (c0: float) (d0: float) : float * float =
    let rbig = BitConverter.Int64BitsToDouble 0x7fdfffffffffffffL
    let rmin = BitConverter.Int64BitsToDouble 0x0010000000000000L
    let rmin2 = BitConverter.Int64BitsToDouble 0x3cb0000000000000L
    let rminscal = BitConverter.Int64BitsToDouble 0x4330000000000000L
    let rmax2 = BitConverter.Int64BitsToDouble 0x7c9fffffffffffffL
    let mutable a = a0
    let mutable b = b0
    let mutable c = c0
    let mutable d = d0
    let halve () = a <- a * 0.5; b <- b * 0.5; c <- c * 0.5; d <- d * 0.5
    let scaleUp () = a <- a * rminscal; b <- b * rminscal; c <- c * rminscal; d <- d * rminscal
    let mutable x = 0.0
    let mutable y = 0.0
    if abs c < abs d then
        if abs d >= rbig then halve ()
        if abs d < rmin2 then scaleUp ()
        elif ((abs a < rmin) && (abs b < rmax2) && (abs d < rmax2))
             || ((abs b < rmin) && (abs a < rmax2) && (abs d < rmax2)) then scaleUp ()
        let ratio = c / d
        let denom = (c * ratio) + d
        if abs ratio > rmin then
            x <- ((a * ratio) + b) / denom
            y <- ((b * ratio) - a) / denom
        else
            x <- ((c * (a / d)) + b) / denom
            y <- ((c * (b / d)) - a) / denom
    else
        if abs c >= rbig then halve ()
        if abs c < rmin2 then scaleUp ()
        elif ((abs a < rmin) && (abs b < rmax2) && (abs c < rmax2))
             || ((abs b < rmin) && (abs a < rmax2) && (abs c < rmax2)) then scaleUp ()
        let ratio = d / c
        let denom = (d * ratio) + c
        if abs ratio > rmin then
            x <- ((b * ratio) + a) / denom
            y <- (b - (a * ratio)) / denom
        else
            x <- (a + (d * (b / c))) / denom
            y <- (b - (d * (a / c))) / denom
    if Double.IsNaN x && Double.IsNaN y then
        let fin (v: float) = not (Double.IsNaN v || Double.IsInfinity v)
        let box (v: float) = Math.CopySign ((if Double.IsInfinity v then 1.0 else 0.0), v)
        if c = 0.0 && d = 0.0 && (not (Double.IsNaN a) || not (Double.IsNaN b)) then
            let ci = Math.CopySign (Double.PositiveInfinity, c)
            (ci * a, ci * b)
        elif (Double.IsInfinity a || Double.IsInfinity b) && fin c && fin d then
            let a' = box a
            let b' = box b
            (Double.PositiveInfinity * (a' * c + b' * d), Double.PositiveInfinity * (b' * c - a' * d))
        elif (Double.IsInfinity c || Double.IsInfinity d) && fin a && fin b then
            let c' = box c
            let d' = box d
            (0.0 * (a * c' + b * d'), 0.0 * (b * c' - a * d'))
        else (x, y)
    else (x, y)

/// mingw-w64 `ctanh`, transcribed from its object code (ctan.o): the
/// sin(2y)/cosh(2x) form, falling back to (e^z - e^-z) / (e^z + e^-z) through
/// cexp and __divdc3 when the denominator cancels to zero.
let private mingwCtanh (x: float) (y: float) : float * float =
    let eps = BitConverter.Int64BitsToDouble 0x3cb0000000000000L
    let halfPi = BitConverter.Int64BitsToDouble 0x3ff921fb54442d18L
    if Double.IsNaN x then
        if y = 0.0 then (x, y) else (qnanD, qnanD)
    elif Double.IsInfinity x then
        let r = y % Math.PI
        let im =
            if not (isNegBit y) then (if eps < r - halfPi then -0.0 else 0.0)
            else (if not (r + halfPi < -eps) then -0.0 else 0.0)
        (Math.CopySign (1.0, x), im)
    elif Double.IsInfinity y || Double.IsNaN y then (qnanD, qnanD)
    else
        let s = Ucrt.sin (y + y)
        let c = Ucrt.cos (y + y)
        let x2 = x + x
        let d = c + Ucrt.cosh x2
        if d = 0.0 then
            let (a, b) = mingwCexp x y
            let (p, q) = mingwCexp (-x) (-y)
            libgccDivdc3 (a - p) (b - q) (a + p) (b + q)
        else
            (Ucrt.sinh x2 / d, s / d)

/// mingw-w64 `cpow`: cexp(y * clog(x)), the product through __muldc3.
let private mingwCpow (xr: float) (xi: float) (yr: float) (yi: float) : float * float =
    let (lr, li) = mingwClog xr xi
    let (pr, pi) = libgccMuldc3 lr li yr yi
    mingwCexp pr pi

/// std::polar(r, t) = (r cos t, r sin t), over ucrtbase cos/sin.
let private polarU (r: float) (t: float) : float * float = (r * Ucrt.cos t, r * Ucrt.sin t)

let private onWindows () = Blade.Platforms.os = Blade.Platforms.Windows

let complexMath (name: string) (re: float) (im: float) : float * float =
    match name with
    // Windows: the C99 library functions the compiled side calls through
    // blade_libm's complex overloads (blade_runtime.hpp), ported above.
    | "exp" when onWindows () -> mingwCexp re im
    | "log" when onWindows () -> mingwClog re im
    | "sqrt" when onWindows () -> mingwCsqrt re im
    | "sinh" when onWindows () -> mingwCsinh re im
    | "cosh" when onWindows () -> mingwCcosh re im
    | "tanh" when onWindows () -> mingwCtanh re im
    // sin/cos/tan: the -i f(iz) rotations of the hyperbolic ones (csin.o,
    // ccos.o, ctan.o): csin = (im, -re) of csinh(-y + ix); ccos = ccosh(-y +
    // ix) unrotated; ctan = (im, -re) of ctanh(-y + ix).
    | "sin" when onWindows () -> let (hr, hi) = mingwCsinh (-im) re in (hi, -hr)
    | "cos" when onWindows () -> mingwCcosh (-im) re
    | "tan" when onWindows () -> let (hr, hi) = mingwCtanh (-im) re in (hi, -hr)
    | "exp" ->
        let e = math1 "exp" re
        (e * math1 "cos" im, e * math1 "sin" im)
    | "log" ->
        (math1 "log" (complexAbs re im), complexArg re im)
    | "sqrt" ->
        // libstdc++ std::sqrt(complex<double>) is Kahan's branch algorithm (NOT
        // the polar m*(cos(t/2),sin(t/2)) formula, which loses the exact-zero
        // real part: sqrt(-1+0i) came out (6.12e-17, 1) instead of (0, 1)).
        // Ported arm-for-arm from libstdc++ <complex>, bit-verified against
        // g++ -O2 over 11 operands (incl. neg1, 3+4i, -3-4i, 2i, subnormal
        // 1e-300): every hex pair identical.
        let x = re
        let y = im
        if x = 0.0 then
            let t = math1 "sqrt" (abs y / 2.0)
            (t, (if y < 0.0 then -t else t))
        else
            let t = math1 "sqrt" (2.0 * (complexAbs x y + abs x))
            let u = t / 2.0
            if x > 0.0 then (u, y / t)
            else (abs y / t, (if y < 0.0 then -u else u))
    | "atan" when Blade.Platforms.os = Blade.Platforms.Windows ->
        // mingw-w64 catan: catanh(-y + i x), then (im, -re). See above.
        let (hr, hi) = mingwCatanh (-im) re
        (hi, -hr)
    | "asin" when Blade.Platforms.os = Blade.Platforms.Windows ->
        mingwCasin re im
    | "acos" when Blade.Platforms.os = Blade.Platforms.Windows ->
        // mingw-w64 cacos: (pi/2 - casin(z).re, -casin(z).im).
        let (sr, si) = mingwCasin re im
        (BitConverter.Int64BitsToDouble 0x3ff921fb54442d18L - sr, -si)
    | _ ->
        raise (NumericsUnsupported
                $"complex intrinsic '{name}' is not yet bit-verified in the interpreter")

/// z ^ w for complex. On Windows, the twin of blade_libm::pow's three
/// overloads (blade_runtime.hpp), chosen by which operand is complex exactly
/// as the emitted call's overload resolution chooses (a real operand, integer
/// or not, is cast to double first in both lanes):
///   complex ^ complex = cpow
///   complex ^ real    = re > 0 && im == 0 ? (pow(re, y), 0)
///                       : polar(exp(y * log(z).re), y * log(z).im)
///   real ^ complex    = x > 0 ? polar(pow(x, w.re), w.im * log(x))
///                       : cpow((x, 0), w)
/// Elsewhere the old best-effort exp(w * log z) (NOT bit-verified).
let private complexCaret (l: Value) (r: Value) : Value =
    let isC (v: Value) = match v with VComplex _ -> true | _ -> false
    if onWindows () then
        match isC l, isC r with
        | true, true ->
            let (zr, zi) = asComplex l
            let (wr, wi) = asComplex r
            VComplex (mingwCpow zr zi wr wi)
        | true, false ->
            let (zr, zi) = asComplex l
            let y = asF64 r
            if zi = 0.0 && zr > 0.0 then VComplex (Ucrt.pow (zr, y), 0.0)
            else
                let (tr, ti) = mingwClog zr zi
                VComplex (polarU (Ucrt.exp (y * tr)) (y * ti))
        | _ ->
            let x = asF64 l
            let (wr, wi) = asComplex r
            if x > 0.0 then VComplex (polarU (Ucrt.pow (x, wr)) (wi * Ucrt.log x))
            else VComplex (mingwCpow x 0.0 wr wi)
    else
    let (zr, zi) = asComplex l
    let (wr, wi) = asComplex r
    let (lr, li) = complexMath "log" zr zi
    let (pr, pi) = complexMul wr wi lr li   // w * log z
    let (er, ei) = complexMath "exp" pr pi
    VComplex (er, ei)

// Scalar binop / unaryop dispatch (mirrors CodeGen's IRBinOp / IRUnaryOp).

let private evalArith (loc: SrcLoc) (op: IRBinOp) (l: Value) (r: Value) : Value =
    match scalarElem l, scalarElem r with
    | Some le, Some re ->
        let resElem = promoteElemType le re |> Option.defaultValue le
        if isComplexElem resElem then
            // Complex node. Which operands render as complex in the emitted C++
            // decides full-complex vs. mixed-scalar overload.
            let lc = isComplexElem le
            let rc = isComplexElem re
            match op with
            | IRCaret -> complexCaret l r
            | _ ->
                let (xr, xi) =
                    if lc && rc then
                        let (ar, ai) = asComplex l
                        let (br, bi) = asComplex r
                        match op with
                        | IRAdd -> (ar + br, ai + bi)
                        | IRSub -> (ar - br, ai - bi)
                        | IRMul -> complexMul ar ai br bi
                        | IRDiv -> complexDiv ar ai br bi
                        | _ -> (nan, nan)
                    elif lc then
                        let (a, b) = asComplex l
                        let s = asF64 r
                        match op with
                        | IRAdd -> addCR a b s
                        | IRSub -> subCR a b s
                        | IRMul -> mulCR a b s
                        | IRDiv -> divCR a b s
                        | _ -> (nan, nan)
                    else
                        let s = asF64 l
                        let (a, b) = asComplex r
                        match op with
                        | IRAdd -> addRC s a b
                        | IRSub -> subRC s a b
                        | IRMul -> mulRC s a b
                        | IRDiv -> divRC s a b
                        | _ -> (nan, nan)
                VComplex (xr, xi)
        else
            match op with
            | IRCaret ->
                // The arithmetic contract (CodeGenExprSupport.renderContractBinOp):
                // integer ^ integer is EXACT in the C++ evaluation type (it used
                // to go through a double, so 3^35 printed ...704 for ...707);
                // anything real is realPow in double, then rounded once to the
                // node type (Float32 for a Float32 base).
                (match cppArithElem le re with
                 | ETInt64 -> VInt (intPow64At loc (asI64 l) (asI64 r))
                 | ETInt32 -> VInt32 (intPow32At loc (asI32 l) (asI32 r))
                 | ETFloat32 ->
                     // Evaluated in float, like `s * 2` (blade_arith::fpowf):
                     // the double pow rounded ONCE to float, then widened to
                     // the node type if that is Float64.
                     convertTo resElem (VFloat32 (float32 (realPow (asF64 l) (asF64 r))))
                 | _ -> convertTo resElem (VFloat (realPow (asF64 l) (asF64 r))))
            | _ ->
                let comp = cppArithElem le re
                convertTo resElem (computeReal loc op comp l r)
    | _ ->
        // Non-scalar-numeric: string concatenation is the only IRBinOp Blade
        // lowers here (`(l + r)` on std::string).
        match op, l, r with
        | IRAdd, VString a, VString b -> VString (a + b)
        | _ -> raise (InterpPanic("BL9001", "unsupported operand types for binary operator", None, 0))

// IEEE-exact per-type comparisons. Direct-typed float operators compile to the
// IEEE ordered/unordered comparisons (NaN => false for </<=/>/>=/=, true for <>),
// matching C++. (F#'s generic `compare` does NOT -- it total-orders NaN -- so it is
// deliberately avoided here.)
let private cmpF64 op (a: float) (b: float) =
    match op with
    | IREq -> a = b | IRNeq -> a <> b | IRLt -> a < b | IRLe -> a <= b | IRGt -> a > b | IRGe -> a >= b | _ -> false
let private cmpF32 op (a: float32) (b: float32) =
    match op with
    | IREq -> a = b | IRNeq -> a <> b | IRLt -> a < b | IRLe -> a <= b | IRGt -> a > b | IRGe -> a >= b | _ -> false
let private cmpI64 op (a: int64) (b: int64) =
    match op with
    | IREq -> a = b | IRNeq -> a <> b | IRLt -> a < b | IRLe -> a <= b | IRGt -> a > b | IRGe -> a >= b | _ -> false
let private cmpI32 op (a: int32) (b: int32) =
    match op with
    | IREq -> a = b | IRNeq -> a <> b | IRLt -> a < b | IRLe -> a <= b | IRGt -> a > b | IRGe -> a >= b | _ -> false

let rec private evalCompare (op: IRBinOp) (l: Value) (r: Value) : Value =
    match l, r with
    // Tuples, mirroring `std::tuple`'s own operators -- which is what codegen
    // emits, so the twins agree by construction: `==`/`!=` are the conjunction
    // of the component comparisons, the ordered ones are LEXICOGRAPHIC (the
    // first differing component decides, and `<=`/`>=` are what "all equal"
    // answers true to). Recursive, so a nested tuple component compares
    // structurally the way `std::tuple`'s nested operator== does.
    //
    // Widths agree by construction: TypeCheck refuses a comparison of two
    // different widths (BL3001), as it must -- C++ has no `operator==` across
    // widths at all. A disagreement reaching here is a compiler bug, not user
    // input, so it panics rather than quietly answering `false`.
    | VTuple ls, VTuple rs ->
        if ls.Length <> rs.Length then
            raise (InterpPanic("BL9001", "comparison of tuples of different widths", None, 0))
        else
            let compEq (a: Value) (b: Value) =
                match evalCompare IREq a b with VBool t -> t | _ -> false
            match op with
            | IREq -> VBool (Array.forall2 compEq ls rs)
            | IRNeq -> VBool (not (Array.forall2 compEq ls rs))
            | _ ->
                let rec lex i =
                    if i >= ls.Length then VBool (match op with IRLe | IRGe -> true | _ -> false)
                    elif compEq ls.[i] rs.[i] then lex (i + 1)
                    else evalCompare op ls.[i] rs.[i]
                lex 0
    | VComplex _, _ | _, VComplex _ ->
        // std::complex has only == / != ; ordered comparisons never type-check.
        let (ar, ai) = asComplex l
        let (br, bi) = asComplex r
        match op with
        | IREq -> VBool (ar = br && ai = bi)
        | IRNeq -> VBool (not (ar = br && ai = bi))
        | _ -> VBool false
    | VString a, VString b ->
        // std::string byte-lexicographic order. NOTE: Blade strings are UTF-8
        // bytes in std::string; .NET strings are UTF-16 -- ordinal comparison
        // agrees for ASCII, may differ for multibyte (documented edge).
        let c = String.CompareOrdinal(a, b)
        VBool (match op with IREq -> c = 0 | IRNeq -> c <> 0 | IRLt -> c < 0 | IRLe -> c <= 0 | IRGt -> c > 0 | IRGe -> c >= 0 | _ -> false)
    | VBool a, VBool b ->
        VBool (match op with IREq -> a = b | IRNeq -> a <> b | IRLt -> a < b | IRLe -> a <= b | IRGt -> a > b | IRGe -> a >= b | _ -> false)
    | _ ->
        match scalarElem l, scalarElem r with
        | Some le, Some re ->
            match cppArithElem le re with
            | ETInt32 -> VBool (cmpI32 op (asI32 l) (asI32 r))
            | ETInt64 -> VBool (cmpI64 op (asI64 l) (asI64 r))
            | ETFloat32 -> VBool (cmpF32 op (asF32 l) (asF32 r))
            | _ -> VBool (cmpF64 op (asF64 l) (asF64 r))
        | _ -> raise (InterpPanic("BL9001", "unsupported operand types for comparison", None, 0))

let private toBool (v: Value) : bool =
    match v with VBool b -> b | VInt n -> n <> 0L | VInt32 n -> n <> 0 | _ -> false

/// Value-level `&&` / `||`. The evaluator is responsible for short-circuiting
/// side-effecting operands upstream; this is the pure boolean combiner.
let private evalLogical (op: IRBinOp) (l: Value) (r: Value) : Value =
    match op with
    | IRAnd -> VBool (toBool l && toBool r)
    | IROr -> VBool (toBool l || toBool r)
    | _ -> VBool false

/// Evaluate a scalar binary operator on two already-evaluated operands, matching
/// the C++ CodeGen emits (promotion, wraparound, complex coercion). `loc` is the
/// operator's source position, named by the BL8013 panic a zero divisor or a
/// negative integer exponent raises.
let evalBinOpAt (loc: SrcLoc) (op: IRBinOp) (l: Value) (r: Value) : Value =
    match op with
    // String concatenation: `+` on two Strings is std::string operator+ in
    // the compiled lane -- byte-identical by construction (no formatting).
    // Ahead of the numeric arms so a VString operand never reaches asF64.
    | IRAdd ->
        (match l, r with
         | VString a, VString b -> VString (a + b)
         | _ -> evalArith loc op l r)
    | IREq | IRNeq | IRLt | IRLe | IRGt | IRGe -> evalCompare op l r
    | IRAnd | IROr -> evalLogical op l r
    // Binary math intrinsics. Real-only by construction (TypeCheck rejects
    // complex operands), always Float64, so they bypass evalArith's promotion
    // and complex machinery entirely and mirror CodeGen.renderMath2 directly:
    // `std::atan2(l, r)` and `(std::log(l) / std::log(r))`. The quotient is a
    // plain IEEE double division in both lanes.
    //
    // Float32 operands follow blade_libm's overloads (the arithmetic contract,
    // docs/formalism.md section 2.4): atan2 of TWO Float32s is the double
    // function rounded once to float; log_base rounds each Float32 operand's
    // log to float, and the quotient of two floats is float's (a double
    // quotient of two floats, rounded once to float, IS the float quotient).
    | IRMath2 "atan2" ->
        let v = mathAtan2 (asF64 l) (asF64 r)
        (match l, r with
         | VFloat32 _, VFloat32 _ -> VFloat (float (float32 v))
         | _ -> VFloat v)
    | IRMath2 "log_base" ->
        let lg (v: Value) =
            match v with
            | VFloat32 f -> float (float32 (math1 "log" (float f)))
            | _ -> math1 "log" (asF64 v)
        let q = lg l / lg r
        (match l, r with
         | VFloat32 _, VFloat32 _ -> VFloat (float (float32 q))
         | _ -> VFloat q)
    | IRMath2 name ->
        raise (InterpPanic("BL9001", $"unknown binary math intrinsic '{name}'", None, 0))
    | IRSub | IRMul | IRDiv | IRMod | IRCaret -> evalArith loc op l r

/// abs(x): std::abs, whose C++ overload preserves the operand's numeric type
/// (llabs->int64, fabs->double, fabsf->float, hypot->double magnitude for
/// complex). Two's-complement wrap on INT_MIN matches C++'s llabs/abs.
/// The anonymous form, for the interpreter's own synthesized arithmetic (folds,
/// scales, dot products) that has no source operator to point at.
let evalBinOp (op: IRBinOp) (l: Value) (r: Value) : Value = evalBinOpAt SrcLoc.Nowhere op l r

let private evalAbs (v: Value) : Value =
    match v with
    | VInt n -> VInt (if n < 0L then 0L - n else n)
    | VInt32 n -> VInt32 (if n < 0 then 0 - n else n)
    | VFloat f -> VFloat (math1 "fabs" f)
    | VFloat32 f -> VFloat32 (float32 (math1 "fabs" (float f)))
    | VComplex (r, i) -> VFloat (complexAbs r i)
    | _ -> v

/// Apply an IRMath intrinsic (real result Float64, except abs which follows the
/// operand type, and complex operands which preserve the complex type).
let evalMath (name: string) (v: Value) : Value =
    if name = "abs" then evalAbs v
    else
        match v with
        | VComplex (r, i) -> let (xr, xi) = complexMath name r i in VComplex (xr, xi)
        // A Float32 operand: the double function at the widened operand,
        // rounded ONCE to float -- blade_libm's float overload for the
        // transcendentals, and exactly std::sqrt/floor/ceil(float) for the
        // correctly rounded ones (the arithmetic contract, docs/formalism.md
        // section 2.4). The node type stays Float64; the VALUE is float's.
        // lgamma/digamma take double on both sides (blade_rt::lgamma(double)).
        | VFloat32 f when name <> "lgamma" && name <> "digamma" ->
            VFloat (float (float32 (math1 name (float f))))
        | other -> VFloat (math1 name (asF64 other))

/// Explicit numeric cast, matching CodeGen's static_cast / complex-constructor
/// emission bit for bit: float->int truncates toward zero (blade_rt::f2i;
/// TypeCheck only licenses it through floor/ceil, so the value is already
/// integral -- or NaN / out of range, which panics BL8014 in every lane), int64->int32 wraps two's-complement, and a Complex64 target
/// squeezes both components through float32 -- VComplex stores doubles (the
/// Value DU has no width-tagged complex case), so the narrowing is applied to
/// the components exactly where C++ stores complex<float>.
let private evalCast (loc: SrcLoc) (target: ElemType) (v: Value) : Value =
    let bad () =
        raise (InterpPanic("BL9001", $"numeric cast to {castNameOf target} on an unsupported operand (typecheck licenses casts, so this is an interpreter bug)", None, 0))
    let asRealF64 () =
        match v with
        | VInt n -> float n
        | VInt32 n -> float n
        | VFloat f -> f
        | VFloat32 f -> float f
        | _ -> bad ()
    match target with
    | ETFloat64 -> VFloat (asRealF64 ())
    | ETFloat32 ->
        // Narrow through float32 with a single rounding from the SOURCE type:
        // C++ converts int64->float directly, so don't detour via double.
        (match v with
         | VInt n -> VFloat32 (float32 n)
         | VInt32 n -> VFloat32 (float32 n)
         | VFloat f -> VFloat32 (float32 f)
         | VFloat32 f -> VFloat32 f
         | _ -> bad ())
    | ETInt64 ->
        (match v with
         | VInt n -> VInt n
         | VInt32 n -> VInt (int64 n)
         | VFloat f -> VInt (floatToInt64At loc f)
         | VFloat32 f -> VInt (floatToInt64At loc (float f))
         | _ -> bad ())
    | ETInt32 ->
        (match v with
         | VInt n -> VInt32 (int32 n)
         | VInt32 n -> VInt32 n
         | VFloat f -> VInt32 (floatToInt32At loc f)
         | VFloat32 f -> VInt32 (floatToInt32At loc (float f))
         | _ -> bad ())
    | ETComplex128 ->
        (match v with
         | VComplex _ -> v
         | _ -> VComplex (asRealF64 (), 0.0))
    | ETComplex64 ->
        (match v with
         | VComplex (r, i) -> VComplex (float (float32 r), float (float32 i))
         | _ -> VComplex (float (float32 (asRealF64 ())), 0.0))
    | ETBool | ETUnit | ETString -> bad ()

/// Evaluate a scalar unary operator, matching CodeGen's IRUnaryOp emission.
let evalUnaryOp (op: IRUnaryOp) (v: Value) : Value =
    match op with
    | IRNeg ->
        match v with
        | VInt n -> VInt (0L - n)
        | VInt32 n -> VInt32 (0 - n)
        | VFloat f -> VFloat (-f)
        | VFloat32 f -> VFloat32 (-f)
        | VComplex (r, i) -> VComplex (-r, -i)
        | _ -> v
    | IRNot ->
        match v with VBool b -> VBool (not b) | _ -> v
    | IRConj ->
        // std::conj on complex; the identity on reals (CodeGen emits the operand
        // bare for real operands, IR.fs unaryOpToCpp note).
        match v with VComplex (r, i) -> VComplex (r, -i) | _ -> v
    | IRReal ->
        match v with VComplex (r, _) -> VFloat r | _ -> v
    | IRImag ->
        match v with VComplex (_, i) -> VFloat i | _ -> VFloat 0.0
    | IRArg ->
        let (r, i) = asComplex v in VFloat (complexArg r i)
    | IRMath name -> evalMath name v
    | IRCast (target, loc) -> evalCast loc target v
