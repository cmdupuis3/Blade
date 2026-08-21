// Type-checking environment: schemes, instantiate/generalize, TypeEnv's
// variable/type-def registries (audit sec 4: Check/TypeEnv.fs).
module Blade.TypeEnv

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
                // Copy arity constraints and rank lower bounds too: without the
                // rank copy, a generalized (static) value's deduced bound
                // resets to unbounded/scalar at each use site. Only the
                // ReadOnly/generalized case reaches here (plain `let` shares one var, never instantiates).
                match fresh with
                | IRTInfer freshId ->
                    subst.CopyArityConstraint(v, freshId)
                    subst.CopyRankLowerBound(v, freshId)
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

// 2. Type Environment

/// Variable assignability levels tracked during type checking.
/// Maps to binding forms: static -> ReadOnly, let -> Assignable, let mut -> MutPassable
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
    /// First member's name -- doubles as the group's display id.
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
    /// Static function ASTs from this module, imported alongside Variables so
    /// an importing module's eta-reduced DepIdx can inline a static function
    /// defined here (needs the body; TypeEnv-side StaticFunctions is the consumer).
    StaticFunctions: Map<string, FunctionDecl>
    /// Folded `let static` values, bare names only (see checkProgram's export
    /// builder). Mirrors StaticFunctions: importedStaticSeed /
    /// rewriteImportedStaticRefs seed these under "alias.name" (qualified) or
    /// "name" (selective) ahead of StaticEval.resolveStatics.
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
    /// True while a LAMBDA body is being type-inferred. Unit checks that fire
    /// at scalar position read it to DEFER premature rejections: an unresolved
    /// kernel param contributes "no units" to the first-pass walk, so an
    /// annotation computed against it is provisional until buildApplyInfo
    /// unifies the params and reruns kernelBodyUnits (the authoritative pass).
    /// NOT set for named-function declaration bodies: their unannotated params
    /// are dimensionless by contract, so decl-time strictness is correct there.
    InLambdaBody: bool
    /// True while ANY callable body is being type-inferred -- a lambda body, a
    /// named-function declaration body, or an impl-method body. Those three are
    /// exactly the `enterCallableBody` sites, and exactly the bodies Lowering
    /// runs `forceCallableBody` (S2 + S4) over, so this flag is the
    /// checker-side name for "a `let` bound here WILL be materialized".
    /// Distinct from `InLambdaBody`, which is deliberately unset for
    /// named-function bodies (their unannotated params are dimensionless by
    /// contract).
    ///
    /// Read together with `OuterScope`: since `enterCallableBody` snapshots
    /// everything visible at the body boundary, a name in `Variables` but NOT
    /// in `OuterScope` is bound INSIDE the innermost body -- which is what
    /// `bodyLocalBinding` tests.
    InCallableBody: bool
    /// Names of the `mut` parameters of the function whose body is being
    /// checked (all array-typed -- MutParamNotArray rejects any other kind
    /// first). Rebinding one WHOLE (`a = <array expr>`) cannot reach the
    /// caller, because the C++ ABI passes the `Array<>` wrapper by value; only
    /// element writes travel, through the shared data pointer. A `let mut`
    /// BINDING, by contrast, may be rebound whole -- that is real rebind
    /// semantics with its own corpus (memfree/015, 016) -- and both bind
    /// `MutPassable`, so assignability alone cannot tell them apart. This set
    /// is how `assignTargetError` does.
    MutArrayParams: Set<string>
    CurrentCommGroups: int list list
    /// Interface name -> InterfaceDecl
    Interfaces: Map<string, InterfaceDecl>
    /// (typeName, methodName) -> (mangledFuncVarId, funcType)
    ImplMethods: Map<string * string, IRId * IRType>
    /// Unit name -> canonical UnitSig
    Units: Map<string, UnitSig>
    /// The MAGNITUDE an enclosing annotation says the value being checked is
    /// supposed to have, threaded down from the annotated-let seam. Read by
    /// the scalar +/- conversion seam so each operand converts straight into
    /// the unit that was asked for -- ONE factor per operand -- instead of
    /// joining at the left operand and correcting the sum afterwards, which
    /// rounds twice and computes in a magnitude nobody chose. Applied only
    /// when it agrees dimensionally with what the operands actually produce,
    /// so it can never turn a real unit error into a conversion.
    UnitTarget: UnitSig option
    /// Context stack for error reporting, e.g. ["in function 'foo'"]
    Context: string list
    /// Exports from modules type-checked earlier in this compilation
    ModuleExports: Map<string, TypeModuleExport>
    /// Static function ASTs, populated in checkModule's pre-pass. Lets
    /// lowerIndexTypeList inline eta-reduced DepIdx bodies (`DepIdx<O, f>`
    /// desugars to `lambda(i) -> Idx<f(i)>`; substitution needs f's body).
    StaticFunctions: Map<string, FunctionDecl>
    /// `let static x = ...` bindings, resolved once via StaticEval in
    /// checkModule's pre-pass, so compile-time-known scalars (e.g. a
    /// `replicate` count) resolve ahead of lowering's own resolveStatics.
    /// Best-effort: non-evaluable entries are simply absent.
    StaticValues: Map<string, StaticEval.StaticValue>
    /// Type aliases' SURFACE bodies, by name. `TypeDefs` stores an alias as a
    /// LOWERED `TDIAlias`, which loses `min=`/`max=`; the element-bound guard
    /// synthesis needs the bound EXPRESSIONS, so it resolves alias chains
    /// through this map instead. Populated by registerTypeDecl per `type X = ...`.
    SurfaceAliases: Map<string, TypeExpr>
    /// Names declared `static struct` (the static-eligibility fence).
    /// Registration validates fields against StaticValue shapes and records
    /// the name on success, so later static structs can nest earlier ones.
    /// A name set, not a TDIStruct flag: only decl-time checks and the
    /// constrained-index layers consult it.
    StaticStructs: Set<string>
    /// Non-fatal diagnostics accumulated during type-checking. A mutable
    /// ResizeArray so `{ env with ... }` updates share one collector across
    /// scopes. Surfaced only via `typeCheck`'s Ok return; skipped on the error path.
    Warnings: ResizeArray<string>
    /// Dist value provenance: varId -> source set (underlying array names for
    /// module-level dists, `func.param` tokens for Dist params). Consumed by
    /// Dist +/- dispatch and where-clause discharge. Shared by reference, like Warnings.
    Provenance: System.Collections.Generic.Dictionary<IRId, Set<string>>
    /// Custom where-clause conjuncts per function: funcName -> (paramNames,
    /// conjuncts). Populated by checkFunctionDecl; consulted at call sites for discharge.
    FuncConstraints: System.Collections.Generic.Dictionary<string, string list * (string * string list) list>
    /// Parameter metadata for callables with DEFAULT parameter values:
    /// callee name -> (paramName, surface type annotation, surface default)
    /// per param, in declaration order. Populated by checkFunctionDecl and by
    /// let bindings whose value is a defaults-carrying lambda; consulted by
    /// the surface call-site desugar (omitted trailing args re-type the
    /// default at the call site). Name-keyed like FuncConstraints, and shares
    /// its known shadowing weakness. Shared by reference.
    FuncDefaults: System.Collections.Generic.Dictionary<string, (string * TypeExpr option * Expr option) list>
    /// Mutually constrained alias groups: groupId -> group info.
    MutualGroups: Map<string, MutualGroupInfo>
    /// Member alias name -> owning groupId, for annotation scanning.
    MutualMembers: Map<string, string>
    /// Functions whose return type introduces a mutual group (`-> (P1, P2)`):
    /// funcName -> groupId. Joint check emitted at return; callers don't re-check. Shared by reference.
    MutualReturnFuncs: System.Collections.Generic.Dictionary<string, string>
    /// Callee name -> the 0-based positions of its `mut` parameters. A `mut`
    /// parameter grants the callee WRITE access to the caller's array, so the
    /// caller has to have write access to grant: formalism 2.7 lists only
    /// `let mut x = e` as passable to a `mut` param. The check needs the
    /// callee's DECLARATION at the call site, and the function type carries
    /// only param types, so the positions ride here. Name-keyed like
    /// FuncConstraints/FuncDefaults, and shares their known shadowing
    /// weakness. Shared by reference.
    MutParamPositions: System.Collections.Generic.Dictionary<string, int list>
    /// Callee name -> how its return's UNIT is built from its arguments':
    /// `(exponents, residual)` means the result measures
    /// `residual * PROD_i (unit of argument i) ^ exponents[i]`.
    ///
    /// A generic signature's return is DEDUCED, and a `T^1 -> T^0` pair shares
    /// ONE inference variable between the parameter's element and the return --
    /// but direct application never unifies parameters against arguments (that
    /// variable has to stay open for per-call-site monomorphization), so the
    /// substitution never learns the caller's unit and every unit rule read a
    /// bare variable. Propagating the ARGUMENT's unit unchanged is not the fix:
    /// `mean` preserves it, `variance` SQUARES it. What transfers is the
    /// exponent the body derives, which is what this records.
    ///
    /// Computed once per declaration by probing the typed body with a synthetic
    /// base dimension per generic parameter (checkFunctionDecl), consumed by
    /// `unitStampedReturn` at the call site. Absent = no claim, which is the
    /// pre-existing silence; a body the unit walk cannot read is never entered.
    /// Name-keyed like MutParamPositions, and shares its shadowing weakness.
    /// Shared by reference.
    FuncUnitTransform: System.Collections.Generic.Dictionary<string, int list * UnitSig>
    /// Named functions' `where comm(...)` groups (by param index): funcName ->
    /// int list list. Populated by checkFunctionDecl; must survive
    /// eta-expansion (etaExpandFunctionKernel) onto the loop-kernel wrapper, or
    /// `object_for(f)` on a `where comm` function silently produces DENSE output. Shared by reference.
    FuncCommGroups: System.Collections.Generic.Dictionary<string, int list list>
    /// Named functions' `where anticomm(...)` groups (by param index):
    /// funcName -> int list list. Signed twin of FuncCommGroups, same seam: if
    /// not re-attached to the synthesized kernel, `object_for(f) <@> (A, A)`
    /// silently falls back to dense. Shared by reference.
    FuncAntisymGroups: System.Collections.Generic.Dictionary<string, int list list>
    /// Stage-3 deduction, early tier: per-function parameter NAMES plus
    /// ADJACENT-pair swap parities (n params -> n-1 entries), from
    /// checkFunctionDecl for fixed-arity functions. buildApplyInfo consults it
    /// so an eta-expanded `object_for(f)` gets f's deduced symmetry and real
    /// param names, without reanalyzing. Shared by reference.
    FuncDeducedPairs: System.Collections.Generic.Dictionary<string, string list * Blade.Deduce.Parity list>
    /// Stage-3 interprocedural sign-linearity: per-function, per-param sign
    /// parities in decl order (checkFunctionDecl, fixed-arity only). SOdd
    /// means `f(.., -x, ..) = -f(..)`; the call rule uses it to propagate
    /// PNeg on e.g. `mymean(x - y)`, where callee-and-args-invariant alone
    /// gives only PBottom. Keyed by BINDER ID (`FuncId`), not name (a
    /// shadowing local can't borrow the law); DECL ORDER resolution (self/forward -> SUnknown) needs no fixpoint. Shared by reference.
    FuncSignParities: System.Collections.Generic.Dictionary<IRId, Blade.Deduce.SignParity list>
    /// Binder IDs of every NAMED function declaration (`function f`, static or
    /// not, including the static pre-pass registration and any imported
    /// module's functions -- the set is shared by reference across the whole
    /// program, like Warnings).
    ///
    /// The one consumer is `buildCaptures`: a named function lowers to an
    /// IRCallable emitted at C++ GLOBAL scope, so a lambda body that calls one
    /// never needs it on the capture list -- the body already emits the
    /// callable's own (possibly monomorphized) name. Left on the list it
    /// becomes a dead parameter that the call site still forwards by SOURCE
    /// name, which is not a C++ declaration whenever the callee was
    /// monomorphized: `'scale' was not declared in this scope`.
    ///
    /// Keyed by BINDER ID, not name, so a local that SHADOWS a function name
    /// still captures (its VarId is a different binder).
    DeclaredFuncIds: System.Collections.Generic.HashSet<IRId>
    /// CERTIFIED half of the typed equivariance lattice (FuncRepSpec below is
    /// the speculative half): per-function rep signatures for functions
    /// carrying an `__ml_equiv` conjunct (a source `where ml.equiv(G)` pin, or
    /// an elaborator stamp -- read uniformly). Recorded at checkFunctionDecl
    /// from ZONKED types; keyed by BINDER ID (shadowing reason, as
    /// FuncSignParities). DeduceRep trusts this as an AXIOM -- pins aren't validated at typecheck. Shared by reference.
    FuncRepSigs: System.Collections.Generic.Dictionary<IRId, Blade.DeduceRep.RepSigT>
    /// SPECULATIVE half: summaries DEDUCED this pass, their dependency
    /// closures, and decl order, per candidate group. Analysis only -- never
    /// exported; only source-written pins license checking. Single-pass,
    /// no fixpoint: a forward/self call resolves to nothing (declines silently, correctly). Shared by reference.
    FuncRepSpec: Blade.DeduceRep.RepSpecTable
    /// Stage-3 late tier: PACK symmetry for arity-polymorphic (Poly) kernels
    /// -- funcName -> (packParamName, parity). PInv means invariant under
    /// every permutation at every arity, established by the AC-fold template
    /// (Deduce.deducePackFold) or compositionally (Deduce.packParityOf,
    /// resolving callees via this table in decl order). Packs never claim
    /// PNeg (no signed exchange law) -- fuels suggestions only, never errors. Shared by reference.
    PackDeducedComm: System.Collections.Generic.Dictionary<string, string * Blade.Deduce.Parity>
    /// Named functions' `where` parallel strategies (`omp`/`cuda`/`mpi`) with
    /// param NAMES: funcName -> (paramNames, strategies). Consulted so the
    /// clause survives onto a synthesized loop-kernel wrapper -- without it,
    /// `object_for(f)` on a `where omp(...)` function silently emitted a
    /// SERIAL nest (`Parallel = []` reached lowering). Names matter because
    /// `extractParallelism` resolves `omp(a: n)` by NAME, but wrapper params are renamed (`__k<uid>_<i>`); surfacing remaps by position. Shared by reference.
    FuncParallel: System.Collections.Generic.Dictionary<string, string list * ParallelStrategy list>
    /// Named 2-param functions whose BODY is a bare builtin commutative +
    /// associative op over the two params (`a + b`, `a * b`, `a && b`,
    /// `a || b`, either order): funcName -> true. Populated by
    /// checkFunctionDecl; consulted only by the fold-kernel omp licence check
    /// (`reduce(xs, f)` where `f` carries `where omp`) -- a side channel since
    /// BODY lives nowhere else in TypeEnv. Codegen re-derives the same predicate from IRCallable (CodeGen.foldKernelBuiltinOp); the two must agree.
    FuncFoldBuiltin: System.Collections.Generic.Dictionary<string, bool>
    /// WIDTH SCHEMA side channel (docs/plan-tuples-vs-arg-packs.md 6c, Design
    /// C): parameters DECLARED `Tuple<N>`, keyed by the parameter's binder
    /// VarId -> N. A parameter list is a width schema over the pack's flat leaf
    /// sequence -- unannotated = 1, `Tuple<k>` = k -- and the matcher must read
    /// the WRITTEN annotation, never the inferred type (ruling 1: tuple-ness is
    /// always written). The lowered type `IRTTuple [v1..vk]` cannot be trusted
    /// for this: an unannotated param unifies INTO a tuple as soon as the pack
    /// binds it, so reading widths off the resolved type would make pack widths
    /// inference-dependent -- the exact cliff 5.1 rules out. Populated at
    /// inferLambda / checkFunctionDecl from `TyTupleWidth`; read by
    /// buildApplyInfo's schema matcher and by the direct-call arg pairing.
    /// Shared by reference.
    DeclaredTupleWidths: System.Collections.Generic.Dictionary<IRId, int>
    /// REDUCTION-JOIN leg lists (docs/plan-reduction-joins.md, Form 2): the
    /// SURFACE elements of an array literal bound to a name,
    /// `let ps = [prodsum(a, b), reduce(c, (+))]`. `reduce(ps, (<&!>))` reads
    /// them back and joins the legs into one traversal; the elements never
    /// escape as values, so the surface list is what the join needs and the
    /// TYPED literal (four independent scalars) is not.
    ///
    /// Name-keyed, with FuncDefaults' known shadowing weakness and the same
    /// justification: it is a SURFACE side channel, and the join re-validates
    /// what it finds against the resolved binding (an array literal of the
    /// same width) before using it. Shared by reference.
    JoinLegLists: System.Collections.Generic.Dictionary<string, Expr list>
}

let emptyEnv () = {
    Variables = Map.empty
    TypeDefs = Map.empty
    VariantTags = Map.empty
    Subst = Subst()
    Builder = IRBuilder()
    OuterScope = Map.empty
    InPolyContext = false
    InLambdaBody = false
    InCallableBody = false
    MutArrayParams = Set.empty
    CurrentCommGroups = []
    Interfaces = Map.empty
    ImplMethods = Map.empty
    Units = Map.empty
    UnitTarget = None
    Context = []
    ModuleExports = Map.empty
    StaticFunctions = Map.empty
    StaticValues = Map.empty
    SurfaceAliases = Map.empty
    StaticStructs = Set.empty
    Warnings = ResizeArray<string>()
    Provenance = System.Collections.Generic.Dictionary<IRId, Set<string>>()
    FuncConstraints = System.Collections.Generic.Dictionary<string, string list * (string * string list) list>()
    FuncDefaults = System.Collections.Generic.Dictionary<string, (string * TypeExpr option * Expr option) list>()
    MutualGroups = Map.empty
    MutualMembers = Map.empty
    MutualReturnFuncs = System.Collections.Generic.Dictionary<string, string>()
    MutParamPositions = System.Collections.Generic.Dictionary<string, int list>()
    FuncUnitTransform = System.Collections.Generic.Dictionary<string, int list * UnitSig>()
    FuncCommGroups = System.Collections.Generic.Dictionary<string, int list list>()
    FuncAntisymGroups = System.Collections.Generic.Dictionary<string, int list list>()
    FuncDeducedPairs = System.Collections.Generic.Dictionary<string, string list * Blade.Deduce.Parity list>()
    FuncSignParities = System.Collections.Generic.Dictionary<IRId, Blade.Deduce.SignParity list>()
    DeclaredFuncIds = System.Collections.Generic.HashSet<IRId>()
    FuncRepSigs = System.Collections.Generic.Dictionary<IRId, Blade.DeduceRep.RepSigT>()
    FuncRepSpec = Blade.DeduceRep.RepSpecTable()
    PackDeducedComm = System.Collections.Generic.Dictionary<string, string * Blade.Deduce.Parity>()
    FuncParallel = System.Collections.Generic.Dictionary<string, string list * ParallelStrategy list>()
    FuncFoldBuiltin = System.Collections.Generic.Dictionary<string, bool>()
    DeclaredTupleWidths = System.Collections.Generic.Dictionary<IRId, int>()
    JoinLegLists = System.Collections.Generic.Dictionary<string, Expr list>()
}

/// Structured twin of `TypeEnv.Warnings`: every warning as a coded, spanned
/// `Diagnostic`. One of an AsyncLocal side-channel family (also
/// TypeCheck.PinSuggestions / IdePartial / ML.Equiv.CertSuggestions) that
/// surfaces a fact without widening `typeCheck`'s Ok-only `string list`
/// return (Repl.fs and three test files consume it by shape); this channel
/// drains on both the Ok and error arms, unlike that return type. Lives here
/// (not TypeCheck) because its writer `emitWarning` is TypeEnv-owned and
/// TypeEnv (compile index 156) cannot reference TypeCheck (158); re-exported as `TypeCheck.WarningLog`.
module WarningLog =
    let private slot = new System.Threading.AsyncLocal<Blade.Diagnostics.Diagnostic list>()
    let reset () = slot.Value <- []
    let add (d: Blade.Diagnostics.Diagnostic) = slot.Value <- d :: slot.Value
    let get () : Blade.Diagnostics.Diagnostic list =
        match box slot.Value with
        | null -> []
        | _ -> List.rev slot.Value

/// What the checker DEDUCED vs. what the source ANNOTATED -- an editor
/// otherwise can't tell a rank the user WROTE from one the checker PROVED
/// (both render as the same type). Recording only, at deduction sites;
/// drained by `ide check --json` on both arms into a top-level `deduced[]`
/// array. Hosted in TypeEnv because Zonk (compile index 157) also closes
/// deduced ranks and cannot reference TypeCheck (158). IDE-only:
/// ppType/abstractRenderer has no provenance hook into the REPL renderer.
type DeducedFact =
    /// A parameter whose array RANK came from the body-only rank lower bound
    /// rather than an annotation. `index` is the parameter position.
    | DeducedRank of owner: string * param: string * index: int * rank: int
    /// An ADJACENT-PAIR swap parity the deduction proved, with nothing declared
    /// for that pair. `isAnti` distinguishes antisymm from comm.
    | DeducedPairSym of owner: string * left: string * right: string * index: int * isAnti: bool
    /// The late tier: an arity-polymorphic pack proved invariant under every
    /// permutation at every arity.
    | DeducedPackComm of owner: string * pack: string

module DeducedFacts =
    let private slot = new System.Threading.AsyncLocal<(DeducedFact * Span) list>()
    let reset () = slot.Value <- []
    let add (f: DeducedFact) (span: Span) = slot.Value <- (f, span) :: slot.Value
    let get () : (DeducedFact * Span) list =
        match box slot.Value with
        | null -> []
        | _ -> List.rev slot.Value |> List.distinct

    /// THE ZONK STITCH: hook for the zonk rank auto-close in
    /// `Zonk.zonkType`'s `IRTInfer n` arm (where it builds a rank-k array
    /// from a lower bound instead of defaulting to Float64):
    ///
    ///     Blade.TypeEnv.DeducedFacts.recordZonkClosedRank n k
    ///
    /// Own function because `zonkType` has no owner/param name, index, or
    /// span -- only a type variable. Sentinel placeholders live here once:
    /// owner `"<zonk>"`, param renders as the variable itself, index -1.
    /// Safe because `deduced[]` consumers key on `kind`, not owner/name.
    let recordZonkClosedRank (varId: IRId) (rank: int) =
        add (DeducedRank ("<zonk>", sprintf "?%d" varId, -1, rank)) noSpan

/// Append a non-fatal diagnostic to BOTH warning channels: the plain
/// string list (`typeCheck`'s Ok payload, kept exact for Repl.fs and the
/// provider tests) and the structured WarningLog (BLxxxx code + span,
/// survives the ERROR path). The collector is shared by reference across all
/// functional env updates, so call sites thread nothing through. Pass the
/// tightest span in scope; `noSpan` renders header-only.
let emitWarning (env: TypeEnv) (code: string) (span: Span) (msg: string) : unit =
    env.Warnings.Add(msg)
    WarningLog.add
        (Blade.Diagnostics.mkWarning code (Blade.Diagnostics.Codes.phaseOfCode code) span msg)

/// Push a context frame onto the environment
let pushContext (ctx: string) (env: TypeEnv) : TypeEnv =
    { env with Context = ctx :: env.Context }

/// Span of the statement currently being type-checked, per async flow (audit
/// sec 3.4; expression granularity is rewrite work). inferBlock stamps it per
/// StmtSpanned; locateError prefers it over the caller's declaration span, so
/// an error points at the failing STATEMENT (inferBlock stops at the first
/// error, so the last stamp is always the failing one). Reset at every
/// checkDecl/typeCheck/checkModule entry: AsyncLocal storage outlives one
/// compilation in a long-lived process (`blade ide check`), so a stale span would leak.
let private currentStmtSpanStorage = System.Threading.AsyncLocal<Span>()

/// Expression-level span, stamped by inferExpr on entry to every node.
/// Finer than the statement span; since inference short-circuits on first
/// error, the last stamp lies on the path to the failing expression. Cleared
/// on each new statement so a previous leaf can't win.
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
/// order: active expression span > active statement span > the caller's span.
let locateError (span: Span) (env: TypeEnv) (err: TypeError) : CompileError =
    let exprSpan = currentExprSpan ()
    let stmtSpan = currentStmtSpan ()
    let span =
        if exprSpan.StartLine > 0 then exprSpan
        elif stmtSpan.StartLine > 0 then stmtSpan
        else span
    { Error = err; Span = span; Context = env.Context; Code = None }

/// Stands in for a declaration name when a unit-expression error comes from a
/// TYPE ANNOTATION rather than a `Unit` declaration. The annotation consumers
/// in TypeCheck.fs pass this; formatTypeError words the message around it.
let unitAnnoContext = "<type annotation>"

/// Format a TypeError as a human-readable string
let formatTypeError (err: TypeError) : string =
    match err with
    | UnboundVariable name -> sprintf "Unbound variable: %s" name
    | TypeMismatch (exp, act) -> sprintf "Type mismatch: expected %s, got %s" (ppIRType exp) (ppIRType act)
    | ArityMismatch (exp, act) -> sprintf "Arity mismatch: expected %d args, got %d" exp act
    | KernelPackArity msg -> msg
    | ArgRankMismatch (pos, expRank, actRank, expTy, actTy) ->
        let describe rank ty =
            if rank = 0 then sprintf "a scalar (%s)" ty
            else sprintf "a rank-%d array (%s)" rank ty
        sprintf "argument %d: rank mismatch: the parameter expects %s but the argument is %s. A call site neither broadcasts nor reduces rank -- pass a value of the declared rank, or change the parameter's declared type."
                pos (describe expRank expTy) (describe actRank actTy)
    | ArgTypeMismatch (pos, func, expTy, actTy) ->
        sprintf "argument %d of %s: type mismatch: the parameter is declared %s but the argument is %s. A call site performs no conversion between these -- pass a value of the declared type, or change the parameter's declared type."
                pos func expTy actTy
    | InvalidArrayCapture name -> sprintf "Lambda cannot capture array '%s'" name
    | InvalidApplication funcTy -> sprintf "Cannot apply non-function type: %A" funcTy
    | PatternTypeMismatch (pat, ty) -> sprintf "Pattern '%s' incompatible with type %A" pat ty
    | ProviderNativeLoadFailure (provider, path, detail) ->
        sprintf "provider '%s' cannot load its native library, so the store '%s' cannot be read at compile time: %s. Every type this store binds is unresolvable until the library loads -- install the provider's runtime, or point its install-root variable at it (NETCDF_DIR for netcdf: the compiler and generated programs then use that install's own libraries)."
                provider path detail
    // Promoted variants (Stage 5): text reproduced verbatim.
    | IndexTagMismatchNamed (expected, actual) -> sprintf "Array index tag mismatch: slot expects '%s' but argument has type '%s'." expected actual
    | IndexTagMismatchAnon expected -> sprintf "Array index tag mismatch: slot expects named tag '%s' but argument is an anonymous index value." expected
    | CrossNominalIndexArith (left, right) -> sprintf "Cross-nominal index-type arithmetic: cannot combine values of distinct index domains '%s' and '%s'." left right
    | CrossAnonIndexArith (left, right) -> sprintf "Cross-nominal index-type arithmetic: cannot combine values of distinct anonymous index domains (#%d vs #%d)." left right
    | CompoundTupleForm rank -> sprintf "Compound arrays take FLAT positional subscripts like SymIdx: write B(c0, ..., c%d), not the tuple form B((c0, ..., c%d)) -- and wildcards (`_`) are not accepted on a compound axis. Partial/wildcard reads (pinning some coordinates, gathering the matches) are a SparseIdx feature: build the valid tuples as a SparseIdx<keys> and index S((c0, _, ...)) there (formalism 3.5)." (rank - 1) (rank - 1)
    | CompoundUnderSupplied (rank, got) -> sprintf "Compound index under-supplied: this array's compound axis has rank %d (mask is %d-dimensional), so it needs %d flat subscripts B(c0, ..., c%d); got %d. Partial reads are a SparseIdx feature (formalism 3.5)." rank rank rank (rank - 1) got
    | CompoundOverSupplied (rank, got) -> sprintf "Compound index over-supplied: this array's compound axis has rank %d (mask is %d-dimensional) and consumes %d flat subscripts (plus one per trailing dim); got %d total." rank rank rank got
    | SparseBareWildcard rank -> sprintf "A bare wildcard `_` cannot index a sparse axis: it pins no coordinate (the result would just be the array itself). Index with a full %d-tuple, pinning at least one coordinate." rank
    | SparseWildcardArity (rank, tupleLen) -> sprintf "Wildcard sparse indexing must use a FULL-arity tuple: this sparse axis has rank %d, so write all %d coordinates with `_` marking each free axis (got a %d-tuple). Short tuples (without wildcards) pin a leading prefix instead: S((c0, ..., cj))." rank rank tupleLen
    | SparseAllFree rank -> sprintf "Sparse index with all %d coordinates free (`_`) pins nothing -- the result is the array itself. Drop the index, or pin at least one coordinate." rank
    | SparseOverSupplied (rank, got) -> sprintf "Sparse index over-supplied: this array's sparse axis has rank %d (keys are %d-tuples), so it takes at most a %d-tuple like S((c0, ..., c%d)); got a %d-tuple." rank rank rank (rank - 1) got
    | SparseNeedsTuple rank -> sprintf "Sparse index must be a single tuple: write S((c0, ..., cj)) with inner parentheses, not the flat form S(c0, ..., cj). A SparseIdx<keys> axis of rank %d is indexed as one joint tuple, full or partial (formalism 3.5)." rank
    // OrbIdx storage-refusal text, front-end half. Not routed through
    // IR.orbitStorageUnsupported: a TypeError carries strings, not IR
    // structures, so this renderer can't recover depth as a number -- the
    // ONLY difference between the two spellings ("depth >= 2" here, "depth d"
    // there); everything else is identical and corpus-pinned. KEEP THEM IN
    // STEP or a half-updated pair tells the user two different stories.
    | OrbitStorageUnsupported (levels, where_) ->
        sprintf "%s: OrbIdx<%s, n> is a declarable index class of depth >= 2, and a DEDUCED one can now be \
allocated, written, printed, READ at an arbitrary tuple (the per-level canon fold, the accumulated \
character, the zero set), FULLY DECOMPACTED to its dense tensor, and round-tripped through a Zarr \
store (the spec_version 2 'orbit' head -- providers/ZarrTriangularSpec.md). What is still missing is every \
path that would put a wreath pool anywhere but under its own traversal nest: an OrbIdx ANNOTATION \
(a store is now a producer, but the annotation also admits classes nothing produces), reduce/prodsum \
over the pool, transpose, PARTIAL (per-level) decompaction, a WINDOWED or distributed store read, and \
provider I/O outside Zarr (CSV and NetCDF have no pool axis to carry the class on). So the \
compiler refuses here rather than compute an address it cannot compute. \
The depth-1 spellings work through the existing compact machinery instead: OrbIdx<[(r,+)], n> is \
exactly SymIdx<r, n>, OrbIdx<[(r,-)], n> is exactly AntisymIdx<r, n>, and OrbIdx<[], n> is exactly \
Idx<n>." where_ levels
    | OrbitSubscriptArity (levels, axes, got) ->
        sprintf "OrbIdx<%s, n> acts on %d raw axes, so a subscript of it takes exactly %d flat \
coordinates (W(i0, ..., i%d)) -- the same flat spelling a rank-k SymIdx group takes; got %d. \
%sA wreath record is ONE index slot spanning all %d axes, not %d slots." levels axes axes (axes - 1) got
                (if got < axes then
                    "A PARTIAL subscript has no answer for a wreath class: its rows shrink per LEVEL, \
so no residual index type describes a fibre of the pool -- decompact(W, 0) first and slice the \
dense result. "
                 else "")
                axes axes
    | OrbitDecompactPartial (levels, dim) ->
        sprintf "decompact(W, %d): only FULL decompaction of an OrbIdx<%s, n> class is implemented -- \
write decompact(W, 0), which produces the complete dense tensor (one plain Idx<n> axis per raw axis, \
each cell the class's own read: the per-level canon fold, the accumulated character, and 0 on the \
zero set). For a wreath class the second argument is the number of LEVELS TO KEEP \
(docs/plan-orbidx-decompaction.md section 4.3), not a dimension to free, and the partial/peel lattice of \
that plan's section 3 is not built: peeling level d unties its r_d sub-blocks into a juxtaposition of \
depth-(d-1) classes, which needs a typed residual nothing produces yet." dim levels
    | OrbitFoldUnsupported (levels, op) ->
        sprintf "%s() over OrbIdx<%s, n> compact storage is not supported: folding the canonical POOL \
cells and folding the logical (mirrored) cells differ. The pool holds one cell per ORBIT, so a fold \
over it answers for an array of that many elements instead of the n^rank tensor it stands for -- and \
each cell would need its orbit multiplicity (a Burnside count), with a '-' level's character \
cancelling terms outright. decompact(W, 0) first for the logical fold: full decompaction of a wreath \
class IS implemented, and the dense result folds like any other array." op levels
    | RaggedIdxNeedsPrior func -> sprintf "function '%s': RaggedIdx requires at least one prior index in the array's index list -- the ragged extent is a per-row function of the OUTER iteration position (formalism 4.4). Add an outer index, e.g. Array<T like Idx<n>, RaggedIdx<lens>>." func
    | TagWildcardNotParam where_ -> sprintf "%s: the tag wildcard `_` is legal in PARAMETER position only. A parameter may decline to constrain its argument's index tag or unit, but this position has to PRODUCE one -- a wildcard here would erase the tag rather than relax it. Write the concrete index type (e.g. Nat<LatIdx>) or the bare base type." where_
    | IndexRankMismatch (where_, left, leftRank, right, rightRank) ->
        let components n = if n = 1 then "1 index component" else sprintf "%d index components" n
        sprintf "%s: %s spans %s but %s spans %s. A rank-k compact group (SymIdx<k, n> / AntisymIdx<k, n>) is ONE index slot covering k dimensions -- indexed A(i0, ..., i(k-1)), not A(j) -- so it is a different type from a flat axis holding the same cells (SymIdx<2, 3> packs 6 cells, exactly Idx<6>). An equal cell count does NOT make the two interchangeable. Convert with decompact (compact group -> dense axes); an annotation cannot reinterpret one form as the other." where_ left (components leftRank) right (components rightRank)
    | DecompactDimRange (dim, totalDims) -> sprintf "decompact: dimension %d is out of range for a rank-%d array (valid dims 0..%d)" dim totalDims (totalDims - 1)
    | DecompactPlainAxis dim -> sprintf "decompact: dimension %d is a plain (rank-1, non-symmetric) axis; there is nothing to decompact. decompact pulls a component out of a compact group (SymIdx/AntisymIdx/HermitianIdx)." dim
    | DecompactLastSlotOnly (slots, slot) -> sprintf "decompact: only a compact group in the LAST index slot, optionally preceded by plain free Idx dimensions, is supported by codegen (the chained to-the-right peel shape). The array here has %d index slots with the compact group at slot %d." slots slot
    | TransposeAxisRange (axis, totalDims) -> sprintf "transpose: axis %d is out of range for a rank-%d array (valid axes 0..%d)" axis totalDims (totalDims - 1)
    | TransposeAxesEqual (axisA, axisB) -> sprintf "transpose: the two axes must differ (got [%d, %d]); swapping an axis with itself is the identity" axisA axisB
    | TransposeWithinGroup rank -> sprintf "transpose: swapping two dimensions within a single rectangular index group (rank %d) is not yet supported." rank
    | StackNeedsArrays (pos, got) -> sprintf "stack: argument %d has type %s, not an array. stack(A1, ..., An) adds a fresh LEADING axis over n arrays of the SAME shape; to build a rank-1 array from scalars write the array literal [a, b, c] instead." pos got
    | StackShapeMismatch (pos, detail) -> sprintf "stack: argument %d does not match argument 1 (%s). stack(A1, ..., An) requires every operand to have the same rank, extents, and element type -- the fresh leading axis selects among them." pos detail
    | JoinNeedsArrays (pos, got) -> sprintf "join: argument %d has type %s, not an array. join(A, B, d) concatenates arrays along dimension d." pos got
    | JoinDimRange (dim, totalDims) -> sprintf "join: dimension %d is out of range for a rank-%d array (valid dims 0..%d)" dim totalDims (totalDims - 1)
    | JoinShapeMismatch (pos, detail) -> sprintf "join: argument %d does not match argument 1 (%s). join(A, B, d) requires equal rank, equal element type, and equal extents on EVERY axis except the joined dimension d." pos detail
    | StackJoinCompactSlot (op, slot) -> sprintf "%s: index slot %d is a compact, ragged, or compound group. %s materializes a dense rectangular result, so its operands must be dense (plain Idx) on every axis -- decompact the axis first." op slot op
    | UnitMismatch (context, left, right) -> sprintf "Unit mismatch in %s: %s vs %s" context left right
    | QuantityArgMismatch (pos, quantity, got) ->
        sprintf "argument %d: the parameter's declared type carries the quantity '%s', and a quantity-typed slot only accepts values ASSERTED to be that quantity -- this argument is %s. Ascribe it at the call site (e.g. `x : %s`); matching dimensions alone do not imply the quantity." pos quantity got quantity
    | ExtentArgMismatch (pos, dim, expected, actual) ->
        sprintf "argument %d: extent mismatch on index slot %d -- the parameter declares Idx<%d> but the argument has Idx<%d>. A LITERAL parameter extent is baked into the emitted loop bounds and result allocations (a symbolic extent like Idx<n> reads the argument's extent at runtime instead), so this reads past the argument's allocation rather than merely disagreeing. Make the extents match, or declare the parameter over a symbolic extent." pos dim expected actual
    | ZipExtentMismatch (pos, expected, actual) ->
        sprintf "elementwise co-iteration: operand %d has extent %d on the shared axis, but operand 1 has extent %d. A zip walks ONE index space, taken from the first operand, so the longer walk reads past the shorter operand's allocation -- silent out-of-bounds, not a broadcast (Blade does not broadcast mismatched extents). Bring the operands to a common extent, or index/slice the longer one first." pos actual expected
    | HaloExtentMismatch (declared, dim, targetName, actual) ->
        sprintf "halo extent mismatch: the halo declares an inner extent of %d, but '%s' (read through the window at index slot %d) has extent %d. The window walk is bounded by the DECLARED extent, so an oversized halo reads past '%s''s allocation and an undersized one silently emits fewer windows. Make the halo's inner index match the array it windows over." declared targetName dim actual targetName
    | QuantityTerminal (quantity, declName) ->
        sprintf "unit '%s': the quantity '%s' cannot be used inside a unit expression. Quantities are TERMINAL -- the nominal layer is exactly one level deep -- so a quantity name can neither be composed (`Unit x = %s * m`) nor re-derived from (`Unit q: %s`). Compose from the structural units the quantity was declared over instead." declName quantity quantity quantity
    | UnknownUnitName (name, declName, candidates) ->
        let where =
            if declName = unitAnnoContext then "unit annotation"
            else sprintf "unit '%s'" declName
        sprintf "%s: '%s' is not a declared unit or a known scale constant. A unit expression composes names already in scope -- only a numeric LITERAL may appear without being declared -- so declare '%s' first (`Unit %s`), import the module that exports it, or fix the spelling.%s" where name name name
            (if List.isEmpty candidates then "" else sprintf " Did you mean: %s?" (String.concat ", " candidates))
    | DefaultParamOrder (func, requiredParam, defaultedParam) ->
        sprintf "%s: parameter '%s' has no default but follows the defaulted parameter '%s'. Defaults are TRAILING: once a parameter has a default, every later parameter needs one too (otherwise an omitted-argument call is ambiguous). Reorder the parameters or give '%s' a default." func requiredParam defaultedParam requiredParam
    | DefaultParamScope (func, param, referenced) ->
        sprintf "%s: the default for parameter '%s' references '%s', which is itself a defaulted parameter. A default may reference the REQUIRED parameters only -- defaults evaluate left-to-right at call entry with just the required arguments bound, so another default's value is not available." func param referenced
    | FactoryDupQuantityDecl (func, quantity, param1, param2) ->
        sprintf "%s: defaulted parameters '%s' and '%s' both carry the quantity '%s'. By-nominal argument routing (`f(x, 3 : %s)`) needs each quantity to name exactly ONE defaulted slot -- give the second slot a distinct quantity, or make it a plain (non-quantity) parameter." func param1 param2 quantity quantity
    | FactoryDupFill (callee, quantity, slot) ->
        sprintf "call to '%s': the quantity slot '%s' (quantity '%s') is supplied twice -- a second argument tagged '%s' (or a positional argument already claiming that slot) conflicts with an earlier one. Each slot takes at most one argument." callee slot quantity quantity
    | FactoryUnknownTag (callee, quantity, candidates) ->
        sprintf "call to '%s': an argument is tagged with the quantity '%s', but '%s' has no defaulted slot of that quantity. Its quantity slots are: %s." callee quantity callee (if List.isEmpty candidates then "none" else String.concat ", " candidates)
    | FactoryAmbiguousMix (callee, pos) ->
        sprintf "call to '%s': argument %d has no quantity tag but appears AFTER a quantity-tagged argument, so its slot would be a guess. Positional (untagged) arguments must come first, in declared order; tag the stragglers (`v : quantity`) or reorder the call." callee pos
    | IntrinsicBindArrayFailed op -> sprintf "%s(): failed to bind array type after unification" op
    | IntrinsicNeedsArray op -> sprintf "%s() requires an array as argument" op
    | IntrinsicNotComplex name -> sprintf "%s is not defined for complex operands." name
    | IntrinsicNeedsNumeric name -> sprintf "%s expects a numeric operand." name
    | AbsNeedsNumericScalar got -> sprintf "abs expects a numeric scalar operand, got %s" got
    | IntrinsicComplexScalarOnly name -> sprintf "%s applies to complex scalars; map it over the array elementwise (e.g. method_for(A) <@> lambda(z) -> %s(z) |> compute)." name name
    | IntrinsicNeedsComplex (name, got) -> sprintf "%s expects a complex operand, got %s" name got
    | ReduceEmptyArray extent -> sprintf "reduce() rejects statically empty arrays (extent = %d). Empty input has no defined reduction without an identity; supply one with the 3-arg form `reduce(arr, op, init)`." extent
    | ProdsumExtentMismatch (a, b) -> sprintf "prodsum() operands must share one extent: got %d and %d" a b
    | GramNeedsRank2 (leftRank, rightRank) -> sprintf "gram(A, B): both operands must be rank-2 (matrix) arrays; got rank-%d and rank-%d. gram contracts the trailing axis: A (m x n), B (p x n) -> m x p." leftRank rightRank
    | GramCompactOperand side -> sprintf "gram(A, B): operands must be rank-2 with two PLAIN index axes; %s compact rank-2 group storage (SymIdx / AntisymIdx / HermitianIdx, e.g. a gram result), whose single packed axis cannot supply the outer and contracted dimensions separately. Expand to a dense matrix first: decompact(X, 0)." side
    | ArrayLitLength (got, expected, axisTag) ->
        let axis = match axisTag with Some t -> sprintf " for axis '%s'" t | None -> ""
        sprintf "Array literal%s has %d elements, but the annotation's extent is %d" axis got expected
    | CompactLitShape (idx, shape, where_, detail) ->
        sprintf "Array literal over the compact group %s: %s %s. A compact group is ONE axis storing \
one cell per canonical tuple, laid out as a left-justified simplex, so its literal is written in that \
same shape -- %s. (A flat list or a rectangular nest names cells the storage does not have.)"
            idx where_ detail shape
    | HermitianLitDiagComplex where_ ->
        sprintf "Hermitian literal: %s is a DIAGONAL cell with a non-zero imaginary part. \
A(i, i) = conj(A(i, i)) forces the diagonal real, and the stored diagonal cell is read unconjugated, \
so this value is not one the class can hold. Write a real diagonal (the off-diagonal cells carry the \
complex half)." where_
    | RaggedLensMismatch (lensName, declared, actual) ->
        sprintf "Ragged literal: the annotation names `RaggedIdx<%s>`, and %s holds %s, but the literal's rows are %s. A ragged array takes its row lengths from its LITERAL -- the baked lens and offsets are computed from this nesting, and nothing reads `%s` back -- so the annotation would describe a shape the array does not have. Fix whichever one is wrong; if the lengths are meant to come from the data, drop the annotation and let the literal infer the ragged type."
            lensName lensName declared actual lensName
    | RaggedLensNotStatic lensName ->
        sprintf "Ragged literal: the annotation names `RaggedIdx<%s>`, but `%s` is not a compile-time value. A ragged array's row lengths are baked from the LITERAL's own nesting, so a lens known only at run time can neither be honoured nor checked -- it would be accepted and then ignored. Make `%s` a compile-time array of integer literals (so the two can be compared), or drop the annotation and let the literal infer the ragged type. Allocating to lengths only the running program knows is separate, planned work."
            lensName lensName lensName
    | ObjectForKernel got -> sprintf "object_for kernel must be a lambda, reynolds, or zero, but got %A" got
    | ChainOpNeedsMethodFor leftDesc -> sprintf "<@> requires method_for or object_for on the left side, but got %s" leftDesc
    | ChainOpBadKernel rightDesc -> sprintf "<@> kernel must be a lambda, operator section, named function, reynolds(...), or zero, but got %s" rightDesc
    | ChainOpUndecidable (leftDesc, rightDesc) -> sprintf "cannot infer the roles of the <@> operands: the left side is %s and the right side is %s, so the arrays/kernel roles are ambiguous. A former is implicit only when one side is decisive: a kernel (lambda, operator section, named function, reynolds(...), zero) or a former. Write it explicitly: method_for(arrays) <@> kernel, or object_for(kernel) <@> (arrays)." leftDesc rightDesc
    | CommContradictsBody (p1, p2) -> sprintf "`where comm(%s, %s)` contradicts the kernel body, which is provably ANTIcommutative under that swap (f(%s, %s) = -f(%s, %s)): triangular storage would silently corrupt half the output. Remove the comm clause, or wrap the kernel in reynolds(...) if a signed iteration license over the permutation sum is what you intend." p1 p2 p2 p1 p1 p2
    | AntisymmContradictsBody (p1, p2) -> sprintf "`where anticomm(%s, %s)` contradicts the kernel body, which is provably COMMUTATIVE under that swap (f(%s, %s) = f(%s, %s)): strict-triangular anticommutative storage would drop the diagonal and negate half the output. Remove the anticomm clause (use `where comm(%s, %s)` for the symmetric triangle), or wrap the kernel in reynolds(..., Antisymmetric) if a signed antisymmetrization is what you intend." p1 p2 p2 p1 p1 p2 p1 p2
    | AntisymMapNotOdd (param, proved) -> sprintf "mapping this kernel over an ANTISYMMETRIC (AntisymIdx) array would keep the input's strict-triangular storage, and that is only correct for a SIGN-ODD kernel (f(-x) = -f(x)); the deduction says this one is %s in '%s'. An even or unknown-parity map of an antisymmetric array is SYMMETRIC -- it has a diagonal, and the strict iteration the input forces cannot produce one -- so the compact result would negate every mirrored read. Map over a dense copy instead (`decompact(A, 0)` materializes the full tensor, and the kernel over THAT is symmetric with the right diagonal), or use a sign-odd kernel." proved param
    | WreathTieKernelNotOdd (param, proved, levels) -> sprintf "the declared clause ties every argument over a compact class with a '-' inner level (%s), and that tie is only sound for a kernel SIGN-ODD in each argument separately (h(-p, q) = -h(p, q)): a '-' level claims that mirroring ONE argument's sub-block negates the value, so an even or unknown-parity kernel would store a class whose mirrored reads and decompaction answer with signs the values do not satisfy. The deduction says this kernel is %s in '%s'. Use a per-argument sign-odd kernel (e.g. p * q; note p + q is NOT odd in each argument), or map over dense copies instead: decompact(_, 0) materializes the full tensor, and the kernel over THAT carries no wreath claim." levels proved param
    | HermitianMapNotReal param -> sprintf "mapping this kernel over a HERMITIAN (HermitianIdx) array would keep the input's Hermitian storage, whose mirrored reads recover H(j,i) as conj(H(i,j)); that is only correct when the kernel commutes with conjugation (f(conj z) = conj(f z)), which is not deducible for '%s'. A kernel built from the parameter, real constants, + - * /, and neg/conj/real qualifies; a complex constant, imag(z), arg(z), `^` and the math intrinsics (exp/log/sqrt/...) do not. Map over a dense copy instead: `decompact(A, 0)` materializes the full conjugate-mirrored matrix, and the kernel over THAT carries no storage claim." param
    | FoldOmpNeedsLicense kernelDesc -> sprintf "parallel reduction needs comm(...) or a builtin op: %s carries `omp` but nothing licenses the reorder. A parallel fold splits the axis into per-thread chunks and combines the partials, so the kernel must be COMMUTATIVE and ASSOCIATIVE -- write `where comm(a, b), omp` to declare it (the same word `<@>` uses for symmetric storage, cross-checked against the body's parity), or use a builtin fold body (a + b, a * b, a && b, a || b), which carries both properties outright. Drop `omp` to keep the serial fold." kernelDesc
    | PlaceholderNeedsAllBound (got, total) -> sprintf "the `_` placeholder needs every other parameter bound: this call supplies %d of %d args. Combine with prefix partial application in two steps, or use a lambda." got total
    | GroupKeysRank1 -> "group_keys: all key arrays must be rank-1 and share the same outer index (same length). Compound grouping requires each i-th element of every key array to refer to the same record."
    | GroupKeysEscapes (what, pos) -> sprintf "%s cannot be used %s: a `group_keys` result is NAME-KEYED, not a value. It compiles to three locals named after the binding (`<name>__ngroups`, `<name>__offsets`, `<name>__perm`) and the binding itself carries only an opaque sentinel, so `group_by` can find the grouping only under the exact name the keys were bound to. Bind the keys once (`let gk = group_keys(...)`) and pass that same `gk` directly to each `group_by` -- a group_keys result cannot be aliased to a second name, put in a tuple or struct, passed as a function argument, or returned. (Grouping two arrays the same way is what one shared `gk` is FOR: `group_by(a, gk)` and `group_by(b, gk)` co-iterate; two separate `group_keys` calls do not.)" what pos
    | GroupingNeedsName (intrinsic, got) -> sprintf "%s(gk) requires the BARE NAME of a `group_keys(...)` binding; got %s. A grouping is not a value -- its state lives in locals named after the binding (`<gk>__ngroups`, `<gk>__offsets`, `<gk>__perm`), so it cannot be aliased, passed, returned, or built inline. Bind it once (`let gk = group_keys(...)`) and write `%s(gk)`." intrinsic got intrinsic
    | GroupBucketNotGrouping got -> sprintf "group_bucket expects a `group_keys(...)` binding; '%s' is not one. Bind the keys first: `let gk = group_keys(k)`, then `group_bucket(gk)`." got
    | FallbackNeedsArrays (leftDesc, rightDesc) -> sprintf "<|:> (allocated-fallback) reads the LEFT array where its storage holds a cell and the right array elsewhere, so both operands must be arrays; got %s and %s. For value-level choice (first nonzero wins) over scalars or computations, use <|>." leftDesc rightDesc
    | FallbackSymmetricLeft -> "<|:> over a symmetric/antisymmetric/Hermitian left operand is not yet supported: symmetric A requires symmetric allocation (formalism 2.6), which the compiler cannot yet verify. decompact(A, d) to dense first."
    | FallbackRightNotDense what -> sprintf "<|:> right operand must be a plain dense array (it supplies the value for every cell the left side lacks); got %s." what
    | FallbackRankMismatch (leftRank, rightRank) -> sprintf "<|:> operands must cover the same index space: the left side spans %d dimension(s) but the right side has rank %d." leftRank rightRank
    | CumulantOrderPositive order -> sprintf "cumulant: order must be >= 1, got %d" order
    | CumulantNeedsDist got -> sprintf "cumulant expects cumulant(d, k) where d is a Dist value (a dist(...) binding or Dist-typed parameter); got %s" got
    | DistOpUndefined (left, right) -> sprintf "this operator is not defined on Dist values (left: %s, right: %s): dists support scalar * (multilinearity), + and - of independent dists, and component projection via cumulant(d, k)" left right
    | EnumIdxMixedKinds name -> sprintf "EnumIdx '%s' has mixed value kinds: integer and string literals in the same EnumIdx<[...]> aren't allowed. The runtime backing must be one or the other (int64_t or std::string)." name
    | EnumIdxUnknownLabel (enumName, label, available) ->
        sprintf "'%s' is not a value of EnumIdx '%s'. Available: %s." label enumName (available |> String.concat ", ")
    | ImplMissingMethods (iface, typeName, methods) -> sprintf "impl %s for %s is missing required methods: %s" iface typeName methods
    | StructFieldDuplicate (structName, field) -> sprintf "struct %s: field '%s' assigned more than once" structName field
    | StructNoField (structName, field) -> sprintf "struct %s has no field '%s'" structName field
    | StructFieldUnknown (structName, field, available, steering) ->
        let avail =
            match available with
            | [] -> "; it declares no fields"
            | fs -> sprintf "; available fields: %s" (fs |> String.concat ", ")
        sprintf "struct %s has no field '%s'%s%s" structName field steering avail
    | StructSpreadBase structName -> sprintf "struct %s: a spread base must be a variable or field path -- bind it with let first" structName
    | StructSpreadNotStruct (structName, got) -> sprintf "struct %s: spread base must be a %s value, got %s" structName structName got
    | StructSpreadRedundant structName -> sprintf "struct %s: every field is provided explicitly -- the '..' spread is redundant" structName
    | StructMissingField (structName, field) -> sprintf "struct %s: missing field '%s' in constructor" structName field
    | StructFieldType (structName, field, expected, actual) -> sprintf "struct %s, field '%s': expected %s, got %s" structName field expected actual
    | UnknownStructType name -> sprintf "unknown struct type '%s' in constructor" name
    | StructWhereNotBool (structName, got) -> sprintf "struct %s: where-constraint must be a boolean expression, got %s" structName got
    | StructWhereError (structName, inner) -> sprintf "struct %s where-constraint: %s" structName inner
    | WherePredicateUnannotated (owner, func) -> sprintf "static function '%s' is called from a where-clause of '%s': annotate all its parameter types and its return type" func owner
    | UnknownWhereConstraint (func, name, vocab) -> sprintf "function '%s': unknown where-clause constraint '%s' (registered constraints: %s)" func name vocab
    | DistOrderCompileTime func -> sprintf "function '%s': Dist order must be a compile-time integer >= 1 (a literal, `let static`, or static-function call): Dist<order, Elem like I1, ..., Ik>" func
    | ImmutableStaticAssign name -> sprintf "Cannot assign to '%s': static bindings are immutable" name
    | MutAssignRefused (target, reason) -> sprintf "Cannot assign to '%s': %s" target reason
    | MutArgNotPassable (func, argIndex, got) ->
        sprintf "function '%s': argument %d is passed to a `mut` parameter, which writes back into the caller's array, but %s. Only a `let mut` binding (or another `mut` parameter being forwarded) may be passed there -- declare it `let mut`, or drop `mut` from the parameter if the callee does not write to it." func argIndex got
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
    | ProviderWriteModuleScope alias -> sprintf "%s.write is a MODULE-LEVEL declaration form: it is only allowed as the whole right-hand side of a top-level `let` (let _ = %s.write(\"path\", A)). A write nested inside a block, a function or lambda body, a loop, or a branch is not lowered -- hoist it to module scope, or return the array from the block and write it there." alias alias
    | IrrepsIdxArgMismatch (pos, expected, actual) -> sprintf "argument %d: IrrepsIdx mismatch: the parameter expects %s but the argument carries %s. IrrepsIdx identity is the spec (plus nominative alias name) -- equal total_dim does not make two irreps spaces interchangeable." pos expected actual
    | BlockSpecArgMismatch (pos, expected, actual) -> sprintf "argument %d: block-spec index mismatch: the parameter expects %s but the argument carries %s. A block-structured index's identity is its GROUP FAMILY plus its spec (plus nominative alias name) -- equal total_dim does not make two block spaces interchangeable, and an O(3) irreps space is never a point-group one." pos expected actual
    | IrrepsIdxSpec detail -> sprintf "IrrepsIdx: %s. The spec must be a static array of (l, parity, mult) int triples -- a `let static` binding or an inline literal like IrrepsIdx<[(0, 0, 2), (1, 1, 2)]>." detail
    | IrrepsIdxSpecFn (func, detail) -> sprintf "function '%s': IrrepsIdx: %s. The spec must be a static array of (l, parity, mult) int triples -- a `let static` binding or an inline literal like IrrepsIdx<[(0, 0, 2), (1, 1, 2)]>." func detail
    | PgIrrepsIdxSpec detail -> sprintf "PgIrrepsIdx: %s. The form is PgIrrepsIdx<GROUP, SPEC> with GROUP a registered point group and SPEC a static array of (LABEL_NAME, mult) tuples -- a `let static` binding or an inline literal like PgIrrepsIdx<C4, [(\"A\", 1), (\"E\", 2)]>." detail
    | PgIrrepsIdxSpecFn (func, detail) -> sprintf "function '%s': PgIrrepsIdx: %s. The form is PgIrrepsIdx<GROUP, SPEC> with GROUP a registered point group and SPEC a static array of (LABEL_NAME, mult) tuples -- a `let static` binding or an inline literal like PgIrrepsIdx<C4, [(\"A\", 1), (\"E\", 2)]>." func detail
    | ComplexArity got -> sprintf "complex expects exactly two float components -- complex(re, im) -- got %d argument(s)" got
    | CumulantOrderExceeds (order, carried) -> sprintf "cumulant: order %d exceeds the dist's carried order %d -- insufficient stochastic order. Construct with a higher order (dist(A, %d)) or project a carried component." order carried order
    | DistOrderDisagree (op, leftOrder, rightOrder) -> sprintf "dist %s: orders disagree (%d vs %d) -- carry the same stochastic order on both sides" op leftOrder rightOrder
    | DistNotIndependent (op, source1, source2, steering) -> sprintf "dist %s: cumulants combine only for independent distributions -- sources '%s' and '%s' are not declared independent; %s" op source1 source2 steering
    | PplConstraintNeedsImport (func, bare) -> sprintf "function '%s': constraint '%s' belongs to the ppl module -- add `import ppl as <alias>` and write `where <alias>.%s(...)`" func bare bare
    | StructBoundScope (structName, field, bad) -> sprintf "struct %s, field '%s': bound references '%s' -- bounds may reference only earlier fields and statics" structName field bad
    | StaticStructField (structName, field, why) -> sprintf "static struct %s, field '%s': %s -- every field of a `static struct` must have a statically evaluable type (Int, Float, Bool, String, Char, a tuple of those, or another static struct)" structName field why
    | BoundsInverted (where_, lo, hi) -> sprintf "%s: bounds cross -- min=%s is greater than max=%s (bounds are inclusive on both ends, so this type has no values)" where_ lo hi
    | BoundsOnAggregate (where_, noun, subject) -> sprintf "%s: bounds apply to primitive types, not aggregates -- the bound is applied to %s. A bound is a runtime comparison against %s itself (formalism 2.4: bounded PRIMITIVES carry runtime-checked bounds), and an aggregate has no such comparison. Write the bound on the ELEMENT type instead -- `Array<Float64<min=.., max=..> like I, J>` is checked cell by cell." where_ noun subject
    // Wording must match Unify.fs's rank-bound block: `got` carries the
    // whole "a scalar" / "a rank-N array" tail, so the sentence stays exact.
    | RankBoundViolation (needed, got) -> sprintf "this value flows into a position that requires a rank-%d (or higher) array, but it resolved to %s" needed got
    | ProviderImportByModule (suggestion, providers) -> sprintf "provider modules are imported by module name -- write `import %s as <alias>` (the Providers.* spelling was removed; registered providers: %s)" suggestion providers
    | ProviderNoSelectiveImport pname -> sprintf "provider module '%s' does not support selective import -- use `import %s as <alias>` and call <alias>.load/read/write" pname pname
    | IndexTypeArithForbidden name -> sprintf "Arithmetic on index type '%s' is not permitted. Index types are nominal labels -- for value-level arithmetic on positions, use virtual array iteration (which produces plain ints); for new index types derived from arithmetic, type-level construction is a separate workstream not yet implemented." name
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
            // Environment condition, not a type judgment: the provider's
            // native library is unloadable, so the store's names cannot
            // resolve -- same band as BL2004's "module not found".
            | ProviderNativeLoadFailure _ -> "BL2007"
            | TypeMismatch _ | ArgRankMismatch _ | ArgTypeMismatch _ -> "BL3001"
            | ArityMismatch _ | KernelPackArity _ -> "BL3002"
            | InvalidApplication _ -> "BL3003"
            | PatternTypeMismatch _ -> "BL3004"
            | InvalidArrayCapture _ -> "BL3005"
            // Promoted variants (Stage 5)
            | UnitMismatch _ -> "BL3006"
            | QuantityArgMismatch _ -> "BL3010"
            | ExtentArgMismatch _ | HaloExtentMismatch _ | ZipExtentMismatch _ -> "BL3016"
            | QuantityTerminal _ -> "BL3011"
            | DefaultParamOrder _ | DefaultParamScope _ -> "BL3012"
            | FactoryDupQuantityDecl _ -> "BL3013"
            | FactoryDupFill _ | FactoryUnknownTag _ | FactoryAmbiguousMix _ -> "BL3014"
            | UnknownUnitName _ -> "BL3015"
            | GroupKeysEscapes _ -> "BL3017"
            | IntrinsicBindArrayFailed _ | IntrinsicNeedsArray _
            | IntrinsicNotComplex _ | IntrinsicNeedsNumeric _ | AbsNeedsNumericScalar _
            | IntrinsicComplexScalarOnly _ | IntrinsicNeedsComplex _ | ComplexArity _
            | ReduceEmptyArray _ | ProdsumExtentMismatch _ | GramNeedsRank2 _
            | GramCompactOperand _
            | ArrayLitLength _ | CompactLitShape _ | HermitianLitDiagComplex _
            | ObjectForKernel _ | ChainOpNeedsMethodFor _ | ChainOpBadKernel _
            | ChainOpUndecidable _
            | PlaceholderNeedsAllBound _ | GroupKeysRank1
            | GroupingNeedsName _ | GroupBucketNotGrouping _
            | CumulantOrderPositive _
            | CumulantOrderExceeds _ | CumulantNeedsDist _ | DistOrderDisagree _
            | DistNotIndependent _ | DistOpUndefined _ | EnumIdxMixedKinds _
            | EnumIdxUnknownLabel _
            | ImplMissingMethods _
            | FallbackNeedsArrays _ | FallbackSymmetricLeft
            | FallbackRightNotDense _ | FallbackRankMismatch _ -> "BL3007"
            // Field ACCESS gets its own code: BL3008 reads "struct
            // construction error", and this one is raised nowhere near a
            // constructor.
            | StructFieldUnknown _ -> "BL3018"
            | StructFieldDuplicate _ | StructNoField _ | StructMissingField _
            | StructFieldType _ | UnknownStructType _ | StructBoundScope _
            | StaticStructField _
            | StructSpreadBase _ | StructSpreadNotStruct _ | StructSpreadRedundant _ -> "BL3008"
            | RankBoundViolation _ -> "BL3009"
            // A `comm`/`antisymm` clause the deduction PROVED wrong is not
            // BL3007's generic "invalid builtin argument" bucket: it's an
            // annotation contradicting its own body -- drop the clause, or
            // wrap in `reynolds` for the signed iteration license.
            | CommContradictsBody _ | AntisymmContradictsBody _ -> "BL4013"
            // Same family, other direction: nothing is DECLARED here -- the
            // output would inherit the input's compact class, and the
            // deduction can't certify the kernel commutes with its mirror
            // involution. WreathTieKernelNotOdd joins deliberately: it DOES
            // declare a clause (the SWAP h(p,q) = h(q,p)), but the failing
            // certificate -- per-argument oddness against the inner class's
            // mirror -- is exactly this family's undeclared gap.
            | AntisymMapNotOdd _ | HermitianMapNotReal _
            | WreathTieKernelNotOdd _ -> "BL4015"
            // Own code, not BL4013's: nothing is CONTRADICTED here -- `omp`
            // asks for a reorder the checker has no licence for. Fix is to
            // add `comm(...)` (or use a builtin body), not remove a disproved annotation.
            | FoldOmpNeedsLicense _ -> "BL4016"
            // Own code, not BL4003's index-type bucket: the index type is
            // well formed and the USE is servable -- it is the LENS, the one
            // part of the annotation construction does not derive for itself,
            // that the literal contradicts. The fix is to reconcile two
            // spellings of one shape, not to stop using RaggedIdx.
            | RaggedLensMismatch _ | RaggedLensNotStatic _ -> "BL4018"
            | StructWhereNotBool _ | StructWhereError _ | WherePredicateUnannotated _
            | PplConstraintNeedsImport _
            | UnknownWhereConstraint _ -> "BL4001"
            | DistOrderCompileTime _ -> "BL4002"
            | IndexTagMismatchNamed _ | IndexTagMismatchAnon _ | CrossNominalIndexArith _
            | CrossAnonIndexArith _ | IndexTypeArithForbidden _ | IrrepsIdxArgMismatch _
            | BlockSpecArgMismatch _
            | CompoundTupleForm _ | CompoundUnderSupplied _ | CompoundOverSupplied _
            | SparseBareWildcard _ | SparseWildcardArity _ | SparseAllFree _
            | SparseOverSupplied _ | SparseNeedsTuple _ | RaggedIdxNeedsPrior _
            // BL4003: the index TYPE is well formed but this USE can't be
            // served. Not BL7001 ("not yet supported by THIS BACKEND") --
            // both backends refuse, in the front end.
            | OrbitStorageUnsupported _ | OrbitSubscriptArity _
            | OrbitDecompactPartial _ | OrbitFoldUnsupported _
            | IrrepsIdxSpec _ | IrrepsIdxSpecFn _
            | PgIrrepsIdxSpec _ | PgIrrepsIdxSpecFn _ | TagWildcardNotParam _
            | BoundsInverted _ | BoundsOnAggregate _ -> "BL4003"
            | IndexRankMismatch _
            | DecompactDimRange _ | DecompactPlainAxis _ | DecompactLastSlotOnly _
            | TransposeAxisRange _ | TransposeAxesEqual _ | TransposeWithinGroup _
            | StackNeedsArrays _ | StackShapeMismatch _ | JoinNeedsArrays _
            | JoinDimRange _ | JoinShapeMismatch _ | StackJoinCompactSlot _ -> "BL4004"
            | ImmutableStaticAssign _ | MutParamNotArray _ | MutAssignRefused _
            | MutArgNotPassable _ -> "BL4005"
            | MutualBindJointly _ | MutualDirectElementsOnly _ | MutualMixedGroups
            | MutualDuplicateMember _ | MutualIncompleteAnnotation _ | MutualJointAnnotationOnly _
            | MutualParamMemberType _ | MutualBindTuple _ | MutualReturnTupleElements _
            | StructFieldMutualType _ | MutualMemberDupGroup _ | MutualMemberNotStruct _
            | MutualMemberBadAlias _ | MutualUnknownField _ | MutualScalarBare _
            | MutualStructNeedsField _ | MutualUnknownIdent _
            | MutualUnsupportedExpr | MutualConstraintNotBool _ | MutualConstraintError _ -> "BL4006"
            | ProviderStreamNeedsVar _ | ProviderReadWindowBounds _ | ProviderReadWindowLiteralExtent _
            | ProviderReadWindowPacked _ | ProviderReadWindowNeedsVar _ | ProviderReadWindowArgs _
            | ProviderWriteNeedsArray _ | ProviderWriteNamedBinding _ | ProviderWriteArgs _
            | ProviderWriteModuleScope _ -> "BL3007"
            | ProviderImportByModule _ | ProviderNoSelectiveImport _ -> "BL2003"
            | Other _ -> "BL3999"
    Blade.Diagnostics.mkError code (Blade.Diagnostics.Codes.phaseOfCode code) e.Span (formatTypeError e.Error)
    |> Blade.Diagnostics.withContext e.Context

/// Unified Diagnostic -> CompileError, for pipeline stages (elaborators) that
/// produce Diagnostics inside typeCheck's CompileError channel. Code and span survive; extra context appends outermost.
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
    | BindConst -> ReadOnly    // static / let const -> immutable
    | BindLet -> Assignable    // let -> assignable in scope
    | BindMut -> MutPassable   // let mut -> assignable + mut-passable

/// Enter a callable body: snapshot every currently-visible binding into
/// `OuterScope`, and mark the environment as being inside a body.
///
/// The two halves are one operation because the only scope boundary that
/// exists in the checker IS a callable body -- a LAMBDA body, a named-FUNCTION
/// declaration body, or an IMPL-METHOD body. (A `{ ... }` block does not enter
/// a scope; its lets go straight into `Variables`.) Keeping them together is
/// what makes `bodyLocalBinding` below sound.
let enterCallableBody env =
    { env with
        OuterScope = Map.foldBack Map.add env.Variables env.OuterScope
        InCallableBody = true }

/// Was `name` bound INSIDE the callable body currently being inferred?
///
/// `enterCallableBody` snapshots every visible binding into `OuterScope` at the
/// body boundary, and only a NESTED body extends it again -- so `OuterScope` is
/// always the snapshot taken at the INNERMOST enclosing body, and a name absent
/// from it was bound after that boundary. False at module level (no body, so
/// nothing is body-local) and false for a captured outer binding.
///
/// Shadowing reads conservatively: a body-local `let e` that shadows an outer
/// `e` answers false, because the outer name is in the snapshot. Callers use
/// this to opt OUT of an optimization, so a false negative costs efficiency,
/// never correctness.
let bodyLocalBinding (name: string) (env: TypeEnv) =
    env.InCallableBody && not (Map.containsKey name env.OuterScope)

/// Is `name` a CAPTURE of the lambda currently being inferred -- visible here,
/// but bound outside the innermost enclosing body, so `buildCaptures` will put
/// it in the lambda's `Captures` and codegen will forward it by name at every
/// call site?
///
/// Gated on `InLambdaBody`, NOT `InCallableBody`, because only a lambda has a
/// capture list: a NAMED function's `Captures` is always empty (IRCallable),
/// and the module bindings its body spells are served by main-local emission /
/// the S0 declaration hoist instead. Widening this to every callable body
/// silently breaks that path -- a named function reading a deferred binding by
/// name has nothing to materialize it (`function g(w) = w * reduce(c, (+))`
/// over a deferred `c` emits `c[0]` with no definition of `c`).
///
/// False at module level, like `bodyLocalBinding` -- there is no body, so a
/// name is neither body-local nor captured.
let capturedOuterBinding (name: string) (env: TypeEnv) =
    env.InLambdaBody && Map.containsKey name env.OuterScope

let registerTypeDef name info (env: TypeEnv) =
    { env with TypeDefs = Map.add name info env.TypeDefs }

/// Register a loaded provider module's struct types and return the binding's
/// module-struct type. The provider's dims/vars structs register as-is; a
/// synthetic top-level struct `name` is added so the binding carries
/// `.dims`/`.vars` fields (e.g. `sample.vars.temp` resolves to the real Array
/// type). Pure (no file IO) -- unit-testable with a mock module; reading `pm`'s file metadata is the caller's concern.
let registerProviderModule (env: TypeEnv) (name: string) (pm: IRModule) : TypeEnv * IRType =
    let envS =
        pm.Types |> List.fold (fun e td ->
            match td with
            | IRTDStruct (n, fields) -> registerTypeDef n (TDIStruct (n, [], fields, [])) e
            | IRTDEnumIdx (n, idx, values) ->
                // Provider-synthesized column enums (CSV headered mode):
                // registration folds string-literal column subscripts to
                // ordinals at dispatchAppOrIndex. Body is a synthesized
                // surface TypeExpr -- no source declaration exists.
                let bodyExpr =
                    mkExpr noSpan (ExprKind.ExprArrayLit (
                        values |> List.map (fun v ->
                            match v with
                            | EVString s -> mkExpr noSpan (ExprKind.ExprLit (LitString s))
                            | EVInt n -> mkExpr noSpan (ExprKind.ExprLit (LitInt n)))))
                registerTypeDef n (TDIEnumIdx (n, idx, values, TyEnumIdx bodyExpr)) e
            | IRTDIndexType (n, idx) ->
                // Axis types the provider derived from the file, exposed as
                // `<binding>.index.<dim>` (e.g. `Array<Float64 like
                // store.index.y>`) instead of hand-copying an extent that goes
                // stale on regeneration. QUALIFIED ONLY, deliberately: bare
                // names like `time` recur across stores and would clobber
                // last-write-wins in the shared TypeDefs map (alias down with
                // `type Y = store.index.y` if needed). Registered EXACTLY as
                // the provider built it (Tag = None, asserted by
                // NetcdfTests); lowerIndexType stamps a fresh Id per use, unifying on extent match like a hand-written Idx<64>.
                let extentExpr =
                    match idx.Extent with
                    | IRLit (IRLitInt v) -> mkExpr noSpan (ExprKind.ExprLit (LitInt v))
                    | _ -> mkExpr noSpan (ExprKind.ExprLit (LitInt 0L))
                let qualified = sprintf "%s.index.%s" name n
                registerTypeDef qualified (TDIIndexType (qualified, idx, TyIdx extentExpr)) e
            | _ -> e) env
    // Module-struct fields point at the dims/vars struct decls the provider
    // emitted, namespaced "<binding>__dims"/"<binding>__vars" so multiple
    // loads don't clobber each other in this flat TypeDefs map (a clobbered
    // entry silently retypes earlier uses). Bare `n = label` covers providers that emit unsuffixed structs.
    let structNames = pm.Types |> List.choose (function IRTDStruct (n, _) -> Some n | _ -> None)
    let fieldFor (label: string) =
        structNames
        |> List.tryFind (fun n -> n = label || n = sprintf "%s__%s" name label)
        |> Option.map (fun n -> (label, IRTNamed n))
    let moduleFields = [fieldFor "dims"; fieldFor "vars"] |> List.choose id
    let moduleStruct = TDIStruct (name, [], moduleFields, [])
    (registerTypeDef name moduleStruct envS, IRTNamed name)


/// Check if a variant type is a pure enum (all constructors have no data)
let isEnumType (env: TypeEnv) (parentName: string) : bool =
    match Map.tryFind parentName env.TypeDefs with
    | Some (TDIVariant (_, _, variants)) -> variants |> List.forall (fun (_, d) -> d.IsNone)
    | _ -> false

let registerVariantTag tag parentName payload (env: TypeEnv) =
    { env with VariantTags = Map.add tag (parentName, payload) env.VariantTags }

/// Why a UnitExpr failed to resolve. Both cases are hard declaration-site
/// errors at a `Unit` RHS -- an unknown name is BL3015, a terminal-quantity
/// misuse BL3011. The split survives because the ANNOTATION consumers
/// (compound unit annotations) still degrade rather than reject, and they
/// distinguish the two: see unitAnnoTerminalError in TypeCheck.fs.
type UnitResolveErr =
    | UResolveUnknown of name: string
    /// A quantity (nominal) name referenced inside unit algebra. Quantities
    /// are terminal: the nominal layer is exactly one level deep.
    | UResolveTerminal of quantity: string

/// Render a UnitResolveErr for the channels that still degrade rather than
/// reject (the defensive lowering fallback; annotation resolution).
let ppUnitResolveErr (e: UnitResolveErr) : string =
    match e with
    | UResolveUnknown name -> sprintf "Unknown unit '%s'" name
    | UResolveTerminal q -> sprintf "Quantity '%s' cannot appear in a unit expression (quantities are terminal)" q

/// Resolve a UnitExpr AST node to a canonical UnitSig. Quantity names
/// (Nominal = Some) are REJECTED in every position — a quantity is an
/// identity, not a factor, so it can neither be composed (`speed * m`) nor
/// re-derived from (`Unit q: speed`).
let rec resolveUnitExpr (units: Map<string, UnitSig>) (expr: UnitExpr) : Result<UnitSig, UnitResolveErr> =
    match expr with
    | UnitNamed name ->
        match Map.tryFind name units with
        | Some sig' when sig'.Nominal.IsSome -> Error (UResolveTerminal name)
        | Some sig' -> Ok sig'
        // Irrational scale constants (`pi`) resolve only AFTER the unit
        // table, so a user's own `Unit pi` still shadows the built-in and no
        // existing program changes meaning. Dimensionless: a constant
        // contributes a magnitude, never a dim.
        | None when unitScaleConstants.ContainsKey name ->
            Ok (unitOfDimsScaled Map.empty (scaleOfConst name))
        | None -> Error (UResolveUnknown name)
    | UnitOne -> Ok unitDimensionless
    | UnitScaleLit (num, den) -> Ok (unitOfDimsScaled Map.empty (scaleOfRational num den))
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

/// Names already in scope that a misspelling plausibly meant. Quantities are
/// excluded: suggesting one would only trade BL3015 for BL3011. Shared with
/// the ANNOTATION check in TypeCheck.fs, which raises the same BL3015.
let unitSpellingCandidates (units: Map<string, UnitSig>) (name: string) : string list =
    let close (a: string) (b: string) =
        // One transposition, or a one-character insert/delete/substitute --
        // enough for `pii`/`pi` and `metre`/`meter`, tight enough that an
        // unrelated unit never shows up as a suggestion.
        if abs (a.Length - b.Length) > 1 then false
        elif a.Length = b.Length then
            let diffs = Seq.zip a b |> Seq.filter (fun (x, y) -> x <> y) |> Seq.toList
            match diffs with
            | [] | [_] -> true
            | [(x1, y1); (x2, y2)] -> x1 = y2 && x2 = y1  // transposition
            | _ -> false
        else
            let short, long = if a.Length < b.Length then a, b else b, a
            // One deletion turns `long` into `short`.
            [0 .. long.Length - 1]
            |> List.exists (fun i -> long.Remove(i, 1) = short)
    let lowered = name.ToLowerInvariant()
    Map.toList units
    |> List.filter (fun (n, s) -> s.Nominal.IsNone)
    |> List.map fst
    |> List.append (Map.toList unitScaleConstants |> List.map fst)
    |> List.filter (fun n -> n <> name && (n.ToLowerInvariant() = lowered || close n name))
    |> List.distinct
    |> List.sort

/// Register a unit declaration in the environment. A `Unit` right-hand side
/// composes names already in scope, so a name that is neither a declared unit
/// nor a scale constant is a hard error (BL3015) -- the old warn-and-fallback
/// minted the declared name as a fresh BASE unit, which typechecks a
/// misspelling into a silently wrong dimension. A terminal-quantity misuse
/// stays BL3011.
let registerUnit (env: TypeEnv) (decl: UnitDecl) : Result<TypeEnv, TypeError> =
    // Base-unit signature: canonical form is {name: 1}
    let baseSig () = unitOfDims (Map.ofList [(decl.Name, 1)])
    let resolveErr (err: UnitResolveErr) =
        match err with
        | UResolveTerminal q -> QuantityTerminal (q, decl.Name)
        | UResolveUnknown n -> UnknownUnitName (n, decl.Name, unitSpellingCandidates env.Units n)
    let sigResult =
        match decl.Definition with
        | None | Some UnitBase -> Ok (baseSig ())
        | Some (UnitDerived expr) ->
            resolveUnitExpr env.Units expr
            |> Result.mapError resolveErr
        | Some (UnitQuantity expr) ->
            // Quantity: nominal identity entailing the RHS dims. The RHS is
            // resolved through the same terminal-checking path, so a quantity
            // on the RHS of another quantity rejects here too.
            resolveUnitExpr env.Units expr
            |> Result.map (fun resolved -> { resolved with Nominal = Some decl.Name })
            |> Result.mapError resolveErr
    sigResult |> Result.map (fun sig' ->
        { env with Units = Map.add decl.Name sig' env.Units })

// 2b. Generalization (needs VarInfo defined above)

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

// 3. Constant Expression Evaluation (for index extents)

/// Try to evaluate an AST expression to a compile-time int64.
