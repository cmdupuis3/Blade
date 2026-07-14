// ============================================================================
// Ide.fs - Machine-readable check output for editor tooling
// ============================================================================
//
// Implements `blade ide check --json <file>`: parse + typecheck (no codegen)
// and emit one JSON object on stdout for the VS Code extension (see the
// extension README at _blade_ide for the consumer-side contract):
//
//   { "version": 1,
//     "diagnostics": [ { severity, line, col, endLine, endCol, message } ],
//     "bindings":    [ { name, kind, line, col, type,
//                        doc?,                       // comment block above
//                        params?: [{name,type,doc?}],// functions only
//                        ret? } ] }                  // functions only
//
// All positions are 1-based. Diagnostics carry statement-granularity spans
// (the finest the AST tracks today). Bindings cover top-level lets/statics,
// functions (rendered as a signature), function parameters, and
// function-body let/for-in bindings.
//
// Doc comments: the contiguous run of `//` lines directly above a binding's
// line is its documentation (corpus directives like `// TEST:` and pure
// ===== banner lines are filtered out). A doc line of the form
// `name: description` documents the parameter of that name, Ionide-style.
//
// The TypedProgram carries resolved types but no spans (TypedExpr.Span is
// noSpan until expression-level spans land), so binding positions come from
// a parallel walk of the UNTYPED AST joined by (scope, name) in declaration
// order. Compiler-generated declarations (ML/PPL/grad expansion) find no
// source span and are silently skipped.

module Blade.Ide

open System.IO
open System.Text
open System.Text.RegularExpressions
open System.Collections.Generic
open Blade.Ast
open Blade.Types
open Blade.IR
open Blade.TypedAst

// ----------------------------------------------------------------------------
// JSON emission (hand-rolled: tiny payload, zero dependencies)
// ----------------------------------------------------------------------------

let private jsonEscape (s: string) =
    let sb = StringBuilder(s.Length + 8)
    for c in s do
        match c with
        | '"' -> sb.Append "\\\"" |> ignore
        | '\\' -> sb.Append "\\\\" |> ignore
        | '\n' -> sb.Append "\\n" |> ignore
        | '\r' -> sb.Append "\\r" |> ignore
        | '\t' -> sb.Append "\\t" |> ignore
        | c when int c < 32 -> sb.AppendFormat("\\u{0:x4}", int c) |> ignore
        | c -> sb.Append c |> ignore
    sb.ToString()

type private Diag = {
    Severity: string
    Line: int
    Col: int
    EndLine: int
    EndCol: int
    Message: string
}

type private ParamInfo = {
    PName: string
    PType: string
    PDoc: string
}

type private BindingInfo = {
    Name: string
    Kind: string
    Line: int
    Col: int
    TypeStr: string
    Doc: string
    Params: ParamInfo list   // non-empty only for functions
    Ret: string option       // Some only for functions
    Where: string list       // where-clause conjuncts, functions only
}

/// Clamp a span to 1-based sanity; noSpan (all zeros) becomes 1:1-1:1.
let private clampSpan (s: Span) =
    let line = max 1 s.StartLine
    let col = max 1 s.StartCol
    let endLine = max line s.EndLine
    let endCol = if s.EndCol >= 1 then s.EndCol else col
    (line, col, endLine, endCol)

let private renderJson (diags: Diag list) (bindings: BindingInfo list) =
    let sb = StringBuilder()
    sb.Append "{\"version\":1,\"diagnostics\":[" |> ignore
    diags
    |> List.iteri (fun i d ->
        if i > 0 then sb.Append ',' |> ignore
        sb.AppendFormat(
            "{{\"severity\":\"{0}\",\"line\":{1},\"col\":{2},\"endLine\":{3},\"endCol\":{4},\"message\":\"{5}\"}}",
            d.Severity, d.Line, d.Col, d.EndLine, d.EndCol, jsonEscape d.Message) |> ignore)
    sb.Append "],\"bindings\":[" |> ignore
    bindings
    |> List.iteri (fun i b ->
        if i > 0 then sb.Append ',' |> ignore
        sb.AppendFormat(
            "{{\"name\":\"{0}\",\"kind\":\"{1}\",\"line\":{2},\"col\":{3},\"type\":\"{4}\"",
            jsonEscape b.Name, jsonEscape b.Kind, b.Line, b.Col, jsonEscape b.TypeStr) |> ignore
        if b.Doc <> "" then
            sb.AppendFormat(",\"doc\":\"{0}\"", jsonEscape b.Doc) |> ignore
        match b.Ret with
        | Some ret ->
            sb.Append ",\"params\":[" |> ignore
            b.Params
            |> List.iteri (fun j p ->
                if j > 0 then sb.Append ',' |> ignore
                sb.AppendFormat("{{\"name\":\"{0}\",\"type\":\"{1}\"", jsonEscape p.PName, jsonEscape p.PType) |> ignore
                if p.PDoc <> "" then sb.AppendFormat(",\"doc\":\"{0}\"", jsonEscape p.PDoc) |> ignore
                sb.Append '}' |> ignore)
            sb.AppendFormat("],\"ret\":\"{0}\"", jsonEscape ret) |> ignore
            if not b.Where.IsEmpty then
                sb.Append ",\"where\":[" |> ignore
                b.Where
                |> List.iteri (fun j w ->
                    if j > 0 then sb.Append ',' |> ignore
                    sb.AppendFormat("\"{0}\"", jsonEscape w) |> ignore)
                sb.Append ']' |> ignore
        | None -> ()
        sb.Append '}' |> ignore)
    sb.Append "]}" |> ignore
    sb.ToString()

// ----------------------------------------------------------------------------
// Type rendering
// ----------------------------------------------------------------------------

/// Collect Id -> nominal-name entries from the index types embedded in a
/// type, so ppIRTypeIn renders `Idx<Lat>` instead of a raw extent. Index
/// aliases stamp their name into Tag at every annotation use site (TyNamed
/// lowering copies the registered record), which sidesteps the fresh-Id-per-
/// occurrence problem a decl-keyed map would have. Internal structural tags
/// (`__raggedidx` etc.) are excluded.
let rec private indexNamesOf (t: IRType) : (IRId * string) list =
    match t with
    | ArrayElem arr ->
        let fromIndices =
            arr.IndexTypes
            |> List.choose (fun idx ->
                match idx.Tag with
                | Some tag when not (tag.StartsWith "__") -> Some (idx.Id, tag)
                | _ -> None)
        fromIndices @ indexNamesOf arr.ElemType
    | IRTTuple ts -> ts |> List.collect indexNamesOf
    | _ -> []

let private ppType (t: IRType) : string =
    ppIRTypeIn (indexNamesOf t |> Map.ofList) t

/// Multi-line function signature: each parameter and the return type on its
/// own line (requested hover style — long array types stay readable).
let private formatFunctionSig (ps: (string * string) list) (ret: string) =
    match ps with
    | [] -> sprintf "() -> %s" ret
    | _ ->
        let paramLines = ps |> List.map (fun (n, t) -> sprintf "    %s: %s" n t)
        sprintf "(\n%s\n) -> %s" (String.concat ",\n" paramLines) ret

// ----------------------------------------------------------------------------
// Doc comments
// ----------------------------------------------------------------------------

let private directiveRe = Regex(@"^(TEST|EXPECT|MODULE|EXPECT_OUTPUT|EXPECT_ERROR)\b", RegexOptions.Compiled)

/// A line that is only banner punctuation (`// ====...`) — filtered from docs.
let private isBanner (s: string) =
    s.Length > 0 && s |> Seq.forall (fun c -> c = '=' || c = '-' || c = '*' || c = '#')

/// The contiguous `//` comment block directly above 1-based line `line`,
/// stripped of comment markers, directives, and banner lines.
let private docAbove (lines: string[]) (line: int) : string =
    let acc = ResizeArray<string>()
    let mutable i = line - 2   // 0-based index of the line above the binding
    let mutable go = true
    while go && i >= 0 do
        let t = lines.[i].TrimStart()
        if t.StartsWith "//" then
            acc.Add(t.TrimStart('/').Trim())
            i <- i - 1
        else
            go <- false
    let cleaned =
        acc
        |> Seq.rev
        |> Seq.filter (fun l -> not (directiveRe.IsMatch l) && not (isBanner l))
        |> List.ofSeq
    // Drop leading/trailing blank lines the filtering may have exposed.
    let rec trimEnds = function
        | "" :: rest -> trimEnds rest
        | xs -> xs
    cleaned |> trimEnds |> List.rev |> trimEnds |> List.rev |> String.concat "\n"

/// Ionide-style per-parameter doc: a doc-block line of the form
/// `name: description` (optionally bulleted) documents parameter `name`.
let private paramDocIn (doc: string) (pname: string) : string =
    if doc = "" then ""
    else
        let re = Regex(sprintf @"^[\s\-\*]*%s\s*[:—-]\s*(.+)$" (Regex.Escape pname))
        doc.Split('\n')
        |> Array.tryPick (fun l ->
            let m = re.Match l
            if m.Success then Some (m.Groups.[1].Value.Trim()) else None)
        |> Option.defaultValue ""

// ----------------------------------------------------------------------------
// Untyped-side span collection: (scopeKey, name, span, kind option) in
// declaration order. scopeKey is "" at module level, the function name
// inside a function body.
// ----------------------------------------------------------------------------

let rec private patternNames (p: Pattern) : string list =
    match p with
    | PatVar name -> [name]
    | PatTuple ps -> ps |> List.collect patternNames
    | PatCons (a, b) -> patternNames a @ patternNames b
    | PatTyped (inner, _) -> patternNames inner
    | PatGuarded (inner, _) -> patternNames inner
    | PatStruct (_, fields) -> fields |> List.collect (snd >> patternNames)
    | PatVariant (_, inner) -> inner |> Option.map patternNames |> Option.defaultValue []
    | PatWildcard | PatLit _ -> []

/// Binding-keyword kind from the surface syntax. TypedBinding.IsMutable is
/// not usable for this — module-level bindings come back mutable regardless
/// of the `mut` keyword — so the source AST is the authority.
let private bindingKind (b: Binding) =
    match b.Mutability with
    | BindMut -> "let mut"
    | BindConst -> "let const"
    | BindLet -> "let"

let private collectSourceBindings (prog: Ast.Program) =
    // Some kind for let-style bindings (surface keyword is authoritative),
    // None where the typed side names the kind (function / param).
    let acc = ResizeArray<string * string * Span * string option>()
    let rec walkStmts (scope: string) (stmts: Stmt list) (declSpan: Span) =
        for s in stmts do
            let (span, inner) =
                match s with
                | StmtSpanned (inner, sp) -> (sp, unwrapStmt inner)
                | other -> (declSpan, unwrapStmt other)
            match inner with
            | StmtLet b -> for n in patternNames b.Pattern do acc.Add(scope, n, span, Some (bindingKind b))
            | StmtForIn (v, _, body) ->
                acc.Add(scope, v, span, Some "for")
                walkStmts scope body span
            | _ -> ()
    let walkFuncBody (scope: string) (body: Expr) (declSpan: Span) =
        match body with
        | ExprBlock (stmts, _) -> walkStmts scope stmts declSpan
        | _ -> ()
    let addFunc (f: FunctionDecl) (span: Span) =
        acc.Add("", f.Name, span, None)
        for p in f.Params do acc.Add(f.Name, p.Name, span, None)
        walkFuncBody f.Name f.Body span
    for m in prog.Modules do
        for ld in m.Decls do
            match ld.Value with
            | DeclLet b ->
                for n in patternNames b.Pattern do acc.Add("", n, ld.Span, Some (bindingKind b))
            | DeclStatic b ->
                for n in patternNames b.Pattern do acc.Add("", n, ld.Span, Some "static")
            | DeclFunction f -> addFunc f ld.Span
            | DeclImpl impl -> for f in impl.Methods do addFunc f ld.Span
            | _ -> ()
    acc

// ----------------------------------------------------------------------------
// Typed-side collection, in decl order.
// ----------------------------------------------------------------------------

/// Render a function's where-clause as displayable conjunct strings:
/// comm groups, parallelization strategies, and open custom conjuncts
/// (indep etc. from the Constraints registry). TDim specs are internal
/// shape scaffolding and not shown.
let private whereConjuncts (wc: WhereClause option) : string list =
    match wc with
    | None -> []
    | Some w ->
        let comms =
            w.Commutativity
            |> List.map (fun group -> sprintf "comm(%s)" (String.concat ", " group))
        let pars =
            w.Parallel
            |> List.map (function
                | Omp s ->
                    let vars = s.Vars |> List.map (fun (v, n) -> sprintf "%s: %d" v n)
                    sprintf "omp(%s)" (String.concat ", " vars)
                | Cuda s -> sprintf "cuda(block: %d)" s.BlockSize)
        let customs =
            w.Custom
            |> List.map (fun (name, args) -> sprintf "%s(%s)" name (String.concat ", " args))
        comms @ pars @ customs

type private TypedEntry = {
    Scope: string
    EName: string
    EKind: string
    ETypeStr: string
    EParams: (string * string) list
    ERet: string option
    EWhere: string list
}

let private collectTypedBindings (tp: TypedProgram) =
    let acc = ResizeArray<TypedEntry>()
    let add scope name kind tyStr =
        acc.Add { Scope = scope; EName = name; EKind = kind; ETypeStr = tyStr
                  EParams = []; ERet = None; EWhere = [] }
    let rec walkTStmts (scope: string) (stmts: TypedStmt list) =
        for s in stmts do
            match s with
            | TStmtLet b ->
                add scope b.Name "let" (ppType b.Type)
                for (n, _, t) in b.SubBindings do add scope n "let" (ppType t)
            | TStmtForIn (v, _, _, _, body) ->
                add scope v "for" (ppType (IRTScalar ETInt64))
                walkTStmts scope body
            | _ -> ()
    let walkFuncBody (scope: string) (body: TypedExpr) =
        match body.Kind with
        | TExprBlock (stmts, _) -> walkTStmts scope stmts
        | _ -> ()
    let addFunc (f: TypedFunctionDecl) =
        let ps = f.Params |> List.map (fun p -> (p.Name, ppType p.Type))
        let ret = ppType f.ReturnType
        let kind = if f.IsStatic then "static function" else "function"
        acc.Add { Scope = ""; EName = f.Name; EKind = kind
                  ETypeStr = formatFunctionSig ps ret
                  EParams = ps; ERet = Some ret
                  EWhere = whereConjuncts f.WhereClause }
        for p in f.Params do add f.Name p.Name "param" (ppType p.Type)
        walkFuncBody f.Name f.Body
    for m in tp.Modules do
        for d in m.Decls do
            match d with
            | TDeclLet b ->
                add "" b.Name "let" (ppType b.Type)
                for (n, _, t) in b.SubBindings do add "" n "let" (ppType t)
            | TDeclStatic b ->
                add "" b.Name "static" (ppType b.Type)
                for (n, _, t) in b.SubBindings do add "" n "static" (ppType t)
            | TDeclFunction f -> addFunc f
            | TDeclImpl impl -> for f in impl.Methods do addFunc f
            | _ -> ()
    acc

/// Join typed bindings to source spans by (scope, name), consuming spans in
/// declaration order so shadowed/reused names pair up positionally. Typed
/// decls with no source span (compiler-generated) are dropped. The surface
/// keyword kind (let / let mut / static / for) wins over the typed-side kind
/// when the source recorded one.
let private joinBindings (prog: Ast.Program) (tp: TypedProgram) (sourceLines: string[]) : BindingInfo list =
    let spans = Dictionary<string, Queue<Span * string option>>()
    for (scope, name, span, kindOpt) in collectSourceBindings prog do
        let key = scope + " " + name
        match spans.TryGetValue key with
        | true, q -> q.Enqueue((span, kindOpt))
        | _ ->
            let q = Queue<Span * string option>()
            q.Enqueue((span, kindOpt))
            spans.[key] <- q
    // Memoize doc blocks per source line: params share their function's line
    // (param-level spans don't exist), so the block is fetched repeatedly.
    let docCache = Dictionary<int, string>()
    let docAt line =
        match docCache.TryGetValue line with
        | true, d -> d
        | _ ->
            let d = docAbove sourceLines line
            docCache.[line] <- d
            d
    [ for e in collectTypedBindings tp do
        let key = e.Scope + " " + e.EName
        match spans.TryGetValue key with
        | true, q when q.Count > 0 ->
            let (span, srcKind) = q.Dequeue()
            let (line, col, _, _) = clampSpan span
            let kind = srcKind |> Option.defaultValue e.EKind
            let block = docAt line
            // A parameter's doc is its `name: ...` line in the enclosing
            // function's block. Function summaries drop those lines (they
            // travel on params[] instead, Ionide-style); everything else
            // gets the whole block.
            let doc =
                if e.EKind = "param" then paramDocIn block e.EName
                elif not e.EParams.IsEmpty && block <> "" then
                    let paramRes =
                        e.EParams
                        |> List.map (fun (n, _) ->
                            Regex(sprintf @"^[\s\-\*]*%s\s*[:—-]" (Regex.Escape n)))
                    block.Split('\n')
                    |> Array.filter (fun l -> paramRes |> List.forall (fun re -> not (re.IsMatch l)))
                    |> String.concat "\n"
                    |> fun s -> s.Trim()
                else block
            let ps = e.EParams |> List.map (fun (n, t) -> { PName = n; PType = t; PDoc = paramDocIn block n })
            yield { Name = e.EName; Kind = kind; Line = line; Col = col
                    TypeStr = e.ETypeStr; Doc = doc; Params = ps; Ret = e.ERet
                    Where = e.EWhere }
        | _ -> () ]

// ----------------------------------------------------------------------------
// Entry point
// ----------------------------------------------------------------------------

/// `blade ide check --json <file>`: JSON diagnostics + binding types on
/// stdout. Exit 0 = clean, 1 = errors (the JSON is emitted either way).
let ideCheck (filePath: string) : int =
    let mutable exitCode = 0
    let diags = ResizeArray<Diag>()
    let mutable bindings = []
    if not (File.Exists filePath) then
        diags.Add { Severity = "error"; Line = 1; Col = 1; EndLine = 1; EndCol = 1
                    Message = sprintf "File not found: %s" filePath }
        exitCode <- 1
    else
        let source = File.ReadAllText filePath
        match Blade.Parser.parseProgram source with
        | Error e ->
            let line = max 1 e.Line
            let col = max 1 e.Col
            diags.Add { Severity = "error"; Line = line; Col = col; EndLine = line; EndCol = col
                        Message = e.Message }
            exitCode <- 1
        | Ok program ->
            match Blade.TypeCheck.typeCheck program with
            | Error errors ->
                for e in errors do
                    let (line, col, endLine, endCol) = clampSpan e.Span
                    let msg =
                        let baseMsg = Blade.TypeEnv.formatTypeError e.Error
                        match e.Context with
                        | [] -> baseMsg
                        | ctx -> sprintf "%s (%s)" baseMsg (String.concat "; " (List.rev ctx))
                    diags.Add { Severity = "error"; Line = line; Col = col
                                EndLine = endLine; EndCol = endCol; Message = msg }
                exitCode <- 1
            | Ok (typedProg, _, warnings) ->
                for w in warnings do
                    diags.Add { Severity = "warning"; Line = 1; Col = 1; EndLine = 1; EndCol = 1
                                Message = w }
                let sourceLines = source.Replace("\r\n", "\n").Split('\n')
                bindings <- joinBindings program typedProg sourceLines
    printfn "%s" (renderJson (List.ofSeq diags) bindings)
    exitCode
