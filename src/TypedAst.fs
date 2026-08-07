// The typed AST resulting from type checking. Every expression node carries
// its inferred type, enabling IDE hover info, incremental type checking, and
// a clean separation of type inference from IR lowering. Mirrors Ast's
// structure but with type annotations and pre-computed symmetry information
// for combinator applications.

module Blade.TypedAst

open Blade.Ast
open Blade.IR
open Blade.Types

// Span and noSpan are inherited from Blade.Ast via 'open'.

type TypedVarInfo = {
    Name: string
    Type: IRType
    Identity: ArrayIdentity option
    IsMutable: bool
    VarId: IRId
}

type TypedParam = {
    Name: string
    Type: IRType
    Index: int
    VarId: IRId
    /// Source span of the parameter's NAME TOKEN, carried through from
    /// Ast.ParamDecl/LambdaParam so `references[]` can point go-to-definition
    /// at it. `noSpan` on synthesized params (the kernels elaborators invent).
    NameSpan: Span
}

type TypedLambdaInfo = {
    Params: TypedParam list
    Body: TypedExpr
    ReturnType: IRType
    CommGroups: int list list
    /// `where anticomm(...)` groups, by parameter index: the signed twin of
    /// CommGroups. Licensed output storage is the strict simplex (AntisymIdx<r,
    /// n>: no diagonal, negate on swapped reads), not the inclusive triangle.
    /// Kept separate from CommGroups so validators can distinguish a declared
    /// comm on a PNeg body from a declared anticomm on a PInv body.
    AntisymGroups: int list list
    /// Per-parameter sign parity of the body (`Types.KernelSignParity`, decl
    /// order), feeding `IR.deduceWreathTie`'s soundness gate. Populated only
    /// when this lambda reaches the seam as a kernel; other construction sites
    /// leave it empty (read as all-unknown). Lowering copies it onto the
    /// lifted `IRCallable` so codegen and the interpreter agree on the tie.
    SignParities: KernelSignParity list
    Captures: TypedVarInfo list
    IsCommutative: bool
    // Parallelization strategy assignments (see WhereClause.Parallel),
    // propagated from the lambda's where-clause. Today 0 or 1 element.
    Parallel: ParallelStrategy list
    // Self-binding for a named, recursive lambda: Some (name, id) when this is
    // `let const name = lambda(...)` (including the nested-`function` desugar)
    // and its body refers to itself. Threaded to Lowering so the lifted
    // callable keeps the real name/id and can call itself in emitted C++.
    SelfBinding: (string * IRId) option
}

and TypedMethodForInfo = {
    Arrays: TypedExpr list
    Identities: ArrayIdentity list
    ArrayTypes: IRArrayType list
    SDimsPerArray: int list
    TotalSDims: int
    SharedIndexTypes: IRIndexType list  // For co-iteration: shared iteration records (empty = not co-iteration; multi = product space)
}

and TypedObjectForInfo = {
    Kernel: TypedExpr
    CommGroups: int list list
    InputRanks: int list
    OutputRank: int
}

// Application info for the <@> combinator.
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
    /// True when Loop = TExprCompose(OpComposeObj, _, _) (or a TExprVar
    /// resolving to one), which puts input arrays in the Kernel slot rather
    /// than a callable; Lowering routes to IRComposeApply instead of
    /// IRApplyCombinator. Defaults to false for ordinary applies.
    IsComposeApply: bool
}

and TypedStmt =
    | TStmtLet of TypedBinding
    | TStmtAssign of lhs: TypedExpr * rhs: TypedExpr
    | TStmtExpr of TypedExpr
    | TStmtForIn of varName: string * varId: IRId * lo: TypedExpr * hi: TypedExpr * body: TypedStmt list

and TypedExpr = {
    Kind: TypedExprKind
    Type: IRType
    Span: Span
}

and TypedExprKind =
    // Literals
    | TExprLit of Literal
    // Wildcard hole `_` (typed sibling of TPatWild). Carries a hole type so it
    // flows through tuple inference; reaching lowering/codegen unconsumed is an error.
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

    // Pack tail from cons-destructuring `let head :: tail = pack`; `drop` is
    // the number of leading elements peeled. Lowers to IRPolyTail, expanded
    // into trailing pack params by arity-monomorphization. Only for Poly pack
    // scrutinees (tuples keep the TExprTuple-of-projections desugaring).
    | TExprPolyTail of pack: TypedExpr * drop: int
    
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
    
    // Complex literal `complex(re, im)`. Distinct from TExprTuple to preserve
    // scalar nature: runtime layout is two floats (std::complex<double>),
    // typed as scalar IRTScalar ETComplex64/128. Lowering routes to IRLitComplex.
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
    // halo<Inner, [offsets]> has no typed node -- it typechecks to a
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
    // fill_random(mod): internal builtin, random-filled array constructor. Type
    // comes from the binding annotation, so only appears as an annotated
    // let-binding value. Lowering records it in RandomInits; `modulus` is the
    // argument to rand() % modulus.
    | TExprFillRandom of modulus: TypedExpr
    // rand.<fam>(key, params..., shape): internal builtin, deterministic
    // random-array constructor, self-typed from the shape argument. Lowering
    // records (kind, key, pars) in RandomInits. `kind` is the family name
    // ("uniform" | "normal" | "exponential" | "gamma" | "poisson" |
    // "bernoulli" | "beta"); `pars` are the family's runtime Float64 scalar
    // parameters in surface order (empty for uniform/normal, one for
    // exponential/poisson/bernoulli, two for gamma/beta) -- ordinary typed
    // expressions, evaluated once per fill, NOT per draw; `dims` are the
    // static extents.
    | TExprRandGen of kind: string * key: TypedExpr * pars: TypedExpr list * dims: int list
    | TExprGuard of cond: TypedExpr * body: TypedExpr
    | TExprZero
    | TExprReynolds of kernel: TypedExpr * isAntisymmetric: bool
    
    // Arity special forms
    | TExprArity of paramName: string
    | TExprRank of TypedExpr
    
    // Filtered array
    | TExprMask of array: TypedExpr * pred: TypedExpr
    | TExprCompound of dense: TypedExpr * mask: TypedExpr
    | TExprSparse of values: TypedExpr * keys: TypedExpr
    | TExprIntersect of TypedExpr * TypedExpr
    | TExprUnion of TypedExpr * TypedExpr
    | TExprUnique of array: TypedExpr
    | TExprContains of array: TypedExpr * value: TypedExpr
    | TExprGroupBy of values: TypedExpr * grouping: TypedExpr
    | TExprGroupKeys of keys: TypedExpr list
    | TExprSort of array: TypedExpr * key: TypedExpr
    | TExprReduce of array: TypedExpr * kernel: TypedExpr * init: TypedExpr option
    | TExprProdSum of args: TypedExpr list  // prodsum(x1..xk): fused sum_t prod_l x_l(t) over rank-1 arrays
    | TExprTranspose of array: TypedExpr * dim1: int * dim2: int
    | TExprDecompact of array: TypedExpr * dim: int
    | TExprGram of left: TypedExpr * right: TypedExpr * isSameArray: bool
    /// matmul(A, B): A(m x k) * B(k x n) -> dense m x n, routed through
    /// blade_linalg rather than synthesized as a Blade triple loop.
    | TExprMatmul of left: TypedExpr * right: TypedExpr
    /// eigh(S): symmetric/Hermitian eigendecomposition of a rank-2 square
    /// operand, yielding (Q, LAM): Q's columns the eigenvectors, LAM
    /// descending. Routed through blade_lapack when available at elaboration
    /// time; otherwise `math.eigh` expands to Blade Jacobi source instead.
    | TExprEigh of operand: TypedExpr
    /// solve(A, b): the general dense linear solve A.x = b by partial-pivoted
    /// LU, A rank-2 square and b rank-1 of the matching extent, yielding x.
    /// ALWAYS a first-class node (unlike eigh): the native arm is emitted LU
    /// loops whose operation order the interpreter's `A.solveArray` mirrors, so
    /// the byte-identity differential covers the code an ordinary build runs.
    /// The LAPACK `dgesv` route is an availability-gated replacement for those
    /// loops, not a precondition for the node existing.
    | TExprSolve of matrix: TypedExpr * rhs: TypedExpr
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

and TypedBinding = {
    Name: string
    VarId: IRId
    Type: IRType
    Identity: ArrayIdentity option
    IsMutable: bool
    Value: TypedExpr
    /// Destructured sub-bindings: (name, varId, type) for PatTuple/PatCons/PatStruct
    SubBindings: (string * IRId * IRType) list
    /// How Lowering derives each SubBindings entry -- without it, `head ::
    /// tail` is indistinguishable from a tuple pattern and miscompiles into
    /// two positional projections. See DestructureShape.
    Destructure: DestructureShape
    /// Constraint guards run right after this binding (mutual-group joint
    /// checks). IRIds are allocated directly after the SubBinding ids, since
    /// module emission is IRId-ordered and later fresh ids would run too late.
    PostChecks: (IRId * TypedExpr) list
}

/// How a TypedBinding's SubBindings relate to its value. Lives on the binding
/// rather than each sub-binding entry because SubBindings' element shape is
/// consumed positionally by Cli.fs/Ide.fs/Zonk.fs for information only
/// Lowering needs.
and DestructureShape =
    /// Sub-binding i is element i of a tuple (or the field with its own name,
    /// for a struct scrutinee). Also the shape when there is no destructuring.
    | DSPositional
    /// Cons split over a tuple scrutinee. `::` is right-associative, so a chain
    /// `a :: b :: rest` flattens into leading leaves [a; b] plus one rest leaf.
    /// Every leaf but the last is positional; the last takes the whole
    /// remainder (re-tupled), or the bare element if the remainder is one
    /// element (Blade has no 1-tuple).
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
    /// Source span of the function's NAME TOKEN (see Ast.FunctionDecl.NameSpan).
    NameSpan: Span
}

// Typed type definitions, resolved from raw TypeDecl.
and TypedTypeDef =
    | TTDAlias of name: string * typeParams: string list * resolved: IRType
    | TTDStruct of name: string * typeParams: string list * fields: (string * IRType) list
    | TTDVariant of name: string * typeParams: string list * variants: (string * IRType option) list
    /// Index-type alias (`type RegionIdx = Idx<3>`), distinguished from
    /// TTDAlias so codegen emits `using RegionIdx = int64_t;` rather than a
    /// generic IRType alias.
    | TTDIndexType of name: string * idx: IRIndexType
    /// EnumIdx alias (`type LandType = EnumIdx<[101, 205, 307]>`), carrying
    /// the concrete value list for downstream reverse-lookup codegen.
    | TTDEnumIdx of name: string * idx: IRIndexType * values: EnumValue list
    /// Mutually constrained alias group (`type P1 = T1 and P2 = T2 where ...`);
    /// the joint constraint itself lives in TypeEnv.MutualGroups, not here.
    | TTDMutualGroup of members: (string * IRType) list

and TypedImplDecl = {
    ForType: TypeExpr
    TypeName: string
    Methods: TypedFunctionDecl list
}

and TypedDecl =
    | TDeclLet of TypedBinding
    | TDeclFunction of TypedFunctionDecl
    | TDeclType of TypedTypeDef
    | TDeclInterface of InterfaceDecl
    | TDeclImpl of TypedImplDecl
    | TDeclStatic of TypedBinding
    | TDeclUnit of UnitDecl
    | TDeclImport of QualifiedName * ImportStyle

type TypedModule = {
    Name: string list option     // Qualified module name
    Decls: TypedDecl list
}

type TypedProgram = {
    Modules: TypedModule list
}

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
