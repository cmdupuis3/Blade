/// Reference FFT/ifft/power for the spectra module.
///
/// MUST match spectra/compiler/SpectraDecls.fs -- the tables and the
/// arithmetic order are the ulp contract: both sides bake the SAME
/// System.Math.Cos/Sin table values (the generated code as float literals,
/// this oracle directly), and perform the SAME operations in the SAME order
/// (complex multiply = the naive component formula, which std::complex
/// matches bit-for-bit on finite values). Keep the two files textually
/// parallel; do not hand-optimize either side.
module BladeSpectra.Fft

/// Hand-rolled complex pair -- NOT System.Numerics.Complex, whose operator
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

/// The radix-2 (power-of-2 n >= 4) twiddles e^(sgn*2*pi*i*j/n), j < n/2, from
/// the quarter-wave table cos(2*pi*k/n), k <= n/4, by the exact symmetries --
/// mirrors SpectraDecls.pow2TwiddleStmts (reads and negations only). n = 2
/// keeps the direct table.
let pow2Twiddles (sgn: float) (n: int) : Cplx[] =
    if n < 4 then (if sgn < 0.0 then fwdTwiddles n (n / 2) else invTwiddles n (n / 2))
    else
        let q4 = n / 4
        let qc = [| for k in 0 .. q4 -> if k = q4 then 0.0 else cos (2.0 * System.Math.PI * float k / float n) |]
        [| for j in 0 .. n / 2 - 1 ->
             let re = if j <= q4 then qc.[j] else -qc.[n / 2 - j]
             let s0 = if j <= q4 then qc.[q4 - j] else qc.[j - q4]
             cplx re (if sgn < 0.0 then -s0 else s0) |]

// ---- Bluestein (chirp-z): every n >= 3 that is not a power of two ----------
// Mirrors SpectraDecls.nextPow2 / bluesteinChirp / bluesteinKernel /
// bluesteinStmts step for step (F6): chirp-weighted input zero-padded to m (the
// smallest power of two >= 2n-1), gather through the bit reversal, forward
// butterflies, pointwise product with the baked kernel spectrum (which carries
// the exact 1/m), gather, inverse butterflies, chirp-weighted output.

let nextPow2 (m: int) =
    let mutable p = 1
    while p < m do
        p <- p * 2
    p

let bluesteinChirp (n: int) (sgn: float) : Cplx[] =
    [| for j in 0 .. n - 1 ->
         let r = (int64 j * int64 j) % (2L * int64 n)
         let a = sgn * System.Math.PI * float r / float n
         cplx (cos a) (sin a) |]

/// The kernel spectrum FFT_m(b) / m -- the elaborator's `bluesteinKernel`
/// (it bakes these values; this is the same computation).
let bluesteinKernel (n: int) (m: int) (sgn: float) : Cplx[] =
    let c = bluesteinChirp n sgn
    let b = Array.create m (cplx 0.0 0.0)
    for j in 0 .. n - 1 do
        b.[j] <- cplx c.[j].Re (-c.[j].Im)
        if j > 0 then b.[m - j] <- cplx c.[j].Re (-c.[j].Im)
    let stages = fftStages m
    let tw = pow2Twiddles (-1.0) m
    let sx = [| for i in 0 .. m - 1 -> b.[bitrev stages i] |]
    for st in 1 .. stages do
        let len = 1 <<< st
        let half = len / 2
        let tstr = m / len
        for u in 0 .. m / len - 1 do
            for j in 0 .. half - 1 do
                let p = u * len + j
                let q = p + half
                let t = cmul tw.[j * tstr] sx.[q]
                let p0 = sx.[p]
                sx.[p] <- cadd p0 t
                sx.[q] <- csub p0 t
    let inv = 1.0 / float m
    [| for z in sx -> cplx (z.Re * inv) (z.Im * inv) |]

/// The batched radix-2 butterflies over `batches` runs of length m.
let private batchedButterflies (buf: Cplx[]) (tw: Cplx[]) (m: int) (batches: int) =
    for st in 1 .. fftStages m do
        let len = 1 <<< st
        let half = len / 2
        let tstr = m / len
        for g in 0 .. batches - 1 do
            for u in 0 .. m / len - 1 do
                for j in 0 .. half - 1 do
                    let p = g * m + u * len + j
                    let q = p + half
                    let t = cmul tw.[j * tstr] buf.[q]
                    let p0 = buf.[p]
                    buf.[p] <- cadd p0 t
                    buf.[q] <- csub p0 t

/// A batched Bluestein DFT of length n with sign `sgn`: `readIn g j` is
/// element j of vector g, `writeOut g k v` stores output k of vector g.
let bluestein (n: int) (batches: int) (sgn: float)
              (readIn: int -> int -> Cplx) (writeOut: int -> int -> Cplx -> unit) =
    let m = nextPow2 (2 * n - 1)
    let stages = fftStages m
    let c = bluesteinChirp n sgn
    let bh = bluesteinKernel n m sgn
    let twf = pow2Twiddles (-1.0) m
    let twi = pow2Twiddles 1.0 m
    let z = Array.create (batches * m) (cplx 0.0 0.0)
    for g in 0 .. batches - 1 do
        for j in 0 .. n - 1 do
            z.[g * m + j] <- cmul (readIn g j) c.[j]
    let a = Array.create (batches * m) (cplx 0.0 0.0)
    for g in 0 .. batches - 1 do
        for i in 0 .. m - 1 do
            a.[g * m + i] <- z.[g * m + bitrev stages i]
    batchedButterflies a twf m batches
    for g in 0 .. batches - 1 do
        for k in 0 .. m - 1 do
            a.[g * m + k] <- cmul a.[g * m + k] bh.[k]
    let d = Array.create (batches * m) (cplx 0.0 0.0)
    for g in 0 .. batches - 1 do
        for i in 0 .. m - 1 do
            d.[g * m + i] <- a.[g * m + bitrev stages i]
    batchedButterflies d twi m batches
    for g in 0 .. batches - 1 do
        for k in 0 .. n - 1 do
            writeOut g k (cmul d.[g * m + k] c.[k])

// ---- fft: unnormalized forward DFT of a real signal ------------------------

let fft (x: float[]) : Cplx[] =
    let n = x.Length
    if isPow2 n && n >= 2 then
        let stages = fftStages n
        let perm = [| for i in 0 .. n - 1 -> bitrev stages i |]
        let tw = pow2Twiddles (-1.0) n
        let sx = Array.zeroCreate<Cplx> n
        // Gather copy-in through the bit-reversal permutation (gather, not
        // scatter -- the generated code mirrors this direction).
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
    elif n >= 3 then
        let sx = Array.create n (cplx 0.0 0.0)
        bluestein n 1 (-1.0) (fun _ j -> cplx x.[j] 0.0) (fun _ k v -> sx.[k] <- v)
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
// Power-of-2 n: the fft butterfly on the complex spectrum with the inverse
// twiddles, real part, / n (mirrors SpectraDecls.ifftDecl). Other n >= 3:
// Bluestein with the inverse chirp, real part / n; n = 1 the one-term table.

let ifft (xs: Cplx[]) : float[] =
    let n = xs.Length
    if isPow2 n && n >= 2 then
        let stages = fftStages n
        let perm = [| for i in 0 .. n - 1 -> bitrev stages i |]
        let tw = pow2Twiddles 1.0 n
        let sx = Array.zeroCreate<Cplx> n
        for i in 0 .. n - 1 do
            sx.[i] <- xs.[perm.[i]]
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
        [| for i in 0 .. n - 1 -> sx.[i].Re / float n |]
    elif n >= 3 then
        let xo = Array.zeroCreate<float> n
        bluestein n 1 1.0 (fun _ j -> xs.[j]) (fun _ k v -> xo.[k] <- v.Re / float n)
        xo
    else
        let tw = invTwiddles n n
        let xo = Array.zeroCreate<float> n
        for i in 0 .. n - 1 do
            for k in 0 .. n - 1 do
                let t = (k * i) % n
                xo.[i] <- xo.[i] + (cmul xs.[k] tw.[t]).Re
            xo.[i] <- xo.[i] / float n
        xo

// ---- power: |FFT(x)|^2 per bin ----------------------------------------------

let power (x: float[]) : float[] =
    let sx = fft x
    [| for k in 0 .. x.Length - 1 ->
         sx.[k].Re * sx.[k].Re + sx.[k].Im * sx.[k].Im |]

// ---- fft2 / ifft2: separable 2-D DFT (rows, then columns) ------------------
// Flat row-major buffers of size r*c; each axis takes the radix-2 path when
// pow2 (>= 2), Bluestein at >= 3 otherwise, the one-term table at 1. MUST stay textually
// parallel with rowPass2/colPass2 in SpectraDecls.fs (the ulp contract).

/// Row pass: DFT along axis 1 into a fresh flat buffer. `readIn i j` reads
/// input cell (i, j); `mkTw` is fwdTwiddles or invTwiddles.
let private rowPass2 (r: int) (c: int) (mkTw: int -> int -> Cplx[]) (sgn: float) (readIn: int -> int -> Cplx) : Cplx[] =
    let sa = Array.create (r * c) (cplx 0.0 0.0)
    if isPow2 c && c >= 2 then
        let stages = fftStages c
        let perm = [| for j in 0 .. c - 1 -> bitrev stages j |]
        let twc = pow2Twiddles sgn c
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
    elif c >= 3 then
        bluestein c r sgn (fun g j -> readIn g j) (fun g k v -> sa.[g * c + k] <- v)
        sa
    else
        let twc = mkTw c c
        for i in 0 .. r - 1 do
            for k in 0 .. c - 1 do
                for j in 0 .. c - 1 do
                    let t = (k * j) % c
                    sa.[i * c + k] <- cadd sa.[i * c + k] (cmul (readIn i j) twc.[t])
        sa

/// Column pass: DFT along axis 0, `sa` -> fresh `sb` (flat row-major rxc).
let private colPass2 (r: int) (c: int) (mkTw: int -> int -> Cplx[]) (sgn: float) (sa: Cplx[]) : Cplx[] =
    let sb = Array.create (r * c) (cplx 0.0 0.0)
    if isPow2 r && r >= 2 then
        let stages = fftStages r
        let perm = [| for i in 0 .. r - 1 -> bitrev stages i |]
        let twr = pow2Twiddles sgn r
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
    elif r >= 3 then
        bluestein r c sgn (fun g i -> sa.[i * c + g]) (fun g k v -> sb.[k * c + g] <- v)
        sb
    else
        let twr = mkTw r r
        for k in 0 .. r - 1 do
            for j in 0 .. c - 1 do
                for i in 0 .. r - 1 do
                    let t = (k * i) % r
                    sb.[k * c + j] <- cadd sb.[k * c + j] (cmul sa.[i * c + j] twr.[t])
        sb

/// fft2: unnormalized forward 2-D DFT of a real rxc field (flat row-major).
let fft2 (r: int) (c: int) (x: float[]) : Cplx[] =
    rowPass2 r c fwdTwiddles (-1.0) (fun i j -> cplx x.[i * c + j] 0.0)
    |> colPass2 r c fwdTwiddles (-1.0)

/// ifft2: real inverse synthesis of an rxc complex spectrum (carries the
/// 1/(r*c), applied once at copy-out).
let ifft2 (r: int) (c: int) (xs: Cplx[]) : float[] =
    let sb =
        rowPass2 r c invTwiddles 1.0 (fun i j -> xs.[i * c + j])
        |> colPass2 r c invTwiddles 1.0
    [| for t in 0 .. r * c - 1 -> sb.[t].Re / float (r * c) |]
