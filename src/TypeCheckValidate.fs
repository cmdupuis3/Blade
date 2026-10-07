// Post-zonk structural validators: inline mixed-EnumIdx pre-validation,
// cross-module static-import rewriting, and the whole-module sweeps for
// application rank errors, misplaced provider writes, and group_keys
// escapes.
module Blade.TypeCheckValidate

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

// After type checking, some IRTInfer nodes may remain in the typed AST.
// These are either (a) solved but unresolved (the substitution knows the answer
// but nobody called Resolve on that particular node), or (b) genuinely ambiguous
// (no constraints were generated). Zonking walks the entire typed AST, resolves
// every type through the substitution, and defaults any remaining unknowns to
// Float64. After zonking, no IRTInfer should remain in the program.

// 11c. Type expression pre-validation
// Walks every TypeExpr in a module looking for inline `EnumIdx<[...]>` with
// mixed value kinds (ints and strings in the same list). The aliased case
// `type X = EnumIdx<[1, "two"]>` is caught by registerTypeDecl's per-decl
// validation; this pre-pass covers the inline cases that the aliased check
// can't see, e.g. `let x: Array<EnumIdx<[1, "two"]> like ...> = ...`.

let internal isMixedEnumIdxValues (valuesExpr: Expr) : bool =
    match valuesExpr.Kind with
    | ExprKind.ExprArrayLit elems ->
        let isInt (e: Expr) =
            match e.Kind with
            | ExprKind.ExprLit (LitInt _) | ExprKind.ExprUnaryOp (OpNeg, { Kind = ExprKind.ExprLit (LitInt _) }) -> true
            | _ -> false
        let isString (e: Expr) = match e.Kind with ExprKind.ExprLit (LitString _) -> true | _ -> false
        let hasInt = elems |> List.exists isInt
        let hasString = elems |> List.exists isString
        hasInt && hasString
    | _ -> false

let rec internal walkTypeExprForMixedEnumIdx (ty: TypeExpr) : Expr list =
    let here =
        match ty with
        | TyEnumIdx v when isMixedEnumIdxValues v -> [v]
        | _ -> []
    let children =
        match ty with
        | TyArray (elem, idxs) ->
            walkTypeExprForMixedEnumIdx elem @ (idxs |> List.collect walkTypeExprForMixedEnumIdx)
        | TyAbstractArray (elem, _, _) -> walkTypeExprForMixedEnumIdx elem
        | TyFunc (args, ret) ->
            (args |> List.collect walkTypeExprForMixedEnumIdx) @ walkTypeExprForMixedEnumIdx ret
        | TyTuple ts -> ts |> List.collect walkTypeExprForMixedEnumIdx
        | TyDepIdx (outer, _, body) ->
            walkTypeExprForMixedEnumIdx outer @ walkTypeExprForMixedEnumIdx body
        | TyConstrained (inner, _) -> walkTypeExprForMixedEnumIdx inner
        | TyBounded (inner, _, _) -> walkTypeExprForMixedEnumIdx inner
        | TyPoly inner -> walkTypeExprForMixedEnumIdx inner
        | TyNamed (_, args) -> args |> List.collect walkTypeExprForMixedEnumIdx
        | TyEquivIdx (_, g, r) ->
            walkTypeExprForMixedEnumIdx g @ walkTypeExprForMixedEnumIdx r
        | _ -> []
    here @ children

/// Find all mixed-value TyEnumIdx inside a single declaration. Returns the
/// list of offending valuesExpr nodes (one per occurrence). The caller
/// converts each into a TypeError with the decl's span.
let collectMixedEnumIdxInDecl (decl: Decl) : Expr list =
    let walkOpt = function Some t -> walkTypeExprForMixedEnumIdx t | None -> []
    match decl with
    | DeclLet binding | DeclStatic binding ->
        walkOpt binding.Type
    | DeclFunction f ->
        (f.Params |> List.collect (fun p -> walkOpt p.Type))
        @ walkOpt f.ReturnType
    | DeclType (TyDeclAlias (_, _, body)) ->
        // The TyDeclAlias body when itself a TyEnumIdx is caught by
        // registerTypeDecl. We still walk deeper into the body for nested
        // inline forms (e.g., `type X = Array<EnumIdx<[1, "x"]> like ...>`).
        match body with
        | TyEnumIdx _ -> []  // already handled at the alias site
        | _ -> walkTypeExprForMixedEnumIdx body
    | DeclType (TyDeclStruct (_, _, fields, _, _)) ->
        fields |> List.collect (fun f -> walkTypeExprForMixedEnumIdx f.Type)
    | DeclType (TyDeclSum (_, _, variants)) ->
        variants |> List.collect (fun v -> walkOpt v.Data)
    | DeclType (TyDeclMutualGroup (members, _)) ->
        members |> List.collect (fun (_, mty) -> walkTypeExprForMixedEnumIdx mty)
    | DeclImpl impl ->
        impl.Methods |> List.collect (fun m ->
            (m.Params |> List.collect (fun p -> walkOpt p.Type))
            @ walkOpt m.ReturnType)
    | DeclInterface _ | DeclImport _ | DeclUnit _ -> []

// Cross-module static value visibility (checkModule's import-seeding
// pre-pass). KNOWN GAP being closed: `let static k = 5` in module M wasn't
// visible to module Main's own static resolution -- `let static x = M.k +
// 1` failed the fold assertion even though M.k is compile-time-known.
// Root cause: StaticEval.resolveStatics is a pure function of a module's
// OWN decls with no seed parameter, so it can't learn what a DIFFERENT
// module folded its statics to -- and even a seed wouldn't help, since
// `M.k` parses as ExprField(ExprVar "M", "k") and StaticEval.evalExpr's
// ExprField arm unconditionally errors on qualified access.
//
// The fix: substitute resolved cross-module static references with their
// literal values in a COPY of the decls handed to resolveStatics, before
// it runs. `M.k` and a bare `k` (selective import) both become e.g.
// `ExprLit (LitInt 3L)`. Only that copy is rewritten; the decls used for
// ordinary type-checking are untouched, so `let v = M.k` still goes
// through the ordinary qualified-value-access path.

/// Render a folded StaticValue back into surface `Expr` literal form, for
/// splicing into another module's decls ahead of static resolution. The
/// TypedExpr analog of this conversion (checkDecl's DeclStatic
/// "RESOLVED-VALUE SHORTCUT") runs one stage later, on already-typed trees;
/// this one runs pre-typing, directly on the surface AST.
let rec internal staticValueToImportExpr sp (v: StaticEval.StaticValue) : Expr =
    match v with
    | StaticEval.SVInt i -> mkExpr sp (ExprLit (LitInt i))
    | StaticEval.SVFloat f -> mkExpr sp (ExprLit (LitFloat f))
    | StaticEval.SVBool b -> mkExpr sp (ExprLit (LitBool b))
    | StaticEval.SVString s -> mkExpr sp (ExprLit (LitString s))
    | StaticEval.SVUnit -> mkExpr sp (ExprLit LitUnit)
    | StaticEval.SVTuple vs -> mkExpr sp (ExprTuple (vs |> List.map (staticValueToImportExpr sp)))
    | StaticEval.SVStruct (n, fs) ->
        mkExpr sp (ExprStruct (n, fs |> List.map (fun (fn, v) -> (fn, staticValueToImportExpr sp v)), None))

/// Substitute references to cross-module static values (keyed in `seed` as
/// "alias.name" for a qualified import, "name" for a selective one) with
/// their literal form. Shadow-aware for local binding forms (let/match/
/// lambda) that could plausibly appear inside a `let static` RHS or a
/// static function body, so a same-named local doesn't get clobbered.
/// Structured after StaticEval.collectFreeNames's case coverage (the set of
/// expression forms StaticEval.evalExpr actually supports; substitution
/// beyond that set is moot since resolveStatics would reject the form
/// regardless).
let rec internal rewriteImportedStaticRefs (seed: Map<string, StaticEval.StaticValue>) (expr: Expr) : Expr =
    if Map.isEmpty seed then expr else
    let go = rewriteImportedStaticRefs seed
    let goWithout (boundNames: string list) (e: Expr) =
        let seed' = boundNames |> List.fold (fun (s: Map<string, StaticEval.StaticValue>) n -> Map.remove n s) seed
        rewriteImportedStaticRefs seed' e
    match expr.Kind with
    | ExprKind.ExprVar name ->
        match Map.tryFind name seed with
        | Some sv -> staticValueToImportExpr expr.Span sv
        | None -> expr
    | ExprKind.ExprField ({ Kind = ExprKind.ExprVar alias }, field) ->
        match Map.tryFind $"{alias}.{field}" seed with
        | Some sv -> staticValueToImportExpr expr.Span sv
        | None -> expr
    | ExprKind.ExprField (obj, field) -> inheritSpan expr (ExprField (go obj, field))
    | ExprKind.ExprBinOp (mode, op, l, r) -> inheritSpan expr (ExprBinOp (mode, op, go l, go r))
    | ExprKind.ExprUnaryOp (op, e) -> inheritSpan expr (ExprUnaryOp (op, go e))
    | ExprKind.ExprApp (f, args) -> inheritSpan expr (ExprApp (go f, args |> List.map go))
    | ExprKind.ExprIf (c, t, e) -> inheritSpan expr (ExprIf (go c, go t, go e))
    | ExprKind.ExprTuple es -> inheritSpan expr (ExprTuple (es |> List.map go))
    | ExprKind.ExprArrayLit es -> inheritSpan expr (ExprArrayLit (es |> List.map go))
    | ExprKind.ExprLet (binding, body) ->
        let bound = StaticEval.collectPatternBindings binding.Pattern |> Set.toList
        inheritSpan expr (ExprLet ({ binding with Value = go binding.Value }, goWithout bound body))
    | ExprKind.ExprMatch (scrut, cases) ->
        inheritSpan expr (ExprMatch (go scrut, cases |> List.map (fun c ->
            let bound = StaticEval.collectPatternBindings c.Pattern |> Set.toList
            { c with Guard = c.Guard |> Option.map (goWithout bound); Body = goWithout bound c.Body })))
    | ExprKind.ExprBlock (stmts, finalExpr) ->
        inheritSpan expr (ExprBlock (stmts |> List.map (rewriteImportedStaticRefsStmt seed), finalExpr |> Option.map go))
    | ExprKind.ExprStruct (name, fields, spread) -> inheritSpan expr (ExprStruct (name, fields |> List.map (fun (n, e) -> (n, go e)), spread |> Option.map go))
    | ExprKind.ExprTyped (e, t) -> inheritSpan expr (ExprTyped (go e, t))
    | ExprKind.ExprLambda (parms, whereClause, body) ->
        let bound = parms |> List.map (_.Name)
        inheritSpan expr (ExprLambda (parms, whereClause, goWithout bound body))
    | _ -> expr
and internal rewriteImportedStaticRefsStmt (seed: Map<string, StaticEval.StaticValue>) (stmt: Stmt) : Stmt =
    match stmt with
    | StmtSpanned (inner, span) -> StmtSpanned (rewriteImportedStaticRefsStmt seed inner, span)
    | StmtLet binding -> StmtLet { binding with Value = rewriteImportedStaticRefs seed binding.Value }
    | StmtAssign (lhs, op, rhs) -> StmtAssign (rewriteImportedStaticRefs seed lhs, op, rewriteImportedStaticRefs seed rhs)
    | StmtExpr e -> StmtExpr (rewriteImportedStaticRefs seed e)
    | StmtForIn (n, range, body) ->
        StmtForIn (n, rewriteImportedStaticRefs seed range, body |> List.map (rewriteImportedStaticRefsStmt seed))

/// Rewrite only the two decl shapes StaticEval.resolveStatics actually
/// consults (its Phase 1: `DeclStatic` and `DeclFunction ... IsStatic`) --
/// everything else (including plain `let`s and the DeclImport decls
/// themselves) passes through unchanged. A static function's own parameters
/// are excluded from the substitution seed (they're local, not the
/// cross-module reference).
let internal seedImportedStaticsIntoDecls (seed: Map<string, StaticEval.StaticValue>) (decls: Located<Decl> list) : Located<Decl> list =
    if Map.isEmpty seed then decls else
    decls |> List.map (fun locDecl ->
        match locDecl.Value with
        | DeclStatic binding ->
            { locDecl with Value = DeclStatic { binding with Value = rewriteImportedStaticRefs seed binding.Value } }
        | DeclFunction fd when fd.IsStatic ->
            let paramNames = fd.Params |> List.map (_.Name)
            let seed' = paramNames |> List.fold (fun (s: Map<string, StaticEval.StaticValue>) n -> Map.remove n s) seed
            { locDecl with Value = DeclFunction { fd with Body = rewriteImportedStaticRefs seed' fd.Body } }
        | _ -> locDecl)

/// Collect the StaticValues exported by this module's imports, keyed the
/// same way references to them actually parse: "alias.name" for a
/// qualified/aliased import (`M.k` parses as ExprField(ExprVar "M", "k")),
/// "name" for a selective import (`from M import k` brings in a bare
/// ExprVar "k"). Only modules already present in env.ModuleExports
/// (checked earlier in program order) contribute -- providers and
/// not-yet-checked modules are silently skipped, same as the existing
/// DeclImport handling in checkDecl.
let internal importedStaticSeed (env: TypeEnv) (decls: Located<Decl> list) : Map<string, StaticEval.StaticValue> =
    decls
    |> List.fold (fun acc locDecl ->
        match locDecl.Value with
        | DeclImport (qname, style) ->
            let fullName = String.concat "." qname
            match Map.tryFind fullName env.ModuleExports with
            | Some exports ->
                match style with
                | ImportQualified aliasOpt ->
                    let alias = aliasOpt |> Option.defaultValue (List.last qname)
                    exports.StaticValues
                    |> Map.fold (fun acc2 k v -> Map.add $"{alias}.{k}" v acc2) acc
                | ImportSelective names ->
                    names |> List.fold (fun acc2 n ->
                        match Map.tryFind n exports.StaticValues with
                        | Some v -> Map.add n v acc2
                        | None -> acc2) acc
            | None -> acc
        | _ -> acc) Map.empty

/// Post-unification sweep for direct-application RANK disagreements -- the
/// late half of the check whose eager half lives in `dispatchAppOrIndex`'s
/// FuncElem arm (both call `firstArgRankClash`, so the rule exists once).
/// The eager half only compares CLOSED types; an unannotated parameter is
/// not closed at its call site (Blade arithmetic is rank-polymorphic, so
/// `x * s` leaves `x` open and zonking defaults it to a SCALAR). That is how
///
///     function f(x, s: Float) -> Array<Float like IrrepsIdx<S>> = x * s
///     let r = f(xv, 2.0)          // xv : Array<Float64 like Idx<4>>
///
/// passed `blade check` and was refused by g++, which saw `double
/// f(double, double)` handed an `Array<double,1>` -- filling an array return
/// from a scalar body is a real, separately-tested feature, so the CALL
/// SITE is what is wrong. Running on the ZONKED module makes this total:
/// every parameter/argument carries the exact type codegen will emit.
let rec internal collectAppRankErrors (subst: Subst) (expr: TypedExpr) : CompileError list =
    let here =
        match expr.Kind with
        | TExprApp (tFunc, tArgs) ->
            match subst.Resolve tFunc.Type with
            | FuncElem (paramTys, _) ->
                match firstArgRankClash subst paramTys (tArgs |> List.map (_.Type)) with
                | Some (i, pr, ar, pTy, aTy) ->
                    let arg = List.item i tArgs
                    [ { Error = ArgRankMismatch (i + 1, pr, ar,
                                                 ppIRType (subst.Resolve pTy),
                                                 ppIRType (subst.Resolve aTy))
                        Span = arg.Span
                        Context = []
                        Code = None } ]
                | None ->
                // The ABSTRACT-PARAMETER twin, and the reason this sweep has
                // to carry it: a `T^k` parameter is STILL an open variable
                // after zonking -- polymorphic ids are deliberately preserved
                // for IR-phase monomorphization -- so the rank clash above
                // cannot see these, and neither could the eager seam when the
                // arguments were not yet determined there. What IS determined
                // by now is every ARGUMENT type, which is all this predicate
                // reads. See firstAbstractVarConflict.
                match firstAbstractVarConflict subst paramTys (tArgs |> List.map (_.Type)) with
                | Some (firstPos, conflictPos, firstTy, conflictTy) ->
                    let arg = List.item conflictPos tArgs
                    let calleeDesc =
                        match tFunc.Kind with
                        | TExprVar (name, _, _) -> $"'{name}'"
                        | _ -> "this function"
                    [ { Error = Other (abstractVarConflictMessage subst calleeDesc
                                                                  firstPos conflictPos firstTy conflictTy)
                        Span = arg.Span
                        Context = []
                        Code = None } ]
                | None -> []
            | _ -> []
        | _ -> []
    here @ (typedExprChildren expr |> List.collect (collectAppRankErrors subst))

/// The safety half of P5's open function-signature door, and it is NOT
/// speculative: both refusals below were MEASURED to pass silently before this
/// sweep existed.
///
/// Two mistakes, one site, because both are the same seam -- direct application
/// does NOT unify plain-call arguments, so nothing on the eager path ever
/// compares an argument's index record against its parameter's.
///
///  1. LAUNDERING. A tree-slotted argument reaching a parameter that declares no
///     tree slot -- an abstract `T^r`, or a plain `Array<T like I>` of matching
///     rank. The callee would read the pool as an ordinary rank-1 array, which
///     is true of the STORAGE and false of the TYPE: it could then return it,
///     alias it, or hand it to a loop former, and the tree identity is gone in
///     one step. Measured: `function h(x: T^1)` applied to a tree binding
///     compiled and ran.
///
///  2. SILENT MISADDRESSING. Both sides tree-slotted, different shapes. This is
///     the one the phase plan flagged as its top risk and it landed exactly
///     there: `f(A)` with a `[5,0,0,0,0,0]` argument and a `[2,2,0,0,3,0,0,0]`
///     parameter compiled, ran, and returned a value -- the callee folded the
///     path (1,2) against the PARAMETER's degree sequence and read offset 4 of
///     an array that has no such path. Equal cardinality is what let it through,
///     which is precisely the coincidence tree identity exists to reject.
///     `Unify`'s TreeTag arm already decides this correctly; it simply is never
///     asked on this path.
///
/// Runs on the ZONKED module for `collectAppRankErrors`' reason: an abstract
/// parameter is not closed at its call site, so the eager half in
/// `dispatchAppOrIndex`'s FuncElem arm cannot see it (`firstArgTypeClash`'s
/// `concreteClassOf` declines on arrays by design). A MATCHING concrete tree
/// parameter is fine and is the whole point of opening the door.
let rec internal collectAppTreeErrors (subst: Subst) (expr: TypedExpr) : CompileError list =
    // The tree slot of a type, as (rendered class, degree sequence). Both are
    // needed: the rendering names the class in the message, the sequence is the
    // identity.
    let treeOf (t: IRType) : (string * string option * int list) option =
        match subst.Resolve t with
        | ArrayElem at ->
            at.IndexTypes
            |> List.tryPick (fun ix ->
                if ix.IxKind <> IxKTree then None
                else
                    match ix, ix.Tag with
                    | TreeIdxLike rendered, Some (TreeTag (nameOpt, degrees)) -> Some (rendered, nameOpt, degrees)
                    | TreeIdxLike rendered, _ -> Some (rendered, None, [])
                    | _ -> None)
        | _ -> None
    // EXACTLY `Unify.indexPairIncompatible`'s TreeTag predicate, restated here
    // rather than approximated: identity is the degree SEQUENCE plus the
    // optional nominative alias, so two NAMED aliases of the same shape differ
    // while anon-vs-named stays compatible. Keeping the two rules in step is the
    // point of this sweep -- it exists only because direct application never
    // asks unify, not because it wants a different answer.
    let treeIdentityDiffers (n1, d1) (n2, d2) =
        d1 <> d2 || (match n1, n2 with
                     | Some a, Some b -> a <> b
                     | _ -> false)
    let here =
        match expr.Kind with
        | TExprApp (tFunc, tArgs) ->
            (match subst.Resolve tFunc.Type with
             | FuncElem (paramTys, _) ->
                 let n = min paramTys.Length tArgs.Length
                 List.zip (paramTys |> List.truncate n) (tArgs |> List.truncate n)
                 |> List.indexed
                 |> List.choose (fun (i, (pTy, arg)) ->
                     let mk detail =
                         Some { Error = TreeIdxUnsupported (detail |> fst, detail |> snd)
                                Span = arg.Span; Context = []; Code = None }
                     match treeOf arg.Type, treeOf pTy with
                     | Some (rendered, _, _), None ->
                         mk (rendered,
                             $"argument {i + 1}, whose parameter declares no tree slot -- an abstract or \
plain-array parameter would read the pool as an ordinary dense array and could then return, alias or \
iterate it with the tree identity gone. Declare the parameter over the same tree type, or pass \
leaves(T) and declare the parameter over LeafIdx")
                     | Some (aRend, aName, aDeg), Some (pRend, pName, pDeg)
                            when treeIdentityDiffers (aName, aDeg) (pName, pDeg) ->
                         mk (aRend,
                             $"argument {i + 1}, whose parameter declares the DIFFERENT tree type {pRend} -- \
a tree's identity is its degree sequence plus its nominative alias, and neither an equal leaf count nor \
an equal shape under another name makes two tree spaces interchangeable. The callee would fold its paths \
against its OWN type and read cells this argument does not have at those offsets")
                     | _ -> None)
             | _ -> [])
        | _ -> []
    here @ (typedExprChildren expr |> List.collect (collectAppTreeErrors subst))

/// Post-check sweep for MISPLACED provider writes.
///
/// `alias.write("path", A)` is a module-level DECLARATION form, not an
/// expression: `Lowering`'s decl loop intercepts the whole `let _ = c.write(..)`
/// binding into `IRModule.ProviderWrites` (a spec keyed by the binding's IRId)
/// and `genProviderWriteBinding` emits the flatten + the provider's writer for
/// it. Nothing intercepts a write written anywhere ELSE -- inside a block,
/// a function or lambda body, a loop body, an if/match branch -- so it used to
/// lower as an ordinary method call on the alias value and reach g++ as
/// `c.write(std::string("out.csv"), __v5)`, which fails with `'c' was not
/// declared in this scope`. Refuse it here instead, where the write's own span
/// is still in hand.
///
/// `nested` is false only for the top node of a module-level `let` RHS -- the
/// one position the decl loop actually intercepts. Every descent sets it, so
/// the rule needs no per-construct bookkeeping: anything that is not literally
/// that node is refused, `let static` included (the intercept arm matches
/// `TDeclLet` alone).
let rec internal collectMisplacedProviderWrites (subst: Subst) (nested: bool) (expr: TypedExpr) : CompileError list =
    let here =
        match expr.Kind with
        | TExprApp ({ Kind = TExprField (recv, "write", _) }, _) when nested ->
            (match recv.Kind, subst.Resolve recv.Type with
             | TExprVar (alias, _, _), IRTNamed pn when (Blade.ProviderRegistry.tryFind pn).IsSome ->
                 [ { Error = ProviderWriteModuleScope alias
                     Span = expr.Span
                     Context = []
                     Code = None } ]
             | _ -> [])
        | _ -> []
    here @ (typedExprChildren expr |> List.collect (collectMisplacedProviderWrites subst true))

/// Declaration entry points for the sweep above, tagged with whether the
/// expression sits in the one blessed position (a plain `let`'s RHS).
let internal declWriteRoots (decl: TypedDecl) : (bool * TypedExpr) list =
    let ofFunc (f: TypedFunctionDecl) = [(true, f.Body)]
    match decl with
    | TDeclLet b -> [(false, b.Value)]
    | TDeclStatic b -> [(true, b.Value)]
    | TDeclFunction f -> ofFunc f
    | TDeclImpl impl -> impl.Methods |> List.collect ofFunc
    | TDeclType _ | TDeclInterface _ | TDeclUnit _ | TDeclImport _ -> []

/// Post-check sweep for GROUP-KEYS ESCAPES (BL3017).
///
/// A `group_keys` result is NAME-KEYED, not a value. `genGroupKeysBinding`
/// (CodeGen.fs) puts the entire CSR structure into C++ locals suffixed off the
/// BINDING name -- `<name>__ngroups`, `<name>__offsets`, `<name>__perm` -- and
/// gives the binding itself a `void*` sentinel; `genGroupByBinding` recovers
/// the state by re-deriving those suffixed symbols from whatever cpp name the
/// grouping EXPRESSION resolves to. So the two ops are joined by a NAME, and
/// every indirection breaks the joint silently: `let gk2 = gk` emitted
/// `gk2__offsets`, a tuple round-trip emitted the same, and an untyped
/// function parameter zonked to a scalar and emitted `double g` alongside
/// `g__offsets`. All three died in g++, not in Blade.
///
/// The invariant is already ASSUMED elsewhere -- `sameGroupKeysBinding` in
/// inferMethodFor decides grouped co-iteration by comparing gk operands'
/// binding NAMES, which is only meaningful if a gk always IS its binding name.
/// This sweep is what makes it enforced.
///
/// `pos` is `None` in the two blessed positions (the direct RHS of a `let`
/// binding the call, and `group_by`'s grouping slot when it holds a plain
/// variable) and `Some phrase` everywhere else, where `phrase` completes
/// "... cannot be used <phrase>". Running on the ZONKED module makes this
/// total: IRTGroupKeys is minted only by `inferGroupKeys` and survives
/// zonking intact, so a resolved IRTGroupKeys anywhere else IS the escape.
/// A node that fires does not descend -- a gk-typed block or function body is
/// one mistake, not one per enclosing layer.
///
/// THE blessing for a `let` RHS, shared by the block walk and the declaration
/// roots so module-level and function-local `let`s cannot drift apart. Blessed
/// only when the RHS IS the call and the pattern is a bare name: `let gk2 = gk`
/// is an alias, and the alias is exactly the bug.
let internal groupKeysLetRhs (b: TypedBinding) : string option * TypedExpr =
    match b.Value.Kind with
    // `segments(A)` is a grouping on the same name-keyed terms as group_keys
    // (docs/plans/structural/07 §3.2): its locals are suffixed off the binding.
    | TExprGroupKeys _ | TExprSegments _ | TExprSegmentsGrid _ when List.isEmpty b.SubBindings -> (None, b.Value)
    | _ -> (Some "as another binding's value", b.Value)

let rec internal collectGroupKeysEscapes (subst: Subst) (pos: string option) (expr: TypedExpr) : CompileError list =
    let isGk (e: TypedExpr) = (subst.Resolve e.Type).IsIRTGroupKeys
    let describe (e: TypedExpr) =
        match e.Kind with
        | TExprGroupKeys _ -> "a `group_keys(...)` call"
        | TExprSegments (_, _, Some _) -> "a `files(...)` call"
        | TExprSegments _ | TExprSegmentsGrid _ -> "a `segments(...)` call"
        | TExprVar (n, _, _) -> $"the group_keys binding '{n}'"
        | _ -> "a group_keys result"
    // A block is TRANSPARENT here: its type is its final expression's, and its
    // span covers the whole body, so firing on the block would point the
    // caret at the innocent first statement. Descend and let the final
    // expression carry the enclosing position instead.
    let isBlock = expr.Kind.IsTExprBlock
    match pos with
    | Some phrase when isGk expr && not isBlock ->
        [ { Error = GroupKeysEscapes (describe expr, phrase); Span = expr.Span; Context = []; Code = None } ]
    | _ ->
    let elsewhere = "in this position"
    let kids : (string option * TypedExpr) list =
        match expr.Kind with
        | TExprGroupBy (values, gk) ->
            // The grouping slot takes the BINDING NAME only -- an inline
            // `group_by(v, group_keys(k))` has no locals to suffix off.
            let gkPos =
                match gk.Kind with
                | TExprVar _ -> None
                | _ -> Some "inline as `group_by`'s grouping argument"
            [ (Some elsewhere, values); (gkPos, gk) ]
        // The grouping ACCESSORS are blessed slots too, on the same terms as
        // group_by's: they read the CSR tables through the binding name, so a
        // bare name is fine and anything else has no locals to suffix off.
        // Without these arms the default below would fire BL3017 on the very
        // spellings these accessors exist to provide.
        | TExprGroupBucket gk ->
            let gkPos =
                match gk.Kind with
                | TExprVar _ -> None
                | _ -> Some "inline as `group_bucket`'s argument"
            [ (gkPos, gk) ]
        // `extents` is only a grouping accessor when its operand IS a grouping;
        // over an array it is the ordinary extent query, which must keep
        // descending normally.
        | TExprExtents a when isGk a ->
            let gkPos =
                match a.Kind with
                | TExprVar _ -> None
                | _ -> Some "inline as `extents`' argument"
            [ (gkPos, a) ]
        | TExprTuple es -> es |> List.map (fun e -> (Some "as a tuple element", e))
        | TExprArrayLit (elems, _) -> elems |> List.map (fun e -> (Some "as an array element", e))
        | TExprStruct (_, fields) -> fields |> List.map (fun (_, e) -> (Some "as a struct field", e))
        | TExprApp (f, args) ->
            (Some elsewhere, f) :: (args |> List.map (fun a -> (Some "as a function argument", a)))
        | TExprBlock (stmts, final) ->
            let rec ofStmt (s: TypedStmt) : (string option * TypedExpr) list =
                match s with
                | TStmtLet b -> [groupKeysLetRhs b]
                | TStmtAssign (l, r) -> [(Some elsewhere, l); (Some elsewhere, r)]
                | TStmtExpr e -> [(Some elsewhere, e)]
                | TStmtForIn (_, _, lo, hi, body) ->
                    (Some elsewhere, lo) :: (Some elsewhere, hi) :: (body |> List.collect ofStmt)
            // The final expression inherits the block's own position, so a
            // gk returned out of a function body reads "as a function's
            // return value" rather than the generic block phrasing.
            let finalPos = pos |> Option.defaultValue "as a block's result value"
            (stmts |> List.collect ofStmt)
            @ (final |> Option.toList |> List.map (fun e -> (Some finalPos, e)))
        | _ -> typedExprChildren expr |> List.map (fun e -> (Some elsewhere, e))
    kids |> List.collect (fun (p, e) -> collectGroupKeysEscapes subst p e)

/// Declaration entry points for the sweep above. Same blessing as the block
/// case: a module-level `let` may hold the call itself and nothing else, and a
/// function body is a returning position (BL7001's "no rule for IRGroupKeys in
/// expression position" was the old, misleadingly backend-flavoured verdict).
let internal declGroupKeysRoots (decl: TypedDecl) : (string option * TypedExpr) list =
    let ofFunc (f: TypedFunctionDecl) = [(Some "as a function's return value", f.Body)]
    match decl with
    | TDeclLet b | TDeclStatic b -> [groupKeysLetRhs b]
    | TDeclFunction f -> ofFunc f
    | TDeclImpl impl -> impl.Methods |> List.collect ofFunc
    | TDeclType _ | TDeclInterface _ | TDeclUnit _ | TDeclImport _ -> []

/// POST-ZONK SWEEP FOR ESCAPING SHARED-STATE CLOSURES (BL4005).
///
/// A closure VALUE copies what it captures (CodeGenExprSupport, "CLOSURE
/// VALUES CAPTURE BY VALUE"), which is what lets `function mk(i) = lambda(j)
/// -> i * 10 + j` outlive `mk`. The one capture it cannot copy is a binding
/// that is REASSIGNED (`n = n + k`, by the closure or by its scope): the
/// closure and the scope share that one variable -- the interpreter's shared
/// cell, the compiled lane's `[&]` -- and a copy would silently split it in
/// two. So such a closure lives exactly as long as the scope defining the
/// variable, and this sweep refuses every use that could let it outlive that
/// scope: returned (from a function or a block), placed in a tuple, array or
/// struct, passed to a function whose result could carry it back, assigned,
/// or captured in turn by a closure that escapes.
///
/// What stays legal is everything that runs the closure where it is defined:
/// calling it, using it as a kernel (a combinator's result is an array or a
/// scalar, which cannot carry a function), and naming it with a `let` that is
/// itself only used that way. Positions are judged by TYPE: a node whose type
/// cannot carry a function value (a scalar, an array of scalars, a tuple of
/// those) cannot carry the closure out, so everything beneath it is blessed;
/// any other node passes its position down.
///
/// "Reassigned" is the same predicate codegen keys `[&]` on
/// (computeReboundVarIds): an assignment whose target bottoms out in the
/// binding, except a store into an ARRAY element (a copied wrapper shares the
/// cells, so it is still copyable). Only bindings LOCAL to the root are judged:
/// a module-level binding lives as long as the program.
let internal collectMutCaptureEscapes (subst: Subst) (rootPos: string option) (root: TypedExpr) : CompileError list =
    // A type that may hold a function value somewhere inside it. Unknown and
    // nominal shapes (structs, loop objects, computations) answer yes.
    let rec carriesFn (t: IRType) =
        match IR.stripUnits (subst.Resolve t) with
        | IRTScalar _ | IRTUnit | IRTIdxTagged _ -> false
        | ArrayElem at -> carriesFn at.ElemType
        | IRTTuple ts -> ts |> List.exists carriesFn
        | _ -> true
    // Every expression and statement-carried expression, depth first.
    let rec allExprs (e: TypedExpr) : TypedExpr seq =
        seq { yield e
              for c in typedExprChildren e do yield! allExprs c }
    let all = allExprs root |> Seq.toList
    let blockStmts =
        all |> List.collect (fun e ->
            match e.Kind with
            | TExprBlock (stmts, _) ->
                let rec flat (s: TypedStmt) =
                    match s with
                    | TStmtForIn (_, _, _, _, body) -> s :: (body |> List.collect flat)
                    | _ -> [s]
                stmts |> List.collect flat
            | _ -> [])
    // Bindings introduced inside the root.
    let locals =
        let fromExprs =
            all |> List.collect (fun e ->
                match e.Kind with
                | TExprLet (_, id, _, _) -> [id]
                | TExprLambda li -> li.Params |> List.map (_.VarId)
                | TExprMatch (_, cases) ->
                    cases |> List.collect (fun c -> c.Pattern.Bindings |> List.map (fun (_, id, _) -> id))
                | _ -> [])
        let fromStmts =
            blockStmts |> List.collect (fun s ->
                match s with
                | TStmtLet b -> b.VarId :: (b.SubBindings |> List.map (fun (_, id, _) -> id))
                | TStmtForIn (_, id, _, _, _) -> [id]
                | _ -> [])
        Set.ofList (fromExprs @ fromStmts)
    // Bindings some assignment inside the root REBINDS.
    let rec storeRoot (e: TypedExpr) : IRId option =
        match e.Kind with
        | TExprVar (_, id, _) -> Some id
        | TExprField (b, _, _) | TExprTupleIndex (b, _) -> storeRoot b
        | TExprIndex (b, _, _) | TExprApp (b, _) ->
            (match subst.Resolve b.Type with
             | ArrayElem _ -> None
             | _ -> storeRoot b)
        | _ -> None
    let assignTargets =
        (all |> List.choose (fun e -> match e.Kind with TExprAssign (l, _) -> Some l | _ -> None))
        @ (blockStmts |> List.choose (fun s -> match s with TStmtAssign (l, _) -> Some l | _ -> None))
    let shared =
        assignTargets |> List.choose storeRoot |> Set.ofList |> Set.intersect locals
    if Set.isEmpty shared then [] else
    // Closures that share one of those bindings: a lambda capturing one, or
    // capturing a let-bound closure that does (fixpoint over the let chain).
    // Maps the closure's binding id to the shared variable's name.
    let lets =
        (all |> List.choose (fun e -> match e.Kind with TExprLet (_, id, v, _) -> Some (id, v) | _ -> None))
        @ (blockStmts |> List.choose (fun s ->
               match s with
               | TStmtLet b when List.isEmpty b.SubBindings -> Some (b.VarId, b.Value)
               | _ -> None))
    let sharedNameOf (li: TypedLambdaInfo) (closures: Map<IRId, string>) : string option =
        li.Captures |> List.tryPick (fun c ->
            if Set.contains c.VarId shared then Some c.Name
            else Map.tryFind c.VarId closures)
    let rec viaValue (closures: Map<IRId, string>) (v: TypedExpr) : string option =
        match v.Kind with
        | TExprLambda li -> sharedNameOf li closures
        | TExprVar (_, id, _) -> Map.tryFind id closures
        // `f >> g` is a closure over its operands: it shares whatever they
        // share, and it reads a reassigned function-typed operand by
        // reference too (CodeGenExpr's IRCompose arm).
        | TExprCompose (_, l, r) ->
            [l; r] |> List.tryPick (fun op ->
                match op.Kind with
                | TExprVar (n, id, _) when Set.contains id shared -> Some n
                | _ -> viaValue closures op)
        // A loop object carries its kernel.
        | TExprObjectFor info -> viaValue closures info.Kernel
        | _ -> None
    let rec fix (closures: Map<IRId, string>) =
        let next =
            lets |> List.fold (fun m (id, v) ->
                if Map.containsKey id m then m
                else match viaValue m v with Some n -> Map.add id n m | None -> m) closures
        if next.Count = closures.Count then closures else fix next
    let closures = fix Map.empty
    let describe (e: TypedExpr) =
        match e.Kind with
        | TExprVar (n, _, _) -> $"the closure '{n}'"
        | _ -> "this closure"
    let rec walk (pos: string option) (e: TypedExpr) : CompileError list =
        match pos, viaValue closures e with
        | Some phrase, Some captured ->
            [ { Error = MutCaptureEscapes (describe e, captured, phrase); Span = e.Span; Context = []; Code = None } ]
        | _ ->
        // Below a node that cannot carry a function, nothing can escape.
        let down (phrase: string) (t: IRType) = if carriesFn t then Some phrase else None
        // A `let` may NAME a closure (the closure map tracks the name from
        // then on); any other function-carrying value is judged as escaping
        // into the binding, since the map cannot follow it through a block,
        // a branch or a call.
        let letPos (v: TypedExpr) =
            match v.Kind with
            | TExprLambda _ | TExprVar _ | TExprCompose _ | TExprObjectFor _ -> None
            | _ -> down "as another binding's value" v.Type
        let kids : (string option * TypedExpr) list =
            match e.Kind with
            | TExprApp (f, args) ->
                (None, f) :: (args |> List.map (fun a -> (down "as a function argument" e.Type, a)))
            | TExprLambda li ->
                [ (down "as a closure's result value" li.Body.Type, li.Body) ]
            // Closure-carrying values the map tracks as a whole (viaValue):
            // their operands are judged through the value itself.
            | TExprCompose (_, l, r) -> [ (None, l); (None, r) ]
            | TExprObjectFor info -> [ (None, info.Kernel) ]
            | TExprLet (_, _, v, b) -> [ (letPos v, v); (pos, b) ]
            | TExprIf (c, t, f) -> [ (None, c); (pos, t); (pos, f) ]
            | TExprMatch (s, cases) ->
                (None, s) :: (cases |> List.collect (fun c ->
                    (pos, c.Body) :: (c.Guard |> Option.toList |> List.map (fun g -> (None, g)))))
            | TExprAssign (l, r) -> [ (None, l); (Some "as an assigned value", r) ]
            | TExprTuple es -> es |> List.map (fun x -> (Some "as a tuple element", x))
            | TExprArrayLit (es, _) -> es |> List.map (fun x -> (Some "as an array element", x))
            | TExprStruct (_, fields) -> fields |> List.map (fun (_, x) -> (Some "as a struct field", x))
            | TExprBlock (stmts, final) ->
                let rec ofStmt (s: TypedStmt) : (string option * TypedExpr) list =
                    match s with
                    | TStmtLet b when List.isEmpty b.SubBindings -> [ (letPos b.Value, b.Value) ]
                    | TStmtLet b -> [ (down "as another binding's value" b.Value.Type, b.Value) ]
                    | TStmtAssign (l, r) -> [ (None, l); (Some "as an assigned value", r) ]
                    | TStmtExpr x -> [ (None, x) ]
                    | TStmtForIn (_, _, lo, hi, body) ->
                        (None, lo) :: (None, hi) :: (body |> List.collect ofStmt)
                (stmts |> List.collect ofStmt) @ (final |> Option.toList |> List.map (fun x -> (pos, x)))
            | _ -> typedExprChildren e |> List.map (fun x -> (down "in this position" e.Type, x))
        kids |> List.collect (fun (p, x) -> walk p x)
    walk rootPos root

/// Declaration entry points for the sweep above: a function body (or method)
/// is a returning position. A module-level `let`'s value is NOT: its block is
/// flattened into main()'s own scope (genLetChainBinding), which lives as long
/// as the program, so `let c = { let mut n = 0; lambda(k) -> ... }` shares a
/// variable that never dies. Positions INSIDE it are still judged (a closure
/// in a tuple, an argument, a nested function body).
let internal declMutCaptureRoots (decl: TypedDecl) : (string option * TypedExpr) list =
    let ofFunc (f: TypedFunctionDecl) = [(Some "as a function's return value", f.Body)]
    match decl with
    | TDeclLet b | TDeclStatic b -> [(None, b.Value)]
    | TDeclFunction f -> ofFunc f
    | TDeclImpl impl -> impl.Methods |> List.collect ofFunc
    | TDeclType _ | TDeclInterface _ | TDeclUnit _ | TDeclImport _ -> []

/// POST-ZONK SUBSCRIPT SWEEP -- the late half of the subscript judgment
/// (formalism 3.10), whose eager half is `checkArrayIndexTags`. The eager
/// half sees a kernel parameter or an unannotated function parameter while it
/// is still an open variable; by now every subscript carries the type codegen
/// will emit, so the CLASS rule (no Float / Bool / Complex / String position)
/// and the NOMINAL rule (a tagged slot refuses a differently tagged index)
/// are total here. Its call-site twin: a direct call whose argument was open
/// when the call judgment ran (the eta wrapper `lambda(__k) -> g(__k)` that
/// a named-function kernel over `range<Lon>` becomes) is judged again for
/// the two index rules the judgment would have applied -- a different tag,
/// or a non-integer value, meeting an index parameter.
let rec internal collectSubscriptErrors (env: TypeEnv) (expr: TypedExpr) : CompileError list =
    let subst = env.Subst
    let mkErr (span: Span) (e: TypeError) : CompileError =
        { Error = e; Span = span; Context = []; Code = None }
    let judgeSubscripts (arr: TypedExpr) (arrTy: IRArrayType) (args: TypedExpr list) =
        let synthetic = isSynthesizedBuffer arr
        subscriptSlotPairs arrTy args
        |> List.tryPick (fun (a, ix) ->
            match subscriptClassOrRangeError env synthetic ix a with
            | Some e -> Some (mkErr a.Span e)
            | None ->
                match ix.Tag, IR.stripUnits (subst.Resolve a.Type) with
                | Some tag, IRTIdxTagged (_, IRefNamed argTag)
                    when not (tag.StartsWith "__") && argTag <> tag && not (slotIsEnumIdx env ix) ->
                    Some (mkErr a.Span (IndexTagMismatchNamed (tag, argTag)))
                | _ -> None)
        |> Option.toList
    let here =
        match expr.Kind with
        // The index cast's late half: an operand that was OPEN when
        // `(e : I)` was typed got the checked conversion without being
        // judged (TypeCheckInfer.inferIndexCast); judged now on its final
        // type, by the eager judgment's rules -- an index of a different
        // named type is refused (a position is an integer), and so is a
        // non-integer.
        | TExprBlock ([ TStmtLet tb; TStmtExpr { Kind = TExprConstraintCheck (_, "BL8006", _) } ], Some _)
            when tb.Name.StartsWith indexCastBindingPrefix ->
            (match IR.stripUnits (subst.Resolve expr.Type), IR.stripUnits (subst.Resolve tb.Value.Type) with
             | IRTIdxTagged (_, IRefNamed tag), IRTIdxTagged (IRTScalar (ETInt32 | ETInt64), IRefNamed src)
                 when src <> tag && not (src.StartsWith "__") && not (isIndexPositionExpr tb.Value) ->
                 [ mkErr tb.Value.Span (IndexCastForeignTag (tag, src)) ]
             | target, (IRTScalar (ETFloat32 | ETFloat64 | ETBool | ETComplex64 | ETComplex128 | ETString) as actual) ->
                 [ mkErr tb.Value.Span (TypeMismatch (target, actual)) ]
             | _ -> [])
        | TExprIndex (arr, args, _) ->
            (match subst.Resolve arr.Type with
             | ArrayElem at -> judgeSubscripts arr at args
             | _ -> [])
        | TExprApp (f, args) ->
            (match subst.Resolve f.Type with
             | ArrayElem at -> judgeSubscripts f at args
             | FuncElem (ps, _) ->
                 let fname = match f.Kind with TExprVar (nm, _, _) -> nm | _ -> "this function"
                 let n = min ps.Length args.Length
                 List.zip (List.truncate n ps) (List.truncate n args)
                 |> List.indexed
                 |> List.tryPick (fun (i, (p, a)) ->
                     let pr = IR.stripUnits (subst.Resolve p)
                     let ar = IR.stripUnits (subst.Resolve a.Type)
                     let clash =
                         match pr, ar with
                         | IRTIdxTagged (_, IRefNamed t1), IRTIdxTagged (_, IRefNamed t2) -> t1 <> t2
                         | IRTIdxTagged (IRTScalar (ETInt32 | ETInt64), _),
                           IRTScalar (ETFloat32 | ETFloat64 | ETBool | ETComplex64 | ETComplex128) -> true
                         | _ -> false
                     if clash then
                         Some (mkErr a.Span (ArgTypeMismatch (i + 1, fname, ppIRType pr, ppIRType ar)))
                     else None)
                 |> Option.orElse (
                     // The callee's GENERIC OBLIGATIONS (TypeEnv.GenericObligation),
                     // for a call whose arguments were still open when the call
                     // judgment ran -- the eta wrapper `lambda(__k) -> mean(__k)`
                     // a named-function kernel becomes. The instance is read off
                     // the (now concrete) arguments against the declared
                     // parameters, the way IR monomorphization will read it.
                     match calleeDeclId env f with
                     | Some fid ->
                         (match env.FuncGenericObligations.TryGetValue fid with
                          | true, obs ->
                              let rec learn (p: IRType) (a: IRType) (acc: Map<int, IRType>) =
                                  match IR.stripUnits (subst.Resolve p), IR.stripUnits (subst.Resolve a) with
                                  | IRTInfer r, at -> if acc.ContainsKey r then acc else Map.add r at acc
                                  | ArrayElem pa, ArrayElem aa -> learn pa.ElemType aa.ElemType acc
                                  | IRTTuple pts, IRTTuple ats when pts.Length = ats.Length ->
                                      List.fold2 (fun m pt at -> learn pt at m) acc pts ats
                                  | IRTIdxTagged (pi, _), IRTIdxTagged (ai, _) -> learn pi ai acc
                                  | _ -> acc
                              let n = min ps.Length args.Length
                              let inst =
                                  List.fold2 (fun m p (a: TypedExpr) -> learn p a.Type m) Map.empty
                                      (List.truncate n ps) (List.truncate n args)
                              judgeGenericObligations env fname obs (fun r ->
                                  Map.tryFind r inst |> Option.bind (concreteElemOf subst))
                              |> Option.map (fun e -> mkErr expr.Span e)
                          | _ -> None)
                     | None -> None)
                 |> Option.toList
             | _ -> [])
        | _ -> []
    here @ (typedExprChildren expr |> List.collect (collectSubscriptErrors env))

/// Every expression a zonked declaration carries, for the sweep above.
let internal declExprs (decl: TypedDecl) : TypedExpr list =
    let ofFunc (f: TypedFunctionDecl) = [f.Body]
    match decl with
    | TDeclLet b | TDeclStatic b -> [b.Value]
    | TDeclFunction f -> ofFunc f
    | TDeclImpl impl -> impl.Methods |> List.collect ofFunc
    | TDeclType _ | TDeclInterface _ | TDeclUnit _ | TDeclImport _ -> []

