// The type checker's RECURSIVE CORE: the rec-chain from inferExpr through checkExpr -- the block / let / match /
// lambda rules and every infer* rule that recurses back into inferExpr. The chain is OPEN (cleanup round 2, K5):
// a group of its functions may live in another file. A group the chain calls that never calls back sits in a
// file BEFORE this one and is called directly (TypeCheckInferUnits.fs, TypeCheckInferApply.fs); a group that
// calls back takes the chain's entry points as its first argument, the InferEntries record
// (TypeCheckInferEntries.fs) this chain builds once as its value `inferEntries` (TypeCheckInferRecArray.fs,
// TypeCheckInferArrayOps.fs, TypeCheckInferIntrinsics.fs, TypeCheckInferArith.fs, TypeCheckInferReduce.fs); a
// group that only calls the chain sits in a file AFTER it (TypeCheckInferDecl.fs, the declaration checkers).
module Blade.TypeCheckInfer

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
open Blade.TypedBuilder
open Blade.TypeCheckInferUnits
open Blade.TypeCheckInferApply
open Blade.TypeCheckInferEntries
open Blade.TypeCheckInferRecArray
open Blade.TypeCheckInferArrayOps
open Blade.TypeCheckInferIntrinsics
open Blade.TypeCheckInferArith
open Blade.TypeCheckInferReduce

/// The BODIES (by reference) of lambdas that a nested `function` declaration
/// desugared to (parseNestedFunction), registered by inferBlock just before
/// the binding is inferred. inferLambdaFull reads it to give their
/// UNANNOTATED parameters the meaning a top-level declaration's have: an
/// anonymous generic `x: T` (formalism 4.3), not an inference variable the
/// first call pins. A plain block `let f = lambda` is not registered (D5).
/// Per thread: the test harness checks programs concurrently in one process.
let private nestedFunctionBodiesTL =
    new System.Threading.ThreadLocal<System.Collections.Generic.HashSet<Expr>>(fun () ->
        System.Collections.Generic.HashSet<Expr>(HashIdentity.Reference))
let internal nestedFunctionBodies () = nestedFunctionBodiesTL.Value

/// The reading the block that DRIVES a monomorphic-first check has put its
/// nest under (inferBlock, deep review D5): 0 = no block is driving, 1 = as
/// written, 2 = eligible block lambdas generalized. Per thread, as above.
let private blockLambdaModeTL = new System.Threading.ThreadLocal<int ref>(fun () -> ref 0)
let internal blockLambdaMode () = blockLambdaModeTL.Value

/// A `let rec`'s STEP ORDINAL (`n` in `| prefix :: n -> ..`) runs over the
/// recursion's leading axis, so it is that axis's index value `Nat<I>` when
/// the axis is a NAMED index type -- exactly the iteration index a
/// `method_for(range<I>)` kernel receives (formalism 7.5). The desugar (and
/// the AD pipeline's own, GradExpand) ascribes the loop's lower bound with
/// the axis -- `(0 : Step)..5` -- and inferForIn binds the loop variable at
/// that index type and records its id in `recStepVars`, whose arithmetic
/// (`n - 1`, `(n + 1) % 5`) is a POSITION that keeps the tag, as a kernel
/// parameter's does (inferBinOp). Untagged, `f(n)` into the axis warned
/// BL4003 on correct code.
let private recStepVarsTL =
    new System.Threading.ThreadLocal<System.Collections.Generic.Dictionary<IRId, string>>(fun () ->
        System.Collections.Generic.Dictionary<IRId, string>())
let internal recStepVars () = recStepVarsTL.Value

/// The per-program side tables above are keyed by ids a program's own builder
/// mints; a thread checks many programs (the test harness), so each program
/// starts them empty (TypeCheck.checkProgram).
let internal resetInferSideTables () =
    (nestedFunctionBodies ()).Clear()
    (recStepVars ()).Clear()

/// A CURRIED TREE-READ SPINE: `T(p1)(p2)...(pk)` decomposed into the base
/// variable and its argument groups, defined only when every group has exactly
/// one argument and the base names an array whose SOLE index slot is a tree.
///
/// This is what lets the view-composition rewrite be depth-general instead of
/// two-deep. It matters: a spine the rewrite does not claim folds its inner
/// prefix to a SCALAR and then applies the next group to it, which typechecks
/// (over-applying a scalar is an old, general hole -- `A(0)(0)` on a plain
/// rank-1 array passes `check` too) and dies in g++ with "expression cannot be
/// used as a function". Claiming the whole spine means an over-long path meets
/// TreeRank's own "outside [0,0)" refusal instead.
///
/// Purely syntactic and pre-typing, which is the point: this phase ships no
/// subtree VALUE, so the only way applications can compose is textually.
let rec internal treeViewSpine (env: TypeEnv) (e: Expr) : (Expr * Expr list) option =
    match e.Kind with
    | ExprKind.ExprVar n ->
        (match lookupVar n env with
         | Some vi ->
             (match env.Subst.Resolve vi.Type with
              | ArrayElem at ->
                  (match at.IndexTypes with
                   | [ ix ] when ix.IxKind = IxKTree -> Some (e, [])
                   | _ -> None)
              | _ -> None)
         | None -> None)
    | ExprKind.ExprApp (h, [a]) ->
        treeViewSpine env h |> Option.map (fun (b, prior) -> (b, prior @ [a]))
    | _ -> None

/// ARITY LIFT at a direct call: `add0(A, s)` where `add0(a: T^0, b: T^0)`.
///
/// The signature declares ONE rank-0 type variable across several positions,
/// so a call that MIXES an array with a scalar asks `T` to be two things at
/// once. The answer arity polymorphism promises -- and the one every
/// neighbouring construct already gives -- is to LIFT: iterate the array
/// positions, broadcast the scalar ones, and deduce the output rank from the
/// arrays. Binary intrinsics do exactly this (`atan2(A, 2.0)`,
/// `atan2(2.0, A)`, `log_base(A, 10.0)`), and so do the operators
/// (`A * 2.0`, `100.0 + A`); only the user-defined `T^0` function was left
/// out, and it did not merely decline -- it emitted a specialization typed
/// from the FIRST argument and handed it the others verbatim, which g++
/// rejected with no BL code (see `firstAbstractVarConflict`).
///
/// MECHANISM. The same SURFACE re-synthesis `inferBinaryIntrinsic` uses --
/// `(method_for(...) <@> lambda(..) -> f(..)) |> compute` -- re-inferred from
/// scratch. So zip co-iteration, array/scalar broadcast in every position,
/// packed storage, capture analysis and codegen are the ones already proven
/// for `A + B`; nothing new reaches the back end, and the interpreter needs
/// no arm of its own because the lifted form is one it already runs. EAGER
/// (`|> compute`) for the reason `cos(A)` is: a call is a value everywhere
/// else, so it must not hand back a deferred loop.
///
/// The lambda parameters stay UNANNOTATED -- unlike the intrinsics', which
/// pin Float64 -- because the element type is precisely what `T` becomes:
/// apply-site unification has to bind it. Scalar positions embed their
/// SURFACE expression rather than the typed node, so capture analysis sees
/// the variable references (the array/scalar binop arm's note).
///
/// WHAT IT DECLINES, each for a reason that is a refusal rather than a gap:
///   * a `T^k` parameter with k >= 1 -- that parameter is an ARRAY by
///     declaration, so a scalar there is the wrong shape, not a broadcast,
///     and mapping would be inventing an axis;
///   * array arguments of DIFFERENT ranks -- they share no iteration space,
///     so there is nothing to deduce an output rank from;
///   * a signature whose positions each own their own variable -- there is no
///     conflict to resolve, and today's meaning stands.
/// Every declined shape falls through to `dispatchAppOrIndex`, where
/// `firstAbstractVarConflict` judges it.
///
/// RE-ENTRY is impossible: the synthesized inner call has an unresolved
/// lambda parameter in every array position, and the trigger below requires
/// every argument rank to be KNOWN.
let internal tryArityLiftCall (env: TypeEnv) (func: Expr) (args: Expr list)
                              (tArgs: TypedExpr list) (funcTy: IRType) : Expr option =
    // The lift's element-agreement gate must see the same contextual literal
    // widths as the ordinary call judgment. A Float64 array fixes a scalar
    // float literal to Float64 before deciding whether it can broadcast.
    let tArgs = adaptLiteralArgs env (env.Subst.Resolve funcTy) tArgs
    match env.Subst.Resolve funcTy with
    | FuncElem (paramTys, _) when paramTys.Length = args.Length
                                  && args.Length = tArgs.Length
                                  && args.Length > 1
                                  && not (paramTys |> List.exists (fun t -> (env.Subst.Resolve t).IsIRTPoly)) ->
        // Every parameter must be an OPEN, rank-0 signature variable: `T^0`,
        // or its caret-free spellings `T` / `T<u>` which lower to the same
        // node. `GetArityConstraint` is what separates those from `T^k`:
        // `LookupOrCreateTypeVar` records the pin only when the caret is >= 1.
        let openRank0Var (t: IRType) =
            match env.Subst.Resolve t with
            | IRTInfer id
            | IRTUnitAnnotated (IRTInfer id, _) ->
                (match env.Subst.GetArityConstraint id with
                 | Some k when k > 0 -> None
                 | _ -> Some id)
            | _ -> None
        let varIds = paramTys |> List.map openRank0Var
        let argRanks = tArgs |> List.map (fun a -> concreteRankOf env.Subst a.Type)
        if (varIds |> List.exists Option.isNone) || (argRanks |> List.exists Option.isNone) then None
        else
            let ids = varIds |> List.map Option.get
            let ranks = argRanks |> List.map Option.get
            let arrayRanks = ranks |> List.filter (fun r -> r > 0) |> List.distinct
            // MIXED, and unambiguously so: at least one array position, at
            // least one scalar position, and all array positions at ONE rank.
            if arrayRanks.Length <> 1 || not (List.contains 0 ranks) then None
            // ... and the disagreement must actually land on a SHARED
            // variable. Distinct variables per position (`f(a: T^0, b: U^0)`)
            // have nothing to reconcile, and lifting them would change the
            // meaning of a call that is already well typed.
            elif not (List.zip ids ranks
                      |> List.groupBy fst
                      |> List.exists (fun (_, g) -> (g |> List.map snd |> List.distinct).Length > 1)) then None
            // ... and the positions sharing a variable must agree on their
            // ELEMENT exactly (formalism 2.4: no argument converts implicitly):
            // `add0([0.5, 0.5], 1)` is refused at the `1` by the call
            // judgment's firstAbstractVarConflict, not lifted into a kernel
            // whose `add0(__x, 1)` would retype the array's elements.
            elif (let elems = tArgs |> List.map (fun a -> concreteElemOf env.Subst a.Type)
                  List.zip ids elems
                  |> List.groupBy fst
                  |> List.exists (fun (_, g) ->
                      (g |> List.choose snd |> List.distinct).Length > 1)) then None
            else
                let sp = args |> List.fold (fun acc (a: Expr) -> mergeSpan acc a.Span) func.Span
                let uid = env.Builder.FreshId()
                let arrPositions =
                    ranks |> List.mapi (fun i r -> (i, r))
                          |> List.filter (fun (_, r) -> r > 0)
                          |> List.map fst
                let pname i = $"__al{uid}_{i}"
                let lamParams =
                    arrPositions
                    |> List.map (fun i ->
                        ({ Name = pname i; Type = None; Default = None; NameSpan = noSpan } : LambdaParam))
                let newArgs =
                    args |> List.mapi (fun i a ->
                        if List.contains i arrPositions then mkExpr a.Span (ExprVar (pname i)) else
                        // Re-entry infers surface syntax again: preserve the
                        // width the array just taught this literal, rather
                        // than defaulting it anew inside the lifted lambda.
                        match literalOnlyElem env.Subst tArgs.[i] with
                        | Some et -> ascribeLiteralOnly et a
                        | None -> a)
                let former =
                    match arrPositions with
                    | [i] -> mkExpr sp (ExprMethodFor [args.[i]])
                    | many -> mkExpr sp (ExprMethodFor [mkExpr sp (ExprZip (many |> List.map (fun i -> args.[i])))])
                let body = mkExpr sp (ExprApp (func, newArgs))
                Some (mkExpr sp (ExprCompute (mkExpr sp (ExprBinOp (Elementwise, OpApply,
                          former, mkExpr sp (ExprLambda (lamParams, None, body)))))))
    | _ -> None

/// One INTRINSIC SPECIAL FORM (inferExprInner's intrinsicForms table): given the
/// environment, the whole call, its head name and its arguments, the typed call --
/// or ValueNone when the call's shape or the form's guard does not hold (the form
/// declines and dispatch tries the next one).
type internal IntrinsicForm = TypeEnv -> Expr -> string -> Expr list -> TypeResult<TypedExpr> voption

/// Entry for every expression: stamps the ambient expression span, FREEZES it
/// against an error leaving here (TypeEnv.noteErrorSpan: what is checked after
/// the raise can no longer move the report, see TypeEnv.locateError) and
/// back-fills the source span onto the typed node so TypedExpr.Span is live
/// (full-span AST).
let rec inferExpr (env: TypeEnv) (expr: Expr) : TypeResult<TypedExpr> =
    if expr.Span.StartLine > 0 then setCurrentExprSpan expr.Span
    let r = inferExprInner env expr
    (match r with Error e -> noteErrorSpan e | Ok _ -> ())
    match r with
    | Ok te when te.Span.StartLine = 0 && expr.Span.StartLine > 0 ->
        Ok { te with Span = expr.Span }
    | r -> r

/// The chain's entry points for the groups moved out of it (TypeCheckInferEntries.InferEntries): built ONCE, here,
/// as the chain's value, and handed to those groups as their first argument.
and internal inferEntries : InferEntries =
    { CheckExpr = fun env expected expr -> checkExpr env expected expr
      InferExpr = fun env expr -> inferExpr env expr
      InferReduceGeneral = fun env array kernel init axisCountOpt -> inferReduceGeneral env array kernel init axisCountOpt }

and inferExprInner (env: TypeEnv) (expr: Expr) : TypeResult<TypedExpr> =
    match expr.Kind with
    // ---- Literals ----
    // Literals synthesize CONCRETE (their natural scalar). Flexibility (flexing
    // a literal into a generic `T`/`T^k` or a wider width) is introduced
    // BIDIRECTIONALLY in `checkExpr`, only where an expected type demands it --
    // making a literal a bare inference var at synthesis would spray unresolved
    // vars through array/arith/index positions that assume concrete scalars.
    | ExprKind.ExprLit lit ->
        Ok (mkTyped (TExprLit lit) (inferLiteralTypeAt lit expr.Span))
    // `-2147483648` is an Int32 (lead ruling R2): the fit test reads the
    // NEGATED value, though the lexer hands the literal over unsigned.
    | ExprKind.ExprUnaryOp (OpNeg, ({ Kind = ExprKind.ExprLit (LitInt 2147483648L) } as inner)) ->
        let tLit = mkTypedSpan (TExprLit (LitInt 2147483648L)) (IRTScalar ETInt32) inner.Span
        Ok (mkTyped (TExprUnaryOp (OpNeg, tLit)) (IRTScalar ETInt32))

    // ---- Wildcard hole ----
    // `_` in expression position. Not a value; carries a fresh hole type so it
    // passes through tuple inference (e.g. a free axis in a compound index
    // B((a, _, c))). The compound-index dispatch reads the wildcard positions;
    // any other context that reaches lowering with a TExprWildcard is an error.
    | ExprKind.ExprWildcard -> Ok (mkTyped TExprWildcard (env.Subst.Fresh()))

    // ---- Static former marker ----
    // Produced by the parser, eliminated by the Unfold pass before
    // typechecking; reaching here means the pipeline skipped unfolding.
    | ExprKind.ExprStatic _ ->
        Error (InternalErr "internal: static former survived unfolding (the Unfold pass did not run)")

    // ---- Recursive array definitions ----
    // Only legal as the immediate Value of a `let rec` binding; the binding
    // path routes to inferRecArray (which needs the declared type and the
    // bound name for self-reference). Reaching it here means it appeared in
    // ordinary expression position.
    | ExprKind.ExprRecArray def ->
        Error (RecArrayFormErr $"recursive array '{def.Name}': a recursive array definition is only legal as the body of `let rec {def.Name}: ... = match {def.Name} with ...`")

    // ---- Variables ----
    | ExprKind.ExprVar name ->
        inferVarExpr env name

    | ExprKind.ExprQualified names ->
        // Qualified name resolution -- limited for now
        Ok (mkTyped (TExprQualified names) IRTUnit)

    // ---- Binary operations (dispatch to helper) ----
    | ExprKind.ExprBinOp (mode, op, left, right) ->
        inferBinOp env mode op left right

    // ---- Unary operations ----
    | ExprKind.ExprUnaryOp (op, operand) ->
        inferUnaryOp inferEntries env op operand
    | ExprKind.ExprApp ({ Kind = ExprKind.ExprField ({ Kind = ExprKind.ExprVar alias }, "load_compound") }, [varE; maskE])
        when (providerAliasName env alias).IsSome ->
        inferLoadCompound inferEntries env varE maskE alias

    // ---- Provider read: alias.read(view) / view |> alias.read ----
    // Both spellings arrive as this application (the pipe desugars to an
    // application). The result is the operand's type unchanged; the typed
    // node is TExprRead, which lowering's tryPlainRead/tryCompoundRead
    // intercept to record the deferred ProviderReadSpec.
    | ExprKind.ExprApp ({ Kind = ExprKind.ExprField ({ Kind = ExprKind.ExprVar alias }, "read") }, [operand])
        when (providerAliasName env alias).IsSome ->
        inferExpr env operand |> Result.bind (fun tE ->
            Ok (mkTyped (TExprRead tE) tE.Type))

    // ---- Streamed read: alias.stream(view) / view |> alias.stream ----
    // Types exactly like a read (the array type is unchanged) but keeps the
    // generic application shape so lowering records a STREAMED spec: no
    // materialization at the binding; consuming loop nests inline per-fiber
    // store reads at the S/T boundary instead.
    | ExprKind.ExprApp ({ Kind = ExprKind.ExprField ({ Kind = ExprKind.ExprVar alias }, "stream") }, [operand])
        when (providerAliasName env alias).IsSome ->
        inferExpr env operand |> Result.bind (fun tE ->
            match env.Subst.Resolve tE.Type with
            | ArrayElem _ ->
                let aliasVi = (lookupVar alias env).Value
                let tAlias = mkTyped (TExprVar (alias, aliasVi.VarId, aliasVi.Identity)) aliasVi.Type
                let tField = mkTyped (TExprField (tAlias, "stream", 0)) tE.Type
                Ok (mkTyped (TExprApp (tField, [tE])) tE.Type)
            | _ -> Error (ProviderStreamNeedsVar alias))

    // ---- The masked read's hidden raw read (ProviderDesugar.rawReadVerb) ----
    // Typed like `.stream` (the operand's array type, the application shape
    // kept) so lowering's tryRawRead records a dense spec exempt from the
    // provider boundary rule. Only ProviderDesugar produces this verb.
    | ExprKind.ExprApp ({ Kind = ExprKind.ExprField ({ Kind = ExprKind.ExprVar alias }, verb) }, [operand])
        when verb = Blade.ProviderDesugar.rawReadVerb && (providerAliasName env alias).IsSome ->
        inferExpr env operand |> Result.bind (fun tE ->
            match env.Subst.Resolve tE.Type with
            | ArrayElem at when at.IndexTypes |> List.forall (fun ix -> ix.Symmetry = SymNone && ix.Rank = 1) ->
                let aliasVi = (lookupVar alias env).Value
                let tAlias = mkTyped (TExprVar (alias, aliasVi.VarId, aliasVi.Identity)) aliasVi.Type
                let tField = mkTyped (TExprField (tAlias, verb, 0)) tE.Type
                Ok (mkTyped (TExprApp (tField, [tE])) tE.Type)
            | _ ->
                Error (BuiltinArgErr $"`{alias}.read_present` / `{alias}.present` read a DENSE stored variable (`store.vars.<name>`); packed, orbit and compound variables have no masked read"))

    // ---- A masked read ProviderDesugar did not rewrite ----
    // Unreachable from a checked program (the desugar refuses every position
    // it cannot serve, BL3007); a clear refusal rather than a missing-member
    // error if a funnel ever skips the pass.
    | ExprKind.ExprApp ({ Kind = ExprKind.ExprField ({ Kind = ExprKind.ExprVar alias }, ("read_present" | "present" as verb)) }, _)
        when (providerAliasName env alias).IsSome ->
        Error (BuiltinArgErr $"`{alias}.{verb}` must be the whole value of a module-level binding (`let t = store.vars.<name> |> {alias}.{verb}`)")

    // ---- Windowed packed read: alias.read_window(view, lo, hi) ----
    | ExprKind.ExprApp ({ Kind = ExprKind.ExprField ({ Kind = ExprKind.ExprVar alias }, "read_window") }, args)
        when (providerAliasName env alias).IsSome ->
        inferProviderReadWindow inferEntries env args alias

    // ---- Provider write: alias.write("path", A) ----
    | ExprKind.ExprApp ({ Kind = ExprKind.ExprField ({ Kind = ExprKind.ExprVar alias }, "write") }, args)
        when (providerAliasName env alias).IsSome ->
        inferProviderWrite inferEntries env args alias

    | ExprKind.ExprApp (({ Kind = ExprKind.ExprField ({ Kind = ExprKind.ExprVar n }, field) } as qualFuncE), args)
        when (lookupVar $"{n}.{field}" env).IsSome ->
        inferQualifiedCall env n field expr qualFuncE args

    // A MODULE-QUALIFIED PAYLOAD CONSTRUCTOR `U.Some(2.5)` (D11 leftover, deep
    // review R1): the method-call arm below would infer `U` as a value and say
    // "Unbound variable: U". The import registers the constructor under its
    // scope key `U.Some` (VariantTags), which the bare ExprVar arm resolves --
    // so it is the application of that key, as `Some(2.5)` is of `Some`.
    | ExprKind.ExprApp (({ Kind = ExprKind.ExprField ({ Kind = ExprKind.ExprVar n }, field) } as qualCtorE), args)
        when (lookupVar n env).IsNone && (Map.tryFind $"{n}.{field}" env.VariantTags).IsSome ->
        inferExpr env { expr with Kind = ExprKind.ExprApp ({ qualCtorE with Kind = ExprKind.ExprVar $"{n}.{field}" }, args) }

    // ---- Method call: obj.method(args) -> impl resolution ----
    | ExprKind.ExprApp ({ Kind = ExprKind.ExprField (obj, method) }, args) ->
        inferMethodCall env expr.Span obj method args
    // ---- INTRINSIC SPECIAL FORMS: `name(args)` read by its head name (the intrinsicForms table) ----
    | IntrinsicFormCall env expr r -> r

    // ---- TREE VIEW COMPOSITION: `T((p))((q))` is `T((p ++ q))` ----
    | ExprKind.ExprApp (func, [outerArg])
            when (match treeViewSpine env func with
                  | Some (_, prior) -> not (List.isEmpty prior)
                  | None -> false) ->
        inferTreeViewComposition env func outerArg expr

    | ExprKind.ExprApp (func, args) ->
        inferGeneralApp env func args expr

    // ---- Poly-tuple indexing OR array indexing (brackets) ----
    // `e[i]` is parsed as ExprTupleIndex regardless of e's type. Disambiguate
    // here: if e resolves to an IRTArray, this is conventional array
    // indexing and we route to TExprIndex (matching the function-call form
    // `e(i)` which goes through ExprApp's array branch at line 1518). If e
    // is a poly-pack (any other shape), keep TExprTupleIndex for IRPolyIndex
    // codegen.
    | ExprKind.ExprTupleIndex (tuple, index) ->
        // Round 6: a projection position is index-like (Int64).
        inferTupleIndex env tuple index
        |> Result.map (fun te ->
            match te.Kind with
            | TExprTupleIndex (t, idx) ->
                (match literalOnlyElem env.Subst idx with
                 | Some le when litClassOf le = 0 ->
                     (match retypeLiteralOnly env.Subst ETInt64 idx with
                      | Some idx' -> { te with Kind = TExprTupleIndex (t, idx') }
                      | None -> te)
                 | _ -> te)
            | _ -> te)
    | ExprKind.ExprField ({ Kind = ExprKind.ExprVar n }, field)
        when (lookupVar $"{n}.{field}" env).IsSome ->
        // Module-qualified value access (e.g. `let tau = Math.pi * 2.0`).
        // Same rationale as the qualified application case above.
        let qualName = $"{n}.{field}"
        let info = (lookupVar qualName env).Value
        let useTy =
            match info.Scheme with
            | Some scheme -> instantiate env.Subst scheme
            | None -> info.Type
        Ok (mkTyped (TExprVar (qualName, info.VarId, info.Identity)) useTy)

    // A MODULE-QUALIFIED CONSTRUCTOR (D11): `A.Red`, `U.Red` after
    // `import A as U` -- the spelling that names one of two imported
    // constructors of one name.
    | ExprKind.ExprField ({ Kind = ExprKind.ExprVar n }, field)
        when (lookupVar n env).IsNone && (Map.tryFind $"{n}.{field}" env.VariantTags).IsSome ->
        let key = $"{n}.{field}"
        let (parentName, payload) = env.VariantTags.[key]
        let ident = ctorIdentityOfKey env parentName key
        let ty =
            match payload with
            | None -> IRTNamed parentName
            | Some payloadTy -> mkFuncArrow [payloadTy] (IRTNamed parentName)
        Ok (mkTyped (TExprVar (ident, env.Builder.FreshId(), None)) ty)

    | ExprKind.ExprField (obj, field) ->
        inferFieldAccess env obj expr field

    // ---- Lambda ----
    | ExprKind.ExprLambda (parms, whereClause, body) -> inferLambda env parms whereClause body

    // ---- Let ----
    | ExprKind.ExprLet (binding, body) -> inferLetBinding env binding body

    // ---- Match ----
    | ExprKind.ExprMatch (scrutinee, cases) -> inferMatch env scrutinee cases

    // ---- If-then-else ----
    | ExprKind.ExprIf (cond, thenBr, elseBr) ->
        inferIfExpr env cond thenBr elseBr

    // ---- Tuple ----
    | ExprKind.ExprTuple exprs ->
        exprs |> List.map (inferExpr env) |> sequenceResults |> Result.bind (fun tExprs ->
            // A grouping packed into a tuple is caught by the BL3017 sweep
            // (collectGroupKeysEscapes' TExprTuple arm), together with every
            // other escape position -- nothing to check here.
            Ok (mkTyped (TExprTuple tExprs) (IRTTuple (tExprs |> List.map (_.Type)))))

    // ---- Array literal ----
    | ExprKind.ExprArrayLit elems ->
        inferArrayLit env elems

    // ---- Block ----
    | ExprKind.ExprBlock (stmts, finalExpr) -> inferBlock env stmts finalExpr None

    // ---- Loop constructs ----
    | ExprKind.ExprMethodFor arrays -> inferMethodFor env arrays
    | ExprKind.ExprObjectFor kernel -> inferObjectFor env kernel

    // ---- Virtual arrays ----
    | ExprKind.ExprRange idxTys ->
        inferRangeExpr env idxTys expr
    | ExprKind.ExprDotDot (lo, hi) ->
        inferDotDotRange env lo hi
    | ExprKind.ExprReverse idxTy ->
        let idx = lowerIndexType env 0 idxTy
        let elemType = elemTypeForIterationIndex idx
        Ok (mkTyped (TExprReverse idx) (mkVirtualArrayArrow [idx] elemType))

    | ExprKind.ExprHalo (innerTy, offsetsExpr) ->
        // halo<Inner, offsets> in expression position -- a range over the halo
        // slot(s): one slot for a flat offset array, k slots for the nested
        // per-axis form [[..],[..],..] (arity = sub-array count). All halo
        // semantics live in the slots (haloSlotsOf); per-slot center offsets
        // are re-derived from the tags at loop building.
        haloSlotsOf env innerTy offsetsExpr
        |> Result.map (fun slots ->
            mkTyped (TExprRange slots) (mkVirtualArrayArrow slots (elemTypeForIterationIndex (List.last slots))))

    // ---- Zip / Stack ----
    | ExprKind.ExprZip exprs ->
        inferZip env exprs
    | ExprKind.ExprStack exprs ->
        inferStack inferEntries env exprs
    | ExprKind.ExprJoin (arrays, dim) ->
        inferJoin inferEntries env arrays dim

    // ---- Computation combinators ----
    | ExprKind.ExprPure e ->
        inferExpr env e |> Result.bind (fun tE ->
            Ok (mkTyped (TExprPure tE) (IRTComputation tE.Type)))
    | ExprKind.ExprCompute e ->
        inferComputeExpr env e
    | ExprKind.ExprRead e ->
        // |> read forces a deferred provider read; the result is the operand's
        // (possibly view-modified) array, so the type passes through unchanged.
        inferExpr env e |> Result.bind (fun tE ->
            Ok (mkTyped (TExprRead tE) tE.Type))
    | ExprKind.ExprGuard (cond, body) ->
        inferExpr env cond |> Result.bind (fun tC ->
        inferExpr env body |> Result.bind (fun tB ->
            Ok (mkTyped (TExprGuard (tC, tB)) tB.Type)))
    
    // mask(array, pred) -- construct the Bool PRESENCE array over the source's
    // own index space: m(i) = pred(A(i)). mask is the predicate-driven mask
    // CONSTRUCTOR; compaction is compound(A, m) (formalism 4.5), iteration of
    // the filtered space is range<CompoundIdx<m>>, and positional composition
    // (WHERE p AND q) is elementwise Bool algebra on mask arrays. The result
    // type reuses the source's IRIndexType records VERBATIM (same Ids/Tags)
    // -- index-space identity is what compoundViewType checks, so a
    // freshly-derived mask must provably live over A's space even when A's
    // indices are anonymous.
    | ExprKind.ExprMask (array, pred) ->
        inferMask env array pred
    | ExprKind.ExprCompound (dense, mask) ->
        inferCompoundExpr env dense mask

    | ExprKind.ExprSparse (values, keys) ->
        inferSparseExpr env values keys

    | ExprKind.ExprIntersect (a, b) | ExprKind.ExprUnion (a, b) ->
        inferSetOpExpr env expr a b

    // unique(A) -- deduplicate, preserving first-occurrence order. Same
    // element type as input, dynamic extent (<= input extent).
    | ExprKind.ExprUnique a ->
        inferExpr env a |> Result.bind (fun tA ->
            requireArrayArg env tA "unique" |> Result.bind (fun arrTy ->
                let resultIdx = {
                    Id = env.Builder.FreshId(); Rank = 1
                    Extent = IRParam ("__unique", 0, IRTNat None)
                    Symmetry = SymNone; Tag = None; IxKind = IxKPlain
                    Kind = SDimension; Dependencies = []
                }
                let resultType = mkArrayArrow [resultIdx] arrTy.ElemType None
                Ok (mkTyped (TExprUnique tA) resultType)))

    | ExprKind.ExprContains (a, value) ->
        inferContainsExpr env a value

    // group_keys(keys1, keys2, ...) -- build CSR grouping structure.
    // Single key: existing single-keyed grouping (positional / EnumIdx /
    // dynamic-discovery cases).
    // Multi-key (>=2): compound grouping. Each (k1, k2, ...) tuple becomes
    // its own bucket. Discovery is dynamic regardless of any single key's
    // staticness -- even if all components were Idx<N>, the compound shape
    // is determined by which tuples actually appear in the data.
    // Precondition: all key arrays share the same outer index (same length;
    // i-th element of each represents the same record).
    | ExprKind.ExprGroupKeys keys ->
        inferGroupKeys inferEntries env keys
    | ExprKind.ExprGroupBy (values, grouping) ->
        inferGroupBy inferEntries env values grouping
    | ExprKind.ExprGroupBucket grouping ->
        inferGroupBucket inferEntries env grouping
    | ExprKind.ExprSort (array, key) ->
        inferSortExpr env array key

    | ExprKind.ExprTranspose (array, d1, d2) ->
        inferTranspose inferEntries env array d1 d2
    | ExprKind.ExprGram (leftE, rightE) ->
        inferGram inferEntries env leftE rightE
    | ExprKind.ExprGramApply (leftE, rightE, vecE) ->
        inferGramApply inferEntries env leftE rightE vecE
    | ExprKind.ExprDecompact (array, d) ->
        inferDecompact inferEntries env array d
    | ExprKind.ExprReduce (array, kernel, init, axes) ->
        // REDUCTION JOIN, Form 2: `reduce(<leg list>, (<&!>))`. The `(<&!>)`
        // section in the FOLD position is the declaration -- the fold is the
        // associative join chain leg1 <&!> leg2 <&!> ..., so the operand is a
        // list of legs, not an array of values.
        (match kernel.Kind with
         | ExprKind.ExprSection OpFusion ->
            (match init, axes with
             | None, None ->
                (match joinLegListOf env array with
                 | Some legs -> inferReductionJoin env legs array
                 | None ->
                    Error (ReductionFormErr "reduce(..., (<&!>)) folds a LIST OF LEGS into one traversal, so its operand must be an array literal of reductions -- written inline, `reduce([prodsum(a, b), reduce(c, (+))], (<&!>))`, or bound to a name by a literal. Use `object_for(<&!>) <@> (r1, r2, ...)` for the pack spelling."))
             | _ ->
                Error (ReductionFormErr "reduce(..., (<&!>)) takes neither an init nor `axes = n`: the join carries each leg's OWN fold and seed, so there is nothing shared to seed, and every leg folds its whole traversal."))
         | _ -> inferReduce inferEntries env array kernel init axes)
    | ExprKind.ExprExtents array ->
        inferExtents env array
    | ExprKind.ExprSequence exprs ->
        inferSequence env exprs
    | ExprKind.ExprReplicate (count, body) ->
        inferReplicate inferEntries env count body
    | ExprKind.ExprReynolds (kernel, isAntisym) ->
        inferExpr env kernel |> Result.bind (fun tK ->
            Ok (mkTyped (TExprReynolds (tK, isAntisym)) tK.Type))

    // ---- Type annotation ----
    | ExprKind.ExprTyped (e, tyAnno) ->
        inferTypedAnnotation env tyAnno e

    // ---- Arity special forms ----
    | ExprKind.ExprArity paramName -> Ok (mkTyped (TExprArity paramName) (IRTScalar ETInt64))
    // `nth` (the recursion-depth variable) is PLANNED (formalism 8.2): it
    // used to type as the literal 0 at every depth, a silently wrong value.
    | ExprKind.ExprNth ->
        Error (NotYetSupportedErr "`nth` (the recursion-depth variable) is planned, not built: it used to evaluate to 0 at every depth. Count the depth with an explicit parameter, or read the pack size with `arity(args)`.")
    | ExprKind.ExprZero ->
        // zero gets a fresh type variable -- unifies with int, float, bool context
        let ty = env.Subst.Fresh()
        Ok (mkTyped TExprZero ty)
    | ExprKind.ExprRank e ->
        inferExpr env e |> Result.bind (fun tE ->
            Ok (mkTyped (TExprRank tE) (IRTScalar ETInt64)))

    // ---- Struct construction ----
    | ExprKind.ExprStruct (name, fields, spread) -> inferStructConstruction env name fields spread

    // ---- Sectioned operators ----
    | ExprKind.ExprSection op ->
        let paramTy = env.Subst.Fresh()
        Ok (mkTyped (TExprSection op) (mkFuncArrow [paramTy; paramTy] paramTy))
    | ExprKind.ExprPartialApp (op, arg, isLeft) ->
        inferExpr env arg |> Result.bind (fun tArg ->
            Ok (mkTyped (TExprPartialApp (op, tArg, isLeft))
                        (mkFuncArrow [tArg.Type] tArg.Type)))

    // ---- Assignment ----
    | ExprKind.ExprAssign (lhs, rhs) ->
        inferAssignExpr env lhs rhs

    // ---- For expression ----
    | ExprKind.ExprFor (source, _constraints, kernelOpt) ->
        inferForExpr env source kernelOpt

    // ---- Align ----
    | ExprKind.ExprAlign (exprs, spec) ->
        exprs |> List.map (inferExpr env) |> sequenceResults |> Result.bind (fun tExprs ->
            let ty = if tExprs.IsEmpty then IRTUnit else tExprs.[0].Type
            Ok (mkTyped (TExprAlign (tExprs, spec)) ty))

// 10a. Binary Operation Inference


// ---- THE INTRINSIC SPECIAL FORMS: a call `name(args)` read by its HEAD NAME ----
// One ordered table, consulted by ONE arm of inferExprInner (the IntrinsicFormCall
// pattern below) at the position the forms' own arms held. Each entry names the
// heads it can match and a form that answers ValueNone when the argument shape or
// its guard does not hold: the next entry for the same head is tried, in table
// order, and when every entry declines the call falls through to the arms after it
// (the tree-view composition, then the general application). The guards are the
// arms' own, so a user binding that SHADOWS an intrinsic name (`lookupVar name env`
// is Some) still makes the form decline where it did. A new intrinsic is one entry.
and intrinsicForms : (string list * IntrinsicForm) list =
    [
      // ---- Scalar math intrinsics: exp(x), sqrt(x), ... ----
      Set.toList Blade.Grad.mathIntrinsics, (fun env _ name args ->
          match args with
          | [arg] when isMathIntrinsic name && (lookupVar name env).IsNone -> ValueSome (inferMathIntrinsicCall inferEntries env arg name)
          | _ -> ValueNone)

      // ---- abs(x): polymorphic numeric intrinsic ----
      [ "abs" ], (fun env _ _ args ->
          match args with
          | [arg] when (lookupVar "abs" env).IsNone -> ValueSome (inferAbsCall inferEntries env arg)
          | _ -> ValueNone)

      // ---- real(z) / imag(z) / arg(z): complex component/phase accessors ----
      [ "real"; "imag"; "arg" ], (fun env _ name args ->
          match args with
          | [arg] when (lookupVar name env).IsNone -> ValueSome (inferComplexPartCall inferEntries env arg name)
          | _ -> ValueNone)

      // ---- atan2(y, x) / log_base(x, b): BINARY math intrinsics ----
      // Same surface shape and shadowing rule as the unary intrinsics (a user
      // `function atan2(...)` wins), one arity up. The work is in
      // inferBinaryIntrinsic; the second entry turns a wrong-arity call into an
      // intrinsic-specific message instead of "unbound variable atan2".
      Set.toList Blade.Grad.binaryMathIntrinsics, (fun env _ name args ->
          match args with
          | [aExpr; bExpr]
                  when isBinaryIntrinsic name && (lookupVar name env).IsNone ->
              ValueSome (inferBinaryIntrinsic inferEntries env name aExpr bExpr)
          | _ -> ValueNone)
      Set.toList Blade.Grad.binaryMathIntrinsics, (fun env _ name args ->
          if isBinaryIntrinsic name && (lookupVar name env).IsNone then
              ValueSome (Error (ArgCountErr (sprintf "%s takes exactly 2 arguments (got %d): %s"
                              name args.Length
                              (if name = "atan2" then "atan2(y, x) is the quadrant-correct angle of the point (x, y)"
                               else "log_base(x, b) is log x / log b"))))
          else ValueNone)

      // ---- leaves(T): the tree pool, retyped onto its derived dense leaf axis ----
      [ "leaves" ], (fun env _ _ args ->
          match args with
          | [arg] when (lookupVar "leaves" env).IsNone -> ValueSome (inferLeavesCall inferEntries env arg)
          | _ -> ValueNone)
      [ "leaves" ], (fun env _ _ args ->
          if (lookupVar "leaves" env).IsNone then
              ValueSome (Error (ArgCountErr $"leaves takes exactly 1 argument (got {args.Length}): leaves(T) retypes one tree-slotted array onto its leaf axis."))
          else ValueNone)
      // ---- fma(a, b, c): fused multiply-add ----
      // a*b + c rounded ONCE. Plain-call intrinsic, shadowable like abs/complex.
      // Float64 scalars only (checkExpr against Float64, so a unit-carrying or
      // integer operand is refused rather than promoted): this is the
      // building block of the error-free transformations (TwoProd:
      // `e = fma(a, b, -(a*b))` is the exact rounding error of a*b) that
      // double-double arithmetic is made of, and those are written on plain
      // doubles. Its own node (TExprFma -> IRFma) so no pass can split the
      // fusion; the interpreter's Math.FusedMultiplyAdd and the emitted
      // std::fma are both correctly rounded, so the lanes agree bit for bit
      // whatever -ffp-contract the build chose.
      // segments(A) / ungroup(G[, A]) -- the structural grouping of a Chunked
      // axis and its inverse (docs/plans/structural/07 §2.2, §3.3). By-name
      // intrinsics: a user binding of the same name shadows them.
      // segments(a) over a VALUE: the tiling its annotation declared.
      [ "segments" ], (fun env _ _ args ->
          match args with
          | [ { Kind = ExprKind.ExprVar x } ] when (lookupVar "segments" env).IsNone && (lookupVar x env).IsSome && not (Map.containsKey x env.Segmentations) ->
              ValueSome (
                  match env.SlotAliases.TryGetValue x with
                  | true, aliases when aliases |> List.forall Option.isSome ->
                      (match aliases |> List.map Option.get with
                       | [ a ] -> inferSegments env a
                       | [ a0; a1 ] -> inferSegmentsGrid env [ a0; a1 ]
                       | _ -> Error (SegmentedDomainErr $"segments({x}): tile groupings of rank {aliases.Length} are not supported (rank 1 and 2)"))
                  | true, aliases ->
                      let missing = aliases |> List.mapi (fun i a -> if a.IsNone then Some (string i) else None) |> List.choose id
                      Error (SegmentedDomainErr $"""segments({x}): slot(s) {String.concat ", " missing} of '{x}' are not declared `Chunked`; annotate every slot with a `Chunked<..>` alias, or name the aliases: segments(C0[, C1])""")
                  | _ -> Error (SegmentedDomainErr $"segments({x}): '{x}' has no annotation naming `Chunked<..>` slots; annotate it (`let {x}: Array<T like C0, C1> = ..`) or name the aliases: segments(C0[, C1])"))
          | _ -> ValueNone)
      [ "segments" ], (fun env _ _ args ->
          match args with
          | [ { Kind = ExprKind.ExprVar alias } ] when (lookupVar "segments" env).IsNone -> ValueSome (inferSegments env alias)
          | _ -> ValueNone)
      [ "segments" ], (fun env _ _ args ->
          match args with
          | [ { Kind = ExprKind.ExprVar a0 }; { Kind = ExprKind.ExprVar a1 } ] when (lookupVar "segments" env).IsNone ->
              ValueSome (inferSegmentsGrid env [ a0; a1 ])
          | _ -> ValueNone)
      [ "segments" ], (fun env _ _ _ ->
          if (lookupVar "segments" env).IsNone then
              ValueSome (Error (SegmentedDomainErr "segments takes the name of a `Chunked<..>` index alias, or two of them for the tile grouping of a rank-2 array"))
          else ValueNone)
      [ "files" ], (fun env _ _ args ->
          match args with
          | [ { Kind = ExprKind.ExprVar alias } ] when (lookupVar "files" env).IsNone -> ValueSome (inferFiles env alias)
          | _ -> ValueNone)
      [ "files" ], (fun env _ _ _ ->
          if (lookupVar "files" env).IsNone then
              ValueSome (Error (SegmentedDomainErr "files takes exactly one argument: the name of a file-segmented `Chunked<..>` index alias"))
          else ValueNone)
      [ "ungroup" ], (fun env _ _ args ->
          match args with
          | [ { Kind = ExprKind.ExprArrayLit rows }; { Kind = ExprKind.ExprVar alias } ] when (lookupVar "ungroup" env).IsNone ->
              ValueSome (inferUngroupRows inferEntries env rows alias)
          | _ -> ValueNone)
      [ "ungroup" ], (fun env _ _ args ->
          match args with
          | [ g ] when (lookupVar "ungroup" env).IsNone -> ValueSome (inferUngroup inferEntries env g None)
          | _ -> ValueNone)
      [ "ungroup" ], (fun env _ _ args ->
          match args with
          | [ g; { Kind = ExprKind.ExprVar alias } ] when (lookupVar "ungroup" env).IsNone -> ValueSome (inferUngroup inferEntries env g (Some alias))
          | _ -> ValueNone)
      [ "ungroup" ], (fun env _ _ _ ->
          if (lookupVar "ungroup" env).IsNone then
              ValueSome (Error (SegmentedDomainErr "ungroup takes a segment-grouped array, optionally followed by the name of its `Chunked<..>` axis: ungroup(G) or ungroup(G, A)"))
          else ValueNone)
      [ "fma" ], (fun env _ _ args ->
          match args with
          | [aExpr; bExpr; cExpr] when (lookupVar "fma" env).IsNone -> ValueSome (inferFmaCall inferEntries env aExpr bExpr cExpr)
          | _ -> ValueNone)
      [ "fma" ], (fun env _ _ args ->
          if (lookupVar "fma" env).IsNone then
              ValueSome (Error (ArgCountErr (sprintf "fma takes exactly 3 arguments (got %d): fma(a, b, c) is a*b + c rounded once"
                              args.Length)))
          else ValueNone)

      // ---- complex(re, im): complex literal constructor ----
      [ "complex" ], (fun env _ _ args ->
          match args with
          | [reExpr; imExpr] when (lookupVar "complex" env).IsNone -> ValueSome (inferComplexCtorCall inferEntries env reExpr imExpr)
          | _ -> ValueNone)
      [ "complex" ], (fun env _ _ args ->
          if (lookupVar "complex" env).IsNone then ValueSome (Error (ComplexArity args.Length))
          else ValueNone)

      // ---- Explicit numeric casts: Float32(x), Int64(floor(x)), ... ----
      // A scalar type name in call position is a conversion intrinsic --
      // plain-call, shadowable like abs/complex. The accepted heads are
      // Types.numericCastTargets; the work (legality, the float->int rounding
      // gate, units, array lifting) is in inferNumericCast.
      (numericCastTargets |> Map.toList |> List.map fst), (fun env expr name args ->
          match args with
          | [arg]
                  when (castTargetOf name).IsSome && (lookupVar name env).IsNone ->
              ValueSome (inferNumericCast env expr.Span name (castTargetOf name).Value arg)
          | _ -> ValueNone)
      (numericCastTargets |> Map.toList |> List.map fst), (fun env _ name args ->
          if (castTargetOf name).IsSome && (lookupVar name env).IsNone then
              ValueSome (Error (InvalidCast $"{name} takes exactly 1 argument (got {args.Length}): a numeric cast converts one value, {name}(x)."))
          else ValueNone)

      // ---- prodsum(x1, ..., xk): fused fiber product-sum ----
      // Sigma_t Pi_l xl(t) over rank-1 arrays of equal extent -- the k-fold
      // generalization of a dot product, and the comoment primitive the PPL
      // moment formers elaborate to. Surface form is a plain call,
      // shadowable like the math intrinsics. Empty extent folds to 0 (sum
      // identity), so no non-empty check.
      [ "prodsum" ], (fun env _ _ args ->
          if not args.IsEmpty && (lookupVar "prodsum" env).IsNone then ValueSome (inferProdSum inferEntries env args)
          else ValueNone)

      // ---- __dist_pack(kappa1, ..., kappar): typed-dist construction intrinsic ----
      // Compiler-internal (double-underscore reserved): emitted by the PPL
      // elaboration stage after it builds the fused cumulant tower, never
      // written by users. Packs the component arrays into a value of nominal
      // type Dist<r, tau like axes>; the typed node is a plain TExprTuple (the
      // representation a Dist erases to at zonk), only the TYPE is nominal.
      [ "__dist_pack" ], (fun env _ _ args ->
          if not args.IsEmpty then ValueSome (inferDistPack inferEntries env args)
          else ValueNone)

      // ---- __rand_<fam>(key, p1, .., pk, d1, ..., dn): rand module ----
      [ "__rand_uniform"; "__rand_normal"; "__rand_exponential"
        "__rand_gamma"; "__rand_poisson"; "__rand_bernoulli"
        "__rand_beta"; "__rand_categorical"
        "__rand_uniform_at"; "__rand_normal_at"; "__rand_exponential_at"
        "__rand_gamma_at"; "__rand_poisson_at"; "__rand_bernoulli_at"
        "__rand_beta_at"; "__rand_categorical_at" ], (fun env _ fn args ->
          match args with
          | keyE :: rest when not rest.IsEmpty -> ValueSome (inferRandFamilyCall inferEntries env fn rest keyE)
          | _ -> ValueNone)

      // ---- __rand_sample_at("<kind>_at", key, stream, index, [weights], pars..) ----
      [ "__rand_sample_at" ], (fun env _ _ args ->
          match args with
          | { Kind = ExprKind.ExprLit (LitString kind) } :: keyE :: sE :: iE :: rest
                  when (lookupVar "__rand_sample_at" env).IsNone && kind.EndsWith "_at" ->
              ValueSome (inferRandSampleAt inferEntries env kind rest keyE sE iE)
          | _ -> ValueNone)

      // ---- __rand_guard(cond, "message"): an addressed draw's run-time guard ----
      // Compiler-internal: the BL8001 domain check of an `_at` draw's inline
      // form (above), raised in every lane like the fill's own guards.
      [ "__rand_guard" ], (fun env _ _ args ->
          match args with
          | [ condE; { Kind = ExprKind.ExprLit (LitString msg) } ]
                  when (lookupVar "__rand_guard" env).IsNone ->
              ValueSome (
                  checkExpr env (IRTScalar ETBool) condE
                  |> Result.map (fun tCond -> mkTyped (TExprConstraintCheck (tCond, "BL8001", msg)) IRTUnit))
          | _ -> ValueNone)

      // ---- __math_converged(cond, "message"): the math module's solver check ----
      // Compiler-internal (double-underscore reserved): emitted by the synthesized
      // Jacobi / QR bodies (Math/MathDecls) after their fixed iteration budget.
      // A false `cond` is the iteration's NON-CONVERGENCE, reported as a BL8008
      // panic in every lane (the same code the LAPACK route raises on a non-zero
      // info) instead of a silently unconverged result. Unit-typed; the message
      // is a literal so both lanes print the same text. The three-argument form
      // `__math_converged(cond, "message", "BL8010")` is the iteration-BUDGET
      // verdict (eig's Francis QR on a finite operand: the language's "budget ran
      // out" code, as a `while`-guarded recursion's); no other code is admitted.
      [ "__math_converged" ], (fun env _ _ args ->
          match args with
          | [ condE; { Kind = ExprKind.ExprLit (LitString msg) } ] ->
              ValueSome (
                  checkExpr env (IRTScalar ETBool) condE
                  |> Result.map (fun tCond -> mkTyped (TExprConstraintCheck (tCond, "BL8008", msg)) IRTUnit))
          | [ condE; { Kind = ExprKind.ExprLit (LitString msg) }; { Kind = ExprKind.ExprLit (LitString "BL8010") } ] ->
              ValueSome (
                  checkExpr env (IRTScalar ETBool) condE
                  |> Result.map (fun tCond -> mkTyped (TExprConstraintCheck (tCond, "BL8010", msg)) IRTUnit))
          | _ -> ValueNone)

      // ---- __display_emit(head, quoted, data, metaTail): display module ----
      [ "__display_emit" ], (fun env _ _ args ->
          match args with
          | [headE; quotedE; dataE; metaE]
                  when (lookupVar "__display_emit" env).IsNone ->
              ValueSome (inferDisplayEmit inferEntries env headE quotedE metaE dataE)
          | _ -> ValueNone)

      // ---- __display_emit_id(head, quoted, id, data, metaTail): display module ----
      [ "__display_emit_id" ], (fun env _ _ args ->
          match args with
          | [headE; quotedE; idE; dataE; metaE]
                  when (lookupVar "__display_emit_id" env).IsNone ->
              ValueSome (inferDisplayEmitId inferEntries env headE quotedE metaE idE dataE)
          | _ -> ValueNone)

      // ---- __display_json_array(A): display module JSON serialization ----
      [ "__display_json_array" ], (fun env _ _ args ->
          match args with
          | [arrE]
                  when (lookupVar "__display_json_array" env).IsNone ->
              ValueSome (inferDisplayJsonArray inferEntries env arrE)
          | _ -> ValueNone)

      // ---- __display_unit_label(x): elaboration-time unit/quantity label ----
      [ "__display_unit_label" ], (fun env _ _ args ->
          match args with
          | [e]
                  when (lookupVar "__display_unit_label" env).IsNone ->
              ValueSome (inferDisplayUnitLabel env e)
          | _ -> ValueNone)

      // ---- __display_json_num(x): display module scalar JSON rendering ----
      // A numeric scalar rendered as JSON text, same 15-significant-digit
      // byte-parity formatting as json_array's elements. Unit/quantity
      // annotations are transparent, so a factory can serialize its
      // `Int64<levels>` slot directly.
      [ "__display_json_num" ], (fun env _ _ args ->
          match args with
          | [numE]
                  when (lookupVar "__display_json_num" env).IsNone ->
              ValueSome (
                  inferExpr env numE |> Result.bind (fun tNum ->
                      match IR.stripUnits (env.Subst.Resolve tNum.Type) with
                      | IRTScalar (ETFloat64 | ETFloat32 | ETInt64 | ETInt32) ->
                          Ok (mkTyped (TExprDisplayNum tNum) (IRTScalar ETString))
                      | other ->
                          Error (BuiltinArgErr $"display.json_num: expected a numeric scalar, got {ppIRType other}")))
          | _ -> ValueNone)

      // ---- __display_json_string(s): display module String JSON rendering ----
      // A String rendered as a QUOTED, escaped JSON string -- quotes included,
      // so the caller writes `"\"text\":" + json_string(t)` and never has to
      // supply the delimiters (supplying them by hand is precisely the bug this
      // exists to retire). Quantity annotations are transparent, exactly as for
      // json_num, so plot.blade's `String<title>` slot serializes directly.
      [ "__display_json_string" ], (fun env _ _ args ->
          match args with
          | [strE]
                  when (lookupVar "__display_json_string" env).IsNone ->
              ValueSome (
                  inferExpr env strE |> Result.bind (fun tStr ->
                      match IR.stripUnits (env.Subst.Resolve tStr.Type) with
                      | IRTScalar ETString ->
                          Ok (mkTyped (TExprDisplayStr tStr) (IRTScalar ETString))
                      | other ->
                          Error (BuiltinArgErr $"display.json_string: expected a String, got {ppIRType other}")))
          | _ -> ValueNone)

      // ---- cumulant(d, k): dist component projection, order-guarded ----
      // The order guard as a TYPE error (ppl/NOTES.md typed-Dist arc): k must
      // be a static int in 1..r where r is the dist's carried order. Works in
      // any expression position on any Dist-typed value -- including function
      // parameters, which the elaboration-level registry could never see.
      // `cumulant` is part of the `ppl` module surface: the ppl elaborator
      // rewrites a qualified `p.cumulant(d, k)` to this internal marker, so a
      // bare `cumulant(...)` no longer resolves (import-gated, not language-wide).
      [ "__ppl_cumulant" ], (fun env _ _ args ->
          match args with
          | [dExpr; kExpr] when (lookupVar "__ppl_cumulant" env).IsNone -> ValueSome (inferCumulantProj inferEntries env dExpr kExpr)
          | _ -> ValueNone)

      // `math.matmul` as a FIRST-CLASS intrinsic (docs/plan-cpp-perf-exploitation.md).
      // The math elaborator rewrites a qualified `m.matmul(A, B)` to this
      // internal marker after validating the declared shapes, so a bare
      // `matmul(...)` still does not resolve (import-gated, like
      // `__ppl_cumulant` above). Instead of a synthesized Blade triple loop
      // specialized per (m, k, n), the call becomes an IR node that codegen
      // emits as one `blade_linalg::blade_matmul` call -- blocked/microkernel
      // GEMM is the one shape Blade-native loop code cannot approach, so it
      // earns a first-class node rather than a desugaring.
      [ "__math_matmul" ], (fun env _ _ args ->
          match args with
          | [aExpr; bExpr] when (lookupVar "__math_matmul" env).IsNone -> ValueSome (inferMatmul inferEntries env aExpr bExpr)
          | _ -> ValueNone)

      // `math.eigh` as a FIRST-CLASS intrinsic. Same import-gated
      // internal-marker trick as `__math_matmul` above, but emitted
      // CONDITIONALLY: `MathElaborate` consults
      // `LinAlgPatterns.lapackAvailable()` and only rewrites to this marker
      // when LAPACK will be there; without it the elaborator synthesizes the
      // cyclic-Jacobi Blade source, which stays the default path and the only
      // thing the interp / diff-oracle differentials ever see.
      [ "__math_eigh" ], (fun env _ _ args ->
          match args with
          | [aExpr] when (lookupVar "__math_eigh" env).IsNone -> ValueSome (inferEigh inferEntries env aExpr)
          | _ -> ValueNone)

      // `math.solve` as a FIRST-CLASS intrinsic. Import-gated internal marker
      // like `__math_matmul`, and emitted UNCONDITIONALLY like it too (NOT like
      // `__math_eigh`): the native arm is the emitted LU loop nest, so the node
      // is the only spelling of this operation and the LAPACK gate only decides
      // whether those loops are replaced by one `dgesv` call. That is the
      // difference eigh cannot have -- an eigensolver's output is not unique, so
      // its two arms could not be one verification truth, while an LU solve's is
      // (up to the last ULP) and its native arm is byte-pinned against the
      // interpreter.
      [ "__math_solve" ], (fun env _ _ args ->
          match args with
          | [aExpr; bExpr] when (lookupVar "__math_solve" env).IsNone -> ValueSome (inferSolve inferEntries env aExpr bExpr)
          | _ -> ValueNone)
      // ---- __math_lu(A) / __math_lu_solve[_t](LU, piv, b): the factorization value ----
      [ "__math_lu" ], (fun env _ _ args ->
          match args with
          | [aExpr] when (lookupVar "__math_lu" env).IsNone -> ValueSome (inferLu inferEntries env aExpr)
          | _ -> ValueNone)
      [ "__math_lu_solve" ], (fun env _ _ args ->
          match args with
          | [luExpr; pivExpr; bExpr] when (lookupVar "__math_lu_solve" env).IsNone -> ValueSome (inferLuSolve inferEntries env luExpr pivExpr bExpr false)
          | _ -> ValueNone)
      [ "__math_lu_solve_t" ], (fun env _ _ args ->
          match args with
          | [luExpr; pivExpr; bExpr] when (lookupVar "__math_lu_solve_t" env).IsNone -> ValueSome (inferLuSolve inferEntries env luExpr pivExpr bExpr true)
          | _ -> ValueNone)
    ]

/// intrinsicForms indexed by head name, each name's forms in TABLE order: built
/// once, so a call whose head is a name costs one dictionary probe.
and intrinsicFormsByName : System.Collections.Frozen.FrozenDictionary<string, IntrinsicForm[]> =
    intrinsicForms
    |> List.collect (fun (names, form) -> names |> List.map (fun n -> n, form))
    |> List.groupBy fst
    |> List.map (fun (n, forms) -> System.Collections.Generic.KeyValuePair(n, forms |> List.map snd |> Array.ofList))
    |> fun pairs -> System.Collections.Frozen.FrozenDictionary.ToFrozenDictionary(pairs)

/// The intrinsic special form a call is, tried entry by entry in table order:
/// the first entry that does not decline answers; ValueNone when every entry
/// for the head declines, or the expression is not a call by a name.
and [<return: Struct>] (|IntrinsicFormCall|_|) (env: TypeEnv) (expr: Expr) (kind: ExprKind) : TypeResult<TypedExpr> voption =
    match kind with
    | ExprKind.ExprApp ({ Kind = ExprKind.ExprVar name }, args) ->
        let mutable forms = Unchecked.defaultof<IntrinsicForm[]>
        if intrinsicFormsByName.TryGetValue(name, &forms) then
            let mutable result = ValueNone
            let mutable i = 0
            while result.IsNone && i < forms.Length do
                result <- forms[i] env expr name args
                i <- i + 1
            result
        else ValueNone
    | _ -> ValueNone

// ---- Variables ----
and inferVarExpr (env: TypeEnv) (name: string) : TypeResult<TypedExpr> =
    match lookupVar name env with
    | Some info ->
        // If this variable has a polymorphic scheme, instantiate it
        // so each use site gets fresh type variables.
        let useTy =
            match info.Scheme with
            | Some scheme -> instantiate env.Subst scheme
            | None -> info.Type
        Ok (mkTyped (TExprVar (name, info.VarId, info.Identity)) useTy)
    | None ->
        // A constructor: typed under its IDENTITY (D11 -- `A.Red` when
        // module A declares it), which is what lowering emits.
        match Map.tryFind name env.VariantTags with
        | Some (parentName, None) ->
            Ok (mkTyped (TExprVar (ctorIdentityOfKey env parentName name, env.Builder.FreshId(), None)) (IRTNamed parentName))
        | Some (parentName, Some payloadTy) ->
            Ok (mkTyped (TExprVar (ctorIdentityOfKey env parentName name, env.Builder.FreshId(), None))
                        (mkFuncArrow [payloadTy] (IRTNamed parentName)))
        | None when env.AmbiguousCtors.ContainsKey name ->
            Error (AmbiguousImport ("constructor", name, env.AmbiguousCtors.[name]))
        | None -> Error (UnboundVariable name)

and inferQualifiedCall (env: TypeEnv) (n: string) (field: string) (expr: Expr) (qualFuncE: Expr) (args: Expr list) : TypeResult<TypedExpr> =
    // ONE CALL PATH. A module-qualified callee (`M.f(x)`, `plot.contourf`,
    // a qualified ARRAY read `Geo.w(i)`) is the unqualified application
    // of the name the import registered (`alias.name`, DeclImport), so it
    // is rewritten to exactly that and re-enters the general ExprApp arm:
    // default fill, factory flattening, partial application, the arity
    // lift, where-conjunct discharge, dispatchAppOrIndex and the call
    // judgment are then the SAME code for both spellings. (This arm used
    // to build the node itself and inherited none of that seam's checks:
    // units, `mut`, extents, co-iteration, arity and array indexing were
    // all skipped, and every fix was a second copy here.) The declaration
    // facts those checks read by name ride the import under the same
    // `alias.name` key (TypeModuleExport.Callees).
    let qualName = $"{n}.{field}"
    inferExpr env { expr with Kind = ExprKind.ExprApp ({ qualFuncE with Kind = ExprKind.ExprVar qualName }, args) }

// ---- __display_unit_label(x): elaboration-time unit/quantity label ----
// Collapses to a STRING LITERAL naming the operand's unit signature: the
// quantity's nominal name when it carries one, the structural dims
// rendering otherwise, "" when bare. Arrays answer for their ELEMENT
// units (the axis-label use case). No typed/IR node survives -- both
// lanes see an ordinary string literal, so parity is definitional.
and inferDisplayUnitLabel (env: TypeEnv) (e: Expr) : TypeResult<TypedExpr> =
    inferExpr env e |> Result.map (fun tE ->
        let sigOf =
            match env.Subst.Resolve tE.Type with
            | ArrayElem at -> IR.getUnits (env.Subst.Resolve at.ElemType)
            | resolved -> IR.getUnits resolved
        let label =
            match sigOf with
            | Some u ->
                (match u.Nominal with
                 | Some n -> n
                 | None ->
                     let norm = unitNormalize u
                     if Map.isEmpty norm.Dims then "" else ppUnitSig norm)
            | None -> ""
        mkTyped (TExprLit (LitString label)) (IRTScalar ETString))

// ---- TREE VIEW COMPOSITION: `T((p))((q))` is `T((p ++ q))` ----
// The nested-view identity, discharged as a pure SURFACE rewrite performed
// BEFORE either application is typed. The landed single-path fold then does
// all the work, so the identity holds by construction rather than by a
// second code path agreeing with the first, and the intermediate never
// needs a type at all.
//
// Deliberately syntactic. This phase ships no subtree VALUE -- an escaping
// view would need a real runtime object (pointer + length) and a new IR node
// in both lanes -- so the ONLY way two applications can compose is
// textually, which is exactly what this arm recognises. `let sub = T((1))`
// still refuses through the ordinary fold ("must end at a leaf"), because
// there is no second application to splice.
//
// Guarded on the INNER head resolving to a sole-tree-slotted array, so no
// other curried-application form is disturbed: dimensional currying on
// ordinary arrays, factory chains and partial application all fall through
// untouched.
// The whole condition rides in the guard, so this arm is total once entered.
// `treeViewSpine` is defined only for a spine of SINGLE-argument groups over
// a sole-tree-slotted base, and the non-empty `prior` requires at least two
// groups -- so `T((0, 1))` (one group) falls straight through to the
// ordinary dispatch and the landed fold, and `let sub = T((1))` still
// refuses there with "must end at a leaf". Nothing else curried is
// disturbed: dimensional currying on ordinary arrays, factory chains and
// partial application never satisfy the base predicate.
and inferTreeViewComposition (env: TypeEnv) (func: Expr) (outerArg: Expr) (expr: Expr) : TypeResult<TypedExpr> =
    // A path fragment is a tuple of coordinates, or a bare scalar -- the
    // parser has no 1-tuple form, so `T((0))` is a parenthesized scalar and
    // arrives here as the one-coordinate fragment.
    let fragment (e: Expr) : Expr list =
        match e.Kind with
        | ExprKind.ExprTuple comps -> comps
        | _ -> [ e ]
    (match treeViewSpine env func with
     | Some (baseExpr, prior) ->
         let groups = prior @ [ outerArg ]
         let sp = mergeSpan (List.head groups).Span (List.last groups).Span
         let spliced = mkExpr sp (ExprTuple (groups |> List.collect fragment))
         inferExpr env (mkExpr expr.Span (ExprApp (baseExpr, [ spliced ])))
     | None -> Error (InternalErr "unreachable: tree view spine guard"))

and inferGeneralApp (env: TypeEnv) (func: Expr) (args: Expr list) (expr: Expr) : TypeResult<TypedExpr> =
    // CHAINED FACTORY SUGAR first: `f(x)(a : q1)(b : q2)` flattens to
    // the single call `f(x, a : q1, b : q2)` when the base is a
    // defaults-carrying function and every trailing-application arg is
    // quantity-tagged. Everything below then sees the flat call.
    let func, args =
        match tryFlattenFactoryChain env func args with
        | Some (baseFn, flatArgs) -> (baseFn, flatArgs)
        | None -> (func, args)
    // DEFAULT PARAMETER FILL + by-nominal routing: must run BEFORE the
    // partial-application eta-expansion below -- for a defaults-carrying
    // callee, omitting a trailing argument means "use the default", not
    // "curry". Calls with fewer than the REQUIRED count (and `_`
    // placeholders) fall through to the existing partial-application
    // machinery unchanged; invalid routing errors here.
    match tryFillDefaultArgs env expr.Span func args with
    | Some (Ok rewritten) -> inferExpr env rewritten
    | Some (Error e) -> Error e
    | None ->
    // A declared GENERIC function passed as an argument to a head that is
    // not itself a declared function is a USE: typed at an instantiated
    // copy of its signature (see the dispatch below).
    let instantiateGenericUses (tArgs: TypedExpr list) : TypedExpr list =
        tArgs |> List.map (instantiateGenericValueUse env)
    // A LAMBDA LITERAL APPLIED DIRECTLY -- `(lambda(f, x) -> f(x))(g, 2.0)`
    // -- is typed from its ARGUMENTS first: an unannotated parameter that
    // receives a function or an array takes the argument's type before
    // the body is inferred, exactly as `let f = g in ...` would.
    //
    // In the other order the parameter is an open variable while the body
    // is checked, and applying an open variable decides nothing (`f(x)` is
    // a call or an array read, and dispatchAppOrIndex rightly refuses to
    // guess), so the body's result stayed a fresh variable no use ever
    // closed: a generic `g` reached IR validation unspecialized (BL6001),
    // and an ARRAY argument -- `(lambda(a, i) -> a(i))(xs, 1)`, even the
    // identity `(lambda(a) -> a)(xs)` -- had its parameter defaulted to a
    // scalar and was refused as a rank mismatch (BL3001).
    //
    // Scalar arguments seed nothing: a scalar parameter is already settled
    // by its body (`x * 2.5`), and the call judgment then coerces a literal
    // argument against it as before. Only the shapes the body could not
    // otherwise learn (function, array) are seeded.
    match func.Kind with
    | ExprKind.ExprLambda (parms, whereClause, body)
            when parms.Length = args.Length
                 && parms |> List.exists (fun p -> p.Type.IsNone && p.Default.IsNone)
                 && not (args |> List.exists _.Kind.IsExprWildcard) ->
        args |> List.map (inferExpr env) |> sequenceResults |> Result.bind (fun tArgs ->
            // A lambda head is never a declared function.
            let tArgs = instantiateGenericUses tArgs
            let seeds =
                List.zip parms tArgs |> List.map (fun (p, a) ->
                    if p.Type.IsSome || p.Default.IsSome then None
                    else
                        match env.Subst.Resolve a.Type with
                        | FuncElem _ | ArrayElem _ -> Some a.Type
                        // G275: a FIXED numeric scalar argument (not a
                        // literal: that adapts, and binds the parameter at
                        // the call, dispatchAppOrIndex's lambdaHead) decides
                        // the parameter before the body is typed, as the one
                        // call of a lambda written where it is applied
                        // (formalism 4.3: its first use decides). Left open,
                        // the parameter fell to zonk's Float64 default and
                        // the call converted the argument: `(lambda(x) -> x /
                        // 2)(Int64(7))` computed 3.5.
                        | t when (match IR.stripUnits t with
                                  | IRTScalar (ETInt32 | ETInt64 | ETFloat32 | ETFloat64 | ETComplex64 | ETComplex128) -> true
                                  | _ -> false) -> Some a.Type
                        // An abstract array still open inside a generic
                        // body (`xs: T^1`): the variable itself, rank pin
                        // and all, so the body reads it as an array.
                        | IRTInfer id
                        | IRTUnitAnnotated (IRTInfer id, _) ->
                            (match env.Subst.GetArityConstraint id with
                             | Some k when k >= 1 -> Some a.Type
                             | _ -> None)
                        | _ -> None)
            // inferExpr's entry bookkeeping for the head node, which is
            // typed here rather than through inferExpr.
            if func.Span.StartLine > 0 then setCurrentExprSpan func.Span
            // (its parameters are a lambda value's, decided by this one
            // call: a literal beside one takes the width the call gives it,
            // TypeEnv.LetLambdaParamVars)
            env.PendingLetLambda.Value <- true
            let tLamR = inferLambdaSeeded env parms whereClause body seeds
            env.PendingLetLambda.Value <- false
            tLamR
            |> Result.map (fun te ->
                if te.Span.StartLine = 0 && func.Span.StartLine > 0 then { te with Span = func.Span } else te)
            |> Result.bind (fun tLam ->
                match tryArityLiftCall env func args tArgs tLam.Type with
                | Some synth -> inferExpr env synth
                | None -> dispatchAppOrIndex env tLam tArgs))
    | _ ->
    inferExpr env func |> Result.bind (fun tFunc ->
    // Prefix partial application (formalism 6.2.3): applying an n-ary
    // FUNCTION to 0 < k < n args eta-expands to a lambda over the
    // residual params -- lambda(__pa..) -> f(a1..ak, __pa..) -- so the
    // residual value rides the entire existing lambda pipeline
    // (inferLambda captures, lowerTypedLambda lifting, std::function
    // value emission, resolveCallable kernel wrappers). The FuncElem
    // guard keeps arrays on their own dimensional-currying path below.
    // Bound args are inlined into the lambda body (each appears exactly
    // once), so they re-evaluate per call of the residual and their
    // free locals become ordinary lambda captures -- the same semantics
    // as a user-written lambda. `func` is inferred a second time inside
    // the body; inference of an application head is pure, so the
    // discarded detection pass above costs nothing.
    // A Poly<T^r> pack param makes the arrow variadic: its declared
    // param count says nothing about legal call-site arg counts, so
    // both the placeholder desugar and the under-application
    // eta-expansion must stand down (monomorphization owns those calls).
    let hasPolyParam (paramTys: IRType list) =
        paramTys |> List.exists (fun t -> (env.Subst.Resolve t).IsIRTPoly)
    match env.Subst.Resolve tFunc.Type with
    // Single `_` placeholder (formalism 6.2.3): one hole in an
    // otherwise-full application binds every other parameter and
    // leaves the hole's parameter free -- f(_, b) == lambda(x) -> f(x, b).
    // FuncElem-gated: array wildcard-indexing (the 4.5 currying table,
    // where MULTIPLE holes are legal) stays on the ArrayElem path.
    | FuncElem (paramTys, retTy) when not (hasPolyParam paramTys) && args |> List.exists _.Kind.IsExprWildcard ->
        let wildPositions =
            args |> List.mapi (fun i a -> (i, a))
                 |> List.choose (fun (i, a) -> match a.Kind with ExprKind.ExprWildcard -> Some i | _ -> None)
        if wildPositions.Length > 1 then
            Error (InvalidApplicationErr "function partial application takes a single `_` placeholder only (formalism 6.2.3) -- bind the rest with prefix partial application or a lambda")
        elif args.Length <> paramTys.Length then
            Error (PlaceholderNeedsAllBound (args.Length, paramTys.Length))
        else
            let wildPos = wildPositions.Head
            let uid = env.Builder.FreshId()
            let name = $"__pa{uid}_w"
            let newArgs = args |> List.mapi (fun i a -> if i = wildPos then inheritSpan a (ExprVar name) else a)
            inferLambda env [{ Name = name; Type = None; Default = None; NameSpan = noSpan }] None (inheritSpan func (ExprApp (func, newArgs)))
            |> Result.bind (fun tLam ->
                // Pinned against an INSTANTIATED copy of the callee's
                // signature, like every call: the lambda's body call now
                // returns the call's own instance, and unifying that with
                // the declaration's shared return would bind the
                // declaration itself to this one use.
                let quantified, _ = calleeQuantifier env tFunc
                let pinTys, _ = instantiateOpenVars env.Subst quantified [paramTys.[wildPos]; retTy]
                unify env.Subst tLam.Type (mkFuncArrow [pinTys.[0]] pinTys.[1])
                |> Result.map (fun () -> tLam))
    | FuncElem (paramTys, retTy) when not (hasPolyParam paramTys) && not args.IsEmpty && args.Length < paramTys.Length ->
        inferPrefixPartial env func args tFunc paramTys retTy []
    | _ ->
        // A LAMBDA LITERAL MEETS A DECLARED ARROW SLOT -- `ap(lambda(v) ->
        // reduce(v * 2.0, (+)), xs)` against `ap(f: (Float64^1) -> Float64,
        // ..)`. The lambda is CHECKED against the slot, not inferred and
        // compared afterwards: its unannotated parameters take the slot's
        // parameter types before its body is typed (inferLambdaSeeded), as
        // a directly applied lambda's take its arguments'. In the other
        // order `v` was an open variable while the body was typed, so
        // whatever the body did to it decided its kind: arithmetic made it
        // a scalar (`reduce(v * 2.0, ..)` refused, `v * 2.0` emitted as a
        // `double` lambda g++ could not pass as the declared
        // `std::function<double(Array<double, 1>)>`), and an application
        // `v(0)` fell to the function reading. The slot types come from an
        // INSTANTIATED copy of the callee's signature (one per call, shared
        // by every lambda argument of it), as at every call, so a generic
        // `(T^1) -> T` is never bound to this one use. The call's OTHER
        // arguments are typed first and a fully known one teaches the copy
        // (`apg(lambda(v) -> reduce(v, (+)), [1, 2, 3])` makes `T` Int64
        // before the lambda's body is typed, not after it zonk-defaulted to
        // Float64). An annotated parameter keeps its annotation
        // (inferLambdaFull ignores its seed), and a lambda whose parameter
        // count differs from the slot's is seeded by position: its body
        // types as far as it can, and the call judgment refuses the
        // mismatch at the argument, naming the declared arrow.
        let slotCopies : IRType list option =
            let synthesized =
                match func.Kind with
                | ExprKind.ExprVar fname -> fname.StartsWith "__"
                | _ -> false
            let hasLambdaArg = args |> List.exists (fun a -> a.Kind.IsExprLambda || a.Kind.IsExprApp)
            match env.Subst.Resolve tFunc.Type with
            | FuncElem (paramTys, _)
                    when hasLambdaArg && not synthesized && not (hasPolyParam paramTys)
                         && args.Length <= paramTys.Length
                         && not (paramTys |> List.exists (fun t -> (env.Subst.Resolve t).IsIRTTuple)) ->
                let quantified, _ = calleeQuantifier env tFunc
                Some (fst (instantiateOpenVars env.Subst quantified paramTys))
            | _ -> None
        let lambdaSeeds (copies: IRType list) (i: int) : IRType option list =
            match List.tryItem i copies |> Option.map env.Subst.Resolve with
            | Some (FuncElem (slots, _)) ->
                slots |> List.map (fun s ->
                    match env.Subst.Resolve s with
                    // An open slot says nothing a fresh variable would
                    // not -- unless it is a `T^k` (an exact rank: the
                    // parameter is an array).
                    | IRTInfer id
                    | IRTUnitAnnotated (IRTInfer id, _) ->
                        (match env.Subst.GetArityConstraint id with
                         | Some k when k >= 1 -> Some s
                         | _ -> None)
                    | _ -> Some s)
            | _ -> []
        let seededLambda (copies: IRType list) (i: int) (a: Expr) =
            match a.Kind with
            | ExprKind.ExprLambda (parms, whereClause, body) ->
                let seeds = lambdaSeeds copies i
                if seeds |> List.exists Option.isSome then
                    if a.Span.StartLine > 0 then setCurrentExprSpan a.Span
                    inferLambdaSeeded env parms whereClause body seeds
                    |> Result.map (fun te ->
                        if te.Span.StartLine = 0 && a.Span.StartLine > 0 then { te with Span = a.Span } else te)
                else inferExpr env a
            | ExprKind.ExprApp _ ->
                match List.tryItem i copies with
                | Some (FuncElem _ as slot) -> checkExpr env slot a
                | _ -> inferExpr env a
            | _ -> inferExpr env a
        let inferredArgs : TypeResult<TypedExpr list> =
            match slotCopies with
            | None -> args |> List.map (inferExpr env) |> sequenceResults
            | Some copies ->
                let others =
                    args |> List.mapi (fun i a ->
                        let contextualPartial =
                            a.Kind.IsExprApp
                            && (match List.tryItem i copies |> Option.map env.Subst.Resolve with
                                | Some (FuncElem _) -> true | _ -> false)
                        if a.Kind.IsExprLambda || contextualPartial then Ok None
                        else inferExpr env a |> Result.map Some)
                    |> sequenceResults
                others |> Result.bind (fun others ->
                    // Only a CLOSED argument teaches its copy: unifying
                    // binds nothing but this call's fresh copy variables.
                    // (The judgment still judges every pair.)
                    List.zip others (List.truncate others.Length copies)
                    |> List.iter (fun (o, c) ->
                        match o with
                        | Some ta when Set.isEmpty (freeInferVars env.Subst (env.Subst.Resolve ta.Type)) ->
                            unify env.Subst c ta.Type |> ignore
                        | _ -> ())
                    List.zip args others
                    |> List.mapi (fun i (a, o) ->
                        match o with
                        | Some ta -> Ok ta
                        | None -> seededLambda copies i a)
                    |> sequenceResults)
        inferredArgs |> Result.bind (fun tArgs ->
            // Call-site constraint DISCHARGE: if the callee declared custom
            // where-clause conjuncts (e.g. PPL's `indep(a, b)`), the caller
            // must prove them for the actual arguments -- each registered
            // handler gets the callee's conjunct args plus a provenance
            // oracle mapping callee param names to the actuals' provenance.
            let dischargeErr =
                match func.Kind with
                | ExprKind.ExprVar fname ->
                    // The declaration this call RESOLVES to (binder id),
                    // not whatever last declared the name.
                    match defaultsKeyOfName env fname |> Option.map env.FuncConstraints.TryGetValue with
                    | Some (true, (paramNames, conjuncts)) ->
                        let provOf (pname: string) : Set<string> =
                            match List.tryFindIndex ((=) pname) paramNames with
                            | Some i when i < args.Length -> provenanceOfSurface env args.[i]
                            | _ -> Set.empty
                        conjuncts |> List.tryPick (fun (cname, cargs) ->
                            Blade.Constraints.lookupConstraint cname
                            |> Option.bind (fun h ->
                                match h.Discharge fname cargs provOf with
                                | Ok () -> None
                                | Error msg -> Some msg))
                    | _ -> None
                | _ -> None
            match dischargeErr with
            | Some msg -> Error (ConstraintErr msg)
            | None ->
                // A declared GENERIC function passed to a head that is not
                // itself a declared function (a lambda, a function-typed
                // parameter): the argument is a USE, so it is typed at an
                // instantiated copy of its signature, as a composition
                // operand is. Typed at the declaration's own variables,
                // `(lambda(f, x) -> f(x))(idg, 2.0)` bound the lambda's
                // `f` to them, nothing ever made them concrete, and the
                // program died BL6001; with the copy, the lambda's use
                // fixes it and IR monomorphization specializes `idg` at
                // the head's parameter type (IRMono.hmValueRefRewrite).
                // A declared head keeps the call judgment, which
                // instantiates its own signature and judges a generic
                // argument against a FUNCTION-typed parameter
                // (`twice(g: (T) -> T, x: T)`) there. A parameter that is
                // a bare VARIABLE (`function keep(f) = f`) judges
                // nothing: it passed the argument's DECLARATION variables
                // out into the call's result, where no later use could
                // fix them (`keep(dbl)(4)` died BL6001). Such an argument
                // is a use too, typed at an instantiated copy the
                // result's uses fix; monomorphization specializes the
                // reference at the copy's type (IRMono.hmValueRefRewrite).
                let tArgs =
                    let headIsDeclared =
                        match calleeDeclId env tFunc with
                        | Some fid -> env.FuncSigVarRange.ContainsKey fid
                        | None -> false
                    if not headIsDeclared then instantiateGenericUses tArgs
                    else
                        match env.Subst.Resolve tFunc.Type with
                        | FuncElem (paramTys, _) ->
                            tArgs |> List.mapi (fun i a ->
                                match List.tryItem i paramTys |> Option.map (fun p -> IR.stripUnits (env.Subst.Resolve p)) with
                                | Some (IRTInfer _) -> instantiateGenericValueUse env a
                                | _ -> a)
                        | _ -> tArgs
                // ARITY LIFT before dispatch: a call that mixes arrays
                // and scalars across ONE rank-0 signature variable is
                // re-synthesized as the map it means. Declines fall
                // through to dispatchAppOrIndex, which judges them.
                match tryArityLiftCall env func args tArgs tFunc.Type with
                | Some synth -> inferExpr env synth
                | None -> dispatchAppOrIndex env tFunc tArgs))

and inferFieldAccess (env: TypeEnv) (obj: Expr) (expr: Expr) (field: string) : TypeResult<TypedExpr> =
    inferExpr env obj |> Result.bind (fun tObj ->
        // Re-stamp the WHOLE access's span: inferExpr on the object just
        // stamped the innermost leaf ('sample' in `sample.vars.xdim`),
        // which is not where the bad name is.
        if expr.Span.StartLine > 0 then setCurrentExprSpan expr.Span
        // A miss on a RESOLVED struct is BL3018, not a fresh type
        // variable; `structFieldAccess` returns Ok None for every other
        // named type, which keeps the old fallback where it belongs.
        let resolved =
            match tObj.Type with
            | IRTNamed typeName -> structFieldAccess env typeName field
            | _ -> Ok None
        resolved |> Result.map (fun hit ->
            let (fieldTy, fieldIdx) =
                match hit with
                | Some (ty, idx) -> (ty, idx)
                | None -> (env.Subst.Fresh(), 0)
            mkTyped (TExprField (tObj, field, fieldIdx)) fieldTy))

// ---- If-then-else ----
and inferIfExpr (env: TypeEnv) (cond: Expr) (thenBr: Expr) (elseBr: Expr) : TypeResult<TypedExpr> =
    inferExpr env cond |> Result.bind (fun tCond ->
    // The condition IS a Bool -- the rule every other predicate position
    // (guards, `while`, the static evaluator) already enforces. Left
    // unchecked, `if 1 then ...` compiled through C++ truthiness and an
    // ARRAY condition reached the emitter as an IRArrayLit it has no
    // rule for. ONE exemption: the recursive-array desugar's stop-ordinal
    // update `if __grd_x then n else stop` conditions on the `while`
    // guard's value, and inferRecArray's own check of that value owns
    // the diagnostic (it names the array and says what a guard is);
    // checking here first would replace it with a bare type mismatch.
    let isRecArrayGuardVar =
        match cond.Kind with
        | ExprKind.ExprVar n -> n.StartsWith "__grd_"
        | _ -> false
    (if isRecArrayGuardVar then Ok ()
     else unify env.Subst (IRTScalar ETBool) tCond.Type) |> Result.bind (fun () ->
    inferExpr env thenBr |> Result.bind (fun tThen ->
    inferExpr env elseBr |> Result.bind (fun tElse ->
        // ROUND 6: a literal-only branch takes the other branch's WIDTH
        // within its class (adaptLiteralPeers) -- never its class, so the
        // rule below still refuses `if c then 2.5 else 10`.
        // R29: one peer judgment for every join (adaptLiteralPeersChecked):
        // a literal-only branch that does not fit the other branch's
        // width is BL3020 at the literal.
        adaptLiteralPeersChecked env [ tThen; tElse ] |> Result.bind (fun adapted ->
        let tThen, tElse =
            match adapted with
            | [ a; b ] -> a, b
            | _ -> tThen, tElse
        // THE BRANCHES MUST ALREADY SHARE A TYPE. Blade has explicit
        // numeric casts (`Float64(n)`), and those are the one licensed
        // widening pathway: an implicit promotion here would convert a
        // value the source never asked to convert, at a site whose whole
        // job is to choose between two values. Refusing is also what
        // makes the two lanes agree -- an accepted mixed pair would be
        // widened by the C++ ternary, which the interpreter cannot see.
        //
        // This result used to be DISCARDED and the node typed from the
        // then-branch, so acceptance depended on branch order: one order
        // emitted a silently-widening ternary and the other died in g++
        // as a narrowing conversion, with no Blade diagnostic either way.
        //
        // A numeric literal adapts here only in WIDTH (above): its CLASS is
        // never changed by a branch, so the `10` in `if c then 2.5 else 10`
        // stays an integer and the pair is refused -- write `10.0`.
        // Width adaptation is symmetric (either branch order gives the
        // same answer), so the order-dependence above cannot return.
        //
        // And the branches must share their STATIC EXTENTS, which unify
        // does not compare: the node is typed from the then-branch, so an
        // else-branch of a different literal length would be read at the
        // then-branch's length -- `nan`s past a short buffer. Same shared
        // refinement as every ascription seam (`staticExtentClash`).
        unify env.Subst tThen.Type tElse.Type
        |> withUnifyContext "the branches of this `if` (then-branch vs else-branch)"
        |> Result.bind (fun () ->
            match staticExtentClash env.Subst tThen.Type tElse.Type with
            | Some (d, e, a) ->
                if elseBr.Span.StartLine > 0 then setCurrentExprSpan elseBr.Span
                Error (ExtentAscribeMismatch ("the else-branch of this `if` (against the then-branch)", d + 1, e, a))
            | None -> Ok (mkTyped (TExprIf (tCond, tThen, tElse)) tThen.Type)))))))

// ---- Array literal ----
and inferArrayLit (env: TypeEnv) (elems: Expr list) : TypeResult<TypedExpr> =
    elems |> List.map (inferExpr env) |> sequenceResults |> Result.bind (fun tElems ->
        // ROUND 6: literal-only elements take the width of a non-literal
        // element of their class, else join to the widest default
        // (adaptLiteralPeers); the class join below is unchanged.
        adaptLiteralPeersChecked env tElems |> Result.bind (fun tElems ->
        // Numeric LITERALS adopt the literal's joined element type (they
        // have no committed type of their own: `[1, 2.5, 3]` is Float64,
        // exactly as `let x: Float64 = 1` is). A non-literal VALUE never
        // widens implicitly -- it meets the unification below as it is.
        let tElems =
            let isNumLit (e: TypedExpr) =
                match e.Kind with
                | TExprLit (LitInt _ | LitFloat _) -> true
                | TExprUnaryOp (OpNeg, { Kind = TExprLit (LitInt _ | LitFloat _) }) -> true
                | _ -> false
            let prims =
                tElems |> List.map (fun e ->
                    match env.Subst.Resolve e.Type with
                    | IRTScalar et -> Some et
                    | _ -> None)
            if tElems.Length >= 2 && prims |> List.forall Option.isSome && tElems |> List.exists isNumLit then
                let joined =
                    prims |> List.map Option.get
                    |> List.reduce (fun a b -> match IR.promoteElemType a b with Some p -> p | None -> a)
                let rec retype (e: TypedExpr) =
                    match e.Kind with
                    | TExprUnaryOp (OpNeg, inner) -> { e with Kind = TExprUnaryOp (OpNeg, retype inner); Type = IRTScalar joined }
                    | _ -> { e with Type = IRTScalar joined }
                tElems |> List.map (fun e ->
                    if isNumLit e && (match env.Subst.Resolve e.Type with IRTScalar et -> et <> joined | _ -> false)
                       && (match joined with ETFloat32 | ETFloat64 | ETComplex64 | ETComplex128 | ETInt32 | ETInt64 -> true | _ -> false)
                    then retype e
                    else e)
            else tElems
        // ONE ELEMENT TYPE. The literal's type is read off its FIRST element
        // (inferArrayLitType), so every other element must agree with it:
        // unified (numeric literals still widen together -- `[1, 2.5]` is
        // Float64 -- through the literal-variable rules) with units judged
        // on their own, like every other seam. Before this the rest were
        // never looked at: `[a_m, b_s]` took meters, `[1.5, true]` printed
        // [1.5, 1], two differently tagged rows stacked, and mixed Int/Float
        // or mixed rank died in g++.
        let unitsOf (t: IRType) =
            match env.Subst.Resolve t with
            | ArrayElem at -> IR.getUnits (env.Subst.Resolve at.ElemType)
            | r -> IR.getUnits r
        let disagreement =
            match tElems with
            | first :: rest ->
                rest |> List.tryPick (fun e ->
                    // Two nested SUB-LITERALS (rows written in brackets)
                    // are the literal's own shape, which the ragged /
                    // compact-simplex inference owns: `[[1, 2, 3], [4, 5],
                    // [6]]` has rows of different types on purpose.
                    let bothSubLiterals = first.Kind.IsTExprArrayLit && e.Kind.IsTExprArrayLit
                    if bothSubLiterals then None else
                    match unitsOf first.Type, unitsOf e.Type with
                    | Some a, Some b when not (unitCompatible a b) || not (unitSameScale a b) ->
                        Some (e, UnitMismatch ("the elements of an array literal", ppUnitSig a, ppUnitSig b))
                    | _ ->
                        match unify env.Subst (stripUnitsDeep env.Subst first.Type) (stripUnitsDeep env.Subst e.Type) with
                        | Ok () -> None
                        | Error _ ->
                            Some (e, TypeMismatch (env.Subst.Resolve first.Type, env.Subst.Resolve e.Type)))
            | [] -> None
        match disagreement with
        | Some (e, err) ->
            if e.Span.StartLine > 0 then setCurrentExprSpan e.Span
            Error err
        | None ->
        let arrTy = inferArrayLitType env.Builder tElems
        Ok (mkTyped (TExprArrayLit (tElems, arrTy)) (mkArrayLike arrTy))))

// ---- Virtual arrays ----
// Iteration-tagging: when the source index type carries a user-named tag
// (e.g., `range<LatIdx>`), the element type is wrapped as Nat<LatIdx>
// rather than bare int64. Iterating the virtual array via method_for
// then yields tagged values to the kernel -- so `range<LatIdx> <@>
// lambda(i) -> A(i)` (where A is `Array<T like LatIdx>`) typechecks
// cleanly under step 5's tag rule without an annotation on i.
// Anonymous index types (`Idx<5>`, etc., Tag=None) and synthetic tags
// (prefixed "__") keep the bare int64 element type, matching gap 1's
// asymmetric treatment of named vs anonymous element-position tags.
and inferRangeExpr (env: TypeEnv) (idxTys: TypeExpr list) (expr: Expr) : TypeResult<TypedExpr> =
    // An enumerable constrained domain (`range<R>`, R a `static struct`;
    // docs/plans/structural/06) is routed HERE, ahead of lowering, so a
    // refusal has a code and a table-enumerated domain gets its advisory:
    // the closed-form route visits exactly the solutions; the table
    // route (class B) pays the box once at compile time and iterates the
    // baked keys; neither scans the box at run time.
    let domainSlots =
        idxTys |> List.choose (fun ty ->
            match ty with
            | TyNamed (n, []) ->
                (match Map.tryFind n (Blade.TypeLower.staticEnvOf env).Structs with
                 | Some si when si.IsStatic && (match lookupTypeDef n env with Some (TDIStruct _) -> true | _ -> false) -> Some n
                 | _ -> None)
            | _ -> None)
    domainSlots
    |> List.fold (fun acc n ->
        acc |> Result.bind (fun () ->
            match Blade.StructIdxSpec.domainRoute (Blade.TypeLower.staticEnvOf env) n with
            | Error why -> Error (ConstrainedDomainRefused (n, why))
            | Ok (Blade.StructIdxSpec.DomainTable (_, card, why)) ->
                emitWarning env "BL4010" expr.Span
                    $"range<{n}>: the domain's constraints are not closed-form ({why}), so its {card} solution(s) were enumerated over the whole box at compile time and baked as a key table; the loop visits the solutions only, but the box is capped at {Blade.StructIdxSpec.maxBoxCells} cells. Writing the constraints as linear inequalities on the fields (`i - j <= w`, `abs(i - j) <= w`, `l3 <= l1 + l2`, `m1 + m2 == m_out`) enumerates them in closed form, uncapped"
                Ok ()
            | Ok (Blade.StructIdxSpec.DomainClosedForm plan) when plan.Card = 0L ->
                emitWarning env "BL4010" expr.Span
                    $"range<{n}>: the domain is EMPTY -- its constraints admit no cell of the box, so this loop runs zero times and its output has no cells"
                Ok ()
            | Ok _ -> Ok ())) (Ok ())
    |> Result.bind (fun () ->
    // A TyHalo slot builds through haloSlotsOf (static-offset validation +
    // interior shrink + "__halowin|" tag) and may SPLICE several slots
    // (nested per-axis offsets); every other slot lowers as before. n-D
    // separable stencils are ranges of halo slots -- one window per slot.
    (idxTys
     |> List.map (fun ty ->
         match ty with
         | TyHalo (innerTy, offsetsExpr) -> haloSlotsOf env innerTy offsetsExpr
         | _ -> Ok [lowerIndexType env 0 ty])
     |> sequenceResults)
    |> Result.map List.concat
    |> Result.bind (fun idxs -> typed {
    // A CompoundIdx slot (masked product space, formalism 4.5) IS the whole
    // iteration space, so it cannot share a range<> with other index types.
    // Reject range<CompoundIdx<m>, J> HERE at typecheck (EXPECT: typecheck
    // failure) rather than letting it fall through and leak a codegen #error
    // plus a cascade of undeclared-variable errors into the generated C++.
    // A SOLE compound slot (idxs.Length = 1) is fine and passes through.
    let hasCompound =
        idxs |> List.exists _.Extent.IsIRCompoundMask
    let hasSparse =
        idxs |> List.exists _.Extent.IsIRSparseKeys
    // A wreath range slot is the storage refusal's THIRD front-end door
    // (beside the let annotation and the function signature): `range<...>`
    // names an ITERATION space and needs no annotation, so without this the
    // program reaches buildRawLoopLevels' backstop and surfaces as a BL9001
    // internal error -- accurate about the failure, wrong about whose fault
    // it is.
    do! (match idxs |> List.tryFind (fun ix -> ix.Symmetry = SymWreath) with
         | Some ix ->
             Error (OrbitStorageUnsupported (ppOrbitLevels (orbitLevelsOf ix), "range<> iteration slot"))
         | None -> Ok ())
    // The tree twin of the wreath door above, and the same third front-end
    // door: `range<TreeIdx<s>>` names an iteration space with no annotation
    // to catch, so without this the program reaches buildRawLoopLevels'
    // backstop and dies as a BL9001 internal error.
    do! (match idxs |> List.tryPick (fun ix -> match ix with TreeIdxLike r -> Some r | _ -> None) with
         | Some rendered ->
             Error (TreeIdxUnsupported (rendered, "range<> iteration slot"))
         | None -> Ok ())
    // A bad SHAPE reaching a range slot: the marker record never sees an
    // annotation consumer, so surface it here too.
    do! (match idxs |> List.tryPick (fun ix ->
                           if ix.IxKind = IxKErrorTreeBadShape then
                               match ix.Extent with
                               | IRParam (d, _, _) -> Some d
                               | _ -> Some "invalid shape"
                           else None) with
         | Some detail -> Error (TreeIdxShape detail)
         | None -> Ok ())
    // A range slot whose extent never resolved. `range<>` is VIRTUAL: it
    // materializes no object, so codegen takes every bound and every
    // output extent from the slot's own Extent expression -- and a
    // symbolic `IRParam` placeholder has nothing to take. What reached
    // the C++ instead was `__range<i>.extents[0]`, a reference to the
    // operand variable a virtual range deliberately never declares, so
    // the program died in g++ naming a compiler-internal. Same reasoning
    // as the wreath door above: refuse at the seam that can still name
    // the user's own text.
    //
    // Narrowly: a bare NAME that resolved to neither a value nor a type
    // (`lowerExtentExpr`'s ExprVar arm, `lowerIndexType`'s unregistered-
    // `TyNamed` fallback). Three placeholder families are deliberately
    // NOT refused here, because each is resolved by machinery that runs
    // after this seam or reports better elsewhere:
    //   * `__`-prefixed internal sentinels (`__depidx_inner` and kin),
    //     supplied by their own iteration machinery;
    //   * the bad-spec error markers, which carry a spec-specific
    //     diagnostic on the annotation path;
    //   * `"?"`, `lowerExtentExpr`'s fallback for an extent that is an
    //     EXPRESSION it cannot lower structurally rather than an unknown
    //     name. `range<Idx<arity(args)>>` (arity/015) lands here and is
    //     legitimate: the deferred-former unroll rewrites it into an
    //     n-element array literal before codegen, precisely so no
    //     symbolic extent ever reaches a bound (IR.fs, pack former).
    //     Refusing it would reject a working program.
    let unresolvedSlot =
        idxs |> List.tryPick (fun ix ->
            match ix.IxKind, ix.Extent with
            | (IxKErrorIrrepsBadSpec | IxKErrorPgIrrepsBadSpec | IxKErrorRaggedNoPrior), _ -> None
            | _, IRParam (n, _, _) when n <> "?" && not (n.StartsWith "__") -> Some n
            | _ -> None)
    do! (match unresolvedSlot with
         | Some n ->
             Error (StaticValueErr $"range<...>: the extent '{n}' is not known at compile time, and a range has no runtime object to read one from -- it is a virtual iteration space, so its bounds must come from the type. '{n}' names neither a value in scope nor a declared index type. Declare it as an index type (`type {n} = Idx<N>`, then `range<{n}>`), bind it with `let static {n} = N`, or write the extent literally.")
         | None -> Ok ())
    if hasCompound && idxs.Length > 1 then
        return! Error (IndexTypeErr "range<CompoundIdx<m>, ...>: a compound range slot cannot be combined with other index types in one range<> (formalism 4.5)")
    elif hasSparse && idxs.Length > 1 then
        // Same whole-iteration-space rule as the compound slot: the key
        // enumeration IS the space.
        return! Error (IndexTypeErr "range<SparseIdx<keys>, ...>: a sparse range slot cannot be combined with other index types in one range<> (formalism 3.5)")
    else
    // Each listed index type becomes one virtual slot; downstream the slots
    // uncurry into nested loop levels. The element type is taken from the
    // innermost (last) index -- the value yielded at the deepest level --
    // which preserves single-index behavior (one slot -> that slot's tagged
    // element type).
    let elemType =
        match List.tryLast idxs with
        | Some i -> elemTypeForIterationIndex i
        | None -> IRTScalar ETInt64
    return mkTyped (TExprRange idxs) (mkVirtualArrayArrow idxs elemType) }))

and inferDotDotRange (env: TypeEnv) (lo: Expr) (hi: Expr) : TypeResult<TypedExpr> =
    inferExpr env lo |> Result.bind (fun tLo ->
    inferExpr env hi |> Result.bind (fun tHi ->
        // Round 6: a range bound is index-like -- a literal-only integer
        // bound is an Int64.
        let int64Bound (t: TypedExpr) =
            match literalOnlyElem env.Subst t with
            | Some le when litClassOf le = 0 -> retypeLiteralOnly env.Subst ETInt64 t |> Option.defaultValue t
            | _ -> t
        let tLo = int64Bound tLo
        let tHi = int64Bound tHi
        // A BOUND IS AN INTEGER (an Int64, or an index value). A float bound
        // used to type-check and reach the emitted extent and offset as a C++
        // cast, `(size_t)((3L - 1.5))` / `(int64_t)(1.5)` -- a silent
        // float-to-int conversion, which the mixing rule (formalism 2.4)
        // refuses everywhere else. A still-open bound defaults to Int64, as
        // a subscript does.
        let boundRefusal (e: Expr) (t: TypedExpr) =
            match IR.stripUnits (env.Subst.Resolve t.Type) with
            | IRTScalar (ETInt64 | ETInt32) | IRTNat _ -> None
            | IRTIdxTagged (inner, _) ->
                (match IR.stripUnits (env.Subst.Resolve inner) with
                 | IRTScalar (ETInt64 | ETInt32) | IRTNat _ | IRTInfer _ -> None
                 | other -> Some (e, other))
            | IRTInfer vid -> env.Subst.MarkIndexDefault vid; None
            | other -> Some (e, other)
        match boundRefusal lo tLo |> Option.orElse (boundRefusal hi tHi) with
        | Some (e, ty) ->
            if e.Span.StartLine > 0 then setCurrentExprSpan e.Span
            Error (ImplicitNumericMix
                     ($"a range bound is an integer (an Int64 or an index value), but this bound is {ppIRType ty}: "
                      + "`lo..hi` counts whole positions, and Blade converts nothing implicitly. "
                      + "Spell the rounding at the bound -- Int64(floor(x)) or Int64(ceil(x))."))
        | None ->
        // The extent under the full static contract (literals, `let static`
        // names, static calls): `0..N` with `let static N = 5` is an
        // Idx<5>. A bound that is not static leaves the extent UNKNOWN
        // (lowerExtentExpr's give-up), read at run time from the range
        // Lowering builds -- never a literal 0, which every consumer of the
        // TYPE believed: `Float64(0..N)` came out empty, `extents(0..N)`
        // was 0 and `reduce(0..n, (+))` was refused as statically empty.
        // StaticEval reads statics by NAME, so a bound is folded only when
        // every name in it still resolves to the `let static` itself (a
        // static binds ReadOnly with its value; a parameter or local of
        // the same spelling shadows it and is a runtime bound).
        let staticallyBound (e: Expr) =
            collectFreeVars Set.empty e
            |> Set.forall (fun n ->
                match lookupVar n env with
                | Some vi -> vi.Assign = ReadOnly && vi.TypedValue.IsSome && env.StaticValues.ContainsKey n
                | None -> true)
        // Integer bounds only (the evaluators truncate a float literal),
        // and a literal keeps its full Int64 value.
        let foldBound (e: Expr) (t: TypedExpr) : int64 option =
            match env.Subst.Resolve t.Type with
            | IRTScalar (ETInt64 | ETInt32) when staticallyBound e ->
                (match e.Kind with
                 | ExprKind.ExprLit (LitInt n) -> Some n
                 | _ -> evalStaticIntExpr env e |> Option.map int64)
            | _ -> None
        // A folded range also hands Lowering its bounds as LITERALS, so the
        // range it builds carries the same literal extent and offset the
        // type does: `L..H` lowered to `H - L` over the static names, which
        // codegen cannot evaluate (`extents(L..H)` refused BL7004).
        let literalOf (t: TypedExpr) (v: int64) =
            { t with Kind = TExprLit (LitInt v); Type = IRTScalar ETInt64 }
        let (extentExpr, tLo, tHi) =
            match foldBound lo tLo, foldBound hi tHi with
            | Some a, Some b -> (IRLit (IRLitInt (b - a)), literalOf tLo a, literalOf tHi b)
            | _ -> (IRParam ("?", 0, IRTNat None), tLo, tHi)
        let idx = {
            Id = env.Builder.FreshId()
            Rank = 1
            Extent = extentExpr
            Symmetry = SymNone
            Tag = Some "__anon"; IxKind = IxKPlain
            Kind = SDimension
            Dependencies = []
        }
        // ExprDotDot has no index-type annotation, so element type
        // stays bare int64 (no name to tag with).
        Ok (mkTyped (TExprDotDot (tLo, tHi)) (mkVirtualArrayArrow [idx] (IRTScalar ETInt64)))))

and inferComputeExpr (env: TypeEnv) (e: Expr) : TypeResult<TypedExpr> =
    inferExpr env e |> Result.bind (fun tE ->
        // IDEMPOTENCE. `compute` forces a computation to a value; forcing a
        // value again is the identity, and the second wrapper is not free:
        // it lowers to IRCompute(IRCompute(IRApplyCombinator)), a shape
        // genFuncBodyScoped's let dispatch (which matches ONE IRCompute)
        // falls through, landing on the inline expression form and its
        // "array-valued elementwise kernel body" rejection. This is not a
        // user writing `compute` twice: the ELEMENTWISE ARRAY OPS
        // re-synthesize themselves as `compute(method_for(..) <@> k)`
        // (inferBinOp's zip and array/scalar-broadcast arms), so any
        // `A - s |> compute` arrives here already computed. At module
        // level genComputeBinding peels IRCompute recursively and the
        // double wrap was invisible; inside a FUNCTION body it was a hard
        // codegen failure. Fold it at the source instead.
        match tE.Kind with
        | TExprCompute _ -> Ok tE
        | _ ->
            let inner = match tE.Type with IRTComputation t -> t | t -> t
            Ok (mkTyped (TExprCompute tE) inner))

and inferCompoundExpr (env: TypeEnv) (dense: Expr) (mask: Expr) : TypeResult<TypedExpr> =
    inferExpr env dense |> Result.bind (fun tDense ->
    inferExpr env mask |> Result.bind (fun tMask ->
        match env.Subst.Resolve(tDense.Type), env.Subst.Resolve(tMask.Type) with
        | ArrayElem denseArr, ArrayElem maskArr ->
            let cid = env.Builder.FreshId()
            (match compoundViewType cid denseArr maskArr (IRLit IRLitUnit) with
             | Ok compoundTy ->
                 // G265 (d): the compound record's mask provenance -- the
                 // mask binding when the mask is a named value, resolved
                 // through a chain of plain aliases (`let k2 = keep`) to its
                 // ROOT binder, but never past a binder the module may write
                 // or reassign (the never-written proof of the stored alias
                 // classes, which judges MODULE-LEVEL binders only: a block or
                 // a callable body is not resolved) -- else a fresh
                 // token (every inline mask is its own index space). Wave-end
                 // review F3.
                 let rec maskRoot (seen: Set<IRId>) (name: string) (vid: IRId) : IRId =
                     if env.InCallableBody || not (env.ModuleLetBinders.Contains vid) then vid
                     else
                     match Map.tryFind name env.Variables with
                     | Some vi when vi.VarId = vid ->
                         (match vi.TypedValue with
                          | Some { Kind = TExprVar (src, srcVid, _) }
                              when not (seen.Contains srcVid) && not (src.Contains ".")
                                   && env.ModuleLetBinders.Contains srcVid
                                   && (match Map.tryFind src env.Variables with
                                       | Some si -> si.VarId = srcVid
                                       | None -> false) ->
                              (match env.StoredAliasNeverWritten.Value src false [ src; name ] with
                               | Ok _ -> maskRoot (seen.Add vid) src srcVid
                               | Error _ -> vid)
                          | _ -> vid)
                     | _ -> vid
                 let token =
                     match tMask.Kind with
                     | TExprVar (name, vid, _) -> maskRoot (Set.singleton vid) name vid
                     | _ -> env.Builder.FreshId()
                 env.CompoundMaskTokens.[cid] <- token
                 Ok (mkTyped (TExprCompound (tDense, tMask)) compoundTy)
             | Error msg -> Error (BuiltinArgErr msg))
        | _ -> Error (BuiltinArgErr "compound(dense, mask) expects two array arguments: a dense array and a bool mask covering its leading dimensions")))

and inferSparseExpr (env: TypeEnv) (values: Expr) (keys: Expr) : TypeResult<TypedExpr> =
    // sparse(values, keys): bundle a values array whose LEADING dimension
    // is the key axis (one cell per key, IN KEY ORDER -- no scatter) with
    // an explicit key list into a SparseIdx-typed array (formalism 3.5).
    // Shape rule (mirrors compoundViewType's leading-prefix rule): the
    // FIRST values dimension collapses into the SparseIdx axis; remaining
    // dims stay as trailing index slots, giving
    // `Array<T like SparseIdx<keys>, Idx<...>>` (rank-1 values -> scalar
    // Sparse). Unlike the compound builder, no leading-PREFIX matching is
    // needed: the key axis is one dimension by construction. Keys resolve
    // as in the SparseIdx<keys> type form; |values| = |keys| is a runtime
    // guard.
    inferExpr env values |> Result.bind (fun tValues ->
    inferExpr env keys |> Result.bind (fun tKeys ->
        match env.Subst.Resolve tValues.Type with
        | ArrayElem valuesArr ->
            (match valuesArr.IndexTypes with
             | [] ->
                 Error (BuiltinArgErr "sparse(values, keys): values must be an array whose leading dimension is the key axis (one cell per key, in key order)")
             | keyAxis :: trailing when (max 1 keyAxis.Rank) = 1 ->
                 (match resolveSparseKeysSource env keys with
                  | Ok (source, rank) ->
                      let sparseIdx =
                          { Id = env.Builder.FreshId(); Rank = rank
                            Extent = IRSparseKeys source
                            Symmetry = SymNone; Tag = Some "__sparseidx"; IxKind = IxKSparse
                            Kind = SDimension; Dependencies = [] }
                      // The key axis is REPLACED by the sparse slot; the
                      // remaining values dims carry over verbatim as
                      // trailing slots (same records, so index-space
                      // identity is preserved for downstream reads).
                      let sparseTy = mkArrayArrow (sparseIdx :: trailing) valuesArr.ElemType valuesArr.Identity
                      Ok (mkTyped (TExprSparse (tValues, tKeys)) sparseTy)
                  | Error msg -> Error (BuiltinArgErr msg))
             | keyAxis :: _ ->
                 Error (BuiltinArgErr $"sparse(values, keys): the LEADING values dimension is the key axis and must be a plain rank-1 index; got a rank-{keyAxis.Rank} (compact/tabulated) slot. Trailing dimensions may be any shape."))
        | _ -> Error (BuiltinArgErr "sparse(values, keys) expects a values array (leading dim = key axis, remaining dims trailing) and a key list (a `let static` tuple list or a rank-1 tuple-array variable)")))

// intersect(A, B) / union(A, B) -- set operations on arrays
and inferSetOpExpr (env: TypeEnv) (expr: Expr) (a: Expr) (b: Expr) : TypeResult<TypedExpr> =
    let isIntersect = expr.Kind.IsExprIntersect
    let opName = if isIntersect then "intersect" else "union"
    inferExpr env a |> Result.bind (fun tA ->
    inferExpr env b |> Result.bind (fun tB ->
        requireArrayArg env tA opName |> Result.bind (fun arrTy ->
            // Drive inference for the second array too -- both should be
            // arrays of compatible elem type.
            requireArrayArg env tB opName |> Result.bind (fun _arrTyB ->
                let resultIdx = {
                    Id = env.Builder.FreshId(); Rank = 1
                    Extent = IRParam ((if isIntersect then "__isect" else "__union"), 0, IRTNat None)
                    Symmetry = SymNone; Tag = None; IxKind = IxKPlain
                    Kind = SDimension; Dependencies = []
                }
                let resultType = mkArrayArrow [resultIdx] arrTy.ElemType None
                let texpr = if isIntersect then TExprIntersect (tA, tB) else TExprUnion (tA, tB)
                Ok (mkTyped texpr resultType)))))

// contains(A, x) -- membership test. Returns Bool. The value's type
// must unify with the array's element type; mismatch (e.g., looking
// for a Float64 in an Int64 array) is a hard error.
and inferContainsExpr (env: TypeEnv) (a: Expr) (value: Expr) : TypeResult<TypedExpr> =
    inferExpr env a |> Result.bind (fun tA ->
    inferExpr env value |> Result.bind (fun tValue ->
        requireArrayArg env tA "contains" |> Result.bind (fun arrTy ->
            // G114's class (round 4 wave 5 lane c): a literal-only target
            // takes the set's element width (`contains(a64, 0.0)` probes
            // a Float64), the partner rule of formalism 2.4; it was typed
            // at the literal default and refused BL3001 over a Float64 set.
            let tValue = adaptLiteralToFirst env [ arrTy.ElemType ] tValue
            unify env.Subst tValue.Type arrTy.ElemType
            |> Result.bind (fun () ->
                Ok (mkTyped (TExprContains (tA, tValue)) (IRTScalar ETBool))))))

and inferSortExpr (env: TypeEnv) (array: Expr) (key: Expr) : TypeResult<TypedExpr> =
    inferExpr env array |> Result.bind (fun tArr ->
    inferExpr env key |> Result.bind (fun tKey ->
        requireArrayArg env tArr "sort" |> Result.bind (fun arrTy ->
            if arrTy.IndexTypes.Length <> 1 then
                Error (NotYetSupportedErr "sort() requires a rank-1 array (multi-rank sort not yet supported)")
            // G126 (round 4 wave 5 lane c): a NON-PLAIN single record --
            // `range<SymIdx<2, n>>`, whose cells are index tuples -- passed
            // the record count and was BL7001 in C++ / unsupported in the
            // interpreter. Only a rank-1 record sorts its own cells.
            elif ((match array.Kind with ExprKind.ExprRange _ -> true | _ -> false) || arrTy.IsVirtual)
                 && (arrTy.IndexTypes.[0].Rank <> 1 || arrTy.IndexTypes.[0].Symmetry <> SymNone) then
                Error (NotYetSupportedErr $"sort() requires a rank-1 array or a plain single-slot range; this operand is over {ppIndexType arrTy.IndexTypes.[0]}, whose cells are index tuples (sorting them is not supported)")
            else
                let srcIdx = arrTy.IndexTypes.[0]
                // Fresh anonymous index, same extent as source. Sort doesn't
                // change length, so the static extent (when known) propagates.
                let resultIdx = {
                    Id = env.Builder.FreshId(); Rank = 1
                    Extent = srcIdx.Extent
                    Symmetry = SymNone; Tag = None; IxKind = IxKPlain
                    Kind = SDimension; Dependencies = []
                }
                // The key extractor is `Element -> Orderable`. inferExpr
                // typed it without knowing the element type, so an
                // unannotated param starts as a fresh IRTInfer and, with
                // nothing to bind it, zonkType's Float64 default takes
                // over -- the lifted key function then emits as
                // `double __lambda_N(double)` and any body that USES the
                // element as an integer/index (`A(i)` on an index-typed
                // array) dies in g++ under -Werror=float-conversion.
                // Same unification, same reason, as inferMask's predicate
                // param; a named-function key already carries declared
                // params, so only the lambda arm needs it.
                let unifyKeyParam =
                    match tKey.Kind with
                    | TExprLambda info when info.Params.Length = 1 ->
                        unify env.Subst info.Params.[0].Type arrTy.ElemType
                    | _ -> callableRefParamMeetsCells env tKey arrTy.ElemType
                // THE KEY'S RESULT TYPE (gap G126; docs/features/sql.md
                // section 9): a real, integer, Bool or String scalar --
                // the types sort's total preorder is written for, in
                // every lane. A COMPLEX key has no order (it was BL9002
                // in C++ and sorted by the real part in the
                // interpreter); a TUPLE key compiled in C++ (std::tuple
                // `<`, lexicographic) while the interpreter tied every
                // key; an array key has no scalar order. Refused here.
                let keyResultOk () =
                    let rec ok (t: IRType) =
                        match IR.stripUnits (env.Subst.Resolve t) with
                        | IRTScalar (ETComplex64 | ETComplex128) -> false
                        | IRTScalar _ | IRTNat _ | IRTInfer _ -> true
                        | IRTIdxTagged (inner, _) -> ok inner
                        | _ -> false
                    match env.Subst.Resolve tKey.Type with
                    | FuncElem (_, ret) ->
                        if ok ret then Ok ()
                        else Error (NotYetSupportedErr $"sort(A, key): the key must return a real, integer, Bool or String scalar -- the types sort's order is defined for (NaN first, signed zeros tied; strings by code point) -- but this key returns {ppIRType (env.Subst.Resolve ret)}. A complex value has no order, and a tuple or array key is not supported: sort by one scalar key, or compose the key into one (`k1 * scale + k2`).")
                    | _ -> Ok ()
                // (F2: the key's field reads / tuple projections of its
                // now-known parameter take their types before its result
                // is judged -- else an Int64 field key fell to a Float64
                // default)
                unifyKeyParam |> Result.map (fun () -> settleOpenProjections env tKey)
                |> Result.bind keyResultOk |> Result.bind (fun () ->
                    // R3: a key that is a value of a GENERIC variable
                    // orders that variable's values; at an integer EnumIdx
                    // instance they are keys (GOKeyObserve -- the call hands
                    // the callee the keys).
                    (match env.CurrentGenericObligations, env.Subst.Resolve tKey.Type with
                     | Some acc, FuncElem (_, ret) ->
                         (match IR.stripUnits (env.Subst.Resolve ret) with
                          | IRTInfer v when env.Subst.IsPolymorphicId v -> acc.Add(GOKeyObserve (v, "sort key"))
                          | _ -> ())
                     | _ -> ())
                    let resultType = mkArrayArrow [resultIdx] arrTy.ElemType None
                    Ok (mkTyped (TExprSort (tArr, tKey)) resultType)))))

// ---- Type annotation ----
and inferTypedAnnotation (env: TypeEnv) (tyAnno: TypeExpr) (e: Expr) : TypeResult<TypedExpr> =
    // Route through bidirectional checkExpr so the annotation pushes
    // down into literal/constructor positions. The motivating case
    // is `complex(re, im) : Complex64`: the constructor checked
    // against a Complex width adopts it (and the 2-tuple complex
    // form gets its steering error there rather than a generic
    // unify failure). For non-special-cased shapes, checkExpr
    // falls through to inferExpr + unify, preserving plain-cast
    // behavior.
    // A quantity name inside a COMPOUND unit annotation is terminal
    // (BL3011) -- checked on the surface type, since lowering degrades
    // rather than errors.
    match unitAnnoError env tyAnno with
    | Some err -> Error err
    | None ->
    let annoTy = lowerTypeExpr env tyAnno
    match indexCastTarget env annoTy with
    | Some (tag, ext) -> inferIndexCast env e annoTy tag ext
    | None ->
    checkExpr env annoTy e |> Result.bind (fun tE ->
      // G190 (a): an EXPRESSION ascription to an opaque RaggedIdx<_> of a
      // non-ragged value hides its dense provenance exactly as a let
      // ascription would (Codex diff review of C3)
      // (components of a TUPLE ascription too: Codex diff review round 2)
      let rec hides (a: IRType) (v: IRType) =
          match IR.stripUnits (env.Subst.Resolve a), IR.stripUnits (env.Subst.Resolve v) with
          | ArrayElem aa, ArrayElem va ->
              (match aa.IndexTypes, va.IndexTypes with
               | ax :: _, vx :: _ -> ax.IxKind = IxKRaggedOpaque && not (isRaggedRowKind vx.IxKind)
               | _ -> false)
          | IRTTuple ats, IRTTuple vts when ats.Length = vts.Length -> List.exists2 hides ats vts
          | _ -> false
      match hides annoTy tE.Type with
      | true when not e.Kind.IsExprArrayLit ->
          Error (EnumKeyObserved "a dense array ascribed an opaque `RaggedIdx<_>` type: that form is the type of a row PEELED from a ragged parent, and a dense array has no ragged parent to stand for (formalism 4.4). Pass a row of a ragged array (`R(0)` for `R: Array<T like Idx<n>, RaggedIdx<lens>>`), or keep the dense type.")
      | _ ->
        let tE = { tE with Type = annoTy }
        // A BOUNDED annotation (`-1.0 : Salinity`, Salinity =
        // Float64<psu, min=0.0>) asserts the bound exactly as a `let`
        // annotation does, so it gets the same guards: the value is bound
        // once and checked (BL8001), then yielded. Unbounded ascriptions
        // keep their node untouched.
        let span = if e.Span.StartLine > 0 then e.Span else tE.Span
        let ascName = $"__asc{env.Builder.FreshId()}"
        let ascId = env.Builder.FreshId()
        let ascEnv = bindVarSimple ascName ascId annoTy env
        let subject = match tyAnno with TyNamed (n, []) -> $": {n}" | _ -> "an ascription"
        synthesizeBoundChecks ascEnv (Some tyAnno) subject { Kind = ExprKind.ExprVar ascName; Span = span }
        |> Result.map (fun checks ->
            if checks.IsEmpty then tE
            else
                let tb : TypedBinding = {
                    Name = ascName; VarId = ascId; Type = annoTy
                    Identity = None; IsMutable = false; IsLetMut = false; Value = tE
                    SubBindings = []; Destructure = DSPositional; PostChecks = []; ErasedType = None }
                mkTypedSpan (TExprBlock (TStmtLet tb :: (checks |> List.map TStmtExpr),
                                         Some (mkTypedSpan (TExprVar (ascName, ascId, None)) annoTy span)))
                            annoTy span))

// ---- Assignment ----
and inferAssignExpr (env: TypeEnv) (lhs: Expr) (rhs: Expr) : TypeResult<TypedExpr> =
    inferExpr env lhs |> Result.bind (fun tL ->
    // Bidirectional: check the RHS against the target's type so literals
    // adapt (Int64 literal into an Int32 field) as in every other
    // checked position.
    checkExpr env tL.Type rhs |> Result.bind (fun tR ->
        // Check assignability of LHS (whole-array rebinds and writes
        // through an index/field included -- see assignTargetError).
        let assignErr = assignTargetError env tL
        match assignErr with
        | Some e ->
            // The refusal is about the TARGET: name it, not the last leaf
            // of the value just checked (deep review #48).
            if tL.Span.StartLine > 0 then setCurrentExprSpan tL.Span
            Error e
        | None ->
            match unify env.Subst tL.Type tR.Type with
            | Ok () ->
                let tAssign = mkTyped (TExprAssign (tL, tR)) IRTUnit
                // Constrained-struct target: inline the guard after the
                // store (whole-struct stores and field mutations alike).
                structChecksForAssign env lhs tL |> Result.map (fun checks ->
                    if checks.IsEmpty then tAssign
                    else mkTyped (TExprBlock (TStmtExpr tAssign :: (checks |> List.map TStmtExpr), None)) IRTUnit)
            | Error _ -> Error (TypeMismatch (tL.Type, tR.Type))))

// ---- REDUCTION JOINS (docs/plan-reduction-joins.md) ------------------------
//
// `<&!>` is the DECLARED join surface. It already joined `<@>` maps; these
// three functions make the REDUCTION primitives valid legs, in two spellings:
//
//   Form 1 (pack)   let a, b = object_for(<&!>) <@> (prodsum(s, c), reduce(x, (+)))
//   Form 2 (fold)   let ps = [prodsum(s, c), reduce(x, (+))]
//                   let a, b = reduce(ps, (<&!>))
//
// Both elaborate to ONE node -- the fused reduction terminal `<&!>` maps
// already use (`TExprReduce` over a fusion tree, lowered to `IRReduceCompute`)
// -- by NORMALIZING each leg into the (traversal, fold kernel, seed) triple
// that terminal is made of:
//
//   prodsum(x1..xk)      ->  method_for(zip(x1..xk)) <@> lambda(p1..pk) -> p1*..*pk,  (+),  0
//   reduce(<map>, op, i) ->  the map itself,                                          op,   i
//   reduce(<array>, op, i) -> method_for(A) <@> lambda(p) -> p,                       op,   i
//
// so a join is exactly the existing chain with a PER-LEG fold instead of one
// shared fold. That per-leg part is the only new capability: the kernel and
// seed slots carry an `IRTuple` of k kernels / k seeds (the JOIN ENCODING,
// documented at IRReduceCompute), which is what lets `prodsum` (which folds
// `(+)` from 0) join `reduce(x, max, -inf)` in one traversal.
//
// SEEDS ARE WHY THE ENCODING IS NEEDED AT ALL, not just heterogeneous ops:
// `prodsum(a, b)` seeds at 0 and `reduce(x, (+), 10.0)` seeds at 10.0, so even
// an all-(+) join has k distinct seeds.
//
// ONE LEG IS THE IDENTITY. `reduce([r], (<&!>))` is `r` -- a scalar, not a
// `Tuple<1>` (Blade has no 1-tuple), and the SINGLE-leaf terminal keeps every
// specialization the multi-leaf nest declines (BLAS dot dispatch, the chunked
// `omp` fold, the reassociated lane forms). ZERO legs is refused: an empty
// join names no index space and no element type.

/// One leg's normalization: surface leg -> (traversal leaf, fold kernel, init).
/// Purely syntactic apart from ONE probe -- a `reduce` leg's operand is
/// inferred to decide whether it is already a deferred map (use it as the
/// leaf) or a materialized array (wrap it in the identity map). The probe's
/// typed result is discarded; only the classification is kept.
and joinLegSurface (env: TypeEnv) (idx: int) (leg: Expr) : Result<Expr * Expr * Expr option, TypeError> =
    let sp = leg.Span
    let v n = mkExpr sp (ExprKind.ExprVar n)
    let par i = { Name = $"__jl{idx}_{i}"; Type = None; Default = None; NameSpan = noSpan }
    let mapOver (src: Expr) (parms: LambdaParam list) (body: Expr) =
        mkExpr sp (ExprKind.ExprBinOp (Elementwise, OpApply,
                                       mkExpr sp (ExprKind.ExprMethodFor [src]),
                                       mkExpr sp (ExprKind.ExprLambda (parms, None, body))))
    match leg.Kind with
    | ExprKind.ExprApp ({ Kind = ExprKind.ExprVar "prodsum" }, args)
            when not args.IsEmpty && (lookupVar "prodsum" env).IsNone ->
        // sum_t prod_L x_L(t): the product is the map, `(+)` from 0 is the
        // fold. Co-iteration (zip), never the outer product -- prodsum walks
        // ONE index space, and `method_for(a, b)` would walk |a|*|b|.
        let parms = args |> List.mapi (fun i _ -> par i)
        let body =
            parms |> List.map (fun p -> v p.Name)
                  |> List.reduce (fun a b -> mkExpr sp (ExprKind.ExprBinOp (Elementwise, OpMul, a, b)))
        let src = match args with [one] -> one | many -> mkExpr sp (ExprKind.ExprZip many)
        Ok (mapOver src parms body, mkExpr sp (ExprKind.ExprSection OpAdd), None)
    | ExprKind.ExprReduce (_, _, _, Some _) ->
        Error (ReductionFormErr "a reduction-join leg cannot carry `axes = n`: a join folds every leg to a SCALAR over one shared traversal, and a partial fold answers an array. Force the partial fold with `|> compute` and join over its result, or drop the `axes` clause.")
    | ExprKind.ExprReduce (arr, kernel, init, None) ->
        inferExpr env arr |> Result.map (fun tA ->
            match (resolveTypedExpr env tA).Kind with
            // Already a deferred traversal: it IS the leaf.
            | TExprApply info when not info.IsComposeApply -> (arr, kernel, init)
            // A materialized array (or anything else the fold accepts): the
            // identity map over it reads the same cells in the same order.
            | _ -> (mapOver arr [par 0] (v (par 0).Name), kernel, init))
    | _ ->
        Error (ReductionFormErr "a reduction-join leg must be a REDUCTION: `prodsum(...)` or `reduce(...)`. `<&!>` joins the traversals of reductions into one loop; an expression that is not a reduction has no accumulator to join.")

/// Form 2's leg list: an array literal written inline, or a name bound to one
/// (recovered from `JoinLegLists`, the surface side channel). Anything else
/// answers None and the caller refuses.
/// Is this surface expression written as a REDUCTION? The dispatch predicate
/// for both join forms, and deliberately syntactic.
///
/// `<&!>` over a pack is OVERLOADED, and has been since before joins existed:
/// `object_for(<&!>) <@> (c1, c2, c3)` over deferred MAPS is n-ary map fusion
/// answering k ARRAYS (`tests/corpus/loops/029`), while the same shape over
/// REDUCTIONS is a join answering k scalars. Both readings are right; the LEGS
/// say which. So the leading leg decides, and a pack that does not start with a
/// reduction falls through to the map-fusion path untouched.
///
/// Leading-leg (not every-leg), so a genuine leg list with a bad element in the
/// middle still reaches `inferReductionJoin`, which names the offending leg,
/// instead of silently becoming a map fusion that fails somewhere else.
and isJoinLegShape (e: Expr) : bool =
    match e.Kind with
    | ExprKind.ExprReduce _ -> true
    | ExprKind.ExprApp ({ Kind = ExprKind.ExprVar "prodsum" }, args) -> not args.IsEmpty
    | _ -> false

and joinLegListOf (env: TypeEnv) (arr: Expr) : Expr list option =
    // Data arrays are array literals too (`let a = [1.0, 2.0, 3.0]`), and
    // `reduce(a, (<&!>))` over one of those is a different mistake deserving a
    // different message.
    let legShaped = isJoinLegShape
    let elemsOf =
        match arr.Kind with
        | ExprKind.ExprArrayLit elems -> Some elems
        | ExprKind.ExprVar name ->
            (match env.JoinLegLists.TryGetValue name with
             | true, elems -> Some elems
             | _ -> None)
        | _ -> None
    match elemsOf with
    | Some (first :: _ as elems) when legShaped first -> Some elems
    | _ -> None

/// The join itself. `legs` are the SURFACE legs, in traversal order; the
/// result is a scalar at one leg and a left-nested tuple of k scalars beyond
/// (the fusion terminal's own convention, so destructuring projects flat).
and inferReductionJoin (env: TypeEnv) (legs: Expr list) (site: Expr) : TypeResult<TypedExpr> =
    let sp = site.Span
    if legs.IsEmpty then
        Error (ReductionFormErr "a reduction join needs at least one leg: `object_for(<&!>) <@> (r1, r2, ...)` (or `reduce([r1, r2, ...], (<&!>))`) joins reductions over one traversal, and an empty join names neither an index space nor an element type.")
    else
    legs |> List.mapi (joinLegSurface env) |> sequenceResults |> Result.bind (fun parts ->
    parts |> List.map (fun (leaf, _, _) -> inferExpr env leaf) |> sequenceResults
    |> Result.bind (fun tLeaves0 ->
    // RESOLVED leaves for the SHAPE judgments below, like the `<&!>` chain.
    // The leaf SPLICED into the typed node is the resolved apply too -- with
    // one exception, the join's sharing declaration read from the leg's side:
    // a leg written `reduce(<name>, op)` over a NAMED deferred map keeps the
    // NAME as its leaf when the join has several legs. Splicing a copy of the
    // apply there erased the identity between this leg's traversal and the
    // operand slot another leg reads (`prodsum(<name>, v)`), so the emitter
    // declared the per-iteration share for the slot and this leg re-spelled
    // the producer beside it (docs/plans/structural/03, defect D2). Every
    // consumer of the node resolves a variable leaf through the deferred
    // table it already keeps (codegen's `DeferredComputations`, the
    // interpreter's `VDeferred`, the device and LLVM producers), and lowering
    // counts the leaf as a join reference so S2 leaves the binding deferred
    // (`joinDeferrableIdsMany`). A CAPTURED binding keeps the copy: codegen
    // materializes a deferred capture at the boundary that forwards it, so
    // the name would no longer resolve to an unforced apply in the body.
    let tResolved = tLeaves0 |> List.map (resolveTypedExpr env)
    let tLeaves =
        if legs.Length < 2 then tResolved
        else
            List.map2 (fun (t0: TypedExpr) (r: TypedExpr) ->
                match t0.Kind, r.Kind with
                | TExprVar (name, _, _), TExprApply info
                        when not info.IsComposeApply && not (capturedOuterBinding name env) -> t0
                | _ -> r) tLeaves0 tResolved
    let leafShape (i: int) (t: TypedExpr) : Result<IRType * IRIndexType list, TypeError> =
        match t.Kind with
        | TExprApply info when not info.IsComposeApply ->
            (match env.Subst.Resolve info.OutputType with
             | ArrayElem arr ->
                let packed =
                    arr.IndexTypes |> List.exists (fun ix -> not ix.Symmetry.IsSymNone)
                if packed then
                    Error (CompactStorageErr $"reduction-join leg {i + 1} traverses compact symmetric/antisymmetric/Hermitian storage: folding the canonical cells and folding the logical (mirrored) cells differ. Force with `|> compute` and `decompact(A, d)` first for the logical fold.")
                else Ok (arr.ElemType, arr.IndexTypes)
             | _ ->
                Error (ReductionFormErr $"reduction-join leg {i + 1} does not traverse an index space -- a join leg needs an array-producing kernel application to fold over."))
        | TExprApply _ ->
            Error (NotYetSupportedErr $"reduction-join leg {i + 1} reduces a COMPOSED (>>@/@>>) application, which is not supported yet -- force it with `|> compute` and join over the resulting array.")
        | _ ->
            Error (ReductionFormErr $"reduction-join leg {i + 1} could not be resolved to a traversal -- a join leg must be `prodsum(...)` or `reduce(<computation or array>, op)`.")
    tResolved |> List.mapi leafShape |> sequenceResults |> Result.bind (fun shapes ->
    // ---- JOINT INDEX SPACE -------------------------------------------------
    // A join emits ONE loop nest, so every leg must walk the SAME cell grid:
    // equal rank, and equal extents wherever both are statically known. Only
    // a provable disagreement is an error -- unknown extents are trusted and
    // the nest takes its bound from the first leg, exactly the rule `prodsum`
    // has always applied to its own operands.
    let (elem0, ix0) = List.head shapes
    let spaceErr =
        shapes |> List.mapi (fun i s -> (i, s)) |> List.tryPick (fun (i, (_, ixs)) ->
            if ixs.Length <> ix0.Length then
                Some (ReductionFormErr $"reduction-join legs do not share an index space: leg 1 traverses a rank-{ix0.Length} space and leg {i + 1} traverses a rank-{ixs.Length} one. Every leg of a join folds the SAME cell grid.")
            else
                List.zip ix0 ixs |> List.mapi (fun d (a, b) -> (d, a, b)) |> List.tryPick (fun (d, a, b) ->
                    match tryEvalIntIR a.Extent, tryEvalIntIR b.Extent with
                    | Some na, Some nb when na <> nb ->
                        Some (ReductionFormErr $"reduction-join legs do not share an index space: axis {d} has extent {na} in leg 1 and {nb} in leg {i + 1}. Every leg of a join folds the SAME cell grid.")
                    | _ -> None))
    match spaceErr with
    | Some e -> Error e
    | None ->
    parts |> List.map (fun (_, k, _) -> inferExpr env k) |> sequenceResults |> Result.bind (fun tKernels ->
    parts
    |> List.mapi (fun leg (_, _, i) ->
        match i with
        // Each seed has its own leg's context, just as in an ordinary fold.
        // This rebuilds literal-only arithmetic (including -0.0) at that
        // width while fixed values and explicit casts still have to match.
        | Some e -> checkExpr env (fst shapes.[leg]) e |> Result.map Some
        | None -> Ok None)
    |> sequenceResults |> Result.bind (fun tInits ->
    // Per leg: the fold kernel's params and the seed live in that leg's OWN
    // element type (the shared-fold terminal unifies them all with one; a join
    // must not, or a complex leg would drag a real one complex).
    let seedFor (i: int) : Result<TypedExpr, TypeError> =
        let (elem, _) = shapes.[i]
        let tK = tKernels.[i]
        (match env.Subst.Resolve tK.Type with
         | FuncElem (paramTys, _) ->
            paramTys |> List.fold (fun acc pTy -> acc |> Result.bind (fun () -> unify env.Subst pTy elem)) (Ok ())
         | _ -> Ok ())
        |> Result.map (fun () -> settleOpenProjections env tK)
        |> Result.bind (fun () ->
        match tInits.[i] with
        | Some tInit -> unify env.Subst tInit.Type elem |> Result.map (fun () -> tInit)
        | None ->
            let et = match env.Subst.Resolve elem with AnyPrimElem e -> e | _ -> ETFloat64
            let lit () = match et with
                         | ETInt32 | ETInt64 -> TExprLit (LitInt 0L)
                         | _ -> TExprLit (LitFloat 0.0)
            let one () = match et with
                         | ETInt32 | ETInt64 -> TExprLit (LitInt 1L)
                         | _ -> TExprLit (LitFloat 1.0)
            // An unseeded `reduce(x, (+))` leg is UNSEEDED (marked, gap G74);
            // a `prodsum` leg is the sum FROM ZERO, so its synthesized zero is
            // a real seed and stays unmarked (the condition is joinLegSurface's
            // prodsum arm).
            let isProdsumLeg =
                match legs.[i].Kind with
                | ExprKind.ExprApp ({ Kind = ExprKind.ExprVar "prodsum" }, args) ->
                    not args.IsEmpty && (lookupVar "prodsum" env).IsNone
                | _ -> false
            let markAdd (s: TypedExpr) = if isProdsumLeg then s else markUnseeded s
            match (resolveTypedExpr env tK).Kind with
            | TExprSection OpAdd when isOpenElemType env elem -> Ok (markAdd (mkTyped TExprZero elem))
            | TExprSection OpAdd -> Ok (markAdd (mkTyped (lit ()) elem))
            | TExprSection OpMul -> Ok (mkTyped (one ()) elem)
            | TExprSection _ ->
                Error (ReductionFormErr $"reduction-join leg {i + 1} needs an explicit init (`reduce(x, op, init)`): only (+) and (*) carry implicit identities, and a joined fold cannot seed from its first element.")
            | _ ->
                Error (ReductionFormErr $"reduction-join leg {i + 1} needs an explicit init (`reduce(x, op, init)`) for a lambda or named fold kernel -- a joined fold cannot seed from its first element."))
    List.init legs.Length seedFor |> sequenceResults |> Result.map (fun seeds ->
    match tLeaves with
    | [one] ->
        // 1 leg = the identity: the ordinary single-leaf fused terminal.
        mkTyped (TExprReduce (one, List.head tKernels, Some (List.head seeds))) elem0
    | first :: rest ->
        let tree =
            rest |> List.fold (fun acc leaf ->
                mkTyped (TExprFusion (acc, leaf)) (IRTTuple [acc.Type; leaf.Type])) first
        // FLAT `Tuple<k>`, mirroring the flat `make_tuple` of k accumulators
        // the join emits (see typeOf IRReduceCompute). The `<&!>` CHAIN nests
        // its type because the operator is binary; a join is k-ary.
        let resultType = IRTTuple (shapes |> List.map fst)
        // The JOIN ENCODING: k kernels and k seeds, in leaf order.
        let kCarrier = mkTyped (TExprTuple tKernels) (IRTTuple (tKernels |> List.map (_.Type)))
        let sCarrier = mkTyped (TExprTuple seeds) (IRTTuple (seeds |> List.map (_.Type)))
        mkTyped (TExprReduce (tree, kCarrier, Some sCarrier)) resultType
    | [] -> mkTyped (TExprReduce (List.head tLeaves, List.head tKernels, Some (List.head seeds))) elem0))))))

/// An already-TYPED array of integer EnumIdx values, observed: the typed
/// `Int64(a)` (each cell's key). A variable is re-spelled by name when the
/// name still binds it; any other expression is bound once to a fresh
/// block-local first, so it is evaluated exactly where it was. Used by the
/// call judgment (an array of keys handed to a generic parameter its callee
/// observes) through TypeCheckSupport.enumKeyArgHook.
and enumKeyArgTyped (env: TypeEnv) (a: TypedExpr) : TypeResult<TypedExpr> =
    match a.Kind with
    | TExprVar (name, vid, _) when (match lookupVar name env with Some vi -> vi.VarId = vid | None -> false) ->
        inferExpr env (enumKeyCastAst (mkExpr a.Span (ExprVar name)))
    | _ ->
        let vid = env.Builder.FreshId()
        let name = $"__ekarg{vid}"
        let env' = bindVarSimple name vid a.Type env
        inferExpr env' (enumKeyCastAst (mkExpr a.Span (ExprVar name)))
        |> Result.map (fun tMap ->
            let tb : TypedBinding = {
                Name = name; VarId = vid; Type = a.Type
                Identity = None; IsMutable = false; IsLetMut = false; Value = a
                SubBindings = []; Destructure = DSPositional; PostChecks = []; ErasedType = None }
            mkTypedSpan (TExprBlock ([ TStmtLet tb ], Some tMap)) tMap.Type a.Span)

and inferReduceGeneral (env: TypeEnv) (array: Expr) (kernel: Expr) (init: Expr option) (axisCountOpt: int option) : TypeResult<TypedExpr> =
    inferExpr env array |> Result.bind (fun tArr ->
    // G114: the fold's ELEMENT parameter (the second; the first is the
    // accumulator, whose width the seed may set) is hinted by the operand's
    // narrow element, so its literal partners take that width
    // (`reduce(a32, lambda(x, y) -> x + y * 2.0)` folds Float32).
    let kernelTyped =
        let hinted (v: int) =
            match env.Subst.GetElemHint v with
            | Some (ETFloat32 | ETInt32 | ETComplex64 as et) -> Some et
            | _ -> None
        // G172 (a): the operand's element, narrow: a concrete narrow element;
        // an open element carrying a hint; or an operand that is itself a
        // still-open hinted kernel parameter (a ROW of a narrow operand inside
        // a row kernel: its hint is that operand's element)
        let elemNarrow =
            match IR.stripUnits (env.Subst.Resolve tArr.Type) with
            | ArrayElem at ->
                (match IR.stripUnits (env.Subst.Resolve at.ElemType) with
                 | IRTScalar (ETFloat32 | ETInt32 | ETComplex64 as et) -> Some et
                 | IRTInfer v -> hinted v
                 | _ -> None)
            | IRTInfer v -> hinted v
            | _ -> None
        match kernel.Kind with
        | ExprKind.ExprLambda ([ p0; p1 ] as parms, wc, body) when p1.Type.IsNone ->
            let narrow = elemNarrow
            // the ACCUMULATOR takes the element width too when nothing else
            // can set it: no seed, or a literal seed (which adapts); a typed
            // seed keeps its own width (and its refusals)
            let accHint =
                match init with
                | None -> narrow
                // (a literal-only seed reaches here ascribed at the element by the
                // ROUND-6 seed rule: `ExprTyped (literal, <element>)`)
                | Some ie when p0.Type.IsNone
                               && ((surfaceLiteralClass ie).IsSome
                                   || (match ie.Kind with ExprKind.ExprTyped (l, _) -> (surfaceLiteralClass l).IsSome | _ -> false)) -> narrow
                | _ -> None
            (match narrow with
             | Some et ->
                 if kernel.Span.StartLine > 0 then setCurrentExprSpan kernel.Span
                 inferLambdaHinted env parms wc body [] [ accHint; Some et ]
                 |> Result.map (fun te ->
                     if te.Span.StartLine = 0 && kernel.Span.StartLine > 0 then { te with Span = kernel.Span } else te)
             | _ -> inferExpr env kernel)
        | _ -> inferExpr env kernel
    kernelTyped |> Result.bind (fun tKernelDecl ->
    // A declared GENERIC function as the fold kernel (`reduce(xs, add)`) is a
    // USE, typed at an instantiated copy of its signature exactly as a call or
    // a `<@>` kernel is: unifying the kernel's parameters with the element
    // below must fix THIS use's instance, not bind the declaration's own
    // variables (which refused every later use at another element type --
    // `reduce([1.5, 2.5], add)` after `reduce([1, 2, 3], add)` -- and fixed
    // `add` for its callers too). IRMono.hmValueRefRewrite specializes the
    // kernel reference at the instance; its generic obligations (casts) are
    // judged against it once the fold has fixed it.
    let tKernel = instantiateGenericValueUse env tKernelDecl
    let judgeKernelUse (r: TypeResult<TypedExpr>) : TypeResult<TypedExpr> =
        match r with
        | Ok _ when not (obj.ReferenceEquals (tKernelDecl, tKernel)) ->
            (match genericValueObligationClash env tKernelDecl (env.Subst.Resolve tKernel.Type) with
             | Some e -> Error e
             | None -> r)
        | _ -> r
    (match init with
     | Some e -> inferExpr env e |> Result.map Some
     | None -> Ok None) |> Result.bind (fun tInitOpt ->
        // Round 6: a literal-only SEED takes the kernel accumulator's width,
        // else the operand element's (`reduce(xs64, f, 0.0)` seeds a Float64).
        let tInitOpt =
            tInitOpt |> Option.map (fun ti ->
                let acc =
                    match env.Subst.Resolve tKernel.Type with
                    | FuncElem (p0 :: _, _) -> [ p0 ]
                    | _ -> []
                adaptLiteralToFirst env (acc @ [ tArr.Type ]) ti)
        // Parallel-fold reorder licence, checked BEFORE the array/deferred
        // split so both fold shapes refuse an unlicensed `omp` identically.
        checkFoldOmpLicense env tKernel |> Result.bind (fun () ->
        // ---- Fused reduction terminal -------------------------------------
        // reduce over a DEFERRED computation (an unforced `L <@> k`, or an
        // <&!> fusion tree of them, possibly behind one let-hop) folds the
        // kernel over the computation's cells WITHOUT materializing arrays:
        // one loop nest, one scalar accumulator per fusion leaf (a tree
        // yields a tuple of scalars, mirroring `|> compute`'s tuple shape).
        // Semantically this is the fold stage of the loop-object composition
        // algebra -- a binary-kernel object (object_for((+))) composed after
        // the map stages -- typed here at the forcing site. (+)/(*) sections
        // seed with their identity; any other kernel REQUIRES the 3-arg
        // init: a fused nest cannot seed-with-first like the array fold.
        match tryInferReduceCompute env tArr tKernel tInitOpt with
        | Some result -> judgeKernelUse result
        | None ->
        // Drive type inference for unannotated kernel parameters: when the
        // array argument is an unconstrained inference variable (e.g.
        // `lambda(g) -> reduce(g, (+))`), bind it to a fresh rank-1 array.
        // Element type: use the kernel's first param type if concrete
        // (`FuncElem (paramTys,_)` with `paramTys.[0] : IRTScalar et`);
        // otherwise defer to a fresh inference var rather than Float64 --
        // sections like `(+)` always have fresh param vars, and pinning
        // Float64 here would make `reduce(int_array, (+))` fail. Fires only
        // when the array type is genuinely unconstrained.
        let deduceElemFromKernel () : IRType =
            // Unresolved first param: return that SAME var (not a fresh
            // one) so it gets pinned together with the per-row type at
            // buildApplyInfo's unification. Concrete: pin to it directly.
            match env.Subst.Resolve(tKernel.Type) with
            | FuncElem (paramTys, _) when not paramTys.IsEmpty ->
                let resolved = env.Subst.Resolve(paramTys.[0])
                match resolved with
                | IRTScalar _ -> resolved          // annotated: pin to user's type
                | IRTInfer _ -> resolved           // unresolved: defer via the same var
                | _ -> env.Builder.FreshInferType()
            | _ -> env.Builder.FreshInferType()
        (match env.Subst.Resolve(tArr.Type) with
         | IRTInfer arrVid ->
            let elemType = deduceElemFromKernel ()
            // ELEMENT POLYMORPHISM SURVIVES THE SHAPE, as in
            // requireArrayArgMinRank: when the folded operand is a SIGNATURE
            // variable (`reduce(row, (+))` over `row: T^1` in a declaration
            // body), its element is generic too. Without the mark the fold's
            // element was generic only if a declared `-> T^0` return happened
            // to be unified with it (the var-to-var bind copies the mark),
            // so an UNANNOTATED return -- or a cast of the fold -- saw a
            // plain var zonk would default to Float64.
            if env.Subst.IsPolymorphicId arrVid then
                (match env.Subst.Resolve elemType |> IR.stripUnits with
                 | IRTInfer eid -> env.Subst.MarkPolymorphic eid
                 | _ -> ())
            // One axis per FOLDED axis. The default is one (rank-1 -- the
            // shape every existing `lambda(g) -> reduce(g, (+))` receives, so
            // this arm is unchanged for it); an explicit `axes = n` over an
            // operand whose rank is not yet pinned says the operand carries at
            // least n axes, and minting only one would silently fold an
            // n-axis request as a rank-1 one.
            // ...or one per PINNED axis, when the operand is a caret-pinned
            // `T^k` var: minting fewer slots than the pin makes the unify
            // below fail, and its failure is discarded, so the operand stays
            // unresolved and the tail arm blames the caller for not passing an
            // array. (`operandRank` reads the same pin, so a partial fold has
            // already been rewritten away by here; this is the full fold and
            // the paths that decline one.)
            let pinnedRank =
                match env.Subst.Resolve(tArr.Type) with
                | IRTInfer vid ->
                    (match env.Subst.GetArityConstraint vid with
                     | Some k when k > 0 -> k
                     | _ -> 0)
                | _ -> 0
            let nSlots = max (max 1 (defaultArg axisCountOpt 1)) pinnedRank
            let freshIdxs =
                // The name carries the record's own id, as requireArrayArg's
                // mints do: shape monomorphization bakes extents BY NAME, so
                // one fixed `__inferred_n` shared by every fold in a program
                // made unrelated axes one identity.
                [ for _ in 1 .. nSlots ->
                    let ixId = env.Builder.FreshId()
                    { Id = ixId
                      Rank = 1
                      Extent = IRParam ($"__reduce_inferred_n_{ixId}", 0, IRTNat None)
                      Symmetry = SymNone
                      Tag = None; IxKind = IxKPlain
                      Kind = SDimension
                      Dependencies = [] } ]
            let freshArr = mkArrayArrow freshIdxs elemType None
            unify env.Subst tArr.Type freshArr |> ignore
         | _ -> ())
        match env.Subst.Resolve(tArr.Type) with
        // A wreath axis reaches the CANONICAL-VS-LOGICAL refusal, in parity
        // with depth-1 compact storage one arm down: `decompact(W, 0)` does
        // produce the dense tensor this fold wants, so the message names it
        // instead of saying the class cannot be touched. Still absent: the
        // orbit MULTIPLICITIES that would let a pool walk stand in for dense.
        | ArrayElem arrTy when arrTy.IndexTypes |> List.exists (fun ix -> ix.Symmetry = SymWreath) ->
            let ix = arrTy.IndexTypes |> List.find (fun ix -> ix.Symmetry = SymWreath)
            Error (OrbitFoldUnsupported (ppOrbitLevels (orbitLevelsOf ix), "reduce"))
        | ArrayElem arrTy when arrTy.IndexTypes.Length = 1
                               && (match arrTy.IndexTypes.[0].Symmetry with
                                   | SymSymmetric | SymAntisymmetric | SymHermitian -> true
                                   // Unreachable: the wreath arm above ran first.
                                   // Spelled out so FS0025 keeps auditing this
                                   // match rather than a wildcard swallowing it.
                                   | SymWreath -> true
                                   | SymNone -> false) ->
            // A single SYMMETRY-CLASS record passes the one-record check but
            // is NOT a rank-1 axis -- reduce would walk extents[0] handing
            // out row pointers (compiled garbage). Also ambiguous (canonical
            // vs logical cells). Reject until a semantics is chosen.
            Error (CompactStorageErr "reduce() over compact symmetric/antisymmetric/Hermitian storage is not supported: folding the canonical cells and folding the logical (mirrored) cells differ. decompact(A, d) first for the logical fold.")
        | ArrayElem arrTy when arrTy.IndexTypes.Length = 1 ->
            // Static guarantee: reject if we can prove the extent is 0 AND no
            // init was supplied. With an init, the empty fold is defined (it
            // is simply init), so statically-empty inputs are legal.
            match tryEvalIntIR arrTy.IndexTypes.[0].Extent with
            | Some n when n <= 0L && tInitOpt.IsNone ->
                Error (ReduceEmptyArray n)
            | _ ->
                // Unify the kernel's param types with the array's element
                // type: the kernel's `(alpha, beta) -> gamma` arrow typically has
                // unresolved alpha, beta (sections lower polymorphic; unannotated
                // 2-arg lambdas don't get params pinned by the body alone).
                // Without this, zonking defaults IRTInfer to Float64, baking
                // a literal-Float64 signature into the lifted function and
                // breaking reduces over non-Float64 arrays. Result type
                // stays whatever the kernel declared (Bool for comparisons,
                // elemType for arithmetic).
                let kernelUnify =
                    match env.Subst.Resolve(tKernel.Type) with
                    | FuncElem (paramTys, _) ->
                        paramTys |> List.fold (fun acc pTy ->
                            acc |> Result.bind (fun () -> unify env.Subst pTy arrTy.ElemType))
                            (Ok ())
                    | _ -> Ok ()
                // The init seeds the accumulator, so it must share the
                // element type (same unification the kernel params get).
                let initUnify =
                    match tInitOpt with
                    | Some tInit -> unify env.Subst tInit.Type arrTy.ElemType
                    | None -> Ok ()
                kernelUnify |> Result.bind (fun () ->
                // (F2: the kernel's field reads / tuple projections of its
                // now-known parameters take their types: `lambda(a, b) ->
                // (a[0] + b[0], b[1])` over an array of tuples)
                settleOpenProjections env tKernel
                initUnify |> Result.bind (fun () ->
                reduceKernelUnitCheck env tKernel arrTy.ElemType |> Result.bind (fun () ->
                    // arrTy.ElemType is IRType post-B2; return directly.
                    // Sound only because the check above established that the
                    // kernel preserves the element's unit signature.
                    let resultType = arrTy.ElemType
                    judgeKernelUse (Ok (mkTyped (TExprReduce (tArr, tKernel, tInitOpt)) resultType)))))
        | ArrayElem at ->
            // Reached only by a FULL fold (n = rank) that the rank-k nest
            // above declined -- non-dense axes, non-static extents, or a
            // non-scalar element. A partial fold (n < rank) never lands here:
            // it was rewritten into the row-mode apply before this point.
            Error (ReductionFormErr (sprintf "reduce: the full fold (`axes = %d`) over a rank-%d operand needs dense (plain, non-compact) axes with static extents and scalar elements; fold one axis at a time instead (`reduce(A, op)` folds the innermost axis into a rank-%d result)"
                                  at.IndexTypes.Length at.IndexTypes.Length (at.IndexTypes.Length - 1)))
        | _ ->
            Error (ReductionFormErr "reduce() requires an array as first argument")))))

// sort(array, key) -- stable sort by ascending key. Eager materialization:
// result is a new physical array with the same element type and extent as
// the input, indexed by a fresh anonymous index (mask-style). The order
// property is NOT tracked in the type system -- a future "key map chain"
// subsystem could make sort lazy (a chain handle recording (key_fn,
// permutation), materialized on first access) to preserve optimization
// headroom for sort-skip, merge-style joins, etc.


/// segments(A): the STRUCTURAL grouping of the `Chunked` axis A
/// (docs/plans/structural/07 §2.2). A GroupKeys over A's own record whose
/// partition is the segment table -- no key array, no CSR, identity
/// permutation. The outer slot is `Idx<G>` (or the file-label `EnumIdx` of a
/// file-segmented axis) and carries the `__segments|A` tag, which is how
/// `inferGroupBy` recognizes a structural grouping and records the alias for
/// `ungroup`. Name-keyed like every grouping (sql.md §7): bind it with `let`.
and inferSegments (env: TypeEnv) (alias: string) : TypeResult<TypedExpr> =
    match Map.tryFind alias env.Segmentations with
    | None ->
        (match Map.tryFind alias env.TypeDefs with
         | Some (TDIIndexType _) ->
             Error (SegmentedDomainErr $"segments({alias}): '{alias}' is an index type but not a `Chunked<..>` one; declare `type {alias}2 = Chunked<{alias}, edge>` and take segments of that")
         | _ -> Error (SegmentedDomainErr $"segments({alias}): '{alias}' is not a `Chunked<..>` index alias in scope"))
    | Some seg ->
        let offsets = segmentationOffsets seg
        let labels = segmentationLabels seg
        let g = int64 (offsets.Length - 1)
        let outer = {
            Id = env.Builder.FreshId(); Rank = 1
            Extent = IRLit (IRLitInt g)
            Symmetry = SymNone; Tag = Some ("__segments|" + alias); IxKind = IxKPlain
            Kind = SDimension; Dependencies = []
        }
        let enumValues = labels |> Option.map (List.map EVString)
        let gkType = IRTGroupKeys (outer, seg.Source, enumValues)
        Ok (mkTyped (TExprSegments (alias, offsets, labels)) gkType)

/// segments(C0, C1): the PRODUCT grouping of two segmented slots (docs/
/// plans/structural/07 §4.1b, the multi-dimensional case in the fixed slot
/// order of decisions D8/D10): one group per tile (g0, g1), row-major over
/// the tile grid, the member the tile's cells row-major. Both slots are
/// single-slot segmentations, so every step's table depends only on its
/// own slot and the cross-slot condition of §4.0 holds by construction;
/// a two-dimensional mosaic of stores (where it could fail) has no
/// declaration form yet and is refused where it would arise.
and inferSegmentsGrid (env: TypeEnv) (aliases: string list) : TypeResult<TypedExpr> =
    let segs = aliases |> List.map (fun a -> Map.tryFind a env.Segmentations)
    match segs with
    | [ Some s0; Some s1 ] ->
        if (segmentationLabels s0).IsSome || (segmentationLabels s1).IsSome then
            Error (SegmentedDomainErr "segments(C0, C1): a tile grouping over a FILE-tiled axis (a two-dimensional store mosaic) has no declaration form yet; both slots must be regular or store-inherited grids")
        else
        let b0 = segmentationOffsets s0
        let b1 = segmentationOffsets s1
        let g = int64 ((b0.Length - 1) * (b1.Length - 1))
        let outer = {
            Id = env.Builder.FreshId(); Rank = 1
            Extent = IRLit (IRLitInt g)
            Symmetry = SymNone; Tag = Some ("__segmentsgrid|" + String.concat "," aliases); IxKind = IxKPlain
            Kind = SDimension; Dependencies = []
        }
        let gkType = IRTGroupKeys (outer, s0.Source, None)
        Ok (mkTyped (TExprSegmentsGrid (aliases, [ b0; b1 ])) gkType)
    | _ ->
        let missing = List.zip aliases segs |> List.filter (fun (_, s) -> s.IsNone) |> List.map fst
        Error (SegmentedDomainErr $"""segments({String.concat ", " aliases}): {String.concat ", " missing} is not a `Chunked<..>` index alias in scope""")

/// files(A): the FILE-level grouping of a file-segmented axis (§2.7): one
/// run per store, in declaration order, the outer slot labelled by the
/// store names (the `EnumIdx` states of decision D3, carried as the
/// grouping's enum values). Same structural machinery as `segments(A)`,
/// which for such an axis is the innermost (per-file chunk) level.
and inferFiles (env: TypeEnv) (alias: string) : TypeResult<TypedExpr> =
    match Map.tryFind alias env.Segmentations with
    | None -> Error (SegmentedDomainErr $"files({alias}): '{alias}' is not a `Chunked<..>` index alias in scope")
    | Some seg ->
        match segmentationFileOffsets seg, segmentationLabels seg with
        | Some offsets, Some labels ->
            let f = int64 (offsets.Length - 1)
            let outer = {
                Id = env.Builder.FreshId(); Rank = 1
                Extent = IRLit (IRLitInt f)
                Symmetry = SymNone; Tag = Some ("__segments|" + alias); IxKind = IxKPlain
                Kind = SDimension; Dependencies = []
            }
            let gkType = IRTGroupKeys (outer, seg.Source, Some (labels |> List.map EVString))
            Ok (mkTyped (TExprSegments (alias, offsets, Some labels)) gkType)
        | _ -> Error (SegmentedDomainErr $"files({alias}): '{alias}' has no file level; it is a regular chunk grid -- use segments({alias})")

// group_by(values, grouping) -- apply GroupKeys to a values array
// Result: rank-2 array (groups x members), with GroupIdx
// Tags ("__group_outer", "__group_member") signal to codegen to use ragged peel.


// ---- Reynolds ----


and inferTupleIndex (env: TypeEnv) tuple index : TypeResult<TypedExpr> =
    inferExpr env tuple |> Result.bind (fun tT ->
    inferExpr env index |> Result.bind (fun tI ->
        match env.Subst.Resolve(tT.Type) with
        | ArrayElem arrTy ->
            // One bracket = one index dimension. Mirrors ExprApp's
            // tArgs.Length <= arrTy.IndexTypes.Length check.
            let identity = match tT.Kind with TExprVar (_, _, id) -> id | _ -> None
            // The whole subscript judgment on the single index -- the SAME
            // function as the call spelling (class, literal range, nominal
            // tag, synthesized-buffer suppression), so the one-bracket
            // spelling cannot drift from `A(i)`.
            match (if arrTy.IndexTypes.IsEmpty then Ok () else checkArrayIndexTags env tT arrTy [tI]) with
            | Error err -> Error err
            | Ok () ->
                if 1 = arrTy.IndexTypes.Length then
                    Ok (mkTyped (TExprIndex (tT, [tI], identity)) arrTy.ElemType)
                elif 1 < arrTy.IndexTypes.Length then
                    let remaining = arrTy.IndexTypes |> List.skip 1
                    Ok (mkTyped (TExprIndex (tT, [tI], identity))
                                (mkArrayLike { arrTy with IndexTypes = remaining }))
                else
                    Error (ArgCountErr "array indexing: too many indices for array rank")
        | IRTTuple ts when (match tI.Kind with TExprLit (LitInt k) -> k >= 0L && int k < ts.Length | _ -> false) ->
            // A LITERAL projection of a tuple whose element types are known
            // names that element's type. Answering a fresh variable here
            // (the pack fallback below) left `s[2] / s[1]` in a row kernel
            // untyped at the apply's output unification, and the emitted row
            // map took the loop-index tag as its element type -- g++ then
            // refused the store under -Werror=float-conversion
            // (docs/plans/structural/03, defect D1). The pack case keeps the
            // fallback: its leaves are flat paths codegen resolves itself.
            let k = match tI.Kind with TExprLit (LitInt k) -> int k | _ -> 0
            Ok (mkTyped (TExprTupleIndex (tT, tI)) ts.[k])
        | IRTPoly (baseTy, _)
            when (match tI.Kind with TExprLit (LitInt _) -> false | _ -> true)
                 && (match env.Subst.Resolve baseTy with IRTTuple _ -> false | _ -> true) ->
            // A DYNAMIC read of a homogeneous pack -- `args[k]` under a former
            // over `range<Idx<arity(args)>>` -- is one of the pack's elements,
            // whichever `k` turns out to be, so it has the pack's element
            // type. The fresh variable of the fallback below is the same
            // defect the tuple arm above closes: nothing in `lambda(k) ->
            // args[k]` ever constrains it, the map's element type then
            // defaults to the LOOP INDEX's, and the body pins `T := Int64`
            // for every caller. `polySum(1.5, 2.5)` typed as Int64 -- 3 under
            // the interpreter, a g++ float-conversion refusal compiled -- and
            // two calls at different element types shared one instantiation.
            //
            // Only the dynamic index, and only a non-tuple element: a literal
            // index (and a pack of tuples) addresses FLAT LEAVES, which the
            // fallback leaves for codegen to resolve by path.
            Ok (mkTyped (TExprTupleIndex (tT, tI)) baseTy)
        // A LITERAL read of a pack whose element is an ARRAY or a SCALAR --
        // `P[0]` over `P: Poly<T^1>` -- is that element, too: there are no
        // flat leaves to resolve by path when the element is not a tuple, and
        // a `T^k` element (k >= 1) never becomes one (the caret pins it to a
        // rank-k array). The fresh variable of the fallback cut `P[0]` off
        // from `T`, so `reduce(P[0], (+))` returning `T^0` left the call's
        // result unresolved (BL6001) and `P[0](0)` was typed as a CALL
        // (BL9002 on `P_0(0L)`). An open element variable WITHOUT a rank pin
        // may still turn out to be a tuple, and keeps the fallback.
        | IRTPoly (baseTy, _)
            when (match env.Subst.Resolve baseTy with
                  | ArrayElem _ | AnyPrimElem _ -> true
                  | IRTInfer vid | IRTUnitAnnotated (IRTInfer vid, _) ->
                      env.Subst.GetArityConstraint vid |> Option.exists (fun k -> k >= 1)
                  | _ -> false) ->
            Ok (mkTyped (TExprTupleIndex (tT, tI)) baseTy)
        | _ ->
            // Poly-pack / tuple indexing: result type is fresh -- codegen
            // resolves via std::get based on flat-leaf paths.
            Ok (mkTyped (TExprTupleIndex (tT, tI)) (env.Subst.Fresh()))))

// ---- Field access ----


// ---- Provider compound read: alias.load_compound(var, mask) ----
// Rides the generic field-call shape (no new syntax). The mask is any
// integer array; compoundViewType validates full-dimension coverage and
// yields the compact Compound<T, RANK> view type. The maskIR carried in the
// type is a unit placeholder: codegen recovers the actual mask variable by
// name from the argument shape (ProviderReadSpec), not from the type.


and inferMethodCall (env: TypeEnv) (callSpan: Span) obj method args : TypeResult<TypedExpr> =
    inferExpr env obj |> Result.bind (fun tObj ->
    args |> List.map (inferExpr env) |> sequenceResults |> Result.bind (fun tArgs ->
        // Check if this is an impl method call
        match tObj.Type with
        | IRTNamed typeName ->
            match Map.tryFind (typeName, method) env.ImplMethods with
            | Some (funcVarId, funcType) ->
                // Resolve to mangled function call: TypeName__method(self, args)
                let mangledName = $"{Blade.Types.TypeOrigins.sourceName typeName}__{method}"
                let retTy = match funcType with FuncElem (_, ret) -> ret | _ -> env.Subst.Fresh()
                let tFunc = mkTyped (TExprVar (mangledName, funcVarId, None)) funcType
                Ok (mkTyped (TExprApp (tFunc, tObj :: tArgs)) retTy)
            | None ->
                // Not an impl method -- treat as struct field access +
                // application. Same BL3018 refusal as the bare-access arm:
                // the impl lookup above already had its chance, so a name
                // that is neither a method nor a declared field is a typo.
                if callSpan.StartLine > 0 then setCurrentExprSpan callSpan
                structFieldAccess env typeName method |> Result.bind (fun hit ->
                    let (fieldTy, fieldIdx) =
                        match hit with
                        | Some (ty, idx) -> (ty, idx)
                        | None -> (env.Subst.Fresh(), 0)
                    let tField = mkTyped (TExprField (tObj, method, fieldIdx)) fieldTy
                    // Route through dispatchAppOrIndex so array-typed fields
                    // become TExprIndex (with tag-checking) rather than
                    // TExprApp. Without this, `data.region(s)` would lower to
                    // IRApp and emit a C++ function call against the
                    // Array<T,N> wrapper, which has no operator().
                    dispatchAppOrIndex env tField tArgs)
        | _ ->
            // Non-named type -- regular field access + application
            let tField = mkTyped (TExprField (tObj, method, 0)) (env.Subst.Fresh())
            let retTy = env.Subst.Fresh()
            Ok (mkTyped (TExprApp (tField, tArgs)) retTy)))

// ---- Application / Array indexing ----


and inferMask (env: TypeEnv) array pred : TypeResult<TypedExpr> =
    inferExpr env array |> Result.bind (fun tArr ->
    // A NAMED function predicate (`mask(xs, pos)`) is eta-wrapped like a
    // kernel (`lambda(__k) -> pos(__k)`): left a bare function reference, the
    // mask lowering found no unary lambda and every cell read `true`.
    (match etaExpandFunctionKernel env pred with
     | Some r -> r
     | None -> inferExpr env pred) |> Result.bind (fun tPred ->
        requireArrayArg env tArr "mask" |> Result.bind (fun arrTy ->
            // The predicate is `Element -> Bool`. inferExpr typed it
            // without knowing the element type, so its param starts as
            // a fresh IRTInfer. Without explicit unification here, the
            // var stays unbound and zonkType's default (Float64) kicks
            // in -- which then breaks bodies that use integer ops on
            // int arrays (`x % 2`) or struct field access on struct
            // arrays (`p.a`). Unify the predicate's param with the
            // array's element type so zonking propagates the right
            // type into the standalone-lifted function signature and
            // the body's operator/field-access positions resolve
            // against the real element type.
            let unifyPredParam =
                match tPred.Kind with
                | TExprLambda info when info.Params.Length = 1 ->
                    unify env.Subst info.Params.[0].Type arrTy.ElemType
                | _ -> callableRefParamMeetsCells env tPred arrTy.ElemType
            unifyPredParam |> Result.bind (fun () ->
                // (F2: and its field reads / tuple projections of that
                // parameter take their types: `lambda(q) -> q.ok`)
                settleOpenProjections env tPred
                let resultType = mkArrayArrow arrTy.IndexTypes (IRTScalar ETBool) None
                Ok (mkTyped (TExprMask (tArr, tPred)) resultType)))))

// compound(dense, mask) -- scatter a dense array into a CompoundIdx-typed
// compact array via a bool mask over the leading dims (formalism 4.5). The
// in-language analog of the provider's load_compound: same validation
// (compoundViewType checks the mask covers a leading prefix of dense's dims
// and yields the compact Compound<T, RANK> view type), but the dense source
// is a Blade array value rather than a NetCDF variable. The mask must be a
// bool array; compoundViewType already accepts ETBool masks. Lowering records
// (denseIR, maskIR) in CompoundInits; codegen materializes the index (P0,
// genCompoundIndexFromMask), scatters dense -> compact, and bundles a
// Compound<T, RANK> wrapper.


and inferZip (env: TypeEnv) exprs : TypeResult<TypedExpr> =
    exprs |> List.map (inferExpr env) |> sequenceResults |> Result.bind (fun tExprs ->
        // Zip produces an array with tuple element type, shared prefix index space.
        // zip(A : T1^r1, B : T2^r2) -> Array<Tuple(T1,T2), min(r1,r2), shared_indices>
        let arrayTypes =
            tExprs |> List.choose (fun te ->
                match env.Subst.Resolve te.Type with
                | ArrayElem at -> Some at
                | _ -> None)
        if arrayTypes.Length = tExprs.Length && arrayTypes.Length >= 2 then
            // All inputs are arrays -- build proper zip type
            let minRank = arrayTypes |> List.map (_.IndexTypes.Length) |> List.min
            let sharedIndices = arrayTypes.[0].IndexTypes |> List.take minRank
            // Element types: for rank-equal arrays, the elem itself; for higher-rank, remaining slice
            let elemTypes =
                arrayTypes |> List.map (fun at ->
                    let extra = at.IndexTypes |> List.skip minRank
                    if extra.IsEmpty then at.ElemType
                    else mkArrayLike { at with IndexTypes = extra })
            let tupleElemType =
                match elemTypes with
                | [single] -> single  // degenerate: single-array zip
                | _ -> IRTTuple elemTypes
            // Infer a shared ElemType tag for the IRArrayType wrapper
            // We use ETFloat64 as placeholder since the real element is a tuple
            let zipArrayType =
                mkArrayArrow sharedIndices (IRTScalar ETFloat64) None  // placeholder; real elem is the tuple
            Ok (mkTyped (TExprZip tExprs) zipArrayType)
        else
            // Fallback: not all arrays, or fewer than 2 -- return tuple type
            Ok (mkTyped (TExprZip tExprs) (IRTTuple (tExprs |> List.map (_.Type)))))


and inferExtents (env: TypeEnv) array : TypeResult<TypedExpr> =
    // extents(gk) -- the per-group SIZES, as an array over the group axis.
    //
    // This is the answer the rank-2 rejection below cannot give. A grouping is
    // the ragged rank-2 shape (ngroups x ragged), and its inner extent is not a
    // scalar but a vector, one entry per group -- exactly "the lengths array"
    // that rejection points at. Answering it from the GROUPING rather than from
    // a grouped array is what makes it free: sizes are offsets[g+1]-offsets[g],
    // so no values ever have to be gathered.
    //
    // Dispatched before requireArrayArg, which would reject IRTGroupKeys as a
    // non-array. Bare name required, same rule and same reason as group_bucket.
    inferExpr env array |> Result.bind (fun tArr ->
        match env.Subst.Resolve tArr.Type with
        | IRTGroupKeys (outerIdx, _, _) ->
            (match array.Kind with
             | ExprKind.ExprVar _ ->
                 // The group axis verbatim, as group_by's outer slot spells it,
                 // so `extents(gk)` lines up with anything a peel over the same
                 // grouping produced.
                 let outer = { outerIdx with Tag = Some "__group_outer"; IxKind = IxKGroupOuter }
                 Ok (mkTyped (TExprExtents tArr) (mkArrayArrow [outer] (IRTScalar ETInt64) None))
             | ExprKind.ExprGroupKeys _ ->
                 Error (GroupingNeedsName ("extents", "an inline `group_keys(...)` call"))
             | _ -> Error (GroupingNeedsName ("extents", "a non-name expression")))
        | _ ->
        requireArrayArg env tArr "extents" |> Result.bind (fun arrTy ->
            if arrTy.IndexTypes.Length = 1 then
                Ok (mkTyped (TExprExtents tArr) (IRTScalar ETInt64))
            else
                // extents() is static-first: it answers from the ARGUMENT
                // TYPE when possible (IRExtent emits a literal for
                // statically-evaluable extents). A ragged-family slot has
                // no scalar extent AT ALL -- its extent is a per-row
                // function of the outer position -- so the multi-rank
                // tuple form is statically ill-posed for such arrays
                // (the runtime fallback would read a meaningless 0
                // placeholder). Reject with guidance instead.
                // A GROUP-OUTER slot is deliberately NOT in this list: its
                // runtime extent is the group COUNT, a perfectly scalar
                // answer. What has no scalar answer is the ragged MEMBER
                // dimension -- and an array that still carries one wears
                // IxKGroupMember (or a ragged kind) on THAT slot and is
                // refused by it. A rectangular grid a grouped apply returned
                // (group axis x static kernel axes -- the lswosa family grid)
                // has consumed the member axis, so every slot it has left
                // answers, and `extents(G)[0]` is the group count the
                // leading-axis fold's runtime loop bound reads.
                let raggedFamilySlot =
                    arrTy.IndexTypes |> List.exists (fun ix ->
                        match ix.IxKind with
                        | IxKRagged | IxKRaggedInline | IxKRaggedOpaque
                        | IxKDepInner
                        | IxKGroupMember
                        | IxKCompound | IxKCompoundDynamic | IxKSparse -> true
                        | _ -> false)
                if raggedFamilySlot then
                    Error (BuiltinArgErr "extents() on a ragged, grouped, or multi-rank compound/sparse array has no scalar answer for the masked/keyed/ragged dimensions. Use extents(row) on a peeled or indexed row, the lengths array, or extents on a rank-1 compound/sparse (which is its cardinality).")
                else
                // Multi-rank: tuple of Int64s, one per dimension
                let n = arrTy.IndexTypes.Length
                let tupleTy = IRTTuple (List.replicate n (IRTScalar ETInt64))
                Ok (mkTyped (TExprExtents tArr) tupleTy)))



and inferSequence (env: TypeEnv) exprs : TypeResult<TypedExpr> =
    exprs |> List.map (inferExpr env) |> sequenceResults |> Result.bind (fun tExprs ->
        match tExprs with
        | [] -> Ok (mkTyped (TExprSequence []) IRTUnit)
        | [single] -> Ok single  // sequence(c) == c
        | _ ->
            // Unify all element types -- sequence is homogeneous
            let elemType = (List.head tExprs).Type
            let unifyResults =
                tExprs |> List.tail |> List.fold (fun acc e ->
                    acc |> Result.bind (fun () -> unify env.Subst elemType e.Type)) (Ok ())
            unifyResults |> Result.bind (fun () ->
                let resolved = env.Subst.Resolve(elemType)
                let n = tExprs.Length
                // Create anonymous Idx<N> for the sequence dimension
                let seqIdx = {
                    Id = env.Builder.FreshId()
                    Rank = 1
                    Extent = IRLit (IRLitInt (int64 n))
                    Symmetry = SymNone
                    Tag = Some "__seq"; IxKind = IxKSeq
                    Kind = SDimension
                    Dependencies = []
                }
                // Result type: prepend Idx<N> to the element type
                let resultType =
                    match resolved with
                    | ArrayElem arrTy ->
                        // Array elements: Idx<N> x inner index types
                        mkArrayLike { arrTy with IndexTypes = seqIdx :: arrTy.IndexTypes }
                    | IRTScalar et ->
                        // Scalar elements: simple array Idx<N> -> scalar
                        mkArrayArrow [seqIdx] (IRTScalar et) None
                    | _ ->
                        // Fallback to Float64 array for non-array, non-scalar
                        // resolved types -- arguably should be a typecheck error.
                        mkArrayArrow [seqIdx] (IRTScalar ETFloat64) None
                Ok (mkTyped (TExprSequence tExprs) resultType)))

/// The numpy-shaped-mistake guard for IMPLICIT method_for (stage-1 formers):
/// `(A, B) <@> kernel` over operands that COULD co-iterate (same index
/// structure) is the outer product, not a zip. When the former is implicit
/// there is no `method_for` on the page to signal that, so surface a one-time
/// steering warning. Suppressed when the operands are all the same array
/// (self-outer is the domain's core idiom) or the kernel is comm-annotated
/// (the user is thinking in symmetric-outer terms) -- and never emitted for
/// the explicit spelling, which states the intent already.
and warnImplicitOuterProduct (env: TypeEnv) (tLoop: TypedExpr) (rightResult: TypeResult<TypedExpr>) : unit =
    match tLoop.Kind with
    | TExprMethodFor info when info.Arrays.Length >= 2 && List.isEmpty info.SharedIndexTypes ->
        let allSameIdentity =
            match info.Identities with
            | [] -> true
            | first :: rest -> rest |> List.forall (sameIdentity first)
        let kernelComm =
            match rightResult with
            | Ok tR ->
                (match (resolveTypedExpr env tR).Kind with
                 // A declared anticomm counts too: the user who wrote it is
                 // thinking in symmetric-outer terms just as much as a comm
                 // author, and the steering note would be noise.
                 | TExprLambda li ->
                     li.IsCommutative
                     || not (List.isEmpty li.CommGroups)
                     || not (List.isEmpty li.AntisymGroups)
                 | TExprSection (OpAdd | OpMul | OpEq | OpNeq | OpAnd | OpOr) -> true
                 | TExprReynolds _ -> true
                 | _ -> false)
            | Error _ -> false
        let coIterable =
            match zipSharedRecords info.ArrayTypes with
            | Ok _ -> true
            | Error _ -> false
        if coIterable && not allSameIdentity && not kernelComm then
            // BL4004 (symmetry violation band): the note's own suppression rule
            // keys on a declared comm/antisymm, so it belongs with the
            // array-structure/symmetry codes. Minting a dedicated code is a
            // one-line follow-up once the branch's allocations settle.
            emitWarning env "BL4004" tLoop.Span ($"implicit method_for: `(A, B) <@> kernel` iterates the OUTER product of its {info.Arrays.Length} operands (structure-first default), not elementwise pairs. For elementwise co-iteration write `for (A, B) in range<...> <@> kernel` or `zip(A, B) <@> kernel`; write `method_for(...)` explicitly to confirm the outer product and silence this note.")
    | _ -> ()

/// Refuse a multi-leaf combinator (`<&!>` hard fusion, `<&>` parallel) whose
/// leaves include a deduced OrbIdx (iterated-wreath) output.
///
/// Both combinators MERGE loop nests: they build one `LoopNestCodeGen` per leaf
/// and emit their bodies under a shared header. A wreath leaf has no
/// `LoopNestCodeGen` at all -- its nest is the segment-peeled `orb_visit` one,
/// which is a whole-application emitter rather than a level list, so there is
/// nothing to merge and nothing to share a header with. Left alone, the leaf
/// builder reaches `buildSymmVec`, whose `failwith` surfaces as BL9001
/// "internal compiler error, please report it" -- the wrong story for a
/// language limitation. Refuse on the user-facing channel instead; the
/// `buildSymmVec` backstop stays for internal producers.
and internal wreathLeafRefusal (opName: string) (leaves: TypedExpr list) : TypeError option =
    let wreathOf (t: IRType) =
        match (match t with IRTComputation inner -> inner | other -> other) with
        | ArrayElem at -> at.IndexTypes |> List.tryFind (fun ix -> ix.Symmetry = SymWreath)
        | _ -> None
    leaves
    |> List.tryPick (fun (l: TypedExpr) -> wreathOf l.Type)
    |> Option.map (fun ix ->
        OrbitStorageUnsupported (ppOrbitLevels (orbitLevelsOf ix),
                                 sprintf "%s over a wreath-producing leaf (nest merging needs a level \
list to share, and a wreath's nest is the segment-peeled orb_visit traversal)" opName))

/// ---- Explicit numeric casts: Float32(x), Int64(floor(x)), Complex64(z) ----
/// A scalar type name in CALL position converts one numeric value -- the
/// explicit spelling of the conversions the language performs nowhere
/// implicitly. Same plain-call surface and shadowing rule as abs/complex;
/// the accepted heads and their targets are Types.numericCastTargets (the
/// type-position aliases included, so `Int` casts to Int32 and
/// `Float`/`Double` to Float64, exactly what those names mean after `:`).
///
/// Legality: int<->int (both directions, explicit narrowing wraps like the
/// C++ it compiles to), int->float, int->complex, float<->float,
/// float->complex, complex<->complex, and the identity. A complex source
/// never casts to a real/int target (project with real/imag/abs/arg). A
/// float source casts to an int target ONLY through a floor/ceil visible at
/// the cast site -- `Int64(floor(x))` -- so truncation is always spelled.
/// The gate is judged on the TYPED operand (`OpMath "floor"`), so a user
/// function shadowing floor does not license, and a rounded value that took
/// a detour through a let-binding refuses on purpose.
///
/// Units ride through unchanged (a cast changes representation width, not
/// the quantity); the float->int gate composes, since floor/ceil already
/// require a dimensionless operand. Nat/index-tagged sources read out as
/// the underlying integer -- one-way on purpose: no cast target is ever a
/// Nat/index type, so provenance (bounds safety) cannot be laundered back in.
and inferNumericCast (env: TypeEnv) (span: Span) (name: string) (target: ElemType) (arg: Expr) : TypeResult<TypedExpr> =
    let isIntElem = function ETInt32 | ETInt64 -> true | _ -> false
    let isFloatElem = function ETFloat32 | ETFloat64 -> true | _ -> false
    let isComplexElem = function ETComplex64 | ETComplex128 -> true | _ -> false
    let isRoundedOperand (t: TypedExpr) =
        match t.Kind with
        | TExprUnaryOp (OpMath ("floor" | "ceil"), _) -> true
        | _ -> false
    let complexSourceErr () =
        Error (InvalidCast ($"{name}() cannot cast a complex value: project a real component first -- "
                            + "real(z), imag(z), abs(z), or arg(z)."))
    let roundingGateErr () =
        Error (InvalidCast ($"{name}() would truncate a float: spell the rounding at the cast site -- "
                            + $"{name}(floor(x)) or {name}(ceil(x))."))
    inferExpr env arg |> Result.bind (fun tArg ->
        // ROUND 6, THE CAST SEAM (rule 7): a literal-only operand -- or an
        // array-literal nest of them -- takes the CAST'S width: `Float64(0.1)`
        // is the double nearest 0.1, never the Float32 0.1 widened, and the
        // same-class cast is no conversion at all (no cast node). Across
        // classes the cast stays a conversion; a float literal-only operand of
        // a COMPLEX cast first takes the component width (`Complex128(0.1)`).
        match castOfLiteralOnly env span target tArg with
        | Some ({ Kind = TExprArrayLit _ } as node) ->
            // The explicit cast fixes the entire array's width. Keep a value
            // boundary around the optimized literal nest, just as the scalar
            // arm keeps its identity cast: later call adaptation must neither
            // retype it nor mistake it for an uncommitted array-literal peer.
            // Computing an already materialized value is the identity.
            Ok (mkTypedSpan (TExprCompute node) node.Type span)
        // A scalar keeps its cast node (over the operand now AT the cast's
        // width, so it converts nothing): the cast FIXED the width, so the
        // result is no literal-only expression a later seam may retype.
        | Some node -> Ok (mkTypedSpan (TExprUnaryOp (OpCast name, node)) (IRTScalar target) span)
        | None ->
        let tArg =
            match literalOnlyElem env.Subst tArg with
            | Some le when litClassOf le = 1 && litClassOf target = 2 ->
                retypeLiteralOnly env.Subst (if target = ETComplex64 then ETFloat32 else ETFloat64) tArg
                |> Option.defaultValue tArg
            // Any other CROSS-class cast of a literal-only operand converts it
            // from its class's wide width (Int64 / Float64): the cast fixed the
            // result, and the operand computes as it always did.
            | Some le when litClassOf le <> litClassOf target ->
                (match wideOfClass (litClassOf le) with
                 | Some w -> retypeLiteralOnly env.Subst w tArg |> Option.defaultValue tArg
                 | None -> tArg)
            | _ -> tArg
        // A literal argument's width-flexible var binds to its default first,
        // so a CROSS-class cast is a conversion of a concretely-typed value.
        let rec literalDefault (k: TypedExprKind) =
            match k with
            | TExprLit (LitInt n) -> Some (LiteralDefaults.intDefault n)
            | TExprLit (LitFloat f) -> Some (LiteralDefaults.floatDefault f)
            | TExprUnaryOp (OpNeg, inner) -> literalDefault inner.Kind
            | _ -> None
        let bindLit =
            match env.Subst.Resolve tArg.Type, literalDefault tArg.Kind with
            | IRTInfer _, Some et -> unify env.Subst tArg.Type (IRTScalar et)
            | _ -> Ok ()
        bindLit |> Result.bind (fun () ->
        // A CARET operand (`row: T^1`) in a generic body is a rank-k array by
        // declaration: give it its array shape (the demand every array
        // intrinsic issues, materializeArityVar -- index and element
        // variables stay the declaration's own), so the cast lifts
        // elementwise like any array's and its result keeps the operand's
        // shape (formalism 2.4: a cast converts the value type only).
        (match IR.stripUnits (env.Subst.Resolve tArg.Type) with
         | IRTInfer vid when env.InCallableBody && not env.InLambdaBody && env.Subst.IsPolymorphicId vid ->
             (match env.Subst.GetArityConstraint vid with
              | Some k when k >= 1 -> materializeArityVar env tArg name
              | _ -> ())
         | _ -> ())
        let resolved = env.Subst.Resolve tArg.Type
        let mkCast (resTy: IRType) =
            Ok (mkTypedSpan (TExprUnaryOp (OpCast name, tArg)) resTy span)
        let scalarResult (units: UnitSig option) =
            match units with
            | Some u -> IRTUnitAnnotated (IRTScalar target, u)
            | None -> IRTScalar target
        let judgeScalar (src: ElemType) (units: UnitSig option) =
            match src with
            // A Bool converts to 1 / 0 (formalism 2.4: the explicit conversion).
            | ETString | ETUnit ->
                Error (InvalidCast $"{name}() expects a numeric operand; got {ppIRType resolved}.")
            | _ when isComplexElem src && not (isComplexElem target) -> complexSourceErr ()
            | _ when isFloatElem src && isIntElem target && not (isRoundedOperand tArg) -> roundingGateErr ()
            | _ -> mkCast (scalarResult units)
        match resolved with
        | ArrayElem arr ->
            // ARRAY operand: lift elementwise, the same eager synthesis as
            // `cos(A)` -- `(method_for(A) <@> kernel) |> compute`. The
            // kernel must be a LAMBDA (a cast head is not a value); its
            // param stays unannotated: the cast defers inside the body and
            // apply-site unification binds the param to the element type
            // (units ride through kernelBodyUnits' OpCast rule). Element
            // CLASS is judged here so the message names the cast.
            let elemBare = IR.stripUnits (env.Subst.Resolve arr.ElemType)
            let castOf (e: Expr) = mkExpr e.Span (ExprApp (mkExpr e.Span (ExprVar name), [e]))
            let liftOver (source: Expr) (mkBody: Expr -> Expr) =
                let sp = arg.Span
                let param : LambdaParam = { Name = "__castv"; Type = None; Default = None; NameSpan = noSpan }
                let body = mkBody (mkExpr sp (ExprVar "__castv"))
                inferExpr env (mkExpr sp (ExprCompute (mkExpr sp (ExprBinOp (Elementwise, OpApply,
                    mkExpr sp (ExprMethodFor [source]),
                    mkExpr sp (ExprLambda ([param], None, body)))))))
            // A GENERIC element (a caret parameter's cells, `row: T^1`): the
            // class is the instance's, so the legality is judged per call
            // (GOCast), as for a generic scalar below.
            (match elemBare with
             | IRTInfer eid when env.InCallableBody && not env.InLambdaBody && env.Subst.IsPolymorphicId eid ->
                 let rounded =
                     match arg.Kind with
                     | ExprKind.ExprApp ({ Kind = ExprKind.ExprVar ("floor" | "ceil") }, [_]) -> true
                     | _ -> isRoundedOperand tArg
                 (match env.CurrentGenericObligations with
                  | Some acc -> acc.Add(GOCast (eid, name, target, rounded))
                  | None -> ())
             | _ -> ())
            // The direct spelling `Int64(floor(A))` fuses rounding and cast
            // into ONE kernel over floor's own operand (one traversal, and
            // the gate stays visible in the synthesized body).
            let roundedCall =
                match arg.Kind with
                | ExprKind.ExprApp ({ Kind = ExprKind.ExprVar rname }, [inner])
                       when (rname = "floor" || rname = "ceil") && (lookupVar rname env).IsNone -> Some (rname, inner)
                | _ -> None
            let fusedRounding () =
                roundedCall |> Option.map (fun (rname, inner) ->
                    liftOver inner (fun v -> castOf (mkExpr v.Span (ExprApp (mkExpr v.Span (ExprVar rname), [v])))))
            match elemBare with
            // A Bool array (a mask) converts cell by cell to 1 / 0.
            | IRTScalar (ETString | ETUnit) ->
                Error (InvalidCast $"{name}() expects numeric elements; got an array of {ppIRType elemBare} elements.")
            | IRTScalar src when isComplexElem src && not (isComplexElem target) -> complexSourceErr ()
            // A GENERIC element (`x: T^1`) rounded at the cast site: fused
            // the same way, so the kernel's cast sees its floor (lifting the
            // floor alone first typed its cells Float64 with no rounding in
            // sight of the cast -- refused at the declaration). The
            // instance's class is judged per call (GOCast, above).
            | IRTInfer _ when isIntElem target && roundedCall.IsSome -> (fusedRounding ()).Value
            | IRTScalar src when isFloatElem src && isIntElem target ->
                // Rounding gate, array flavor: the fused spelling above;
                // anything else refuses with the kernel spelling.
                (match fusedRounding () with
                 | Some r -> r
                 | None ->
                     Error (InvalidCast ($"{name}() would truncate float elements: spell the rounding at the "
                                         + $"cast site -- {name}(floor(A)) / {name}(ceil(A)) -- or put both in "
                                         + $"one kernel: method_for(A) <@> lambda(v) -> {name}(floor(v))")))
            | _ -> liftOver arg castOf
        | _ ->
            let bare = IR.stripUnits resolved
            let units = IR.getUnits resolved
            match bare with
            | IRTScalar src -> judgeScalar src units
            | IRTNat _ -> mkCast (scalarResult units)
            | IRTIdxTagged (inner, _) ->
                (match IR.stripUnits inner with
                 | IRTScalar src -> judgeScalar src units
                 | _ -> Error (InvalidCast $"{name}() expects a numeric operand; got {ppIRType resolved}."))
            | IRTInfer _ when env.InLambdaBody ->
                // KERNEL-BODY DEFERRAL, same shape as the math intrinsics'
                // IRTInfer arm: the operand is a kernel parameter apply-site
                // unification has not bound yet. The RESULT type needs no
                // deferral -- a cast's type is its target -- but the
                // legality judgment needs the source, so buildApplyInfo's
                // findBadDeferredCast re-judges this node once the param is
                // bound.
                mkCast (IRTScalar target)
            | IRTInfer vid when env.InCallableBody && env.Subst.IsPolymorphicId vid ->
                // GENERIC CAST: the operand's element is a SIGNATURE variable
                // of the function being declared (`Float64(reduce(row, (+)))`
                // over `row: T^1`). Its source class is decided per call
                // site, by the same instantiation IR monomorphization
                // specializes on, so the legality judgment moves there with
                // it: every call is judged against the instance the call
                // judgment built (TypeCheckSupport.genericObligationClash), and the
                // specialized body is judged again after monomorphization
                // (IRValidate's cast sweep) for the seams no call judgment
                // sees (eta-wrapped kernels). The result is the target,
                // carrying the operand's unit like every cast.
                //
                // SHAPE: a cast converts the VALUE TYPE only (formalism 2.4).
                // A variable a PARAMETER is declared as (`x: T`, which an
                // array instantiates as readily as a scalar) or an earlier
                // cast's open result has no rank here, so the result is the
                // element-cast variable "T with element `target`" (Subst.
                // ElemCastVar), which each instance resolves to its own shape
                // -- `Float64(x)` over an Int64 matrix is a Float64 matrix. A
                // variable that is only ever a scalar (an array's element,
                // `reduce(row, (+))`) keeps the scalar target.
                let paramTopVar =
                    let sigTys =
                        env.CurrentSignature
                        |> List.map (fun t -> env.Subst.Resolve (IR.stripUnits t) |> IR.stripUnits)
                    let paramTys = if sigTys.IsEmpty then [] else List.take (sigTys.Length - 1) sigTys
                    paramTys |> List.exists (fun t -> match t with IRTInfer r -> r = vid | _ -> false)
                match env.Subst.GetElemCast vid with
                | Some (_, earlier) ->
                    // A cast of a cast: the source element is the earlier
                    // target, known here.
                    (match castLegality name target earlier (isRoundedOperand tArg) with
                     | Some msg -> Error (InvalidCast msg)
                     | None ->
                         let r = env.Subst.ElemCastVar(vid, target)
                         mkCast (match units with Some u -> IRTUnitAnnotated (r, u) | None -> r))
                | None ->
                (match env.CurrentGenericObligations with
                 | Some acc -> acc.Add(GOCast (vid, name, target, isRoundedOperand tArg))
                 | None -> ())
                if paramTopVar then
                    let r = env.Subst.ElemCastVar(vid, target)
                    mkCast (match units with Some u -> IRTUnitAnnotated (r, u) | None -> r)
                else
                    mkCast (scalarResult units)
            | IRTInfer _ ->
                Error (InvalidCast ($"{name}() needs a concretely-typed operand, and this one's type is not "
                                    + "determined here -- annotate the value (or the parameter it came from) "
                                    + "with a concrete numeric type."))
            | _ ->
                Error (InvalidCast $"{name}() expects a numeric operand; got {ppIRType resolved}.")))

/// ARITHMETIC ON A `let rec` STEP ORDINAL (recStepVars) is a POSITION: typed
/// as the plain integer it is for the operator itself (an index value
/// refuses arithmetic, IndexTypeArithForbidden), and the arithmetic result
/// keeps the axis tag for the nominal rule -- `prefix(n - 1)`, `u((n + 1) %
/// 5)` -- exactly as an unannotated `range<I>` kernel parameter's arithmetic
/// does (formalism 3.10, "Positions and casts"). A comparison is a Bool.
and inferBinOp env mode op left right : TypeResult<TypedExpr> =
    let steps = recStepVars ()
    let stepNames =
        if steps.Count = 0 then Map.empty
        else
            let rec scan (acc: Map<string, IRId * string>) (e: Expr) =
                match e.Kind with
                | ExprKind.ExprVar v ->
                    (match lookupVar v env with
                     // Still bound at its tag: a nested operand of an outer
                     // position (`(n + 1) % 5`) is typed under the outer's
                     // plain rebinding and keeps a plain type until the top.
                     | Some vi when steps.ContainsKey vi.VarId && vi.Type.IsIRTIdxTagged ->
                         Map.add v (vi.VarId, steps.[vi.VarId]) acc
                     | _ -> acc)
                | ExprKind.ExprBinOp (Elementwise, (OpAdd | OpSub | OpMul | OpDiv | OpMod), l, r) -> scan (scan acc l) r
                | ExprKind.ExprUnaryOp (OpNeg, x) -> scan acc x
                | _ -> acc
            match op with
            | OpAdd | OpSub | OpMul | OpDiv | OpMod
            | OpEq | OpNeq | OpLt | OpLe | OpGt | OpGe when mode = Elementwise -> scan (scan Map.empty left) right
            | _ -> Map.empty
    if stepNames.IsEmpty then
        // Round 6: a literal-only result widened by its exact value.
        inferBinOpCore env mode op left right |> Result.map (widenLiteralOnlyByValue env.Subst)
    else
        let plainEnv =
            stepNames |> Map.fold (fun e name (vid, _) -> bindVarSimple name vid (IRTScalar ETInt64) e) env
        // Two ordinals of DIFFERENT axes in one position (nested let recs) keep
        // the first one's tag; the nominal rule then judges where it is used.
        let tag = stepNames |> Map.toList |> List.head |> snd |> snd
        inferBinOpCore plainEnv mode op left right
        |> Result.map (fun te ->
            match op, IR.stripUnits (env.Subst.Resolve te.Type) with
            | (OpAdd | OpSub | OpMul | OpDiv | OpMod), IRTScalar (ETInt64 | ETInt32) ->
                { te with Type = IRTIdxTagged (IRTScalar ETInt64, IRefNamed tag) }
            | _ -> te)

and inferBinOpCore env mode op left right : TypeResult<TypedExpr> =
    // REDUCTION JOIN, Form 1: `object_for(<&!>) <@> (r1, r2, ...)`. The
    // fusion operator as the kernel of a loop former (operator sections are
    // already legal kernels) applied over a PACK of reductions joins them into
    // one traversal answering a Tuple<k>. Matched on the SURFACE, before the
    // former/kernel classification below, because the pack elements are legs
    // to be normalized -- not arrays to be iterated over.
    //
    // Only when the leading leg is written as a REDUCTION. The same shape over
    // deferred MAPS is n-ary map fusion answering k arrays and predates joins
    // (loops/029); `isJoinLegShape` is what keeps the two readings apart.
    let joinPackLegs =
        match op, left.Kind with
        | OpApply, ExprKind.ExprObjectFor { Kind = ExprKind.ExprSection OpFusion } ->
            let legs =
                match right.Kind with
                | ExprKind.ExprTuple legs -> legs
                | _ -> [right]
            (match legs with
             | first :: _ when isJoinLegShape first -> Some legs
             | _ -> None)
        | _ -> None
    match joinPackLegs with
    | Some legs -> inferReductionJoin env legs right
    | None ->

    match op with
    | OpApply ->
        inferApplyOp env left right mode

    | OpBind ->
        // >>= : Computation alpha x (alpha -> Computation beta) -> Computation beta
        // Result type is the return type of the continuation
        inferExpr env left |> Result.bind (fun tL ->
        inferExpr env right |> Result.bind (fun tR ->
            // Path 1 follow-on: propagate type info from the computation
            // into the continuation. Without this, a continuation like
            // `lambda(arr) -> method_for(arr) <@> ...` has arr left as
            // IRTInfer (because the body's `method_for(arr)` doesn't
            // pin it), and codegen subsequently emits arr's type as
            // a default scalar -- which doesn't match the array-typed
            // computation flowing in. Unify the continuation's first
            // param with alpha (the computation's element type, unwrapping
            // an explicit IRTComputation if present).
            let alpha =
                match tL.Type with
                | IRTComputation t -> t
                | t -> t
            let unifyResult =
                match tR.Type with
                | FuncElem (paramTys, _) when paramTys.Length >= 1 ->
                    unify env.Subst (List.head paramTys) alpha
                | _ -> Ok ()
            unifyResult |> Result.bind (fun () ->
                let resultType =
                    match tR.Type with
                    | FuncElem (_, retType) -> retType  // k : alpha -> beta, result is beta
                    | _ -> tR.Type  // If not a function, use right's type directly
                Ok (mkTyped (TExprBind (tL, tR)) resultType))))

    | OpParallel ->
        inferExpr env left |> Result.bind (fun tL ->
        inferExpr env right |> Result.bind (fun tR ->
            match wreathLeafRefusal "<&>" [tL; tR] with
            | Some e -> Error e
            | None -> Ok (mkTyped (TExprParallel (tL, tR)) (IRTTuple [tL.Type; tR.Type]))))

    | OpFusion ->
        inferExpr env left |> Result.bind (fun tL ->
        inferExpr env right |> Result.bind (fun tR ->
            match wreathLeafRefusal "<&!>" [tL; tR] with
            | Some e -> Error e
            | None -> Ok (mkTyped (TExprFusion (tL, tR)) (IRTTuple [tL.Type; tR.Type]))))

    | OpFunctor ->
        // <$> : (alpha -> beta) x Computation alpha -> Computation beta
        // f <$> c  transforms the result of computation c by applying f
        inferExpr env left |> Result.bind (fun tF ->
        inferExpr env right |> Result.bind (fun tC ->
            // Output type: same array shape, element type from f's return
            let outputType = functorMapOutputType env tF tC
            match functorMapInheritError env tF tC with
            | Some e -> Error e
            | None -> Ok (mkTyped (TExprFunctorMap (tF, tC)) outputType)))

    | OpArrayProd ->
        inferArrayProdOp env left right mode op

    | OpChoice ->
        inferExpr env left |> Result.bind (fun tL ->
        inferExpr env right |> Result.bind (fun tR ->
            // The two alternatives are one value's two sources, so they must
            // agree (the result was discarded, and the node typed from the
            // left alone).
            unify env.Subst tL.Type tR.Type |> Result.map (fun () ->
                mkTyped (TExprChoice (tL, tR)) tL.Type)))

    | OpFallback ->
        inferFallbackOp env left right

    | OpComposeMeth ->
        // @>> : Computation alpha x Computation beta -> Computation beta
        // Result type is the right side's type
        inferExpr env left |> Result.bind (fun tL ->
        inferExpr env right |> Result.bind (fun tR ->
            Ok (mkTyped (TExprCompose (op, tL, tR)) tR.Type)))

    | OpComposeObj ->
        // >>@ : ObjectLoop x ObjectLoop -> ObjectLoop
        // Preserve as loop type so inferApply can handle application
        inferExpr env left |> Result.bind (fun tL ->
        inferExpr env right |> Result.bind (fun tR ->
            // Result type: preserve right side's loop type (since g determines output shape)
            let resultType = 
                match tR.Type with
                | IRTLoop _ -> tR.Type
                | _ -> tL.Type
            Ok (mkTyped (TExprCompose (OpComposeObj, tL, tR)) resultType)))

    | OpCompose ->
        inferComposeOp env left right op

    | OpCons ->
        inferExpr env left |> Result.bind (fun tL ->
        inferExpr env right |> Result.bind (fun tR ->
            let resTy = match tR.Type with
                         | IRTTuple ts -> IRTTuple (tL.Type :: ts)
                         | _ -> IRTTuple [tL.Type; tR.Type]
            Ok (mkTyped (TExprBinOp (mode, op, tL, tR)) resTy)))

    | _ ->
        // BOUND, not returned: a returned hand-off into a moved rule with more stack arguments goes through the
        // runtime's tail-call helper (TypeCheckInferEntries.fs).
        match inferArithBinOp inferEntries env left right op mode with
        | Ok t -> Ok t
        | Error e -> Error e

and inferApplyOp (env: TypeEnv) (left: Expr) (right: Expr) (mode: BinOpMode) : TypeResult<TypedExpr> =
    // A bare named-function reference on the kernel side (the right
    // operand of <@>) is eta-expanded to lambda(__k..) -> f(__k..) so it
    // matches the existing TExprLambda kernel arm in inferApply.
    // A kernel LAMBDA over INTEGER EnumIdx values -- `method_for(range<R>)`
    // or an array of R -- is typed with its parameters SEEDED as R: a value
    // of R is a key (D2), and the body's arithmetic and comparisons observe
    // it (inferBinOpCore's observeKey) only if the parameter is known to be
    // one while the body is typed. Left open, `r + 1` took r's own type and
    // the kernel's result read as a value of R.
    //
    // R3: `object_for(k) <@> codes` with `k` a NAMED callable (a let-bound
    // lambda value or a function) over ONE rank-1 array of R keys is the
    // map `method_for(codes) <@> k` (formalism 6: the same arrays against
    // the same kernel), and is typed as that one: there the kernel meets
    // its operand while it is typed (a lambda value is instantiated at the
    // apply, a function is called from a wrapper seeded R), so its
    // arithmetic observes the keys. Typed through the object former, the
    // lambda value's body had been typed before it met R, and its `c + 1`
    // read as a value of R (refused, BL4003).
    let objectForOverKeys =
        match left.Kind, right.Kind with
        | ExprKind.ExprObjectFor ({ Kind = ExprKind.ExprVar kname } as k), ExprKind.ExprVar aname ->
            (match lookupVar kname env, lookupVar aname env with
             | Some ki, Some ai ->
                 // Any kernel value but a former, an array or a pack (an
                 // arity mismatch is the same error in either spelling).
                 let unaryCallable =
                     let kTy = match ki.Scheme with Some sc -> sc.Body | None -> ki.Type
                     match IR.stripUnits (env.Subst.Resolve kTy) with
                     | IRTLoop _ | ArrayElem _ | IRTTuple _ -> false
                     | FuncElem (ps, _) -> not (ps |> List.exists (fun p -> (env.Subst.Resolve p).IsIRTPoly))
                     | _ -> true
                 let rank1Keys =
                     match IR.stripUnits (env.Subst.Resolve ai.Type) with
                     | ArrayElem at -> at.IndexTypes.Length = 1 && intEnumValueType env at.ElemType
                     | _ -> false
                 if unaryCallable && rank1Keys then Some k else None
             | _ -> None)
        | _ -> None
    match objectForOverKeys with
    | Some k -> inferBinOpCore env mode OpApply (mkExpr left.Span (ExprMethodFor [ right ])) k
    | None ->
    inferApplyKernelSide env left right mode

and inferApplyKernelSide (env: TypeEnv) (left: Expr) (right: Expr) (mode: BinOpMode) : TypeResult<TypedExpr> =
    let seededKernel = inferApplySeededKernel env left right
    let rightResult =
        match seededKernel with
        | Some r -> r
        | None ->
        match etaExpandFunctionKernel env right with
        | Some r -> r
        | None -> inferExpr env right
    // Implicit formers: when the left side is not already a former, the
    // RIGHT operand classifies the pair (right-operand-first). A lambda /
    // section / reynolds / zero (or a named function, eta-expanded above)
    // is decisively a kernel, so the left side must be the arrays, and the
    // method_for the keyword would have introduced is synthesized around
    // it; a kernel-shaped LEFT with a non-kernel right synthesizes
    // object_for instead. Both directions re-drive the same inferMethodFor
    // / inferObjectFor the explicit keywords use, so everything downstream
    // of this seam (Lowering, CodeGen, the interpreter, both differential
    // gates) sees the identical typed nodes. Undecidable pairs fall
    // through to inferApply's steering diagnostic (ChainOpUndecidable).
    let rightKernelShaped =
        match rightResult with
        | Ok tR ->
            (match (resolveTypedExpr env tR).Kind with
             | TExprLambda _ | TExprSection _ | TExprReynolds _ | TExprZero -> true
             | _ -> false)
        | Error _ -> false
    // The RIGHT operand is inferred FIRST, so a refusal inside it (a call
    // in a kernel lambda's body) was located where its inference stopped
    // -- and then the left operand's inference re-stamped the expression
    // span (TypeEnv.locateError takes the LAST stamp), which put the
    // refusal on `range<I>` / the arrays instead of the offending
    // argument. Keep the right's own location for its error.
    let rightErrSpan =
        match rightResult with
        | Error e -> Some (errorOwnSpan e |> Option.defaultWith currentExprSpan)
        | Ok _ -> None
    let applyWith (tL: TypedExpr) =
        match rightResult, rightErrSpan with
        | Error e, Some s ->
            if s.StartLine > 0 then setCurrentExprSpan s
            Error e
        | _ -> rightResult |> Result.bind (fun tR -> inferApply env tL tR)
    // A bare NAMED function on the LEFT is a kernel too -- `covariance <@>
    // (data, data)`. It can never resolve to a TExprLambda (a top-level
    // `function` binds with TypedValue = None), so without this arm the
    // pair falls through to ChainOpUndecidable. Routes through
    // inferObjectFor, same as inferObjectFor's own two shapes (fixed
    // arity: etaExpandFunctionKernel builds lambda(__k..) -> f(__k..);
    // Poly pack: etaExpandFunctionKernel refuses since the pack width is
    // unknown until the argument tuple reveals it, so inferObjectFor
    // builds a DEFERRED former instead) -- so `f <@> args` is exactly
    // `object_for(f) <@> args`. Tested by predicate rather than calling
    // etaExpandFunctionKernel directly, which would mint a second lambda.
    let namedFunctionKernelLeft =
        match left.Kind with
        | ExprKind.ExprVar name ->
            (match lookupVar name env with
             | Some info when Option.isNone info.TypedValue ->
                 (match env.Subst.Resolve info.Type with
                  | FuncElem (paramTys, _) -> not paramTys.IsEmpty
                  | _ -> false)
             | _ -> false)
        | _ -> false
    let syntacticFormer =
        match left.Kind with
        | ExprKind.ExprMethodFor _ | ExprKind.ExprObjectFor _
        | ExprKind.ExprFor _
        | ExprKind.ExprBinOp (_, OpComposeObj, _, _) -> true
        | _ -> false
    // `method_for(A, B) <@> polyFn` for a named function over a `Poly`
    // pack: etaExpandFunctionKernel cannot wrap it (the pack width is the
    // operand count, unknown to it), and the method_for arm took the
    // un-expanded variable as a non-kernel (BL3007 `got variable`) while
    // `object_for(polyFn) <@> (A, B)` and `polyFn <@> (A, B)` worked.
    // It IS that application -- the same arrays against the same kernel --
    // so it is typed as one (inferObjectFor builds the deferred former).
    let polyNamedKernelRight =
        match right.Kind with
        | ExprKind.ExprVar name ->
            (match lookupVar name env with
             | Some info when Option.isNone info.TypedValue ->
                 (match env.Subst.Resolve info.Type with
                  | FuncElem (paramTys, _) -> paramTys |> List.exists (fun t -> (env.Subst.Resolve t).IsIRTPoly)
                  | _ -> false)
             | _ -> false)
        | _ -> false
    // G114: an OBJECT former's lambda kernel -- `object_for(lambda ..) <@>
    // ops` or the implicit `(lambda ..) <@> ops` -- is hinted by its
    // operands' narrow elements exactly as a method former's is (the
    // seededKernel arm above): its literal partners take the operand
    // width, the parameters stay open.
    let objectKernelHints =
        let kernelLam =
            match left.Kind with
            | ExprKind.ExprObjectFor ({ Kind = ExprKind.ExprLambda _ } as k) -> Some k
            | ExprKind.ExprLambda _ -> Some left
            | _ -> None
        match kernelLam with
        | Some ({ Kind = ExprKind.ExprLambda (parms, _, _) } as k) when not rightKernelShaped ->
            let ops = match right.Kind with ExprKind.ExprTuple es -> es | _ -> [ right ]
            if ops.Length = parms.Length then
                let hints = ops |> List.map (operandElemHint env)
                // R3: an operand of integer EnumIdx keys SEEDS its
                // parameter R (as a method former's lambda is seeded), so
                // the body's arithmetic observes the key. A let-bound
                // lambda VALUE used as `object_for(k)` arrives here too,
                // its source substituted into the kernel position.
                let seeds =
                    ops |> List.map (fun (o: Expr) ->
                        match o.Kind with
                        | ExprKind.ExprRange [ TyNamed (n, []) ] when (intEnumKeysOfTag env n).IsSome ->
                            Some (IRTIdxTagged (IRTScalar ETInt64, IRefNamed n))
                        | ExprKind.ExprVar v ->
                            (match lookupVar v env with
                             | Some vi ->
                                 (match IR.stripUnits (env.Subst.Resolve vi.Type) with
                                  | ArrayElem at when at.IndexTypes.Length = 1 && intEnumValueType env at.ElemType ->
                                      Some (env.Subst.Resolve at.ElemType)
                                  | _ -> None)
                             | None -> None)
                        | _ -> None)
                if hints |> List.exists Option.isSome || seeds |> List.exists Option.isSome then
                    Some (k, (seeds, hints |> List.mapi (fun i h -> if (List.item i seeds).IsSome then None else h)))
                else None
            else None
        | _ -> None
    match objectKernelHints with
    | Some (k, (seeds, hints)) ->
        let kernelTyped () =
            match k.Kind with
            | ExprKind.ExprLambda (parms, wc, body) ->
                if k.Span.StartLine > 0 then setCurrentExprSpan k.Span
                inferLambdaHinted env parms wc body (if List.exists Option.isSome seeds then seeds else []) hints
                |> Result.map (fun te ->
                    if te.Span.StartLine = 0 && k.Span.StartLine > 0 then { te with Span = k.Span } else te)
            | _ -> inferExpr env k
        inferObjectForCore env k (Some kernelTyped)
        |> Result.map (stampSynthSpan left)
        |> Result.bind applyWith
    | None ->
    // G172 (c): a COMPOSE CHAIN of one-parameter object_for(lambda)
    // stages over ONE narrow operand is hinted STAGE BY STAGE: stage 1
    // by the operand's element, stage k+1 by stage k's result when that
    // is narrow -- a resolved narrow scalar, a cast to a narrow type
    // (`Float32(v)` restarts the hint), or an open result carrying the
    // hint (`v + 1.0`); a widening cast or anything unknown stops it. A
    // hint fixes only the width of literal partners, never a shape; the
    // chain is then applied exactly as the unhinted spelling is.
    let composeHints =
        let rec stages (e: Expr) =
            match e.Kind with
            | ExprKind.ExprBinOp (_, OpComposeObj, l, r) ->
                stages l |> Option.bind (fun ls -> stages r |> Option.map (fun rs -> ls @ rs))
            | ExprKind.ExprObjectFor ({ Kind = ExprKind.ExprLambda ([ p ], _, _) } as k) when p.Type.IsNone -> Some [ (e, k) ]
            | _ -> None
        match left.Kind with
        | ExprKind.ExprBinOp (_, OpComposeObj, _, _) when not rightKernelShaped ->
            (match right.Kind, stages left, operandElemHint env right with
             | ExprKind.ExprTuple _, _, _ -> None
             | _, Some ks, Some h0 when ks.Length >= 2 -> Some (ks, h0)
             | _ -> None)
        | _ -> None
    match composeHints with
    | Some (ks, h0) ->
        let rec narrowOf (t: IRType) : ElemType option =
            let isNarrow et = (match et with ETFloat32 | ETInt32 | ETComplex64 -> true | _ -> false)
            match IR.stripUnits (env.Subst.Resolve t) with
            | IRTScalar et when isNarrow et -> Some et
            | IRTInfer v ->
                (match env.Subst.GetElemCast v with
                 | Some (_, et) -> if isNarrow et then Some et else None
                 | None -> env.Subst.GetElemHint v |> Option.filter isNarrow)
            // an array-valued stage (`lambda(v) -> [v, v]`): its element
            | ArrayElem at -> narrowOf at.ElemType
            | _ -> None
        let typeStage (hint: ElemType option) ((stageExpr: Expr), (k: Expr)) : TypeResult<TypedExpr * ElemType option> =
            let next : ElemType option ref = ref None
            let kernelTyped () =
                match k.Kind with
                | ExprKind.ExprLambda (parms, wc, body) ->
                    if k.Span.StartLine > 0 then setCurrentExprSpan k.Span
                    inferLambdaHinted env parms wc body [] [ hint ]
                    |> Result.map (fun te ->
                        next.Value <-
                            (match env.Subst.Resolve te.Type with
                             | FuncElem (_, ret) -> narrowOf ret
                             | _ -> None)
                        if te.Span.StartLine = 0 && k.Span.StartLine > 0 then { te with Span = k.Span } else te)
                | _ -> inferExpr env k
            inferObjectForCore env k (Some kernelTyped)
            |> Result.map (fun t -> (stampSynthSpan stageExpr t, next.Value))
        let typedStages =
            ks |> List.fold (fun acc stage ->
                acc |> Result.bind (fun (done_, hint) ->
                    typeStage hint stage |> Result.map (fun (t, next) -> (done_ @ [ t ], next))))
                (Ok ([], Some h0))
        typedStages
        |> Result.map (fun (ts, _) ->
            // the OpComposeObj judgment, left-associated
            ts.Tail |> List.fold (fun (tL: TypedExpr) (tR: TypedExpr) ->
                let resultType = match tR.Type with IRTLoop _ -> tR.Type | _ -> tL.Type
                mkTyped (TExprCompose (OpComposeObj, tL, tR)) resultType) ts.Head)
        |> Result.map (stampSynthSpan left)
        |> Result.bind applyWith
    | None ->
    match left.Kind with
    | ExprKind.ExprMethodFor arrays when polyNamedKernelRight && not arrays.IsEmpty ->
        let operand =
            match arrays with
            | [ single ] -> single
            | many -> ({ Kind = ExprKind.ExprTuple many; Span = left.Span } : Expr)
        let objFor : Expr = { Kind = ExprKind.ExprObjectFor right; Span = right.Span }
        inferBinOp env mode OpApply objFor operand
    | _ ->
    if syntacticFormer then
        // Explicit spelling: the unchanged path.
        inferExpr env left |> Result.bind applyWith
    else
        match left.Kind with
        | ExprKind.ExprTuple elems when rightKernelShaped ->
            // (A, B) <@> kernel  ==  method_for(A, B) <@> kernel
            inferMethodFor env elems |> Result.bind (fun tL0 ->
                let tL = stampSynthSpan left tL0
                warnImplicitOuterProduct env tL rightResult
                applyWith tL)
        | ExprKind.ExprVar name when isUnaryIntrinsic name && (lookupVar name env).IsNone ->
            // Bare unary intrinsic on the left: `abs <@> u` ==
            // `object_for(abs) <@> u`, the same shape namedFunctionKernelLeft
            // gives a named function. It needs its own arm because an
            // intrinsic has no binding AT ALL: the arms below open by
            // inferring the left operand, which reports it unbound long
            // before any classification runs. An intrinsic can never be the
            // arrays operand, so -- unlike a bare name -- a kernel-shaped
            // RIGHT does not take it back; that pairing falls through
            // applyWith to the kernel <@> kernel steering.
            inferObjectFor env left
            |> Result.map (stampSynthSpan left)
            |> Result.bind applyWith
        | _ ->
            inferExpr env left |> Result.bind (fun tL ->
                match (resolveTypedExpr env tL).Kind with
                | TExprMethodFor _ | TExprObjectFor _
                | TExprCompose (OpComposeObj, _, _) ->
                    // Resolves to a former (e.g. a let-bound loop object):
                    // the unchanged path, with the original typed left.
                    applyWith tL
                | TExprLambda _ | TExprSection _ | TExprReynolds _ | TExprZero ->
                    if rightKernelShaped then
                        // Kernel <@> kernel: nothing to iterate over --
                        // reaches the steering diagnostic below.
                        applyWith tL
                    else
                        // kernel <@> arrays  ==  object_for(kernel) <@> arrays.
                        // Re-driving inferObjectFor on the source expr keeps
                        // the resolve-at-apply behavior for let-bound
                        // lambdas (compose chains) in the one existing place.
                        inferObjectFor env left
                        |> Result.map (stampSynthSpan left)
                        |> Result.bind applyWith
                | _ when rightKernelShaped ->
                    // Arrays-shaped left (variable, call, zip, literal):
                    // A <@> kernel  ==  method_for(A) <@> kernel. Re-driving
                    // inferMethodFor on the source expr keeps zip expansion
                    // and identity extraction in the one existing place.
                    inferMethodFor env [left]
                    |> Result.map (stampSynthSpan left)
                    |> Result.bind applyWith
                | _ when namedFunctionKernelLeft ->
                    // Bare named function on the left, nothing decisive on
                    // the right: named-kernel <@> arrays ==
                    // object_for(named-kernel) <@> arrays. (Guarded by the
                    // arm above, so a decisive RIGHT still wins -- a bare
                    // name meeting a lambda stays the arrays operand.)
                    //
                    // The synthesized former is built by calling
                    // inferObjectFor directly, so it misses the span
                    // back-fill inferExpr does for the explicit spelling --
                    // hence stampSynthSpan (see its note: the BL4010
                    // suggestion needs a location to anchor on).
                    inferObjectFor env left
                    |> Result.map (stampSynthSpan left)
                    |> Result.bind applyWith
                | _ ->
                    // Undecidable: steering diagnostic via inferApply.
                    applyWith tL)

and inferApplySeededKernel (env: TypeEnv) (left: Expr) (right: Expr) : TypeResult<TypedExpr> option =
    // The operands each kernel parameter meets: `method_for(a, b)`, a
    // zip's members, or the implicit former's one array (`xs <@> ..`).
    let operandsOf (e: Expr) =
        match e.Kind with
        | ExprKind.ExprMethodFor [ { Kind = ExprKind.ExprZip zs } ] -> Some zs
        | ExprKind.ExprMethodFor ops -> Some ops
        | ExprKind.ExprVar _ -> Some [ e ]
        // G114: the implicit former over an operand EXPRESSION
        // (`(a + a) <@> lambda ..`) or a tuple (`(A, B) <@> ..`).
        | ExprKind.ExprBinOp (Elementwise, (OpAdd | OpSub | OpMul | OpDiv), _, _)
        | ExprKind.ExprUnaryOp (OpNeg, _) -> Some [ e ]
        | ExprKind.ExprTuple es -> Some es
        | _ -> None
    // G114: a STORED method former (`let loop = method_for(a)`, then
    // `loop <@> lambda ..`) hints its arrays' elements.
    let storedLoopHints (nParams: int) =
        match left.Kind with
        | ExprKind.ExprVar v ->
            (match lookupVar v env with
             | Some vi ->
                 (match env.Subst.Resolve vi.Type with
                  | IRTLoop lt when lt.Kind = LKMethod && lt.ArrayTypes.Length = nParams ->
                      let hs = lt.ArrayTypes |> List.map (fun t ->
                          match IR.stripUnits (env.Subst.Resolve t) with
                          | ArrayElem at ->
                              (match IR.stripUnits (env.Subst.Resolve at.ElemType) with
                               | IRTScalar (ETFloat32 | ETInt32 | ETComplex64 as et) -> Some et
                               | _ -> None)
                          | _ -> None)
                      if hs |> List.exists Option.isSome then Some hs else None
                  | _ -> None)
             | None -> None)
        | _ -> None
    // R3: the R key seed of one operand (`range<R>`, or a named rank-1
    // array of R keys) -- the integer EnumIdx half of the lambda arm's
    // seedOf below, for the named-function wrapper.
    let keySeedOf (o: Expr) =
        let rTy (n: string) =
            if (intEnumKeysOfTag env n).IsSome then Some (IRTIdxTagged (IRTScalar ETInt64, IRefNamed n)) else None
        match o.Kind with
        | ExprKind.ExprRange [ TyNamed (n, []) ] -> rTy n
        | ExprKind.ExprVar v ->
            (match lookupVar v env with
             | Some vi ->
                 (match IR.stripUnits (env.Subst.Resolve vi.Type) with
                  | ArrayElem at when at.IndexTypes.Length = 1 ->
                      (match IR.stripUnits (env.Subst.Resolve at.ElemType) with
                       | IntTaggedIndex n -> rTy n
                       | _ -> None)
                  | _ -> None)
             | None -> None)
        | _ -> None
    match operandsOf left, right.Kind with
    // R3: a NAMED function kernel over R keys (`method_for(codes) <@>
    // inc`): its eta wrapper `lambda(__k) -> inc(__k)` is typed with
    // the wrapper's parameter SEEDED as R, so the call inside it hands
    // a key to the function -- observed at the call (a generic
    // parameter takes the key, index-types/503), never the position.
    // Unseeded, the wrapper parameter was open at the call and met R
    // only afterwards (refused at zonk, BL4003).
    | Some operands, ExprKind.ExprVar _
            when operands |> List.exists (fun o -> (keySeedOf o).IsSome) ->
        etaExpandFunctionKernelSeeded env right (operands |> List.map keySeedOf)
    | _, ExprKind.ExprLambda (parms, wc, body) when (storedLoopHints parms.Length).IsSome ->
        Some (inferLambdaHinted env parms wc body [] (storedLoopHints parms.Length).Value)
    | Some operands, ExprKind.ExprLambda (parms, wc, body)
            when parms.Length = operands.Length ->
        let rTag (n: string) =
            if (intEnumKeysOfTag env n).IsSome then Some (IRTIdxTagged (IRTScalar ETInt64, IRefNamed n)) else None
        let seedOf (o: Expr) =
            match o.Kind with
            | ExprKind.ExprRange [ TyNamed (n, []) ] -> rTag n
            | ExprKind.ExprVar v ->
                (match lookupVar v env with
                 | Some vi ->
                     (match IR.stripUnits (env.Subst.Resolve vi.Type) with
                      | ArrayElem at when at.IndexTypes.Length = 1 ->
                          (match IR.stripUnits (env.Subst.Resolve at.ElemType) with
                           | IntTaggedIndex n -> rTag n
                           // G110 (round 4 wave 4 lane A; formalism
                           // 2.4's partner rule): a NARROW numeric
                           // cell seeds its parameter too, so a
                           // literal partner in the body takes the
                           // operand's width (`lambda(v) -> v * v +
                           // 1.0` over Float32 is Float32). Left open,
                           // the literal typed at its class's wide
                           // width and fixed the kernel's result
                           // there. The wide widths are what an open
                           // partner already gives, so they are not
                           // seeded (no change for them).
                           | IRTScalar (ETFloat32 | ETInt32 | ETComplex64) -> Some (env.Subst.Resolve at.ElemType)
                           | _ -> None)
                      | _ -> None)
                 | None -> None)
            | _ -> None
        let seeds = operands |> List.map seedOf
        // G114: an operand the G110 seed does not reach (a rank >= 2
        // array, an operand expression) HINTS its narrow element: the
        // parameter stays open -- its body decides cell or row -- and
        // only its literal partners take the hinted width.
        let hints =
            List.zip operands seeds |> List.map (fun (o, s) ->
                match s with
                | Some _ -> None
                | None -> operandElemHint env o)
        if seeds |> List.exists Option.isSome || hints |> List.exists Option.isSome then
            Some (inferLambdaHinted env parms wc body seeds hints)
        else None
    | Some operands, ExprKind.ExprApp (func, args)
            when not args.IsEmpty && not (args |> List.exists _.Kind.IsExprWildcard) ->
        // A prefix partial used as a scalar kernel receives its
        // residual slots from the loop's cells, before its supplied
        // literal arguments settle the generic call in its body.
        match inferExpr env func with
        | Ok tFunc ->
            match env.Subst.Resolve tFunc.Type with
            | FuncElem (ps, ret) when args.Length < ps.Length
                                     && ps.Length - args.Length = operands.Length
                                     && not (ps |> List.exists (fun p -> (env.Subst.Resolve p).IsIRTPoly))
                                     && (tryFillDefaultArgs env right.Span func args).IsNone ->
                let residual = ps |> List.skip args.Length
                let scalarSlot p =
                    match IR.stripUnits (env.Subst.Resolve p) with
                    | IRTScalar _ -> true
                    | IRTInfer v -> (env.Subst.GetArityConstraint v).IsNone
                    | _ -> false
                if not (residual |> List.forall scalarSlot) then None
                else
                    match operands |> List.map (inferExpr env) |> sequenceResults with
                    | Error e -> Some (Error e)
                    | Ok os ->
                        let seeds = os |> List.map (fun o ->
                            match env.Subst.Resolve o.Type with
                            | ArrayElem a -> Some a.ElemType
                            | _ -> None)
                        if seeds |> List.forall Option.isSome then
                            Some (inferPrefixPartial env func args tFunc ps ret seeds)
                        else None
            | _ -> None
        | Error _ -> None
    | _ -> None

and inferArrayProdOp (env: TypeEnv) (left: Expr) (right: Expr) (mode: BinOpMode) (op: BinOp) : TypeResult<TypedExpr> =
    // <*> : MethodLoop x MethodLoop -> MethodLoop (concatenate array lists)
    inferExpr env left |> Result.bind (fun tL ->
    inferExpr env right |> Result.bind (fun tR ->
        // Resolve both sides to find TExprMethodFor
        let rL = resolveTypedExpr env tL
        let rR = resolveTypedExpr env tR
        match rL.Kind, rR.Kind with
        | TExprMethodFor m1, TExprMethodFor m2 ->
            // Merge into single TExprMethodFor with concatenated arrays
            let merged : TypedMethodForInfo = {
                Arrays = m1.Arrays @ m2.Arrays
                Identities = m1.Identities @ m2.Identities
                ArrayTypes = m1.ArrayTypes @ m2.ArrayTypes
                SDimsPerArray = m1.SDimsPerArray @ m2.SDimsPerArray
                TotalSDims = m1.TotalSDims + m2.TotalSDims
                SharedIndexTypes = []
            }
            let loopTy = IRTLoop {
                Kind = LKMethod
                Arity = Some (m1.Arrays.Length + m2.Arrays.Length)
                ArrayTypes = (m1.ArrayTypes @ m2.ArrayTypes) |> List.map mkArrayLike
                KernelType = None
            }
            Ok (mkTyped (TExprMethodFor merged) loopTy)
        | _ ->
            // Fallback: produce BinOp for non-method_for operands
            let arity =
                match tL.Type, tR.Type with
                | IRTLoop l1, IRTLoop l2 -> 
                    match l1.Arity, l2.Arity with
                    | Some a, Some b -> Some (a + b)
                    | _ -> None
                | _ -> None
            let loopTy = IRTLoop {
                Kind = LKMethod; Arity = arity
                ArrayTypes = []; KernelType = None
            }
            Ok (mkTyped (TExprBinOp (mode, op, tL, tR)) loopTy)))

// <|:> allocated-fallback (formalism 2.6): read A where its STORAGE holds
// the cell, else B. Storage-keyed, unlike <|>'s value-keyed zero test --
// an allocated zero survives fallback but not choice.
//   * compound-left: the CompoundIdx mask IS the allocation record (absent
//     cells have no storage). Result = the dense expansion: B's type, with
//     A overlaid on present cells. B must be dense over the compound's
//     underlying dims (+ trailing dims); extent agreement vs the runtime
//     mask is guarded in generated code.
//   * dense-left: allocation = the nested-pointer chain, checked per curry
//     level in codegen (nullptr-robust reads; compiler-built arrays are
//     fully allocated -- partially-allocated arrays arrive via the
//     C++-level partial-depth allocation API). Result = A's type.
// Scalars/computations/loop objects reject (steer to <|>); symmetric left
// rejects (symmetric allocation is not verifiable).
and inferFallbackOp (env: TypeEnv) (left: Expr) (right: Expr) : TypeResult<TypedExpr> =
    inferExpr env left |> Result.bind (fun tL ->
    inferExpr env right |> Result.bind (fun tR ->
        let describe (t: IRType) =
            match env.Subst.Resolve t with
            | ArrayElem _ -> "an array"
            | IRTScalar _ -> "a scalar"
            | IRTComputation _ -> "a computation"
            | IRTLoop _ -> "a loop object"
            | _ -> "a non-array value"
        let isPlainDense (a: IRArrayType) =
            a.IndexTypes |> List.forall (fun ix ->
                ix.IxKind = IxKPlain && ix.Symmetry = SymNone && ix.Rank <= 1)
        let hasSym (a: IRArrayType) =
            a.IndexTypes |> List.exists (fun ix -> ix.Symmetry <> SymNone)
        match env.Subst.Resolve tL.Type, env.Subst.Resolve tR.Type with
        | ArrayElem aL, ArrayElem aR ->
            if hasSym aL then Error (FallbackSymmetricLeft ())
            elif not (isPlainDense aR) then
                Error (FallbackRightNotDense
                        (if hasSym aR then "a symmetric array" else "a compound/non-dense array"))
            else
                (match aL.IndexTypes with
                 | head :: trailing when head.IxKind = IxKCompound ->
                     // Compound-left: B spans the mask's underlying dims
                     // plus A's trailing regular dims.
                     let leftSpan = head.Rank + trailing.Length
                     let rightRank = aR.IndexTypes.Length
                     if rightRank <> leftSpan then
                         Error (FallbackRankMismatch (leftSpan, rightRank))
                     else
                         unify env.Subst aL.ElemType aR.ElemType
                         |> Result.map (fun () ->
                             mkTyped (TExprFallback (tL, tR)) tR.Type)
                 | _ when isPlainDense aL ->
                     // Dense-left: same index space required outright.
                     unify env.Subst tL.Type tR.Type
                     |> Result.map (fun () ->
                         mkTyped (TExprFallback (tL, tR)) tL.Type)
                 | head :: _ when head.IxKind = IxKSparse ->
                     // Sparse-left is deferred: <|:>'s compound rule requires
                     // a dense right operand covering the WHOLE product
                     // space, which is well-defined for a mask over a grid
                     // but has no canonical shape for an arbitrary key set
                     // (the keys' bounding box is not part of the type).
                     Error (NotYetSupportedErr "<|:> with a SparseIdx left operand is not yet supported (a sparse key set has no product-space shape for the dense fallback to cover).")
                 | _ ->
                     Error (BuiltinArgErr "<|:> left operand must be a plain dense array or a compound(A, mask) array; ragged/dynamic-compound left operands are not supported."))
        | _ ->
            Error (FallbackNeedsArrays (describe tL.Type, describe tR.Type))))

and inferComposeOp (env: TypeEnv) (left: Expr) (right: Expr) (op: BinOp) : TypeResult<TypedExpr> =
    inferExpr env left |> Result.bind (fun tL ->
    inferExpr env right |> Result.bind (fun tR ->
        // f >> g : (A -> B) >> (B -> C) = (A -> C)
        // Each operand that names a DECLARED generic function is judged
        // at an INSTANTIATED copy of its signature, as at every call: the
        // composition is a use, and unifying the declaration's own
        // variables pinned `idg` to Float64 for every later caller after
        // `sq >> idg` (docs/plans/plan-call-judgment.md section 9, F7).
        // IR monomorphization treats a composition operand as a call site
        // (IRMono.hmValueRefRewrite), so the use still gets its spec.
        let instOperand (t: TypedExpr) =
            match t.Kind with
            | TExprVar _ ->
                let quantified, _ = calleeQuantifier env t
                let tys, _ = instantiateOpenVars env.Subst quantified [t.Type]
                List.head tys
            | _ -> t.Type
        let lInst, rInst = instOperand tL, instOperand tR
        match env.Subst.Resolve lInst, env.Subst.Resolve rInst with
        | FuncElem (fArgs, fRet), FuncElem (gArgs, gRet) ->
            // f's return is g's argument: they must agree (the unify
            // result used to be discarded, so `(Float -> Float) >>
            // (String -> String)` type-checked and died in g++).
            let gIn = match gArgs with [gArg] -> gArg | _ -> IRTTuple gArgs
            match unify env.Subst fRet gIn with
            | Ok () ->
                // (F2: g's projections of the value f hands it)
                settleOpenProjections env tL
                settleOpenProjections env tR
                // The use fixes each operand's instance: judge a generic
                // operand's casts there (genericValueObligationClash).
                let obligation =
                    [ tL, lInst; tR, rInst ] |> List.tryPick (fun (t, inst) ->
                        genericValueObligationClash env t (env.Subst.Resolve inst)
                        |> Option.map (fun e -> (t, e)))
                match obligation with
                | Some (t, e) ->
                    if t.Span.StartLine > 0 then setCurrentExprSpan t.Span
                    Error e
                | None -> Ok (mkTyped (TExprCompose (op, tL, tR)) (mkFuncArrow fArgs gRet))
            | Error _ -> Error (TypeMismatch (env.Subst.Resolve gIn, env.Subst.Resolve fRet))
        // `f >> g` APPLIES g to f's result, so a right operand that is no
        // function (`f >> 3.0`) is the refusal any application of a
        // non-function gets (BL3003). It used to print a raw warning and
        // succeed, and g++ then rejected the composition (BL9002).
        | FuncElem _, (IRTInfer _ | IRTUnitAnnotated (IRTInfer _, _)) ->
            Ok (mkTyped (TExprCompose (op, tL, tR)) tR.Type)
        | FuncElem _, rt ->
            if right.Span.StartLine > 0 then setCurrentExprSpan right.Span
            Error (InvalidApplication rt)
        | _, FuncElem (gArgs, gRet) ->
            // f might be a fresh/unresolved type -- permissive
            Ok (mkTyped (TExprCompose (op, tL, tR)) (mkFuncArrow gArgs gRet))
        | _ ->
            // Both unresolved -- permissive, return fresh
            Ok (mkTyped (TExprCompose (op, tL, tR)) (env.Subst.Fresh()))))

// 10b. <@> Application with Symmetry Analysis

and etaExpandFunctionKernel (env: TypeEnv) (kernelExpr: Expr) : TypeResult<TypedExpr> option =
    etaExpandFunctionKernelSeeded env kernelExpr []

/// `etaExpandFunctionKernel` with the wrapper's parameters SEEDED (R3: the R
/// keys of the operands a NAMED function kernel meets, so the call in the
/// wrapper's body hands it keys). Only the named-function arm reads the
/// seeds; an empty list is the unseeded wrapper exactly.
and etaExpandFunctionKernelSeeded (env: TypeEnv) (kernelExpr: Expr) (seeds: IRType option list) : TypeResult<TypedExpr> option =
    match kernelExpr.Kind with
    // A MODULE-QUALIFIED named function (`A.f`, `U.f` after `import A as U`)
    // is a named function wherever the bare name is one: the import binds it
    // as the variable `A.f`, so it takes the bare-name arm below under that
    // name (D11 follow-up).
    | ExprKind.ExprField ({ Kind = ExprKind.ExprVar n }, field)
            when (lookupVar n env).IsNone && (lookupVar $"{n}.{field}" env).IsSome ->
        etaExpandFunctionKernelSeeded env { kernelExpr with Kind = ExprKind.ExprVar $"{n}.{field}" } seeds
    // A unary intrinsic in kernel position: `abs <@> u`, `object_for(sqrt)`.
    // The intrinsics are call-shaped SYNTAX rather than values -- nothing binds
    // the name, so the lookupVar arm below cannot see one and the fall-through
    // reports it as an unbound variable. Wrapping it as lambda(__k) -> abs(__k)
    // puts the body back on the plain-call arms, so their typing, unit rules,
    // complex overloads and derivative rules all apply unchanged. Arity is 1 by
    // construction (isUnaryIntrinsic), so there is no signature to read it from.
    // The unbound guard is the same shadowing rule those arms use: a user
    // `function abs(...)` wins and takes the ordinary named-function path.
    | ExprKind.ExprVar name when isUnaryIntrinsic name && (lookupVar name env).IsNone ->
        let uid = env.Builder.FreshId()
        let pname = $"__k{uid}_0"
        let lamParams : LambdaParam list = [ { Name = pname; Type = None; Default = None; NameSpan = noSpan } ]
        let bodyApp =
            inheritSpan kernelExpr
                (ExprApp (kernelExpr, [ inheritSpan kernelExpr (ExprVar pname) ]))
        Some (if List.exists Option.isSome seeds then inferLambdaHinted env lamParams None bodyApp seeds [] else inferLambda env lamParams None bodyApp)
    // A BINARY intrinsic in kernel position: `method_for(zip(Y, X)) <@> atan2`,
    // `object_for(log_base)`. Same construction as the unary arm one arity up;
    // arity is 2 by construction (isBinaryIntrinsic), which is exactly why the
    // binary names are kept OUT of isUnaryIntrinsic -- that predicate is read
    // as "arity 1" and would build a one-parameter wrapper here.
    | ExprKind.ExprVar name when isBinaryIntrinsic name && (lookupVar name env).IsNone ->
        let uid = env.Builder.FreshId()
        let pnames = [ $"__k{uid}_0"; $"__k{uid}_1" ]
        let lamParams : LambdaParam list =
            pnames |> List.map (fun n -> { Name = n; Type = None; Default = None; NameSpan = noSpan })
        let bodyApp =
            inheritSpan kernelExpr
                (ExprApp (kernelExpr, pnames |> List.map (fun n -> inheritSpan kernelExpr (ExprVar n))))
        Some (if List.exists Option.isSome seeds then inferLambdaHinted env lamParams None bodyApp seeds [] else inferLambda env lamParams None bodyApp)
    | ExprKind.ExprVar name ->
        match lookupVar name env with
        // TypedValue = None is exactly the case resolveTypedExpr cannot turn
        // into a lambda: a top-level `function` (bindVarSimple) or a
        // function-typed parameter. A let-bound `lambda` carries
        // TypedValue = Some and MUST keep its existing resolve-at-apply path
        // (eta-wrapping it would turn the lambda into a std::function capture
        // and break compose chains like `object_for(f) >>@ object_for(g)`).
        | Some info when Option.isNone info.TypedValue ->
            match env.Subst.Resolve info.Type with
            | FuncElem (paramTys, retTy)
                    when not paramTys.IsEmpty
                         && not (paramTys |> List.exists (fun t -> (env.Subst.Resolve t).IsIRTPoly)) ->
                let uid = env.Builder.FreshId()
                let names = paramTys |> List.mapi (fun i _ -> $"__k{uid}_{i}")
                // WIDTH SCHEMA carry-over (docs/plan-tuples-vs-arg-packs.md
                // 6b): the wrapper's params are 1:1 with the callee's, so a
                // callee parameter declared `Tuple<k>` (or `(T1,..,Tk)`) must
                // reach the apply seam still declaring width k -- otherwise
                // `object_for(addPair) <@> (A, B)` reads as one unannotated
                // param over a 2-pack and lands on 5.2's annotation demand,
                // while the identical lambda spelling works. Re-spelling it
                // `Tuple<k>` (rather than copying the IRType) keeps the ONE
                // rule that a width is always WRITTEN.
                let lamParams =
                    List.map2 (fun n pTy ->
                        let annot =
                            match env.Subst.Resolve pTy with
                            | IRTTuple ts when ts.Length >= 2 -> Some (TyTupleWidth ts.Length)
                            | _ -> None
                        { Name = n; Type = annot; Default = None; NameSpan = noSpan } : LambdaParam)
                        names paramTys
                let bodyApp =
                    inheritSpan kernelExpr
                        (ExprApp (kernelExpr, names |> List.map (fun n -> inheritSpan kernelExpr (ExprVar n))))
                Some (
                    (if List.exists Option.isSome seeds then inferLambdaHinted env lamParams None bodyApp seeds []
                     else inferLambda env lamParams None bodyApp)
                    |> Result.bind (fun tLam ->
                        // Pin the wrapper's param types to the callee's
                        // signature -- an INSTANTIATED copy of it, as at every
                        // call and at the partial-application eta arms. Pinning
                        // the DECLARATION's own variables bound a generic
                        // callee to this one kernel use: `method_for(B) <@>
                        // mean` over Float64 rows made `mean(int_row)` anywhere
                        // after it a BL3001 against `Array<Float64 ..>` (and a
                        // call before it a g++ error against the one emitted
                        // `double mean(Array<double,1>)`).
                        let quantified, _ =
                            calleeQuantifier env (mkTyped (TExprVar (name, info.VarId, None)) info.Type)
                        let pinTys, _ = instantiateOpenVars env.Subst quantified (paramTys @ [retTy])
                        // R3: a SEEDED wrapper is not pinned -- its parameter
                        // is the R value the loop hands it, and the call in its
                        // body hands the callee the KEY (a generic callee is
                        // instantiated at Int64), so the wrapper is no instance
                        // of the callee's arrow. The call judgment has already
                        // typed that call against the callee's signature.
                        (if List.exists Option.isSome seeds then Ok ()
                         else
                            unify env.Subst tLam.Type
                                (mkFuncArrow (List.truncate paramTys.Length pinTys) (List.last pinTys)))
                        |> Result.map (fun () ->
                            // Surface the callee's `where` clause onto the wrapper
                            // lambda. Params are 1:1 with the callee's, so:
                            //   - comm-group INDICES carry over unchanged. This is
                            //     what makes `object_for(f)` / `method_for(..) <@> f`
                            //     for a commutative NAMED function compact to SymIdx.
                            //   - parallel strategies carry over, but an `omp(a: n)`
                            //     var is resolved by NAME downstream
                            //     (Lowering.extractParallelism), and `names` above
                            //     renamed every param to `__k<uid>_<i>` -- so remap
                            //     each var through the callee's param list by
                            //     position. Without this the whole clause is lost
                            //     and the loop nest is emitted serial, silently.
                            let withComm (li: TypedLambdaInfo) =
                                match env.FuncCommGroups.TryGetValue info.VarId with
                                | true, cg when not (List.isEmpty cg) ->
                                    { li with CommGroups = cg; IsCommutative = true }
                                | _ -> li
                            // Same for `where anticomm(...)`: param indices carry
                            // over 1:1, and without this the clause is dropped by
                            // the eta wrapper and `object_for(f) <@> (A, A)` for a
                            // declared-antisymmetric function silently falls back
                            // to dense storage.
                            let withAntisym (li: TypedLambdaInfo) =
                                match env.FuncAntisymGroups.TryGetValue info.VarId with
                                | true, ag when not (List.isEmpty ag) ->
                                    { li with AntisymGroups = ag }
                                | _ -> li
                            let withParallel (li: TypedLambdaInfo) =
                                match env.FuncParallel.TryGetValue info.VarId with
                                | true, (calleeNames, strategies) when not (List.isEmpty strategies) ->
                                    { li with Parallel = remapParallelVars calleeNames names strategies }
                                | _ -> li
                            match tLam.Kind with
                            | TExprLambda li ->
                                { tLam with Kind = TExprLambda (li |> withComm |> withAntisym |> withParallel) }
                            | _ -> tLam)))
            | _ -> None
        | Some _ -> None   // let-bound value (lambda etc.): use the existing path
        | None -> None
    | _ -> None

and inferApply (env: TypeEnv) (tLeft: TypedExpr) (tRight: TypedExpr) : TypeResult<TypedExpr> =
    let rL = resolveTypedExpr env tLeft
    let rR = resolveTypedExpr env tRight

    match rL.Kind, rR.Kind with
    | TExprMethodFor mfInfo, TExprLambda lambdaInfo ->
        buildApplyInfo env mfInfo.Arrays mfInfo.Identities mfInfo.ArrayTypes mfInfo.SDimsPerArray mfInfo.SharedIndexTypes lambdaInfo tLeft tRight false false

    | TExprMethodFor mfInfo, TExprSection op ->
        // Synthesize a TypedLambdaInfo from the operator section
        let aId = env.Builder.FreshId()
        let bId = env.Builder.FreshId()
        let isComm = match op with
                     | OpAdd | OpMul | OpEq | OpNeq | OpAnd | OpOr -> true
                     | _ -> false
        let paramTy = IRTScalar ETFloat64
        let lambdaInfo : TypedLambdaInfo = {
            Params = [{ Name = "a"; Type = paramTy; Index = 0; VarId = aId; Default = None; NameSpan = noSpan }
                      { Name = "b"; Type = paramTy; Index = 1; VarId = bId; Default = None; NameSpan = noSpan }]
            Body = mkTyped (TExprBinOp (Elementwise, op,
                      mkTyped (TExprVar ("a", aId, None)) paramTy,
                      mkTyped (TExprVar ("b", bId, None)) paramTy)) paramTy
            ReturnType = paramTy
            CommGroups = if isComm then [[0; 1]] else []
            AntisymGroups = []   // synthesized (operator section): no user clause
            SignParities = []    // populated at the apply seam, if it gets there
            Captures = []; IsCommutative = isComm
            Parallel = []  // synthesized (operator section): no user clause
            SelfBinding = None  // anonymous: cannot self-reference
        }
        buildApplyInfo env mfInfo.Arrays mfInfo.Identities mfInfo.ArrayTypes mfInfo.SDimsPerArray mfInfo.SharedIndexTypes lambdaInfo tLeft tRight false false

    | TExprMethodFor mfInfo, TExprReynolds (innerKernel, isReynoldsAntisym) ->
        let resolvedInner = resolveTypedExpr env innerKernel
        match resolvedInner.Kind with
        | TExprLambda li -> buildApplyInfo env mfInfo.Arrays mfInfo.Identities mfInfo.ArrayTypes mfInfo.SDimsPerArray mfInfo.SharedIndexTypes li tLeft tRight true isReynoldsAntisym
        | _ -> Error (BuiltinArgErr "reynolds() requires a lambda kernel, but the inner expression could not be resolved to a lambda")

    | TExprMethodFor mfInfo, TExprZero ->
        inferApplyMethodForZero env mfInfo tLeft

    | TExprObjectFor objInfo, _ when
        objInfo.Kernel.Kind.IsTExprSection ->
        inferApplyObjectForSection env objInfo rR tRight

    | TExprObjectFor objInfo, _ ->
        inferApplyObjectFor env tRight objInfo tLeft

    | TExprCompose (OpComposeObj, _, _), _ ->
        inferApplyComposeObj env tRight rL tLeft

    | _ ->
        // Name the real culprit. When the LEFT already is a valid
        // method_for/object_for, the unmatched operand is the RIGHT (the
        // kernel) -- reporting the left here would produce the misleading
        // "requires method_for ... but got TExprMethodFor" red herring. With
        // implicit formers (inferBinOp's OpApply normalization), reaching
        // this catch-all with a former-less left means NEITHER side was
        // decisive -- steer instead of demanding a former blind.
        let describeKind (k: TypedExprKind) =
            match k with
            | TExprVar (name, _, _) -> $"variable '{name}'"
            | _ -> k.GetType().Name.Replace("TExpr", "")
        match rL.Kind with
        | TExprMethodFor _ | TExprObjectFor _ ->
            Error (ChainOpBadKernel (describeKind rR.Kind))
        | _ ->
            Error (ChainOpUndecidable (describeKind rL.Kind, describeKind rR.Kind))

// object_for(<combinator>) <@> (c1, c2, ...) -> left-fold or map+combine
//
// DELIBERATELY EXEMPT from the pack spine expansion the two arms below use
// (docs/plan-tuples-vs-arg-packs.md 3.10). This is not a kernel pack at
// all: the elements are COMPUTATIONS being folded, and
// `object_for(<@>) <@> ((L1, f1), (L2, f2))` reads each element as a
// (loop, kernel) PAIR -- deep-flattening would destroy exactly the nesting
// this arm requires. It keeps the one-level shallow match on purpose.
and inferApplyObjectForSection (env: TypeEnv) (objInfo: TypedObjectForInfo) (rR: TypedExpr) (tRight: TypedExpr) : TypeResult<TypedExpr> =
    let op = match objInfo.Kernel.Kind with TExprSection op -> op | _ -> OpAdd
    // Extract elements from right side
    let elems = match rR.Kind with
                | TExprTuple es -> es
                | _ -> [tRight]
    if elems.Length < 2 then
        Error (ArgCountErr "object_for(<combinator>) requires at least 2 arguments")
    else
        match op with
        | OpApply ->
            // object_for(<@>) <@> ((L1, f1), (L2, f2), ...)
            // Apply <@> to each (loop, kernel) pair, return tuple of computations
            let pairs = elems |> List.map (fun e ->
                match e.Kind with
                | TExprTuple [loop; kernel] -> Ok (loop, kernel)
                | _ -> Error (BuiltinArgErr "object_for(<@>) expects (loop, kernel) pairs"))
            pairs |> sequenceResults |> Result.bind (fun pairList ->
                pairList |> List.map (fun (loop, kernel) -> inferApply env loop kernel)
                |> sequenceResults
                |> Result.map (fun computations ->
                    let types = computations |> List.map (_.Type)
                    mkTyped (TExprTuple computations) (IRTTuple types)))
        | OpFunctor ->
            // object_for(<$>) <@> (f, c)  or  (f1, f2, ..., c)
            // Right-fold: f1 <$> (f2 <$> (... <$> c))
            let funcs : TypedExpr list = elems |> List.take (elems.Length - 1)
            let comp : TypedExpr = elems |> List.last
            // Same record copy, and so the same compact-class inheritance
            // check, as the binary `<$>` arm -- each fold step is one
            // `f <$> acc`.
            let applyFmap (accR: TypeResult<TypedExpr>) (f: TypedExpr) : TypeResult<TypedExpr> =
                accR |> Result.bind (fun acc ->
                    match functorMapInheritError env f acc with
                    | Some e -> Error e
                    | None ->
                        let outputType = functorMapOutputType env f acc
                        Ok (mkTyped (TExprFunctorMap (f, acc)) outputType))
            funcs |> List.rev |> List.fold applyFmap (Ok comp)
        | OpChoice ->
            // object_for(<|>) <@> (c1, c2, ...) -> left-fold producing TExprChoice
            let folder (acc: TypedExpr) (elem: TypedExpr) : TypedExpr =
                mkTyped (TExprChoice (acc, elem)) acc.Type
            Ok (elems |> List.tail |> List.fold folder (List.head elems))
        | OpFallback ->
            // object_for(<|:>) <@> (A1, A2, ...) -> left-fold of allocated-
            // fallback. Each intermediate is a fully-allocated dense array,
            // so only the FIRST operand's allocation (compound mask /
            // pointer chain) can defer to later ones -- later dense results
            // never fall through. That is the correct left-fold semantics.
            let folder (acc: TypedExpr) (elem: TypedExpr) : TypedExpr =
                mkTyped (TExprFallback (acc, elem)) elem.Type
            Ok (elems |> List.tail |> List.fold folder (List.head elems))
        | _ ->
            // Standard left-associative fold: (((c1 op c2) op c3) op ...)
            let folder (acc: TypedExpr) (elem: TypedExpr) =
                let resTy = 
                    match op with
                    | OpParallel | OpFusion -> IRTTuple [acc.Type; elem.Type]
                    | _ -> acc.Type
                let kind =
                    match op with
                    | OpParallel -> TExprParallel (acc, elem)
                    | OpFusion -> TExprFusion (acc, elem)
                    | _ -> TExprBinOp (Elementwise, op, acc, elem)
                mkTyped kind resTy
            Ok (elems |> List.tail |> List.fold folder (List.head elems))

// object_for <@> arrays: kernel-first application
// Preserves TExprObjectFor as the loop provenance (no synthetic TExprMethodFor)
// Detects zip() arguments and expands them into co-iteration groups
and inferApplyObjectFor (env: TypeEnv) (tRight: TypedExpr) (objInfo: TypedObjectForInfo) (tLeft: TypedExpr) : TypeResult<TypedExpr> =
    // ---- SPINE EXPANSION (docs/plan-tuples-vs-arg-packs.md 6c) ----
    // The KERNEL-side half of the one pack site (inferMethodFor has the
    // former-side half). Replaces the `resolveTypedExpr`-then-match-
    // TExprTuple splat, which took exactly ONE alias hop and so made
    // `K <@> P` and `let Q = P; K <@> Q` different programs (3.1, M1).
    // Nested nodes SURVIVE here and are matched against the schema in
    // buildApplyInfo's spine matcher.
    let rawExprs = packSpine env [tRight]
    // Resolve variables to detect indirect zip (let Z = zip(A,B); ... <@> Z)
    let resolvedExprs = rawExprs |> List.map (resolveTypedExprDeep env)

    // Check if ANY argument is a zip -- flatten zip children into co-iteration groups
    let hasZip = resolvedExprs |> List.exists _.Kind.IsTExprZip

    // GUARD: a zip as ONE operand of a MULTI-operand pack. The flattening
    // below splices the zip's children into the array pack as ordinary
    // operands, so `object_for(k) <@> (A, zip(B, C))` builds a THREE-way
    // outer product (|A|*|B|*|C| cells) instead of co-iterating B and C
    // over one axis -- silently, with a kernel arity that happens to match.
    // The method_for orientation of the same shape carries the zip to
    // codegen unmaterialized (undeclared `arr1`). Co-iterating a zip
    // inside an outer loop is not implemented; reject both orientations
    // rather than return the wrong grid. An all-zip pack
    // (`(zip(A,B), zip(C,D))`) is a plain co-iteration and stays legal.
    if hasZip && (resolvedExprs |> List.exists (fun e -> not e.Kind.IsTExprZip)) then
        Error (NotYetSupportedErr zipInMultiArrayPackMsg)
    else

    let (flatArrays, sharedRecords) =
        if hasZip then
            let mutable arrays : TypedExpr list = []
            let mutable isCoIterGroup : bool list = []
            for expr in resolvedExprs do
                match expr.Kind with
                | TExprZip children ->
                    arrays <- arrays @ children
                    isCoIterGroup <- isCoIterGroup @ (children |> List.map (fun _ -> true))
                | _ ->
                    arrays <- arrays @ [expr]
                    isCoIterGroup <- isCoIterGroup @ [false]
            // S1 SEAM 3, CO-ITERATION TWIN (cd0f202 left this open for lack
            // of a repro; `object_for(k) <@> zip(a, b)` inside a function
            // whose params are the caret shorthand is one). These records
            // become the apply's SharedIndexTypes, and two independent
            // degradations both ended in a rank-0 record and so in `recs = []`:
            //
            //   * the arity demand ran only at the `materializeArityVar`
            //     line further down, so a `T^k` operand was still an open
            //     var at THIS point and could not match `ArrayElem`;
            //   * the match read `a.Type` RAW, so an operand whose var the
            //     substitution had already bound to an array missed the
            //     `ArrayElem` arm regardless of the arity demand.
            //
            // Losing the shared records is not a compile error here -- it is
            // a SILENT OUTER PRODUCT, since buildApplyInfo reads no shared
            // axis and falls back to the full product grid.
            // `function f(a: T^1, b: T^1) =
            //  object_for(lambda(x, y) -> x * y) <@> zip(a, b)` returned the
            // 3x3 grid [[10,20,30],[20,40,60],[30,60,90]] where the concrete
            // twin returns the co-iterated [10,40,90]; at `T^2` it
            // returned a 2x3x2x3 grid for a 2x3 elementwise product.
            //
            // Demand the arity FIRST, so these records are minted from the
            // same extents `arrayTypes` reads below -- shared records built
            // from a DIFFERENT extent name than the operands carry emit a
            // loop bound nothing declares. The demand is idempotent (the
            // iteration at the bottom re-runs it over these same flattened
            // operands and finds them resolved). Then resolve through the
            // substitution, and fall back exactly as the non-zip path does.
            arrays |> List.iter (fun a -> materializeArityVar env a "map")
            arrays |> List.iter (fun a -> materializeGenericLoopOperand env a "map")
            let arrTypes = arrays |> List.map (fun a ->
                match env.Subst.Resolve a.Type with
                | ArrayElem at -> reSDimOperand at
                | _ -> objectForOperandFallback env a)
            let allCoIter = isCoIterGroup |> List.forall id
            let recs =
                if allCoIter then
                    // Shared records: the FULL common index shape when the
                    // operands agree and the shape is co-iterable (all plain
                    // dense, or a single packed record); a first-record-only
                    // fallback otherwise (buildApplyInfo's row-rank trim keeps
                    // row-mode kernels working either way).
                    match arrTypes with
                    | first :: rest when not first.IndexTypes.IsEmpty ->
                        let shape0 = first.IndexTypes
                        if rest |> List.forall (fun at -> indexShapesAgree at.IndexTypes shape0)
                           && coIterableRecords shape0 then shape0
                        else [shape0.Head]
                    | _ -> []
                else []
            (arrays, recs)
        else (rawExprs, [])

    let identities = flatArrays |> List.map (fun arr ->
        match arr.Kind with
        | TExprVar (name, vid, _) -> loopOperandIdentity env name vid
        | _ -> AIDLiteral (env.Builder.FreshId()))
    // S1 SEAM 3 (docs/plan-kernel-body-materialization.md, M-B): the
    // operand-shape fallback just below degrades anything that is not
    // already an IRTArray to a RANK-0 record, which types the whole apply
    // scalar. A caret-shorthand `T^k` operand (`lambda(r: T^1) -> exp <@> r`)
    // is an arity-constrained var, not an IRTArray, so it landed there and
    // the nested map's result was typed a scalar. Supply the shape the
    // arity constraint already forces -- see materializeArityVar.
    //
    // NAMED FUNCTION BODIES TOO, since requireArrayArgMinRank now carries
    // the polymorphic mark onto the synthesized element type. Binding `T`
    // here used to SPEND the declaration's HM-polymorphic signature var
    // (`function dbl(xs: T^1) = xs <@> lambda(x) -> x + x |> compute`
    // collapsed to its first call site's element type and dropped the
    // `_HM_` specializations from loops/115 and loops/116); with the mark,
    // the shape is supplied and the ELEMENT stays open, so the IR
    // monomorphizer still specializes per call site.
    flatArrays |> List.iter (fun arr -> materializeArityVar env arr "map")
    // ...and a generic declaration's UNANNOTATED parameter, which has no
    // caret to read (see materializeGenericLoopOperand).
    flatArrays |> List.iter (fun arr -> materializeGenericLoopOperand env arr "map")
    // S1 SEAM 3, second half: `materializeArityVar` covers the operand that
    // still WEARS the caret-shorthand var, but an array-valued INTERMEDIATE
    // built from one (`let wt = w * t` over a `t: T^1` the binop seam
    // left deferred) is a plain unresolved var, so the demand is a no-op and
    // the old rank-0 fallback typed the apply scalar. Fall back exactly as
    // the method_for orientation of this same apply already does -- see
    // objectForOperandFallback.
    let arrayTypes = flatArrays |> List.map (fun arr ->
        match env.Subst.Resolve arr.Type with
        | ArrayElem at -> reSDimOperand at
        | _ -> objectForOperandFallback env arr)
    // Real per-array S-dim counts in BOTH modes: the co-iteration case needs
    // them so buildApplyInfo's IRTInfer fallback computes the kernel slice
    // rank against the true array rank (a scalar kernel over rank-2 zips
    // must yield kR = 0 -> full-product co-iteration, not a mis-trim).
    let sDimsPerArray = computeSDimsPerArray arrayTypes

    // Resolve kernel and build ApplyInfo with object_for as provenance
    let resolvedKernel = resolveTypedExpr env objInfo.Kernel
    match resolvedKernel.Kind with
    | TExprLambda lambdaInfo ->
        buildApplyInfo env flatArrays identities arrayTypes sDimsPerArray sharedRecords lambdaInfo tLeft objInfo.Kernel false false
    | TExprReynolds (innerK, isReynoldsAntisym) ->
        let resolvedInnerK = resolveTypedExpr env innerK
        match resolvedInnerK.Kind with
        | TExprLambda li ->
            buildApplyInfo env flatArrays identities arrayTypes sDimsPerArray sharedRecords li tLeft objInfo.Kernel true isReynoldsAntisym
        | _ -> Error (BuiltinArgErr "reynolds() requires a lambda kernel, but the inner expression could not be resolved to a lambda")
    | TExprZero ->
        // object_for(zero) <@> arrays: synthesize zero-returning lambda.
        // Extract primitive for literal choice.
        // Option C: preserve the wrapper (IRTIdxTagged/IRTUnitAnnotated)
        // in paramTy so the synthesized lambda's param type unifies with
        // the iteration's yielded type. Extract only the inner primitive
        // for zeroLitForElem.
        let elemTypeIR =
            arrayTypes |> List.tryHead
            |> Option.map (_.ElemType)
            |> Option.defaultValue (IRTScalar ETFloat64)
        let elemType =
            match elemTypeIR with
            | AnyPrimElem et -> et
            | _ -> ETFloat64
        let paramTy =
            match elemTypeIR with
            | AnyPrimElem _ -> elemTypeIR
            | _ -> IRTScalar ETFloat64
        let zeroLit = zeroLitForElem env elemType
        let nArrays = flatArrays.Length
        let params_ = List.init nArrays (fun i ->
            let pid = env.Builder.FreshId()
            { Name = $"__z{i}"; Type = paramTy; Index = i; VarId = pid; Default = None; NameSpan = noSpan })
        let lambdaInfo : TypedLambdaInfo = {
            Params = params_
            Body = mkTyped zeroLit paramTy
            ReturnType = paramTy
            CommGroups = []
            AntisymGroups = []
            SignParities = []
            Captures = []; IsCommutative = true
            Parallel = []  // synthesized (object_for zero kernel): no clause
            SelfBinding = None  // anonymous: cannot self-reference
        }
        buildApplyInfo env flatArrays identities arrayTypes sDimsPerArray sharedRecords lambdaInfo tLeft (mkTyped (TExprLambda lambdaInfo) (IRTScalar elemType)) false false
    | TExprVar (fnName, fnId, _) when
        (match env.Subst.Resolve resolvedKernel.Type with
         | FuncElem (ps, _) ->
             ps |> List.exists (fun t -> (env.Subst.Resolve t).IsIRTPoly)
         | _ -> false) ->
        // Deferred former: a bare arity-polymorphic named kernel (e.g.
        // `object_for(comoment_generator)`). The pack arity is revealed HERE
        // by the argument tuple, so eta-expand now to that width and rebuild
        // the ordinary inline form `object_for(lambda(__e..) -> fn(__e..))`,
        // routing it through inferObjectFor + buildApplyInfo so the whole
        // existing lambda pipeline (arity + HM specialization) applies. Each
        // <@> use mints a fresh lambda, so multiple uses at different arities
        // or element types stay independent. The synthesized loop (not the
        // bare `tLeft` var) becomes the apply's loop provenance, keeping the
        // node self-contained after the deferred-former binding is emitted
        // inert (see the DeclLet / block-let binding sites).
        let span = objInfo.Kernel.Span
        let n = flatArrays.Length
        let uid = env.Builder.FreshId()
        let names = List.init n (fun i -> $"__of{uid}_{i}")
        let lamParams = names |> List.map (fun nm -> ({ Name = nm; Type = None; Default = None; NameSpan = noSpan } : LambdaParam))
        let bodyApp =
            mkExpr span (ExprApp (mkExpr span (ExprVar fnName),
                                  names |> List.map (fun nm -> mkExpr span (ExprVar nm))))
        let etaLoopExpr = mkExpr span (ExprLambda (lamParams, None, bodyApp))
        inferObjectFor env etaLoopExpr |> Result.bind (fun tLoopSynth ->
            match tLoopSynth.Kind with
            | TExprObjectFor synInfo ->
                match (resolveTypedExpr env synInfo.Kernel).Kind with
                | TExprLambda li ->
                    // Surface a poly kernel's `where comm(pack)` onto the
                    // eta-lambda. A comm over the whole pack makes all n
                    // expanded arguments ONE joint comm group; expand the
                    // pack-level group to the concrete arity revealed here.
                    let li' =
                        match env.FuncCommGroups.TryGetValue fnId with
                        | true, cg when not (List.isEmpty cg) ->
                            { li with CommGroups = [ [ 0 .. n - 1 ] ]; IsCommutative = true }
                        | _ -> li
                    // Same for the `where omp(...)` clause, which is otherwise
                    // dropped by the eta expansion (the wrapper is built with
                    // no where-clause) and the nest comes out serial. A poly
                    // kernel's omp var names the PACK, which expanded into the
                    // n params `__of<uid>_0..n-1`; the pragma gate only reads
                    // level 0, so map it onto the first expanded param.
                    let li'' =
                        match env.FuncParallel.TryGetValue fnId with
                        | true, (calleeNames, strategies)
                                when not (List.isEmpty strategies) && not (List.isEmpty names) ->
                            let packNames = calleeNames |> List.map (fun _ -> List.head names)
                            { li' with Parallel = remapParallelVars calleeNames packNames strategies }
                        | _ -> li'
                    // Stage-3 LATE TIER confirm-and-pin for PACK kernels:
                    // no declared comm, the pack deduces invariant at
                    // every arity (AC-fold template / wrapper walk), and
                    // the SAME array fills every expanded position --
                    // H  intersect  Stab would license compaction. Dense until the
                    // user pins `where comm(pack)` on the kernel.
                    (match env.FuncCommGroups.TryGetValue fnId with
                     | true, cg when not (List.isEmpty cg) -> ()
                     | _ ->
                         match env.PackDeducedComm.TryGetValue fnId with
                         | true, (packName, Blade.Deduce.PInv) when n >= 2 ->
                             let allSame =
                                 match identities with
                                 | [] -> false
                                 | first :: rest -> rest |> List.forall (sameIdentity first)
                             // A fold leaf stores nothing (see the pair-site
                             // note in buildApplyInfo and FoldOperandApplies).
                             let isFoldLeaf =
                                 FoldOperandApplies.contains tLeft.Span || FoldOperandApplies.contains tRight.Span
                             if allSame && not isFoldLeaf then
                                 let msg =
                                     $"kernel `{fnName}` deduces commutative over its argument pack `{packName}` (at every arity) and all {n} positions receive the same array: output storage is DENSE today. Pin `where comm({packName})` on `{fnName}` to opt into compact symmetric (triangular) storage."
                                 // BL4010 -- the confirm-and-pin storage
                                 // suggestion, same code and now the same
                                 // span the IDE channel carries.
                                 emitWarning env "BL4010" span msg
                                 PinSuggestions.add msg span
                         | _ -> ())
                    buildApplyInfo env flatArrays identities arrayTypes sDimsPerArray sharedRecords
                                   li'' tLoopSynth synInfo.Kernel false false
                | _ -> Error (ObjectForKernel "deferred former eta-expansion did not yield a lambda")
            | _ -> Error (ObjectForKernel "deferred former eta-expansion did not yield an object_for"))
    | _ -> Error (ObjectForKernel (resolvedKernel.Kind.GetType().Name))

// Composed ObjectLoop: (o1 >>@ o2) <@> A
and inferApplyComposeObj (env: TypeEnv) (tRight: TypedExpr) (rL: TypedExpr) (tLeft: TypedExpr) : TypeResult<TypedExpr> =
    // Typed by CHAINING each stage through the same deduction a direct
    // `stage <@> X` runs (recursive inferApply): stage 1 sees the real
    // operands (real identities), each later stage sees the previous
    // stage's OUTPUT TYPE as an anonymous intermediate. This gives the
    // composed pipeline the right element/index type out the end, and
    // runs every per-stage gate -- comm surfacing, and BL4015's
    // compact-class inheritance check -- that a composed kernel could
    // otherwise slip past entirely.
    //
    // The RESULT keeps the compose shape (Loop = the chain,
    // IsComposeApply = true): codegen's genComposeApply chases the chain
    // and emits one fused nest, so only the type and the recorded
    // identities improve here, not the runtime plan.
    // Same spine expansion as the two pack sites above (6c): a compose
    // chain must not disagree with a direct apply about how a tuple operand
    // opens.
    let arrayExprs = packSpine env [tRight]
    // Left-assoc chain -> ordered stages: Compose(Compose(o1,o2),o3) = [o1;o2;o3].
    let rec stagesOf (e: TypedExpr) : TypedExpr list =
        match (resolveTypedExpr env e).Kind with
        | TExprCompose (OpComposeObj, l, r) -> stagesOf l @ stagesOf r
        | _ -> [e]
    let chained =
        stagesOf rL
        |> List.fold
            (fun acc stage -> acc |> Result.bind (fun operand -> inferApply env stage operand))
            (Ok tRight)
    chained
    |> Result.bind (fun finalApplied ->
        let outputType = finalApplied.Type
        let info : TypedApplyInfo = {
            Loop = tLeft; Kernel = tRight
            Arrays = arrayExprs
            Identities = arrayExprs |> List.map (fun arr ->
                match (resolveTypedExpr env arr).Kind with
                | TExprVar (name, vid, _) -> loopOperandIdentity env name vid
                | _ -> AIDLiteral (env.Builder.FreshId()))
            ArrayTypes = arrayExprs |> List.map (fun a ->
                match a.Type with
                | ArrayElem at -> at
                | _ -> { ElemType = IRTScalar ETFloat64; IndexTypes = []; IsVirtual = false; Identity = None })
            SharedIndexTypes = []
            SymcomStates = []; TriangularLevels = []
            SDimsPerArray = []
            KernelInputRanks = []; KernelOutputRank = 0
            KernelTDims = []
            SpeedupFactor = 1L; ReynoldsSpeedup = 1L
            HasReynolds = false; OutputType = outputType
            IsCoIteration = false
            IsComposeApply = true
        }
        Ok (mkTyped (TExprApply info) outputType))

// 10c. Lambda, Let, Match, Block, MethodFor, ObjectFor, Struct, For

/// Pre-scan type annotations to collect type variable NAMES before lowering
/// (pass 1; pass 2 lowers types, creating inference vars lazily via
/// lowerTypeExpr). Two sources: explicit `T^N` syntax (TyVar, always a type
/// variable) and implicit bare `T` (TyNamed without args, a type variable
/// IFF the name isn't a registered type or builtin scalar) -- F#/OCaml-style
/// implicit polymorphism, so `function f(x: T) -> T = x` needs no `T^0`
/// annotation and composes with explicit `T^N` (`Poly<T^0>, T` is one `T`).
/// Recurses through all TypeExpr variants so names nested in
/// `Array<T like Idx<n>>`, `(T, U)`, `T -> U`, etc. are collected too.
and prescanTypeVarNames (env: TypeEnv) (types: TypeExpr option list) : unit =
    let rec scan ty =
        match ty with
        // A caret head naming a DECLARED type (`Speed^1`) is that type's
        // array, not a variable (TypeLower.isDeclaredCaretHead).
        | TyVar (name, _) when isDeclaredCaretHead env name -> ()
        | TyVar (name, _) ->
            env.Subst.RegisterTypeVarName(name)
        // CARET-FREE `T<u>` (owner ruling, 2026-08-09): the same head the
        // `TyAbstractArray` arm below claims, one caret shorter. Registered
        // here and its argument NOT scanned, for that arm's two reasons: the
        // args-empty rule would miss the head, and the argument is a UNIT, not
        // a type -- scanning it used to register `time` itself as a type-var
        // name.
        | TyNamed (name, args) when isUnitCarryingTypeVarHead env name args ->
            env.Subst.RegisterTypeVarName(name)
        | TyNamed (name, args) ->
            // F#/OCaml-style implicit type vars: a bare name (no args) that
            // isn't a registered type or builtin scalar is an implicit type
            // variable. The check uses lookupTypeDef against the current
            // env, so types declared earlier in the same module are
            // correctly recognized as concrete (and not registered as vars).
            // Forward references to types declared later remain unsupported
            // in Blade -- same convention as F#/OCaml.
            if args.IsEmpty
               && not (isBuiltinScalar name)
               && (lookupTypeDef name env).IsNone then
                env.Subst.RegisterTypeVarName(name)
            // Recurse into args regardless -- `Array<T like Idx<n>>` has
            // `T` in a nested position.
            args |> List.iter scan
        // `T<u>^k` (array-expression plan bug #8): the caret makes the head a
        // type VARIABLE even though it carries a unit argument, so the
        // args-empty rule above would miss it. The argument is a UNIT, not a
        // type, so it is deliberately not scanned.
        | TyAbstractArray (TyNamed (name, args), _, _)
                when not args.IsEmpty
                     && not (isBuiltinScalar name)
                     && (lookupTypeDef name env).IsNone ->
            env.Subst.RegisterTypeVarName(name)
        | TyAbstractArray (elemTy, _, _) -> scan elemTy
        | TyFunc (args, ret) -> args |> List.iter scan; scan ret
        | TyTuple ts -> ts |> List.iter scan
        | TyArray (elemTy, idxTys) -> scan elemTy; idxTys |> List.iter scan
        | TyPoly inner -> scan inner
        | TyConstrained (inner, _) -> scan inner
        | TyBounded (inner, _, _) -> scan inner
        | _ -> ()
    types |> List.iter (Option.iter scan)

/// One prefix-partial expansion for synthesis and contextual checking alike.
/// The latter seeds the residual parameter before literal bound arguments
/// are judged by the full call in its body.
and inferPrefixPartial (env: TypeEnv) (func: Expr) (args: Expr list) (tFunc: TypedExpr)
                       (paramTys: IRType list) (retTy: IRType) (seeds: IRType option list) : TypeResult<TypedExpr> =
    // A `mut` parameter left UNSUPPLIED by a partial application would
    // be filled through the residual closure, whose parameter is a
    // synthesized `__pa` name -- and a synthesized name is exactly what
    // the call-site write-permission check has to exempt, so the
    // permission would be lost at the seam rather than enforced. That
    // is not hypothetical: `f(w)(g)` mutated a plain `let g` through a
    // `mut` slot while the direct `f(w, g)` was refused. Currying a
    // write-back parameter is refused instead; supply it directly.
    let unsuppliedMut =
        match func.Kind with
        | ExprKind.ExprVar fname when not (fname.StartsWith "__") ->
            (match declIdOfName env fname |> Option.map (fun fid -> env.MutParamPositions.TryGetValue fid) |> Option.defaultValue (false, []) with
             | true, positions -> positions |> List.tryFind (fun p -> p >= args.Length)
                                  |> Option.map (fun p -> (fname, p))
             | _ -> None)
        | _ -> None
    match unsuppliedMut with
    | Some (fname, p) ->
        Error (MutArgNotPassable (fname, p + 1,
                "it is left unsupplied by a partial application, which would fill it through a "
                + "closure and lose the caller's write permission at that seam"))
    | None ->
    let residual = paramTys |> List.skip args.Length
    let uid = env.Builder.FreshId()
    let names = residual |> List.mapi (fun i _ -> $"__pa{uid}_{i}")
    let lamParams = names |> List.map (fun n -> { Name = n; Type = None; Default = None; NameSpan = noSpan } : LambdaParam)
    let bodyApp = inheritSpan func (ExprApp (func, args @ (names |> List.map (fun n -> inheritSpan func (ExprVar n)))))
    inferLambdaSeeded env lamParams None bodyApp seeds
    |> Result.bind (fun tLam ->
        // Pin the residual param types to the callee's declared
        // ones -- an INSTANTIATED copy of them, as at every call (the
        // body's call returns its own instance; unifying that with
        // the declaration's shared return would bind the declaration
        // to this one use). The call judgment never binds an argument
        // that is still open (the residual `__pa` params are), so
        // nothing else would resolve the lambda's fresh param vars.
        let quantified, _ = calleeQuantifier env tFunc
        let pinTys, _ = instantiateOpenVars env.Subst quantified (residual @ [retTy])
        unify env.Subst tLam.Type (mkFuncArrow (List.truncate residual.Length pinTys) (List.last pinTys))
        |> Result.map (fun () -> tLam))

and inferLambda env parms whereClause body : TypeResult<TypedExpr> =
    inferLambdaSeeded env parms whereClause body []

/// `inferLambda` with optional SEED types for unannotated parameters (by
/// position; None or a missing entry = a fresh inference variable, the
/// ordinary case). Used for a lambda literal applied directly, whose argument
/// types are known before its body is checked (ExprApp's lambda-head arm).
and inferLambdaSeeded env parms whereClause body (seeds: IRType option list) : TypeResult<TypedExpr> =
    inferLambdaFull env parms whereClause body seeds None

/// The general lambda judgment: `inferLambdaSeeded` plus an optional declared
/// RETURN annotation, lowered in the lambda's OWN type-variable scope (so the
/// `T` of `-> T` is the `T` of the parameters). A nested `function f(x: T) ->
/// T = ...` reaches here with its return type (inferLetBindingValueCore); it
/// used to lower that annotation in the ENCLOSING scope, where a bare `T` is
/// no variable at all -- it became the nominal type `T` and the parameter with
/// it (BL3001 "declared T but the argument is Float64").
and inferLambdaFull env parms whereClause body (seeds: IRType option list) (retAnnot: TypeExpr option) : TypeResult<TypedExpr> =
    inferLambdaFullHinted env parms whereClause body seeds [] retAnnot

/// `inferLambdaSeeded` plus ELEMENT HINTS (gap G114): by position, the
/// element a still-unseeded parameter's map operand delivers. The parameter
/// stays an open variable (its body decides whether it receives cells or
/// rows); the hint only fixes the width of its literal partners
/// (TypeCheckSupport.adaptLiteralOperands).
and inferLambdaHinted env parms whereClause body (seeds: IRType option list) (hints: ElemType option list) : TypeResult<TypedExpr> =
    inferLambdaFullHinted env parms whereClause body seeds hints None

and inferLambdaFullHinted env parms whereClause body (seeds: IRType option list) (hints: ElemType option list) (retAnnot: TypeExpr option) : TypeResult<TypedExpr> =
    let scopeEnv = enterCallableBody env
    // `repro` rides the emitted function boundary (noinline + contraction-off
    // attribute; call form everywhere) -- a lambda kernel is textually inlined
    // into its call sites, where none of that can travel, so accepting the
    // clause here would promise reproducibility the emission cannot keep.
    match whereClause with
    | Some wc when wc.Repro ->
        Error (WhereClauseErr "`where repro` is carried by named function declarations only (`function f(...) where repro = ...`): a lambda kernel inlines into its call sites, where the reproducibility attribute cannot travel. Name the kernel and call it.")
    | _ ->
    match parms |> List.tryPick (fun p -> p.Type |> Option.bind (caretHeadError env)) with
    | Some e -> Error e
    | None ->
    let commGroups = extractCommGroups parms whereClause
    let antisymGroups = extractAntisymGroups parms whereClause

    // Fresh type variable scope for this lambda's type annotations.
    let savedScope = env.Subst.PushTypeVarScope()

    // Pre-scan: collect type variable names from all annotations.
    prescanTypeVarNames env ((parms |> List.map (_.Type)) @ [ retAnnot ])

    let mutable paramEnv = scopeEnv
    // A NESTED `function`'s unannotated parameter is generic, exactly as a
    // top-level one is (checkFunctionDeclWith): `{ function idl(x) = x;
    // idl(2.0); idl(2) }` was BL3020 at the second call, the first having
    // pinned `x` to Float64.
    let genericUnannotated = (nestedFunctionBodies ()).Remove body
    let typedParams = parms |> List.mapi (fun i p ->
        let varId = env.Builder.FreshId()
        let ty = match p.Type with
                 | Some t -> lowerTypeExpr env t
                 | None ->
                     match List.tryItem i seeds with
                     | Some (Some seed) -> seed
                     | _ when genericUnannotated ->
                         let v = env.Subst.Fresh()
                         (match v with IRTInfer id -> env.Subst.MarkPolymorphic id | _ -> ())
                         v
                     | _ ->
                         let v = env.Subst.Fresh()  // Infer from usage
                         (match v, List.tryItem i hints with
                          | IRTInfer id, Some (Some h) -> env.Subst.SetElemHint(id, h)
                          | _ -> ())
                         v
        // WIDTH SCHEMA (docs/plan-tuples-vs-arg-packs.md 6c): record the
        // WRITTEN `Tuple<k>` width before the annotation is thrown away by
        // lowering. Keyed by binder id, so the matcher at buildApplyInfo reads
        // the declaration rather than the (by then already unified) type.
        declaredTupleWidth p.Type |> Option.iter (fun w ->
            env.DeclaredTupleWidths.[varId] <- w)
        paramEnv <- bindVarSimple p.Name varId ty paramEnv
        { Name = p.Name; Type = ty; Index = i; VarId = varId; Default = None; NameSpan = p.NameSpan } : TypedParam)
    // G275 (2): this lambda is a let's whole value (TypeEnv.PendingLetLambda,
    // consumed by the first lambda judged under that let): its open
    // parameters are the ones a later call decides.
    if env.PendingLetLambda.Value then
        env.PendingLetLambda.Value <- false
        for tp in typedParams do
            match tp.Type with
            | IRTInfer v -> env.LetLambdaParamVars.Add v |> ignore
            | _ -> ()

    // ---- Parameter defaults (BL3012 rules + typing) ----
    // Trailing rule, required-params-only scope rule, then each default
    // typed against its param's (possibly inferred) type with the params in
    // scope. The typed expr rides on TypedParam.Default for the kernel-apply
    // seam (buildApplyInfo binds an omitted trailing param as a body-entry
    // TExprLet over it); direct calls re-type the SURFACE default instead.
    let defaultsResult : TypeResult<TypedParam list> =
        match parms |> List.tryFindIndex (_.Default.IsSome) with
        | None -> Ok typedParams
        | Some fd ->
            let firstDefaultedName = (List.item fd parms).Name
            let orderErr =
                parms
                |> List.mapi (fun i p -> (i, p))
                |> List.tryPick (fun (i, p) ->
                    if i > fd && p.Default.IsNone
                    then Some (DefaultParamOrder ("lambda", p.Name, firstDefaultedName))
                    else None)
            match orderErr with
            | Some e -> Error e
            | None ->
                let defaultedNames =
                    parms |> List.filter (_.Default.IsSome) |> List.map (_.Name) |> Set.ofList
                let scopeErr =
                    parms |> List.tryPick (fun p ->
                        match p.Default with
                        | Some d ->
                            let bad = Set.intersect (collectFreeVars Set.empty d) defaultedNames
                            if Set.isEmpty bad then None
                            else Some (DefaultParamScope ("lambda", p.Name, Set.minElement bad))
                        | None -> None)
                match scopeErr with
                | Some e -> Error e
                | None ->
                // FACTORY rule (BL3013), lambda spelling: quantity-typed
                // defaulted slots must carry distinct quantities (see
                // checkFunctionDecl for the rationale).
                let dupQuantityErr =
                    List.zip typedParams parms
                    |> List.choose (fun (tp, p) ->
                        match p.Default with
                        | Some _ ->
                            (match IR.getUnits (env.Subst.Resolve tp.Type) with
                             | Some u -> u.Nominal |> Option.map (fun q -> (q, tp.Name))
                             | None -> None)
                        | None -> None)
                    |> List.groupBy fst
                    |> List.tryPick (fun (q, members) ->
                        match members with
                        | (_, p1) :: (_, p2) :: _ ->
                            Some (FactoryDupQuantityDecl ("lambda", q, p1, p2))
                        | _ -> None)
                match dupQuantityErr with
                | Some e -> Error e
                | None ->
                    List.zip typedParams parms
                    |> List.map (fun (tp, p) ->
                        match p.Default with
                        | Some d ->
                            checkExpr paramEnv tp.Type d
                            |> Result.map (fun td -> { tp with Default = Some td })
                        | None -> Ok tp)
                    |> sequenceResults

    let boundNames = parms |> List.map (_.Name) |> Set.ofList
    // A default's free variables are captures too: at the kernel-apply seam
    // the default is spliced into the BODY (a body-entry let), so a name it
    // reads from the enclosing scope must ride the capture list or the
    // lifted kernel would emit a dangling reference.
    let freeVars =
        let bodyFree = collectFreeVars boundNames body
        parms
        |> List.choose (_.Default)
        |> List.map (collectFreeVars boundNames)
        |> List.fold Set.union bodyFree
    let captures = buildCaptures scopeEnv freeVars
    // An `omp(v: n)` naming no parameter is silently dropped downstream; say so.
    checkOmpVarNames env (typedParams |> List.map (_.Name)) whereClause "this lambda"
    checkSymmetryVarNames env (typedParams |> List.map (_.Name)) whereClause "this lambda"

    let result =
        defaultsResult |> Result.bind (fun typedParams ->
        // Body typing runs with InLambdaBody set: params may still be
        // inference variables here, so unit checks at scalar position defer
        // provisional rejections to the kernel-apply second pass (see
        // typedExprHasUnresolvedType). Defaults above type WITHOUT the flag --
        // they are decl-time values and keep decl-time strictness.
        let parallelKernel =
            paramEnv.InParallelBody
            || (whereClause |> Option.map (fun w -> not w.Parallel.IsEmpty) |> Option.defaultValue false)
        inferExpr { paramEnv with InLambdaBody = true; InParallelBody = parallelKernel } body |> Result.bind (fun tBody ->
            // A lambda body is a value-forming boundary: reject a wildcard `_`
            // that escaped into it (its only legitimate role is a compound-index
            // coordinate), rather than letting it reach lowering.
            if exprContainsWildcard tBody then
                Error (WildcardErr
                    "wildcard `_` is not a value: it cannot be a lambda's body. It is only meaningful as a compound-index coordinate (e.g. B((a, _, c))).")
            else
            // #17: a parallel body may not reach an outer write through a call
            // either (the direct, lexical write is assignTargetError's).
            match (if parallelKernel then parallelCalleeWriteError env (typedParams |> List.map (_.VarId)) tBody else None) with
            | Some e -> Error e
            | None ->
            let retCheck =
                match retAnnot with
                | Some a -> unify env.Subst tBody.Type (lowerTypeExpr env a)
                | None -> Ok ()
            match retCheck with
            | Error e -> Error e
            | Ok () ->
            let info : TypedLambdaInfo = {
                Params = typedParams; Body = tBody; ReturnType = tBody.Type
                CommGroups = commGroups; AntisymGroups = antisymGroups; Captures = captures
                SignParities = []  // populated at the apply seam, if it gets there
                IsCommutative = not (List.isEmpty commGroups)
                // Propagate the lambda's parallelization strategy (omp/cuda) from
                // its where-clause so lambda-level omp drives parallelization.
                Parallel = (match whereClause with Some wc -> wc.Parallel | None -> [])
                // inferLambda always produces an anonymous lambda; a self-binding
                // (for a named recursive `let const`) is grafted on by inferBlock.
                SelfBinding = None
            }
            let funcTy = mkFuncArrow (typedParams |> List.map (_.Type)) tBody.Type
            Ok (mkTyped (TExprLambda info) funcTy)))

    env.Subst.PopTypeVarScope(savedScope)
    result

/// An array literal checked against a COMPACT index group -- `SymIdx<r, n>`,
/// `AntisymIdx<r, n>`, `HermitianIdx<n>`, r >= 2. Such a group is ONE index
/// slot spanning r dimensions; its STORED cells are the left-justified
/// simplex the allocator builds (`build_skeleton`): outer level n rows, a
/// row seeded at p carries n - p cells, seed threads p' = p + i + strict
/// (strict = 1 drops the diagonal, i.e. AntisymIdx). So `SymIdx<2, 3>` takes
/// `[[a00, a01, a02], [a11, a12], [a22]]`, `AntisymIdx<2, 3>` takes
/// `[[a01, a02], [a12], []]`; this walk checks the nesting level by level.
/// Leaves land in the pool in literal order (the allocator's DFS order), so
/// codegen fills straight through (genArrayLiteral's compact branch).
///
/// The FLAT canonical pool is NOT a second accepted spelling: a flat list
/// against a group whose extent is n (not its cardinality) is the wrong
/// length. Only an ANNOTATION reaches here -- an unannotated triangular
/// literal still infers the ragged type (the same brackets are legal
/// RaggedIdx data), so the annotation decides the class, never the shape.
/// An array literal checked against a CLOSED `RaggedIdx<lens>` annotation.
///
/// The lens is the one thing in a ragged annotation that construction does
/// NOT derive for itself. Codegen bakes `<name>_lens` and `<name>_offsets`
/// straight from the literal's own nesting (genArrayLiteral's ragged branch,
/// via computeRaggedRowLengths), and `IRRaggedLookup`'s payload -- the lens
/// reference -- has no consumer in CodeGen*/Interp at all. So a lens that
/// disagreed with the literal was accepted and then ignored: three separate
/// spellings of a wrong lens all compiled clean and all ran to the LITERAL's
/// shape. This is the seam that makes the two agree or say why not.
///
/// It is the ragged twin of a check codegen already runs for DepIdx, whose
/// inner extent FORMULA is evaluated per row and compared against the same
/// `computeRaggedRowLengths` (CodeGenLoopNest). Ragged does it in the front
/// end instead, because unlike a formula a lens is a value in SCOPE here.
///
/// A lens the compiler cannot hold is refused rather than dropped: honouring
/// one -- allocating to lengths only the running program knows -- is a
/// separate, planned change, and until it lands "ignored" is the only other
/// thing the annotation could mean.
and checkRaggedLensAgainstLit (env: TypeEnv) (arrTy: IRArrayType) (elems: Expr list) : TypeResult<unit> =
    // Only the two-slot form `Array<T like Idx<n>, RaggedIdx<lens>>` is
    // judged. That is the shape whose literal nests exactly twice and the one
    // the ragged literal emitter builds; with further prior axes the lens is
    // a FLATTENED companion over all of them, so its entries no longer stand
    // one-to-one against this literal's rows.
    match arrTy.IndexTypes with
    | [_outer; inner] when inner.IxKind = IxKRagged ->
        (match inner.Extent with
         | IRRaggedLookup lengths ->
             // The name the annotation wrote. `resolveRaggedLensSource` has
             // already declined anything synthesized, so a non-IRParam
             // payload here is a lens spelled some way this seam does not
             // read -- it resolves to RlRuntime and is reported as such.
             let lensName =
                 match lengths with
                 | IRParam (n, _, _) -> n
                 | _ -> "the declared lens"
             (match resolveRaggedLensSource env lengths with
              | RlRuntime -> Error (RaggedLensNotStatic lensName)
              | RlStatic declared ->
                  let rowLens =
                      elems |> List.map (fun e ->
                          match e.Kind with
                          | ExprKind.ExprArrayLit row -> Some (int64 row.Length)
                          | _ -> None)
                  // A row that is not itself a literal (a named array spliced
                  // in, say) has no length to read here; judge nothing rather
                  // than compare against a count that is not the one built.
                  if rowLens |> List.exists Option.isNone then Ok ()
                  else
                      let actual = rowLens |> List.map Option.get
                      if declared = actual then Ok ()
                      else
                          let fmt (xs: int64 list) =
                              "[" + (xs |> List.map string |> String.concat ", ") + "]"
                          Error (RaggedLensMismatch (lensName, fmt declared, fmt actual)))
         | _ -> Ok ())
    | _ -> Ok ()

and checkCompactArrayLit (env: TypeEnv) (arrTy: IRArrayType) (elems: Expr list) (litSpan: Span)
                         : TypeResult<TypedExpr> =
    let ix = arrTy.IndexTypes.Head
    let innerIdxs = arrTy.IndexTypes.Tail
    let idxName = ppIndexType ix
    let rank = ix.Rank
    let strict = if ix.Symmetry = SymAntisymmetric then 1 else 0
    let n = match ix.Extent with IRLit (IRLitInt v) -> int v | _ -> -1
    let width (seed: int) = max 0 (n - seed)
    let topName = "the literal"
    let childName (parent: string) (i: int) =
        if parent = topName then sprintf "row %d" i else $"{parent}.{i}"
    // The expected skeleton: the bracket picture when it fits on a line, else
    // the row-length recurrence it is a picture of.
    let shapeStr =
        let rec pic (depth: int) (seed: int) =
            let w = width seed
            if depth = rank - 1 then
                "[" + String.concat ", " (List.replicate w "_") + "]"
            else
                "[" + String.concat ", " [ for i in 0 .. w - 1 -> pic (depth + 1) (seed + i + strict) ] + "]"
        let p = if n >= 0 && n <= 8 then pic 0 0 else ""
        if p <> "" && p.Length <= 160 then $"write it as {p}"
        else
            sprintf "the outer level has %d rows, and a row seeded at coordinate p holds %d - p cells%s"
                n n (if strict = 1 then " (strict: the diagonal is dropped, so the last row is empty)" else "")
    let denseAxis (w: int) : IRIndexType =
        { Id = env.Builder.FreshId(); Rank = 1; Extent = IRLit (IRLitInt (int64 w))
          Symmetry = SymNone; Tag = None; IxKind = IxKPlain; Kind = SDimension; Dependencies = [] }
    // A Hermitian DIAGONAL cell must be real: A(i,i) = conj(A(i,i)), and the
    // stored diagonal cell is read unconjugated. Only a written-out
    // `complex(re, im)` with a non-zero literal imaginary part is judged here --
    // a computed cell carries no static verdict and passes.
    let rec nonZeroImagLit (e: Expr) =
        match e.Kind with
        | ExprKind.ExprLit (LitFloat v) -> v <> 0.0
        | ExprKind.ExprLit (LitInt v) -> v <> 0L
        | ExprKind.ExprUnaryOp (OpNeg, inner) -> nonZeroImagLit inner
        | _ -> false
    let isComplexWithImag (e: Expr) =
        match e.Kind with
        | ExprKind.ExprApp ({ Kind = ExprKind.ExprVar "complex" }, [_; im]) -> nonZeroImagLit im
        | _ -> false
    if not innerIdxs.IsEmpty then
        // A compact group followed by further axes: the leaves are themselves
        // arrays, so the literal no longer maps cell-for-cell onto the pool the
        // compact branch fills. Refuse here rather than accept a shape codegen
        // would have to bail on.
        Error (CompactLitShape (idxName, shapeStr, topName,
                                "is followed by further index axes, and only a literal whose leaves are \
ELEMENTS of the compact group is representable (build the wider array from a producer instead)"))
    elif n < 0 then
        Error (CompactLitShape (idxName, shapeStr, topName,
                                "needs a compile-time extent: the simplex row lengths, and so the literal's \
own shape, are not known without one"))
    else
    // Every Error below re-stamps the ambient span first: the walk's recursive
    // checkExpr calls move it to whatever leaf they last visited, so without
    // this a row-shape complaint would point at the previous row's last cell.
    let rec checkChildren (depth: int) (seed: int) (where_: string) (selfSpan: Span) (cs: Expr list)
                          : TypeResult<TypedExpr list> =
        let w = width seed
        if cs.Length <> w then
            setCurrentExprSpan selfSpan
            Error (CompactLitShape (idxName, shapeStr, where_,
                                    $"holds {cs.Length} cell(s), but the simplex row there is {w} wide"))
        else
            let isLeafLevel = (depth = rank - 1)
            let checkChild (i: int) (c: Expr) : TypeResult<TypedExpr> =
                let here = childName where_ i
                if isLeafLevel then
                    // i = 0 within a row is the diagonal cell (c_k = 0, so
                    // p' = p): the only cell a Hermitian class constrains.
                    if ix.Symmetry = SymHermitian && i = 0 && isComplexWithImag c then
                        setCurrentExprSpan c.Span
                        Error (HermitianLitDiagComplex $"the leading cell of {where_}")
                    else checkExpr env arrTy.ElemType c
                else
                    match c.Kind with
                    | ExprKind.ExprArrayLit gs ->
                        let childSeed = seed + i + strict
                        checkChildren (depth + 1) childSeed here c.Span gs
                        |> Result.map (fun tgs ->
                            let elemT = match tgs with t :: _ -> t.Type | [] -> arrTy.ElemType
                            let rowTy = { arrTy with ElemType = elemT
                                                     IndexTypes = [denseAxis (width childSeed)]
                                                     Identity = None }
                            mkTyped (TExprArrayLit (tgs, rowTy)) (mkArrayLike rowTy))
                    | _ ->
                        setCurrentExprSpan c.Span
                        Error (CompactLitShape (idxName, shapeStr, here,
                                                sprintf "is not a nested row: a rank-%d group takes %d levels \
of brackets, one per dimension of the group" rank rank))
            // Stop at the first bad cell rather than mapping the whole row and
            // taking the first Error out of the list: a later SUCCESSFUL child
            // would re-stamp the ambient span (checkExprInner does that on every
            // node), and the diagnostic would point one cell past the offender.
            let rec go (i: int) (acc: TypedExpr list) (rest: Expr list) =
                match rest with
                | [] -> Ok (List.rev acc)
                | c :: tl ->
                    match checkChild i c with
                    | Ok t -> go (i + 1) (t :: acc) tl
                    | Error e -> Error e
            go 0 [] cs
    checkChildren 0 0 topName litSpan elems
    |> Result.map (fun tElems -> mkTyped (TExprArrayLit (tElems, arrTy)) (mkArrayLike arrTy))

// Bidirectional checking: checkExpr drives an expression to a known target
// type, pushing the expectation into literal/constructor positions; falls
// through to inferExpr + unify otherwise. Used by inferLetBinding when an
// annotation is present. Strict policy: element-count mismatch on Idx<N>
// against a literal is an error; heterogeneous numeric literals against a
// typed array are checked individually (no cross-element promotion); no
// 0/false coercion, no float/int silent narrowing.
/// Back-fills the source span onto the checked node, exactly as inferExpr
/// does for the synthesis direction: the bidirectional fast paths below build
/// their nodes with `mkTyped` (noSpan), which would leave literals, tuples,
/// array literals, fill_random and `complex(re, im): Complex64` spanless -- and
/// so invisible to span-based editor tooling (Ide.fs calls[]).
/// An expected arrow supplies the residual slots of a partial application,
/// just as it supplies a written lambda's parameter slots. Do this before
/// inference of the body, when its supplied literal arguments are flexible.
and inferExprAtExpected (env: TypeEnv) (expected: IRType) (expr: Expr) : TypeResult<TypedExpr> =
    match expr.Kind, env.Subst.Resolve expected with
    | ExprLambda (ps, wc, body), FuncElem (slots, _) ->
        inferLambdaSeeded env ps wc body (slots |> List.map Some)
    | ExprApp (func, args), FuncElem (slots, _) ->
        let func, args = tryFlattenFactoryChain env func args |> Option.defaultValue (func, args)
        match tryFillDefaultArgs env expr.Span func args with
        | Some (Ok rewritten) -> checkExpr env expected rewritten
        | Some (Error e) -> Error e
        | None ->
            inferExpr env func |> Result.bind (fun tFunc ->
                match env.Subst.Resolve tFunc.Type with
                | FuncElem (ps, ret) when not args.IsEmpty && args.Length < ps.Length
                                         && ps.Length - args.Length = slots.Length
                                         && not (args |> List.exists _.Kind.IsExprWildcard)
                                         && not (ps |> List.exists (fun p -> (env.Subst.Resolve p).IsIRTPoly)) ->
                    inferPrefixPartial env func args tFunc ps ret (slots |> List.map Some)
                | _ -> inferExpr env expr)
    | _ -> inferExpr env expr
and checkExpr (env: TypeEnv) (expected: IRType) (expr: Expr) : TypeResult<TypedExpr> =
    // As inferExpr: the span current when a mismatch is raised here is frozen
    // against it.
    let r = checkExprInner env expected expr
    (match r with Error e -> noteErrorSpan e | Ok _ -> ())
    match r with
    | Ok te when te.Span.StartLine = 0 && expr.Span.StartLine > 0 ->
        Ok { te with Span = expr.Span }
    | r -> r

and checkExprInner (env: TypeEnv) (expected: IRType) (expr: Expr) : TypeResult<TypedExpr> =
    // Stamp the ambient expression span (mirrors inferExpr) so errors raised
    // here -- and in recursive checkExpr calls on sub-expressions -- anchor to
    // the innermost offending node (e.g. the specific array-literal row whose
    // length is wrong) instead of the enclosing declaration's whole span.
    if expr.Span.StartLine > 0 then setCurrentExprSpan expr.Span
    let resolved = env.Subst.Resolve expected
    // R19: the NARROW width a literal position gives (Int32 / Float32, bare,
    // unit-carrying or index-tagged); a literal that does not fit it is
    // refused here, at the literal's own span (LiteralDefaults.misfitMessage).
    let narrowTarget (t: IRType) : ElemType option =
        match t with
        | IRTScalar (ETInt32 | ETFloat32 as et)
        | IRTUnitAnnotated (IRTScalar (ETInt32 | ETFloat32 as et), _)
        | IRTIdxTagged (IRTScalar (ETInt32 as et), _) -> Some et
        | _ -> None
    let misfit (et: ElemType) (text: string) : TypeResult<TypedExpr> =
        Error (ImplicitNumericMix (LiteralDefaults.misfitMessage et text))
    match expr.Kind, resolved with

    // R19: an integer / float literal (or a negated one, judged WHOLE:
    // `-2147483648` fits Int32) that does not fit the narrow width its
    // position gives it. The arms below then only meet literals that fit.
    | ExprKind.ExprLit (LitInt v), t
        when (match narrowTarget t with Some ETInt32 -> not (LiteralDefaults.intFits ETInt32 v) | _ -> false)
             && (match namedIndexParam env t with Some (_, Some _) -> false | _ -> true) ->
        misfit ETInt32 (LiteralDefaults.intText v)
    | ExprKind.ExprLit (LitFloat f), t
        when (match narrowTarget t with Some ETFloat32 -> not (LiteralDefaults.floatLiteralFits ETFloat32 f expr.Span) | _ -> false) ->
        misfit ETFloat32 (LiteralDefaults.floatTextAt f expr.Span)
    | ExprKind.ExprUnaryOp (OpNeg, ({ Kind = ExprKind.ExprLit (LitInt v) } as litExpr)), t
        when narrowTarget t = Some ETInt32
             && (match namedIndexParam env t with Some (_, Some _) -> false | _ -> true) ->
        if not (LiteralDefaults.intFits ETInt32 (-v)) then misfit ETInt32 ("-" + LiteralDefaults.intText v)
        elif LiteralDefaults.intFits ETInt32 v then
            checkExpr env expected litExpr
            |> Result.map (fun tLit -> mkTyped (TExprUnaryOp (OpNeg, tLit)) tLit.Type)
        else
            // -2147483648: Int32's minimum, whose magnitude alone does not fit
            // (lead ruling R2; inferExprInner's arm for the same literal).
            let ty = match t with IRTScalar et -> IRTScalar et | _ -> t
            let tLit = mkTypedSpan (TExprLit (LitInt v)) ty litExpr.Span
            Ok (mkTyped (TExprUnaryOp (OpNeg, tLit)) ty)
    | ExprKind.ExprUnaryOp (OpNeg, ({ Kind = ExprKind.ExprLit (LitFloat f) } as fl)), t
        when (match narrowTarget t with Some ETFloat32 -> not (LiteralDefaults.floatLiteralFits ETFloat32 f fl.Span) | _ -> false) ->
        misfit ETFloat32 ("-" + LiteralDefaults.floatTextAt f fl.Span)

    // Numeric/scalar literals retype to the expected scalar (numbers stay numbers,
    // bools stay bools -- no implicit 0<->false). The literal carries its source-
    // level value but its IRType matches the annotation.
    | ExprKind.ExprLit (LitInt _ as lit), IRTScalar et ->
        match et with
        | ETInt32 | ETInt64 ->
            Ok (mkTyped (TExprLit lit) (IRTScalar et))
        | ETFloat32 | ETFloat64 | ETComplex64 | ETComplex128 ->
            // THE MIXING RULE (formalism 2.4), at a typed position: an
            // integer literal is an integer and never means a float -- the
            // decimal point is the type (`let x: Float64 = 1.0`), as in F#.
            let n = match lit with LitInt v -> string v | _ -> "1"
            Error (ImplicitNumericMix
                       ($"implicit numeric conversion: the integer literal `{n}` stands where a {ppIRType resolved} is expected, "
                        + $"and Blade converts nothing implicitly -- `{n}` is an integer literal and `{n}.0` the float one: write `{n}.0`."))
        | _ ->
            Error (TypeMismatch (resolved, IRTScalar ETInt64))
    | ExprKind.ExprLit (LitInt v as lit), IRTIdxTagged (IRTScalar (ETInt32 | ETInt64), _) ->
        // section 4.18.3: untyped int literal acquires the index tag from annotation
        // context. `let i: Idx<3> = 0` works; the 0 becomes Nat<Idx<3>>.
        // Strict in the OTHER direction: a bare `Nat` value cannot flow to
        // Nat<I> position without explicit cast -- but a LITERAL has no
        // pre-committed type, so context-driven typing applies here.
        // RANGE-CHECKED against a named plain index type's static extent:
        // an index-typed VALUE -- a `Nat<I>` let, a foreign-key column's
        // literal cells -- is a position of I, and a literal past the end
        // (`[0, 2, 0, 7]` over Idx<3>) can never be one. The ONE admitted
        // out-of-range key is `-1`, group_by's "excluded" key (sql-group-by/
        // 028, 041), which the negated-literal arm below lets through; a
        // value that is not proven -- that `-1`, any cell read out of a
        // foreign-key column -- is guarded at its subscript (Zonk).
        match namedIndexParam env resolved with
        | Some (tag, Some n) when v >= n -> Error (SubscriptOutOfRange (v, Some n, tag))
        | _ -> Ok (mkTyped (TExprLit lit) resolved)
    | ExprKind.ExprLit (LitInt _ as lit), (IRTNat _ | IRTUnitAnnotated (IRTNat _, _)) ->
        // Same context-driven rule for Nat targets, unit-annotated or bare:
        // `l1: Nat<angular_momentum> = 1` retypes the literal to the target.
        Ok (mkTyped (TExprLit lit) resolved)
    | ExprKind.ExprLit (LitString _ as lit), IRTScalar et ->
        match et with
        | ETString ->
            Ok (mkTyped (TExprLit lit) (IRTScalar et))
        | _ ->
            Error (TypeMismatch (resolved, IRTScalar ETString))
    | ExprKind.ExprLit (LitString s as lit), IRTIdxTagged (IRTScalar ETString, tagRef) ->
        // section 4.18.3 parallel for string-valued index tags (EnumIdx with
        // string values). Same context-driven coercion as the int case --
        // and the same range check: a label the EnumIdx does not declare
        // (["pacific", "bogus"] in a key column) is no key of it.
        match tagRef with
        | IRefNamed tag ->
            (match Map.tryFind tag env.TypeDefs with
             | Some (TDIEnumIdx (_, _, values, _)) when not (List.contains (EVString s) values) ->
                 let avail = values |> List.map (function EVString v -> v | EVInt n -> string n)
                 Error (EnumIdxUnknownLabel (tag, s, avail))
             | _ -> Ok (mkTyped (TExprLit lit) resolved))
        | _ -> Ok (mkTyped (TExprLit lit) resolved)
    | ExprKind.ExprLit (LitString _ as lit), IRTUnitAnnotated (IRTScalar ETString, _) ->
        // Quantity-tagged string position (`let s: String<title> = "K"`,
        // `"K" : title`): the literal adopts the annotation, same
        // context-driven rule as the numeric/Nat unit arms above.
        Ok (mkTyped (TExprLit lit) resolved)
    | ExprKind.ExprLit (LitFloat _ as lit), IRTScalar et ->
        match et with
        | ETFloat32 | ETFloat64 -> Ok (mkTyped (TExprLit lit) (IRTScalar et))
        | _ -> Error (TypeMismatch (resolved, IRTScalar ETFloat64))
    // A UNIT-CARRYING numeric position -- an array literal's cells against
    // `Array<Float32<meters> like ..>`: the literal adapts within its class
    // (`1.0` is a Float32 here, as above) and the unit attaches, as a bare
    // value adopts the target unit everywhere. Without it the cell fell to
    // inference, `1.0` met `Float32<meters>` as a Float64, and the literal
    // was refused (BL3001) -- the Float64 spelling only passed because its
    // width agreed. The mixing rule is the scalar arm's: an integer literal
    // never stands for a float.
    | ExprKind.ExprLit (LitFloat _ as lit), IRTUnitAnnotated (IRTScalar (ETFloat32 | ETFloat64), _) ->
        Ok (mkTyped (TExprLit lit) resolved)
    | ExprKind.ExprLit (LitInt _ as lit), IRTUnitAnnotated (IRTScalar (ETInt32 | ETInt64), _) ->
        Ok (mkTyped (TExprLit lit) resolved)
    | ExprKind.ExprLit (LitInt _), IRTUnitAnnotated ((IRTScalar (ETFloat32 | ETFloat64 | ETComplex64 | ETComplex128) as bare), _) ->
        checkExprInner env bare expr
    | ExprKind.ExprLit (LitBool _ as lit), IRTScalar ETBool ->
        Ok (mkTyped (TExprLit lit) (IRTScalar ETBool))
    | ExprKind.ExprLit (LitBool _ as lit), IRTUnitAnnotated (IRTScalar ETBool, _) ->
        // Quantity-tagged bool position (`let f: Bool<flag> = true`): same
        // literal-adoption rule as the string arm above.
        Ok (mkTyped (TExprLit lit) resolved)

    // A NEGATED numeric literal is a literal: `-1` retypes to the expected
    // scalar exactly as `1` does. Without this arm the negation falls through
    // to inference, which types the operand at the literal default (Int64) and
    // then reports a spurious mismatch against, say, an Int32 struct field --
    // so `P { a = -1 }` failed where `P { a = 1 }` succeeded. Deliberately
    // narrow: only a LITERAL operand, so this is literal retyping and not
    // general bidirectional propagation through arithmetic.
    | AstNegIntLit v, _
        when v > 1L && (match namedIndexParam env resolved with Some (_, Some _) -> true | _ -> false) ->
        // A negative index-typed value: only `-1`, group_by's "excluded"
        // key, is admitted (see the LitInt arm above).
        Error (SubscriptOutOfRange (-v, None, fst (namedIndexParam env resolved).Value))
    | ExprKind.ExprUnaryOp (OpNeg, ({ Kind = ExprKind.ExprLit (LitInt _ | LitFloat _) } as litExpr)), _ ->
        checkExpr env expected litExpr
        |> Result.map (fun tLit -> mkTyped (TExprUnaryOp (OpNeg, tLit)) tLit.Type)

    // A numeric/bool literal checked against an unpinned inference var -- a
    // generic `T` (arity 0) or an arity-polymorphic `T^k` (e.g. the return of
    // comoment_prod's `-> T^1`). Introduce flexibility HERE, bidirectionally:
    // mint a fresh kind-seeded literal var and defer it into the expected var,
    // exactly as `zero` does. The literal stays a scalar VALUE at lowering and a
    // consuming pseudo-native op broadcasts it; a concretely-shaped array target
    // is instead handled by the scalar->array fill coercion (not this arm).
    | ExprKind.ExprLit (LitInt _ | LitFloat _ | LitBool _ as lit), (IRTInfer _ | IRTUnitAnnotated (IRTInfer _, _)) ->
        let flexTy = freshLiteralTypeAt env.Subst lit expr.Span
        // A generic unit-bearing default must not pin the shared element
        // width at declaration time. Its units attach now; its width stays
        // flexible until the call's supplied arguments instantiate it.
        unify env.Subst flexTy (IR.stripUnits resolved)
        |> Result.mapError (fun _ -> TypeMismatch (resolved, inferLiteralTypeAt lit expr.Span))
        |> Result.map (fun () ->
            let ty =
                match IR.getUnits resolved with
                | Some u -> IRTUnitAnnotated (flexTy, u)
                | None -> flexTy
            mkTyped (TExprLit lit) ty)

    // Array literal: extract per-rank shape from the annotation and recurse.
    // Outer index supplies the literal's length; inner index types form the
    // element annotation. Elements are checked individually against this.
    // A COMPACT leading group (SymIdx / AntisymIdx / HermitianIdx, rank >= 2)
    // is one index slot over r dimensions whose stored cells are a shrinking
    // simplex, so neither the length check below (which reads the group's
    // extent n, not its cardinality) nor the one-index-type-per-bracket-level
    // peel describes it. checkCompactArrayLit owns that shape end to end.
    | ExprKind.ExprArrayLit elems, ArrayElem arrTy when
            (not arrTy.IndexTypes.IsEmpty
             && arrTy.IndexTypes.Head.Rank >= 2
             && (match arrTy.IndexTypes.Head.Symmetry with
                 | SymSymmetric | SymAntisymmetric | SymHermitian -> true
                 | SymNone | SymWreath -> false)) ->
        checkCompactArrayLit env arrTy elems expr.Span
    // An OrbIdx (iterated-wreath) class: its rows shrink per LEVEL, not per
    // coordinate, so it is neither the simplex above nor a rectangular nest.
    // The class has no writable annotation at all (BL4003) -- but a literal
    // checked against one reached here through an inferred type, so refuse at
    // the same seam every other wreath-storage site does.
    | ExprKind.ExprArrayLit _, ArrayElem arrTy when
            (not arrTy.IndexTypes.IsEmpty && arrTy.IndexTypes.Head.Symmetry = SymWreath) ->
        Error (OrbitStorageUnsupported (ppOrbitLevels (orbitLevelsOf arrTy.IndexTypes.Head),
                                        "an array literal"))
    | ExprKind.ExprArrayLit elems, ArrayElem arrTy when not arrTy.IndexTypes.IsEmpty ->
        checkArrayLitAgainst env arrTy elems

    // fill_random(mod): internal random-fill array constructor. The result
    // array type/shape comes from the annotation (this bidirectional arm), so
    // it is only usable in an annotated position -- without one, `fill_random`
    // synthesizes as an unbound name. The modulus is the argument to
    // rand() % mod and must be an integer. Lowering records it in RandomInits;
    // codegen emits allocate<> + the runtime fill_random.
    | ExprKind.ExprApp ({ Kind = ExprKind.ExprVar "fill_random" }, [modE]), ArrayElem _ ->
        checkExpr env (IRTScalar ETInt64) modE |> Result.map (fun tMod ->
            mkTyped (TExprFillRandom tMod) resolved)

    // Tuple literal: zip components against expected component types.
    | ExprKind.ExprTuple exprs, IRTTuple ts when exprs.Length = ts.Length ->
        List.zip exprs ts |> List.map (fun (e, t) -> checkExpr env t e) |> sequenceResults
        |> Result.map (fun tExprs ->
            mkTyped (TExprTuple tExprs) (IRTTuple (tExprs |> List.map (_.Type))))

    // Complex literal construction checked against an expected Complex
    // width: `complex(re, im)` adopts the width (Complex64 components are
    // Float32, Complex128 components Float64), so
    // `let z: Complex64 = complex(a, b)` works without a distinct
    // narrow-width constructor. Produces TExprComplexLit (NOT TExprTuple)
    // -- the tuple form would lower to IRTuple and lose the scalar nature,
    // flattening an N-element Complex array into N x 2 doubles.
    // TExprComplexLit lowers to IRComplex, a scalar IR node rendered as
    // std::complex<double>(re, im). Both components must be float-typed
    // (no implicit int -> float promotion at literal construction time).
    | ExprKind.ExprApp ({ Kind = ExprKind.ExprVar "complex" }, [reExpr; imExpr]), IRTScalar (ETComplex64 | ETComplex128 as cet) when (lookupVar "complex" env).IsNone ->
        let componentTy =
            match cet with
            | ETComplex64 -> IRTScalar ETFloat32
            | _ -> IRTScalar ETFloat64
        checkExpr env componentTy reExpr |> Result.bind (fun tRe ->
        checkExpr env componentTy imExpr |> Result.map (fun tIm ->
            mkTyped (TExprComplexLit (tRe, tIm)) (IRTScalar cet)))

    // A 2-tuple checked against a Complex type gets a steering error rather
    // than a generic mismatch: complex values are built with complex(re, im),
    // which composes cleanly inside larger expressions (a cast form has a
    // precedence trap: `a * (re, im) : T` binds the cast OUTSIDE the
    // multiply, and the bare tuple operand would miscompile).
    | ExprKind.ExprTuple [_; _], IRTScalar (ETComplex64 | ETComplex128) ->
        Error (TypeMismatchErr "complex values are constructed with complex(re, im); the tuple form `(re, im) : Complex128` is no longer supported")

    // Match in a checking position: push the expected type into each arm so a
    // literal/scalar arm can flex to it (bidirectional match -- see checkMatch).
    | ExprKind.ExprMatch (scrutinee, cases), _ ->
        checkMatch env expected scrutinee cases

    // Block in a checking position: push the expected type into the block's
    // final expression (which may itself be a match/literal that flexes).
    | ExprKind.ExprBlock (stmts, finalExpr), _ ->
        inferBlock env stmts finalExpr (Some expected)

    // Tuple return/argument context reaches each branch before its literal
    // components default. Independently inferring the branches loses the
    // expected widths even though checking a tuple already handles its slots.
    | ExprKind.ExprIf (cond, thenBr, elseBr), IRTTuple _ ->
        checkExpr env (IRTScalar ETBool) cond |> Result.bind (fun tCond ->
        checkExpr env expected thenBr |> Result.bind (fun tThen ->
        checkExpr env expected elseBr |> Result.map (fun tElse ->
            mkTyped (TExprIf (tCond, tThen, tElse)) expected)))
    // ROUND 6: an `if` checked against a numeric scalar checks its branches
    // against it, so a literal-only branch takes the expected width
    // (`let x: Float64 = if c then 1.0 else 0.0`).
    | ExprKind.ExprIf (cond, thenBr, elseBr), (IRTScalar et | IRTUnitAnnotated (IRTScalar et, _))
            when litClassOf et >= 0 ->
        // Check recursively: an else-if or block final needs the same
        // context as an immediate literal. Fixed values still unify normally.
        checkExpr env (IRTScalar ETBool) cond |> Result.bind (fun tCond ->
        checkExpr env expected thenBr |> Result.bind (fun tThen ->
        checkExpr env expected elseBr |> Result.map (fun tElse ->
            mkTyped (TExprIf (tCond, tThen, tElse)) expected)))

    | _ ->
        checkExprDefault env expr expected

and checkArrayLitAgainst (env: TypeEnv) (arrTy: IRArrayType) (elems: Expr list) : TypeResult<TypedExpr> =
    let outerIdx = arrTy.IndexTypes.Head
    let innerIdxs = arrTy.IndexTypes.Tail
    // Length check: if extent is a literal, the count must match.
    let lengthOk =
        match outerIdx.Extent with
        | IRLit (IRLitInt n) -> int n = elems.Length
        | _ -> true  // dynamic / parametric extent: no static check
    if not lengthOk then
        let expectedN =
            match outerIdx.Extent with IRLit (IRLitInt n) -> int n | _ -> -1
        // Name the offending axis by its index tag; suppress synthetic
        // (__anon / __-prefixed) tags, which carry no user-facing name.
        let axisTag =
            match outerIdx.Tag with
            | Some t when not (t.StartsWith("__")) -> Some t
            | _ -> None
        Error (ArrayLitLength (elems.Length, expectedN, axisTag))
    else
        // A closed `RaggedIdx<lens>` inner slot: the outer length check
        // above reads the OUTER extent, and the per-row recursion below
        // reaches the ragged record with no static extent to check
        // against (its Extent is an IRRaggedLookup), so the row lengths
        // are only judgeable from here, where the rows are still in hand.
        checkRaggedLensAgainstLit env arrTy elems |> Result.bind (fun () ->
        // Build the element annotation: just the elem type if no inner
        // index types, otherwise an array with the remaining index types.
        // arrTy.ElemType is IRType post-B2.
        let elemAnnot =
            if innerIdxs.IsEmpty then arrTy.ElemType
            else mkArrayLike { arrTy with IndexTypes = innerIdxs }
        elems |> List.map (checkExpr env elemAnnot) |> sequenceResults
        |> Result.map (fun tElems ->
            mkTyped (TExprArrayLit (tElems, arrTy)) (mkArrayLike arrTy)))

// Default: synthesize, then unify. This is the path that handles variables,
// function calls, complex expressions, and any case the special-cases miss.
and checkExprDefault (env: TypeEnv) (expr: Expr) (expected: IRType) : TypeResult<TypedExpr> =
    // ROUND 6: a map whose kernel RETURNS a literal-only expression, checked
    // against an array of a concrete numeric element, returns it at that
    // element (`let a: Array<Float64 like I> = method_for(range<I>) <@>
    // lambda(i) -> 0.0 |> compute` fills Float64 zeros).
    let expr =
        let elemOfExpected =
            match IR.stripUnits (env.Subst.Resolve expected) with
            | ArrayElem a ->
                (match IR.stripUnits (env.Subst.Resolve a.ElemType) with
                 | IRTScalar et when litClassOf et >= 0 -> Some et
                 | _ -> None)
            | _ -> None
        match elemOfExpected with
        | None -> expr
        | Some et ->
            let pushKernel (e: Expr) =
                match e.Kind with
                | ExprKind.ExprBinOp (Elementwise, OpApply, former, ({ Kind = ExprKind.ExprLambda (ps, wc, body) } as lam))
                        when (surfaceLiteralClass body).IsSome ->
                    { e with Kind = ExprKind.ExprBinOp (Elementwise, OpApply, former,
                                                        { lam with Kind = ExprKind.ExprLambda (ps, wc, ascribeLiteralOnly et body) }) }
                | _ -> e
            match expr.Kind with
            | ExprKind.ExprCompute inner -> { expr with Kind = ExprKind.ExprCompute (pushKernel inner) }
            | _ -> pushKernel expr
    inferExprAtExpected env expected expr |> Result.bind (fun tE ->
        // ROUND 6, THE ANNOTATION SEAM: a literal-only expression takes
        // the checked width within its class (`let c: Float64 = 1.0 / 3.0`
        // computes in Float64), also as the element of an array position
        // it will fill (tryScalarFill below).
        let tE =
            match literalOnlyElem env.Subst tE with
            | Some le ->
                let sameClass (u: IRType) =
                    match IR.stripUnits (env.Subst.Resolve u) with
                    | IRTScalar et when litClassOf et = litClassOf le -> Some et
                    // An index-typed position is an Int64 (index-like).
                    | IRTNat _ | IRTIdxTagged _ when litClassOf le = 0 -> Some ETInt64
                    | _ -> None
                let target =
                    match IR.stripUnits (env.Subst.Resolve expected) with
                    | ArrayElem arr -> sameClass arr.ElemType
                    | u -> sameClass u
                (match target with
                 | Some et -> retypeLiteralOnly env.Subst et tE |> Option.defaultValue tE
                 | None -> tE)
            | None -> tE
        // A PROVIDER READ reaching a checking position is the one synthesis
        // whose shape unify is not allowed to judge but must (see
        // providerReadExtentClash). Ahead of unify so the specific
        // extent complaint wins over the generic mismatch it would
        // otherwise be flattened into -- and ahead of tryScalarFill, which
        // cannot apply to an array-typed read anyway.
        match providerReadExtentClash env expr tE.Type expected with
        | Some clash -> Error clash
        | None ->
        match unify env.Subst tE.Type expected with
        | Ok () ->
            // The ASCRIPTION half of the shared static-extent refinement
            // (`staticExtentClash`; the call judgment is the other half):
            // unify never compares extents, so `let x: Array<F like
            // Idx<5>> = a3` unified clean and emitted a copy of 5 out of a
            // 3-element buffer. This one site covers `let`, annotated
            // returns, block finals and match arms checked against an
            // expected type.
            (match staticExtentClash env.Subst expected tE.Type with
             | Some (d, e, a) ->
                 Error (ExtentAscribeMismatch ("this value, against its declared type", d + 1, e, a))
             | None -> Ok tE)
        | Error e ->
            // Mechanism 2: a scalar in a concretely-shaped array position
            // broadcasts to a fill; otherwise the normal mismatch stands.
            match tryScalarFill env tE expected with
            | Some node -> Ok node
            // A component-rank clash is flattened to the generic mismatch
            // by the collapse below, which then renders as two type names
            // that differ only in a `SymIdx<k, ...>` the reader has no
            // reason to read as a rank. Keep unify's wording in that one
            // case; every other error keeps the plain TypeMismatch shape.
            | None ->
                match e with
                | IndexRankMismatch _ -> Error e
                // Re-resolve at REPORT time: the failed unify may still
                // have bound inner vars (e.g. the fresh scalar under a
                // bare-quantity ascription), and the entry-time `resolved`
                // predates them.
                | _ -> Error (TypeMismatch (env.Subst.Resolve expected, env.Subst.Resolve tE.Type)))

/// A cast TARGET that is a named integer index type `I` (formalism 3.10):
/// its tag and, when it has one, its static extent.
and internal indexCastTarget (env: TypeEnv) (annoTy: IRType) : (string * int64 option) option =
    match env.Subst.Resolve annoTy with
    | IRTIdxTagged (IRTScalar (ETInt32 | ETInt64), IRefNamed tag) ->
        let ext =
            match lookupTypeDef tag env with
            | Some (TDIIndexType (_, idx, _)) when idx.IxKind = IxKPlain && idx.Symmetry = SymNone ->
                Blade.IRPrint.tryEvalIntIR idx.Extent
            | _ -> None
        // An EnumIdx's int keys are raw keys, not positions -- not a cast target
        // this rule knows how to check.
        (match lookupTypeDef tag env with
         | Some (TDIEnumIdx _) -> None
         | _ -> Some (tag, ext))
    // G208 (3): an ANONYMOUS index ascription `(e : Idx<3>)` is the same cast
    // into an index space: a literal is range-checked at compile time, a
    // computed integer gets the checked conversion (an anonymous tag is never
    // proof, so it used to make `(5 : Idx<3>)` an unchecked position).
    // (A run-time extent keeps the old unification: G208 (2)'s class.)
    | IRTIdxTagged (IRTScalar (ETInt32 | ETInt64), IRefAnon (_, extE)) ->
        (match Blade.IRPrint.tryEvalIntIR extE with
         | Some n -> Some ($"Idx<{n}>", Some n)
         | None -> None)
    | _ -> None

/// `(e : I)` for a named integer index type I -- the cast into an index
/// space, and the ONE place a plain integer becomes an index value:
///   * a LITERAL is a known position: range-checked against I's static
///     extent at compile time (`(7 : Lat)` over Idx<3> is refused);
///   * an index value of I already unifies as before (no guard);
///   * a plain INTEGER value -- or a position, arithmetic on an index -- is
///     a CHECKED conversion: the value is bound once and guarded
///     `0 <= v < extent(I)` at run time (BL8006), in both lanes -- the guard
///     is an ordinary constraint check, so codegen and the interpreter need
///     nothing new. It needs I's extent at compile time; without one the
///     cast is refused rather than left unchecked;
///   * a still-OPEN operand (an unannotated kernel parameter) is NOT pinned
///     to I: it gets the checked conversion, and its final type is judged
///     after inference (Zonk elides the guard for a proven index of I; the
///     post-zonk sweep refuses a foreign index or a non-integer). Without a
///     static extent it still unifies (the old pin) -- no guard is possible.
/// An index value of a DIFFERENT named index type is refused
/// (IndexCastForeignTag, BL4003); a float is the mismatch it was.
and internal inferIndexCast (env: TypeEnv) (e: Expr) (annoTy: IRType) (tag: string) (ext: int64 option) : TypeResult<TypedExpr> =
    let litVal =
        match e.Kind with
        | ExprKind.ExprLit (LitInt v) -> Some v
        | AstNegIntLit v -> Some (-v)
        | _ -> None
    match litVal with
    | Some v when v < 0L -> Error (SubscriptOutOfRange (v, None, tag))
    | Some v when (match ext with Some n -> v >= n | None -> false) ->
        Error (SubscriptOutOfRange (v, ext, tag))
    | Some _ ->
        checkExpr env annoTy e |> Result.map (fun tE -> { tE with Type = annoTy })
    | None ->
    inferExpr env e |> Result.bind (fun tE ->
        let span = if e.Span.StartLine > 0 then e.Span else tE.Span
        match IR.stripUnits (env.Subst.Resolve tE.Type) with
        | IRTScalar (ETInt32 | ETInt64) ->
            match ext with
            | None ->
                Error (StaticValueErr $"a cast of a computed integer to the index type '{tag}' is a checked conversion, and checking it needs '{tag}'s extent at compile time, which is not known here. Iterate with range<{tag}> to produce '{tag}' values, or give '{tag}' a static extent.")
            | Some n -> Ok (checkedIndexConversion env tE annoTy tag n span)
        // A position (arithmetic on an index value) typed by its operand is
        // an integer all the same.
        | IRTIdxTagged (IRTScalar (ETInt32 | ETInt64), IRefNamed src)
            when src <> tag && isIndexPositionExpr tE && ext.IsSome ->
            Ok (checkedIndexConversion env tE annoTy tag ext.Value span)
        | IRTIdxTagged (IRTScalar (ETInt32 | ETInt64), IRefNamed src)
            when src <> tag && not (src.StartsWith "__") ->
            Error (IndexCastForeignTag (tag, src))
        // An OPEN operand -- an unannotated kernel or function parameter, or
        // arithmetic on one -- is not yet known to be an integer, an index of
        // I, or something else. The cast must NOT decide it by unifying the
        // operand with Nat<I>: that pinned the parameter itself to I, so a
        // kernel `lambda(k) -> s((k : State))` fed by `range<Idx<91>>` (or
        // `0..91`) was refused at the RANGE ("expected Nat<State>, got
        // Int64") -- the very cast the BL4003 untagged-integer advice
        // recommends. A conversion constrains its result, never its operand:
        // emit the checked conversion now (the operand defaults to an
        // integer, as a subscript's does), and let the operand's FINAL type
        // decide after inference -- Zonk drops the guard when the operand
        // turns out to be a proven index of I already, and the post-zonk
        // subscript sweep (collectSubscriptErrors) refuses an index of a
        // different named type or a non-integer, exactly as this eager
        // judgment would have.
        | IRTInfer vid when ext.IsSome ->
            if (env.Subst.GetArityConstraint vid).IsNone then env.Subst.MarkIndexDefault vid
            Ok (checkedIndexConversion env tE annoTy tag ext.Value span)
        | _ ->
            match unify env.Subst tE.Type annoTy with
            | Ok () -> Ok { tE with Type = annoTy }
            | Error _ -> Error (TypeMismatch (env.Subst.Resolve annoTy, env.Subst.Resolve tE.Type)))

// ---- Shared helpers for both let paths (let-as-expression and top-level DeclLet) ----

/// Scan a lowered IRType for the `__error_ragged_no_prior` placeholder that
/// lowerTypeExpr plants when RaggedIdx appears as the FIRST index slot (no
/// prior index to drive the per-row lengths lookup, formalism 4.4). Lowering
/// can only produce a degenerate placeholder -- IT cannot Error -- so the
/// annotation consumers (let bindings, function signatures) call this and
/// surface the actual rejection. Without this check the placeholder would
/// sail through silently.
and irTypeHasRaggedNoPrior (t: IRType) : bool =
    match t with
    | ArrayElem at ->
        (at.IndexTypes |> List.exists (fun ix -> ix.IxKind = IxKErrorRaggedNoPrior))
        || irTypeHasRaggedNoPrior at.ElemType
    | IRTTuple ts -> ts |> List.exists irTypeHasRaggedNoPrior
    | FuncElem (ps, r) -> (ps |> List.exists irTypeHasRaggedNoPrior) || irTypeHasRaggedNoPrior r
    | _ -> false

/// Detect the Dist order sentinel (lowerTypeExpr's TyDist arm lowers a
/// non-static or < 1 order to IRTDist(-1, ...) because it has no error
/// channel). Same consumption-site pattern as irTypeHasRaggedNoPrior:
/// let bindings and function signatures call this and surface the rejection.
and irTypeHasBadDistOrder (t: IRType) : bool =
    match t with
    | IRTDist (n, elem, _) -> n < 1 || irTypeHasBadDistOrder elem
    | ArrayElem at -> irTypeHasBadDistOrder at.ElemType
    | IRTTuple ts -> ts |> List.exists irTypeHasBadDistOrder
    | FuncElem (ps, r) -> (ps |> List.exists irTypeHasBadDistOrder) || irTypeHasBadDistOrder r
    | _ -> false

/// Detect the `Base<_>` tag wildcard (IRefAny). Same consumption-site
/// pattern as irTypeHasRaggedNoPrior: lowerTypeExpr has no error channel, so
/// positions where a wildcard is ILLEGAL call this. A wildcard is only
/// meaningful where a value FLOWS IN and the callee declines to constrain
/// its tag (function/lambda parameters); everywhere else a type must
/// PRODUCE a tag, so a wildcard would silently erase the tag discipline
/// (rejected as BL4003). Deliberate asymmetry: a FuncElem's parameter slots
/// are NOT scanned (the legal position), only its result -- so
/// `f: (Nat<_>) -> Float64` stays legal.
and irTypeHasTagWildcard (t: IRType) : bool =
    match t with
    | IRTIdxTagged (_, IRefAny) -> true
    | IRTIdxTagged (inner, _) -> irTypeHasTagWildcard inner
    | IRTUnitAnnotated (inner, _) -> irTypeHasTagWildcard inner
    | ArrayElem at -> irTypeHasTagWildcard at.ElemType
    | IRTTuple ts -> ts |> List.exists irTypeHasTagWildcard
    | FuncElem (_, r) -> irTypeHasTagWildcard r
    | IRTComputation inner -> irTypeHasTagWildcard inner
    | _ -> false

/// Detect the IrrepsIdx bad-spec marker (lowerIndexType's TyIrrepsIdx arm
/// plants IxKErrorIrrepsBadSpec when the spec is non-static or malformed,
/// smuggling the failure detail in the marker's IRParam extent). Same
/// consumption-site pattern as the two checks above, but returns the detail
/// so the diagnostic can say WHAT was wrong with the spec.
and irTypeBadIrrepsDetail (t: IRType) : string option =
    let detailOf (ix: IRIndexType) =
        if ix.IxKind = IxKErrorIrrepsBadSpec then
            match ix.Extent with
            | IRParam (detail, _, _) -> Some detail
            | _ -> Some "invalid spec"
        else None
    match t with
    | ArrayElem at ->
        (at.IndexTypes |> List.tryPick detailOf)
        |> Option.orElseWith (fun () -> irTypeBadIrrepsDetail at.ElemType)
    | IRTTuple ts -> ts |> List.tryPick irTypeBadIrrepsDetail
    | FuncElem (ps, r) ->
        (ps |> List.tryPick irTypeBadIrrepsDetail)
        |> Option.orElseWith (fun () -> irTypeBadIrrepsDetail r)
    | _ -> None

/// The pg twin of `irTypeBadIrrepsDetail` (stage 5b-i): the PgIrrepsIdx
/// bad-spec marker, whose failure detail rides the same IRParam-extent
/// channel. A separate walker rather than a widened one so the two members'
/// consumption-site diagnostics stay separate -- an unknown point group and an
/// unknown (l, parity) triple want different follow-up sentences.
and irTypeBadPgIrrepsDetail (t: IRType) : string option =
    let detailOf (ix: IRIndexType) =
        if ix.IxKind = IxKErrorPgIrrepsBadSpec then
            match ix.Extent with
            | IRParam (detail, _, _) -> Some detail
            | _ -> Some "invalid spec"
        else None
    match t with
    | ArrayElem at ->
        (at.IndexTypes |> List.tryPick detailOf)
        |> Option.orElseWith (fun () -> irTypeBadPgIrrepsDetail at.ElemType)
    | IRTTuple ts -> ts |> List.tryPick irTypeBadPgIrrepsDetail
    | FuncElem (ps, r) ->
        (ps |> List.tryPick irTypeBadPgIrrepsDetail)
        |> Option.orElseWith (fun () -> irTypeBadPgIrrepsDetail r)
    | _ -> None

/// Detect a depth >= 2 OrbIdx (SymWreath) record anywhere in a type, returning
/// its rendered level list. Consumed at the let-binding annotation and the
/// function signature, the two places a user program can name an array type.
///
/// STILL REFUSED, deliberately, even though DEDUCTION produces storable
/// wreath classes -- the two are different powers. Deduction reaches a
/// wreath class only through the one shape that has a traversal nest (a comm
/// tie over every argument, `IRLoopStructure.deduceWreathTie`), and the value it produces
/// is written once and printed. An ANNOTATION names the class in the
/// abstract: it admits `let R: Array<F64 like OrbIdx<[(2,-),(2,+)],4>> = zero
/// |> compute`, a producer that does not exist, and makes the binding a
/// value a user will then subscript -- the mirrored read that is exactly
/// what is missing.
///
/// The seams further down (allocation, loop construction, the compact read
/// and print paths, providers) each refuse too, but as BACKSTOPS with no
/// diagnostic channel (`failwith`/AllocUnsupported). Both layers exist on
/// purpose: this one produces the message a user reads, and the backstops
/// make a future producer that synthesizes a SymWreath record internally a
/// loud failure instead of a wrong address.
///
/// Same walker shape as irTypeBadIrrepsDetail -- including scanning a
/// FuncElem's parameter slots: a higher-order parameter
/// `f: (Array<F64 like OrbIdx<[...], n>>) -> F64` still names storage that
/// would have to exist.
and irTypeWreathLevels (t: IRType) : string option =
    let levelsOf (ix: IRIndexType) =
        if ix.Symmetry = SymWreath then Some (ppOrbitLevels (orbitLevelsOf ix)) else None
    match t with
    | ArrayElem at ->
        (at.IndexTypes |> List.tryPick levelsOf)
        |> Option.orElseWith (fun () -> irTypeWreathLevels at.ElemType)
    | IRTTuple ts -> ts |> List.tryPick irTypeWreathLevels
    | FuncElem (ps, r) ->
        (ps |> List.tryPick irTypeWreathLevels)
        |> Option.orElseWith (fun () -> irTypeWreathLevels r)
    | IRTComputation inner -> irTypeWreathLevels inner
    | _ -> None

/// Detect a `TreeIdx<s>` slot anywhere in a type, returning its rendered class.
/// Consumed at the let-binding annotation, the function signature and the
/// `range<>` slot -- the three places a user program can name an array type
/// without going through an allocation the checker already refuses. P2
/// registers the TYPE and refuses every USE; P3-P5 replace these with the real
/// storage and read paths.
///
/// Same walker shape as irTypeWreathLevels, including scanning a FuncElem's
/// PARAMETER slots: a higher-order parameter `f: (Array<F64 like TreeIdx<s>>) -> F64`
/// still names storage that would have to exist.
and irTypeTreeShape (t: IRType) : string option =
    let shapeOf (ix: IRIndexType) =
        match ix with
        | TreeIdxLike rendered -> Some rendered
        | _ -> None
    match t with
    | ArrayElem at ->
        (at.IndexTypes |> List.tryPick shapeOf)
        |> Option.orElseWith (fun () -> irTypeTreeShape at.ElemType)
    | IRTTuple ts -> ts |> List.tryPick irTypeTreeShape
    | FuncElem (ps, r) ->
        (ps |> List.tryPick irTypeTreeShape)
        |> Option.orElseWith (fun () -> irTypeTreeShape r)
    | IRTComputation inner -> irTypeTreeShape inner
    | _ -> None

/// Detect the TreeIdx bad-shape marker (lowerIndexType's TyTreeIdx arm plants
/// IxKErrorTreeBadShape when the shape is non-static or malformed, smuggling the
/// failure detail in the marker's IRParam extent). The irTypeBadIrrepsDetail
/// pattern verbatim; returns the detail so the diagnostic can say WHAT was wrong.
and irTypeBadTreeDetail (t: IRType) : string option =
    let detailOf (ix: IRIndexType) =
        if ix.IxKind = IxKErrorTreeBadShape then
            match ix.Extent with
            | IRParam (detail, _, _) -> Some detail
            | _ -> Some "invalid shape"
        else None
    match t with
    | ArrayElem at ->
        (at.IndexTypes |> List.tryPick detailOf)
        |> Option.orElseWith (fun () -> irTypeBadTreeDetail at.ElemType)
    | IRTTuple ts -> ts |> List.tryPick irTypeBadTreeDetail
    | FuncElem (ps, r) ->
        (ps |> List.tryPick irTypeBadTreeDetail)
        |> Option.orElseWith (fun () -> irTypeBadTreeDetail r)
    | IRTComputation inner -> irTypeBadTreeDetail inner
    | _ -> None

/// Detect an unresolved QUALIFIED index-type path (`store.index.y`). Same
/// consumption-site pattern as the checks above, and self-marking: a path that
/// RESOLVES yields the registered record, so a dotted Tag can only be
/// lowerIndexType's fall-through for a name that was never registered. Without
/// this a typo'd or renamed dimension would silently become a free dependent
/// extent -- the annotation would look bound to the store while constraining
/// nothing.
///
/// PROVIDER TAGS ARE EXEMPT, and the exemption is load-bearing. A resolved
/// provider axis carries an IDENTITY tag,
/// `__icaxis|time@station_temps.icechunk:7d414d69...`, whose repo-label half
/// is a DIRECTORY NAME -- and `<name>.icechunk` is the conventional spelling,
/// so it contains a dot for entirely ordinary reasons. Without this clause the
/// heuristic read a perfectly resolved axis as an unresolved path and refused
/// `type T = ck.index.time` on every conventionally-named repo. (No fixture
/// repo is named with a dot, which is why the corpus never caught it.)
and irTypeUnknownAxisPath (t: IRType) : string option =
    let pathOf (ix: IRIndexType) =
        match ix.Tag with
        // The tree prefix is exempted for the same reason the irreps one is:
        // a parameterized tag's payload is not a provider axis path, and a tree
        // tag holding a dotted alias name would otherwise be misdiagnosed as an
        // unresolved store dimension.
        | Some tag when tag.Contains "."
                        // A module-qualified index type (D11, `Geo.Cell`)
                        // is a resolved identity, not a store path.
                        && not (Blade.Types.TypeOrigins.isQualified tag)
                        && not (tag.StartsWith irrepsTagPrefix)
                        && not (tag.StartsWith treeTagPrefix)
                        && not (isProviderAxisTag tag) -> Some tag
        | _ -> None
    match t with
    | ArrayElem at ->
        (at.IndexTypes |> List.tryPick pathOf)
        |> Option.orElseWith (fun () -> irTypeUnknownAxisPath at.ElemType)
    | IRTTuple ts -> ts |> List.tryPick irTypeUnknownAxisPath
    | FuncElem (ps, r) ->
        (ps |> List.tryPick irTypeUnknownAxisPath)
        |> Option.orElseWith (fun () -> irTypeUnknownAxisPath r)
    | _ -> None

/// Diagnostic for an unresolved `<store>.index.<dim>`, naming the dimensions
/// the store actually exposes (read back from the module the load site
/// recorded, so no file is re-opened).
and unknownAxisPathMessage (path: string) : string =
    let parts = path.Split('.')
    if parts.Length >= 3 && parts.[1] = "index" then
        let storeName = parts.[0]
        let dim = String.concat "." (parts |> Array.skip 2)
        let known =
            match Blade.ProviderRegistry.IdeStores.tryFind storeName with
            | Some pm -> pm.Types |> List.choose (function IRTDIndexType (n, _) -> Some n | _ -> None)
            | None -> []
        if known.IsEmpty then
            $"unknown index type '{path}': '{storeName}' is not a data-provider store binding in scope"
        else
            $"""the store '{storeName}' has no dimension '{dim}'. It exposes: {(known |> String.concat ", ")}"""
    else
        $"unknown qualified index type '{path}'"

/// Name the AGGREGATE a `min=`/`max=` bound was applied to, or None when the
/// base is a legitimate bound target. `Ast.TyBounded` is the bounded
/// PRIMITIVE node (formalism section 2.4); nothing lowers a bounded aggregate, and
/// the guards `Ast.boundedConjuncts` synthesizes compare the annotated value
/// ITSELF, which on an aggregate is nonsense (an undefined CodeGen sentinel
/// for arrays, a raw C++ type error for tuples). Classified on the LOWERED
/// form to resolve alias chains: `type Field = Array<Float64 like Y>` then
/// `x: Field<min=0.0, max=1.0>` builds `TyBounded (TyNamed ("Field", []), ..)`
/// -- the parser sees only a bare name, so array-ness is knowable only after
/// alias resolution (hence this lives here, not in `buildTypeApp`). Index
/// and unit tags are stripped first since they stay on the BASE node --
/// `Float64<velocity, min=0, max=1>` must keep working.
and internal boundedAggregateNoun (env: TypeEnv) (baseTy: TypeExpr) : string option =
    let rec strip (t: IRType) =
        match t with
        | Tagged inner | IRTComputation inner -> strip inner
        | _ -> t
    // ArrayElem / FuncElem are both views of IRTArrow, so the bare IRTArrow
    // arm below is only the mixed-slot residue (neither uniformly indexed nor
    // uniformly valued); it is still an aggregate.
    match strip (lowerTypeExpr env baseTy) with
    | ArrayElem _ -> Some "an array type"
    | FuncElem _ -> Some "a function type"
    | IRTArrow _ -> Some "an array type"
    | IRTTuple _ -> Some "a tuple type"
    | IRTDist _ -> Some "a Dist type"
    | IRTPoly _ -> Some "an arity-polymorphic array type"
    | IRTGroupKeys _ -> Some "a group-keys type"
    | IRTNamed n ->
        match lookupTypeDef n env with
        | Some (TDIStruct _) -> Some "a struct type"
        | Some (TDIVariant _) -> Some "a sum type"
        | _ -> None
    | _ -> None

/// The provider-access forms, for the shape gate below. All five spellings
/// (`alias.read` / `alias.stream` / `alias.read_window` / `alias.load_compound`
/// as applications, and the bare `|> read`) reach a checking position through
/// checkExprInner's default arm, and all five are typed from the STORE rather
/// than from their annotation. Returns the provider's registry name, for the
/// message; the bare form has no alias in hand and answers generically.
and internal providerReadFormName (env: TypeEnv) (expr: Expr) : string option =
    let isReadField (name: string) =
        name = "read" || name = "stream" || name = "read_window" || name = "load_compound"
    match expr.Kind with
    | ExprKind.ExprApp ({ Kind = ExprKind.ExprField ({ Kind = ExprKind.ExprVar alias }, fld) }, _)
            when isReadField fld ->
        providerAliasName env alias
    | ExprKind.ExprRead _ -> Some "provider"
    | _ -> None

/// THE PROVIDER-ASCRIPTION SHAPE GATE (BL3016).
///
/// `unify` deliberately does not compare extents (Unify.fs,
/// `indexPairIncompatible`: "Extents are NOT compared"). Extents are out of
/// type identity on purpose, and a blanket extent rule would refuse sound code
/// all over the language. That policy is not what this arm relaxes.
///
/// Extent agreement is instead enforced, seam by seam, wherever the two
/// extents can come from DIFFERENT places and disagreeing is an out-of-bounds
/// read rather than a naming quarrel: param vs argument (`ExtentArgMismatch`),
/// operand vs operand (`ZipExtentMismatch`), halo vs target
/// (`HaloExtentMismatch`), array literal vs annotation (`ArrayLitLength`).
/// This is the fourth member of that family, not an exception to unify's rule.
///
/// The seam it covers: at a provider read the STORE fixes the allocation
/// (codegen bakes the file's real shape into the buffer and the reader loop)
/// while the ANNOTATION fixes the type every later subscript compiles against,
/// and the read arm returns the operand's type unchanged, so nothing ever
/// reconciled them. Concretely, on a 3x2 CSV,
///     let bad: Array<Float64 like Idx<5>, Idx<9>> = t.vars.data |> c.read
///     let x = bad((4: Wrong), 8)
/// typechecked clean (in `check` and in the IDE), allocated 6 doubles, emitted
/// `bad[4][8]`, and segfaulted.
///
/// The array-literal precedent is the exact one: there too the value's shape
/// is fixed by something the annotation does not control, and there too the
/// answer is a dedicated check rather than a change to unify. A provider read
/// is the other such value; it just never got its check.
///
/// LITERAL-vs-literal only. A symbolic or parametric extent on either side is
/// left alone, the same convention the array-literal check uses ("dynamic /
/// parametric extent: no static check") -- which is also what keeps
/// `load_compound` (whose compacted extent is a runtime count) out of the way.
/// Equal SLOT COUNT only: a rank clash is unify's, and it reports it with both
/// full types in the message, which reads better than a per-slot complaint.
/// Index-type IDENTITY is likewise already unify's -- `indexPairIncompatible`
/// gates component rank, symmetry, wreath levels, and nominal tags (a named
/// `type R = Idx<3>` against the store's own axis tag is refused there today);
/// extent is the single axis of the shape it lets through.
and internal providerReadExtentClash (env: TypeEnv) (expr: Expr)
                                     (actual: IRType) (expected: IRType) : TypeError option =
    match providerReadFormName env expr with
    | None -> None
    | Some provider ->
        match env.Subst.Resolve actual, env.Subst.Resolve expected with
        | ArrayElem actualArr, ArrayElem expectedArr
                when actualArr.IndexTypes.Length = expectedArr.IndexTypes.Length ->
            let literalExtent (ix: IRIndexType) =
                match ix.Extent with
                | IRLit (IRLitInt n) -> Some n
                | _ -> None
            List.zip expectedArr.IndexTypes actualArr.IndexTypes
            |> List.indexed
            |> List.tryPick (fun (i, (annotIx, actualIx)) ->
                match literalExtent annotIx, literalExtent actualIx with
                | Some a, Some s when a <> s ->
                    Some (ProviderReadExtentMismatch (provider, i + 1, a, s))
                | _ -> None)
        | _ -> None

/// The consumption-site check: find a bound applied to an aggregate anywhere
/// in a surface annotation. Reads off the SURFACE TypeExpr, not the lowered
/// IRType -- bounds erase in `lowerTypeExpr` and never reach `IRType`.
/// Descends into aggregate COMPONENT positions (element types, tuple slots,
/// arrow slots) so `Array<Field<min=0.0> like Y>` is caught too; array INDEX
/// slots are left alone (a bound there is a BL4003 index-type question).
and internal boundedAggregateError (env: TypeEnv) (site: string) (ty: TypeExpr) : TypeError option =
    let rec go (t: TypeExpr) : string option =
        match t with
        | TyBounded (b, _, _) ->
            match boundedAggregateNoun env b with
            | Some noun -> Some noun
            | None -> go b
        | TyArray (elem, _) -> go elem
        | TyAbstractArray (elem, _, _) -> go elem
        | TyDist (_, elem, _) -> go elem
        | TyTuple ts -> ts |> List.tryPick go
        | TyFunc (args, ret) ->
            (args |> List.tryPick go) |> Option.orElseWith (fun () -> go ret)
        | TyConstrained (inner, _) -> go inner
        | _ -> None
    go ty |> Option.map (fun noun -> BoundsOnAggregate (site, noun, "the annotated value"))

// inferLetBinding (let-as-expression in function bodies and blocks) and
// checkDecl/DeclLet (top-level let declarations) share their annotation handling
// and PatVar binding logic. Extracting them here keeps the two paths in sync --
// letting them drift (one updated for bidirectional checking, the other not)
// would silently regress every top-level annotated let.
//
// The two paths still diverge afterward -- the expression-form recurses into a
// body, the top-level builds a TypedBinding record and surfaces destructured
// sub-vars to Lowering -- so we don't try to unify them entirely.

/// Resolve the value of a let binding: with annotation, drive the value via
/// bidirectional checking and store the annotation as the canonical type;
/// without, plain synthesis.
and inferLetBindingValue (env: TypeEnv) (binding: Binding) : TypeResult<TypedExpr> =
    // A defaults-carrying LAMBDA bound to a simple name: record its surface
    // params so call sites can fill omitted trailing args (the same surface
    // desugar named functions use). All four let paths (expression-form,
    // block statement, top-level DeclLet, DeclStatic) funnel through here.
    // Keyed by the TYPED lambda's first parameter id (defaultsKeyOfVar): the
    // binding's own id is minted later, on each of those paths separately.
    // G275 (2): a lambda that IS the let's value is a lambda VALUE whose open
    // parameters a later call decides; the lambda judgment records them.
    (match binding.Value.Kind with
     | ExprKind.ExprLambda _ -> env.PendingLetLambda.Value <- true
     | _ -> ())
    let inferred = inferLetBindingValueCore env binding
    env.PendingLetLambda.Value <- false
    inferred |> Result.map (fun tv ->
        // ROUND 6 (lead ruling): a COMPILER-SYNTHESIZED literal-only binding
        // -- an unannotated `__` binder, the internal-name marker every
        // elaborator (ppl, ml, math, spectra, sgs, rand, display, Grad*) uses
        // is WIDTH-FLEXIBLE: it is typed by a fresh literal variable that its
        // uses fix (within the class), and that defaults to the class's OLD
        // wide width when nothing does (Subst.MarkWideDefault, Zonk). A
        // user-written binding keeps the ordinary default.
        let tv =
            match binding.Pattern.Kind, binding.Type, literalOnlyElem env.Subst tv with
            | PatVar name, None, Some le when name.StartsWith "__" && litClassOf le <= 1 ->
                (match env.Subst.FreshLiteral le with
                 | IRTInfer v as var ->
                     env.Subst.MarkWideDefault v
                     typeLiteralOnlyAs var tv
                 | _ -> tv)
            | _ -> tv
        (match binding.Pattern.Kind, binding.Value.Kind, tv.Kind with
         | PatVar _, ExprKind.ExprLambda (parms, _, _), TExprLambda li
                when parms |> List.exists (_.Default.IsSome) && not li.Params.IsEmpty ->
             let key = li.Params.Head.VarId
             env.FuncDefaults.[key] <- (parms |> List.map (fun p -> (p.Name, p.Type, p.Default)))
             // Same identity record checkFunctionDecl keeps for a named function
             // (TypeEnv.FuncDefaultCaptures): the splice compares against it.
             env.FuncDefaultCaptures.[key] <-
                 defaultCaptureIdentities env (parms |> List.map (fun p -> (p.Name, p.Default)))
         | _ -> ())
        tv)

and inferLetBindingValueCore (env: TypeEnv) (binding: Binding) : TypeResult<TypedExpr> =
    // REDUCTION JOIN, Form 2: an array literal bound to a name is a candidate
    // LEG LIST for `reduce(name, (<&!>))`. Recorded unconditionally and read
    // by nothing else, so a literal nobody joins keeps its ordinary eager
    // array meaning -- this only preserves the SURFACE elements, which the
    // typed literal (k independent scalar folds) has already dissolved.
    (match binding.Pattern.Kind, binding.Value.Kind with
     | PatVar name, ExprKind.ExprArrayLit elems when not elems.IsEmpty ->
         env.JoinLegLists.[name] <- elems
     | _ -> ())
    // A let binding is a value-forming boundary. A wildcard `_` is a hole, not a
    // value: it is only meaningful as a compound-index coordinate (consumed by
    // dispatchAppOrIndex before it reaches here). If one survives into the bound
    // value, it has escaped and is an error -- reject cleanly at typecheck rather
    // than let it reach lowering.
    let rejectEscapedWildcard (tv: TypedExpr) : TypeResult<TypedExpr> =
        if exprContainsWildcard tv then
            Error (WildcardErr
                "wildcard `_` is not a value: it can only appear as a compound-index coordinate (e.g. B((a, _, c))), not bound in a let. A tuple carrying a hole like (a, _, c) has no meaning on its own.")
        else Ok tv
    // (An aliased grouping -- `let gk2 = gk` -- is the BL3017 sweep's job:
    // groupKeysLetRhs blesses a let RHS only when it IS the group_keys call.)
    match binding.Type with
    | Some annot ->
        let annotTy = lowerTypeExpr env annot
        // Remember which slots the annotation names as `Chunked` aliases, so
        // `segments(name)` can read the array's own tiling (structural/07).
        (match binding.Pattern.Kind, annot with
         | PatVar name, TyArray (_, its) ->
             let aliases =
                 its |> List.map (fun t ->
                     match t with
                     | TyNamed (n, []) when Map.containsKey n env.Segmentations -> Some n
                     | _ -> None)
             if aliases |> List.exists Option.isSome then env.SlotAliases.[name] <- aliases
         | _ -> ())
        // Recursive array definition (`let rec q: T = match q with ...`).
        // Route to the dedicated desugar BEFORE the generic annotated-value
        // machinery: the structured arms become the internal sequential
        // scheme (mut buffer + for-in over the leading axis) and ordinary
        // inference runs on the result -- the recursion is the semantics,
        // the loop is the compilation.
        match binding.Value.Kind with
        | ExprKind.ExprRecArray def ->
            inferRecArray env annot annotTy def binding.Value.Span
        | _ ->
        // A quantity name inside a COMPOUND unit annotation is terminal
        // (BL3011) -- surface-checked, since lowering degrades rather than
        // errors (annotTy above already lowered to the bare base).
        match unitAnnoError env annot with
        | Some err -> Error err
        | None ->
        // An array LITERAL annotated with an OPAQUE RaggedIdx (`RaggedIdx<_>`)
        // as its leading slot has no outer axis either (gap G190 (a), lead
        // decision 2026-11-09): the opaque form exists for a row PEELED from a
        // ragged parent (a kernel parameter, `let r: .. = rag(0)`), and a
        // literal is no peeled row. The interpreter read `[2.0]` as a plain
        // rank-1 array while the C++ lane refused it in codegen (BL7004) --
        // refused here, in every lane, like the named first-slot RaggedIdx.
        let opaqueRaggedLiteral =
            match env.Subst.Resolve annotTy, binding.Value.Kind with
            | ArrayElem at, ExprKind.ExprArrayLit _ ->
                (match at.IndexTypes with
                 | ix :: _ -> ix.IxKind = IxKRaggedOpaque
                 | [] -> false)
            | _ -> false
        if irTypeHasRaggedNoPrior annotTy || opaqueRaggedLiteral then
            Error (IndexTypeErr "RaggedIdx requires at least one prior index in the array's index list: the ragged extent is a per-row function of the OUTER iteration position (formalism 4.4), so there is nothing for a leading RaggedIdx to vary over. Add an outer index, e.g. Array<T like Idx<n>, RaggedIdx<lens>>.")
        elif irTypeHasBadDistOrder annotTy then
            Error (StaticValueErr "Dist order must be a compile-time integer >= 1 (a literal, `let static`, or static-function call): Dist<order, Elem like I1, ..., Ik>")
        elif irTypeHasTagWildcard annotTy then
            Error (TagWildcardNotParam
                       (match binding.Pattern.Kind with
                        | PatVar n -> $"let binding '{n}'"
                        | _ -> "let binding annotation"))
        else
        let badIrreps = irTypeBadIrrepsDetail annotTy
        if badIrreps.IsSome then
            Error (IrrepsIdxSpec badIrreps.Value)
        else
        let badPgIrreps = irTypeBadPgIrrepsDetail annotTy
        if badPgIrreps.IsSome then
            Error (PgIrrepsIdxSpec badPgIrreps.Value)
        else
        // A bad SHAPE reports BEFORE the use refusal below: a user who mistyped
        // the degree sequence should hear about the sequence, not about P3.
        let badTree = irTypeBadTreeDetail annotTy
        if badTree.IsSome then
            Error (TreeIdxShape badTree.Value)
        else
        let badAxis = irTypeUnknownAxisPath annotTy
        if badAxis.IsSome then
            Error (UnknownAxisPathErr (unknownAxisPathMessage badAxis.Value))
        else
        let wreath = irTypeWreathLevels annotTy
        if wreath.IsSome then
            Error (OrbitStorageUnsupported
                       (wreath.Value,
                        (match binding.Pattern.Kind with
                         | PatVar n -> $"let binding '{n}'"
                         | _ -> "let binding annotation")))
        else
        // NO tree door here, deliberately -- this WAS the P2 allocation door and
        // P3 is precisely its removal. A tree-annotated `let` is the
        // construction form: the value is a flat literal holding the leaves in
        // preorder, it checks through the generic annotated-literal arm below
        // (rank 1, SymNone, so neither the compact nor the wreath arm claims
        // it), and the result carries the ANNOTATION's array type, so the tree
        // slot survives while storage, allocation and teardown stay the
        // ordinary dense path. Only the TYPE carries treeness. The bad-SHAPE
        // door above still runs first, and the function-signature door still
        // refuses a tree across a call boundary.
        let badBound =
            boundedAggregateError env
                (match binding.Pattern.Kind with
                 | PatVar n -> $"let binding '{n}'"
                 | _ -> "this let binding")
                annot
        if badBound.IsSome then
            Error badBound.Value
        else
        // Nested-function desugar (parseNestedFunction): `function f(x) -> T
        // = body` becomes a let of a lambda whose binding annotation is the
        // declared RETURN type -- there is no surface TypeExpr spelling for
        // "function of unannotated params". Read it accordingly: infer the
        // lambda, then unify its return type with the annotation (a genuine
        // function-type annotation on a lambda still checks structurally
        // below).
        match binding.Value.Kind with
        | ExprKind.ExprLambda (parms, whereClause, body) when (match annotTy with FuncElem _ -> false | _ -> true) ->
            // The return annotation is lowered in the lambda's own type-var
            // scope (inferLambdaFull), exactly as checkFunctionDecl lowers a
            // top-level function's: `-> T` names the parameters' `T`.
            let lam = binding.Value
            if lam.Span.StartLine > 0 then setCurrentExprSpan lam.Span
            inferLambdaFull env parms whereClause body [] (Some annot)
            |> Result.map (fun te ->
                if te.Span.StartLine = 0 && lam.Span.StartLine > 0 then { te with Span = lam.Span } else te)
            |> Result.bind rejectEscapedWildcard
        // Monadic zero at an array annotation: `let A: Array<Float64 like Y,
        // X> = zero [|> compute]` -- zero is the additive-identity CONCEPT
        // and the annotation supplies its shape. TExprZero's lowering only
        // knows scalar shapes, so an array-typed zero is rewritten here (the
        // one place the annotation is in hand) to the explicit spelling
        //     for () in range<I1, ..., In> <@> lambda(...) -> <elem zero> |> compute
        // and re-driven through ordinary inference, riding the existing
        // former pipeline. Each range slot is the annotation slot's nominal
        // tag (IxKPlain only) or its literal static extent when untagged.
        | ExprKind.ExprZero | ExprKind.ExprCompute { Kind = ExprKind.ExprZero }
                when (match annotTy with ArrayElem _ -> true | _ -> false) ->
            let arrTy = match annotTy with ArrayElem a -> a | _ -> failwith "unreachable"
            let sp = binding.Value.Span
            // The zero is spelled at the element's WIDTH (a same-class cast of a
            // literal is that literal at the cast's width, round 6), so it
            // never takes the literal default.
            let atWidth (et: ElemType) (e: Expr) =
                match typeExprOfElem et with
                | Some te -> mkExpr sp (ExprKind.ExprTyped (e, te))
                | None -> e
            let elemZero =
                match arrTy.ElemType with
                | AnyPrimElem (ETInt32 | ETInt64 as et) -> Some (atWidth et (mkExpr sp (ExprKind.ExprLit (LitInt 0L))))
                | AnyPrimElem ETBool -> Some (mkExpr sp (ExprKind.ExprLit (LitBool false)))
                | AnyPrimElem (ETFloat32 | ETFloat64 as et) -> Some (atWidth et (mkExpr sp (ExprKind.ExprLit (LitFloat 0.0))))
                | AnyPrimElem (ETComplex64 | ETComplex128 as et) ->
                    let z () = mkExpr sp (ExprKind.ExprLit (LitFloat 0.0))
                    Some (atWidth et (mkExpr sp (ExprKind.ExprApp (mkExpr sp (ExprKind.ExprVar "complex"), [z (); z ()]))))
                | _ -> None
            let slotSurface (idx: IRIndexType) : TypeExpr option =
                if idx.Rank <> 1 || idx.IxKind <> IxKPlain then None else
                match idx.Tag with
                | Some tag when not (tag.StartsWith "__") -> Some (TyNamed (tag, []))
                // A `__` tag (a kind sentinel, a product frame's flat position
                // axis) is no name a range<> slot can resolve: use the extent.
                | _ ->
                    match idx.Extent with
                    | IRLit (IRLitInt n) -> Some (TyIdx (mkExpr sp (ExprKind.ExprLit (LitInt n))))
                    | _ -> None
            let slots = arrTy.IndexTypes |> List.map slotSurface
            // An array of a COMPOSITE element (a struct, String, tuple) has no
            // fill-former spelling -- the former's kernel would have to return
            // the composite -- so it is the explicit nested literal
            // `[[zero, ...], ...]` of the annotation's static shape, each cell
            // a `zero` the annotation types at the element; lowering and
            // IRMono.resolveTypedZerosModule give each its value
            // (IR.zeroValueOf), the way a struct array literal is written by
            // hand (structs/006).
            let staticExtents =
                arrTy.IndexTypes |> List.map (fun idx ->
                    match idx.Extent with
                    | IRLit (IRLitInt n) when idx.Rank = 1 && idx.IxKind = IxKPlain && n >= 0L -> Some n
                    | _ -> None)
            let compositeLiteral =
                if elemZero.IsSome || arrTy.IndexTypes.IsEmpty || not (staticExtents |> List.forall Option.isSome) then None
                else
                    let ns = staticExtents |> List.map Option.get
                    if ns |> List.fold (fun acc n -> if acc > IR.zeroArrayCellCap then acc else acc * n) 1L > IR.zeroArrayCellCap then None
                    else
                        let rec nest (ns: int64 list) =
                            match ns with
                            | [] -> mkExpr sp ExprKind.ExprZero
                            | n :: rest -> mkExpr sp (ExprKind.ExprArrayLit (List.init (int n) (fun _ -> nest rest)))
                        Some (nest ns)
            match compositeLiteral with
            | Some lit -> checkExpr env annotTy lit |> Result.bind (fun tv -> rejectEscapedWildcard { tv with Type = annotTy })
            | None ->
            (match elemZero, (if slots |> List.forall Option.isSome then Some (List.map Option.get slots) else None) with
             | Some zBody, Some idxTys ->
                 let params_ : LambdaParam list =
                     idxTys |> List.mapi (fun i _ -> { Name = $"__zero_i{i}"; Type = None; Default = None; NameSpan = noSpan })
                 let former = mkExpr sp (ExprKind.ExprFor (ForArrays ([], Some (mkExpr sp (ExprKind.ExprRange idxTys))), [], None))
                 let lam = mkExpr sp (ExprKind.ExprLambda (params_, None, zBody))
                 let synth = mkExpr sp (ExprKind.ExprCompute (mkExpr sp (ExprKind.ExprBinOp (Elementwise, OpApply, former, lam))))
                 checkExpr env annotTy synth |> Result.bind (fun tv ->
                     rejectEscapedWildcard { tv with Type = annotTy })
             | _ ->
                 Error (BuiltinArgErr "zero at this array annotation cannot be materialized: every axis must be a plain rank-1 index with a nominal name or a static extent, and the element type must be numeric, bool, or complex (a struct, String or tuple element needs a static shape of at most 65536 cells). Spell the fill explicitly (`for () in range<...> <@> lambda(...) -> <zero literal> |> compute`) for packed, ragged, or non-static shapes."))
        | _ ->
        // THE ascription conversion seam, and the one that makes an
        // annotation choose the magnitude: `let c: Float64<hour> = <days>`.
        //
        // Bidirectional checking bottoms out at `unify`, a pure relation over
        // types with no expression in hand, so it cannot bridge a magnitude
        // itself -- it can only accept or reject. So when the annotation
        // names a NON-UNITY magnitude, check the value against the annotation
        // with its unit stripped (bare `Float64`), which leaves every other
        // type-directed rule -- literal retyping, widening, array shape --
        // working exactly as before, then convert the synthesized magnitude
        // into the one that was asked for.
        //
        // Gated to a STRUCTURAL unit on a plain SCALAR, which is the only
        // shape a magnitude can attach to. That deliberately excludes the
        // two annotation kinds whose meaning comes from reaching
        // checkExprInner still wearing their unit: a QUANTITY (nominal names
        // are terminal and never scaled) and a `Nat<...>` (whose literal arm
        // is keyed on the annotated form). The relaxation cannot be gated on
        // the ANNOTATION's scale alone -- `let d: Float64<second> = <days>`
        // has a unity annotation and a scaled value.
        let scaledAnnot =
            match annotTy with
            | IRTUnitAnnotated (IRTScalar _, u) when u.Nominal.IsNone -> Some u
            | _ -> None
        let checkedValue =
            match scaledAnnot with
            | None -> checkUnderDeclaredLayout env annotTy binding.Value
            | Some dst ->
                // UnitTarget lets an additive RHS convert its operands
                // straight into `dst`; convertScaleTo then finds nothing left
                // to do. It stays a fallback, not a replacement: an RHS that
                // is not an additive binop (a bare variable, a call) still
                // gets its single conversion here.
                checkExpr { env with UnitTarget = Some dst } (IR.stripUnits annotTy) binding.Value
                |> Result.bind (convertScaleTo env "assignment" dst)
        checkedValue |> Result.bind (fun tv ->
            // G190 (a), the ASCRIPTION boundary (Codex design review of C3):
            // `let r: Array<T like RaggedIdx<_>> = d` over a DENSE `d` would
            // hide the dense provenance from the call-boundary refusal (Zonk,
            // TExprApp) behind the annotation installed just below. The opaque
            // form is the type of a row PEELED from a ragged parent; a value
            // that is not a ragged row cannot take it. An array literal is
            // left to the literal refusal (Zonk, TExprArrayLit).
            // (tuple components too: Codex diff review round 2)
            let rec hides (a: IRType) (v: IRType) =
                match IR.stripUnits (env.Subst.Resolve a), IR.stripUnits (env.Subst.Resolve v) with
                | ArrayElem aa, ArrayElem va ->
                    (match aa.IndexTypes, va.IndexTypes with
                     | ax :: _, vx :: _ -> ax.IxKind = IxKRaggedOpaque && not (isRaggedRowKind vx.IxKind)
                     | _ -> false)
                | IRTTuple ats, IRTTuple vts when ats.Length = vts.Length -> List.exists2 hides ats vts
                | _ -> false
            // R7, the same boundary for a GROUPED value: the annotation below
            // would replace its grouped slots (IxKGroupOuter / IxKGroupMember)
            // with plain ones, and every later consumer then read the row-
            // pointer table as a dense array -- a wrong value (BL8003 on a
            // non-empty group) or BL9002. A grouped value keeps its type.
            let rec groupedIn (fuel: int) (t: IRType) =
                fuel > 0 &&
                match IR.stripUnits (env.Subst.Resolve t) with
                | ArrayElem at ->
                    at.IndexTypes |> List.exists (fun ix -> ix.IxKind = IxKGroupMember)
                    || groupedIn (fuel - 1) at.ElemType
                | IRTTuple ts -> ts |> List.exists (groupedIn (fuel - 1))
                | _ -> false
            if groupedIn 64 tv.Type && not (groupedIn 64 annotTy) then
                setCurrentExprSpan binding.Value.Span
                let shown = match binding.Pattern.Kind with PatternKind.PatVar n -> n | _ -> "_"
                Error (GroupedValueEscapes $"the binding '{shown}', annotated with a plain array type,")
            else
            match hides annotTy tv.Type, binding.Value.Kind with
            | true, kind when not kind.IsExprArrayLit ->
                Error (EnumKeyObserved "a dense array bound to an opaque `RaggedIdx<_>` annotation: that form is the type of a row PEELED from a ragged parent, and a dense array has no ragged parent to stand for (formalism 4.4). Bind a row of a ragged array (`R(0)` for `R: Array<T like Idx<n>, RaggedIdx<lens>>`), or annotate the binding dense (`Array<T like Idx<k>>`).")
            | _ ->
            // Prefer the annotation as the canonical type -- it can be more
            // specific than what the value synthesized to.
            rejectEscapedWildcard { tv with Type = annotTy })
    | None -> inferExpr env binding.Value |> Result.bind rejectEscapedWildcard

/// THE DECLARED-LAYOUT RULE (docs/plans/plan-identity-transpose-compute.md
/// 2.3, LANGUAGE CHANGE; formalism 8.3 "Declared signatures and
/// ascriptions"): an explicit DENSE array type -- a `let` ascription, or a
/// declared return type for its body -- fixes the layout AND the elaboration of
/// the value it annotates. When the ordinary elaboration does not meet it
/// (`method_for(A, A) <@> comm-kernel`, `gram(A, A)`, a sharing-specialized
/// call, each packed), the value is checked again with every operand identity
/// at its own level fresh (TypeEnv.DenseAscription; cleared inside nested
/// callable bodies): no identity group forms and no call is sharing-
/// specialized, so it is elaborated dense, as declared (it was BL3001). A value
/// the ordinary check accepts is untouched. The discarded attempt's side
/// channels -- warnings, the warning log, pin suggestions, deduced facts, the
/// editor's deductions, the spans -- are restored before the re-check, and a
/// re-check that fails restores the first attempt's and reports ITS refusal.
/// A restore point for the WARNING side channels (both warning logs, pin
/// suggestions, deduced facts, IDE deductions): call the returned function to
/// drop everything appended since. The module ring's replay sweep re-checks
/// its recursion's step; what that re-check reports, x's own check already did.
and warningChannelsRestorePoint (env: TypeEnv) : unit -> unit =
    let ws = List.ofSeq env.Warnings
    let log = Blade.TypeEnv.WarningLog.get ()
    let pins = Blade.TypeCheckIde.PinSuggestions.get ()
    let facts = Blade.TypeEnv.DeducedFacts.get ()
    let pairs, packs, kernels = IdeDeductions.pairs.Value, IdeDeductions.packs.Value, IdeDeductions.kernels.Value
    fun () ->
        env.Warnings.Clear()
        env.Warnings.AddRange(ws)
        Blade.TypeEnv.WarningLog.reset ()
        for d in log do Blade.TypeEnv.WarningLog.add d
        Blade.TypeCheckIde.PinSuggestions.reset ()
        for (m, sp) in pins do Blade.TypeCheckIde.PinSuggestions.add m sp
        Blade.TypeEnv.DeducedFacts.reset ()
        for (f, sp) in facts do Blade.TypeEnv.DeducedFacts.add f sp
        IdeDeductions.pairs.Value <- pairs
        IdeDeductions.packs.Value <- packs
        IdeDeductions.kernels.Value <- kernels

/// A TRIAL check whose every trace is rolled back (checkUnderDeclaredLayout's
/// discipline: side channels, plan records, the substitution, instances and
/// pending applications): does `expr` check in `env`?
and trialChecks (env: TypeEnv) (expr: Expr) : bool =
    let rollback = trialRestorePoint env
    let ok = try inferExpr env expr |> Result.isOk with _ -> false
    rollback ()
    ok

/// The restore point `trialChecks` rolls back to, on its own: call the returned
/// function to undo every trace of the checking done since (side channels, plan
/// records, the substitution, instances, pending applications, the span cells).
and trialRestorePoint (env: TypeEnv) : unit -> unit =
    let restoreWarnings = warningChannelsRestorePoint env
    let spans0 = saveSpanCells ()
    let pending0 = env.PendingArrowApps.Count
    let obligations0 = env.CurrentGenericObligations |> Option.map (fun o -> o.Count)
    let decisions0 = Blade.Effects.Decisions.mark ()
    let subst0 = env.Subst.Snapshot()
    let arrowKeys0 = System.Collections.Generic.HashSet<string>(env.ArrowVariants.Keys)
    let shareKeys0 = System.Collections.Generic.HashSet<string>(env.SharingVariants.Keys)
    let variantDecls0 = env.ArrowVariantDecls.Count
    // Every other table a check appends to: id- or node-keyed entries the
    // trial minted are removed; the string-keyed ones are restored whole.
    let keysOf (d: System.Collections.Generic.Dictionary<'k, 'v>) = System.Collections.Generic.HashSet<'k>(d.Keys)
    let trimDict (d: System.Collections.Generic.Dictionary<'k, 'v>) (k0: System.Collections.Generic.HashSet<'k>) =
        for k in List.ofSeq d.Keys do
            if not (k0.Contains k) then d.Remove k |> ignore
    let trimSet (s: System.Collections.Generic.HashSet<'k>) (k0: System.Collections.Generic.HashSet<'k>) =
        for k in List.ofSeq s do
            if not (k0.Contains k) then s.Remove k |> ignore
    let declared0 = System.Collections.Generic.HashSet<IRId>(env.DeclaredFuncIds)
    let effects0 = keysOf env.FuncEffects
    let witness0 = keysOf env.FuncWitnessBodies
    let retargets0 = keysOf env.ArrowRetargets
    let openArgs0 = System.Collections.Generic.HashSet<TypedExpr>(env.OpenCallArgs, HashIdentity.Reference)
    let parallel0 = keysOf env.FuncParallel
    let foldBuiltin0 = keysOf env.FuncFoldBuiltin
    let mutualRet0 = List.ofSeq env.MutualReturnFuncs
    let joinLegs0 = List.ofSeq env.JoinLegLists
    fun () ->
        trimSet env.DeclaredFuncIds declared0
        trimDict env.FuncEffects effects0
        trimDict env.FuncWitnessBodies witness0
        trimDict env.ArrowRetargets retargets0
        for a in List.ofSeq env.OpenCallArgs do
            if not (openArgs0.Contains a) then env.OpenCallArgs.Remove a |> ignore
        trimDict env.FuncParallel parallel0
        trimDict env.FuncFoldBuiltin foldBuiltin0
        env.MutualReturnFuncs.Clear()
        for kv in mutualRet0 do env.MutualReturnFuncs.[kv.Key] <- kv.Value
        env.JoinLegLists.Clear()
        for kv in joinLegs0 do env.JoinLegLists.[kv.Key] <- kv.Value
        restoreWarnings ()
        restoreSpanCells spans0
        Blade.Effects.Decisions.rollback decisions0
        env.Subst.Restore subst0
        for k in List.ofSeq env.ArrowVariants.Keys do
            if not (arrowKeys0.Contains k) then env.ArrowVariants.Remove k |> ignore
        for k in List.ofSeq env.SharingVariants.Keys do
            if not (shareKeys0.Contains k) then env.SharingVariants.Remove k |> ignore
        if env.ArrowVariantDecls.Count > variantDecls0 then
            env.ArrowVariantDecls.RemoveRange(variantDecls0, env.ArrowVariantDecls.Count - variantDecls0)
        if env.PendingArrowApps.Count > pending0 then
            env.PendingArrowApps.RemoveRange(pending0, env.PendingArrowApps.Count - pending0)
        (match env.CurrentGenericObligations, obligations0 with
         | Some o, Some n when o.Count > n -> o.RemoveRange(n, o.Count - n)
         | _ -> ())

and checkUnderDeclaredLayout (env: TypeEnv) (expected: IRType) (expr: Expr) : TypeResult<TypedExpr> =
    // A dense array, or a tuple holding one (the rule applies at each annotated
    // array boundary; the whole value is re-checked).
    let rec hasDenseArray (fuel: int) (t: IRType) =
        fuel > 0
        && (match IR.stripUnits (env.Subst.Resolve t) with
            | ArrayElem at ->
                not at.IndexTypes.IsEmpty
                && at.IndexTypes |> List.forall (fun ix -> ix.Rank <= 1 && ix.Symmetry = SymNone && ix.IxKind = IxKPlain)
            | IRTTuple ts -> ts |> List.exists (hasDenseArray (fuel - 1))
            | _ -> false)
    if not (hasDenseArray 8 expected) || env.DenseAscription then checkExpr env expected expr else
    let snapshot () =
        (List.ofSeq env.Warnings, Blade.TypeEnv.WarningLog.get (), Blade.TypeCheckIde.PinSuggestions.get (),
         Blade.TypeEnv.DeducedFacts.get (), IdeDeductions.pairs.Value, IdeDeductions.packs.Value,
         IdeDeductions.kernels.Value, saveSpanCells ())
    let restore (ws, log, pins, facts, pairs, packs, kernels, spans) =
        env.Warnings.Clear()
        env.Warnings.AddRange(ws: string list)
        Blade.TypeEnv.WarningLog.reset ()
        for d in log do Blade.TypeEnv.WarningLog.add d
        Blade.TypeCheckIde.PinSuggestions.reset ()
        for (m, sp) in pins do Blade.TypeCheckIde.PinSuggestions.add m sp
        Blade.TypeEnv.DeducedFacts.reset ()
        for (f, sp) in facts do Blade.TypeEnv.DeducedFacts.add f sp
        IdeDeductions.pairs.Value <- pairs
        IdeDeductions.packs.Value <- packs
        IdeDeductions.kernels.Value <- kernels
        restoreSpanCells spans
    // The declaration-level accumulators the discarded attempt appended to
    // (open arrow applications, generic casts) are cut back as well: their
    // entries name nodes of a tree nobody keeps.
    let pending0 = env.PendingArrowApps.Count
    let obligations0 = env.CurrentGenericObligations |> Option.map (fun o -> o.Count)
    let before = snapshot ()
    let decisions0 = Blade.Effects.Decisions.mark ()
    // The substitution itself (Codex second look: a failed attempt's
    // unifications -- an unannotated parameter bound to the packed branch --
    // must not constrain the re-check), and with it every instance the attempt
    // made (its checked types lean on bindings now undone): array and sharing
    // instances are forgotten and their declarations withdrawn.
    let subst0 = env.Subst.Snapshot()
    let arrowKeys0 = System.Collections.Generic.HashSet<string>(env.ArrowVariants.Keys)
    let shareKeys0 = System.Collections.Generic.HashSet<string>(env.SharingVariants.Keys)
    let variantDecls0 = env.ArrowVariantDecls.Count
    match checkExpr env expected expr with
    | Error e ->
        let failed = snapshot ()
        restore before
        // ...and its `blade plan` records.
        Blade.Effects.Decisions.rollback decisions0
        env.Subst.Restore subst0
        for k in List.ofSeq env.ArrowVariants.Keys do
            if not (arrowKeys0.Contains k) then env.ArrowVariants.Remove k |> ignore
        for k in List.ofSeq env.SharingVariants.Keys do
            if not (shareKeys0.Contains k) then env.SharingVariants.Remove k |> ignore
        if env.ArrowVariantDecls.Count > variantDecls0 then
            env.ArrowVariantDecls.RemoveRange(variantDecls0, env.ArrowVariantDecls.Count - variantDecls0)
        if env.PendingArrowApps.Count > pending0 then
            env.PendingArrowApps.RemoveRange(pending0, env.PendingArrowApps.Count - pending0)
        (match env.CurrentGenericObligations, obligations0 with
         | Some o, Some n when o.Count > n -> o.RemoveRange(n, o.Count - n)
         | _ -> ())
        (match checkExpr { env with DenseAscription = true } expected expr with
         | Ok tv -> Ok tv
         | Error _ ->
             restore failed
             Error e)
    | r -> r

/// Recursive array definition, desugared here. The parser has already
/// enforced the structural form (base arm `zero -> zero`, optional seed arm
/// `zero :: s -> zero :: SEED`, inductive arm `prefix :: n -> prefix ::
/// SLICE`), which gives termination by construction: the recursion walks
/// the extent down to the empty array. Here the scheme is rewritten to the
/// INTERNAL sequential form (a mut buffer + for-in over the leading axis --
/// the same nodes the compiler's own generators emit) and checked by the
/// ordinary machinery, so prefix reads become plain buffer reads and no
/// self-referential typing is needed.
///
/// Current bounds (deliberate -- they box out the halting problem):
///   - the leading (recursion) axis extent must be STATIC;
///   - rank-1 arrays with Float/Int elements (whole-slice writes for
///     rank >= 2 land with the sequential-evolution port);
///   - the slice expression reads the prefix at earlier ordinals only
///     (the structural form guarantees the sweep order; lag validity
///     inside a step is the user's assertion, as it was imperatively).
/// The storage decision for one block-level `let rec` given the statements
/// that follow it (plan-rec-array-windowing.md sections 3-5; PM33/PM34).
/// `Some (stmts, tail', final')`: window -- `stmts` replace the binding and
/// the rest of the block reads snapshots; `None`: full buffer, as before
/// (also when the windowed desugar itself fails, so the full path reports its
/// own diagnostics). Every outcome is a `rec-window v1` record (`blade
/// plan`). The syntactic checks run first, so a demotion on syntax alone
/// never lowers the annotation.
and recArrayWindowDecision (env: TypeEnv) (binding: Binding) (tail: Stmt list) (final: Expr option)
                           : (Stmt list * Stmt list * Expr option) option =
    match binding.Pattern.Kind, binding.Value.Kind, binding.Type with
    | PatternKind.PatVar name, ExprKind.ExprRecArray def, Some annot ->
        let span = binding.Value.Span
        let record outcome evidence =
            Blade.Effects.Decisions.record
                { Blade.Effects.Rule = "rec-window"; Version = 1; Span = span
                  Subject = name; Outcome = outcome; Evidence = evidence }
        let decline (why: string) = record (Blade.Effects.Declined why) []; None
        if not (Blade.Optimize.recWindowEnabled ()) then decline "disabled by BLADE_REC_WINDOW"
        elif def.Guard.IsSome then decline "a `while` guard: its freeze epilogue repeats the last written row over the rest of the extent"
        elif (Blade.Optimize.recognizeFreezeIdiom (fun _ -> None) def).IsSome then
            decline "the freeze-idiom shape (`if G then STEP else prefix(n - 1)`) takes the guarded form"
        else
        match annot with
        | TyArray (_, _ :: _) ->
            match recArrayMaxLag def with
            | Error why -> decline why
            | Ok k ->
            match recArrayTailReads name tail final with
            | Error why -> decline why
            | Ok snaps ->
            let annotTy = lowerTypeExpr env annot
            match annotTy with
            | ArrayElem at when not at.IndexTypes.IsEmpty ->
                let lit (ix: IRIndexType) = match ix.Extent with IRLit (IRLitInt v) -> Some v | _ -> None
                match lit at.IndexTypes.Head, at.IndexTypes.Tail |> List.map lit with
                | Some n, trailingOpt when trailingOpt |> List.forall Option.isSome ->
                    match recArrayWindowVerdict k snaps n with
                    | Error why -> decline why
                    | Ok () ->
                    match recArrayLower inferEntries env annot annotTy def span (Some (k, snaps)) false with
                    | Ok (RecWindowStmts stmts) ->
                        let rw = recSnapRewriter name
                        let tail' = tail |> List.map (Blade.Unfold.mapStmtPre rw)
                        let final' = final |> Option.map (Blade.Unfold.mapExprPre rw)
                        let cells = trailingOpt |> List.fold (fun a o -> a * Option.get o) 1L
                        let resident = k + 1L + int64 snaps.Length
                        record Blade.Effects.Applied
                            [ $"ring of {k + 1L} rows (max lag K = {k}) in place of the {n}-row trajectory, {cells} cells a row"
                              (if snaps.IsEmpty then "no later row reads"
                               else "snapshots of rows " + (snaps |> List.map string |> String.concat ", ") + $" for the reads after it")
                              $"resident rows {n} -> {resident} ({n * cells} -> {resident * cells} cells)"
                              $"`{name}` dissolves: every later use is a fixed-row read, rewritten to its snapshot" ]
                        Some (stmts, tail', final')
                    | Ok (RecFullTyped _) -> decline "internal: the windowed desugar returned a full value"
                    | Error e -> decline $"the windowed desugar failed ({e}); the full buffer reports its own diagnostics"
                | _ -> decline "a non-static extent (the full desugar reports it)"
            | _ -> decline "the annotation is not an array type"
        | _ -> decline "the annotation is not a literal `Array<...>` (the ring's head slot is rebuilt from its slots)"
    | _ -> None

/// The MODULE-scope arm of the ring (plan-rec-array-windowing.md section 7,
/// P3; round 4 wave 4 B1). A module-level `let rec x` is auto-printed, so its
/// whole history is observable; P1 declines it. Here x STAYS a binding (its
/// full desugar, which the interpreter and the LLVM lane keep), and the
/// declarations after it gain:
///   * `let __replay_<x> = { ring sweep with a row marker after each row }`
///     -- the C++ lane's schedule for x (CodeGenBinding.planRecPrintReplays:
///     run once at its slot, again at x's print; x never stored);
///   * `let __snap_<p>_<x> = x(p)` for every fixed row the later
///     declarations read, those reads rewritten to the snapshot (the C++ lane
///     fills them from the sweep; the other lanes read x).
/// Admitted only when every later use of x is a fixed-row read (functions'
/// bodies included), no other module of the program imports this one, and
/// the ring is smaller than the trajectory (the P1 coverage gate). Returns
/// the declarations that follow x (the inserted ones first), or None.
and recArrayModuleDecision (env: TypeEnv) (importedElsewhere: string option) (binding: Binding) (later: Located<Decl> list)
                           : Located<Decl> list option =
    match binding.Pattern.Kind, binding.Value.Kind, binding.Type with
    | PatternKind.PatVar name, ExprKind.ExprRecArray def, Some annot ->
        let span = binding.Value.Span
        let record outcome evidence =
            Blade.Effects.Decisions.record
                { Blade.Effects.Rule = "rec-window"; Version = 1; Span = span
                  Subject = name; Outcome = outcome; Evidence = evidence }
        let decline (why: string) = record (Blade.Effects.Declined ("module scope: " + why)) []; None
        let mentions (o: obj) = (sprintf "%A" o).Contains ("\"" + name + "\"")
        if not (Blade.Optimize.recWindowEnabled ()) then decline "disabled by BLADE_REC_WINDOW"
        elif not (Blade.Optimize.recReplayEnabled ()) then decline "disabled by BLADE_REC_REPLAY"
        elif importedElsewhere.IsSome then decline importedElsewhere.Value
        elif def.Guard.IsSome then decline "a `while` guard: its freeze epilogue repeats the last written row over the rest of the extent"
        elif (Blade.Optimize.recognizeFreezeIdiom (fun _ -> None) def).IsSome then
            decline "the freeze-idiom shape (`if G then STEP else prefix(n - 1)`) takes the guarded form"
        else
        match annot with
        | TyArray (_, _ :: _) ->
            match recArrayMaxLag def with
            | Error why -> decline why
            | Ok k ->
            // The later declarations as a block tail: a `let` is a statement,
            // a function body an expression (its parameters must not rebind
            // x, and nothing else in it may name x); any other declaration
            // naming x is a use this scan cannot classify.
            let tailOrErr =
                later |> List.fold (fun acc d ->
                    match acc with
                    | Error _ -> acc
                    | Ok stmts ->
                        match d.Value with
                        | DeclLet b -> Ok (stmts @ [ StmtLet b ])
                        | DeclFunction f ->
                            if f.Name = name || f.Params |> List.exists (fun p -> p.Name = name) then
                                Error $"function `{f.Name}` rebinds `{name}`"
                            elif mentions (box { f with Body = mkExpr f.Body.Span (ExprKind.ExprLit LitUnit) }) then
                                Error $"function `{f.Name}`'s signature mentions `{name}`"
                            else Ok (stmts @ [ StmtExpr f.Body ])
                        | other when mentions (box other) -> Error $"a later declaration other than a `let` or a function mentions `{name}`"
                        | _ -> Ok stmts) (Ok [])
            match tailOrErr with
            | Error why -> decline why
            | Ok tail ->
            match recArrayTailReads name tail None with
            | Error why -> decline why
            | Ok snaps ->
            let annotTy = lowerTypeExpr env annot
            match annotTy with
            | ArrayElem at when not at.IndexTypes.IsEmpty ->
                let lit (ix: IRIndexType) = match ix.Extent with IRLit (IRLitInt v) -> Some v | _ -> None
                match lit at.IndexTypes.Head, at.IndexTypes.Tail |> List.map lit with
                | Some n, trailingOpt when trailingOpt |> List.forall Option.isSome ->
                    match recArrayWindowVerdict k snaps n with
                    | Error why -> decline why
                    | Ok () ->
                    match recArrayLower inferEntries env annot annotTy def span (Some (k, [])) true with
                    | Ok (RecWindowStmts stmts) when
                            // TRIAL CHECK of the sweep, every trace rolled back: a ring the
                            // checker refuses declines here instead of refusing a program
                            // the full buffer accepts.
                            not (trialChecks env (mkExpr span (ExprBlock (stmts, None)))) ->
                        decline "the ring sweep does not check (the full buffer reports its own diagnostics)"
                    | Ok (RecWindowStmts stmts) ->
                        let at' (v: Decl) : Located<Decl> = { Value = v; Span = span }
                        let mkLet (n: string) (value: Expr) : Decl =
                            DeclLet { Mutability = BindLet; Pattern = mkPat span (PatVar n); Type = None; Value = value }
                        let replayDecl =
                            at' (mkLet (Blade.IR.recReplayBindingPrefix + name) (mkExpr span (ExprBlock (stmts, None))))
                        let snapDecls =
                            snaps |> List.map (fun p ->
                                at' (mkLet (recSnapName name p)
                                        (mkExpr span (ExprApp (mkExpr span (ExprVar name), [ mkExpr span (ExprLit (LitInt p)) ])))))
                        let rw = recSnapRewriter name
                        let later' =
                            later |> List.map (fun d ->
                                match d.Value with
                                | DeclLet b -> { d with Value = DeclLet { b with Value = Blade.Unfold.mapExprPre rw b.Value } }
                                | DeclFunction f -> { d with Value = DeclFunction { f with Body = Blade.Unfold.mapExprPre rw f.Body } }
                                | _ -> d)
                        let cells = trailingOpt |> List.fold (fun a o -> a * Option.get o) 1L
                        let resident = k + 1L + int64 snaps.Length
                        record Blade.Effects.Applied
                            [ $"module scope: a ring of {k + 1L} rows (max lag K = {k}) replays the {n}-row trajectory, {cells} cells a row"
                              (if snaps.IsEmpty then "no later row reads"
                               else "snapshots of rows " + (snaps |> List.map string |> String.concat ", ") + " for the reads after it")
                              $"resident rows {n} -> {resident} ({n * cells} -> {resident * cells} cells) in the C++ lane, which runs the sweep at `{name}`'s slot and again at its print (rule rec-print-replay); the interpreter and the LLVM lane keep the full buffer"
                              $"every later use of `{name}` is a fixed-row read, rewritten to its snapshot" ]
                        Some ([ replayDecl ] @ snapDecls @ later')
                    | Ok (RecFullTyped _) -> decline "internal: the windowed desugar returned a full value"
                    | Error e -> decline $"the windowed desugar failed ({e}); the full buffer reports its own diagnostics"
                | _ -> decline "a non-static extent (the full desugar reports it)"
            | _ -> decline "the annotation is not an array type"
        | _ -> decline "the annotation is not a literal `Array<...>` (the ring's head slot is rebuilt from its slots)"
    | _ -> None

/// Full materialization: the recursion as a block expression bound to its
/// name, exactly as it has always been emitted.
and inferRecArray (env: TypeEnv) (annot: TypeExpr) (annotTy: IRType) (def: RecArrayDef) (span: Span) : TypeResult<TypedExpr> =
    recArrayLower inferEntries env annot annotTy def span None false
    |> Result.bind (function
        | RecFullTyped tv -> Ok tv
        | RecWindowStmts _ -> Error (InternalErr $"recursive array '{def.Name}': internal error -- a full desugar returned windowed statements"))

/// Bind the primary name of a let binding (single name or placeholder) with
/// let-generalization. Only ReadOnly bindings are generalized -- assignable
/// bindings could be reassigned, making the original scheme unsound.
/// For destructuring patterns (tuple/cons/struct), the caller passes name="_"
/// and identity=None; the returned env then needs further extension with the
/// pattern's sub-names by the caller (which differs by path).
and bindLetPatVar (env: TypeEnv) (name: string) (identity: ArrayIdentity option)
                  (assign: Assignability) (tValue: TypedExpr) : IRId * TypeEnv =
    let varId = env.Builder.FreshId()
    // VALUE RESTRICTION for function values: a FUNCTION computed by an
    // application (`let gd = keep(dbl)`, `keep(lambda(x) -> x * 2)`) is ONE
    // runtime value with one emitted body, so its open variables are decided by
    // its uses, not quantified away from them. Generalized, every `gd(n)`
    // instantiated a private copy and the value's own variables fell to zonk's
    // Float64 default: `gd(4)` over `dbl(x) = x * 2` printed the Float64 8.0.
    // A lambda literal and a name keep their existing paths (a let-bound
    // lambda is decided by its first use; an alias of a declared function is
    // transparent to the call judgment).
    let expansiveFunction =
        match tValue.Kind with
        | TExprLambda _ | TExprVar _ | TExprSection _ -> false
        | _ ->
            match env.Subst.Resolve tValue.Type with
            | FuncElem _ -> true
            | _ -> false
    let scheme =
        if assign <> ReadOnly || expansiveFunction then None
        else
            let s = generalize env.Subst env.Variables tValue.Type
            if s.QuantifiedVars.IsEmpty then None else Some s
    let env' =
        match scheme with
        | Some s -> bindVarPoly name varId tValue.Type identity assign (Some tValue) s env
        | None -> bindVarFull name varId tValue.Type identity assign (Some tValue) env
    (varId, env')

/// Leaf (name, type) list for a `head :: tail` destructure of `scrutTy`.
///
/// SINGLE SOURCE OF TRUTH for cons-pattern typing: shared by top-level `let`
/// (checkDecl/DeclLet), block-scoped `let` (inferBlock's TStmtLet),
/// `let static` (checkDecl/DeclStatic) and let-as-expression
/// (inferLetBinding), so the rules below cannot drift between them --
/// letting one site diverge means fresh unconstrained type vars zonking to
/// Float64 and Lowering projecting positionally wrong (`tail` binding
/// element 1 instead of the remainder). Rules (mirrored by
/// Lowering.subBindingValue on the VALUE side):
///   * `::` is right-associative, so `a :: b :: rest` flattens to k leading
///     leaves plus exactly ONE rest leaf.
///   * Leading leaf i takes tuple element i; the rest leaf takes the whole
///     REMAINDER, re-tupled (a one-element remainder binds BARE -- Blade has
///     no 1-tuple).
///   * Anything else is a hard error, never a silent fresh type var: not a
///     tuple, too short, or a leaf that isn't a plain variable (which would
///     desync the name count from "one sub-binding per tuple slot").
and consDestructureLeaves (env: TypeEnv) (scrutTy: IRType) (h: Pattern) (t: Pattern)
                          : TypeResult<(string * IRType) list> =
    let rec flattenCons (hp: Pattern) (tp: Pattern) : Pattern list * Pattern =
        match tp.Kind with
        | PatternKind.PatCons (h2, t2) ->
            let (heads, rest) = flattenCons h2 t2
            (hp :: heads, rest)
        | _ -> ([hp], tp)
    let (headPats, restPat) = flattenCons h t
    let leafPats = headPats @ [restPat]
    let resolvedTy = env.Subst.Resolve scrutTy
    let leafNames =
        leafPats |> List.map (fun p ->
            match p.Kind with PatternKind.PatVar n -> Some n | _ -> None)
    match resolvedTy with
    | IRTTuple ts when ts.Length > headPats.Length
                       && leafNames |> List.forall Option.isSome ->
        let names = leafNames |> List.map Option.get
        let k = headPats.Length
        let tailTy =
            match ts |> List.skip k with
            | [single] -> single
            | many -> IRTTuple many
        let leafTys = (ts |> List.truncate k) @ [tailTy]
        Ok (List.zip names (leafTys |> List.map (fun ty -> env.Subst.Resolve ty)))
    | IRTPoly (baseTy, _) when leafNames |> List.forall Option.isSome ->
        // Cons-destructuring a parameter pack: `head :: tail = A`. Each head
        // leaf has the pack's element (base) type; the tail is a pack of the
        // same base with arity `k` less. Arity is symbolic until
        // monomorphization, so (unlike the tuple arm) no statically-known
        // length > k is required -- a pack too short for the heads fails
        // when its concrete arity is fixed (specializeFunction).
        let names = leafNames |> List.map Option.get
        let k = headPats.Length
        // Head leaves get FRESH vars, not the pack's base type directly (a
        // pack element read already decouples this way): if `head` WERE the
        // base var, `head + ..` arithmetic would unify the base to a scalar,
        // collapsing a `Poly<T^1>` pack's rank (params would monomorphize to
        // `double` instead of `Array<double,1>`). The fresh var only needs
        // internal consistency for body inference; the tail keeps the base
        // (so `f(tail)` sees the same element type) with a fresh arity var.
        // ...but each keeps the DECLARED RANK of the pack's element (D4):
        // `Poly<T^1>`'s head is a rank-1 value, so `head [*] f(tail)` is an
        // outer product at every arity. The fresh var carries the base caret's
        // rank as a LOWER BOUND (an array of at least that rank -- the caret's
        // exact pin would have the elementwise rules materialize `head` and
        // zip it against the recursive call, whose base instance is the
        // scalar identity arm) and its generic mark, so it stays open for
        // each instance to name (IRMono: packHeadLinks, the HM learning of
        // `let head = A_0`); not the variable itself.
        let headTys =
            List.init k (fun _ ->
                // A SETTLED base (`Poly<Float64^1>`) has nothing to collapse:
                // the head is that type.
                match IR.stripUnits (env.Subst.Resolve baseTy) with
                | ArrayElem _ -> baseTy
                | _ ->
                let v = env.Subst.Fresh()
                (match v, IR.stripUnits (env.Subst.Resolve baseTy) with
                 | IRTInfer hv, IRTInfer bv ->
                     (match env.Subst.GetArityConstraint bv with
                      | Some r when r >= 1 -> env.Subst.AddRankLowerBound(hv, r)
                      | _ -> ())
                     env.Subst.CopyPolymorphic(bv, hv)
                 | _ -> ())
                v)
        let tailArityName = $"r{env.Builder.FreshId()}"
        let tailTy = IRTPoly (baseTy, tailArityName)
        let leafTys = headTys @ [tailTy]
        Ok (List.zip names leafTys)
    | _ ->
        let patText =
            leafPats
            |> List.map (fun p ->
                match p.Kind with
                | PatternKind.PatVar n -> n
                | PatternKind.PatWildcard -> "_"
                | _ -> "<pattern>")
            |> String.concat " :: "
        Error (PatternTypeMismatch (patText, resolvedTy))

/// Declared field list of a struct-typed scrutinee, or [] when the type is not
/// a registered struct. Shared by the PatStruct destructuring arms so they all
/// read field types from the same place.
and structFieldTypesOf (env: TypeEnv) (scrutTy: IRType) : (string * IRType) list =
    match env.Subst.Resolve scrutTy with
    | IRTNamed sName ->
        match Map.tryFind sName env.TypeDefs with
        | Some (TDIStruct (_, _, fields, _)) -> fields
        | _ -> []
    | _ -> []

/// Flat leaves of a (possibly nested) tuple type, as (path, leafType) pairs
/// in left-to-right order. `path` is the STRUCTURAL indices from root to
/// leaf, OUTERMOST FIRST: for ((alpha, beta), gamma) the leaves are ([0;0],alpha), ([0;1],beta),
/// ([1],gamma) -- i.e. get<0>(get<0>(x)), get<1>(get<0>(x)), get<1>(x). A
/// non-tuple is one leaf at the empty path.
///
/// Paths rather than a flat index: a flat projection IS the composition of
/// structural projections along the path, letting expression position
/// destructure with ordinary chained TExprTupleIndex nodes (TypedAst has no
/// `isFlat` flag; adding one would ripple through TypedAst, Lowering, Zonk
/// and five sites here). CodeGen's flat IRTupleProj arm computes exactly
/// this path at emit time, so the two agree on what a flat projection means.
///
/// LEAF ORDER MUST MATCH IR.flattenTupleLeaves: declaration position takes
/// flat leaf TYPES from that function while expression position takes leaf
/// PATHS from this one, so the same source text must destructure identically
/// at top level and inside an expression. The recursion is deliberately the
/// same shape, so `flatTupleLeafPaths ty |> List.map snd =
/// IR.flattenTupleLeaves ty` holds by structural induction.
and flatTupleLeafPaths (ty: IRType) : (int list * IRType) list =
    match ty with
    | IRTTuple ts ->
        ts
        |> List.mapi (fun i t ->
               flatTupleLeafPaths t |> List.map (fun (path, leafTy) -> (i :: path, leafTy)))
        |> List.concat
    | _ -> [([], ty)]

/// Desugar a destructuring `let` in EXPRESSION position into a CHAIN of
/// single-name `TExprLet`s: one temp bound to the scrutinee, then one let per
/// leaf bound to its projection out of that temp, innermost-out.
///
/// WHY a chain instead of the SubBindings mechanism the declaration and
/// statement forms use: `TExprLet` is `name * varId * value * body` -- one
/// name, no sub-binding slot -- and widening it would ripple through ~13 use
/// sites across TypeCheck, Lowering and Zonk. wrapMutualReturnBody already
/// binds its return-tuple leaves with exactly this nested-let shape, so the
/// desugar needs no DU change, no Zonk change and no Lowering change.
///
/// Without this chain, every leaf would get a fresh type variable and a
/// varId that the single emitted `TExprLet` does NOT bind, so any body
/// reference to a leaf would lower to an IRId that nothing introduces.
///
/// Each leaf supplies its own value-builder rather than a positional index, so
/// wildcard slots (which bind nothing) cannot shift the projections of the
/// leaves after them, and struct leaves can project by field instead.
and destructureLetChain (env: TypeEnv) (tValue: TypedExpr)
                        (leaves: (string * IRType * (TypedExpr -> TypedExpr)) list)
                        (body: Expr) : TypeResult<TypedExpr> =
    let scrutTy = env.Subst.Resolve tValue.Type
    let tmpId = env.Builder.FreshId()
    let tmpName = "__destructure_src"
    let tmpVar = mkTyped (TExprVar (tmpName, tmpId, None)) scrutTy
    let prepared =
        leaves |> List.map (fun (n, ty, mkValue) -> (n, ty, env.Builder.FreshId(), mkValue tmpVar))
    let mutable env' = env
    for (n, ty, leafId, _) in prepared do
        env' <- bindVarSimple n leafId ty env'
    inferExpr env' body |> Result.map (fun tBody ->
        let withLeaves =
            List.foldBack (fun (n, _ty, leafId, leafValue) acc ->
                mkTyped (TExprLet (n, leafId, leafValue, acc)) tBody.Type) prepared tBody
        mkTyped (TExprLet (tmpName, tmpId, tValue, withLeaves)) tBody.Type)

and inferLetBinding env binding body : TypeResult<TypedExpr> =
    // Bidirectional checking pushes annotations into literal/constructor
    // positions -- see inferLetBindingValue. Then dispatch on the binding
    // pattern to bind names into the body's environment.
    let valueResult = inferLetBindingValue env binding
    valueResult |> Result.bind (fun tValue ->
        let assign = assignOfBindingMut binding.Mutability
        match binding.Pattern.Kind with
        | PatternKind.PatVar name ->
            let (varId, env') = bindLetPatVar env name (Some (AIDVariable name)) assign tValue
            inferExpr env' body |> Result.map (fun tBody ->
                mkTyped (TExprLet (name, varId, tValue, tBody)) tBody.Type)

        | PatternKind.PatTuple pats ->
            // A tuple destructure in expression position desugars to a nested
            // let chain (destructureLetChain). TWO pattern shapes, tried in
            // the same priority order as declaration position (checkDecl's
            // PatTuple arm): STRUCTURAL -- (w, z) against ((alpha, beta), gamma): one
            // leaf per top-level slot, one projection each. FLAT -- (x, y, z)
            // against ((alpha, beta), gamma): one leaf per FLATTENED leaf, reached by a
            // PATH of projections (without this arm, this shape falls
            // through to letValueOnlyChain and binds NOTHING, so every name
            // resurfaces as UnboundVariable). Structural wins the tie (a flat
            // tuple is its own flattening).
            //
            // Paths rather than a flat projection node: TypedAst's
            // TExprTupleIndex has no flat flag, but a flat projection IS the
            // composition of structural projections along the path, so
            // chaining ordinary TExprTupleIndex nodes says the same thing
            // with no DU/Lowering/Zonk change. flatTupleLeafPaths documents
            // why its leaf order agrees with IR.flattenTupleLeaves, which the
            // declaration-position arm flattens with.
            let resolvedTy = env.Subst.Resolve(tValue.Type)
            let intLit i = mkTyped (TExprLit (LitInt (int64 i))) (IRTScalar ETInt64)
            // Fold one leaf's path into chained projections, innermost first.
            // Each intermediate node is given the type it actually has, read
            // back out of its parent's resolved tuple type: Lowering's
            // TExprTupleIndex arm only emits a static IRTupleProj when the
            // OPERAND's type is an IRTTuple, so an intermediate typed wrongly
            // would silently divert the rest of the chain onto the poly-pack
            // path (IRPolyIndex) -- a fresh miscompile rather than a fix.
            let projectPath (path: int list) (src: TypedExpr) : TypedExpr =
                path |> List.fold (fun (acc: TypedExpr) idx ->
                    let elemTy =
                        match env.Subst.Resolve acc.Type with
                        | IRTTuple ts when idx < ts.Length -> ts.[idx]
                        // Unreachable: every path here was produced FROM this
                        // very type. Keeping the parent's type (rather than a
                        // fresh variable) at least leaves the node internally
                        // consistent if that ever stops holding.
                        | other -> other
                    mkTyped (TExprTupleIndex (acc, intLit idx)) elemTy) src
            let structural =
                match resolvedTy with
                | IRTTuple ts when ts.Length = pats.Length ->
                    Some (ts |> List.mapi (fun i t -> ([i], t)))
                | _ -> None
            // Flat leaves are only consulted when the structural arity does NOT
            // match, and only when the pattern supplies exactly one leaf per
            // flat leaf -- the same test checkDecl's PatTuple arm applies before
            // it switches to IR.flattenTupleLeaves.
            let flat =
                match structural, resolvedTy with
                | None, IRTTuple _ ->
                    let leaves = flatTupleLeafPaths resolvedTy
                    if leaves.Length = pats.Length then Some leaves else None
                | _ -> None
            // Wildcards and compound leaves bind no let, in EITHER shape. A
            // compound leaf would need a recursive desugar; leaving its names
            // unbound turns a silent dangling-IRId miscompile into an honest
            // UnboundVariable.
            let leavesOf (leafInfo: (int list * IRType) list) =
                pats
                |> List.mapi (fun i p -> (i, p))
                |> List.choose (fun (i, p) ->
                    match p.Kind with
                    | PatternKind.PatVar n ->
                        let (path, leafTy) = leafInfo.[i]
                        Some (n, env.Subst.Resolve leafTy, projectPath path)
                    | _ -> None)
            let chosen = if structural.IsSome then structural else flat
            match tupleDestructureArityError env pats tValue.Type with
            // Neither reading covers the names. Without this the leaves bind
            // NOTHING and every one of them resurfaces as UnboundVariable,
            // which names the symptom rather than the cause.
            | Some err -> Error err
            | None ->
            match chosen with
            | Some leafInfo -> destructureLetChain env tValue (leavesOf leafInfo) body
            | None -> letValueOnlyChain env tValue body

        | PatternKind.PatCons (headPat, tailPat) ->
            // `let head :: tail = t` in expression position. Same leaf rules as
            // every other cons site (consDestructureLeaves), and the same hard
            // error when the scrutinee cannot be split -- binding fresh type
            // vars instead is exactly the miscompile the shared helper exists
            // to prevent.
            consDestructureLeaves env tValue.Type headPat tailPat
            |> Result.bind (fun leafTys ->
                let resolvedTy = env.Subst.Resolve(tValue.Type)
                let elemTys = match resolvedTy with IRTTuple ts -> ts | _ -> []
                let intLit i = mkTyped (TExprLit (LitInt (int64 i))) (IRTScalar ETInt64)
                let lastIdx = leafTys.Length - 1
                let leaves =
                    leafTys
                    |> List.mapi (fun i (n, ty) ->
                        let isPoly = resolvedTy.IsIRTPoly
                        let mkValue (src: TypedExpr) =
                            if i = lastIdx then
                                if isPoly then
                                    // REST leaf of a PACK: the symbolic pack tail
                                    // (dropping `i` heads), NOT a re-tuple -- the
                                    // arity is only known at monomorphization.
                                    // Matches Lowering.subBindingValue's IRTPoly arm.
                                    mkTyped (TExprPolyTail (src, i)) ty
                                else
                                // REST leaf: the remainder, re-tupled from its
                                // own index onward -- bare when one element
                                // (Blade has no 1-tuple). Identical rule to
                                // Lowering.subBindingValue, so the leaf's
                                // declared type and its value always agree.
                                let rest =
                                    [ for j in i .. elemTys.Length - 1 ->
                                        mkTyped (TExprTupleIndex (src, intLit j)) elemTys.[j] ]
                                match rest with
                                | [single] -> single
                                | many -> mkTyped (TExprTuple many) ty
                            else mkTyped (TExprTupleIndex (src, intLit i)) ty
                        (n, ty, mkValue))
                destructureLetChain env tValue leaves body)

        | PatternKind.PatStruct (_, fieldPats) ->
            // `let Point { x, y } = p` in expression position. Field leaves
            // project by NAME (TExprField), so a missing/extra field cannot
            // shift the others the way a positional index would.
            let fieldTypes = structFieldTypesOf env tValue.Type
            if fieldTypes.IsEmpty then letValueOnlyChain env tValue body
            else
                let leaves =
                    fieldPats
                    |> List.choose (fun (fieldName, p) ->
                        match p.Kind, fieldTypes |> List.tryFindIndex (fun (fn, _) -> fn = fieldName) with
                        | PatternKind.PatVar n, Some idx ->
                            let fTy = env.Subst.Resolve (snd fieldTypes.[idx])
                            Some (n, fTy, fun (src: TypedExpr) ->
                                     mkTyped (TExprField (src, fieldName, idx)) fTy)
                        | _ -> None)
                destructureLetChain env tValue leaves body

        | _ ->
            letValueOnlyChain env tValue body)

/// Fallback for a destructuring `let` in expression position whose pattern the
/// leaf desugar cannot describe (a tuple pattern whose arity matches NEITHER
/// the scrutinee's structural slot count NOR its flat leaf count, a scrutinee
/// that is not a tuple at all, an unregistered struct type, wildcard/literal/
/// variant patterns). The VALUE is still bound to a temp so
/// its effects and type survive; no leaf name is bound, because the only
/// alternative on offer is the fresh-type-var-plus-unbound-varId shape that
/// destructureLetChain exists to replace.
and letValueOnlyChain (env: TypeEnv) (tValue: TypedExpr) (body: Expr) : TypeResult<TypedExpr> =
    destructureLetChain env tValue [] body

/// BL3021: a `match` whose scrutinee is a SPECIALIZATION INDEX -- `arity(p)`
/// over a pack param, or `rank(p)` over an abstract/caret param (the operand's
/// type is still an inference var here, so the rank is answered per HM clone)
/// -- selects its arm when the specialization is cloned. The constant-match
/// fold (IRMono.foldConstIntMatch) is deliberately order-sound: a GUARDED
/// matching arm bails it, and for a recursive pack the arity specializer then
/// cannot pick the base arm, specialization shrinks past arity 0, and the
/// program dies as a raw g++ error naming the missing clone. Refuse here
/// instead: arms must be statically decidable -- unguarded integer literals
/// plus at most one unguarded catch-all (`_` or a name).
///
/// Scope: fires only when the scrutinee IS the intrinsic syntactically.
/// `match (arity(A) + 0)` and ordinary int matches keep today's behavior, and
/// `rank(x)` over a CONCRETE operand stays a plain static int match (its rank
/// folds at lowering; a guard there is an ordinary runtime branch). The
/// specializer's bail remains the backstop for anything that slips past.
and validateSpecIndexMatchArms (env: TypeEnv) (tScrutinee: TypedExpr) (cases: MatchCase list) : TypeResult<unit> =
    let scrutName =
        match tScrutinee.Kind with
        | TExprArity name -> Some $"arity({name})"
        | TExprRank inner ->
            let rec isAbstract (t: IRType) =
                match t with
                | IRTInfer _ -> true
                | IRTUnitAnnotated (i, _) -> isAbstract i
                | _ -> false
            if isAbstract (env.Subst.Resolve inner.Type) then
                match inner.Kind with
                | TExprVar (n, _, _) -> Some $"rank({n})"
                | _ -> Some "rank(...)"
            else None
        | _ -> None
    match scrutName with
    | None -> Ok ()
    | Some scrut ->
        let rec stripTyped (p: Pattern) =
            match p.Kind with
            | PatternKind.PatTyped (inner, _) -> stripTyped inner
            | _ -> p
        let mutable catchAlls = 0
        let mutable offender : string option = None
        for case in cases do
            if offender.IsNone then
                let p = stripTyped case.Pattern
                let guarded =
                    case.Guard.IsSome
                    || (match p.Kind with PatternKind.PatGuarded _ -> true | _ -> false)
                if guarded then
                    offender <- Some "a guarded arm (`if` after the pattern) reads runtime values"
                else
                    match p.Kind with
                    | PatternKind.PatLit (LitInt _) -> ()
                    | PatternKind.PatWildcard | PatternKind.PatVar _ ->
                        catchAlls <- catchAlls + 1
                        if catchAlls > 1 then
                            offender <- Some "a second catch-all arm can never be selected"
                    | _ ->
                        offender <- Some "a non-integer pattern cannot be decided against a static arity/rank"
        match offender with
        | Some off -> Error (SpecIndexMatchNotStatic (scrut, off))
        | None -> Ok ()

/// ONE match judgment, parameterized by how an arm BODY is judged -- the two
/// entry points below differ only in that, so everything else (the
/// spec-index gate, pattern checking, the Bool guard, the short-circuiting
/// fold) exists once. `judgeBody caseEnv body` returns the typed body; the
/// match node is typed `resultTy` resolved after every arm.
///
/// SHORT-CIRCUITING fold, not List.map + sequenceResults: a later SUCCESSFUL
/// arm re-stamps the ambient error span, so the first arm's error used to
/// point its caret at whichever arm inferred last (the compact-literal walker
/// fixed the identical bug class -- see its `go` fold).
and matchWith env scrutinee cases (resultTy: IRType)
              (judgeBody: TypeEnv -> Expr -> TypeResult<TypedExpr>) : TypeResult<TypedExpr> =
    inferExpr env scrutinee |> Result.bind (fun tScrutinee ->
        validateSpecIndexMatchArms env tScrutinee cases |> Result.bind (fun () ->
        let judgeCase (case: MatchCase) : TypeResult<TypedMatchCase> =
            checkPattern env tScrutinee.Type case.Pattern |> Result.bind (fun tPat ->
                // Extend env with pattern bindings
                let mutable caseEnv = env
                for (name, varId, ty) in tPat.Bindings do
                    caseEnv <- bindVarSimple name varId ty caseEnv
                // A guard is a predicate: it must be Bool, not merely
                // truthy (C truthiness was what the emitted C++ applied to
                // whatever type arrived here, arrays included).
                let tGuard =
                    case.Guard |> Option.map (fun g ->
                        inferExpr caseEnv g |> Result.bind (fun tg ->
                            unify env.Subst tg.Type (IRTScalar ETBool)
                            |> Result.mapError (fun _ ->
                                TypeMismatchErr "a match-arm guard must be Bool: the `if` condition after a pattern is a predicate (comparisons and boolean operators), not a value")
                            |> Result.map (fun () -> Some tg)))
                    |> Option.defaultValue (Ok None)
                tGuard |> Result.bind (fun guardOpt ->
                judgeBody caseEnv case.Body |> Result.map (fun tBody ->
                    ({ Pattern = tPat; Guard = guardOpt; Body = tBody } : TypedMatchCase))))
        let rec go acc cs =
            match cs with
            | [] -> Ok (List.rev acc)
            | case :: rest -> judgeCase case |> Result.bind (fun tc -> go (tc :: acc) rest)
        go [] cases |> Result.map (fun tCases ->
            matchCoverageWarnings env tScrutinee.Type scrutinee.Span cases tCases
            mkTyped (TExprMatch (tScrutinee, tCases)) (env.Subst.Resolve resultTy))))

/// Inferred match: each arm is INFERRED and unified with one result type --
/// binding, so an array arm and a scalar arm cannot coexist (it was `let _ =
/// unify`, which shipped the mismatch to g++ as an ill-typed ternary) -- and
/// then held to the SAME static extents as the arms before it
/// (`staticExtentClash`): the node's type is the first arm's, so an arm of a
/// different literal length would be read at the wrong length.
and inferMatch env scrutinee cases : TypeResult<TypedExpr> =
    let resultTy = env.Subst.Fresh()
    let unifyArm (body: Expr) (tBody: TypedExpr) =
        // unify names its FIRST argument as the expectation, and the
        // expectation here is the arms BEFORE this one (`resultTy`); the
        // report is flipped rather than the call, whose binding order
        // the arms' shared type has always relied on.
        // R29: two concrete scalar widths of one class are the `if`'s
        // mismatch (concreteScalarClash), not a promotion of the result.
        (match concreteScalarClash env.Subst resultTy tBody.Type with
         | Some e ->
             if body.Span.StartLine > 0 then setCurrentExprSpan body.Span
             Error e
         | None -> unify env.Subst tBody.Type resultTy |> flipMismatch)
        |> withUnifyContext "the arms of this `match` (the earlier arms vs this arm)"
        |> Result.bind (fun () ->
            match staticExtentClash env.Subst resultTy tBody.Type with
            | Some (d, e, a) ->
                if body.Span.StartLine > 0 then setCurrentExprSpan body.Span
                Error (ExtentAscribeMismatch ("this match arm (against the arms before it)", d + 1, e, a))
            | None -> Ok tBody)
    // `match arity(pack)` (D4, formalism 8.2): its arms are the SAME function
    // at different arities, so a concrete SCALAR arm (the empty-pack identity
    // `| 0 -> 1.0`) is not evidence about the rank of the others -- the
    // instance whose body selects it narrows to that scalar
    // (IRMono.specializeFunction's finalScalarType). Unifying it with an arm
    // built over the pack's rank-k `head` collapsed every instance to a
    // scalar. Such an arm is held back while the other arms are judged; if
    // they decide an array (or a variable known to be one), only the scalar's
    // ELEMENT is held to theirs (where the arms already know it -- the
    // mixing rule judges the rest per instance), and an open result stays
    // open for the instances to decide. With no array arm, the held arms
    // unify exactly as before.
    let isArityMatch = match scrutinee.Kind with ExprKind.ExprArity _ -> true | _ -> false
    let held = System.Collections.Generic.List<Expr * TypedExpr>()
    let mutable judgedOthers = false
    // ROUND 6: a LITERAL-ONLY arm is held back until the next non-literal arm
    // (or the end): a non-literal SCALAR arm of its class then fixes its width
    // (adaptLiteralPeers' rule for a `match`: `| 3 -> 0.1 | _ -> x64` is
    // Float64), the held arms are unified first, in source order, so a type
    // clash still reads earlier-arm-first; literal-only arms at the end take
    // the result the others fixed, or join to the widest default.
    let pending = System.Collections.Generic.List<Expr * TypedExpr>()
    let swaps = System.Collections.Generic.List<TypedExpr * TypedExpr>()
    let flushPending (target: ElemType option) : TypeResult<unit> =
        let arms = List.ofSeq pending
        pending.Clear()
        let retyped =
            match target with
            | Some et ->
                // R29: a literal-only arm that does not fit the width the
                // other arms fixed is BL3020 at the literal (the `if`'s rule).
                (match arms |> List.tryPick (fun (_, t) -> peerLiteralMisfit env et t) with
                 | Some e -> Error e
                 | None -> Ok (arms |> List.map (fun (b, t) -> (b, t, retypeLiteralOnly env.Subst et t |> Option.defaultValue t))))
            | None ->
                Ok (List.zip arms (adaptLiteralPeers env (arms |> List.map snd))
                    |> List.map (fun ((b, t), t') -> (b, t, t')))
        let rec go (xs: (Expr * TypedExpr * TypedExpr) list) =
            match xs with
            | [] -> Ok ()
            | (body, old, tNew) :: rest ->
                swaps.Add((old, tNew))
                if body.Span.StartLine > 0 then setCurrentExprSpan body.Span
                unifyArm body tNew |> Result.bind (fun _ -> go rest)
        retyped |> Result.bind go
    let sameClassScalar (t: IRType) =
        match IR.stripUnits (env.Subst.Resolve t) with
        | IRTScalar et when litClassOf et >= 0 ->
            (match pending |> Seq.tryHead |> Option.bind (fun (_, p) -> literalOnlyElem env.Subst p) with
             | Some le when litClassOf le = litClassOf et -> Some et
             | _ -> None)
        | _ -> None
    matchWith env scrutinee cases resultTy (fun caseEnv body ->
        inferExpr caseEnv body |> Result.bind (fun tBody ->
            let isScalarArm =
                isArityMatch
                && (match IR.stripUnits (env.Subst.Resolve tBody.Type) with IRTScalar _ -> true | _ -> false)
            if isScalarArm then
                held.Add((body, tBody))
                Ok tBody
            elif (literalOnlyElem env.Subst tBody).IsSome then
                (match judgedOthers, sameClassScalar resultTy with
                 | true, _ ->
                     // An earlier non-literal arm fixed the result: take it.
                     pending.Add((body, tBody))
                     let target =
                         match IR.stripUnits (env.Subst.Resolve resultTy) with
                         | IRTScalar et when litClassOf et = litClassOf (literalOnlyElem env.Subst tBody).Value -> Some et
                         | _ -> None
                     flushPending target |> Result.map (fun () -> tBody)
                 | false, _ ->
                     pending.Add((body, tBody))
                     Ok tBody)
            else
                let r =
                    if pending.Count = 0 then Ok ()
                    else flushPending (sameClassScalar tBody.Type)
                r |> Result.bind (fun () ->
                    judgedOthers <- true
                    unifyArm body tBody)))
    |> Result.bind (fun tMatch ->
        (if pending.Count = 0 then Ok () else flushPending None)
        |> Result.map (fun () ->
            if swaps.Count = 0 then tMatch
            else
                let swap (b: TypedExpr) =
                    match swaps |> Seq.tryFind (fun (old, _) -> obj.ReferenceEquals (old, b)) with
                    | Some (_, tNew) -> tNew
                    | None -> b
                match tMatch.Kind with
                | TExprMatch (sc, cs) ->
                    { tMatch with
                        Kind = TExprMatch (sc, cs |> List.map (fun c -> { c with Body = swap c.Body }))
                        Type = env.Subst.Resolve resultTy }
                | _ -> { tMatch with Type = env.Subst.Resolve resultTy }))
    |> Result.bind (fun tMatch ->
        if held.Count = 0 then Ok tMatch
        else
            let arrayish =
                judgedOthers
                && (match IR.stripUnits (env.Subst.Resolve resultTy) with
                    | ArrayElem _ -> true
                    | IRTInfer v ->
                        (match env.Subst.GetArityConstraint v, env.Subst.GetRankLowerBound v with
                         | Some k, _ when k >= 1 -> true
                         | _, Some k when k >= 1 -> true
                         | _ -> false)
                    | _ -> false)
            let rec go (arms: (Expr * TypedExpr) list) =
                match arms with
                | [] -> Ok ()
                | (body, tBody) :: rest ->
                    if body.Span.StartLine > 0 then setCurrentExprSpan body.Span
                    let r =
                        if not arrayish then unifyArm body tBody |> Result.map ignore
                        else
                            match IR.stripUnits (env.Subst.Resolve resultTy) with
                            | ArrayElem arr ->
                                unify env.Subst (IR.stripUnits tBody.Type) (IR.stripUnits arr.ElemType)
                                |> flipMismatch
                                |> withUnifyContext "the element of this scalar arm vs the arms of this `match arity(..)`"
                            | _ -> Ok ()
                    r |> Result.bind (fun () -> go rest)
            go (List.ofSeq held) |> Result.map (fun () ->
                (match IR.stripUnits (env.Subst.Resolve resultTy) with
                 | IRTInfer v when arrayish -> env.Subst.MarkPolymorphic v
                 | _ -> ())
                { tMatch with Type = env.Subst.Resolve resultTy }))

/// Bidirectional match: push the `expected` type into each arm body via
/// `checkExpr`, so a literal (or scalar) arm can flex to the expected
/// `T`/`T^k` instead of pinning the result to its concrete type -- this is what
/// lets `comoment_prod`'s `| 0 -> 1` defer into the `-> T^1` return.
///
/// checkExpr's verdict STANDS. This used to fall back to inference plus a
/// lenient unify when the body check failed, which swallowed the specific
/// error: a 2-element literal arm checked against `Idx<3>` passed here (and
/// died later as BL7001) while the same literal outside a match is BL3007.
and checkMatch env (expected: IRType) scrutinee cases : TypeResult<TypedExpr> =
    matchWith env scrutinee cases expected (fun caseEnv body -> checkExpr caseEnv expected body)

/// Mechanism 2 -- scalar -> concretely-shaped-array broadcast fill. When a scalar
/// value is checked against a concrete array type whose extents are statically
/// known (`let a: Array<f64, Idx<3>> = s`), materialize a fill that broadcasts
/// the scalar across the shape: one `replicate` level per index, innermost
/// first, so the outermost node carries the full target type. Element types must
/// match exactly (no implicit int->float). Returns None when not applicable --
/// including a shapeless rank-k var, whose shape isn't known here -- so the
/// caller's normal mismatch error is preserved.
and tryScalarFill (env: TypeEnv) (tE: TypedExpr) (expected: IRType) : TypedExpr option =
    match env.Subst.Resolve tE.Type, env.Subst.Resolve expected with
    | IRTScalar se, ArrayElem arr
        when arr.ElemType = IRTScalar se
             && not arr.IndexTypes.IsEmpty
             && arr.IndexTypes |> List.forall (fun ix ->
                    match ix.Extent with IRLit (IRLitInt _) -> true | _ -> false) ->
        // Build nested array literals: the outer index gives N rows, each row is
        // the fill over the remaining indices; the innermost row is N copies of
        // the scalar. Mirrors the array-literal checking arm so it declares a
        // real array binding (unlike replicate/IRSequence, which defers).
        let rec build (idxs: IRIndexType list) : TypedExpr =
            match idxs with
            | [] -> tE
            | outer :: rest ->
                let n = match outer.Extent with IRLit (IRLitInt v) -> int v | _ -> 0
                let thisArrTy = { arr with IndexTypes = idxs }
                let rowExpr = if rest.IsEmpty then tE else build rest
                mkTyped (TExprArrayLit (List.replicate n rowExpr, thisArrTy)) (mkArrayLike thisArrTy)
        Some (build arr.IndexTypes)
    | _ -> None

/// BLOCK LAMBDAS ARE GENERIC ONLY IN THE TYPES THEY ARE USED AT (deep review
/// D5 / N9b, the user's ruling of 2026-11-14; formalism 4.3). A `let f =
/// lambda(..)` bound in a block is ONE monomorphic value, and stays one
/// whenever the block checks that way: the block is checked exactly as written
/// first, so every program that checked before is checked, lowered and emitted
/// as before. Only when that check FAILS is the block checked again with each
/// eligible lambda bound as a nested `function` is (BindConst: its unannotated
/// parameters generic per call, one lifted body per type it is used at, by IR
/// monomorphization) -- so `let idl = lambda(x) -> x; idl(1); idl(2.0)` is
/// two instances, Int32 and Float32, and nothing else. If the second check
/// fails too, the block is checked a third time as written and THAT refusal is
/// reported: a program that was refused keeps its diagnostic, word for word.
///
/// Eligible -- the value restriction, as TypeCheck.generalizeArrowLetLambdas
/// states it for the top level: a plain unannotated `let NAME = lambda(...)`
/// with no `where` clause and no parameter defaults, some parameter
/// unannotated, not self-referencing; NAME is applied at two or more sites in
/// the rest of the block (one site is one type), is never used as a VALUE
/// there (a kernel, an argument, an alias: those need one value) and is never
/// assigned. It stays a lambda (it captures at its creation), so no condition
/// on its captures is needed.
/// Returns the block's statements with the eligible bindings marked, or None.
and private generalizableBlockLambdas (stmts: Stmt list) (finalExpr: Expr option) : (Stmt list * Expr list) option =
    let mapPre = Blade.Unfold.mapExprPre
    let arr = List.toArray stmts
    // Applications of NAME in an expression. By spelling (an inner binding of
    // the same name is counted too): it only decides whether one site could be
    // two types, and an over-count costs nothing but the second check.
    let applications (name: string) (e: Expr) : int =
        let apps = ref 0
        mapPre (fun x ->
            (match x.Kind with
             | ExprKind.ExprApp ({ Kind = ExprKind.ExprVar n }, args) when n = name && not args.IsEmpty ->
                 apps.Value <- apps.Value + 1
             | _ -> ())
            None) e |> ignore
        apps.Value
    // Does NAME occur free other than as the head of an application? SCOPE-
    // AWARE (collectFreeVars): an inner `let NAME = ..` binds its own NAME, so
    // a use or an assignment of that one is not this lambda's. An assignment
    // `NAME = e` of THIS binding is a free non-call occurrence, so it counts
    // as a value use and the lambda is not generalized.
    let usedAsValue (name: string) (e: Expr) =
        let rec dropCalls (x: Expr) : Expr option =
            match x.Kind with
            | ExprKind.ExprApp ({ Kind = ExprKind.ExprVar n }, args)
                    when n = name && not args.IsEmpty
                         && args |> List.forall (fun a -> match a.Kind with ExprKind.ExprWildcard -> false | _ -> true) ->
                Some (mkExpr x.Span (ExprKind.ExprTuple (args |> List.map (mapPre dropCalls))))
            | _ -> None
        Set.contains name (collectFreeVars Set.empty (mapPre dropCalls e))
    let bodies = ResizeArray<Expr>()
    let marked =
        arr |> Array.mapi (fun i raw ->
            match unwrapStmt raw with
            | StmtLet b when b.Mutability = BindLet && b.Type.IsNone ->
                (match b.Pattern.Kind, b.Value.Kind with
                 | PatternKind.PatVar name, ExprKind.ExprLambda (parms, None, body)
                       when not parms.IsEmpty
                            && parms |> List.forall (fun p -> p.Default.IsNone)
                            && parms |> List.exists (fun p -> p.Type.IsNone) ->
                     let rest = mkExpr noSpan (ExprKind.ExprBlock (List.ofArray arr.[i + 1 ..], finalExpr))
                     let selfRef = Set.contains name (collectFreeVars (parms |> List.map (_.Name) |> Set.ofList) body)
                     if applications name rest >= 2 && not selfRef && not (usedAsValue name rest) then
                         bodies.Add body
                         let s = StmtLet { b with Mutability = BindConst }
                         (match raw with
                          | StmtSpanned (_, sp) -> StmtSpanned (s, sp)
                          | _ -> s)
                     else raw
                 | _ -> raw)
            | _ -> raw)
    if bodies.Count = 0 then None else Some (List.ofArray marked, List.ofSeq bodies)

/// ONE DRIVER per nest: the outermost block holding an eligible lambda runs the
/// three checks, and every block nested in it (lexically, or through a lambda
/// or kernel body) follows the driver's current reading instead of retrying on
/// its own -- a failing block costs at most three checks of its nest whatever
/// its depth (each level retrying alone was 3^depth: Codex review, 2026-11-14).
/// The second reading therefore generalizes EVERY eligible lambda of the nest,
/// not only the one whose applications disagreed; that is unobservable but for
/// which lifted bodies are emitted, and only in a program that was refused.
and inferBlock env stmts finalExpr (expectedFinal: IRType option) : TypeResult<TypedExpr> =
    match generalizableBlockLambdas stmts finalExpr with
    | None -> inferBlockCore env stmts finalExpr expectedFinal
    | Some (generalized, _) ->
        let mode = blockLambdaMode ()
        match mode.Value with
        | 1 -> inferBlockCore env stmts finalExpr expectedFinal
        | 2 -> inferBlockCore env generalized finalExpr expectedFinal
        | _ ->
            let under (m: int) (f: unit -> TypeResult<TypedExpr>) =
                mode.Value <- m
                try f () finally mode.Value <- 0
            let nested0 = System.Collections.Generic.HashSet<Expr>(nestedFunctionBodies (), HashIdentity.Reference)
            let rollback = trialRestorePoint env
            match under 1 (fun () -> inferBlockCore env stmts finalExpr expectedFinal) with
            | Ok t -> Ok t
            | Error _ ->
                rollback ()
                let rollback = trialRestorePoint env
                let numbersOnly0 = numbersOnlyInstanceRefusals.Value
                match under 2 (fun () -> inferBlockCore env generalized finalExpr expectedFinal) with
                | Ok t -> Ok t
                | Error e2 when numbersOnlyInstanceRefusals.Value > numbersOnly0
                                 && (match e2 with TypeMismatchErr _ | BuiltinArgErr _ -> true | _ -> false) ->
                    // The generalized reading got past the as-written one's refusal
                    // (a lambda used at two types) and was refused only because an
                    // instance computes on a non-number (R10: `dbl(true)` beside
                    // `dbl(1)`). That is the program's defect: report IT, at the
                    // application, rather than the as-written type mismatch the
                    // generalization exists to look past. No rollback: the module
                    // is refused, and the span cells must keep the application's.
                    Error e2
                | Error _ ->
                    rollback ()
                    // (A body the second check marked but never reached must
                    // not read as a nested function's in the third.)
                    let marks = nestedFunctionBodies ()
                    for b in List.ofSeq marks do
                        if not (nested0.Contains b) then marks.Remove b |> ignore
                    under 1 (fun () -> inferBlockCore env stmts finalExpr expectedFinal)

and inferBlockCore env stmts finalExpr (expectedFinal: IRType option) : TypeResult<TypedExpr> =
    let mutable curEnv = env
    let mutable err : TypeError option = None
    // ResizeArray, not `typedStmts <- typedStmts @ [s]`: the append copied the
    // whole prefix per statement, quadratic in block length. Read back as a
    // list at the (single) exit points, in the same order.
    let typedStmts = ResizeArray<TypedStmt>()

    // A WORK LIST rather than a plain `for`: a windowed `let rec`
    // (recArrayWindowDecision, the PM33/PM34 ring) replaces itself with its
    // ring/snapshot statements AND rewrites the rest of the block -- its later
    // `NAME(p, ...)` reads become snapshot reads -- before any of it is checked.
    let pending = ResizeArray<Stmt>(stmts: Stmt list)
    let mutable finalExpr = finalExpr
    let mutable pos = 0
    while pos < pending.Count do
        let rawStmt = pending.[pos]
        pos <- pos + 1
        if err.IsNone then
            // Unwrap the parser's span annotation and stamp the statement's
            // location for error reporting (see currentStmtSpanStorage).
            let stmt =
                match rawStmt with
                | StmtSpanned (inner, sp) -> setCurrentStmtSpan sp; inner
                | s -> s
            let windowed =
                match stmt with
                | StmtLet b ->
                    (match b.Value.Kind with
                     | ExprKind.ExprRecArray _ ->
                         recArrayWindowDecision curEnv b (pending |> Seq.skip pos |> List.ofSeq) finalExpr
                     | _ -> None)
                | _ -> None
            match windowed with
            | Some (spliced, tail', final') ->
                // The replacement statements carry the binding's own span, so a
                // diagnostic inside them points at the `let rec`.
                let spliced =
                    match rawStmt with
                    | StmtSpanned (_, sp) -> spliced |> List.map (fun s -> StmtSpanned (s, sp))
                    | _ -> spliced
                pending.RemoveRange (pos, pending.Count - pos)
                pending.AddRange spliced
                pending.AddRange tail'
                finalExpr <- final'
            | None ->
            match stmt with
            | StmtSpanned _ ->
                // Unreachable: the parser emits exactly one annotation layer,
                // stripped just above. Loud failure beats a skipped statement.
                failwith "inferBlock: nested StmtSpanned"
            | StmtLet binding ->
                // Shared annotation handler (inferLetBindingValue): without
                // it, block lets would call plain inferExpr and IGNORE the
                // annotation entirely -- `let mut vy: Float<velocity> = 19.62`
                // would bind vy at the bare synthesized type. Routing through
                // the shared handler gives blocks the same bidirectional
                // checking and annotation-as-canonical-type behavior as
                // top-level and expression-form lets.
                // Named-recursive-lambda unification (Stage 3a): a `let const
                // name = lambda(...)` whose body refers to itself is a
                // function. Bind `name` to a fresh id + type var BEFORE
                // inferring the lambda so a self-reference resolves, exactly
                // as checkFunctionDecl binds a function's name before its
                // body (the nested-`function` desugar lands here). Gated on
                // the body actually referencing `name` (free-var scan), so
                // the common NON-recursive case stays on the original path
                // (no id allocation) and its lowering stays byte-identical.
                //
                // A NESTED FUNCTION is generic exactly as a top-level one is:
                // every variable its declaration mints is recorded against its
                // binder (FuncSigVarRange), so the call judgment instantiates
                // the POLYMORPHIC ones per call (calleeQuantifier) and IR
                // monomorphization specializes the lifted callable per use.
                // Without the range, `{ function idl(x: T) -> T = x; idl(2.0) }`
                // bound the declaration's `T` at its first call and left a
                // `T^1` body open (BL6001). Only `function` / `let static`
                // lambdas (BindConst): a plain `let f = lambda` in a block stays
                // monomorphic. (At TOP level, one that applies an unannotated
                // parameter and is only ever applied is checked as a `function`
                // declaration: TypeCheck.generalizeArrowLetLambdas.)
                let nestedFunction =
                    match binding.Mutability, binding.Pattern.Kind, binding.Value.Kind with
                    | BindConst, PatternKind.PatVar _, ExprKind.ExprLambda _ -> true
                    | _ -> false
                (match nestedFunction, binding.Value.Kind with
                 | true, ExprKind.ExprLambda (_, _, lamBody) -> (nestedFunctionBodies ()).Add lamBody |> ignore
                 | _ -> ())
                let sigVarLo = curEnv.Subst.NextId
                let selfInfo =
                    match binding.Mutability, binding.Pattern.Kind, binding.Value.Kind with
                    | BindConst, PatternKind.PatVar n, ExprKind.ExprLambda (lamParms, _, lamBody)
                          when Set.contains n
                                   (collectFreeVars (lamParms |> List.map (_.Name) |> Set.ofList) lamBody) ->
                        let ty = curEnv.Subst.Fresh()
                        let id = curEnv.Builder.FreshId()
                        Some (n, ty, id)
                    | _ -> None
                let inferEnv =
                    match selfInfo with
                    | Some (n, ty, id) -> bindVarSimple n id ty curEnv
                    | None -> curEnv
                // Open-ended while the body is checked, as checkFunctionDecl's
                // (a recursive call is not instantiated against an unfinished
                // signature); closed once the binding id is known, below.
                (match selfInfo with
                 | Some (_, _, id) when nestedFunction ->
                     curEnv.FuncSigVarRange.[id] <- (sigVarLo, System.Int32.MaxValue)
                 | _ -> ())
                // A NESTED FUNCTION's generic obligations (the numbers-only rule's
                // GONumericOperand among them) are ITS OWN, judged at each call of it
                // as a top-level declaration's are (genericObligationClash reads
                // FuncGenericObligations by the binder id). Recorded into the
                // ENCLOSING declaration's list only, they were never judged against
                // the nested function's instances: `let dbl = lambda(x) -> x + x`
                // generalized and called with `true` reached the post-mono IRValidate
                // backstop, with no span (R10 remainder). The enclosing list still
                // receives every entry, as before.
                let nestedObligations = ResizeArray<GenericObligation>()
                let inferEnv =
                    if nestedFunction then { inferEnv with CurrentGenericObligations = Some nestedObligations }
                    else inferEnv
                let valueResult = inferLetBindingValue inferEnv binding
                if nestedFunction then
                    curEnv.CurrentGenericObligations |> Option.iter (fun acc -> acc.AddRange nestedObligations)
                match valueResult with
                | Ok tValue0 ->
                    // Confirm the pre-bound name really surfaced as a self-capture
                    // (it will, given the free-var gate above). Keep the
                    // pre-allocated id so the lifted callable's id matches the
                    // body's self-reference; drop the self-name from captures (it
                    // is the function, not a captured value); tie the recursive-call
                    // constraints to the lambda's own type; and record the
                    // self-binding for Lowering. Non-recursive lets fall through
                    // unchanged with a fresh binding id.
                    let (tValue, varId) =
                        match selfInfo, tValue0.Kind with
                        | Some (n, ty, id), TExprLambda info
                              when info.Captures |> List.exists (fun c -> c.Name = n) ->
                            (match unify curEnv.Subst ty tValue0.Type with
                             | Error e -> err <- Some e
                             | Ok () -> ())
                            let info' =
                                { info with
                                    Captures = info.Captures |> List.filter (fun c -> c.Name <> n)
                                    SelfBinding = Some (n, id) }
                            ({ tValue0 with Kind = TExprLambda info' }, id)
                        // A GENERIC nested function (its type keeps a
                        // polymorphic signature variable open) takes the
                        // top-level road in lowering too: the self-binding
                        // makes its lifted callable a NAMED function whose id
                        // is the binder's, so every call is a direct call of an
                        // HM function and IR monomorphization specializes it
                        // per use, as it does a top-level declaration. Left
                        // as a let of an anonymous lifted lambda, the calls
                        // went through a local alias no specialization was
                        // requested for (BL6001). Non-generic nested functions
                        // keep their original lowering.
                        | _, TExprLambda info
                              when nestedFunction && info.SelfBinding.IsNone
                                   // ITS OWN variables only: one shared with an
                                   // enclosing generic body (a captured `T`) is
                                   // the enclosing declaration's to specialize.
                                   && (freeInferVars curEnv.Subst tValue0.Type
                                       |> Set.exists (fun v -> v >= sigVarLo && curEnv.Subst.IsPolymorphicId v)) ->
                            let id = curEnv.Builder.FreshId()
                            let n = match binding.Pattern.Kind with PatternKind.PatVar n -> n | _ -> ""
                            ({ tValue0 with Kind = TExprLambda { info with SelfBinding = Some (n, id) } }, id)
                        | _ -> (tValue0, curEnv.Builder.FreshId())
                    // #17: a nested `function` (or any let-bound lambda) is a callee a
                    // parallel body can name; record the bindings outside it that it
                    // writes, as checkFunctionDecl does for a top-level declaration.
                    (match tValue.Kind with
                     | TExprLambda info ->
                         (match outerWritesOfBody curEnv curEnv info.Body with
                          | [] -> curEnv.FuncOuterWrites.Remove varId |> ignore
                          | ws -> curEnv.FuncOuterWrites.[varId] <- ws)
                         // N16: and the outside bindings it names, so a function
                         // calling it inherits them (the mut-argument alias check).
                         (match outerReadsOfBody curEnv curEnv info.Body with
                          | [] -> curEnv.FuncOuterReads.Remove varId |> ignore
                          | rs -> curEnv.FuncOuterReads.[varId] <- rs)
                     | _ -> ())
                    (match selfInfo with
                     | Some (_, _, id) when id <> varId -> curEnv.FuncSigVarRange.Remove id |> ignore
                     | _ -> ())
                    if nestedFunction then
                        curEnv.FuncSigVarRange.[varId] <- (sigVarLo, curEnv.Subst.NextId)
                        // Its own obligations, while their variable is still open
                        // (a variable the body settled decided the judgment there).
                        let kept =
                            // The numbers-only rule's kinds only: the other kinds were
                            // never judged at a nested call, and judging them now would
                            // refuse programs that are accepted today (their own item).
                            nestedObligations
                            |> Seq.filter (fun ob ->
                                (match ob with
                                 | GONumericOperand _ | GOBoolOperand _ -> true
                                 | _ -> false)
                                && (match curEnv.Subst.Resolve (IRTInfer (genericObligationVar ob)) |> IR.stripUnits with
                                    | IRTInfer _ -> true
                                    | _ -> false))
                            |> List.ofSeq
                        if not kept.IsEmpty then
                            let prior =
                                match curEnv.FuncGenericObligations.TryGetValue varId with
                                | true, obs -> obs
                                | _ -> []
                            curEnv.FuncGenericObligations.[varId] <- prior @ kept
                    // A destructuring binding's own name is synthesized from its leaves,
                    // `_(a,b)`: the interpreter's session memo is keyed by binding NAME,
                    // and the old placeholder `_` made every top-level destructure in a
                    // session share one key -- the second cell's `let (c, d) = ...`
                    // adopted the FIRST cell's cached tuple (measured: every later
                    // destructure echoed the first one's values). The leaf set is what
                    // identifies the binding to the session (a rebind of the same
                    // leaves supersedes by name exactly as a plain `let` does). `(`
                    // cannot occur in a user name, so `_(` is the recognizer codegen
                    // and the interpreter use to give the parent its `__tup_N` C++ name.
                    let name =
                        match binding.Pattern.Kind with
                        | PatternKind.PatVar n -> n
                        | _ -> "_(" + String.concat "," (patternNames binding.Pattern) + ")"
                    let identity = match binding.Pattern.Kind with PatternKind.PatVar n -> Some (AIDVariable n) | _ -> None
                    let assign = assignOfBindingMut binding.Mutability
                    (blockLocalVids ()).Add varId |> ignore   // a block let (the reduce alias chase, G60)
                    curEnv.BinderValues.[varId] <- tValue   // R10: aliases are followed by binder id
                    curEnv <- bindVarFull name varId tValue.Type identity assign (Some tValue) curEnv
                    // Dist provenance: in-block dist lets (e.g. `let h =
                    // 2.0 * d` on a licensed parameter) derive from the RHS.
                    (match curEnv.Subst.Resolve tValue.Type with
                     | IRTDist _ ->
                         let prov = provenanceOfSurface curEnv binding.Value
                         if not (Set.isEmpty prov) then curEnv.Provenance.[varId] <- prov
                     | _ -> ())
                    // Destructuring in a block: bind the leaves AND record them
                    // as SubBindings so Lowering emits their projection lets
                    // (mirrors checkDecl's DeclLet path) -- without this the
                    // leaf VarIds are never introduced in the IR, a dangling
                    // VarId for any in-body `let (x, y) = p` (or `let Point
                    // { x, y } = p`, bound not at all). All three shapes live
                    // in stmtDestructureBindings, shared with the for-in body
                    // path (inferForIn) since a loop body IS a block scope.
                    let mutable subBindings : (string * IRId * IRType) list = []
                    // Shape tag handed to Lowering (see TypedAst.DestructureShape).
                    let mutable destructure = DSPositional
                    (match stmtDestructureBindings curEnv binding.Pattern tValue.Type with
                     | Error e -> err <- Some e
                     | Ok (leafEnv, leaves, shape) ->
                        curEnv <- leafEnv
                        subBindings <- leaves
                        destructure <- shape)
                    // Mutual-group check-point (block-level twin of the
                    // top-level DeclLet hook).
                    let mutualChecks =
                        match mutualBindingObligation curEnv binding with
                        | Ok None -> []
                        | Ok (Some (group, memberToLeaf)) ->
                            match synthesizeMutualChecks curEnv group memberToLeaf with
                            | Ok checks -> checks
                            | Error e -> err <- Some e; []
                        | Error e -> err <- Some e; []
                    // Constrained-struct binding: check at every assignment.
                    // A bounded-primitive ANNOTATION (section 2.4) guards the same
                    // way, from the surface type -- bounds are erased before
                    // IRType, so tValue.Type cannot carry them.
                    let structChecks =
                        match binding.Pattern.Kind with
                        | PatternKind.PatVar n ->
                            let subject = mkExpr binding.Pattern.Span (ExprVar n)
                            let checksR =
                                synthesizeStructChecks curEnv tValue.Type subject
                                |> Result.bind (fun sc ->
                                    synthesizeBoundChecks curEnv binding.Type n subject
                                    |> Result.map (fun bc -> sc @ bc))
                            match checksR with
                            | Ok cs -> cs |> List.map (fun c -> (curEnv.Builder.FreshId(), c))
                            | Error e -> err <- Some e; []
                        | _ -> []
                    let postChecks = mutualChecks @ structChecks
                    // Deferred former (Arity = None): emit inert; every <@> use
                    // rebuilds it inline. Env keeps the real former for <@>.
                    let isDeferredFormer =
                        match curEnv.Subst.Resolve tValue.Type with
                        | IRTLoop { Kind = LKObject; Arity = None } -> true
                        | _ -> false
                    let tb : TypedBinding = {
                        Name = name; VarId = varId
                        Type = (if isDeferredFormer then IRTUnit else tValue.Type)
                        Identity = identity; IsMutable = (assign <> ReadOnly); IsLetMut = (assign = MutPassable)
                        Value = (if isDeferredFormer then mkTyped (TExprLit LitUnit) IRTUnit else tValue)
                        SubBindings = subBindings |> List.map (fun (n, id, ty) -> (n, id, curEnv.Subst.Resolve ty))
                        Destructure = destructure
                        PostChecks = postChecks
                        ErasedType = (if isDeferredFormer then Some (curEnv.Subst.Resolve tValue.Type) else None)
                    }
                    typedStmts.Add (TStmtLet tb)
                | Error e -> err <- Some e
            | StmtAssign (lhs, _, rhs) ->
                match inferExpr curEnv lhs, inferExpr curEnv rhs with
                | Ok tL, Ok tR ->
                    // Constrained-struct target: guard after the store.
                    let assignChecks () =
                        match structChecksForAssign curEnv lhs tL with
                        | Ok cs -> cs |> List.map TStmtExpr
                        | Error e -> err <- Some e; []
                    // Check assignability of LHS (shared with the expression
                    // -position site so the two cannot drift).
                    match assignTargetError curEnv tL with
                    | Some e ->
                        // (Located at the target, as in expression position.)
                        if tL.Span.StartLine > 0 then setCurrentExprSpan tL.Span
                        err <- Some e
                    | None ->
                        let _ = unify curEnv.Subst tL.Type tR.Type
                        typedStmts.Add (TStmtAssign (tL, tR))
                        typedStmts.AddRange (assignChecks ())
                | Error e, _ | _, Error e -> err <- Some e
            | StmtExpr e ->
                match inferExpr curEnv e with
                | Ok tE -> typedStmts.Add (TStmtExpr tE)
                | Error e -> err <- Some e
            | StmtForIn (varName, rangeExpr, bodyStmts) ->
                match inferForIn curEnv varName rangeExpr bodyStmts with
                | Ok tStmt -> typedStmts.Add tStmt
                | Error e -> err <- Some e

    match err with
    | Some e -> Error e
    | None ->
        match finalExpr with
        | Some e ->
            // Push a checking-position expected type into the final expression
            // so a block that ends in a literal/match arm flexes (bidirectional).
            let tFR =
                match expectedFinal with
                | Some ty -> checkExpr curEnv ty e
                | None -> inferExpr curEnv e
            tFR |> Result.map (fun tF ->
                mkTyped (TExprBlock (List.ofSeq typedStmts, Some tF)) tF.Type)
        | None -> Ok (mkTyped (TExprBlock (List.ofSeq typedStmts, None)) IRTUnit)

/// Leaf bindings for a destructuring `let` in STATEMENT position. Returns the
/// environment extended with every leaf, the (name, id, type) sub-binding
/// list Lowering projects from, and the shape tag (TypedAst.DestructureShape).
///
/// Shared because a for-in body is a block scope, so inferBlock's TStmtLet
/// and inferForIn's StmtLet must answer identically about what a pattern
/// binds and at what types -- diverging (e.g. inferForIn hardcoding
/// `SubBindings = []`) fails silently: the primary binding takes the
/// synthetic "_", and each leaf name resurfaces as UnboundVariable. Leaf
/// TYPES come from the same helpers checkDecl/DeclLet reads
/// (consDestructureLeaves, structFieldTypesOf), so the two positions cannot
/// drift on typing rules. A non-destructuring pattern records nothing.
and stmtDestructureBindings (env: TypeEnv) (pat: Pattern) (valueTy: IRType)
                            : TypeResult<TypeEnv * (string * IRId * IRType) list * DestructureShape> =
    let mutable e = env
    let mutable subs : (string * IRId * IRType) list = []
    let bindLeaf (n: string) (ty: IRType) =
        let subId = e.Builder.FreshId()
        subs <- subs @ [(n, subId, ty)]
        e <- bindVarSimple n subId ty e
    // A compound leaf (nested tuple/struct pattern) is not recursively
    // destructured here; its names bind at fresh type vars so a later reference
    // resolves to *something* instead of cascading UnboundVariable errors.
    // Same conservative rule as checkDecl/DeclLet.
    let bindCompound (p: Pattern) =
        for n in patternNames p do bindLeaf n (e.Subst.Fresh())
    match pat.Kind with
    | PatternKind.PatTuple pats ->
        match tupleDestructureArityError env pats valueTy with
        | Some err -> Error err
        | None ->
        let resolvedTy = e.Subst.Resolve valueTy
        let typeList =
            match resolvedTy with
            | IRTTuple ts ->
                if pats.Length = ts.Length then ts
                else
                    // Flat match: (x, y, z) against ((alpha,beta), gamma)
                    let flat = IR.flattenTupleLeaves resolvedTy
                    if pats.Length = flat.Length then flat else ts
            | _ -> []
        let (entries, slots) = tuplePatternSlots pats
        for (n, posOpt, _) in entries do
            let eTy =
                match posOpt with
                | Some i when i < typeList.Length -> e.Subst.Resolve(typeList.[i])
                | _ -> e.Subst.Fresh()
            bindLeaf n eTy
        Ok (e, subs, DSTupleAt (entries |> List.map (fun (_, _, s) -> s), slots))
    | PatternKind.PatCons (h, t) ->
        // `let head :: tail = tup`. Flatten/typing/reject rules live in
        // consDestructureLeaves, shared with the top-level, `let static` and
        // expression-position forms. A scrutinee that cannot be split is a
        // hard error, never fresh type vars (which would zonk to Float64
        // while Lowering projects positionally -- a silent miscompile).
        consDestructureLeaves env valueTy h t
        |> Result.map (fun leaves ->
            for (n, ty) in leaves do bindLeaf n ty
            (e, subs, DSConsRest))
    | PatternKind.PatStruct (_, fieldPats) ->
        // `let Point { x, y } = p`. Leaves matched BY NAME (Lowering projects
        // via IRFieldAccess on the leaf's own name), so a missing/extra field
        // can't shift another leaf's field. structFieldTypesOf returns []
        // for an unregistered struct, so a leaf falls back to a fresh var
        // rather than silently taking another field's type.
        let fieldTypeMap = Map.ofList (structFieldTypesOf env valueTy)
        for (fieldName, p) in fieldPats do
            (match p.Kind with
             | PatternKind.PatVar n ->
                let eTy =
                    match Map.tryFind fieldName fieldTypeMap with
                    | Some ty -> e.Subst.Resolve ty
                    | None -> e.Subst.Fresh()
                bindLeaf n eTy
             | _ -> bindCompound p)
        Ok (e, subs, DSPositional)
    | _ -> Ok (env, [], DSPositional)

/// Infer one for-in loop statement. Recursive so loops nest to any depth
/// (required by the ML-module layers and grad-generated adjoint loops).
/// The loop variable binds as Int64 in the body scope; body lets stay local
/// to the loop (they do NOT leak past it), matching block-scope rules.
and inferForIn (env: TypeEnv) (varName: string) (rangeExpr: Expr) (bodyStmts: Stmt list) : TypeResult<TypedStmt> =
    match rangeExpr.Kind with
    | ExprKind.ExprDotDot (lo, hi) ->
        match inferExpr env lo, inferExpr env hi with
        | Error e, _ | _, Error e -> Error e
        | Ok tLo, Ok tHi ->
            // Desugared loops bypass ExprDotDot inference, but their bounds
            // have the same Int64 index context as an ordinary range.
            let indexBound t =
                match literalOnlyElem env.Subst t with
                | Some le when litClassOf le = 0 ->
                    retypeLiteralOnly env.Subst ETInt64 t |> Option.defaultValue t
                | _ -> t
            let tLo, tHi = indexBound tLo, indexBound tHi
            let varId = env.Builder.FreshId()
            // A `let rec` step ordinal over a named axis: its lower bound is
            // ascribed with the axis (recStepVars).
            let varTy =
                match lo.Kind, IR.stripUnits (env.Subst.Resolve tLo.Type) with
                | ExprKind.ExprTyped _, IRTIdxTagged (_, IRefNamed tag) when not (tag.StartsWith "__") ->
                    (recStepVars ()).[varId] <- tag
                    IRTIdxTagged (IRTScalar ETInt64, IRefNamed tag)
                | _ -> IRTScalar ETInt64
            let loopEnv = bindVarSimple varName varId varTy env
            let mutable bodyEnv = loopEnv
            let mutable bodyErr = None
            // ResizeArray for the same reason as inferBlock's typedStmts (these
            // two statement walkers are deliberate twins).
            let typedBodyStmts = ResizeArray<TypedStmt>()
            for bodyStmt in bodyStmts do
                if bodyErr.IsNone then
                    let bodyStmt =
                        match bodyStmt with
                        | StmtSpanned (inner, sp) -> setCurrentStmtSpan sp; inner
                        | s -> s
                    match bodyStmt with
                    | StmtSpanned _ ->
                        failwith "inferForIn: nested StmtSpanned"
                    | StmtLet binding ->
                        // A synthesized loop body is still a binding scope:
                        // honor annotations and internal literal flexibility
                        // exactly as inferBlock does.
                        match inferLetBindingValue bodyEnv binding with
                        | Ok tValue ->
                            let bName = match binding.Pattern.Kind with PatternKind.PatVar n -> n | _ -> "_"
                            let bId = bodyEnv.Builder.FreshId()
                            let assign = assignOfBindingMut binding.Mutability
                            (blockLocalVids ()).Add bId |> ignore
                            bodyEnv <- bindVarFull bName bId tValue.Type None assign (Some tValue) bodyEnv
                            // Destructuring inside a loop body: routed through
                            // the same stmtDestructureBindings inferBlock uses
                            // (a loop body IS a block scope), so every shape
                            // -- tuple, cons, struct -- binds its leaves
                            // instead of leaving them to resurface later as
                            // UnboundVariable.
                            let mutable subBindings : (string * IRId * IRType) list = []
                            let mutable destructure = DSPositional
                            (match stmtDestructureBindings bodyEnv binding.Pattern tValue.Type with
                             | Error e -> bodyErr <- Some e
                             | Ok (leafEnv, leaves, shape) ->
                                bodyEnv <- leafEnv
                                subBindings <- leaves
                                destructure <- shape)
                            let tb : TypedBinding = {
                                Name = bName; VarId = bId; Type = tValue.Type
                                Identity = None; IsMutable = (assign <> ReadOnly); IsLetMut = (assign = MutPassable); Value = tValue
                                SubBindings = subBindings |> List.map (fun (n, id, ty) -> (n, id, bodyEnv.Subst.Resolve ty))
                                Destructure = destructure; PostChecks = []; ErasedType = None
                            }
                            typedBodyStmts.Add (TStmtLet tb)
                        | Error e -> bodyErr <- Some e
                    | StmtAssign (lhs, _, rhs) ->
                        match inferExpr bodyEnv lhs, inferExpr bodyEnv rhs with
                        | Ok tL, Ok tR ->
                            let _ = unify bodyEnv.Subst tL.Type tR.Type
                            // Constrained-struct target: guard after the store.
                            let checks =
                                match structChecksForAssign bodyEnv lhs tL with
                                | Ok cs -> cs |> List.map TStmtExpr
                                | Error e -> bodyErr <- Some e; []
                            typedBodyStmts.Add (TStmtAssign (tL, tR))
                            typedBodyStmts.AddRange checks
                        | Error e, _ | _, Error e -> bodyErr <- Some e
                    | StmtExpr e ->
                        match inferExpr bodyEnv e with
                        | Ok tE -> typedBodyStmts.Add (TStmtExpr tE)
                        | Error e -> bodyErr <- Some e
                    | StmtForIn (v2, range2, body2) ->
                        match inferForIn bodyEnv v2 range2 body2 with
                        | Ok tStmt -> typedBodyStmts.Add tStmt
                        | Error e -> bodyErr <- Some e
            match bodyErr with
            | Some e -> Error e
            | None -> Ok (TStmtForIn (varName, varId, tLo, tHi, List.ofSeq typedBodyStmts))
    | _ -> Error (BuiltinArgErr "for-in range must use a..b syntax")

/// A TREE-slotted operand in a loop former. A tree array's cells are its leaves
/// and the flat pool IS a rank-1 dense array, so iterating it is meaningful --
/// but `method_for` PRODUCES an array that inherits this operand's index record,
/// and an inherited tree slot reaches output-storage classification, the
/// identity/grouping machinery, fusion, and `exprTypeIfKnown`'s HM-argument
/// whitelist, none of which has a tree reading yet (and whose failure mode there
/// is a BL6001 spray, not a refusal). `reduce` is unaffected: it CONSUMES the
/// pool and produces a scalar, which is why it works today.
///
/// The clean spelling arrives with the derived dense leaf axis, which is a plain
/// `Idx<card>` -- every existing optimization applies to it unchanged and no
/// tree slot ever enters a loop former. Deferred there deliberately.
///
/// Applied to the RESULT rather than at an operand scan because inferMethodFor
/// has four exits that each build their own `arrayTypes`; the loop object's own
/// `TypedMethodForInfo` is the one place all four agree.
and treeLoopOperandGuard (tv: TypedExpr) : TypeResult<TypedExpr> =
    match tv.Kind with
    | TExprMethodFor info ->
        (match info.ArrayTypes
               |> List.tryPick (fun at ->
                      at.IndexTypes |> List.tryPick (fun ix -> match ix with TreeIdxLike r -> Some r | _ -> None)) with
         | Some rendered -> Error (TreeIdxUnsupported (rendered, "a method_for / object_for operand"))
         | None -> Ok tv)
    | _ -> Ok tv

and inferMethodFor env arrays : TypeResult<TypedExpr> =
    inferMethodForOperands env arrays |> Result.bind treeLoopOperandGuard

and inferMethodForOperands env arrays : TypeResult<TypedExpr> =
    // GROUPED operands are the one non-dense co-iteration
    // (zipSharedRecords' isGroupedRaggedShape arm): the rows line up
    // one-to-one and the kernel takes one row per operand. That is only
    // meaningful when every operand was grouped by the SAME keys -- one
    // offsets table drives every row -- and the types cannot say so, since
    // two independent `group_keys` calls produce structurally identical
    // records. Discharge it on the EXPRESSIONS: chase each operand to its
    // `group_by(vals, gk)` and require the `gk` operands to resolve (through
    // any number of alias hops) to the same binding.
    //
    // Shared by BOTH zip spellings: `method_for(zip(ga, gb))` and the
    // let-bound `let Z = zip(ga, gb); method_for(Z)`. The second used to skip
    // it, so two independent groupings typechecked and died in codegen's
    // BL7004 gate instead of here.
    let groupedZipSameKeys (tOps: TypedExpr list) (arrayTypes: IRArrayType list) : TypeResult<unit> =
        let groupKeysOperandOf (ta: TypedExpr) : TypedExpr option =
            match (resolveTypedExprDeep env ta).Kind with
            | TExprGroupBy (_, gkExpr) -> Some (resolveTypedExprDeep env gkExpr)
            | _ -> None
        let allGrouped = arrayTypes |> List.forall (fun at -> isGroupedRaggedShape at.IndexTypes)
        let sameGroupKeysBinding () =
            match tOps |> List.map groupKeysOperandOf with
            | (Some g0) :: rest when rest |> List.forall Option.isSome ->
                let nameOf (g: TypedExpr) =
                    match g.Kind with TExprVar (n, _, _) -> Some n | _ -> None
                rest |> List.map Option.get |> List.forall (fun g ->
                    System.Object.ReferenceEquals(g, g0)
                    || (match nameOf g, nameOf g0 with
                        | Some a, Some b -> a = b
                        | _ -> false))
            | _ -> false
        if allGrouped && arrayTypes.Length > 1 && not (sameGroupKeysBinding ()) then
            Error (IndexSpaceErr "co-iterating grouped arrays requires every operand to be grouped by the SAME group_keys binding (one offsets table drives the shared row walk). Bind the keys once (`let gk = group_keys(...)`) and pass that same `gk` to each group_by; grouping each operand with its own group_keys call gives two independent partitions with no row correspondence.")
        else Ok ()
    // Detect method_for(zip(A, B, ...)) -- expand zip into co-iteration
    match arrays with
    | [{ Kind = ExprKind.ExprZip zipExprs }] ->
        zipExprs |> List.map (inferExpr env) |> sequenceResults |> Result.bind (fun tZipArrays ->
            let identities = List.map2 (fun (arr: Expr) (ta: TypedExpr) ->
                match arr.Kind, ta.Kind with
                | ExprKind.ExprVar name, TExprVar (tn, vid, _) when tn = name -> loopOperandIdentity env name vid
                | ExprKind.ExprVar name, _ -> AIDVariable name
                | _ -> AIDLiteral (env.Builder.FreshId())) zipExprs tZipArrays
            // Resolved shape, defaulted element -- see loopOperandArrayType. This
            // is the arm a compound zip operand reaches (`(A - m) * (B - m)`).
            tZipArrays |> List.iter (fun ta -> materializeGenericLoopOperand env ta "method_for")
            tZipArrays |> List.map (fun ta -> demandNamedLoopOperand env ta "method_for") |> sequenceResults
            |> Result.bind (fun _ ->
            let arrayTypes = tZipArrays |> List.mapi (fun i ta ->
                loopOperandArrayType env (fun () -> getArrayType env zipExprs.[i]) ta.Type)
            // Shared iteration records. Single-record operands (dense rank-1 or
            // packed symmetric) use the first-record rule unchecked.
            // MULTI-record operands (dense rank >= 2) co-iterate the FULL product
            // of records -- all operands must agree structurally and every record
            // must be plain dense. buildApplyInfo trims the co-iterated prefix
            // by the kernel's slice rank, so row-mode kernels (loops/085) keep
            // receiving their inner-record slice. Grouped operands: see
            // groupedZipSameKeys above.
            match groupedZipSameKeys tZipArrays arrayTypes with
            | Error e -> Error e
            | Ok () ->
            match zipSharedRecords arrayTypes with
            | Error e -> Error e
            | Ok sharedRecords ->
            match compoundMaskProvenanceClash env arrayTypes with
            | Some e -> Error e
            | None ->
            // Real per-array S-dim counts: buildApplyInfo's IRTInfer fallback
            // computes the kernel slice rank as (records - sDims); a scalar
            // kernel over rank-2 operands must see kR = 0 (full product), which
            // a flat per-array 1 would mis-trim to row mode.
            let sDimsPerArray = computeSDimsPerArray arrayTypes
            let totalSDims = List.sum sDimsPerArray

            let info : TypedMethodForInfo = {
                Arrays = tZipArrays; Identities = identities; ArrayTypes = arrayTypes
                SDimsPerArray = sDimsPerArray; TotalSDims = totalSDims
                SharedIndexTypes = sharedRecords
            }
            let loopTy = IRTLoop {
                Kind = LKMethod; Arity = Some zipExprs.Length
                ArrayTypes = arrayTypes |> List.map mkArrayLike; KernelType = None
            }
            Ok (mkTyped (TExprMethodFor info) loopTy)))
    | _ ->
    arrays |> List.map (inferExpr env) |> sequenceResults |> Result.bind (fun tArrays ->
        // Also detect method_for(Z) where Z was bound to a zip.
        //
        // NOT deepened to `resolveTypedExprDeep` alongside the tuple leaf
        // expansion below, deliberately. `let Z = zip(A,B); let W = Z;
        // method_for(W)` is broken today at depth 2, but it is ALSO broken at
        // depth 1 (`method_for(Z)`: the auto-printer calls `.extents` on a
        // zip binding) -- so deepening the hop here does not fix a program, it
        // only swaps one g++ failure for a different one. Zip aliasing is its
        // own defect and wants its own change; R1's depth-invariance is
        // implemented for TUPLE operands, which is what the pack rule is about.
        match tArrays with
        | [single] when (resolveTypedExpr env single).Kind.IsTExprZip ->
            let resolved = resolveTypedExpr env single
            match resolved.Kind with
            | TExprZip zipExprs ->
                let identities = zipExprs |> List.map (fun te ->
                    match te.Kind with
                    | TExprVar (name, vid, _) -> loopOperandIdentity env name vid
                    | _ -> AIDLiteral (env.Builder.FreshId()))
                match zipExprs |> List.map (fun te -> demandNamedLoopOperand env te "method_for") |> sequenceResults with
                | Error e -> Error e
                | Ok _ ->
                // Same stale-IRTInfer hazard as the zip arm above.
                let arrayTypes = zipExprs |> List.map (fun te ->
                    loopOperandArrayType env
                        (fun () -> { ElemType = IRTScalar ETFloat64; IndexTypes = []; IsVirtual = false; Identity = None })
                        te.Type)
                match groupedZipSameKeys zipExprs arrayTypes with
                | Error e -> Error e
                | Ok () ->
                match zipSharedRecords arrayTypes with
                | Error e -> Error e
                | Ok sharedRecords ->
                match compoundMaskProvenanceClash env arrayTypes with
                | Some e -> Error e
                | None ->
                let sDimsPerArray = computeSDimsPerArray arrayTypes
                let totalSDims = List.sum sDimsPerArray
                let info : TypedMethodForInfo = {
                    Arrays = zipExprs; Identities = identities; ArrayTypes = arrayTypes
                    SDimsPerArray = sDimsPerArray; TotalSDims = totalSDims
                    SharedIndexTypes = sharedRecords
                }
                let loopTy = IRTLoop {
                    Kind = LKMethod; Arity = Some zipExprs.Length
                    ArrayTypes = arrayTypes |> List.map mkArrayLike; KernelType = None
                }
                Ok (mkTyped (TExprMethodFor info) loopTy)
            | _ -> failwith "unreachable"
        // GUARD (the method_for twin of the object_for pack guard): a zip
        // BESIDE other operands. The two single-operand arms above are the
        // supported co-iteration form; here the zip would become one ordinary
        // pack slot whose array never materializes, and codegen emits an
        // undeclared `arr<i>`. Reject with the same message both orientations
        // share. `for (A, zip(B, C))` desugars through here too.
        | _ when arrays.Length > 1
                 && (tArrays |> List.exists (fun ta -> (resolveTypedExpr env ta).Kind.IsTExprZip)) ->
            Error (NotYetSupportedErr zipInMultiArrayPackMsg)
        | _ ->
        // ---- SPINE EXPANSION (docs/plan-tuples-vs-arg-packs.md 6c) ----
        // The loop-former side of the ONE pack site: rule 3's one-level splice,
        // so `method_for(A, B)`, `method_for((A, B))`, `let P = (A,B);
        // method_for(P)` and `let Q = P; method_for(Q)` are one program. This
        // also covers the IMPLICIT former on the left of `<@>` -- `P <@> k`
        // reaches here as `inferMethodFor env [P]` (inferBinOp's OpApply
        // normalization), which is why M4 does not need its own site.
        //
        // ONE level only. `method_for((A, (B, C)))` splices to the two nodes
        // `A` and `(B, C)`; the second stays a tuple and is matched against the
        // kernel's schema in buildApplyInfo (which is where the kernel is
        // known -- the former is built before it). Under 6b this deep-flattened
        // to three operands, silently equating it with `method_for(A, B, C)`.
        //
        // Taken only when the splice actually changes the operand list, so the
        // overwhelmingly common no-tuple case keeps the original code path
        // (identities and the stale-IRTInfer `getArrayType` fallback both read
        // the SURFACE expr by index, and that indexing is only valid when the
        // two lists are still aligned).
        let leafArrays = packSpine env tArrays
        if leafArrays.Length <> tArrays.Length then
            // Same zip guard as above, re-asked over the SPINE: a tuple can
            // now carry a zip into a multi-operand pack.
            if leafArrays.Length > 1
               && (leafArrays |> List.exists (fun ta -> (resolveTypedExprDeep env ta).Kind.IsTExprZip)) then
                Error (NotYetSupportedErr zipInMultiArrayPackMsg)
            else
            let identities = leafArrays |> List.map (fun ta ->
                match ta.Kind with
                | TExprVar (name, vid, _) -> loopOperandIdentity env name vid
                | _ -> AIDLiteral (env.Builder.FreshId()))
            // No surface expr to fall back on (a node may have come out of a
            // tuple VALUE), so the defaulted-shape thunk stands in -- the same
            // one the let-bound-zip arm above uses for exactly this reason.
            leafArrays |> List.iter (fun ta -> materializeGenericLoopOperand env ta "method_for")
            match leafArrays |> List.map (fun ta -> demandNamedLoopOperand env ta "method_for") |> sequenceResults with
            | Error e -> Error e
            | Ok _ ->
            let arrayTypes = leafArrays |> List.map (fun ta ->
                loopOperandArrayType env
                    (fun () -> { ElemType = IRTScalar ETFloat64; IndexTypes = []; IsVirtual = false; Identity = None })
                    ta.Type)
            let sDimsPerArray = computeSDimsPerArray arrayTypes
            let totalSDims = List.sum sDimsPerArray
            let info : TypedMethodForInfo = {
                Arrays = leafArrays; Identities = identities; ArrayTypes = arrayTypes
                SDimsPerArray = sDimsPerArray; TotalSDims = totalSDims
                SharedIndexTypes = []
            }
            let loopTy = IRTLoop {
                Kind = LKMethod; Arity = Some leafArrays.Length
                ArrayTypes = arrayTypes |> List.map mkArrayLike; KernelType = None
            }
            Ok (mkTyped (TExprMethodFor info) loopTy)
        else
        let identities = List.map2 (fun (arr: Expr) (ta: TypedExpr) ->
            match arr.Kind, ta.Kind with
            | ExprKind.ExprVar name, TExprVar (tn, vid, _) when tn = name -> loopOperandIdentity env name vid
            | ExprKind.ExprVar name, _ -> AIDVariable name
            | _ -> AIDLiteral (env.Builder.FreshId())) arrays tArrays
        // S1 SEAM 3, method_for orientation (see the object_for site): a
        // caret-shorthand `T^k` operand is an arity-constrained var, so
        // loopOperandArrayType takes its `fallback ()` branch and the loop
        // iterates a shape nobody declared. Supply the forced shape first.
        tArrays |> List.iter (fun ta -> materializeArityVar env ta "method_for")
        // ...and a generic declaration's UNANNOTATED parameter (no caret to
        // read): see materializeGenericLoopOperand.
        tArrays |> List.iter (fun ta -> materializeGenericLoopOperand env ta "method_for")
        // ...and a named operand the fallback would read wrongly (G125): see
        // demandNamedLoopOperand.
        match tArrays |> List.map (fun ta -> demandNamedLoopOperand env ta "method_for") |> sequenceResults with
        | Error e -> Error e
        | Ok _ ->
        // Same stale-IRTInfer hazard as the zip arms above.
        let arrayTypes = tArrays |> List.mapi (fun i ta ->
            loopOperandArrayType env (fun () -> getArrayType env arrays.[i]) ta.Type)
        let sDimsPerArray = computeSDimsPerArray arrayTypes
        let totalSDims = List.sum sDimsPerArray

        let info : TypedMethodForInfo = {
            Arrays = tArrays; Identities = identities; ArrayTypes = arrayTypes
            SDimsPerArray = sDimsPerArray; TotalSDims = totalSDims
            SharedIndexTypes = []
        }
        let loopTy = IRTLoop {
            Kind = LKMethod; Arity = Some arrays.Length
            ArrayTypes = arrayTypes |> List.map mkArrayLike; KernelType = None
        }
        Ok (mkTyped (TExprMethodFor info) loopTy))

and inferObjectFor env kernel : TypeResult<TypedExpr> =
    inferObjectForCore env kernel None

/// `inferObjectFor` with an optional override that types the kernel (G114:
/// a lambda kernel hinted by the operands of its `<@>` site).
and inferObjectForCore env kernel (typeKernel: (unit -> TypeResult<TypedExpr>) option) : TypeResult<TypedExpr> =
    // A bare named-function reference used as an object_for kernel is
    // eta-expanded to lambda(__k..) -> f(__k..), so `object_for(lkm) <@> ...`
    // works symmetrically with `method_for(...) <@> lkm`.
    let kernelResult =
        match typeKernel with
        | Some f -> f ()
        | None ->
        match etaExpandFunctionKernel env kernel with
        | Some r -> r
        | None ->
            // G276 (2): an object former's lambda is a kernel VALUE (a stored
            // former, a compose stage -- a fused pipeline's kernel included):
            // its open parameters are decided where it is later applied, as a
            // let-bound lambda value's are (TypeEnv.LetLambdaParamVars).
            (match kernel.Kind with
             | ExprKind.ExprLambda _ -> env.PendingLetLambda.Value <- true
             | _ -> ())
            let r = inferExpr env kernel
            env.PendingLetLambda.Value <- false
            r
    kernelResult |> Result.bind (fun tKernel ->
        // A DEFERRED former: a bare named reference to an arity-polymorphic
        // function. etaExpandFunctionKernel refused to eta-expand it (its Poly
        // pack param makes the arity unknown), so it is still a TExprVar. Mark
        // the loop arity-polymorphic (Arity = None) and eta-expand at the <@>
        // site, where the argument tuple reveals the pack width.
        let isDeferredPoly =
            match tKernel.Kind, env.Subst.Resolve tKernel.Type with
            | TExprVar _, FuncElem (paramTys, _) ->
                paramTys |> List.exists (fun t -> (env.Subst.Resolve t).IsIRTPoly)
            | _ -> false
        let (commGroups, inputRanks, outputRank) =
            match tKernel.Kind with
            | TExprLambda info ->
                let iRanks = info.Params |> List.map (fun p ->
                    match p.Type with ArrayElem arr -> arr.IndexTypes.Length | _ -> 0)
                (info.CommGroups, iRanks, 0)
            | _ -> ([], [], 0)
        let info : TypedObjectForInfo = {
            Kernel = tKernel; CommGroups = commGroups
            InputRanks = inputRanks; OutputRank = outputRank
        }
        let loopTy = IRTLoop {
            Kind = LKObject
            Arity = if isDeferredPoly then None else Some inputRanks.Length
            ArrayTypes = []; KernelType = Some tKernel.Type
        }
        Ok (mkTyped (TExprObjectFor info) loopTy))

and inferStructConstruction env srcName fields (spread: Expr option) : TypeResult<TypedExpr> =
    // The struct's IDENTITY (D11: an imported `P` is `A.P`); the scope key is
    // only how this module spells it.
    let name =
        match lookupTypeDef srcName env with
        | Some (TDIStruct (ident, _, _, _)) -> ident
        | _ -> srcName
    if (lookupTypeDef srcName env).IsNone && env.AmbiguousTypes.ContainsKey srcName then
        Error (AmbiguousImport ("type", srcName, env.AmbiguousTypes.[srcName]))
    else
    match lookupTypeDef name env with
    | Some (TDIStruct (_, _, declFields, _)) ->
        let declNames = declFields |> List.map fst
        // Functional update: `S { f = v, ..base }` desugars the MISSING
        // fields to `base.f` reads and falls into the ordinary construction
        // path (dup/unknown/missing checks, per-field bidirectional check,
        // decl-order emission, assignment-site guards -- all unchanged).
        let desugared =
            match spread with
            | None -> Ok fields
            | Some baseExpr ->
                // Base restriction: a variable / field path / typed wrap --
                // pure, so re-evaluating it once per copied field is safe and
                // no temp binding (with its IRId-ordering interactions) is
                // needed. Anything else: bind it with let first.
                let rec pureBase (e: Expr) =
                    match e.Kind with
                    | ExprKind.ExprVar _ -> true
                    | ExprKind.ExprField (o, _) -> pureBase o
                    | ExprKind.ExprTyped (i, _) -> pureBase i
                    | _ -> false
                if not (pureBase baseExpr) then Error (StructSpreadBase name)
                else
                    inferExpr env baseExpr |> Result.bind (fun tBase ->
                        let resolved =
                            match env.Subst.Resolve tBase.Type with
                            | IRTNamed n ->
                                // Chase one transparent-alias level so a base
                                // typed by an alias of this struct passes.
                                (match lookupTypeDef n env with
                                 | Some (TDIAlias (IRTNamed t)) -> IRTNamed t
                                 | _ -> IRTNamed n)
                            | other -> other
                        let acceptBase () =
                            let providedNames = fields |> List.map fst
                            let missing = declNames |> List.filter (fun f -> not (List.contains f providedNames))
                            if missing.IsEmpty then Error (StructSpreadRedundant name)
                            else
                                Ok (fields @ (missing |> List.map (fun f ->
                                    (f, inheritSpan baseExpr (ExprField (baseExpr, f))))))
                        match resolved with
                        | IRTNamed n when n = name -> acceptBase ()
                        | IRTInfer _ ->
                            // Unresolved base (e.g. a kernel param whose type
                            // unifies with the iterated array's elem type only
                            // later, in buildApplyInfo): the spread base of an
                            // `S { .. }` construction must BE an S, so bind
                            // the variable now and let the constraint flow
                            // back to the param.
                            (match unify env.Subst resolved (IRTNamed name) with
                             | Ok () -> acceptBase ()
                             | Error _ -> Error (StructSpreadNotStruct (name, ppIRType resolved)))
                        | other -> Error (StructSpreadNotStruct (name, ppIRType other)))
        desugared |> Result.bind (fun fields ->
        let providedNames = fields |> List.map fst
        let duplicate =
            providedNames |> List.countBy id
            |> List.tryPick (fun (n, count) -> if count > 1 then Some n else None)
        let unknown = providedNames |> List.tryFind (fun n -> not (List.contains n declNames))
        let missing = declNames |> List.tryFind (fun n -> not (List.contains n providedNames))
        match duplicate, unknown, missing with
        | Some d, _, _ -> Error (StructFieldDuplicate (name, d))
        | _, Some u, _ -> Error (StructNoField (name, u))
        | _, _, Some m -> Error (StructMissingField (name, m))
        | None, None, None ->
            fields |> List.map (fun (fname, fexpr) ->
                // Bidirectional: check the field expr against the declared
                // type so literals adapt (Int64 literal into an Int32 field)
                // exactly as they do in every other checked position.
                let eTy = declFields |> List.find (fun (n, _) -> n = fname) |> snd
                match checkExpr env eTy fexpr with
                | Ok tFE -> Ok (fname, tFE)
                | Error (TypeMismatch (exp, act)) ->
                    Error (StructFieldType (name, fname, ppIRType exp, ppIRType act))
                | Error e -> Error e)
            |> sequenceResults |> Result.map (fun tFields ->
                // Emit fields in DECLARATION order: C++ designated
                // initializers require it, and evaluation order becomes
                // deterministic regardless of the literal's field order.
                let ordered = declNames |> List.map (fun n -> tFields |> List.find (fun (fn, _) -> fn = n))
                mkTyped (TExprStruct (name, ordered)) (IRTNamed name)))
    | Some (TDIAlias (IRTNamed target)) when target <> name ->
        // Transparent alias naming a struct: construct through it.
        inferStructConstruction env target fields spread
    | _ ->
        Error (UnknownStructType name)

// ---- Mutual-group binding-site machinery -----------------------------------
// The joint check attaches exactly where a group's type-tuple is INTRODUCED:
// a function's declared `(P1, P2)` return (checked at the return site) or an
// annotated joint let (checked after the destructure). Detection runs on the
// SURFACE TypeExpr -- members are transparent aliases, erased by lowerTypeExpr.

/// Deep-collect mutual-group member names appearing anywhere in a type
/// annotation (one entry per occurrence).
and mutualMemberNamesIn (env: TypeEnv) (t: TypeExpr) : string list =
    let rec walk t =
        match t with
        | TyNamed (n, args) ->
            (if env.MutualMembers.ContainsKey n then [n] else [])
            @ (args |> List.collect walk)
        | TyTuple ts -> ts |> List.collect walk
        | TyArray (e, idxs) -> walk e @ (idxs |> List.collect walk)
        | TyAbstractArray (e, _, _) -> walk e
        | TyFunc (args, ret) -> (args |> List.collect walk) @ walk ret
        | TyDepIdx (outer, _, body) -> walk outer @ walk body
        | TyConstrained (inner, _) -> walk inner
        | TyBounded (inner, _, _) -> walk inner
        | TyPoly inner -> walk inner
        | TyEquivIdx (_, g, r) -> walk g @ walk r
        | _ -> []
    walk t

/// Annotation side of the check-point rule. Ok None -- no member names
/// anywhere. Ok (Some group) -- a top-level tuple listing exactly one group's
/// full member set, each as a DIRECT element, exactly once (non-member
/// elements alongside are fine). Anything else is a compile error.
and tryMutualAnnotation (env: TypeEnv) (annot: TypeExpr) : TypeResult<MutualGroupInfo option> =
    let allOccurrences = mutualMemberNamesIn env annot
    if allOccurrences.IsEmpty then Ok None
    else
        let groupId = env.MutualMembers.[List.head allOccurrences]
        let group = env.MutualGroups.[groupId]
        let groupNames = group.Members |> List.map fst
        let describe = groupNames |> String.concat ", "
        match annot with
        | TyNamed (n, []) ->
            Error (MutualBindJointly (n, describe, (groupNames |> List.map (_.ToLower()) |> String.concat ", ")))
        | TyTuple elems ->
            let directNames =
                elems |> List.choose (function
                    | TyNamed (n, []) when env.MutualMembers.ContainsKey n -> Some n
                    | _ -> None)
            if directNames.Length <> allOccurrences.Length then
                Error (MutualDirectElementsOnly describe)
            elif directNames |> List.exists (fun n -> env.MutualMembers.[n] <> groupId) then
                Error (MutualMixedGroups ())
            elif (directNames |> List.distinct |> List.length) <> directNames.Length then
                Error (MutualDuplicateMember describe)
            elif Set.ofList directNames <> Set.ofList groupNames then
                Error (MutualIncompleteAnnotation describe)
            else Ok (Some group)
        | _ ->
            Error (MutualJointAnnotationOnly describe)

/// Rename member references in a decl-validated conjunct to a binding's leaf
/// variable names (bare scalar refs and field-path bases alike).
and renameMutualRefs (mapping: Map<string, string>) (e: Expr) : Expr =
    let r = renameMutualRefs mapping
    match e.Kind with
    | ExprKind.ExprVar n when mapping.ContainsKey n -> inheritSpan e (ExprVar mapping.[n])
    | ExprKind.ExprVar _ | ExprKind.ExprLit _ -> e
    | ExprKind.ExprField (o, f) -> inheritSpan e (ExprField (r o, f))
    | ExprKind.ExprApp (f, args) -> inheritSpan e (ExprApp (r f, args |> List.map r))
    | ExprKind.ExprBinOp (mode, op, l, rr) -> inheritSpan e (ExprBinOp (mode, op, r l, r rr))
    | ExprKind.ExprUnaryOp (op, i) -> inheritSpan e (ExprUnaryOp (op, r i))
    | ExprKind.ExprIf (c, t, f) -> inheritSpan e (ExprIf (r c, r t, r f))
    | ExprKind.ExprTuple es -> inheritSpan e (ExprTuple (es |> List.map r))
    | ExprKind.ExprArrayLit es -> inheritSpan e (ExprArrayLit (es |> List.map r))
    | ExprKind.ExprTyped (i, ty) -> inheritSpan e (ExprTyped (r i, ty))
    | _ -> e  // decl-time validation restricts conjuncts to the forms above

/// Synthesize the joint runtime checks at a binding site. `env` must already
/// have the leaf variables bound; the check IRIds are allocated HERE, after
/// the leaves' ids -- module emission is id-ordered, so later ids run later.
and synthesizeMutualChecks (env: TypeEnv) (group: MutualGroupInfo) (memberToLeaf: Map<string, string>) : TypeResult<(IRId * TypedExpr) list> =
    group.Constraints |> List.map (fun conjunct ->
        let renamed = renameMutualRefs memberToLeaf conjunct
        inferExpr env renamed |> Result.map (fun tCond ->
            let checkId = env.Builder.FreshId()
            let msg = $"Mutual constraint violation ({group.GroupId})"
            (checkId, mkTypedSpan (TExprConstraintCheck (tCond, "BL8001", msg)) IRTUnit tCond.Span)))
    |> sequenceResults

/// Binding side of the check-point rule for an annotated let. Ok None -- no
/// obligation here (no group named, or the RHS is a call to a function whose
/// declared return already carries the check). Ok (Some (group, member->leaf))
/// -- synthesize checks after the destructure. Error -- annotation/pattern
/// misuse (lone member, non-tuple pattern, arity mismatch).
and mutualBindingObligation (env: TypeEnv) (binding: Binding) : TypeResult<(MutualGroupInfo * Map<string, string>) option> =
    match binding.Type with
    | None -> Ok None
    | Some annot ->
        tryMutualAnnotation env annot |> Result.bind (function
            | None -> Ok None
            | Some group ->
                match binding.Pattern.Kind, annot with
                | PatternKind.PatTuple pats, TyTuple elems when
                        pats.Length = elems.Length &&
                        pats |> List.forall _.Kind.IsPatVar ->
                    let memberToLeaf =
                        List.zip elems pats
                        |> List.choose (fun (t, p) ->
                            match t, p.Kind with
                            | TyNamed (n, []), PatternKind.PatVar leaf when group.Members |> List.exists (fun (m, _) -> m = n) ->
                                Some (n, leaf)
                            | _ -> None)
                        |> Map.ofList
                    // A call to a declared-return function was already checked
                    // at its return site -- the single verification point.
                    let rec stripT (e: Expr) = match e.Kind with ExprKind.ExprTyped (i, _) -> stripT i | _ -> e
                    let alreadyChecked =
                        match (stripT binding.Value).Kind with
                        | ExprKind.ExprApp ({ Kind = ExprKind.ExprVar f }, _) ->
                            // By the callee's DECLARATION (its binder id), not its
                            // name: another module's same-named function, or a
                            // local value shadowing this one, checked nothing.
                            match lookupVar f env with
                            | Some v ->
                                match env.MutualReturnFuncs.TryGetValue v.VarId with
                                | true, gid -> gid = group.GroupId
                                | _ -> false
                            | None -> false
                        | _ -> false
                    if alreadyChecked then Ok None
                    else Ok (Some (group, memberToLeaf))
                | _ ->
                    let names = group.Members |> List.map fst |> String.concat ", "
                    Error (MutualBindTuple names))

/// Struct where-constraint checks for an assignment target: substitute each
/// field name with `<target>.<field>`, infer, and wrap as inlined guards.
/// Returns [] when the target's type is not a constrained struct. Fires at
/// every assignment of a constrained struct value -- construction bindings,
/// whole-struct reassignment, and field mutation alike (the math runs only
/// in the generated C++).
and synthesizeStructChecks (env: TypeEnv) (targetTy: IRType) (targetSurface: Expr) : TypeResult<TypedExpr list> =
    match env.Subst.Resolve targetTy with
    | IRTNamed sname ->
        match lookupTypeDef sname env with
        | Some (TDIStruct (_, _, declFields, constraints)) when not constraints.IsEmpty ->
            let fieldNames = declFields |> List.map fst |> Set.ofList
            let rec subst (e: Expr) =
                match e.Kind with
                | ExprKind.ExprVar n when fieldNames.Contains n -> inheritSpan e (ExprField (targetSurface, n))
                | ExprKind.ExprVar _ | ExprKind.ExprLit _ -> e
                | ExprKind.ExprField (o, f) -> inheritSpan e (ExprField (subst o, f))
                | ExprKind.ExprApp (f, args) -> inheritSpan e (ExprApp (subst f, args |> List.map subst))
                | ExprKind.ExprBinOp (m, op, l, r) -> inheritSpan e (ExprBinOp (m, op, subst l, subst r))
                | ExprKind.ExprUnaryOp (op, i) -> inheritSpan e (ExprUnaryOp (op, subst i))
                | ExprKind.ExprIf (c, t, f) -> inheritSpan e (ExprIf (subst c, subst t, subst f))
                | ExprKind.ExprTuple es -> inheritSpan e (ExprTuple (es |> List.map subst))
                | ExprKind.ExprArrayLit es -> inheritSpan e (ExprArrayLit (es |> List.map subst))
                | ExprKind.ExprTyped (i, ty) -> inheritSpan e (ExprTyped (subst i, ty))
                | _ -> e
            constraints |> List.mapi (fun i c ->
                inferExpr env (subst c) |> Result.map (fun tCond ->
                    let msg =
                        if constraints.Length = 1 then $"Constraint violation in {sname}"
                        else $"Constraint violation in {sname} (conjunct {i + 1})"
                    mkTypedSpan (TExprConstraintCheck (tCond, "BL8001", msg)) IRTUnit tCond.Span))
            |> sequenceResults
        | _ -> Ok []
    | _ -> Ok []

/// Runtime bound guards for a BOUNDED-PRIMITIVE annotation (formalism section 2.4).
/// Bounds are INCLUSIVE, so this emits `min <= subj` and/or `subj <= max`,
/// one guard per declared endpoint, via the same `TExprConstraintCheck` node
/// the struct guards use.
///
/// STRUCT FIELDS DO NOT COME THROUGH HERE: field bounds normalize into
/// `FieldDecl.Bound` at parse time and are guarded by `synthesizeStructChecks`
/// already -- routing them here too would double every field guard. This
/// serves annotation sites where the surface type is the only carrier
/// (bounds erase in `lowerTypeExpr`, never reaching `IRType`).
///
/// CURRENT SCOPE: `let` binding annotations only. Bounded PARAMETERS,
/// RETURN types, and re-assignment of a bounded mutable are unguarded --
/// each needs the declared annotation at a site that doesn't currently
/// carry it. An ELEMENT-position bound
/// (`Array<Float64<min=0.0, max=1.0> like Y>`) is guarded by
/// `synthesizeElementBoundChecks` below (`boundedConjuncts` sees only a
/// TOP-LEVEL TyBounded, so the two paths never both fire) and inherits the
/// same scope limit.
and synthesizeBoundChecks (env: TypeEnv) (annot: TypeExpr option) (subjectName: string) (targetSurface: Expr) : TypeResult<TypedExpr list> =
    match annot with
    | None -> Ok []
    | Some ty ->
        // Through ALIASES: `type Sal = Float64<psu, min=0.0>` keeps its bound
        // only in the surface body (lowering erases min=/max=), so `let s: Sal`
        // must look the alias up to see it -- without this the inline spelling
        // aborted BL8001 and the aliased one printed -1.
        let ty = resolveSurfaceAlias env 8 ty
        // `Ast.boundedConjuncts` is the ONE definition of what a bounded
        // annotation asserts; the side labels are recovered in parallel from
        // the same node so a one-sided annotation still names its endpoint.
        match boundedConjuncts targetSurface ty with
        | [] -> synthesizeElementBoundChecks env ty subjectName targetSurface
        | conjs ->
            let sides =
                match ty with
                | TyBounded (_, lo, hi) ->
                    (lo |> Option.map (fun _ -> "min") |> Option.toList)
                    @ (hi |> Option.map (fun _ -> "max") |> Option.toList)
                | _ -> []
            let labelled =
                if List.length sides = List.length conjs then List.zip sides conjs
                else conjs |> List.map (fun c -> ("bound", c))
            labelled
            |> List.map (fun (side, c) ->
                inferExpr env c |> Result.map (fun tCond ->
                    let msg = $"Bound violation in '{subjectName}' ({side})"
                    mkTypedSpan (TExprConstraintCheck (tCond, "BL8001", msg)) IRTUnit tCond.Span))
            |> sequenceResults

/// Resolve a surface type through ORDINARY alias chains. Distinct from
/// `lowerTypeExpr`'s resolution, which answers in IRType and has therefore
/// already thrown the bounds away -- this keeps the surface node, which is the
/// only place the bound EXPRESSIONS survive. Fuel-bounded like the
/// elaborators' `resolveTop`.
and internal resolveSurfaceAlias (env: TypeEnv) (fuel: int) (ty: TypeExpr) : TypeExpr =
    if fuel <= 0 then ty else
    match ty with
    | TyNamed (n, []) ->
        match Map.tryFind n env.SurfaceAliases with
        | Some body -> resolveSurfaceAlias env (fuel - 1) body
        | None -> ty
    | TyConstrained (inner, _) -> resolveSurfaceAlias env (fuel - 1) inner
    | _ -> ty

/// The bound written on an array annotation's ELEMENT type. Returns the two
/// endpoints plus the DEPTH of array nesting the bound sits under, so the
/// caller can guard depth 1 and refuse deeper rather than ignore it.
and internal elementBoundOf (env: TypeEnv) (annot: TypeExpr) : (Expr option * Expr option * int) option =
    let rec go depth (t: TypeExpr) =
        match resolveSurfaceAlias env 8 t with
        | TyArray (elem, _) -> go (depth + 1) elem
        | TyBounded (_, lo, hi) when depth > 0 && (lo.IsSome || hi.IsSome) -> Some (lo, hi, depth)
        | _ -> None
    go 0 annot

/// Runtime guards for an ELEMENT-position bound: `Array<Float64<min=0.0,
/// max=1.0> like Y, Z>` asserts EVERY cell is in range. Emits an explicit
/// loop nest with one `TExprConstraintCheck` per endpoint in the innermost
/// body, so the first offending cell panics with the bound's own message.
///
/// NOT the obvious `reduce(lo <= x, (&&), true)`: (1) `inferReduce`'s rank-k
/// desugar requires LITERAL extents, so a `let static n` extent would fail
/// outright with "reduce() currently supports only rank-1 arrays"; (2) that
/// desugar reads cells with untagged ints, so every rank >= 2 bounded array
/// would earn two spurious BL4003 tag warnings on a loop the user never
/// wrote. Building the typed nodes directly avoids both (no inference runs
/// over synthesized code). Loop variables are plain int64 and the cell read
/// is a bare `TExprIndex`, the same shape `inferReduce`'s desugar produces.
///
/// REFUSED rather than silently skipped (a bound that evaporates is the bug
/// this fixes): compact/ragged/grouped/compound axes (not a dense nest) and
/// bounds nested more than one array deep.
and internal synthesizeElementBoundChecks (env: TypeEnv) (annot: TypeExpr) (subjectName: string) (targetSurface: Expr) : TypeResult<TypedExpr list> =
    match elementBoundOf env annot with
    | None -> Ok []
    | Some (_, _, depth) when depth > 1 ->
        Error (ConstraintErr ($"the bound on the element type of '{subjectName}' is not enforced: it sits {depth} array levels deep, and the guard synthesis walks one. Flatten to a single Array<T<min=.., max=..> like I, J, ...> annotation, whose elements ARE checked."))
    | Some (lo, hi, _) ->
        inferExpr env targetSurface |> Result.bind (fun tSubj ->
        match env.Subst.Resolve tSubj.Type with
        | ArrayElem arrTy ->
            let exotic =
                arrTy.IndexTypes |> List.tryFind (fun ix ->
                    ix.IxKind <> IxKPlain || ix.Symmetry <> SymNone)
            match exotic with
            | Some ix ->
                Error (ConstraintErr (sprintf "the bound on the element type of '%s' is not enforced: the array has a %s axis, whose stored cells are not a dense loop nest (folding canonical vs logical cells differs, and ragged/compound axes have no rectangular extent). Bound the element of a dense array, or check the values explicitly."
                                              subjectName
                                              (match ix.Symmetry with
                                               | SymNone -> "ragged, grouped, or compound"
                                               | _ -> "compact symmetric/antisymmetric/Hermitian")))
            | None ->
            let span = targetSurface.Span
            let mkT k ty = mkTypedSpan k ty span
            let int64Ty = IRTScalar ETInt64
            let n = arrTy.IndexTypes.Length
            // One loop variable per axis. Plain int64 (see docstring); the ids
            // come from the builder so they cannot collide with user bindings.
            let idxVars =
                arrTy.IndexTypes |> List.mapi (fun k _ ->
                    ($"__ebnd_{subjectName}_{k}", env.Builder.FreshId()))
            // Upper bound per axis: the index record's own extent when it has
            // folded to a literal, else `extents(subject)` -- scalar at rank 1,
            // a tuple to project at higher rank (inferExtents' two shapes).
            let extentsTy =
                if n = 1 then int64Ty else IRTTuple (List.replicate n int64Ty)
            let hiOf k (ix: IRIndexType) =
                match ix.Extent with
                | IRLit (IRLitInt v) -> mkT (TExprLit (LitInt v)) int64Ty
                | _ ->
                    let ext = mkT (TExprExtents tSubj) extentsTy
                    if n = 1 then ext
                    else mkT (TExprTupleIndex (ext, mkT (TExprLit (LitInt (int64 k))) int64Ty)) int64Ty
            let identity = match tSubj.Kind with TExprVar (_, _, id) -> id | _ -> None
            let elemRead =
                let refs = idxVars |> List.map (fun (nm, vid) -> mkT (TExprVar (nm, vid, None)) int64Ty)
                mkT (TExprIndex (tSubj, refs, identity)) arrTy.ElemType
            // Bounds are INCLUSIVE on both ends, exactly as in the top-level
            // path -- `Ast.boundedConjuncts`' orientation, one guard per endpoint.
            let endpoints =
                (lo |> Option.map (fun l -> ("min", l, true)) |> Option.toList)
                @ (hi |> Option.map (fun h -> ("max", h, false)) |> Option.toList)
            endpoints
            |> List.map (fun (side, bexpr, isLo) ->
                // The endpoint belongs to the cell's declared numeric type.
                // Inferring it alone rounds a Float64 bound through Float32
                // before the synthesized comparison ever sees the cell.
                checkExpr env arrTy.ElemType bexpr |> Result.map (fun tB ->
                    let cond =
                        if isLo then mkT (TExprBinOp (Elementwise, OpLe, tB, elemRead)) (IRTScalar ETBool)
                        else mkT (TExprBinOp (Elementwise, OpLe, elemRead, tB)) (IRTScalar ETBool)
                    let msg = $"Bound violation in an element of '{subjectName}' ({side})"
                    TStmtExpr (mkT (TExprConstraintCheck (cond, "BL8001", msg)) IRTUnit)))
            |> sequenceResults
            |> Result.map (fun checkStmts ->
                let zero = mkT (TExprLit (LitInt 0L)) int64Ty
                let his = arrTy.IndexTypes |> List.mapi hiOf
                let nest =
                    List.foldBack2 (fun (nm, vid) hiE inner -> [TStmtForIn (nm, vid, zero, hiE, inner)])
                        idxVars his checkStmts
                [ mkT (TExprBlock (nest, None)) IRTUnit ])
        // Not an array after all (e.g. the annotation lost to inference) --
        // nothing to walk. The aggregate REJECTION path owns the other
        // direction, so this cannot silently swallow a misplaced bound.
        | _ -> Ok [])

/// Struct checks for an assignment statement/expression: a field mutation
/// re-checks the OBJECT's constraints; any other target checks the assigned
/// value's own struct type.
and structChecksForAssign (env: TypeEnv) (lhsSurface: Expr) (tL: TypedExpr) : TypeResult<TypedExpr list> =
    match lhsSurface.Kind, tL.Kind with
    | ExprKind.ExprField (objSurface, _), TExprField (tObj, _, _) ->
        synthesizeStructChecks env tObj.Type objSurface
    | _ ->
        synthesizeStructChecks env tL.Type lhsSurface

and inferForExpr env source kernelOpt : TypeResult<TypedExpr> =
    match source with
    | ForArrays (arrays, Some inClause) ->
        // Co-iteration: for (A, B) in range<Idx<N>> <@> lambda(a, b) -> ...
        // All arrays share the iteration space from the in-clause
        arrays |> List.map (inferExpr env) |> sequenceResults |> Result.bind (fun tArrays ->
        inferExpr env inClause |> Result.bind (fun tVirtual ->
            // The `in` clause supplies the shared iteration index, so it must
            // be a VIRTUAL array (range<...>, reverse<...>, blocked<...>) --
            // its type is an all-SIdxVirt arrow (ArrayElem's IsVirtual). A
            // stored array, zip, or loop object is rejected here: co-iterating
            // stored arrays is `for (A, B)` with no `in` clause (==
            // method_for(A, B)); zipping them is method_for(zip(A, B)).
            let sharedRecordsRes =
                match env.Subst.Resolve(tVirtual.Type) with
                | ArrayElem at when at.IsVirtual && not (List.isEmpty at.IndexTypes) ->
                    // ALL of the in-clause's slots become shared iteration
                    // records -- `for (A, B) in range<Lat, Lon>` co-iterates the
                    // full LatxLon product space. Multi-slot spaces require
                    // every slot plain dense (a sole packed/compound slot is
                    // fine -- its Rank levels walk the flat canonical cells).
                    if coIterableRecords at.IndexTypes then Ok at.IndexTypes
                    else Error (IndexSpaceErr "for (...) in range<...>: a multi-slot iteration space must consist of plain dense index types (a packed or compound slot cannot share the product with other slots)")
                | resolved ->
                    Error (BuiltinArgErr $"the `in` clause of `for (...) in <source>` must be a virtual array (range<...>, reverse<...>, or blocked<...>) -- it supplies the shared iteration index; got {ppIRType resolved}. Drop the `in` clause to co-iterate stored arrays (for (A, B) == method_for(A, B)), or use method_for(zip(A, B)) to zip them.")
            sharedRecordsRes |> Result.bind (fun sharedRecords ->

            let identities = List.map2 (fun (arr: Expr) (ta: TypedExpr) ->
                match arr.Kind, ta.Kind with
                | ExprKind.ExprVar name, TExprVar (tn, vid, _) when tn = name -> loopOperandIdentity env name vid
                | ExprKind.ExprVar name, _ -> AIDVariable name
                | _ -> AIDLiteral (env.Builder.FreshId())) arrays tArrays
            // For co-iteration, all arrays use the shared index space. A
            // generic declaration's unannotated parameter has no caret to
            // read: see materializeGenericLoopOperand.
            tArrays |> List.iter (fun ta -> materializeGenericLoopOperand env ta "for")
            match tArrays |> List.map (fun ta -> demandNamedLoopOperand env ta "for") |> sequenceResults with
            | Error e -> Error e
            | Ok _ ->
            let arrayTypes = tArrays |> List.mapi (fun i ta ->
                loopOperandArrayType env (fun () -> getArrayType env arrays.[i]) ta.Type)
            // Real per-array S-dim counts (see the zip arms: the IRTInfer
            // fallback in buildApplyInfo needs true ranks to compute the
            // kernel slice rank when this loop reaches it via inferApply).
            let sDimsPerArray = computeSDimsPerArray arrayTypes
            let totalSDims = sDimsPerArray |> List.sum

            let mfInfo : TypedMethodForInfo = {
                Arrays = tArrays; Identities = identities; ArrayTypes = arrayTypes
                SDimsPerArray = sDimsPerArray; TotalSDims = totalSDims
                SharedIndexTypes = sharedRecords
            }
            let loopTy = IRTLoop {
                Kind = LKMethod; Arity = Some arrays.Length
                ArrayTypes = arrayTypes |> List.map mkArrayLike; KernelType = None
            }
            let tLoop = mkTyped (TExprMethodFor mfInfo) loopTy
            
            match kernelOpt with
            | Some kernel ->
                // Infer the kernel and build co-iteration ApplyInfo directly
                inferExpr env kernel |> Result.bind (fun tK ->
                    let resolvedKernel = resolveTypedExpr env tK
                    match resolvedKernel.Kind with
                    | TExprLambda lambdaInfo ->
                        // Kernel arity: N (one value param per co-iterated array)
                        // or N + R (values plus the R shared iteration indices).
                        // In the N + R form the in-clause virtual source rides
                        // along as a TRAILING operand -- its per-slot params bind
                        // to the loop indices through the same VirtualRange
                        // element machinery the outer-product path uses for
                        // range<...> slots, so `for (uq, ph) in range<Y, X> <@>
                        // lambda(zu, zp, i, j) -> ...` gives the kernel both the
                        // co-iterated values and the (i, j) coordinates.
                        let nOperands = tArrays.Length
                        let idxSlotTypes =
                            sharedRecords |> List.collect (fun r -> List.replicate r.Rank (elemTypeForIterationIndex r))
                        let idxParamCount = idxSlotTypes.Length
                        let wantsIndices = lambdaInfo.Params.Length = nOperands + idxParamCount
                        if not wantsIndices && lambdaInfo.Params.Length <> nOperands then
                            Error (ArgCountErr $"for (...) in co-iteration kernel takes {nOperands} parameter(s) (one per co-iterated array) or {nOperands + idxParamCount} (values plus the {idxParamCount} shared iteration indices), got {lambdaInfo.Params.Length}")
                        else
                        // Bind the kernel params to their iterated types: value
                        // params to the operands' ELEMENT types, index params to
                        // the tagged Nat<...> slot types. The body was inferred
                        // against unresolved vars, so deferred intrinsics (e.g.
                        // imag(zp) on a complex operand) only resolve correctly
                        // once the params are unified here -- the same post-body
                        // unification buildApplyInfo performs for <@> applies.
                        let paramUnifyResult =
                            let valueTypes = arrayTypes |> List.map (_.ElemType)
                            let rowTypes = if wantsIndices then valueTypes @ idxSlotTypes else valueTypes
                            if lambdaInfo.Params.Length = rowTypes.Length then
                                List.zip lambdaInfo.Params rowTypes
                                |> List.fold (fun acc (p, row) ->
                                    acc |> Result.bind (fun () -> unify env.Subst (env.Subst.Resolve p.Type) row))
                                    (Ok ())
                            else Ok ()
                        match paramUnifyResult with
                        | Error e -> Error e
                        | Ok () ->
                        // (F2: projections of the now-bound parameters)
                        settleOpenProjections env lambdaInfo.Body
                        // Extended operand lists: the virtual source appended
                        // LAST so the real arrays keep their param positions.
                        let (exArrays, exIdentities, exTypes) =
                            if wantsIndices then
                                let vAt =
                                    match env.Subst.Resolve tVirtual.Type with
                                    | ArrayElem at -> at
                                    | _ -> { ElemType = IRTScalar ETInt64; IndexTypes = sharedRecords; IsVirtual = true; Identity = None }
                                (tArrays @ [tVirtual],
                                 identities @ [AIDLiteral (env.Builder.FreshId())],
                                 arrayTypes @ [vAt])
                            else (tArrays, identities, arrayTypes)
                        let exSDims = computeSDimsPerArray exTypes
                        let exTotalSDims = List.sum exSDims
                        let exMfInfo : TypedMethodForInfo = {
                            mfInfo with
                                Arrays = exArrays; Identities = exIdentities; ArrayTypes = exTypes
                                SDimsPerArray = exSDims; TotalSDims = exTotalSDims
                        }
                        // Carry the resolved param types into the stored kernel
                        // (records are immutable -- the substitution refinements
                        // above don't rewrite lambdaInfo.Params in place).
                        let resolvedLambdaInfo =
                            { lambdaInfo with
                                Params = lambdaInfo.Params |> List.map (fun p -> { p with Type = env.Subst.Resolve p.Type }) }
                        let storedKernel = mkTyped (TExprLambda resolvedLambdaInfo) tK.Type
                        // Infer element type: prefer kernel return type, fall back to arrays.
                        // Phase B2: returns IRType.
                        let elemType =
                            let resolved = env.Subst.Resolve(lambdaInfo.ReturnType)
                            match resolved with
                            | IRTScalar _ as t -> t
                            | ArrayElem arr -> arr.ElemType
                            | IRTUnitAnnotated (IRTScalar _, _) as t -> t
                            | _ ->
                                match arrayTypes with
                                | at :: _ -> at.ElemType
                                | [] -> IRTScalar ETFloat64
                        // Output type: array with shared index structure + kernel T-dims
                        let (kernelTDims, kernelOutputRank) =
                            let resolved = env.Subst.Resolve(lambdaInfo.ReturnType)
                            match resolved with
                            | ArrayElem arr ->
                                let tDims = arr.IndexTypes |> List.map (fun idx -> { idx with Kind = TDimension })
                                (tDims, tDims.Length)
                            | _ -> ([], 0)
                        let outputIndexTypes = sharedRecords @ (kernelTDims |> List.map (fun idx -> { idx with Id = env.Builder.FreshId() }))
                        let outputType = mkArrayArrow outputIndexTypes elemType None
                        // G67: a compact kernel cell is refused on this path too
                        // (the same check as inferApply's).
                        match kernelTDims |> List.tryFind (fun ix -> ix.Symmetry <> SymNone) with
                        | Some _ -> Error (RowMapCompactCell "has a compact index group")
                        | None ->
                        // Note: SymcomStates/TriangularLevels/SpeedupFactor are unused
                        // by the co-iteration codegen path -- it derives loop structure
                        // directly from SharedIndexTypes
                        let info : TypedApplyInfo = {
                            Loop = mkTyped (TExprMethodFor exMfInfo) loopTy
                            Kernel = storedKernel
                            Arrays = exArrays; Identities = exIdentities
                            ArrayTypes = exTypes; SharedIndexTypes = sharedRecords
                            SymcomStates = List.replicate exTotalSDims SCNeither
                            TriangularLevels = List.replicate exTotalSDims false
                            SDimsPerArray = exSDims
                            KernelInputRanks = lambdaInfo.Params |> List.map (fun _ -> 0)
                            KernelOutputRank = kernelOutputRank
                            KernelTDims = kernelTDims
                            SpeedupFactor = 1L; ReynoldsSpeedup = 1L
                            HasReynolds = false; OutputType = outputType
                            IsCoIteration = true
                            IsComposeApply = false
                        }
                        Ok (mkTyped (TExprApply info) outputType)
                    | _ ->
                        // Fallback: treat as generic apply
                        inferApply env tLoop tK)
            | None -> Ok tLoop)))

    | ForArrays (arrays, None) ->
        // No in-clause: equivalent to method_for(arrays)
        inferMethodFor env arrays |> Result.bind (fun tLoop ->
            match kernelOpt with
            | Some kernel -> inferExpr env kernel |> Result.bind (fun tK -> inferApply env tLoop tK)
            | None -> Ok tLoop)
    | ForKernel kernel ->
        inferObjectFor env kernel |> Result.bind (fun tLoop ->
            match kernelOpt with
            | Some e -> inferExpr env e |> Result.bind (fun tA -> inferApply env tLoop tA)
            | None -> Ok tLoop)
