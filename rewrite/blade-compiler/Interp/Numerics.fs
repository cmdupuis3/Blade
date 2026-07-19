// Blade tree-walking interpreter — scalar numerics (Milestone 1 foundation).
//
// Bit-exact reimplementation of the scalar arithmetic that CodeGen EMITS, so the
// interpreter's printed output byte-matches the g++ -std=c++17 -O2 binaries (the
// differential gate). Every rule here was pinned empirically against the actual
// toolchain (MSYS2 ucrt64 g++ 15.2 + .NET 7 on Windows); see the arc report for
// the probe methodology. The mechanisms:
//
//   * Real binops mirror C++ usual-arithmetic-conversion promotion and integer
//     wraparound/truncation. The ONE non-obvious case: `int64 op float32` is
//     computed in `float` (C++ converts the int64 to float, not double) and only
//     then widened to the Float64 result type — computing it directly in double
//     diverges. cppArithElem encodes the C++ conversion; the Blade node type
//     (IR.promoteElemType) is applied afterward.
//   * Complex128 +,-,*,/ replicate libgcc __muldc3 (naive product + Annex-G NaN
//     recovery) and __divdc3 (Smith scaled division + recovery), verified
//     bit-for-bit incl. the NaN/inf recovery paths and NaN sign bits.
//   * complex-mixed-with-real uses std::complex's SCALAR overloads (component
//     scale / real-part-only add), NOT full complex arithmetic — CodeGen leaves
//     a real operand un-promoted (coerceComplexOperand), so C++ resolves the
//     mixed overload. Diverges from full complex on signed-zero / non-finite.
//   * Scalar libm intrinsics: on THIS platform g++'s std::<fn> and .NET Math.*
//     BOTH bottom out in ucrtbase, so they are bit-identical. The lone exception
//     is hypot (no .NET managed equivalent; naive sqrt(x*x+y*y) diverges) — it
//     routes through ucrtbase.dll. Backend choice is a data table (mathBackend),
//     not scattered code, so any function can be re-pinned to the ucrt shim if a
//     future battery reveals a managed divergence.
//
// Compiled inside Blade.fsproj AFTER IR.fs/CodeGen.fs. Depends on Value.fs, the
// IR op discriminators (IRBinOp/IRUnaryOp, IR.fs:25/36), ElemType (Types.fs:285),
// and IR.promoteElemType (IR.fs:417, the compiler's own node-typing rule — reused
// so the interpreter cannot drift from it). The bit-critical core (complex ops,
// math dispatch, cppArithElem) is written over plain float/int so it is testable
// standalone via dotnet fsi with stubbed types.
module Blade.Interp.Numerics

open System
open System.Runtime.InteropServices
open Blade.Types
open Blade.IR
open Blade.Interp.Value

// ============================================================================
// ucrtbase.dll shims (the exact library the g++ binaries call)
// ============================================================================
// MinGW ucrt64's libstdc++ forwards <cmath> to ucrtbase; calling ucrtbase
// directly is therefore provably identical to the compiled binary. Verified
// bit-for-bit over a 284-value battery for every function below.
module private Ucrt =
    [<DllImport("ucrtbase.dll", CallingConvention=CallingConvention.Cdecl)>] extern double exp(double x)
    [<DllImport("ucrtbase.dll", CallingConvention=CallingConvention.Cdecl)>] extern double log(double x)
    [<DllImport("ucrtbase.dll", CallingConvention=CallingConvention.Cdecl)>] extern double sqrt(double x)
    [<DllImport("ucrtbase.dll", CallingConvention=CallingConvention.Cdecl)>] extern double sin(double x)
    [<DllImport("ucrtbase.dll", CallingConvention=CallingConvention.Cdecl)>] extern double cos(double x)
    [<DllImport("ucrtbase.dll", CallingConvention=CallingConvention.Cdecl)>] extern double tan(double x)
    [<DllImport("ucrtbase.dll", CallingConvention=CallingConvention.Cdecl)>] extern double sinh(double x)
    [<DllImport("ucrtbase.dll", CallingConvention=CallingConvention.Cdecl)>] extern double cosh(double x)
    [<DllImport("ucrtbase.dll", CallingConvention=CallingConvention.Cdecl)>] extern double tanh(double x)
    [<DllImport("ucrtbase.dll", CallingConvention=CallingConvention.Cdecl)>] extern double asin(double x)
    [<DllImport("ucrtbase.dll", CallingConvention=CallingConvention.Cdecl)>] extern double acos(double x)
    [<DllImport("ucrtbase.dll", CallingConvention=CallingConvention.Cdecl)>] extern double atan(double x)
    [<DllImport("ucrtbase.dll", CallingConvention=CallingConvention.Cdecl)>] extern double floor(double x)
    [<DllImport("ucrtbase.dll", CallingConvention=CallingConvention.Cdecl)>] extern double ceil(double x)
    [<DllImport("ucrtbase.dll", CallingConvention=CallingConvention.Cdecl)>] extern double fabs(double x)
    [<DllImport("ucrtbase.dll", CallingConvention=CallingConvention.Cdecl)>] extern double pow(double x, double y)
    [<DllImport("ucrtbase.dll", CallingConvention=CallingConvention.Cdecl)>] extern double atan2(double y, double x)
    [<DllImport("ucrtbase.dll", CallingConvention=CallingConvention.Cdecl, EntryPoint="hypot")>] extern double hypot(double x, double y)

/// Per-intrinsic backend. `Managed` = .NET Math.* (exact/correctly-rounded and
/// identical to ucrt for these; cheaper, no marshalling). `Ucrt` = ucrtbase.dll
/// P/Invoke (provably what g++ calls). The choice is DATA — flip an entry to
/// Ucrt to eliminate any residual managed-divergence risk for that function.
type MathBackend =
    | Managed
    | Ucrt

/// Backend selection. Transcendentals + pow/atan2/hypot default to Ucrt (the
/// zero-risk, provably-g++-identical path); the exact algebraic/rounding ops
/// (sqrt, floor, ceil) use Managed (IEEE-correctly-rounded => identical
/// everywhere, and avoid marshalling). hypot has NO managed equivalent, so it is
/// Ucrt unconditionally.
let mathBackend : Map<string, MathBackend> =
    Map.ofList [
        "exp", Ucrt;  "log", Ucrt;   "sin", Ucrt;  "cos", Ucrt;  "tan", Ucrt
        "sinh", Ucrt; "cosh", Ucrt;  "tanh", Ucrt
        "asin", Ucrt; "acos", Ucrt;  "atan", Ucrt
        "sqrt", Managed; "floor", Managed; "ceil", Managed
        "pow", Ucrt;  "atan2", Ucrt; "hypot", Ucrt ]

let private managed1 (name: string) (x: float) : float =
    match name with
    | "exp" -> Math.Exp x | "log" -> Math.Log x | "sqrt" -> Math.Sqrt x
    | "sin" -> Math.Sin x | "cos" -> Math.Cos x | "tan" -> Math.Tan x
    | "sinh" -> Math.Sinh x | "cosh" -> Math.Cosh x | "tanh" -> Math.Tanh x
    | "asin" -> Math.Asin x | "acos" -> Math.Acos x | "atan" -> Math.Atan x
    | "floor" -> Math.Floor x | "ceil" -> Math.Ceiling x
    | "fabs" | "abs" -> Math.Abs x
    | _ -> nan

let private ucrt1 (name: string) (x: float) : float =
    match name with
    | "exp" -> Ucrt.exp x | "log" -> Ucrt.log x | "sqrt" -> Ucrt.sqrt x
    | "sin" -> Ucrt.sin x | "cos" -> Ucrt.cos x | "tan" -> Ucrt.tan x
    | "sinh" -> Ucrt.sinh x | "cosh" -> Ucrt.cosh x | "tanh" -> Ucrt.tanh x
    | "asin" -> Ucrt.asin x | "acos" -> Ucrt.acos x | "atan" -> Ucrt.atan x
    | "floor" -> Ucrt.floor x | "ceil" -> Ucrt.ceil x
    | "fabs" | "abs" -> Ucrt.fabs x
    | _ -> nan

/// Apply a single-argument real intrinsic through its selected backend.
let math1 (name: string) (x: float) : float =
    match Map.tryFind name mathBackend with
    | Some Ucrt -> ucrt1 name x
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

/// hypot(x, y) — the std::abs(complex) backend. No managed equivalent that
/// matches; always ucrtbase.
let mathHypot (x: float) (y: float) : float = Ucrt.hypot(x, y)

// ============================================================================
// Complex128 arithmetic (bit-exact: libgcc __muldc3 / __divdc3)
// ============================================================================

let inline private isInf (x: float) = Double.IsInfinity x
let inline private isNan (x: float) = Double.IsNaN x
let inline private isFin (x: float) = not (Double.IsInfinity x) && not (Double.IsNaN x)
let inline private csign (m: float) (s: float) = Math.CopySign(m, s)
let private INF = Double.PositiveInfinity

/// (a+bi)*(c+di) — naive product with C99 Annex-G NaN/inf recovery, exactly as
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

/// (a+bi)/(c+di) — Smith's scaled division with libgcc __divdc3 recovery.
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

// --- complex ⊕ real: std::complex's mixed SCALAR overloads (verified) ---
// Applied when exactly one operand renders as complex in the emitted C++.
let private addCR a b s = (a + s, b)          // (a+bi) + s
let private subCR a b s = (a - s, b)          // (a+bi) - s
let private mulCR a b s = (a * s, b * s)      // (a+bi) * s
let private divCR a b s = (a / s, b / s)      // (a+bi) / s
let private addRC s a b = (s + a, b)          // s + (a+bi)
let private subRC s a b = (s - a, 0.0 - b)    // s - (a+bi)  (imag = +0 − b)
let private mulRC s a b = (s * a, s * b)      // s * (a+bi)
let private divRC s a b = complexDiv s 0.0 a b // s / (a+bi) : full __divdc3 of (s,0)/(a,b)

// ============================================================================
// Value <-> primitive coercions
// ============================================================================

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

/// Coerce a value to complex components. A real operand becomes (v, 0.0) — the
/// same widening CodeGen applies (coerceComplexOperand casts to the component
/// real type; the imaginary part is an implicit +0).
let private asComplex (v: Value) : float * float =
    match v with
    | VComplex (r, i) -> (r, i)
    | other -> (asF64 other, 0.0)

// ============================================================================
// C++ usual-arithmetic-conversion type
// ============================================================================
// The type C++ EVALUATES a real binop in (distinct from the Blade node type
// which is IR.promoteElemType). Ranks: Float64 > Float32 > Int64 > Int32. The
// higher-ranked operand wins. This is where `int64 + float32` becomes `float`
// (Float32 outranks Int64) — the value is then converted to the Float64 result.
let private numRank (et: ElemType) =
    match et with
    | ETFloat64 -> 5 | ETFloat32 -> 4 | ETInt64 -> 3 | ETInt32 -> 2 | _ -> 1

let cppArithElem (le: ElemType) (re: ElemType) : ElemType =
    if numRank le >= numRank re then le else re

// ============================================================================
// Runtime faults for arithmetic
// ============================================================================
// Integer division/modulo by zero is UB in C++ (a SIGFPE trap, no output). The
// interpreter fails loudly instead — there is no matching printed output to
// reproduce. Code is advisory (not a registered blade_rt panic site).
let private divByZero () : 'a =
    raise (InterpPanic("BL8007", "integer division or modulo by zero", None, 0))

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
/// producing a value of THAT type. Integer +,-,* wrap (two's complement); / and
/// % truncate toward zero (matching C++ and F#'s int operators).
let private computeReal (op: IRBinOp) (comp: ElemType) (l: Value) (r: Value) : Value =
    match comp with
    | ETInt32 ->
        let a = asI32 l
        let b = asI32 r
        match op with
        | IRAdd -> VInt32 (a + b)
        | IRSub -> VInt32 (a - b)
        | IRMul -> VInt32 (a * b)
        | IRDiv -> if b = 0 then divByZero () else VInt32 (a / b)
        | IRMod -> if b = 0 then divByZero () else VInt32 (a % b)
        | _ -> VInt32 0
    | ETInt64 ->
        let a = asI64 l
        let b = asI64 r
        match op with
        | IRAdd -> VInt (a + b)
        | IRSub -> VInt (a - b)
        | IRMul -> VInt (a * b)
        | IRDiv -> if b = 0L then divByZero () else VInt (a / b)
        | IRMod -> if b = 0L then divByZero () else VInt (a % b)
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

// ============================================================================
// Complex transcendental intrinsics (BEST-EFFORT — NOT bit-verified)
// ============================================================================
// libstdc++ implements complex exp/log/sqrt/trig with its own algorithms; these
// standard formulas are close but NOT guaranteed to match its exact operation
// order. The realistic complex corpus (spectra/FFT) uses only +,-,*,/ and abs,
// which ARE bit-exact above. A later milestone should probe libstdc++'s complex
// transcendentals and pin these. Until then, unsupported names fail loudly
// rather than silently miscompute.
let complexMath (name: string) (re: float) (im: float) : float * float =
    match name with
    | "exp" ->
        let e = math1 "exp" re
        (e * math1 "cos" im, e * math1 "sin" im)
    | "log" ->
        (math1 "log" (complexAbs re im), complexArg re im)
    | "sqrt" ->
        // libstdc++ std::sqrt(complex<double>) is Kahan's branch algorithm (NOT
        // the polar m*(cos(t/2),sin(t/2)) formula, which loses the exact-zero real
        // part: sqrt(-1+0i) came out (6.12e-17, 1) instead of (0, 1)). Ported
        // arm-for-arm from libstdc++ <complex>, bit-verified against g++ -O2 over
        // 11 operands (incl. neg1, 3+4i, -3-4i, 2i, subnormal 1e-300): every hex
        // pair identical. std::abs(z)=hypot (complexAbs), abs(x)=fabs, sqrt=Managed
        // (correctly-rounded ≡ ucrtbase).
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
    | _ ->
        raise (InterpPanic("BL8011",
            sprintf "complex intrinsic '%s' is not yet bit-verified in the interpreter" name,
            None, 0))

/// z ^ w for complex — best-effort exp(w * log z); NOT bit-verified (see above).
let private complexCaret (l: Value) (r: Value) : Value =
    let (zr, zi) = asComplex l
    let (wr, wi) = asComplex r
    let (lr, li) = complexMath "log" zr zi
    let (pr, pi) = complexMul wr wi lr li   // w * log z
    let (er, ei) = complexMath "exp" pr pi
    VComplex (er, ei)

// ============================================================================
// Scalar binop / unaryop dispatch (mirrors CodeGen's IRBinOp / IRUnaryOp)
// ============================================================================

let private evalArith (op: IRBinOp) (l: Value) (r: Value) : Value =
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
                // `^` emits pow(l, r): compute in double, then to the node type
                // (e.g. Int64^Int64 truncates the double result to int64).
                convertTo resElem (VFloat (mathPow (asF64 l) (asF64 r)))
            | _ ->
                let comp = cppArithElem le re
                convertTo resElem (computeReal op comp l r)
    | _ ->
        // Non-scalar-numeric: string concatenation is the only IRBinOp Blade
        // lowers here (`(l + r)` on std::string).
        match op, l, r with
        | IRAdd, VString a, VString b -> VString (a + b)
        | _ -> raise (InterpPanic("BL8010", "unsupported operand types for binary operator", None, 0))

// IEEE-exact per-type comparisons. Direct-typed float operators compile to the
// IEEE ordered/unordered comparisons (NaN => false for </<=/>/>=/=, true for <>),
// matching C++. (F#'s generic `compare` does NOT — it total-orders NaN — so it is
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

let private evalCompare (op: IRBinOp) (l: Value) (r: Value) : Value =
    match l, r with
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
        // bytes in std::string; .NET strings are UTF-16 — ordinal comparison
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
        | _ -> raise (InterpPanic("BL8010", "unsupported operand types for comparison", None, 0))

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
/// the C++ CodeGen emits (promotion, wraparound, complex coercion).
let evalBinOp (op: IRBinOp) (l: Value) (r: Value) : Value =
    match op with
    | IREq | IRNeq | IRLt | IRLe | IRGt | IRGe -> evalCompare op l r
    | IRAnd | IROr -> evalLogical op l r
    | IRAdd | IRSub | IRMul | IRDiv | IRMod | IRCaret -> evalArith op l r

/// abs(x): std::abs, whose C++ overload preserves the operand's numeric type
/// (llabs->int64, fabs->double, fabsf->float, hypot->double magnitude for
/// complex). Two's-complement wrap on INT_MIN matches C++'s llabs/abs.
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
        // NOTE: a Float32 operand would use C++'s float overload (expf, ...);
        // that rare path is computed through double here and is a documented
        // minor divergence (MathF/…f pinning deferred).
        | other -> VFloat (math1 name (asF64 other))

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
