module BladeSpectra.Program

[<EntryPoint>]
let main _ =
    BladeSpectra.OracleDump.dumpAll ()
    0
