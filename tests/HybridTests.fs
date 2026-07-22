// Mixed-parallelism (hybrid backend) tests — MixedParallelismPlan.md.
// One source per case; the hybrid clause is INERT with gates off (the
// serial/omp oracle comes from the same source), and the hybrid build is
// differentially compared under mpiexec -n {1,3} × OMP_NUM_THREADS {1,4}
// (SPMD + rank-0 prints make output independent of both P and T).
// Skips cleanly without mpiexec, like MpiTests.
module Blade.Tests.HybridTests

open System
open System.IO
open Blade
open Blade.Lowering
open Blade.CodeGen
open Blade.Build
open Blade.Tests.TestHarness

let runHybridTests () =
    printHeader "Mixed Parallelism (hybrid) Tests"
    let mutable passed = 0
    let mutable failed = 0
    let check (name: string) (condition: bool) (detail: string) =
        if condition then
            printfn "  PASS: %s" name
            passed <- passed + 1
        else
            printfn "  FAIL: %s — %s" name detail
            failed <- failed + 1

    let outDir = "./generated_cpp_tests"
    if not (Directory.Exists outDir) then Directory.CreateDirectory outDir |> ignore
    let normalize (s: string) =
        s.Split('\n')
        |> Array.filter (fun l -> not (l.Contains "completed in"))
        |> Array.map (fun l -> l.TrimEnd())
        |> String.concat "\n"
        |> fun x -> x.Trim()
    let codegenOf (testName: string) (src: string) : Result<string, string> =
        match lower src with
        | Error e -> Error (sprintf "lower: %s" e)
        | Ok ir -> Ok (fst (CodeGen.genSelfContainedProgramFromIR ir testName))
    let compileRunSerial (testName: string) (src: string) : Result<string, string> =
        match codegenOf testName src with
        | Error e -> Error e
        | Ok cpp ->
            CodeGen.deployRuntimeHeaders outDir
            let f = Path.Combine(outDir, testName + ".cpp")
            File.WriteAllText(f, cpp)
            match compileCpp f outDir with
            | Error e -> Error (sprintf "compile: %s" e)
            | Ok exe ->
                match runExecutable exe with
                | Ok (0, out) -> Ok out
                | Ok (code, out) -> Error (sprintf "exit %d: %s" code (out.Substring(0, min 200 out.Length)))
                | Error e -> Error e

    // ---------------------------------------------------------------
    // 1. Parse-level order table
    // ---------------------------------------------------------------
    printfn "\n--- order table (parse) ---"
    let parseErrOf (src: string) : string =
        match Parser.parseProgram src with
        | Error e -> e.Message
        | Ok _ -> ""
    let kernelWith (clause: string) =
        sprintf "let f = lambda(x, y) where comm(x, y), %s -> x * y\n" clause
    check "mpi, omp accepted" (parseErrOf (kernelWith "mpi, omp(x: 1)") = "") (parseErrOf (kernelWith "mpi, omp(x: 1)"))
    check "mpi, cuda accepted" (parseErrOf (kernelWith "mpi, cuda(block: 64)") = "") (parseErrOf (kernelWith "mpi, cuda(block: 64)"))
    check "omp, mpi rejected with steering"
        ((parseErrOf (kernelWith "omp(x: 1), mpi")).Contains "did you mean `where mpi, omp(...)`")
        (parseErrOf (kernelWith "omp(x: 1), mpi"))
    check "omp, cuda rejected (one iteration space, two owners)"
        ((parseErrOf (kernelWith "omp(x: 1), cuda(block: 64)")).Contains "cannot be both OpenMP-host and CUDA-device")
        (parseErrOf (kernelWith "omp(x: 1), cuda(block: 64)"))
    check "cuda-first rejected (device owns the leaf)"
        ((parseErrOf (kernelWith "cuda(block: 64), omp(x: 1)")).Contains "nothing nests inside a device kernel")
        (parseErrOf (kernelWith "cuda(block: 64), omp(x: 1)"))
    check "duplicate mpi rejected" ((parseErrOf (kernelWith "mpi, mpi")).Contains "twice") ""
    check "three strategies rejected"
        ((parseErrOf (kernelWith "mpi, omp(x: 1), cuda(block: 64)")).Contains "At most two")
        (parseErrOf (kernelWith "mpi, omp(x: 1), cuda(block: 64)"))

    // ---------------------------------------------------------------
    // 2. Gate-off inertness: `mpi, omp` with gates off ==
    //    the plain omp kernel, byte-identical C++.
    // ---------------------------------------------------------------
    printfn "\n--- degradation: hybrid == omp-only with gates off ---"
    let denseSrc (clause: string) = sprintf """
type RIdx = Idx<4>
type CIdx = Idx<3>
let A: Array<Float64 like RIdx, CIdx> = [
    [1.0, 2.0, 3.0],
    [2.0, 4.0, 6.0],
    [4.0, 3.0, 2.0],
    [5.0, 5.0, 5.0]]
let R = method_for(A) <@> lambda(x) where %s -> x * 2.0 + 1.0 |> compute
"""
                                        clause
    // Same testName for both: it is embedded in the emitted timing line,
    // and the comparison must be over the CODE, not the label.
    (match codegenOf "hyb_degrade" (denseSrc "mpi, omp(x: 1)"),
           codegenOf "hyb_degrade" (denseSrc "omp(x: 1)") with
     | Ok a, Ok b ->
         check "gates off: `mpi, omp` emits byte-identical C++ to `omp`" (a = b)
             (sprintf "lengths %d vs %d" a.Length b.Length)
     | Error e, _ | _, Error e -> check "gates off degradation" false e)

    // ---------------------------------------------------------------
    // 3. mpi+cuda over a DENSE rectangular nest: loud not-yet-emitted
    //    guard (the simplicial hybrid IS emitted — tested below); codegen
    //    only, no nvcc needed.
    // ---------------------------------------------------------------
    printfn "\n--- mpi+cuda dense: loud not-yet-emitted guard ---"
    (try
        try
            CodeGen.setCudaEmitMode true
            CodeGen.setMpiEmitMode true
            match lower (denseSrc "mpi, cuda(block: 64)") with
            | Error e -> check "mpi+cuda dense lowers" false e
            | Ok ir ->
                (try
                    CodeGen.genSelfContainedProgramFromIR ir "hyb_mpicuda_dense" |> ignore
                    check "mpi+cuda dense: loud not-yet-emitted guard" false "codegen succeeded?"
                 with ex ->
                    check "mpi+cuda dense: loud not-yet-emitted guard"
                        (ex.Message.Contains "dense rectangular nests") ex.Message)
        finally
            CodeGen.setCudaEmitMode false
            CodeGen.setMpiEmitMode false
     with ex -> check "mpi+cuda dense guard" false ex.Message)

    // ---------------------------------------------------------------
    // 4. Hybrid differentials (need mpiexec; skip otherwise): dense,
    //    simplicial comoment, and <&!> co-fusion — serial oracle vs
    //    mpiexec -n {1,3} × OMP_NUM_THREADS {1,4}.
    // ---------------------------------------------------------------
    printfn "\n--- hybrid differentials (mpi outer, omp inner) ---"
    let simplicialSrc (clause: string) = sprintf """
type VarIdx = Idx<4>
type TimeIdx = Idx<3>
let A: Array<Float64 like VarIdx, TimeIdx> = [
    [1.0, 2.0, 3.0],
    [2.0, 4.0, 6.0],
    [4.0, 3.0, 2.0],
    [5.0, 5.0, 5.0]]
let m2 = method_for(A, A) <@> lambda(x: Array<Float64 like TimeIdx>, y: Array<Float64 like TimeIdx>) where comm(x, y), %s -> prodsum(x, y) / 3.0 |> compute
"""
                                             clause
    let cofuseSrc (clause: string) = sprintf """
type RIdx = Idx<4>
type CIdx = Idx<3>
let A: Array<Float64 like RIdx, CIdx> = [
    [1.0, 2.0, 3.0],
    [2.0, 4.0, 6.0],
    [4.0, 3.0, 2.0],
    [5.0, 5.0, 5.0]]
let (u, v) = (method_for(A) <@> lambda(x) where %s -> x * 2.0 + 1.0) <&!> (method_for(A) <@> lambda(x) where %s -> x + 100.0) |> compute
"""
                                         clause clause
    if Blade.Build.mpiexecPath.Value.IsNone then
        printfn "  SKIP hybrid differentials: mpiexec not found"
    else
        let hybridDifferential (label: string) (src: string) (expectMarkers: (string * string) list) =
            match compileRunSerial (sprintf "hyb_%s_ref" label) src with
            | Error e -> printfn "  SKIP hybrid %s: serial reference failed (%s)" label e
            | Ok refOut ->
                try
                    try
                        CodeGen.setMpiEmitMode true
                        match codegenOf (sprintf "hyb_%s_mpi" label) src with
                        | Error e -> check (sprintf "hybrid %s: lowers under gate" label) false e
                        | Ok cpp ->
                            for (what, marker) in expectMarkers do
                                check (sprintf "hybrid %s: %s" label what) (cpp.Contains marker)
                                    (sprintf "marker '%s' missing" marker)
                            CodeGen.deployRuntimeHeaders outDir
                            let f = Path.Combine(outDir, sprintf "hyb_%s_mpi.cpp" label)
                            File.WriteAllText(f, cpp)
                            (match compileCpp f outDir with
                             | Error e ->
                                 if isSkipError e then printfn "  SKIP hybrid %s (compile skipped): %s" label e
                                 else check (sprintf "hybrid %s: compiles" label) false e
                             | Ok exe ->
                                 for ranks in [1; 3] do
                                     for threads in ["1"; "4"] do
                                         let prior = Environment.GetEnvironmentVariable "OMP_NUM_THREADS"
                                         Environment.SetEnvironmentVariable("OMP_NUM_THREADS", threads)
                                         (try
                                             match runExecutableMpi ranks exe with
                                             | Ok (0, out) ->
                                                 check (sprintf "hybrid %s: -n %d × OMP %s == serial" label ranks threads)
                                                     (normalize out = normalize refOut)
                                                     (sprintf "hybrid: %s" ((normalize out).Substring(0, min 160 (normalize out).Length)))
                                             | Ok (code, out) ->
                                                 check (sprintf "hybrid %s: -n %d × OMP %s runs" label ranks threads) false
                                                     (sprintf "exit %d: %s" code (out.Substring(0, min 160 out.Length)))
                                             | Error e -> check (sprintf "hybrid %s: -n %d × OMP %s runs" label ranks threads) false e
                                          finally
                                             Environment.SetEnvironmentVariable("OMP_NUM_THREADS", prior)))
                    finally
                        CodeGen.setMpiEmitMode false
                with ex -> check (sprintf "hybrid %s" label) false ex.Message
        hybridDifferential "dense" (denseSrc "mpi, omp(x: 1)")
            [ ("thread-aware MPI init", "MPI_THREAD_FUNNELED")
              ("omp pragma present", "#pragma omp")
              ("slab decomposition present", "__blade_mpi_lo_") ]
        hybridDifferential "simplicial" (simplicialSrc "mpi, omp(x: 1)")
            [ ("thread-aware MPI init", "MPI_THREAD_FUNNELED")
              ("cell-range pragma", "#pragma omp parallel for")
              ("packed cell-range loop", "__blade_c") ]
        hybridDifferential "cofuse" (cofuseSrc "mpi, omp(x: 1)")
            [ ("thread-aware MPI init", "MPI_THREAD_FUNNELED")
              ("shared-slab omp pragma", "#pragma omp")
              ("shared slab bounds", "__blade_mpi_lo_") ]
        // Pure-mpi keeps plain MPI_Init byte-for-byte (regen-diff discipline).
        (try
            try
                CodeGen.setMpiEmitMode true
                match codegenOf "hyb_puretest" (simplicialSrc "mpi") with
                | Ok cpp ->
                    check "pure mpi: keeps MPI_Init (no thread-aware init)"
                        (cpp.Contains "MPI_Init(&argc, &argv);" && not (cpp.Contains "MPI_THREAD_FUNNELED")) ""
                | Error e -> check "pure mpi: lowers" false e
            finally
                CodeGen.setMpiEmitMode false
         with ex -> check "pure mpi init check" false ex.Message)
        // Co-fusion inner-backend agreement under <&!>: mixed omp/serial
        // inner rejects with steering.
        (try
            try
                CodeGen.setMpiEmitMode true
                let mixedSrc = """
type RIdx = Idx<4>
type CIdx = Idx<3>
let A: Array<Float64 like RIdx, CIdx> = [
    [1.0, 2.0, 3.0],
    [2.0, 4.0, 6.0],
    [4.0, 3.0, 2.0],
    [5.0, 5.0, 5.0]]
let (u, v) = (method_for(A) <@> lambda(x) where mpi, omp(x: 1) -> x * 2.0) <&!> (method_for(A) <@> lambda(x) where mpi -> x + 1.0) |> compute
"""
                match lower mixedSrc with
                | Error e -> check "cofuse mixed-inner: lowers" false e
                | Ok ir ->
                    let (cpp, warnings) = CodeGen.genSelfContainedProgramFromIR ir "hyb_mixed_inner"
                    let combined = cpp + "\n" + String.concat "\n" warnings
                    check "cofuse mixed-inner under <&!>: loud agreement reject"
                        (combined.Contains "mixed serial/omp INNER")
                        (combined.Substring(0, min 300 combined.Length))
            finally
                CodeGen.setMpiEmitMode false
         with ex ->
            check "cofuse mixed-inner under <&!>: loud agreement reject"
                (ex.Message.Contains "mixed serial/omp INNER") ex.Message)

    // ---------------------------------------------------------------
    // 5. MPI/CUDA (phase 3): rank-scoped device launches over packed
    // cell-ranges + cell-range Allgatherv. Needs nvcc + GPU + cl.exe +
    // mpiexec; skips cleanly otherwise (CudaTests discipline). The .cu is
    // built as a self-contained MSVC DLL (nvcc -shared) linked directly by
    // the g++/-lmsmpi host.
    // ---------------------------------------------------------------
    printfn "\n--- mpi+cuda (phase 3): rank-scoped simplicial launches ---"
    (let caps = Blade.Build.capabilities.Value
     let mpicudaSrc (clause: string) = sprintf """
type NIdx = Idx<6>
let A: Array<Float64 like NIdx> = [1.0, 2.0, 4.0, 8.0, 16.0, 32.0]
let m2 = method_for(A, A) <@> lambda(x, y) where comm(x, y), %s -> x * y |> compute
"""
                                          clause
     if not (caps.HasNvcc && caps.HasGpu && caps.HasCl) then
         printfn "  SKIP mpi+cuda: requires nvcc + GPU + cl.exe (nvcc=%b, gpu=%b, cl=%b)" caps.HasNvcc caps.HasGpu caps.HasCl
     elif Blade.Build.mpiexecPath.Value.IsNone then
         printfn "  SKIP mpi+cuda: mpiexec not found"
     else
         match compileRunSerial "hyb_mpicuda_ref" (mpicudaSrc "mpi, cuda(block: 64)") with
         | Error e -> printfn "  SKIP mpi+cuda: serial reference failed (%s)" e
         | Ok refOut ->
             try
                 try
                     CodeGen.setMpiEmitMode true
                     CodeGen.setCudaEmitMode true
                     match lower (mpicudaSrc "mpi, cuda(block: 64)") with
                     | Error e -> check "mpi+cuda: lowers under both gates" false e
                     | Ok ir ->
                         let (cpp, _) = CodeGen.genSelfContainedProgramFromIR ir "hyb_mpicuda"
                         match CodeGen.getCudaFileContent () with
                         | None -> check "mpi+cuda: emits a .cu" false "no cuda kernels collected"
                         | Some cu ->
                             check "mpi+cuda: rank-scoped kernel (lo/hi range params)"
                                 (cu.Contains "__blade_rlo" && cu.Contains "__blade_rhi") ""
                             check "mpi+cuda: per-rank device selection"
                                 (cu.Contains "cudaSetDevice") ""
                             check "mpi+cuda: dllexport'd wrapper (DLL build)"
                                 (cu.Contains "__declspec(dllexport)") ""
                             check "mpi+cuda: host restores pool via cell-range Allgatherv"
                                 (cpp.Contains "MPI_Allgatherv" && cpp.Contains "__blade_mpi_lo_") ""
                             CodeGen.deployRuntimeHeaders outDir
                             let cppFile = Path.Combine(outDir, "hyb_mpicuda.cpp")
                             let cuFile = Path.Combine(outDir, "hyb_mpicuda.cu")
                             File.WriteAllText(cppFile, cpp)
                             File.WriteAllText(cuFile, cu)
                             (match compileCudaMpiHybrid cuFile cppFile outDir with
                              | Error e ->
                                  if isSkipError e then printfn "  SKIP mpi+cuda (compile skipped): %s" e
                                  else check "mpi+cuda: hybrid build (nvcc DLL + g++ host)" false e
                              | Ok exe ->
                                  check "mpi+cuda: hybrid build (nvcc DLL + g++ host)" true ""
                                  for ranks in [1; 3] do
                                      (match runExecutableMpi ranks exe with
                                       | Ok (0, out) ->
                                           check (sprintf "mpi+cuda: -n %d == serial" ranks)
                                               (normalize out = normalize refOut)
                                               (sprintf "hybrid: %s" ((normalize out).Substring(0, min 160 (normalize out).Length)))
                                       | Ok (code, out) ->
                                           check (sprintf "mpi+cuda: -n %d runs" ranks) false
                                               (sprintf "exit %d: %s" code (out.Substring(0, min 200 out.Length)))
                                       | Error e -> check (sprintf "mpi+cuda: -n %d runs" ranks) false e))
                 finally
                     CodeGen.setMpiEmitMode false
                     CodeGen.setCudaEmitMode false
             with ex -> check "mpi+cuda hybrid" false ex.Message)

    printFooter "Mixed Parallelism (hybrid)" [sprintf "%d passed" passed; sprintf "%d failed" failed]
    if failed > 0 then 1 else 0
