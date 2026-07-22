// Blade-DSL Compiler Entry Point
//
// This file is intentionally minimal (audit §2.3): command parsing and
// dispatch live in Cli.fs, the C++/CUDA build orchestration in Build.fs,
// and the test framework in tests/ (Expect.fs, Runner.fs, Oracles.fs,
// Differential.fs, Benchmarks.fs, CudaTests.fs, OmpTests.fs, AllocTests.fs,
// NetcdfTests.fs, RunAll.fs).
module Blade.Main

// The whole compile pipeline recurses over the AST/IR (one native frame per
// nesting level), so deeply-nested — usually machine-generated — programs can
// overflow the default ~1 MB stack. Run dispatch on a large-stack thread; the
// test runner wraps its parallel per-test pipeline separately (Runner.fs),
// since those escape this thread onto the thread pool. See Runtime.fs.
[<EntryPoint>]
let main args = Blade.Runtime.runOnLargeStack (fun () -> Blade.Cli.dispatch args)
