// Differential MPI decomposition tests (mpi-vs-serial codegen equivalence).
// One source per case: with the mpi emit gate OFF the `where mpi` clause is
// inert (serial host loop — the oracle); with the gate ON the same source
// gets slab decomposition + Allgatherv + rank-0 printing, compiled with
// -lmsmpi and run under `mpiexec -n {1,2,4}`. Outputs must be identical
// (timing lines stripped) — the SPMD invariant makes rank count unobservable.
// Requires g++ + the mingw-w64 msmpi package + the MS-MPI runtime (mpiexec);
// skips cleanly otherwise.
module Blade.Tests.MpiTests

open System.IO
open Blade
open Blade.IR
open Blade.Lowering
open Blade.CodeGen
open Blade.Build
open Blade.Tests.TestHarness

let runMpiTests () : Blade.Tests.TestHarness.BlockResult =
    printHeader "MPI Decomposition Tests"
    let skipResult = { Block = "MPI Decomposition"; Passed = 0; Failed = 0; Skipped = 0; FailedNames = [] }
    let caps = capabilities.Value
    if not caps.HasGpp then
        printfn "Skipped: requires g++, not found."
        skipResult
    elif not hasMpiLink.Value then
        printfn "Skipped: g++ cannot link MS-MPI (-lmsmpi)."
        printfn "         Install it with: pacman -S mingw-w64-ucrt-x86_64-msmpi (MSYS2)."
        skipResult
    elif mpiexecPath.Value.IsNone then
        printfn "Skipped: mpiexec not found."
        printfn "         Install the MS-MPI runtime (msmpisetup.exe, microsoft/Microsoft-MPI releases)."
        skipResult
    else
        let outputDir = "./generated_cpp_tests"
        Directory.CreateDirectory(outputDir) |> ignore
        CodeGen.deployRuntimeHeaders outputDir

        // Lower + codegen one variant under the CURRENT emit-gate state.
        let genVariant (name: string) (src: string) : Result<string, string> =
            try
                match lower src with
                | Error e -> Error (sprintf "lower failed: %s" e)
                | Ok ir0 ->
                    let ir = match IR.validateIR ir0 with Ok v -> v | Error _ -> ir0
                    let (cppCode, _w) = CodeGen.genSelfContainedProgramFromIR ir name
                    let safe = sanitizeFileName name
                    let cppFile = Path.Combine(outputDir, safe + ".cpp")
                    File.WriteAllText(cppFile, cppCode)
                    Ok cppFile
            with ex -> Error (sprintf "codegen failed: %s" ex.Message)

        let resultLines (s: string) =
            (s.Replace("\r\n", "\n").Trim()).Split('\n')
            |> Array.filter (fun l -> not (l.Contains("completed in")) && not (l.Contains("allocation took")))
            |> String.concat "\n"

        let mutable passed = 0
        let mutable failures = 0
        let mutable failedNames : string list = []
        let pass label detail =
            passed <- passed + 1
            resultLine Pass label detail
        let fail label detail =
            failures <- failures + 1
            failedNames <- failedNames @ [label]
            resultLine Fail label detail

        // One differential case: serial oracle (gate off, inertness pinned)
        // vs MPI build (gate on, scaffolding pinned) at -n 1, 2, and 4.
        let runMpiCase (label: string) (src: string) : unit =
            let serialName = sprintf "mpi_%s_serial" label
            let mpiName = sprintf "mpi_%s_mpi" label
            for stem in [serialName; mpiName] do
                for ext in [".cpp"; ".exe"; ".out"] do
                    let f = Path.Combine(outputDir, stem + ext)
                    try if File.Exists f then File.Delete f with _ -> ()
            let serialOut =
                setMpiEmitMode false
                match genVariant serialName src with
                | Error e -> Error e
                | Ok cppFile ->
                    let cpp = File.ReadAllText cppFile
                    if cpp.Contains "MPI_Init" || cpp.Contains "mpi.h" then
                        Error "serial variant unexpectedly emitted MPI scaffolding (gate leak)"
                    else
                        match compileCpp cppFile outputDir with
                        | Error e -> Error (sprintf "serial compile: %s" e)
                        | Ok exe ->
                            match runExecutable exe with
                            | Error e -> Error (sprintf "serial run: %s" e)
                            | Ok (0, out) -> Ok out
                            | Ok (code, out) -> Error (sprintf "serial run exit %d:\n%s" code out)
            let mpiOuts =
                setMpiEmitMode true
                try
                    match genVariant mpiName src with
                    | Error e -> Error e
                    | Ok cppFile ->
                        let cpp = File.ReadAllText cppFile
                        if not (cpp.Contains "MPI_Init") then
                            Error "mpi variant did not emit MPI scaffolding"
                        else
                            match compileCpp cppFile outputDir with
                            | Error e -> Error (sprintf "mpi compile: %s" e)
                            | Ok exe ->
                                let runs =
                                    [1; 2; 4]
                                    |> List.map (fun n ->
                                        match runExecutableMpi n exe with
                                        | Error e -> Error (sprintf "-n %d: %s" n e)
                                        | Ok (0, out) -> Ok (n, out)
                                        | Ok (code, out) -> Error (sprintf "-n %d exit %d:\n%s" n code out))
                                match runs |> List.tryPick (function Error e -> Some e | Ok _ -> None) with
                                | Some e -> Error e
                                | None -> Ok (runs |> List.map (function Ok r -> r | Error _ -> failwith "unreachable"))
                finally
                    setMpiEmitMode false
            match serialOut, mpiOuts with
            | Error e, _ -> fail label (sprintf "serial oracle: %s" e)
            | _, Error e -> fail label (sprintf "mpi: %s" e)
            | Ok sOut, Ok runs ->
                let oracle = resultLines sOut
                let mismatches =
                    runs |> List.filter (fun (_, out) -> resultLines out <> oracle)
                if mismatches.IsEmpty then
                    pass label "mpi -n 1/2/4 all match serial oracle"
                else
                    let (n, out) = List.head mismatches
                    fail label (sprintf "-n %d output differs from serial oracle" n)
                    printfn "    serial: %s" oracle
                    printfn "    -n %d:  %s" n (resultLines out)

        // Ineligible-shape case: under the gate the generated source must
        // carry the loud #error marker (never a silently serialized nest).
        let runRejectCase (label: string) (src: string) (expectFragment: string) : unit =
            let nm = sprintf "mpi_%s_reject" label
            setMpiEmitMode true
            try
                match genVariant nm src with
                | Error e -> fail label (sprintf "codegen: %s" e)
                | Ok cppFile ->
                    let cpp = File.ReadAllText cppFile
                    if cpp.Contains "#error \"mpi:" && cpp.Contains expectFragment then
                        pass label "ineligible shape rejected with #error"
                    else
                        fail label (sprintf "expected #error with '%s' in generated source" expectFragment)
            finally
                setMpiEmitMode false

        // ---- Case sources ------------------------------------------------
        // Slab basics: n = 8 splits evenly at P = 1, 2, 4.
        let denseRank1 = """
let A = [1.0, 2.0, 3.0, 4.0, 5.0, 6.0, 7.0, 8.0]
let R = method_for(A) <@> lambda(x) where mpi -> x * 2.0 + 1.0 |> compute
"""
        // Balanced remainder: n = 7 -> 2+2+2+1 at P = 4.
        let denseRemainder = """
let A = [1.0, 2.0, 3.0, 4.0, 5.0, 6.0, 7.0]
let R = method_for(A) <@> lambda(x) where mpi -> x * x |> compute
"""
        // P > n: at -n 4 with n = 3, one rank has an empty slab (lo == hi)
        // and a zero-count Allgatherv contribution.
        let denseSmallN = """
let A = [10.0, 20.0, 30.0]
let R = method_for(A) <@> lambda(x) where mpi -> x + 1.0 |> compute
"""
        // Outer product: rank-2 output, slab on the outer level only; the
        // gather's counts math uses the inner extent (R_extents[1]).
        let denseRank2Outer = """
let A = [1.0, 2.0, 3.0]
let B = [4.0, 5.0, 6.0, 7.0]
let R = method_for(A, B) <@> lambda(x, y) where mpi -> x * y |> compute
"""
        // Rank-3 outer with DISTINCT extents (3x2x4): the inner product in
        // the gather is extents[1]*extents[2] with different moduli.
        let denseRank3Outer = """
let A = [1.0, 2.0, 3.0]
let B = [10.0, 20.0]
let C = [100.0, 200.0, 300.0, 400.0]
let R = method_for(A, B, C) <@> lambda(a, b, c) where mpi -> a + b + c |> compute
"""
        // Int64 elements: MPI_LONG_LONG datatype arm.
        let denseInt = """
let A = [1, 2, 3, 4, 5, 6]
let R = method_for(A) <@> lambda(x) where mpi -> x * 10 |> compute
"""
        // THE SPMD-invariant pin: a serial kernel consumes the mpi kernel's
        // output. Only correct if the Allgatherv restored ALL of R on EVERY
        // rank — a missing/partial gather makes S wrong on some rank, and
        // rank 0's print of S would still expose it at -n > 1 only if rank 0
        // itself lacked cells, so S is also printed (rank 0 prints, and rank
        // 0 computed S from its own gathered copy of R).
        let downstream = """
let A = [1.0, 2.0, 3.0, 4.0, 5.0, 6.0, 7.0, 8.0]
let R = method_for(A) <@> lambda(x) where mpi -> x * 3.0 |> compute
let S = method_for(R) <@> lambda(r) -> r + 0.5 |> compute
"""
        // Two independent mpi kernels in one program: per-nest local names
        // must not collide; two Allgathervs; one Init/Finalize pair.
        let multiKernel = """
let A = [1.0, 2.0, 3.0, 4.0, 5.0]
let B = [10.0, 20.0, 30.0]
let R = method_for(A) <@> lambda(x) where mpi -> x * 2.0 |> compute
let S = method_for(B) <@> lambda(y) where mpi -> y + 5.0 |> compute
"""
        // fill_random SPMD replication: rand() is un-seeded (C default seed 1)
        // so every rank materializes the identical input array; the mpi kernel
        // then decomposes over it. Serial-vs-mpi equality pins exactly this.
        let fillRandom = """
type I8 = Idx<8>
let A: Array<Int64 like I8> = fill_random(100)
let R = method_for(A) <@> lambda(x) where mpi -> x * 2 |> compute
"""
        // SYMMETRIC rank-2 inclusive simplex (i<=j): C(6,2) = 15 cells split
        // 8/7 at P=2, 4/4/4/3 at P=4. Packed-pool Allgatherv over cell ranges.
        let sym2 = """
let A = [1.0, 2.0, 3.0, 4.0, 5.0]
let R = method_for(A, A) <@> lambda(x, y) where comm(x, y), mpi -> 2.0 * x + y |> compute
"""
        // ANTISYMMETRIC rank-2 strict simplex (i<j): C(5,2) = 10 cells;
        // reynolds(g, Antisymmetric) folds to g(x,y)-g(y,x) — pins the strict
        // unrank + sign under decomposition.
        let antisym2 = """
let A = [1.0, 2.0, 3.0, 4.0, 5.0]
let g = lambda(x, y) where comm(x, y), mpi -> x * x * y
let R = method_for(A, A) <@> reynolds(g, Antisymmetric) |> compute
"""
        // SYMMETRIC rank-3 inclusive simplex (i<=j<=k): C(7,3) = 35 cells —
        // the r=3 unrank depth.
        let sym3 = """
let A = [1.0, 2.0, 3.0, 4.0, 5.0]
let R = method_for(A, A, A) <@> lambda(x, y, z) where comm(x, y, z), mpi -> x + 2.0 * y + 3.0 * z |> compute
"""
        // ANTISYMMETRIC rank-3 strict simplex (i<j<k): C(5,3) = 10 cells;
        // non-degenerate kernel so the signed fold has distinct values.
        let antisym3 = """
let A = [1.0, 2.0, 3.0, 4.0, 5.0]
let g = lambda(x, y, z) where comm(x, y, z), mpi -> x * x * y + z
let R = method_for(A, A, A) <@> reynolds(g, Antisymmetric) |> compute
"""
        // Downstream consumption of a PACKED mpi output: the serial unary map
        // over R (packing-preserving) is only correct if the cell-range
        // Allgatherv restored the whole pool on every rank.
        let symDownstream = """
let A = [1.0, 2.0, 3.0, 4.0, 5.0]
let R = method_for(A, A) <@> lambda(x, y) where comm(x, y), mpi -> x * y |> compute
let S = method_for(R) <@> lambda(r) -> r + 0.5 |> compute
"""
        // THE PROJECT PIN — comoment tensors: FIBER kernels (typed rank-1
        // params) over per-variable observation rows, prodsum inside the
        // kernel; SymIdx<2,4> pair + SymIdx<3,4> triple comoments with the
        // cell domain decomposed. Pins the sub-array-wrapper peel in
        // genMpiNestSimplicial (a raw row-pointer bind breaks prodsum's
        // `.extents[0]` bound).
        let comoment = """
type VarIdx = Idx<4>
type TimeIdx = Idx<3>
let A: Array<Float64 like VarIdx, TimeIdx> = [
    [1.0, 2.0, 3.0],
    [2.0, 4.0, 6.0],
    [4.0, 3.0, 2.0],
    [5.0, 5.0, 5.0]]
let m2 = method_for(A, A) <@> lambda(x: Array<Float64 like TimeIdx>, y: Array<Float64 like TimeIdx>) where comm(x, y), mpi -> prodsum(x, y) / 3.0 |> compute
let m3 = method_for(A, A, A) <@> lambda(x: Array<Float64 like TimeIdx>, y: Array<Float64 like TimeIdx>, z: Array<Float64 like TimeIdx>) where comm(x, y, z), mpi -> prodsum(x, y, z) / 3.0 |> compute
"""

        // CO-FUSION: multiple mpi leaves share ONE outer-row slab decomposition;
        // each leaf's output is restored by its own Allgatherv. Same-arity
        // (rank-1) and STAGGERED (rank-1 + rank-2 over the same array — the mpi
        // path supports staggering, unlike cuda). serial-vs-mpi equality pins
        // that every rank reassembles all outputs identically.
        let cofuseSameArity = """
let A = [1.0, 2.0, 3.0, 4.0, 5.0, 6.0, 7.0, 8.0]
let (u, v) = (method_for(A) <@> lambda(x) where mpi -> x * 2.0 + 1.0) <&!> (method_for(A) <@> lambda(x) where mpi -> x + 100.0) |> compute
"""
        let cofuseStaggered = """
let A = [1.0, 2.0, 3.0, 4.0, 5.0]
let (m1, m2) = (method_for(A) <@> lambda(x) where mpi -> x) <&!> (method_for(A, A) <@> lambda(x, y) where mpi -> x * y) |> compute
"""
        // COMPLEX dense slab: MPI_C_DOUBLE_COMPLEX Allgatherv over the
        // std::complex pool (layout-compatible with double[2]; probed against
        // MS-MPI at -n 1/2/4). n = 5 exercises the remainder split.
        let denseComplex = """
let Z = [complex(1.0, 2.0), complex(-0.5, 0.25), complex(3.0, -1.0), complex(0.0, 1.0), complex(2.0, 2.0)]
let R = method_for(Z) <@> lambda(z) where mpi -> z * conj(z) + 2.0 * z |> compute
"""
        // Complex TRANSCENDENTAL under mpi: every rank runs the same host
        // libm, so values are bit-identical across ranks — the differential
        // pins pure data movement of computed complex values.
        let denseComplexExp = """
let Z = [complex(0.1, 0.2), complex(-0.3, 0.4), complex(0.5, -0.5)]
let R = method_for(Z) <@> lambda(z) where mpi -> exp(z) |> compute
"""
        // COMPLEX symmetric simplex: packed-pool cell-range Allgatherv with
        // the complex datatype.
        let symComplex = """
let Z = [complex(1.0, 1.0), complex(2.0, -1.0), complex(0.5, 3.0), complex(-1.0, 2.0)]
let R = method_for(Z, Z) <@> lambda(x, y) where comm(x, y), mpi -> x * y + conj(x) * conj(y) |> compute
"""
        // Downstream consumption of a gathered COMPLEX output: S is only
        // correct on rank 0 if the Allgatherv restored all of R there.
        let complexDownstream = """
let Z = [complex(1.0, 2.0), complex(3.0, -1.0), complex(-2.0, 0.5), complex(0.0, 1.0)]
let R = method_for(Z) <@> lambda(z) where mpi -> z * 2.0 |> compute
let S = method_for(R) <@> lambda(r) -> r + complex(0.5, 0.5) |> compute
"""
        runMpiCase "cofuse_same_arity" cofuseSameArity
        runMpiCase "cofuse_staggered" cofuseStaggered
        runMpiCase "dense_complex" denseComplex
        runMpiCase "dense_complex_exp" denseComplexExp
        runMpiCase "sym_complex" symComplex
        runMpiCase "complex_downstream" complexDownstream
        runMpiCase "dense_rank1" denseRank1
        runMpiCase "dense_rank1_remainder" denseRemainder
        runMpiCase "dense_small_n" denseSmallN
        runMpiCase "dense_rank2_outer" denseRank2Outer
        runMpiCase "dense_rank3_outer" denseRank3Outer
        runMpiCase "dense_int64" denseInt
        runMpiCase "downstream_consumption" downstream
        runMpiCase "multi_mpi_kernel" multiKernel
        runMpiCase "fill_random_replication" fillRandom
        runMpiCase "sym2_flat" sym2
        runMpiCase "antisym2_flat" antisym2
        runMpiCase "sym3_flat" sym3
        runMpiCase "antisym3_flat" antisym3
        runMpiCase "sym_downstream" symDownstream
        runMpiCase "comoment_fiber_kernels" comoment

        printfn ""
        printfn "MPI Decomposition: %d passed, %d failed" passed failures
        { Block = "MPI Decomposition"; Passed = passed; Failed = failures; Skipped = 0; FailedNames = failedNames }
