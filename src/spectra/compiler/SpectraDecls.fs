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
let cplx (re: Expr) (im: Expr) = syn (ExprApp (v "complex", [re; im]))
let cplxLit (re: float) (im: float) = cplx (fLit re) (fLit im)
let cplxZerosLit (n: int) = syn (ExprArrayLit (List.replicate n (cplxLit 0.0 0.0)))
let cplxArrLit (pairs: (float * float) list) =
    syn (ExprArrayLit (pairs |> List.map (fun (re, im) -> cplxLit re im)))
let intArrLit (xs: int list) = syn (ExprArrayLit (xs |> List.map iLit))
let conjE e = syn (ExprUnaryOp (OpConj, e))
let realE e = syn (ExprApp (v "real", [e]))
let imagE e = syn (ExprApp (v "imag", [e]))
let modE a b = syn (ExprBinOp (Elementwise, OpMod, a, b))
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
            blockE (stmts, Some (v "sx"))
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
            blockE (stmts, Some (v "sx"))
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
    mkFunc name [ ("xs", tyCplxArr n) ] (tyFloatArr n) (blockE (stmts, Some (v "xo")))

// ============================================================================
// power — |FFT(x)|² per bin (real)
// ============================================================================

let powerDecl (name: string) (n: int) (fftName: string) : FunctionDecl =
    let xk = idx "sx" (v "k")
    let stmts =
        [ sLet "sx" (syn (ExprApp (v fftName, [ v "x" ])))
          sLetMut "p" (zerosLit n)
          sFor "k" 0 n
            [ sAssign (idx "p" (v "k"))
                      (add (mul (realE xk) (realE xk)) (mul (imagE xk) (imagE xk))) ] ]
    mkFunc name [ ("x", tyFloatArr n) ] (tyFloatArr n) (blockE (stmts, Some (v "p")))

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
        [ for i in 1 .. k -> sLet (sprintf "s%d" i) (syn (ExprApp (v fftName, [ v (sprintf "x%d" i) ]))) ]
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
            blockE (stmts, Some (v "pp"))
        else
            // Rank-(k-1): reshape the flat work array through a nested
            // literal of runtime reads (element-type-agnostic, so the
            // MathDecls helper serves complex cells too).
            blockE (stmts @ [ sLet "po" (nestedFromFlatN "pp" outDims 0) ], Some (v "po"))
    let ps = [ for i in 1 .. k -> (sprintf "x%d" i, tyFloatArr n) ]
    mkFunc name ps (tyCplxTensor outDims) body

// ============================================================================
// fft2 / ifft2 — separable 2-D DFT over a rank-2 field (rows, then columns)
// ============================================================================
//
// Both passes work on flat row-major complex buffers of size r*c (MathDecls
// house style); the rank-2 result is the nested-literal-of-reads convention.
// Each axis independently takes the radix-2 path when pow2 (>= 2) and the
// naive table DFT otherwise — the same per-axis contract as the 1-D fft,
// and the same ulp discipline: twiddles baked, naive complex multiply, and
// loop structure textually parallel with rowPass2/colPass2 in spectra/Fft.fs.

/// Row pass: DFT along axis 1 of an r×c field into the flat complex work
/// array `sa`. `readIn i j` builds the complex read of input cell (i, j);
/// `mkTw` is fwdTwiddles or invTwiddles.
let private rowPass2 (r: int) (c: int) (mkTw: int -> int -> (float * float) list)
                     (readIn: Expr -> Expr -> Expr) : Stmt list =
    let flatIJ i j = add (mul i (iLit c)) j
    if isPow2 c && c >= 2 then
        let stages = fftStages c
        let perm = [ for j in 0 .. c - 1 -> bitrev stages j ]
        [ sLet "brc" (intArrLit perm)
          sLet "twc" (cplxArrLit (mkTw c (c / 2)))
          sLetMut "sa" (cplxZerosLit (r * c))
          // Gather copy-in through the per-row bit-reversal permutation.
          sFor "i" 0 r
            [ sFor "j" 0 c
                [ sAssign (idx "sa" (flatIJ (v "i") (v "j")))
                          (readIn (v "i") (idx "brc" (v "j"))) ] ] ]
        @ [ for st in 1 .. stages do
              let len = 1 <<< st
              let half = len / 2
              let tstr = c / len
              yield sFor "i" 0 r
                [ sFor "b" 0 (c / len)
                    [ sFor "t" 0 half
                        [ sLet "p" (add (mul (v "i") (iLit c)) (add (mul (v "b") (iLit len)) (v "t")))
                          sLet "q" (add (v "p") (iLit half))
                          sLet "tt" (mul (idx "twc" (mul (v "t") (iLit tstr))) (idx "sa" (v "q")))
                          sLet "p0" (idx "sa" (v "p"))
                          sAssign (idx "sa" (v "p")) (add (v "p0") (v "tt"))
                          sAssign (idx "sa" (v "q")) (sub (v "p0") (v "tt")) ] ] ] ]
    else
        [ sLet "twc" (cplxArrLit (mkTw c c))
          sLetMut "sa" (cplxZerosLit (r * c))
          sFor "i" 0 r
            [ sFor "k" 0 c
                [ sFor "j" 0 c
                    [ sLet "t" (modE (mul (v "k") (v "j")) (iLit c))
                      sAccum (idx "sa" (flatIJ (v "i") (v "k")))
                             (mul (readIn (v "i") (v "j")) (idx "twc" (v "t"))) ] ] ] ]

/// Column pass: DFT along axis 0, `sa` -> `sb` (both flat row-major r×c).
let private colPass2 (r: int) (c: int) (mkTw: int -> int -> (float * float) list) : Stmt list =
    let flatIJ i j = add (mul i (iLit c)) j
    if isPow2 r && r >= 2 then
        let stages = fftStages r
        let perm = [ for i in 0 .. r - 1 -> bitrev stages i ]
        [ sLet "brr" (intArrLit perm)
          sLet "twr" (cplxArrLit (mkTw r (r / 2)))
          sLetMut "sb" (cplxZerosLit (r * c))
          // Gather copy-in through the per-column bit-reversal permutation.
          sFor "i" 0 r
            [ sFor "j" 0 c
                [ sAssign (idx "sb" (flatIJ (v "i") (v "j")))
                          (idx "sa" (flatIJ (idx "brr" (v "i")) (v "j"))) ] ] ]
        @ [ for st in 1 .. stages do
              let len = 1 <<< st
              let half = len / 2
              let tstr = r / len
              yield sFor "j" 0 c
                [ sFor "b" 0 (r / len)
                    [ sFor "t" 0 half
                        [ sLet "p" (add (mul (add (mul (v "b") (iLit len)) (v "t")) (iLit c)) (v "j"))
                          sLet "q" (add (v "p") (iLit (half * c)))
                          sLet "tt" (mul (idx "twr" (mul (v "t") (iLit tstr))) (idx "sb" (v "q")))
                          sLet "p0" (idx "sb" (v "p"))
                          sAssign (idx "sb" (v "p")) (add (v "p0") (v "tt"))
                          sAssign (idx "sb" (v "q")) (sub (v "p0") (v "tt")) ] ] ] ]
    else
        [ sLet "twr" (cplxArrLit (mkTw r r))
          sLetMut "sb" (cplxZerosLit (r * c))
          sFor "k" 0 r
            [ sFor "j" 0 c
                [ sFor "i" 0 r
                    [ sLet "t" (modE (mul (v "k") (v "i")) (iLit r))
                      sAccum (idx "sb" (flatIJ (v "k") (v "j")))
                             (mul (idx "sa" (flatIJ (v "i") (v "j"))) (idx "twr" (v "t"))) ] ] ] ]

/// fft2 — unnormalized forward 2-D DFT of a real r×c field, complex output.
let fft2Decl (name: string) (r: int) (c: int) : FunctionDecl =
    let stmts =
        rowPass2 r c fwdTwiddles (fun i j -> cplx (idx2 "x" i j) (fLit 0.0))
        @ colPass2 r c fwdTwiddles
        @ [ sLet "po" (nestedFromFlatN "sb" [ r; c ] 0) ]
    mkFunc name [ ("x", tyFloatTensor [ r; c ]) ] (tyCplxTensor [ r; c ]) (blockE (stmts, Some (v "po")))

/// ifft2 — real inverse synthesis of an r×c complex spectrum (carries the
/// 1/(r·c), applied once at copy-out).
let ifft2Decl (name: string) (r: int) (c: int) : FunctionDecl =
    let flatIJ i j = add (mul i (iLit c)) j
    let stmts =
        rowPass2 r c invTwiddles (fun i j -> idx2 "xs" i j)
        @ colPass2 r c invTwiddles
        @ [ sLetMut "xo" (zerosLit (r * c))
            sFor "i" 0 r
              [ sFor "j" 0 c
                  [ sAssign (idx "xo" (flatIJ (v "i") (v "j")))
                            (divE (realE (idx "sb" (flatIJ (v "i") (v "j")))) (fLit (float (r * c)))) ] ]
            sLet "po" (nestedFromFlatN "xo" [ r; c ] 0) ]
    mkFunc name [ ("xs", tyCplxTensor [ r; c ]) ] (tyFloatTensor [ r; c ]) (blockE (stmts, Some (v "po")))
