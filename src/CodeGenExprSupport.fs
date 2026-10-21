// Expression-emission support: the CUDA device dialect gate, complex-type
// vocabulary, operator rendering, capture forwarding, fresh-return facts,
// dep-idx extent solving, and the fp-reassoc lane/SIMD builders.
module Blade.CodeGenExprSupport

open Blade.IR
open Blade.IRLoopStructure
open Blade.IRStorage
open Blade.IRLift
open Blade.IRMono
open Blade.IRPrint
open Blade.IRValidate
open Blade.Types
open Blade.EmitCpp
open Blade.ReynoldsCore
open Blade.CodeGenState

// CUDA device dialect (complex over CUDA): std::complex's operators are
// HOST-ONLY under nvcc, so device code speaks thrust::complex instead
// (layout-compatible, both T[2]). The extern "C" wrapper SIGNATURES keep the
// std:: spelling (text-copied into the host .cpp; cudaMemcpy's void* params
// reinterpret with no casts). The gate below lets kernel BODIES emit thrust
// vocabulary while host rendering is unaffected.

/// AsyncLocal dialect gate (mirrors the other per-flow emission cells).
let internal cudaDeviceDialectStorage =
    System.Threading.AsyncLocal<bool ref>()

let internal cudaDeviceDialectCell () : bool ref =
    let v = cudaDeviceDialectStorage.Value
    if isNull (box v) then
        let fresh = ref false
        cudaDeviceDialectStorage.Value <- fresh
        fresh
    else v

/// Whether device-dialect (thrust) complex rendering is active.
let inCudaDeviceDialect () : bool = (cudaDeviceDialectCell ()).Value

/// Run `f` with device-dialect complex rendering ON, restoring on exit.
let withCudaDeviceDialect (f: unit -> 'a) : 'a =
    let cell = cudaDeviceDialectCell ()
    let prev = cell.Value
    cell.Value <- true
    try f () finally cell.Value <- prev

/// Device (.cu kernel / device-buffer) spelling of an element type: complex
/// maps to thrust::complex; everything else matches elemTypeToCpp. Matched
/// via AnyPrimElem FIRST so a unit-annotated or idx-tagged complex erases to
/// the thrust type (the .cu has no host typedef aliases).
let cudaDevElemTypeToCpp (ty: IRType) : string =
    match ty with
    | AnyPrimElem ETComplex64 -> "thrust::complex<float>"
    | AnyPrimElem ETComplex128 -> "thrust::complex<double>"
    | _ -> elemTypeToCpp ty

/// Spelling of a complex scalar type in the current dialect.
let complexCppTypeName (et: ElemType) : string =
    match et, inCudaDeviceDialect () with
    | ETComplex64, false -> "std::complex<float>"
    | ETComplex64, true -> "thrust::complex<float>"
    | _, false -> "std::complex<double>"
    | _, true -> "thrust::complex<double>"


/// Extract the element type from an expression that should be array-shaped or scalar.
/// On failure (type not array/scalar after upstream inference), record a codegen
/// warning and emit a `#error` line as the rendered code. The point is to fail
/// loudly rather than silently emit code that might miscompile -- e.g., indexing
/// with a float, or narrowing int64 keys to double in a sort comparator.
/// Returns (elemType as IRType, optional error code).
let inferElemTypeStrict (ctx: CodeGenContext) (ind: string) (expr: IRExpr) (opName: string) : IRType * string list =
    match inferExprType expr with
    | ArrayElem arr -> (arr.ElemType, [])
    | IRTScalar _ as t -> (t, [])
    | t ->
        let msg = sprintf "%s: could not determine element type from expression (got %A) -- likely a typechecker or IR bug" opName t
        let errLines = codegenError ctx ind msg
        // Sentinel: the #error makes the C++ refuse to compile, so the
        // Float64 we return is never actually exercised in valid output.
        (IRTScalar ETFloat64, errLines)


/// Variant of inferElemTypeStrict for ctx-less callers (e.g. inside
/// `exprToCpp`). On failure, records a warning into the AsyncLocal
/// exprWarnings collector and returns the sentinel `BLADE_UNRESOLVED_INLINE_ELEM_TYPE`
/// (not a valid C++ identifier fragment in context) so a regression surfaces
/// as a precise g++ "unknown type name" error rather than a silent `double`.
let inferInlineElemTypeStr (opName: string) (form: IRExpr) : string =
    let arrExpr =
        match form with
        // IRMask deliberately NOT extracted: its result elem is Bool (the
        // presence array), independent of the source elem -- fall through to
        // `form` so inferExprType's IRMask arm answers.
        | IRSort (a, _)
        | IRIntersect (a, _) | IRUnion (a, _) -> a
        | IRUnique a -> a
        // stack/join carry no type of their own (Ir.CarriedType leaves the
        // rank-changing assembly combinators untyped); their result element
        // type is their operands', which TypeCheck has already unified.
        | IRStack (a :: _) | IRJoin (a :: _, _) -> a
        // eigh's RESULT is a tuple, which has no single element type -- asking
        // this function for one would collect a spurious "unresolvable element
        // type" warning and hand back the poison sentinel on a perfectly good
        // program. Answer the OPERAND's element type instead. The value is
        // never consumed (materializeEighForm ignores the caller's string and
        // derives BOTH element types itself, since Q's and LAM's can differ),
        // so this arm exists purely to keep the diagnostic honest.
        | IREigh a -> a
        | IRLu a -> a
        | _ -> form
    match inferExprType arrExpr with
    | ArrayElem a -> elemTypeToCpp a.ElemType
    // AnyPrimElem, not IRTScalar: a scalar under a TRANSPARENT wrapper --
    // IRTUnitAnnotated (physical units) or IRTIdxTagged (nominal index tag)
    // -- is a perfectly resolved element type. Both wrappers preserve their
    // inner type and erase at codegen (elemTypeToCpp/irTypeToCpp already
    // unwrap them), so matching only the bare form made a kernel-local
    // `let d = a - b` over Float<meters> elements collect a spurious
    // "likely a typechecker or IR bug" warning.
    | AnyPrimElem et -> primTypeToCpp et
    | t ->
        let cell = exprWarningsCell ()
        cell.Value <- cell.Value @
            [sprintf "%s: could not determine element type from inline form (got %A) -- likely a typechecker or IR bug" opName t]
        "BLADE_UNRESOLVED_INLINE_ELEM_TYPE"


// C++ Expression Generation

/// Convert binary operator to C++ string
let binOpToCpp = function
    | IRAdd -> "+" | IRSub -> "-" | IRMul -> "*" | IRDiv -> "/"
    | IRMod -> "%" | IRCaret -> "pow"  // Special handling needed
    | IREq -> "==" | IRNeq -> "!=" 
    | IRLt -> "<" | IRLe -> "<=" | IRGt -> ">" | IRGe -> ">="
    | IRAnd -> "&&" | IROr -> "||"
    | IRMath2 name -> name   // call-shaped, like IRCaret; see renderMath2

/// Render a BINARY math intrinsic. Call-shaped rather than infix, the same
/// exception `^` already needs (`pow(l, r)`), so every IRBinOp emission site
/// routes IRMath2 here before reaching binOpToCpp.
///   atan2(y, x)    -> blade_libm::atan2(y, x)
///   log_base(x, b) -> (blade_libm::log(x) / blade_libm::log(b))
/// There is no std::log_base; the quotient IS the definition, and emitting it
/// inline keeps the interpreter's mirror (Numerics.evalArith) a one-liner over
/// the same two std::log calls. Both operands are real by construction
/// (TypeCheck rejects complex ones), so no complex coercion is needed. In the
/// CUDA device dialect the names go UNQUALIFIED, matching renderUnaryOpTyped's
/// real-operand rule -- CUDA's device overloads live in the global namespace.
/// ONE ADDRESSED SAMPLE (IRRandAt, D13): `blade_rand::<kind>_one(key,
/// stream, index[, weights pool, k], pars..)` -- the fill's own transform at
/// one address (rand_runtime.hpp). `args` are rendered: key, stream, index,
/// then the Float64 parameters.
let renderRandAt (kind: string) (args: string list) (weights: (string * int) option) : string =
    match args with
    | key :: stream :: index :: pars ->
        let w =
            match weights with
            | Some (wStr, k) -> $", nested_array_utilities::pool_base(({wStr}).data), (size_t){k}LL"
            | None -> ""
        let ps = pars |> List.map (fun p -> $", (double)({p})") |> String.concat ""
        $"blade_rand::{kind}_one((int64_t)({key}), (int64_t)({stream}), (int64_t)({index}){w}{ps})"
    | _ -> "/* malformed IRRandAt */ 0"

let renderMath2 (name: string) (lStr: string) (rStr: string) : string =
    // Host: blade_libm:: -- the run-time libm, never a compile-time fold, and
    // its overloads round a Float32 pair once to float (the arithmetic
    // contract, docs/formalism.md section 2.4; blade_runtime.hpp).
    let q (fn: string) = if inCudaDeviceDialect () then fn else "blade_libm::" + fn
    match name with
    | "log_base" -> $"""({(q "log")}({lStr}) / {(q "log")}({rStr}))"""
    | _ -> $"{(q name)}({lStr}, {rStr})"

/// Convert unary operator to C++ string
let unaryOpToCpp = function
    | IRNeg -> "-"
    | IRNot -> "!"
    | IRConj -> "std::conj"   // function-call form; exprToCppCore/exprToCppSimple
                              // special-case IRConj for the complex-vs-real
                              // decision (real conj is the identity)
    // std::real/std::imag/std::arg are <complex> free functions with C++11
    // arithmetic-type overloads (std::real(double)=x, std::imag(double)=0,
    // std::arg(double)=0/pi), so they render through the generic unary arm on
    // both complex and real operands with no special-casing.
    | IRReal -> "std::real"
    | IRImag -> "std::imag"
    | IRArg -> "std::arg"
    // lgamma and digamma are Blade's OWN (blade_runtime.hpp), not libm's: the
    // interpreter has to reproduce them bit for bit and can borrow neither
    // ucrtbase's nor .NET's (there is no .NET gamma, and no libm psi at all).
    // Both sides run the same hand-rolled series -- see the header, and
    // Interp/Numerics.fs lgammaLanczos / digammaSeries.
    | IRMath "lgamma" -> "blade_rt::lgamma"
    | IRMath "digamma" -> "blade_rt::digamma"
    | IRMath name -> "std::" + name  // function-call form via the generic
                                     // `op(expr)` unary arm
    // Explicit numeric cast. Complex targets use the constructor form
    // (std::complex<T>(x) -- also the licensed spelling for the EXPLICIT
    // complex<float>(complex<double>) narrowing ctor); everything else is
    // static_cast. complexCppTypeName picks thrust:: in the device dialect;
    // static_cast is dialect-neutral.
    | IRCast ((ETComplex64 | ETComplex128) as et, _) -> complexCppTypeName et
    | IRCast (et, _) -> $"static_cast<{primTypeToCpp et}>"

/// Namespace-qualified spelling of a <complex>-vocabulary function in the
/// current dialect (std:: on the host, thrust:: inside CUDA device bodies).
let complexFnName (name: string) : string =
    (if inCudaDeviceDialect () then "thrust::" else "std::") + name

/// True iff a type's underlying scalar is a complex element type. Used to
/// decide whether conj must emit std::conj (complex) or is the identity (real).
let rec isComplexType (t: IRType) : bool =
    match t with
    | IRTScalar (ETComplex64 | ETComplex128) -> true
    | IRTIdxTagged (inner, _) -> isComplexType inner
    | IRTUnitAnnotated (inner, _) -> isComplexType inner
    | _ -> false

/// Project an IRType to its underlying scalar element type, if any.
let rec scalarElemOf (t: IRType) : ElemType option =
    match t with
    | IRTScalar et -> Some et
    | IRTIdxTagged (inner, _) -> scalarElemOf inner
    | IRTUnitAnnotated (inner, _) -> scalarElemOf inner
    | _ -> None

/// The real intrinsics whose value is the RUN-TIME platform libm's
/// (blade_runtime.hpp `blade_libm`; docs/formalism.md section 2.4). The
/// interpreter's mathBackend routes the same names to ucrtbase.
let libmRuntimeNames : Set<string> =
    Set.ofList [ "exp"; "log"; "log10"; "sin"; "cos"; "tan"; "sinh"; "cosh"; "tanh"
                 "asin"; "acos"; "atan" ]

/// The COMPLEX intrinsics, likewise the run-time library's (blade_runtime.hpp
/// binds each to its C99 function -- cexp, clog, csqrt, ..., catan -- behind
/// the same no-fold barrier). sqrt is in this set although real sqrt is not:
/// complex sqrt is not correctly rounded, so a fold is NOT the run-time value.
let complexLibmRuntimeNames : Set<string> =
    Set.ofList [ "exp"; "log"; "sqrt"; "sin"; "cos"; "tan"; "sinh"; "cosh"; "tanh"
                 "asin"; "acos"; "atan" ]

let private isComplexElem (et: ElemType) =
    match et with ETComplex64 | ETComplex128 -> true | _ -> false

// ---------------------------------------------------------------------------
// WHERE `Float64(x)` of an Int64 takes the split conversion (both lanes)
//
// Two spellings of ONE value: the plain conversion (`static_cast<double>` /
// `sitofp i64`) and the exact split (blade_arith::i64_to_f64 / EmitLlvm's
// emitI64ToF64: high word through the packed int32 convert, low word through
// the 2^52 magic exponent, the exact sum rounded once). Each is the double
// nearest the integer, ties to even, bit for bit -- so choosing between them
// moves no value and no pin, in any lane or build. They differ only in COST:
// AVX2 has no packed int64 -> double, so a loop holding the plain form does
// not vectorize (g++ refuses it; LLVM scalarizes it per lane, bench gaps
// G30/G33), while in SCALAR code the split is ~6 instructions where the plain
// form is one -- +27% on the bench's serial input generators, a scalar
// recurrence of integer ops around one `Float64(h)` per cell (bench
// loose-ends item 1).
//
// So the split is taken only in the code the cell loops of a CLOSED-FORM
// DOMAIN run: the domain map kernel itself (rendered inline in the nest) and
// every callable it reaches (band's `phi`, inlined into the run by the back
// end). Everywhere else the plain conversion stands. The set is a property of
// the whole module, computed once per emission (`i64SplitReachOf`) and read
// at each function's emission; the inline kernel sets the scope itself.
// ---------------------------------------------------------------------------

/// The map kernels of every `method_for(range<R>) <@> k` over a closed-form
/// domain in `e`, as callables.
let private domainMapKernelsIn (e: IRExpr) : IRCallable list =
    let acc = ResizeArray<IRCallable>()
    let rec walk (x: IRExpr) =
        (match x with
         | IRApplyCombinator info
             when info.Arrays |> List.exists (function
                      | IRRange ([ ix ], _) -> (match ix.Extent with IRSparseKeys (SkDomain _) -> true | _ -> false)
                      | _ -> false) ->
             (match resolveCallable info.Kernel with Some cl -> acc.Add cl | None -> ())
         | _ -> ())
        childrenOf x |> List.iter walk
    walk e
    List.ofSeq acc

/// The ids of every domain map kernel in the module and of every callable it
/// reaches through calls -- the bodies that take the split.
let i64SplitReachOf (modul: IRModule) : Set<IRId> =
    let roots =
        (modul.Bindings |> List.collect (fun b -> domainMapKernelsIn b.Value))
        @ (modul.Functions |> List.collect (fun f -> domainMapKernelsIn f.Body))
    let seen = System.Collections.Generic.HashSet<IRId>()
    let rec visit (cl: IRCallable) =
        if seen.Add cl.Id then
            let rec calls (x: IRExpr) =
                (match x with
                 | IRApp (f, _, _) -> (match resolveCallable f with Some callee -> visit callee | None -> ())
                 | _ -> ())
                childrenOf x |> List.iter calls
            calls cl.Body
    roots |> List.iter visit
    Set.ofSeq seen

let private i64SplitReachStorage = System.Threading.AsyncLocal<Set<IRId>>()
let private i64SplitScopeStorage = System.Threading.AsyncLocal<bool>()

/// Install the module's split set (i64SplitReachOf); emission entry points
/// call it once, before any body is rendered.
let setI64SplitReach (ids: Set<IRId>) : unit = i64SplitReachStorage.Value <- ids

/// Does this callable's body take the split?
let i64SplitReaches (id: IRId) : bool =
    let s = i64SplitReachStorage.Value
    not (isNull (box s)) && Set.contains id s

/// Is the code being rendered right now in split scope?
let i64SplitInScope () : bool = i64SplitScopeStorage.Value

/// Render `f` with the split scope set to `on`, restoring it after.
let withI64SplitScope (on: bool) (f: unit -> 'a) : 'a =
    let saved = i64SplitScopeStorage.Value
    i64SplitScopeStorage.Value <- on
    try f () finally i64SplitScopeStorage.Value <- saved

/// Render a function-call unary op, choosing the spelling by operand type and
/// active dialect. Host rendering is unaryOpToCpp verbatim. In the CUDA device
/// dialect: complex operands use thrust:: (thrust has no free real()/imag(), so
/// those emit __host__ __device__ member accessors instead); real operands emit
/// math intrinsics UNQUALIFIED (`exp(x)`) since CUDA's device overloads live in
/// the global namespace. Shared by exprToCppCore and exprToCppSimple.
let renderUnaryOpTyped (op: IRUnaryOp) (operandTy: IRType) (inner: string) : string =
    if inCudaDeviceDialect () then
        if isComplexType operandTy then
            match op with
            | IRReal -> $"({inner}).real()"
            | IRImag -> $"({inner}).imag()"
            | IRArg -> $"thrust::arg({inner})"
            | IRMath name -> $"thrust::{name}({inner})"
            | _ -> $"{(unaryOpToCpp op)}({inner})"
        else
            match op with
            | IRMath name -> $"{name}({inner})"
            | _ -> $"{(unaryOpToCpp op)}({inner})"
    else
        match op, scalarElemOf operandTy with
        // Float -> integer cast: truncation of a value the target can hold,
        // else a BL8014 panic (blade_rt::f2i; docs/formalism.md section 2.4).
        // A bare static_cast is UB on NaN / out-of-range (x86 answers the
        // INT_MIN sentinel) where the interpreter used to saturate.
        | IRCast ((ETInt64 | ETInt32) as et, loc), Some (ETFloat64 | ETFloat32) ->
            // The cast's source position rides into the panic's `--> file:line`.
            let at = panicSpanArgs loc.Span
            (if et = ETInt64 then $"blade_rt::f2i64({inner}, {at})" else $"blade_rt::f2i32({inner}, {at})")
        // Int64 -> Float64 in a closed-form domain's cell code: the exact
        // split (blade_arith::i64_to_f64), which vectorizes on AVX2 (bench gap
        // G30). Elsewhere the plain static_cast below -- the same double (see
        // "WHERE `Float64(x)` of an Int64 takes the split" above).
        | IRCast (ETFloat64, _), Some ETInt64 when i64SplitInScope () ->
            $"blade_arith::i64_to_f64({inner})"
        // A transcendental on a REAL operand: blade_libm::, the run-time
        // platform libm (never folded at compile time), whose overloads
        // evaluate a Float32 operand in double and round once to float and
        // widen an integral one -- the arithmetic contract (docs/formalism.md
        // section 2.4), mirrored by Interp/Numerics.evalMath. Real sqrt/floor/
        // ceil keep std::: correctly rounded, so a fold is the run-time value,
        // and sqrt must stay the vectorizable builtin.
        | IRMath name, et when libmRuntimeNames.Contains name
                               && not (et |> Option.exists isComplexElem) ->
            $"blade_libm::{name}({inner})"
        // A transcendental on a COMPLEX operand: blade_libm::'s complex
        // overloads, the C99 library function behind the same barrier (the
        // contract covers complex intrinsics too; complex<float> evaluates in
        // double and rounds each component once).
        | IRMath name, Some et when isComplexElem et && complexLibmRuntimeNames.Contains name ->
            $"blade_libm::{name}({inner})"
        | _ -> $"{(unaryOpToCpp op)}({inner})"

/// Coerce a rendered scalar operand to match std::complex's SAME-TYPE-ONLY
/// operator overload set (`complex<double> * 2` and `complex<double> *
/// complex<float>` both fail template deduction): integers and mismatched-width
/// floats cast to the component real type; a narrower complex widens via
/// std::complex's converting constructor. Matching types pass through unchanged.
let coerceComplexOperand (resultElem: ElemType) (operandElem: ElemType) (rendered: string) : string =
    match resultElem with
    | ETComplex128 ->
        match operandElem with
        | ETComplex128 | ETFloat64 -> rendered
        | ETComplex64 -> $"{(complexCppTypeName ETComplex128)}({rendered})"
        | ETFloat32 | ETInt64 | ETInt32 -> $"(double)({rendered})"
        | _ -> rendered
    | ETComplex64 ->
        match operandElem with
        | ETComplex64 | ETFloat32 -> rendered
        | ETInt64 | ETInt32 -> $"(float)({rendered})"
        | _ -> rendered
    | _ -> rendered

/// Emit a binop, inserting complex-operand coercions when the promoted result
/// is complex. `renderBin` is the caller's fallback (`(l op r)`), used verbatim
/// for the common non-complex path so nothing changes there.
let emitBinOpWithComplexCoercion
        (op: IRBinOp) (l: IRExpr) (r: IRExpr) (lStr: string) (rStr: string)
        (inferTy: IRExpr -> IRType) (binToCpp: IRBinOp -> string) : string =
    match scalarElemOf (inferTy l), scalarElemOf (inferTy r) with
    | Some le, Some re ->
        match promoteElemType le re with
        | Some ((ETComplex64 | ETComplex128) as resElem) ->
            let lC = coerceComplexOperand resElem le lStr
            let rC = coerceComplexOperand resElem re rStr
            $"({lC} {(binToCpp op)} {rC})"
        | _ -> $"({lStr} {(binToCpp op)} {rStr})"
    | _ -> $"({lStr} {(binToCpp op)} {rStr})"

/// The integer literal an operand IS, if it is one (`3`, `-1`).
let private intLiteralOf (e: IRExpr) : int64 option =
    match e with
    | IRLit (IRLitInt n) -> Some n
    | IRUnaryOp (IRNeg, IRLit (IRLitInt n)) -> Some (0L - n)
    | _ -> None

/// Render `/`, `%` and `^` under the ARITHMETIC CONTRACT (docs/formalism.md
/// section 2.4), which the interpreter (Interp/Numerics.fs) and the LLVM lane
/// (EmitLlvm.emitBinOp) implement identically:
///
///   * integer `/` `%` : a zero divisor panics BL8013 and MIN / -1 wraps
///     (blade_rt::idiv / imod). A nonzero literal divisor other than -1 can
///     do neither, so it stays a plain `/` -- the common `n / 2` costs
///     nothing and keeps its kernel's shadow frame elided.
///   * integer `^`     : exact, wrapping (blade_arith::ipow_nn); a
///     non-literal exponent may be negative, which panics (blade_rt::ipow).
///     It used to emit `pow(b, e)` -- a double, so 3^39 came back rounded and
///     an Int64 binding of it failed -Werror=float-conversion.
///   * real or complex `^` an INTEGER : blade_arith::fpowi / fpowif / cpowi,
///     repeated squaring in the base's type (a negative exponent is the
///     reciprocal of the positive power) -- never libm pow.
///   * real `^` real   : blade_arith::fpow (x*x at exponent 2, else libm pow),
///     rounded ONCE to Float32 when that is the node's type (pow(float, long)
///     is a double; storing it in a float was an implicit narrowing).
///
///   * complex `^` a floating or complex exponent : blade_libm::pow,
///     libstdc++'s algorithm over the run-time library functions (never
///     folded at compile time).
///
/// None = the caller's ordinary rendering. The CUDA device dialect keeps the
/// old `pow(l, r)` / infix spelling: device bodies cannot reach blade_rt or
/// blade_libm (host-only).
let renderContractBinOp
        (op: IRBinOp) (l: IRExpr) (r: IRExpr) (lStr: string) (rStr: string)
        (inferTy: IRExpr -> IRType) (loc: SrcLoc) : string option =
    let isInt et = match et with ETInt32 | ETInt64 -> true | _ -> false
    // The fault's source position, for the panic's `--> file:line`: constants
    // the guard reads only on its cold path (blade_runtime.hpp idiv/imod/ipow).
    let at = panicSpanArgs loc.Span
    match op with
    // Device integer `^`: EXACT and wrapping like the host's (section 2.4),
    // never the double `pow` it used to become. Only a literal nonnegative
    // exponent reaches here -- the negative-exponent panic cannot be raised
    // on the device, so a variable exponent is refused before rendering
    // (CodeGenCuda.cudaScalarNodeOk). The helper is emitted into the .cu
    // (getCudaFileContent) whenever a kernel names it.
    | IRCaret when inCudaDeviceDialect () ->
        match scalarElemOf (inferTy l), scalarElemOf (inferTy r), intLiteralOf r with
        | Some le, Some re, Some n when isInt le && isInt re && n >= 0L ->
            let w = if le = ETInt64 || re = ETInt64 then "64" else "32"
            Some $"blade_dev_ipow{w}({lStr}, {rStr})"
        | _ -> None
    | IRDiv | IRMod | IRCaret when not (inCudaDeviceDialect ()) ->
        match scalarElemOf (inferTy l), scalarElemOf (inferTy r) with
        | Some le, Some re when isInt le && isInt re ->
            let w = if le = ETInt64 || re = ETInt64 then "64" else "32"
            match op, intLiteralOf r with
            | (IRDiv | IRMod), Some n when n <> 0L && n <> -1L -> None
            | IRDiv, _ -> Some $"blade_rt::idiv{w}({lStr}, {rStr}, {at})"
            | IRMod, _ -> Some $"blade_rt::imod{w}({lStr}, {rStr}, {at})"
            | _, Some n when n >= 0L -> Some $"blade_arith::ipow_nn{w}({lStr}, {rStr})"
            | _ -> Some $"blade_rt::ipow{w}({lStr}, {rStr}, {at})"
        | Some le, Some re when op = IRCaret ->
            // The C++ EVALUATION width, not the node type: Float32 with an
            // integer computes in float (a literal exponent adapts, `s ^ 2`
            // over a Float32 `s` is a Float32), exactly as `s * 2` does.
            let isReal et = match et with ETFloat32 | ETFloat64 | ETInt32 | ETInt64 -> true | _ -> false
            // AN INTEGER EXPONENT keeps the base's type and is computed by
            // repeated squaring (formalism 2.4: the exponent of `^` is
            // polymorphic) -- blade_arith::fpowi / fpowif / cpowi, never libm
            // pow. An Int32 exponent widens to the int64_t parameter
            // implicitly (exact; no cast spelled -- `static_cast<T>(` reads as
            // a call through a value to the shadow-frame scan).
            if isInt re && (isComplexElem le || le = ETFloat64 || le = ETFloat32) then
                let e = rStr
                if isComplexElem le then Some $"blade_arith::cpowi({lStr}, {e})"
                elif le = ETFloat64 then Some $"blade_arith::fpowi({lStr}, {e})"
                else Some $"blade_arith::fpowif({lStr}, {e})"
            // Complex `^` with a FLOATING (or complex) exponent:
            // blade_libm::pow, the libstdc++ algorithm over the
            // no-fold barrier functions (blade_runtime.hpp). A real operand
            // is cast to the component type first so the call lands on one
            // of the three (complex|real, complex|real) overloads exactly.
            elif isComplexElem le || isComplexElem re then
                match promoteElemType le re with
                | Some ((ETComplex64 | ETComplex128) as resElem) ->
                    Some $"blade_libm::pow({(coerceComplexOperand resElem le lStr)}, {(coerceComplexOperand resElem re rStr)})"
                | _ -> None
            elif not (isReal le && isReal re) then None
            elif le = ETFloat64 || re = ETFloat64 then Some $"blade_arith::fpow({lStr}, {rStr})"
            else Some $"blade_arith::fpowf({lStr}, {rStr})"
        | _ -> None
    | _ -> None

/// Render a float as a C++ double literal. `sprintf "%g"` would violate two
/// invariants: ROUND-TRIP precision (%g truncates to 6 sig figs, breaking any
/// test pinned finer than 1e-6) and FLOAT SPELLING (%g renders 2.0 as the bare
/// token `2`, an int literal, so `2.0 / 3.0` becomes integer division = 0).
/// "R" is shortest-round-trip; invariant culture guards decimal-comma locales;
/// the suffix check restores the `.0` spelling.
let floatToCppLiteral (f: float) : string =
    if System.Double.IsNaN f then "std::numeric_limits<double>::quiet_NaN()"
    elif System.Double.IsPositiveInfinity f then "std::numeric_limits<double>::infinity()"
    elif System.Double.IsNegativeInfinity f then "(-std::numeric_limits<double>::infinity())"
    else
        let s = f.ToString("R", System.Globalization.CultureInfo.InvariantCulture)
        if s.Contains "." || s.Contains "e" || s.Contains "E" then s else s + ".0"

/// The Float32 twin: an `f`-suffixed C++ literal, so float-context arithmetic
/// stays in float (a width-less `1.0` would promote the op to double and then
/// narrow at the store -- rejected by -Werror=float-conversion).
let float32ToCppLiteral (f: float32) : string =
    if System.Single.IsNaN f then "std::numeric_limits<float>::quiet_NaN()"
    elif System.Single.IsPositiveInfinity f then "std::numeric_limits<float>::infinity()"
    elif System.Single.IsNegativeInfinity f then "(-std::numeric_limits<float>::infinity())"
    else
        let s = f.ToString("R", System.Globalization.CultureInfo.InvariantCulture)
        (if s.Contains "." || s.Contains "e" || s.Contains "E" then s else s + ".0") + "f"

/// Quote a Blade string value as a C++ string literal. Escapes the minimal
/// set that would otherwise break the surrounding "..." token: backslash,
/// double-quote, and the four common control characters. Other characters
/// pass through, including UTF-8 multibyte sequences (which are valid as
/// raw bytes inside a C++ "..." literal).
let escapeStringLit (s: string) : string =
    let sb = System.Text.StringBuilder()
    sb.Append('"') |> ignore
    for c in s do
        match c with
        | '\\' -> sb.Append("\\\\") |> ignore
        | '"'  -> sb.Append("\\\"") |> ignore
        | '\n' -> sb.Append("\\n") |> ignore
        | '\r' -> sb.Append("\\r") |> ignore
        | '\t' -> sb.Append("\\t") |> ignore
        | '\000' -> sb.Append("\\0") |> ignore
        | _ -> sb.Append(c) |> ignore
    sb.Append('"') |> ignore
    sb.ToString()

/// Simplified exprToCpp that doesn't recurse into complex IR nodes
/// Used for kernel bodies in inline generation
let rec exprToCppSimple (names: Map<IRId, string>) (expr: IRExpr) : string =
    match expr with
    | IRLit (IRLitInt n) -> $"{n}L"
    | IRLit (IRLitFloat f) -> floatToCppLiteral f
    | IRLit (IRLitFloat32 f) -> float32ToCppLiteral f
    | IRLit (IRLitBool b) -> if b then "true" else "false"
    | IRLit (IRLitString s) -> $"std::string({(escapeStringLit s)})"
    | IRLit IRLitUnit -> "((void)0)"
    | IRVar (id, _) -> Map.tryFind id names |> Option.defaultValue ($"__v{id}")
    | IRParam (name, _, _) -> Blade.Types.TypeOrigins.cppName name
    | IRBinOp (_, op, l, r, loc) ->
        let lStr = exprToCppSimple names l
        let rStr = exprToCppSimple names r
        match renderContractBinOp op l r lStr rStr inferExprType loc, op with
        | Some s, _ -> s
        | None, IRCaret -> $"pow({lStr}, {rStr})"
        | None, IRMath2 name -> renderMath2 name lStr rStr
        | None, _ -> emitBinOpWithComplexCoercion op l r lStr rStr inferExprType binOpToCpp
    | IRFma (a, b, c) ->
        $"std::fma({(exprToCppSimple names a)}, {(exprToCppSimple names b)}, {(exprToCppSimple names c)})"
    | IRRandAt (kind, args, weights) ->
        renderRandAt kind (args |> List.map (exprToCppSimple names))
                     (weights |> Option.map (fun (w, n) -> (exprToCppSimple names w, n)))
    | IRUnaryOp (IRConj, e) ->
        let inner = exprToCppSimple names e
        if isComplexType (inferExprType e) then $"""{(complexFnName "conj")}({inner})"""
        else inner
    | IRUnaryOp (op, e) -> renderUnaryOpTyped op (inferExprType e) (exprToCppSimple names e)
    | IRGuard (cond, body) ->
        $"({(exprToCppSimple names cond)} ? {(exprToCppSimple names body)} : 0.0)"
    | other -> $"BLADE_UNSUPPORTED_EXPR_{(other.GetType().Name.ToUpper())}"

/// Convert IRLit to C++ literal string
let litToCpp (lit: IRLit) : string =
    match lit with
    | IRLitInt n -> $"{n}L"
    | IRLitFloat f -> floatToCppLiteral f
    | IRLitFloat32 f -> float32ToCppLiteral f
    | IRLitBool b -> if b then "true" else "false"
    | IRLitString s -> $"std::string({(escapeStringLit s)})"
    | IRLitUnit -> "((void)0)"  // Valid C++ no-op; should be elided by callers

// isRaggedArrayType / isRaggedRowType / isDepIdxArrayType /
// isCompoundArrayType / isSparseArrayType are declared ABOVE the
// irTypeToCpp/elemTypeToCpp recursion group (search "Array-SHAPE predicates"),
// so that `cppArrayTypeStr` can join that group and every signature-rendering
// site inside it reaches the same wrapper decision.

/// Array types whose storage is the plain dense pool + pointer skeleton that
/// `deallocate` walks. Ragged, compound and dep-idx layouts carry side tables
/// (lens/offsets/row pointers, compound_index_t) with their own ownership rules.
/// (Declared here rather than in the deallocation registry below because the
/// copy-in-place mut eligibility analysis and the registry both need it.)
let isFreeableDenseArrayType (at: IRArrayType) : bool =
    not (isCompoundArrayType at) && not (isSparseArrayType at)
    && not (isRaggedArrayType at) && not (isDepIdxArrayType at)

// Copy-in-place mut cell (analysis lives with the deallocation block further down)

let internal copyInPlaceMutsStorage =
    System.Threading.AsyncLocal<Map<IRId, int> ref>()

/// Module-level mut array bindings whose whole-array reassignments compile to a
/// copy-into-place, mapped to the STATIC element count of the shared pool.
/// Filled by `computeCopyInPlaceMuts` (below, where the freshness helpers live);
/// read here so `exprToCppCore`'s IRAssign arm can consult it. AsyncLocal for the
/// same per-parallel-test-task isolation as exprWarningsCell.
let copyInPlaceMutsCell () : Map<IRId, int> ref =
    let v = copyInPlaceMutsStorage.Value
    if isNull (box v) then
        let fresh = ref Map.empty
        copyInPlaceMutsStorage.Value <- fresh
        fresh
    else v

/// The single decision point shared by the two IRAssign emission arms and
/// computeScopeEscapes: does THIS assign compile to a copy-into-place?
/// Returns (targetId, rhsId, element count). Eligibility already proved that
/// every whole-array assign to an eligible target has a plain-IRVar RHS of the
/// same static shape, so matching the two id slots here is sufficient.
let copyInPlaceAssign (target: IRExpr) (value: IRExpr) : (IRId * IRId * int) option =
    match target, value with
    | LVVar tid, IRVar (rid, _) ->
        match Map.tryFind tid (copyInPlaceMutsCell ()).Value with
        | Some n -> Some (tid, rid, n)
        | None -> None
    | _ -> None

// cppArrayTypeStr is defined ABOVE, inside the irTypeToCpp/elemTypeToCpp
// recursion group, so that arrowSlotTypeForFuncSig (which renders
// std::function<> slots and lives in that group) reaches the SAME wrapper
// decision the declaration sites use.

/// P0 (compound-index materialization keystone): emit the C++ that builds a
/// `compound_index_t<RANK>` from a Blade bool mask VALUE, independent of any
/// provider, so any source-level compound producer (dense-array scatter,
/// range<CompoundIdx> driver, fill_random-built compound) can materialize the
/// index the same way. `maskName` names an in-scope `Array<bool, RANK>` mask,
/// `rank` is RANK, `idxName` is the base name for the emitted index variable.
///
/// Emits (in order):
///   std::array<size_t, RANK> <idxName>_extents = { <maskName>.extents[0], ... };
///   size_t <idxName>_grid = <maskName>.extents[0] * ... ;
///   bool* <idxName>_pool = nested_array_utilities::pool_base(<maskName>.data);
///   std::vector<bool> <idxName>_maskvec(<idxName>_pool, <idxName>_pool + <idxName>_grid);
///   compound_index_t<RANK>* <idxName> = new compound_index_t<RANK>("<idxName>", <idxName>_extents, <idxName>_maskvec);
///
/// Flattening relies on allocate<>'s single-contiguous-pool invariant: pool_base
/// gives the row-major (DFS) flat buffer that compound_index_t's enumerate()
/// also walks, so the mask bit order matches the index's tuple enumeration.
/// Returns (emitted lines, index variable name). Heap-allocated; the caller owns
/// bundling it into a Compound<T,RANK> wrapper (P0b).
let genCompoundIndexFromMask (maskName: string) (rank: int) (idxName: string) : string list * string =
    let extentTerms = [ for d in 0 .. rank - 1 -> $"{maskName}.extents[{d}]" ]
    let extentsInit = String.concat ", " extentTerms
    let gridExpr = String.concat " * " extentTerms
    let lines =
        if rank = 1 then
            // RANK-1 COMPACTION (bench gap G9): the specialization
            // compound_index_t<1> builds its kept-position table straight
            // from the flat bool pool -- a count pass and one branchless fill,
            // no std::vector<bool> copy and no hash node per present cell.
            // Same locals (`_grid`, `_pool`) as the general arm so the
            // consumers that read them (the CUDA range driver) see one shape.
            [ $"size_t {idxName}_grid = {gridExpr};"
              $"bool* {idxName}_pool = nested_array_utilities::pool_base({maskName}.data);"
              $"compound_index_t<1>* {idxName} = new compound_index_t<1>(\"{idxName}\", {idxName}_grid, {idxName}_pool); // rank-1 compaction: kept positions, no hash" ]
        else
            [ $$"""std::array<size_t, {{rank}}> {{idxName}}_extents = { {{extentsInit}} };"""
              $"size_t {idxName}_grid = {gridExpr};"
              $"bool* {idxName}_pool = nested_array_utilities::pool_base({maskName}.data);"
              $"std::vector<bool> {idxName}_maskvec({idxName}_pool, {idxName}_pool + {idxName}_grid);"
              $"compound_index_t<{rank}>* {idxName} = new compound_index_t<{rank}>(\"{idxName}\", {idxName}_extents, {idxName}_maskvec);" ]
    (lines, idxName)

/// `blade plan`'s line for how a `compound(dense, mask)` binding is
/// REPRESENTED (rule `compound`): a rank-1 mask takes the compaction
/// representation (`applied`); a rank-k mask keeps the hashed index
/// (`declined`, with the reason). Both lanes build the same representation
/// for everything the LLVM lane accepts (it refuses every compound that is
/// not a rank-1, trailing-free compaction), so the record holds for either.
let recordCompoundRepresentation (subject: string) (leadRank: int) (trailingDims: int) : unit =
    let trail = if trailingDims = 0 then "no trailing dims" else $"{trailingDims} trailing dim(s) per kept cell"
    if leadRank = 1 then
        recordCodegenDecision "compound" subject Blade.Effects.Applied
            [ "representation: rank-1 compaction (count pass + branchless kept-position fill, gather by position; no hash table; keyed reads by binary search over the kept positions)"
              $"rank-1 mask over one dense axis; {trail}" ]
    else
        recordCodegenDecision "compound" subject
            (Blade.Effects.Declined $"hashed SparseIdx: a rank-{leadRank} mask has no closed-form rank, so compound_index_t<{leadRank}> keeps one tuple->rank hash entry per present cell")
            [ $"representation: hashed SparseIdx (compound_index_t<{leadRank}>, lex-sorted tuple table + reverse hash)"
              $"rank-{leadRank} mask; {trail}" ]

/// Emit the construction of a standalone `sparse_index_t<RANK>` -- the sparse
/// twin of genCompoundIndexFromMask, keyed on the SparseKeysSource branch:
///
///   SkStatic entries -- the key table is a compile-time constant; emit it as a
///       braced vector literal. No runtime array is consulted (desync-proof).
///   SkRuntime keys   -- loop the named keys array (rank-1, std::tuple elements
///       for arity >= 2, plain integers for a rank-1 sparse) into the vector.
///       `keysName` is the resolved C++ name of that array.
///
/// Key order is preserved verbatim in both branches (given order == iteration
/// order). The index is heap-allocated; duplicate keys throw at construction
/// (sparse_index_t's ctor). Caller owns dealloc registration.
let genSparseIndexFromKeys (source: SparseKeysSource) (keysName: string option) (rank: int) (idxName: string) : string list =
    match source with
    | SkStatic entries ->
        let rows =
            entries
            |> List.map (fun e -> $$"""{ {{(e |> List.map string |> String.concat ", ")}} }""")
            |> String.concat ", "
        [ $$"""std::vector<std::array<size_t, {{rank}}>> {{idxName}}_keys = { {{rows}} };"""
          $"sparse_index_t<{rank}>* {idxName} = new sparse_index_t<{rank}>(\"{idxName}\", std::move({idxName}_keys));" ]
    | SkDomain plan ->
        // Closed-form enumeration (docs/plans/structural/06): nested loops,
        // level k over [lo_k, hi_k] with the ends affine in the earlier
        // levels, pushing each solution in lex order -- the same order the
        // interpreter and the compile-time certificate produce. Exactly the
        // solutions are visited (plus an empty inner loop at a dead prefix
        // of an unprojected general bound); the box is never scanned.
        let r = plan.Levels.Length
        let var k = $"__d{k}"
        let affine (f: DomainAffine) =
            f.Coefs |> List.fold (fun acc (j, a) -> $"{acc} + {a}LL * {var j}") $"({f.Const}LL)"
        let boundExpr (isLower: bool) (b: DomainBound) =
            let fn = if isLower then "std::max<int64_t>" else "std::min<int64_t>"
            match b |> List.map affine with
            | [] -> if isLower then "INT64_MIN" else "INT64_MAX"
            | first :: rest -> rest |> List.fold (fun acc t -> $"{fn}({acc}, {t})") first
        let opens =
            plan.Levels |> List.mapi (fun k lvl ->
                let ind = String.replicate k "    "
                $"{ind}for (int64_t {var k} = {boundExpr true lvl.Lo}; {var k} <= {boundExpr false lvl.Hi}; ++{var k}) {{")
        let push =
            let comps = [ for k in 0 .. r - 1 -> $"(size_t){var k}" ] |> String.concat ", "
            $"""{String.replicate r "    "}{idxName}_keys.push_back({{ {comps} }});"""
        let closes = [ for k in r - 1 .. -1 .. 0 -> String.replicate k "    " + "}" ]
        [ $"// enumerable domain {plan.Name}: {plan.Card} solution(s) in closed form, lex order (docs/plans/structural/06)"
          $"std::vector<std::array<size_t, {rank}>> {idxName}_keys; {idxName}_keys.reserve({plan.Card});" ]
        @ opens @ [ push ] @ closes
        @ [ $"sparse_index_t<{rank}>* {idxName} = new sparse_index_t<{rank}>(\"{idxName}\", std::move({idxName}_keys));" ]
    | SkRuntime _ ->
        match keysName with
        | Some kn ->
            let entryInit =
                if rank = 1 then $$"""{ (size_t)({{kn}}[__r]) }"""
                else
                    [ for c in 0 .. rank - 1 -> $"(size_t)std::get<{c}>({kn}[__r])" ]
                    |> String.concat ", "
                    |> sprintf "{ %s }"
            [ $"std::vector<std::array<size_t, {rank}>> {idxName}_keys({kn}.extents[0]);"
              $"for (size_t __r = 0; __r < {kn}.extents[0]; __r++) {idxName}_keys[__r] = {entryInit};"
              $"sparse_index_t<{rank}>* {idxName} = new sparse_index_t<{rank}>(\"{idxName}\", std::move({idxName}_keys));" ]
        | None ->
            [ refusalErrorLine "" "SparseIdx: runtime keys variable not found in scope at codegen" ]

/// Where a callable's captured bindings get their C++ names when its body is
/// rendered (F8: one helper for what were sixteen hand folds in two variants).
/// The two policies are two different LEXICAL situations, not two styles:
type CaptureNamePolicy =
    /// The body is rendered as its OWN C++ function (a lifted callable, its
    /// std::function closure, a CUDA __global__ kernel): every capture is a
    /// PARAMETER of that function, declared under the capture's own name, so
    /// that name must replace whatever the enclosing scope called the
    /// binding.
    | CapturesAreParameters
    /// The body is rendered INLINE in the enclosing scope (a loop nest, a
    /// peel, a fused kernel, a row map): the binding is read where it lives,
    /// under the name that scope EMITTED for it -- a block-local `let k` is
    /// `__v<id>` there, not `k` -- so an id the map already knows keeps its
    /// name and the capture's source name only fills a gap. Overwriting here
    /// re-spells the binding to its source name: an undeclared identifier,
    /// or worse, a different binding of the same name (a module-level `k`).
    | EnclosingNamesWin

/// Extend `names` with `captures` under `policy` (see CaptureNamePolicy).
let withCaptureNames (policy: CaptureNamePolicy) (captures: CaptureInfo list) (names: Map<IRId, string>) : Map<IRId, string> =
    captures |> List.fold (fun (m: Map<IRId, string>) (c: CaptureInfo) ->
        match policy with
        | CapturesAreParameters -> Map.add c.Id c.Name m
        | EnclosingNamesWin -> if Map.containsKey c.Id m then m else Map.add c.Id c.Name m) names

/// Resolve a capture to the C++ identifier naming it in the SCOPE where the
/// forwarding closure is emitted. A capture's `Name` is its SOURCE name,
/// correct for params/module-level bindings, but a block-local `let` is
/// renamed to `__v<id>` when its IRLet chain is flattened -- forwarding the
/// source name there would reference an undeclared identifier. Resolve
/// through the active name map (`names`/`ctx.VarNames`) and fall back to
/// the source name when unmapped.
let captureForwardName (names: Map<IRId, string>) (c: CaptureInfo) : string =
    Map.tryFind c.Id names |> Option.defaultValue c.Name

// GROUPED-CAPTURE FORWARDING.
//
// A `group_by` result is a row-pointer table (`Array<T*, 1>`, see
// genGroupByBinding) whose per-row lengths live OUTSIDE the value, in the
// driving `group_keys` binding's side state (`<gk>__ngroups`,
// `<gk>__offsets`). A lifted callable that captures one therefore needs
// three things the plain capture path cannot give it:
//
//   1. the row table typed as what it IS (`Array<T*, 1>&`) -- the IR rank-2
//      type would render `Array<T, 2>&`, which the call site's actual
//      `Array<T*, 1>` value cannot bind to;
//   2. the gk side state, forwarded as trailing hidden params (`size_t
//      __gk<id>__ngroups, const size_t* __gk<id>__offsets`), ONE pair per
//      DISTINCT gk across the captures -- the grouped-zip peel requires
//      co-grouped operands to resolve to the SAME stem, so two captures
//      grouped by one gk must share a pair;
//   3. a `ctx.GroupedArrays` seed in the callable's body context mapping the
//      capture's body-scope name to that stem, so every downstream consumer
//      (tryRaggedPeel, tryGroupedZipPeel, the peel-result extent sites)
//      resolves lengths exactly as it does in the frame that built the group.
//
// Parameter names and argument spellings agree by POSITION, not by text: the
// signature always spells the stem off the gk's VAR ID (`__gk<id>`), while a
// call site spells whatever the gk is named in ITS scope -- the emitted
// binding name where the gk is local (`__v<id>`/`gk`), or `__gk<id>` when the
// caller is itself a lifted callable holding the forwarded pair.
//
// Which capture is grouped comes from a module-level pre-pass
// (computeGroupedCaptureFacts): binding var id of a group_by result -> the
// gk binding's var id, harvested from `IRLet (id, IRGroupBy (_, IRVar gk), _)`
// in every function body plus module-level group_by bindings. HM
// specialization preserves body let ids (only param VarIds are remapped), so
// one fact set serves the original, the spec, and every lambda clone.
let internal groupedCaptureFactsStorage =
    System.Threading.AsyncLocal<Map<IRId, IRId> ref>()

let groupedCaptureFactsCell () : Map<IRId, IRId> ref =
    let v = groupedCaptureFactsStorage.Value
    if isNull (box v) then
        let fresh = ref Map.empty
        groupedCaptureFactsStorage.Value <- fresh
        fresh
    else v

let groupedCaptureGkOf (c: CaptureInfo) : IRId option =
    Map.tryFind c.Id (groupedCaptureFactsCell ()).Value

/// The C++ spelling of a gk's side-state stem in the CURRENT scope: the gk's
/// emitted name where it is in scope, `__gk<id>` (the forwarded-pair param
/// stem) where it is not.
let gkSidecarStem (names: Map<IRId, string>) (gkId: IRId) : string =
    match Map.tryFind gkId names with
    | Some n -> n
    | None -> $"__gk{gkId}"

/// Distinct gk ids across a callable's captures, in first-appearance order
/// (the order both the signature and every call site iterate).
let groupedCaptureGks (caps: CaptureInfo list) : IRId list =
    caps |> List.choose groupedCaptureGkOf |> List.distinct

/// Trailing hidden params carrying the gk side state, as (C++ type, name)
/// pairs -- the tail of CodeGen.captureParamDecls, which every declaration of
/// a lifted callable's signature renders.
let gkSidecarParams (caps: CaptureInfo list) : (string * string) list =
    groupedCaptureGks caps
    |> List.collect (fun gkId ->
        [ ("size_t", $"__gk{gkId}__ngroups")
          ("const size_t*", $"__gk{gkId}__offsets") ])

/// Capture ARGUMENTS at a call/wrapper site: the regular captures resolved
/// through the active name map, then the gk side-state pairs in the same
/// order the signature declares them.
let captureForwardArgs (names: Map<IRId, string>) (caps: CaptureInfo list) : string list =
    // A `.stream` capture forwarded from a scope that does not materialize it
    // names an array no C++ declaration carries: forward the deferred refusal
    // sentinel instead (CodeGenState, "STREAMED VALUES NEVER REACH C++").
    (caps |> List.map (fun c ->
        let n = captureForwardName names c
        match streamedValueSentinel c.Id n with
        | Some sentinel -> sentinel
        | None -> n))
    @ (groupedCaptureGks caps
       |> List.collect (fun gkId ->
           let stem = gkSidecarStem names gkId
           [ $"{stem}__ngroups"; $"{stem}__offsets" ]))

/// The pre-pass behind groupedCaptureGkOf: every `let <id> = group_by(_, gk)`
/// in any function body (and any module-level group_by binding) contributes
/// id -> gk's var id. Installed by genModule/genModuleSplit alongside the
/// other module-level fact caches.
let computeGroupedCaptureFacts (modul: IRModule) : Map<IRId, IRId> =
    let acc = System.Collections.Generic.Dictionary<IRId, IRId>()
    let strip e = match e with IRCompute inner -> inner | e -> e
    let note (id: IRId) (v: IRExpr) =
        match strip v with
        | IRGroupBy (_, gkE) ->
            (match strip gkE with
             | IRVar (gkId, _) -> acc.[id] <- gkId
             | _ -> ())
        | _ -> ()
    let walkBody (b: IRExpr) =
        iterIRExpr (fun e ->
            match e with
            | IRLet (id, v, _) -> note id v
            | _ -> ()) b
    for bind in modul.Bindings do
        note bind.Id bind.Value
        walkBody bind.Value
    for f in modul.Functions do
        walkBody f.Body
    acc |> Seq.map (fun kv -> (kv.Key, kv.Value)) |> Map.ofSeq

/// EXTENTS-ONLY GROUP_BY: which group_by bindings never have their VALUES read.
///
/// A grouped array's only legal consumers are ragged peels, and a peel whose
/// kernel touches its row solely through `extents(row)` reads the row's LENGTH
/// -- which the emitter computes from the gk offsets, not from the gathered
/// buffer. So if EVERY use of a group_by result is such a peel, the gather is
/// dead: `genGroupByBinding` still emits the row-pointer table (the peel indexes
/// it to build each RaggedRow) but skips the
/// per-group `new[]` and the O(n) copy, leaving the rows null. That is legal --
/// the pointer is read, never dereferenced -- and `delete[] nullptr` is a no-op,
/// so teardown is unchanged.
///
/// The user-facing point: `extents(gk)` is the direct spelling for per-group
/// sizes, and this makes the older `method_for(group_by(v, gk)) <@> lambda(r) ->
/// extents(r)` cost the same, so nobody has to know which one is fast.
///
/// FAIL-SAFE. The walk marks a binding BAD on any occurrence it cannot classify,
/// and only the classified-good set survives; an unrecognised use therefore
/// keeps the gather. Both directions are pinned (sql-group-by/043).
let computeExtentsOnlyGroupBys (modul: IRModule) : Set<IRId> =
    let strip e = match e with IRCompute inner -> inner | e -> e
    // Every id bound to a group_by, module-level or inside a body.
    let groupByIds = System.Collections.Generic.HashSet<IRId>()
    let noteBind (id: IRId) (v: IRExpr) =
        match strip v with IRGroupBy _ -> groupByIds.Add id |> ignore | _ -> ()
    for bind in modul.Bindings do
        noteBind bind.Id bind.Value
        iterIRExpr (fun e -> match e with IRLet (id, v, _) -> noteBind id v | _ -> ()) bind.Value
    for f in modul.Functions do
        iterIRExpr (fun e -> match e with IRLet (id, v, _) -> noteBind id v | _ -> ()) f.Body
    if groupByIds.Count = 0 then Set.empty else

    // A kernel is extents-only when its SOLE parameter is reached exclusively
    // through IRExtent. Counting works because iterIRExpr visits the IRExtent
    // node and its IRVar child both, so a row read any other way lifts `total`
    // without lifting `underExtent`.
    let kernelExtentsOnly (kernel: IRExpr) : bool =
        match resolveCallable kernel with
        | Some c when c.Params.Length = 1 ->
            let pid = c.Params.[0].VarId
            let mutable total = 0
            let mutable underExtent = 0
            iterIRExpr (fun e ->
                match e with
                | IRVar (i, _) when i = pid -> total <- total + 1
                | IRExtent (IRVar (i, _), _) when i = pid -> underExtent <- underExtent + 1
                | _ -> ()) c.Body
            total > 0 && total = underExtent
        | _ -> false

    // Sole grouped operand of a peel: co-iteration (Arrays > 1) binds rows from
    // several tables and is never elided.
    let soleGroupedOperand (info: ApplyInfo) : IRId option =
        match info.Arrays |> List.map strip with
        | [IRVar (gid, _)] when groupByIds.Contains gid -> Some gid
        | _ -> None

    // `bad` = the binding has at least one use that could read VALUES.
    //
    // Structural walk, not a count: an extents-only peel mentions its operand in
    // BOTH `Loop` and `Arrays`, so those subtrees are skipped wholesale rather
    // than tallied. Every other route to an `IRVar` naming a group_by marks it,
    // which is what makes the default "gather". The kernel is still walked -- it
    // is an IRVar naming a callable, and the callable's BODY is visited via
    // modul.Functions.
    let bad = System.Collections.Generic.HashSet<IRId>()
    // ExprShape is TOTAL (a leaf yields an empty child list), so it is the last
    // arm and needs no fallback after it.
    let rec scan (e: IRExpr) =
        match e with
        | IRApplyCombinator info ->
            (match soleGroupedOperand info with
             | Some gid when kernelExtentsOnly info.Kernel -> scan info.Kernel
             | _ -> (match e with ExprShape (cs, _) -> cs |> List.iter scan))
        | IRVar (i, _) when groupByIds.Contains i -> bad.Add i |> ignore
        | ExprShape (cs, _) -> cs |> List.iter scan
    for bind in modul.Bindings do scan bind.Value
    for f in modul.Functions do scan f.Body
    // The AUTO-PRINT is a consumer too: a printed top-level grouped array
    // shows its rows' VALUES (genPrintStatements, via the grouping's offsets),
    // so its gather is live. Only a binding the print selection leaves out
    // (`--print`, BLADE_PRINT) -- or one inside a function body, which is
    // never printed -- can still lose its gather to the rule above.
    let selection = printSelection ()
    for bind in modul.Bindings do
        match strip bind.Value with
        | IRGroupBy _ when (match selection with None -> true | Some names -> Set.contains bind.Name names) ->
            bad.Add bind.Id |> ignore
        | _ -> ()
    // Elidable is exactly the COMPLEMENT of `bad`, which is why a group_by
    // nothing consumes at all is elided too: its gather is dead for the same
    // reason, just more obviously. (Tracking the extents-only peels positively
    // and intersecting would exclude that case for no benefit -- an unused
    // binding has no use to classify, not an unclassifiable one.)
    Set.ofSeq groupByIds - Set.ofSeq bad

let internal extentsOnlyGroupBysStorage =
    System.Threading.AsyncLocal<Set<IRId> ref>()

let extentsOnlyGroupBysCell () : Set<IRId> ref =
    let v = extentsOnlyGroupBysStorage.Value
    if isNull (box v) then
        let fresh = ref Set.empty
        extentsOnlyGroupBysStorage.Value <- fresh
        fresh
    else v

/// GROUPED BINDINGS: binding id -> (emitted C++ name, grouping stem) for every
/// binding whose value is a group_by result -- the group_by itself, and an
/// alias of one (genVarAliasBinding). A grouped array's row LENGTHS live in
/// the grouping (`<gk>__offsets`), not in the value (whose inner extent is the
/// ragged placeholder 0), so the printer needs the grouping to print the rows;
/// it reads this map because genPrintStatements runs after body generation
/// and has no codegen context. Reset per assembly, like forcedDeferredIdsCell.
let internal groupedBindingsStorage =
    System.Threading.AsyncLocal<Map<IRId, string * string> ref>()

let groupedBindingsCell () : Map<IRId, string * string> ref =
    let v = groupedBindingsStorage.Value
    if isNull (box v) then
        let fresh = ref Map.empty
        groupedBindingsStorage.Value <- fresh
        fresh
    else v

let noteGroupedBinding (id: IRId) (cppName: string) (gkName: string) : unit =
    let cell = groupedBindingsCell ()
    cell.Value <- Map.add id (cppName, gkName) cell.Value

// CLOSURE VALUES CAPTURE BY VALUE.
//
// A lifted callable takes its captures as trailing `T&` parameters, and a
// closure VALUE over it (a lambda in value position, `let f = lambda ...`)
// is a C++ lambda forwarding them. That closure used to capture `[&]`, which
// is only sound while the closure is used inside the frame that owns the
// captured locals. A closure RETURNED from a function -- `function mk(i) =
// lambda(j) -> i * 10 + j` -- outlived `mk`'s frame and read a dead `i`: the
// compiled lane printed 3 for `mk(2)(3)` where the interpreter printed 23.
//
// A captured binding that is never REBOUND after its definition has one
// value for its whole life, so copying it into the closure is the same
// program as referring to it, and the copy survives any frame. Copying is the
// default: the clause is `[=] ... mutable` (`mutable` because the lifted
// signature binds each capture to a non-const `T&`, which the copy then
// satisfies). An array capture copies the `Array<>` WRAPPER -- a data pointer
// and an extents pointer, not the cells -- and its storage is already kept
// alive past the frame: computeScopeEscapes pins every captured binding
// ("Seed 2 -- captures"), so a closure never reads a freed pool.
//
// A REBOUND capture (`n = n + k`, written by the closure or by its scope)
// keeps `[&]`: copying it would give the closure its own `n`, silently
// diverging from the interpreter's shared cell (Interp/Value.fs VClosure).
// Such a closure is sound only while it cannot outlive the frame, which the
// type checker enforces (TypeCheckValidate.collectMutCaptureEscapes, BL4005):
// it may be called, used as a kernel, or let-bound, but never returned,
// stored, or passed where it could be kept.
//
// Element writes into an ARRAY (`a(i) = v`) are not rebinds -- the copied
// wrapper shares the cells -- so they leave a capture copyable.
//
// The consumer-local `__wrap_<id>` closures (genCallableWrapper) keep `[&]`
// unconditionally: they are generated by a loop emitter, named by nothing but
// that emitter, and dead before the statement that declared them ends.
let internal reboundVarIdsStorage =
    System.Threading.AsyncLocal<Set<IRId> ref>()

let reboundVarIdsCell () : Set<IRId> ref =
    let v = reboundVarIdsStorage.Value
    if isNull (box v) then
        let fresh = ref Set.empty
        reboundVarIdsStorage.Value <- fresh
        fresh
    else v

/// Every binding id some assignment REBINDS: the root of an assignment
/// target, unless the target lies inside an element of an array (a store into
/// cells a copied wrapper shares). A struct field write rebinds its root --
/// the struct is copied by value.
let computeReboundVarIds (modul: IRModule) : Set<IRId> =
    let acc = System.Collections.Generic.HashSet<IRId>()
    // The binding a store REBINDS, walking through fields; None once the walk
    // passes an array subscript (the store lands in shared cells). The type
    // checker's twin (TypeCheckValidate.collectMutCaptureEscapes, storeRoot)
    // must judge at least these bindings rebound: it refuses the escapes that
    // this `[&]` cannot survive.
    let rec storeRoot (e: IRExpr) =
        match e with
        | IRVar (id, _) -> Some id
        | IRFieldAccess (b, _) -> storeRoot b
        | IRIndex (b, _, _) ->
            (match inferExprType b with
             | ArrayElem _ -> None
             | _ -> storeRoot b)
        | _ -> None
    let visit (e: IRExpr) =
        match e with
        | IRAssign (target, _) -> storeRoot target |> Option.iter (acc.Add >> ignore)
        | _ -> ()
    for b in modul.Bindings do iterIRExpr visit b.Value
    for f in modul.Functions do iterIRExpr visit f.Body
    Set.ofSeq acc

/// The capture clause and lambda specifier of a closure VALUE over a lifted
/// callable with these captures: `("[=]", " mutable")` when none is rebound,
/// `("[&]", "")` otherwise (see CLOSURE VALUES CAPTURE BY VALUE).
let closureValueCaptureOf (ids: IRId list) : string * string =
    let rebound = (reboundVarIdsCell ()).Value
    if ids |> List.exists (fun id -> Set.contains id rebound) then ("[&]", "")
    else ("[=]", " mutable")

let closureValueCapture (caps: CaptureInfo list) : string * string =
    closureValueCaptureOf (caps |> List.map (_.Id))

/// Wrapper-emission helper: a local C++ closure mediating between a lifted
/// function's signature (regular + capture params) and a consumer's expected
/// shape (regular params only), so `IRVar(callable.Id, funcType)` can stand in
/// wherever the callable is called with just its regular params.
///
/// Shape: `auto __wrap_<id>_<suffix> = [&](P1 p1, P2 p2) { return <fnName>(p1, p2, c1, c2); };`
///
/// Captures are hidden from the wrapper's signature and forwarded by reference
/// via `[&]`, matching the `T& cap_name` capture-param signature `genFuncDef`
/// emits on the lifted side. `[&]` is sound HERE (unlike a closure value --
/// closureValueCapture) because the wrapper is consumer-local: the emitter
/// that asked for it is its only user, within the statement it emits. Resolved
/// through `names` (`captureForwardName`) so
/// a renamed block-local `let` (`__v<id>`) forwards its EMITTED identifier.
///
/// Return type is `auto`: avoids computing an explicit type for IRTInfer/
/// synthesized RetTypes and handles void returns for free (C++14 deduces void).
///
/// Wrapper name = callable id + caller-supplied suffix, to disambiguate one
/// callable referenced from multiple consumer sites in the same C++ scope
/// (suffix is typically the let binding's name or a fresh counter; pass "" when
/// the caller guarantees a single emission per scope).
///
/// Returns (code lines, wrapper name); callers prepend the lines and use the
/// name wherever they'd otherwise inline the lambda body.
let genCallableWrapper (names: Map<IRId, string>) (suffix: string) (callable: IRCallable) : string list * string =
    let safeName = sanitizeCppName callable.Name
    let wrapperName =
        if suffix = "" then $"__wrap_{callable.Id}"
        else $"__wrap_{callable.Id}_{suffix}"
    let paramSig =
        callable.Params
        |> List.map (fun p ->
            match p.Type with
            | ArrayElem arr -> $"{(cppArrayTypeStr arr)} {p.Name}"
            | _ -> $"{(irTypeToCpp p.Type)} {p.Name}")
        |> String.concat ", "
    let regularArgs = callable.Params |> List.map (_.Name)
    let captureArgs = captureForwardArgs names callable.Captures
    let allArgs = (regularArgs @ captureArgs) |> String.concat ", "
    let code =
        [$$"""auto {{wrapperName}} = [&]({{paramSig}}) { return {{safeName}}({{allArgs}}); };"""]
    (code, wrapperName)

/// Whether a callee's return value is a pool the CALLER owns. `FreshPool` means
/// every return hands back storage this call allocated; `NotFresh` means it may
/// alias a parameter, a capture, or a view, so freeing it at the call site would
/// free storage the callee's caller still uses. Absent from the facts map reads
/// as `NotFresh` -- unproven means leaked, never freed.
///
/// Declared HERE, ahead of the loop emitters, rather than beside its classifier
/// (`computeFreshReturnFacts`, further down): stage S3's array-valued kernel
/// return frees the row the callee handed back, once per outer cell, and that
/// free is only sound for a `FreshPool` callee. The classifier still lives with
/// the rest of the deterministic-deallocation block; only the fact TYPE, its
/// cell and the lookup moved up.
type FreshReturn =
    | NotFresh
    | FreshPool

/// Per-module fresh-return facts, installed by genModule / genModuleSplit after
/// the callables table (freshReturnOf resolves through it). AsyncLocal for the
/// same per-parallel-test-task isolation as exprWarningsCell.
let internal freshReturnFactsStorage =
    System.Threading.AsyncLocal<Map<IRId, FreshReturn> ref>()

let freshReturnFactsCell () : Map<IRId, FreshReturn> ref =
    let v = freshReturnFactsStorage.Value
    if isNull (box v) then
        let fresh = ref Map.empty
        freshReturnFactsStorage.Value <- fresh
        fresh
    else v

/// This file's per-program cells, reset by installing fresh refs in the
/// current flow (CodeGenState.freshCell explains why a reset must never
/// mutate the ref it finds). Called at every program-assembly entry.
let resetExprSupportCells () : unit =
    freshCell cudaDeviceDialectStorage false
    freshCell copyInPlaceMutsStorage Map.empty
    freshCell groupedCaptureFactsStorage Map.empty
    freshCell extentsOnlyGroupBysStorage Set.empty
    freshCell groupedBindingsStorage Map.empty
    freshCell freshReturnFactsStorage Map.empty

/// The fresh-return fact for whatever callable an expression in callee position
/// resolves to (module function or synthetic). Unresolvable => NotFresh.
let freshReturnOf (calleeExpr: IRExpr) : FreshReturn =
    match resolveCallable calleeExpr with
    | Some c -> (freshReturnFactsCell ()).Value |> Map.tryFind c.Id |> Option.defaultValue NotFresh
    | None -> NotFresh

// ---------------------------------------------------------------------------
// Kernel body: expression-shaped, or call the lifted callable?
// (docs/plan-kernel-body-materialization.md sections 3 and 5, stage S2)
// ---------------------------------------------------------------------------
//
// The loop emitters that serve `<@> |> compute` render the kernel body as ONE
// C++ expression (`genKernelExprWithReynolds` -> `exprToCpp`). There is no
// statement scope there, so a body that MATERIALIZES an array -- a kernel-local
// `let` bound to a computed array -- has nowhere to put the allocation and the
// fill loop, and `exprToCppCore` refuses it with a sentinel.
//
// The lifted form of the SAME body already compiles correctly:
// `genFuncBodyScoped` is a full statement context (alloc, fill nest, scope-owned
// free) and the callable is emitted for every kernel regardless. So the fix is
// not new emission machinery -- it is ROUTING: when the body is not
// expression-shaped, call the lifted callable instead of inlining its text.
// `genObjectForApplication` already takes exactly this route for the sibling
// `IRApp(IRObjectFor ...)` shape.
//
// CONSERVATISM DIRECTION. A false "not expression-shaped" costs one non-inlined
// call per output cell. A false "expression-shaped" reintroduces a sentinel, i.e.
// a program that does not compile. So the predicate names only the node classes
// `exprToCppCore` refuses UNCONDITIONALLY -- which is also what makes this change
// unable to regress a passing test: every body it re-routes emits a
// `BLADE_CODEGEN_ERROR_` sentinel today, and a sentinel is already a failed
// compile. Inline forms (mask/sort/intersect/transpose/...) are deliberately NOT
// in the list: `renderLetExpr` materializes those into an IIFE prelude and they
// work inline today.

/// True iff `e` is a node `exprToCppCore` refuses outright in expression
/// position. Deliberately narrow -- see the conservatism note above.
let internal isNonExpressionNode (e: IRExpr) : bool =
    match e with
    | IRApplyCombinator _ | IRComposeApply _ -> true     // exprToCppCore: "unevaluated computation used as value"
    | IRMethodFor _ | IRObjectFor _ -> true              // exprToCppCore: "loop object used as value"
    | IRReduceCompute _ -> true                          // exprToCppCore: "reduce over a deferred computation ..."
    | IRCompute (IRApplyCombinator _) -> true            // genApplyCombinatorExpr: unconditional refusal
    | _ -> false

/// Does the kernel body render as a single C++ expression? False when a
/// materializing node appears anywhere in it (including as an `IRLet` RHS, which
/// is the shape stage S2's Lowering half now produces for a kernel-local array).
let rec kernelBodyIsExpressionShaped (body: IRExpr) : bool =
    if isNonExpressionNode body then false
    else childrenOf body |> List.forall kernelBodyIsExpressionShaped

/// True iff a RETURN expression branches (`if`/`match`) and at least one of the
/// leaves it can return is a materializing form. Follows only the return spine
/// -- branch arms and let-chain tails -- because that is the set of leaves the
/// return arm would have to find a destination for. Consumed by
/// `genFuncBodyScoped`'s return dispatch (stage S4) to refuse that shape with an
/// accurate message instead of letting each arm reach an unrelated sentinel.
let rec internal branchingReturnMaterializes (e: IRExpr) : bool =
    match e with
    | IRIf (_, t, f) -> branchingReturnMaterializes t || branchingReturnMaterializes f
    | IRMatch (_, cases) -> cases |> List.exists (fun c -> branchingReturnMaterializes c.Body)
    | IRLet (_, _, body) -> branchingReturnMaterializes body
    | leaf -> isNonExpressionNode leaf

/// Route a nest whose kernel body is not expression-shaped through a CALL to the
/// lifted callable: the body becomes `IRApp(IRVar(callable.Id, ...), params)`,
/// which `exprToCppCore`'s IRApp arm renders as `__lambda_N(<peeled args>,
/// <captures>)` -- capture arguments resolved through `captureForwardName` on the
/// SAME name map the emitter already builds, so a renamed block-local forwards
/// its EMITTED spelling (memory/block-local-capture-forwarding.md).
///
/// Applied at every CodeGen site that builds a `LoopNestCodeGen`, i.e. BEFORE
/// the flat-elementwise fast path, the nest, the fused tree and the device
/// emitters see it. That ordering is deliberate: `tryGenFlatElementwiseNest`'s
/// own gates (notably the OpenMP licence check at "OmpRequested && not
/// allParallel") still decide flat-vs-nest exactly as before -- this changes WHAT
/// the body renders as, never WHICH emitter renders it.
///
/// Abstains (leaving today's sentinel) for:
///   * a Reynolds kernel -- the permutation sum is rendered by substituting
///     param names, and while the call form survives that substitution, no
///     measured program combines Reynolds with a materializing body; keep the
///     loud refusal rather than ship an unexercised path;
///   * an unresolvable kernel, or one with no params.
///
/// An ARRAY-returning callable is NO LONGER an abstention (stage S3): the call
/// form is exactly how a whole row gets produced per outer cell, and
/// `genLoopNestStreamed`'s row-write arm copies the returned pool into the
/// output row. That pairing is deliberate -- the S0 typecheck guard
/// (`arrayValuedComputeBody`) and this abstention came out in the same change.
let routeKernelBodyThroughCall (info: ApplyInfo) (cg: LoopNestCodeGen) : LoopNestCodeGen =
    if cg.HasReynolds || cg.IsAntisymmetric then cg
    elif kernelBodyIsExpressionShaped cg.KernelExpr then cg
    else
        match resolveKernel info.Kernel with
        | Some rk when not (List.isEmpty rk.Callable.Params)
                       && not rk.Reynolds.HasReynolds ->
            let paramTypes = rk.Callable.Params |> List.map (_.Type)
            let funcTy = mkFuncArrow paramTypes rk.Callable.RetType
            let args = rk.Callable.Params |> List.map (fun p -> IRVar (p.VarId, p.Type))
            { cg with KernelExpr = IRApp (IRVar (rk.Callable.Id, funcTy), args, rk.Callable.RetType) }
        | _ -> cg

/// Evaluate a DepIdx inner-extent formula for a specific outer index value:
/// substitute the concrete integer `i` for the outer record's IRVar Id and
/// fold constants. None means something can't be reduced statically (free
/// variables, IRParam, non-arithmetic ops); used to produce the `_lens` table
/// for DepIdx literals, where None currently errors out (no runtime eval yet).
let rec evalDepIdxExtent (outerId: IRId) (i: int) (expr: IRExpr) : int option =
    match expr with
    | IRLit (IRLitInt n) -> Some (int n)
    | IRVar (vid, _) when vid = outerId -> Some i
    | IRBinOp (_, op, l, r, _) ->
        match evalDepIdxExtent outerId i l, evalDepIdxExtent outerId i r with
        | Some a, Some b ->
            match op with
            | IRAdd -> Some (a + b)
            | IRSub -> Some (a - b)
            | IRMul -> Some (a * b)
            | IRDiv when b <> 0 -> Some (a / b)
            | IRMod when b <> 0 -> Some (a % b)
            | _ -> None
        | _ -> None
    | IRUnaryOp (IRNeg, e) -> evalDepIdxExtent outerId i e |> Option.map (fun n -> -n)
    | _ -> None

/// The number of scalar cells an operand's backing pool is KNOWN to hold, as a
/// C++ expression, or `"0"` when the storage class doesn't statically settle it.
/// Consumed by the shim's `row_major_base` capacity bound: the contiguity probe
/// sees only `double**` (row STARTS, never LENGTHS), so it can't distinguish a
/// dense 2x2 (4 cells) from an n=2 packed-symmetric pool (3 cells) -- both
/// satisfy `rows[i] == base + i*ld`, and handing BLAS the packed pool's base
/// reads one element past it. The cell count is the only thing that separates
/// them. `"0"` (refuse) is deliberate for anything not provably dense rank-2:
/// unknown storage falls to staging, not a guessed capacity. Currently
/// unreachable in practice (only dense operands reach a linalg route; compact
/// ones are refused at typecheck, BL4004) -- guards a future surface widening.
let internal denseCellCountOfArray (arr: IRArrayType) (name: string) : string =
    if arr.IsVirtual then "0" else
    // Dense rank-2 <=> two stored slots, each a plain dense axis of rank 1.
    // `IxDense` is the compiler's one classification of "plain dense index";
    // a symmetry-like, compound, ragged or dependent slot lands in a sibling
    // arm and yields "0" here.
    let denseSlot (ix: IRIndexType) =
        (match ix with IxDense -> true | _ -> false) && ix.Rank <= 1
    match arr.IndexTypes with
    | [ i0; i1 ] when denseSlot i0 && denseSlot i1 ->
        $"({name}.extents[0] * {name}.extents[1])"
    | _ -> "0"

let internal denseCellCountExpr (ty: IRType) (name: string) : string =
    match ty with
    | ArrayElem arr -> denseCellCountOfArray arr name
    | _ -> "0"

/// The singular-matrix panic message, spelled ONCE. Four readers have to agree
/// on it byte for byte: `materializeSolveForm`'s native guard, the same form's
/// LAPACK `info` check, the interpreter's `Interp/ArrayOps.solveArray`, and any
/// corpus `// ABORT:` pin. Two of those four live in other files, so this
/// binding is the anchor a reviewer greps for, not a saving of characters.
///
/// Kept free of the operand's name and of the failing column index on purpose:
/// the LAPACK arm learns the column from `info` and the native arm from `k`,
/// but a message that differed between the arms would make the corpus pin
/// gate-dependent -- and a gate-dependent abort pin is a test that passes for
/// the wrong reason on exactly one machine.
let internal solveSingularMessage =
    "solve(A, b): the matrix is SINGULAR -- LU factorization found an exactly-zero pivot"

/// One dimension's extent, read off the OPERAND'S OWN index record. THE RULE
/// LIVES HERE; `literalOrRuntimeExtentOfArray` below is its string shape.
///
/// Two callers need the two different answers, which is why the rule is split
/// out rather than folded into the renderer: a loop bound wants TEXT with a
/// runtime fallback, while a `static constexpr` return-extents table has to
/// know STRUCTURALLY whether every axis is literal. Deciding the latter by
/// inspecting the rendered text would make an emitted table's storage class
/// depend on string formatting.
///
/// This is `genLoopBoundExpr`'s first arm (`IRLit (IRLitInt n) -> "%d"`),
/// factored so the loop NEST and the INTRINSIC/dispatch emitters cannot
/// disagree about a bound that is by construction the same number. The nest has
/// baked literals since Phase 4; the intrinsics did not, which is the whole of
/// the reported "runtime .extents[] read despite a literal Idx<n>" symptom at
/// those sites. The gemv and syrk dispatches each carried their own inline copy
/// of this match until they were routed through here.
///
/// SOUNDNESS. `IRMono.shapeMonomorphizeModule` writes a literal into `Extent` only
/// when every occurrence of that symbolic name was pinned to the SAME literal,
/// and `shapeRewriteType` confines the rewrite to the `Extent` field -- it never
/// touches a body's `extents(A)` read. So a literal in the record is a statement
/// about the runtime array, not a hope about it, and `<name>.extents[dim]` holds
/// that same value: the allocation's extents table is filled FROM this record.
///
/// RECORD POSITION IS NOT AXIS POSITION, and this is where that used to bite.
/// A packed multi-component record is ONE record covering SEVERAL dense axes
/// (`SymIdx<2, m>` is one record, two axes), so `List.item dim IndexTypes` is
/// the wrong record the moment any record ahead of `dim` has arity > 1. The
/// earlier rule sidestepped that by declining outright whenever the record at
/// the naive position had `Rank <> 1` -- safe, but it also declined the case it
/// was reading correctly, which is every axis of a compact operand (`decompact`
/// bounds itself by `A.extents[0]` over an `AntiIdx<r, n>` slot).
///
/// The mapping is a PREFIX-SUM WALK over record arities, the same walk
/// `IR.typeOf`'s decompact arm already does to find the record owning a given
/// dimension. It is exact rather than conservative because a record of arity r
/// contributes r dense axes that ALL carry that record's extent -- `IR.typeOf`
/// states this by construction, expanding a wreath slot into
/// `List.replicate axes { ... Extent = baseExtent ... }` -- and the emitted
/// extents TABLE is filled in that same dense-axis space (an n=3 antisymmetric
/// rank-2 operand emits `A_extents[0] = 3; A_extents[1] = 3;`). So the record's
/// `Extent` and `<name>.extents[dim]` are two spellings of one number for every
/// axis the record covers, which is exactly the licence this function needs.
///
/// Arity is read as `max 1 ix.Rank`, matching `IR.typeOf`'s walk and the
/// `sumBy (fun ix -> max 1 ix.Rank)` dense-rank count used by the array-form
/// emitters, so a 0/absent arity spans one axis here and everywhere else.
///
/// ONE DECLINE remains, to `.extents[]` (the pre-existing behaviour, never a
/// guess): the walk runs off the end of the record list, i.e. the operand has
/// fewer dense axes than `dim` -- nothing to read.
///
/// Non-dense storage classes are NOT special-cased here. A compound/sparse
/// operand has no `.extents` member at all and a `RaggedRow` carries `.len`, so
/// the runtime fallback is equally wrong for them; every caller therefore tests
/// its own storage class BEFORE reaching this (see `genReduceForm`'s
/// `isCompoundOperand`/`isRaggedRowOperand` pair for the canonical shape), and
/// adding a redundant guard here would only hide a caller that forgot to.
let internal literalExtentOfArray (arr: IRArrayType) (dim: int) : int64 option =
    // Walk records, accumulating the dense axes each one spans, until `dim`
    // falls inside one. Running off the end = fewer axes than `dim`.
    let rec ownerOf (axesSoFar: int) (remaining: IRIndexType list) : IRIndexType option =
        match remaining with
        | [] -> None
        | ix :: rest ->
            let span = max 1 ix.Rank
            if dim < axesSoFar + span then Some ix else ownerOf (axesSoFar + span) rest
    match ownerOf 0 arr.IndexTypes with
    | Some ix -> (match ix.Extent with IRLit (IRLitInt n) -> Some n | _ -> None)
    | None -> None

/// `literalExtentOfArray` as a C++ expression: the literal when the record
/// settles the axis, else the runtime `<name>.extents[dim]` read that every
/// intrinsic emitter used to hardcode. The payoff is not the removed load (any
/// compiler hoists an invariant one) but the TRIP COUNT -- see the note on the
/// `IRType` wrapper below.
let internal literalOrRuntimeExtentOfArray (arr: IRArrayType) (name: string) (dim: int) : string =
    match literalExtentOfArray arr dim with
    | Some n -> string n
    | None -> $"{name}.extents[{dim}]"

/// The same answer as `literalOrRuntimeExtentOfArray`, PAIRED with whether the
/// rendered text is a literal -- the `(value, isLiteral)` shape
/// `emitExtentsTable` consumes. The pairing has to come from the match that
/// chose the text (see emitExtentsTable's note), so it lives here rather than
/// at the extents sites, and `literalOrRuntimeExtentOfArray` above stays the
/// value-only spelling for loop bounds.
let internal extentDimOfArray (arr: IRArrayType) (name: string) (dim: int) : string * bool =
    match literalExtentOfArray arr dim with
    | Some n -> (string n, true)
    | None -> ($"{name}.extents[{dim}]", false)

/// THE shared companion-extents rule. Every emitter that materializes an
/// `Array<T,R>` needs a table for its shape; this decides which of the two
/// forms that table takes, and reports back whether the caller now OWNS it.
///
/// Default (any entry is a runtime read): a HEAP table. `Array<T,R>` stores
/// only a POINTER to its extents, so a stack `size_t[R]` would make the
/// wrapper non-returnable -- the pool outlives the frame but the extents
/// pointer dangles, and a caller reading `c.extents[d]` gets garbage. Heap
/// extents make the wrapper self-describing across a call boundary, at the
/// cost of a `delete[]` the scope has to remember (`Some name`).
///
/// WHEN EVERY ENTRY IS LITERAL none of that management is needed: a
/// `static constexpr const size_t[R]` table has STATIC storage duration, which
/// satisfies the same constraint strictly more safely -- it outlives every
/// wrapper naming it and every copy of that wrapper unconditionally, there is
/// nothing to free, and nothing to get wrong if a frame is torn down early.
/// It is the form the rectangular array-literal path already hands back across
/// a function return, chosen there for exactly this reason. `allocate<>` and
/// `deallocate<>` both take `const size_t extents[]` and `Array<T,R>::extents`
/// is a `const size_t*`, so the array-to-pointer decay leaves the stored
/// pointer identical in type and in every read (`NAME[d]` indexes an array the
/// same way it indexed a pointer). Being constexpr, the static also costs no
/// `__cxa_guard` even when the declaration sits inside a loop body.
/// `None` is returned as the owned name, which is what suppresses the free.
///
/// A MIXED table keeps the heap: a constexpr initializer cannot name a runtime
/// `.extents[]` read, and half-baking it would need two tables. RANK 0 keeps
/// the heap too -- `new size_t[0]` is legal where `const size_t t[0]` is not.
///
/// `dims` pairs each entry's RENDERED value with whether it is a literal. That
/// pairing is the caller's job precisely because the answer is structural: it
/// must fall out of the same match that chose the text (an `IRLit` arm vs an
/// `.extents[]` arm), never out of re-inspecting the rendered string.
///
/// DEFINED HERE, far above the allocation registry it used to sit beside,
/// because `materializeInlineForm`'s builders are in the `exprToCppCore` rec
/// group and every one of them needs this rule: their results are returned
/// out of function bodies, which is exactly the case a frame-local table
/// cannot survive.
let emitExtentsTable (ind: string) (extentsName: string) (rank: int)
                     (dims: (string * bool) list) : string list * string option =
    if rank > 0 && dims |> List.forall snd then
        ([ $"""{ind}static constexpr const size_t {extentsName}[{rank}] = {{ {(dims |> List.map fst |> String.concat ", ")} }};""" ],
         None)
    else
        (($"{ind}size_t* {extentsName} = new size_t[{rank}];")
         :: (dims |> List.mapi (fun d (e, _) -> $"{ind}{extentsName}[{d}] = {e};")),
         Some extentsName)

/// A `group_by` result's extents table (G1): `[ngroups, 0, offsets[0 ..
/// ngroups]]` -- slot 1 is ragged, and the grouping's offsets ride along, so
/// a read of group i's rows is checked against its own length (`extents[2 +
/// i + 1] - extents[2 + i]`, renderExtentExpr) wherever the grouped value
/// goes, a kernel it is captured into included.
let emitGroupedExtentsTable (ind: string) (extentsName: string) (gkName: string) : string list * string option =
    ([ $"{ind}size_t* {extentsName} = new size_t[3 + {gkName}__ngroups];"
       $"{ind}{extentsName}[0] = {gkName}__ngroups;"
       $"{ind}{extentsName}[1] = 0; /* inner extent is ragged */"
       $"{ind}for (size_t __g = 0; __g <= {gkName}__ngroups; __g++) {extentsName}[2 + __g] = {gkName}__offsets[__g];" ],
     Some extentsName)

/// The word a dispatch marker comment leads with, for a route resolved by
/// `LinAlgPatterns.resolveNodeRoute`. Names the BACKEND because that's the only
/// place the choice is observable (host cblas / device cuBLAS / Blade's own
/// loops all compute the same matrix to within rounding). `None` is unreachable
/// from call sites (only built on the routed arm) and answers the host word
/// rather than raising -- a comment is not the place to fail a compile.
let internal dispatchMarkerTag (resolved: (Blade.LinAlgPatterns.LinAlgBackend * string) option) : string =
    match resolved with
    | Some (Blade.LinAlgPatterns.CudaBlas, _) -> "cublas"
    | _ -> "linalg"

/// Is this binary operation commutative? (a op b) = (b op a)
let isCommutativeOp (op: IRBinOp) : bool =
    match op with
    | IRAdd | IRMul | IREq | IRNeq | IRAnd | IROr -> true
    | _ -> false

/// Is this binary operation associative? (a op b) op c = a op (b op c)
/// Only ops that are BOTH commutative and associative get flattened.
let isAssociativeOp (op: IRBinOp) : bool =
    match op with
    | IRAdd | IRMul | IRAnd | IROr -> true
    | _ -> false

// ---- Parallel-fold reorder licence (docs/plan-cpp-perf-exploitation.md section 2) --
//
// `reduce(xs, k)` is parallelized only when `k` carries `where ... omp` AND the
// reorder is licensed. A chunked fold reassociates and reorders, so the licence
// is exactly "commutative and associative":
//
//   * `comm(a, b)` declared on the kernel -- the user's word, already
//     cross-checked against body parity at typecheck (CommContradictsBody);
//     associativity is the part `omp` itself asserts (the plan's trust model,
//     the same escape hatch comm's PBottom case uses);
//   * a recognised BUILTIN body, which carries both outright and needs nothing
//     declared.
//
// TypeCheck refuses `omp` with neither (BL4016), so reaching codegen unlicensed
// means the front end and this file disagreed; the emitters then fall back to
// the serial loop with a visible marker rather than parallelizing on a licence
// nobody granted.
//
// The SAME predicate answers "may this serial chain become lanes / an `omp simd
// reduction`?" -- spent by DEFAULT since 2026-10-17 (`fpReassocEnabled`), and
// withheld only by the repro build (BLADE_FP_REPRO=1), which is a polarity, never
// the licence itself. An unlicensed user kernel stays serial in every build.

/// The builtin binary op a 2-parameter fold callable's body IS, when the body is
/// exactly `p0 <op> p1` (either argument order) and `op` is both commutative and
/// associative. None for anything else -- including a body that merely CONTAINS
/// such an op, which carries no such guarantee.
///
/// Paired with TypeCheck.isBuiltinFoldBodySurface / isBuiltinFoldBodyTyped,
/// which answer the same question at the surface and typed levels for the
/// BL4016 diagnostic. Deliberately narrow at all three sites so "recognised
/// builtin" means one thing.
let foldKernelBuiltinOp (callable: IRCallable) : IRBinOp option =
    match callable.Params, callable.Body with
    | [p0; p1], IRBinOp (_, op, l, r, _) when isCommutativeOp op && isAssociativeOp op ->
        // A param reference lowers as IRVar over the param's VarId; IRParam
        // (positional) is accepted too so a callable built by either convention
        // is recognised.
        let slotOf (e: IRExpr) =
            match e with
            | IRVar (id, _) ->
                if id = p0.VarId then Some 0 elif id = p1.VarId then Some 1 else None
            | IRParam (_, idx, _) ->
                if idx = p0.Index then Some 0 elif idx = p1.Index then Some 1 else None
            | _ -> None
        (match slotOf l, slotOf r with
         | Some a, Some b when a <> b -> Some op
         | _ -> None)
    | _ -> None

/// Gap G42: a projection chain (`t[1]`, `t[0][1]`, flat or structural steps)
/// rooted at a binding the C++ lane holds as a FLAT tuple (flatTupleIdsCell)
/// -> (root, the flat position of the selected part's first leaf, the selected
/// part's IR type). None when the root is not such a binding, or a step does
/// not fit the type (the ordinary render then reports it).
let flatTupleProjection (e: IRExpr) : (IRExpr * int * IRType) option =
    let rec peel (x: IRExpr) (steps: (int * bool) list) =
        match x with
        | IRTupleProj (inner, i, fl) -> peel inner ((i, fl) :: steps)
        | root -> (root, steps)
    match peel e [] with
    | (IRVar (id, rootTy) as root), steps when not steps.IsEmpty && (flatTupleIdsCell ()).Value.Contains id ->
        let step (state: (IRType * int) option) (i: int, isFlat: bool) =
            match state with
            | None -> None
            | Some (ty, off) ->
                match stripUnits ty with
                | IRTTuple ts when not isFlat && i >= 0 && i < ts.Length ->
                    let before = ts |> List.take i |> List.sumBy (fun t -> (IR.flattenTupleLeaves t).Length)
                    Some (ts.[i], off + before)
                | IRTTuple _ as tt when isFlat ->
                    let leaves = IR.flattenTupleLeaves tt
                    if i >= 0 && i < leaves.Length then Some (leaves.[i], off + i) else None
                | _ -> None
        steps |> List.fold step (Some (rootTy, 0))
        |> Option.map (fun (ty, off) -> (root, off, ty))
    | _ -> None

/// Does evaluating this callable WRITE something another evaluation could
/// observe -- transitively, through every callable its body names (calls,
/// kernels of inner folds and maps, lifted lambdas)? An assignment is LOCAL
/// (not a write) when its target's root is a `let` bound inside the walked
/// body itself -- a local accumulator, a local scratch array, a let-rec
/// buffer -- the typechecker's own notion of "a binding outside the body"
/// (formalism §5.2), except that a local bound directly to another NAME
/// (`let t = G`) may alias it and counts. Every other assignment counts, and
/// so does a call through a head that does not resolve to a callable (a
/// function-typed value: may do anything). Conservative: a "may".
///
/// Read by `foldReorderLicensed`: formalism §5.2 accepts a serial body's
/// write to a captured `let mut` as an ORDERED side effect, and a `comm`
/// clause says the kernel's VALUE is symmetric -- not that its applications
/// may be reordered, chunked across threads, or interleaved with the
/// element evaluations they used to follow (gap G41).
/// The walk itself is Blade.Optimize.exprMayWriteWith, shared with
/// Lowering's join-leg refusal (gap G43); here it resolves callees through
/// the codegen callable table.
let internal programLetValuesStorage =
    System.Threading.AsyncLocal<Map<IRId, IRExpr> ref>()

/// Every let-bound value of the module being emitted (module bindings and the
/// lets inside every function body; ids are unique per program), installed by
/// each emitter's program entry (CodeGen.genModule*, EmitLlvm) so the write
/// analysis can follow a NAMED deferred computation that a kernel or a fold
/// operand reads to the kernels that run when it is read (gaps G41 / G43).
/// Stored values (`|> compute`, calls) are never followed: only deferred forms.
let programLetValuesCell () : Map<IRId, IRExpr> ref =
    let v = programLetValuesStorage.Value
    if isNull (box v) then
        let fresh = ref Map.empty
        programLetValuesStorage.Value <- fresh
        fresh
    else v

let computeProgramLetValues (modul: IRModule) : Map<IRId, IRExpr> =
    let acc = System.Collections.Generic.Dictionary<IRId, IRExpr>()
    let note (e: IRExpr) = iterIRExpr (fun n -> match n with IRLet (id, v, _) -> acc.[id] <- v | _ -> ()) e
    for b in modul.Bindings do
        acc.[b.Id] <- b.Value
        note b.Value
    for f in modul.Functions do note f.Body
    acc |> Seq.map (fun kv -> (kv.Key, kv.Value)) |> Map.ofSeq

let internal programCallbackArgsStorage =
    System.Threading.AsyncLocal<Map<IRId, IRExpr list> ref>()

/// Every argument a call site of the module passes for a FUNCTION-VALUED
/// parameter, keyed by that parameter's id (gap G41, Codex finding 20): a
/// kernel that calls a callback is judged through what the callback can be.
/// Installed beside programLetValuesCell (after the callables table).
let programCallbackArgsCell () : Map<IRId, IRExpr list> ref =
    let v = programCallbackArgsStorage.Value
    if isNull (box v) then
        let fresh = ref Map.empty
        programCallbackArgsStorage.Value <- fresh
        fresh
    else v

let internal programMutableArrayLetsStorage =
    System.Threading.AsyncLocal<Set<IRId> ref>()

/// The module's ASSIGNABLE array lets (IRModule.MutableArrayLets: every block
/// array `let`, since a plain block let is assignable in its scope, and every
/// `let mut`), installed beside programLetValuesCell by each emitter's program
/// entry. A dense one initialized from an existing array DEEP-COPIES it in
/// every lane (gap G46): genVarAliasBinding for a binding-level let, the
/// expression emitter's kernel-body copy (CodeGenExpr.kernelLetCopy) for a let
/// inside an IIFE kernel body -- the interpreter's IRLet arm copies both.
let programMutableArrayLetsCell () : Set<IRId> ref =
    let v = programMutableArrayLetsStorage.Value
    if isNull (box v) then
        let fresh = ref Set.empty
        programMutableArrayLetsStorage.Value <- fresh
        fresh
    else v

/// The dense, plain-index array shapes whose assignable let deep-copies its
/// initializer (the shapes genVarAliasBinding / the interpreter copy; a
/// compound / ragged / dependent-index store keeps the historical alias).
let isDenseCopyLetType (t: IRType) : bool =
    match t with
    | ArrayElem at -> at.IndexTypes |> List.forall (fun ix -> ix.IxKind = IxKPlain && ix.Symmetry = SymNone)
    | _ -> false

/// `let x = Z` (Z an existing dense array) whose x is assignable: x holds a
/// private copy, so a write through x writes nothing Z's other names see.
/// The same rule Lowering's write facts use (its `copied`).
let isCopiedArrayLet (id: IRId) : bool =
    Set.contains id (programMutableArrayLetsCell ()).Value
    && (match Map.tryFind id (programLetValuesCell ()).Value with
        | Some (IRVar (_, ty)) -> isDenseCopyLetType (stripUnits ty)
        | _ -> false)

let computeProgramCallbackArgs (modul: IRModule) : Map<IRId, IRExpr list> =
    // the SAME collector Lowering uses (Optimize.callbackTable)
    Blade.Optimize.callbackTable resolveCallable modul.Functions (modul.Bindings |> List.map (fun b -> (b.Id, b.Value)))

let private codegenWriteFacts (deferred: Map<IRId, IRExpr>) : Blade.Optimize.WriteFacts =
    { Resolve = resolveCallable
      OuterLet = (fun id ->
          match Map.tryFind id deferred with
          | Some v -> Some v
          | None -> Map.tryFind id (programLetValuesCell ()).Value)
      // an assignable dense `let x = Z` deep-copies Z in every lane (gap G46:
      // the C++ kernel-body let copies too now), so a write through x is local
      CopiedLet = isCopiedArrayLet
      UnknownIsWrite = true
      // a callback joins EVERY argument its call sites pass (Codex finding 22)
      Callbacks = (fun id ->
          match Map.tryFind id (programCallbackArgsCell ()).Value with
          | Some args when not args.IsEmpty -> Some args
          | _ -> None) }
let callableMayWrite (callable: IRCallable) : bool =
    Blade.Optimize.callableMayWriteWith (codegenWriteFacts Map.empty) callable

/// `callableMayWrite`'s rule over a bare kernel BODY (a loop nest's
/// `KernelExpr`), every callable it names judged transitively; `deferred`
/// (ctx.DeferredComputations) lets it follow a named unforced computation the
/// body reads to the kernels that run when it is read.
let exprMayWriteIn (deferred: Map<IRId, IRExpr>) (body: IRExpr) : bool =
    Blade.Optimize.exprMayWriteWith (codegenWriteFacts deferred) body

let exprMayWrite (body: IRExpr) : bool = exprMayWriteIn Map.empty body

/// Does this fold kernel demand REPRODUCIBLE evaluation? True when the
/// callable itself is a `where repro` function, or when it is the
/// eta-expanded wrapper around one (`lambda(a, b) -> f(a, b)` -- the shape a
/// named function takes in kernel position; the wrapper carries no clause of
/// its own, so the demand is read off the callee). A repro demand VETOES the
/// reorder licence below even when `comm` grants it: comm says reordering is
/// mathematically sound, repro says the operation sequence is the contract.
let foldKernelReproVetoed (callable: IRCallable) : bool =
    callable.IsRepro
    || (match callable.Body with
        | IRApp ((IRVar _) as f, _, _) ->
            (match resolveCallable f with
             | Some callee -> callee.IsRepro
             | None -> false)
        | _ -> false)

/// May a fold through `callable` be reordered/reassociated across threads?
/// Answers only the LICENCE question -- whether omp was requested is separate
/// (callable.IsOmpParallel), so the two can be reported independently.
/// `where repro` vetoes unconditionally: this one predicate gates the OpenMP
/// fold paths, every K-lane / `omp simd reduction` fold form, and the LLVM
/// lane's fast-math flags, so the veto lands on all of them at once. (The
/// BUILD-wide twin, BLADE_FP_REPRO=1, is `fpReassocEnabled` -- checked beside
/// this predicate at each site, because the predicate is also read as a FACT
/// about the kernel, e.g. by the Path B chunk plan, whose route selection must
/// not depend on a build knob.)
///
/// A kernel that WRITES (`callableMayWrite`) is never licensed, whatever its
/// clauses: its applications are ordered side effects (formalism §5.2), so
/// every reordered form -- a Path B team reached through the MAP's
/// `omp(i: 1)`, K lanes that evaluate eight elements before combining them,
/// the LLVM fast-math flags -- would change what the program does, not just
/// its rounding (gap G41). The front end's BL4005 ban covers only a PARALLEL
/// body, and a `comm` fold kernel is not one.
let foldReorderLicensed (callable: IRCallable) : bool =
    not (foldKernelReproVetoed callable)
    && not (callableMayWrite callable)
    && (callable.IsCommutative
        || not (List.isEmpty callable.CommGroups)
        || (foldKernelBuiltinOp callable).IsSome)

/// The `blade plan` fold-form text of a fold `foldReorderLicensed` refuses,
/// naming the write when that is the reason.
let unlicensedFoldForm (callable: IRCallable) : string =
    if callableMayWrite callable then
        "serial (the fold kernel writes a captured binding: its applications are ordered side effects; no licence -- comm, omp on the fold or the map -- reorders or threads them)"
    else "serial (no reorder licence)"

/// An operand's extent along `dim`, as a C++ expression: the LITERAL when the
/// operand's own index record carries one, else the runtime `.extents[dim]`
/// read that every intrinsic emitter used to hardcode.
///
/// This is the rule the gemv/syrk dispatches apply to their `n`, lifted so the
/// intrinsic/IIFE emitters can share it instead of each restating
/// `.extents[0]`. Baking is sound because Phase 4
/// (`IRMono.shapeMonomorphizeModule`) writes a literal into `IRIndexTypeG.Extent`
/// only when EVERY occurrence of the symbolic name was pinned to the SAME
/// literal, and `shapeRewriteType` confines the rewrite to `Extent` -- it never
/// touches a body's `extents(A)` read. Deliberately matches `IRLit (IRLitInt
/// n)` ONLY, not the broader literal-arithmetic evaluation `renderExtentExpr`
/// does: this is a loop bound in a hot emitter, and the narrow rule is the one
/// with an existing precedent.
///
/// The payoff is not the removed load (any compiler hoists an invariant one)
/// but the TRIP COUNT: a literal bound lets GCC unroll and vectorize a short
/// fiber sweep that an opaque `.extents[0]` leaves as a counted loop.
let literalOrRuntimeExtent (ty: IRType) (name: string) (dim: int) : string =
    // Delegates to the IRArrayType core (defined earlier, next to the gram/
    // matmul emitters' uses) so the dense-axis -> record mapping is done
    // identically at every site; parallel implementations of this rule have
    // twice existed and both times disagreed on exactly that mapping.
    match ty with
    | ArrayElem at -> literalOrRuntimeExtentOfArray at name dim
    | _ -> $"{name}.extents[{dim}]"

/// The deterministic K-lane accumulation body, as unindented C++ statements.
/// SHARED by every `fpReassocEnabled ()` site so the emitted shape -- and
/// therefore the numeric answer -- cannot drift between them, and so it stays
/// the same shape Path B's chunked fold already uses (Round C, measured).
///
/// Emits, for the caller's lane count `K` (`foldLaneCount`, or
/// `laneCountForStreams` where the body reads several operand streams) and the
/// half-open range `[lo, hi)`:
///
///   T L0 = elem(lo + 0); ... T L7 = elem(lo + 7);   // seed: no identity needed
///   size_t i = lo + 8;
///   for (; i + 8 <= hi; i += 8) {                   // 8 INDEPENDENT chains
///       combine(L0, elem(i + 0)); ... combine(L7, elem(i + 7));
///   }
///   if (i < hi) { combine(L0, elem(i)); i++; }      // tail: at most K-1 left,
///   ... (through L6) ...                            // lane K-1 can never get one
///   combine(L0, L1); ... combine(L0, L7);           // fixed ASCENDING combine
///
/// and returns that list together with the name of the lane holding the result
/// (`L0`). No pragma of any kind: this is pure instruction-level parallelism,
/// so it is safe to drop inside an already-parallel loop nest (a `#pragma omp
/// parallel for` there would nest teams) and the answer does not depend on any
/// team size. Callers own the `hi - lo < K` short fallback and the disposal of
/// the result lane.
///
/// SEPARATE NAMED LOCALS, not `T lane[K]`: an array kept live by index
/// arithmetic can defeat scalar replacement, leaving every lane update a
/// load-modify-store to the stack -- reintroducing exactly the latency the
/// lanes exist to remove. The fully-unrolled K-1 tail is the price.
///
/// `combine acc rhs` renders one complete fold statement (semicolon included),
/// which is what lets `+=` (prodsum) and `acc = W(acc, x)` (reduce) share this.
///
/// `k` is passed IN rather than read from `foldLaneCount` here because the
/// budget rule (`laneCountForStreams`) makes it a function of the body's
/// operand-stream count; every caller must therefore state the K its emitted
/// order is a function of. Callers whose body reads one stream pass
/// `foldLaneCount` and emit exactly what they emitted before the parameter
/// existed.
let internal fpReassocLaneStmts
        (k: int)
        (elemStr: string)
        (lanePrefix: string)
        (idxName: string)
        (loExpr: string)
        (hiExpr: string)
        (elemAt: string -> string)
        (combine: string -> string -> string)
        : string list * string =
    let lane (l: int) = $"{lanePrefix}{l}"
    // Lane l's seed index. `lo` is the literal 0 at the intrinsic sites, and
    // `0 + 3` would be noise there; everywhere else it is a hoisted `const`.
    let atLo (l: int) = if loExpr = "0" then string l else $"{loExpr} + {l}"
    let stmts =
        [ for l in 0 .. k - 1 -> $"{elemStr} {(lane l)} = {(elemAt (atLo l))};" ]
        @ [ $"size_t {idxName} = {(atLo k)};"
            $$"""for (; {{idxName}} + {{k}} <= {{hiExpr}}; {{idxName}} += {{k}}) {""" ]
        @ [ for l in 0 .. k - 1 ->
              "    " + combine (lane l) (elemAt ($"{idxName} + {l}")) ]
        @ [ "}" ]
        @ [ for l in 0 .. k - 2 ->
              $$"""if ({{idxName}} < {{hiExpr}}) { {{(combine (lane l) (elemAt idxName))}} {{idxName}}++; }""" ]
        @ [ for l in 1 .. k - 1 -> combine (lane 0) (lane l) ]
    (stmts, lane 0)

/// The C++ `reduction(<op>:acc)` operator for a builtin fold body, when OpenMP
/// knows an identity for it. `+` and `*` only: those are the two whose private
/// initializer (0 / 1) is unambiguous for every element type Blade folds, and
/// they are the ones the plan's Path A commits to. `&&`/`||` are licensed by
/// `isCommutativeOp`/`isAssociativeOp` and ARE valid OpenMP reduction
/// identifiers, but no Blade fold reaches these sites with a `bool` element
/// type today, so they are left out rather than shipped unmeasured; everything
/// else licensed goes down the manual chunked path (Path B) or the K-lane form,
/// neither of which needs an identity at all.
///
/// DEFINED HERE, far above Path A's chunked-fold reader, because the
/// BLADE_FP_REASSOC simd form needs it from `simdReducibleElem`'s neighbourhood
/// onwards -- `fpReassocSimdOp` sits with the rest of the knob's machinery, and
/// F# is order-dependent. Same reason `foldLaneCount` and the licence
/// predicates live up here.
let ompReductionOperator (op: IRBinOp) : string option =
    match op with
    | IRAdd -> Some "+"
    | IRMul -> Some "*"
    | _ -> None

/// Is `elemStr` a C++ type OpenMP's builtin `+`/`*` reduction identifiers are
/// defined for, and that a SIMD lane can hold?
///
/// The list is closed on purpose. `reduction(+:x)` on a class type is not a
/// portable construct (whether the builtin identifiers apply to a type with an
/// `operator+` is compiler-dependent, and `declare reduction` would be the
/// portable spelling), and `std::complex<double>` and `std::string` both reach
/// `elemTypeToCpp`. A fold over either keeps the K-lane form, which needs no
/// identity and no vector lane -- it is plain instruction-level parallelism
/// over whatever `combine` the caller passes.
let internal simdReducibleElem (elemStr: string) : bool =
    match elemStr with
    | "double" | "float" | "int32_t" | "int64_t" -> true
    | _ -> false

/// The `omp simd reduction` accumulation body, as unindented C++ statements:
/// the SECOND emission form BLADE_FP_REASSOC can spend its licence on, and a
/// sibling of `fpReassocLaneStmts` rather than a replacement for it.
///
/// Emits, for the half-open range `[lo, hi)` and an accumulator the caller has
/// already DECLARED AND SEEDED:
///
///   BLADE_OMP_SIMD_REDUCTION(+:acc)
///   for (size_t i = lo; i < hi; i++) {
///       acc = acc + elem(i);
///   }
///
/// WHY THIS CAN BEAT THE LANES. Both forms buy the same thing -- independent
/// partial sums that break the serial dependence chain through the fold -- but
/// they ask for it at different levels. The lane form asks for it in SCALARS and
/// relies on the C++ compiler's SLP pass to pack K named locals into vectors;
/// where that pass packs them through a shuffle network the packing costs as
/// much as the chain it removed (measured: the dot shape, where 8 named lanes
/// land at parity with the serial chain through a vunpck/vpermpd permute
/// storm). The simd form asks for it in VECTORS directly: the vectorizer owns
/// the accumulator, so the partials are born in lanes and never marshalled.
///
/// WHAT IT COSTS. The lane count -- and therefore the summation order -- stops
/// being a property of the emitted text and becomes a property of the compiler
/// and its flags (vector width, unroll factor, whether it vectorized at all).
/// That is a trade the knob is allowed to make and the lane form was not: see
/// the contract note at `fpReassocEnabled`, which the policy change to
/// "optimized Release builds are non-repro" is what licenses.
///
/// NO SHORT FALLBACK, no seed lanes, no tail peel, no fixed combine order: the
/// loop IS the serial loop, and every reassociation is the pragma's. That is
/// why this emitter is four lines and its sibling is twenty -- and why a `hi -
/// lo` below the vector width is not a special case here (the vectorizer emits
/// its own scalar remainder).
///
/// `accName` must name a plain local: OpenMP reduction list items may not be
/// struct members or dereferences. Every caller passes either its own IIFE
/// accumulator or `bindingCppName`, both of which are locals.
///
/// `prelude` is emitted at the top of the loop body, before the accumulate.
/// Empty for the `prodsum` IIFE, whose element is just a subscript product; the
/// reduce-over-computation site puts the level's ELEMENT PEELS there, so that
/// its body expression is written straight into the vectorized loop rather than
/// behind a lambda the vectorizer would have to see through. (The lane form has
/// to use a lambda -- it evaluates the body at K different indices and would
/// otherwise re-render it K times -- which is exactly the asymmetry that makes
/// one shared `elemAt` insufficient here.)
let internal fpReassocSimdStmts
        (opStr: string)
        (accName: string)
        (idxName: string)
        (loExpr: string)
        (hiExpr: string)
        (prelude: string list)
        (elemAt: string -> string)
        : string list =
    [ $"BLADE_OMP_SIMD_REDUCTION({opStr}:{accName})"
      $$"""for (size_t {{idxName}} = {{loExpr}}; {{idxName}} < {{hiExpr}}; {{idxName}}++) {""" ]
    @ (prelude |> List.map (fun s -> "    " + s))
    @ [ $"    {accName} = {accName} {opStr} {(elemAt idxName)};"
        "}" ]

/// The reduction operator the simd form would use for a fold through
/// `callable` over `elemStr` elements -- `None` when the simd form is not
/// available and the caller must keep the K-lane form.
///
/// TWO conditions, and they are different in kind. The ELEMENT TYPE condition
/// is portability (see `simdReducibleElem`). The OP condition is that OpenMP's
/// reduction clause names an OPERATOR, not a function: a `comm`-declared user
/// kernel is licensed to be reassociated but its combine is a call
/// `w(acc, x)`, which is not a reduction-statement form -- there is no identity
/// to seed private copies with and no operator to name in the clause. Such a
/// fold keeps the lane form, which needs neither. So the split is:
///
///   builtin `+`/`*` body  -> simd form (this returns Some)
///   comm-declared kernel  -> K-lane form (this returns None)
///
/// and `foldReorderLicensed` -- unchanged -- still gates whether either is
/// reached at all.
let internal fpReassocSimdOp (callable: IRCallable) (elemStr: string) : string option =
    if not (simdReducibleElem elemStr) then None
    else foldKernelBuiltinOp callable |> Option.bind ompReductionOperator

/// Is a domain fold's MAP kernel free of side effects, so that its cells may
/// be evaluated in another order (the diagonal walk) or on a thread team? The
/// fold's licence covers only the COMBINE; nothing has vetted the map body the
/// way BL4005 vets an omp body: a map kernel may assign a captured `let mut`
/// or print in serial code, and then the cell order (or a team) is observable.
/// Conservative: no assignment anywhere in the body, and every callee either a
/// function whose effect summary rules out mutation and output or a lambda
/// whose body passes the same test.
let domainMapKernelPure (body: IRExpr) : bool =
    let visited = System.Collections.Generic.HashSet<IRId>()
    let rec ok (e: IRExpr) : bool =
        match e with
        | IRAssign _ -> false
        | IRApp (f, _, _) ->
            let calleeOk =
                match f with
                | IRVar _ ->
                    (match resolveCallable f with
                     | Some cl when not cl.Effects.Unknown -> not cl.Effects.Mutates && not cl.Effects.EmitsOutput
                     | Some cl -> (not (visited.Add cl.Id)) || ok cl.Body
                     | None -> false)
                | _ -> ok f
            calleeOk && (childrenOf e |> List.forall ok)
        | _ -> childrenOf e |> List.forall ok
    ok body

/// The order a FOLD over a closed-form domain walks its cells.
type DomainFoldOrder =
    /// k = i - j outermost, a contiguous run of i inside (Types.domainDiagonal).
    | DomainDiagonalOrder of Blade.Types.DomainDiagonal
    /// The plan's lexicographic affine nest, and why the diagonal was declined.
    | DomainLexOrder of reason: string

/// THE one decision, shared by `blade plan`'s `[domain-enum v1]` line, the C++
/// domain fold arm (CodeGenBinding) and the LLVM lane's domain folds, so the
/// lanes walk the same order and the plan says which and why (docs/formalism.md,
/// "Diagonal order of a licensed band fold"). The diagonal is taken only under
/// the REORDER LICENCE on a builtin `+`/`*` fold -- a `BLADE_FP_REPRO=1` build,
/// a `where repro` fold kernel and any other kernel keep the lex order and its
/// bits -- over a band (two fields, a difference bounded on both sides with
/// unit slope) whose map kernel subscripts no matrix (a diagonal strides a
/// matrix by a row). `mapKernelBody` None = unknown, which keeps the lex order.
let domainFoldOrder (foldCallable: IRCallable option) (mapKernelBody: IRExpr option) (plan: DomainPlan) : DomainFoldOrder =
    match Blade.Types.domainDiagonal plan with
    | None -> DomainLexOrder "not a band: no two-field conjunct bounds the difference of the fields from both sides with unit slope"
    | Some dg ->
    match foldCallable with
    | None -> DomainLexOrder "the fold kernel does not resolve to a callable"
    | Some cl when (foldKernelBuiltinOp cl).IsNone ->
        DomainLexOrder "the fold kernel is not the builtin + or *: only a builtin reduction may walk the band by diagonal"
    | Some cl when foldKernelReproVetoed cl -> DomainLexOrder "the fold kernel is `where repro`: the lexicographic order is the contract"
    | Some _ when not (Blade.CodeGenState.fpReassocEnabled ()) ->
        DomainLexOrder "BLADE_FP_REPRO=1: a repro fold keeps the lexicographic order"
    | Some _ when not (Blade.CodeGenState.domainDiagonalEnabled ()) -> DomainLexOrder "BLADE_DOMAIN_DIAGONAL=0"
    | Some _ when (match mapKernelBody with Some b -> Blade.IR.kernelSubscriptsRank2 b | None -> true) ->
        DomainLexOrder "the map kernel subscripts a matrix, which a diagonal walk would stride by a row"
    | Some _ when (match mapKernelBody with Some b -> not (domainMapKernelPure b) | None -> true) ->
        DomainLexOrder "the map kernel may assign a capture or emit output: its cells' side effects keep the lexicographic order"
    | Some _ -> DomainDiagonalOrder dg

/// `blade plan`'s rendering of a domain fold's order.
let domainFoldOrderText (plan: DomainPlan) (order: DomainFoldOrder) : string =
    match order, plan.Levels with
    | DomainDiagonalOrder dg, [ l0; l1 ] ->
        $"order: diagonal (k = {l0.Field} - {l1.Field} outer, k in [{dg.KLo}, {dg.KHi}]; {l0.Field} inner and contiguous) -- a licensed fold over a difference-bounded band: same cells, same per-cell arithmetic, and a term depending only on {l0.Field} - {l1.Field} is invariant in the inner run"
    | DomainDiagonalOrder _, _ -> "order: diagonal"
    | DomainLexOrder why, _ -> $"order: lexicographic ({why})"


/// Is the packed native matmul kernel (blade_packed_gemm.hpp's dgemm_nn)
/// worth it for an M x K times K x N product? THE one statement of the rule:
/// the matmul emitter decides at compile time on the literal extents, and the
/// header carries no runtime copy to drift from it. Crossovers measured
/// against the i-t-j loop: M below ~18 or K below ~16 wastes most of a 6 x 8
/// tile or pays a C load/store per few MACs, and under ~32K MACs packing does
/// not amortize. Small N is fine (the loop is at its worst there).
let packedGemmWorth (m: int64) (k: int64) (n: int64) : bool =
    m >= 18L && k >= 16L && m * n * k >= 32768L


/// Is the packed-triangle SYRK tile (blade_packed_gemm.hpp's dsyrk_tri over
/// blade_tiles.h's 6 x 8 core) worth it for a p x p same-array row contraction
/// over k samples? THE one statement of the rule, decided at compile time on
/// the literal extents (the header carries no copy). Measured against the
/// per-cell dot it replaces (default build, Zen 3, single thread, scratch
/// sweep 2026-10-18): the tile wins from p = 16 at every k >= 4 (24 x 8 1.3x,
/// 61 x 31 2.5x, 61 x 257 2.7x, 97 x 513 3.1x, 1601 x 1001 5.9x) and loses
/// below (13 x 31 0.78x, 9 x 1001 0.56x) -- a 6 x 8 tile is mostly padding on a
/// 13-row triangle. The serial call makes NO OpenMP runtime call, which is
/// what moved the crossover down from the plan's first guess (an `if(false)`
/// parallel region cost ~17 us per K block).
let packedTriWorth (p: int64) (k: int64) : bool =
    p >= 16L && k >= 4L

/// The tile's sample-block cap, passed to dsyrk_tri as its `kcMax` so the
/// plan line and the header cannot disagree: k samples run in
/// nkb = ceil(k / cap) BALANCED blocks of ceil(k / nkb) (k = 1001: 3 x 334).
/// 336 keeps the 6 x KC A micro-panel (16 KB) resident in Zen 3's 32 KB L1d
/// beside the streaming B lines; 256 / 336 / 400 measured within 2% at
/// 1601 x 1001, 192 ~5% slower (more output reload passes).
let syrkTileKcMax = 336L

/// The balanced sample block the header derives from `syrkTileKcMax` (stated
/// here only for the `blade plan` line).
let syrkTileKc (k: int64) : int64 =
    if k <= 0L then 0L
    else
        let nkb = (k + syrkTileKcMax - 1L) / syrkTileKcMax
        (k + nkb - 1L) / nkb

/// BLADE_TILE_TUNE: the packed-tile drivers' SPEED-ONLY parameters, for A/B
/// sweeps -- `key=value` pairs separated by commas or spaces, unknown keys
/// ignored, a malformed value = the default. No bit depends on any of them
/// (every cell is one owner's chain in one order whatever the visiting order
/// or block size), so they need no licence and apply under BLADE_FP_REPRO=1.
/// Read per call; they change the EMISSION (the driver's arguments), so the
/// exe cache keys on them for free.
///   syrk_g   row panels per GROUP in dsyrk_tri / blade_dsyrk_tri_f64 (1..8):
///            G A micro-panels are packed together and each column panel is
///            read once from L3 and reused G times from L1 (default 4;
///            1 = the previous order, every column panel from L3 per row panel)
///   anti_kc  sample-block cap INSIDE an antisym tile visit (P and Q partials
///            spilled and reloaded exactly between balanced blocks; 0 = the
///            whole chain in one block, the default)
///   anti_g   row panels per GROUP in dantisym_tri / blade_dantisym_tri_f64:
///            unset = per regime (`antisymRowGroup`: 1, the previous order,
///            for a serial tile; the cache budget for a TEAM-ELIGIBLE one --
///            licence, thread emission, static work cap >= 2), 0 = the
///            compile-time cache budget, 1..8 = forced
///   tri_edge the syrk / simplex EDGE tiles' kloop (blade_tiles.h
///            blade_tri_tile_edge): 1 = only the accumulators with a real lane
///            (the default), 0 = the full 6 x 8 kloop
///   epi_blk  the cell epilogue's producer block between lane-state feeds
///            (`cellEpiBlock`: default 256, a multiple of the lane block)
///   epi_blk_r  the cell-chain mini-block (`cellEpiBlockR`: default 0 = one
///            fused loop)
///   sel_cap  the compaction's single-pass selectivity cap in KiB (read by
///            the optimizer, Optimize.compactionSelCapBytes: default 262144
///            = 256 MiB of n + 1 private cells across the compound's buffers)
/// The parser itself is Blade.Toolchain.tileTune (the optimizer reads it too).
let tileTune (key: string) (dflt: int64) : int64 = Blade.Toolchain.tileTune key dflt

/// dsyrk_tri's row panels per group (BLADE_TILE_TUNE syrk_g), clamped to the
/// drivers' TRI_G_MAX = 8.
let syrkRowGroup () : int64 = max 1L (min 8L (tileTune "syrk_g" 4L))

/// The antisym tile's sample-block cap (BLADE_TILE_TUNE anti_kc); 0 = whole.
let antisymKcMax () : int64 = max 0L (tileTune "anti_kc" 0L)

/// The L2 budget the antisym row-panel groups are sized against: half of Zen
/// 3's 512 KiB per-core L2, the rest left to the output lines, the P / Q
/// spills, the packing stream and the other data the visit touches.
let antisymRowGroupBudgetBytes = 256L * 1024L

/// The antisym driver's STATIC work cap on its team (blade_packed_gemm.hpp
/// `dantisym_tri`: T = max(1, min(omp_get_max_threads(), (p * p * n) >> 20)),
/// a team only when T > 1 and not inside an enclosing parallel region),
/// computed here without overflow. A cap below 2 means the driver runs
/// serially whatever the thread count.
let antisymTeamWorkCap (p: int64) (n: int64) : bigint =
    (bigint (max 0L p) * bigint (max 0L p) * bigint (max 0L n)) / bigint (1L <<< 20)

/// The plan phrases of anti_g's per-regime default (`antisymRowGroup`).
/// Why a tile is in the SERIAL regime, by cause.
let antisymNoTeamRequested =
    "no thread team is requested for this tile (no omp licence on the kernel, thread emission off, or the LLVM lane)"
let antisymNoTeamByWorkCap (cap: bigint) =
    $"the driver's static work cap (p * p * n) >> 20 = {cap} admits no team of two, so the tile runs serially at any thread count"
let antisymSerialDefaultNote (why: string) =
    $"row groups of 1 (the serial-tile default: {why}; G = 1 measured faster than the cache-budget groups serially, round 3; BLADE_TILE_TUNE anti_g=0 selects the cache-budget groups)"
/// The suffix of the TEAM-ELIGIBLE regime's cache-budget phrase.
let antisymTeamEligibleNote (cap: bigint) =
    $"; the team-eligible-tile default: the omp licence, thread emission and the driver's static work cap (p * p * n) >> 20 = {cap} >= 2 make this tile ELIGIBLE for the driver's thread team -- the runtime team is min(OMP_NUM_THREADS, cap), none at one thread or inside an enclosing parallel region -- and the cache-budget groups measured faster than G = 1 under mt8, round 3; BLADE_TILE_TUNE anti_g=1 keeps the previous order"

/// dantisym_tri's row panels per group and the plan phrase that states why
/// (BLADE_TILE_TUNE anti_g; the C++ driver's and the LLVM core's rowGroup).
/// A GROUP packs G row panels' X(i) / Y(i) pairs (G x 2 x RM x n doubles per
/// thread) and visits each column panel's X(j) / Y(j) pair (2 x RN x n
/// doubles) with every member in turn: each member rereads the pair; the
/// modeled lower-cache traffic assumes one fetch per group and residency
/// across its visits (a model, not a measured fetch count).
/// DEFAULT (anti_g unset) PER REGIME (round 3, W2-0b; the wave-1 harness
/// A/B, bench/notes/round3/w1-ab.md, three interleaved sessions per mirror,
/// pooled n = 63): a SERIAL tile takes G = 1, the previous order (the cache
/// budget measured 1.0-2.0% SLOWER serially: 1.010 llvm, 1.020 cpp); a
/// TEAM-ELIGIBLE tile takes the cache-budget policy (measured 4.8% FASTER
/// under mt8: 0.952 [0.929, 0.987] cpp). Team-eligible = the driver is asked
/// for a team (`teamRequested`: the kernel's omp licence AND thread emission
/// on -- the driver's own `threaded` argument, unchanged) AND its STATIC work
/// cap (`antisymTeamWorkCap`, (p * p * n) >> 20) is at least 2: below that the
/// driver never forms a team (29 x 11: cap 0; 61 x 401: cap 1; 193 x 401: 14;
/// the bench's 1201 x 401: 551). Everything else is serial: thread emission
/// off (BLADE_OMP_THREADS=1/0/off), no licence, a cap below 2, or the LLVM
/// lane (no thread runtime). ELIGIBILITY, not a guaranteed team: at run time
/// OMP_NUM_THREADS=1 or an enclosing parallel region still runs the call
/// serially (with the cache-budget G, which no bit depends on). The regime
/// is the tile's own, not the build's: an unlicensed tile in a threaded build
/// runs serially, which is the serial measurement's situation. anti_g = 0:
/// the cache-budget policy in either regime, the largest fitting G in
/// {4, 2}, otherwise 1, with
///     G * 2 * RM * n * 8 + 2 * RN * n * 8 <= 256 KiB,
/// decided at COMPILE time on the literal n (the header carries no copy of
/// the rule). The budget covers the ACTIVE B pair -- the one column panel the
/// group is visiting -- not the whole packed B (n = 401: 4 groups' A pairs
/// 150.4 KiB + the B pair 50.1 KiB = 200.5 KiB, G = 4; n = 1001: G = 2 needs
/// 312.8 KiB, G = 1). The fallback G = 1 need not fit either (6x8 at n = 1201
/// needs 269,024 B): it is simply the previous order. 1..8 forces G (in
/// either regime). No bit depends on G: the group visits the same (i0, j0)
/// tile calls with the same arguments, in another order.
/// `paired` = the 4 x 4 schedule (RM = RN = 4), else the sequential 6 x 8.
/// `teamRequested` = the C++ lane's `threaded` driver argument (licence AND
/// thread emission); always false in the LLVM lane.
let antisymRowGroup (p: int64) (n: int64) (paired: bool) (teamRequested: bool) : int64 * string =
    let rm, rn = if paired then 4L, 4L else 6L, 8L
    let cap = antisymTeamWorkCap p n
    let teamEligible = teamRequested && cap >= 2I
    let bytes (g: int64) = g * 2L * rm * n * 8L + 2L * rn * n * 8L
    let budgetPolicy (why: string) =
        let budget = antisymRowGroupBudgetBytes
        match [ 4L; 2L ] |> List.tryFind (fun g -> bytes g <= budget) with
        | Some g -> g, $"row groups of {g} (cache budget {bytes g / 1024L} KiB of {budget / 1024L} KiB{why})"
        | None ->
            let over = (bytes 2L + 1023L) / 1024L
            1L, $"row groups of 1 -- cache budget: B pair + 2 row panels = {over} KiB > {budget / 1024L} KiB{why}"
    match tileTune "anti_g" -1L with
    | v when v >= 1L ->
        let g = min 8L v
        g, $"row groups of {g} (BLADE_TILE_TUNE anti_g={g})"
    | v when v < 0L && not teamRequested ->
        1L, antisymSerialDefaultNote antisymNoTeamRequested
    | v when v < 0L && not teamEligible ->
        1L, antisymSerialDefaultNote (antisymNoTeamByWorkCap cap)
    | v when v < 0L ->
        budgetPolicy (antisymTeamEligibleNote cap)
    | _ -> budgetPolicy ""

/// The syrk / simplex edge tiles' kloop (BLADE_TILE_TUNE tri_edge): 1 = the
/// live-accumulator variants (default), 0 = the full 6 x 8 kloop. Speed only.
let triTileEdge () : int64 = if tileTune "tri_edge" 1L = 0L then 0L else 1L

/// The fold-form phrase for `triTileEdge` (both lanes' plan lines).
let triEdgeNote (edge: int64) : string =
    if edge <> 0L then ", live-accumulator edges" else ", full-kloop edges (BLADE_TILE_TUNE tri_edge=0)"

/// The row-slot form's STATIC WORK GATE (BLADE_TILE_TUNE rowslots_min,
/// bench gap G35): the total number of inner fold steps (outer rows x each
/// row's fold cells) below which a threaded complete-row fold is declined
/// and the serial walk kept -- a team plus a slot array is not free, and a
/// row of a handful of adds does not pay for it. Default 2^18 = 262144 is
/// PROVISIONAL: chosen on operation counts alone (it declines the bench's
/// input-checksum rows and admits its logical-walk checksums), with no
/// timing behind it yet. Speed-only: no bit depends on it.
let rowSlotsMin () : int64 = max 0L (tileTune "rowslots_min" 262144L)

/// BLADE_FOLD_ROW_SLOTS, a POLICY knob (round 3, W2-0a):
///   unset / `1` / `on` / `true` = under BLADE_FP_REPRO=1 (or BLADE_FP_REASSOC=0) a
///             `where omp` fold whose outer level's value is one complete
///             scalar fold per index runs those row folds on the thread team
///             into indexed slots, then the outer chain serially (G35) --
///             EXCEPT where the ordered orbit scatter admits the same walk:
///             there the scatter takes precedence (measured faster under mt8
///             repro, round 3: pairkernel 0.841, comoment3 0.843 of the row
///             slots' time, bench/notes/round3/w1-ab.md);
///   `prefer` = the row slots win wherever both admit (the round-2 order);
///   `0` / `off` / `false` = no row slots at all (the serial walk, or the
///             scatter where it admits the walk).
/// Any other spelling reads as the default. Emission-only (C++ lane; the
/// LLVM lane has no thread runtime). FUNCTIONS, read per call.
type RowSlotsPolicy =
    | RowSlotsOff
    | RowSlotsAdmit
    | RowSlotsPrefer

let foldRowSlotsPolicy () : RowSlotsPolicy =
    match Blade.Toolchain.envSwitch "BLADE_FOLD_ROW_SLOTS" with
    | Blade.Toolchain.SwitchOff -> RowSlotsOff
    | Blade.Toolchain.SwitchOther t when t.ToLowerInvariant() = "prefer" -> RowSlotsPrefer
    | _ -> RowSlotsAdmit

let foldRowSlotsEnabled () : bool = foldRowSlotsPolicy () <> RowSlotsOff

/// `BLADE_FOLD_ROW_SLOTS=prefer`: a taken row-slot form wins over an
/// admitted ordered orbit scatter (the round-2 precedence).
let foldRowSlotsPreferred () : bool = foldRowSlotsPolicy () = RowSlotsPrefer

/// B1's producer block between lane-state feeds (BLADE_TILE_TUNE epi_blk,
/// round-2 cell epilogues): cells per block, default 256 (2 KB of double).
/// It MUST be a positive multiple of the lane block B = K * 32 / sizeof(T)
/// (a feed consumes whole blocks only); any other value is ignored and the
/// default is used (256 is a multiple of every B: 16 / 32 / 64). Speed only:
/// the lane tree is fixed by B, not by the block, so no bit depends on it.
let cellEpiBlock (laneBlock: int64) : int64 =
    let v = tileTune "epi_blk" 256L
    if v > 0L && laneBlock > 0L && v % laneBlock = 0L then v else 256L

/// B1-repro / B2r's cell-loop mini-block (BLADE_TILE_TUNE epi_blk_r): 0 (the
/// default: a chosen simple competitive value -- the round-2 drop sweep over
/// 8-64 showed no consistent winner, without paired uncertainty) =
/// one fused loop; n > 0 strip-mines the cell loop into n-cell blocks. Speed
/// only: the chain visits the same cells in the same order either way.
let cellEpiBlockR () : int64 = max 0L (tileTune "epi_blk_r" 0L)

/// The ordered orbit scatter's row GROUP (BLADE_TILE_TUNE scatter_g; default
/// 4, clamped to 1..8): canonical rows fed together, so the group's row chains
/// take one column per step and each column chain takes G terms in row order.
/// Speed only: every chain receives its source sequence for any G (the group
/// order proof in OrbitFold.planStrict's header), so no bit depends on it.
let scatterRowGroup () : int = int (max 1L (min 8L (tileTune "scatter_g" 4L)))

/// The ordered orbit scatter's column BLOCK (BLADE_TILE_TUNE scatter_b;
/// default 64, clamped to 8..1024): columns whose G terms are formed into the
/// term buffer before the column and row chains consume them. Speed only.
let scatterColBlock () : int = int (max 8L (min 1024L (tileTune "scatter_b" 64L)))

/// The LLVM lane's scatter row chains for a builtin `+` on double at G % 4 = 0
/// (BLADE_TILE_TUNE scatter_vrow): default 1 = explicit `<4 x double>` adds (one
/// vertical add per column per 4 chains, the C++ lane's v4d), 0 = scalar chains.
/// Speed only (each lane is one chain either way); measured within noise.
let scatterVectorRows () : bool = tileTune "scatter_vrow" 1L <> 0L

/// BLADE_SYRK_TILE: unset / `1` / `on` = the packed-triangle SYRK tile is the
/// BLAS-off arm of the same-array `comm` row contraction (default); `0` / `off`
/// keeps the plain per-cell nest, for A/B. A FUNCTION, read per call, so a
/// harness can pin it mid-process. It changes the emission, so the exe cache
/// keys on it for free.
let syrkTileEnabled () : bool = Blade.Toolchain.envFlag "BLADE_SYRK_TILE" true

/// BLAS-ON PRECEDENCE (bench gap G28, 2026-10-19): with the BLAS gate on, a
/// real f64 same-array `comm` row contraction or `gram(A, A)` node that the
/// tile would take ANYWAY (literal extents above `packedTriWorth`, knob on)
/// goes to the tile, not to host `?syrk`. Measured at the bench's gram shape
/// (p = 1601, k = 1001, Zen 3): the tile 57.96 ms serial / 15.58 ms at 8
/// threads against an explicit `dsyrk` caller's 62.5-65.4 / 19.3-20.0 ms --
/// and the BLAS route additionally stages a dense p x p buffer and repacks
/// it (blade_linalg.hpp). The library keeps everything the tile does not
/// cover: complex (herk), float32, runtime extents, small shapes -- and an
/// explicit device route (BLADE_CUBLAS) is never preempted. BLADE_SYRK_TILE=0
/// hands the eligible shapes back to `?syrk`. The values move from the
/// library's blocked association to the serial chain per cell (ulps; the
/// bench pins hold at their tolerance -- re-validated with `run.py check
/// gram` in the blas regime).
///
/// WHICH SIDE wins at an eligible shape is `LinAlgPatterns.blasPreference`
/// (BLADE_BLAS_PREFER, env or blade.toolchain.json): `native` (and `auto`
/// unless the library is identified as MKL) takes the tile; `library` keeps
/// `?syrk`. `syrkTileEligibleShape` is the shape half alone, so a site can
/// say in the plan that it skipped an eligible tile for the library.
let syrkTileEligibleShape (p: string) (k: string) : bool =
    let lit (s: string) = match System.Int64.TryParse s with | true, v -> Some v | _ -> None
    syrkTileEnabled ()
    && (match lit p, lit k with
        | Some p, Some k -> packedTriWorth p k
        | _ -> false)

let syrkTilePreferredOverBlas (p: string) (k: string) : bool =
    syrkTileEligibleShape p k
    && (Blade.LinAlgPatterns.blasPreference ()).Prefer = Blade.LinAlgPatterns.PreferNative

/// The antisym pair's tile schedule (`LinAlgPatterns.AntiPair`, driver
/// blade_pgemm::dantisym_tri), read from BLADE_ANTISYM_TILE per call:
/// unset / `1` / `on` / `6x8` = the SEQUENTIAL 6 x 8 tile (P pass, spill, Q
/// pass: 12 accumulators, 0.67 loads per FMA -- the reviewed default);
/// `4x4` / `paired` = the PAIRED 4 x 4 tile (P and Q in one pass: 8
/// accumulators, 1.25 loads per FMA, no spill -- the measured alternative,
/// bench/notes/microkernel-sketches-review.md S2); `0` / `off` = the plain
/// per-cell nest. Anything else is the default. Same bits in every schedule
/// (each cell's two chains and one subtraction are unchanged).
type AntisymTileSchedule =
    | AntiSeq6x8
    | AntiPaired4x4
    | AntiOff

let antisymTileSchedule () : AntisymTileSchedule =
    match System.Environment.GetEnvironmentVariable "BLADE_ANTISYM_TILE" with
    | null -> AntiSeq6x8
    | v ->
        match v.Trim().ToLowerInvariant() with
        | "0" | "off" | "false" | "no" -> AntiOff
        | "4x4" | "paired" -> AntiPaired4x4
        | _ -> AntiSeq6x8

/// BLADE_SIMPLEX_TILE: unset / `1` / `on` = the rank-3 simplex tile with the
/// hoisted W = X(i) * X(j) panel (`LinAlgPatterns.Sym3`, driver
/// blade_pgemm::dsym3_tri) is the emission of the same-array 3-way `comm`
/// prodsum nest (default); `0` / `off` keeps the plain per-cell nest, for A/B.
let simplexTileEnabled () : bool = Blade.Toolchain.envFlag "BLADE_SIMPLEX_TILE" true

// ---- The explicit K-lane vector fold form (BLADE_FOLD_LANES; review M9/M14) --

/// BLADE_FOLD_LANES, read per call:
///   unset / `1` / `on` / `auto` -> the explicit K-lane vector form
///                                  (blade_fold_lanes.hpp) for every licensed
///                                  DOT-CLASS fold, K by `foldVecLanes`; and
///                                  the simd form inside Path B chunks
///   `4` / `8`                   -> the same, with that K everywhere (A/B)
///   `0` / `off`                 -> the forms before M9/M14: ONE `omp simd
///                                  reduction` accumulator, and a serial chain
///                                  inside each Path B chunk
/// It changes the emission, so the exe cache keys on it for free. Inert under
/// BLADE_FP_REPRO=1, which never reaches a reassociated form.
type FoldLanesMode =
    | FoldLanesOff
    | FoldLanesAuto
    | FoldLanesFixed of int

let foldLanesMode () : FoldLanesMode =
    match System.Environment.GetEnvironmentVariable "BLADE_FOLD_LANES" with
    | null -> FoldLanesAuto
    | v ->
        match v.Trim().ToLowerInvariant() with
        | "0" | "off" | "false" | "no" -> FoldLanesOff
        | "4" -> FoldLanesFixed 4
        | "8" -> FoldLanesFixed 8
        | _ -> FoldLanesAuto

/// The vector forms blade_fold_lanes.hpp spells, by the kernel's factor list
/// (source order; a repeated array is a self-dot): `sum` (one stream),
/// `dot` (two), `sq` (one stream read once, squared), `dot3` (three, the
/// product left-nested `(a * b) * c`).
type FoldVecKind =
    | VecSum
    | VecDot
    | VecSq
    | VecDot3

/// The form for a factor list of 1..3 array names, with the DISTINCT operand
/// names in the call's order; None for anything else.
let foldVecKind (factors: string list) : (FoldVecKind * string list) option =
    match factors with
    | [ a ] -> Some (VecSum, [ a ])
    | [ a; b ] when a = b -> Some (VecSq, [ a ])
    | [ a; b ] -> Some (VecDot, [ a; b ])
    | [ a; b; c ] -> Some (VecDot3, [ a; b; c ])
    | _ -> None

let foldVecKindName (k: FoldVecKind) : string =
    match k with VecSum -> "sum" | VecDot -> "dot" | VecSq -> "sq" | VecDot3 -> "dot3"

/// The number of distinct memory streams a form reads per element.
let foldVecStreams (k: FoldVecKind) : int =
    match k with VecSum -> 1 | VecDot -> 2 | VecSq -> 1 | VecDot3 -> 3

/// K, the number of independent 32-byte vector accumulators. The rule is by
/// the form's load/FMA ratio on Zen 3 (2 loads + 2 FMAs per cycle, FMA latency
/// 4): a two-stream dot is load-bound at one vector FMA per cycle, which 4
/// chains cover; a self-dot or a plain sum issues one load per FMA and can
/// run both FMA units, which needs 8 chains. 8 is never worse than 4 at any
/// shape measured (bench/notes/gap-register.md, G26 entry), so 8 is the
/// default everywhere; `BLADE_FOLD_LANES=4|8` forces either for A/B.
let foldVecLanes (k: FoldVecKind) : int =
    match foldLanesMode () with
    | FoldLanesFixed n -> n
    | _ -> 8

/// Is `elemStr` an element type blade_fold's vector forms are defined for?
let foldVecElem (elemStr: string) : bool =
    elemStr = "double" || elemStr = "float"

/// The C++ call: `blade_fold::<kind><T, K>(p0, p1, .., n)` over raw element
/// pointers (`.data` of a rank-1 Array, `+ lo` for a chunk) and an element
/// count. Sets nothing: the caller marks `foldLanesUsedCell` (the include).
let foldVecCall (elemStr: string) (k: int) (kind: FoldVecKind) (ptrs: string list) (nExpr: string) : string =
    let args = String.concat ", " (ptrs @ [ nExpr ])
    $"blade_fold::{(foldVecKindName kind)}<{elemStr}, {k}>({args})"

/// The census / `blade plan` spelling of the form: `simd K-lanes(8) dot`.
let foldVecLabel (kind: FoldVecKind) (k: int) : string =
    let word =
        match kind with VecSum -> "sum" | VecDot -> "dot" | VecSq -> "self-dot" | VecDot3 -> "dot3"
    $"simd K-lanes({k}) {word}"

/// Is `lvl` a plain rectangular sweep a fold may take a lane / simd / vector
/// form over -- no bound dependencies, no strict offset, not a fused joint
/// level, not a tabulated (compound / sparse) axis, no Reynolds body, no MPI
/// slab, no halo-window slot? ONE predicate for the one-level licensed arm of
/// `genReduceComputeBinding` and for the Path B chunk plan (M14), so the
/// serial and the threaded forms admit exactly the same levels.
let laneableFoldLevel (cg: LoopNestCodeGen) (lvl: LoopIndexBinding) : bool =
    lvl.BoundDependencies.IsEmpty && lvl.StrictOffset = 0
    && lvl.FusedRank.IsNone
    && (match lvl.Extent with
        | IRCompoundMask _ | IRSparseKeys _ -> false
        | _ -> true)
    && not cg.HasReynolds && not cg.MpiSlab
    && (lvl.Elements
        |> List.forall (fun e ->
            match e.SlotTag with
            | Some t -> not (t.StartsWith "__halowin")
            | None -> true))

/// The factor list of a DOT-CLASS kernel body over `lvl`'s peels: the body is
/// a left-nested elementwise product of 1..3 kernel parameters, each bound at
/// this level to a REAL rank-1 array (not a range / reverse / halo slot) whose
/// element type `elemOk` accepts, so the body at index i is `A[i] * B[i]
/// (* C[i])` and the whole fold is one of blade_fold's forms. Returns the
/// arrays in source factor order (a repeated array appears twice: `x * x`);
/// None for any other body -- a composed chain, a call, a cast, a scalar
/// capture in the product -- which keeps the simd form.
let dotClassFactors (elemOk: IRType -> bool) (body: IRExpr) (kernelParams: IRParam list) (lvl: LoopIndexBinding) : string list option =
    // A slot's tag is its index type's NAME for a named index (`Ix`); only the
    // halo-window payload marks a slot that is not a plain element read.
    let plainSlot (e: ElementBinding) =
        match e.SlotTag with
        | Some t -> not (t.StartsWith "__halowin")
        | None -> true
    let arrayOfParam (vid: IRId) =
        lvl.Elements
        |> List.tryPick (fun e ->
            if e.ParamVarId = vid && e.Virtual.IsRealArray && e.ArrayRank = 1
               && plainSlot e && elemOk e.ArrayElemType
            then Some e.ArrayName else None)
    let leaf (e: IRExpr) =
        match e with
        | IRVar (id, _) -> arrayOfParam id
        | IRParam (_, idx, _) ->
            kernelParams |> List.tryFind (fun p -> p.Index = idx) |> Option.bind (fun p -> arrayOfParam p.VarId)
        | _ -> None
    match body with
    | IRBinOp (IRElementwise, IRMul, IRBinOp (IRElementwise, IRMul, a, b, _), c, _) ->
        (match leaf a, leaf b, leaf c with
         | Some x, Some y, Some z -> Some [ x; y; z ]
         | _ -> None)
    | IRBinOp (IRElementwise, IRMul, a, b, _) ->
        (match leaf a, leaf b with
         | Some x, Some y -> Some [ x; y ]
         | _ -> None)
    | e -> leaf e |> Option.map (fun x -> [ x ])

/// Is the antisym pair's tile worth it over the per-cell double dot? The
/// review's routes: tile at p = 61 x n = 401 / 600, decline at 23 x 11 (a
/// strict 23-row triangle is a handful of 6 x 8 tiles, mostly padding). One
/// statement of the rule, decided on the literal extents.
let packedAntiWorth (p: int64) (n: int64) : bool =
    p >= 24L && n >= 8L

/// Is the rank-3 simplex tile worth it over the per-cell triple dot? The
/// review's routes: tile at (29, 67) (29, 201) (61, 11) (61, 201), decline at
/// (13, 11) (13, 201) -- a 13-wide simplex leaves most of every 6 x 8 tile
/// below its k >= j edge.
let packedSym3Worth (p: int64) (n: int64) : bool =
    p >= 24L && n >= 4L


/// Can `A(i1, ..., ik)` -- `consumed` positional subscripts, fewer than A's
/// records -- be emitted as the sub-view aggregate
/// `Array<T, R>{ A.data[i1]...[ik], A.extents + k }`? The ONE predicate both
/// twins ask (the let-binding arm in CodeGenLoopNest and the expression arm in
/// CodeGenExpr); they must answer identically or a let-bound slice and an
/// inline slice diverge in shape.
///
/// CONSUMED records (the subscripted-through leading ones) must be plain
/// rank-1 positions -- or group-outer, whose grid is a dense pool with the
/// group count in extents[0] -- so the subscript count IS the coordinate
/// count the data pointer steps through and the extents shift past.
///
/// RESIDUAL records are plain rank-1 axes or WHOLE compact groups (SymIdx /
/// AntisymIdx / HermitianIdx, rank >= 2). The packed skeleton allocate<> builds
/// shrinks a row only INSIDE a group (`extents[d] - lastIndex`, the lastIndex
/// threaded from the group's previous coordinate), and a plain record before
/// the group threads nothing, so `A.data[i]` of `Idx<m>, SymIdx<2, n>` is
/// exactly the skeleton a standalone `Array<T like SymIdx<2, n>>` holds, at the
/// same coordinate extents: the view shares the pool and every compact read of
/// it (canon_fold / canon_left_justify / canon_transform) addresses it as it
/// would the stored group. A partially-read group, or a group completed with
/// slots after it, is refused at typecheck (TypeCheckSupport.compactCoordWalk);
/// ragged/dep-idx/compound/sparse/wreath residuals keep their own read paths.
///
/// An IRREPS residual (IrrepsIdx / point-group irreps, rank 1) counts as plain:
/// those spaces are dense by design (IR.placementClass), one flat axis of the
/// spec's total dimension, so a row of `Array<T like I, IrrepsIdx<S>>` is the
/// same `{ A.data[i], A.extents + 1 }` view a plain row is. It used to fall to
/// the bare `A[i]` (a `T*`), which g++ refused to pass as an `Array<T, 1>`.
let internal subviewAxesOk (arrTy: IRArrayType) (consumed: int) : bool =
    let plainAxis (ix: IRIndexType) =
        (ix.IxKind = IxKPlain || ix.IxKind = IxKIrreps || ix.IxKind = IxKPgIrreps)
        && ix.Symmetry = SymNone && ix.Rank = 1
    let compactGroup (ix: IRIndexType) =
        ix.IxKind = IxKPlain && ix.Rank >= 2
        && (match ix.Symmetry with
            | SymSymmetric | SymAntisymmetric | SymHermitian -> true
            | SymNone | SymWreath -> false)
    consumed > 0
    && arrTy.IndexTypes.Length > consumed
    && arrTy.IndexTypes |> List.mapi (fun d ix -> (d, ix))
       |> List.forall (fun (d, ix) ->
            if d < consumed then
                (ix.IxKind = IxKPlain || ix.IxKind = IxKGroupOuter)
                && ix.Symmetry = SymNone && ix.Rank = 1
            else plainAxis ix || compactGroup ix)

/// THE COMPACTION PASS LOOP (Blade.Optimize.planCompactionFusion): the loop
/// over the source axis that both compaction-fusion passes run -- B4's
/// fill+gather pass at the compound and T4a's predicate+gather pass at the
/// mask -- in 256-cell BLOCKS: `block body lo hi` gives the lines of one
/// block [lo, hi) at indentation `body` (the block advances `cursor`, the
/// count of kept cells written so far).
///
/// With `feeds` (accumulator, kernel wrapper) -- the serial chains of the
/// planned `reduce(y * y, (+))` folds (B4c) -- after each block every chain
/// advances over the compact cells that block COMPLETED (index < cursor; the
/// slack cell the branchless store lands on is never read): the first kept
/// square peeled as the chain's start, then `acc = k(acc, v * v)` in kept
/// order, each product rounded before its add (BLADE_FOLD_ROUND), as a
/// stored product is. The same terms in the same order wherever the pass
/// runs, and whatever a block does inside.
///
/// `pre` names the loop's own scratch (`<pre>_b`, `<pre>_e`, `<pre>_f`).
let rec compactionBlockLoop (ind: string) (pre: string) (bound: string) (elemCpp: string) (compact: string) (cursor: string) (feeds: (string * string) list) (block: string -> string -> string -> string list) : string list =
    compactionBlockLoopMulti ind pre bound elemCpp cursor (feeds |> List.map (fun (acc, wname) -> (acc, wname, compact))) block

/// `compactionBlockLoop` whose chains may each read a DIFFERENT compact
/// buffer (the shared selection, T4c: every companion's buffer advances on
/// the one cursor `q`, so one completed-cell counter serves every chain).
/// `feeds` = (accumulator, kernel wrapper, the buffer it folds).
and compactionBlockLoopMulti (ind: string) (pre: string) (bound: string) (elemCpp: string) (cursor: string) (feeds: (string * string * string) list) (block: string -> string -> string -> string list) : string list =
    let i1 = ind + "    "
    let i2 = i1 + "    "
    let peel =
        feeds |> List.map (fun (acc, _, compact) ->
            $"{i2}    {{ {elemCpp} __t = {compact}[0] * {compact}[0]; BLADE_FOLD_ROUND(__t); {acc} = __t; }}")
    let step =
        feeds |> List.map (fun (acc, wname, compact) ->
            $"{i2}    {{ {elemCpp} __t = {compact}[{pre}_f] * {compact}[{pre}_f]; BLADE_FOLD_ROUND(__t); {acc} = {wname}({acc}, __t); }}")
    let chain =
        if feeds.IsEmpty then []
        else
            [ $"{i1}// the chain over the cells this block completed (index < q; the slack cell is never read)"
              $"{i1}if ({pre}_f < {cursor}) {{"
              $"{i2}if ({pre}_f == 0) {{" ]
            @ peel
            @ [ $"{i2}    {pre}_f = 1;"
                $"{i2}}}"
                $"{i2}for (; {pre}_f < {cursor}; {pre}_f++) {{" ]
            @ step
            @ [ $"{i2}}}"
                $"{i1}}}" ]
    (if feeds.IsEmpty then [] else [ $"{ind}size_t {pre}_f = 0;" ])
    @ [ $"{ind}for (size_t {pre}_b = 0; {pre}_b < {bound}; {pre}_b += 256) {{"
        $"{i1}const size_t {pre}_e = ({bound} - {pre}_b < 256) ? {bound} : {pre}_b + 256;" ]
    @ block i1 $"{pre}_b" $"{pre}_e"
    @ chain
    @ [ $"{ind}}}" ]

/// The per-CELL form of the pass (B4's compound-site fill+gather pass): one
/// unblocked loop when no chain is fed, else the block loop above with one
/// inner cell loop per block. `cell body` gives one source cell's lines.
let compactionPassLoop (ind: string) (pre: string) (iv: string) (bound: string) (elemCpp: string) (compact: string) (cursor: string) (feeds: (string * string) list) (cell: string -> string list) : string list =
    if feeds.IsEmpty then
        [ $"{ind}for (size_t {iv} = 0; {iv} < {bound}; {iv}++) {{" ]
        @ cell (ind + "    ")
        @ [ $"{ind}}}" ]
    else
        compactionBlockLoop ind pre bound elemCpp compact cursor feeds (fun body lo hi ->
            [ $"{body}for (size_t {iv} = {lo}; {iv} < {hi}; {iv}++) {{" ]
            @ cell (body + "    ")
            @ [ $"{body}}}" ])

/// The per-cell pass over several companion buffers (T4c's joint pass).
let compactionPassLoopMulti (ind: string) (pre: string) (iv: string) (bound: string) (cursor: string) (feeds: (string * string * string) list) (cell: string -> string list) : string list =
    if feeds.IsEmpty then
        [ $"{ind}for (size_t {iv} = 0; {iv} < {bound}; {iv}++) {{" ]
        @ cell (ind + "    ")
        @ [ $"{ind}}}" ]
    else
        compactionBlockLoopMulti ind pre bound "double" cursor feeds (fun body lo hi ->
            [ $"{body}for (size_t {iv} = {lo}; {iv} < {hi}; {iv}++) {{" ]
            @ cell (body + "    ")
            @ [ $"{body}}}" ])