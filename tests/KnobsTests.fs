// `blade test knobs`: the knob registry (src/Knobs.fs, deep review 2 K16).
//
//   1. THE OLD-VERSUS-NEW TABLES. Every knob read the K16 change moved onto the
//      registry has a table: raw values (unset, empty, the switch spellings,
//      blanks, garbage, the knob's own special values) and the result the OLD
//      reader gave, RECORDED BY RUNNING THE OLD CODE (the master 0c39e4a9
//      Blade.dll, through reflection for its private functions; an inline
//      expression the change extracted was run as copied from the old source).
//      The new reader must give the same row. Each row runs with
//      BLADE_TOOLCHAIN_FILE pointed at a private temp file (holding the row's
//      file keys, or absent), so no ambient toolchain file can leak in.
//   2. THE REGISTRY AGAINST ITS WITNESSES: the hand-written notes (docs/reference/knob-notes.md),
//      `Optimize.optimizerGates` / `optimizerGateMirrors`, the suite's cleared
//      and pinned lists, and `Setup.persistedToolchainKeys`.
//   3. NO RAW READ LEFT in the converted files.
//   4. docs/reference/knobs.md is the registry's rendering (`--write`
//      regenerates it).
//
// The default suite runs everything but the PrintForm rows as "Knobs Sync"
// (`runKnobsSyncTests`), so a knob added to the notes or to optimizerGates
// without its registry entry fails the suite. The PrintForm rows read or record
// PrintForm's process state (`describe` / `applyCliDefault`), so they run only
// under the standalone `blade test knobs`, last.
module Blade.Tests.KnobsTests

open System
open System.IO
open System.Text.RegularExpressions
open Blade
open Blade.Tests.TestHarness

// ---------------------------------------------------------------------------
// Formatting: the SAME text as the recorder script that produced the tables.
// ---------------------------------------------------------------------------

let private fmtStr (s: string) =
    if isNull s then "null" else "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""
let private fmtOpt (o: string option) = match o with None -> "None" | Some s -> "Some " + fmtStr s
let private fmtBool (b: bool) = if b then "true" else "false"
let private fmtSwitch (s: Toolchain.Switch) =
    match s with
    | Toolchain.SwitchOn -> "SwitchOn"
    | Toolchain.SwitchOff -> "SwitchOff"
    | Toolchain.SwitchUnset -> "SwitchUnset"
    | Toolchain.SwitchOther t -> "SwitchOther " + fmtStr t
let private fmtPool (p: Toolchain.PoolRetention) =
    String.concat "|" [ p.Arm; p.Setting; string p.MaxBlockMB; string p.BudgetMB; p.Source; fmtOpt p.Malformed ]
let private fmtOrigin (o: (string * Toolchain.Origin) option) =
    match o with
    | None -> "None"
    | Some (v, Toolchain.FromEnv) -> "Env " + fmtStr v
    | Some (v, Toolchain.FromFile) -> "File " + fmtStr v
let private normalize (s: string) =
    let lad = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
    s.Replace(AppContext.BaseDirectory.Replace("\\", "\\\\"), "<base>\\\\")
     .Replace(lad.Replace("\\", "\\\\"), "<localappdata>")

// ---------------------------------------------------------------------------
// One row's environment.
// ---------------------------------------------------------------------------

let private withRow (env: (string * string option) list) (file: (string * string) list option) (f: unit -> string) : string =
    let tcFile = Path.Combine(Path.GetTempPath(), $"blade_knobs_test_{System.Diagnostics.Process.GetCurrentProcess().Id}.json")
    let names = (Knobs.toolchainFile.Name :: List.map fst env) |> List.distinct
    let saved = names |> List.map (fun k -> k, Environment.GetEnvironmentVariable k)
    (try File.Delete tcFile with _ -> ())
    match file with
    | Some kvs ->
        let body = kvs |> List.map (fun (k, v) -> fmtStr k + ": " + fmtStr v) |> String.concat ", "
        File.WriteAllText(tcFile, "{" + body + "}")
    | None -> ()
    Environment.SetEnvironmentVariable(Knobs.toolchainFile.Name, tcFile)
    for (k, v) in env do Environment.SetEnvironmentVariable(k, (match v with Some s -> s | None -> null))
    Toolchain.refresh ()
    try
        try f () with e -> "THROWS " + e.GetType().Name
    finally
        for (k, v) in saved do Environment.SetEnvironmentVariable(k, v)
        (try File.Delete tcFile with _ -> ())
        Toolchain.refresh ()

let private firstValue (env: (string * string option) list) = match env with (_, v) :: _ -> v | [] -> None

/// The NEW readers, by the names the tables use.
let private readers : (string * ((string * string option) list -> unit -> string)) list =
    let applyCli (fromFlag: bool) () =
        let r = match PrintForm.applyCliDefault fromFlag with Ok f -> "Ok " + PrintForm.name f | Error e -> "Error " + e
        r + " env=" + fmtStr (Environment.GetEnvironmentVariable Knobs.printForm.Name)
        + (if r.StartsWith "Ok" then " describe=" + PrintForm.describe () else "")
    [ "Toolchain.parseSwitch", (fun env () -> fmtSwitch (Toolchain.parseSwitch (firstValue env)))
      "Toolchain.envSwitch", (fun _ () -> fmtSwitch (Toolchain.envSwitch Knobs.joinThreads.Name))
      "Toolchain.envFlag.off", (fun _ () -> fmtBool (Toolchain.envFlag Knobs.llvm.Name false))
      "Toolchain.envFlag.on", (fun _ () -> fmtBool (Toolchain.envFlag Knobs.joinThreads.Name true))
      "Toolchain.fpReproEnabled", (fun _ () -> fmtBool (Toolchain.fpReproEnabled ()))
      "Toolchain.tileTune.syrk_g", (fun _ () -> string (Toolchain.tileTune "syrk_g" 4L))
      "Toolchain.tileTune.sel_cap", (fun _ () -> string (Toolchain.tileTune "sel_cap" 262144L))
      "Toolchain.joinThreadsEnabled", (fun _ () -> fmtBool (Toolchain.joinThreadsEnabled ()))
      "Toolchain.ompThreadEmissionEnabled", (fun _ () -> fmtBool (Toolchain.ompThreadEmissionEnabled ()))
      "Toolchain.activePath", (fun _ () -> normalize (fmtStr (Toolchain.activePath ())))
      "Toolchain.poolRetention", (fun _ () -> fmtPool (Toolchain.poolRetention ()))
      "Toolchain.getWithOrigin", (fun _ () -> fmtOrigin (Toolchain.getWithOrigin Knobs.blasPrefer.Name))
      "Build.optFlags", (fun _ () -> fmtStr (Build.optFlags ()))
      "Build.llvmOptFlags", (fun _ () -> fmtStr (Build.llvmOptFlags ()))
      "Build.llvmEnabled", (fun _ () -> fmtBool (Build.llvmEnabled ()))
      "Build.resolveClang", (fun _ () -> fmtOpt (Build.resolveClang ()))
      "Build.exeCacheDir", (fun _ () -> normalize (fmtOpt (Build.exeCacheDir ())))
      "Build.exeCacheVerbose", (fun _ () -> fmtBool (Build.exeCacheVerbose ()))
      "Build.tileCacheDir", (fun _ () -> normalize (fmtOpt (Build.tileCacheDir ())))
      "Build.pchCacheDir", (fun _ () -> normalize (fmtOpt (Build.pchCacheDir ())))
      "Build.memcheckCxx", (fun _ () -> fmtOpt (Build.memcheckCxxOverride ()))
      "PrintForm.current", (fun _ () -> PrintForm.name (PrintForm.current ()))
      "PrintForm.describe", (fun _ () -> PrintForm.describe ())
      "Doctor.llvmClangOrigin", (fun _ () -> Doctor.llvmRowOrigin ())
      "Doctor.stdlibOrigin", (fun _ () -> Doctor.stdlibRowOrigin ())
      "Grad.gradDump", (fun _ () -> fmtBool (Grad.gradDumpEnabled ()))
      "GrRender.explicitPath", (fun _ () -> fmtOpt (Blade.Display.GrRender.explicitHelperPath ()))
      "Setup.priorFileVar", (fun _ () -> fmtStr (Knobs.raw Knobs.toolchainFile))
      "PrintForm.applyCliDefault.flag", (fun _ -> applyCli true)
      "PrintForm.applyCliDefault.env", (fun _ -> applyCli false) ]

/// The tables: (reader, environment, toolchain-file keys, the OLD result).
/// Produced by C:\bt_cleanup\ke_scratch\rec\record.fsx against master
/// 0c39e4a9's Blade.dll; regenerate only from an old build, never from this one.
let private tables : (string * (string * string option) list * (string * string) list option * string) list =
    [
      ("Toolchain.parseSwitch", [ ("V", None) ], None, "SwitchUnset")
      ("Toolchain.parseSwitch", [ ("V", Some "") ], None, "SwitchUnset")
      ("Toolchain.parseSwitch", [ ("V", Some "1") ], None, "SwitchOn")
      ("Toolchain.parseSwitch", [ ("V", Some "0") ], None, "SwitchOff")
      ("Toolchain.parseSwitch", [ ("V", Some "on") ], None, "SwitchOn")
      ("Toolchain.parseSwitch", [ ("V", Some "OFF") ], None, "SwitchOff")
      ("Toolchain.parseSwitch", [ ("V", Some " true ") ], None, "SwitchOn")
      ("Toolchain.parseSwitch", [ ("V", Some "yes") ], None, "SwitchOther \"yes\"")
      ("Toolchain.parseSwitch", [ ("V", Some "2") ], None, "SwitchOther \"2\"")
      ("Toolchain.parseSwitch", [ ("V", Some "garbage") ], None, "SwitchOther \"garbage\"")
      ("Toolchain.parseSwitch", [ ("V", Some " ") ], None, "SwitchUnset")
      ("Toolchain.parseSwitch", [ ("V", Some "False") ], None, "SwitchOff")
      ("Toolchain.parseSwitch", [ ("V", Some "ON") ], None, "SwitchOn")
      ("Toolchain.parseSwitch", [ ("V", Some " 0 ") ], None, "SwitchOff")
      ("Toolchain.parseSwitch", [ ("V", Some "tRuE") ], None, "SwitchOn")
      ("Toolchain.parseSwitch", [ ("V", Some "	1	") ], None, "SwitchOn")
      ("Toolchain.envSwitch", [ ("BLADE_JOIN_THREADS", None) ], None, "SwitchUnset")
      ("Toolchain.envSwitch", [ ("BLADE_JOIN_THREADS", Some "") ], None, "SwitchUnset")
      ("Toolchain.envSwitch", [ ("BLADE_JOIN_THREADS", Some "1") ], None, "SwitchOn")
      ("Toolchain.envSwitch", [ ("BLADE_JOIN_THREADS", Some "0") ], None, "SwitchOff")
      ("Toolchain.envSwitch", [ ("BLADE_JOIN_THREADS", Some "on") ], None, "SwitchOn")
      ("Toolchain.envSwitch", [ ("BLADE_JOIN_THREADS", Some "OFF") ], None, "SwitchOff")
      ("Toolchain.envSwitch", [ ("BLADE_JOIN_THREADS", Some " true ") ], None, "SwitchOn")
      ("Toolchain.envSwitch", [ ("BLADE_JOIN_THREADS", Some "yes") ], None, "SwitchOther \"yes\"")
      ("Toolchain.envSwitch", [ ("BLADE_JOIN_THREADS", Some "2") ], None, "SwitchOther \"2\"")
      ("Toolchain.envSwitch", [ ("BLADE_JOIN_THREADS", Some "garbage") ], None, "SwitchOther \"garbage\"")
      ("Toolchain.envSwitch", [ ("BLADE_JOIN_THREADS", Some " ") ], None, "SwitchUnset")
      ("Toolchain.envSwitch", [ ("BLADE_JOIN_THREADS", Some "False") ], None, "SwitchOff")
      ("Toolchain.envSwitch", [ ("BLADE_JOIN_THREADS", Some " Other Text ") ], None, "SwitchOther \"Other Text\"")
      ("Toolchain.envFlag.off", [ ("BLADE_LLVM", None) ], None, "false")
      ("Toolchain.envFlag.off", [ ("BLADE_LLVM", Some "") ], None, "false")
      ("Toolchain.envFlag.off", [ ("BLADE_LLVM", Some "1") ], None, "true")
      ("Toolchain.envFlag.off", [ ("BLADE_LLVM", Some "0") ], None, "false")
      ("Toolchain.envFlag.off", [ ("BLADE_LLVM", Some "on") ], None, "true")
      ("Toolchain.envFlag.off", [ ("BLADE_LLVM", Some "OFF") ], None, "false")
      ("Toolchain.envFlag.off", [ ("BLADE_LLVM", Some " true ") ], None, "true")
      ("Toolchain.envFlag.off", [ ("BLADE_LLVM", Some "yes") ], None, "false")
      ("Toolchain.envFlag.off", [ ("BLADE_LLVM", Some "2") ], None, "false")
      ("Toolchain.envFlag.off", [ ("BLADE_LLVM", Some "garbage") ], None, "false")
      ("Toolchain.envFlag.off", [ ("BLADE_LLVM", Some " ") ], None, "false")
      ("Toolchain.envFlag.off", [ ("BLADE_LLVM", Some "False") ], None, "false")
      ("Toolchain.envFlag.on", [ ("BLADE_JOIN_THREADS", None) ], None, "true")
      ("Toolchain.envFlag.on", [ ("BLADE_JOIN_THREADS", Some "") ], None, "true")
      ("Toolchain.envFlag.on", [ ("BLADE_JOIN_THREADS", Some "1") ], None, "true")
      ("Toolchain.envFlag.on", [ ("BLADE_JOIN_THREADS", Some "0") ], None, "false")
      ("Toolchain.envFlag.on", [ ("BLADE_JOIN_THREADS", Some "on") ], None, "true")
      ("Toolchain.envFlag.on", [ ("BLADE_JOIN_THREADS", Some "OFF") ], None, "false")
      ("Toolchain.envFlag.on", [ ("BLADE_JOIN_THREADS", Some " true ") ], None, "true")
      ("Toolchain.envFlag.on", [ ("BLADE_JOIN_THREADS", Some "yes") ], None, "true")
      ("Toolchain.envFlag.on", [ ("BLADE_JOIN_THREADS", Some "2") ], None, "true")
      ("Toolchain.envFlag.on", [ ("BLADE_JOIN_THREADS", Some "garbage") ], None, "true")
      ("Toolchain.envFlag.on", [ ("BLADE_JOIN_THREADS", Some " ") ], None, "true")
      ("Toolchain.envFlag.on", [ ("BLADE_JOIN_THREADS", Some "False") ], None, "false")
      ("Toolchain.fpReproEnabled", [ ("BLADE_FP_REPRO", None); ("BLADE_FP_REASSOC", None) ], None, "false")
      ("Toolchain.fpReproEnabled", [ ("BLADE_FP_REPRO", None); ("BLADE_FP_REASSOC", Some "0") ], None, "true")
      ("Toolchain.fpReproEnabled", [ ("BLADE_FP_REPRO", None); ("BLADE_FP_REASSOC", Some "off") ], None, "true")
      ("Toolchain.fpReproEnabled", [ ("BLADE_FP_REPRO", None); ("BLADE_FP_REASSOC", Some "1") ], None, "false")
      ("Toolchain.fpReproEnabled", [ ("BLADE_FP_REPRO", None); ("BLADE_FP_REASSOC", Some "on") ], None, "false")
      ("Toolchain.fpReproEnabled", [ ("BLADE_FP_REPRO", None); ("BLADE_FP_REASSOC", Some "x") ], None, "false")
      ("Toolchain.fpReproEnabled", [ ("BLADE_FP_REPRO", None); ("BLADE_FP_REASSOC", Some " OFF ") ], None, "true")
      ("Toolchain.fpReproEnabled", [ ("BLADE_FP_REPRO", Some "1"); ("BLADE_FP_REASSOC", None) ], None, "true")
      ("Toolchain.fpReproEnabled", [ ("BLADE_FP_REPRO", Some "1"); ("BLADE_FP_REASSOC", Some "0") ], None, "true")
      ("Toolchain.fpReproEnabled", [ ("BLADE_FP_REPRO", Some "1"); ("BLADE_FP_REASSOC", Some "off") ], None, "true")
      ("Toolchain.fpReproEnabled", [ ("BLADE_FP_REPRO", Some "1"); ("BLADE_FP_REASSOC", Some "1") ], None, "true")
      ("Toolchain.fpReproEnabled", [ ("BLADE_FP_REPRO", Some "1"); ("BLADE_FP_REASSOC", Some "on") ], None, "true")
      ("Toolchain.fpReproEnabled", [ ("BLADE_FP_REPRO", Some "1"); ("BLADE_FP_REASSOC", Some "x") ], None, "true")
      ("Toolchain.fpReproEnabled", [ ("BLADE_FP_REPRO", Some "1"); ("BLADE_FP_REASSOC", Some " OFF ") ], None, "true")
      ("Toolchain.fpReproEnabled", [ ("BLADE_FP_REPRO", Some "0"); ("BLADE_FP_REASSOC", None) ], None, "false")
      ("Toolchain.fpReproEnabled", [ ("BLADE_FP_REPRO", Some "0"); ("BLADE_FP_REASSOC", Some "0") ], None, "true")
      ("Toolchain.fpReproEnabled", [ ("BLADE_FP_REPRO", Some "0"); ("BLADE_FP_REASSOC", Some "off") ], None, "true")
      ("Toolchain.fpReproEnabled", [ ("BLADE_FP_REPRO", Some "0"); ("BLADE_FP_REASSOC", Some "1") ], None, "false")
      ("Toolchain.fpReproEnabled", [ ("BLADE_FP_REPRO", Some "0"); ("BLADE_FP_REASSOC", Some "on") ], None, "false")
      ("Toolchain.fpReproEnabled", [ ("BLADE_FP_REPRO", Some "0"); ("BLADE_FP_REASSOC", Some "x") ], None, "false")
      ("Toolchain.fpReproEnabled", [ ("BLADE_FP_REPRO", Some "0"); ("BLADE_FP_REASSOC", Some " OFF ") ], None, "true")
      ("Toolchain.fpReproEnabled", [ ("BLADE_FP_REPRO", Some "on"); ("BLADE_FP_REASSOC", None) ], None, "true")
      ("Toolchain.fpReproEnabled", [ ("BLADE_FP_REPRO", Some "on"); ("BLADE_FP_REASSOC", Some "0") ], None, "true")
      ("Toolchain.fpReproEnabled", [ ("BLADE_FP_REPRO", Some "on"); ("BLADE_FP_REASSOC", Some "off") ], None, "true")
      ("Toolchain.fpReproEnabled", [ ("BLADE_FP_REPRO", Some "on"); ("BLADE_FP_REASSOC", Some "1") ], None, "true")
      ("Toolchain.fpReproEnabled", [ ("BLADE_FP_REPRO", Some "on"); ("BLADE_FP_REASSOC", Some "on") ], None, "true")
      ("Toolchain.fpReproEnabled", [ ("BLADE_FP_REPRO", Some "on"); ("BLADE_FP_REASSOC", Some "x") ], None, "true")
      ("Toolchain.fpReproEnabled", [ ("BLADE_FP_REPRO", Some "on"); ("BLADE_FP_REASSOC", Some " OFF ") ], None, "true")
      ("Toolchain.fpReproEnabled", [ ("BLADE_FP_REPRO", Some " TRUE "); ("BLADE_FP_REASSOC", None) ], None, "true")
      ("Toolchain.fpReproEnabled", [ ("BLADE_FP_REPRO", Some " TRUE "); ("BLADE_FP_REASSOC", Some "0") ], None, "true")
      ("Toolchain.fpReproEnabled", [ ("BLADE_FP_REPRO", Some " TRUE "); ("BLADE_FP_REASSOC", Some "off") ], None, "true")
      ("Toolchain.fpReproEnabled", [ ("BLADE_FP_REPRO", Some " TRUE "); ("BLADE_FP_REASSOC", Some "1") ], None, "true")
      ("Toolchain.fpReproEnabled", [ ("BLADE_FP_REPRO", Some " TRUE "); ("BLADE_FP_REASSOC", Some "on") ], None, "true")
      ("Toolchain.fpReproEnabled", [ ("BLADE_FP_REPRO", Some " TRUE "); ("BLADE_FP_REASSOC", Some "x") ], None, "true")
      ("Toolchain.fpReproEnabled", [ ("BLADE_FP_REPRO", Some " TRUE "); ("BLADE_FP_REASSOC", Some " OFF ") ], None, "true")
      ("Toolchain.fpReproEnabled", [ ("BLADE_FP_REPRO", Some "garbage"); ("BLADE_FP_REASSOC", None) ], None, "false")
      ("Toolchain.fpReproEnabled", [ ("BLADE_FP_REPRO", Some "garbage"); ("BLADE_FP_REASSOC", Some "0") ], None, "true")
      ("Toolchain.fpReproEnabled", [ ("BLADE_FP_REPRO", Some "garbage"); ("BLADE_FP_REASSOC", Some "off") ], None, "true")
      ("Toolchain.fpReproEnabled", [ ("BLADE_FP_REPRO", Some "garbage"); ("BLADE_FP_REASSOC", Some "1") ], None, "false")
      ("Toolchain.fpReproEnabled", [ ("BLADE_FP_REPRO", Some "garbage"); ("BLADE_FP_REASSOC", Some "on") ], None, "false")
      ("Toolchain.fpReproEnabled", [ ("BLADE_FP_REPRO", Some "garbage"); ("BLADE_FP_REASSOC", Some "x") ], None, "false")
      ("Toolchain.fpReproEnabled", [ ("BLADE_FP_REPRO", Some "garbage"); ("BLADE_FP_REASSOC", Some " OFF ") ], None, "true")
      ("Toolchain.fpReproEnabled", [ ("BLADE_FP_REPRO", Some ""); ("BLADE_FP_REASSOC", None) ], None, "false")
      ("Toolchain.fpReproEnabled", [ ("BLADE_FP_REPRO", Some ""); ("BLADE_FP_REASSOC", Some "0") ], None, "true")
      ("Toolchain.fpReproEnabled", [ ("BLADE_FP_REPRO", Some ""); ("BLADE_FP_REASSOC", Some "off") ], None, "true")
      ("Toolchain.fpReproEnabled", [ ("BLADE_FP_REPRO", Some ""); ("BLADE_FP_REASSOC", Some "1") ], None, "false")
      ("Toolchain.fpReproEnabled", [ ("BLADE_FP_REPRO", Some ""); ("BLADE_FP_REASSOC", Some "on") ], None, "false")
      ("Toolchain.fpReproEnabled", [ ("BLADE_FP_REPRO", Some ""); ("BLADE_FP_REASSOC", Some "x") ], None, "false")
      ("Toolchain.fpReproEnabled", [ ("BLADE_FP_REPRO", Some ""); ("BLADE_FP_REASSOC", Some " OFF ") ], None, "true")
      ("Toolchain.tileTune.syrk_g", [ ("BLADE_TILE_TUNE", None) ], None, "4")
      ("Toolchain.tileTune.syrk_g", [ ("BLADE_TILE_TUNE", Some "") ], None, "4")
      ("Toolchain.tileTune.syrk_g", [ ("BLADE_TILE_TUNE", Some "1") ], None, "4")
      ("Toolchain.tileTune.syrk_g", [ ("BLADE_TILE_TUNE", Some "0") ], None, "4")
      ("Toolchain.tileTune.syrk_g", [ ("BLADE_TILE_TUNE", Some "on") ], None, "4")
      ("Toolchain.tileTune.syrk_g", [ ("BLADE_TILE_TUNE", Some "OFF") ], None, "4")
      ("Toolchain.tileTune.syrk_g", [ ("BLADE_TILE_TUNE", Some " true ") ], None, "4")
      ("Toolchain.tileTune.syrk_g", [ ("BLADE_TILE_TUNE", Some "yes") ], None, "4")
      ("Toolchain.tileTune.syrk_g", [ ("BLADE_TILE_TUNE", Some "2") ], None, "4")
      ("Toolchain.tileTune.syrk_g", [ ("BLADE_TILE_TUNE", Some "garbage") ], None, "4")
      ("Toolchain.tileTune.syrk_g", [ ("BLADE_TILE_TUNE", Some " ") ], None, "4")
      ("Toolchain.tileTune.syrk_g", [ ("BLADE_TILE_TUNE", Some "False") ], None, "4")
      ("Toolchain.tileTune.syrk_g", [ ("BLADE_TILE_TUNE", Some "syrk_g=2") ], None, "2")
      ("Toolchain.tileTune.syrk_g", [ ("BLADE_TILE_TUNE", Some "SYRK_G = 3") ], None, "4")
      ("Toolchain.tileTune.syrk_g", [ ("BLADE_TILE_TUNE", Some "syrk_g=x") ], None, "4")
      ("Toolchain.tileTune.syrk_g", [ ("BLADE_TILE_TUNE", Some "a=1,syrk_g=5;b=2") ], None, "5")
      ("Toolchain.tileTune.syrk_g", [ ("BLADE_TILE_TUNE", Some "syrk_g=1 syrk_g=2") ], None, "1")
      ("Toolchain.tileTune.syrk_g", [ ("BLADE_TILE_TUNE", Some "syrk_g") ], None, "4")
      ("Toolchain.tileTune.syrk_g", [ ("BLADE_TILE_TUNE", Some "syrk_g=1=2") ], None, "4")
      ("Toolchain.tileTune.syrk_g", [ ("BLADE_TILE_TUNE", Some "anti_kc=7") ], None, "4")
      ("Toolchain.tileTune.syrk_g", [ ("BLADE_TILE_TUNE", Some "syrk_g=-3") ], None, "-3")
      ("Toolchain.tileTune.syrk_g", [ ("BLADE_TILE_TUNE", Some " syrk_g=8 ") ], None, "8")
      ("Toolchain.tileTune.syrk_g", [ ("BLADE_TILE_TUNE", Some "syrk_g=9999999999") ], None, "9999999999")
      ("Toolchain.tileTune.syrk_g", [ ("BLADE_TILE_TUNE", Some "syrk_g=x,syrk_g=6") ], None, "6")
      ("Toolchain.tileTune.sel_cap", [ ("BLADE_TILE_TUNE", None) ], None, "262144")
      ("Toolchain.tileTune.sel_cap", [ ("BLADE_TILE_TUNE", Some "") ], None, "262144")
      ("Toolchain.tileTune.sel_cap", [ ("BLADE_TILE_TUNE", Some "1") ], None, "262144")
      ("Toolchain.tileTune.sel_cap", [ ("BLADE_TILE_TUNE", Some "0") ], None, "262144")
      ("Toolchain.tileTune.sel_cap", [ ("BLADE_TILE_TUNE", Some "on") ], None, "262144")
      ("Toolchain.tileTune.sel_cap", [ ("BLADE_TILE_TUNE", Some "OFF") ], None, "262144")
      ("Toolchain.tileTune.sel_cap", [ ("BLADE_TILE_TUNE", Some " true ") ], None, "262144")
      ("Toolchain.tileTune.sel_cap", [ ("BLADE_TILE_TUNE", Some "yes") ], None, "262144")
      ("Toolchain.tileTune.sel_cap", [ ("BLADE_TILE_TUNE", Some "2") ], None, "262144")
      ("Toolchain.tileTune.sel_cap", [ ("BLADE_TILE_TUNE", Some "garbage") ], None, "262144")
      ("Toolchain.tileTune.sel_cap", [ ("BLADE_TILE_TUNE", Some " ") ], None, "262144")
      ("Toolchain.tileTune.sel_cap", [ ("BLADE_TILE_TUNE", Some "False") ], None, "262144")
      ("Toolchain.tileTune.sel_cap", [ ("BLADE_TILE_TUNE", Some "sel_cap=1024") ], None, "1024")
      ("Toolchain.tileTune.sel_cap", [ ("BLADE_TILE_TUNE", Some "sel_cap=0") ], None, "0")
      ("Toolchain.tileTune.sel_cap", [ ("BLADE_TILE_TUNE", Some "sel_cap=+12") ], None, "12")
      ("Toolchain.joinThreadsEnabled", [ ("BLADE_JOIN_THREADS", None) ], None, "true")
      ("Toolchain.joinThreadsEnabled", [ ("BLADE_JOIN_THREADS", Some "") ], None, "true")
      ("Toolchain.joinThreadsEnabled", [ ("BLADE_JOIN_THREADS", Some "1") ], None, "true")
      ("Toolchain.joinThreadsEnabled", [ ("BLADE_JOIN_THREADS", Some "0") ], None, "false")
      ("Toolchain.joinThreadsEnabled", [ ("BLADE_JOIN_THREADS", Some "on") ], None, "true")
      ("Toolchain.joinThreadsEnabled", [ ("BLADE_JOIN_THREADS", Some "OFF") ], None, "false")
      ("Toolchain.joinThreadsEnabled", [ ("BLADE_JOIN_THREADS", Some " true ") ], None, "true")
      ("Toolchain.joinThreadsEnabled", [ ("BLADE_JOIN_THREADS", Some "yes") ], None, "true")
      ("Toolchain.joinThreadsEnabled", [ ("BLADE_JOIN_THREADS", Some "2") ], None, "true")
      ("Toolchain.joinThreadsEnabled", [ ("BLADE_JOIN_THREADS", Some "garbage") ], None, "true")
      ("Toolchain.joinThreadsEnabled", [ ("BLADE_JOIN_THREADS", Some " ") ], None, "true")
      ("Toolchain.joinThreadsEnabled", [ ("BLADE_JOIN_THREADS", Some "False") ], None, "false")
      ("Toolchain.ompThreadEmissionEnabled", [ ("BLADE_OMP_THREADS", None) ], None, "true")
      ("Toolchain.ompThreadEmissionEnabled", [ ("BLADE_OMP_THREADS", Some "") ], None, "true")
      ("Toolchain.ompThreadEmissionEnabled", [ ("BLADE_OMP_THREADS", Some "1") ], None, "false")
      ("Toolchain.ompThreadEmissionEnabled", [ ("BLADE_OMP_THREADS", Some "0") ], None, "false")
      ("Toolchain.ompThreadEmissionEnabled", [ ("BLADE_OMP_THREADS", Some "on") ], None, "true")
      ("Toolchain.ompThreadEmissionEnabled", [ ("BLADE_OMP_THREADS", Some "OFF") ], None, "false")
      ("Toolchain.ompThreadEmissionEnabled", [ ("BLADE_OMP_THREADS", Some " true ") ], None, "true")
      ("Toolchain.ompThreadEmissionEnabled", [ ("BLADE_OMP_THREADS", Some "yes") ], None, "true")
      ("Toolchain.ompThreadEmissionEnabled", [ ("BLADE_OMP_THREADS", Some "2") ], None, "true")
      ("Toolchain.ompThreadEmissionEnabled", [ ("BLADE_OMP_THREADS", Some "garbage") ], None, "true")
      ("Toolchain.ompThreadEmissionEnabled", [ ("BLADE_OMP_THREADS", Some " ") ], None, "true")
      ("Toolchain.ompThreadEmissionEnabled", [ ("BLADE_OMP_THREADS", Some "False") ], None, "false")
      ("Toolchain.ompThreadEmissionEnabled", [ ("BLADE_OMP_THREADS", Some " 1 ") ], None, "false")
      ("Toolchain.ompThreadEmissionEnabled", [ ("BLADE_OMP_THREADS", Some "01") ], None, "true")
      ("Toolchain.ompThreadEmissionEnabled", [ ("BLADE_OMP_THREADS", Some "4") ], None, "true")
      ("Toolchain.ompThreadEmissionEnabled", [ ("BLADE_OMP_THREADS", Some "off") ], None, "false")
      ("Toolchain.ompThreadEmissionEnabled", [ ("BLADE_OMP_THREADS", Some "true") ], None, "true")
      ("Toolchain.ompThreadEmissionEnabled", [ ("BLADE_OMP_THREADS", Some "ON") ], None, "true")
      ("Toolchain.activePath", [ ("BLADE_TOOLCHAIN_FILE", None) ], None, "\"<base>\\\\blade.toolchain.json\"")
      ("Toolchain.activePath", [ ("BLADE_TOOLCHAIN_FILE", Some "") ], None, "\"<base>\\\\blade.toolchain.json\"")
      ("Toolchain.activePath", [ ("BLADE_TOOLCHAIN_FILE", Some "1") ], None, "\"1\"")
      ("Toolchain.activePath", [ ("BLADE_TOOLCHAIN_FILE", Some "0") ], None, "\"0\"")
      ("Toolchain.activePath", [ ("BLADE_TOOLCHAIN_FILE", Some "on") ], None, "\"on\"")
      ("Toolchain.activePath", [ ("BLADE_TOOLCHAIN_FILE", Some "OFF") ], None, "\"OFF\"")
      ("Toolchain.activePath", [ ("BLADE_TOOLCHAIN_FILE", Some " true ") ], None, "\" true \"")
      ("Toolchain.activePath", [ ("BLADE_TOOLCHAIN_FILE", Some "yes") ], None, "\"yes\"")
      ("Toolchain.activePath", [ ("BLADE_TOOLCHAIN_FILE", Some "2") ], None, "\"2\"")
      ("Toolchain.activePath", [ ("BLADE_TOOLCHAIN_FILE", Some "garbage") ], None, "\"garbage\"")
      ("Toolchain.activePath", [ ("BLADE_TOOLCHAIN_FILE", Some " ") ], None, "\" \"")
      ("Toolchain.activePath", [ ("BLADE_TOOLCHAIN_FILE", Some "False") ], None, "\"False\"")
      ("Toolchain.activePath", [ ("BLADE_TOOLCHAIN_FILE", Some "C:\\t\\a.json") ], None, "\"C:\\\\t\\\\a.json\"")
      ("Toolchain.activePath", [ ("BLADE_TOOLCHAIN_FILE", Some " x ") ], None, "\" x \"")
      ("Toolchain.poolRetention", [ ("BLADE_POOL_RETAIN", None) ], None, "default|default|32|64|default|None")
      ("Toolchain.poolRetention", [ ("BLADE_POOL_RETAIN", Some "") ], None, "default|default|32|64|default|None")
      ("Toolchain.poolRetention", [ ("BLADE_POOL_RETAIN", Some "1") ], None, "retain|1|64|96|env BLADE_POOL_RETAIN|None")
      ("Toolchain.poolRetention", [ ("BLADE_POOL_RETAIN", Some "0") ], None, "off|0|4|8|env BLADE_POOL_RETAIN|None")
      ("Toolchain.poolRetention", [ ("BLADE_POOL_RETAIN", Some "on") ], None, "retain|1|64|96|env BLADE_POOL_RETAIN|None")
      ("Toolchain.poolRetention", [ ("BLADE_POOL_RETAIN", Some "OFF") ], None, "off|0|4|8|env BLADE_POOL_RETAIN|None")
      ("Toolchain.poolRetention", [ ("BLADE_POOL_RETAIN", Some " true ") ], None, "retain|1|64|96|env BLADE_POOL_RETAIN|None")
      ("Toolchain.poolRetention", [ ("BLADE_POOL_RETAIN", Some "yes") ], None, "default|default|32|64|env BLADE_POOL_RETAIN|Some \"yes\"")
      ("Toolchain.poolRetention", [ ("BLADE_POOL_RETAIN", Some "2") ], None, "default|default|32|64|env BLADE_POOL_RETAIN|Some \"2\"")
      ("Toolchain.poolRetention", [ ("BLADE_POOL_RETAIN", Some "garbage") ], None, "default|default|32|64|env BLADE_POOL_RETAIN|Some \"garbage\"")
      ("Toolchain.poolRetention", [ ("BLADE_POOL_RETAIN", Some " ") ], None, "default|default|32|64|env BLADE_POOL_RETAIN|None")
      ("Toolchain.poolRetention", [ ("BLADE_POOL_RETAIN", Some "False") ], None, "off|0|4|8|env BLADE_POOL_RETAIN|None")
      ("Toolchain.poolRetention", [ ("BLADE_POOL_RETAIN", Some "default") ], None, "default|default|32|64|env BLADE_POOL_RETAIN|None")
      ("Toolchain.poolRetention", [ ("BLADE_POOL_RETAIN", Some "DEFAULT") ], None, "default|default|32|64|env BLADE_POOL_RETAIN|None")
      ("Toolchain.poolRetention", [ ("BLADE_POOL_RETAIN", Some "4:8") ], None, "pair|4:8|4|8|env BLADE_POOL_RETAIN|None")
      ("Toolchain.poolRetention", [ ("BLADE_POOL_RETAIN", Some " 64 : 96 ") ], None, "pair|64:96|64|96|env BLADE_POOL_RETAIN|None")
      ("Toolchain.poolRetention", [ ("BLADE_POOL_RETAIN", Some "8:4") ], None, "default|default|32|64|env BLADE_POOL_RETAIN|Some \"8:4\"")
      ("Toolchain.poolRetention", [ ("BLADE_POOL_RETAIN", Some "0:4") ], None, "default|default|32|64|env BLADE_POOL_RETAIN|Some \"0:4\"")
      ("Toolchain.poolRetention", [ ("BLADE_POOL_RETAIN", Some "1048577:1048577") ], None, "default|default|32|64|env BLADE_POOL_RETAIN|Some \"1048577:1048577\"")
      ("Toolchain.poolRetention", [ ("BLADE_POOL_RETAIN", Some "1048576:1048576") ], None, "pair|1048576:1048576|1048576|1048576|env BLADE_POOL_RETAIN|None")
      ("Toolchain.poolRetention", [ ("BLADE_POOL_RETAIN", Some "a:b") ], None, "default|default|32|64|env BLADE_POOL_RETAIN|Some \"a:b\"")
      ("Toolchain.poolRetention", [ ("BLADE_POOL_RETAIN", Some "1:2:3") ], None, "default|default|32|64|env BLADE_POOL_RETAIN|Some \"1:2:3\"")
      ("Toolchain.poolRetention", [ ("BLADE_POOL_RETAIN", Some "-1:4") ], None, "default|default|32|64|env BLADE_POOL_RETAIN|Some \"-1:4\"")
      ("Toolchain.poolRetention", [ ("BLADE_POOL_RETAIN", Some "off") ], None, "off|0|4|8|env BLADE_POOL_RETAIN|None")
      ("Toolchain.poolRetention", [ ("BLADE_POOL_RETAIN", None) ], Some [ ("BLADE_POOL_RETAIN", "1") ], "retain|1|64|96|blade.toolchain.json|None")
      ("Toolchain.poolRetention", [ ("BLADE_POOL_RETAIN", Some "") ], Some [ ("BLADE_POOL_RETAIN", "off") ], "off|0|4|8|blade.toolchain.json|None")
      ("Toolchain.poolRetention", [ ("BLADE_POOL_RETAIN", Some "2:4") ], Some [ ("BLADE_POOL_RETAIN", "1") ], "pair|2:4|2|4|env BLADE_POOL_RETAIN|None")
      ("Toolchain.poolRetention", [ ("BLADE_POOL_RETAIN", None) ], Some [ ("BLADE_POOL_RETAIN", "") ], "default|default|32|64|default|None")
      ("Toolchain.poolRetention", [ ("BLADE_POOL_RETAIN", None) ], Some [ ("BLADE_POOL_RETAIN", "junk") ], "default|default|32|64|blade.toolchain.json|Some \"junk\"")
      ("Toolchain.poolRetention", [ ("BLADE_POOL_RETAIN", None) ], Some [ ("BLADE_POOL_RETAIN", " 8:16 ") ], "pair|8:16|8|16|blade.toolchain.json|None")
      ("Toolchain.getWithOrigin", [ ("BLADE_BLAS_PREFER", None) ], None, "None")
      ("Toolchain.getWithOrigin", [ ("BLADE_BLAS_PREFER", None) ], Some [ ("BLADE_BLAS_PREFER", "") ], "None")
      ("Toolchain.getWithOrigin", [ ("BLADE_BLAS_PREFER", None) ], Some [ ("BLADE_BLAS_PREFER", "native") ], "File \"native\"")
      ("Toolchain.getWithOrigin", [ ("BLADE_BLAS_PREFER", Some "") ], None, "None")
      ("Toolchain.getWithOrigin", [ ("BLADE_BLAS_PREFER", Some "") ], Some [ ("BLADE_BLAS_PREFER", "") ], "None")
      ("Toolchain.getWithOrigin", [ ("BLADE_BLAS_PREFER", Some "") ], Some [ ("BLADE_BLAS_PREFER", "native") ], "File \"native\"")
      ("Toolchain.getWithOrigin", [ ("BLADE_BLAS_PREFER", Some "library") ], None, "Env \"library\"")
      ("Toolchain.getWithOrigin", [ ("BLADE_BLAS_PREFER", Some "library") ], Some [ ("BLADE_BLAS_PREFER", "") ], "Env \"library\"")
      ("Toolchain.getWithOrigin", [ ("BLADE_BLAS_PREFER", Some "library") ], Some [ ("BLADE_BLAS_PREFER", "native") ], "Env \"library\"")
      ("Toolchain.getWithOrigin", [ ("BLADE_BLAS_PREFER", Some " ") ], None, "Env \" \"")
      ("Toolchain.getWithOrigin", [ ("BLADE_BLAS_PREFER", Some " ") ], Some [ ("BLADE_BLAS_PREFER", "") ], "Env \" \"")
      ("Toolchain.getWithOrigin", [ ("BLADE_BLAS_PREFER", Some " ") ], Some [ ("BLADE_BLAS_PREFER", "native") ], "Env \" \"")
      ("Build.optFlags", [ ("BLADE_MARCH", None); ("BLADE_FP_CONTRACT", None) ], None, "\"-O3 -march=native -ffp-contract=fast -fno-math-errno -fwrapv\"")
      ("Build.optFlags", [ ("BLADE_MARCH", None); ("BLADE_FP_CONTRACT", Some "") ], None, "\"-O3 -march=native -ffp-contract=fast -fno-math-errno -fwrapv\"")
      ("Build.optFlags", [ ("BLADE_MARCH", None); ("BLADE_FP_CONTRACT", Some "off") ], None, "\"-O3 -march=native -ffp-contract=off -fno-math-errno -fwrapv\"")
      ("Build.optFlags", [ ("BLADE_MARCH", None); ("BLADE_FP_CONTRACT", Some "fast") ], None, "\"-O3 -march=native -ffp-contract=fast -fno-math-errno -fwrapv\"")
      ("Build.optFlags", [ ("BLADE_MARCH", None); ("BLADE_FP_CONTRACT", Some " on ") ], None, "\"-O3 -march=native -ffp-contract=on -fno-math-errno -fwrapv\"")
      ("Build.optFlags", [ ("BLADE_MARCH", None); ("BLADE_FP_CONTRACT", Some "Off") ], None, "\"-O3 -march=native -ffp-contract=Off -fno-math-errno -fwrapv\"")
      ("Build.optFlags", [ ("BLADE_MARCH", None); ("BLADE_FP_CONTRACT", Some "0") ], None, "\"-O3 -march=native -ffp-contract=0 -fno-math-errno -fwrapv\"")
      ("Build.optFlags", [ ("BLADE_MARCH", Some ""); ("BLADE_FP_CONTRACT", None) ], None, "\"-O3 -march=native -ffp-contract=fast -fno-math-errno -fwrapv\"")
      ("Build.optFlags", [ ("BLADE_MARCH", Some ""); ("BLADE_FP_CONTRACT", Some "") ], None, "\"-O3 -march=native -ffp-contract=fast -fno-math-errno -fwrapv\"")
      ("Build.optFlags", [ ("BLADE_MARCH", Some ""); ("BLADE_FP_CONTRACT", Some "off") ], None, "\"-O3 -march=native -ffp-contract=off -fno-math-errno -fwrapv\"")
      ("Build.optFlags", [ ("BLADE_MARCH", Some ""); ("BLADE_FP_CONTRACT", Some "fast") ], None, "\"-O3 -march=native -ffp-contract=fast -fno-math-errno -fwrapv\"")
      ("Build.optFlags", [ ("BLADE_MARCH", Some ""); ("BLADE_FP_CONTRACT", Some " on ") ], None, "\"-O3 -march=native -ffp-contract=on -fno-math-errno -fwrapv\"")
      ("Build.optFlags", [ ("BLADE_MARCH", Some ""); ("BLADE_FP_CONTRACT", Some "Off") ], None, "\"-O3 -march=native -ffp-contract=Off -fno-math-errno -fwrapv\"")
      ("Build.optFlags", [ ("BLADE_MARCH", Some ""); ("BLADE_FP_CONTRACT", Some "0") ], None, "\"-O3 -march=native -ffp-contract=0 -fno-math-errno -fwrapv\"")
      ("Build.optFlags", [ ("BLADE_MARCH", Some "off"); ("BLADE_FP_CONTRACT", None) ], None, "\"-O3 -ffp-contract=fast -fno-math-errno -fwrapv\"")
      ("Build.optFlags", [ ("BLADE_MARCH", Some "off"); ("BLADE_FP_CONTRACT", Some "") ], None, "\"-O3 -ffp-contract=fast -fno-math-errno -fwrapv\"")
      ("Build.optFlags", [ ("BLADE_MARCH", Some "off"); ("BLADE_FP_CONTRACT", Some "off") ], None, "\"-O3 -ffp-contract=off -fno-math-errno -fwrapv\"")
      ("Build.optFlags", [ ("BLADE_MARCH", Some "off"); ("BLADE_FP_CONTRACT", Some "fast") ], None, "\"-O3 -ffp-contract=fast -fno-math-errno -fwrapv\"")
      ("Build.optFlags", [ ("BLADE_MARCH", Some "off"); ("BLADE_FP_CONTRACT", Some " on ") ], None, "\"-O3 -ffp-contract=on -fno-math-errno -fwrapv\"")
      ("Build.optFlags", [ ("BLADE_MARCH", Some "off"); ("BLADE_FP_CONTRACT", Some "Off") ], None, "\"-O3 -ffp-contract=Off -fno-math-errno -fwrapv\"")
      ("Build.optFlags", [ ("BLADE_MARCH", Some "off"); ("BLADE_FP_CONTRACT", Some "0") ], None, "\"-O3 -ffp-contract=0 -fno-math-errno -fwrapv\"")
      ("Build.optFlags", [ ("BLADE_MARCH", Some " Off "); ("BLADE_FP_CONTRACT", None) ], None, "\"-O3 -ffp-contract=fast -fno-math-errno -fwrapv\"")
      ("Build.optFlags", [ ("BLADE_MARCH", Some " Off "); ("BLADE_FP_CONTRACT", Some "") ], None, "\"-O3 -ffp-contract=fast -fno-math-errno -fwrapv\"")
      ("Build.optFlags", [ ("BLADE_MARCH", Some " Off "); ("BLADE_FP_CONTRACT", Some "off") ], None, "\"-O3 -ffp-contract=off -fno-math-errno -fwrapv\"")
      ("Build.optFlags", [ ("BLADE_MARCH", Some " Off "); ("BLADE_FP_CONTRACT", Some "fast") ], None, "\"-O3 -ffp-contract=fast -fno-math-errno -fwrapv\"")
      ("Build.optFlags", [ ("BLADE_MARCH", Some " Off "); ("BLADE_FP_CONTRACT", Some " on ") ], None, "\"-O3 -ffp-contract=on -fno-math-errno -fwrapv\"")
      ("Build.optFlags", [ ("BLADE_MARCH", Some " Off "); ("BLADE_FP_CONTRACT", Some "Off") ], None, "\"-O3 -ffp-contract=Off -fno-math-errno -fwrapv\"")
      ("Build.optFlags", [ ("BLADE_MARCH", Some " Off "); ("BLADE_FP_CONTRACT", Some "0") ], None, "\"-O3 -ffp-contract=0 -fno-math-errno -fwrapv\"")
      ("Build.optFlags", [ ("BLADE_MARCH", Some "znver3"); ("BLADE_FP_CONTRACT", None) ], None, "\"-O3 -march=znver3 -ffp-contract=fast -fno-math-errno -fwrapv\"")
      ("Build.optFlags", [ ("BLADE_MARCH", Some "znver3"); ("BLADE_FP_CONTRACT", Some "") ], None, "\"-O3 -march=znver3 -ffp-contract=fast -fno-math-errno -fwrapv\"")
      ("Build.optFlags", [ ("BLADE_MARCH", Some "znver3"); ("BLADE_FP_CONTRACT", Some "off") ], None, "\"-O3 -march=znver3 -ffp-contract=off -fno-math-errno -fwrapv\"")
      ("Build.optFlags", [ ("BLADE_MARCH", Some "znver3"); ("BLADE_FP_CONTRACT", Some "fast") ], None, "\"-O3 -march=znver3 -ffp-contract=fast -fno-math-errno -fwrapv\"")
      ("Build.optFlags", [ ("BLADE_MARCH", Some "znver3"); ("BLADE_FP_CONTRACT", Some " on ") ], None, "\"-O3 -march=znver3 -ffp-contract=on -fno-math-errno -fwrapv\"")
      ("Build.optFlags", [ ("BLADE_MARCH", Some "znver3"); ("BLADE_FP_CONTRACT", Some "Off") ], None, "\"-O3 -march=znver3 -ffp-contract=Off -fno-math-errno -fwrapv\"")
      ("Build.optFlags", [ ("BLADE_MARCH", Some "znver3"); ("BLADE_FP_CONTRACT", Some "0") ], None, "\"-O3 -march=znver3 -ffp-contract=0 -fno-math-errno -fwrapv\"")
      ("Build.optFlags", [ ("BLADE_MARCH", Some " haswell "); ("BLADE_FP_CONTRACT", None) ], None, "\"-O3 -march=haswell -ffp-contract=fast -fno-math-errno -fwrapv\"")
      ("Build.optFlags", [ ("BLADE_MARCH", Some " haswell "); ("BLADE_FP_CONTRACT", Some "") ], None, "\"-O3 -march=haswell -ffp-contract=fast -fno-math-errno -fwrapv\"")
      ("Build.optFlags", [ ("BLADE_MARCH", Some " haswell "); ("BLADE_FP_CONTRACT", Some "off") ], None, "\"-O3 -march=haswell -ffp-contract=off -fno-math-errno -fwrapv\"")
      ("Build.optFlags", [ ("BLADE_MARCH", Some " haswell "); ("BLADE_FP_CONTRACT", Some "fast") ], None, "\"-O3 -march=haswell -ffp-contract=fast -fno-math-errno -fwrapv\"")
      ("Build.optFlags", [ ("BLADE_MARCH", Some " haswell "); ("BLADE_FP_CONTRACT", Some " on ") ], None, "\"-O3 -march=haswell -ffp-contract=on -fno-math-errno -fwrapv\"")
      ("Build.optFlags", [ ("BLADE_MARCH", Some " haswell "); ("BLADE_FP_CONTRACT", Some "Off") ], None, "\"-O3 -march=haswell -ffp-contract=Off -fno-math-errno -fwrapv\"")
      ("Build.optFlags", [ ("BLADE_MARCH", Some " haswell "); ("BLADE_FP_CONTRACT", Some "0") ], None, "\"-O3 -march=haswell -ffp-contract=0 -fno-math-errno -fwrapv\"")
      ("Build.optFlags", [ ("BLADE_MARCH", Some " "); ("BLADE_FP_CONTRACT", None) ], None, "\"-O3 -march= -ffp-contract=fast -fno-math-errno -fwrapv\"")
      ("Build.optFlags", [ ("BLADE_MARCH", Some " "); ("BLADE_FP_CONTRACT", Some "") ], None, "\"-O3 -march= -ffp-contract=fast -fno-math-errno -fwrapv\"")
      ("Build.optFlags", [ ("BLADE_MARCH", Some " "); ("BLADE_FP_CONTRACT", Some "off") ], None, "\"-O3 -march= -ffp-contract=off -fno-math-errno -fwrapv\"")
      ("Build.optFlags", [ ("BLADE_MARCH", Some " "); ("BLADE_FP_CONTRACT", Some "fast") ], None, "\"-O3 -march= -ffp-contract=fast -fno-math-errno -fwrapv\"")
      ("Build.optFlags", [ ("BLADE_MARCH", Some " "); ("BLADE_FP_CONTRACT", Some " on ") ], None, "\"-O3 -march= -ffp-contract=on -fno-math-errno -fwrapv\"")
      ("Build.optFlags", [ ("BLADE_MARCH", Some " "); ("BLADE_FP_CONTRACT", Some "Off") ], None, "\"-O3 -march= -ffp-contract=Off -fno-math-errno -fwrapv\"")
      ("Build.optFlags", [ ("BLADE_MARCH", Some " "); ("BLADE_FP_CONTRACT", Some "0") ], None, "\"-O3 -march= -ffp-contract=0 -fno-math-errno -fwrapv\"")
      ("Build.llvmOptFlags", [ ("BLADE_MARCH", None) ], None, "\"-O3 -march=native -fno-math-errno -fwrapv\"")
      ("Build.llvmOptFlags", [ ("BLADE_MARCH", Some "") ], None, "\"-O3 -march=native -fno-math-errno -fwrapv\"")
      ("Build.llvmOptFlags", [ ("BLADE_MARCH", Some "1") ], None, "\"-O3 -march=1 -fno-math-errno -fwrapv\"")
      ("Build.llvmOptFlags", [ ("BLADE_MARCH", Some "0") ], None, "\"-O3 -march=0 -fno-math-errno -fwrapv\"")
      ("Build.llvmOptFlags", [ ("BLADE_MARCH", Some "on") ], None, "\"-O3 -march=on -fno-math-errno -fwrapv\"")
      ("Build.llvmOptFlags", [ ("BLADE_MARCH", Some "OFF") ], None, "\"-O3 -fno-math-errno -fwrapv\"")
      ("Build.llvmOptFlags", [ ("BLADE_MARCH", Some " true ") ], None, "\"-O3 -march=true -fno-math-errno -fwrapv\"")
      ("Build.llvmOptFlags", [ ("BLADE_MARCH", Some "yes") ], None, "\"-O3 -march=yes -fno-math-errno -fwrapv\"")
      ("Build.llvmOptFlags", [ ("BLADE_MARCH", Some "2") ], None, "\"-O3 -march=2 -fno-math-errno -fwrapv\"")
      ("Build.llvmOptFlags", [ ("BLADE_MARCH", Some "garbage") ], None, "\"-O3 -march=garbage -fno-math-errno -fwrapv\"")
      ("Build.llvmOptFlags", [ ("BLADE_MARCH", Some " ") ], None, "\"-O3 -march= -fno-math-errno -fwrapv\"")
      ("Build.llvmOptFlags", [ ("BLADE_MARCH", Some "False") ], None, "\"-O3 -march=False -fno-math-errno -fwrapv\"")
      ("Build.llvmOptFlags", [ ("BLADE_MARCH", Some "off") ], None, "\"-O3 -fno-math-errno -fwrapv\"")
      ("Build.llvmOptFlags", [ ("BLADE_MARCH", Some " Off ") ], None, "\"-O3 -fno-math-errno -fwrapv\"")
      ("Build.llvmOptFlags", [ ("BLADE_MARCH", Some "znver3") ], None, "\"-O3 -march=znver3 -fno-math-errno -fwrapv\"")
      ("Build.llvmOptFlags", [ ("BLADE_MARCH", Some " haswell ") ], None, "\"-O3 -march=haswell -fno-math-errno -fwrapv\"")
      ("Build.llvmEnabled", [ ("BLADE_LLVM", None) ], None, "false")
      ("Build.llvmEnabled", [ ("BLADE_LLVM", Some "") ], None, "false")
      ("Build.llvmEnabled", [ ("BLADE_LLVM", Some "1") ], None, "true")
      ("Build.llvmEnabled", [ ("BLADE_LLVM", Some "0") ], None, "false")
      ("Build.llvmEnabled", [ ("BLADE_LLVM", Some "on") ], None, "true")
      ("Build.llvmEnabled", [ ("BLADE_LLVM", Some "OFF") ], None, "false")
      ("Build.llvmEnabled", [ ("BLADE_LLVM", Some " true ") ], None, "true")
      ("Build.llvmEnabled", [ ("BLADE_LLVM", Some "yes") ], None, "false")
      ("Build.llvmEnabled", [ ("BLADE_LLVM", Some "2") ], None, "false")
      ("Build.llvmEnabled", [ ("BLADE_LLVM", Some "garbage") ], None, "false")
      ("Build.llvmEnabled", [ ("BLADE_LLVM", Some " ") ], None, "false")
      ("Build.llvmEnabled", [ ("BLADE_LLVM", Some "False") ], None, "false")
      ("Build.resolveClang", [ ("BLADE_LLVM_CLANG", Some "C:\\c\\clang.exe") ], None, "Some \"C:\\\\c\\\\clang.exe\"")
      ("Build.resolveClang", [ ("BLADE_LLVM_CLANG", Some " clang ") ], None, "Some \"clang\"")
      ("Build.resolveClang", [ ("BLADE_LLVM_CLANG", Some " ") ], None, "Some \"\"")
      ("Build.resolveClang", [ ("BLADE_LLVM_CLANG", Some "0") ], None, "Some \"0\"")
      ("Build.resolveClang", [ ("BLADE_LLVM_CLANG", Some "off") ], None, "Some \"off\"")
      ("Build.exeCacheDir", [ ("BLADE_EXE_CACHE", None) ], None, "Some \"<localappdata>\\\\Blade\\\\exe-cache\"")
      ("Build.exeCacheDir", [ ("BLADE_EXE_CACHE", Some "") ], None, "Some \"<localappdata>\\\\Blade\\\\exe-cache\"")
      ("Build.exeCacheDir", [ ("BLADE_EXE_CACHE", Some "1") ], None, "Some \"<localappdata>\\\\Blade\\\\exe-cache\"")
      ("Build.exeCacheDir", [ ("BLADE_EXE_CACHE", Some "0") ], None, "None")
      ("Build.exeCacheDir", [ ("BLADE_EXE_CACHE", Some "on") ], None, "Some \"<localappdata>\\\\Blade\\\\exe-cache\"")
      ("Build.exeCacheDir", [ ("BLADE_EXE_CACHE", Some "OFF") ], None, "None")
      ("Build.exeCacheDir", [ ("BLADE_EXE_CACHE", Some " true ") ], None, "Some \"<localappdata>\\\\Blade\\\\exe-cache\"")
      ("Build.exeCacheDir", [ ("BLADE_EXE_CACHE", Some "yes") ], None, "None")
      ("Build.exeCacheDir", [ ("BLADE_EXE_CACHE", Some "2") ], None, "None")
      ("Build.exeCacheDir", [ ("BLADE_EXE_CACHE", Some "garbage") ], None, "None")
      ("Build.exeCacheDir", [ ("BLADE_EXE_CACHE", Some " ") ], None, "Some \"<localappdata>\\\\Blade\\\\exe-cache\"")
      ("Build.exeCacheDir", [ ("BLADE_EXE_CACHE", Some "False") ], None, "None")
      ("Build.exeCacheDir", [ ("BLADE_EXE_CACHE", Some "C:\\bt\\cache") ], None, "Some \"C:\\\\bt\\\\cache\"")
      ("Build.exeCacheDir", [ ("BLADE_EXE_CACHE", Some "rel\\dir") ], None, "None")
      ("Build.exeCacheDir", [ ("BLADE_EXE_CACHE", Some " C:\\x ") ], None, "Some \"C:\\\\x\"")
      ("Build.exeCacheDir", [ ("BLADE_EXE_CACHE", Some "/abs") ], None, "Some \"/abs\"")
      ("Build.exeCacheDir", [ ("BLADE_EXE_CACHE", Some "\\\\srv\\share") ], None, "Some \"\\\\\\\\srv\\\\share\"")
      ("Build.exeCacheVerbose", [ ("BLADE_EXE_CACHE_VERBOSE", None) ], None, "false")
      ("Build.exeCacheVerbose", [ ("BLADE_EXE_CACHE_VERBOSE", Some "") ], None, "false")
      ("Build.exeCacheVerbose", [ ("BLADE_EXE_CACHE_VERBOSE", Some "1") ], None, "true")
      ("Build.exeCacheVerbose", [ ("BLADE_EXE_CACHE_VERBOSE", Some "0") ], None, "false")
      ("Build.exeCacheVerbose", [ ("BLADE_EXE_CACHE_VERBOSE", Some "on") ], None, "true")
      ("Build.exeCacheVerbose", [ ("BLADE_EXE_CACHE_VERBOSE", Some "OFF") ], None, "false")
      ("Build.exeCacheVerbose", [ ("BLADE_EXE_CACHE_VERBOSE", Some " true ") ], None, "true")
      ("Build.exeCacheVerbose", [ ("BLADE_EXE_CACHE_VERBOSE", Some "yes") ], None, "false")
      ("Build.exeCacheVerbose", [ ("BLADE_EXE_CACHE_VERBOSE", Some "2") ], None, "false")
      ("Build.exeCacheVerbose", [ ("BLADE_EXE_CACHE_VERBOSE", Some "garbage") ], None, "false")
      ("Build.exeCacheVerbose", [ ("BLADE_EXE_CACHE_VERBOSE", Some " ") ], None, "false")
      ("Build.exeCacheVerbose", [ ("BLADE_EXE_CACHE_VERBOSE", Some "False") ], None, "false")
      ("Build.tileCacheDir", [ ("BLADE_TILE_CACHE", None) ], None, "None")
      ("Build.tileCacheDir", [ ("BLADE_TILE_CACHE", Some "") ], None, "None")
      ("Build.tileCacheDir", [ ("BLADE_TILE_CACHE", Some "1") ], None, "Some \"<localappdata>\\\\Blade\\\\tile-cache\"")
      ("Build.tileCacheDir", [ ("BLADE_TILE_CACHE", Some "0") ], None, "None")
      ("Build.tileCacheDir", [ ("BLADE_TILE_CACHE", Some "on") ], None, "Some \"<localappdata>\\\\Blade\\\\tile-cache\"")
      ("Build.tileCacheDir", [ ("BLADE_TILE_CACHE", Some "OFF") ], None, "None")
      ("Build.tileCacheDir", [ ("BLADE_TILE_CACHE", Some " true ") ], None, "Some \"<localappdata>\\\\Blade\\\\tile-cache\"")
      ("Build.tileCacheDir", [ ("BLADE_TILE_CACHE", Some "yes") ], None, "None")
      ("Build.tileCacheDir", [ ("BLADE_TILE_CACHE", Some "2") ], None, "None")
      ("Build.tileCacheDir", [ ("BLADE_TILE_CACHE", Some "garbage") ], None, "None")
      ("Build.tileCacheDir", [ ("BLADE_TILE_CACHE", Some " ") ], None, "None")
      ("Build.tileCacheDir", [ ("BLADE_TILE_CACHE", Some "False") ], None, "None")
      ("Build.tileCacheDir", [ ("BLADE_TILE_CACHE", Some "C:\\bt\\cache") ], None, "Some \"C:\\\\bt\\\\cache\"")
      ("Build.tileCacheDir", [ ("BLADE_TILE_CACHE", Some "rel\\dir") ], None, "None")
      ("Build.tileCacheDir", [ ("BLADE_TILE_CACHE", Some " C:\\x ") ], None, "Some \"C:\\\\x\"")
      ("Build.tileCacheDir", [ ("BLADE_TILE_CACHE", Some "/abs") ], None, "Some \"/abs\"")
      ("Build.tileCacheDir", [ ("BLADE_TILE_CACHE", Some "\\\\srv\\share") ], None, "Some \"\\\\\\\\srv\\\\share\"")
      ("Build.pchCacheDir", [ ("BLADE_PCH", None) ], None, "Some \"<localappdata>\\\\Blade\\\\pch-cache\"")
      ("Build.pchCacheDir", [ ("BLADE_PCH", Some "") ], None, "Some \"<localappdata>\\\\Blade\\\\pch-cache\"")
      ("Build.pchCacheDir", [ ("BLADE_PCH", Some "1") ], None, "Some \"<localappdata>\\\\Blade\\\\pch-cache\"")
      ("Build.pchCacheDir", [ ("BLADE_PCH", Some "0") ], None, "None")
      ("Build.pchCacheDir", [ ("BLADE_PCH", Some "on") ], None, "Some \"<localappdata>\\\\Blade\\\\pch-cache\"")
      ("Build.pchCacheDir", [ ("BLADE_PCH", Some "OFF") ], None, "None")
      ("Build.pchCacheDir", [ ("BLADE_PCH", Some " true ") ], None, "Some \"<localappdata>\\\\Blade\\\\pch-cache\"")
      ("Build.pchCacheDir", [ ("BLADE_PCH", Some "yes") ], None, "None")
      ("Build.pchCacheDir", [ ("BLADE_PCH", Some "2") ], None, "None")
      ("Build.pchCacheDir", [ ("BLADE_PCH", Some "garbage") ], None, "None")
      ("Build.pchCacheDir", [ ("BLADE_PCH", Some " ") ], None, "Some \"<localappdata>\\\\Blade\\\\pch-cache\"")
      ("Build.pchCacheDir", [ ("BLADE_PCH", Some "False") ], None, "None")
      ("Build.pchCacheDir", [ ("BLADE_PCH", Some "C:\\bt\\cache") ], None, "Some \"C:\\\\bt\\\\cache\"")
      ("Build.pchCacheDir", [ ("BLADE_PCH", Some "rel\\dir") ], None, "None")
      ("Build.pchCacheDir", [ ("BLADE_PCH", Some " C:\\x ") ], None, "Some \"C:\\\\x\"")
      ("Build.pchCacheDir", [ ("BLADE_PCH", Some "/abs") ], None, "Some \"/abs\"")
      ("Build.pchCacheDir", [ ("BLADE_PCH", Some "\\\\srv\\share") ], None, "Some \"\\\\\\\\srv\\\\share\"")
      ("Build.memcheckCxx", [ ("BLADE_MEMCHECK_CXX", None) ], None, "None")
      ("Build.memcheckCxx", [ ("BLADE_MEMCHECK_CXX", Some "") ], None, "None")
      ("Build.memcheckCxx", [ ("BLADE_MEMCHECK_CXX", Some "1") ], None, "Some \"1\"")
      ("Build.memcheckCxx", [ ("BLADE_MEMCHECK_CXX", Some "0") ], None, "Some \"0\"")
      ("Build.memcheckCxx", [ ("BLADE_MEMCHECK_CXX", Some "on") ], None, "Some \"on\"")
      ("Build.memcheckCxx", [ ("BLADE_MEMCHECK_CXX", Some "OFF") ], None, "Some \"OFF\"")
      ("Build.memcheckCxx", [ ("BLADE_MEMCHECK_CXX", Some " true ") ], None, "Some \" true \"")
      ("Build.memcheckCxx", [ ("BLADE_MEMCHECK_CXX", Some "yes") ], None, "Some \"yes\"")
      ("Build.memcheckCxx", [ ("BLADE_MEMCHECK_CXX", Some "2") ], None, "Some \"2\"")
      ("Build.memcheckCxx", [ ("BLADE_MEMCHECK_CXX", Some "garbage") ], None, "Some \"garbage\"")
      ("Build.memcheckCxx", [ ("BLADE_MEMCHECK_CXX", Some " ") ], None, "Some \" \"")
      ("Build.memcheckCxx", [ ("BLADE_MEMCHECK_CXX", Some "False") ], None, "Some \"False\"")
      ("Build.memcheckCxx", [ ("BLADE_MEMCHECK_CXX", Some "C:\\x\\clang++.exe") ], None, "Some \"C:\\\\x\\\\clang++.exe\"")
      ("Build.memcheckCxx", [ ("BLADE_MEMCHECK_CXX", Some " y ") ], None, "Some \" y \"")
      ("PrintForm.current", [ ("BLADE_PRINT_FORM", None) ], None, "test")
      ("PrintForm.current", [ ("BLADE_PRINT_FORM", Some "") ], None, "test")
      ("PrintForm.current", [ ("BLADE_PRINT_FORM", Some "1") ], None, "test")
      ("PrintForm.current", [ ("BLADE_PRINT_FORM", Some "0") ], None, "test")
      ("PrintForm.current", [ ("BLADE_PRINT_FORM", Some "on") ], None, "test")
      ("PrintForm.current", [ ("BLADE_PRINT_FORM", Some "OFF") ], None, "test")
      ("PrintForm.current", [ ("BLADE_PRINT_FORM", Some " true ") ], None, "test")
      ("PrintForm.current", [ ("BLADE_PRINT_FORM", Some "yes") ], None, "test")
      ("PrintForm.current", [ ("BLADE_PRINT_FORM", Some "2") ], None, "test")
      ("PrintForm.current", [ ("BLADE_PRINT_FORM", Some "garbage") ], None, "test")
      ("PrintForm.current", [ ("BLADE_PRINT_FORM", Some " ") ], None, "test")
      ("PrintForm.current", [ ("BLADE_PRINT_FORM", Some "False") ], None, "test")
      ("PrintForm.current", [ ("BLADE_PRINT_FORM", Some "test") ], None, "test")
      ("PrintForm.current", [ ("BLADE_PRINT_FORM", Some " Display ") ], None, "display")
      ("PrintForm.current", [ ("BLADE_PRINT_FORM", Some "DISPLAY") ], None, "display")
      ("PrintForm.current", [ ("BLADE_PRINT_FORM", Some "tes") ], None, "test")
      ("PrintForm.describe", [ ("BLADE_PRINT_FORM", None) ], None, "test (unset: the in-process default)")
      ("PrintForm.describe", [ ("BLADE_PRINT_FORM", Some "") ], None, "test (unset: the in-process default)")
      ("PrintForm.describe", [ ("BLADE_PRINT_FORM", Some "1") ], None, "test (BLADE_PRINT_FORM)")
      ("PrintForm.describe", [ ("BLADE_PRINT_FORM", Some "0") ], None, "test (BLADE_PRINT_FORM)")
      ("PrintForm.describe", [ ("BLADE_PRINT_FORM", Some "on") ], None, "test (BLADE_PRINT_FORM)")
      ("PrintForm.describe", [ ("BLADE_PRINT_FORM", Some "OFF") ], None, "test (BLADE_PRINT_FORM)")
      ("PrintForm.describe", [ ("BLADE_PRINT_FORM", Some " true ") ], None, "test (BLADE_PRINT_FORM)")
      ("PrintForm.describe", [ ("BLADE_PRINT_FORM", Some "yes") ], None, "test (BLADE_PRINT_FORM)")
      ("PrintForm.describe", [ ("BLADE_PRINT_FORM", Some "2") ], None, "test (BLADE_PRINT_FORM)")
      ("PrintForm.describe", [ ("BLADE_PRINT_FORM", Some "garbage") ], None, "test (BLADE_PRINT_FORM)")
      ("PrintForm.describe", [ ("BLADE_PRINT_FORM", Some " ") ], None, "test (BLADE_PRINT_FORM)")
      ("PrintForm.describe", [ ("BLADE_PRINT_FORM", Some "False") ], None, "test (BLADE_PRINT_FORM)")
      ("PrintForm.describe", [ ("BLADE_PRINT_FORM", Some "test") ], None, "test (BLADE_PRINT_FORM)")
      ("PrintForm.describe", [ ("BLADE_PRINT_FORM", Some " Display ") ], None, "display (BLADE_PRINT_FORM)")
      ("PrintForm.describe", [ ("BLADE_PRINT_FORM", Some "tes") ], None, "test (BLADE_PRINT_FORM)")
      ("Doctor.llvmClangOrigin", [ ("BLADE_LLVM_CLANG", None) ], None, "")
      ("Doctor.llvmClangOrigin", [ ("BLADE_LLVM_CLANG", Some "") ], None, "")
      ("Doctor.llvmClangOrigin", [ ("BLADE_LLVM_CLANG", Some "1") ], None, "BLADE_LLVM_CLANG [env]")
      ("Doctor.llvmClangOrigin", [ ("BLADE_LLVM_CLANG", Some "0") ], None, "BLADE_LLVM_CLANG [env]")
      ("Doctor.llvmClangOrigin", [ ("BLADE_LLVM_CLANG", Some "on") ], None, "BLADE_LLVM_CLANG [env]")
      ("Doctor.llvmClangOrigin", [ ("BLADE_LLVM_CLANG", Some "OFF") ], None, "BLADE_LLVM_CLANG [env]")
      ("Doctor.llvmClangOrigin", [ ("BLADE_LLVM_CLANG", Some " true ") ], None, "BLADE_LLVM_CLANG [env]")
      ("Doctor.llvmClangOrigin", [ ("BLADE_LLVM_CLANG", Some "yes") ], None, "BLADE_LLVM_CLANG [env]")
      ("Doctor.llvmClangOrigin", [ ("BLADE_LLVM_CLANG", Some "2") ], None, "BLADE_LLVM_CLANG [env]")
      ("Doctor.llvmClangOrigin", [ ("BLADE_LLVM_CLANG", Some "garbage") ], None, "BLADE_LLVM_CLANG [env]")
      ("Doctor.llvmClangOrigin", [ ("BLADE_LLVM_CLANG", Some " ") ], None, "BLADE_LLVM_CLANG [env]")
      ("Doctor.llvmClangOrigin", [ ("BLADE_LLVM_CLANG", Some "False") ], None, "BLADE_LLVM_CLANG [env]")
      ("Doctor.llvmClangOrigin", [ ("BLADE_LLVM_CLANG", None); ("BLADE_LLVM", Some "1") ], None, "BLADE_LLVM [env]")
      ("Doctor.llvmClangOrigin", [ ("BLADE_LLVM_CLANG", Some ""); ("BLADE_LLVM", None) ], Some [ ("BLADE_LLVM", "on") ], "BLADE_LLVM [toolchain.json]")
      ("Doctor.llvmClangOrigin", [ ("BLADE_LLVM_CLANG", Some "x"); ("BLADE_LLVM", Some "1") ], None, "BLADE_LLVM_CLANG [env]")
      ("Doctor.stdlibOrigin", [ ("BLADE_STDLIB", None) ], None, "")
      ("Doctor.stdlibOrigin", [ ("BLADE_STDLIB", Some "") ], None, "")
      ("Doctor.stdlibOrigin", [ ("BLADE_STDLIB", Some "1") ], None, "BLADE_STDLIB [env]")
      ("Doctor.stdlibOrigin", [ ("BLADE_STDLIB", Some "0") ], None, "BLADE_STDLIB [env]")
      ("Doctor.stdlibOrigin", [ ("BLADE_STDLIB", Some "on") ], None, "BLADE_STDLIB [env]")
      ("Doctor.stdlibOrigin", [ ("BLADE_STDLIB", Some "OFF") ], None, "BLADE_STDLIB [env]")
      ("Doctor.stdlibOrigin", [ ("BLADE_STDLIB", Some " true ") ], None, "BLADE_STDLIB [env]")
      ("Doctor.stdlibOrigin", [ ("BLADE_STDLIB", Some "yes") ], None, "BLADE_STDLIB [env]")
      ("Doctor.stdlibOrigin", [ ("BLADE_STDLIB", Some "2") ], None, "BLADE_STDLIB [env]")
      ("Doctor.stdlibOrigin", [ ("BLADE_STDLIB", Some "garbage") ], None, "BLADE_STDLIB [env]")
      ("Doctor.stdlibOrigin", [ ("BLADE_STDLIB", Some " ") ], None, "BLADE_STDLIB [env]")
      ("Doctor.stdlibOrigin", [ ("BLADE_STDLIB", Some "False") ], None, "BLADE_STDLIB [env]")
      ("Doctor.stdlibOrigin", [ ("BLADE_STDLIB", Some "C:\\std") ], None, "BLADE_STDLIB [env]")
      ("Grad.gradDump", [ ("BLADE_GRAD_DUMP", None) ], None, "false")
      ("Grad.gradDump", [ ("BLADE_GRAD_DUMP", Some "") ], None, "false")
      ("Grad.gradDump", [ ("BLADE_GRAD_DUMP", Some "1") ], None, "true")
      ("Grad.gradDump", [ ("BLADE_GRAD_DUMP", Some "0") ], None, "false")
      ("Grad.gradDump", [ ("BLADE_GRAD_DUMP", Some "on") ], None, "true")
      ("Grad.gradDump", [ ("BLADE_GRAD_DUMP", Some "OFF") ], None, "true")
      ("Grad.gradDump", [ ("BLADE_GRAD_DUMP", Some " true ") ], None, "true")
      ("Grad.gradDump", [ ("BLADE_GRAD_DUMP", Some "yes") ], None, "true")
      ("Grad.gradDump", [ ("BLADE_GRAD_DUMP", Some "2") ], None, "true")
      ("Grad.gradDump", [ ("BLADE_GRAD_DUMP", Some "garbage") ], None, "true")
      ("Grad.gradDump", [ ("BLADE_GRAD_DUMP", Some " ") ], None, "true")
      ("Grad.gradDump", [ ("BLADE_GRAD_DUMP", Some "False") ], None, "true")
      ("Grad.gradDump", [ ("BLADE_GRAD_DUMP", Some " 0 ") ], None, "true")
      ("GrRender.explicitPath", [ ("BLADE_GR_RENDER", None) ], None, "None")
      ("GrRender.explicitPath", [ ("BLADE_GR_RENDER", Some "") ], None, "None")
      ("GrRender.explicitPath", [ ("BLADE_GR_RENDER", Some "1") ], None, "Some \"1\"")
      ("GrRender.explicitPath", [ ("BLADE_GR_RENDER", Some "0") ], None, "Some \"0\"")
      ("GrRender.explicitPath", [ ("BLADE_GR_RENDER", Some "on") ], None, "Some \"on\"")
      ("GrRender.explicitPath", [ ("BLADE_GR_RENDER", Some "OFF") ], None, "Some \"OFF\"")
      ("GrRender.explicitPath", [ ("BLADE_GR_RENDER", Some " true ") ], None, "Some \"true\"")
      ("GrRender.explicitPath", [ ("BLADE_GR_RENDER", Some "yes") ], None, "Some \"yes\"")
      ("GrRender.explicitPath", [ ("BLADE_GR_RENDER", Some "2") ], None, "Some \"2\"")
      ("GrRender.explicitPath", [ ("BLADE_GR_RENDER", Some "garbage") ], None, "Some \"garbage\"")
      ("GrRender.explicitPath", [ ("BLADE_GR_RENDER", Some " ") ], None, "None")
      ("GrRender.explicitPath", [ ("BLADE_GR_RENDER", Some "False") ], None, "Some \"False\"")
      ("GrRender.explicitPath", [ ("BLADE_GR_RENDER", Some " C:\\g\\gr-render.exe ") ], None, "Some \"C:\\\\g\\\\gr-render.exe\"")
      ("GrRender.explicitPath", [ ("BLADE_GR_RENDER", Some "	") ], None, "None")
      ("Setup.priorFileVar", [ ("BLADE_TOOLCHAIN_FILE", None) ], None, "null")
      ("Setup.priorFileVar", [ ("BLADE_TOOLCHAIN_FILE", Some "") ], None, "\"\"")
      ("Setup.priorFileVar", [ ("BLADE_TOOLCHAIN_FILE", Some "1") ], None, "\"1\"")
      ("Setup.priorFileVar", [ ("BLADE_TOOLCHAIN_FILE", Some "0") ], None, "\"0\"")
      ("Setup.priorFileVar", [ ("BLADE_TOOLCHAIN_FILE", Some "on") ], None, "\"on\"")
      ("Setup.priorFileVar", [ ("BLADE_TOOLCHAIN_FILE", Some "OFF") ], None, "\"OFF\"")
      ("Setup.priorFileVar", [ ("BLADE_TOOLCHAIN_FILE", Some " true ") ], None, "\" true \"")
      ("Setup.priorFileVar", [ ("BLADE_TOOLCHAIN_FILE", Some "yes") ], None, "\"yes\"")
      ("Setup.priorFileVar", [ ("BLADE_TOOLCHAIN_FILE", Some "2") ], None, "\"2\"")
      ("Setup.priorFileVar", [ ("BLADE_TOOLCHAIN_FILE", Some "garbage") ], None, "\"garbage\"")
      ("Setup.priorFileVar", [ ("BLADE_TOOLCHAIN_FILE", Some " ") ], None, "\" \"")
      ("Setup.priorFileVar", [ ("BLADE_TOOLCHAIN_FILE", Some "False") ], None, "\"False\"")
      ("Setup.priorFileVar", [ ("BLADE_TOOLCHAIN_FILE", Some "C:\\t\\a.json") ], None, "\"C:\\\\t\\\\a.json\"")
      ("PrintForm.applyCliDefault.flag", [ ("BLADE_PRINT_FORM", None) ], None, "Ok display env=\"display\" describe=display (the default of the run / compile / emit / plan verbs)")
      ("PrintForm.applyCliDefault.flag", [ ("BLADE_PRINT_FORM", None) ], Some [ ("BLADE_PRINT_FORM", "display") ], "Ok display env=\"display\" describe=display (blade.toolchain.json)")
      ("PrintForm.applyCliDefault.flag", [ ("BLADE_PRINT_FORM", None) ], Some [ ("BLADE_PRINT_FORM", "nope") ], "Error BLADE_PRINT_FORM in blade.toolchain.json: 'nope' is not a print form (expected test or display) env=null")
      ("PrintForm.applyCliDefault.flag", [ ("BLADE_PRINT_FORM", Some "") ], None, "Ok display env=\"display\" describe=display (the default of the run / compile / emit / plan verbs)")
      ("PrintForm.applyCliDefault.flag", [ ("BLADE_PRINT_FORM", Some "") ], Some [ ("BLADE_PRINT_FORM", "display") ], "Ok display env=\"display\" describe=display (blade.toolchain.json)")
      ("PrintForm.applyCliDefault.flag", [ ("BLADE_PRINT_FORM", Some "") ], Some [ ("BLADE_PRINT_FORM", "nope") ], "Error BLADE_PRINT_FORM in blade.toolchain.json: 'nope' is not a print form (expected test or display) env=\"\"")
      ("PrintForm.applyCliDefault.flag", [ ("BLADE_PRINT_FORM", Some "display") ], None, "Ok display env=\"display\" describe=display (--print-form)")
      ("PrintForm.applyCliDefault.flag", [ ("BLADE_PRINT_FORM", Some "display") ], Some [ ("BLADE_PRINT_FORM", "display") ], "Ok display env=\"display\" describe=display (--print-form)")
      ("PrintForm.applyCliDefault.flag", [ ("BLADE_PRINT_FORM", Some "display") ], Some [ ("BLADE_PRINT_FORM", "nope") ], "Ok display env=\"display\" describe=display (--print-form)")
      ("PrintForm.applyCliDefault.flag", [ ("BLADE_PRINT_FORM", Some " TEST ") ], None, "Ok test env=\" TEST \" describe=test (--print-form)")
      ("PrintForm.applyCliDefault.flag", [ ("BLADE_PRINT_FORM", Some " TEST ") ], Some [ ("BLADE_PRINT_FORM", "display") ], "Ok test env=\" TEST \" describe=test (--print-form)")
      ("PrintForm.applyCliDefault.flag", [ ("BLADE_PRINT_FORM", Some " TEST ") ], Some [ ("BLADE_PRINT_FORM", "nope") ], "Ok test env=\" TEST \" describe=test (--print-form)")
      ("PrintForm.applyCliDefault.flag", [ ("BLADE_PRINT_FORM", Some "bogus") ], None, "Error --print-form: 'bogus' is not a print form (expected test or display) env=\"bogus\"")
      ("PrintForm.applyCliDefault.flag", [ ("BLADE_PRINT_FORM", Some "bogus") ], Some [ ("BLADE_PRINT_FORM", "display") ], "Error --print-form: 'bogus' is not a print form (expected test or display) env=\"bogus\"")
      ("PrintForm.applyCliDefault.flag", [ ("BLADE_PRINT_FORM", Some "bogus") ], Some [ ("BLADE_PRINT_FORM", "nope") ], "Error --print-form: 'bogus' is not a print form (expected test or display) env=\"bogus\"")
      ("PrintForm.applyCliDefault.env", [ ("BLADE_PRINT_FORM", None) ], None, "Ok display env=\"display\" describe=display (the default of the run / compile / emit / plan verbs)")
      ("PrintForm.applyCliDefault.env", [ ("BLADE_PRINT_FORM", None) ], Some [ ("BLADE_PRINT_FORM", "test") ], "Ok test env=\"test\" describe=test (blade.toolchain.json)")
      ("PrintForm.applyCliDefault.env", [ ("BLADE_PRINT_FORM", None) ], Some [ ("BLADE_PRINT_FORM", "nope") ], "Error BLADE_PRINT_FORM in blade.toolchain.json: 'nope' is not a print form (expected test or display) env=null")
      ("PrintForm.applyCliDefault.env", [ ("BLADE_PRINT_FORM", Some "display") ], None, "Ok display env=\"display\" describe=display (BLADE_PRINT_FORM)")
      ("PrintForm.applyCliDefault.env", [ ("BLADE_PRINT_FORM", Some "display") ], Some [ ("BLADE_PRINT_FORM", "test") ], "Ok display env=\"display\" describe=display (BLADE_PRINT_FORM)")
      ("PrintForm.applyCliDefault.env", [ ("BLADE_PRINT_FORM", Some "display") ], Some [ ("BLADE_PRINT_FORM", "nope") ], "Ok display env=\"display\" describe=display (BLADE_PRINT_FORM)")
      ("PrintForm.applyCliDefault.env", [ ("BLADE_PRINT_FORM", Some "bogus") ], None, "Error BLADE_PRINT_FORM: 'bogus' is not a print form (expected test or display) env=\"bogus\"")
      ("PrintForm.applyCliDefault.env", [ ("BLADE_PRINT_FORM", Some "bogus") ], Some [ ("BLADE_PRINT_FORM", "test") ], "Error BLADE_PRINT_FORM: 'bogus' is not a print form (expected test or display) env=\"bogus\"")
      ("PrintForm.applyCliDefault.env", [ ("BLADE_PRINT_FORM", Some "bogus") ], Some [ ("BLADE_PRINT_FORM", "nope") ], "Error BLADE_PRINT_FORM: 'bogus' is not a print form (expected test or display) env=\"bogus\"")
    ]

// ---------------------------------------------------------------------------
// docs/reference/knobs.md
// ---------------------------------------------------------------------------

let private kindTitle (k: Knobs.Kind) =
    match k with
    | Knobs.OptimizerGate -> "Optimizer gates"
    | Knobs.AbArm -> "Emission A/B arms"
    | Knobs.Policy -> "Policies"
    | Knobs.EmissionForm -> "Print selection and form"
    | Knobs.Instrumentation -> "Instrumentation"
    | Knobs.Infrastructure -> "Infrastructure"
    | Knobs.TestHarness -> "Test harness"

let private kindName (k: Knobs.Kind) =
    match k with
    | Knobs.OptimizerGate -> "optimizer gate"
    | Knobs.AbArm -> "A/B arm"
    | Knobs.Policy -> "policy"
    | Knobs.EmissionForm -> "emission form"
    | Knobs.Instrumentation -> "instrumentation"
    | Knobs.Infrastructure -> "infrastructure"
    | Knobs.TestHarness -> "test harness"

let private kindOrder =
    [ Knobs.Policy; Knobs.EmissionForm; Knobs.OptimizerGate; Knobs.AbArm; Knobs.Instrumentation
      Knobs.Infrastructure; Knobs.TestHarness ]

let private cell (s: string) = s.Replace("|", "\\|")

let private spell (s: string) =
    s.Split([| " / " |], StringSplitOptions.None) |> Array.map (fun p -> "`" + p + "`") |> String.concat " / "

/// The reference page, LF line endings.
let renderReference () : string =
    let sb = System.Text.StringBuilder()
    let line (s: string) = sb.Append(s).Append('\n') |> ignore
    line "# Environment knobs"
    line ""
    line "GENERATED from the registry in `src/Knobs.fs` by `blade test knobs --write`; do not edit by hand."
    line "`blade test knobs` fails when this page and the registry disagree."
    line ""
    line ("Every environment variable the compiler, the programs it compiles and the test harness read, "
          + $"{Knobs.all.Length} in all, grouped by kind. `blade test` is what the suite does with the knob for its run "
          + "(cleared, pinned to a value, or left alone); `toolchain` says whether `blade.toolchain.json` is read when the "
          + "variable is unset (`file`) and whether bare `blade setup` persists it (`persisted`). A knob marked "
          + "*no notes* has no row in the hand-written notes, `docs/reference/knob-notes.md`.")
    for kind in kindOrder do
        let ks = Knobs.all |> List.filter (fun k -> k.Kind = kind)
        if not ks.IsEmpty then
            line ""
            line ("## " + kindTitle kind)
            line ""
            line "| Knob | Kind | Default | Values | Read by | `blade test` | toolchain | What it does |"
            line "|---|---|---|---|---|---|---|---|"
            for k in ks do
                let values =
                    k.Values
                    |> List.map (fun v -> spell v.Spell + " " + v.Means + (if v.Mirror then " (opt-diff mirror arm)" else ""))
                    |> String.concat "; "
                let readers =
                    k.Readers
                    |> List.map (function Knobs.Compiler -> "compiler" | Knobs.CompiledProgram -> "compiled program" | Knobs.Harness -> "harness")
                    |> String.concat ", "
                let suite =
                    match k.Suite with
                    | Knobs.Untouched -> "left alone"
                    | Knobs.Cleared -> "cleared"
                    | Knobs.Pinned p -> $"pinned `{p}`"
                    | Knobs.ClearedAndPinned p -> $"cleared, pinned `{p}`"
                let tc =
                    match k.Toolchain with
                    | Knobs.EnvOnly -> "env only"
                    | Knobs.FileFallback -> "file"
                    | Knobs.Persisted -> "file, persisted"
                let text = if k.Documented then k.Text else k.Text + " *No notes.*"
                line ("| `" + k.Name + "` | " + kindName k.Kind + " | " + cell k.Default + " | " + cell values + " | "
                      + readers + " | " + suite + " | " + tc + " | " + cell text + " |")
    sb.ToString()

// ---------------------------------------------------------------------------
// The block.
// ---------------------------------------------------------------------------

/// The files whose knob reads K16 moved onto the registry.
let private convertedFiles =
    [ "src/Knobs.fs"; "src/Toolchain.fs"; "src/Build.fs"; "src/PrintForm.fs"; "src/Doctor.fs"; "src/Setup.fs"
      "src/Grad.fs"; "src/display/GrRender.fs" ]

/// The BLADE_* names in the first column of the notes table (docs/reference/knob-notes.md, its
/// "Notes by knob" section), plus its non-BLADE rows.
let private notesTableNames (notes: string) : string list =
    let lines = notes.Replace("\r\n", "\n").Split('\n')
    let start = lines |> Array.tryFindIndex (fun l -> l.StartsWith "## Notes by knob")
    match start with
    | None -> []
    | Some s ->
        lines
        |> Array.skip (s + 1)
        |> Array.takeWhile (fun l -> not (l.StartsWith "## "))
        |> Array.filter (fun l -> l.StartsWith "| `")
        |> Array.collect (fun l ->
            let first = l.Split([| " | " |], StringSplitOptions.None).[0]
            [| for m in Regex.Matches(first, "`([A-Z][A-Z0-9_]+)`") -> m.Groups.[1].Value |])
        |> Array.distinct
        |> Array.toList

/// The tables whose readers touch PrintForm's process state (`describe` reads
/// the CLI resolution's recorded source; `applyCliDefault` records it): run by
/// the standalone `blade test knobs` only, after everything else, because an
/// earlier in-process `blade run` in the full suite may have set that source.
let private statefulReaders =
    set [ "PrintForm.describe"; "PrintForm.applyCliDefault.flag"; "PrintForm.applyCliDefault.env" ]

/// The checks. `stateful` adds the PrintForm-state tables (standalone only).
let private runChecks (blockName: string) (stateful: bool) (suiteCleared: string list)
                      (suitePinned: (string * string) list) (args: string list) : BlockResult =
    printHeader (blockName + ": the environment-knob registry (src/Knobs.fs)")
    let mutable passed = 0
    let mutable failed = 0
    let mutable failedNames = []
    let check name cond detail =
        if cond then
            passed <- passed + 1
            resultLine Pass name detail
        else
            failed <- failed + 1
            failedNames <- failedNames @ [ name ]
            resultLine Fail name detail
    let regen = "then run `blade test knobs --write` and commit docs/reference/knobs.md"
    // A registry set against a hand-kept list, each side's extra names with the fix.
    let setDiff (label: string) (listName: string) (registry: string list) (witness: string list) =
        let r = Set.ofList registry
        let w = Set.ofList witness
        let extra = Set.difference r w |> Set.toList
        let missing = Set.difference w r |> Set.toList
        let parts =
            [ if not missing.IsEmpty then
                  yield $"""in {listName} but not so in src/Knobs.fs: {String.concat ", " missing} -- add or correct the entry in src/Knobs.fs, {regen}"""
              if not extra.IsEmpty then
                  yield $"""so in src/Knobs.fs but not in {listName}: {String.concat ", " extra} -- correct the entry in src/Knobs.fs (or the list), {regen}""" ]
        check label parts.IsEmpty (if parts.IsEmpty then $"{r.Count} names" else String.concat "; " parts)

    let root = DocTests.tryDocRoot ()

    // ---- --write (standalone only): regenerate the page first ----
    match root with
    | Some r when List.contains "--write" args ->
        let path = Path.Combine(r, "docs", "reference", "knobs.md")
        Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
        File.WriteAllText(path, renderReference (), System.Text.UTF8Encoding(false))
        printfn "  wrote %s" path
    | _ -> ()

    // ---- 1. the old-versus-new tables ----
    let runTables (pick: string -> bool) =
        let byReader = tables |> List.filter (fun (r, _, _, _) -> pick r) |> List.groupBy (fun (r, _, _, _) -> r)
        for (readerName, rows) in byReader do
            match readers |> List.tryFind (fun (n, _) -> n = readerName) with
            | None -> check $"table {readerName}" false "no new reader of that name in tests/KnobsTests.fs `readers`"
            | Some (_, f) ->
                let bad =
                    rows
                    |> List.choose (fun (_, env, file, expected) ->
                        let got = withRow env file (f env)
                        if got = expected then None
                        else
                            let envText = env |> List.map (fun (k, v) -> $"{k}={fmtOpt v}") |> String.concat " "
                            Some $"{envText}: old {expected}, new {got}")
                check $"table {readerName}" bad.IsEmpty
                    (if bad.IsEmpty then $"{rows.Length} rows equal the old reader's"
                     else "a knob reader parses differently from the recorded old one: " + String.concat "; " (List.truncate 4 bad))
    runTables (fun r -> not (statefulReaders.Contains r))
    check "every new reader has a table"
        (readers |> List.forall (fun (n, _) -> tables |> List.exists (fun (r, _, _, _) -> r = n)))
        $"{readers.Length} readers, {tables.Length} rows"

    // ---- 2. the registry ----
    let names = Knobs.all |> List.map (fun k -> k.Name)
    let dups = names |> List.countBy id |> List.filter (fun (_, c) -> c > 1) |> List.map fst
    check "registry: names unique" dups.IsEmpty
        (if dups.IsEmpty then $"{names.Length} knobs" else $"""duplicated in src/Knobs.fs `all`: {String.concat ", " dups}""")
    let longText = Knobs.all |> List.filter (fun k -> k.Text.Length = 0 || k.Text.Length >= 160) |> List.map (fun k -> k.Name)
    check "registry: one line under 160 characters each" longText.IsEmpty
        (if longText.IsEmpty then "" else $"""shorten the Text of {String.concat ", " longText} in src/Knobs.fs, {regen}""")
    let noValues = Knobs.all |> List.filter (fun k -> k.Values.IsEmpty || k.Default = "") |> List.map (fun k -> k.Name)
    check "registry: a default and at least one value each" noValues.IsEmpty
        (if noValues.IsEmpty then "" else $"""give {String.concat ", " noValues} a Default and Values in src/Knobs.fs, {regen}""")
    let strayMirror =
        Knobs.all |> List.filter (fun k -> k.Kind <> Knobs.OptimizerGate && k.Values |> List.exists (fun v -> v.Mirror))
        |> List.map (fun k -> k.Name)
    check "registry: mirror arms only on optimizer gates" strayMirror.IsEmpty
        (if strayMirror.IsEmpty then "" else $"""{String.concat ", " strayMirror}: a mirror value on a knob whose Kind is not OptimizerGate""")
    check "registry: Knobs.tryFind finds every entry"
        (Knobs.all |> List.forall (fun k -> Knobs.tryFind k.Name = Some k)) ""
    setDiff "registry optimizer gates = Optimize.optimizerGates" "Optimize.optimizerGates (Kind = OptimizerGate)"
        (Knobs.all |> List.filter (fun k -> k.Kind = Knobs.OptimizerGate) |> List.map (fun k -> k.Name))
        Optimize.optimizerGates
    check "Optimize.optimizerGates has no duplicate"
        (List.length (List.distinct Optimize.optimizerGates) = Optimize.optimizerGates.Length) ""
    setDiff "registry mirror arms = Optimize.optimizerGateMirrors" "Optimize.optimizerGateMirrors (a `mirror` value)"
        (Knobs.all |> List.collect (fun k -> k.Values |> List.filter (fun v -> v.Mirror) |> List.map (fun v -> k.Name + "=" + v.Spell)))
        (Optimize.optimizerGateMirrors |> List.map (fun (k, v) -> k + "=" + v))
    setDiff "registry cleared knobs = suiteClearedKnobs" "CliSelfTests.suiteClearedKnobs (Suite = Cleared / ClearedAndPinned)"
        (Knobs.all
         |> List.filter (fun k -> match k.Suite with Knobs.Cleared | Knobs.ClearedAndPinned _ -> true | _ -> false)
         |> List.map (fun k -> k.Name))
        suiteCleared
    setDiff "registry pinned knobs = suitePinnedKnobs" "CliSelfTests.suitePinnedKnobs (Suite = Pinned / ClearedAndPinned)"
        (Knobs.all
         |> List.choose (fun k -> match k.Suite with Knobs.Pinned p | Knobs.ClearedAndPinned p -> Some (k.Name + "=" + p) | _ -> None))
        (suitePinned |> List.map (fun (k, v) -> k + "=" + v))
    setDiff "registry persisted keys = Setup.persistedToolchainKeys" "Setup.persistedToolchainKeys (Toolchain = Persisted)"
        (Knobs.all |> List.filter (fun k -> k.Toolchain = Knobs.Persisted) |> List.map (fun k -> k.Name))
        Setup.persistedToolchainKeys

    match root with
    | None ->
        resultLine Skip "registry vs docs/reference/knob-notes.md, raw reads, docs/reference/knobs.md" "no repository root (docs/formalism.md + CLAUDE.md) above the working directory or the binary"
    | Some r ->
        let notes = File.ReadAllText(Path.Combine(r, "docs", "reference", "knob-notes.md"))
        let inNotes (name: string) = Regex.IsMatch(notes, "(?<![A-Z0-9_])" + Regex.Escape name + "(?![A-Z0-9_])")
        let wrongDoc =
            Knobs.all |> List.filter (fun k -> inNotes k.Name <> k.Documented)
            |> List.map (fun k ->
                if k.Documented then $"{k.Name} is marked Documented in src/Knobs.fs but docs/reference/knob-notes.md does not name it (set Documented = false)"
                else $"{k.Name} is named in docs/reference/knob-notes.md but marked Documented = false in src/Knobs.fs (set it true)")
        check "registry: Documented = named in docs/reference/knob-notes.md" wrongDoc.IsEmpty
            (if wrongDoc.IsEmpty then "" else String.concat "; " wrongDoc + "; " + regen)
        let tableNames = notesTableNames notes
        let notInRegistry = tableNames |> List.filter (fun n -> (Knobs.tryFind n).IsNone)
        check "knob-notes.md table: every row's knob is in the registry"
            (not tableNames.IsEmpty && notInRegistry.IsEmpty)
            (if tableNames.IsEmpty then "no knob table found under the `## Notes by knob` heading of docs/reference/knob-notes.md"
             elif notInRegistry.IsEmpty then $"{tableNames.Length} names"
             else $"""in the knob-notes.md table but not in src/Knobs.fs: {String.concat ", " notInRegistry} -- add an entry to src/Knobs.fs (and to `all`), {regen}""")

        // ---- 3. no raw read of a registered knob in the converted files ----
        let rawRead =
            Regex("GetEnvironmentVariable\\s*\\(?\\s*\"(" + (names |> List.map Regex.Escape |> String.concat "|") + ")\"")
        let offenders =
            convertedFiles
            |> List.collect (fun rel ->
                let p = Path.Combine(r, rel)
                if not (File.Exists p) then [ $"{rel}: missing" ]
                else
                    File.ReadAllLines p
                    |> Array.mapi (fun i l -> (i + 1, l))
                    |> Array.filter (fun (_, l) -> rawRead.IsMatch l)
                    |> Array.map (fun (i, _) -> $"{rel}:{i}")
                    |> Array.toList)
        check "converted files read no registered knob by a literal name" offenders.IsEmpty
            (if offenders.IsEmpty then ""
             else String.concat ", " offenders + " -- read it through the registry (Knobs.raw / Knobs.flag / Knobs.switch with the entry)")

        // ---- 4. the generated page is in sync ----
        let page = Path.Combine(r, "docs", "reference", "knobs.md")
        let onDisk = if File.Exists page then File.ReadAllText(page).Replace("\r\n", "\n") else ""
        check "docs/reference/knobs.md is the registry's rendering"
            (onDisk = renderReference ())
            (if onDisk = renderReference () then ""
             elif File.Exists page then "the page is stale: run `blade test knobs --write` and commit docs/reference/knobs.md"
             else "the page is missing: run `blade test knobs --write` and commit docs/reference/knobs.md")

    // ---- last, standalone only: the tables that touch PrintForm's process state ----
    if stateful then runTables statefulReaders.Contains

    printFooter blockName [ $"{passed} passed"; $"{failed} failed" ]
    { Block = blockName; Passed = passed; Failed = failed; Skipped = 0; FailedNames = failedNames }

/// The default suite's block: every check that holds no process state -- the
/// registry against the hand-written notes and the hand-kept lists, the literal-read check,
/// the generated page, and the old-versus-new tables except PrintForm's.
let runKnobsSyncTests (suiteCleared: string list) (suitePinned: (string * string) list) : BlockResult =
    runChecks "Knobs Sync" false suiteCleared suitePinned []

/// `blade test knobs [--write]`: the sync checks plus the PrintForm-state tables.
let runKnobsTests (suiteCleared: string list) (suitePinned: (string * string) list) (args: string list) : BlockResult =
    runChecks "Knobs" true suiteCleared suitePinned args
