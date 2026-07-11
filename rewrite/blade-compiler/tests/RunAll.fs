// The combined test corpus (`allTests`) and the full-suite entry points.
// Extracted from Main.fs (audit §2.3). The OpenMP-coverage and CUDA blocks
// are OPT-IN here: CUDA needs nvcc + a GPU + (on Windows) cl.exe on PATH —
// i.e. a run from the "x64 Native Tools Command Prompt for VS" — and the
// OpenMP block forces multi-threaded runs. Everything else always runs.
module Blade.Tests.RunAll

open Blade.Tests.Basic
open Blade.Tests.Loops
open Blade.Tests.Symmetry
open Blade.Tests.Reynolds
open Blade.Tests.Arity
open Blade.Tests.Functions
open Blade.Tests.Structs
open Blade.Tests.SumTypes
open Blade.Tests.Interfaces
open Blade.Tests.Modules
open Blade.Tests.Guards
open Blade.Tests.Bracketed
open Blade.Tests.IndexTypes
open Blade.Tests.Mutability
open Blade.Tests.Static
open Blade.Tests.Units
open Blade.Tests.Sqlish
open Blade.Tests.InferenceProbes
open Blade.Tests.FuncArrays
open Blade.Tests.Normalize
open Blade.Tests.Unify
open Blade.Tests.ValidateArrow
open Blade.Tests.ExprAttrs
open Blade.Tests.CodeGenSubst
open Blade.Tests.Runner
open Blade.Tests.AllocTests
open Blade.Tests.OmpTests
open Blade.Tests.CudaTests
open Blade.Tests.Differential
open Blade.Tests.Benchmarks

// ============================================================================
// Test Collections
// ============================================================================

/// All tests combined
let allTests =
    basicTests @ loopTests @ symmetryTests @ reynoldsTests @ arityTests @ functionTests
    @ structTests @ sumTypeTests @ interfaceTests @ moduleTests @ guardTests @ guardCombinatorTests @ zeroCombinatorTests @ sequenceCombinatorTests @ tupleViewTests @ replicateTests @ anonRangeTests @ forInTests @ bracketedTests
    @ indexTypeTests @ mutabilityTests @ staticTests @ unitTests
    @ foreignKeyTests @ maskTests @ setOpTests @ uniqueContainsTests @ semijoinTests @ groupByTests @ sortTests @ reduceTests @ extentsTests @ extentsMultiRankTests @ regressionTests @ sqlCombinedTests @ v24dProbes
    @ inferenceProbes
    @ funcArrayTests

/// Which optional, toolchain-heavy blocks the full suite should include.
/// All default to OFF: the CUDA block needs the x64 Native Tools prompt on
/// Windows, the OpenMP-coverage block forces multi-threaded runs, and the
/// differential-timing block compiles and repeatedly runs large programs
/// (it dominates the suite's wall time). Enable with
/// `blade test --omp --cuda --timing`, or run the blocks standalone
/// (`blade test omp-coverage`, `blade test cuda`, `blade test timing`).
type FullSuiteOptions = {
    IncludeOmp    : bool
    IncludeCuda   : bool
    IncludeTiming : bool
}

let defaultFullSuiteOptions = { IncludeOmp = false; IncludeCuda = false; IncludeTiming = false }

/// Includes both the single-file test corpus (`allTests`) and the multi-file
/// module/import corpus (`multiFileTests`). External-dependency tests
/// (NetCDF provider tests in particular) are NOT included here — they have
/// their own entry point because they require `libnetcdf` and a sample data
/// file that may not be present in CI / local dev environments.
///
/// `extraBlocks` lets a LATER-compiled module contribute blocks to the grand
/// total: the CLI smoke test lives in Cli.fs (it exercises Cli.compileToExe),
/// which the F# compile order places after this file, so it cannot be
/// referenced here directly. Cli.fs passes it in.
let runAllTestsFullWith (extraBlocks: (unit -> Blade.Tests.TestHarness.BlockResult) list) (opts: FullSuiteOptions) =
    let outputDir = "./generated_cpp_tests"
    let r1 = runTestCategoryFull "All" allTests outputDir
    let r2 = runMultiFileTestsFull "Multi-File Modules" multiFileTests outputDir
    // Phase B: F# unit tests for the exprAttrs computation. Runs after
    // the source-program tests; reports separately so it doesn't muddy
    // the source-test counts.
    let attrs = runAttrsTests ()
    // Phase C Step 2: F# unit tests for the codegen substitution mechanism.
    let subst = runCodeGenSubstTests ()
    // Canonical ExprShape traversal: round-trips, walker completeness (§3.2).
    let shape = Blade.Tests.Shape.runShapeTests ()
    // Oracle review: differential-harness oracles vs hand-computed truth (Phase 0.2).
    let oracles = Blade.Tests.OracleReview.runOracleTests ()
    // C++ runtime-layout tests for the contiguous-backing allocate<>.
    // Verifies layout invariants the value-checking source tests cannot catch.
    // Skips cleanly if g++ absent.
    let alloc = runAllocLayoutTests ()
    // OpenMP thread-coverage: verifies emitted pragmas form genuine parallel
    // regions when cores are available. Opt-in (see FullSuiteOptions).
    let omp =
        if opts.IncludeOmp then Some (runOmpCoverageTests ())
        else
            printfn "\nOpenMP coverage: not run (opt-in; enable with 'blade test --omp' or run 'blade test omp-coverage')."
            None
    // Device buffer dimensional-type tests (CUDA streaming foundation). Pure F#.
    let bufType = runBufferTypeTests ()
    // `where cuda` hardware tests (differential vs host-loop oracle). Opt-in;
    // even when requested they skip cleanly if nvcc/GPU/cl.exe are absent.
    let cuda =
        if opts.IncludeCuda then Some (runCudaTests ())
        else
            printfn "CUDA kernel tests: not run (opt-in; enable with 'blade test --cuda' from the x64 Native Tools prompt)."
            None
    // Differential symmetry harness: every symmetry case vs an independent F#
    // oracle over randomized inputs. Skips cleanly when g++ absent.
    let diff = runDifferentialSymmetryTest ()
    // Type-structure tests: assert deduced IR types of bindings (no codegen/run).
    let typeStruct = Blade.Tests.TypeStructure.runTypeStructureTests ()
    // Differential timing: measured (r!)^d speedup of comm-annotation and
    // symmetric-type forms vs their dense equivalents. Reports ratios; warns
    // (never fails) on a slow ratio. Skips cleanly when g++ absent. Opt-in:
    // it compiles + repeatedly runs large programs and dominates wall time.
    let timing =
        if opts.IncludeTiming then Some (runDifferentialTimingTests ())
        else
            printfn "Differential timing: not run (opt-in; enable with 'blade test --timing' or run 'blade test timing')."
            None
    // Caller-supplied blocks (see doc comment): currently the CLI smoke test.
    let extras = extraBlocks |> List.map (fun run -> run ())

    // Grand-total roll-up (#4): one line per block, a total, and failed names.
    let blocks =
        [ yield r1; yield r2; yield attrs; yield subst; yield shape; yield oracles; yield alloc
          match omp with Some b -> yield b | None -> ()
          yield bufType
          match cuda with Some b -> yield b | None -> ()
          yield diff; yield typeStruct
          match timing with Some b -> yield b | None -> ()
          yield! extras ]
    Blade.Tests.TestHarness.printGrandTotal blocks
    let anyFailed = blocks |> List.sumBy (fun b -> b.Failed)
    if anyFailed = 0 then 0 else 1

/// Full suite with no caller-supplied blocks (standalone/back-compat form).
let runAllTestsFull (opts: FullSuiteOptions) = runAllTestsFullWith [] opts

/// Run all tests with generate only
let runAllTestsGenOnly () =
    let outputDir = "./generated_cpp_tests"
    runTestCategoryGenOnly "All" allTests outputDir

let runAllTests () =
    let r1 = runTestCategory "All" allTests
    let r2 = runMultiFileTests "Multi-File Modules" multiFileTests
    if r1 = 0 && r2 = 0 then 0 else 1
