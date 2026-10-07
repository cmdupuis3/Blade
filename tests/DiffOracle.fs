// Differential-vs-oracle harness (plan Phase 4): run corpus programs
// through TWO compiler binaries — this one and a PINNED oracle build — and
// require byte-identical printed values (not merely identical pass/fail).
// This is the plan's central structural idea made into tooling: the v7
// prototype's validated behavior is the ground truth the evolving compiler
// is diffed against, value by value.
//
// Pinning an oracle: copy a fully-gated build to ./oracle, e.g.
//   Copy-Item bin\Release\net10.0 oracle -Recurse
// The harness skips cleanly (with that hint) when no oracle is pinned.
// Deliberately NOT part of the default suite: it g++-compiles every test
// twice. Run standalone: `blade test diff-oracle [category]`.
module Blade.Tests.DiffOracle

open System
open System.IO
open System.Diagnostics
open Blade.Build
open Blade.Tests.TestHarness
open Blade.Tests.Corpus

/// Phase 4's dense slice: literals, scalar bindings, dense method_for,
/// compute, printing. Categories grow as later phases claim more surface.
/// (The 2026-07-21 re-pin absorbed recursive-arrays and stack-join: the
/// oracle now parses `let rec` and compiles stack/join, so their former
/// capability-skips diff for real.) The 2026-10-07 re-pin (master eabafca6)
/// absorbed 8accf580's printer change -- a Float64 prints with its decimal
/// point (`2.0`, not `2`) in every lane -- which diverged every float-printing
/// program (204 of 243 compared against the 2026-08-23 pin). The mask that
/// would have hidden it was deliberately NOT added to `normalize`: a printer
/// change is a behaviour change, and the oracle is re-pinned for it. The seven
/// divergences that were not the decimal point were corrections already pinned
/// by corpus EXPECTs (Int64 literals exact past 2^53, `-x^2` = `-(x^2)`, match
/// tuple-guard binding, grouped row-map literal kernels, named-kernel capture
/// hygiene, rec-array prefix-alias zero history) or a corpus program that
/// changed since the pin (loops "Pipe Compute After If Kernel").
let denseSlice = [ "basic"; "loops"; "guards"; "recursive-arrays"; "stack-join" ]

/// Corrected-semantics slice: corpus tests whose values INTENTIONALLY
/// diverge from the pinned oracle after a semantics correction — the
/// mechanism the plan calls for ("the differential test for that slice
/// should assert disagreement"). Divergence for a listed name CONFIRMS the
/// correction; agreement means the old unsound path did not fire for that
/// shape. Ground truth is the hand-computed EXPECT values in the corpus,
/// never the oracle.
///
/// EMPTY since the 2026-07-21 re-pin (post imperative-removal arc). The
/// three entries that served against the 2026-07-12 pin — "Fusion
/// Different Arrays" (merged-nest fusion fix) and the two "Mut Array
/// Copy" names (deep-copy semantics fix) — were retired with it: those
/// corrections are now IN the oracle and pinned by corpus EXPECTs, so the
/// divergences they asserted can no longer occur. (History: the arc-1
/// joint-product-symmetry names retired at the 2026-07-12 pin; the
/// signed-iteration loops/066 entry retired mid-arc when the file moved
/// to `let rec` and capability-skipped the old oracle.)
let correctedSlice : Set<string> = Set.empty

/// Run `<exe> run <srcFile>` and capture stdout. The generous timeout covers
/// the g++ compile that `blade run` performs internally.
let private runBlade (exePath: string) (srcFile: string) : Result<string, string> =
    try
        let psi = ProcessStartInfo(exePath, $"run \"{srcFile}\"")
        psi.RedirectStandardOutput <- true
        psi.RedirectStandardError <- true
        psi.UseShellExecute <- false
        psi.CreateNoWindow <- true
        use proc = Process.Start(psi)
        let outT = Blade.Runtime.readToEndOffPool proc.StandardOutput
        let errT = Blade.Runtime.readToEndOffPool proc.StandardError
        if not (proc.WaitForExit(180000)) then
            (try proc.Kill() with _ -> ())
            Error "timed out (>180s)"
        elif proc.ExitCode <> 0 then
            Error ($"exit {proc.ExitCode}: {(errT.Result.Trim())}")
        else Ok outT.Result
    with ex -> Error ex.Message

/// Value lines only: timing lines vary run to run; line endings normalize.
/// Heap pointers are NOT masked: the mask existed because the struct printer
/// streamed an array-typed field as its raw address (`samples: 0x1f3f4a878b0`),
/// and it hid that bug. The printer prints the field's values now, and a
/// pointer in either binary's output is a difference this gate reports. (An
/// oracle pinned before that fix prints the address, so structs/013 differs
/// against it until the oracle is re-pinned -- a real behaviour change.)
let private normalize (s: string) : string =
    s.Replace("\r\n", "\n").Split('\n')
    |> Array.filter (fun l -> not (l.Contains "completed in"))
    |> Array.map (fun l -> l.TrimEnd())
    |> String.concat "\n"
    |> fun t -> t.Trim()

/// Byte-exact comparison needs both sides built with contraction off, even
/// though the user-facing default is `fast` (Build.fs): the pin covers the
/// in-process compile directly and reaches the oracle subprocess via env
/// inheritance (older pinned oracles default to off on their own). Restored
/// on exit.
let private pinFpContractOff () =
    let prior = System.Environment.GetEnvironmentVariable("BLADE_FP_CONTRACT")
    System.Environment.SetEnvironmentVariable("BLADE_FP_CONTRACT", "off")
    { new System.IDisposable with
        member _.Dispose() =
            System.Environment.SetEnvironmentVariable("BLADE_FP_CONTRACT", prior) }

let runDiffOracleTests (oracleExe: string) (categories: string list) : BlockResult =
    printHeader "Differential vs Pinned Oracle"
    let blockName = "Diff Oracle"
    use _fpPin = pinFpContractOff ()
    if not (File.Exists oracleExe) then
        printfn "Skipped: no pinned oracle at %s" (Path.GetFullPath oracleExe)
        printfn "         Pin one from a fully-gated build:  Copy-Item bin\\Release\\net10.0 oracle -Recurse"
        { Block = blockName; Passed = 0; Failed = 0; Skipped = 1; FailedNames = [] }
    elif not capabilities.Value.HasGpp then
        printfn "Skipped: requires g++."
        { Block = blockName; Passed = 0; Failed = 0; Skipped = 1; FailedNames = [] }
    else
        let thisExe = Environment.ProcessPath
        printfn "current: %s" thisExe
        printfn "oracle:  %s" (Path.GetFullPath oracleExe)
        let tmpRoot = Path.Combine(Path.GetTempPath(), "blade_diff_oracle")
        let mineDir = Path.Combine(tmpRoot, "mine")
        let theirsDir = Path.Combine(tmpRoot, "theirs")
        (try Directory.Delete(tmpRoot, true) with _ -> ())
        Directory.CreateDirectory(mineDir) |> ignore
        Directory.CreateDirectory(theirsDir) |> ignore
        let mutable passed = 0
        let mutable failed = 0
        let mutable skipped = 0
        let mutable failedNames : string list = []
        for cat in categories do
            printSubHeader ($"category: {cat}")
            for (name, source) in category cat do
                if name.EndsWith "(rejects)" || name.EndsWith "(aborts)" then
                    // Neither shape has values to diff: a reject-probe never
                    // reaches codegen, and an `(aborts)` test terminates non-zero BY
                    // DESIGN (its `// ABORT:` pin is what the main harness checks),
                    // which `runBlade` cannot distinguish from a real failure.
                    skipped <- skipped + 1
                else
                    let safe = sanitizeFileName name + ".blade"
                    let mineSrc = Path.Combine(mineDir, safe)
                    let theirsSrc = Path.Combine(theirsDir, safe)
                    File.WriteAllText(mineSrc, source)
                    File.WriteAllText(theirsSrc, source)
                    match runBlade thisExe mineSrc, runBlade oracleExe theirsSrc with
                    | Ok mine, Ok theirs when normalize mine = normalize theirs ->
                        passed <- passed + 1
                        if Set.contains name correctedSlice then
                            resultLine Pass name "oracle agrees (old unsound path did not fire for this shape)"
                        else
                            resultLine Pass name "values identical"
                    | Ok mine, Ok theirs when Set.contains name correctedSlice ->
                        // Corrected-semantics slice: this divergence is the point.
                        passed <- passed + 1
                        resultLine Pass name "INTENTIONAL divergence from pinned oracle confirmed (corrected semantics; corpus EXPECTs are ground truth)"
                    | Ok mine, Ok theirs ->
                        failed <- failed + 1
                        failedNames <- failedNames @ [name]
                        resultLine Fail name "VALUES DIVERGE from oracle"
                        printfn "    current: %s" ((normalize mine).Split('\n') |> Array.truncate 3 |> String.concat " | ")
                        printfn "    oracle:  %s" ((normalize theirs).Split('\n') |> Array.truncate 3 |> String.concat " | ")
                    | Error e, _ ->
                        failed <- failed + 1
                        failedNames <- failedNames @ [name]
                        resultLine Fail name ($"current binary failed: {e}")
                    | _, Error e ->
                        // Oracle can't run it (e.g. feature added after pinning):
                        // that's a skip with a note, not a divergence.
                        skipped <- skipped + 1
                        resultLine Skip name ($"oracle failed: {e}")
        printFooter blockName
            [ $"{passed} passed"; $"{failed} failed"; $"{skipped} skipped" ]
        { Block = blockName; Passed = passed; Failed = failed; Skipped = skipped; FailedNames = failedNames }
