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
    let t112 = realCGDense 1 1 2
    check "real (1,1,2) has the (-1,+1,-2) entry (F1 witness)"
          (abs t112.[0].[2].[0] > 0.1)
          (sprintf "coef %g" t112.[0].[2].[0])

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
