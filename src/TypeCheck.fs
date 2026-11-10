// The type-checking driver and public surface: checkModule/checkProgram,
// the IdePartial side-channel, and typeCheck -- the pipeline entry point
// that runs the elaborators (Unfold, ML, Sgs, Ppl, Math, Rand, Spectra,
// Display, Grad, IndexTypeValidator) and then the checker. Keeps the
// module name Blade.TypeCheck so external callers are untouched; the two
// helpers below re-export the remaining cross-module surface.
module Blade.TypeCheck

open Blade.Ast
open Blade.IR
open Blade.IRLoopStructure
open Blade.IRStorage
open Blade.IRLift
open Blade.IRMono
open Blade.IRPrint
open Blade.IRValidate
open Blade.Types
open Blade.TypedAst
open Blade.Unify
open Blade.TypeEnv
open Blade.Zonk
open Blade.TypeCheckIde
open Blade.TypeLower
open Blade.TypeCheckSupport
open Blade.TypeCheckInfer
open Blade.TypeCheckValidate

// 12. Module and Program

/// The type names one `type` declaration introduces (a mutual group names
/// every member).
let private typeDeclNames (td: TypeDecl) : string list =
    match td with
    | TyDeclAlias (n, _, _) | TyDeclStruct (n, _, _, _, _) | TyDeclSum (n, _, _) -> [ n ]
    | TyDeclMutualGroup (members, _) -> members |> List.map fst

/// BL2009 for a variant constructor two sum types of ONE module declare.
///
/// Type identity is module-qualified (D11, Types.TypeOrigins): module A's
/// `struct P` is `A.P`, B's is `B.P`, main's is `P`, so two modules may each
/// declare a type or constructor of one name, and an unqualified use that two
/// imports both answer is refused at the USE (BL2010). Within one module a
/// constructor still names one sum type: its tag table and its C++ name
/// (enum member, constructor function) are keyed by the module's identity.
let private crossModuleDeclErrors (env: TypeEnv) (program: Program) : CompileError list =
    let errors = ResizeArray<CompileError>()
    let seenCtors = System.Collections.Generic.Dictionary<string, string * Span>()
    // ...and a name that is NOT module-qualified in two modules: a
    // `__`-prefixed (synthetic) name keeps its spelling in every module
    // (TypeOrigins.identityOf), and the main module's names are bare, so two
    // such declarations would be one identity -- still refused.
    let seenUnqualified = System.Collections.Generic.Dictionary<string, string * Span>()
    let moduleCount = program.Modules.Length
    // The error-location side channel still holds the LAST expression the
    // checker visited (in whichever module ran last); cleared per refusal so
    // each one points at its own declaration.
    let refuse (span: Span) (err: TypeError) =
        resetCurrentStmtSpan ()
        errors.Add (locateError span env err)
    let siteOf (first: Span) (here: Span) =
        let where = $"line {first.StartLine}, column {first.StartCol}"
        match first.File, here.File with
        | Some f1, Some f2 when f1 <> f2 -> $"{f1}, {where}"
        | _ -> where
    for (mi, m) in List.indexed program.Modules do
        let modName = m.Name |> String.concat "."
        let isMain = Blade.Types.TypeOrigins.isMainModule mi moduleCount || modName = ""
        let unqualified (kind: string) (n: string) (span: Span) =
            if isMain || n.StartsWith "__" then
                match seenUnqualified.TryGetValue (kind + "\u0000" + n) with
                | true, (firstMod, firstSpan) when firstMod <> modName ->
                    refuse span (DuplicateDecl (kind, n, siteOf firstSpan span, Some firstMod))
                | true, _ -> ()
                | _ -> seenUnqualified.[kind + "\u0000" + n] <- (modName, span)
        for d in m.Decls do
            match d.Value with
            | DeclType td ->
                for n in typeDeclNames td do unqualified "type" n d.Span
                match td with
                | TyDeclSum (_, _, variants) -> for v in variants do unqualified "constructor" v.Name d.Span
                | _ -> ()
                match td with
                | TyDeclSum (_, _, variants) ->
                    for v in variants do
                        let key = modName + "\u0000" + v.Name
                        match seenCtors.TryGetValue key with
                        | true, (_, firstSpan) ->
                            refuse d.Span (DuplicateDecl ("constructor", v.Name, siteOf firstSpan d.Span, None))
                        | _ -> seenCtors.[key] <- (modName, d.Span)
                | _ -> ()
            | _ -> ()
    List.ofSeq errors

/// The lambda parameters a RANGE feeds, over a whole typed module (the one
/// place Zonk's subscript guard may trust a lambda parameter's index type --
/// SubscriptGuardCtx.RangeFedParams). A plain apply whose operand k is
/// `range<I>` / `0..n` feeds kernel parameter k; a single multi-slot
/// `range<Y, X>` feeds all of them. Collected BEFORE zonk because a kernel
/// lambda can be zonked (at its own `let`) before the apply that feeds it.
let rec rangeFedLambdaParams (modul: TypedModule) : System.Collections.Generic.HashSet<IRId> =
    fst (rangeFedLambdaParamsWithBounds modul)

/// The same walk, also answering each range-fed parameter's STATIC value
/// bounds `[lo, hi)` when the range has them (`range<I>` over a static extent
/// n: [0, n); `lo..hi` over two integer literals: [lo, hi)) -- what the guard
/// of an ANONYMOUS slot accepts as proof (G192 (1): an anonymous slot carries
/// no nominal tag, so the only proven positions are literals and the values
/// of a range whose bounds lie inside the slot's static extent).
and rangeFedLambdaParamsWithBounds (modul: TypedModule)
        : System.Collections.Generic.HashSet<IRId> * System.Collections.Generic.Dictionary<IRId, int64 * int64> =
    let fed = System.Collections.Generic.HashSet<IRId>()
    let bounds = System.Collections.Generic.Dictionary<IRId, int64 * int64>()
    let isRange (a: TypedExpr) =
        match a.Kind with
        | TExprRange _ | TExprDotDot _ -> true
        | _ -> false
    let litInt (e: TypedExpr) =
        match e.Kind with
        | TExprLit (Blade.Ast.LitInt v) -> Some v
        | TExprUnaryOp (Blade.Ast.OpNeg, { Kind = TExprLit (Blade.Ast.LitInt v) }) -> Some (-v)
        | _ -> None
    let staticBounds (a: TypedExpr) (slot: int) =
        match a.Kind with
        | TExprRange ixs when slot < ixs.Length ->
            let ix = ixs.[slot]
            match ix.Tag |> Option.bind haloAccessOfTag with
            // A dense HALO window parameter: the positions its in-reach reads
            // `w(o)` produce, [0, M + Shrink) over a shrunk extent M -- the
            // window's own demand, tied to THIS parameter (Codex diff review
            // round 2 of G192 (1): two anonymous halos may share a tag).
            | Some h when not h.IsCompound ->
                tryEvalIntIR ix.Extent |> Option.map (fun m -> haloDemand h (0L, m))
            | Some _ -> None
            | None ->
            if ix.IxKind = IxKPlain && ix.Symmetry = SymNone && ix.Rank <= 1 then
                tryEvalIntIR ix.Extent |> Option.map (fun n -> (0L, n))
            else None
        | TExprDotDot (lo, hi) ->
            (match litInt lo, litInt hi with
             | Some l, Some h -> Some (l, h)
             | _ -> None)
        | _ -> None
    // A bound proves a parameter only when EVERY occurrence of its lambda is a
    // kernel some range with static bounds feeds (Codex diff review of G192
    // (1): a let-bound lambda applied over `0..3` and also called `k(5)`, or
    // fed by data, must not inherit the range's proof). Per lambda (keyed by
    // its first parameter): every occurrence, the kernel occurrences, and per
    // parameter the union of its feeds (None once one feed has no bounds).
    let lambdaOcc = System.Collections.Generic.Dictionary<IRId, int>()
    let kernelOcc = System.Collections.Generic.Dictionary<IRId, int>()
    let lambdaOf = System.Collections.Generic.Dictionary<IRId, IRId>()
    let feeds = System.Collections.Generic.Dictionary<IRId, (int64 * int64) option>()
    let bump (d: System.Collections.Generic.Dictionary<IRId, int>) k =
        d.[k] <- (match d.TryGetValue k with | true, n -> n + 1 | _ -> 1)
    let noteFeed (pid: IRId) (b: (int64 * int64) option) =
        feeds.[pid] <-
            match feeds.TryGetValue pid, b with
            | (true, None), _ | _, None -> None
            | (true, Some (l0, h0)), Some (l, h) -> Some (min l0 l, max h0 h)
            | _, Some lh -> Some lh
    let kernelUse (li: TypedLambdaInfo) =
        match li.Params with
        | p0 :: _ ->
            bump kernelOcc p0.VarId
            for p in li.Params do lambdaOf.[p.VarId] <- p0.VarId
        | [] -> ()
    let feed (p: TypedParam) (a: TypedExpr) (slot: int) =
        fed.Add p.VarId |> ignore
        noteFeed p.VarId (staticBounds a slot)
    let rec walk (e: TypedExpr) =
        (match e.Kind with
         | TExprApply info when not info.IsComposeApply ->
             let operands =
                 info.Arrays |> List.collect (fun a ->
                     match a.Kind with
                     | TExprZip es -> es
                     | _ -> [ a ])
             let ps =
                 match info.Kernel.Kind with
                 | TExprLambda li | TExprReynolds ({ Kind = TExprLambda li }, _) -> kernelUse li; li.Params
                 | _ -> []
             // A REYNOLDS kernel is applied to every permutation of its
             // operands, so each parameter may receive ANY operand: its bounds
             // are the union over all of them (Codex diff review round 3 of
             // G192 (1): `reynolds(lambda(i, j) -> v(i))` over `0..3, 0..5`).
             let permuted = match info.Kernel.Kind with TExprReynolds _ -> true | _ -> false
             match operands with
             | [ { Kind = TExprRange ixs } as r ] when ixs.Length > 1 && ixs.Length = ps.Length ->
                 if permuted then
                     for p in ps do ixs |> List.iteri (fun k _ -> feed p r k)
                 else ps |> List.iteri (fun k p -> feed p r k)
             | _ when operands.Length = ps.Length ->
                 if permuted then
                     for p in ps do
                         for a in operands do
                             if isRange a then feed p a 0 else noteFeed p.VarId None
                 else
                     List.iter2 (fun (p: TypedParam) a -> if isRange a then feed p a 0 else noteFeed p.VarId None) ps operands
             | _ -> for p in ps do noteFeed p.VarId None
         // `sort(range<I>, lambda(i) -> ..)` / `sort(lo..hi, ..)` (round 4
         // wave 4 D1): the key sees the range's own positions, exactly as a
         // map kernel over that range does -- a single-slot range only (a
         // `lo..hi` from a literal 0: an offset range's values are not
         // positions of its own axis).
         | TExprSort (({ Kind = TExprRange [ _ ] } as r), { Kind = TExprLambda li })
         | TExprSort (({ Kind = TExprDotDot ({ Kind = TExprLit (Blade.Ast.LitInt 0L) }, _) } as r), { Kind = TExprLambda li }) ->
             kernelUse li
             for p in li.Params do feed p r 0
         | TExprLambda li ->
             (match li.Params with
              | p0 :: _ -> bump lambdaOcc p0.VarId
              | [] -> ())
         | _ -> ())
        for c in typedExprChildren e do walk c
    for d in modul.Decls do
        for e in declExprs d do walk e
    for KeyValue (pid, b) in feeds do
        match b, lambdaOf.TryGetValue pid with
        | Some lh, (true, l0) ->
            let all = match lambdaOcc.TryGetValue l0 with | true, n -> n | _ -> 0
            let asKernel = match kernelOcc.TryGetValue l0 with | true, n -> n | _ -> 0
            if all = asKernel then bounds.[pid] <- lh
        | _ -> ()
    (fed, bounds)

/// LET-POLYMORPHISM FOR A LAMBDA'S ARROW PARAMETER (formalism 4.3).
///
/// `let u = lambda(c) -> c(0) + 1.0` applies its unannotated parameter, so `c`
/// is an ARROW whose kind -- array or function -- only a use can decide. A
/// lambda is ONE emitted body (a lifted callable behind a `std::function` in
/// main), so its first use used to decide for every later one, and
/// `u([5.0, 6.0]); u(lambda(i) -> Float64(i))` was refused at the second.
/// A `function` declaration's arrow parameter is GENERIC instead: each call
/// decides (TypeCheckSupport.arrowObligationClash) and IR monomorphization
/// emits one body per kind. Such a binding IS that declaration whenever
/// nothing observes the difference, so it is checked as one -- the value
/// restriction, stated for this language:
///   * a plain, unannotated, top-level `let NAME = lambda(...) -> body`: not
///     `let mut`, no `where` clause (kernel metadata -- comm/omp -- that rides
///     the lambda VALUE into a loop), no parameter defaults, not
///     self-referencing; some parameter is unannotated (a declaration's
///     unannotated parameter is GENERIC whatever its use -- an arrow, or a
///     value each call types, so `let inc = lambda(x) -> x + 1` is
///     `inc(6) = 7`, an Int64, exactly as `function inc(x) = x + 1` is;
///     the lambda's call judgment binds nothing, so its parameter would
///     fall to zonk's Float64 default);
///   * every later use of NAME is an ordinary APPLICATION `NAME(args)`. A
///     use as a VALUE -- a kernel (`method_for(A) <@> u`, `object_for(u)`,
///     `reduce(A, u)`), an argument, an alias, a reassignment -- needs ONE
///     value, and keeps the lambda (and its first-use rule);
///   * no variable the lambda captures is ever assigned: a lambda captures at
///     its creation, a declaration reads the module binding when called.
/// Anything else stays a lambda, exactly as before.
/// The let-bound lambdas `instantiateLetLambdaKernels` spliced at a kernel use
/// (gap G205), by their binding PATTERN object: such a lambda was used as a
/// VALUE in the source, so it keeps the lambda semantics (its unit judgment
/// defers to the use, `generalizeArrowLetLambdas` does not turn it into a
/// generic declaration) even when the splice removed its last value use
/// (Codex diff review round 3: `log(x / scale)` over metres, BL3006).
let private splicedLambdaBindings = System.Runtime.CompilerServices.ConditionalWeakTable<Pattern, obj>()

let private generalizeArrowLetLambdas (decls: Located<Decl> list) : Located<Decl> list =
    let mapPre = Blade.Unfold.mapExprPre
    // Every expression a declaration holds, with the names its own binders
    // bind around it (a function's parameters shadow a module name).
    let declScopes (d: Decl) : (Set<string> * Expr) list =
        let fnScopes (f: FunctionDecl) =
            let ps = f.Params |> List.map (_.Name) |> Set.ofList
            (ps, f.Body) :: (f.Params |> List.choose (_.Default) |> List.map (fun e -> (ps, e)))
        match d with
        | DeclLet b | DeclStatic b -> [ (Set.empty, b.Value) ]
        | DeclFunction f -> fnScopes f
        | DeclImpl impl -> impl.Methods |> List.collect fnScopes
        | DeclType (TyDeclStruct (_, _, _, cs, _)) | DeclType (TyDeclMutualGroup (_, cs)) ->
            cs |> List.map (fun e -> (Set.empty, e))
        | DeclType _ | DeclInterface _ | DeclUnit _ | DeclImport _ -> []
    // Names written anywhere in the module: `x = ..`, `A(i) = ..`, `p.f = ..`.
    let assigned =
        let acc = System.Collections.Generic.HashSet<string>()
        let rec root (e: Expr) =
            match e.Kind with
            | ExprKind.ExprVar n -> Some n
            | ExprKind.ExprApp (f, _) -> root f
            | ExprKind.ExprField (x, _) | ExprKind.ExprTupleIndex (x, _) | ExprKind.ExprTyped (x, _) -> root x
            | _ -> None
        let rec stmts (ss: Stmt list) =
            for s in ss do
                match unwrapStmt s with
                | StmtAssign (l, _, _) -> root l |> Option.iter (acc.Add >> ignore)
                | StmtForIn (_, _, body) -> stmts body
                | _ -> ()
        for d in decls do
            for (_, e) in declScopes d.Value do
                mapPre (fun x ->
                    (match x.Kind with
                     | ExprKind.ExprAssign (l, _) -> root l |> Option.iter (acc.Add >> ignore)
                     | ExprKind.ExprBlock (ss, _) -> stmts ss
                     | _ -> ())
                    None) e |> ignore
        acc
    // Does NAME occur free in `e` other than as the head of an application?
    let usedAsValue (name: string) (bound: Set<string>) (e: Expr) =
        let rec dropCalls (x: Expr) : Expr option =
            match x.Kind with
            | ExprKind.ExprApp ({ Kind = ExprKind.ExprVar n }, args)
                    when n = name && not args.IsEmpty
                         && args |> List.forall (fun a -> match a.Kind with ExprKind.ExprWildcard -> false | _ -> true) ->
                Some (inheritSpan x (ExprKind.ExprTuple (args |> List.map (mapPre dropCalls))))
            | _ -> None
        Set.contains name (collectFreeVars bound (mapPre dropCalls e))
    let arr = List.toArray decls
    arr |> Array.mapi (fun i ld ->
        match ld.Value with
        | DeclLet b when b.Mutability = BindLet && b.Type.IsNone
                         && (let mutable o = null in not (splicedLambdaBindings.TryGetValue(b.Pattern, &o))) ->
            match b.Pattern.Kind, b.Value.Kind with
            | PatternKind.PatVar name, ExprKind.ExprLambda (parms, None, body)
                    when not parms.IsEmpty
                         && parms |> List.forall (fun p -> p.Default.IsNone)
                         && parms |> List.exists (fun p -> p.Type.IsNone) ->
                let captured = collectFreeVars (parms |> List.map (_.Name) |> Set.ofList) body
                let later = arr |> Array.skip (i + 1)
                let ok =
                    not (captured.Contains name)
                    && not (captured |> Set.exists assigned.Contains)
                    && not (assigned.Contains name)
                    && later |> Array.forall (fun d ->
                        declScopes d.Value |> List.forall (fun (bound, e) -> not (usedAsValue name bound e)))
                if not ok then ld
                else
                    let fd : FunctionDecl =
                        { Name = name
                          TypeParams = []
                          Params =
                              parms |> List.map (fun p ->
                                  { Name = p.Name; Type = p.Type; Mutability = Immutable
                                    Default = None; NameSpan = p.NameSpan } : ParamDecl)
                          WhereClause = None
                          ReturnType = None
                          Body = body
                          IsStatic = false
                          NameSpan = b.Pattern.Span }
                    { ld with Value = DeclFunction fd }
            | _ -> ld
        | _ -> ld)
    |> List.ofArray

/// A LET-BOUND LAMBDA VALUE IN KERNEL POSITION IS INSTANTIATED PER USE (gap
/// G205; G172 (b), G114). The checker resolves a kernel `inc` through its
/// binding to the ONE typed lambda and unifies that lambda's parameters with
/// the element (`inferApply`), so its first kernel use fixed its types for
/// every later one: `method_for(a32) <@> inc` then `method_for(a64) <@> inc`
/// was BL3001, and a literal partner (`x + 1.0`) never saw the operand's
/// width (the G110 / G114 seeding reads a lambda WRITTEN at the use). A call
/// `inc(f)` instantiates per call; a kernel use now does too: the source
/// lambda is spliced at each KERNEL position -- the right operand of `<@>`,
/// `object_for(..)`, a fold kernel `reduce(xs, ..)`, `reynolds(..)`, a sort
/// key, a mask predicate -- exactly as if it were written there, so it is
/// typed afresh per use (fresh parameter ids and type variables, the same
/// captured bindings) and stays INLINE (no eta call, no std::function per
/// cell). The binding itself is kept for its other uses (pipeline fusion
/// already splices a let-bound stage the same way, Grad.fuseProgram).
///
/// Admission (fail closed; anything else keeps today's resolve-at-apply):
///   * a top-level `let NAME = lambda(...) -> body` (not `mut`, no type
///     annotation on the binding, not self-referencing), NAME never assigned;
///   * no free name of the lambda is ever assigned anywhere in the module (a
///     lambda captures at its creation, a spliced copy reads at its use);
///   * HYGIENE: in the using declaration, neither NAME nor any free name of
///     the lambda is bound by ANY binder (its parameters, lets, match / for /
///     let-rec binders, lambda parameters) -- so every name of the spliced
///     copy denotes what it denoted at the definition (a top-level name is
///     unique per module, BL2009).
/// Only uses in the SAME module are spliced. A stored former (`let o =
/// object_for(inc)`) is one value: spliced once, still ONE instance for
/// every application of `o` (gap G205 remainder).
let private instantiateLetLambdaKernels (imports: ImportDecl list) (decls: Located<Decl> list) : Located<Decl> list =
    let mapPre = Blade.Unfold.mapExprPre
    let rootName (e: Expr) =
        let rec go (e: Expr) =
            match e.Kind with
            | ExprKind.ExprVar n -> Some n
            | ExprKind.ExprApp (f, _) -> go f
            | ExprKind.ExprField (x, _) | ExprKind.ExprTupleIndex (x, _) | ExprKind.ExprTyped (x, _) -> go x
            | _ -> None
        go e
    let scopesOf (d: Decl) : Expr list =
        let fnScopes (f: FunctionDecl) = f.Body :: (f.Params |> List.choose (_.Default))
        match d with
        | DeclLet b | DeclStatic b -> [ b.Value ]
        | DeclFunction f -> fnScopes f
        | DeclImpl impl -> impl.Methods |> List.collect fnScopes
        | DeclType (TyDeclStruct (_, _, _, cs, _)) | DeclType (TyDeclMutualGroup (_, cs)) -> cs
        | DeclType _ | DeclInterface _ | DeclUnit _ | DeclImport _ -> []
    // every name the module ASSIGNS (`x = ..`, `A(i) = ..`, `p.f = ..`)
    let assigned =
        let acc = System.Collections.Generic.HashSet<string>()
        let rec stmts (ss: Stmt list) =
            for s in ss do
                match unwrapStmt s with
                | StmtAssign (l, _, _) -> rootName l |> Option.iter (acc.Add >> ignore)
                | StmtForIn (_, _, body) -> stmts body
                | _ -> ()
        for d in decls do
            for e in scopesOf d.Value do
                mapPre (fun x ->
                    (match x.Kind with
                     | ExprKind.ExprAssign (l, _) -> rootName l |> Option.iter (acc.Add >> ignore)
                     | ExprKind.ExprBlock (ss, _) -> stmts ss
                     | _ -> ())
                    None) e |> ignore
        acc
    // every name any binder of declaration `d` binds (a superset is safe)
    let bindersOf (d: Decl) : System.Collections.Generic.HashSet<string> =
        let acc = System.Collections.Generic.HashSet<string>()
        (match d with
         | DeclFunction f -> for p in f.Params do acc.Add p.Name |> ignore
         | DeclImpl impl -> for m in impl.Methods do for p in m.Params do acc.Add p.Name |> ignore
         | _ -> ())
        let rec stmts (ss: Stmt list) =
            for s in ss do
                match unwrapStmt s with
                | StmtLet b -> for n in patternNames b.Pattern do acc.Add n |> ignore
                | StmtForIn (v, _, body) -> acc.Add v |> ignore; stmts body
                | _ -> ()
        for e in scopesOf d do
            mapPre (fun x ->
                (match x.Kind with
                 | ExprKind.ExprLambda (ps, _, _) -> for p in ps do acc.Add p.Name |> ignore
                 | ExprKind.ExprLet (b, _) -> for n in patternNames b.Pattern do acc.Add n |> ignore
                 | ExprKind.ExprMatch (_, cases) -> for c in cases do for n in patternNames c.Pattern do acc.Add n |> ignore
                 | ExprKind.ExprBlock (ss, _) -> stmts ss
                 | ExprKind.ExprRecArray def ->
                     acc.Add def.Name |> ignore
                     acc.Add def.PrefixVar |> ignore
                     acc.Add def.StepVar |> ignore
                     (match def.SeedArm with Some (v, _) -> acc.Add v |> ignore | None -> ())
                 | _ -> ())
                None) e |> ignore
        acc
    // the names a top-level declaration INTRODUCES (a later one shadows an
    // earlier binding or import of the same name)
    let introduces (d: Decl) : string list =
        match d with
        | DeclLet b | DeclStatic b -> patternNames b.Pattern
        | DeclFunction f -> [ f.Name ]
        | DeclImpl impl -> impl.Methods |> List.map (_.Name)
        | DeclInterface i -> [ i.Name ]
        | DeclUnit u -> [ u.Name ]
        | DeclType (TyDeclAlias (n, _, _)) -> [ n ]
        | DeclType (TyDeclSum (n, _, vs)) -> n :: (vs |> List.map (_.Name))
        | DeclType (TyDeclStruct (n, _, _, _, _)) -> [ n ]
        | DeclType (TyDeclMutualGroup (ms, _)) -> ms |> List.map fst
        | DeclImport (qn, ImportSelective ids) -> ids @ [ String.concat "." qn ]
        | DeclImport (qn, ImportQualified alias) ->
            (match alias with Some a -> [ a ] | None -> []) @ [ String.concat "." qn; List.last qn ]
    // a TYPE, unit or import declaration can change what an ANNOTATION of
    // the lambda (`lambda(x: T)`) means; the free-name walk does not see
    // annotations, so such a declaration ends every earlier candidate's reach
    let typeLevel (d: Decl) =
        match d with
        | DeclType _ | DeclUnit _ | DeclImport _ | DeclInterface _ -> true
        | _ -> false
    // same-module functions with a DEFAULT argument: a call that omits it
    // elaborates the default expression at the call site, a dependency the
    // free-name walk does not see -- a lambda calling one is not spliced
    // (a let-bound lambda with a default too; an IMPORTED callee's defaults
    // are not visible here, so a lambda naming any imported name is not
    // spliced, and a module importing names wholesale is left as it is)
    let withDefaults =
        decls |> List.choose (fun ld ->
            match ld.Value with
            | DeclFunction f when f.Params |> List.exists (fun p -> p.Default.IsSome) -> Some f.Name
            | DeclLet b | DeclStatic b ->
                (match b.Pattern.Kind, b.Value.Kind with
                 | PatternKind.PatVar n, ExprKind.ExprLambda (ps, _, _) when ps |> List.exists (fun p -> p.Default.IsSome) -> Some n
                 | _ -> None)
            | _ -> None)
        |> Set.ofList
    let importsAll =
        imports |> List.exists (fun i ->
            match i.Items with
            | None -> true
            | Some items -> items |> List.exists (function ImportHiding _ -> true | ImportName _ -> false))
    let imported =
        (imports |> List.collect (fun i ->
            (match i.Alias with Some a -> [ a ] | None -> [])
            @ (match i.Items with
               | Some items -> items |> List.choose (function ImportName n -> Some n | ImportHiding _ -> None)
               | None -> [])
            @ [ String.concat "." i.Module; List.last i.Module ]))
        @ (decls |> List.collect (fun ld ->
            match ld.Value with
            | DeclImport _ -> introduces ld.Value
            | _ -> []))
        |> Set.ofList
    let arr = List.toArray decls
    // CANDIDATES are processed in declaration order over the REWRITTEN
    // declarations, so a candidate spliced into a later candidate's body is
    // already expanded when that one is spliced (any nesting depth).
    let cur = Array.copy arr
    // (index, name, every name the source depends on, the source)
    let live = System.Collections.Generic.List<int * string * Set<string> * Expr * Pattern>()
    for j in 0 .. arr.Length - 1 do
        // a declaration that introduces a name a live candidate depends on
        // (or any type-level declaration) ends that candidate's reach,
        // including at this declaration itself
        let intro = introduces arr.[j].Value |> Set.ofList
        live.RemoveAll(fun (_, _, names, _, _) ->
            typeLevel arr.[j].Value || not (Set.isEmpty (Set.intersect names intro))) |> ignore
        if live.Count > 0 then
            let binders = bindersOf arr.[j].Value
            let here =
                live
                |> Seq.filter (fun (_, _, names, _, _) -> not (names |> Set.exists binders.Contains))
                |> Seq.map (fun (_, name, _, src, pat) -> (name, (src, pat)))
                |> Map.ofSeq
            if not here.IsEmpty then
                let splice (k: Expr) : Expr option =
                    match k.Kind with
                    | ExprKind.ExprVar n ->
                        Map.tryFind n here |> Option.map (fun (src, pat) ->
                            (try splicedLambdaBindings.AddOrUpdate(pat, box true) with _ -> ())
                            { src with Span = k.Span })
                    | _ -> None
                let rec rw (x: Expr) : Expr = mapPre f x
                and f (x: Expr) : Expr option =
                    let re k = Some (inheritSpan x k)
                    let kern (k: Expr) = match splice k with Some s -> s | None -> rw k
                    match x.Kind with
                    | ExprKind.ExprBinOp (m, OpApply, l, r) when (splice r).IsSome -> re (ExprBinOp (m, OpApply, rw l, kern r))
                    | ExprKind.ExprObjectFor k when (splice k).IsSome -> re (ExprObjectFor (kern k))
                    | ExprKind.ExprReduce (a, k, init, ax) when (splice k).IsSome ->
                        re (ExprReduce (rw a, kern k, Option.map rw init, ax))
                    | ExprKind.ExprReynolds (k, anti) when (splice k).IsSome -> re (ExprReynolds (kern k, anti))
                    | ExprKind.ExprSort (a, k) when (splice k).IsSome -> re (ExprSort (rw a, kern k))
                    | ExprKind.ExprMask (a, p) when (splice p).IsSome -> re (ExprMask (rw a, kern p))
                    | _ -> None
                let rwFn (fd: FunctionDecl) =
                    { fd with Body = rw fd.Body
                              Params = fd.Params |> List.map (fun p -> { p with Default = Option.map rw p.Default }) }
                let v =
                    match cur.[j].Value with
                    | DeclLet b -> DeclLet { b with Value = rw b.Value }
                    | DeclStatic b -> DeclStatic { b with Value = rw b.Value }
                    | DeclFunction fd -> DeclFunction (rwFn fd)
                    | DeclImpl impl -> DeclImpl { impl with Methods = impl.Methods |> List.map rwFn }
                    | other -> other
                cur.[j] <- { cur.[j] with Value = v }
        // this declaration as a candidate, its (rewritten) source
        match cur.[j].Value with
        | DeclLet b when b.Mutability = BindLet && b.Type.IsNone ->
            (match b.Pattern.Kind, b.Value.Kind with
             | PatternKind.PatVar name, ExprKind.ExprLambda (parms, _, _) when not parms.IsEmpty ->
                 let free = collectFreeVars Set.empty b.Value
                 if not (importsAll || free.Contains name || assigned.Contains name
                         || (free |> Set.exists assigned.Contains)
                         || (free |> Set.exists withDefaults.Contains)
                         || (free |> Set.exists imported.Contains)) then
                     live.Add((j, name, Set.add name free, b.Value, b.Pattern))
             | _ -> ())
        | _ -> ()
    List.ofArray cur

/// THE NEVER-WRITTEN PROOF OF STORED ALIASES, the checker's half
/// (docs/plans/plan-identity-transpose-compute.md 2.1 second step, 2.6;
/// TypeCheckSupport.recordStoredAliasMember is its one reader). Identity is
/// decided at TYPECHECK -- the output type of `method_for(A, B) <@> comm-
/// kernel` depends on it -- while a write that would refute it may come later
/// in the source, so this is a PRE-PASS over the module's SURFACE
/// declarations, computed before any declaration is checked: a syntactic,
/// module-wide, fail-closed closure that mirrors the read-only alias plan's
/// IR closure (Optimize.aliasWriteClosure) and is STRICTER than it at every
/// point, so an alias given its root's identity is always one the plan may
/// share storage for (Lowering cross-checks and records the plan's verdict,
/// `[alias-identity v1]`). Names, not binder ids (nothing is resolved yet);
/// a top-level name is unique per module (BL2009), and a local binder that
/// shadows one is scoped out by collectFreeVars.
///
/// Per top-level declaration: its binders, its FREE names, whether it holds
/// any assignment (`x = ..`, `A(i) = ..`, `p.f = ..`, `s += ..`, anywhere in
/// it, lambdas and blocks included, to ANY target -- a write to local storage
/// counts, as in the IR closure), and whether it names foreign code (a
/// qualified name, or a name an import binds: another module's bodies are not
/// judged here). A WRITING definition is one that does any of that, or names
/// a writing definition (least fixpoint). For seeds S (the root, the alias's
/// source, the alias; for a CALL root also every top-level name the root's
/// declaration reaches through references, transitively -- a call's result
/// may share storage with its arguments or with what the callee reads), the
/// MEMBERS are S plus every top-level binder whose declaration names a member
/// (a view, alias, tuple, map or call result derived from one may share its
/// storage), to a fixpoint. Ok iff no other module of the program imports
/// this one and no declaration that names a member is writing. Higher-order
/// calls need no rule of their own: code reaches a call site only through
/// names, so a call that could write a member's storage is in a declaration
/// that names both the member (or a member derived from it) and the writing
/// code (or a writing definition naming it). Builtins are assumed to write
/// nothing (no builtin takes a `mut` argument).
let private storedAliasWriteProof (decls: Located<Decl> list) (imports: ImportDecl list) (importedElsewhere: string option)
                                  : string -> bool -> string list -> Result<string, string> =
    let mapPre = Blade.Unfold.mapExprPre
    let scopesOf (d: Decl) : (Set<string> * Expr) list =
        let fnScopes (f: FunctionDecl) =
            let ps = f.Params |> List.map (_.Name) |> Set.ofList
            (ps, f.Body) :: (f.Params |> List.choose (_.Default) |> List.map (fun e -> (ps, e)))
        match d with
        | DeclLet b | DeclStatic b -> [ (Set.empty, b.Value) ]
        | DeclFunction f -> fnScopes f
        | DeclImpl impl -> impl.Methods |> List.collect fnScopes
        | DeclType (TyDeclStruct (_, _, _, cs, _)) | DeclType (TyDeclMutualGroup (_, cs)) ->
            cs |> List.map (fun e -> (Set.empty, e))
        | DeclType _ | DeclInterface _ | DeclUnit _ | DeclImport _ -> []
    let bindersOf (d: Decl) : string list =
        match d with
        | DeclLet b | DeclStatic b -> patternNames b.Pattern |> List.filter (fun n -> n <> "_")
        | DeclFunction f -> [ f.Name ]
        | DeclImpl impl -> impl.Methods |> List.map (_.Name)
        | _ -> []
    let importNames =
        decls |> List.collect (fun d ->
            match d.Value with
            | DeclImport (qn, ImportSelective ids) -> ids @ [ String.concat "." qn ]
            | DeclImport (qn, ImportQualified alias) ->
                (match alias with Some a -> [ a ] | None -> []) @ [ String.concat "." qn; List.last qn ]
            | _ -> [])
        |> List.append (imports |> List.collect (fun i ->
               (match i.Alias with Some a -> [ a ] | None -> [])
               @ (match i.Items with
                  | Some items -> items |> List.choose (fun it -> match it with ImportName n -> Some n | ImportHiding _ -> None)
                  | None -> [])
               @ [ String.concat "." i.Module; List.last i.Module ]))
        |> Set.ofList
    // (binders, free names, assigns anything, names foreign code)
    let facts =
        decls |> List.map (fun d ->
            let scopes = scopesOf d.Value
            let free = scopes |> List.map (fun (bound, e) -> collectFreeVars bound e) |> Set.unionMany
            let mutable assigns = false
            let mutable qualified = false
            let rec stmts (ss: Stmt list) =
                for s in ss do
                    match unwrapStmt s with
                    | StmtAssign _ -> assigns <- true
                    | StmtForIn (_, _, body) -> stmts body
                    | _ -> ()
            // mapExprPre does not descend into a lambda's parameter DEFAULTS,
            // an align's pad expression or a halo's offsets (Codex review of
            // this rule, finding 2: a write inside a default escaped), so
            // the visitor scans those itself.
            let rec scan (e: Expr) =
                mapPre (fun x ->
                    (match x.Kind with
                     | ExprKind.ExprAssign _ -> assigns <- true
                     | ExprKind.ExprBlock (ss, _) -> stmts ss
                     | ExprKind.ExprQualified _ -> qualified <- true
                     | ExprKind.ExprLambda (ps, _, _) -> for p in ps do Option.iter scan p.Default
                     | ExprKind.ExprAlign (_, Some { Boundary = Ast.BoundaryMode.BndPad p }) -> scan p
                     | ExprKind.ExprHalo (_, offsets) -> scan offsets
                     | _ -> ())
                    None) e |> ignore
            for (_, e) in scopes do scan e
            (bindersOf d.Value, free, assigns, qualified || not (Set.isEmpty (Set.intersect free importNames))))
        |> List.toArray
    let topNames = facts |> Array.collect (fun (bs, _, _, _) -> List.toArray bs) |> Set.ofArray
    let refsTop = facts |> Array.map (fun (_, free, _, _) -> Set.intersect free topNames)
    let declOf =
        facts |> Array.mapi (fun i (bs, _, _, _) -> bs |> List.map (fun n -> (n, i))) |> List.concat |> Map.ofList
    let writingNames =
        let mutable w = Set.empty
        let mutable writingDecls = Set.empty
        for i in 0 .. facts.Length - 1 do
            let (bs, _, assigns, foreign) = facts.[i]
            if assigns || foreign then
                writingDecls <- writingDecls.Add i
                w <- Set.union w (Set.ofList bs)
        let mutable changed = true
        while changed do
            changed <- false
            for i in 0 .. facts.Length - 1 do
                if not (writingDecls.Contains i) && not (Set.isEmpty (Set.intersect refsTop.[i] w)) then
                    let (bs, _, _, _) = facts.[i]
                    writingDecls <- writingDecls.Add i
                    w <- Set.union w (Set.ofList bs)
                    changed <- true
        w
    let writingDecl (i: int) =
        let (_, _, assigns, foreign) = facts.[i]
        assigns || foreign || not (Set.isEmpty (Set.intersect refsTop.[i] writingNames))
    let nameOfDecl (i: int) =
        match facts.[i] with
        | (b :: _, _, _, _) -> $"`{b}`"
        | _ -> $"the declaration at line {decls.[i].Span.StartLine}"
    let named (ns: Set<string>) = ns |> Set.toList |> List.map (fun n -> $"`{n}`") |> String.concat ", "
    fun (root: string) (rootIsCall: bool) (seedNames: string list) ->
        match importedElsewhere with
        | Some why -> Error why
        | None ->
        // A call's result may share storage with whatever its declaration
        // reaches (arguments, the callee's captures, transitively): those are
        // seeds too, never written either.
        let backward =
            if not rootIsCall then Set.empty
            else
                let mutable seen = Set.empty
                let rec visit (n: string) =
                    if not (seen.Contains n) then
                        seen <- seen.Add n
                        match Map.tryFind n declOf with
                        | Some i -> for m in refsTop.[i] do visit m
                        | None -> ()
                visit root
                seen.Remove root
        let seeds = Set.union (Set.ofList seedNames) backward
        let mutable members = seeds
        let mutable changed = true
        while changed do
            changed <- false
            for i in 0 .. facts.Length - 1 do
                let (bs, _, _, _) = facts.[i]
                if not (Set.isEmpty (Set.intersect refsTop.[i] members))
                   && bs |> List.exists (fun b -> not (members.Contains b)) then
                    members <- Set.union members (Set.ofList bs)
                    changed <- true
        // A root that needs its reach is judged by its OWN declaration too, and
        // by every declaration in the reach: what the root's value may share
        // is decided by that code (a call into another module, `Lib.get()`,
        // names an import and fails here).
        let reachDecls =
            if not rootIsCall then []
            else (Set.add root backward) |> Set.toList |> List.choose (fun n -> Map.tryFind n declOf)
        let offender =
            (reachDecls |> List.tryFind writingDecl)
            |> Option.orElse (
                [ 0 .. facts.Length - 1 ] |> List.tryFind (fun i ->
                    not (Set.isEmpty (Set.intersect refsTop.[i] members)) && writingDecl i))
        match offender with
        | Some i ->
            let (_, _, assigns, foreign) = facts.[i]
            let how =
                if assigns then "holds an assignment (a write to local storage counts: the copy it relies on is the one an identity-transparent alias removes)"
                elif foreign then "names another module's code or a qualified name (its writes are not judged here)"
                else $"""names a definition that may write ({named (Set.intersect refsTop.[i] writingNames)})"""
            let hit = Set.intersect refsTop.[i] members
            if hit.IsEmpty then Error $"{nameOfDecl i} (in the reach of `{root}`, whose cells may be shared with what it names) {how}"
            else Error $"{nameOfDecl i} names {named hit} and {how}"
        | None ->
            let derived = Set.difference members seeds
            let seedTxt = named (Set.ofList seedNames)
            let backTxt = if backward.IsEmpty then "" else $"; the call's reach ({named backward}) is never written either"
            Ok ($"never written (module-wide, syntactic): no assignment, mutating call or writing definition names {seedTxt}"
                + (if derived.IsEmpty then "" else $" or a value that may share their storage ({named derived})")
                + backTxt
                + "; no other module imports this one")

let checkModule (env: TypeEnv) (modul: ModuleDecl) : TypedModule * TypeEnv * CompileError list =
    let modul = { modul with Decls = generalizeArrowLetLambdas (instantiateLetLambdaKernels modul.Imports modul.Decls) }
    // The never-written proof of top-level stored aliases (plan 2.1 second
    // step): over the WHOLE module's surface, before any declaration is
    // checked (TypeCheckSupport.recordStoredAliasMember reads it).
    (let importedElsewhere =
        match env.ProgramModules with
        | [] -> Some "the program's other modules are not known to this check (fail closed)"
        | mods ->
            let me = modul.Name
            let related (qn: string list) =
                let n = min qn.Length me.Length
                n > 0 && List.truncate n qn = List.truncate n me
            let importsMe (m: ModuleDecl) =
                m.Name <> me
                && ((m.Imports |> List.exists (fun i -> related i.Module))
                    || (m.Decls |> List.exists (fun d -> match d.Value with DeclImport (qn, _) -> related qn | _ -> false)))
            match mods |> List.tryFind importsMe with
            | Some m -> Some $"""module `{String.concat "." m.Name}` imports this module (its writes are not judged here)"""
            | None -> None
     env.StoredAliasNeverWritten.Value <- storedAliasWriteProof modul.Decls modul.Imports importedElsewhere)
    // MODULE-scope ring (rec-window, P3 print replay): a module-level `let
    // rec` read by another module is observed beyond this module's tail scan.
    let ringImportedElsewhere =
        match env.ProgramModules with
        | [] -> Some "the program's other modules are not known to this check (fail closed)"
        | mods ->
            let me = modul.Name
            let related (qn: string list) =
                let n = min qn.Length me.Length
                n > 0 && List.truncate n qn = List.truncate n me
            let importsMe (m: ModuleDecl) =
                m.Name <> me
                && ((m.Imports |> List.exists (fun i -> related i.Module))
                    || (m.Decls |> List.exists (fun d -> match d.Value with DeclImport (qn, _) -> related qn | _ -> false)))
            match mods |> List.tryFind importsMe with
            | Some m -> Some ("module `" + String.concat "." m.Name + "` imports this module (its reads of the trajectory are not scanned)")
            | None -> None
    // Binder ids restart per compile: a block-local id from an earlier module
    // or program must not stop this module's reduce alias chase (G60).
    Blade.TypeCheckInfer.blockLocalVidsStorage.Value <- System.Collections.Generic.HashSet<_>()
    // Fresh module: drop any span the PREVIOUS module's decl loop left in the
    // side-channel. The static-assertion errors below are raised before this
    // module's first `checkDecl` (which is where the per-decl reset lives), so
    // without this they would be located in the previous module's coordinates.
    // `typeCheck` resets on entry too; this covers module-to-module inside one
    // compilation, and callers that reach checkProgram by another route.
    resetCurrentStmtSpan ()
    // The checker of arrow parameters' ARRAY INSTANCES, which the call
    // judgment reaches through a forward reference (it re-checks a
    // declaration). Installed here, at the one entry every check passes.
    arrowVariantHook.Value <- checkArrowVariant
    // ...and of identity-sensitive declarations' SHARING INSTANCES (plan 2.2).
    sharingVariantHook.Value <- checkSharingVariant
    // (The callee-fact tables -- defaults, where-conjuncts, `mut` positions,
    // co-iteration, units -- are keyed by binder id, program-unique, and
    // shared by reference across the program's modules: a module's entries
    // stay valid for every module that imports it, and nothing is cleared.)
    // Resolve compile-time-known static VALUES up front (the same
    // StaticEval.resolveStatics the lowering phase runs), so type-checking
    // can consult them (e.g. a `replicate` count written as `let static`).
    // `let static` is an assertion -- fold or fail loudly, not a silent
    // demotion to a runtime binding (lambda statics excepted). A circular
    // dependency, which would otherwise be silently swallowed, also lands
    // as an error on the first static decl.
    // Cross-module static import seeding (see the comment above this
    // function): seed env.StaticValues with imported entries so other
    // StaticValues consumers see them, AND splice literal substitutions
    // into a copy of this module's OWN static decls so resolveStatics's
    // fold assertion can see through a `let static x = M.k + 1` reference.
    let crossModuleStaticSeed = importedStaticSeed env modul.Decls
    let env = { env with StaticValues = Map.fold (fun acc k v -> Map.add k (StaticEval.normalizeStatic v) acc) env.StaticValues crossModuleStaticSeed }
    let declsForStaticResolution = seedImportedStaticsIntoDecls crossModuleStaticSeed modul.Decls
    let env, staticAssertErrors =
        match StaticEval.resolveStatics declsForStaticResolution with
        | Ok (se, failures) ->
            staticWidths.Clear()
            for kv in se.Widths do staticWidths.[kv.Key] <- kv.Value
            let env' = { env with StaticValues = Map.fold (fun acc k v -> Map.add k v acc) env.StaticValues se.Values }
            let errs =
                failures |> List.map (fun (f: StaticEval.StaticFailure) ->
                    let msg =
                        $"""`let static {(f.Names |> String.concat ", ")}` does not evaluate at compile time: {f.Reason}. `let static` asserts a compile-time value -- use plain `let` for values computed at runtime."""
                    locateError f.Span env' (StaticEvalErr msg))
            env', errs
        | Error msg ->
            let span =
                modul.Decls
                |> List.tryPick (fun d -> match d.Value with DeclStatic _ -> Some d.Span | _ -> None)
                |> Option.defaultValue noSpan
            env, [locateError span env (StaticEvalErr msg)]
    // Pre-pass: register static functions and static values with placeholder types
    // so forward references and mutual recursion resolve correctly.
    let preEnv =
        modul.Decls |> List.fold (fun (e: TypeEnv) locDecl ->
            match locDecl.Value with
            | DeclFunction funcDecl when funcDecl.IsStatic ->
                let paramTypes = funcDecl.Params |> List.map (fun p ->
                    match p.Type with Some t -> lowerTypeExpr e t | None -> e.Subst.Fresh())
                let retType = match funcDecl.ReturnType with
                              | Some t -> lowerTypeExpr e t
                              | None -> e.Subst.Fresh()
                let funcType = mkFuncArrow paramTypes retType
                let funcVarId = e.Builder.FreshId()
                e.DeclaredFuncIds.Add funcVarId |> ignore
                let e' = bindVarSimple funcDecl.Name funcVarId funcType e
                // Stash the AST so lowerIndexTypeList can inline the body when
                // this function appears in an eta-reduced DepIdx position.
                { e' with StaticFunctions = Map.add funcDecl.Name funcDecl e'.StaticFunctions }
            | DeclStatic binding ->
                let name = match binding.Pattern.Kind with PatternKind.PatVar n -> n | _ -> "_"
                let varId = e.Builder.FreshId()
                bindVarSimple name varId (e.Subst.Fresh()) e
            | _ -> e) env
    
    let mutable currentEnv = preEnv
    let mutable decls = []
    let mutable errors = []
    // Module-scope names already declared, first span each (BL2009), one
    // map per namespace: values (`let`/`static`/`function` share one -- the
    // bool marks a function, which keeps the function-vs-function message),
    // types, units.
    let mutable declaredValues : Map<string, Span * bool> = Map.empty
    let mutable declaredTypes : Map<string, Span> = Map.empty
    let mutable declaredUnits : Map<string, Span> = Map.empty

    // A WORK LIST rather than a plain `for`: a module-level `let rec` the ring
    // admits (TypeCheckInfer.recArrayModuleDecision) inserts its replay sweep
    // and snapshot declarations after itself and rewrites the later fixed-row
    // reads, before any of them is checked.
    let pendingDecls = ResizeArray<Located<Decl>>(modul.Decls)
    let replayDecls = System.Collections.Generic.HashSet<string>()
    let mutable declPos = 0
    while declPos < pendingDecls.Count do
        let d = pendingDecls.[declPos]
        declPos <- declPos + 1
        (match d.Value with
         | DeclLet b when (match b.Value.Kind with ExprKind.ExprRecArray _ -> true | _ -> false) ->
             (match Blade.TypeCheckInfer.recArrayModuleDecision currentEnv ringImportedElsewhere b
                        (pendingDecls |> Seq.skip declPos |> List.ofSeq) with
              | Some rest ->
                  (match b.Pattern.Kind with
                   | PatternKind.PatVar n -> replayDecls.Add (Blade.IR.recReplayBindingPrefix + n) |> ignore
                   | _ -> ())
                  pendingDecls.RemoveRange (declPos, pendingDecls.Count - declPos)
                  pendingDecls.AddRange rest
              | None -> ())
         | _ -> ())
        // BL2009 -- duplicate top-level `function` name. Without this the
        // later declaration silently rebinds the name (checkFunctionDecl's
        // bindVarSimple), and a call matching the FIRST signature dies with a
        // rank/type mismatch blaming the caller. Refuse here, at module decl
        // level, so nested `function`s (desugared to block lets, checked in
        // inferBlock) keep their legal shadowing of outer names, and imports
        // (DeclImport, not DeclFunction) cannot trip it. The duplicate decl is
        // NOT checked: references keep resolving against the first declaration,
        // so the refusal is the only diagnostic instead of the root cause plus
        // downstream mismatch noise. Prerequisite for same-name clause
        // dispatch (plan-match-statements.md §5 R1).
        //
        // The same refusal covers every other top-level namespace: a second
        // `let x` (or a `let x` beside a `function x`), a second `type`/
        // `struct`/sum type `T`, a second `Unit u`. A top-level re-`let`
        // passed the checker and then died in g++ as a redeclaration, while
        // the interpreter SHADOWED it -- two lanes, two meanings; a duplicate
        // type silently let the last declaration win. Wildcard `_` binds
        // nothing and never clashes.
        let firstSiteOf (firstSpan: Span) =
            let where = $"line {firstSpan.StartLine}, column {firstSpan.StartCol}"
            match firstSpan.File, d.Span.File with
            | Some f1, Some f2 when f1 <> f2 -> $"{f1}, {where}"
            | _ -> where
        let claim (names: string list) (table: Map<string, Span>) (kind: string) =
            match names |> List.tryPick (fun n -> Map.tryFind n table |> Option.map (fun sp -> (n, sp))) with
            | Some (n, sp) -> Some (DuplicateDecl (kind, n, firstSiteOf sp, None)), table
            | None -> None, (names |> List.fold (fun t n -> Map.add n d.Span t) table)
        let duplicateOf =
            match d.Value with
            | DeclFunction f ->
                match Map.tryFind f.Name declaredValues with
                | Some (firstSpan, true) -> Some (DuplicateFunctionDecl (f.Name, firstSiteOf firstSpan))
                | Some (firstSpan, false) -> Some (DuplicateDecl ("value", f.Name, firstSiteOf firstSpan, None))
                | None ->
                    declaredValues <- Map.add f.Name (d.Span, true) declaredValues
                    None
            | DeclLet b | DeclStatic b ->
                let names = patternNames b.Pattern |> List.filter (fun n -> n <> "_")
                match names |> List.tryPick (fun n -> Map.tryFind n declaredValues |> Option.map (fun (sp, _) -> (n, sp))) with
                | Some (n, sp) -> Some (DuplicateDecl ("value", n, firstSiteOf sp, None))
                | None ->
                    for n in names do declaredValues <- Map.add n (d.Span, false) declaredValues
                    None
            | DeclType td ->
                let err, table = claim (typeDeclNames td) declaredTypes "type"
                declaredTypes <- table
                err
            | DeclUnit u ->
                let err, table = claim [ u.Name ] declaredUnits "unit"
                declaredUnits <- table
                err
            | _ -> None
        match duplicateOf with
        | Some dupErr ->
            // The duplicate decl skips checkDecl, whose per-decl reset would
            // otherwise clear the PREVIOUS decl's expression span -- without
            // this, locateError's precision order picks that stale span and
            // the refusal points into the FIRST declaration's body.
            resetCurrentStmtSpan ()
            let ce = locateError d.Span currentEnv dupErr
            errors <- ce :: errors
        | None ->

        // Pre-validation: inline TyEnumIdx<[mixed values]> occurrences. The
        // alias-site check in registerTypeDecl catches `type X = EnumIdx<[...]>`
        // declarations but not inline embeddings like `let x: Array<EnumIdx<[1,
        // "two"]> like ...> = ...`. Each finding becomes an error attached to
        // the decl's span.
        let mixedFindings = collectMixedEnumIdxInDecl d.Value
        for _ in mixedFindings do
            let err = IndexTypeErr "Inline EnumIdx<[...]> has mixed value kinds (integer and string literals in the same list). The runtime backing must be one or the other (int64_t or std::string)."
            let ce = locateError d.Span currentEnv err
            errors <- ce :: errors

        let declName =
            match d.Value with
            | DeclLet b -> $"""in let binding '{(match b.Pattern.Kind with PatternKind.PatVar n -> n | _ -> "_")}'"""
            | DeclStatic b -> $"""in static binding '{(match b.Pattern.Kind with PatternKind.PatVar n -> n | _ -> "_")}'"""
            | DeclFunction f -> $"in function '{f.Name}'"
            | DeclType td ->
                match td with
                | TyDeclAlias (n, _, _) | TyDeclStruct (n, _, _, _, _) | TyDeclSum (n, _, _) -> $"in type '{n}'"
                | TyDeclMutualGroup (members, _) ->
                    $"""in mutual group '{(members |> List.map fst |> String.concat ", ")}'"""
            | DeclInterface i -> $"in interface '{i.Name}'"
            | DeclImpl impl -> $"in impl for '{Blade.StructIdxFence.typeExprLabel impl.ForType}'"
            | DeclImport (qn, _) -> $"""in import '{(String.concat "." qn)}'"""
            | DeclUnit u -> $"in unit '{u.Name}'"
        let envWithCtx = pushContext declName currentEnv
        // A module ring's replay sweep re-checks its recursion's step: the
        // warnings it raises are x's own, already reported (rollback).
        let isReplayDecl =
            match d.Value with
            | DeclLet { Pattern = { Kind = PatternKind.PatVar n } } ->
                n.StartsWith Blade.IR.recReplayBindingPrefix && replayDecls.Contains n
            | _ -> false
        let restoreWarnings =
            if isReplayDecl then
                let w = Blade.TypeCheckInfer.warningChannelsRestorePoint currentEnv
                let d0 = Blade.Effects.Decisions.mark ()
                (fun () -> w (); Blade.Effects.Decisions.rollback d0)
            else ignore
        // A user error the type lowering found where it has no error channel
        // (TypeEnv.TypeErrorRaised) is this declaration's error like any other.
        let declResult, raisedCode =
            try checkDecl envWithCtx d.Value, None
            with TypeErrorRaised (te, code) -> Error te, code
        restoreWarnings ()
        match declResult with
        | Ok (td, env') ->
            decls <- td :: decls
            // Carry forward env' but restore original context (don't nest)
            currentEnv <- { env' with Context = currentEnv.Context }
        | Error err ->
            let ce = { locateError d.Span currentEnv err with Code = raisedCode }
            errors <- ce :: errors
            // Continue with pre-failure env, but bind the failed decl's
            // name(s) to a FRESH inference var so downstream references
            // resolve to *some* type instead of erroring `Unbound variable`.
            // Without this, one bad annotation smears ~N spurious
            // Unbound-variable diagnostics across the rest of the module and
            // buries the real root cause. A fresh (unsolved) var unifies with
            // anything, so it silences the cascade without manufacturing a
            // second layer of false errors the way binding the *annotation*
            // type would. (Value bindings only -- type/interface/impl/import/
            // unit decls don't produce value-scope names that cascade here.)
            let recoveryNames =
                match d.Value with
                | DeclLet b | DeclStatic b -> patternNames b.Pattern
                | DeclFunction f -> [f.Name]
                | _ -> []
            for n in recoveryNames do
                let varId = currentEnv.Builder.FreshId()
                currentEnv <- bindVarSimple n varId (currentEnv.Subst.Fresh()) currentEnv

    // ARROW APPLICATIONS still pending (TypeEnv.ArrowApplication): a head bound
    // since (a kernel's row, a later call's argument) types its application
    // now, and a head nothing ever reached is read as a FUNCTION of its
    // arguments -- the safer bet, and never a zonk-defaulted scalar applied in
    // g++. A refusal is located at the application; it is reported only on an
    // otherwise clean module (after an earlier error the heads may be the
    // recovery's fresh variables, and the refusal would be a cascade).
    let arrowErrors = settleArrowApps currentEnv true
    if List.isEmpty errors then
        for (span, err) in arrowErrors do
            resetCurrentStmtSpan ()
            errors <- locateError span currentEnv err :: errors
    // ARRAY INSTANCES of arrow parameters (TypeEnv.ArrowVariant). A call whose
    // arrow argument was still open when it was judged and is an array now is
    // re-targeted at its declaration's array instance (retargetArrowCalls),
    // over this module's declarations and every instance made meanwhile --
    // whose own bodies may hold such calls. Then each instance is placed right
    // after its ORIGIN declaration (before every use of it, since a use
    // follows the origin), or -- an origin of an earlier module -- after this
    // module's imports.
    let decls =
        let ordered = List.rev decls
        if List.isEmpty errors then
            let walked = System.Collections.Generic.HashSet<IRId>(HashIdentity.Structural)
            let found = ResizeArray<Span * TypeError>()
            let rec settle (roots: TypedExpr list) (fuel: int) =
                found.AddRange (retargetArrowCalls currentEnv roots)
                let fresh =
                    currentEnv.ArrowVariantDecls
                    |> Seq.choose (fun (_, d) ->
                        match d with
                        | TDeclFunction f when walked.Add f.FuncId -> Some f.Body
                        | _ -> None)
                    |> List.ofSeq
                if not fresh.IsEmpty && fuel > 0 then settle fresh (fuel - 1)
            // Every instance made so far is walked from the module's
            // declarations or below; one made by the walk is walked in turn.
            let preexisting =
                currentEnv.ArrowVariantDecls
                |> Seq.choose (fun (_, d) ->
                    match d with
                    | TDeclFunction f when walked.Add f.FuncId -> Some f.Body
                    | _ -> None)
                |> List.ofSeq
            settle ((ordered |> List.collect declExprs) @ preexisting) 64
            for (span, err) in found do
                resetCurrentStmtSpan ()
                errors <- locateError span currentEnv err :: errors
        let variants = List.ofSeq currentEnv.ArrowVariantDecls
        currentEnv.ArrowVariantDecls.Clear()
        if variants.IsEmpty then ordered
        else
            let ofOrigin (fid: IRId) = variants |> List.filter (fun (o, _) -> o = fid) |> List.map snd
            let here =
                ordered |> List.choose (fun d -> match d with TDeclFunction f -> Some f.FuncId | _ -> None) |> Set.ofList
            let foreign = variants |> List.filter (fun (o, _) -> not (here.Contains o)) |> List.map snd
            let placed =
                ordered |> List.collect (fun d ->
                    match d with
                    | TDeclFunction f -> d :: ofOrigin f.FuncId
                    | _ -> [ d ])
            let lastImport = placed |> List.tryFindIndexBack (fun d -> match d with TDeclImport _ -> true | _ -> false)
            match lastImport with
            | Some k ->
                let before, after = List.splitAt (k + 1) placed
                before @ foreign @ after
            | None -> foreign @ placed
    let typedModule = { Name = Some modul.Name; Decls = decls }
    // Zonk: resolve all IRTInfer through the substitution, default unsolved to Float64
    // The zonk walk also retypes index POSITIONS and guards unproven
    // subscripts into named index types (Zonk.fs, SUBSCRIPT POSITIONS AND
    // GUARDS); its context carries the builder for guard bindings.
    let enumKeyErrorSink = System.Collections.Generic.List<Span * string>()
    let zonked =
        let saved = subscriptGuardCtx.Value
        let rangeFedSet, rangeFedBounds = rangeFedLambdaParamsWithBounds typedModule
        subscriptGuardCtx.Value <-
            Some { FreshId = (fun () -> currentEnv.Builder.FreshId())
                   Positions = System.Collections.Generic.HashSet<IRId>()
                   DataVars = System.Collections.Generic.HashSet<IRId>()
                   EnumKeyErrors = enumKeyErrorSink
                   RangeFedParams = rangeFedSet
                   RangeFedBounds = rangeFedBounds
                   CoordVars = System.Collections.Generic.HashSet<IRId>()
                   Retargets = currentEnv.ArrowRetargets
                   Subscripts = currentEnv.ArrowSubscripts
                   GroupOuter = fun id ->
                       match currentEnv.GroupOuters.TryGetValue id with
                       | true, gk -> Some gk
                       | _ -> None
                   EnumLabels = fun tag ->
                       match Map.tryFind tag currentEnv.TypeDefs with
                       | Some (TDIEnumIdx (_, _, values, _)) when EnumValue.allString values ->
                           Some (values |> List.choose (function EVString s -> Some s | _ -> None))
                       | _ -> None
                   EnumKeys = fun tag -> intEnumKeysOfTag currentEnv tag
                   IndexExtent = fun tag ->
                       match Map.tryFind tag currentEnv.TypeDefs with
                       | Some (TDIIndexType (_, idx, _)) when idx.IxKind = IxKPlain && idx.Symmetry = SymNone ->
                           tryEvalIntIR idx.Extent
                       | _ -> None
                   IndexExtentVar = fun tag ->
                       match Map.tryFind tag currentEnv.TypeDefs with
                       | Some (TDIIndexType (_, idx, _)) when idx.IxKind = IxKPlain && idx.Symmetry = SymNone ->
                           // `Idx<nr>` over a runtime `let nr` lowers to the
                           // symbolic IRParam "nr" (TypeLower.lowerExtentExpr);
                           // the module binding of that name is the extent.
                           (match idx.Extent with
                            | IRParam (name, _, _) when name <> "?" ->
                                (match lookupVar name currentEnv with
                                 | Some vi ->
                                     (match currentEnv.Subst.Resolve vi.Type |> IR.stripUnits with
                                      | IRTScalar (ETInt64 | ETInt32) as t -> Some (name, vi.VarId, t)
                                      | _ -> None)
                                 | None -> None)
                            | _ -> None)
                       | _ -> None }
        try zonkModule currentEnv.Subst typedModule
        finally subscriptGuardCtx.Value <- saved
    // Late direct-application rank check, on the zonked tree -- see
    // collectAppRankErrors. Suppressed when the module already has errors:
    // a failed decl binds its name to a fresh var (the cascade guard above),
    // and calls through that var would report rank noise on top of the real
    // root cause.
    let rankErrors =
        if List.isEmpty errors && List.isEmpty staticAssertErrors then
            zonked.Decls |> List.collect declExprs
                         |> List.collect (collectAppRankErrors currentEnv.Subst)
        else []
    // Tree-slot laundering and same-rank tree-shape mismatch at direct
    // applications -- the safety half of P5's open signature door, sharing this
    // sweep's zonked-module reasoning and its cascade suppression (a failed decl
    // binds a fresh var, and calls through it would report tree noise over the
    // real root cause). See collectAppTreeErrors: both refusals were measured to
    // pass silently before it existed.
    let treeArgErrors =
        if List.isEmpty errors && List.isEmpty staticAssertErrors then
            zonked.Decls |> List.collect declExprs
                         |> List.collect (collectAppTreeErrors currentEnv.Subst)
        else []
    // The subscript judgment's post-zonk half (collectSubscriptErrors): the
    // class and nominal rules over every subscript and every index-typed
    // call argument, now that no kernel parameter is still open. Same
    // cascade suppression as the rank sweep.
    let subscriptErrors =
        if List.isEmpty errors && List.isEmpty staticAssertErrors then
            zonked.Decls |> List.collect declExprs
                         |> List.collect (collectSubscriptErrors currentEnv)
        else []
    // The mixing rule's post-zonk half (collectNumericMixErrors): operators
    // whose operands were open when inferBinOp met them, judged now that no
    // kernel parameter is. Same cascade suppression as the rank sweep; one
    // error per declaration (the first offender is the root cause).
    let mixErrors =
        if List.isEmpty errors && List.isEmpty staticAssertErrors && List.isEmpty subscriptErrors then
            // On the UNZONKED tree: a variable nothing pinned (a returned
            // section's parameter) is still open there, where zonk's
            // Float64 default would invent a mix nobody wrote.
            typedModule.Decls |> List.collect declExprs
                              |> List.collect (collectNumericMixErrors currentEnv)
                         |> List.truncate 1
        else []
    // Misplaced provider writes: structural, inference-independent (an
    // unresolved receiver simply fails the IRTNamed match), so unlike the rank
    // sweep it runs even when the module already has errors.
    // A literal of an integer EnumIdx that is none of its keys
    // (collectUnknownEnumKeys): judged on the UNZONKED tree.
    let enumKeyErrors =
        if List.isEmpty errors && List.isEmpty staticAssertErrors then
            (typedModule.Decls |> List.collect declExprs
                               |> List.collect (collectUnknownEnumKeys currentEnv))
            @ (enumKeyErrorSink |> Seq.map (fun (sp, msg) ->
                   ({ Error = EnumKeyObserved msg; Span = sp; Context = []; Code = None } : CompileError))
               |> Seq.distinctBy (fun e -> e.Span) |> List.ofSeq)
        else []
    // A `zero` at a sum type (collectValuelessZeros): judged on the zonked
    // types, with the same cascade suppression as the subscript sweep.
    let zeroErrors =
        if List.isEmpty errors && List.isEmpty staticAssertErrors then
            zonked.Decls |> List.collect declExprs
                         |> List.collect (collectValuelessZeros currentEnv)
        else []
    let writeErrors =
        zonked.Decls |> List.collect declWriteRoots
                     |> List.collect (fun (nested, e) -> collectMisplacedProviderWrites currentEnv.Subst nested e)
    // group_keys escapes: structural like the write sweep (IRTGroupKeys is
    // minted in exactly one place and never inferred), so it runs even when
    // the module already has errors.
    let groupKeysErrors =
        zonked.Decls |> List.collect declGroupKeysRoots
                     |> List.collect (fun (pos, e) -> collectGroupKeysEscapes currentEnv.Subst pos e)
    // Escaping closures that share a reassigned local (collectMutCaptureEscapes):
    // structural, so it runs even when the module already has errors.
    let mutCaptureErrors =
        zonked.Decls |> List.collect declMutCaptureRoots
                     |> List.collect (fun (pos, e) -> collectMutCaptureEscapes currentEnv.Subst pos e)
    (zonked, currentEnv, staticAssertErrors @ List.rev errors @ rankErrors @ treeArgErrors @ subscriptErrors @ mixErrors @ enumKeyErrors @ zeroErrors @ writeErrors @ groupKeysErrors @ mutCaptureErrors)

let checkProgram (program: Program) : TypedProgram * IRBuilder * CompileError list * string list =
    resetInferSideTables ()
    Blade.Types.StoredAliasIdentity.reset ()
    let env = emptyEnv ()
    let mutable modules = []
    let mutable allErrors = []
    let mutable moduleExports = Map.empty<string, TypeModuleExport>
    for (i, modul) in List.indexed program.Modules do
        let moduleName = modul.Name |> String.concat "."
        // D11: a non-main module's declarations get the identity
        // `<module>.<name>` (Types.TypeOrigins); the main module -- the
        // entry, which every program-assembling path puts LAST (the rule
        // Lowering.qualifyModuleNames uses for values) -- keeps bare names.
        let qualifier =
            if Blade.Types.TypeOrigins.isMainModule i program.Modules.Length || moduleName = "" then None
            else Some moduleName
        let envWithExports = { env with ModuleExports = moduleExports; ModuleQualifier = qualifier; ProgramModules = program.Modules }
        let (tm, finalEnv, errs) = checkModule envWithExports modul
        modules <- tm :: modules
        allErrors <- allErrors @ errs
        // Build export from this module's checked environment. TYPES: every
        // qualified identity the module's tables hold (its own and,
        // transitively, the ones its API mentions) under its identity; the
        // module's own names as TypeNames / CtorNames for the importer's
        // scope keys; other undotted entries (provider structs) as before.
        // Provider axes (`store.index.d`) stay out: dotted but no identity.
        let isScopeKey (k: string) =
            finalEnv.LocalTypeNames.ContainsKey k || finalEnv.ImportedTypeNames.ContainsKey k
        let isCtorScopeKey (k: string) =
            finalEnv.LocalCtorNames.ContainsKey k || finalEnv.ImportedCtorNames.ContainsKey k
        let export : TypeModuleExport = {
            Variables = finalEnv.Variables |> Map.filter (fun k _ -> not (k.Contains(".")))
            TypeDefs =
                finalEnv.TypeDefs |> Map.filter (fun k _ ->
                    Blade.Types.TypeOrigins.isQualified k || (not (k.Contains(".")) && not (isScopeKey k)))
            VariantTags =
                finalEnv.VariantTags |> Map.filter (fun k _ ->
                    Blade.Types.TypeOrigins.isQualified k || (not (k.Contains(".")) && not (isCtorScopeKey k)))
            TypeNames = finalEnv.LocalTypeNames
            CtorNames = finalEnv.LocalCtorNames
            Units = finalEnv.Units
            StaticFunctions = finalEnv.StaticFunctions |> Map.filter (fun k _ -> not (k.Contains(".")))
            StaticValues = finalEnv.StaticValues |> Map.filter (fun k _ -> not (k.Contains(".")))
        }
        moduleExports <- Map.add moduleName export moduleExports
    allErrors <- allErrors @ crossModuleDeclErrors env program
    // env.Warnings is shared by reference across all envWithExports updates
    // (mutable ResizeArray, not a Map), so all module-scope warnings
    // accumulate here.
    let warnings = env.Warnings |> Seq.toList
    ({ Modules = List.rev modules }, env.Builder, allErrors, warnings)

// 13. Public Entry Point

/// Type check a program. Returns the typed program, builder, and any
/// non-fatal warnings in the Ok case; or compile errors in the Error case.
/// (Warnings emitted before a hard error is encountered are currently
/// dropped on the Error path; that's a separate refinement.)
///
/// Pre-pass: IndexTypeValidator enforces the rules for where index types may
/// appear in declaration-level type expressions. Validation errors abort
/// compilation early -- once an AST passes validation, downstream lowering
/// can assume index types only appear in their permitted positions.
/// IDE side-channel: the partial typed program + builder from the most recent
/// checkProgram. typeCheck discards these when the checker reports errors, but
/// editor tooling (Ide.fs) still wants bindings/types for the parts that DID
/// check, so a file with errors keeps its hovers. Reset at the top of typeCheck
/// and recorded after checkProgram; None means the pre-check pipeline failed
/// (no typed program produced). AsyncLocal, like ProviderRegistry.IdeStores.
module IdePartial =
    let private slot = new System.Threading.AsyncLocal<(TypedProgram * IRBuilder) option>()
    let reset () = slot.Value <- None
    let record (tp: TypedProgram) (b: IRBuilder) = slot.Value <- Some (tp, b)
    let get () : (TypedProgram * IRBuilder) option =
        match box slot.Value with null -> None | _ -> slot.Value

/// The stores this program loads that CANNOT BE READ at compile time, as
/// BL2008 errors at their load sites.
///
/// The checker reports exactly this when it reaches `let store =
/// alias.load("path")` -- but the elaborations run first, and they resolve
/// `store.index.<dim>` and provider-backed statics through a reader that
/// answers "not foldable" for a store it cannot open. So the first stage to
/// need a static extent from a missing store refuses with an error about
/// ITSELF (`ifft2: every axis extent ... must be statically known`), the
/// pipeline stops there, and the load site is never reached. This is the
/// same question asked directly, so the cause can be reported with (and
/// ahead of) its consequence.
///
/// Consulted ONLY after a stage has already failed: a program that checks
/// pays nothing, and no store is read twice on the success path. A native
/// library that will not load is left to the checker's BL2007.
let private unreadableStoreErrors (program: Program) : CompileError list =
    let program = Blade.ProviderDesugar.desugarOrIdentity program
    [ for m in program.Modules do
        let aliases =
            m.Decls |> List.fold (fun acc d ->
                match d.Value with
                | DeclImport ([pname], ImportQualified aliasOpt)
                    when (Blade.ProviderRegistry.tryFind pname).IsSome ->
                    Map.add (aliasOpt |> Option.defaultValue pname) pname acc
                | _ -> acc) Map.empty
        if not aliases.IsEmpty then
            for d in m.Decls do
                match d.Value with
                | DeclLet { Pattern = { Kind = PatternKind.PatVar root }; Value = ({ Kind = ExprKind.ExprApp ({ Kind = ExprKind.ExprField ({ Kind = ExprKind.ExprVar alias }, "load") }, [{ Kind = ExprKind.ExprLit (LitString path) }]) } as value) }
                | DeclStatic { Pattern = { Kind = PatternKind.PatVar root }; Value = ({ Kind = ExprKind.ExprApp ({ Kind = ExprKind.ExprField ({ Kind = ExprKind.ExprVar alias }, "load") }, [{ Kind = ExprKind.ExprLit (LitString path) }]) } as value) }
                    when aliases.ContainsKey alias ->
                    let pname = aliases.[alias]
                    let failure =
                        try
                            (Blade.ProviderRegistry.tryFind pname).Value.LoadAsModule (IRBuilder()) root path |> ignore
                            None
                        with
                        | :? System.DllNotFoundException -> None
                        | :? System.TypeInitializationException as tix
                            when (tix.InnerException :? System.DllNotFoundException) -> None
                        | ex -> Some ex.Message
                    match failure with
                    | Some detail ->
                        yield { Error = ProviderStoreUnresolvable (pname, path, detail)
                                Span = (if value.Span = noSpan then d.Span else value.Span)
                                Context = []
                                Code = None }
                    | None -> ()
                | _ -> () ]

let rec typeCheck (program: Program) : Result<TypedProgram * IRBuilder * string list, CompileError list> =
    match typeCheckStages program with
    | Ok _ as ok -> ok
    | Error errors ->
        let isStoreError (e: CompileError) =
            match e.Error with
            | ProviderStoreUnresolvable _ | ProviderNativeLoadFailure _ -> true
            | _ -> false
        // The checker already named a store: nothing to add.
        if errors |> List.exists isStoreError then Error errors
        else
            match (try unreadableStoreErrors program with _ -> []) with
            | [] -> Error errors
            // Cause first, then what it broke.
            | stores -> Error (stores @ errors)

and private typeCheckStages (program: Program) : Result<TypedProgram * IRBuilder * string list, CompileError list> =
    // AST -> AST expansions, in order: ML-op elaboration first (so grad()
    // sees the generated functions as plain Blade source and can inline
    // them), then grad() expansion. Both synthesize ordinary declarations
    // that flow through validation, checking, lowering and codegen exactly
    // like user code.
    // Provider-backed statics: install the compile-time data reader before
    // ANY resolveStatics pass runs (the ML and PPL elaborations each run
    // their own; all inherit the fold through StaticEval's hook).
    Blade.ProviderStatics.install ()
    // A fresh fold log for this program (the run record's `content`-identity
    // inputs; see ProviderStatics.resetFoldLog).
    Blade.ProviderStatics.resetFoldLog ()
    // The constrained-index counting layer's `idx_card(R)` builtin, on the
    // same footing and for the same reason: registered before ANY
    // resolveStatics pass, so every elaboration's own statics can size
    // against it.
    Blade.StructIdxSpec.install ()
    // The `__ad_body` conjunct Grad stamps on synthesized derivatives (its
    // body scope vetoes implicit unit-scale conversions -- Constraints.fs).
    Blade.Constraints.registerAdBody ()
    IdePartial.reset ()
    PinSuggestions.reset ()
    WarningLog.reset ()
    DeducedFacts.reset ()
    // Error-location side-channel: AsyncLocal, so without this a second
    // compilation in one process inherits the FIRST one's last-stamped span.
    // `checkDecl` resets it per declaration, but errors raised before any
    // declaration is checked -- the elaborations below, and `checkModule`'s
    // `let static` fold assertion -- run before that and would otherwise be
    // located in the previous source's coordinates. The real exposure is the
    // long-lived `blade ide check` path, not just the test host.
    resetCurrentStmtSpan ()
    // Phase B typed rep-deduction channel: same lifecycle as the facts channel
    // beside it. The per-module SUMMARY tables need no reset -- they hang off
    // the TypeEnv that `emptyEnv ()` builds fresh below -- but the proposal
    // channel and the skipped-polymorphic tally are AsyncLocal and would
    // otherwise accumulate across compilations in one process (the test host).
    Blade.DeduceRep.TypedCertProposals.reset ()
    Blade.DeduceRep.SkippedPolymorphic.reset ()
    // The rep-check's two channels, same lifecycle: the disagreement list must not
    // carry across compilations (it becomes compile errors), and the census
    // must count THIS program's certified decls, not the test host's history.
    Blade.DeduceRep.RepCheckDisagreements.reset ()
    Blade.DeduceRep.RepCheckCensus.reset ()
    // Register the typed polynomial engine as DeduceRep's discharger.
    // DeduceRep compiles at index 29 and cannot name Blade.ML.PolyExtractTyped
    // (index 120), so the dependency is inverted through the hook slot, tied
    // here where both are visible. Registered at every `typeCheck` entry
    // (idempotent) so the production adapter is ALWAYS installed for a real
    // compilation even if a test cleared the slot; `EngineDischarge.clear ()`
    // stays usable for test isolation.
    //
    // LIEGUARDFAILURE IS CONVERTED, NOT SWALLOWED: `engineVerdict` deliberately
    // RE-RAISES `LieDischarge.LieGuardFailure` (the post-accept float guard, a
    // compiler-bug assert, not a decoder refusal). Left alone it would be
    // eaten by an outer try/with and lost silently. Catching it HERE and
    // returning a refutation preserves its meaning: in CHECKING it surfaces
    // as a disagreement (correct for an internal guard trip); in DEDUCTION
    // it is simply a decline. No other exception gets this treatment.
    Blade.DeduceRep.EngineDischarge.register (fun resolve parms sg body ->
        try
            match Blade.ML.PolyExtractTyped.engineVerdict resolve parms sg body with
            | Some Blade.ML.PolyExtractTyped.EngineHolds ->
                Some Blade.DeduceRep.EngineConfirms
            | Some (Blade.ML.PolyExtractTyped.EngineRefutes msg) ->
                Some (Blade.DeduceRep.EngineRefutes msg)
            | None -> None
        with Blade.ML.LieDischarge.LieGuardFailure msg ->
            Some (Blade.DeduceRep.EngineRefutes
                    $"the Lie-discharge post-accept guard tripped while validating this body: {msg}"))
    IdeDeductions.reset ()
    // Provider checkout desugar BEFORE EVERYTHING: `repo.checkout("v1.0",
    // ic.tag)` becomes the ordinary load shape `ic.load("path@tag:v1.0")`,
    // so typecheck, every elaborator's own `resolveStatics`, and
    // `checkModule`'s provider-roots scan all see a form they already
    // handle. It has to be first because Unfold, immediately below, is
    // itself the earliest `resolveStatics` consumer -- and StaticEval's miss
    // would be SILENT ("not foldable"), not an error. No-op, and
    // reference-equal, for programs with no `import icechunk`.
    match Blade.ProviderDesugar.expand program with
    | Error diags -> Error (diags |> List.map (compileErrorOfDiagnostic ["provider checkout desugar"]))
    | Ok program ->
    // Staged-former unfold FIRST of the elaborations: `static
    // method_for/object_for/for` argument lists elaborate to plain formers
    // before any other stage (ML/PPL/math/grad and the checker never see
    // ExprStatic).
    match Blade.Unfold.expand program with
    | Error diags -> Error (diags |> List.map (compileErrorOfDiagnostic ["static unfold"]))
    | Ok program ->
    match Blade.ML.Elaborate.expand program with
    | Error diags -> Error (diags |> List.map (compileErrorOfDiagnostic ["ML elaboration"]))
    | Ok program ->
    // sgs runs AFTER ML so the (future) ml.galilean judgment sees surface
    // `sgs.*` op calls at ML's seam, and before PPL/Math/Grad so its
    // generated plain source flows through them untouched.
    match Blade.Sgs.Elaborate.expand program with
    | Error diags -> Error (diags |> List.map (compileErrorOfDiagnostic ["sgs elaboration"]))
    | Ok program ->
    match Blade.Ppl.Elaborate.expand program with
    | Error diags -> Error (diags |> List.map (compileErrorOfDiagnostic ["PPL elaboration"]))
    | Ok program ->
    match Blade.Math.Elaborate.expand program with
    | Error diags -> Error (diags |> List.map (compileErrorOfDiagnostic ["math elaboration"]))
    | Ok program ->
    match Blade.Rand.Elaborate.expand program with
    | Error diags -> Error (diags |> List.map (compileErrorOfDiagnostic ["rand elaboration"]))
    | Ok program ->
    match Blade.Spectra.Elaborate.expand program with
    | Error diags -> Error (diags |> List.map (compileErrorOfDiagnostic ["spectra elaboration"]))
    | Ok program ->
    // display LAST of the module elaborations: a frame is a side effect on an
    // already-elaborated payload, so nothing downstream needs to see the
    // surface `alias.emit(...)` call.
    match Blade.Display.Elaborate.expand program with
    | Error diags -> Error (diags |> List.map (compileErrorOfDiagnostic ["display elaboration"]))
    | Ok program ->
    // C7: fuse `>>@` / `@>>` / `<$>` pipelines into ordinary maps for EVERY
    // program, not just differentiated ones. The proved Compose-Apply
    // identity (formalism.md 10.3), so it cannot change an answer -- and it
    // is what makes three-stage chains, multi-operand pipelines, and
    // function-body `let p = o1 >>@ o2` / `f <$> c` / `c1 @>> c2` emit at
    // all. Shapes it declines fall through to the IRComposeApply path
    // unchanged. Runs BEFORE grad expansion so AD inherits fused bodies.
    // G205: a let-bound lambda value is spliced at each KERNEL use (typed per
    // use, inline), ahead of fusion, which reads the same surface
    let program = { program with Modules = program.Modules |> List.map (fun m -> { m with Decls = instantiateLetLambdaKernels m.Imports m.Decls }) }
    let program = Blade.Grad.fuseProgram program
    match Blade.Grad.expand program with
    | Error diags -> Error (diags |> List.map (compileErrorOfDiagnostic ["grad expansion"]))
    | Ok program ->
    let validationErrors = IndexTypeValidator.validateProgram program
    if not validationErrors.IsEmpty then
        let compileErrors =
            validationErrors |> List.map (fun e ->
                { Error = IndexTypeErr e.Message; Span = e.Span; Context = [e.DeclName]; Code = Some "BL4003" })
        Error compileErrors
    else
        let (tp, builder, errors, warnings) = checkProgram program
        IdePartial.record tp builder
        // Certificate suggestions (BL4011 equivariance, BL4014 galilean) ride
        // the ordinary warning channel like the BL4010 storage pins: plain
        // strings here (what the CLI prints), structured (message, span)
        // pairs in Equiv.CertSuggestions / Galilean.GalCertSuggestions (what
        // the editor ghost-renders). Appended AFTER the checker's own --
        // equiv then galilean -- the same order
        // `Lowering.typeCheckWarningDiagnostics` assembles, so strings and
        // diagnostics stay parallel.
        let warnings =
            warnings
            @ (Blade.ML.Equiv.CertSuggestions.get () |> List.map fst)
            @ (Blade.ML.Galilean.GalCertSuggestions.get () |> List.map fst)
        // Drain the declared-certificate agreement channel. An entry here
        // means the typed walker and the seam checker reached CONTRADICTORY
        // judgments about the same certified body -- not the user's fault,
        // since the seam already accepted the program. Surfaces as an
        // INTERNAL COMPILER ERROR (BL9004) and stops the build: a compiler
        // that knows two of its own judgments disagree must not quietly
        // emit code. Abstentions are silent by construction and never reach
        // here.
        let repIce =
            Blade.DeduceRep.RepCheckDisagreements.get ()
            |> List.map (fun (owner, detail, span) ->
                { Error =
                    InternalErr $"internal compiler error: equivariance certificate validation disagrees with the elaboration checker for '{owner}': {detail}. This is a bug in the Blade compiler, not in your program -- please report it (the certificate itself was accepted by the checking authority; only the typed second opinion dissents)"
                  Span = span
                  Context = [ "equiv certificate validation" ]
                  Code = Some "BL9004" })
        let errors = errors @ repIce
        if errors.IsEmpty then Ok (tp, builder, warnings)
        else Error errors

// Re-exported so Ide.fs/Cli keep their single Blade.TypeCheck surface for
// these two; the AsyncLocal channel MODULES cannot be re-exported (module
// abbreviations are file-private in F#), so their consumers open
// Blade.TypeCheckIde directly.
let typedExprChildren = TypeCheckSupport.typedExprChildren
let builtinScalarNames = TypeLower.builtinScalarNames
