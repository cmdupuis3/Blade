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
      // Depth 2 on the same nest threads BOTH levels. Since Phase 3
      // (docs/plan-cpp-perf-exploitation.md) this nest is index-free
      // elementwise over one contiguous pool, so it is emitted as a single
      // flat loop over all cells and the pragma is `parallel for simd` rather
      // than `collapse(2)` over the two headers. Same licence, same threaded
      // dimensions — collapse(2) fuses exactly the iteration space the flat
      // loop already IS, so the fused form subsumes it (and adds SIMD, which
      // the flat write pattern licenses). The pair this case exists to
      // separate is unaffected: depth 1 above still threads one level only.
      ("one_arg_two_levels_depth_2",
       "function k(a: Float64) where omp(a: 2) = a * 2.0\n" +
       "let M = [[1.0, 2.0, 3.0], [4.0, 5.0, 6.0]]\n" +
       "let m = object_for(k) <@> (M) |> compute\n",
       "#pragma omp parallel for simd") ]

/// Comm-licensed parallel REDUCTIONS (Phase 2 of
/// docs/plan-cpp-perf-exploitation.md). `where ... omp` on a FOLD kernel opts
/// the reduction into a parallel fold; which SHAPE it gets is decided by the
/// licence, and the two shapes are textually unmistakable:
///
///   Path A — builtin `+`/`*` body: `#pragma omp parallel for simd
///     reduction(<op>:acc)` over the flat sweep, no runtime API at all.
///   Path B — any other licensed kernel (and every reduce over a deferred
///     computation): an explicit team with per-thread partials, which is the
///     only shape that calls `omp_get_max_threads` / `omp_get_thread_num`.
///
/// Asserting `mustNotContain` matters as much as `mustContain` here: the two
/// paths differ in reassociation guarantees (Path A's combine order is
/// unspecified; Path B's is fixed by chunk index), so a kernel silently
/// switching paths changes a property tests downstream rely on.
let private ompReduceCases : (string * string * string list * string list) list =
    // (name, source, mustContain, mustNotContain)
    let arr = "let A = [1.0, 2.0, 3.0, 4.0, 5.0, 6.0]\n"
    [ // Path A: builtin body, no comm needed — `+` carries commutativity and
      // associativity outright.
      ("reduce_builtin_lambda_omp",
       arr + "let s = reduce(A, lambda(a, b) where omp -> a + b)\n",
       ["#pragma omp parallel for simd reduction(+:s)"],
       ["omp_get_max_threads"])
      ("reduce_builtin_product_omp",
       arr + "let s = reduce(A, lambda(a, b) where omp -> a * b)\n",
       ["#pragma omp parallel for simd reduction(*:s)"], [])
      // Negative control: parallelism is OPT-IN. The identical program minus
      // the clause must emit the old serial accumulator loop.
      ("reduce_builtin_lambda_no_clause",
       arr + "let s = reduce(A, lambda(a, b) -> a + b)\n",
       ["// reduce: accumulator loop, eager"], ["#pragma omp"])
      // Negative control: an operator section cannot carry a where-clause, so
      // the most common fold spelling of all stays serial.
      ("reduce_section_no_clause",
       arr + "let s = reduce(A, (+))\n",
       ["// reduce: accumulator loop, eager"], ["#pragma omp"])
      // Path B via a NAMED function — the eta-prone spelling, and the one whose
      // body is invisible at the reduce seam (TypeEnv.FuncFoldBuiltin exists for
      // exactly this). Body is deliberately not a bare builtin op.
      ("reduce_named_comm_function",
       "function myAdd(a: Float64, b: Float64) where comm(a, b), omp = (a + b) * 1.0\n" + arr +
       "let s = reduce(A, myAdd)\n",
       ["#pragma omp parallel num_threads("; "omp_get_thread_num()"],
       ["#pragma omp parallel for"])
      // Path B via an inline comm lambda.
      ("reduce_inline_comm_lambda",
       arr + "let s = reduce(A, lambda(a, b) where comm(a, b), omp -> (a + b) * 1.0)\n",
       ["#pragma omp parallel num_threads("], ["#pragma omp parallel for"])
      // Path B with an explicit init: the seed stays in the shared accumulator
      // and enters the fixed-order combine first, so nothing about the chunk
      // shape changes.
      ("reduce_named_comm_with_init",
       "function myAdd(a: Float64, b: Float64) where comm(a, b), omp = (a + b) * 1.0\n" + arr +
       "let s = reduce(A, myAdd, 100.0)\n",
       ["#pragma omp parallel num_threads("], [])
      // Path B over a DEFERRED computation: no intermediate array is
      // materialized, and the OUTERMOST level is the one chunked.
      ("reduce_over_computation_chunked",
       "function myAdd(a: Float64, b: Float64) where comm(a, b), omp = (a + b) * 1.0\n" + arr +
       "let B = [2.0, 3.0, 4.0, 5.0, 6.0, 7.0]\n" +
       "let s = reduce(method_for(A, B) <@> lambda(x, y) -> x * y, myAdd, 0.0)\n",
       ["comm-licensed parallel fold, outer level chunked"
        "#pragma omp parallel num_threads("
        "for (size_t __i0 = __rlo; __i0 < __rhi; __i0++)"], [])
      // Same computation, clause dropped: back to the materialize-then-fold
      // desugar, and no parallel region anywhere.
      ("reduce_over_computation_no_clause",
       "function myAdd(a: Float64, b: Float64) where comm(a, b) = (a + b) * 1.0\n" + arr +
       "let B = [2.0, 3.0, 4.0, 5.0, 6.0, 7.0]\n" +
       "let s = reduce(method_for(A, B) <@> lambda(x, y) -> x * y, myAdd, 0.0)\n",
       [], ["#pragma omp"]) ]

/// The BL-coded refusal for an UNLICENSED `omp` on a fold kernel. Pinned in the
/// diagnostics corpus too (049_fold_omp_needs_license.blade); repeated here so
/// the codegen-side block that owns the two emission paths also owns the
/// statement that there is no third, silent one.
let private ompReduceDiagCases : (string * string) list =
    // (name, source) — each must fail to lower, with BL4016 in the message.
    let arr = "let A = [1.0, 2.0, 3.0, 4.0, 5.0, 6.0]\n"
    [ ("omp_without_comm_antisym_body",
       arr + "let s = reduce(A, lambda(a, b) where omp -> a - b)\n")
      ("omp_without_comm_nonbuiltin_body",
       arr + "let s = reduce(A, lambda(a, b) where omp -> (a + b) * 2.0)\n")
      ("omp_named_function_without_comm",
       "function halfSum(a: Float64, b: Float64) where omp = (a + b) * 0.5\n" + arr +
       "let s = reduce(A, halfSum)\n") ]

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
    // ---- comm-licensed parallel REDUCTIONS: which emission path fired ----
    for (name, src, mustContain, mustNotContain) in ompReduceCases do
        match cppOf name src with
        | Error e -> fail name e
        | Ok cpp ->
            let missing = mustContain |> List.filter (fun s -> not (cpp.Contains s))
            let present = mustNotContain |> List.filter cpp.Contains
            // A licensed fold calls the OpenMP runtime API, which needs the
            // header — `#pragma omp` alone does not. Asserted as "included, not
            // commented out", the shape genIncludes can regress to.
            let needsHeader = mustContain |> List.exists (fun s -> s.Contains "omp_get_")
            let headerOk =
                not needsHeader
                || (cpp.Split('\n')
                    |> Array.exists (fun l -> l.Trim().StartsWith "#include <omp.h>"))
            if not missing.IsEmpty then
                fail name (sprintf "generated C++ lacks: %s" (String.concat " | " missing))
            elif not present.IsEmpty then
                fail name (sprintf "generated C++ unexpectedly contains: %s" (String.concat " | " present))
            elif not headerOk then
                fail name "calls the omp_* runtime API but <omp.h> is not included"
            else
                passed <- passed + 1
                resultLine Pass name "emission path as expected"
    // ---- the unlicensed case is a hard error, not a silent serial fold ----
    for (name, src) in ompReduceDiagCases do
        // lowerDiag, not lower: the CODE is the assertion, and the plain
        // string channel renders the message without it.
        match fst (Lowering.lowerDiag None src) with
        | Ok _ -> fail name "compiled cleanly; expected BL4016"
        | Error diags ->
            let codes = diags |> List.map (fun (d: Blade.Diagnostics.Diagnostic) -> d.Code)
            if List.contains "BL4016" codes then
                passed <- passed + 1
                resultLine Pass name "BL4016"
            else fail name (sprintf "expected BL4016, got: %s" (String.concat ", " codes))
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
        printfn "Skipped: g++ not found (cannot compile the -fopenmp coverage programs)."
        // Skipped = 1, not 0: a Skipped = 0 return made a toolchain-less box
        // print "0 passed, 0 failed" for this block with no skip note. Same
        // convention as DiffOracle/InterpDiff.
        { Block = "OpenMP Coverage"; Passed = 0; Failed = 0; Skipped = 1; FailedNames = [] }
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
        // Phase 2 Path B: a comm-licensed fold over a DEFERRED computation. Its
        // parallel region is an explicit team with per-thread partials, not a
        // `parallel for`, so pragma text alone cannot say whether a team really
        // forms — this is the ground-truth check that it does.
        let foldSrc =
            "function myAdd(a: Float64, b: Float64) where comm(a, b), omp = (a + b) * 1.0\n" +
            "let A = [1.0,2.0,3.0,4.0,5.0,6.0,7.0,8.0]\n" +
            "let B = [1.0,2.0,3.0,4.0,5.0,6.0,7.0,8.0]\n" +
            "let s = reduce(method_for(A, B) <@> lambda(x, y) -> x * y, myAdd, 0.0)\n"
        let programs =
            [ ("rect_outer_product", rectSrc)
              ("symmetric_triangular", symSrc)
              ("mixed_partial_comm", mixedSrc)
              ("comm_licensed_fold", foldSrc) ]
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
                        // A validation error is a hard failure. The old
                        // `| Error _ -> ir0` ("validation errors don't block this
                        // probe") generated C++ from invalid IR, so a validator
                        // regression on these programs was invisible here.
                        match IR.validateIR ir0 with
                        | Error validationErrors ->
                            Error (sprintf "IR validation failed: %s" (String.concat "; " validationErrors))
                        | Ok ir ->
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
                let cpsi = ProcessStartInfo("g++", sprintf "-std=c++17 %s -fopenmp -o \"%s\" \"%s\"" optFlags exeAbs srcAbs)
                cpsi.RedirectStandardError <- true
                cpsi.UseShellExecute <- false
                use cproc = Process.Start(cpsi)
                let cerr = cproc.StandardError.ReadToEndAsync()
                // WaitForExit(ms) returns false on TIMEOUT; ExitCode is
                // meaningless then, so a hung g++ used to be read as success.
                let cExited = cproc.WaitForExit(60000)
                if not cExited then
                    (try cproc.Kill(true) with _ -> ())
                    Blade.Tests.TestHarness.resultLine Blade.Tests.TestHarness.Fail name "compile timed out (60s)"
                    errors <- errors + 1
                    failedNames <- failedNames @ [name]
                elif cproc.ExitCode <> 0 then
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
                    let rerr = rproc.StandardError.ReadToEndAsync()
                    let rExited = rproc.WaitForExit(30000)
                    if not rExited then (try rproc.Kill(true) with _ -> ())
                    let lines = rout.Result.Split('\n') |> Array.filter (fun l -> l.Contains("[omp-coverage]"))
                    if not rExited then
                        Blade.Tests.TestHarness.resultLine Blade.Tests.TestHarness.Fail name "run timed out (30s)"
                        errors <- errors + 1
                        failedNames <- failedNames @ [name]
                    elif lines.Length = 0 then
                        // Every program in `programs` carries an explicit
                        // `omp(x: 1)` clause, and CodeGen emits the
                        // [omp-coverage] instrumentation exactly when
                        // ompInstrument && outerIsParallel. So zero coverage
                        // lines means the pragma was NOT emitted for an
                        // annotated kernel -- the very condition this block
                        // exists to detect. It used to print a Skip line and
                        // increment nothing at all: not passed, not failed, not
                        // skipped, so the block silently shrank to nothing.
                        Blade.Tests.TestHarness.resultLine Blade.Tests.TestHarness.Fail name
                            (sprintf "no [omp-coverage] line emitted for an omp()-annotated kernel (exit %d)%s"
                                rproc.ExitCode
                                (if String.IsNullOrWhiteSpace rerr.Result then "" else " stderr: " + rerr.Result.Trim()))
                        errors <- errors + 1
                        failedNames <- failedNames @ [name]
                    for line in lines do
                        // parse "region=R teamsz=K distinct=D maxth=M"
                        // None (not -1) on a regex miss: a sentinel -1 flowed
                        // into `maxth <= 1`, which is the UNCONDITIONAL-PASS
                        // arm below, so an unparseable coverage line scored a
                        // "single-core, cannot test parallelism" pass.
                        let getField (k: string) =
                            let m = System.Text.RegularExpressions.Regex.Match(line, k + "=(\\d+)")
                            if m.Success then Some (int m.Groups.[1].Value) else None
                        let teamszO = getField "teamsz"
                        let distinctO = getField "distinct"
                        let maxthO = getField "maxth"
                        let missing =
                            [ if teamszO.IsNone then "teamsz"
                              if distinctO.IsNone then "distinct"
                              if maxthO.IsNone then "maxth" ]
                        let teamsz = defaultArg teamszO 0
                        let distinct = defaultArg distinctO 0
                        let maxth = defaultArg maxthO 0
                        if not missing.IsEmpty then
                            Blade.Tests.TestHarness.resultLine Blade.Tests.TestHarness.Fail name
                                (sprintf "unparseable coverage line (missing %s): %s"
                                    (String.concat ", " missing) (line.Trim()))
                            errors <- errors + 1
                            failedNames <- failedNames @ [name]
                        elif maxth <= 1 then
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
                    // Hard-fail on validation errors (was `| Error _ -> ir0`).
                    match IR.validateIR ir0 with
                    | Error validationErrors ->
                        Error (sprintf "IR validation failed: %s" (String.concat "; " validationErrors))
                    | Ok ir ->
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
            let cpsi = ProcessStartInfo("g++", sprintf "-std=c++17 %s -fopenmp -o \"%s\" \"%s\"" optFlags exeAbs srcAbs)
            cpsi.RedirectStandardError <- true
            cpsi.UseShellExecute <- false
            use cproc = Process.Start(cpsi)
            let cerr = cproc.StandardError.ReadToEndAsync()
            let cExited = cproc.WaitForExit(60000)
            if not cExited then
                (try cproc.Kill(true) with _ -> ())
                Blade.Tests.TestHarness.resultLine Blade.Tests.TestHarness.Fail "omp_value_check" "compile timed out (60s)"
                errors <- errors + 1
                failedNames <- failedNames @ ["omp_value_check"]
            elif cproc.ExitCode <> 0 then
                Blade.Tests.TestHarness.resultLine Blade.Tests.TestHarness.Fail "omp_value_check" (sprintf "compile: %s" cerr.Result)
                errors <- errors + 1
                failedNames <- failedNames @ ["omp_value_check"]
            else
                // Malformed-pin gate, same rule as Runner.fs: a `// EXPECT:`
                // line that does not parse is a DROPPED assertion, and
                // checkExpectedValues over an empty pin list returns Ok (),
                // so without this the whole race check could pass vacuously.
                let malformed = parseMalformedExpectLines valSrc
                let expected = parseExpectedValues valSrc
                let mutable allRunsOk = true
                if not malformed.IsEmpty then
                    allRunsOk <- false
                    printfn "    unparseable EXPECT pin(s): %s" (String.concat " | " malformed)
                elif expected.IsEmpty then
                    allRunsOk <- false
                    printfn "    no EXPECT pin parsed -- the value check would be vacuous"
                else
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
                        let rerrV = rproc.StandardError.ReadToEndAsync()
                        let rExited = rproc.WaitForExit(30000)
                        if not rExited then
                            (try rproc.Kill(true) with _ -> ())
                            rproc.WaitForExit(5000) |> ignore
                            allRunsOk <- false
                            printfn "    run %d: TIMED OUT (30s)" run
                        elif rproc.ExitCode <> 0 then
                            // A crashed run prints little or nothing; with no
                            // exit-code gate the value check over the truncated
                            // stdout was the only verdict.
                            allRunsOk <- false
                            printfn "    run %d: exit %d %s" run rproc.ExitCode (rerrV.Result.Trim())
                        else
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

// ============================================================================
// Comm-licensed parallel reductions: VALUES under real threads
// ============================================================================
//
// The pragma block above answers "which emission path fired". This one answers
// the question no string check can: does the parallel fold compute what the
// serial fold computes, when a real OpenMP runtime really splits the axis?
//
// Everything here is a DIFFERENTIAL against the same program with the `omp`
// clause removed — not against hand-written expected values — so the oracle
// cannot drift from the feature. Two properties are asserted separately:
//
//   * VALUE, within 1e-9 of serial. Float tolerance lives HERE and nowhere
//     else: the corpus files for this feature (loops/110, loops/111) are also
//     run by the interpreter differential, which demands byte-identical output,
//     so they are restricted to integer-valued data where every association is
//     exact. This block is not part of that gate and may use awkward values,
//     which is the point — it is where reassociation is actually exercised.
//
//   * RUN-TO-RUN IDENTITY at a fixed OMP_NUM_THREADS, for Path B ONLY. Path B's
//     chunk boundaries and combine order are fixed functions of the team size,
//     so repeated runs reproduce bit-for-bit; Path A hands the combine to the
//     OpenMP runtime, whose order is unspecified by the standard, so asserting
//     it there would be pinning an implementation detail (a legal runtime could
//     fail it).
//
// Also here, and deliberately not a reduce: a COMPILED-AND-RUN case of
// `where omp(a: 1, b: 1)` on a 2-level dense map. `collapse(2)` fuses the two
// headers into one iteration space, and g++ rejects `#pragma GCC ivdep` on a
// header the construct owns ("loop not permitted in intervening code in OpenMP
// loop body" / "not enough nested loops"). The omp-pragma block asserts the
// pragma TEXT and never compiles it; the omp-coverage block compiles but with
// test-mode instrumentation, which disables ivdep by its own gate. Neither can
// see the interaction — so it gets a plain compile+run here.

/// A single compiled program: source -> exe path, ready to run. `testMode` is
/// forced OFF (the coverage instrumentation suppresses Phase 1's ivdep, which
/// is exactly what the collapse case below needs to exercise).
let private compileProgram (outputDir: string) (name: string) (src: string) : Result<string, string> =
    try
        setOmpTestMode false
        match lower src with
        | Error e -> Error (sprintf "lower failed: %s" e)
        | Ok ir0 ->
            match IR.validateIR ir0 with
            | Error errs -> Error (sprintf "IR validation failed: %s" (String.concat "; " errs))
            | Ok ir ->
                let (cppCode, _w) = CodeGen.genSelfContainedProgramFromIR ir name
                let srcAbs = Path.GetFullPath(Path.Combine(outputDir, sanitizeFileName name + ".cpp"))
                File.WriteAllText(srcAbs, cppCode)
                let exeExt = if RuntimeInformation.IsOSPlatform(OSPlatform.Windows) then ".exe" else ".out"
                let exeAbs = Path.ChangeExtension(srcAbs, exeExt)
                let cpsi = ProcessStartInfo("g++", sprintf "-std=c++17 %s -fopenmp -o \"%s\" \"%s\"" optFlags exeAbs srcAbs)
                cpsi.RedirectStandardError <- true
                cpsi.UseShellExecute <- false
                use cproc = Process.Start(cpsi)
                let cerr = cproc.StandardError.ReadToEndAsync()
                if not (cproc.WaitForExit(120000)) then
                    (try cproc.Kill(true) with _ -> ())
                    Error "compile timed out (120s)"
                elif cproc.ExitCode <> 0 then Error (sprintf "compile: %s" (cerr.Result.Trim()))
                else Ok exeAbs
    with ex -> Error (sprintf "codegen raised: %s" ex.Message)

/// Run a compiled program with OMP_NUM_THREADS forced, returning stdout.
let private runProgram (exeAbs: string) (threads: string) : Result<string, string> =
    let rpsi = ProcessStartInfo(exeAbs)
    rpsi.RedirectStandardOutput <- true
    rpsi.RedirectStandardError <- true
    rpsi.UseShellExecute <- false
    rpsi.WorkingDirectory <- Path.GetDirectoryName(exeAbs)
    rpsi.Environment.["OMP_NUM_THREADS"] <- threads
    use rproc = Process.Start(rpsi)
    let rout = rproc.StandardOutput.ReadToEndAsync()
    let rerr = rproc.StandardError.ReadToEndAsync()
    if not (rproc.WaitForExit(60000)) then
        (try rproc.Kill(true) with _ -> ())
        Error "run timed out (60s)"
    elif rproc.ExitCode <> 0 then
        Error (sprintf "exit %d: %s" rproc.ExitCode (rerr.Result.Trim()))
    else Ok rout.Result

/// Every `name = value` scalar line of a program's stdout, as floats. The
/// auto-printer emits one per top-level binding, which is what the differential
/// compares.
let private scalarBindings (stdout: string) : Map<string, float> =
    stdout.Split('\n')
    |> Array.choose (fun line ->
        let m = System.Text.RegularExpressions.Regex.Match(
                    line.Trim(), @"^([A-Za-z_][A-Za-z0-9_]*) = (-?[0-9.eE+-]+)$")
        if m.Success then
            match System.Double.TryParse(m.Groups.[2].Value,
                                         System.Globalization.NumberStyles.Float,
                                         System.Globalization.CultureInfo.InvariantCulture) with
            | true, v -> Some (m.Groups.[1].Value, v)
            | _ -> None
        else None)
    |> Map.ofArray

/// Comm-licensed parallel reduction VALUE tests. Requires g++; skips otherwise.
let runOmpReduceTests () : Blade.Tests.TestHarness.BlockResult =
    let caps = capabilities.Value
    printHeader "OpenMP Comm-Licensed Reductions"
    if not caps.HasGpp then
        printfn "Skipped: g++ not found (cannot compile the -fopenmp reduction programs)."
        { Block = "OpenMP Reduce"; Passed = 0; Failed = 0; Skipped = 1; FailedNames = [] }
    else
        let outputDir = "./generated_omp_reduce"
        Directory.CreateDirectory(outputDir) |> ignore
        CodeGen.deployRuntimeHeaders outputDir
        let forcedThreads = "4"
        let mutable passed = 0
        let mutable failed = 0
        let mutable failedNames = []
        let fail name detail =
            failed <- failed + 1
            failedNames <- failedNames @ [name]
            Blade.Tests.TestHarness.resultLine Blade.Tests.TestHarness.Fail name detail
        let pass name detail =
            passed <- passed + 1
            Blade.Tests.TestHarness.resultLine Blade.Tests.TestHarness.Pass name detail

        // Awkward, NON-integer data: reassociation is only observable when the
        // partial sums are not exact. 240 elements so a 4-thread split gives
        // four genuinely different chunks.
        let n = 240
        let vals = [ for i in 1 .. n -> 1.0 / float i + float (i % 7) * 0.3 ]
        let aLit = vals |> List.map (fun v -> v.ToString("R", System.Globalization.CultureInfo.InvariantCulture)) |> String.concat ", "
        let arrDecl = sprintf "let A = [%s]\n" aLit
        // A second array for the deferred-computation (2-level nest) case; kept
        // short so the fused nest stays a quick compile.
        let bVals = [ for i in 1 .. 9 -> 0.5 + 1.0 / float (i + 2) ]
        let bLit = bVals |> List.map (fun v -> v.ToString("R", System.Globalization.CultureInfo.InvariantCulture)) |> String.concat ", "
        let bDecl = sprintf "let B = [%s]\n" bLit
        let commFn = "function myAdd(a: Float64, b: Float64) where comm(a, b), omp = (a + b) * 1.0\n"
        let serialFn = "function myAdd(a: Float64, b: Float64) where comm(a, b) = (a + b) * 1.0\n"
        // Compound (masked) operand: reduce walks the flat `.data` buffer of
        // present cells, which is the OTHER contiguous 1-D sweep Path A covers.
        let maskDecl = "let m = mask(A, lambda(x) -> x > 1.0)\nlet C = compound(A, m)\n"

        // (name, ompSource, serialSource, pathB?)
        let cases : (string * string * string * bool) list =
            [ ("path_a_builtin_sum_dense",
               arrDecl + "let s = reduce(A, lambda(a, b) where omp -> a + b)\n",
               arrDecl + "let s = reduce(A, lambda(a, b) -> a + b)\n",
               false)
              ("path_a_builtin_sum_compound",
               arrDecl + maskDecl + "let s = reduce(C, lambda(a, b) where omp -> a + b)\n",
               arrDecl + maskDecl + "let s = reduce(C, lambda(a, b) -> a + b)\n",
               false)
              ("path_b_named_comm_function",
               commFn + arrDecl + "let s = reduce(A, myAdd)\n",
               serialFn + arrDecl + "let s = reduce(A, myAdd)\n",
               true)
              ("path_b_named_comm_with_init",
               commFn + arrDecl + "let s = reduce(A, myAdd, 100.0)\n",
               serialFn + arrDecl + "let s = reduce(A, myAdd, 100.0)\n",
               true)
              ("path_b_inline_comm_lambda",
               arrDecl + "let s = reduce(A, lambda(a, b) where comm(a, b), omp -> (a + b) * 1.0)\n",
               arrDecl + "let s = reduce(A, lambda(a, b) where comm(a, b) -> (a + b) * 1.0)\n",
               true)
              ("path_b_reduce_over_computation",
               commFn + bDecl + "let s = reduce(method_for(B, B) <@> lambda(x, y) -> x * y, myAdd, 0.0)\n",
               serialFn + bDecl + "let s = reduce(method_for(B, B) <@> lambda(x, y) -> x * y, myAdd, 0.0)\n",
               true) ]

        for (name, ompSrc, serialSrc, isPathB) in cases do
            match compileProgram outputDir (name + "_omp") ompSrc,
                  compileProgram outputDir (name + "_serial") serialSrc with
            | Error e, _ -> fail name (sprintf "omp build: %s" e)
            | _, Error e -> fail name (sprintf "serial build: %s" e)
            | Ok ompExe, Ok serialExe ->
                match runProgram ompExe forcedThreads, runProgram serialExe "1" with
                | Error e, _ -> fail name (sprintf "omp run: %s" e)
                | _, Error e -> fail name (sprintf "serial run: %s" e)
                | Ok ompOut, Ok serialOut ->
                    let ompVals = scalarBindings ompOut
                    let serVals = scalarBindings serialOut
                    // `s` is the fold's binding in every case above. Its absence
                    // would make the comparison vacuous, so it is checked.
                    match Map.tryFind "s" ompVals, Map.tryFind "s" serVals with
                    | Some pv, Some sv ->
                        let diff = abs (pv - sv)
                        let tol = 1e-9 * max 1.0 (abs sv)
                        if diff > tol then
                            fail name (sprintf "parallel %.17g vs serial %.17g (|diff| = %g)" pv sv diff)
                        elif not isPathB then
                            pass name (sprintf "value matches serial (|diff| = %g)" diff)
                        else
                            // Path B determinism: a second run at the same team
                            // size must reproduce the first byte for byte.
                            match runProgram ompExe forcedThreads with
                            | Error e -> fail name (sprintf "second omp run: %s" e)
                            | Ok again ->
                                let strip (s: string) =
                                    s.Split('\n')
                                    |> Array.filter (fun l -> not (l.Contains "completed in"))
                                    |> String.concat "\n"
                                if strip again <> strip ompOut then
                                    fail name "run-to-run output differs at fixed OMP_NUM_THREADS=4 (chunking is not deterministic)"
                                else
                                    pass name (sprintf "value matches serial (|diff| = %g); identical across 2 runs" diff)
                    | _ ->
                        fail name "no scalar binding 's' in program output (comparison would be vacuous)"

        // ---- Phase 1 regression: ivdep must not land inside collapse(2) ----
        // Text-only assertions cannot see this; only g++ can.
        let collapseSrc =
            "function k(a: Float64, b: Float64) where omp(a: 1, b: 1) = a * b + 1.0\n" +
            "let P = [1.5, 2.5, 3.5, 4.5, 5.5]\n" +
            "let Q = [0.25, 0.5, 0.75, 1.25]\n" +
            "let M = object_for(k) <@> (P, Q) |> compute\n"
        let collapseName = "collapse2_dense_map_compiles"
        match compileProgram outputDir collapseName collapseSrc with
        | Error e -> fail collapseName (sprintf "%s" e)
        | Ok exeAbs ->
            match runProgram exeAbs forcedThreads with
            | Error e -> fail collapseName (sprintf "run: %s" e)
            | Ok out ->
                // 5x4 outer product through the kernel, printed flat.
                let expected =
                    [ for p in [1.5; 2.5; 3.5; 4.5; 5.5] do
                        for q in [0.25; 0.5; 0.75; 1.25] -> p * q + 1.0 ]
                let printed =
                    let m = System.Text.RegularExpressions.Regex.Match(out, @"M = \[([^\]]*)\]")
                    if not m.Success then []
                    else
                        m.Groups.[1].Value.Split(',')
                        |> Array.toList
                        |> List.choose (fun s ->
                            match System.Double.TryParse(s.Trim(),
                                                         System.Globalization.NumberStyles.Float,
                                                         System.Globalization.CultureInfo.InvariantCulture) with
                            | true, v -> Some v
                            | _ -> None)
                if printed.Length <> expected.Length then
                    fail collapseName (sprintf "expected %d values, parsed %d from output" expected.Length printed.Length)
                elif List.exists2 (fun (a: float) b -> abs (a - b) > 1e-9) printed expected then
                    fail collapseName "collapse(2) map produced wrong values under 4 threads"
                else
                    pass collapseName "compiles under g++ and computes correctly (ivdep/collapse interaction)"

        printFooter "OpenMP Reduce" [sprintf "%d passed" passed; sprintf "%d failed" failed]
        { Block = "OpenMP Reduce"; Passed = passed; Failed = failed; Skipped = 0; FailedNames = failedNames }
