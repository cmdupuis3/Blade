// Blade-DSL Parser
// Parser with proper precedence and error handling

module Blade.Parser

open Blade.Ast
open Blade.Lexer

// Parser Types

type ParseError = {
    Message: string
    Line: int
    Col: int
    // Exclusive end of the offending span (defaults to a point at Line/Col for
    // legacy call sites). Stable BLxxxx code for classification: BL1001
    // expected-token, BL1002 unexpected-EOF, BL1999 generic parse error.
    EndLine: int
    EndCol: int
    Code: string
}

type ParseResult<'T> = Result<'T * Token list, ParseError>

// Parser-wide mutable state (per-parse)

/// The source file currently being parsed. Set by parseMultiSource /
/// parseProgramWithFile before parsing each file and reset afterwards; every
/// span the parser constructs stamps this into Span.File.
let mutable private currentFile : string option = None

/// End position (line, col) of the input, used to report EOF errors at the end
/// of the last token instead of 0:0. Refreshed per file when tokenizing.
let mutable private lastTokenEnd : int * int = (0, 0)

let private setEofFrom (tokens: Token list) =
    match List.tryLast tokens with
    | Some t -> lastTokenEnd <- (t.EndLine, t.EndCol)
    | None -> lastTokenEnd <- (0, 0)

// Basic Combinators

let success value remaining : ParseResult<'T> = Ok (value, remaining)

/// Generic parse error (BL1999), end defaults to a point at line:col.
let error msg line col : ParseResult<'T> =
    Error { Message = msg; Line = line; Col = col; EndLine = line; EndCol = col; Code = "BL1999" }

/// Coded parse error with a caller-supplied BLxxxx code; point end at line:col.
let errorC code msg line col : ParseResult<'T> =
    Error { Message = msg; Line = line; Col = col; EndLine = line; EndCol = col; Code = code }

/// Coded parse error with an explicit end span.
let errorFull code msg line col endLine endCol : ParseResult<'T> =
    Error { Message = msg; Line = line; Col = col; EndLine = endLine; EndCol = endCol; Code = code }

/// ParseError -> unified Diagnostic. The file is supplied by the parse
/// entry point (the error record does not carry it).
let diagnosticOfParseError (file: string option) (e: ParseError) : Blade.Diagnostics.Diagnostic =
    let span : Span =
        { StartLine = e.Line; StartCol = e.Col
          EndLine = e.EndLine; EndCol = e.EndCol; File = file }
    Blade.Diagnostics.mkError e.Code Blade.Diagnostics.PhParse span e.Message

/// Unexpected-EOF error (BL1002) reported at the end of the last token.
let errorEof msg : ParseResult<'T> =
    let (l, c) = lastTokenEnd
    Error { Message = msg; Line = l; Col = c; EndLine = l; EndCol = c; Code = "BL1002" }

/// Human-readable rendering of a token kind for error messages, e.g.
///   keyword 'let'   '('   identifier 'foo'   integer literal 42   end of file
/// Avoids leaking raw DU constructor names (TokLParen, TokKeyword, etc).
let private keywordText =
    // Reverse of Lexer.keywords (a Map string->Keyword): canonical spelling per keyword.
    keywords |> Map.toList |> List.map (fun (s, k) -> (k, s)) |> Map.ofList
let describeToken (kind: TokenKind) : string =
    match kind with
    | TokInt v -> sprintf "integer literal %d" v
    | TokFloat v -> sprintf "float literal %g" v
    | TokString s -> sprintf "string literal \"%s\"" s
    | TokChar c -> sprintf "character literal '%c'" c
    | TokBool b -> sprintf "boolean literal %b" b
    | TokIdent n -> sprintf "identifier '%s'" n
    | TokKeyword kw ->
        match Map.tryFind kw keywordText with
        | Some s -> sprintf "keyword '%s'" s
        | None -> sprintf "keyword %A" kw
    | TokOp s -> sprintf "'%s'" s
    | TokNamedInfix s -> sprintf "operator ':%s:'" s
    | TokLParen -> "'('"
    | TokRParen -> "')'"
    | TokLBracket -> "'['"
    | TokRBracket -> "']'"
    | TokLBrace -> "'{'"
    | TokRBrace -> "'}'"
    | TokComma -> "','"
    | TokSemi -> "';'"
    | TokColon -> "':'"
    | TokColonColon -> "'::'"
    | TokDot -> "'.'"
    | TokDotDot -> "'..'"
    | TokPipe -> "'|'"
    | TokUnderscore -> "'_'"
    | TokAt -> "'@'"
    | TokHash -> "'#'"
    | TokQuestion -> "'?'"
    | TokNewline -> "end of line"
    | TokEOF -> "end of file"
    | TokError s -> sprintf "invalid token (%s)" s

let currentPos (tokens: Token list) =
    match tokens with
    | t :: _ -> t.Line, t.Col
    | [] -> lastTokenEnd

/// End position (line, col) of the last meaningful (non-terminator) token
/// consumed advancing from `before` to `after` (`after` must be a suffix of
/// `before`). Newline/semi terminators are excluded so the span stops at the
/// statement's real end rather than overshooting to the next token's start.
/// Falls back to the given start position when nothing was consumed.
let consumedEnd (before: Token list) (after: Token list) (fallbackLine: int) (fallbackCol: int) : int * int =
    let n = List.length before - List.length after
    if n <= 0 then (fallbackLine, fallbackCol)
    else
        let meaningful =
            before
            |> List.truncate n
            |> List.filter (fun t -> match t.Kind with TokNewline | TokSemi -> false | _ -> true)
        match List.tryLast meaningful with
        | Some t -> (t.EndLine, t.EndCol)
        | None -> (fallbackLine, fallbackCol)

/// Build a Span from a single token, stamped with the current file.
let spanOfToken (t: Token) : Span =
    { StartLine = t.Line; StartCol = t.Col; EndLine = t.EndLine; EndCol = t.EndCol; File = currentFile }

/// Span of the head token of `tokens` (single-token productions: ExprVar,
/// ExprLit, PatVar, keyword atoms). noSpan when the list is empty (unreachable
/// for the call sites, which already matched a `Some` head).
let private headSpan (tokens: Token list) : Span =
    match tokens with
    | t :: _ -> spanOfToken t
    | [] -> noSpan

/// Real span for a multi-token production: from the first token of `startToks`
/// to the last meaningful token consumed reaching `remaining`. The natural
/// range for delimited/keyword-led forms (calls, blocks, formers).
let private rangeSpan (startToks: Token list) (remaining: Token list) : Span =
    let sL, sC = currentPos startToks
    let eL, eC = consumedEnd startToks remaining sL sC
    { StartLine = sL; StartCol = sC; EndLine = eL; EndCol = eC; File = currentFile }

/// Build an Expr whose span covers the production from `startToks` to `remaining`.
let private mkE (startToks: Token list) (remaining: Token list) (kind: ExprKind) : Expr =
    mkExpr (rangeSpan startToks remaining) kind

/// Build a Pattern whose span covers the production from `startToks` to `remaining`.
let private mkP (startToks: Token list) (remaining: Token list) (kind: PatternKind) : Pattern =
    mkPat (rangeSpan startToks remaining) kind

/// Expected-token error: `expected` is the wanted kind, `tokens` the stream
/// (head = actual token, empty = EOF). Humanized message + BL1001, except
/// "got end of file" which is classified BL1002.
let expectedError (expected: TokenKind) (tokens: Token list) : ParseResult<'T> =
    let msg actual = sprintf "Expected %s but got %s" (describeToken expected) (describeToken actual)
    match tokens with
    | t :: _ when t.Kind = TokEOF -> errorFull "BL1002" (msg TokEOF) t.Line t.Col t.EndLine t.EndCol
    | t :: _ -> errorFull "BL1001" (msg t.Kind) t.Line t.Col t.EndLine t.EndCol
    | [] ->
        let (l, c) = lastTokenEnd
        errorFull "BL1002" (msg TokEOF) l c l c

let peek (tokens: Token list) =
    match tokens with
    | t :: _ -> Some t.Kind
    | [] -> None

/// Peek, skipping any leading newlines
let advance (tokens: Token list) =
    match tokens with
    | _ :: rest -> rest
    | [] -> []

/// Skip all leading newlines
let rec skipNL (tokens: Token list) =
    match tokens with
    | t :: rest when t.Kind = TokNewline -> skipNL rest
    | _ -> tokens

/// Is this token an infix combinator operator that can never start a statement?
/// Used for implicit line continuation: if a line starts with one of these,
/// it's a continuation of the previous expression.
let isCombinatorOp (kind: TokenKind) : bool =
    match kind with
    | TokOp "|>" | TokOp "|@>"
    | TokOp "<@>" | TokOp "<$>"
    | TokOp "<&>" | TokOp "<&!>"
    | TokOp "<|>" | TokOp "<|:>"
    | TokOp ">>=" | TokOp ">>@" | TokOp "@>>" | TokOp ">>"
    | TokOp "<*>" -> true
    | _ -> false

/// Consume `.segment` runs after a leading identifier in TYPE position,
/// joining them into one dotted name. The only qualified type paths today are
/// the type-provider axis types (`store.index.y`), resolved through the
/// ordinary TypeDefs lookup under that joined key (the same trick StaticEval
/// uses to key qualified statics, "Module.name"). `..` lexes as its own
/// TokDotDot, so a range can never be swallowed here.
let rec parseDottedTypeName (name: string) (tokens: Token list) : string * Token list =
    match peek tokens with
    | Some TokDot ->
        let afterDot = advance tokens
        match peek afterDot with
        | Some (TokIdent seg) -> parseDottedTypeName (name + "." + seg) (advance afterDot)
        | _ -> (name, tokens)
    | _ -> (name, tokens)

let peekContinuation (tokens: Token list) : TokenKind option * Token list =
    let skipped = skipNL tokens
    match skipped with
    | t :: _ when isCombinatorOp t.Kind -> (Some t.Kind, skipped)
    | _ -> (peek tokens, tokens)

let expect kind (tokens: Token list) : ParseResult<Token> =
    match tokens with
    | t :: rest when t.Kind = kind -> Ok (t, rest)
    | _ -> expectedError kind tokens

let expectIdent (tokens: Token list) : ParseResult<string> =
    match tokens with
    | t :: rest ->
        match t.Kind with
        | TokIdent name -> Ok (name, rest)
        | TokEOF -> errorFull "BL1002" (sprintf "Expected identifier but got %s" (describeToken TokEOF)) t.Line t.Col t.EndLine t.EndCol
        | _ -> errorFull "BL1001" (sprintf "Expected identifier but got %s" (describeToken t.Kind)) t.Line t.Col t.EndLine t.EndCol
    | [] -> errorEof "Expected identifier but got end of file"

/// Expect a closing > for type parameters. Handles >> by splitting: consume
/// one > and leave one > (the standard Rust/Java7+/C# resolution of the
/// >> shift-vs-two-type-closes ambiguity).
let expectGt (tokens: Token list) : ParseResult<unit> =
    match tokens with
    | t :: rest when t.Kind = TokOp ">" ->
        Ok ((), rest)
    | t :: rest when t.Kind = TokOp ">>" ->
        // Split >>: consume first >, leave second > with adjusted position
        let remainingGt = { t with Kind = TokOp ">"; Col = t.Col + 1; Length = 1 }
        Ok ((), remainingGt :: rest)
    | t :: _ when t.Kind = TokEOF -> errorFull "BL1002" (sprintf "Expected '>' but got %s" (describeToken TokEOF)) t.Line t.Col t.EndLine t.EndCol
    | t :: _ -> errorFull "BL1001" (sprintf "Expected '>' but got %s" (describeToken t.Kind)) t.Line t.Col t.EndLine t.EndCol
    | [] -> errorEof "Expected '>' but got end of file"

// Bind operator for chaining parsers
let (>>=) (result: ParseResult<'a>) (f: 'a -> Token list -> ParseResult<'b>) : ParseResult<'b> =
    match result with
    | Ok (v, rest) -> f v rest
    | Error e -> Error e

// Forward reference for body parser (inline or block)
let parseBodyRef : (Token list -> ParseResult<Expr>) ref = ref (fun _ -> Error { Message = "Not initialized"; Line = 0; Col = 0; EndLine = 0; EndCol = 0; Code = "BL1999" })
let parseBody tokens = !parseBodyRef tokens

// Active Patterns for Token Classification (sorted by precedence). The
// (mode, op) pair returned by most of these is (Elementwise | Outer, BinOp).
let (|LiteralTok|_|) = function
    | TokInt v -> Some (LitInt v)
    | TokFloat v -> Some (LitFloat v)
    | TokBool v -> Some (LitBool v)
    | TokString v -> Some (LitString v)
    | TokChar v -> Some (LitChar v)
    | _ -> None

let (|PipelineOp|_|) = function
    | TokOp "|>" -> Some ()
    | _ -> None

let (|ChoiceOp|_|) = function
    | TokOp "<|>" -> Some OpChoice
    | TokOp "<|:>" -> Some OpFallback
    | _ -> None

let (|ParallelOp|_|) = function
    | TokOp "<&>" -> Some OpParallel
    | TokOp "<&!>" -> Some OpFusion
    | _ -> None

let (|BindOp|_|) = function
    | TokOp ">>=" -> Some OpBind
    | TokOp ">>@" -> Some OpComposeObj
    | TokOp "@>>" -> Some OpComposeMeth
    | _ -> None

let (|ApplyOp|_|) = function
    | TokOp "<@>" -> Some OpApply
    | TokOp "<$>" -> Some OpFunctor
    | _ -> None

let (|ArrayProductOp|_|) = function
    | TokOp "<*>" -> Some OpArrayProd
    | _ -> None

let (|OrOp|_|) = function
    | TokOp "||" -> Some (Elementwise, OpOr)
    | TokOp "[||]" -> Some (Outer, OpOr)
    | _ -> None

let (|AndOp|_|) = function
    | TokOp "&&" -> Some (Elementwise, OpAnd)
    | TokOp "[&&]" -> Some (Outer, OpAnd)
    | _ -> None

let (|EqualityOp|_|) = function
    | TokOp "==" -> Some (Elementwise, OpEq)
    | TokOp "!=" -> Some (Elementwise, OpNeq)
    | TokOp "[==]" -> Some (Outer, OpEq)
    | TokOp "[!=]" -> Some (Outer, OpNeq)
    | _ -> None

let (|ComparisonOp|_|) = function
    | TokOp "<" -> Some (Elementwise, OpLt)
    | TokOp "<=" -> Some (Elementwise, OpLe)
    | TokOp ">" -> Some (Elementwise, OpGt)
    | TokOp ">=" -> Some (Elementwise, OpGe)
    | TokOp "[<]" -> Some (Outer, OpLt)
    | TokOp "[<=]" -> Some (Outer, OpLe)
    | TokOp "[>]" -> Some (Outer, OpGt)
    | TokOp "[>=]" -> Some (Outer, OpGe)
    | _ -> None

let (|AdditiveOp|_|) = function
    | TokOp "+" -> Some (Elementwise, OpAdd)
    | TokOp "-" -> Some (Elementwise, OpSub)
    | TokOp "[+]" -> Some (Outer, OpAdd)
    | TokOp "[-]" -> Some (Outer, OpSub)
    | _ -> None

let (|MultiplicativeOp|_|) = function
    | TokOp "*" -> Some (Elementwise, OpMul)
    | TokOp "/" -> Some (Elementwise, OpDiv)
    | TokOp "%" -> Some (Elementwise, OpMod)
    | TokOp "[*]" -> Some (Outer, OpMul)
    | TokOp "[/]" -> Some (Outer, OpDiv)
    | TokOp "[%]" -> Some (Outer, OpMod)
    | _ -> None

let (|PowerOp|_|) = function
    | TokOp "^" -> Some (Elementwise, OpCaret)
    | TokOp "[^]" -> Some (Outer, OpCaret)
    | _ -> None

let (|UnaryOp|_|) = function
    | TokOp "-" -> Some OpNeg
    | TokOp "!" -> Some OpNot
    | _ -> None

// Helper Combinators

let optional parser (tokens: Token list) =
    match parser tokens with
    | Ok (v, rest) -> Ok (Some v, rest)
    | Error _ -> Ok (None, tokens)

let many parser (tokens: Token list) =
    let rec loop acc toks =
        match parser toks with
        | Ok (v, rest) -> loop (v :: acc) rest
        | Error _ -> Ok (List.rev acc, toks)
    loop [] tokens

/// Errors that `sepBy` (and the let-annotation slot) must PROPAGATE rather
/// than treat as "the list ends here". A code qualifies only if it can be
/// raised exclusively after the parse has committed -- BL1004 fires only once
/// the two-token lookahead `Tuple <` has matched in TYPE position, so there
/// is no legitimate backtrack it could be cutting off. Without this the
/// precise diagnostic is swallowed and the caller re-reports far away
/// ("Expected ')' but got identifier 't'" at the parameter NAME).
let isHardParseError (e: ParseError) : bool = e.Code = "BL1004"

let sepBy parser sep (tokens: Token list) =
    match parser tokens with
    | Error e when isHardParseError e -> Error e
    | Error _ -> Ok ([], tokens)
    | Ok (first, rest) ->
        let rec loop acc toks =
            match expect sep toks with
            | Error _ -> Ok (List.rev acc, toks)
            | Ok (_, afterSep) ->
                match parser afterSep with
                | Ok (v, rest') -> loop (v :: acc) rest'
                | Error e when isHardParseError e -> Error e
                | Error _ -> Ok (List.rev acc, toks)
        loop [first] rest

// Forward Reference for Expression Parser

// The only forward reference we need - expressions can be nested
let parseExprRef : (Token list -> ParseResult<Expr>) ref = 
    ref (fun _ -> failwith "parseExpr not initialized")

let parseExpr tokens = !parseExprRef tokens

// Simple expression parser for type arguments (stops at > and ,); multiplicative binds tighter than additive.

/// For-in range header context. A `for x in RANGE { body }` statement's range
/// expression is always followed by the loop-body brace, so a bare identifier
/// meeting `{` there is the start of the body, not a struct literal -- without
/// this, `for k in 0..n {` misparses as struct construction `n { ... }`. The
/// flag suppresses struct-literal parsing at the top nesting level of the
/// range expression only; delimited sub-expressions (parens, call arguments,
/// index brackets) restore it, so `for p in f(Point { x = 1 })` stays
/// expressible. AsyncLocal mirrors the compiler's other context cells
/// (parse-parallel safety).
let private noStructLiteralCtx = new System.Threading.AsyncLocal<bool>()

/// Run a sub-parse with struct literals re-enabled (delimited context).
let private allowingStructLiterals (parse: unit -> ParseResult<'a>) : ParseResult<'a> =
    let saved = noStructLiteralCtx.Value
    noStructLiteralCtx.Value <- false
    let r = parse ()
    noStructLiteralCtx.Value <- saved
    r

/// Run a sub-parse with struct literals suppressed (for-in range header).
let private suppressingStructLiterals (parse: unit -> ParseResult<'a>) : ParseResult<'a> =
    let saved = noStructLiteralCtx.Value
    noStructLiteralCtx.Value <- true
    let r = parse ()
    noStructLiteralCtx.Value <- saved
    r

let rec parseSimpleExpr (tokens: Token list) : ParseResult<Expr> =
    parseSimpleAdditive tokens

and parseSimpleAdditive (tokens: Token list) : ParseResult<Expr> =
    parseSimpleMultiplicative tokens >>= fun left rest ->
    let rec loop acc toks =
        match peek toks with
        | Some (TokOp "+") ->
            advance toks |> parseSimpleMultiplicative >>= fun right remaining ->
            loop (mkExpr (mergeSpan acc.Span right.Span) (ExprBinOp (Elementwise, OpAdd, acc, right))) remaining
        | Some (TokOp "-") ->
            advance toks |> parseSimpleMultiplicative >>= fun right remaining ->
            loop (mkExpr (mergeSpan acc.Span right.Span) (ExprBinOp (Elementwise, OpSub, acc, right))) remaining
        | _ -> success acc toks
    loop left rest

and parseSimpleMultiplicative (tokens: Token list) : ParseResult<Expr> =
    parseSimplePrimary tokens >>= fun left rest ->
    let rec loop acc toks =
        match peek toks with
        | Some (TokOp "*") ->
            advance toks |> parseSimplePrimary >>= fun right remaining ->
            loop (mkExpr (mergeSpan acc.Span right.Span) (ExprBinOp (Elementwise, OpMul, acc, right))) remaining
        | Some (TokOp "/") ->
            advance toks |> parseSimplePrimary >>= fun right remaining ->
            loop (mkExpr (mergeSpan acc.Span right.Span) (ExprBinOp (Elementwise, OpDiv, acc, right))) remaining
        | _ -> success acc toks
    loop left rest

and parseSimplePrimary (tokens: Token list) : ParseResult<Expr> =
    match peek tokens with
    | Some (TokOp "-") ->
        // Unary minus on a simple operand: needed for signed static payloads
        // such as halo<I, [-1, 0, 1]> (and negative EnumIdx keys). Binds
        // tighter than the additive/multiplicative loops, matching normal
        // unary-minus precedence; folds via StaticEval's OpNeg arm.
        advance tokens |> parseSimplePrimary >>= fun operand remaining ->
        success (mkExpr (mergeSpan (headSpan tokens) operand.Span) (ExprUnaryOp (OpNeg, operand))) remaining
    | Some (LiteralTok lit) -> success (mkExpr (headSpan tokens) (ExprLit lit)) (advance tokens)
    | Some (TokIdent name) -> success (mkExpr (headSpan tokens) (ExprVar name)) (advance tokens)
    | Some (TokKeyword KwArity) ->
        // `arity(p)` in a static payload position, e.g. `Idx<arity(args)>`:
        // the poly-pack former's extent (formalism 7.4). Resolves to a
        // literal at pack monomorphization (Ir.specializeFunction's IRArity rewrite).
        let afterArity = advance tokens
        expect TokLParen afterArity >>= fun _ afterLParen ->
        (match peek afterLParen with
         | Some (TokIdent packName) -> success packName (advance afterLParen)
         | _ ->
            let line, col = currentPos afterLParen
            error "Expected a pack parameter name in arity(...)" line col)
        >>= fun packName afterName ->
        expect TokRParen afterName >>= fun _ remaining ->
        success (mkE tokens remaining (ExprArity packName)) remaining
    | Some TokLParen ->
        // Parenthesized simple expression OR tuple of simple expressions --
        // the tuple form serves static index-type arguments whose payload is
        // tuple-structured (e.g. IrrepsIdx<[(0, 0, 2), (1, 1, 2)]>).
        advance tokens |> sepBy parseSimpleExpr TokComma >>= fun exprs afterExprs ->
        expect TokRParen afterExprs >>= fun _ remaining ->
        match exprs with
        | [] ->
            let line, col = currentPos tokens
            error "Expected simple expression inside parentheses" line col
        | [single] -> success single remaining
        | many -> success (mkE tokens remaining (ExprTuple many)) remaining
    | Some TokLBracket ->
        // Array literal inside simple expression context (e.g., EnumIdx<[1, 2, 3]>)
        advance tokens |> sepBy parseSimpleExpr TokComma >>= fun elems afterElems ->
        expect TokRBracket afterElems >>= fun _ remaining ->
        success (mkE tokens remaining (ExprArrayLit elems)) remaining
    | Some kind ->
        let line, col = currentPos tokens
        error (sprintf "Expected simple expression but got %s" (describeToken kind)) line col
    | None ->
        errorEof "Expected expression but got end of file"

// Literal Parsing

let parseLiteral (tokens: Token list) : ParseResult<Literal> =
    match tokens with
    | t :: rest ->
        match t.Kind with
        | TokInt v -> success (LitInt v) rest
        | TokFloat v -> success (LitFloat v) rest
        | TokBool v -> success (LitBool v) rest
        | TokString v -> success (LitString v) rest
        | TokChar v -> success (LitChar v) rest
        | _ -> error "Expected literal" t.Line t.Col
    | [] -> errorEof "Expected literal but got end of file"

// Type Expression Parsing

/// Parse a rank expression (for T^r syntax)
/// Can be: integer literal, arity keyword, or simple identifier
let parseRankExpr (tokens: Token list) : ParseResult<Expr> =
    match peek tokens with
    | Some (TokInt n) ->
        success (mkExpr (headSpan tokens) (ExprLit (LitInt n))) (advance tokens)
    | Some (TokKeyword KwArity) ->
        let afterArity = advance tokens
        match peek afterArity with
        | Some TokLParen ->
            advance afterArity |> fun afterLParen ->
            match peek afterLParen with
            | Some (TokIdent paramName) ->
                advance afterLParen |> expect TokRParen >>= fun _ remaining ->
                success (mkE tokens remaining (ExprArity paramName)) remaining
            | _ ->
                let line, col = currentPos afterLParen
                error "Expected parameter name in arity()" line col
        | _ ->
            let line, col = currentPos afterArity
            error "arity requires parameter name: arity(paramName)" line col
    | Some (TokIdent name) ->
        success (mkExpr (headSpan tokens) (ExprVar name)) (advance tokens)
    | Some t ->
        let line, col = currentPos tokens
        error (sprintf "Expected rank expression (integer, arity, or identifier), got %s" (describeToken t)) line col
    | None ->
        errorEof "Expected rank expression but got end of file"

/// Two-token lookahead for the tag wildcard `Base<_>`: TokUnderscore
/// immediately followed by `>` or `>>` (expectGt splits the latter). `_`
/// means "any tag" only when it is the sole type argument (same discipline
/// as `RaggedIdx<_>` below), so `_ + 1`, `_, x` etc. go to the ordinary
/// parsers. `tokens` is positioned just after the opening `<`.
let isTagWildcardArg (tokens: Token list) : bool =
    match tokens with
    | t1 :: t2 :: _ when
        t1.Kind = TokUnderscore &&
        (t2.Kind = TokOp ">" || t2.Kind = TokOp ">>") -> true
    | _ -> false

/// One argument of a type application `Base<...>`: an ordinary positional
/// type argument (a unit name, an index-type name, a type), or a named bound
/// argument `min=e` / `max=e` (formalism 2.4's bounded primitives).
type TypeArg =
    | TAPositional of TypeExpr
    | TANamed of name: string * value: Expr

/// Two-token lookahead for a named type argument: an identifier immediately
/// followed by `=`. At HEAD this shape is a hard parse error (the identifier
/// parses as a type name and the `=` then fails the `,`-or-`>` expectation),
/// so recognizing it is a strict widening of the grammar.
let isNamedTypeArg (tokens: Token list) : bool =
    match tokens with
    | t1 :: t2 :: _ ->
        (match t1.Kind with TokIdent _ -> true | _ -> false) && t2.Kind = TokOp "="
    | _ -> false

// Unit-expression grammar (shared by Unit-declaration right-hand sides and
// compound type-argument annotations like `Float<meter/second^2>`). Defined
// HERE, ahead of the parseTypeExpr group, because parseTypeArg routes into
// it; parseUnitDecl (further down) calls it too. A unit expression never
// contains `,` or `>`, so a sub-parse inside a type-argument list always
// stops cleanly before the enclosing constructor's delimiters.

/// Recover the EXACT rational a decimal literal denotes, from its shortest
/// round-trip spelling rather than from the binary double it parsed to:
/// `0.0254` is 254/10000, not the dyadic fraction that literal lands on.
/// "R" round-trips, so the digits recovered are exactly the ones that could
/// have been written. None for a non-finite or zero factor (caller rejects).
let rationalOfFloatLiteral (v: float) : (bigint * bigint) option =
    if System.Double.IsNaN v || System.Double.IsInfinity v || v = 0.0 then None
    else
        let s = v.ToString ("R", System.Globalization.CultureInfo.InvariantCulture)
        // Split off an exponent suffix first ("1.5E-05"), then the point.
        let mantissa, exp10 =
            match s.IndexOfAny [| 'e'; 'E' |] with
            | -1 -> s, 0
            | i -> s.Substring (0, i), int (s.Substring (i + 1))
        let neg = mantissa.StartsWith "-"
        let mantissa = if neg then mantissa.Substring 1 else mantissa
        let intPart, fracPart =
            match mantissa.IndexOf '.' with
            | -1 -> mantissa, ""
            | i -> mantissa.Substring (0, i), mantissa.Substring (i + 1)
        match System.Numerics.BigInteger.TryParse (intPart + fracPart) with
        | false, _ -> None
        | true, digits ->
            let num = if neg then -digits else digits
            // Net power of ten: fraction digits push down, the exponent
            // suffix pushes either way.
            let p = exp10 - fracPart.Length
            if p >= 0 then Some (num * System.Numerics.BigInteger.Pow (bigint 10, p), bigint 1)
            else Some (num, System.Numerics.BigInteger.Pow (bigint 10, -p))

/// Parse a unit expression: meters / seconds, kg * velocity, meters^2
let rec parseUnitExpr (tokens: Token list) : ParseResult<UnitExpr> =
    parseUnitTerm tokens >>= fun left rest ->
    parseUnitExprTail left rest

and parseUnitExprTail (left: UnitExpr) (tokens: Token list) : ParseResult<UnitExpr> =
    match peek tokens with
    | Some (TokOp "*") ->
        parseUnitTerm (advance tokens) >>= fun right rest ->
        parseUnitExprTail (UnitMul (left, right)) rest
    | Some (TokOp "/") ->
        parseUnitTerm (advance tokens) >>= fun right rest ->
        parseUnitExprTail (UnitDiv (left, right)) rest
    | _ -> success left tokens

and parseUnitTerm (tokens: Token list) : ParseResult<UnitExpr> =
    parseUnitAtom tokens >>= fun atom rest ->
    match peek rest with
    | Some (TokOp "^") ->
        let afterCaret = advance rest
        match peek afterCaret with
        | Some (TokInt n) -> success (UnitPow (atom, int n)) (advance afterCaret)
        // Negative exponent (`seconds^-1`, `meters^-2`): the lexer emits the
        // minus as its own operator token, so glue it back on here --
        // reciprocal units (frequencies, decay coefficients) are too common
        // to force through the a/b spelling.
        | Some (TokOp "-") ->
            (match peek (advance afterCaret) with
             | Some (TokInt n) -> success (UnitPow (atom, -(int n))) (advance (advance afterCaret))
             | _ ->
                 let line, col = currentPos (advance afterCaret)
                 error "Expected integer exponent after '^' in unit expression" line col)
        | _ ->
            let line, col = currentPos afterCaret
            error "Expected integer exponent after '^' in unit expression" line col
    | _ -> success atom rest

and parseUnitAtom (tokens: Token list) : ParseResult<UnitExpr> =
    match peek tokens with
    | Some (TokIdent name) -> success (UnitNamed name) (advance tokens)
    // The unity literal `1`: empty dims. Enables the dimensionless-quantity
    // form (`Unit levels: 1`) and reciprocal aliases (`Unit hz = 1 / seconds`).
    | Some (TokInt 1L) -> success UnitOne (advance tokens)
    // A MAGNITUDE factor (`Unit day = 86400 * second`). Dimensionless, so it
    // composes through the same mul/div/pow arms as any other atom; what it
    // contributes is the scale, not a dim.
    | Some (TokInt 0L) ->
        let line, col = currentPos tokens
        error "A unit scale factor cannot be zero (a zero-magnitude unit has no inverse)" line col
    | Some (TokInt n) -> success (UnitScaleLit (bigint n, bigint 1)) (advance tokens)
    | Some (TokFloat v) ->
        (match rationalOfFloatLiteral v with
         | Some (num, den) -> success (UnitScaleLit (num, den)) (advance tokens)
         | None ->
             let line, col = currentPos tokens
             error (sprintf "'%g' is not usable as a unit scale factor (must be finite and non-zero)" v) line col)
    | Some TokLParen ->
        parseUnitExpr (advance tokens) >>= fun expr afterExpr ->
        expect TokRParen afterExpr >>= fun _ remaining ->
        success expr remaining
    | _ ->
        let line, col = currentPos tokens
        error "Expected unit name, '1', or '(' in unit expression" line col

/// Lookahead for a COMPOUND unit expression in type-argument position.
/// Deliberately conservative -- it claims only shapes the type grammar
/// cannot already mean, so every existing type-argument spelling parses
/// exactly as before:
///   - a LONE unit name stays TyNamed (`Float<meter>`, `Float<speed>`);
///   - `name^INT` stays TyVar (`T^2`); a unit name there is disambiguated
///     at LOWERING (units-first policy), not in the grammar;
///   - claimed here: `name * ...` / `name / ...`, `name^-INT` (negative
///     exponents never parsed before), `name^INT` followed by `*`/`/`,
///     a leading unity `1`, a leading MAGNITUDE followed by `*`/`/`
///     (`86400 * second`, `2 * pi / day`, `0.5 * meter`), and a
///     parenthesized group opening with `(name *|/|^ ...` (a tuple type
///     never continues a name that way).
///
/// The magnitude arms exist so an annotation can spell the same thing a
/// `Unit` RHS can. Without them a scale factor parses only when it is not
/// the FIRST token (`Float<day / 2>` worked, `Float<2 * pi / day>` did not),
/// which is precisely the position a conversion target gets written in. No
/// type argument in the language starts with a numeric literal followed by
/// `*` or `/`, so nothing else can mean this.
let isUnitExprArg (tokens: Token list) : bool =
    match tokens |> List.truncate 4 |> List.map (fun t -> t.Kind) with
    | TokInt 1L :: _ -> true
    | TokInt _ :: TokOp "*" :: _ -> true
    | TokInt _ :: TokOp "/" :: _ -> true
    | TokFloat _ :: TokOp "*" :: _ -> true
    | TokFloat _ :: TokOp "/" :: _ -> true
    | TokIdent _ :: TokOp "*" :: _ -> true
    | TokIdent _ :: TokOp "/" :: _ -> true
    | TokIdent _ :: TokOp "^" :: TokOp "-" :: _ -> true
    | TokIdent _ :: TokOp "^" :: TokInt _ :: TokOp "*" :: _ -> true
    | TokIdent _ :: TokOp "^" :: TokInt _ :: TokOp "/" :: _ -> true
    | TokLParen :: TokIdent _ :: TokOp "*" :: _ -> true
    | TokLParen :: TokIdent _ :: TokOp "/" :: _ -> true
    | TokLParen :: TokIdent _ :: TokOp "^" :: _ -> true
    | _ -> false

let rec parseTypeExpr (tokens: Token list) : ParseResult<TypeExpr> =
    parseTypeAtom tokens >>= fun first rest ->
    match peek rest with
    | Some (TokOp "->") ->
        advance rest |> parseTypeExpr >>= fun ret remaining ->
        success (TyFunc ([first], ret)) remaining
    | _ -> success first rest

and parseTypeAtom (tokens: Token list) : ParseResult<TypeExpr> =
    match peek tokens with
    | Some (TokKeyword KwArray) ->
        // Array<T like I1, I2>
        advance tokens |> expect (TokOp "<") >>= fun _ afterLt ->
        parseTypeExpr afterLt >>= fun elemType afterElem ->
        match peek afterElem with
        | Some (TokKeyword KwLike) ->
            // After 'like', only expect index types (Idx or SymIdx)
            advance afterElem |> sepBy parseIndexType TokComma >>= fun indexTypes afterIndices ->
            expectGt afterIndices >>= fun _ remaining ->
            success (TyArray (elemType, indexTypes)) remaining
        | _ ->
            expectGt afterElem >>= fun _ remaining ->
            success (TyArray (elemType, [])) remaining
    
    | Some (TokKeyword KwPoly) ->
        // Poly<T^r> - arity-polymorphic pack type
        advance tokens |> expect (TokOp "<") >>= fun _ afterLt ->
        parseTypeExpr afterLt >>= fun innerType afterInner ->
        expectGt afterInner >>= fun _ remaining ->
        success (TyPoly innerType) remaining
    
    | Some (TokKeyword KwIdx) ->
        parseIndexType tokens
    
    | Some (TokKeyword KwSymIdx) ->
        parseIndexType tokens
    
    | Some (TokKeyword KwAntisymIdx) ->
        parseIndexType tokens
    
    | Some (TokKeyword KwHermitianIdx) ->
        parseIndexType tokens
    
    | Some (TokKeyword KwCompoundIdx) ->
        parseIndexType tokens

    | Some (TokKeyword KwSparseIdx) ->
        parseIndexType tokens

    | Some (TokKeyword KwOrbIdx) ->
        parseIndexType tokens

    | Some (TokKeyword KwEnumIdx) ->
        parseIndexType tokens
    
    | Some (TokKeyword KwDepIdx) ->
        // DepIdx in standalone position (alias body, fn return, etc). Inside
        // `Array<T like ...>` this is reached via sepBy parseIndexType
        // directly; this dispatch makes the type writable everywhere.
        parseIndexType tokens

    | Some (TokKeyword KwRaggedIdx) ->
        parseIndexType tokens

    | Some (TokKeyword KwIrrepsIdx) ->
        parseIndexType tokens

    | Some (TokKeyword KwPgIrrepsIdx) ->
        parseIndexType tokens

    | Some (TokKeyword KwHalo) ->
        // halo<Inner, [offsets]> in TYPE position: a range<> slot only.
        // Deliberately not in parseIndexType -- `Array<T like halo<..>>` must
        // stay illegal (a halo is a traversal transformer, not storage).
        advance tokens |> expect (TokOp "<") >>= fun _ afterLt ->
        parseTypeExpr afterLt >>= fun inner afterInner ->
        expect TokComma afterInner >>= fun _ afterComma ->
        parseSimpleExpr afterComma >>= fun offsets afterOffsets ->
        expectGt afterOffsets >>= fun _ remaining ->
        success (TyHalo (inner, offsets)) remaining

    | Some (TokKeyword KwVoid) ->
        success (TyNamed ("Void", [])) (advance tokens)

    | Some TokLParen ->
        advance tokens |> sepBy parseTypeExpr TokComma >>= fun types afterTypes ->
        expect TokRParen afterTypes >>= fun _ remaining ->
        match types with
        | [single] -> success single remaining
        | _ -> success (TyTuple types) remaining

    // `Tuple<-2>`: the lexer glues `<-` into ONE token, so a negative width
    // never reaches the arm below. Caught here so it gets the width
    // diagnostic rather than falling through to `TyNamed "Tuple"` and
    // failing at the `=` with an unrelated message.
    | Some (TokIdent "Tuple") when (match peek (advance tokens) with Some (TokOp "<-") -> true | _ -> false) ->
        let (line, col) = currentPos (advance tokens)
        errorC "BL1004" "A tuple width cannot be negative: `Tuple<N>` requires an integer literal N >= 2" line col

    | Some (TokIdent "Tuple") when (match peek (advance tokens) with Some (TokOp "<") -> true | _ -> false) ->
        // `Tuple<...>` has TWO spellings, disambiguated by the ARGUMENT LIST:
        //
        //   * a SINGLE INTEGER LITERAL -- `Tuple<2>` -- is the WIDTH-ONLY
        //     annotation (Design C, docs/plan-tuples-vs-arg-packs.md 6b): the
        //     width is written, the element types are inferred.
        //   * ANY other argument list is a COMPONENT-TYPE LIST of width
        //     k >= 2 -- `Tuple<U^1, T<time>^1>` -- and produces exactly the
        //     node a written `(T1, ..., Tk)` produces. `Tuple<A, B>` and
        //     `(A, B)` are therefore THE SAME TYPE, not two types that agree:
        //     unify's equal-length rule, printing, projection, the width
        //     schema (`declaredTupleWidth` already counts a written `TyTuple`)
        //     and codegen are all untouched by this spelling existing.
        //
        // Why both: `TyTupleWidth` lowers its element slots to FRESH
        // inference variables and nothing at a direct call instantiates them
        // (plan 9, `tuples/002`'s note), so a width-only tuple of ARRAYS
        // defaults to `double` and dies in g++. With the components WRITTEN
        // the slots are concrete, which is the whole point of this spelling.
        //
        // The two cannot be MIXED (`Tuple<3, Float64>`): a width is not a type
        // and a type is not a width, so a list containing both is refused
        // outright rather than silently given one of the two readings.
        //
        // Reserved-name story: this follows `Dist`'s SOFT-keyword precedent
        // (the arm just above) rather than `Array`'s hard lexer keyword. The
        // builtin wins in TYPE position whenever the two-token lookahead
        // `Tuple <` matches, so a user `type Tuple<...>` can never shadow it
        // there; a bare `Tuple` with no `<`, and every VALUE named `Tuple`,
        // still falls through to the ordinary TyNamed / identifier paths.
        // Making it a lexer keyword would have broken those.
        let (ltLine, ltCol) = currentPos (advance tokens)
        advance tokens |> expect (TokOp "<") >>= fun _ afterLt ->
        // The width reading needs the integer to be the WHOLE list, so the
        // closing `>` (or the `>>` expectGt splits) has to follow immediately.
        let isWidthForm =
            match peek afterLt, peek (advance afterLt) with
            | Some (TokInt _), Some (TokOp ">") | Some (TokInt _), Some (TokOp ">>") -> true
            | _ -> false
        (match peek afterLt with
         | Some (TokInt n) when isWidthForm ->
             // Width must be a positive integer LITERAL >= 2. A 1-tuple does
             // not exist in Blade (`(e)` is grouping, formalism.md:1275) and
             // the 0-tuple has no annotation spelling, so both are refused
             // here rather than lowered into a degenerate IRTTuple. A
             // symbolic width would make pack widths inference-dependent,
             // which 6b rules out explicitly.
             if n < 2L then
                 let (line, col) = currentPos afterLt
                 errorC "BL1004" (sprintf "Tuple<%d> is not a tuple width: `Tuple<N>` requires an integer literal N >= 2 (there is no 1-tuple -- `(e)` is grouping -- and no 0-tuple annotation)" n) line col
             else
                 expectGt (advance afterLt) >>= fun _ remaining ->
                 success (TyTupleWidth (int n)) remaining
         | Some (TokOp ">") | Some (TokOp ">>") ->
             errorC "BL1004" "`Tuple<>` is empty: write an integer literal width (`Tuple<2>`, element types inferred) or a list of at least two component types (`Tuple<Float64, Float64>`)" ltLine ltCol
         | _ ->
             // COMPONENT-TYPE LIST. Hand-rolled instead of `sepBy` so that an
             // integer in ANY position -- `Tuple<3, Float64>` and
             // `Tuple<Float64, 3>` alike -- gets the mixture diagnostic rather
             // than `parseTypeExpr`'s generic "unexpected token in type".
             let rec parseComponents (acc: TypeExpr list) (toks: Token list) : ParseResult<TypeExpr list> =
                 match peek toks with
                 | Some (TokInt _) ->
                     let (line, col) = currentPos toks
                     errorC "BL1004" "`Tuple<...>` is either a single integer WIDTH (`Tuple<2>`, element types inferred) or a list of component TYPES (`Tuple<Float64, Float64>`) -- the two spellings cannot be mixed" line col
                 | _ ->
                     parseTypeExpr toks >>= fun ty afterTy ->
                     match peek afterTy with
                     | Some TokComma -> parseComponents (ty :: acc) (advance afterTy)
                     | _ -> success (List.rev (ty :: acc)) afterTy
             parseComponents [] afterLt >>= fun comps afterComps ->
             expectGt afterComps >>= fun _ remaining ->
             match comps with
             | [_] ->
                 // Same rule as `Tuple<1>`, one spelling up: there is no
                 // 1-tuple, and `Tuple<T>` reads like one.
                 errorC "BL1004" "`Tuple<T>` is not a tuple type: a component-typed `Tuple<...>` needs at least TWO component types (there is no 1-tuple -- `(e)` is grouping). For the width-only spelling write an integer literal, e.g. `Tuple<2>`." ltLine ltCol
             | _ ->
                 // The SAME node the written `(T1, ..., Tk)` type produces.
                 success (TyTuple comps) remaining)

    | Some (TokIdent "Dist") when (match peek (advance tokens) with Some (TokOp "<") -> true | _ -> false) ->
        // Dist<order, Elem like I1, ..., Ik>: the typed dist tower
        // (ppl/NOTES.md). Order leads because it's an expression (any
        // statically-evaluable int, per the replicate-count contract), which
        // keeps the parse unambiguous; the rest is Array's `Elem like
        // indices` syntax. A bare `Dist` without `<` falls to TyNamed below.
        advance tokens |> expect (TokOp "<") >>= fun _ afterLt ->
        parseSimpleExpr afterLt >>= fun orderExpr afterOrder ->
        expect TokComma afterOrder >>= fun _ afterComma ->
        parseTypeExpr afterComma >>= fun elemType afterElem ->
        (match peek afterElem with
         | Some (TokKeyword KwLike) ->
             advance afterElem |> sepBy parseIndexType TokComma >>= fun axes afterAxes ->
             expectGt afterAxes >>= fun _ remaining ->
             success (TyDist (orderExpr, elemType, axes)) remaining
         | _ ->
             expectGt afterElem >>= fun _ remaining ->
             success (TyDist (orderExpr, elemType, [])) remaining)

    | Some (TokIdent name0) ->
        // A dotted run is a qualified type path (`store.index.y`); a bare
        // name is unaffected.
        let (name, afterName) = parseDottedTypeName name0 (advance tokens)
        match peek afterName with
        | Some (TokOp "^") ->
            // Caret marks a type variable: T^0 = scalar, T^2 = rank-2 array,
            // T^r = variable-rank.
            advance afterName |> parseRankExpr >>= fun rankExpr remaining ->
            match rankExpr.Kind with
            | ExprKind.ExprLit (LitInt n) ->
                success (TyVar (name, Some (int n))) remaining
            | _ ->
                success (TyAbstractArray (TyVar (name, None), rankExpr, None)) remaining
        | Some (TokOp "<") ->
            let afterLt = advance afterName
            if isTagWildcardArg afterLt then
                // Tag wildcard: `Nat<_>`, `Int64<_>`, `Float64<_>`. Legality
                // per base type and per position is settled at lowering.
                expectGt (advance afterLt) >>= fun _ remaining ->
                success (TyNamed (name, [TyWildcard])) remaining
            else
            // Parameterized type: Array<T>, MyStruct<Int>, Float<velocity>,
            // or the bounded primitives Float<min=0, max=1> /
            // Float<velocity, min=0, max=1>.
            afterLt |> sepBy parseTypeArg TokComma >>= fun args afterArgs ->
            expectGt afterArgs >>= fun _ remaining ->
            let line, col = currentPos afterLt
            buildTypeApp name args line col remaining >>= fun applied afterApplied ->
            // A TRAILING caret after a type application is the abstract-array
            // spelling with a unit-carrying element: `T<day>^1` is a rank-1
            // abstract array of `T` annotated `day`, `T<day>^0` the scalar.
            // The caret is what marks the type-VARIABLE reading of the head;
            // without it `T<x>` keeps its ordinary named-type meaning, and a
            // real base name under a caret (`Float<day>^1`) stays concrete.
            // Lowering (TypeCheck.lowerTypeExpr, TyAbstractArray) decides
            // which of the two the head is.
            match peek afterApplied with
            | Some (TokOp "^") ->
                advance afterApplied |> parseRankExpr >>= fun rankExpr afterRank ->
                success (TyAbstractArray (applied, rankExpr, None)) afterRank
            | _ -> success applied afterApplied
        | _ ->
            success (TyNamed (name, [])) afterName

    | Some kind ->
        let line, col = currentPos tokens
        error (sprintf "Unexpected token in type: %s" (describeToken kind)) line col
    
    | None ->
        errorEof "Expected type but got end of file"

/// One argument of a type application. A named argument (`min=e`) is
/// recognized by two-token lookahead and its value parses with the same
/// static-payload grammar `parseSimpleExpr` that `Idx<n>` uses, so literals
/// (including negative ones), `let static` names, and + - * / arithmetic are
/// all admissible and stop cleanly before `,`/`>`. Anything else is an
/// ordinary positional type.
and parseTypeArg (tokens: Token list) : ParseResult<TypeArg> =
    if isNamedTypeArg tokens then
        let argName = match (List.head tokens).Kind with TokIdent n -> n | _ -> ""
        parseSimpleExpr (advance (advance tokens)) >>= fun value remaining ->
        success (TANamed (argName, value)) remaining
    elif isUnitExprArg tokens then
        // COMPOUND unit annotation (`meter/second`, `second^-1`, `1`,
        // `(meter*second)^2`): the full Unit-declaration grammar, resolved
        // through env.Units at lowering. The sub-parse stops at anything
        // that is not `* / ^`, so it can never eat the `,` or closing `>`
        // of the enclosing constructor. Lone names and `name^INT` are NOT
        // claimed (see isUnitExprArg), so existing spellings are untouched.
        parseUnitExpr tokens >>= fun ue remaining ->
        success (TAPositional (TyUnitExpr ue)) remaining
    else
        parseTypeExpr tokens >>= fun ty remaining ->
        success (TAPositional ty) remaining

/// Assemble a parsed type application. With no named arguments this is just
/// `TyNamed (name, args)`. Named `min=`/`max=` arguments (formalism 2.4) wrap
/// that node in TyBounded; they must follow every positional argument, may
/// not repeat, and no other argument name exists.
and buildTypeApp (name: string) (args: TypeArg list) (line: int) (col: int) (remaining: Token list) : ParseResult<TypeExpr> =
    let positionals = args |> List.choose (function TAPositional t -> Some t | _ -> None)
    let named = args |> List.choose (function TANamed (n, v) -> Some (n, v) | _ -> None)
    if named.IsEmpty then
        success (TyNamed (name, positionals)) remaining
    else
        // Positional-after-named is rejected: the unit/tag argument is what
        // the base type is ABOUT, so it leads.
        let orderOk =
            args
            |> List.fold (fun (sawNamed, ok) a ->
                match a with
                | TANamed _ -> (true, ok)
                | TAPositional _ -> (sawNamed, ok && not sawNamed)) (false, true)
            |> snd
        let badName = named |> List.tryPick (fun (n, _) -> if n = "min" || n = "max" then None else Some n)
        let dup =
            named |> List.map fst |> List.countBy id
            |> List.tryPick (fun (n, c) -> if c > 1 then Some n else None)
        if not orderOk then
            error (sprintf "In `%s<...>`: a positional type argument may not follow a named bound. Write the unit or tag first: `%s<Unit, min=..., max=...>`" name name) line col
        elif badName.IsSome then
            error (sprintf "Unknown named type argument '%s=' in `%s<...>`: only `min=` and `max=` exist" badName.Value name) line col
        elif dup.IsSome then
            error (sprintf "In `%s<...>`: `%s=` given more than once" name dup.Value) line col
        else
            let pick k = named |> List.tryPick (fun (n, v) -> if n = k then Some v else None)
            success (TyBounded (TyNamed (name, positionals), pick "min", pick "max")) remaining

/// The second argument of `SymIdx<k, _>` / `AntisymIdx<k, _>` (seam S1 of the
/// transforms-as-types plan 2.7). The slot is normally `parseSimpleExpr` (an
/// int expression, so the base space is an anonymous dense extent), but also
/// admits an index TYPE for the two forms meaningful as a symmetric-power base:
///   - `IrrepsIdx<spec>`: `SymIdx<k, IrrepsIdx<s>>` is Sym^k of an irreps
///     space, keeping the spec as type identity so two specs of equal
///     total_dim stay distinct (6.3).
///   - `Idx<n>`: the trivial base, lowering to exactly what legacy
///     `SymIdx<k, n>` produces.
/// Anything else -- in particular a bare NAME -- stays on the legacy int
/// reading permanently: `SymIdx<2, N>` always means "extent N", never "the
/// index-type alias N", so existing programs cannot change meaning. A named
/// alias base (`type S = IrrepsIdx<spec>` then `SymIdx<2, S>`) is therefore
/// NOT supported -- write `IrrepsIdx<spec>` inline, or alias the whole thing
/// (`type S = SymIdx<2, IrrepsIdx<spec>>`).
and parseSymIdxBase (tokens: Token list) : ParseResult<SymIdxBase> =
    match peek tokens with
    | Some (TokKeyword KwIrrepsIdx) | Some (TokKeyword KwIdx) ->
        parseIndexType tokens >>= fun ty afterTy -> success (SymBaseIndex ty) afterTy
    | _ ->
        parseSimpleExpr tokens >>= fun extent afterExtent -> success (SymBaseExtent extent) afterExtent

/// One level of an `OrbIdx` class: `( INT , + | - )`. A dedicated sub-parser,
/// deliberately not routed through `parseSimpleExpr`: there are no tuple or
/// sign literals in the expression grammar (`Literal` is
/// int/float/bool/string/char/unit, Ast.fs), so `(2,+)` has no expression
/// parse, and inventing one would widen the whole grammar for one type
/// argument. `+`/`-` already lex as `TokOp`, so no lexer change is needed.
///
/// The rank must be a bare non-negative INT LITERAL -- no `let static` name,
/// no arithmetic -- because the level list is part of the type's IDENTITY
/// (two classes with the same total rank but different level lists are
/// different types with different cardinality), and identity depending on
/// static evaluation would have to be compared after folding. Rank 0 is
/// refused: `S_0` is not a group this class can act with, matching OrbRank's
/// `validateLevels`.
and parseOrbLevel (tokens: Token list) : ParseResult<int * bool> =
    expect TokLParen tokens >>= fun _ afterLP ->
    match peek afterLP with
    | Some (TokInt r) ->
        let afterRank = advance afterLP
        if r < 1L then
            let line, col = currentPos afterLP
            error (sprintf "OrbIdx level (%d, ...): a level rank must be >= 1 (S_0 is not a symmetric group; a rank-1 level is the trivial group and is normalized away)" r) line col
        elif r > 4096L then
            // A per-level sanity cap, not the real bound: the real bound is the
            // class's raw axis count (product of level ranks), checked at
            // lowering where the whole list is in hand, and int64 cell-count
            // overflow bites long before either (7.2). This only catches a
            // typo like `(4000000000,+)` before it reaches an int multiply.
            let line, col = currentPos afterLP
            error (sprintf "OrbIdx level (%d, ...): a level rank above 4096 is almost certainly a typo. The binding constraint on an OrbIdx class is int64 overflow in its cell count, which is reached at far smaller ranks (docs/plan-orbit-index-types.md section 7.2)." r) line col
        else
        expect TokComma afterRank >>= fun _ afterComma ->
        match peek afterComma with
        | Some (TokOp "+") | Some (TokOp "-") ->
            let isPlus = (peek afterComma = Some (TokOp "+"))
            let afterSign = advance afterComma
            expect TokRParen afterSign >>= fun _ remaining ->
            success (int r, isPlus) remaining
        | _ ->
            let line, col = currentPos afterComma
            error "OrbIdx level: expected a character sign '+' or '-' after the rank, as in (2,+) or (2,-)" line col
    | _ ->
        let line, col = currentPos afterLP
        error "OrbIdx level: expected an integer rank literal, as in (2,+)" line col

/// The bracketed level list `[(r1,s1), ..., (rd,sd)]`. Empty (`[]`) is legal
/// and denotes the trivial class (docs/plan-orbit-index-types.md 3: `Idx<n>`
/// is `OrbIdx<[],n>` in normal form).
and parseOrbLevels (tokens: Token list) : ParseResult<(int * bool) list> =
    expect TokLBracket tokens >>= fun _ afterLB ->
    let rec loop (acc: (int * bool) list) (toks: Token list) : ParseResult<(int * bool) list> =
        match peek toks with
        | Some TokRBracket -> success (List.rev acc) (advance toks)
        | _ ->
            parseOrbLevel toks >>= fun lvl afterLvl ->
            match peek afterLvl with
            | Some TokComma -> loop (lvl :: acc) (advance afterLvl)
            | Some TokRBracket -> success (List.rev (lvl :: acc)) (advance afterLvl)
            | _ ->
                let line, col = currentPos afterLvl
                error "OrbIdx level list: expected ',' or ']' after a (rank, sign) level" line col
    loop [] afterLB

// Index types (Idx<extent>, SymIdx<arity, extent>, ...) are self-contained with their own < > brackets.
and parseIndexType (tokens: Token list) : ParseResult<TypeExpr> =
    match peek tokens with
    | Some (TokKeyword KwIdx) ->
        advance tokens |> expect (TokOp "<") >>= fun _ afterLt ->
        parseSimpleExpr afterLt >>= fun extent afterExtent ->
        expectGt afterExtent >>= fun _ remaining ->
        success (TyIdx extent) remaining
    
    | Some (TokKeyword KwSymIdx) ->
        advance tokens |> expect (TokOp "<") >>= fun _ afterLt ->
        parseLiteral afterLt >>= fun rankLit afterRank ->
        expect TokComma afterRank >>= fun _ afterComma ->
        parseSymIdxBase afterComma >>= fun baseIdx afterBase ->
        expectGt afterBase >>= fun _ remaining ->
        let rank = match rankLit with LitInt n -> int n | _ -> 2
        success (TySymIdx (rank, baseIdx)) remaining

    | Some (TokKeyword KwAntisymIdx) ->
        advance tokens |> expect (TokOp "<") >>= fun _ afterLt ->
        parseLiteral afterLt >>= fun rankLit afterRank ->
        expect TokComma afterRank >>= fun _ afterComma ->
        parseSymIdxBase afterComma >>= fun baseIdx afterBase ->
        expectGt afterBase >>= fun _ remaining ->
        let rank = match rankLit with LitInt n -> int n | _ -> 2
        success (TyAntisymIdx (rank, baseIdx)) remaining

    // OrbIdx<[(r1,s1), ..., (rd,sd)], n>: the flat iterated-wreath class
    // (docs/plan-orbit-index-types.md 2). Levels are outermost-last. The
    // extent slot reuses `parseSymIdxBase` verbatim, so it accepts exactly what
    // `SymIdx<k, _>`'s second argument accepts and the two forms cannot drift.
    | Some (TokKeyword KwOrbIdx) ->
        advance tokens |> expect (TokOp "<") >>= fun _ afterLt ->
        parseOrbLevels afterLt >>= fun levels afterLevels ->
        expect TokComma afterLevels >>= fun _ afterComma ->
        parseSymIdxBase afterComma >>= fun baseIdx afterBase ->
        expectGt afterBase >>= fun _ remaining ->
        success (TyOrbIdx (levels, baseIdx)) remaining

    | Some (TokKeyword KwHermitianIdx) ->
        advance tokens |> expect (TokOp "<") >>= fun _ afterLt ->
        parseSimpleExpr afterLt >>= fun extent afterExtent ->
        expectGt afterExtent >>= fun _ remaining ->
        success (TyHermitianIdx extent) remaining

    // CompoundIdx<mask>: masked product space (formalism 4.5). Mask is a
    // runtime array expression whose rank determines the compound's arity,
    // recovered from the mask's type at lowering.
    | Some (TokKeyword KwCompoundIdx) ->
        advance tokens |> expect (TokOp "<") >>= fun _ afterLt ->
        parseSimpleExpr afterLt >>= fun mask afterMask ->
        expectGt afterMask >>= fun _ remaining ->
        success (TyCompoundIdx mask) remaining

    // SparseIdx<keys>: explicit valid-tuple enumeration (formalism 3.5). Keys
    // is a rank-1 array of Nat tuples (`let static` or runtime); arity is
    // implicit from the tuple arity, recovered at lowering like CompoundIdx.
    | Some (TokKeyword KwSparseIdx) ->
        advance tokens |> expect (TokOp "<") >>= fun _ afterLt ->
        parseSimpleExpr afterLt >>= fun keys afterKeys ->
        expectGt afterKeys >>= fun _ remaining ->
        success (TySparseIdx keys) remaining

    | Some (TokKeyword KwEnumIdx) ->
        advance tokens |> expect (TokOp "<") >>= fun _ afterLt ->
        parseSimpleExpr afterLt >>= fun values afterValues ->
        expectGt afterValues >>= fun _ remaining ->
        success (TyEnumIdx values) remaining

    // DepIdx<outer, lambda(i) -> body> | DepIdx<outer, func>
    // Both forms produce TyDepIdx(outer, param, body); the eta-reduced form
    // is desugared to a lambda whose body is `func(<param>)`.
    | Some (TokKeyword KwDepIdx) ->
        advance tokens |> expect (TokOp "<") >>= fun _ afterLt ->
        parseIndexType afterLt >>= fun outer afterOuter ->
        expect TokComma afterOuter >>= fun _ afterComma ->
        // Body is either `lambda(i) -> Idx<...>` or a bare function name.
        match peek afterComma with
        | Some (TokKeyword KwLambda) ->
            // `lambda(name) -> idxBody`; body is an index type, not a general type.
            let afterLambda = advance afterComma
            expect TokLParen afterLambda >>= fun _ afterLParen ->
            match peek afterLParen with
            | Some (TokIdent paramName) ->
                let afterName = advance afterLParen
                expect TokRParen afterName >>= fun _ afterRParen ->
                expect (TokOp "->") afterRParen >>= fun _ afterArrow ->
                parseIndexType afterArrow >>= fun bodyTy afterBody ->
                expectGt afterBody >>= fun _ remaining ->
                success (TyDepIdx (outer, paramName, bodyTy)) remaining
            | _ ->
                let line, col = currentPos afterLParen
                error "DepIdx lambda: expected single parameter name" line col
        | Some (TokIdent funcName) ->
            // Eta-reduced form `DepIdx<outer, func>` desugars to
            // `DepIdx<outer, lambda(__d_i) -> func(__d_i)>` with a fresh
            // parameter name to avoid collisions. The synthesized body is a
            // TypeExpr (TyNamed(func, [paramRef])) even though `func(i)` is
            // conceptually an index type; lowering resolves it through the
            // type-def lookup path.
            let afterName = advance afterComma
            expectGt afterName >>= fun _ remaining ->
            let paramName = "__d_i"
            let bodyTy = TyNamed (funcName, [TyNamed (paramName, [])])
            success (TyDepIdx (outer, paramName, bodyTy)) remaining
        | _ ->
            let line, col = currentPos afterComma
            error "DepIdx: expected lambda or function name as second argument" line col

    // RaggedIdx<lengths>: externally parameterized via a lengths array.
    // RaggedIdx<_>: opaque-extent variant, used in kernel-parameter types
    // (`lambda(g: Array<T like RaggedIdx<_>>) -> ...`) where the extent is
    // supplied by the peel context, not declared up front. `_` means "opaque
    // extent" only when it's the sole argument (immediately followed by `>`
    // or `>>`, which expectGt splits) -- any other position (`_ + 1`, `_, x`,
    // etc.) is left for the lengths-expr parser, so the wildcard can't
    // accidentally swallow a piece of a real expression.
    | Some (TokKeyword KwRaggedIdx) ->
        advance tokens |> expect (TokOp "<") >>= fun _ afterLt ->
        let isOpaqueWildcard =
            match afterLt with
            | t1 :: t2 :: _ when
                t1.Kind = TokUnderscore &&
                (t2.Kind = TokOp ">" || t2.Kind = TokOp ">>") -> true
            | _ -> false
        if isOpaqueWildcard then
            let afterUnderscore = advance afterLt
            expectGt afterUnderscore >>= fun _ remaining ->
            success TyRaggedIdxOpaque remaining
        else
            parseSimpleExpr afterLt >>= fun lengthsExpr afterLengths ->
            expectGt afterLengths >>= fun _ remaining ->
            success (TyRaggedIdx lengthsExpr) remaining

    // IrrepsIdx<spec>: spec is a static-expression argument, a `let static`
    // name or an inline array-of-triples literal ([(l, parity, mult), ...]),
    // resolved at typecheck via StaticEval. Call syntax (IrrepsIdx<sh_spec(2)>)
    // is not part of the simple-expression grammar -- bind a `let static` first.
    | Some (TokKeyword KwIrrepsIdx) ->
        advance tokens |> expect (TokOp "<") >>= fun _ afterLt ->
        parseSimpleExpr afterLt >>= fun specExpr afterSpec ->
        expectGt afterSpec >>= fun _ remaining ->
        success (TyIrrepsIdx specExpr) remaining

    // PgIrrepsIdx<GROUP, spec>: the point-group block-spec member (transforms-
    // as-types plan 3.6). GROUP is a bare identifier, not an expression --
    // point-group names are frozen registry data ({C4, D4}), so the slot is a
    // name the way `Idx<n>`'s slot is a number. The registry lookup (with the
    // unknown-group diagnostic) and spec decode happen at lowering. `spec` is
    // the same static-expression argument IrrepsIdx takes.
    | Some (TokKeyword KwPgIrrepsIdx) ->
        advance tokens |> expect (TokOp "<") >>= fun _ afterLt ->
        (match peek afterLt with
         | Some (TokIdent groupName) ->
             let afterGroup = advance afterLt
             expect TokComma afterGroup >>= fun _ afterComma ->
             parseSimpleExpr afterComma >>= fun specExpr afterSpec ->
             expectGt afterSpec >>= fun _ remaining ->
             success (TyPgIrrepsIdx (groupName, specExpr)) remaining
         | _ ->
             let line, col = currentPos afterLt
             error "PgIrrepsIdx: expected a point-group NAME as the first argument -- PgIrrepsIdx<C4, SPEC>" line col)

    | Some (TokIdent name0) ->
        // Named index type alias (e.g. type RegionIdx = Idx<3>; ...like RegionIdx),
        // or a qualified provider axis (`like store.index.y`). Resolved at
        // typecheck via lowerIndexType / TyNamed lookup against TDIIndexType
        // or TDIEnumIdx.
        let (name, afterName) = parseDottedTypeName name0 (advance tokens)
        match peek afterName with
        | Some (TokOp "<") ->
            let afterLt = advance afterName
            if isTagWildcardArg afterLt then
                // A wildcard in a storage-index slot is illegal, but parsing it
                // lets lowerIndexType report the position error with a real span
                // instead of a bare "unexpected token" from the token stream.
                expectGt (advance afterLt) >>= fun _ remaining ->
                success (TyNamed (name, [TyWildcard])) remaining
            else
            afterLt |> sepBy parseTypeExpr TokComma >>= fun args afterArgs ->
            expectGt afterArgs >>= fun _ remaining ->
            success (TyNamed (name, args)) remaining
        | _ ->
            success (TyNamed (name, [])) afterName
    
    | Some kind ->
        let line, col = currentPos tokens
        error (sprintf "Expected index type (Idx, SymIdx, AntisymIdx, HermitianIdx, EnumIdx, DepIdx, RaggedIdx, IrrepsIdx, PgIrrepsIdx, or a named index type alias) but got %s" (describeToken kind)) line col
    
    | None ->
        errorEof "Expected index type but got end of file"

// Pattern Parsing

let rec parsePattern (tokens: Token list) : ParseResult<Pattern> =
    parseAtomicPattern tokens >>= fun left rest ->
    match peek rest with
    | Some TokColonColon ->
        advance rest |> parsePattern >>= fun right remaining ->
        success (mkPat (mergeSpan left.Span right.Span) (PatCons (left, right))) remaining
    | _ -> success left rest

and parseAtomicPattern (tokens: Token list) : ParseResult<Pattern> =
    match peek tokens with
    | Some TokUnderscore ->
        success (mkPat (headSpan tokens) PatWildcard) (advance tokens)

    | Some (TokIdent name) ->
        let afterName = advance tokens
        match peek afterName with
        | Some TokLBrace ->
            // Struct pattern: Name { field1, field2: pat }
            parseStructPattern name afterName
        | Some TokLParen ->
            // Variant pattern with data: Some(x)
            advance afterName |> parsePattern >>= fun inner afterInner ->
            expect TokRParen afterInner >>= fun _ remaining ->
            success (mkP tokens remaining (PatVariant (name, Some inner))) remaining
        | _ ->
            // Could be a variant without data (like None) or a variable;
            // treated as a variable here, with variant detection at typecheck.
            success (mkPat (headSpan tokens) (PatVar name)) afterName

    | Some (TokInt v) ->
        success (mkPat (headSpan tokens) (PatLit (LitInt v))) (advance tokens)

    | Some (TokBool v) ->
        success (mkPat (headSpan tokens) (PatLit (LitBool v))) (advance tokens)

    | Some (TokString v) ->
        success (mkPat (headSpan tokens) (PatLit (LitString v))) (advance tokens)

    | Some TokLParen ->
        advance tokens |> sepBy parsePattern TokComma >>= fun pats afterPats ->
        expect TokRParen afterPats >>= fun _ remaining ->
        match pats with
        | [] -> success (mkP tokens remaining (PatLit LitUnit)) remaining
        | [single] -> success single remaining
        | _ -> success (mkP tokens remaining (PatTuple pats)) remaining
    
    | Some kind ->
        let line, col = currentPos tokens
        error (sprintf "Unexpected token in pattern: %s" (describeToken kind)) line col
    
    | None ->
        errorEof "Expected pattern but got end of file"

/// Parse struct pattern: Name { field1, field2: pat, ... }
and parseStructPattern (name: string) (tokens: Token list) : ParseResult<Pattern> =
    expect TokLBrace tokens >>= fun _ afterBrace ->
    
    let rec parseFieldPats toks =
        let toks = skipNL toks
        match peek toks with
        | Some TokRBrace -> success [] (advance toks)
        | Some (TokIdent fieldName) ->
            let afterFieldName = advance toks
            match peek afterFieldName with
            | Some TokColon ->
                parsePattern (advance afterFieldName) >>= fun pat afterPat ->
                let afterPat = skipNL afterPat
                match peek afterPat with
                | Some TokComma ->
                    parseFieldPats (advance afterPat) >>= fun rest remaining ->
                    success ((fieldName, pat) :: rest) remaining
                | Some TokRBrace ->
                    success [(fieldName, pat)] (advance afterPat)
                | _ ->
                    let line, col = currentPos afterPat
                    error "Expected ',' or '}' in struct pattern" line col
            | Some TokComma ->
                // shorthand: field (binds to variable of same name)
                parseFieldPats (advance afterFieldName) >>= fun rest remaining ->
                success ((fieldName, mkPat (headSpan toks) (PatVar fieldName)) :: rest) remaining
            | Some TokRBrace ->
                success [(fieldName, mkPat (headSpan toks) (PatVar fieldName))] (advance afterFieldName)
            | _ ->
                let line, col = currentPos afterFieldName
                error "Expected ':' or ',' in struct pattern" line col
        | _ ->
            let line, col = currentPos toks
            error "Expected field name or '}' in struct pattern" line col
    
    parseFieldPats afterBrace >>= fun fields remaining ->
    // Span covers the `{ ... }` body (the type name is consumed by the caller
    // before this function is entered, so it is not part of this range).
    success (mkP tokens remaining (PatStruct (name, fields))) remaining

// Expression Parsing - Precedence Climbing

// Precedence levels (lowest to highest):
// 1. Assignment =
// 2. Pipeline |>
// 3. Choice <|>
// 4. Parallel <&>
// 5. Bind >>=
// 6. Apply <@>
// 7. Array product <*>
// 8. Or ||
// 9. And &&
// 10. Equality == !=
// 11. Comparison < <= > >=
// 12. Cons ::
// 13. Additive + -
// 14. Multiplicative * / %
// 15. Power **
// 16. Unary - !
// 17. Postfix (call, index, field)
// 18. Primary (literals, variables, etc.)

let parseIdentList (tokens: Token list) : ParseResult<string list> =
    let rec loop acc toks =
        match toks with
        | t :: rest when (match t.Kind with TokIdent _ -> true | _ -> false) ->
            let name = match t.Kind with TokIdent n -> n | _ -> ""
            match rest with
            | t2 :: rest2 when t2.Kind = TokComma -> loop (name :: acc) rest2
            | _ -> Ok (List.rev (name :: acc), rest)
        | _ -> 
            if List.isEmpty acc then
                let line, col = currentPos toks
                errorC "BL1001" "Expected identifier" line col
            else
                Ok (List.rev acc, toks)
    loop [] tokens

/// Arguments of an open where-conjunct (`<name>(...)` / `<alias>.<name>(...)`,
/// the Blade.Constraints registry surface): identifiers (the comm/indep/
/// galilean shape, where an argument names a parameter) or int literals,
/// rendered into the same `string list` the registry carries. The literal
/// form is what a group-parameter conjunct needs: `where ml.perm_equiv(4)`
/// names the node-axis extent of the S_n certificate, not a parameter
/// (MLPerm.fs parses the string back, and also accepts a `let static` name).
/// Widening the token set here cannot loosen comm/antisymm groups, which
/// keep the ident-only `parseIdentList`.
let parseConjunctArgList (tokens: Token list) : ParseResult<string list> =
    let rec loop acc toks =
        let taken =
            match toks with
            | t :: rest ->
                match t.Kind with
                | TokIdent n -> Some (n, rest)
                | TokInt n -> Some (string n, rest)
                | _ -> None
            | [] -> None
        match taken with
        | Some (text, rest) ->
            match rest with
            | t2 :: rest2 when t2.Kind = TokComma -> loop (text :: acc) rest2
            | _ -> Ok (List.rev (text :: acc), rest)
        | None ->
            if List.isEmpty acc then
                let line, col = currentPos toks
                errorC "BL1001" "Expected an identifier or an integer literal" line col
            else
                Ok (List.rev acc, toks)
    loop [] tokens

// Where clause parsing (used by both function declarations and lambdas)
/// Parse the body of omp(...): comma-separated `ident: int` pairs, e.g.
/// omp(a: 2, b: 1) => [("a",2); ("b",1)]. The parenthesised form is optional --
/// bare `omp` yields `Omp { Vars = [] }`, read downstream as "auto": every
/// consumer treats an empty depth list as the outermost-level license
/// (IR.buildLoopNestCodeGen's `licenseUnresolved` fallback), and the
/// fold-kernel gate (docs/plan-cpp-perf-exploitation.md) has no per-argument
/// depth to name at all -- a reduce walks one axis, so the only question is
/// whether it may be reordered.
let rec private parseOmpArgs (acc: (string * int) list) (tokens: Token list) : ParseResult<(string * int) list> =
    expectIdent tokens >>= fun name afterName ->
    expect TokColon afterName >>= fun _ afterColon ->
    match afterColon with
    | t :: rest ->
        match t.Kind with
        | TokInt n ->
            let acc' = (name, int n) :: acc
            match rest with
            | t2 :: rest2 when t2.Kind = TokComma -> parseOmpArgs acc' rest2
            | _ -> Ok (List.rev acc', rest)
        | _ -> error (sprintf "Expected integer in omp(...) but got %s" (describeToken t.Kind)) t.Line t.Col
    | [] -> errorEof "Expected integer in omp(...) but got end of file"

let parseWhereClause (tokens: Token list) : ParseResult<WhereClause> =
    // `par` accumulates parallelization strategies as a LIST (see
    // WhereClause.Parallel), outer backend first. Mixed parallelism allows at
    // most two: `mpi, omp(...)` (ranks outer, threads within each rank's
    // slab) and `mpi, cuda(...)` (ranks outer, device kernels per rank). This
    // order table is closed and purely syntactic, so illegal pairs reject
    // here with steering; shape-dependent checks (device eligibility, fold
    // reassociation) stay in codegen.
    let rec loop comms (antis: string list list) (par: ParallelStrategy list) custom toks =
        let isOmp = function Omp _ -> true | _ -> false
        let isCuda = function Cuda _ -> true | _ -> false
        let isMpi = function Mpi -> true | _ -> false
        let rejectPair (line: int) (col: int) (incoming: string) =
            if List.length par >= 2 then
                error "At most two parallelization strategies per where-clause (outer, inner)" line col
            elif par |> List.exists isCuda then
                error "CUDA owns the whole device leaf -- nothing nests inside a device kernel (write the cuda backend LAST: `where mpi, cuda(...)`)" line col
            else
                match incoming with
                | "omp" when par |> List.exists isOmp ->
                    error "omp requested twice -- one omp(...) clause lists all parallel dims" line col
                | "mpi" when par |> List.exists isMpi ->
                    error "mpi requested twice" line col
                | "cuda" -> // par starts with Omp here (cuda/dup handled above)
                    error "A single kernel cannot be both OpenMP-host and CUDA-device -- use `where cuda(...)` for the device kernel (omp-driven sections over independent cuda leaves are future work)" line col
                | "mpi" -> // par starts with Omp: the rejected OpenMP-outer/MPI-inner order
                    error "OpenMP-outer / MPI-inner is not supported: MPI ranks are processes fixed at launch and cannot nest inside host threads -- did you mean `where mpi, omp(...)` (ranks outer, threads within each rank)?" line col
                | _ ->
                    error "Unsupported parallelization strategy combination" line col
        match peek toks with
        | Some (TokKeyword KwComm) ->
            advance toks |> expect TokLParen >>= fun _ afterLParen ->
            parseIdentList afterLParen >>= fun names afterNames ->
            expect TokRParen afterNames >>= fun _ remaining ->
            loop (names :: comms) antis par custom remaining
        | Some (TokKeyword KwAntisymm) ->
            // `anticomm(a, b, ...)`: the signed sibling of `comm`. Needs at
            // least two parameters -- an anticommutativity relation is about
            // an exchange, so a one-name group names no swap at all.
            let line, col = currentPos toks
            advance toks |> expect TokLParen >>= fun _ afterLParen ->
            parseIdentList afterLParen >>= fun names afterNames ->
            expect TokRParen afterNames >>= fun _ remaining ->
            if List.length names < 2 then
                error "anticomm(...) needs at least two parameters: anticommutativity is a relation between two exchanged arguments (f(b, a) = -f(a, b))" line col
            else
                loop comms (names :: antis) par custom remaining
        | Some (TokKeyword KwOmp) ->
            // Legal as: sole strategy, or the INNER of `mpi, omp(...)`.
            if not (par = [] || par = [Mpi]) then
                let line, col = currentPos toks
                rejectPair line col "omp"
            else
                // `omp(a: n, ...)`  OR  bare `omp` (auto). Same optional-paren
                // shape the `cuda` arm below already uses.
                let afterOmp = advance toks
                match peek afterOmp with
                | Some TokLParen ->
                    expect TokLParen afterOmp >>= fun _ afterLParen ->
                    parseOmpArgs [] afterLParen >>= fun pairs afterArgs ->
                    expect TokRParen afterArgs >>= fun _ remaining ->
                    loop comms antis (par @ [Omp { Vars = pairs }]) custom remaining
                | _ ->
                    loop comms antis (par @ [Omp { Vars = [] }]) custom afterOmp
        | Some (TokKeyword KwMpi) ->
            // Legal only as the sole (and hence OUTER) strategy.
            if not (List.isEmpty par) then
                let line, col = currentPos toks
                rejectPair line col "mpi"
            else
                // bare `mpi`: rank count is supplied at launch (mpiexec -n N)
                loop comms antis (par @ [Mpi]) custom (advance toks)
        | Some (TokKeyword KwCuda) ->
            // Legal as: sole strategy, or the INNER of `mpi, cuda(...)`.
            if not (par = [] || par = [Mpi]) then
                let line, col = currentPos toks
                rejectPair line col "cuda"
            else
                // cuda  OR  cuda(block: N)
                let afterCuda = advance toks
                match peek afterCuda with
                | Some TokLParen ->
                    expect TokLParen afterCuda >>= fun _ afterLParen ->
                    expectIdent afterLParen >>= fun key afterKey ->
                    if key <> "block" then
                        let line, col = currentPos afterLParen
                        error (sprintf "Expected 'block' in cuda(...) but got '%s'" key) line col
                    else
                        expect TokColon afterKey >>= fun _ afterColon ->
                        match afterColon with
                        | t :: rest ->
                            match t.Kind with
                            | TokInt n ->
                                expect TokRParen rest >>= fun _ remaining ->
                                loop comms antis (par @ [Cuda { BlockSize = int n }]) custom remaining
                            | _ -> error (sprintf "Expected integer block size but got %s" (describeToken t.Kind)) t.Line t.Col
                        | [] -> errorEof "Expected integer block size but got end of file"
                | _ ->
                    // bare `cuda` => default block size
                    loop comms antis (par @ [Cuda { BlockSize = 256 }]) custom afterCuda
        | Some (TokIdent alias) when (match peek (advance toks) with Some TokDot -> true | _ -> false) ->
            // Module-qualified constraint conjunct: `<alias>.<name>(<idents>)`
            // (e.g. `p.indep(a, b)` with `import ppl as p`). Stored as DATA
            // with the dotted name ("p.indep", args); the owning module's
            // elaboration stage normalizes the alias, and the CHECKER
            // dispatches through the Blade.Constraints registry.
            advance (advance toks) |> expectIdent >>= fun name afterName ->
            expect TokLParen afterName >>= fun _ afterLParen ->
            parseConjunctArgList afterLParen >>= fun args afterArgs ->
            expect TokRParen afterArgs >>= fun _ remaining ->
            loop comms antis par (custom @ [(alias + "." + name, args)]) remaining
        | Some (TokIdent name) when (match peek (advance toks) with Some TokLParen -> true | _ -> false) ->
            // Open constraint conjunct: `<name>(<idents>)` for any identifier
            // the grammar doesn't own. Parsed as data (name, args) and
            // dispatched by the checker through the Blade.Constraints
            // registry; an unregistered name errors there with the
            // registered vocabulary, not here. (Module-owned keywords like
            // PPL's `indep` are qualified -- see the dotted arm above.)
            advance toks |> expect TokLParen >>= fun _ afterLParen ->
            parseConjunctArgList afterLParen >>= fun args afterArgs ->
            expect TokRParen afterArgs >>= fun _ remaining ->
            loop comms antis par (custom @ [(name, args)]) remaining
        | Some TokComma ->
            loop comms antis par custom (advance toks)
        | _ ->
            // A parameter cannot be declared both commutative and
            // antisymmetric (nor antisymmetric twice): the two clauses would
            // fuse into one axis group whose storage must be simultaneously
            // inclusive and strict, so iteration and layout would disagree.
            // Checked syntactically, on names, before either list is resolved
            // to indices. (Overlapping comm groups stay legal.)
            let antiNames = antis |> List.collect id
            let commNames = comms |> List.collect id
            let dupInAnti =
                antiNames |> List.countBy id |> List.tryPick (fun (n, c) -> if c > 1 then Some n else None)
            let bothWays = antiNames |> List.tryFind (fun n -> List.contains n commNames)
            let line, col = currentPos toks
            match dupInAnti, bothWays with
            | Some n, _ ->
                error (sprintf "'%s' appears in two anticomm(...) groups: an argument belongs to at most one anticommutativity relation (the groups would fuse into one axis with two contradictory layouts)" n) line col
            | _, Some n ->
                error (sprintf "'%s' is declared both comm(...) and anticomm(...): one exchange cannot be both commutative (inclusive triangle, diagonal stored) and anticommutative (strict triangle, diagonal zero)" n) line col
            | None, None ->
            success {
                Commutativity = List.rev comms
                Antisymmetry = List.rev antis
                Parallel = par
                TDims = []
                Custom = custom
            } toks
    loop [] [] [] [] tokens

let rec parseExprImpl (tokens: Token list) : ParseResult<Expr> =
    parseAssignment tokens

/// Parse a body expression - either a block {...} or an inline expression
/// Inline expressions stop at newline (consumed) or other terminators
and parseInlineOrBlock (tokens: Token list) : ParseResult<Expr> =
    let tokens = skipNL tokens
    match peek tokens with
    | Some TokLBrace ->
        parseBlock (advance tokens)
    | _ ->
        parseExprImpl tokens >>= fun expr remaining ->
        let remaining = 
            match peek remaining with
            | Some TokNewline -> advance remaining
            | _ -> remaining
        success expr remaining

and parseAssignment (tokens: Token list) : ParseResult<Expr> =
    parseTyped tokens >>= fun left rest ->
    match peek rest with
    | Some (TokOp "=") ->
        advance rest |> parseAssignment >>= fun right remaining ->
        success (mkExpr (mergeSpan left.Span right.Span) (ExprAssign (left, right))) remaining
    // Compound assignment: desugar x += y to x = x + y
    | Some (TokOp "+=") ->
        advance rest |> parseAssignment >>= fun right remaining ->
        let sp = mergeSpan left.Span right.Span
        success (mkExpr sp (ExprAssign (left, mkExpr sp (ExprBinOp (Elementwise, OpAdd, left, right))))) remaining
    | Some (TokOp "-=") ->
        advance rest |> parseAssignment >>= fun right remaining ->
        let sp = mergeSpan left.Span right.Span
        success (mkExpr sp (ExprAssign (left, mkExpr sp (ExprBinOp (Elementwise, OpSub, left, right))))) remaining
    | Some (TokOp "*=") ->
        advance rest |> parseAssignment >>= fun right remaining ->
        let sp = mergeSpan left.Span right.Span
        success (mkExpr sp (ExprAssign (left, mkExpr sp (ExprBinOp (Elementwise, OpMul, left, right))))) remaining
    | Some (TokOp "/=") ->
        advance rest |> parseAssignment >>= fun right remaining ->
        let sp = mergeSpan left.Span right.Span
        success (mkExpr sp (ExprAssign (left, mkExpr sp (ExprBinOp (Elementwise, OpDiv, left, right))))) remaining
    | _ -> success left rest

/// Postfix type annotation `expr : Type`, sitting between parseAssignment and
/// parseNamedInfix in the precedence chain: binds tighter than `=` (so
/// `x = e: T` parses as `x = (e: T)`) but looser than every operator below it
/// (so `a + b : Int` parses as `(a + b) : Int`). Motivating case is width
/// adoption on constructor calls, e.g. `complex(re, im) : Complex64`, but the
/// form is general; TypeCheck applies it via bidirectional checkExpr, falling
/// back to inferExpr + unify.
and parseTyped (tokens: Token list) : ParseResult<Expr> =
    parseNamedInfix tokens >>= fun expr rest ->
    match peek rest with
    | Some TokColon ->
        advance rest |> parseTypeExpr >>= fun ty remaining ->
        // Span runs from the base expression through the annotated type.
        success (mkExpr (mergeSpan expr.Span (rangeSpan rest remaining)) (ExprTyped (expr, ty))) remaining
    | _ -> success expr rest

/// Named infix operators: a :name: b -> name(a, b)
/// Lowest precedence, left-associative
and parseNamedInfix (tokens: Token list) : ParseResult<Expr> =
    parsePipeline tokens >>= fun left rest ->
    let rec loop (acc: Expr) toks =
        match peek toks with
        | Some (TokNamedInfix name) ->
            advance toks |> parsePipeline >>= fun right remaining ->
            // Desugar :name: to function application: name(a, b)
            let fn = mkExpr (headSpan toks) (ExprVar name)
            let call = mkExpr (mergeSpan acc.Span right.Span) (ExprApp (fn, [acc; right]))
            loop call remaining
        | _ -> success acc toks
    loop left rest

and parsePipeline (tokens: Token list) : ParseResult<Expr> =
    parseChoice tokens >>= fun left rest ->
    let rec loop acc toks =
        let (peeked, toks') = peekContinuation toks
        match peeked with
        | Some (TokOp "|>") ->
            advance toks' |> parseChoice >>= fun right remaining ->
            match right.Kind with
            | ExprKind.ExprVar "compute" -> loop (mkExpr (mergeSpan acc.Span right.Span) (ExprCompute acc)) remaining
            // Provider reads are module-qualified (`x |> alias.read`), so they
            // take the generic pipe-application desugar below; the checker's
            // provider-read arm rewrites the application to TExprRead.
            | _ -> loop (mkExpr (mergeSpan acc.Span right.Span) (ExprApp (right, [acc]))) remaining
        | Some (TokOp "|@>") ->
            // Pipe-apply: a |@> b  desugars to  b <@> a
            advance toks' |> parseChoice >>= fun right remaining ->
            loop (mkExpr (mergeSpan acc.Span right.Span) (ExprBinOp (Elementwise, OpApply, right, acc))) remaining
        | _ -> success acc toks
    loop left rest

and parseChoice (tokens: Token list) : ParseResult<Expr> =
    parseParallel tokens >>= fun left rest ->
    let rec loop acc toks =
        let (peeked, toks') = peekContinuation toks
        match peeked with
        | Some (ChoiceOp op) ->
            advance toks' |> parseParallel >>= fun right remaining ->
            loop (mkExpr (mergeSpan acc.Span right.Span) (ExprBinOp (Elementwise, op, acc, right))) remaining
        | _ -> success acc toks
    loop left rest

and parseParallel (tokens: Token list) : ParseResult<Expr> =
    parseBind tokens >>= fun left rest ->
    let rec loop acc toks =
        let (peeked, toks') = peekContinuation toks
        match peeked with
        | Some (ParallelOp op) ->
            advance toks' |> parseBind >>= fun right remaining ->
            loop (mkExpr (mergeSpan acc.Span right.Span) (ExprBinOp (Elementwise, op, acc, right))) remaining
        | _ -> success acc toks
    loop left rest

and parseBind (tokens: Token list) : ParseResult<Expr> =
    parseApply tokens >>= fun left rest ->
    let rec loop acc toks =
        let (peeked, toks') = peekContinuation toks
        match peeked with
        | Some (BindOp op) ->
            advance toks' |> parseApply >>= fun right remaining ->
            loop (mkExpr (mergeSpan acc.Span right.Span) (ExprBinOp (Elementwise, op, acc, right))) remaining
        // >> is now a single token from the lexer
        | Some (TokOp ">>") ->
            advance toks' |> parseApply >>= fun right remaining ->
            loop (mkExpr (mergeSpan acc.Span right.Span) (ExprBinOp (Elementwise, OpCompose, acc, right))) remaining
        | _ -> success acc toks
    loop left rest

and parseApply (tokens: Token list) : ParseResult<Expr> =
    parseArrayProduct tokens >>= fun left rest ->
    let rec loop acc toks =
        let (peeked, toks') = peekContinuation toks
        match peeked with
        | Some (ApplyOp op) ->
            advance toks' |> parseArrayProduct >>= fun right remaining ->
            loop (mkExpr (mergeSpan acc.Span right.Span) (ExprBinOp (Elementwise, op, acc, right))) remaining
        | _ -> success acc toks
    loop left rest

and parseArrayProduct (tokens: Token list) : ParseResult<Expr> =
    parseOr tokens >>= fun left rest ->
    let rec loop acc toks =
        let (peeked, toks') = peekContinuation toks
        match peeked with
        | Some (ArrayProductOp op) ->
            advance toks' |> parseOr >>= fun right remaining ->
            loop (mkExpr (mergeSpan acc.Span right.Span) (ExprBinOp (Elementwise, op, acc, right))) remaining
        | _ -> success acc toks
    loop left rest

and parseOr (tokens: Token list) : ParseResult<Expr> =
    parseAnd tokens >>= fun left rest ->
    let rec loop acc toks =
        match peek toks with
        | Some (OrOp (mode, op)) ->
            advance toks |> parseAnd >>= fun right remaining ->
            loop (mkExpr (mergeSpan acc.Span right.Span) (ExprBinOp (mode, op, acc, right))) remaining
        | _ -> success acc toks
    loop left rest

and parseAnd (tokens: Token list) : ParseResult<Expr> =
    parseEquality tokens >>= fun left rest ->
    let rec loop acc toks =
        match peek toks with
        | Some (AndOp (mode, op)) ->
            advance toks |> parseEquality >>= fun right remaining ->
            loop (mkExpr (mergeSpan acc.Span right.Span) (ExprBinOp (mode, op, acc, right))) remaining
        | _ -> success acc toks
    loop left rest

and parseEquality (tokens: Token list) : ParseResult<Expr> =
    parseComparison tokens >>= fun left rest ->
    match peek rest with
    | Some (EqualityOp (mode, op)) ->
        advance rest |> parseComparison >>= fun right remaining ->
        success (mkExpr (mergeSpan left.Span right.Span) (ExprBinOp (mode, op, left, right))) remaining
    | _ -> success left rest

and parseComparison (tokens: Token list) : ParseResult<Expr> =
    parseCons tokens >>= fun left rest ->
    match peek rest with
    | Some (ComparisonOp (mode, op)) ->
        advance rest |> parseCons >>= fun right remaining ->
        success (mkExpr (mergeSpan left.Span right.Span) (ExprBinOp (mode, op, left, right))) remaining
    | _ -> success left rest

and parseCons (tokens: Token list) : ParseResult<Expr> =
    parseDotDot tokens >>= fun left rest ->
    match peek rest with
    | Some TokColonColon ->
        advance rest |> parseDotDot >>= fun right remaining ->
        success (mkExpr (mergeSpan left.Span right.Span) (ExprBinOp (Elementwise, OpCons, left, right))) remaining
    | _ -> success left rest

and parseDotDot (tokens: Token list) : ParseResult<Expr> =
    parseAdditive tokens >>= fun left rest ->
    match peek rest with
    | Some TokDotDot ->
        advance rest |> parseAdditive >>= fun right remaining ->
        success (mkExpr (mergeSpan left.Span right.Span) (ExprDotDot (left, right))) remaining
    | _ -> success left rest

and parseAdditive (tokens: Token list) : ParseResult<Expr> =
    parseMultiplicative tokens >>= fun left rest ->
    let rec loop acc toks =
        match peek toks with
        | Some (AdditiveOp (mode, op)) ->
            advance toks |> parseMultiplicative >>= fun right remaining ->
            loop (mkExpr (mergeSpan acc.Span right.Span) (ExprBinOp (mode, op, acc, right))) remaining
        | _ -> success acc toks
    loop left rest

and parseMultiplicative (tokens: Token list) : ParseResult<Expr> =
    parsePower tokens >>= fun left rest ->
    let rec loop acc toks =
        match peek toks with
        | Some (MultiplicativeOp (mode, op)) ->
            advance toks |> parsePower >>= fun right remaining ->
            loop (mkExpr (mergeSpan acc.Span right.Span) (ExprBinOp (mode, op, acc, right))) remaining
        | _ -> success acc toks
    loop left rest

and parsePower (tokens: Token list) : ParseResult<Expr> =
    parseUnary tokens >>= fun left rest ->
    match peek rest with
    | Some (PowerOp (mode, op)) ->
        advance rest |> parsePower >>= fun right remaining ->
        success (mkExpr (mergeSpan left.Span right.Span) (ExprBinOp (mode, op, left, right))) remaining
    | _ -> success left rest

and parseUnary (tokens: Token list) : ParseResult<Expr> =
    match peek tokens with
    | Some (UnaryOp op) ->
        advance tokens |> parseUnary >>= fun expr remaining ->
        success (mkExpr (mergeSpan (headSpan tokens) expr.Span) (ExprUnaryOp (op, expr))) remaining
    | _ -> parsePostfix tokens

/// Parse struct construction: Name { field1 = val1, field2 = val2 }
and parseStructExpr (name: string) (tokens: Token list) : ParseResult<Expr> =
    expect TokLBrace tokens >>= fun _ afterBrace ->
    
    let rec parseFieldInits toks =
        let toks = skipNL toks
        match peek toks with
        | Some TokRBrace -> success ([], None) (advance toks)
        | Some TokDotDot ->
            // Functional update: `..base` copies the remaining fields from
            // base. Must be the LAST entry (nothing may follow it).
            parseExprImpl (advance toks) >>= fun baseExpr afterBase ->
            let afterBase = skipNL afterBase
            (match peek afterBase with
             | Some TokRBrace -> success ([], Some baseExpr) (advance afterBase)
             | _ ->
                 let line, col = currentPos afterBase
                 error "'..base' must be the last entry in a struct literal" line col)
        | Some (TokIdent fieldName) ->
            let afterFieldName = advance toks
            match peek afterFieldName with
            | Some (TokOp "=") ->
                parseExprImpl (advance afterFieldName) >>= fun value afterValue ->
                let afterValue = skipNL afterValue
                match peek afterValue with
                | Some TokComma ->
                    parseFieldInits (advance afterValue) >>= fun (rest, spread) remaining ->
                    success ((fieldName, value) :: rest, spread) remaining
                | Some TokRBrace ->
                    success ([(fieldName, value)], None) (advance afterValue)
                | _ ->
                    let line, col = currentPos afterValue
                    error "Expected ',' or '}' in struct expression" line col
            | Some TokComma ->
                // shorthand: field (same as field = field)
                parseFieldInits (advance afterFieldName) >>= fun (rest, spread) remaining ->
                success ((fieldName, mkExpr (headSpan toks) (ExprVar fieldName)) :: rest, spread) remaining
            | Some TokRBrace ->
                success ([(fieldName, mkExpr (headSpan toks) (ExprVar fieldName))], None) (advance afterFieldName)
            | _ ->
                let line, col = currentPos afterFieldName
                error "Expected '=' or ',' in struct field" line col
        | _ ->
            let line, col = currentPos toks
            error "Expected field name, '..base', or '}'" line col

    parseFieldInits afterBrace >>= fun (fields, spread) remaining ->
    // Span covers the `{ ... }` body (the type name is consumed by the caller
    // before this function is entered, so it is not part of this range).
    success (mkE tokens remaining (ExprStruct (name, fields, spread))) remaining

and parsePostfix (tokens: Token list) : ParseResult<Expr> =
    parsePrimary tokens >>= fun left rest ->
    // A postfix `(` or `[` extends the previous expression only when it opens
    // on the line where that expression ends. The lexer strips newline
    // tokens inside delimiters (tokenizeWithNewlines), so inside a block
    // `let b = v` followed by a final tuple `(a, b)` on the next line would
    // otherwise parse as the call `v(a, b)`, reported as a baffling
    // unbound-variable error at the let. Line numbers survive the stripping:
    // compare the opener's line against the token before it (the same-line
    // rule Kotlin and Swift use). `.field` chains stay line-insensitive.
    let opensOnLaterLine (opener: Token) =
        let rec lastLineBefore last toks =
            match toks with
            | (t: Token) :: rest when t.Line < opener.Line
                                      || (t.Line = opener.Line && t.Col < opener.Col) ->
                lastLineBefore t.Line rest
            | _ -> last
        lastLineBefore opener.Line tokens < opener.Line
    let rec loop acc toks =
        match peek toks with
        | Some TokLParen when not (opensOnLaterLine (List.head toks)) ->
            // Function call (delimited: struct literals re-enabled)
            allowingStructLiterals (fun () -> advance toks |> sepBy parseExprImpl TokComma) >>= fun args afterArgs ->
            expect TokRParen afterArgs >>= fun _ remaining ->
            loop (mkExpr (mergeSpan acc.Span (rangeSpan toks remaining)) (ExprApp (acc, args))) remaining
        | Some TokLBracket when not (opensOnLaterLine (List.head toks)) ->
            // Poly-tuple indexing: args[k] (delimited: struct literals re-enabled)
            allowingStructLiterals (fun () -> advance toks |> parseExprImpl) >>= fun index afterIndex ->
            expect TokRBracket afterIndex >>= fun _ remaining ->
            loop (mkExpr (mergeSpan acc.Span (rangeSpan toks remaining)) (ExprTupleIndex (acc, index))) remaining
        | Some TokDot ->
            advance toks |> expectIdent >>= fun field remaining ->
            loop (mkExpr (mergeSpan acc.Span (rangeSpan toks remaining)) (ExprField (acc, field))) remaining
        | _ -> success acc toks
    loop left rest

and parsePrimary (tokens: Token list) : ParseResult<Expr> =
    match peek tokens with
    | Some (LiteralTok lit) -> success (mkExpr (headSpan tokens) (ExprLit lit)) (advance tokens)

    // Wildcard hole `_` (e.g. a free axis in a compound index B((a, _, c))):
    // a general token whose meaning comes from the consuming context;
    // unconsumed uses are rejected in typecheck.
    | Some TokUnderscore -> success (mkExpr (headSpan tokens) ExprWildcard) (advance tokens)

    | Some (TokIdent name) ->
        let afterName = advance tokens
        match peek afterName with
        | Some TokLBrace when not noStructLiteralCtx.Value ->
            // Struct construction: Name { field1 = val1, ... }, suppressed in
            // for-in range headers (see noStructLiteralCtx). Widen the span
            // to include the leading type name.
            parseStructExpr name afterName >>= fun e remaining ->
            success { e with Span = rangeSpan tokens remaining } remaining
        | _ ->
            success (mkExpr (headSpan tokens) (ExprVar name)) afterName

    | Some (TokKeyword KwArity) ->
        let afterArity = advance tokens
        match peek afterArity with
        | Some TokLParen ->
            advance afterArity |> fun afterLParen ->
            match peek afterLParen with
            | Some (TokIdent paramName) ->
                advance afterLParen |> expect TokRParen >>= fun _ remaining ->
                success (mkE tokens remaining (ExprArity paramName)) remaining
            | _ ->
                let line, col = currentPos afterLParen
                error "Expected parameter name in arity()" line col
        | _ ->
            let line, col = currentPos afterArity
            error "arity requires parameter name: arity(paramName)" line col
    | Some (TokKeyword KwNth) -> success (mkExpr (headSpan tokens) ExprNth) (advance tokens)
    | Some (TokKeyword KwZero) -> success (mkExpr (headSpan tokens) ExprZero) (advance tokens)

    | Some (TokKeyword KwRank) ->
        advance tokens |> expect TokLParen >>= fun _ afterLParen ->
        parseExprImpl afterLParen >>= fun expr afterExpr ->
        expect TokRParen afterExpr >>= fun _ remaining ->
        success (mkE tokens remaining (ExprRank expr)) remaining

    | Some (TokKeyword KwCompute) ->
        success (mkExpr (headSpan tokens) (ExprVar "compute")) (advance tokens)

    | Some (TokKeyword KwLambda) ->
        // Widen the span to include the leading `lambda` keyword.
        parseLambda (advance tokens) >>= fun e remaining ->
        success { e with Span = rangeSpan tokens remaining } remaining

    | Some (TokKeyword KwLet) ->
        parseLet (advance tokens) >>= fun e remaining ->
        success { e with Span = rangeSpan tokens remaining } remaining

    | Some (TokKeyword KwIf) ->
        parseIf (advance tokens) >>= fun e remaining ->
        success { e with Span = rangeSpan tokens remaining } remaining

    | Some (TokKeyword KwMatch) ->
        parseMatch (advance tokens) >>= fun e remaining ->
        success { e with Span = rangeSpan tokens remaining } remaining

    | Some (TokKeyword KwMethodFor) ->
        parseMethodFor (advance tokens) >>= fun e remaining ->
        success { e with Span = rangeSpan tokens remaining } remaining

    // for (A, B) in virtualArray: co-iteration construct
    | Some (TokKeyword KwFor) ->
        parseForConstruct (advance tokens) >>= fun e remaining ->
        success { e with Span = rangeSpan tokens remaining } remaining

    // static method_for / static object_for / static for: the wrapped
    // former's argument list elaborates at compile time (Unfold eliminates
    // ExprStatic before typechecking). Only valid immediately before a former.
    | Some (TokKeyword KwStatic) ->
        let afterStatic = advance tokens
        match peek afterStatic with
        | Some (TokKeyword KwMethodFor) ->
            parseMethodFor (advance afterStatic) >>= fun former remaining ->
            success (mkE tokens remaining (ExprStatic former)) remaining
        | Some (TokKeyword KwObjectFor) ->
            parseObjectFor (advance afterStatic) >>= fun former remaining ->
            success (mkE tokens remaining (ExprStatic former)) remaining
        | Some (TokKeyword KwFor) ->
            parseForConstruct (advance afterStatic) >>= fun former remaining ->
            success (mkE tokens remaining (ExprStatic former)) remaining
        | _ ->
            let (line, col) = currentPos afterStatic
            error "Expected 'method_for', 'object_for', or 'for' after 'static' in expression position" line col

    | Some (TokKeyword KwObjectFor) ->
        parseObjectFor (advance tokens) >>= fun e remaining ->
        success { e with Span = rangeSpan tokens remaining } remaining

    | Some (TokKeyword KwZip) ->
        advance tokens |> expect TokLParen >>= fun _ afterLParen ->
        sepBy parseExprImpl TokComma afterLParen >>= fun exprs afterExprs ->
        expect TokRParen afterExprs >>= fun _ remaining ->
        success (mkE tokens remaining (ExprZip exprs)) remaining

    | Some (TokKeyword KwStack) ->
        advance tokens |> expect TokLParen >>= fun _ afterLParen ->
        sepBy parseExprImpl TokComma afterLParen >>= fun exprs afterExprs ->
        expect TokRParen afterExprs >>= fun _ remaining ->
        success (mkE tokens remaining (ExprStack exprs)) remaining

    // join(A, B, ..., d): concatenate along dimension d. d is the LAST
    // argument and must be an integer literal (compile-time, like transpose's axis pair).
    | Some (TokKeyword KwJoin) ->
        advance tokens |> expect TokLParen >>= fun _ afterLParen ->
        sepBy parseExprImpl TokComma afterLParen >>= fun exprs afterExprs ->
        expect TokRParen afterExprs >>= fun _ remaining ->
        let line, col = currentPos afterExprs
        (match List.rev exprs with
         | last :: revArrays when revArrays.Length >= 2 ->
            (match last.Kind with
             | ExprKind.ExprLit (LitInt d) ->
                success (mkE tokens remaining (ExprJoin (List.rev revArrays, int d))) remaining
             | _ ->
                error "join expects an integer dimension as its last argument: join(A, B, d)" line col)
         | _ ->
            error "join expects at least two arrays and a dimension: join(A, B, d)" line col)

    | Some (TokKeyword KwSequence) ->
        advance tokens |> expect TokLParen >>= fun _ afterLParen ->
        sepBy parseExprImpl TokComma afterLParen >>= fun exprs afterExprs ->
        expect TokRParen afterExprs >>= fun _ remaining ->
        success (mkE tokens remaining (ExprSequence exprs)) remaining

    | Some (TokKeyword KwReplicate) ->
        advance tokens |> expect TokLParen >>= fun _ afterLParen ->
        parseExprImpl afterLParen >>= fun count afterCount ->
        expect TokComma afterCount >>= fun _ afterComma ->
        parseExprImpl afterComma >>= fun body afterBody ->
        expect TokRParen afterBody >>= fun _ remaining ->
        success (mkE tokens remaining (ExprReplicate (count, body))) remaining

    | Some (TokKeyword KwGuard) ->
        advance tokens |> expect TokLParen >>= fun _ afterLParen ->
        parseExprImpl afterLParen >>= fun cond afterCond ->
        expect TokComma afterCond >>= fun _ afterComma ->
        parseExprImpl afterComma >>= fun body afterBody ->
        expect TokRParen afterBody >>= fun _ remaining ->
        success (mkE tokens remaining (ExprGuard (cond, body))) remaining

    | Some (TokKeyword KwMask) ->
        advance tokens |> expect TokLParen >>= fun _ afterLParen ->
        parseExprImpl afterLParen >>= fun arr afterArr ->
        expect TokComma afterArr >>= fun _ afterComma ->
        parseExprImpl afterComma >>= fun pred afterPred ->
        expect TokRParen afterPred >>= fun _ remaining ->
        success (mkE tokens remaining (ExprMask (arr, pred))) remaining

    | Some (TokKeyword KwCompound) ->
        advance tokens |> expect TokLParen >>= fun _ afterLParen ->
        parseExprImpl afterLParen >>= fun dense afterDense ->
        expect TokComma afterDense >>= fun _ afterComma ->
        parseExprImpl afterComma >>= fun mask afterMask ->
        expect TokRParen afterMask >>= fun _ remaining ->
        success (mkE tokens remaining (ExprCompound (dense, mask))) remaining

    // sparse(values, keys): bundles rank-1 values (in key order) with an
    // explicit key list into a SparseIdx-typed array (formalism 3.5).
    | Some (TokKeyword KwSparse) ->
        advance tokens |> expect TokLParen >>= fun _ afterLParen ->
        parseExprImpl afterLParen >>= fun values afterValues ->
        expect TokComma afterValues >>= fun _ afterComma ->
        parseExprImpl afterComma >>= fun keys afterKeys ->
        expect TokRParen afterKeys >>= fun _ remaining ->
        success (mkE tokens remaining (ExprSparse (values, keys))) remaining

    | Some (TokKeyword KwIntersect) ->
        advance tokens |> expect TokLParen >>= fun _ afterLParen ->
        parseExprImpl afterLParen >>= fun a afterA ->
        expect TokComma afterA >>= fun _ afterComma ->
        parseExprImpl afterComma >>= fun b afterB ->
        expect TokRParen afterB >>= fun _ remaining ->
        success (mkE tokens remaining (ExprIntersect (a, b))) remaining

    | Some (TokKeyword KwUnion) ->
        advance tokens |> expect TokLParen >>= fun _ afterLParen ->
        parseExprImpl afterLParen >>= fun a afterA ->
        expect TokComma afterA >>= fun _ afterComma ->
        parseExprImpl afterComma >>= fun b afterB ->
        expect TokRParen afterB >>= fun _ remaining ->
        success (mkE tokens remaining (ExprUnion (a, b))) remaining

    | Some (TokKeyword KwUnique) ->
        advance tokens |> expect TokLParen >>= fun _ afterLParen ->
        parseExprImpl afterLParen >>= fun arr afterArr ->
        expect TokRParen afterArr >>= fun _ remaining ->
        success (mkE tokens remaining (ExprUnique arr)) remaining

    | Some (TokKeyword KwContains) ->
        advance tokens |> expect TokLParen >>= fun _ afterLParen ->
        parseExprImpl afterLParen >>= fun arr afterArr ->
        expect TokComma afterArr >>= fun _ afterComma ->
        parseExprImpl afterComma >>= fun value afterValue ->
        expect TokRParen afterValue >>= fun _ remaining ->
        success (mkE tokens remaining (ExprContains (arr, value))) remaining

    | Some (TokKeyword KwGroupBy) ->
        advance tokens |> expect TokLParen >>= fun _ afterLParen ->
        parseExprImpl afterLParen >>= fun vals afterVals ->
        expect TokComma afterVals >>= fun _ afterComma ->
        parseExprImpl afterComma >>= fun keys afterKeys ->
        expect TokRParen afterKeys >>= fun _ remaining ->
        success (mkE tokens remaining (ExprGroupBy (vals, keys))) remaining
    
    // group_keys(keys1, keys2, ...): one key for ordinary grouping; multiple
    // keys triggers compound (tuple-keyed) grouping.
    | Some (TokKeyword KwGroupKeys) ->
        advance tokens |> expect TokLParen >>= fun _ afterLParen ->
        sepBy parseExprImpl TokComma afterLParen >>= fun keys afterKeys ->
        expect TokRParen afterKeys >>= fun _ remaining ->
        success (mkE tokens remaining (ExprGroupKeys keys)) remaining

    | Some (TokKeyword KwSort) ->
        advance tokens |> expect TokLParen >>= fun _ afterLParen ->
        parseExprImpl afterLParen >>= fun array afterArr ->
        expect TokComma afterArr >>= fun _ afterComma ->
        parseExprImpl afterComma >>= fun key afterKey ->
        expect TokRParen afterKey >>= fun _ remaining ->
        success (mkE tokens remaining (ExprSort (array, key))) remaining

    // transpose(A, [d1, d2]): swaps exactly two axes; the axis list must be
    // exactly two integer literals (no general permutation). Semantic checks
    // (d1 != d2, in range, both axes arity-1 SymNone) happen in TypeCheck.
    | Some (TokKeyword KwTranspose) ->
        advance tokens |> expect TokLParen >>= fun _ afterLParen ->
        parseExprImpl afterLParen >>= fun array afterArr ->
        expect TokComma afterArr >>= fun _ afterComma ->
        expect TokLBracket afterComma >>= fun _ afterLBrack ->
        (match peek afterLBrack with
         | Some (TokInt d1) ->
            let afterD1 = advance afterLBrack
            expect TokComma afterD1 >>= fun _ afterComma2 ->
            (match peek afterComma2 with
             | Some (TokInt d2) ->
                let afterD2 = advance afterComma2
                expect TokRBracket afterD2 >>= fun _ afterRBrack ->
                expect TokRParen afterRBrack >>= fun _ remaining ->
                success (mkE tokens remaining (ExprTranspose (array, int d1, int d2))) remaining
             | _ ->
                let line, col = currentPos afterComma2
                error "transpose expects exactly two integer axis indices: transpose(A, [d1, d2])" line col)
         | _ ->
            let line, col = currentPos afterLBrack
            error "transpose expects exactly two integer axis indices: transpose(A, [d1, d2])" line col)
    
    // hermitian(A): the conjugate-transpose A^H of a rank-2 array; desugars to
    // conj(transpose(A, [0, 1])). Result is a plain dense array (the operation
    // A^H), not a SymHermitian-typed matrix -- that comes from `gram`.
    | Some (TokKeyword KwHermitian) ->
        advance tokens |> expect TokLParen >>= fun _ afterLParen ->
        parseExprImpl afterLParen >>= fun array afterArr ->
        expect TokRParen afterArr >>= fun _ remaining ->
        let sp = rangeSpan tokens remaining
        success (mkExpr sp (ExprUnaryOp (OpConj, mkExpr sp (ExprTranspose (array, 0, 1))))) remaining

    // gram(A, B) = A * B^H: result[i][j] = sum_k A[i][k] * conj(B[j][k]).
    // A is m x n, B is p x n (shared contracted dim n), result is m x p. When A
    // and B are the same variable the result is symmetric/Hermitian, computed
    // via the triangular upper-half scatter; otherwise a general dense array.
    | Some (TokKeyword KwGram) ->
        advance tokens |> expect TokLParen >>= fun _ afterLParen ->
        parseExprImpl afterLParen >>= fun left afterLeft ->
        expect TokComma afterLeft >>= fun _ afterComma ->
        parseExprImpl afterComma >>= fun right afterRight ->
        expect TokRParen afterRight >>= fun _ remaining ->
        success (mkE tokens remaining (ExprGram (left, right))) remaining

    | Some (TokKeyword KwDecompact) ->
        advance tokens |> expect TokLParen >>= fun _ afterLParen ->
        parseExprImpl afterLParen >>= fun array afterArr ->
        expect TokComma afterArr >>= fun _ afterComma ->
        (match peek afterComma with
         | Some (TokInt d) ->
            let afterD = advance afterComma
            expect TokRParen afterD >>= fun _ remaining ->
            success (mkE tokens remaining (ExprDecompact (array, int d))) remaining
         | _ ->
            let line, col = currentPos afterComma
            error "decompact expects a single integer dimension index: decompact(A, d)" line col)
    
    // reduce(array, op[, init][, axes = n]): folds the innermost `n` axes by a
    // binary kernel, n = 1 by default (default kernel (+) if omitted; accepts
    // operator sections like (+)). The optional init is each folded group's
    // initial accumulator (init (+) a0 (+) a1 ...); an empty group reduces to
    // init, and without init empty inputs are rejected.
    //
    // `axes` is a NAMED final argument, not a fourth positional one: the third
    // POSITIONAL slot is already the seed, so a bare `reduce(A, op, 2)` would be
    // ambiguous between "seed 2" and "fold 2 axes". Recognized by the same
    // two-token lookahead `isNamedTypeArg` uses for `min=`/`max=` in type
    // arguments -- an identifier immediately followed by `=`, a shape that is a
    // hard parse error in an expression argument today, so this is a strict
    // widening. Special-cased to `reduce`; ordinary calls have no named slots.
    | Some (TokKeyword KwReduce) ->
        let isAxesArg (toks: Token list) =
            match toks with
            | t1 :: t2 :: _ ->
                (match t1.Kind with TokIdent "axes" -> true | _ -> false) && t2.Kind = TokOp "="
            | _ -> false
        // `axes = n` then the closing paren. Only ever called with `isAxesArg`
        // true, so the two leading tokens are the name and the `=`.
        let parseAxesTail (array: Expr) (op: Expr) (initE: Expr option) (toks: Token list) =
            parseExprImpl (advance (advance toks)) >>= fun axesE afterAxes ->
            expect TokRParen afterAxes >>= fun _ remaining ->
            success (mkE tokens remaining (ExprReduce (array, op, initE, Some axesE))) remaining
        advance tokens |> expect TokLParen >>= fun _ afterLParen ->
        parseExprImpl afterLParen >>= fun array afterArr ->
        match peek afterArr with
        | Some TokRParen ->
            // 1-arg form: reduce(arr) = reduce(arr, (+))
            expect TokRParen afterArr >>= fun _ remaining ->
            let sp = rangeSpan tokens remaining
            success (mkExpr sp (ExprReduce (array, mkExpr sp (ExprSection OpAdd), None, None))) remaining
        | _ ->
            expect TokComma afterArr >>= fun _ afterComma ->
            // `reduce(A, axes = n)`: the kernel-omitted sugar, carrying an axis
            // count. Same defaulted (+) the 1-arg form supplies.
            if isAxesArg afterComma then
                parseAxesTail array (mkExpr (rangeSpan tokens afterComma) (ExprSection OpAdd)) None afterComma
            else
            parseExprImpl afterComma >>= fun op afterOp ->
            match peek afterOp with
            | Some TokRParen ->
                expect TokRParen afterOp >>= fun _ remaining ->
                success (mkE tokens remaining (ExprReduce (array, op, None, None))) remaining
            | _ ->
                expect TokComma afterOp >>= fun _ afterComma2 ->
                if isAxesArg afterComma2 then
                    parseAxesTail array op None afterComma2
                else
                parseExprImpl afterComma2 >>= fun initE afterInit ->
                match peek afterInit with
                | Some TokRParen ->
                    expect TokRParen afterInit >>= fun _ remaining ->
                    success (mkE tokens remaining (ExprReduce (array, op, Some initE, None))) remaining
                | _ ->
                    expect TokComma afterInit >>= fun _ afterComma3 ->
                    if isAxesArg afterComma3 then
                        parseAxesTail array op (Some initE) afterComma3
                    else
                        let line, col = currentPos afterComma3
                        error "reduce takes at most three positional arguments (array, kernel, init); \
the axis count is the named final argument `axes = n`" line col

    // conj(x): complex conjugate (identity on real). Lowers to ExprUnaryOp(OpConj, _).
    | Some (TokKeyword KwConj) ->
        advance tokens |> expect TokLParen >>= fun _ afterLParen ->
        parseExprImpl afterLParen >>= fun arg afterArg ->
        expect TokRParen afterArg >>= fun _ remaining ->
        success (mkE tokens remaining (ExprUnaryOp (OpConj, arg))) remaining

    | Some (TokKeyword KwExtents) ->
        advance tokens |> expect TokLParen >>= fun _ afterLParen ->
        parseExprImpl afterLParen >>= fun array afterArr ->
        expect TokRParen afterArr >>= fun _ remaining ->
        success (mkE tokens remaining (ExprExtents array)) remaining

    | Some (TokKeyword KwPure) ->
        advance tokens |> expect TokLParen >>= fun _ afterLParen ->
        parseExprImpl afterLParen >>= fun expr afterExpr ->
        expect TokRParen afterExpr >>= fun _ remaining ->
        success (mkE tokens remaining (ExprPure expr)) remaining

    // reynolds(kernel) or reynolds(kernel, Antisymmetric)
    | Some (TokKeyword KwReynolds) ->
        advance tokens |> expect TokLParen >>= fun _ afterLParen ->
        parseExprImpl afterLParen >>= fun kernel afterKernel ->
        let isAntisym, afterSpec =
            match peek afterKernel with
            | Some TokComma ->
                match peek (advance afterKernel) with
                | Some (TokIdent "Antisymmetric") -> true, advance (advance afterKernel)
                | Some (TokIdent "Symmetric") -> false, advance (advance afterKernel)
                | _ -> false, afterKernel
            | _ -> false, afterKernel
        expect TokRParen afterSpec >>= fun _ remaining ->
        success (mkE tokens remaining (ExprReynolds (kernel, isAntisym))) remaining
    
    // range<T1, ..., Tn>: one virtual array spanning all listed index types,
    // uncurried into nested loop levels in IR.
    | Some (TokKeyword KwRange) ->
        advance tokens |> expect (TokOp "<") >>= fun _ afterLt ->
        afterLt |> sepBy parseTypeExpr TokComma >>= fun tys afterTys ->
        expectGt afterTys >>= fun _ remaining ->
        success (mkE tokens remaining (ExprRange tys)) remaining

    | Some (TokKeyword KwReverse) ->
        advance tokens |> expect (TokOp "<") >>= fun _ afterLt ->
        parseTypeExpr afterLt >>= fun ty afterTy ->
        expectGt afterTy >>= fun _ remaining ->
        success (mkE tokens remaining (ExprReverse ty)) remaining

    // halo<Inner, [offsets]>: stencil traversal transformer. At each ordinal
    // position of the inner index type, the static signed offsets select the
    // neighborhood (center = 0, sign = direction). Composes inside range<...>
    // for n-D. Offsets parse via parseSimpleExpr, like other static payloads.
    | Some (TokKeyword KwHalo) ->
        advance tokens |> expect (TokOp "<") >>= fun _ afterLt ->
        parseTypeExpr afterLt >>= fun inner afterInner ->
        expect TokComma afterInner >>= fun _ afterComma ->
        parseSimpleExpr afterComma >>= fun offsets afterOffsets ->
        expectGt afterOffsets >>= fun _ remaining ->
        success (mkE tokens remaining (ExprHalo (inner, offsets))) remaining

    // Parenthesized expr or tuple (delimited: struct literals re-enabled --
    // the for-in range-header suppression applies only at the top nesting
    // level). Span widens to cover both parens; child spans are untouched.
    | Some TokLParen ->
        allowingStructLiterals (fun () -> advance tokens |> parseParenExpr) >>= fun e remaining ->
        success { e with Span = rangeSpan tokens remaining } remaining

    | Some TokLBracket ->
        allowingStructLiterals (fun () ->
            advance tokens |> sepBy parseExprImpl TokComma >>= fun elems afterElems ->
            expect TokRBracket afterElems >>= fun _ remaining ->
            success (mkE tokens remaining (ExprArrayLit elems)) remaining)

    | Some TokLBrace ->
        parseBlock (advance tokens)

    | Some kind ->
        let line, col = currentPos tokens
        error (sprintf "Unexpected token: %s" (describeToken kind)) line col

    | None ->
        errorEof "Unexpected end of input"

// Compound Expression Parsers

and parseLambda (tokens: Token list) : ParseResult<Expr> =
    expect TokLParen tokens >>= fun _ afterLParen ->
    sepBy parseLambdaParam TokComma afterLParen >>= fun parms afterParms ->
    expect TokRParen afterParms >>= fun _ afterRParen ->
    
    // Optional where clause (parallel to function declarations). A parse
    // error inside it must propagate as a genuine error, not be swallowed
    // to None -- that would produce a misleading "expected ->" at `where`
    // and hide the real cause (e.g. mutual-exclusion violations).
    let whereResult : ParseResult<WhereClause option> =
        match peek afterRParen with
        | Some (TokKeyword KwWhere) ->
            match parseWhereClause (advance afterRParen) with
            | Ok (w, rest) -> Ok (Some w, rest)
            | Error e -> Error e
        | _ -> Ok (None, afterRParen)
    whereResult >>= fun whereClause afterWhere ->
    expect (TokOp "->") afterWhere >>= fun _ afterArrow ->
    let afterArrow = skipNL afterArrow
    match peek afterArrow with
    | Some TokLBrace ->
        parseBlock (advance afterArrow) >>= fun body remaining ->
        success (mkE tokens remaining (ExprLambda (parms, whereClause, body))) remaining
    | _ ->
        // Inline body parses at Apply precedence so |> isn't consumed: this
        // means `lambda(x) -> a <@> b |> compute` parses as
        // `(lambda(x) -> a <@> b) |> compute`.
        parseApply afterArrow >>= fun body remaining ->
        success (mkE tokens remaining (ExprLambda (parms, whereClause, body))) remaining

and parseLambdaParam (tokens: Token list) : ParseResult<LambdaParam> =
    // The head token IS the name, so its span is kept for navigation before
    // the optional annotation is consumed.
    let nameSpan = headSpan tokens
    expectIdent tokens >>= fun name afterName ->
    // Optional default value: `name = expr` / `name: Type = expr`. The
    // expression parser stops at `,` and `)`, so the param list's own
    // delimiters are unaffected.
    let withDefault ty afterTy =
        match peek afterTy with
        | Some (TokOp "=") ->
            parseExprImpl (advance afterTy) >>= fun dflt remaining ->
            success { Name = name; Type = ty; Default = Some dflt; NameSpan = nameSpan } remaining
        | _ ->
            success { Name = name; Type = ty; Default = None; NameSpan = nameSpan } afterTy
    match peek afterName with
    | Some TokColon ->
        advance afterName |> parseTypeExpr >>= fun ty afterTy ->
        withDefault (Some ty) afterTy
    | _ ->
        withDefault None afterName

/// The right-hand side of a `let`, with the BARE-COMMA tuple construction of
/// docs/plan-tuples-vs-arg-packs.md 6b: `let t = b, c` builds the 2-tuple
/// `(b, c)`, `let t = a, b, c` the 3-tuple, and so on for any width >= 2.
/// This is the CONSTRUCTION half of plan issue #10 only -- the binder side
/// (`let a, b = t`) stays deferred, and the parenthesized `let (a, b) = t`
/// destructure is untouched.
///
/// Each component parses at full expression precedence and stops at the
/// comma (a comma is a separator, never an operator), so `let t = f(x), g(y)`
/// is a 2-tuple of two CALLS, not `f(x, g(y))`. The node produced is exactly
/// the `ExprTuple` a parenthesized literal produces, so the two spellings are
/// the same program from the checker down.
///
/// Shared by all three let-parse sites (`parseLet`, `parseLetStmt`,
/// `parseTopLevelLet`) so they cannot drift.
/// The optional `: T` annotation slot of a `let`, shared by all three let
/// sites. Historically each site inlined `match parseTypeExpr ... with Error
/// _ -> None, afterPat` -- a type-parse failure is SWALLOWED and the site
/// re-reports at the `=`, which turns a precise annotation diagnostic into a
/// misleading "Expected '=' but got ':'" pointing at the colon.
///
/// `isHardParseError` codes are propagated instead of swallowed -- the same
/// rule `sepBy` applies, for the same reason: a BL1004 can only be raised
/// once the slot is unambiguously an annotation, so the fallback's tolerance
/// for non-annotation colons is unchanged.
and parseLetAnnotation (afterPat: Token list) : ParseResult<TypeExpr option> =
    match peek afterPat with
    | Some TokColon ->
        match parseTypeExpr (advance afterPat) with
        | Ok (t, rest) -> success (Some t) rest
        | Error e when isHardParseError e -> Error e
        | Error _ -> success None afterPat
    | _ -> success None afterPat

/// The LHS of a `let`, in BOTH spellings: a single pattern, or a BARE COMMA
/// LIST (`let a, b = t`). The list desugars to exactly what the parenthesized
/// `let (a, b) = t` produces -- one `PatTuple` over the same leaves -- so
/// there is no second destructuring mechanism to keep in step
/// (docs/plan-array-expression-fixes.md #10, the deferred half; construction
/// `let t = b, c` is `parseLetRhs`'s mirror image, landed the same day).
///
/// UNAMBIGUOUS against RHS construction, which is the reason the two halves
/// can coexist: the LHS list is bounded by the `:` or `=` that must follow a
/// let pattern, and the RHS list only begins after that `=`. So
/// `let a, b = c, d` is "destructure the pair built from c and d", and no
/// existing program changes meaning -- a comma could not previously follow a
/// let pattern at all (it was `BL1001: Expected '=' but got ','`). The only
/// behavioural difference on ill-formed input is which token the missing-`=`
/// error points at.
///
/// Shared by all three let-parse sites (`parseLet`, `parseLetStmt`,
/// `parseTopLevelLet`) so they cannot drift.
and parseLetPattern (tokens: Token list) : ParseResult<Pattern> =
    parsePattern tokens >>= fun first afterFirst ->
    match peek afterFirst with
    | Some TokComma ->
        let rec rest (acc: Pattern list) (toks: Token list) : ParseResult<Pattern list> =
            parsePattern toks >>= fun p afterP ->
            match peek afterP with
            | Some TokComma -> rest (p :: acc) (advance afterP)
            | _ -> success (List.rev (p :: acc)) afterP
        rest [] (advance afterFirst) >>= fun tail afterTail ->
        success (mkP tokens afterTail (PatTuple (first :: tail))) afterTail
    | _ -> success first afterFirst

and parseLetRhs (tokens: Token list) : ParseResult<Expr> =
    parseExprImpl tokens >>= fun first afterFirst ->
    match peek afterFirst with
    | Some TokComma ->
        advance afterFirst |> sepBy parseExprImpl TokComma >>= fun rest afterRest ->
        success (mkExpr (rangeSpan tokens afterRest) (ExprTuple (first :: rest))) afterRest
    | _ -> success first afterFirst

and parseLet (tokens: Token list) : ParseResult<Expr> =
    // let [mut] pattern [: type] = value. Blade has no ML-style
    // "let x = v in body" -- `in` is only used for virtual arrays in for-loops.
    // There is NO `const` in the surface language: BindConst exists only as
    // the internal immutability marker minted by `let static` and the local
    // `function` desugar.
    let mutability, afterMut =
        match peek tokens with
        | Some (TokKeyword KwMut) -> BindMut, advance tokens
        | _ -> BindLet, tokens
    
    parseLetPattern afterMut >>= fun pat afterPat ->
    parseLetAnnotation afterPat >>= fun ty afterTy ->

    expect (TokOp "=") afterTy >>= fun _ afterEq ->

    let afterEq = skipNL afterEq
    match peek afterEq with
    | Some TokLBrace ->
        parseBlock (advance afterEq) >>= fun value afterValue ->
        let binding = { Mutability = mutability; Pattern = pat; Type = ty; Value = value }
        // Blade has no `let ... in`; the body is a synthesized unit placeholder
        // spanning the same source region as the let.
        let sp = rangeSpan tokens afterValue
        success (mkExpr sp (ExprLet (binding, mkExpr sp (ExprLit LitUnit)))) afterValue
    | _ ->
        parseLetRhs afterEq >>= fun value afterValue ->
        let binding = { Mutability = mutability; Pattern = pat; Type = ty; Value = value }
        let sp = rangeSpan tokens afterValue
        success (mkExpr sp (ExprLet (binding, mkExpr sp (ExprLit LitUnit)))) afterValue

and parseIf (tokens: Token list) : ParseResult<Expr> =
    parseExprImpl tokens >>= fun cond afterCond ->
    expect (TokKeyword KwThen) afterCond >>= fun _ afterThen ->
    parseExprImpl afterThen >>= fun thenBr afterThenBr ->
    expect (TokKeyword KwElse) afterThenBr >>= fun _ afterElse ->
    parseExprImpl afterElse >>= fun elseBr remaining ->
    success (mkE tokens remaining (ExprIf (cond, thenBr, elseBr))) remaining

and parseMatch (tokens: Token list) : ParseResult<Expr> =
    parseExprImpl tokens >>= fun scrutinee afterScrutinee ->
    expect (TokKeyword KwWith) afterScrutinee >>= fun _ afterWith ->
    many parseMatchCase (skipNL afterWith) >>= fun cases remaining ->
    success (mkE tokens remaining (ExprMatch (scrutinee, cases))) remaining

// Guard-parse errors propagate rather than being swallowed.
and parseMatchCase (tokens: Token list) : ParseResult<MatchCase> =
    let tokens = skipNL tokens
    match peek tokens with
    | Some TokPipe ->
        advance tokens |> parsePattern >>= fun pat afterPat ->
        match peek afterPat with
        | Some (TokKeyword KwIf) ->
            advance afterPat |> parseGuardExpr >>= fun guard afterGuard ->
            expect (TokOp "->") afterGuard >>= fun _ afterArrow ->
            // parseBody: inline expressions stop at newline; multi-line
            // bodies (e.g. nested match) require braces.
            parseBody afterArrow >>= fun body remaining ->
            success { Pattern = pat; Guard = Some guard; Body = body } remaining
        | _ ->
            expect (TokOp "->") afterPat >>= fun _ afterArrow ->
            parseBody afterArrow >>= fun body remaining ->
            success { Pattern = pat; Guard = None; Body = body } remaining
    | _ ->
        let line, col = currentPos tokens
        error "Expected '|' to start match case" line col

// Restricted expression parser for match guards: stops before -> (doesn't consume it).
and parseGuardExpr (tokens: Token list) : ParseResult<Expr> =
    parseGuardOr tokens

and parseGuardOr (tokens: Token list) : ParseResult<Expr> =
    parseGuardAnd tokens >>= fun left rest ->
    let rec loop acc toks =
        match peek toks with
        | Some (TokOp "||") ->
            advance toks |> parseGuardAnd >>= fun right remaining ->
            loop (mkExpr (mergeSpan acc.Span right.Span) (ExprBinOp (Elementwise, OpOr, acc, right))) remaining
        | _ -> success acc toks
    loop left rest

and parseGuardAnd (tokens: Token list) : ParseResult<Expr> =
    parseGuardComparison tokens >>= fun left rest ->
    let rec loop acc toks =
        match peek toks with
        | Some (TokOp "&&") ->
            advance toks |> parseGuardComparison >>= fun right remaining ->
            loop (mkExpr (mergeSpan acc.Span right.Span) (ExprBinOp (Elementwise, OpAnd, acc, right))) remaining
        | _ -> success acc toks
    loop left rest

and parseGuardComparison (tokens: Token list) : ParseResult<Expr> =
    parseGuardAdditive tokens >>= fun left rest ->
    match peek rest with
    | Some (TokOp "==") ->
        advance rest |> parseGuardAdditive >>= fun right remaining ->
        success (mkExpr (mergeSpan left.Span right.Span) (ExprBinOp (Elementwise, OpEq, left, right))) remaining
    | Some (TokOp "!=") ->
        advance rest |> parseGuardAdditive >>= fun right remaining ->
        success (mkExpr (mergeSpan left.Span right.Span) (ExprBinOp (Elementwise, OpNeq, left, right))) remaining
    | Some (TokOp "<") ->
        advance rest |> parseGuardAdditive >>= fun right remaining ->
        success (mkExpr (mergeSpan left.Span right.Span) (ExprBinOp (Elementwise, OpLt, left, right))) remaining
    | Some (TokOp "<=") ->
        advance rest |> parseGuardAdditive >>= fun right remaining ->
        success (mkExpr (mergeSpan left.Span right.Span) (ExprBinOp (Elementwise, OpLe, left, right))) remaining
    | Some (TokOp ">") ->
        advance rest |> parseGuardAdditive >>= fun right remaining ->
        success (mkExpr (mergeSpan left.Span right.Span) (ExprBinOp (Elementwise, OpGt, left, right))) remaining
    | Some (TokOp ">=") ->
        advance rest |> parseGuardAdditive >>= fun right remaining ->
        success (mkExpr (mergeSpan left.Span right.Span) (ExprBinOp (Elementwise, OpGe, left, right))) remaining
    | _ -> success left rest

and parseGuardAdditive (tokens: Token list) : ParseResult<Expr> =
    parseGuardMultiplicative tokens >>= fun left rest ->
    let rec loop acc toks =
        match peek toks with
        | Some (TokOp "+") ->
            advance toks |> parseGuardMultiplicative >>= fun right remaining ->
            loop (mkExpr (mergeSpan acc.Span right.Span) (ExprBinOp (Elementwise, OpAdd, acc, right))) remaining
        | Some (TokOp "-") ->
            advance toks |> parseGuardMultiplicative >>= fun right remaining ->
            loop (mkExpr (mergeSpan acc.Span right.Span) (ExprBinOp (Elementwise, OpSub, acc, right))) remaining
        | _ -> success acc toks
    loop left rest

and parseGuardMultiplicative (tokens: Token list) : ParseResult<Expr> =
    parseGuardPrimary tokens >>= fun left rest ->
    let rec loop acc toks =
        match peek toks with
        | Some (TokOp "*") ->
            advance toks |> parseGuardPrimary >>= fun right remaining ->
            loop (mkExpr (mergeSpan acc.Span right.Span) (ExprBinOp (Elementwise, OpMul, acc, right))) remaining
        | Some (TokOp "/") ->
            advance toks |> parseGuardPrimary >>= fun right remaining ->
            loop (mkExpr (mergeSpan acc.Span right.Span) (ExprBinOp (Elementwise, OpDiv, acc, right))) remaining
        | _ -> success acc toks
    loop left rest

and parseGuardPrimary (tokens: Token list) : ParseResult<Expr> =
    match peek tokens with
    | Some (LiteralTok lit) -> success (mkExpr (headSpan tokens) (ExprLit lit)) (advance tokens)
    | Some (TokIdent name) ->
        let afterName = advance tokens
        match peek afterName with
        | Some TokLParen ->
            advance afterName |> sepBy parseGuardExpr TokComma >>= fun args afterArgs ->
            expect TokRParen afterArgs >>= fun _ remaining ->
            let fn = mkExpr (headSpan tokens) (ExprVar name)
            success (mkE tokens remaining (ExprApp (fn, args))) remaining
        | _ -> success (mkExpr (headSpan tokens) (ExprVar name)) afterName
    | Some TokLParen ->
        advance tokens |> parseGuardExpr >>= fun expr afterExpr ->
        expect TokRParen afterExpr >>= fun _ remaining ->
        success expr remaining
    | Some (TokOp "-") ->
        advance tokens |> parseGuardPrimary >>= fun expr remaining ->
        success (mkExpr (mergeSpan (headSpan tokens) expr.Span) (ExprUnaryOp (OpNeg, expr))) remaining
    | Some (TokOp "!") ->
        advance tokens |> parseGuardPrimary >>= fun expr remaining ->
        success (mkExpr (mergeSpan (headSpan tokens) expr.Span) (ExprUnaryOp (OpNot, expr))) remaining
    | Some kind ->
        let line, col = currentPos tokens
        error (sprintf "Unexpected token in guard: %s" (describeToken kind)) line col
    | None ->
        errorEof "Expected guard expression but got end of file"

and parseMethodFor (tokens: Token list) : ParseResult<Expr> =
    expect TokLParen tokens >>= fun _ afterLParen ->
    sepBy parseExprImpl TokComma afterLParen >>= fun arrays afterArrays ->
    expect TokRParen afterArrays >>= fun _ remaining ->
    success (mkE tokens remaining (ExprMethodFor arrays)) remaining

/// The body of a `for` expression (tokens start after the `for` keyword):
/// `for (A, B) [in virt]` -> ForArrays; `for lambda(...)` -> ForKernel.
/// Shared by the plain and `static`-marked spellings.
and parseForConstruct (tokens: Token list) : ParseResult<Expr> =
    match peek tokens with
    | Some TokLParen ->
        advance tokens |> sepBy parseExprImpl TokComma >>= fun arrays afterArrays ->
        expect TokRParen afterArrays >>= fun _ afterRParen ->
        match peek afterRParen with
        | Some (TokKeyword KwIn) ->
            // Virtual array expression parses at arrayProduct level (stops before <@>).
            parseArrayProduct (advance afterRParen) >>= fun inExpr afterIn ->
            success (mkE tokens afterIn (ExprFor (ForArrays (arrays, Some inExpr), [], None))) afterIn
        | _ ->
            // No in-clause: equivalent to method_for(A, B)
            success (mkE tokens afterRParen (ExprFor (ForArrays (arrays, None), [], None))) afterRParen
    | _ ->
        parseExprImpl tokens >>= fun kernel remaining ->
        success (mkE tokens remaining (ExprFor (ForKernel kernel, [], None))) remaining

and parseObjectFor (tokens: Token list) : ParseResult<Expr> =
    expect TokLParen tokens >>= fun _ afterLParen ->
    // Combinator section: object_for(<&>), object_for(<&!>), object_for(<*>), etc.
    match peek afterLParen with
    | Some (TokOp op) ->
        let afterOp = advance afterLParen
        match peek afterOp with
        | Some TokRParen ->
            let binOp = combinatorOrScalarSection op
            match binOp with
            | Some b ->
                let remaining = advance afterOp
                let sp = rangeSpan tokens remaining
                success (mkExpr sp (ExprObjectFor (mkExpr sp (ExprSection b)))) remaining
            | None ->
                let line, col = currentPos afterLParen
                error (sprintf "Unknown operator in object_for: %s" op) line col
        | _ ->
            parseExprImpl afterLParen >>= fun kernel afterKernel ->
            expect TokRParen afterKernel >>= fun _ remaining ->
            success (mkE tokens remaining (ExprObjectFor kernel)) remaining
    | _ ->
        parseExprImpl afterLParen >>= fun kernel afterKernel ->
        expect TokRParen afterKernel >>= fun _ remaining ->
        success (mkE tokens remaining (ExprObjectFor kernel)) remaining

and parseParenExpr (tokens: Token list) : ParseResult<Expr> =
    match peek tokens with
    | Some TokRParen ->
        let remaining = advance tokens
        success (mkE tokens remaining (ExprTuple [])) remaining
    // Operator section: (+), (*), (<&!>), etc.
    | Some (TokOp op) ->
        let afterOp = advance tokens
        match peek afterOp with
        | Some TokRParen ->
            match combinatorOrScalarSection op with
            | Some binOp ->
                let remaining = advance afterOp
                success (mkE tokens remaining (ExprSection binOp)) remaining
            | None ->
                let line, col = currentPos tokens
                error (sprintf "Unknown operator in section: %s" op) line col
        | _ ->
            parseExprImpl tokens >>= fun first afterFirst ->
            match peek afterFirst with
            | Some TokRParen ->
                success first (advance afterFirst)
            | Some TokComma ->
                advance afterFirst |> sepBy parseExprImpl TokComma >>= fun rest afterRest ->
                expect TokRParen afterRest >>= fun _ remaining ->
                success (mkE tokens remaining (ExprTuple (first :: rest))) remaining
            | _ ->
                let line, col = currentPos afterFirst
                errorC "BL1001" "Expected ')' or ',' in parenthesized expression" line col
    | _ ->
        parseExprImpl tokens >>= fun first afterFirst ->
        match peek afterFirst with
        | Some TokRParen ->
            success first (advance afterFirst)
        | Some TokComma ->
            advance afterFirst |> sepBy parseExprImpl TokComma >>= fun rest afterRest ->
            expect TokRParen afterRest >>= fun _ remaining ->
            success (mkE tokens remaining (ExprTuple (first :: rest))) remaining
        | _ ->
            let line, col = currentPos afterFirst
            errorC "BL1001" "Expected ')' or ',' in parenthesized expression" line col

/// Convert operator string to BinOp
/// The section table shared by `(op)` and `object_for(op)`: the COMBINATOR
/// operators first, then the scalar ops. One table, so the two spellings can
/// never disagree about which operators have a section -- which is what let
/// `object_for(<&!>)` parse while `(<&!>)` did not (BL1999 "Unknown operator
/// in section"), even though the reduction-join forms need both.
and combinatorOrScalarSection (op: string) : BinOp option =
    match op with
    | "<&>" -> Some OpParallel
    | "<&!>" -> Some OpFusion
    | "<*>" -> Some OpArrayProd
    | "<@>" -> Some OpApply
    | "<$>" -> Some OpFunctor
    | "<|>" -> Some OpChoice
    | "<|:>" -> Some OpFallback
    | ">>=" -> Some OpBind
    | ">>@" -> Some OpComposeObj
    | "@>>" -> Some OpComposeMeth
    | _ -> stringToBinOp op  // scalar ops (+, *, etc.)

and stringToBinOp (op: string) : BinOp option =
    match op with
    | "+" -> Some OpAdd
    | "-" -> Some OpSub
    | "*" -> Some OpMul
    | "/" -> Some OpDiv
    | "%" -> Some OpMod
    | "^" -> Some OpCaret
    | "==" -> Some OpEq
    | "!=" -> Some OpNeq
    | "<" -> Some OpLt
    | "<=" -> Some OpLe
    | ">" -> Some OpGt
    | ">=" -> Some OpGe
    | "&&" -> Some OpAnd
    | "||" -> Some OpOr
    | _ -> None

and parseBlock (tokens: Token list) : ParseResult<Expr> =
    let rec loop stmts toks =
        let toks = skipNL toks
        // Statement span (audit 3.4): start = first token; end filled in by
        // `spanned` below using the last meaningful token consumed (not the
        // next token's start, which would overshoot across whitespace/comments).
        let sLine, sCol = currentPos toks
        let spanned (remaining: Token list) (stmt: Stmt) =
            let eLine, eCol = consumedEnd toks remaining sLine sCol
            StmtSpanned (stmt, { StartLine = sLine; StartCol = sCol; EndLine = eLine; EndCol = eCol; File = currentFile })
        match peek toks with
        | Some TokRBrace ->
            // Last expression (if any) is the block's return value. stmts is
            // in reverse order (most recent first), so head = last statement.
            let (statements, finalExpr) =
                match stmts with
                | StmtSpanned (StmtExpr e, _) :: rest
                | StmtExpr e :: rest -> List.rev rest, Some e
                | all -> List.rev all, None
            // Span runs from the first statement token to the closing brace
            // (`tokens` is positioned just after the opening `{`).
            success (mkE tokens (advance toks) (ExprBlock (statements, finalExpr))) (advance toks)
        | Some TokSemi ->
            loop stmts (advance toks)
        | Some (TokKeyword KwLet) ->
            advance toks |> parseLetStmt >>= fun stmt remaining ->
            let remaining = skipTerminator remaining
            loop (spanned remaining stmt :: stmts) remaining
        | Some (TokKeyword KwFunction) ->
            // Nested function declaration: parsed as a let binding of a lambda.
            advance toks |> parseNestedFunction >>= fun stmt remaining ->
            let remaining = skipTerminator remaining
            loop (spanned remaining stmt :: stmts) remaining
        | Some (TokKeyword KwFor) ->
            // Imperative `for IDENT in a..b { }` is removed from the surface
            // language: iteration is loop objects (parallel) or recursive
            // arrays (sequential). This shell stays only to give the shape a
            // steering diagnostic instead of a confusing misparse at `in`.
            // (Internal generators still construct StmtForIn directly.)
            let afterFor = advance toks
            match peek afterFor with
            | Some (TokIdent _) ->
                let afterIdent = advance afterFor
                match peek afterIdent with
                | Some (TokKeyword KwIn) ->
                    let line, col = currentPos toks
                    errorC "BL1003"
                        "The imperative `for x in a..b { ... }` statement has been removed. Re-express sequential recurrences as a recursive array (`let rec q: Array<T like Step, ...> = match q with | zero -> zero | prefix :: n -> prefix :: <slice>`), folds as `reduce(...)`, and parallel maps as `method_for(range<...>) <@> lambda(...)`. See formalism 7.5."
                        line col
                | _ ->
                    // Loop-object `for` expression: the surviving form.
                    parseExprImpl toks >>= fun expr remaining ->
                    let remaining = skipTerminator remaining
                    loop (spanned remaining (StmtExpr expr) :: stmts) remaining
            | _ ->
                parseExprImpl toks >>= fun expr remaining ->
                let remaining = skipTerminator remaining
                loop (spanned remaining (StmtExpr expr) :: stmts) remaining
        | Some _ ->
            parseExprImpl toks >>= fun expr remaining ->
            let remaining = skipTerminator remaining
            loop (spanned remaining (StmtExpr expr) :: stmts) remaining
        | None ->
            errorEof "Unexpected end of input in block"
    loop [] tokens

and skipTerminator toks =
    match peek toks with
    | Some TokNewline -> advance toks
    | Some TokSemi -> advance toks
    | _ -> toks

and parseNestedFunction (tokens: Token list) : ParseResult<Stmt> =
    // `function name(params) where ... -> Type = body` desugars to
    // `let name = lambda(params) where ... -> Type -> body`.
    expectIdent tokens >>= fun name afterName ->
    expect TokLParen afterName >>= fun _ afterLParen ->
    sepBy parseLambdaParam TokComma afterLParen >>= fun parms afterParms ->
    expect TokRParen afterParms >>= fun _ afterRParen ->

    let afterRParen = skipNL afterRParen

    // A parse error inside an optional where clause must propagate as a
    // genuine error (mirrors parseLambda), not be swallowed to None -- that
    // would fail later with a misleading "expected =" at `where`.
    let whereResult : ParseResult<WhereClause option> =
        match peek afterRParen with
        | Some (TokKeyword KwWhere) ->
            match parseWhereClause (advance afterRParen) with
            | Ok (w, rest) -> Ok (Some w, skipNL rest)
            | Error e -> Error e
        | _ -> Ok (None, afterRParen)
    whereResult >>= fun whereClause afterWhere ->

    // A swallowed type-parse failure re-reports at the `=`; `isHardParseError`
    // codes are precise enough that losing them would be strictly worse.
    (match peek afterWhere with
     | Some (TokOp "->") ->
         (match parseTypeExpr (advance afterWhere) with
          | Ok (t, rest) -> success (Some t) (skipNL rest)
          | Error e when isHardParseError e -> Error e
          | Error _ -> success None afterWhere)
     | _ -> success None afterWhere) >>= fun retType afterRet ->

    expect (TokOp "=") afterRet >>= fun _ afterEq ->
    parseInlineOrBlock afterEq >>= fun body remaining ->

    let lambda = mkExpr (rangeSpan tokens remaining) (ExprLambda (parms, whereClause, body))
    let binding = {
        Pattern = mkPat (headSpan tokens) (PatVar name)
        Type = retType
        Value = lambda
        Mutability = BindConst
    }
    success (StmtLet binding) remaining

/// Parse the binding tail of `let rec NAME : TYPE = match NAME with ...`
/// (tokens start at NAME). Shared by the block-level and top-level let
/// paths. The arm shapes are validated HERE -- productivity is syntactic:
///   | zero        -> zero              required base (extent 0)
///   | zero :: n   -> zero :: SEED      optional seed (extent 1)
///   | prefix :: n -> prefix :: SLICE   required inductive arm
/// The snoc `::` exists ONLY inside these arm bodies (no general array-cons
/// expression operator), so the inductive arm literally cannot produce more
/// or less than one new slice.
and parseRecArrayBinding (tokens: Token list) : ParseResult<Binding> =
    let errHere toks msg =
        let line, col = currentPos toks
        errorC "BL1003" msg line col
    expectIdent tokens >>= fun name afterName ->
    // Type annotation is REQUIRED: a self-referential definition cannot
    // infer its own type from its body (mirrors recursive functions
    // declaring return types).
    match peek afterName with
    | Some TokColon ->
        parseTypeExpr (advance afterName) >>= fun ty afterTy ->
        expect (TokOp "=") afterTy >>= fun _ afterEq ->
        let afterEq = skipNL afterEq
        match peek afterEq with
        | Some (TokKeyword KwMatch) ->
            let afterMatch = advance afterEq
            (match peek afterMatch with
             | Some (TokIdent scrut) when scrut = name -> success () (advance afterMatch)
             | _ -> errHere afterMatch (sprintf "recursive array '%s': the match scrutinee must be the array being defined (`match %s with`)" name name))
            >>= fun () afterScrut ->
            expect (TokKeyword KwWith) afterScrut >>= fun _ afterWith ->
            // --- arm 1 (required): | zero -> zero
            let afterWith = skipNL afterWith
            expect TokPipe afterWith >>= fun _ a1 ->
            (match peek a1 with
             | Some (TokKeyword KwZero) -> success () (advance a1)
             | _ -> errHere a1 (sprintf "recursive array '%s': the first arm must be the base case `| zero -> zero` (extent 0 is the empty array)" name))
            >>= fun () a2 ->
            expect (TokOp "->") a2 >>= fun _ a3 ->
            (match peek a3 with
             | Some (TokKeyword KwZero) -> success () (advance a3)
             | _ -> errHere a3 (sprintf "recursive array '%s': the base arm's body must be `zero`" name))
            >>= fun () afterBase ->
            // --- arm 2 (optional seed) / arm 3 (required inductive):
            //     | zero :: n -> zero :: SEED
            //     | prefix :: n -> prefix :: SLICE
            let parseConsArm (toks: Token list) : ParseResult<bool * Ident * Ident * Expr> =
                // returns (isSeedArm, prefixOrZeroName, stepVar, sliceExpr)
                expect TokPipe (skipNL toks) >>= fun _ t1 ->
                let isSeed, pfxName, t2res =
                    match peek t1 with
                    | Some (TokKeyword KwZero) -> true, "", Ok (advance t1)
                    | Some (TokIdent p) -> false, p, Ok (advance t1)
                    | _ -> false, "", Error ()
                match t2res with
                | Error () -> errHere t1 (sprintf "recursive array '%s': expected `zero :: n` (seed arm) or `prefix :: n` (inductive arm) pattern" name)
                | Ok t2 ->
                expect TokColonColon t2 >>= fun _ t3 ->
                expectIdent t3 >>= fun stepVar t4 ->
                expect (TokOp "->") t4 >>= fun _ t5 ->
                // Body must open with the SAME constructor head: `zero ::` /
                // `prefix ::` -- this is the productivity check.
                let headOk, t6res =
                    match peek t5, isSeed with
                    | Some (TokKeyword KwZero), true -> true, Ok (advance t5)
                    | Some (TokIdent p), false when p = pfxName -> true, Ok (advance t5)
                    | _ -> false, Error ()
                match t6res with
                | Error () ->
                    let expected = if isSeed then "zero :: <seed slice>" else sprintf "%s :: <slice expr>" pfxName
                    errHere t5 (sprintf "recursive array '%s': the arm body must produce exactly one new slice -- `%s`" name expected)
                | Ok t6 ->
                let _ = headOk
                expect TokColonColon t6 >>= fun _ t7 ->
                suppressingStructLiterals (fun () -> parseExprImpl t7) >>= fun slice t8 ->
                success (isSeed, pfxName, stepVar, slice) t8
            parseConsArm afterBase >>= fun (isSeed1, pfx1, step1, slice1) afterArm2 ->
            if isSeed1 then
                // seed arm present; inductive arm must follow
                parseConsArm afterArm2 >>= fun (isSeed2, pfx2, step2, slice2) afterArm3 ->
                if isSeed2 then
                    errHere afterArm2 (sprintf "recursive array '%s': only one seed arm (`zero :: n`) is allowed; expected the inductive arm `prefix :: n -> prefix :: <slice>`" name)
                else
                    let sp = rangeSpan tokens afterArm3
                    success {
                        Mutability = BindLet
                        // The PATTERN is the name token, not the whole `let rec
                        // ... match ... with` block the value spans -- otherwise
                        // rename would rewrite the entire declaration.
                        Pattern = mkPat (headSpan tokens) (PatVar name)
                        Type = Some ty
                        Value = mkExpr sp (ExprRecArray {
                            Name = name; SeedArm = Some (step1, slice1)
                            PrefixVar = pfx2; StepVar = step2; SliceExpr = slice2 })
                    } afterArm3
            else
                let sp = rangeSpan tokens afterArm2
                success {
                    Mutability = BindLet
                    Pattern = mkPat (headSpan tokens) (PatVar name)
                    Type = Some ty
                    Value = mkExpr sp (ExprRecArray {
                        Name = name; SeedArm = None
                        PrefixVar = pfx1; StepVar = step1; SliceExpr = slice1 })
                } afterArm2
        | _ ->
            errHere afterEq (sprintf "recursive array '%s': the body must be `match %s with | zero -> zero | prefix :: n -> prefix :: <slice>`" name name)
    | _ ->
        errHere afterName (sprintf "recursive array '%s' requires an explicit type annotation (`let rec %s: Array<T like Step, ...> = ...`) -- a self-referential definition cannot infer its own type" name name)

and parseLetStmt (tokens: Token list) : ParseResult<Stmt> =
    match peek tokens with
    | Some (TokKeyword KwRec) ->
        parseRecArrayBinding (advance tokens) >>= fun binding remaining ->
        success (StmtLet binding) remaining
    | _ ->
    let mutability, afterMut =
        match peek tokens with
        | Some (TokKeyword KwMut) -> BindMut, advance tokens
        | _ -> BindLet, tokens

    parseLetPattern afterMut >>= fun pat afterPat ->
    parseLetAnnotation afterPat >>= fun ty afterTy ->

    expect (TokOp "=") afterTy >>= fun _ afterEq ->
    parseLetRhs afterEq >>= fun value remaining ->

    success (StmtLet {
        Mutability = mutability
        Pattern = pat
        Type = ty
        Value = value
    }) remaining

// Declaration Parsing

let parseParamDecl (tokens: Token list) : ParseResult<ParamDecl> =
    let nameSpan = headSpan tokens
    expectIdent tokens >>= fun name afterName ->
    // Optional default value after the (optional) annotation:
    // `x: Type = expr` or `x = expr`. Trailing/scope rules are the type
    // checker's (BL3012); the grammar only collects the expression.
    let withDefault ty mutability afterTy =
        match peek afterTy with
        | Some (TokOp "=") ->
            parseExprImpl (advance afterTy) >>= fun dflt remaining ->
            success { Name = name; Type = ty; Mutability = mutability; Default = Some dflt; NameSpan = nameSpan } remaining
        | _ ->
            success { Name = name; Type = ty; Mutability = mutability; Default = None; NameSpan = nameSpan } afterTy
    match peek afterName with
    | Some TokColon ->
        // Optional mutability marker before the type: `x: mut T` (formalism
        // 2.7, permits callee mutation; used by grad's gradient out-buffers).
        // All params pass by reference already, so this is a checking
        // property, not a calling-convention one.
        let mutability, afterAnnot =
            match peek (advance afterName) with
            | Some (TokKeyword KwMut) -> Mutable, advance (advance afterName)
            | _ -> Immutable, advance afterName
        parseTypeExpr afterAnnot >>= fun ty afterTy ->
        withDefault (Some ty) mutability afterTy
    | _ ->
        withDefault None Immutable afterName

let parseFunctionDecl (tokens: Token list) : ParseResult<Decl> =
    // `tokens` starts AT the name (the `function` keyword is consumed by the
    // dispatcher), so the head token is exactly what F12 should land on.
    let nameSpan = headSpan tokens
    expectIdent tokens >>= fun name afterName ->
    expect TokLParen afterName >>= fun _ afterLParen ->
    sepBy parseParamDecl TokComma afterLParen >>= fun parms afterParms ->
    expect TokRParen afterParms >>= fun _ afterRParen ->

    let afterRParen = skipNL afterRParen

    // A parse error inside an optional where clause must propagate as a
    // genuine error, not be swallowed to None (which would fail later with a
    // misleading "expected =" at `where`).
    let whereResult : ParseResult<WhereClause option> =
        match peek afterRParen with
        | Some (TokKeyword KwWhere) ->
            match parseWhereClause (advance afterRParen) with
            | Ok (w, rest) -> Ok (Some w, skipNL rest)
            | Error e -> Error e
        | _ -> Ok (None, afterRParen)
    whereResult >>= fun whereClause afterWhere ->

    // Return type: either : Type or -> Type. A swallowed type-parse failure
    // re-reports at the `=`; `isHardParseError` codes propagate instead.
    let parseRet (afterArrow: Token list) : ParseResult<TypeExpr option> =
        match parseTypeExpr afterArrow with
        | Ok (t, rest) -> success (Some t) (skipNL rest)
        | Error e when isHardParseError e -> Error e
        | Error _ -> success None afterWhere
    (match peek afterWhere with
     | Some TokColon -> parseRet (advance afterWhere)
     | Some (TokOp "->") -> parseRet (advance afterWhere)
     | _ -> success None afterWhere) >>= fun retType afterRet ->

    expect (TokOp "=") afterRet >>= fun _ afterEq ->
    parseInlineOrBlock afterEq >>= fun body remaining ->
    
    success (DeclFunction {
        Name = name
        TypeParams = []
        Params = parms
        WhereClause = whereClause
        ReturnType = retType
        Body = body
        IsStatic = false
        NameSpan = nameSpan
    }) remaining

let parseTopLevelLet (tokens: Token list) : ParseResult<Decl> =
    match peek tokens with
    | Some (TokKeyword KwRec) ->
        parseRecArrayBinding (advance tokens) >>= fun binding remaining ->
        success (DeclLet binding) remaining
    | _ ->
    let mutability, afterMut =
        match peek tokens with
        | Some (TokKeyword KwMut) -> BindMut, advance tokens
        | _ -> BindLet, tokens

    parseLetPattern afterMut >>= fun pat afterPat ->
    parseLetAnnotation afterPat >>= fun ty afterTy ->

    expect (TokOp "=") afterTy >>= fun _ afterEq ->
    parseLetRhs afterEq >>= fun value remaining ->

    success (DeclLet {
        Mutability = mutability
        Pattern = pat
        Type = ty
        Value = value
    }) remaining

// Type, Struct, Interface, Impl Declarations

/// Parse type parameters: <T, U, ...>
let parseTypeParams (tokens: Token list) : Ident list * Token list =
    match peek tokens with
    | Some (TokOp "<") ->
        let rec loop acc toks =
            match peek toks with
            | Some (TokIdent name) ->
                let afterName = advance toks
                match peek afterName with
                | Some TokComma -> loop (name :: acc) (advance afterName)
                | Some (TokOp ">") -> (List.rev (name :: acc), advance afterName)
                | Some (TokOp ">>") ->
                    // Split >>: consume one >, leave one >
                    match afterName with
                    | t :: rest -> (List.rev (name :: acc), { t with Kind = TokOp ">"; Col = t.Col + 1; Length = 1 } :: rest)
                    | _ -> (List.rev (name :: acc), afterName)
                | _ -> (List.rev (name :: acc), afterName)
            | Some (TokOp ">") -> (List.rev acc, advance toks)
            | Some (TokOp ">>") ->
                // Split >>: consume one >, leave one >
                match toks with
                | t :: rest -> (List.rev acc, { t with Kind = TokOp ">"; Col = t.Col + 1; Length = 1 } :: rest)
                | _ -> (List.rev acc, toks)
            | _ -> (List.rev acc, toks)
        loop [] (advance tokens)
    | _ -> ([], tokens)

/// Parse a variant: Name or Name : Type
let parseVariant (tokens: Token list) : ParseResult<VariantDecl> =
    match peek tokens with
    | Some (TokIdent name) ->
        let afterName = advance tokens
        match peek afterName with
        | Some TokColon ->
            parseTypeExpr (advance afterName) >>= fun ty remaining ->
            success { Name = name; Data = Some ty } remaining
        | _ ->
            success { Name = name; Data = None } afterName
    | _ ->
        let line, col = currentPos tokens
        error "Expected variant name" line col

/// Parse sum type: Variant1 | Variant2 : T | Variant3
let parseSumType (tokens: Token list) : ParseResult<VariantDecl list> =
    let tokens = 
        match peek tokens with
        | Some TokPipe -> advance tokens
        | _ -> tokens
    
    let rec loop variants toks =
        parseVariant toks >>= fun v afterV ->
        let afterV = skipNL afterV
        match peek afterV with
        | Some TokPipe -> loop (v :: variants) (skipNL (advance afterV))
        | _ -> success (List.rev (v :: variants)) afterV
    
    loop [] tokens

/// Parse a comma-separated conjunct list after `where`: c1, c2, ... Each
/// conjunct is a full expression. A top-level comma always separates
/// conjuncts -- tuple expressions require parentheses, so parseExpr never
/// consumes one.
let parseConjuncts (tokens: Token list) : ParseResult<Expr list> =
    let rec loop acc toks =
        parseExpr (skipNL toks) >>= fun c afterC ->
        match peek afterC with
        | Some TokComma -> loop (acc @ [c]) (advance afterC)
        | _ -> success (acc @ [c]) afterC
    loop [] tokens

/// Parse type declaration: type Name<T> = ... (alias or sum type)
let parseTypeDecl (tokens: Token list) : ParseResult<Decl> =
    match peek tokens with
    | Some (TokIdent name) ->
        let afterName = advance tokens
        let typeParams, afterParams = parseTypeParams afterName
        expect (TokOp "=") (skipNL afterParams) >>= fun _ afterEq ->
        let afterEq = skipNL afterEq

        // Check if it's a sum type (starts with | or identifier followed by |)
        let isSumType =
            match peek afterEq with
            | Some TokPipe -> true
            | Some (TokIdent _) ->
                match parseVariant afterEq with
                | Ok (_, rest) ->
                    let rest = skipNL rest
                    match peek rest with
                    | Some TokPipe -> true
                    | _ -> false
                | Error _ -> false
            | _ -> false

        if isSumType then
            parseSumType afterEq >>= fun variants remaining ->
            success (DeclType (TyDeclSum (name, typeParams, variants))) remaining
        else
            parseTypeExpr afterEq >>= fun ty remaining ->
            // Mutual-group continuation:
            //   type N1 = T1 (and N2 = T2)+ where c1, c2, ...
            // `and` only ever continues a type decl, so peeking after a
            // complete alias body (newlines allowed) is unambiguous.
            let rec parseAndMembers acc toks =
                let toks' = skipNL toks
                match peek toks' with
                | Some (TokKeyword KwAnd) ->
                    match peek (advance toks') with
                    | Some (TokIdent mname) ->
                        let afterMName = advance (advance toks')
                        expect (TokOp "=") (skipNL afterMName) >>= fun _ afterMEq ->
                        parseTypeExpr (skipNL afterMEq) >>= fun mty afterMTy ->
                        parseAndMembers (acc @ [(mname, mty)]) afterMTy
                    | _ ->
                        let line, col = currentPos (advance toks')
                        error "Expected member name after 'and' in mutually constrained type declaration" line col
                | _ -> success acc toks'
            parseAndMembers [] remaining >>= fun members afterMembers ->
            if members.IsEmpty then
                // Plain alias: hand back the original remainder so trailing
                // newlines are consumed by the decl loop.
                success (DeclType (TyDeclAlias (name, typeParams, ty))) remaining
            elif not typeParams.IsEmpty then
                let line, col = currentPos tokens
                error "Mutually constrained type aliases cannot take type parameters" line col
            else
                let afterMembers' = skipNL afterMembers
                match peek afterMembers' with
                | Some (TokKeyword KwWhere) ->
                    parseConjuncts (advance afterMembers') >>= fun conjuncts afterWhere ->
                    success (DeclType (TyDeclMutualGroup ((name, ty) :: members, conjuncts))) afterWhere
                | _ ->
                    let line, col = currentPos afterMembers'
                    error "Mutually constrained type aliases require a 'where' clause" line col
    | _ ->
        let line, col = currentPos tokens
        error "Expected type name" line col

/// Parse field declaration: name : Type
let parseFieldDecl (tokens: Token list) : ParseResult<FieldDecl> =
    match peek tokens with
    | Some (TokIdent name) ->
        let afterName = advance tokens
        expect TokColon afterName >>= fun _ afterColon ->
        parseTypeExpr afterColon >>= fun tyRaw remaining ->
        // A bounded-primitive field type (`f: Int<min=a, max=b>`) normalizes
        // into the field-bound channel: the wrapper is stripped and the
        // bounds become a FieldBound with HiInclusive = true. That keeps the
        // struct's conjunct list the one representation both evaluation
        // worlds read (no second bounds channel to drift from it), and
        // leaves every consumer's test on the field's declared type
        // (Int-ness, unit resolution) working verbatim on both spellings.
        let ty, typeBound =
            match tyRaw with
            | TyBounded (baseTy, lo, hi) -> baseTy, Some { Lo = lo; Hi = hi; HiInclusive = true }
            | _ -> tyRaw, None
        // Optional dependent range refinement: `in lo .. hi`. Bounds parse at
        // the additive level (parseDotDot's own operand level) so they stop
        // cleanly at `..`, ',' and '}'. `in` never otherwise follows a field
        // type inside a struct body, so the postfix is unambiguous.
        match peek remaining with
        | Some (TokKeyword KwIn) when typeBound.IsSome ->
            let line, col = currentPos remaining
            error (sprintf "Field '%s' has two bound specifications: `min=`/`max=` on its type and `in lo .. hi`. Use one." name) line col
        | Some (TokKeyword KwIn) ->
            let afterIn = advance remaining
            let loR =
                match peek afterIn with
                | Some TokDotDot -> success None afterIn
                | _ -> parseAdditive afterIn >>= fun lo rest -> success (Some lo) rest
            loR >>= fun lo afterLo ->
            expect TokDotDot afterLo >>= fun _ afterDots ->
            let hiR =
                match peek afterDots with
                | Some TokComma | Some TokRBrace | Some TokNewline | None -> success None afterDots
                | _ -> parseAdditive afterDots >>= fun hi rest -> success (Some hi) rest
            hiR >>= fun hi afterHi ->
            if lo.IsNone && hi.IsNone then
                let line, col = currentPos afterIn
                error "Field bound needs at least one endpoint: `in lo .. hi`, `in lo ..`, or `in .. hi`" line col
            else
                success { Name = name; Type = ty; Default = None; Bound = Some { Lo = lo; Hi = hi; HiInclusive = false } } afterHi
        | _ ->
            success { Name = name; Type = ty; Default = None; Bound = typeBound } remaining
    | _ ->
        let line, col = currentPos tokens
        error "Expected field name" line col

/// Parse struct declaration: struct Name<T> { field1: T1, field2: T2 }.
/// `isStatic` is set by the `static struct` spelling: the declared
/// static-eligibility fence, checked at registration (every field type must
/// be statically evaluable). Otherwise the same declaration form.
let parseStructDeclWith (isStatic: bool) (tokens: Token list) : ParseResult<Decl> =
    match peek tokens with
    | Some (TokIdent name) ->
        let afterName = advance tokens
        let typeParams, afterParams = parseTypeParams afterName
        expect TokLBrace (skipNL afterParams) >>= fun _ afterBrace ->
        
        let rec loop fields toks =
            let toks = skipNL toks
            match peek toks with
            | Some TokRBrace -> success (List.rev fields) (advance toks)
            | _ ->
                parseFieldDecl toks >>= fun field afterField ->
                let afterField = skipNL afterField
                match peek afterField with
                | Some TokComma -> loop (field :: fields) (advance afterField)
                | Some TokRBrace -> success (List.rev (field :: fields)) (advance afterField)
                | _ -> 
                    let line, col = currentPos afterField
                    error "Expected ',' or '}' in struct" line col
        
        loop [] afterBrace >>= fun fields remaining ->
        // Parse optional where constraint: comma-separated conjuncts,
        // aligned with the function where-clause grammar.
        let remaining = skipNL remaining
        match peek remaining with
        | Some (TokKeyword KwWhere) ->
            parseConjuncts (advance remaining) >>= fun conjuncts afterConstraint ->
            success (DeclType (TyDeclStruct (name, typeParams, fields, conjuncts, isStatic))) afterConstraint
        | _ ->
            success (DeclType (TyDeclStruct (name, typeParams, fields, [], isStatic))) remaining
    | _ ->
        let line, col = currentPos tokens
        error "Expected struct name" line col

let parseStructDecl (tokens: Token list) : ParseResult<Decl> = parseStructDeclWith false tokens

/// Parse function signature: function name(parameters) -> RetType
let parseFunctionSig (tokens: Token list) : ParseResult<FunctionSig> =
    expect (TokKeyword KwFunction) tokens >>= fun _ afterKw ->
    match peek afterKw with
    | Some (TokIdent name) ->
        let afterName = advance afterKw
        expect TokLParen afterName >>= fun _ afterLParen ->
        sepBy parseParamDecl TokComma afterLParen >>= fun parms afterParms ->
        expect TokRParen afterParms >>= fun _ afterRParen ->

        match peek afterRParen with
        | Some (TokOp "->") ->
            parseTypeExpr (advance afterRParen) >>= fun retType remaining ->
            success { Name = name; Params = parms; ReturnType = retType } remaining
        | _ ->
            // No arrow: defaults to Unit return type.
            success { Name = name; Params = parms; ReturnType = TyUnit } afterRParen
    | _ ->
        let line, col = currentPos afterKw
        error "Expected function name" line col

/// Parse interface declaration: interface Name<T> { function sig1; function sig2 }
let parseInterfaceDecl (tokens: Token list) : ParseResult<Decl> =
    match peek tokens with
    | Some (TokIdent name) ->
        let afterName = advance tokens
        let typeParams, afterParams = parseTypeParams afterName
        expect TokLBrace (skipNL afterParams) >>= fun _ afterBrace ->
        
        let rec loop methods toks =
            let toks = skipNL toks
            match peek toks with
            | Some TokRBrace -> success (List.rev methods) (advance toks)
            | Some (TokKeyword KwFunction) ->
                parseFunctionSig toks >>= fun meth afterMeth ->
                loop (meth :: methods) (skipNL afterMeth)
            | _ ->
                let line, col = currentPos toks
                error "Expected 'function' or '}' in interface" line col
        
        loop [] afterBrace >>= fun methods remaining ->
        success (DeclInterface { Name = name; TypeParams = typeParams; Methods = methods }) remaining
    | _ ->
        let line, col = currentPos tokens
        error "Expected interface name" line col

/// Parse impl declaration: impl Interface for Type { methods }
let parseImplDecl (tokens: Token list) : ParseResult<Decl> =
    match peek tokens with
    | Some (TokIdent ifaceName) ->
        let afterIface = advance tokens
        expect (TokKeyword KwFor) afterIface >>= fun _ afterFor ->
        parseTypeExpr afterFor >>= fun forType afterType ->
        expect TokLBrace (skipNL afterType) >>= fun _ afterBrace ->
        
        let rec loop methods toks =
            let toks = skipNL toks
            match peek toks with
            | Some TokRBrace -> success (List.rev methods) (advance toks)
            | Some (TokKeyword KwFunction) ->
                parseFunctionDecl (advance toks) >>= fun decl afterDecl ->
                match decl with
                | DeclFunction f -> loop (f :: methods) afterDecl
                | _ -> 
                    let line, col = currentPos toks
                    error "Expected function in impl block" line col
            | _ ->
                let line, col = currentPos toks
                error "Expected 'function' or '}' in impl block" line col
        
        loop [] afterBrace >>= fun methods remaining ->
        success (DeclImpl { Interface = ifaceName; ForType = forType; Methods = methods }) remaining
    | _ ->
        let line, col = currentPos tokens
        error "Expected interface name after 'impl'" line col

/// Parse qualified name: A.B.C
let parseQualifiedName (tokens: Token list) : ParseResult<QualifiedName> =
    match peek tokens with
    | Some (TokIdent first) ->
        let rec loop parts toks =
            match peek toks with
            | Some TokDot ->
                let afterDot = advance toks
                match peek afterDot with
                | Some (TokIdent part) -> loop (part :: parts) (advance afterDot)
                | _ -> success (List.rev parts) toks
            | _ -> success (List.rev parts) toks
        loop [first] (advance tokens)
    | _ ->
        let line, col = currentPos tokens
        error "Expected module name" line col

// Unit of Measure Declarations
//
// (The unit-expression grammar itself -- parseUnitExpr and friends -- now
// lives ahead of the parseTypeExpr group, because compound type arguments
// like `Float<meter/second^2>` route into it via parseTypeArg.)

/// Parse a unit declaration:
///   Unit meters                       — base dimension
///   Unit velocity = meters / seconds  — structural alias (canonicalized, name discarded)
///   Unit speed: mps                   — QUANTITY: nominal identity entailing the RHS dims
let parseUnitDecl (tokens: Token list) : ParseResult<Decl> =
    expectIdent tokens >>= fun name afterName ->
    match peek afterName with
    | Some (TokOp "=") ->
        parseUnitExpr (advance afterName) >>= fun expr remaining ->
        success (DeclUnit { Name = name; Definition = Some (UnitDerived expr) }) remaining
    | Some TokColon ->
        parseUnitExpr (advance afterName) >>= fun expr remaining ->
        success (DeclUnit { Name = name; Definition = Some (UnitQuantity expr) }) remaining
    | _ ->
        success (DeclUnit { Name = name; Definition = None }) afterName

let parseDecl (tokens: Token list) : ParseResult<Decl> =
    match peek tokens with
    | Some (TokKeyword KwImport) ->
        // import Providers.NetCDF [as NetCDF]
        parseQualifiedName (advance tokens) >>= fun qname afterName ->
        match peek afterName with
        | Some (TokKeyword KwAs) ->
            expectIdent (advance afterName) >>= fun alias remaining ->
            success (DeclImport (qname, ImportQualified (Some alias))) remaining
        | _ ->
            // import Providers.NetCDF  (no alias: use last segment)
            success (DeclImport (qname, ImportQualified None)) afterName
    | Some (TokKeyword KwFrom) ->
        // from Math import pi, e
        parseQualifiedName (advance tokens) >>= fun qname afterName ->
        match peek afterName with
        | Some (TokKeyword KwImport) ->
            let rec parseNames acc toks =
                match expectIdent toks with
                | Ok (name, rest) ->
                    match peek rest with
                    | Some TokComma -> parseNames (name :: acc) (advance rest)
                    | _ -> success (List.rev (name :: acc)) rest
                | Error e -> error e.Message e.Line e.Col
            parseNames [] (advance afterName) >>= fun names remaining ->
            success (DeclImport (qname, ImportSelective names)) remaining
        | _ ->
            let line, col = currentPos afterName
            error "Expected 'import' after 'from <module>'" line col
    | Some (TokKeyword KwFunction) ->
        parseFunctionDecl (advance tokens)
    | Some (TokKeyword KwLet) ->
        let afterLet = advance tokens
        match peek afterLet with
        | Some (TokKeyword KwStatic) ->
            // let static x = ...  ->  DeclStatic
            parseTopLevelLet (advance afterLet) >>= fun decl remaining ->
            match decl with
            | DeclLet binding ->
                success (DeclStatic { binding with Mutability = BindConst }) remaining
            | other -> success other remaining
        | _ ->
            parseTopLevelLet afterLet
    | Some (TokKeyword KwStatic) ->
        let afterStatic = advance tokens
        match peek afterStatic with
        | Some (TokKeyword KwFunction) ->
            parseFunctionDecl (advance afterStatic) >>= fun decl remaining ->
            match decl with
            | DeclFunction f ->
                success (DeclFunction { f with IsStatic = true }) remaining
            | other -> success other remaining
        | Some (TokKeyword KwStruct) ->
            // static struct S { ... }: the declared static-eligibility fence.
            parseStructDeclWith true (advance afterStatic)
        | _ ->
            let (line, col) = currentPos afterStatic
            error "Expected 'function' or 'struct' after 'static'. For static values, use 'let static x = ...'" line col
    | Some (TokKeyword KwType) ->
        parseTypeDecl (advance tokens)
    | Some (TokKeyword KwStruct) ->
        parseStructDecl (advance tokens)
    | Some (TokKeyword KwInterface) ->
        parseInterfaceDecl (advance tokens)
    | Some (TokKeyword KwImpl) ->
        parseImplDecl (advance tokens)
    | Some (TokKeyword KwUnit) ->
        parseUnitDecl (advance tokens)
    | Some kind ->
        let line, col = currentPos tokens
        error (sprintf "Expected declaration but got %s" (describeToken kind)) line col
    | None ->
        errorEof "Expected declaration but got end of file"

// Module and Program Parsing

/// Skip tokens until we find a declaration-starting keyword or EOF.
/// Used for parser error recovery.
let rec skipToNextDecl (tokens: Token list) : Token list =
    let tokens = skipNL tokens
    match peek tokens with
    | Some TokEOF | None -> tokens
    | Some (TokKeyword KwLet) | Some (TokKeyword KwFunction) | Some (TokKeyword KwType)
    | Some (TokKeyword KwStruct) | Some (TokKeyword KwInterface) | Some (TokKeyword KwImpl)
    | Some (TokKeyword KwUnit) | Some (TokKeyword KwImport) | Some (TokKeyword KwFrom)
    | Some (TokKeyword KwStatic) | Some (TokKeyword KwModule) ->
        tokens
    | _ -> skipToNextDecl (advance tokens)

/// Parse a module, accumulating errors and recovering at declaration boundaries.
/// Returns the module (with successfully parsed declarations) and any parse errors.
let parseModuleRecovering (tokens: Token list) : (ModuleDecl * ParseError list) * Token list =
    setEofFrom tokens
    let tokens = skipNL tokens

    let moduleName, afterModule =
        match peek tokens with
        | Some (TokKeyword KwModule) ->
            match parseQualifiedName (advance tokens) with
            | Ok (name, rest) -> (name, skipNL rest)
            | Error _ -> (["Main"], tokens)
        | _ -> (["Main"], tokens)

    let mutable decls = []
    let mutable errors = []
    let mutable toks = afterModule
    
    let mutable cont = true
    while cont do
        toks <- skipNL toks
        match peek toks with
        | Some TokEOF | None ->
            cont <- false
        | _ ->
            let (startLine, startCol) = currentPos toks
            match parseDecl toks with
            | Ok (decl, remaining) ->
                let (endLine, endCol) = consumedEnd toks remaining startLine startCol
                let span = { StartLine = startLine; StartCol = startCol
                             EndLine = endLine; EndCol = endCol; File = currentFile }
                let located = { Value = decl; Span = span }
                decls <- located :: decls
                toks <- remaining
            | Error e ->
                errors <- e :: errors
                toks <- skipToNextDecl (advance toks)
    
    let modul = { Name = moduleName; Imports = []; Decls = List.rev decls }
    ((modul, List.rev errors), toks)

/// Non-recovering counterpart to parseModuleRecovering: fails on the first parse error.
let parseModule (tokens: Token list) : ParseResult<ModuleDecl> =
    setEofFrom tokens
    let tokens = skipNL tokens

    let moduleName, afterModule =
        match peek tokens with
        | Some (TokKeyword KwModule) ->
            match parseQualifiedName (advance tokens) with
            | Ok (name, rest) -> (name, skipNL rest)
            | Error _ -> (["Main"], tokens)
        | _ -> (["Main"], tokens)
    
    let rec loop decls toks =
        let toks = skipNL toks
        match peek toks with
        | Some TokEOF | None ->
            success (List.rev decls) toks
        | _ ->
            let (startLine, startCol) = currentPos toks
            parseDecl toks >>= fun decl remaining ->
            let (endLine, endCol) = consumedEnd toks remaining startLine startCol
            let span = { StartLine = startLine; StartCol = startCol
                         EndLine = endLine; EndCol = endCol; File = currentFile }
            let located = { Value = decl; Span = span }
            loop (located :: decls) remaining
    
    loop [] afterModule >>= fun decls remaining ->
    success {
        Name = moduleName
        Imports = []
        Decls = decls
    } remaining

/// Parse a single source, stamping the given file into every span it builds.
let parseProgramWithFile (file: string option) (source: string) : Result<Program, ParseError> =
    currentFile <- file
    try
        let tokens = tokenizeWithNewlines source
        setEofFrom tokens
        match parseModule tokens with
        | Ok (modul, _) -> Ok { Modules = [modul] }
        | Error e -> Error e
    finally
        currentFile <- None

/// Backward-compatible entry point: parse a single anonymous source (File=None).
let parseProgram (source: string) : Result<Program, ParseError> =
    parseProgramWithFile None source

/// Parse multiple source files into a single Program.
/// Each entry is (fileName, sourceCode). If a source has a `module` declaration,
/// that name is used; otherwise the fileName (sans extension) becomes the module name.
let parseMultiSource (sources: (string * string) list) : Result<Program, ParseError> =
    let rec go acc remaining =
        match remaining with
        | [] -> currentFile <- None; Ok { Modules = List.rev acc }
        | (fileName, source) :: rest ->
            // Stamp this file into every span the parser builds for it.
            currentFile <- (if fileName <> "" then Some fileName else None)
            let tokens = tokenizeWithNewlines source
            setEofFrom tokens
            match parseModule tokens with
            | Ok (modul, _) ->
                // If module name is "Main" (default) and fileName is provided, use fileName
                let modul' =
                    if modul.Name = ["Main"] && fileName <> "" && fileName <> "Main" then
                        { modul with Name = [fileName] }
                    else modul
                go (modul' :: acc) rest
            | Error e ->
                currentFile <- None
                Error { e with Message = sprintf "[%s] %s" fileName e.Message }
    go [] sources

// Initialize Forward Reference

do parseExprRef := parseExprImpl
do parseBodyRef := parseInlineOrBlock
