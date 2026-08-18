// Blade-DSL Parser: top-level declaration grammar and the public entry points
// (parseProgram / parseProgramWithFile / parseMultiSource). The `do` wire-ups
// at the bottom install the ParserGrammar implementations into ParserCore's
// forward-reference cells; nothing parses until this file has compiled.
module Blade.Parser

open Blade.Ast
open Blade.Lexer
open Blade.ParserCore
open Blade.ParserTypes
open Blade.ParserPatterns
open Blade.ParserGrammar

// Initialize the forward-reference cells. A module-level `do` alone is NOT
// enough since the split: the cells live in ParserCore.fs while the wire-ups
// live here, and .NET only runs this file's initializer when one of its OWN
// values is touched -- calling the entry-point FUNCTIONS does not qualify, so
// a bare `do` would leave the cells on their "not initialized" dummies
// (BL9001 in every consumer). Each public entry point therefore installs the
// grammar explicitly; the assignment is idempotent and costs two writes.
let internal installGrammar () =
    parseExprRef := parseExprImpl
    parseBodyRef := parseInlineOrBlock

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
    // The removed imperative `for IDENT in ...`, in TOP-LEVEL position. This
    // must precede the bare-expression arm below: `for` also opens the
    // surviving loop-object form, so without the guard the expression parser
    // would swallow the shape and report something downstream and confusing
    // instead of the BL1003 steer. Same predicate and same message as
    // parseBlock's statement-position shell.
    | Some (TokKeyword KwFor) when isImperativeForIn tokens ->
        let line, col = currentPos tokens
        errorC "BL1003" forInRemovedMsg line col
    | Some kind ->
        // A bare top-level EXPRESSION statement. Blade has no `main`: a
        // top-level expression evaluates in declaration order and auto-prints,
        // exactly like a top-level `let`. That equivalence is the
        // implementation -- the expression desugars to a let over a
        // synthesized `__exprN` name, which is the same move the REPL/notebook
        // lane makes with `let __cellN = `. Because the desugar happens HERE,
        // every downstream phase (typecheck, lowering, codegen, the
        // interpreter, and the auto-printer) inherits the feature through the
        // binding path it already has, so the codegen/interpreter twins cannot
        // drift on it.
        let line, col = currentPos tokens
        let classic () =
            error (sprintf "Expected declaration but got %s" (describeToken kind)) line col
        match parseExprImpl tokens with
        | Ok (expr, remaining) ->
            let st = PS.Cur
            st.TopExprCounter <- st.TopExprCounter + 1
            let name = sprintf "__expr%d" st.TopExprCounter
            success (DeclLet { Mutability = BindLet
                               Pattern = mkPat (headSpan tokens) (PatVar name)
                               Type = None
                               Value = expr }) remaining
        // The expression parser rejected the very first token, so this was
        // never an expression: keep the historical declaration-position
        // wording, which names what the position actually wanted. Only when it
        // got PAST that token does its own error describe the input better.
        | Error e when e.Line = line && e.Col = col -> classic ()
        | Error e -> Error e
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
    installGrammar ()
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
                             EndLine = endLine; EndCol = endCol; File = PS.Cur.File }
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
    installGrammar ()
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
                         EndLine = endLine; EndCol = endCol; File = PS.Cur.File }
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
    PS.Cur.File <- file
    try
        let tokens = tokenizeWithNewlines source
        setEofFrom tokens
        match parseModule tokens with
        | Ok (modul, _) -> Ok { Modules = [modul] }
        | Error e -> Error e
    finally
        PS.Cur.File <- None

/// Backward-compatible entry point: parse a single anonymous source (File=None).
let parseProgram (source: string) : Result<Program, ParseError> =
    parseProgramWithFile None source

/// Parse multiple source files into a single Program.
/// Each entry is (fileName, sourceCode). If a source has a `module` declaration,
/// that name is used; otherwise the fileName (sans extension) becomes the module name.
let parseMultiSource (sources: (string * string) list) : Result<Program, ParseError> =
    let rec go acc remaining =
        match remaining with
        | [] -> PS.Cur.File <- None; Ok { Modules = List.rev acc }
        | (fileName, source) :: rest ->
            // Stamp this file into every span the parser builds for it.
            PS.Cur.File <- (if fileName <> "" then Some fileName else None)
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
                PS.Cur.File <- None
                Error { e with Message = sprintf "[%s] %s" fileName e.Message }
    go [] sources

// (The forward-reference wire-up lives at the TOP of this file -- see
// installGrammar -- because each entry point must run it explicitly.)

do installGrammar ()

// Re-exported so external modules keep a single Blade.Parser surface.
let diagnosticOfParseError = ParserCore.diagnosticOfParseError
