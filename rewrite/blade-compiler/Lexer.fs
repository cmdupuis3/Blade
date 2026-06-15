// Blade-DSL Lexer
// Tokenizes Blade source code

module Blade.Lexer

open System
open System.Text

// ============================================================================
// Tokens
// ============================================================================

type TokenKind =
    // Literals
    | TokInt of int64
    | TokFloat of float
    | TokString of string
    | TokChar of char
    | TokBool of bool
    // Identifiers and keywords
    | TokIdent of string
    | TokKeyword of Keyword
    // Operators
    | TokOp of string
    | TokNamedInfix of string  // :name: syntax for custom infix operators
    // Punctuation
    | TokLParen        // (
    | TokRParen        // )
    | TokLBracket      // [
    | TokRBracket      // ]
    | TokLBrace        // {
    | TokRBrace        // }
    | TokComma         // ,
    | TokSemi          // ;
    | TokColon         // :
    | TokColonColon    // ::
    | TokDot           // .
    | TokDotDot        // ..

    | TokPipe          // |
    | TokUnderscore    // _
    | TokAt            // @
    | TokHash          // #
    | TokQuestion      // ?
    // Special
    | TokNewline
    | TokEOF
    | TokError of string

and Keyword =
    | KwLet
    | KwConst
    | KwMut
    | KwStatic
    | KwFunction
    | KwLambda
    | KwType
    | KwStruct
    | KwInterface
    | KwImpl
    | KwModule
    | KwFor
    | KwIf
    | KwThen
    | KwElse
    | KwMatch
    | KwWith
    | KwWhere
    | KwComm
    | KwOmp
    | KwCuda
    | KwReynolds
    | KwTrue
    | KwFalse
    | KwIn
    | KwImport
    | KwFrom
    | KwAs
    | KwVoid
    | KwUnit
    | KwArray
    | KwIdx
    | KwSymIdx
    | KwAntisymIdx
    | KwHermitianIdx
    | KwEnumIdx
    | KwDepIdx
    | KwRaggedIdx
    | KwMethodFor
    | KwObjectFor
    | KwRange
    | KwReverse
    | KwTranspose
    | KwHermitian
    | KwGram
    | KwDecompact
    | KwPure
    | KwCompute
    | KwGuard
    | KwSequence
    | KwReplicate
    | KwZip
    | KwStack
    | KwArity
    | KwNth
    | KwZero
    | KwRank
    | KwMask
    | KwIntersect
    | KwUnion
    | KwUnique
    | KwContains
    | KwGroupBy
    | KwGroupKeys
    | KwSort
    | KwReduce
    | KwConj
    | KwExtents
    | KwLike
    | KwPoly

type Token = {
    Kind: TokenKind
    Line: int
    Col: int
    Length: int
}

// ============================================================================
// Keyword Map
// ============================================================================

let keywords = 
    [ "let", KwLet
      "const", KwConst
      "mut", KwMut
      "static", KwStatic
      "function", KwFunction
      "lambda", KwLambda
      "type", KwType
      "struct", KwStruct
      "interface", KwInterface
      "impl", KwImpl
      "module", KwModule
      "for", KwFor
      "if", KwIf
      "then", KwThen
      "else", KwElse
      "match", KwMatch
      "with", KwWith
      "where", KwWhere
      "comm", KwComm
      "omp", KwOmp
      "cuda", KwCuda
      "reynolds", KwReynolds
      "true", KwTrue
      "false", KwFalse
      "True", KwTrue
      "False", KwFalse
      "in", KwIn
      "import", KwImport
      "from", KwFrom
      "as", KwAs
      "Void", KwVoid
      "Unit", KwUnit
      "Array", KwArray
      "Idx", KwIdx
      "SymIdx", KwSymIdx
      "AntisymIdx", KwAntisymIdx
      "HermitianIdx", KwHermitianIdx
      "EnumIdx", KwEnumIdx
      "DepIdx", KwDepIdx
      "RaggedIdx", KwRaggedIdx
      "method_for", KwMethodFor
      "object_for", KwObjectFor
      "range", KwRange
      "reverse", KwReverse
      "transpose", KwTranspose
      "hermitian", KwHermitian
      "gram", KwGram
      "decompact", KwDecompact
      "pure", KwPure
      "compute", KwCompute
      "guard", KwGuard
      "sequence", KwSequence
      "replicate", KwReplicate
      "zip", KwZip
      "stack", KwStack
      "arity", KwArity
      "nth", KwNth
      "zero", KwZero
      "rank", KwRank
      "mask", KwMask
      "intersect", KwIntersect
      "union", KwUnion
      "unique", KwUnique
      "contains", KwContains
      "group_by", KwGroupBy
      "group_keys", KwGroupKeys
      "sort", KwSort
      "reduce", KwReduce
      "conj", KwConj
      "extents", KwExtents
      "like", KwLike
      "Poly", KwPoly ]
    |> Map.ofList

// ============================================================================
// Multi-character Operators
// ============================================================================

let operators = 
    [ "<@>"; ">>="; "<&>"; "<&!>"; "<*>"; "<$>"; "<|>"; "<|:>"
      ">>@"; "@>>"; "|@>"; "::"; "->"; "=>"; ".."; "=="
      "!="; "<="; ">="; "&&"; "||"; ">>"
      "+="; "-="; "*="; "/="
      "|>"; "<-"
      "+"; "-"; "*"; "/"; "%"; "="; "<"; ">"; "!"; "^" ]
    |> List.sortByDescending String.length  // Match longer operators first

// ============================================================================
// Lexer State
// ============================================================================

type LexerState = {
    Source: string
    mutable Pos: int
    mutable Line: int
    mutable Col: int
    mutable Tokens: Token list
}

let createLexer source = {
    Source = source
    Pos = 0
    Line = 1
    Col = 1
    Tokens = []
}

// ============================================================================
// Character Utilities
// ============================================================================

let isDigit c = c >= '0' && c <= '9'
let isAlpha c = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')
let isAlphaNum c = isDigit c || isAlpha c
let isIdentStart c = isAlpha c || c = '_'
let isIdentChar c = isAlphaNum c || c = '_'
let isWhitespace c = c = ' ' || c = '\t' || c = '\r'

let peek (state: LexerState) =
    if state.Pos < state.Source.Length then
        Some state.Source.[state.Pos]
    else
        None

let peekN (state: LexerState) n =
    if state.Pos + n < state.Source.Length then
        Some state.Source.[state.Pos + n]
    else
        None

let peekStr (state: LexerState) len =
    if state.Pos + len <= state.Source.Length then
        state.Source.Substring(state.Pos, len)
    else
        ""

let advance (state: LexerState) =
    if state.Pos < state.Source.Length then
        let c = state.Source.[state.Pos]
        state.Pos <- state.Pos + 1
        if c = '\n' then
            state.Line <- state.Line + 1
            state.Col <- 1
        else
            state.Col <- state.Col + 1
        Some c
    else
        None

let emit (state: LexerState) startLine startCol kind =
    let len = state.Col - startCol
    let tok = { Kind = kind; Line = startLine; Col = startCol; Length = max 1 len }
    state.Tokens <- state.Tokens @ [tok]

// ============================================================================
// Token Scanners
// ============================================================================

let skipWhitespace (state: LexerState) =
    while (match peek state with Some c -> isWhitespace c | None -> false) do
        advance state |> ignore

let skipLineComment (state: LexerState) =
    // Consume //
    advance state |> ignore
    advance state |> ignore
    // Consume until newline or EOF
    while (match peek state with Some c -> c <> '\n' | None -> false) do
        advance state |> ignore

let skipBlockComment (state: LexerState) =
    // Consume /*
    advance state |> ignore
    advance state |> ignore
    let mutable depth = 1
    while depth > 0 do
        match peek state, peekN state 1 with
        | Some '/', Some '*' ->
            advance state |> ignore
            advance state |> ignore
            depth <- depth + 1
        | Some '*', Some '/' ->
            advance state |> ignore
            advance state |> ignore
            depth <- depth - 1
        | Some _, _ ->
            advance state |> ignore
        | None, _ ->
            depth <- 0  // EOF, stop

let scanNumber (state: LexerState) =
    let startLine = state.Line
    let startCol = state.Col
    let sb = StringBuilder()
    let mutable isFloat = false
    
    // Integer part
    while (match peek state with Some c -> isDigit c | None -> false) do
        sb.Append(advance state |> Option.get) |> ignore
    
    // Decimal part
    match peek state, peekN state 1 with
    | Some '.', Some c when isDigit c ->
        isFloat <- true
        sb.Append(advance state |> Option.get) |> ignore  // .
        while (match peek state with Some c -> isDigit c | None -> false) do
            sb.Append(advance state |> Option.get) |> ignore
    | _ -> ()
    
    // Exponent
    match peek state with
    | Some 'e' | Some 'E' ->
        isFloat <- true
        sb.Append(advance state |> Option.get) |> ignore
        match peek state with
        | Some '+' | Some '-' ->
            sb.Append(advance state |> Option.get) |> ignore
        | _ -> ()
        while (match peek state with Some c -> isDigit c | None -> false) do
            sb.Append(advance state |> Option.get) |> ignore
    | _ -> ()
    
    let text = sb.ToString()
    let kind =
        if isFloat then
            match Double.TryParse(text) with
            | true, v -> TokFloat v
            | false, _ -> TokError (sprintf "Invalid float: %s" text)
        else
            match Int64.TryParse(text) with
            | true, v -> TokInt v
            | false, _ -> TokError (sprintf "Invalid integer: %s" text)
    
    emit state startLine startCol kind

let scanString (state: LexerState) =
    let startLine = state.Line
    let startCol = state.Col
    let sb = StringBuilder()
    
    // Consume opening quote
    advance state |> ignore
    
    let mutable escaped = false
    let mutable closed = false
    
    while not closed do
        match peek state with
        | None ->
            emit state startLine startCol (TokError "Unterminated string")
            closed <- true
        | Some '\\' when not escaped ->
            escaped <- true
            advance state |> ignore
        | Some '"' when not escaped ->
            advance state |> ignore
            emit state startLine startCol (TokString (sb.ToString()))
            closed <- true
        | Some c ->
            if escaped then
                let ec = 
                    match c with
                    | 'n' -> '\n'
                    | 't' -> '\t'
                    | 'r' -> '\r'
                    | '\\' -> '\\'
                    | '"' -> '"'
                    | _ -> c
                sb.Append(ec) |> ignore
                escaped <- false
            else
                sb.Append(c) |> ignore
            advance state |> ignore

let scanChar (state: LexerState) =
    let startLine = state.Line
    let startCol = state.Col
    
    // Consume opening quote
    advance state |> ignore
    
    let c =
        match peek state with
        | Some '\\' ->
            advance state |> ignore
            match peek state with
            | Some 'n' -> advance state |> ignore; '\n'
            | Some 't' -> advance state |> ignore; '\t'
            | Some 'r' -> advance state |> ignore; '\r'
            | Some '\\' -> advance state |> ignore; '\\'
            | Some '\'' -> advance state |> ignore; '\''
            | Some c -> advance state |> ignore; c
            | None -> '\000'
        | Some c ->
            advance state |> ignore
            c
        | None -> '\000'
    
    match peek state with
    | Some '\'' ->
        advance state |> ignore
        emit state startLine startCol (TokChar c)
    | _ ->
        emit state startLine startCol (TokError "Unterminated character literal")

let scanIdentOrKeyword (state: LexerState) =
    let startLine = state.Line
    let startCol = state.Col
    let sb = StringBuilder()
    
    while (match peek state with Some c -> isIdentChar c | None -> false) do
        sb.Append(advance state |> Option.get) |> ignore
    
    let text = sb.ToString()
    let kind =
        match Map.tryFind text keywords with
        | Some kw -> 
            match kw with
            | KwTrue -> TokBool true
            | KwFalse -> TokBool false
            | _ -> TokKeyword kw
        | None -> TokIdent text
    
    emit state startLine startCol kind

let tryOperator (state: LexerState) =
    // Try to match longest operator first
    operators
    |> List.tryFind (fun op ->
        let s = peekStr state op.Length
        s = op)

let scanOperator (state: LexerState) =
    let startLine = state.Line
    let startCol = state.Col
    
    match tryOperator state with
    | Some op ->
        for _ in 1..op.Length do
            advance state |> ignore
        emit state startLine startCol (TokOp op)
    | None ->
        // Single character operator
        let c = advance state |> Option.get
        emit state startLine startCol (TokOp (string c))

// ============================================================================
// Main Lexer
// ============================================================================

let scanToken (state: LexerState) =
    skipWhitespace state
    
    let startLine = state.Line
    let startCol = state.Col
    
    match peek state with
    | None ->
        emit state startLine startCol TokEOF
        false
    
    | Some '\n' ->
        advance state |> ignore
        emit state startLine startCol TokNewline
        true
    
    | Some '/' ->
        match peekN state 1 with
        | Some '/' ->
            skipLineComment state
            true
        | Some '*' ->
            skipBlockComment state
            true
        | _ ->
            scanOperator state
            true
    
    | Some '"' ->
        scanString state
        true
    
    | Some '\'' ->
        scanChar state
        true
    
    | Some c when isDigit c ->
        scanNumber state
        true
    
    | Some c when isIdentStart c ->
        scanIdentOrKeyword state
        true
    
    | Some '(' ->
        advance state |> ignore
        emit state startLine startCol TokLParen
        true
    
    | Some ')' ->
        advance state |> ignore
        emit state startLine startCol TokRParen
        true
    
    | Some '[' ->
        // Check for bracketed operators: [op] for outer product mode
        // Supported: arithmetic (+,-,*,/,%,^), comparison (==,!=,<,>,<=,>=), logical (&&,||)
        let tryBracketedOp () =
            // Try two-char ops first
            match peekN state 1, peekN state 2, peekN state 3 with
            | Some '=', Some '=', Some ']' -> Some "[==]"  // equality
            | Some '!', Some '=', Some ']' -> Some "[!=]"  // not equal
            | Some '<', Some '=', Some ']' -> Some "[<=]"  // less equal
            | Some '>', Some '=', Some ']' -> Some "[>=]"  // greater equal
            | Some '&', Some '&', Some ']' -> Some "[&&]"  // logical and
            | Some '|', Some '|', Some ']' -> Some "[||]"  // logical or
            | _ ->
                // Try single-char ops
                match peekN state 1, peekN state 2 with
                | Some '+', Some ']' -> Some "[+]"
                | Some '-', Some ']' -> Some "[-]"
                | Some '*', Some ']' -> Some "[*]"
                | Some '/', Some ']' -> Some "[/]"
                | Some '%', Some ']' -> Some "[%]"
                | Some '^', Some ']' -> Some "[^]"
                | Some '<', Some ']' -> Some "[<]"
                | Some '>', Some ']' -> Some "[>]"
                | _ -> None
        
        match tryBracketedOp () with
        | Some opStr ->
            for _ in 1..opStr.Length do
                advance state |> ignore
            emit state startLine startCol (TokOp opStr)
        | None ->
            advance state |> ignore
            emit state startLine startCol TokLBracket
        true
    
    | Some ']' ->
        advance state |> ignore
        emit state startLine startCol TokRBracket
        true
    
    | Some '{' ->
        advance state |> ignore
        emit state startLine startCol TokLBrace
        true
    
    | Some '}' ->
        advance state |> ignore
        emit state startLine startCol TokRBrace
        true
    
    | Some ',' ->
        advance state |> ignore
        emit state startLine startCol TokComma
        true
    
    | Some ';' ->
        advance state |> ignore
        emit state startLine startCol TokSemi
        true
    
    | Some ':' ->
        match peekN state 1 with
        | Some ':' ->
            advance state |> ignore
            advance state |> ignore
            emit state startLine startCol TokColonColon
        | Some c when Char.IsLetter(c) || c = '_' ->
            // Potential named infix: :name:
            advance state |> ignore  // consume first ':'
            let nameStart = state.Pos
            // Collect the identifier
            while state.Pos < state.Source.Length && 
                  (let ch = state.Source.[state.Pos] in Char.IsLetterOrDigit(ch) || ch = '_') do
                advance state |> ignore
            let name = state.Source.Substring(nameStart, state.Pos - nameStart)
            // Check for closing ':'
            match peek state with
            | Some ':' ->
                advance state |> ignore  // consume closing ':'
                emit state startLine startCol (TokNamedInfix name)
            | _ ->
                // Not a named infix, emit colon and let identifier be re-lexed
                // This is tricky - we've consumed too much. For now, error.
                emit state startLine startCol (TokError (sprintf "Expected ':' after :%s" name))
        | _ ->
            advance state |> ignore
            emit state startLine startCol TokColon
        true
    
    | Some '.' ->
        match peekN state 1 with
        | Some '.' ->
            advance state |> ignore
            advance state |> ignore
            emit state startLine startCol TokDotDot
        | _ ->
            advance state |> ignore
            emit state startLine startCol TokDot
        true
    
    | Some '|' ->
        match peekN state 1 with
        | Some '@' when peekN state 2 = Some '>' ->
            advance state |> ignore  // |
            advance state |> ignore  // @
            advance state |> ignore  // >
            emit state startLine startCol (TokOp "|@>")
        | Some '>' ->
            advance state |> ignore
            advance state |> ignore
            emit state startLine startCol (TokOp "|>")
        | Some '|' ->
            advance state |> ignore
            advance state |> ignore
            emit state startLine startCol (TokOp "||")
        | _ ->
            advance state |> ignore
            emit state startLine startCol TokPipe
        true
    
    | Some '_' ->
        match peekN state 1 with
        | Some c when isIdentChar c ->
            scanIdentOrKeyword state
        | _ ->
            advance state |> ignore
            emit state startLine startCol TokUnderscore
        true
    
    | Some '@' ->
        // Check for @>> operator before treating @ as standalone
        match peekN state 1, peekN state 2 with
        | Some '>', Some '>' ->
            advance state |> ignore  // @
            advance state |> ignore  // >
            advance state |> ignore  // >
            emit state startLine startCol (TokOp "@>>")
        | _ ->
            advance state |> ignore
            emit state startLine startCol TokAt
        true
    
    | Some '#' ->
        advance state |> ignore
        emit state startLine startCol TokHash
        true
    
    | Some '?' ->
        advance state |> ignore
        emit state startLine startCol TokQuestion
        true
    
    | Some _ ->
        scanOperator state
        true

let tokenize source =
    let state = createLexer source
    while scanToken state do ()
    state.Tokens

/// Filter newlines based on delimiter depth
/// Newlines inside (), [], {} are removed (treated as whitespace)
/// Newlines at depth 0 are kept (statement terminators)
/// Consecutive newlines are collapsed to one
/// Leading/trailing newlines around delimiters are removed
let tokenizeWithNewlines source =
    let tokens = tokenize source
    let mutable depth = 0
    let mutable lastWasNewline = false
    let mutable lastWasOpen = false  // after (, [, {
    
    tokens |> List.filter (fun t ->
        match t.Kind with
        | TokLParen | TokLBracket | TokLBrace ->
            depth <- depth + 1
            lastWasNewline <- false
            lastWasOpen <- true
            true
        | TokRParen | TokRBracket | TokRBrace ->
            depth <- max 0 (depth - 1)
            lastWasNewline <- false
            lastWasOpen <- false
            true
        | TokNewline ->
            if depth > 0 then
                // Inside delimiters - skip newline
                false
            elif lastWasNewline || lastWasOpen then
                // Collapse consecutive newlines, skip after open delimiter
                false
            else
                lastWasNewline <- true
                lastWasOpen <- false
                true
        | TokEOF ->
            // Don't update flags for EOF
            true
        | _ ->
            lastWasNewline <- false
            lastWasOpen <- false
            true)

// Filter out all newlines for simpler parsing (legacy mode)
let tokenizeFiltered source =
    tokenize source
    |> List.filter (fun t -> 
        match t.Kind with 
        | TokNewline -> false 
        | _ -> true)
