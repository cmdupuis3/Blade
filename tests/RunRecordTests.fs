// Input manifests + run records (src/RunRecord.fs, src/cpp/blade_run_record.hpp;
// docs/plans/plan-fortran-killer-2.md section 7). Three layers:
//   1. the MANIFEST a lowered program yields (no toolchain): one entry per
//      provider read, named by its binding, with element type, axes, storage
//      interpretation and identity policy -- and its text / JSON renderings;
//   2. the EMISSION (no toolchain): every program carries the baked table
//      and the at-exit writer, and no environment reaches the emitted text;
//   3. the RECORD a run writes (g++, skips without it): BLADE_RUN_RECORD set
//      -> a JSON file naming the program, an `ok` status, the observed input
//      (exists, kind, size), the build policy the defines carried, the
//      routes; and on a BL8007 abort the same file with `ok:false` and the
//      code. Unset -> no file.
// Hermetic like CsvTests: the CSV fixture is written on the fly -- into the
// scratch tree, never the repo -- at both resolution roots (compiler + exe cwd).
module Blade.Tests.RunRecordTests

open System
open System.IO
open Blade
open Blade.IR
open Blade.Lowering
open Blade.CodeGen
open Blade.CsvProvider
open Blade.Build
open Blade.Tests.TestHarness

let runRunRecordTests () : BlockResult =
    printHeader "Run Records (input manifests + BLADE_RUN_RECORD)"
    let mutable passed = 0
    let mutable failed = 0
    let mutable skipped = 0
    let failedNames = ResizeArray<string>()
    let check (name: string) (condition: bool) (detail: string) =
        if condition then
            printfn "  PASS: %s" name
            passed <- passed + 1
        else
            printfn "  FAIL: %s -- %s" name detail
            failed <- failed + 1
            failedNames.Add name
    let skip (name: string) (why: string) =
        printfn "  SKIP: %s (%s)" name why
        skipped <- skipped + 1

    // The fixture is written into the SCRATCH tree (the cwd-relative
    // generated_cpp_tests the corpus runner already owns), never into the
    // repository: this block used to write tests/fixtures/csv_files/rr_grid.csv,
    // a TRACKED file, so every suite run rewrote a checked-in fixture (with LF
    // endings over a CRLF checkout). The relative path is the same text at both
    // resolution roots, so the program source names one path.
    let fixDir = "generated_cpp_tests/run_record_fixtures"
    Directory.CreateDirectory fixDir |> ignore
    let fixFile (name: string) = fixDir + "/" + name
    let e2eDir = "./generated_cpp_tests"
    if not (Directory.Exists e2eDir) then Directory.CreateDirectory e2eDir |> ignore
    Directory.CreateDirectory (Path.Combine(e2eDir, fixDir)) |> ignore
    let writeFixture (name: string) (lines: string list) =
        CsvWrite.writeRaw (fixFile name) lines
        CsvWrite.writeRaw (Path.Combine(e2eDir, fixFile name)) lines
    writeFixture "rr_grid.csv" [ "1.0,2.0,3.0"; "4.0,5.0,6.0" ]

    let readSource =
        "import csv as c
"
        + "let m = c.load(\"" + fixFile "rr_grid.csv" + "\")
"
        + "let V = m.vars.data |> c.read
"
        + "let rowsum = method_for(V) <@> lambda(r: Array<Float64 like Idx<3>>) -> reduce(r, (+)) |> compute
"

    // ---------------- 1. the manifest ----------------
    printfn "\n--- manifest ---"
    (match Blade.Lowering.lower readSource with
     | Error e -> check "manifest: program lowers" false e
     | Ok ir ->
         let entries = RunRecord.manifestOf ir.Modules (Blade.ProviderStatics.drainFoldLog ())
         check "manifest: one runtime read" (entries.Length = 1) (sprintf "%d entries" entries.Length)
         match entries with
         | [ e ] ->
             check "manifest: named by its binding, provider csv, variable data"
                 (e.Name = "V" && e.Provider = "csv" && e.Variable = "data") (sprintf "%A" e)
             check "manifest: dense Float64 over 2 x 3" (e.Storage = "dense" && e.Elem = "Float64" && e.Axes = [ ("Idx", Some 2L); ("Idx", Some 3L) ]) (sprintf "%A" e)
             check "manifest: a runtime read has version identity and no content hash"
                 (e.Identity = "version" && e.ContentHash.IsNone) (sprintf "%A" e)
             let text = RunRecord.renderEntry e
             check "manifest: text names the binding, the store and the policy"
                 (text.StartsWith "V: csv " && text.Contains "#data" && text.Contains "dense Float64[Idx=2 x Idx=3]" && text.Contains "identity: version") text
             let js = RunRecord.renderJson entries
             check "manifest: JSON carries the same fields"
                 (js.Contains "\"name\":\"V\"" && js.Contains "\"provider\":\"csv\"" && js.Contains "\"axes\":[{\"tag\":\"Idx\",\"extent\":2},{\"tag\":\"Idx\",\"extent\":3}]"
                  && js.Contains "\"identity\":\"version\"" && not (js.Contains "content_hash")) js
         | _ -> ())
    // A compile-time FOLD (`let static`) is an input of the program with
    // content identity: the hash the fold was taken over, baked as such.
    let foldSource =
        "import csv as c
"
        + "let m = c.load(\"" + fixFile "rr_grid.csv" + "\")
"
        + "let static V = m.vars.data |> c.read
"
    (match Blade.Lowering.lower foldSource with
     | Error e -> check "manifest: folded program lowers" false e
     | Ok ir ->
         let entries = RunRecord.manifestOf ir.Modules (Blade.ProviderStatics.drainFoldLog ())
         match entries with
         | [ e ] ->
             check "manifest: a folded read has content identity with its hash"
                 (e.Storage = "folded" && e.Identity = "content" && e.Variable = "data" && e.Provider = "csv"
                  && (match e.ContentHash with Some h -> h.Length = 64 | None -> false)) (sprintf "%A" e)
             check "manifest: the fold renders as folded, hashed"
                 ((RunRecord.renderEntry e).Contains "folded at compile time" && (RunRecord.renderEntry e).Contains "identity: content sha256:") (RunRecord.renderEntry e)
             let (cpp, _) = genSelfContainedProgramFromIR ir "rr_fold"
             // The fold log was drained above; codegen's own drain sees nothing
             // new, so this emission checks the runtime table stays empty of it
             // -- and a fresh lowering bakes the hash.
             check "manifest: a drained fold log does not leak into the next emission"
                 (not (cpp.Contains "\"content\"")) ""
         | _ -> check "manifest: one folded input" false (sprintf "%d entries" entries.Length))
    (match Blade.Lowering.lower foldSource with
     | Error e -> check "emission: folded program lowers" false e
     | Ok ir ->
         let (cpp, _) = genSelfContainedProgramFromIR ir "rr_fold"
         check "emission: the folded input is baked with its content hash"
             (cpp.Contains "\"folded\", \"\", \"content\", \"") cpp)
    // A program with no provider read has an empty manifest -- and still a record.
    (match Blade.Lowering.lower "let x = 1.0 + 2.0\n" with
     | Error e -> check "manifest: plain program lowers" false e
     | Ok ir ->
         let entries = RunRecord.manifestOf ir.Modules (Blade.ProviderStatics.drainFoldLog ())
         check "manifest: a program without inputs has an empty manifest" entries.IsEmpty (sprintf "%d entries" entries.Length))

    // ---------------- 2. the emission ----------------
    printfn "\n--- emission ---"
    (match Blade.Lowering.lower readSource with
     | Error e -> check "emission: program lowers" false e
     | Ok ir ->
         let (cpp, _) = genSelfContainedProgramFromIR ir "rr_emit"
         check "emission: includes the record header" (cpp.Contains "#include \"blade_run_record.hpp\"") ""
         check "emission: bakes the manifest row and the writer"
             (cpp.Contains "static const blade_rr::Input __blade_rr_inputs[]" && cpp.Contains "\"V\", \"csv\"" && cpp.Contains "blade_rr::AtExit __blade_rr_at_exit{ \"rr_emit\"") cpp
         check "emission: no build policy in the text (it travels as -D defines)"
             (not (cpp.Contains "BLADE_RR_MARCH") && not (cpp.Contains "march=")) "")
    // Emission is a function of the program alone: two lowerings agree byte for byte.
    (match Blade.Lowering.lower readSource, Blade.Lowering.lower readSource with
     | Ok a, Ok b ->
         let (ca, _) = genSelfContainedProgramFromIR a "rr_same"
         let (cb, _) = genSelfContainedProgramFromIR b "rr_same"
         check "emission: deterministic across lowerings" (ca = cb) ""
     | _ -> check "emission: deterministic across lowerings" false "lowering failed")

    // ONE FAILURE EXIT ACROSS A DLL BOUNDARY (src/cpp/blade_dll_panic.hpp).
    // A DLL Blade builds with nvcc -shared is its own image; a failure inside
    // it used to bypass the executable's panic (the cuBLAS shim called abort();
    // the hybrid's kernels checked no CUDA status at all), so no run record was
    // written. Now each such DLL exports a binder, the host binds
    // blade_rt::dll_panic into it at static initialization, and a DLL-side
    // failure is the host's own BL8005 -- hooks, record and all. Emission
    // only (no toolchain); the device runs are `blade test cuda` / `hybrid`.
    let pinned (vars: (string * string) list) (f: unit -> unit) =
        let priors = vars |> List.map (fun (k, _) -> (k, Environment.GetEnvironmentVariable k))
        for (k, v) in vars do Environment.SetEnvironmentVariable(k, v)
        try f () finally for (k, p) in priors do Environment.SetEnvironmentVariable(k, p)
    let cublasGramSource =
        "let A: Array<Float64 like Idx<3>, Idx<2>> = [[1.0, 2.0], [3.0, 4.0], [5.0, 6.0]]\nlet G = gram(A, A)\n"
    pinned [ ("BLADE_CUBLAS", "1"); ("BLADE_BLAS", "0") ] (fun () ->
        match Blade.Lowering.lower cublasGramSource with
        | Error e -> check "emission: cuBLAS-routed program lowers" false e
        | Ok ir ->
            let (cpp, _) = genSelfContainedProgramFromIR ir "rr_cublas_bind"
            check "emission: a cuBLAS-routed program binds its own panic into the shim DLL"
                (cpp.Contains "#include \"blade_linalg_cuda.hpp\""
                 && cpp.Contains "blade_cuda_bind_panic(&blade_rt::dll_panic)") cpp)
    pinned [ ("BLADE_CUBLAS", "0"); ("BLADE_BLAS", "0") ] (fun () ->
        match Blade.Lowering.lower cublasGramSource with
        | Error e -> check "emission: host-routed program lowers" false e
        | Ok ir ->
            let (cpp, _) = genSelfContainedProgramFromIR ir "rr_host_nobind"
            check "emission: a program linking no Blade DLL binds nothing"
                (not (cpp.Contains "bind_panic")) "")
    let hybridSource =
        "type NIdx = Idx<6>\nlet A: Array<Float64 like NIdx> = [1.0, 2.0, 4.0, 8.0, 16.0, 32.0]\n"
        + "let m2 = method_for(A, A) <@> lambda(x, y) where comm(x, y), mpi, cuda(block: 64) -> x * y |> compute\n"
    (try
        try
            CodeGen.setMpiEmitMode true
            CodeGen.setCudaEmitMode true
            match Blade.Lowering.lower hybridSource with
            | Error e -> check "emission: mpi+cuda hybrid lowers" false e
            | Ok ir ->
                let (cpp, _) = genSelfContainedProgramFromIR ir "rr_hybrid_bind"
                match CodeGen.getCudaFileContent () with
                | None -> check "emission: the hybrid emits its .cu" false "no cuda kernels collected"
                | Some cu ->
                    check "emission: the hybrid's DLL exports a panic binder and carries no runtime state of its own"
                        (cu.Contains "#include \"blade_dll_panic.hpp\""
                         && cu.Contains ("BLADE_DLL_EXPORT void " + CodeGenCuda.kernelsPanicBinder + "(")
                         && not (cu.Contains "blade_runtime.hpp") && not (cu.Contains "blade_rt::")) cu
                    check "emission: the hybrid's DLL checks every CUDA runtime status through the host's panic"
                        (cu.Contains "blade_dll::ck(cudaSetDevice(" && cu.Contains "blade_dll::ck(cudaMalloc("
                         && cu.Contains "blade_dll::ck(cudaGetLastError()" && cu.Contains "blade_dll::ck(cudaDeviceSynchronize()"
                         && cu.Contains "cudaMemcpyDeviceToHost), \"cudaMemcpy D2H\")") cu
                    check "emission: the hybrid's host binds its own panic into the kernels DLL"
                        (cpp.Contains (CodeGenCuda.kernelsPanicBinder + "(&blade_rt::dll_panic)")) ""
        finally
            CodeGen.setMpiEmitMode false
            CodeGen.setCudaEmitMode false
     with ex -> check "emission: mpi+cuda hybrid binding" false ex.Message)
    // The plain `where cuda` paths link their kernels into the executable
    // (compileCudaSplit), but their wrappers used to check no CUDA status
    // either: the same rewrite and the same binding cover them, so a device
    // failure there is the host's BL8005 too. Both wrapper families: the
    // rectangular map and the triangular (simplicial) comm kernel.
    let plainCudaSource =
        "let A = [1.0, 2.0, 3.0, 4.0, 5.0]\n"
        + "let B = method_for(A) <@> lambda(x) where cuda(block: 32) -> x * 2.0 + 1.0 |> compute\n"
        + "let R = method_for(A, A) <@> lambda(x, y) where comm(x, y), cuda(block: 32) -> x * y |> compute\n"
    (try
        try
            CodeGen.setCudaEmitMode true
            match Blade.Lowering.lower plainCudaSource with
            | Error e -> check "emission: plain cuda program lowers" false e
            | Ok ir ->
                let (cpp, _) = genSelfContainedProgramFromIR ir "rr_cuda_bind"
                match CodeGen.getCudaFileContent () with
                | None -> check "emission: the plain cuda program emits its .cu" false "no cuda kernels collected"
                | Some cu ->
                    let launches = System.Text.RegularExpressions.Regex.Matches(cu, @">>>\(").Count
                    let launchChecks = System.Text.RegularExpressions.Regex.Matches(cu, @"blade_dll::ck\(cudaGetLastError\(\), ""kernel launch""\);").Count
                    let bareCalls =
                        cu.Split('\n')
                        |> Array.filter (fun l ->
                            System.Text.RegularExpressions.Regex.IsMatch(l, @"(?<!ck\()\b(cudaMalloc|cudaMemcpy|cudaDeviceSynchronize|cudaSetDevice)\("))
                    check "emission: every plain cuda wrapper checks its CUDA statuses (no bare call, every launch checked)"
                        (launches = 2 && launchChecks = 2 && Array.isEmpty bareCalls
                         && cu.Contains "blade_dll::ck(cudaMalloc(" && cu.Contains "\"cudaMemcpy H2D\")"
                         && cu.Contains "\"cudaMemcpy D2H\")")
                        (let bare = String.concat " | " bareCalls in $"launches={launches} checks={launchChecks} bare={bare}")
                    check "emission: the plain cuda .cu exports the binder and its host binds it"
                        (cu.Contains ("BLADE_DLL_EXPORT void " + CodeGenCuda.kernelsPanicBinder + "(")
                         && cpp.Contains (CodeGenCuda.kernelsPanicBinder + "(&blade_rt::dll_panic)")) ""
        finally
            CodeGen.setCudaEmitMode false
     with ex -> check "emission: plain cuda binding" false ex.Message)

    // ---------------- 3. the record (g++) ----------------
    printfn "\n--- record (g++) ---"
    let withRecord (path: string option) (f: unit -> unit) =
        let prior = Environment.GetEnvironmentVariable "BLADE_RUN_RECORD"
        Environment.SetEnvironmentVariable("BLADE_RUN_RECORD", (match path with Some p -> p | None -> null))
        try f () finally Environment.SetEnvironmentVariable("BLADE_RUN_RECORD", prior)
    let buildExe (name: string) (source: string) : Result<string, string> =
        match Blade.Lowering.lower source with
        | Error e -> Error $"lower: {e}"
        | Ok ir ->
            let (cpp, _) = genSelfContainedProgramFromIR ir name
            deployRuntimeHeaders e2eDir
            let cppFile = Path.Combine(e2eDir, name + ".cpp")
            File.WriteAllText(cppFile, cpp)
            compileCpp cppFile e2eDir
    (match buildExe "rr_ok" readSource with
     | Error e ->
         if isSkipError e then skip "record: ok run" e else check "record: compiles" false e
     | Ok exe ->
         let recPath = Path.GetFullPath(Path.Combine(e2eDir, "rr_ok.record.json"))
         if File.Exists recPath then File.Delete recPath
         // Unset: no file.
         withRecord None (fun () ->
             match runExecutable exe with
             | Ok (0, _) -> check "record: no BLADE_RUN_RECORD, no file" (not (File.Exists recPath)) ""
             | Ok (code, out) -> check "record: plain run exits 0" false $"exit {code}: {out}"
             | Error e -> check "record: plain run" false e)
         withRecord (Some recPath) (fun () ->
             match runExecutable exe with
             | Ok (0, out) ->
                 check "record: run exits 0 with the pin set" (out.Contains "rowsum = [6.0, 15.0]") out
                 if not (File.Exists recPath) then check "record: file written" false recPath
                 else
                     let js = File.ReadAllText recPath
                     check "record: names the program and reports ok"
                         (js.Contains "\"blade_run_record\":1" && js.Contains "\"program\":\"rr_ok\"" && js.Contains "\"status\":{\"ok\":true}") js
                     check "record: the input was observed as an existing file with its size"
                         (js.Contains "\"name\":\"V\"" && js.Contains "\"identity\":\"version\"" && js.Contains "\"observed\":{\"exists\":true,\"kind\":\"file\",\"size\":") js
                     check "record: build policy arrived through the defines"
                         (js.Contains "\"policy\":{\"march\":\"" && not (js.Contains "\"march\":\"unknown\"") && js.Contains "\"fp_contract\":\"") js
                     check "record: routes and executable identity are present"
                         (js.Contains "\"routes\":{\"blas\":" && js.Contains "\"executable\":{\"path\":" && js.Contains "\"compiler\":\"") js
                     check "record: no rng in a program that draws none" (js.Contains "\"rng\":null") js
             | Ok (code, out) -> check "record: run exits 0 with the pin set" false $"exit {code}: {out}"
             | Error e -> check "record: run with the pin set" false e))
    // The failure path: a singular factor aborts BL8007 and the record says so.
    let abortSource = """
import math as m
let A: Array<Float64 like Idx<2>, Idx<2>> = [[1.0, 2.0], [2.0, 4.0]]
let b: Array<Float64 like Idx<2>> = [1.0, 2.0]
let x = m.solve(A, b)
"""
    (match buildExe "rr_abort" abortSource with
     | Error e ->
         if isSkipError e then skip "record: aborting run" e else check "record: aborting program compiles" false e
     | Ok exe ->
         let recPath = Path.GetFullPath(Path.Combine(e2eDir, "rr_abort.record.json"))
         if File.Exists recPath then File.Delete recPath
         withRecord (Some recPath) (fun () ->
             match runExecutable exe with
             | Ok (code, out) ->
                 check "record: the singular solve aborts" (code <> 0) $"exit {code}: {out}"
                 if not (File.Exists recPath) then check "record: file written on the failure exit" false recPath
                 else
                     let js = File.ReadAllText recPath
                     check "record: status carries the failure code"
                         (js.Contains "\"status\":{\"ok\":false,\"code\":\"BL8007\"" && js.Contains "\"inputs\":[]") js
             | Error e -> check "record: aborting run" false e))

    // MANY THREADS FAIL AT ONCE: every iteration of an OpenMP loop divides by
    // zero. The runtime's failure exit used to call std::exit from each worker
    // that got there -- concurrent static teardown, undefined. Now the first
    // failure reports and leaves through _Exit after flushing and writing the
    // record; every other worker parks. So: one diagnostic, a nonzero exit,
    // and the record written once with that code.
    let ompAbortSource = """
let z: Array<Int64 like Idx<4000>> = 0 * (0..4000)
let q = method_for(z) <@> lambda(x) where omp(x: 1) -> 10 / x |> compute
"""
    (match buildExe "rr_omp_abort" ompAbortSource with
     | Error e ->
         if isSkipError e then skip "record: concurrent failures" e else check "record: concurrently failing program compiles" false e
     | Ok exe ->
         let recPath = Path.GetFullPath(Path.Combine(e2eDir, "rr_omp_abort.record.json"))
         if File.Exists recPath then File.Delete recPath
         let priorThreads = Environment.GetEnvironmentVariable "OMP_NUM_THREADS"
         Environment.SetEnvironmentVariable("OMP_NUM_THREADS", "8")
         try
             withRecord (Some recPath) (fun () ->
                 match runExecutable exe with
                 | Ok (code, out) ->
                     let reports = out.Split('\n') |> Array.filter (fun l -> l.Contains "error[BL8013]") |> Array.length
                     check "record: concurrent failures end the run with a nonzero status" (code <> 0) $"exit {code}: {out}"
                     check "record: exactly one failure is reported however many workers fail" (reports = 1) $"{reports} report(s): {out}"
                     if not (File.Exists recPath) then check "record: file written on the concurrent failure exit" false recPath
                     else
                         let js = File.ReadAllText recPath
                         check "record: one well-formed record carrying the failure code"
                             (js.Contains "\"status\":{\"ok\":false,\"code\":\"BL8013\"" && js.TrimEnd().EndsWith "]}"
                              && (js.Split("\"blade_run_record\"").Length = 2)) js
                 | Error e -> check "record: concurrently failing run" false e)
         finally
             Environment.SetEnvironmentVariable("OMP_NUM_THREADS", priorThreads))

    { Block = "Run Records"; Passed = passed; Failed = failed; Skipped = skipped
      FailedNames = List.ofSeq failedNames }
