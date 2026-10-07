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
///
/// Shared (not private) so other emission-shape blocks compiled after this one
/// -- Test_Sqlish's gather-elision pins -- reach the same path rather than
/// re-deriving it.
let cppOf (name: string) (src: string) : Result<string, string> =
    try
        match fst (Blade.Lowering.lowerCaptured src) with
        | Error e -> Error ($"lower: {e}")
        | Ok ir -> Ok (fst (Blade.CodeGen.genSelfContainedProgramFromIR ir name))
    with ex -> Error ($"codegen raised: {ex.Message}")

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
            ($"""{(countOccurrences cf "plot(")} occurrences (prototype + definition + 1 call = 3)""")
        check "no std::function residue (no materialized partial application)"
            (not (cc.Contains "std::function"))
            ""
        check "no eta-expansion residue (__pa wrapper params)"
            (not (cc.Contains "__pa"))
            ""
    | a, b, c ->
        let describe = function Ok _ -> "ok" | Error e -> e
        fail "factory emission sources lower + generate"
            ($"flat: {(describe a)}; chained: {(describe b)}; swapped: {(describe c)}")
    { Block = "Factory Flat Emission"
      Passed = passed
      Failed = failed
      Skipped = 0
      FailedNames = failedNames }

// ============================================================================
// Closure capture emission (pure codegen string checks; no toolchain)
// ============================================================================
//
// A closure VALUE copies its captures (`[=] ... mutable`) so it can outlive
// the frame that made it; a consumer-local kernel wrapper keeps `[&]`; a
// closure sharing a REBOUND binding keeps `[&]` (the type checker keeps it from
// escaping). The corpus pins the values (functions/330-340); only the emitted
// text shows which capture each site chose -- a `[&]` closure over a live
// frame prints the same numbers as a `[=]` one.
let runClosureCaptureEmissionTests () : BlockResult =
    printHeader "Closure Capture Emission"
    let mutable passed = 0
    let mutable failed = 0
    let mutable failedNames : string list = []
    let check name cond detail =
        if cond then
            passed <- passed + 1
            resultLine Pass name detail
        else
            failed <- failed + 1
            failedNames <- failedNames @ [name]
            resultLine Fail name detail
    let src =
        "function mk(i: Int64) = lambda(j: Int64) -> Float64(i * 10 + j)\n"
        + "function relay(i: Int64) = {\n"
        + "    let f = lambda(j: Int64) -> i + j\n"
        + "    f\n"
        + "}\n"
        + "function weighted(v: Array<Float64 like Idx<4>>, s: Float64) = reduce(v, lambda(a, b) -> a + b * s)\n"
        + "function tally(x: Float64) = {\n"
        + "    let mut seen = 0.0\n"
        + "    let bump = lambda(d: Float64) -> {\n"
        + "        seen = seen + d\n"
        + "        seen\n"
        + "    }\n"
        + "    bump(x) + bump(x)\n"
        + "}\n"
        + "let r = mk(2)(3)\n"
        + "let r2 = relay(4)(5)\n"
        + "let r3 = weighted([1.0, 2.0, 3.0, 4.0], 0.5)\n"
        + "let r4 = tally(1.5)\n"
    match cppOf "closure_capture" src with
    | Ok cpp ->
        let lines = cpp.Split('\n') |> Array.map (fun l -> l.Trim())
        let lineWith (needle: string) = lines |> Array.tryFind (fun l -> l.Contains needle)
        let show = function Some (l: string) -> l | None -> "<no such line>"
        // The returned closure: its own copy of `i`.
        let ret = lineWith "return __lambda_" |> Option.filter (fun l -> l.StartsWith "return [")
        check "a returned closure copies its captures ([=] ... mutable)"
            (match ret with Some l -> l.StartsWith "return [=](int64_t j) mutable {" | None -> false)
            (show ret)
        // The let-bound closure that is then returned: same rule.
        let letBound = lines |> Array.tryFind (fun l -> l.Contains "= [" && l.Contains "(int64_t j)" && not (l.StartsWith "return"))
        check "a let-bound closure value copies its captures"
            (match letBound with Some l -> l.Contains "= [=](int64_t j) mutable {" | None -> false)
            (show letBound)
        // The reduce kernel: consumer-local, by reference.
        let wrap = lineWith "auto __wrap_" |> Option.filter (fun l -> l.Contains ", s)")
        check "a non-escaping kernel wrapper keeps [&]"
            (match wrap with Some l -> l.Contains "= [&](double a, double b) { return __lambda_" | None -> false)
            (show (lineWith "auto __wrap_"))
        // The closure sharing the reassigned `seen`: by reference.
        let shared = lines |> Array.tryFind (fun l -> l.Contains "(double d)" && l.Contains "= [")
        check "a closure over a reassigned binding keeps [&]"
            (match shared with Some l -> l.Contains "= [&](double d) {" | None -> false)
            (show shared)
        check "no closure value captures by reference except the shared one"
            (lines |> Array.filter (fun l -> l.Contains "[&](" && l.Contains "return __lambda_" && not (l.Contains "__wrap_"))
                   |> Array.length = 1)
            ""
    | Error e ->
        check "closure capture source lowers + generates" false e
    { Block = "Closure Capture Emission"
      Passed = passed
      Failed = failed
      Skipped = 0
      FailedNames = failedNames }
