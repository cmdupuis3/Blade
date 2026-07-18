// Device-buffer type unit tests and the differential CUDA kernel tests
// (cuda-vs-host codegen equivalence). Extracted verbatim from Main.fs
// (audit §2.3). Requires nvcc + a CUDA GPU (+ cl.exe on Windows, i.e. the
// "x64 Native Tools" prompt); skips cleanly otherwise.
module Blade.Tests.CudaTests

open System
open Blade
open System.IO
open System.Diagnostics
open System.Runtime.InteropServices
open Blade.Ast
open Blade.IR
open Blade.Types
open Blade.Lowering
open Blade.CodeGen
open Blade.Build
open Blade.Tests.TestHarness

/// F# unit tests for the DeviceBufferType dimensional-type machinery (the
/// foundation for CUDA buffer streaming). Verifies cardinality computation —
/// the load-bearing arithmetic that, if wrong, would silently corrupt the
/// device buffer mapping (and there is no CPU oracle once on hardware, so this
/// must be checked HERE against hand-computed values). Pure F#, no g++.
let runBufferTypeTests () : Blade.Tests.TestHarness.BlockResult =
    printHeader "Device Buffer Type Tests"
    let mutable failures = 0
    let mutable passed = 0
    let mutable failedNames = []
    let lit n = IRLit (IRLitInt (int64 n))
    // A buffer dim group constructor for the test
    let grp rank ext symm : BufferDimGroup =
        { Rank = rank; Extent = lit ext; Symmetry = symm
          Kind = (if symm = SymNone then TDimension else SDimension)
          Dependencies = [] }
    // Rectangular SDimension group (Rank 1, SymNone, but SDimension)
    let rectS ext : BufferDimGroup =
        { Rank = 1; Extent = lit ext; Symmetry = SymNone
          Kind = SDimension; Dependencies = [] }
    let card (groups: BufferDimGroup list) =
        match deviceBufferCardinality { ElemType = IRTScalar ETFloat64; Groups = groups } with
        | IRLit (IRLitInt n) -> Some n
        | _ -> None
    let pass name detail =
        passed <- passed + 1
        Blade.Tests.TestHarness.resultLine Blade.Tests.TestHarness.Pass name detail
    let fail name detail =
        failures <- failures + 1
        failedNames <- failedNames @ [name]
        Blade.Tests.TestHarness.resultLine Blade.Tests.TestHarness.Fail name detail
    let check name (groups: BufferDimGroup list) (expected: int64) =
        match card groups with
        | Some n when n = expected ->
            pass name (sprintf "=> %d" n)
        | Some n ->
            fail name (sprintf "=> %d (expected %d)" n expected)
        | None ->
            fail name (sprintf "=> non-literal (expected %d)" expected)
    // Rectangular: 8 x 8 = 64
    check "rect 8x8" [rectS 8; rectS 8] 64L
    // Rectangular 1-D: 12
    check "rect 12" [rectS 12] 12L
    // Symmetric SymIdx<2> over n=5: C(5+2-1, 2) = C(6,2) = 15
    check "sym2 n=5" [grp 2 5 SymSymmetric] 15L
    // Symmetric SymIdx<3> over n=4: C(4+3-1,3) = C(6,3) = 20
    check "sym3 n=4" [grp 3 4 SymSymmetric] 20L
    // Antisymmetric AntisymIdx<2> over n=5: C(5,2) = 10
    check "antisym2 n=5" [grp 2 5 SymAntisymmetric] 10L
    // Antisymmetric AntisymIdx<3> over n=5: C(5,3) = 10
    check "antisym3 n=5" [grp 3 5 SymAntisymmetric] 10L
    // Hermitian = same storage count as symmetric: C(6,2) = 15
    check "herm2 n=5" [grp 2 5 SymHermitian] 15L
    // Product symmetry: symmetric(n=5,r=2)=15 times rectangular 4 = 60
    check "sym2 x rect4" [grp 2 5 SymSymmetric; rectS 4] 60L
    // isRectangularConstBuffer predicate
    let rectBt = { ElemType = IRTScalar ETFloat64; Groups = [rectS 8; rectS 8] }
    let symBt = { ElemType = IRTScalar ETFloat64; Groups = [grp 2 5 SymSymmetric] }
    if isRectangularConstBuffer rectBt then pass "isRectangular(rect)" "true"
    else fail "isRectangular(rect)" "should be true"
    if not (isRectangularConstBuffer symBt) then pass "isRectangular(sym)" "false"
    else fail "isRectangular(sym)" "should be false"
    // extern "C" boundary-safety gate: fundamental scalars cross, library types don't.
    let checkBnd name ty expected =
        if isCudaBoundarySafeElem ty = expected then pass (sprintf "boundary(%s)" name) (sprintf "%b" expected)
        else fail (sprintf "boundary(%s)" name) (sprintf "should be %b" expected)
    checkBnd "f64" (IRTScalar ETFloat64) true
    checkBnd "f32" (IRTScalar ETFloat32) true
    checkBnd "i64" (IRTScalar ETInt64) true
    checkBnd "i32" (IRTScalar ETInt32) true
    checkBnd "bool" (IRTScalar ETBool) true
    checkBnd "complex128" (IRTScalar ETComplex128) false
    checkBnd "string" (IRTScalar ETString) false
    printFooter "Buffer Type" [sprintf "%d passed" passed; sprintf "%d failure(s)" failures]
    { Block = "Buffer Type"; Passed = passed; Failed = failures; Skipped = 0; FailedNames = failedNames }

/// First `where cuda` hardware test. Differential: generate the SAME rank-1 map
/// program twice — once WITHOUT a cuda clause (host-loop oracle, g++), once WITH
/// `cuda(block: N)` (device kernel, split-compiled nvcc+g++ then linked) — run
/// both, and require identical output. This verifies cuda-vs-host CODEGEN
/// equivalence (not just cuda-vs-hand-math). SKIPs cleanly when nvcc/GPU absent,
/// so the harness stays green on non-CUDA machines; runs for real where a GPU is
/// present. Rank-1 is the simplest kernel: flat thread index IS the coordinate
/// (no div/mod recovery).
let runCudaTests () : Blade.Tests.TestHarness.BlockResult =
    printHeader "CUDA Kernel Tests"
    let skipResult = { Block = "CUDA Kernel"; Passed = 0; Failed = 0; Skipped = 0; FailedNames = [] }
    let caps = capabilities.Value
    let onWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
    if not caps.HasNvcc || not caps.HasGpu then
        printfn "Skipped: requires nvcc + CUDA GPU (nvcc=%b, gpu=%b)." caps.HasNvcc caps.HasGpu
        skipResult  // skip, not failure — mirrors harness skip policy
    elif (not onWindows) && not caps.HasGpp then
        // g++ is the host compiler only on the Linux path; Windows uses nvcc/cl.
        printfn "Skipped: requires g++ for the host half (Linux path)."
        skipResult
    elif onWindows && not caps.HasCl then
        // On Windows nvcc drives MSVC's cl.exe as its host compiler. If cl.exe
        // isn't on PATH (e.g. running from a plain terminal rather than the
        // "x64 Native Tools Command Prompt for VS"), nvcc fails with
        // "Cannot find compiler 'cl.exe'". Skip cleanly here — same policy as
        // resolveCompile's RequiresCuda/PWindows/not-HasCl branch — rather than
        // attempt a compile that's guaranteed to fail. To actually run this
        // test on Windows, launch from the VS Native Tools prompt (or run
        // vcvars64.bat first) so cl.exe is on PATH.
        printfn "Skipped: nvcc needs cl.exe (MSVC) as host compiler, not found on PATH."
        printfn "         Run from the 'x64 Native Tools Command Prompt for VS' (or after vcvars64.bat)."
        skipResult
    else
        let outputDir = "./generated_cpp_tests"
        Directory.CreateDirectory(outputDir) |> ignore
        CodeGen.deployRuntimeHeaders outputDir

        // Compile a plain host .cpp (the oracle) without MinGW on Windows: there
        // we use nvcc to drive cl.exe (single MSVC toolchain, consistent with the
        // cuda variant's host half). On Linux, g++ via compileCpp.
        let compileHost (cppFile: string) : Result<string, string> =
            if onWindows then
                let cppFull = Path.GetFullPath(cppFile)
                let exeFull = Path.ChangeExtension(cppFull, ".exe")
                let args = sprintf "-std=c++17 -O2 -o \"%s\" \"%s\"" exeFull cppFull
                runProc "nvcc" args 120000 |> Result.map (fun () -> exeFull)
            else compileCpp cppFile outputDir

        // Generate one variant: lower -> validate -> codegen -> write .cpp (+ .cu
        // if a kernel was emitted). Returns (cppFile, optional cuFile).
        let genVariant (name: string) (src: string) : Result<string * string option, string> =
            try
                match lower src with
                | Error e -> Error (sprintf "lower failed: %s" e)
                | Ok ir0 ->
                    let ir = match IR.validateIR ir0 with Ok v -> v | Error _ -> ir0
                    let (cppCode, _w) = CodeGen.genSelfContainedProgramFromIR ir name
                    let cuOpt = CodeGen.getCudaFileContent ()
                    let safe = sanitizeFileName name
                    let cppFile = Path.Combine(outputDir, safe + ".cpp")
                    File.WriteAllText(cppFile, cppCode)
                    let cuFileOpt =
                        cuOpt |> Option.map (fun cu ->
                            let cuFile = Path.Combine(outputDir, safe + ".cu")
                            File.WriteAllText(cuFile, cu)
                            cuFile)
                    Ok (cppFile, cuFileOpt)
            with ex -> Error (sprintf "codegen failed: %s" ex.Message)

        let resultLines (s: string) =
            (s.Replace("\r\n", "\n").Trim()).Split('\n')
            |> Array.filter (fun l -> not (l.Contains("completed in")))
            |> String.concat "\n"

        // One differential case: the SAME program with and without `where cuda`.
        // host (no clause, must emit NO .cu) vs cuda (clause, must emit a .cu);
        // both run, outputs must match (cuda-vs-host codegen equivalence).
        // Returns 0 on pass, 1 on failure; prints a labeled line either way.
        let runCudaCase (label: string) (hostSrc: string) (cudaSrc: string) : int =
            let hostName = sprintf "cuda_%s_host" label
            let cudaName = sprintf "cuda_%s_dev" label
            // Force-clean stale artifacts for THIS case (the generated dir
            // persists across runs / version unzips).
            for stem in [hostName; cudaName] do
                for ext in [".cu"; ".cpp"; ".cu.obj"; ".cpp.obj"; ".cu.o"; ".cpp.o"; ".exe"; ".out"] do
                    let f = Path.Combine(outputDir, stem + ext)
                    try if File.Exists f then File.Delete f with _ -> ()
            let hostOut =
                // Host variant: CUDA emission OFF -> `cuda` clause stays inert,
                // no .cu emitted, pure host-loop codegen (the oracle).
                CodeGen.setCudaEmitMode false
                match genVariant hostName hostSrc with
                | Error e -> Error e
                | Ok (cppFile, cuOpt) ->
                    if cuOpt.IsSome then Error "host variant unexpectedly emitted a .cu"
                    else
                        match compileHost cppFile with
                        | Error e -> Error (sprintf "host compile: %s" e)
                        | Ok exe -> runExecutable exe |> Result.map snd
            let cudaOut =
                // CUDA variant: emission ON -> kernel + launch emitted into .cu/.cpp.
                CodeGen.setCudaEmitMode true
                let r =
                    match genVariant cudaName cudaSrc with
                    | Error e -> Error e
                    | Ok (_cppFile, None) -> Error "cuda variant did not emit a .cu (kernel not generated)"
                    | Ok (cppFile, Some cuFile) ->
                        match compileCudaSplit cuFile cppFile outputDir with
                        | Error e -> Error (sprintf "cuda split-compile: %s" e)
                        | Ok exe -> runExecutable exe |> Result.map snd
                CodeGen.setCudaEmitMode false
                r
            match hostOut, cudaOut with
            | Error e, _ -> Blade.Tests.TestHarness.resultLine Blade.Tests.TestHarness.Fail label (sprintf "host oracle: %s" e); 1
            | _, Error e -> Blade.Tests.TestHarness.resultLine Blade.Tests.TestHarness.Fail label (sprintf "cuda: %s" e); 1
            | Ok hOut, Ok cOut ->
                let h, c = resultLines hOut, resultLines cOut
                if h = c then
                    Blade.Tests.TestHarness.resultLine Blade.Tests.TestHarness.Pass label "cuda matches host-loop oracle"
                    0
                else
                    Blade.Tests.TestHarness.resultLine Blade.Tests.TestHarness.Fail label "cuda output differs from host oracle"
                    printfn "    host: %s" h
                    printfn "    cuda: %s" c
                    1

        // Host-only compile+run check (no cuda variant). Used for cases that
        // aren't cuda-eligible but exercise a host codegen path we want to keep
        // honest under MSVC — notably a genuinely SYMMETRIC output, which fires
        // the hasRealSymmetry=true branch (named static symm array passed to
        // allocate). Verifies the program compiles under cl AND its result line
        // contains the expected substring.
        let runHostCompileCase (label: string) (src: string) (expectSubstr: string) : int =
            let nm = sprintf "cuda_%s_host" label
            for ext in [".cu"; ".cpp"; ".cu.obj"; ".cpp.obj"; ".cu.o"; ".cpp.o"; ".exe"; ".out"] do
                let f = Path.Combine(outputDir, nm + ext)
                try if File.Exists f then File.Delete f with _ -> ()
            let outcome =
                match genVariant nm src with
                | Error e -> Error e
                | Ok (cppFile, _) ->
                    match compileHost cppFile with
                    | Error e -> Error (sprintf "host compile: %s" e)
                    | Ok exe -> runExecutable exe |> Result.map snd
            match outcome with
            | Error e -> Blade.Tests.TestHarness.resultLine Blade.Tests.TestHarness.Fail label e; 1
            | Ok out ->
                let r = resultLines out
                if r.Contains(expectSubstr) then
                    Blade.Tests.TestHarness.resultLine Blade.Tests.TestHarness.Pass label "host compiles under MSVC + correct result"
                    0
                else
                    Blade.Tests.TestHarness.resultLine Blade.Tests.TestHarness.Fail label (sprintf "expected substring %s not in output" expectSubstr)
                    printfn "    output: %s" r
                    1

        // A case = (label, hostSrc, cudaSrc). cudaSrc adds `where cuda(block: N)`
        // to the lambda; everything else identical so any diff is a codegen bug.
        // Source variants bound individually first. Multi-line triple-quoted
        // strings whose content dedents to column 0 confuse F#'s layout analysis
        // when placed directly inside a list-of-tuples literal (the column-0
        // `let A = ...` inside the string collides with the enclosing block's
        // offside context). Binding them to names first sidesteps that entirely.
        let rank1Host = """
let A = [1.0, 2.0, 3.0, 4.0, 5.0, 6.0, 7.0, 8.0]
let R = method_for(A) <@> lambda(x) -> x * 2.0 + 1.0 |> compute
"""
        let rank1Cuda = """
let A = [1.0, 2.0, 3.0, 4.0, 5.0, 6.0, 7.0, 8.0]
let R = method_for(A) <@> lambda(x) where cuda(block: 64) -> x * 2.0 + 1.0 |> compute
"""
        // rank-2 outer product: exercises div/mod coordinate recovery AND
        // two-input streaming (the parts rank-1 skips entirely).
        let rank2Host = """
let A = [1.0, 2.0, 3.0]
let B = [4.0, 5.0, 6.0]
let R = method_for(A, B) <@> lambda(x, y) -> x * y |> compute
"""
        let rank2Cuda = """
let A = [1.0, 2.0, 3.0]
let B = [4.0, 5.0, 6.0]
let R = method_for(A, B) <@> lambda(x, y) where cuda(block: 64) -> x * y |> compute
"""
        // multi-block: 8 elements with block size 4 => 2 blocks, so the grid
        // spans multiple blocks (stresses __blade_blocks math + the bound check).
        let mbHost = """
let A = [10.0, 20.0, 30.0, 40.0, 50.0, 60.0, 70.0, 80.0]
let R = method_for(A) <@> lambda(x) -> x * 3.0 |> compute
"""
        let mbCuda = """
let A = [10.0, 20.0, 30.0, 40.0, 50.0, 60.0, 70.0, 80.0]
let R = method_for(A) <@> lambda(x) where cuda(block: 4) -> x * 3.0 |> compute
"""
        // rank-3 with NON-UNIFORM extents (3x2x3=18): two `/=` steps in the
        // coordinate recovery with DISTINCT moduli per dim — the deepest test of
        // the div/mod unpacking (rank-2 used equal extents, which is more forgiving).
        let rank3Host = """
let A = [1.0, 2.0, 3.0]
let B = [10.0, 20.0]
let C = [100.0, 200.0, 300.0]
let R = method_for(A, B, C) <@> lambda(a, b, c) -> a + b + c |> compute
"""
        let rank3Cuda = """
let A = [1.0, 2.0, 3.0]
let B = [10.0, 20.0]
let C = [100.0, 200.0, 300.0]
let R = method_for(A, B, C) <@> lambda(a, b, c) where cuda(block: 64) -> a + b + c |> compute
"""
        // integer element type: exercises int64_t crossing the extern "C"
        // boundary (boundary-safe set includes ETInt64), not just double.
        let intHost = """
let A = [1, 2, 3, 4, 5, 6]
let R = method_for(A) <@> lambda(x) -> x * 10 |> compute
"""
        let intCuda = """
let A = [1, 2, 3, 4, 5, 6]
let R = method_for(A) <@> lambda(x) where cuda(block: 64) -> x * 10 |> compute
"""
        // non-trivial kernel body: a polynomial in x exercises a deeper
        // exprToCpp expression in the kernel (multiple ops, reuse of the bound
        // element), not just a single multiply.
        let polyHost = """
let A = [1.0, 2.0, 3.0, 4.0, 5.0]
let R = method_for(A) <@> lambda(x) -> x * x + x * 2.0 - 1.0 |> compute
"""
        let polyCuda = """
let A = [1.0, 2.0, 3.0, 4.0, 5.0]
let R = method_for(A) <@> lambda(x) where cuda(block: 64) -> x * x + x * 2.0 - 1.0 |> compute
"""
        // larger grid: 50 elements, block 8 => 7 blocks. Bigger grid than the
        // 2-block mb case; the last block is partial (50 = 6*8 + 2), so the
        // bound check is genuinely exercised at a non-trivial tail.
        let bigHost = """
let A = [1.0, 2.0, 3.0, 4.0, 5.0, 6.0, 7.0, 8.0, 9.0, 10.0, 11.0, 12.0, 13.0, 14.0, 15.0, 16.0, 17.0, 18.0, 19.0, 20.0, 21.0, 22.0, 23.0, 24.0, 25.0, 26.0, 27.0, 28.0, 29.0, 30.0, 31.0, 32.0, 33.0, 34.0, 35.0, 36.0, 37.0, 38.0, 39.0, 40.0, 41.0, 42.0, 43.0, 44.0, 45.0, 46.0, 47.0, 48.0, 49.0, 50.0]
let R = method_for(A) <@> lambda(x) -> x + 100.0 |> compute
"""
        let bigCuda = """
let A = [1.0, 2.0, 3.0, 4.0, 5.0, 6.0, 7.0, 8.0, 9.0, 10.0, 11.0, 12.0, 13.0, 14.0, 15.0, 16.0, 17.0, 18.0, 19.0, 20.0, 21.0, 22.0, 23.0, 24.0, 25.0, 26.0, 27.0, 28.0, 29.0, 30.0, 31.0, 32.0, 33.0, 34.0, 35.0, 36.0, 37.0, 38.0, 39.0, 40.0, 41.0, 42.0, 43.0, 44.0, 45.0, 46.0, 47.0, 48.0, 49.0, 50.0]
let R = method_for(A) <@> lambda(x) where cuda(block: 8) -> x + 100.0 |> compute
"""
        // SYMMETRIC output (host-only; cuda gates reject non-rectangular). The
        // `comm(x, y)` on the kernel + same array A twice folds into a SYMMETRIC
        // output (SymIdx), so OutputSymmVec has a repeated group => hasRealSymmetry
        // is TRUE => the named-static symm-array allocate branch fires. This is the
        // branch the v24 fix did NOT change (the else), so this case confirms it
        // still compiles under MSVC and produces correct values. method_for(A, A)
        // with distinct... no: SAME array A is required for the comm to fold.
        let symHost = """
let A = [1.0, 2.0, 3.0, 4.0]
let R = method_for(A, A) <@> lambda(x, y) where comm(x, y) -> x * y |> compute
"""
        // SYMMETRIC rank-2 DIFFERENTIAL case (the first triangular CUDA path):
        // same symmetric Reynolds product with vs without `where cuda`. The cuda
        // variant must emit a .cu (genCudaKernelSimplicial fires: single arity-2
        // symmetric group, const square extent, symmetric — not antisym — fold),
        // run on the device with the triangular unrank, and match the host
        // triangular output exactly. A kernel that DOES touch the symmetry path.
        let symTriHost = """
let A = [1.0, 2.0, 3.0, 4.0, 5.0]
let R = method_for(A, A) <@> lambda(x, y) where comm(x, y) -> 2.0 * x + y |> compute
"""
        let symTriCuda = """
let A = [1.0, 2.0, 3.0, 4.0, 5.0]
let R = method_for(A, A) <@> lambda(x, y) where comm(x, y), cuda(block: 32) -> 2.0 * x + y |> compute
"""
        // ANTISYMMETRIC rank-2 strict-triangular DIFFERENTIAL case. reynolds(g,
        // Antisymmetric) folds to g(x,y)-g(y,x); stored on the strict triangle
        // (i<j). The cuda variant must emit a .cu (genCudaKernelSimplicial fires:
        // single arity-2 antisym group, antisym Reynolds), run the strict unrank
        // + sign on device, and match the host strict-triangular output. Lands on
        // the strict-offset/sign bug class the differential harness guards.
        let antiTriHost = """
let A = [1.0, 2.0, 3.0, 4.0, 5.0]
let g = lambda(x, y) where comm(x, y) -> x * x * y
let R = method_for(A, A) <@> reynolds(g, Antisymmetric) |> compute
"""
        let antiTriCuda = """
let A = [1.0, 2.0, 3.0, 4.0, 5.0]
let g = lambda(x, y) where comm(x, y), cuda(block: 32) -> x * x * y
let R = method_for(A, A) <@> reynolds(g, Antisymmetric) |> compute
"""
        // SYMMETRIC rank-3 INCLUSIVE simplex (i<=j<=k) DIFFERENTIAL case — the
        // first higher-rank triangular CUDA path (2-level simplicial unrank). raw
        // comm kernel over method_for(A,A,A); cuda variant must emit a .cu
        // (genCudaKernelSimplicial fires: single arity-3 symmetric group), run the
        // closed-form outer unrank + rank-2 inner unrank on device, match host.
        let sym3Host = """
let A = [1.0, 2.0, 3.0, 4.0, 5.0]
let R = method_for(A, A, A) <@> lambda(x, y, z) where comm(x, y, z) -> x + 2.0 * y + 3.0 * z |> compute
"""
        let sym3Cuda = """
let A = [1.0, 2.0, 3.0, 4.0, 5.0]
let R = method_for(A, A, A) <@> lambda(x, y, z) where comm(x, y, z), cuda(block: 32) -> x + 2.0 * y + 3.0 * z |> compute
"""
        // ANTISYMMETRIC rank-3 STRICT simplex (i<j<k) DIFFERENTIAL case — the
        // strict higher-rank path (binomial outer start + sign). non-degenerate
        // kernel x*x*y+z (antisymmetrizes to distinct nonzero values).
        let anti3Host = """
let A = [1.0, 2.0, 3.0, 4.0, 5.0]
let g = lambda(x, y, z) where comm(x, y, z) -> x * x * y + z
let R = method_for(A, A, A) <@> reynolds(g, Antisymmetric) |> compute
"""
        let anti3Cuda = """
let A = [1.0, 2.0, 3.0, 4.0, 5.0]
let g = lambda(x, y, z) where comm(x, y, z), cuda(block: 32) -> x * x * y + z
let R = method_for(A, A, A) <@> reynolds(g, Antisymmetric) |> compute
"""
        // RANK-4 and RANK-5 cases — exercise the GENERAL simplicial unrank at
        // higher S-group arity (the depths a closed-form would not cover). sym
        // = inclusive simplex, anti = strict simplex. Non-degenerate kernels.
        let sym4Host = """
let A = [1.0, 2.0, 3.0, 4.0, 5.0, 6.0]
let R = method_for(A, A, A, A) <@> lambda(w, x, y, z) where comm(w, x, y, z) -> w + 2.0 * x + 3.0 * y + 4.0 * z |> compute
"""
        let sym4Cuda = """
let A = [1.0, 2.0, 3.0, 4.0, 5.0, 6.0]
let R = method_for(A, A, A, A) <@> lambda(w, x, y, z) where comm(w, x, y, z), cuda(block: 32) -> w + 2.0 * x + 3.0 * y + 4.0 * z |> compute
"""
        let anti4Host = """
let A = [1.0, 2.0, 3.0, 4.0, 5.0, 6.0]
let g = lambda(w, x, y, z) where comm(w, x, y, z) -> x * y * y * z * z * z
let R = method_for(A, A, A, A) <@> reynolds(g, Antisymmetric) |> compute
"""
        let anti4Cuda = """
let A = [1.0, 2.0, 3.0, 4.0, 5.0, 6.0]
let g = lambda(w, x, y, z) where comm(w, x, y, z), cuda(block: 32) -> x * y * y * z * z * z
let R = method_for(A, A, A, A) <@> reynolds(g, Antisymmetric) |> compute
"""
        let sym5Host = """
let A = [1.0, 2.0, 3.0, 4.0, 5.0, 6.0]
let R = method_for(A, A, A, A, A) <@> lambda(a, b, c, d, e) where comm(a, b, c, d, e) -> a + 2.0 * b + 3.0 * c + 4.0 * d + 5.0 * e |> compute
"""
        let sym5Cuda = """
let A = [1.0, 2.0, 3.0, 4.0, 5.0, 6.0]
let R = method_for(A, A, A, A, A) <@> lambda(a, b, c, d, e) where comm(a, b, c, d, e), cuda(block: 32) -> a + 2.0 * b + 3.0 * c + 4.0 * d + 5.0 * e |> compute
"""
        let anti5Host = """
let A = [1.0, 2.0, 3.0, 4.0, 5.0, 6.0]
let g = lambda(a, b, c, d, e) where comm(a, b, c, d, e) -> b * c * c * d * d * d * e * e * e * e
let R = method_for(A, A, A, A, A) <@> reynolds(g, Antisymmetric) |> compute
"""
        let anti5Cuda = """
let A = [1.0, 2.0, 3.0, 4.0, 5.0, 6.0]
let g = lambda(a, b, c, d, e) where comm(a, b, c, d, e), cuda(block: 32) -> b * c * c * d * d * d * e * e * e * e
let R = method_for(A, A, A, A, A) <@> reynolds(g, Antisymmetric) |> compute
"""
        // Arc-8 probe: the arc-1 FUSED JOINT level (one identity group over a
        // 2-D array -> single compound axis, joint SymIdx<2, 6> output) under
        // the cuda clause. Device-side element access must decode per-dim
        // coordinates from the compound index (row-major) exactly like the
        // host fused arm — or the emitter must decline cleanly to the host
        // loop. Either way host and cuda variants must agree on values.
        let joint2dHost = """
let A: Array<Float64 like Idx<2>, Idx<3>> = [[1.0, 2.0, 3.0], [4.0, 5.0, 6.0]]
let R = method_for(A, A) <@> lambda(x, y) where comm(x, y) -> x * y |> compute
"""
        let joint2dCuda = """
let A: Array<Float64 like Idx<2>, Idx<3>> = [[1.0, 2.0, 3.0], [4.0, 5.0, 6.0]]
let R = method_for(A, A) <@> lambda(x, y) where comm(x, y), cuda(block: 32) -> x * y |> compute
"""
        // CO-FUSION: two SAME-ARITY cuda leaves over the SAME input, <&!>-fused
        // into ONE device launch (genCudaCoFusion). The host oracle fuses them
        // into one serial nest; the cuda variant emits a single __global__ with
        // two output buffers. Values must match. rank-1 (flat grid, shared input
        // loaded once) and rank-2 (div/mod recovery shared across both writes).
        let cofuse1Host = """
let A = [1.0, 2.0, 3.0, 4.0, 5.0, 6.0, 7.0, 8.0]
let (u, v) = (method_for(A) <@> lambda(x) -> x * 2.0 + 1.0) <&!> (method_for(A) <@> lambda(x) -> x + 100.0) |> compute
"""
        let cofuse1Cuda = """
let A = [1.0, 2.0, 3.0, 4.0, 5.0, 6.0, 7.0, 8.0]
let (u, v) = (method_for(A) <@> lambda(x) where cuda(block: 64) -> x * 2.0 + 1.0) <&!> (method_for(A) <@> lambda(x) where cuda(block: 64) -> x + 100.0) |> compute
"""
        let cofuse2Host = """
let A = [1.0, 2.0, 3.0]
let B = [4.0, 5.0, 6.0]
let (p, q) = (method_for(A, B) <@> lambda(x, y) -> x * y) <&!> (method_for(A, B) <@> lambda(x, y) -> x + y) |> compute
"""
        let cofuse2Cuda = """
let A = [1.0, 2.0, 3.0]
let B = [4.0, 5.0, 6.0]
let (p, q) = (method_for(A, B) <@> lambda(x, y) where cuda(block: 64) -> x * y) <&!> (method_for(A, B) <@> lambda(x, y) where cuda(block: 64) -> x + y) |> compute
"""
        // <&> SOFT JOIN over independent cuda leaves: leaves that cannot
        // co-fuse (different extents / block sizes / arities) still run as
        // per-leaf kernels, launched via split begin/end wrappers with
        // round-robin device assignment inside the .cu (one device => the
        // default stream serializes; more => the begin pass overlaps). The
        // SAME source serves as its own host oracle (clauses inert gate-off).
        let softRect = """
let A = [1.0, 2.0, 3.0, 4.0, 5.0, 6.0]
let B = [10.0, 20.0, 30.0, 40.0]
let (u, v) = (method_for(A) <@> lambda(x) where cuda(block: 64) -> x * 2.0) <&> (method_for(B) <@> lambda(y) where cuda(block: 32) -> y + 100.0) |> compute
"""
        let softSimp = """
let A = [1.0, 2.0, 3.0, 4.0, 5.0]
let B = [2.0, 4.0, 6.0]
let (m2a, m2b) = (method_for(A, A) <@> lambda(x, y) where comm(x, y), cuda(block: 32) -> x * y) <&> (method_for(B, B) <@> lambda(x, y) where comm(x, y), cuda(block: 64) -> x * y + 1.0) |> compute
"""
        let softMixed = """
let A = [1.0, 2.0, 3.0, 4.0]
let (u, m2) = (method_for(A) <@> lambda(x) where cuda(block: 64) -> x * 3.0) <&> (method_for(A, A) <@> lambda(x, y) where comm(x, y), cuda(block: 32) -> x * y) |> compute
"""
        let cases =
            [ ("rank1", rank1Host, rank1Cuda)
              ("cofuse_rank1", cofuse1Host, cofuse1Cuda)
              ("cofuse_rank2", cofuse2Host, cofuse2Cuda)
              ("rank2_outer", rank2Host, rank2Cuda)
              ("rank1_multiblock", mbHost, mbCuda)
              ("rank3_nonuniform", rank3Host, rank3Cuda)
              ("int_elem", intHost, intCuda)
              ("poly_body", polyHost, polyCuda)
              ("big_multiblock", bigHost, bigCuda)
              ("sym_triangular", symTriHost, symTriCuda)
              ("anti_triangular", antiTriHost, antiTriCuda)
              ("sym3_simplex", sym3Host, sym3Cuda)
              ("anti3_simplex", anti3Host, anti3Cuda)
              ("sym4_simplex", sym4Host, sym4Cuda)
              ("anti4_simplex", anti4Host, anti4Cuda)
              ("sym5_simplex", sym5Host, sym5Cuda)
              ("anti5_simplex", anti5Host, anti5Cuda)
              ("joint_2d", joint2dHost, joint2dCuda)
              ("softjoin_rect", softRect, softRect)
              ("softjoin_simplicial", softSimp, softSimp)
              ("softjoin_mixed", softMixed, softMixed) ]

        let mutable failures = 0
        let mutable passed = 0
        let mutable failedNames = []
        for (label, hostSrc, cudaSrc) in cases do
            let rc = runCudaCase label hostSrc cudaSrc
            if rc = 0 then passed <- passed + 1
            else (failures <- failures + 1; failedNames <- failedNames @ [label])
        // Host-only: symmetric output exercises the hasRealSymmetry=true branch
        // (named static symm array) under MSVC — the branch the v24 fix did NOT
        // change, so this confirms it still compiles under cl. The PRIMARY signal
        // is that it compiles + runs at all (the symm allocation path is the
        // MSVC-sensitive part); we check only that an R array is printed, NOT a
        // specific value (symmetric print ordering/storage isn't asserted here).
        let symRc = runHostCompileCase "sym_output" symHost "R = ["
        if symRc = 0 then passed <- passed + 1
        else (failures <- failures + 1; failedNames <- failedNames @ ["sym_output"])
        // Soft-join STRUCTURE: the .cu must carry per-leaf kernels + split
        // begin/end wrappers with in-wrapper device selection, and the host
        // must sequence EVERY begin before the FIRST end (that ordering is
        // what multi-device overlap exploits). Call sites are distinguished
        // from the extern-C protos by their pool_base(...) arguments.
        let softStructRc =
            let label = "softjoin_structure"
            try
                CodeGen.setCudaEmitMode true
                let outcome =
                    match genVariant "cuda_softjoin_struct" softRect with
                    | Error e -> Error e
                    | Ok (_, None) -> Error "no .cu emitted for the soft join"
                    | Ok (cppFile, Some cuFile) ->
                        let cu = File.ReadAllText cuFile
                        let cpp = File.ReadAllText cppFile
                        let countOf (hay: string) (needle: string) =
                            let mutable c = 0
                            let mutable i = hay.IndexOf needle
                            while i >= 0 do
                                c <- c + 1
                                i <- hay.IndexOf(needle, i + needle.Length)
                            c
                        let kernels = countOf cu "__global__ void __cuda_"
                        let lastBegin = cpp.LastIndexOf "_begin(pool_base"
                        let firstEnd = cpp.IndexOf "_end(pool_base"
                        if kernels <> 2 then Error (sprintf "expected 2 kernels, .cu has %d" kernels)
                        elif not (cu.Contains "cudaGetDeviceCount") then Error "no in-wrapper device query"
                        elif countOf cu "_begin(" < 2 || countOf cu "_end(" < 2 then Error "missing split wrappers"
                        elif lastBegin < 0 || firstEnd < 0 then Error "host begin/end calls not found"
                        elif lastBegin > firstEnd then Error "host does not sequence all begins before ends"
                        else Ok ()
                CodeGen.setCudaEmitMode false
                match outcome with
                | Ok () ->
                    Blade.Tests.TestHarness.resultLine Blade.Tests.TestHarness.Pass label "per-leaf begin/end + device round-robin emitted"
                    0
                | Error e ->
                    Blade.Tests.TestHarness.resultLine Blade.Tests.TestHarness.Fail label e
                    1
            with ex ->
                CodeGen.setCudaEmitMode false
                Blade.Tests.TestHarness.resultLine Blade.Tests.TestHarness.Fail label ex.Message
                1
        if softStructRc = 0 then passed <- passed + 1
        else (failures <- failures + 1; failedNames <- failedNames @ ["softjoin_structure"])
        // <&!> stays HARD: the same unfusable pair under mandatory fusion is
        // still a loud codegen diagnostic, not a silent soft-join.
        let hardRc =
            let label = "softjoin_not_for_hard_join"
            let hardSrc = softRect.Replace("<&>", "<&!>")
            try
                CodeGen.setCudaEmitMode true
                let outcome =
                    match genVariant "cuda_softjoin_hard" hardSrc with
                    | Error e -> Error e
                    | Ok (cppFile, _) ->
                        let cpp = File.ReadAllText cppFile
                        if cpp.Contains "cannot fuse" then Ok ()
                        else Error "expected the <&!> cannot-fuse diagnostic in the emitted host code"
                CodeGen.setCudaEmitMode false
                match outcome with
                | Ok () ->
                    Blade.Tests.TestHarness.resultLine Blade.Tests.TestHarness.Pass label "<&!> still rejects loudly"
                    0
                | Error e ->
                    Blade.Tests.TestHarness.resultLine Blade.Tests.TestHarness.Fail label e
                    1
            with ex ->
                CodeGen.setCudaEmitMode false
                Blade.Tests.TestHarness.resultLine Blade.Tests.TestHarness.Fail label ex.Message
                1
        if hardRc = 0 then passed <- passed + 1
        else (failures <- failures + 1; failedNames <- failedNames @ ["softjoin_not_for_hard_join"])
        printFooter "CUDA Kernel" [sprintf "%d passed" passed; sprintf "%d failure(s)" failures]
        { Block = "CUDA Kernel"; Passed = passed; Failed = failures; Skipped = 0; FailedNames = failedNames }
