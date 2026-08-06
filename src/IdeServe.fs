// IdeServe.fs: `blade ide serve` -- the persistent twin of `ide check --json`.
//
// The editor used to re-spawn the compiler on every save; as-you-type checking
// cannot afford that, so this is one long-lived process speaking NDJSON over
// stdin/stdout (one JSON object per line, in both directions):
//
//   -> {"id":N,"cmd":"check","tier":"fast"|"full","file":"<abs path>","source":"<buffer>"}
//   <- the ide check payload, plus top-level "id" and "tier"
//   -> {"id":N,"cmd":"ping"}     <- {"id":N,"ok":true,"serve":1,"version":"..."}
//   -> {"cmd":"shutdown"}        (no response; also exits cleanly on stdin EOF)
//   <- {"id":N|null,"error":"<message>"} for anything malformed
//
// ...plus the NOTEBOOK lane, which evaluates cells with REPL semantics:
//
//   -> {"id":N,"cmd":"eval","session":"<key>","source":"<cell>","cwd":"<dir>"?}
//   <- {"id":N,"kept":B,"exitCode":E,"lane":"interp"|"gpp","elapsedMs":M,
//       "stdout":"..","stderr":"..","bindings":[{name,type,value}],
//       "diagnostics":[{severity,line,col,endLine,endCol,message,code?}]}
//   -> {"id":N,"cmd":"resetSession","session":"<key>"}   <- {"id":N,"ok":true}
//
// One Blade.ReplSession per `session` key -- a notebook's cells accumulate,
// two notebooks never see each other, and every session dies with the process.
// Diagnostic coordinates are CELL-LOCAL: the compiler reports against the
// assembled session file and the engine rebases them onto the submission.
// An UNKNOWN cmd still answers `{"id":N,"error":...}`, which is how an old
// extension probes a new compiler and vice versa -- so that arm is contract,
// not just courtesy.
//
// stderr stays free-form logging: the client ignores it.
//
// The two tiers are where the pipeline STOPS. Fast = parse + typecheck +
// deduce, exactly what `ide check` has always run -- the editor's squiggle
// clock. Full = fast plus lowering/monomorphization, which is the only stage
// that resolves an HM-polymorphic value binding's `T` down to a concrete type,
// so it feeds `concreteType` upgrades on a slower clock.
//
// Compiles after Lowering.fs (the full tier calls it) and Ide.fs (whose
// payload it wraps), and before Cli.fs, which dispatches `ide serve`.
module Blade.IdeServe

open System
open System.IO
open System.Text
open System.Text.Json

// Full tier

/// Top-level binding name -> its type as monomorphization left it, rendered
/// with Ide.fs's own printers so a `concreteType` never disagrees in spelling
/// with the `type` beside it. A FRESH abstract renderer per binding: schemes
/// don't share inference ids across bindings, so per-binding letter
/// namespaces can't collide (Ide.collectTypedBindings makes the same call).
let private concreteValueTypes (ir: Blade.IR.IRProgram) : Map<string, string> =
    Map.ofList
        [ for m in ir.Modules do
            for b in m.Bindings do
                yield (b.Name, Blade.Ide.abstractRenderer [] b.Type) ]

/// The full tier's extra pass: the same lower + validateIR chain
/// `Interp.Repl.lowerSession` drives, but keeping failures STRUCTURED (code +
/// message) instead of rendering them rustc-style -- the IDE payload wants
/// diagnostics it can squiggle, not a formatted console block. Lowering can
/// THROW rather than return Error (a provider load that fails at compile
/// time), so the call is guarded.
let fullTierUpgrade : Blade.Ide.FullTierUpgrade =
    fun prog typed builder ->
        match (try Ok (Blade.Lowering.lowerTypedProgram typed (Some prog) builder)
               with ex -> Error [ ("BL6002", ex.Message) ]) with
        | Error failures -> Error failures
        | Ok ir ->
            match Blade.IR.validateIR ir with
            | Error errs -> Error (errs |> List.map (fun e -> ("BL6001", e)))
            | Ok validated -> Ok (concreteValueTypes validated)

// Request decoding (System.Text.Json in, hand-rolled JSON out -- the payload
// itself is built by Ide.renderJson, so there is only ever one emitter).

let private tryProp (root: JsonElement) (name: string) : JsonElement option =
    match root.TryGetProperty name with
    | true, v -> Some v
    | _ -> None

let private tryStr (root: JsonElement) (name: string) : string option =
    tryProp root name
    |> Option.bind (fun v ->
        if v.ValueKind = JsonValueKind.String then Some (v.GetString()) else None)

let private tryInt (root: JsonElement) (name: string) : int option =
    tryProp root name
    |> Option.bind (fun v ->
        if v.ValueKind <> JsonValueKind.Number then None
        else match v.TryGetInt32() with
             | true, n -> Some n
             | _ -> None)

// The notebook lane's response encoder. Hand-rolled like the error response
// above: one line, no pretty printing, `Ide.jsonEscape` on every string that
// came from user source or a diagnostic.

let private laneName (l: Blade.ReplSession.Lane) =
    match l with
    | Blade.ReplSession.LaneInterp -> "interp"
    | Blade.ReplSession.LaneGpp -> "gpp"

let private evalResponse (id: int) (r: Blade.ReplSession.EvalResult) : string =
    let sb = StringBuilder()
    let esc = Blade.Ide.jsonEscape
    sb.AppendFormat(
        "{{\"id\":{0},\"kept\":{1},\"exitCode\":{2},\"lane\":\"{3}\",\"elapsedMs\":{4}",
        id, (if r.Kept then "true" else "false"), r.ExitCode, laneName r.Lane, r.ElapsedMs) |> ignore
    sb.AppendFormat(",\"stdout\":\"{0}\",\"stderr\":\"{1}\",\"bindings\":[",
                    esc r.Stdout, esc r.Stderr) |> ignore
    r.Bindings
    |> List.iteri (fun i b ->
        if i > 0 then sb.Append ',' |> ignore
        sb.AppendFormat("{{\"name\":\"{0}\",\"type\":\"{1}\",\"value\":\"{2}\"}}",
                        esc b.Name, esc b.Type, esc b.Value) |> ignore)
    sb.Append "],\"diagnostics\":[" |> ignore
    r.Diagnostics
    |> List.iteri (fun i d ->
        if i > 0 then sb.Append ',' |> ignore
        sb.AppendFormat(
            "{{\"severity\":\"{0}\",\"line\":{1},\"col\":{2},\"endLine\":{3},\"endCol\":{4},\"message\":\"{5}\"",
            d.Severity, d.Line, d.Col, d.EndLine, d.EndCol, esc d.Message) |> ignore
        if d.Code <> "" then
            sb.AppendFormat(",\"code\":\"{0}\"", esc d.Code) |> ignore
        sb.Append '}' |> ignore)
    sb.Append "]}" |> ignore
    sb.ToString()

// The loop

/// One request at a time, deliberately: `Parser.currentFile` /
/// `Parser.lastTokenEnd` and `Ast.synthSpan` are plain mutable globals, and
/// serializing is what keeps a daemon honest about them. Factored over
/// TextReader/TextWriter so the test suite can drive a whole conversation
/// in-process without spawning anything.
let serveLoop (version: string) (input: TextReader) (output: TextWriter) : int =
    // Exactly one \n-terminated line per response, flushed before the next
    // read: the client frames on newlines and correlates by id. WriteLine is
    // avoided on purpose -- it would emit CRLF on Windows.
    let respond (line: string) =
        output.Write line
        output.Write '\n'
        output.Flush ()
    let errorResponse (id: int option) (msg: string) =
        let idJson = match id with Some i -> string i | None -> "null"
        respond (sprintf "{\"id\":%s,\"error\":\"%s\"}" idJson (Blade.Ide.jsonEscape msg))
    // Provider relative data paths resolve against the process working
    // directory, which one-shot `ide check` inherited from the editor (= the
    // checked file's directory). A persistent process has to reproduce that
    // per request. `file` is whatever the client sent and an unsaved buffer's
    // path need not exist, so the move is guarded rather than trusted.
    let setCwdFor (file: string) =
        try
            let dir = Path.GetDirectoryName(Path.GetFullPath file)
            if not (String.IsNullOrEmpty dir) && Directory.Exists dir then
                Directory.SetCurrentDirectory dir
        with _ -> ()
    let runCheck (id: int) (tier: string) (file: string) (source: string) =
        setCwdFor file
        // typeCheck resets its own AsyncLocal side-channels; these two it does
        // not touch. IdeStores would otherwise carry a PREVIOUS file's provider
        // stores into this payload, and `provenance` is an append-only fold log
        // that would grow without bound in a process that never exits. The
        // mtime-keyed caches beside it are left alone -- they were built for
        // exactly this daemon shape.
        Blade.ProviderRegistry.IdeStores.reset ()
        Blade.ProviderStatics.provenance.Clear ()
        let env : Blade.Ide.Envelope = { Id = Some id; Tier = Some tier }
        let upgrade = if tier = "full" then Some fullTierUpgrade else None
        // The exit code is dropped: on this wire the diagnostics array carries
        // the same verdict, and the process outlives the check.
        let (json, _) = Blade.Ide.ideCheckSourceWith env upgrade file source
        respond json
    // One engine per `session` key, alive for the life of the process. The
    // sessions hold nothing but source snippets and a temp directory, so an
    // interleaved `check` cannot disturb them: each eval re-lowers the whole
    // session from its own list, and typeCheck resets its AsyncLocal channels
    // on the way in.
    let sessions = System.Collections.Generic.Dictionary<string, Blade.ReplSession.ReplSession>()
    let runEval (id: int) (key: string) (source: string) (cwd: string option) =
        // The notebook's folder, so a provider's relative data path resolves
        // where the user's data actually is. Same guarded move as `check`,
        // which resolves against the request file's directory instead.
        match cwd with
        | Some dir when Directory.Exists dir ->
            (try Directory.SetCurrentDirectory dir with _ -> ())
        | _ -> ()
        // The same per-request hygiene the check handler applies: a PREVIOUS
        // request's provider stores must not follow this evaluation, and
        // `provenance` would otherwise grow without bound.
        Blade.ProviderRegistry.IdeStores.reset ()
        Blade.ProviderStatics.provenance.Clear ()
        let session =
            match sessions.TryGetValue key with
            | true, s -> s
            | _ ->
                let s = Blade.ReplSession.ReplSession(Directory.GetCurrentDirectory())
                sessions.[key] <- s
                s
        // Only the g++ lane reads this, and only to run the built executable.
        session.RunCwd <- Directory.GetCurrentDirectory()
        respond (evalResponse id (session.EvalOnce source))
    /// Handle one line; false means "stop the loop".
    let handle (line: string) : bool =
        use doc = JsonDocument.Parse line
        let root = doc.RootElement
        if root.ValueKind <> JsonValueKind.Object then
            errorResponse None "request must be a JSON object"
            true
        else
            let id = tryInt root "id"
            match tryStr root "cmd" with
            | Some "shutdown" -> false
            | Some "ping" ->
                (match id with
                 | Some i ->
                     respond (sprintf "{\"id\":%d,\"ok\":true,\"serve\":1,\"version\":\"%s\"}"
                                      i (Blade.Ide.jsonEscape version))
                 | None -> errorResponse None "\"ping\" requires an integer \"id\"")
                true
            | Some "check" ->
                (match id, tryStr root "file", tryStr root "source" with
                 | None, _, _ -> errorResponse None "\"check\" requires an integer \"id\""
                 | Some i, None, _ -> errorResponse (Some i) "\"check\" requires a \"file\" path"
                 | Some i, _, None -> errorResponse (Some i) "\"check\" requires a \"source\" string"
                 | Some i, Some file, Some source ->
                     match defaultArg (tryStr root "tier") "fast" with
                     | ("fast" | "full") as tier -> runCheck i tier file source
                     | other ->
                         errorResponse (Some i)
                             (sprintf "unknown tier '%s' (expected \"fast\" or \"full\")" other))
                true
            | Some "eval" ->
                (match id, tryStr root "session", tryStr root "source" with
                 | None, _, _ -> errorResponse None "\"eval\" requires an integer \"id\""
                 | Some i, None, _ -> errorResponse (Some i) "\"eval\" requires a \"session\" key"
                 | Some i, _, None -> errorResponse (Some i) "\"eval\" requires a \"source\" string"
                 | Some i, Some key, Some source -> runEval i key source (tryStr root "cwd"))
                true
            | Some "resetSession" ->
                // Idempotent by design: "restart kernel" fires before the
                // notebook has evaluated anything, and an unknown key simply
                // has no state to clear.
                (match id, tryStr root "session" with
                 | None, _ -> errorResponse None "\"resetSession\" requires an integer \"id\""
                 | Some i, None -> errorResponse (Some i) "\"resetSession\" requires a \"session\" key"
                 | Some i, Some key ->
                     (match sessions.TryGetValue key with
                      | true, s -> s.Reset()
                      | _ -> ())
                     respond (sprintf "{\"id\":%d,\"ok\":true}" i))
                true
            | Some other ->
                errorResponse id (sprintf "unknown cmd '%s'" other)
                true
            | None ->
                errorResponse id "request has no \"cmd\" string"
                true
    // Restored on the way out because the suite drives this loop in-process,
    // and a leaked chdir would follow every later test.
    let entryDir = try Some (Directory.GetCurrentDirectory()) with _ -> None
    try
        try
            let mutable running = true
            while running do
                // EOF (a closed stdin) is a clean shutdown, same as the verb.
                match input.ReadLine() with
                | null -> running <- false
                | line ->
                    // Tolerate the \r of a CRLF client, and ignore blank lines.
                    let trimmed = line.Trim()
                    if trimmed <> "" then
                        // NO input may kill the loop: the editor keeps typing
                        // through malformed frames and internal failures alike.
                        try running <- handle trimmed
                        with ex ->
                            // Bad framing is the client's business and gets one
                            // line; anything else is ours and earns the stack.
                            match ex with
                            | :? JsonException -> eprintfn "[ide serve] bad request: %s" ex.Message
                            | _ -> eprintfn "[ide serve] %s" (ex.ToString())
                            errorResponse (None: int option) ex.Message
        with :? IOException ->
            // The client went away mid-write; nothing left to say.
            ()
        0
    finally
        // Each session owns a temp directory; the loop outliving them all is
        // the only chance to remove it.
        for kv in sessions do kv.Value.Cleanup()
        sessions.Clear()
        match entryDir with
        | Some d -> (try Directory.SetCurrentDirectory d with _ -> ())
        | None -> ()

/// `blade ide serve`: wire the loop to the real console streams.
let serve (version: string) : int =
    // NDJSON is UTF-8 by contract; a redirected stdout on Windows would
    // otherwise inherit the console codepage. No BOM -- it would corrupt the
    // first response line.
    let out = new StreamWriter(Console.OpenStandardOutput(), UTF8Encoding(false))
    out.AutoFlush <- false
    // Any stray printfn from a deep compiler phase then lands in the SAME
    // buffer, in order, instead of racing a second writer onto the handle.
    Console.SetOut out
    let inp = new StreamReader(Console.OpenStandardInput(), UTF8Encoding(false))
    try serveLoop version inp out
    finally out.Flush ()
