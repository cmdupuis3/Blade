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
