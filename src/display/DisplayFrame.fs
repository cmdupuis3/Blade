// Display frames: the compiler's half of the `{mime, data}` rich-output wire
// format specified in Blade-REPL/docs/display-frames.md. One frame is one
// MIME-typed payload -- a plot, an image -- produced by evaluated Blade code
// and carried to the editor alongside stdout.
//
// This module owns the FORMAT and nothing else: the sentinel bytes, the
// encoding-inference rule, the JSON escape, and the exact byte sequence of one
// frame line. Four consumers share it, which is the whole point of putting it
// this early in the compile order (it depends on nothing but FSharp.Core):
//
//   Interp/Core.fs   evaluates IRDisplayEmit -> `emit` (buffered, flushed by
//                    Interp/Run.fs ahead of the binding prints)
//   CodeGen.fs       emits a C++ `blade_display::emit` MIRROR of `emit`; the
//                    two must agree byte-for-byte or the differential gate
//                    (tests/InterpDiff.fs) fails, which is exactly the pin we
//                    want on this format
//   ReplSession.fs   lifts frame lines out of a run's stdout into EvalResult
//   IdeServe.fs      writes them as the eval response's `display` array
//
// Frames are produced on stdout in BOTH lanes because that is the one place
// the interpreter and the compiled binary already agree byte-for-byte, and
// because the REPL channel (spec section 4) needs them there anyway. The serve
// channel (spec section 2) is then a pure extraction at the session boundary,
// not a second emission path.
module Blade.Display.Frame

open System
open System.Text

/// Envelope version (spec section 1: `v`). A reader rejects anything greater.
[<Literal>]
let Version = 1

/// The REPL channel's line prefix: SOH, `blade-display`, SOH -- 15 bytes.
/// The control-character delimiters are what make the prefix unforgeable: JSON
/// escaping turns a payload's own SOH into `\u0001` (see `escape`), so a frame
/// can never re-open a frame, and no escaping scheme is needed on top.
let Sentinel = "\u0001blade-display\u0001"

/// Prefix for generated `meta.id`s. Frames with equal ids are alternate
/// renders of the SAME plot, so the panel merges rather than appends -- which
/// is what makes the REPL's re-run-the-whole-session model harmless: replaying
/// a session re-emits every earlier frame with the SAME ordinal, and the panel
/// updates the existing entries in place instead of growing.
///
/// Mutable so a front end holding several independent sessions can keep their
/// ids apart (`ide serve` sets it per notebook). Left at the default, ids are a
/// pure function of the program, which is what the differential gate and the
/// corpus pins require. CodeGen BAKES the current value into the generated C++,
/// so both lanes of one compilation always agree.
let mutable SessionTag = "blade-"

/// A `SessionTag` for one session key. Deterministic (FNV-1a, not
/// String.GetHashCode, which .NET randomizes per process) so the same notebook
/// keeps the same plot identities across restarts of `ide serve`.
let tagForSession (key: string) : string =
    let mutable h = 2166136261u
    for ch in key do
        h <- (h ^^^ uint32 ch) * 16777619u
    sprintf "s%08x-" h

// Per-run emission state. The interpreter runs one program at a time on one
// worker thread (Runtime.runOnLargeStack), so plain mutables are enough; the
// compiled lane's counterpart is a `static int` in the generated C++.
let private buffer = ResizeArray<string>()
let mutable private ordinal = 0

/// Clear the per-run state. Called at the top of every interpreter run so the
/// n-th emission of a program is always id `<tag><n>`, run after run.
let resetRun () =
    buffer.Clear()
    ordinal <- 0

/// How many frames this run has emitted so far. Interp/Run.fs brackets each
/// top-level binding with it to find the frame EMITTERS: a session memo must
/// never adopt one, because a binding that does not re-run cannot re-emit, and
/// the editor keys its plot panel on the frames of the run it is showing.
let emitted () = buffer.Count

/// Take the frames this run produced, in emission order.
let drain () : string list =
    let xs = List.ofSeq buffer
    buffer.Clear()
    xs

/// `encoding` implied by a mime type (spec section 1): JSON-shaped mimes carry
/// an inline JSON value, `text/*` carries a string, everything else is binary
/// and travels base64.
let encodingFor (mime: string) : string =
    if mime = "application/json" || mime.EndsWith "+json" then "json"
    elif mime.StartsWith "text/" then "utf8"
    else "base64"

/// Is `data` inserted verbatim (an inline JSON value) or as a quoted, escaped
/// JSON string? Exactly `encoding <> "json"`.
let quotedFor (mime: string) : bool = encodingFor mime <> "json"

/// `type/subtype`, per the spec's mime grammar.
let isMimeType (s: string) : bool =
    let part (p: string) =
        p.Length > 0
        && (Char.IsLetterOrDigit p.[0])
        && p |> Seq.forall (fun c -> Char.IsLetterOrDigit c || c = '.' || c = '+' || c = '_' || c = '-')
    match s.Split('/') with
    | [| a; b |] -> part a && part b
    | _ -> false

/// JSON string escape. Control characters below 0x20 go out as `\u00xx`, which
/// is what disarms a payload containing the sentinel's own SOH. Mirrored
/// character-for-character by the generated C++ (`cppRuntime` below).
let escape (s: string) : string =
    let sb = StringBuilder(s.Length + 8)
    for ch in s do
        match ch with
        | '"' -> sb.Append "\\\"" |> ignore
        | '\\' -> sb.Append "\\\\" |> ignore
        | '\n' -> sb.Append "\\n" |> ignore
        | '\r' -> sb.Append "\\r" |> ignore
        | '\t' -> sb.Append "\\t" |> ignore
        | '\b' -> sb.Append "\\b" |> ignore
        | '\f' -> sb.Append "\\f" |> ignore
        | c when c < ' ' -> sb.AppendFormat("\\u{0:x4}", int c) |> ignore
        | c -> sb.Append c |> ignore
    sb.ToString()

/// The static leading half of a frame, everything up to and including
/// `"data":`. Built at ELABORATION time (the mime is a literal, see
/// DisplayElaborate) so neither runtime lane has to know the encoding rule.
let headFor (mime: string) : string =
    sprintf "{\"v\":%d,\"mime\":\"%s\",\"encoding\":\"%s\",\"data\":" Version mime (encodingFor mime)

/// A user `meta` object literal reduced to the tail that follows the generated
/// `"id"` entry: `{"title":"x"}` -> `,"title":"x"`, `{}` -> `""`. The braces
/// come off because `id` is generated at runtime and has to lead the object.
/// Returns None when the text is not brace-delimited (the elaborator turns
/// that into a user-facing error).
let metaTailOf (metaJson: string) : string option =
    let t = metaJson.Trim()
    if t.Length >= 2 && t.StartsWith "{" && t.EndsWith "}" then
        let inner = t.Substring(1, t.Length - 2).Trim()
        Some (if inner = "" then "" else "," + inner)
    else None

/// One frame line, without its terminating newline. `head` and `metaTail` are
/// the elaboration-time constants; `data` is the runtime payload; `ord` is this
/// run's 1-based emission ordinal.
let composeLine (head: string) (quoted: bool) (data: string) (metaTail: string) (ord: int) : string =
    let payload = if quoted then "\"" + escape data + "\"" else data
    Sentinel + head + payload + ",\"meta\":{\"id\":\"" + SessionTag + string ord + "\"" + metaTail + "}}"

/// Interpreter-lane emission: buffer one frame and answer `true` (the value
/// `display.emit` evaluates to). Buffered rather than written because the
/// interpreter builds its whole stdout after every binding has been evaluated
/// -- Interp/Run.fs writes these ahead of the binding prints, which is where
/// the compiled binary's `std::cout` writes land too (they run inside main()'s
/// body, before the timing line and the print block).
let emit (head: string) (quoted: bool) (data: string) (metaTail: string) : bool =
    ordinal <- ordinal + 1
    buffer.Add(composeLine head quoted data metaTail ordinal)
    true

/// Split a program's raw stdout into the text a terminal should show and the
/// frame JSON strings it carried. Frame lines are always whole lines at column
/// 0 (nothing else writes to stdout while a program body runs), so this is a
/// line filter, not a parser. Used by the `ide serve` lane to fill the eval
/// response's `display` array; the interactive REPL leaves stdout alone and
/// lets the extension do its own extraction.
let extract (stdout: string) : string * string list =
    if String.IsNullOrEmpty stdout || not (stdout.Contains Sentinel) then (stdout, [])
    else
        let frames = ResizeArray<string>()
        let kept = ResizeArray<string>()
        for raw in stdout.Split('\n') do
            let line = if raw.EndsWith "\r" then raw.Substring(0, raw.Length - 1) else raw
            if line.StartsWith Sentinel then frames.Add(line.Substring Sentinel.Length)
            else kept.Add raw
        (String.Join("\n", kept), List.ofSeq frames)

/// The generated C++ mirror of `escape`/`composeLine`/`emit`, emitted into
/// every program's preamble (header-only, `static inline`, and free when
/// unused, so it needs no per-program feature scan). `\x01` sits in its OWN
/// string literal because `"\x01blade-display"` would parse `\x01b` as one
/// oversized hex escape -- adjacent-literal concatenation is the fix, not a
/// style choice.
///
/// BYTE PARITY WITH `emit` IS THE CONTRACT: the differential gate runs the same
/// program down both lanes and compares stdout, so any divergence here is a
/// test failure by construction.
let cppRuntime () : string list =
    [ "#include <sstream>  // blade_display::json1/json2/jsonnum"
      "namespace blade_display {"
      "// Display frames (docs/display-frames.md). Mirrors Blade.Display.Frame."
      "static int __blade_display_ord = 0;"
      "static inline std::string __blade_display_esc(const std::string& s) {"
      "    static const char* hx = \"0123456789abcdef\";"
      "    std::string o; o.reserve(s.size() + 8);"
      "    for (std::size_t i = 0; i < s.size(); ++i) {"
      "        unsigned char c = (unsigned char)s[i];"
      "        switch (c) {"
      "        case '\"': o += \"\\\\\\\"\"; break;"
      "        case '\\\\': o += \"\\\\\\\\\"; break;"
      "        case '\\n': o += \"\\\\n\"; break;"
      "        case '\\r': o += \"\\\\r\"; break;"
      "        case '\\t': o += \"\\\\t\"; break;"
      "        case '\\b': o += \"\\\\b\"; break;"
      "        case '\\f': o += \"\\\\f\"; break;"
      "        default:"
      "            if (c < 0x20) { o += \"\\\\u00\"; o += hx[(c >> 4) & 0xF]; o += hx[c & 0xF]; }"
      "            else o += (char)c;"
      "        }"
      "    }"
      "    return o;"
      "}"
      "// JSON serialization of numeric arrays / scalars (display.json_array /"
      "// display.json_num). setprecision(15) is the print block's own float"
      "// rule; Blade.Interp.CppFormat.formatFloat15 is its byte-exact mirror,"
      "// so the differential gate pins the two lanes together."
      "template <typename A>"
      "static inline std::string json1(const A& a) {"
      "    std::ostringstream o; o << std::setprecision(15) << '[';"
      "    for (size_t i = 0; i < a.extents[0]; ++i) { if (i) o << ','; o << a[i]; }"
      "    o << ']'; return o.str();"
      "}"
      "template <typename A>"
      "static inline std::string json2(const A& a) {"
      "    std::ostringstream o; o << std::setprecision(15) << '[';"
      "    for (size_t i = 0; i < a.extents[0]; ++i) {"
      "        if (i) o << ','; o << '[';"
      "        for (size_t j = 0; j < a.extents[1]; ++j) { if (j) o << ','; o << a[i][j]; }"
      "        o << ']';"
      "    }"
      "    o << ']'; return o.str();"
      "}"
      "template <typename T>"
      "static inline std::string jsonnum(T x) {"
      "    std::ostringstream o; o << std::setprecision(15) << x; return o.str();"
      "}"
      "static inline bool emit(const char* head, bool quoted, const std::string& data,"
      "                        const char* metaTail, const char* tag) {"
      "    std::cout << \"\\x01\" \"blade-display\" \"\\x01\" << head;"
      "    if (quoted) std::cout << '\"' << __blade_display_esc(data) << '\"';"
      "    else std::cout << data;"
      "    std::cout << \",\\\"meta\\\":{\\\"id\\\":\\\"\" << tag << ++__blade_display_ord"
      "              << \"\\\"\" << metaTail << \"}}\" << \"\\n\";"
      "    return true;"
      "}"
      "}" ]
