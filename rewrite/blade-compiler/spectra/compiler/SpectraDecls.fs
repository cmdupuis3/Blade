/// Spectra-module decl builders: FFT and arity-polymorphic polyspectra
/// synthesized as plain Blade source (the MathDecls mold).
///
/// Order polymorphism lives HERE: polyspecDecl is ONE order-generic F#
/// function that emits a fixed-order Blade FunctionDecl per requested
/// polyspectrum order (the call-site arity).
///
/// House style (MathDecls): mut work arrays, for-in nests, no statement
/// control flow. Complex values are native Complex128 scalars built with
/// the complex(re, im) constructor call (typecheck intrinsic).
///
/// THE ULP CONTRACT (shared with spectra/Fft.fs, spectra/Polyspec.fs — the
/// standalone .NET oracle): every trig value in generated code is an F#
/// System.Math.Cos/Sin literal baked at elaboration time (no runtime trig,
/// so libm never enters the picture), and the oracle performs the SAME
/// arithmetic in the SAME order on the SAME tables. isPow2/fftStages/bitrev
/// and each kernel's loop structure MUST stay textually parallel with the
/// oracle. Complex multiply is the naive component formula on both sides
/// (finite values: std::complex agrees bit-for-bit). No complex division
/// anywhere (libstdc++'s scaled division would diverge from any mirror).
module Blade.Spectra.Decls

open Blade.Ast
open Blade.Math.Decls

// ============================================================================
// Complex AST helpers (Float64 pair -> native Complex128)
// ============================================================================

/// complex(re, im) from arbitrary Float64 exprs — the surface constructor
/// call (typecheck intrinsic, infers Complex128). Generated decls never
/// shadow the name, so the intrinsic always resolves.
let cplx (re: Expr) (im: Expr) = ExprApp (ExprVar "complex", [re; im])
let cplxLit (re: float) (im: float) = cplx (fLit re) (fLit im)
let cplxZerosLit (n: int) = ExprArrayLit (List.replicate n (cplxLit 0.0 0.0))
let cplxArrLit (pairs: (float * float) list) =
    ExprArrayLit (pairs |> List.map (fun (re, im) -> cplxLit re im))
let intArrLit (xs: int list) = ExprArrayLit (xs |> List.map iLit)
let conjE e = ExprUnaryOp (OpConj, e)
let realE e = ExprApp (v "real", [e])
let imagE e = ExprApp (v "imag", [e])
let modE a b = ExprBinOp (Elementwise, OpMod, a, b)
let tyCplxArr (n: int) = TyArray (TyComplex128, [ TyIdx (iLit n) ])
/// Rank-N dense complex tensor type.
let tyCplxTensor (dims: int list) =
    TyArray (TyComplex128, dims |> List.map (fun d -> TyIdx (iLit d)))

// ============================================================================
// FFT structure helpers — MUST match spectra/Fft.fs (oracle) exactly
// ============================================================================

let isPow2 (n: int) = n > 0 && (n &&& (n - 1)) = 0

/// log2 n by integer doubling (n a power of two).
let fftStages (n: int) =
    let mutable s = 0
    let mutable l = 1
    while l < n do
        l <- l * 2
        s <- s + 1
    s

/// Reverse the low `bits` bits of x.
let bitrev (bits: int) (x: int) =
    let mutable r = 0
    let mutable xx = x
    for _ in 1 .. bits do
        r <- (r <<< 1) ||| (xx &&& 1)
        xx <- xx >>> 1
    r

/// Forward twiddle table entries j = 0 .. m-1: e^{-2πi·j/n}.
let fwdTwiddles (n: int) (m: int) : (float * float) list =
    [ for j in 0 .. m - 1 ->
        (cos (-2.0 * System.Math.PI * float j / float n),
         sin (-2.0 * System.Math.PI * float j / float n)) ]

/// Inverse-synthesis twiddle table entries j = 0 .. m-1: e^{+2πi·j/n}.
let invTwiddles (n: int) (m: int) : (float * float) list =
    [ for j in 0 .. m - 1 ->
        (cos (2.0 * System.Math.PI * float j / float n),
         sin (2.0 * System.Math.PI * float j / float n)) ]

// ============================================================================
// fft — unnormalized forward DFT of a real signal, complex output
// ============================================================================

/// Radix-2 iterative Cooley-Tukey for power-of-2 n; naive table-driven
/// O(n²) DFT otherwise. Stages are statically unrolled in F#: a runtime
/// stage loop would need the multiplicative loop-carried scalar `len = len*2`,
/// which Grad's loop discipline rejects (spectra elaborates BEFORE Grad) —
/// do not "simplify" this back into a loop.
let fftDecl (name: string) (n: int) : FunctionDecl =
    let body =
        if isPow2 n && n >= 2 then
            let stages = fftStages n
            let perm = [ for i in 0 .. n - 1 -> bitrev stages i ]
            let stmts =
                [ sLet "brp" (intArrLit perm)
                  sLet "tw" (cplxArrLit (fwdTwiddles n (n / 2)))
                  sLetMut "sx" (cplxZerosLit n)
                  // Gather copy-in through the bit-reversal permutation
                  // (gather, not scatter — the oracle mirrors this direction).
                  sFor "i" 0 n
                    [ sAssign (idx "sx" (v "i")) (cplx (idx "x" (idx "brp" (v "i"))) (fLit 0.0)) ] ]
                @ [ for st in 1 .. stages do
                      let len = 1 <<< st
                      let half = len / 2
                      let tstr = n / len
                      yield sFor "b" 0 (n / len)
                        [ sFor "j" 0 half
                            [ sLet "p" (add (mul (v "b") (iLit len)) (v "j"))
                              sLet "q" (add (v "p") (iLit half))
                              sLet "t" (mul (idx "tw" (mul (v "j") (iLit tstr))) (idx "sx" (v "q")))
                              sLet "p0" (idx "sx" (v "p"))
                              sAssign (idx "sx" (v "p")) (add (v "p0") (v "t"))
                              sAssign (idx "sx" (v "q")) (sub (v "p0") (v "t")) ] ] ]
            ExprBlock (stmts, Some (v "sx"))
        else
            // Naive DFT: X(k) = Σ_i x(i) · e^{-2πi·k·i/n}, twiddle by table
            // at (k·i) mod n — nonnegative Int %, C++ semantics match F#.
            let stmts =
                [ sLet "tw" (cplxArrLit (fwdTwiddles n n))
                  sLetMut "sx" (cplxZerosLit n)
                  sFor "k" 0 n
                    [ sFor "i" 0 n
                        [ sLet "t" (modE (mul (v "k") (v "i")) (iLit n))
                          sAccum (idx "sx" (v "k"))
                                 (mul (cplx (idx "x" (v "i")) (fLit 0.0)) (idx "tw" (v "t"))) ] ] ]
            ExprBlock (stmts, Some (v "sx"))
    mkFunc name [ ("x", tyFloatArr n) ] (tyCplxArr n) body

// ============================================================================
// ifft — real inverse synthesis (carries the 1/n), any n
// ============================================================================

let ifftDecl (name: string) (n: int) : FunctionDecl =
    let stmts =
        [ sLet "tw" (cplxArrLit (invTwiddles n n))
          sLetMut "xo" (zerosLit n)
          sFor "i" 0 n
            [ sFor "k" 0 n
                [ sLet "t" (modE (mul (v "k") (v "i")) (iLit n))
                  sAccum (idx "xo" (v "i")) (realE (mul (idx "xs" (v "k")) (idx "tw" (v "t")))) ]
              // Post-loop rescale: non-additive ARRAY-cell write (Grad-legal;
              // only scalars carry the additive-accumulation restriction).
              sAssign (idx "xo" (v "i")) (divE (idx "xo" (v "i")) (fLit (float n))) ] ]
    mkFunc name [ ("xs", tyCplxArr n) ] (tyFloatArr n) (ExprBlock (stmts, Some (v "xo")))

// ============================================================================
// power — |FFT(x)|² per bin (real)
// ============================================================================

let powerDecl (name: string) (n: int) (fftName: string) : FunctionDecl =
    let xk = idx "sx" (v "k")
    let stmts =
        [ sLet "sx" (ExprApp (v fftName, [ v "x" ]))
          sLetMut "p" (zerosLit n)
          sFor "k" 0 n
            [ sAssign (idx "p" (v "k"))
                      (add (mul (realE xk) (realE xk)) (mul (imagE xk) (imagE xk))) ] ]
    mkFunc name [ ("x", tyFloatArr n) ] (tyFloatArr n) (ExprBlock (stmts, Some (v "p")))

// ============================================================================
// polyspec — order-k cross-polyspectrum (order = call-site arity)
// ============================================================================

/// P(f_0..f_{k-2}) = X_1(f_0) ··· X_{k-1}(f_{k-2}) · conj(X_k((Σf) mod n)),
/// a rank-(k-1) complex array. The order lives entirely in this F# generator:
/// k fft calls, a (k-1)-deep loop nest, and a statically-unrolled complex
/// product chain of fresh lets.
let polyspecDecl (name: string) (n: int) (k: int) (fftName: string) : FunctionDecl =
    let outDims = List.replicate (k - 1) n
    let outSize = prodInts outDims
    let fvars = [ for j in 0 .. k - 2 -> sprintf "f%d" j ]
    let strides = [ for j in 0 .. k - 2 -> prodInts (List.replicate (k - 2 - j) n) ]
    let flatIdx =
        List.map2 (fun fv st -> mul (v fv) (iLit st)) fvars strides
        |> List.reduce add
    let ffts =
        [ for i in 1 .. k -> sLet (sprintf "s%d" i) (ExprApp (v fftName, [ v (sprintf "x%d" i) ])) ]
    let chain =
        [ yield sLet "a1" (idx "s1" (v "f0"))
          for j in 2 .. k - 1 do
            yield sLet (sprintf "a%d" j)
                       (mul (v (sprintf "a%d" (j - 1))) (idx (sprintf "s%d" j) (v (sprintf "f%d" (j - 1))))) ]
    let inner =
        sLet "sm" (modE (fvars |> List.map v |> List.reduce add) (iLit n))
        :: chain
        @ [ sAssign (idx "pp" flatIdx)
                    (mul (v (sprintf "a%d" (k - 1))) (conjE (idx (sprintf "s%d" k) (v "sm")))) ]
    let stmts =
        ffts
        @ [ sLetMut "pp" (cplxZerosLit outSize) ]
        @ loopNest fvars outDims inner
    let body =
        if k = 2 then
            // Rank-1 output: the flat mut IS the result.
            ExprBlock (stmts, Some (v "pp"))
        else
            // Rank-(k-1): reshape the flat work array through a nested
            // literal of runtime reads (element-type-agnostic, so the
            // MathDecls helper serves complex cells too).
            ExprBlock (stmts @ [ sLet "po" (nestedFromFlatN "pp" outDims 0) ], Some (v "po"))
    let ps = [ for i in 1 .. k -> (sprintf "x%d" i, tyFloatArr n) ]
    mkFunc name ps (tyCplxTensor outDims) body
