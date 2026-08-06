// ReplSession.fs: the REPL's eval-once engine, lifted out of `Cli.replLoop` so
// two front ends can drive the SAME session semantics -- the interactive REPL
// (prompts, typed echoes, `[snippet not kept]` on stderr) and `ide serve`'s
// notebook `eval` command (structured results over NDJSON).
//
// A session is a list of raw source snippets plus the top-level name each one
// defines. Every submission builds a CANDIDATE list: a declaration REPLACES
// the earlier definition of the same name in place (so later snippets see the
// rebind), a reassignment and a bare expression each append a hidden wrapper.
// The candidate is written to a temp `session.blade`, lowered ONCE
// (Interp.Repl.lowerSessionDiag) and run under the tree-walking interpreter,
// falling back to a g++ compile+run only where the interpreter falls short.
// A candidate the front end REJECTS is discarded and the session is untouched;
// the session is only replaced once its candidate ran to exit 0. That is the
// whole of "snippet not kept".
//
// The engine NEVER prints. Rejections come back as coded, spanned Diagnostics
// beside the SourceMap that renders them; runs come back as the raw streams
// plus the matched echo LINE, so every caller-visible string -- the prompts,
// the `[snippet not kept]` marker, the type-annotated echo -- stays in the
// front end that owns it. The one exception is a caller-supplied `notice`
// sink, which exists so the interactive REPL can still print "falling back to
// compiled evaluation" BEFORE the multi-second g++ build rather than after it.
//
// Compiles after Interp/Repl.fs (its lowering seam) and before IdeServe.fs
// (its second front end). Cli.fs, far later, installs the g++ lane by hand:
// `compileToExe` sits on top of Build.fs and cannot be reached from here.
module Blade.ReplSession

open System
open System.IO
open System.Text.RegularExpressions

// REPL display: type-annotated echoes. The compiled session prints raw `name
// = value` lines; the REPL joins those with an in-process parse+typecheck of
// the SAME source to display types:
//   - primitives inline:                  a = Int64: 5
//   - other types (arrays, tuples, functions) on the next line, tabbed:
//         v = [1, 2, 3]
//             Array<Int64, Idx<3>>
//   - functions echo their signature; abstract (type-variable) positions
//     render with source names (`T`, `T^2`), inference-bound positions render
//     the concrete type substituted in.
module ReplTypes =
    open Blade.Ast
    open Blade.Types
    open Blade.IR
    open Blade.TypedAst

    /// What the REPL knows about one top-level name.
    type Info =
        | RVal of IRType
        | RFunc of signature: string

    /// Render a function signature: `(Int64, T) -> T`. Concrete positions
    /// print concretely; abstract positions print their source type-variable
    /// names (fresh letters for inference-invented ones); recovery/naming
    /// live in Blade.Ide, shared with `ide check`'s hover types.
    let funcSig (src: FunctionDecl option) (tf: TypedFunctionDecl) : string =
        let seed =
            match src with
            | Some f when f.Params.Length = tf.Params.Length ->
                [ for (p, tp) in List.zip f.Params tf.Params do
                    match p.Type with
                    | Some ann -> yield! Blade.Ide.collectVarNames ann tp.Type
                    | None -> ()
                  match f.ReturnType with
                  | Some ann -> yield! Blade.Ide.collectVarNames ann tf.ReturnType
                  | None -> () ]
            | _ -> []
        let pp = Blade.Ide.abstractRenderer seed
        let ps = tf.Params |> List.map (fun p -> pp p.Type)
        sprintf "(%s) -> %s" (String.concat ", " ps) (pp tf.ReturnType)

    /// Build the top-level name -> display info map from an ALREADY-lowered
    /// session (the same front-end pass the interpreter runs, so it never
    /// lowers twice). Value bindings prefer the LOWERED types: HM calls
    /// monomorphize during lowering, so the typed AST can still carry T?n
    /// inference vars where the IR is concrete.
    let sessionInfoOf (lowered: Blade.Interp.Repl.LoweredSession) : Map<string, Info> =
        let prog = lowered.Prog
        let tp = lowered.Typed
        let srcFuncs =
            [ for m in prog.Modules do
                for ld in m.Decls do
                    match ld.Value with
                    | DeclFunction f -> yield (f.Name, f)
                    | _ -> () ]
            |> Map.ofList
        let irTypes =
            Map.ofList
                [ for m in lowered.Ir.Modules do
                    for b in m.Bindings do
                        yield (b.Name, b.Type) ]
        let valTy (name: string) (fallback: IRType) =
            match Map.tryFind name irTypes with
            | Some t -> t
            | None -> fallback
        let mutable acc = Map.empty
        for m in tp.Modules do
            for d in m.Decls do
                match d with
                | TDeclLet b | TDeclStatic b ->
                    acc <- Map.add b.Name (RVal (valTy b.Name b.Type)) acc
                    for (n, _, t) in b.SubBindings do
                        acc <- Map.add n (RVal (valTy n t)) acc
                | TDeclFunction f ->
                    acc <- Map.add f.Name
                               (RFunc (funcSig (Map.tryFind f.Name srcFuncs) f)) acc
                | _ -> ()
        acc

    /// Parse + typecheck + lower session source (one pass) and return
    /// top-level name -> display info. Failures yield an empty map (values
    /// still print, just unannotated). Used for the bare-identifier "is this
    /// a session function?" probe; the candidate path reuses the
    /// interpreter's own LoweredSession via sessionInfoOf instead, so it never lowers twice.
    let sessionInfo (source: string) : Map<string, Info> =
        try
            match Blade.Interp.Repl.lowerSession None false source with
            | Error _ -> Map.empty
            | Ok lowered -> sessionInfoOf lowered
        with _ -> Map.empty

    /// Primitive = annotate inline ("Int64: 5"); everything else goes on the
    /// next line, tabbed.
    let rec isPrimitive (t: IRType) : bool =
        match t with
        | IRTScalar _ | IRTNat _ -> true
        | IRTIdxTagged (inner, _) | IRTUnitAnnotated (inner, _) -> isPrimitive inner
        | _ -> false

    let private eqLineRe = Regex(@"^([A-Za-z_][A-Za-z0-9_]*) = (.*)$", RegexOptions.Compiled)

    /// Split a bracketed body at the commas sitting at nesting depth zero, so a
    /// row (`[1, 2]`) or a complex cell (`(1, 0)`) stays ONE part. Depth counts
    /// `[` and `(` alike; commas inside quotes are literal.
    let private splitTopLevelCommas (inner: string) : string list =
        let parts = ResizeArray<string>()
        let cur = System.Text.StringBuilder()
        let mutable depth = 0
        let mutable inQuotes = false
        for c in inner do
            if c = '"' then
                inQuotes <- not inQuotes
                cur.Append(c) |> ignore
            elif inQuotes then cur.Append(c) |> ignore
            else
                match c with
                | '[' | '(' -> depth <- depth + 1; cur.Append(c) |> ignore
                | ']' | ')' -> depth <- depth - 1; cur.Append(c) |> ignore
                | ',' when depth = 0 ->
                    parts.Add(cur.ToString())
                    cur.Clear() |> ignore
                | _ -> cur.Append(c) |> ignore
        if cur.Length > 0 then parts.Add(cur.ToString())
        parts |> List.ofSeq

    /// How many entries the REPL shows per bracket level before eliding.
    let private elideAfter = 5

    /// The REPL's display cap: at EVERY bracket level, show the first
    /// `elideAfter` entries and truncate the rest to `...`. DISPLAY ONLY --
    /// the program's own stdout is untouched (`blade run` prints every cell,
    /// which is what the corpus pins read). Text-level on purpose: works for
    /// any printed shape without re-deriving the value. Public because the
    /// notebook lane caps its `bindings[]` values by the same rule the
    /// interactive echo does -- one display policy, not two.
    let rec elideValue (s: string) : string =
        let t = s.Trim()
        if not (t.Length >= 2 && t.StartsWith "[" && t.EndsWith "]") then t
        else
            let inner = t.Substring(1, t.Length - 2).Trim()
            if inner = "" then "[]"
            else
                let parts = splitTopLevelCommas inner
                let kept = parts |> List.truncate elideAfter |> List.map elideValue
                let shown = if parts.Length > elideAfter then kept @ [ "..." ] else kept
                "[" + String.concat ", " shown + "]"

    /// Rewrite one raw output line for display. `transient` is the synthetic
    /// binding a bare REPL expression was wrapped in; its name is stripped so the value echoes alone.
    let annotate (info: Map<string, Info>) (transient: string option) (line: string) : string =
        let m = eqLineRe.Match line
        if not m.Success then line
        else
            let name = m.Groups.[1].Value
            let value = elideValue m.Groups.[2].Value
            let isTransient = (transient = Some name)
            match Map.tryFind name info with
            | Some (RVal t) ->
                let tyStr = Blade.Ide.abstractRenderer [] t
                if isPrimitive t then
                    if isTransient then sprintf "%s: %s" tyStr value
                    else sprintf "%s = %s: %s" name tyStr value
                else
                    if isTransient then sprintf "%s\n\t%s" value tyStr
                    else sprintf "%s = %s\n\t%s" name value tyStr
            | Some (RFunc _) -> line
            | None -> if isTransient then value else line

// Submission classification. These regexes decide the SHAPE of a submission
// (declaration / reassignment / bare expression) and name the binding it
// echoes. They live here rather than in either front end because both drive
// the same three candidate builders below, and a classification that drifted
// between the REPL and the notebook would be two languages, not one.

/// Top-level name a snippet (re)defines, for rebind replacement.
let private bindingNameRe =
    Regex(@"^\s*(?:let\s+(?:mut\s+|static\s+)?|static\s+function\s+|function\s+|type\s+)([A-Za-z_][A-Za-z0-9_]*)")

let bindingName (snippet: string) : string option =
    let m = bindingNameRe.Match snippet
    if m.Success then Some m.Groups.[1].Value else None

/// The generated main prints a "<name> completed in Xs" timing line whose
/// value changes every run -- exclude it from the output diff.
let isTimingLine (l: string) =
    Regex.IsMatch(l, @"completed in [0-9.eE+~-]+m?s\s*$")

/// A snippet is a declaration iff it opens with a declaration keyword;
/// anything else is a bare expression to evaluate and echo.
let declRe =
    Regex(@"^\s*(let|static|function|type|struct|interface|impl|unit|import|from|module)\b")

let identRe = Regex(@"^[A-Za-z_][A-Za-z0-9_]*$")

/// A reassignment `x = e` (or `x[i] = e`, `x.f = e`, `x += e`, etc.): an
/// lvalue followed by an assignment operator. `=(?!=)` matches `=` but not
/// `==`, so `b == 1` stays a bare expression. Group 1 (leading identifier)
/// is the ROOT variable to echo. Checked after declRe so `let ...` stays a declaration.
let assignRe =
    Regex(@"^\s*([A-Za-z_][A-Za-z0-9_]*)(?:\.[A-Za-z_][A-Za-z0-9_]*|\[[^\]]*\])*\s*(?:\+=|-=|\*=|/=|=(?!=))")

/// A raw run-output line is `name = value`; grab the leading name so we can
/// single out just the one binding we mean to echo.
let outNameRe = Regex(@"^([A-Za-z_][A-Za-z0-9_]*) = ", RegexOptions.Compiled)

/// Classification looks at the first non-comment, non-blank line so a
/// doc-commented declaration isn't mistaken for a bare expression.
let classifyTarget (s: string) =
    s.Replace("\r\n", "\n").Split('\n')
    |> Array.tryFind (fun l ->
        let t = l.TrimStart()
        t <> "" && not (t.StartsWith "//"))
    |> Option.defaultValue ""

// The evaluation lanes and their results.

/// Which evaluator produced a run's output.
type Lane =
    /// The tree-walking interpreter -- the common path, <100 ms.
    | LaneInterp
    /// A g++ compile+run of this ONE input, entered only when the interpreter
    /// cannot evaluate some node yet (125) or hit its own bug (70).
    | LaneGpp

/// Everything one candidate evaluation produced, with nothing rendered.
type CandidateRun =
    { ExitCode: int
      Lane: Lane
      /// Wall time across lowering AND evaluation -- the number a notebook cell
      /// badge means by "how long did this take".
      ElapsedMs: int
      Stdout: string
      Stderr: string
      /// stdout split on newlines with the generated main's timing line dropped.
      Lines: string[]
      /// The RAW `name = value` line for the echo target, if the run printed
      /// one. Annotation is the caller's (ReplTypes.annotate).
      Echo: string option
      Info: Map<string, ReplTypes.Info>
      /// Typecheck warnings, drained structurally -- INTERP lane only. On the
      /// g++ lane the compile driver re-runs the front end and prints them
      /// itself, exactly as it always has, so surfacing them here too would
      /// double them.
      Warnings: Blade.Diagnostics.Diagnostic list }

/// The verdict on one candidate. `CandidateRan` covers a NON-ZERO exit too: a
/// runtime panic is a real answer, and whether such a candidate is kept is the
/// caller's call, not the engine's.
type CandidateOutcome =
    /// Front-end / validate rejection, structured plus the SourceMap that
    /// renders it rustc-style.
    | CandidateRejected of Blade.Diagnostics.Diagnostic list * Blade.Diagnostics.SourceMap
    /// The g++ lane could not produce a result: the message is already
    /// composed exactly as the REPL has always printed it.
    | CandidateFailed of string
    | CandidateRan of CandidateRun

// The structured result the notebook lane speaks.

/// One (re)bound name, with its concrete type and its ELIDED display value.
/// A bare expression yields a single entry with `Name = ""` -- the transient
/// wrapper's name, stripped exactly as the interactive echo strips it.
type Binding =
    { Name: string
      Type: string
      Value: string }

/// A diagnostic in CELL-LOCAL 1-based coordinates (see `EvalResult`).
type EvalDiagnostic =
    { Severity: string
      Line: int
      Col: int
      EndLine: int
      EndCol: int
      Message: string
      /// BLxxxx code; "" = none.
      Code: string }

/// One submission's outcome, in the coordinate space of the SUBMISSION rather
/// than of the assembled session file.
type EvalResult =
    { /// The submission was ACCEPTED (parsed, typechecked, lowered and ran to
      /// exit 0). False is the wire form of "[snippet not kept]": the session
      /// is unchanged. Note that a bare expression is accepted without joining
      /// the session -- acceptance and membership are different questions.
      Kept: bool
      ExitCode: int
      Lane: Lane
      ElapsedMs: int
      /// What the PROGRAM wrote: the run's stdout minus the generated main's
      /// timing line and minus every `name = value` binding echo. The session
      /// re-prints all of its bindings on every input, so leaving them here
      /// would make each cell replay the whole notebook; the ones that matter
      /// are in `Bindings`.
      Stdout: string
      Stderr: string
      Bindings: Binding list
      Diagnostics: EvalDiagnostic list }

/// The compiled fallback lane: session source path -> working directory ->
/// (exit code, stdout, stderr), or an already-composed failure message.
/// Injected because `compileToExe` lives in Cli.fs on top of Build.fs, both of
/// which compile long after this file.
type CompiledLane = string -> string -> Result<int * string * string, string>

let mutable private compiledLane : CompiledLane option = None

/// Cli.fs calls this once at dispatch time. Until it does, a submission the
/// interpreter cannot evaluate reports the lane as unavailable rather than
/// pretending the snippet failed.
let installCompiledLane (f: CompiledLane) = compiledLane <- Some f

// Diagnostic remapping: session-file coordinates -> submission coordinates.

/// Where a submission's own text sits inside the assembled session file --
/// everything the remap needs. `Prefix` is the characters a hidden wrapper
/// prepended on the snippet's first line (`let it = `), `LeadPad` the source
/// lines `Trim()` removed from the front of the submission.
type private Placement =
    { Index: int
      Prefix: int
      LeadPad: int }

let private lineCount (s: string) =
    (s.Replace("\r\n", "\n").Split('\n')).Length

/// Snippets are assembled with `String.concat "\n\n"`, so each one costs its
/// own line count plus the single blank line separating it from the next.
let private startLineOf (candidate: ResizeArray<string>) (idx: int) =
    let mutable line = 1
    for j in 0 .. idx - 1 do
        line <- line + lineCount candidate.[j] + 1
    line

let private severityText (s: Blade.Diagnostics.Severity) =
    match s with
    | Blade.Diagnostics.SevError -> "error"
    | Blade.Diagnostics.SevWarning -> "warning"
    | Blade.Diagnostics.SevNote -> "note"

/// Rebase one session-file diagnostic onto the submission.
///
/// A rebind splices MID-session, so the compiler can perfectly well report
/// against a LATER snippet that the rebind just broke -- a position with no
/// meaning in this cell at all. Those clamp to 1:1 and say so in the message,
/// which is honest and keeps the client from squiggling an unrelated line. A
/// diagnostic with no span (a provider load that failed at lowering time)
/// clamps to 1:1 as well, but earns no prefix: it is not elsewhere, it is
/// nowhere.
let private remapDiagnostic (candidate: ResizeArray<string>) (p: Placement)
                            (d: Blade.Diagnostics.Diagnostic) : EvalDiagnostic =
    let sev = severityText d.Severity
    let start = startLineOf candidate p.Index
    let span = lineCount candidate.[p.Index]
    let sl = d.Span.StartLine
    if sl <= 0 then
        { Severity = sev; Line = 1; Col = 1; EndLine = 1; EndCol = 1
          Message = d.Message; Code = d.Code }
    elif sl < start || sl > start + span - 1 then
        { Severity = sev; Line = 1; Col = 1; EndLine = 1; EndCol = 1
          Message = "elsewhere in session: " + d.Message; Code = d.Code }
    else
        // The wrapper's prefix only ever sits on the snippet's FIRST line.
        let mapCol (l: int) (c: int) = if l = start then max 1 (c - p.Prefix) else max 1 c
        let shift = start - 1 - p.LeadPad
        let line = max 1 (sl - shift)
        let col = mapCol sl d.Span.StartCol
        let endLine = max line (d.Span.EndLine - shift)
        let endCol = if d.Span.EndCol >= 1 then mapCol d.Span.EndLine d.Span.EndCol else col
        { Severity = sev; Line = line; Col = col
          EndLine = endLine; EndCol = max 1 endCol
          Message = d.Message; Code = d.Code }

/// A failure with nothing to point at still has to SAY something. A front-end
/// rejection arrives as spanned diagnostics, but a runtime guard panic and a
/// toolchain failure arrive on stderr with no position at all -- and a client
/// that builds its error card from `Diagnostics.[0]` would show "rejected"
/// with no reason. Those become ONE 1:1 error diagnostic carrying the message
/// verbatim; the text stays on `Stderr` as well, for a client that shows both.
let private ensureFailureDiagnostic (r: EvalResult) : EvalResult =
    if r.Kept || r.Stderr.Trim() = "" then r
    elif r.Diagnostics |> List.exists (fun d -> d.Severity = "error") then r
    else
        { r with
            Diagnostics =
                { Severity = "error"; Line = 1; Col = 1; EndLine = 1; EndCol = 1
                  Message = r.Stderr.Trim(); Code = "" } :: r.Diagnostics }

// The engine.

/// One accumulating REPL session: the snippet list, the temp directory its
/// assembled program is written to, and eval-once. Independent instances share
/// nothing, which is what lets `ide serve` hold one per notebook.
type ReplSession(runCwd: string) =
    let sessionDir =
        Path.Combine(Path.GetTempPath(), "blade-repl-" + Guid.NewGuid().ToString("N").Substring(0, 8))
    do Directory.CreateDirectory sessionDir |> ignore
    let srcPath = Path.Combine(sessionDir, "session.blade")
    let snippets = ResizeArray<string>()

    /// Where a COMPILED session executable runs. The interpreter lane ignores
    /// it; the g++ lane needs it so relative data paths resolve where the user
    /// is, not in the session temp dir.
    member val RunCwd = runCwd with get, set

    member _.SessionDir = sessionDir
    member _.SourcePath = srcPath

    /// The accumulated snippets, in session order. Read-only by convention:
    /// the front ends use it for `:show` and the engine replaces it wholesale
    /// through `Commit`.
    member _.Snippets = snippets

    member _.Reset() = snippets.Clear()

    /// Adopt a candidate as the new session. Only ever called for a candidate
    /// that ran to exit 0.
    member _.Commit(candidate: ResizeArray<string>) =
        snippets.Clear()
        snippets.AddRange candidate

    member _.Cleanup() =
        try Directory.Delete(sessionDir, true) with _ -> ()

    /// The candidate a DECLARATION produces: rebinding replaces the earlier
    /// definition IN PLACE so later snippets referencing the name see the
    /// update (duplicate lets are a C++ redeclaration error). Returns the list
    /// and the index the snippet landed at.
    member _.DeclarationCandidate(trimmed: string) : ResizeArray<string> * int =
        let candidate = ResizeArray(snippets)
        match bindingName trimmed with
        | Some name ->
            let idx = candidate.FindIndex(fun s -> bindingName s = Some name)
            if idx >= 0 then
                candidate.[idx] <- trimmed
                (candidate, idx)
            else
                candidate.Add trimmed
                (candidate, candidate.Count - 1)
        | None ->
            candidate.Add trimmed
            (candidate, candidate.Count - 1)

    /// The candidate a REASSIGNMENT produces. A bare assignment does not parse
    /// at top level, but wrapping it in a hidden binding does -- the wrapper's
    /// value IS the ExprAssign, which mutates the target's existing cell.
    /// Unlike bare expressions the wrapper is KEPT so the mutation persists;
    /// successive assignments append under fresh __assignN names, so
    /// `b = b + 1` twice accumulates 1->2->3. Returns the list, the index, and
    /// the hidden name.
    member _.AssignmentCandidate(trimmed: string) : ResizeArray<string> * int * string =
        let candidate = ResizeArray(snippets)
        let hidden =
            let inUse = candidate |> Seq.choose bindingName |> Set.ofSeq
            Seq.initInfinite (fun i -> if i = 0 then "__assign" else sprintf "__assign%d" i)
            |> Seq.find (fun n -> not (Set.contains n inUse))
        candidate.Add (sprintf "let %s = %s" hidden trimmed)
        (candidate, candidate.Count - 1, hidden)

    /// The candidate a BARE EXPRESSION produces: `blade run` semantics only
    /// print top-level BINDINGS, so the expression is wrapped in a transient
    /// one that is run, echoed, and NOT kept -- re-entering the same expression
    /// echoes again rather than diffing to silence. Returns the list, the
    /// index, and the transient name.
    member _.ExpressionCandidate(trimmed: string) : ResizeArray<string> * int * string =
        let transient =
            let inUse = snippets |> Seq.choose bindingName |> Set.ofSeq
            Seq.initInfinite (fun i -> if i = 0 then "it" else sprintf "it%d" i)
            |> Seq.find (fun n -> not (Set.contains n inUse))
        let candidate = ResizeArray(snippets)
        candidate.Add (sprintf "let %s = %s" transient trimmed)
        (candidate, candidate.Count - 1, transient)

    /// Evaluate one candidate. INTERP-FIRST: the candidate lowers ONCE
    /// (shared with the type-annotation map), then runs under the tree-walking
    /// interpreter. On a supported exit its output is authoritative and no g++
    /// is invoked -- a typical turn drops from ~1-5s to <100ms. If the
    /// interpreter can't yet evaluate some node (125) or hits its own bug (70)
    /// it falls back to a g++ compile+run for this one input.
    ///
    /// `target` is the binding whose output line to hand back as the echo;
    /// `notice` receives the one-line fallback announcement BEFORE the g++
    /// build starts, so an interactive caller can show it while waiting.
    member this.EvalCandidate(candidate: ResizeArray<string>, target: string option,
                              notice: string -> unit) : CandidateOutcome =
        let src = String.concat "\n\n" candidate + "\n"
        File.WriteAllText(srcPath, src)
        let watch = System.Diagnostics.Stopwatch.StartNew()
        match Blade.Interp.Repl.lowerSessionDiag (Some srcPath) src with
        | Error (ds, sm) ->
            watch.Stop()
            CandidateRejected (ds, sm)
        | Ok lowered ->
            let info = ReplTypes.sessionInfoOf lowered
            // Drained HERE, where `lower` would have printed: the channels are
            // AsyncLocal and reset per typeCheck, and nothing between this
            // point and the run touches them.
            let warnings = Blade.Lowering.typeCheckWarningDiagnostics false
            let finish (lane: Lane) (code: int) (stdout: string) (stderr: string) =
                watch.Stop()
                let lines =
                    stdout.Replace("\r\n", "\n").Split('\n')
                    |> Array.filter (fun l -> not (isTimingLine l))
                let echo =
                    target
                    |> Option.bind (fun tgt ->
                        lines
                        |> Array.tryFind (fun l ->
                            let m = outNameRe.Match l
                            m.Success && m.Groups.[1].Value = tgt))
                CandidateRan
                    { ExitCode = code; Lane = lane; ElapsedMs = int watch.ElapsedMilliseconds
                      Stdout = stdout; Stderr = stderr; Lines = lines; Echo = echo
                      Info = info
                      Warnings = (if lane = LaneInterp then warnings else []) }
            match Blade.Interp.Repl.evalSession lowered "session" with
            | Blade.Interp.Repl.InterpDone r ->
                // Interpreter is authoritative (exit 0 or guard panic 1).
                finish LaneInterp r.ExitCode r.Stdout r.Stderr
            | Blade.Interp.Repl.InterpFellShort _ ->
                notice "-- falling back to compiled evaluation for this input --"
                match compiledLane with
                | None ->
                    watch.Stop()
                    CandidateFailed "compiled evaluation is unavailable in this process"
                | Some run ->
                    match run srcPath this.RunCwd with
                    | Error msg ->
                        watch.Stop()
                        CandidateFailed msg
                    | Ok (code, stdout, stderr) -> finish LaneGpp code stdout stderr

    /// Evaluate one submission with full REPL semantics and hand back
    /// structured data -- the notebook's entry point. Classification,
    /// splicing, evaluation, commit and diagnostic rebasing all happen here;
    /// nothing is printed and nothing is rendered.
    member this.EvalOnce(source: string) : EvalResult =
        let trimmed = source.Trim()
        let blank =
            { Kept = true; ExitCode = 0; Lane = LaneInterp; ElapsedMs = 0
              Stdout = ""; Stderr = ""; Bindings = []; Diagnostics = [] }
        if trimmed = "" then blank else
        // `Trim()` may have eaten leading blank lines; the client's cell
        // coordinates still count them.
        let leadPad =
            let cut = source.Length - source.TrimStart().Length
            source.Substring(0, cut).Replace("\r\n", "\n") |> Seq.filter (fun c -> c = '\n') |> Seq.length
        let head = classifyTarget trimmed
        /// Run a candidate and project it onto the submission. `wanted` pairs
        /// the name to REPORT with the name the session actually bound it
        /// under (they differ only for a bare expression, whose transient
        /// wrapper reports as ""). `commit` is false for the bare-expression
        /// lane, which never joins the session.
        let evalWith (candidate: ResizeArray<string>) (placement: Placement)
                     (target: string option) (wanted: (string * string) list) (commit: bool) =
            ensureFailureDiagnostic <|
            match this.EvalCandidate(candidate, target, ignore) with
            | CandidateRejected (ds, _) ->
                { Kept = false; ExitCode = 1; Lane = LaneInterp; ElapsedMs = 0
                  Stdout = ""; Stderr = ""; Bindings = []
                  Diagnostics = ds |> List.map (remapDiagnostic candidate placement) }
            | CandidateFailed msg ->
                { Kept = false; ExitCode = 1; Lane = LaneGpp; ElapsedMs = 0
                  Stdout = ""; Stderr = msg; Bindings = []; Diagnostics = [] }
            | CandidateRan r ->
                let valueOf (name: string) =
                    r.Lines
                    |> Array.tryPick (fun l ->
                        let m = outNameRe.Match l
                        if m.Success && m.Groups.[1].Value = name then
                            Some (ReplTypes.elideValue (l.Substring m.Length))
                        else None)
                    |> Option.defaultValue ""
                let typeOf (name: string) =
                    match Map.tryFind name r.Info with
                    | Some (ReplTypes.RVal t) -> Blade.Ide.abstractRenderer [] t
                    | Some (ReplTypes.RFunc s) -> s
                    | None -> ""
                let kept = (r.ExitCode = 0)
                if kept && commit then this.Commit candidate
                { Kept = kept
                  ExitCode = r.ExitCode
                  Lane = r.Lane
                  ElapsedMs = r.ElapsedMs
                  Stdout =
                    let userOut = r.Lines |> Array.filter (fun l -> not (outNameRe.IsMatch l))
                    let joined = String.concat "\n" userOut
                    if joined.Trim() = "" then "" else joined
                  Stderr = r.Stderr.Trim()
                  Bindings =
                    if kept then
                        wanted |> List.map (fun (report, bound) ->
                            { Name = report; Type = typeOf bound; Value = valueOf bound })
                    else []
                  Diagnostics = r.Warnings |> List.map (remapDiagnostic candidate placement) }
        if declRe.IsMatch head then
            let (candidate, idx) = this.DeclarationCandidate trimmed
            // A :paste block may declare several names; every one of them is a
            // binding this submission made, and the LAST is what the REPL echoes.
            let names =
                trimmed.Replace("\r\n", "\n").Split('\n')
                |> Array.choose bindingName
                |> Array.toList
            let target = List.tryLast names
            evalWith candidate { Index = idx; Prefix = 0; LeadPad = leadPad }
                     target (names |> List.map (fun n -> (n, n))) true
        elif assignRe.IsMatch head then
            let (candidate, idx, hidden) = this.AssignmentCandidate trimmed
            let root = (assignRe.Match trimmed).Groups.[1].Value
            evalWith candidate
                     { Index = idx; Prefix = (sprintf "let %s = " hidden).Length; LeadPad = leadPad }
                     (Some root) [ (root, root) ] true
        else
            // A bare identifier naming a session FUNCTION can't be let-bound
            // just to echo it; its signature comes straight from the
            // typechecker and nothing runs.
            let asFuncSig =
                if identRe.IsMatch trimmed then
                    match Map.tryFind trimmed (ReplTypes.sessionInfo (String.concat "\n\n" snippets + "\n")) with
                    | Some (ReplTypes.RFunc s) -> Some s
                    | _ -> None
                else None
            match asFuncSig with
            | Some s ->
                { blank with Bindings = [ { Name = trimmed; Type = s; Value = "" } ] }
            | None ->
                let (candidate, idx, transient) = this.ExpressionCandidate trimmed
                evalWith candidate
                         { Index = idx; Prefix = (sprintf "let %s = " transient).Length; LeadPad = leadPad }
                         (Some transient) [ ("", transient) ] false
