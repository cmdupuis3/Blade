// Structural pins for `blade doctor` (src/Doctor.fs).
//
// The doctor's probes run FOR REAL here -- a hello compile, the netcdf/mpi
// link probes, tool version queries -- but their statuses are machine facts
// (this box has g++; CI may not), so statuses of real probes are
// deliberately NOT asserted. What IS pinned: the row set and order (the
// extension's --json consumer keys off them), every row carrying a detail,
// the JSON shape parsing, and the one deterministic status we create
// ourselves by pinning the environment surface (BLADE_BLAS=0 -> the blas
// row must be `off` with an env origin) -- which also proves doctor reads
// the SAME gate the compiler does.
module Blade.Tests.DoctorTests

open System
open System.IO
open Blade
open Blade.Tests.TestHarness

/// Same use-guard idiom as LinAlgTests.pinEnv (private there).
let private pinEnv (name: string) (value: string) =
    let prior = System.Environment.GetEnvironmentVariable(name)
    System.Environment.SetEnvironmentVariable(name, value)
    { new System.IDisposable with
        member _.Dispose() = System.Environment.SetEnvironmentVariable(name, prior) }

let runDoctorTests () : BlockResult =
    printHeader "Doctor"
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

    // Deterministic surface: BLAS explicitly off, no toolchain file (pinned
    // to a NONEXISTENT path -- null would fall back to a blade.toolchain.json
    // beside the binary, which a configured machine may genuinely have).
    use _a = pinEnv "BLADE_BLAS" "0"
    use _b = pinEnv "BLADE_BLAS_LINK" null
    use _c = pinEnv "OPENBLAS_DIR" null
    use _d = pinEnv "BLADE_TOOLCHAIN_FILE" (Path.Combine(Path.GetTempPath(), "blade_no_such_toolchain.json"))

    let checks = Doctor.collectChecks ()
    let expectedKeys = ["dotnet"; "gpp"; "blas"; "lapack"; "netcdf"; "mpi"; "cuda"; "make"; "gfortran"; "git"; "coq"]
    check "row set and order stable"
        ((checks |> List.map (fun c -> c.Key)) = expectedKeys)
        (sprintf "%d rows" checks.Length)
    check "every row carries a detail"
        (checks |> List.forall (fun c -> c.Detail <> ""))
        "no blank rows"
    check "dotnet row is ok"
        ((checks |> List.find (fun c -> c.Key = "dotnet")).Status = Doctor.StatusOk)
        "we are running on it"
    let blasRow = checks |> List.find (fun c -> c.Key = "blas")
    check "BLADE_BLAS=0 -> blas row off"
        (blasRow.Status = Doctor.StatusOff)
        "doctor reads the compiler's own gate"
    check "blas row origin names BLADE_BLAS [env]"
        (blasRow.Origin = "BLADE_BLAS [env]")
        "provenance reporting"
    check "lapack row off when BLAS is off"
        ((checks |> List.find (fun c -> c.Key = "lapack")).Status = Doctor.StatusOff)
        "gate coupling"

    let json = Doctor.renderJson checks (Doctor.isHealthy checks)
    let parsedOk =
        try
            use doc = System.Text.Json.JsonDocument.Parse json
            let root = doc.RootElement
            let arr = root.GetProperty("checks")
            let healthyKind = root.GetProperty("healthy").ValueKind
            arr.GetArrayLength() = checks.Length
            && (healthyKind = System.Text.Json.JsonValueKind.True
                || healthyKind = System.Text.Json.JsonValueKind.False)
            && root.GetProperty("os").ValueKind = System.Text.Json.JsonValueKind.String
            && (arr.EnumerateArray()
                |> Seq.forall (fun el ->
                    el.GetProperty("key").ValueKind = System.Text.Json.JsonValueKind.String
                    && el.GetProperty("status").ValueKind = System.Text.Json.JsonValueKind.String))
        with _ -> false
    check "renderJson parses; counts and field kinds hold" parsedOk "extension/CI contract"

    printFooter "Doctor" [sprintf "%d passed" passed; sprintf "%d failed" failed]
    { Block = "Doctor"; Passed = passed; Failed = failed; Skipped = 0; FailedNames = failedNames }
