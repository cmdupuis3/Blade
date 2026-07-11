namespace BladeML

open System
open System.Collections.Generic

/// SO(3) rotations and their action on irrep features.
///
/// The real Wigner D matrix for degree l is *fit from the spherical
/// harmonics themselves*: sample 2l+1 generic unit vectors v_i and solve
/// Y_l(R v_i) = D_l(R) Y_l(v_i). This makes D an independent oracle for the
/// CG machinery — tensor-product equivariance tests cross-validate the CG
/// tensors against the harmonics with no shared code path.
///
/// Parity note: everything here is proper rotations (SO(3)), where parity is
/// irrelevant. Under improper O(3) elements an irrep of parity p picks up an
/// extra Irreps.paritySign factor; that is a one-line extension, not needed
/// by the current tests.
module Rotations =

    open MathUtils

    let rotZ (t: float) : float[][] =
        [| [| cos t; -sin t; 0.0 |]
           [| sin t; cos t; 0.0 |]
           [| 0.0; 0.0; 1.0 |] |]

    let rotY (t: float) : float[][] =
        [| [| cos t; 0.0; sin t |]
           [| 0.0; 1.0; 0.0 |]
           [| -sin t; 0.0; cos t |] |]

    /// Haar-ish random rotation: Rz(alpha) Ry(beta) Rz(gamma), cos(beta) uniform.
    let randomRotation (rng: Random) : float[][] =
        let alpha = rng.NextDouble() * 2.0 * Math.PI
        let gamma = rng.NextDouble() * 2.0 * Math.PI
        let beta = acos (2.0 * rng.NextDouble() - 1.0)
        matMul (rotZ alpha) (matMul (rotY beta) (rotZ gamma))

    let randomUnitVector (rng: Random) : float[] =
        let z = 2.0 * rng.NextDouble() - 1.0
        let phi = rng.NextDouble() * 2.0 * Math.PI
        let s = sqrt (max 0.0 (1.0 - z * z))
        [| s * cos phi; s * sin phi; z |]

    let private evalAt (l: int) (v: float[]) : float[] =
        SphericalHarmonics.eval l v.[0] v.[1] v.[2]

    /// Real Wigner D matrix for degree l: Y_l(R v) = D_l(R) Y_l(v).
    /// Solved from generic samples with residual verification on fresh
    /// vectors; retries with new samples if the system is ill-conditioned.
    let wignerD (l: int) (r: float[][]) : float[][] =
        if l = 0 then [| [| 1.0 |] |]
        else
            let n = 2 * l + 1
            let mutable result = Unchecked.defaultof<float[][]>
            let mutable ok = false
            let mutable attempt = 0
            while not ok && attempt < 10 do
                let rng = Random(773 + 7919 * l + attempt)
                let pts = Array.init n (fun _ -> randomUnitVector rng)
                let m = pts |> Array.map (evalAt l)
                let mr = pts |> Array.map (fun v -> evalAt l (matVec r v))
                try
                    // Y(Rv_i) = D Y(v_i) for all i  <=>  Mr = M D^T
                    let dT = solve m mr
                    let d = transpose dT
                    let mutable resid = 0.0
                    for _ in 1 .. 4 do
                        let v = randomUnitVector rng
                        let lhs = evalAt l (matVec r v)
                        let rhs = matVec d (evalAt l v)
                        resid <- max resid (maxAbsDiff lhs rhs)
                    if resid < 1e-8 then
                        result <- d
                        ok <- true
                with _ -> ()
                attempt <- attempt + 1
            if not ok then failwithf "wignerD: could not fit D for l=%d" l
            result

    /// Apply the block-diagonal representation of R (per the spec's block
    /// structure) to a feature vector laid out as IrrepsIdx<spec>. Each
    /// multiplicity copy of a block transforms by the same D_l on its m index.
    let applyRep (spec: SpecEntry[]) (r: float[][]) (feat: float[]) : float[] =
        if feat.Length <> Irreps.totalDim spec then
            invalidArg "feat" "feature vector length does not match spec"
        let starts = IrrepsIdx.blockStarts spec
        let out = Array.zeroCreate feat.Length
        let cache = Dictionary<int, float[][]>()
        for b in 0 .. spec.Length - 1 do
            let e = spec.[b]
            let l = e.Ir.L
            let d = 2 * l + 1
            let dm =
                match cache.TryGetValue l with
                | true, v -> v
                | _ ->
                    let v = wignerD l r
                    cache.[l] <- v
                    v
            for mu in 0 .. e.Mult - 1 do
                let s = starts.[b] + mu * d
                for i in 0 .. d - 1 do
                    let mutable acc = 0.0
                    for j in 0 .. d - 1 do
                        acc <- acc + dm.[i].[j] * feat.[s + j]
                    out.[s + i] <- acc
        out
