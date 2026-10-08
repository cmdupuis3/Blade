module BladeML.Program

[<EntryPoint>]
let main argv =
    match argv with
    | [| "dump-oracle" |] ->
        // Print the fixed dataset, init weights, and training pins in a
        // copy-pasteable form for authoring the Blade e2e example.
        OracleDump.dump ()
        0
    | [| "dump-equiv" |] ->
        // Print the rotation-certificate fixtures (baked Wigner-D matrices,
        // both-ways evaluations) for the ml-equiv certificate corpus tests.
        OracleDump.dumpEquiv ()
        0
    | [| "dump-cartesian" |] ->
        // Print the Cartesian<->irreps bridge constants and certificates
        // (fit-derived, Wigner-consistent) for the sgs corpus tests.
        OracleDump.dumpCartesian ()
        0
    | [| "dump-lie" |] ->
        // Print the Lie-algebra pins (so(3) tables certified against the
        // fit Wigner D, Killing/Casimir, small-algebra invariants, moment
        // annihilation, the d = 12 canonicalization lighthouse, the
        // functional lane) for the tests/corpus/lie/ corpus.
        OracleDump.dumpLie ()
        0
    | _ ->
        printfn "BladeML reference implementation -- equivariant ML module tests"
        printfn ""
        Tests_Core.run ()
        Tests_Wigner.run ()
        Tests_SphericalHarmonics.run ()
        Tests_Ops.run ()
        Tests_Graph.run ()
        Tests_Autodiff.run ()
        TestHarness.summary ()
