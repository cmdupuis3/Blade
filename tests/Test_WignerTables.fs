// Pins for the compiler-native CG machinery (WignerTables.fs) against
// INDEPENDENTLY KNOWN truth — closed forms, orthogonality, and constants
// already cross-validated by the ml/ project's Gaunt/Wigner-D pipeline and
// frozen into the ml-e2e corpus pins. The ml/ implementation stays the
// primary oracle; these checks make the PORT independently trustworthy.
module Blade.Tests.WignerTablesReview

open Blade.Tests.TestHarness
open Blade.ML.WignerTables

module MLS = Blade.ML.Spec

let private close (a: float) (b: float) = abs (a - b) < 1e-12

let runWignerTablesTests () : BlockResult =
    printHeader "Wigner/CG Tables (compiler port)"
    let mutable passed = 0
    let mutable failed = 0
    let mutable failedNames : string list = []
    let check name ok detail =
        if ok then
            passed <- passed + 1
            resultLine Pass name detail
        else
            failed <- failed + 1
            failedNames <- failedNames @ [name]
            resultLine Fail name detail

    // ---- closed forms -------------------------------------------------
    // (1 1 0; 0 0 0) = -1/sqrt(3); (1 1 2; 0 0 0) = sqrt(2/15).
    check "3j (1 1 0; 0 0 0) = -1/sqrt(3)"
          (close (wigner3j 1 1 0 0 0 0) (-1.0 / sqrt 3.0)) ""
    check "3j (1 1 2; 0 0 0) = sqrt(2/15)"
          (close (wigner3j 1 1 2 0 0 0) (sqrt (2.0 / 15.0))) ""
    // <1 0; 1 0 | 2 0> = sqrt(2/3)
    check "CG <1 0; 1 0 | 2 0> = sqrt(2/3)"
          (close (clebsch 1 0 1 0 2 0) (sqrt (2.0 / 3.0))) ""

    // ---- 3j orthogonality: sum over (m1, m2) of (2j3+1)·3j² = 1 --------
    let ortho j1 j2 j3 (m3: int) =
        let mutable s = 0.0
        for m1 in -j1 .. j1 do
            for m2 in -j2 .. j2 do
                let v = wigner3j j1 j2 j3 m1 m2 m3
                s <- s + float (2 * j3 + 1) * v * v
        s
    check "3j orthogonality (2 2 3, m3 = 1)" (close (ortho 2 2 3 1) 1.0) ""
    check "3j orthogonality (1 2 2, m3 = 0)" (close (ortho 1 2 2 0) 1.0) ""

    // ---- real-basis support: the F1 witness -----------------------------
    // Real (1,1,2) couples (m1,m2,m3) = (-1,+1,-2) — the y·x → xy entry —
    // where m1+m2 = 0 ≠ -2: the complex selection rule does NOT describe
    // the real support. 0-based components: (0, 2, 0).
    //
    // The coefficient is pinned EXACTLY at 1/sqrt(2), derived from the closed
    // form in the same 3j normalization the sparse checks below establish
    // (`sparse (1,1,0)` = -1/sqrt(3) on the diagonal is that same convention:
    // U(l=1) row m=0 is the single complex mu=0 column, so that entry IS
    // <1 0; 1 0 | 0 0> = 3j(1 1 0; 0 0 0) = -1/sqrt(3), no extra factor).
    //
    // Derivation. T[c1][c2][c3] = Σ_{mu1,mu2} conj(U1[c1][mu1]) conj(U2[c2][mu2])
    // <l1 mu1; l2 mu2 | l3 mu3> U3[c3][mu3], mu3 = mu1 + mu2. With s = 1/sqrt 2
    // the (phase-free real-SH) U rows in play are
    //   U(1) row m=-1 : mu=-1 -> i·s,  mu=+1 -> i·s        (the "y" harmonic)
    //   U(1) row m=+1 : mu=-1 ->   s,  mu=+1 ->  -s        (the "x" harmonic)
    //   U(2) row m=-2 : mu=-2 -> i·s,  mu=+2 -> -i·s       (the "xy" harmonic)
    // c1=0 (m1=-1), c2=2 (m2=+1), c3=0 (m3=-2). U3's row is supported only at
    // mu3 = ±2, so of the four (mu1,mu2) pairs only (-1,-1) and (+1,+1) survive
    // (the two with mu3 = 0 meet a zero column — that is the real support
    // living OUTSIDE m1+m2 = m3):
    //   (-1,-1): (-i·s)(  s)·<1,-1;1,-1|2,-2>·( i·s) = (-i·i)s^3 = s^3
    //   (+1,+1): (-i·s)( -s)·<1,+1;1,+1|2,+2>·(-i·s) = ( i·-i)s^3 = s^3
    // Both CGs are the highest/lowest-weight couplings and equal 1 exactly
    // (Racah: 3j(1 1 2; 1 1 -2) = 1/sqrt 5, times the sqrt(2j3+1) = sqrt 5 in
    // `clebsch`, times parity +1). Total = 2·s^3 = 2/(2·sqrt 2) = 1/sqrt(2).
    // l1+l2+l3 = 4 is even, so the tensor is real and the -i phase fix does not
    // fire; the value is positive as computed.
    //
    // SECOND, INDEPENDENT ROUTE (no 3j/U algebra at all), via the Cartesian
    // bridge conventions: 1 (x) 1 -> 2 is the symmetric-traceless part of the
    // outer product G_ij = u_i v_j, whose xy row is (G_01 + G_10)/sqrt 2
    // (CartesianBridge.bridge9Rows, l=2 xy). Y1 component order is (y, z, x)
    // so c1 = 0 is u_y and c2 = 2 is v_x; Y2 order is
    // (xy, yz, 3z^2-r^2, xz, x^2-y^2) so c3 = 0 is xy. The coefficient of
    // u_y·v_x in (u_x v_y + u_y v_x)/sqrt 2 is 1/sqrt(2). Same number, and it
    // is also a cross-check that the two compiler-native tables share one
    // convention.
    let t112 = realCGDense 1 1 2
    check "real (1,1,2) (-1,+1,-2) entry = 1/sqrt(2) (F1 witness)"
          (close t112.[0].[2].[0] (1.0 / sqrt 2.0))
          (sprintf "coef %.17g, want %.17g" t112.[0].[2].[0] (1.0 / sqrt 2.0))

    // ---- exchange antisymmetry of 1x1->1 (cross product) ----------------
    let t111 = realCGDense 1 1 1
    let mutable antisym = true
    let mutable diagZero = true
    for a in 0 .. 2 do
        for b in 0 .. 2 do
            for c in 0 .. 2 do
                if abs (t111.[a].[b].[c] + t111.[b].[a].[c]) > 1e-12 then antisym <- false
                if a = b && abs t111.[a].[b].[c] > 1e-12 then diagZero <- false
    check "real (1,1,1) exchange-antisymmetric with zero diagonal" (antisym && diagZero) ""

    // ---- constants frozen in the ml-e2e corpus pins ---------------------
    // 1 (x) 1 -> 0: three diagonal entries at -1/sqrt(3).
    let s110 = realCGSparse 1 1 0
    check "sparse (1,1,0): 3 diagonal entries at -1/sqrt(3)"
          (s110.Length = 3
           && s110 |> Array.forall (fun e -> e.C1 = e.C2 && e.C3 = 0 && close e.Coef (-1.0 / sqrt 3.0))) ""
    // 2 (x) 2 -> 0: five diagonal entries at 1/sqrt(5).
    let s220 = realCGSparse 2 2 0
    check "sparse (2,2,0): 5 diagonal entries at 1/sqrt(5)"
          (s220.Length = 5
           && s220 |> Array.forall (fun e -> e.C1 = e.C2 && e.C3 = 0 && close e.Coef (1.0 / sqrt 5.0))) ""
    // 0 (x) l -> l: identity coupling (coef 1 up to rounding at 1e-12).
    let s022 = realCGSparse 0 2 2
    check "sparse (0,2,2): identity coupling"
          (s022.Length = 5
           && s022 |> Array.forall (fun e -> e.C1 = 0 && e.C2 = e.C3 && abs (e.Coef - 1.0) < 1e-12)) ""

    // ---- lexicographic entry order (the CGIndex enumeration contract) ---
    let s112 = realCGSparse 1 1 2
    let sorted =
        s112
        |> Array.pairwise
        |> Array.forall (fun (a, b) ->
            (a.C1, a.C2, a.C3) < (b.C1, b.C2, b.C3))
    check "sparse entries in lexicographic (c1,c2,c3) order" sorted ""

    // ---- spec algebra: tpSpec / homDim vs hand-computed truth -----------
    // The SAME hand values pin the ml/ reference (Tests_Core): both
    // implementations are pinned to the truth, never to each other.
    let mkS triples =
        triples |> List.map (fun (l, p, m) -> ({ L = l; Parity = p; Mult = m } : MLS.SpecEntry))
    let sh1 = MLS.shSpec 1
    let spec60 = mkS [ (0, 0, 16); (1, 1, 8); (2, 0, 4) ]
    check "tpSpec (sh1 x sh1) canonical value"
          (MLS.tpSpec sh1 sh1 = mkS [ (0, 0, 2); (1, 0, 1); (1, 1, 2); (2, 0, 1) ]) ""
    check "tpSpec completeness (spec60 x sh1)"
          (MLS.totalDim (MLS.tpSpec spec60 sh1) = MLS.totalDim spec60 * MLS.totalDim sh1) ""
    check "homDim spec60 -> spec60 = 336" (MLS.homDim spec60 spec60 = 336) ""
    check "homDim disjoint parity = 0"
          (MLS.homDim (mkS [ (0, 0, 2) ]) (mkS [ (0, 1, 2) ]) = 0) ""
    check "homDim duplicate entries aggregate"
          (MLS.homDim (mkS [ (0, 0, 1); (0, 0, 2) ]) (mkS [ (0, 0, 3) ]) = 9) ""

    printFooter "Wigner Tables" [ sprintf "%d passed" passed; sprintf "%d failed" failed ]
    { Block = "Wigner Tables"; Passed = passed; Failed = failed; Skipped = 0; FailedNames = failedNames }
