// TypeCheck.fs - Type Checking and Inference
//
//   - Unification with substitution (inference variables resolve through constraints)
//   - Extent preservation (Idx<180> keeps extent=180, not placeholder 0)
//   - Pattern type checking (match arms produce correct bindings)
//   - Function and type declaration registration in environment
//   - Struct/sum type field resolution
//   - Symmetry analysis for <@> combinator applications
//   - Explicit errors for all unhandled forms (no silent fallthrough)
//
// Public API (consumed by Lowering.fs and Main.fs):
//   - typeCheck : Program -> Result<TypedProgram, CompileError list>
//   - TypeError union: UnboundVariable | TypeMismatch | ArityMismatch
//                      | InvalidArrayCapture | InvalidApplication
//                      | PatternTypeMismatch | Other

module Blade.TypeCheck

open Blade.Ast
open Blade.IR
open Blade.Types
open Blade.TypedAst
open Blade.Unify
open Blade.TypeEnv
open Blade.Zonk

/// IDE side-channel for stage-3/4 confirm-and-pin suggestions (BL4010): the
/// structured twin of the plain-string warning the CLI prints. Each entry is
/// (message, kernel span) so editor tooling can render a ghost annotation at
/// the kernel and offer the one-click pin. Reset at the top of typeCheck,
/// recorded at buildApplyInfo's suggestion site. AsyncLocal, like IdePartial
/// (defined near typeCheck below); lives at the file head because its writer
/// (buildApplyInfo) precedes typeCheck in the compilation order.
module PinSuggestions =
    let private slot = new System.Threading.AsyncLocal<(string * Span) list>()
    let reset () = slot.Value <- []
    let add (msg: string) (span: Span) = slot.Value <- (msg, span) :: slot.Value
    let get () : (string * Span) list =
        match box slot.Value with null -> [] | _ -> List.rev slot.Value

/// The TOP-LEVEL WIDTH of a parameter's WRITTEN type annotation, in nodes of
/// the pack's spine (docs/plan-tuples-vs-arg-packs.md 6c). `Tuple<k>` is the
/// width-only spelling; a fully written `(T1, ..., Tk)` says the same thing
/// with the element types filled in, so both count k -- that is what turns
/// 3.6's M6 (a tuple-annotated kernel param) from a miscompile into the main
/// path. Everything else is width 1. Nesting INSIDE the annotation is not
/// counted: `((A,B),(C,D))` is width 2, not 4 (diagnostics/062).
///
/// Deliberately over the SURFACE `TypeExpr`, not over the lowered `IRType`:
/// both spellings lower to `IRTTuple`, but so does an UNANNOTATED param the
/// moment a tuple-typed row unifies into it. Reading widths off the lowered
/// type would therefore make pack widths inference-dependent, which is exactly
/// the ordering cliff 5.1 rules out (kernel bodies are inferred before their
/// params are bound). Ruling 1: tuple-ness is always written.
let declaredTupleWidth (t: TypeExpr option) : int option =
    match t with
    | Some (TyTupleWidth n) when n >= 2 -> Some n
    | Some (TyTuple ts) when ts.Length >= 2 -> Some ts.Length
    | _ -> None

/// `let (a, b, c) = <2-tuple>` -- the pattern's leaf count matches NEITHER the
/// scrutinee's top-level width NOR its flattened leaf count, so there is no
/// reading under which every name gets a component.
///
/// Every destructuring site used to fall back to the structural list and let
/// FRESH INFERENCE VARIABLES cover the overflow ("Fall back to structural, let
/// fresh vars handle overflow"). Measured consequence: `blade check` says OK
/// and `g++` then rejects `std::get<2>` applied to a `std::tuple<double,
/// double>`. Shared by all four destructuring sites (`DeclLet`, `let static`,
/// block `StmtLet`, expression position) so they cannot drift.
///
/// Deliberately silent for every NON-tuple scrutinee: a `Poly` pack, a struct,
/// or a type still unresolved at this point is somebody else's judgement, and
/// answering here would turn inference order into a diagnostic.
let tupleDestructureArityError (env: TypeEnv) (pats: Pattern list) (valueTy: IRType) : TypeError option =
    match env.Subst.Resolve valueTy with
    | IRTTuple ts ->
        let flat = IR.flattenTupleLeaves (IRTTuple ts)
        if pats.Length = ts.Length || pats.Length = flat.Length then None
        else
            let flatNote =
                if flat.Length <> ts.Length
                then sprintf " (%d leaves when flattened)" flat.Length
                else ""
            Some (Other (sprintf
                    "this `let` binds %d names, but the value is a %d-tuple%s. A tuple pattern needs one name per component -- or one per flattened leaf -- so write %d, or project the components you want with `t[i]`."
                    pats.Length ts.Length flatNote ts.Length))
    | _ -> None

/// Fourth family member -- see `TypeEnv.WarningLog` for the storage and why it
/// has to live down there (its only writer is `emitWarning`, and TypeEnv cannot
/// reference upward). Re-exported here so that
/// `Blade.TypeCheck.{PinSuggestions, IdePartial, WarningLog}` is the ONE
/// namespace a drain site reads.
module WarningLog =
    let reset () = Blade.TypeEnv.WarningLog.reset ()
    let get () : Blade.Diagnostics.Diagnostic list = Blade.TypeEnv.WarningLog.get ()

/// Fifth family member -- storage in `TypeEnv.DeducedFacts` (Zonk writes to it
/// too, and Zonk cannot reference TypeCheck). Re-exported for the same
/// one-namespace reason as WarningLog.
module DeducedFacts =
    let reset () = Blade.TypeEnv.DeducedFacts.reset ()
    let get () : (Blade.TypeEnv.DeducedFact * Span) list = Blade.TypeEnv.DeducedFacts.get ()
/// IDE side-channel for the deduction RESULTS themselves -- the structured twin
/// of PinSuggestions' prose. The per-function tables (FuncDeducedPairs,
/// PackDeducedComm) and the rank pins live in the TypeEnv/Subst local to
/// checkProgram and are gone when it returns, so `ide check --json` records
/// them here as they are computed: named functions by name, lambda kernels by
/// source span. Reset alongside PinSuggestions; AsyncLocal like IdePartial.
module IdeDeductions =
    /// One lambda-kernel instantiation at the apply seam: kernel span, param
    /// names, adjacent-pair parities (n-1 entries), declared where-clause
    /// conjuncts (comm/anticomm, rendered), and per-param cell ranks.
    type KernelInfo = {
        KSpan: Span
        KParams: string list
        KParities: Blade.Deduce.Parity list
        KDeclared: string list
        KRanks: (string * int) list
    }
    let private pairs = new System.Threading.AsyncLocal<(string * (string list * Blade.Deduce.Parity list)) list>()
    let private packs = new System.Threading.AsyncLocal<(string * (string * Blade.Deduce.Parity)) list>()
    let private kernels = new System.Threading.AsyncLocal<KernelInfo list>()
    let reset () =
        pairs.Value <- []
        packs.Value <- []
        kernels.Value <- []
    let addPairs (funcName: string) (paramNames: string list) (ps: Blade.Deduce.Parity list) =
        pairs.Value <- (funcName, (paramNames, ps)) :: pairs.Value
    let addPack (funcName: string) (packParam: string) (p: Blade.Deduce.Parity) =
        packs.Value <- (funcName, (packParam, p)) :: packs.Value
    let addKernel (info: KernelInfo) =
        kernels.Value <- info :: kernels.Value
    let private read (slot: System.Threading.AsyncLocal<'a list>) =
        match box slot.Value with null -> [] | _ -> List.rev slot.Value
    let getPairs () = read pairs
    let getPacks () = read packs
    let getKernels () = read kernels

let rec evalConstExpr (env: TypeEnv) (expr: Expr) : int64 option =
    match expr.Kind with
    | ExprKind.ExprLit (LitInt n) -> Some n
    | ExprKind.ExprLit (LitFloat f) -> Some (int64 f)
    | ExprKind.ExprVar name ->
        match lookupVar name env with
        | Some info ->
            match info.Type with
            | IRTNat (Some n) -> Some (int64 n)
            | _ -> None
        | None -> None
    | ExprKind.ExprBinOp (_, OpAdd, l, r) ->
        match evalConstExpr env l, evalConstExpr env r with
        | Some a, Some b -> Some (a + b) | _ -> None
    | ExprKind.ExprBinOp (_, OpSub, l, r) ->
        match evalConstExpr env l, evalConstExpr env r with
        | Some a, Some b -> Some (a - b) | _ -> None
    | ExprKind.ExprBinOp (_, OpMul, l, r) ->
        match evalConstExpr env l, evalConstExpr env r with
        | Some a, Some b -> Some (a * b) | _ -> None
    | ExprKind.ExprBinOp (_, OpDiv, l, r) ->
        match evalConstExpr env l, evalConstExpr env r with
        | Some a, Some b when b <> 0L -> Some (a / b) | _ -> None
    | _ -> None

/// Evaluate an expression to a compile-time int under the FULL static
/// contract (the replicate-count rule): a literal, a Nat-typed var, a
/// `let static` value, or a static-function call. Two tiers: the cheap
/// evalConstExpr first, then StaticEval against the StaticValues/
/// StaticFunctions maps populated by checkModule's pre-pass. Shared by the
/// Dist annotation order (lowerTypeExpr's TyDist arm) and the cumulant
/// projection order (inferCumulantProj).
let staticEnvOf (env: TypeEnv) : StaticEval.StaticEnv =
    { Values = env.StaticValues
      Functions =
        env.StaticFunctions
        |> Map.map (fun _ (fd: FunctionDecl) ->
            { StaticEval.Name = fd.Name
              StaticEval.Params = fd.Params |> List.map (fun p -> p.Name)
              StaticEval.Body = fd.Body })
      CalledFunctions = ref Set.empty
      ProviderRoots = Map.empty
      Structs = Map.empty }

let evalStaticIntExpr (env: TypeEnv) (expr: Expr) : int option =
    match evalConstExpr env expr with
    | Some n -> Some (int n)
    | None ->
        match StaticEval.evalExpr (staticEnvOf env) StaticEval.maxSteps expr with
        | Ok (StaticEval.SVInt v) -> Some (int v)
        | _ -> None

/// Evaluate an expression to its raw StaticValue under the same full static
/// contract as evalStaticIntExpr. For type arguments whose payload is
/// structured rather than an int (the IrrepsIdx spec: an array of triples,
/// which StaticEval folds to nested SVTuples).
let evalStaticValueExpr (env: TypeEnv) (expr: Expr) : Result<StaticEval.StaticValue, string> =
    StaticEval.evalExpr (staticEnvOf env) StaticEval.maxSteps expr

/// Dist provenance of a surface expression: the union of the provenance
/// sets of every variable reachable in it (conservative -- an
/// over-approximated source set can only make independence HARDER to
/// prove, never easier, so union is sound). Empty means "unknown", which
/// consumers treat as un-provable rather than vacuously independent.
/// Sources of ground truth: module-level dist bindings (seeded from the
/// PPL elaboration state at checkDecl) and Dist-typed function parameters
/// (seeded with their license token at checkFunctionDecl).
let rec provenanceOfSurface (env: TypeEnv) (e: Expr) : Set<string> =
    let prov = provenanceOfSurface env
    let unionMany es = es |> List.map prov |> List.fold Set.union Set.empty
    match e.Kind with
    | ExprKind.ExprVar n ->
        (match lookupVar n env with
         | Some vi ->
             match env.Provenance.TryGetValue vi.VarId with
             | true, s -> s
             | _ -> Set.empty
         | None -> Set.empty)
    | ExprKind.ExprBinOp (_, _, l, r) -> Set.union (prov l) (prov r)
    | ExprKind.ExprUnaryOp (_, x) -> prov x
    | ExprKind.ExprApp ({ Kind = ExprKind.ExprVar "__ppl_cumulant" }, _) -> Set.empty   // a projected component is an ARRAY, not a dist
    | ExprKind.ExprApp (_, args) -> unionMany args            // call result: union of Dist-relevant args (conservative)
    | ExprKind.ExprTyped (x, _) -> prov x
    | ExprKind.ExprTuple es -> unionMany es
    | ExprKind.ExprIf (_, t, f) -> Set.union (prov t) (prov f)
    | ExprKind.ExprLet (b, body) -> Set.union (prov b.Value) (prov body)
    | ExprKind.ExprBlock (stmts, fin) ->
        let stmtProv s =
            let rec go s =
                match s with
                | StmtSpanned (inner, _) -> go inner
                | StmtLet b -> prov b.Value
                | StmtAssign (_, _, r) -> prov r
                | StmtExpr x -> prov x
                | StmtForIn (_, _, _) -> Set.empty
            go s
        Set.union (stmts |> List.map stmtProv |> List.fold Set.union Set.empty)
                  (fin |> Option.map prov |> Option.defaultValue Set.empty)
    | _ -> Set.empty

/// Lower an extent expression to IRExpr, preserving as much info as possible.
let lowerExtentExpr (env: TypeEnv) (expr: Expr) : IRExpr =
    match evalConstExpr env expr with
    | Some n -> IRLit (IRLitInt n)
    | None ->
        match expr.Kind with
        | ExprKind.ExprVar name -> IRParam (name, 0, IRTNat None)
        | ExprKind.ExprLit (LitInt n) -> IRLit (IRLitInt n)
        | _ -> IRParam ("?", 0, IRTNat None)

/// Lower an extent expression with one bound parameter substituted for an IR
/// expression. Used by DepIdx to substitute the lambda parameter (the outer
/// index variable) into the inner extent expression. Walks the AST recursively
/// so binary-op extents like `n - i` lower correctly.
///
/// This is more general than `lowerExtentExpr`, which falls through to a `?`
/// placeholder for anything beyond ExprLit and ExprVar. For DepIdx the inner
/// extent expression is consumed directly at the iteration-bound emission
/// site, so the expression structure must survive.
let rec substituteAndLowerExtent (env: TypeEnv) (paramName: Ident) (subst: IRExpr) (expr: Expr) : IRExpr =
    match expr.Kind with
    | ExprKind.ExprVar n when n = paramName -> subst
    | _ ->
        match evalConstExpr env expr with
        | Some k -> IRLit (IRLitInt k)
        | None ->
            match expr.Kind with
            | ExprKind.ExprVar name -> IRParam (name, 0, IRTNat None)
            | ExprKind.ExprLit (LitInt n) -> IRLit (IRLitInt n)
            | ExprKind.ExprBinOp (_mode, op, l, r) ->
                let l' = substituteAndLowerExtent env paramName subst l
                let r' = substituteAndLowerExtent env paramName subst r
                let irOpOpt =
                    match op with
                    | OpAdd -> Some IRAdd
                    | OpSub -> Some IRSub
                    | OpMul -> Some IRMul
                    | OpDiv -> Some IRDiv
                    | OpMod -> Some IRMod
                    | _ -> None
                match irOpOpt with
                | Some irOp -> IRBinOp (IRElementwise, irOp, l', r')
                | None -> IRParam ("?", 0, IRTNat None)
            | ExprKind.ExprUnaryOp (OpNeg, e) ->
                IRUnaryOp (IRNeg, substituteAndLowerExtent env paramName subst e)
            | _ ->
                IRParam ("?", 0, IRTNat None)

// 4. AST TypeExpr -> IRType (with extent preservation)

/// Names that can never be a type VARIABLE: the built-in scalar bases and the
/// built-in constructors. Deliberately the same list `prescanTypeVarNames`
/// tests, and used for the same decision from the other side -- the
/// `T<u>^k` head (array-expression plan bug #8) is a variable exactly when
/// the name is neither one of these nor a declared type. Kept next to
/// `lowerTypeExpr` because that is where the head is classified; the
/// `unitSlotBases` set further down answers a different question (which
/// bases OWN a unit slot) and is not interchangeable.
let isConcreteTypeBaseName (name: string) : bool =
    match name with
    | "Int" | "Int32" | "Int64"
    | "Float" | "Float32" | "Float64" | "Double"
    | "Complex64" | "Complex128"
    | "Bool" | "Void" | "Nat" | "String" | "Char"
    | "Poly" | "Array" | "Dist" -> true
    | _ -> false

/// The UNIT named by a type-variable head's argument list, if any -- the three
/// spellings `tryResolveTagArg` admits in a `Float<u>` slot: a bare unit name,
/// a compound unit expression, and the `u^n` power spelling that parses as a
/// rank-marked type var.
///
/// Hoisted out of `lowerTypeExpr`'s `T<u>^k` arm so that the CARET-FREE
/// spelling `T<u>` decides the same question with the same code. The owner's
/// ruling (2026-08-08) is that `T<u>` and `T<u>^0` are the SAME type, so the
/// only safe implementation is one where the two spellings cannot disagree
/// about whether the head is a variable at all.
let unitOfTypeVarArgs (env: TypeEnv) (args: TypeExpr list) : UnitSig option =
    match args with
    | [TyNamed (argName, [])] -> Map.tryFind argName env.Units
    | [TyUnitExpr ue] ->
        (match resolveUnitExpr env.Units ue with Ok s -> Some s | Error _ -> None)
    | [TyVar (argName, Some n)] when Map.containsKey argName env.Units ->
        (match resolveUnitExpr env.Units (UnitPow (UnitNamed argName, n)) with
         | Ok s -> Some s
         | Error _ -> None)
    | _ -> None

/// Is `TyNamed (name, args)` the CARET-FREE spelling of a unit-carrying type
/// VARIABLE (`T<time>`), rather than an ordinary named-type application?
///
/// Exactly the three conditions the caret arm applies to its head -- not a
/// built-in base, not a declared type, and an argument list that resolves to a
/// unit -- so `T<u>` and `T<u>^0` classify identically by construction.
///
/// Deliberately NOT gated on `Subst.IsTypeVar`. A defaulted parameter's
/// annotation is re-lowered at the CALL SITE (`tryFillDefaultArgs`' `mkFill`
/// ascription), where the callee's prescanned type-var scope is gone -- which
/// is precisely why bare `T` and `T^0` are NOT equivalent today (measured:
/// `function f(t: T = 0.0)` called as `f()` is BL3001 "expected T, got
/// Float64", while `t: T^0` is accepted; `TyVar` mints a variable
/// unconditionally, `TyNamed` consults the scope). The motivating program
/// writes `t_zero: T<time> = 0.0`, i.e. exactly that shape, so a scope-gated
/// rule would have broken on its first use.
let isUnitCarryingTypeVarHead (env: TypeEnv) (name: string) (args: TypeExpr list) : bool =
    not args.IsEmpty
    && not (isConcreteTypeBaseName name)
    && (lookupTypeDef name env).IsNone
    && (unitOfTypeVarArgs env args).IsSome

let rec lowerTypeExpr (env: TypeEnv) (ty: TypeExpr) : IRType =
    match ty with
    | TyInt32 -> IRTScalar ETInt32
    | TyInt64 -> IRTScalar ETInt64
    | TyFloat32 -> IRTScalar ETFloat32
    | TyFloat64 -> IRTScalar ETFloat64
    | TyComplex64 -> IRTScalar ETComplex64
    | TyComplex128 -> IRTScalar ETComplex128
    | TyBool -> IRTScalar ETBool
    | TyUnit -> IRTUnit
    | TyString -> IRTScalar ETString
    | TyChar -> IRTScalar ETInt32

    // A BARE wildcard is unreachable from the surface grammar -- the parser
    // only builds TyWildcard as the sole argument of `Base<_>`, handled just
    // below. Kept total (and equal to `Nat<_>`) so the match stays exhaustive
    // and FS0025 keeps auditing future TypeExpr growth.
    | TyWildcard -> IRTIdxTagged (IRTScalar ETInt64, IRefAny)

    // Tag wildcard `Base<_>`: lower the base bare and wrap it in the
    // any-tag marker, ahead of the name dispatch so no base silently drops
    // the wildcard. `Nat<_>` and `Float64<_>` are the useful spellings;
    // `MyStruct<_>` lowering to "any MyStruct" is harmless. Position
    // legality is enforced separately by irTypeHasTagWildcard.
    | TyNamed (name, [TyWildcard]) ->
        // Nat's bare form is IRTNat (a type-level natural, not a value type),
        // so the wildcard's inner follows elemTypeForIterationIndex instead --
        // int64, exactly what a tagged index VALUE carries.
        let inner =
            if name = "Nat" then IRTScalar ETInt64
            else lowerTypeExpr env (TyNamed (name, []))
        IRTIdxTagged (inner, IRefAny)

    // Bounded primitive (section 2.4): the bounds are a REFINEMENT of the base type,
    // not a distinct runtime representation, so they erase here -- `Float<min=0,
    // max=1>` lowers exactly as `Float`, and `Float<velocity, min=0, max=1>`
    // exactly as `Float<velocity>` (the unit lives on the base node). Nothing
    // downstream -- unification, promotion, codegen -- needs to know. The bounds
    // are enforced where the annotation is WRITTEN: struct fields normalize
    // into the conjunct list at parse time, and other annotation sites carry
    // the surface TypeExpr (Ast.boundedConjuncts).
    | TyBounded (baseTy, _, _) -> lowerTypeExpr env baseTy

    // CARET-FREE `T<u>`: the SAME TYPE as `T<u>^0` (owner ruling, 2026-08-09 --
    // "`^0` should be optional, they're semantically equivalent"). The head is
    // a unit-carrying type VARIABLE under exactly the conditions the caret arm
    // uses (isUnitCarryingTypeVarHead); a real named type keeps its ordinary
    // reading and falls through to the arm below.
    //
    // Implemented as a DESUGAR onto the caret node rather than as a second
    // construction, so the two spellings cannot drift: unification, the unit
    // checks, HM monomorphization, printing and every diagnostic see one
    // representation, and a future change to `T<u>^0` reaches `T<u>` for free.
    //
    // What it replaces: `T<time>` used to fall to the named-type fallback and
    // lower to `IRTNamed "T"` -- an opaque nominal type with the unit SILENTLY
    // DROPPED, so every argument was BL3001 "the parameter is declared T but
    // the argument is Float64<second>". The annotation was not an error and not
    // meaningful either, which is the worst of the three options.
    | TyNamed (name, args) when isUnitCarryingTypeVarHead env name args ->
        lowerTypeExpr env
            (TyAbstractArray (TyNamed (name, args),
                              { Kind = ExprKind.ExprLit (LitInt 0L); Span = noSpan },
                              None))

    | TyNamed (name, args) ->
        // Helper: try to resolve a type arg as a unit annotation, then -- for
        // integer bases -- as a nominal index-type alias. `taggedInner` is the
        // type that sits UNDER an index tag, which differs from `baseType`
        // for Nat (whose bare form is the type-level IRTNat).
        //
        // Units are tried first so `Nat<angular_momentum>` keeps its existing
        // meaning; an index alias only wins when the name is not a unit.
        let tryResolveTagArg baseType (taggedInner: IRType) args =
            let isIntBase =
                match taggedInner with
                | IRTScalar (ETInt32 | ETInt64) -> true
                | _ -> false
            match args with
            | [TyNamed (argName, [])] ->
                match Map.tryFind argName env.Units with
                | Some unitSig -> IRTUnitAnnotated (baseType, unitSig)
                | None when isIntBase ->
                    // `Nat<LatIdx>` -- the explicit spelling of what
                    // elemTypeForIterationIndex produces for `range<LatIdx>`.
                    match lookupTypeDef argName env with
                    | Some (TDIIndexType _) ->
                        IRTIdxTagged (taggedInner, IRefNamed argName)
                    | Some (TDIEnumIdx (_, _, values, _)) ->
                        IRTIdxTagged (IRTScalar (EnumValue.underlyingElemType values),
                                      IRefNamed argName)
                    | _ -> baseType
                | None -> baseType  // not a unit, ignore
            | [TyUnitExpr ue] ->
                // COMPOUND unit annotation (`Float<meter/second^2>`,
                // `Float<second^-1>`, `Float<1>`): structural composition
                // through the same resolver Unit-declaration RHSs use.
                // Lowering stays total: a terminal-quantity misuse (BL3011,
                // surfaced by unitAnnoError at the annotation
                // consumers) or an unknown name degrades to the bare base,
                // exactly like an unknown unit name in the arm above.
                (match resolveUnitExpr env.Units ue with
                 | Ok sig' -> IRTUnitAnnotated (baseType, sig')
                 | Error _ -> baseType)
            | [TyVar (argName, Some n)] when Map.containsKey argName env.Units ->
                // `Float<meter^2>`: the positive-exponent power spelling
                // parses as a rank-marked type VARIABLE (grammar collision
                // with `T^2`); the name being a registered unit disambiguates
                // -- the same units-first policy as the bare-name arm.
                // Terminal quantities reject via the consumer check; resolver
                // failure degrades to the bare base.
                (match resolveUnitExpr env.Units (UnitPow (UnitNamed argName, n)) with
                 | Ok sig' -> IRTUnitAnnotated (baseType, sig')
                 | Error _ -> baseType)
            | _ -> baseType
        let tryResolveUnitArg baseType args = tryResolveTagArg baseType baseType args
        match name with
        | "Int" | "Int32" -> tryResolveUnitArg (IRTScalar ETInt32) args
        | "Int64" -> tryResolveUnitArg (IRTScalar ETInt64) args
        | "Float" | "Float64" | "Double" -> tryResolveUnitArg (IRTScalar ETFloat64) args
        | "Float32" -> tryResolveUnitArg (IRTScalar ETFloat32) args
        | "Complex64" -> tryResolveUnitArg (IRTScalar ETComplex64) args
        | "Complex128" -> tryResolveUnitArg (IRTScalar ETComplex128) args
        // Bool/String route through tryResolveUnitArg like the numeric bases
        // (previously they silently DROPPED type args), so `Bool<flag>` and
        // `String<title>` carry their (typically dimensionless-quantity) tag.
        | "Bool" -> tryResolveUnitArg (IRTScalar ETBool) args
        | "Void" -> IRTUnit
        // Nat resolves a unit arg like the other numeric bases so
        // `Nat<angular_momentum>` carries its tag instead of silently
        // dropping it (non-unit args keep returning bare Nat, as before).
        | "Nat" -> tryResolveTagArg (IRTNat None) (IRTScalar ETInt64) args
        | "String" -> tryResolveUnitArg (IRTScalar ETString) args
        | "Char" -> IRTScalar ETInt32
        | "Poly" ->
            // Each Poly occurrence gets its own fresh arity variable name --
            // packs are independent at the call site, so the rep shouldn't
            // claim they share one variable. Used by ppIRType for
            // diagnostics; doesn't drive per-slot specialization (that's
            // keyed by parameter position in the IR phase).
            let arityName = sprintf "r%d" (env.Builder.FreshId())
            match args with
            | [inner] -> IRTPoly (lowerTypeExpr env inner, arityName)
            | _ -> IRTPoly (IRTScalar ETFloat64, arityName)
        | _ when args.IsEmpty && Map.containsKey name env.Units ->
            // BARE unit/quantity name in type position (`10 : levels`,
            // `4.0 : speed`, param `x: speed`). Checked BEFORE the named-type
            // fallback (previously this fell through to IRTNamed and failed as
            // an unknown type). The inner scalar is a fresh inference var: the
            // checked expression's bare type flows in bidirectionally (the
            // permissive asymmetric unify arm binds it), so the ascription
            // adopts the value's scalar while stamping the signature.
            IRTUnitAnnotated (env.Subst.Fresh(), Map.find name env.Units)
        | _ ->
            match lookupTypeDef name env with
            | Some (TDIAlias resolvedTy) -> resolvedTy
            | Some (TDIStruct (n, _, _, _)) -> IRTNamed n
            | Some (TDIVariant (n, _, _)) -> IRTNamed n
            | Some (TDIIndexType _) ->
                // Aliased index type in value position (function param,
                // struct field, let-binding annotation, etc.). Under Option C
                // this lowers to IRTIdxTagged (int64, IRefNamed name) -- an
                // int64 tagged by the index type's nominal name. The codegen
                // emits `using <name> = int64_t;` so the C++ type carries
                // the alias for documentation; runtime backing is still int.
                IRTIdxTagged (IRTScalar ETInt64, IRefNamed name)
            | Some (TDIEnumIdx (_, _, values, _)) ->
                // Same shape, but the underlying type follows the values
                // list (all-string -> ETString, else ETInt64). The C++
                // typedef emitted alongside resolves the alias to the
                // matching primitive.
                let underlying = EnumValue.underlyingElemType values
                IRTIdxTagged (IRTScalar underlying, IRefNamed name)
            | None ->
                // If this name is in the type variable scope (introduced by T^k
                // elsewhere in this declaration), bare T means T^0 (scalar).
                if args.IsEmpty && env.Subst.IsTypeVar(name) then
                    env.Subst.LookupOrCreateTypeVar(name, 0, env.Builder)
                else
                    IRTNamed name  // Forward reference or external type

    | TyArray (elemTy, indexTys) ->
        let elem = lowerElemType env elemTy
        // RaggedIdx requires at least one prior index in the index list to
        // iterate over. A 1-D Array<T like RaggedIdx<lens>> is malformed:
        // there's no prior position to provide the iteration that drives the
        // lengths-array lookup. The check is structural -- first index can't
        // be a closed RaggedIdx.
        //
        // The opaque variant `RaggedIdx<_>` is exempted: it's specifically
        // designed for kernel-parameter types (`g: Array<T like RaggedIdx<_>>`)
        // representing a sub-array peeled from a parent ragged. There is no
        // lengths array to look up; the extent is supplied by the loop
        // binding's ExtentArrayRef at the peel point.
        let firstIsRagged =
            match indexTys with
            | TyRaggedIdx _ :: _ -> true
            | _ -> false
        if firstIsRagged then
            // Produce a degenerate IRTArray; the actual error reporting site
            // is the typechecker proper, not lowering. The placeholder lets
            // downstream lowering proceed to surface a clearer diagnostic
            // when the type appears in a function signature or let binding.
            // Emit a Tag that downstream phases can detect for error reporting.
            let placeholderIdx = {
                Id = env.Builder.FreshId(); Rank = 1
                Extent = IRParam ("__error_ragged_no_prior", 0, IRTNat None)
                Symmetry = SymNone
                Tag = Some "__error_ragged_no_prior"; IxKind = IxKErrorRaggedNoPrior
                Kind = SDimension; Dependencies = []
            }
            mkArrayArrow [placeholderIdx] elem None
        else
            // Index types are normally one IRIndexType per surface index, but
            // dependent forms like DepIdx produce TWO records (outer + inner with
            // Dependencies linking them). lowerIndexTypeList handles the expansion.
            let indices = indexTys |> List.mapi (fun i ity -> lowerIndexTypeList env i ity) |> List.concat
            // Nested-array normalization: `Array<Array<T like Idx<n>> like
            // Idx<m>>` flattens to `Array<T like Idx<m>, Idx<n>>` so all
            // downstream rank-N machinery keys off IndexTypes count, not
            // nesting depth. Without it, arrayRank=1 (outer only) but a
            // literal's computeArrayDims recurses to depth 2, malforming
            // extents[1]={n,m} and breaking allocate<>. Limited to explicit
            // `TyArray (TyArray, _)` syntax; other IRTArray producers don't
            // compose nested arrays. Inner Identity/IsVirtual reset on flatten.
            match elem with
            | ArrayElem inner ->
                mkArrayArrow (indices @ inner.IndexTypes) inner.ElemType None
            | _ ->
                mkArrayArrow indices elem None

    | TyDist (orderExpr, elemTy, axesTys) ->
        // Typed dist tower: Dist<order, Elem like I1, ..., Ik>.
        // The order must be a compile-time integer >= 1 -- a literal, a
        // `let static`, or a static-function call (the replicate-count
        // contract; same two-tier resolution as inferReplicate: cheap
        // evalConstExpr first, then the full StaticEval against the
        // checkModule pre-pass's StaticValues/StaticFunctions). Failure
        // lowers to the -1 SENTINEL, reported at the annotation-consumption
        // sites (inferLetBindingValue / checkFunctionDecl) alongside the
        // ragged no-prior check -- lowerTypeExpr itself has no error channel.
        let order = evalStaticIntExpr env orderExpr
        let elem = lowerElemType env elemTy
        let axes = axesTys |> List.mapi (fun i ity -> lowerIndexTypeList env i ity) |> List.concat
        match order with
        | Some n when n >= 1 -> IRTDist (n, elem, axes)
        | _ -> IRTDist (-1, elem, axes)

    | TyAbstractArray (elemTy, rankExpr, _symmOpt) ->
        // `T<u>^k` -- a UNIT-CARRYING type variable (array-expression plan
        // bug #8). The trailing caret is what marks the head as a variable:
        // without it `T<x>` keeps its ordinary named-type reading, and a head
        // that names a real type (`Float<day>^1`, or any declared struct /
        // alias) is concrete and lowers through the ordinary element path
        // below. The element becomes IRTUnitAnnotated over the SCALAR type
        // variable -- exactly the shape a concrete `Float<u>` element
        // produces -- so every unit walk (IR.getUnits and the arithmetic /
        // kernel rules built on it) reads it identically, and a caller's
        // `Array<Float<u'> like I>` meets it element-to-element in `unify`,
        // where the unit-compatibility check already lives. Nothing here is
        // a new unit mechanism; it is the existing one, reached from an
        // abstract signature.
        //
        // Name resolution follows the `Float<u>` slot exactly (the three
        // spellings tryResolveTagArg admits): a bare unit name, a compound
        // unit expression, and the `u^n` power spelling that parses as a
        // rank-marked type var. An unresolvable name yields None here and is
        // REPORTED by unitAnnoError's TyAbstractArray arm (BL3015), which
        // knows this slot cannot hold anything but a unit. The resolver itself
        // lives at `unitOfTypeVarArgs` above, shared with the CARET-FREE
        // spelling so the two cannot classify a head differently.
        let unitOfArgs (args: TypeExpr list) : UnitSig option = unitOfTypeVarArgs env args
        // (variable name, element type) when the head is a unit-carrying type
        // variable. A bare `T` (TyVar) is NOT one: it keeps the existing
        // whole-array-is-the-variable reading, arity constraint and all.
        let unitVarElem =
            match elemTy with
            | TyNamed (vname, args) when not args.IsEmpty
                                         && not (isConcreteTypeBaseName vname)
                                         && (lookupTypeDef vname env).IsNone ->
                unitOfArgs args
                |> Option.map (fun u ->
                    (vname,
                     IRTUnitAnnotated (env.Subst.LookupOrCreateTypeVar (vname, 0, env.Builder), u)))
            | _ -> None
        match evalConstExpr env rankExpr with
        | Some rank ->
            let r = int rank
            let elemVarName =
                match elemTy with
                | TyVar (n, _) -> Some n
                | _ -> None
            match elemVarName, unitVarElem with
            | Some name, _ ->
                // Type variable with arity: route through type var scope
                env.Subst.LookupOrCreateTypeVar(name, r, env.Builder)
            | None, Some (_, unitElem) when r = 0 ->
                // `T<u>^0`: the scalar type variable, unit stamped on.
                unitElem
            | _ ->
                if r = 0 then
                    lowerTypeExpr env elemTy  // Rank-0: just the scalar
                else
                    let elem =
                        match unitVarElem with
                        | Some (_, unitElem) -> unitElem
                        | None -> lowerElemType env elemTy
                    // Every abstract axis is genuinely UNKNOWN, so it takes
                    // `lowerExtentExpr`'s own unknown-extent spelling rather
                    // than a made-up name like `n`: two abstract parameters
                    // of the same rank are independently shaped (`t:
                    // T<time>^1, w: T<freq>^1` are different lengths), and a
                    // real name would read as the `Idx<n>, Idx<n>` tie and
                    // print as a variable the user never wrote. Nothing
                    // consumes it: extents are never compared in `unify`, and
                    // codegen bakes only LITERAL extents, falling back to the
                    // runtime `.extents[dim]` read for everything else.
                    let indices = [0 .. r - 1] |> List.map (fun _ ->
                        { Id = env.Builder.FreshId(); Rank = 1
                          Extent = IRParam ("?", 0, IRTNat None)
                          Symmetry = SymNone; Tag = None; IxKind = IxKPlain; Kind = SDimension; Dependencies = [] })
                    mkArrayArrow indices elem None
        | None ->
            // Non-constant rank (e.g., T^r where r is a variable)
            match elemTy, unitVarElem with
            | TyVar (name, _), _ ->
                // Can't resolve arity statically -- create unconstrained type var
                env.Subst.LookupOrCreateTypeVar(name)
            | _, Some (vname, IRTUnitAnnotated (_, u)) ->
                // `T<u>^r`: rank unknown, so no array shape can be built.
                // The unit still rides the unconstrained variable, the same
                // shape a bare quantity annotation produces.
                IRTUnitAnnotated (env.Subst.LookupOrCreateTypeVar(vname), u)
            | _ ->
                lowerElemType env elemTy  // Arity-polymorphic fallback

    | TyFunc (args, ret) ->
        mkFuncArrow (args |> List.map (lowerTypeExpr env)) (lowerTypeExpr env ret)

    | TyTuple tys -> IRTTuple (tys |> List.map (lowerTypeExpr env))

    // `Tuple<N>` (docs/plan-tuples-vs-arg-packs.md 6c): width written,
    // element types inferred. Lowers to the SAME `IRTTuple` a written
    // `(T1, ..., TN)` produces, with N fresh inference variables in the
    // element slots -- so unify's equal-length `IRTTuple` rule
    // (Unify.fs:710) supplies both the width check (a width mismatch is an
    // ordinary TypeMismatch, no new diagnostic) and the element inference.
    // The parser guarantees n >= 2.
    | TyTupleWidth n -> IRTTuple (List.init n (fun _ -> env.Subst.Fresh()))

    | TyVar (name, arityOpt) ->
        // Type variable with optional arity annotation.
        // T or T^0 = scalar type variable. T^k (k>0) = rank-k array type variable.
        let arity = arityOpt |> Option.defaultValue 0
        env.Subst.LookupOrCreateTypeVar(name, arity, env.Builder)

    // Index types as standalone type expressions denote VALUE TYPES, NOT
    // array types: an anonymous Idx<n> in value position is an int in [0,n)
    // (a loop bound at codegen), not a Float-array. Lowers to IRTIdxTagged
    // (int64, IRefAnon) -- parallel to Float<meters> -> IRTUnitAnnotated --
    // so inferArithType rejects arithmetic on it. Named aliases route
    // through TyNamed above to the same shape with IRefNamed.
    //
    // KNOWN GAP: higher-arity/dependent cases below (TySymIdx, TyAntisymIdx,
    // TyHermitianIdx, TyCompoundIdx, TyDepIdx/TyRaggedIdx/TyRaggedIdxOpaque,
    // TyEquivIdx) preserve the IRTArray-with-Float64 shape -- what a
    // SymIdx<r, n> value-level type should mean isn't decided. No test
    // exercises these paths; the wrong shape is a dead-code latent bug.
    | TyIdx extent ->
        // Value-position TyIdx lowers to IRTIdxTagged wrapping int64.
        // Per Option C: index values are int64 tagged with a nominal
        // IdxRef. The fresh nominalId is the identity; the extent is
        // preserved on the IdxRef solely for diagnostics / pretty-print.
        let nominalId = env.Builder.FreshId()
        IRTIdxTagged (IRTScalar ETInt64,
                      IRefAnon (nominalId, lowerExtentExpr env extent))

    // Both arms keep the documented KNOWN-GAP shape above (IRTArray-with-
    // Float64), but build the index record through the SHARED
    // `symPowerIndexRecord` so the value-position and index-position twins
    // cannot drift. For a `SymIdx<r, IrrepsIdx<s>>` base it carries the spec
    // identity (see the helper's doc comment).
    | TySymIdx (rank, baseIdx) ->
        let idx = symPowerIndexRecord env (env.Builder.FreshId()) rank SymSymmetric baseIdx
        mkArrayArrow [idx] (IRTScalar ETFloat64) None

    | TyAntisymIdx (rank, baseIdx) ->
        let idx = symPowerIndexRecord env (env.Builder.FreshId()) rank SymAntisymmetric baseIdx
        mkArrayArrow [idx] (IRTScalar ETFloat64) None

    // Same KNOWN-GAP shape as the two arms above, through the same shared
    // record builder -- so the value-position and index-position twins of an
    // OrbIdx cannot drift, exactly as symPowerIndexRecord guarantees for its
    // own pair.
    | TyOrbIdx (levels, baseIdx) ->
        let idx = orbitIndexRecord env (env.Builder.FreshId()) levels baseIdx
        mkArrayArrow [idx] (IRTScalar ETFloat64) None

    | TyHermitianIdx extent ->
        let ext = lowerExtentExpr env extent
        let idx = { Id = env.Builder.FreshId(); Rank = 2; Extent = ext
                    Symmetry = SymHermitian; Tag = None; IxKind = IxKPlain; Kind = SDimension; Dependencies = [] }
        mkArrayArrow [idx] (IRTScalar ETFloat64) None

    | TyBoundedIdx _ -> IRTScalar ETInt64

    | TyCompoundIdx _mask ->
        let idx = { Id = env.Builder.FreshId(); Rank = 1; Extent = IRParam ("compound", 0, IRTNat None)
                    Symmetry = SymNone; Tag = None; IxKind = IxKPlain; Kind = SDimension; Dependencies = [] }
        mkArrayArrow [idx] (IRTScalar ETFloat64) None

    | TyEnumIdx valuesExpr ->
        // Determine underlying element type from values. All-string lowers to
        // ETString; otherwise (all-int, empty, or mixed-but-recoverable) ETInt64.
        // Mixed lists won't reach here in practice -- the same extraction is
        // run by registerTypeDecl and surfaces a clean error when aliased; an
        // unaliased mixed-list slips through silently with ETInt64.
        let isAllString =
            match valuesExpr.Kind with
            | ExprKind.ExprArrayLit elems when not elems.IsEmpty ->
                elems |> List.forall (fun el -> match el.Kind with ExprKind.ExprLit (LitString _) -> true | _ -> false)
            | _ -> false
        if isAllString then IRTScalar ETString else IRTScalar ETInt64

    | TyDepIdx _ | TyRaggedIdx _ | TyRaggedIdxOpaque | TyIrrepsIdx _ | TyPgIrrepsIdx _ | TySparseIdx _ ->
        // DepIdx/RaggedIdx/IrrepsIdx/PgIrrepsIdx/SparseIdx in non-index position --
        // the pg member takes PARITY with the O(3) one here: same known gap,
        // same treatment. Defensive fallback matching TyCompoundIdx/TyEquivIdx:
        // wrap in a single-index Array so the IR shape is consistent. Real
        // iteration happens via lowerIndexType, which produces the correctly
        // tagged IRIndexType.
        let idx = lowerIndexType env 0 ty
        mkArrayArrow [idx] (IRTScalar ETFloat64) None

    | TyHalo _ ->
        // halo<Inner, [offs]> is legal only as a range<> slot (handled in the
        // ExprRange arm via haloSlotOf). In any other type position there is
        // no value meaning; degrade to the iteration value (int64) -- the slot
        // path never routes here.
        IRTScalar ETInt64

    | TyPoly inner ->
        // Fresh arity-variable name per Poly occurrence. See the TyNamed "Poly"
        // case above for rationale: packs are independent, so each Poly param
        // gets its own identifier in the type rep.
        let arityName = sprintf "r%d" (env.Builder.FreshId())
        IRTPoly (lowerTypeExpr env inner, arityName)
    | TyUnitExpr ue ->
        // A compound unit expression standing ALONE in type position. The
        // parser only produces TyUnitExpr inside a type-argument list, where
        // the enclosing TyNamed arm consumes it via tryResolveTagArg -- but
        // the node is a TypeExpr, so lower it totally: annotate a fresh
        // inferred scalar, exactly like a bare unit name in type position
        // (the checked expression's bare type flows in bidirectionally).
        (match resolveUnitExpr env.Units ue with
         | Ok sig' -> IRTUnitAnnotated (env.Subst.Fresh(), sig')
         | Error _ -> env.Subst.Fresh())
    | TyConstrained (inner, _) -> lowerTypeExpr env inner
    | TyEquivIdx (_dim, _group, _rep) ->
        let idx = { Id = env.Builder.FreshId(); Rank = 1; Extent = IRParam ("equiv", 0, IRTNat None)
                    Symmetry = SymNone; Tag = None; IxKind = IxKPlain; Kind = SDimension; Dependencies = [] }
        mkArrayArrow [idx] (IRTScalar ETFloat64) None

and lowerElemType env ty : IRType =
    // Primitives become IRTScalar et; named index types in element position
    // (foreign-key syntax) become IRTIdxTagged + IRefNamed; user-defined
    // types pass through as IRTNamed; nested arrays pass through as
    // IRTArray; etc.
    //
    // Struct-element and nested-array types propagate as their actual
    // IRType rather than collapsing to ETFloat64 (codegen support for
    // non-primitive element types is still incomplete). Named-index cases
    // produce IRTIdxTagged wrapping the underlying primitive, unifying
    // element-position and value-position encodings.
    match ty with
    | TyNamed (name, []) ->
        match lookupTypeDef name env with
        | Some (TDIIndexType _) ->
            IRTIdxTagged (IRTScalar ETInt64, IRefNamed name)
        | Some (TDIEnumIdx (_, _, values, _)) ->
            let underlying = EnumValue.underlyingElemType values
            IRTIdxTagged (IRTScalar underlying, IRefNamed name)
        | _ -> lowerTypeExpr env ty   // struct, sum, alias, type variable, etc.
    | TyIdx _ | TySymIdx _ | TyAntisymIdx _ | TyOrbIdx _ | TyHermitianIdx _ ->
        // Raw index type syntax in element position (e.g., Array<Idx<3> like ...>).
        // Anonymous-tag preservation here is deferred: collapses to bare
        // int64, losing index identity (named tags are preserved elsewhere).
        IRTScalar ETInt64
    | TyEnumIdx valuesExpr ->
        // Mirror of the value-position handling: all-string values -> ETString,
        // otherwise -> ETInt64. Same anonymous-tag-loss caveat as above.
        let isAllString =
            match valuesExpr.Kind with
            | ExprKind.ExprArrayLit elems when not elems.IsEmpty ->
                elems |> List.forall (fun el -> match el.Kind with ExprKind.ExprLit (LitString _) -> true | _ -> false)
            | _ -> false
        if isAllString then IRTScalar ETString else IRTScalar ETInt64
    | _ ->
        lowerTypeExpr env ty

/// The index record for `SymIdx<k, base>` / `AntisymIdx<k, base>`, shared by
/// index-position and value-position lowering.
///
///   - `SymBaseExtent e`: an anonymous rank-k compact record over extent `e`.
///   - `SymBaseIndex ty` (e.g. `SymIdx<k, IrrepsIdx<s>>`): the base index
///     type is lowered first, then re-stamped with Rank/Symmetry; Extent,
///     Tag (mkIrrepsTag), IxKind, Kind, Dependencies (incl. bad-spec ERROR
///     marker) ride through verbatim, so a malformed spec still surfaces at
///     irTypeBadIrrepsDetail.
///
/// Re-stamping (not rebuilding) makes this field-for-field what
/// `deduceOutputType` produces for an INFERRED symmetric group over
/// irreps-typed inputs -- a written annotation and an inferred type are the
/// same type by construction. `IxSymmetryLike` dispatches on Symmetry before
/// IxKind, so this is compact-simplex over total_dim(spec) cells like
/// `SymIdx<k, total_dim>`.
and symPowerIndexRecord env (id: IRId) (rank: int) (symmetry: SymmetryClass)
                          (baseIdx: SymIdxBase) : IRIndexType =
    match baseIdx with
    | SymBaseExtent extent ->
        { Id = id; Rank = rank; Extent = lowerExtentExpr env extent
          Symmetry = symmetry; Tag = None; IxKind = IxKPlain
          Kind = SDimension; Dependencies = [] }
    | SymBaseIndex baseTy ->
        let baseRec = lowerIndexType env 0 baseTy
        { baseRec with Id = id; Rank = rank; Symmetry = symmetry }

/// The index record for `OrbIdx<[(r1,s1), ..., (rd,sd)], base>`, the twin of
/// `symPowerIndexRecord`, shared by index-position and value-position
/// lowering. Normalized first via `IR.orbitNormalForm` (shared with the
/// deduction producer `IR.deduceWreathTie`, so written and deduced classes
/// never disagree): a rank-1 level is the trivial group S_1, so `(1,-)`
/// zeroes nothing and `(1,+)` ties nothing, keeping depth logarithmic.
///
///   []        trivial -- the PLAIN `Idx<n>` record, field for field.
///   [(r,+)]   the `SymIdx<r, base>` record via symPowerIndexRecord (not
///             rebuilt): depth-1 rides the existing compact machinery.
///   [(r,-)]   likewise `AntisymIdx<r, base>`.
///   depth >=2 SymWreath: Rank = product of level ranks, levels carried in
///             Extent as IROrbitClass, IxKOrbit + "__orbidx" sentinel Tag.
///
/// DEFERRED: a BLOCK-SPEC base under depth >= 2
/// (`OrbIdx<[(2,+),(2,+)], IrrepsIdx<s>>`) parses, but the wreath Tag is
/// "__orbidx", so spec identity is lost -- only the base EXTENT survives.
/// Depth <= 1 keeps the irreps tag (goes through symPowerIndexRecord).
and orbitIndexRecord env (id: IRId) (levels: (int * bool) list)
                     (baseIdx: SymIdxBase) : IRIndexType =
    match orbitNormalForm levels with
    | OrbNfTrivial ->
        (match baseIdx with
         | SymBaseExtent extent ->
             { Id = id; Rank = 1; Extent = lowerExtentExpr env extent
               Symmetry = SymNone; Tag = None; IxKind = IxKPlain
               Kind = SDimension; Dependencies = [] }
         | SymBaseIndex baseTy ->
             // The base index type verbatim (rank 1, no symmetry) -- its own
             // identity, which is what `OrbIdx<[], Idx<n>>` should mean.
             { lowerIndexType env 0 baseTy with Id = id; Rank = 1; Symmetry = SymNone })
    | OrbNfDepth1 (r, isPlus) ->
        symPowerIndexRecord env id r (if isPlus then SymSymmetric else SymAntisymmetric) baseIdx
    | OrbNfWreath normalized ->
        // Rank is the RAW AXIS COUNT (the product of the level ranks), bounded
        // before it lands in the record's int field -- see mkWreathIndexRecord,
        // which both this producer and DEDUCTION share so the two cannot build
        // differently-shaped records for the same class.
        let baseExtent =
            match baseIdx with
            | SymBaseExtent extent -> lowerExtentExpr env extent
            | SymBaseIndex baseTy -> (lowerIndexType env 0 baseTy).Extent
        mkWreathIndexRecord id normalized baseExtent

/// Resolve a SparseIdx keys expression to its (source, rank). Shared by the
/// SparseIdx<keys> TYPE form (lowerIndexType, which failwiths on Error) and
/// the sparse(values, keys) BUILDER (which surfaces Error as a type error).
///
///   STATIC:  the keys expression folds under the static contract (a `let
///            static` tuple list). Entries are validated (uniform arity,
///            non-negative Nat components, no duplicates) and BAKED
///            (SkStatic) -- codegen emits the table as literals and no
///            runtime array is consulted, so a mutated source cannot desync
///            the index.
///   RUNTIME: a named variable of rank-1 tuple-element array type
///            (SkRuntime), mirroring the compound mask's deferred build.
and resolveSparseKeysSource (env: TypeEnv) (keysExpr: Expr) : Result<SparseKeysSource * int, string> =
    match evalStaticValueExpr env keysExpr with
    | Ok sv ->
        let decodeEntry (e: StaticEval.StaticValue) : Result<int64 list, string> =
            match e with
            | StaticEval.SVTuple comps ->
                comps |> List.fold (fun acc c ->
                    acc |> Result.bind (fun vs ->
                        match c with
                        | StaticEval.SVInt v when v >= 0L -> Ok (vs @ [v])
                        | StaticEval.SVInt v -> Error (sprintf "SparseIdx: key coordinates must be non-negative; got %d" v)
                        | other -> Error (sprintf "SparseIdx: key tuple components must be Nat literals; got %A" other))) (Ok [])
            | StaticEval.SVInt v when v >= 0L -> Ok [v]   // rank-1: bare Nat keys
            | StaticEval.SVInt v -> Error (sprintf "SparseIdx: key coordinates must be non-negative; got %d" v)
            | other -> Error (sprintf "SparseIdx: keys must be a static list of Nat tuples; got element %A" other)
        (match sv with
         | StaticEval.SVTuple elems when not elems.IsEmpty ->
             elems |> List.fold (fun acc e ->
                 acc |> Result.bind (fun es -> decodeEntry e |> Result.map (fun d -> es @ [d]))) (Ok [])
         | other -> Error (sprintf "SparseIdx: keys must be a non-empty static list of Nat tuples; got %A" other))
        |> Result.bind (fun entries ->
            let arity = entries.Head.Length
            if entries |> List.exists (fun e -> e.Length <> arity) then
                Error (sprintf "SparseIdx: all key tuples must have the same arity; first is %d-ary" arity)
            elif (entries |> List.distinct |> List.length) <> entries.Length then
                Error "SparseIdx: duplicate key tuple in static key list"
            else Ok (SkStatic entries, arity))
    | Error _ ->
        // Runtime branch: named keys variable, rank-1 array of Nat tuples
        // (or of bare Nats for a rank-1 sparse index).
        (match keysExpr.Kind with
         | ExprKind.ExprVar name ->
             (match lookupVar name env with
              | Some vi ->
                  (match vi.Type with
                   | ArrayElem arr when (arr.IndexTypes |> List.sumBy (fun ix -> ix.Rank)) = 1 ->
                       (match arr.ElemType with
                        | IRTTuple ts when ts.Length >= 2 ->
                            let natLike t =
                                match t with
                                | IRTNat _ | IRTScalar ETInt64 | IRTScalar ETInt32 -> true
                                | IRTIdxTagged (IRTScalar (ETInt64 | ETInt32), _) -> true
                                | _ -> false
                            if ts |> List.forall natLike then Ok (SkRuntime (IRVar (vi.VarId, vi.Type)), ts.Length)
                            else Error (sprintf "SparseIdx<%s>: key tuple components must be Nat-valued; '%s' has element type %A" name name arr.ElemType)
                        | IRTNat _ | IRTScalar ETInt64 | IRTScalar ETInt32 ->
                            Ok (SkRuntime (IRVar (vi.VarId, vi.Type)), 1)
                        | other ->
                            Error (sprintf "SparseIdx<%s>: keys must be a rank-1 array of Nat tuples (Array<(Nat, ...) like ...>); '%s' has element type %A" name name other))
                   | ArrayElem _ ->
                       Error (sprintf "SparseIdx<%s>: keys array must be rank 1 (one key tuple per entry)" name)
                   | other ->
                       Error (sprintf "SparseIdx<%s>: keys must be an array (Array<(Nat, ...) like ...>); '%s' has type %A" name name other))
              | None -> Ok (SkRuntime (lowerExtentExpr env keysExpr), 1))
         | _ -> Ok (SkRuntime (lowerExtentExpr env keysExpr), 1))

and lowerIndexType env (_position: int) (ty: TypeExpr) : IRIndexType =
    let id = env.Builder.FreshId()
    match ty with
    | TyIdx extent ->
        { Id = id; Rank = 1; Extent = lowerExtentExpr env extent
          Symmetry = SymNone; Tag = None; IxKind = IxKPlain; Kind = SDimension; Dependencies = [] }
    | TySymIdx (rank, baseIdx) -> symPowerIndexRecord env id rank SymSymmetric baseIdx
    | TyAntisymIdx (rank, baseIdx) -> symPowerIndexRecord env id rank SymAntisymmetric baseIdx
    | TyOrbIdx (levels, baseIdx) -> orbitIndexRecord env id levels baseIdx
    | TyHermitianIdx extent ->
        { Id = id; Rank = 2; Extent = lowerExtentExpr env extent
          Symmetry = SymHermitian; Tag = None; IxKind = IxKPlain; Kind = SDimension; Dependencies = [] }
    | TyEnumIdx valuesExpr ->
        let nValues =
            match valuesExpr.Kind with
            | ExprKind.ExprArrayLit elems -> int64 elems.Length
            | _ -> 0L
        { Id = id; Rank = 1; Extent = IRLit (IRLitInt nValues); Symmetry = SymNone
          Tag = None; IxKind = IxKPlain; Kind = SDimension; Dependencies = [] }
    | TyDepIdx (outerTy, _paramName, _bodyTy) ->
        // Single-slot context (e.g., type alias, range): return a placeholder
        // inner-only record. Two-record expansion happens at the array-index-
        // list construction site (lowerIndexTypeList). Single-slot use of
        // DepIdx is suspect -- code paths that need the full DepIdx structure
        // (iteration, etc.) should route through lowerIndexTypeList instead.
        let outerIdx = lowerIndexType env _position outerTy
        { Id = id; Rank = 2; Extent = IRParam ("__depidx_inner", 0, IRTNat None)
          Symmetry = SymNone; Tag = Some "__depidx"; IxKind = IxKDep
          Kind = SDimension; Dependencies = [outerIdx.Id] }
    | TyRaggedIdx lengthsExpr ->
        // Single-record shape matching lowerIndexTypeList's unaliased TyRaggedIdx
        // case, so aliasing (`type R = RaggedIdx<lens>` then `Array<... R>`)
        // gets the correct rank. The structural tag `__raggedidx` is preserved
        // so codegen predicates (isRaggedArrayType etc.) keep detecting the
        // ragged form through alias indirection.
        let lengthsIR = lowerExtentExpr env lengthsExpr
        { Id = id; Rank = 1; Extent = IRRaggedLookup lengthsIR
          Symmetry = SymNone; Tag = Some "__raggedidx"; IxKind = IxKRagged
          Kind = SDimension; Dependencies = [] }
    | TyRaggedIdxOpaque ->
        // Opaque-extent variant: rank-1, no lengths array, no outer position.
        // Used in kernel-parameter types where the extent is supplied by the
        // peel context. The Extent is a sentinel (IROpaqueExtent) rather than
        // a placeholder IRParam, so codegen can distinguish "extent unknown
        // because we haven't computed it yet" (IRParam) from "extent supplied
        // by surrounding loop binding" (IROpaqueExtent).
        { Id = id; Rank = 1; Extent = IROpaqueExtent
          Symmetry = SymNone; Tag = Some "__raggedidx_opaque"; IxKind = IxKRaggedOpaque
          Kind = SDimension; Dependencies = [] }
    | TyIrrepsIdx specExpr ->
        // IrrepsIdx<spec>: block-structured dense index over an irreps spec.
        // The spec resolves under the full static contract (like Dist's
        // order); extent = total_dim(spec) and EVERY cell is stored -- flat
        // dense, no compression -- so the record rides the ordinary dense
        // paths (SymNone). The block structure matters for IDENTITY, carried
        // in the Tag (mkIrrepsTag: spec equality = index-space identity;
        // Unify adds the spec-mismatch strictness arm). lowerIndexType has
        // no error channel, so a non-static/malformed spec lowers to the
        // marker record consumed by irTypeHasBadIrrepsSpec at let-binding /
        // function-signature sites (ragged-no-prior pattern), the failure
        // detail smuggled in the IRParam name.
        (match evalStaticValueExpr env specExpr
               |> Result.bind (Blade.ML.Statics.specOfStatic "IrrepsIdx") with
         | Ok spec ->
             let triples = spec |> List.map (fun e -> (e.L, e.Parity, e.Mult))
             { Id = id; Rank = 1
               Extent = IRLit (IRLitInt (int64 (Blade.ML.Spec.totalDim spec)))
               Symmetry = SymNone; Tag = Some (mkIrrepsTag None triples)
               IxKind = IxKIrreps; Kind = SDimension; Dependencies = [] }
         | Error detail ->
             // specOfStatic prefixes its own "IrrepsIdx: " (its `what`
             // label); the consumption-site diagnostic adds the same prefix,
             // so strip it here to avoid "IrrepsIdx: IrrepsIdx: ...".
             let detail =
                 if detail.StartsWith "IrrepsIdx: " then detail.Substring "IrrepsIdx: ".Length
                 else detail
             { Id = id; Rank = 1
               Extent = IRParam (detail, 0, IRTNat None)
               Symmetry = SymNone; Tag = Some "__error_irreps_bad_spec"
               IxKind = IxKErrorIrrepsBadSpec; Kind = SDimension; Dependencies = [] })
    | TyPgIrrepsIdx (groupName, specExpr) ->
        // PgIrrepsIdx<GROUP, spec>: the point-group block-spec member. Field
        // for field the shape of the TyIrrepsIdx arm above -- rank 1, extent
        // a folded literal, dense (SymNone) with the block structure carried
        // as IDENTITY in the Tag, and the same error-marker channel for a
        // non-static/malformed spec -- over a DIFFERENT frozen tag prefix and
        // a DIFFERENT decoder.
        //
        // TWO resolutions, in order, because the diagnostics differ: the GROUP
        // name against the frozen registry, then the spec's LABELS against
        // THAT group's character table. Getting the group wrong and getting a
        // label wrong are different mistakes and each names its own roster.
        (match Blade.ML.Statics.pgGroupByName "PgIrrepsIdx" groupName
               |> Result.bind (fun grp ->
                    evalStaticValueExpr env specExpr
                    |> Result.bind (Blade.ML.Statics.pgSpecOfStatic "PgIrrepsIdx" grp)
                    |> Result.map (fun spec -> (grp, spec))) with
         | Ok (grp, spec) ->
             { Id = id; Rank = 1
               Extent = IRLit (IRLitInt (int64 (Blade.ML.PointSpec.pgTotalDim grp spec)))
               Symmetry = SymNone; Tag = Some (mkPgIrrepsTag grp.Name None spec)
               IxKind = IxKPgIrreps; Kind = SDimension; Dependencies = [] }
         | Error detail ->
             // The decoders prefix their own "PgIrrepsIdx: " (their `what`
             // label); the consumption-site diagnostic adds the same prefix,
             // so strip it here to avoid "PgIrrepsIdx: PgIrrepsIdx: ...".
             let detail =
                 if detail.StartsWith "PgIrrepsIdx: " then detail.Substring "PgIrrepsIdx: ".Length
                 else detail
             { Id = id; Rank = 1
               Extent = IRParam (detail, 0, IRTNat None)
               Symmetry = SymNone; Tag = Some "__error_pgirreps_bad_spec"
               IxKind = IxKErrorPgIrrepsBadSpec; Kind = SDimension; Dependencies = [] })
    | TyNamed (name, _) ->
        match lookupTypeDef name env with
        | Some (TDIIndexType (_, idx, _)) -> { idx with Id = id }
        | Some (TDIEnumIdx (_, idx, _, _)) -> { idx with Id = id }
        | _ ->
            { Id = id; Rank = 1; Extent = IRParam (name, 0, IRTNat None); Symmetry = SymNone
              Tag = Some name; IxKind = ixKindOfTag (Some name); Kind = SDimension; Dependencies = [] }
    | TyCompoundIdx maskExpr ->
        // CompoundIdx<mask> -- masked product space (formalism 4.5). Rank = the
        // RANK of the mask array (its number of dimensions). The mask is a runtime
        // array value carried in IRCompoundMask for codegen; cardinality (popcount)
        // is computed at runtime by the emitted compound_index_t. Canonical surface
        // form is a named mask whose declared type yields the rank; other forms
        // fall back to a rank-1 degraded placeholder for now (no producer relies on
        // them yet). Nested matches are parenthesized to avoid outer-arm absorption.
        let maskIR, rank =
            match maskExpr.Kind with
            | ExprKind.ExprVar name ->
                (match lookupVar name env with
                 | Some vi ->
                     let rank =
                         (match vi.Type with
                          | ArrayElem arr ->
                              // Enforce: a CompoundIdx mask must be Array<bool like ...>.
                              // Construction (popcount + flatten to std::vector<bool>) is
                              // cheap only for a boolean mask, so a non-bool (or non-array)
                              // mask is a hard type error here rather than a silent
                              // downstream miscompile. (A span-attributed diagnostic would
                              // be nicer, but lowerIndexType has no error channel today.)
                              (match arr.ElemType with
                               | IRTScalar ETBool -> ()
                               | other ->
                                   failwithf "CompoundIdx<%s>: mask must have bool element type (Array<bool like ...>); '%s' has element type %A" name name other)
                              arr.IndexTypes |> List.sumBy (fun ix -> ix.Rank)
                          | other ->
                              failwithf "CompoundIdx<%s>: mask must be an array (Array<bool like ...>); '%s' has type %A" name name other)
                     IRVar (vi.VarId, vi.Type), rank
                 | None -> lowerExtentExpr env maskExpr, 1)
            | _ -> lowerExtentExpr env maskExpr, 1
        { Id = id; Rank = rank; Extent = IRCompoundMask maskIR
          Symmetry = SymNone; Tag = Some "__compoundidx"; IxKind = IxKCompound
          Kind = SDimension; Dependencies = [] }
    | TySparseIdx keysExpr ->
        // SparseIdx<keys> -- explicit valid-tuple enumeration (formalism 3.5).
        // Rank is IMPLICIT: the key tuple arity. Keys keep their given order;
        // lookup is by tuple hash (no grid, no per-axis extents). The
        // static/runtime branch split lives in resolveSparseKeysSource.
        // Validation failures are hard errors like the compound-mask arm's
        // (lowerIndexType has no error channel today).
        let source, rank =
            match resolveSparseKeysSource env keysExpr with
            | Ok sr -> sr
            | Error msg -> failwith msg
        { Id = id; Rank = rank; Extent = IRSparseKeys source
          Symmetry = SymNone; Tag = Some "__sparseidx"; IxKind = IxKSparse
          Kind = SDimension; Dependencies = [] }
    | _ ->
        { Id = id; Rank = 1; Extent = IRParam ("?", 0, IRTNat None); Symmetry = SymNone
          Tag = None; IxKind = IxKPlain; Kind = SDimension; Dependencies = [] }

/// Lower an index type to a list of IRIndexType records. Most types produce a
/// single-element list; dependent forms (DepIdx, RaggedIdx) produce two records
/// -- outer + inner with Dependencies linking them.
///
/// Used at array-index-list construction sites where a multi-record expansion
/// is meaningful. Single-slot contexts (range, type alias) use lowerIndexType
/// directly and get a placeholder for dependent forms.
and lowerIndexTypeList (env: TypeEnv) (position: int) (ty: TypeExpr) : IRIndexType list =
    match ty with
    | TyDepIdx (outerTy, paramName, bodyTy) ->
        // Lower outer first to get its Id; that Id is the dependency target
        // for the inner extent's reference to the lambda parameter.
        let outerIdx = lowerIndexType env position outerTy
        let outerWithTag = { outerIdx with Tag = Some "__depidx_outer"; IxKind = IxKDepOuter }
        let outerVarRef = IRVar (outerWithTag.Id, IRTScalar ETInt64)
        // Extract the inner extent expression. Two body shapes are recognized:
        //   - Lambda form: `lambda(i) -> Idx<expr>` parses to `TyIdx expr`.
        //     Substitute paramName with outerVarRef in expr.
        //   - Eta-reduced form: `DepIdx<O, f>` parses to a synthesized
        //     TyNamed(funcName, [TyNamed(paramName, [])]) body. Inline by
        //     looking up f in StaticFunctions, taking its body Expr, and
        //     substituting f's param name (not paramName) with outerVarRef.
        // Anything else falls back to a runtime placeholder.
        let innerExtent =
            match bodyTy with
            | TyIdx e ->
                substituteAndLowerExtent env paramName outerVarRef e
            | TyNamed (funcName, [TyNamed (innerParam, [])]) when innerParam = paramName ->
                match Map.tryFind funcName env.StaticFunctions with
                | Some funcDecl when funcDecl.Params.Length = 1 ->
                    // Substitute the function's formal param with the outer
                    // iteration var. The function's body becomes the inner
                    // extent expression -- its structure is fixed at compile
                    // time, but it evaluates per-iteration as outer walks.
                    //
                    // Peel a trivial block wrapper so `function f(x) = { e }`
                    // (parses to ExprBlock([], Some e)) reduces the same as
                    // the inline form `function f(x) = e`.
                    let funcParamName = funcDecl.Params.[0].Name
                    let bodyExpr =
                        match funcDecl.Body.Kind with
                        | ExprKind.ExprBlock ([], Some e) -> e
                        | _ -> funcDecl.Body
                    substituteAndLowerExtent env funcParamName outerVarRef bodyExpr
                | _ ->
                    IRParam ("__depidx_inner", 0, IRTNat None)
            | _ ->
                IRParam ("__depidx_inner", 0, IRTNat None)
        let innerIdx = {
            Id = env.Builder.FreshId()
            Rank = 1
            Extent = innerExtent
            Symmetry = SymNone
            Tag = Some "__depidx_inner"; IxKind = IxKDepInner
            Kind = SDimension
            Dependencies = [outerWithTag.Id]
        }
        [outerWithTag; innerIdx]
    | TyRaggedIdx lengthsExpr ->
        // RaggedIdx contributes a SINGLE record. Its inner extent is a
        // per-iteration lookup into the lengths array, indexed by the
        // current outer iteration's flat position. The lengths array's
        // shape conceptually mirrors the prior index dimensions of the
        // enclosing array (e.g., for Idx<M>, Idx<N>, RaggedIdx<lens>,
        // lens is internally M*N elements); the codegen handles the
        // flat-position computation so the user-facing type stays clean.
        //
        // RaggedIdx is "open" -- it does NOT declare its own outer position;
        // it references the iteration over the prior index types in the
        // enclosing Array's index list. A 1-D `Array<T like RaggedIdx<lens>>`
        // is malformed (no prior index to iterate); RaggedIdx requires at
        // least one prior index. The malformedness check happens at the
        // TyArray level (see lowerTypeExpr), not here, since this function
        // doesn't see the surrounding context.
        let lengthsIR = lowerExtentExpr env lengthsExpr
        [{
            Id = env.Builder.FreshId()
            Rank = 1
            Extent = IRRaggedLookup lengthsIR
            Symmetry = SymNone
            Tag = Some "__raggedidx"; IxKind = IxKRagged
            Kind = SDimension
            Dependencies = []  // populated by the codegen iteration as needed
        }]
    | TyRaggedIdxOpaque ->
        // Opaque-extent variant -- used in kernel-parameter types where the
        // extent is supplied by the surrounding peel context, not declared
        // up front. Single-record like the closed form, but the Extent is
        // IROpaqueExtent (a marker, no payload) and the Tag distinguishes it
        // from the closed form for downstream codegen routing.
        [{
            Id = env.Builder.FreshId()
            Rank = 1
            Extent = IROpaqueExtent
            Symmetry = SymNone
            Tag = Some "__raggedidx_opaque"; IxKind = IxKRaggedOpaque
            Kind = SDimension
            Dependencies = []
        }]
    | TyNamed (n, _) ->
        // DepIdx aliases: recurse on the stored body so the multi-record
        // expansion (outer + inner, linked by Dependencies) runs at the use
        // site -- the catch-all below would return the single-record
        // placeholder registered at declaration time, structurally wrong for
        // DepIdx. Other aliases are structurally one record and skip
        // recursion deliberately: the alias name is baked into the stored
        // idx's Tag (nominative identity for ETIndexRef foreign keys), and
        // re-walking would lose it. Chained aliases (`type B = A` where
        // `type A = DepIdx<...>`) are not handled: registerTypeDecl routes
        // that to TDIAlias, not TDIIndexType.
        match lookupTypeDef n env with
        | Some (TDIIndexType (_, _, (TyDepIdx _ as body))) ->
            lowerIndexTypeList env position body
        | _ ->
            [lowerIndexType env position ty]
    | _ -> [lowerIndexType env position ty]

/// Decide the element type for a virtual array iterating over a given
/// index: the element values produced ARE positions in the indexed space,
/// so they carry that space's tag when it has a user-named identity
/// (anonymous/synthetic-tagged indices fall back to plain int64). This is
/// the iteration-tagging hook (section 4.18 indirect): `range<LatIdx> <@>
/// lambda(i) -> A(i)` typechecks under step 5's tag rule because i inherits
/// `Nat<LatIdx>` here, with no manual annotation needed.
let elemTypeForIterationIndex (idx: IRIndexType) : IRType =
    match idx.Tag with
    | Some name when name.StartsWith("__halowin|") ->
        // Halo window param: the kernel receives this as `w`, and w(o) neighbor
        // reads dispatch on the "__halowin|" tag (offsets + inner name encoded
        // in it). Carried as a tagged int index so it erases to int64 in C++.
        IRTIdxTagged (IRTScalar ETInt64, IRefNamed name)
    | Some name when not (name.StartsWith("__")) ->
        IRTIdxTagged (IRTScalar ETInt64, IRefNamed name)
    | _ ->
        IRTScalar ETInt64

// Co-iteration shape agreement (multi-axis co-iteration)

/// A plain dense rank-1 index record -- the kind that can share a co-iteration
/// product axis. Mirrors the isPlainDense predicate used by the fallback
/// operator's operand checks.
let isPlainDenseIx (ix: IRIndexType) : bool =
    ix.IxKind = IxKPlain && ix.Symmetry = SymNone && ix.Rank <= 1

/// Structural agreement of two index records for co-iteration purposes.
/// Compares shape-bearing fields only -- the nominal Id is EXCLUDED because
/// every occurrence of an index type gets a fresh Id; two Array<F64 like
/// Lat, Lon> annotations must agree.
let indexRecordsAgree (a: IRIndexType) (b: IRIndexType) : bool =
    a.Rank = b.Rank && a.Symmetry = b.Symmetry && a.IxKind = b.IxKind
    && a.Kind = b.Kind && a.Tag = b.Tag && a.Extent = b.Extent

/// Whole-shape agreement: same record count, records pairwise agree.
let indexShapesAgree (xs: IRIndexType list) (ys: IRIndexType list) : bool =
    xs.Length = ys.Length && List.forall2 indexRecordsAgree xs ys

/// A record list co-iteration can span: exactly one record (dense rank-1 OR
/// packed symmetric of any logical rank -- walked as flat canonical cells), or
/// several records ALL plain dense (the product space). Mixed dense+packed
/// multi-record shapes are rejected -- the packed record's triangular walk
/// cannot interleave a foreign dense axis.
let coIterableRecords (recs: IRIndexType list) : bool =
    recs.Length = 1 || recs |> List.forall isPlainDenseIx

/// The record shape of a `group_by` result: the group axis, then the ragged
/// member axis. Several of these CAN co-iterate -- the rows line up one-to-one
/// -- but only when they were grouped by the SAME keys, which is a fact about
/// the operand EXPRESSIONS, not their types (two `group_keys` calls produce
/// structurally identical records). So this recognises the shape only; the
/// same-keys obligation is discharged at the call site, which can see the
/// expressions (`sameGroupKeysBinding` in inferMethodFor).
let isGroupedRaggedShape (recs: IRIndexType list) : bool =
    match recs with
    | [outer; inner] -> outer.IxKind = IxKGroupOuter && inner.IxKind = IxKGroupMember
    | _ -> false

/// Shared iteration records for a zip co-iteration, from the operands' array
/// types. Single-record operands (dense rank-1, packed symmetric) use the
/// first record with no agreement check. Multi-record operands (dense rank
/// >= 2) span the FULL product of records and require structural agreement
/// + all-plain-dense records (mixed dense/packed multi-axis rejects), with
/// the grouped-ragged shape as the one non-dense exception.
let zipSharedRecords (arrayTypes: IRArrayType list) : Result<IRIndexType list, TypeError> =
    match arrayTypes with
    | [] -> Ok []
    | first :: rest ->
        let shape0 = first.IndexTypes
        let minRank = arrayTypes |> List.map (fun at -> at.IndexTypes.Length) |> List.min
        if shape0.Length <= 1 || minRank <= 1 then
            // Single-record rule (first array's first record).
            Ok (if minRank > 0 then [shape0.Head] else [])
        elif not (rest |> List.forall (fun at -> indexShapesAgree at.IndexTypes shape0)) then
            Error (Other "co-iteration over multi-axis arrays requires all operands to have identical index shapes (same records: tags, extents, symmetry)")
        elif isGroupedRaggedShape shape0 then
            // All operands grouped: the ragged member axis is NOT a product
            // axis, so this is not the plain-dense product rule -- the rows
            // co-iterate positionally, group g of each operand together, and
            // the kernel receives one row per operand. Legal only for
            // same-keys operands (checked by the caller).
            Ok shape0
        elif not (coIterableRecords shape0) then
            Error (Other "co-iteration spans one index record per operand (dense rank-1 or packed symmetric), or a product of plain-dense records; mixed dense/packed multi-axis shapes are not supported")
        else
            Ok shape0

/// halo<Inner, offsets> slot construction -- shared by the expression form
/// (`method_for(halo<..>)`) and the range<> slot form (`range<halo<..>, ..>`).
/// The offsets payload is either
///   - a FLAT int array `[-1, 0, 1]`  -> ONE slot (a 1-D halo), or
///   - an array of per-axis int arrays `[[-1,0,1],[0],[-1,0,1]]` -> k slots
///     over the SAME inner index (arity = sub-array count), the n-D product
///     window written as one halo.
/// Each slot is the inner index SHRUNK to its interior (BndShrink: every
/// declared neighbor of every iterated center is in-bounds, so window reads
/// need no guards) and tagged "__halowin|<d|c>:<innerName>|<o1,o2,..>". The
/// center's start offset (max(0, -min offsets union {0})) is re-derived from the
/// tag at loop-building time (IR mkElement via Types.haloStartOffsetOfTag) --
/// per-slot, which the single shared IRRange offset cannot express for
/// multi-slot ranges.
let haloSlotsOf (env: TypeEnv) (innerTy: TypeExpr) (offsetsExpr: Expr) : TypeResult<IRIndexType list> =
    let inner = lowerIndexType env 0 innerTy
    // One slot from one flat per-axis offset set.
    let slotOfInts (offsets: int list) : TypeResult<IRIndexType> =
        if List.isEmpty offsets then Error (Other "halo<...>: offsets array must be non-empty")
        elif inner.Rank <> 1 then
            Error (Other "halo<...>: the inner index must be rank-1 (n-D = per-axis offset arrays [[..],[..],..] or separate range<halo<..>, ..> slots)")
        else
        let offCsv = offsets |> List.map string |> String.concat ","
        match inner.Extent with
        | IRCompoundMask _ ->
            // Compound inner: ordinals walk the PRESENT cells, so "next"
            // is the next present cell (the hashed-index generalization).
            // The mask cardinality is runtime -- the interior shrink can't
            // fold into the extent here; it rides the tag and is applied
            // at the loop bound (buildLoopNestCodeGen StrictOffset).
            // IxKCompound is KEPT so the cidx materialization and
            // cardinality-bound machinery still engage.
            Ok { inner with
                    Id = env.Builder.FreshId()
                    Extent = inner.Extent
                    Tag = Some (sprintf "__halowin|c:|%s" offCsv)
                    IxKind = IxKCompound }
        | _ ->
            // Dense inner. Reach includes the implicit center 0: w(0) is
            // always readable even when 0 is not in the declared set
            // (e.g. lag sets [-12,-24]).
            let lo = min 0 (List.min offsets)
            let hi = max 0 (List.max offsets)
            let shrink = int64 (-lo + hi)
            let shrunkExtent =
                match inner.Extent with
                | IRLit (IRLitInt n) -> IRLit (IRLitInt (n - shrink))
                | e -> IRBinOp (IRElementwise, IRSub, e, IRLit (IRLitInt shrink))
            let innerName =
                match inner.Tag with
                | Some n when not (n.StartsWith("__")) -> n
                | _ -> ""
            Ok { inner with
                    Id = env.Builder.FreshId()
                    Extent = shrunkExtent
                    Tag = Some (sprintf "__halowin|d:%s|%s" innerName offCsv)
                    IxKind = IxKPlain }
    match evalStaticValueExpr env offsetsExpr with
    | Error msg -> Error (Other (sprintf "halo<...>: offsets must be a compile-time int array (%s)" msg))
    | Ok sv ->
        let asInt = function StaticEval.SVInt n -> Some (int n) | _ -> None
        match sv with
        | StaticEval.SVInt n -> slotOfInts [int n] |> Result.map List.singleton
        | StaticEval.SVTuple vs when not vs.IsEmpty ->
            let flat = vs |> List.map asInt
            if List.forall Option.isSome flat then
                // Flat form: one axis.
                slotOfInts (flat |> List.map Option.get) |> Result.map List.singleton
            else
                // Nested form: every entry must be a non-empty int array;
                // each becomes one slot over the same inner index.
                let perAxis =
                    vs |> List.map (function
                        | StaticEval.SVTuple xs ->
                            let os = xs |> List.map asInt
                            if List.forall Option.isSome os && not os.IsEmpty
                            then Some (os |> List.map Option.get) else None
                        | _ -> None)
                if List.forall Option.isSome perAxis then
                    // (local sequencer: TypeCheck's sequenceResults is defined
                    // further down the file, out of scope here)
                    perAxis
                    |> List.map (Option.get >> slotOfInts)
                    |> List.fold (fun acc r ->
                        match acc, r with
                        | Ok xs, Ok x -> Ok (xs @ [x])
                        | Error e, _ -> Error e
                        | _, Error e -> Error e) (Ok [])
                else
                    Error (Other "halo<...>: offsets must be a flat int array [-1,0,1] or an array of per-axis int arrays [[-1,0,1],[0],[-1,0,1]] (no mixing, no empty axes)")
        | _ -> Error (Other "halo<...>: offsets must be a compile-time array of integer literals, e.g. [-1, 0, 1]")

// 5. Capture Analysis

/// Collect free variables in an expression (names not bound in local scope).
let rec collectFreeVars (bound: Set<string>) (expr: Expr) : Set<string> =
    match expr.Kind with
    | ExprKind.ExprVar name ->
        if Set.contains name bound then Set.empty else Set.singleton name
    | ExprKind.ExprLit _ -> Set.empty
    | ExprKind.ExprBinOp (_, _, l, r) ->
        Set.union (collectFreeVars bound l) (collectFreeVars bound r)
    | ExprKind.ExprUnaryOp (_, e) -> collectFreeVars bound e
    | ExprKind.ExprApp (f, args) ->
        Set.unionMany (collectFreeVars bound f :: (args |> List.map (collectFreeVars bound)))
    | ExprKind.ExprLambda (parms, _, body) ->
        let bound' = parms |> List.fold (fun s p -> Set.add p.Name s) bound
        collectFreeVars bound' body
    | ExprKind.ExprLet (binding, body) ->
        let valFree = collectFreeVars bound binding.Value
        let names = patternNames binding.Pattern
        let bound' = names |> List.fold (fun s n -> Set.add n s) bound
        Set.union valFree (collectFreeVars bound' body)
    | ExprKind.ExprIf (c, t, e) ->
        Set.unionMany [collectFreeVars bound c; collectFreeVars bound t; collectFreeVars bound e]
    | ExprKind.ExprTuple es | ExprKind.ExprArrayLit es | ExprKind.ExprZip es | ExprKind.ExprStack es | ExprKind.ExprSequence es ->
        es |> List.map (collectFreeVars bound) |> Set.unionMany
    | ExprKind.ExprJoin (es, _) ->
        es |> List.map (collectFreeVars bound) |> Set.unionMany
    | ExprKind.ExprMatch (scr, cases) ->
        let scrFree = collectFreeVars bound scr
        let caseFree = cases |> List.map (fun c ->
            let names = patternNames c.Pattern
            let bound' = names |> List.fold (fun s n -> Set.add n s) bound
            let guardFree = c.Guard |> Option.map (collectFreeVars bound')
                            |> Option.defaultValue Set.empty
            Set.union guardFree (collectFreeVars bound' c.Body))
        Set.union scrFree (Set.unionMany caseFree)
    | ExprKind.ExprBlock (stmts, finalExpr) ->
        let mutable b = bound
        let mutable free = Set.empty
        for stmt in stmts do
            match unwrapStmt stmt with
            | StmtSpanned _ -> ()  // unreachable: unwrapStmt strips the annotation
            | StmtLet binding ->
                free <- Set.union free (collectFreeVars b binding.Value)
                b <- patternNames binding.Pattern |> List.fold (fun s n -> Set.add n s) b
            | StmtAssign (lhs, _, rhs) ->
                free <- Set.union free (Set.union (collectFreeVars b lhs) (collectFreeVars b rhs))
            | StmtExpr e ->
                free <- Set.union free (collectFreeVars b e)
            | StmtForIn (varName, rangeExpr, bodyStmts) ->
                free <- Set.union free (collectFreeVars b rangeExpr)
                // Recurse over the body with the loop variable bound; nested
                // for-in loops recurse through the same walker. NOTE: like
                // the outer StmtLet case, lets inside the body extend the
                // bound set for SUBSEQUENT statements.
                let rec walkForBody (bound: Set<string>) (stmts: Stmt list) =
                    let mutable bb = bound
                    for bodyStmt in stmts do
                        match unwrapStmt bodyStmt with
                        | StmtSpanned _ -> ()  // unreachable: unwrapStmt strips the annotation
                        | StmtLet binding ->
                            free <- Set.union free (collectFreeVars bb binding.Value)
                            bb <- patternNames binding.Pattern |> List.fold (fun s n -> Set.add n s) bb
                        | StmtAssign (lhs, _, rhs) ->
                            free <- Set.union free (Set.union (collectFreeVars bb lhs) (collectFreeVars bb rhs))
                        | StmtExpr e ->
                            free <- Set.union free (collectFreeVars bb e)
                        | StmtForIn (v2, range2, body2) ->
                            free <- Set.union free (collectFreeVars bb range2)
                            walkForBody (Set.add v2 bb) body2
                walkForBody (Set.add varName b) bodyStmts
        match finalExpr with
        | Some e -> Set.union free (collectFreeVars b e)
        | None -> free
    | ExprKind.ExprMethodFor arrays -> arrays |> List.map (collectFreeVars bound) |> Set.unionMany
    | ExprKind.ExprObjectFor kernel -> collectFreeVars bound kernel
    | ExprKind.ExprPure e | ExprKind.ExprCompute e | ExprKind.ExprRead e | ExprKind.ExprRank e -> collectFreeVars bound e
    | ExprKind.ExprGuard (c, b) -> Set.union (collectFreeVars bound c) (collectFreeVars bound b)
    | ExprKind.ExprMask (a, p) -> Set.union (collectFreeVars bound a) (collectFreeVars bound p)
    | ExprKind.ExprCompound (d, m) -> Set.union (collectFreeVars bound d) (collectFreeVars bound m)
    | ExprKind.ExprSparse (v, k) -> Set.union (collectFreeVars bound v) (collectFreeVars bound k)
    | ExprKind.ExprIntersect (a, b) -> Set.union (collectFreeVars bound a) (collectFreeVars bound b)
    | ExprKind.ExprUnion (a, b) -> Set.union (collectFreeVars bound a) (collectFreeVars bound b)
    | ExprKind.ExprUnique a -> collectFreeVars bound a
    | ExprKind.ExprContains (a, v) -> Set.union (collectFreeVars bound a) (collectFreeVars bound v)
    | ExprKind.ExprGroupBy (v, k) -> Set.union (collectFreeVars bound v) (collectFreeVars bound k)
    | ExprKind.ExprGroupKeys ks -> ks |> List.map (collectFreeVars bound) |> Set.unionMany
    | ExprKind.ExprSort (a, k) -> Set.union (collectFreeVars bound a) (collectFreeVars bound k)
    | ExprKind.ExprReduce (a, k, i, _) ->
        let baseVars = Set.union (collectFreeVars bound a) (collectFreeVars bound k)
        match i with
        | Some e -> Set.union baseVars (collectFreeVars bound e)
        | None -> baseVars
    | ExprKind.ExprExtents a -> collectFreeVars bound a
    | ExprKind.ExprReynolds (k, _) -> collectFreeVars bound k
    | ExprKind.ExprField (e, _) -> collectFreeVars bound e
    | ExprKind.ExprTupleIndex (t, i) -> Set.union (collectFreeVars bound t) (collectFreeVars bound i)
    | ExprKind.ExprStruct (_, fields, spread) ->
        let spreadRefs = spread |> Option.map (collectFreeVars bound) |> Option.defaultValue Set.empty
        Set.union spreadRefs (fields |> List.map (snd >> collectFreeVars bound) |> Set.unionMany)
    | ExprKind.ExprReplicate (n, b) -> Set.union (collectFreeVars bound n) (collectFreeVars bound b)
    | ExprKind.ExprDotDot (lo, hi) -> Set.union (collectFreeVars bound lo) (collectFreeVars bound hi)
    | ExprKind.ExprTyped (e, _) -> collectFreeVars bound e
    | ExprKind.ExprAssign (l, r) -> Set.union (collectFreeVars bound l) (collectFreeVars bound r)
    | ExprKind.ExprFor (src, _, kernelOpt) ->
        let srcFree = match src with
                      | ForArrays (arrs, inOpt) -> 
                          let arrFree = arrs |> List.map (collectFreeVars bound) |> Set.unionMany
                          let inFree = inOpt |> Option.map (collectFreeVars bound) |> Option.defaultValue Set.empty
                          Set.union arrFree inFree
                      | ForKernel k -> collectFreeVars bound k
        let kFree = kernelOpt |> Option.map (collectFreeVars bound) |> Option.defaultValue Set.empty
        Set.union srcFree kFree
    | ExprKind.ExprAlign (es, _) -> es |> List.map (collectFreeVars bound) |> Set.unionMany
    | _ -> Set.empty

/// Extract variable names bound by a pattern.
and patternNames (pat: Pattern) : string list =
    match pat.Kind with
    | PatternKind.PatWildcard -> []
    | PatternKind.PatVar name -> [name]
    | PatternKind.PatLit _ -> []
    | PatternKind.PatTuple pats -> pats |> List.collect patternNames
    | PatternKind.PatCons (h, t) -> patternNames h @ patternNames t
    | PatternKind.PatStruct (_, fields) -> fields |> List.collect (fun (_, p) -> patternNames p)
    | PatternKind.PatVariant (_, Some p) -> patternNames p
    | PatternKind.PatVariant (_, None) -> []
    | PatternKind.PatGuarded (p, _) -> patternNames p
    | PatternKind.PatTyped (p, _) -> patternNames p

/// Build TypedVarInfo capture list from free variable names.
let buildCaptures (env: TypeEnv) (freeVars: Set<string>) : TypedVarInfo list =
    freeVars |> Set.toList |> List.choose (fun name ->
        let info = match Map.tryFind name env.OuterScope with
                   | Some i -> Some i
                   | None -> Map.tryFind name env.Variables
        info |> Option.map (fun i ->
            { Name = name; Type = i.Type; Identity = i.Identity
              IsMutable = (i.Assign <> ReadOnly); VarId = i.VarId }))

// 6. Commutativity Extraction

/// Resolve one where-clause conjunct's parameter NAMES to parameter INDICES.
/// Names that match no parameter are dropped.
let private groupsToIndices (parms: LambdaParam list) (groups: Ident list list) : int list list =
    groups |> List.map (fun names ->
        names |> List.choose (fun name ->
            parms |> List.tryFindIndex (fun p -> p.Name = name)))

let extractCommGroups (parms: LambdaParam list) (whereClause: WhereClause option) : int list list =
    match whereClause with
    | Some wc -> groupsToIndices parms wc.Commutativity
    | None -> []

/// `where anticomm(...)` groups, by parameter index -- the signed twin of
/// extractCommGroups. Kept as its own extractor (rather than folded into the
/// comm list) because the two declarations mean different things to the
/// stage-3 validators and to output storage; the consumers that only need
/// GROUPING concatenate them.
let extractAntisymGroups (parms: LambdaParam list) (whereClause: WhereClause option) : int list list =
    match whereClause with
    | Some wc -> groupsToIndices parms wc.Antisymmetry
    | None -> []

/// Rewrite a parallel-strategy list so its `omp(var: n)` variable names refer to
/// a WRAPPER lambda's parameter names instead of the original callee's.
///
/// Needed because a named function used in kernel position is eta-expanded into
/// `lambda(__k<uid>_0, ..) -> f(__k<uid>_0, ..)`, whose params are positionally
/// 1:1 with `f`'s but carry synthesized names -- while `Lowering.extractParallelism`
/// resolves an omp var to a param INDEX by NAME. Unmapped names (a var naming no
/// parameter -- see checkOmpVarNames) are passed through unchanged rather than
/// dropped, so this stays purely a renaming. `cuda`/`mpi` carry no variable
/// names and pass through untouched.
let remapParallelVars (calleeNames: string list) (wrapperNames: string list)
                      (strategies: ParallelStrategy list) : ParallelStrategy list =
    strategies |> List.map (function
        | Omp o ->
            Omp { o with
                    Vars = o.Vars |> List.map (fun (n, dims) ->
                        match List.tryFindIndex ((=) n) calleeNames with
                        | Some i when i < List.length wrapperNames -> (wrapperNames.[i], dims)
                        | _ -> (n, dims)) }
        | s -> s)

/// Warn when an `omp(v: n)` clause names a variable that is not a parameter.
///
/// Nothing rejects this at parse time: `Lowering.extractParallelism` resolves
/// names to param indices with `List.choose`, so an unmatched name is simply
/// DROPPED, leaving `IsOmpParallel = true` with an empty depth list. Since
/// `omp(a: n)` is a licence, that silently decides "parallelized at the
/// outermost level only" instead of erroring on the typo -- this warning is
/// what makes the typo visible. buildLoopNestCodeGen's outermost-level
/// fallback keeps the typo from silently serializing the whole nest.
let checkOmpVarNames (env: TypeEnv) (paramNames: string list)
                     (whereClause: WhereClause option) (owner: string) : unit =
    match whereClause with
    | Some wc ->
        wc.Parallel
        |> List.iter (function
            | Omp o ->
                o.Vars |> List.iter (fun (v, _) ->
                    if not (List.contains v paramNames) then
                        // BL4001 (constraint violation): a `where`-clause
                        // conjunct that names nothing. `noSpan` is honest here --
                        // checkOmpVarNames takes no span, and threading one in
                        // means editing its two callers; the render degrades to
                        // a header-only line.
                        emitWarning env "BL4001" noSpan
                            (sprintf "omp(%s: ...) on %s names no parameter (parameters: %s). The clause is ignored for that variable; parallelization falls back to the outermost loop level only."
                                     v owner
                                     (if List.isEmpty paramNames then "none"
                                      else String.concat ", " paramNames)))
            | _ -> ())
    | None -> ()

// Parallel-fold reorder licence (docs/plan-cpp-perf-exploitation.md section 2)
//
// `where ... omp` on a FOLD kernel asks codegen to split the reduced axis into
// per-thread chunks and combine the partials. That reassociates AND reorders, so
// the kernel must be commutative and associative. Two licences are accepted:
//
//   1. a declared `comm(a, b)` -- the user's word, already cross-checked against
//      body parity (CommContradictsBody rejects a provably antisymmetric body);
//   2. a BUILTIN body, which carries both properties outright and needs nothing
//      declared.
//
// The predicates below answer (2) at the surface/typed level. Codegen re-derives
// the same fact from the LOWERED body (CodeGen.foldKernelBuiltinOp) because that
// is what picks the emission path; these two must stay in step. They are
// deliberately narrow -- exactly `p <op> q` over the two parameters -- so
// "recognised builtin" means the same thing at both ends, and anything richer
// takes the `comm` route instead of silently drifting apart.

/// Commutative AND associative surface ops. The pair
/// `CodeGen.isCommutativeOp && CodeGen.isAssociativeOp` over the IR ops these
/// lower to (IRAdd/IRMul/IRAnd/IROr) -- comparison ops are commutative but not
/// associative, so they are not here.
let foldBuiltinCommAssocOp (op: BinOp) : bool =
    match op with
    | OpAdd | OpMul | OpAnd | OpOr -> true
    | _ -> false

/// Surface form: is `body` exactly `p0 <op> p1` (either order) over the
/// declaration's two parameters, for a comm+assoc builtin op? Used for NAMED
/// functions, whose bodies are invisible at the reduce seam (see
/// TypeEnv.FuncFoldBuiltin).
let isBuiltinFoldBodySurface (paramNames: string list) (body: Expr) : bool =
    match paramNames, body.Kind with
    | [p0; p1], ExprKind.ExprBinOp (_, op, l, r) when foldBuiltinCommAssocOp op ->
        (match l.Kind, r.Kind with
         | ExprKind.ExprVar a, ExprKind.ExprVar b ->
            (a = p0 && b = p1) || (a = p1 && b = p0)
         | _ -> false)
    | _ -> false

/// Typed form of the same predicate, for lambda kernels (whose TypedLambdaInfo
/// IS in hand at the reduce seam). Matches on parameter VarIds rather than
/// names, so shadowing cannot fake a licence.
let isBuiltinFoldBodyTyped (ps: TypedParam list) (body: TypedExpr) : bool =
    match ps, body.Kind with
    | [p0; p1], TExprBinOp (_, op, l, r) when foldBuiltinCommAssocOp op ->
        (match l.Kind, r.Kind with
         | TExprVar (_, ia, _), TExprVar (_, ib, _) ->
            (ia = p0.VarId && ib = p1.VarId) || (ia = p1.VarId && ib = p0.VarId)
         | _ -> false)
    | _ -> false

// 7. Array Type Utilities

let inferElemType (exprs: TypedExpr list) : IRType =
    // Empty literal defaults to Float64 -- empty literals are rank-0
    // placeholders with no useful elem type to infer. For non-empty:
    // extract from the first expr's type.
    if List.isEmpty exprs then IRTScalar ETFloat64
    else
        match exprs.[0].Type with
        | ArrayElem arr -> arr.ElemType  // Already IRType
        | IRTScalar _ as t -> t          // Pass through
        | t -> t                          // Other types (Named, Tuple, etc.) -- propagate

let inferArrayLitType (builder: IRBuilder) (exprs: TypedExpr list) : IRArrayType =
    let elemType = inferElemType exprs

    // Check for ragged structure at the second level. A ragged literal is one
    // where outer entries are themselves arrays whose lengths differ. When
    // detected, produce a RaggedIdx-typed result instead of a rectangular one.
    //
    // Note: we only check at the immediate inner level. Deeper raggedness
    // (rank-3+ with internal raggedness) is not yet supported; such literals
    // will produce wrong-shape output but no compile error.
    let isRaggedAtSecondLevel =
        match exprs with
        | [] -> false
        | first :: _ ->
            match first.Kind with
            | TExprArrayLit _ ->
                let innerLengths =
                    exprs |> List.map (fun e ->
                        match e.Kind with
                        | TExprArrayLit (inner, _) -> Some inner.Length
                        | _ -> None)
                // Ragged when lengths exist for all entries and differ
                match innerLengths |> List.choose id with
                | [] -> false
                | first :: rest -> rest |> List.exists (fun n -> n <> first)
            | _ -> false

    // Array-valued elements: when entries are expressions whose TYPE is
    // already an array (e.g. `method_for(..) |> compute` bound to a name,
    // not a bracket sub-literal), the bracket contributes only the outer
    // dimension -- inner index structure comes from the element's own
    // array type. Without this, getShape (which walks TExprArrayLit
    // nesting only) infers rank-1 scalars and codegen assigns Array
    // wrappers into scalar slots. Restricted to plain dense element index
    // types (rank-1, no symmetry/dependencies, not virtual).
    let rowTypedElemArr =
        match exprs with
        | first :: _ ->
            match first.Kind, first.Type with
            | TExprArrayLit _, _ -> None
            | _, ArrayElem elemArr when
                not elemArr.IsVirtual
                && not elemArr.IndexTypes.IsEmpty
                && elemArr.IndexTypes |> List.forall (fun ix ->
                    ix.Rank = 1 && ix.Symmetry = SymNone && ix.Dependencies.IsEmpty) ->
                Some elemArr
            | _ -> None
        | [] -> None

    // TRIANGULAR => SYMMETRIC: an unannotated nest whose rows are n, n-1,
    // ..., 1 IS the left-justified simplex of `SymIdx<2, n>`
    // (canon_left_justify's storage, cell for cell):
    //
    //     let B = [[3, 2, 1], [5, 4], [6]]     // Array<Int64 like SymIdx<2, 3>>
    //     B(1, 0) == B(0, 1) == 2
    //
    // RAGGED data of exactly this shape needs its annotation
    // (`Array<T like Idx<n>, RaggedIdx<lens>>`, corpus index-types/019); any
    // other row profile still infers rectangular or inline-ragged below.
    // Matched at ANY rank r (level seeded at p is n-p wide, p'=p+i), so
    // `[[[1,2,3],[4,5],[6]], [[7,8],[9]], [[10]]]` is `SymIdx<3, 3>`.
    //
    // INCLUSIVE only: the strict profile (n-1,...,1,0) is AntisymIdx's
    // storage, but antisymmetry is a SIGN claim no shape alone justifies.
    let triangularExtent =
        // The nest's depth, if every leaf sits at the same one and no level is
        // empty. A ragged/rectangular nest is rejected by the width walk below,
        // not here; this only establishes the r to walk against.
        let rec depthOf (e: TypedExpr) =
            match e.Kind with
            | TExprArrayLit ([], _) -> None
            | TExprArrayLit (cs, _) ->
                let ds = cs |> List.map depthOf
                match ds with
                | d :: rest when ds |> List.forall Option.isSome && rest |> List.forall (fun x -> x = d) ->
                    d |> Option.map (fun k -> k + 1)
                | _ -> None
            | _ -> Some 0
        // Width walk over one level's children: a level seeded at p holds
        // n - p of them, and child i seeds the next level at p + i. The leaf
        // level's children are the cells.
        let rec widthsOk (n: int) (rank: int) (depth: int) (seed: int) (cs: TypedExpr list) =
            cs.Length = n - seed
            && (depth = rank - 1
                || cs
                   |> List.mapi (fun i c ->
                       match c.Kind with
                       | TExprArrayLit (gs, _) -> widthsOk n rank (depth + 1) (seed + i) gs
                       | _ -> false)
                   |> List.forall id)
        let n = List.length exprs
        if n < 2 then None
        else
            // The literal is one level deeper than its rows.
            let rowDepths = exprs |> List.map depthOf
            match rowDepths with
            | d :: rest when rowDepths |> List.forall Option.isSome
                             && rest |> List.forall (fun x -> x = d) ->
                let rank = Option.get d + 1
                if rank >= 2 && widthsOk n rank 0 0 exprs then Some (rank, n) else None
            | _ -> None

    if triangularExtent.IsSome then
        let (rank, n) = triangularExtent.Value
        let symIdx = {
            Id = builder.FreshId(); Rank = rank; Extent = IRLit (IRLitInt (int64 n))
            Symmetry = SymSymmetric; Tag = None; IxKind = IxKPlain; Kind = SDimension; Dependencies = []
        }
        { ElemType = elemType; IndexTypes = [symIdx]; IsVirtual = false; Identity = None }
    elif rowTypedElemArr.IsSome then
        let elemArr = rowTypedElemArr.Value
        let outerIdx = {
            Id = builder.FreshId(); Rank = 1; Extent = IRLit (IRLitInt (int64 exprs.Length))
            Symmetry = SymNone; Tag = None; IxKind = IxKPlain; Kind = SDimension; Dependencies = []
        }
        // Fresh Ids: the literal's dimensions are new index-space occurrences,
        // not the source rows' (mirrors the fresh-Id policy of the
        // rectangular branch below). Extent/Tag/Kind carry over.
        let innerIdxs = elemArr.IndexTypes |> List.map (fun ix -> { ix with Id = builder.FreshId() })
        { ElemType = elemArr.ElemType; IndexTypes = outerIdx :: innerIdxs; IsVirtual = false; Identity = None }
    elif isRaggedAtSecondLevel then
        // Build a RaggedIdx-typed array. Outer index has extent = number of
        // entries (rectangular at outer level). Inner index is RaggedIdx with
        // an IRRaggedLookup whose lengths reference is synthesized from the
        // literal's actual sub-array lengths (computed at codegen time).
        let n = List.length exprs
        let outerIdx = {
            Id = builder.FreshId(); Rank = 1; Extent = IRLit (IRLitInt (int64 n))
            Symmetry = SymNone; Tag = None; IxKind = IxKPlain; Kind = SDimension; Dependencies = []
        }
        // The lengths reference is a synthetic IRParam -- codegen recognizes
        // this and emits the lengths array inline from the literal structure.
        // The "__inline_lens" name is a sentinel that the codegen detects.
        let innerIdx = {
            Id = builder.FreshId(); Rank = 1
            Extent = IRRaggedLookup (IRParam ("__inline_lens", 0, IRTNat None))
            Symmetry = SymNone; Tag = Some "__raggedidx_inline"; IxKind = IxKRaggedInline
            Kind = SDimension; Dependencies = []
        }
        { ElemType = elemType; IndexTypes = [outerIdx; innerIdx]; IsVirtual = false; Identity = None }
    else
        // Rectangular: existing behavior -- first sub-array's length defines all rows.
        let rec getShape (es: TypedExpr list) : int list =
            match es with
            | [] -> [0]
            | first :: _ ->
                match first.Kind with
                | TExprArrayLit (inner, _) -> List.length es :: getShape inner
                | _ -> [List.length es]
        let shape = getShape exprs
        let indexTypes = shape |> List.map (fun extent ->
            { Id = builder.FreshId(); Rank = 1; Extent = IRLit (IRLitInt (int64 extent))
              Symmetry = SymNone; Tag = None; IxKind = IxKPlain; Kind = SDimension; Dependencies = [] })
        { ElemType = elemType; IndexTypes = indexTypes; IsVirtual = false; Identity = None }

let getArrayType (env: TypeEnv) (expr: Expr) : IRArrayType =
    match expr.Kind with
    | ExprKind.ExprVar name ->
        match lookupVar name env with
        | Some info ->
            match info.Type with
            | ArrayElem arrTy -> arrTy
            | _ ->
                { ElemType = IRTScalar ETFloat64
                  IndexTypes = [{ Id = env.Builder.FreshId(); Rank = 1
                                  Extent = IRParam(name + "_n", 0, IRTNat None)
                                  Symmetry = SymNone; Tag = None; IxKind = IxKPlain; Kind = SDimension
                                  Dependencies = [] }]
                  IsVirtual = false; Identity = Some (AIDVariable name) }
        | None ->
            { ElemType = IRTScalar ETFloat64
              IndexTypes = [{ Id = env.Builder.FreshId(); Rank = 1
                              Extent = IRParam(name + "_n", 0, IRTNat None)
                              Symmetry = SymNone; Tag = None; IxKind = IxKPlain; Kind = SDimension
                              Dependencies = [] }]
              IsVirtual = false; Identity = Some (AIDVariable name) }
    | _ ->
        { ElemType = IRTScalar ETFloat64; IndexTypes = []; IsVirtual = false; Identity = None }

/// Read a loop operand's array SHAPE for a method_for / co-iteration former.
///
/// Resolve through Subst before matching (the same discipline buildApplyInfo
/// applies to kernel param types): an operand that is a COMPOUND expression --
/// `zip(A - m, B - m)`, the desugaring of `(A - m) * (B - m)` -- carries a type
/// that is still an unresolved IRTInfer var here, because only the substitution
/// knows it was unified with the rank-1 array the inner pipeline produces.
/// Matching the raw type let `ArrayElem` miss and dropped the operand to
/// `getArrayType`'s non-variable fallback, whose ZERO index records made
/// zipSharedRecords see minRank 0 and return no shared records -- collapsing the
/// co-iteration to rank 0, so an elementwise product of two compound operands
/// typed as a SCALAR and `reduce` over it rejected with "requires an array as
/// first argument". Naming the operands hid the bug, since getArrayType's
/// ExprVar arm recovers a rank-1 record from env.
///
/// The ELEMENT type keeps getArrayType's Float64 default whenever it is still
/// unresolved. Inside a rank-polymorphic body a `T^1` parameter's element is
/// only pinned at the call site, so the honest answer here is an inference var
/// -- and codegen has no C++ spelling for one (it emits a
/// BLADE_UNRESOLVED_ELEM_TYPE placeholder). Defaulting matches what the
/// getArrayType fallback already supplied on this path.
/// AN INDEX RECORD'S `Kind` IS A STATEMENT ABOUT ONE APPLY, NOT ABOUT THE VALUE.
/// `SDimension` means "this apply's grid iterates it"; `TDimension` means "this
/// apply's KERNEL contributed it" -- `kernelTDims` stamps the kernel's return
/// axes that way, and `deduceOutputType` bakes them into the result TYPE. The
/// resulting VALUE has no T-shaped axes: it is an array with n axes, and the
/// NEXT apply must iterate all of them.
///
/// `computeSDimsPerArray` counts only `SDimension`, so an operand carrying an
/// inherited `TDimension` record silently lost that axis at the next `<@>`.
/// Measured on `examples/lswosa.blade`: `ls_e` is the (freq x segment) grid a
/// grouped kernel returned, `transpose(ls_e, [0,1])` accepts it as rank 2, and
/// `ls_e <@> mag2` came back RANK 1 -- so `reduce(mod_avg, (+))` refused a value
/// that is an array (line 187). The same shape with a lambda kernel came back a
/// SCALAR, because the ragged-inner accounting in `kernelInputRanks` subtracts
/// the (undercounted) S-dims and re-attributes the difference to the kernel.
///
/// Unreachable before S3 -- an array-valued kernel return had no way to exist,
/// so no value ever carried a `TDimension` record into an operand slot. This
/// normalization runs BEFORE `buildApplyInfo` re-tags the fibers THIS apply
/// consumes, so the two compose: inherited kinds reset to S, then this apply
/// stamps its own T-dims.
let private reSDimOperand (at: IRArrayType) : IRArrayType =
    if at.IndexTypes |> List.exists (fun ix -> ix.Kind <> SDimension) then
        { at with IndexTypes = at.IndexTypes |> List.map (fun ix -> { ix with Kind = SDimension }) }
    else at

let loopOperandArrayType (env: TypeEnv) (fallback: unit -> IRArrayType) (ty: IRType) : IRArrayType =
    match env.Subst.Resolve ty with
    | ArrayElem at0 ->
        let at = reSDimOperand at0
        match env.Subst.Resolve at.ElemType with
        | IRTInfer _ -> { at with ElemType = IRTScalar ETFloat64 }
        | _ -> at
    | _ -> fallback ()

// 8. Helpers

let sequenceResults (results: TypeResult<'a> list) : TypeResult<'a list> =
    let rec loop acc = function
        | [] -> Ok (List.rev acc)
        | Ok x :: rest -> loop (x :: acc) rest
        | Error e :: _ -> Error e
    loop [] results

/// Infer the type of a literal.
let inferLiteralType lit =
    match lit with
    | LitInt _ -> IRTScalar ETInt64
    | LitFloat _ -> IRTScalar ETFloat64
    | LitBool _ -> IRTScalar ETBool
    | LitString _ -> IRTScalar ETString
    | LitChar _ -> IRTScalar ETInt32
    | LitUnit -> IRTUnit

/// Synthesize the type of a *value-position* literal. Numeric/bool literals
/// get a fresh, kind-seeded inference var (like `zero`) so they flex to
/// context -- lifting into `T`/`T^k` slots and pinning to the width a context
/// requires -- while the seed keeps an unpinned literal at its natural type
/// (`let x = 1` stays Int64). String/Char/Unit have no such flex and stay
/// concrete. NOTE: pattern literals keep `inferLiteralType` (they compare by
/// value), as do the explicit `checkExpr` literal arms.
let freshLiteralType (subst: Subst) lit =
    match lit with
    | LitInt _ -> subst.FreshLiteral ETInt64
    | LitFloat _ -> subst.FreshLiteral ETFloat64
    | LitBool _ -> subst.FreshLiteral ETBool
    | _ -> inferLiteralType lit

// 9. Pattern Type Checking

/// Type-check a pattern against an expected type. Returns a TypedPattern
/// whose Bindings list contains every (name, varId, type) introduced.
let rec checkPattern (env: TypeEnv) (expected: IRType) (pat: Pattern)
    : TypeResult<TypedPattern> =
    match pat.Kind with
    | PatternKind.PatWildcard ->
        Ok { Kind = TPatWild; Type = expected; Bindings = [] }

    | PatternKind.PatVar name ->
        // Check if this name is a registered variant tag (enum constructor without data).
        // The parser can't distinguish `| North -> ...` (variant match) from `| x -> ...` (variable binding)
        // because it doesn't have type information. We resolve the ambiguity here.
        match Map.tryFind name env.VariantTags with
        | Some (parentName, None) ->
            // This is a no-payload variant constructor -- treat as PatVariant
            checkPattern env expected (inheritPatSpan pat (PatVariant (name, None)))
        | Some (parentName, Some _) ->
            // Variant with payload but used without -- treat as variable (may shadow)
            let varId = env.Builder.FreshId()
            Ok { Kind = TPatVar (name, varId); Type = expected
                 Bindings = [(name, varId, expected)] }
        | None ->
            let varId = env.Builder.FreshId()
            Ok { Kind = TPatVar (name, varId); Type = expected
                 Bindings = [(name, varId, expected)] }

    | PatternKind.PatLit lit ->
        let litTy = inferLiteralType lit
        unify env.Subst litTy expected |> Result.map (fun () ->
            { Kind = TPatLit lit; Type = expected; Bindings = [] })

    | PatternKind.PatTuple pats ->
        match env.Subst.Resolve expected with
        | IRTTuple tys when tys.Length = pats.Length ->
            List.zip pats tys |> List.map (fun (p, t) -> checkPattern env t p)
            |> sequenceResults |> Result.map (fun tPats ->
                { Kind = TPatTuple tPats; Type = expected
                  Bindings = tPats |> List.collect (fun p -> p.Bindings) })
        | _ ->
            let tys = pats |> List.map (fun _ -> env.Subst.Fresh())
            let tupleTy = IRTTuple tys
            unify env.Subst tupleTy expected |> Result.bind (fun () ->
                List.zip pats tys |> List.map (fun (p, t) -> checkPattern env t p)
                |> sequenceResults |> Result.map (fun tPats ->
                    { Kind = TPatTuple tPats; Type = expected
                      Bindings = tPats |> List.collect (fun p -> p.Bindings) }))

    | PatternKind.PatCons (headPat, tailPat) ->
        let headTy = env.Subst.Fresh()
        let tailTy = env.Subst.Fresh()
        checkPattern env headTy headPat |> Result.bind (fun tHead ->
        checkPattern env tailTy tailPat |> Result.bind (fun tTail ->
            Ok { Kind = TPatCons (tHead, tTail); Type = expected
                 Bindings = tHead.Bindings @ tTail.Bindings }))

    | PatternKind.PatVariant (tag, payloadPat) ->
        match Map.tryFind tag env.VariantTags with
        | Some (parentName, payloadTy) ->
            let isEnum = isEnumType env parentName
            match payloadPat, payloadTy with
            | Some p, Some ty ->
                checkPattern env ty p |> Result.map (fun tPayload ->
                    { Kind = TPatVariant (tag, Some tPayload, isEnum)
                      Type = IRTNamed parentName
                      Bindings = tPayload.Bindings })
            | None, None ->
                Ok { Kind = TPatVariant (tag, None, isEnum)
                     Type = IRTNamed parentName; Bindings = [] }
            | Some p, None ->
                Error (PatternTypeMismatch (sprintf "%s(...)" tag, expected))
            | None, Some _ ->
                Ok { Kind = TPatVariant (tag, None, isEnum)
                     Type = IRTNamed parentName; Bindings = [] }
        | None ->
            // Unknown variant tag: allow it, bind any payload
            match payloadPat with
            | Some p ->
                let payTy = env.Subst.Fresh()
                checkPattern env payTy p |> Result.map (fun tPayload ->
                    { Kind = TPatVariant (tag, Some tPayload, false); Type = expected
                      Bindings = tPayload.Bindings })
            | None ->
                Ok { Kind = TPatVariant (tag, None, false); Type = expected; Bindings = [] }

    | PatternKind.PatStruct (typeName, fieldPats) ->
        let fieldTypes =
            match lookupTypeDef typeName env with
            | Some (TDIStruct (_, _, fields, _)) ->
                fields |> List.map (fun (n, t) -> (n, t)) |> Map.ofList
            | _ -> Map.empty
        fieldPats |> List.map (fun (fname, fpat) ->
            let fTy = Map.tryFind fname fieldTypes |> Option.defaultValue (env.Subst.Fresh())
            checkPattern env fTy fpat |> Result.map (fun tp -> (fname, tp)))
        |> sequenceResults |> Result.map (fun tFields ->
            { Kind = TPatStruct (typeName, tFields)
              Type = (if Map.isEmpty fieldTypes then expected else IRTNamed typeName)
              Bindings = tFields |> List.collect (fun (_, p) -> p.Bindings) })

    | PatternKind.PatGuarded (innerPat, _guardExpr) ->
        // Guard expression is type-checked in inferMatch when we have full env
        checkPattern env expected innerPat |> Result.map (fun tInner ->
            { Kind = TPatGuarded (tInner, mkTyped (TExprLit (LitBool true)) (IRTScalar ETBool))
              Type = expected; Bindings = tInner.Bindings })

    | PatternKind.PatTyped (innerPat, tyAnnotation) ->
        let annotTy = lowerTypeExpr env tyAnnotation
        unify env.Subst annotTy expected |> Result.bind (fun () ->
            checkPattern env annotTy innerPat)

// 10. Expression Type Inference (every Expr variant handled explicitly)

/// Drive type inference for special forms (extents, mask, sort, intersect,
/// union, group_keys, group_by) when the array argument is unresolved
/// (typically an unannotated kernel parameter). Rejecting non-arrays on the
/// resolved type fails with "requires array" on an unbound IRTInfer even
/// though the special form could supply the constraint; this helper
/// inverts that by synthesizing a fresh IRTArray (rank 1, fresh elem type,
/// fresh anonymous index, Tag=None so it passes as "synthetic") and
/// unifying the argument with it. Concrete-but-not-an-array is still a
/// real type error. Centralized here rather than duplicated per special
/// form for IDE/language-server tooling.
///
/// `requireArrayArg` generalizes to a MINIMUM rank (`minRank` rank-1
/// slots): ops needing rank >= 2 (gram, transpose) must use this, since
/// rank-1 synthesis always failed on an unannotated argument otherwise.
///
/// DEFERRED: `decompact` is NOT switched over -- its demand is a
/// compact-GROUP slot (Rank >= 2, non-SymNone), not a rank COUNT, and needs
/// a symmetry-carrying synthesis.
let requireArrayArgMinRank (env: TypeEnv) (tArr: TypedExpr) (opName: string) (minRank: int) : TypeResult<IRArrayType> =
    let resolved = env.Subst.Resolve(tArr.Type)
    match resolved with
    | ArrayElem arrTy -> Ok arrTy
    | IRTInfer _ ->
        let k = max 1 minRank
        let freshIdx i = {
            Id = env.Builder.FreshId()
            Rank = 1
            Extent =
                IRParam ((if k = 1 then sprintf "__%s_inferred_n" opName
                          else sprintf "__%s_inferred_n%d" opName i),
                         0, IRTNat None)
            Symmetry = SymNone
            Tag = None; IxKind = IxKPlain
            Kind = SDimension
            Dependencies = []
        }
        // NOTE (measured, 2026-08-09) -- this synthesis is where a `T^k`
        // DECLARATION parameter loses its HM element polymorphism: giving `T`
        // its shape rewrites it to `Array<E, ..>` where `E` is a plain fresh
        // var, so zonk defaults `E` to whatever the first call site wanted and
        // the function exists at exactly one element type. `function
        // variance(x: T^1) = { let n = extents(x); ... }` called on a real and
        // then a complex series fails in g++; `function dbl(xs: T^1) = xs <@>
        // ... ` (no array demand in the body) does not, because nothing gives
        // `T` a shape and the IR monomorphizer sees it whole. PRE-EXISTING --
        // identical under master @3500258. The obvious repair (mint `E` in the
        // substitution's id space and carry the polymorphic mark, propagating
        // it on var-to-var binds) was implemented and measured: it fixes the
        // direct case (`variance` at two element types) and then fails one
        // level down in IR validation, because lifted lambdas and module
        // bindings holding the still-open var are not cloned per specialization
        // (`functions/055`, `functions/059`, and `lsdft`'s own frequency kernel
        // all go BL6001). It needs the IR-side clone-and-specialize pass to
        // cover those shapes first; see the report accompanying this comment.
        let freshElem = env.Builder.FreshInferType()
        let freshArrType = {
            ElemType = freshElem
            IndexTypes = List.init k freshIdx
            IsVirtual = false
            Identity = None
        }
        unify env.Subst tArr.Type (mkArrayLike freshArrType)
        |> Result.bind (fun () ->
            // Re-resolve in case unification refined the elem type via
            // some other constraint already in the substitution.
            match env.Subst.Resolve(tArr.Type) with
            | ArrayElem a -> Ok a
            | _ -> Error (IntrinsicBindArrayFailed opName))
    | _ ->
        Error (IntrinsicNeedsArray opName)

let requireArrayArg (env: TypeEnv) (tArr: TypedExpr) (opName: string) : TypeResult<IRArrayType> =
    requireArrayArgMinRank env tArr opName 1

/// S1 (docs/plan-kernel-body-materialization.md, M-B): materialize a
/// caret-shorthand `T^k` operand into the rank-k array it is already pinned to
/// be.
///
/// A `T^k` parameter is an arity-constrained inference VAR, not an IRTArray
/// (Subst.LookupOrCreateTypeVar): the caret pins the RANK and the array shape
/// is only built when some demand supplies it. Every array INTRINSIC issues
/// that demand (`requireArrayArg`; see inferProdSum's note for the long
/// version) -- but the two seams that make an array-valued INTERMEDIATE
/// (an elementwise binop, and a nested `<@>` whose operand is the var) both
/// gate their array-producing arms on the operand RESOLVING to an array, so
/// against an unmaterialized var they fall to the scalar arm and the
/// intermediate is scalar-typed. Every array consumer downstream then honestly
/// refuses a value that IS an array.
///
/// This is NOT a guess. Unify.fs's arity invariant already says an arity-k var
/// can bind to nothing but a rank-k array (anything else is an outright type
/// error there), so supplying the shape early can only pre-compute a binding
/// unification would have been forced into later. Errors are therefore
/// swallowed: the demand is an optimization of WHEN, never of WHETHER, and a
/// failure leaves the var exactly as it was for the pre-existing diagnostic to
/// report. Non-arity vars are left alone -- an unannotated kernel parameter is
/// one of those and may still resolve to a scalar.
///
/// THE DEMAND IS NOT FREE, and both of its call-site restrictions were paid for
/// in regressions. Binding a `T^k` var SPENDS it: in a named function's
/// declaration body that var is the HM-polymorphic signature var the
/// monomorphizer specializes on, so an unguarded demand collapses the function
/// to one specialization -- or worse, hands codegen a synthetic shape whose
/// invented `__..._inferred_n` extent nothing ever declares. Measured:
///
///   * `function packsum1(A: Poly<T^1>) -> T^1` folding `head + packsum1(tail)`
///     lost every `_HM_..._arr_double__r1s0e2` suffix across arity/019, 020,
///     021, 022, 024, 025, 026, 028; the recursive call vanished from the
///     emitted C++ and the surviving loop read `arr0.extents[0]` off an
///     undeclared name. Guard: the binop seam demands only when the OTHER
///     operand pins the shape (there, both were unresolved).
///   * `function dbl(xs: T^1) = xs <@> lambda(x) -> x + x |> compute`, applied
///     to a Float array and then an Int array, collapsed to ONE specialization
///     ("could not convert Array<long long int> to Array<double>"). Guard: the
///     loop-former seam demands only inside a LAMBDA body, where the var is
///     monomorphic anyway -- `buildApplyInfo` unifies a kernel param with the
///     iterated row type moments later, so pre-computing can only agree with
///     what unification was about to do.
///
/// Both guards live at the call sites, since they differ per seam.
let materializeArityVar (env: TypeEnv) (tArg: TypedExpr) (opName: string) : unit =
    match env.Subst.Resolve tArg.Type with
    | IRTInfer vid ->
        (match env.Subst.GetArityConstraint vid with
         | Some k when k >= 1 -> requireArrayArgMinRank env tArg opName k |> ignore
         | _ -> ())
    | _ -> ()

/// Tag-check helper: validate that each index argument's nominal tag (if any)
/// agrees with the corresponding array slot's nominal tag. Slot tags starting
/// with "__" are internal synthetic markers and skipped. Untagged ints into
/// named slots are permissive (warning emitted, no error) -- iteration-tagging
/// typically resolves these later.
///
/// Pulled out as a separate helper so the same logic can run BOTH at the
/// indexing call site (eager check via dispatchAppOrIndex) AND as a
/// post-unification pass over a kernel body (revalidateBodyTagChecks).
/// The index slot each POSITIONAL argument aligns to. One slot per arg --
/// except a compound head, whose rank-k axis consumes k FLAT subscripts, so
/// it repeats k times (mirroring the SymIdx-style flat consumption). Sparse
/// heads stay 1:1 (one tuple arg fills the slot).
let private slotPerArg (arrTy: IRArrayType) : IRIndexType list =
    match arrTy.IndexTypes with
    | h :: rest when h.IxKind = IxKCompound -> List.replicate (max 1 h.Rank) h @ rest
    | l -> l

/// Is this subscript target a COMPILER-SYNTHESIZED buffer? Desugarings that
/// build their own scratch arrays (`let rec`'s `__rec_x` / `__slice_x` /
/// `__seed_x` / `__lag<k>_x`, rank-k `reduce`'s `__rksrc<uid>`) walk them with
/// their own generated counters, which are plain Int64 -- the desugarer owns
/// both the buffer and the walk, so the index space is correct by
/// construction. The untagged-index NOTE below is advice to the AUTHOR ("cast,
/// or iterate via range<Tag>"), and on a `__`-named buffer there is no source
/// spelling that could take it: a `let rec` with ZERO subscripts in the source
/// still drew one note per tagged axis. Same rule, and the same reason, as the
/// `tagName.StartsWith "__"` guard below and BL4010's `__of13_0` guard: a
/// diagnostic the user cannot act on is noise. The ERROR arms are unaffected
/// -- only the advisory warning is suppressed, and only on names the surface
/// grammar reserves for the compiler.
let private isSynthesizedBuffer (tArr: TypedExpr) : bool =
    match tArr.Kind with
    | TExprVar (name, _, _) -> name.StartsWith "__"
    | _ -> false

let private checkArrayIndexTags (env: TypeEnv) (tArr: TypedExpr) (arrTy: IRArrayType) (tArgs: TypedExpr list) : TypeResult<unit> =
    let synthetic = isSynthesizedBuffer tArr
    let slots = slotPerArg arrTy
    let n = min tArgs.Length slots.Length
    let tagMismatch =
        List.zip (tArgs |> List.truncate n) (slots |> List.truncate n)
        |> List.tryPick (fun (tArg, idxType) ->
            match idxType.Tag with
            | Some tagName when not (tagName.StartsWith("__")) ->
                match env.Subst.Resolve tArg.Type with
                | IRTIdxTagged (_, IRefNamed argName)
                    when argName = tagName -> None
                | IRTIdxTagged (_, IRefNamed argName) ->
                    Some (IndexTagMismatchNamed (tagName, argName))
                | IRTIdxTagged (_, IRefAnon _) ->
                    Some (IndexTagMismatchAnon tagName)
                // A `Base<_>` parameter declined to constrain the tag, so it
                // carries no more guarantee than an untagged int -- warn with
                // the same text rather than erroring, keeping the wildcard
                // usable as the documented escape hatch for raveled indices.
                | IRTIdxTagged (_, IRefAny)
                | IRTScalar (ETInt32 | ETInt64) ->
                    // BL4003 (index type violation) -- the warning twin of this
                    // very site: the ERROR branch two cases up raises
                    // IndexTagMismatchNamed, which is already BL4003.
                    if not synthetic then
                        emitWarning env "BL4003" tArg.Span (sprintf
                            "Array indexed with untagged integer where slot expects tag '%s'. Consider an explicit cast like `(expr : %s)` or iterate via `range<%s>` to flow the tag automatically."
                            tagName tagName tagName)
                    None
                | _ -> None
            | _ -> None)
    match tagMismatch with
    | Some err -> Error err
    | None -> Ok ()

/// EnumIdx label subscript: a STRING LITERAL index into an axis whose index
/// type is a registered EnumIdx folds to its ordinal HERE, so lowering,
/// codegen, and the interpreter all see a plain constant subscript -- the
/// CSV headered-column access idiom `obs.vars.data[i, "temp"]`. The folded
/// literal is retyped IRTIdxTagged so the nominal tag check accepts it.
/// Restricted to STRING literals: int-valued EnumIdx keys are stored raw
/// (foreign-key semantics, sql-foreign-keys corpus) and not position-folded.
/// An unknown label is a type error naming the available labels; runtime
/// (non-literal) label subscripts stay unsupported.
let private foldEnumIdxLabels (env: TypeEnv) (arrTy: IRArrayType) (tArgs: TypedExpr list) : TypeResult<TypedExpr list> =
    let slots = slotPerArg arrTy |> Array.ofList
    tArgs
    |> List.mapi (fun i a ->
        if i >= slots.Length then Ok a
        else
            match a.Kind, slots.[i].Tag with
            | TExprLit (LitString s), Some tagName ->
                (match Map.tryFind tagName env.TypeDefs with
                 | Some (TDIEnumIdx (_, _, values, _)) ->
                     (match values |> List.tryFindIndex ((=) (EVString s)) with
                      | Some ord ->
                          Ok { a with
                                Kind = TExprLit (LitInt (int64 ord))
                                Type = IRTIdxTagged (IRTScalar ETInt64, IRefNamed tagName) }
                      | None ->
                          let avail = values |> List.map (function EVString v -> v | EVInt n -> string n)
                          Error (EnumIdxUnknownLabel (tagName, s, avail)))
                 | _ -> Ok a)
            | _ -> Ok a)
    |> sequenceResults

/// Index-arity validation for a CompoundIdx slot (formalism 4.5): when the
/// NEXT slot is a compound of Rank k, the coordinate filling it must be a
/// single k-tuple `B((c0, ..., c_{k-1}))` (the canonical poly-index form,
/// 5.4) -- NOT the flat currying form `B(c0, c1)`, since currying one
/// coordinate at a time would make partial forms like `B(c0)(_)(c2)`
/// ambiguous with wildcards. Fires only when the head slot is compound;
/// validates the FIRST arg against it and lets remaining args flow to
/// trailing regular slots (`B((i,j), t)` is allowed). Rank-1 compound
/// (k=1) also accepts a bare scalar `B(i)` (the parser collapses a 1-tuple
/// to a bare expr); k >= 2 requires the tuple form.
///
/// Keyed on the head slot's IxKind:
///   COMPOUND -- FLAT positional subscripts like SymIdx: B(c0,...,c(k-1),t).
///       Full-arity only; tuple/wildcard/partial forms are rejected (moved
///       to SparseIdx). Also owns flat-count accounting (under/over-supply).
///   SPARSE -- one TUPLE per sparse axis (3.5 currying): S((a,b)), S((a,_)),
///       short prefix tuples; wildcards mark free axes.
let private validateTabulatedIndex (env: TypeEnv) (arrTy: IRArrayType) (tArgs: TypedExpr list) : TypeResult<unit> =
    match arrTy.IndexTypes with
    | headSlot :: trailingSlots when headSlot.IxKind = IxKCompound ->
        let k = headSlot.Rank
        (match tArgs with
         | [] -> Ok ()  // bare array value; nothing to check
         | args ->
             let isWild (e: TypedExpr) = match e.Kind with TExprWildcard -> true | _ -> false
             let firstIsTuple =
                 (match args.Head.Kind with TExprTuple _ -> true | _ -> false)
                 || (match env.Subst.Resolve args.Head.Type with IRTTuple _ -> true | _ -> false)
             if firstIsTuple then Error (CompoundTupleForm k)
             elif args |> List.exists isWild then Error (CompoundTupleForm k)
             elif args.Length < k then Error (CompoundUnderSupplied (k, args.Length))
             elif args.Length > k + trailingSlots.Length then Error (CompoundOverSupplied (k, args.Length))
             else Ok ())
    | headSlot :: _ when headSlot.IxKind = IxKSparse ->
        let k = headSlot.Rank
        match tArgs with
        | [] -> Ok ()  // no args consumed here (e.g. bare array value); nothing to check
        | firstArg :: _ ->
            // Wildcard sparse indexing: a FULL-arity tuple with `_` marking
            // FREE axes (3.5 currying: S((a,_)), S((_,b)), S((a,_,_)), ...).
            // Residual rank = wildcard count (1 free -> dense Idx gather;
            // >=2 -> residual SparseIdx). Multiple wildcards are ALLOWED
            // here, unlike function partial application (6.2.3, single `_`
            // only): S((a,_,_)) is the only way to pin a single leading
            // coordinate of a rank-3 sparse, since `(a)` collapses to a
            // bare scalar in the parser.
            let wildcardPositions =
                match firstArg.Kind with
                | TExprTuple elems ->
                    elems |> List.mapi (fun i e -> (i, e))
                          |> List.choose (fun (i, e) -> match e.Kind with TExprWildcard -> Some i | _ -> None)
                | _ -> []
            match firstArg.Kind, wildcardPositions with
            | TExprWildcard, _ ->
                // Bare `S(_)` (the parser collapses a 1-tuple `(_)` to the bare
                // hole). It pins nothing: on a rank-1 head the "residual"
                // would be the whole array, and on rank >= 2 it is not even a
                // tuple. Reject rather than let the hole flow as a coordinate.
                Error (SparseBareWildcard k)
            | _, (_ :: _) ->
                let tupleLen =
                    match firstArg.Kind with
                    | TExprTuple es -> es.Length
                    | _ -> 0
                if tupleLen <> k then
                    Error (SparseWildcardArity (k, tupleLen))
                elif wildcardPositions.Length = k then
                    Error (SparseAllFree k)
                else Ok ()
            | _, [] ->
              (match env.Subst.Resolve firstArg.Type with
               | IRTTuple tys when tys.Length >= 1 && tys.Length <= k -> Ok ()
                // 1 <= j <= k: full (j = k) or partial (j < k, leading-prefix)
                // index. The residual type is computed by tabulatedResidualType;
                // codegen reconstitutes it as a gather.
               | IRTTuple tys ->
                   Error (SparseOverSupplied (k, tys.Length))
               | _ when k = 1 -> Ok ()  // rank-1 head: bare scalar index is fine
               | _ ->
                   // k >= 2 but the first arg is not a tuple. This is the flat
                   // currying form S(c0, c1, ...) or a single scalar -- reject and
                   // point at the canonical tuple form.
                   Error (SparseNeedsTuple k))
    | _ -> Ok ()

/// The residual index-type fragment (formalism 4.5 currying table) that
/// REPLACES a rank-k compound slot after pinning j of its coordinates.
/// Pinned axes are the leading prefix (short tuple) or non-`_` positions
/// (full-arity wildcard tuple) -- shape depends only on the count, not
/// position. Pinned POSITIONS live in the index tuple itself (unit-literal
/// sentinels at free axes); codegen reads them to pick window vs gather.
///
///   j = k       -> []            (compound fully consumed; trailing dims follow)
///   k - j = 1   -> [dense Idx]   (contiguous window of present cells)
///   k - j >= 2  -> [CompoundIdx] (residual masked product over k-j axes)
///
/// Both residual cases carry Extent = IRCompoundProject(parentIR, j);
/// placementOf reads the residual RANK to pick dense vs tabulated.
let private tabulatedResidualType (headSlot: IRIndexType) (parentIR: IRExpr) (j: int) (fresh: unit -> IRId) : IRIndexType list =
    let k = headSlot.Rank
    let residualRank = k - j
    if residualRank <= 0 then
        []  // j = k: fully consumed
    elif residualRank = 1 then
        // Dense residual Idx: for a compound head, a contiguous [lo,hi)
        // window (prefix) or gather (scattered); for sparse, always a
        // gathered dense copy in key order.
        [ { Id = fresh (); Rank = 1
            Extent = IRCompoundProject (parentIR, j)
            Symmetry = SymNone; Tag = None; IxKind = IxKPlain
            Kind = SDimension; Dependencies = [] } ]
    else
        // Residual keeps the PARENT's kind (partial compound stays compound,
        // partial sparse stays sparse), so it's further indexable/partial-
        // indexable just like a top-level one.
        let tag, kind =
            match headSlot.IxKind with
            | IxKSparse -> Some "__sparseidx", IxKSparse
            | _ -> Some "__compoundidx", IxKCompound
        [ { Id = fresh (); Rank = residualRank
            Extent = IRCompoundProject (parentIR, j)
            Symmetry = SymNone; Tag = tag; IxKind = kind
            Kind = SDimension; Dependencies = [] } ]

/// Rank as CODEGEN will see it -- the `k` in `Array<elem, k>`, 0 for a
/// scalar -- when the type is concrete enough to know. `None` means
/// "unknown, stand down" (inference vars, poly packs, funcs, structs,
/// tuples, loops, dists); callers compare two `Some` ranks only, so an
/// unresolved type never manufactures a mismatch.
///
/// SINGLE SOURCE OF TRUTH for the direct-application rank check: the eager
/// check in `dispatchAppOrIndex`'s FuncElem arm and the post-unification
/// sweep `collectAppRankErrors` both call this, since the first cannot see
/// through an open variable and the two must agree on what a rank is.
let concreteRankOf (subst: Subst) (ty: IRType) : int option =
    let rec go t =
        match subst.Resolve t with
        | ArrayElem a -> Some (a.IndexTypes |> List.sumBy (fun i -> max 1 i.Rank))
        | IRTScalar _ -> Some 0
        | IRTUnitAnnotated (inner, _) -> go inner
        | IRTIdxTagged (inner, _) -> go inner
        | _ -> None
    go ty

/// Coarse VALUE CLASS of a type -- "what kind of thing this is at runtime"
/// -- when the type is concrete enough to know. `None` means "unknown,
/// stand down"; callers compare two `Some` classes only, so an unresolved
/// type never manufactures a mismatch. That is what keeps HM alive here: a
/// `T^k` parameter resolves to an open IRTInfer at the call site and simply
/// declines to be classified, so no call site ever binds it.
///
/// Deliberately COARSE. The numeric tower is ONE class (an Int64 literal
/// legitimately reaches a Float64 parameter, and Float-into-Int is the
/// annotation seam's business, not this one); units and index tags are
/// transparent, so bare-literal unit LIFTING (`f(2.0)` into a `Float64<day>`
/// parameter, f1ba7b2) still works and BL3010 keeps its own, earlier say;
/// arrays decline because rank is `concreteRankOf`'s job; tuples decline
/// because tuple WIDTH belongs to the pack/tuple schema
/// (docs/plan-tuples-vs-arg-packs.md), not to an element-class test.
let concreteClassOf (subst: Subst) (ty: IRType) : string option =
    let rec go t =
        match subst.Resolve t with
        | IRTUnitAnnotated (inner, _) -> go inner
        | IRTIdxTagged (inner, _) -> go inner
        | IRTScalar ETString -> Some "text"
        | IRTScalar ETBool -> Some "boolean"
        | IRTScalar ETUnit -> Some "unit"
        | IRTUnit -> Some "unit"
        | IRTScalar _ -> Some "number"
        | IRTDist _ -> Some "distribution"
        | IRTNamed _ -> Some "named type"
        | FuncElem _ -> Some "function"
        | _ -> None
    go ty

/// Pair each argument with the parameter it binds, 1:1 and positional,
/// truncated to the shorter list: (0-based parameter position, param type,
/// arg type).
///
/// Stays 1:1 under the width schema (docs/plan-tuples-vs-arg-packs.md 6c), and
/// that is the design, not an omission: `regroupArgsByWidth` runs FIRST at the
/// FuncElem arm and hands every check below an argument list already regrouped
/// to one node per parameter. So the pairing rule lives in exactly one place,
/// and each per-pair check here keeps comparing one parameter against one
/// value. If a future change moves regrouping later, this is the function that
/// has to learn about widths instead.
let appArgPairs (paramTys: IRType list) (argTys: IRType list)
                : (int * IRType * IRType) list =
    let n = min paramTys.Length argTys.Length
    List.zip (List.truncate n paramTys) (List.truncate n argTys)
    |> List.mapi (fun i (pTy, aTy) -> (i, pTy, aTy))

/// WIDTH SCHEMA at the DIRECT-CALL seam (docs/plan-tuples-vs-arg-packs.md 6c).
/// The argument list is a list of NODES -- one per written argument, nesting
/// preserved -- matched greedily against the parameter list read as a width
/// schema. A `Tuple<k>` parameter (declared `Tuple<k>` or `(T1, .., Tk)`; both
/// lower to `IRTTuple`) prefers one k-wide tuple node and otherwise regroups k
/// consecutive nodes, so `addPair(b, c)`, `addPair((b, c))` and
/// `let t = b, c; addPair(t)` are the same call.
///
/// Deliberately one-directional. The reverse -- expanding a tuple ARGUMENT into
/// k scalar arguments for k scalar parameters -- is NOT done, because `f(t)` on
/// a 2-parameter `f` is already partial application (Parser/ExprApp eta-expands
/// it), and the language cannot have both readings. Every arm below fires only
/// where the plain pairing has no reading at all, so it can turn an error into
/// a call but never redirect one that already type-checks. That is also why
/// 6c rule 3's `f((a, b)) == f(a, b)` holds at the OPERAND seam but not here:
/// at a call, the left-hand side is partial application and already means
/// something. Reported as a deviation rather than silently.
let regroupArgsByWidth (env: TypeEnv) (paramTys: IRType list) (tArgs: TypedExpr list)
                       : TypedExpr list =
    let subst = env.Subst
    let widthOf (t: IRType) =
        match subst.Resolve t with
        | IRTTuple ts when ts.Length >= 2 -> ts.Length
        | _ -> 1
    /// The components of a single tuple-typed argument, seen through any number
    /// of alias hops (the same fixpoint `resolveTypedExprDeep` applies at the
    /// operand seam, inlined because that one lives in the later `and` group).
    let tupleParts (e: TypedExpr) : TypedExpr list option =
        let rec chase (fuel: int) (x: TypedExpr) =
            if fuel <= 0 then x
            else
                match x.Kind with
                | TExprVar (name, _, _) ->
                    (match lookupVar name env with
                     | Some info ->
                         (match info.TypedValue with
                          | Some v when not (System.Object.ReferenceEquals(v, x)) -> chase (fuel - 1) v
                          | _ -> x)
                     | None -> x)
                | _ -> x
        match (chase 64 e).Kind with
        | TExprTuple es when es.Length >= 2 -> Some es
        | _ -> None
    let widths = paramTys |> List.map widthOf
    // Greedy left-to-right fold over a NODE list (6c rule 2): a `Tuple<k>`
    // parameter PREFERS one tuple node of top-level width k, and otherwise
    // regroups k consecutive nodes. Nesting is preserved either way -- the
    // regrouped tuple keeps whatever the nodes were. `None` when the schema
    // does not fit these nodes.
    let fold (nodes: TypedExpr list) : TypedExpr list option =
        let mutable rest = nodes
        let mutable ok = true
        let out =
            widths
            |> List.map (fun w ->
                if not ok then []
                elif w = 1 then
                    match rest with
                    | a :: t -> rest <- t; [a]
                    | [] -> ok <- false; []
                else
                    match rest with
                    | a :: t when (match subst.Resolve a.Type with
                                   | IRTTuple ts -> ts.Length = w
                                   | _ -> false) ->
                        rest <- t; [a]
                    | _ when rest.Length >= w ->
                        let taken = rest |> List.truncate w
                        rest <- rest |> List.skip w
                        [ { Kind = TExprTuple taken
                            Type = IRTTuple (taken |> List.map (fun (a: TypedExpr) -> a.Type))
                            Span = (List.head taken).Span } ]
                    | _ -> ok <- false; [])
            |> List.concat
        if ok && List.isEmpty rest then Some out else None
    if List.sum widths = paramTys.Length then tArgs        // no tuple parameter
    elif tArgs.Length = paramTys.Length then tArgs
        // Already 1:1 -- and this IS 6c rule 3's precedence (a): a lone
        // `Tuple<m>` parameter facing a lone m-tuple argument DIRECT-BINDS, so
        // `lam(((a,b),(c,d)))` against `lambda(r: Tuple<2>)` gives r the pair
        // of pairs rather than splicing it into two.
    else
        match fold tArgs with
        | Some out -> out
        | None ->
            // Precedence (b): the ONE-LEVEL SPLICE. A whole argument list that
            // is a single tuple node opens once and the schema is re-offered
            // the components -- `f(((a,b),(c,d)))` against
            // `f(p: Tuple<2>, q: Tuple<2>)`. Tried only AFTER the unspliced
            // fold, so the direct match always wins; and it can only rescue a
            // call that had no reading at all, never redirect one that did.
            let spliced =
                match tArgs with
                | [single] ->
                    (match tupleParts single with
                     | Some parts -> parts
                     | None -> tArgs)
                | _ -> tArgs
            if spliced.Length = tArgs.Length then tArgs
            else match fold spliced with
                 | Some out -> out
                 | None -> tArgs

/// The rank comparison itself, over a parameter list and an argument list:
/// the first position whose two ranks are both known and disagree, as
/// (0-based position, param rank, arg rank, param type, arg type).
///
/// Two arms stand down entirely rather than compete with a better
/// diagnostic:
///   * A `Poly<T^r>` pack param makes the arrow variadic -- positional
///     pairing is meaningless there (monomorphization owns the call).
///   * UNDER-application. Fewer args than params is an arity error, and the
///     arity message names the real defect; a rank complaint about the args
///     that ARE present would bury it. (OVER-application still checks the
///     prefix this arrow consumes -- the surplus re-dispatches against the
///     result type and gets its own pass.)
let firstArgRankClash (subst: Subst) (paramTys: IRType list) (argTys: IRType list)
                      : (int * int * int * IRType * IRType) option =
    let isVariadic =
        paramTys |> List.exists (fun t ->
            match subst.Resolve t with IRTPoly _ -> true | _ -> false)
    if isVariadic || argTys.Length < paramTys.Length then None
    else
        appArgPairs paramTys argTys
        |> List.tryPick (fun (i, pTy, aTy) ->
            match concreteRankOf subst pTy, concreteRankOf subst aTy with
            | Some pr, Some ar when pr <> ar -> Some (i, pr, ar, pTy, aTy)
            | _ -> None)

/// The element-CLASS comparison, the twin of `firstArgRankClash` over the
/// same pairs: the first position whose two classes are both known and
/// disagree, as (0-based position, param type, arg type). Same two
/// stand-downs, for the same reasons -- a variadic `Poly<T^r>` pack makes
/// positional pairing meaningless, and under-application is an arity error
/// whose own message must not be buried.
///
/// This is the CHECK-time half of a hole that used to reach g++: a direct
/// application does NOT unify arguments against parameters (see the comment
/// in dispatchAppOrIndex's FuncElem arm), so nothing else at this seam
/// noticed `f("hello")` against a `Float64` parameter. Unifying here is not
/// an option: a `function` declaration's type is created ONCE with SHARED
/// type variables across every call site (checkFunctionDecl binds it with
/// bindVarSimple, no scheme), so unifying at one site would over-constrain
/// the next. Comparing resolved CLASSES binds nothing.
let firstArgTypeClash (subst: Subst) (paramTys: IRType list) (argTys: IRType list)
                      : (int * IRType * IRType) option =
    let isVariadic =
        paramTys |> List.exists (fun t ->
            match subst.Resolve t with IRTPoly _ -> true | _ -> false)
    if isVariadic || argTys.Length < paramTys.Length then None
    else
        appArgPairs paramTys argTys
        |> List.tryPick (fun (i, pTy, aTy) ->
            match concreteClassOf subst pTy, concreteClassOf subst aTy with
            | Some pc, Some ac when pc <> ac -> Some (i, pTy, aTy)
            | _ -> None)

let rec private dispatchAppOrIndex (env: TypeEnv) (tFunc: TypedExpr) (tArgs: TypedExpr list) : TypeResult<TypedExpr> =
    match tFunc.Type with
    // SUBSCRIPTING A WREATH ARRAY, handled FIRST and EXPLICITLY -- because
    // of the catch-all's vacuity, not tidiness. A depth-2 OrbIdx record is
    // ONE index slot spanning prod(ri) raw axes, so `W(i,j,k,l)` has 4 args
    // against 1 slot: the arity guard below is FALSE, no other arm matches,
    // and without an arm here the catch-all mints a fresh inference var and
    // returns Ok, letting a bogus subscript reach codegen/interp as a
    // plausible dense read into a flat pool. Every exit below is explicit.
    //
    // THE RULE: a sole wreath slot takes exactly `Rank` = prod(ri) FLAT
    // scalar subscripts (like a rank-k SymIdx group) and yields the ELEMENT
    // type, per docs/plan-orbidx-decompaction.md section 2's
    //     dense[t] = chi(t) * pool[orbRank(canon(t))],  0 on the zero set --
    // both VALUES, not errors. A wrong ARITY (incl. a partial read, which
    // has no residual class) is OrbitSubscriptArity; a wreath slot combined
    // with other index slots has no pool layout, so it gets the generic
    // storage refusal.
    | ArrayElem arrTy when
        not (List.isEmpty tArgs)
        && arrTy.IndexTypes |> List.exists (fun ix -> ix.Symmetry = SymWreath) ->
        (match arrTy.IndexTypes with
         | [ ix ] when ix.Symmetry = SymWreath ->
             let levels = ppOrbitLevels (orbitLevelsOf ix)
             let axes = max 1 ix.Rank
             if tArgs.Length <> axes then
                 Error (OrbitSubscriptArity (levels, axes, tArgs.Length))
             elif tArgs |> List.exists (fun a -> match a.Kind with TExprWildcard -> true | _ -> false) then
                 // A `_` marks a FREE axis; freeing one axis of a wreath is the
                 // partial read again, in the other spelling. Same verdict, and
                 // the arity message's partial-read sentence is the right one:
                 // a hole is a coordinate not supplied.
                 Error (OrbitSubscriptArity (levels, axes, axes - (tArgs |> List.filter (fun a -> match a.Kind with TExprWildcard -> true | _ -> false) |> List.length)))
             else
                 // The nominal tag check runs for uniformity, not because it
                 // can currently fire: `mkWreathIndexRecord` stamps the
                 // "__orbidx" KIND sentinel as the record's Tag, and the check
                 // skips every "__" tag. It is here so that if a wreath class
                 // ever carries its base's tag through (section "DEFERRED: a
                 // BLOCK-SPEC base under a depth >= 2 class"), this door already
                 // enforces it instead of being the one subscript form that
                 // silently does not.
                 checkArrayIndexTags env tFunc arrTy tArgs
                 |> Result.map (fun () ->
                     let identity = match tFunc.Kind with TExprVar (_, _, id) -> id | _ -> None
                     mkTyped (TExprIndex (tFunc, tArgs, identity)) arrTy.ElemType)
         | _ ->
             let ix = arrTy.IndexTypes |> List.find (fun ix -> ix.Symmetry = SymWreath)
             Error (OrbitStorageUnsupported (ppOrbitLevels (orbitLevelsOf ix),
                                             "array subscript of a wreath group combined with other index slots")))
    // FULL-ARITY READ OF A COMPACT GROUP -- same hole the wreath arm above
    // closes. A rank-k compact slot (SymIdx/AntisymIdx/HermitianIdx) is ONE
    // index record spanning k dims and takes k FLAT subscripts, so `A(i,j)`
    // presents 2 args against 1 slot: the next arm's arity guard is FALSE,
    // and without this arm the read reaches the catch-all, mints a FRESH
    // inference var, and succeeds at a type nothing downstream constrains
    // -- defaulting to Float64 (a complex read needed a hand-written
    // `: Complex128` to build at all, corpus index-types/168). Answer at
    // the element type, exact arity only: a SHORT read still routes to the
    // arm below (whole slots), an over-supplied one still falls through.
    | ArrayElem arrTy when
        not (List.isEmpty tArgs)
        && tArgs.Length > arrTy.IndexTypes.Length
        && tArgs.Length = (arrTy.IndexTypes |> List.sumBy (fun ix -> max 1 ix.Rank))
        && arrTy.IndexTypes |> List.exists (fun ix ->
               ix.Rank >= 2 &&
               (match ix.Symmetry with
                | SymSymmetric | SymAntisymmetric | SymHermitian -> true
                | SymNone | SymWreath -> false)) ->
        if tArgs |> List.exists (fun a -> match a.Kind with TExprWildcard -> true | _ -> false) then
            // A hole frees ONE axis of the group, and a partially-read compact
            // group has no residual class -- the same refusal the wreath arm
            // gives a partial read, and the reason `decompact` exists.
            Error (Other "a wildcard `_` frees one axis of a compact (SymIdx / AntisymIdx / HermitianIdx) group, and a partially-read compact group has no residual class. Supply every coordinate of the group, or decompact(A, d) first and read the freed axis there.")
        else
            foldEnumIdxLabels env arrTy tArgs
            |> Result.bind (fun tArgs ->
            checkArrayIndexTags env tFunc arrTy tArgs
            |> Result.map (fun () ->
                let identity = match tFunc.Kind with TExprVar (_, _, id) -> id | _ -> None
                mkTyped (TExprIndex (tFunc, tArgs, identity)) arrTy.ElemType))
    | ArrayElem arrTy when
        // A compound head takes FLAT subscripts (k for the axis + one per
        // trailing dim), so its arg budget exceeds the slot count -- and it
        // ALWAYS routes here so validateTabulatedIndex owns the flat-count
        // accounting (no silent fresh-var fallthrough for compound heads).
        (match arrTy.IndexTypes with
         | h :: _ when h.IxKind = IxKCompound -> true
         | _ -> tArgs.Length <= arrTy.IndexTypes.Length) ->
        validateTabulatedIndex env arrTy tArgs
        |> Result.bind (fun () ->
        foldEnumIdxLabels env arrTy tArgs
        |> Result.bind (fun tArgs ->
        checkArrayIndexTags env tFunc arrTy tArgs
        |> Result.bind (fun () ->
            let identity = match tFunc.Kind with TExprVar (_, _, id) -> id | _ -> None
            // Tabulated-head consumption: the FIRST slot is one rank-k axis
            // filled by ONE k-tuple at the IR level (classify/compoundRead
            // read the pinned/free split off it).
            //   COMPOUND: flat surface B(c0,...,c(k-1),t...) -- pack the
            //       first k args into a synthetic tuple; j = k always.
            //   SPARSE: tuple surface S((a,_)) arrives as-is; j <= k.
            // Remaining args consume the trailing regular slots.
            let headIsTabulated =
                match arrTy.IndexTypes with
                | h :: _ -> h.IxKind = IxKCompound || h.IxKind = IxKSparse
                | [] -> false
            if headIsTabulated then
                let headSlot = List.head arrTy.IndexTypes
                let trailingSlots = List.tail arrTy.IndexTypes
                let k = headSlot.Rank
                // Flat compound packing: first k args -> one synthetic full
                // tuple (k >= 2; a rank-1 axis keeps its bare scalar arg,
                // which the existing scalar path already consumes as j = 1).
                let tArgs =
                    if headSlot.IxKind = IxKCompound && k >= 2 && tArgs.Length >= k then
                        let coords = tArgs |> List.truncate k
                        let packed =
                            { (List.head coords) with
                                Kind = TExprTuple coords
                                Type = IRTTuple (coords |> List.map (fun c -> c.Type)) }
                        packed :: (tArgs |> List.skip k)
                    else tArgs
                let firstArg = List.head tArgs
                // Wildcard form (full-arity tuple, `_` = free axes):
                // validateTabulatedIndex already enforced full arity + >=1
                // pinned coordinate. CONSUME the holes by rewriting each
                // TExprWildcard to a unit literal, so the wildcard-escape
                // scan sees a hole-free value and codegen reads the
                // pinned/free split directly off the tuple (unit is never a
                // valid coordinate); IRCompoundProject records only the count.
                let wildPositions =
                    match firstArg.Kind with
                    | TExprTuple elems ->
                        elems |> List.mapi (fun i e -> (i, e))
                              |> List.choose (fun (i, e) -> match e.Kind with TExprWildcard -> Some i | _ -> None)
                    | _ -> []
                let firstArg =
                    if List.isEmpty wildPositions then firstArg
                    else
                        match firstArg.Kind with
                        | TExprTuple elems ->
                            let elems' =
                                elems |> List.map (fun e ->
                                    match e.Kind with
                                    | TExprWildcard -> { e with Kind = TExprLit LitUnit; Type = IRTUnit }
                                    | _ -> e)
                            { firstArg with
                                Kind = TExprTuple elems'
                                Type = IRTTuple (elems' |> List.map (fun e -> e.Type)) }
                        | _ -> firstArg
                let tArgs = firstArg :: List.tail tArgs
                // Trailing-dim wildcards: `B((...), _)` leaves the trailing
                // dim FREE, identical to omitting the arg (lex-sorted,
                // trailing-innermost layout). Dropped here so the
                // wildcard-escape scan sees no unconsumed hole; must form a
                // contiguous SUFFIX -- a wildcard BEFORE a supplied trailing
                // index frees an INTERIOR dim, which needs a data restructure
                // and is rejected.
                let keptRemaining, interiorTrailingHole =
                    let isWild (e: TypedExpr) = match e.Kind with TExprWildcard -> true | _ -> false
                    let rec split acc seenWild args =
                        match args with
                        | [] -> (List.rev acc, false)
                        | e :: rest when isWild e -> split acc true rest
                        | e :: rest ->
                            if seenWild then (List.rev acc, true)
                            else split (e :: acc) false rest
                    split [] false (List.tail tArgs)
                let tArgs = firstArg :: keptRemaining
                // j = pinned coordinate count: tuple arity minus free axes for
                // the wildcard form; tuple arity for a short (prefix) tuple; 1
                // for a scalar (rank-1 compound). validateTabulatedIndex already
                // rejected the malformed shapes, so this is well-formed here.
                let j =
                    if not (List.isEmpty wildPositions) then
                        (match firstArg.Kind with
                         | TExprTuple es -> es.Length
                         | _ -> k) - wildPositions.Length
                    else
                        match env.Subst.Resolve firstArg.Type with
                        | IRTTuple tys -> tys.Length
                        | _ -> 1
                // Parent IR reference for the residual extent carrier: a
                // plain variable, or IRLitUnit placeholder for a non-var
                // parent (field access, chained residual) -- harmless since
                // codegen reads the ACTUAL array expr at the IRIndex site;
                // the carrier is only consulted for pass-throughs and
                // tryEvalIntIR (which returns None for it, correctly).
                let parentIR =
                    match tFunc.Kind with
                    | TExprVar (_, vid, _) -> IRVar (vid, tFunc.Type)
                    | _ -> IRLit IRLitUnit
                let residualFragment =
                    tabulatedResidualType headSlot parentIR j (fun () -> env.Builder.FreshId())
                // Remaining args after the compound tuple consume trailing slots.
                let remainingArgs = List.tail tArgs
                let trailingRemaining =
                    if remainingArgs.Length <= List.length trailingSlots then
                        trailingSlots |> List.skip remainingArgs.Length
                    else trailingSlots  // (validateTabulatedIndex/arity guard covers over-supply)
                let finalSlots = residualFragment @ trailingRemaining
                if interiorTrailingHole then
                    Error (Other "A wildcard `_` among the trailing indices of a compound array must come AFTER all supplied trailing indices (a free interior trailing dimension would require restructuring the trailing blocks). Reorder, or leave the trailing dims free by omitting them.")
                elif List.isEmpty finalSlots then
                    Ok (mkTyped (TExprIndex (tFunc, tArgs, identity)) arrTy.ElemType)
                else
                    Ok (mkTyped (TExprIndex (tFunc, tArgs, identity))
                                (mkArrayLike { arrTy with IndexTypes = finalSlots }))
            elif tArgs.Length = arrTy.IndexTypes.Length then
                Ok (mkTyped (TExprIndex (tFunc, tArgs, identity)) arrTy.ElemType)
            else
                let remaining = arrTy.IndexTypes |> List.skip tArgs.Length
                Ok (mkTyped (TExprIndex (tFunc, tArgs, identity))
                            (mkArrayLike { arrTy with IndexTypes = remaining })))))
    | FuncElem (paramTys, retTy) ->
        // WIDTH SCHEMA first, so every check below (and the arity accounting,
        // and the emitted TExprApp) sees the regrouped list: `g(b, c)` against
        // `g(t: Tuple<2>)` is one argument, the pair. No-op unless the callee
        // declares a tuple parameter AND the flat pairing does not fit, so the
        // ordinary call path is untouched.
        let tArgs = regroupArgsByWidth env paramTys tArgs
        // Four checks direct-application would otherwise skip (params are
        // NOT unified against args here, unlike kernel application); each
        // catches a mismatch g++ rejects that Blade would typecheck clean:
        //   irrepsClash  - BLOCK-SPEC (irreps/point-group) pairs must match
        //                  identity, not just extent.
        //   rankClash    - a rank-k compact slot is k emitted dims but ONE
        //                  slot; SymIdx<2,4> vs Idx<10> looks equal otherwise.
        //   unitClash    - unit signatures never meet at a call site
        //                  otherwise; both-sided signatures must agree.
        //   argRankClash - slot-COUNT (rankClash is per-component within a
        //                  slot); collectAppRankErrors re-runs it post-zonk
        //                  for arguments still open here.
        let irrepsClash =
            let n = min paramTys.Length tArgs.Length
            List.zip (List.truncate n paramTys) (List.truncate n tArgs)
            |> List.mapi (fun i pair -> (i, pair))
            |> List.tryPick (fun (i, (pTy, arg)) ->
                match env.Subst.Resolve pTy, env.Subst.Resolve arg.Type with
                | ArrayElem pa, ArrayElem aa when pa.IndexTypes.Length = aa.IndexTypes.Length ->
                    List.zip pa.IndexTypes aa.IndexTypes
                    |> List.tryPick (fun (pi, ai) ->
                        match pi.Tag, ai.Tag with
                        | Some (BlockSpecTag _), Some (BlockSpecTag _) when indexPairIncompatible pi ai ->
                            Some (i, pi, ai)
                        | _ -> None)
                | _ -> None)
        let rankClash =
            let n = min paramTys.Length tArgs.Length
            List.zip (List.truncate n paramTys) (List.truncate n tArgs)
            |> List.mapi (fun i pair -> (i, pair))
            |> List.tryPick (fun (i, (pTy, arg)) ->
                match env.Subst.Resolve pTy, env.Subst.Resolve arg.Type with
                | ArrayElem pa, ArrayElem aa when pa.IndexTypes.Length = aa.IndexTypes.Length ->
                    List.zip pa.IndexTypes aa.IndexTypes
                    |> List.mapi (fun slot pair -> (slot, pair))
                    |> List.tryPick (fun (slot, (pi, ai)) ->
                        if indexRankDiffers pi ai then Some (i, slot, pi, ai) else None)
                | _ -> None)
        // POINT THE CARET AT THE ARGUMENT THE MESSAGE NAMES. `currentExprSpan`
        // is stamped by `inferExpr` on entry to EVERY node and the last stamp
        // wins (TypeEnv.locateError), so by the time these checks run it holds
        // the LAST argument inferred -- the text said "argument 4" while the
        // caret underlined argument 8 (`examples/lswosa.blade`'s BL3010, and
        // every other argument-indexed refusal below shares the defect). Each
        // check already knows the offending index; re-stamp before building the
        // error so the two agree. No-op when the argument carries no span
        // (synthesized nodes), which leaves the previous behaviour intact.
        let atArg (i: int) =
            if i >= 0 && i < tArgs.Length then
                let s = (List.item i tArgs).Span
                if s.StartLine > 0 then setCurrentExprSpan s
        let unitClash =
            let n = min paramTys.Length tArgs.Length
            // (sig, concrete): `concrete` is false while the (element) type is
            // still an unresolved inference variable — the strict quantity
            // check below must not reject a type that simply isn't KNOWN yet
            // (e.g. a kernel body calling a helper before params are bound).
            let sigOf t =
                match env.Subst.Resolve t with
                | ArrayElem at ->
                    let e = env.Subst.Resolve at.ElemType
                    (IR.getUnits e, (match e with IRTInfer _ -> false | _ -> true))
                | IRTInfer _ -> (None, false)
                | resolved -> (IR.getUnits resolved, true)
            let describeArg (au: UnitSig option) =
                match au with
                | None -> "bare (it carries no unit signature)"
                | Some a ->
                    match a.Nominal with
                    | Some qn -> sprintf "the quantity '%s'" qn
                    | None -> sprintf "structurally dimensioned (%s)" (ppUnitSig a)
            List.zip (List.truncate n paramTys) (List.truncate n tArgs)
            |> List.mapi (fun i (pTy, arg) -> (i, sigOf pTy, sigOf arg.Type))
            |> List.tryPick (fun (i, (pu, _), (au, aConcrete)) ->
                match pu, au with
                | Some pu, Some au when not (unitCompatible pu au) ->
                    atArg i
                    Some (UnitMismatch (sprintf "argument %d" (i + 1), ppUnitSig pu, ppUnitSig au))
                // Convertible but at a different MAGNITUDE. Argument passing
                // is a seam that does not (yet) insert a factor, so name the
                // difference instead of handing the callee a raw number in
                // the wrong magnitude.
                | Some pu, Some au when not (unitSameScale pu au) ->
                    atArg i
                    Some (Other (sprintf
                            "argument %d expects %s but got %s: same dimensions, magnitudes differing by the factor %s"
                            (i + 1) (ppUnitSig pu) (ppUnitSig au)
                            (ppUnitScale (unitConversionFactor au pu))))
                // STRICT quantity slots (BL3010): a parameter declared with a
                // QUANTITY (Nominal = Some) rejects any CONCRETE argument not
                // carrying that nominal — bare and structurally-dimensioned
                // args alike; the caller must ascribe. Structural (None)
                // parameters keep the permissive behavior above exactly.
                | Some pu, _ when pu.Nominal.IsSome
                                  && aConcrete
                                  && (match au with
                                      | Some a -> a.Nominal <> pu.Nominal
                                      | None -> true) ->
                    atArg i
                    Some (QuantityArgMismatch (i + 1, pu.Nominal.Value, describeArg au))
                | _ -> None)
        // ONE LEVEL INTO A TUPLE ARGUMENT. Every check above reads
        // `IR.getUnits` / `ArrayElem` off the argument AS A WHOLE, and both
        // answer "nothing here" for an `IRTTuple` -- so with a COMPONENT-TYPED
        // tuple parameter (`Tuple<U^1, T<time>^1>`, or the identical written
        // `(U^1, T<time>^1)`) a wrong unit or a wrong shape INSIDE a component
        // was invisible at this seam and surfaced as a g++ failure. Written
        // component types exist precisely so that they can be checked, so they
        // get the same two judgements their top-level twins get.
        //
        // Folded into `unitClash` rather than added as a fifth clash so the
        // arm structure below is untouched; the top-level verdict still wins.
        //
        // ONE level only (6c's one-level structural rule), and only when the
        // widths already agree -- a width disagreement is unify's ordinary
        // equal-length `IRTTuple` refusal and reads better from there. Both
        // sides must be CONCRETE: a `Tuple<N>` parameter's element slots are
        // fresh inference variables, so the width-only spelling keeps exactly
        // today's behaviour and only the written spelling gains the check.
        let unitClash =
            match unitClash with
            | Some _ -> unitClash
            | None ->
                let n = min paramTys.Length tArgs.Length
                let shapeOf t =
                    match env.Subst.Resolve t with
                    | ArrayElem at -> (at.IndexTypes.Length, env.Subst.Resolve at.ElemType)
                    | r -> (0, r)
                // "Concrete" must see through the UNIT wrapper. A `T<day>^1`
                // annotation produces an inference variable wearing a unit
                // (`IRTUnitAnnotated(IRTInfer _, day)`), and a bare `IRTInfer`
                // test reads that as concrete -- so the rank comparison below
                // fired against a rank inference had not determined yet and
                // rejected a correct program. The identical program without
                // units passed, which is the tell: units must not decide
                // whether a type is known, only what it measures.
                let isConcrete t =
                    match IR.stripUnits (env.Subst.Resolve t) with
                    | IRTInfer _ -> false
                    | _ -> true
                List.zip (List.truncate n paramTys) (List.truncate n tArgs)
                |> List.mapi (fun i (pTy, arg) -> (i, pTy, arg))
                |> List.tryPick (fun (i, pTy, arg) ->
                    match env.Subst.Resolve pTy, env.Subst.Resolve arg.Type with
                    | IRTTuple pcs, IRTTuple acs when pcs.Length = acs.Length ->
                        List.zip pcs acs
                        |> List.mapi (fun j (pc, ac) -> (j, pc, ac))
                        |> List.tryPick (fun (j, pc, ac) ->
                            let where = sprintf "argument %d, component %d" (i + 1) (j + 1)
                            let (pr, pe) = shapeOf pc
                            let (ar, ae) = shapeOf ac
                            if not (isConcrete (env.Subst.Resolve pc)) then None
                            else
                            match IR.getUnits pe, IR.getUnits ae with
                            | Some pu, Some au when not (unitCompatible pu au) ->
                                atArg i
                                Some (UnitMismatch (where, ppUnitSig pu, ppUnitSig au))
                            | _ when isConcrete ae && pr <> ar ->
                                atArg i
                                Some (Other (sprintf
                                        "%s: the parameter component is declared %s (rank %d) but the argument component is %s (rank %d). A call site performs no conversion between these -- pass a value of the declared type, or change the declared component type."
                                        where (ppIRType (env.Subst.Resolve pc)) pr
                                        (ppIRType (env.Subst.Resolve ac)) ar))
                            | _ -> None)
                    | _ -> None)
        // A Poly<T^r> pack param makes the arrow variadic -- its declared
        // param count says nothing about legal call-site arg counts, so
        // arity accounting stands down (monomorphization owns the call).
        let isVariadic =
            paramTys |> List.exists (fun t ->
                match env.Subst.Resolve t with IRTPoly _ -> true | _ -> false)
        let argRankClash = firstArgRankClash env.Subst paramTys (tArgs |> List.map (fun a -> a.Type))
        match irrepsClash, rankClash, unitClash, argRankClash with
        | Some (i, pi, ai), _, _, _ ->
            atArg i
            // O(3) member gets a named message; pg-vs-pg or a cross-member
            // pair gets the family-level twin naming the discipline instead.
            (match pi.Tag, ai.Tag with
             | Some (IrrepsTag _), Some (IrrepsTag _) ->
                 Error (IrrepsIdxArgMismatch (i + 1, ppIndexType pi, ppIndexType ai))
             | _ ->
                 Error (BlockSpecArgMismatch (i + 1, ppIndexType pi, ppIndexType ai)))
        | None, Some (i, slot, pi, ai), _, _ ->
            atArg i
            Error (IndexRankMismatch (sprintf "argument %d, index slot %d" (i + 1) slot,
                                      ppIndexType pi, max 1 pi.Rank,
                                      ppIndexType ai, max 1 ai.Rank))
        | None, None, Some unitErr, _ ->
            Error unitErr
        | None, None, None, Some (i, pr, ar, pTy, aTy) ->
            atArg i
            Error (ArgRankMismatch (i + 1, pr, ar,
                                    ppIRType (env.Subst.Resolve pTy),
                                    ppIRType (env.Subst.Resolve aTy)))
        | None, None, None, None ->
            // The FIFTH check, last because every one above it names the
            // defect more precisely: element CLASS. See firstArgTypeClash.
            match firstArgTypeClash env.Subst paramTys (tArgs |> List.map (fun a -> a.Type)) with
            | Some (i, pTy, aTy) ->
                atArg i
                let callee =
                    match tFunc.Kind with
                    | TExprVar (name, _, _) -> sprintf "'%s'" name
                    | _ -> "this function"
                Error (ArgTypeMismatch (i + 1, callee,
                                        ppIRType (env.Subst.Resolve pTy),
                                        ppIRType (env.Subst.Resolve aTy)))
            | None ->
            // Rank propagation (the INFERENCE half of argRankClash's
            // CHECKING): impose the callee param's rank as a LOWER BOUND on
            // still-unresolved argument vars, so an unannotated caller param
            // learns the callee's rank demand instead of the typechecker
            // staying quiet and codegen emitting ill-typed C++.
            (let n = min paramTys.Length tArgs.Length
             List.zip (List.truncate n paramTys) (List.truncate n tArgs)
             |> List.iter (fun (pTy, arg) ->
                 let calleeRank =
                     match env.Subst.Resolve pTy with
                     | ArrayElem pa -> pa.IndexTypes.Length
                     | IRTInfer pid -> env.Subst.GetRankLowerBound(pid) |> Option.defaultValue 0
                     | _ -> 0
                 if calleeRank > 0 then
                     match env.Subst.Resolve arg.Type with
                     | IRTInfer aid -> env.Subst.AddRankLowerBound(aid, calleeRank)
                     | _ -> ()))
            if isVariadic then
                Ok (mkTyped (TExprApp (tFunc, tArgs)) retTy)
            elif tArgs.Length > paramTys.Length then
                // Curried over-application: this arrow consumes its declared
                // params; the remainder re-dispatches against the result
                // type (function curries on, array falls into indexing,
                // scalar is a plain arity error).
                let now, rest = List.splitAt paramTys.Length tArgs
                let head = mkTyped (TExprApp (tFunc, now)) retTy
                match env.Subst.Resolve retTy with
                | FuncElem _ | ArrayElem _ -> dispatchAppOrIndex env head rest
                | _ -> Error (ArityMismatch (paramTys.Length, tArgs.Length))
            elif tArgs.Length < paramTys.Length then
                // Not partial application: ExprApp eta-expands 0 < k < n
                // before dispatching, so this is `f()` on an n-ary function
                // or an under-applied struct field -- genuine arity errors.
                // "Too few" means fewer than the REQUIRED params: for a
                // defaults-carrying callee (whose fills the desugar already
                // handled for required <= k < total) report the required
                // count, not the full param count.
                let expectedMin =
                    match tFunc.Kind with
                    | TExprVar (name, _, _) ->
                        (match env.FuncDefaults.TryGetValue name with
                         | true, ps ->
                             ps |> List.takeWhile (fun (_, _, d) -> Option.isNone d) |> List.length
                         | _ -> paramTys.Length)
                    | _ -> paramTys.Length
                Error (ArityMismatch (expectedMin, tArgs.Length))
            else
                Ok (mkTyped (TExprApp (tFunc, tArgs)) retTy)
    | _ ->
        let retTy = env.Subst.Fresh()
        Ok (mkTyped (TExprApp (tFunc, tArgs)) retTy)

/// Structural child enumerator for a typed expression: the immediate
/// sub-expressions of a node, total over TExpr kinds. Shared by the
/// tag-check revalidation walk and the wildcard-escape scan so the two
/// never drift.
// Public: Ide.fs walks the zonked typed tree with this to collect builtin
// call-site instantiations (calls[] in `ide check --json`).
let typedExprChildren (expr: TypedExpr) : TypedExpr list =
        match expr.Kind with
        | TExprLit _ | TExprVar _ | TExprQualified _ | TExprSection _
        | TExprWildcard
        | TExprZero | TExprRange _ | TExprReverse _ | TExprArity _ -> []
        | TExprUnaryOp (_, e) -> [e]
        | TExprBinOp (_, _, l, r) -> [l; r]
        | TExprApp (f, args) -> f :: args
        | TExprTupleIndex (t, i) -> [t; i]
        | TExprPolyTail (p, _) -> [p]
        | TExprField (e, _, _) -> [e]
        | TExprLambda info -> [info.Body]
        | TExprLet (_, _, v, b) -> [v; b]
        | TExprMatch (s, cases) ->
            s :: (cases |> List.collect (fun c ->
                c.Body :: (Option.toList c.Guard)))
        | TExprIf (c, t, e) -> [c; t; e]
        | TExprTuple es | TExprArrayLit (es, _) | TExprZip es | TExprStack es
        | TExprSequence es -> es
        | TExprJoin (es, _) -> es
        | TExprComplexLit (re, im) -> [re; im]
        | TExprMethodFor info -> info.Arrays
        | TExprObjectFor info -> [info.Kernel]
        | TExprApply info -> info.Loop :: info.Kernel :: info.Arrays
        | TExprBind (a, b) | TExprParallel (a, b) | TExprFusion (a, b)
        | TExprChoice (a, b) -> [a; b]
        | TExprFallback (a, b) -> [a; b]
        | TExprFunctorMap (f, c) -> [f; c]
        | TExprCompose (_, l, r) -> [l; r]
        | TExprDotDot (lo, hi) -> [lo; hi]
        | TExprBlocked (_, bs) -> [bs]
        | TExprPure e | TExprCompute e | TExprRead e | TExprFillRandom e | TExprRank e
        | TExprExtents e | TExprReynolds (e, _) -> [e]
        | TExprRandGen (_, key, _) -> [key]
        | TExprGuard (c, b) -> [c; b]
        | TExprMask (a, p) | TExprIntersect (a, p) | TExprUnion (a, p)
        | TExprGroupBy (a, p) | TExprSort (a, p)
        | TExprCompound (a, p) | TExprSparse (a, p) -> [a; p]
        | TExprReduce (a, p, i) -> [a; p] @ Option.toList i
        | TExprProdSum args -> args
        | TExprUnique a -> [a]
        | TExprTranspose (a, _, _) -> [a]
        | TExprDecompact (a, _) -> [a]
        | TExprGram (l, r, _) -> [l; r]
        | TExprMatmul (l, r) -> [l; r]
        | TExprEigh a -> [a]
        | TExprSolve (a, b) -> [a; b]
        | TExprArrayNegate a -> [a]
        | TExprArrayConjugate a -> [a]
        | TExprContains (a, v) -> [a; v]
        | TExprDisplayEmit (_, _, d, _) -> [d]
        | TExprDisplayJson (_, d) -> [d]
        | TExprDisplayNum d -> [d]
        | TExprGroupKeys keys -> keys
        | TExprStruct (_, fields) -> fields |> List.map snd
        | TExprIndex (arr, idxs, _) -> arr :: idxs
        | TExprBlock (stmts, final) ->
            let rec stmtExprsOf (s: TypedStmt) : TypedExpr list =
                match s with
                | TStmtLet b -> [b.Value]
                | TStmtAssign (l, r) -> [l; r]
                | TStmtExpr e -> [e]
                | TStmtForIn (_, _, lo, hi, body) ->
                    lo :: hi :: (body |> List.collect stmtExprsOf)
            (stmts |> List.collect stmtExprsOf) @ Option.toList final
        | TExprAssign (l, r) -> [l; r]
        | TExprConstraintCheck (c, _) -> [c]
        | TExprReplicate (c, b) -> [c; b]
        | TExprAlign (es, _) -> es
        | TExprPartialApp (_, a, _) -> [a]

/// Warn when a function-level `omp(p: n)` is being read as a licence for a
/// loop the function generates INTERNALLY over `p`.
///
/// THE RULE (owner, 2026-08-08): an `omp` clause licenses the EXTERNAL S-dims
/// an argument contributes -- the CALLER's co-iteration over that parameter
/// when the function is used in kernel position (`object_for(f) <@> (..)`,
/// `method_for(..) <@> f`, where the clause is surfaced onto the eta wrapper
/// from `FuncParallel`). It says nothing about loops the body itself builds:
/// those belong to the kernel of the apply that builds them, and are licensed
/// by a clause on THAT kernel.
///
/// The two are indistinguishable in the emitted C++ -- "asked for omp on the
/// inner loop and got serial" and "never asked" are byte-identical -- so the
/// misconception is silent, which is what this diagnostic is for. It fires
/// only on the actionable shape: the licensed parameter is an OPERAND of an
/// apply inside the body AND that apply's kernel carries no parallel clause of
/// its own. A body that already spells the inner licence is left alone (the
/// function-level clause is then doing its real, external job), as is a
/// parameter the body never iterates.
let checkOmpInternalLoop (env: TypeEnv) (paramNames: string list)
                         (whereClause: WhereClause option) (owner: string)
                         (body: TypedExpr) : unit =
    let licensed =
        match whereClause with
        | Some wc ->
            wc.Parallel |> List.collect (function
                | Omp o -> o.Vars |> List.map fst |> List.filter (fun v -> List.contains v paramNames)
                | _ -> [])
        | None -> []
    if not (List.isEmpty licensed) then
        // An apply whose kernel declares no parallel strategy: the only shape
        // where surfacing the misconception is actionable.
        // One-hop binding resolution, spelled locally: `resolveTypedExpr` lives
        // in the inference rec-chain far below this point, and this walk needs
        // nothing more than its `let`-bound-value hop (`let k = lambda ..` in
        // the kernel slot).
        let resolveHop (e: TypedExpr) =
            match e.Kind with
            | TExprVar (name, _, _) ->
                (match lookupVar name env with
                 | Some info -> info.TypedValue |> Option.defaultValue e
                 | None -> e)
            | _ -> e
        let unlicensedApplyOver (pname: string) (e: TypedExpr) =
            match e.Kind with
            | TExprApply info ->
                let namesParam (a: TypedExpr) =
                    match a.Kind with
                    | TExprVar (n, _, _) -> n = pname
                    | _ -> false
                let kernelAsksParallel =
                    match (resolveHop info.Kernel).Kind with
                    | TExprLambda li -> not (List.isEmpty li.Parallel)
                    | TExprVar (fn, _, _) ->
                        (match env.FuncParallel.TryGetValue fn with
                         | true, (_, s) -> not (List.isEmpty s)
                         | _ -> false)
                    | _ -> false
                if (info.Arrays |> List.exists namesParam) && not kernelAsksParallel
                then Some e.Span else None
            | _ -> None
        let rec findFirst (pname: string) (e: TypedExpr) : Span option =
            match unlicensedApplyOver pname e with
            | Some sp -> Some sp
            | None -> typedExprChildren e |> List.tryPick (findFirst pname)
        licensed |> List.iter (fun v ->
            match findFirst v body with
            | Some sp ->
                // BL4001 (constraint violation), the same class as the
                // names-no-parameter warning: a `where` conjunct that does not
                // mean what it was written to mean.
                emitWarning env "BL4001" sp
                    (sprintf "omp(%s: ...) on %s licenses the CALLER's iteration over `%s` (the S-dims an argument contributes when this is used as a kernel), not the loop over `%s` built inside this body. That loop is licensed by a clause on its OWN kernel -- write `%s <@> lambda(..) where omp(..) -> ..`. As written the inner loop is emitted SERIAL."
                             v owner v v v)
            | None -> ())

/// True if any node in the typed subtree still has an UNRESOLVED type (an
/// inference variable, possibly under a unit-annotation wrapper).
///
/// NOT the right predicate for unit provisionality -- use
/// `typedExprHasProvisionalUnits` below. This one stops at a let-bound var
/// (resolved type, no children) and so cannot see an unresolved param inside
/// that var's defining expression, which is how the SAME bug was reported four
/// separate times before the chasing version replaced it at both deferral
/// sites. Currently unused; kept as the plain structural query it is.
let rec typedExprHasUnresolvedType (env: TypeEnv) (expr: TypedExpr) : bool =
    let rec tyUnresolved (t: IRType) =
        match env.Subst.Resolve t with
        | IRTInfer _ -> true
        | IRTUnitAnnotated (inner, _) | IRTIdxTagged (inner, _) -> tyUnresolved inner
        | _ -> false
    tyUnresolved expr.Type
    || (typedExprChildren expr |> List.exists (typedExprHasUnresolvedType env))

/// THE deferral trigger, shared by every "is this unit signature still
/// PROVISIONAL?" site. `typedExprHasUnresolvedType` answers a strictly weaker
/// question -- "does some NODE here still have an inference-variable type" --
/// and misses the shape that produced three separate bug reports: a value
/// reached through a LET, whose cached type is perfectly resolved and
/// nonetheless provisional.
///
///     let w = two_pi * fq        // fq unresolved => w types BARE Float64
///     cos(w * t_zero)            // judged 1 * day = day, rejects; but once
///                                // fq binds, w is 1/day and it cancels
///
/// `w`'s node is resolved and `TExprVar` has no children, so the walk stops
/// there and never sees `fq`. Chase let-bound vars into their defining
/// expression (`VarInfo.TypedValue`, matched on VarId so shadowing cannot
/// redirect the chase); a visited set plus a fuel cap keep a self-referential
/// binding from looping.
///
/// Deliberately still a walk over the SUBTREE, not a blanket "we are inside a
/// lambda": `cos(tz)` on a resolved dimensioned capture depends on nothing
/// provisional and must keep rejecting where it is written, because a lambda
/// that is never `<@>`-applied never runs a second pass to catch it.
let typedExprHasProvisionalUnits (env: TypeEnv) (expr: TypedExpr) : bool =
    let rec go (fuel: int) (seen: Set<IRId>) (e: TypedExpr) : bool =
        if fuel <= 0 then false
        else
            let rec tyUnresolved (t: IRType) =
                match env.Subst.Resolve t with
                | IRTInfer _ -> true
                | IRTUnitAnnotated (inner, _) | IRTIdxTagged (inner, _) -> tyUnresolved inner
                | _ -> false
            tyUnresolved e.Type
            || (match e.Kind with
                | TExprVar (name, varId, _) when not (Set.contains varId seen) ->
                    (match lookupVar name env with
                     | Some info when info.VarId = varId ->
                         (match info.TypedValue with
                          | Some v -> go (fuel - 1) (Set.add varId seen) v
                          | None -> false)
                     | _ -> false)
                | _ -> false)
            || (typedExprChildren e |> List.exists (go (fuel - 1) seen))
    go 64 Set.empty expr

/// True if the typed expression contains an unconsumed wildcard hole anywhere
/// in its subtree. A wildcard is legitimate only as a compound-index coordinate,
/// where dispatchAppOrIndex consumes it into a residual node before it reaches a
/// value-forming boundary. Any TExprWildcard still present here has escaped into
/// a value (bound to a name, returned, nested in a non-consuming call) and is an
/// error. Local check: called at value-forming boundaries, not threaded through
/// the AST.
let rec private exprContainsWildcard (expr: TypedExpr) : bool =
    match expr.Kind with
    | TExprWildcard -> true
    | _ -> typedExprChildren expr |> List.exists exprContainsWildcard

/// Re-run the tag-check at every TExprIndex site reachable from `expr`,
/// after buildApplyInfo's kernel-parameter unification pins previously-open
/// inference variables to nominally-tagged types (the eager check in
/// dispatchAppOrIndex saw them as IRTInfer and let them through). Without
/// this, indexing through an iteration-tagged kernel parameter -- e.g.
/// `lambda(r) -> by_country(r)` where `r` iterates `Array<RegionIdx like
/// StationIdx>` but `by_country` expects CountryIdx -- would silently
/// typecheck. Walks structurally, short-circuiting on the first error.
let rec private revalidateBodyTagChecks (env: TypeEnv) (expr: TypedExpr) : TypeResult<unit> =
    // Recurse into children first, short-circuiting on error.
    let childRes =
        typedExprChildren expr
        |> List.fold (fun acc child ->
            acc |> Result.bind (fun () -> revalidateBodyTagChecks env child))
            (Ok ())
    childRes |> Result.bind (fun () ->
        match expr.Kind with
        | TExprIndex (arr, args, _) ->
            match env.Subst.Resolve arr.Type with
            | ArrayElem at when args.Length <= at.IndexTypes.Length ->
                checkArrayIndexTags env arr at args
            | _ -> Ok ()
        | _ -> Ok ())

/// Scalar math intrinsics -- the canonical list lives in Grad.fs (which also
/// carries the derivative rules); StaticEval.evalBuiltin mirrors the same
/// names for static contexts.
let isMathIntrinsic (name: string) : bool = Blade.Grad.isMathIntrinsic name

/// Whitelist subset permitted on complex operands (has a std::complex overload).
let isComplexMathIntrinsic (name: string) : bool = Blade.Grad.isComplexMathIntrinsic name

/// Every intrinsic reachable as a PLAIN CALL: the Grad-listed scalar
/// intrinsics plus the ones with their own inferExpr arms (abs, which
/// preserves its operand's numeric type, and the complex accessors
/// real/imag/arg). All are arity-1 -- which is what lets
/// etaExpandFunctionKernel wrap one in kernel position without a declared
/// signature to read the arity from.
let isUnaryIntrinsic (name: string) : bool =
    isMathIntrinsic name || name = "abs" || name = "real" || name = "imag" || name = "arg"

/// BINARY plain-call intrinsics (atan2, log_base). Kept out of
/// `isUnaryIntrinsic` on purpose: that predicate is what tells
/// `etaExpandFunctionKernel` the arity is 1, so a binary name listed there
/// would eta-expand to a one-parameter lambda and then fail arity inside its
/// own body. The canonical list lives in Grad.fs beside the adjoint rules.
let isBinaryIntrinsic (name: string) : bool = Blade.Grad.isBinaryMathIntrinsic name

/// Rejection message shared by the two orientations of the same unimplemented
/// shape: a `zip(...)` sitting beside other arrays in ONE operand pack
/// (`object_for(k) <@> (A, zip(B, C))`, `method_for(A, zip(B, C)) <@> k`).
/// A zip should contribute ONE axis and deliver k values to the kernel; the
/// object_for path instead flattened it into the pack (a silent extra outer
/// axis) and the method_for path carried it to codegen unmaterialized.
/// Single-operand zip application is the supported co-iteration form.
let zipInMultiArrayPackMsg =
    "zip cannot appear as one operand of a multi-array loop; co-iterating a zip inside an outer loop is not yet supported -- hoist the zip to its own <@>, or pass the zipped arrays as separate operands"

/// A variable is a provider-module alias when it is bound opaque to a
/// registered provider's module name (`import netcdf as nc` binds
/// nc : IRTNamed "netcdf"). Returns the registry name for dispatch.
let providerAliasName (env: TypeEnv) (alias: string) : string option =
    match lookupVar alias env with
    | Some vi ->
        (match env.Subst.Resolve(vi.Type) with
         | IRTNamed pn when (Blade.ProviderRegistry.tryFind pn).IsSome -> Some pn
         | _ -> None)
    | None -> None

/// Stamp a source expression's span onto a node built by calling a former
/// builder (inferObjectFor / inferMethodFor) DIRECTLY -- those bypass
/// inferExpr, so they miss its span back-fill and would return noSpan.
/// Consumers that need a location: the stage-3/4 BL4010 pin suggestion (which
/// otherwise has nothing to anchor on) and the editor's call-site walk.
/// Existing spans are never overwritten.
let stampSynthSpan (src: Expr) (te: TypedExpr) : TypedExpr =
    if te.Span.StartLine = 0 && src.Span.StartLine > 0 then { te with Span = src.Span } else te

/// May an elementwise kernel INHERIT the compact storage class of the array it
/// maps over? Each compact class stores one triangle and reconstructs the
/// other through a mirror involution applied to the VALUE, so a map keeps
/// the class exactly when it COMMUTES with that involution:
///
///   SymSymmetric      mirror = identity      -> any map preserves it
///   SymAntisymmetric  mirror = negation      -> needs a sign-ODD kernel
///   SymHermitian      mirror = conjugation   -> needs a conj-commuting kernel
///
/// The only UNARY seam asking this (the parity engine handles binary
/// pair-swap deduction elsewhere); `deduceOutputType`'s rank-0 elementwise
/// arm and the `<$>` functor arm copy the input's Symmetry through for ANY
/// kernel otherwise, which for an antisymmetric input is a silent
/// miscompile: the result stores a strict upper triangle and every mirrored
/// read applies NegateOnSwap, so `C <@> (v * v)` reads out(1,0) as -out(0,1)
/// when the truth is +out(0,1).
///
/// No third answer ("demote to symmetric/dense"): ITERATION is fixed by the
/// INPUT record, so an antisymmetric input drives the STRICT simplex and
/// writes C(n,r) cells -- neither the diagonal a symmetric map would need
/// nor the full square a dense one would is a shape that nest can fill.
/// Refusing is the only sound answer; an unprovable kernel is an error with
/// a decompact-first fix, not a downgrade.
///
/// `argPos` is the kernel parameter receiving the compact array's cells;
/// `signParities`/`conjCommutes` are its per-parameter summaries. `wreathLevels`
/// is the class's level list when cls = SymWreath: a wreath's mirror is the
/// PRODUCT of its per-level characters, so the answer depends on levels, not
/// the class name alone -- the one place in this file where that is true.
let compactClassInheritError
        (cls: SymmetryClass)
        (wreathLevels: (int * bool) list)
        (argPos: int)
        (paramName: string)
        (signParities: Blade.Deduce.SignParity list)
        (conjCommutes: bool list) : TypeError option =
    match cls with
    | SymNone | SymSymmetric -> None
    // An all-'+' wreath's mirror is the identity, exactly like SymSymmetric, so
    // any map preserves it. A single '-' level anywhere makes the mirror carry a
    // negation, and then the SAME sign-ODD certificate the depth-1
    // antisymmetric case needs applies -- an inner '-' level must not silently
    // skip BL4015 just because the outer class is spelled differently.
    // (A wreath output is currently refused at the storage boundary before
    // this can fire; the gate is here so it is RIGHT if/when that refusal
    // lifts.)
    | SymWreath when wreathLevels |> List.forall snd -> None
    | SymWreath ->
        (match List.tryItem argPos signParities with
         | Some Blade.Deduce.SOdd -> None
         | Some Blade.Deduce.SEven ->
             Some (AntisymMapNotOdd (paramName, "provably sign-EVEN (f(-x) = f(x))"))
         | _ -> Some (AntisymMapNotOdd (paramName, "of UNKNOWN sign parity")))
    | SymAntisymmetric ->
        (match List.tryItem argPos signParities with
         | Some Blade.Deduce.SOdd -> None
         | Some Blade.Deduce.SEven ->
             Some (AntisymMapNotOdd (paramName, "provably sign-EVEN (f(-x) = f(x))"))
         | _ -> Some (AntisymMapNotOdd (paramName, "of UNKNOWN sign parity")))
    | SymHermitian ->
        (match List.tryItem argPos conjCommutes with
         | Some true -> None
         | _ -> Some (HermitianMapNotReal paramName))

/// The QUANTITY nominal a surface type annotation names, when it names one:
/// a bare quantity (`levels`) or a tagged base (`Float<speed>`,
/// `Int64<levels>`, `String<title>`, ...). Structural units (Nominal = None)
/// and non-unit names answer None.
let private surfaceTypeQuantity (env: TypeEnv) (ty: TypeExpr) : string option =
    let quantityOf name =
        match Map.tryFind name env.Units with
        | Some (s: UnitSig) -> s.Nominal
        | None -> None
    match ty with
    | TyNamed (q, []) -> quantityOf q
    | TyNamed (_, [TyNamed (q, [])]) -> quantityOf q
    | _ -> None

/// The QUANTITY nominal a call ARGUMENT carries, judged at surface level:
/// an ascription (`20 : levels`, `v : Float<speed>`) or a variable whose
/// (resolved) type already carries the nominal. Compound expressions and
/// call results are NOT probed -- they route positionally unless ascribed.
let private argQuantityTag (env: TypeEnv) (a: Expr) : string option =
    match a.Kind with
    | ExprKind.ExprTyped (_, ty) -> surfaceTypeQuantity env ty
    | ExprKind.ExprVar n ->
        (match lookupVar n env with
         | Some vi ->
             (match IR.getUnits (env.Subst.Resolve vi.Type) with
              | Some u -> u.Nominal
              | None -> None)
         | None -> None)
    | _ -> None

/// Well-formedness check for unit annotations in TYPE position -- the
/// annotation twin of registerUnit's rules, raising the SAME two codes:
///   BL3011: a quantity name inside a COMPOUND unit expression
///     (`Float<speed/second>`, `Float<speed^2>`). The nominal layer is
///     exactly one level deep, so only a LONE quantity name
///     (`Float<speed>`) may appear in a type argument.
///   BL3015: a name that resolves to no unit at all (`Float<meter/secnd>`).
///     Only a numeric LITERAL may appear in a unit expression undeclared;
///     an identifier must already name something. Without this the
///     annotation degrades to the BARE type, so the value silently carries
///     no unit and every later check on it passes.
/// Both are checked only where the parser already committed to unit syntax
/// (`TyUnitExpr` -- see isUnitExprArg: a lone name and `name^INT` are NOT
/// claimed, since they collide with `Float<speed>` and `T^2`). Descends
/// aggregate component positions the way boundedAggregateError does.
/// Lowering itself stays TOTAL (it degrades to the bare base type), so the
/// annotation CONSUMERS -- ascriptions, let annotations, function signatures
/// -- call this to surface the error.
/// Scalar bases whose type argument is a UNIT (or index-tag) slot -- mirrors
/// the dispatch in lowerTypeExpr. These constructors take no GENERIC
/// parameter, which is what makes the slot checkable: the only things that
/// can inhabit it are a unit, a quantity, an index-type or enum tag
/// (`Nat<LatIdx>`), and the tag wildcard `_`. A name that is none of those is
/// a misspelling, so here -- and ONLY here -- `name` and `name^INT` can be
/// rejected without colliding with `Float<speed>` and `T^2`.
/// (`Char`/`Void` take no argument at all, so they are absent.)
let private unitSlotBases =
    Set.ofList
        [ "Int"; "Int32"; "Int64"; "Float"; "Float64"; "Double"; "Float32"
          "Complex64"; "Complex128"; "Bool"; "Nat"; "String" ]

let rec private unitAnnoError (env: TypeEnv) (ty: TypeExpr) : TypeError option =
    let quantityIn name =
        match Map.tryFind name env.Units with
        | Some (s: UnitSig) -> s.Nominal
        | None -> None
    let rec inUnitExpr (ue: UnitExpr) : TypeError option =
        match ue with
        | UnitNamed n ->
            match Map.tryFind n env.Units with
            | Some (s: UnitSig) ->
                s.Nominal |> Option.map (fun q -> QuantityTerminal (q, unitAnnoContext))
            | None when unitScaleConstants.ContainsKey n -> None
            | None ->
                Some (UnknownUnitName (n, unitAnnoContext, unitSpellingCandidates env.Units n))
        | UnitMul (a, b) | UnitDiv (a, b) ->
            inUnitExpr a |> Option.orElseWith (fun () -> inUnitExpr b)
        | UnitPow (a, _) -> inUnitExpr a
        // A magnitude names nothing, so neither rule can fire on it.
        | UnitOne | UnitScaleLit _ -> None
    match ty with
    | TyUnitExpr ue -> inUnitExpr ue
    | TyVar (name, Some _) ->
        // `Float<speed^2>` parses as a rank-marked type var; a quantity name
        // there is the power spelling of the same terminality violation.
        // BL3015 is NOT raised from this arm -- reached from an arbitrary
        // position, an unresolvable name here is the `T^2` type-variable
        // spelling. It is raised from the unitSlotBases arm below, which
        // knows the slot cannot hold a type variable.
        quantityIn name |> Option.map (fun q -> QuantityTerminal (q, unitAnnoContext))
    | TyNamed (ctor, args) when unitSlotBases.Contains ctor ->
        args |> List.tryPick (fun arg ->
            match arg with
            | (TyNamed (argName, []) | TyVar (argName, Some _))
                    when not (Map.containsKey argName env.Units)
                         && (lookupTypeDef argName env).IsNone ->
                // `Float<secnd>`, `Float<secnd^2>`, `Int32<secnd>`: the name
                // resolves to no unit, quantity, index type, or enum, and the
                // slot admits nothing else. Any typedef at all suppresses
                // this -- the conservative direction. `Nat<_>` parses as
                // TyWildcard and never matches here.
                Some (UnknownUnitName
                        (argName, unitAnnoContext, unitSpellingCandidates env.Units argName))
            | _ -> unitAnnoError env arg)
    // CARET-FREE `T<u>` (owner ruling, 2026-08-09): `T<u>` IS `T<u>^0`, so a
    // misspelled unit under it must be the same BL3015 the caret arm below
    // raises -- otherwise the equivalence holds for programs that type and
    // breaks for programs that do not, which is the harder half to notice.
    // Same head test as that arm, routed through the same `unitSlotBases`
    // logic, so all three spellings (`Float<secnd>`, `T<secnd>`, `T<secnd>^0`)
    // answer identically.
    | TyNamed (ctor, (_ :: _ as args))
            when not (isConcreteTypeBaseName ctor)
                 && (lookupTypeDef ctor env).IsNone ->
        unitAnnoError env (TyNamed ("Float", args))
    | TyNamed (_, args) -> args |> List.tryPick (unitAnnoError env)
    // `T<u>^k` (array-expression plan bug #8): under a caret the head is a
    // type VARIABLE, so its argument slot admits nothing but a unit -- the
    // same situation as a `unitSlotBases` base, with the variable standing in
    // for it. Routed through that arm verbatim (substituting a base name that
    // owns a unit slot) so the two spellings cannot drift: an unresolvable
    // name is BL3015 here just as in `Float<secnd>`. A concrete head keeps
    // its ordinary reading and recurses normally.
    | TyAbstractArray ((TyNamed (ctor, (_ :: _ as args))), _, _)
            when not (isConcreteTypeBaseName ctor)
                 && (lookupTypeDef ctor env).IsNone ->
        unitAnnoError env (TyNamed ("Float", args))
    | TyAbstractArray (elem, _, _) -> unitAnnoError env elem
    | TyBounded (b, _, _) -> unitAnnoError env b
    | TyArray (elem, _) -> unitAnnoError env elem
    | TyDist (_, elem, _) -> unitAnnoError env elem
    | TyTuple ts -> ts |> List.tryPick (unitAnnoError env)
    | TyFunc (args, ret) ->
        (args |> List.tryPick (unitAnnoError env))
        |> Option.orElseWith (fun () -> unitAnnoError env ret)
    | TyConstrained (inner, _) -> unitAnnoError env inner
    | TyPoly inner -> unitAnnoError env inner
    | _ -> None

/// DEFAULT PARAMETER FILL (surface call-site desugar). A call omitting
/// trailing DEFAULTED parameters rewrites into an ordinary full-arity call
/// BEFORE any typing happens, so codegen and the interpreter only ever see
/// plain calls and lets -- absence resolves statically, nothing option-like
/// exists at runtime.
///
///   f(a0, a1)  with  f(x, y, s: T = d)   ==>   f(a0, a1, (d : T))
///
/// A fill is ascribed with the parameter's declared annotation, which makes
/// a defaulted QUANTITY slot an INTRODUCTION position: the ascription mints
/// the slot's nominal onto the default (with the dims check ascription
/// carries), so no BL3010 fires for the API author's own default while
/// caller-supplied bare arguments are still rejected.
///
/// When a fill references required params (the `auto_levels(z)` pattern),
/// the call wraps in a block that binds each REFERENCED supplied argument
/// exactly once -- first to a fresh temp (so every argument expression still
/// evaluates in CALLER scope; a later arg may use a name that collides with
/// an earlier param), then to the parameter's own name, which is the name
/// the default expression references:
///
///   { let __dflt7_0 = a0; let z = __dflt7_0; f(z, (auto_levels(z) : T)) }
///
/// Defaults evaluate at call entry, left-to-right, seeing the caller's
/// evaluated required-argument values.
///
/// FACTORY BY-NOMINAL ROUTING. When the callee's defaulted trailing group
/// contains QUANTITY-typed slots (Nominal = Some, distinct per slot --
/// BL3013), a trailing argument carrying a quantity nominal (an ascription
/// `20 : levels` / `v : Float<speed>`, or a variable whose type already
/// carries the nominal) routes TO THAT SLOT regardless of its position among
/// the trailing args. Untagged trailing args keep positional fill and must
/// PRECEDE every tagged one (an untagged straggler after a tag is ambiguous
/// -- BL3014); a tag matching no slot and a slot supplied twice are BL3014
/// too. A callee with no quantity slots keeps pure positional semantics --
/// quantity-typed VALUES may legitimately flow into structural slots.
///
/// Returns None when this call needs no rewriting (unknown callee, no
/// defaults, full-arity in declared order, fewer than required args, or a
/// `_` placeholder -- partial application owns those); Some (Error e) when
/// routing itself is invalid.
let private tryFillDefaultArgs (env: TypeEnv) (callSpan: Span) (func: Expr) (args: Expr list) : TypeResult<Expr> option =
    let calleeName =
        match func.Kind with
        | ExprKind.ExprVar n -> n
        | ExprKind.ExprField ({ Kind = ExprKind.ExprVar alias }, fname) -> alias + "." + fname
        | _ -> "lambda"
    let paramInfos =
        match func.Kind with
        | ExprKind.ExprVar name ->
            (match env.FuncDefaults.TryGetValue name with
             | true, ps -> Some ps
             | _ -> None)
        // Module-QUALIFIED callee (`plot.contourf(...)`): declarations inside
        // an imported module registered their defaults under the BARE
        // function name (checkFunctionDecl runs inside that module), so the
        // field name is the lookup key. Shares FuncDefaults' documented
        // name-keyed shadowing weakness.
        | ExprKind.ExprField ({ Kind = ExprKind.ExprVar _ }, fname) ->
            (match env.FuncDefaults.TryGetValue fname with
             | true, ps -> Some ps
             | _ -> None)
        // Immediately-applied lambda literal: its params are right here.
        | ExprKind.ExprLambda (parms, _, _) when parms |> List.exists (fun p -> p.Default.IsSome) ->
            Some (parms |> List.map (fun p -> (p.Name, p.Type, p.Default)))
        | _ -> None
    match paramInfos with
    | None -> None
    | Some ps ->
        let total = ps.Length
        let required = ps |> List.takeWhile (fun (_, _, d) -> Option.isNone d) |> List.length
        let k = args.Length
        let hasWildcard =
            args |> List.exists (fun a -> match a.Kind with ExprKind.ExprWildcard -> true | _ -> false)
        if hasWildcard || k < required then None
        else
        let trailingSlots = ps |> List.skip required   // all defaulted (trailing rule)
        let slotQs =
            trailingSlots |> List.map (fun (_, tyOpt, _) -> tyOpt |> Option.bind (surfaceTypeQuantity env))
        let trailingArgs = if k > required then args |> List.skip required else []
        let argTags =
            // Tags engage only when the callee HAS quantity slots (see doc).
            if slotQs |> List.exists Option.isSome
            then trailingArgs |> List.map (argQuantityTag env)
            else trailingArgs |> List.map (fun _ -> None)
        let anyTagged = argTags |> List.exists Option.isSome
        // TAGS THAT ONLY CONFIRM THE POSITIONAL READING ARE A NO-OP, and saying
        // so is what makes THIS FUNCTION IDEMPOTENT. A partial call is rewritten
        // to full arity and re-inferred, which lands back here -- and the
        // rewrite appends the missing defaults UNTAGGED, because a slot with no
        // quantity (`n_segments: Float64 = 10.0`) has no tag to carry. On the
        // second pass those appended arguments read as untagged stragglers
        // after the user's tagged one and the straggler check below refused a
        // call it had itself just built: `lswosa((s, t), freqs, (0.0 : time))`
        // -- three arguments -- was reported as "argument 4 has no quantity tag"
        // (examples/lswosa.blade could not be called at all).
        //
        // The test is exact rather than a re-entry flag: routing has nothing to
        // do when every TAGGED trailing argument already sits in the slot
        // carrying its quantity and the call is complete. Reordering calls
        // (`plot(1.0, 3.0: cmap, 2.0: levels)`, functions/049) disagree with
        // their positions and still route; the genuine ambiguity
        // (`plot(1.0, 3.0: cmap, 2.0)`, functions/053) still refuses. Full
        // arity is required because a SHORT call still needs this function to
        // fill its defaults.
        let tagsConfirmPositions =
            argTags
            |> List.indexed
            |> List.forall (fun (j, t) ->
                match t with
                | Some q -> j < slotQs.Length && slotQs.[j] = Some q
                | None -> true)
        if k >= total && (not anyTagged || tagsConfirmPositions) then None
        else
        // Per trailing slot, in DECLARED order: Some suppliedArg | None (use default).
        let assembled : TypeResult<Expr option list> =
            if not anyTagged then
                Ok [ for j in 0 .. trailingSlots.Length - 1 ->
                        if j < trailingArgs.Length then Some (List.item j trailingArgs) else None ]
            else
                let firstTag = argTags |> List.findIndex Option.isSome
                let straggler =
                    argTags
                    |> List.mapi (fun i t -> (i, t))
                    |> List.tryPick (fun (i, t) -> if i > firstTag && t.IsNone then Some i else None)
                match straggler with
                | Some i -> Error (FactoryAmbiguousMix (calleeName, required + i + 1))
                | None ->
                    let nPos = firstTag   // untagged prefix fills leading trailing slots positionally
                    if nPos > trailingSlots.Length then
                        Error (ArityMismatch (total, k))
                    else
                    let assignments = System.Collections.Generic.Dictionary<int, Expr>()
                    for j in 0 .. nPos - 1 do assignments.[j] <- List.item j trailingArgs
                    let mutable err = None
                    trailingArgs
                    |> List.skip nPos
                    |> List.iteri (fun i a ->
                        if err.IsNone then
                            let tag = (List.item (nPos + i) argTags).Value
                            match slotQs |> List.tryFindIndex (fun q -> q = Some tag) with
                            | None ->
                                err <- Some (FactoryUnknownTag (calleeName, tag, slotQs |> List.choose id))
                            | Some j ->
                                if assignments.ContainsKey j then
                                    let (slotName, _, _) = List.item j trailingSlots
                                    err <- Some (FactoryDupFill (calleeName, tag, slotName))
                                else
                                    assignments.[j] <- a)
                    match err with
                    | Some e -> Error e
                    | None ->
                        Ok [ for j in 0 .. trailingSlots.Length - 1 ->
                                match assignments.TryGetValue j with
                                | true, a -> Some a
                                | _ -> None ]
        match assembled with
        | Error e -> Some (Error e)
        | Ok slotAssign ->
        // Nothing to do (every slot supplied, already in declared order):
        // return None so the re-entry after a rewrite terminates.
        let isIdentity =
            slotAssign.Length = trailingArgs.Length
            && List.forall2
                (fun s a -> match s with Some x -> System.Object.ReferenceEquals(x, a) | None -> false)
                slotAssign trailingArgs
        if isIdentity then None
        else
        let mkFill (slot: string * TypeExpr option * Expr option) =
            let (_, tyOpt, dfltOpt) = slot
            let d = Option.get dfltOpt   // every trailing slot has a default (trailing rule)
            match tyOpt with
            | Some ty -> { d with Kind = ExprKind.ExprTyped (d, ty) }
            | None -> d
        let slotExprsAndFills =
            List.zip trailingSlots slotAssign
            |> List.map (fun (slot, assigned) ->
                match assigned with
                | Some a -> (a, false)
                | None -> (mkFill slot, true))
        let finalTrailing = slotExprsAndFills |> List.map fst
        let fills = slotExprsAndFills |> List.filter snd |> List.map fst
        let requiredArgs = args |> List.truncate required
        let requiredNames = ps |> List.truncate required |> List.map (fun (n, _, _) -> n)
        // Only fills can reference params, and only REQUIRED ones (scope rule).
        let referencedNames =
            let free =
                fills
                |> List.map (collectFreeVars Set.empty)
                |> List.fold Set.union Set.empty
            requiredNames |> List.filter (fun n -> Set.contains n free) |> Set.ofList
        if Set.isEmpty referencedNames then
            Some (Ok (mkExpr callSpan (ExprKind.ExprApp (func, requiredArgs @ finalTrailing))))
        else
            let uid = env.Builder.FreshId()
            let refIdx =
                requiredNames
                |> List.mapi (fun i n -> (i, n))
                |> List.filter (fun (_, n) -> Set.contains n referencedNames)
            let mkLet name (value: Expr) =
                StmtLet { Mutability = BindLet
                          Pattern = { Kind = PatVar name; Span = value.Span }
                          Type = None
                          Value = value }
            let tempName i = sprintf "__dflt%d_%d" uid i
            let tempStmts = refIdx |> List.map (fun (i, _) -> mkLet (tempName i) (List.item i requiredArgs))
            let aliasStmts =
                refIdx |> List.map (fun (i, n) ->
                    mkLet n (mkExpr (List.item i requiredArgs).Span (ExprKind.ExprVar (tempName i))))
            let callRequired =
                requiredArgs |> List.mapi (fun i a ->
                    if refIdx |> List.exists (fun (j, _) -> j = i)
                    then mkExpr a.Span (ExprKind.ExprVar (List.item i requiredNames))
                    else a)
            let call = mkExpr callSpan (ExprKind.ExprApp (func, callRequired @ finalTrailing))
            Some (Ok (mkExpr callSpan (ExprKind.ExprBlock (tempStmts @ aliasStmts, Some call))))

/// CHAINED FACTORY SUGAR: `f(x, y)(a : q1)(b : q2)` flattens into the single
/// call `f(x, y, a : q1, b : q2)` BEFORE dispatch, when the base callee is a
/// defaults-carrying function (env.FuncDefaults) and EVERY argument of every
/// trailing application is quantity-tagged. Comma groups and chains are
/// equivalent (`f(x)(a : q1, b : q2)` too). Genuine arrow over-application
/// never matches -- a curried function carries no defaults entry, or its
/// trailing args are not quantity-tagged -- so the existing curry/eta paths
/// keep those shapes exactly.
let private tryFlattenFactoryChain (env: TypeEnv) (func: Expr) (args: Expr list) : (Expr * Expr list) option =
    match func.Kind with
    | ExprKind.ExprApp _ ->
        let rec collect (f: Expr) (groups: Expr list list) =
            match f.Kind with
            | ExprKind.ExprApp (inner, innerArgs) -> collect inner (innerArgs :: groups)
            | _ -> (f, groups)
        let baseFn, groups = collect func [args]
        let isDefaultsCallee =
            match baseFn.Kind with
            | ExprKind.ExprVar name -> env.FuncDefaults.ContainsKey name
            // Module-qualified base (`plot.contourf(...)(...)`): defaults are
            // registered under the bare name (see tryFillDefaultArgs).
            | ExprKind.ExprField ({ Kind = ExprKind.ExprVar _ }, fname) -> env.FuncDefaults.ContainsKey fname
            | _ -> false
        if not isDefaultsCallee then None
        else
            let trailingGroups = List.tail groups
            let allTagged =
                trailingGroups
                |> List.forall (fun g ->
                    not (List.isEmpty g)
                    && g |> List.forall (fun a -> (argQuantityTag env a).IsSome))
            if List.isEmpty trailingGroups || not allTagged then None
            else Some (baseFn, List.concat groups)
    | _ -> None

/// Entry for every expression: stamps the ambient expression span (for
/// error location, see TypeEnv.locateError) and back-fills the source span
/// onto the typed node so TypedExpr.Span is live (full-span AST).
let rec inferExpr (env: TypeEnv) (expr: Expr) : TypeResult<TypedExpr> =
    if expr.Span.StartLine > 0 then setCurrentExprSpan expr.Span
    match inferExprInner env expr with
    | Ok te when te.Span.StartLine = 0 && expr.Span.StartLine > 0 ->
        Ok { te with Span = expr.Span }
    | r -> r

and inferExprInner (env: TypeEnv) (expr: Expr) : TypeResult<TypedExpr> =
    match expr.Kind with
    // ---- Literals ----
    // Literals synthesize CONCRETE (their natural scalar). Flexibility (flexing
    // a literal into a generic `T`/`T^k` or a wider width) is introduced
    // BIDIRECTIONALLY in `checkExpr`, only where an expected type demands it --
    // making a literal a bare inference var at synthesis would spray unresolved
    // vars through array/arith/index positions that assume concrete scalars.
    | ExprKind.ExprLit lit -> Ok (mkTyped (TExprLit lit) (inferLiteralType lit))

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
        Error (Other "internal: static former survived unfolding (the Unfold pass did not run)")

    // ---- Recursive array definitions ----
    // Only legal as the immediate Value of a `let rec` binding; the binding
    // path routes to inferRecArray (which needs the declared type and the
    // bound name for self-reference). Reaching it here means it appeared in
    // ordinary expression position.
    | ExprKind.ExprRecArray def ->
        Error (Other (sprintf "recursive array '%s': a recursive array definition is only legal as the body of `let rec %s: ... = match %s with ...`" def.Name def.Name def.Name))

    // ---- Variables ----
    | ExprKind.ExprVar name ->
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
            match Map.tryFind name env.VariantTags with
            | Some (parentName, None) ->
                Ok (mkTyped (TExprVar (name, env.Builder.FreshId(), None)) (IRTNamed parentName))
            | Some (parentName, Some payloadTy) ->
                Ok (mkTyped (TExprVar (name, env.Builder.FreshId(), None))
                            (mkFuncArrow [payloadTy] (IRTNamed parentName)))
            | None -> Error (UnboundVariable name)

    | ExprKind.ExprQualified names ->
        // Qualified name resolution -- limited for now
        Ok (mkTyped (TExprQualified names) IRTUnit)

    // ---- Binary operations (dispatch to helper) ----
    | ExprKind.ExprBinOp (mode, op, left, right) ->
        inferBinOp env mode op left right

    // ---- Unary operations ----
    | ExprKind.ExprUnaryOp (op, operand) ->
        inferUnaryOp env op operand
    | ExprKind.ExprApp ({ Kind = ExprKind.ExprField ({ Kind = ExprKind.ExprVar alias }, "load_compound") }, [varE; maskE])
        when (providerAliasName env alias).IsSome ->
        inferExpr env varE |> Result.bind (fun tVar ->
        inferExpr env maskE |> Result.bind (fun tMask ->
            match env.Subst.Resolve(tVar.Type), env.Subst.Resolve(tMask.Type) with
            | ArrayElem varArr, ArrayElem maskArr ->
                (match compoundViewType (env.Builder.FreshId()) varArr maskArr (IRLit IRLitUnit) with
                 | Ok compoundTy ->
                     let aliasVi = (lookupVar alias env).Value
                     let tAlias = mkTyped (TExprVar (alias, aliasVi.VarId, aliasVi.Identity)) aliasVi.Type
                     let tField = mkTyped (TExprField (tAlias, "load_compound", 0)) compoundTy
                     Ok (mkTyped (TExprApp (tField, [tVar; tMask])) compoundTy)
                 | Error msg -> Error (Other msg))
            | _ -> Error (Other "load_compound expects two array arguments: the variable and an integer mask")))

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

    // ---- Windowed packed read: alias.read_window(view, lo, hi) ----
    // Materializes only the cells with every coordinate in [lo, hi): a
    // translated sub-simplex, typed with leading packed extent hi-lo.
    // Bounds are integer literals (the window is a compile-time shape).
    | ExprKind.ExprApp ({ Kind = ExprKind.ExprField ({ Kind = ExprKind.ExprVar alias }, "read_window") }, args)
        when (providerAliasName env alias).IsSome ->
        (match args with
         | [operand; { Kind = ExprKind.ExprLit (LitInt lo) }; { Kind = ExprKind.ExprLit (LitInt hi) }] ->
             inferExpr env operand |> Result.bind (fun tE ->
                 match env.Subst.Resolve tE.Type with
                 | ArrayElem at ->
                     (match at.IndexTypes with
                      // A wreath lead passes the packed test below, but has no
                      // window type: a sub-simplex of a wreath class is not a
                      // wreath class -- every level would need restricting at
                      // once, and level i's ground set is level (i-1)'s cells,
                      // not [lo,hi). spec_version 2 agrees on the store side.
                      | lead :: _ when lead.Symmetry = SymWreath ->
                          Error (OrbitStorageUnsupported (ppOrbitLevels (orbitLevelsOf lead),
                                                          "z.read_window (a wreath class has no translated sub-class to window into)"))
                      | lead :: rest when lead.Symmetry <> SymNone && lead.Rank >= 2 ->
                          (match lead.Extent with
                           | IRLit (IRLitInt n) when lo >= 0L && lo < hi && hi <= n ->
                               let winIdx = { lead with Id = env.Builder.FreshId()
                                                        Extent = IRLit (IRLitInt (hi - lo)) }
                               let winTy = mkArrayLike { at with IndexTypes = winIdx :: rest }
                               let aliasVi = (lookupVar alias env).Value
                               let tAlias = mkTyped (TExprVar (alias, aliasVi.VarId, aliasVi.Identity)) aliasVi.Type
                               let tField = mkTyped (TExprField (tAlias, "read_window", 0)) winTy
                               let tLo = mkTyped (TExprLit (LitInt lo)) (IRTScalar ETInt64)
                               let tHi = mkTyped (TExprLit (LitInt hi)) (IRTScalar ETInt64)
                               Ok (mkTyped (TExprApp (tField, [tE; tLo; tHi])) winTy)
                           | IRLit (IRLitInt n) ->
                               Error (ProviderReadWindowBounds (alias, lo, hi, n))
                           | _ ->
                               Error (ProviderReadWindowLiteralExtent alias))
                      | _ ->
                          Error (ProviderReadWindowPacked alias))
                 | _ -> Error (ProviderReadWindowNeedsVar alias))
         | _ -> Error (ProviderReadWindowArgs alias))

    // ---- Provider write: alias.write("path", A) ----
    // The source must be a named array binding (the store variable takes
    // its name); the path must be a string literal (the store is created
    // at a compile-time-known location, mirroring alias.load). Types as
    // unit; the generic application node is kept -- lowering's
    // tryProviderWrite intercepts the shape into a ProviderWriteSpec.
    | ExprKind.ExprApp ({ Kind = ExprKind.ExprField ({ Kind = ExprKind.ExprVar alias }, "write") }, args)
        when (providerAliasName env alias).IsSome ->
        (match args with
         | [{ Kind = ExprKind.ExprLit (LitString path) }; valueE] ->
             (match valueE.Kind with
              | ExprKind.ExprVar _ ->
                  inferExpr env valueE |> Result.bind (fun tValue ->
                      match env.Subst.Resolve(tValue.Type) with
                      | ArrayElem _ ->
                          let aliasVi = (lookupVar alias env).Value
                          let tAlias = mkTyped (TExprVar (alias, aliasVi.VarId, aliasVi.Identity)) aliasVi.Type
                          let tField = mkTyped (TExprField (tAlias, "write", 0)) IRTUnit
                          let tPath = mkTyped (TExprLit (LitString path)) (IRTScalar ETString)
                          Ok (mkTyped (TExprApp (tField, [tPath; tValue])) IRTUnit)
                      | _ -> Error (ProviderWriteNeedsArray alias))
              | _ -> Error (ProviderWriteNamedBinding alias))
         | _ -> Error (ProviderWriteArgs alias))

    | ExprKind.ExprApp (({ Kind = ExprKind.ExprField ({ Kind = ExprKind.ExprVar n }, field) } as qualFuncE), args)
        when (lookupVar (sprintf "%s.%s" n field) env).IsSome ->
        // DEFAULT PARAMETER FILL + by-nominal routing for module-QUALIFIED
        // callees (`plot.contourf(x, y, z, 3: cmap)`): this arm short-circuits
        // the general ExprApp arm below, so the fill must run here too --
        // without it an omitted-slot call reaches codegen under-applied.
        // The rewritten full-arity call re-enters this arm and passes through.
        match tryFillDefaultArgs env expr.Span qualFuncE args with
        | Some (Ok rewritten) -> inferExpr env rewritten
        | Some (Error e) -> Error e
        | None ->
        let qualName = sprintf "%s.%s" n field
        let info = (lookupVar qualName env).Value
        let useTy =
            match info.Scheme with
            | Some scheme -> instantiate env.Subst scheme
            | None -> info.Type
        let tFunc = mkTyped (TExprVar (qualName, info.VarId, info.Identity)) useTy
        args |> List.map (inferExpr env) |> sequenceResults |> Result.bind (fun tArgs ->
            let retTy =
                match useTy with
                | FuncElem (_, ret) -> ret
                | _ -> env.Subst.Fresh()
            // This arm builds the node itself instead of going through
            // dispatchAppOrIndex, so it inherits NONE of that seam's
            // argument checks -- `MathLib.double("nope")` type-checked clean
            // and died in g++. The element-class check is repeated here
            // rather than the arm being rerouted: dispatchAppOrIndex would
            // also impose arity/rank accounting on every qualified callee
            // (providers, `plot.*`, stdlib arrays), which is a larger change
            // than an argument-type hole warrants. If this arm is ever
            // rerouted, DELETE this block -- do not leave two copies.
            //
            // Same reason the WIDTH SCHEMA regrouping is repeated here
            // (docs/plan-tuples-vs-arg-packs.md 6c): a qualified callee with a
            // `Tuple<k>` parameter must slice the flat argument list exactly
            // like an unqualified one, or `M.addPair(b, c)` and `addPair(b, c)`
            // stop being the same call. Same delete-if-rerouted note applies.
            let tArgs =
                regroupArgsByWidth env
                    (match useTy with FuncElem (ps, _) -> ps | _ -> []) tArgs
            match firstArgTypeClash env.Subst
                      (match useTy with FuncElem (ps, _) -> ps | _ -> [])
                      (tArgs |> List.map (fun a -> a.Type)) with
            | Some (i, pTy, aTy) ->
                Error (ArgTypeMismatch (i + 1, sprintf "'%s'" qualName,
                                        ppIRType (env.Subst.Resolve pTy),
                                        ppIRType (env.Subst.Resolve aTy)))
            | None ->
            Ok (mkTyped (TExprApp (tFunc, tArgs)) retTy))

    // ---- Method call: obj.method(args) -> impl resolution ----
    | ExprKind.ExprApp ({ Kind = ExprKind.ExprField (obj, method) }, args) ->
        inferMethodCall env obj method args
    // ---- Scalar math intrinsics: exp(x), sqrt(x), ... ----
    // Surface form is a plain call; the name is rewritten to OpMath only
    // when it is NOT user-bound (a user `function exp(...)` or a local
    // binding named `exp` shadows the intrinsic). Scalar-only: mapping over
    // an array is a kernel's job, so an array operand is a type error with
    // steering. Result is Float64 (Float32 operands widen; Int operands are
    // promoted by the C++ overload set).
    | ExprKind.ExprApp ({ Kind = ExprKind.ExprVar name }, [arg]) when isMathIntrinsic name && (lookupVar name env).IsNone ->
        inferExpr env arg |> Result.bind (fun tArg ->
            match env.Subst.Resolve tArg.Type with
            | ArrayElem _ ->
                Error (IntrinsicScalarOnly name)
            | IRTScalar (ETComplex64 | ETComplex128) ->
                // exp/log/sqrt and the trig/hyperbolic families have std::complex
                // overloads and preserve the complex type; floor/ceil have no
                // complex overload and stay rejected.
                if isComplexMathIntrinsic name then
                    Ok (mkTyped (TExprUnaryOp (OpMath name, tArg)) tArg.Type)
                else
                    Error (IntrinsicNotComplex name)
            | IRTScalar ETBool | IRTScalar ETString ->
                Error (IntrinsicNeedsNumeric name)
            | IRTInfer _ when isComplexMathIntrinsic name ->
                // Unresolved operand: DEFER, since apply-site unification may
                // later bind it COMPLEX (exp/log/sqrt/trig preserve complex),
                // so pinning Float64 now would reject complex kernels; the
                // kernel re-stamp in buildApplyInfo corrects the result type.
                Ok (mkTyped (TExprUnaryOp (OpMath name, tArg)) tArg.Type)
            | IRTInfer _ ->
                // floor/ceil have no complex overload -- the operand really is
                // real; pin it to Float64, the intrinsic's natural domain.
                unify env.Subst tArg.Type (IRTScalar ETFloat64) |> Result.bind (fun () ->
                Ok (mkTyped (TExprUnaryOp (OpMath name, tArg)) (IRTScalar ETFloat64)))
            | IRTUnitAnnotated _ when env.InLambdaBody && typedExprHasProvisionalUnits env tArg ->
                // PROVISIONAL annotation inside a lambda body: something the
                // argument depends on is still an unresolved inference variable
                // (a kernel param before apply-site unification), and an
                // unresolved operand contributes "no units" to first-pass
                // typing -- so the annotation seen here can be exactly a
                // dimensioned CAPTURE's signature even when the product cancels
                // once the param's unit is known (`lambda(w) -> cos(w * tz)`,
                // w : 1/day, tz : day).
                // DEFER: buildApplyInfo's kernelBodyUnits reruns the same
                // per-op table after param unification and rejects for real.
                // Result is typed bare Float64, matching the accept path below.
                //
                // The dependency is chased THROUGH kernel-local lets
                // (typedExprHasProvisionalUnits): `let w = two_pi * fq;
                // cos(w * t_zero)` is the same situation with `w` in the way,
                // and the plain node-type walk stopped at `w` -- resolved, bare
                // -- and rejected. Pass 2 models those lets in its `bound` map,
                // so the deferred judgement is the correct one.
                Ok (mkTyped (TExprUnaryOp (OpMath name, tArg)) (IRTScalar ETFloat64))
            | resolvedArg ->
                // Unit propagation at scalar position, same table as the
                // kernel-body walk (unitRulesForUnaryOp): sqrt halves
                // all-even exponents; floor/ceil and the transcendentals
                // are not homogeneous and REJECT a dimensioned operand.
                unitRulesForUnaryOp (OpMath name) (IR.getUnits resolvedArg)
                |> Result.map (fun resUnits ->
                    let resTy =
                        match resUnits with
                        | Some u -> IRTUnitAnnotated (IRTScalar ETFloat64, u)
                        | None -> IRTScalar ETFloat64
                    mkTyped (TExprUnaryOp (OpMath name, tArg)) resTy))

    // ---- abs(x): polymorphic numeric intrinsic ----
    // Deliberately NOT in mathIntrinsics (those are real-valued, typed
    // Float64, and carry derivative rules); abs preserves its operand's
    // numeric type and renders as std::abs, whose C++ overload set covers
    // int64 and double. Ubiquitous in dependent field bounds
    // (`in abs(l1 - l2) .. l1 + l2 + 1`).
    | ExprKind.ExprApp ({ Kind = ExprKind.ExprVar "abs" }, [arg]) when (lookupVar "abs" env).IsNone ->
        inferExpr env arg |> Result.bind (fun tArg ->
            match env.Subst.Resolve tArg.Type with
            | IRTScalar (ETInt32 | ETInt64 | ETFloat32 | ETFloat64) as sc ->
                Ok (mkTyped (TExprUnaryOp (OpMath "abs", tArg)) sc)
            | IRTScalar (ETComplex64 | ETComplex128) ->
                // abs of a complex is its real magnitude (std::abs(complex<T>)
                // returns T); type the result Float64 (IRMath "abs" reports
                // Float64 at the IR level, which is correct for both widths).
                Ok (mkTyped (TExprUnaryOp (OpMath "abs", tArg)) (IRTScalar ETFloat64))
            | IRTInfer _ ->
                Ok (mkTyped (TExprUnaryOp (OpMath "abs", tArg)) tArg.Type)
            | IRTUnitAnnotated (IRTScalar (ETInt32 | ETInt64 | ETFloat32 | ETFloat64), _) as t ->
                // abs preserves the operand's unit annotation -- a unitful
                // scalar is a perfectly good abs operand.
                Ok (mkTyped (TExprUnaryOp (OpMath "abs", tArg)) t)
            | other ->
                Error (AbsNeedsNumericScalar (ppIRType other)))

    // ---- real(z) / imag(z) / arg(z): complex component/phase accessors ----
    // Plain-call intrinsics (shadowable by a user binding, like abs). Require a
    // complex scalar operand -- real/imag on a real value is trivially the
    // identity/zero and a likely mistake, so we steer instead. real/imag yield
    // the component width (Complex128 -> Float64, Complex64 -> Float32); arg is
    // a Float64 angle. Emit std::real/std::imag/std::arg via the generic unary
    // codegen arm.
    | ExprKind.ExprApp ({ Kind = ExprKind.ExprVar (("real" | "imag" | "arg") as name) }, [arg]) when (lookupVar name env).IsNone ->
        inferExpr env arg |> Result.bind (fun tArg ->
            let op = match name with
                     | "real" -> OpReal
                     | "imag" -> OpImag
                     | _ -> OpArg
            match env.Subst.Resolve tArg.Type with
            | IRTScalar ETComplex64 ->
                let resTy = if name = "arg" then IRTScalar ETFloat64 else IRTScalar ETFloat32
                Ok (mkTyped (TExprUnaryOp (op, tArg)) resTy)
            | IRTScalar ETComplex128 ->
                Ok (mkTyped (TExprUnaryOp (op, tArg)) (IRTScalar ETFloat64))
            | IRTInfer _ ->
                // Unresolved operand (unannotated kernel/lambda parameter):
                // DEFER the complex requirement -- the apply-site unification
                // binds the param to the iterated element type. Result is
                // provisionally Float64 (the Complex128-operand answer); the
                // kernel re-stamp corrects the Complex64 width (-> Float32
                // components).
                Ok (mkTyped (TExprUnaryOp (op, tArg)) (IRTScalar ETFloat64))
            | ArrayElem _ ->
                Error (IntrinsicComplexScalarOnly name)
            | other ->
                Error (IntrinsicNeedsComplex (name, ppIRType other)))

    // ---- atan2(y, x) / log_base(x, b): BINARY math intrinsics ----
    // Same surface shape and shadowing rule as the unary intrinsics (a user
    // `function atan2(...)` wins), one arity up. The work is in
    // inferBinaryIntrinsic; the second arm turns a wrong-arity call into an
    // intrinsic-specific message instead of "unbound variable atan2".
    | ExprKind.ExprApp ({ Kind = ExprKind.ExprVar name }, [aExpr; bExpr])
            when isBinaryIntrinsic name && (lookupVar name env).IsNone ->
        inferBinaryIntrinsic env name aExpr bExpr
    | ExprKind.ExprApp ({ Kind = ExprKind.ExprVar name }, args)
            when isBinaryIntrinsic name && (lookupVar name env).IsNone ->
        Error (Other (sprintf "%s takes exactly 2 arguments (got %d): %s"
                        name args.Length
                        (if name = "atan2" then "atan2(y, x) is the quadrant-correct angle of the point (x, y)"
                         else "log_base(x, b) is log x / log b")))

    // ---- complex(re, im): complex literal constructor ----
    // The one way to construct a complex value. As a plain call this
    // composes under any operator without the precedence trap a 2-tuple
    // cast form would hit (`a * (re, im) : T` binding the cast outside the
    // multiply). Plain-call intrinsic, shadowable like abs/real. Components
    // must be float-typed (no implicit int -> float promotion at
    // construction time). Infers Complex128; checking against an expected
    // Complex64 adopts the narrow width (checkExpr arm).
    | ExprKind.ExprApp ({ Kind = ExprKind.ExprVar "complex" }, [reExpr; imExpr]) when (lookupVar "complex" env).IsNone ->
        (match checkExpr env (IRTScalar ETFloat64) reExpr, checkExpr env (IRTScalar ETFloat64) imExpr with
         | Ok tRe, Ok tIm -> Ok (mkTyped (TExprComplexLit (tRe, tIm)) (IRTScalar ETComplex128))
         | scalarRe, scalarIm ->
            // ARRAY LIFT. A component that is an ARRAY makes `complex` an
            // elementwise constructor, lifted exactly the way the elementwise
            // binops lift themselves (inferBinOp's zip and array/scalar arms):
            // re-synthesize as `method_for(...) <@> lambda(..) -> complex(..)
            // |> compute` and re-drive inference, so the zip/shape rules, the
            // loop machinery and codegen are the ones already proven for
            // `A + B` -- and the result is literally the workaround users
            // write by hand today. Both arrays co-iterate; one array against
            // a scalar broadcasts (the scalar's SURFACE expr is embedded so
            // capture analysis sees its variable references, per the
            // array/scalar binop arm's note). Only reached once the scalar
            // construction has already been rejected, so scalar `complex` is
            // unchanged.
            let arrayOperand (e: Expr) =
                match inferExpr env e with
                | Ok t -> (match env.Subst.Resolve t.Type with ArrayElem _ -> true | _ -> false)
                | Error _ -> false
            let sp = mergeSpan reExpr.Span imExpr.Span
            let cparam n : LambdaParam = { Name = n; Type = Some TyFloat64; Default = None; NameSpan = noSpan }
            let cvar n = mkExpr sp (ExprVar n)
            let cbody re im = mkExpr sp (ExprApp (mkExpr sp (ExprVar "complex"), [re; im]))
            let apply former ps body =
                inferExpr env (mkExpr sp (ExprCompute (mkExpr sp (ExprBinOp (Elementwise, OpApply,
                    former, mkExpr sp (ExprLambda (ps, None, body)))))))
            match arrayOperand reExpr, arrayOperand imExpr with
            | true, true ->
                apply (mkExpr sp (ExprMethodFor [mkExpr sp (ExprZip [reExpr; imExpr])]))
                      [cparam "__cre"; cparam "__cim"]
                      (cbody (cvar "__cre") (cvar "__cim"))
            | true, false ->
                apply (mkExpr sp (ExprMethodFor [reExpr])) [cparam "__cre"] (cbody (cvar "__cre") imExpr)
            | false, true ->
                apply (mkExpr sp (ExprMethodFor [imExpr])) [cparam "__cim"] (cbody reExpr (cvar "__cim"))
            | false, false ->
                // Neither component is an array: report the scalar rejection.
                match scalarRe, scalarIm with
                | Error e, _ -> Error e
                | _, Error e -> Error e
                | _ -> Error (ComplexArity 2))
    | ExprKind.ExprApp ({ Kind = ExprKind.ExprVar "complex" }, args) when (lookupVar "complex" env).IsNone ->
        Error (ComplexArity args.Length)

    // ---- prodsum(x1, ..., xk): fused fiber product-sum ----
    // Sigma_t Pi_l xl(t) over rank-1 arrays of equal extent -- the k-fold
    // generalization of a dot product, and the comoment primitive the PPL
    // moment formers elaborate to. Surface form is a plain call,
    // shadowable like the math intrinsics. Empty extent folds to 0 (sum
    // identity), so no non-empty check.
    | ExprKind.ExprApp ({ Kind = ExprKind.ExprVar "prodsum" }, args) when not args.IsEmpty && (lookupVar "prodsum" env).IsNone ->
        inferProdSum env args

    // ---- __dist_pack(kappa1, ..., kappar): typed-dist construction intrinsic ----
    // Compiler-internal (double-underscore reserved): emitted by the PPL
    // elaboration stage after it builds the fused cumulant tower, never
    // written by users. Packs the component arrays into a value of nominal
    // type Dist<r, tau like axes>; the typed node is a plain TExprTuple (the
    // representation a Dist erases to at zonk), only the TYPE is nominal.
    | ExprKind.ExprApp ({ Kind = ExprKind.ExprVar "__dist_pack" }, args) when not args.IsEmpty ->
        inferDistPack env args

    // ---- __rand_uniform / __rand_normal(key, d1, ..., dn): rand module ----
    // Compiler-internal (double-underscore reserved): emitted by the `rand`
    // elaboration stage from `alias.uniform/normal(key, shape)`. `key` is an
    // Int64 stream key; the trailing args are the (elaborator-resolved) static
    // extents. Self-typed as a dense Float64 array of that shape -- no annotation
    // needed. Lowering records (kind, key) in RandomInits; codegen emits
    // allocate<> + the runtime blade_rand fill.
    | ExprKind.ExprApp ({ Kind = ExprKind.ExprVar (("__rand_uniform" | "__rand_normal") as fn) }, (keyE :: dimArgs)) when not dimArgs.IsEmpty ->
        let kind = if fn = "__rand_uniform" then "uniform" else "normal"
        // Extents must be static ints (the elaborator resolves them to literals).
        let dimResults =
            dimArgs |> List.map (fun d ->
                match d.Kind with
                | ExprKind.ExprLit (LitInt n) when n > 0L -> Ok (int n)
                | ExprKind.ExprLit (LitInt n) -> Error (sprintf "rand.%s: shape extents must be positive (got %d)" kind n)
                | _ -> Error (sprintf "rand.%s: shape must be a static positive int (or list of them)" kind))
        match dimResults |> List.fold (fun acc r -> match acc, r with Ok xs, Ok x -> Ok (xs @ [x]) | Error e, _ -> Error e | _, Error e -> Error e) (Ok []) with
        | Error e -> Error (Other e)
        | Ok dims ->
            checkExpr env (IRTScalar ETInt64) keyE |> Result.map (fun tKey ->
                let indices =
                    dims |> List.map (fun n ->
                        { Id = env.Builder.FreshId(); Rank = 1; Extent = IRLit (IRLitInt (int64 n))
                          Symmetry = SymNone; Tag = None; IxKind = IxKPlain; Kind = SDimension; Dependencies = [] })
                let arrTy = mkArrayArrow indices (IRTScalar ETFloat64) None
                mkTyped (TExprRandGen (kind, tKey, dims)) arrTy)

    // ---- __display_emit(head, quoted, data, metaTail): display module ----
    // Compiler-internal (double-underscore reserved): emitted by the `display`
    // elaboration stage from `alias.emit(mime, data[, meta])`. Three of the
    // four arguments are elaboration-time constants the elaborator already
    // validated (the frame's JSON head, the payload-quoting flag, and the user
    // meta object minus its braces -- see Blade.Display.Frame); only `data` is
    // a runtime value, and it must be a String. Self-typed Bool: the call
    // answers `true` so it can sit in an ordinary binding or echo as a bare
    // REPL expression. A `data` of any other type is an ordinary type error
    // reported by checkExpr against ETString.
    | ExprKind.ExprApp ({ Kind = ExprKind.ExprVar "__display_emit" }, [headE; quotedE; dataE; metaE])
            when (lookupVar "__display_emit" env).IsNone ->
        (match headE.Kind, quotedE.Kind, metaE.Kind with
         | ExprKind.ExprLit (LitString head), ExprKind.ExprLit (LitBool quoted), ExprKind.ExprLit (LitString metaTail) ->
            checkExpr env (IRTScalar ETString) dataE
            |> Result.map (fun tData ->
                mkTyped (TExprDisplayEmit (head, quoted, tData, metaTail)) (IRTScalar ETBool))
         | _ ->
            Error (Other "display.emit: internal marker arguments must be literals (this is a compiler bug -- write display.emit(mime, data) instead of calling __display_emit directly)"))

    // ---- __display_json_array(A): display module JSON serialization ----
    // A rank-1 or rank-2 PLAIN-DENSE numeric array rendered as JSON text
    // (String). The rank is pinned on the typed node so both back ends pick
    // the 1-D/2-D serializer without re-resolving; unit annotations on the
    // element type are transparent (stripUnits) -- a Float<meter> array
    // serializes like a Float array. Compact/ragged storage is rejected with
    // a decompact steer: JSON has no triangular layout, and silently
    // densifying here would hide an allocation.
    | ExprKind.ExprApp ({ Kind = ExprKind.ExprVar "__display_json_array" }, [arrE])
            when (lookupVar "__display_json_array" env).IsNone ->
        inferExpr env arrE |> Result.bind (fun tArr ->
            // An abstract-rank parameter (`x: Float64^1`) is still an
            // inference var carrying only its EXACT-rank arity constraint;
            // shape the var here exactly like requireArrayArgMinRank does,
            // so the body's serializer rank is pinned at the declaration and
            // shape monomorphization sees an ordinary array param.
            (match env.Subst.Resolve tArr.Type with
             | IRTInfer vid ->
                 (match env.Subst.GetArityConstraint vid with
                  | Some k when k = 1 || k = 2 ->
                      let freshIdx (i: int) = {
                          Id = env.Builder.FreshId(); Rank = 1
                          Extent = IRParam (sprintf "__json_inferred_n%d" i, 0, IRTNat None)
                          Symmetry = SymNone; Tag = None; IxKind = IxKPlain
                          Kind = SDimension; Dependencies = []
                      }
                      let freshArrType = {
                          ElemType = env.Builder.FreshInferType()
                          IndexTypes = List.init k freshIdx
                          IsVirtual = false; Identity = None
                      }
                      unify env.Subst tArr.Type (mkArrayLike freshArrType) |> ignore
                  | _ -> ())
             | _ -> ())
            match env.Subst.Resolve tArr.Type with
            | ArrayElem at when at.IndexTypes.Length >= 1 && at.IndexTypes.Length <= 2 ->
                if not (at.IndexTypes |> List.forall isPlainDenseIx) then
                    Error (Other "display.json_array: the array must be plain dense on every axis (compact symmetric/ragged storage has no JSON layout) -- decompact(A, d) first")
                else
                    (match IR.stripUnits (env.Subst.Resolve at.ElemType) with
                     | IRTScalar (ETFloat64 | ETFloat32 | ETInt64 | ETInt32)
                     // Unresolved element (an abstract-rank param shaped just
                     // above, or a generic slice): defer -- the concrete call
                     // site supplies it, and both serializers reject
                     // non-numeric elements there.
                     | IRTInfer _ ->
                         Ok (mkTyped (TExprDisplayJson (at.IndexTypes.Length, tArr)) (IRTScalar ETString))
                     | other ->
                         Error (Other (sprintf "display.json_array: element type must be numeric (Float64/Float32/Int64/Int32), got %s" (ppIRType other))))
            | ArrayElem at ->
                Error (Other (sprintf "display.json_array: rank-1 and rank-2 arrays only (got rank %d)" at.IndexTypes.Length))
            | other ->
                Error (Other (sprintf "display.json_array: expected a rank-1 or rank-2 numeric array, got %s" (ppIRType other))))

    // ---- __display_unit_label(x): elaboration-time unit/quantity label ----
    // Collapses to a STRING LITERAL naming the operand's unit signature: the
    // quantity's nominal name when it carries one, the structural dims
    // rendering otherwise, "" when bare. Arrays answer for their ELEMENT
    // units (the axis-label use case). No typed/IR node survives -- both
    // lanes see an ordinary string literal, so parity is definitional.
    | ExprKind.ExprApp ({ Kind = ExprKind.ExprVar "__display_unit_label" }, [e])
            when (lookupVar "__display_unit_label" env).IsNone ->
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

    // ---- __display_json_num(x): display module scalar JSON rendering ----
    // A numeric scalar rendered as JSON text, same 15-significant-digit
    // byte-parity formatting as json_array's elements. Unit/quantity
    // annotations are transparent, so a factory can serialize its
    // `Int64<levels>` slot directly.
    | ExprKind.ExprApp ({ Kind = ExprKind.ExprVar "__display_json_num" }, [numE])
            when (lookupVar "__display_json_num" env).IsNone ->
        inferExpr env numE |> Result.bind (fun tNum ->
            match IR.stripUnits (env.Subst.Resolve tNum.Type) with
            | IRTScalar (ETFloat64 | ETFloat32 | ETInt64 | ETInt32) ->
                Ok (mkTyped (TExprDisplayNum tNum) (IRTScalar ETString))
            | other ->
                Error (Other (sprintf "display.json_num: expected a numeric scalar, got %s" (ppIRType other))))

    // ---- cumulant(d, k): dist component projection, order-guarded ----
    // The order guard as a TYPE error (ppl/NOTES.md typed-Dist arc): k must
    // be a static int in 1..r where r is the dist's carried order. Works in
    // any expression position on any Dist-typed value -- including function
    // parameters, which the elaboration-level registry could never see.
    // `cumulant` is part of the `ppl` module surface: the ppl elaborator
    // rewrites a qualified `p.cumulant(d, k)` to this internal marker, so a
    // bare `cumulant(...)` no longer resolves (import-gated, not language-wide).
    | ExprKind.ExprApp ({ Kind = ExprKind.ExprVar "__ppl_cumulant" }, [dExpr; kExpr]) when (lookupVar "__ppl_cumulant" env).IsNone ->
        inferCumulantProj env dExpr kExpr

    // `math.matmul` as a FIRST-CLASS intrinsic (docs/plan-cpp-perf-exploitation.md).
    // The math elaborator rewrites a qualified `m.matmul(A, B)` to this
    // internal marker after validating the declared shapes, so a bare
    // `matmul(...)` still does not resolve (import-gated, like
    // `__ppl_cumulant` above). Instead of a synthesized Blade triple loop
    // specialized per (m, k, n), the call becomes an IR node that codegen
    // emits as one `blade_linalg::blade_matmul` call -- blocked/microkernel
    // GEMM is the one shape Blade-native loop code cannot approach, so it
    // earns a first-class node rather than a desugaring.
    | ExprKind.ExprApp ({ Kind = ExprKind.ExprVar "__math_matmul" }, [aExpr; bExpr]) when (lookupVar "__math_matmul" env).IsNone ->
        inferMatmul env aExpr bExpr

    // `math.eigh` as a FIRST-CLASS intrinsic. Same import-gated
    // internal-marker trick as `__math_matmul` above, but emitted
    // CONDITIONALLY: `MathElaborate` consults
    // `LinAlgPatterns.lapackAvailable()` and only rewrites to this marker
    // when LAPACK will be there; without it the elaborator synthesizes the
    // cyclic-Jacobi Blade source, which stays the default path and the only
    // thing the interp / diff-oracle differentials ever see.
    | ExprKind.ExprApp ({ Kind = ExprKind.ExprVar "__math_eigh" }, [aExpr]) when (lookupVar "__math_eigh" env).IsNone ->
        inferEigh env aExpr

    // `math.solve` as a FIRST-CLASS intrinsic. Import-gated internal marker
    // like `__math_matmul`, and emitted UNCONDITIONALLY like it too (NOT like
    // `__math_eigh`): the native arm is the emitted LU loop nest, so the node
    // is the only spelling of this operation and the LAPACK gate only decides
    // whether those loops are replaced by one `dgesv` call. That is the
    // difference eigh cannot have -- an eigensolver's output is not unique, so
    // its two arms could not be one verification truth, while an LU solve's is
    // (up to the last ULP) and its native arm is byte-pinned against the
    // interpreter.
    | ExprKind.ExprApp ({ Kind = ExprKind.ExprVar "__math_solve" }, [aExpr; bExpr]) when (lookupVar "__math_solve" env).IsNone ->
        inferSolve env aExpr bExpr

    | ExprKind.ExprApp (func, args) ->
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
            paramTys |> List.exists (fun t ->
                match env.Subst.Resolve t with IRTPoly _ -> true | _ -> false)
        match env.Subst.Resolve tFunc.Type with
        // Single `_` placeholder (formalism 6.2.3): one hole in an
        // otherwise-full application binds every other parameter and
        // leaves the hole's parameter free -- f(_, b) == lambda(x) -> f(x, b).
        // FuncElem-gated: array wildcard-indexing (the 4.5 currying table,
        // where MULTIPLE holes are legal) stays on the ArrayElem path.
        | FuncElem (paramTys, retTy) when not (hasPolyParam paramTys) && args |> List.exists (fun a -> match a.Kind with ExprKind.ExprWildcard -> true | _ -> false) ->
            let wildPositions =
                args |> List.mapi (fun i a -> (i, a))
                     |> List.choose (fun (i, a) -> match a.Kind with ExprKind.ExprWildcard -> Some i | _ -> None)
            if wildPositions.Length > 1 then
                Error (Other "function partial application takes a single `_` placeholder only (formalism 6.2.3) -- bind the rest with prefix partial application or a lambda")
            elif args.Length <> paramTys.Length then
                Error (PlaceholderNeedsAllBound (args.Length, paramTys.Length))
            else
                let wildPos = wildPositions.Head
                let uid = env.Builder.FreshId()
                let name = sprintf "__pa%d_w" uid
                let newArgs = args |> List.mapi (fun i a -> if i = wildPos then inheritSpan a (ExprVar name) else a)
                inferLambda env [{ Name = name; Type = None; Default = None; NameSpan = noSpan }] None (inheritSpan func (ExprApp (func, newArgs)))
                |> Result.bind (fun tLam ->
                    unify env.Subst tLam.Type (mkFuncArrow [paramTys.[wildPos]] retTy)
                    |> Result.map (fun () -> tLam))
        | FuncElem (paramTys, retTy) when not (hasPolyParam paramTys) && not args.IsEmpty && args.Length < paramTys.Length ->
            let residual = paramTys |> List.skip args.Length
            let uid = env.Builder.FreshId()
            let names = residual |> List.mapi (fun i _ -> sprintf "__pa%d_%d" uid i)
            let lamParams = names |> List.map (fun n -> { Name = n; Type = None; Default = None; NameSpan = noSpan } : LambdaParam)
            let bodyApp = inheritSpan func (ExprApp (func, args @ (names |> List.map (fun n -> inheritSpan func (ExprVar n)))))
            inferLambda env lamParams None bodyApp
            |> Result.bind (fun tLam ->
                // Pin the residual param types to the callee's declared
                // ones: direct application keeps its looseness
                // (no param-vs-arg unification), so nothing else would
                // resolve the lambda's fresh param inference vars.
                unify env.Subst tLam.Type (mkFuncArrow residual retTy)
                |> Result.map (fun () -> tLam))
        | _ ->
            args |> List.map (inferExpr env) |> sequenceResults |> Result.bind (fun tArgs ->
                // Call-site constraint DISCHARGE: if the callee declared custom
                // where-clause conjuncts (e.g. PPL's `indep(a, b)`), the caller
                // must prove them for the actual arguments -- each registered
                // handler gets the callee's conjunct args plus a provenance
                // oracle mapping callee param names to the actuals' provenance.
                let dischargeErr =
                    match func.Kind with
                    | ExprKind.ExprVar fname ->
                        match env.FuncConstraints.TryGetValue fname with
                        | true, (paramNames, conjuncts) ->
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
                | Some msg -> Error (Other msg)
                | None -> dispatchAppOrIndex env tFunc tArgs))

    // ---- Poly-tuple indexing OR array indexing (brackets) ----
    // `e[i]` is parsed as ExprTupleIndex regardless of e's type. Disambiguate
    // here: if e resolves to an IRTArray, this is conventional array
    // indexing and we route to TExprIndex (matching the function-call form
    // `e(i)` which goes through ExprApp's array branch at line 1518). If e
    // is a poly-pack (any other shape), keep TExprTupleIndex for IRPolyIndex
    // codegen.
    | ExprKind.ExprTupleIndex (tuple, index) ->
        inferTupleIndex env tuple index
    | ExprKind.ExprField ({ Kind = ExprKind.ExprVar n }, field)
        when (lookupVar (sprintf "%s.%s" n field) env).IsSome ->
        // Module-qualified value access (e.g. `let tau = Math.pi * 2.0`).
        // Same rationale as the qualified application case above.
        let qualName = sprintf "%s.%s" n field
        let info = (lookupVar qualName env).Value
        let useTy =
            match info.Scheme with
            | Some scheme -> instantiate env.Subst scheme
            | None -> info.Type
        Ok (mkTyped (TExprVar (qualName, info.VarId, info.Identity)) useTy)

    | ExprKind.ExprField (obj, field) ->
        inferExpr env obj |> Result.bind (fun tObj ->
            let (fieldTy, fieldIdx) =
                match tObj.Type with
                | IRTNamed typeName ->
                    match lookupTypeDef typeName env with
                    | Some (TDIStruct (_, _, fields, _)) ->
                        let idx = fields |> List.tryFindIndex (fun (n, _) -> n = field)
                        let ty = fields |> List.tryFind (fun (n, _) -> n = field) |> Option.map snd
                        (ty |> Option.defaultValue (env.Subst.Fresh()),
                         idx |> Option.defaultValue 0)
                    | _ -> (env.Subst.Fresh(), 0)
                | _ -> (env.Subst.Fresh(), 0)
            Ok (mkTyped (TExprField (tObj, field, fieldIdx)) fieldTy))

    // ---- Lambda ----
    | ExprKind.ExprLambda (parms, whereClause, body) -> inferLambda env parms whereClause body

    // ---- Let ----
    | ExprKind.ExprLet (binding, body) -> inferLetBinding env binding body

    // ---- Match ----
    | ExprKind.ExprMatch (scrutinee, cases) -> inferMatch env scrutinee cases

    // ---- If-then-else ----
    | ExprKind.ExprIf (cond, thenBr, elseBr) ->
        inferExpr env cond |> Result.bind (fun tCond ->
        inferExpr env thenBr |> Result.bind (fun tThen ->
        inferExpr env elseBr |> Result.bind (fun tElse ->
            let _ = unify env.Subst tThen.Type tElse.Type
            Ok (mkTyped (TExprIf (tCond, tThen, tElse)) tThen.Type))))

    // ---- Tuple ----
    | ExprKind.ExprTuple exprs ->
        exprs |> List.map (inferExpr env) |> sequenceResults |> Result.bind (fun tExprs ->
            Ok (mkTyped (TExprTuple tExprs) (IRTTuple (tExprs |> List.map (fun e -> e.Type)))))

    // ---- Array literal ----
    | ExprKind.ExprArrayLit elems ->
        elems |> List.map (inferExpr env) |> sequenceResults |> Result.bind (fun tElems ->
            let arrTy = inferArrayLitType env.Builder tElems
            Ok (mkTyped (TExprArrayLit (tElems, arrTy)) (mkArrayLike arrTy)))

    // ---- Block ----
    | ExprKind.ExprBlock (stmts, finalExpr) -> inferBlock env stmts finalExpr None

    // ---- Loop constructs ----
    | ExprKind.ExprMethodFor arrays -> inferMethodFor env arrays
    | ExprKind.ExprObjectFor kernel -> inferObjectFor env kernel

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
    | ExprKind.ExprRange idxTys ->
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
        |> Result.bind (fun idxs ->
        // A CompoundIdx slot (masked product space, formalism 4.5) IS the whole
        // iteration space, so it cannot share a range<> with other index types.
        // Reject range<CompoundIdx<m>, J> HERE at typecheck (EXPECT: typecheck
        // failure) rather than letting it fall through and leak a codegen #error
        // plus a cascade of undeclared-variable errors into the generated C++.
        // A SOLE compound slot (idxs.Length = 1) is fine and passes through.
        let hasCompound =
            idxs |> List.exists (fun ix -> (match ix.Extent with IRCompoundMask _ -> true | _ -> false))
        let hasSparse =
            idxs |> List.exists (fun ix -> (match ix.Extent with IRSparseKeys _ -> true | _ -> false))
        // A wreath range slot is the storage refusal's THIRD front-end door
        // (beside the let annotation and the function signature): `range<...>`
        // names an ITERATION space and needs no annotation, so without this the
        // program reaches buildRawLoopLevels' backstop and surfaces as a BL9001
        // internal error -- accurate about the failure, wrong about whose fault
        // it is.
        match idxs |> List.tryFind (fun ix -> ix.Symmetry = SymWreath) with
        | Some ix ->
            Error (OrbitStorageUnsupported (ppOrbitLevels (orbitLevelsOf ix), "range<> iteration slot"))
        | None ->
        if hasCompound && idxs.Length > 1 then
            Error (Other "range<CompoundIdx<m>, ...>: a compound range slot cannot be combined with other index types in one range<> (formalism 4.5)")
        elif hasSparse && idxs.Length > 1 then
            // Same whole-iteration-space rule as the compound slot: the key
            // enumeration IS the space.
            Error (Other "range<SparseIdx<keys>, ...>: a sparse range slot cannot be combined with other index types in one range<> (formalism 3.5)")
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
        Ok (mkTyped (TExprRange idxs) (mkVirtualArrayArrow idxs elemType)))
    | ExprKind.ExprDotDot (lo, hi) ->
        inferExpr env lo |> Result.bind (fun tLo ->
        inferExpr env hi |> Result.bind (fun tHi ->
            let extentExpr =
                match tLo.Kind, tHi.Kind with
                | TExprLit (LitInt 0L), TExprLit (LitInt n) -> IRLit (IRLitInt n)
                | TExprLit (LitInt a), TExprLit (LitInt b) -> IRLit (IRLitInt (b - a))
                | _ -> IRLit (IRLitInt 0L)  // placeholder -- Lowering computes actual extent
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
    | ExprKind.ExprReverse idxTy ->
        let idx = lowerIndexType env 0 idxTy
        let elemType = elemTypeForIterationIndex idx
        Ok (mkTyped (TExprReverse idx) (mkVirtualArrayArrow [idx] elemType))
    | ExprKind.ExprBlocked (idxTy, blockSize) ->
        let idx = lowerIndexType env 0 idxTy
        inferExpr env blockSize |> Result.bind (fun tBS ->
            let elemType = elemTypeForIterationIndex idx
            Ok (mkTyped (TExprBlocked (idx, tBS)) (mkVirtualArrayArrow [idx] elemType)))

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
        inferStack env exprs
    | ExprKind.ExprJoin (arrays, dim) ->
        inferJoin env arrays dim

    // ---- Computation combinators ----
    | ExprKind.ExprPure e ->
        inferExpr env e |> Result.bind (fun tE ->
            Ok (mkTyped (TExprPure tE) (IRTComputation tE.Type)))
    | ExprKind.ExprCompute e ->
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
        inferExpr env dense |> Result.bind (fun tDense ->
        inferExpr env mask |> Result.bind (fun tMask ->
            match env.Subst.Resolve(tDense.Type), env.Subst.Resolve(tMask.Type) with
            | ArrayElem denseArr, ArrayElem maskArr ->
                (match compoundViewType (env.Builder.FreshId()) denseArr maskArr (IRLit IRLitUnit) with
                 | Ok compoundTy ->
                     Ok (mkTyped (TExprCompound (tDense, tMask)) compoundTy)
                 | Error msg -> Error (Other msg))
            | _ -> Error (Other "compound(dense, mask) expects two array arguments: a dense array and a bool mask covering its leading dimensions")))

    | ExprKind.ExprSparse (values, keys) ->
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
                     Error (Other "sparse(values, keys): values must be an array whose leading dimension is the key axis (one cell per key, in key order)")
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
                      | Error msg -> Error (Other msg))
                 | keyAxis :: _ ->
                     Error (Other (sprintf "sparse(values, keys): the LEADING values dimension is the key axis and must be a plain rank-1 index; got a rank-%d (compact/tabulated) slot. Trailing dimensions may be any shape." keyAxis.Rank)))
            | _ -> Error (Other "sparse(values, keys) expects a values array (leading dim = key axis, remaining dims trailing) and a key list (a `let static` tuple list or a rank-1 tuple-array variable)")))

    // intersect(A, B) / union(A, B) -- set operations on arrays
    | ExprKind.ExprIntersect (a, b) | ExprKind.ExprUnion (a, b) ->
        let isIntersect = match expr.Kind with ExprKind.ExprIntersect _ -> true | _ -> false
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

    // contains(A, x) -- membership test. Returns Bool. The value's type
    // must unify with the array's element type; mismatch (e.g., looking
    // for a Float64 in an Int64 array) is a hard error.
    | ExprKind.ExprContains (a, value) ->
        inferExpr env a |> Result.bind (fun tA ->
        inferExpr env value |> Result.bind (fun tValue ->
            requireArrayArg env tA "contains" |> Result.bind (fun arrTy ->
                unify env.Subst tValue.Type arrTy.ElemType
                |> Result.bind (fun () ->
                    Ok (mkTyped (TExprContains (tA, tValue)) (IRTScalar ETBool))))))

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
        inferGroupKeys env keys
    | ExprKind.ExprGroupBy (values, grouping) ->
        inferGroupBy env values grouping
    | ExprKind.ExprSort (array, key) ->
        inferExpr env array |> Result.bind (fun tArr ->
        inferExpr env key |> Result.bind (fun tKey ->
            requireArrayArg env tArr "sort" |> Result.bind (fun arrTy ->
                if arrTy.IndexTypes.Length <> 1 then
                    Error (Other "sort() requires a rank-1 array (multi-rank sort not yet supported)")
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
                    let resultType = mkArrayArrow [resultIdx] arrTy.ElemType None
                    Ok (mkTyped (TExprSort (tArr, tKey)) resultType))))

    | ExprKind.ExprTranspose (array, d1, d2) ->
        inferTranspose env array d1 d2
    | ExprKind.ExprGram (leftE, rightE) ->
        inferGram env leftE rightE
    | ExprKind.ExprDecompact (array, d) ->
        inferDecompact env array d
    | ExprKind.ExprReduce (array, kernel, init, axes) ->
        inferReduce env array kernel init axes
    | ExprKind.ExprExtents array ->
        inferExtents env array
    | ExprKind.ExprSequence exprs ->
        inferSequence env exprs
    | ExprKind.ExprReplicate (count, body) ->
        inferReplicate env count body
    | ExprKind.ExprReynolds (kernel, isAntisym) ->
        inferExpr env kernel |> Result.bind (fun tK ->
            Ok (mkTyped (TExprReynolds (tK, isAntisym)) tK.Type))

    // ---- Type annotation ----
    | ExprKind.ExprTyped (e, tyAnno) ->
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
        checkExpr env annoTy e |> Result.map (fun tE ->
            { tE with Type = annoTy })

    // ---- Arity special forms ----
    | ExprKind.ExprArity paramName -> Ok (mkTyped (TExprArity paramName) (IRTScalar ETInt64))
    | ExprKind.ExprNth -> Ok (mkTyped (TExprLit (LitInt 0L)) (IRTScalar ETInt64))
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
        inferExpr env lhs |> Result.bind (fun tL ->
        // Bidirectional: check the RHS against the target's type so literals
        // adapt (Int64 literal into an Int32 field) as in every other
        // checked position.
        checkExpr env tL.Type rhs |> Result.bind (fun tR ->
            // Check assignability of LHS
            let assignErr =
                match tL.Kind with
                | TExprVar (name, _, _) ->
                    match lookupVar name env with
                    | Some info when info.Assign = ReadOnly ->
                        Some (ImmutableStaticAssign name)
                    | _ -> None
                | _ -> None  // array element assignment etc. -- allowed
            match assignErr with
            | Some e -> Error e
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

    // ---- For expression ----
    | ExprKind.ExprFor (source, _constraints, kernelOpt) ->
        inferForExpr env source kernelOpt

    // ---- Align ----
    | ExprKind.ExprAlign (exprs, spec) ->
        exprs |> List.map (inferExpr env) |> sequenceResults |> Result.bind (fun tExprs ->
            let ty = if tExprs.IsEmpty then IRTUnit else tExprs.[0].Type
            Ok (mkTyped (TExprAlign (tExprs, spec)) ty))

// 10a. Binary Operation Inference


/// Detect and type the fused reduction terminal. Returns None when the
/// first argument is NOT a deferred computation (ordinary array reduce
/// proceeds). Some (Ok ...) types the fold: the child spliced into the
/// typed node is a CANONICAL left-nested fusion over the RESOLVED leaves
/// (one let-hop each, the <@> resolution rule), so lowering and codegen
/// never chase variable bindings. Result type: elem for a single apply,
/// left-nested scalar pairs for a tree (mirroring `|> compute`'s tuple
/// convention -- nested pair TYPE, flat make_tuple VALUE, flat projections).
and tryInferReduceCompute (env: TypeEnv) (tArr: TypedExpr) (tKernel: TypedExpr) (tInitOpt: TypedExpr option) : TypeResult<TypedExpr> option =
    // Collect fusion leaves left-to-right, resolving each through bindings.
    // None = the root is not a deferred computation at all (fall through);
    // Some (Error _) = it IS deferred but malformed for a fused fold.
    let rec collect (t: TypedExpr) : Result<TypedExpr list, TypeError> option =
        let r = resolveTypedExpr env t
        match r.Kind with
        | TExprFusion (l, rgt) ->
            (match collect l, collect rgt with
             | Some (Ok ls), Some (Ok rs) -> Some (Ok (ls @ rs))
             | Some (Error e), _ | _, Some (Error e) -> Some (Error e)
             | _ ->
                Some (Error (Other "reduce() over a fused tree requires every <&!> leaf to be an unforced `method_for/object_for <@> kernel` application")))
        | TExprApply info when not info.IsComposeApply -> Some (Ok [r])
        | TExprApply _ ->
            Some (Error (Other "reduce() over a composed (>>@/@>>) application is not supported yet -- force it with `|> compute` and reduce the resulting array"))
        | _ -> None
    match collect tArr with
    | None -> None
    | Some leavesR ->
        Some (
            leavesR |> Result.bind (fun leaves ->
            // Each leaf must produce plain (non-compact) cells: folding
            // canonical vs logical cells of symmetric storage differ, the
            // same ambiguity the array form rejects.
            let leafElem (leaf: TypedExpr) : Result<IRType, TypeError> =
                match leaf.Kind with
                | TExprApply info ->
                    (match env.Subst.Resolve info.OutputType with
                     | ArrayElem arr ->
                        let packed =
                            arr.IndexTypes |> List.exists (fun ix ->
                                match ix.Symmetry with SymNone -> false | _ -> true)
                        if packed then
                            Error (Other "reduce() over a computation with compact symmetric/antisymmetric/Hermitian output is not supported: folding the canonical cells and folding the logical (mirrored) cells differ. Force with `|> compute` and decompact(A, d) first for the logical fold.")
                        else Ok arr.ElemType
                     | IRTScalar _ as s -> Ok s
                     | _ -> Error (Other "reduce() over a deferred computation needs an array-producing kernel application"))
                | _ -> Error (Other "reduce(): internal -- fusion leaf is not an apply")
            leaves |> List.map leafElem |> sequenceResults |> Result.bind (fun elems ->
            let elem0 = elems.Head
            elems.Tail
            |> List.fold (fun acc e -> acc |> Result.bind (fun () -> unify env.Subst e elem0)) (Ok ())
            |> Result.bind (fun () ->
            // Fold-kernel params and init share the leaves' element type
            // (same unification the array form performs).
            (match env.Subst.Resolve(tKernel.Type) with
             | FuncElem (paramTys, _) ->
                paramTys |> List.fold (fun acc pTy ->
                    acc |> Result.bind (fun () -> unify env.Subst pTy elem0)) (Ok ())
             | _ -> Ok ())
            |> Result.bind (fun () ->
            (match tInitOpt with
             | Some tInit -> unify env.Subst tInit.Type elem0
             | None -> Ok ())
            |> Result.bind (fun () ->
            // Seed: user's init, else the section's identity. A fused nest
            // cannot seed-with-first (no single first cell across a
            // multi-dim or multi-leaf nest), so everything else is an error.
            let seed : Result<TypedExpr, TypeError> =
                match tInitOpt with
                | Some tInit -> Ok tInit
                | None ->
                    let et = match env.Subst.Resolve elem0 with AnyPrimElem e -> e | _ -> ETFloat64
                    (match tKernel.Kind with
                     | TExprSection OpAdd ->
                        let lit = match et with ETInt32 | ETInt64 -> TExprLit (LitInt 0L) | _ -> TExprLit (LitFloat 0.0)
                        Ok (mkTyped lit elem0)
                     | TExprSection OpMul ->
                        let lit = match et with ETInt32 | ETInt64 -> TExprLit (LitInt 1L) | _ -> TExprLit (LitFloat 1.0)
                        Ok (mkTyped lit elem0)
                     | TExprSection _ ->
                        Error (Other "reduce() over a deferred computation requires an explicit init for this kernel (3-arg form `reduce(c, op, init)`) -- only (+) and (*) carry implicit identities")
                     | _ ->
                        Error (Other "reduce() over a deferred computation requires an explicit init for a lambda kernel (3-arg form `reduce(c, op, init)`) -- a fused fold cannot seed from its first element"))
            seed |> Result.map (fun tSeed ->
            let rebuilt =
                match leaves with
                | [one] -> one
                | first :: rest ->
                    rest |> List.fold (fun acc leaf ->
                        mkTyped (TExprFusion (acc, leaf)) (IRTTuple [acc.Type; leaf.Type])) first
                | [] -> tArr
            let resultType =
                match leaves with
                | [_] -> elem0
                | _ :: rest -> rest |> List.fold (fun acc _ -> IRTTuple [acc; elem0]) elem0
                | [] -> elem0
            mkTyped (TExprReduce (rebuilt, tKernel, Some tSeed)) resultType)))))))

and inferReduce (env: TypeEnv) array kernel (init: Expr option) (axes: Expr option) : TypeResult<TypedExpr> =
    // ---- Axis count (`axes = n`, default 1) --------------------------------
    // `reduce` folds the innermost `n` axes RIGHT-TO-LEFT: rank k in, rank k-n
    // out, and n = k is the full fold to a scalar. `n` must be an integer
    // LITERAL -- the result RANK is k - n, a static property of the type, so a
    // symbolic count would make the result type depend on a runtime value.
    let axisCountR : Result<int option, TypeError> =
        match axes with
        | None -> Ok None
        | Some ae ->
            match ae.Kind with
            | ExprKind.ExprLit (LitInt n) -> Ok (Some (int n))
            | _ ->
                Error (Other "reduce: `axes = n` requires an integer literal -- the result rank is rank(A) - n, so a symbolic axis count (a variable, a parameter, or an expression) has no static result type (deferred)")
    axisCountR |> Result.bind (fun axisCountOpt ->
    // The operand, inferred ONCE for this whole function. Everything below
    // that needs the operand's shape -- the rank probe, the element type, the
    // gates, the row annotation -- reads this, and `rankKDesugar` takes it as
    // an argument instead of re-inferring. Inference is not free of visible
    // side effects: each run mints fresh ids, and an extra run SHIFTS every
    // generated `__lambda_N` / `__vN` downstream. Sharing one keeps the
    // emitted C++ of an unchanged program byte-identical, which is the point.
    let tArrCache = lazy (inferExpr env array)
    // Statically-known rank of the fold's OPERAND: an array's index-type
    // count, or -- for the fused reduction terminal -- the deferred
    // computation's output rank (a fused fold walks the same cell grid).
    // None whenever the rank is not yet pinned (an unconstrained inference
    // variable, e.g. `lambda(g) -> reduce(g, (+))`); with the default n = 1
    // that leaves every existing path byte-identical.
    let operandRank () : int option =
        match tArrCache.Force() with
        | Error _ -> None
        | Ok t ->
            let rankOfOut (ty: IRType) =
                match env.Subst.Resolve ty with
                | ArrayElem at -> Some at.IndexTypes.Length
                | _ -> None
            match rankOfOut t.Type with
            | Some r -> Some r
            | None ->
                // Deferred computation: a SINGLE apply leaf answers with its
                // output rank, so `reduce(A <@> f, (+))` partial-folds exactly
                // like the array it would have materialized into.
                //
                // An `<&!>` fusion TREE deliberately answers None -- it has no
                // partial form to give. Its leaves may have DIFFERENT ranks
                // (the staggered-arity nest accumulates each leaf at its own
                // depth) and its result is a TUPLE of scalars, so "fold the
                // innermost axis" names no single axis and no array shape.
                // The tree terminal stays the full fold it has always been.
                match (resolveTypedExpr env t).Kind with
                | TExprApply info when not info.IsComposeApply -> rankOfOut info.OutputType
                | _ -> None
    // The operand's ELEMENT type, by the same walk. Needed at this level for
    // the units gate below, which cannot fire from inside the synthesized
    // kernel body.
    let operandElem () : IRType option =
        match tArrCache.Force() with
        | Error _ -> None
        | Ok t ->
            let elemOfOut (ty: IRType) =
                match env.Subst.Resolve ty with
                | ArrayElem at -> Some at.ElemType
                | _ -> None
            match elemOfOut t.Type with
            | Some e -> Some e
            | None ->
                // Single apply leaf only, matching `operandRank` -- a fusion
                // tree never reaches the partial path, so it needs no element.
                let leafElem (te: TypedExpr) =
                    match (resolveTypedExpr env te).Kind with
                    | TExprApply info when not info.IsComposeApply -> elemOfOut info.OutputType
                    | _ -> None
                leafElem t
    let rk = operandRank ()
    let axisRangeErr =
        match axisCountOpt with
        | Some n when n < 1 ->
            Some (Other (sprintf "reduce: `axes = %d` is out of range -- the axis count must be at least 1 (a zero-axis fold is the identity on A, so it has no fold to perform)" n))
        | Some n ->
            (match rk with
             | Some r when n > r ->
                Some (Other (sprintf "reduce: `axes = %d` exceeds the operand's rank %d -- the axis count must satisfy 1 <= n <= rank(A), and n = %d is the full fold to a scalar" n r r))
             | _ -> None)
        | None -> None
    match axisRangeErr with
    | Some e -> Error e
    | None ->
    // ---- PARTIAL fold (n < rank): the row-mode apply --------------------
    // A rank-k operand folded over its innermost n axes is exactly the
    // documented row-wise idiom one level up:
    //
    //     (method_for(A) <@> lambda(row) -> reduce(row, op[, init])) |> compute
    //
    // so the partial form is REWRITTEN into it rather than given its own IR
    // node: no new node, no new loop, no new codegen. Everything the fold owes
    // -- the seed / empty-group rule, the compact-storage refusal on the
    // folded axis, the `omp` fold licence, the interpreter and codegen paths
    // -- is inherited from the `reduce` now sitting in the kernel body, and
    // the outer iteration is the proven `<@>` former. (The two checks the body
    // position CANNOT perform, because they read a type that is not pinned
    // until after the body is typed, are lifted to this level below: units and
    // statically-empty groups.) For 1 < n < rank the body is the row's own
    // FULL fold and the row parameter carries its slice type -- the hosvd
    // corpus's hand-written shape. n = rank skips all of this and keeps the
    // existing full-fold nest byte-identical.
    let partialFold (r: int) (n: int) : TypeResult<TypedExpr> option =
        let span = array.Span
        let synAt k = mkExpr span k
        // UNITS. The endomorphism check the rank-1 fold performs cannot fire
        // from inside the synthesized kernel body: the row parameter is still
        // an unresolved inference variable while that body is typed (its
        // element type is pinned later, at the apply's perRowType
        // unification), so `IR.getUnits` sees nothing there and a `*` fold
        // over `Float64<meter>` would be waved through and then MISLABELLED
        // meter (it is meter^2). Run the identical check HERE, where the
        // operand's element type is known -- same function, same message.
        let unitGate : TypeResult<unit> =
            match operandElem () with
            | Some et ->
                (match inferExpr env kernel with
                 | Ok tk -> reduceKernelUnitCheck env tk et
                 | Error _ -> Ok ())
            | None -> Ok ()
        // COMPACT FOLDED AXES. The rank-1 form refuses to fold a
        // symmetric/antisymmetric/Hermitian record (canonical cells and
        // logical mirrored cells give different answers). The partial form
        // inherits the REFUSAL through the row parameter -- but as an
        // unification failure about index slots, since the fresh rank-1 row it
        // mints cannot match a rank-k compact group. Say the real reason here
        // instead, in the rank-1 form's own words.
        let compactGate : TypeResult<unit> =
            match tArrCache.Force() |> Result.map (fun t -> env.Subst.Resolve t.Type) with
            | Ok (ArrayElem at) when at.IndexTypes.Length >= n ->
                if at.IndexTypes |> List.skip (at.IndexTypes.Length - n)
                                 |> List.exists (fun ix -> ix.Symmetry <> SymNone) then
                    Error (Other "reduce() over compact symmetric/antisymmetric/Hermitian storage is not supported: folding the canonical cells and folding the logical (mirrored) cells differ. decompact(A, d) first for the logical fold.")
                else Ok ()
            | _ -> Ok ()
        // EMPTY GROUPS. Same reason as the units gate: the rank-1 form's
        // statically-empty rejection reads the ROW's extent, which is not yet
        // pinned inside the synthesized body, so a `Idx<0>` folded axis would
        // slip through to a runtime panic. Check the FOLDED (innermost n)
        // axes here. With an init the empty fold is defined (it is init), so
        // this fires only for the seedless form -- the rank-1 rule verbatim.
        let emptyGate : TypeResult<unit> =
            if init.IsSome then Ok ()
            else
                match tArrCache.Force() |> Result.map (fun t -> env.Subst.Resolve t.Type) with
                | Ok (ArrayElem at) when at.IndexTypes.Length >= n ->
                    at.IndexTypes
                    |> List.skip (at.IndexTypes.Length - n)
                    |> List.tryPick (fun ix ->
                        match tryEvalIntIR ix.Extent with
                        | Some e when e <= 0L -> Some e
                        | _ -> None)
                    |> function
                       | Some e -> Error (ReduceEmptyArray e)
                       | None -> Ok ()
                | _ -> Ok ()
        match unitGate |> Result.bind (fun () -> compactGate) |> Result.bind (fun () -> emptyGate) with
        | Error e -> Some (Error e)
        | Ok () ->
        let uid = env.Builder.FreshId()
        let rowName = sprintf "__pfrow%d" uid
        let rowVar = synAt (ExprVar rowName)
        // The former's source. The row-mode apply hands out ROW VIEWS of it,
        // and codegen names those views after the source buffer -- so the
        // source has to BE a named buffer.
        //
        // A bare variable already is one: it goes in directly, and binding it
        // to a synthetic `let` first would emit a pointless full `std::copy_n`
        // of the operand (measured in the emitted C++) for a source the apply
        // reads exactly once. This is the hot shape (`reduce(A, (+))`).
        //
        // Anything else -- a deferred `A <@> f`, a bracketed product, an array
        // literal -- has no name, and codegen emits a reference to an
        // undeclared buffer ("'arr0' was not declared in this scope",
        // measured). Those get the `let`, which also forces a deferred operand
        // exactly once: the fused terminal has no partial form (it collapses a
        // whole nest into scalars), so the cells must exist before rows can be
        // handed out.
        let srcIsNamed = match array.Kind with ExprKind.ExprVar _ -> true | _ -> false
        let srcName = sprintf "__pfsrc%d" uid
        // Kernel body + row-parameter annotation.
        //
        // n = 1: `reduce(row, op[, init])`, the rank-1 fold, with the row
        // parameter UNANNOTATED -- byte-identical to the row-wise idiom the
        // corpus already writes by hand, whose rank the inner reduce itself
        // establishes (the fresh rank-1 array it mints for an unconstrained
        // operand).
        //
        // n > 1: the body is the row's own FULL fold, `reduce(row, op[,
        // init], axes = n)`, and the row parameter MUST carry its type. The
        // full fold needs static extents, and inside an unannotated kernel
        // body the row is still an inference variable when that body is
        // typed -- the extents arrive later, at perRowType, far too late.
        // (Measured: the unannotated spelling types the row as a SCALAR and
        // emits `row(i, j)` as a call, which does not compile.) With the
        // annotation it is exactly the hand-written hosvd shape
        // `lambda(face: Array<Float64 like Idx<3>, Idx<3>>) -> reduce(face,
        // (+), axes = 2)`, which is proven. The annotation is only
        // constructible for a dense, statically-sized, untagged, unitless
        // scalar slice; anything else is refused and pointed at the manual
        // spelling, where the author can write the type the printer shows.
        let rowAnnotR : Result<TypeExpr option, TypeError> =
            if n = 1 then Ok None
            else
                let bail () =
                    Error (Other (sprintf "reduce: `axes = %d` over a rank-%d operand needs the folded slice to be dense (plain, untagged, non-compact) with static extents and a unitless scalar element -- write the row-wise form explicitly instead: `method_for(A) <@> lambda(row: <the slice type>) -> reduce(row, op, axes = %d) |> compute`" n r n))
                match tArrCache.Force() |> Result.map (fun t -> env.Subst.Resolve t.Type) with
                | Ok (ArrayElem at) when at.IndexTypes.Length = r ->
                    let innerDims = at.IndexTypes |> List.skip (r - n)
                    let elemTy = env.Subst.Resolve at.ElemType
                    let elemTE =
                        match elemTy, IR.getUnits elemTy with
                        | _, Some _ -> None                       // units cannot be re-rendered here
                        | IRTScalar ETFloat64, _ -> Some TyFloat64
                        | IRTScalar ETFloat32, _ -> Some TyFloat32
                        | IRTScalar ETInt64, _ -> Some TyInt64
                        | IRTScalar ETInt32, _ -> Some TyInt32
                        | IRTScalar ETComplex128, _ -> Some TyComplex128
                        | IRTScalar ETComplex64, _ -> Some TyComplex64
                        | IRTScalar ETBool, _ -> Some TyBool
                        | _ -> None
                    let slotTEs =
                        innerDims |> List.map (fun ix ->
                            if ix.IxKind <> IxKPlain || ix.Symmetry <> SymNone
                               || ix.Tag.IsSome || ix.Rank <> 1 then None
                            else
                                match tryEvalIntIR ix.Extent with
                                | Some e when e > 0L -> Some (TyIdx (synAt (ExprLit (LitInt e))))
                                | _ -> None)
                    match elemTE with
                    | Some et when slotTEs |> List.forall Option.isSome ->
                        Ok (Some (TyArray (et, slotTEs |> List.map Option.get)))
                    | _ -> bail ()
                | _ -> bail ()
        match rowAnnotR with
        | Error e -> Some (Error e)
        | Ok rowAnnot ->
            let body =
                if n = 1 then synAt (ExprReduce (rowVar, kernel, init, None))
                else synAt (ExprReduce (rowVar, kernel, init, Some (synAt (ExprLit (LitInt (int64 n))))))
            let lam = synAt (ExprLambda ([{ Name = rowName; Type = rowAnnot; Default = None; NameSpan = span }], None, body))
            let appliedOver (src: Expr) =
                synAt (ExprCompute (synAt (ExprBinOp (Elementwise, OpApply,
                                                      synAt (ExprMethodFor [src]), lam))))
            if srcIsNamed then Some (inferExpr env (appliedOver array))
            else
                Some (inferExpr env (synAt (ExprBlock (
                        [ StmtLet { Mutability = BindLet
                                    Pattern = mkPat span (PatVar srcName)
                                    Type = None
                                    Value = array } ],
                        Some (appliedOver (synAt (ExprVar srcName)))))))
    let partialResult =
        match rk with
        | Some r when r >= 2 && (defaultArg axisCountOpt 1) < r -> partialFold r (defaultArg axisCountOpt 1)
        | _ -> None
    match partialResult with
    | Some result -> result
    | None ->
    // ---- Rank-k dense fold (k >= 2): desugar to the internal loop nest ----
    // Folds every element in DECLARED (row-major) order through the kernel
    // with one scalar accumulator -- byte-identical to the imperative
    // accumulation nest it replaces (the hosvd/math-corpus port shape and
    // the structured-binning route). Bounds: static extents, dense
    // non-symmetric axes, scalar elements. Seed = the 3-arg init, or the
    // (+)/(*) identity for operator sections (a rank-k nest cannot
    // seed-with-first without a per-element guard).
    let rankKDesugar () =
        match tArrCache.Force() with
        | Error _ -> None
        | Ok tArr0 ->
            // A DEFERRED rank-k computation whose fold kernel asked for `omp`
            // is deliberately NOT desugared here. The desugar MATERIALIZES the
            // computation into a temporary and folds it with a hand-built nest
            // that carries no clause at all -- so a licensed `where comm, omp`
            // would come out serial with no trace, which is precisely the
            // silent-drop failure this feature exists to avoid. Declining sends
            // it to the fused reduction terminal instead (tryInferReduceCompute
            // below), which needs no intermediate array AND can chunk the outer
            // level. Everything else -- including every unannotated rank-k
            // reduce -- is untouched.
            let deferredWithOmpKernel () =
                match (resolveTypedExpr env tArr0).Kind with
                | TExprApply _ | TExprFusion _ ->
                    (match inferExpr env kernel with
                     | Ok tk ->
                        (match (resolveTypedExpr env tk).Kind with
                         | TExprLambda li ->
                            li.Parallel |> List.exists (function Omp _ -> true | _ -> false)
                         | TExprVar (fn, _, _) ->
                            (match env.FuncParallel.TryGetValue fn with
                             | true, (_, s) -> s |> List.exists (function Omp _ -> true | _ -> false)
                             | _ -> false)
                         | _ -> false)
                     | Error _ -> false)
                | _ -> false
            match env.Subst.Resolve tArr0.Type with
            | ArrayElem at when at.IndexTypes.Length >= 2
                                && at.IndexTypes |> List.forall (fun ix ->
                                       ix.IxKind = IxKPlain && ix.Symmetry = SymNone)
                                && (match env.Subst.Resolve at.ElemType with IRTScalar _ -> true | _ -> false)
                                && not (deferredWithOmpKernel ()) ->
                let extents =
                    at.IndexTypes |> List.map (fun ix ->
                        match ix.Extent with IRLit (IRLitInt n) -> Some n | _ -> None)
                if extents |> List.exists Option.isNone then None
                else
                    let ns = extents |> List.map Option.get
                    let span = array.Span
                    let synAt k = mkExpr span k
                    let iLit (v: int64) = synAt (ExprLit (LitInt v))
                    let elemTy = env.Subst.Resolve at.ElemType
                    let zeroOf () =
                        match elemTy with
                        | IRTScalar (ETFloat64 | ETFloat32) -> Some (synAt (ExprLit (LitFloat 0.0)))
                        | IRTScalar (ETInt64 | ETInt32) -> Some (synAt (ExprLit (LitInt 0L)))
                        | IRTScalar (ETComplex128 | ETComplex64) ->
                            Some (synAt (ExprApp (synAt (ExprVar "complex"),
                                                  [synAt (ExprLit (LitFloat 0.0)); synAt (ExprLit (LitFloat 0.0))])))
                        | _ -> None
                    let oneOf () =
                        match elemTy with
                        | IRTScalar (ETFloat64 | ETFloat32) -> Some (synAt (ExprLit (LitFloat 1.0)))
                        | IRTScalar (ETInt64 | ETInt32) -> Some (synAt (ExprLit (LitInt 1L)))
                        | _ -> None
                    let seedOpt =
                        match init, kernel.Kind with
                        | Some e, _ -> Some e
                        | None, ExprKind.ExprSection OpAdd -> zeroOf ()
                        | None, ExprKind.ExprSection OpMul -> oneOf ()
                        | _ -> None
                    match seedOpt with
                    | None -> Some (Error (Other "reduce over a rank >= 2 array needs an explicit init (3-arg reduce) unless the kernel is a (+) or (*) section"))
                    | Some seed ->
                        let uid = env.Builder.FreshId()
                        let srcName = sprintf "__rksrc%d" uid
                        let accName = sprintf "__rkacc%d" uid
                        let srcVar = synAt (ExprVar srcName)
                        let accVar = synAt (ExprVar accName)
                        let ivars = ns |> List.mapi (fun k _ -> sprintf "__rk%d_%d" uid k)
                        let elemRead = synAt (ExprApp (srcVar, ivars |> List.map (fun v -> synAt (ExprVar v))))
                        let combined =
                            match kernel.Kind with
                            | ExprKind.ExprSection op -> synAt (ExprBinOp (Elementwise, op, accVar, elemRead))
                            | _ -> synAt (ExprApp (kernel, [accVar; elemRead]))
                        let assign = StmtExpr (synAt (ExprAssign (accVar, combined)))
                        let nest =
                            List.foldBack2 (fun ivar ext inner ->
                                [ StmtForIn (ivar, synAt (ExprKind.ExprDotDot (iLit 0L, iLit ext)), inner) ])
                                ivars ns [assign]
                        let block =
                            synAt (ExprBlock (
                                [ StmtLet { Mutability = BindLet; Pattern = mkPat span (PatVar srcName); Type = None; Value = array }
                                  StmtLet { Mutability = BindMut; Pattern = mkPat span (PatVar accName); Type = None; Value = seed } ]
                                @ nest, Some accVar))
                        Some (inferExpr env block)
            | _ -> None
    match rankKDesugar () with
    | Some result -> result
    | None ->
    inferExpr env array |> Result.bind (fun tArr ->
    inferExpr env kernel |> Result.bind (fun tKernel ->
    (match init with
     | Some e -> inferExpr env e |> Result.map Some
     | None -> Ok None) |> Result.bind (fun tInitOpt ->
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
        | Some result -> result
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
         | IRTInfer _ ->
            let elemType = deduceElemFromKernel ()
            // One axis per FOLDED axis. The default is one (rank-1 -- the
            // shape every existing `lambda(g) -> reduce(g, (+))` receives, so
            // this arm is unchanged for it); an explicit `axes = n` over an
            // operand whose rank is not yet pinned says the operand carries at
            // least n axes, and minting only one would silently fold an
            // n-axis request as a rank-1 one.
            let nSlots = max 1 (defaultArg axisCountOpt 1)
            let freshIdxs =
                [ for _ in 1 .. nSlots ->
                    { Id = env.Builder.FreshId()
                      Rank = 1
                      Extent = IRParam ("__inferred_n", 0, IRTNat None)
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
            Error (Other "reduce() over compact symmetric/antisymmetric/Hermitian storage is not supported: folding the canonical cells and folding the logical (mirrored) cells differ. decompact(A, d) first for the logical fold.")
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
                initUnify |> Result.bind (fun () ->
                reduceKernelUnitCheck env tKernel arrTy.ElemType |> Result.bind (fun () ->
                    // arrTy.ElemType is IRType post-B2; return directly.
                    // Sound only because the check above established that the
                    // kernel preserves the element's unit signature.
                    let resultType = arrTy.ElemType
                    Ok (mkTyped (TExprReduce (tArr, tKernel, tInitOpt)) resultType))))
        | ArrayElem at ->
            // Reached only by a FULL fold (n = rank) that the rank-k nest
            // above declined -- non-dense axes, non-static extents, or a
            // non-scalar element. A partial fold (n < rank) never lands here:
            // it was rewritten into the row-mode apply before this point.
            Error (Other (sprintf "reduce: the full fold (`axes = %d`) over a rank-%d operand needs dense (plain, non-compact) axes with static extents and scalar elements; fold one axis at a time instead (`reduce(A, op)` folds the innermost axis into a rank-%d result)"
                                  at.IndexTypes.Length at.IndexTypes.Length (at.IndexTypes.Length - 1)))
        | _ ->
            Error (Other "reduce() requires an array as first argument"))))))

/// Unit soundness of a fold, checked at the rank-1 reduce site. `reduce`
/// types its result as the array's ELEMENT type, right only if the kernel
/// PRESERVES the element's unit signature: folding n meters through `+`
/// gives meters (holds), through `*` gives meters^n (a grade that varies
/// with the EXTENT, no fixed signature). `+`/`-` are unit-endomorphic and
/// pass; `*`/`/` over a dimensioned element reject. (min/max are the other
/// textbook endomorphic members but exist only as bounded-type keywords,
/// not callable, so no kernel can name them; docs/features/sql.md's T x T
/// -> T fold kernel is exactly this restriction.)
///
/// The kernel's own ARROW can't answer this: sections are typed as strict
/// endomorphisms `(tau,tau)->tau` after the caller unifies params with the
/// element type, so every arrow says "unit-preserving" by construction.
/// Recompute the real signature instead (section: per-op unit table; lambda:
/// kernel-body walk with params already unified). A dimensionless element is
/// unconstrained (the `*`-fold escape hatch); a kernel whose form the walk
/// cannot read (named function, captured value) is left alone.
and reduceKernelUnitCheck (env: TypeEnv) (tKernel: TypedExpr) (elemTy: IRType) : TypeResult<unit> =
    match IR.getUnits (env.Subst.Resolve elemTy) with
    | Some eu when not (Map.isEmpty (unitNormalize eu).Dims) ->
        let kernelUnits =
            match (resolveTypedExpr env tKernel).Kind with
            | TExprSection op -> Some (unitRulesForOp op (Some eu) (Some eu))
            | TExprLambda info -> Some (kernelBodyUnits env Map.empty info.Body)
            | _ -> None
        match kernelUnits with
        | None -> Ok ()
        | Some computed ->
            computed |> Result.bind (fun ku ->
                let preserves =
                    match ku with
                    // Magnitude counts as part of "preserves": a fold is
                    // typed at its element's signature, so a kernel that
                    // returned a different magnitude would mislabel the
                    // accumulator with no factor anywhere to fix it.
                    | Some u -> unitCompatible u eu && unitSameScale u eu
                    | None -> false
                if preserves then Ok ()
                else
                    Error (UnitMismatch (
                                "reduce kernel (a fold is typed at its element's unit, so the kernel must preserve that unit -- `+` and `-` do; `*` and `/` do not, since folding n of them yields a grade that depends on the extent)",
                                ppUnitSig eu,
                                (match ku with Some u -> ppUnitSig u | None -> "dimensionless"))))
    | _ -> Ok ()

/// Reorder licence for a fold kernel that carries `where ... omp`.
///
/// `omp` on the SECOND argument of `reduce` is the opt-in for a parallel fold
/// (docs/plan-cpp-perf-exploitation.md): codegen chunks the reduced axis,
/// folds each chunk serially, and combines the partials in a fixed order.
/// That is sound only if the kernel is commutative and associative, so an
/// unlicensed `omp` is REFUSED here rather than silently emitted serial --
/// same reasoning as the BL9001 dropped-clause guard: "asked and got serial"
/// and "never asked" produce byte-identical C++, so the user could not tell.
/// Ok () for every kernel that did not ask (the overwhelming majority: an
/// operator section cannot carry a where-clause at all).
and checkFoldOmpLicense (env: TypeEnv) (tKernel: TypedExpr) : TypeResult<unit> =
    let asksOmp (strategies: ParallelStrategy list) =
        strategies |> List.exists (function Omp _ -> true | _ -> false)
    match (resolveTypedExpr env tKernel).Kind with
    // Inline / let-bound lambda: the clause and the body are both right here.
    | TExprLambda info when asksOmp info.Parallel ->
        if info.IsCommutative
           || not (List.isEmpty info.CommGroups)
           || isBuiltinFoldBodyTyped info.Params info.Body then Ok ()
        else Error (FoldOmpNeedsLicense "this fold kernel")
    // Named function: the clause and the comm groups live in the side channels
    // checkFunctionDecl fills (a named function's body never reaches here).
    | TExprVar (fname, _, _) ->
        let asks =
            match env.FuncParallel.TryGetValue fname with
            | true, (_, strategies) -> asksOmp strategies
            | _ -> false
        if not asks then Ok ()
        else
            let licensed =
                (match env.FuncCommGroups.TryGetValue fname with
                 | true, cg -> not (List.isEmpty cg)
                 | _ -> false)
                || (match env.FuncFoldBuiltin.TryGetValue fname with
                    | true, b -> b
                    | _ -> false)
            if licensed then Ok ()
            else Error (FoldOmpNeedsLicense (sprintf "fold kernel '%s'" fname))
    | _ -> Ok ()

and inferProdSum (env: TypeEnv) (args: Expr list) : TypeResult<TypedExpr> =
    args |> List.map (inferExpr env) |> sequenceResults |> Result.bind (fun tArgs ->
        // Every operand must be a rank-1 array over free (SymNone) storage;
        // element types unify across operands (the product's type).
        let rec go (elemTy: IRType option) (staticN: int64 option) (rest: TypedExpr list) : Result<IRType, TypeError> =
            match rest with
            | [] ->
                (match elemTy with
                 | Some e -> Ok e
                 | None -> Error (Other "prodsum() requires at least one array argument"))
            | t :: more ->
                // A `T^k` parameter is an arity-k inference VAR, not an
                // IRTArray (Subst.LookupOrCreateTypeVar): the caret pins the
                // RANK, and the array shape is only materialized when some
                // demand supplies it. `requireArrayArg` is that demand for
                // every other array intrinsic, and prodsum used to skip it and
                // read the raw resolution instead -- so `prodsum(a, b)` over
                // `a: T^1, b: T^1` answered "requires array arguments" unless
                // something ELSE in the body (an `extents(a)`, a call whose
                // callee's parameter is concrete) happened to pin the var
                // first. That is why `covariance` above typechecks and the
                // same call one line up does not.
                //
                // Only an UNBOUND operand is synthesized, and only where the
                // rank it was pinned to is the rank prodsum folds. A concrete
                // non-array, and a `T^k` with k <> 1, both fall through to the
                // honest refusals below -- synthesizing a rank-1 shape against
                // an arity-2 var would refuse in `unify` instead, which reports
                // the raw IRTArrow rather than prodsum's own rank guidance.
                let materialize =
                    match env.Subst.Resolve t.Type with
                    | IRTInfer vid when env.Subst.GetArityConstraint vid = Some 1
                                        || (env.Subst.GetArityConstraint vid).IsNone ->
                        requireArrayArg env t "prodsum" |> Result.map ignore
                    | _ -> Ok ()
                materialize |> Result.bind (fun () ->
                match env.Subst.Resolve t.Type with
                // Fold refusal first, for reduce()'s reason (see there).
                | ArrayElem arrTy when arrTy.IndexTypes |> List.exists (fun ix -> ix.Symmetry = SymWreath) ->
                    let ix = arrTy.IndexTypes |> List.find (fun ix -> ix.Symmetry = SymWreath)
                    Error (OrbitFoldUnsupported (ppOrbitLevels (orbitLevelsOf ix), "prodsum"))
                | ArrayElem arrTy when arrTy.IndexTypes.Length = 1
                                       && (match arrTy.IndexTypes.[0].Symmetry with
                                           | SymSymmetric | SymAntisymmetric | SymHermitian -> true
                                           | SymWreath -> true   // unreachable: taken above
                                           | SymNone -> false) ->
                    // Same ambiguity as reduce over compact storage: canonical
                    // vs logical cells differ. Mirror its guidance.
                    Error (Other "prodsum() over compact symmetric/antisymmetric/Hermitian storage is not supported: folding the canonical cells and folding the logical (mirrored) cells differ. decompact(A, d) first for the logical fold.")
                | ArrayElem arrTy when arrTy.IndexTypes.Length = 1 ->
                    // Provably mismatched static extents are an error now;
                    // unknown extents are trusted (codegen loops over the
                    // first operand's extent).
                    let thisN = tryEvalIntIR arrTy.IndexTypes.[0].Extent
                    (match staticN, thisN with
                     | Some a, Some b when a <> b ->
                         Error (ProdsumExtentMismatch (a, b))
                     | _ ->
                        // ELEMENT TYPES PROMOTE, THEY DO NOT UNIFY (issue #18,
                        // docs/plan-array-expression-fixes.md row 18).
                        //
                        // `prodsum` is sum_i a_i * b_i, and `*` between a real
                        // and a complex operand promotes -- the operands keep
                        // their own element types and only the RESULT widens.
                        // Unifying them instead made the two operands' element
                        // types the same type, which is a claim prodsum never
                        // makes. In `lsdft`, `prodsum(s, e)` with `s : U^1` and
                        // `e` complex bound `U := Complex128` from inside the
                        // function's own body: `blade check` still said OK for a
                        // real-valued caller, but the emitted signature read
                        // `lsdft(tuple<Array<complex<double>>, ...>)` while the
                        // call site passed `Array<double>` -- a check-time
                        // soundness gap that only g++ caught.
                        //
                        // So: two concrete element types JOIN through the same
                        // `promoteElemType` the scalar and broadcast seams use
                        // (Float64 |_| Complex128 = Complex128), and a still-
                        // generic operand element contributes nothing and is
                        // LEFT FREE for the call site to fix. Anything
                        // promotion cannot join (Float64 with String) falls
                        // through to `unify`, which reports it exactly as
                        // before -- the refusals are unchanged, only the
                        // accepted mixed-numeric case moves.
                        let joinElem (acc: IRType) (next: IRType) : Result<IRType, TypeError> =
                            match IR.stripUnits (env.Subst.Resolve acc),
                                  IR.stripUnits (env.Subst.Resolve next) with
                            | IRTScalar a, IRTScalar b when a = b -> Ok acc
                            | IRTScalar a, IRTScalar b ->
                                (match IR.promoteElemType a b with
                                 | Some p -> Ok (IRTScalar p)
                                 | None -> unify env.Subst acc next |> Result.map (fun () -> acc))
                            // One side still generic: the concrete side is the
                            // best answer available, and binding the var to it
                            // is the #18 miscompile.
                            | IRTScalar _, IRTInfer _ -> Ok acc
                            | IRTInfer _, IRTScalar _ -> Ok next
                            | _ -> unify env.Subst acc next |> Result.map (fun () -> acc)
                        (match elemTy with
                         | Some e -> joinElem e arrTy.ElemType
                         | None -> Ok arrTy.ElemType)
                        |> Result.bind (fun joined ->
                            go (Some joined)
                               (match staticN with Some _ -> staticN | None -> thisN)
                               more))
                | ArrayElem _ ->
                    Error (Other "prodsum() supports only rank-1 arrays (fibers); pass each operand's innermost slice")
                // A `T^k` operand with k <> 1: the caret already declared the
                // rank, so this is the same refusal as a concrete rank-k array
                // and reads better than "requires array arguments" would.
                | IRTInfer vid when (env.Subst.GetArityConstraint vid |> Option.exists (fun k -> k <> 1)) ->
                    Error (Other "prodsum() supports only rank-1 arrays (fibers); pass each operand's innermost slice")
                | _ ->
                    Error (Other "prodsum() requires array arguments"))
        go None None tArgs |> Result.map (fun elemTy ->
            // WIDEST OPERAND LEADS. `IRProdSum` carries no result type of its
            // own: codegen sizes the accumulator from the FIRST operand
            // (`inferExprType (List.head args)`, CodeGen.fs's IRProdSum arm),
            // which was exactly right while every operand shared one element
            // type and is wrong the moment they promote -- `prodsum(s, e)` with
            // real `s` and complex `e` accumulated into a `double` and g++
            // refused `__ps += s[t] * e[t]`.
            //
            // Rotating the operand that already carries the joined type to the
            // front costs nothing and asserts nothing new: prodsum is
            // sum_t prod_L a_L[t], the product is commutative, and the
            // summation order over t -- the only order floating-point
            // accumulation is sensitive to -- is untouched. It fires ONLY when
            // the operands' element types actually differ, so no program that
            // compiles today changes shape or rounding.
            //
            // Ragged/grouped operands opt out: codegen also reads the loop
            // BOUND off the head (`.len` for a peeled row, `.extents[0]`
            // otherwise), and swapping a peeled row out of that position would
            // change which rule applies.
            let bareElemOf (t: TypedExpr) =
                match env.Subst.Resolve t.Type with
                | ArrayElem a -> Some (IR.stripUnits (env.Subst.Resolve a.ElemType))
                | _ -> None
            let anyRagged =
                tArgs |> List.exists (fun t ->
                    match env.Subst.Resolve t.Type with
                    | ArrayElem a ->
                        a.IndexTypes |> List.exists (fun ix ->
                            isRaggedRowKind ix.IxKind || isRaggedFamilyKind ix.IxKind)
                    | _ -> false)
            let joined = IR.stripUnits (env.Subst.Resolve elemTy)
            let ordered =
                if anyRagged || tArgs.Length < 2 || bareElemOf (List.head tArgs) = Some joined then tArgs
                else
                    match tArgs |> List.tryFindIndex (fun a -> bareElemOf a = Some joined) with
                    | Some idx when idx > 0 ->
                        tArgs.[idx] :: (tArgs |> List.indexed
                                              |> List.filter (fun (j, _) -> j <> idx)
                                              |> List.map snd)
                    | _ -> tArgs
            mkTyped (TExprProdSum ordered) elemTy))

/// __dist_pack(kappa1, ..., kappar): construct a Dist<r, tau like axes> value from
/// its cumulant component arrays. Compiler-internal -- the PPL elaboration
/// stage emits it after building the fused tower. kappa_1 (the mean tensor over
/// the variable axes as-declared) fixes tau and the axes; the component count
/// fixes r. The typed node is a plain TExprTuple -- the exact representation
/// the type erases to at zonk -- so no new lowering or codegen path exists;
/// only the TYPE is nominal. Unification stays strict (Unify has no
/// tuple<->Dist coercion), so this intrinsic and Dist-typed operators are the
/// only producers of Dist values.
and inferDistPack (env: TypeEnv) (args: Expr list) : TypeResult<TypedExpr> =
    args |> List.map (inferExpr env) |> sequenceResults |> Result.bind (fun tArgs ->
        match env.Subst.Resolve tArgs.Head.Type with
        | ArrayElem a1 ->
            let order = tArgs.Length
            Ok (mkTyped (TExprTuple tArgs) (IRTDist (order, a1.ElemType, a1.IndexTypes)))
        | t ->
            Error (Other (sprintf "__dist_pack: components must be arrays (kappa_1 lowered to %s) -- this intrinsic is emitted by the PPL elaboration stage, not written by hand" (ppIRType t))))

/// cumulant(d, k): the order-k component of a Dist value, as an ordinary
/// array. THE ORDER GUARD AS A TYPE ERROR: k must be a static int (the
/// replicate-count contract) in 1..r. Result type comes from
/// distComponentType, so the projection is fully typed at any expression
/// position -- including on Dist-typed function parameters, where the old
/// elaboration-level registry could never reach.
and inferCumulantProj (env: TypeEnv) (dExpr: Expr) (kExpr: Expr) : TypeResult<TypedExpr> =
    inferExpr env dExpr |> Result.bind (fun tD ->
        match env.Subst.Resolve tD.Type with
        | IRTDist (order, elem, axes) ->
            (match evalStaticIntExpr env kExpr with
             | None ->
                 Error (Other "cumulant: the order must be a compile-time integer (a literal, `let static`, or static-function call)")
             | Some k when k < 1 ->
                 Error (CumulantOrderPositive k)
             | Some k when k > order ->
                 Error (CumulantOrderExceeds (k, order))
             | Some k ->
                 let compTy = distComponentType k elem axes
                 let idxLit = mkTyped (TExprLit (LitInt (int64 (k - 1)))) (IRTScalar ETInt64)
                 Ok (mkTyped (TExprTupleIndex (tD, idxLit)) compTy))
        | t ->
            Error (CumulantNeedsDist (ppIRType t)))

// extents(array) -- extent(s) along each dimension as Int64. Rank-1 ->
// scalar Int64; rank-N -> tuple (Int64, ..., Int64) of length N, outermost
// first. Codegen prefers a static literal when the extent expression is
// statically evaluable (tryEvalIntIR), matching rank()'s behavior.
//
// A homogeneous List<Int64> would be a more natural return type for the
// multi-rank case than a tuple once List exists.


and inferDecompact (env: TypeEnv) array d : TypeResult<TypedExpr> =
    inferExpr env array |> Result.bind (fun tArr ->
        requireArrayArg env tArr "decompact" |> Result.bind (fun arrTy ->
            // WREATH DECOMPACTION, ahead of the dimension resolution below --
            // `d` does not MEAN a dimension for a wreath class. The generic
            // path splits a compact slot into left-remainder/freed Idx/
            // right-remainder groups of the SAME class, which is ill-defined
            // here: a wreath's fission must say which LEVEL is peeled, and
            // the residual is a JUXTAPOSITION of depth-(d-1) classes, not one
            // group of the same shape (`{ ix with Rank = a }` would produce a
            // Rank that no longer matches the level-list product).
            //
            // Only the ENDPOINT is implemented: FULL decompaction,
            // `decompact(W, 0)`, where the second arg is LEVELS TO KEEP
            // (docs/plan-orbidx-decompaction.md section 4.3) -- the dense
            // rank-prod(ri) tensor, every cell the class's own section 2 read. Any
            // other `d` asks for the section 3 peel lattice and is refused by name.
            match arrTy.IndexTypes with
            | [ ix ] when ix.Symmetry = SymWreath ->
                let levels = ppOrbitLevels (orbitLevelsOf ix)
                if d <> 0 then Error (OrbitDecompactPartial (levels, d))
                else
                    let axes = max 1 ix.Rank
                    let baseExtent = orbitBaseExtent ix
                    // The dense record is built from scratch, NOT `{ ix with
                    // ... }`: a wreath record carries IxKOrbit, the "__orbidx"
                    // sentinel Tag and an IROrbitClass extent marker, and every
                    // one of those would be a lie on a plain axis (the IR
                    // validator enforces the kind/Tag agreement, and
                    // orbitLevelsOf would keep answering with the level list).
                    let denseAxis () =
                        { Id = env.Builder.FreshId(); Rank = 1; Extent = baseExtent
                          Symmetry = SymNone; Tag = None; IxKind = IxKPlain
                          Kind = SDimension; Dependencies = [] }
                    let newIndexTypes = List.init axes (fun _ -> denseAxis ())
                    let resultType = mkArrayArrow newIndexTypes arrTy.ElemType None
                    Ok (mkTyped (TExprDecompact (tArr, d)) resultType)
            | _ when arrTy.IndexTypes |> List.exists (fun ix -> ix.Symmetry = SymWreath) ->
                let ix = arrTy.IndexTypes |> List.find (fun ix -> ix.Symmetry = SymWreath)
                Error (OrbitStorageUnsupported (ppOrbitLevels (orbitLevelsOf ix),
                                                "decompact() of a wreath group combined with other index slots"))
            | _ ->
            // Resolve the logical dimension d to (slotIndex, slotArity,
            // posInSlot). A compact slot of arity r spans r consecutive
            // dimensions; posInSlot in [0, r) says which component within
            // the group d targets -- that position decides peel-first /
            // peel-last / peel-middle.
            let totalDims = arrTy.IndexTypes |> List.sumBy (fun ix -> max 1 ix.Rank)
            let dimToSlot (dd: int) : Result<int * int * int, TypeError> =
                if dd < 0 || dd >= totalDims then
                    Error (DecompactDimRange (dd, totalDims))
                else
                    let rec walk slotIdx acc remaining =
                        match remaining with
                        | [] -> Error (Other (sprintf "decompact: dimension %d out of range (internal)" dd))
                        | (ix: IRIndexType) :: rest ->
                            let ar = max 1 ix.Rank
                            if dd < acc + ar then Ok (slotIdx, ar, dd - acc)
                            else walk (slotIdx + 1) (acc + ar) rest
                    walk 0 0 arrTy.IndexTypes
            dimToSlot d |> Result.bind (fun (slot, r, posInSlot) ->
                let ix = arrTy.IndexTypes.[slot]
                if r < 2 || ix.Symmetry = SymNone then
                    Error (DecompactPlainAxis d)
                // Unreachable: the two wreath arms above (sole slot / combined
                // with others) ran first and between them cover every record
                // list containing a SymWreath. Kept spelled out so the fission
                // code below can go on assuming `{ ix with Rank = a }` builds a
                // consistent record -- for a wreath it would not (the Rank
                // would stop matching the product of its level list).
                elif ix.Symmetry = SymWreath then
                    Error (OrbitStorageUnsupported (ppOrbitLevels (orbitLevelsOf ix), "decompact()"))
                else
                // Codegen scope: the group being decompacted must be the LAST
                // index slot, preceding slots plain free Idx singletons --
                // the shape a chained "to-the-right-only" peel produces
                // (accumulating free dims on the left while the residual
                // group stays last), enabling composed full densification.
                // Free dims become an outer loop product wrapping the
                // last-slot fission scatter. Other arrangements deferred.
                let leadingSlots = arrTy.IndexTypes |> List.take slot
                let groupIsLast = (slot = arrTy.IndexTypes.Length - 1)
                let leadingAllFreeSingletons =
                    leadingSlots |> List.forall (fun s -> (max 1 s.Rank) = 1 && s.Symmetry = SymNone)
                if not (groupIsLast && leadingAllFreeSingletons) then
                    Error (DecompactLastSlotOnly (arrTy.IndexTypes.Length, slot))
                elif leadingSlots.Length > 0 && ix.Symmetry <> SymSymmetric then
                    // Surrounding-dim wrapping is currently wired only for the
                    // symmetric fission scatter (the gather form). Antisym /
                    // Hermitian fission with leading free dims is not yet
                    // emitted, so reject rather than miscompile.
                    Error (Other "decompact: surrounding free dimensions are currently supported only for symmetric groups; antisymmetric/Hermitian groups with preceding free dimensions are not yet wired.")
                elif ix.Symmetry = SymHermitian && r >= 3 then
                    // Rank-2 Hermitian (the only Hermitian arrays a producer
                    // makes today, via gram) dissolves to a dense nxn with the
                    // lower triangle conjugated -- handled below. Rank >= 3
                    // Hermitian has no producer yet and its compact-residual
                    // conjugate semantics aren't worked out, so reject.
                    Error (Other "decompact: rank >= 3 SymHermitian is not yet supported (no producer exists for rank >= 3 Hermitian arrays; gram produces rank-2 Hermitian, which decompacts to a dense conjugate-mirrored matrix).")
                else
                    // SYMMETRIC: general fission, any rank/cut, via the
                    // gather materializer. ANTISYMMETRIC: rank 2 dissolves to
                    // dense nxn; rank >= 3 boundary cuts leave one residual
                    // antisym group, allocatable via allocate_strict with the
                    // sign applied lazily on read (canon_* transform).
                    // Replacement slots: left remainder (arity posInSlot) +
                    // extracted Idx + right remainder (arity r-1-posInSlot);
                    // each remainder of arity a>=2 becomes a fresh SymIdx<a>
                    // of the SAME class (fresh Id: the two halves are now
                    // independent relations); a=1 -> plain Idx, a=0 -> omitted.
                    let mkRemainder (a: int) : IRIndexType list =
                        if a <= 0 then []
                        elif a = 1 then
                            [ { ix with Id = env.Builder.FreshId(); Rank = 1; Symmetry = SymNone } ]
                        else
                            [ { ix with Id = env.Builder.FreshId(); Rank = a } ]
                    let extracted =
                        { ix with Id = env.Builder.FreshId(); Rank = 1; Symmetry = SymNone }
                    let leftRem = mkRemainder posInSlot
                    let rightRem = mkRemainder (r - 1 - posInSlot)
                    let replacement = leftRem @ [extracted] @ rightRem
                    let newIndexTypes =
                        arrTy.IndexTypes
                        |> List.mapi (fun i s -> (i, s))
                        |> List.collect (fun (i, s) -> if i = slot then replacement else [s])
                    let resultType = mkArrayArrow newIndexTypes arrTy.ElemType None
                    Ok (mkTyped (TExprDecompact (tArr, d)) resultType))))

// reduce(array, kernel) -- T/S reduction primitive. Consumes the innermost
// dimension via a binary kernel; rank-1 input produces a scalar (multi-rank
// reduction deferred). Empty-array policy: statically-0 extent is a
// typecheck error (caller bug); statically->0 emits a standard loop;
// dynamic extent adds a runtime guard that aborts cleanly on empty.


and inferGram (env: TypeEnv) leftE rightE : TypeResult<TypedExpr> =
    // gram(A, B) = A * B^H: result[i][j] = sum_k A[i][k] * conj(B[j][k]).
    // A: m x n, B: p x n (shared contracted dim n) -> result: m x p. Element
    // type complex iff EITHER operand is (conj is identity on reals). When A
    // and B are the SAME array (syntactically -- conservative, never claims
    // false symmetry), result is square and SymHermitian (complex) /
    // SymSymmetric (real) via the triangular upper-half scatter; otherwise a
    // general dense m x p array (SymNone).
    inferExpr env leftE |> Result.bind (fun tL ->
    inferExpr env rightE |> Result.bind (fun tR ->
        // minRank 2: gram's contract IS rank-2-on-both-sides (the check just
        // below), so an unannotated operand should deduce rank 2 rather than
        // be pinned to rank 1 and then rejected by that check.
        requireArrayArgMinRank env tL "gram" 2 |> Result.bind (fun lTy ->
        requireArrayArgMinRank env tR "gram" 2 |> Result.bind (fun rTy ->
            let lDims = lTy.IndexTypes |> List.sumBy (fun ix -> max 1 ix.Rank)
            let rDims = rTy.IndexTypes |> List.sumBy (fun ix -> max 1 ix.Rank)
            if lDims <> 2 || rDims <> 2 then
                Error (GramNeedsRank2 (lDims, rDims))
            else
                // Extents: outer (m / p) and inner contracted (n) per operand.
                let lOuter = lTy.IndexTypes.[0].Extent
                let lInner = lTy.IndexTypes.[1].Extent
                let rOuter = rTy.IndexTypes.[0].Extent
                let rInner = rTy.IndexTypes.[1].Extent
                // Static contracted-dim mismatch is a hard error; otherwise trust.
                let innerMismatch =
                    match tryEvalIntIR lInner, tryEvalIntIR rInner with
                    | Some a, Some b -> a <> b
                    | _ -> false
                if innerMismatch then
                    Error (Other "gram(A, B): the contracted (trailing) dimensions of A and B must match.")
                else
                    // Element type join: complex if either operand is complex.
                    let isComplexElem (t: IRType) =
                        match t with
                        | IRTScalar (ETComplex64 | ETComplex128) -> true
                        | _ -> false
                    let outElem =
                        if isComplexElem lTy.ElemType then lTy.ElemType
                        elif isComplexElem rTy.ElemType then rTy.ElemType
                        else lTy.ElemType
                    let isComplex = isComplexElem outElem
                    // Conservative same-array test: both bare vars, same name.
                    let sameArray =
                        match tL.Kind, tR.Kind with
                        | TExprVar (n1, _, _), TExprVar (n2, _, _) -> n1 = n2
                        | _ -> false
                    let freshSlot (ext: IRExpr) (sym: SymmetryClass) (rank: int) =
                        { Id = env.Builder.FreshId(); Rank = rank; Extent = ext
                          Symmetry = sym; Tag = None; IxKind = IxKPlain; Kind = SDimension; Dependencies = [] }
                    let resultType =
                        if sameArray then
                            // Square m x m, compact group of arity 2 carrying the
                            // (anti-)symmetry: Hermitian (complex) or symmetric.
                            let sym = if isComplex then SymHermitian else SymSymmetric
                            let grp = { (freshSlot lOuter sym 2) with Extent = lOuter }
                            mkArrayArrow [grp] outElem None
                        else
                            // General dense m x p (two independent plain axes).
                            let s0 = freshSlot lOuter SymNone 1
                            let s1 = freshSlot rOuter SymNone 1
                            mkArrayArrow [s0; s1] outElem None
                    Ok (mkTyped (TExprGram (tL, tR, sameArray)) resultType)))))


and inferMatmul (env: TypeEnv) leftE rightE : TypeResult<TypedExpr> =
    // matmul(A, B): A is m x k, B is k x n -> DENSE m x n.
    //
    // The checks below fix the intrinsic's domain:
    //   * two operands, each rank-2 with two PLAIN axes (a compact/symmetric
    //     rank-2 operand cannot unify with two separate Idx slots);
    //   * both element types Float64 -- blade_gemm is dgemm/native-double only;
    //   * the contracted extents agree, when both are statically known.
    // The math elaborator already rejects a shape mismatch with its own
    // message before inference runs; these are the backstop for a marker
    // that reaches the checker some other way.
    inferExpr env leftE |> Result.bind (fun tL ->
    inferExpr env rightE |> Result.bind (fun tR ->
        requireArrayArgMinRank env tL "matmul" 2 |> Result.bind (fun lTy ->
        requireArrayArgMinRank env tR "matmul" 2 |> Result.bind (fun rTy ->
            let plainRank2 (a: IRArrayType) =
                a.IndexTypes.Length = 2 && a.IndexTypes |> List.forall (fun ix -> ix.Rank <= 1)
            if not (plainRank2 lTy) || not (plainRank2 rTy) then
                Error (Other "matmul: both arguments must be rank-2 dense matrices (m x k and k x n, two plain index axes each).")
            else
            let isFloat64 (t: IRType) = match t with IRTScalar ETFloat64 -> true | _ -> false
            if not (isFloat64 lTy.ElemType) || not (isFloat64 rTy.ElemType) then
                Error (Other "matmul: both arguments must have Float64 elements (Array<Float64 like Idx<m>, Idx<k>>).")
            else
                let lInner = lTy.IndexTypes.[1].Extent
                let rOuter = rTy.IndexTypes.[0].Extent
                let innerMismatch =
                    match tryEvalIntIR lInner, tryEvalIntIR rOuter with
                    | Some a, Some b -> a <> b
                    | _ -> false
                if innerMismatch then
                    Error (Other "matmul(A, B): A's trailing dimension and B's leading dimension must match.")
                else
                    // Dense m x n: two independent plain axes, exactly the
                    // shape the synthesized routine's return type declared.
                    let freshSlot (ext: IRExpr) =
                        { Id = env.Builder.FreshId(); Rank = 1; Extent = ext
                          Symmetry = SymNone; Tag = None; IxKind = IxKPlain
                          Kind = SDimension; Dependencies = [] }
                    let resultType =
                        mkArrayArrow [ freshSlot lTy.IndexTypes.[0].Extent
                                       freshSlot rTy.IndexTypes.[1].Extent ] lTy.ElemType None
                    Ok (mkTyped (TExprMatmul (tL, tR)) resultType)))))


and inferEigh (env: TypeEnv) (operandE: Expr) : TypeResult<TypedExpr> =
    // eigh(S) -> (Q, LAM): the symmetric / Hermitian eigendecomposition, as a
    // first-class intrinsic (docs/plan-cpp-perf-exploitation.md).
    //
    // THE ADMISSIBILITY RULE IS `LinAlgPatterns.classifyEigh`, NOT A
    // RESTATEMENT: eigh's domain is the (precision x SYMMETRY) matrix, where
    // symmetry selects the routine FAMILY and one row (complex +
    // SymSymmetric) has no routine at all -- a restatement would be a
    // second place for that matrix to be wrong. Checks below establish
    // SHAPE (rank-2, square); the classifier decides the family, and a
    // `None` is a type error (this node exists only when a route does).
    // The classifier is CONSULTED, never explained: diagnostic text is
    // derived from the operand's own record so each decline names its
    // actual reason instead of one generic "not supported".
    //
    // Since direct application does not unify param types with argument
    // types, an f32/complex/int operand here would typecheck and then die
    // in g++ without this rule -- it accepts strictly more programs, and
    // rejects, with a named reason, exactly the ones that would otherwise
    // hit a C++ template error.
    inferExpr env operandE |> Result.bind (fun tA ->
        requireArrayArgMinRank env tA "eigh" 2 |> Result.bind (fun aTy ->
            // Rank-2 in either admissible spelling: ONE compact slot of arity 2
            // (SymIdx / Hermitian storage -- the zero-conversion packed route),
            // or TWO plain dense axes. Anything else is not a matrix.
            let shapeResult =
                match aTy.IndexTypes with
                | [ ix ] when ix.Rank = 2 ->
                    // A rank-2 compact group is square BY CONSTRUCTION: one
                    // extent covers both dimensions.
                    Ok ix.Extent
                | [ i0; i1 ] when i0.Rank <= 1 && i1.Rank <= 1 ->
                    let squareMismatch =
                        match tryEvalIntIR i0.Extent, tryEvalIntIR i1.Extent with
                        | Some a, Some b -> a <> b
                        | _ -> false
                    if squareMismatch then
                        Error (Other "eigh: the argument must be SQUARE (n x n); symmetry is assumed, not checked.")
                    else Ok i0.Extent
                | _ ->
                    Error (Other "eigh: the argument must be rank-2 square -- either two plain axes (Array<Float64 like Idx<n>, Idx<n>>) or one compact arity-2 group (Array<Float64 like SymIdx<2, n>>).")
            shapeResult |> Result.bind (fun nExtent ->
                match Blade.LinAlgPatterns.classifyEigh aTy with
                | None ->
                    // Explain the specific decline. Order matters: the element
                    // type is checked first because it is the coarsest gate,
                    // then the two symmetry rows that have no routine.
                    let isComplexElem =
                        match aTy.ElemType with
                        | IRTScalar (ETComplex64 | ETComplex128) -> true
                        | _ -> false
                    if aTy.IsVirtual then
                        Error (Other "eigh: the argument must be a materialized array -- a virtual (range / reverse) view has no pool for the eigensolver to read; bind it with |> compute first.")
                    elif (Blade.LinAlgPatterns.precisionOf aTy.ElemType).IsNone then
                        Error (Other "eigh: the element type has no eigensolver -- expected Float32, Float64, Complex64 or Complex128.")
                    else
                        match aTy.IndexTypes with
                        | [ ix ] when ix.Rank = 2 && ix.Symmetry = SymSymmetric && isComplexElem ->
                            // THE COMPLEX-SYMMETRIC TRAP, refused by name. A
                            // complex array carrying SymSymmetric is
                            // complex-SYMMETRIC (A = A^T, no conjugation): not
                            // Hermitian, not normal, complex spectrum,
                            // non-orthogonal eigenvectors. There is no `zsyev`
                            // and no `zspev`; the right routine is the general
                            // `zgeev`, which is a different operation with a
                            // different result TYPE.
                            Error (Other "eigh: a COMPLEX SYMMETRIC matrix (A = A^T, without conjugation) is not Hermitian and has no symmetric eigensolver -- its spectrum is complex and its eigenvectors are not orthogonal. Use eig for the general decomposition, or declare the operand Hermitian storage.")
                        | [ ix ] when ix.Rank = 2 && ix.Symmetry = SymAntisymmetric ->
                            Error (Other "eigh: an ANTISYMMETRIC (skew) operand has a purely imaginary spectrum and no symmetric eigensolver. Use eig on its decompacted form.")
                        | _ ->
                            Error (Other "eigh: the argument's index structure has no eigensolver route -- expected a plain dense n x n matrix or a rank-2 symmetric/Hermitian compact group.")
                | Some _ ->
                    // THE MIXED-ELEMENT RESULT. Q inherits the operand's element
                    // type; LAM does NOT -- a symmetric/Hermitian matrix has REAL
                    // eigenvalues, so a Complex128 operand yields
                    // `(Array<Complex128 ...>, Array<Float64 ...>)`. That is the
                    // first tuple the surface produces whose elements differ in
                    // element type, and it is exactly what `blade_lapack`'s
                    // signatures say (`std::complex<double>** V` beside
                    // `double* lam`). Typing LAM complex would be a silent
                    // storage-width error at the shim boundary.
                    let lamElem =
                        match aTy.ElemType with
                        | IRTScalar ETComplex128 -> IRTScalar ETFloat64
                        | IRTScalar ETComplex64 -> IRTScalar ETFloat32
                        | t -> t
                    let freshSlot () =
                        { Id = env.Builder.FreshId(); Rank = 1; Extent = nExtent
                          Symmetry = SymNone; Tag = None; IxKind = IxKPlain
                          Kind = SDimension; Dependencies = [] }
                    // Q is DENSE n x n whatever the operand's storage was: the
                    // eigenvectors of a symmetric matrix carry no symmetry of
                    // their own (Q is orthogonal, not symmetric), so claiming a
                    // compact class for the packed route's output would be
                    // false. The shim writes through `V[i][k]`, a dense row
                    // skeleton, which is the same statement one level down.
                    let qTy = mkArrayArrow [ freshSlot (); freshSlot () ] aTy.ElemType None
                    let lamTy = mkArrayArrow [ freshSlot () ] lamElem None
                    Ok (mkTyped (TExprEigh tA) (IRTTuple [qTy; lamTy])))))


and inferSolve (env: TypeEnv) (matrixE: Expr) (rhsE: Expr) : TypeResult<TypedExpr> =
    // solve(A, b) -> x with A.x = b: the general dense linear solve by
    // partial-pivoted LU. A is rank-2 square n x n, b is rank-1 of extent n,
    // and x comes back rank-1 of extent n.
    //
    // The domain is deliberately NARROWER than eigh's, and the narrowness is
    // the point: eigh's admissibility is a (precision x SYMMETRY) matrix
    // because symmetry picks the routine FAMILY, so it delegates to
    // `classifyEigh`. LU has no symmetry axis -- `dgesv` is the routine for
    // ANY square matrix -- so the only questions are shape and element type,
    // and both are settled here. `LinAlgPatterns.classifySolve` still answers
    // the ROUTE (and thus the precision letter), but it cannot decline an
    // operand this function accepted.
    //
    // Float64 ONLY, matching `inferMatmul`'s surface rule rather than eigh's
    // four precisions. Widening is a real option (dgesv has s/c/z siblings)
    // but it is a LANGUAGE decision about what the emitted native arm must
    // then also cover byte-identically, so it is not taken silently here.
    //
    // As with matmul, the math elaborator already rejected a shape mismatch
    // with its own message before inference runs; these are the backstop for a
    // marker that reaches the checker some other way (and the only check a
    // hand-written `__math_solve` ever meets).
    inferExpr env matrixE |> Result.bind (fun tA ->
    inferExpr env rhsE |> Result.bind (fun tB ->
        requireArrayArgMinRank env tA "solve" 2 |> Result.bind (fun aTy ->
        requireArrayArgMinRank env tB "solve" 1 |> Result.bind (fun bTy ->
            // A: two PLAIN axes. A rank-2 compact (symmetric / antisymmetric)
            // group is refused rather than densified: LU overwrites its copy
            // with a factor that is NOT symmetric, so the packed pool would be
            // the wrong shape to read and the right answer would need a dense
            // staging copy this node does not make. `decompact` first.
            let plainRank2 (a: IRArrayType) =
                a.IndexTypes.Length = 2 && a.IndexTypes |> List.forall (fun ix -> ix.Rank <= 1)
            let plainRank1 (a: IRArrayType) =
                a.IndexTypes.Length = 1 && a.IndexTypes.Head.Rank <= 1
            if not (plainRank2 aTy) then
                Error (Other "solve: A must be a rank-2 dense SQUARE matrix (two plain index axes, Array<Float64 like Idx<n>, Idx<n>>).")
            elif not (plainRank1 bTy) then
                Error (Other "solve: b must be a rank-1 dense vector (Array<Float64 like Idx<n>>).")
            elif aTy.IsVirtual || bTy.IsVirtual then
                Error (Other "solve: both arguments must be materialized arrays -- a virtual (range / reverse) view has no pool for the factorization to read; bind it with |> compute first.")
            else
            let isFloat64 (t: IRType) = match t with IRTScalar ETFloat64 -> true | _ -> false
            if not (isFloat64 aTy.ElemType) || not (isFloat64 bTy.ElemType) then
                Error (Other "solve: both arguments must have Float64 elements (Array<Float64 like Idx<n>, Idx<n>> and Array<Float64 like Idx<n>>).")
            else
                // EXTENT AGREEMENT, checked only where BOTH sides are statically
                // known -- the same discipline `inferMatmul`'s contracted-extent
                // check uses, and for the same reason: unify never compares
                // extents, so a static disagreement is the only kind this seam
                // can see. A dynamic mismatch is not silently wrong either; the
                // emitted loops and the shim both take n from A's extents table
                // and read b at those indices.
                let n0 = aTy.IndexTypes.[0].Extent
                let n1 = aTy.IndexTypes.[1].Extent
                let bn = bTy.IndexTypes.Head.Extent
                let disagree (l: IRExpr) (r: IRExpr) =
                    match tryEvalIntIR l, tryEvalIntIR r with
                    | Some a, Some b -> a <> b
                    | _ -> false
                if disagree n0 n1 then
                    Error (Other "solve: A must be SQUARE (n x n); its two extents disagree.")
                elif disagree n0 bn then
                    Error (Other "solve(A, b): b's extent must match A's dimension (A is n x n, b must be length n).")
                else
                    // x is a fresh DENSE rank-1 pool of A's leading extent --
                    // taken from A, not from b, because A's is the extent the
                    // factorization iterates and the shim's `n`.
                    let freshSlot (ext: IRExpr) =
                        { Id = env.Builder.FreshId(); Rank = 1; Extent = ext
                          Symmetry = SymNone; Tag = None; IxKind = IxKPlain
                          Kind = SDimension; Dependencies = [] }
                    let resultType = mkArrayArrow [ freshSlot n0 ] aTy.ElemType None
                    Ok (mkTyped (TExprSolve (tA, tB)) resultType)))))


// stack / join -- the two rank-changing assembly combinators (formalism 2.6)
//
// Both materialize a fresh DENSE rectangular pool by copying their operands
// (the codegen twins are materializeStackForm / materializeJoinForm), so both
// share the same admissibility fence: every operand must be an array whose
// index slots are plain arity-1 SymNone dimensions. A compact / ragged /
// compound slot has no rectangular address space to copy into, and silently
// densifying one would be a storage-class change behind the user's back -- so
// it is rejected with a decompact steer instead.

/// A slot that can take part in a stack/join copy: one dense dimension, no
/// symmetry, no ragged/compound/group kind.
and isDenseStackableSlot (ix: IRIndexType) : bool =
    ix.Rank = 1 && ix.Symmetry = SymNone && ix.IxKind = IxKPlain

/// The statically-known extent of a slot, when there is one. Extents that are
/// runtime expressions answer None and are simply not compared (the emitted
/// C++ reads `.extents[d]` either way; this is a compile-time courtesy check,
/// not a soundness requirement).
and staticExtentOf (e: IRExpr) : int64 option =
    match e with
    | IRLit (IRLitInt n) -> Some n
    | _ -> None

/// Shared operand fence: all arrays, all dense slots, all equal rank. Returns
/// the operands' array types in order. `mkNeedsArrays` builds the op-specific
/// error so stack and join each report in their own words.
and stackJoinOperandTypes
        (env: TypeEnv) (opName: string) (tExprs: TypedExpr list)
        (mkNeedsArrays: int -> string -> TypeError) : TypeResult<IRArrayType list> =
    let rec go i acc (rest: TypedExpr list) =
        match rest with
        | [] -> Ok (List.rev acc)
        | te :: tl ->
            match env.Subst.Resolve te.Type with
            | ArrayElem at ->
                match at.IndexTypes |> List.tryFindIndex (isDenseStackableSlot >> not) with
                | Some bad -> Error (StackJoinCompactSlot (opName, bad))
                | None -> go (i + 1) (at :: acc) tl
            | other -> Error (mkNeedsArrays i (ppIRType other))
    go 1 [] tExprs

and inferStack (env: TypeEnv) (exprs: Expr list) : TypeResult<TypedExpr> =
    exprs |> List.map (inferExpr env) |> sequenceResults |> Result.bind (fun tExprs ->
        if tExprs.IsEmpty then
            Error (Other "stack() needs at least one array: stack(A1, ..., An) adds a fresh leading axis of extent n.")
        else
        stackJoinOperandTypes env "stack" tExprs (fun i g -> StackNeedsArrays (i, g))
        |> Result.bind (fun arrTys ->
            let first = List.head arrTys
            // Every operand must have the SAME shape -- the fresh leading axis
            // selects among them, so a ragged selection has no rank-(r+1) type.
            let shapeCheck =
                arrTys |> List.tail |> List.mapi (fun i at -> (i + 2, at))
                |> List.fold (fun acc (pos, at) ->
                    acc |> Result.bind (fun () ->
                        if at.IndexTypes.Length <> first.IndexTypes.Length then
                            Error (StackShapeMismatch (pos, sprintf "rank %d vs rank %d" at.IndexTypes.Length first.IndexTypes.Length))
                        else
                            let extentClash =
                                List.zip first.IndexTypes at.IndexTypes
                                |> List.tryPick (fun (a, b) ->
                                    match staticExtentOf a.Extent, staticExtentOf b.Extent with
                                    | Some x, Some y when x <> y -> Some (x, y)
                                    | _ -> None)
                            match extentClash with
                            | Some (x, y) -> Error (StackShapeMismatch (pos, sprintf "extent %d vs extent %d" y x))
                            | None ->
                                match unify env.Subst first.ElemType at.ElemType with
                                | Ok () -> Ok ()
                                | Error _ -> Error (StackShapeMismatch (pos, "element types differ")))) (Ok ())
            shapeCheck |> Result.map (fun () ->
                let leadIdx = {
                    Id = env.Builder.FreshId()
                    Rank = 1
                    Extent = IRLit (IRLitInt (int64 tExprs.Length))
                    Symmetry = SymNone
                    Tag = None; IxKind = IxKPlain
                    Kind = SDimension
                    Dependencies = []
                }
                let resultType = mkArrayArrow (leadIdx :: first.IndexTypes) first.ElemType None
                mkTyped (TExprStack tExprs) resultType)))

and inferJoin (env: TypeEnv) (arrays: Expr list) (dim: int) : TypeResult<TypedExpr> =
    arrays |> List.map (inferExpr env) |> sequenceResults |> Result.bind (fun tExprs ->
        stackJoinOperandTypes env "join" tExprs (fun i g -> JoinNeedsArrays (i, g))
        |> Result.bind (fun arrTys ->
            let first = List.head arrTys
            let totalDims = first.IndexTypes.Length
            if dim < 0 || dim >= totalDims then
                Error (JoinDimRange (dim, totalDims))
            else
            // Equal rank + equal extents off the joined axis; the joined axis
            // is the only one allowed to differ, and its extents add.
            let shapeCheck =
                arrTys |> List.tail |> List.mapi (fun i at -> (i + 2, at))
                |> List.fold (fun acc (pos, at) ->
                    acc |> Result.bind (fun () ->
                        if at.IndexTypes.Length <> totalDims then
                            Error (JoinShapeMismatch (pos, sprintf "rank %d vs rank %d" at.IndexTypes.Length totalDims))
                        else
                            let offAxisClash =
                                List.zip first.IndexTypes at.IndexTypes
                                |> List.indexed
                                |> List.tryPick (fun (d, (a, b)) ->
                                    if d = dim then None else
                                    match staticExtentOf a.Extent, staticExtentOf b.Extent with
                                    | Some x, Some y when x <> y -> Some (d, x, y)
                                    | _ -> None)
                            match offAxisClash with
                            | Some (d, x, y) -> Error (JoinShapeMismatch (pos, sprintf "dimension %d has extent %d, not %d" d y x))
                            | None ->
                                match unify env.Subst first.ElemType at.ElemType with
                                | Ok () -> Ok ()
                                | Error _ -> Error (JoinShapeMismatch (pos, "element types differ")))) (Ok ())
            shapeCheck |> Result.map (fun () ->
                // Joined extent: the literal sum when every operand's extent is
                // static (the overwhelmingly common case, and what pins/prints
                // depend on), otherwise a runtime addition chain.
                let dimExtents = arrTys |> List.map (fun at -> at.IndexTypes.[dim].Extent)
                let joinedExtent =
                    let statics = dimExtents |> List.map staticExtentOf
                    if statics |> List.forall Option.isSome then
                        IRLit (IRLitInt (statics |> List.sumBy Option.get))
                    else
                        dimExtents |> List.reduce (fun a b -> IRBinOp (IRElementwise, IRAdd, a, b))
                let joinedIdx =
                    { first.IndexTypes.[dim] with
                        Id = env.Builder.FreshId()
                        Extent = joinedExtent
                        Tag = None }
                let resultIndexTypes =
                    first.IndexTypes |> List.mapi (fun d ix -> if d = dim then joinedIdx else ix)
                let resultType = mkArrayArrow resultIndexTypes first.ElemType None
                mkTyped (TExprJoin (tExprs, dim)) resultType)))

and inferTranspose (env: TypeEnv) array d1 d2 : TypeResult<TypedExpr> =
    inferExpr env array |> Result.bind (fun tArr ->
        // minRank: transpose needs at least 2 dimensions, and specifically
        // enough of them to contain the requested axes -- the axes are already
        // in scope here, so synthesize exactly the rank they need rather than
        // a flat 2. Strictly more permissive than today (an unannotated arg
        // was pinned to rank 1 and then always failed TransposeAxisRange), so
        // it cannot regress a program that compiles.
        requireArrayArgMinRank env tArr "transpose" (max 2 (max d1 d2 + 1)) |> Result.bind (fun arrTy ->
            // Map a logical DIMENSION index to its INDEX-TYPE slot. A slot
            // of arity k occupies k consecutive dimensions; we walk the
            // slot list accumulating arities until the target dimension
            // falls inside a slot. Returns (slotIndex, slotArity, dimWithinSlot).
            // For the first cut every reachable slot is arity-1, so this is
            // identity -- but writing it properly keeps the gate correct in
            // the presence of compound groups elsewhere in the array.
            let totalDims = arrTy.IndexTypes |> List.sumBy (fun ix -> max 1 ix.Rank)
            let dimToSlot (d: int) : Result<int * int * int, TypeError> =
                if d < 0 || d >= totalDims then
                    Error (TransposeAxisRange (d, totalDims))
                else
                    let rec walk slotIdx acc remaining =
                        match remaining with
                        | [] -> Error (Other (sprintf "transpose: axis %d out of range (internal)" d))
                        | (ix: IRIndexType) :: rest ->
                            let ar = max 1 ix.Rank
                            if d < acc + ar then Ok (slotIdx, ar, d - acc)
                            else walk (slotIdx + 1) (acc + ar) rest
                    walk 0 0 arrTy.IndexTypes
            if d1 = d2 then
                Error (TransposeAxesEqual (d1, d2))
            else
                dimToSlot d1 |> Result.bind (fun (slot1, ar1, _) ->
                dimToSlot d2 |> Result.bind (fun (slot2, ar2, _) ->
                    let ix1 = arrTy.IndexTypes.[slot1]
                    let ix2 = arrTy.IndexTypes.[slot2]
                    if slot1 = slot2 then
                        // Both dimensions lie INSIDE one index type -- an intra-
                        // group swap. The index-type class decides the behavior
                        // (storage-preserving): symmetric -> identity, antisym ->
                        // whole-array negation, hermitian -> whole-array
                        // conjugation. No decompaction, no dense blow-up.
                        (match (behaviorOf ix1).TransposeWithin () with
                         | TIdentity ->
                            // A(i,j) = A(j,i): storage unchanged. Erase the
                            // transpose; the result IS the source array.
                            Ok tArr
                         | TNegatedCopy ->
                            Ok (mkTyped (TExprArrayNegate tArr) tArr.Type)
                         | TConjugatedCopy ->
                            Ok (mkTyped (TExprArrayConjugate tArr) tArr.Type)
                         | TDataMove ->
                            // A plain (SymNone) slot of arity >= 2 swapped
                            // within itself: a genuine dimensional swap inside a
                            // rectangular compound. Not yet emitted (the data-
                            // move materializer handles cross-slot rank-1 only).
                            Error (TransposeWithinGroup ar1)
                         | TRequiresDecompaction reason ->
                            Error (Other (sprintf "transpose: %s" reason)))
                    else
                        // Different slots. The structure-preserving case is two
                        // plain (arity-1 SymNone) axes -> physical data move.
                        // Anything else means one axis is bound in a compact
                        // group and the other is outside it: swapping them would
                        // break that group's symmetry. That is a structure-
                        // changing operation requiring explicit decompaction
                        // (decompact then transpose), not a silent transpose.
                        let plain ar (ix: IRIndexType) = ar = 1 && ix.Symmetry = SymNone
                        if plain ar1 ix1 && plain ar2 ix2 then
                            let swapped =
                                arrTy.IndexTypes
                                |> List.mapi (fun i ix ->
                                    if i = slot1 then arrTy.IndexTypes.[slot2]
                                    elif i = slot2 then arrTy.IndexTypes.[slot1]
                                    else ix)
                            let resultType = mkArrayArrow swapped arrTy.ElemType None
                            Ok (mkTyped (TExprTranspose (tArr, d1, d2)) resultType)
                        else
                            let culprit, cd, car, cix =
                                if not (plain ar1 ix1) then "first", d1, ar1, ix1 else "second", d2, ar2, ix2
                            Error (Other (sprintf "transpose: the %s axis (dim %d) is bound in a %A index group (rank %d), and the other axis is outside it. Swapping across a group boundary would decompose the group's symmetry. Decompact the axis first (decompact then transpose)." culprit cd cix.Symmetry car))))))



and inferGroupBy (env: TypeEnv) values grouping : TypeResult<TypedExpr> =
    inferExpr env values |> Result.bind (fun tVals ->
    inferExpr env grouping |> Result.bind (fun tGrouping ->
        requireArrayArg env tVals "group_by" |> Result.bind (fun arrTy ->
            // Extract group structure from GroupKeys type, or fall back for raw key arrays
            let (outerIdx, memberIdx) =
                match env.Subst.Resolve(tGrouping.Type) with
                | IRTGroupKeys (outer, _, _) ->
                    let member_ = {
                        Id = env.Builder.FreshId(); Rank = 1
                        Extent = IRParam ("__groupsz", 0, IRTNat None)
                        Symmetry = SymNone; Tag = Some "__group_member"; IxKind = IxKGroupMember
                        Kind = SDimension; Dependencies = []
                    }
                    ({ outer with Id = env.Builder.FreshId(); Tag = Some "__group_outer"; IxKind = IxKGroupOuter }, member_)
                | _ ->
                    // Fallback: treat second arg as raw key array (backward compat)
                    let outer = {
                        Id = env.Builder.FreshId(); Rank = 1
                        Extent = IRParam ("__ngroups", 0, IRTNat None)
                        Symmetry = SymNone; Tag = Some "__group_outer"; IxKind = IxKGroupOuter
                        Kind = SDimension; Dependencies = []
                    }
                    let member_ = {
                        Id = env.Builder.FreshId(); Rank = 1
                        Extent = IRParam ("__groupsz", 0, IRTNat None)
                        Symmetry = SymNone; Tag = Some "__group_member"; IxKind = IxKGroupMember
                        Kind = SDimension; Dependencies = []
                    }
                    (outer, member_)
            let resultType = mkArrayArrow [outerIdx; memberIdx] arrTy.ElemType None
            Ok (mkTyped (TExprGroupBy (tVals, tGrouping)) resultType))))

// sort(array, key) -- stable sort by ascending key. Eager materialization:
// result is a new physical array with the same element type and extent as
// the input, indexed by a fresh anonymous index (mask-style). The order
// property is NOT tracked in the type system -- a future "key map chain"
// subsystem could make sort lazy (a chain handle recording (key_fn,
// permutation), materialized on first access) to preserve optimization
// headroom for sort-skip, merge-style joins, etc.


and inferGroupKeys (env: TypeEnv) keys : TypeResult<TypedExpr> =
    match keys with
    | [] ->
        Error (Other "group_keys requires at least one key array; got empty argument list")
    | [singleKey] ->
        // Existing single-key path, unchanged.
        inferExpr env singleKey |> Result.bind (fun tKeys ->
            requireArrayArg env tKeys "group_keys" |> Result.bind (fun arrTy ->
                let sourceIdx =
                    if arrTy.IndexTypes.Length > 0 then arrTy.IndexTypes.[0]
                    else {
                        Id = env.Builder.FreshId(); Rank = 1
                        Extent = IRParam ("__src", 0, IRTNat None)
                        Symmetry = SymNone; Tag = None; IxKind = IxKPlain
                        Kind = SDimension; Dependencies = []
                    }
                let namedRef =
                    match arrTy.ElemType with
                    | IRTIdxTagged (_, IRefNamed name) -> Some name
                    | _ -> None
                let (outerIdx, enumValues) =
                    match namedRef with
                    | Some name ->
                        match lookupTypeDef name env with
                        | Some (TDIIndexType (_, idx, _)) ->
                            ({ idx with Id = env.Builder.FreshId(); Tag = Some name; IxKind = ixKindOfTag (Some name) }, None)
                        | Some (TDIEnumIdx (_, idx, values, _)) ->
                            ({ idx with Id = env.Builder.FreshId(); Tag = Some name; IxKind = ixKindOfTag (Some name) }, Some values)
                        | _ ->
                            ({ Id = env.Builder.FreshId(); Rank = 1
                               Extent = IRParam ("__ngroups", 0, IRTNat None)
                               Symmetry = SymNone; Tag = None; IxKind = IxKPlain
                               Kind = SDimension; Dependencies = [] }, None)
                    | None ->
                        ({ Id = env.Builder.FreshId(); Rank = 1
                           Extent = IRParam ("__ngroups", 0, IRTNat None)
                           Symmetry = SymNone; Tag = None; IxKind = IxKPlain
                           Kind = SDimension; Dependencies = [] }, None)
                let gkType = IRTGroupKeys (outerIdx, sourceIdx, enumValues)
                Ok (mkTyped (TExprGroupKeys [tKeys]) gkType)))
    | multipleKeys ->
        // Compound case: infer all keys, verify shared outer index,
        // build a GroupKeys result with dynamic compound outer.
        let inferAll =
            multipleKeys
            |> List.fold (fun accRes k ->
                accRes |> Result.bind (fun acc ->
                    inferExpr env k |> Result.bind (fun tk ->
                        requireArrayArg env tk "group_keys" |> Result.map (fun arrTy ->
                            acc @ [(tk, arrTy)]))))
                (Ok [])
        inferAll |> Result.bind (fun pairs ->
            // Precondition check: all key arrays must be rank-1 and
            // share an outer index. We compare extent expressions
            // structurally -- Blade's typechecker is structural enough
            // that the same Idx<N> annotation produces equal Extent
            // values across multiple bindings.
            let firstSource =
                pairs |> List.head |> snd |> fun ty -> ty.IndexTypes.[0]
            let allShareOuter =
                pairs |> List.forall (fun (_, ty) ->
                    ty.IndexTypes.Length = 1
                    && ty.IndexTypes.[0].Extent = firstSource.Extent)
            if not allShareOuter then
                Error (GroupKeysRank1)
            else
                // Compound outer: dynamic extent, tagged so codegen
                // recognizes "this is compound-dynamic, dispatch via
                // tuple unordered_map". The tag name reserves
                // `__compoundidx_static` for the future mask-derived
                // path (TyCompoundIdx) which is statically evaluable
                // -- that case would have Extent = IRLit (cardinality)
                // and a different codegen story. Component types are
                // not carried in IRTGroupKeys today; codegen recovers
                // them from the IRGroupKeys node's keys list at emit
                // time, and IDE tooltips would do the same.
                let outerIdx = {
                    Id = env.Builder.FreshId(); Rank = 1
                    Extent = IRParam ("__ngroups", 0, IRTNat None)
                    Symmetry = SymNone
                    Tag = Some "__compoundidx_dynamic"; IxKind = IxKCompoundDynamic
                    Kind = SDimension; Dependencies = []
                }
                let gkType = IRTGroupKeys (outerIdx, firstSource, None)
                let tKeys = pairs |> List.map fst
                Ok (mkTyped (TExprGroupKeys tKeys) gkType))

// group_by(values, grouping) -- apply GroupKeys to a values array
// Result: rank-2 array (groups x members), with GroupIdx
// Tags ("__group_outer", "__group_member") signal to codegen to use ragged peel.


and inferReplicate (env: TypeEnv) count body : TypeResult<TypedExpr> =
    inferExpr env count |> Result.bind (fun tC ->
    inferExpr env body |> Result.bind (fun tB ->
        // The count must be compile-time known. Accept a bare literal, or any
        // statically evaluable integer expression (a `let static` value or a
        // static-function call), resolved via the same StaticEval the lowering
        // phase uses. env.StaticValues/StaticFunctions were populated in the
        // checkModule pre-pass.
        let n =
            match tC.Kind with
            | TExprLit (LitInt v) -> Some (int v)
            | _ ->
                let staticEnv : StaticEval.StaticEnv =
                    { Values = env.StaticValues
                      Functions =
                        env.StaticFunctions
                        |> Map.map (fun _ (fd: FunctionDecl) ->
                            { StaticEval.Name = fd.Name
                              StaticEval.Params = fd.Params |> List.map (fun p -> p.Name)
                              StaticEval.Body = fd.Body })
                      CalledFunctions = ref Set.empty
                      ProviderRoots = Map.empty
                      Structs = Map.empty }
                match StaticEval.evalExpr staticEnv StaticEval.maxSteps count with
                | Ok (StaticEval.SVInt v) -> Some (int v)
                | _ -> None
        // Normalize the resolved count to a literal in the typed tree, so the
        // lowering unroll (List.replicate n) sees a concrete factor regardless
        // of how the count was written at the source level.
        let litCount k = mkTyped (TExprLit (LitInt (int64 k))) tC.Type
        match n with
        | None ->
            Error (Other "replicate count must be a compile-time integer (a literal, `let static`, or static-function call)")
        | Some 1 ->
            // replicate(1, c) == c
            Ok tB
        | Some n when n >= 2 ->
            let resolved = env.Subst.Resolve(tB.Type)
            // Create anonymous Idx<N> for the replicate dimension
            let seqIdx = {
                Id = env.Builder.FreshId()
                Rank = 1
                Extent = IRLit (IRLitInt (int64 n))
                Symmetry = SymNone
                Tag = Some "__seq"; IxKind = IxKSeq
                Kind = SDimension
                Dependencies = []
            }
            let resultType =
                match resolved with
                | ArrayElem arrTy ->
                    mkArrayLike { arrTy with IndexTypes = seqIdx :: arrTy.IndexTypes }
                | IRTScalar et ->
                    mkArrayArrow [seqIdx] (IRTScalar et) None
                | _ ->
                    // Same fallback as the sequence case above.
                    mkArrayArrow [seqIdx] (IRTScalar ETFloat64) None
            Ok (mkTyped (TExprReplicate (litCount n, tB)) resultType)
        | _ ->
            Error (Other (sprintf "replicate count must be >= 1, got %A" n))))

// ---- Reynolds ----


and inferTupleIndex (env: TypeEnv) tuple index : TypeResult<TypedExpr> =
    inferExpr env tuple |> Result.bind (fun tT ->
    inferExpr env index |> Result.bind (fun tI ->
        match env.Subst.Resolve(tT.Type) with
        | ArrayElem arrTy ->
            // One bracket = one index dimension. Mirrors ExprApp's
            // tArgs.Length <= arrTy.IndexTypes.Length check.
            let identity = match tT.Kind with TExprVar (_, _, id) -> id | _ -> None
            // Tag check on the single index (same rule as ExprApp).
            let tagMismatch =
                match arrTy.IndexTypes with
                | [] -> None
                | idxType :: _ ->
                    match idxType.Tag with
                    | Some tagName when not (tagName.StartsWith("__")) ->
                        match env.Subst.Resolve tI.Type with
                        | IRTIdxTagged (_, IRefNamed argName) when argName = tagName -> None
                        | IRTIdxTagged (_, IRefNamed argName) ->
                            Some (IndexTagMismatchNamed (tagName, argName))
                        | IRTIdxTagged (_, IRefAnon _) ->
                            Some (IndexTagMismatchAnon tagName)
                        // Wildcard-typed index: warn, don't error -- kept in
                        // step with checkArrayIndexTags above.
                        | IRTIdxTagged (_, IRefAny)
                        | IRTScalar (ETInt32 | ETInt64) ->
                            // BL4003, same as checkArrayIndexTags' twin --
                            // including its synthesized-buffer suppression, so
                            // the one-bracket spelling cannot drift from the
                            // call spelling on a desugarer's own scratch array.
                            if not (isSynthesizedBuffer tT) then
                                emitWarning env "BL4003" tI.Span (sprintf
                                    "Array indexed with untagged integer where slot expects tag '%s'. Consider an explicit cast like `(expr : %s)` or iterate via `range<%s>` to flow the tag automatically."
                                    tagName tagName tagName)
                            None
                        | _ -> None
                    | _ -> None
            match tagMismatch with
            | Some err -> Error err
            | None ->
                if 1 = arrTy.IndexTypes.Length then
                    Ok (mkTyped (TExprIndex (tT, [tI], identity)) arrTy.ElemType)
                elif 1 < arrTy.IndexTypes.Length then
                    let remaining = arrTy.IndexTypes |> List.skip 1
                    Ok (mkTyped (TExprIndex (tT, [tI], identity))
                                (mkArrayLike { arrTy with IndexTypes = remaining }))
                else
                    Error (Other "array indexing: too many indices for array rank")
        | _ ->
            // Poly-pack / tuple indexing: result type is fresh -- codegen
            // resolves via std::get based on flat-leaf paths.
            Ok (mkTyped (TExprTupleIndex (tT, tI)) (env.Subst.Fresh()))))

// ---- Field access ----


and inferUnaryOp (env: TypeEnv) op operand : TypeResult<TypedExpr> =
    inferExpr env operand |> Result.bind (fun tOp ->
        // OpConj: result type equals operand type (complex stays complex,
        // real is the identity), mirroring OpNeg -- no type guard. std::conj
        // emitted only for complex element types.
        //
        // WHOLE-ARRAY conj: a scalar IRUnaryOp(IRConj,_) on an array operand
        // would emit a scalar std::conj against an array value and pass
        // through with no conjugation applied. Route to TExprArrayConjugate
        // instead (same node Hermitian-transpose's TConjugatedCopy uses),
        // materializing a fresh same-shape array via a pool conjugation loop
        // -- this is what makes `hermitian(A) = conj(transpose(A,[0,1]))`
        // work. Both whole-array arms keep the operand's type (Symmetry
        // included) with no compactClassInheritError-style check: negation
        // and conjugation commute with BOTH mirror involutions, so every
        // compact class is genuinely preserved -- (-A)(j,i)=-A(j,i)=A(i,j)
        // keeps antisymmetry; conj(conj z)=z keeps Hermitian.
        match op, tOp.Type with
        | OpConj, ArrayElem _ ->
            Ok (mkTyped (TExprArrayConjugate tOp) tOp.Type)
        | _ ->
            let resTy = match op with
                        | OpNot -> IRTScalar ETBool
                        | OpNeg -> tOp.Type
                        | OpConj -> tOp.Type
                        // OpReal/OpImag project a complex to its component
                        // width (identity on a real operand); OpArg is a real
                        // angle. Synthesized by the intrinsic intercept below.
                        | OpReal | OpImag ->
                            (match tOp.Type with
                             | IRTScalar ETComplex64 -> IRTScalar ETFloat32
                             | IRTScalar ETComplex128 -> IRTScalar ETFloat64
                             | other -> other)
                        | OpArg -> IRTScalar ETFloat64
                        // OpMath is synthesized by the ExprApp intrinsic
                        // intercept, never parsed as ExprUnaryOp -- this arm
                        // is exhaustiveness only.
                        | OpMath _ -> IRTScalar ETFloat64
            Ok (mkTyped (TExprUnaryOp (op, tOp)) resTy))

// ---- Module-qualified value/function: `Math.pi`, `MathLib.double(x)` ----
// These two cases must precede the method-call and struct-field handlers
// because `Math` would otherwise be looked up as a value and fail. The
// DeclImport handler registers imported entries under qualified names
// (`Math.pi`, `MathLib.double`); when the form is `ExprVar n . field`
// and the qualified name resolves, we treat the access as a direct
// variable reference. Falls through to the existing struct/method
// handlers when the qualified name is not registered.

// ---- Provider compound read: alias.load_compound(var, mask) ----
// Rides the generic field-call shape (no new syntax). The mask is any
// integer array; compoundViewType validates full-dimension coverage and
// yields the compact Compound<T, RANK> view type. The maskIR carried in the
// type is a unit placeholder: codegen recovers the actual mask variable by
// name from the argument shape (ProviderReadSpec), not from the type.


and inferMethodCall (env: TypeEnv) obj method args : TypeResult<TypedExpr> =
    inferExpr env obj |> Result.bind (fun tObj ->
    args |> List.map (inferExpr env) |> sequenceResults |> Result.bind (fun tArgs ->
        // Check if this is an impl method call
        match tObj.Type with
        | IRTNamed typeName ->
            match Map.tryFind (typeName, method) env.ImplMethods with
            | Some (funcVarId, funcType) ->
                // Resolve to mangled function call: TypeName__method(self, args)
                let mangledName = sprintf "%s__%s" typeName method
                let retTy = match funcType with FuncElem (_, ret) -> ret | _ -> env.Subst.Fresh()
                let tFunc = mkTyped (TExprVar (mangledName, funcVarId, None)) funcType
                Ok (mkTyped (TExprApp (tFunc, tObj :: tArgs)) retTy)
            | None ->
                // Not an impl method -- treat as struct field access + application
                let (fieldTy, fieldIdx) =
                    match lookupTypeDef typeName env with
                    | Some (TDIStruct (_, _, fields, _)) ->
                        let idx = fields |> List.tryFindIndex (fun (n, _) -> n = method)
                        let ty = fields |> List.tryFind (fun (n, _) -> n = method) |> Option.map snd
                        (ty |> Option.defaultValue (env.Subst.Fresh()),
                         idx |> Option.defaultValue 0)
                    | _ -> (env.Subst.Fresh(), 0)
                let tField = mkTyped (TExprField (tObj, method, fieldIdx)) fieldTy
                // Route through dispatchAppOrIndex so array-typed fields
                // become TExprIndex (with tag-checking) rather than
                // TExprApp. Without this, `data.region(s)` would lower to
                // IRApp and emit a C++ function call against the
                // Array<T,N> wrapper, which has no operator().
                dispatchAppOrIndex env tField tArgs
        | _ ->
            // Non-named type -- regular field access + application
            let tField = mkTyped (TExprField (tObj, method, 0)) (env.Subst.Fresh())
            let retTy = env.Subst.Fresh()
            Ok (mkTyped (TExprApp (tField, tArgs)) retTy)))

// ---- Application / Array indexing ----


and inferMask (env: TypeEnv) array pred : TypeResult<TypedExpr> =
    inferExpr env array |> Result.bind (fun tArr ->
    inferExpr env pred |> Result.bind (fun tPred ->
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
                | _ -> Ok ()
            unifyPredParam |> Result.bind (fun () ->
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
            let minRank = arrayTypes |> List.map (fun at -> at.IndexTypes.Length) |> List.min
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
            Ok (mkTyped (TExprZip tExprs) (IRTTuple (tExprs |> List.map (fun e -> e.Type)))))


and inferExtents (env: TypeEnv) array : TypeResult<TypedExpr> =
    inferExpr env array |> Result.bind (fun tArr ->
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
                let raggedFamilySlot =
                    arrTy.IndexTypes |> List.exists (fun ix ->
                        match ix.IxKind with
                        | IxKRagged | IxKRaggedInline | IxKRaggedOpaque
                        | IxKDepInner
                        | IxKGroupOuter | IxKGroupMember
                        | IxKCompound | IxKCompoundDynamic | IxKSparse -> true
                        | _ -> false)
                if raggedFamilySlot then
                    Error (Other "extents() on a ragged, grouped, or multi-rank compound/sparse array has no scalar answer for the masked/keyed/ragged dimensions. Use extents(row) on a peeled or indexed row, the lengths array, or extents on a rank-1 compound/sparse (which is its cardinality).")
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
            | first :: rest -> rest |> List.forall (fun i -> i = first)
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
            emitWarning env "BL4004" tLoop.Span (sprintf
                "implicit method_for: `(A, B) <@> kernel` iterates the OUTER product of its %d operands (structure-first default), not elementwise pairs. For elementwise co-iteration write `for (A, B) in range<...> <@> kernel` or `zip(A, B) <@> kernel`; write `method_for(...)` explicitly to confirm the outer product and silence this note."
                info.Arrays.Length)
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
and private wreathLeafRefusal (opName: string) (leaves: TypedExpr list) : TypeError option =
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

and inferBinOp env mode op left right : TypeResult<TypedExpr> =
    match op with
    | OpApply ->
        // A bare named-function reference on the kernel side (the right
        // operand of <@>) is eta-expanded to lambda(__k..) -> f(__k..) so it
        // matches the existing TExprLambda kernel arm in inferApply.
        let rightResult =
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
        let applyWith (tL: TypedExpr) =
            rightResult |> Result.bind (fun tR -> inferApply env tL tR)
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
            let outputType =
                match tF.Type, tC.Type with
                | FuncElem (_, IRTScalar et), ArrayElem arr ->
                    // Array with updated element type. Wrap et as IRTScalar
                    // since arr.ElemType is IRType post-B2.
                    mkArrayLike { arr with ElemType = IRTScalar et }
                | FuncElem (_, retTy), _ -> retTy
                | _ -> tC.Type  // fallback: preserve computation type
            match functorMapInheritError env tF tC with
            | Some e -> Error e
            | None -> Ok (mkTyped (TExprFunctorMap (tF, tC)) outputType)))

    | OpArrayProd ->
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

    | OpChoice ->
        inferExpr env left |> Result.bind (fun tL ->
        inferExpr env right |> Result.bind (fun tR ->
            let _ = unify env.Subst tL.Type tR.Type
            Ok (mkTyped (TExprChoice (tL, tR)) tL.Type)))

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
    | OpFallback ->
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
                if hasSym aL then Error FallbackSymmetricLeft
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
                         Error (Other "<|:> with a SparseIdx left operand is not yet supported (a sparse key set has no product-space shape for the dense fallback to cover).")
                     | _ ->
                         Error (Other "<|:> left operand must be a plain dense array or a compound(A, mask) array; ragged/dynamic-compound left operands are not supported."))
            | _ ->
                Error (FallbackNeedsArrays (describe tL.Type, describe tR.Type))))

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
        inferExpr env left |> Result.bind (fun tL ->
        inferExpr env right |> Result.bind (fun tR ->
            // f >> g : (A -> B) >> (B -> C) = (A -> C)
            match env.Subst.Resolve(tL.Type), env.Subst.Resolve(tR.Type) with
            | FuncElem (fArgs, fRet), FuncElem (gArgs, gRet) ->
                // Unify f's return type with g's parameter type(s)
                match gArgs with
                | [gArg] -> 
                    let _ = unify env.Subst fRet gArg
                    Ok (mkTyped (TExprCompose (op, tL, tR)) (mkFuncArrow fArgs gRet))
                | _ ->
                    // Multi-arg g: unify f's return (should be tuple) with g's args as tuple
                    let _ = unify env.Subst fRet (IRTTuple gArgs)
                    Ok (mkTyped (TExprCompose (op, tL, tR)) (mkFuncArrow fArgs gRet))
            | FuncElem _, _ ->
                eprintfn "Warning: right side of >> should be a function"
                Ok (mkTyped (TExprCompose (op, tL, tR)) tR.Type)
            | _, FuncElem (gArgs, gRet) ->
                // f might be a fresh/unresolved type -- permissive
                Ok (mkTyped (TExprCompose (op, tL, tR)) (mkFuncArrow gArgs gRet))
            | _ ->
                // Both unresolved -- permissive, return fresh
                Ok (mkTyped (TExprCompose (op, tL, tR)) (env.Subst.Fresh()))))

    | OpCons ->
        inferExpr env left |> Result.bind (fun tL ->
        inferExpr env right |> Result.bind (fun tR ->
            let resTy = match tR.Type with
                         | IRTTuple ts -> IRTTuple (tL.Type :: ts)
                         | _ -> IRTTuple [tL.Type; tR.Type]
            Ok (mkTyped (TExprBinOp (mode, op, tL, tR)) resTy)))

    | _ ->
        // Arithmetic, comparison, logical
        inferExpr env left |> Result.bind (fun tL ->
        inferExpr env right |> Result.bind (fun tR ->
            let lRes0 = env.Subst.Resolve tL.Type
            let rRes0 = env.Subst.Resolve tR.Type
            let isDist t = match t with IRTDist _ -> true | _ -> false
            if isDist lRes0 || isDist rRes0 then
                // Typed-Dist operator dispatch (checker-level; the surface
                // operand exprs are re-synthesized into the expansion, so
                // this works in any expression position -- see inferDistBinOp).
                inferDistBinOp env op left right lRes0 rRes0
            else
            // S1 SEAM 1 (docs/plan-kernel-body-materialization.md, M-B).
            // Both array-producing arms below -- the two-array zip and the
            // array/scalar broadcast -- are gated on an operand RESOLVING to
            // an array. A caret-shorthand `T^k` row parameter never does on
            // its own, so `r * 2.0` inside `lambda(r: T^1) -> ...` fell to the
            // scalar fallback and typed the row product `IRTScalar Float64`;
            // every array consumer of that intermediate then refused it.
            // Issue the same demand the array intrinsics issue, BEFORE the
            // arms are chosen. See materializeArityVar: the arity constraint
            // already forces a rank-k array, so this changes when the shape
            // appears, not whether it may.
            //
            // ONLY WHEN THE OTHER OPERAND PINS THE SHAPE -- the second of the
            // two restrictions this demand carries (the first, lambda bodies
            // only, lives in materializeArityVar). When the other side is a
            // concrete array (zip) or a concrete scalar (broadcast), the rank is
            // already decided by this expression and unification would reach the
            // same binding, so the demand is not speculation. When BOTH sides
            // are unresolved -- `packsum1`'s `head + packsum1(tail)`, a plain
            // destructure var against the function's own generic return var --
            // nothing here knows the shape, and deferring to lowering (master's
            // behaviour) is the correct answer.
            let pinsShape (t: IRType) =
                match IR.stripUnits t with
                | ArrayElem _ -> true       // zip partner: rank decided
                | IRTScalar _ -> true       // broadcast partner: scalar by construction
                // `T<u>^0` lowers to a unit-annotated VAR but the caret already
                // said rank 0, so it pins just as a scalar does. A BARE var
                // pins nothing.
                | IRTInfer _ -> (match t with IRTUnitAnnotated (IRTInfer _, _) -> true | _ -> false)
                | _ -> false
            (match op with
             | (OpAdd | OpSub | OpMul | OpDiv | OpMod | OpCaret
               | OpEq | OpNeq | OpLt | OpLe | OpGt | OpGe
               | OpAnd | OpOr) when mode = Elementwise ->
                 // Both tested against the PRE-materialization snapshots, so
                 // materializing one operand can never make the other look
                 // pinned (`r1 * r2` over two `T^1` params stays deferred).
                 if pinsShape rRes0 then materializeArityVar env tL "elementwise"
                 if pinsShape lRes0 then materializeArityVar env tR "elementwise"
             | _ -> ())
            let lRes = env.Subst.Resolve tL.Type
            let rRes = env.Subst.Resolve tR.Type
            // Elementwise op on TWO ARRAYS: re-synthesize as the zip
            // co-iteration pipeline -- method_for(zip(l, r)) <@>
            // lambda(u, w) -> u op w |> compute -- and re-infer
            // (synthesize-and-infer, the inferDistBinOp pattern). The
            // direct TExprBinOp lowering hand-rolled a flat rank-1
            // object_for loop, which mis-iterates symmetry-PACKED storage
            // (row pointers into a scalar kernel -- silent miscompile) and
            // any rank > 1 operand; the co-iteration builder handles
            // packed, dense, and multi-rank uniformly. Outer mode ([+])
            // keeps its cross-iteration path.
            let bothArrays =
                (match lRes with ArrayElem _ -> true | _ -> false)
                && (match rRes with ArrayElem _ -> true | _ -> false)
            let isZipOp =
                match op with
                | OpAdd | OpSub | OpMul | OpDiv | OpMod | OpCaret
                | OpEq | OpNeq | OpLt | OpLe | OpGt | OpGe
                | OpAnd | OpOr -> true
                | _ -> false
            // Unit judgment for the synthesized kernel paths below happens
            // at THIS site, not in the kernel body: the lambda parameters
            // are still unresolved inference variables when the body's
            // binop is inferred (they only unify with the element types
            // later, in buildApplyInfo), so inferArithType's unit rules
            // see no units there. The operand ELEMENT units are visible
            // here -- unitRulesForOp checks/composes them and the result
            // element type is stamped over the inferred pipeline type.
            let elemUnits t =
                match t with ArrayElem at -> IR.getUnits at.ElemType | _ -> None
            if mode = Elementwise && bothArrays && isZipOp then
                // Zip-able operand shapes: one index record per operand (dense
                // rank-1, or packed symmetry-class storage of any logical rank --
                // the co-iteration walks its flat canonical cells), or BOTH
                // operands multi-record all-plain-dense with structurally
                // matching shapes (dense rank >= 2 -- the co-iteration spans the
                // full product of the shared records). Mismatched or mixed
                // dense/packed multi-axis shapes reject clearly rather than
                // letting codegen emit a loop-object error.
                let zipable =
                    match lRes, rRes with
                    | ArrayElem aL, ArrayElem aR ->
                        (aL.IndexTypes.Length = 1 && aR.IndexTypes.Length = 1)
                        || (aL.IndexTypes |> List.forall isPlainDenseIx
                            && aR.IndexTypes |> List.forall isPlainDenseIx
                            && indexShapesAgree aL.IndexTypes aR.IndexTypes)
                    | _ -> false
                if zipable then
                    match unitRulesForArrayOp "an elementwise array operator" op (elemUnits lRes) (elemUnits rRes) (Some tR) with
                    | Error e -> Error e
                    | Ok resUnits ->
                        let sp = mergeSpan left.Span right.Span
                        let kbody = mkExpr sp (ExprBinOp (Elementwise, op, mkExpr sp (ExprVar "__zl"), mkExpr sp (ExprVar "__zr")))
                        let klam = mkExpr sp (ExprLambda ([{ Name = "__zl"; Type = None; Default = None; NameSpan = noSpan }; { Name = "__zr"; Type = None; Default = None; NameSpan = noSpan }], None, kbody))
                        let kzip = mkExpr sp (ExprMethodFor [mkExpr sp (ExprZip [left; right])])
                        let synth = mkExpr sp (ExprCompute (mkExpr sp (ExprBinOp (Elementwise, OpApply, kzip, klam))))
                        inferExpr env synth |> Result.map (stampElemUnits env resUnits)
                else
                    Error (Other "elementwise operators on multi-axis arrays require both operands to have matching plain-dense index shapes (same axis tags and extents); mixed dense/packed or mismatched shapes are not zip-able")
            else
            // Elementwise op on ARRAY <-> SCALAR (`A + a`, `2.0 / A`,
            // `A > t`): re-synthesize as a 1-param kernel map over the array
            // operand -- method_for(A) <@> lambda(__bx) -> __bx op s |>
            // compute -- the same synthesize-and-infer route as the both-array
            // zip above. Embedding the scalar operand's SURFACE expr (not its
            // lowered IR) in the lambda body lets capture analysis see its
            // variable references, so the lifted kernel receives them as
            // explicit capture params and emits at file scope
            // (forward-declared) -- embedding the lowered IR directly would
            // make a variable reference a free var, forcing a main-local
            // std::function emitted AFTER its use site (invalid C++).
            let scalarish t =
                match t with IRTScalar _ -> true | _ -> false
            // `T<u>^0` -- the rank-0 abstract parameter -- lowers to a
            // unit-annotated inference VARIABLE, not an IRTScalar
            // (lowerTypeExpr's TyAbstractArray arm stamps the unit onto the
            // scalar type var). It is a scalar by construction: the caret says
            // rank 0. Without this it missed `scalarish`, the broadcast arm
            // never fired, and the fallback below stamped the op's unit around
            // the whole ARRAY result -- `Array<T<time> like Idx<n>><time>`, a
            // shape no `ArrayElem` match can see through, so every later array
            // demand ("prodsum() requires array arguments", "reduce() requires
            // an array") refused a value that IS an array. An UNANNOTATED
            // `T^0` never showed it: with no unit there is nothing to wrap, so
            // the fallback returned the bare array and the program typed.
            //
            // Deliberately narrow: a bare `IRTInfer` is NOT admitted here. An
            // unannotated kernel parameter is one of those and may still
            // resolve to an array, which is a zip, not a broadcast.
            let rankZeroQuantity t =
                match t with
                | IRTUnitAnnotated (IRTInfer _, _) -> true
                | _ -> false
            let arrayScalar =
                match lRes, rRes with
                | ArrayElem _, r when scalarish (IR.stripUnits r) || rankZeroQuantity r -> Some true    // array on left
                | l, ArrayElem _ when scalarish (IR.stripUnits l) || rankZeroQuantity l -> Some false   // array on right
                | _ -> None
            match arrayScalar with
            | Some arrayOnLeft when mode = Elementwise && isZipOp ->
                // Same synthesis-site unit judgment as the zip path above:
                // the kernel param annotation deliberately strips units
                // (elemAnn below), so the body's binop checks nothing --
                // judge the array's ELEMENT units against the scalar
                // operand's units here, in operand order.
                let arrU = elemUnits (if arrayOnLeft then lRes else rRes)
                let scalU = IR.getUnits (if arrayOnLeft then rRes else lRes)
                let luB, ruB = if arrayOnLeft then (arrU, scalU) else (scalU, arrU)
                match unitRulesForArrayOp "an elementwise array/scalar operator" op luB ruB (Some tR) with
                | Error e -> Error e
                | Ok resUnits ->
                let sp = mergeSpan left.Span right.Span
                let (arrExpr, body) =
                    if arrayOnLeft then (left, mkExpr sp (ExprBinOp (Elementwise, op, mkExpr sp (ExprVar "__bx"), right)))
                    else (right, mkExpr sp (ExprBinOp (Elementwise, op, left, mkExpr sp (ExprVar "__bx"))))
                // Annotate the kernel param with the array's element type:
                // at body-inference time an unannotated param is still an
                // unresolved infer var, and inferArithType's promotion rules
                // would fall back to the scalar side's type (`a * A` would
                // type Int64 elements for a double-computing body).
                let elemAnn =
                    match (if arrayOnLeft then lRes else rRes) with
                    | ArrayElem arr ->
                        match IR.stripUnits arr.ElemType with
                        | IRTScalar ETFloat64 -> Some TyFloat64
                        | IRTScalar ETFloat32 -> Some TyFloat32
                        | IRTScalar ETInt64 -> Some TyInt64
                        | IRTScalar ETInt32 -> Some TyInt32
                        | IRTScalar ETBool -> Some TyBool
                        | IRTScalar ETComplex64 -> Some TyComplex64
                        | IRTScalar ETComplex128 -> Some TyComplex128
                        | IRTScalar ETString -> Some TyString
                        | _ -> None
                    | _ -> None
                let synth =
                    mkExpr sp (ExprCompute (mkExpr sp (ExprBinOp (Elementwise, OpApply,
                        mkExpr sp (ExprMethodFor [arrExpr]),
                        mkExpr sp (ExprLambda ([{ Name = "__bx"; Type = elemAnn; Default = None; NameSpan = noSpan }], None, body))))))
                inferExpr env synth |> Result.map (stampElemUnits env resUnits)
            | _ ->
            // env.Builder: inferArithType mints fresh index-type ids for a
            // synthesized outer-product result (same allocator deduceOutputType
            // uses for the method_for output type).
            inferArithType env.Builder mode op tL.Type tR.Type (Some tR) |> Result.bind (fun resTy0 ->
                // S1 SEAM 2 (docs/plan-kernel-body-materialization.md, M-B, the
                // concrete-operand triple). One operand is a real array, the
                // other an UNRESOLVED inference var -- the shape an enclosing
                // kernel's own parameter has while its body is being inferred
                // (`ws <@> lambda(w) -> { let e = exp <@> (w * ts); ... }`).
                // inferArithType's Elementwise table has no arm for that pair,
                // so it fell through to `| _ -> lBare` and handed back THE VAR
                // ITSELF as the product's type. Two things followed, both
                // wrong: the nested `<@>` saw a non-array operand, degraded it
                // to a rank-0 record, and typed `e` a scalar, so `prodsum(e,e)`
                // refused a value that is an array; and any consumer that DID
                // issue an array demand (prodsum, reduce) satisfied it by
                // binding the ALIASED var -- i.e. by making the enclosing
                // kernel's scalar parameter an array, which lowering then
                // emitted as a bogus zip over the parameter.
                //
                // The result SHAPE is knowable without settling which arm
                // applies: whether `w` turns out to be a scalar (broadcast) or
                // an array (zip), an elementwise op against a rank-r array
                // yields that same rank-r shape -- a zip requires the shapes to
                // agree. So stamp the shape and nothing else. The node stays a
                // plain TExprBinOp, so lowering still reads the RESOLVED
                // operands and picks broadcast or zip exactly as before.
                //
                // Element type follows the array operand, which is
                // `promoteElem`'s own answer when the other side carries no
                // information. A var that later resolves complex against a real
                // array is the pre-existing mixed real/complex promotion gap
                // (issue #18), not something this seam can settle -- and the
                // spelling that pins it (`i * w * ts`, complex first) already
                // reaches the resolved-scalar broadcast arm above.
                //
                // Narrow on purpose: only the case where inferArithType handed
                // back a bare VAR is repaired. `ts * w` (array on the LEFT)
                // already answers with the array -- `| _ -> lBare` happens to be
                // right in that orientation -- and is left untouched, wrapper
                // and all.
                //
                // A caret-shorthand `T^k` var counts as unresolved here TOO, and
                // this is what carries M-B inside a NAMED function body, where
                // materializeArityVar deliberately abstains so HM keeps the
                // signature var (see its note). `examples/lswosa.blade`'s
                // `hanning` ends `(s_sub * w / sqrt(scale)) |> compute` with
                // `s_sub: U^1` and `w` a concrete array: repairing the SHAPE
                // makes the function's return array-typed -- which is all the
                // caller's `reduce(sw, (+))` ever needed -- while `U` stays free
                // for the monomorphizer. Nothing is bound; only this node's own
                // type is stamped.
                let resTy =
                    let unboundVar t =
                        match IR.stripUnits t with
                        | IRTInfer _ -> true
                        | _ -> false
                    let reshape (arr: IRArrayType) =
                        match IR.getUnits resTy0 with
                        | Some u -> mkArrayLike { arr with ElemType = IRTUnitAnnotated (IR.stripUnits arr.ElemType, u) }
                        | None -> mkArrayLike arr
                    if mode <> Elementwise || not isZipOp || not (unboundVar resTy0) then resTy0
                    else
                        match lRes, rRes with
                        | ArrayElem arr, other when unboundVar other -> reshape arr
                        | other, ArrayElem arr when unboundVar other -> reshape arr
                        | _ -> resTy0
                // THE conversion seam. `*` and `/` need nothing: unitMul and
                // unitDiv fold the magnitudes into the result TYPE, so the
                // emitted code is untouched. Only the ops that require two
                // operands to share a magnitude bridge one at run time.
                //
                // The target is the result's own signature, so the factor is
                // judged by what the expression is supposed to BE -- which is
                // the annotation when there is one, and otherwise the left
                // operand (unitJoin's existing preference). Comparisons read
                // the left operand directly: their result is Bool and carries
                // no signature to aim at.
                let natural =
                    match op with
                    | OpAdd | OpSub -> IR.getUnits (env.Subst.Resolve resTy)
                    | OpEq | OpNeq | OpLt | OpLe | OpGt | OpGe ->
                        IR.getUnits (env.Subst.Resolve tL.Type)
                    | _ -> None
                // An enclosing annotation names the magnitude the result is
                // SUPPOSED to be, so aim the operands straight at it: one
                // factor each, computed in the magnitude the programmer
                // chose. Guarded by unitCompatible, so an annotation of a
                // different DIMENSION never becomes a conversion -- it falls
                // back to the natural signature and rejects as it always did.
                let target =
                    match env.UnitTarget, natural with
                    | Some t, Some n when unitCompatible t n -> Some t
                    | _ -> natural
                match target with
                | None -> Ok (mkTyped (TExprBinOp (mode, op, tL, tR)) resTy)
                | Some dst ->
                    let ctx =
                        match op with
                        | OpAdd -> "addition" | OpSub -> "subtraction"
                        | _ -> "comparison"
                    // The result now carries the magnitude actually computed
                    // in, which is `dst` whenever an annotation redirected
                    // it; leaving resTy at the natural join would label an
                    // hours value as days. Comparisons keep resTy (Bool).
                    let resTy' =
                        match op, env.Subst.Resolve resTy with
                        | (OpAdd | OpSub), IRTUnitAnnotated (inner, _) -> IRTUnitAnnotated (inner, dst)
                        | _ -> resTy
                    convertScaleTo env ctx dst tL |> Result.bind (fun tL' ->
                    convertScaleTo env ctx dst tR |> Result.map (fun tR' ->
                        mkTyped (TExprBinOp (mode, op, tL', tR')) resTy')))))

/// Checker-level Dist operator dispatch.
/// Scalar * Dist (either side) is kappa_k(c*X) = c^k kappa_k(X) -- pure
/// multilinearity, exact with NO independence requirement -- so it
/// dispatches in ANY expression position, including on Dist-typed function
/// parameters: the surface operand exprs are packed into the expansion
/// DistSynth.scaleExpr builds, and the whole block is re-inferred
/// (synthesize-and-infer). Dist +/- Dist is exact ONLY for independent
/// operands; until function-boundary independence licenses land
/// (`where indep(...)`), provenance is invisible in checker positions, so
/// +/- here steers to the module-level elaboration path (which checks
/// declared independence). dist * dist steers to the Wick machinery message.
and inferDistBinOp (env: TypeEnv) (op: BinOp) (left: Expr) (right: Expr) (lTy: IRType) (rTy: IRType) : TypeResult<TypedExpr> =
    let isScalarish t =
        match t with
        | IRTScalar _ | IRTUnitAnnotated (IRTScalar _, _) | IRTInfer _ -> true
        | _ -> false
    match op, lTy, rTy with
    | OpMul, IRTDist _, IRTDist _ ->
        Error (Other "dist * dist is not defined: cumulants are additive under independent sums and multilinear under scalar scaling; products of random variables need the moment (Wick/Faa di Bruno) machinery")
    | OpMul, IRTDist (order, _, _), c when isScalarish c ->
        inferExpr env (Blade.Ppl.Elaborate.DistSynth.scaleExpr (env.Builder.FreshId()) right left order)
    | OpMul, c, IRTDist (order, _, _) when isScalarish c ->
        inferExpr env (Blade.Ppl.Elaborate.DistSynth.scaleExpr (env.Builder.FreshId()) left right order)
    | (OpAdd | OpSub), IRTDist (lo, _, _), IRTDist (ro, _, _) ->
        // Exact ONLY for independent operands: every cross pair of the two
        // provenance sets must be related under the declared relation  union  the
        // active `where indep` licenses (PPL-owned state). Empty provenance
        // is un-provable, not vacuously independent.
        if lo <> ro then
            Error (DistOrderDisagree ((if op = OpAdd then "+" else "-"), lo, ro))
        else
        let provL = provenanceOfSurface env left
        let provR = provenanceOfSurface env right
        if Set.isEmpty provL || Set.isEmpty provR then
            Error (Other "dist + / -: cannot establish the operands' provenance -- combine dist bindings (or expressions built from them) so independence of their sources can be verified")
        else
            let missing =
                [ for s1 in provL do
                    for s2 in provR do
                      if not (Blade.Ppl.Elaborate.Independence.isRelated s1 s2) then yield (s1, s2) ]
            match missing with
            | (s1, s2) :: _ ->
                // Token-shaped sources ("func.param") mean unlicensed
                // parameters -- steer to the signature license, not to a
                // module-level declaration over internal token names.
                let steering =
                    if s1.Contains "." || s2.Contains "." then
                        "add a `where <alias>.indep(...)` license (with `import ppl as <alias>`) naming the two parameters to the enclosing function's signature"
                    else
                        sprintf "declare `let _ = ppl.independent(%s, %s)` (module level) or a struct `where ppl.indep(...)`" s1 s2
                Error (DistNotIndependent ((if op = OpAdd then "+" else "-"), s1, s2, steering))
            | [] ->
                let weight = if op = OpAdd then (fun _ -> 1.0) else (fun k -> if k % 2 = 0 then 1.0 else -1.0)
                inferExpr env (Blade.Ppl.Elaborate.DistSynth.combineExpr (env.Builder.FreshId()) weight left right lo)
    | _ ->
        Error (DistOpUndefined (ppIRType lTy, ppIRType rTy))

/// Unit rules for one binary op, shared by scalar arithmetic
/// (inferArithType) and the array kernel-synthesis paths in inferBinOp
/// (both-array zip, array<->scalar broadcast). The synthesized kernels
/// infer their bodies against unresolved parameter types -- the params
/// only unify with the element types later, in buildApplyInfo -- so the
/// unit judgment must happen at the synthesis site, where the operand
/// units are still visible. Returns the RESULT unit signature (None =
/// no annotation): +/-/comparison require agreement, * and / compose
/// signatures, `^` scales them by its exponent (unitRulesForCaret -- it
/// needs the exponent's VALUE, so this signature can only reject), and
/// everything else (&&, ||, ...) drops units.
and unitRulesForOp (op: BinOp) (lUnits: UnitSig option) (rUnits: UnitSig option) : TypeResult<UnitSig option> =
    match op with
    | OpAdd | OpSub ->
        match lUnits, rUnits with
        | Some lu, Some ru ->
            // unitCompatible also demands NOMINAL agreement (same quantity,
            // or one side structural); unitJoin keeps whichever nominal is
            // present, so `speed + m/s-value` stays `speed`.
            if unitCompatible lu ru then Ok (Some (unitJoin lu ru))
            else Error (UnitMismatch ((if op = OpAdd then "addition" else "subtraction"), ppUnitSig lu, ppUnitSig ru))
        | Some u, None | None, Some u -> Ok (Some u)
        | None, None -> Ok None
    | OpMul ->
        // Multiplicative composition DROPS the nominal layer (a quantity is
        // an identity, not a factor): the one-sided arms strip it explicitly
        // since the sig passes through unitMul only when both sides carry one.
        match lUnits, rUnits with
        | Some lu, Some ru -> Ok (Some (unitMul lu ru))
        | Some u, None | None, Some u -> Ok (Some { u with Nominal = None })
        | None, None -> Ok None
    | OpDiv | OpMod ->
        match lUnits, rUnits with
        | Some lu, Some ru -> Ok (Some (unitDiv lu ru))
        | Some u, None -> Ok (Some { u with Nominal = None })
        | None, Some u -> Ok (Some (unitDiv unitDimensionless u))
        | None, None -> Ok None
    | OpEq | OpNeq | OpLt | OpLe | OpGt | OpGe ->
        match lUnits, rUnits with
        | Some lu, Some ru when not (unitCompatible lu ru) ->
            Error (UnitMismatch ("comparison", ppUnitSig lu, ppUnitSig ru))
        | _ -> Ok None
    // atan2(y, x) is the angle of the point (x, y): the operands enter only as
    // the RATIO y/x, so any shared signature cancels and the result is always
    // dimensionless -- `atan2(dy: m, dx: m)` is a perfectly good slope angle.
    // Two rejections, and both must live HERE rather than in the array-op
    // guard, because unlike `+` there is no rewrite site that could insert a
    // conversion factor into an intrinsic call:
    //   - incompatible DIMENSIONS (atan2(1 m, 1 s)): the ratio is not a number.
    //   - compatible dimensions at different SCALE (atan2(1 day, 86400 s)):
    //     the ratio is 1/86400 or 1 depending on a factor nobody applied.
    // A one-sided signature is accepted, mirroring `+`: an operand with no
    // annotation makes no claim.
    | OpMath2 "atan2" ->
        (match lUnits, rUnits with
         | Some lu, Some ru ->
             if not (unitCompatible lu ru) then
                 Error (UnitMismatch ("atan2(y, x) arguments (both must carry the same unit -- the intrinsic sees only the ratio y/x)",
                                      ppUnitSig lu, ppUnitSig ru))
             elif not (unitSameScale lu ru) then
                 requireSameScale "atan2(y, x) arguments" lu ru |> Result.map (fun () -> None)
             else Ok None
         | _ -> Ok None)
    // log_base(x, b) = log x / log b: two transcendentals, so BOTH operands are
    // dimensionless-only for exactly the reason unitRulesForUnaryOp gives for
    // `log`. Result is dimensionless.
    | OpMath2 _ ->
        let dimensioned u =
            match u with
            | Some s when not (Map.isEmpty (unitNormalize s).Dims) -> Some s
            | _ -> None
        (match dimensioned lUnits, dimensioned rUnits with
         | Some s, _ | _, Some s ->
             Error (UnitMismatch ("log_base(x, b) argument (a logarithm sums powers of its argument, so it is defined only on dimensionless values; divide by a reference quantity first)",
                                  "dimensionless", ppUnitSig s))
         | None, None -> Ok None)
    // `^` needs the exponent VALUE, which this signature does not carry.
    // Reaching the exponent-free form with a dimensioned base is therefore a
    // rejection, not a silent drop: a call site that can see the right
    // operand routes through unitRulesForOpWith instead and gets the real
    // answer. Erring loudly here keeps a future call site from
    // reintroducing the bug where `^` fell into the catch-all below, so
    // `d ^ 2` over meters typed as a bare Float.
    | OpCaret -> unitRulesForCaret lUnits rUnits None
    | _ -> Ok None

/// The static integer exponent of a `^` right operand, when it has one.
/// Only an integer-valued literal gives the result a unit signature:
/// `d ^ 2` over meters is meters^2, whereas `d ^ n` for a runtime n has a
/// grade that is not known until the value is. `x ^ 2.0` counts (same power
/// as `x ^ 2`); `x ^ 0.5` does not, since meters^(1/2) has no representation
/// in the integer-exponent grammar UnitSig uses. The bound keeps unitPow's
/// exponent multiplication away from overflow -- past it the answer is "no
/// static exponent", which the caller treats as a rejection, so the cap
/// cannot silently drop a signature.
and staticPowExponent (e: TypedExpr) : int option =
    match e.Kind with
    | TExprLit (LitInt n) when n >= -1024L && n <= 1024L -> Some (int n)
    | TExprLit (LitFloat f) when System.Double.IsFinite f && f = floor f && abs f <= 1024.0 ->
        Some (int f)
    | TExprUnaryOp (OpNeg, inner) -> staticPowExponent inner |> Option.map (fun n -> -n)
    | _ -> None

/// Unit rule for `^`. Split out of unitRulesForOp because it is the one op
/// whose result signature depends on the right operand's VALUE rather than
/// its signature: meters ^ 2 is meters^2, which is exactly unitPow -- a
/// function that until now was reachable only from the `Unit area =
/// meters^2` DECLARATION grammar, never from an expression.
///
/// Two rejections. A dimensioned EXPONENT is meaningless at any base
/// (`x ^ (2.0 : Float<seconds>)`). A dimensioned BASE with no static integer
/// exponent is rejected rather than silently stripped, because the result's
/// grade genuinely depends on a runtime value and no signature describes it.
/// A dimensionless base is unconstrained either way, so `x ^ y` over plain
/// Floats -- including the fractional and variable exponents the AD corpus
/// leans on -- is untouched.
and unitRulesForCaret (lUnits: UnitSig option) (rUnits: UnitSig option) (powExp: int option) : TypeResult<UnitSig option> =
    let dimensioned u =
        match u with
        | Some s when not (Map.isEmpty (unitNormalize s).Dims) -> Some s
        | _ -> None
    match dimensioned rUnits with
    | Some ru ->
        Error (UnitMismatch ("exponent of ^ (an exponent must be dimensionless)", "dimensionless", ppUnitSig ru))
    | None ->
        match dimensioned lUnits, powExp with
        | None, _ -> Ok None
        | Some lu, Some n -> Ok (Some (unitPow lu n))
        | Some lu, None ->
            // Phrased as expected-vs-found on the WHOLE power, since the
            // mismatch is not between two signatures but between a signature
            // and the absence of one.
            Error (UnitMismatch (
                        "^ (a dimensioned base needs a compile-time integer exponent -- the grade of the result depends on its value)",
                        sprintf "%s ^ <integer literal>" (ppUnitSig lu),
                        sprintf "%s ^ <value known only at run time>" (ppUnitSig lu)))

/// unitRulesForOp for call sites that hold the right operand's TYPED expr.
/// Only `^` reads it; every other op ignores the extra argument. Sites that
/// genuinely cannot see the operand (the comparison-only path) keep calling
/// unitRulesForOp, whose `^` arm rejects a dimensioned base rather than
/// answering from incomplete information.
and unitRulesForOpWith (op: BinOp) (lUnits: UnitSig option) (rUnits: UnitSig option) (rExpr: TypedExpr option) : TypeResult<UnitSig option> =
    match op with
    | OpCaret -> unitRulesForCaret lUnits rUnits (rExpr |> Option.bind staticPowExponent)
    | _ -> unitRulesForOp op lUnits rUnits

/// Unit rules for the ARRAY elementwise synthesis sites (zip and
/// array<->scalar). Identical to unitRulesForOpWith plus a magnitude guard:
/// those sites annotate the synthesized kernel's params with the element
/// type STRIPPED of units, so the body's binop sees no signatures and the
/// scalar conversion seam cannot fire inside the lambda. An element
/// magnitude difference therefore has to reject here rather than reach
/// codegen as arithmetic on raw numbers.
and unitRulesForArrayOp (context: string) (op: BinOp) (lu: UnitSig option) (ru: UnitSig option) (rExpr: TypedExpr option) : TypeResult<UnitSig option> =
    let scaleGuard =
        match op, lu, ru with
        | (OpAdd | OpSub | OpEq | OpNeq | OpLt | OpLe | OpGt | OpGe), Some a, Some b
                when unitCompatible a b -> requireSameScale context a b
        | _ -> Ok ()
    scaleGuard |> Result.bind (fun () -> unitRulesForOpWith op lu ru rExpr)

/// atan2(y, x) and log_base(x, b): the two BINARY math intrinsics. Surface
/// form is a plain call, shadowable by a user binding exactly like the unary
/// intrinsics and `complex`.
///
/// MECHANISM. The typed node is an ordinary `TExprBinOp (Elementwise,
/// OpMath2 name, ...)` -- not a dedicated 2-argument node -- so the entire
/// existing binary pipeline carries them: the unit tables above, lowering's
/// op map, and codegen's binop emission, where they join `^` as the second
/// binop that renders as a CALL rather than infix. Nothing about loops,
/// captures or kernels had to learn a new shape.
///
/// ARRAY LIFT. An array operand makes the intrinsic elementwise, re-synthesized
/// as `method_for(...) <@> lambda(..) -> name(..) |> compute` and re-inferred --
/// the identical synthesize-and-infer route `complex(A, B)` takes, so zip
/// co-iteration, array/scalar broadcast in BOTH orders, packed storage and
/// codegen are the ones already proven for `A + B`.
///
/// UNITS are judged HERE, at the synthesis site, for both routes: the
/// synthesized lambda annotates its parameters Float64, which strips the
/// element signatures, so a check deferred into the body would see nothing.
/// The rules themselves live in unitRulesForOp's OpMath2 arms (atan2: one
/// shared signature, cancels; log_base: both dimensionless). Result is
/// dimensionless either way, so nothing needs stamping back onto the pipeline.
and inferBinaryIntrinsic (env: TypeEnv) (name: string) (aExpr: Expr) (bExpr: Expr) : TypeResult<TypedExpr> =
    let sp = mergeSpan aExpr.Span bExpr.Span
    inferExpr env aExpr |> Result.bind (fun tA ->
    inferExpr env bExpr |> Result.bind (fun tB ->
        let rA = env.Subst.Resolve tA.Type
        let rB = env.Subst.Resolve tB.Type
        let isArr t = match t with ArrayElem _ -> true | _ -> false
        // Element units for an array operand, scalar units otherwise -- the two
        // routes below judge units off the same pair.
        let opUnits t =
            match t with
            | ArrayElem at -> IR.getUnits at.ElemType
            | other -> IR.getUnits other
        unitRulesForArrayOp (sprintf "%s arguments" name) (OpMath2 name) (opUnits rA) (opUnits rB) None
        |> Result.bind (fun _ ->
            if isArr rA || isArr rB then
                let bparam n : LambdaParam = { Name = n; Type = Some TyFloat64; Default = None; NameSpan = noSpan }
                let bvar n = mkExpr sp (ExprVar n)
                let bbody x y = mkExpr sp (ExprApp (mkExpr sp (ExprVar name), [x; y]))
                let apply former ps body =
                    inferExpr env (mkExpr sp (ExprCompute (mkExpr sp (ExprBinOp (Elementwise, OpApply,
                        former, mkExpr sp (ExprLambda (ps, None, body)))))))
                match isArr rA, isArr rB with
                | true, true ->
                    apply (mkExpr sp (ExprMethodFor [mkExpr sp (ExprZip [aExpr; bExpr])]))
                          [bparam "__m2a"; bparam "__m2b"]
                          (bbody (bvar "__m2a") (bvar "__m2b"))
                | true, false ->
                    // Array <-> scalar broadcast. The scalar's SURFACE expr is
                    // embedded (not its typed node) so capture analysis sees the
                    // variable references, per the array/scalar binop arm's note.
                    apply (mkExpr sp (ExprMethodFor [aExpr])) [bparam "__m2a"] (bbody (bvar "__m2a") bExpr)
                | _ ->
                    apply (mkExpr sp (ExprMethodFor [bExpr])) [bparam "__m2b"] (bbody aExpr (bvar "__m2b"))
            else
                // SCALAR. Neither intrinsic has a std::complex overload, so a
                // complex operand is rejected the way floor/ceil reject one.
                let reject (t: IRType) =
                    match IR.stripUnits t with
                    | IRTScalar (ETComplex64 | ETComplex128) -> Some (IntrinsicNotComplex name)
                    | IRTScalar ETBool | IRTScalar ETString -> Some (IntrinsicNeedsNumeric name)
                    | _ -> None
                match reject rA, reject rB with
                | Some e, _ -> Error e
                | _, Some e -> Error e
                | None, None ->
                    // An UNRESOLVED operand pins to Float64 -- the operand
                    // really is real, so pinning rejects a complex binding here
                    // instead of letting it reach codegen as
                    // std::atan2(complex, complex), a C++ error rather than a
                    // Blade one.
                    //
                    // NOT inside a lambda body, though. There the operand is a
                    // kernel parameter that apply-site unification has not bound
                    // yet, and binding it to BARE Float64 would erase the
                    // element's unit annotation -- after which buildApplyInfo's
                    // kernelBodyUnits walk re-runs this same op's unit rule
                    // against no signatures and silently accepts
                    // `zip(D: m, T: s) <@> lambda(d, t) -> atan2(d, t)`. Defer
                    // instead (what `log` does, and why `log` rejects that shape
                    // while the PINNING floor/ceil arm above does not): the
                    // param binds to the real element type and the unit walk
                    // sees it. The result type is Float64 either way, so
                    // deferring costs nothing else.
                    let pin (t: TypedExpr) =
                        match env.Subst.Resolve t.Type with
                        | IRTInfer _ when not env.InLambdaBody ->
                            unify env.Subst t.Type (IRTScalar ETFloat64)
                        | _ -> Ok ()
                    pin tA |> Result.bind (fun () ->
                    pin tB |> Result.map (fun () ->
                        // Float64 regardless of operand widths: the C++ overload
                        // set promotes integers, so atan2(1, 1) is a double.
                        mkTyped (TExprBinOp (Elementwise, OpMath2 name, tA, tB)) (IRTScalar ETFloat64)))
        )))

/// Materialize the MAGNITUDE conversion a seam needs: multiply `t` by the
/// exact factor carrying its own signature into `dst`. Returns `t` untouched
/// when the magnitudes already agree, which is every program written before
/// scaled units existed.
///
/// The factor is ONE ratio (src.Scale / dst.Scale) computed exactly and only
/// then rounded, so `day -> hour` is 24.0 rather than 86400.0/3600.0
/// evaluated in floating point. A value therefore crosses at most a single
/// multiply and never round-trips through a canonical base -- which is what
/// keeps a `Float64<nanosecond>` from being inflated to seconds and back,
/// losing at both ends.
///
/// INTEGER operands convert only by an exact INTEGER factor. `hour -> second`
/// (x2600) is exact; `second -> hour` would truncate, so it rejects and says
/// so rather than quietly returning zeros.
/// A source signature that is NOT convertible to `dst` is a plain unit
/// mismatch and must be reported as one. This matters because the ascription
/// caller reaches here having checked the value against the annotation with
/// its unit STRIPPED -- so this is the only remaining place a dimension
/// clash at that seam can be caught, and letting it fall through would
/// silently retype `Float<seconds>` as `Float<meters>`.
and convertScaleTo (env: TypeEnv) (context: string) (dst: UnitSig) (t: TypedExpr) : TypeResult<TypedExpr> =
    let resolved = env.Subst.Resolve t.Type
    match IR.getUnits resolved with
    // A BARE value carries no claim and adopts the target unit freely --
    // the literal ergonomics every existing program relies on.
    | None -> Ok t
    | Some src when not (unitCompatible src dst) ->
        Error (UnitMismatch (context, ppUnitSig dst, ppUnitSig src))
    | Some src when unitCompatible src dst && not (unitSameScale src dst) ->
        let factor = unitConversionFactor src dst
        let scaled et lit =
            Ok (mkTyped (TExprBinOp (Elementwise, OpMul, t, mkTyped (TExprLit lit) (IRTScalar et)))
                        (IRTUnitAnnotated (IRTScalar et, dst)))
        match IR.stripUnits resolved with
        | IRTScalar ((ETInt32 | ETInt64) as et) ->
            let exact =
                factor.Den.IsOne && Map.isEmpty factor.Consts
                && abs factor.Num <= bigint System.Int64.MaxValue
            if exact then scaled et (LitInt (int64 factor.Num))
            else
                Error (Other (sprintf
                        "converting %s to %s in %s would scale an integer by %s, which is not a whole number; use a float element type, or annotate the result as %s"
                        (ppUnitSig src) (ppUnitSig dst) context (ppUnitScale factor) (ppUnitSig src)))
        | IRTScalar ((ETFloat32 | ETFloat64) as et) -> scaled et (LitFloat (scaleToFloat factor))
        | other ->
            Error (Other (sprintf
                    "%s relates %s and %s, which differ by the factor %s, but a %s value cannot carry a unit conversion"
                    context (ppUnitSig src) (ppUnitSig dst) (ppUnitScale factor) (IR.ppIRType other)))
    | _ -> Ok t

/// Reject a magnitude difference at a seam that does NOT yet insert a
/// conversion (array elementwise, argument passing, ascription, folds).
/// unitCompatible is scale-blind on purpose -- it answers "convertible?" --
/// so without this guard those seams would accept `day` where `second` is
/// wanted and compute on the raw numbers. Loud beats silent.
and requireSameScale (context: string) (expected: UnitSig) (actual: UnitSig) : TypeResult<unit> =
    if unitSameScale expected actual then Ok ()
    else
        Error (Other (sprintf
                "%s relates %s and %s: same dimensions, but magnitudes differing by the factor %s. Blade inserts a unit conversion only in scalar +, -, and comparisons; here the operands must already share a magnitude"
                context (ppUnitSig expected) (ppUnitSig actual)
                (ppUnitScale (unitConversionFactor actual expected))))

/// Overwrite the ELEMENT unit annotation of an array-typed result from a
/// synthesized kernel pipeline with the signature the unit rules computed.
/// Without this the kernel return type leaks the LEFT operand's unit
/// through * and / (meters * meters would stay meters). None strips --
/// comparisons produce Bool elements, and `^` over a dimensionless base
/// has no signature to stamp (a dimensioned base either composes through
/// unitPow or rejects; see unitRulesForCaret).
and stampElemUnits env (resUnits: UnitSig option) (t: TypedExpr) : TypedExpr =
    match env.Subst.Resolve t.Type with
    | ArrayElem arr ->
        let bare = IR.stripUnits arr.ElemType
        let elem =
            match resUnits with
            | Some u -> IRTUnitAnnotated (bare, u)
            | None -> bare
        { t with Type = mkArrayLike { arr with ElemType = elem } }
    | _ -> t

/// Unit rules for the unary and math-intrinsic ops, shared by
/// scalar-position intrinsic inference and the kernel-body walk. Split by
/// HOMOGENEITY, which decides whether a function of a dimensioned quantity
/// has a signature at all:
///   - degree 1 (f(cx)=c*f(x)): negation, abs, complex projections PRESERVE.
///   - degree 1/2: sqrt halves exponents when all even (sqrt(m^2)=m); odd
///     exponents have no integer-signature representation, so dropped.
///   - degree 0: arg, logical not are dimensionless-out for any operand.
///   - NOT homogeneous: floor/ceil and transcendentals REJECT a dimensioned
///     operand instead of inventing a result signature. floor is NOT lumped
///     with abs: rounding doesn't commute with scale (floor(3.7m)=3m, but
///     370cm floors to 3.7m). exp/log/sin/... are worse: their series add
///     powers of the argument, so a dimensioned argument is meaningless.
/// A dimensionless (or absent) signature is never a rejection -- keeps
/// `floor(x)`/`exp(x)` usable on ordinary numbers.
and unitRulesForUnaryOp (op: UnaryOp) (u: UnitSig option) : TypeResult<UnitSig option> =
    let dimensioned =
        match u with
        | Some s when not (Map.isEmpty (unitNormalize s).Dims) -> Some s
        | _ -> None
    let requireDimensionless (context: string) =
        match dimensioned with
        | Some s -> Error (UnitMismatch (context, "dimensionless", ppUnitSig s))
        | None -> Ok None
    match op with
    | OpNeg | OpConj | OpReal | OpImag -> Ok u
    | OpNot | OpArg -> Ok None
    | OpMath "abs" -> Ok u
    | OpMath "sqrt" ->
        // sqrt DROPS the nominal layer like the other non-degree-1
        // compositions (sqrt of a quantity is not that quantity).
        match u with
        | Some s ->
            let n = unitNormalize s
            if n.Dims |> Map.forall (fun _ ex -> ex % 2 = 0)
            then Ok (Some (unitOfDims (n.Dims |> Map.map (fun _ ex -> ex / 2))))
            else Ok None
        | None -> Ok None
    | OpMath (("floor" | "ceil") as name) ->
        requireDimensionless
            (sprintf "%s() argument (rounding is not scale-invariant: floor(3.7 m) = 3 m, but the same length as 370 cm floors to 370 cm = 3.7 m; divide by a reference quantity to get a dimensionless count first)" name)
    | OpMath name ->
        requireDimensionless
            (sprintf "%s() argument (a transcendental sums powers of its argument, so it is defined only on dimensionless values; divide by a reference quantity first)" name)

/// Unit-only second pass over a KERNEL BODY, run by buildApplyInfo after
/// parameter unification has bound the parameter types. The body was
/// type-inferred while its params were still unresolved inference
/// variables, so the cached node types carry no unit information (or a
/// leaked left-operand annotation) -- this walk recomputes the signature
/// bottom-up with the same per-op table the scalar path uses
/// (unitRulesForOp), so `t * t` over meters elements comes out meters^2
/// and a mismatched `a + b` over a hand-written zip REJECTS. `bound`
/// carries walk-computed signatures for kernel-local lets (their cached
/// var types are as stale as the intermediate nodes). Constructs the walk
/// doesn't model return None (no claim) -- only op rules error.
and kernelBodyUnits (env: TypeEnv) (bound: Map<IRId, UnitSig option>) (e: TypedExpr) : TypeResult<UnitSig option> =
    let combineBranches context (a: UnitSig option) (b: UnitSig option) =
        match a, b with
        | Some ua, Some ub ->
            if not (unitCompatible ua ub) then Error (UnitMismatch (context, ppUnitSig ua, ppUnitSig ub))
            // This walk RECOMPUTES signatures over an already-typed kernel
            // body; it rewrites nothing, so a magnitude difference between
            // branches has no place to put a factor.
            elif not (unitSameScale ua ub) then requireSameScale context ua ub |> Result.map (fun () -> None)
            else Ok (Some (unitJoin ua ub))
        | Some u, None | None, Some u -> Ok (Some u)
        | None, None -> Ok None
    let ofType (t: IRType) = IR.getUnits (env.Subst.Resolve t)
    let elemOfType (t: IRType) =
        match env.Subst.Resolve t with
        | ArrayElem at -> IR.getUnits at.ElemType
        | resolved -> IR.getUnits resolved
    // Walk a subtree only for its ERRORS (mismatched ops inside call args,
    // assignment right-hand sides, conditions), discarding the signature.
    let errorsOnly sub = kernelBodyUnits env bound sub |> Result.map ignore
    match e.Kind with
    | TExprLit _ | TExprComplexLit _ -> Ok None
    | TExprVar (_, varId, _) ->
        match Map.tryFind varId bound with
        | Some u -> Ok u
        // ELEMENT-aware, like the TExprIndex / TExprReduce arms below: an
        // ARRAY-typed var carries no TOP-level annotation (`getUnits` only
        // reads IRTUnitAnnotated), so `ofType` reported None for one and a
        // kernel-body product involving it silently kept only the OTHER
        // operand's signature -- `let wt = w * ts` computed 1/day instead of
        // 1/day * day = dimensionless, and a nested `sin <@> wt` then
        // rejected on the param's own unit. A kernel body reads an array
        // elementwise, so the element signature is the right claim.
        | None -> Ok (elemOfType e.Type)
    | TExprBinOp (_, op, l, r) ->
        kernelBodyUnits env bound l |> Result.bind (fun lu ->
        kernelBodyUnits env bound r |> Result.bind (fun ru ->
        unitRulesForOpWith op lu ru (Some r)))
    | TExprUnaryOp (op, inner) ->
        kernelBodyUnits env bound inner |> Result.bind (unitRulesForUnaryOp op)
    | TExprIf (cond, thenBr, elseBr) ->
        errorsOnly cond |> Result.bind (fun () ->
        kernelBodyUnits env bound thenBr |> Result.bind (fun tu ->
        kernelBodyUnits env bound elseBr |> Result.bind (fun eu ->
        combineBranches "conditional branches" tu eu)))
    | TExprLet (_, varId, value, body) ->
        kernelBodyUnits env bound value |> Result.bind (fun vu ->
        kernelBodyUnits env (Map.add varId vu bound) body)
    | TExprBlock (stmts, finalOpt) ->
        let foldStmt acc stmt =
            acc |> Result.bind (fun (b: Map<IRId, UnitSig option>) ->
                match stmt with
                | TStmtLet binding ->
                    kernelBodyUnits env b binding.Value
                    |> Result.map (fun vu -> Map.add binding.VarId vu b)
                | TStmtAssign (_, rhs) ->
                    kernelBodyUnits env b rhs |> Result.map (fun _ -> b)
                | TStmtExpr sub ->
                    kernelBodyUnits env b sub |> Result.map (fun _ -> b)
                | TStmtForIn _ -> Ok b)
        stmts |> List.fold foldStmt (Ok bound) |> Result.bind (fun b ->
            match finalOpt with
            | Some f -> kernelBodyUnits env b f
            | None -> Ok None)
    | TExprApp (_, args) ->
        args |> List.fold (fun acc a ->
            acc |> Result.bind (fun () -> errorsOnly a)) (Ok ())
        |> Result.map (fun () -> None)
    | TExprIndex (arr, _, _) -> Ok (elemOfType arr.Type)
    | TExprReduce (arr, _, _) -> Ok (elemOfType arr.Type)
    | TExprField _ | TExprTupleIndex _ -> Ok (ofType e.Type)
    // NESTED ELEMENTWISE MAP inside a kernel body (`lambda(w) -> { let e =
    // exp <@> (i * w * ts); ... }`). Without an arm here this fell to the
    // no-claim catch-all, which is why the nested apply's OWN unit check had
    // to reject eagerly -- and it ran during the OUTER lambda's body
    // inference, while `w` was still an unresolved infer var contributing no
    // units, so it judged a PROVISIONAL element signature (exactly the
    // dimensioned capture's own, `day`) and rejected a product that cancels.
    // That inner check now defers (buildApplyInfo); this arm is the recheck
    // that keeps the deferral from becoming a false acceptance. It is also
    // strictly more informative than the old catch-all: an array-typed
    // operand carries no TOP-level unit annotation, so the plain scalar walk
    // read every nested map as "no claim".
    | TExprApply info -> nestedApplyElemUnits env bound info
    // `compute` is unit-transparent: it forces a computation to a value.
    | TExprCompute inner -> kernelBodyUnits env bound inner
    | _ -> Ok None

/// ELEMENT-level sibling of `kernelBodyUnits`, for the array operands of a
/// nested map. The scalar walk answers about SCALAR positions and reads a
/// node's TOP-level annotation, which an array type never carries (`getUnits`
/// only sees `IRTUnitAnnotated`), so it reports None for every array operand.
/// This reads through to the ELEMENT signature instead, and the two are
/// mutually recursive because an operand can itself be a synthesized
/// elementwise map (`i * w * ts` re-synthesizes as a broadcast map whose
/// stamped element annotation is the provisional one we must not trust).
///
/// Note this cannot be selected by testing an operand's TYPE for `ArrayElem`:
/// inside a kernel body an array-valued expression is typed at its ELEMENT
/// type (`w * ts` is a scalar `Float64<1/day>` node, and a nested apply node
/// is typed `Float64`), so the array-ness is in the expression SHAPE, not the
/// type. The walk dispatches on shape for exactly that reason.
and nestedOperandElemUnits (env: TypeEnv) (bound: Map<IRId, UnitSig option>) (e: TypedExpr) : TypeResult<UnitSig option> =
    let elemOfType (t: IRType) =
        match env.Subst.Resolve t with
        | ArrayElem at -> IR.getUnits at.ElemType
        | resolved -> IR.getUnits resolved
    match e.Kind with
    | TExprCompute inner -> nestedOperandElemUnits env bound inner
    | TExprApply info -> nestedApplyElemUnits env bound info
    // RECOMPUTE rather than read the stamp: a synthesized elementwise binop
    // (`w * ts`) carries a first-pass element annotation computed while the
    // captured param was unresolved. Leaves stay elem-aware, so a scalar
    // operand contributes its own signature and an array its element's.
    | TExprBinOp (_, op, l, r) ->
        nestedOperandElemUnits env bound l |> Result.bind (fun lu ->
        nestedOperandElemUnits env bound r |> Result.bind (fun ru ->
        unitRulesForOpWith op lu ru (Some r)))
    | TExprUnaryOp (op, inner) ->
        nestedOperandElemUnits env bound inner |> Result.bind (unitRulesForUnaryOp op)
    | TExprVar (_, varId, _) ->
        match Map.tryFind varId bound with
        | Some u -> Ok u
        | None -> Ok (elemOfType e.Type)
    | _ -> Ok (elemOfType e.Type)

/// The element signature a nested `<@>` map PRODUCES: bind the kernel's params
/// to its operands' element signatures (recomputed, not read off the
/// provisional stamps) and walk the kernel body with the ordinary machinery.
/// For an eta-expanded unary intrinsic (`exp <@> A` becomes
/// `lambda(__k) -> exp(__k)`) this bottoms out in the same
/// `unitRulesForUnaryOp` application the scalar path uses, so the transcendental
/// rule is enforced once, at the point where the operand units are finally real.
/// Anything not modelled (non-lambda kernel, arity disagreement, co-iteration
/// index params) returns None -- no claim, exactly as before.
and nestedApplyElemUnits (env: TypeEnv) (bound: Map<IRId, UnitSig option>) (info: TypedApplyInfo) : TypeResult<UnitSig option> =
    let rec kernelLambda (k: TypedExpr) =
        match k.Kind with
        | TExprLambda li -> Some li
        | TExprReynolds (inner, _) -> kernelLambda inner
        | _ -> None
    let rec collect acc rest =
        match rest with
        | [] -> Ok (List.rev acc)
        | a :: tl ->
            nestedOperandElemUnits env bound a
            |> Result.bind (fun u -> collect (u :: acc) tl)
    collect [] info.Arrays
    |> Result.bind (fun inputUnits ->
        match kernelLambda info.Kernel with
        | Some li when li.Params.Length = inputUnits.Length ->
            let bound' =
                List.fold2 (fun m (p: TypedParam) u -> Map.add p.VarId u m) bound li.Params inputUnits
            kernelBodyUnits env bound' li.Body
        | _ -> Ok None)

/// `rExpr` is the right operand's typed expr, carried only so the `^` unit
/// rule can read its exponent VALUE (see unitRulesForCaret); every other op
/// ignores it, and `None` is a safe caller default everywhere except a `^`
/// over a dimensioned base, which then rejects rather than guessing.
and inferArithType (builder: IRBuilder) mode op leftTy rightTy (rExpr: TypedExpr option) : TypeResult<IRType> =
    // Result type for an OUTER (bracketed) op over two arrays, shared by
    // `boolResultTy` and `bareResult`. Careful NOT to spell this as
    // `mkArrayLike { arrL with ... }`, which would smuggle three of the LEFT
    // OPERAND's properties onto a value that is not the left operand:
    //   - Identity: `None`, matching `deduceOutputType` -- an outer product
    //     is a fresh array with no source-level name, so wearing arrL's
    //     would make two different arrays indistinguishable.
    //   - IsVirtual: `false` -- an outer product is materialized
    //     (`genObjectForApplication` allocates and fills it), so inheriting
    //     `true` would describe real storage as virtual.
    //   - Index-type Ids: refreshed, not copied -- otherwise an axis of the
    //     product and an axis of an operand compare equal by id (consumers:
    //     IR's compound/mask prefix check, Lowering's dim-name lookup).
    // The refresh does NOT remap intra-record back-references (DepIdx inner
    // extent formulas, `Dependencies`); safe since this emitter only handles
    // rank-1 plain-dense operands, so ragged/dependent operands can't reach it.
    let mkOuterResult (arrL: IRArrayType) (arrR: IRArrayType) (elemTy: IRType) : IRType =
        mkArrayLike
            { arrL with
                ElemType = elemTy
                IndexTypes =
                    (arrL.IndexTypes @ arrR.IndexTypes)
                    |> List.map (fun ix -> { ix with Id = builder.FreshId() })
                IsVirtual = false
                Identity = None }
    // Boolean/comparison ops over ARRAYS lift to a Bool-ELEMENT array; only
    // the SHAPE rule differs per mode. Lowering already synthesizes the
    // object_for with a Bool kernel, so only the RESULT TYPE needs to become
    // the array here. Outer mode is the CROSS product of the operands'
    // index spaces (`A [<] B` with |A|=5, |B|=3 is a 5x3 Bool array, matching
    // codegen's deduceOutputType). Without this arm it falls through to the
    // scalar `IRTScalar ETBool` arm -- a silent miscompile: the loop nest and
    // Array<bool,2> allocation are emitted correctly, but the IR type says
    // "scalar Bool", so genPrintStatements takes the scalar `cout << x` path
    // and Array's implicit pointer-promotion operator prints an ADDRESS,
    // with nothing downstream seeing the real values. Array<->scalar
    // broadcast in Outer mode has no cross axis, so it still degrades to the
    // scalar Bool arm. A FUNCTION not a value: mints fresh index ids only
    // when reached, so an eagerly-bound `let` wouldn't burn ids on every
    // arithmetic binop that never calls it.
    let boolResultTy () =
        match mode, IR.stripUnits leftTy, IR.stripUnits rightTy with
        | Elementwise, ArrayElem arrL, ArrayElem _ ->
            mkArrayLike { arrL with ElemType = IRTScalar ETBool }
        | Elementwise, ArrayElem arrL, _ ->
            // array <op> scalar broadcast (`A > 2.0`): result shape follows the array
            mkArrayLike { arrL with ElemType = IRTScalar ETBool }
        | Elementwise, _, ArrayElem arrR ->
            // scalar <op> array broadcast (`2.0 < A`): result shape follows the array
            mkArrayLike { arrR with ElemType = IRTScalar ETBool }
        | Outer, ArrayElem arrL, ArrayElem arrR ->
            // Outer (bracketed) comparison / logical over two arrays: index
            // spaces concatenate (left axes then right axes), elements are Bool.
            // Mirrors the arithmetic Outer rule in `bareResult` below, which
            // keeps the left operand's ElemType instead.
            mkOuterResult arrL arrR (IRTScalar ETBool)
        | _ -> IRTScalar ETBool
    match op with
    | OpEq | OpNeq | OpLt | OpLe | OpGt | OpGe ->
        // Comparisons require compatible units (unitRulesForOp errors on
        // mismatch; the result carries no annotation)
        unitRulesForOp op (IR.getUnits leftTy) (IR.getUnits rightTy)
        |> Result.map (fun _ -> boolResultTy ())
    | OpAnd | OpOr -> Ok (boolResultTy ())
    | _ ->
        // Extract unit annotations if present
        let lUnits = IR.getUnits leftTy
        let rUnits = IR.getUnits rightTy
        // A WILDCARD-tagged operand is arithmetic-transparent. The ban below
        // exists because an index type is a nominal label for a particular
        // space, and the escape hatch it points at -- "value-level position
        // arithmetic is reachable via virtual array iteration, which produces
        // plain ints" -- is exactly what a `Base<_>` parameter is consuming.
        // Declining to name the space is declining the label, so there is no
        // space to mis-mix and nothing to preserve: strip the wildcard here
        // and the operand promotes, unit-checks and shapes like its bare
        // inner type everywhere downstream.
        //
        // Concrete tags are untouched: `Nat<Lat> * 2.0` still errors. Note
        // this also makes the annotated form agree with the UNANNOTATED one,
        // which has always allowed the arithmetic by accident of ordering --
        // an unannotated kernel param is still an inference variable when its
        // body is typed, and only unifies with Nat<Lat> afterwards.
        let stripAnyTag t =
            match t with
            | IRTIdxTagged (inner, IRefAny) -> inner
            | _ -> t
        let lBare = IR.stripUnits leftTy |> stripAnyTag
        let rBare = IR.stripUnits rightTy |> stripAnyTag
        // No arithmetic on index types (named OR anonymous). Per the
        // formalism's nominal-type discipline, index types are nominal
        // labels -- arithmetic on them serves no useful purpose:
        // (1) value-level position arithmetic is reachable via virtual
        // array iteration, which produces plain ints; (2) type-level
        // construction of new index types from arithmetic is a separate
        // (deferred) workstream. So we simply reject. Post-Option-C, all
        // index references (named or anonymous, value or element position)
        // are represented uniformly as IRTIdxTagged.
        // Floats: by the same principle, index types are completely
        // incompatible with floating point -- no `Idx + Float` either.
        let isIndexType t =
            match t with
            | IRTIdxTagged _ -> true
            | _ -> false
        let indexTypeName t =
            match t with
            | IRTIdxTagged (_, IRefNamed n) -> n
            | IRTIdxTagged (_, IRefAnon _) -> "<anonymous Idx>"
            | _ -> "?"
        let indexArithErr =
            match lBare, rBare with
            | IRTIdxTagged (_, IRefNamed ln), IRTIdxTagged (_, IRefNamed rn) when ln <> rn ->
                Some (CrossNominalIndexArith (ln, rn))
            | IRTIdxTagged (_, IRefAnon (lid, _)), IRTIdxTagged (_, IRefAnon (rid, _)) when lid <> rid ->
                Some (CrossAnonIndexArith (lid, rid))
            | l, r when isIndexType l || isIndexType r ->
                let n = if isIndexType l then indexTypeName l else indexTypeName r
                Some (IndexTypeArithForbidden n)
            | _ -> None
        match indexArithErr with
        | Some err -> Error err
        | None ->
        // Dist operands: checker-level operator dispatch (per-order cumulant
        // combination -- + adds, - flips odd orders, scalar * is c^k
        // multilinearity, all independence-gated) is still incomplete.
        // Until it lands, module-level dist operators still go
        // through the elaboration rewrites (which never reach here); any
        // OTHER position -- notably operators on Dist-typed function
        // parameters -- must error with steering rather than fall through
        // to scalar promotion and silently type nonsense.
        match lBare, rBare with
        | IRTDist _, _ | _, IRTDist _ ->
            Error (Other "operators on Dist values are not yet typed in this position (checker-level dist operator dispatch is in progress): combine dists where they are constructed (module-level d1 + d2, d1 - d2, c * d), or project a component with cumulant(d, k)")
        | _ ->
        // (Both-array Elementwise ops never reach here anymore: inferBinOp
        // re-synthesizes them as the zip co-iteration pipeline, which
        // handles packed and multi-rank storage the plain lowering could
        // not.)
        // Element-type promotion for array<->scalar broadcast: same rule as
        // the scalar-scalar cases below.
        let promoteElem (elemTy: IRType) (scalarTy: IRType) =
            match IR.stripUnits elemTy, IR.stripUnits scalarTy with
            // Complex mixed with real (or mixed-width complex) widens to the
            // appropriate complex type -- otherwise the element type would fall
            // back to the real side and the array would be typed real.
            | IRTScalar le, IRTScalar re
                when (match IR.promoteElemType le re with Some (ETComplex64 | ETComplex128) -> true | _ -> false) ->
                IRTScalar (IR.promoteElemType le re |> Option.get)
            | _ ->
                match elemTy, scalarTy with
                | IRTScalar ETFloat64, _ | _, IRTScalar ETFloat64 -> IRTScalar ETFloat64
                | IRTScalar ETFloat32, _ | _, IRTScalar ETFloat32 -> IRTScalar ETFloat32
                | _ -> elemTy
        let bareResult =
            match mode with
            | Outer ->
                match lBare, rBare with
                | ArrayElem arrL, ArrayElem arrR ->
                    // Element type stays the LEFT operand's (the
                    // arithmetic-Outer convention, matched by lowering's
                    // kernelRetType = IRTScalar elemTypeL); everything else about
                    // the result is synthesized fresh -- see mkOuterResult.
                    mkOuterResult arrL arrR arrL.ElemType
                | _ -> lBare
            | Elementwise ->
                match lBare, rBare with
                // Array <op> scalar / scalar <op> array broadcast (`A + a`,
                // `2.0 / A`): the result follows the array's shape; the
                // ELEMENT type follows scalar promotion against the other
                // operand. Without this arm these fall into the scalar
                // rules below and type the whole result as a scalar (or
                // the bare left type), which codegen then emits as pointer
                // arithmetic on the Array wrapper. Lowering's broadcast
                // kernel path (lowerTypedBinOp) is the value-side pair of
                // this rule.
                | ArrayElem arrL, (IRTScalar _ as s) ->
                    mkArrayLike { arrL with ElemType = promoteElem arrL.ElemType s }
                | (IRTScalar _ as s), ArrayElem arrR ->
                    mkArrayLike { arrR with ElemType = promoteElem arrR.ElemType s }
                // Scalar complex promotion (mixed real/complex or mixed-width
                // complex): must precede the float rules so complex wins.
                | IRTScalar le, IRTScalar re
                    when (match IR.promoteElemType le re with Some (ETComplex64 | ETComplex128) -> true | _ -> false) ->
                    IRTScalar (IR.promoteElemType le re |> Option.get)
                | IRTScalar ETFloat64, _ | _, IRTScalar ETFloat64 -> IRTScalar ETFloat64
                | IRTScalar ETFloat32, _ | _, IRTScalar ETFloat32 -> IRTScalar ETFloat32
                | _ -> lBare
        // Apply unit rules based on operation (shared with the array
        // kernel-synthesis paths in inferBinOp)
        unitRulesForOpWith op lUnits rUnits rExpr |> Result.map (function
            | Some u -> IRTUnitAnnotated (bareResult, u)
            | None -> bareResult)

// 10b. <@> Application with Symmetry Analysis

/// Resolve a TypedExpr through variable bindings to find the underlying
/// method_for / object_for / lambda.
and resolveTypedExpr (env: TypeEnv) (texpr: TypedExpr) : TypedExpr =
    match texpr.Kind with
    | TExprVar (name, _, _) ->
        match lookupVar name env with
        | Some info -> info.TypedValue |> Option.defaultValue texpr
        | None -> texpr
    | _ -> texpr

/// `resolveTypedExpr` to a FIXPOINT. The one-hop version is why `let P = (A,B)`
/// and `let Q = P` are different programs today (docs/plan-tuples-vs-arg-packs.md
/// 3.1, M1: `K <@> P` splats, `K <@> Q` does not, and the second miscompiles).
/// Substitution has to hold, so alias depth may not be observable. Fuelled
/// rather than cycle-tracked: a binding chain is acyclic by construction (a
/// `let` cannot mention itself), and the fuel is the cheap backstop if some
/// future binder form breaks that.
and resolveTypedExprDeep (env: TypeEnv) (texpr: TypedExpr) : TypedExpr =
    let rec chase (fuel: int) (e: TypedExpr) =
        if fuel <= 0 then e
        else
            match e.Kind with
            | TExprVar _ ->
                let r = resolveTypedExpr env e
                if System.Object.ReferenceEquals(r, e) then e else chase (fuel - 1) r
            | _ -> e
    chase 64 texpr

/// The TOP-LEVEL TUPLE WIDTH of a value, by its STATIC type, seen through any
/// number of alias hops (docs/plan-tuples-vs-arg-packs.md 6c, rule 1). `None`
/// for anything that is not a tuple. This is the only thing the matcher reads
/// off a node: its spine width, never its depth.
and tupleNodeWidth (env: TypeEnv) (e: TypedExpr) : int option =
    match env.Subst.Resolve e.Type with
    | IRTTuple ts when ts.Length >= 2 -> Some ts.Length
    | _ -> None

/// The COMPONENTS of a tuple node, if they can be named as expressions.
/// A tuple-typed value whose alias chain does not end at a written tuple (a
/// `<&!>` fusion result, say) has a width but no component expressions; the
/// callers report that rather than inventing projections.
and tupleNodeParts (env: TypeEnv) (e: TypedExpr) : TypedExpr list option =
    match (resolveTypedExprDeep env e).Kind with
    | TExprTuple es when es.Length >= 2 -> Some es
    | _ -> None

/// The SPINE of a pack (docs/plan-tuples-vs-arg-packs.md 6c, rules 1 and 3).
///
/// Each element of a written operand list is ONE NODE, and its internal
/// structure is DATA that survives -- this is the one-level rule that replaced
/// 6b's free-monoid deep-flatten. The single transformation applied here is
/// rule 3's ONE-LEVEL SPLICE: a pack that is a single tuple node opens into its
/// components, which is what makes `f((a, b)) == f(a, b)` and, with the alias
/// fixpoint below it, what makes `K <@> (A, B)`, `let P = (A, B); K <@> P` and
/// `let Q = P; K <@> Q` one program (3.1's M1, 3.2's M4).
///
/// It does NOT recurse: `(A, (B, C))` splices to the two nodes `A` and
/// `(B, C)`, and the second stays a tuple. Under 6b it became three leaves,
/// which silently equated `(A, (B, C))` with `(A, B, C)`, destroyed nested
/// tuples as data, and broke `Poly`'s documented "arity counts top level"
/// (formalism.md:787). 6c's ruling restores all three, at the cost of the
/// cross-level recount (`Tuple<4>` over `((a,b),(c,d))`, which is now an error
/// steering to the flat spelling).
///
/// A node that came through an alias is reported as the ALIAS EXPRESSION, not
/// the chased binding: identities (`AIDVariable name`) are read off these
/// nodes, and a plain non-tuple binding must keep naming itself exactly as it
/// does today. Only the outermost tuple structure is seen through.
and packSpine (env: TypeEnv) (operands: TypedExpr list) : TypedExpr list =
    match operands with
    | [single] ->
        match tupleNodeParts env single with
        | Some parts -> parts
        | None -> operands
    | _ -> operands

/// The `<$>` half of the compact-class inheritance check (see
/// compactClassInheritError). `f <$> c` applies f to every element of c, so f
/// must commute with the mirror involution of c's storage class before the
/// result may keep that class -- and it does keep it: the arms below copy c's
/// index-type record wholesale, `Symmetry` included, for ANY f.
///
/// The question is asked of the MAPPED-OVER computation `tC`, not of the
/// deduced output type, because the two can disagree. When f's return type is
/// still an inference variable the arms fall through to `retTy` and the output
/// carries no records at all at this point; codegen then folds f into the inner
/// kernel (applyFunctorWrappers / mapKernelInner) and allocates from the INNER
/// apply's type -- which was deduced before f existed and so certified only the
/// inner kernel's parity. Keying off `tC` covers both routes.
///
/// f is unary by construction here (it maps one element), so the law is always
/// its parameter 0's.
and functorMapInheritError (env: TypeEnv) (tF: TypedExpr) (tC: TypedExpr)
                           : TypeError option =
    match env.Subst.Resolve tC.Type with
    // SymWreath joins the filter: a wreath class whose mirror negates (any '-'
    // level) needs the same certificate, and leaving it out would let an inner
    // '-' level inherit through a `<$>` map uncertified.
    | ArrayElem arr when
            arr.IndexTypes |> List.exists (fun ix ->
                ix.Rank > 1
                && (ix.Symmetry = SymAntisymmetric || ix.Symmetry = SymHermitian
                    || ix.Symmetry = SymWreath)) ->
        let signResolver (calleeId: IRId) =
            match env.FuncSignParities.TryGetValue calleeId with
            | true, ps -> Some ps
            | _ -> None
        // An f that surfaces as neither a lambda nor a summarized top-level
        // function certifies nothing and lands on the conservative refusal.
        let (signParities, conjCommutes, fName) =
            match (resolveTypedExpr env tF).Kind with
            | TExprLambda li ->
                (Blade.Deduce.deduceSignParities signResolver li.Params li.Body,
                 Blade.Deduce.deduceConjCommutes li.Params li.Body,
                 (match li.Params with
                  | (p: TypedParam) :: _ -> p.Name
                  | [] -> "the mapped element"))
            | TExprVar (name, id, _) ->
                // A top-level `function` is bound with TypedValue = None, so
                // resolveTypedExpr can never surface it as a lambda; read its
                // recorded interprocedural sign summary instead. There is no
                // conjugation side-channel, so a Hermitian input refuses here.
                ((match env.FuncSignParities.TryGetValue id with
                  | true, ps -> ps
                  | _ -> []), [], name)
            | _ -> ([], [], "the mapped element")
        arr.IndexTypes
        |> List.filter (fun ix -> ix.Rank > 1)
        |> List.tryPick (fun ix ->
            compactClassInheritError ix.Symmetry (orbitLevelsOf ix) 0 fName signParities conjCommutes)
    | _ -> None

/// Eta-expand a bare named-function reference used in KERNEL position:
///   lkm  ==>  lambda(__k0..__kn) -> lkm(__k0..__kn)
/// A top-level `function` is bound with TypedValue = None (bindVarSimple),
/// so resolveTypedExpr can never surface it as a TExprLambda -- hence
/// `method_for(...) <@> lkm` and `object_for(lkm)` never match a kernel arm.
/// This mirrors the prefix partial-application eta-expansion (the FuncElem
/// arm of the ExprApp case) but for the 0-args case that path deliberately
/// excludes, so the synthesized lambda rides the entire existing lambda
/// pipeline (captures, lifting, std::function emission, kernel wrappers).
/// Returns None when `kernelExpr` is not a bare function reference -- callers
/// then fall back to their ordinary `inferExpr env kernelExpr`. Gated to
/// kernel positions only, so bare function VALUES elsewhere are unaffected.
and etaExpandFunctionKernel (env: TypeEnv) (kernelExpr: Expr) : TypeResult<TypedExpr> option =
    match kernelExpr.Kind with
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
        let pname = sprintf "__k%d_0" uid
        let lamParams : LambdaParam list = [ { Name = pname; Type = None; Default = None; NameSpan = noSpan } ]
        let bodyApp =
            inheritSpan kernelExpr
                (ExprApp (kernelExpr, [ inheritSpan kernelExpr (ExprVar pname) ]))
        Some (inferLambda env lamParams None bodyApp)
    // A BINARY intrinsic in kernel position: `method_for(zip(Y, X)) <@> atan2`,
    // `object_for(log_base)`. Same construction as the unary arm one arity up;
    // arity is 2 by construction (isBinaryIntrinsic), which is exactly why the
    // binary names are kept OUT of isUnaryIntrinsic -- that predicate is read
    // as "arity 1" and would build a one-parameter wrapper here.
    | ExprKind.ExprVar name when isBinaryIntrinsic name && (lookupVar name env).IsNone ->
        let uid = env.Builder.FreshId()
        let pnames = [ sprintf "__k%d_0" uid; sprintf "__k%d_1" uid ]
        let lamParams : LambdaParam list =
            pnames |> List.map (fun n -> { Name = n; Type = None; Default = None; NameSpan = noSpan })
        let bodyApp =
            inheritSpan kernelExpr
                (ExprApp (kernelExpr, pnames |> List.map (fun n -> inheritSpan kernelExpr (ExprVar n))))
        Some (inferLambda env lamParams None bodyApp)
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
                         && not (paramTys |> List.exists (fun t -> match env.Subst.Resolve t with IRTPoly _ -> true | _ -> false)) ->
                let uid = env.Builder.FreshId()
                let names = paramTys |> List.mapi (fun i _ -> sprintf "__k%d_%d" uid i)
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
                    inferLambda env lamParams None bodyApp
                    |> Result.bind (fun tLam ->
                        // Pin the residual param types to the callee's declared
                        // ones: direct application keeps its looseness
                        // (no param-vs-arg unification), mirroring the prefix
                        // partial-application eta-expansion.
                        unify env.Subst tLam.Type (mkFuncArrow paramTys retTy)
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
                                match env.FuncCommGroups.TryGetValue name with
                                | true, cg when not (List.isEmpty cg) ->
                                    { li with CommGroups = cg; IsCommutative = true }
                                | _ -> li
                            // Same for `where anticomm(...)`: param indices carry
                            // over 1:1, and without this the clause is dropped by
                            // the eta wrapper and `object_for(f) <@> (A, A)` for a
                            // declared-antisymmetric function silently falls back
                            // to dense storage.
                            let withAntisym (li: TypedLambdaInfo) =
                                match env.FuncAntisymGroups.TryGetValue name with
                                | true, ag when not (List.isEmpty ag) ->
                                    { li with AntisymGroups = ag }
                                | _ -> li
                            let withParallel (li: TypedLambdaInfo) =
                                match env.FuncParallel.TryGetValue name with
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

/// Pick the zero literal for an element type. ETIndexRef _ requires looking up
/// the alias in env.TypeDefs: a string-valued EnumIdx needs LitString "" (the
/// empty string is the natural zero for std::string), an int-valued EnumIdx
/// uses LitInt 0L. ETString itself uses LitString "". Other types follow the
/// obvious pattern. This is consulted by every site that synthesizes a "zero
/// kernel" for a method_for / object_for with `<@> zero` -- the runtime value
/// is rarely meaningful semantically (no obvious string identity for fold),
/// but the literal kind must match the element type or codegen produces
/// invalid C++.
and zeroLitForElem (env: TypeEnv) (et: ElemType) : TypedExprKind =
    match et with
    | ETInt32 | ETInt64 -> TExprLit (LitInt 0L)
    | ETBool -> TExprLit (LitBool false)
    | ETString -> TExprLit (LitString "")
    | _ -> TExprLit (LitFloat 0.0)

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
        | _ -> Error (Other "reynolds() requires a lambda kernel, but the inner expression could not be resolved to a lambda")

    | TExprMethodFor mfInfo, TExprZero ->
        // M <@> zero: synthesize a lambda that returns 0 for each index point.
        // Infer element type from first array, default to Float64. Extract
        // the primitive scalar for the literal-choice match below;
        // non-primitive elem types (struct/named) fall through to Float64 --
        // this path semantically requires a primitive-typed array anyway,
        // since `zero` produces literal 0/false values.
        let elemTypeIR =
            mfInfo.ArrayTypes |> List.tryHead
            |> Option.map (fun at -> at.ElemType)
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
        // Create one parameter per array (all rank-0 element types)
        let nArrays = mfInfo.Arrays.Length
        let params_ = List.init nArrays (fun i ->
            let pid = env.Builder.FreshId()
            { Name = sprintf "__z%d" i; Type = paramTy; Index = i; VarId = pid; Default = None; NameSpan = noSpan })
        let lambdaInfo : TypedLambdaInfo = {
            Params = params_
            Body = mkTyped zeroLit paramTy
            ReturnType = paramTy
            CommGroups = []
            AntisymGroups = []
            SignParities = []
            Captures = []; IsCommutative = true
            Parallel = []  // synthesized (zero kernel): no user clause
            SelfBinding = None  // anonymous: cannot self-reference
        }
        let tZeroKernel = mkTyped (TExprLambda lambdaInfo) (IRTScalar elemType)
        buildApplyInfo env mfInfo.Arrays mfInfo.Identities mfInfo.ArrayTypes mfInfo.SDimsPerArray mfInfo.SharedIndexTypes lambdaInfo tLeft tZeroKernel false false

    // object_for(<combinator>) <@> (c1, c2, ...) -> left-fold or map+combine
    //
    // DELIBERATELY EXEMPT from the pack spine expansion the two arms below use
    // (docs/plan-tuples-vs-arg-packs.md 3.10). This is not a kernel pack at
    // all: the elements are COMPUTATIONS being folded, and
    // `object_for(<@>) <@> ((L1, f1), (L2, f2))` reads each element as a
    // (loop, kernel) PAIR -- deep-flattening would destroy exactly the nesting
    // this arm requires. It keeps the one-level shallow match on purpose.
    | TExprObjectFor objInfo, _ when
        (match objInfo.Kernel.Kind with TExprSection _ -> true | _ -> false) ->
        let op = match objInfo.Kernel.Kind with TExprSection op -> op | _ -> OpAdd
        // Extract elements from right side
        let elems = match rR.Kind with
                    | TExprTuple es -> es
                    | _ -> [tRight]
        if elems.Length < 2 then
            Error (Other "object_for(<combinator>) requires at least 2 arguments")
        else
            match op with
            | OpApply ->
                // object_for(<@>) <@> ((L1, f1), (L2, f2), ...)
                // Apply <@> to each (loop, kernel) pair, return tuple of computations
                let pairs = elems |> List.map (fun e ->
                    match e.Kind with
                    | TExprTuple [loop; kernel] -> Ok (loop, kernel)
                    | _ -> Error (Other "object_for(<@>) expects (loop, kernel) pairs"))
                pairs |> sequenceResults |> Result.bind (fun pairList ->
                    pairList |> List.map (fun (loop, kernel) -> inferApply env loop kernel)
                    |> sequenceResults
                    |> Result.map (fun computations ->
                        let types = computations |> List.map (fun c -> c.Type)
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
                            let outputType =
                                match f.Type, acc.Type with
                                | FuncElem (_, IRTScalar et), ArrayElem arr ->
                                    mkArrayLike { arr with ElemType = IRTScalar et }
                                | FuncElem (_, retTy), _ -> retTy
                                | _ -> acc.Type
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
    | TExprObjectFor objInfo, _ ->
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
        let hasZip = resolvedExprs |> List.exists (fun e ->
            match e.Kind with TExprZip _ -> true | _ -> false)

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
        if hasZip && (resolvedExprs |> List.exists (fun e ->
                        match e.Kind with TExprZip _ -> false | _ -> true)) then
            Error (Other zipInMultiArrayPackMsg)
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
                let arrTypes = arrays |> List.map (fun a ->
                    match a.Type with
                    | ArrayElem at -> reSDimOperand at
                    | _ -> { ElemType = IRTScalar ETFloat64; IndexTypes = []; IsVirtual = false; Identity = None })
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
            | TExprVar (name, _, _) -> AIDVariable name
            | _ -> AIDLiteral (env.Builder.FreshId()))
        // S1 SEAM 3 (docs/plan-kernel-body-materialization.md, M-B): the
        // operand-shape fallback just below degrades anything that is not
        // already an IRTArray to a RANK-0 record, which types the whole apply
        // scalar. A caret-shorthand `T^k` operand (`lambda(r: T^1) -> exp <@> r`)
        // is an arity-constrained var, not an IRTArray, so it landed there and
        // the nested map's result was typed a scalar. Supply the shape the
        // arity constraint already forces -- see materializeArityVar.
        //
        // LAMBDA BODIES ONLY. In a named function's body the same demand spends
        // the declaration's HM-polymorphic signature var: `function dbl(xs: T^1)
        // = xs <@> lambda(x) -> x + x |> compute` reaches this seam, and binding
        // `T` here collapsed dbl to its first call site's element type and
        // dropped the `_HM_` specializations from loops/115 and loops/116.
        if env.InLambdaBody then
            flatArrays |> List.iter (fun arr -> materializeArityVar env arr "map")
        let arrayTypes = flatArrays |> List.map (fun arr ->
            match env.Subst.Resolve arr.Type with
            | ArrayElem at -> reSDimOperand at
            | _ -> { ElemType = IRTScalar ETFloat64; IndexTypes = []; IsVirtual = false; Identity = None })
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
            | _ -> Error (Other "reynolds() requires a lambda kernel, but the inner expression could not be resolved to a lambda")
        | TExprZero ->
            // object_for(zero) <@> arrays: synthesize zero-returning lambda.
            // Extract primitive for literal choice.
            // Option C: preserve the wrapper (IRTIdxTagged/IRTUnitAnnotated)
            // in paramTy so the synthesized lambda's param type unifies with
            // the iteration's yielded type. Extract only the inner primitive
            // for zeroLitForElem.
            let elemTypeIR =
                arrayTypes |> List.tryHead
                |> Option.map (fun at -> at.ElemType)
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
                { Name = sprintf "__z%d" i; Type = paramTy; Index = i; VarId = pid; Default = None; NameSpan = noSpan })
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
        | TExprVar (fnName, _, _) when
            (match env.Subst.Resolve resolvedKernel.Type with
             | FuncElem (ps, _) ->
                 ps |> List.exists (fun t -> match env.Subst.Resolve t with IRTPoly _ -> true | _ -> false)
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
            let names = List.init n (fun i -> sprintf "__of%d_%d" uid i)
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
                            match env.FuncCommGroups.TryGetValue fnName with
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
                            match env.FuncParallel.TryGetValue fnName with
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
                        (match env.FuncCommGroups.TryGetValue fnName with
                         | true, cg when not (List.isEmpty cg) -> ()
                         | _ ->
                             match env.PackDeducedComm.TryGetValue fnName with
                             | true, (packName, Blade.Deduce.PInv) when n >= 2 ->
                                 let allSame =
                                     match identities with
                                     | [] -> false
                                     | first :: rest -> rest |> List.forall (fun i -> i = first)
                                 if allSame then
                                     let msg =
                                         sprintf
                                             "kernel `%s` deduces commutative over its argument pack `%s` (at every arity) and all %d positions receive the same array: output storage is DENSE today. Pin `where comm(%s)` on `%s` to opt into compact symmetric (triangular) storage."
                                             fnName packName n packName fnName
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
    | TExprCompose (OpComposeObj, _, _), _ ->
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
                    | TExprVar (name, _, _) -> AIDVariable name
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
            | TExprVar (name, _, _) -> sprintf "variable '%s'" name
            | _ -> k.GetType().Name.Replace("TExpr", "")
        match rL.Kind with
        | TExprMethodFor _ | TExprObjectFor _ ->
            Error (ChainOpBadKernel (describeKind rR.Kind))
        | _ ->
            Error (ChainOpUndecidable (describeKind rL.Kind, describeKind rR.Kind))

and buildApplyInfo (env: TypeEnv)
    (arrays: TypedExpr list) (identities: ArrayIdentity list)
    (arrayTypes: IRArrayType list) (sDimsPerArray: int list)
    (sharedIndexTypes: IRIndexType list)
    (lambdaInfo: TypedLambdaInfo)
    (tLoop: TypedExpr) (tKernel: TypedExpr)
    (isReynolds: bool) (isReynoldsAntisym: bool)
    : TypeResult<TypedExpr> =

    // ---- THE SPINE MATCHER (docs/plan-tuples-vs-arg-packs.md 6c, rule 2) ----
    // The operand pack arrives here as a SPINE: one node per top-level element,
    // with nested tuples still intact. This is where the schema and the spine
    // meet -- and it has to be here rather than at the former, because
    // `method_for(...)` is built before the kernel is known.
    //
    // Greedy, left to right:
    //   * an UNANNOTATED parameter takes one non-tuple node. Facing a tuple
    //     node it is an error demanding an annotation -- 5.2's rule one level
    //     out: the pack reading and the whole-tuple reading both type and
    //     disagree, and the body cannot vote (5.1).
    //   * a `Tuple<k>` parameter PREFERS one tuple node of top-level width k
    //     (direct bind), and otherwise consumes k consecutive non-tuple nodes
    //     (regroup). Preferring the node is what makes `(A, (B,C))` and
    //     `(A, B, C)` agree against `lambda(x, y: Tuple<2>)` -- the CONDITIONAL
    //     equivalence 6c restores, in place of 6b's unconditional one.
    //
    // Greedy is deterministic, and the other grouping is now spellable, because
    // structure survives: `(t1, a, b)` against `[y: Tuple<2>, z]` takes t1 for
    // y, and `((t1, a), b)` is how you say the other thing (rule 4).
    //
    // Whatever a `Tuple<k>` parameter binds, the LOOP still iterates k operands
    // -- a tuple of arrays has nothing to iterate as a unit -- so a directly
    // bound tuple node opens into its k components here, and the tuple is
    // rebuilt per iteration by the surface rewrite further down. Direct bind
    // and regroup therefore produce the SAME loop; what differs is which
    // spellings are legal, which is the whole point of the ruling.
    //
    // Runs ONLY when some node is tuple-typed. Every pack without one is
    // byte-identical to before, which is what keeps this off the hot path.
    let spineMatch : TypeResult<(TypedExpr list * ArrayIdentity list * IRArrayType list * int list) option> =
        if not (arrays |> List.exists (fun a -> (tupleNodeWidth env a).IsSome)) then Ok None
        else
            let widthOf (p: TypedParam) =
                match env.DeclaredTupleWidths.TryGetValue p.VarId with
                | true, w -> w
                | _ -> 1
            // A SYNTHESIZED parameter (`__`-prefixed: the eta wrappers built for
            // a named-function kernel, the Poly deferred former, sections, zero)
            // is not a name the user can annotate, so the annotation demand is
            // not addressed to anyone -- it gets its own message naming the
            // OPERAND instead.
            //
            // `Poly` reaches here having already counted TOP-LEVEL arity
            // (formalism.md:787), which is the property 6c restores: the pack
            // width the deferred former eta-expands to is the number of NODES,
            // so `(A, (B,C))` is arity 2 with a tuple second argument, not
            // arity 3. Passing that tuple to a `Poly` pack is 3.8's M9 and is
            // broken in every spelling, so it is refused here rather than
            // allowed to reach codegen as an undeclared temporary (measured:
            // `psum_arity_2_...(A____i0, __v18)` with `__v18` never declared).
            let rec walk (ps: TypedParam list) (ns: TypedExpr list)
                         (acc: TypedExpr list) : TypeResult<TypedExpr list> =
                match ps, ns with
                | [], [] -> Ok (List.rev acc)
                | [], _ -> Ok (List.rev acc @ ns)   // arity is settled downstream
                | _, [] -> Ok (List.rev acc)        // ditto (defaults may fill)
                | p :: pt, n :: nt ->
                    let w = widthOf p
                    // An operator SECTION's two params are synthesized as plain
                    // `a`/`b` rather than `__`-prefixed, so it needs its own
                    // test: `(+)` has no parameter list to annotate either.
                    let synthetic =
                        p.Name.StartsWith "__"
                        || (match tKernel.Kind with TExprSection _ -> true | _ -> false)
                    match tupleNodeWidth env n, w with
                    | Some tw, 1 when not synthetic ->
                        Error (KernelPackArity
                                 (sprintf "kernel parameter '%s' is unannotated but the operand in that position is a %d-tuple. Tuple-ness is never inferred: annotate the parameter `Tuple<%d>` to take the group as one tuple, or write %d parameters and pass the components separately."
                                          p.Name tw tw tw))
                    | Some tw, 1 ->
                        Error (KernelPackArity
                                 (sprintf "this kernel takes the %d-tuple in operand position %d as ONE argument (its parameter list counts top-level operands), and a tuple of arrays has nothing to iterate. Pass the components as separate operands, or use a kernel whose parameter in that position is annotated `Tuple<%d>`."
                                          tw (arrays.Length - nt.Length) tw))
                    | Some tw, k when tw = k ->
                        // DIRECT BIND. Opened into components for the loop; the
                        // tuple is rebuilt at kernel entry by the surface rewrite.
                        (match tupleNodeParts env n with
                         | Some parts -> walk pt nt (List.rev parts @ acc)
                         | None ->
                             Error (KernelPackArity
                                      (sprintf "kernel parameter '%s' is declared `Tuple<%d>` and the operand in that position is a %d-tuple VALUE whose components cannot be named (a fused or computed tuple). Bind the components to their own names and pass them as separate operands."
                                               p.Name k tw)))
                    | Some tw, k ->
                        Error (KernelPackArity
                                 (sprintf "kernel parameter '%s' is declared `Tuple<%d>` but the operand in that position is a %d-tuple. Widths are matched at the TOP LEVEL only -- a `Tuple<%d>` cannot re-count across nesting. Write the operands flat, or change the annotation to `Tuple<%d>`."
                                          p.Name k tw k tw))
                    | None, 1 -> walk pt nt (n :: acc)
                    | None, k ->
                        // REGROUP: k consecutive nodes, none of which may itself
                        // be a tuple (that would be the cross-level recount).
                        let avail = n :: nt
                        let taken = avail |> List.truncate k
                        if taken.Length < k || (taken |> List.exists (fun t -> (tupleNodeWidth env t).IsSome)) then
                            Error (KernelPackArity
                                     (sprintf "kernel parameter '%s' is declared `Tuple<%d>` and cannot be filled from this position: the operands here are neither one %d-tuple nor %d plain operands. Parenthesize the grouping you mean -- matching is greedy left to right, so `((x, y), z)` and `(x, (y, z))` are different packs."
                                              p.Name k k k))
                        else
                            walk pt (avail |> List.skip k) (List.rev taken @ acc)
            walk lambdaInfo.Params arrays []
            |> Result.map (fun expanded ->
                if expanded.Length = arrays.Length then None
                else
                    let ids =
                        expanded |> List.map (fun (ta: TypedExpr) ->
                            match ta.Kind with
                            | TExprVar (name, _, _) -> AIDVariable name
                            | _ -> AIDLiteral (env.Builder.FreshId()))
                    let ats =
                        expanded |> List.map (fun (ta: TypedExpr) ->
                            match ta.Type with
                            | ArrayElem at -> at
                            | _ -> { ElemType = IRTScalar ETFloat64; IndexTypes = []; IsVirtual = false; Identity = None })
                    Some (expanded, ids, ats, computeSDimsPerArray ats))

    match spineMatch with
    | Error e -> Error e
    | Ok spineExpansion ->
    let (arrays, identities, arrayTypes, sDimsPerArray) =
        match spineExpansion with
        | Some (a, i, at, sd) -> (a, i, at, sd)
        | None -> (arrays, identities, arrayTypes, sDimsPerArray)

    // DEFAULTED TRAILING KERNEL PARAMS: when the kernel declares more params
    // than the sources supply rows and every EXCESS trailing param carries a
    // default, the apply is a defaults fill -- the omitted params become
    // body-entry lets over their (declaration-typed) default exprs, so each
    // kernel invocation evaluates them at entry with the row params bound:
    //   method_for(A) <@> lambda(x, s = 2.0) -> x * s
    //     ==  method_for(A) <@> lambda(x) -> { let s = 2.0; x * s }
    // The let reuses the param's own VarId, so body references resolve
    // unchanged. Done FIRST so every downstream consumer (deduction, ranks,
    // param unification, grouping) sees the shrunk, ordinary kernel. The
    // co-iteration INDEX-PARAM form (params = arrays + shared index slots)
    // is checked first and wins -- its trailing params are index receivers,
    // not omitted defaults.
    let lambdaInfo =
        let expectedRows =
            arrayTypes |> List.sumBy (fun (at: IRArrayType) ->
                if at.IsVirtual then at.IndexTypes |> List.sumBy (fun idx -> idx.Rank) else 1)
        let idxParamCount = sharedIndexTypes |> List.sumBy (fun r -> r.Rank)
        let coIterIndexForm =
            not sharedIndexTypes.IsEmpty
            && idxParamCount > 0
            && not (arrayTypes |> List.exists (fun at -> at.IsVirtual))
            && lambdaInfo.Params.Length = arrays.Length + idxParamCount
        // WIDTH-AWARE prefix (docs/plan-tuples-vs-arg-packs.md 6c, 5.3's
        // "defaulted kernel params" seam): the satisfied prefix is the one
        // whose WIDTHS sum to expectedRows, not its first `expectedRows`
        // entries -- `lambda(x, y: Tuple<2>, s = 1.0)` over 3 rows keeps two
        // params, not three. With every width 1 this is `expectedRows`
        // exactly, so the ordinary case is unchanged.
        let widthOf (p: TypedParam) =
            match env.DeclaredTupleWidths.TryGetValue p.VarId with
            | true, w -> w
            | _ -> 1
        let satisfiedPrefix =
            let mutable acc = 0
            let mutable n = 0
            for p in lambdaInfo.Params do
                if acc < expectedRows then
                    acc <- acc + widthOf p
                    n <- n + 1
            if acc = expectedRows then Some n else None
        if not coIterIndexForm
           && expectedRows > 0
           && (match satisfiedPrefix with
               | Some n -> n < lambdaInfo.Params.Length
                           && (lambdaInfo.Params |> List.skip n |> List.forall (fun p -> p.Default.IsSome))
               | None -> false) then
            let n = Option.get satisfiedPrefix
            let kept = lambdaInfo.Params |> List.truncate n
            let dropped = lambdaInfo.Params |> List.skip n
            let newBody =
                (dropped, lambdaInfo.Body)
                ||> List.foldBack (fun p body ->
                    { Kind = TExprLet (p.Name, p.VarId, Option.get p.Default, body)
                      Type = body.Type
                      Span = body.Span })
            { lambdaInfo with Params = kept; Body = newBody }
        else lambdaInfo

    // ---- TUPLE PARAMS: the schema's re-nesting half ----
    // docs/plan-tuples-vs-arg-packs.md 6c. A `Tuple<k>` parameter consumes k
    // consecutive leaves and receives them AS A TUPLE. It is realized here, in
    // the surface, by the same body-entry-let mechanism the defaults fill
    // above uses: the tuple param is replaced by k fresh row params and the
    // body opens with `let p = (__tp_0, ..., __tp_{k-1})`, reusing p's own
    // VarId so every body reference (`p[0]`) resolves unchanged.
    //
    // Doing it as a SURFACE rewrite rather than in codegen is what keeps this
    // ~30 lines: after it, the schema is all-width-1, so ranks, deduction,
    // grouping, unification, lowering and emission are the flattened path they
    // already were, and `t[i]` is the existing TExprTupleIndex -> std::get<i>.
    // It is the Poly whole-pack arm's move (2.5: eta-expand to the pack width,
    // re-absorb inside) at a width the annotation makes static. The
    // std::make_tuple the let builds is a register shuffle every optimizer
    // folds away -- and it is the ONLY shape that needs no new IR node.
    //
    // The synthesized params take p's OWN element types (its annotation
    // lowered to `IRTTuple [a1..ak]`), so unifying them against the rows
    // downstream also pins the annotation's element slots -- the tuple the let
    // builds then has exactly p's declared type, with nothing left to reconcile.
    let declaredParamCount = lambdaInfo.Params.Length
    let declaredParamWidths =
        lambdaInfo.Params |> List.map (fun p ->
            match env.DeclaredTupleWidths.TryGetValue p.VarId with
            | true, w -> w
            | _ -> 1)
    let declaredTotalWidth = List.sum declaredParamWidths
    // A `where` clause on a kernel that also declares a tuple parameter splits
    // by CONJUNCT CLASS, because the two classes address parameters in
    // different ways and only one of them survives the expansion below.
    //
    //   * `comm` / `anticomm` are POSITIONAL index lists (CommGroups /
    //     AntisymGroups). The expansion renumbers positions, so a surviving
    //     group would land on the wrong slot -- silently, since a misplaced
    //     comm group degrades to dense storage with no diagnostic. Worse, there
    //     is nothing settled to remap TO: "comm(p, q)" between two PAIRS has no
    //     agreed meaning. REFUSED.
    //
    //   * The PARALLEL STRATEGIES (`WhereClause.Parallel`: `omp`, `cuda`,
    //     `mpi`, and any future member of `ParallelStrategy`) are resolved BY
    //     NAME against the operand a parameter contributes, and under one-level
    //     structural matching (docs/plan-tuples-vs-arg-packs.md 6c) a `Tuple<k>`
    //     parameter is ONE schema node -- so the name still designates a
    //     well-defined unit of the nest after expansion. ALLOWED, with the
    //     licence rewritten onto the synthesized row params by
    //     `remapTupleParallel` below. Strategies carrying no variable names
    //     (`cuda`, `mpi`) are unaffected by the renaming and pass through.
    //
    // The split is on the CLASS, not on a per-strategy allowlist, so a new
    // `ParallelStrategy` case inherits the permitted behaviour automatically.
    let tupleParamWhereClash =
        if declaredTotalWidth = declaredParamCount then None
        elif not (List.isEmpty lambdaInfo.CommGroups)
             || not (List.isEmpty lambdaInfo.AntisymGroups) then
            Some (Other "a `comm`/`anticomm` clause on a kernel that also declares a `Tuple<N>` parameter is not supported: those clauses address parameters by POSITION, and a tuple parameter is expanded into its k row parameters (renumbering every later position). `comm` between two PAIRS has no settled meaning to remap to. Write the parameters flat (one per operand) to use `comm`/`anticomm` here. (The parallel strategies -- `omp`/`cuda`/`mpi` -- ARE supported on a tuple parameter: they resolve by name to the operand node.)")
        else None
    match tupleParamWhereClash with
    | Some e -> Error e
    | None ->
    let lambdaInfo =
        if declaredTotalWidth = declaredParamCount then lambdaInfo
        else
            let uid = env.Builder.FreshId()
            let mutable nextIdx = 0
            let mutable wraps : (TypedParam * TypedParam list) list = []
            let newParams =
                List.map2 (fun (p: TypedParam) w ->
                    if w = 1 then
                        let p' = { p with Index = nextIdx }
                        nextIdx <- nextIdx + 1
                        [p']
                    else
                        let elemTys =
                            match env.Subst.Resolve p.Type with
                            | IRTTuple ts when ts.Length = w -> ts
                            | _ -> List.init w (fun _ -> env.Subst.Fresh())
                        let subs =
                            elemTys |> List.map (fun ty ->
                                let sp : TypedParam =
                                    { Name = sprintf "__tp%d_%s_%d" uid p.Name nextIdx
                                      Type = ty; Index = nextIdx
                                      VarId = env.Builder.FreshId()
                                      Default = None; NameSpan = p.NameSpan }
                                nextIdx <- nextIdx + 1
                                sp)
                        wraps <- wraps @ [(p, subs)]
                        subs) lambdaInfo.Params declaredParamWidths
                |> List.concat
            let newBody =
                (wraps, lambdaInfo.Body)
                ||> List.foldBack (fun (p, subs) body ->
                    let tupleTy = IRTTuple (subs |> List.map (fun sp -> sp.Type))
                    let tupleExpr =
                        { Kind = TExprTuple (subs |> List.map (fun sp ->
                                    { Kind = TExprVar (sp.Name, sp.VarId, None)
                                      Type = sp.Type; Span = body.Span }))
                          Type = tupleTy; Span = body.Span }
                    { Kind = TExprLet (p.Name, p.VarId, tupleExpr, body)
                      Type = body.Type; Span = body.Span })
            // PARALLEL LICENCE carry-over across the expansion. `omp(p: n)`
            // names the tuple parameter, which no longer exists in `newParams`
            // -- Lowering.extractParallelism resolves omp vars by NAME against
            // the callable's params, so leaving it alone would drop the whole
            // licence and emit a serial nest whose C++ is indistinguishable
            // from a program that never asked (the failure the refusal above
            // used to prevent by rejecting the program).
            //
            // A `Tuple<k>` parameter is one schema NODE, and the depth counts
            // levels OF THAT NODE, outermost first -- the same rule a rank-k
            // array parameter follows (`omp(a: 2)` on `a: T^2`). The node's
            // levels are its k rows in order, so `omp(p: n)` licenses its first
            // n rows: row j keeps the residual budget `n - j`, and rows past
            // the budget are dropped rather than licensed. `omp(p: 1)` on a
            // 2-tuple therefore threads one level, not two -- the co-iterated
            // tuple contributes ONE licensed axis, exactly as written.
            //
            // Conservative where a row is itself multi-rank: row j is offered
            // `n - j` levels when a strict level count would offer
            // `n - (levels of rows 0..j-1)`. Under-licensing is the safe
            // direction for a cap, and rows of a co-iterated tuple are rank-1
            // in every spelling that exists today.
            // Snapshot before the closure: `wraps` is a mutable local.
            let wrapsFinal = wraps
            let remapTupleParallel (strategies: ParallelStrategy list) =
                if List.isEmpty wrapsFinal then strategies
                else
                    let subNamesOf (n: string) =
                        wrapsFinal |> List.tryPick (fun ((p: TypedParam), subs) ->
                            if p.Name = n then Some (subs |> List.map (fun (s: TypedParam) -> s.Name))
                            else None)
                    strategies |> List.map (function
                        | Omp o ->
                            Omp { o with
                                    Vars =
                                      o.Vars |> List.collect (fun (n, dims) ->
                                        match subNamesOf n with
                                        | Some subs ->
                                            subs
                                            |> List.mapi (fun j s -> (s, dims - j))
                                            |> List.filter (fun (_, d) -> d > 0)
                                        | None -> [(n, dims)]) }
                        // `cuda` / `mpi` carry no variable names: nothing to remap.
                        | s -> s)
            { lambdaInfo with Params = newParams; Body = newBody
                              Parallel = remapTupleParallel lambdaInfo.Parallel }

    let commGroups = lambdaInfo.CommGroups
    // Declared `where anticomm(...)` positions. Same axis grouping and the
    // same iteration license as comm (the exchange is licensed either way --
    // only the SIGN and the diagonal differ), so `iterGroups` is what every
    // grouping consumer sees. `antisymStorageGroups` is the narrower list that
    // decides the licensed simplex is the STRICT one (AntisymIdx, no
    // diagonal): under `reynolds` the wrapper owns the output symmetry, so a
    // declared clause there degrades to an iteration license exactly as `comm`
    // does on a reynolds-wrapped kernel (index-types/050, symmetry/019).
    let iterGroups = commGroups @ lambdaInfo.AntisymGroups
    let antisymStorageGroups = if isReynolds then [] else lambdaInfo.AntisymGroups

    // ---- Stage 3: symmetry deduction (early tier), at the ONE seam every
    // apply arm funnels through. Deduce the kernel's adjacent-pair swap
    // parity; an eta-expanded named-function wrapper (body = f(p0..pn-1) in
    // order) defers to the function's recorded summary. Under reynolds a
    // `comm` clause is an ITERATION LICENSE over the signed permutation sum,
    // not a claim about the bare kernel -- validation and suggestions both
    // stand down (isReynolds).
    let (stage3Names, stage3Pairs) =
        if isReynolds || lambdaInfo.Params.Length < 2 then ([], [])
        else
            let etaSummary =
                match lambdaInfo.Body.Kind with
                | TExprApp ({ Kind = TExprVar (fname, _, _) }, args)
                        when args.Length = lambdaInfo.Params.Length
                             && List.forall2 (fun (arg: TypedExpr) (p: TypedParam) ->
                                    match arg.Kind with
                                    | TExprVar (_, aid, _) -> aid = p.VarId
                                    | _ -> false) args lambdaInfo.Params ->
                    (match env.FuncDeducedPairs.TryGetValue fname with
                     | true, (names, ps) when ps.Length = lambdaInfo.Params.Length - 1 ->
                         Some (names, ps)
                     | _ ->
                         // Fixed-arity wrapper over a PACK-summarized kernel
                         // (`lambda(x, y) -> comoment(x, y)`): invariance at
                         // every arity specializes to full pairwise symmetry
                         // at this one. Suggestion names the LAMBDA's own
                         // params -- that is where this spelling pins.
                         (match env.PackDeducedComm.TryGetValue fname with
                          | true, (_, Blade.Deduce.PInv) ->
                              Some (lambdaInfo.Params |> List.map (fun p -> p.Name),
                                    List.replicate (lambdaInfo.Params.Length - 1) Blade.Deduce.PInv)
                          | _ -> None))
                | _ -> None
            match etaSummary with
            | Some (names, ps) -> (names, ps)
            | None ->
                // Same sign-linearity resolver as checkFunctionDecl: a lambda
                // kernel calling an already-summarized helper (`lambda(x, y) ->
                // mymean(x - y)`) gets the callee's per-parameter sign law.
                let signResolver (calleeId: IRId) =
                    match env.FuncSignParities.TryGetValue calleeId with
                    | true, ps -> Some ps
                    | _ -> None
                (lambdaInfo.Params |> List.map (fun p -> p.Name),
                 Blade.Deduce.deduceAdjacentPairs signResolver lambdaInfo.Params lambdaInfo.Body)
    // RECORDING ONLY (channel (f)): the kernel's proved adjacent-pair
    // parities, so the editor can distinguish "you declared this" from "the
    // checker proved it". Deliberately BROADER than the confirm-and-pin
    // suggestion below, which additionally requires the same array in both
    // positions -- that extra condition is about the STORAGE decision, while
    // provenance is worth surfacing whether or not compaction is on the table.
    // Synthesized `__`-prefixed wrapper params are filtered for the same reason
    // the suggestion filters them: they are not names a user can see.
    if List.isEmpty iterGroups then
        List.indexed stage3Pairs
        |> List.iter (fun (i, par) ->
            if (par = Blade.Deduce.PInv || par = Blade.Deduce.PNeg)
               && i + 1 < stage3Names.Length
               && not (stage3Names.[i].StartsWith "__")
               && not (stage3Names.[i + 1].StartsWith "__") then
                Blade.TypeEnv.DeducedFacts.add
                    (Blade.TypeEnv.DeducedPairSym
                        ("<kernel>", stage3Names.[i], stage3Names.[i + 1], i,
                         par = Blade.Deduce.PNeg))
                    (if tKernel.Span = noSpan then tLoop.Span else tKernel.Span))

    // IDE: record this kernel's deduction snapshot, span-keyed (`ide check
    // --json` kernels[]). Unconditional -- declared and reynolds kernels
    // record too (parities are empty under reynolds by construction). Skip
    // synthesized eta-wrappers over named functions (`__k...`/`__of...`
    // params): the named function's own binding entry covers those with
    // names a user can actually write.
    (let kParams = lambdaInfo.Params |> List.map (fun p -> p.Name)
     if not (kParams |> List.exists (fun n -> n.StartsWith "__")) then
        let renderGroups kw (groups: int list list) =
            groups |> List.choose (fun g ->
                let names = g |> List.choose (fun i ->
                    if i >= 0 && i < kParams.Length then Some kParams.[i] else None)
                if List.isEmpty names then None
                else Some (sprintf "%s(%s)" kw (String.concat ", " names)))
        IdeDeductions.addKernel {
            KSpan = (if tKernel.Span = noSpan then tLoop.Span else tKernel.Span)
            KParams = kParams
            KParities = stage3Pairs
            KDeclared = renderGroups "comm" lambdaInfo.CommGroups
                        @ renderGroups "anticomm" lambdaInfo.AntisymGroups
            KRanks = lambdaInfo.Params |> List.map (fun p ->
                let rank =
                    match env.Subst.Resolve p.Type with
                    | ArrayElem arr -> arr.IndexTypes.Length
                    | _ -> 0
                (p.Name, rank)) })
    // Declared comm + provably antisymmetric body = the silent-corruption
    // case: triangular storage would drop the sign flips. Hard error;
    // PBottom stays trusted (status quo escape hatch). The mirror case --
    // declared anticomm + provably COMMUTATIVE body -- is the same error with
    // the signs exchanged: strict-simplex storage would drop the diagonal and
    // return the negation for half the reads. (Both stand down under
    // reynolds, where stage3Pairs is empty by construction.)
    let contradictsIn (groups: int list list) (wanted: Blade.Deduce.Parity)
                      (mk: string -> string -> TypeError) =
        if List.isEmpty stage3Pairs || List.isEmpty groups then None
        else
            List.indexed stage3Pairs
            |> List.tryPick (fun (i, par) ->
                if par = wanted
                   && groups |> List.exists (fun g ->
                          List.contains i g && List.contains (i + 1) g) then
                    Some (mk stage3Names.[i] stage3Names.[i + 1])
                else None)
    let stage3Err =
        match contradictsIn commGroups Blade.Deduce.PNeg
                            (fun a b -> CommContradictsBody (a, b)) with
        | Some e -> Some e
        | None ->
            contradictsIn lambdaInfo.AntisymGroups Blade.Deduce.PInv
                          (fun a b -> AntisymmContradictsBody (a, b))
    match stage3Err with
    | Some e -> Error e
    | None ->
    // Confirm-and-pin suggestion: kernel provably commutative (or provably
    // ANTI-commutative) in an adjacent pair, nothing declared for that pair,
    // and the SAME array occupies both positions (H  intersect  Stab would license
    // compaction). Output stays DENSE until the user pins -- the suggestion is
    // the compiler proposing, never deciding. PInv proposes the inclusive
    // triangle (`comm`), PNeg the strict one (`anticomm`, zero diagonal).
    //
    // OUTER PRODUCTS ONLY. The suggestion's whole content is "your output is a
    // square with a redundant half; pin the symmetry and store the triangle",
    // and that presupposes the two operand slots span two SEPARATE axes. A
    // ZIPPED apply co-iterates: `zip(A, A) <@> lambda(x, y) -> x * y` walks ONE
    // axis feeding both slots, so the output is rank-1 and there is no triangle
    // in existence to compact (pinning `where comm` on it changes nothing --
    // sql-reduce/017). `sharedIndexTypes` is non-empty exactly for the zipped
    // arms (both inferMethodFor zip shapes via zipSharedRecords, and
    // inferObjectFor's `hasZip` split); every outer-product former passes `[]`,
    // so this gate cannot reach a real square. Note `A * A` desugars to a zip
    // too, and is likewise co-iteration, not a suppressed true positive.
    let isCoIterApply = not (List.isEmpty sharedIndexTypes)
    if List.isEmpty iterGroups && not (List.isEmpty stage3Pairs) && not isCoIterApply then
        List.indexed stage3Pairs
        |> List.iter (fun (i, par) ->
            if (par = Blade.Deduce.PInv || par = Blade.Deduce.PNeg)
               && i + 1 < identities.Length
               && identities.[i] = identities.[i + 1]
               // Synthesized wrapper params (`__of13_0`, `__k13_0`) are not
               // names a user can write in a pin -- suppress; the pack-level
               // suggestion at the deferred-former seam names the REAL pack
               // param for exactly these kernels (and under --strict-pins an
               // unactionable suggestion would be an unfixable build break).
               && not (stage3Names.[i].StartsWith "__")
               && not (stage3Names.[i + 1].StartsWith "__") then
                let (n1, n2) = (stage3Names.[i], stage3Names.[i + 1])
                let msg =
                    if par = Blade.Deduce.PInv then
                        sprintf
                            "kernel deduces commutative in (%s, %s) and both positions receive the same array: output storage is DENSE today. Pin `where comm(%s, %s)` on the kernel to opt into compact symmetric (triangular) storage."
                            n1 n2 n1 n2
                    else
                        sprintf
                            "kernel deduces ANTIcommutative in (%s, %s) (f(%s, %s) = -f(%s, %s)) and both positions receive the same array: output storage is DENSE today. Pin `where anticomm(%s, %s)` on the kernel to opt into compact anticommutative (strict-triangular, zero-diagonal) storage."
                            n1 n2 n2 n1 n1 n2 n1 n2
                // Synthesized kernels (eta wrappers, sections) carry noSpan;
                // fall back to the former expression's source span so the
                // ghost annotation lands on `object_for(f)` / `method_for(..)`.
                // Hoisted above the warning so BOTH channels carry one span.
                let span = if tKernel.Span = noSpan then tLoop.Span else tKernel.Span
                emitWarning env "BL4010" span msg
                PinSuggestions.add msg span)

    // Dropped-parallel-clause guard. This is the apply seam -- the ONE place a
    // loop and a kernel meet -- so it is where "the kernel declared `omp(...)`"
    // and "the lambda reaching the loop carries it" can be compared at all.
    //
    // A named function used in kernel position is eta-expanded into a wrapper
    // lambda built with NO where-clause, so its `Parallel` starts empty and the
    // callee's clause has to be surfaced explicitly (etaExpandFunctionKernel /
    // the deferred-former eta). When that surfacing is missing the clause
    // vanishes with no other trace: `Parallel = []` becomes IsOmpParallel =
    // false, the nest is emitted serial, and the generated C++ is identical to
    // a program that never asked. That was a real, long-lived silent bug. This
    // is its regression guard -- it should never fire, and firing means some
    // apply path grew a wrapper that forgets the clause again.
    (if List.isEmpty lambdaInfo.Parallel then
        match lambdaInfo.Body.Kind with
        | TExprApp ({ Kind = TExprVar (fname, _, _) }, args)
                when args.Length = lambdaInfo.Params.Length
                     && List.forall2 (fun (arg: TypedExpr) (p: TypedParam) ->
                            match arg.Kind with
                            | TExprVar (_, aid, _) -> aid = p.VarId
                            | _ -> false) args lambdaInfo.Params ->
            (match env.FuncParallel.TryGetValue fname with
             | true, (_, strategies) when not (List.isEmpty strategies) ->
                 // Two different situations share this shape, and only one of
                 // them is a compiler bug. A SYNTHESIZED eta wrapper's params
                 // are all `__`-prefixed (`__k<uid>_<i>` / `__of<uid>_<i>`);
                 // reaching here means a surfacing site forgot the clause. A
                 // wrapper the USER wrote (`lambda(x, y) -> cov(x, y)`) has
                 // ordinary names, and its empty `Parallel` is just an
                 // unwritten clause -- blaming the compiler for that sends the
                 // reader to a bug report instead of to their own where-clause.
                 let synthesized =
                     lambdaInfo.Params |> List.forall (fun p -> p.Name.StartsWith "__")
                 let span = if tKernel.Span = noSpan then tLoop.Span else tKernel.Span
                 if synthesized then
                     // BL9001 -- it says "this is a compiler bug" in so many
                     // words, so it renders under the internal-compiler-error code.
                     emitWarning env "BL9001" span
                         (sprintf "internal: `where` parallel clause on '%s' was dropped by kernel-position eta-expansion -- the loop nest will be emitted serial. This is a compiler bug, not a source error; please report it."
                                  fname)
                 else
                     // BL4001 -- a source-level fact, same class as the other
                     // "this clause licenses nothing here" warnings. The clause
                     // on '%s' licenses a nest built around '%s' ITSELF; this
                     // nest is built around the LAMBDA, which declared none.
                     emitWarning env "BL4001" span
                         (sprintf "the `where` parallel clause on '%s' does not carry through a hand-written wrapper lambda: this nest's kernel is the lambda, and it declares none, so the nest is emitted SERIAL. Write the clause on the lambda (`lambda(..) where omp(..) -> %s(..)`), or drop the wrapper and use '%s' as the kernel directly."
                                  fname fname fname)
             | _ -> ())
        | _ -> ())

    // Co-iteration INDEX-PARAM form: a co-iterated kernel may declare
    // N + R parameters -- one value per co-iterated array plus the R shared
    // iteration indices -- e.g. `for (uq, ph) in range<Y, X> <@>
    // lambda(zu, zp, i, j) -> ...`. The indices ride as a TRAILING synthetic
    // range<...> operand over the shared records: expandedRows then expands
    // it to one tagged Nat<...> param per slot (unifying + tag-checking the
    // index params exactly like an explicit range<> source), and the loop
    // builder's VirtualRange elements bind them to the loop indices. The
    // virtual operand is appended LAST so the value params keep their
    // positions. Values-only kernels (arity N) are untouched, as is every
    // non-co-iteration apply.
    let (arrays, identities, arrayTypes, sDimsPerArray) =
        let idxParamCount = sharedIndexTypes |> List.sumBy (fun r -> r.Rank)
        let alreadyVirtual = arrayTypes |> List.exists (fun at -> at.IsVirtual)
        if not sharedIndexTypes.IsEmpty
           && idxParamCount > 0
           && not alreadyVirtual
           && lambdaInfo.Params.Length = arrays.Length + idxParamCount then
            let elemT =
                match List.tryLast sharedIndexTypes with
                | Some i -> elemTypeForIterationIndex i
                | None -> IRTScalar ETInt64
            let vExpr = mkTyped (TExprRange sharedIndexTypes) (mkVirtualArrayArrow sharedIndexTypes elemT)
            let vAt =
                match vExpr.Type with
                | ArrayElem at -> at
                | _ -> { ElemType = elemT; IndexTypes = sharedIndexTypes; IsVirtual = true; Identity = None }
            (arrays @ [vExpr],
             identities @ [AIDLiteral (env.Builder.FreshId())],
             arrayTypes @ [vAt],
             sDimsPerArray @ [idxParamCount])
        else (arrays, identities, arrayTypes, sDimsPerArray)

    // Resolve param types through Subst before reading rank. A kernel
    // param may have started as IRTInfer at lambda
    // creation but been refined during the kernel body's typecheck (e.g.,
    // reduce's kernel-arg deduction synthesizes a rank-1 IRTArray binding).
    // If we read p.Type directly here, we'd see the stale IRTInfer and
    // compute kRank = 0, which makes perRowType think the kernel takes a
    // scalar -- leading to a shape mismatch when we later unify against
    // the real rank-N source per-row type.
    //
    // When the param's resolved type is
    // STILL IRTInfer after body typechecking -- i.e., the body didn't
    // structurally constrain the param's shape (e.g. `lambda(g) -> g(0)`,
    // where g(0) is ambiguous between array indexing and function
    // application) -- fall back to the array-side rank: the kernel sees
    // a slice of rank (array rank - iterated S-dimensions). For a
    // typical `method_for(arr)` with one iterated outer dim, kRank
    // becomes (arrTy.rank - 1). This recovers the array shape
    // information that the body alone couldn't supply, and lets the
    // subsequent perRowType unification pin the param to the correct
    // Array<T, N> type rather than collapsing to the scalar element.
    let resolvedParamTypes =
        lambdaInfo.Params |> List.map (fun p -> env.Subst.Resolve p.Type)
    // Param rank: if the param's resolved type is an array, read its
    // rank directly. If the param is still IRTInfer -- meaning the body
    // didn't structurally constrain the param's shape (e.g.
    // `lambda(g) -> g(0)`, where the application is ambiguous between
    // array indexing and function application) -- fall back to the
    // array-side rank.
    //
    // The array-side rank isn't naively `(array rank - iterated
    // S-dimensions)`. For ragged-inner-dim arrays (group_by results,
    // ragged literals, depidx-inner shapes), `computeSDimsPerArray`
    // counts every SDimension equally -- the inner ragged dim included.
    // But codegen's ragged-peel pass treats those inner dims as
    // KERNEL-SIDE (peeled into a per-row binding), not iterated. To
    // match codegen semantics here, we adjust: every index with a
    // ragged-family tag contributes to the kernel's rank, not to the
    // iterated count. The result is the kernel's effective rank as
    // codegen actually sees it.
    let isRaggedInnerKind (k: IxKind) : bool =
        isRaggedRowKind k || k = IxKErrorRaggedNoPrior
    // Does the kernel body use parameter `pname` AS AN ARRAY (indexed, applied,
    // or passed to reduce/extents/rank/arity)? Distinguishes a CONSUMING
    // kernel (`lambda(g) -> reduce(g, ...)`, param is a sub-array along a
    // ragged inner dim) from an ELEMENTWISE kernel (`lambda(e) -> e * 2.0`,
    // param is a scalar). Can't rely on the param's resolved scalar type: mixed
    // int/float arithmetic legitimately leaves an untyped scalar param
    // flexible (`i * 2.0` stays Int64, promoted, not unifiable to Float64).
    // The structural use is the reliable signal.
    let paramUsedAsArray (pname: string) (body: TypedExpr) : bool =
        let isParamVar (e: TypedExpr) =
            match e.Kind with TExprVar (n, _, _) -> n = pname | _ -> false
        let rec walk (e: TypedExpr) : bool =
            let here =
                match e.Kind with
                | TExprIndex (arr, _, _) when isParamVar arr -> true
                | TExprApp (f, _) when isParamVar f -> true            // g(0) as application
                | TExprReduce (arr, _, _) when isParamVar arr -> true
                | TExprProdSum args when List.exists isParamVar args -> true
                | TExprExtents arr when isParamVar arr -> true
                | TExprRank arr when isParamVar arr -> true
                | TExprArity n when n = pname -> true
                | _ -> false
            here || childrenAny e
        and childrenAny (e: TypedExpr) : bool =
            match e.Kind with
            | TExprBinOp (_, _, l, r) -> walk l || walk r
            | TExprUnaryOp (_, x) -> walk x
            | TExprApp (f, args) -> walk f || List.exists walk args
            | TExprIndex (a, idxs, _) -> walk a || List.exists walk idxs
            | TExprReduce (a, k, i) ->
                walk a || walk k || (match i with Some e -> walk e | None -> false)
            | TExprProdSum args -> List.exists walk args
            | TExprExtents a | TExprRank a | TExprArrayNegate a
            | TExprArrayConjugate a | TExprUnique a -> walk a
            | TExprMask (a, p) -> walk a || walk p
            | TExprCompound (a, p) | TExprSparse (a, p) -> walk a || walk p
            | TExprSort (a, k) -> walk a || walk k
            | TExprIf (c, t, e2) -> walk c || walk t || walk e2
            | TExprBlock (_, Some fe) -> walk fe
            | TExprSequence es -> List.exists walk es
            | TExprTuple es -> List.exists walk es
            | TExprLet (_, _, v, b) -> walk v || walk b
            | _ -> false
        walk body
    // When the param is passed as an ARGUMENT to a function whose parameter at
    // that position is an array, that callee fixes the rank the kernel
    // consumes: `rowsum(x)` with `rowsum : Array<_,1> -> _` means `x` is a
    // rank-1 fiber. Plain application does NOT unify arg types against param
    // types, so such a param stays IRTInfer -- recover the rank from the
    // callee's signature here so the former peels the right number of outer
    // dims and perRowType below pins the param to the concrete fiber.
    let rankOfCalleeParam (pty: IRType) : int option =
        // A `Poly<T^k>` pack's base is an arity-k inference VAR (from `T^k`), not
        // a concrete array -- read the rank off its arity constraint. `mean(row: T^1)`
        // is the same: a bare `T^1` param is an arity-1 var, not `Array<..>`.
        let rankOfTy t =
            match env.Subst.Resolve t with
            | ArrayElem arr -> Some arr.IndexTypes.Length
            | IRTInfer id ->
                match env.Subst.GetArityConstraint id with
                | Some k when k > 0 -> Some k
                | _ -> None
            | _ -> None
        match env.Subst.Resolve pty with
        | IRTPoly (baseTy, _) -> rankOfTy baseTy
        | other -> rankOfTy other
    let paramRankFromFuncArg (pname: string) (body: TypedExpr) : int option =
        let isParamVar (e: TypedExpr) =
            match e.Kind with TExprVar (n, _, _) -> n = pname | _ -> false
        let mutable found : int option = None
        let rec walk (e: TypedExpr) =
            (match e.Kind with
             | TExprApp (f, args) ->
                 match env.Subst.Resolve f.Type with
                 | FuncElem (paramTys, _) ->
                     args |> List.iteri (fun i a ->
                         if found.IsNone && isParamVar a then
                             // Poly packs are variadic: every arg maps to the
                             // single pack param's element rank.
                             let pIdx = if i < paramTys.Length then i else paramTys.Length - 1
                             if pIdx >= 0 then
                                 match rankOfCalleeParam paramTys.[pIdx] with
                                 | Some r -> found <- Some r
                                 | None -> ())
                 | _ -> ()
             | _ -> ())
            typedExprChildren e |> List.iter walk
        walk body
        found
    let kernelInputRanks =
        resolvedParamTypes |> List.mapi (fun i t0 ->
            // Match through the UNIT wrapper. A `T<day>^1` annotation resolves
            // to `IRTUnitAnnotated(IRTInfer _, day)`, so matching `t0` directly
            // sends every unit-carrying abstract param past the IRTInfer arms
            // to the rank-0 default -- the unit-free twin of the same program
            // took a different path. Units say what a value MEASURES, never
            // whether its shape is known. `ArrayElem` already sees through the
            // wrapper, so it keeps reading the original.
            let t = IR.stripUnits t0
            match t with
            | ArrayElem arr -> arr.IndexTypes.Length
            | IRTInfer _ when
                (let pn = if i < lambdaInfo.Params.Length then lambdaInfo.Params.[i].Name else ""
                 (paramRankFromFuncArg pn lambdaInfo.Body).IsSome) ->
                let pn = lambdaInfo.Params.[i].Name
                (paramRankFromFuncArg pn lambdaInfo.Body).Value
            | IRTInfer id when i < arrayTypes.Length && i < sDimsPerArray.Length ->
                let arrTy = arrayTypes.[i]
                let sDims = sDimsPerArray.[i]
                let raggedInnerCount =
                    arrTy.IndexTypes
                    |> List.filter (fun idx -> isRaggedInnerKind idx.IxKind)
                    |> List.length
                // Re-attribute ragged inner dims to the kernel side ONLY when the
                // param is structurally used as an array (consuming kernel). For
                // an elementwise scalar use, the inner dim is NOT consumed and
                // must stay on the iteration/output side -> kernel rank 0, so the
                // ragged/DepIdx inner dim propagates to the output.
                //
                // A WRITTEN rank-1 annotation overrides that body scan. `T^1`,
                // `T<day>^1` and friends leave the param an ARITY-CONSTRAINED
                // inference var rather than a concrete `Array<..>` (see
                // rankOfCalleeParam), so the `ArrayElem` arm above never fires
                // for them and the structural scan below never sees the intent:
                // a body that only forwards the row into a tuple or a call
                // argument (`hanning((trow, srow), ..)`) reads as elementwise
                // and the param silently binds one ELEMENT. The annotation is a
                // declaration of the row, not a hint -- honour it directly.
                let annotatedRank =
                    match env.Subst.GetArityConstraint id with
                    | Some k when k > 0 -> Some k
                    | _ -> None
                let pname =
                    if i < lambdaInfo.Params.Length then lambdaInfo.Params.[i].Name else ""
                if raggedInnerCount > 0 && annotatedRank.IsNone
                   && not (paramUsedAsArray pname lambdaInfo.Body) then
                    0
                else
                    let trueIteratedDims = max 0 (sDims - raggedInnerCount)
                    max 0 (arrTy.IndexTypes.Length - trueIteratedDims)
            | _ -> 0)

    // MIXED ROW/ELEMENT ANNOTATIONS over an ALL-ragged operand pack.
    // The peel binds every param off ONE offsets table at one __g, so the
    // params must agree on what a "step" is: all rows, or all elements. A
    // rank-1 annotation on one param and a rank-0 (or absent) annotation on
    // another asks for both at once, which the peel cannot express -- and
    // which would otherwise reach codegen as a silently half-bound kernel.
    //
    // Every operand must be ragged for this to be the right diagnosis. A
    // ragged operand BESIDE a dense one is a different (and still refused)
    // shape whose refusal already lands in codegen, with a message about
    // mixing -- taking it over here would move that pin's stage.
    let allRaggedOperandPack =
        not (List.isEmpty arrayTypes)
        && arrayTypes |> List.forall (fun at ->
            at.IndexTypes |> List.exists (fun idx -> isRaggedInnerKind idx.IxKind))
    let mixedRowElementAnnotations =
        allRaggedOperandPack
        && arrayTypes.Length > 1
        && kernelInputRanks.Length = arrayTypes.Length
        && (kernelInputRanks |> List.exists (fun r -> r > 0))
        && (kernelInputRanks |> List.exists (fun r -> r = 0))

    if mixedRowElementAnnotations then
        Error (Other "co-iterating ragged or grouped arrays needs every kernel parameter to bind the same way: all ROWS (each annotated rank-1 -- `T^1`, `T<unit>^1`, or `Array<T like RaggedIdx<_>>`) or all ELEMENTS (none annotated rank-1). One offsets table drives the shared walk, so a row parameter beside an element parameter has no single step to take. Annotate every parameter, or none.")
    else

    // Infer T-dimensions from the kernel's resolved return type (section 9.2).
    // If the kernel returns an array, its index types become T-dimensions
    // in the output. If it returns a scalar, there are no T-dimensions.
    let (kernelTDims, kernelOutputRank) =
        let resolved = env.Subst.Resolve(lambdaInfo.ReturnType)
        match resolved with
        | ArrayElem arr ->
            let tDims = arr.IndexTypes |> List.map (fun idx -> { idx with Kind = TDimension })
            (tDims, tDims.Length)
        | _ -> ([], 0)

    // Mark each array's consumed fiber dimensions as T-dimensions (section 9.2). The
    // kernel consumes its innermost irank(f,i) = kernelInputRanks.[i] dims as a
    // fiber argument (e.g. a TimeIdx fiber reduced inside the kernel). Those
    // dims are NOT part of the symmetric iteration grid: re-tagging them
    // Kind = TDimension makes every downstream consumer consistent at once --
    // computeSDimsPerArray (counts only SDimension) yields the grid count,
    // buildLoopLevelStructure (builds levels only for SDimension) emits grid-
    // depth loops and leaves the fiber as the kernel's array slice, and
    // deduceOutputType symmetrizes only the grid dims. For scalar kernels
    // irank = 0, so nothing is re-tagged and behavior is unchanged.
    let gridArrayTypes =
        arrayTypes |> List.mapi (fun i at ->
            let irank = if i < kernelInputRanks.Length then kernelInputRanks.[i] else 0
            if irank <= 0 then at
            else
                // The fiber is the innermost irank dims; mark them TDimension.
                let n = at.IndexTypes.Length
                let retagged =
                    at.IndexTypes |> List.mapi (fun j idx ->
                        if j >= n - irank then { idx with Kind = TDimension } else idx)
                { at with IndexTypes = retagged })

    // A deduced WREATH TIE (docs/plan-orbit-index-types.md section 9 step 4) takes the
    // whole application off the generic axis-group thread: its iteration is the
    // segment-peeled `orb_visit` nest, driven from the OUTPUT class, and none of
    // the three analyses below has a meaning for it. They are skipped rather
    // than computed-and-ignored because all three funnel into
    // `buildLoopLevelStructure`, which REFUSES a wreath input outright -- the
    // depth-3 shape `let P = f(A,A) in g(P,P)` would otherwise die here, before
    // `deduceOutputType` ever ran. Same predicate, same arguments, one rule
    // (`IR.deduceWreathTie`), so this cannot disagree with what the output type
    // ends up being.
    // Per-parameter SIGN parities of the kernel body, for the tie rule's
    // soundness gate (deduceWreathTie condition 6: a '-' inner level requires
    // the kernel provably sign-odd in every tied argument). Computed ONCE with
    // the same resolver checkFunctionDecl uses -- an eta wrapper over a named
    // function resolves through the callee's FuncSignParities summary -- and
    // used three ways: the verdict below, deduceOutputType's identical call,
    // and recorded on the kernel (resolvedLambdaInfo.SignParities ->
    // IRCallable.SignParities) so codegen and the interpreter hand the tie
    // rule the SAME values. Skipped where no tie can fire (reynolds, arity
    // < 2); the gate reads a missing list as all-unknown.
    let kernelSignParities : KernelSignParity list =
        if isReynolds || lambdaInfo.Params.Length < 2 then []
        else
            let signResolver (calleeId: IRId) =
                match env.FuncSignParities.TryGetValue calleeId with
                | true, ps -> Some ps
                | _ -> None
            Blade.Deduce.deduceSignParities signResolver lambdaInfo.Params lambdaInfo.Body
            |> List.map (function
                | Blade.Deduce.SOdd -> KspOdd
                | Blade.Deduce.SEven -> KspEven
                | Blade.Deduce.SUnknown -> KspUnknown)
    let wreathVerdict =
        deduceWreathTie gridArrayTypes identities iterGroups antisymStorageGroups
                        kernelTDims (kernelInputRanks |> List.exists (fun r -> r > 0)) isReynolds
                        kernelSignParities
    // Surface the gate's refusal HERE -- the one seam that can say it to the
    // user (codegen and the interpreter run downstream of a successful
    // typecheck; their arms are loud backstops). KspEven is the
    // CommContradictsBody analog one level up; KspUnknown refuses too, because
    // per-argument oddness is a claim no clause declares, so there is no user
    // word to trust -- the BL4015 inheritance-gate precedent (the rationale is
    // spelled out at deduceWreathTie condition 6).
    let wreathGateErr =
        match wreathVerdict with
        | IR.WreathKernelNotOdd (argPos, parity, innerLevels) ->
            let pname =
                match List.tryItem argPos lambdaInfo.Params with
                | Some (p: TypedParam) -> p.Name
                | None -> sprintf "argument %d" argPos
            let proved =
                match parity with
                | KspEven -> "provably sign-EVEN (h(-p, q) = h(p, q))"
                | _ -> "of UNKNOWN sign parity"
            Some (WreathTieKernelNotOdd (pname, proved, ppOrbitLevels innerLevels))
        | _ -> None
    match wreathGateErr with
    | Some e -> Error e
    | None ->
    let wreathTie =
        match wreathVerdict with IR.WreathTied t -> Some t | _ -> None
    // A wreath INPUT that did NOT earn a tie has no iteration at all: only
    // the segment-peeled `orb_visit` nest walks a wreath pool, driven by the
    // OUTPUT class, so an application that doesn't produce one (unary map,
    // non-comm binary, distinct wreath operands) can't be iterated.
    // `buildRawLoopLevels` already refuses it via `failwith` (surfaces as
    // BL9001 "internal compiler error"), but this is a language limitation,
    // not a compiler bug -- say so on the user-facing channel before the
    // three analyses below reach that backstop (kept as a loud fallback for
    // any future producer that slips past this gate).
    let wreathInputRefusal =
        if wreathTie.IsSome then None
        else
            gridArrayTypes
            |> List.tryPick (fun at ->
                at.IndexTypes |> List.tryFind (fun ix ->
                    ix.Kind = SDimension && ix.Symmetry = SymWreath))
            |> Option.map (fun ix ->
                OrbitStorageUnsupported (ppOrbitLevels (orbitLevelsOf ix),
                                         "kernel application over a wreath-typed argument \
(only a comm/anticomm tie over EVERY argument produces a wreath output, and only that shape has a traversal nest)"))
    match wreathInputRefusal with
    | Some e -> Error e
    | None ->
    // Grouping/iteration reads `iterGroups` (comm  union  anticomm): a declared
    // antisymmetric pair fuses its axes and iterates the simplex exactly like
    // a comm pair -- only the strictness (IR's StrictOffset) and the stored
    // symmetry class differ, and both of those are decided downstream.
    let states =
        if wreathTie.IsSome then []
        else computeAllSymcomStates identities gridArrayTypes iterGroups (computeSDimsPerArray gridArrayTypes)
    let triLevels =
        if wreathTie.IsSome then []
        else computeTriangularLevels gridArrayTypes identities iterGroups (computeSDimsPerArray gridArrayTypes)
    let speedup =
        if wreathTie.IsSome then 1L
        else computePartialProductSpeedup gridArrayTypes identities iterGroups (computeSDimsPerArray gridArrayTypes)

    // Unify each kernel parameter with the source array's per-row type.
    // This catches mismatches like a String-typed kernel param applied to
    // a Float64 array, which would otherwise silently miscompile because
    // elem types weren't compared.
    //
    // Per-row type: for a source array of rank R and kernel param of rank K,
    // the kernel sees the array sliced at the outer R-K dims: kRank=0 -> the
    // elem type; kRank=R -> the whole array (degenerate); 0<kRank<R -> an
    // array with the inner kRank dims preserved. Length mismatches (arity
    // errors) are caught earlier, so a defensive zip is sufficient.
    let perRowType (arrTy: IRArrayType) (kRank: int) : IRType =
        let r = arrTy.IndexTypes.Length
        if kRank <= 0 then
            arrTy.ElemType  // Scalar kernel param sees one element per iter.
        elif kRank >= r then
            mkArrayLike arrTy  // Kernel wants the whole array (degenerate).
        else
            let nOuter = r - kRank
            let innerDims = arrTy.IndexTypes |> List.skip nOuter
            mkArrayLike { arrTy with IndexTypes = innerDims }

    // Expand each source into its kernel-facing param row type(s). A REAL array
    // contributes ONE param (its per-row slice). A VIRTUAL source (range<...>)
    // contributes ONE param PER index-type slot -- the index value at that slot --
    // so range<I1, I2> presents (i1, i2) to the kernel. For a single-slot virtual
    // source this equals perRowType's single-slot result (the arrow's
    // ElemType), so single-index behavior is unchanged.
    let expandedRows =
        arrayTypes
        |> List.mapi (fun i at ->
            if at.IsVirtual then
                // One param per RANK SLOT: a multi-rank index type (e.g. SymIdx<2,N>,
                // or a CompoundIdx of mask-rank R) contributes Rank coordinate params,
                // per the rank rule (kernel index slots = iteration rank). For rank-1
                // (dense) indices this is one param per index type, unchanged from 1b.
                at.IndexTypes |> List.collect (fun idx -> List.replicate idx.Rank (elemTypeForIterationIndex idx))
            else
                let kRank = if i < kernelInputRanks.Length then kernelInputRanks.[i] else 0
                [perRowType at kRank])
        |> List.concat

    // ---- THE WIDTH-SCHEMA MATCH ----
    // docs/plan-tuples-vs-arg-packs.md 6c. The parameter
    // list is a WIDTH SCHEMA over the pack's flat leaf sequence: an
    // unannotated parameter consumes one leaf, a `Tuple<k>` parameter consumes
    // k. Totals must agree, or it is a hard error.
    //
    // `expandedRows` IS the leaf sequence at this seam. Its two existing rules
    // are preserved verbatim because they are what makes the leaf count right:
    // a REAL array contributes one row (its per-row slice), a VIRTUAL source
    // (`range<I, J>`) contributes one row PER INDEX SLOT -- which is why the
    // co-iteration index-param form needs no special case here (the synthetic
    // range operand appended above already widened the leaf sequence by
    // exactly the number of index params). `zip` contributed its children as
    // separate operands upstream, so it is already k leaves.
    //
    // The SLICING half already happened: the tuple-param expansion at the top
    // of this function turned every `Tuple<k>` param into k row params plus a
    // body-entry let. So by here the schema is all-width-1 and the match is a
    // length comparison again -- but now it is CHECKED (`declaredTotalWidth`
    // is what the pack must supply), where it used to be waved through.
    let schemaRows : IRType list option =
        if lambdaInfo.Params.Length <> expandedRows.Length then None
        else Some expandedRows

    // STRICT quantity slots for KERNEL parameters (BL3010, the <@> twin of
    // the dispatch-seam check in dispatchAppOrIndex): a lambda param DECLARED
    // with a quantity type (`lambda(x: Float<speed>) -> ...`) rejects a row
    // whose element signature does not carry that nominal. Unannotated params
    // (still inference vars, sig None) and structural-unit params keep the
    // permissive unification below exactly.
    let kernelQuantityClash =
        match schemaRows with
        | None -> None
        | Some expandedRows ->
            let sigOf (t: IRType) =
                match env.Subst.Resolve t with
                | ArrayElem at ->
                    let e = env.Subst.Resolve at.ElemType
                    (IR.getUnits e, (match e with IRTInfer _ -> false | _ -> true))
                | IRTInfer _ -> (None, false)
                | resolved -> (IR.getUnits resolved, true)
            List.zip resolvedParamTypes expandedRows
            |> List.mapi (fun i (paramTy, row) -> (i, sigOf paramTy, sigOf row))
            |> List.tryPick (fun (i, (pu, _), (ru, rConcrete)) ->
                match pu with
                | Some pu when pu.Nominal.IsSome
                               && rConcrete
                               && (match ru with
                                   | Some r -> r.Nominal <> pu.Nominal
                                   | None -> true) ->
                    let got =
                        match ru with
                        | None -> "bare (it carries no unit signature)"
                        | Some r ->
                            match r.Nominal with
                            | Some qn -> sprintf "the quantity '%s'" qn
                            | None -> sprintf "structurally dimensioned (%s)" (ppUnitSig r)
                    Some (QuantityArgMismatch (i + 1, pu.Nominal.Value, got))
                | _ -> None)

    // A PACK parameter (`Poly<T^k>`) absorbs the whole argument list and has no
    // fixed width, so the schema does not describe it. Every supported spelling
    // eta-expands to the pack width BEFORE reaching this seam (the deferred-
    // former arm in inferApply), so a Poly param surviving here means some
    // other route built the kernel -- stand down rather than invent a width.
    // Disjoint from the tuple arm by construction: Unify has no
    // `IRTPoly ~ IRTTuple` rule (5.3).
    let hasPolyParam =
        resolvedParamTypes |> List.exists (fun t ->
            match t with IRTPoly _ -> true | _ -> false)

    let kernelParamUnifyResult =
        match kernelQuantityClash with
        | Some err -> Error err
        | None ->
        match schemaRows with
        | Some rows ->
            // Use resolved types so the unify call sees the same shape we used
            // to compute kRank. (Reading param.Type directly could be stale.)
            (List.zip resolvedParamTypes rows)
            |> List.fold (fun acc (paramTy, row) ->
                acc |> Result.bind (fun () -> unify env.Subst paramTy row))
                (Ok ())
        | None when hasPolyParam || lambdaInfo.Params.IsEmpty || expandedRows.IsEmpty ->
            Ok ()
        | None ->
            // THE HARD ARITY ERROR. This slot used to read
            //   `Ok ()  // Arity mismatch handled elsewhere`
            // and "elsewhere" did not exist (2.4): under-arity silently dropped
            // operands and still iterated their axes (3.4's 12-cell program),
            // over-arity passed `check` and died in g++ on an undeclared temp.
            // Both are now this message.
            let widthDesc =
                if declaredTotalWidth = declaredParamCount then
                    sprintf "%d parameter%s" declaredParamCount
                            (if declaredParamCount = 1 then "" else "s")
                else
                    sprintf "%d parameter%s of total width %d"
                            declaredParamCount
                            (if declaredParamCount = 1 then "" else "s")
                            declaredTotalWidth
            let steer =
                if declaredParamCount = 1 && declaredTotalWidth = 1 && expandedRows.Length > 1 then
                    // 5.2's genuinely undecidable shape: one unannotated param
                    // over a width-k pack. The pack reading and the tuple
                    // reading are both well-formed and disagree, and the body
                    // cannot vote (it is inferred before the param is bound).
                    // Demand the annotation, exactly as BL3010 does for
                    // quantities.
                    sprintf " Write %d parameters to take the operands separately, or annotate the single parameter `Tuple<%d>` to receive them as one tuple."
                            expandedRows.Length expandedRows.Length
                elif declaredTotalWidth > expandedRows.Length then
                    " Drop the extra parameters, or supply more operands."
                else
                    " Add parameters for the extra operands, or group them into a parameter annotated `Tuple<N>`."
            Error (KernelPackArity
                     (sprintf "kernel arity: this application supplies %d operand%s to a kernel with %s.%s"
                              expandedRows.Length
                              (if expandedRows.Length = 1 then "" else "s")
                              widthDesc steer))

    match kernelParamUnifyResult with
    | Error e -> Error e
    | Ok () ->
        // Reject unsupported / miscompiling kernel-body shapes now that the
        // params are bound to the iterated element/row types:
        // (1) Complex accessor on a ROW param (`real(z)` etc. whose operand
        //     unified to an ARRAY, e.g. a zip row): DEFERRED at body-inference
        //     time (operand was still an infer var, typed scalar Float64
        //     without constraining it), so lowering would embed the
        //     uncaptured param in a broadcast kernel, giving an IR
        //     dangling-VarId (BL6001). Steer to a scalar-per-element map.
        // (2) Array-valued ELEMENTWISE body (`ra * rb` between two row
        //     params): re-synthesizes into compute(method_for(zip...)) with
        //     output rank >= 1, which codegen collapses to (sum ra)(sum rb)
        //     in expression position -- a silent miscompile. A bare
        //     array-param passthrough is fine; reject and steer to
        //     prodsum/reduce otherwise.
        let rec findBadComplexAccessor (e: TypedExpr) : string option =
            match e.Kind with
            | TExprUnaryOp ((OpReal | OpImag | OpArg) as op, operand)
                    when (match env.Subst.Resolve operand.Type with ArrayElem _ -> true | _ -> false) ->
                Some (match op with OpReal -> "real" | OpImag -> "imag" | _ -> "arg")
            | _ -> typedExprChildren e |> List.tryPick findBadComplexAccessor
        // AN ARRAY-VALUED KERNEL RETURN IS NOW SUPPORTED (stage S3,
        // docs/plan-kernel-body-materialization.md manifestation M-C). The S0
        // guard that stood here -- "kernelOutputRank >= 1 and the body is not a
        // bare row passthrough" -> reject -- is gone, together with its pin
        // (diagnostics/069_array_valued_kernel_return_rejects, deleted; its
        // value twin is loops/121).
        //
        // What it was holding the line against, and what replaced it:
        // `kernelTDims` is computed just above and `deduceOutputType` has always
        // appended it to the output TYPE, so the deduced grid was already
        // rank-(outer+inner). Nothing sized the emitted grid to match: codegen
        // built the extents table from the LOOP BINDINGS alone, so a rank-2 grid
        // got a one-entry table ({ 2 }), the inner extent read as 0, and the
        // program printed `[[], []]` with no diagnostic anywhere -- the same
        // silent-wrong-answer class as func-arrays/011's rank-2 literal of
        // computed rows. S3 closes exactly that gap in CodeGen (the trailing
        // T-dim extents now come from the output type, and the nest writes a
        // whole row per outer cell); the type side needed no change at all.
        //
        // Rejection (2) below survives on its own terms: a complex ACCESSOR
        // (real/imag/arg) applied to an array operand is still refused, because
        // that is an elementwise array-valued body the inline path collapses to
        // a scalar -- a different failure from the one S3 fixed.
        match findBadComplexAccessor lambdaInfo.Body with
        | Some name -> Error (IntrinsicComplexScalarOnly name)
        | None ->
        // After param-type unification, inference variables that flowed into
        // the body's TExprIndex sites may now resolve to nominally-tagged
        // types (e.g., `r` in `lambda(r) -> by_country(r)` is unified with
        // the iterated array's elem type `Nat<RegionIdx>`). Re-run the tag
        // check across the body so cross-tag indexing through kernel
        // parameters surfaces as a real type error rather than silently
        // typechecking. See revalidateBodyTagChecks for rationale.
        revalidateBodyTagChecks env lambdaInfo.Body
        |> Result.bind (fun () ->
        // Unit-only second pass over the kernel body, now that the params
        // are bound to the input element types (see kernelBodyUnits). The
        // computed signature replaces whatever annotation the return type
        // resolution leaked, and op mismatches inside the body reject here.
        // NESTED-APPLY DEFERRAL, the array sibling of the scalar OpMath
        // deferral. This apply may itself be nested inside an enclosing lambda
        // body that is STILL being inferred (`lambda(w) -> { let e =
        // exp <@> (i * w * ts); ... }`): our params have just unified with the
        // operands' element types, but every unit reachable from here was
        // computed while the ENCLOSING param was an unresolved infer var
        // contributing no units, so a signature judged now can be exactly the
        // dimensioned capture's own even when the product cancels. Defer the
        // REJECTION (never an acceptance) to the enclosing kernel's own second
        // pass, whose TExprApply arm rechecks this map with the outer params
        // bound and the enclosing kernel-local lets modelled in `bound`.
        //
        // Staleness reaches this apply by three routes, and the trigger is the
        // SHARED provisional-units predicate because only it sees all three:
        // an operand that is itself provisional; a LET-BOUND operand, a bare
        // var with no children, so a plain node walk never reaches the
        // unresolved param inside its defining expression; and a CAPTURE read
        // by the kernel body (`let w = 2.0 * f; ts <@> lambda(t) ->
        // sin(w * t)`), where every operand is fully resolved and only `w` is
        // stale. `typedExprHasProvisionalUnits` chases lets, so routes 2 and 3
        // are visible from the operands and the kernel body alike -- this is
        // the same predicate the scalar OpMath deferral uses, deliberately, so
        // the next manifestation of this shape has one place to be fixed.
        //
        // Second condition: defer only when pass 2 can actually RE-MODEL this
        // apply -- a lambda kernel (reynolds peeled) whose arity matches the
        // operand count, which is exactly what `nestedApplyElemUnits` handles.
        // Anything it would answer "no claim" for is judged here and now, so a
        // deferral cannot decay into silent acceptance. (The scalar site needs
        // no such condition: pass 2's TExprUnaryOp arm re-models every scalar
        // OpMath node unconditionally.) The one loss is the seam Theme C
        // already documents: a lambda that is never `<@>`-applied runs no
        // second pass at all.
        let pass2CanRemodel =
            let rec kernelLambda (k: TypedExpr) =
                match k.Kind with
                | TExprLambda li -> Some li
                | TExprReynolds (inner, _) -> kernelLambda inner
                | _ -> None
            match kernelLambda tKernel with
            | Some li -> li.Params.Length = arrays.Length
            | None -> false
        let operandsProvisional =
            (arrays |> List.exists (typedExprHasProvisionalUnits env))
            || typedExprHasProvisionalUnits env lambdaInfo.Body
        (match kernelBodyUnits env Map.empty lambdaInfo.Body with
         | Error _ when env.InLambdaBody && pass2CanRemodel && operandsProvisional -> Ok None
         | r -> r)
        |> Result.bind (fun bodyUnits ->
        // Infer output element type from kernel return type, falling back to input arrays.
        // Returns IRType (Phase B2). Primitives are wrapped IRTScalar.
        let restampScalar (t: IRType) =
            match IR.stripUnits t with
            | IRTScalar _ as bare ->
                match bodyUnits with
                | Some u -> IRTUnitAnnotated (bare, u)
                | None -> bare
            | _ -> t
        // Post-unification COMPLEX re-stamp: the kernel body was typed while
        // its params were unresolved, so scalar promotion collapsed
        // prematurely to real (`lambda(z) -> z * 2.0` over a complex array
        // stamped Float64 because z hadn't unified with Complex128 yet). Now
        // that params ARE unified, redo the promotion bottom-up. Conservative:
        // re-stamps ONLY when recomputed is complex and stamped is real.
        let restampedBody =
            let elemOfType (ty: IRType) : ElemType option =
                match IR.stripUnits (env.Subst.Resolve ty) with
                | IRTScalar et -> Some et
                | _ -> None
            let isComplexElem = function ETComplex64 | ETComplex128 -> true | _ -> false
            let isRealScalar = function
                | Some (ETFloat32 | ETFloat64 | ETInt32 | ETInt64) -> true
                | _ -> false
            // Upgrade a node's scalar type, preserving a unit-annotation wrapper.
            let withElem (node: TypedExpr) (et: ElemType) : TypedExpr =
                let newTy =
                    match node.Type with
                    | IRTUnitAnnotated (_, u) -> IRTUnitAnnotated (IRTScalar et, u)
                    | _ -> IRTScalar et
                { node with Type = newTy }
            // Upgrade `node` iff its stamp is a real scalar and `computed` is
            // complex; otherwise keep it (byte-identical for real kernels).
            let maybeUpgrade (node: TypedExpr) (computed: ElemType option) : TypedExpr =
                match computed, elemOfType node.Type with
                | Some ce, cur when isComplexElem ce && isRealScalar cur -> withElem node ce
                | _ -> node
            let rec walk (t: TypedExpr) : TypedExpr =
                match t.Kind with
                | TExprBinOp (bmode, ((OpAdd | OpSub | OpMul | OpDiv | OpCaret) as bop), l, r) ->
                    let l2, r2 = walk l, walk r
                    let node = { t with Kind = TExprBinOp (bmode, bop, l2, r2) }
                    match elemOfType l2.Type, elemOfType r2.Type with
                    | Some le, Some re ->
                        match IR.promoteElemType le re with
                        | Some pe when isComplexElem pe -> maybeUpgrade node (Some pe)
                        | _ -> node
                    | _ -> node
                | TExprUnaryOp (((OpNeg | OpConj) as uop), e) ->
                    let e2 = walk e
                    let node = { t with Kind = TExprUnaryOp (uop, e2) }
                    maybeUpgrade node (elemOfType e2.Type)
                | TExprUnaryOp (OpMath name, e) ->
                    let e2 = walk e
                    let node = { t with Kind = TExprUnaryOp (OpMath name, e2) }
                    match name, elemOfType e2.Type with
                    // abs of a complex is the real magnitude: correct a
                    // deferred stamp (the operand's variable, now resolved
                    // complex) to Float64.
                    | "abs", Some (ETComplex64 | ETComplex128) ->
                        (match elemOfType node.Type with
                         | Some ETFloat64 -> node
                         | _ -> withElem node ETFloat64)
                    | "abs", _ -> node
                    // Transcendentals preserve a complex operand's type.
                    | _, Some ((ETComplex64 | ETComplex128) as ce) ->
                        maybeUpgrade node (Some ce)
                    // Deferred REAL operand (see the intrinsic's IRTInfer
                    // arm): the real intrinsics are Float64-valued.
                    | _, Some (ETInt32 | ETInt64 | ETFloat32 | ETFloat64) ->
                        (match elemOfType node.Type with
                         | Some ETFloat64 -> node
                         | _ -> withElem node ETFloat64)
                    | _ -> node
                | TExprUnaryOp (((OpReal | OpImag) as uop), e) ->
                    // Deferred-width correction: real/imag of a Complex64
                    // yield Float32 components (the deferred arm stamped the
                    // Complex128 answer, Float64).
                    let e2 = walk e
                    let node = { t with Kind = TExprUnaryOp (uop, e2) }
                    (match elemOfType e2.Type, elemOfType node.Type with
                     | Some ETComplex64, Some ETFloat64 -> withElem node ETFloat32
                     | _ -> node)
                | TExprIf (c, a, b) ->
                    let a2, b2 = walk a, walk b
                    let node = { t with Kind = TExprIf (c, a2, b2) }
                    match elemOfType a2.Type, elemOfType b2.Type with
                    | Some le, Some re -> maybeUpgrade node (IR.promoteElemType le re)
                    | _ -> node
                | TExprLet (name, vid, value, body) ->
                    let v2, b2 = walk value, walk body
                    let node = { t with Kind = TExprLet (name, vid, v2, b2) }
                    maybeUpgrade node (elemOfType b2.Type)
                | _ -> t
            walk lambdaInfo.Body
        // The re-stamped body overrides the resolved return type in BOTH
        // directions.
        //   real -> complex: the collapse hit the return type too (the body was
        //     typed while its params were unresolved).
        //   complex -> real: the four complex -> real intrinsics. `abs <@>
        //     complexArray` eta-expands to lambda(__k) -> abs(__k), and the
        //     scalar `abs` arm types a deferred operand's application AT the
        //     operand's own type var -- which is the lambda's return-type var.
        //     Unifying the param with Complex128 therefore made the RESOLVED
        //     return complex, so `abs` came out complex-with-zero-imag and
        //     propagated into every downstream binding. The walk above already
        //     corrected the body node to its real answer; adopt it. Narrow on
        //     purpose: only when the body's TOP node is one of those four
        //     intrinsics applied to a complex operand.
        let adoptBodyElem (r: IRType) =
            let bodyIsComplexToReal =
                match restampedBody.Kind with
                | TExprUnaryOp ((OpMath "abs" | OpReal | OpImag | OpArg), operand) ->
                    (match IR.stripUnits (env.Subst.Resolve operand.Type) with
                     | IRTScalar (ETComplex64 | ETComplex128) -> true
                     | _ -> false)
                | _ -> false
            match IR.stripUnits r, IR.stripUnits restampedBody.Type with
            | IRTScalar (ETFloat32 | ETFloat64 | ETInt32 | ETInt64), (IRTScalar (ETComplex64 | ETComplex128) as ct) -> ct
            | IRTScalar (ETComplex64 | ETComplex128), (IRTScalar (ETFloat32 | ETFloat64) as rt) when bodyIsComplexToReal -> rt
            | _ -> r
        let outputElemType =
            let resolved = adoptBodyElem (env.Subst.Resolve(lambdaInfo.ReturnType))
            match resolved with
            | IRTScalar _ as t -> restampScalar t                  // stamp walk-computed units
            | ArrayElem arr -> arr.ElemType                         // already IRType
            | IRTUnitAnnotated (IRTScalar _, _) as t -> restampScalar t
            | IRTNamed _ as t -> t                                 // struct/sum rows: Array<Struct> output
            | IRTTuple _ as t -> t                                 // tuple rows: Array<(..,..)> output
            | _ ->
                // Fall back to common element type of input arrays when the
                // return type is unresolved (IRTInfer) or has no
                // element-position meaning.
                arrayTypes |> List.tryPick (fun at -> Some at.ElemType)
                |> Option.defaultValue (IRTScalar ETFloat64)
                |> restampScalar
        // A kernel CONSUMES an inner dimension when it has an array-typed
        // parameter of rank > 0 (e.g. reduce over a ragged row). A purely
        // elementwise kernel (all scalar params) consumes nothing, so the
        // consumed-dim filter in deduceOutputType must NOT drop ragged/dep
        // inner dims -- they propagate through the elementwise map.
        let kernelConsumesInner = kernelInputRanks |> List.exists (fun r -> r > 0)
        let outputType = deduceOutputType gridArrayTypes identities iterGroups antisymStorageGroups (computeSDimsPerArray gridArrayTypes) kernelTDims outputElemType isReynolds isReynoldsAntisym kernelConsumesInner kernelSignParities env.Builder

        // Compact-class inheritance (stage 3, unary seam): deduceOutputType's
        // rank-0 elementwise arm hands the input group's compact class to
        // the output VERBATIM. Certify the kernel commutes with that class's
        // mirror involution, or refuse (see compactClassInheritError). A
        // rank > 1 compact record never joins a CROSS-argument axis group,
        // so each belongs to exactly one array/parameter; a consuming
        // position (kernelInputRanks > 0) is skipped since its record is
        // re-tagged TDimension. Under reynolds the wrapper OWNS the output
        // class, so no input producer of an antisymmetric output is in scope.
        let compactInheritErr : TypeError option =
            let inheritedClaims =
                if isReynolds || lambdaInfo.Params.Length <> gridArrayTypes.Length then []
                else
                    match outputType with
                    | ArrayElem outArr ->
                        let claimed =
                            outArr.IndexTypes
                            // SymWreath joins the filter for the reason spelled
                            // out at compactClassInheritError: an inner '-'
                            // level negates on mirror, so an inherited wreath
                            // class needs the same BL4015 certificate.
                            |> List.filter (fun ix ->
                                ix.Rank > 1
                                && (ix.Symmetry = SymAntisymmetric || ix.Symmetry = SymHermitian
                                    || ix.Symmetry = SymWreath))
                            |> List.map (fun ix -> ix.Symmetry)
                            |> Set.ofList
                        if Set.isEmpty claimed then []
                        else
                            gridArrayTypes
                            |> List.indexed
                            |> List.collect (fun (i, at) ->
                                if (kernelInputRanks |> List.tryItem i |> Option.defaultValue 0) > 0
                                then []
                                else
                                    at.IndexTypes
                                    |> List.filter (fun ix ->
                                        ix.Kind = SDimension && ix.Rank > 1
                                        && Set.contains ix.Symmetry claimed)
                                    // The level list travels with the class: two
                                    // wreath records of the same Symmetry can
                                    // need DIFFERENT verdicts (all-'+' needs no
                                    // certificate, any '-' does), so distinct-ing
                                    // on Symmetry alone would collapse them.
                                    |> List.map (fun ix -> (i, ix.Symmetry, orbitLevelsOf ix)))
                            |> List.distinct
                    | _ -> []
            if List.isEmpty inheritedClaims then None
            else
                let signResolver (calleeId: IRId) =
                    match env.FuncSignParities.TryGetValue calleeId with
                    | true, ps -> Some ps
                    | _ -> None
                let signParities =
                    Blade.Deduce.deduceSignParities signResolver lambdaInfo.Params restampedBody
                let conjCommutes =
                    Blade.Deduce.deduceConjCommutes lambdaInfo.Params restampedBody
                inheritedClaims |> List.tryPick (fun (i, cls, lvls) ->
                    let pname =
                        match List.tryItem i lambdaInfo.Params with
                        | Some (p: TypedParam) -> p.Name
                        | None -> sprintf "argument %d" i
                    compactClassInheritError cls lvls i pname signParities conjCommutes)
        match compactInheritErr with
        | Some e -> Error e
        | None ->

        let reynoldsSpeedup =
            if isReynolds then
                let r = identities.Length
                if r > 1 then factorial r else 1L
            else 1L

        // The substitution holds the refined param types but
        // `lambdaInfo.Params[i].Type` still holds the original (often
        // IRTInfer) values -- F# records are immutable, and lifted lambdas
        // can get `(double)` parameters where the substitution bound them
        // to `Array<...>`. Rebuild the lambda info with explicitly-resolved
        // param/return types and the body, so the resolved kernel carries
        // refined types directly, independent of downstream zonking.
        let resolvedLambdaInfo =
            { lambdaInfo with
                Params =
                    lambdaInfo.Params |> List.map (fun p ->
                        { p with Type = env.Subst.Resolve p.Type })
                // Record the seam's sign summary so Lowering stamps it onto
                // the lifted IRCallable -- codegen's and the interpreter's
                // deduceWreathTie calls then judge from these same values.
                SignParities = kernelSignParities
                // The complex re-stamp above must flow into the lifted lambda
                // too: with a stale Float64 stamp the lifted C++ function
                // would declare a double return around a std::complex body.
                Body = restampedBody
                ReturnType = adoptBodyElem (env.Subst.Resolve lambdaInfo.ReturnType) }

        // Store the kernel with resolved types in the typed AST. Lowering
        // walks this typed lambda and emits a lifted IRCallable referenced
        // via IRVar(callable.Id) at the kernel slot.
        let resolvedKernel =
            let lambdaExpr = mkTyped (TExprLambda resolvedLambdaInfo) tKernel.Type
            if isReynolds then mkTyped (TExprReynolds (lambdaExpr, isReynoldsAntisym)) tKernel.Type
            else lambdaExpr

        // Co-iterated records = the leading (nRecords - kernelSliceRank)
        // shared records; the kernel consumes the trailing kernelSliceRank
        // records as a per-iteration slice. One trim serves three shapes:
        // scalar kernel (kR=0, FULL product co-iterated, multi-axis);
        // row-mode kernel (loops/085, kR=1, only outer records co-iterate,
        // inner rides as a rank-1 slice); single packed record (SymIdx,
        // kR=0, walked as its Rank flat canonical levels). Operand shape
        // agreement is enforced where records are collected, so
        // kernelInputRanks.[0] is representative.
        let coIterSharedRecords =
            match sharedIndexTypes with
            | [] -> []
            | full ->
                let kR = kernelInputRanks |> List.tryHead |> Option.defaultValue 0
                let nShared = full.Length - kR
                if nShared <= 0 then [] else full |> List.truncate nShared
        let isCoIter = not (List.isEmpty coIterSharedRecords)
        // For co-iteration, output type spans the co-iterated records (not the
        // operands' outer product) -- PLUS the kernel's T-dimensions, exactly as
        // `deduceOutputType`'s step 4 appends them on the outer-product path.
        //
        // S3, manifestation M-C. This override replaces deduceOutputType's
        // result wholesale, so before S3 it silently dropped the kernel's array
        // return: `method_for(zip(A, B)) <@> lambda(ra, rb) -> ra * rb` came out
        // rank 1 (just the co-iterated Y axis) even though every cell holds a
        // whole X row, and lswosa's `family_spectra` grid came out rank 1
        // Float64 instead of rank 2 Complex128 -- which is why `transpose(grid,
        // [0,1])` reported "axis 1 out of range" and `grid <@> mag2` reported a
        // Complex128/Float64 mismatch one line later. Both are the same missing
        // append. Fresh ids and Kind = TDimension mirror deduceOutputType so the
        // two paths produce structurally identical records.
        let outputType =
            if isCoIter then
                let outputTDims =
                    kernelTDims
                    |> List.map (fun idx -> { idx with Kind = TDimension; Id = env.Builder.FreshId() })
                mkArrayArrow (coIterSharedRecords @ outputTDims) outputElemType None
            else outputType
        let info : TypedApplyInfo = {
            Loop = tLoop; Kernel = resolvedKernel
            Arrays = arrays; Identities = identities
            ArrayTypes = gridArrayTypes; SharedIndexTypes = coIterSharedRecords
            SymcomStates = states; TriangularLevels = triLevels
            // Grid S-dim count from the fiber-retagged array types (consumed
            // fiber dims are now TDimension, excluded from the count). Matches
            // SymcomStates/TriangularLevels and the grid-depth loop nest codegen
            // builds from ArrayTypes. Scalar kernels: irank=0, unchanged.
            SDimsPerArray = computeSDimsPerArray gridArrayTypes
            KernelInputRanks = kernelInputRanks; KernelOutputRank = kernelOutputRank
            KernelTDims = kernelTDims
            SpeedupFactor = speedup; ReynoldsSpeedup = reynoldsSpeedup
            HasReynolds = isReynolds; OutputType = outputType
            IsCoIteration = isCoIter  // derived: non-empty co-iterated records
            IsComposeApply = false
        }
        Ok (mkTyped (TExprApply info) outputType)))

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
    let isBuiltinScalar name =
        match name with
        | "Int" | "Int32" | "Int64"
        | "Float" | "Float32" | "Float64" | "Double"
        | "Complex64" | "Complex128"
        | "Bool" | "Void" | "Nat" | "String" | "Char"
        | "Poly" | "Array" -> true
        | _ -> false
    let rec scan ty =
        match ty with
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

and inferLambda env parms whereClause body : TypeResult<TypedExpr> =
    let scopeEnv = enterScope env
    let commGroups = extractCommGroups parms whereClause
    let antisymGroups = extractAntisymGroups parms whereClause

    // Fresh type variable scope for this lambda's type annotations.
    let savedScope = env.Subst.PushTypeVarScope()

    // Pre-scan: collect type variable names from all annotations.
    prescanTypeVarNames env (parms |> List.map (fun p -> p.Type))

    let mutable paramEnv = scopeEnv
    let typedParams = parms |> List.mapi (fun i p ->
        let varId = env.Builder.FreshId()
        let ty = match p.Type with
                 | Some t -> lowerTypeExpr env t
                 | None -> env.Subst.Fresh()  // Infer from usage
        // WIDTH SCHEMA (docs/plan-tuples-vs-arg-packs.md 6c): record the
        // WRITTEN `Tuple<k>` width before the annotation is thrown away by
        // lowering. Keyed by binder id, so the matcher at buildApplyInfo reads
        // the declaration rather than the (by then already unified) type.
        declaredTupleWidth p.Type |> Option.iter (fun w ->
            env.DeclaredTupleWidths.[varId] <- w)
        paramEnv <- bindVarSimple p.Name varId ty paramEnv
        { Name = p.Name; Type = ty; Index = i; VarId = varId; Default = None; NameSpan = p.NameSpan } : TypedParam)

    // ---- Parameter defaults (BL3012 rules + typing) ----
    // Trailing rule, required-params-only scope rule, then each default
    // typed against its param's (possibly inferred) type with the params in
    // scope. The typed expr rides on TypedParam.Default for the kernel-apply
    // seam (buildApplyInfo binds an omitted trailing param as a body-entry
    // TExprLet over it); direct calls re-type the SURFACE default instead.
    let defaultsResult : TypeResult<TypedParam list> =
        match parms |> List.tryFindIndex (fun p -> p.Default.IsSome) with
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
                    parms |> List.filter (fun p -> p.Default.IsSome) |> List.map (fun p -> p.Name) |> Set.ofList
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

    let boundNames = parms |> List.map (fun p -> p.Name) |> Set.ofList
    // A default's free variables are captures too: at the kernel-apply seam
    // the default is spliced into the BODY (a body-entry let), so a name it
    // reads from the enclosing scope must ride the capture list or the
    // lifted kernel would emit a dangling reference.
    let freeVars =
        let bodyFree = collectFreeVars boundNames body
        parms
        |> List.choose (fun p -> p.Default)
        |> List.map (collectFreeVars boundNames)
        |> List.fold Set.union bodyFree
    let captures = buildCaptures scopeEnv freeVars
    // An `omp(v: n)` naming no parameter is silently dropped downstream; say so.
    checkOmpVarNames env (typedParams |> List.map (fun p -> p.Name)) whereClause "this lambda"

    let result =
        defaultsResult |> Result.bind (fun typedParams ->
        // Body typing runs with InLambdaBody set: params may still be
        // inference variables here, so unit checks at scalar position defer
        // provisional rejections to the kernel-apply second pass (see
        // typedExprHasUnresolvedType). Defaults above type WITHOUT the flag --
        // they are decl-time values and keep decl-time strictness.
        inferExpr { paramEnv with InLambdaBody = true } body |> Result.bind (fun tBody ->
            // A lambda body is a value-forming boundary: reject a wildcard `_`
            // that escaped into it (its only legitimate role is a compound-index
            // coordinate), rather than letting it reach lowering.
            if exprContainsWildcard tBody then
                Error (Other
                    "wildcard `_` is not a value: it cannot be a lambda's body. It is only meaningful as a compound-index coordinate (e.g. B((a, _, c))).")
            else
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
            let funcTy = mkFuncArrow (typedParams |> List.map (fun p -> p.Type)) tBody.Type
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
        if parent = topName then sprintf "row %d" i else sprintf "%s.%d" parent i
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
        if p <> "" && p.Length <= 160 then sprintf "write it as %s" p
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
                                    sprintf "holds %d cell(s), but the simplex row there is %d wide" cs.Length w))
        else
            let isLeafLevel = (depth = rank - 1)
            let checkChild (i: int) (c: Expr) : TypeResult<TypedExpr> =
                let here = childName where_ i
                if isLeafLevel then
                    // i = 0 within a row is the diagonal cell (c_k = 0, so
                    // p' = p): the only cell a Hermitian class constrains.
                    if ix.Symmetry = SymHermitian && i = 0 && isComplexWithImag c then
                        setCurrentExprSpan c.Span
                        Error (HermitianLitDiagComplex (sprintf "the leading cell of %s" where_))
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
and checkExpr (env: TypeEnv) (expected: IRType) (expr: Expr) : TypeResult<TypedExpr> =
    match checkExprInner env expected expr with
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
    match expr.Kind, resolved with

    // Numeric/scalar literals retype to the expected scalar (numbers stay numbers,
    // bools stay bools -- no implicit 0<->false). The literal carries its source-
    // level value but its IRType matches the annotation.
    | ExprKind.ExprLit (LitInt _ as lit), IRTScalar et ->
        match et with
        | ETInt32 | ETInt64 ->
            Ok (mkTyped (TExprLit lit) (IRTScalar et))
        | ETFloat32 | ETFloat64 ->
            // F#-style type-directed widening: an int LITERAL in an explicitly
            // float-typed position adopts the float type (`let x: Float64 = 1`
            // is 1.0, `complex(0, 0)` works). Literals only -- an int-typed
            // VALUE still never flows to a float position implicitly.
            // lowerLiteralValued reconciles the value (emits IRLitFloat).
            Ok (mkTyped (TExprLit lit) (IRTScalar et))
        | _ ->
            Error (TypeMismatch (resolved, IRTScalar ETInt64))
    | ExprKind.ExprLit (LitInt _ as lit), IRTIdxTagged (IRTScalar (ETInt32 | ETInt64), _) ->
        // section 4.18.3: untyped int literal acquires the index tag from annotation
        // context. `let i: Idx<3> = 0` works; the 0 becomes Nat<Idx<3>>.
        // Strict in the OTHER direction: a bare `Nat` value cannot flow to
        // Nat<I> position without explicit cast -- but a LITERAL has no
        // pre-committed type, so context-driven typing applies here.
        Ok (mkTyped (TExprLit lit) resolved)
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
    | ExprKind.ExprLit (LitString _ as lit), IRTIdxTagged (IRTScalar ETString, _) ->
        // section 4.18.3 parallel for string-valued index tags (EnumIdx with
        // string values). Same context-driven coercion as the int case.
        Ok (mkTyped (TExprLit lit) resolved)
    | ExprKind.ExprLit (LitString _ as lit), IRTUnitAnnotated (IRTScalar ETString, _) ->
        // Quantity-tagged string position (`let s: String<title> = "K"`,
        // `"K" : title`): the literal adopts the annotation, same
        // context-driven rule as the numeric/Nat unit arms above.
        Ok (mkTyped (TExprLit lit) resolved)
    | ExprKind.ExprLit (LitFloat _ as lit), IRTScalar et ->
        match et with
        | ETFloat32 | ETFloat64 -> Ok (mkTyped (TExprLit lit) (IRTScalar et))
        | _ -> Error (TypeMismatch (resolved, IRTScalar ETFloat64))
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
    | ExprKind.ExprLit (LitInt _ | LitFloat _ | LitBool _ as lit), IRTInfer _ ->
        let flexTy = freshLiteralType env.Subst lit
        unify env.Subst flexTy expected
        |> Result.mapError (fun _ -> TypeMismatch (resolved, inferLiteralType lit))
        |> Result.map (fun () -> mkTyped (TExprLit lit) flexTy)

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
            // Build the element annotation: just the elem type if no inner
            // index types, otherwise an array with the remaining index types.
            // arrTy.ElemType is IRType post-B2.
            let elemAnnot =
                if innerIdxs.IsEmpty then arrTy.ElemType
                else mkArrayLike { arrTy with IndexTypes = innerIdxs }
            elems |> List.map (checkExpr env elemAnnot) |> sequenceResults
            |> Result.map (fun tElems ->
                mkTyped (TExprArrayLit (tElems, arrTy)) (mkArrayLike arrTy))

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
            mkTyped (TExprTuple tExprs) (IRTTuple (tExprs |> List.map (fun e -> e.Type))))

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
        Error (Other "complex values are constructed with complex(re, im); the tuple form `(re, im) : Complex128` is no longer supported")

    // Match in a checking position: push the expected type into each arm so a
    // literal/scalar arm can flex to it (bidirectional match -- see checkMatch).
    | ExprKind.ExprMatch (scrutinee, cases), _ ->
        checkMatch env expected scrutinee cases

    // Block in a checking position: push the expected type into the block's
    // final expression (which may itself be a match/literal that flexes).
    | ExprKind.ExprBlock (stmts, finalExpr), _ ->
        inferBlock env stmts finalExpr (Some expected)

    // Default: synthesize, then unify. This is the path that handles variables,
    // function calls, complex expressions, and any case the special-cases miss.
    | _ ->
        inferExpr env expr |> Result.bind (fun tE ->
            match unify env.Subst tE.Type expected with
            | Ok () -> Ok tE
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
/// tie over every argument, `IR.deduceWreathTie`), and the value it produces
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

/// Detect an unresolved QUALIFIED index-type path (`store.index.y`). Same
/// consumption-site pattern as the checks above, and self-marking: a path that
/// resolves yields the registered record verbatim (the provider builds its
/// axes with Tag = None), so a dotted Tag can only be lowerIndexType's
/// fall-through for a name that was never registered. Without this a typo'd or
/// renamed dimension would silently become a free dependent extent -- the
/// annotation would look bound to the store while constraining nothing.
and irTypeUnknownAxisPath (t: IRType) : string option =
    let pathOf (ix: IRIndexType) =
        match ix.Tag with
        | Some tag when tag.Contains "." && not (tag.StartsWith irrepsTagPrefix) -> Some tag
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
            sprintf "unknown index type '%s': '%s' is not a data-provider store binding in scope" path storeName
        else
            sprintf "the store '%s' has no dimension '%s'. It exposes: %s"
                storeName dim (known |> String.concat ", ")
    else
        sprintf "unknown qualified index type '%s'" path

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
and private boundedAggregateNoun (env: TypeEnv) (baseTy: TypeExpr) : string option =
    let rec strip (t: IRType) =
        match t with
        | IRTIdxTagged (inner, _) | IRTUnitAnnotated (inner, _) | IRTComputation inner -> strip inner
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

/// The consumption-site check: find a bound applied to an aggregate anywhere
/// in a surface annotation. Reads off the SURFACE TypeExpr, not the lowered
/// IRType -- bounds erase in `lowerTypeExpr` and never reach `IRType`.
/// Descends into aggregate COMPONENT positions (element types, tuple slots,
/// arrow slots) so `Array<Field<min=0.0> like Y>` is caught too; array INDEX
/// slots are left alone (a bound there is a BL4003 index-type question).
and private boundedAggregateError (env: TypeEnv) (site: string) (ty: TypeExpr) : TypeError option =
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
    // Name-keyed like FuncConstraints, same known shadowing weakness.
    (match binding.Pattern.Kind, binding.Value.Kind with
     | PatVar name, ExprKind.ExprLambda (parms, _, _)
            when parms |> List.exists (fun p -> p.Default.IsSome) ->
         env.FuncDefaults.[name] <- (parms |> List.map (fun p -> (p.Name, p.Type, p.Default)))
     | _ -> ())
    // A let binding is a value-forming boundary. A wildcard `_` is a hole, not a
    // value: it is only meaningful as a compound-index coordinate (consumed by
    // dispatchAppOrIndex before it reaches here). If one survives into the bound
    // value, it has escaped and is an error -- reject cleanly at typecheck rather
    // than let it reach lowering.
    let rejectEscapedWildcard (tv: TypedExpr) : TypeResult<TypedExpr> =
        if exprContainsWildcard tv then
            Error (Other
                "wildcard `_` is not a value: it can only appear as a compound-index coordinate (e.g. B((a, _, c))), not bound in a let. A tuple carrying a hole like (a, _, c) has no meaning on its own.")
        else Ok tv
    match binding.Type with
    | Some annot ->
        let annotTy = lowerTypeExpr env annot
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
        if irTypeHasRaggedNoPrior annotTy then
            Error (Other "RaggedIdx requires at least one prior index in the array's index list: the ragged extent is a per-row function of the OUTER iteration position (formalism 4.4), so there is nothing for a leading RaggedIdx to vary over. Add an outer index, e.g. Array<T like Idx<n>, RaggedIdx<lens>>.")
        elif irTypeHasBadDistOrder annotTy then
            Error (Other "Dist order must be a compile-time integer >= 1 (a literal, `let static`, or static-function call): Dist<order, Elem like I1, ..., Ik>")
        elif irTypeHasTagWildcard annotTy then
            Error (TagWildcardNotParam
                       (match binding.Pattern.Kind with
                        | PatVar n -> sprintf "let binding '%s'" n
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
        let badAxis = irTypeUnknownAxisPath annotTy
        if badAxis.IsSome then
            Error (Other (unknownAxisPathMessage badAxis.Value))
        else
        let wreath = irTypeWreathLevels annotTy
        if wreath.IsSome then
            Error (OrbitStorageUnsupported
                       (wreath.Value,
                        (match binding.Pattern.Kind with
                         | PatVar n -> sprintf "let binding '%s'" n
                         | _ -> "let binding annotation")))
        else
        let badBound =
            boundedAggregateError env
                (match binding.Pattern.Kind with
                 | PatVar n -> sprintf "let binding '%s'" n
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
        | ExprKind.ExprLambda _ when (match annotTy with FuncElem _ -> false | _ -> true) ->
            inferExpr env binding.Value |> Result.bind (fun tv ->
                (match env.Subst.Resolve tv.Type with
                 | FuncElem (_, ret) -> unify env.Subst ret annotTy |> Result.map (fun () -> tv)
                 | _ -> Ok tv)
                |> Result.bind rejectEscapedWildcard)
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
            let elemZero =
                match arrTy.ElemType with
                | AnyPrimElem (ETInt32 | ETInt64) -> Some (mkExpr sp (ExprKind.ExprLit (LitInt 0L)))
                | AnyPrimElem ETBool -> Some (mkExpr sp (ExprKind.ExprLit (LitBool false)))
                | AnyPrimElem (ETFloat32 | ETFloat64) -> Some (mkExpr sp (ExprKind.ExprLit (LitFloat 0.0)))
                | AnyPrimElem (ETComplex64 | ETComplex128) ->
                    let z () = mkExpr sp (ExprKind.ExprLit (LitFloat 0.0))
                    Some (mkExpr sp (ExprKind.ExprApp (mkExpr sp (ExprKind.ExprVar "complex"), [z (); z ()])))
                | _ -> None
            let slotSurface (idx: IRIndexType) : TypeExpr option =
                if idx.Rank <> 1 || idx.IxKind <> IxKPlain then None else
                match idx.Tag with
                | Some tag -> Some (TyNamed (tag, []))
                | None ->
                    match idx.Extent with
                    | IRLit (IRLitInt n) -> Some (TyIdx (mkExpr sp (ExprKind.ExprLit (LitInt n))))
                    | _ -> None
            let slots = arrTy.IndexTypes |> List.map slotSurface
            (match elemZero, (if slots |> List.forall Option.isSome then Some (List.map Option.get slots) else None) with
             | Some zBody, Some idxTys ->
                 let params_ : LambdaParam list =
                     idxTys |> List.mapi (fun i _ -> { Name = sprintf "__zero_i%d" i; Type = None; Default = None; NameSpan = noSpan })
                 let former = mkExpr sp (ExprKind.ExprFor (ForArrays ([], Some (mkExpr sp (ExprKind.ExprRange idxTys))), [], None))
                 let lam = mkExpr sp (ExprKind.ExprLambda (params_, None, zBody))
                 let synth = mkExpr sp (ExprKind.ExprCompute (mkExpr sp (ExprKind.ExprBinOp (Elementwise, OpApply, former, lam))))
                 checkExpr env annotTy synth |> Result.bind (fun tv ->
                     rejectEscapedWildcard { tv with Type = annotTy })
             | _ ->
                 Error (Other "zero at this array annotation cannot be materialized: every axis must be a plain rank-1 index with a nominal name or a static extent, and the element type must be numeric, bool, or complex. Spell the fill explicitly (`for () in range<...> <@> lambda(...) -> <zero literal> |> compute`) for packed, ragged, or non-static shapes."))
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
            | None -> checkExpr env annotTy binding.Value
            | Some dst ->
                // UnitTarget lets an additive RHS convert its operands
                // straight into `dst`; convertScaleTo then finds nothing left
                // to do. It stays a fallback, not a replacement: an RHS that
                // is not an additive binop (a bare variable, a call) still
                // gets its single conversion here.
                checkExpr { env with UnitTarget = Some dst } (IR.stripUnits annotTy) binding.Value
                |> Result.bind (convertScaleTo env "assignment" dst)
        checkedValue |> Result.bind (fun tv ->
            // Prefer the annotation as the canonical type -- it can be more
            // specific than what the value synthesized to.
            rejectEscapedWildcard { tv with Type = annotTy })
    | None -> inferExpr env binding.Value |> Result.bind rejectEscapedWildcard

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
and inferRecArray (env: TypeEnv) (annot: TypeExpr) (annotTy: IRType) (def: RecArrayDef) (span: Span) : TypeResult<TypedExpr> =
    let synAt k = mkExpr span k
    match annotTy with
    | ArrayElem at when not at.IndexTypes.IsEmpty ->
        // Static leading extent (a deliberate bound).
        let extentOf (ix: IRIndexType) =
            match ix.Extent with
            | IRLit (IRLitInt n) -> Some n
            | _ -> None
        match extentOf at.IndexTypes.Head with
        | None ->
            Error (Other (sprintf "recursive array '%s': the leading (recursion) axis must have a static extent -- dynamic-extent recurrences are not supported" def.Name))
        | Some n when n < 1L ->
            Error (Other (sprintf "recursive array '%s': the recursion axis extent must be >= 1 (got %d)" def.Name n))
        | Some n ->
        // Element zero expression for the buffer pre-fill: Float/Int
        // literals, complex(0, 0) for complex elements. (Record/tuple
        // slices land with the IR-level alloc.)
        let zeroElem () =
            match env.Subst.Resolve at.ElemType with
            | IRTScalar ETFloat64 | IRTScalar ETFloat32 -> Ok (synAt (ExprLit (LitFloat 0.0)))
            | IRTScalar ETInt64 | IRTScalar ETInt32 -> Ok (synAt (ExprLit (LitInt 0L)))
            | IRTScalar ETComplex128 | IRTScalar ETComplex64 ->
                Ok (synAt (ExprApp (synAt (ExprVar "complex"),
                                    [synAt (ExprLit (LitFloat 0.0)); synAt (ExprLit (LitFloat 0.0))])))
            | _ -> Error (Other (sprintf "recursive array '%s': only Float/Int/Complex element types are supported (record/tuple slices land with the IR-level alloc)" def.Name))
        // Trailing (slice) axes must be static too -- they drive the
        // desugared copy nest and the zero-fill literal.
        let trailingOpt = at.IndexTypes.Tail |> List.map extentOf
        if trailingOpt |> List.exists Option.isNone then
            Error (Other (sprintf "recursive array '%s': all slice-axis extents must be static" def.Name))
        else
        let trailing = trailingOpt |> List.map Option.get
        zeroElem () |> Result.bind (fun zed ->
        let bufName = sprintf "__rec_%s" def.Name
        let bufVar = synAt (ExprVar bufName)
        let iLit (v: int64) = synAt (ExprLit (LitInt v))
        // Buffer pre-fill, checked against the DECLARED annotation (keeps
        // named index types / units authoritative). Small buffers use a
        // nested zero literal; large ones (QG-scale trajectories) would
        // explode the AST as literals (millions of nodes), so they
        // materialize through a zero FORMER over the annotation's own
        // slots -- method_for(range<slots...>) <@> lambda(...) -> 0.
        // Zero fill over a list of axes -- the whole buffer (leading :: trailing)
        // or, for the zero-history slices below, the trailing axes alone.
        let annotSlots = match annot with TyArray (_, slotTys) -> Some slotTys | _ -> None
        let zerosOver (tag: string) (slotsOpt: TypeExpr list option) (exts: int64 list) =
            let total = List.fold (fun a b -> a * b) 1L exts
            match slotsOpt with
            | Some slotTys when total > 4096L ->
                let ps : LambdaParam list =
                    slotTys |> List.mapi (fun i _ -> { Name = sprintf "__%s%d_%s" tag i def.Name; Type = None; Default = None; NameSpan = noSpan })
                synAt (ExprCompute (synAt (ExprBinOp (Elementwise, OpApply,
                    synAt (ExprMethodFor [synAt (ExprRange slotTys)]),
                    synAt (ExprLambda (ps, None, zed))))))
            | _ ->
                List.foldBack (fun ext inner -> synAt (ExprArrayLit (List.replicate (int ext) inner)))
                              exts zed
        let zerosValue = zerosOver "z" annotSlots (n :: trailing)
        let bufLet = StmtLet { Mutability = BindMut; Pattern = mkPat span (PatVar bufName); Type = Some annot; Value = zerosValue }
        // Write one slice into the buffer at step position `stepIdxE`.
        // Rank-1: direct scalar assign. Rank >= 2: materialize the slice
        // once (`let __slice = e`), then a nested elementwise copy over the
        // trailing axes -- the same shape the imperative double-buffer
        // corpus used, so the whole machinery downstream is proven.
        let sliceCopyStmts (stepIdxE: Expr) (srcName: string) (srcExpr: Expr) : Stmt list =
            match trailing with
            | [] -> [ StmtExpr (synAt (ExprAssign (synAt (ExprApp (bufVar, [stepIdxE])), srcExpr))) ]
            | _ ->
                let ivars = trailing |> List.mapi (fun k _ -> sprintf "__ri%d_%s" k def.Name)
                let idxEs = ivars |> List.map (fun v -> synAt (ExprVar v))
                let srcVar = synAt (ExprVar srcName)
                let assign =
                    StmtExpr (synAt (ExprAssign (
                        synAt (ExprApp (bufVar, stepIdxE :: idxEs)),
                        synAt (ExprApp (srcVar, idxEs)))))
                let nest =
                    List.foldBack2 (fun ivar ext inner ->
                        [ StmtForIn (ivar, synAt (ExprKind.ExprDotDot (iLit 0L, iLit ext)), inner) ])
                        ivars trailing [assign]
                StmtLet { Mutability = BindLet; Pattern = mkPat span (PatVar srcName); Type = None; Value = srcExpr } :: nest
        // Seed (extent-1) arm: slice write at step 0 with the step ordinal
        // substituted to the literal 0.
        let seedStmts, loopStart =
            match def.SeedArm with
            | Some (seedStep, seedExpr) ->
                let seeded = Blade.Unfold.substFree (Map.ofList [seedStep, iLit 0L]) seedExpr
                sliceCopyStmts (iLit 0L) (sprintf "__seed_%s" def.Name) seeded, 1L
            | None -> [], 0L
        // Implicit zero history (formalism 7.5; monadic zero 10.4): a prefix
        // read OUTSIDE the prefix built so far resolves to the element
        // type's zero (mirrors the identity base case of recursive kernels,
        // 8.2), so `prefix(n - 3)` at n < 3 IS zero by specification and
        // callers don't hand-guard it. Must be ENFORCED, not inherited from
        // the buffer's zero pre-fill: the desugared read `__rec_x[n - 3]`
        // indexes BEFORE the buffer at n < 3, yielding garbage. Reads
        // provably inside [0, n) keep their bare, branch-free form.
        let stepVarE = synAt (ExprVar def.StepVar)
        let isStepVar (e: Expr) =
            match e.Kind with ExprKind.ExprVar v -> v = def.StepVar | _ -> false
        // Which of the two bounds can this leading index actually violate?
        // Recognises the index shapes a recursion produces (n, n - c, n + c, a
        // constant); anything else is guarded on both sides.
        let guardsFor (idx: Expr) : bool * bool =
            match idx.Kind with
            | _ when isStepVar idx -> false, true
            | ExprKind.ExprBinOp (_, OpSub, l, { Kind = ExprKind.ExprLit (LitInt c) }) when isStepVar l ->
                // idx = n - c over n in [loopStart, N): least value is
                // loopStart - c, and idx < n exactly when c > 0.
                (loopStart - c < 0L), (c <= 0L)
            | ExprKind.ExprBinOp (_, OpAdd, l, { Kind = ExprKind.ExprLit (LitInt c) }) when isStepVar l ->
                (loopStart + c < 0L), (c >= 0L)
            | ExprKind.ExprLit (LitInt c) ->
                // Constant index: inside the prefix at every step iff
                // 0 <= c < loopStart.
                (c < 0L), (c >= loopStart)
            | _ -> true, true
        // Nest the (at most two) bounds rather than conjoining them, so every
        // emitted condition stays a single comparison.
        let guardWrap needsLo needsHi (idx: Expr) (inPrefix: Expr) (outside: Expr) =
            let hi =
                if needsHi then synAt (ExprIf (synAt (ExprBinOp (Elementwise, OpLt, idx, stepVarE)), inPrefix, outside))
                else inPrefix
            if needsLo then synAt (ExprIf (synAt (ExprBinOp (Elementwise, OpGe, idx, iLit 0L)), hi, outside))
            else hi
        // Zero SLICES for guarded partial reads, one per read depth (a rank-3
        // family can be read as a rank-2 row or a rank-1 line). Loop-invariant,
        // so they bind at block level; allocated only if some read needs one.
        let zeroSlices = ResizeArray<int * string>()
        let zeroSliceFor (dropped: int) : Expr =
            let name =
                match zeroSlices |> Seq.tryFind (fun (d, _) -> d = dropped) with
                | Some (_, nm) -> nm
                | None ->
                    let nm = sprintf "__zs%d_%s" dropped def.Name
                    zeroSlices.Add (dropped, nm)
                    nm
            synAt (ExprVar name)
        // Rank >= 2: PARTIAL prefix reads (lag rows, e.g. prefix(n-1) on a
        // rank-2 family) must flow through a BINDING so CodeGen's dense
        // row-view wrap (densePartialSubview) fires -- a bare partial read in
        // argument position renders as a raw row pointer. Hoist each
        // distinct partial application into a loop-body let; scalar reads
        // (full applications) stay in place.
        let rewritePrefixReads (slice: Expr) : (string * Expr) list * Expr =
            let hoisted = ResizeArray<string * string * Expr>()  // key, name, value
            let emit (key: string) (value: unit -> Expr) =
                match hoisted |> Seq.tryFind (fun (k2, _, _) -> k2 = key) with
                | Some (_, nm, _) -> nm
                | None ->
                    let nm = sprintf "__lag%d_%s" hoisted.Count def.Name
                    hoisted.Add (key, nm, value ())
                    nm
            let slice' =
                Blade.Unfold.mapExprPre (fun x ->
                    match x.Kind with
                    | ExprKind.ExprApp ({ Kind = ExprKind.ExprVar p }, args)
                            when p = def.PrefixVar && not args.IsEmpty ->
                        let idx = args.Head
                        let rest = args.Tail
                        let needsLo, needsHi = guardsFor idx
                        let isPartial = args.Length <= trailing.Length
                        let key = sprintf "%A" (args |> List.map (fun a -> a.Kind))
                        if not (needsLo || needsHi) then
                            // Provably inside the prefix -- unchanged.
                            if isPartial then Some (synAt (ExprVar (emit key (fun () -> synAt (ExprApp (bufVar, args))))))
                            else None
                        else
                            // Read through a CLAMPED index: merely FORMING an
                            // out-of-range row view is undefined, so the
                            // discarded branch must stay in bounds too.
                            let safeIdx = guardWrap needsLo needsHi idx idx (iLit 0L)
                            let read = synAt (ExprApp (bufVar, safeIdx :: rest))
                            if isPartial then
                                // Only the READ is hoisted (that is what the
                                // row-view wrap keys off). The select stays
                                // INLINE: a binding whose value is an
                                // array-valued `if` is declared as a raw
                                // element pointer, not an Array<T,N>.
                                let raw = emit key (fun () -> read)
                                Some (guardWrap needsLo needsHi idx (synAt (ExprVar raw)) (zeroSliceFor rest.Length))
                            else Some (guardWrap needsLo needsHi idx read zed)
                    | _ -> None) slice
            (hoisted |> Seq.map (fun (_, nm, v) -> (nm, v)) |> List.ofSeq), slice'
        // Inductive arm: for n in start..N, prefix reads become buffer reads
        // (partials via hoisted row-view lets, scalars in place).
        let lagLets, sliceHoisted = rewritePrefixReads def.SliceExpr
        let slice' = Blade.Unfold.substFree (Map.ofList [def.PrefixVar, bufVar]) sliceHoisted
        let lagStmts =
            lagLets |> List.map (fun (nm, rd) ->
                StmtLet { Mutability = BindLet; Pattern = mkPat span (PatVar nm); Type = None; Value = rd })
        let loop =
            StmtForIn (def.StepVar,
                       synAt (ExprKind.ExprDotDot (iLit loopStart, iLit n)),
                       lagStmts @ sliceCopyStmts (synAt (ExprVar def.StepVar)) (sprintf "__slice_%s" def.Name) slice')
        // Zero slices consumed by out-of-prefix lag reads. Annotated from the
        // declared slot list so their index tags match the row views they
        // alternate with.
        let zeroSliceStmts =
            zeroSlices
            |> Seq.map (fun (dropped, nm) ->
                let slotsOpt =
                    annotSlots |> Option.bind (fun s ->
                        if s.Length > 1 + dropped then Some (List.skip (1 + dropped) s) else None)
                let tyOpt =
                    match annot, slotsOpt with
                    | TyArray (elemT, _), Some slots -> Some (TyArray (elemT, slots))
                    | _ -> None
                StmtLet { Mutability = BindLet; Pattern = mkPat span (PatVar nm); Type = tyOpt
                          Value = zerosOver "zs" slotsOpt (List.skip dropped trailing) })
            |> List.ofSeq
        let block = synAt (ExprBlock ((bufLet :: zeroSliceStmts) @ seedStmts @ [loop], Some bufVar))
        checkExpr env annotTy block |> Result.map (fun tv -> { tv with Type = annotTy }))
    | _ ->
        Error (Other (sprintf "recursive array '%s': the annotation must be an Array type (`let rec %s: Array<T like Step, ...> = ...`)" def.Name def.Name))

/// Bind the primary name of a let binding (single name or placeholder) with
/// let-generalization. Only ReadOnly bindings are generalized -- assignable
/// bindings could be reassigned, making the original scheme unsound.
/// For destructuring patterns (tuple/cons/struct), the caller passes name="_"
/// and identity=None; the returned env then needs further extension with the
/// pattern's sub-names by the caller (which differs by path).
and bindLetPatVar (env: TypeEnv) (name: string) (identity: ArrayIdentity option)
                  (assign: Assignability) (tValue: TypedExpr) : IRId * TypeEnv =
    let varId = env.Builder.FreshId()
    let scheme =
        if assign <> ReadOnly then None
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
        let headTys = List.init k (fun _ -> env.Subst.Fresh())
        let tailArityName = sprintf "r%d" (env.Builder.FreshId())
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
                        let isPoly = match resolvedTy with IRTPoly _ -> true | _ -> false
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

and inferMatch env scrutinee cases : TypeResult<TypedExpr> =
    inferExpr env scrutinee |> Result.bind (fun tScrutinee ->
        let resultTy = env.Subst.Fresh()
        cases |> List.map (fun case ->
            checkPattern env tScrutinee.Type case.Pattern |> Result.bind (fun tPat ->
                // Extend env with pattern bindings
                let mutable caseEnv = env
                for (name, varId, ty) in tPat.Bindings do
                    caseEnv <- bindVarSimple name varId ty caseEnv

                // Type-check guard
                let tGuard =
                    case.Guard |> Option.map (fun g ->
                        inferExpr caseEnv g |> Result.map Some)
                    |> Option.defaultValue (Ok None)

                tGuard |> Result.bind (fun guardOpt ->
                inferExpr caseEnv case.Body |> Result.bind (fun tBody ->
                    let _ = unify env.Subst tBody.Type resultTy
                    Ok ({ Pattern = tPat; Guard = guardOpt; Body = tBody } : TypedMatchCase)))))
        |> sequenceResults |> Result.map (fun tCases ->
            let resolvedTy = env.Subst.Resolve resultTy
            mkTyped (TExprMatch (tScrutinee, tCases)) resolvedTy))

/// Bidirectional match: push the `expected` type into each arm body via
/// `checkExpr`, so a literal (or scalar) arm can flex to the expected
/// `T`/`T^k` instead of pinning the result to its concrete type -- this is what
/// lets `comoment_prod`'s `| 0 -> 1` defer into the `-> T^1` return. Falls back
/// to plain inference on a body-check failure, preserving `inferMatch`'s lenient
/// cross-unify (which ignored arm/result mismatches).
and checkMatch env (expected: IRType) scrutinee cases : TypeResult<TypedExpr> =
    inferExpr env scrutinee |> Result.bind (fun tScrutinee ->
        cases |> List.map (fun case ->
            checkPattern env tScrutinee.Type case.Pattern |> Result.bind (fun tPat ->
                let mutable caseEnv = env
                for (name, varId, ty) in tPat.Bindings do
                    caseEnv <- bindVarSimple name varId ty caseEnv
                let tGuard =
                    case.Guard |> Option.map (fun g -> inferExpr caseEnv g |> Result.map Some)
                    |> Option.defaultValue (Ok None)
                tGuard |> Result.bind (fun guardOpt ->
                    let tBodyR =
                        match checkExpr caseEnv expected case.Body with
                        | Ok tb -> Ok tb
                        | Error _ ->
                            inferExpr caseEnv case.Body |> Result.map (fun tb ->
                                unify env.Subst tb.Type expected |> ignore
                                tb)
                    tBodyR |> Result.map (fun tBody ->
                        ({ Pattern = tPat; Guard = guardOpt; Body = tBody } : TypedMatchCase)))))
        |> sequenceResults |> Result.map (fun tCases ->
            mkTyped (TExprMatch (tScrutinee, tCases)) (env.Subst.Resolve expected)))

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

and inferBlock env stmts finalExpr (expectedFinal: IRType option) : TypeResult<TypedExpr> =
    let mutable curEnv = env
    let mutable err : TypeError option = None
    let mutable typedStmts : TypedStmt list = []

    for stmt in stmts do
        if err.IsNone then
            // Unwrap the parser's span annotation and stamp the statement's
            // location for error reporting (see currentStmtSpanStorage).
            let stmt =
                match stmt with
                | StmtSpanned (inner, sp) -> setCurrentStmtSpan sp; inner
                | s -> s
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
                let selfInfo =
                    match binding.Mutability, binding.Pattern.Kind, binding.Value.Kind with
                    | BindConst, PatternKind.PatVar n, ExprKind.ExprLambda (lamParms, _, lamBody)
                          when Set.contains n
                                   (collectFreeVars (lamParms |> List.map (fun p -> p.Name) |> Set.ofList) lamBody) ->
                        let ty = curEnv.Subst.Fresh()
                        let id = curEnv.Builder.FreshId()
                        Some (n, ty, id)
                    | _ -> None
                let inferEnv =
                    match selfInfo with
                    | Some (n, ty, id) -> bindVarSimple n id ty curEnv
                    | None -> curEnv
                match inferLetBindingValue inferEnv binding with
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
                        | _ -> (tValue0, curEnv.Builder.FreshId())
                    let name = match binding.Pattern.Kind with PatternKind.PatVar n -> n | _ -> "_"
                    let identity = match binding.Pattern.Kind with PatternKind.PatVar n -> Some (AIDVariable n) | _ -> None
                    let assign = assignOfBindingMut binding.Mutability
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
                        Identity = identity; IsMutable = (assign <> ReadOnly)
                        Value = (if isDeferredFormer then mkTyped (TExprLit LitUnit) IRTUnit else tValue)
                        SubBindings = subBindings |> List.map (fun (n, id, ty) -> (n, id, curEnv.Subst.Resolve ty))
                        Destructure = destructure
                        PostChecks = postChecks
                    }
                    typedStmts <- typedStmts @ [TStmtLet tb]
                | Error e -> err <- Some e
            | StmtAssign (lhs, _, rhs) ->
                match inferExpr curEnv lhs, inferExpr curEnv rhs with
                | Ok tL, Ok tR ->
                    // Constrained-struct target: guard after the store.
                    let assignChecks () =
                        match structChecksForAssign curEnv lhs tL with
                        | Ok cs -> cs |> List.map TStmtExpr
                        | Error e -> err <- Some e; []
                    // Check assignability of LHS
                    match tL.Kind with
                    | TExprVar (name, _, _) ->
                        match lookupVar name curEnv with
                        | Some info when info.Assign = ReadOnly ->
                            err <- Some (ImmutableStaticAssign name)
                        | _ ->
                            let _ = unify curEnv.Subst tL.Type tR.Type
                            typedStmts <- typedStmts @ [TStmtAssign (tL, tR)] @ assignChecks ()
                    | _ ->
                        let _ = unify curEnv.Subst tL.Type tR.Type
                        typedStmts <- typedStmts @ [TStmtAssign (tL, tR)] @ assignChecks ()
                | Error e, _ | _, Error e -> err <- Some e
            | StmtExpr e ->
                match inferExpr curEnv e with
                | Ok tE -> typedStmts <- typedStmts @ [TStmtExpr tE]
                | Error e -> err <- Some e
            | StmtForIn (varName, rangeExpr, bodyStmts) ->
                match inferForIn curEnv varName rangeExpr bodyStmts with
                | Ok tStmt -> typedStmts <- typedStmts @ [tStmt]
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
                mkTyped (TExprBlock (typedStmts, Some tF)) tF.Type)
        | None -> Ok (mkTyped (TExprBlock (typedStmts, None)) IRTUnit)

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
        pats |> List.iteri (fun i p ->
            match p.Kind with
            | PatternKind.PatVar n ->
                let eTy =
                    if i < typeList.Length then e.Subst.Resolve(typeList.[i])
                    else e.Subst.Fresh()
                bindLeaf n eTy
            | _ -> bindCompound p)
        Ok (e, subs, DSPositional)
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
            let varId = env.Builder.FreshId()
            let loopEnv = bindVarSimple varName varId (IRTScalar ETInt64) env
            let mutable bodyEnv = loopEnv
            let mutable bodyErr = None
            let mutable typedBodyStmts : TypedStmt list = []
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
                        match inferExpr bodyEnv binding.Value with
                        | Ok tValue ->
                            let bName = match binding.Pattern.Kind with PatternKind.PatVar n -> n | _ -> "_"
                            let bId = bodyEnv.Builder.FreshId()
                            let assign = assignOfBindingMut binding.Mutability
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
                                Identity = None; IsMutable = (assign <> ReadOnly); Value = tValue
                                SubBindings = subBindings |> List.map (fun (n, id, ty) -> (n, id, bodyEnv.Subst.Resolve ty))
                                Destructure = destructure; PostChecks = []
                            }
                            typedBodyStmts <- typedBodyStmts @ [TStmtLet tb]
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
                            typedBodyStmts <- typedBodyStmts @ [TStmtAssign (tL, tR)] @ checks
                        | Error e, _ | _, Error e -> bodyErr <- Some e
                    | StmtExpr e ->
                        match inferExpr bodyEnv e with
                        | Ok tE -> typedBodyStmts <- typedBodyStmts @ [TStmtExpr tE]
                        | Error e -> bodyErr <- Some e
                    | StmtForIn (v2, range2, body2) ->
                        match inferForIn bodyEnv v2 range2 body2 with
                        | Ok tStmt -> typedBodyStmts <- typedBodyStmts @ [tStmt]
                        | Error e -> bodyErr <- Some e
            match bodyErr with
            | Some e -> Error e
            | None -> Ok (TStmtForIn (varName, varId, tLo, tHi, typedBodyStmts))
    | _ -> Error (Other "for-in range must use a..b syntax")

and inferMethodFor env arrays : TypeResult<TypedExpr> =
    // Detect method_for(zip(A, B, ...)) -- expand zip into co-iteration
    match arrays with
    | [{ Kind = ExprKind.ExprZip zipExprs }] ->
        zipExprs |> List.map (inferExpr env) |> sequenceResults |> Result.bind (fun tZipArrays ->
            let identities = zipExprs |> List.map (fun arr ->
                match arr.Kind with ExprKind.ExprVar name -> AIDVariable name | _ -> AIDLiteral (env.Builder.FreshId()))
            // Resolved shape, defaulted element -- see loopOperandArrayType. This
            // is the arm a compound zip operand reaches (`(A - m) * (B - m)`).
            let arrayTypes = tZipArrays |> List.mapi (fun i ta ->
                loopOperandArrayType env (fun () -> getArrayType env zipExprs.[i]) ta.Type)
            // Shared iteration records. Single-record operands (dense rank-1 or
            // packed symmetric) use the first-record rule unchecked.
            // MULTI-record operands (dense rank >= 2) co-iterate the FULL product
            // of records -- all operands must agree structurally and every record
            // must be plain dense. buildApplyInfo trims the co-iterated prefix
            // by the kernel's slice rank, so row-mode kernels (loops/085) keep
            // receiving their inner-record slice.
            //
            // GROUPED operands are the one non-dense co-iteration
            // (zipSharedRecords' isGroupedRaggedShape arm): the rows line up
            // one-to-one and the kernel takes one row per operand. That is
            // only meaningful when every operand was grouped by the SAME keys
            // -- one offsets table drives every row -- and the types cannot
            // say so, since two independent `group_keys` calls produce
            // structurally identical records. Discharge it here on the
            // EXPRESSIONS: chase each operand to its `group_by(vals, gk)` and
            // require the `gk` operands to resolve (through any number of
            // alias hops) to the same binding.
            let groupKeysOperandOf (ta: TypedExpr) : TypedExpr option =
                match (resolveTypedExprDeep env ta).Kind with
                | TExprGroupBy (_, gkExpr) -> Some (resolveTypedExprDeep env gkExpr)
                | _ -> None
            let allGrouped = arrayTypes |> List.forall (fun at -> isGroupedRaggedShape at.IndexTypes)
            let sameGroupKeysBinding () =
                match tZipArrays |> List.map groupKeysOperandOf with
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
                Error (Other "co-iterating grouped arrays requires every operand to be grouped by the SAME group_keys binding (one offsets table drives the shared row walk). Bind the keys once (`let gk = group_keys(...)`) and pass that same `gk` to each group_by; grouping each operand with its own group_keys call gives two independent partitions with no row correspondence.")
            else
            match zipSharedRecords arrayTypes with
            | Error e -> Error e
            | Ok sharedRecords ->
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
            Ok (mkTyped (TExprMethodFor info) loopTy))
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
        | [single] when (match (resolveTypedExpr env single).Kind with TExprZip _ -> true | _ -> false) ->
            let resolved = resolveTypedExpr env single
            match resolved.Kind with
            | TExprZip zipExprs ->
                let identities = zipExprs |> List.map (fun te ->
                    match te.Kind with
                    | TExprVar (name, _, _) -> AIDVariable name
                    | _ -> AIDLiteral (env.Builder.FreshId()))
                // Same stale-IRTInfer hazard as the zip arm above.
                let arrayTypes = zipExprs |> List.map (fun te ->
                    loopOperandArrayType env
                        (fun () -> { ElemType = IRTScalar ETFloat64; IndexTypes = []; IsVirtual = false; Identity = None })
                        te.Type)
                match zipSharedRecords arrayTypes with
                | Error e -> Error e
                | Ok sharedRecords ->
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
                 && (tArrays |> List.exists (fun ta ->
                        match (resolveTypedExpr env ta).Kind with TExprZip _ -> true | _ -> false)) ->
            Error (Other zipInMultiArrayPackMsg)
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
               && (leafArrays |> List.exists (fun ta ->
                      match (resolveTypedExprDeep env ta).Kind with TExprZip _ -> true | _ -> false)) then
                Error (Other zipInMultiArrayPackMsg)
            else
            let identities = leafArrays |> List.map (fun ta ->
                match ta.Kind with
                | TExprVar (name, _, _) -> AIDVariable name
                | _ -> AIDLiteral (env.Builder.FreshId()))
            // No surface expr to fall back on (a node may have come out of a
            // tuple VALUE), so the defaulted-shape thunk stands in -- the same
            // one the let-bound-zip arm above uses for exactly this reason.
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
        let identities = arrays |> List.map (fun arr ->
            match arr.Kind with ExprKind.ExprVar name -> AIDVariable name | _ -> AIDLiteral (env.Builder.FreshId()))
        // S1 SEAM 3, method_for orientation (see the object_for site for the
        // lambda-bodies-only rule and the regression that bought it): a
        // caret-shorthand `T^k` operand is an arity-constrained var, so
        // loopOperandArrayType takes its `fallback ()` branch and the loop
        // iterates a shape nobody declared. Supply the forced shape first.
        if env.InLambdaBody then
            tArrays |> List.iter (fun ta -> materializeArityVar env ta "method_for")
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
    // A bare named-function reference used as an object_for kernel is
    // eta-expanded to lambda(__k..) -> f(__k..), so `object_for(lkm) <@> ...`
    // works symmetrically with `method_for(...) <@> lkm`.
    let kernelResult =
        match etaExpandFunctionKernel env kernel with
        | Some r -> r
        | None -> inferExpr env kernel
    kernelResult |> Result.bind (fun tKernel ->
        // A DEFERRED former: a bare named reference to an arity-polymorphic
        // function. etaExpandFunctionKernel refused to eta-expand it (its Poly
        // pack param makes the arity unknown), so it is still a TExprVar. Mark
        // the loop arity-polymorphic (Arity = None) and eta-expand at the <@>
        // site, where the argument tuple reveals the pack width.
        let isDeferredPoly =
            match tKernel.Kind, env.Subst.Resolve tKernel.Type with
            | TExprVar _, FuncElem (paramTys, _) ->
                paramTys |> List.exists (fun t ->
                    match env.Subst.Resolve t with IRTPoly _ -> true | _ -> false)
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

and inferStructConstruction env name fields (spread: Expr option) : TypeResult<TypedExpr> =
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
            Error (MutualBindJointly (n, describe, (groupNames |> List.map (fun s -> s.ToLower()) |> String.concat ", ")))
        | TyTuple elems ->
            let directNames =
                elems |> List.choose (function
                    | TyNamed (n, []) when env.MutualMembers.ContainsKey n -> Some n
                    | _ -> None)
            if directNames.Length <> allOccurrences.Length then
                Error (MutualDirectElementsOnly describe)
            elif directNames |> List.exists (fun n -> env.MutualMembers.[n] <> groupId) then
                Error MutualMixedGroups
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
            let msg = sprintf "Mutual constraint violation (%s)" group.GroupId
            (checkId, mkTypedSpan (TExprConstraintCheck (tCond, msg)) IRTUnit tCond.Span)))
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
                        pats |> List.forall (fun p -> match p.Kind with PatternKind.PatVar _ -> true | _ -> false) ->
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
                            match env.MutualReturnFuncs.TryGetValue f with
                            | true, gid -> gid = group.GroupId
                            | _ -> false
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
                        if constraints.Length = 1 then sprintf "Constraint violation in %s" sname
                        else sprintf "Constraint violation in %s (conjunct %d)" sname (i + 1)
                    mkTypedSpan (TExprConstraintCheck (tCond, msg)) IRTUnit tCond.Span))
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
                    let msg = sprintf "Bound violation in '%s' (%s)" subjectName side
                    mkTypedSpan (TExprConstraintCheck (tCond, msg)) IRTUnit tCond.Span))
            |> sequenceResults

/// Resolve a surface type through ORDINARY alias chains. Distinct from
/// `lowerTypeExpr`'s resolution, which answers in IRType and has therefore
/// already thrown the bounds away -- this keeps the surface node, which is the
/// only place the bound EXPRESSIONS survive. Fuel-bounded like the
/// elaborators' `resolveTop`.
and private resolveSurfaceAlias (env: TypeEnv) (fuel: int) (ty: TypeExpr) : TypeExpr =
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
and private elementBoundOf (env: TypeEnv) (annot: TypeExpr) : (Expr option * Expr option * int) option =
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
and private synthesizeElementBoundChecks (env: TypeEnv) (annot: TypeExpr) (subjectName: string) (targetSurface: Expr) : TypeResult<TypedExpr list> =
    match elementBoundOf env annot with
    | None -> Ok []
    | Some (_, _, depth) when depth > 1 ->
        Error (Other (sprintf "the bound on the element type of '%s' is not enforced: it sits %d array levels deep, and the guard synthesis walks one. Flatten to a single Array<T<min=.., max=..> like I, J, ...> annotation, whose elements ARE checked."
                              subjectName depth))
    | Some (lo, hi, _) ->
        inferExpr env targetSurface |> Result.bind (fun tSubj ->
        match env.Subst.Resolve tSubj.Type with
        | ArrayElem arrTy ->
            let exotic =
                arrTy.IndexTypes |> List.tryFind (fun ix ->
                    ix.IxKind <> IxKPlain || ix.Symmetry <> SymNone)
            match exotic with
            | Some ix ->
                Error (Other (sprintf "the bound on the element type of '%s' is not enforced: the array has a %s axis, whose stored cells are not a dense loop nest (folding canonical vs logical cells differs, and ragged/compound axes have no rectangular extent). Bound the element of a dense array, or check the values explicitly."
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
                    (sprintf "__ebnd_%s_%d" subjectName k, env.Builder.FreshId()))
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
                inferExpr env bexpr |> Result.map (fun tB ->
                    let cond =
                        if isLo then mkT (TExprBinOp (Elementwise, OpLe, tB, elemRead)) (IRTScalar ETBool)
                        else mkT (TExprBinOp (Elementwise, OpLe, elemRead, tB)) (IRTScalar ETBool)
                    let msg = sprintf "Bound violation in an element of '%s' (%s)" subjectName side
                    TStmtExpr (mkT (TExprConstraintCheck (cond, msg)) IRTUnit)))
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

/// Wrap a declared-return function body so the joint check runs at the
/// return -- the group's single verification point. The body becomes:
///   let __mg_ret = <body>
///   let __mg<i> = __mg_ret.<i>   (one per member, at its annotation slot)
///   <conjunct checks>
///   __mg_ret
and wrapMutualReturnBody (env: TypeEnv) (retAnnot: TypeExpr) (group: MutualGroupInfo) (tBody: TypedExpr) : TypeResult<TypedExpr> =
    let retTy = env.Subst.Resolve tBody.Type
    let memberSlots =
        match retAnnot with
        | TyTuple elems ->
            elems |> List.mapi (fun i t -> (i, t))
            |> List.choose (fun (i, t) ->
                match t with
                | TyNamed (n, []) when group.Members |> List.exists (fun (m, _) -> m = n) -> Some (n, i)
                | _ -> None)
        | _ -> []
    if memberSlots.Length <> group.Members.Length then
        Error (MutualReturnTupleElements (group.Members |> List.map fst |> String.concat ", "))
    else
        let rid = env.Builder.FreshId()
        let retVar = mkTyped (TExprVar ("__mg_ret", rid, None)) retTy
        let leaves =
            memberSlots |> List.map (fun (mname, slot) ->
                let kind = group.Members |> List.find (fun (m, _) -> m = mname) |> snd
                let mTy = match kind with MMStruct s -> IRTNamed s | MMScalar t -> t
                (mname, slot, env.Builder.FreshId(), sprintf "__mg%d" slot, mTy))
        let mutable checkEnv = env
        for (_, _, leafId, leafName, mTy) in leaves do
            checkEnv <- bindVarSimple leafName leafId mTy checkEnv
        let mapping = leaves |> List.map (fun (m, _, _, leafName, _) -> (m, leafName)) |> Map.ofList
        synthesizeMutualChecks checkEnv group mapping |> Result.map (fun checks ->
            let intLit i = mkTyped (TExprLit (LitInt (int64 i))) (IRTScalar ETInt64)
            let checkStmts = checks |> List.map (fun (_, c) -> TStmtExpr c)
            let inner = mkTyped (TExprBlock (checkStmts, Some retVar)) retTy
            let withLeaves =
                List.foldBack (fun (_, slot, leafId, leafName, mTy) acc ->
                    let proj = mkTyped (TExprTupleIndex (retVar, intLit slot)) mTy
                    mkTyped (TExprLet (leafName, leafId, proj, acc)) retTy) leaves inner
            mkTyped (TExprLet ("__mg_ret", rid, tBody, withLeaves)) retTy)

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
                | ArrayElem at when at.IsVirtual && at.IndexTypes.Length > 0 ->
                    // ALL of the in-clause's slots become shared iteration
                    // records -- `for (A, B) in range<Lat, Lon>` co-iterates the
                    // full LatxLon product space. Multi-slot spaces require
                    // every slot plain dense (a sole packed/compound slot is
                    // fine -- its Rank levels walk the flat canonical cells).
                    if coIterableRecords at.IndexTypes then Ok at.IndexTypes
                    else Error (Other "for (...) in range<...>: a multi-slot iteration space must consist of plain dense index types (a packed or compound slot cannot share the product with other slots)")
                | resolved ->
                    Error (Other (sprintf "the `in` clause of `for (...) in <source>` must be a virtual array (range<...>, reverse<...>, or blocked<...>) -- it supplies the shared iteration index; got %s. Drop the `in` clause to co-iterate stored arrays (for (A, B) == method_for(A, B)), or use method_for(zip(A, B)) to zip them." (ppIRType resolved)))
            sharedRecordsRes |> Result.bind (fun sharedRecords ->

            let identities = arrays |> List.map (fun arr ->
                match arr.Kind with ExprKind.ExprVar name -> AIDVariable name | _ -> AIDLiteral (env.Builder.FreshId()))
            // For co-iteration, all arrays use the shared index space
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
                            Error (Other (sprintf "for (...) in co-iteration kernel takes %d parameter(s) (one per co-iterated array) or %d (values plus the %d shared iteration indices), got %d" nOperands (nOperands + idxParamCount) idxParamCount lambdaInfo.Params.Length))
                        else
                        // Bind the kernel params to their iterated types: value
                        // params to the operands' ELEMENT types, index params to
                        // the tagged Nat<...> slot types. The body was inferred
                        // against unresolved vars, so deferred intrinsics (e.g.
                        // imag(zp) on a complex operand) only resolve correctly
                        // once the params are unified here -- the same post-body
                        // unification buildApplyInfo performs for <@> applies.
                        let paramUnifyResult =
                            let valueTypes = arrayTypes |> List.map (fun at -> at.ElemType)
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

// 11. Declaration Type Checking

and checkDecl (env: TypeEnv) (decl: Decl) : TypeResult<TypedDecl * TypeEnv> =
    // Fresh declaration: clear any statement span left over from the previous
    // decl's body so errors here can't inherit a stale location (section 3.4).
    resetCurrentStmtSpan ()
    match decl with
    | DeclLet binding ->
        // Top-level let: shares value-resolution and primary-name binding with
        // inferLetBinding (the let-as-expression form). Diverges from there in
        // that we surface destructured sub-vars to Lowering and wrap in a
        // TypedBinding rather than recursing into a body expression.
        inferLetBindingValue env binding |> Result.bind (fun tValue ->
            let name = match binding.Pattern.Kind with PatternKind.PatVar n -> n | _ -> "_"
            // Provider load (e.g. `let sample = NetCDF.load("f.nc")`): resolve the
            // module's real struct type at compile time by reading the file
            // metadata, then register the dims/vars structs plus a top-level
            // module struct so field access like `sample.vars.temp` resolves to
            // the variable's real Array type rather than a fresh type var. This
            // mirrors the metadata read that Lowering.tryInvokeProvider performs;
            // the typed value SHAPE is left intact so the lowering-side provider
            // detection still fires. Ordinary (opaque) inference is the fallback
            // when the receiver is not a provider alias or the file can't be read.
            let (env, tValue) =
                match binding.Value.Kind with
                | ExprKind.ExprApp ({ Kind = ExprKind.ExprField ({ Kind = ExprKind.ExprVar alias }, "load") }, [{ Kind = ExprKind.ExprLit (LitString path) }]) ->
                    match providerAliasName env alias with
                    | None -> (env, tValue)
                    | Some pname ->
                        try
                            // Read the store metadata at compile time (the same read
                            // Lowering.tryInvokeProvider performs) and register the
                            // resulting struct types so `sample.vars.<v>` resolves.
                            let spec = (Blade.ProviderRegistry.tryFind pname).Value
                            let pm = spec.LoadAsModule env.Builder name path
                            // Record for the IDE hover path (Ide.collectProviderStores)
                            // so it never has to re-open the store.
                            Blade.ProviderRegistry.IdeStores.record name pm
                            let (envM, moduleTy) = registerProviderModule env name pm
                            (envM, { tValue with Type = moduleTy })
                        with _ -> (env, tValue)
                | _ -> (env, tValue)
            let identity = match binding.Pattern.Kind with PatternKind.PatVar n -> Some (AIDVariable n) | _ -> None
            let assign = assignOfBindingMut binding.Mutability
            let (varId, env') = bindLetPatVar env name identity assign tValue

            // Dist provenance seeding: a module-level dist binding gets its
            // source-array set from the PPL elaboration state (by name --
            // covers `let d = dist(A, r)` and the call-form combinators);
            // any other Dist-typed value derives conservatively from its
            // RHS (covers operator results: `let s = combine(dx, dy)` etc.).
            (match env.Subst.Resolve tValue.Type with
             | IRTDist _ ->
                 let prov =
                     match Blade.Ppl.Elaborate.Independence.distSources name with
                     | Some s -> s
                     | None -> provenanceOfSurface env binding.Value
                 if not (Set.isEmpty prov) then env.Provenance.[varId] <- prov
             | _ -> ())

            // Handle destructuring at top level -- collect sub-bindings for Lowering
            let mutable subBindings : (string * IRId * IRType) list = []
            // Shape tag handed to Lowering (see TypedAst.DestructureShape). Only
            // the PatCons arm below moves it off DSPositional.
            let mutable destructure = DSPositional
            // A destructuring pattern that cannot be satisfied by the scrutinee's
            // real type is a type ERROR, but the env-building fold below is a pure
            // expression with nowhere to return one from. Park it here and surface
            // it just before the binding is assembled; silently binding fresh type
            // vars instead is exactly how `head :: tail` would miscompile.
            let mutable patternError : TypeError option = None
            let env' =
                match binding.Pattern.Kind with
                | PatternKind.PatTuple pats ->
                    // Neither reading covers the names: park the error (the
                    // fold below is a pure expression with nowhere to return
                    // one from) rather than letting fresh vars paper over the
                    // overflow, which used to check OK and die in g++.
                    (match tupleDestructureArityError env pats tValue.Type with
                     | Some e -> patternError <- Some e
                     | None -> ())
                    // Resolve and determine which type list to use for binding
                    let resolvedTy = env.Subst.Resolve(tValue.Type)
                    let typeList =
                        match resolvedTy with
                        | IRTTuple ts ->
                            if pats.Length = ts.Length then
                                // Structural match: (w, z) against ((alpha,beta), gamma) -> w:(alpha,beta), z:gamma
                                ts
                            else
                                // Try flat match: (x, y, z) against ((alpha,beta), gamma) -> x:alpha, y:beta, z:gamma
                                let flat = IR.flattenTupleLeaves resolvedTy
                                if pats.Length = flat.Length then flat
                                else ts  // arity already parked above
                        | _ -> []
                    pats |> List.mapi (fun i p -> (i, p))
                    |> List.fold (fun e (i, p) ->
                        match p.Kind with
                        | PatternKind.PatVar n ->
                            let eTy =
                                if i < typeList.Length then env.Subst.Resolve(typeList.[i])
                                else env.Subst.Fresh()
                            let subId = env.Builder.FreshId()
                            subBindings <- subBindings @ [(n, subId, eTy)]
                            bindVarSimple n subId eTy e
                        | _ -> patternNames p |> List.fold (fun e2 n ->
                            let subId = env.Builder.FreshId()
                            let eTy = env.Subst.Fresh()
                            subBindings <- subBindings @ [(n, subId, eTy)]
                            bindVarSimple n subId eTy e2) e) env'
                | PatternKind.PatCons (h, t) ->
                    // `let head :: tail = tup` splits an n-tuple into element 0
                    // and the REMAINDER (`tail` is the (n-1)-tuple, not the
                    // single next element). Flatten/typing/reject rules live
                    // in consDestructureLeaves, shared with the block-scoped,
                    // `let static` and expression-position forms so the four
                    // sites cannot drift apart.
                    match consDestructureLeaves env tValue.Type h t with
                    | Error e ->
                        patternError <- Some e
                        env'
                    | Ok leaves ->
                        destructure <- DSConsRest
                        leaves
                        |> List.fold (fun e (n, ty) ->
                            let subId = env.Builder.FreshId()
                            subBindings <- subBindings @ [(n, subId, ty)]
                            bindVarSimple n subId ty e) env'
                | PatternKind.PatStruct (structName, fieldPats) ->
                    // Look up struct field types from the type definition
                    // (shared with the expression-position PatStruct arm so
                    // both read the field list from the same place).
                    let structFields = structFieldTypesOf env tValue.Type
                    let fieldTypeMap = Map.ofList structFields
                    fieldPats |> List.fold (fun e (fieldName, pat) ->
                        match pat.Kind with
                        | PatternKind.PatVar n ->
                            let subId = env.Builder.FreshId()
                            let eTy = Map.tryFind fieldName fieldTypeMap
                                      |> Option.defaultWith (fun () -> env.Subst.Fresh())
                            subBindings <- subBindings @ [(n, subId, eTy)]
                            bindVarSimple n subId eTy e
                        | _ -> patternNames pat |> List.fold (fun e2 n ->
                            let subId = env.Builder.FreshId()
                            let eTy = env.Subst.Fresh()
                            subBindings <- subBindings @ [(n, subId, eTy)]
                            bindVarSimple n subId eTy e2) e) env'
                | _ -> env'

            // Surface a destructuring pattern that the scrutinee's type cannot
            // satisfy (see patternError above) before anything else is built --
            // the sub-binding ids are already allocated at this point, but a
            // failed decl never reaches Lowering so they simply go unused.
            if patternError.IsSome then Error patternError.Value else
            // Mutual-group check-point: an annotation naming a group makes
            // this let the introduce-site (unless the RHS is a call already
            // checked at its declared return). Check IRIds allocate after
            // the sub-binding ids, so id-ordered emission runs them last.
            mutualBindingObligation env binding |> Result.bind (fun obligation ->
            let mutualChecksR =
                match obligation with
                | Some (group, memberToLeaf) -> synthesizeMutualChecks env' group memberToLeaf
                | None -> Ok []
            mutualChecksR |> Result.bind (fun mutualChecks ->
            // Constrained-struct binding: check at every assignment site.
            let structChecksR =
                match binding.Pattern.Kind with
                | PatternKind.PatVar n ->
                    // Plus the bounded-primitive annotation guards (section 2.4) --
                    // see synthesizeBoundChecks: bounds live only on the
                    // surface type, never on tValue.Type.
                    let subject = mkExpr binding.Pattern.Span (ExprVar n)
                    synthesizeStructChecks env' tValue.Type subject
                    |> Result.bind (fun sc ->
                        synthesizeBoundChecks env' binding.Type n subject
                        |> Result.map (fun bc -> sc @ bc))
                | _ -> Ok []
            structChecksR |> Result.map (fun structChecks ->
            let postChecks =
                mutualChecks @ (structChecks |> List.map (fun c -> (env.Builder.FreshId(), c)))
            // A DEFERRED former (arity-polymorphic object_for over a bare
            // poly kernel, Arity = None: pack width unknown until <@>) has no
            // standalone runtime value and cannot compose (>>@ needs a
            // concrete loop) -- every <@> use rebuilds it inline (inferApply).
            // Emit the binding INERT so it never reaches IR validation
            // carrying the unresolved element type; the env still holds the
            // real former (TypedValue) for <@> resolution. Concrete-arity
            // formers (Arity = Some n) stay real bindings even with a
            // generic element type: codegen's compose path chases their
            // IRVar back to the real IRObjectFor, so neutralizing them would
            // break `(o1 >>@ o2) <@> A`.
            let isDeferredFormer =
                match env.Subst.Resolve tValue.Type with
                | IRTLoop { Kind = LKObject; Arity = None } -> true
                | _ -> false
            let tb : TypedBinding = {
                Name = name; VarId = varId
                Type = (if isDeferredFormer then IRTUnit else env.Subst.Resolve(tValue.Type))
                Identity = identity; IsMutable = (assign <> ReadOnly)
                Value = (if isDeferredFormer then mkTyped (TExprLit LitUnit) IRTUnit else tValue)
                SubBindings = subBindings |> List.map (fun (n, id, ty) -> (n, id, env.Subst.Resolve ty))
                Destructure = destructure
                PostChecks = postChecks
            }
            (TDeclLet tb, env')))))

    | DeclStatic binding ->
        // Shared annotation handler so `let static` bindings enforce type
        // annotations like regular lets (`let static x: Float<meters> =
        // 100.0` would otherwise carry the synthesized type, dropping the
        // annotation silently).
        //
        // RESOLVED-VALUE SHORTCUT: when StaticEval already reduced this
        // binding to a value (env.StaticValues), type from that VALUE
        // rather than re-inferring the expression -- static-only builtins
        // (sh_spec, total_dim, tp_weight_dim, length, ...) have no runtime
        // binding, so inferring their call expressions would fail with
        // "unbound variable" even though the static evaluator handled them.
        let staticShortcut =
            match binding.Pattern.Kind with
            | PatternKind.PatVar n ->
                match Map.tryFind n env.StaticValues with
                | Some sv ->
                    let rec svToTyped (v: StaticEval.StaticValue) : TypedExpr =
                        match v with
                        | StaticEval.SVInt i -> mkTyped (TExprLit (LitInt i)) (IRTScalar ETInt64)
                        | StaticEval.SVFloat f -> mkTyped (TExprLit (LitFloat f)) (IRTScalar ETFloat64)
                        | StaticEval.SVBool b -> mkTyped (TExprLit (LitBool b)) (IRTScalar ETBool)
                        | StaticEval.SVString s -> mkTyped (TExprLit (LitString s)) (IRTScalar ETString)
                        | StaticEval.SVUnit -> mkTyped (TExprLit LitUnit) IRTUnit
                        | StaticEval.SVTuple vs ->
                            let ts = vs |> List.map svToTyped
                            mkTyped (TExprTuple ts) (IRTTuple (ts |> List.map (fun t -> t.Type)))
                        | StaticEval.SVStruct (sname, sfields) ->
                            // Fields are already in declaration order (the
                            // ExprStruct fold orders them via the struct
                            // registry), as C++ designated initializers require.
                            let tFields = sfields |> List.map (fun (fn, fv) -> (fn, svToTyped fv))
                            mkTyped (TExprStruct (sname, tFields)) (IRTNamed sname)
                    Some (svToTyped sv)
                | None -> None
            | _ -> None
        let inferred =
            match staticShortcut with
            | Some tv -> Ok tv
            | None -> inferLetBindingValue env binding
        inferred |> Result.bind (fun tValue ->
            let name = match binding.Pattern.Kind with PatternKind.PatVar n -> n | _ -> "_"
            // Reuse the pre-pass varId if pre-registered -- but ONLY for a
            // plain `let static x = ...`. checkModule's pre-pass registers
            // static values with placeholder types so a FORWARD reference
            // resolves (`let static a = b + 1` before `let static b = 2`
            // needs `b` bound at a placeholder varId the real decl adopts),
            // keyed by pattern (PatVar -> real name, else synthetic "_").
            // For a DESTRUCTURING static, reusing that entry is wrong: every
            // destructured static registers under the SAME key "_", so a
            // second one's `lookupVar "_"` would find the FIRST one's varId,
            // the two would share one IRId, and `unify` below would weld
            // their unrelated types together. Fresh id whenever destructured.
            let preRegistered =
                match binding.Pattern.Kind with
                | PatternKind.PatVar _ -> lookupVar name env
                | _ -> None
            let varId =
                match preRegistered with
                | Some existing ->
                    // Unify pre-pass type with checked type so forward references resolve
                    let _ = unify env.Subst existing.Type tValue.Type
                    existing.VarId
                | None -> env.Builder.FreshId()
            // Static bindings are ReadOnly and generalizable
            let scheme =
                let s = generalize env.Subst env.Variables tValue.Type
                if s.QuantifiedVars.IsEmpty then None else Some s
            let env' =
                match scheme with
                | Some s -> bindVarPoly name varId tValue.Type None ReadOnly (Some tValue) s env
                | None -> bindVarFull name varId tValue.Type None ReadOnly (Some tValue) env

            // Tuple, cons and struct destructuring for `let static (a, b)`
            // / `let static head :: tail` / `let static Point { x, y }`.
            // Mirrors DeclLet's branches. Division of labour with StaticEval:
            // its bindPattern folds PatVar/PatTuple/PatStruct leaves into
            // env.StaticValues as compile-time constants, but has no PatCons
            // case, so cons leaves get no static value and Lowering's
            // TDeclStatic path projects them out of the primary binding
            // instead (subBindingValue) -- which is what DSConsRest tells it.
            let mutable subBindings : (string * IRId * IRType) list = []
            let mutable destructure = DSPositional
            // Same parking spot as DeclLet's: the env-building fold is a pure
            // expression with nowhere to return an error from, so a
            // non-splittable cons pattern is surfaced just before the binding
            // is assembled.
            let mutable patternError : TypeError option = None
            let env'' =
                match binding.Pattern.Kind with
                | PatternKind.PatTuple pats ->
                    (match tupleDestructureArityError env pats tValue.Type with
                     | Some e -> patternError <- Some e
                     | None -> ())
                    let resolvedTy = env.Subst.Resolve(tValue.Type)
                    let typeList =
                        match resolvedTy with
                        | IRTTuple ts ->
                            if pats.Length = ts.Length then ts
                            else
                                let flat = IR.flattenTupleLeaves resolvedTy
                                if pats.Length = flat.Length then flat else ts
                        | _ -> []
                    pats |> List.mapi (fun i p -> (i, p))
                    |> List.fold (fun e (i, p) ->
                        match p.Kind with
                        | PatternKind.PatVar n ->
                            let eTy =
                                if i < typeList.Length then env.Subst.Resolve(typeList.[i])
                                else env.Subst.Fresh()
                            let subId = env.Builder.FreshId()
                            subBindings <- subBindings @ [(n, subId, eTy)]
                            bindVarSimple n subId eTy e
                        | _ -> patternNames p |> List.fold (fun e2 n ->
                            let subId = env.Builder.FreshId()
                            let eTy = env.Subst.Fresh()
                            subBindings <- subBindings @ [(n, subId, eTy)]
                            bindVarSimple n subId eTy e2) e) env'
                | PatternKind.PatCons (h, t) ->
                    match consDestructureLeaves env tValue.Type h t with
                    | Error e ->
                        patternError <- Some e
                        env'
                    | Ok leaves ->
                        destructure <- DSConsRest
                        leaves
                        |> List.fold (fun e (n, ty) ->
                            let subId = env.Builder.FreshId()
                            subBindings <- subBindings @ [(n, subId, ty)]
                            bindVarSimple n subId ty e) env'
                | PatternKind.PatStruct (_, fieldPats) ->
                    // `let static Point { x, y } = Point { x = 3, y = 4 }`.
                    // StaticEval.bindPattern already folds each leaf to a
                    // compile-time value (what Lowering's TDeclStatic path
                    // prefers); the sub-binding recorded here gives that
                    // constant its name, IRId and type. Field types come
                    // from structFieldTypesOf (same declared-field list
                    // DeclLet reads), matched BY NAME so a missing/extra
                    // field can't shift another leaf's projection.
                    let fieldTypeMap = Map.ofList (structFieldTypesOf env tValue.Type)
                    fieldPats |> List.fold (fun e (fieldName, p) ->
                        match p.Kind with
                        | PatternKind.PatVar n ->
                            let subId = env.Builder.FreshId()
                            let eTy =
                                Map.tryFind fieldName fieldTypeMap
                                |> Option.defaultWith (fun () -> env.Subst.Fresh())
                            subBindings <- subBindings @ [(n, subId, eTy)]
                            bindVarSimple n subId eTy e
                        | _ -> patternNames p |> List.fold (fun e2 n ->
                            let subId = env.Builder.FreshId()
                            let eTy = env.Subst.Fresh()
                            subBindings <- subBindings @ [(n, subId, eTy)]
                            bindVarSimple n subId eTy e2) e) env'
                | _ -> env'

            if patternError.IsSome then Error patternError.Value else
            let tb : TypedBinding = {
                Name = name; VarId = varId; Type = env.Subst.Resolve(tValue.Type)
                Identity = None; IsMutable = false; Value = tValue
                SubBindings = subBindings |> List.map (fun (n, id, ty) -> (n, id, env.Subst.Resolve ty))
                Destructure = destructure
                PostChecks = []
            }
            Ok (TDeclStatic tb, env''))

    | DeclFunction funcDecl -> checkFunctionDecl env funcDecl

    | DeclType typeDecl ->
        registerTypeDecl env typeDecl |> Result.bind (fun env' ->
            let ttdResult =
                match typeDecl with
                | TyDeclAlias (name, typeParams, body) ->
                    // Distinguish index-type aliases (Idx, SymIdx, ..., EnumIdx) from
                    // ordinary type aliases. Both register in env.TypeDefs the same
                    // way; the typed-AST distinction is what survives into Lowering
                    // and CodeGen, where index aliases need different rendering than
                    // generic IRType aliases (using Name = int64_t; rather than a
                    // promote<>-based template expansion).
                    match Map.tryFind name env'.TypeDefs with
                    | Some (TDIIndexType (_, idx, _)) ->
                        Ok (TTDIndexType (name, idx))
                    | Some (TDIEnumIdx (_, idx, values, _)) ->
                        Ok (TTDEnumIdx (name, idx, values))
                    | _ ->
                        Ok (TTDAlias (name, typeParams, lowerTypeExpr env' body))
                | TyDeclStruct (name, typeParams, fields, _constraints, _isStatic) ->
                    // Constraint validation happened in registerTypeDecl;
                    // checks materialize per assignment site (PostChecks /
                    // TExprConstraintCheck), not on the type def.
                    let resolvedFields = fields |> List.map (fun f -> (f.Name, lowerTypeExpr env' f.Type))
                    Ok (TTDStruct (name, typeParams, resolvedFields))
                | TyDeclSum (name, typeParams, variants) ->
                    let resolvedVariants = variants |> List.map (fun v ->
                        (v.Name, v.Data |> Option.map (lowerTypeExpr env')))
                    Ok (TTDVariant (name, typeParams, resolvedVariants))
                | TyDeclMutualGroup (members, _) ->
                    // Constraint validation happened in registerTypeDecl; the
                    // typed decl just carries the member aliases for lowering.
                    Ok (TTDMutualGroup (members |> List.map (fun (mname, mty) ->
                        (mname, lowerTypeExpr env' mty))))
            ttdResult |> Result.map (fun ttd -> (TDeclType ttd, env')))

    | DeclInterface ifaceDecl -> 
        let env' = { env with Interfaces = Map.add ifaceDecl.Name ifaceDecl env.Interfaces }
        Ok (TDeclInterface ifaceDecl, env')
    | DeclImpl implDecl -> 
        // Resolve the concrete type name
        let typeName = 
            match implDecl.ForType with
            | TyNamed (name, _) -> Some name
            | _ -> None
        match typeName with
        | Some tName ->
            // Register all mangled method names first (enables mutual recursion within impl block)
            let mutable env' = env
            let methodIds = implDecl.Methods |> List.map (fun method ->
                let mangledName = sprintf "%s__%s" tName method.Name
                let selfType = IRTNamed tName
                let paramTypes = method.Params |> List.map (fun p ->
                    if p.Name = "self" && p.Type.IsNone then selfType
                    else match p.Type with Some t -> lowerTypeExpr env' t | None -> IRTScalar ETFloat64)
                let retType = match method.ReturnType with
                              | Some t -> lowerTypeExpr env' t
                              | None -> env'.Subst.Fresh()
                let funcType = mkFuncArrow paramTypes retType
                let funcVarId = env'.Builder.FreshId()
                env' <- bindVarSimple mangledName funcVarId funcType env'
                env' <- { env' with ImplMethods = Map.add (tName, method.Name) (funcVarId, funcType) env'.ImplMethods }
                (mangledName, funcVarId, paramTypes, retType))

            // Validate interface if specified
            match Map.tryFind implDecl.Interface env.Interfaces with
            | Some ifaceDecl ->
                let missing = ifaceDecl.Methods |> List.filter (fun ifaceMethod ->
                    not (implDecl.Methods |> List.exists (fun m -> m.Name = ifaceMethod.Name)))
                match missing with
                | _ :: _ ->
                    let names = missing |> List.map (fun m -> m.Name) |> String.concat ", "
                    Error (ImplMissingMethods (implDecl.Interface, tName, names))
                | [] -> Ok ()
            | None -> Ok ()
            |> Result.bind (fun () ->
                // Type-check each method body
                let selfType = IRTNamed tName
                let mutable typedMethods = []
                let mutable methodErr = None
                for (method, (mangledName, funcVarId, paramTypes, retType)) in List.zip implDecl.Methods methodIds do
                    if methodErr.IsNone then
                        let savedScope = env'.Subst.PushTypeVarScope()
                        let mutable bodyEnv = enterScope env'
                        let typedParams = method.Params |> List.mapi (fun i p ->
                            let varId = env'.Builder.FreshId()
                            let ty =
                                if p.Name = "self" && p.Type.IsNone then selfType
                                else paramTypes.[i]
                            bodyEnv <- bindVarSimple p.Name varId ty bodyEnv
                            { Name = p.Name; Type = ty; Index = i; VarId = varId; Default = None; NameSpan = p.NameSpan } : TypedParam)
                        match inferExpr bodyEnv method.Body with
                        | Ok tBody ->
                            let _ = unify env'.Subst tBody.Type retType
                            let commGroups =
                                extractCommGroups
                                    (method.Params |> List.map (fun p -> { Name = p.Name; Type = p.Type; Default = None; NameSpan = p.NameSpan } : LambdaParam))
                                    method.WhereClause
                            let tf : TypedFunctionDecl = {
                                Name = mangledName; FuncId = funcVarId
                                TypeParams = method.TypeParams
                                Params = typedParams; ReturnType = tBody.Type
                                WhereClause = method.WhereClause; Body = tBody
                                CommGroups = commGroups; IsStatic = false
                                NameSpan = method.NameSpan
                            }
                            typedMethods <- typedMethods @ [tf]
                        | Error e -> methodErr <- Some e
                        env'.Subst.PopTypeVarScope(savedScope)
                match methodErr with
                | Some e -> Error e
                | None ->
                    let timpl : TypedImplDecl = {
                        ForType = implDecl.ForType
                        TypeName = tName
                        Methods = typedMethods
                    }
                    Ok (TDeclImpl timpl, env'))
        | None ->
            // Can't resolve type name -- pass through with empty methods
            let timpl : TypedImplDecl = {
                ForType = implDecl.ForType
                TypeName = "_"
                Methods = []
            }
            Ok (TDeclImpl timpl, env)
    | DeclUnit unitDecl ->
        // registerUnit rejects both resolver failures at the declaration
        // site: a TERMINAL-quantity misuse (BL3011: `Unit x = speed * m` /
        // `Unit q: speed`) and an unknown name (BL3015: `Unit t = 2*pii*rad`).
        registerUnit env unitDecl
        |> Result.map (fun env' -> (TDeclUnit unitDecl, env'))
    | DeclImport (qname, style) when (not qname.IsEmpty) && qname.Head = "Providers" ->
        // The pre-module provider spelling (`import Providers.NetCDF as X`)
        // is a hard break: providers are ordinary modules now.
        let suggestion =
            match qname with
            | [_; sub] -> sub.ToLowerInvariant()
            | _ -> "netcdf"
        Error (ProviderImportByModule (suggestion, (Blade.ProviderRegistry.names () |> String.concat ", ")))
    | DeclImport ([pname], ImportSelective _) when (Blade.ProviderRegistry.tryFind pname).IsSome
                                                   && not (Map.containsKey pname env.ModuleExports) ->
        // Providers expose load/read/write through a qualified alias only;
        // there are no free-standing names to import selectively.
        Error (ProviderNoSelectiveImport pname)
    | DeclImport (qname, style) ->
        let fullName = String.concat "." qname
        let env' =
            match Map.tryFind fullName env.ModuleExports with
            | Some exports ->
                match style with
                | ImportQualified aliasOpt ->
                    let alias = aliasOpt |> Option.defaultValue (List.last qname)
                    // Register all exported variables as alias.name
                    let mutable e = env
                    for kv in exports.Variables do
                        let qualName = sprintf "%s.%s" alias kv.Key
                        e <- bindVar qualName kv.Value e
                    for kv in exports.TypeDefs do
                        e <- registerTypeDef kv.Key kv.Value e
                    for kv in exports.VariantTags do
                        e <- { e with VariantTags = Map.add kv.Key kv.Value e.VariantTags }
                    for kv in exports.Units do
                        e <- { e with Units = Map.add kv.Key kv.Value e.Units }
                    // Static functions: register under alias.name. Bare-name
                    // references to these would need parser support for
                    // dotted names in eta-reduced DepIdx position; for now,
                    // qualified-import use sites of static functions only
                    // work in expression contexts where dotted names parse.
                    for kv in exports.StaticFunctions do
                        let qualName = sprintf "%s.%s" alias kv.Key
                        e <- { e with StaticFunctions = Map.add qualName kv.Value e.StaticFunctions }
                    e
                | ImportSelective names ->
                    let mutable e = env
                    for name in names do
                        match Map.tryFind name exports.Variables with
                        | Some vi -> e <- bindVar name vi e
                        | None -> ()
                        match Map.tryFind name exports.TypeDefs with
                        | Some tdi -> e <- registerTypeDef name tdi e
                        | None -> ()
                        match Map.tryFind name exports.StaticFunctions with
                        | Some fd ->
                            e <- { e with StaticFunctions = Map.add name fd e.StaticFunctions }
                        | None -> ()
                        // Units, the same way the QUALIFIED arm above imports
                        // them (unit names have no qualified spelling -- an
                        // annotation is `Float<newton>`, never `Float<SI.newton>`
                        // -- so the only difference between the two arms is
                        // ALL of them vs the ones named). Without this,
                        // `from units.SI import newton` silently binds nothing
                        // and `Float<newton>` degrades to no annotation at all.
                        match Map.tryFind name exports.Units with
                        | Some us -> e <- { e with Units = Map.add name us e.Units }
                        | None -> ()
                    e
            | None ->
                // Provider or unknown module -- bind alias as opaque so references type-check
                // (actual types resolved during lowering when provider runs)
                match style with
                | ImportQualified aliasOpt ->
                    let alias = aliasOpt |> Option.defaultValue (List.last qname)
                    let varId = env.Builder.FreshId()
                    bindVarSimple alias varId (IRTNamed (fullName)) env
                | ImportSelective _ -> env
        Ok (TDeclImport (qname, style), env')

and checkFunctionDecl (env: TypeEnv) (funcDecl: FunctionDecl) : TypeResult<TypedDecl * TypeEnv> =
    // Fresh type variable scope for this function's type annotations.
    let savedScope = env.Subst.PushTypeVarScope()

    // Pre-scan all parameter + return type annotations to register type variable names.
    let allAnnotations =
        (funcDecl.Params |> List.map (fun p -> p.Type))
        @ [funcDecl.ReturnType]
    prescanTypeVarNames env allAnnotations

    let paramTypes = funcDecl.Params |> List.map (fun p ->
        match p.Type with Some t -> lowerTypeExpr env t | None -> env.Subst.Fresh())
    let retType = match funcDecl.ReturnType with
                  | Some t -> lowerTypeExpr env t
                  | None -> env.Subst.Fresh()
    if (paramTypes |> List.exists irTypeHasRaggedNoPrior) || irTypeHasRaggedNoPrior retType then
        Error (RaggedIdxNeedsPrior funcDecl.Name)
    elif (paramTypes |> List.exists irTypeHasBadDistOrder) || irTypeHasBadDistOrder retType then
        Error (DistOrderCompileTime funcDecl.Name)
    // Parameters are exactly where the wildcard belongs, so only the RETURN
    // type is scanned here (irTypeHasTagWildcard likewise skips a functional
    // parameter's own slots, keeping `f: (Nat<_>) -> Float64` legal).
    elif irTypeHasTagWildcard retType then
        Error (TagWildcardNotParam (sprintf "function '%s' return type" funcDecl.Name))
    else
    let badAxis = (paramTypes @ [retType]) |> List.tryPick irTypeUnknownAxisPath
    if badAxis.IsSome then
        Error (Other (unknownAxisPathMessage badAxis.Value))
    else
    let badIrreps = (paramTypes @ [retType]) |> List.tryPick irTypeBadIrrepsDetail
    if badIrreps.IsSome then
        Error (IrrepsIdxSpecFn (funcDecl.Name, badIrreps.Value))
    else
    let badPgIrreps = (paramTypes @ [retType]) |> List.tryPick irTypeBadPgIrrepsDetail
    if badPgIrreps.IsSome then
        Error (PgIrrepsIdxSpecFn (funcDecl.Name, badPgIrreps.Value))
    else
    // Both parameters AND the return type: either one names an array whose
    // storage would have to exist. (Unlike the tag wildcard, which is LEGAL in
    // parameter position, there is no position where a wreath array can be
    // handled -- a caller would have had to allocate it.)
    let wreath = (paramTypes @ [retType]) |> List.tryPick irTypeWreathLevels
    if wreath.IsSome then
        Error (OrbitStorageUnsupported (wreath.Value, sprintf "function '%s'" funcDecl.Name))
    else
    // Bounded PARAMETERS and RETURNS are unguarded today (synthesizeBoundChecks
    // is let-annotation-only), so a bounded aggregate here is silently dropped
    // rather than mis-lowered. Reject it anyway: the annotation is meaningless
    // in both readings, and a bound that quietly evaporates is worse than one
    // that is refused.
    let badBound =
        (funcDecl.Params |> List.tryPick (fun p ->
            p.Type |> Option.bind (boundedAggregateError env (sprintf "parameter '%s' of function '%s'" p.Name funcDecl.Name))))
        |> Option.orElseWith (fun () ->
            funcDecl.ReturnType |> Option.bind (boundedAggregateError env (sprintf "the return type of function '%s'" funcDecl.Name)))
    if badBound.IsSome then
        Error badBound.Value
    else
    // A quantity name inside a COMPOUND unit annotation on a param or the
    // return type is terminal (BL3011) -- surface-checked here, since
    // lowering degrades to the bare base rather than erroring.
    let badUnitAnno =
        (funcDecl.Params |> List.tryPick (fun p -> p.Type |> Option.bind (unitAnnoError env)))
        |> Option.orElseWith (fun () ->
            funcDecl.ReturnType |> Option.bind (unitAnnoError env))
    if badUnitAnno.IsSome then
        Error badUnitAnno.Value
    else
    let funcType = mkFuncArrow paramTypes retType
    // Reuse pre-pass varId if this function was already pre-registered (static functions)
    // This ensures other functions' bodies reference the same varId
    let funcVarId =
        match lookupVar funcDecl.Name env with
        | Some existing -> existing.VarId
        | None -> env.Builder.FreshId()
    // Register function BEFORE body (enables recursion)
    let envWithFunc = bindVarSimple funcDecl.Name funcVarId funcType env

    // `x: mut T` params bind MutPassable so the body may assign into them
    // (gradient out-buffers). Array-typed only: the C++ ABI passes the
    // Array<> wrapper by value, which aliases the caller's DATA (shallow
    // pointer copy) -- element writes land in the caller -- but a scalar
    // passed by value would silently drop its writes.
    let mutParamErr =
        funcDecl.Params |> List.tryPick (fun p ->
            if p.Mutability = Mutable then
                let i = funcDecl.Params |> List.findIndex (fun q -> q.Name = p.Name)
                match env.Subst.Resolve paramTypes.[i] with
                | ArrayElem _ -> None
                | _ -> Some (MutParamNotArray (funcDecl.Name, p.Name))
            else None)
    match mutParamErr with
    | Some e ->
        env.Subst.PopTypeVarScope(savedScope)
        Error e
    | None ->

    // Mutual-group scans. Member types are forbidden in parameter positions
    // (alias transparency would silently erase the constraint); a declared
    // return tuple naming a full group makes this function the group's
    // introduce-site -- checks emit at the return, annotated callers exempt.
    let mutualParamErr =
        funcDecl.Params |> List.tryPick (fun p ->
            match p.Type with
            | Some t ->
                match mutualMemberNamesIn env t with
                | [] -> None
                | n :: _ -> Some (MutualParamMemberType (funcDecl.Name, p.Name, n))
            | None -> None)
    match mutualParamErr with
    | Some e ->
        env.Subst.PopTypeVarScope(savedScope)
        Error e
    | None ->
    match (match funcDecl.ReturnType with Some t -> tryMutualAnnotation env t | None -> Ok None) with
    | Error e ->
        env.Subst.PopTypeVarScope(savedScope)
        Error e
    | Ok mutualReturnGroup ->
    (match mutualReturnGroup with
     | Some g -> env.MutualReturnFuncs.[funcDecl.Name] <- g.GroupId
     | None -> ())

    // Custom where-clause conjuncts (`where <name>(<args>)` for names the
    // grammar doesn't own): dispatch each through the Blade.Constraints
    // registry. Validate at the signature; record the function for
    // call-site discharge; the license scope opens around body checking
    // below. An unregistered name errors with the registered vocabulary.
    let paramNames = funcDecl.Params |> List.map (fun p -> p.Name)
    let customConjuncts =
        funcDecl.WhereClause |> Option.map (fun w -> w.Custom) |> Option.defaultValue []
    let conjunctErr =
        customConjuncts |> List.tryPick (fun (cname, cargs) ->
            match Blade.Constraints.lookupConstraint cname with
            | None ->
                // Module-owned keywords are registered under mangled names
                // ("__ppl_indep") and reached via a qualified conjunct that
                // the owning module's elaborator normalizes. A bare (or
                // wrongly-qualified) use of such a keyword gets a targeted
                // hint; the vocabulary list shows the module spelling.
                let bare = match cname.Split('.') with [| _; n |] -> n | _ -> cname
                if (Blade.Constraints.lookupConstraint ("__ppl_" + bare)).IsSome then
                    Some (PplConstraintNeedsImport (funcDecl.Name, bare))
                else
                let known =
                    Blade.Constraints.registeredConstraintNames ()
                    |> List.map (fun n ->
                        if n.StartsWith "__ppl_" then "ppl." + n.Substring 6
                        elif n.StartsWith "__ml_" then "ml." + n.Substring 5
                        else n)
                let vocab = if known.IsEmpty then "none registered" else String.concat ", " known
                Some (UnknownWhereConstraint (funcDecl.Name, cname, vocab))
            | Some h ->
                match h.Validate funcDecl.Name paramNames cargs with
                | Ok () -> None
                | Error msg -> Some (Other msg))
    match conjunctErr with
    | Some e ->
        env.Subst.PopTypeVarScope(savedScope)
        Error e
    | None ->
    if not customConjuncts.IsEmpty then
        env.FuncConstraints.[funcDecl.Name] <- (paramNames, customConjuncts)

    let mutable bodyEnv = enterScope envWithFunc
    let typedParams = funcDecl.Params |> List.mapi (fun i p ->
        let varId = env.Builder.FreshId()
        let assign = match p.Mutability with
                     | Mutable -> MutPassable
                     | _ -> ReadOnly
        bodyEnv <- bindVarFull p.Name varId paramTypes.[i] None assign None bodyEnv
        // Dist-typed parameters carry their license token as provenance --
        // the `where indep` handler licenses exactly these tokens for the
        // body, and call-site discharge maps them back to actuals.
        (match env.Subst.Resolve paramTypes.[i] with
         | IRTDist _ ->
             env.Provenance.[varId] <- Set.singleton (Blade.Constraints.paramProvenanceToken funcDecl.Name p.Name)
         | _ -> ())
        { Name = p.Name; Type = paramTypes.[i]; Index = i; VarId = varId; Default = None; NameSpan = p.NameSpan } : TypedParam)

    // ---- Parameter defaults (BL3012 rules + declaration-time typing) ----
    // Trailing rule; required-params-only scope rule; then each default
    // type-checked against its param's declared (lowered) type with the
    // params in scope, so a bad default errors HERE even if never called.
    // A quantity-typed slot's default is an INTRODUCTION position: checkExpr
    // against the annotated type is the permissive direction (the call-site
    // desugar re-ascribes the surface default, minting the nominal). Call
    // sites re-type the SURFACE exprs, so the typed results are discarded.
    let defaultsErr =
        match funcDecl.Params |> List.tryFindIndex (fun p -> p.Default.IsSome) with
        | None -> None
        | Some fd ->
            let firstDefaultedName = (List.item fd funcDecl.Params).Name
            let orderErr =
                funcDecl.Params
                |> List.mapi (fun i p -> (i, p))
                |> List.tryPick (fun (i, p) ->
                    if i > fd && p.Default.IsNone
                    then Some (DefaultParamOrder (sprintf "function '%s'" funcDecl.Name, p.Name, firstDefaultedName))
                    else None)
            match orderErr with
            | Some e -> Some e
            | None ->
                let defaultedNames =
                    funcDecl.Params |> List.filter (fun p -> p.Default.IsSome)
                    |> List.map (fun p -> p.Name) |> Set.ofList
                let scopeErr =
                    funcDecl.Params |> List.tryPick (fun p ->
                        match p.Default with
                        | Some d ->
                            let bad = Set.intersect (collectFreeVars Set.empty d) defaultedNames
                            if Set.isEmpty bad then None
                            else Some (DefaultParamScope (sprintf "function '%s'" funcDecl.Name, p.Name, Set.minElement bad))
                        | None -> None)
                match scopeErr with
                | Some e -> Some e
                | None ->
                // FACTORY rule (BL3013): quantity-typed defaulted slots must
                // carry DISTINCT quantities -- by-nominal call-site routing
                // (`f(x, 3 : levels)`) needs each nominal to name exactly one
                // slot. Judged on the LOWERED param types, so `Float<speed>`
                // and a bare `speed` annotation agree.
                let dupQuantityErr =
                    let slots =
                        funcDecl.Params
                        |> List.mapi (fun i p -> (i, p))
                        |> List.choose (fun (i, p) ->
                            match p.Default with
                            | Some _ ->
                                (match IR.getUnits (env.Subst.Resolve paramTypes.[i]) with
                                 | Some u -> u.Nominal |> Option.map (fun q -> (q, p.Name))
                                 | None -> None)
                            | None -> None)
                    slots
                    |> List.groupBy fst
                    |> List.tryPick (fun (q, members) ->
                        match members with
                        | (_, p1) :: (_, p2) :: _ ->
                            Some (FactoryDupQuantityDecl (sprintf "function '%s'" funcDecl.Name, q, p1, p2))
                        | _ -> None)
                match dupQuantityErr with
                | Some e -> Some e
                | None ->
                    funcDecl.Params
                    |> List.mapi (fun i p -> (i, p))
                    |> List.tryPick (fun (i, p) ->
                        match p.Default with
                        | Some d ->
                            (match checkExpr bodyEnv paramTypes.[i] d with
                             | Ok _ -> None
                             | Error e -> Some e)
                        | None -> None)
    match defaultsErr with
    | Some e ->
        env.Subst.PopTypeVarScope(savedScope)
        Error e
    | None ->
    // Register the surface param list (name, annotation, default) so call
    // sites can fill omitted trailing args -- BEFORE the body is checked,
    // so recursive calls inside the body may omit them too.
    if funcDecl.Params |> List.exists (fun p -> p.Default.IsSome) then
        env.FuncDefaults.[funcDecl.Name] <- (funcDecl.Params |> List.map (fun p -> (p.Name, p.Type, p.Default)))

    // Open the license scope for the body; closed after `result` is
    // computed (both success and error paths flow past the exit below).
    for (cname, cargs) in customConjuncts do
        Blade.Constraints.lookupConstraint cname |> Option.iter (fun h -> h.EnterBody funcDecl.Name cargs)

    let result =
        // When a return type is annotated, drive the body bidirectionally
        // via checkExpr. This pushes the expected type into literal and
        // tuple-constructor positions so that e.g. `(4, 1)` retypes its
        // elements against `(StationIdx, Idx<3>)` (giving the literals
        // their named-index types directly) rather than synthesizing
        // `(Int64, Int64)` and then failing to unify against the named
        // tuple. Without an annotation, fall back to plain inference --
        // there's no expectation to push.
        let bodyResult =
            match funcDecl.ReturnType with
            | Some _ -> checkExpr bodyEnv retType funcDecl.Body
            | None -> inferExpr bodyEnv funcDecl.Body
        bodyResult |> Result.bind (fun tBody ->
            // A function body is a value-forming boundary: a wildcard `_` that
            // survives into it has escaped its only legitimate role (a compound-
            // index coordinate). Reject cleanly here rather than at lowering.
            if exprContainsWildcard tBody then
                Error (Other
                    "wildcard `_` is not a value: it cannot be a function's returned value. It is only meaningful as a compound-index coordinate (e.g. B((a, _, c))).")
            else
            // Belt-and-suspenders: even after checkExpr, run unify on the
            // synthesized body type vs the annotation. checkExpr's fall-
            // through case (line ~3082) is `inferExpr + unify` already,
            // and the special-cased shapes (literals, tuples) build a
            // TypedExpr whose .Type matches the expected type by
            // construction -- so this unify is mostly a no-op when
            // checkExpr was used. When the bodyResult came from inferExpr
            // (no annotation), retType is itself a fresh inference var
            // and the unify just binds it. Either way we propagate the
            // result so genuine mismatches surface here rather than
            // exploding at codegen.
            unify env.Subst tBody.Type retType |> Result.bind (fun () ->
            // The wreath gate again, on the RESOLVED return type. The one at
            // the signature above sees only DECLARED annotations; an
            // unannotated function whose body deduces a wreath class (a comm
            // tie over a repeated compact parameter) gets its return type from
            // this unify, so the declared-side check never saw it. Returning a
            // wreath is not supported: the C++ ABI hands arrays back as an
            // `Array<T,N>` wrapper, and a wreath pool is a flat `T*` with no
            // extents -- the callee would return a wrapper describing a
            // skeleton that does not exist. Produce the class at the use site
            // instead (the corpus's deduced cases all do).
            match irTypeWreathLevels (env.Subst.Resolve retType) with
            | Some levels ->
                Error (OrbitStorageUnsupported
                         (levels, sprintf "function '%s' returns a deduced wreath class" funcDecl.Name))
            | None ->
            // Declared-return introduce-site: wrap the body so the joint
            // check fires at the return (the single verification point).
            let wrappedBodyR =
                match mutualReturnGroup, funcDecl.ReturnType with
                | Some group, Some retAnnot -> wrapMutualReturnBody envWithFunc retAnnot group tBody
                | _ -> Ok tBody
            wrappedBodyR |> Result.bind (fun tBody ->
            let commGroups =
                extractCommGroups
                    (funcDecl.Params |> List.map (fun p -> { Name = p.Name; Type = p.Type; Default = None; NameSpan = p.NameSpan } : LambdaParam))
                    funcDecl.WhereClause
            // Register the function's comm groups so a later kernel-use site
            // (etaExpandFunctionKernel / deferred-former eta) can surface them
            // onto the synthesized wrapper lambda -- otherwise `where comm` on a
            // named function is dropped and the loop emits dense storage.
            if not (List.isEmpty commGroups) then
                env.FuncCommGroups.[funcDecl.Name] <- commGroups
            // Same for `where anticomm(...)` -- its own side-channel, since a
            // where-clause attribute that no surfacing site re-attaches is
            // dropped by the kernel-position eta wrapper (silently falling
            // back to dense storage).
            let antisymGroups =
                extractAntisymGroups
                    (funcDecl.Params |> List.map (fun p -> { Name = p.Name; Type = p.Type; Default = None; NameSpan = p.NameSpan } : LambdaParam))
                    funcDecl.WhereClause
            if not (List.isEmpty antisymGroups) then
                env.FuncAntisymGroups.[funcDecl.Name] <- antisymGroups
            // Register the function's parallel strategies for the same reason,
            // paired with its param NAMES: an `omp(a: n)` var is resolved by
            // name against the callable's params (Lowering.extractParallelism),
            // and the eta wrapper renames them, so the surfacing site needs the
            // originals to remap by position.
            (match funcDecl.WhereClause with
             | Some wc when not (List.isEmpty wc.Parallel) ->
                 env.FuncParallel.[funcDecl.Name] <-
                     (funcDecl.Params |> List.map (fun p -> p.Name), wc.Parallel)
             | _ -> ())
            // Fold-kernel builtin-body bit, for the parallel-fold reorder
            // licence (checkFoldOmpLicense). Recorded here for the same reason
            // as the tables above: a named function's BODY is invisible at the
            // `reduce(xs, f)` seam, which sees only a `TExprVar`.
            if isBuiltinFoldBodySurface (funcDecl.Params |> List.map (fun p -> p.Name))
                                        funcDecl.Body then
                env.FuncFoldBuiltin.[funcDecl.Name] <- true
            // An `omp(v: n)` naming no parameter is silently dropped downstream.
            checkOmpVarNames env (funcDecl.Params |> List.map (fun p -> p.Name))
                             funcDecl.WhereClause (sprintf "function '%s'" funcDecl.Name)
            // ... and an `omp(p: n)` read as a licence for a loop this body
            // builds over `p` itself licenses nothing (the clause is about the
            // EXTERNAL S-dims `p` contributes to a CALLER's nest).
            checkOmpInternalLoop env (funcDecl.Params |> List.map (fun p -> p.Name))
                                 funcDecl.WhereClause (sprintf "function '%s'" funcDecl.Name)
                                 tBody
            // Stage 3 (symmetry deduction, early tier): summarize the
            // adjacent-pair swap parity of this fixed-arity function's body
            // and record it for kernel-position uses -- buildApplyInfo
            // consults the summary when the kernel is an eta-expanded
            // wrapper around this function. Poly packs are late-tier work
            // (their pairs only exist per materialized arity): skipped.
            let polyParams =
                typedParams |> List.filter (fun p ->
                    match env.Subst.Resolve p.Type with IRTPoly _ -> true | _ -> false)
            // Interprocedural SIGN-LINEARITY summary, recorded BEFORE the pair
            // deduction of this same body so a helper is available to its
            // callers (decl order) -- `mymean(row) = reduce(row,(+))/extents(row)`
            // is odd in `row`, which is what lets a caller's `mymean(x - y)`
            // deduce PNeg instead of PBottom. Fixed-arity (non-Poly) only: a
            // pack has no per-position summary to index by argument. Keyed by
            // funcVarId -- the id every other body's reference to this function
            // resolves to -- so a shadowing parameter cannot borrow the law.
            let signResolver (calleeId: IRId) =
                match env.FuncSignParities.TryGetValue calleeId with
                | true, ps -> Some ps
                | _ -> None
            if List.isEmpty polyParams && not (List.isEmpty typedParams) then
                env.FuncSignParities.[funcVarId] <-
                    Blade.Deduce.deduceSignParities signResolver typedParams tBody
            let deducedPairs =
                if not (List.isEmpty polyParams) then []
                else Blade.Deduce.deduceAdjacentPairs signResolver typedParams tBody
            if not (List.isEmpty deducedPairs) then
                env.FuncDeducedPairs.[funcDecl.Name] <-
                    (typedParams |> List.map (fun p -> p.Name), deducedPairs)
                IdeDeductions.addPairs funcDecl.Name
                    (typedParams |> List.map (fun p -> p.Name)) deducedPairs
            // Stage-3 LATE TIER: pack symmetry for arity-polymorphic kernels.
            // The forall -arity AC-fold template first (the head::tail recursion
            // itself -- packprod, comoment_prod); failing that, the
            // compositional wrapper walk (comoment = mean(prod(a))), which
            // resolves callees through PackDeducedComm in decl order.
            // PInv-or-PBottom only: packs never claim PNeg, so this table
            // fuels suggestions and can produce no false errors.
            (match polyParams with
             | [packP] ->
                 let packSummary =
                     match Blade.Deduce.deducePackFold funcDecl.Name packP.Name packP.VarId tBody with
                     | Blade.Deduce.PInv -> Blade.Deduce.PInv
                     | _ ->
                         let resolver fname =
                             match env.PackDeducedComm.TryGetValue fname with
                             | true, (_, p) -> Some p
                             | _ -> None
                         Blade.Deduce.packParityOf resolver packP.VarId tBody
                 if packSummary = Blade.Deduce.PInv then
                     env.PackDeducedComm.[funcDecl.Name] <- (packP.Name, packSummary)
                     Blade.TypeEnv.DeducedFacts.add
                         (Blade.TypeEnv.DeducedPackComm (funcDecl.Name, packP.Name)) tBody.Span
                     IdeDeductions.addPack funcDecl.Name packP.Name packSummary
             | _ -> ())
            // RECORDING ONLY (channel (f)) -- everything below this point in the
            // decl's deduction is unchanged; these two loops only WRITE to the
            // IDE side-channel. Ranks are read here rather than after the close
            // 60 lines down because the close resolves the var: once `unify`
            // has run, `Resolve pt` is an array and the bound is invisible.
            // FunctionDecl has no Span field and no decl span is in scope, so
            // tBody.Span is the tightest honest anchor.
            let pairDeclared i =
                (commGroups @ antisymGroups)
                |> List.exists (fun g -> List.contains i g && List.contains (i + 1) g)
            List.indexed deducedPairs
            |> List.iter (fun (i, par) ->
                if (par = Blade.Deduce.PInv || par = Blade.Deduce.PNeg)
                   && i + 1 < typedParams.Length
                   && not (pairDeclared i) then
                    Blade.TypeEnv.DeducedFacts.add
                        (Blade.TypeEnv.DeducedPairSym
                            (funcDecl.Name, typedParams.[i].Name, typedParams.[i + 1].Name, i,
                             par = Blade.Deduce.PNeg))
                        tBody.Span)
            // Ranks the body FORCED on unannotated params -- read off the very
            // same bounds the decl-close block below consumes (same Resolve,
            // same arity guard, same GetRankLowerBound), so what the editor
            // reports and what the checker closes agree by construction.
            paramTypes |> List.iteri (fun i pt ->
                match env.Subst.Resolve pt with
                | IRTInfer id when (env.Subst.GetArityConstraint id).IsNone ->
                    (match env.Subst.GetRankLowerBound(id) with
                     | Some k when k > 0 ->
                         Blade.TypeEnv.DeducedFacts.add
                             (Blade.TypeEnv.DeducedRank
                                 (funcDecl.Name, funcDecl.Params.[i].Name, i, k))
                             tBody.Span
                     | _ -> ())
                | _ -> ())
            // Declared comm on a named function whose body is PROVABLY
            // antisymmetric in a declared adjacent pair is the
            // silent-corruption case section 4 exists for. A named function cannot
            // be a reynolds kernel (reynolds requires a lambda), so no
            // iteration license can apply here -- hard error. PBottom stays
            // trusted (the escape hatch when the analysis is too weak).
            // The declared-anticomm twin is the same check with the parities
            // exchanged: a body provably INVARIANT under a pair declared
            // antisymmetric would be stored on a strict simplex that has no
            // diagonal and negates half its reads.
            let declContradiction (groups: int list list) (wanted: Blade.Deduce.Parity)
                                  (mk: string -> string -> TypeError) =
                if List.isEmpty groups then None
                else
                    List.indexed deducedPairs
                    |> List.tryPick (fun (i, par) ->
                        if par = wanted
                           && groups |> List.exists (fun g ->
                                  List.contains i g && List.contains (i + 1) g) then
                            Some (mk typedParams.[i].Name typedParams.[i + 1].Name)
                        else None)
            let commContradiction =
                match declContradiction commGroups Blade.Deduce.PNeg
                                        (fun a b -> CommContradictsBody (a, b)) with
                | Some e -> Some e
                | None ->
                    declContradiction antisymGroups Blade.Deduce.PInv
                                      (fun a b -> AntisymmContradictsBody (a, b))
            match commContradiction with
            | Some e -> Error e
            | None ->
            // Close the body-only rank deduction (stage 2): a param whose
            // type is still an unresolved inference var but carries a rank
            // lower bound (accumulated from the body's builtin pins and
            // direct-call demands, max-joined) is pinned to a fresh rank-k
            // array with a free element type -- the minimum rank the body
            // forces IS the cell rank. Params with no bound stay fully
            // generic; params under a `T^k` annotation are skipped (governed
            // by their exact arity constraint). Lives in Zonk.fs so this
            // DECLARED-param site and zonk's auto-close (for lambda params,
            // no decl site) build the same array and cannot drift.
            Blade.Zonk.closeDeducedRanks env.Subst env.Builder funcDecl.Name paramTypes
            let resolvedParams = typedParams |> List.map (fun p ->
                { p with Type = env.Subst.Resolve(p.Type) } : TypedParam)
            let resolvedRet = env.Subst.Resolve(retType)
            // REPRESENTATION-STATUS deduction -- the fourth lattice, beside the
            // parity deduction above.
            //
            // It sits HERE rather than beside deduceSignParities on purpose:
            // deducing from CLOSED types requires the rank deduction to have
            // already closed at `closeDeducedRanks` two lines up. Above this
            // point an unannotated parameter is still an IRTInfer and would
            // classify unclassifiable.
            //
            // `Subst.Resolve`, deliberately NOT `Zonk.zonkType`: zonk DEFAULTS
            // an unsolved variable to Float64, which would present a still-open
            // parameter as a provable invariant SCALAR -- a false shape, and
            // shape is load-bearing in the scaling rule. Resolve leaves it
            // IRTInfer, which classifies unclassifiable and skips the function.
            //
            // PROPOSALS ONLY: this channel is read by the differential harness
            // alone -- no BL4011, no warning, no CertSuggestion. The MLElaborate
            // seam remains the user-facing emitter and the checking authority.
            let repResolve (t: IRType) = env.Subst.Resolve t
            let repParams =
                resolvedParams
                |> List.map (fun p ->
                    ({ PName = p.Name; PId = p.VarId; PType = p.Type } : Blade.DeduceRep.RepParam))
            (match customConjuncts |> List.tryFind (fun (n, _) -> n = "__ml_equiv") with
             | Some (_, gargs) ->
                 // Pinned or elaborator-stamped: record the declared signature
                 // as an axiom for later callers. Conjuncts are read uniformly,
                 // so a synthesized function stamped by the ML elaborator lands
                 // here by the same path a user's `where ml.equiv(O3)` does.
                 let declaredGroup = (match gargs with g :: _ -> g | [] -> "")
                 Blade.DeduceRep.recordCertified env.FuncRepSigs repResolve
                     funcDecl.Name funcVarId declaredGroup repParams resolvedRet
                 // THE SECOND OPINION. The seam checker has already
                 // ruled on this body and remains the authority; this
                 // walk changes no verdict and gates no code. Only a DISAGREE
                 // is recorded, and a disagreement between two independent
                 // judgments of the same theorem is a compiler bug, not a user
                 // error -- the LieGuardFailure posture. `recordCertified` ran
                 // FIRST on purpose, so a recursive body can assume its own
                 // declared certificate (assume-guarantee), exactly as the
                 // seam's judgeFunction does.
                 (match Blade.DeduceRep.checkDeclaredRep env.FuncRepSigs repResolve
                            funcDecl.Name funcVarId declaredGroup repParams resolvedRet tBody with
                  | Blade.DeduceRep.RepConfirm ->
                      Blade.DeduceRep.RepCheckCensus.recordConfirm funcDecl.Name
                  | Blade.DeduceRep.RepAbstain reason ->
                      Blade.DeduceRep.RepCheckCensus.recordAbstain funcDecl.Name reason
                  | Blade.DeduceRep.RepDisagree detail ->
                      Blade.DeduceRep.RepCheckDisagreements.add funcDecl.Name detail tBody.Span)
             | None ->
                 // Unpinned: deduce. Silence is the overwhelmingly common
                 // outcome (no rep family in the signature -> no candidates).
                 match Blade.DeduceRep.deduceFunctionRep env.FuncRepSigs env.FuncRepSpec
                           repResolve funcDecl.Name funcVarId repParams resolvedRet tBody with
                 | Some prop -> Blade.DeduceRep.TypedCertProposals.add prop tBody.Span
                 | None -> ())
            let tf : TypedFunctionDecl = {
                Name = funcDecl.Name; FuncId = funcVarId
                TypeParams = funcDecl.TypeParams
                Params = resolvedParams; ReturnType = resolvedRet
                WhereClause = funcDecl.WhereClause; Body = tBody
                CommGroups = commGroups; IsStatic = funcDecl.IsStatic
                NameSpan = funcDecl.NameSpan
            }
            Ok (TDeclFunction tf, envWithFunc))))

    // Close the license scope (error paths included -- `result` has
    // materialized either way by this point).
    for (cname, cargs) in customConjuncts do
        Blade.Constraints.lookupConstraint cname |> Option.iter (fun h -> h.ExitBody funcDecl.Name cargs)

    env.Subst.PopTypeVarScope(savedScope)
    result

/// Where-clause predicate contract: a static function called from a
/// struct/mutual where-conjunct must have fully annotated params + return.
/// Its pre-pass type is created ONCE (shared tyvars across every call
/// site), so an unannotated predicate unified against one owner's field
/// types would silently over-constrain the next -- annotations pin the
/// contract instead.
and private wherePredicateAnnotationCheck (env: TypeEnv) (owner: string) (conjuncts: Expr list) : TypeResult<unit> =
    let rec heads (e: Expr) : string list =
        match e.Kind with
        | ExprKind.ExprApp ({ Kind = ExprKind.ExprVar f }, args) -> f :: (args |> List.collect heads)
        | ExprKind.ExprApp (h, args) -> heads h @ (args |> List.collect heads)
        | ExprKind.ExprBinOp (_, _, l, r) -> heads l @ heads r
        | ExprKind.ExprUnaryOp (_, i) -> heads i
        | ExprKind.ExprIf (c, t, f) -> heads c @ heads t @ heads f
        | ExprKind.ExprTuple es | ExprKind.ExprArrayLit es -> es |> List.collect heads
        | ExprKind.ExprTyped (i, _) -> heads i
        | ExprKind.ExprField (o, _) -> heads o
        | _ -> []
    let offender =
        conjuncts |> List.collect heads |> List.distinct
        |> List.tryPick (fun f ->
            match Map.tryFind f env.StaticFunctions with
            | Some fd when fd.ReturnType.IsNone
                        || fd.Params |> List.exists (fun p -> p.Type.IsNone) -> Some f
            | _ -> None)
    match offender with
    | Some f -> Error (WherePredicateUnannotated (owner, f))
    | None -> Ok ()

and registerTypeDecl (env: TypeEnv) (typeDecl: TypeDecl) : TypeResult<TypeEnv> =
    match typeDecl with
    | TyDeclAlias (name, _typeParams, body) ->
        // Keep the SURFACE body: TDIAlias below stores a lowered IRType, and
        // lowering erases min=/max=, so this is the only record of a bound
        // written inside an alias (see TypeEnv.SurfaceAliases).
        let env = { env with SurfaceAliases = Map.add name body env.SurfaceAliases }
        // Chase one level of alias indirection: if the body is `TyNamed n`
        // where n is itself a registered TDIIndexType or TDIEnumIdx alias,
        // use n's stored body for our own registration. Each registration
        // step already stores its resolved body, so chains of any depth
        // flatten without transitive walking at lookup time.
        //
        // Without this, `type B = A` (where A is an index or enum alias)
        // falls to the generic `_ -> TDIAlias` branch and B is never
        // recognized as an index/enum alias -- using B in array index lists
        // or foreign-key element positions then fails with placeholder
        // records.
        let chasedBody =
            match body with
            | TyNamed (referencedName, _) ->
                match Map.tryFind referencedName env.TypeDefs with
                | Some (TDIIndexType (_, _, refBody)) -> refBody
                | Some (TDIEnumIdx (_, _, _, refBody)) -> refBody
                | _ -> body
            | _ -> body
        let defInfoResult =
            match chasedBody with
            | TyIdx _ | TySymIdx _ | TyAntisymIdx _ | TyOrbIdx _ | TyHermitianIdx _ | TyBoundedIdx _ ->
                let idx = lowerIndexType env 0 chasedBody
                // Nominative-alias rule: the alias name BECOMES the identity
                // tag. Two exceptions, both reachable only from stage 3's
                // `type S = SymIdx<k, IrrepsIdx<spec>>` (no legacy form of
                // these index types can produce an irreps tag, so this is
                // behaviour-preserving for everything that shipped before):
                //   - an irreps identity: fold the name INTO the tag exactly
                //     as the TyIrrepsIdx arm below does, rather than
                //     overwriting it -- otherwise aliasing silently drops the
                //     spec payload and breaks Tag<->IxKind agreement;
                //   - a bad-spec ERROR marker: keep it, so the consumption-site
                //     diagnostic still fires through the alias.
                let named =
                    match idx.Tag with
                    | Some (IrrepsTag (_, triples)) ->
                        { idx with Tag = Some (mkIrrepsTag (Some name) triples) }
                    | _ when idx.IxKind = IxKErrorIrrepsBadSpec -> idx
                    // A depth >= 2 wreath record keeps its "__orbidx" sentinel.
                    // Here Tag IS the kind channel (the IR validator enforces
                    // Tag<->IxKind agreement) and there is no parameterized tag
                    // format for a level list the way mkIrrepsTag has one for a
                    // spec, so overwriting it with the alias name would break
                    // agreement and lose the kind. NOMINATIVE aliasing of a
                    // wreath class is therefore deferred WITH its storage:
                    // `type R = OrbIdx<[...], n>` names the class for
                    // readability but mints no distinct identity. Depth <= 1
                    // normalizes to a Sym/Antisym/plain record and takes the
                    // ordinary nominative path below, exactly like
                    // `type S = SymIdx<2, n>`.
                    | _ when idx.IxKind = IxKOrbit -> idx
                    | _ -> { idx with Tag = Some name; IxKind = ixKindOfTag (Some name) }
                Ok (TDIIndexType (name, named, chasedBody))
            | TyDepIdx _ | TyRaggedIdx _ | TyRaggedIdxOpaque ->
                let idx = lowerIndexType env 0 chasedBody
                Ok (TDIIndexType (name, idx, chasedBody))
            | TyIrrepsIdx _ ->
                // Nominative-alias rule: the alias name is FOLDED INTO the
                // irreps identity tag (mkIrrepsTag (Some name) ...), so two
                // aliases of the same spec are DISTINCT types while anonymous
                // IrrepsIdx<spec> unifies with either (Unify's name-permissive
                // rule). The plain-index arm's `Tag = Some name` overwrite
                // would drop the spec payload and break Tag<->IxKind
                // agreement. A bad-spec marker keeps its error tag so the
                // consumption-site check still fires through the alias.
                let idx = lowerIndexType env 0 chasedBody
                let named =
                    match idx.Tag with
                    | Some (IrrepsTag (_, triples)) ->
                        { idx with Tag = Some (mkIrrepsTag (Some name) triples) }
                    | _ -> idx
                Ok (TDIIndexType (name, named, chasedBody))
            | TyPgIrrepsIdx _ ->
                // THE STAGE-3 ALIAS FIX, REPLAYED for the pg tag (section 7's 5b-i
                // checklist item). Identical reasoning one member over: the
                // nominative-alias rule folds the alias name INTO the
                // pg-irreps identity tag (mkPgIrrepsTag group (Some name) ...)
                // rather than overwriting Tag with the bare name, so two
                // aliases of the same (group, spec) are DISTINCT types while
                // anonymous PgIrrepsIdx<G, spec> unifies with either. The
                // plain-index arm's `Tag = Some name` overwrite would drop the
                // group and the spec payload AND break the Tag<->IxKind
                // agreement the IR validator enforces. A bad-spec marker keeps
                // its error tag so the consumption-site check still fires
                // through the alias.
                let idx = lowerIndexType env 0 chasedBody
                let named =
                    match idx.Tag with
                    | Some (PgIrrepsTag (group, _, entries)) ->
                        { idx with Tag = Some (mkPgIrrepsTag group (Some name) entries) }
                    | _ -> idx
                Ok (TDIIndexType (name, named, chasedBody))
            | TyEnumIdx valuesExpr ->
                // Static-evaluate the array literal to extract values. Each
                // element must be either an int literal (with optional negation)
                // or a string literal. Mixed kinds are a type error -- the two
                // backings (int64_t vs std::string) cannot share one EnumIdx.
                let raw =
                    match valuesExpr.Kind with
                    | ExprKind.ExprArrayLit elems ->
                        elems |> List.choose (fun e ->
                            match e.Kind with
                            | ExprKind.ExprLit (LitInt n) -> Some (EVInt n)
                            | ExprKind.ExprUnaryOp (OpNeg, { Kind = ExprKind.ExprLit (LitInt n) }) -> Some (EVInt (-n))
                            | ExprKind.ExprLit (LitString s) -> Some (EVString s)
                            | _ -> None)
                    | _ -> []
                let hasInt = raw |> List.exists (function EVInt _ -> true | _ -> false)
                let hasString = raw |> List.exists (function EVString _ -> true | _ -> false)
                if hasInt && hasString then
                    Error (EnumIdxMixedKinds name)
                else
                    let extent = int64 raw.Length
                    let idx = {
                        Id = env.Builder.FreshId(); Rank = 1
                        Extent = IRLit (IRLitInt extent)
                        Symmetry = SymNone; Tag = Some name; IxKind = ixKindOfTag (Some name)
                        Kind = SDimension; Dependencies = []
                    }
                    Ok (TDIEnumIdx (name, idx, raw, chasedBody))
            | _ ->
                // `type B = Field<min=0.0, max=1.0>` hides the bound in the
                // alias BODY, where no use site can see it -- the annotation
                // `x: B` carries no TyBounded at all, so today the bound is
                // silently dropped. Catch it at the declaration.
                match boundedAggregateError env (sprintf "type alias '%s'" name) body with
                | Some e -> Error e
                | None -> Ok (TDIAlias (lowerTypeExpr env body))
        defInfoResult |> Result.map (fun defInfo -> registerTypeDef name defInfo env)

    | TyDeclStruct (name, typeParams, fields, constraints, isStatic) ->
        // Mutual member types are forbidden as field types -- alias
        // transparency would silently erase the constraint.
        let memberMisuse =
            fields |> List.tryPick (fun f ->
                match mutualMemberNamesIn env f.Type with
                | [] -> None
                | n :: _ -> Some (StructFieldMutualType (name, f.Name, n)))
        // A struct field STORES a value, so its declared type has to name a
        // real tag; a wildcard there would erase the discipline (see
        // irTypeHasTagWildcard).
        // A field's bounds do NOT survive as a TyBounded -- the parser
        // normalizes BOTH spellings (`f: T<min=a, max=b>` and `f: T in lo ..
        // hi`) into `FieldDecl.Bound` and leaves `f.Type` bare. So the field
        // check reads the Bound slot and classifies the field's own type,
        // rather than walking for a TyBounded that is no longer there.
        // Without it, `structConjuncts` desugars the bound into a conjunct and
        // the failure surfaces as "where-constraint must be a boolean
        // expression, got Array<Bool like ...>" -- a constraint the user never
        // wrote.
        let boundedAggregateField =
            fields |> List.tryPick (fun f ->
                let site = sprintf "struct %s, field '%s'" name f.Name
                let fromBound =
                    if f.Bound.IsSome then
                        boundedAggregateNoun env f.Type
                        |> Option.map (fun noun -> BoundsOnAggregate (site, noun, "the field's value"))
                    else None
                fromBound
                |> Option.orElseWith (fun () -> boundedAggregateError env site f.Type))
        let wildcardField =
            fields |> List.tryPick (fun f ->
                if irTypeHasTagWildcard (lowerTypeExpr env f.Type)
                then Some (TagWildcardNotParam (sprintf "struct %s, field '%s'" name f.Name))
                else None)
        // `static struct` DECLARES the static-eligibility fence instead of
        // leaving it to be inferred at each use: every field type must be a
        // shape StaticEval can carry as a StaticValue. Ordinary structs skip
        // this entirely -- nothing about them changes.
        let staticFieldErr =
            if not isStatic then None
            else
                let rec why (t: TypeExpr) : string option =
                    match t with
                    | TyInt32 | TyInt64 | TyFloat32 | TyFloat64
                    | TyBool | TyString | TyChar | TyUnit -> None
                    | TyBounded (b, _, _) -> why b
                    | TyTuple ts -> ts |> List.tryPick why
                    // A width-only `Tuple<N>` has INFERRED element types, so
                    // the static world has no shape to carry. The generic
                    // catch-all below would say "index and other structured
                    // types", which misnames it.
                    | TyTupleWidth _ ->
                        Some "`Tuple<N>` leaves its element types inferred -- write them out as `(T1, T2)`"
                    | TyNamed (n, _) ->
                        match n with
                        | "Int" | "Int32" | "Int64" | "Float" | "Float64" | "Double"
                        | "Float32" | "Bool" | "String" | "Char" | "Nat" | "Void" -> None
                        | _ when env.StaticStructs.Contains n -> None
                        | _ when (match lookupTypeDef n env with Some (TDIStruct _) -> true | _ -> false) ->
                            Some (sprintf "'%s' is a struct declared without `static`" n)
                        | _ when (match lookupTypeDef n env with Some (TDIVariant _) -> true | _ -> false) ->
                            Some (sprintf "'%s' is a sum type, which the static world cannot represent" n)
                        | _ when (match lookupTypeDef n env with Some (TDIAlias _) -> true | _ -> false) ->
                            // Aliases are transparent in the value world but the
                            // surface type is all we have here; be explicit.
                            Some (sprintf "'%s' is a type alias -- write the underlying primitive" n)
                        | _ -> Some (sprintf "type '%s' is not a static shape" n)
                    | TyArray _ | TyAbstractArray _ -> Some "array types are not statically evaluable"
                    | TyFunc _ -> Some "function types are not statically evaluable"
                    | TyPoly _ -> Some "arity-polymorphic packs are not statically evaluable"
                    | TyVar _ -> Some "type variables are not statically evaluable"
                    | _ -> Some "index and other structured types are not statically evaluable"
                fields |> List.tryPick (fun f ->
                    why f.Type |> Option.map (fun w -> StaticStructField (name, f.Name, w)))
        // Bounds that CROSS, decided statically: `min=` strictly above
        // `max=`. Only the INCLUSIVE spelling is checked -- `in lo .. hi` is
        // pre-existing syntax whose empty case (`in 0 .. 0`) has always been
        // accepted, and an empty solution set is a warning-class event per
        // the constrained-index plan, not an error. Only fires when both
        // ends fold; a bound referencing an earlier field is not decidable
        // here and is left to the conjuncts.
        let invertedBoundErr =
            fields |> List.tryPick (fun f ->
                match f.Bound with
                | Some { Lo = Some lo; Hi = Some hi; HiInclusive = true } ->
                    let fold e =
                        match evalStaticValueExpr env e with
                        | Ok (StaticEval.SVInt v) -> Some v
                        | _ -> None
                    match fold lo, fold hi with
                    | Some l, Some h when l > h ->
                        Some (BoundsInverted (sprintf "struct %s, field '%s'" name f.Name, string l, string h))
                    | _ -> None
                | _ -> None)
        match memberMisuse |> Option.orElse wildcardField
                           |> Option.orElse boundedAggregateField
                           |> Option.orElse staticFieldErr
                           |> Option.orElse invertedBoundErr with
        | Some e -> Error e
        | None ->
            let env = if isStatic then { env with StaticStructs = Set.add name env.StaticStructs } else env
            let fieldTypes = fields |> List.map (fun f -> (f.Name, lowerTypeExpr env f.Type))
            // Field range refinements: SEQUENTIAL scoping -- a bound may
            // reference only EARLIER fields and statics, and call only
            // statically-evaluable functions (the closed forms that lower
            // into type positions).
            let boundScopeErr =
                let callables =
                    Set.union (StaticEval.knownBuiltinNames ())
                              (env.StaticFunctions |> Map.toSeq |> Seq.map fst |> Set.ofSeq)
                let rec firstErr priorFields flds =
                    match flds with
                    | [] -> None
                    | (f: FieldDecl) :: rest ->
                        let checkRefs (e: Expr) =
                            StaticEval.collectFreeNames e
                            |> Set.filter (fun n ->
                                not (Set.contains n priorFields)
                                && not (env.StaticValues.ContainsKey n)
                                && not (callables.Contains n))
                            |> Set.toList
                            |> List.tryHead
                            |> Option.map (fun bad ->
                                StructBoundScope (name, f.Name, bad))
                        let err =
                            match f.Bound with
                            | Some b -> [b.Lo; b.Hi] |> List.choose id |> List.tryPick checkRefs
                            | None -> None
                        match err with
                        | Some e -> Some e
                        | None -> firstErr (Set.add f.Name priorFields) rest
                firstErr Set.empty fields
            match boundScopeErr with
            | Some e -> Error e
            | None ->
            // Full conjunct list (declared where + desugared field bounds)
            // via the SHARED helper -- StaticEval uses the same one for
            // fold-time checks, so the two worlds cannot drift.
            let allConstraints = structConjuncts fields constraints
            // Validate all conjuncts at declaration: fields bound, each
            // conjunct must typecheck to Bool. Hard errors -- a malformed
            // constraint is a compile error, not a silently dropped check.
            let mutable conjEnv = env
            for (fn, ft) in fieldTypes do
                let fId = conjEnv.Builder.FreshId()
                conjEnv <- bindVarSimple fn fId ft conjEnv
            let conjCheck =
                allConstraints |> List.fold (fun acc c ->
                    acc |> Result.bind (fun () ->
                        match inferExpr conjEnv c with
                        | Ok tC ->
                            match unify conjEnv.Subst tC.Type (IRTScalar ETBool) with
                            | Ok () -> Ok ()
                            | Error _ ->
                                Error (StructWhereNotBool (name, ppIRType tC.Type))
                        | Error e ->
                            Error (StructWhereError (name, formatTypeError e))))
                    (Ok ())
            wherePredicateAnnotationCheck env name allConstraints |> Result.bind (fun () ->
            conjCheck |> Result.map (fun () ->
                registerTypeDef name (TDIStruct (name, typeParams, fieldTypes, allConstraints)) env))

    | TyDeclSum (name, typeParams, variants) ->
        let variantTypes = variants |> List.map (fun v ->
            (v.Name, v.Data |> Option.map (lowerTypeExpr env)))
        let env' = registerTypeDef name (TDIVariant (name, typeParams, variantTypes)) env
        Ok (variants |> List.fold (fun e v ->
            registerVariantTag v.Name name (v.Data |> Option.map (lowerTypeExpr env)) e) env')

    | TyDeclMutualGroup (members, constraints) ->
        // Members register as ordinary transparent aliases -- unannotated use
        // of the underlying types stays completely unconstrained. The group
        // itself is a side registration consumed at binding sites.
        let groupId = members |> List.head |> fst
        let envAfterMembers =
            members |> List.fold (fun acc (mname, mty) ->
                acc |> Result.bind (fun e ->
                    if e.MutualMembers.ContainsKey mname || e.MutualGroups.ContainsKey mname then
                        Error (MutualMemberDupGroup mname)
                    else registerTypeDecl e (TyDeclAlias (mname, [], mty)))) (Ok env)
        envAfterMembers |> Result.bind (fun env1 ->
            // Resolve each member to a struct or scalar kind.
            let memberKindsR =
                members |> List.map (fun (mname, mty) ->
                    match lowerTypeExpr env1 mty with
                    | IRTNamed s ->
                        match Map.tryFind s env1.TypeDefs with
                        | Some (TDIStruct _) -> Ok (mname, MMStruct s)
                        | _ -> Error (MutualMemberNotStruct (mname, s))
                    | (IRTScalar _ | IRTUnitAnnotated _ | IRTIdxTagged _ | IRTNat _) as sc ->
                        Ok (mname, MMScalar sc)
                    | otherTy ->
                        Error (MutualMemberBadAlias (mname, ppIRType otherTy)))
                |> sequenceResults
            memberKindsR |> Result.bind (fun memberKindList ->
                let memberKinds = Map.ofList memberKindList
                let structFields s =
                    match Map.tryFind s env1.TypeDefs with
                    | Some (TDIStruct (_, _, fields, _)) -> fields |> List.map fst |> Set.ofList
                    | _ -> Set.empty
                // Position-sensitive reference validation: member field paths,
                // bare scalar members, folded statics. Call HEADS are open --
                // guards are inlined runtime code on the backend, so any
                // Bool-typed callable is legal (the conjunct typecheck below
                // rejects unknown/ill-typed callees); args still classify.
                let rec walkConjunct (e: Expr) =
                    match e.Kind with
                    | ExprKind.ExprLit _ -> Ok ()
                    | ExprKind.ExprField ({ Kind = ExprKind.ExprVar m }, f) when memberKinds.ContainsKey m ->
                        (match memberKinds.[m] with
                         | MMStruct s when (structFields s).Contains f -> Ok ()
                         | MMStruct s -> Error (MutualUnknownField (m, f, s))
                         | MMScalar _ -> Error (MutualScalarBare (m, f)))
                    | ExprKind.ExprVar m when memberKinds.ContainsKey m ->
                        (match memberKinds.[m] with
                         | MMScalar _ -> Ok ()
                         | MMStruct _ -> Error (MutualStructNeedsField m))
                    | ExprKind.ExprVar n ->
                        if env1.StaticValues.ContainsKey n then Ok ()
                        else Error (MutualUnknownIdent n)
                    | ExprKind.ExprApp ({ Kind = ExprKind.ExprVar _ }, args) ->
                        // A call argument may pass a WHOLE member (predicates
                        // take struct-typed params: conserved(P1, P2)); other
                        // args classify as usual.
                        let walkArg (a: Expr) =
                            match a.Kind with
                            | ExprKind.ExprVar m when memberKinds.ContainsKey m -> Ok ()
                            | _ -> walkConjunct a
                        args |> List.fold (fun acc a -> acc |> Result.bind (fun () -> walkArg a)) (Ok ())
                    | ExprKind.ExprBinOp (_, _, l, r) -> walkConjunct l |> Result.bind (fun () -> walkConjunct r)
                    | ExprKind.ExprUnaryOp (_, inner) -> walkConjunct inner
                    | ExprKind.ExprIf (c, t, f) -> walkAll [c; t; f]
                    | ExprKind.ExprTuple es | ExprKind.ExprArrayLit es -> walkAll es
                    | ExprKind.ExprTyped (inner, _) -> walkConjunct inner
                    | _ -> Error MutualUnsupportedExpr
                and walkAll es =
                    match es with
                    | [] -> Ok ()
                    | e :: rest -> walkConjunct e |> Result.bind (fun () -> walkAll rest)
                let refCheck =
                    constraints |> List.fold (fun acc c -> acc |> Result.bind (fun () -> walkConjunct c)) (Ok ())
                refCheck
                |> Result.bind (fun () -> wherePredicateAnnotationCheck env1 groupId constraints)
                |> Result.bind (fun () ->
                    // Typecheck each conjunct with members bound (struct
                    // members as their nominal type, scalars as themselves)
                    // and require Bool.
                    let mutable conjEnv = env1
                    for (mname, kind) in memberKindList do
                        let mTy = match kind with MMStruct s -> IRTNamed s | MMScalar t -> t
                        let mId = conjEnv.Builder.FreshId()
                        conjEnv <- bindVarSimple mname mId mTy conjEnv
                    let typeCheckAll =
                        constraints |> List.fold (fun acc c ->
                            acc |> Result.bind (fun () ->
                                match inferExpr conjEnv c with
                                | Ok tC ->
                                    match unify conjEnv.Subst tC.Type (IRTScalar ETBool) with
                                    | Ok () -> Ok ()
                                    | Error _ ->
                                        Error (MutualConstraintNotBool (groupId, ppIRType tC.Type))
                                | Error e ->
                                    Error (MutualConstraintError (groupId, formatTypeError e))))
                            (Ok ())
                    typeCheckAll |> Result.map (fun () ->
                        let info = { GroupId = groupId; Members = memberKindList; Constraints = constraints }
                        { env1 with
                            MutualGroups = Map.add groupId info env1.MutualGroups
                            MutualMembers =
                                memberKindList |> List.fold (fun m (mname, _) ->
                                    Map.add mname groupId m) env1.MutualMembers }))))

// 11b. Zonking -- Final Type Resolution
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

let private isMixedEnumIdxValues (valuesExpr: Expr) : bool =
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

let rec private walkTypeExprForMixedEnumIdx (ty: TypeExpr) : Expr list =
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
let rec private staticValueToImportExpr sp (v: StaticEval.StaticValue) : Expr =
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
let rec private rewriteImportedStaticRefs (seed: Map<string, StaticEval.StaticValue>) (expr: Expr) : Expr =
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
        match Map.tryFind (sprintf "%s.%s" alias field) seed with
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
        let bound = parms |> List.map (fun p -> p.Name)
        inheritSpan expr (ExprLambda (parms, whereClause, goWithout bound body))
    | _ -> expr
and private rewriteImportedStaticRefsStmt (seed: Map<string, StaticEval.StaticValue>) (stmt: Stmt) : Stmt =
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
let private seedImportedStaticsIntoDecls (seed: Map<string, StaticEval.StaticValue>) (decls: Located<Decl> list) : Located<Decl> list =
    if Map.isEmpty seed then decls else
    decls |> List.map (fun locDecl ->
        match locDecl.Value with
        | DeclStatic binding ->
            { locDecl with Value = DeclStatic { binding with Value = rewriteImportedStaticRefs seed binding.Value } }
        | DeclFunction fd when fd.IsStatic ->
            let paramNames = fd.Params |> List.map (fun p -> p.Name)
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
let private importedStaticSeed (env: TypeEnv) (decls: Located<Decl> list) : Map<string, StaticEval.StaticValue> =
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
                    |> Map.fold (fun acc2 k v -> Map.add (sprintf "%s.%s" alias k) v acc2) acc
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
let rec private collectAppRankErrors (subst: Subst) (expr: TypedExpr) : CompileError list =
    let here =
        match expr.Kind with
        | TExprApp (tFunc, tArgs) ->
            match subst.Resolve tFunc.Type with
            | FuncElem (paramTys, _) ->
                match firstArgRankClash subst paramTys (tArgs |> List.map (fun a -> a.Type)) with
                | Some (i, pr, ar, pTy, aTy) ->
                    let arg = List.item i tArgs
                    [ { Error = ArgRankMismatch (i + 1, pr, ar,
                                                 ppIRType (subst.Resolve pTy),
                                                 ppIRType (subst.Resolve aTy))
                        Span = arg.Span
                        Context = []
                        Code = None } ]
                | None -> []
            | _ -> []
        | _ -> []
    here @ (typedExprChildren expr |> List.collect (collectAppRankErrors subst))

/// Every expression a zonked declaration carries, for the sweep above.
let private declExprs (decl: TypedDecl) : TypedExpr list =
    let ofFunc (f: TypedFunctionDecl) = [f.Body]
    match decl with
    | TDeclLet b | TDeclStatic b -> [b.Value]
    | TDeclFunction f -> ofFunc f
    | TDeclImpl impl -> impl.Methods |> List.collect ofFunc
    | TDeclType _ | TDeclInterface _ | TDeclUnit _ | TDeclImport _ -> []

// 12. Module and Program
let checkModule (env: TypeEnv) (modul: ModuleDecl) : TypedModule * TypeEnv * CompileError list =
    // Fresh module: drop any span the PREVIOUS module's decl loop left in the
    // side-channel. The static-assertion errors below are raised before this
    // module's first `checkDecl` (which is where the per-decl reset lives), so
    // without this they would be located in the previous module's coordinates.
    // `typeCheck` resets on entry too; this covers module-to-module inside one
    // compilation, and callers that reach checkProgram by another route.
    resetCurrentStmtSpan ()
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
    let env = { env with StaticValues = Map.fold (fun acc k v -> Map.add k v acc) env.StaticValues crossModuleStaticSeed }
    let declsForStaticResolution = seedImportedStaticsIntoDecls crossModuleStaticSeed modul.Decls
    let env, staticAssertErrors =
        match StaticEval.resolveStatics declsForStaticResolution with
        | Ok (se, failures) ->
            let env' = { env with StaticValues = Map.fold (fun acc k v -> Map.add k v acc) env.StaticValues se.Values }
            let errs =
                failures |> List.map (fun (f: StaticEval.StaticFailure) ->
                    let msg =
                        sprintf "`let static %s` does not evaluate at compile time: %s. `let static` asserts a compile-time value -- use plain `let` for values computed at runtime."
                            (f.Names |> String.concat ", ") f.Reason
                    locateError f.Span env' (Other msg))
            env', errs
        | Error msg ->
            let span =
                modul.Decls
                |> List.tryPick (fun d -> match d.Value with DeclStatic _ -> Some d.Span | _ -> None)
                |> Option.defaultValue noSpan
            env, [locateError span env (Other msg)]
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
    
    for d in modul.Decls do
        // Pre-validation: inline TyEnumIdx<[mixed values]> occurrences. The
        // alias-site check in registerTypeDecl catches `type X = EnumIdx<[...]>`
        // declarations but not inline embeddings like `let x: Array<EnumIdx<[1,
        // "two"]> like ...> = ...`. Each finding becomes an error attached to
        // the decl's span.
        let mixedFindings = collectMixedEnumIdxInDecl d.Value
        for _ in mixedFindings do
            let err = Other "Inline EnumIdx<[...]> has mixed value kinds (integer and string literals in the same list). The runtime backing must be one or the other (int64_t or std::string)."
            let ce = locateError d.Span currentEnv err
            errors <- ce :: errors

        let declName =
            match d.Value with
            | DeclLet b -> sprintf "in let binding '%s'" (match b.Pattern.Kind with PatternKind.PatVar n -> n | _ -> "_")
            | DeclStatic b -> sprintf "in static binding '%s'" (match b.Pattern.Kind with PatternKind.PatVar n -> n | _ -> "_")
            | DeclFunction f -> sprintf "in function '%s'" f.Name
            | DeclType td ->
                match td with
                | TyDeclAlias (n, _, _) | TyDeclStruct (n, _, _, _, _) | TyDeclSum (n, _, _) -> sprintf "in type '%s'" n
                | TyDeclMutualGroup (members, _) ->
                    sprintf "in mutual group '%s'" (members |> List.map fst |> String.concat ", ")
            | DeclInterface i -> sprintf "in interface '%s'" i.Name
            | DeclImpl impl -> sprintf "in impl for '%A'" impl.ForType
            | DeclImport (qn, _) -> sprintf "in import '%s'" (String.concat "." qn)
            | DeclUnit u -> sprintf "in unit '%s'" u.Name
        let envWithCtx = pushContext declName currentEnv
        match checkDecl envWithCtx d.Value with
        | Ok (td, env') ->
            decls <- td :: decls
            // Carry forward env' but restore original context (don't nest)
            currentEnv <- { env' with Context = currentEnv.Context }
        | Error err ->
            let ce = locateError d.Span currentEnv err
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

    let typedModule = { Name = Some modul.Name; Decls = List.rev decls }
    // Zonk: resolve all IRTInfer through the substitution, default unsolved to Float64
    let zonked = zonkModule currentEnv.Subst typedModule
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
    (zonked, currentEnv, staticAssertErrors @ List.rev errors @ rankErrors)

let checkProgram (program: Program) : TypedProgram * IRBuilder * CompileError list * string list =
    let env = emptyEnv ()
    let mutable modules = []
    let mutable allErrors = []
    let mutable moduleExports = Map.empty<string, TypeModuleExport>
    for modul in program.Modules do
        let envWithExports = { env with ModuleExports = moduleExports }
        let (tm, finalEnv, errs) = checkModule envWithExports modul
        modules <- tm :: modules
        allErrors <- allErrors @ errs
        // Build export from this module's checked environment
        let moduleName = modul.Name |> String.concat "."
        let export : TypeModuleExport = {
            Variables = finalEnv.Variables |> Map.filter (fun k _ -> not (k.Contains(".")))
            TypeDefs = finalEnv.TypeDefs |> Map.filter (fun k _ -> not (k.Contains(".")))
            VariantTags = finalEnv.VariantTags
            Units = finalEnv.Units
            StaticFunctions = finalEnv.StaticFunctions |> Map.filter (fun k _ -> not (k.Contains(".")))
            StaticValues = finalEnv.StaticValues |> Map.filter (fun k _ -> not (k.Contains(".")))
        }
        moduleExports <- Map.add moduleName export moduleExports
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

let typeCheck (program: Program) : Result<TypedProgram * IRBuilder * string list, CompileError list> =
    // AST -> AST expansions, in order: ML-op elaboration first (so grad()
    // sees the generated functions as plain Blade source and can inline
    // them), then grad() expansion. Both synthesize ordinary declarations
    // that flow through validation, checking, lowering and codegen exactly
    // like user code.
    // Provider-backed statics: install the compile-time data reader before
    // ANY resolveStatics pass runs (the ML and PPL elaborations each run
    // their own; all inherit the fold through StaticEval's hook).
    Blade.ProviderStatics.install ()
    // The constrained-index counting layer's `idx_card(R)` builtin, on the
    // same footing and for the same reason: registered before ANY
    // resolveStatics pass, so every elaboration's own statics can size
    // against it.
    Blade.StructIdxSpec.install ()
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
                    (sprintf "the Lie-discharge post-accept guard tripped while validating this body: %s" msg)))
    IdeDeductions.reset ()
    // Staged-former unfold FIRST: `static method_for/object_for/for`
    // argument lists elaborate to plain formers before any other stage
    // (ML/PPL/math/grad and the checker never see ExprStatic).
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
    match Blade.Grad.expand program with
    | Error diags -> Error (diags |> List.map (compileErrorOfDiagnostic ["grad expansion"]))
    | Ok program ->
    let validationErrors = IndexTypeValidator.validateProgram program
    if not validationErrors.IsEmpty then
        let compileErrors =
            validationErrors |> List.map (fun e ->
                { Error = Other e.Message; Span = e.Span; Context = [e.DeclName]; Code = Some "BL4003" })
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
                    Other (sprintf "internal compiler error: equivariance certificate validation disagrees with the elaboration checker for '%s': %s. This is a bug in the Blade compiler, not in your program -- please report it (the certificate itself was accepted by the checking authority; only the typed second opinion dissents)" owner detail)
                  Span = span
                  Context = [ "equiv certificate validation" ]
                  Code = Some "BL9004" })
        let errors = errors @ repIce
        if errors.IsEmpty then Ok (tp, builder, warnings)
        else Error errors
