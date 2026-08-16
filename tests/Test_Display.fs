// Display frames (Blade-REPL/docs/display-frames.md): the corpus categories
// plus the in-process block that pins the parts a .blade file cannot reach.
//
// The corpus half (`displayTests` / `displayErrorTests`) covers the SURFACE --
// that `display.emit(mime, data)` compiles, returns `true`, and rejects the
// three malformed usages -- and, through `blade test interp`, that the
// interpreter and the compiled binary put byte-identical frames on stdout.
//
// This file's own block covers what the corpus cannot:
//
//   * the exact BYTES of a frame (a corpus EXPECT only sees binding values);
//   * the escape table, including the sentinel's own SOH, on payloads that do
//     not have to survive Blade's string lexer first;
//   * the two CHANNELS -- the REPL's sentinel line on raw interpreter stdout
//     (spec section 4), and `ide serve`'s `display` array (spec section 2),
//     pinned on the real response encoder rather than a paraphrase of it.
module Blade.Tests.Display

open Blade.Tests.Corpus
open Blade.Tests.TestHarness

/// display-module tests (emit surface, encodings, both back ends).
let displayTests = category "display"

/// display-module reject probes (non-String payload, non-literal mime,
/// malformed mime). Their own category, like unit-errors and
/// mutability-errors: these never reach codegen, so they must not sit in the
/// category that feeds the interpreter differential.
let displayErrorTests = category "display-errors"

// The in-process block.

module F = Blade.Display.Frame

/// One check: name, condition, and the detail printed on failure.
let private checks = ResizeArray<string * (unit -> bool * string)>()
let private add name fn = checks.Add((name, fn))

let private eq (expected: string) (actual: string) =
    (expected = actual), (if expected = actual then "" else sprintf "expected %s, got %s" expected actual)

// ---- 1. The frame format ----------------------------------------------------

add "sentinel is SOH + 'blade-display' + SOH (15 bytes)" (fun () ->
    let codes = F.Sentinel |> Seq.map (fun c -> string (int c)) |> String.concat ","
    eq "1,98,108,97,100,101,45,100,105,115,112,108,97,121,1" codes)

add "encoding inferred: +json mime -> json" (fun () ->
    eq "json" (F.encodingFor "application/vnd.plotly.v1+json"))

add "encoding inferred: application/json -> json" (fun () ->
    eq "json" (F.encodingFor "application/json"))

add "encoding inferred: text/* -> utf8" (fun () ->
    eq "utf8" (F.encodingFor "text/html"))

add "encoding inferred: everything else -> base64" (fun () ->
    eq "base64" (F.encodingFor "image/png"))

add "mime grammar accepts type/subtype, rejects the rest" (fun () ->
    let ok = F.isMimeType "application/vnd.plotly.v1+json" && F.isMimeType "image/png"
    let bad = F.isMimeType "plotly" || F.isMimeType "a/b/c" || F.isMimeType "/png" || F.isMimeType ""
    (ok && not bad), sprintf "ok=%b bad=%b" ok bad)

add "escape covers quote, backslash and the sentinel's own SOH" (fun () ->
    // The SOH escape is the load-bearing one: it is what makes a payload
    // unable to forge a frame boundary, which is why the format needs no
    // escaping scheme of its own.
    eq "a\\\"b\\\\c\\u0001d\\n" (F.escape "a\"b\\cd\n"))

add "jsonString supplies its own quotes and escapes what is inside them" (fun () ->
    // The caller writes `"\"text\":" + jsonString t` -- delimiters included is
    // the whole point, because the bug this retires was a caller writing them
    // by hand around unescaped text.
    eq "\"he said \\\"hi\\\"\\\\\"" (F.jsonString "he said \"hi\"\\"))

add "jsonString of the empty string is a pair of quotes" (fun () ->
    eq "\"\"" (F.jsonString ""))

add "jsonNumber passes a finite rendering through untouched" (fun () ->
    eq "2.25" (F.jsonNumber "2.25" 2.25))

add "jsonNumber turns NaN and both infinities into null" (fun () ->
    // The guard reads the VALUE, not the rendering. The spelling of a
    // non-finite is implementation-defined (`nan`, `-nan`, `NaN`, `1.#QNAN`),
    // so a text test would pin the JSON rule to whichever formatter happened
    // to be underneath; both lanes branch on the finite predicate instead.
    let vals = [ nan; infinity; -infinity; -nan ]
    let outs = vals |> List.map (fun x -> F.jsonNumber "SHOULD-NOT-APPEAR" x)
    eq "null,null,null,null" (String.concat "," outs))

add "head carries v, mime and the inferred encoding" (fun () ->
    eq "{\"v\":1,\"mime\":\"image/png\",\"encoding\":\"base64\",\"data\":" (F.headFor "image/png"))

add "metaTailOf strips the braces and leads with a comma" (fun () ->
    eq ",\"title\":\"x\"" (defaultArg (F.metaTailOf "{\"title\":\"x\"}") "<none>"))

add "metaTailOf turns an empty object into an empty tail" (fun () ->
    eq "" (defaultArg (F.metaTailOf "{}") "<none>"))

add "metaTailOf rejects a non-object" (fun () ->
    let r = F.metaTailOf "\"title\""
    r.IsNone, sprintf "got %A" r)

add "a json-encoded frame inlines its payload unquoted" (fun () ->
    eq (F.Sentinel + "{\"v\":1,\"mime\":\"application/vnd.plotly.v1+json\",\"encoding\":\"json\",\"data\":{\"z\":[1]},\"meta\":{\"id\":\"blade-3\",\"title\":\"t\"}}")
       (F.composeLine (F.headFor "application/vnd.plotly.v1+json") false "{\"z\":[1]}" ",\"title\":\"t\"" 3))

add "a base64 frame quotes and escapes its payload" (fun () ->
    eq (F.Sentinel + "{\"v\":1,\"mime\":\"image/png\",\"encoding\":\"base64\",\"data\":\"iVBOR\",\"meta\":{\"id\":\"blade-1\"}}")
       (F.composeLine (F.headFor "image/png") true "iVBOR" "" 1))

add "the IR node is marked IMPURE (a future CSE/hoist must not drop it)" (fun () ->
    let node =
        Blade.IR.IRDisplayEmit (F.headFor "image/png", true,
                                Blade.IR.IRLit (Blade.IR.IRLitString "AA=="), "")
    let a = Blade.IR.exprAttrs node
    (not a.IsPure) && (Blade.IR.exprAttrs (Blade.IR.IRLit (Blade.IR.IRLitInt 1L))).IsPure,
    sprintf "IsPure = %b" a.IsPure)

// ---- 2. The REPL channel (spec section 4) -----------------------------------

/// Lower + interpret one source, returning the interpreter's RAW stdout --
/// i.e. what `blade repl` prints and what the extension's scanner reads.
/// The synthetic cwd-anchored entry path is what lets `import plot` /
/// `import units.SI` resolve against <repo>/stdlib exactly as a real
/// session does (lowerSession only resolves file imports when given a path).
let private interpStdout (source: string) : Result<string, string> =
    let entry =
        System.IO.Path.Combine(System.IO.Directory.GetCurrentDirectory(), "__display_tests__.blade")
    match Blade.Interp.Repl.lowerSession (Some entry) false source with
    | Error msg -> Error msg
    | Ok lowered ->
        match Blade.Interp.Repl.evalSession lowered "session" with
        | Blade.Interp.Repl.InterpDone r -> Ok r.Stdout
        | Blade.Interp.Repl.InterpFellShort f -> Error ("interpreter fell short: " + f)

let private emitSource =
    "import display as d\n\
     let ok = d.emit(\"application/vnd.plotly.v1+json\", \"{\\\"z\\\":[1,2]}\", \"{\\\"title\\\":\\\"t\\\"}\")\n"

add "REPL channel: the frame is a whole line at column 0 of stdout" (fun () ->
    match interpStdout emitSource with
    | Error e -> false, e
    | Ok out ->
        let lines = out.Replace("\r\n", "\n").Split('\n')
        match lines |> Array.tryFindIndex (fun l -> l.StartsWith F.Sentinel) with
        | None -> false, sprintf "no sentinel line in %A" lines
        | Some i ->
            // Column 0 by construction, and NOTHING but the frame on the line.
            let l = lines.[i]
            let json = l.Substring F.Sentinel.Length
            (json.StartsWith "{" && json.EndsWith "}" && not (json.Contains "\n")),
            sprintf "line %d = %s" i json)

add "REPL channel: the frame precedes the binding prints" (fun () ->
    // The compiled binary emits inside main()'s body, ahead of the timing line
    // and the print block; the interpreter has to agree or `blade test interp`
    // fails. Pinning the ORDER here catches the divergence without needing g++.
    match interpStdout emitSource with
    | Error e -> false, e
    | Ok out ->
        let lines = out.Replace("\r\n", "\n").Split('\n')
        let frameAt = lines |> Array.tryFindIndex (fun l -> l.StartsWith F.Sentinel)
        let bindAt = lines |> Array.tryFindIndex (fun l -> l.StartsWith "ok = ")
        match frameAt, bindAt with
        | Some f, Some b -> (f < b), sprintf "frame at %d, binding at %d" f b
        | _ -> false, sprintf "missing line(s) in %A" lines)

add "REPL channel: ids count emissions and repeat across runs" (fun () ->
    // Two runs of the same program must produce the same ids -- that is what
    // makes a REPL session's re-run update the panel's plots in place.
    let src =
        "import display as d\n\
         let a = d.emit(\"image/png\", \"AA==\")\n\
         let b = d.emit(\"image/png\", \"BB==\")\n"
    let idsOf () =
        match interpStdout src with
        | Error e -> Error e
        | Ok out ->
            Ok (out.Replace("\r\n", "\n").Split('\n')
                |> Array.filter (fun l -> l.StartsWith F.Sentinel)
                |> Array.map (fun l ->
                    let i = l.IndexOf "\"id\":\""
                    l.Substring(i + 6, l.IndexOf("\"", i + 6) - i - 6))
                |> String.concat ",")
    match idsOf (), idsOf () with
    | Ok a, Ok b -> (a = "blade-1,blade-2" && a = b), sprintf "run1=%s run2=%s" a b
    | Error e, _ | _, Error e -> false, e)

add "REPL channel: a program that never emits produces no sentinel" (fun () ->
    match interpStdout "let x = 1\n" with
    | Error e -> false, e
    | Ok out -> (not (out.Contains F.Sentinel)), out)

// ---- 3. The serve channel (spec section 2) ----------------------------------

/// One submission through the real REPL session engine -- the same call
/// `ide serve`'s `eval` command makes.
let private evalOnce (source: string) =
    let session = Blade.ReplSession.ReplSession(System.IO.Path.GetTempPath())
    try session.EvalOnce source
    finally session.Cleanup()

add "serve channel: eval carries the frame in Display" (fun () ->
    let r = evalOnce "import display as d\nlet ok = d.emit(\"image/png\", \"AA==\")"
    match r.Display with
    | [ f ] -> (f.StartsWith "{\"v\":1,\"mime\":\"image/png\"" && f.EndsWith "}"), f
    | other -> false, sprintf "expected 1 frame, got %A" other)

add "serve channel: frames are LIFTED OUT of stdout (never shown twice)" (fun () ->
    let r = evalOnce "import display as d\nlet ok = d.emit(\"image/png\", \"AA==\")"
    (not (r.Stdout.Contains F.Sentinel) && not (r.Stdout.Contains "blade-display")), r.Stdout)

add "serve channel: the submission is still kept and typed as usual" (fun () ->
    let r = evalOnce "import display as d\nlet ok = d.emit(\"image/png\", \"AA==\")"
    let b = r.Bindings |> List.tryFind (fun b -> b.Name = "ok")
    match b with
    | Some b -> (r.Kept && b.Type = "Bool" && b.Value = "true"), sprintf "kept=%b type=%s value=%s" r.Kept b.Type b.Value
    | None -> false, sprintf "no 'ok' binding in %A" r.Bindings)

add "serve channel: a non-emitting submission has an empty Display" (fun () ->
    let r = evalOnce "let x = 1"
    r.Display.IsEmpty, sprintf "%A" r.Display)

add "serve wire: display[] splices raw frame objects, not strings" (fun () ->
    // The frames are already JSON objects; escaping them into the array would
    // hand the reader an array of strings and every frame would be rejected.
    let r = evalOnce "import display as d\nlet ok = d.emit(\"image/png\", \"AA==\")"
    let wire = Blade.IdeServe.evalResponse 7 r
    let doc = System.Text.Json.JsonDocument.Parse wire
    let mutable arr = Unchecked.defaultof<System.Text.Json.JsonElement>
    if not (doc.RootElement.TryGetProperty("display", &arr)) then false, wire
    else
        let items = [ for e in arr.EnumerateArray() -> e ]
        match items with
        | [ one ] ->
            (one.ValueKind = System.Text.Json.JsonValueKind.Object
             && one.GetProperty("mime").GetString() = "image/png"
             && one.GetProperty("encoding").GetString() = "base64"
             && one.GetProperty("data").GetString() = "AA=="
             && one.GetProperty("meta").GetProperty("id").GetString() = "blade-1"),
            wire
        | _ -> false, wire)

add "serve wire: display[] is omitted entirely when nothing was emitted" (fun () ->
    // Backward compatibility in the honest sense: a non-plotting submission's
    // response is byte-identical to what this compiler produced before display
    // frames existed.
    let wire = Blade.IdeServe.evalResponse 7 (evalOnce "let x = 1")
    (not (wire.Contains "display")), wire)

add "serve wire: a json-encoded payload lands as an OBJECT, not a string" (fun () ->
    let r = evalOnce "import display as d\nlet ok = d.emit(\"application/vnd.plotly.v1+json\", \"{\\\"z\\\":[1,2]}\")"
    let wire = Blade.IdeServe.evalResponse 1 r
    let doc = System.Text.Json.JsonDocument.Parse wire
    let f = (doc.RootElement.GetProperty "display").EnumerateArray() |> Seq.head
    let d = f.GetProperty "data"
    (d.ValueKind = System.Text.Json.JsonValueKind.Object
     && (d.GetProperty "z").GetArrayLength() = 2), wire)

// ---- 4. The plot module (stdlib/plot.blade) ---------------------------------
//
// Frame CONTENT checks the corpus cannot express: the payload parses as
// JSON, the trace/coloring/z land where plotly expects them, quantity-tagged
// slots steer the figure, and units.SI axis labels arrive via
// display.unit_label. Driven through the interpreter lane (interpStdout);
// the corpus + `blade test interp` pin compiled-lane byte parity.

let private plotFrame (source: string) : Result<System.Text.Json.JsonDocument, string> =
    match interpStdout source with
    | Error e -> Error e
    | Ok out ->
        let frames =
            out.Replace("\r\n", "\n").Split('\n')
            |> Array.filter (fun l -> l.StartsWith F.Sentinel)
        match frames with
        | [| l |] -> Ok (System.Text.Json.JsonDocument.Parse (l.Substring F.Sentinel.Length))
        | _ -> Error (sprintf "expected exactly 1 frame, got %d" frames.Length)

let private contourSource =
    "import plot\n\
     let ok = plot.contourf([0.0, 1.0, 2.0], [0.0, 1.0], [[0.0, 1.0, 2.25], [3.0, 4.0, 5.5]], 20: levels, \"waves\": title)\n"

add "plot.contourf: one frame, plotly mime, inline-json encoding" (fun () ->
    match plotFrame contourSource with
    | Error e -> false, e
    | Ok doc ->
        let r = doc.RootElement
        ((r.GetProperty "mime").GetString() = "application/vnd.plotly.v1+json"
         && (r.GetProperty "encoding").GetString() = "json"
         && (r.GetProperty "data").ValueKind = System.Text.Json.JsonValueKind.Object),
        r.ToString())

add "plot.contourf: the trace is a fill contour carrying the z grid" (fun () ->
    match plotFrame contourSource with
    | Error e -> false, e
    | Ok doc ->
        let trace = (doc.RootElement.GetProperty("data").GetProperty "data").EnumerateArray() |> Seq.head
        let z = trace.GetProperty "z"
        let row1 = z.EnumerateArray() |> Seq.item 1
        ((trace.GetProperty "type").GetString() = "contour"
         && (trace.GetProperty("contours").GetProperty "coloring").GetString() = "fill"
         && z.GetArrayLength() = 2
         && row1.EnumerateArray() |> Seq.map (fun e -> e.GetDouble()) |> List.ofSeq = [3.0; 4.0; 5.5]),
        trace.ToString())

add "plot.contourf: tagged slots steer ncontours and the layout title" (fun () ->
    match plotFrame contourSource with
    | Error e -> false, e
    | Ok doc ->
        let data = doc.RootElement.GetProperty "data"
        let trace = (data.GetProperty "data").EnumerateArray() |> Seq.head
        ((trace.GetProperty "ncontours").GetInt32() = 20
         && (data.GetProperty("layout").GetProperty("title").GetProperty "text").GetString() = "waves"),
        data.ToString())

add "plot.line + units.SI: unit_label auto-fills the axis titles" (fun () ->
    let src =
        "import units.SI\n\
         import display as d\n\
         import plot\n\
         let ts: Array<Float<second> like Idx<3>> = [0.0, 1.0, 2.0]\n\
         let vs: Array<Float<meter/second^2> like Idx<3>> = [0.0, 9.81, 19.62]\n\
         let ok = plot.line(ts, vs, d.unit_label(ts): xlabel, d.unit_label(vs): ylabel)\n"
    match plotFrame src with
    | Error e -> false, e
    | Ok doc ->
        let layout = doc.RootElement.GetProperty("data").GetProperty "layout"
        ((layout.GetProperty("xaxis").GetProperty("title").GetProperty "text").GetString() = "second"
         && (layout.GetProperty("yaxis").GetProperty("title").GetProperty "text").GetString() = "meter / second^2"),
        layout.ToString())

add "plot: a title with quotes, a backslash and a tab still parses" (fun () ->
    // plotFrame PARSES the payload, so an unescaped title fails this check at
    // the parse, before the comparison -- which is exactly how the bug used to
    // present: one apostrophe-shaped character and the panel got nothing.
    let src =
        "import plot\n\
         let ok = plot.line([0.0, 1.0], [0.0, 1.0], \"he said \\\"hi\\\"\\tand\\\\left\": title)\n"
    match plotFrame src with
    | Error e -> false, e
    | Ok doc ->
        let layout = doc.RootElement.GetProperty("data").GetProperty "layout"
        let text = (layout.GetProperty("title").GetProperty "text").GetString()
        // Round trip: the reader hands back the ORIGINAL characters, not the
        // escapes -- the escaping is transport, not content.
        eq "he said \"hi\"\tand\\left" text)

add "plot: an axis label with a quote does not leak out of its string" (fun () ->
    let src =
        "import plot\n\
         let ok = plot.line([0.0, 1.0], [0.0, 1.0], \"x\\\" ,\\\"evil\\\":1\": xlabel)\n"
    match plotFrame src with
    | Error e -> false, e
    | Ok doc ->
        let layout = doc.RootElement.GetProperty("data").GetProperty "layout"
        // The injected `"evil":1` has to arrive as LABEL TEXT, never as a
        // sibling key of the layout object.
        let mutable leaked = Unchecked.defaultof<System.Text.Json.JsonElement>
        let escaped = layout.TryGetProperty("evil", &leaked)
        let text = (layout.GetProperty("xaxis").GetProperty("title").GetProperty "text").GetString()
        (not escaped && text = "x\" ,\"evil\":1"), sprintf "escaped=%b text=%s" escaped text)

add "plot: NaN and both infinities serialize as JSON null" (fun () ->
    let src =
        "import plot\n\
         let ok = plot.line([0.0, 1.0, 2.0, 3.0, 4.0], [2.5, 0.0 / 0.0, 1.0 / 0.0, -1.0 / 0.0, 0.5])\n"
    match plotFrame src with
    | Error e -> false, e
    | Ok doc ->
        let trace = (doc.RootElement.GetProperty("data").GetProperty "data").EnumerateArray() |> Seq.head
        let kinds =
            (trace.GetProperty "y").EnumerateArray()
            |> Seq.map (fun e ->
                match e.ValueKind with
                | System.Text.Json.JsonValueKind.Null -> "null"
                | System.Text.Json.JsonValueKind.Number -> string (e.GetDouble())
                | k -> sprintf "%A" k)
            |> String.concat ","
        // The finite samples are untouched: `null` is a gap marker, not a
        // blanket fallback.
        eq "2.5,null,null,null,0.5" kinds)

add "plot: json_num of a non-finite scalar slot is null too" (fun () ->
    // `ncontours` is the json_num path -- an Int slot here, so this check
    // drives the same serializer through a Float-typed figure field by way of
    // a NaN z grid, which is the only way a scalar slot can go non-finite in
    // v1. The z array covers json_array's rank-2 arm at the same time.
    let src =
        "import plot\n\
         let ok = plot.contourf([0.0, 1.0], [0.0, 1.0], [[0.0, 0.0 / 0.0], [1.0 / 0.0, 1.5]], 5: levels)\n"
    match plotFrame src with
    | Error e -> false, e
    | Ok doc ->
        let trace = (doc.RootElement.GetProperty("data").GetProperty "data").EnumerateArray() |> Seq.head
        let row (i: int) =
            (trace.GetProperty "z").EnumerateArray() |> Seq.item i
            |> fun r -> r.EnumerateArray() |> Seq.map (fun e -> e.ValueKind.ToString()) |> String.concat ","
        ((trace.GetProperty "ncontours").GetInt32() = 5
         && row 0 = "Number,Null" && row 1 = "Null,Number"),
        sprintf "row0=%s row1=%s" (row 0) (row 1))

// ---- Runner -----------------------------------------------------------------

/// Run the in-process display block. No compiler toolchain: the REPL-channel
/// checks drive the interpreter directly and the serve-channel checks drive the
/// real session engine and response encoder, so this block is cheap and
/// unconditional.
let runDisplayTests () : BlockResult =
    printHeader "Display Frames"
    let mutable passed = 0
    let mutable failed = 0
    let mutable failedNames = []
    for (name, fn) in checks do
        let (ok, detail) =
            try fn () with ex -> false, sprintf "exception: %s" ex.Message
        if ok then
            passed <- passed + 1
            resultLine Pass name ""
        else
            failed <- failed + 1
            failedNames <- failedNames @ [name]
            resultLine Fail name detail
    printFooter "Display" [sprintf "%d passed" passed; sprintf "%d failed" failed]
    { Block = "Display"; Passed = passed; Failed = failed; Skipped = 0; FailedNames = failedNames }
