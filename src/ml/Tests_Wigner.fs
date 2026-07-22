namespace BladeML

/// 3j symbols, complex CG, and the real-basis coupling tensors.
/// Includes the demonstration that the ml-spec's `m1 + m2 == m_out`
/// CGIndex constraint is a COMPLEX-basis rule that does not hold in the
/// real basis its own spherical-harmonics section uses.
module Tests_Wigner =

    open System
    open TestHarness
    open MathUtils

    /// Gaunt-type identity: coupling two real SH of the SAME argument
    /// through the real CG tensor must reproduce Y_l3 scaled by
    /// k = sqrt((2l1+1)(2l2+1) / (4 pi (2l3+1))) * <l1 0 l2 0 | l3 0>.
    let private gauntProject (l1: int) (l2: int) (l3: int) (v: float[]) : float[] =
        let c = Wigner.realCGDense l1 l2 l3
        let y1 = SphericalHarmonics.eval l1 v.[0] v.[1] v.[2]
        let y2 = SphericalHarmonics.eval l2 v.[0] v.[1] v.[2]
        let g = Array.zeroCreate (2 * l3 + 1)
        for c1 in 0 .. 2 * l1 do
            for c2 in 0 .. 2 * l2 do
                for c3 in 0 .. 2 * l3 do
                    g.[c3] <- g.[c3] + c.[c1].[c2].[c3] * y1.[c1] * y2.[c2]
        g

    let run () =
        section "wigner: 3j symbols"

        checkClose "3j(1,1,2;0,0,0)" 1e-12 (sqrt (2.0 / 15.0)) (Wigner.wigner3j 1 1 2 0 0 0)
        checkClose "3j(2,2,2;0,0,0)" 1e-12 (-(sqrt (2.0 / 35.0))) (Wigner.wigner3j 2 2 2 0 0 0)
        checkClose "3j(1,1,0;1,-1,0)" 1e-12 (1.0 / sqrt 3.0) (Wigner.wigner3j 1 1 0 1 (-1) 0)
        checkClose "3j zero when m-sum nonzero" 0.0 0.0 (Wigner.wigner3j 1 1 2 1 0 0)
        checkClose "3j zero outside triangle" 0.0 0.0 (Wigner.wigner3j 1 1 3 0 0 0)
        // 3j column-permutation symmetry: even permutation invariant.
        checkClose "3j cyclic invariance" 1e-12
            (Wigner.wigner3j 1 2 3 1 (-1) 0) (Wigner.wigner3j 2 3 1 (-1) 0 1)

        section "wigner: complex Clebsch-Gordan"

        checkClose "<1 0 1 0|2 0> = sqrt(2/3)" 1e-12 (sqrt (2.0 / 3.0)) (Wigner.clebsch 1 0 1 0 2 0)
        checkClose "<1 1 1 -1|0 0> = 1/sqrt(3)" 1e-12 (1.0 / sqrt 3.0) (Wigner.clebsch 1 1 1 (-1) 0 0)
        checkClose "<1 1 1 0|2 1> = 1/sqrt(2)" 1e-12 (1.0 / sqrt 2.0) (Wigner.clebsch 1 1 1 0 2 1)
        checkClose "CG selection: m1+m2 <> m3 is zero" 0.0 0.0 (Wigner.clebsch 1 1 1 1 2 1)

        // Orthogonality: sum_{m1,m2} CG(l3,m3) CG(l3',m3') = delta.
        for (l1, l2) in [ (1, 1); (1, 2); (2, 2) ] do
            let mutable worst = 0.0
            for l3 in abs (l1 - l2) .. l1 + l2 do
                for l3' in abs (l1 - l2) .. l1 + l2 do
                    for m3 in -l3 .. l3 do
                        for m3' in -l3' .. l3' do
                            let mutable s = 0.0
                            for m1 in -l1 .. l1 do
                                for m2 in -l2 .. l2 do
                                    s <- s + Wigner.clebsch l1 m1 l2 m2 l3 m3
                                             * Wigner.clebsch l1 m1 l2 m2 l3' m3'
                            let expect = if l3 = l3' && m3 = m3' then 1.0 else 0.0
                            worst <- max worst (abs (s - expect))
            check (sprintf "CG orthogonality (%d x %d), worst dev %.2g" l1 l2 worst) (worst < 1e-10)

        section "wigner: real-basis coupling"

        // Orthogonality survives the unitary change of basis.
        for (l1, l2) in [ (1, 1); (1, 2); (2, 2) ] do
            let mutable worst = 0.0
            for l3 in abs (l1 - l2) .. l1 + l2 do
                let c = Wigner.realCGDense l1 l2 l3
                for l3' in abs (l1 - l2) .. l1 + l2 do
                    let c' = Wigner.realCGDense l1 l2 l3'
                    for c3 in 0 .. 2 * l3 do
                        for c3' in 0 .. 2 * l3' do
                            let mutable s = 0.0
                            for c1 in 0 .. 2 * l1 do
                                for c2 in 0 .. 2 * l2 do
                                    s <- s + c.[c1].[c2].[c3] * c'.[c1].[c2].[c3']
                            let expect = if l3 = l3' && c3 = c3' then 1.0 else 0.0
                            worst <- max worst (abs (s - expect))
            check (sprintf "real CG orthogonality (%d x %d), worst dev %.2g" l1 l2 worst) (worst < 1e-10)

        // Exchange symmetry (ml-spec 14.2): for l1 = l2,
        // C[m1,m2,m3] = (-1)^(l1+l2-l3) C[m2,m1,m3] — survives the real basis.
        for l in [ 1; 2 ] do
            for l3 in 0 .. 2 * l do
                let c = Wigner.realCGDense l l l3
                let sign = paritySign (2 * l - l3)
                let mutable worst = 0.0
                for c1 in 0 .. 2 * l do
                    for c2 in 0 .. 2 * l do
                        for c3 in 0 .. 2 * l3 do
                            worst <- max worst (abs (c.[c1].[c2].[c3] - sign * c.[c2].[c1].[c3]))
                check (sprintf "real CG exchange symmetry l=%d l3=%d (sign %+.0f), dev %.2g" l l3 sign worst)
                    (worst < 1e-10)

        // Real-basis sparsity: every nonzero satisfies
        // |m3| in { ||m1|-|m2||, |m1|+|m2| }.
        for (l1, l2, l3) in [ (1, 1, 2); (1, 2, 3); (2, 2, 2); (1, 2, 2) ] do
            let entries = Wigner.realCGSparse l1 l2 l3
            let allOk =
                entries |> Array.forall (fun e ->
                    let m1 = abs (e.C1 - l1)
                    let m2 = abs (e.C2 - l2)
                    let m3 = abs (e.C3 - l3)
                    m3 = abs (m1 - m2) || m3 = m1 + m2)
            check (sprintf "real sparsity |m3| in {||m1|-|m2||, |m1|+|m2|} for (%d,%d,%d), %d nonzeros"
                       l1 l2 l3 entries.Length) allOk

        // THE SPEC DISCREPANCY, demonstrated: in the real basis the complex
        // rule m1 + m2 = m3 does NOT characterize the support. For
        // (1,1,2), the entry (m1,m2,m3) = (-1,+1,-2) — the coupling of y and
        // x into the xy harmonic — is nonzero although m1 + m2 = 0 <> -2.
        let c112 = Wigner.realCGDense 1 1 2
        check "real (1,1,2): C[m=-1, m=+1 -> m=-2] nonzero (violates m1+m2=m3)"
            (abs c112.[0].[2].[0] > 1e-3)
        let violations =
            Wigner.realCGSparse 1 1 2
            |> Array.filter (fun e -> (e.C1 - 1) + (e.C2 - 1) <> e.C3 - 2)
        check (sprintf "real (1,1,2): %d nonzeros violate the complex m-rule" violations.Length)
            (violations.Length > 0)

        section "wigner: Gaunt cross-validation against spherical harmonics"

        // Even l1+l2+l3: real-CG projection of Y_l1 Y_l2 (same argument)
        // reproduces k * Y_l3 with the closed-form Gaunt constant.
        let rng = Random(42)
        let kExpected =
            sqrt (3.0 * 3.0 / (4.0 * Math.PI * 5.0)) * Wigner.clebsch 1 0 1 0 2 0
        for i in 1 .. 3 do
            let v = Rotations.randomUnitVector rng
            let g = gauntProject 1 1 2 v
            let y3 = SphericalHarmonics.eval 2 v.[0] v.[1] v.[2]
            let expected = y3 |> Array.map (fun t -> kExpected * t)
            checkArrayClose (sprintf "Gaunt (1,1,2) sample %d: proj = k Y_2, k=%.6f" i kExpected)
                1e-10 expected g

        // Odd l1+l2+l3: <l1 0 l2 0|l3 0> = 0, so the projection of a
        // same-argument product vanishes identically.
        for i in 1 .. 3 do
            let v = Rotations.randomUnitVector rng
            let g = gauntProject 1 2 2 v
            check (sprintf "Gaunt (1,2,2) sample %d: projection vanishes (odd sum), max %.2g" i (maxAbs g))
                (maxAbs g < 1e-10)

        // (1,1,1) is the cross-product coupling: nonzero and antisymmetric.
        let c111 = Wigner.realCGDense 1 1 1
        let mutable c111max = 0.0
        let mutable c111antisym = 0.0
        for c1 in 0 .. 2 do
            for c2 in 0 .. 2 do
                for c3 in 0 .. 2 do
                    c111max <- max c111max (abs c111.[c1].[c2].[c3])
                    c111antisym <- max c111antisym (abs (c111.[c1].[c2].[c3] + c111.[c2].[c1].[c3]))
        check (sprintf "real (1,1,1) coupling nonzero (max %.4f)" c111max) (c111max > 0.1)
        check (sprintf "real (1,1,1) coupling antisymmetric (dev %.2g)" c111antisym) (c111antisym < 1e-10)
