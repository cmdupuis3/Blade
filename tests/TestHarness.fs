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

/// Major section header: blank line, full rule, indented title (+ version if
/// set), full rule, blank line. This is the canonical block header.
let printHeader (title: string) =
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
                |> Array.map (fun s -> s.Trim())
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
        blocks |> List.map (fun b -> b.Block.Length) |> (fun ls -> if ls.IsEmpty then 0 else List.max ls)
    for b in blocks do
        let skipNote = if b.Skipped > 0 then sprintf ", %d skipped" b.Skipped else ""
        printfn "  %s  %d passed, %d failed%s" (b.Block.PadRight nameWidth) b.Passed b.Failed skipNote
    let totalPassed  = blocks |> List.sumBy (fun b -> b.Passed)
    let totalFailed  = blocks |> List.sumBy (fun b -> b.Failed)
    let totalSkipped = blocks |> List.sumBy (fun b -> b.Skipped)
    printfn "%s" (String.replicate ruleWidth "-")
    let skipTotal = if totalSkipped > 0 then sprintf ", %d skipped" totalSkipped else ""
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
