/// Reference order-k cross-polyspectrum for the spectra module.
///
/// MUST match spectra/compiler/SpectraDecls.fs polyspecDecl: same row-major
/// (k-1)-deep frequency nest, same left-fold product chain, same explicit
/// conjugate multiply — the arithmetic order is the ulp contract.
module BladeSpectra.Polyspec

open BladeSpectra.Fft

/// P(f_0..f_{k-2}) = X_1(f_0) ··· X_{k-1}(f_{k-2}) · conj(X_k((Σf) mod n)),
/// returned FLAT row-major (Blade prints rank-2+ complex arrays flat).
let polyspec (xs: float[] list) : Cplx[] =
    let k = xs.Length
    let n = xs.Head.Length
    let ss = xs |> List.map fft
    let outSize = pown n (k - 1)
    let pp = Array.create outSize (cplx 0.0 0.0)
    // (k-1)-digit row-major odometer over the frequency indices.
    let fs = Array.zeroCreate<int> (k - 1)
    for flat in 0 .. outSize - 1 do
        let mutable rem = flat
        for j in (k - 2) .. -1 .. 0 do
            fs.[j] <- rem % n
            rem <- rem / n
        let sm = (Array.sum fs) % n
        let mutable a = ss.[0].[fs.[0]]
        for j in 2 .. k - 1 do
            a <- cmul a ss.[j - 1].[fs.[j - 1]]
        pp.[flat] <- cmul a (cconj ss.[k - 1].[sm])
    pp
