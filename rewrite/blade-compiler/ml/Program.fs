module BladeML.Program

[<EntryPoint>]
let main _argv =
    printfn "BladeML reference implementation — equivariant ML module tests"
    printfn ""
    Tests_Core.run ()
    Tests_Wigner.run ()
    Tests_SphericalHarmonics.run ()
    Tests_Ops.run ()
    Tests_Graph.run ()
    TestHarness.summary ()
