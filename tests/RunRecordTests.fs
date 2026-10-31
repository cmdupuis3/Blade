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
    (match Blade.Lowering.lower "let x: Float64 = 1.0 + 2.0\n" with
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
        "let A = Float64([1.0, 2.0, 3.0, 4.0, 5.0])\n"
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

    // ---------------- 4. the record's OPTIONAL members, parsed ----------------
    // Two runtime components append to the record: the FORM CENSUS (`forms`,
    // W2-3: the rank-3 orbit scatter's executed leaf form) and membership
    // hoisting's builds (`member_hoist`, W2-1). Each member is written ONLY
    // when its component recorded something (Codex second look of W2-1, NEW
    // 3): a program with neither writes exactly the top-level members the
    // record had before both. Five programs, the record PARSED (System.Text.Json),
    // the member list and the route fields asserted: none / membership only /
    // forms only, on a team of 3 (`row-pull`) and of 1 (`serial-leaf3`) /
    // both together.
    printfn "\n--- record: optional members (parsed JSON) ---"
    let withPins (pins: (string * string) list) (f: unit -> 'a) : 'a =
        let prior = pins |> List.map (fun (k, _) -> (k, Environment.GetEnvironmentVariable k))
        for (k, v) in pins do Environment.SetEnvironmentVariable(k, v)
        try f () finally for (k, v) in prior do Environment.SetEnvironmentVariable(k, v)
    let baseMembers = [ "blade_run_record"; "program"; "status"; "executable"; "policy"; "routes"; "rng"; "inputs" ]
    let memberPart =
        "type Row = Idx<9>\n"
        + "let customer: Array<Int64 like Row> = [3, 1, 3, 2, 1, 4, 3, 9, -5]\n"
        + "let evens = Int64([0, 2, 4, 6, 8, 10, 12, 14, 16, 18, 20, 22, 24, 26, 28, 30, 44, 46, 58, 60])\n"
        + "let keep = mask(customer, lambda(c) -> contains(evens, c))\n"
    // the rank-3 ordered orbit scatter's row-owned pull (BLADE_FP_REPRO=1,
    // threads on, BLADE_TILE_TUNE pull_min=0): its leaf stage notes its form
    let formsPart =
        "type Obs = Idx<3>\n"
        + "type V13 = Idx<13>\n"
        + "let x13 = method_for(range<V13>, range<Obs>) <@> lambda(i, s) -> Float64((i * 7 + s * 3) % 11) * 0.37 + 0.1 / Float64(i + 1) |> compute\n"
        + "let t13 = method_for(x13, x13, x13) <@> lambda(a: Array<Float64 like Obs>, b: Array<Float64 like Obs>, c: Array<Float64 like Obs>) where comm(a, b, c) -> prodsum(a, b, c) |> compute\n"
        + "let a0 = reduce(method_for(range<V13>) <@> lambda(i) -> reduce(method_for(range<V13>) <@> lambda(j) -> reduce(method_for(range<V13>) <@> lambda(k) -> t13(i, j, k) * t13(i, j, k), (+), 0.0), (+), 0.0), lambda(u, v) where omp -> u + v, 0.0)\n"
    let buildPins = [ ("BLADE_FP_REPRO", "1"); ("BLADE_FP_CONTRACT", "off"); ("BLADE_TILE_TUNE", "pull_min=0"); ("BLADE_OMP_THREADS", null)
                      ("BLADE_ORBIT_PULL", null); ("BLADE_ORBIT_SCATTER", null); ("BLADE_ORBIT_FOLD", null); ("BLADE_FOLD_ROW_SLOTS", null)
                      ("BLADE_MEMBER_HOIST", null) ]
    // (stem, source, top-level members in order, member_hoist expected, the
    // one forms row expected (form, requested, actual), OMP_NUM_THREADS at run
    // time): the forms program runs on a team of 3 (the row-owned pull) and,
    // as its own build, on a team of 1 (the serial slice form, no team opened)
    let withForms = [ "blade_run_record"; "program"; "status"; "executable"; "policy"; "routes"; "rng"; "forms"; "inputs" ]
    let fixtures =
        [ ("rr_opt_none", "let xs = Int64([1, 2, 3])\nlet s = reduce(xs, (+))\n", baseMembers, false, None, "3")
          ("rr_opt_member", memberPart, baseMembers @ [ "member_hoist"; "member_hoist_dropped" ], true, None, "3")
          ("rr_opt_forms", formsPart, withForms, false, Some ("row-pull", 3, 3), "3")
          ("rr_opt_forms_serial", formsPart, withForms, false, Some ("serial-leaf3", 1, 1), "1")
          ("rr_opt_both", formsPart + memberPart, withForms @ [ "member_hoist"; "member_hoist_dropped" ], true, Some ("row-pull", 3, 3), "3") ]
    for (stem, src, wantMembers, wantMember, wantForms, threads) in fixtures do
        let name = $"record (parsed): {stem} -- top-level members, the route fields"
        match withPins buildPins (fun () -> buildExe stem src) with
        | Error e when isSkipError e -> skip name e
        | Error e -> check name false ("build: " + e)
        | Ok exe ->
            let recPath = Path.GetFullPath(Path.Combine(e2eDir, stem + ".record.json"))
            if File.Exists recPath then File.Delete recPath
            let runOutcome =
                withPins [ ("OMP_NUM_THREADS", threads) ] (fun () ->
                    let r = ref (Error "not run")
                    withRecord (Some recPath) (fun () -> r.Value <- runExecutable exe)
                    r.Value)
            match runOutcome with
            | Ok (0, _) -> ()
            | Ok (code, out) -> check name false ($"exit {code}: {out}")
            | Error e -> check name false ("run: " + e)
            if not (File.Exists recPath) then check name false "no record written"
            else
                let text = File.ReadAllText recPath
                match (try Ok (System.Text.Json.JsonDocument.Parse text) with ex -> Error ex.Message) with
                | Error e -> check name false ($"the record is not JSON: {e}")
                | Ok doc ->
                    use doc = doc
                    let root = doc.RootElement
                    let members = [ for p in root.EnumerateObject() -> p.Name ]
                    let memberOk =
                        if not wantMember then true
                        else
                            let mh = root.GetProperty "member_hoist"
                            mh.GetArrayLength() = 1
                            && (let e = mh.[0]
                                e.GetProperty("host").GetString() = "keep" && e.GetProperty("set").GetString() = "evens"
                                && e.GetProperty("m").GetInt64() = 20L && e.GetProperty("preferred").GetString() = "bitmap"
                                && e.GetProperty("actual").GetString() = "bitmap" && e.GetProperty("fallback").GetString() = "")
                            && root.GetProperty("member_hoist_dropped").GetInt32() = 0
                    let formsOk =
                        match wantForms with
                        | None -> true
                        | Some (form, requested, actual) ->
                            let fs = root.GetProperty "forms"
                            fs.GetArrayLength() = 1
                            && (let e = fs.[0]
                                e.GetProperty("site").GetString() = "a0" && e.GetProperty("form").GetString() = form
                                && e.GetProperty("requested").GetInt32() = requested && e.GetProperty("actual").GetInt32() = actual
                                && e.GetProperty("calls").GetInt64() = 1L)
                    let statusOk = root.GetProperty("status").GetProperty("ok").GetBoolean()
                    check name (members = wantMembers && memberOk && formsOk && statusOk)
                        ($"""members [{String.concat ", " members}] (want [{String.concat ", " wantMembers}]); member route {memberOk}; forms route {formsOk}; status ok {statusOk}; {text}""")

    // ---------------- 5. the pool-retention arm (BLADE_POOL_RETAIN) ----------------
    // Round-3 A4 / gap G52: an OPT-IN runtime arm. The parser; the emission
    // (the default writes nothing, the arm writes exactly its #define pair
    // ahead of every include, so the exe cache keys on it through the text);
    // and the run record's `policy.pool`, which says which arm ran.
    printfn "\n--- pool retention (BLADE_POOL_RETAIN) ---"
    let withPool (v: string option) (f: unit -> 'a) : 'a =
        let prior = Environment.GetEnvironmentVariable "BLADE_POOL_RETAIN"
        Environment.SetEnvironmentVariable("BLADE_POOL_RETAIN", (match v with Some s -> s | None -> null))
        try f () finally Environment.SetEnvironmentVariable("BLADE_POOL_RETAIN", prior)
    let parse (v: string option) = Toolchain.parsePoolRetention v "test"
    let p1 = parse (Some "1")
    let pOn = parse (Some " ON ")
    let pPair = parse (Some "16:48")
    check "pool parser: unset / 0 / off are the default 4 MB / 8 MB"
        ([ None; Some "0"; Some "off"; Some "" ] |> List.forall (fun v -> let p = parse v in p.IsDefault && p.MaxBlockMB = 4L && p.BudgetMB = 8L && p.Malformed.IsNone)) ""
    check "pool parser: 1 / on are the 64 MB / 96 MB arm"
        (p1.Arm = "retain" && p1.MaxBlockBytes = 67108864L && p1.BudgetBytes = 100663296L && pOn.Arm = "retain" && pOn.Setting = "1") $"{p1}"
    check "pool parser: a valid <max>:<budget> pair"
        (pPair.Arm = "pair" && pPair.Setting = "16:48" && pPair.MaxBlockMB = 16L && pPair.BudgetMB = 48L
         && (parse (Some " 8 : 8 ")).Arm = "pair") $"{pPair}"
    check "pool parser: max > budget, zero, negative, overflow and garbage are the default, flagged malformed"
        ([ "96:64"; "0:8"; "-4:8"; "4:2000000"; "4:8:16"; "lots"; "4MB:8MB"; "2" ]
         |> List.forall (fun v -> let p = parse (Some v) in p.IsDefault && p.MaxBlockMB = 4L && p.Malformed = Some v)) ""
    check "pool parser: the default contributes no define; the arm the twin pair"
        (Toolchain.poolRetentionDefines (parse None) = []
         && Toolchain.poolRetentionDefines p1 = [ "BLADE_POOL_MAX_BLOCK", 67108864L; "BLADE_POOL_BUDGET", 100663296L ]) ""
    // Precedence and the UNSET path, against ISOLATED toolchain fixtures: an
    // unset variable falls back to the toolchain file, so every operational
    // default below says `off` explicitly, and unset is tested only here,
    // with a file this block wrote (selector and parse cache restored after).
    let withToolchain (json: string) (f: unit -> 'a) : 'a =
        let path = Path.GetFullPath(Path.Combine(e2eDir, $"""pool_toolchain_{Guid.NewGuid().ToString("N")}.json"""))
        File.WriteAllText(path, json)
        let prior = Environment.GetEnvironmentVariable "BLADE_TOOLCHAIN_FILE"
        Environment.SetEnvironmentVariable("BLADE_TOOLCHAIN_FILE", path)
        Toolchain.refresh ()
        try f ()
        finally
            Environment.SetEnvironmentVariable("BLADE_TOOLCHAIN_FILE", prior)
            Toolchain.refresh ()
            (try File.Delete path with _ -> ())
    let fileArm = "{\"BLADE_POOL_RETAIN\": \"1\"}"
    let fileOff = "{\"BLADE_POOL_RETAIN\": \"off\"}"
    let resolved (json: string) (env: string option) = withToolchain json (fun () -> withPool env Toolchain.poolRetention)
    let rUnset = resolved "{}" None
    let rFile = resolved fileArm None
    let rFileArmEnvOff = resolved fileArm (Some "off")
    let rFileOffEnvArm = resolved fileOff (Some "1")
    check "pool precedence: unset with an empty toolchain file is the default, from no source"
        (rUnset.IsDefault && rUnset.Source = "default" && rUnset.Setting = "default") $"{rUnset}"
    check "pool precedence: unset env reads the toolchain file's arm"
        (rFile.Arm = "retain" && rFile.Source = "blade.toolchain.json") $"{rFile}"
    check "pool precedence: file=1, env=off is the default (env wins)"
        (rFileArmEnvOff.IsDefault && rFileArmEnvOff.Source = "env BLADE_POOL_RETAIN") $"{rFileArmEnvOff}"
    check "pool precedence: file=off, env=1 is the arm (env wins)"
        (rFileOffEnvArm.Arm = "retain" && rFileOffEnvArm.Source = "env BLADE_POOL_RETAIN") $"{rFileOffEnvArm}"
    let poolSource = "let x = 1.0 * Float64(0..6000000)\nlet y = x * 2.0 |> compute\nlet s = reduce(y, (+))\n"
    (match Blade.Lowering.lower poolSource with
     | Error e -> check "pool emission: program lowers" false e
     | Ok ir ->
         let emitUnder v = withPool v (fun () -> fst (genSelfContainedProgramFromIR ir "rr_pool"))
         let dflt = emitUnder (Some "off")
         let unsetEmpty = withToolchain "{}" (fun () -> emitUnder None)
         check "pool emission: unset (empty toolchain file) emits exactly the `off` default" (unsetEmpty = dflt) ""
         let off = emitUnder (Some "0")
         let bad = emitUnder (Some "96:64")
         let arm = emitUnder (Some "1")
         check "pool emission: the default writes no pool define"
             (not (dflt.Contains "BLADE_POOL_MAX_BLOCK") && not (dflt.Contains "BLADE_POOL_BUDGET") && not (dflt.Contains "BLADE_POOL_RETAIN")) ""
         check "pool emission: `0` and a malformed value emit the default byte for byte" (off = dflt && bad = dflt) ""
         let lines = arm.Replace("\r\n", "\n").Split('\n')
         let firstInclude = lines |> Array.findIndex (fun l -> l.StartsWith "#include")
         check "pool emission: the arm writes its #define pair ahead of every include"
             (firstInclude = 3 && lines.[0].StartsWith "// BLADE_POOL_RETAIN=1:"
              && lines.[1] = "#define BLADE_POOL_MAX_BLOCK 67108864" && lines.[2] = "#define BLADE_POOL_BUDGET 100663296")
             (String.concat " | " (Array.truncate 4 lines))
         check "pool emission: the arm differs from the default ONLY by those lines"
             (String.concat "\n" (Array.skip 3 lines) = dflt.Replace("\r\n", "\n")) ""
         // The record (g++), PARSED. `policy.pool` is an ADDITIVE, OPTIONAL
         // member of the v1 record (blade_run_record.hpp): every C++ record
         // written since W2-6 carries it, default builds included (so a
         // default record is NOT byte-identical to an older one), and a v1
         // reader must accept records that lack it. The version stays 1.
         // Fixtures: the default (`off`), an explicit pair, and a memcheck
         // (ASan) build of the arm -- the cache compiled OFF, its caps kept.
         let buildPool (name: string) (v: string option) (memcheck: bool) : Result<string, string> =
             // BLADE_PRINT=s: the 6M-cell arrays are not printed (an ASan
             // build printing them runs past the harness's 120 s ceiling).
             withPins [ ("BLADE_MEMCHECK", (if memcheck then "1" else null)); ("BLADE_PRINT", "s") ] (fun () ->
                 let cpp = emitUnder v
                 deployRuntimeHeaders e2eDir
                 let cppFile = Path.Combine(e2eDir, name + ".cpp")
                 File.WriteAllText(cppFile, cpp)
                 if memcheck then compileCppMemcheck (Some cpp) [] cppFile e2eDir else compileCpp cppFile e2eDir)
         let outputs = ResizeArray<string * string>()
         let policyMembers = [ "march"; "fp_contract"; "fp_reassoc"; "pool" ]
         let poolMembers = [ "retain"; "max_block"; "budget"; "budget_scope"; "cache" ]
         for (name, v, memcheck, (retain, maxBlock, budget, cache)) in
             [ "rr_pool_default", Some "off", false, ("default", 4194304L, 8388608L, "on")
               "rr_pool_pair", Some "16:48", false, ("arm", 16777216L, 50331648L, "on")
               "rr_pool_memcheck_arm", Some "1", true, ("arm", 67108864L, 100663296L, "off") ] do
             let label = $"pool record (parsed): {name} -- policy.pool {retain} {maxBlock}/{budget} per-thread, cache {cache}"
             match buildPool name v memcheck with
             | Error e ->
                 if isSkipError e || (memcheck && (e.Contains "clang" || e.Contains "sanitize" || e.Contains "not found")) then skip label e
                 else check label false ("build: " + e)
             | Ok exe ->
                 let recPath = Path.GetFullPath(Path.Combine(e2eDir, name + ".record.json"))
                 if File.Exists recPath then File.Delete recPath
                 let run = ref (Error "not run")
                 withRecord (Some recPath) (fun () -> run.Value <- runExecutable exe)
                 match run.Value with
                 | Error e -> check label false ("run: " + e)
                 | Ok (code, out) ->
                     if not memcheck then
                         // Values only: the timing line differs run to run.
                         let values =
                             out.Replace("\r\n", "\n").Split('\n')
                             |> Array.filter (fun l -> not (l.Contains "completed in"))
                             |> String.concat "\n"
                         outputs.Add((name, values))
                     let text = if File.Exists recPath then File.ReadAllText recPath else ""
                     match (try Ok (System.Text.Json.JsonDocument.Parse text) with ex -> Error ex.Message) with
                     | Error e -> check label false ($"exit {code}; the record is not JSON: {e} {text}")
                     | Ok doc ->
                         use doc = doc
                         let root = doc.RootElement
                         let policy = root.GetProperty "policy"
                         let pool = policy.GetProperty "pool"
                         let names (e: System.Text.Json.JsonElement) = [ for p in e.EnumerateObject() -> p.Name ]
                         let ok =
                             code = 0
                             && root.GetProperty("blade_run_record").GetInt32() = 1
                             && names policy = policyMembers && names pool = poolMembers
                             && pool.GetProperty("retain").GetString() = retain
                             && pool.GetProperty("max_block").GetInt64() = maxBlock
                             && pool.GetProperty("budget").GetInt64() = budget
                             && pool.GetProperty("budget_scope").GetString() = "per-thread"
                             && pool.GetProperty("cache").GetString() = cache
                             && root.GetProperty("status").GetProperty("ok").GetBoolean()
                         check label ok ($"exit {code}; {text}")
         match List.ofSeq outputs with
         | [ (_, a); (_, b) ] ->
             check "pool record: the default and the pair print identical values" (a = b && a.Contains "s = ") (a + " /// " + b)
         | _ -> ())
    // COEXISTENCE: the arm's policy.pool beside the other OPTIONAL members --
    // the forms census and member_hoist (section 4's both-members program),
    // built under the arm: the same top-level member list, both routes present.
    (let name = "record (parsed): rr_opt_both under BLADE_POOL_RETAIN=1 -- policy.pool beside forms and member_hoist"
     match withPins (buildPins @ [ ("BLADE_POOL_RETAIN", "1") ]) (fun () -> buildExe "rr_opt_both_pool" (formsPart + memberPart)) with
     | Error e when isSkipError e -> skip name e
     | Error e -> check name false ("build: " + e)
     | Ok exe ->
         let recPath = Path.GetFullPath(Path.Combine(e2eDir, "rr_opt_both_pool.record.json"))
         if File.Exists recPath then File.Delete recPath
         let run = ref (Error "not run")
         withPins [ ("OMP_NUM_THREADS", "3") ] (fun () -> withRecord (Some recPath) (fun () -> run.Value <- runExecutable exe))
         let text = if File.Exists recPath then File.ReadAllText recPath else ""
         match run.Value, (try Ok (System.Text.Json.JsonDocument.Parse text) with ex -> Error ex.Message) with
         | Ok (0, _), Ok doc ->
             use doc = doc
             let root = doc.RootElement
             let members = [ for p in root.EnumerateObject() -> p.Name ]
             let pool = root.GetProperty("policy").GetProperty("pool")
             let ok =
                 members = withForms @ [ "member_hoist"; "member_hoist_dropped" ]
                 && root.GetProperty("forms").GetArrayLength() = 1
                 && root.GetProperty("member_hoist").GetArrayLength() = 1
                 && pool.GetProperty("retain").GetString() = "arm" && pool.GetProperty("max_block").GetInt64() = 67108864L
             check name ok text
         | r, p -> check name false ($"run {r}; parse {p}; {text}"))

    { Block = "Run Records"; Passed = passed; Failed = failed; Skipped = skipped
      FailedNames = List.ofSeq failedNames }
