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
// Binding positions come from a parallel walk of the UNTYPED AST joined by
// (scope, name) in declaration order (a convention from before TypedExpr
// spans went live). Compiler-generated declarations (ML/PPL/grad expansion)
// find no source span and are silently skipped.
//
// calls[]: one entry per BUILTIN call site — the concrete (monomorphized)
// argument/result types the checker resolved there, rendered in the
// compiler's `Array<Elem like Idx...>` notation. Collected by walking the
// zonked typed tree directly (TypedExpr.Span is live); synthesized nodes
// (eta-expanded kernels, checkExpr fast paths) carry noSpan and are
// skipped. The editor renders these under the abstract builtin signature:
//   "calls": [ { name, line, col, endLine, endCol, args: [..], ret } ]

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
    /// BLxxxx diagnostic code; "" = none (the JSON field is omitted).
    Code: string
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
    /// Provenance for a top-level provider read (`let x = store.vars.v |>
    /// alias.read`): (store binding name, "vars.v" / "dims.v"). None for
    /// every non-provider binding. Surfaced as a "from …" line in the hover.
    ProviderRead: (string * string) option
}

// A single member of a loaded provider store (a `dims` or `vars` field),
// with its type rendered in the provider's named index types (Idx<Y>, ...).
type private ProviderMemberInfo = {
    MName: string
    MType: string
}

// A provided named index type (e.g. `Idx<Y>` from a stored dimension), with
// its extent when statically known.
type private ProviderIndexInfo = {
    IName: string
    IExtent: int64 option
}

// One `let store = alias.load("path")` binding and the structure the provider
// derived from the data file: its index types plus the `dims` / `vars`
// members. Types are structural only (no file attributes). Emitted under
// `providers[]` so the editor can hover members, the store handle, and the
// alias — none of which are ordinary bindings.
type private ProviderInfo = {
    Store: string
    Alias: string
    Provider: string
    Path: string
    PLine: int
    PCol: int
    IndexTypes: ProviderIndexInfo list
    Dims: ProviderMemberInfo list
    Vars: ProviderMemberInfo list
}

// One builtin call site with its concrete (monomorphized) instantiation:
// argument and result types as the checker resolved them there. Types are
// pre-rendered strings in the compiler's concrete notation.
type private CallInfo = {
    CName: string
    CLine: int
    CCol: int
    CEndLine: int
    CEndCol: int
    CArgs: string list
    CRet: string
}

/// Clamp a span to 1-based sanity; noSpan (all zeros) becomes 1:1-1:1.
let private clampSpan (s: Span) =
    let line = max 1 s.StartLine
    let col = max 1 s.StartCol
    let endLine = max line s.EndLine
    let endCol = if s.EndCol >= 1 then s.EndCol else col
    (line, col, endLine, endCol)

let private renderJson (diags: Diag list) (bindings: BindingInfo list) (providers: ProviderInfo list) (calls: CallInfo list) =
    let sb = StringBuilder()
    sb.Append "{\"version\":1,\"diagnostics\":[" |> ignore
    diags
    |> List.iteri (fun i d ->
        if i > 0 then sb.Append ',' |> ignore
        sb.AppendFormat(
            "{{\"severity\":\"{0}\",\"line\":{1},\"col\":{2},\"endLine\":{3},\"endCol\":{4},\"message\":\"{5}\"",
            d.Severity, d.Line, d.Col, d.EndLine, d.EndCol, jsonEscape d.Message) |> ignore
        if d.Code <> "" then
            sb.AppendFormat(",\"code\":\"{0}\"", jsonEscape d.Code) |> ignore
        sb.Append '}' |> ignore)
    sb.Append "],\"bindings\":[" |> ignore
    bindings
    |> List.iteri (fun i b ->
        if i > 0 then sb.Append ',' |> ignore
        sb.AppendFormat(
            "{{\"name\":\"{0}\",\"kind\":\"{1}\",\"line\":{2},\"col\":{3},\"type\":\"{4}\"",
            jsonEscape b.Name, jsonEscape b.Kind, b.Line, b.Col, jsonEscape b.TypeStr) |> ignore
        if b.Doc <> "" then
            sb.AppendFormat(",\"doc\":\"{0}\"", jsonEscape b.Doc) |> ignore
        match b.ProviderRead with
        | Some (store, memberPath) ->
            sb.AppendFormat(
                ",\"providerRead\":{{\"store\":\"{0}\",\"member\":\"{1}\"}}",
                jsonEscape store, jsonEscape memberPath) |> ignore
        | None -> ()
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
    sb.Append "],\"providers\":[" |> ignore
    let appendMembers (label: string) (ms: ProviderMemberInfo list) =
        sb.AppendFormat(",\"{0}\":[", label) |> ignore
        ms
        |> List.iteri (fun j m ->
            if j > 0 then sb.Append ',' |> ignore
            sb.AppendFormat(
                "{{\"name\":\"{0}\",\"type\":\"{1}\"}}", jsonEscape m.MName, jsonEscape m.MType) |> ignore)
        sb.Append ']' |> ignore
    providers
    |> List.iteri (fun i p ->
        if i > 0 then sb.Append ',' |> ignore
        sb.AppendFormat(
            "{{\"store\":\"{0}\",\"alias\":\"{1}\",\"provider\":\"{2}\",\"path\":\"{3}\",\"line\":{4},\"col\":{5}",
            jsonEscape p.Store, jsonEscape p.Alias, jsonEscape p.Provider, jsonEscape p.Path, p.PLine, p.PCol) |> ignore
        sb.Append ",\"indexTypes\":[" |> ignore
        p.IndexTypes
        |> List.iteri (fun j ix ->
            if j > 0 then sb.Append ',' |> ignore
            sb.AppendFormat("{{\"name\":\"{0}\"", jsonEscape ix.IName) |> ignore
            match ix.IExtent with
            | Some e -> sb.AppendFormat(",\"extent\":{0}", e) |> ignore
            | None -> ()
            sb.Append '}' |> ignore)
        sb.Append ']' |> ignore
        appendMembers "dims" p.Dims
        appendMembers "vars" p.Vars
        sb.Append '}' |> ignore)
    sb.Append "],\"calls\":[" |> ignore
    calls
    |> List.iteri (fun i c ->
        if i > 0 then sb.Append ',' |> ignore
        sb.AppendFormat(
            "{{\"name\":\"{0}\",\"line\":{1},\"col\":{2},\"endLine\":{3},\"endCol\":{4},\"ret\":\"{5}\",\"args\":[",
            jsonEscape c.CName, c.CLine, c.CCol, c.CEndLine, c.CEndCol, jsonEscape c.CRet) |> ignore
        c.CArgs
        |> List.iteri (fun j a ->
            if j > 0 then sb.Append ',' |> ignore
            sb.AppendFormat("\"{0}\"", jsonEscape a) |> ignore)
        sb.Append "]}" |> ignore)
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

/// Public: also the REPL's display printer (Cli.fs) — index-name-aware
/// rendering beats bare ppIRType for any type embedding named index types.
let ppType (t: IRType) : string =
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
// Abstract (type-variable) rendering — shared with the REPL (Cli.ReplTypes).
// Post-zonk, surviving IRTInfer vars are exactly the HM-polymorphic positions
// of a generic signature; rendering them as `T?10000` leaks inference ids
// into hovers. Name them from the source annotations where possible (T,
// T^2), fresh letters otherwise.
// ----------------------------------------------------------------------------

/// Does an unresolved inference variable survive anywhere in the type?
let rec hasInfer (t: IRType) : bool =
    match t with
    | IRTInfer _ -> true
    | IRTTuple ts -> ts |> List.exists hasInfer
    | IRTComputation t | IRTPoly (t, _)
    | IRTUnitAnnotated (t, _) | IRTIdxTagged (t, _) -> hasInfer t
    | IRTDist (_, elem, _) -> hasInfer elem
    | IRTLoop lt ->
        (lt.ArrayTypes |> List.exists hasInfer)
        || (lt.KernelType |> Option.exists hasInfer)
    | IRTArrow (slots, ret, _) ->
        hasInfer ret
        || (slots |> List.exists (function SVal t -> hasInfer t | _ -> false))
    | _ -> false

/// Replace surviving inference variables with named placeholders (IRTNamed
/// prints as itself), so the standard printer renders them as abstract type
/// variables.
let rec nameInfers (nameOf: int -> string) (t: IRType) : IRType =
    match t with
    | IRTInfer id -> IRTNamed (nameOf id)
    | IRTTuple ts -> IRTTuple (ts |> List.map (nameInfers nameOf))
    | IRTComputation t -> IRTComputation (nameInfers nameOf t)
    | IRTPoly (t, v) -> IRTPoly (nameInfers nameOf t, v)
    | IRTUnitAnnotated (t, u) -> IRTUnitAnnotated (nameInfers nameOf t, u)
    | IRTIdxTagged (t, r) -> IRTIdxTagged (nameInfers nameOf t, r)
    | IRTDist (o, elem, axes) -> IRTDist (o, nameInfers nameOf elem, axes)
    | IRTLoop lt ->
        IRTLoop { lt with
                    ArrayTypes = lt.ArrayTypes |> List.map (nameInfers nameOf)
                    KernelType = lt.KernelType |> Option.map (nameInfers nameOf) }
    | IRTArrow (slots, ret, ident) ->
        let slot = function SVal t -> SVal (nameInfers nameOf t) | s -> s
        IRTArrow (slots |> List.map slot, nameInfers nameOf ret, ident)
    | _ -> t

/// Best-effort recovery of the SOURCE names of abstract type variables: walk
/// an annotation in parallel with its resolved type, recording (inference id
/// -> declared name) wherever a type-variable position is still unresolved.
/// `T^k` keeps its arity suffix. A bare `T` parses as TyNamed — if that
/// position resolved to an inference var, it was a type variable, so the
/// name applies.
let rec collectVarNames (ann: TypeExpr) (t: IRType) : (int * string) list =
    match ann, t with
    | TyVar (name, arity), IRTInfer id ->
        let disp = match arity with
                   | Some k when k > 0 -> sprintf "%s^%d" name k
                   | _ -> name
        [(id, disp)]
    | TyNamed (name, []), IRTInfer id -> [(id, name)]
    | TyAbstractArray (TyVar (name, _), _, _), IRTInfer id -> [(id, name)]
    | TyTuple anns, IRTTuple ts when anns.Length = ts.Length ->
        List.zip anns ts |> List.collect (fun (a, ty) -> collectVarNames a ty)
    | TyFunc (args, ret), IRTArrow (slots, res, _) ->
        let vals = slots |> List.choose (function SVal ty -> Some ty | _ -> None)
        (if args.Length = vals.Length then
            List.zip args vals |> List.collect (fun (a, ty) -> collectVarNames a ty)
         else [])
        @ collectVarNames ret res
    | TyArray (elem, _), ArrayElem arr -> collectVarNames elem arr.ElemType
    | _ -> []

/// Fresh-letter pool for inference vars no source annotation names.
let private typeVarPool =
    seq { yield! ["T"; "U"; "V"; "W"]
          for i in 1 .. 1000 -> sprintf "T%d" i }

/// A per-signature abstract-type renderer: consistent letters across every
/// type it prints (a function's params + return share one namespace).
/// `seed` pre-names inference ids recovered from source annotations.
let abstractRenderer (seed: (int * string) seq) : IRType -> string =
    let named = Dictionary<int, string>()
    for (id, n) in seed do named.[id] <- n
    let used = HashSet<string>(named.Values)
    let nameOf id =
        match named.TryGetValue id with
        | true, n -> n
        | _ ->
            let n = typeVarPool |> Seq.find (fun c -> not (used.Contains c))
            used.Add n |> ignore
            named.[id] <- n
            n
    fun t -> ppType (if hasInfer t then nameInfers nameOf t else t)

// ----------------------------------------------------------------------------
// Concrete call-site instantiations (calls[]). The zonked typed tree carries
// full monomorphized types and live source spans, so builtin applications can
// be reported by a plain walk — the editor shows the concrete instantiation
// under the abstract signature from its static builtin table. The abstract
// and concrete signatures line up positionally (same argument order), but the
// concrete side uses the compiler's own notation: `Array<Elem like Idx...>`
// arrays (named index types preserved) and curried arrows for functions.
// ----------------------------------------------------------------------------

/// Every index slot embedded in a type — the nominal-name walk (indexNamesOf)
/// only reports slots that carry a source-level name.
let rec private allIndicesOf (t: IRType) : IRIndexType list =
    match t with
    | ArrayElem arr -> arr.IndexTypes @ allIndicesOf arr.ElemType
    | IRTTuple ts -> ts |> List.collect allIndicesOf
    | IRTComputation inner | IRTPoly (inner, _) | IRTUnitAnnotated (inner, _) -> allIndicesOf inner
    | IRTGroupKeys (outer, source, _) -> [outer; source]
    | IRTDist (_, elem, axes) -> axes @ allIndicesOf elem
    | IRTArrow (slots, ret, _) ->
        (slots |> List.collect (function SIdx i | SIdxVirt i -> [i] | SVal v -> allIndicesOf v))
        @ allIndicesOf ret
    | _ -> []

/// Index-name map for CONCRETE rendering. A slot keeps its nominal name when
/// it has one; otherwise its extent is folded to a literal when statically
/// evaluable, and rendered `_` when it is not. Feeding this to the compiler's
/// own ppIndexTypeIn (which consults the map before the extent) keeps the
/// symmetry/irreps spellings while replacing internal extent params
/// (`__ngroups`, `v12`) and the bare `?` with a wildcard a reader can parse.
let private concreteNames (ts: IRType list) : Map<IRId, string> =
    let nominal = ts |> List.collect indexNamesOf |> Map.ofList
    ts
    |> List.collect allIndicesOf
    |> List.fold (fun (acc: Map<IRId, string>) idx ->
        if Map.containsKey idx.Id acc then acc
        else
            let text =
                match tryEvalIntIR idx.Extent with
                | Some n -> string n
                | None -> "_"
            Map.add idx.Id text acc) nominal

/// Concrete-type rendering: `Array<Elem like Idx...>` for arrays, curried
/// arrows for function types, structural tuples/Computation. Falls back to
/// the named-index printer for everything else.
let rec private ppConcrete (names: Map<IRId, string>) (t: IRType) : string =
    match t with
    | ArrayElem arr ->
        let indices = arr.IndexTypes |> List.map (ppIndexTypeIn names) |> String.concat ", "
        sprintf "Array<%s like %s>" (ppConcrete names arr.ElemType) indices
    | FuncElem (paramTys, retTy) ->
        let piece ty =
            match ty with
            | FuncElem _ -> sprintf "(%s)" (ppConcrete names ty)
            | _ -> ppConcrete names ty
        String.concat " -> " ((paramTys |> List.map piece) @ [ppConcrete names retTy])
    | IRTTuple ts -> sprintf "(%s)" (ts |> List.map (ppConcrete names) |> String.concat ", ")
    | IRTComputation inner -> sprintf "Computation<%s>" (ppConcrete names inner)
    // GroupKeys/Dist render their axes through the CONTEXT-FREE printer
    // upstream, which would reintroduce internal extent params; re-render them
    // here so the whole tooltip obeys the name/literal/`_` rule.
    | IRTGroupKeys (outer, source, _) ->
        sprintf "GroupKeys<%s, %s>" (ppIndexTypeIn names outer) (ppIndexTypeIn names source)
    | IRTDist (order, elem, axes) ->
        sprintf "Dist<%d, %s like %s>"
            order (ppConcrete names elem)
            (axes |> List.map (ppIndexTypeIn names) |> String.concat ", ")
    | other -> ppIRTypeIn names other

/// The builtin a typed node is an application of, with its argument nodes in
/// source order — None for everything that is not a builtin call. Operators
/// are not covered; the import-gated PPL/ML surfaces are collected separately
/// (collectFormerCalls) since they rewrite away before checking.
///
/// `hermitian(A)` is the one builtin the PARSER rewrites into other builtins
/// (conj of a transpose, both nodes sharing the whole call's span). Reporting
/// the expansion would both mis-name the call and duplicate it, so the pair is
/// matched here as a unit and its inner transpose is skipped by the walker.
let private builtinCallOf (te: TypedExpr) : (string * TypedExpr list) option =
    match te.Kind with
    // Array operands conjugate through TExprArrayConjugate (the whole-array
    // eager form); scalar ones keep the unary op. Match both shapes.
    | TExprArrayConjugate ({ Kind = TExprTranspose (a, 0, 1) } as inner)
    | TExprUnaryOp (OpConj, ({ Kind = TExprTranspose (a, 0, 1) } as inner))
        when inner.Span = te.Span && te.Span.StartLine > 0 -> Some ("hermitian", [a])
    | TExprArrayConjugate a -> Some ("conj", [a])
    | TExprMethodFor info -> Some ("method_for", info.Arrays)
    | TExprObjectFor info -> Some ("object_for", [info.Kernel])
    | TExprPure e -> Some ("pure", [e])
    | TExprCompute e -> Some ("compute", [e])
    | TExprRead e -> Some ("read", [e])
    | TExprGuard (c, b) -> Some ("guard", [c; b])
    | TExprReynolds (k, _) -> Some ("reynolds", [k])
    | TExprZero -> Some ("zero", [])
    | TExprRank e -> Some ("rank", [e])
    | TExprArity _ -> Some ("arity", [])
    | TExprExtents a -> Some ("extents", [a])
    | TExprReduce (a, k, i) -> Some ("reduce", [a; k] @ Option.toList i)
    | TExprMask (a, p) -> Some ("mask", [a; p])
    | TExprCompound (d, m) -> Some ("compound", [d; m])
    | TExprZip es -> Some ("zip", es)
    | TExprStack es -> Some ("stack", es)
    | TExprSort (a, k) -> Some ("sort", [a; k])
    | TExprUnique a -> Some ("unique", [a])
    | TExprIntersect (a, b) -> Some ("intersect", [a; b])
    | TExprUnion (a, b) -> Some ("union", [a; b])
    | TExprContains (a, v) -> Some ("contains", [a; v])
    | TExprGroupBy (v, g) -> Some ("group_by", [v; g])
    | TExprGroupKeys ks -> Some ("group_keys", ks)
    | TExprTranspose (a, _, _) -> Some ("transpose", [a])
    | TExprDecompact (a, _) -> Some ("decompact", [a])
    | TExprGram (l, r, _) -> Some ("gram", [l; r])
    | TExprSequence es -> Some ("sequence", es)
    | TExprReplicate (c, b) -> Some ("replicate", [c; b])
    | TExprComplexLit (re, im) -> Some ("complex", [re; im])
    | TExprProdSum args -> Some ("prodsum", args)
    | TExprFillRandom m -> Some ("fill_random", [m])
    | TExprUnaryOp (OpMath name, a) -> Some (name, [a])
    | TExprUnaryOp (OpConj, a) -> Some ("conj", [a])
    | _ -> None

/// One call entry from a name, span and already-typed operands.
let private mkCall (name: string) (span: Span) (argTys: IRType list) (retTy: IRType) : CallInfo =
    let (line, col, endLine, endCol) = clampSpan span
    let names = concreteNames (retTy :: argTys)
    { CName = name; CLine = line; CCol = col
      CEndLine = endLine; CEndCol = endCol
      CArgs = argTys |> List.map (ppConcrete names)
      CRet = ppConcrete names retTy }

/// Walk the zonked typed program collecting every builtin call site with a
/// live source span (synthesized nodes carry noSpan and are skipped).
let private collectCalls (tp: TypedProgram) : CallInfo list =
    let acc = ResizeArray<CallInfo>()
    let rec walk (te: TypedExpr) =
        let hit = builtinCallOf te
        (match hit with
         | Some (name, args) when te.Span.StartLine > 0 ->
             acc.Add (mkCall name te.Span (args |> List.map (fun a -> a.Type)) te.Type)
         | _ -> ())
        // `hermitian` consumed its own transpose node above; recursing through
        // typedExprChildren would report that expansion a second time.
        match hit, te.Kind with
        | Some ("hermitian", args), _ -> for a in args do walk a
        | _ -> for c in Blade.TypeCheck.typedExprChildren te do walk c
    for m in tp.Modules do
        for d in m.Decls do
            match d with
            | TDeclLet b | TDeclStatic b -> walk b.Value
            | TDeclFunction f -> walk f.Body
            | TDeclImpl impl -> for f in impl.Methods do walk f.Body
            | _ -> ()
    // An eta-expanded former and the kernel it wraps can land on one span
    // (both are the same source text); report each name once per position.
    acc
    |> Seq.distinctBy (fun c -> (c.CName, c.CLine, c.CCol, c.CEndLine, c.CEndCol))
    |> List.ofSeq

// ---- Import-gated surfaces (ppl.* / ml.*) ----------------------------------
// These formers are rewritten by their elaborators BEFORE the checker runs, so
// no typed node carries their name and the walk above cannot see them. They
// are recovered from the PRE-elaboration AST (which ideCheck still holds)
// joined to the checked types: a former is the entire RHS of a `let`, so the
// call's result type is the type inferred for the name it binds, and its
// arguments are module-level names whose types are likewise known. Arguments
// that are static integers show their VALUE (the moment order / degree is what
// the shape depends on), matching the literal-extent rule above.

/// Binding types by name from the checked program. Module-level bindings win;
/// function parameters and body lets are added underneath so a former called
/// inside a function can still type its operands (a shadowed name may pick the
/// outer type — acceptable for a tooltip, and the common case is distinct).
let private moduleBindingTypes (tp: TypedProgram) : Map<string, IRType> =
    let acc = Dictionary<string, IRType>()
    let addLocal (n: string) (t: IRType) = if not (acc.ContainsKey n) then acc.[n] <- t
    let rec localsOf (stmts: TypedStmt list) =
        for s in stmts do
            match s with
            | TStmtLet b ->
                addLocal b.Name b.Type
                for (n, _, t) in b.SubBindings do addLocal n t
            | TStmtForIn (v, _, _, _, body) ->
                addLocal v (IRTScalar ETInt64)
                localsOf body
            | _ -> ()
    let fnLocals (f: TypedFunctionDecl) =
        for p in f.Params do addLocal p.Name p.Type
        match f.Body.Kind with
        | TExprBlock (stmts, _) -> localsOf stmts
        | _ -> ()
    // Module level first, so it takes precedence over any same-named local.
    for m in tp.Modules do
        for d in m.Decls do
            match d with
            | TDeclLet b | TDeclStatic b ->
                acc.[b.Name] <- b.Type
                for (n, _, t) in b.SubBindings do acc.[n] <- t
            | _ -> ()
    for m in tp.Modules do
        for d in m.Decls do
            match d with
            | TDeclFunction f -> fnLocals f
            | TDeclImpl impl -> for f in impl.Methods do fnLocals f
            | _ -> ()
    acc |> Seq.map (fun kv -> kv.Key, kv.Value) |> Map.ofSeq

/// A dist binding's NOMINAL type, rebuilt from the PPL registry exactly as the
/// binding hover does (collectTypedBindings): dists erase to a tuple of their
/// κ components, so the raw binding type would show that tuple where the
/// abstract signature promises `Dist<r, Elem like axes>`.
let private distTypeOf (types: Map<string, IRType>) (name: string) : IRType option =
    match Blade.Ppl.Elaborate.IdeDists.tryFind name with
    | Some (order, k1 :: _) ->
        match Map.tryFind k1 types with
        | Some (ArrayElem arr) -> Some (IRTDist (order, arr.ElemType, arr.IndexTypes))
        | _ -> None
    | _ -> None

/// Aliases bound to the import-gated modules whose surfaces `calls[]` covers,
/// as alias -> module name (`import ppl as p` -> "p" -> "ppl").
let private surfaceAliases (prog: Ast.Program) : Map<string, string> =
    let acc = Dictionary<string, string>()
    let consider (qn: string list) (aliasOpt: string option) =
        match List.tryLast qn with
        | Some m when m = "ppl" || m = "ml" -> acc.[defaultArg aliasOpt m] <- m
        | _ -> ()
    for m in prog.Modules do
        for imp in m.Imports do consider imp.Module imp.Alias
        for ld in m.Decls do
            match ld.Value with
            | DeclImport (qn, ImportQualified aliasOpt) -> consider qn aliasOpt
            | _ -> ()
    acc |> Seq.map (fun kv -> kv.Key, kv.Value) |> Map.ofSeq

/// Typed argument lists of the specialized functions the ML elaborator
/// generates, keyed by source span. `ml.linear(SPEC_IN, SPEC_OUT, w, x)`
/// becomes `__ml_N(w, x)` carrying the ORIGINAL call's span (inheritSpan), so
/// operands with no binding to look up — inline array literals, most often the
/// weight vectors — can still be typed from the checked call.
let private mlGeneratedArgs (tp: TypedProgram) : Map<(int * int * int * int), IRType list> =
    let acc = Dictionary<(int * int * int * int), IRType list>()
    let rec walk (te: TypedExpr) =
        (match te.Kind with
         | TExprApp ({ Kind = TExprVar (fn, _, _) }, args)
             when fn.StartsWith "__ml" && te.Span.StartLine > 0 ->
             acc.[clampSpan te.Span] <- (args |> List.map (fun a -> a.Type))
         | _ -> ())
        for c in Blade.TypeCheck.typedExprChildren te do walk c
    for m in tp.Modules do
        for d in m.Decls do
            match d with
            | TDeclLet b | TDeclStatic b -> walk b.Value
            | TDeclFunction f -> walk f.Body
            | TDeclImpl impl -> for f in impl.Methods do walk f.Body
            | _ -> ()
    acc |> Seq.map (fun kv -> kv.Key, kv.Value) |> Map.ofSeq

/// A qualified surface call `alias.op(args)` (or the bare `op(args)` an
/// elaborator pass may already have normalized to), with its operand exprs.
let private surfaceCallOf (aliases: Map<string, string>) (e: Expr) : (string * Expr list) option =
    match e.Kind with
    | ExprKind.ExprApp ({ Kind = ExprKind.ExprField ({ Kind = ExprKind.ExprVar alias }, op) }, args)
        when aliases.ContainsKey alias -> Some (op, args)
    | _ -> None

/// Former/op calls in declaration RHS position, typed from the checked
/// program. Only the `let x = alias.op(...)` shape is reported — that is the
/// placement the formers require, and it is what makes the result type
/// recoverable from `x`.
let private collectFormerCalls (prog: Ast.Program) (tp: TypedProgram) : CallInfo list =
    let aliases = surfaceAliases prog
    if aliases.IsEmpty then [] else
    let types = moduleBindingTypes tp
    let mlArgs = mlGeneratedArgs tp
    // A dist name reads as its nominal Dist type, not the κ tuple it erases to.
    let typeOfName (n: string) : IRType option =
        match distTypeOf types n with
        | Some d -> Some d
        | None -> Map.tryFind n types
    let render (t: IRType) = ppConcrete (concreteNames [t]) t
    let argType (a: Expr) : string option =
        match a.Kind with
        | ExprKind.ExprVar n -> typeOfName n |> Option.map render
        | ExprKind.ExprLit (LitInt n) -> Some (string n)
        | ExprKind.ExprLit (LitFloat _) -> Some "Float64"
        | _ -> None
    [ for m in prog.Modules do
        for ld in m.Decls do
            match ld.Value with
            | DeclLet b | DeclStatic b ->
                match b.Pattern.Kind, surfaceCallOf aliases b.Value with
                | PatternKind.PatVar outName, Some (op, args) ->
                    match typeOfName outName with
                    | Some retTy ->
                        // An argument whose type we cannot recover (a lambda,
                        // a compound expression) renders `_` rather than
                        // dropping the whole call — the other columns still
                        // tell the reader what was instantiated.
                        let span = if b.Value.Span.StartLine > 0 then b.Value.Span else ld.Span
                        let key = clampSpan span
                        let (line, col, endLine, endCol) = key
                        // The elaborator consumes the leading STATIC arguments
                        // (specs, degrees) and keeps the runtime ones, so the
                        // generated call's args align to the TAIL of the
                        // surface args. Used only where a name/literal lookup
                        // came up empty (inline array literals).
                        let generated = defaultArg (Map.tryFind key mlArgs) []
                        let offset = args.Length - generated.Length
                        let argAt i (a: Expr) =
                            match argType a with
                            | Some s -> s
                            | None when offset >= 0 && i >= offset ->
                                render generated.[i - offset]
                            | None -> "_"
                        yield { CName = op; CLine = line; CCol = col
                                CEndLine = endLine; CEndCol = endCol
                                CArgs = args |> List.mapi argAt
                                CRet = ppConcrete (concreteNames [retTy]) retTy }
                    | None -> ()
                | _ -> ()
            | _ -> () ]

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
    match p.Kind with
    | PatternKind.PatVar name -> [name]
    | PatternKind.PatTuple ps -> ps |> List.collect patternNames
    | PatternKind.PatCons (a, b) -> patternNames a @ patternNames b
    | PatternKind.PatTyped (inner, _) -> patternNames inner
    | PatternKind.PatGuarded (inner, _) -> patternNames inner
    | PatternKind.PatStruct (_, fields) -> fields |> List.collect (snd >> patternNames)
    | PatternKind.PatVariant (_, inner) -> inner |> Option.map patternNames |> Option.defaultValue []
    | PatternKind.PatWildcard | PatternKind.PatLit _ -> []

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
        match body.Kind with
        | ExprKind.ExprBlock (stmts, _) -> walkStmts scope stmts declSpan
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
        let antis =
            w.Antisymmetry
            |> List.map (fun group -> sprintf "antisymm(%s)" (String.concat ", " group))
        let pars =
            w.Parallel
            |> List.map (function
                | Omp s ->
                    let vars = s.Vars |> List.map (fun (v, n) -> sprintf "%s: %d" v n)
                    sprintf "omp(%s)" (String.concat ", " vars)
                | Cuda s -> sprintf "cuda(block: %d)" s.BlockSize
                | Mpi -> "mpi")
        let customs =
            w.Custom
            |> List.map (fun (name, args) -> sprintf "%s(%s)" name (String.concat ", " args))
        comms @ antis @ pars @ customs

type private TypedEntry = {
    Scope: string
    EName: string
    EKind: string
    ETypeStr: string
    EParams: (string * string) list
    ERet: string option
    EWhere: string list
}

let private collectTypedBindings (srcFuncs: Map<string, FunctionDecl>) (tp: TypedProgram) =
    let acc = ResizeArray<TypedEntry>()
    // Value bindings: each binding names its own abstract vars (T, U, ...) —
    // schemes don't share ids across bindings, so per-binding namespaces
    // can't collide.
    let ppVal (t: IRType) = abstractRenderer [] t
    let add scope name kind tyStr =
        acc.Add { Scope = scope; EName = name; EKind = kind; ETypeStr = tyStr
                  EParams = []; ERet = None; EWhere = [] }
    let rec walkTStmts (scope: string) (stmts: TypedStmt list) =
        for s in stmts do
            match s with
            | TStmtLet b ->
                add scope b.Name "let" (ppVal b.Type)
                for (n, _, t) in b.SubBindings do add scope n "let" (ppVal t)
            | TStmtForIn (v, _, _, _, body) ->
                add scope v "for" (ppType (IRTScalar ETInt64))
                walkTStmts scope body
            | _ -> ()
    let walkFuncBody (scope: string) (body: TypedExpr) =
        match body.Kind with
        | TExprBlock (stmts, _) -> walkTStmts scope stmts
        | _ -> ()
    let addFunc (f: TypedFunctionDecl) =
        // One abstract-var namespace across the whole signature, seeded with
        // the SOURCE type-variable names where the annotations reveal them.
        let seed =
            match Map.tryFind f.Name srcFuncs with
            | Some src when src.Params.Length = f.Params.Length ->
                [ for (p, tp) in List.zip src.Params f.Params do
                    match p.Type with
                    | Some ann -> yield! collectVarNames ann tp.Type
                    | None -> ()
                  match src.ReturnType with
                  | Some ann -> yield! collectVarNames ann f.ReturnType
                  | None -> () ]
            | _ -> []
        let pp = abstractRenderer seed
        let ps = f.Params |> List.map (fun p -> (p.Name, pp p.Type))
        let ret = pp f.ReturnType
        let kind = if f.IsStatic then "static function" else "function"
        acc.Add { Scope = ""; EName = f.Name; EKind = kind
                  ETypeStr = formatFunctionSig ps ret
                  EParams = ps; ERet = Some ret
                  EWhere = whereConjuncts f.WhereClause }
        for p in f.Params do add f.Name p.Name "param" (pp p.Type)
        walkFuncBody f.Name f.Body
    // Module-level let types by name, for rebuilding erased dists below.
    let moduleLets = Dictionary<string, IRType>()
    for m in tp.Modules do
        for d in m.Decls do
            match d with
            | TDeclLet b ->
                add "" b.Name "let" (ppVal b.Type)
                moduleLets.[b.Name] <- b.Type
                for (n, _, t) in b.SubBindings do add "" n "let" (ppVal t)
            | TDeclStatic b ->
                add "" b.Name "static" (ppVal b.Type)
                for (n, _, t) in b.SubBindings do add "" n "static" (ppVal t)
            | TDeclFunction f -> addFunc f
            | TDeclImpl impl -> for f in impl.Methods do addFunc f
            | _ -> ()
    // Erased dists: the flat pushforward formers (dist_map/dist_jet/...) are
    // register-only — PPL elaboration emits their κ components but no decl
    // under the user's name, so the walk above never sees them and the name
    // would hover as nothing. Rebuild Dist<order, elem like axes> from κ_1's
    // inferred type (distComponentType 1 = the array over the variable axes,
    // so this inverts exactly). Names the walk DID find are left alone.
    let named = HashSet<string>(acc |> Seq.filter (fun e -> e.Scope = "") |> Seq.map (fun e -> e.EName))
    for (name, order, comps) in Blade.Ppl.Elaborate.IdeDists.entries () do
        if not (named.Contains name) then
            match comps with
            | k1 :: _ ->
                match moduleLets.TryGetValue k1 with
                | true, ArrayElem arr -> add "" name "let" (ppVal (IRTDist (order, arr.ElemType, arr.IndexTypes)))
                | _ -> ()
            | [] -> ()
    acc

// ----------------------------------------------------------------------------
// Type-provider structure. Provided members (`store.vars.x`), the store handle,
// and the provider alias are not ordinary bindings, so the walk above never
// sees them. This section re-derives, per loaded store, the provider's index
// types and dims/vars members (structural only — no file attributes), plus the
// provenance of a top-level provider-read binding.
// ----------------------------------------------------------------------------

/// alias -> provider module name for every `import <p> as <alias>` (or bare
/// `import <p>`) whose module is a registered data provider. Scans both the
/// module-header imports and any DeclImport in the body.
let private providerAliases (prog: Ast.Program) : Map<string, string> =
    let acc = Dictionary<string, string>()
    let consider (qn: string list) (aliasOpt: string option) =
        match List.tryLast qn with
        | Some modName when (Blade.ProviderRegistry.tryFind modName).IsSome ->
            acc.[defaultArg aliasOpt modName] <- modName
        | _ -> ()
    for m in prog.Modules do
        for imp in m.Imports do consider imp.Module imp.Alias
        for ld in m.Decls do
            match ld.Value with
            | DeclImport (qn, ImportQualified aliasOpt) -> consider qn aliasOpt
            | _ -> ()
    acc |> Seq.map (fun kv -> kv.Key, kv.Value) |> Map.ofSeq

/// The `store.vars.v` / `store.dims.v` receiver of a `|> alias.read` (or
/// `.stream`) — recovered from the untyped RHS so a top-level provider-read
/// binding can show which store member it came from. The pipe desugars to
/// `alias.read(store.vars.v)` (Parser pipeline lowering).
let private readOperandProvenance (aliases: Map<string, string>) (v: Expr) : (string * string) option =
    match v.Kind with
    | ExprKind.ExprApp ({ Kind = ExprKind.ExprField ({ Kind = ExprKind.ExprVar alias }, meth) }, [operand])
        when (meth = "read" || meth = "stream") && aliases.ContainsKey alias ->
        match operand.Kind with
        | ExprKind.ExprField ({ Kind = ExprKind.ExprField ({ Kind = ExprKind.ExprVar store }, section) }, field)
            when section = "vars" || section = "dims" ->
            Some (store, sprintf "%s.%s" section field)
        | _ -> None
    | _ -> None

/// bindingName -> (store, "vars.v") for module-level provider reads.
let private readProvenance (prog: Ast.Program) (aliases: Map<string, string>) : Map<string, string * string> =
    let acc = Dictionary<string, string * string>()
    if not aliases.IsEmpty then
        for m in prog.Modules do
            for ld in m.Decls do
                match ld.Value with
                | DeclLet b | DeclStatic b ->
                    match b.Pattern.Kind with
                    | PatternKind.PatVar name ->
                        match readOperandProvenance aliases b.Value with
                        | Some pr -> acc.[name] <- pr
                        | None -> ()
                    | _ -> ()
                | _ -> ()
    acc |> Seq.map (fun kv -> kv.Key, kv.Value) |> Map.ofSeq

/// Provided structure for every `let store = alias.load("path")`, rendered from
/// the module TypeCheck already built at the load site and stashed in
/// IdeStores — so this NEVER re-opens the data file (a second, possibly native,
/// read is redundant and can crash the process, killing the whole JSON output).
/// A store with no recorded module (its load didn't type-check) is skipped, and
/// per-store rendering is guarded so one unusual type can't break the output.
let private collectProviderStores (prog: Ast.Program) : ProviderInfo list =
    let aliases = providerAliases prog
    if aliases.IsEmpty then [] else
    let describe store alias provider path (span: Span) (pm: IRModule) : ProviderInfo option =
        try
            let names = indexNameMap pm
            let ppIn t = ppIRTypeIn names t
            let membersOf label =
                pm.Types
                |> List.tryPick (function
                    | IRTDStruct (n, fields)
                        when n = label || n = sprintf "%s__%s" store label -> Some fields
                    | _ -> None)
                |> Option.defaultValue []
                |> List.map (fun (fn, ft) -> { MName = fn; MType = ppIn ft })
            let idxTypes =
                pm.Types
                |> List.choose (function
                    | IRTDIndexType (n, idx) ->
                        let ext =
                            match idx.Extent with
                            | IRLit (IRLitInt v) -> Some v
                            | _ -> None
                        Some { IName = n; IExtent = ext }
                    | _ -> None)
            let (line, col, _, _) = clampSpan span
            Some { Store = store; Alias = alias; Provider = provider; Path = path
                   PLine = line; PCol = col
                   IndexTypes = idxTypes; Dims = membersOf "dims"; Vars = membersOf "vars" }
        with _ -> None
    [ for m in prog.Modules do
        for ld in m.Decls do
            match ld.Value with
            | DeclLet b ->
                match b.Pattern.Kind, b.Value.Kind with
                | PatternKind.PatVar store,
                  ExprKind.ExprApp ({ Kind = ExprKind.ExprField ({ Kind = ExprKind.ExprVar alias }, "load") },
                                    [{ Kind = ExprKind.ExprLit (LitString path) }]) ->
                    match Map.tryFind alias aliases, Blade.ProviderRegistry.IdeStores.tryFind store with
                    | Some provider, Some pm ->
                        match describe store alias provider path ld.Span pm with
                        | Some info -> yield info
                        | None -> ()
                    | _ -> ()
                | _ -> ()
            | _ -> () ]

/// Join typed bindings to source spans by (scope, name), consuming spans in
/// declaration order so shadowed/reused names pair up positionally. Typed
/// decls with no source span (compiler-generated) are dropped. The surface
/// keyword kind (let / let mut / static / for) wins over the typed-side kind
/// when the source recorded one.
let private joinBindings (prog: Ast.Program) (tp: TypedProgram) (sourceLines: string[]) : BindingInfo list =
    // Source-side function decls by name, for recovering declared
    // type-variable names in signatures (collectTypedBindings.addFunc).
    let srcFuncs =
        [ for m in prog.Modules do
            for ld in m.Decls do
                match ld.Value with
                | DeclFunction f -> yield (f.Name, f)
                | DeclImpl impl -> for f in impl.Methods do yield (f.Name, f)
                | _ -> () ]
        |> Map.ofList
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
    // Provenance for top-level provider reads (`let x = store.vars.v |>
    // alias.read`), attached to the matching module-level binding.
    let provRead = readProvenance prog (providerAliases prog)
    [ for e in collectTypedBindings srcFuncs tp do
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
            let providerRead = if e.Scope = "" then Map.tryFind e.EName provRead else None
            yield { Name = e.EName; Kind = kind; Line = line; Col = col
                    TypeStr = e.ETypeStr; Doc = doc; Params = ps; Ret = e.ERet
                    Where = e.EWhere; ProviderRead = providerRead }
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
    let mutable providers = []
    let mutable calls = []
    if not (File.Exists filePath) then
        diags.Add { Severity = "error"; Line = 1; Col = 1; EndLine = 1; EndCol = 1
                    Message = sprintf "File not found: %s" filePath; Code = "" }
        exitCode <- 1
    else
        let source = File.ReadAllText filePath
        match Blade.Parser.parseProgramWithFile (Some filePath) source with
        | Error e ->
            let line = max 1 e.Line
            let col = max 1 e.Col
            let endLine = max line e.EndLine
            let endCol = if e.EndCol >= 1 then e.EndCol else col
            diags.Add { Severity = "error"; Line = line; Col = col; EndLine = endLine; EndCol = endCol
                        Message = e.Message; Code = e.Code }
            exitCode <- 1
        | Ok program ->
            // Fresh provider-module registry for this check (the load site
            // records into it during typeCheck; collectProviderStores reads it).
            Blade.ProviderRegistry.IdeStores.reset ()
            // Suggestions and warnings are NOT error-exclusive: a file with a
            // type error has still earned every BL4010/BL4011 nudge, and every
            // ordinary warning, the checker produced before it hit the error.
            // Dropping them on the error arm was the S1/S2 surfacing gap — the
            // arm where an editor needs the nudges MOST, since a half-broken
            // file is exactly what an editor is looking at while you type. All
            // three channels are AsyncLocal, so the Error arm reads them
            // exactly as the Ok arm does; this is that one block, shared.
            let drainWarningChannels () =
                // Confirm-and-pin suggestions (stage 3/4) arrive twice: as
                // plain strings in typeCheck's Ok payload (what the CLI
                // printed) and as structured (message, kernel-span) pairs in
                // the PinSuggestions side-channel. Emit the structured form —
                // code BL4010 at the kernel's real span, so the editor can
                // render a ghost annotation and offer the one-click pin.
                let pinSuggestions = Blade.TypeCheck.PinSuggestions.get ()
                // Stage-6a equivariance-certificate suggestions arrive the same
                // way, one code over: BL4011 at the DECL span, so the editor can
                // ghost-render `where ml.equiv(G)` on the function it belongs
                // to. Same field shape as BL4010 — no new `ide check --json`
                // field is needed, the diagnostics array carries both.
                let certSuggestions = Blade.ML.Equiv.CertSuggestions.get ()
                for (msg, span) in pinSuggestions do
                    let (line, col, endLine, endCol) = clampSpan span
                    diags.Add { Severity = "warning"; Line = line; Col = col
                                EndLine = endLine; EndCol = endCol
                                Message = msg; Code = "BL4010" }
                for (msg, span) in certSuggestions do
                    let (line, col, endLine, endCol) = clampSpan span
                    diags.Add { Severity = "warning"; Line = line; Col = col
                                EndLine = endLine; EndCol = endCol
                                Message = msg; Code = "BL4011" }
                // The checker's own warnings, now coded and spanned instead of
                // `Code = ""` at 1:1. BL4010 is skipped: PinSuggestions above
                // already emitted exactly those, at exactly that span, and a
                // duplicate diagnostic is a duplicate squiggle. (BL4011 never
                // rides this channel — the ML elaborator writes CertSuggestions
                // directly, never through emitWarning.)
                for d in Blade.TypeCheck.WarningLog.get () |> List.distinct do
                    if d.Code <> "BL4010" then
                        let (line, col, endLine, endCol) = clampSpan d.Span
                        diags.Add { Severity = "warning"; Line = line; Col = col
                                    EndLine = endLine; EndCol = endCol
                                    Message = d.Message; Code = d.Code }
            match Blade.TypeCheck.typeCheck program with
            | Error errors ->
                for e in errors do
                    let (line, col, endLine, endCol) = clampSpan e.Span
                    let code = (Blade.TypeEnv.diagnosticOfCompileError e).Code
                    let msg =
                        let baseMsg = Blade.TypeEnv.formatTypeError e.Error
                        match e.Context with
                        | [] -> baseMsg
                        | ctx -> sprintf "%s (%s)" baseMsg (String.concat "; " (List.rev ctx))
                    diags.Add { Severity = "error"; Line = line; Col = col
                                EndLine = endLine; EndCol = endCol; Message = msg; Code = code }
                exitCode <- 1
                drainWarningChannels ()
                // Errors don't have to mean zero hovers: if the checker ran and
                // produced a PARTIAL typed program (only a pre-check pipeline
                // failure yields none), surface bindings/types for the parts
                // that DID check, so a file with errors still gets tooltips.
                match Blade.TypeCheck.IdePartial.get () with
                | Some (typedProg, _) ->
                    let sourceLines = source.Replace("\r\n", "\n").Split('\n')
                    bindings <- (try joinBindings program typedProg sourceLines with _ -> [])
                    providers <- (try collectProviderStores program with _ -> [])
                    calls <- (try collectCalls typedProg @ collectFormerCalls program typedProg with _ -> [])
                | None -> ()
            | Ok (typedProg, _, _) ->
                drainWarningChannels ()
                let sourceLines = source.Replace("\r\n", "\n").Split('\n')
                bindings <- joinBindings program typedProg sourceLines
                // Guarded so provider structure can never break the JSON output.
                providers <- (try collectProviderStores program with _ -> [])
                calls <- (try collectCalls typedProg @ collectFormerCalls program typedProg with _ -> [])
    printfn "%s" (renderJson (List.ofSeq diags) bindings providers calls)
    exitCode
