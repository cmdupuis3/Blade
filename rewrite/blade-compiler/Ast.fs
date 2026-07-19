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
    | OpReal      // real(z) — real part of a complex (identity on real)
    | OpImag      // imag(z) — imaginary part of a complex (0 on real)
    | OpArg       // arg(z) — phase angle of a complex
    | OpMath of string  // scalar math intrinsic: exp/log/sqrt/sin/cos/... —
                        // surface form is a plain call `exp(x)`; TypeCheck
                        // rewrites unbound whitelisted names to this op
                        // (user definitions of the same name shadow it)

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
/// no strategy => single-threaded. `omp`, `cuda`, and `mpi` are sibling
/// backends. These are standalone (not in the recursive AST chain) because a
/// strategy does not reference any recursive AST node — it carries only plain
/// descriptors.
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
    // Bare `mpi`: rank-count is a runtime property (mpiexec -n N), so the
    // strategy carries no payload; decomposition options can be added later.
    | Mpi

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
    // Typed dist tower (ppl/NOTES.md): Dist<order, Elem like I1, ..., Ik>.
    // order is any statically-evaluable int expression (literal, `let
    // static`, or static-function call — the replicate-count contract);
    // axes are the variable-axis index types of the underlying random
    // vector, parsed with the same `like` syntax as Array's index list.
    | TyDist of order: Expr * elemType: TypeExpr * axes: TypeExpr list
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
    | TySymIdx of rank: int * extent: Expr
    | TyAntisymIdx of rank: int * extent: Expr
    | TyBoundedIdx of lower: Expr * upper: Expr
    | TyCompoundIdx of mask: Expr
    // Dormant scaffolding for a GENERAL group-parameterized rep index. For
    // O(3)/SO(3) the transforms-as feature shipped (2026-07-18) on
    // IrrepsIdx + the `where ml.equiv(G)` function constraint
    // (ml/compiler/MLEquiv.fs) instead — the spec IS the rep, parity
    // distinguishes the groups. Surface this form only when a second group
    // family (finite groups via Reynolds) arrives; IrrepsIdx<spec> is then
    // reinterpretable as TyEquivIdx(total_dim(spec), O3, spec).
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
    // IrrepsIdx<spec> — block-structured dense index over an equivariant-NN
    // irreps spec (a static array of (l, parity, mult) int triples). Extent
    // is total_dim(spec) = Σ mult*(2l+1); every cell is stored (flat dense,
    // no compression) — the spec matters for type IDENTITY, not storage.
    // The spec is an expression resolved at typecheck via StaticEval.
    | TyIrrepsIdx of spec: Expr
    // halo<Inner, [offsets]> in TYPE position — a stencil traversal
    // transformer wrapping an inner index type, legal ONLY as a range<> slot
    // (n-D separable composition: range<halo<Lat,[..]>, halo<Lon,[..]>>).
    // Not a storage dimension: rejected in Array<... like ...> lists. The
    // offsets are a static signed-int array (center = 0, sign = direction).
    | TyHalo of inner: TypeExpr * offsets: Expr
    // With constraints
    | TyConstrained of TypeExpr * Constraint list
    // Poly type for arity polymorphism
    | TyPoly of TypeExpr  // Poly<T^r>

and Constraint =
    | CnComm of Ident list              // comm(a, b, c)
    | CnAntisymm of Ident list          // antisymm(a, b)
    | CnReynolds of Ident list * bool   // reynolds([a,b], antisym?)
    // equiv(G, rho) — superseded by WhereClause.Custom + the Blade.Constraints
    // registry: `where ml.equiv(O3|SO3)` parses as a Custom conjunct and is
    // judged by ml/compiler/MLEquiv.fs. Retained as documentation of the
    // original design; no constructor site exists.
    | CnEquiv of Ident * TypeExpr

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
    // OPEN constraint conjuncts: `where <name>(<idents>)` for any name the
    // parser doesn't recognize as a built-in clause keyword. The parser
    // stays grammar-only — it records (name, args) as data; the CHECKER
    // dispatches each conjunct through the Blade.Constraints registry
    // (extension modules register handlers; PPL registers `indep`). An
    // unregistered name is a check-time error listing the registered
    // vocabulary, not a parse error.
    Custom: (Ident * Ident list) list
}

and TDimSpec = {
    Extent: Expr
    Symmetry: int
    Name: string option
}

// ============================================================================
// Patterns
// ============================================================================

and PatternKind =
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

and ExprKind =
    // Literals
    | ExprLit of Literal
    // Wildcard hole `_` in expression position. A general discard/hole token
    // (the expression-position sibling of PatWildcard). Context gives it meaning:
    // as a compound-index coordinate it marks a FREE axis (B((a, _, c))). It is
    // not a value and has no type of its own; contexts that don't interpret it
    // (arbitrary expression position) reject it.
    | ExprWildcard
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
    | ExprRange of TypeExpr list           // range<I> or range<I1, ..., In> (multi-index)
    | ExprDotDot of lo: Expr * hi: Expr  // a..b — anonymous range sugar
    | ExprReverse of TypeExpr              // reverse<I>
    | ExprBlocked of TypeExpr * Expr       // blocked<I, K>
    | ExprHalo of inner: TypeExpr * offsets: Expr  // halo<I, [o..]> — stencil traversal transformer over I (signed ordinal offsets, center = 0)
    // Zip and align
    | ExprZip of Expr list
    | ExprAlign of Expr list * AlignSpec option
    // Stack
    | ExprStack of Expr list
    // Combinators
    | ExprPure of Expr
    | ExprCompute of Expr                  // expr |> compute
    | ExprRead of Expr                     // expr |> read (force a deferred provider read)
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
    | ExprCompound of dense: Expr * mask: Expr // compound(dense, mask) - scatter dense array into a CompoundIdx-typed compact array via a bool mask (formalism 4.5)
    | ExprIntersect of Expr * Expr         // intersect(A, B) - elements in both
    | ExprUnion of Expr * Expr             // union(A, B) - elements in either
    | ExprUnique of array: Expr            // unique(A) - dedup, first-occurrence order
    | ExprContains of array: Expr * value: Expr  // contains(A, x) - is x present in A
    | ExprGroupBy of values: Expr * grouping: Expr  // group_by(vals, gk) - apply grouping to values
    | ExprGroupKeys of keys: Expr list             // group_keys(keys1, keys2, ...) - build CSR grouping structure (compound if >1 key)
    | ExprSort of array: Expr * key: Expr          // sort(A, key) - sort array by key function (stable)
    | ExprReduce of array: Expr * kernel: Expr * init: Expr option  // reduce(A, op[, init]) - fold innermost dim; init seeds the fold and defines the empty-array result
    | ExprTranspose of array: Expr * dim1: int * dim2: int  // transpose(A, [d1, d2]) - swap two arity-1 SymNone axes (hard; allocates)
    | ExprDecompact of array: Expr * dim: int  // decompact(A, d) - pull the compact component at dim d out as a free Idx (hard; allocates dense)
    | ExprGram of left: Expr * right: Expr  // gram(A, B) = A * B^H: result[i][j] = sum_k A[i][k]*conj(B[j][k]). Square+Hermitian/symmetric when A,B same array; dense otherwise.
    | ExprExtents of array: Expr                   // extents(A) - innermost dim extent (rank-1 only for now)
    // Struct construction
    | ExprStruct of Ident * (Ident * Expr) list * spread: Expr option  // Point { x = 1, ..p }
    // Sectioned operators
    | ExprSection of BinOp                 // (+), (*), etc.
    | ExprPartialApp of BinOp * Expr * bool  // (+ 1) or (1 +), bool = is left section
    // Assignment expression (for imperative updates)
    | ExprAssign of lhs: Expr * rhs: Expr
    // For-loop expression (loop object construction)
    | ExprFor of source: ForSource * whereClauses: Constraint list * kernel: Expr option
    // Static former marker: `static method_for/object_for/for (...)` — the
    // wrapped former's ARGUMENT LIST elaborates at compile time. Produced by
    // the parser, consumed and ELIMINATED by the Unfold pass (Unfold.fs)
    // before any elaboration or typechecking; downstream stages never see it.
    | ExprStatic of Expr

/// Every expression carries its source span (full-span AST). Construct via
/// mkExpr / inheritSpan / syn (defined after this type group); match on
/// `e.Kind` with qualified `ExprKind.Case` patterns.
and Expr = { Kind: ExprKind; Span: Span }

/// Every pattern carries its source span. Construct via mkPat.
and Pattern = { Kind: PatternKind; Span: Span }

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
    /// A statement annotated with its source span (audit §3.4). The parser
    /// wraps every block statement in exactly one layer; the type checker
    /// unwraps it to stamp error locations, and consumers that don't care
    /// about locations match via `unwrapStmt` (defined after this group).
    | StmtSpanned of Stmt * Span

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
    // struct (with where-constraint conjuncts; empty = unconstrained)
    | TyDeclStruct of name: Ident * typeParams: Ident list * fields: FieldDecl list * constraints: Expr list
    // mutually constrained aliases: type P1 = T1 and P2 = T2 where c1, c2, ...
    // Members are transparent aliases; the group's conjuncts are checked
    // jointly wherever the members' types are introduced together.
    | TyDeclMutualGroup of members: (Ident * TypeExpr) list * constraints: Expr list

and VariantDecl = {
    Name: Ident
    Data: TypeExpr option  // None for unit variants
}

and FieldDecl = {
    Name: Ident
    Type: TypeExpr
    Default: Expr option
    /// Dependent range refinement: `f: T in lo .. hi` — half-open like every
    /// other `..`, either side optional. Bounds may reference earlier fields
    /// and statics; they desugar into the struct's constraint conjuncts.
    Bound: FieldBound option
}

and FieldBound = {
    Lo: Expr option
    Hi: Expr option
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

/// Strip a statement's StmtSpanned annotation (recursively, defensively —
/// the parser emits exactly one layer). Walkers that don't report locations
/// match on `unwrapStmt stmt` instead of adding a StmtSpanned arm.
let rec unwrapStmt (s: Stmt) : Stmt =
    match s with
    | StmtSpanned (inner, _) -> unwrapStmt inner
    | _ -> s

// ============================================================================
// Span-carrying constructors (full-span AST)
// ============================================================================

let mkExpr (span: Span) (kind: ExprKind) : Expr = { Kind = kind; Span = span }
let mkPat (span: Span) (kind: PatternKind) : Pattern = { Kind = kind; Span = span }

/// Rewriters (Unfold/Grad/StaticEval/elaborators) synthesize nodes from an
/// existing one: the new node inherits the source node's span.
let inheritSpan (src: Expr) (kind: ExprKind) : Expr = { Kind = kind; Span = src.Span }
let inheritPatSpan (src: Pattern) (kind: PatternKind) : Pattern = { Kind = kind; Span = src.Span }

/// Ambient span for synthesized AST: elaborators (ml/ppl/math/rand/spectra/
/// grad) build many nodes on behalf of ONE user declaration. The expansion
/// entry stamps that decl's span here; `syn`/`synPat` read it, so builder
/// helpers stay span-free. Elaboration is single-threaded (typeCheck
/// pipeline), so a plain mutable is safe.
let mutable synthSpan : Span = noSpan
let syn (kind: ExprKind) : Expr = { Kind = kind; Span = synthSpan }
let synPat (kind: PatternKind) : Pattern = { Kind = kind; Span = synthSpan }

/// A struct's FULL constraint-conjunct list: the declared where-conjuncts
/// plus the desugared field range refinements (`f: T in lo .. hi` — `..`
/// is half-open, so `lo <= f` and `f < hi`). ONE definition shared by the
/// type checker (registration + guard synthesis) and the static evaluator
/// (fold-time checks) so the two worlds cannot drift.
let structConjuncts (fields: FieldDecl list) (declared: Expr list) : Expr list =
    let boundConjuncts =
        fields |> List.collect (fun f ->
            match f.Bound with
            | Some b ->
                (b.Lo |> Option.map (fun lo -> inheritSpan lo (ExprBinOp (Elementwise, OpLe, lo, inheritSpan lo (ExprVar f.Name)))) |> Option.toList)
                @ (b.Hi |> Option.map (fun hi -> inheritSpan hi (ExprBinOp (Elementwise, OpLt, inheritSpan hi (ExprVar f.Name), hi))) |> Option.toList)
            | None -> [])
    declared @ boundConjuncts

/// Union of two spans: min start, max end. noSpan is the identity; the
/// filename comes from whichever side has one.
let mergeSpan (a: Span) (b: Span) : Span =
    if a.StartLine = 0 then b
    elif b.StartLine = 0 then a
    else
        let sL, sC =
            if (a.StartLine, a.StartCol) <= (b.StartLine, b.StartCol)
            then a.StartLine, a.StartCol else b.StartLine, b.StartCol
        let eL, eC =
            if (a.EndLine, a.EndCol) >= (b.EndLine, b.EndCol)
            then a.EndLine, a.EndCol else b.EndLine, b.EndCol
        { StartLine = sL; StartCol = sC; EndLine = eL; EndCol = eC
          File = match a.File with Some _ -> a.File | None -> b.File }

