// Blade-DSL Abstract Syntax Tree
// Based on Blade Formalism v9

module Blade.Ast

open System

// ============================================================================
// Source Location Tracking
// ============================================================================

type Span = {
    StartLine: int
    StartCol: int
    EndLine: int
    EndCol: int
    File: string option
}

let noSpan = { StartLine = 0; StartCol = 0; EndLine = 0; EndCol = 0; File = None }

type Located<'T> = {
    Value: 'T
    Span: Span
}

let locate span value = { Value = value; Span = span }
let at span value = { Value = value; Span = span }

// ============================================================================
// Identifiers and Names
// ============================================================================

type Ident = string

type QualifiedName = Ident list  // e.g., ["Module"; "SubModule"; "Name"]

// ============================================================================
// Literals
// ============================================================================

type Literal =
    | LitInt of int64
    | LitFloat of float
    | LitBool of bool
    | LitString of string
    | LitChar of char
    | LitUnit  // ()

// ============================================================================
// Operators
// ============================================================================

type BinOp =
    // Arithmetic
    | OpAdd       // +
    | OpSub       // -
    | OpMul       // *
    | OpDiv       // /
    | OpMod       // %
    | OpCaret     // ^ (power/exponentiation)
    // Comparison
    | OpEq        // ==
    | OpNeq       // !=
    | OpLt        // <
    | OpLe        // <=
    | OpGt        // >
    | OpGe        // >=
    // Logical
    | OpAnd       // &&
    | OpOr        // ||
    // Combinators
    | OpApply       // <@>
    | OpBind        // >>=
    | OpParallel    // <&>
    | OpFusion      // <&!>
    | OpArrayProd   // <*>
    | OpFunctor     // <$>
    | OpChoice      // <|>
    | OpFallback    // <|:>
    | OpComposeObj  // >>@
    | OpComposeMeth // @>>
    | OpCompose     // >> (classic function composition)
    | OpCons        // ::

/// Mode for binary operations
type BinOpMode =
    | Elementwise   // a + b (zip iteration)
    | Outer         // a [+] b (cross iteration)

type UnaryOp =
    | OpNeg       // -
    | OpNot       // !
    | OpConj      // conj(x) — complex conjugate (identity on real)

type AssignOp =
    | AssignEq    // =
    | AssignAdd   // +=
    | AssignSub   // -=
    | AssignMul   // *=
    | AssignDiv   // /=

// ============================================================================
// Types
// ============================================================================

type Mutability =
    | Immutable       // default parameter
    | Mutable         // mut
    | Static          // static (compile-time)

/// Parallelization strategy requested by a where-clause. Parallelism is OPT-IN:
/// no strategy => single-threaded. `omp` and `cuda` are sibling backends. These
/// are standalone (not in the recursive AST chain) because a strategy does not
/// reference any recursive AST node — it carries only plain descriptors.
type OmpStrategy = {
    // Per-variable dim counts from omp(a: 2, b: 1) => [("a",2); ("b",1)].
    // (variable-name, number-of-dims-to-parallelize). Maps in lowering to the
    // IRCallable.Parallelism (param-index, level) shape.
    Vars : (Ident * int) list
}

type CudaStrategy = {
    BlockSize : int        // CUDA launch block size; default 256
}

type ParallelStrategy =
    | Omp of OmpStrategy
    | Cuda of CudaStrategy

type TypeExpr =
    // Primitive types
    | TyInt32
    | TyInt64
    | TyFloat32
    | TyFloat64
    | TyComplex64
    | TyComplex128
    | TyBool
    | TyString
    | TyChar
    | TyUnit
    // Named type (possibly generic)
    | TyNamed of Ident * TypeExpr list
    // Array type: Array<T like I1, I2, ...>
    | TyArray of elemType: TypeExpr * indexTypes: TypeExpr list
    // Abstract array type: Float64^r or similar where element type is concrete
    // For type variable arities (T^r), use TyVar with arity instead
    | TyAbstractArray of elemType: TypeExpr * rank: Expr * symmetry: int list option
    // Function type: (T1, T2, ...) -> R
    | TyFunc of args: TypeExpr list * ret: TypeExpr
    // Tuple type: (T1, T2, ...)
    | TyTuple of TypeExpr list
    // Type variable (for parametric polymorphism)
    // Ident is a single uppercase letter (T, U, V, ...)
    // int option is the arity: None or Some 0 = scalar, Some k = rank-k array
    | TyVar of Ident * int option
    // Index types
    | TyIdx of extent: Expr
    | TySymIdx of arity: int * extent: Expr
    | TyAntisymIdx of arity: int * extent: Expr
    | TyBoundedIdx of lower: Expr * upper: Expr
    | TyCompoundIdx of mask: Expr
    | TyEquivIdx of dim: Expr * group: TypeExpr * rep: TypeExpr
    | TyHermitianIdx of extent: Expr
    | TyEnumIdx of values: Expr  // EnumIdx<[v1, v2, ...]> — dependent on static array
    // DepIdx<outer, lambda(param) -> body> — function-parameterized inner extent.
    // The eta-reduced surface form `DepIdx<outer, func>` is desugared to the
    // lambda form at parse time, so all DepIdx values land here.
    | TyDepIdx of outer: TypeExpr * param: Ident * body: TypeExpr
    // RaggedIdx<lengths> — externally parameterized inner extent. The lengths
    // expression is an array (or a name resolving to an array); its outer
    // index implicitly defines RaggedIdx's outer index.
    | TyRaggedIdx of lengths: Expr
    // RaggedIdx<_> — opaque-extent variant. The inner extent is supplied by
    // the surrounding context (typically a kernel-parameter type whose extent
    // is filled in at the peel point of a parent ragged array). Distinct from
    // TyRaggedIdx because it carries no lengths expression — there is nothing
    // to look up; the extent is whatever the loop binding provides.
    | TyRaggedIdxOpaque
    // With constraints
    | TyConstrained of TypeExpr * Constraint list
    // Poly type for arity polymorphism
    | TyPoly of TypeExpr  // Poly<T^r>

and Constraint =
    | CnComm of Ident list              // comm(a, b, c)
    | CnAntisymm of Ident list          // antisymm(a, b)
    | CnReynolds of Ident list * bool   // reynolds([a,b], antisym?)
    | CnEquiv of Ident * TypeExpr       // equiv(G, rho)

and WhereClause = {
    Commutativity: Ident list list        // comm(a,b), comm(c,d)
    // Parallelization strategy assignments. A LIST of per-backend groupings,
    // each carrying its own dimensions (OmpStrategy.Vars / CudaStrategy). Today
    // the list holds 0 or 1 element: [] => serial, [single] => one strategy.
    // The parser enforces a SINGLE-BACKEND validation rule (rejecting e.g.
    // omp+cuda together) — see parseWhereClause. This is deliberate scaffolding:
    // the eventual mixed-strategy feature (`omp(a:1), cuda(b:...)` — different
    // backends on different dims of one kernel) becomes a RELAXATION of that
    // validation rule, NOT a type change, because the list already represents
    // multiple per-dim assignments. Mixed strategies additionally require the
    // host/device mid-nest boundary machinery (deferred), so support is gated on
    // that, but the data model no longer forecloses it.
    Parallel: ParallelStrategy list       // [] => serial; today 0 or 1 element
    TDims: TDimSpec list
}

and TDimSpec = {
    Extent: Expr
    Symmetry: int
    Name: string option
}

// ============================================================================
// Patterns
// ============================================================================

and Pattern =
    | PatWildcard                           // _
    | PatVar of Ident                       // x
    | PatLit of Literal                     // 42, "hello", etc.
    | PatTuple of Pattern list              // (p1, p2, p3)
    | PatCons of Pattern * Pattern          // head :: tail
    | PatStruct of Ident * (Ident * Pattern) list  // Point { x, y }
    | PatVariant of Ident * Pattern option  // Some(x), None
    | PatGuarded of Pattern * Expr          // p if condition
    | PatTyped of Pattern * TypeExpr        // p : T

// ============================================================================
// Expressions
// ============================================================================

and Expr =
    // Literals
    | ExprLit of Literal
    // Variables and names
    | ExprVar of Ident
    | ExprQualified of QualifiedName
    // Binary and unary operations
    | ExprBinOp of BinOpMode * BinOp * Expr * Expr
    | ExprUnaryOp of UnaryOp * Expr
    // Function application (also used for array indexing since arrays are functions)
    | ExprApp of func: Expr * args: Expr list
    // Poly-tuple indexing with [] syntax: args[k]
    | ExprTupleIndex of tuple: Expr * index: Expr
    // Field access
    | ExprField of Expr * Ident
    // Lambda
    | ExprLambda of parms: LambdaParam list * whereClause: WhereClause option * body: Expr
    // Let binding
    | ExprLet of binding: Binding * body: Expr
    // Match expression
    | ExprMatch of scrutinee: Expr * cases: MatchCase list
    // If-then-else (sugar for match on bool)
    | ExprIf of cond: Expr * thenBr: Expr * elseBr: Expr
    // Tuple construction
    | ExprTuple of Expr list
    // Array literal
    | ExprArrayLit of Expr list
    // Block (sequence of statements, last is result)
    | ExprBlock of Stmt list * Expr option
    // Loop constructs
    | ExprMethodFor of arrays: Expr list
    | ExprObjectFor of kernel: Expr
    // Virtual arrays
    | ExprRange of TypeExpr                // range<I>
    | ExprDotDot of lo: Expr * hi: Expr  // a..b — anonymous range sugar
    | ExprReverse of TypeExpr              // reverse<I>
    | ExprBlocked of TypeExpr * Expr       // blocked<I, K>
    // Zip and align
    | ExprZip of Expr list
    | ExprAlign of Expr list * AlignSpec option
    // Stack
    | ExprStack of Expr list
    // Combinators
    | ExprPure of Expr
    | ExprCompute of Expr                  // expr |> compute
    | ExprGuard of cond: Expr * body: Expr
    | ExprSequence of Expr list
    | ExprReplicate of count: Expr * body: Expr
    | ExprReynolds of kernel: Expr * isAntisymmetric: bool  // reynolds(kernel) or reynolds(kernel, Antisymmetric)
    // Type annotation
    | ExprTyped of Expr * TypeExpr
    // Arity special forms
    | ExprArity of Ident                      // arity(paramName) - only valid for Poly<> params
    | ExprNth                              // nth keyword (recursion depth)
    | ExprZero                             // zero keyword
    | ExprRank of Expr                     // rank(A) - get rank of array
    | ExprMask of array: Expr * pred: Expr // mask(A, pred) - filter array by predicate
    | ExprIntersect of Expr * Expr         // intersect(A, B) - elements in both
    | ExprUnion of Expr * Expr             // union(A, B) - elements in either
    | ExprUnique of array: Expr            // unique(A) - dedup, first-occurrence order
    | ExprContains of array: Expr * value: Expr  // contains(A, x) - is x present in A
    | ExprGroupBy of values: Expr * grouping: Expr  // group_by(vals, gk) - apply grouping to values
    | ExprGroupKeys of keys: Expr list             // group_keys(keys1, keys2, ...) - build CSR grouping structure (compound if >1 key)
    | ExprSort of array: Expr * key: Expr          // sort(A, key) - sort array by key function (stable)
    | ExprReduce of array: Expr * kernel: Expr     // reduce(A, op) - reduce innermost dim by binary kernel
    | ExprTranspose of array: Expr * dim1: int * dim2: int  // transpose(A, [d1, d2]) - swap two arity-1 SymNone axes (hard; allocates)
    | ExprDecompact of array: Expr * dim: int  // decompact(A, d) - pull the compact component at dim d out as a free Idx (hard; allocates dense)
    | ExprGram of left: Expr * right: Expr  // gram(A, B) = A * B^H: result[i][j] = sum_k A[i][k]*conj(B[j][k]). Square+Hermitian/symmetric when A,B same array; dense otherwise.
    | ExprExtents of array: Expr                   // extents(A) - innermost dim extent (rank-1 only for now)
    // Struct construction
    | ExprStruct of Ident * (Ident * Expr) list  // Point { x = 1, y = 2 }
    // Sectioned operators
    | ExprSection of BinOp                 // (+), (*), etc.
    | ExprPartialApp of BinOp * Expr * bool  // (+ 1) or (1 +), bool = is left section
    // Assignment expression (for imperative updates)
    | ExprAssign of lhs: Expr * rhs: Expr
    // For-loop expression (loop object construction)
    | ExprFor of source: ForSource * whereClauses: Constraint list * kernel: Expr option
    
and ForSource =
    | ForArrays of arrays: Expr list * inClause: Expr option  // (A, B) [in virtualArray]
    | ForKernel of kernel: Expr  // lambda(...) -> ...

and LambdaParam = {
    Name: Ident
    Type: TypeExpr option
}

and MatchCase = {
    Pattern: Pattern
    Guard: Expr option
    Body: Expr
}

and AlignSpec = {
    Offsets: (int * int) list  // dimension, offset pairs
    Boundary: BoundaryMode
}

and BoundaryMode =
    | BndShrink
    | BndPad of Expr
    | BndPeriodic
    | BndReflect

// ============================================================================
// Statements
// ============================================================================

and Stmt =
    | StmtLet of Binding
    | StmtAssign of lhs: Expr * op: AssignOp * rhs: Expr
    | StmtExpr of Expr
    | StmtForIn of varName: string * range: Expr * body: Stmt list

and Binding = {
    Mutability: BindingMut
    Pattern: Pattern
    Type: TypeExpr option
    Value: Expr
}

and BindingMut =
    | BindConst    // let const
    | BindLet      // let
    | BindMut      // let mut

// ============================================================================
// Declarations
// ============================================================================

type FunctionDecl = {
    Name: Ident
    TypeParams: Ident list
    Params: ParamDecl list
    WhereClause: WhereClause option
    ReturnType: TypeExpr option
    Body: Expr
    IsStatic: bool
}

and ParamDecl = {
    Name: Ident
    Type: TypeExpr option
    Mutability: Mutability
}

type TypeDecl =
    // type alias
    | TyDeclAlias of name: Ident * typeParams: Ident list * body: TypeExpr
    // sum type (enum/variant)
    | TyDeclSum of name: Ident * typeParams: Ident list * variants: VariantDecl list
    // struct (with optional where invariant)
    | TyDeclStruct of name: Ident * typeParams: Ident list * fields: FieldDecl list * invariant: Expr option

and VariantDecl = {
    Name: Ident
    Data: TypeExpr option  // None for unit variants
}

and FieldDecl = {
    Name: Ident
    Type: TypeExpr
    Default: Expr option
}

type InterfaceDecl = {
    Name: Ident
    TypeParams: Ident list
    Methods: FunctionSig list
}

and FunctionSig = {
    Name: Ident
    Params: ParamDecl list
    ReturnType: TypeExpr
}

type ImplDecl = {
    Interface: Ident
    ForType: TypeExpr
    Methods: FunctionDecl list
}

type UnitDecl = {
    Name: Ident
    Definition: UnitDef option
}

and UnitDef =
    | UnitBase                           // base unit
    | UnitDerived of UnitExpr            // derived from other units

and UnitExpr =
    | UnitNamed of Ident
    | UnitMul of UnitExpr * UnitExpr
    | UnitDiv of UnitExpr * UnitExpr
    | UnitPow of UnitExpr * int

/// How names from an imported module are brought into scope
type ImportStyle =
    | ImportQualified of Ident option    // import Math / import Math as M → qualified access
    | ImportSelective of Ident list       // from Math import pi, e → unqualified access

// Top-level declarations
type Decl =
    | DeclFunction of FunctionDecl
    | DeclType of TypeDecl
    | DeclInterface of InterfaceDecl
    | DeclImpl of ImplDecl
    | DeclUnit of UnitDecl
    | DeclLet of Binding
    | DeclStatic of Binding              // static x = ...
    | DeclImport of QualifiedName * ImportStyle  // import A.B.C as X / from A import x, y

// ============================================================================
// Module Structure
// ============================================================================

type ModuleDecl = {
    Name: QualifiedName
    Imports: ImportDecl list
    Decls: Located<Decl> list
}

and ImportDecl = {
    Module: QualifiedName
    Alias: Ident option
    Items: ImportItem list option  // None means import all
}

and ImportItem =
    | ImportName of Ident
    | ImportHiding of Ident

// ============================================================================
// Program
// ============================================================================

type Program = {
    Modules: ModuleDecl list
}
