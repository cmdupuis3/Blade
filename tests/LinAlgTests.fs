// Linear-algebra dispatch EMISSION tests — Phases 5 / 5b / 5c of
// docs/plan-cpp-perf-exploitation.md.
//
// The corpus proves the VALUES (and `blade test interp math` proves they are
// byte-identical to the interpreter). This block proves the ROUTING: that a
// recognised shape reaches the `blade_linalg.hpp` shim when BLAS is available,
// that it emits Blade's OWN loops when it is not, that the header is included
// exactly when a route fires, and — as much as anything here — that a program
// using no route at all carries no dependency surface.
//
// Why assert on emitted TEXT. The routing decision is very nearly invisible in
// the values (BLAS agrees with Blade's loops to within a ULP), so a regression
// that silently stopped dispatching — or, worse, silently started — would leave
// every value test green. Only the emitted text can tell the two apart. The
// negative assertions matter as much as the positive ones: `cblas_` must NOT
// appear in generated code (the call spelling lives in the header), and neither
// the include nor `blade_linalg::` may appear when the gate is off.
//
// THE GATE IS PER-CASE (Phase 5c). `LinAlgPatterns.blasAvailable` re-reads the
// environment at every consultation — deliberately a function, never a frozen
// module value — so a `use`-guard around one emit is enough to pin it. Every
// case therefore declares which side of the gate it exercises, and the SHAPE
// negatives run with the gate ON so that their decline is proven to be about
// the shape rather than about availability.
//
// Pure codegen — no g++, no toolchain, no BLAS runtime. Always runs: pinning
// BLADE_BLAS=1 makes codegen emit shim CALLS, which needs no OpenBLAS
// installation because nothing here is compiled. (Compiling them is the
// corpus's job, and the corpus stays default-off.)
//
// The SECOND block in this file (`runLinAlgProbeTests`, also under `blade test
// linalg`) is the one thing emitted text cannot show: what the shim's runtime
// contiguity probe DECIDES. It compiles and runs cpp/linalg_probe_tests.cpp, so
// it needs g++ and skips without it — but it needs no BLAS, because the probe
// and the staging views live in `blade_linalg_views.hpp`, the BLAS-free half of
// the layer (Phase 5d).
module Blade.Tests.LinAlgTests

open System
open System.IO
open System.Diagnostics
open System.Runtime.InteropServices
open Blade
open Blade.Build
open Blade.Lowering
open Blade.Tests.TestHarness

/// Pin the BLAS availability gate for the duration of one emit, restoring the
/// prior value on exit. Same use-guard idiom as `DiffOracle.pinFpContractOff`,
/// and it works for the same reason: the gate is read per-call, so a
/// mid-process set takes effect immediately.
let private pinBlas (on: bool) =
    let prior = System.Environment.GetEnvironmentVariable("BLADE_BLAS")
    System.Environment.SetEnvironmentVariable("BLADE_BLAS", (if on then "1" else "0"))
    { new System.IDisposable with
        member _.Dispose() =
            System.Environment.SetEnvironmentVariable("BLADE_BLAS", prior) }

/// Lower + generate under a pinned gate, returning the C++ source. No compiler
/// involved. (Same helper shape as OmpTests.cppOf.)
let private cppOf (blasOn: bool) (testName: string) (src: string) : Result<string, string> =
    use _gate = pinBlas blasOn
    try
        match lower src with
        | Error e -> Error (sprintf "lower: %s" e)
        | Ok ir -> Ok (fst (CodeGen.genSelfContainedProgramFromIR ir testName))
    with ex -> Error (sprintf "codegen raised: %s" ex.Message)

let private shimInclude = "#include \"blade_linalg.hpp\""

/// (name, blasGateOn, source, mustContain, mustNotContain).
///
/// Every case asserts the absence of `cblas_` in the generated text: the
/// compiler NEVER writes a BLAS call itself, on any machine, with any
/// environment variable set. Which cblas routine runs is the header's business;
/// codegen's only decision is dispatch-or-not.
let private emissionCases : (string * bool * string * string list * string list) list =
    let realMat =
        "let A: Array<Float64 like Idx<3>, Idx<2>> = [[1.0, 2.0], [3.0, 4.0], [5.0, 6.0]]\n"
    let realMatB =
        "let B: Array<Float64 like Idx<4>, Idx<2>> = [[1.0, 2.0], [3.0, 4.0], [5.0, 6.0], [7.0, 8.0]]\n"
    // ---- Phase 5b fixtures ----
    let vecX = "let x: Array<Float64 like Idx<5>> = [1.0, 2.0, 3.0, 4.0, 5.0]\n"
    let vecY = "let y: Array<Float64 like Idx<5>> = [2.0, 3.0, 4.0, 5.0, 6.0]\n"
    /// An UNFORCED zip whose kernel is the product of the two co-iterated
    /// leaves — the deferred computation `reduce` folds without materializing.
    let deferredProd =
        "let P = method_for(zip(x, y)) <@> lambda(a: Float64, b: Float64) -> a * b\n"
    let matA =
        "type M = Idx<3>\ntype N = Idx<4>\n"
        + "let A: Array<Float64 like M, N> = [[1.0, 2.0, 3.0, 4.0], [5.0, 6.0, 7.0, 8.0], [9.0, 10.0, 11.0, 12.0]]\n"
    let vecXv = "let xv: Array<Float64 like N> = [1.0, 2.0, 3.0, 4.0]\n"
    [ // gram(A, A) — the symmetric rank-k update. Blade's result is PACKED
      // upper-triangular storage, which is why the route is its own adapter
      // rather than a bare `blade_syrk` call.
      //
      // Each positive case also pins the POOL CAPACITY argument that follows
      // every skeleton operand (Phase 5d). The shim's contiguity probe cannot
      // derive a pool's cell count from a row skeleton — row pointers give row
      // starts, not row lengths — so the emitter supplies it, and an emission
      // that dropped it would restore the n = 2 packed-symmetric false accept
      // with no runtime symptom. cpp/linalg_probe_tests.cpp proves what the
      // probe then does with it.
      ("gram_same_array_routes_to_syrk_adapter", true,
       realMat + "let G = gram(A, A)\n",
       [ shimInclude; "blade_linalg::blade_gram_same("; "linalg dispatch: gram(A, A)"
         "A.data, (A.extents[0] * A.extents[1])" ],
       [ "cblas_"; "#include <cblas.h>" ])
      // gram(A, B) — distinct operands, dense result: C = A * B^T, a gemm with
      // B transposed.
      // The dense OUTPUT's capacity is not inferred from a type — it is
      // allocated m x p a few lines above the call, so the emitter states it
      // directly as the extent product.
      ("gram_distinct_routes_to_gemm_adapter", true,
       realMat + realMatB + "let G = gram(A, B)\n",
       [ shimInclude; "blade_linalg::blade_gram_distinct("; "linalg dispatch: gram(A, B)"
         "A.data, (A.extents[0] * A.extents[1])"
         "B.data, (B.extents[0] * B.extents[1])"
         "G.data, (A.extents[0] * B.extents[0])" ],
       [ "cblas_"; "#include <cblas.h>"; "blade_gram_same" ])
      // matmul — the first-class intrinsic. `__math_matmul` must NOT survive
      // into the output (it is a pre-inference marker), and no synthesized
      // `__math_<n>` triple-loop function may be generated for it either.
      ("matmul_routes_to_gemm_adapter", true,
       "import math as m\n" +
       "let A: Array<Float64 like Idx<2>, Idx<3>> = [[1.0, 2.0, 3.0], [4.0, 5.0, 6.0]]\n" +
       "let B: Array<Float64 like Idx<3>, Idx<2>> = [[7.0, 8.0], [9.0, 10.0], [11.0, 12.0]]\n" +
       "let C = m.matmul(A, B)\n",
       [ shimInclude; "blade_linalg::blade_matmul("; "linalg dispatch: matmul(A, B)"
         "A.data, (A.extents[0] * A.extents[1])"
         "B.data, (B.extents[0] * B.extents[1])"
         "C.data, (A.extents[0] * B.extents[1])" ],
       [ "cblas_"; "#include <cblas.h>"; "__math_matmul"; "double __math_1" ])
      // COMPLEX gram keeps the scalar loops: the shim's v1 domain is real f64
      // (dsyrk/dgemm), exactly the restriction the pre-shim BLAS lowering had.
      // A complex program must therefore name neither the header nor a route.
      ("complex_gram_stays_on_scalar_loops", true,
       "let A: Array<Complex128 like Idx<2>, Idx<2>> = [[complex(1.0, 0.0), complex(2.0, 1.0)], [complex(3.0, 0.0), complex(4.0, -1.0)]]\n" +
       "let G = gram(A, A)\n",
       [ "conj_scalar" ],
       [ shimInclude; "blade_linalg::"; "cblas_" ])
      // NO GRATUITOUS DEPENDENCY SURFACE. A program with no linalg route must
      // not name the header at all — the include is collector-driven, not
      // unconditional, even though the file itself is deployed unconditionally.
      ("no_linalg_program_excludes_the_header", true,
       "let A = [1.0, 2.0, 3.0]\nlet s = reduce(A, (+))\n",
       [],
       [ shimInclude; "blade_linalg::"; "cblas_" ])

      // ================= Phase 5b: L1 dot =================
      // `reduce(<unforced zip under `*`>, (+))` over two rank-1 f64 arrays.
      // The whole fused accumulator loop disappears: no wrapper lambda, no
      // `for (size_t __i0 ...)`, one call carrying the SEED.
      ("dot_reduce_over_deferred_zip_routes_to_dot", true,
       vecX + vecY + deferredProd + "let s = reduce(P, (+))\n",
       [ shimInclude; "blade_linalg::blade_dot("; "linalg dispatch: dot(x, y)"
         // seed = (+)'s implicit identity, passed through to the shim so the
         // native fallback starts its accumulator exactly where the loop did.
         "x.data, y.data, 0.0)" ],
       [ "cblas_"; "for (size_t __i0" ])
      // The 3-arg form: the user's `init` is the seed, and it reaches the shim.
      ("dot_with_explicit_init_carries_the_seed", true,
       vecX + vecY + deferredProd + "let s = reduce(P, (+), 100.0)\n",
       [ "blade_linalg::blade_dot("; "x.data, y.data, 100.0)" ],
       [ "cblas_"; "for (size_t __i0" ])
      // PRECEDENCE (the load-bearing negative). An `omp`-licensed fold kernel
      // keeps Phase 2's chunked parallel fold. Firing the dot route here would
      // silently convert an EXPLICIT user reorder licence into serial code in
      // every build without BLAS, with nothing in the emitted text to show for
      // it — so the licence outranks the dispatch heuristic, always.
      ("dot_declines_when_the_fold_kernel_is_omp_licensed", true,
       vecX + vecY + deferredProd
       + "let s = reduce(P, lambda(p: Float64, q: Float64) where comm(p, q), omp -> p + q, 0.0)\n",
       [ "comm-licensed parallel fold"; "omp_get_num_threads()" ],
       [ "blade_linalg::"; shimInclude; "cblas_" ])
      // ELEMENT TYPE. The shim's v1 domain is real f64 (`ddot`). An integer
      // fold keeps its accumulator loop and names nothing.
      ("dot_declines_on_integer_elements", true,
       "let x: Array<Int64 like Idx<5>> = [1, 2, 3, 4, 5]\n"
       + "let y: Array<Int64 like Idx<5>> = [2, 3, 4, 5, 6]\n"
       + "let P = method_for(zip(x, y)) <@> lambda(a: Int64, b: Int64) -> a * b\n"
       + "let s = reduce(P, (+))\n",
       [ "for (size_t __i0" ],
       [ "blade_linalg::"; shimInclude; "cblas_" ])
      // The MATERIALIZED twin is deliberately NOT matched in v1: `x * y` is
      // synthesized WITH `|> compute`, so the reduce sees a real array (the
      // ordinary `IRReduce` path) and the intermediate is already paid for —
      // dispatching the second half alone saves far less than fusing the two.
      ("dot_declines_on_the_materialized_array_form", true,
       vecX + vecY + "let P = x * y\nlet s = reduce(P, (+))\n",
       [ "reduce: accumulator loop" ],
       [ "blade_linalg::"; shimInclude ])

      // ================= Phase 5b: L2 gemv =================
      // A per-row apply whose kernel is `prodsum(row, x)`: rank-2 operand
      // peeled one level, shared rank-1 vector, rank-1 output. The whole nest
      // — row peel, prodsum IIFE and all — becomes one call.
      ("gemv_per_row_prodsum_fiber_routes_to_gemv", true,
       matA + vecXv
       + "let yv = method_for(A) <@> lambda(row: Array<Float64 like N>) -> prodsum(row, xv) |> compute\n",
       [ shimInclude; "blade_linalg::blade_gemv("; "linalg dispatch: gemv y = A * xv"
         // m from the row loop's own bound, n from A's TRAILING extent — both
         // literal after shape monomorphization, exactly as the nest would —
         // and A's pool capacity, because gemv stages A through the same
         // `in_view` the L3 adapters use (Phase 5d). `xv` and `yv` are rank-1
         // pools handed over directly: no skeleton, hence no probe, hence no
         // capacity argument.
         "blade_linalg::blade_gemv(3, 4, A.data, (A.extents[0] * A.extents[1]), xv.data, yv.data)" ],
       // The NEST is gone: no row peel, no per-row output write. (The lifted
       // kernel lambda itself is still emitted as a now-unused function — it
       // always was, since the nest inlined the body rather than calling it —
       // so its `prodsum` IIFE is not a usable negative here.)
       [ "cblas_"; "A____i0"; "yv[__i0]" ])
      // SELF-DOT PER ROW is not a matrix-vector product. `prodsum(er, er)` (the
      // row-norm idiom the math corpus uses) matches every structural gate;
      // only the "second operand comes from outside the kernel" guard separates
      // them, and routing it would pass the peeled row as `x` for every row.
      ("gemv_declines_on_per_row_self_dot", true,
       matA
       + "let rowsums = method_for(A) <@> lambda(er: Array<Float64 like N>) -> prodsum(er, er) |> compute\n",
       [ "__ps = 0" ],
       [ "blade_linalg::"; shimInclude ])
      // ARGUMENT ORDER is not cosmetic: `IRProdSum` takes its loop bound from
      // its FIRST operand, so `prodsum(xv, row)` is bounded by the VECTOR's
      // extent, not by A's trailing extent. Blade's unify does not compare
      // extents, so the two can disagree — declining is the honest answer.
      ("gemv_declines_when_the_vector_comes_first", true,
       matA + vecXv
       + "let yv = method_for(A) <@> lambda(row: Array<Float64 like N>) -> prodsum(xv, row) |> compute\n",
       [ "xv.extents[0]" ],
       [ "blade_linalg::"; shimInclude ])
      // PRECEDENCE, same rule as dot: an `omp` request on the row kernel keeps
      // the nest, so the pragma the user asked for still lands on it.
      ("gemv_declines_when_the_row_kernel_requests_omp", true,
       matA + vecXv
       + "let yv = method_for(A) <@> lambda(row: Array<Float64 like N>) where omp -> prodsum(row, xv) |> compute\n",
       [ "#pragma omp parallel for" ],
       [ "blade_linalg::"; shimInclude ])

      // ============ Phase 5c: the GATE-OFF side (the default build) ============
      // With BLAS unavailable, no route is emitted and the native math comes
      // from Blade's OWN pre-existing emission paths. These four cases are the
      // exact positives above with the gate flipped, so together each pair
      // isolates the gate as the only difference. The `blade_linalg` negatives
      // are the load-bearing half: since Phase 5c the header has NO native
      // fallbacks and `#error`s without the define, so emitting the include
      // here would produce a program that does not compile.
      //
      // gram(A, A): the packed upper-triangular write, row-shortened, one
      // accumulator per canonical cell.
      ("gate_off_gram_same_emits_packed_triangular_loops", false,
       realMat + "let G = gram(A, A)\n",
       [ "for (size_t __gi"; "__gjr < A.extents[0] - __gi"; "G[__gi][__gjr] = __gacc;" ],
       [ shimInclude; "blade_linalg::"; "cblas_" ])
      // gram(A, B): the dense scatter over all (i, j).
      ("gate_off_gram_distinct_emits_dense_loops", false,
       realMat + realMatB + "let G = gram(A, B)\n",
       [ "for (size_t __gi"; "for (size_t __gj"; "G[__gi][__gj] = __gacc;" ],
       [ shimInclude; "blade_linalg::"; "cblas_" ])
      // matmul: the triple loop, i/j/t ascending — the SAME order
      // Interp/ArrayOps.matmulArray uses, which is what makes `interp math`
      // a byte-identity test of the code an ordinary build actually runs.
      ("gate_off_matmul_emits_native_triple_loop", false,
       "import math as m\n" +
       "let A: Array<Float64 like Idx<2>, Idx<3>> = [[1.0, 2.0, 3.0], [4.0, 5.0, 6.0]]\n" +
       "let B: Array<Float64 like Idx<3>, Idx<2>> = [[7.0, 8.0], [9.0, 10.0], [11.0, 12.0]]\n" +
       "let C = m.matmul(A, B)\n",
       [ "for (size_t __mi"; "for (size_t __mj"; "for (size_t __mt"
         "__macc += A[__mi][__mt] * B[__mt][__mj];" ],
       [ shimInclude; "blade_linalg::"; "cblas_"; "__math_matmul" ])
      // dot: the fused fold nest, unchanged from before any dispatch existed.
      ("gate_off_dot_emits_the_fold_nest", false,
       vecX + vecY + deferredProd + "let s = reduce(P, (+))\n",
       [ "for (size_t __i0"; "(x____i0 * y____i0)" ],
       [ shimInclude; "blade_linalg::" ])
      // gemv: the per-row peel plus the prodsum IIFE, likewise unchanged.
      ("gate_off_gemv_emits_the_per_row_nest", false,
       matA + vecXv
       + "let yv = method_for(A) <@> lambda(row: Array<Float64 like N>) -> prodsum(row, xv) |> compute\n",
       [ "A____i0"; "yv[__i0]"; "__ps = 0" ],
       [ shimInclude; "blade_linalg::" ]) ]

let runLinAlgEmissionTests () : BlockResult =
    printHeader "LinAlg Dispatch Emission"
    let mutable passed = 0
    let mutable failed = 0
    let mutable failedNames = []
    let fail name detail =
        failed <- failed + 1
        failedNames <- failedNames @ [name]
        resultLine Fail name detail
    for (name, blasOn, src, mustContain, mustNotContain) in emissionCases do
        match cppOf blasOn name src with
        | Error e -> fail name e
        | Ok cpp ->
            let missing = mustContain |> List.filter (fun s -> not (cpp.Contains s))
            let present = mustNotContain |> List.filter cpp.Contains
            if not missing.IsEmpty then
                fail name (sprintf "generated C++ lacks: %s" (String.concat " | " missing))
            elif not present.IsEmpty then
                fail name (sprintf "generated C++ unexpectedly contains: %s" (String.concat " | " present))
            else
                passed <- passed + 1
                resultLine Pass name (if blasOn then "routing as expected (gate on)"
                                      else "native emission as expected (gate off)")

    // ---- the availability gate itself ----
    // Pinned because it is now shared with Build.fs's flag injection: a change
    // in these semantics silently changes which g++ line a program gets. The
    // unset case reads OPENBLAS_DIR, so it is exercised with that pinned too.
    let gateCases =
        [ "gate_forced_on",   Some "1",   None,                  true
          "gate_forced_on_word", Some "on", None,                true
          "gate_forced_off",  Some "0",   Some "C:\\opt\\blas",  false
          "gate_forced_off_word", Some "off", Some "C:\\opt\\blas", false
          "gate_unset_follows_openblas_dir", None, Some "C:\\opt\\blas", true
          "gate_unset_without_openblas_dir", None, None,         false ]
    for (name, blasVar, openblasVar, expected) in gateCases do
        let priorB = System.Environment.GetEnvironmentVariable("BLADE_BLAS")
        let priorO = System.Environment.GetEnvironmentVariable("OPENBLAS_DIR")
        try
            System.Environment.SetEnvironmentVariable("BLADE_BLAS", Option.toObj blasVar)
            System.Environment.SetEnvironmentVariable("OPENBLAS_DIR", Option.toObj openblasVar)
            let actual = LinAlgPatterns.blasAvailable ()
            if actual = expected then
                passed <- passed + 1
                resultLine Pass name (sprintf "%b" actual)
            else fail name (sprintf "expected %b, got %b" expected actual)
        finally
            System.Environment.SetEnvironmentVariable("BLADE_BLAS", priorB)
            System.Environment.SetEnvironmentVariable("OPENBLAS_DIR", priorO)

    // ---- the policy table is the documented contract, so pin it ----
    // A row moving from Native to ViaShim (or vanishing) is a deliberate
    // decision that should require editing a test, not a silent drift.
    //
    // These need NO gate pin, and that is the point of keeping `routingOf`
    // free of the availability question (Phase 5c): the table states whether a
    // shape is WORTH a BLAS call, which is a property of the routine and must
    // read the same on a machine with OpenBLAS and one without. Availability
    // enters one level down, in `shimEntryPoint`.
    let policyCases =
        [ "gemm_via_shim", LinAlgPatterns.Gemm, LinAlgPatterns.ViaShim
          "syrk_via_shim", LinAlgPatterns.Syrk, LinAlgPatterns.ViaShim
          "dot_via_shim",  LinAlgPatterns.Dot,  LinAlgPatterns.ViaShim
          "gemv_via_shim", LinAlgPatterns.Gemv, LinAlgPatterns.ViaShim
          // Same paying-L1-reduction policy as dot, but NOT MATCHED in v1 (no
          // sqrt-shape case exists). The row is here so "routed via the shim
          // but never recognised" stays readable from the table rather than
          // being an undocumented gap.
          "nrm2_via_shim", LinAlgPatterns.Nrm2, LinAlgPatterns.ViaShim
          // The recorded "matched but routed native" decisions: L1-elementwise
          // is bandwidth-bound and the flat loop already vectorises it.
          "axpy_native",   LinAlgPatterns.Axpy, LinAlgPatterns.Native
          "scal_native",   LinAlgPatterns.Scal, LinAlgPatterns.Native ]
    for (name, routine, expected) in policyCases do
        let actual = LinAlgPatterns.routingOf routine
        if actual = expected then
            passed <- passed + 1
            resultLine Pass name (sprintf "%A" actual)
        else fail name (sprintf "expected %A, got %A" expected actual)

    printFooter "LinAlg Dispatch" [sprintf "%d passed" passed; sprintf "%d failed" failed]
    { Block = "LinAlg Dispatch"; Passed = passed; Failed = failed; Skipped = 0; FailedNames = failedNames }


/// Run the standalone C++ contiguity-probe tests (cpp/linalg_probe_tests.cpp).
///
/// The emission block above proves gram/matmul/dot/gemv REACH the shim. This one
/// proves what the shim's `row_major_base` then decides at runtime —
/// specifically that the degenerate n = 2 packed-symmetric skeleton, whose row
/// starts are identical to a dense 2x2's over a pool one cell shorter, is
/// REFUSED rather than handed to BLAS (docs/plan-cpp-perf-exploitation.md,
/// Phase 5d).
///
/// No value test can see this: the probe's accept and refuse arms are
/// value-identical by construction, so a false accept produces correct output
/// and an out-of-bounds read. The property is a C++ runtime invariant about
/// pointer arithmetic, so it is tested in C++ against the SHIPPED headers —
/// same category and same harness shape as `blade test alloc` (AllocTests.fs).
///
/// NEEDS g++, NOT BLAS. The probe test includes `blade_linalg_views.hpp`, the
/// BLAS-free half of the layer, so it compiles and runs on a machine with no
/// OpenBLAS — which is the whole point of that split. Returns Skipped = 1 when
/// g++ is absent; never fails for toolchain reasons.
let runLinAlgProbeTests () : BlockResult =
    let blockName = "LinAlg Probe"
    printHeader "LinAlg Contiguity Probe"
    let cppDir = Path.Combine(AppContext.BaseDirectory, "cpp")
    let testSrc = Path.Combine(cppDir, "linalg_probe_tests.cpp")
    let caps = capabilities.Value
    if not caps.HasGpp then
        printfn "Skipped: g++ not found (cannot compile C++ probe tests)."
        { Block = blockName; Passed = 0; Failed = 0; Skipped = 1; FailedNames = [] }
    elif not (File.Exists testSrc) then
        eprintfn "linalg_probe_tests.cpp not found at: %s" testSrc
        eprintfn "Check that Blade.fsproj copies cpp/linalg_probe_tests.cpp to the output dir."
        { Block = blockName; Passed = 0; Failed = 1; Skipped = 0
          FailedNames = ["linalg_probe_tests.cpp missing"] }
    else
        let exeExt = if RuntimeInformation.IsOSPlatform(OSPlatform.Windows) then ".exe" else ".out"
        let exePath = Path.ChangeExtension(testSrc, exeExt)
        // Compile IN cppDir so the `#include "blade_linalg_views.hpp"` resolves
        // to the shipped header the codegen path deploys — a stale copy would
        // defeat the point of running this at all. No -DBLADE_HAS_BLAS and no
        // -I/-l: the views header needs none.
        let args = sprintf "-std=c++17 %s -o \"%s\" \"%s\"" (optFlags ()) exePath testSrc
        let psi = ProcessStartInfo("g++", args)
        psi.RedirectStandardOutput <- true
        psi.RedirectStandardError <- true
        psi.UseShellExecute <- false
        psi.CreateNoWindow <- true
        psi.WorkingDirectory <- cppDir
        use cproc = Process.Start(psi)
        let cOut = cproc.StandardOutput.ReadToEndAsync()
        let cErr = cproc.StandardError.ReadToEndAsync()
        let cExited = cproc.WaitForExit(60000)
        if not cExited then
            (try cproc.Kill(true) with _ -> ())
            printfn "C++ compilation TIMED OUT (60s)"
            printFooter blockName ["FAILED"]
            { Block = blockName; Passed = 0; Failed = 1; Skipped = 0; FailedNames = ["<compile timeout>"] }
        elif cproc.ExitCode <> 0 then
            printfn "C++ compilation FAILED:"
            printfn "%s" (cOut.Result + "\n" + cErr.Result)
            printFooter blockName ["FAILED"]
            { Block = blockName; Passed = 0; Failed = 1; Skipped = 0; FailedNames = ["<compile failed>"] }
        else
            let rpsi = ProcessStartInfo(exePath)
            rpsi.RedirectStandardOutput <- true
            rpsi.RedirectStandardError <- true
            rpsi.UseShellExecute <- false
            rpsi.CreateNoWindow <- true
            rpsi.WorkingDirectory <- cppDir
            use rproc = Process.Start(rpsi)
            let rOut = rproc.StandardOutput.ReadToEndAsync()
            let rErr = rproc.StandardError.ReadToEndAsync()
            let rExited = rproc.WaitForExit(30000)
            if not rExited then
                (try rproc.Kill(true) with _ -> ())
                rproc.WaitForExit(5000) |> ignore
                printfn "linalg probe test binary TIMED OUT (30s)"
            printf "%s" rOut.Result
            if not (String.IsNullOrWhiteSpace rErr.Result) then eprintf "%s" rErr.Result
            let outText = rOut.Result.Replace("\r\n", "\n")
            let m =
                System.Text.RegularExpressions.Regex.Match(
                    outText, @"LINALG PROBE TESTS:\s*(\d+)/(\d+)\s*passed")
            let pPassed = if m.Success then int m.Groups.[1].Value else 0
            let pTotal = if m.Success then int m.Groups.[2].Value else 0
            let failNames =
                outText.Split('\n')
                |> Array.choose (fun l ->
                    let fm = System.Text.RegularExpressions.Regex.Match(l, @"\[FAIL\]:\s*(.+)$")
                    if fm.Success then Some (fm.Groups.[1].Value.Trim()) else None)
                |> Array.toList
            let pFailed = if pTotal >= pPassed then pTotal - pPassed else failNames.Length
            // Same doctrine as AllocTests: the summary line must be present
            // before an exit 0 is read as a pass, so a binary that aborted
            // before running any check cannot score a vacuous 0/0.
            if not rExited then
                printFooter blockName ["FAILED"]
                { Block = blockName; Passed = 0; Failed = 1; Skipped = 0; FailedNames = ["<run timeout>"] }
            elif not m.Success then
                printFooter blockName ["FAILED"]
                printfn "  no 'LINALG PROBE TESTS: p/n passed' summary in output -- cannot confirm any check ran"
                { Block = blockName; Passed = 0; Failed = 1; Skipped = 0
                  FailedNames = ["<no LINALG PROBE TESTS summary line>"] }
            elif rproc.ExitCode = 0 then
                printFooter blockName ["all passed"]
                { Block = blockName; Passed = pPassed; Failed = 0; Skipped = 0; FailedNames = [] }
            else
                printFooter blockName ["FAILED"]
                { Block = blockName; Passed = pPassed; Failed = max 1 pFailed; Skipped = 0
                  FailedNames = (if failNames.IsEmpty then [sprintf "<exit %d>" rproc.ExitCode] else failNames) }
