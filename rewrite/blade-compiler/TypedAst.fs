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
    SharedIndexType: IRIndexType option  // For co-iteration: shared index space from 'in' clause
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
    Loop: TypedExpr
    Kernel: TypedExpr
    SymcomStates: SymcomState list
    TriangularLevels: bool list
    SDimsPerArray: int list
    KernelInputRanks: int list
    KernelOutputRank: int
    SpeedupFactor: int64
    ReynoldsSpeedup: int64
    HasReynolds: bool
    OutputType: IRType
    IsCoIteration: bool
}

// ============================================================================
// Typed Statements (for blocks)
// ============================================================================

and TypedStmt =
    | TStmtLet of TypedBinding
    | TStmtAssign of lhs: TypedExpr * rhs: TypedExpr
    | TStmtExpr of TypedExpr

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
    | TExprCompose of BinOp * TypedExpr * TypedExpr
    
    // Virtual arrays
    | TExprRange of indexType: IRIndexType
    | TExprReverse of indexType: IRIndexType
    | TExprBlocked of indexType: IRIndexType * blockSize: TypedExpr
    
    // Zip and stack
    | TExprZip of TypedExpr list
    | TExprStack of TypedExpr list
    
    // Special forms
    | TExprPure of TypedExpr
    | TExprCompute of TypedExpr
    | TExprGuard of cond: TypedExpr * body: TypedExpr
    | TExprReynolds of kernel: TypedExpr * isAntisymmetric: bool
    
    // Arity special forms
    | TExprArity of paramName: string
    | TExprRank of TypedExpr
    
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
    | TPatVariant of tag: string * payload: TypedPattern option
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
}

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
    | TTDStruct of name: string * typeParams: string list * fields: (string * IRType) list * invariant: TypedExpr option
    | TTDVariant of name: string * typeParams: string list * variants: (string * IRType option) list

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
