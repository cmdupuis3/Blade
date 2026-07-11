// Blade-DSL Compiler Entry Point
//
// This file is intentionally minimal (audit §2.3): command parsing and
// dispatch live in Cli.fs, the C++/CUDA build orchestration in Build.fs,
// and the test framework in tests/ (Expect.fs, Runner.fs, Oracles.fs,
// Differential.fs, Benchmarks.fs, CudaTests.fs, OmpTests.fs, AllocTests.fs,
// NetcdfTests.fs, RunAll.fs).
module Blade.Main

[<EntryPoint>]
let main args = Blade.Cli.dispatch args
