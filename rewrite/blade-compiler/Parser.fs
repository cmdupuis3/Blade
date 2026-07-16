// Blade-DSL Parser
// Parser with proper precedence and error handling

module Blade.Parser

open Blade.Ast
open Blade.Lexer

// ============================================================================
// Parser Types
// ============================================================================

type ParseError = {
    Message: string
    Line: int
    Col: int
}

type ParseResult<'T> = Result<'T * Token list, ParseError>

// ============================================================================
// Basic Combinators
// ============================================================================

let success value remaining : ParseResult<'T> = Ok (value, remaining)
let error msg line col : ParseResult<'T> = Error { Message = msg; Line = line; Col = col }

let currentPos (tokens: Token list) =
    match tokens with
    | t :: _ -> t.Line, t.Col
    | [] -> 0, 0

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

/// Peek past newlines, but only if the next non-newline token is a combinator operator.
/// Returns the peeked token kind and the token list with newlines skipped.
/// If the next non-newline token is NOT a combinator, returns the original stream unchanged.
let peekContinuation (tokens: Token list) : TokenKind option * Token list =
    let skipped = skipNL tokens
    match skipped with
    | t :: _ when isCombinatorOp t.Kind -> (Some t.Kind, skipped)
    | _ -> (peek tokens, tokens)

let expect kind (tokens: Token list) : ParseResult<Token> =
    match tokens with
    | t :: rest when t.Kind = kind -> Ok (t, rest)
    | t :: _ -> error (sprintf "Expected %A but got %A" kind t.Kind) t.Line t.Col
    | [] -> error (sprintf "Expected %A but got EOF" kind) 0 0

let expectIdent (tokens: Token list) : ParseResult<string> =
    match tokens with
    | t :: rest ->
        match t.Kind with
        | TokIdent name -> Ok (name, rest)
        | _ -> error (sprintf "Expected identifier but got %A" t.Kind) t.Line t.Col
    | [] -> error "Expected identifier but got EOF" 0 0

/// Expect a closing > for type parameters.
/// Handles >> (compose token) by splitting: consume one > and leave one >.
/// This is the standard approach used by Rust, Java 7+, and C# to resolve
/// the ambiguity between >> (shift/compose) and >> (two type closes).
let expectGt (tokens: Token list) : ParseResult<unit> =
    match tokens with
    | t :: rest when t.Kind = TokOp ">" ->
        Ok ((), rest)
    | t :: rest when t.Kind = TokOp ">>" ->
        // Split >>: consume first >, leave second > with adjusted position
        let remainingGt = { t with Kind = TokOp ">"; Col = t.Col + 1; Length = 1 }
        Ok ((), remainingGt :: rest)
    | t :: _ -> error (sprintf "Expected '>' but got %A" t.Kind) t.Line t.Col
    | [] -> error "Expected '>' but got EOF" 0 0

// Bind operator for chaining parsers
let (>>=) (result: ParseResult<'a>) (f: 'a -> Token list -> ParseResult<'b>) : ParseResult<'b> =
    match result with
    | Ok (v, rest) -> f v rest
    | Error e -> Error e

// Forward reference for body parser (inline or block)
let parseBodyRef : (Token list -> ParseResult<Expr>) ref = ref (fun _ -> Error { Message = "Not initialized"; Line = 0; Col = 0 })
let parseBody tokens = !parseBodyRef tokens

// ============================================================================
// Active Patterns for Token Classification (sorted by precedence)
// ============================================================================

// Literal tokens
let (|LiteralTok|_|) = function
    | TokInt v -> Some (LitInt v)
    | TokFloat v -> Some (LitFloat v)
    | TokBool v -> Some (LitBool v)
    | TokString v -> Some (LitString v)
    | TokChar v -> Some (LitChar v)
    | _ -> None

// Pipeline operators (lowest precedence combinators)
let (|PipelineOp|_|) = function
    | TokOp "|>" -> Some ()
    | _ -> None

// Choice/Alternative combinators
let (|ChoiceOp|_|) = function
    | TokOp "<|>" -> Some OpChoice
    | TokOp "<|:>" -> Some OpFallback
    | _ -> None

// Parallel combinators
let (|ParallelOp|_|) = function
    | TokOp "<&>" -> Some OpParallel
    | TokOp "<&!>" -> Some OpFusion
    | _ -> None

// Bind/Compose combinators
let (|BindOp|_|) = function
    | TokOp ">>=" -> Some OpBind
    | TokOp ">>@" -> Some OpComposeObj
    | TokOp "@>>" -> Some OpComposeMeth
    | _ -> None

// Apply/Functor combinators
let (|ApplyOp|_|) = function
    | TokOp "<@>" -> Some OpApply
    | TokOp "<$>" -> Some OpFunctor
    | _ -> None

// Array product combinator
let (|ArrayProductOp|_|) = function
    | TokOp "<*>" -> Some OpArrayProd
    | _ -> None

// Logical Or - returns (mode, op)
let (|OrOp|_|) = function
    | TokOp "||" -> Some (Elementwise, OpOr)
    | TokOp "[||]" -> Some (Outer, OpOr)
    | _ -> None

// Logical And - returns (mode, op)
let (|AndOp|_|) = function
    | TokOp "&&" -> Some (Elementwise, OpAnd)
    | TokOp "[&&]" -> Some (Outer, OpAnd)
    | _ -> None

// Equality operators - returns (mode, op)
let (|EqualityOp|_|) = function
    | TokOp "==" -> Some (Elementwise, OpEq)
    | TokOp "!=" -> Some (Elementwise, OpNeq)
    | TokOp "[==]" -> Some (Outer, OpEq)
    | TokOp "[!=]" -> Some (Outer, OpNeq)
    | _ -> None

// Comparison operators - returns (mode, op)
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

// Additive operators - returns (mode, op)
let (|AdditiveOp|_|) = function
    | TokOp "+" -> Some (Elementwise, OpAdd)
    | TokOp "-" -> Some (Elementwise, OpSub)
    | TokOp "[+]" -> Some (Outer, OpAdd)
    | TokOp "[-]" -> Some (Outer, OpSub)
    | _ -> None

// Multiplicative operators - returns (mode, op)
let (|MultiplicativeOp|_|) = function
    | TokOp "*" -> Some (Elementwise, OpMul)
    | TokOp "/" -> Some (Elementwise, OpDiv)
    | TokOp "%" -> Some (Elementwise, OpMod)
    | TokOp "[*]" -> Some (Outer, OpMul)
    | TokOp "[/]" -> Some (Outer, OpDiv)
    | TokOp "[%]" -> Some (Outer, OpMod)
    | _ -> None

// Power operator - returns (mode, op)
let (|PowerOp|_|) = function
    | TokOp "^" -> Some (Elementwise, OpCaret)
    | TokOp "[^]" -> Some (Outer, OpCaret)
    | _ -> None

// Unary operators
let (|UnaryOp|_|) = function
    | TokOp "-" -> Some OpNeg
    | TokOp "!" -> Some OpNot
    | _ -> None

// ============================================================================
// Helper Combinators  
// ============================================================================

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

let sepBy parser sep (tokens: Token list) =
    match parser tokens with
    | Error _ -> Ok ([], tokens)
    | Ok (first, rest) ->
        let rec loop acc toks =
            match expect sep toks with
            | Error _ -> Ok (List.rev acc, toks)
            | Ok (_, afterSep) ->
                match parser afterSep with
                | Ok (v, rest') -> loop (v :: acc) rest'
                | Error _ -> Ok (List.rev acc, toks)
        loop [first] rest

// ============================================================================
// Forward Reference for Expression Parser
// ============================================================================

// The only forward reference we need - expressions can be nested
let parseExprRef : (Token list -> ParseResult<Expr>) ref = 
    ref (fun _ -> failwith "parseExpr not initialized")

let parseExpr tokens = !parseExprRef tokens

// ============================================================================
// Simple Expression Parser for Type Arguments
// Stops at > and , - used inside type angle brackets
// Has proper precedence: multiplicative binds tighter than additive
// ============================================================================

/// FOR-IN RANGE HEADER CONTEXT. A `for x in RANGE { body }` statement's
/// range expression is always followed by the loop-body brace, so a bare
/// identifier meeting `{` there is the START OF THE BODY, not a struct
/// literal — without this, `for k in 0..n {` misparsed as struct
/// construction `n { ... }` (any identifier bound before a block).
/// The flag suppresses struct-literal parsing at the top nesting level of
/// the range expression only; delimited sub-expressions (parens, call
/// arguments, index brackets) restore it, so `for p in f(Point { x = 1 })`
/// stays expressible. This also hedges against future anonymous-struct
/// syntax: any brace-headed literal form would inherit the same ambiguity
/// and the same context rule. AsyncLocal mirrors the compiler's other
/// context cells (parse-parallel safety).
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
            loop (ExprBinOp (Elementwise, OpAdd, acc, right)) remaining
        | Some (TokOp "-") ->
            advance toks |> parseSimpleMultiplicative >>= fun right remaining ->
            loop (ExprBinOp (Elementwise, OpSub, acc, right)) remaining
        | _ -> success acc toks
    loop left rest

and parseSimpleMultiplicative (tokens: Token list) : ParseResult<Expr> =
    parseSimplePrimary tokens >>= fun left rest ->
    let rec loop acc toks =
        match peek toks with
        | Some (TokOp "*") ->
            advance toks |> parseSimplePrimary >>= fun right remaining ->
            loop (ExprBinOp (Elementwise, OpMul, acc, right)) remaining
        | Some (TokOp "/") ->
            advance toks |> parseSimplePrimary >>= fun right remaining ->
            loop (ExprBinOp (Elementwise, OpDiv, acc, right)) remaining
        | _ -> success acc toks
    loop left rest

and parseSimplePrimary (tokens: Token list) : ParseResult<Expr> =
    match peek tokens with
    | Some (LiteralTok lit) -> success (ExprLit lit) (advance tokens)
    | Some (TokIdent name) -> success (ExprVar name) (advance tokens)
    | Some TokLParen ->
        // Parenthesized simple expression OR tuple of simple expressions.
        // The tuple form serves static index-type arguments whose payload is
        // tuple-structured (e.g. IrrepsIdx<[(0, 0, 2), (1, 1, 2)]>). A comma
        // here was previously a parse error, so accepting tuples changes no
        // existing program's meaning.
        advance tokens |> sepBy parseSimpleExpr TokComma >>= fun exprs afterExprs ->
        expect TokRParen afterExprs >>= fun _ remaining ->
        match exprs with
        | [] ->
            let line, col = currentPos tokens
            error "Expected simple expression inside parentheses" line col
        | [single] -> success single remaining
        | many -> success (ExprTuple many) remaining
    | Some TokLBracket ->
        // Array literal inside simple expression context (e.g., EnumIdx<[1, 2, 3]>)
        advance tokens |> sepBy parseSimpleExpr TokComma >>= fun elems afterElems ->
        expect TokRBracket afterElems >>= fun _ remaining ->
        success (ExprArrayLit elems) remaining
    | Some kind ->
        let line, col = currentPos tokens
        error (sprintf "Expected simple expression but got %A" kind) line col
    | None ->
        error "Expected expression but got EOF" 0 0

// ============================================================================
// Literal Parsing
// ============================================================================

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
    | [] -> error "Expected literal but got EOF" 0 0

// ============================================================================
// Type Expression Parsing
// ============================================================================

/// Parse a rank expression (for T^r syntax)
/// Can be: integer literal, arity keyword, or simple identifier
let parseRankExpr (tokens: Token list) : ParseResult<Expr> =
    match peek tokens with
    | Some (TokInt n) -> 
        success (ExprLit (LitInt n)) (advance tokens)
    | Some (TokKeyword KwArity) -> 
        let afterArity = advance tokens
        match peek afterArity with
        | Some TokLParen ->
            advance afterArity |> fun afterLParen ->
            match peek afterLParen with
            | Some (TokIdent paramName) ->
                advance afterLParen |> expect TokRParen >>= fun _ remaining ->
                success (ExprArity paramName) remaining
            | _ ->
                let line, col = currentPos afterLParen
                error "Expected parameter name in arity()" line col
        | _ ->
            let line, col = currentPos afterArity
            error "arity requires parameter name: arity(paramName)" line col
    | Some (TokIdent name) -> 
        success (ExprVar name) (advance tokens)
    | Some t ->
        let line, col = currentPos tokens
        error (sprintf "Expected rank expression (integer, arity, or identifier), got %A" t) line col
    | None ->
        error "Expected rank expression but got EOF" 0 0

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
    
    | Some (TokKeyword KwEnumIdx) ->
        parseIndexType tokens
    
    | Some (TokKeyword KwDepIdx) ->
        // DepIdx in standalone position (alias body, fn return, etc).
        // Inside `Array<T like ...>` this is reached via sepBy parseIndexType
        // directly; this dispatch makes the type writable everywhere.
        parseIndexType tokens
    
    | Some (TokKeyword KwRaggedIdx) ->
        // RaggedIdx in standalone position. Same rationale as KwDepIdx above.
        parseIndexType tokens

    | Some (TokKeyword KwIrrepsIdx) ->
        // IrrepsIdx in standalone position. Same rationale as KwDepIdx above.
        parseIndexType tokens
    
    | Some (TokKeyword KwVoid) ->
        // Void type (the unit type — no value)
        success (TyNamed ("Void", [])) (advance tokens)
    
    | Some TokLParen ->
        // Tuple type or parenthesized
        advance tokens |> sepBy parseTypeExpr TokComma >>= fun types afterTypes ->
        expect TokRParen afterTypes >>= fun _ remaining ->
        match types with
        | [single] -> success single remaining
        | _ -> success (TyTuple types) remaining
    
    | Some (TokIdent "Dist") when (match peek (advance tokens) with Some (TokOp "<") -> true | _ -> false) ->
        // Dist<order, Elem like I1, ..., Ik> — the typed dist tower
        // (ppl/NOTES.md). Order-first is deliberate: the order is an
        // EXPRESSION (any statically-evaluable int, per the replicate-count
        // contract), so leading with it keeps the parse unambiguous; the
        // rest is exactly Array's `Elem like indices` inner syntax.
        // A bare `Dist` without `<` falls through to the TyNamed arm below.
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

    | Some (TokIdent name) ->
        let afterName = advance tokens
        match peek afterName with
        | Some (TokOp "^") ->
            // The caret is the syntactic marker for type variables.
            // T^0 = scalar type var, T^2 = rank-2 array type var, T^r = variable-rank
            advance afterName |> parseRankExpr >>= fun rankExpr remaining ->
            match rankExpr with
            | ExprLit (LitInt n) ->
                success (TyVar (name, Some (int n))) remaining
            | _ ->
                // Non-literal rank (T^r where r is a variable)
                success (TyAbstractArray (TyVar (name, None), rankExpr, None)) remaining
        | Some (TokOp "<") ->
            // Parameterized type: Array<T>, MyStruct<Int>, etc.
            advance afterName |> sepBy parseTypeExpr TokComma >>= fun args afterArgs ->
            expectGt afterArgs >>= fun _ remaining ->
            success (TyNamed (name, args)) remaining
        | _ ->
            // Bare name without caret: always a named type / type constructor
            success (TyNamed (name, [])) afterName
    
    | Some kind ->
        let line, col = currentPos tokens
        error (sprintf "Unexpected token in type: %A" kind) line col
    
    | None ->
        error "Expected type but got EOF" 0 0

// Parse index types specifically - Idx<extent> or SymIdx<arity, extent>
// These are self-contained with their own < > brackets
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
        parseSimpleExpr afterComma >>= fun extent afterExtent ->
        expectGt afterExtent >>= fun _ remaining ->
        let rank = match rankLit with LitInt n -> int n | _ -> 2
        success (TySymIdx (rank, extent)) remaining
    
    | Some (TokKeyword KwAntisymIdx) ->
        advance tokens |> expect (TokOp "<") >>= fun _ afterLt ->
        parseLiteral afterLt >>= fun rankLit afterRank ->
        expect TokComma afterRank >>= fun _ afterComma ->
        parseSimpleExpr afterComma >>= fun extent afterExtent ->
        expectGt afterExtent >>= fun _ remaining ->
        let rank = match rankLit with LitInt n -> int n | _ -> 2
        success (TyAntisymIdx (rank, extent)) remaining
    
    | Some (TokKeyword KwHermitianIdx) ->
        advance tokens |> expect (TokOp "<") >>= fun _ afterLt ->
        parseSimpleExpr afterLt >>= fun extent afterExtent ->
        expectGt afterExtent >>= fun _ remaining ->
        success (TyHermitianIdx extent) remaining

    // CompoundIdx<mask> -- masked product space (formalism 4.5). The mask is a
    // runtime array expression; its rank determines the compound's arity, so the
    // surface form carries only the mask (the per-dimension extents and arity are
    // recovered from the mask's type at lowering).
    | Some (TokKeyword KwCompoundIdx) ->
        advance tokens |> expect (TokOp "<") >>= fun _ afterLt ->
        parseSimpleExpr afterLt >>= fun mask afterMask ->
        expectGt afterMask >>= fun _ remaining ->
        success (TyCompoundIdx mask) remaining
    
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
        // The body position can be either `lambda(i) -> Idx<...>` or a bare
        // function name. We peek to decide.
        match peek afterComma with
        | Some (TokKeyword KwLambda) ->
            // Lambda form. Parse `lambda(name) -> idxBody`. Body is an index
            // type expression, not a general type expression.
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
            // Eta-reduced form: `DepIdx<outer, func>`.
            // Desugar to `DepIdx<outer, lambda(__d_i) -> func(__d_i)>` by
            // synthesizing a body that is the named function applied to the
            // parameter. We use a fresh parameter name to avoid collisions.
            // Note: the body produced here is itself a TypeExpr — but the
            // result of `func(i)` is conceptually an index type, not a value.
            // We represent it as TyNamed(func, [paramRef]) so lowering can
            // resolve it through the type-def lookup path.
            let afterName = advance afterComma
            expectGt afterName >>= fun _ remaining ->
            let paramName = "__d_i"
            // Synthesized body: TyNamed referring to a function-valued type.
            // The lowering layer will need to recognize and reduce this; for
            // Round 1 scaffolding, we emit it as a TyNamed with a fresh
            // expr-level reference and let lowering produce a placeholder
            // dynamic extent. Real evaluation lands in Round 2.
            let bodyTy = TyNamed (funcName, [TyNamed (paramName, [])])
            success (TyDepIdx (outer, paramName, bodyTy)) remaining
        | _ ->
            let line, col = currentPos afterComma
            error "DepIdx: expected lambda or function name as second argument" line col

    // RaggedIdx<lengths> — externally parameterized via a lengths array.
    // RaggedIdx<_>      — opaque-extent variant. Used in kernel-parameter
    // types (`lambda(g: Array<T like RaggedIdx<_>>) -> ...`) where the
    // extent is supplied by the peel context, not declared up front.
    //
    // Context-sensitivity: `_` only means "opaque extent" when it's the SOLE
    // argument to RaggedIdx — i.e., immediately followed by the closing `>`
    // (or `>>`, which expectGt splits). Any other position (`_ + 1`, `_, x`,
    // a leading `_` in an identifier, etc.) is left for the lengths-expr
    // parser to handle. This conservative two-token lookahead prevents the
    // wildcard from accidentally swallowing a piece of a real expression.
    | Some (TokKeyword KwRaggedIdx) ->
        advance tokens |> expect (TokOp "<") >>= fun _ afterLt ->
        // Two-token lookahead: TokUnderscore immediately followed by `>` or `>>`.
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

    // IrrepsIdx<spec> — spec is a static-expression argument: a `let static`
    // name or an inline array-of-triples literal ([(l, parity, mult), ...]).
    // Resolved at typecheck via StaticEval; call syntax (IrrepsIdx<sh_spec(2)>)
    // is not part of the simple-expression grammar — bind a `let static` first.
    | Some (TokKeyword KwIrrepsIdx) ->
        advance tokens |> expect (TokOp "<") >>= fun _ afterLt ->
        parseSimpleExpr afterLt >>= fun specExpr afterSpec ->
        expectGt afterSpec >>= fun _ remaining ->
        success (TyIrrepsIdx specExpr) remaining

    | Some (TokIdent name) ->
        // Named index type alias (e.g. type RegionIdx = Idx<3>; ...like RegionIdx).
        // Resolved at typecheck via lowerIndexType / TyNamed lookup against
        // TDIIndexType or TDIEnumIdx.
        let afterName = advance tokens
        match peek afterName with
        | Some (TokOp "<") ->
            // Parameterized named type: still acceptable here.
            advance afterName |> sepBy parseTypeExpr TokComma >>= fun args afterArgs ->
            expectGt afterArgs >>= fun _ remaining ->
            success (TyNamed (name, args)) remaining
        | _ ->
            success (TyNamed (name, [])) afterName
    
    | Some kind ->
        let line, col = currentPos tokens
        error (sprintf "Expected index type (Idx, SymIdx, AntisymIdx, HermitianIdx, EnumIdx, DepIdx, RaggedIdx, IrrepsIdx, or a named index type alias) but got %A" kind) line col
    
    | None ->
        error "Expected index type but got EOF" 0 0

// ============================================================================
// Pattern Parsing
// ============================================================================

let rec parsePattern (tokens: Token list) : ParseResult<Pattern> =
    parseAtomicPattern tokens >>= fun left rest ->
    match peek rest with
    | Some TokColonColon ->
        advance rest |> parsePattern >>= fun right remaining ->
        success (PatCons (left, right)) remaining
    | _ -> success left rest

and parseAtomicPattern (tokens: Token list) : ParseResult<Pattern> =
    match peek tokens with
    | Some TokUnderscore ->
        success PatWildcard (advance tokens)
    
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
            success (PatVariant (name, Some inner)) remaining
        | _ ->
            // Could be a variant without data (like None) or a variable
            // For now, treat as variable - variant detection happens at type checking
            success (PatVar name) afterName
    
    | Some (TokInt v) ->
        success (PatLit (LitInt v)) (advance tokens)
    
    | Some (TokBool v) ->
        success (PatLit (LitBool v)) (advance tokens)
    
    | Some (TokString v) ->
        success (PatLit (LitString v)) (advance tokens)
    
    | Some TokLParen ->
        advance tokens |> sepBy parsePattern TokComma >>= fun pats afterPats ->
        expect TokRParen afterPats >>= fun _ remaining ->
        match pats with
        | [] -> success (PatLit LitUnit) remaining
        | [single] -> success single remaining
        | _ -> success (PatTuple pats) remaining
    
    | Some kind ->
        let line, col = currentPos tokens
        error (sprintf "Unexpected token in pattern: %A" kind) line col
    
    | None ->
        error "Expected pattern but got EOF" 0 0

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
                // field: pattern
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
                success ((fieldName, PatVar fieldName) :: rest) remaining
            | Some TokRBrace ->
                // shorthand: field
                success [(fieldName, PatVar fieldName)] (advance afterFieldName)
            | _ ->
                let line, col = currentPos afterFieldName
                error "Expected ':' or ',' in struct pattern" line col
        | _ ->
            let line, col = currentPos toks
            error "Expected field name or '}' in struct pattern" line col
    
    parseFieldPats afterBrace >>= fun fields remaining ->
    success (PatStruct (name, fields)) remaining

// ============================================================================
// Expression Parsing - Precedence Climbing
// ============================================================================

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

// Helper for parsing comma-separated identifier lists (for where clauses)
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
                Error { Message = "Expected identifier"; Line = line; Col = col }
            else
                Ok (List.rev acc, toks)
    loop [] tokens

// Where clause parsing (used by both function declarations and lambdas)
/// Parse the body of omp(...): comma-separated `ident: int` pairs.
/// e.g. omp(a: 2, b: 1) => [("a",2); ("b",1)]
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
        | _ -> error (sprintf "Expected integer in omp(...) but got %A" t.Kind) t.Line t.Col
    | [] -> error "Expected integer in omp(...) but got EOF" 0 0

let parseWhereClause (tokens: Token list) : ParseResult<WhereClause> =
    // `par` accumulates parallelization strategy assignments as a LIST (see
    // WhereClause.Parallel), written OUTER backend first. Mixed parallelism
    // allows at most TWO strategies; the legal pairs are `mpi, omp(...)`
    // (rank decomposition outer, threads within each rank's slab/cell-range)
    // and `mpi, cuda(...)` (ranks outer, device kernels over each rank's
    // range). The order table is closed and purely syntactic, so every
    // illegal pair rejects HERE with steering; shape-dependent checks
    // (device eligibility, fold reassociation) stay in codegen.
    let rec loop comms (par: ParallelStrategy list) custom toks =
        let isOmp = function Omp _ -> true | _ -> false
        let isCuda = function Cuda _ -> true | _ -> false
        let isMpi = function Mpi -> true | _ -> false
        let rejectPair (line: int) (col: int) (incoming: string) =
            if List.length par >= 2 then
                error "At most two parallelization strategies per where-clause (outer, inner)" line col
            elif par |> List.exists isCuda then
                error "CUDA owns the whole device leaf — nothing nests inside a device kernel (write the cuda backend LAST: `where mpi, cuda(...)`)" line col
            else
                match incoming with
                | "omp" when par |> List.exists isOmp ->
                    error "omp requested twice — one omp(...) clause lists all parallel dims" line col
                | "mpi" when par |> List.exists isMpi ->
                    error "mpi requested twice" line col
                | "cuda" -> // par starts with Omp here (cuda/dup handled above)
                    error "A single kernel cannot be both OpenMP-host and CUDA-device — use `where cuda(...)` for the device kernel (omp-driven sections over independent cuda leaves are future work)" line col
                | "mpi" -> // par starts with Omp: the rejected OpenMP-outer/MPI-inner order
                    error "OpenMP-outer / MPI-inner is not supported: MPI ranks are processes fixed at launch and cannot nest inside host threads — did you mean `where mpi, omp(...)` (ranks outer, threads within each rank)?" line col
                | _ ->
                    error "Unsupported parallelization strategy combination" line col
        match peek toks with
        | Some (TokKeyword KwComm) ->
            advance toks |> expect TokLParen >>= fun _ afterLParen ->
            parseIdentList afterLParen >>= fun names afterNames ->
            expect TokRParen afterNames >>= fun _ remaining ->
            loop (names :: comms) par custom remaining
        | Some (TokKeyword KwOmp) ->
            // Legal as: sole strategy, or the INNER of `mpi, omp(...)`.
            if not (par = [] || par = [Mpi]) then
                let line, col = currentPos toks
                rejectPair line col "omp"
            else
                advance toks |> expect TokLParen >>= fun _ afterLParen ->
                parseOmpArgs [] afterLParen >>= fun pairs afterArgs ->
                expect TokRParen afterArgs >>= fun _ remaining ->
                loop comms (par @ [Omp { Vars = pairs }]) custom remaining
        | Some (TokKeyword KwMpi) ->
            // Legal only as the sole (and hence OUTER) strategy.
            if not (List.isEmpty par) then
                let line, col = currentPos toks
                rejectPair line col "mpi"
            else
                // bare `mpi` — rank count is supplied at launch (mpiexec -n N)
                loop comms (par @ [Mpi]) custom (advance toks)
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
                                loop comms (par @ [Cuda { BlockSize = int n }]) custom remaining
                            | _ -> error (sprintf "Expected integer block size but got %A" t.Kind) t.Line t.Col
                        | [] -> error "Expected integer block size but got EOF" 0 0
                | _ ->
                    // bare `cuda` => default block size
                    loop comms (par @ [Cuda { BlockSize = 256 }]) custom afterCuda
        | Some (TokIdent alias) when (match peek (advance toks) with Some TokDot -> true | _ -> false) ->
            // Module-qualified constraint conjunct: `<alias>.<name>(<idents>)`
            // (e.g. `p.indep(a, b)` with `import ppl as p`). Stored as DATA
            // with the dotted name ("p.indep", args); the owning module's
            // elaboration stage normalizes the alias, and the CHECKER
            // dispatches through the Blade.Constraints registry.
            advance (advance toks) |> expectIdent >>= fun name afterName ->
            expect TokLParen afterName >>= fun _ afterLParen ->
            parseIdentList afterLParen >>= fun args afterArgs ->
            expect TokRParen afterArgs >>= fun _ remaining ->
            loop comms par (custom @ [(alias + "." + name, args)]) remaining
        | Some (TokIdent name) when (match peek (advance toks) with Some TokLParen -> true | _ -> false) ->
            // Open constraint conjunct: `<name>(<idents>)` for any identifier
            // the grammar doesn't own. Parsed as DATA — (name, args) — and
            // dispatched by the CHECKER through the Blade.Constraints
            // registry; an unregistered name errors there with the
            // registered vocabulary, not here. (Module-owned keywords like
            // PPL's `indep` are qualified — see the dotted arm above.)
            advance toks |> expect TokLParen >>= fun _ afterLParen ->
            parseIdentList afterLParen >>= fun args afterArgs ->
            expect TokRParen afterArgs >>= fun _ remaining ->
            loop comms par (custom @ [(name, args)]) remaining
        | Some TokComma ->
            loop comms par custom (advance toks)
        | _ ->
            success {
                Commutativity = List.rev comms
                Parallel = par
                TDims = []
                Custom = custom
            } toks
    loop [] [] [] tokens

let rec parseExprImpl (tokens: Token list) : ParseResult<Expr> =
    parseAssignment tokens

/// Parse a body expression - either a block {...} or an inline expression
/// Inline expressions stop at newline (consumed) or other terminators
and parseInlineOrBlock (tokens: Token list) : ParseResult<Expr> =
    let tokens = skipNL tokens
    match peek tokens with
    | Some TokLBrace ->
        // Block scope: { ... }
        parseBlock (advance tokens)
    | _ ->
        // Inline scope: expression until newline
        parseExprImpl tokens >>= fun expr remaining ->
        // Consume trailing newline if present
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
        success (ExprAssign (left, right)) remaining
    // Compound assignment: desugar x += y to x = x + y
    | Some (TokOp "+=") ->
        advance rest |> parseAssignment >>= fun right remaining ->
        success (ExprAssign (left, ExprBinOp (Elementwise, OpAdd, left, right))) remaining
    | Some (TokOp "-=") ->
        advance rest |> parseAssignment >>= fun right remaining ->
        success (ExprAssign (left, ExprBinOp (Elementwise, OpSub, left, right))) remaining
    | Some (TokOp "*=") ->
        advance rest |> parseAssignment >>= fun right remaining ->
        success (ExprAssign (left, ExprBinOp (Elementwise, OpMul, left, right))) remaining
    | Some (TokOp "/=") ->
        advance rest |> parseAssignment >>= fun right remaining ->
        success (ExprAssign (left, ExprBinOp (Elementwise, OpDiv, left, right))) remaining
    | _ -> success left rest

/// Postfix type annotation: `expr : Type`
/// Sits between parseAssignment and parseNamedInfix in the precedence chain.
/// The cast binds tighter than `=` (so `x = e: T` parses as `x = (e: T)`)
/// but looser than every operator below it (so `a + b : Int` parses as
/// `(a + b) : Int`). The motivating use case is complex literal construction
/// — `(re, im) : Complex128` — but the form is general; TypeCheck applies
/// it to the surrounding expression and unifies the inferred type with
/// the annotation, with a special case for Complex that recognizes a
/// 2-tuple of float literals.
and parseTyped (tokens: Token list) : ParseResult<Expr> =
    parseNamedInfix tokens >>= fun expr rest ->
    match peek rest with
    | Some TokColon ->
        advance rest |> parseTypeExpr >>= fun ty remaining ->
        success (ExprTyped (expr, ty)) remaining
    | _ -> success expr rest

/// Named infix operators: a :name: b -> name(a, b)
/// Lowest precedence, left-associative
and parseNamedInfix (tokens: Token list) : ParseResult<Expr> =
    parsePipeline tokens >>= fun left rest ->
    let rec loop acc toks =
        match peek toks with
        | Some (TokNamedInfix name) ->
            advance toks |> parsePipeline >>= fun right remaining ->
            // Desugar :name: to function application: name(a, b)
            let call = ExprApp (ExprVar name, [acc; right])
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
            match right with
            | ExprVar "compute" -> loop (ExprCompute acc) remaining
            // Provider reads are module-qualified (`x |> alias.read`), so they
            // take the generic pipe-application desugar below; the checker's
            // provider-read arm rewrites the application to TExprRead.
            | _ -> loop (ExprApp (right, [acc])) remaining
        | Some (TokOp "|@>") ->
            // Pipe-apply: a |@> b  desugars to  b <@> a
            advance toks' |> parseChoice >>= fun right remaining ->
            loop (ExprBinOp (Elementwise, OpApply, right, acc)) remaining
        | _ -> success acc toks
    loop left rest

and parseChoice (tokens: Token list) : ParseResult<Expr> =
    parseParallel tokens >>= fun left rest ->
    let rec loop acc toks =
        let (peeked, toks') = peekContinuation toks
        match peeked with
        | Some (ChoiceOp op) ->
            advance toks' |> parseParallel >>= fun right remaining ->
            loop (ExprBinOp (Elementwise, op, acc, right)) remaining
        | _ -> success acc toks
    loop left rest

and parseParallel (tokens: Token list) : ParseResult<Expr> =
    parseBind tokens >>= fun left rest ->
    let rec loop acc toks =
        let (peeked, toks') = peekContinuation toks
        match peeked with
        | Some (ParallelOp op) ->
            advance toks' |> parseBind >>= fun right remaining ->
            loop (ExprBinOp (Elementwise, op, acc, right)) remaining
        | _ -> success acc toks
    loop left rest

and parseBind (tokens: Token list) : ParseResult<Expr> =
    parseApply tokens >>= fun left rest ->
    let rec loop acc toks =
        let (peeked, toks') = peekContinuation toks
        match peeked with
        | Some (BindOp op) ->
            advance toks' |> parseApply >>= fun right remaining ->
            loop (ExprBinOp (Elementwise, op, acc, right)) remaining
        // >> is now a single token from the lexer
        | Some (TokOp ">>") ->
            advance toks' |> parseApply >>= fun right remaining ->
            loop (ExprBinOp (Elementwise, OpCompose, acc, right)) remaining
        | _ -> success acc toks
    loop left rest

and parseApply (tokens: Token list) : ParseResult<Expr> =
    parseArrayProduct tokens >>= fun left rest ->
    let rec loop acc toks =
        let (peeked, toks') = peekContinuation toks
        match peeked with
        | Some (ApplyOp op) ->
            advance toks' |> parseArrayProduct >>= fun right remaining ->
            loop (ExprBinOp (Elementwise, op, acc, right)) remaining
        | _ -> success acc toks
    loop left rest

and parseArrayProduct (tokens: Token list) : ParseResult<Expr> =
    parseOr tokens >>= fun left rest ->
    let rec loop acc toks =
        let (peeked, toks') = peekContinuation toks
        match peeked with
        | Some (ArrayProductOp op) ->
            advance toks' |> parseOr >>= fun right remaining ->
            loop (ExprBinOp (Elementwise, op, acc, right)) remaining
        | _ -> success acc toks
    loop left rest

and parseOr (tokens: Token list) : ParseResult<Expr> =
    parseAnd tokens >>= fun left rest ->
    let rec loop acc toks =
        match peek toks with
        | Some (OrOp (mode, op)) ->
            advance toks |> parseAnd >>= fun right remaining ->
            loop (ExprBinOp (mode, op, acc, right)) remaining
        | _ -> success acc toks
    loop left rest

and parseAnd (tokens: Token list) : ParseResult<Expr> =
    parseEquality tokens >>= fun left rest ->
    let rec loop acc toks =
        match peek toks with
        | Some (AndOp (mode, op)) ->
            advance toks |> parseEquality >>= fun right remaining ->
            loop (ExprBinOp (mode, op, acc, right)) remaining
        | _ -> success acc toks
    loop left rest

and parseEquality (tokens: Token list) : ParseResult<Expr> =
    parseComparison tokens >>= fun left rest ->
    match peek rest with
    | Some (EqualityOp (mode, op)) ->
        advance rest |> parseComparison >>= fun right remaining ->
        success (ExprBinOp (mode, op, left, right)) remaining
    | _ -> success left rest

and parseComparison (tokens: Token list) : ParseResult<Expr> =
    parseCons tokens >>= fun left rest ->
    match peek rest with
    | Some (ComparisonOp (mode, op)) ->
        advance rest |> parseCons >>= fun right remaining ->
        success (ExprBinOp (mode, op, left, right)) remaining
    | _ -> success left rest

and parseCons (tokens: Token list) : ParseResult<Expr> =
    parseDotDot tokens >>= fun left rest ->
    match peek rest with
    | Some TokColonColon ->
        advance rest |> parseDotDot >>= fun right remaining ->
        success (ExprBinOp (Elementwise, OpCons, left, right)) remaining
    | _ -> success left rest

and parseDotDot (tokens: Token list) : ParseResult<Expr> =
    parseAdditive tokens >>= fun left rest ->
    match peek rest with
    | Some TokDotDot ->
        advance rest |> parseAdditive >>= fun right remaining ->
        success (ExprDotDot (left, right)) remaining
    | _ -> success left rest

and parseAdditive (tokens: Token list) : ParseResult<Expr> =
    parseMultiplicative tokens >>= fun left rest ->
    let rec loop acc toks =
        match peek toks with
        | Some (AdditiveOp (mode, op)) ->
            advance toks |> parseMultiplicative >>= fun right remaining ->
            loop (ExprBinOp (mode, op, acc, right)) remaining
        | _ -> success acc toks
    loop left rest

and parseMultiplicative (tokens: Token list) : ParseResult<Expr> =
    parsePower tokens >>= fun left rest ->
    let rec loop acc toks =
        match peek toks with
        | Some (MultiplicativeOp (mode, op)) ->
            advance toks |> parsePower >>= fun right remaining ->
            loop (ExprBinOp (mode, op, acc, right)) remaining
        | _ -> success acc toks
    loop left rest

and parsePower (tokens: Token list) : ParseResult<Expr> =
    parseUnary tokens >>= fun left rest ->
    match peek rest with
    | Some (PowerOp (mode, op)) ->
        advance rest |> parsePower >>= fun right remaining ->
        success (ExprBinOp (mode, op, left, right)) remaining
    | _ -> success left rest

and parseUnary (tokens: Token list) : ParseResult<Expr> =
    match peek tokens with
    | Some (UnaryOp op) ->
        advance tokens |> parseUnary >>= fun expr remaining ->
        success (ExprUnaryOp (op, expr)) remaining
    | _ -> parsePostfix tokens

/// Parse struct construction: Name { field1 = val1, field2 = val2 }
and parseStructExpr (name: string) (tokens: Token list) : ParseResult<Expr> =
    expect TokLBrace tokens >>= fun _ afterBrace ->
    
    let rec parseFieldInits toks =
        let toks = skipNL toks
        match peek toks with
        | Some TokRBrace -> success [] (advance toks)
        | Some (TokIdent fieldName) ->
            let afterFieldName = advance toks
            match peek afterFieldName with
            | Some (TokOp "=") ->
                // field = value
                parseExprImpl (advance afterFieldName) >>= fun value afterValue ->
                let afterValue = skipNL afterValue
                match peek afterValue with
                | Some TokComma ->
                    parseFieldInits (advance afterValue) >>= fun rest remaining ->
                    success ((fieldName, value) :: rest) remaining
                | Some TokRBrace ->
                    success [(fieldName, value)] (advance afterValue)
                | _ ->
                    let line, col = currentPos afterValue
                    error "Expected ',' or '}' in struct expression" line col
            | Some TokComma ->
                // shorthand: field (same as field = field)
                parseFieldInits (advance afterFieldName) >>= fun rest remaining ->
                success ((fieldName, ExprVar fieldName) :: rest) remaining
            | Some TokRBrace ->
                // shorthand: field (same as field = field)
                success [(fieldName, ExprVar fieldName)] (advance afterFieldName)
            | _ ->
                let line, col = currentPos afterFieldName
                error "Expected '=' or ',' in struct field" line col
        | _ ->
            let line, col = currentPos toks
            error "Expected field name or '}'" line col
    
    parseFieldInits afterBrace >>= fun fields remaining ->
    success (ExprStruct (name, fields)) remaining

and parsePostfix (tokens: Token list) : ParseResult<Expr> =
    parsePrimary tokens >>= fun left rest ->
    // A postfix `(` or `[` extends the previous expression only when it
    // opens on the line where that expression ends. The lexer strips
    // newline tokens inside delimiters (tokenizeWithNewlines), so inside a
    // block `let b = v` followed by a final tuple `(a, b)` on the next line
    // would otherwise parse as the call `v(a, b)` — reported as a baffling
    // unbound-variable error at the let. Line numbers survive the
    // stripping: compare the opener's line against the token before it
    // (the same-line rule Kotlin and Swift use). `.field` chains stay
    // line-insensitive.
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
            loop (ExprApp (acc, args)) remaining
        | Some TokLBracket when not (opensOnLaterLine (List.head toks)) ->
            // Poly-tuple indexing: args[k] (delimited: struct literals re-enabled)
            allowingStructLiterals (fun () -> advance toks |> parseExprImpl) >>= fun index afterIndex ->
            expect TokRBracket afterIndex >>= fun _ remaining ->
            loop (ExprTupleIndex (acc, index)) remaining
        | Some TokDot ->
            // Field access
            advance toks |> expectIdent >>= fun field remaining ->
            loop (ExprField (acc, field)) remaining
        | _ -> success acc toks
    loop left rest

and parsePrimary (tokens: Token list) : ParseResult<Expr> =
    match peek tokens with
    // Literals (most common case first)
    | Some (LiteralTok lit) -> success (ExprLit lit) (advance tokens)

    // Wildcard hole `_` in expression position (e.g. a free axis in a compound
    // index B((a, _, c))). A general token; the consuming context gives it
    // meaning, and unconsumed uses are rejected in typecheck.
    | Some TokUnderscore -> success ExprWildcard (advance tokens)
    
    // Variables or struct construction
    | Some (TokIdent name) ->
        let afterName = advance tokens
        match peek afterName with
        | Some TokLBrace when not noStructLiteralCtx.Value ->
            // Struct construction: Name { field1 = val1, field2 = val2 }
            // (suppressed in for-in range headers, where the brace is the
            // loop body — see noStructLiteralCtx)
            parseStructExpr name afterName
        | _ ->
            success (ExprVar name) afterName
    
    // Arity - requires arity(paramName) syntax
    | Some (TokKeyword KwArity) -> 
        let afterArity = advance tokens
        match peek afterArity with
        | Some TokLParen ->
            advance afterArity |> fun afterLParen ->
            match peek afterLParen with
            | Some (TokIdent paramName) ->
                advance afterLParen |> expect TokRParen >>= fun _ remaining ->
                success (ExprArity paramName) remaining
            | _ ->
                let line, col = currentPos afterLParen
                error "Expected parameter name in arity()" line col
        | _ ->
            let line, col = currentPos afterArity
            error "arity requires parameter name: arity(paramName)" line col
    | Some (TokKeyword KwNth) -> success ExprNth (advance tokens)
    | Some (TokKeyword KwZero) -> success ExprZero (advance tokens)
    
    // rank(expr) - get rank of array
    | Some (TokKeyword KwRank) ->
        advance tokens |> expect TokLParen >>= fun _ afterLParen ->
        parseExprImpl afterLParen >>= fun expr afterExpr ->
        expect TokRParen afterExpr >>= fun _ remaining ->
        success (ExprRank expr) remaining
    
    // compute - standalone keyword (used after |>)
    | Some (TokKeyword KwCompute) ->
        success (ExprVar "compute") (advance tokens)

    // Lambda
    | Some (TokKeyword KwLambda) ->
        parseLambda (advance tokens)
    
    // Let expression
    | Some (TokKeyword KwLet) ->
        parseLet (advance tokens)
    
    // If expression
    | Some (TokKeyword KwIf) ->
        parseIf (advance tokens)
    
    // Match expression
    | Some (TokKeyword KwMatch) ->
        parseMatch (advance tokens)
    
    // method_for
    | Some (TokKeyword KwMethodFor) ->
        parseMethodFor (advance tokens)
    
    // for (A, B) in virtualArray — co-iteration construct
    | Some (TokKeyword KwFor) ->
        let afterFor = advance tokens
        match peek afterFor with
        | Some TokLParen ->
            // Parse array list: (A, B, C)
            advance afterFor |> sepBy parseExprImpl TokComma >>= fun arrays afterArrays ->
            expect TokRParen afterArrays >>= fun _ afterRParen ->
            // Check for 'in' clause
            match peek afterRParen with
            | Some (TokKeyword KwIn) ->
                // Parse virtual array expression at arrayProduct level (stops before <@>)
                parseArrayProduct (advance afterRParen) >>= fun inExpr afterIn ->
                success (ExprFor (ForArrays (arrays, Some inExpr), [], None)) afterIn
            | _ ->
                // No in-clause: equivalent to method_for(A, B)
                success (ExprFor (ForArrays (arrays, None), [], None)) afterRParen
        | _ ->
            // for lambda(...) → ForKernel
            parseExprImpl afterFor >>= fun kernel remaining ->
            success (ExprFor (ForKernel kernel, [], None)) remaining
    
    // object_for
    | Some (TokKeyword KwObjectFor) ->
        parseObjectFor (advance tokens)
    
    // zip(a, b, ...)
    | Some (TokKeyword KwZip) ->
        advance tokens |> expect TokLParen >>= fun _ afterLParen ->
        sepBy parseExprImpl TokComma afterLParen >>= fun exprs afterExprs ->
        expect TokRParen afterExprs >>= fun _ remaining ->
        success (ExprZip exprs) remaining
    
    // stack(a, b, ...)
    | Some (TokKeyword KwStack) ->
        advance tokens |> expect TokLParen >>= fun _ afterLParen ->
        sepBy parseExprImpl TokComma afterLParen >>= fun exprs afterExprs ->
        expect TokRParen afterExprs >>= fun _ remaining ->
        success (ExprStack exprs) remaining
    
    // sequence(a, b, ...)
    | Some (TokKeyword KwSequence) ->
        advance tokens |> expect TokLParen >>= fun _ afterLParen ->
        sepBy parseExprImpl TokComma afterLParen >>= fun exprs afterExprs ->
        expect TokRParen afterExprs >>= fun _ remaining ->
        success (ExprSequence exprs) remaining
    
    // replicate(n, expr)
    | Some (TokKeyword KwReplicate) ->
        advance tokens |> expect TokLParen >>= fun _ afterLParen ->
        parseExprImpl afterLParen >>= fun count afterCount ->
        expect TokComma afterCount >>= fun _ afterComma ->
        parseExprImpl afterComma >>= fun body afterBody ->
        expect TokRParen afterBody >>= fun _ remaining ->
        success (ExprReplicate (count, body)) remaining
    
    // guard(cond, body)
    | Some (TokKeyword KwGuard) ->
        advance tokens |> expect TokLParen >>= fun _ afterLParen ->
        parseExprImpl afterLParen >>= fun cond afterCond ->
        expect TokComma afterCond >>= fun _ afterComma ->
        parseExprImpl afterComma >>= fun body afterBody ->
        expect TokRParen afterBody >>= fun _ remaining ->
        success (ExprGuard (cond, body)) remaining
    
    // mask(array, pred)
    | Some (TokKeyword KwMask) ->
        advance tokens |> expect TokLParen >>= fun _ afterLParen ->
        parseExprImpl afterLParen >>= fun arr afterArr ->
        expect TokComma afterArr >>= fun _ afterComma ->
        parseExprImpl afterComma >>= fun pred afterPred ->
        expect TokRParen afterPred >>= fun _ remaining ->
        success (ExprMask (arr, pred)) remaining

    | Some (TokKeyword KwCompound) ->
        advance tokens |> expect TokLParen >>= fun _ afterLParen ->
        parseExprImpl afterLParen >>= fun dense afterDense ->
        expect TokComma afterDense >>= fun _ afterComma ->
        parseExprImpl afterComma >>= fun mask afterMask ->
        expect TokRParen afterMask >>= fun _ remaining ->
        success (ExprCompound (dense, mask)) remaining
    
    // intersect(A, B)
    | Some (TokKeyword KwIntersect) ->
        advance tokens |> expect TokLParen >>= fun _ afterLParen ->
        parseExprImpl afterLParen >>= fun a afterA ->
        expect TokComma afterA >>= fun _ afterComma ->
        parseExprImpl afterComma >>= fun b afterB ->
        expect TokRParen afterB >>= fun _ remaining ->
        success (ExprIntersect (a, b)) remaining
    
    // union(A, B)
    | Some (TokKeyword KwUnion) ->
        advance tokens |> expect TokLParen >>= fun _ afterLParen ->
        parseExprImpl afterLParen >>= fun a afterA ->
        expect TokComma afterA >>= fun _ afterComma ->
        parseExprImpl afterComma >>= fun b afterB ->
        expect TokRParen afterB >>= fun _ remaining ->
        success (ExprUnion (a, b)) remaining
    
    // unique(A) — dedup, first-occurrence order
    | Some (TokKeyword KwUnique) ->
        advance tokens |> expect TokLParen >>= fun _ afterLParen ->
        parseExprImpl afterLParen >>= fun arr afterArr ->
        expect TokRParen afterArr >>= fun _ remaining ->
        success (ExprUnique arr) remaining
    
    // contains(A, x) — membership test, returns Bool
    | Some (TokKeyword KwContains) ->
        advance tokens |> expect TokLParen >>= fun _ afterLParen ->
        parseExprImpl afterLParen >>= fun arr afterArr ->
        expect TokComma afterArr >>= fun _ afterComma ->
        parseExprImpl afterComma >>= fun value afterValue ->
        expect TokRParen afterValue >>= fun _ remaining ->
        success (ExprContains (arr, value)) remaining
    
    // group_by(values, keys)
    | Some (TokKeyword KwGroupBy) ->
        advance tokens |> expect TokLParen >>= fun _ afterLParen ->
        parseExprImpl afterLParen >>= fun vals afterVals ->
        expect TokComma afterVals >>= fun _ afterComma ->
        parseExprImpl afterComma >>= fun keys afterKeys ->
        expect TokRParen afterKeys >>= fun _ remaining ->
        success (ExprGroupBy (vals, keys)) remaining
    
    // group_keys(keys1, keys2, ...) — single key for ordinary grouping;
    // multiple keys triggers compound (tuple-keyed) grouping.
    | Some (TokKeyword KwGroupKeys) ->
        advance tokens |> expect TokLParen >>= fun _ afterLParen ->
        sepBy parseExprImpl TokComma afterLParen >>= fun keys afterKeys ->
        expect TokRParen afterKeys >>= fun _ remaining ->
        success (ExprGroupKeys keys) remaining
    
    // sort(array, key) — stable sort by ascending key
    | Some (TokKeyword KwSort) ->
        advance tokens |> expect TokLParen >>= fun _ afterLParen ->
        parseExprImpl afterLParen >>= fun array afterArr ->
        expect TokComma afterArr >>= fun _ afterComma ->
        parseExprImpl afterComma >>= fun key afterKey ->
        expect TokRParen afterKey >>= fun _ remaining ->
        success (ExprSort (array, key)) remaining

    // transpose(A, [d1, d2]) — swap exactly two axes. The axis list must be
    // EXACTLY two integer literals; any other shape is a parse error (no
    // implicit "reverse all dims", no general permutation). Semantic checks
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
                success (ExprTranspose (array, int d1, int d2)) remaining
             | _ ->
                let line, col = currentPos afterComma2
                error "transpose expects exactly two integer axis indices: transpose(A, [d1, d2])" line col)
         | _ ->
            let line, col = currentPos afterLBrack
            error "transpose expects exactly two integer axis indices: transpose(A, [d1, d2])" line col)
    
    // hermitian(A) — the conjugate-transpose (Hermitian adjoint) A^H of a
    // rank-2 array: swap axes 0 and 1 AND conjugate the elements. Pure surface
    // sugar: desugars to conj(transpose(A, [0, 1])), riding entirely on the
    // existing transpose + conj machinery (no new AST/IR/typecheck/codegen).
    // NOTE: the RESULT is a plain dense array (the adjoint operation), NOT a
    // SymHermitian-typed matrix — the name refers to the operation A^H, not the
    // property. The Hermitian-typed producer is `gram` (A * hermitian(A)).
    | Some (TokKeyword KwHermitian) ->
        advance tokens |> expect TokLParen >>= fun _ afterLParen ->
        parseExprImpl afterLParen >>= fun array afterArr ->
        expect TokRParen afterArr >>= fun _ remaining ->
        success (ExprUnaryOp (OpConj, ExprTranspose (array, 0, 1))) remaining

    // gram(A, B) = A * B^H — the (conjugate) Gram product:
    //   result[i][j] = sum_k A[i][k] * conj(B[j][k])
    // A is m x n, B is p x n (shared contracted dim n), result is m x p. When A
    // and B are the SAME array (syntactically the same variable) the result is
    // square and symmetric (real) / Hermitian (complex), computed via the
    // triangular upper-half scatter; otherwise it is a general dense m x p array.
    | Some (TokKeyword KwGram) ->
        advance tokens |> expect TokLParen >>= fun _ afterLParen ->
        parseExprImpl afterLParen >>= fun left afterLeft ->
        expect TokComma afterLeft >>= fun _ afterComma ->
        parseExprImpl afterComma >>= fun right afterRight ->
        expect TokRParen afterRight >>= fun _ remaining ->
        success (ExprGram (left, right)) remaining

    | Some (TokKeyword KwDecompact) ->
        advance tokens |> expect TokLParen >>= fun _ afterLParen ->
        parseExprImpl afterLParen >>= fun array afterArr ->
        expect TokComma afterArr >>= fun _ afterComma ->
        (match peek afterComma with
         | Some (TokInt d) ->
            let afterD = advance afterComma
            expect TokRParen afterD >>= fun _ remaining ->
            success (ExprDecompact (array, int d)) remaining
         | _ ->
            let line, col = currentPos afterComma
            error "decompact expects a single integer dimension index: decompact(A, d)" line col)
    
    // reduce(array, op[, init]) — fold innermost dim by binary kernel.
    // The kernel is optional; if omitted, defaults to (+).
    // Accept operator sections like (+) the same way object_for does.
    // The optional 3rd argument is the fold's initial accumulator: the fold
    // computes init ⊕ a0 ⊕ a1 ⊕ ..., and an EMPTY array reduces to init
    // (without init, empty inputs are rejected/guarded).
    | Some (TokKeyword KwReduce) ->
        advance tokens |> expect TokLParen >>= fun _ afterLParen ->
        parseExprImpl afterLParen >>= fun array afterArr ->
        match peek afterArr with
        | Some TokRParen ->
            // 1-arg form: reduce(arr) ≡ reduce(arr, (+))
            expect TokRParen afterArr >>= fun _ remaining ->
            success (ExprReduce (array, ExprSection OpAdd, None)) remaining
        | _ ->
            expect TokComma afterArr >>= fun _ afterComma ->
            parseExprImpl afterComma >>= fun op afterOp ->
            match peek afterOp with
            | Some TokRParen ->
                expect TokRParen afterOp >>= fun _ remaining ->
                success (ExprReduce (array, op, None)) remaining
            | _ ->
                expect TokComma afterOp >>= fun _ afterComma2 ->
                parseExprImpl afterComma2 >>= fun initE afterInit ->
                expect TokRParen afterInit >>= fun _ remaining ->
                success (ExprReduce (array, op, Some initE)) remaining

    // conj(x) — complex conjugate. Built-in unary op (identity on real,
    // conjugate on complex). Function-call surface form; lowers to the
    // existing IRUnaryOp machinery via ExprUnaryOp(OpConj, _).
    | Some (TokKeyword KwConj) ->
        advance tokens |> expect TokLParen >>= fun _ afterLParen ->
        parseExprImpl afterLParen >>= fun arg afterArg ->
        expect TokRParen afterArg >>= fun _ remaining ->
        success (ExprUnaryOp (OpConj, arg)) remaining

    // extents(array) — innermost-dim extent. Rank-1 returns scalar Int64.
    | Some (TokKeyword KwExtents) ->
        advance tokens |> expect TokLParen >>= fun _ afterLParen ->
        parseExprImpl afterLParen >>= fun array afterArr ->
        expect TokRParen afterArr >>= fun _ remaining ->
        success (ExprExtents array) remaining
    
    // pure(expr)
    | Some (TokKeyword KwPure) ->
        advance tokens |> expect TokLParen >>= fun _ afterLParen ->
        parseExprImpl afterLParen >>= fun expr afterExpr ->
        expect TokRParen afterExpr >>= fun _ remaining ->
        success (ExprPure expr) remaining
    
    // reynolds(kernel) or reynolds(kernel, Antisymmetric)
    | Some (TokKeyword KwReynolds) ->
        advance tokens |> expect TokLParen >>= fun _ afterLParen ->
        parseExprImpl afterLParen >>= fun kernel afterKernel ->
        // Check for optional Symmetric/Antisymmetric
        let isAntisym, afterSpec =
            match peek afterKernel with
            | Some TokComma ->
                match peek (advance afterKernel) with
                | Some (TokIdent "Antisymmetric") -> true, advance (advance afterKernel)
                | Some (TokIdent "Symmetric") -> false, advance (advance afterKernel)
                | _ -> false, afterKernel
            | _ -> false, afterKernel
        expect TokRParen afterSpec >>= fun _ remaining ->
        success (ExprReynolds (kernel, isAntisym)) remaining
    
    // range<T> or range<T1, ..., Tn> (multi-index: one virtual array spanning
    // all listed index types, uncurried into nested loop levels in IR)
    | Some (TokKeyword KwRange) ->
        advance tokens |> expect (TokOp "<") >>= fun _ afterLt ->
        afterLt |> sepBy parseTypeExpr TokComma >>= fun tys afterTys ->
        expectGt afterTys >>= fun _ remaining ->
        success (ExprRange tys) remaining
    
    // reverse<T>
    | Some (TokKeyword KwReverse) ->
        advance tokens |> expect (TokOp "<") >>= fun _ afterLt ->
        parseTypeExpr afterLt >>= fun ty afterTy ->
        expectGt afterTy >>= fun _ remaining ->
        success (ExprReverse ty) remaining
    
    // Parenthesized expression or tuple (delimited: struct literals
    // re-enabled inside — the for-in range-header suppression applies to
    // the top nesting level only)
    | Some TokLParen ->
        allowingStructLiterals (fun () -> advance tokens |> parseParenExpr)

    // Array literal (delimited: struct literals re-enabled)
    | Some TokLBracket ->
        allowingStructLiterals (fun () ->
            advance tokens |> sepBy parseExprImpl TokComma >>= fun elems afterElems ->
            expect TokRBracket afterElems >>= fun _ remaining ->
            success (ExprArrayLit elems) remaining)
    
    // Block
    | Some TokLBrace ->
        parseBlock (advance tokens)
    
    | Some kind ->
        let line, col = currentPos tokens
        error (sprintf "Unexpected token: %A" kind) line col
    
    | None ->
        error "Unexpected end of input" 0 0

// ============================================================================
// Compound Expression Parsers
// ============================================================================

and parseLambda (tokens: Token list) : ParseResult<Expr> =
    expect TokLParen tokens >>= fun _ afterLParen ->
    sepBy parseLambdaParam TokComma afterLParen >>= fun parms afterParms ->
    expect TokRParen afterParms >>= fun _ afterRParen ->
    
    // Optional where clause (parallel to function declarations).
    // If a `where` keyword is present, a parse error inside the clause is a
    // GENUINE error and must propagate — previously it was swallowed (reset to
    // None), which then produced a misleading "expected ->" error at the `where`
    // token and hid the real cause (e.g. mutual-exclusion violations).
    let whereResult : ParseResult<WhereClause option> =
        match peek afterRParen with
        | Some (TokKeyword KwWhere) ->
            match parseWhereClause (advance afterRParen) with
            | Ok (w, rest) -> Ok (Some w, rest)
            | Error e -> Error e
        | _ -> Ok (None, afterRParen)
    whereResult >>= fun whereClause afterWhere ->
    expect (TokOp "->") afterWhere >>= fun _ afterArrow ->
    // Lambda body: check for block or parse inline expression
    let afterArrow = skipNL afterArrow
    match peek afterArrow with
    | Some TokLBrace ->
        // Block body
        parseBlock (advance afterArrow) >>= fun body remaining ->
        success (ExprLambda (parms, whereClause, body)) remaining
    | _ ->
        // Inline body - parse at Apply precedence level so |> isn't consumed
        // This means: lambda(x) -> a <@> b |> compute parses as (lambda(x) -> a <@> b) |> compute
        parseApply afterArrow >>= fun body remaining ->
        success (ExprLambda (parms, whereClause, body)) remaining

and parseLambdaParam (tokens: Token list) : ParseResult<LambdaParam> =
    expectIdent tokens >>= fun name afterName ->
    match peek afterName with
    | Some TokColon ->
        advance afterName |> parseTypeExpr >>= fun ty remaining ->
        success { Name = name; Type = Some ty } remaining
    | _ ->
        success { Name = name; Type = None } afterName

and parseLet (tokens: Token list) : ParseResult<Expr> =
    // let [const|mut] pattern [: type] = value
    // Note: Blade does NOT have ML-style "let x = v in body" syntax
    // 'in' is only used for virtual arrays in for-loops
    let mutability, afterMut =
        match peek tokens with
        | Some (TokKeyword KwConst) -> BindConst, advance tokens
        | Some (TokKeyword KwMut) -> BindMut, advance tokens
        | _ -> BindLet, tokens
    
    parsePattern afterMut >>= fun pat afterPat ->
    let ty, afterTy =
        match peek afterPat with
        | Some TokColon ->
            match parseTypeExpr (advance afterPat) with
            | Ok (t, rest) -> Some t, rest
            | Error _ -> None, afterPat
        | _ -> None, afterPat
    
    expect (TokOp "=") afterTy >>= fun _ afterEq ->
    
    // Check for block or inline value
    let afterEq = skipNL afterEq
    match peek afterEq with
    | Some TokLBrace ->
        // Block value
        parseBlock (advance afterEq) >>= fun value afterValue ->
        let binding = { Mutability = mutability; Pattern = pat; Type = ty; Value = value }
        success (ExprLet (binding, ExprLit LitUnit)) afterValue
    | _ ->
        // Inline value
        parseExprImpl afterEq >>= fun value afterValue ->
        let binding = { Mutability = mutability; Pattern = pat; Type = ty; Value = value }
        success (ExprLet (binding, ExprLit LitUnit)) afterValue

and parseIf (tokens: Token list) : ParseResult<Expr> =
    parseExprImpl tokens >>= fun cond afterCond ->
    expect (TokKeyword KwThen) afterCond >>= fun _ afterThen ->
    parseExprImpl afterThen >>= fun thenBr afterThenBr ->
    expect (TokKeyword KwElse) afterThenBr >>= fun _ afterElse ->
    parseExprImpl afterElse >>= fun elseBr remaining ->
    success (ExprIf (cond, thenBr, elseBr)) remaining

and parseMatch (tokens: Token list) : ParseResult<Expr> =
    parseExprImpl tokens >>= fun scrutinee afterScrutinee ->
    expect (TokKeyword KwWith) afterScrutinee >>= fun _ afterWith ->
    many parseMatchCase (skipNL afterWith) >>= fun cases remaining ->
    success (ExprMatch (scrutinee, cases)) remaining

// FIXED: Properly propagate errors from guard parsing instead of swallowing them
and parseMatchCase (tokens: Token list) : ParseResult<MatchCase> =
    let tokens = skipNL tokens
    match peek tokens with
    | Some TokPipe ->
        advance tokens |> parsePattern >>= fun pat afterPat ->
        // Parse optional guard with proper error handling
        match peek afterPat with
        | Some (TokKeyword KwIf) ->
            // Parse guard expression, propagate errors
            advance afterPat |> parseGuardExpr >>= fun guard afterGuard ->
            expect (TokOp "->") afterGuard >>= fun _ afterArrow ->
            // Use parseBody: inline expressions stop at newline,
            // multi-line bodies (e.g. nested match) require braces
            parseBody afterArrow >>= fun body remaining ->
            success { Pattern = pat; Guard = Some guard; Body = body } remaining
        | _ ->
            expect (TokOp "->") afterPat >>= fun _ afterArrow ->
            parseBody afterArrow >>= fun body remaining ->
            success { Pattern = pat; Guard = None; Body = body } remaining
    | _ ->
        let line, col = currentPos tokens
        error "Expected '|' to start match case" line col

// Parse guard expression - stops before ->
// This is a restricted expression parser that doesn't consume ->
and parseGuardExpr (tokens: Token list) : ParseResult<Expr> =
    parseGuardOr tokens

and parseGuardOr (tokens: Token list) : ParseResult<Expr> =
    parseGuardAnd tokens >>= fun left rest ->
    let rec loop acc toks =
        match peek toks with
        | Some (TokOp "||") ->
            advance toks |> parseGuardAnd >>= fun right remaining ->
            loop (ExprBinOp (Elementwise, OpOr, acc, right)) remaining
        | _ -> success acc toks
    loop left rest

and parseGuardAnd (tokens: Token list) : ParseResult<Expr> =
    parseGuardComparison tokens >>= fun left rest ->
    let rec loop acc toks =
        match peek toks with
        | Some (TokOp "&&") ->
            advance toks |> parseGuardComparison >>= fun right remaining ->
            loop (ExprBinOp (Elementwise, OpAnd, acc, right)) remaining
        | _ -> success acc toks
    loop left rest

and parseGuardComparison (tokens: Token list) : ParseResult<Expr> =
    parseGuardAdditive tokens >>= fun left rest ->
    match peek rest with
    | Some (TokOp "==") ->
        advance rest |> parseGuardAdditive >>= fun right remaining ->
        success (ExprBinOp (Elementwise, OpEq, left, right)) remaining
    | Some (TokOp "!=") ->
        advance rest |> parseGuardAdditive >>= fun right remaining ->
        success (ExprBinOp (Elementwise, OpNeq, left, right)) remaining
    | Some (TokOp "<") ->
        advance rest |> parseGuardAdditive >>= fun right remaining ->
        success (ExprBinOp (Elementwise, OpLt, left, right)) remaining
    | Some (TokOp "<=") ->
        advance rest |> parseGuardAdditive >>= fun right remaining ->
        success (ExprBinOp (Elementwise, OpLe, left, right)) remaining
    | Some (TokOp ">") ->
        advance rest |> parseGuardAdditive >>= fun right remaining ->
        success (ExprBinOp (Elementwise, OpGt, left, right)) remaining
    | Some (TokOp ">=") ->
        advance rest |> parseGuardAdditive >>= fun right remaining ->
        success (ExprBinOp (Elementwise, OpGe, left, right)) remaining
    | _ -> success left rest

and parseGuardAdditive (tokens: Token list) : ParseResult<Expr> =
    parseGuardMultiplicative tokens >>= fun left rest ->
    let rec loop acc toks =
        match peek toks with
        | Some (TokOp "+") ->
            advance toks |> parseGuardMultiplicative >>= fun right remaining ->
            loop (ExprBinOp (Elementwise, OpAdd, acc, right)) remaining
        | Some (TokOp "-") ->
            advance toks |> parseGuardMultiplicative >>= fun right remaining ->
            loop (ExprBinOp (Elementwise, OpSub, acc, right)) remaining
        | _ -> success acc toks
    loop left rest

and parseGuardMultiplicative (tokens: Token list) : ParseResult<Expr> =
    parseGuardPrimary tokens >>= fun left rest ->
    let rec loop acc toks =
        match peek toks with
        | Some (TokOp "*") ->
            advance toks |> parseGuardPrimary >>= fun right remaining ->
            loop (ExprBinOp (Elementwise, OpMul, acc, right)) remaining
        | Some (TokOp "/") ->
            advance toks |> parseGuardPrimary >>= fun right remaining ->
            loop (ExprBinOp (Elementwise, OpDiv, acc, right)) remaining
        | _ -> success acc toks
    loop left rest

and parseGuardPrimary (tokens: Token list) : ParseResult<Expr> =
    match peek tokens with
    | Some (LiteralTok lit) -> success (ExprLit lit) (advance tokens)
    | Some (TokIdent name) -> 
        let afterName = advance tokens
        // Allow function calls in guards
        match peek afterName with
        | Some TokLParen ->
            advance afterName |> sepBy parseGuardExpr TokComma >>= fun args afterArgs ->
            expect TokRParen afterArgs >>= fun _ remaining ->
            success (ExprApp (ExprVar name, args)) remaining
        | _ -> success (ExprVar name) afterName
    | Some TokLParen ->
        advance tokens |> parseGuardExpr >>= fun expr afterExpr ->
        expect TokRParen afterExpr >>= fun _ remaining ->
        success expr remaining
    | Some (TokOp "-") ->
        advance tokens |> parseGuardPrimary >>= fun expr remaining ->
        success (ExprUnaryOp (OpNeg, expr)) remaining
    | Some (TokOp "!") ->
        advance tokens |> parseGuardPrimary >>= fun expr remaining ->
        success (ExprUnaryOp (OpNot, expr)) remaining
    | Some kind ->
        let line, col = currentPos tokens
        error (sprintf "Unexpected token in guard: %A" kind) line col
    | None ->
        error "Expected guard expression but got EOF" 0 0

and parseMethodFor (tokens: Token list) : ParseResult<Expr> =
    expect TokLParen tokens >>= fun _ afterLParen ->
    sepBy parseExprImpl TokComma afterLParen >>= fun arrays afterArrays ->
    expect TokRParen afterArrays >>= fun _ remaining ->
    success (ExprMethodFor arrays) remaining

and parseObjectFor (tokens: Token list) : ParseResult<Expr> =
    expect TokLParen tokens >>= fun _ afterLParen ->
    // Check for combinator section: object_for(<&>), object_for(<&!>), object_for(<*>), etc.
    match peek afterLParen with
    | Some (TokOp op) ->
        let afterOp = advance afterLParen
        match peek afterOp with
        | Some TokRParen ->
            // It's a combinator/operator section
            let binOp = 
                match op with
                | "<&>" -> Some OpParallel
                | "<&!>" -> Some OpFusion
                | "<*>" -> Some OpArrayProd
                | "<@>" -> Some OpApply
                | "<$>" -> Some OpFunctor
                | "<|>" -> Some OpChoice
                | ">>=" -> Some OpBind
                | _ -> stringToBinOp op  // fall back to scalar ops (+, *, etc.)
            match binOp with
            | Some b -> success (ExprObjectFor (ExprSection b)) (advance afterOp)
            | None ->
                let line, col = currentPos afterLParen
                error (sprintf "Unknown operator in object_for: %s" op) line col
        | _ ->
            // Not a section — fall back to normal expression parsing
            parseExprImpl afterLParen >>= fun kernel afterKernel ->
            expect TokRParen afterKernel >>= fun _ remaining ->
            success (ExprObjectFor kernel) remaining
    | _ ->
        parseExprImpl afterLParen >>= fun kernel afterKernel ->
        expect TokRParen afterKernel >>= fun _ remaining ->
        success (ExprObjectFor kernel) remaining

and parseParenExpr (tokens: Token list) : ParseResult<Expr> =
    match peek tokens with
    | Some TokRParen ->
        success (ExprTuple []) (advance tokens)
    // Check for operator section: (+), (*), etc.
    | Some (TokOp op) ->
        let afterOp = advance tokens
        match peek afterOp with
        | Some TokRParen ->
            // It's a section like (+) or (*)
            match stringToBinOp op with
            | Some binOp -> success (ExprSection binOp) (advance afterOp)
            | None -> 
                let line, col = currentPos tokens
                error (sprintf "Unknown operator in section: %s" op) line col
        | _ ->
            // Not a section, parse as normal expression
            parseExprImpl tokens >>= fun first afterFirst ->
            match peek afterFirst with
            | Some TokRParen ->
                success first (advance afterFirst)
            | Some TokComma ->
                advance afterFirst |> sepBy parseExprImpl TokComma >>= fun rest afterRest ->
                expect TokRParen afterRest >>= fun _ remaining ->
                success (ExprTuple (first :: rest)) remaining
            | _ ->
                let line, col = currentPos afterFirst
                error "Expected ')' or ',' in parenthesized expression" line col
    | _ ->
        parseExprImpl tokens >>= fun first afterFirst ->
        match peek afterFirst with
        | Some TokRParen ->
            success first (advance afterFirst)
        | Some TokComma ->
            advance afterFirst |> sepBy parseExprImpl TokComma >>= fun rest afterRest ->
            expect TokRParen afterRest >>= fun _ remaining ->
            success (ExprTuple (first :: rest)) remaining
        | _ ->
            let line, col = currentPos afterFirst
            error "Expected ')' or ',' in parenthesized expression" line col

/// Convert operator string to BinOp
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
        // Statement span (audit §3.4): the position of the statement's first
        // token. End position is not tracked yet — a start line/col is what
        // error messages need; widening to full ranges is rewrite work.
        let sLine, sCol = currentPos toks
        let spanned (stmt: Stmt) =
            StmtSpanned (stmt, { StartLine = sLine; StartCol = sCol; EndLine = sLine; EndCol = sCol; File = None })
        match peek toks with
        | Some TokRBrace ->
            // End of block - last expression (if any) is the return value
            // stmts is in reverse order (most recent first), so head = last statement
            let (statements, finalExpr) =
                match stmts with
                | StmtSpanned (StmtExpr e, _) :: rest
                | StmtExpr e :: rest -> List.rev rest, Some e
                | all -> List.rev all, None
            success (ExprBlock (statements, finalExpr)) (advance toks)
        | Some TokSemi ->
            // Explicit semicolon - skip it
            loop stmts (advance toks)
        | Some (TokKeyword KwLet) ->
            advance toks |> parseLetStmt >>= fun stmt remaining ->
            // Consume optional terminator (newline or semicolon)
            let remaining = skipTerminator remaining
            loop (spanned stmt :: stmts) remaining
        | Some (TokKeyword KwFunction) ->
            // Nested function declaration - parse as let binding of lambda
            advance toks |> parseNestedFunction >>= fun stmt remaining ->
            let remaining = skipTerminator remaining
            loop (spanned stmt :: stmts) remaining
        | Some (TokKeyword KwFor) ->
            // Check for imperative for-in loop: for IDENT in EXPR { STMTS }
            let afterFor = advance toks
            match peek afterFor with
            | Some (TokIdent varName) ->
                let afterIdent = advance afterFor
                match peek afterIdent with
                | Some (TokKeyword KwIn) ->
                    // Parse range expression (struct literals suppressed:
                    // the next top-level `{` is the loop body)
                    let afterIn = advance afterIdent
                    suppressingStructLiterals (fun () -> parseExprImpl afterIn) >>= fun rangeExpr afterRange ->
                    let afterRange = skipNL afterRange
                    match peek afterRange with
                    | Some TokLBrace ->
                        // Parse body as block of statements
                        advance afterRange |> parseForInBody >>= fun bodyStmts remaining ->
                        let remaining = skipTerminator remaining
                        loop (spanned (StmtForIn (varName, rangeExpr, bodyStmts)) :: stmts) remaining
                    | _ ->
                        let line, col = currentPos afterRange
                        error "Expected '{' after for-in range expression" line col
                | _ ->
                    // Not a for-in, fall through to expression parser
                    parseExprImpl toks >>= fun expr remaining ->
                    let remaining = skipTerminator remaining
                    loop (spanned (StmtExpr expr) :: stmts) remaining
            | _ ->
                // Not a for-in, fall through to expression parser
                parseExprImpl toks >>= fun expr remaining ->
                let remaining = skipTerminator remaining
                loop (spanned (StmtExpr expr) :: stmts) remaining
        | Some _ ->
            parseExprImpl toks >>= fun expr remaining ->
            let remaining = skipTerminator remaining
            loop (spanned (StmtExpr expr) :: stmts) remaining
        | None ->
            error "Unexpected EOF in block" 0 0
    loop [] tokens

and skipTerminator toks =
    match peek toks with
    | Some TokNewline -> advance toks
    | Some TokSemi -> advance toks
    | _ -> toks

/// Parse the body of a for-in loop: { stmt; stmt; ... }
/// Accepts the same statement forms as parseBlock, including NESTED for-in
/// loops (required by the ML-module layers and the grad transform, whose
/// generated adjoint loops mirror the forward nesting).
and parseForInBody (tokens: Token list) : ParseResult<Stmt list> =
    let rec loop stmts toks =
        let toks = skipNL toks
        let sLine, sCol = currentPos toks
        let spanned (stmt: Stmt) =
            StmtSpanned (stmt, { StartLine = sLine; StartCol = sCol; EndLine = sLine; EndCol = sCol; File = None })
        match peek toks with
        | Some TokRBrace ->
            success (List.rev stmts) (advance toks)
        | Some TokSemi ->
            loop stmts (advance toks)
        | Some (TokKeyword KwLet) ->
            advance toks |> parseLetStmt >>= fun stmt remaining ->
            let remaining = skipTerminator remaining
            loop (spanned stmt :: stmts) remaining
        | Some (TokKeyword KwFor) ->
            // Nested for-in: same disambiguation as parseBlock (an actual
            // `for IDENT in` starts an imperative loop; anything else falls
            // through to the expression parser, e.g. loop-object `for`).
            let afterFor = advance toks
            match peek afterFor with
            | Some (TokIdent varName) ->
                let afterIdent = advance afterFor
                match peek afterIdent with
                | Some (TokKeyword KwIn) ->
                    let afterIn = advance afterIdent
                    // struct literals suppressed in the range header (see
                    // noStructLiteralCtx / the parseBlock site)
                    suppressingStructLiterals (fun () -> parseExprImpl afterIn) >>= fun rangeExpr afterRange ->
                    let afterRange = skipNL afterRange
                    match peek afterRange with
                    | Some TokLBrace ->
                        advance afterRange |> parseForInBody >>= fun bodyStmts remaining ->
                        let remaining = skipTerminator remaining
                        loop (spanned (StmtForIn (varName, rangeExpr, bodyStmts)) :: stmts) remaining
                    | _ ->
                        let line, col = currentPos afterRange
                        error "Expected '{' after for-in range expression" line col
                | _ ->
                    parseExprImpl toks >>= fun expr remaining ->
                    let remaining = skipTerminator remaining
                    loop (spanned (StmtExpr expr) :: stmts) remaining
            | _ ->
                parseExprImpl toks >>= fun expr remaining ->
                let remaining = skipTerminator remaining
                loop (spanned (StmtExpr expr) :: stmts) remaining
        | Some _ ->
            // Parse expression (includes assignments via parseAssignment)
            parseExprImpl toks >>= fun expr remaining ->
            let remaining = skipTerminator remaining
            loop (spanned (StmtExpr expr) :: stmts) remaining
        | None ->
            error "Unexpected EOF in for-in body" 0 0
    loop [] tokens

and parseNestedFunction (tokens: Token list) : ParseResult<Stmt> =
    // Parse: function name(params) where ... -> Type = body
    // Convert to: let name = lambda(params) where ... -> Type -> body
    expectIdent tokens >>= fun name afterName ->
    expect TokLParen afterName >>= fun _ afterLParen ->
    sepBy parseLambdaParam TokComma afterLParen >>= fun parms afterParms ->
    expect TokRParen afterParms >>= fun _ afterRParen ->
    
    // Skip newlines between clauses
    let afterRParen = skipNL afterRParen
    
    // Optional where clause
    // NOTE (latent bug, same as the one fixed in parseLambda ~line 1346): the
    // `Error _ -> None` arm SWALLOWS genuine where-clause parse errors and then
    // fails later with a misleading message. Not fixed here yet — no test
    // exercises this `function` where-clause-error path; fix with a test.
    let whereClause, afterWhere =
        match peek afterRParen with
        | Some (TokKeyword KwWhere) ->
            match parseWhereClause (advance afterRParen) with
            | Ok (w, rest) -> Some w, skipNL rest
            | Error _ -> None, afterRParen
        | _ -> None, afterRParen
    
    // Optional return type
    let retType, afterRet =
        match peek afterWhere with
        | Some (TokOp "->") ->
            match parseTypeExpr (advance afterWhere) with
            | Ok (t, rest) -> Some t, skipNL rest
            | Error _ -> None, afterWhere
        | _ -> None, afterWhere
    
    expect (TokOp "=") afterRet >>= fun _ afterEq ->
    parseInlineOrBlock afterEq >>= fun body remaining ->
    
    // Create a let binding: let name = lambda(params) where ... -> body
    let lambda = ExprLambda (parms, whereClause, body)
    let binding = {
        Pattern = PatVar name
        Type = retType
        Value = lambda
        Mutability = BindConst
    }
    success (StmtLet binding) remaining

and parseLetStmt (tokens: Token list) : ParseResult<Stmt> =
    let mutability, afterMut =
        match peek tokens with
        | Some (TokKeyword KwConst) -> BindConst, advance tokens
        | Some (TokKeyword KwMut) -> BindMut, advance tokens
        | _ -> BindLet, tokens
    
    parsePattern afterMut >>= fun pat afterPat ->
    let ty, afterTy =
        match peek afterPat with
        | Some TokColon ->
            match parseTypeExpr (advance afterPat) with
            | Ok (t, rest) -> Some t, rest
            | Error _ -> None, afterPat
        | _ -> None, afterPat
    
    expect (TokOp "=") afterTy >>= fun _ afterEq ->
    parseExprImpl afterEq >>= fun value remaining ->
    
    success (StmtLet {
        Mutability = mutability
        Pattern = pat
        Type = ty
        Value = value
    }) remaining

// ============================================================================
// Declaration Parsing
// ============================================================================

let parseParamDecl (tokens: Token list) : ParseResult<ParamDecl> =
    expectIdent tokens >>= fun name afterName ->
    match peek afterName with
    | Some TokColon ->
        // Optional mutability marker before the type: `x: mut T`
        // (formalism §2.7 — permits callee mutation; used by grad's
        // gradient out-buffers). All params pass by reference already,
        // so this is a CHECKING property, not a calling-convention one.
        let mutability, afterAnnot =
            match peek (advance afterName) with
            | Some (TokKeyword KwMut) -> Mutable, advance (advance afterName)
            | _ -> Immutable, advance afterName
        parseTypeExpr afterAnnot >>= fun ty remaining ->
        success { Name = name; Type = Some ty; Mutability = mutability } remaining
    | _ ->
        success { Name = name; Type = None; Mutability = Immutable } afterName

let parseFunctionDecl (tokens: Token list) : ParseResult<Decl> =
    expectIdent tokens >>= fun name afterName ->
    expect TokLParen afterName >>= fun _ afterLParen ->
    sepBy parseParamDecl TokComma afterLParen >>= fun parms afterParms ->
    expect TokRParen afterParms >>= fun _ afterRParen ->
    
    // Skip newlines between parts of function declaration
    let afterRParen = skipNL afterRParen
    
    // Optional where clause
    // NOTE (latent bug, same as the one fixed in parseLambda ~line 1346): the
    // `Error _ -> None` arm SWALLOWS genuine where-clause parse errors and then
    // fails later with a misleading message. Not fixed here yet — no test
    // exercises this `function` where-clause-error path; fix with a test.
    let whereClause, afterWhere =
        match peek afterRParen with
        | Some (TokKeyword KwWhere) ->
            match parseWhereClause (advance afterRParen) with
            | Ok (w, rest) -> Some w, skipNL rest
            | Error _ -> None, afterRParen
        | _ -> None, afterRParen
    
    // Optional return type (either : Type or -> Type)
    let retType, afterRet =
        match peek afterWhere with
        | Some TokColon ->
            match parseTypeExpr (advance afterWhere) with
            | Ok (t, rest) -> Some t, skipNL rest
            | Error _ -> None, afterWhere
        | Some (TokOp "->") ->
            match parseTypeExpr (advance afterWhere) with
            | Ok (t, rest) -> Some t, skipNL rest
            | Error _ -> None, afterWhere
        | _ -> None, afterWhere
    
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
    }) remaining

let parseTopLevelLet (tokens: Token list) : ParseResult<Decl> =
    let mutability, afterMut =
        match peek tokens with
        | Some (TokKeyword KwConst) -> BindConst, advance tokens
        | Some (TokKeyword KwMut) -> BindMut, advance tokens
        | _ -> BindLet, tokens
    
    parsePattern afterMut >>= fun pat afterPat ->
    let ty, afterTy =
        match peek afterPat with
        | Some TokColon ->
            match parseTypeExpr (advance afterPat) with
            | Ok (t, rest) -> Some t, rest
            | Error _ -> None, afterPat
        | _ -> None, afterPat
    
    expect (TokOp "=") afterTy >>= fun _ afterEq ->
    parseExprImpl afterEq >>= fun value remaining ->
    
    success (DeclLet {
        Mutability = mutability
        Pattern = pat
        Type = ty
        Value = value
    }) remaining

// ============================================================================
// Type, Struct, Interface, Impl Declarations
// ============================================================================

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
    // Skip optional leading |
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

/// Parse type declaration: type Name<T> = ... (alias or sum type)
/// Parse a comma-separated conjunct list after `where`: c1, c2, ...
/// Each conjunct is a full expression. A top-level comma always separates
/// conjuncts — tuple expressions require parentheses, so parseExpr never
/// consumes one.
let parseConjuncts (tokens: Token list) : ParseResult<Expr list> =
    let rec loop acc toks =
        parseExpr (skipNL toks) >>= fun c afterC ->
        match peek afterC with
        | Some TokComma -> loop (acc @ [c]) (advance afterC)
        | _ -> success (acc @ [c]) afterC
    loop [] tokens

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
                // Look ahead to see if there's a | after the first variant
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
                // Plain alias: hand back the ORIGINAL remainder so trailing
                // newlines are consumed by the decl loop, exactly as before.
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
        parseTypeExpr afterColon >>= fun ty remaining ->
        // Optional dependent range refinement: `in lo .. hi`. Bounds parse at
        // the additive level (parseDotDot's own operand level) so they stop
        // cleanly at `..`, ',' and '}'. `in` never otherwise follows a field
        // type inside a struct body, so the postfix is unambiguous.
        match peek remaining with
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
                success { Name = name; Type = ty; Default = None; Bound = Some { Lo = lo; Hi = hi } } afterHi
        | _ ->
            success { Name = name; Type = ty; Default = None; Bound = None } remaining
    | _ ->
        let line, col = currentPos tokens
        error "Expected field name" line col

/// Parse struct declaration: struct Name<T> { field1: T1, field2: T2 }
let parseStructDecl (tokens: Token list) : ParseResult<Decl> =
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
            success (DeclType (TyDeclStruct (name, typeParams, fields, conjuncts))) afterConstraint
        | _ ->
            success (DeclType (TyDeclStruct (name, typeParams, fields, []))) remaining
    | _ ->
        let line, col = currentPos tokens
        error "Expected struct name" line col

/// Parse function signature: function name(parameters) -> RetType
let parseFunctionSig (tokens: Token list) : ParseResult<FunctionSig> =
    expect (TokKeyword KwFunction) tokens >>= fun _ afterKw ->
    match peek afterKw with
    | Some (TokIdent name) ->
        let afterName = advance afterKw
        expect TokLParen afterName >>= fun _ afterLParen ->
        sepBy parseParamDecl TokComma afterLParen >>= fun parms afterParms ->
        expect TokRParen afterParms >>= fun _ afterRParen ->
        
        // Parse return type
        match peek afterRParen with
        | Some (TokOp "->") ->
            parseTypeExpr (advance afterRParen) >>= fun retType remaining ->
            success { Name = name; Params = parms; ReturnType = retType } remaining
        | _ ->
            // Default to Unit return type
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

// ============================================================================
// Unit of Measure Declarations
// ============================================================================

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
        | _ ->
            let line, col = currentPos afterCaret
            error "Expected integer exponent after '^' in unit expression" line col
    | _ -> success atom rest

and parseUnitAtom (tokens: Token list) : ParseResult<UnitExpr> =
    match peek tokens with
    | Some (TokIdent name) -> success (UnitNamed name) (advance tokens)
    | Some TokLParen ->
        parseUnitExpr (advance tokens) >>= fun expr afterExpr ->
        expect TokRParen afterExpr >>= fun _ remaining ->
        success expr remaining
    | _ ->
        let line, col = currentPos tokens
        error "Expected unit name or '(' in unit expression" line col

/// Parse a unit declaration: Unit meters  or  Unit velocity = meters / seconds
let parseUnitDecl (tokens: Token list) : ParseResult<Decl> =
    expectIdent tokens >>= fun name afterName ->
    match peek afterName with
    | Some (TokOp "=") ->
        parseUnitExpr (advance afterName) >>= fun expr remaining ->
        success (DeclUnit { Name = name; Definition = Some (UnitDerived expr) }) remaining
    | _ ->
        success (DeclUnit { Name = name; Definition = None }) afterName

let parseDecl (tokens: Token list) : ParseResult<Decl> =
    match peek tokens with
    | Some (TokKeyword KwImport) ->
        // import Providers.NetCDF as NetCDF
        // import Math
        parseQualifiedName (advance tokens) >>= fun qname afterName ->
        match peek afterName with
        | Some (TokKeyword KwAs) ->
            expectIdent (advance afterName) >>= fun alias remaining ->
            success (DeclImport (qname, ImportQualified (Some alias))) remaining
        | _ ->
            // import Providers.NetCDF  (no alias — use last segment)
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
            // let static x = ... → DeclStatic
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
            // static function f(...) = ... → DeclFunction with IsStatic = true
            parseFunctionDecl (advance afterStatic) >>= fun decl remaining ->
            match decl with
            | DeclFunction f ->
                success (DeclFunction { f with IsStatic = true }) remaining
            | other -> success other remaining
        | _ ->
            let (line, col) = currentPos afterStatic
            error "Expected 'function' after 'static'. For static values, use 'let static x = ...'" line col
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
        error (sprintf "Expected declaration but got %A" kind) line col
    | None ->
        error "Expected declaration but got EOF" 0 0

// ============================================================================
// Module and Program Parsing
// ============================================================================

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
    let tokens = skipNL tokens
    
    // Check for optional module declaration
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
                let (endLine, endCol) = currentPos remaining
                let span = { StartLine = startLine; StartCol = startCol
                             EndLine = endLine; EndCol = endCol; File = None }
                let located = { Value = decl; Span = span }
                decls <- located :: decls
                toks <- remaining
            | Error e ->
                errors <- e :: errors
                // Skip to next declaration boundary
                toks <- skipToNextDecl (advance toks)
    
    let modul = { Name = moduleName; Imports = []; Decls = List.rev decls }
    ((modul, List.rev errors), toks)

/// Non-recovering version for backward compatibility
let parseModule (tokens: Token list) : ParseResult<ModuleDecl> =
    let tokens = skipNL tokens
    
    // Check for optional module declaration
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
            let (endLine, endCol) = currentPos remaining
            let span = { StartLine = startLine; StartCol = startCol
                         EndLine = endLine; EndCol = endCol; File = None }
            let located = { Value = decl; Span = span }
            loop (located :: decls) remaining
    
    loop [] afterModule >>= fun decls remaining ->
    success {
        Name = moduleName
        Imports = []
        Decls = decls
    } remaining

let parseProgram (source: string) : Result<Program, ParseError> =
    let tokens = tokenizeWithNewlines source
    match parseModule tokens with
    | Ok (modul, _) -> Ok { Modules = [modul] }
    | Error e -> Error e

/// Parse multiple source files into a single Program.
/// Each entry is (fileName, sourceCode). If a source has a `module` declaration,
/// that name is used; otherwise the fileName (sans extension) becomes the module name.
let parseMultiSource (sources: (string * string) list) : Result<Program, ParseError> =
    let rec go acc remaining =
        match remaining with
        | [] -> Ok { Modules = List.rev acc }
        | (fileName, source) :: rest ->
            let tokens = tokenizeWithNewlines source
            match parseModule tokens with
            | Ok (modul, _) ->
                // If module name is "Main" (default) and fileName is provided, use fileName
                let modul' =
                    if modul.Name = ["Main"] && fileName <> "" && fileName <> "Main" then
                        { modul with Name = [fileName] }
                    else modul
                go (modul' :: acc) rest
            | Error e -> Error { e with Message = sprintf "[%s] %s" fileName e.Message }
    go [] sources

// ============================================================================
// Initialize Forward Reference
// ============================================================================

do parseExprRef := parseExprImpl
do parseBodyRef := parseInlineOrBlock
