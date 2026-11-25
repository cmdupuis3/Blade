// The BORROW ORACLE (borrow round, 2026-11-23; review row T1): the borrow rule
// measured from outside.
//
// The rule (COMMON brief of the round): binding an uncomputed (deferred) array
// value over storage `x` makes `x` read-only until the value's last use; a
// write to `x` while the value is still used is refused; `|> compute`
// detaches. It is sound exactly when every ACCEPTED program reads the same
// values whether an uncomputed array value is evaluated (A) at its BINDING,
// (B) afresh at each USE from the current inputs, or (C) once at its first use
// and kept. The interpreter implements A and B behind BLADE_INTERP_READING
// (Interp/Value.fs DeferReading; docs/reference/knob-notes.md); this block
// runs every program of a corpus directory under both and compares what they
// print. PRINTING IS A USE (R5 of the round, as the owner rewrote it): an
// uncomputed binding prints under both readings exactly when the interpreter's
// own schedule prints it (a third walk gives that set), reading A the cells
// evaluated at its binding, reading B the cells evaluated at the END of the
// program from the inputs as they are then -- and what it prints is compared.
//
// Verdicts, per program:
//   AGREE         both readings ran and printed the same, or both failed the
//                 same way (code, message and location).
//   DIFFER        both ran and printed something different: names the first
//                 binding that differs and its two values. Marked NO-WRITES
//                 when the program contains no assignment at all -- a value
//                 difference there is a compiler bug, not the borrow class.
//   FAILURE-ONLY  the difference is ONLY where or whether a failure fires
//                 (one reading fails and the other does not, or both fail
//                 differently): decision 6 of the deep-review page is not
//                 decided (R6), so it is its own column, never a value verdict.
//   UNSUPPORTED   the interpreter skipped the program under either reading
//                 (interp-unsupported, an interpreter fault, or the time limit).
// Programs the front end refuses (reject probes, refused sources) are counted
// as refused and not run.
//
// Two contracts. CENSUS (the default until the rule lands): a DIFFER is a
// finding, reported and listed (`--census <path>` writes them per directory as
// Markdown) -- the dynamic lower bound of what the rule must refuse. STRICT
// (`--strict`): every DIFFER of a program that lowered is a hole in the rule
// and fails the block. FAILURE-ONLY and UNSUPPORTED never fail it.
//
// Cost: one front-end pass and two interpreter walks per program, no g++. The
// walks run one at a time under the harness's pipeline lock (the display-frame
// buffer the interpreter writes is process state), with the front end of up to
// three later programs prepared ahead, so at most four programs are in flight.
module Blade.Tests.BorrowOracle

open System
open System.IO
open System.Text.RegularExpressions
open System.Threading.Tasks
open Blade
open Blade.IR
open Blade.Interp
open Blade.Tests.TestHarness
open Blade.Tests.Corpus
open Blade.Tests.Runner

/// What one reading of one program produced.
type ReadingOutcome =
    /// Ran to completion: the printed entries, `name = value` text, in order.
    | Ran of (string * string) list
    /// Failed at run time: the interpreter's whole panic text (code, message,
    /// location, frames).
    | Failed of string
    /// The interpreter skipped it: why.
    | Skipped of string

/// The verdict on one program.
type Verdict =
    | Agree
    /// The first binding that differs, its value under A, under B, and whether
    /// the program contains no assignment at all.
    | Differ of binding: string * underA: string * underB: string * noWrites: bool
    | FailureOnly of string
    | Unsupported of string

/// The first line of a text (panic texts and skip reasons are one-liners in
/// a report).
let private firstLine (s: string) =
    s.Replace("\r\n", "\n").Split('\n') |> Array.tryHead |> Option.defaultValue ""

/// A printed line that opens an entry: `name = ...` (a qualified module name,
/// a bare-expression name included). Anything else continues the entry above.
let private entryHead = Regex(@"^([A-Za-z_][A-Za-z0-9_.']*) = (.*)$")

/// The printed output as entries in order. Lines before the first entry
/// (display frames) form one `(preamble)` entry.
let printedEntries (stdout: string) : (string * string) list =
    let text = Blade.Tests.InterpDiff.normalize stdout
    if text = "" then []
    else
        let entries = Collections.Generic.List<string * Text.StringBuilder>()
        for line in text.Split('\n') do
            let m = entryHead.Match line
            if m.Success then entries.Add((m.Groups.[1].Value, Text.StringBuilder(m.Groups.[2].Value)))
            elif entries.Count = 0 then entries.Add(("(preamble)", Text.StringBuilder(line)))
            else (snd entries.[entries.Count - 1]).Append('\n').Append(line) |> ignore
        [ for (n, sb) in entries -> (n, sb.ToString()) ]

/// The first entry that differs between two runs: in A's order, then the
/// entries only B printed.
let firstDifference (a: (string * string) list) (b: (string * string) list) : (string * string * string) option =
    let bMap = Map.ofList b
    let aMap = Map.ofList a
    let inA =
        a |> List.tryPick (fun (n, va) ->
            match Map.tryFind n bMap with
            | Some vb when vb = va -> None
            | Some vb -> Some (n, va, vb)
            | None -> Some (n, va, "<not printed>"))
    match inA with
    | Some d -> Some d
    | None ->
        b |> List.tryPick (fun (n, vb) ->
            if aMap.ContainsKey n then None else Some (n, "<not printed>", vb))

/// The verdict on two outcomes (A first). `noWrites`: the program contains no
/// assignment.
let judge (noWrites: bool) (a: ReadingOutcome) (b: ReadingOutcome) : Verdict =
    match a, b with
    | Skipped r, _ -> Unsupported ("A: " + r)
    | _, Skipped r -> Unsupported ("B: " + r)
    | Ran ea, Ran eb ->
        match firstDifference ea eb with
        | None -> Agree
        | Some (n, va, vb) -> Differ (n, va, vb, noWrites)
    | Failed fa, Failed fb when fa = fb -> Agree
    | Failed fa, Failed fb -> FailureOnly ($"A fails `{firstLine fa}`, B fails `{firstLine fb}`")
    | Failed fa, Ran _ -> FailureOnly ($"A fails `{firstLine fa}`, B runs")
    | Ran _, Failed fb -> FailureOnly ($"A runs, B fails `{firstLine fb}`")

/// Whether the program contains no assignment anywhere: its top-level
/// bindings and every function body (lifted lambdas included). The generic
/// walker skips callable bodies, so the functions are walked here.
let programWritesNothing (program: IRProgram) : bool =
    let isAssign (e: IRExpr) = match e with IRAssign _ -> true | _ -> false
    program.Modules |> List.forall (fun m ->
        not (m.Bindings |> List.exists (fun b -> existsIRExpr isAssign b.Value))
        && not (m.Functions |> List.exists (fun f -> existsIRExpr isAssign f.Body)))

/// The per-reading time limit: BLADE_INTERP_TIMEOUT_MS when set, else 60 s
/// (a census over the corpus wants a short backstop; an abandoned walk keeps
/// its thread, as in the interpreter differential).
let private readingTimeoutMs () : int =
    match Knobs.raw Knobs.interpTimeoutMs with
    | null | "" -> 60000
    | s ->
        match Int32.TryParse s with
        | true, v when v > 0 -> v
        | _ -> 60000

/// One interpreter walk under `reading`, bounded by the time limit, with the
/// set of uncomputed bindings that print (`printDeferred`, see
/// Run.runProgramPrinting), returning the outcome and the uncomputed
/// bindings the walk printed.
let runReadingPrinting (reading: Blade.Interp.Value.DeferReading) (printDeferred: Set<string> option)
                       (program: IRProgram) (name: string) : ReadingOutcome * Set<string> =
    let limit = readingTimeoutMs ()
    let task = Task.Run(fun () -> Run.runProgramPrinting reading printDeferred program name Blade.Interp.Value.defaultLimits)
    if not (task.Wait limit) then (Skipped (sprintf "timed out (>%gs)" (float limit / 1000.0)), Set.empty)
    else
        let (r, printed) = task.Result
        if r.ExitCode = Run.ExitOk then (Ran (printedEntries r.Stdout), printed)
        elif r.ExitCode = Run.ExitPanic then (Failed (r.Stderr.Trim()), Set.empty)
        elif r.ExitCode = Run.ExitUnsupported then (Skipped (firstLine r.Stderr), Set.empty)
        else (Skipped ("interpreter fault: " + firstLine r.Stderr), Set.empty)

/// One interpreter walk under `reading` (no print set: under a reading no
/// uncomputed binding prints).
let runReading (reading: Blade.Interp.Value.DeferReading) (program: IRProgram) (name: string) : ReadingOutcome =
    fst (runReadingPrinting reading None program name)

/// The uncomputed bindings the interpreter's OWN schedule prints for a
/// program (COMMON R5 as rewritten: printing is a use, and a binding prints
/// under a reading exactly when it prints today). Empty when today's run
/// fails or is skipped.
let todaysPrintSet (program: IRProgram) (name: string) : Set<string> =
    snd (runReadingPrinting Blade.Interp.Value.ReadingToday None program name)

/// Run a front-end pass for READING A: the type checker keeps the NAME of an
/// uncomputed map a fold reads (TypeCheckInferReduce.frontEndReadingAtBinding)
/// instead of splicing a copy of its producer, so the fold reads the value as
/// evaluated at its binding. Set per flow (AsyncLocal), restored after: a
/// front end prepared ahead for another program is not affected.
let withReadingAFrontEnd (f: unit -> 'T) : 'T =
    let prior = Blade.TypeCheckInferReduce.frontEndReadingAtBinding.Value
    Blade.TypeCheckInferReduce.frontEndReadingAtBinding.Value <- true
    try f ()
    finally Blade.TypeCheckInferReduce.frontEndReadingAtBinding.Value <- prior

/// Both readings of one program, walked one after the other under the
/// pipeline lock, and the verdict: reading A over the program as lowered for
/// reading A (`programA`), reading B over the ordinary lowering (`programB`).
/// When the reading-A pass REFUSES a program the ordinary pass accepts, reading
/// A runs over the ordinary lowering instead: the reading-A pass keeps a fold's
/// spine name (the G235 route), which forces the spine under its own name and so
/// makes it print, and the borrow judgment then sees that print as a use after
/// a later write -- a refusal of the instrument's own lowering, not of the
/// program. Over the ordinary lowering the fold reads a spliced copy of the
/// producer: the same values whenever the fold runs before every write to its
/// inputs, which is the only order the ordinary judgment accepts.
///
/// PRINTING IS A USE (COMMON R5, rewritten by the owner): an uncomputed
/// binding prints under both readings exactly when today's schedule prints it
/// -- reading A the cells evaluated at its binding, reading B the cells
/// evaluated at the end of the program -- and what it prints is compared.
let judgeProgram (programA: Result<IRProgram, string>) (programB: IRProgram) (name: string) : Verdict =
    underPipelineLock (fun () ->
        let printSet = Some (todaysPrintSet programB name)
        let a =
            match programA with
            | Ok pa -> fst (runReadingPrinting Blade.Interp.Value.ReadingAtBinding printSet pa name)
            | Error _ -> fst (runReadingPrinting Blade.Interp.Value.ReadingAtBinding printSet programB name)
        let b = fst (runReadingPrinting Blade.Interp.Value.ReadingAtUse printSet programB name)
        judge (programWritesNothing programB) a b)

/// A program's front end: the validated IR of the reading-A pass (or why it
/// refused) and of the ordinary pass, or why the ordinary pass refused.
type private FrontEnd =
    | Lowered of Result<IRProgram, string> * IRProgram
    | Refused of string

/// How many programs are in flight at once (front end ahead + the walk).
let private maxInFlight = 4

/// Run `work` over `items` with at most `maxInFlight` started ahead, handing
/// results back by index in ascending order (InterpDiff's runAhead, bounded).
let private runAhead (items: 'A[]) (work: 'A -> 'B) : int -> 'B =
    let tasks : Task<'B>[] = Array.zeroCreate items.Length
    let mutable started = 0
    fun (i: int) ->
        while started < items.Length && started < i + maxInFlight do
            let item = items.[started]
            tasks.[started] <- Task.Factory.StartNew<'B>(Func<'B>(fun () -> work item), TaskCreationOptions.LongRunning)
            started <- started + 1
        let t = tasks.[i]
        tasks.[i] <- Unchecked.defaultof<Task<'B>>
        t.GetAwaiter().GetResult()

/// The per-directory tally and the DIFFER findings.
type DirResult =
    { Dir: string
      Agree: int
      Differ: (string * Verdict) list
      FailureOnly: int
      Unsupported: int
      Refused: int }

/// One corpus directory through the oracle. `strict`: a DIFFER fails.
let private runDirectory (strict: bool) (tmpRoot: string) (dir: string) (fail: string -> string -> unit) : DirResult =
    printSubHeader ($"category: {dir}")
    let isMulti = (multiFileCategories ()) |> List.contains dir
    let items : (string * (unit -> FrontEnd))[] =
        if isMulti then
            multiFileCategory dir
            |> List.map (fun (name, files) ->
                name, (fun () ->
                    if name.EndsWith "(rejects)" then Refused "reject probe"
                    else
                        let lower () =
                            underPipelineLock (fun () ->
                                let (lowered, _) = Blade.Runtime.runOnLargeStack (fun () -> Blade.Lowering.lowerMultiSourceCaptured files)
                                lowered |> Result.bind (fun ir -> Blade.IRValidate.validateIR ir |> Result.mapError (String.concat "; ")))
                        match lower () with
                        | Ok ir -> Lowered (withReadingAFrontEnd lower, ir)
                        | Error e -> Refused (firstLine e)))
            |> Array.ofList
        else
            let rejectOnly = rejectOnlyCategories.Contains dir
            categoryTests dir
            |> List.map (fun (name, source) ->
                name, (fun () ->
                    if rejectOnly || name.EndsWith "(rejects)" then Refused "reject probe"
                    else
                        let r = runFullTest name source tmpRoot false
                        match r.IRResult with
                        | Ok ir -> Lowered (withReadingAFrontEnd (fun () -> (runFullTest name source tmpRoot false).IRResult), ir)
                        | Error e -> Refused (firstLine e)))
            |> Array.ofList
    let fronts = runAhead items (fun (_, fe) -> fe ())
    let mutable agree = 0
    let mutable failureOnly = 0
    let mutable unsupported = 0
    let mutable refused = 0
    let differ = Collections.Generic.List<string * Verdict>()
    for i in 0 .. items.Length - 1 do
        let (name, _) = items.[i]
        match fronts i with
        | Refused _ -> refused <- refused + 1
        | Lowered (irA, ir) ->
            match judgeProgram irA ir name with
            | Agree -> agree <- agree + 1
            | FailureOnly d ->
                failureOnly <- failureOnly + 1
                resultLine Pass name ("FAILURE-ONLY: " + d)
            | Unsupported d ->
                unsupported <- unsupported + 1
                resultLine Skip name ("UNSUPPORTED: " + d)
            | Differ (b, va, vb, noWrites) as v ->
                differ.Add((name, v))
                let tag = if noWrites then "DIFFER (NO-WRITES: a compiler bug)" else "DIFFER"
                let detail = $"{tag}: `{b}` is `{firstLine va}` under A, `{firstLine vb}` under B"
                if strict || noWrites then fail name detail
                else resultLine Skip name ("FINDING " + detail)
    printfn "  %s: %d agree, %d differ, %d failure-only, %d unsupported, %d refused"
        dir agree differ.Count failureOnly unsupported refused
    { Dir = dir; Agree = agree; Differ = List.ofSeq differ; FailureOnly = failureOnly
      Unsupported = unsupported; Refused = refused }

/// The corpus-relative path of each test of a directory, by its `// TEST:`
/// name: `dir/NNN_x.blade` for a single-file directory, `dir/NNN_x/` for a
/// multi-file one (the census lists both, for a mechanical comparison).
let private corpusPathsOf (dir: string) : Map<string, string> =
    let root =
        [ Path.Combine(".", "tests", "corpus"); Path.Combine(AppContext.BaseDirectory, "tests", "corpus") ]
        |> List.tryFind Directory.Exists
    let nameOf (file: string) =
        let first = (File.ReadLines file |> Seq.tryHead |> Option.defaultValue "").TrimEnd('\r')
        if first.StartsWith "// TEST: " then Some (first.Substring 9) else None
    match root with
    | None -> Map.empty
    | Some r ->
        let d = Path.Combine(r, dir)
        let files = Directory.GetFiles(d, "*.blade")
        if files.Length > 0 then
            files |> Array.choose (fun f -> nameOf f |> Option.map (fun n -> n, $"{dir}/{Path.GetFileName f}")) |> Map.ofArray
        else
            Directory.GetDirectories d
            |> Array.choose (fun sub ->
                Directory.GetFiles(sub, "*.blade")
                |> Array.sortWith (fun a b -> String.CompareOrdinal(a, b))
                |> Array.tryHead
                |> Option.bind nameOf
                |> Option.map (fun n -> n, $"{dir}/{Path.GetFileName sub}/"))
            |> Map.ofArray

/// The census file: the DIFFER programs per directory, each by its
/// corpus-relative file path and its test name, with the totals; then every
/// DIFFER path that writes, one per line, for a mechanical comparison.
let private writeCensus (path: string) (results: DirResult list) =
    let sb = Text.StringBuilder()
    let line (s: string) = sb.Append(s).Append('\n') |> ignore
    let sum f = results |> List.sumBy f
    line "# Borrow oracle census"
    line ""
    line "Readings A (evaluated at the binding) and B (evaluated at each use) of every corpus program, through the"
    line "interpreter (`blade test borrow-oracle`, BLADE_INTERP_READING). A DIFFER is a program whose printed computed"
    line "values or scalars differ between the readings: the dynamic lower bound of what the borrow rule must refuse."
    line ""
    line (sprintf "Totals: %d agree, %d differ, %d failure-only, %d unsupported, %d refused, over %d directories."
              (sum _.Agree) (sum (fun r -> r.Differ.Length)) (sum _.FailureOnly) (sum _.Unsupported) (sum _.Refused) results.Length)
    line ""
    let borrowClass = Collections.Generic.List<string>()
    for r in results do
        if not r.Differ.IsEmpty then
            let paths = corpusPathsOf r.Dir
            line (sprintf "## %s (%d)" r.Dir r.Differ.Length)
            line ""
            for (name, v) in r.Differ do
                match v with
                | Differ (b, va, vb, noWrites) ->
                    let file = Map.tryFind name paths |> Option.defaultValue (r.Dir + "/?")
                    let tag = if noWrites then " **NO-WRITES**" else ""
                    if not noWrites then borrowClass.Add file
                    line (sprintf "- `%s` -- %s%s: `%s` is `%s` under A, `%s` under B" file name tag b (firstLine va) (firstLine vb))
                | _ -> ()
            line ""
    line (sprintf "## Every DIFFER that writes (%d), one file per line" borrowClass.Count)
    line ""
    line "```"
    for f in borrowClass do line f
    line "```"
    File.WriteAllText(path, sb.ToString())

/// `blade test borrow-oracle [<dir>...] [--strict] [--census <path>]`: every
/// directory named (each a literal tests/corpus/<dir> name), or the whole
/// corpus with none.
let runBorrowOracle (dirs: string list) (strict: bool) (censusPath: string option) : BlockResult =
    printHeader "Borrow Oracle (readings A and B)"
    let blockName = "Borrow Oracle"
    let tmpRoot = Path.Combine(Path.GetTempPath(), $"blade_borrow_oracle_{Diagnostics.Process.GetCurrentProcess().Id}")
    (try Directory.Delete(tmpRoot, true) with _ -> ())
    Directory.CreateDirectory tmpRoot |> ignore
    let mutable failed = 0
    let mutable failedNames : string list = []
    let fail name detail =
        failed <- failed + 1
        failedNames <- failedNames @ [ name ]
        resultLine Fail name detail
    let dirs =
        if dirs.IsEmpty then
            let excluded = Blade.Tests.InterpDiff.interpExcluded |> List.map fst |> Set.ofList
            (singleFileCategories () @ multiFileCategories ()) |> List.filter (fun c -> not (excluded.Contains c))
        else dirs
    let results = dirs |> List.map (fun d -> runDirectory strict tmpRoot d fail)
    censusPath |> Option.iter (fun p -> writeCensus p results)
    (try Directory.Delete(tmpRoot, true) with _ -> ())
    let sum f = results |> List.sumBy f
    let differs = sum (fun r -> r.Differ.Length)
    printFooter blockName
        [ $"{sum _.Agree} agree"
          $"{differs} differ" + (if strict then "" else " (findings)")
          $"{sum _.FailureOnly} failure-only"
          $"{sum _.Unsupported} unsupported"
          $"{sum _.Refused} refused"
          $"{failed} failed" ]
    { Block = blockName
      Passed = sum _.Agree + sum _.FailureOnly
      Failed = failed
      // census findings (DIFFER of a program that writes) count with the skips
      Skipped = sum _.Unsupported + (differs - failed)
      FailedNames = failedNames }

/// `blade test borrow-oracle --file <path.blade>`: one program (a saved
/// generator finding, a probe), run under the interpreter's own schedule and
/// under readings A and B, each output printed, then the verdict. No g++.
let runOracleOnFile (path: string) : int =
    let source = File.ReadAllText path
    let name = Path.GetFileNameWithoutExtension path
    let tmpRoot = Path.Combine(Path.GetTempPath(), $"blade_borrow_oracle_{Diagnostics.Process.GetCurrentProcess().Id}")
    Directory.CreateDirectory tmpRoot |> ignore
    let r = runFullTest name source tmpRoot false
    let rA = withReadingAFrontEnd (fun () -> (runFullTest name source tmpRoot false).IRResult)
    (try Directory.Delete(tmpRoot, true) with _ -> ())
    match r.IRResult with
    | Error e ->
        printfn "%s: refused by the front end: %s" name (firstLine e)
        1
    | Ok ir ->
        let printSet = Some (underPipelineLock (fun () -> todaysPrintSet ir name))
        let show (label: string) (reading: Blade.Interp.Value.DeferReading) =
            let program =
                match reading, rA with
                | Blade.Interp.Value.ReadingAtBinding, Ok irA -> irA
                | _ -> ir
            let o = underPipelineLock (fun () -> fst (runReadingPrinting reading printSet program name))
            printfn "--- %s ---" label
            (match o with
             | Ran entries -> for (n, v) in entries do printfn "%s = %s" n v
             | Failed t -> printfn "FAILS: %s" (firstLine t)
             | Skipped why -> printfn "SKIPPED: %s" why)
            o
        show "interpreter, its own schedule" Blade.Interp.Value.ReadingToday |> ignore
        let a = show "reading A (at the binding)" Blade.Interp.Value.ReadingAtBinding
        let b = show "reading B (at each use)" Blade.Interp.Value.ReadingAtUse
        printfn "--- verdict ---"
        match judge (programWritesNothing ir) a b with
        | Agree -> printfn "AGREE"; 0
        | FailureOnly d -> printfn "FAILURE-ONLY: %s" d; 0
        | Unsupported d -> printfn "UNSUPPORTED: %s" d; 0
        | Differ (bn, va, vb, noWrites) ->
            printfn "DIFFER%s: `%s` is `%s` under A, `%s` under B" (if noWrites then " (NO-WRITES)" else "") bn va vb
            1

/// The words after `test borrow-oracle`: directory names, `--strict`,
/// `--census <path>`, or `--file <path.blade>` alone. Returns the process
/// exit code.
let dispatchBorrowOracle (args: string list) : int =
    let rec parse (dirs: string list) (strict: bool) (census: string option) (rest: string list) =
        match rest with
        | [] -> Ok (List.rev dirs, strict, census)
        | "--strict" :: tail -> parse dirs true census tail
        | "--census" :: p :: tail -> parse dirs strict (Some p) tail
        | "--census" :: [] -> Error "--census needs a path"
        | d :: tail -> parse (d :: dirs) strict census tail
    match args with
    | [ "--file"; path ] -> runOracleOnFile path
    | _ ->
    match parse [] false None args with
    | Error e -> eprintfn "test borrow-oracle: %s" e; 2
    | Ok (dirs, strict, census) ->
        let known = Set.ofList (singleFileCategories () @ multiFileCategories ())
        match dirs |> List.tryFind (fun d -> not (known.Contains d)) with
        | Some d -> eprintfn "test borrow-oracle: '%s' is not a tests/corpus directory" d; 2
        | None ->
            let r = runBorrowOracle dirs strict census
            if r.Failed = 0 then 0 else 1
