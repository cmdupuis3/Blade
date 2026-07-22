module BladeSgs.Program

[<EntryPoint>]
let main argv =
    match argv with
    | [| "dump-oracle" |] ->
        // Print the synthetic-field / filter / stress pins for the sgs corpus.
        Oracle.dump ()
        0
    | [| "dump-training" |] ->
        // Print the a-priori closure-training pins (sgs/006-007).
        TrainingOracle.dump ()
        0
    | [| "dump-formers" |] ->
        // Print the sgs.grad/box_filter/stress op pins (sgs/008-009).
        Oracle.dumpFormers ()
        0
    | [| "dump-burgers" |] ->
        // Print the Burgers DNS->LES pins (sgs/015, examples/08).
        Burgers.dump ()
        0
    | _ ->
        printfn "BladeSgs physics oracle — run with: dump-oracle"
        0
