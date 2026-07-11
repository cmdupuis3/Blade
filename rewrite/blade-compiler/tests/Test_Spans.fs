// Error-location tests (audit §3.4 / plan Phase 2 gate): deliberately
// broken sources, asserting the REPORTED line — not just that an error
// occurred. Three tiers:
//   parse errors     -> ParseError.Line/Col (lexer/parser, long-standing)
//   decl-level types -> CompileError.Span from the Located<Decl> wrapper
//   stmt-level types -> CompileError.Span from the parser's StmtSpanned
//                       annotation, threaded through inferBlock — the new
//                       §3.4 capability: an error inside a multi-statement
//                       body points at the failing STATEMENT, not the
//                       enclosing declaration header.
module Blade.Tests.Spans

open Blade
open Blade.Tests.TestHarness

let runSpanTests () : BlockResult =
    printHeader "Error-Location Tests"
    let mutable passed = 0
    let mutable failed = 0
    let mutable failedNames : string list = []
    let check name ok detail =
        if ok then
            passed <- passed + 1
            resultLine Pass name detail
        else
            failed <- failed + 1
            failedNames <- failedNames @ [name]
            resultLine Fail name detail

    // -- Parse errors carry the offending line ---------------------------
    let parseErrLine (src: string) : int option =
        match Parser.parseProgram src with
        | Error e -> Some e.Line
        | Ok _ -> None
    // (An UNCLOSED construct — e.g. `(1 + 2` — reports at EOF, the point of
    // detection; that is correct, if not maximally helpful. This case uses a
    // token that is wrong ON its own line.)
    check "parse error: bad token on line 3"
        (parseErrLine "let a = 1\n\nlet b = ]\n" = Some 3)
        (sprintf "got %A" (parseErrLine "let a = 1\n\nlet b = ]\n"))
    check "parse error: bad token on line 1"
        (match parseErrLine "let x = )\n" with Some 1 -> true | _ -> false) ""

    // -- Type errors: reported span line ---------------------------------
    let typeErrLine (src: string) : int option =
        match Parser.parseProgram src with
        | Error _ -> None
        | Ok program ->
            match TypeCheck.typeCheck program with
            | Error (e :: _) -> Some e.Span.StartLine
            | Error [] -> None
            | Ok _ -> None

    // Decl-level: the unbound reference is in the decl starting on line 3.
    check "type error: decl-level location"
        (typeErrLine "let a = 1\n\nlet b = no_such_name\n" = Some 3)
        (sprintf "got %A" (typeErrLine "let a = 1\n\nlet b = no_such_name\n"))

    // Statement-level (§3.4): the failing statement is on line 3, inside a
    // function declared on line 1 — the error must NOT point at line 1.
    let stmtSrc =
        "function f(x: Float64) -> Float64 = {\n" +
        "    let a = x + 1.0\n" +
        "    let b = no_such_name + a\n" +
        "    b\n" +
        "}\n"
    check "type error: statement-level location inside a block"
        (typeErrLine stmtSrc = Some 3)
        (sprintf "got %A" (typeErrLine stmtSrc))

    // A later statement failing reports ITS line, not the first statement's.
    let stmtSrc2 =
        "function g(x: Float64) -> Float64 = {\n" +
        "    let a = x + 1.0\n" +
        "    let b = a * 2.0\n" +
        "    let c = b + missing_here\n" +
        "    c\n" +
        "}\n"
    check "type error: later statement reports its own line"
        (typeErrLine stmtSrc2 = Some 4)
        (sprintf "got %A" (typeErrLine stmtSrc2))

    // The span must not leak: an error in a later DECL (no block involved)
    // still reports the decl's own line even after a block was checked.
    let leakSrc =
        "function h(x: Float64) -> Float64 = {\n" +
        "    let a = x + 1.0\n" +
        "    a\n" +
        "}\n" +
        "let broken = also_missing\n"
    check "type error: statement span does not leak into later decls"
        (typeErrLine leakSrc = Some 5)
        (sprintf "got %A" (typeErrLine leakSrc))

    // formatCompileError renders the location as line:col.
    let formatted =
        match Parser.parseProgram stmtSrc with
        | Ok program ->
            match TypeCheck.typeCheck program with
            | Error (e :: _) -> TypeEnv.formatCompileError e
            | _ -> ""
        | Error _ -> ""
    check "formatCompileError includes line:col"
        (formatted.Contains "3:") (sprintf "got: %s" formatted)

    printFooter "Error Locations" [sprintf "%d passed" passed; sprintf "%d failure(s)" failed]
    { Block = "Error Locations"; Passed = passed; Failed = failed; Skipped = 0; FailedNames = failedNames }
