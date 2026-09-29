namespace BladeSgs

open System.Numerics

/// Exact rational arithmetic over the binary64 inputs, for the stress pins:
/// the exact SGS stress is a CENTRAL comoment, and computing it exactly (exact
/// tile means, exact centered products, one rounding at the end) keeps this
/// oracle independent of the formula the compiler emits -- it neither mirrors
/// the raw E[ab] - E[a]E[b] (which cancels when the mean dwarfs the
/// fluctuation) nor the two-pass form SgsDecls now emits. (Same helper as
/// oracles/ppl/Exact.fs; the two oracle projects share no code.)
module Exact =

    /// num / den, den > 0, not necessarily reduced.
    [<Struct>]
    type Q = { Num: BigInteger; Den: BigInteger }

    let private norm (n: BigInteger) (d: BigInteger) : Q =
        let g = BigInteger.GreatestCommonDivisor(n, d)
        let g = if g.IsZero then BigInteger.One else g
        let n, d = n / g, d / g
        if d.Sign < 0 then { Num = -n; Den = -d } else { Num = n; Den = d }

    let zero = { Num = BigInteger.Zero; Den = BigInteger.One }
    let one = { Num = BigInteger.One; Den = BigInteger.One }

    /// The exact value of a finite double.
    let ofFloat (x: float) : Q =
        if System.Double.IsNaN x || System.Double.IsInfinity x then
            failwith "Exact.ofFloat: non-finite input"
        let bits = System.BitConverter.DoubleToInt64Bits x
        let neg = bits < 0L
        let exp = int ((bits >>> 52) &&& 0x7FFL)
        let frac = bits &&& 0xFFFFFFFFFFFFFL
        let mant, e2 =
            if exp = 0 then BigInteger frac, -1074
            else BigInteger (frac ||| (1L <<< 52)), exp - 1075
        let m = if neg then -mant else mant
        if e2 >= 0 then { Num = m * BigInteger.Pow(BigInteger 2, e2); Den = BigInteger.One }
        else norm m (BigInteger.Pow(BigInteger 2, -e2))

    let ofInt (k: int) : Q = { Num = BigInteger k; Den = BigInteger.One }
    let add (a: Q) (b: Q) = norm (a.Num * b.Den + b.Num * a.Den) (a.Den * b.Den)
    let sub (a: Q) (b: Q) = norm (a.Num * b.Den - b.Num * a.Den) (a.Den * b.Den)
    let mul (a: Q) (b: Q) = norm (a.Num * b.Num) (a.Den * b.Den)
    let divInt (a: Q) (k: int) = norm a.Num (a.Den * BigInteger k)
    let scale (c: float) (a: Q) = mul (ofFloat c) a

    /// Round to the nearest double, ties to even: a 55-bit truncated quotient
    /// (guard + round bits) with the remainder folded into a sticky bit, then
    /// one explicit rounding step. Normal-range results only (the oracle
    /// inputs are moderate); ScaleB is exact on the rounded 53/54-bit integer.
    let toFloat (q: Q) : float =
        if q.Num.IsZero then 0.0
        else
            let neg = q.Num.Sign < 0
            let n = BigInteger.Abs q.Num
            let bitLen (b: BigInteger) = int (b.GetBitLength())
            // choose shift so the quotient has exactly 55 or 56 bits
            let shift = bitLen q.Den - bitLen n + 55
            let num, den = (if shift >= 0 then (n <<< shift, q.Den) else (n, q.Den <<< -shift))
            let mutable quot = BigInteger.Divide(num, den)
            let rem = num - quot * den
            // normalise to exactly 55 bits: 53 kept + guard + round
            let mutable sh = shift
            let mutable sticky = not rem.IsZero
            while bitLen quot > 55 do
                if not (quot &&& BigInteger.One).IsZero then sticky <- true
                quot <- quot >>> 1
                sh <- sh - 1
            let low2 = int (quot &&& BigInteger 3)
            let mutable mant = quot >>> 2
            let half = low2 &&& 2 <> 0
            let rest = (low2 &&& 1 <> 0) || sticky
            if half && (rest || not (mant &&& BigInteger.One).IsZero) then mant <- mant + BigInteger.One
            let r = System.Math.ScaleB(float mant, -(sh - 2))
            if neg then -r else r

    /// Exact population central comoment (1/N) Sum_t (a_t - mean a)(b_t - mean b), rounded once.
    let centralComoment (a: float[]) (b: float[]) : float =
        let n = a.Length
        let mean (xs: float[]) = divInt (xs |> Array.fold (fun acc x -> add acc (ofFloat x)) zero) n
        let ma, mb = mean a, mean b
        let mutable acc = zero
        for t in 0 .. n - 1 do
            acc <- add acc (mul (sub (ofFloat a.[t]) ma) (sub (ofFloat b.[t]) mb))
        toFloat (divInt acc n)
