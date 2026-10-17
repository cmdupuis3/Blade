/// Spectra-module decl builders: FFT and arity-polymorphic polyspectra synthesized as plain Blade source (the MathDecls mold).
///
/// Order polymorphism lives HERE: polyspecDecl is ONE order-generic F# function that emits a fixed-order Blade FunctionDecl
/// per requested polyspectrum order (the call-site arity).
///
/// House style (MathDecls): mut work arrays, for-in nests, no statement control flow. Complex values are native Complex128
/// scalars built with the complex(re, im) constructor call (typecheck intrinsic).
///
/// THE ULP CONTRACT (shared with spectra/Fft.fs, spectra/Polyspec.fs, the standalone .NET oracle): every trig value in
/// generated code is an F# System.Math.Cos/Sin literal baked at elaboration time (no runtime trig, so libm never enters the
/// picture), and the oracle performs the SAME arithmetic in the SAME order on the SAME tables. isPow2/fftStages/bitrev and
/// each kernel's loop structure MUST stay textually parallel with the oracle. Complex multiply is the naive component
/// formula on both sides (finite values: std::complex agrees bit-for-bit). No complex division anywhere (libstdc++'s
/// scaled division would diverge from any mirror).
module Blade.Spectra.Decls

open Blade.Ast
open Blade.Math.Decls

// Complex AST helpers (Float64 pair -> native Complex128)

/// complex(re, im) from arbitrary Float64 exprs -- the surface constructor call (typecheck intrinsic, infers Complex128).
/// Generated decls never shadow the name, so the intrinsic always resolves.
let cplx (re: Expr) (im: Expr) = syn (ExprApp (v "complex", [re; im]))
let cplxLit (re: float) (im: float) = cplx (fLit re) (fLit im)
/// A zero-initialized flat complex work array in O(1) text: the complex twin of MathDecls.zerosLit.
let cplxZerosLit (n: int) =
    let ps : LambdaParam list = [ { Name = "__zi"; Type = None; Default = None; NameSpan = noSpan } ]
    syn (ExprCompute (syn (ExprBinOp (Elementwise, OpApply,
                                      syn (ExprMethodFor [ syn (ExprRange [ TyIdx (iLit n) ]) ]),
                                      syn (ExprLambda (ps, None, cplxLit 0.0 0.0))))))
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

// FFT structure helpers -- MUST match spectra/Fft.fs (oracle) exactly
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

/// The bit reversal of the low `bits` bits of the Int expression `iE`, as INDEX ARITHMETIC in the generated code:
/// Sum_b ((i / 2^b) % 2) * 2^(bits-1-b) (Blade's Int `/` and `%` truncate; i >= 0). It replaces an n-entry baked
/// permutation literal -- O(bits) terms of text instead of O(n), the same integers, so no value can change. `bitrev`
/// above stays the oracle-side statement of the same map.
let bitrevE (bits: int) (iE: Expr) : Expr =
    [ for b in 0 .. bits - 1 ->
        mul (modE (divE iE (iLit (1 <<< b))) (iLit 2)) (iLit (1 <<< (bits - 1 - b))) ]
    |> List.reduce add

/// Forward twiddle table entries j = 0 .. m-1: e^(-2*pi*i*j/n).
let fwdTwiddles (n: int) (m: int) : (float * float) list =
    [ for j in 0 .. m - 1 ->
        (cos (-2.0 * System.Math.PI * float j / float n),
         sin (-2.0 * System.Math.PI * float j / float n)) ]

/// cos(2*pi*k/n) for k = 0 .. n/4 (n divisible by 4): the QUARTER-WAVE table every radix-2 twiddle is read from.
/// The k = n/4 entry is cos(pi/2) = 0 EXACTLY (System.Math.Cos gives 6.1e-17 there), so tw(0) = 1 and tw(n/4) = -/+i
/// come out exact rather than carrying a spurious 1e-16 component into every stage's j = 0 butterfly.
let quarterCos (n: int) : float list =
    [ for k in 0 .. n / 4 -> if k = n / 4 then 0.0 else cos (2.0 * System.Math.PI * float k / float n) ]

/// The half-period radix-2 twiddle table e^(sgn*2*pi*i*j/n), j = 0 .. n/2-1, BUILT IN THE GENERATED CODE from the
/// n/4+1-entry quarter-wave cosine table `qn` (bound once, baked like every other trig value) by the exact
/// symmetries cos(2pi j/n) = -cos(2pi (n/2-j)/n), sin(2pi j/n) = cos(2pi (n/4-j)/n) = cos(2pi (j-n/4)/n) -- reads and
/// negations only, so no runtime trig and no rounding. It replaces an n/2-entry complex literal (the bulk of a large
/// fft's emitted C++). `sgn` = -1.0 forward, +1.0 inverse. The oracle's `pow2Twiddles` performs the same selection.
/// Needs n >= 4 (n/4 >= 1); n = 2 keeps the literal table.
let pow2TwiddleStmts (tabName: string) (qName: string) (n: int) (sgn: float) : Stmt list =
    let q4 = n / 4
    let jv = v "j"
    let le = cmp OpLe jv (iLit q4)
    let neg e = syn (ExprUnaryOp (OpNeg, e))
    let re = ifE (le, idx qName jv, neg (idx qName (sub (iLit (n / 2)) jv)))
    let s0 = ifE (le, idx qName (sub (iLit q4) jv), idx qName (sub jv (iLit q4)))
    [ sLet qName (syn (ExprArrayLit (quarterCos n |> List.map fLit)))
      sLetMut tabName (cplxZerosLit (n / 2))
      sFor "j" 0 (n / 2) [ sAssign (idx tabName jv) (cplx re (if sgn < 0.0 then neg s0 else s0)) ] ]

/// Inverse-synthesis twiddle table entries j = 0 .. m-1: e^(+2*pi*i*j/n).
let invTwiddles (n: int) (m: int) : (float * float) list =
    [ for j in 0 .. m - 1 ->
        (cos (2.0 * System.Math.PI * float j / float n),
         sin (2.0 * System.Math.PI * float j / float n)) ]

// Bluestein (chirp-z) DFT -- the O(m log m) path for every n that is not a power of two (F6)
//
// X_k = Sum_j x_j e^(sgn*2*pi*i*jk/n) with jk = (j^2 + k^2 - (k-j)^2)/2 is the chirp-weighted CONVOLUTION
//   X_k = c_k * Sum_j (x_j c_j) * conj(c_(k-j)),   c_j = e^(sgn*pi*i*(j^2 mod 2n)/n),
// evaluated as a circular convolution of length m = the smallest power of two >= 2n-1 through two radix-2 FFTs of
// length m and a pointwise product with the baked kernel spectrum B = FFT_m(b) / m (b_j = conj(c_j) at j and at m-j;
// the 1/m is exact, m a power of two). It replaced the O(n^2) table DFT; its rounding is O(log m) ulps, checked
// against the exact DFT by spectra/063 (both lanes) -- the ulp contract with the oracle is unchanged in kind: the
// chirp and kernel tables are baked F# values, and the generated arithmetic is mirrored step for step by
// spectra/Fft.fs's `bluestein`.

/// The smallest power of two >= m.
let nextPow2 (m: int) =
    let mutable p = 1
    while p < m do
        p <- p * 2
    p

/// The chirp c_j = e^(sgn*pi*i*(j^2 mod 2n)/n), j = 0 .. n-1 (the angle reduced exactly in Int64 first, so a large
/// j^2 never costs precision).
let bluesteinChirp (n: int) (sgn: float) : (float * float) list =
    [ for j in 0 .. n - 1 ->
        let r = (int64 j * int64 j) % (2L * int64 n)
        let a = sgn * System.Math.PI * float r / float n
        (cos a, sin a) ]

/// The baked kernel spectrum: FFT_m of b (b_j = conj(c_j) for j < n, b_(m-j) = conj(c_j) for 0 < j < n, else 0),
/// scaled by 1/m. Computed HERE, at elaboration, by the same radix-2 butterfly the generated code runs (the same
/// quarter-wave twiddles, the same operation order); spectra/Fft.fs's `bluesteinKernel` is the oracle's copy.
let bluesteinKernel (n: int) (m: int) (sgn: float) : (float * float) list =
    let c = bluesteinChirp n sgn |> Array.ofList
    let b = Array.create m (0.0, 0.0)
    for j in 0 .. n - 1 do
        let (re, im) = c.[j]
        b.[j] <- (re, -im)
        if j > 0 then b.[m - j] <- (re, -im)
    let stages = fftStages m
    let q4 = m / 4
    let qc = quarterCos m |> Array.ofList
    let tw =
        [| for j in 0 .. m / 2 - 1 ->
             let re = if j <= q4 then qc.[j] else -qc.[m / 2 - j]
             let s0 = if j <= q4 then qc.[q4 - j] else qc.[j - q4]
             (re, -s0) |]
    let sx = [| for i in 0 .. m - 1 -> b.[bitrev stages i] |]
    let cmulP (ar, ai) (br, bi) = (ar * br - ai * bi, ar * bi + ai * br)
    for st in 1 .. stages do
        let len = 1 <<< st
        let half = len / 2
        let tstr = m / len
        for u in 0 .. m / len - 1 do
            for j in 0 .. half - 1 do
                let p = u * len + j
                let q = p + half
                let (tr, ti) = cmulP tw.[j * tstr] sx.[q]
                let (pr, pi) = sx.[p]
                sx.[p] <- (pr + tr, pi + ti)
                sx.[q] <- (pr - tr, pi - ti)
    let inv = 1.0 / float m
    [ for (re, im) in sx -> (re * inv, im * inv) ]

/// The radix-2 butterflies, in place on `buf` (`batches` contiguous runs of length m, already bit-reversed), with the
/// twiddle table `tw` (the same loop nest as the pow2 fft, one more loop over the batch). Stages statically unrolled.
let private batchedButterflies (buf: string) (tw: string) (m: int) (batches: int) : Stmt list =
    [ for st in 1 .. fftStages m do
        let len = 1 <<< st
        let half = len / 2
        let tstr = m / len
        yield sFor "g" 0 batches
          [ sFor "u" 0 (m / len)
              [ sFor "j" 0 half
                  [ sLet "p" (add (add (mul (v "g") (iLit m)) (mul (v "u") (iLit len))) (v "j"))
                    sLet "q" (add (v "p") (iLit half))
                    sLet "t" (mul (idx tw (mul (v "j") (iLit tstr))) (idx buf (v "q")))
                    sLet "p0" (idx buf (v "p"))
                    sAssign (idx buf (v "p")) (add (v "p0") (v "t"))
                    sAssign (idx buf (v "q")) (sub (v "p0") (v "t")) ] ] ] ]

/// A batched Bluestein DFT of length n (n >= 3, any n) with sign `sgn` (-1 forward, +1 inverse synthesis, both
/// unnormalized). `batches` independent vectors: `readIn g j` is element j of vector g (a complex Expr), and
/// `writeOut g k value` stores the k-th output of vector g. `pre` prefixes every binding so two passes in one body do
/// not collide. Work buffers are flat (`batches` runs of length m).
let bluesteinStmts (pre: string) (n: int) (batches: int) (sgn: float)
                   (readIn: Expr -> Expr -> Expr) (writeOut: Expr -> Expr -> Expr -> Stmt) : Stmt list =
    let m = nextPow2 (2 * n - 1)
    let stages = fftStages m
    let nm name = pre + name
    let at (buf: string) (g: Expr) (i: Expr) = idx (nm buf) (add (mul g (iLit m)) i)
    [ sLet (nm "c") (cplxArrLit (bluesteinChirp n sgn))
      sLet (nm "bh") (cplxArrLit (bluesteinKernel n m sgn)) ]
    @ pow2TwiddleStmts (nm "twf") (nm "qf") m (-1.0)
    @ pow2TwiddleStmts (nm "twi") (nm "qi") m 1.0
    @ [ // Chirp-weighted input, zero-padded to m.
        sLetMut (nm "z") (cplxZerosLit (batches * m))
        sFor "g" 0 batches
          [ sFor "j" 0 n
              [ sAssign (at "z" (v "g") (v "j")) (mul (readIn (v "g") (v "j")) (idx (nm "c") (v "j"))) ] ]
        // Forward FFT_m: gather through the bit-reversal permutation, then the butterflies.
        sLetMut (nm "a") (cplxZerosLit (batches * m))
        sFor "g" 0 batches
          [ sFor "i" 0 m
              [ sAssign (at "a" (v "g") (v "i")) (at "z" (v "g") (bitrevE stages (v "i"))) ] ] ]
    @ batchedButterflies (nm "a") (nm "twf") m batches
    @ [ // The convolution in frequency space (the kernel spectrum carries the 1/m).
        sFor "g" 0 batches
          [ sFor "k" 0 m
              [ sAssign (at "a" (v "g") (v "k")) (mul (at "a" (v "g") (v "k")) (idx (nm "bh") (v "k"))) ] ]
        // Inverse FFT_m (unnormalized: inverse twiddles), gather then butterflies.
        sLetMut (nm "d") (cplxZerosLit (batches * m))
        sFor "g" 0 batches
          [ sFor "i" 0 m
              [ sAssign (at "d" (v "g") (v "i")) (at "a" (v "g") (bitrevE stages (v "i"))) ] ] ]
    @ batchedButterflies (nm "d") (nm "twi") m batches
    @ [ // Chirp-weighted output, the first n of each run.
        sFor "g" 0 batches
          [ sFor "k" 0 n
              [ writeOut (v "g") (v "k") (mul (at "d" (v "g") (v "k")) (idx (nm "c") (v "k"))) ] ] ]

// fft -- unnormalized forward DFT of a real signal, complex output

/// Radix-2 iterative Cooley-Tukey for power-of-2 n; Bluestein (above) for every other n >= 3; n = 1 is the identity
/// (the one-term table DFT). Stages are statically
/// unrolled in F#: a runtime stage loop would need the multiplicative loop-carried scalar `len = len*2`, which Grad's
/// loop discipline rejects (spectra elaborates BEFORE Grad) -- do not "simplify" this back into a loop.
let fftDecl (name: string) (n: int) : FunctionDecl =
    let body =
        if isPow2 n && n >= 2 then
            let stages = fftStages n
            let twStmts =
                if n >= 4 then pow2TwiddleStmts "tw" "qc" n (-1.0)
                else [ sLet "tw" (cplxArrLit (fwdTwiddles n (n / 2))) ]
            let stmts =
                twStmts
                @ [ sLetMut "sx" (cplxZerosLit n)
                    // Gather copy-in through the bit-reversal permutation (gather, not scatter -- the oracle mirrors this).
                    sFor "i" 0 n
                      [ sAssign (idx "sx" (v "i")) (cplx (idx "x" (bitrevE stages (v "i"))) (fLit 0.0)) ] ]
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
        elif n >= 3 then
            let stmts =
                [ sLetMut "sx" (cplxZerosLit n) ]
                @ bluesteinStmts "bf_" n 1 (-1.0)
                    (fun _ j -> cplx (idx "x" j) (fLit 0.0))
                    (fun _ k value -> sAssign (idx "sx" k) value)
            blockE (stmts, Some (v "sx"))
        else
            // n = 1: the one-term table DFT, X(0) = x(0).
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

// ifft -- real inverse synthesis (carries the 1/n), any n
//
// Power-of-2 n (>= 2) runs the SAME radix-2 butterfly as `fft` on the complex spectrum with the inverse twiddles
// (e^(+2*pi*i*j/n)), then keeps the real part and scales by 1/n: O(n log n), where it used to take the naive O(n^2)
// synthesis at every n. Other n >= 3 take Bluestein with the inverse chirp (real part / n); n = 1 the one-term table.
let ifftDecl (name: string) (n: int) : FunctionDecl =
    let stmts =
        if isPow2 n && n >= 2 then
            let stages = fftStages n
            (if n >= 4 then pow2TwiddleStmts "tw" "qc" n 1.0
             else [ sLet "tw" (cplxArrLit (invTwiddles n (n / 2))) ])
            @ [ sLetMut "sx" (cplxZerosLit n)
                sFor "i" 0 n
                  [ sAssign (idx "sx" (v "i")) (idx "xs" (bitrevE stages (v "i"))) ] ]
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
            @ [ sLetMut "xo" (zerosLit n)
                sFor "i" 0 n
                  [ sAssign (idx "xo" (v "i")) (divE (realE (idx "sx" (v "i"))) (fLit (float n))) ] ]
        elif n >= 3 then
            [ sLetMut "xo" (zerosLit n) ]
            @ bluesteinStmts "bi_" n 1 1.0
                (fun _ j -> idx "xs" j)
                (fun _ k value -> sAssign (idx "xo" k) (divE (realE value) (fLit (float n))))
        else
            [ sLet "tw" (cplxArrLit (invTwiddles n n))
              sLetMut "xo" (zerosLit n)
              sFor "i" 0 n
                [ sFor "k" 0 n
                    [ sLet "t" (modE (mul (v "k") (v "i")) (iLit n))
                      sAccum (idx "xo" (v "i")) (realE (mul (idx "xs" (v "k")) (idx "tw" (v "t")))) ]
                  // Post-loop rescale: non-additive ARRAY-cell write (Grad-legal; only scalars carry the additive restriction).
                  sAssign (idx "xo" (v "i")) (divE (idx "xo" (v "i")) (fLit (float n))) ] ]
    mkFunc name [ ("xs", tyCplxArr n) ] (tyFloatArr n) (blockE (stmts, Some (v "xo")))

// power -- |FFT(x)|^2 per bin (real)
let powerDecl (name: string) (n: int) (fftName: string) : FunctionDecl =
    let xk = idx "sx" (v "k")
    let stmts =
        [ sLet "sx" (syn (ExprApp (v fftName, [ v "x" ])))
          sLetMut "p" (zerosLit n)
          sFor "k" 0 n
            [ sAssign (idx "p" (v "k"))
                      (add (mul (realE xk) (realE xk)) (mul (imagE xk) (imagE xk))) ] ]
    mkFunc name [ ("x", tyFloatArr n) ] (tyFloatArr n) (blockE (stmts, Some (v "p")))

// polyspec -- order-k cross-polyspectrum (order = call-site arity)

/// P(f_0..f_{k-2}) = X_1(f_0) *** X_{k-1}(f_{k-2}) * conj(X_k((Sum f) mod n)), a rank-(k-1) complex array. The order lives
/// entirely in this F# generator: k fft calls, a (k-1)-deep loop nest, and a statically-unrolled complex product chain.
let polyspecDecl (name: string) (n: int) (k: int) (fftName: string) : FunctionDecl =
    let outDims = List.replicate (k - 1) n
    let outSize = prodInts outDims
    let fvars = [ for j in 0 .. k - 2 -> $"f{j}" ]
    let strides = [ for j in 0 .. k - 2 -> prodInts (List.replicate (k - 2 - j) n) ]
    let flatIdx =
        List.map2 (fun fv st -> mul (v fv) (iLit st)) fvars strides
        |> List.reduce add
    let ffts =
        [ for i in 1 .. k -> sLet $"s{i}" (syn (ExprApp (v fftName, [ v $"x{i}" ]))) ]
    let chain =
        [ yield sLet "a1" (idx "s1" (v "f0"))
          for j in 2 .. k - 1 do
            yield sLet $"a{j}"
                       (mul (v $"a{j - 1}") (idx $"s{j}" (v $"f{j - 1}"))) ]
    let inner =
        sLet "sm" (modE (fvars |> List.map v |> List.reduce add) (iLit n))
        :: chain
        @ [ sAssign (idx "pp" flatIdx)
                    (mul (v $"a{k - 1}") (conjE (idx $"s{k}" (v "sm")))) ]
    let stmts =
        ffts
        @ [ sLetMut "pp" (cplxZerosLit outSize) ]
        @ loopNest fvars outDims inner
    let body =
        if k = 2 then
            // Rank-1 output: the flat mut IS the result.
            blockE (stmts, Some (v "pp"))
        else
            // Rank-(k-1): reshape the flat work array through a nested literal of runtime reads (element-type-agnostic,
            // so the MathDecls helper serves complex cells too).
            blockE (stmts @ [ sLet "po" (nestedFromFlatN "pp" outDims 0) ], Some (v "po"))
    let ps = [ for i in 1 .. k -> ($"x{i}", tyFloatArr n) ]
    mkFunc name ps (tyCplxTensor outDims) body

// fft2 / ifft2 -- separable 2-D DFT over a rank-2 field (rows, then columns). Both passes work on flat row-major complex
// buffers of size r*c (MathDecls house style); the rank-2 result is the nested-literal-of-reads convention. Each axis
// independently takes the radix-2 path when pow2 (>= 2), Bluestein when >= 3 otherwise (batched over the other axis),
// and the one-term table at extent 1 -- the same per-axis contract
// as the 1-D fft, and the same ulp discipline: twiddles baked, naive complex multiply, loop structure textually parallel
// with rowPass2/colPass2 in spectra/Fft.fs.

/// Row pass: DFT along axis 1 of an r x c field into the flat complex work array `sa`. `readIn i j` builds the complex
/// read of input cell (i, j); `mkTw` is fwdTwiddles or invTwiddles.
let private rowPass2 (r: int) (c: int) (mkTw: int -> int -> (float * float) list) (sgn: float)
                     (readIn: Expr -> Expr -> Expr) : Stmt list =
    let flatIJ i j = add (mul i (iLit c)) j
    if isPow2 c && c >= 2 then
        let stages = fftStages c
        (if c >= 4 then pow2TwiddleStmts "twc" "qcc" c sgn
         else [ sLet "twc" (cplxArrLit (mkTw c (c / 2))) ])
        @ [ sLetMut "sa" (cplxZerosLit (r * c))
            // Gather copy-in through the per-row bit-reversal permutation.
            sFor "i" 0 r
              [ sFor "j" 0 c
                  [ sAssign (idx "sa" (flatIJ (v "i") (v "j")))
                            (readIn (v "i") (bitrevE stages (v "j"))) ] ] ]
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
    elif c >= 3 then
        [ sLetMut "sa" (cplxZerosLit (r * c)) ]
        @ bluesteinStmts "br_" c r sgn
            (fun g j -> readIn g j)
            (fun g k value -> sAssign (idx "sa" (flatIJ g k)) value)
    else
        [ sLet "twc" (cplxArrLit (mkTw c c))
          sLetMut "sa" (cplxZerosLit (r * c))
          sFor "i" 0 r
            [ sFor "k" 0 c
                [ sFor "j" 0 c
                    [ sLet "t" (modE (mul (v "k") (v "j")) (iLit c))
                      sAccum (idx "sa" (flatIJ (v "i") (v "k")))
                             (mul (readIn (v "i") (v "j")) (idx "twc" (v "t"))) ] ] ] ]

/// Column pass: DFT along axis 0, `sa` -> `sb` (both flat row-major r x c).
let private colPass2 (r: int) (c: int) (mkTw: int -> int -> (float * float) list) (sgn: float) : Stmt list =
    let flatIJ i j = add (mul i (iLit c)) j
    if isPow2 r && r >= 2 then
        let stages = fftStages r
        (if r >= 4 then pow2TwiddleStmts "twr" "qcr" r sgn
         else [ sLet "twr" (cplxArrLit (mkTw r (r / 2))) ])
        @ [ sLetMut "sb" (cplxZerosLit (r * c))
            // Gather copy-in through the per-column bit-reversal permutation.
            sFor "i" 0 r
              [ sFor "j" 0 c
                  [ sAssign (idx "sb" (flatIJ (v "i") (v "j")))
                            (idx "sa" (flatIJ (bitrevE stages (v "i")) (v "j"))) ] ] ]
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
    elif r >= 3 then
        [ sLetMut "sb" (cplxZerosLit (r * c)) ]
        @ bluesteinStmts "bc_" r c sgn
            (fun g i -> idx "sa" (flatIJ i g))
            (fun g k value -> sAssign (idx "sb" (flatIJ k g)) value)
    else
        [ sLet "twr" (cplxArrLit (mkTw r r))
          sLetMut "sb" (cplxZerosLit (r * c))
          sFor "k" 0 r
            [ sFor "j" 0 c
                [ sFor "i" 0 r
                    [ sLet "t" (modE (mul (v "k") (v "i")) (iLit r))
                      sAccum (idx "sb" (flatIJ (v "k") (v "j")))
                             (mul (idx "sa" (flatIJ (v "i") (v "j"))) (idx "twr" (v "t"))) ] ] ] ]

/// fft2 -- unnormalized forward 2-D DFT of a real r x c field, complex output.
let fft2Decl (name: string) (r: int) (c: int) : FunctionDecl =
    let stmts =
        rowPass2 r c fwdTwiddles (-1.0) (fun i j -> cplx (idx2 "x" i j) (fLit 0.0))
        @ colPass2 r c fwdTwiddles (-1.0)
        @ [ sLet "po" (nestedFromFlatN "sb" [ r; c ] 0) ]
    mkFunc name [ ("x", tyFloatTensor [ r; c ]) ] (tyCplxTensor [ r; c ]) (blockE (stmts, Some (v "po")))

/// ifft2 -- real inverse synthesis of an r x c complex spectrum (carries the 1/(r*c), applied once at copy-out).
let ifft2Decl (name: string) (r: int) (c: int) : FunctionDecl =
    let flatIJ i j = add (mul i (iLit c)) j
    let stmts =
        rowPass2 r c invTwiddles 1.0 (fun i j -> idx2 "xs" i j)
        @ colPass2 r c invTwiddles 1.0
        @ [ sLetMut "xo" (zerosLit (r * c))
            sFor "i" 0 r
              [ sFor "j" 0 c
                  [ sAssign (idx "xo" (flatIJ (v "i") (v "j")))
                            (divE (realE (idx "sb" (flatIJ (v "i") (v "j")))) (fLit (float (r * c)))) ] ]
            sLet "po" (nestedFromFlatN "xo" [ r; c ] 0) ]
    mkFunc name [ ("xs", tyCplxTensor [ r; c ]) ] (tyFloatTensor [ r; c ]) (blockE (stmts, Some (v "po")))
