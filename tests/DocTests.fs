// The DOC-TEST lane: every ```blade block in the documentation is a program,
// and `blade test docs` holds it to what the page claims.
//
// Why. The docs are where users (and agents) learn the language, and they
// rotted exactly the way an untested corpus would: examples that no longer
// parsed, claims about printed values nobody ran, whole operators that were
// planned and never built. A fenced block that says `blade` now either
// compiles, or says why it does not.
//
// Sources: CLAUDE.md, docs/*.md, docs/features/*.md, docs/research/*.md, and
// docs/plans/**/*.md -- plans are design documents full of proposed syntax, so
// a plans block is tested only when its fence opts in with a flag (below).
//
// The fence's info string is `blade` followed by zero or more flags:
//
//   ```blade            CHECK: the block must lower (parse + typecheck +
//                       lowering + IR validation -- the corpus front end).
//                       If the block carries `// EXPECT: name = value` pins it
//                       is also RUN (C++ pipeline, g++) and its pins checked,
//                       under the corpus classifier (warning pins strict).
//   ```blade sketch     SKIPPED: illustrative only -- grammar schemata,
//                       metavariables, fragments that name data the page does
//                       not define, proposed syntax. Same as a first line
//                       `// sketch` (optionally `// sketch: <why>`).
//   ```blade rejects    must be REFUSED by the front end (a corpus
//                       "(rejects)" probe: `// ERROR: BLxxxx` pins the code,
//                       `// ERROR-CONTAINS:` a message substring).
//   ```blade prelude    checked like a plain block, and then PREPENDED to every
//                       later block of the same SECTION (the "examples below
//                       assume" setup: imports, index types, sample data).
//   ```blade cont       continues the section's running program: the source
//                       is the previous checked/run blocks' chain plus this
//                       block (tutorial sections that build one program step
//                       by step). A plain block starts a new chain.
//
// A SECTION ends at the next `## ` heading: the section's preludes and its
// `cont` chain reset there, so each section's examples declare what they use
// and a top-level name may be reused across sections (a second top-level
// `let` of one name in ONE program is BL2009). A prelude placed BEFORE the
// page's first `## ` heading is page-wide (an `import` every section needs).
//   ```blade check      (plans only) opt this block in.
//
// An unknown flag is a FAIL of its own: a typo like `sketh` must not silently
// turn a sketch into a checked block or the reverse.
//
// Verdicts: CHECK passes iff the program lowers. RUN / REJECTS go through the
// corpus classifier unchanged (Runner.classifyWithDetail), so a doc pin means
// exactly what a corpus pin means. A RUN block with no g++ SKIPs.
//
//   blade test docs            every page
//   blade test docs <substr>   only pages whose repo-relative path contains it
module Blade.Tests.DocTests

open System
open System.IO
open Blade
open Blade.Build
open Blade.Tests.TestHarness
open Blade.Tests.Expect
open Blade.Tests.Runner

type DocKind =
    | Check
    | Run
    | Rejects
    | Sketch

type DocBlock =
    { /// Repo-relative page path, forward slashes.
      Page: string
      /// 1-based line of the block's first body line.
      Line: int
      Kind: DocKind
      /// The assembled program (preludes + continued chain + the block).
      Source: string
      /// A flag problem (unknown flag, `cont` with nothing to continue).
      Problem: string option }

/// The repository root: the first of the working directory and the binary's
/// ancestors that holds docs/formalism.md and CLAUDE.md. The binary's
/// ancestors are what make the lane work from a private test CWD (the
/// deep-review runner's `C:\rtc\<agent>` holds only a `tests` junction).
let tryDocRoot () : string option =
    let isRoot (d: string) =
        File.Exists(Path.Combine(d, "docs", "formalism.md")) && File.Exists(Path.Combine(d, "CLAUDE.md"))
    let rec ancestors (d: DirectoryInfo) =
        seq { if not (isNull d) then
                yield d.FullName
                yield! ancestors d.Parent }
    Seq.append [ Directory.GetCurrentDirectory() ] (ancestors (DirectoryInfo AppContext.BaseDirectory))
    |> Seq.tryFind isRoot

/// The pages the lane reads, repo-relative, in a stable order.
let docPages (root: string) : string list =
    let rel (p: string) = Path.GetRelativePath(root, p).Replace('\\', '/')
    let mdIn (dir: string) (recurse: bool) =
        let full = Path.Combine(root, dir)
        if Directory.Exists full then
            Directory.GetFiles(full, "*.md", (if recurse then SearchOption.AllDirectories else SearchOption.TopDirectoryOnly))
            |> Array.map rel
            |> Array.sortWith (fun a b -> String.CompareOrdinal(a, b))
            |> Array.toList
        else []
    [ "CLAUDE.md" ]
    @ mdIn "docs" false
    @ mdIn (Path.Combine("docs", "features")) false
    @ mdIn (Path.Combine("docs", "research")) false
    @ mdIn (Path.Combine("docs", "plans")) true

let private knownFlags = set [ "sketch"; "rejects"; "prelude"; "cont"; "check" ]

/// Extract a page's ```blade blocks, assembling each block's program.
let extractBlocks (page: string) (text: string) : DocBlock list =
    let lines = text.Replace("\r\n", "\n").Split('\n')
    let isPlan = page.StartsWith "docs/plans/"
    let blocks = ResizeArray<DocBlock>()
    let mutable pagePreludes = ""
    let mutable preludes = ""
    let mutable seenSection = false
    let mutable chain = ""
    let mutable i = 0
    while i < lines.Length do
        let line = lines.[i]
        let trimmed = line.TrimStart(' ')
        let indent = line.Length - trimmed.Length
        if indent <= 3 && trimmed.StartsWith "```" then
            let ticks = trimmed.Length - trimmed.TrimStart('`').Length
            let info = trimmed.Substring(ticks).Trim()
            // Body: up to a closing fence of at least as many backticks.
            let body = ResizeArray<string>()
            let mutable j = i + 1
            let isClose (l: string) =
                let t = l.Trim()
                t.Length >= ticks && t.TrimStart('`').Length = 0
            while j < lines.Length && not (isClose lines.[j]) do
                let l = lines.[j]
                let lead = l.Length - l.TrimStart(' ').Length
                body.Add(if lead >= indent then l.Substring(indent) else l.TrimStart(' '))
                j <- j + 1
            let words = info.Split([| ' '; '\t' |], StringSplitOptions.RemoveEmptyEntries)
            if words.Length > 0 && words.[0] = "blade" then
                let flags = words |> Array.skip 1 |> Set.ofArray
                let src = String.Join("\n", body) + "\n"
                let firstLine =
                    body |> Seq.map (fun l -> l.Trim()) |> Seq.tryFind (fun l -> l <> "") |> Option.defaultValue ""
                let unknown = flags - knownFlags
                let optedIn = not isPlan || not (Set.intersect flags (set [ "check"; "rejects"; "prelude"; "cont" ])).IsEmpty
                let isSketch =
                    flags.Contains "sketch" || firstLine.StartsWith("// sketch", StringComparison.OrdinalIgnoreCase)
                if optedIn then
                    let kind =
                        if isSketch then Sketch
                        elif flags.Contains "rejects" then Rejects
                        // Any `// EXPECT:` line makes it a run: a pin that does not parse
                        // is then a FAIL (the corpus classifier's malformed-pin rule),
                        // never a silently unchecked claim.
                        elif (expectLinesVerbatim src |> List.isEmpty |> not) then Run
                        else Check
                    let problem =
                        if not unknown.IsEmpty then
                            Some $"""unknown doc-test flag(s) {(unknown |> String.concat ", ")} (known: {(knownFlags |> String.concat ", ")})"""
                        elif flags.Contains "cont" && chain = "" && kind <> Sketch then
                            Some "`cont` block with no earlier checked block in its section to continue"
                        else None
                    let chainPart = if flags.Contains "cont" then chain else ""
                    let program = pagePreludes + preludes + chainPart + src
                    blocks.Add { Page = page; Line = i + 2; Kind = kind; Source = program; Problem = problem }
                    match kind with
                    | Check | Run ->
                        if flags.Contains "prelude" then
                            if seenSection then preludes <- preludes + src
                            else pagePreludes <- pagePreludes + src
                        else chain <- chainPart + src
                    | Rejects | Sketch -> ()
            i <- j + 1
        else
            // A level-2 heading closes the section: its preludes and its
            // running `cont` program end with it.
            if line.StartsWith "## " then
                seenSection <- true
                preludes <- ""
                chain <- ""
            i <- i + 1
    List.ofSeq blocks

/// Every block of every page (optionally filtered by a path substring).
let collectBlocks (root: string) (filter: string option) : DocBlock list =
    docPages root
    |> List.filter (fun p -> match filter with Some f -> p.Contains f | None -> true)
    |> List.collect (fun p -> extractBlocks p (File.ReadAllText(Path.Combine(root, p))))

let private blockName (b: DocBlock) =
    let baseName = $"{b.Page}:{b.Line}"
    if b.Kind = Rejects then baseName + " (rejects)" else baseName

let private firstLineOf (s: string) =
    let l = s.Replace("\r\n", "\n").Split('\n') |> Array.tryFind (fun l -> l.Trim() <> "") |> Option.defaultValue s
    if l.Length > 220 then l.Substring(0, 220) + "..." else l

/// Judge one block. Returns (outcome, detail).
let private judge (outputDir: string) (gpp: bool) (b: DocBlock) : Outcome * string =
    match b.Problem with
    | Some p -> Fail, p
    | None ->
    match b.Kind with
    | Sketch -> Skip, "sketch"
    | Check ->
        let r = runFullTest (blockName b) b.Source outputDir false
        match r.IRResult with
        | Ok _ -> Pass, "lowered"
        | Error e -> Fail, firstLineOf e
    | Rejects ->
        let codegenStage = parseRejectStage b.Source = RejectAtCodegen
        let r = runFullTest (blockName b) b.Source outputDir (codegenStage && gpp)
        classifyWithDetail r
    | Run ->
        let r = runFullTest (blockName b) b.Source outputDir gpp
        if not gpp then
            match r.IRResult with
            | Ok _ -> Skip, "lowered; run skipped (no g++)"
            | Error e -> Fail, firstLineOf e
        else
            match classifyWithDetail r with
            | Fail, detail ->
                // The classifier says WHICH stage failed; a doc reader needs
                // the reason too, and a doc block has no corpus file to rerun.
                let why =
                    match r.IRResult, r.CompileResult with
                    | Error e, _ -> firstLineOf e
                    | Ok _, Error e when not (isSkipError e) ->
                        e.Split([| '\r'; '\n' |], System.StringSplitOptions.RemoveEmptyEntries)
                        |> Array.tryFind (fun l -> l.Contains "error")
                        |> Option.map firstLineOf
                        |> Option.defaultValue (firstLineOf e)
                    | _ ->
                        match r.ValueCheckResult with
                        | Error (m :: _) -> firstLineOf m
                        | _ -> ""
                Fail, (if why = "" || detail.Contains why then detail else $"{detail}: {why}")
            | verdict -> verdict

/// `blade test docs [<filter>]`.
let runDocTests (filter: string option) : BlockResult =
    let blockLabel = "Docs (executable code blocks)"
    printHeader "Docs: every ```blade block in CLAUDE.md and docs/ (blade test docs)"
    match tryDocRoot () with
    | None ->
        printfn "  docs not found: no directory holding docs/formalism.md + CLAUDE.md at the working"
        printfn "  directory or above the binary (%s)" AppContext.BaseDirectory
        resultLine Fail "docs root" "not found"
        printFooter blockLabel [ "0 passed"; "1 failed" ]
        { Block = blockLabel; Passed = 0; Failed = 1; Skipped = 0; FailedNames = [ "docs root not found" ] }
    | Some root ->
    let blocks = collectBlocks root filter
    let outputDir = Path.Combine(".", "generated_cpp_tests", "docs")
    Directory.CreateDirectory outputDir |> ignore
    CodeGen.deployRuntimeHeaders outputDir
    let gpp = checkGppAvailable ()
    let sw = Diagnostics.Stopwatch.StartNew()
    // In parallel, like the corpus: the front end is serialized inside
    // runFullTest (on its own large stack); compiles and runs are not.
    let verdicts =
        blocks
        |> Array.ofList
        |> Array.Parallel.map (fun b -> b, judge outputDir gpp b)
        |> Array.toList
    let count k = blocks |> List.filter (fun b -> b.Kind = k) |> List.length
    for (b, (o, d)) in verdicts do
        // Passing checks are the bulk; print the interesting lines only.
        match o, b.Kind with
        | Pass, Check | Skip, Sketch -> ()
        | _ -> resultLine o (blockName b) d
    let n o = verdicts |> List.filter (fun (_, (x, _)) -> x = o) |> List.length
    let passed, failed, skipped = n Pass, n Fail, n Skip
    printfn "  pages: %d; blocks: %d checked, %d run, %d rejects, %d sketch (%.1fs)"
        (blocks |> List.map (_.Page) |> List.distinct |> List.length)
        (count Check) (count Run) (count Rejects) (count Sketch) sw.Elapsed.TotalSeconds
    printFooter blockLabel [ $"{passed} passed"; $"{failed} failed"; $"{skipped} skipped" ]
    { Block = blockLabel
      Passed = passed
      Failed = failed
      // Sketches are declared skips, not missing capabilities: they are
      // counted on the census line above, not in the suite's skip total
      // (which is read as "something could not run").
      Skipped = skipped - count Sketch
      FailedNames = verdicts |> List.filter (fun (_, (o, _)) -> o = Fail) |> List.map (fst >> blockName) }
