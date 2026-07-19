// Differential-vs-oracle harness (plan Phase 4): run corpus programs
// through TWO compiler binaries — this one and a PINNED oracle build — and
// require byte-identical printed values (not merely identical pass/fail).
// This is the plan's central structural idea made into tooling: the v7
// prototype's validated behavior is the ground truth the evolving compiler
// is diffed against, value by value.
//
// Pinning an oracle: copy a fully-gated build to ./oracle, e.g.
//   Copy-Item bin\Release\net7.0 oracle -Recurse
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
let denseSlice = [ "basic"; "loops"; "guards"; "for-in" ]

/// Corrected-semantics slice: corpus tests whose values INTENTIONALLY
/// diverge from the pinned oracle after a semantics correction — the
/// mechanism the plan calls for ("the differential test for that slice
/// should assert disagreement"). Divergence for a listed name CONFIRMS the
/// correction; agreement means the old unsound path did not fire for that
/// shape. Ground truth is the hand-computed EXPECT values in the corpus,
/// never the oracle.
///
/// Was empty since the 2026-07-12 re-pin: the arc-1 joint-product-symmetry
/// names (symmetry/012–016, functions/001) served against the pre-arc-1
/// pin and were retired when the oracle advanced past the correction —
/// those semantics are now IN the oracle and pinned by corpus EXPECTs.
/// Repopulated 2026-07-16 for the signed-iteration-variable fix: virtual-
/// source kernel params (range/lo..hi/reverse) and for-in loop vars were
/// bound size_t in the emitted C++, wrapping negative intermediates
/// unsigned before Float64 promotion and in signed division (loops/066).
/// Oracles pinned before the fix reproduce the wrap. Retire at the next
/// re-pin.
let correctedSlice : Set<string> =
    Set.ofList
        [ "Negative Int64 Mixed Arithmetic"
          // Pinned oracle predates the merged-nest fusion fix: it reads the
          // FIRST leaf's array for every kernel, so v = [2, 4, 6] instead of
          // the correct [20, 40, 60] (the test's own EXPECT pins the fix).
          // Retire at the next re-pin.
          "Fusion Different Arrays"
          // Pinned oracle predates the mut-array copy-semantics fix: an
          // assignable binding initialized from an existing array (`let mut
          // a = Z`) used to ALIAS Z's storage, so the oracle prints the
          // corrupted Z (and stale U/z reads). Divergence confirms the
          // fix; corpus EXPECTs are ground truth. Retire at the next re-pin.
          "Mut Array Copy Semantics"
          "Mut Array Copy In Function Body" ]

/// Run `<exe> run <srcFile>` and capture stdout. The generous timeout covers
/// the g++ compile that `blade run` performs internally.
let private runBlade (exePath: string) (srcFile: string) : Result<string, string> =
    try
        let psi = ProcessStartInfo(exePath, sprintf "run \"%s\"" srcFile)
        psi.RedirectStandardOutput <- true
        psi.RedirectStandardError <- true
        psi.UseShellExecute <- false
        psi.CreateNoWindow <- true
        use proc = Process.Start(psi)
        let outT = proc.StandardOutput.ReadToEndAsync()
        let errT = proc.StandardError.ReadToEndAsync()
        if not (proc.WaitForExit(180000)) then
            (try proc.Kill() with _ -> ())
            Error "timed out (>180s)"
        elif proc.ExitCode <> 0 then
            Error (sprintf "exit %d: %s" proc.ExitCode (errT.Result.Trim()))
        else Ok outT.Result
    with ex -> Error ex.Message

/// Value lines only: timing lines vary run to run; line endings normalize.
/// Heap pointer addresses are masked: the struct printer currently renders an
/// array-typed field as its raw pointer (e.g. `samples: 0x1f3f4a878b0`), which
/// differs on EVERY run — without masking, structs/013 flakes against any
/// oracle. (Printing something useful instead of a pointer is a separate
/// printer backlog item.)
let private normalize (s: string) : string =
    s.Replace("\r\n", "\n").Split('\n')
    |> Array.filter (fun l -> not (l.Contains "completed in"))
    |> Array.map (fun l ->
        System.Text.RegularExpressions.Regex.Replace(l.TrimEnd(), "0x[0-9a-fA-F]+", "0xPTR"))
    |> String.concat "\n"
    |> fun t -> t.Trim()

let runDiffOracleTests (oracleExe: string) (categories: string list) : BlockResult =
    printHeader "Differential vs Pinned Oracle"
    let blockName = "Diff Oracle"
    if not (File.Exists oracleExe) then
        printfn "Skipped: no pinned oracle at %s" (Path.GetFullPath oracleExe)
        printfn "         Pin one from a fully-gated build:  Copy-Item bin\\Release\\net7.0 oracle -Recurse"
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
            printSubHeader (sprintf "category: %s" cat)
            for (name, source) in category cat do
                if name.EndsWith "(rejects)" then
                    skipped <- skipped + 1   // reject-probes have no values to diff
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
                        resultLine Fail name (sprintf "current binary failed: %s" e)
                    | _, Error e ->
                        // Oracle can't run it (e.g. feature added after pinning):
                        // that's a skip with a note, not a divergence.
                        skipped <- skipped + 1
                        resultLine Skip name (sprintf "oracle failed: %s" e)
        printFooter blockName
            [ sprintf "%d passed" passed; sprintf "%d failed" failed; sprintf "%d skipped" skipped ]
        { Block = blockName; Passed = passed; Failed = failed; Skipped = skipped; FailedNames = failedNames }
