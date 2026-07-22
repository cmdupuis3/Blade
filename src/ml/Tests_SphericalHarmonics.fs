namespace BladeML

/// Spherical harmonics against the ml-spec's explicit polynomial tables,
/// closed-form identities, and the fitted Wigner D matrices.
module Tests_SphericalHarmonics =

    open System
    open TestHarness
    open MathUtils

    /// The ml-spec section 6.2 explicit table, transcribed literally.
    let private specY (x: float) (y: float) (z: float) : float[][] =
        let r2 = x * x + y * y + z * z
        [| [| 0.28209479 |]
           [| 0.48860251 * y; 0.48860251 * z; 0.48860251 * x |]
           [| 1.09254843 * x * y
              1.09254843 * y * z
              0.31539157 * (3.0 * z * z - r2)
              1.09254843 * x * z
              0.54627421 * (x * x - y * y) |] |]

    let run () =
        section "spherical harmonics: ml-spec explicit table (L <= 2)"

        let rng = Random(7)
        for i in 1 .. 3 do
            // Deliberately non-unit vectors: these are solid harmonics.
            let v = [| rng.NextDouble() * 2.0 - 1.0
                       rng.NextDouble() * 2.0 - 1.0
                       rng.NextDouble() * 2.0 - 1.0 |]
            let got = SphericalHarmonics.evalUpTo 2 v.[0] v.[1] v.[2]
            let want = specY v.[0] v.[1] v.[2]
            for l in 0 .. 2 do
                // Spec constants have 8 digits; tolerance reflects that.
                checkArrayClose (sprintf "Y_%d matches spec table, sample %d" l i) 5e-7 want.[l] got.[l]

        section "spherical harmonics: closed-form identities"

        // Homogeneity: Y_l(s v) = s^l Y_l(v) — these are solid harmonics.
        let v0 = Rotations.randomUnitVector rng
        let s = 1.7
        for l in 0 .. 4 do
            let a = SphericalHarmonics.eval l (s * v0.[0]) (s * v0.[1]) (s * v0.[2])
            let b = SphericalHarmonics.eval l v0.[0] v0.[1] v0.[2]
                    |> Array.map (fun t -> t * s ** float l)
            checkArrayClose (sprintf "homogeneity degree %d" l) 1e-10 b a

        // Unsold / addition theorem: sum_m Y_lm(v)^2 = (2l+1)/(4pi) r^(2l).
        // Pins the normalization exactly, for every degree.
        for l in 0 .. 4 do
            let v = [| 0.3; -0.8; 0.6 |]
            let r2 = v.[0] * v.[0] + v.[1] * v.[1] + v.[2] * v.[2]
            let y = SphericalHarmonics.eval l v.[0] v.[1] v.[2]
            let sumSq = y |> Array.sumBy (fun t -> t * t)
            let expect = float (2 * l + 1) / (4.0 * Math.PI) * r2 ** float l
            checkClose (sprintf "addition theorem degree %d" l) 1e-10 expect sumSq

        // Rotation invariance of the per-degree norm (independent of Wigner D).
        for l in 0 .. 4 do
            let r = Rotations.randomRotation rng
            let v = Rotations.randomUnitVector rng
            let rv = matVec r v
            let n1 = SphericalHarmonics.eval l v.[0] v.[1] v.[2] |> Array.sumBy (fun t -> t * t)
            let n2 = SphericalHarmonics.eval l rv.[0] rv.[1] rv.[2] |> Array.sumBy (fun t -> t * t)
            checkClose (sprintf "norm invariance degree %d" l) 1e-10 n1 n2

        // Y_to layout = concatenation of per-degree blocks = IrrepsIdx<sh_spec>.
        let vv = Rotations.randomUnitVector rng
        let cat = SphericalHarmonics.evalUpTo 3 vv.[0] vv.[1] vv.[2] |> Array.concat
        checkArrayClose "yTo = concatenated blocks" 0.0 cat (SphericalHarmonics.yTo 3 vv.[0] vv.[1] vv.[2])
        check "yTo extent = IrrepsIdx extent of shSpec"
            ((SphericalHarmonics.yTo 3 0.1 0.2 0.3).Length = IrrepsIdx.extent (Irreps.shSpec 3))

        section "wigner D: fitted representation matrices"

        let rngD = Random(99)
        for l in 1 .. 4 do
            let r = Rotations.randomRotation rngD
            let d = Rotations.wignerD l r

            // Orthogonality: D D^T = I.
            let dT = transpose d
            let ident = matMul d dT
            let mutable dev = 0.0
            for i in 0 .. 2 * l do
                for j in 0 .. 2 * l do
                    let expect = if i = j then 1.0 else 0.0
                    dev <- max dev (abs (ident.[i].[j] - expect))
            check (sprintf "D_%d orthogonal (dev %.2g)" l dev) (dev < 1e-8)

            // Defining property on fresh vectors.
            let mutable worst = 0.0
            for _ in 1 .. 5 do
                let v = Rotations.randomUnitVector rngD
                let rv = matVec r v
                let lhs = SphericalHarmonics.eval l rv.[0] rv.[1] rv.[2]
                let rhs = matVec d (SphericalHarmonics.eval l v.[0] v.[1] v.[2])
                worst <- max worst (maxAbsDiff lhs rhs)
            check (sprintf "Y_%d(Rv) = D_%d Y_%d(v) on fresh vectors (dev %.2g)" l l l worst) (worst < 1e-8)

            // Homomorphism: D(R1 R2) = D(R1) D(R2).
            let r2m = Rotations.randomRotation rngD
            let d12 = Rotations.wignerD l (matMul r r2m)
            let d1d2 = matMul d (Rotations.wignerD l r2m)
            let mutable hdev = 0.0
            for i in 0 .. 2 * l do
                for j in 0 .. 2 * l do
                    hdev <- max hdev (abs (d12.[i].[j] - d1d2.[i].[j]))
            check (sprintf "D_%d homomorphism (dev %.2g)" l hdev) (hdev < 1e-7)

        // applyRep: scalars untouched, vector block = D_1 action.
        let spec = Irreps.mkSpec [ (0, Even, 2); (1, Odd, 1) ]
        let r = Rotations.randomRotation rngD
        let feat = [| 3.5; -1.25; 0.4; 0.9; -0.2 |]
        let out = Rotations.applyRep spec r feat
        check "applyRep leaves scalars fixed" (out.[0] = feat.[0] && out.[1] = feat.[1])
        let d1 = Rotations.wignerD 1 r
        let vec = matVec d1 (Array.sub feat 2 3)
        checkArrayClose "applyRep vector block = D_1 v" 1e-12 vec (Array.sub out 2 3)
