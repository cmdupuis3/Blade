/// Spectra-module elaboration: FFT and arity-polymorphic polyspectra as compile-time source synthesis (the MathElaborate mold).
///
/// Surface (reachable only through `import spectra [as <alias>]`). The ops read a STATIC shape and element type (this
/// pass runs before type inference, and the generated transform bakes in the per-axis radix choice plus the twiddle and
/// bit-reversal tables), which Blade.ElabShapes answers for every domain elaborator: a declared annotation, an array
/// literal / cast / static range, the result of an elaborated call (`sp.ifft(sp.fft(x))`, a math call's result), or an
/// alias, destructured component or projection of any of those.
///
///   sp.fft(x)             -- real x -> Array<Complex128 like Idx<n>> (unnormalized)
///   sp.ifft(X)            -- complex spectrum -> real signal (carries the 1/n)
///   sp.power(x)           -- |FFT(x)|^2 per bin (real)
///   sp.polyspec(x1,..,xk) -- order-k cross-polyspectrum, k = the CALL-SITE
///                            ARITY (2 cross-power, 3 bispectrum, 4 trispectrum):
///                            P(f_0..f_{k-2}) = X1(f_0)***X_{k-1}(f_{k-2}) * conj(Xk((Sum f) mod n))
///
/// For each distinct (op, resolved shape/order) the elaborator synthesizes ONE Blade function (`__spectra_1`, ...) via
/// Blade.Spectra.Decls; call sites rewrite to the generated names, deduped by fingerprint -- every op at one n shares a
/// single generated FFT.
///
/// Pipeline position: after ML/PPL/Math/Rand elaboration, BEFORE Grad expansion. The surface shapes are registered
/// (`registerShapes`) before ANY elaboration runs, so an earlier pass (math, ppl, sgs) can read a spectra call's result.
module Blade.Spectra.Elaborate

open Blade.Ast
open Blade.ElabShapes
open Blade.Spectra.Decls

/// Element-type classes the ops care about.
type private ElemClass = ElemFloat | ElemComplex | ElemOther

let private classifyElem (ty: TypeExpr) : ElemClass =
    match ty with
    | TyFloat64 | TyNamed (("Float" | "Float64" | "Double"), []) -> ElemFloat
    | TyComplex128 | TyNamed ("Complex128", []) -> ElemComplex
    | _ -> ElemOther

/// The steer appended to every "no static shape here" rejection: the ops need (elem, extents) at compile time.
let private steer =
    "spectra ops bake the extents into the generated transform (the per-axis radix choice and the twiddle tables), so annotate the binding (`let x: Array<Float64 like Idx<n>> = ...`) or ascribe the argument (`(e : Array<Float64 like Idx<n>>)`)"

/// An op's array argument: what messages call it, its element class, its static extents, and -- when the element is a
/// literal's DEFAULT width -- the note a wrong-element message appends.
type private Arg = { Label: string; Elem: ElemClass; Dims: int list; Note: string }

let private argOf (env: Env) (scope: Scope) (what: string) (e: Expr) : Result<Arg, string> =
    arrayShape env scope what steer e |> Result.bind (fun (label, s) ->
        staticExtents what label s |> Result.bind (fun dims ->
            match s.Elem with
            | ElemDeclared t -> Ok { Label = label; Elem = classifyElem (resolveTop env.Aliases t); Dims = dims; Note = "" }
            | ElemDefault (t, note) ->
                Ok { Label = label; Elem = classifyElem t; Dims = dims
                     Note = $" -- {note}; annotate it (`let x: Array<Float64 like Idx<n>> = ...`)" }
            | ElemUnknown why | ElemComputed why ->
                Error $"{what}: the element type of {label} is not statically known ({why}); annotate it (`let x: Array<Float64 like Idx<n>> = ...`)"))

/// A real rank-1 signal of static length.
let private realSignal (env: Env) (scope: Scope) (what: string) (e: Expr) : Result<int, string> =
    argOf env scope what e |> Result.bind (fun a ->
        match a.Elem, a.Dims with
        | ElemFloat, [n] -> Ok n
        | ElemFloat, _ -> Error $"{what}: {a.Label} must be rank-1 (Array<Float64 like Idx<n>>)"
        | _, _ -> Error $"{what}: {a.Label} must have Float64 elements (a real signal){a.Note}")

let private ensureFft (st: GenState) (n: int) : string =
    ensureT st (fingerprint "fft" (box n)) "fft" (fun nm -> fftDecl nm n)

// Op elaboration

let private opList = "fft, ifft, fft2, ifft2, power, polyspec"

/// Cap on the polyspectrum output (the zeros literal and the C++ initializer both scale with n^(k-1); steer before g++
/// meets a million-element initializer).
let private maxOutCells = 65536

let private elabOp (st: GenState) (env: Env) (scope: Scope) (op: string) (args: Expr list) : Result<Expr, string> =
    // Every generated routine takes Float64 arrays: an inline float-literal argument takes that width.
    let args = args |> List.map ascribeFloatLiteral
    match op, args with
    | "fft", [xE] ->
        realSignal env scope "fft" xE |> Result.bind (fun n ->
            Ok (ensureFft st n) |> Result.map (fun nm -> syn (ExprApp (syn (ExprVar nm), [xE]))))
    | "fft", _ -> Error "fft: expected fft(X) -- the unnormalized forward DFT of a real signal"
    | "ifft", [xE] ->
        argOf env scope "ifft" xE |> Result.bind (fun a ->
            match a.Elem, a.Dims with
            | ElemComplex, [n] ->
                ensure st (fingerprint "ifft" (box n)) "ifft" (fun nm -> Ok (ifftDecl nm n))
                |> Result.map (fun nm -> syn (ExprApp (syn (ExprVar nm), [xE])))
            | ElemComplex, _ -> Error $"ifft: {a.Label} must be rank-1 (Array<Complex128 like Idx<n>>)"
            | _, _ -> Error $"ifft: {a.Label} must have Complex128 elements (ifft takes a complex spectrum, such as the result of fft){a.Note}")
    | "ifft", _ -> Error "ifft: expected ifft(X) -- real inverse synthesis of a complex spectrum (carries the 1/n)"
    | "fft2", [xE] ->
        argOf env scope "fft2" xE |> Result.bind (fun a ->
            match a.Elem, a.Dims with
            | ElemFloat, [r; c] ->
                if r * c > maxOutCells then
                    Error $"fft2: the {r}x{c} field has {r * c} cells; spectra outputs are capped at {maxOutCells} (the generated initializers scale with it) -- reduce the grid"
                else
                    ensure st (fingerprint "fft2" (box (r, c))) "fft2" (fun nm -> Ok (fft2Decl nm r c))
                    |> Result.map (fun nm -> syn (ExprApp (syn (ExprVar nm), [xE])))
            | ElemFloat, _ -> Error $"fft2: {a.Label} must be rank-2 (Array<Float64 like Idx<r>, Idx<c>>)"
            | _, _ -> Error $"fft2: {a.Label} must have Float64 elements (a real 2-D field){a.Note}")
    | "fft2", _ -> Error "fft2: expected fft2(X) -- the unnormalized forward 2-D DFT of a real rank-2 field"
    | "ifft2", [xE] ->
        argOf env scope "ifft2" xE |> Result.bind (fun a ->
            match a.Elem, a.Dims with
            | ElemComplex, [r; c] ->
                if r * c > maxOutCells then
                    Error $"ifft2: the {r}x{c} spectrum has {r * c} cells; spectra outputs are capped at {maxOutCells} (the generated initializers scale with it) -- reduce the grid"
                else
                    ensure st (fingerprint "ifft2" (box (r, c))) "ifft2" (fun nm -> Ok (ifft2Decl nm r c))
                    |> Result.map (fun nm -> syn (ExprApp (syn (ExprVar nm), [xE])))
            | ElemComplex, _ -> Error $"ifft2: {a.Label} must be rank-2 (Array<Complex128 like Idx<r>, Idx<c>>)"
            | _, _ -> Error $"ifft2: {a.Label} must have Complex128 elements (ifft2 takes a complex spectrum, such as the result of fft2){a.Note}")
    | "ifft2", _ -> Error "ifft2: expected ifft2(X) -- real inverse synthesis of a rank-2 complex spectrum (carries the 1/(r*c))"
    | "power", [xE] ->
        realSignal env scope "power" xE |> Result.bind (fun n ->
            let fftName = ensureFft st n
            ensure st (fingerprint "power" (box n)) "power" (fun nm -> Ok (powerDecl nm n fftName))
            |> Result.map (fun nm -> syn (ExprApp (syn (ExprVar nm), [xE]))))
    | "power", _ -> Error "power: expected power(X) -- |FFT(X)|^2 per bin"
    | "polyspec", args when args.Length >= 2 && args.Length <= 4 ->
        let k = args.Length
        args
        |> List.fold (fun acc a ->
            acc |> Result.bind (fun ns ->
                realSignal env scope "polyspec" a |> Result.map (fun n -> ns @ [n]))) (Ok [])
        |> Result.bind (fun ns ->
            match List.distinct ns with
            | [n] ->
                let cells = pown n (k - 1)
                if cells > maxOutCells then
                    Error $"polyspec: the order-{k} output at n={n} has {cells} cells; the output is capped at {maxOutCells} (the generated initializers scale with it) -- reduce n or the order"
                else
                    let fftName = ensureFft st n
                    ensure st (fingerprint "polyspec" (box (n, k))) "polyspec" (fun nm -> Ok (polyspecDecl nm n k fftName))
                    |> Result.map (fun nm -> syn (ExprApp (syn (ExprVar nm), args)))
            | _ ->
                Error ($"""polyspec: all signals must share one static length (got {(ns |> List.map string |> String.concat ", ")})"""))
    | "polyspec", args ->
        Error $"polyspec: the argument count IS the polyspectrum order and must be 2..4 (got {args.Length}) -- k=2 cross-power, k=3 bispectrum, k=4 trispectrum; the generator is order-generic, raise the cap when needed"
    | _ -> Error $"spectra: unknown op '{op}' (available: {opList})"

/// Register the spectra surface ops with the shared shape oracle (their result shapes are their generated routines'
/// signatures, read off a scratch elaboration). Idempotent; TypeCheck runs it before the FIRST elaboration pass.
let registerShapes () =
    registerDomain "spectra" (scratchShape "__spectra_scratch_" elabOp)

/// Entry point: elaborate spectra ops across a program (after ML/PPL/Math/Rand elaboration, before Grad expansion). The
/// boundary turns a string-errored internal into one coded BL5400 diagnostic at the offending declaration.
let expand (program: Program) : Result<Program, Blade.Diagnostics.Diagnostic list> =
    registerShapes ()
    expandDomain "spectra" "__spectra_" "BL5400" elabOp program
