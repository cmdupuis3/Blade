// Unified diagnostics: the one record every compiler phase's errors converge
// to, the BLxxxx code registry, and the renderers (rustc-style snippet form
// and the legacy one-line form). Sits immediately after Ast.fs in compile
// order so every later phase can construct Diagnostics directly.
//
// Code bands (band = phase; registry below is the source of truth):
//   BL0xxx lexer          BL1xxx parser         BL2xxx name resolution
//   BL3xxx types          BL4xxx constraints/static
//   BL5xxx elaborators (50 ml, 51 ppl, 52 math, 53 rand, 54 spectra, 55 grad)
//   BL6xxx IR validation  BL7xxx backend limits
//   BL8xxx runtime (stamped into generated C++)
//   BL9xxx internal compiler errors
module Blade.Diagnostics

open Blade.Ast

// ============================================================================
// Core types
// ============================================================================

type Severity =
    | SevError
    | SevWarning
    | SevNote

type Phase =
    | PhLex
    | PhParse
    | PhResolve
    | PhTypes
    | PhConstraints
    | PhElaborate of string   // "ml" | "ppl" | "math" | "rand" | "spectra" | "grad"
    | PhIRValidate
    | PhBackend
    | PhRuntime
    | PhInternal

type Diagnostic = {
    Code: string                          // "BL3001"
    Severity: Severity
    Phase: Phase
    Span: Span                            // noSpan allowed; renderers degrade gracefully
    Message: string                       // one-line primary message
    Notes: (Span option * string) list    // secondary labels / "help:" lines
    Context: string list                  // innermost-first (CompileError.Context convention)
}

/// Carrier for phases that signal failure by exception rather than Result
/// (codegen feature limits, internal invariants). Caught at the CLI boundary.
exception BladeDiagnosticException of Diagnostic

// ============================================================================
// SourceMap: original source retained to error-report time for snippets
// ============================================================================

type SourceMap = { Files: Map<string, string[]> }

module SourceMap =
    let empty : SourceMap = { Files = Map.empty }

    let private splitLines (source: string) : string[] =
        source.Replace("\r\n", "\n").Split('\n')

    let ofSources (sources: (string * string) list) : SourceMap =
        { Files = sources |> List.map (fun (f, s) -> f, splitLines s) |> Map.ofList }

    let addFile (file: string) (source: string) (sm: SourceMap) : SourceMap =
        { Files = Map.add file (splitLines source) sm.Files }

    let tryLines (sm: SourceMap) (file: string) : string[] option =
        Map.tryFind file sm.Files

    /// Lines for a span's file. A span with File = None (the common legacy
    /// case) resolves against a single-file map — the usual CLI situation.
    let tryLinesFor (sm: SourceMap) (file: string option) : string[] option =
        match file with
        | Some f -> tryLines sm f
        | None ->
            match Map.toList sm.Files with
            | [ (_, lines) ] -> Some lines
            | _ -> None

// ============================================================================
// Constructors
// ============================================================================

let mkDiagnostic code severity phase span message : Diagnostic =
    { Code = code; Severity = severity; Phase = phase; Span = span
      Message = message; Notes = []; Context = [] }

let mkError code phase span message : Diagnostic =
    mkDiagnostic code SevError phase span message

let withNote (note: string) (d: Diagnostic) : Diagnostic =
    { d with Notes = d.Notes @ [ (None, note) ] }

let withNoteAt (span: Span) (note: string) (d: Diagnostic) : Diagnostic =
    { d with Notes = d.Notes @ [ (Some span, note) ] }

let withContext (context: string list) (d: Diagnostic) : Diagnostic =
    { d with Context = context }

// ============================================================================
// Code registry: every code the compiler can emit, with a short title.
// Test_Diagnostics asserts shape and uniqueness; emitting an unregistered
// code is a bug the corpus tests catch.
// ============================================================================

module Codes =
    let registry : Map<string, string> =
        Map.ofList [
            // BL0xxx — lexer
            "BL0001", "unknown character"
            "BL0002", "unterminated string"
            "BL0003", "invalid numeric literal"
            "BL0999", "lexical error"
            // BL1xxx — parser
            "BL1001", "expected token"
            "BL1002", "unexpected end of file"
            "BL1999", "parse error"
            // BL2xxx — name resolution
            "BL2001", "unbound variable"
            "BL2002", "unknown qualified name"
            "BL2003", "invalid import"
            // BL3xxx — types
            "BL3001", "type mismatch"
            "BL3002", "arity mismatch"
            "BL3003", "invalid application"
            "BL3004", "pattern type mismatch"
            "BL3005", "invalid array capture"
            "BL3006", "unit mismatch"
            "BL3007", "invalid builtin argument"
            "BL3008", "struct construction error"
            "BL3999", "type error"
            // BL4xxx — constraints / static
            "BL4001", "constraint violation"
            "BL4002", "static evaluation failure"
            "BL4003", "index type violation"
            "BL4004", "symmetry violation"
            "BL4005", "immutable assignment"
            "BL4006", "mutual group violation"
            "BL4007", "no equivariant map exists"
            "BL4008", "equivariance discipline violation"
            // BL5xxx — elaborators
            "BL5000", "ml elaboration error"
            "BL5100", "ppl elaboration error"
            "BL5200", "math elaboration error"
            "BL5300", "rand elaboration error"
            "BL5400", "spectra elaboration error"
            "BL5500", "grad elaboration error"
            // BL6xxx — IR validation
            "BL6001", "IR validation error"
            // BL7xxx — backend limits
            "BL7001", "feature not yet supported by this backend"
            "BL7002", "CUDA backend limit"
            "BL7003", "MPI backend limit"
            // BL8xxx — runtime (generated C++)
            "BL8001", "constraint violation"
            "BL8002", "non-exhaustive match"
            "BL8003", "empty reduction"
            "BL8004", "MPI runtime error"
            "BL8005", "unhandled runtime exception"
            "BL8006", "index out of bounds"
            // BL9xxx — internal compiler errors
            "BL9001", "internal compiler error"
            "BL9002", "internal codegen invariant violated"
            "BL9003", "internal lowering invariant violated"
        ]

    let isRegistered (code: string) = Map.containsKey code registry

    /// Phase implied by a code's band (BL0xxx lex ... BL9xxx internal).
    let phaseOfCode (code: string) : Phase =
        if code.Length < 3 then PhInternal
        else
            match code.[2] with
            | '0' -> PhLex
            | '1' -> PhParse
            | '2' -> PhResolve
            | '3' -> PhTypes
            | '4' -> PhConstraints
            | '5' ->
                match code with
                | "BL5000" -> PhElaborate "ml"
                | "BL5100" -> PhElaborate "ppl"
                | "BL5200" -> PhElaborate "math"
                | "BL5300" -> PhElaborate "rand"
                | "BL5400" -> PhElaborate "spectra"
                | "BL5500" -> PhElaborate "grad"
                | _ -> PhElaborate "ml"
            | '6' -> PhIRValidate
            | '7' -> PhBackend
            | '8' -> PhRuntime
            | _ -> PhInternal

    /// Elaborator stage name -> its band's generic code.
    let elaboratorCode (stage: string) =
        match stage with
        | "ml" -> "BL5000"
        | "ppl" -> "BL5100"
        | "math" -> "BL5200"
        | "rand" -> "BL5300"
        | "spectra" -> "BL5400"
        | "grad" -> "BL5500"
        | _ -> "BL5000"

    let ice (message: string) : Diagnostic =
        mkError "BL9001" PhInternal noSpan
            (sprintf "internal compiler error: %s" message)
        |> withNote "this is a bug in the Blade compiler, not in your program — please report it"

    let iceCodegen (message: string) : Diagnostic =
        { ice message with Code = "BL9002" }

    let backendLimit (span: Span) (message: string) : Diagnostic =
        mkError "BL7001" PhBackend span message

// ============================================================================
// Rendering
// ============================================================================

module Render =

    let private sevLabel = function
        | SevError -> "error"
        | SevWarning -> "warning"
        | SevNote -> "note"

    // ANSI styling (used only when the caller says the sink is a TTY).
    let private styled useColor (code: string) (s: string) =
        if useColor then sprintf "[%sm%s[0m" code s else s
    let private bold useColor s = styled useColor "1" s
    let private sevColor useColor sev s =
        match sev with
        | SevError -> styled useColor "1;31" s     // bold red
        | SevWarning -> styled useColor "1;33" s   // bold yellow
        | SevNote -> styled useColor "1;36" s      // bold cyan
    let private gutterColor useColor s = styled useColor "1;34" s   // bold blue

    let private hasLocation (span: Span) = span.StartLine > 0

    let private location (span: Span) =
        let line = span.StartLine
        let col = max 1 span.StartCol
        match span.File with
        | Some f -> sprintf "%s:%d:%d" f line col
        | None -> sprintf "%d:%d" line col

    /// Legacy one-line form, mirroring TypeEnv.formatCompileError's shape:
    ///   "file:line:col: message" + indented context lines (outermost first).
    let renderShort (d: Diagnostic) : string =
        let loc = if hasLocation d.Span then location d.Span else ""
        let context =
            d.Context
            |> List.rev
            |> List.map (sprintf "  %s")
            |> String.concat "\n"
        if loc = "" && context = "" then d.Message
        elif context = "" then sprintf "%s: %s" loc d.Message
        elif loc = "" then sprintf "%s\n%s" d.Message context
        else sprintf "%s: %s\n%s" loc d.Message context

    /// Snippet block for one located span: gutter, source line, underline.
    /// Renders the span's first line only; a multi-line span underlines to
    /// the end of that line. Returns [] when no source is available.
    let private snippet useColor (sm: SourceMap option) (span: Span) : string list =
        match sm |> Option.bind (fun m -> SourceMap.tryLinesFor m span.File) with
        | None -> []
        | Some lines ->
            let lineNo = span.StartLine
            if lineNo < 1 || lineNo > lines.Length then []
            else
                let text = lines.[lineNo - 1]
                let width = (string lineNo).Length
                let pad = String.replicate width " "
                let startCol = max 1 span.StartCol
                let underlineLen =
                    if span.EndLine = span.StartLine && span.EndCol > startCol
                    then span.EndCol - startCol
                    elif span.EndLine > span.StartLine
                    then max 1 (text.Length - startCol + 1)
                    else 1
                // Clamp to the visible line so a stale span cannot overflow.
                let startCol = min startCol (text.Length + 1)
                let underlineLen = max 1 (min underlineLen (text.Length - startCol + 2))
                let gut = gutterColor useColor
                [ sprintf "%s %s" (gut (pad + " |")) ""
                  sprintf "%s %s" (gut (sprintf "%d |" lineNo)) text
                  sprintf "%s %s%s"
                      (gut (pad + " |"))
                      (String.replicate (startCol - 1) " ")
                      (sevColor useColor SevError (String.replicate underlineLen "^")) ]

    /// Full rustc-style rendering:
    ///   error[BL3001]: message
    ///     --> file:line:col
    ///      |
    ///    3 |     offending line
    ///      |     ^^^^^^^
    ///      = note: ...
    ///   (context lines, outermost first, as trailing notes)
    let render (useColor: bool) (sm: SourceMap option) (d: Diagnostic) : string =
        let header =
            sprintf "%s%s %s"
                (sevColor useColor d.Severity (sevLabel d.Severity))
                (sevColor useColor d.Severity (sprintf "[%s]:" d.Code))
                (bold useColor d.Message)
        let locLines =
            if hasLocation d.Span then
                sprintf "  %s %s" (gutterColor useColor "-->") (location d.Span)
                :: snippet useColor sm d.Span
            else []
        let noteLines =
            d.Notes
            |> List.collect (fun (nspan, text) ->
                let noteLine = sprintf "  %s %s" (gutterColor useColor "=") (sprintf "note: %s" text)
                match nspan with
                | Some s when hasLocation s ->
                    noteLine :: (sprintf "    %s %s" (gutterColor useColor "-->") (location s)) :: []
                | _ -> [ noteLine ])
        let contextLines =
            d.Context
            |> List.rev
            |> List.map (fun c -> sprintf "  %s %s" (gutterColor useColor "=") c)
        String.concat "\n" (header :: (locLines @ noteLines @ contextLines))

    let renderAll (useColor: bool) (sm: SourceMap option) (ds: Diagnostic list) : string =
        ds |> List.map (render useColor sm) |> String.concat "\n\n"
