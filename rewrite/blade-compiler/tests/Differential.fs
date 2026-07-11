// Differential symmetry harness: every symmetry case vs an independent F#
// oracle (tests/Oracles.fs) over randomized inputs. Extracted verbatim from
// Main.fs (audit §2.3).
module Blade.Tests.Differential

open System
open Blade
open System.IO
open System.Diagnostics
open System.Runtime.InteropServices
open Blade.IR
open Blade.Types
open Blade.Lowering
open Blade.CodeGen
open Blade.Build
open Blade.Tests.TestHarness
open Blade.Tests.Expect
open Blade.Tests.Oracles

// ===========================================================================
// DIFFERENTIAL SYMMETRY HARNESS
// ===========================================================================
// One test (`differentialSymmetryTest`) returning a single bool: true iff every
// case subroutine agrees with an INDEPENDENT F# oracle. Each case generates
// randomized inputs, computes the dense reference in F# (no symmetry exploited),
// emits .edgi source running the OPTIMIZED Blade path with the oracle values as
// EXPECT, then generates -> compiles -> runs -> value-checks. Because the oracle
// never touches Blade's triangular/strict-offset/conjugate-read logic, a bug in
// that logic makes the values diverge and the case returns false (printing which
// input diverged). Adding a new differential case = appending one bool-returning
// subroutine to the `cases` list in differentialSymmetryTest.
//
// NOTE: this is an end-to-end VALUE check (oracle vs Blade output). When a case
// fails it identifies the operation and the diverging input, but not the faulty
// compiler layer — diagnosis (emitted .cpp, etc.) still follows from there.

/// Shared core: given a case name and .edgi source carrying an F#-computed
/// EXPECT, run the full pipeline and return true iff values match. Prints a
/// diagnostic line only on failure (keeps the top-level result a clean bool).
let private runDiffCase (outputDir: string) (caseName: string) (edgiSrc: string) : bool =
    try
        match lower edgiSrc with
        | Error e -> printfn "    [diff:%s] LOWER FAILED: %s" caseName e; false
        | Ok ir0 ->
            let ir = match IR.validateIR ir0 with Ok v -> v | Error _ -> ir0
            let safeName = "diff_" + caseName.Replace(" ", "_").Replace("=", "")
            let (cppCode, _w) = CodeGen.genSelfContainedProgramFromIR ir safeName
            let srcPath = Path.Combine(outputDir, safeName + ".cpp")
            File.WriteAllText(srcPath, cppCode)
            let srcAbs = Path.GetFullPath srcPath
            let exeExt = if RuntimeInformation.IsOSPlatform(OSPlatform.Windows) then ".exe" else ".out"
            let exeAbs = Path.ChangeExtension(srcAbs, exeExt)
            let cpsi = ProcessStartInfo("g++", sprintf "-std=c++17 -O2 -fopenmp -o \"%s\" \"%s\"" exeAbs srcAbs)
            cpsi.RedirectStandardError <- true
            cpsi.UseShellExecute <- false
            cpsi.WorkingDirectory <- Path.GetDirectoryName(srcAbs)
            use cproc = Process.Start(cpsi)
            let cerr = cproc.StandardError.ReadToEndAsync()
            cproc.WaitForExit(60000) |> ignore
            if cproc.ExitCode <> 0 then
                printfn "    [diff:%s] COMPILE FAILED:\n%s" caseName cerr.Result
                false
            else
                let rpsi = ProcessStartInfo(exeAbs)
                rpsi.RedirectStandardOutput <- true
                rpsi.RedirectStandardError <- true
                rpsi.UseShellExecute <- false
                rpsi.WorkingDirectory <- Path.GetDirectoryName(exeAbs)
                use rproc = Process.Start(rpsi)
                let rout = rproc.StandardOutput.ReadToEndAsync()
                rproc.WaitForExit(30000) |> ignore
                let expected = parseExpectedValues edgiSrc
                match checkExpectedValues expected rout.Result with
                | Ok () -> true
                | Error errs ->
                    printfn "    [diff:%s] VALUE MISMATCH: %s" caseName (String.concat "; " errs)
                    false
    with ex ->
        printfn "    [diff:%s] EXCEPTION: %s" caseName ex.Message
        false

/// CASE 1: antisymmetric Reynolds, ranks 2/3, several random A. The rank-3 path
/// is exactly where the strict-offset under-shift bug lived; this would have
/// caught it (the buggy iteration visits non-canonical tuples -> values diverge
/// from the permutation-sum oracle).
let private diffCaseAntisymReynolds (outputDir: string) : bool =
    let rng = mkRng 20260613
    let mutable ok = true
    // rank-2: g(x,y) = 2x + y ; rank-3: g(x,y,z) = x*x*y + z
    let trials =
        [ // (rank, n, kernelEdgi, kernelF#)
          (2, 4, "2.0 * x + y", (fun (v: float[]) -> 2.0*v.[0] + v.[1]))
          (2, 5, "2.0 * x + y", (fun v -> 2.0*v.[0] + v.[1]))
          (3, 4, "x * x * y + z", (fun v -> v.[0]*v.[0]*v.[1] + v.[2]))
          (3, 5, "x * x * y + z", (fun v -> v.[0]*v.[0]*v.[1] + v.[2])) ]
    for (r, n, kEdgi, kFs) in trials do
        // random integer-valued A (keeps float printing exact)
        let a = [| for _ in 1 .. n -> float (rng.Next(1, 7)) |]
        let expected = oracleAntisymReynolds a r kFs
        let aLit = a |> Array.map (sprintf "%g") |> String.concat ", "
        let paramList = (["x"; "y"; "z"; "w"; "u"] |> List.take r) |> String.concat ", "
        let arrArgs = List.replicate r "A" |> String.concat ", "
        let expectedLit = expected |> List.map (sprintf "%g") |> String.concat ", "
        let src =
            sprintf "let A = [%s]\n" aLit +
            sprintf "let L = method_for(%s)\n" arrArgs +
            sprintf "let g = lambda(%s) where comm(%s) -> %s\n" paramList paramList kEdgi +
            "let result = L <@> reynolds(g, Antisymmetric) |> compute\n" +
            sprintf "// EXPECT: result = [%s]\n" expectedLit
        let caseName = sprintf "antisymReynolds_r%d_n%d" r n
        if not (runDiffCase outputDir caseName src) then ok <- false
    ok

/// CASE 2: gram(A,A) complex Hermitian, several random complex A and sizes. First
/// real-data exercise of the Hermitian produce->store->upper-triangle-print path
/// under randomized inputs.
let private diffCaseGramHermitian (outputDir: string) : bool =
    let rng = mkRng 20260614
    let mutable ok = true
    for (m, k) in [ (2,3); (3,2); (3,4); (4,3) ] do
        let re = Array2D.init m k (fun _ _ -> float (rng.Next(-3, 4)))
        let im = Array2D.init m k (fun _ _ -> float (rng.Next(-3, 4)))
        let expected = oracleGramHermitian re im
        // build the complex 2D literal. Force a decimal point: Blade reads bare
        // `2` as Int64, but a Complex128 literal needs Float64 components, so
        // `%g` (which prints 2.0 as "2") would make the literal ill-typed.
        let fl (x: float) = sprintf "%.1f" x
        let rowLit i =
            [ for j in 0 .. k-1 -> sprintf "(%s, %s) : Complex128" (fl re.[i,j]) (fl im.[i,j]) ]
            |> String.concat ", "
        let arrLit =
            [ for i in 0 .. m-1 -> sprintf "    [%s]" (rowLit i) ] |> String.concat ",\n"
        let expectedLit =
            expected |> List.map (fun (r,i) -> sprintf "(%g, %g)" r i) |> String.concat ", "
        let src =
            sprintf "let A: Array<Complex128 like Idx<%d>, Idx<%d>> = [\n%s\n]\n" m k arrLit +
            "let result = gram(A, A)\n" +
            sprintf "// EXPECT: result = [%s]\n" expectedLit
        let caseName = sprintf "gramHermitian_m%d_k%d" m k
        if not (runDiffCase outputDir caseName src) then ok <- false
    ok

/// CASE 3: decompact rank-2 dissolution — symmetric, antisymmetric, and
/// Hermitian. Each dissolves a compact rank-2 source to a DENSE n×n matrix; the
/// oracle expands the compact source independently (symmetric mirror, antisym
/// sign-on-mirror with zero diagonal, Hermitian conjugate-on-mirror) and the
/// dense result is checked row-major. Exercises the decompact scatter arithmetic
/// against a reference that never touches Blade's canonical addressing.
let private diffCaseDecompact (outputDir: string) : bool =
    let rng = mkRng 20260615
    let mutable ok = true
    // --- symmetric & antisym (real), several random A ---
    for n in [ 3; 4; 5 ] do
        let a = [| for _ in 1 .. n -> float (rng.Next(1, 7)) |]
        let aLit = a |> Array.map (sprintf "%g") |> String.concat ", "
        let g = fun (v: float[]) -> 2.0*v.[0] + v.[1]
        // SYMMETRIC: dense[i][j] = src[min,max]
        let symSrc = oracleSymReynolds2 a g
        let symDense =
            [ for i in 0 .. n-1 do
                for j in 0 .. n-1 do
                    yield symSrc.[(min i j, max i j)] ]
        let symExpect = symDense |> List.map (sprintf "%g") |> String.concat ", "
        let symSrcEdgi =
            sprintf "let A = [%s]\n" aLit +
            "let L = method_for(A, A)\n" +
            "let g = lambda(x, y) where comm(x, y) -> 2.0 * x + y\n" +
            "let sym = L <@> reynolds(g) |> compute\n" +
            "let result = decompact(sym, 0)\n" +
            sprintf "// EXPECT: result = [%s]\n" symExpect
        if not (runDiffCase outputDir (sprintf "decompactSym_n%d" n) symSrcEdgi) then ok <- false
        // ANTISYM: dense[i][j] = +src (i<j), -src (i>j), 0 (i==j)
        let antiSrc = oracleAntiReynolds2 a g
        let antiDense =
            [ for i in 0 .. n-1 do
                for j in 0 .. n-1 do
                    if i < j then yield antiSrc.[(i, j)]
                    elif i > j then yield -antiSrc.[(j, i)]
                    else yield 0.0 ]
        let antiExpect = antiDense |> List.map (sprintf "%g") |> String.concat ", "
        let antiSrcEdgi =
            sprintf "let A = [%s]\n" aLit +
            "let L = method_for(A, A)\n" +
            "let g = lambda(x, y) where comm(x, y) -> 2.0 * x + y\n" +
            "let anti = L <@> reynolds(g, Antisymmetric) |> compute\n" +
            "let result = decompact(anti, 0)\n" +
            sprintf "// EXPECT: result = [%s]\n" antiExpect
        if not (runDiffCase outputDir (sprintf "decompactAnti_n%d" n) antiSrcEdgi) then ok <- false
    // --- Hermitian (complex), via gram, several random A ---
    for (m, k) in [ (2,3); (3,2); (3,3) ] do
        let re = Array2D.init m k (fun _ _ -> float (rng.Next(-3, 4)))
        let im = Array2D.init m k (fun _ _ -> float (rng.Next(-3, 4)))
        // Hermitian H[i][j] = sum_t A[i][t]*conj(A[j][t]); dense conjugate mirror.
        let hAt i j =
            let mutable sr = 0.0
            let mutable si = 0.0
            for t in 0 .. k-1 do
                let ar, ai = re.[i,t], im.[i,t]
                let br, bi = re.[j,t], im.[j,t]
                sr <- sr + (ar*br + ai*bi)
                si <- si + (ai*br - ar*bi)
            (sr, si)
        let dense =
            [ for i in 0 .. m-1 do
                for j in 0 .. m-1 do
                    // hAt i j computes the FULL H[i][j] = sum_t A[i][t]*conj(A[j][t])
                    // directly for any (i,j) — it already yields the conjugated
                    // lower triangle (H[j][i] = conj(H[i][j])), so no extra flip.
                    yield hAt i j ]
        let fl (x: float) = sprintf "%.1f" x
        let rowLit i = [ for j in 0 .. k-1 -> sprintf "(%s, %s) : Complex128" (fl re.[i,j]) (fl im.[i,j]) ] |> String.concat ", "
        let arrLit = [ for i in 0 .. m-1 -> sprintf "    [%s]" (rowLit i) ] |> String.concat ",\n"
        let expectLit = dense |> List.map (fun (r,i) -> sprintf "(%g, %g)" r i) |> String.concat ", "
        let src =
            sprintf "let A: Array<Complex128 like Idx<%d>, Idx<%d>> = [\n%s\n]\n" m k arrLit +
            "let H = gram(A, A)\n" +
            "let result = decompact(H, 0)\n" +
            sprintf "// EXPECT: result = [%s]\n" expectLit
        if not (runDiffCase outputDir (sprintf "decompactHerm_m%d_k%d" m k) src) then ok <- false
    ok

/// The single differential test: true iff every case agrees with its oracle.
/// Skips cleanly (returns true) if g++ is unavailable, mirroring the other
/// C++-dependent harness phases.
let runDifferentialSymmetryTest () : Blade.Tests.TestHarness.BlockResult =
    let outputDir = "./generated_cpp_tests"
    printHeader "Differential Symmetry"
    let caps = capabilities.Value
    if not caps.HasGpp then
        printfn "Skipped: g++ not found (cannot compile differential cases)."
        { Block = "Differential Symmetry"; Passed = 0; Failed = 0; Skipped = 0; FailedNames = [] }
    else
        Directory.CreateDirectory(outputDir) |> ignore
        let cases : (string * (string -> bool)) list =
            [ "antisym-reynolds", diffCaseAntisymReynolds
              "gram-hermitian",   diffCaseGramHermitian
              "decompact",        diffCaseDecompact ]
        let results = cases |> List.map (fun (nm, f) ->
            let r = f outputDir
            let outcome = if r then Blade.Tests.TestHarness.Pass else Blade.Tests.TestHarness.Fail
            Blade.Tests.TestHarness.resultLine outcome nm ""
            (nm, r))
        let passed = results |> List.filter snd |> List.length
        let failedNames = results |> List.filter (fun (_, r) -> not r) |> List.map fst
        let allOk = failedNames.IsEmpty
        printFooter "Differential Symmetry" [if allOk then "PASS" else "FAIL"]
        { Block = "Differential Symmetry"; Passed = passed; Failed = failedNames.Length; Skipped = 0; FailedNames = failedNames }
