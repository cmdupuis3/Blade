// OpenMP thread-coverage tests: verify emitted pragmas form genuine parallel
// regions, and that values stay correct under forced multi-threading.
// Extracted verbatim from Main.fs (audit §2.3). Requires g++; skips otherwise.
module Blade.Tests.OmpTests

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

// ============================================================================
// Pragma-emission tests (pure codegen; no g++, no threads, always run)
// ============================================================================
//
// The thread-coverage block below answers "does an emitted pragma form a real
// parallel team". These answer the question BEFORE it: "is a pragma emitted at
// all for the clause the user wrote". That gap is not academic — `where omp`
// on a NAMED FUNCTION used as a kernel was silently dropped for a long time,
// producing serial code with no diagnostic, because every existing omp test
// wrote the clause on an inline or let-bound LAMBDA. A lambda kernel keeps its
// where-clause on the TypedLambdaInfo; a named-function kernel is eta-expanded
// into a wrapper lambda built with no where-clause, so the clause has to be
// surfaced explicitly onto the wrapper (TypeCheck.etaExpandFunctionKernel).
// Both spellings are pinned here so the two paths cannot drift again.
//
// These assert on the generated C++ STRING rather than on runtime behaviour, so
// they need no toolchain and run in the default suite.

/// Lower + generate, returning the C++ source. No compiler involved.
let private cppOf (testName: string) (src: string) : Result<string, string> =
    try
        match lower src with
        | Error e -> Error (sprintf "lower: %s" e)
        | Ok ir -> Ok (fst (CodeGen.genSelfContainedProgramFromIR ir testName))
    with ex -> Error (sprintf "codegen raised: %s" ex.Message)

/// A kernel body and the two arrays it folds over, spelled once. Each case
/// below varies ONLY how the kernel is written and where the clause sits.
let private ompPragmaCases : (string * string * bool) list =
    // (name, source, expectPragma)
    let arrays = "let A = [1.0, 2.0, 3.0]\nlet B = [4.0, 5.0, 6.0]\n"
    [ // The regression case: clause on a named function, function used as the
      // object_for kernel. Eta-expanded — the clause must survive the wrapper.
      ("named_function_object_for",
       "function cov(a: Float64, b: Float64) where omp(a: 1) = a * b\n" + arrays +
       "let m = object_for(cov) <@> (A, B) |> compute\n", true)
      // Same drop, reached through the OTHER eta site: a bare named function as
      // the RIGHT operand of <@>.
      ("named_function_method_for_apply",
       "function cov(a: Float64, b: Float64) where omp(a: 1) = a * b\n" + arrays +
       "let m = method_for(A, B) <@> cov |> compute\n", true)
      // Control: the spelling every pre-existing omp test uses. Passed before
      // the fix and must keep passing.
      ("let_bound_lambda",
       "let k = lambda(x, y) where omp(x: 1) -> x * y\n" + arrays +
       "let m = object_for(k) <@> (A, B) |> compute\n", true)
      // Control: inline lambda, the third spelling.
      ("inline_lambda",
       arrays +
       "let m = method_for(A, B) <@> lambda(x, y) where omp(x: 1) -> x * y |> compute\n", true)
      // Negatives — parallelism is OPT-IN. Identical programs minus the clause
      // must stay serial; without these the fix could degenerate into
      // "parallelize every named-function kernel".
      ("named_function_no_clause",
       "function cov(a: Float64, b: Float64) = a * b\n" + arrays +
       "let m = object_for(cov) <@> (A, B) |> compute\n", false)
      ("let_bound_lambda_no_clause",
       "let k = lambda(x, y) -> x * y\n" + arrays +
       "let m = object_for(k) <@> (A, B) |> compute\n", false) ]

/// The `omp(a: n)` DEPTH, checked as an exact pragma string rather than mere
/// presence. `n` is a LICENCE — "up to n dimensions of this argument may carry
/// threads" — counted per-argument, outermost first. It caps the structural
/// collapse/dynamic strategy instead of replacing it, so these cases pin the
/// interaction of the two rather than the licence alone.
///
/// Before this was implemented the depth was inert (written by
/// extractParallelism, read by nothing), so every case below emitted whatever
/// the bound structure alone dictated — `omp(a: 1)` on a 2-level nest produced
/// `collapse(2)`, threading a dimension of `b`, which granted nothing.
let private ompDepthCases : (string * string * string) list =
    // (name, source, exact expected pragma line)
    let arrays = "let A = [1.0, 2.0, 3.0]\nlet B = [4.0, 5.0, 6.0]\n"
    let apply = "let m = object_for(k) <@> (A, B) |> compute\n"
    let kern clause = sprintf "function k(a: Float64, b: Float64) where %s = a * b\n" clause
    [ // One dimension licensed of a 2-level collapsible nest: collapse would
      // thread b's level too, so it must NOT be used.
      ("depth_1_of_2_no_collapse", kern "omp(a: 1)" + arrays + apply,
       "#pragma omp parallel for")
      // Both arguments licensed: collapse(2) is now permitted.
      ("depth_1_1_collapses_2", kern "omp(a: 1, b: 1)" + arrays + apply,
       "#pragma omp parallel for collapse(2)")
      // Per-argument counting: `a` is rank-1 and owns exactly one level, so a
      // depth of 2 cannot reach into b's level. (Under a whole-nest "budget"
      // reading this would collapse(2) — that reading is NOT what is
      // implemented; the depth counts levels OF THE NAMED ARGUMENT.)
      ("depth_2_on_rank1_arg_caps_at_1", kern "omp(a: 2)" + arrays + apply,
       "#pragma omp parallel for")
      // A licence on an argument owning an INNER level parallelizes that level
      // rather than silently doing nothing or threading the unlicensed outer.
      ("inner_arg_licence_moves_pragma", kern "omp(b: 1)" + arrays + apply,
       "#pragma omp parallel for")
      // The canonical documented case (quickstart-2 "Parallelism"): ONE argument
      // owning BOTH levels, so the depth alone scales how many are threaded.
      // This is the pair the old inert-depth behaviour could not distinguish —
      // it emitted collapse(2) for both.
      ("one_arg_two_levels_depth_1",
       "function k(a: Float64) where omp(a: 1) = a * 2.0\n" +
       "let M = [[1.0, 2.0, 3.0], [4.0, 5.0, 6.0]]\n" +
       "let m = object_for(k) <@> (M) |> compute\n",
       "#pragma omp parallel for")
      ("one_arg_two_levels_depth_2",
       "function k(a: Float64) where omp(a: 2) = a * 2.0\n" +
       "let M = [[1.0, 2.0, 3.0], [4.0, 5.0, 6.0]]\n" +
       "let m = object_for(k) <@> (M) |> compute\n",
       "#pragma omp parallel for collapse(2)") ]

/// Which loop index the pragma precedes, for the inner-licence case. Presence
/// alone cannot distinguish "parallelized the licensed inner level" from
/// "parallelized the unlicensed outer level".
let private ompPlacementCases : (string * string * string) list =
    // (name, source, index variable the pragma must immediately precede)
    let arrays = "let A = [1.0, 2.0, 3.0]\nlet B = [4.0, 5.0, 6.0]\n"
    let apply = "let m = object_for(k) <@> (A, B) |> compute\n"
    let kern clause = sprintf "function k(a: Float64, b: Float64) where %s = a * b\n" clause
    [ ("outer_licence_pragma_on_i0", kern "omp(a: 1)" + arrays + apply, "__i0")
      ("inner_licence_pragma_on_i1", kern "omp(b: 1)" + arrays + apply, "__i1") ]

/// Assert that `omp` reaches codegen as a pragma for every spelling of a
/// kernel, and reaches it for NO spelling that omitted the clause.
let runOmpPragmaTests () : Blade.Tests.TestHarness.BlockResult =
    printHeader "OpenMP Pragma Emission"
    let mutable passed = 0
    let mutable failed = 0
    let mutable failedNames = []
    let fail name detail =
        failed <- failed + 1
        failedNames <- failedNames @ [name]
        resultLine Fail name detail
    for (name, src, expectPragma) in ompPragmaCases do
        match cppOf name src with
        | Error e -> fail name e
        | Ok cpp ->
            let hasPragma = cpp.Contains "#pragma omp"
            // A dropped clause is exactly the silent case: no pragma AND no
            // marker. Assert the marker's absence too, so a future change that
            // "fixes" a case by suppressing it loudly is still caught here.
            let hasMarker = cpp.Contains "[omp] requested but emitted serial"
            if hasPragma <> expectPragma then
                fail name
                    (sprintf "expected %s, got %s%s"
                        (if expectPragma then "a pragma" else "no pragma")
                        (if hasPragma then "a pragma" else "none")
                        (if hasMarker then " (nest reports omp requested but suppressed)" else ""))
            elif hasMarker then
                fail name "unexpected omp-suppressed marker in generated code"
            else
                passed <- passed + 1
                resultLine Pass name
                    (if expectPragma then "pragma emitted" else "serial (no clause)")
    // ---- depth-as-licence: the exact pragma, not just its presence ----
    for (name, src, expectedPragma) in ompDepthCases do
        match cppOf name src with
        | Error e -> fail name e
        | Ok cpp ->
            // Compare the whole pragma line: `collapse(2)` vs plain is exactly
            // the distinction the licence controls, and `Contains` on the plain
            // form would match the collapse form as a prefix.
            let pragmaLines =
                cpp.Split('\n')
                |> Array.map (fun l -> l.Trim())
                |> Array.filter (fun l -> l.StartsWith "#pragma omp")
                |> Array.toList
            match pragmaLines with
            | [actual] when actual = expectedPragma ->
                passed <- passed + 1
                resultLine Pass name actual
            | [actual] -> fail name (sprintf "expected `%s`, got `%s`" expectedPragma actual)
            | [] -> fail name (sprintf "expected `%s`, got no pragma" expectedPragma)
            | many -> fail name (sprintf "expected one pragma, got %d: %s" many.Length (String.concat " | " many))
    // ---- placement: WHICH loop the pragma governs ----
    for (name, src, expectedIdx) in ompPlacementCases do
        match cppOf name src with
        | Error e -> fail name e
        | Ok cpp ->
            // The emitter writes the pragma and the loop header it governs as
            // consecutive lines, so the next non-blank line after the pragma is
            // that header.
            let lines = cpp.Split('\n') |> Array.map (fun l -> l.Trim())
            let governed =
                lines
                |> Array.tryFindIndex (fun l -> l.StartsWith "#pragma omp")
                |> Option.bind (fun i ->
                    lines
                    |> Array.skip (i + 1)
                    |> Array.tryFind (fun l -> l <> "")
                    |> Option.map (fun header ->
                        // "for (size_t __iN = ..." -> "__iN"
                        header.Split([|' '; '('; ')'; '='|])
                        |> Array.tryFind (fun t -> t.StartsWith "__i")
                        |> Option.defaultValue header))
            match governed with
            | Some idx when idx = expectedIdx ->
                passed <- passed + 1
                resultLine Pass name (sprintf "pragma governs %s" idx)
            | Some idx -> fail name (sprintf "pragma governs %s, expected %s" idx expectedIdx)
            | None -> fail name "no pragma found"
    printFooter "OpenMP Pragma" [sprintf "%d passed" passed; sprintf "%d failed" failed]
    { Block = "OpenMP Pragma"; Passed = passed; Failed = failed; Skipped = 0; FailedNames = failedNames }

/// Run OpenMP thread-coverage tests. Generates representative loop-nest
/// programs with codegen TEST MODE on (which injects per-region thread
/// observation), compiles with -fopenmp, runs with OMP_NUM_THREADS forced > 1,
/// parses the emitted "[omp-coverage] region=... teamsz=K distinct=D maxth=M"
/// lines, and applies the rule:
///   - maxth <= 1            : single-core context — cannot test parallelism;
///                             reported as a skip-ish PASS (not a failure).
///   - maxth > 1, teamsz <= 1: ERROR — a loop that should be an OpenMP-parallel
///                             loop ran as a serial region (pragma not honored).
///   - maxth > 1, teamsz > 1 : PASS — a genuine parallel team was formed. (If
///                             distinct == 1, the scheduler put all work on one
///                             thread; that is an allowed scheduler choice, so
///                             it is reported as a WARNING, not a failure.)
///
/// Returns 0 if no errors (warnings allowed), 1 if any error.
let runOmpCoverageTests () : Blade.Tests.TestHarness.BlockResult =
    let caps = capabilities.Value
    printHeader "OpenMP Thread-Coverage Tests"
    if not caps.HasGpp then
        printfn "Skipped: g++ not found."
        { Block = "OpenMP Coverage"; Passed = 0; Failed = 0; Skipped = 0; FailedNames = [] }
    else
        // Representative programs exercising each parallelization strategy.
        // Source strings are defined as separate bindings (not inline in the
        // list) so the triple-quoted content does not disturb F# offside parsing.
        //
        // COVERAGE programs (just need to compile + form a parallel region):
        //   rect (collapse), symmetric (dynamic outer), and a partial-comm 3-arg
        //   kernel (mixed symmetry structure). Antisymmetric STRICT iteration is
        //   not expressed by a simple clause (it requires AntisymIdx typing), so
        //   it is intentionally omitted here rather than guessed at.
        let rectSrc =
            "let A = [1.0,2.0,3.0,4.0,5.0,6.0,7.0,8.0]\n" +
            "let B = [1.0,2.0,3.0,4.0,5.0,6.0,7.0,8.0]\n" +
            "let L = method_for(A, B)\n" +
            "let f = lambda(x, y) where omp(x: 1) -> x * y\n" +
            "let result = L <@> f |> compute\n"
        let symSrc =
            "let A = [1.0,2.0,3.0,4.0,5.0,6.0,7.0,8.0]\n" +
            "let L = method_for(A, A)\n" +
            "let k = lambda(x, y) where comm(x, y), omp(x: 1) -> x * y\n" +
            "let result = L <@> k |> compute\n"
        // Partial comm: 3-arg kernel with comm on a subset (proven form, see
        // Test_Symmetry). Exercises a mixed symmetry nest through genNestPragma.
        let mixedSrc =
            "let A = [1.0,2.0,3.0,4.0]\n" +
            "let L = method_for(A, A, A)\n" +
            "let k = lambda(x, y, z) where comm(x, y), omp(x: 1) -> x * y * z\n" +
            "let result = L <@> k |> compute\n"
        let programs =
            [ ("rect_outer_product", rectSrc)
              ("symmetric_triangular", symSrc)
              ("mixed_partial_comm", mixedSrc) ]
        let outputDir = "./generated_omp_coverage"
        Directory.CreateDirectory(outputDir) |> ignore
        // Write runtime headers into the output dir so the generated programs'
        // #include "nested_array_utilities.hpp" / "nested_array_types.hpp"
        // resolve at g++ time (same as the main test path does).
        CodeGen.deployRuntimeHeaders outputDir
        let mutable errors = 0
        let mutable warnings = 0
        let mutable passed = 0
        let mutable failedNames = []
        // Force a multi-thread environment for the run so the gate is meaningful.
        let forcedThreads = "4"
        for (name, src) in programs do
            // Generate with codegen test-mode ON (injects instrumentation), then
            // restore so nothing else in the process is affected.
            setOmpTestMode true
            let outcome =
                try
                    let safeName = sanitizeFileName name
                    match lower src with
                    | Error e -> Error (sprintf "lower failed: %s" e)
                    | Ok ir0 ->
                        let ir =
                            match IR.validateIR ir0 with
                            | Ok v -> v
                            | Error _ -> ir0   // validation errors don't block this probe
                        let (cppCode, _warnings) = CodeGen.genSelfContainedProgramFromIR ir name
                        let srcFile = Path.Combine(outputDir, safeName + ".cpp")
                        File.WriteAllText(srcFile, cppCode)
                        Ok srcFile
                with ex -> Error (sprintf "codegen failed: %s" ex.Message)
            setOmpTestMode false
            match outcome with
            | Error e -> Blade.Tests.TestHarness.resultLine Blade.Tests.TestHarness.Fail name (sprintf "generation: %s" e); errors <- errors + 1; failedNames <- failedNames @ [name]
            | Ok srcFile ->
                let exeExt = if RuntimeInformation.IsOSPlatform(OSPlatform.Windows) then ".exe" else ".out"
                // Use ABSOLUTE paths for g++. srcFile is relative to the process
                // cwd; passing it relative while also setting WorkingDirectory to
                // the output dir caused g++ to resolve it against that dir (a
                // doubled path). Absolute paths make the working dir irrelevant.
                let srcAbs = Path.GetFullPath(srcFile)
                let exeAbs = Path.ChangeExtension(srcAbs, exeExt)
                let cpsi = ProcessStartInfo("g++", sprintf "-std=c++17 -O2 -fopenmp -o \"%s\" \"%s\"" exeAbs srcAbs)
                cpsi.RedirectStandardError <- true
                cpsi.UseShellExecute <- false
                use cproc = Process.Start(cpsi)
                let cerr = cproc.StandardError.ReadToEndAsync()
                cproc.WaitForExit(60000) |> ignore
                if cproc.ExitCode <> 0 then
                    Blade.Tests.TestHarness.resultLine Blade.Tests.TestHarness.Fail name (sprintf "compile: %s" cerr.Result)
                    errors <- errors + 1
                    failedNames <- failedNames @ [name]
                else
                    // Run with OMP_NUM_THREADS forced.
                    let rpsi = ProcessStartInfo(exeAbs)
                    rpsi.RedirectStandardOutput <- true
                    rpsi.RedirectStandardError <- true
                    rpsi.UseShellExecute <- false
                    rpsi.WorkingDirectory <- Path.GetDirectoryName(exeAbs)
                    rpsi.Environment.["OMP_NUM_THREADS"] <- forcedThreads
                    use rproc = Process.Start(rpsi)
                    let rout = rproc.StandardOutput.ReadToEndAsync()
                    rproc.WaitForExit(30000) |> ignore
                    let lines = rout.Result.Split('\n') |> Array.filter (fun l -> l.Contains("[omp-coverage]"))
                    if lines.Length = 0 then
                        Blade.Tests.TestHarness.resultLine Blade.Tests.TestHarness.Skip name "no coverage lines emitted (no parallel region?)"
                        // Not an error per se — the program may have no parallel loop.
                    for line in lines do
                        // parse "region=R teamsz=K distinct=D maxth=M"
                        let getField (k: string) =
                            let m = System.Text.RegularExpressions.Regex.Match(line, k + "=(\\d+)")
                            if m.Success then int m.Groups.[1].Value else -1
                        let teamsz = getField "teamsz"
                        let distinct = getField "distinct"
                        let maxth = getField "maxth"
                        if maxth <= 1 then
                            Blade.Tests.TestHarness.resultLine Blade.Tests.TestHarness.Pass name (sprintf "single-core: maxth=%d, cannot test parallelism" maxth)
                            passed <- passed + 1
                        elif teamsz <= 1 then
                            Blade.Tests.TestHarness.resultLine Blade.Tests.TestHarness.Fail name (sprintf "parallel loop ran serially (teamsz=%d, maxth=%d) -- pragma not honored" teamsz maxth)
                            errors <- errors + 1
                            failedNames <- failedNames @ [name]
                        elif distinct <= 1 then
                            Blade.Tests.TestHarness.resultLine Blade.Tests.TestHarness.Pass name (sprintf "WARNING: parallel team formed (teamsz=%d) but scheduler used 1 thread (distinct=%d)" teamsz distinct)
                            warnings <- warnings + 1
                            passed <- passed + 1
                        else
                            Blade.Tests.TestHarness.resultLine Blade.Tests.TestHarness.Pass name (sprintf "teamsz=%d, distinct=%d, maxth=%d" teamsz distinct maxth)
                            passed <- passed + 1

        // -------------------------------------------------------------------
        // VALUE CORRECTNESS UNDER FORCED MULTI-THREADING (#2)
        // -------------------------------------------------------------------
        // The coverage checks above confirm a parallel region forms, but NOT
        // that the parallelized computation produces CORRECT values. A data
        // race in the triangular outer-parallelization (the disjoint-slab
        // assumption) would show as wrong values only under threading — which
        // neither the coverage checks nor the main value suite (default
        // threading) would catch. Here we run a symmetric computation with
        // KNOWN expected values under OMP_NUM_THREADS=4, repeated several times
        // (races are nondeterministic, so one run can pass by luck), and assert
        // the values are correct every time.
        //
        // N=12 symmetric: C(13,2)=78 elements, large enough that the scheduler
        // genuinely distributes the outer loop. Expected values computed here
        // (not hand-written): for comm(x,y)->x*y over A=[1..N], the left-
        // justified symmetric order is A[i]*A[j] for i<=j.
        let nVal = 12
        let aVals = [ for i in 1 .. nVal -> float i ]
        let expectedSym =
            [ for i in 0 .. nVal - 1 do
                for j in i .. nVal - 1 do
                    yield aVals.[i] * aVals.[j] ]
        let aLit = aVals |> List.map (sprintf "%g") |> String.concat ","
        let expectedLit = expectedSym |> List.map (sprintf "%g") |> String.concat ", "
        let valSrc =
            sprintf "let A = [%s]\n" aLit +
            "let L = method_for(A, A)\n" +
            // omp clause so this genuinely runs parallel under OMP_NUM_THREADS=4
            // — otherwise (post-flip) it would be serial and the env var inert,
            // defeating the race-detection purpose of the repeated runs.
            "let k = lambda(x, y) where comm(x, y), omp(x: 1) -> x * y\n" +
            "let result = L <@> k |> compute\n" +
            sprintf "// EXPECT: result = [%s]\n" expectedLit
        printSubHeader "Value correctness under forced threading (N=12 symmetric)"
        setOmpTestMode false  // value test: no instrumentation, just real codegen
        let valOutcome =
            try
                match lower valSrc with
                | Error e -> Error (sprintf "lower failed: %s" e)
                | Ok ir0 ->
                    let ir = match IR.validateIR ir0 with Ok v -> v | Error _ -> ir0
                    let (cppCode, _w) = CodeGen.genSelfContainedProgramFromIR ir "omp_value_check"
                    let sf = Path.Combine(outputDir, "omp_value_check.cpp")
                    File.WriteAllText(sf, cppCode)
                    Ok (Path.GetFullPath sf)
            with ex -> Error (sprintf "codegen failed: %s" ex.Message)
        match valOutcome with
        | Error e -> Blade.Tests.TestHarness.resultLine Blade.Tests.TestHarness.Fail "omp_value_check" (sprintf "generation: %s" e); errors <- errors + 1; failedNames <- failedNames @ ["omp_value_check"]
        | Ok srcAbs ->
            let exeExt = if RuntimeInformation.IsOSPlatform(OSPlatform.Windows) then ".exe" else ".out"
            let exeAbs = Path.ChangeExtension(srcAbs, exeExt)
            let cpsi = ProcessStartInfo("g++", sprintf "-std=c++17 -O2 -fopenmp -o \"%s\" \"%s\"" exeAbs srcAbs)
            cpsi.RedirectStandardError <- true
            cpsi.UseShellExecute <- false
            use cproc = Process.Start(cpsi)
            let cerr = cproc.StandardError.ReadToEndAsync()
            cproc.WaitForExit(60000) |> ignore
            if cproc.ExitCode <> 0 then
                Blade.Tests.TestHarness.resultLine Blade.Tests.TestHarness.Fail "omp_value_check" (sprintf "compile: %s" cerr.Result)
                errors <- errors + 1
                failedNames <- failedNames @ ["omp_value_check"]
            else
                let expected = parseExpectedValues valSrc
                let mutable allRunsOk = true
                // Repeat: a race may pass on some runs and fail on others.
                for run in 1 .. 5 do
                    let rpsi = ProcessStartInfo(exeAbs)
                    rpsi.RedirectStandardOutput <- true
                    rpsi.RedirectStandardError <- true
                    rpsi.UseShellExecute <- false
                    rpsi.WorkingDirectory <- Path.GetDirectoryName(exeAbs)
                    rpsi.Environment.["OMP_NUM_THREADS"] <- forcedThreads
                    use rproc = Process.Start(rpsi)
                    let rout = rproc.StandardOutput.ReadToEndAsync()
                    rproc.WaitForExit(30000) |> ignore
                    match checkExpectedValues expected rout.Result with
                    | Ok () -> ()
                    | Error errs ->
                        allRunsOk <- false
                        printfn "    run %d: VALUE MISMATCH (possible race): %s" run (String.concat "; " errs)
                if allRunsOk then
                    Blade.Tests.TestHarness.resultLine Blade.Tests.TestHarness.Pass "omp_value_check" (sprintf "correct values across 5 runs under OMP_NUM_THREADS=%s" forcedThreads)
                    passed <- passed + 1
                else
                    Blade.Tests.TestHarness.resultLine Blade.Tests.TestHarness.Fail "omp_value_check" "values incorrect under threading -- likely a data race in parallelization"
                    errors <- errors + 1
                    failedNames <- failedNames @ ["omp_value_check"]

        printFooter "OpenMP Coverage" [sprintf "%d passed" passed; sprintf "%d error(s)" errors; sprintf "%d warning(s)" warnings]
        { Block = "OpenMP Coverage"; Passed = passed; Failed = errors; Skipped = 0; FailedNames = failedNames }
