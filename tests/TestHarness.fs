module Blade.Tests.TestHarness

// ============================================================================
// Shared test-harness output helpers.
//
// Centralizes the banner/divider formatting that every test block prints, so
// the suite has ONE header style and ONE footer style. Previously each block
// hand-rolled its own dividers (70-char rules, stray 41-char rules, bare
// `=== Title ===` lines) and its own footer phrasing (`Tests:` vs `TESTS:`
// vs `COVERAGE:` vs `TYPE:`), which made the combined run output look like it
// came from several different tools.
//
// Design: format is uniform; the METRIC each block reports is not forced into
// one mold. A block hands `printFooter` its already-formatted metric parts
// (e.g. ["32/32 passed"] or ["0 failure(s)"] or ["PASS"]); the helper supplies
// the consistent label, divider, and spacing around them. This preserves each
// block's native accounting (failures-only for the C++ blocks, passed/failed
// for the F# unit blocks, a verdict for differential) while unifying the look.
//
// This module is compiled before the Test_*.fs modules and Main.fs, so both
// the standalone `blade test <name>` paths and the full-suite runner share it.
// ============================================================================

/// Width of the major divider rule. One constant so headers and footers agree.
let ruleWidth = 70

/// Version string is injected by Main at startup (the test modules don't own
/// it). Defaults to empty; Main sets it once before any block runs.
let mutable version : string = ""

// ============================================================================
// Timings (docs/plans/plan-test-tiers.md, P0)
// ============================================================================
//
// The harness printed no wall time, so where a run spent its time had to be
// reverse-engineered from an outer script's timestamps. This records it:
//
//   - SECTIONS. Every `printHeader` call marks the start of a section; a
//     section ends where the next one starts. Blocks run in sequence, so the
//     sections partition the run. No block had to change to be timed.
//   - TESTS. A block may record a test's own wall time (the corpus runner
//     does). Those run in parallel, so they sum to more than the section.
//   - PHASES. Seconds summed over every front-end pass a block reports, plus
//     Build.CompileStats' g++ / executable-cache / program-run counters.
//
// `report` prints the table after the run and, given a path, writes all of it
// as JSON (`blade test ... --timings <path>`). Reporting only: nothing here
// feeds a verdict.
module Timings =
    let private clock = System.Diagnostics.Stopwatch.StartNew ()
    let private gate = obj ()
    let private sections = System.Collections.Generic.List<string * System.TimeSpan> ()
    let private tests = System.Collections.Generic.List<string * string * float> ()
    let mutable private frontEndPasses = 0
    let mutable private frontEndTicks = 0L

    let markSection (title: string) : unit =
        lock gate (fun () -> sections.Add((title, clock.Elapsed)))

    /// One test's wall time, filed under the section open when it finished.
    let recordTest (name: string) (seconds: float) : unit =
        lock gate (fun () ->
            let section = if sections.Count = 0 then "" else fst sections.[sections.Count - 1]
            tests.Add((section, name, seconds)))

    /// One front-end pass (parse through C++ generation) took `elapsed`.
    let recordFrontEnd (elapsed: System.TimeSpan) : unit =
        lock gate (fun () ->
            frontEndPasses <- frontEndPasses + 1
            frontEndTicks <- frontEndTicks + elapsed.Ticks)

    let private inv = System.Globalization.CultureInfo.InvariantCulture

    let private jsonString (s: string) : string =
        let sb = System.Text.StringBuilder()
        sb.Append('"') |> ignore
        for c in s do
            match c with
            | '"' -> sb.Append("\\\"") |> ignore
            | '\\' -> sb.Append("\\\\") |> ignore
            | c when c < ' ' -> sb.Append("\\u").Append((int c).ToString("x4")) |> ignore
            | c -> sb.Append(c) |> ignore
        sb.Append('"').ToString()

    let private num (x: float) : string = x.ToString("0.000", inv)

    /// Print the timing table; write the JSON record when `jsonPath` is given.
    /// Sections shorter than a second are folded into one line of the table
    /// (the JSON lists every one).
    let report (jsonPath: string option) : unit =
        let total = clock.Elapsed
        let (marks, testRows, fePasses, feSeconds) =
            lock gate (fun () ->
                (sections.ToArray(), tests.ToArray(), frontEndPasses,
                 System.TimeSpan.FromTicks(frontEndTicks).TotalSeconds))
        let spans =
            marks
            |> Array.mapi (fun i (title, start) ->
                let stop = if i + 1 < marks.Length then snd marks.[i + 1] else total
                (title, start.TotalSeconds, (stop - start).TotalSeconds))
        let (hits, gppRuns, gppSeconds, runs, runSeconds) = Blade.Build.CompileStats.snapshot ()
        let rule = String.replicate ruleWidth "="
        printfn "\n%s" rule
        printfn "  Timings (wall seconds)"
        printfn "%s\n" rule
        let shown = spans |> Array.filter (fun (_, _, d) -> d >= 1.0)
        let width = if shown.Length = 0 then 0 else shown |> Array.map (fun (t, _, _) -> t.Length) |> Array.max
        for (title, _, d) in shown do
            printfn "  %s  %9s" (title.PadRight width) (num d)
        let small = spans |> Array.filter (fun (_, _, d) -> d < 1.0)
        if small.Length > 0 then
            printfn "  (%d section(s) under one second: %s in all)" small.Length (num (small |> Array.sumBy (fun (_, _, d) -> d)))
        printfn "%s" (String.replicate ruleWidth "-")
        printfn "  Run: %s s" (num total.TotalSeconds)
        if gppRuns + hits + runs + fePasses > 0 then
            printfn "  g++: %d compile(s), %s s summed; executable cache: %d hit(s)" gppRuns (num gppSeconds) hits
            printfn "  Programs run: %d, %s s summed; front-end passes: %d, %s s summed" runs (num runSeconds) fePasses (num feSeconds)
        let (pchUses, pchBuilds, pchBuildSeconds, pchRetries) = Blade.Build.CompileStats.pchSnapshot ()
        if pchUses + pchBuilds + pchRetries > 0 then
            printfn "  Precompiled prelude: used by %d compile(s); %d built, %s s; %d compile(s) re-run without it"
                pchUses pchBuilds (num pchBuildSeconds) pchRetries
        match jsonPath with
        | None -> ()
        | Some path ->
            try
                let sb = System.Text.StringBuilder()
                sb.Append("{\n  \"version\": 1,\n") |> ignore
                sb.Append("  \"total_seconds\": ").Append(num total.TotalSeconds).Append(",\n") |> ignore
                sb.Append("  \"compile\": { \"gpp_runs\": ").Append(gppRuns)
                  .Append(", \"gpp_seconds\": ").Append(num gppSeconds)
                  .Append(", \"cache_hits\": ").Append(hits)
                  .Append(", \"program_runs\": ").Append(runs)
                  .Append(", \"program_seconds\": ").Append(num runSeconds)
                  .Append(", \"front_end_passes\": ").Append(fePasses)
                  .Append(", \"front_end_seconds\": ").Append(num feSeconds)
                  .Append(", \"pch_uses\": ").Append(pchUses)
                  .Append(", \"pch_builds\": ").Append(pchBuilds)
                  .Append(", \"pch_build_seconds\": ").Append(num pchBuildSeconds)
                  .Append(", \"pch_retries\": ").Append(pchRetries).Append(" },\n") |> ignore
                sb.Append("  \"sections\": [") |> ignore
                spans |> Array.iteri (fun i (title, start, d) ->
                    sb.Append(if i = 0 then "\n" else ",\n")
                      .Append("    { \"title\": ").Append(jsonString title)
                      .Append(", \"start\": ").Append(num start)
                      .Append(", \"seconds\": ").Append(num d).Append(" }") |> ignore)
                sb.Append("\n  ],\n  \"tests\": [") |> ignore
                testRows |> Array.iteri (fun i (section, name, d) ->
                    sb.Append(if i = 0 then "\n" else ",\n")
                      .Append("    { \"section\": ").Append(jsonString section)
                      .Append(", \"name\": ").Append(jsonString name)
                      .Append(", \"seconds\": ").Append(num d).Append(" }") |> ignore)
                sb.Append("\n  ]\n}\n") |> ignore
                System.IO.File.WriteAllText(path, sb.ToString())
                printfn "  Timings written to %s" path
            with ex ->
                eprintfn "test: could not write the timings file %s: %s" path ex.Message

/// Major section header: blank line, full rule, indented title (+ version if
/// set), full rule, blank line. This is the canonical block header.
let printHeader (title: string) =
    Timings.markSection title
    let rule = String.replicate ruleWidth "="
    printfn "\n%s" rule
    if version <> "" then printfn "  %s (v%s)" title version
    else printfn "  %s" title
    printfn "%s\n" rule

/// Minor sub-header used within a block for an individual named case.
let printSubHeader (title: string) =
    printfn "\n--- %s ---\n" title

/// Locate `name = [...]` in a program's stdout and parse its values as a
/// FLAT float array, accepting BOTH printer shapes: the flat run
/// (`x = [1, 2, 3, 4]` -- rank 1, rank 3, wreath pools) and the nested
/// rank-2 form (`x = [[1, 2], [3, 4]]` -- genPrintNested2, every rank-2
/// array since 7ac4d3a, packed groups included). Interior brackets carry
/// only row boundaries, so dropping them yields the row-major / canonical-
/// pool-order value sequence either way, which is exactly what the e2e
/// blocks compare against their oracles.
///
/// This exists because the provider gates used to inline
/// `inner.Split(',') |> Array.map Double.Parse` at each site: correct for a
/// flat line, but against a nested one the first token is `[0`, so the
/// whole test died in Double.Parse ("The input string '[0' was not in a
/// correct format") without comparing a single value. Six zarr e2e tests
/// rotted that way when the rank-2 printer went nested. One shared parser,
/// shape-tolerant like the corpus validator's ExpectedArray2D, so a future
/// printer-shape change cannot take the gates down again.
///
/// Returns None when no such line exists or any token fails to parse --
/// callers report both as the check's failure detail.
let tryParsePrintedFloats (name: string) (stdout: string) : float[] option =
    let prefix = name + " = ["
    stdout.Split('\n')
    |> Array.tryPick (fun l ->
        let l = l.Trim()
        if l.StartsWith prefix && l.EndsWith "]" then
            let inner = l.Substring(prefix.Length, l.Length - prefix.Length - 1)
            let toks =
                inner.Replace("[", "").Replace("]", "").Split(',')
                |> Array.map (_.Trim())
                |> Array.filter (fun s -> s <> "")
            let parsed =
                toks |> Array.map (fun s ->
                    match System.Double.TryParse(s, System.Globalization.NumberStyles.Float,
                                                 System.Globalization.CultureInfo.InvariantCulture) with
                    | true, v -> Some v
                    | _ -> None)
            if parsed |> Array.forall Option.isSome
            then Some (parsed |> Array.map Option.get)
            else None
        else None)

/// Standardized block footer. `label` names the block; `parts` are the block's
/// own pre-formatted metric strings, joined with ", ". Renders as:
///
///     <rule>
///     <label>: <part1>, <part2>
///
/// Examples:
///   printFooter "Alloc Layout"    ["32/32 passed"]
///   printFooter "CUDA Kernel"     ["0 failure(s)"]
///   printFooter "OpenMP Coverage" ["0 error(s)"; "0 warning(s)"]
///   printFooter "Differential Symmetry" ["PASS"]
let printFooter (label: string) (parts: string list) =
    let rule = String.replicate ruleWidth "-"
    printfn "%s" rule
    printfn "%s: %s" label (String.concat ", " parts)

// ============================================================================
// Per-test result lines
// ============================================================================
//
// Every per-test line, in every block, has the same shape:
//
//     [PASS]: <name>
//     [PASS]: <name> -- <detail>
//     [FAIL]: <name> -- <detail>
//     [SKIP]: <name> -- <detail>
//
// The detail is block-dependent (a stage list, a cardinality, an error
// message); it is omitted entirely when empty, so a plain pass is just
// "[PASS]: name". Passing lines are one-liners by construction (#3): the
// detail, when present, stays on the same line.

/// Outcome of a single test. Skip is distinct from pass/fail so the grand
/// total can report it separately (toolchain/GPU unavailable, etc.).
type Outcome =
    | Pass
    | Fail
    | Skip

/// Render a single per-test line. `detail` "" means no trailing detail.
/// Uses " -- " (ASCII) as the separator to avoid any console-encoding risk
/// with an em-dash on Windows code pages.
let resultLine (outcome: Outcome) (name: string) (detail: string) =
    let tag = match outcome with Pass -> "PASS" | Fail -> "FAIL" | Skip -> "SKIP"
    if detail = "" then printfn "  [%s]: %s" tag name
    else printfn "  [%s]: %s -- %s" tag name detail

// ============================================================================
// Block results + grand-total report
// ============================================================================
//
// Each block returns a BlockResult instead of a bare int/bool. Main collects
// them into one list and prints a single roll-up at the very end: one line per
// block, a grand total, and the names of any failed tests. Returning structure
// (rather than mutating a shared collector) keeps this race-free under the
// parallel test runner.

type BlockResult =
    { Block: string
      Passed: int
      Failed: int
      Skipped: int
      FailedNames: string list }

/// How many failed-test names the grand-total roll-up lists before summarizing
/// the rest as a count. One constant so the cap is tunable in one place.
let failedRollUpCap = 40

/// Print the final grand-total report across all blocks.
let printGrandTotal (blocks: BlockResult list) =
    let rule = String.replicate ruleWidth "="
    printfn "\n%s" rule
    if version <> "" then printfn "  Grand Total (v%s)" version
    else printfn "  Grand Total"
    printfn "%s\n" rule
    // Per-block lines, aligned on the block name.
    let nameWidth =
        blocks |> List.map (_.Block.Length) |> (fun ls -> if ls.IsEmpty then 0 else List.max ls)
    for b in blocks do
        let skipNote = if b.Skipped > 0 then $", {b.Skipped} skipped" else ""
        printfn "  %s  %d passed, %d failed%s" (b.Block.PadRight nameWidth) b.Passed b.Failed skipNote
    let totalPassed  = blocks |> List.sumBy (_.Passed)
    let totalFailed  = blocks |> List.sumBy (_.Failed)
    let totalSkipped = blocks |> List.sumBy (_.Skipped)
    printfn "%s" (String.replicate ruleWidth "-")
    let skipTotal = if totalSkipped > 0 then $", {totalSkipped} skipped" else ""
    printfn "  TOTAL: %d passed, %d failed%s" totalPassed totalFailed skipTotal
    // Failed-test roll-up. Capped: this is an INDEX into the per-block output
    // above (every failure already printed its own "[FAIL]: ..." line with the
    // detail), not the report of record. A broad regression can fail hundreds
    // of corpus tests at once, and an unbounded list scrolls the TOTAL line —
    // the one line a reader checks — off the screen, which is exactly how a red
    // run gets misread as green. Show the first `failedRollUpCap` and say how
    // many more there are.
    let allFailed = blocks |> List.collect (fun b -> b.FailedNames |> List.map (fun n -> b.Block, n))
    if not allFailed.IsEmpty then
        printfn "\n  Failed tests:"
        for (blk, nm) in allFailed |> List.truncate failedRollUpCap do
            printfn "    [%s] %s" blk nm
        let hidden = allFailed.Length - failedRollUpCap
        if hidden > 0 then
            printfn "    ... and %d more (see the per-block [FAIL] lines above)" hidden

// ----------------------------------------------------------------------------
// OpenMP construct pins.
//
// Generated programs spell every OpenMP loop construct through the
// blade_portability.hpp macros (`BLADE_OMP_PARALLEL_FOR_COLLAPSE(2)`, ...):
// the header picks the pragma, the emitter never does. Emission-shape pins
// keep the CANONICAL construct text (`#pragma omp parallel for collapse(2)`)
// because that is what they mean; `ompConstructView` renders the emitted
// macro invocations back to it, and `hasOmpPin` is `Contains` over the raw
// text OR that view -- so a positive pin matches either spelling and a
// negative one ("must not contain `#pragma omp`") still sees every construct.
// The Path-B folds' explicit team, `BLADE_OMP_PARALLEL_NUM_THREADS(n)`,
// renders as `#pragma omp parallel num_threads(n)` the same way.
// The fp-reassoc `BLADE_OMP_SIMD_REDUCTION(spec)` accumulation form is left
// as it is: it was a macro before the routing and its pins name the macro.
// ----------------------------------------------------------------------------

let private ompMacroRx =
    System.Text.RegularExpressions.Regex(
        @"\bBLADE_OMP_(?:PARALLEL_NUM_THREADS\((?<nt>[^)]*)\)|PARALLEL_FOR_SIMD_REDUCTION\((?<red>[^)]*)\)|PARALLEL_FOR_COLLAPSE\((?<n>\d+)\)|(?<k>PARALLEL_FOR_DYNAMIC|PARALLEL_FOR_SIMD|PARALLEL_FOR|SIMD)\b)")

/// The emitted text with each BLADE_OMP_* loop-construct macro replaced by
/// the `#pragma omp ...` line it expands to under OpenMP 4.0+.
let ompConstructView (cpp: string) : string =
    ompMacroRx.Replace(cpp, fun (m: System.Text.RegularExpressions.Match) ->
        let red = m.Groups.["red"]
        let n = m.Groups.["n"]
        let nt = m.Groups.["nt"]
        if nt.Success then $"#pragma omp parallel num_threads({nt.Value})"
        elif red.Success then $"#pragma omp parallel for simd reduction({red.Value})"
        elif n.Success then $"#pragma omp parallel for collapse({n.Value})"
        else
            match m.Groups.["k"].Value with
            | "PARALLEL_FOR_DYNAMIC" -> "#pragma omp parallel for schedule(dynamic)"
            | "PARALLEL_FOR_SIMD" -> "#pragma omp parallel for simd"
            | "PARALLEL_FOR" -> "#pragma omp parallel for"
            | _ -> "#pragma omp simd")

/// `cpp` carries `pin`, in its own spelling or in the construct view.
let hasOmpPin (cpp: string) (pin: string) : bool =
    cpp.Contains pin || (ompConstructView cpp).Contains pin
