// Test sources live on disk in tests/corpus (audit §2.3 / Phase 0.1: the
// corpus doubles as the differential oracle for the rewrite). This module
// only names the categories; edit the .blade files to change tests.
module Blade.Tests.Functions

open Blade.Tests.Corpus
open Blade.Tests.TestHarness

/// Functions and captures
let functionTests = category "functions"

// ============================================================================
// Factory flat emission (pure codegen string checks; no toolchain, always run)
// ============================================================================
//
// The chained factory sugar (`plot(x)(20 : levels)(3 : cmap)`) and by-nominal
// argument routing elaborate at the SURFACE level, before typing — so after
// elaboration there must be EXACTLY one call node: no intermediate partial
// applications in the IR, no std::function residue in the emitted C++, and a
// chain must emit byte-identically to the flat spelling it is sugar for.
// Corpus tests verify the VALUES; these pin the EMISSION SHAPE, which a
// value check cannot see (a materialized-then-invoked partial application
// computes the same numbers).

/// Count non-overlapping occurrences of `needle` in `hay`.
let private countOccurrences (hay: string) (needle: string) : int =
    let mutable i = hay.IndexOf needle
    let mutable n = 0
    while i >= 0 do
        n <- n + 1
        i <- hay.IndexOf(needle, i + needle.Length)
    n

/// Source -> generated C++ (same lower+codegen path OmpTests pins pragmas
/// through). The captured-diagnostics form so a stray warning cannot leak
/// unattributed into the suite output.
let private cppOf (name: string) (src: string) : Result<string, string> =
    try
        match fst (Blade.Lowering.lowerCaptured src) with
        | Error e -> Error (sprintf "lower: %s" e)
        | Ok ir -> Ok (fst (Blade.CodeGen.genSelfContainedProgramFromIR ir name))
    with ex -> Error (sprintf "codegen raised: %s" ex.Message)

let runFactoryFlattenTests () : BlockResult =
    printHeader "Factory Flat Emission"
    let mutable passed = 0
    let mutable failed = 0
    let mutable failedNames : string list = []
    let ok name detail =
        passed <- passed + 1
        resultLine Pass name detail
    let fail name detail =
        failed <- failed + 1
        failedNames <- failedNames @ [name]
        resultLine Fail name detail
    let check name cond detail =
        if cond then ok name detail else fail name detail
    let header =
        "Unit levels: 1\n\
         Unit cmap: 1\n\
         function plot(x: Float, n: Float<levels> = 10.0, c: Float<cmap> = 0.0) -> Float = x + n * 100.0 + c * 7.0\n"
    let flat    = header + "let d = plot(1.0, 2.0: levels, 3.0: cmap)\n"
    let chained = header + "let d = plot(1.0)(2.0: levels)(3.0: cmap)\n"
    let swapped = header + "let d = plot(1.0)(3.0: cmap)(2.0: levels)\n"
    // One program name for all three so the generated text can be compared
    // byte for byte.
    match cppOf "factory_flat" flat, cppOf "factory_flat" chained, cppOf "factory_flat" swapped with
    | Ok cf, Ok cc, Ok cs ->
        check "chained call emits byte-identical C++ to the flat call" (cc = cf) ""
        check "swapped-order chain emits byte-identical C++ too" (cs = cf) ""
        // Exactly one CALL: "plot(" appears three times — prototype,
        // definition, the single call in main. A materialized partial
        // application would add call sites (or std::function wrappers).
        check "exactly one plot(...) call in the emitted C++"
            (countOccurrences cf "plot(" = 3)
            (sprintf "%d occurrences (prototype + definition + 1 call = 3)" (countOccurrences cf "plot("))
        check "no std::function residue (no materialized partial application)"
            (not (cc.Contains "std::function"))
            ""
        check "no eta-expansion residue (__pa wrapper params)"
            (not (cc.Contains "__pa"))
            ""
    | a, b, c ->
        let describe = function Ok _ -> "ok" | Error e -> e
        fail "factory emission sources lower + generate"
            (sprintf "flat: %s; chained: %s; swapped: %s" (describe a) (describe b) (describe c))
    { Block = "Factory Flat Emission"
      Passed = passed
      Failed = failed
      Skipped = 0
      FailedNames = failedNames }
