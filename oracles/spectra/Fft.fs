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

// ---- fft2 / ifft2: separable 2-D DFT ----------------------------------------
// Mirrors SpectraDecls' batchDft / fft2Decl / ifft2Decl step for step (the ulp
// contract): bN complex DFTs of length n held SPLIT (re, im arrays) in
// batch-innermost layout (element k of vector b at k*bN + b); fft2 packs rows
// 2p and 2p+1 into one complex row transform and keeps the half spectrum,
// ifft2 transforms the Hermitian part H(k,l) = (X(k,l) + conj X(-k,-l))/2 on
// its c/2+1 kept columns and synthesizes two real rows per complex row
// transform. Each axis: radix-2 when pow2, Bluestein at >= 3 otherwise, the
// identity at 1.

/// bN DFTs of length n, sign `sgn`; `load k b` is element k of vector b.
let rec private batchDft (n: int) (bN: int) (sgn: float) (load: int -> int -> float * float) : float[] * float[] =
    if n = 1 then
        let re = Array.zeroCreate<float> bN
        let im = Array.zeroCreate<float> bN
        for b in 0 .. bN - 1 do
            let (lr, li) = load 0 b
            re.[b] <- lr
            im.[b] <- li
        re, im
    elif isPow2 n then
        let stages = fftStages n
        let tw = if n >= 4 then pow2Twiddles sgn n else [||]
        let re = Array.zeroCreate<float> (n * bN)
        let im = Array.zeroCreate<float> (n * bN)
        for k in 0 .. n - 1 do
            let kr = bitrev stages k
            for b in 0 .. bN - 1 do
                let (lr, li) = load kr b
                re.[k * bN + b] <- lr
                im.[k * bN + b] <- li
        // One butterfly; `w` None = the unit twiddle (its multiply skipped).
        let bfly (p: int) (halfB: int) (w: Cplx option) =
            let q = p + halfB
            let qr = re.[q]
            let qi = im.[q]
            let tr, ti =
                match w with
                | None -> qr, qi
                | Some w -> w.Re * qr - w.Im * qi, w.Re * qi + w.Im * qr
            let pr = re.[p]
            let pi = im.[p]
            re.[p] <- pr + tr
            im.[p] <- pi + ti
            re.[q] <- pr - tr
            im.[q] <- pi - ti
        for st in 1 .. stages do
            let len = 1 <<< st
            let half = len / 2
            let tstr = n / len
            for u in 0 .. n / len - 1 do
                for b in 0 .. bN - 1 do
                    bfly (u * (len * bN) + b) (half * bN) None
                for t in 1 .. half - 1 do
                    let w = tw.[t * tstr]
                    for b in 0 .. bN - 1 do
                        bfly ((u * len + t) * bN + b) (half * bN) (Some w)
        re, im
    else
        let m = nextPow2 (2 * n - 1)
        let c = bluesteinChirp n sgn
        let bh = bluesteinKernel n m sgn
        let zr = Array.zeroCreate<float> (n * bN)
        let zi = Array.zeroCreate<float> (n * bN)
        for j in 0 .. n - 1 do
            let c0 = c.[j].Re
            let c1 = c.[j].Im
            for b in 0 .. bN - 1 do
                let (x0, x1) = load j b
                zr.[j * bN + b] <- x0 * c0 - x1 * c1
                zi.[j * bN + b] <- x0 * c1 + x1 * c0
        let ar, ai = batchDft m bN (-1.0) (fun k b -> if k < n then zr.[k * bN + b], zi.[k * bN + b] else 0.0, 0.0)
        for k in 0 .. m - 1 do
            let h0 = bh.[k].Re
            let h1 = bh.[k].Im
            for b in 0 .. bN - 1 do
                let a0 = ar.[k * bN + b]
                let a1 = ai.[k * bN + b]
                ar.[k * bN + b] <- a0 * h0 - a1 * h1
                ai.[k * bN + b] <- a0 * h1 + a1 * h0
        let dr, di = batchDft m bN 1.0 (fun k b -> ar.[k * bN + b], ai.[k * bN + b])
        let re = Array.zeroCreate<float> (n * bN)
        let im = Array.zeroCreate<float> (n * bN)
        for k in 0 .. n - 1 do
            let c0 = c.[k].Re
            let c1 = c.[k].Im
            for b in 0 .. bN - 1 do
                let d0 = dr.[k * bN + b]
                let d1 = di.[k * bN + b]
                re.[k * bN + b] <- d0 * c0 - d1 * c1
                im.[k * bN + b] <- d0 * c1 + d1 * c0
        re, im

/// fft2: unnormalized forward 2-D DFT of a real rxc field (flat row-major).
let fft2 (r: int) (c: int) (x: float[]) : Cplx[] =
    let w = c / 2 + 1
    let pN = (r + 1) / 2
    let zr, zi = batchDft c pN (-1.0) (fun k b ->
        let i1 = 2 * b + 1
        x.[(2 * b) * c + k], (if i1 < r then x.[i1 * c + k] else 0.0))
    let hr = Array.zeroCreate<float> (r * w)
    let hi = Array.zeroCreate<float> (r * w)
    for i in 0 .. r - 1 do
        let pb = i / 2
        let ev = i % 2 = 0
        for k in 0 .. w - 1 do
            let km = if k = 0 then 0 else c - k
            let z0 = zr.[k * pN + pb]
            let z1 = zi.[k * pN + pb]
            let m0 = zr.[km * pN + pb]
            let m1 = zi.[km * pN + pb]
            hr.[i * w + k] <- (if ev then 0.5 * (z0 + m0) else 0.5 * (z1 + m1))
            hi.[i * w + k] <- (if ev then 0.5 * (z1 - m1) else 0.5 * (m0 - z0))
    let cr, ci = batchDft r w (-1.0) (fun i j -> hr.[i * w + j], hi.[i * w + j])
    [| for i in 0 .. r - 1 do
         let ii = if i = 0 then 0 else r - i
         for l in 0 .. c - 1 ->
             if l < w then cplx cr.[i * w + l] ci.[i * w + l]
             else cplx cr.[ii * w + (c - l)] (-ci.[ii * w + (c - l)]) |]

/// ifft2: real inverse synthesis of an rxc complex spectrum (carries the
/// 1/(r*c), applied once at copy-out as the product with its reciprocal).
let ifft2 (r: int) (c: int) (xs: Cplx[]) : float[] =
    let w = c / 2 + 1
    let pN = (r + 1) / 2
    let mirror n e = if e = 0 then 0 else n - e
    let yr, yi = batchDft r w 1.0 (fun i j ->
        let a = xs.[i * c + j]
        let b = xs.[mirror r i * c + mirror c j]
        0.5 * (a.Re + b.Re), 0.5 * (a.Im - b.Im))
    let zr, zi = batchDft c pN 1.0 (fun k b ->
        let i0 = 2 * b
        let i1 = i0 + 1
        let y1r kk = if i1 < r then yr.[i1 * w + kk] else 0.0
        let y1i kk = if i1 < r then yi.[i1 * w + kk] else 0.0
        if k < w then yr.[i0 * w + k] - y1i k, yi.[i0 * w + k] + y1r k
        else
            let kk = c - k
            yr.[i0 * w + kk] + y1i kk, y1r kk - yi.[i0 * w + kk])
    let scale = 1.0 / float (r * c)
    [| for i in 0 .. r - 1 do
         let pb = i / 2
         for j in 0 .. c - 1 ->
             (if i % 2 = 0 then zr.[j * pN + pb] else zi.[j * pN + pb]) * scale |]
