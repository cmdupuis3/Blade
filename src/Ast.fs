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
    /// Bounded primitive (formalism §2.4): `Base<Unit, min=e1, max=e2>`.
    /// The unit/tag POSITIONAL arguments stay on `baseTy` (which is the
    /// ordinary `TyNamed (name, positionalArgs)` node), so unit resolution
    /// and tag resolution are untouched — they apply to `baseTy` exactly as
    /// they did before this case existed. Only the NAMED `min=`/`max=`
    /// arguments live here.
    ///
    /// Bounds are INCLUSIVE on both ends: `min=a, max=b` means
    /// `a <= x && x <= b`. This is deliberately NOT the convention of the
    /// field refinement `f: T in lo .. hi`, which is half-open like every
    /// other `..` in the language; the translation law is
    /// `in lo .. hi` ≡ `min=lo, max=hi-1` for integer fields.
    ///
    /// Invariant: at least one of min/max is `Some` — the parser refuses
    /// to build the all-None form (it would be an unbounded bounded type).
    ///
    /// In STRUCT FIELD position this node never survives parsing: it is
    /// normalized into `FieldDecl.Bound` (with `HiInclusive = true`) so the
    /// struct's conjunct list stays the ONE representation both evaluation
    /// worlds read. It survives only in let / parameter / return annotations.
    | TyBounded of baseTy: TypeExpr * min: Expr option * max: Expr option
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
    // SymIdx<k, base> / AntisymIdx<k, base> — the rank-k symmetric /
    // antisymmetric power of a BASE index space. See `SymIdxBase` for what
    // the second argument may be.
    | TySymIdx of rank: int * baseIdx: SymIdxBase
    | TyAntisymIdx of rank: int * baseIdx: SymIdxBase
    // OrbIdx<[(r1,s1), ..., (rd,sd)], n> -- the flat ITERATED-WREATH class
    // (docs/plan-orbit-index-types.md §2). Levels are OUTERMOST-LAST; each is a
    // (rank, sign) pair with `true` = '+' (invariant) and `false` = '-' (sgn).
    // The empty list `OrbIdx<[], n>` is legal and is the trivial class Idx<n>.
    //
    // The levels are PURE SYNTAX -- integer literals and sign tokens, no Expr --
    // so they are stored as data, not as expressions to evaluate. Only the
    // extent is an expression, and it accepts exactly what `SymIdx<k, _>`'s
    // second argument accepts (SymIdxBase), so `OrbIdx<[(2,+)], n>` and
    // `SymIdx<2, n>` share one extent grammar and one lowering.
    //
    // NORMALIZED AT LOWERING (§7.2, and OrbRank.normalizeLevels is the
    // reference): rank-1 levels drop at EITHER sign; [] becomes the plain Idx
    // record; a single surviving level becomes the SymIdx / AntisymIdx record
    // verbatim. Only depth >= 2 produces the new SymWreath representation.
    | TyOrbIdx of levels: (int * bool) list * baseIdx: SymIdxBase
    | TyBoundedIdx of lower: Expr * upper: Expr
    | TyCompoundIdx of mask: Expr
    // SparseIdx<keys> — explicit valid-tuple enumeration (formalism 3.5).
    // `keys` is a rank-1 array of Nat tuples (edge lists, CG triples); rank is
    // implicit from the tuple arity. Keys keep their GIVEN order (never
    // sorted); lookups go through the tuple hash. Static: the entry set is
    // fixed at construction.
    | TySparseIdx of keys: Expr
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
    // The tag wildcard `_`, legal ONLY as the sole type argument of a numeric
    // base: `Nat<_>`, `Int64<_>`, `Float64<_>`. Reads as "any tag" — the
    // parameter accepts a value carrying any nominal index tag, any unit
    // signature, or none at all. Never appears bare: the parser only produces
    // it from the `Base<_>` two-token lookahead, so it cannot leak into
    // arbitrary type positions (element types, tuple slots, arrows).
    //
    // Lowers to `IRTIdxTagged (base, IRefAny)`; legal in PARAMETER position
    // only, since a wildcard has no tag to hand back to a consumer (see
    // irTypeHasTagWildcard's use sites in TypeCheck).
    | TyWildcard
    // IrrepsIdx<spec> — block-structured dense index over an equivariant-NN
    // irreps spec (a static array of (l, parity, mult) int triples). Extent
    // is total_dim(spec) = Σ mult*(2l+1); every cell is stored (flat dense,
    // no compression) — the spec matters for type IDENTITY, not storage.
    // The spec is an expression resolved at typecheck via StaticEval.
    | TyIrrepsIdx of spec: Expr
    // PgIrrepsIdx<GROUP, spec> — the SECOND block-spec member
    // (retired transforms-as-types plan §3.6, stage 5b-i): a block-structured
    // dense index over a POINT GROUP's labelled irreducible blocks. GROUP is
    // an identifier resolved against the frozen MLPointSpec registry ({C4,
    // D4}) at lowering time — a bare name in type position, exactly as the
    // group reads in the mathematics — and `spec` is a static array of
    // (LABEL_NAME, mult) tuples: LABEL names rather than (l, parity) integers,
    // because a finite group's irreps are NAMED in its character table.
    // Extent is pg_total_dim(spec) = Σ mult*dim_R(label); every cell stored.
    //
    // A DISTINCT former from TyIrrepsIdx on purpose (§3.6's twin-not-reroute):
    // the two carry different payloads, print differently, and must never
    // unify with each other even at equal extent.
    | TyPgIrrepsIdx of group: Ident * spec: Expr
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

/// The second argument of `SymIdx<k, _>` / `AntisymIdx<k, _>`: the BASE index
/// space the k-th symmetric (antisymmetric) power is taken over.
///
///   - `SymBaseExtent e` — the legacy form `SymIdx<2, n>`: the base is an
///     anonymous dense space of extent `e`, an ordinary int expression
///     (literal, `let static` name, parameter, arithmetic). A BARE NAME always
///     reads this way, never as an index-type alias.
///   - `SymBaseIndex ty` — the base is written as an index TYPE
///     (`SymIdx<2, IrrepsIdx<spec>>`, `SymIdx<2, Idx<n>>`). The base's full
///     identity (extent, nominal tag, index kind) is inherited by the
///     symmetric-power record; only Rank and Symmetry are re-stamped.
///     This is what makes `Sym^k` of an irreps space a WRITABLE type
///     (retired transforms-as-types plan §7 stage 3, seam S1).
and SymIdxBase =
    | SymBaseExtent of Expr
    | SymBaseIndex of TypeExpr

and Constraint =
    // The live spellings of these two conjuncts are WhereClause.Commutativity
    // and WhereClause.Antisymmetry (the parser records where-clause conjuncts
    // as data on the record, not as Constraint values); these cases remain as
    // documentation of the original design. No constructor site exists.
    | CnComm of Ident list              // comm(a, b, c)
    | CnAntisymm of Ident list          // anticomm(a, b)
    | CnReynolds of Ident list * bool   // reynolds([a,b], antisym?)
    // equiv(G, rho) — superseded by WhereClause.Custom + the Blade.Constraints
    // registry: `where ml.equiv(O3|SO3)` parses as a Custom conjunct and is
    // judged by ml/compiler/MLEquiv.fs. Retained as documentation of the
    // original design; no constructor site exists.
    | CnEquiv of Ident * TypeExpr

and WhereClause = {
    Commutativity: Ident list list        // comm(a,b), comm(c,d)
    // anticomm(a,b) groups — the SIGNED sibling of Commutativity: the kernel
    // is declared ANTI-invariant under exchange of the listed parameters
    // (f(b,a) = -f(a,b), hence a zero diagonal). Same grouping/iteration
    // license as a comm group, but the licensed output storage is the STRICT
    // simplex (AntisymIdx<r,n>, C(n,r) cells, negate-on-swap reads) instead of
    // the inclusive triangle. This is the pin spelling for a deduced-PNeg
    // kernel (retired implicit-formers-and-deduction plan §4).
    Antisymmetry: Ident list list         // anticomm(a,b)
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
    // Join — concatenate n same-rank arrays along dimension d (formalism §2.6)
    | ExprJoin of arrays: Expr list * dim: int
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
    | ExprSparse of values: Expr * keys: Expr  // sparse(values, keys) - bundle a rank-1 values array (in key order) with an explicit key list into a SparseIdx-typed array (formalism 3.5); no scatter, values are already in key order
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
    // Recursive array definition (structural induction on the leading-axis
    // extent — formalism: arrays are functions, functions recurse). Appears
    // ONLY as the Value of a `let rec NAME : TYPE = match NAME with ...`
    // binding; the parser validates the arm shapes (productivity is
    // syntactic: the inductive arm is literally `prefix :: slice`) and
    // structures them here. The `| zero -> zero` base arm is validated and
    // implied, not stored. Sequentiality is DERIVED (each slice depends on
    // the prefix); lowering compiles the scheme to a serial loop.
    | ExprRecArray of RecArrayDef

/// Every expression carries its source span (full-span AST). Construct via
/// mkExpr / inheritSpan / syn (defined after this type group); match on
/// `e.Kind` with qualified `ExprKind.Case` patterns.
and Expr = { Kind: ExprKind; Span: Span }

/// The structured arms of a recursive-array definition:
///   let rec q: Array<T like Step, ...> = match q with
///   | zero        -> zero                    // required base (extent 0)
///   | zero :: n   -> zero :: SEED            // optional seed (extent 1)
///   | prefix :: n -> prefix :: SLICE         // required inductive arm
/// PrefixVar binds the smaller family, StepVar the new step ordinal (the
/// extent of the prefix). SLICE may read prefix at earlier ordinals
/// (curried leading-axis reads) and use StepVar for step-dependent
/// coefficients. SeedArm, when present, carries its own step-var name
/// (always bound to 0) and the seed slice expression.
and RecArrayDef = {
    Name: Ident
    SeedArm: (Ident * Expr) option
    PrefixVar: Ident
    StepVar: Ident
    SliceExpr: Expr
}

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
    // struct (with where-constraint conjuncts; empty = unconstrained).
    // `isStatic` is the DECLARED static-eligibility fence: `static struct S`
    // requires every field type to be statically evaluable (a StaticValue
    // shape), checked once at declaration instead of inferred at each use.
    // Ordinary structs are untouched and carry `false`.
    | TyDeclStruct of name: Ident * typeParams: Ident list * fields: FieldDecl list * constraints: Expr list * isStatic: bool
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
    /// other `..`, either side optional — OR the bounded-primitive spelling
    /// `f: T<min=lo, max=hi>` (§2.4), which is INCLUSIVE and normalizes into
    /// this same slot at parse time so there is exactly ONE bounds channel.
    /// Bounds may reference earlier fields and statics; they desugar into the
    /// struct's constraint conjuncts.
    Bound: FieldBound option
}

and FieldBound = {
    Lo: Expr option
    Hi: Expr option
    /// Inclusivity of the UPPER endpoint, the one place the two spellings
    /// differ. `false` = `in lo .. hi` (Hi desugars to `f < hi`);
    /// `true` = `max=e` (Hi desugars to `f <= hi`). The LOWER endpoint is
    /// inclusive in both spellings, so there is no `LoInclusive`.
    HiInclusive: bool
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

/// The comparison a field bound's UPPER endpoint desugars to: `<` for the
/// half-open `in lo .. hi` spelling, `<=` for the inclusive `max=` spelling.
/// Total, and half-open is the default arm.
let boundHiOp (b: FieldBound) : BinOp =
    if b.HiInclusive then OpLe else OpLt

/// The conjuncts asserted by a BOUNDED-PRIMITIVE annotation (§2.4) about a
/// subject expression: `[min <= subj; subj <= max]`, INCLUSIVE on both ends.
/// Any other type yields `[]`, so call sites can apply it unconditionally.
/// Struct fields do NOT go through here — their bounds are normalized into
/// `FieldDecl.Bound` at parse time; this serves let / parameter / return
/// annotations, where the surface type is the only carrier.
let boundedConjuncts (subject: Expr) (ty: TypeExpr) : Expr list =
    match ty with
    | TyBounded (_, lo, hi) ->
        (lo |> Option.map (fun l -> inheritSpan l (ExprBinOp (Elementwise, OpLe, l, subject))) |> Option.toList)
        @ (hi |> Option.map (fun h -> inheritSpan h (ExprBinOp (Elementwise, OpLe, subject, h))) |> Option.toList)
    | _ -> []

/// The half-open box `[lo, hi)` of a field, from EITHER bound spelling:
/// `in lo .. hi` is already half-open; `max=b` is inclusive, so the
/// exclusive upper endpoint is `b + 1`. Declared where-conjuncts are NOT
/// consulted — this is the BOX, not the solution set. Used by the
/// constrained-index counting layer, which enumerates the box and filters
/// by the conjuncts.
let fieldBoxBounds (f: FieldDecl) : Expr option * Expr option =
    match f.Bound with
    | None -> (None, None)
    | Some b ->
        let hiExclusive =
            b.Hi |> Option.map (fun hi ->
                if b.HiInclusive then
                    inheritSpan hi (ExprBinOp (Elementwise, OpAdd, hi, inheritSpan hi (ExprLit (LitInt 1L))))
                else hi)
        (b.Lo, hiExclusive)

/// A struct's FULL constraint-conjunct list: the declared where-conjuncts
/// plus the desugared field range refinements (`f: T in lo .. hi` — `..`
/// is half-open, so `lo <= f` and `f < hi`; `f: T<min=a, max=b>` is
/// inclusive, so `a <= f` and `f <= b`). ONE definition shared by the
/// type checker (registration + guard synthesis) and the static evaluator
/// (fold-time checks) so the two worlds cannot drift.
///
/// ORDER IS A PINNED CONTRACT (diagnostics number conjuncts 1-based in this
/// order): declared conjuncts first in written order, then bound conjuncts
/// in field-declaration order, Lo before Hi.
///
/// A declared conjunct written as a LITERAL tuple or LITERAL array flattens
/// IN PLACE to its elements, recursively: `where (p1, p2)` and
/// `where [p1, p2]` are accepted spellings of `where p1, p2`. This is a
/// strict widening — a tuple/array-valued conjunct cannot fold to a boolean
/// in either world today, so it is an error case being turned into an
/// acceptance, never a meaning change. Only LITERALS flatten; a non-literal
/// expression of array type must still fold to a boolean like any other
/// conjunct (no all-true semantics is introduced anywhere).
let structConjuncts (fields: FieldDecl list) (declared: Expr list) : Expr list =
    let rec flatten (e: Expr) : Expr list =
        match e.Kind with
        | ExprTuple elems | ExprArrayLit elems -> elems |> List.collect flatten
        | _ -> [e]
    let declared = declared |> List.collect flatten
    let boundConjuncts =
        fields |> List.collect (fun f ->
            match f.Bound with
            | Some b ->
                (b.Lo |> Option.map (fun lo -> inheritSpan lo (ExprBinOp (Elementwise, OpLe, lo, inheritSpan lo (ExprVar f.Name)))) |> Option.toList)
                @ (b.Hi |> Option.map (fun hi -> inheritSpan hi (ExprBinOp (Elementwise, boundHiOp b, inheritSpan hi (ExprVar f.Name), hi))) |> Option.toList)
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

