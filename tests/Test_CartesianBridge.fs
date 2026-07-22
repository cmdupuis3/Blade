// Pins for the compiler-native Cartesian<->irreps bridge constants
// (ml/compiler/CartesianBridge.fs) against INDEPENDENTLY KNOWN truth —
// orthonormality, exact closed forms, the packed/dense consistency law, and
// the one Schur ratio sqrt(15/8pi) tying the l=2 rows to the y_to solid-
// harmonic constants. The ml/ project's `dump-cartesian` fit (against
// SphericalHarmonics + Wigner-D intertwining) stays the primary oracle;
// these checks make the compiler PORT independently trustworthy and catch
// constant drift at `blade test` time.
module Blade.Tests.CartesianBridgeReview

open Blade.Tests.TestHarness

module CB = Blade.ML.CartesianBridge
module MLS = Blade.ML.Spec

let private close (a: float) (b: float) = abs (a - b) < 1e-12

let runCartesianBridgeTests () : BlockResult =
    printHeader "Cartesian Bridge (compiler constants)"
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

    let b9 = CB.bridge9Rows |> List.map List.toArray |> List.toArray
    let s2i = CB.symToIrrRows |> List.map List.toArray |> List.toArray
    let i2s = CB.irrToSymRows |> List.map List.toArray |> List.toArray
    let dot (a: float[]) (b: float[]) = Array.fold2 (fun acc x y -> acc + x * y) 0.0 a b

    // ---- specs -----------------------------------------------------------
    check "gradSpec = [(0,e,1);(1,e,1);(2,e,1)], dim 9"
          (CB.gradSpec = [ { MLS.L = 0; MLS.Parity = 0; MLS.Mult = 1 }
                           { MLS.L = 1; MLS.Parity = 0; MLS.Mult = 1 }
                           { MLS.L = 2; MLS.Parity = 0; MLS.Mult = 1 } ]
           && MLS.totalDim CB.gradSpec = 9) ""
    check "tauSpec = [(0,e,1);(2,e,1)], dim 6"
          (MLS.totalDim CB.tauSpec = 6) ""

    // ---- bridge9 is orthogonal (rows orthonormal over R^9 Frobenius) ----
    let mutable orthoOk = true
    for i in 0 .. 8 do
        for j in 0 .. 8 do
            let expect = if i = j then 1.0 else 0.0
            if not (close (dot b9.[i] b9.[j]) expect) then orthoOk <- false
    check "bridge9 rows orthonormal (B B^T = I)" orthoOk ""

    // ---- exact closed forms ---------------------------------------------
    check "trace row = 1/sqrt(3) on the diagonal"
          (close b9.[0].[0] (1.0 / sqrt 3.0) && close b9.[0].[4] (1.0 / sqrt 3.0) && close b9.[0].[8] (1.0 / sqrt 3.0)) ""
    check "vorticity a_x row = (g21 - g12)/sqrt(2) at slot 3 (Y1 order y,z,x)"
          (close b9.[3].[7] (1.0 / sqrt 2.0) && close b9.[3].[5] (-1.0 / sqrt 2.0)) ""
    check "3z^2-r^2 row = (-1,-1,2)/sqrt(6) on the diagonal"
          (close b9.[6].[0] (-1.0 / sqrt 6.0) && close b9.[6].[4] (-1.0 / sqrt 6.0) && close b9.[6].[8] (2.0 / sqrt 6.0)) ""

    // ---- packed/dense consistency: symToIrr row k = the matching bridge9
    // row with off-diagonal packed entries DOUBLED (Frobenius weighting) ---
    // symToIrr rows [l0; xy; yz; z2; xz; x2y2] map to bridge9 rows [0;4;5;6;7;8].
    let b9RowFor = [| 0; 4; 5; 6; 7; 8 |]
    let packIdx = CB.packPairs |> List.toArray
    let mutable packOk = true
    for k in 0 .. 5 do
        let br = b9.[b9RowFor.[k]]
        for c in 0 .. 5 do
            let (i, j) = packIdx.[c]
            let expect = if i = j then br.[3 * i + j] else 2.0 * br.[3 * i + j]
            if not (close s2i.[k].[c] expect) then packOk <- false
    check "symToIrr = bridge9 restricted to Sym with doubled off-diagonals" packOk ""

    // ---- irrToSym is the exact inverse -----------------------------------
    let mutable invOk = true
    for i in 0 .. 5 do
        for j in 0 .. 5 do
            let mutable acc = 0.0
            for k in 0 .. 5 do
                acc <- acc + i2s.[i].[k] * s2i.[k].[j]
            if not (close acc (if i = j then 1.0 else 0.0)) then invOk <- false
    check "irrToSym . symToIrr = identity" invOk ""

    // ---- the Schur ratio ties the l=2 rows to the y_to constants ---------
    // (the SAME constants baked in MLElaborate.yToDecl and pinned by the
    // ml/ SphericalHarmonics tests): harmonic row = sqrt(15/8pi) * ours.
    let schur = sqrt (15.0 / (8.0 * System.Math.PI))
    check "xy row * sqrt(15/8pi) = 1.0925484305920792 (the y_to xy constant)"
          (close (s2i.[1].[1] * schur) 1.0925484305920792) ""
    check "z2 row * sqrt(15/8pi) = 2 * 0.31539156525252005 (the y_to z2 constant)"
          (close (s2i.[3].[5] * schur) (2.0 * 0.31539156525252005)) ""
    check "x2y2 row * sqrt(15/8pi) = 0.5462742152960396 (the y_to x2y2 constant)"
          (close (s2i.[5].[0] * schur) 0.5462742152960396) ""

    printFooter "Cartesian Bridge" [ sprintf "%d passed" passed; sprintf "%d failed" failed ]
    { Block = "Cartesian Bridge"; Passed = passed; Failed = failed; Skipped = 0; FailedNames = failedNames }
