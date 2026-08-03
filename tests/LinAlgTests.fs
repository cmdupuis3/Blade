// Linear-algebra dispatch EMISSION tests — Phase 5 of
// docs/plan-cpp-perf-exploitation.md.
//
// The corpus proves the VALUES (and `blade test interp math` proves they are
// byte-identical to the interpreter). This block proves the ROUTING: that gram
// and matmul reach the `blade_linalg.hpp` shim rather than an inline loop nest
// or an inline `cblas_*` call, that the header is included exactly when a route
// fires, and — as much as anything here — that a program using NEITHER carries
// no dependency surface at all.
//
// Why assert on emitted TEXT. The routing decision is invisible in the values:
// the shim's native fallback is byte-identical to the loops it replaced by
// construction, so a regression that silently stopped dispatching would leave
// every value test green. Only the emitted text can tell the two apart. The
// negative assertions matter as much as the positive ones: `cblas_` must NOT
// appear in generated code any more (the choice moved into the header's
// `#ifdef`, driven by Build.fs's -DBLADE_HAS_BLAS), and the include must be
// absent from programs with no linalg route.
//
// Pure codegen — no g++, no toolchain, no BLAS. Always runs.
module Blade.Tests.LinAlgTests

open Blade
open Blade.Lowering
open Blade.Tests.TestHarness

/// Lower + generate, returning the C++ source. No compiler involved.
/// (Same helper shape as OmpTests.cppOf.)
let private cppOf (testName: string) (src: string) : Result<string, string> =
    try
        match lower src with
        | Error e -> Error (sprintf "lower: %s" e)
        | Ok ir -> Ok (fst (CodeGen.genSelfContainedProgramFromIR ir testName))
    with ex -> Error (sprintf "codegen raised: %s" ex.Message)

let private shimInclude = "#include \"blade_linalg.hpp\""

/// (name, source, mustContain, mustNotContain).
///
/// Every case asserts the absence of `cblas_` in the generated text: after
/// Phase 5 the compiler NEVER writes a BLAS call itself, on any machine, with
/// any environment variable set. That is the property which makes the emitted
/// C++ reproducible independent of whether OpenBLAS happens to be installed.
let private emissionCases : (string * string * string list * string list) list =
    let realMat =
        "let A: Array<Float64 like Idx<3>, Idx<2>> = [[1.0, 2.0], [3.0, 4.0], [5.0, 6.0]]\n"
    let realMatB =
        "let B: Array<Float64 like Idx<4>, Idx<2>> = [[1.0, 2.0], [3.0, 4.0], [5.0, 6.0], [7.0, 8.0]]\n"
    [ // gram(A, A) — the symmetric rank-k update. Blade's result is PACKED
      // upper-triangular storage, which is why the route is its own adapter
      // rather than a bare `blade_syrk` call.
      ("gram_same_array_routes_to_syrk_adapter",
       realMat + "let G = gram(A, A)\n",
       [ shimInclude; "blade_linalg::blade_gram_same("; "linalg dispatch: gram(A, A)" ],
       [ "cblas_"; "#include <cblas.h>" ])
      // gram(A, B) — distinct operands, dense result: C = A * B^T, a gemm with
      // B transposed.
      ("gram_distinct_routes_to_gemm_adapter",
       realMat + realMatB + "let G = gram(A, B)\n",
       [ shimInclude; "blade_linalg::blade_gram_distinct("; "linalg dispatch: gram(A, B)" ],
       [ "cblas_"; "#include <cblas.h>"; "blade_gram_same" ])
      // matmul — the first-class intrinsic. `__math_matmul` must NOT survive
      // into the output (it is a pre-inference marker), and no synthesized
      // `__math_<n>` triple-loop function may be generated for it either.
      ("matmul_routes_to_gemm_adapter",
       "import math as m\n" +
       "let A: Array<Float64 like Idx<2>, Idx<3>> = [[1.0, 2.0, 3.0], [4.0, 5.0, 6.0]]\n" +
       "let B: Array<Float64 like Idx<3>, Idx<2>> = [[7.0, 8.0], [9.0, 10.0], [11.0, 12.0]]\n" +
       "let C = m.matmul(A, B)\n",
       [ shimInclude; "blade_linalg::blade_matmul("; "linalg dispatch: matmul(A, B)" ],
       [ "cblas_"; "#include <cblas.h>"; "__math_matmul"; "double __math_1" ])
      // COMPLEX gram keeps the scalar loops: the shim's v1 domain is real f64
      // (dsyrk/dgemm), exactly the restriction the pre-shim BLAS lowering had.
      // A complex program must therefore name neither the header nor a route.
      ("complex_gram_stays_on_scalar_loops",
       "let A: Array<Complex128 like Idx<2>, Idx<2>> = [[complex(1.0, 0.0), complex(2.0, 1.0)], [complex(3.0, 0.0), complex(4.0, -1.0)]]\n" +
       "let G = gram(A, A)\n",
       [ "conj_scalar" ],
       [ shimInclude; "blade_linalg::"; "cblas_" ])
      // NO GRATUITOUS DEPENDENCY SURFACE. A program with no linalg route must
      // not name the header at all — the include is collector-driven, not
      // unconditional, even though the file itself is deployed unconditionally.
      ("no_linalg_program_excludes_the_header",
       "let A = [1.0, 2.0, 3.0]\nlet s = reduce(A, (+))\n",
       [],
       [ shimInclude; "blade_linalg::"; "cblas_" ]) ]

let runLinAlgEmissionTests () : BlockResult =
    printHeader "LinAlg Dispatch Emission"
    let mutable passed = 0
    let mutable failed = 0
    let mutable failedNames = []
    let fail name detail =
        failed <- failed + 1
        failedNames <- failedNames @ [name]
        resultLine Fail name detail
    for (name, src, mustContain, mustNotContain) in emissionCases do
        match cppOf name src with
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
                resultLine Pass name "routing as expected"

    // ---- the policy table is the documented contract, so pin it ----
    // A row moving from Native to ViaShim (or vanishing) is a deliberate
    // decision that should require editing a test, not a silent drift.
    let policyCases =
        [ "gemm_via_shim", LinAlgPatterns.Gemm, LinAlgPatterns.ViaShim
          "syrk_via_shim", LinAlgPatterns.Syrk, LinAlgPatterns.ViaShim
          "dot_via_shim",  LinAlgPatterns.Dot,  LinAlgPatterns.ViaShim
          "gemv_via_shim", LinAlgPatterns.Gemv, LinAlgPatterns.ViaShim
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
