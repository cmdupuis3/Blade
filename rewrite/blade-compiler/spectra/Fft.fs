/// Reference FFT/ifft/power for the spectra module.
///
/// MUST match spectra/compiler/SpectraDecls.fs — the tables and the
/// arithmetic order are the ulp contract: both sides bake the SAME
/// System.Math.Cos/Sin table values (the generated code as float literals,
/// this oracle directly), and perform the SAME operations in the SAME order
/// (complex multiply = the naive component formula, which std::complex
/// matches bit-for-bit on finite values). Keep the two files textually
/// parallel; do not hand-optimize either side.
module BladeSpectra.Fft

/// Hand-rolled complex pair — NOT System.Numerics.Complex, whose operator
/// implementations we do not control. Naive formulas only.
type Cplx = { Re: float; Im: float }

let cplx re im = { Re = re; Im = im }
let cadd a b = { Re = a.Re + b.Re; Im = a.Im + b.Im }
let csub a b = { Re = a.Re - b.Re; Im = a.Im - b.Im }
let cmul a b = { Re = a.Re * b.Re - a.Im * b.Im; Im = a.Re * b.Im + a.Im * b.Re }
let cconj a = { Re = a.Re; Im = -a.Im }

// ---- FFT structure helpers (mirror SpectraDecls exactly) -------------------

let isPow2 (n: int) = n > 0 && (n &&& (n - 1)) = 0

let fftStages (n: int) =
    let mutable s = 0
    let mutable l = 1
    while l < n do
        l <- l * 2
        s <- s + 1
    s

let bitrev (bits: int) (x: int) =
    let mutable r = 0
    let mutable xx = x
    for _ in 1 .. bits do
        r <- (r <<< 1) ||| (xx &&& 1)
        xx <- xx >>> 1
    r

let fwdTwiddles (n: int) (m: int) : Cplx[] =
    [| for j in 0 .. m - 1 ->
         cplx (cos (-2.0 * System.Math.PI * float j / float n))
              (sin (-2.0 * System.Math.PI * float j / float n)) |]

let invTwiddles (n: int) (m: int) : Cplx[] =
    [| for j in 0 .. m - 1 ->
         cplx (cos (2.0 * System.Math.PI * float j / float n))
              (sin (2.0 * System.Math.PI * float j / float n)) |]

// ---- fft: unnormalized forward DFT of a real signal ------------------------

let fft (x: float[]) : Cplx[] =
    let n = x.Length
    if isPow2 n && n >= 2 then
        let stages = fftStages n
        let perm = [| for i in 0 .. n - 1 -> bitrev stages i |]
        let tw = fwdTwiddles n (n / 2)
        let sx = Array.zeroCreate<Cplx> n
        // Gather copy-in through the bit-reversal permutation (gather, not
        // scatter — the generated code mirrors this direction).
        for i in 0 .. n - 1 do
            sx.[i] <- cplx x.[perm.[i]] 0.0
        for st in 1 .. stages do
            let len = 1 <<< st
            let half = len / 2
            let tstr = n / len
            for b in 0 .. n / len - 1 do
                for j in 0 .. half - 1 do
                    let p = b * len + j
                    let q = p + half
                    let t = cmul tw.[j * tstr] sx.[q]
                    let p0 = sx.[p]
                    sx.[p] <- cadd p0 t
                    sx.[q] <- csub p0 t
        sx
    else
        let tw = fwdTwiddles n n
        let sx = Array.create n (cplx 0.0 0.0)
        for k in 0 .. n - 1 do
            for i in 0 .. n - 1 do
                let t = (k * i) % n
                sx.[k] <- cadd sx.[k] (cmul (cplx x.[i] 0.0) tw.[t])
        sx

// ---- ifft: real inverse synthesis (carries the 1/n), any n -----------------

let ifft (xs: Cplx[]) : float[] =
    let n = xs.Length
    let tw = invTwiddles n n
    let xo = Array.zeroCreate<float> n
    for i in 0 .. n - 1 do
        for k in 0 .. n - 1 do
            let t = (k * i) % n
            xo.[i] <- xo.[i] + (cmul xs.[k] tw.[t]).Re
        xo.[i] <- xo.[i] / float n
    xo

// ---- power: |FFT(x)|² per bin ----------------------------------------------

let power (x: float[]) : float[] =
    let sx = fft x
    [| for k in 0 .. x.Length - 1 ->
         sx.[k].Re * sx.[k].Re + sx.[k].Im * sx.[k].Im |]

// ---- fft2 / ifft2: separable 2-D DFT (rows, then columns) ------------------
// Flat row-major buffers of size r*c; each axis takes the radix-2 path when
// pow2 (>= 2) and the naive table DFT otherwise. MUST stay textually
// parallel with rowPass2/colPass2 in SpectraDecls.fs (the ulp contract).

/// Row pass: DFT along axis 1 into a fresh flat buffer. `readIn i j` reads
/// input cell (i, j); `mkTw` is fwdTwiddles or invTwiddles.
let private rowPass2 (r: int) (c: int) (mkTw: int -> int -> Cplx[]) (readIn: int -> int -> Cplx) : Cplx[] =
    let sa = Array.create (r * c) (cplx 0.0 0.0)
    if isPow2 c && c >= 2 then
        let stages = fftStages c
        let perm = [| for j in 0 .. c - 1 -> bitrev stages j |]
        let twc = mkTw c (c / 2)
        // Gather copy-in through the per-row bit-reversal permutation.
        for i in 0 .. r - 1 do
            for j in 0 .. c - 1 do
                sa.[i * c + j] <- readIn i perm.[j]
        for st in 1 .. stages do
            let len = 1 <<< st
            let half = len / 2
            let tstr = c / len
            for i in 0 .. r - 1 do
                for b in 0 .. c / len - 1 do
                    for t in 0 .. half - 1 do
                        let p = i * c + (b * len + t)
                        let q = p + half
                        let tt = cmul twc.[t * tstr] sa.[q]
                        let p0 = sa.[p]
                        sa.[p] <- cadd p0 tt
                        sa.[q] <- csub p0 tt
        sa
    else
        let twc = mkTw c c
        for i in 0 .. r - 1 do
            for k in 0 .. c - 1 do
                for j in 0 .. c - 1 do
                    let t = (k * j) % c
                    sa.[i * c + k] <- cadd sa.[i * c + k] (cmul (readIn i j) twc.[t])
        sa

/// Column pass: DFT along axis 0, `sa` -> fresh `sb` (flat row-major r×c).
let private colPass2 (r: int) (c: int) (mkTw: int -> int -> Cplx[]) (sa: Cplx[]) : Cplx[] =
    let sb = Array.create (r * c) (cplx 0.0 0.0)
    if isPow2 r && r >= 2 then
        let stages = fftStages r
        let perm = [| for i in 0 .. r - 1 -> bitrev stages i |]
        let twr = mkTw r (r / 2)
        // Gather copy-in through the per-column bit-reversal permutation.
        for i in 0 .. r - 1 do
            for j in 0 .. c - 1 do
                sb.[i * c + j] <- sa.[perm.[i] * c + j]
        for st in 1 .. stages do
            let len = 1 <<< st
            let half = len / 2
            let tstr = r / len
            for j in 0 .. c - 1 do
                for b in 0 .. r / len - 1 do
                    for t in 0 .. half - 1 do
                        let p = (b * len + t) * c + j
                        let q = p + half * c
                        let tt = cmul twr.[t * tstr] sb.[q]
                        let p0 = sb.[p]
                        sb.[p] <- cadd p0 tt
                        sb.[q] <- csub p0 tt
        sb
    else
        let twr = mkTw r r
        for k in 0 .. r - 1 do
            for j in 0 .. c - 1 do
                for i in 0 .. r - 1 do
                    let t = (k * i) % r
                    sb.[k * c + j] <- cadd sb.[k * c + j] (cmul sa.[i * c + j] twr.[t])
        sb

/// fft2: unnormalized forward 2-D DFT of a real r×c field (flat row-major).
let fft2 (r: int) (c: int) (x: float[]) : Cplx[] =
    rowPass2 r c fwdTwiddles (fun i j -> cplx x.[i * c + j] 0.0)
    |> colPass2 r c fwdTwiddles

/// ifft2: real inverse synthesis of an r×c complex spectrum (carries the
/// 1/(r·c), applied once at copy-out).
let ifft2 (r: int) (c: int) (xs: Cplx[]) : float[] =
    let sb =
        rowPass2 r c invTwiddles (fun i j -> xs.[i * c + j])
        |> colPass2 r c invTwiddles
    [| for t in 0 .. r * c - 1 -> sb.[t].Re / float (r * c) |]
