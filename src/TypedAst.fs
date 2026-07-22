// ============================================================================
// TypedAst.fs - Type-Annotated Abstract Syntax Tree
// ============================================================================
//
// This module defines the typed AST that results from type checking.
// Every expression node carries its inferred type, enabling:
// - IDE hover information
// - Incremental type checking
// - Clean separation of type inference from IR lowering
//
// The TypedAST mirrors the structure of AST but with type annotations
// and pre-computed symmetry information for combinator applications.

module Blade.TypedAst

open Blade.Ast
open Blade.IR
open Blade.Types

// ============================================================================
// Source Location (for error reporting and IDE support)
// ============================================================================

// Span and noSpan are inherited from Blade.Ast via 'open'

// ============================================================================
// Typed Variable Information
// ============================================================================

type TypedVarInfo = {
    Name: string
    Type: IRType
    Identity: ArrayIdentity option
    IsMutable: bool
    VarId: IRId
}

// ============================================================================
// Typed Lambda Information
// ============================================================================

type TypedParam = {
    Name: string
    Type: IRType
    Index: int
    VarId: IRId
}

type TypedLambdaInfo = {
    Params: TypedParam list
    Body: TypedExpr
    ReturnType: IRType
    CommGroups: int list list
    Captures: TypedVarInfo list
    IsCommutative: bool
    // Parallelization strategy assignments (list; see WhereClause.Parallel).
    // Propagated from the lambda's where-clause so lambda-level omp/cuda take
    // effect. Today 0 or 1 element.
    Parallel: ParallelStrategy list
}

// ============================================================================
// Typed Method-For Information
// ============================================================================

and TypedMethodForInfo = {
    Arrays: TypedExpr list
    Identities: ArrayIdentity list
    ArrayTypes: IRArrayType list
    SDimsPerArray: int list
    TotalSDims: int
    SharedIndexTypes: IRIndexType list  // For co-iteration: shared iteration records (empty = not co-iteration; multi = product space)
}

// ============================================================================
// Typed Object-For Information
// ============================================================================

and TypedObjectForInfo = {
    Kernel: TypedExpr
    CommGroups: int list list
    InputRanks: int list
    OutputRank: int
}

// ============================================================================
// Typed Application Information (for <@> combinator)
// ============================================================================

and TypedApplyInfo = {
    Loop: TypedExpr                         // Provenance: TExprMethodFor, TExprObjectFor, or TExprCompose(OpComposeObj,...)
    Kernel: TypedExpr
    Arrays: TypedExpr list                  // The actual array expressions
    Identities: ArrayIdentity list          // Array identity tracking (for symmetry)
    ArrayTypes: IRArrayType list            // Array type info
    SharedIndexTypes: IRIndexType list      // For co-iteration (zip): shared records (empty = not co-iteration)
    SymcomStates: SymcomState list
    TriangularLevels: bool list
    SDimsPerArray: int list
    KernelInputRanks: int list
    KernelOutputRank: int
    KernelTDims: IRIndexType list           // T-dimension index types from kernel return type
    SpeedupFactor: int64
    ReynoldsSpeedup: int64
    HasReynolds: bool
    OutputType: IRType
    IsCoIteration: bool
    /// True when this apply has Loop = TExprCompose(OpComposeObj, _, _) (or
    /// a TExprVar resolving to one). The TypeCheck arm that builds these
    /// puts the input arrays in the Kernel slot rather than a callable —
    /// Lowering uses this flag to route to IRComposeApply instead of
    /// IRApplyCombinator. Defaults to false for ordinary applies.
    IsComposeApply: bool
}

// ============================================================================
// Typed Statements (for blocks)
// ============================================================================

and TypedStmt =
    | TStmtLet of TypedBinding
    | TStmtAssign of lhs: TypedExpr * rhs: TypedExpr
    | TStmtExpr of TypedExpr
    | TStmtForIn of varName: string * varId: IRId * lo: TypedExpr * hi: TypedExpr * body: TypedStmt list

// ============================================================================
// Typed Expressions
// ============================================================================

and TypedExpr = {
    Kind: TypedExprKind
    Type: IRType
    Span: Span
}

and TypedExprKind =
    // Literals
    | TExprLit of Literal
    // Wildcard hole `_` in expression position (typed sibling of TPatWild). Carries
    // a hole type so it flows through tuple inference; only meaningful where a
    // context consumes it (a compound-index coordinate marks a FREE axis). Reaching
    // lowering/codegen unconsumed is an error.
    | TExprWildcard
    
    // Variables and names
    | TExprVar of name: string * varId: IRId * identity: ArrayIdentity option
    | TExprQualified of names: string list
    
    // Binary and unary operations
    | TExprBinOp of BinOpMode * BinOp * TypedExpr * TypedExpr
    | TExprUnaryOp of UnaryOp * TypedExpr
    
    // Function application (also used for array indexing)
    | TExprApp of func: TypedExpr * args: TypedExpr list
    
    // Poly-tuple indexing: args[k]
    | TExprTupleIndex of tuple: TypedExpr * index: TypedExpr
    
    // Field access
    | TExprField of TypedExpr * field: string * fieldIndex: int
    
    // Lambda
    | TExprLambda of TypedLambdaInfo
    
    // Let binding
    | TExprLet of name: string * varId: IRId * value: TypedExpr * body: TypedExpr
    
    // Match expression
    | TExprMatch of scrutinee: TypedExpr * cases: TypedMatchCase list
    
    // If-then-else
    | TExprIf of cond: TypedExpr * thenBr: TypedExpr * elseBr: TypedExpr
    
    // Tuple construction
    | TExprTuple of TypedExpr list
    
    // Complex literal: `complex(re, im)`
    // Distinct from TExprTuple to preserve scalar nature in IR.
    // The runtime layout is two floats (matching std::complex<double>),
    // but the type system treats this as a scalar (IRTScalar ETComplex64
    // or ETComplex128). Lowering routes this to IRLitComplex.
    | TExprComplexLit of re: TypedExpr * im: TypedExpr
    
    // Array literal
    | TExprArrayLit of elems: TypedExpr list * arrayType: IRArrayType
    
    // Loop constructs
    | TExprMethodFor of TypedMethodForInfo
    | TExprObjectFor of TypedObjectForInfo
    
    // Combinator application (with pre-computed symmetry info)
    | TExprApply of TypedApplyInfo
    
    // Other combinators (simpler, no symmetry analysis needed)
    | TExprBind of TypedExpr * TypedExpr
    | TExprParallel of TypedExpr * TypedExpr
    | TExprFusion of TypedExpr * TypedExpr
    | TExprFunctorMap of func: TypedExpr * comp: TypedExpr
    | TExprChoice of TypedExpr * TypedExpr
    // <|:> allocated-fallback: read left where its storage has the cell,
    // else right. Distinct from TExprChoice (zero-vs-nonzero on VALUES):
    // fallback keys on STORAGE (compound mask bit / dense pointer chain).
    | TExprFallback of TypedExpr * TypedExpr
    | TExprCompose of BinOp * TypedExpr * TypedExpr
    
    // Virtual arrays
    | TExprRange of indexTypes: IRIndexType list
    | TExprDotDot of lo: TypedExpr * hi: TypedExpr
    | TExprReverse of indexType: IRIndexType
    | TExprBlocked of indexType: IRIndexType * blockSize: TypedExpr
    // NOTE: halo<Inner, [offsets]> has NO typed node — it typechecks to a
    // TExprRange over a "__halowin|"-tagged slot (TypeCheck.haloSlotOf); the
    // per-slot center offset is re-derived from the tag at loop building.
    
    // Zip and stack
    | TExprZip of TypedExpr list
    | TExprStack of TypedExpr list
    | TExprJoin of arrays: TypedExpr list * dim: int
    
    // Special forms
    | TExprPure of TypedExpr
    | TExprCompute of TypedExpr
    | TExprRead of TypedExpr
    // fill_random(mod): internal builtin -- a random-filled array constructor.
    // The result array type comes from the binding annotation (bidirectional
    // check), so this only appears as an annotated let-binding value. Lowering
    // records it in RandomInits; codegen emits allocate<> + the runtime
    // fill_random. `modulus` is the argument to rand() % modulus.
    | TExprFillRandom of modulus: TypedExpr
    // rand.uniform/normal(key, shape): internal builtin — a deterministic
    // random-array constructor. Self-typed from the (static) shape argument, so
    // it needs no annotation. Lowering records (kind, key) in RandomInits;
    // codegen emits allocate<> + the runtime blade_rand fill. `kind` is
    // "uniform" | "normal"; `dims` are the static extents.
    | TExprRandGen of kind: string * key: TypedExpr * dims: int list
    | TExprGuard of cond: TypedExpr * body: TypedExpr
    | TExprZero
    | TExprReynolds of kernel: TypedExpr * isAntisymmetric: bool
    
    // Arity special forms
    | TExprArity of paramName: string
    | TExprRank of TypedExpr
    
    // Filtered array
    | TExprMask of array: TypedExpr * pred: TypedExpr
    | TExprCompound of dense: TypedExpr * mask: TypedExpr
    | TExprIntersect of TypedExpr * TypedExpr
    | TExprUnion of TypedExpr * TypedExpr
    | TExprUnique of array: TypedExpr
    | TExprContains of array: TypedExpr * value: TypedExpr
    | TExprGroupBy of values: TypedExpr * grouping: TypedExpr
    | TExprGroupKeys of keys: TypedExpr list
    | TExprSort of array: TypedExpr * key: TypedExpr
    | TExprReduce of array: TypedExpr * kernel: TypedExpr * init: TypedExpr option
    | TExprProdSum of args: TypedExpr list  // prodsum(x1..xk): fused Σ_t Π_ℓ xℓ(t) over rank-1 arrays
    | TExprTranspose of array: TypedExpr * dim1: int * dim2: int
    | TExprDecompact of array: TypedExpr * dim: int
    | TExprGram of left: TypedExpr * right: TypedExpr * isSameArray: bool
    | TExprArrayNegate of array: TypedExpr
    | TExprArrayConjugate of array: TypedExpr
    | TExprExtents of array: TypedExpr
    
    // Struct construction
    | TExprStruct of typeName: string * fields: (string * TypedExpr) list
    
    // Index expression (array indexing result)
    | TExprIndex of array: TypedExpr * indices: TypedExpr list * identity: ArrayIdentity option
    
    // Block expression (preserves statement structure for IDE support)
    | TExprBlock of stmts: TypedStmt list * finalExpr: TypedExpr option
    
    // Assignment expression
    | TExprAssign of lhs: TypedExpr * rhs: TypedExpr
    
    // Sequence (evaluated in order, result is last)
    | TExprSequence of TypedExpr list
    
    // Replicate
    | TExprReplicate of count: TypedExpr * body: TypedExpr
    
    // Alignment
    | TExprAlign of exprs: TypedExpr list * spec: Ast.AlignSpec option
    
    // Sectioned operator (e.g., (+) becomes a lambda)
    | TExprSection of BinOp

    // Partial application of operator (e.g., (+ 3) or (3 +))
    | TExprPartialApp of op: BinOp * arg: TypedExpr * isLeft: bool

    // Runtime constraint guard: emits `if (!(cond)) { cerr << message; abort(); }`.
    // Synthesized by the checker for mutual-group joint bindings (and, in later
    // phases, struct constraint checks); not expressible in surface syntax.
    | TExprConstraintCheck of cond: TypedExpr * message: string

// ============================================================================
// Typed Pattern Matching
// ============================================================================

and TypedMatchCase = {
    Pattern: TypedPattern
    Guard: TypedExpr option
    Body: TypedExpr
}

and TypedPattern = {
    Kind: TypedPatternKind
    Type: IRType
    Bindings: (string * IRId * IRType) list  // Variables bound by this pattern
}

and TypedPatternKind =
    | TPatWild
    | TPatVar of name: string * varId: IRId
    | TPatLit of Literal
    | TPatTuple of TypedPattern list
    | TPatCons of TypedPattern * TypedPattern
    | TPatVariant of tag: string * payload: TypedPattern option * isEnum: bool
    | TPatStruct of typeName: string * fields: (string * TypedPattern) list
    | TPatGuarded of TypedPattern * TypedExpr

// ============================================================================
// Typed Declarations
// ============================================================================

and TypedBinding = {
    Name: string
    VarId: IRId
    Type: IRType
    Identity: ArrayIdentity option
    IsMutable: bool
    Value: TypedExpr
    /// Destructured sub-bindings: (name, varId, type) for PatTuple/PatCons/PatStruct
    SubBindings: (string * IRId * IRType) list
    /// How Lowering must derive each SubBindings entry from the primary value.
    /// See DestructureShape — without it a cons pattern is indistinguishable
    /// from a tuple pattern by the time lowering runs, and `head :: tail`
    /// miscompiles into two positional projections.
    Destructure: DestructureShape
    /// Constraint guards to run right after this binding (mutual-group joint
    /// checks). IRIds are allocated by the checker directly after the
    /// SubBinding ids — module emission is IRId-ordered, so lowering-time
    /// fresh ids would sort to the end and run too late.
    PostChecks: (IRId * TypedExpr) list
}

/// How a TypedBinding's SubBindings relate to the primary binding's value.
///
/// The tag lives on the BINDING rather than on each sub-binding entry because
/// SubBindings' element shape `(name, varId, type)` is consumed positionally by
/// Cli.fs, Ide.fs and Zonk.fs; widening the tuple would ripple through all of
/// them for information only Lowering needs.
and DestructureShape =
    /// Sub-binding i is element i of a tuple (or, for a struct scrutinee, the
    /// field with the sub-binding's own name). Also the shape of a binding with
    /// no destructuring at all.
    | DSPositional
    /// Cons split over a tuple scrutinee. `::` is right-associative, so a chain
    /// `a :: b :: rest` is flattened by the checker into leading leaves [a; b]
    /// plus one REST leaf. Every leaf but the LAST is positional as usual; the
    /// last one takes the whole remainder — all elements from its own index
    /// onward, re-tupled — rather than the single element at its index. A
    /// one-element remainder binds that element bare, because Blade has no
    /// 1-tuple: `(x)` is just `x`.
    | DSConsRest

and TypedFunctionDecl = {
    Name: string
    FuncId: IRId
    TypeParams: string list
    Params: TypedParam list
    ReturnType: IRType
    WhereClause: WhereClause option
    Body: TypedExpr
    CommGroups: int list list
    IsStatic: bool
}

// ============================================================================
// Typed Type Definitions (resolved from raw TypeDecl)
// ============================================================================

and TypedTypeDef =
    | TTDAlias of name: string * typeParams: string list * resolved: IRType
    | TTDStruct of name: string * typeParams: string list * fields: (string * IRType) list
    | TTDVariant of name: string * typeParams: string list * variants: (string * IRType option) list
    /// Index-type alias: `type RegionIdx = Idx<3>` and friends. Distinguished
    /// from TTDAlias so codegen can emit `using RegionIdx = int64_t;` rather
    /// than treating it as a generic IRType alias and rendering nonsense.
    | TTDIndexType of name: string * idx: IRIndexType
    /// EnumIdx alias: `type LandType = EnumIdx<[101, 205, 307]>`. Carries the
    /// concrete value list so reverse-lookup codegen can be generated by
    /// downstream stages.
    | TTDEnumIdx of name: string * idx: IRIndexType * values: EnumValue list
    /// Mutually constrained alias group: `type P1 = T1 and P2 = T2 where ...`.
    /// Members lower as ordinary transparent aliases; the joint constraint
    /// itself lives in TypeEnv.MutualGroups and is emitted at binding sites,
    /// not here.
    | TTDMutualGroup of members: (string * IRType) list

// ============================================================================
// Typed Impl Declaration
// ============================================================================

and TypedImplDecl = {
    ForType: TypeExpr
    TypeName: string
    Methods: TypedFunctionDecl list
}

// ============================================================================
// Typed Declarations
// ============================================================================

and TypedDecl =
    | TDeclLet of TypedBinding
    | TDeclFunction of TypedFunctionDecl
    | TDeclType of TypedTypeDef
    | TDeclInterface of InterfaceDecl
    | TDeclImpl of TypedImplDecl
    | TDeclStatic of TypedBinding
    | TDeclUnit of UnitDecl
    | TDeclImport of QualifiedName * ImportStyle

// ============================================================================
// Typed Module and Program
// ============================================================================

type TypedModule = {
    Name: string list option     // Qualified module name
    Decls: TypedDecl list
}

type TypedProgram = {
    Modules: TypedModule list
}

// ============================================================================
// Helper Constructors
// ============================================================================

/// Create a typed expression with a given kind and type
let mkTyped kind ty : TypedExpr = 
    { Kind = kind; Type = ty; Span = noSpan }

/// Create a typed expression with span
let mkTypedSpan kind ty span : TypedExpr = 
    { Kind = kind; Type = ty; Span = span }

/// Create a typed literal
let mkLit lit ty = mkTyped (TExprLit lit) ty

/// Create a typed variable reference
let mkVar name varId identity ty = 
    mkTyped (TExprVar (name, varId, identity)) ty

/// Create a typed binary operation
let mkBinOp mode op left right ty =
    mkTyped (TExprBinOp (mode, op, left, right)) ty

/// Create a typed application
let mkApp func args ty =
    mkTyped (TExprApp (func, args)) ty

/// Create a typed let binding expression
let mkLet name varId value body ty =
    mkTyped (TExprLet (name, varId, value, body)) ty

/// Create a typed if-then-else
let mkIf cond thenBr elseBr ty =
    mkTyped (TExprIf (cond, thenBr, elseBr)) ty

/// Create a typed tuple
let mkTuple elems ty =
    mkTyped (TExprTuple elems) ty
