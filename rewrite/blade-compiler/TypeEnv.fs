// Type-checking environment: schemes, instantiate/generalize, the TypeEnv
// record and its variable/type-definition registries (audit §4:
// Check/TypeEnv.fs). Extracted verbatim from TypeCheck.fs (Phase 3).
module Blade.TypeEnv

open Blade.Ast
open Blade.IR
open Blade.Types
open Blade.TypedAst
open Blade.Unify

/// Instantiate a type scheme: replace each quantified variable with a fresh
/// inference variable, so each use site gets independent type constraints.
let instantiate (subst: Subst) (scheme: TypeScheme) : IRType =
    if scheme.QuantifiedVars.IsEmpty then
        scheme.Body
    else
        let mapping =
            scheme.QuantifiedVars
            |> List.map (fun v ->
                let fresh = subst.Fresh()
                // Propagate arity constraints to the fresh variable
                match fresh with
                | IRTInfer freshId -> subst.CopyArityConstraint(v, freshId)
                | _ -> ()
                (v, fresh))
            |> Map.ofList
        let rec replace ty =
            match ty with
            | IRTInfer id ->
                Map.tryFind id mapping |> Option.defaultValue ty
            | IRTTuple ts -> IRTTuple (ts |> List.map replace)
            | IRTArrow (slots, ret, identity) ->
                let replaceSlot = function
                    | SIdx idx -> SIdx idx
                    | SIdxVirt idx -> SIdxVirt idx
                    | SVal t -> SVal (replace t)
                IRTArrow (slots |> List.map replaceSlot, replace ret, identity)
            | IRTComputation t -> IRTComputation (replace t)
            | IRTPoly (t, v) -> IRTPoly (replace t, v)
            | IRTDist (order, elem, axes) -> IRTDist (order, replace elem, axes)
            | IRTLoop lt ->
                IRTLoop { lt with
                            ArrayTypes = lt.ArrayTypes |> List.map replace
                            KernelType = lt.KernelType |> Option.map replace }
            | _ -> ty  // IRTScalar, IRTUnit, IRTNamed, IRTNat (no inference vars to replace)
        replace scheme.Body

// ============================================================================
// 2. Type Environment
// ============================================================================

/// Variable assignability levels tracked during type checking.
/// Maps to binding forms: static → ReadOnly, let → Assignable, let mut → MutPassable
type Assignability =
    | ReadOnly      // static: not assignable, generalizable
    | Assignable    // let: assignable in scope, not passable to mut params
    | MutPassable   // let mut: assignable + passable to mut params

/// Variable binding information tracked during type checking.
type VarInfo = {
    VarId: IRId
    Type: IRType
    Identity: ArrayIdentity option
    Assign: Assignability
    /// The TypedExpr this variable was bound to, for <@> resolution.
    TypedValue: TypedExpr option
    /// If Some, this binding is polymorphic (let-generalized).
    /// Variable lookup instantiates fresh type variables from the scheme.
    Scheme: TypeScheme option
}

/// Registered type definition
type TypeDefInfo =
    | TDIAlias of IRType
    | TDIStruct of name: string * typeParams: string list * fields: (string * IRType) list * constraints: Expr list
    | TDIVariant of name: string * typeParams: string list * variants: (string * IRType option) list
    | TDIIndexType of name: string * idx: IRIndexType * body: TypeExpr
    | TDIEnumIdx of name: string * idx: IRIndexType * values: EnumValue list * body: TypeExpr

/// What a mutual-group member aliases: a registered struct (constraints
/// reference its fields as `P.f`) or a scalar type (referenced bare).
type MutualMemberKind =
    | MMStruct of structName: string
    | MMScalar of IRType

/// A `type P1 = T1 and P2 = T2 where ...` group. Members stay transparent
/// aliases; this record carries the joint constraint for binding-site checks.
type MutualGroupInfo = {
    /// First member's name — doubles as the group's display id.
    GroupId: string
    /// Members in declaration order.
    Members: (string * MutualMemberKind) list
    /// Untyped where-conjuncts, validated at declaration time.
    Constraints: Expr list
}

/// Exported bindings from a type-checked module, for cross-module imports
type TypeModuleExport = {
    Variables: Map<string, VarInfo>
    TypeDefs: Map<string, TypeDefInfo>
    VariantTags: Map<string, string * IRType option>
    Units: Map<string, UnitSig>
    /// Static function ASTs from this module. Imported alongside Variables
    /// so eta-reduced DepIdx in an importing module can inline a static
    /// function defined in this one. The body is needed for the inlining;
    /// the TypeEnv-side StaticFunctions map is the consumer.
    StaticFunctions: Map<string, FunctionDecl>
    /// Folded `let static` VALUES from this module (the TypeEnv-side
    /// StaticValues map, filtered to bare names — see checkProgram's export
    /// builder). Mirrors StaticFunctions: an importing module's checkModule
    /// pre-pass seeds these under "alias.name" (qualified import) or "name"
    /// (selective import) so cross-module references like `M.k` or a
    /// selectively-imported `k` are visible to that module's OWN static
    /// resolution -- see checkModule's importedStaticSeed / the
    /// rewriteImportedStaticRefs substitution ahead of StaticEval.resolveStatics.
    StaticValues: Map<string, StaticEval.StaticValue>
}

/// Type checking environment
type TypeEnv = {
    Variables: Map<string, VarInfo>
    TypeDefs: Map<string, TypeDefInfo>
    /// Variant tag -> (parentTypeName, payloadType option)
    VariantTags: Map<string, string * IRType option>
    Subst: Subst
    Builder: IRBuilder
    OuterScope: Map<string, VarInfo>
    InPolyContext: bool
    CurrentCommGroups: int list list
    /// Interface name -> InterfaceDecl
    Interfaces: Map<string, InterfaceDecl>
    /// (typeName, methodName) -> (mangledFuncVarId, funcType)
    ImplMethods: Map<string * string, IRId * IRType>
    /// Unit name -> canonical UnitSig
    Units: Map<string, UnitSig>
    /// Context stack for error reporting, e.g. ["in function 'foo'"]
    Context: string list
    /// Exports from previously type-checked modules
    ModuleExports: Map<string, TypeModuleExport>
    /// Static function ASTs, populated during checkModule's pre-pass.
    /// Used by lowerIndexTypeList to inline eta-reduced DepIdx bodies —
    /// `DepIdx<O, f>` desugars to `lambda(i) -> Idx<f(i)>`, and the
    /// substitution into the inner extent requires access to f's body.
    StaticFunctions: Map<string, FunctionDecl>
    /// Static VALUE bindings (`let static x = ...`), resolved once in
    /// checkModule's pre-pass via StaticEval. Mirrors StaticFunctions and
    /// exists so compile-time-known scalars (e.g. a `replicate` count) can be
    /// resolved during type-checking, before the lowering phase's own
    /// resolveStatics runs. Best-effort: entries that don't statically
    /// evaluate are simply absent.
    StaticValues: Map<string, StaticEval.StaticValue>
    /// Non-fatal diagnostics accumulated during type-checking. The field
    /// is a mutable ResizeArray so functional updates (`{ env with ... }`)
    /// share the same collector — warnings emitted from any scope land
    /// in one place. Surfaced through `typeCheck`'s Ok return; runs even
    /// when the program also has hard errors (warnings + errors aren't
    /// mutually exclusive, but the current Ok-only plumbing skips them
    /// on the error path — extending to both paths is a future tweak).
    Warnings: ResizeArray<string>
    /// Dist value provenance: varId → the value's source set (underlying
    /// array names for module-level dists, `func.param` license tokens for
    /// Dist-typed parameters). Consumed by Dist ± dispatch and where-clause
    /// call-site discharge. Mutable dictionary shared by reference across
    /// functional env updates, like Warnings.
    Provenance: System.Collections.Generic.Dictionary<IRId, Set<string>>
    /// Functions carrying registered custom where-clause conjuncts:
    /// funcName → (paramNames, conjuncts). Populated by checkFunctionDecl;
    /// consulted at call sites for constraint discharge.
    FuncConstraints: System.Collections.Generic.Dictionary<string, string list * (string * string list) list>
    /// Mutually constrained alias groups: groupId → group info.
    MutualGroups: Map<string, MutualGroupInfo>
    /// Member alias name → owning groupId, for annotation scanning.
    MutualMembers: Map<string, string>
    /// Functions whose declared return type introduces a mutual group
    /// (e.g. `-> (P1, P2)`): funcName → groupId. The joint check is emitted
    /// at the return site, so annotated callers don't re-check. Mutable
    /// dictionary shared by reference, like FuncConstraints.
    MutualReturnFuncs: System.Collections.Generic.Dictionary<string, string>
}

let emptyEnv () = {
    Variables = Map.empty
    TypeDefs = Map.empty
    VariantTags = Map.empty
    Subst = Subst()
    Builder = IRBuilder()
    OuterScope = Map.empty
    InPolyContext = false
    CurrentCommGroups = []
    Interfaces = Map.empty
    ImplMethods = Map.empty
    Units = Map.empty
    Context = []
    ModuleExports = Map.empty
    StaticFunctions = Map.empty
    StaticValues = Map.empty
    Warnings = ResizeArray<string>()
    Provenance = System.Collections.Generic.Dictionary<IRId, Set<string>>()
    FuncConstraints = System.Collections.Generic.Dictionary<string, string list * (string * string list) list>()
    MutualGroups = Map.empty
    MutualMembers = Map.empty
    MutualReturnFuncs = System.Collections.Generic.Dictionary<string, string>()
}

/// Append a non-fatal diagnostic to the env's warnings collector.
/// The collector is shared by reference across all functional updates
/// of the env, so callsites don't need to thread anything through.
let emitWarning (env: TypeEnv) (msg: string) : unit =
    env.Warnings.Add(msg)

/// Push a context frame onto the environment
let pushContext (ctx: string) (env: TypeEnv) : TypeEnv =
    { env with Context = ctx :: env.Context }

/// Span of the statement currently being type-checked, per async flow
/// (audit §3.4, statement-granularity slice; expression granularity is
/// rewrite work). inferBlock stamps it as it unwraps each StmtSpanned;
/// locateError below prefers it over the caller-supplied (declaration)
/// span, so an error inside a multi-statement body points at the failing
/// STATEMENT rather than the declaration header. inferBlock skips the
/// remaining statements after the first error, so at error-location time
/// the last-stamped span belongs to the statement that failed. Reset at
/// every checkDecl entry so a span cannot leak across declarations.
let private currentStmtSpanStorage = System.Threading.AsyncLocal<Span>()

/// Expression-level span, stamped by inferExpr on entry to every node
/// (full-span AST). Finer than the statement span; because inference
/// short-circuits on the first error, the last stamp lies on the path to
/// the failing expression. Cleared whenever a new statement is entered so
/// a leaf from a PREVIOUS statement can never win.
let private currentExprSpanStorage = System.Threading.AsyncLocal<Span>()

let setCurrentExprSpan (s: Span) = currentExprSpanStorage.Value <- s

let currentExprSpan () : Span =
    match box currentExprSpanStorage.Value with
    | null -> noSpan
    | _ -> currentExprSpanStorage.Value

let setCurrentStmtSpan (s: Span) =
    currentStmtSpanStorage.Value <- s
    currentExprSpanStorage.Value <- noSpan
let resetCurrentStmtSpan () =
    currentStmtSpanStorage.Value <- noSpan
    currentExprSpanStorage.Value <- noSpan

let currentStmtSpan () : Span =
    match box currentStmtSpanStorage.Value with
    | null -> noSpan
    | _ -> currentStmtSpanStorage.Value

/// Wrap a TypeError with span and context into a CompileError. Precision
/// order: active expression span > active statement span > the caller's
/// span (typically the enclosing declaration's).
let locateError (span: Span) (env: TypeEnv) (err: TypeError) : CompileError =
    let exprSpan = currentExprSpan ()
    let stmtSpan = currentStmtSpan ()
    let span =
        if exprSpan.StartLine > 0 then exprSpan
        elif stmtSpan.StartLine > 0 then stmtSpan
        else span
    { Error = err; Span = span; Context = env.Context; Code = None }

/// Format a TypeError as a human-readable string
let formatTypeError (err: TypeError) : string =
    match err with
    | UnboundVariable name -> sprintf "Unbound variable: %s" name
    | TypeMismatch (exp, act) -> sprintf "Type mismatch: expected %s, got %s" (ppIRType exp) (ppIRType act)
    | ArityMismatch (exp, act) -> sprintf "Arity mismatch: expected %d args, got %d" exp act
    | InvalidArrayCapture name -> sprintf "Lambda cannot capture array '%s'" name
    | InvalidApplication funcTy -> sprintf "Cannot apply non-function type: %A" funcTy
    | PatternTypeMismatch (pat, ty) -> sprintf "Pattern '%s' incompatible with type %A" pat ty
    // ---- Promoted variants (Stage 5). Text reproduced verbatim. ----
    | IndexTagMismatchNamed (expected, actual) -> sprintf "Array index tag mismatch: slot expects '%s' but argument has type '%s'." expected actual
    | IndexTagMismatchAnon expected -> sprintf "Array index tag mismatch: slot expects named tag '%s' but argument is an anonymous index value." expected
    | CrossNominalIndexArith (left, right) -> sprintf "Cross-nominal index-type arithmetic: cannot combine values of distinct index domains '%s' and '%s'." left right
    | CrossAnonIndexArith (left, right) -> sprintf "Cross-nominal index-type arithmetic: cannot combine values of distinct anonymous index domains (#%d vs #%d)." left right
    | CompoundBareWildcard rank -> sprintf "A bare wildcard `_` cannot index a compound axis: it pins no coordinate (the result would just be the array itself). Index with a full %d-tuple, pinning at least one coordinate." rank
    | CompoundWildcardArity (rank, tupleLen) -> sprintf "Wildcard compound indexing must use a FULL-arity tuple: this compound axis has rank %d, so write all %d coordinates with `_` marking each free axis (got a %d-tuple). Short tuples (without wildcards) pin a leading prefix instead: B((c0, ..., cj))." rank rank tupleLen
    | CompoundAllFree rank -> sprintf "Compound index with all %d coordinates free (`_`) pins nothing -- the result is the array itself. Drop the index, or pin at least one coordinate." rank
    | CompoundOverSupplied (rank, got) -> sprintf "Compound index over-supplied: this array's compound axis has rank %d (mask is %d-dimensional), so it takes at most a %d-tuple like B((c0, ..., c%d)); got a %d-tuple." rank rank rank (rank - 1) got
    | CompoundNeedsTuple rank -> sprintf "Compound index must be a single tuple: write B((c0, ..., cj)) with inner parentheses, not the flat form B(c0, ..., cj). A CompoundIdx<mask> axis of rank %d is indexed as one joint tuple, full or partial (formalism 4.5 / poly-indexing 5.4)." rank
    | RaggedIdxNeedsPrior func -> sprintf "function '%s': RaggedIdx requires at least one prior index in the array's index list -- the ragged extent is a per-row function of the OUTER iteration position (formalism 4.4). Add an outer index, e.g. Array<T like Idx<n>, RaggedIdx<lens>>." func
    | DecompactDimRange (dim, totalDims) -> sprintf "decompact: dimension %d is out of range for a rank-%d array (valid dims 0..%d)" dim totalDims (totalDims - 1)
    | DecompactPlainAxis dim -> sprintf "decompact: dimension %d is a plain (rank-1, non-symmetric) axis; there is nothing to decompact. decompact pulls a component out of a compact group (SymIdx/AntisymIdx/HermitianIdx)." dim
    | DecompactLastSlotOnly (slots, slot) -> sprintf "decompact: only a compact group in the LAST index slot, optionally preceded by plain free Idx dimensions, is supported by codegen (the chained to-the-right peel shape). The array here has %d index slots with the compact group at slot %d." slots slot
    | TransposeAxisRange (axis, totalDims) -> sprintf "transpose: axis %d is out of range for a rank-%d array (valid axes 0..%d)" axis totalDims (totalDims - 1)
    | TransposeAxesEqual (axisA, axisB) -> sprintf "transpose: the two axes must differ (got [%d, %d]); swapping an axis with itself is the identity" axisA axisB
    | TransposeWithinGroup rank -> sprintf "transpose: swapping two dimensions within a single rectangular index group (rank %d) is not yet supported." rank
    | UnitMismatch (context, left, right) -> sprintf "Unit mismatch in %s: %s vs %s" context left right
    | IntrinsicBindArrayFailed op -> sprintf "%s(): failed to bind array type after unification" op
    | IntrinsicNeedsArray op -> sprintf "%s() requires an array as argument" op
    | IntrinsicScalarOnly name -> sprintf "%s applies to scalars; map it over the array elementwise (e.g. method_for(A) <@> lambda(x) -> %s(x) |> compute)." name name
    | IntrinsicNotComplex name -> sprintf "%s is not defined for complex operands." name
    | IntrinsicNeedsNumeric name -> sprintf "%s expects a numeric operand." name
    | AbsNeedsNumericScalar got -> sprintf "abs expects a numeric scalar operand, got %s" got
    | IntrinsicComplexScalarOnly name -> sprintf "%s applies to complex scalars; map it over the array elementwise (e.g. method_for(A) <@> lambda(z) -> %s(z) |> compute)." name name
    | IntrinsicNeedsComplex (name, got) -> sprintf "%s expects a complex operand, got %s" name got
    | ReduceEmptyArray extent -> sprintf "reduce() rejects statically empty arrays (extent = %d). Empty input has no defined reduction without an identity; supply one with the 3-arg form `reduce(arr, op, init)`." extent
    | ProdsumExtentMismatch (a, b) -> sprintf "prodsum() operands must share one extent: got %d and %d" a b
    | GramNeedsRank2 (leftRank, rightRank) -> sprintf "gram(A, B): both operands must be rank-2 (matrix) arrays; got rank-%d and rank-%d. gram contracts the trailing axis: A (m x n), B (p x n) -> m x p." leftRank rightRank
    | ArrayLitLength (got, expected, axisTag) ->
        let axis = match axisTag with Some t -> sprintf " for axis '%s'" t | None -> ""
        sprintf "Array literal%s has %d elements, but the annotation's extent is %d" axis got expected
    | ObjectForKernel got -> sprintf "object_for kernel must be a lambda, reynolds, or zero, but got %A" got
    | ChainOpNeedsMethodFor leftDesc -> sprintf "<@> requires method_for or object_for on the left side, but got %s" leftDesc
    | PlaceholderNeedsAllBound (got, total) -> sprintf "the `_` placeholder needs every other parameter bound: this call supplies %d of %d args. Combine with prefix partial application in two steps, or use a lambda." got total
    | GroupKeysRank1 -> "group_keys: all key arrays must be rank-1 and share the same outer index (same length). Compound grouping requires each i-th element of every key array to refer to the same record."
    | FallbackNeedsArrays (leftDesc, rightDesc) -> sprintf "<|:> (allocated-fallback) reads the LEFT array where its storage holds a cell and the right array elsewhere, so both operands must be arrays; got %s and %s. For value-level choice (first nonzero wins) over scalars or computations, use <|>." leftDesc rightDesc
    | FallbackSymmetricLeft -> "<|:> over a symmetric/antisymmetric/Hermitian left operand is not yet supported: symmetric A requires symmetric allocation (formalism 2.6), which the compiler cannot yet verify. decompact(A, d) to dense first."
    | FallbackRightNotDense what -> sprintf "<|:> right operand must be a plain dense array (it supplies the value for every cell the left side lacks); got %s." what
    | FallbackRankMismatch (leftRank, rightRank) -> sprintf "<|:> operands must cover the same index space: the left side spans %d dimension(s) but the right side has rank %d." leftRank rightRank
    | CumulantOrderPositive order -> sprintf "cumulant: order must be >= 1, got %d" order
    | CumulantNeedsDist got -> sprintf "cumulant expects cumulant(d, k) where d is a Dist value (a dist(...) binding or Dist-typed parameter); got %s" got
    | DistOpUndefined (left, right) -> sprintf "this operator is not defined on Dist values (left: %s, right: %s): dists support scalar * (multilinearity), + and - of independent dists, and component projection via cumulant(d, k)" left right
    | EnumIdxMixedKinds name -> sprintf "EnumIdx '%s' has mixed value kinds: integer and string literals in the same EnumIdx<[...]> aren't allowed. The runtime backing must be one or the other (int64_t or std::string)." name
    | ImplMissingMethods (iface, typeName, methods) -> sprintf "impl %s for %s is missing required methods: %s" iface typeName methods
    | StructFieldDuplicate (structName, field) -> sprintf "struct %s: field '%s' assigned more than once" structName field
    | StructNoField (structName, field) -> sprintf "struct %s has no field '%s'" structName field
    | StructSpreadBase structName -> sprintf "struct %s: a spread base must be a variable or field path — bind it with let first" structName
    | StructSpreadNotStruct (structName, got) -> sprintf "struct %s: spread base must be a %s value, got %s" structName structName got
    | StructSpreadRedundant structName -> sprintf "struct %s: every field is provided explicitly — the '..' spread is redundant" structName
    | StructMissingField (structName, field) -> sprintf "struct %s: missing field '%s' in constructor" structName field
    | StructFieldType (structName, field, expected, actual) -> sprintf "struct %s, field '%s': expected %s, got %s" structName field expected actual
    | UnknownStructType name -> sprintf "unknown struct type '%s' in constructor" name
    | StructWhereNotBool (structName, got) -> sprintf "struct %s: where-constraint must be a boolean expression, got %s" structName got
    | StructWhereError (structName, inner) -> sprintf "struct %s where-constraint: %s" structName inner
    | WherePredicateUnannotated (owner, func) -> sprintf "static function '%s' is called from a where-clause of '%s': annotate all its parameter types and its return type" func owner
    | UnknownWhereConstraint (func, name, vocab) -> sprintf "function '%s': unknown where-clause constraint '%s' (registered constraints: %s)" func name vocab
    | DistOrderCompileTime func -> sprintf "function '%s': Dist order must be a compile-time integer >= 1 (a literal, `let static`, or static-function call): Dist<order, Elem like I1, ..., Ik>" func
    | ImmutableStaticAssign name -> sprintf "Cannot assign to '%s': static bindings are immutable" name
    | MutParamNotArray (func, param) -> sprintf "function '%s': parameter '%s' is `mut` but not array-typed. Only array parameters can be mutated in place (scalars pass by value); return the new scalar instead." func param
    | MutualBindJointly (typeName, describe, lowerNames) -> sprintf "type '%s' belongs to mutual group (%s); bind the group jointly: let (%s): (%s) = ..." typeName describe lowerNames describe
    | MutualDirectElementsOnly describe -> sprintf "mutual member types (group %s) may appear only as direct elements of a joint tuple annotation" describe
    | MutualMixedGroups -> "annotation mixes members of different mutual groups"
    | MutualDuplicateMember describe -> sprintf "duplicate mutual member in annotation (group %s)" describe
    | MutualIncompleteAnnotation describe -> sprintf "mutual group (%s) is incomplete in this annotation; all group members must appear together" describe
    | MutualJointAnnotationOnly describe -> sprintf "mutual member types (group %s) may appear only in a joint let annotation or a function's declared return type" describe
    | MutualParamMemberType (func, param, memberName) -> sprintf "function '%s': parameter '%s' uses mutual member type '%s'; mutual member types may appear only in a joint let annotation or a function's declared return type" func param memberName
    | MutualBindTuple names -> sprintf "a mutual group (%s) must be bound jointly with a tuple of variables: let (x, y): (%s) = ..." names names
    | MutualReturnTupleElements describe -> sprintf "mutual group (%s): declared return type must list every member as a direct tuple element" describe
    | StructFieldMutualType (structName, field, memberName) -> sprintf "struct %s, field '%s': mutual member type '%s' may not be used as a field type" structName field memberName
    | MutualMemberDupGroup memberName -> sprintf "mutual-group member '%s' is already part of another group" memberName
    | MutualMemberNotStruct (memberName, name) -> sprintf "mutual-group member '%s': '%s' is not a declared struct" memberName name
    | MutualMemberBadAlias (memberName, got) -> sprintf "mutual-group member '%s' must alias a struct or scalar type, got %s" memberName got
    | MutualUnknownField (memberName, field, structName) -> sprintf "mutual constraint references unknown field '%s.%s' (struct %s)" memberName field structName
    | MutualScalarBare (memberName, field) -> sprintf "'%s' aliases a scalar; reference it bare, not '%s.%s'" memberName memberName field
    | MutualStructNeedsField memberName -> sprintf "'%s' aliases a struct; reference one of its fields as '%s.<field>'" memberName memberName
    | MutualUnknownIdent name -> sprintf "identifier '%s' in a mutual-group constraint must be a group member, a member field path, or a static" name
    | MutualUnsupportedExpr -> "unsupported expression form in a mutual-group constraint"
    | MutualConstraintNotBool (groupId, got) -> sprintf "mutual-group constraint (group %s) must be a boolean expression, got %s" groupId got
    | MutualConstraintError (groupId, inner) -> sprintf "mutual-group constraint (group %s): %s" groupId inner
    | ProviderStreamNeedsVar alias -> sprintf "%s.stream expects a provider array variable" alias
    | ProviderReadWindowBounds (alias, lo, hi, n) -> sprintf "%s.read_window bounds [%d, %d) must satisfy 0 <= lo < hi <= %d (the packed extent)" alias lo hi n
    | ProviderReadWindowLiteralExtent alias -> sprintf "%s.read_window needs a literal packed extent" alias
    | ProviderReadWindowPacked alias -> sprintf "%s.read_window applies to PACKED variables (leading SymIdx/AntisymIdx); use %s.read for dense variables" alias alias
    | ProviderReadWindowNeedsVar alias -> sprintf "%s.read_window expects a provider array variable as its first argument" alias
    | ProviderReadWindowArgs alias -> sprintf "%s.read_window expects (variable, lo, hi) with integer-literal bounds" alias
    | ProviderWriteNeedsArray alias -> sprintf "%s.write expects an array as its second argument (the variable to store)" alias
    | ProviderWriteNamedBinding alias -> sprintf "%s.write stores a NAMED array binding (its name becomes the store variable's name): bind the value first (let A = ...; %s.write(\"path\", A))" alias alias
    | ProviderWriteArgs alias -> sprintf "%s.write expects (\"path\", array): a string-literal store path and the array to write" alias
    | IrrepsIdxArgMismatch (pos, expected, actual) -> sprintf "argument %d: IrrepsIdx mismatch: the parameter expects %s but the argument carries %s. IrrepsIdx identity is the spec (plus nominative alias name) — equal total_dim does not make two irreps spaces interchangeable." pos expected actual
    | IrrepsIdxSpec detail -> sprintf "IrrepsIdx: %s. The spec must be a static array of (l, parity, mult) int triples — a `let static` binding or an inline literal like IrrepsIdx<[(0, 0, 2), (1, 1, 2)]>." detail
    | IrrepsIdxSpecFn (func, detail) -> sprintf "function '%s': IrrepsIdx: %s. The spec must be a static array of (l, parity, mult) int triples — a `let static` binding or an inline literal like IrrepsIdx<[(0, 0, 2), (1, 1, 2)]>." func detail
    | ComplexArity got -> sprintf "complex expects exactly two float components — complex(re, im) — got %d argument(s)" got
    | CumulantOrderExceeds (order, carried) -> sprintf "cumulant: order %d exceeds the dist's carried order %d — insufficient stochastic order. Construct with a higher order (dist(A, %d)) or project a carried component." order carried order
    | DistOrderDisagree (op, leftOrder, rightOrder) -> sprintf "dist %s: orders disagree (%d vs %d) — carry the same stochastic order on both sides" op leftOrder rightOrder
    | DistNotIndependent (op, source1, source2, steering) -> sprintf "dist %s: cumulants combine only for independent distributions — sources '%s' and '%s' are not declared independent; %s" op source1 source2 steering
    | PplConstraintNeedsImport (func, bare) -> sprintf "function '%s': constraint '%s' belongs to the ppl module — add `import ppl as <alias>` and write `where <alias>.%s(...)`" func bare bare
    | StructBoundScope (structName, field, bad) -> sprintf "struct %s, field '%s': bound references '%s' — bounds may reference only earlier fields and statics" structName field bad
    | ProviderImportByModule (suggestion, providers) -> sprintf "provider modules are imported by module name — write `import %s as <alias>` (the Providers.* spelling was removed; registered providers: %s)" suggestion providers
    | ProviderNoSelectiveImport pname -> sprintf "provider module '%s' does not support selective import — use `import %s as <alias>` and call <alias>.load/read/write" pname pname
    | IndexTypeArithForbidden name -> sprintf "Arithmetic on index type '%s' is not permitted. Index types are nominal labels — for value-level arithmetic on positions, use virtual array iteration (which produces plain ints); for new index types derived from arithmetic, type-level construction is a separate workstream not yet implemented." name
    | Other msg -> msg

/// Format a CompileError with location and context
let formatCompileError (err: CompileError) : string =
    let loc =
        if err.Span.StartLine > 0 then
            match err.Span.File with
            | Some f -> sprintf "%s:%d:%d" f err.Span.StartLine err.Span.StartCol
            | None -> sprintf "%d:%d" err.Span.StartLine err.Span.StartCol
        else ""
    let msg = formatTypeError err.Error
    let context =
        err.Context
        |> List.rev
        |> List.map (sprintf "  %s")
        |> String.concat "\n"
    if loc = "" && context = "" then msg
    elif context = "" then sprintf "%s: %s" loc msg
    else sprintf "%s: %s\n%s" loc msg context

/// CompileError -> unified Diagnostic. The code comes from the raiser when
/// present (CompileError.Code), else from the TypeError variant.
let diagnosticOfCompileError (e: CompileError) : Blade.Diagnostics.Diagnostic =
    let code =
        match e.Code with
        | Some c -> c
        | None ->
            match e.Error with
            | UnboundVariable _ -> "BL2001"
            | TypeMismatch _ -> "BL3001"
            | ArityMismatch _ -> "BL3002"
            | InvalidApplication _ -> "BL3003"
            | PatternTypeMismatch _ -> "BL3004"
            | InvalidArrayCapture _ -> "BL3005"
            // ---- Promoted variants (Stage 5) ----
            | UnitMismatch _ -> "BL3006"
            | IntrinsicBindArrayFailed _ | IntrinsicNeedsArray _ | IntrinsicScalarOnly _
            | IntrinsicNotComplex _ | IntrinsicNeedsNumeric _ | AbsNeedsNumericScalar _
            | IntrinsicComplexScalarOnly _ | IntrinsicNeedsComplex _ | ComplexArity _
            | ReduceEmptyArray _ | ProdsumExtentMismatch _ | GramNeedsRank2 _
            | ArrayLitLength _ | ObjectForKernel _ | ChainOpNeedsMethodFor _
            | PlaceholderNeedsAllBound _ | GroupKeysRank1 | CumulantOrderPositive _
            | CumulantOrderExceeds _ | CumulantNeedsDist _ | DistOrderDisagree _
            | DistNotIndependent _ | DistOpUndefined _ | EnumIdxMixedKinds _
            | ImplMissingMethods _
            | FallbackNeedsArrays _ | FallbackSymmetricLeft
            | FallbackRightNotDense _ | FallbackRankMismatch _ -> "BL3007"
            | StructFieldDuplicate _ | StructNoField _ | StructMissingField _
            | StructFieldType _ | UnknownStructType _ | StructBoundScope _
            | StructSpreadBase _ | StructSpreadNotStruct _ | StructSpreadRedundant _ -> "BL3008"
            | StructWhereNotBool _ | StructWhereError _ | WherePredicateUnannotated _
            | PplConstraintNeedsImport _
            | UnknownWhereConstraint _ -> "BL4001"
            | DistOrderCompileTime _ -> "BL4002"
            | IndexTagMismatchNamed _ | IndexTagMismatchAnon _ | CrossNominalIndexArith _
            | CrossAnonIndexArith _ | IndexTypeArithForbidden _ | IrrepsIdxArgMismatch _
            | CompoundBareWildcard _ | CompoundWildcardArity _ | CompoundAllFree _
            | CompoundOverSupplied _ | CompoundNeedsTuple _ | RaggedIdxNeedsPrior _
            | IrrepsIdxSpec _ | IrrepsIdxSpecFn _ -> "BL4003"
            | DecompactDimRange _ | DecompactPlainAxis _ | DecompactLastSlotOnly _
            | TransposeAxisRange _ | TransposeAxesEqual _ | TransposeWithinGroup _ -> "BL4004"
            | ImmutableStaticAssign _ | MutParamNotArray _ -> "BL4005"
            | MutualBindJointly _ | MutualDirectElementsOnly _ | MutualMixedGroups
            | MutualDuplicateMember _ | MutualIncompleteAnnotation _ | MutualJointAnnotationOnly _
            | MutualParamMemberType _ | MutualBindTuple _ | MutualReturnTupleElements _
            | StructFieldMutualType _ | MutualMemberDupGroup _ | MutualMemberNotStruct _
            | MutualMemberBadAlias _ | MutualUnknownField _ | MutualScalarBare _
            | MutualStructNeedsField _ | MutualUnknownIdent _
            | MutualUnsupportedExpr | MutualConstraintNotBool _ | MutualConstraintError _ -> "BL4006"
            | ProviderStreamNeedsVar _ | ProviderReadWindowBounds _ | ProviderReadWindowLiteralExtent _
            | ProviderReadWindowPacked _ | ProviderReadWindowNeedsVar _ | ProviderReadWindowArgs _
            | ProviderWriteNeedsArray _ | ProviderWriteNamedBinding _ | ProviderWriteArgs _ -> "BL3007"
            | ProviderImportByModule _ | ProviderNoSelectiveImport _ -> "BL2003"
            | Other _ -> "BL3999"
    Blade.Diagnostics.mkError code (Blade.Diagnostics.Codes.phaseOfCode code) e.Span (formatTypeError e.Error)
    |> Blade.Diagnostics.withContext e.Context

/// Unified Diagnostic -> CompileError, for pipeline stages (elaborators)
/// that produce Diagnostics inside typeCheck's CompileError channel. The
/// code and span survive; extra context is appended outermost.
let compileErrorOfDiagnostic (extraContext: string list) (d: Blade.Diagnostics.Diagnostic) : CompileError =
    { Error = Other d.Message
      Span = d.Span
      Context = d.Context @ extraContext
      Code = Some d.Code }

let bindVar name info (env: TypeEnv) =
    { env with Variables = Map.add name info env.Variables }

let bindVarSimple name varId ty env =
    bindVar name { VarId = varId; Type = ty; Identity = None
                   Assign = ReadOnly; TypedValue = None; Scheme = None } env


let bindVarFull name varId ty identity assign typedValue env =
    bindVar name { VarId = varId; Type = ty; Identity = identity
                   Assign = assign; TypedValue = typedValue; Scheme = None } env

/// Bind a polymorphic (let-generalized) variable.
let bindVarPoly name varId ty identity assign typedValue scheme env =
    bindVar name { VarId = varId; Type = ty; Identity = identity
                   Assign = assign; TypedValue = typedValue
                   Scheme = Some scheme } env

let lookupVar name env = Map.tryFind name env.Variables
let lookupTypeDef name env = Map.tryFind name env.TypeDefs

/// Convert AST BindingMut to Assignability.
let assignOfBindingMut = function
    | BindConst -> ReadOnly    // static / let const → immutable
    | BindLet -> Assignable    // let → assignable in scope
    | BindMut -> MutPassable   // let mut → assignable + mut-passable

let enterScope env =
    { env with OuterScope = Map.foldBack Map.add env.Variables env.OuterScope }

let registerTypeDef name info (env: TypeEnv) =
    { env with TypeDefs = Map.add name info env.TypeDefs }

/// Register a loaded provider module's struct types into the type-check env and
/// return the binding's module-struct type. The provider's emitted dims/vars
/// structs are registered as-is; a synthetic top-level struct named `name` is
/// added so the loaded binding carries `.dims` / `.vars` fields, letting field
/// access (e.g. `sample.vars.temp`) resolve to a variable's real Array type.
/// Pure (no file IO) so it can be unit-tested with a mock module; reading the
/// file metadata that produces `pm` is the caller's concern.
let registerProviderModule (env: TypeEnv) (name: string) (pm: IRModule) : TypeEnv * IRType =
    let envS =
        pm.Types |> List.fold (fun e td ->
            match td with
            | IRTDStruct (n, fields) -> registerTypeDef n (TDIStruct (n, [], fields, [])) e
            | _ -> e) env
    let moduleStruct = TDIStruct (name, [], [("dims", IRTNamed "dims"); ("vars", IRTNamed "vars")], [])
    (registerTypeDef name moduleStruct envS, IRTNamed name)


/// Check if a variant type is a pure enum (all constructors have no data)
let isEnumType (env: TypeEnv) (parentName: string) : bool =
    match Map.tryFind parentName env.TypeDefs with
    | Some (TDIVariant (_, _, variants)) -> variants |> List.forall (fun (_, d) -> d.IsNone)
    | _ -> false

let registerVariantTag tag parentName payload (env: TypeEnv) =
    { env with VariantTags = Map.add tag (parentName, payload) env.VariantTags }

/// Resolve a UnitExpr AST node to a canonical UnitSig
let rec resolveUnitExpr (units: Map<string, UnitSig>) (expr: UnitExpr) : Result<UnitSig, string> =
    match expr with
    | UnitNamed name ->
        match Map.tryFind name units with
        | Some sig' -> Ok sig'
        | None -> Error (sprintf "Unknown unit '%s'" name)
    | UnitMul (a, b) ->
        resolveUnitExpr units a |> Result.bind (fun sa ->
        resolveUnitExpr units b |> Result.map (fun sb ->
            unitMul sa sb))
    | UnitDiv (a, b) ->
        resolveUnitExpr units a |> Result.bind (fun sa ->
        resolveUnitExpr units b |> Result.map (fun sb ->
            unitDiv sa sb))
    | UnitPow (a, n) ->
        resolveUnitExpr units a |> Result.map (fun sa ->
            unitPow sa n)

/// Register a unit declaration in the environment
let registerUnit (env: TypeEnv) (decl: UnitDecl) : TypeEnv =
    let sig' =
        match decl.Definition with
        | None | Some UnitBase ->
            // Base unit: canonical form is {name: 1}
            Map.ofList [(decl.Name, 1)]
        | Some (UnitDerived expr) ->
            match resolveUnitExpr env.Units expr with
            | Ok resolved -> resolved
            | Error msg ->
                eprintfn "Unit error: %s" msg
                Map.ofList [(decl.Name, 1)]  // fallback to base unit
    { env with Units = Map.add decl.Name sig' env.Units }

// ============================================================================
// 2b. Generalization (needs VarInfo defined above)
// ============================================================================

/// Collect free inference vars across all variable types in scope.
let freeInferVarsInEnv (subst: Subst) (vars: Map<string, VarInfo>) : Set<int> =
    vars |> Map.fold (fun acc _ info ->
        Set.union acc (freeInferVars subst info.Type)) Set.empty

/// Generalize a type: quantify over inference vars free in the type
/// but NOT free in the environment.
let generalize (subst: Subst) (envVars: Map<string, VarInfo>) (ty: IRType) : TypeScheme =
    let envFree = freeInferVarsInEnv subst envVars
    let tyFree = freeInferVars subst (subst.Resolve ty)
    let quantified = Set.difference tyFree envFree |> Set.toList
    { QuantifiedVars = quantified; Body = subst.Resolve ty }

// ============================================================================
// 3. Constant Expression Evaluation (for index extents)
// ============================================================================

/// Try to evaluate an AST expression to a compile-time int64.
