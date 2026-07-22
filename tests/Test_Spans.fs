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
open Blade.Ast
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

    // -- `let static` assertion: fold or fail loudly ----------------------
    // A static whose RHS needs a runtime value is a compile error at the
    // static decl's own line, with the assertion wording.
    let firstErr (src: string) : (int * string) option =
        match Parser.parseProgram src with
        | Error _ -> None
        | Ok program ->
            match TypeCheck.typeCheck program with
            | Error (e :: _) -> Some (e.Span.StartLine, TypeEnv.formatTypeError e.Error)
            | _ -> None
    let assertSrc =
        "let runtime_v = 41\n" +
        "\n" +
        "let static bad = runtime_v + 1\n" +
        "let r = bad\n"
    check "static assertion: unfoldable `let static` errors at its line"
        (match firstErr assertSrc with
         | Some (3, msg) -> msg.Contains "does not evaluate at compile time"
         | _ -> false)
        (sprintf "got %A" (firstErr assertSrc))

    // A lambda-valued static declares a function, not a foldable value —
    // exempt from the assertion.
    let lambdaSrc =
        "let static twice = lambda(x) -> x * 2.0\n" +
        "let y = twice(2.0)\n"
    check "static assertion: lambda static stays legal"
        (firstErr lambdaSrc = None)
        (sprintf "got %A" (firstErr lambdaSrc))

    // A destructured static folds (leaves bound by bindPattern) — no error.
    let tupleSrc =
        "static function pr() -> (Int64, Int64) = (4, 1)\n" +
        "let static (a, b) = pr()\n" +
        "let r = a + b\n"
    check "static assertion: destructured static folds without error"
        (firstErr tupleSrc = None)
        (sprintf "got %A" (firstErr tupleSrc))

    // ====================================================================
    // Stage 2: token end positions, real statement/decl span ranges,
    // File threading, and coded ParseErrors (BL1001/BL1002/BL1999).
    // ====================================================================

    // (a) Statement spans are REAL ranges, not zero-width points. Collect the
    //     StmtSpanned annotations from inside a multi-line function block.
    let collectStmtSpans (src: string) : Span list =
        match Parser.parseProgram src with
        | Error _ -> []
        | Ok prog ->
            let acc = System.Collections.Generic.List<Span>()
            let rec walkExpr (e: Expr) =
                match e.Kind with
                | ExprKind.ExprBlock (stmts, fin) ->
                    stmts |> List.iter (fun s ->
                        match s with
                        | StmtSpanned (_, sp) -> acc.Add sp
                        | _ -> ())
                    match fin with Some fe -> walkExpr fe | None -> ()
                | ExprKind.ExprLambda (_, _, body) -> walkExpr body
                | _ -> ()
            for m in prog.Modules do
                for d in m.Decls do
                    match d.Value with
                    | DeclFunction f -> walkExpr f.Body
                    | DeclLet b | DeclStatic b -> walkExpr b.Value
                    | _ -> ()
            List.ofSeq acc
    let stmtRangeSrc =
        "function f(x: Float64) -> Float64 = {\n" +
        "    let a = x + 1.0\n" +
        "    let bee = a * 2.0 + a\n" +
        "    bee\n" +
        "}\n"
    let stmtSpans = collectStmtSpans stmtRangeSrc
    check "statement spans: real ranges (end tracked, not zero-width)"
        (stmtSpans.Length >= 2 &&
         stmtSpans |> List.forall (fun sp ->
            sp.EndLine >= sp.StartLine &&
            (sp.EndLine > sp.StartLine || sp.EndCol > sp.StartCol)))
        (sprintf "got %A" (stmtSpans |> List.map (fun sp -> sp.StartLine, sp.StartCol, sp.EndLine, sp.EndCol)))
    check "statement spans: first stmt starts on its own line (line 2)"
        (match stmtSpans with sp :: _ -> sp.StartLine = 2 && sp.EndCol > sp.StartCol | [] -> false)
        (sprintf "got %A" (stmtSpans |> List.tryHead))

    // (b) parseMultiSource stamps File onto decl spans.
    let declFiles (fname: string) (src: string) : string option list =
        match Parser.parseMultiSource [(fname, src)] with
        | Error _ -> []
        | Ok prog -> [ for m in prog.Modules do for d in m.Decls -> d.Span.File ]
    let stampedFiles = declFiles "mymod.blade" "let a = 1\nlet b = 2\n"
    check "parseMultiSource stamps File onto decl spans"
        (stampedFiles = [ Some "mymod.blade"; Some "mymod.blade" ])
        (sprintf "got %A" stampedFiles)
    // The single-source entry point keeps File = None (unchanged signature).
    check "parseProgram leaves decl-span File unset"
        (match Parser.parseProgram "let a = 1\n" with
         | Ok prog -> prog.Modules |> List.forall (fun m -> m.Decls |> List.forall (fun d -> d.Span.File = None))
         | Error _ -> false)
        ""

    // (c) An EOF parse error reports the END of input (last line), not 0:0.
    check "parse error at EOF reports a real line, not 0"
        (match parseErrLine "let x = (1 + 2" with Some n -> n > 0 | None -> false)
        (sprintf "got %A" (parseErrLine "let x = (1 + 2"))
    check "parse error at EOF reports the LAST line (multi-line source)"
        (parseErrLine "let a = 1\nfunction f(x" = Some 2)
        (sprintf "got %A" (parseErrLine "let a = 1\nfunction f(x"))

    // (d) Expected-token errors read like prose — no raw DU noise (TokLParen…).
    let parseErrMsg (src: string) : string =
        match Parser.parseProgram src with Error e -> e.Message | Ok _ -> ""
    let expMsg = parseErrMsg "function f(x y) -> Int64 = 1\n"
    check "expected-token message is humanized (identifier 'y', not TokIdent)"
        (expMsg.Contains "Expected ')'" && expMsg.Contains "identifier 'y'" && not (expMsg.Contains "Tok"))
        (sprintf "got: %s" expMsg)
    let unexpMsg = parseErrMsg "let x = )\n"
    check "unexpected-token message carries no raw DU constructor name"
        (unexpMsg.Contains "')'" && not (unexpMsg.Contains "Tok"))
        (sprintf "got: %s" unexpMsg)

    // (e) ParseError.Code is classified: BL1001 expected-token, BL1002 EOF,
    //     BL1999 generic.
    let parseErrCode (src: string) : string =
        match Parser.parseProgram src with Error e -> e.Code | Ok _ -> "OK"
    check "parse error code: BL1001 (expected token)"
        (parseErrCode "function f(x y) -> Int64 = 1\n" = "BL1001")
        (sprintf "got %s" (parseErrCode "function f(x y) -> Int64 = 1\n"))
    check "parse error code: BL1002 (unexpected EOF)"
        (parseErrCode "function f(x" = "BL1002")
        (sprintf "got %s" (parseErrCode "function f(x"))
    check "parse error code: BL1999 (generic)"
        (parseErrCode "let x = )\n" = "BL1999")
        (sprintf "got %s" (parseErrCode "let x = )\n"))

    printFooter "Error Locations" [sprintf "%d passed" passed; sprintf "%d failure(s)" failed]
    { Block = "Error Locations"; Passed = passed; Failed = failed; Skipped = 0; FailedNames = failedNames }
