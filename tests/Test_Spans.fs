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
    // Every `parseErrLine`/`typeErrLine` case below is evaluated ONCE, into a
    // binding shared by the assertion and its printed detail. Two evaluations
    // of the same source used to be able to disagree (see the span-leak note
    // further down), which made a failure detail contradict the assertion it
    // was explaining; the leak is fixed, but one evaluation is still the
    // honest way to report what was actually asserted.
    let badTokLine3 = parseErrLine "let a = 1\n\nlet b = ]\n"
    check "parse error: bad token on line 3"
        (badTokLine3 = Some 3)
        (sprintf "got %A" badTokLine3)
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
    let declLevelLine = typeErrLine "let a = 1\n\nlet b = no_such_name\n"
    check "type error: decl-level location"
        (declLevelLine = Some 3)
        (sprintf "got %A" declLevelLine)

    // Statement-level (§3.4): the failing statement is on line 3, inside a
    // function declared on line 1 — the error must NOT point at line 1.
    let stmtSrc =
        "function f(x: Float64) -> Float64 = {\n" +
        "    let a = x + 1.0\n" +
        "    let b = no_such_name + a\n" +
        "    b\n" +
        "}\n"
    let stmtLine = typeErrLine stmtSrc
    check "type error: statement-level location inside a block"
        (stmtLine = Some 3)
        (sprintf "got %A" stmtLine)

    // A later statement failing reports ITS line, not the first statement's.
    let stmtSrc2 =
        "function g(x: Float64) -> Float64 = {\n" +
        "    let a = x + 1.0\n" +
        "    let b = a * 2.0\n" +
        "    let c = b + missing_here\n" +
        "    c\n" +
        "}\n"
    let stmtLine2 = typeErrLine stmtSrc2
    check "type error: later statement reports its own line"
        (stmtLine2 = Some 4)
        (sprintf "got %A" stmtLine2)

    // The span must not leak: an error in a later DECL (no block involved)
    // still reports the decl's own line even after a block was checked.
    let leakSrc =
        "function h(x: Float64) -> Float64 = {\n" +
        "    let a = x + 1.0\n" +
        "    a\n" +
        "}\n" +
        "let broken = also_missing\n"
    let leakLine = typeErrLine leakSrc
    check "type error: statement span does not leak into later decls"
        (leakLine = Some 5)
        (sprintf "got %A" leakLine)

    // formatCompileError renders the location as line:col.
    let formatted =
        match Parser.parseProgram stmtSrc with
        | Ok program ->
            match TypeCheck.typeCheck program with
            | Error (e :: _) -> TypeEnv.formatCompileError e
            | _ -> ""
        | Error _ -> ""
    check "formatCompileError includes line:col"
        (formatted.Contains "3:") ($"got: {formatted}")

    // -- `let static` assertion: fold or fail loudly ----------------------
    // A static whose RHS needs a runtime value is a compile error at the
    // static decl's own line, with the assertion wording.
    //
    // A PARSE failure is reported as an error in its own right, NOT as None:
    // mapping it to None makes it indistinguishable from "type-checked
    // clean", which is what the two `= None` assertions below assert. With
    // the old mapping, a source that stopped parsing passed them silently.
    let allErrs (src: string) : (int * int * string) list =
        match Parser.parseProgram src with
        | Error e -> [ (e.Line, e.Col, "PARSE ERROR: " + e.Message) ]
        | Ok program ->
            match TypeCheck.typeCheck program with
            | Error es ->
                es |> List.map (fun e ->
                    (e.Span.StartLine, e.Span.StartCol, TypeEnv.formatTypeError e.Error))
            | Ok _ -> []
    let assertSrc =
        "let runtime_v = 41\n" +
        "\n" +
        "let static bad = runtime_v + 1\n" +
        "let r = bad\n"
    // ORDER IS LOAD-BEARING — this doubles as the regression test for a
    // cross-call span leak. The correct span here is the static decl's own,
    // 3:1 (`f.Span`, threaded from StaticEval.StaticFailure through
    // checkModule's `locateError f.Span`). But `locateError` prefers TypeEnv's
    // AsyncLocal expression/statement span side-channel over the caller's
    // span, and that side-channel used to be cleared only at `checkDecl`
    // entry — which has not run yet when the `let static` fold assertion is
    // raised at the TOP of `checkModule`, before the decl loop. AsyncLocal
    // outlives a compilation, so the error inherited the PREVIOUS typeCheck
    // call's last-stamped span: type-check `stmtSrc` (error at 3:13, where
    // `no_such_name` starts) and then this source, and the assertion was
    // reported at 3:13 — a column mid-`bad`, in another file's coordinates.
    // The same mechanism made two evaluations of THIS source disagree on the
    // line (3 then 4, inheriting `bad` in `let r = bad`).
    //
    // Fixed by resetting the side-channel at `typeCheck` and `checkModule`
    // entry. Priming with the 3:13 source below and pinning the column to 1
    // is what keeps it fixed; if the reset regresses, the column reverts to
    // 13 and this fails. Do not relax the column back to a wildcard.
    let primedLine = typeErrLine stmtSrc   // leaves 3:13 in the side-channel
    check "span leak: priming source reports its own location first"
        (primedLine = Some 3)
        (sprintf "got %A" primedLine)
    let assertErrs = allErrs assertSrc
    check "static assertion: unfoldable `let static` errors at 3:1, once, naming the decl"
        (match assertErrs with
         | [ (3, 1, msg) ] ->
             msg.Contains "does not evaluate at compile time"
             && msg.Contains "let static bad"
             && msg.Contains "undefined variable 'runtime_v'"
         | _ -> false)
        (sprintf "got %A" assertErrs)
    // Idempotence, stated directly: a second compilation of the same source in
    // the same process reports the same location as the first.
    let assertErrs2 = allErrs assertSrc
    check "span leak: re-compiling the same source reports the same location"
        (assertErrs2 |> List.map (fun (l, c, _) -> l, c) = [ (3, 1) ])
        (sprintf "got %A" assertErrs2)

    // The per-MODULE half of the same fix, and its only guard. `checkProgram`
    // folds `checkModule` over the modules of ONE program, and each
    // `checkModule` raises the `let static` fold assertion at its TOP, before
    // its decl loop — so the per-decl reset has not run, and the
    // typeCheck-entry reset ran once, ahead of the FIRST module only. An
    // earlier module that type-checks CLEANLY still leaves a stamp behind
    // (`inferExpr` stamps `currentExprSpan` on entry to every node, whether or
    // not inference then succeeds), so without the reset at `checkModule`
    // entry a later module's assertion is reported in the EARLIER module's
    // coordinates: with that one reset removed, the fixture below reports
    // mod_a.blade:2:13 — the `40` literal in module 1's last binding — instead
    // of mod_b.blade:3:1. Unlike the cross-call case above this needs no priming
    // compilation: it misfires on a FIRST compile in a fresh process, which is
    // what makes it a user-visible bug rather than a test-host artifact.
    //
    // `parseMultiSource` yields one ModuleDecl per file and stamps that file
    // name onto every span it builds (not just decl spans — see Parser's span
    // constructors), so File discriminates the leak by itself: mod_a.blade
    // means leaked, mod_b.blade means correctly located. Line and column are
    // pinned too; all three must stay pinned for this to keep its teeth.
    let multiModuleErrs (srcs: (string * string) list) : (string option * int * int * string * string) list =
        match Parser.parseMultiSource srcs with
        | Error e -> [ (None, e.Line, e.Col, "PARSE ERROR", e.Message) ]
        | Ok program ->
            match TypeCheck.typeCheck program with
            | Error es ->
                es |> List.map (fun e ->
                    (e.Span.File, e.Span.StartLine, e.Span.StartCol,
                     (TypeEnv.diagnosticOfCompileError e).Code,
                     TypeEnv.formatTypeError e.Error))
            | Ok _ -> []
    // Module 1 type-checks clean; its only job is to leave a stamp whose file,
    // line and column all differ from module 2's static decl (3:1).
    let modASrc =
        "let a = 1 + 2\n" +
        "let b = a + 40\n"
    let crossModErrs = multiModuleErrs [("mod_a.blade", modASrc); ("mod_b.blade", assertSrc)]
    check "span leak: a later module's static assertion is located in its OWN module"
        (match crossModErrs with
         | [ (Some "mod_b.blade", 3, 1, code, msg) ] ->
             code = "BL3999"
             && msg.Contains "does not evaluate at compile time"
             && msg.Contains "let static bad"
         | _ -> false)
        (sprintf "got %A" crossModErrs)

    // A lambda-valued static declares a function, not a foldable value —
    // exempt from the assertion.
    let lambdaSrc =
        "let static twice = lambda(x) -> x * 2.0\n" +
        "let y = twice(2.0)\n"
    let lambdaErrs = allErrs lambdaSrc
    check "static assertion: lambda static stays legal"
        (List.isEmpty lambdaErrs)
        (sprintf "got %A" lambdaErrs)

    // A destructured static folds (leaves bound by bindPattern) — no error.
    let tupleSrc =
        "static function pr() -> (Int64, Int64) = (4, 1)\n" +
        "let static (a, b) = pr()\n" +
        "let r = a + b\n"
    let tupleErrs = allErrs tupleSrc
    check "static assertion: destructured static folds without error"
        (List.isEmpty tupleErrs)
        (sprintf "got %A" tupleErrs)

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
    let eofLine = parseErrLine "let x = (1 + 2"
    check "parse error at EOF reports a real line, not 0"
        (match eofLine with Some n -> n > 0 | None -> false)
        (sprintf "got %A" eofLine)
    let eofLineMulti = parseErrLine "let a = 1\nfunction f(x"
    check "parse error at EOF reports the LAST line (multi-line source)"
        (eofLineMulti = Some 2)
        (sprintf "got %A" eofLineMulti)

    // (d) Expected-token errors read like prose — no raw DU noise (TokLParen…).
    let parseErrMsg (src: string) : string =
        match Parser.parseProgram src with Error e -> e.Message | Ok _ -> ""
    let expMsg = parseErrMsg "function f(x y) -> Int64 = 1\n"
    check "expected-token message is humanized (identifier 'y', not TokIdent)"
        (expMsg.Contains "Expected ')'" && expMsg.Contains "identifier 'y'" && not (expMsg.Contains "Tok"))
        ($"got: {expMsg}")
    let unexpMsg = parseErrMsg "let x = )\n"
    check "unexpected-token message carries no raw DU constructor name"
        (unexpMsg.Contains "')'" && not (unexpMsg.Contains "Tok"))
        ($"got: {unexpMsg}")

    // (e) ParseError.Code is classified: BL1001 expected-token, BL1002 EOF,
    //     BL1999 generic.
    let parseErrCode (src: string) : string =
        match Parser.parseProgram src with Error e -> e.Code | Ok _ -> "OK"
    check "parse error code: BL1001 (expected token)"
        (parseErrCode "function f(x y) -> Int64 = 1\n" = "BL1001")
        ($"""got {(parseErrCode "function f(x y) -> Int64 = 1\n")}""")
    check "parse error code: BL1002 (unexpected EOF)"
        (parseErrCode "function f(x" = "BL1002")
        ($"""got {(parseErrCode "function f(x")}""")
    check "parse error code: BL1999 (generic)"
        (parseErrCode "let x = )\n" = "BL1999")
        ($"""got {(parseErrCode "let x = )\n")}""")

    printFooter "Error Locations" [$"{passed} passed"; $"{failed} failure(s)"]
    { Block = "Error Locations"; Passed = passed; Failed = failed; Skipped = 0; FailedNames = failedNames }
