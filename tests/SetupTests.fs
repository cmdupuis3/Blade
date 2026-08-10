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
        (match Setup.parseArgs ["--blas=weird"] with Error _ -> true | Ok _ -> false) "mode validation"
    check "--jobs x rejected"
        (match Setup.parseArgs ["--jobs"; "x"] with Error _ -> true | Ok _ -> false) "int validation"
    check "trailing valueless flag rejected"
        (match Setup.parseArgs ["--blas-link"] with Error _ -> true | Ok _ -> false) "missing value"

    // ---- writeToolchain roundtrip through the live reader ----
    let tmp = Path.Combine(Path.GetTempPath(), sprintf "blade_setup_rt_%d.json" (System.Diagnostics.Process.GetCurrentProcess().Id))
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

    // ---- package hints are data for every OS (never empty) ----
    check "package hints exist for the deps setup names"
        (["openblas"; "make"; "netcdf"; "mpi"]
         |> List.forall (fun d -> (Setup.packageHint d).Length > 0))
        "per-OS table rows"

    printFooter "Setup" [sprintf "%d passed" passed; sprintf "%d failed" failed]
    { Block = "Setup"; Passed = passed; Failed = failed; Skipped = 0; FailedNames = failedNames }
