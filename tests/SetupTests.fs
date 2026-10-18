// Unit pins for `blade setup`'s pure halves (src/Setup.fs): argument
// parsing, and the toolchain-file merge/remove roundtrip through the SAME
// reader the gates use (Toolchain.get with BLADE_TOOLCHAIN_FILE redirected
// to a temp file). The configuring modes' verify-then-persist behavior runs
// real probes and is exercised manually / by doctor; nothing here touches
// the network, git, or make.
module Blade.Tests.SetupTests

open System
open System.IO
open Blade
open Blade.Tests.TestHarness

let private pinEnv (name: string) (value: string) =
    let prior = System.Environment.GetEnvironmentVariable(name)
    System.Environment.SetEnvironmentVariable(name, value)
    { new System.IDisposable with
        member _.Dispose() = System.Environment.SetEnvironmentVariable(name, prior) }

let runSetupTests () : BlockResult =
    printHeader "Setup"
    let mutable passed = 0
    let mutable failed = 0
    let mutable failedNames = []
    let check name cond detail =
        if cond then
            passed <- passed + 1
            resultLine Pass name detail
        else
            failed <- failed + 1
            failedNames <- failedNames @ [name]
            resultLine Fail name detail

    // ---- parseArgs ----
    check "no args -> BlasUnspecified"
        (match Setup.parseArgs [] with
         | Ok o -> o.Blas = Setup.BlasUnspecified && not o.Force
         | Error _ -> false) "defaults"
    check "--blas=source --jobs 8 --force"
        (match Setup.parseArgs ["--blas=source"; "--jobs"; "8"; "--force"] with
         | Ok o -> o.Blas = Setup.BlasSource && o.Jobs = Some 8 && o.Force
         | Error _ -> false) "= and space forms mix"
    check "--blas prebuilt --blas-dir DIR"
        (match Setup.parseArgs ["--blas"; "prebuilt"; "--blas-dir"; "/opt/openblas"] with
         | Ok o -> o.Blas = Setup.BlasPrebuilt && o.BlasDir = Some "/opt/openblas"
         | Error _ -> false) "space form"
    check "--blas-link with include and flavor"
        (match Setup.parseArgs ["--blas=prebuilt"; "--blas-link=-lmkl_rt"; "--blas-include=/opt/mkl/include"; "--flavor=MKL"] with
         | Ok o -> o.BlasLink = Some "-lmkl_rt" && o.BlasInclude = Some "/opt/mkl/include" && o.Flavor = Some "mkl"
         | Error _ -> false) "flavor lowercased"
    check "--blas=weird rejected"
        (Setup.parseArgs ["--blas=weird"]).IsError "mode validation"
    check "--jobs x rejected"
        (Setup.parseArgs ["--jobs"; "x"]).IsError "int validation"
    check "trailing valueless flag rejected"
        (Setup.parseArgs ["--blas-link"]).IsError "missing value"

    // ---- writeToolchain roundtrip through the live reader ----
    let tmp = Path.Combine(Path.GetTempPath(), $"blade_setup_rt_{(System.Diagnostics.Process.GetCurrentProcess().Id)}.json")
    do
        use _t = pinEnv "BLADE_TOOLCHAIN_FILE" tmp
        try
            Setup.writeToolchain ["BLADE_TEST_KEY_A", Some "1"; "BLADE_TEST_KEY_B", Some "two"] |> ignore
            check "write -> reader sees both keys"
                (Toolchain.get "BLADE_TEST_KEY_A" = Some "1" && Toolchain.get "BLADE_TEST_KEY_B" = Some "two")
                "cache refreshed on write"
            Setup.writeToolchain ["BLADE_TEST_KEY_A", None; "BLADE_TEST_KEY_C", Some "3"] |> ignore
            check "merge preserves, None removes"
                (Toolchain.get "BLADE_TEST_KEY_A" = None
                 && Toolchain.get "BLADE_TEST_KEY_B" = Some "two"
                 && Toolchain.get "BLADE_TEST_KEY_C" = Some "3")
                "setup composes with hand-edits"
            do
                use _e = pinEnv "BLADE_TEST_KEY_B" "env-wins"
                check "env still beats the written file"
                    (Toolchain.get "BLADE_TEST_KEY_B" = Some "env-wins")
                    "precedence unchanged by setup"
        finally
            try File.Delete tmp with _ -> ()
            Toolchain.refresh ()

    // ---- ONE switch grammar for every BLADE_* on/off knob (Toolchain.envFlag) ----
    // Trimmed, case-insensitive 1/on/true vs 0/off/false; unset, empty or an
    // unrecognized value reads as the knob's DEFAULT. BLADE_MEMCHECK=off used
    // to turn memcheck ON (anything but "0" was on), and BLADE_LLVM / _CUBLAS /
    // _FP_REASSOC refused ` ON ` / `true`.
    for (raw, expected) in
            [ "1", Toolchain.SwitchOn; " On ", Toolchain.SwitchOn; "TRUE", Toolchain.SwitchOn
              "0", Toolchain.SwitchOff; "off", Toolchain.SwitchOff; " False", Toolchain.SwitchOff
              "", Toolchain.SwitchUnset; "   ", Toolchain.SwitchUnset
              "C:/x", Toolchain.SwitchOther "C:/x" ] do
        check $"switch grammar: '{raw}'" (Toolchain.parseSwitch (Some raw) = expected) (sprintf "%A" (Toolchain.parseSwitch (Some raw)))
    check "switch grammar: unset" (Toolchain.parseSwitch None = Toolchain.SwitchUnset) ""
    for (var, gate) in
            [ "BLADE_MEMCHECK", Blade.CodeGenState.memcheckEnabled
              "BLADE_LLVM", Blade.Build.llvmEnabled
              "BLADE_FP_REPRO", Blade.CodeGenState.fpReproEnabled
              "BLADE_CUBLAS", Blade.LinAlgPatterns.cublasAvailable ] do
        let reads (v: string) =
            use _p = pinEnv var v
            gate ()
        check $"{var}: off / 0 / false / unset / garbage are off; ON / true / 1 are on"
            (not (reads "off") && not (reads "0") && not (reads " FALSE ") && not (reads null)
             && not (reads "yes") && reads " ON " && reads "true" && reads "1")
            ""
    // BLADE_FP_REASSOC is DEFAULT-ON since 2026-10-17 (the licensed fold forms
    // are the default; plan-fp-default-fastest.md): unset and garbage read as
    // the default, and only an off-word turns it off -- the same grammar,
    // mirrored. Its off-words are the compatibility spelling of BLADE_FP_REPRO=1.
    (let reads (v: string) =
        use _p = pinEnv "BLADE_FP_REASSOC" v
        Blade.CodeGenState.fpReassocEnabled ()
     check "BLADE_FP_REASSOC: unset / garbage / ON / true / 1 are on; off / 0 / false are off"
        (reads null && reads "yes" && reads " ON " && reads "true" && reads "1"
         && not (reads "off") && not (reads "0") && not (reads " FALSE "))
        "")
    (let both (repro: string) (reassoc: string) =
        use _a = pinEnv "BLADE_FP_REPRO" repro
        use _b = pinEnv "BLADE_FP_REASSOC" reassoc
        (Blade.CodeGenState.fpReproEnabled (), Blade.CodeGenState.fpReassocEnabled ())
     check "BLADE_FP_REPRO=1 outranks BLADE_FP_REASSOC=1; BLADE_FP_REASSOC=0 is repro"
        (both "1" "1" = (true, false) && both null "0" = (true, false) && both null null = (false, true))
        "")

    // ---- package hints are data for every OS (never empty) ----
    check "package hints exist for the deps setup names"
        (["openblas"; "make"; "netcdf"; "mpi"]
         |> List.forall (fun d -> (Setup.packageHint d).Length > 0))
        "per-OS table rows"

    printFooter "Setup" [$"{passed} passed"; $"{failed} failed"]
    { Block = "Setup"; Passed = passed; Failed = failed; Skipped = 0; FailedNames = failedNames }
