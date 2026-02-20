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
    | TyDependentIdx of param: Ident * body: TypeExpr  // (i: Idx<n>) -> Idx<n - i>
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
    Parallelism: (Ident * int) list       // omp(a: 2, b: 1)
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
