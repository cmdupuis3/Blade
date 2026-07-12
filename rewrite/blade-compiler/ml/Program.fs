module BladeML.Program

[<EntryPoint>]
let main argv =
    match argv with
    | [| "dump-oracle" |] ->
        // Print the fixed dataset, init weights, and training pins in a
        // copy-pasteable form for authoring the Blade e2e example.
        OracleDump.dump ()
        0
    | _ ->
        printfn "BladeML reference implementation — equivariant ML module tests"
        printfn ""
        Tests_Core.run ()
        Tests_Wigner.run ()
        Tests_SphericalHarmonics.run ()
        Tests_Ops.run ()
        Tests_Graph.run ()
        Tests_Autodiff.run ()
        TestHarness.summary ()
