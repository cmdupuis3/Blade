module Blade.StaticEval

open Blade.Ast

// Static Value Types

/// Compile-time evaluated values
type StaticValue =
    | SVInt of int64
    | SVFloat of float
    | SVBool of bool
    | SVString of string
    | SVUnit
    | SVTuple of StaticValue list
    /// A folded struct literal: the type name plus (field, value) pairs in
    /// DECLARATION order (when the struct registry is in scope). Keeping
    /// the name and field names lets splice-back emit a designated struct
    /// literal instead of a tuple, so runtime field access on a
    /// `let static` struct stays well-typed.
    | SVStruct of name: string * fields: (string * StaticValue) list
    /// Float32 / Int32 values DURING a fold (round 6): the width-correct
    /// results of float32 operations (rounded after every operation) and of
    /// 32-bit wrapping integer operations, so a static agrees bit for bit with
    /// the same expression evaluated at run time. They never leave the
    /// evaluator: `evalExpr` / `resolveStatics` NORMALIZE them to SVFloat /
    /// SVInt (the value is already rounded / wrapped), which is all every
    /// consumer reads; the width survives in `StaticEnv.Widths`.
    | SVFloat32 of float32
    | SVInt32 of int32

/// A static function definition (unevaluated -- applied during evaluation)
type StaticFuncDef = {
    Name: string
    Params: string list
    Body: Expr
}

/// Struct constraint info for fold-time checks: field names in declaration
/// order plus the FULL conjunct list (declared where-conjuncts + desugared
/// field bounds, built with Ast.structConjuncts, the same helper the type
/// checker uses, so the two worlds cannot drift).
type StructStaticInfo = {
    Fields: string list
    Conjuncts: Expr list
    /// Declaration-order field declarations, retained verbatim: the index
    /// fence needs field TYPES and the raw bound expressions, neither of
    /// which survives into the flattened conjunct list.
    FieldDecls: FieldDecl list
    /// The DECLARED where-conjuncts alone (no desugared field bounds). The
    /// enumeration reading runs over a box that already enforces the
    /// bounds, so folding them again at every cell is pure cost; keeping
    /// the two halves separate lets the fence hand out the residual list
    /// without re-deriving it (`Conjuncts = structConjuncts FieldDecls
    /// Declared` is an invariant, pinned by Test_StructIdxFence).
    Declared: Expr list
    /// `static struct Name { ... }` -- the declared static-eligibility
    /// fence, irrelevant to folding; read by the index-type fence, for
    /// which it is the FIRST eligibility question.
    IsStatic: bool
}

/// Environment for static evaluation
type StaticEnv = {
    Values: Map<string, StaticValue>
    /// The MODULE-LEVEL static values -- the lexical environment every
    /// `static function` body closes over. `Values` grows with call-frame
    /// parameters and block-local lets as evaluation descends; a static
    /// function's body must NOT see those (it was declared at module scope),
    /// so a call evaluates its body in `Globals` plus its own parameters.
    /// Before this split a callee read the CALLER's bindings: `static
    /// function f() = a` invoked from `static function g(a) = f()` returned
    /// g's argument at compile time and the module's `a` at runtime.
    /// `resolveStatics` keeps it equal to the top-level `Values` as statics
    /// fold; every other constructor seeds it with its `Values`.
    Globals: Map<string, StaticValue>
    Functions: Map<string, StaticFuncDef>
    /// Accumulates names of functions called during evaluation
    CalledFunctions: ref<Set<string>>
    /// Provider-backed roots in scope: binding name (`sample` from
    /// `let sample = nc.load("f.nc")`) -> (provider module name, store
    /// path). Consulted by the provider-read fold: a closed input is an
    /// argument the program was applied to, so a `let static` read may
    /// fold its payload at compile time.
    ProviderRoots: Map<string, string * string>
    /// Constrained-struct registry for fold-time conjunct checks. Empty in
    /// contexts that never fold user struct literals (angle-bracket args).
    Structs: Map<string, StructStaticInfo>
    /// `type A = Chunked<I, K>` declarations in scope, by alias: the run
    /// boundaries `[0; ..; N]` (docs/plans/structural/07, decision D2:
    /// `segments(A)` is statically evaluable). Literal `Idx<n>` inners with
    /// a literal edge only; anything else is absent here and stays a
    /// runtime grouping.
    Segments: Map<string, int64 list>
    /// The narrow width of each static NAME whose folded value is a Float32
    /// or an Int32 (round 6). `Values` holds the normalized SVFloat / SVInt;
    /// a read of the name re-narrows it, so a later static computes at that
    /// width (`let static sw = si + 1` wraps when `si` is an Int32). Empty
    /// outside `resolveStatics` (a consumer that rebuilds an env from folded
    /// values sees the wide spelling of the same value).
    Widths: Map<string, Blade.Types.ElemType>
}

/// Normalize a fold result for consumers: Float32 / Int32 values as the
/// SVFloat / SVInt of the same (rounded / wrapped) value, recursively.
let rec normalizeStatic (v: StaticValue) : StaticValue =
    match v with
    | SVFloat32 f -> SVFloat (float f)
    | SVInt32 n -> SVInt (int64 n)
    | SVTuple vs -> SVTuple (vs |> List.map normalizeStatic)
    | SVStruct (n, fs) -> SVStruct (n, fs |> List.map (fun (k, x) -> (k, normalizeStatic x)))
    | other -> other

/// The narrow width a folded scalar carries (Float32 / Int32), if any.
let narrowWidthOf (v: StaticValue) : Blade.Types.ElemType option =
    match v with
    | SVFloat32 _ -> Some Blade.Types.ETFloat32
    | SVInt32 _ -> Some Blade.Types.ETInt32
    | _ -> None

/// A literal's DEFAULT width when nothing adapts it (round 6 rule 4): the
/// checker's one table, `Blade.Types.LiteralDefaults`, so the static fold and
/// the checker cannot disagree.
let private litIntDefault (n: int64) : Blade.Types.ElemType = Blade.Types.LiteralDefaults.intDefault n
let private litFloatDefault (f: float) : Blade.Types.ElemType = Blade.Types.LiteralDefaults.floatDefault f

let private litMathNames : Set<string> =
    Set.union Blade.GradCommon.mathIntrinsics (set [ "abs"; "atan2"; "log_base"; "complex" ])

/// THE literal-only (LO) predicate on the SURFACE -- the one every surface
/// reader shares (the static fold here, the checker's pushes
/// TypeCheckSupport.surfaceLiteralClass, the defaulted-literal report): numeric
/// literals, unary minus, `+ - * / % ^` and atan2 / log_base, the real math
/// intrinsics and `abs`, and `complex(..)`, over literal-only operands -- the
/// subtree that adapts AS A WHOLE to the width its context fixes (round 6
/// rule 5). Its TYPED twin is TypeCheckSupport.isLiteralOnly (the same shapes
/// on the checked tree).
let rec isLitOnly (e: Expr) : bool =
    match e.Kind with
    | ExprKind.ExprLit (LitInt _ | LitFloat _) -> true
    | ExprKind.ExprUnaryOp (OpNeg, x) -> isLitOnly x
    | ExprKind.ExprBinOp (Elementwise, (OpAdd | OpSub | OpMul | OpDiv | OpMod | OpCaret | OpMath2 _), l, r) ->
        isLitOnly l && isLitOnly r
    | ExprKind.ExprApp ({ Kind = ExprKind.ExprVar f }, args) when not args.IsEmpty && litMathNames.Contains f ->
        args |> List.forall isLitOnly
    | _ -> false

/// The default (float, int) widths of a literal-only subtree: per class the
/// WIDER of its leaves' defaults (Float64 / Int64 when a class is absent --
/// irrelevant then).
let rec private litDefaultWidths (e: Expr) : Blade.Types.ElemType * Blade.Types.ElemType =
    let mutable f32 = true
    let mutable i32 = true
    let rec walk (e: Expr) =
        match e.Kind with
        | ExprKind.ExprLit (LitInt n) -> if litIntDefault n <> Blade.Types.ETInt32 then i32 <- false
        | ExprKind.ExprLit (LitFloat x) -> if litFloatDefault x <> Blade.Types.ETFloat32 then f32 <- false
        | ExprKind.ExprUnaryOp (_, x) -> walk x
        | ExprKind.ExprBinOp (_, _, l, r) -> walk l; walk r
        | ExprKind.ExprApp (_, args) -> List.iter walk args
        | _ -> ()
    walk e
    // BY VALUE (lead ruling, round 6): a literal-only tree whose EXACT value
    // does not fit its default width is widened -- `2 ^ 40` and
    // `1000000 * 3000` are Int64 (exact integer arithmetic, then the Int32 fit
    // test); a float tree whose double value is past FLT_MAX, or rounds a
    // nonzero value to zero or a subnormal, is Float64. The checker's twin is
    // TypeCheckSupport.widenLiteralOnlyByValue.
    if i32 then
        match exactIntValue e with
        | Some v when v < System.Numerics.BigInteger(System.Int32.MinValue)
                      || v > System.Numerics.BigInteger(System.Int32.MaxValue) -> i32 <- false
        | _ -> ()
    if f32 then
        match approxFloatValue e with
        | Some x when System.Double.IsFinite x && x <> 0.0 ->
            let ax = abs x
            if ax > float System.Single.MaxValue || abs (float32 x) < 1.17549435e-38f then f32 <- false
        | _ -> ()
    ((if f32 then Blade.Types.ETFloat32 else Blade.Types.ETFloat64),
     (if i32 then Blade.Types.ETInt32 else Blade.Types.ETInt64))

/// The EXACT value of a purely-integer literal-only tree (+ - * / % ^ and
/// unary minus over integer literals), or None (a float leaf, an intrinsic, a
/// zero divisor, a negative or huge exponent).
and private exactIntValue (e: Expr) : System.Numerics.BigInteger option =
    let big (n: int64) = System.Numerics.BigInteger(n)
    match e.Kind with
    | ExprKind.ExprLit (LitInt n) -> Some (big n)
    | ExprKind.ExprUnaryOp (OpNeg, x) -> exactIntValue x |> Option.map (fun v -> -v)
    | ExprKind.ExprBinOp (_, op, l, r) ->
        (match exactIntValue l, exactIntValue r with
         | Some a, Some b ->
             (match op with
              | OpAdd -> Some (a + b)
              | OpSub -> Some (a - b)
              | OpMul -> Some (a * b)
              | OpDiv when not b.IsZero -> Some (System.Numerics.BigInteger.Divide(a, b))
              | OpMod when not b.IsZero -> Some (System.Numerics.BigInteger.Remainder(a, b))
              | OpCaret when b.Sign >= 0 && b <= big 4096L -> Some (System.Numerics.BigInteger.Pow(a, int b))
              | _ -> None)
         | _ -> None)
    | _ -> None

/// The double value of a literal-only tree holding a float, for the Float32
/// range test only (None when it has no float leaf or does not evaluate).
and private approxFloatValue (e: Expr) : float option =
    let rec hasFloat (e: Expr) =
        match e.Kind with
        | ExprKind.ExprLit (LitFloat _) -> true
        | ExprKind.ExprUnaryOp (_, x) -> hasFloat x
        | ExprKind.ExprBinOp (_, _, l, r) -> hasFloat l || hasFloat r
        | ExprKind.ExprApp (_, args) -> List.exists hasFloat args
        | _ -> false
    let rec value (e: Expr) : float option =
        match e.Kind with
        | ExprKind.ExprLit (LitFloat f) -> Some f
        | ExprKind.ExprLit (LitInt n) -> Some (float n)
        | ExprKind.ExprUnaryOp (OpNeg, x) -> value x |> Option.map (fun v -> -v)
        | ExprKind.ExprBinOp (_, op, l, r) ->
            (match value l, value r with
             | Some a, Some b ->
                 (match op with
                  | OpAdd -> Some (a + b)
                  | OpSub -> Some (a - b)
                  | OpMul -> Some (a * b)
                  | OpDiv -> Some (a / b)
                  | OpCaret -> Some (System.Math.Pow (a, b))
                  | _ -> None)
             | _ -> None)
        | _ -> None
    if hasFloat e then value e else None

// Dependency Analysis

/// Collect all free variable names referenced in an expression.
/// Does NOT descend into type annotations (those are handled in Phase 4).
let rec collectFreeNames (expr: Expr) : Set<string> =
    match expr.Kind with
    | ExprKind.ExprLit _ -> Set.empty
    | ExprKind.ExprVar name -> Set.singleton name
    | ExprKind.ExprBinOp (_, _, l, r) -> Set.union (collectFreeNames l) (collectFreeNames r)
    | ExprKind.ExprUnaryOp (_, e) -> collectFreeNames e
    | ExprKind.ExprApp (f, args) ->
        Set.union (collectFreeNames f) (args |> List.map collectFreeNames |> Set.unionMany)
    | ExprKind.ExprIf (c, t, e) ->
        [c; t; e] |> List.map collectFreeNames |> Set.unionMany
    | ExprKind.ExprTuple es | ExprKind.ExprArrayLit es ->
        es |> List.map collectFreeNames |> Set.unionMany
    | ExprKind.ExprField (obj, _) -> collectFreeNames obj
    | ExprKind.ExprLet (binding, body) ->
        let valRefs = collectFreeNames binding.Value
        let boundName = match binding.Pattern.Kind with PatternKind.PatVar n -> Set.singleton n | _ -> Set.empty
        Set.union valRefs (Set.difference (collectFreeNames body) boundName)
    | ExprKind.ExprMatch (scrut, cases) ->
        let scrutRefs = collectFreeNames scrut
        let caseRefs = cases |> List.map (fun c ->
            let patBinds = collectPatternBindings c.Pattern
            let guardRefs = c.Guard |> Option.map collectFreeNames |> Option.defaultValue Set.empty
            let bodyRefs = collectFreeNames c.Body
            Set.union guardRefs (Set.difference bodyRefs patBinds)) |> Set.unionMany
        Set.union scrutRefs caseRefs
    | ExprKind.ExprBlock (stmts, finalExpr) ->
        let stmtRefs = stmts |> List.map collectStmtNames |> Set.unionMany
        let finalRefs = finalExpr |> Option.map collectFreeNames |> Option.defaultValue Set.empty
        Set.union stmtRefs finalRefs
    | ExprKind.ExprStruct (_, fields, spread) ->
        let spreadRefs = spread |> Option.map collectFreeNames |> Option.defaultValue Set.empty
        Set.union spreadRefs (fields |> List.map (snd >> collectFreeNames) |> Set.unionMany)
    | ExprKind.ExprTyped (e, _) -> collectFreeNames e
    | ExprKind.ExprLambda (_, _, body) -> collectFreeNames body  // params are local
    | _ -> Set.empty  // conservative for loop/combinator forms

and collectPatternBindings (pat: Pattern) : Set<string> =
    match pat.Kind with
    | PatternKind.PatVar name -> Set.singleton name
    | PatternKind.PatTuple pats -> pats |> List.map collectPatternBindings |> Set.unionMany
    | PatternKind.PatVariant (_, Some p) -> collectPatternBindings p
    | PatternKind.PatStruct (_, fields) -> fields |> List.map (snd >> collectPatternBindings) |> Set.unionMany
    | PatternKind.PatGuarded (p, _) -> collectPatternBindings p
    | PatternKind.PatTyped (p, _) -> collectPatternBindings p
    | _ -> Set.empty

and collectStmtNames (stmt: Stmt) : Set<string> =
    match stmt with
    | StmtSpanned (inner, _) -> collectStmtNames inner
    | StmtLet binding -> collectFreeNames binding.Value
    | StmtAssign (lhs, _, rhs) -> Set.union (collectFreeNames lhs) (collectFreeNames rhs)
    | StmtExpr e -> collectFreeNames e
    | StmtForIn (_, range, body) ->
        Set.union (collectFreeNames range) (body |> List.map collectStmtNames |> Set.unionMany)

/// Struct TYPE names occurring as LITERALS (`R { ... }`) anywhere in an
/// expression.
///
/// `collectFreeNames` deliberately does not report these, and must not
/// start: a struct name is not a variable reference, and surfacing it there
/// would make the checker's field-bound scope check reject a bound that
/// merely mentions a struct. But the static dependency graph genuinely
/// needs them -- CONSTRUCTING `R { ... }` runs R's conjuncts, which may
/// name statics the literal itself never mentions. This is the
/// construction-reading twin of the mention edge in Phase 2 below (that one
/// keys off `ExprVar`; an `ExprStruct` node's name is not one, so neither
/// covers the other).
///
/// Conservative by construction: a form this walker misses simply yields no
/// edge, the pre-existing behavior, never a worse one.
let rec collectStructLitNames (expr: Expr) : Set<string> =
    let u (xs: Set<string> list) = xs |> List.fold Set.union Set.empty
    let opt f o = o |> Option.map f |> Option.defaultValue Set.empty
    match expr.Kind with
    | ExprKind.ExprStruct (name, fields, spread) ->
        u [ Set.singleton name
            fields |> List.map (snd >> collectStructLitNames) |> u
            opt collectStructLitNames spread ]
    | ExprKind.ExprBinOp (_, _, l, r) -> Set.union (collectStructLitNames l) (collectStructLitNames r)
    | ExprKind.ExprUnaryOp (_, e) -> collectStructLitNames e
    | ExprKind.ExprApp (f, args) -> u (collectStructLitNames f :: (args |> List.map collectStructLitNames))
    | ExprKind.ExprIf (c, t, e) -> u [collectStructLitNames c; collectStructLitNames t; collectStructLitNames e]
    | ExprKind.ExprTuple es | ExprKind.ExprArrayLit es -> es |> List.map collectStructLitNames |> u
    | ExprKind.ExprField (o, _) -> collectStructLitNames o
    | ExprKind.ExprLet (b, body) -> Set.union (collectStructLitNames b.Value) (collectStructLitNames body)
    | ExprKind.ExprMatch (s, cases) ->
        u (collectStructLitNames s
           :: (cases |> List.collect (fun c ->
                   [ collectStructLitNames c.Body; opt collectStructLitNames c.Guard ])))
    | ExprKind.ExprBlock (stmts, fin) ->
        u ((stmts |> List.map collectStructLitNamesStmt) @ [ opt collectStructLitNames fin ])
    | ExprKind.ExprTyped (e, _) -> collectStructLitNames e
    | ExprKind.ExprLambda (_, _, body) -> collectStructLitNames body
    | _ -> Set.empty

and collectStructLitNamesStmt (stmt: Stmt) : Set<string> =
    match stmt with
    | StmtSpanned (inner, _) -> collectStructLitNamesStmt inner
    | StmtLet b -> collectStructLitNames b.Value
    | StmtAssign (l, _, r) -> Set.union (collectStructLitNames l) (collectStructLitNames r)
    | StmtExpr e -> collectStructLitNames e
    | StmtForIn (_, range, body) ->
        (collectStructLitNames range :: (body |> List.map collectStructLitNamesStmt))
        |> List.fold Set.union Set.empty

/// Topological sort: given a map of name -> dependencies, return an
/// evaluation order. Returns Error with cycle members if a cycle exists.
let topoSort (deps: Map<string, Set<string>>) : Result<string list, string list> =
    let mutable result = []
    let mutable remaining = deps

    let mutable changed = true
    while changed && not remaining.IsEmpty do
        changed <- false
        // Find all nodes whose dependencies are fully resolved (not in remaining)
        let ready =
            remaining |> Map.filter (fun _ depSet ->
                depSet |> Set.forall (fun d -> not (Map.containsKey d remaining)))
        if not (Map.isEmpty ready) then
            changed <- true
            for KeyValue(name, _) in ready do
                result <- result @ [name]
                remaining <- Map.remove name remaining

    if remaining.IsEmpty then Ok result
    else Error (remaining |> Map.toList |> List.map fst)

// External builtin registry

/// Extension point: domain layers register additional static builtins here
/// (name -> evaluated args -> result). The evaluator consults the registry
/// only after its own builtin table misses, so core names cannot be
/// overridden. Current registrant: the ML module's sizing builtins.
let private externalBuiltins =
    System.Collections.Concurrent.ConcurrentDictionary<string, StaticValue list -> Result<StaticValue, string>>()

/// Register (idempotently -- last write wins) an external static builtin.
let registerStaticBuiltin (name: string) (f: StaticValue list -> Result<StaticValue, string>) =
    externalBuiltins.[name] <- f

/// Extension point: static builtins whose arguments must NOT be evaluated --
/// the argument NAMES A DECLARATION rather than denoting a value.
/// `idx_card(R)` is the first: R is a struct type name, so the
/// evaluate-args-first path above would fold it to "undefined variable"
/// before the builtin ever ran. The handler receives the environment, the
/// REMAINING STEP COUNT of the fold that reached it, and the raw argument
/// expressions, and may call `evalExpr` on whichever it wants evaluated.
///
/// A handler that re-enters `evalExpr` starts a FRESH budget, passed by
/// value, so it inherits neither the caller's pool nor its depth -- a
/// syntactic builtin is a compiler subroutine with its own cost model, and
/// also a LOADED GUN: one reachable from the declaration it is reading can
/// recur forever with the depth counter reset at every hop, unseen by
/// either guard below. Such a builtin owns its own cycle detection
/// (StructIdxSpec's enumeration-in-progress set is the worked example).
///
/// Consulted BEFORE the user's static functions and the evaluated-args
/// path, so a registered syntactic name is reserved; registrants are core
/// layers, not user code.
let private syntacticBuiltins =
    System.Collections.Concurrent.ConcurrentDictionary<string, StaticEnv -> int -> Expr list -> Result<StaticValue, string>>()

/// Register (idempotently -- last write wins) a static builtin that takes
/// its arguments UNEVALUATED.
let registerSyntacticStaticBuiltin (name: string) (f: StaticEnv -> int -> Expr list -> Result<StaticValue, string>) =
    syntacticBuiltins.[name] <- f

let internal trySyntacticBuiltin (name: string) =
    match syntacticBuiltins.TryGetValue name with
    | true, f -> Some f
    | _ -> None

/// Names the static evaluator can call: the core builtin table (must match
/// evalBuiltin's arms) plus everything in the external and syntactic
/// registries. Used by constraint validation to reject calls that could never
/// fold.
let knownBuiltinNames () : Set<string> =
    let core =
        [ "exp"; "log"; "log10"; "sqrt"; "sin"; "cos"; "tan"
          "sinh"; "cosh"; "tanh"; "asin"; "acos"; "atan"
          "floor"; "ceil"; "atan2"; "log_base"; "fma"
          "abs"; "min"; "max"; "length"; "prodsum" ]
    Set.unionMany [ Set.ofList core
                    externalBuiltins.Keys |> Set.ofSeq
                    syntacticBuiltins.Keys |> Set.ofSeq ]

/// Extension point: the provider layer registers its compile-time DATA
/// reader here ((providerName, storePath, varName) -> folded value); see
/// ProviderStatics.install. Kept behind a hook so this module stays free of
/// provider/IR dependencies. When absent or failing, a
/// `let static ... |> alias.read` fails the fold assertion with the
/// reader's message.
let mutable private providerReader : (string -> string -> string -> Result<StaticValue, string>) option = None

let registerProviderReader (f: string -> string -> string -> Result<StaticValue, string>) =
    providerReader <- Some f

/// Extension point: the set of registered provider MODULE names ("netcdf",
/// "zarr", ...), used by resolveStatics to recognize provider imports
/// (`import netcdf as nc`) without referencing the provider registry here.
let mutable private providerModuleNames : Set<string> = Set.empty

let registerProviderNames (names: Set<string>) =
    providerModuleNames <- names

let isProviderModuleName (name: string) : bool =
    Set.contains name providerModuleNames

/// Extension point: the provider layer registers its compile-time AXIS
/// reader here ((providerName, storePath, rootBinding, dimName) -> static
/// extent); see ProviderStatics.install.
let mutable private providerIndexReader : (string -> string -> string -> string -> int option) option = None

let registerProviderIndexReader (f: string -> string -> string -> string -> int option) =
    providerIndexReader <- Some f

/// Resolve a provider axis type (`store.index.y`, which the parser hands
/// over as ONE name carrying the dotted path) to its extent.
///
/// The module elaborations (spectra/math/sgs/ppl) need these extents to
/// specialize their generated code, and they run BEFORE type checking
/// (where TypeEnv registers the provider's axis types), so they resolve
/// the path here instead, through the same (root -> provider, store) map
/// the payload fold uses. None when the name is not such a path, its root
/// is not a provider binding, no reader is installed, or the store has no
/// such dim -- callers then report their ordinary "extent not statically
/// known" steer.
let providerIndexExtent (env: StaticEnv) (name: string) : int option =
    match providerIndexReader with
    | None -> None
    | Some reader ->
        let marker = ".index."
        let i = name.IndexOf marker
        if i <= 0 then None
        else
            let root = name.Substring(0, i)
            let dim = name.Substring(i + marker.Length)
            if dim = "" || dim.Contains "." then None
            else
                match Map.tryFind root env.ProviderRoots with
                | Some (provider, path) -> reader provider path root dim
                | None -> None

// Expression Evaluator

/// A fold budget: TWO numbers, because a fold has two ways to run away and
/// neither bound implies the other.
///   Steps -- total node visits in one top-level fold, SHARED by sibling
///            subexpressions: the WORK bound. A depth bound alone does not
///            give you one -- `f(n) = g(n-1) + g(n-1)` does 2^depth work
///            inside any depth limit.
///   Depth -- maximum nesting of the evaluator's own recursion: the
///            SURVIVAL bound. A step bound alone does not give you one
///            either, since the counter cannot fire before the .NET stack
///            does unless something separately bounds nesting.
type Budget = {
    Steps: int
    Depth: int
}

/// Live state of ONE top-level fold. `Left` is mutable so that sibling
/// subexpressions draw from a single pool rather than each inheriting a
/// copy of the parent's remainder -- the whole difference between a step
/// budget and a depth budget wearing its name.
type private Fuel = {
    mutable Left: int
    MaxDepth: int
}

/// Total node visits one ordinary `let static` fold may take.
///
/// THE NAME USED TO LIE: this number used to be threaded as `fuel - 1` into
/// every CHILD of a node, so both operands of a `+` received the same
/// `fuel - 1` from their parent -- it bounded evaluation DEPTH while being
/// documented as a step count. 100,000 nested `evalExpr` frames exhaust even
/// the 64 MB stack every compiler entry point runs on
/// (`Runtime.largeStackBytes`), so the "step limit exceeded" error was
/// unreachable for exactly the input it was written for: `static function
/// bomb(n: Int) -> Int = bomb(n + 1)` killed the compiler process with an
/// uncatchable StackOverflowException instead of diagnosing. It is now a
/// genuine step count, charged once per node visit, and `maxDepth` is what
/// keeps the process alive long enough for it to matter.
let maxSteps = 100_000

/// Maximum nesting of the evaluator's recursion.
///
/// SIZED AGAINST THE STACK: one evaluation level costs well under 2 KB
/// across its `Result.bind` closures, so 4,096 levels is a few MB against
/// the 64 MB stack, two orders of magnitude of headroom. Lowering that
/// thread's stack size without lowering this re-arms the crash described
/// above.
///
/// A ceiling on NESTING, not on how many times a static function may
/// recurse in total: a static call costs one level, so it admits ~4,000
/// nested static calls -- far past anything a compile-time constant needs.
let maxDepth = 4_096

/// Ordinary `let static` folding: the whole budget, for one declaration.
let defaultBudget = { Steps = maxSteps; Depth = maxDepth }

/// A REAL base `^` an INTEGER exponent (docs/formalism.md section 2.4): the
/// exponent of `^` is polymorphic, and an integer one keeps the base's type and
/// is computed by REPEATED SQUARING -- libgcc's __powidf2 algorithm over a
/// 64-bit exponent: the product starts at `x` for an odd exponent (else 1.0),
/// each later set bit multiplies in the running square, and a negative
/// exponent is the reciprocal of the positive power. Only IEEE multiplies and
/// one divide, in a fixed order, so this compile-time fold IS the run-time
/// value. The ONE definition every F# lane runs (the interpreter's
/// Numerics.realPowInt calls it); its C twins are blade_arith::fpowi
/// (src/cpp/blade_runtime.hpp) and blade_fpowi (src/cpp/blade_llvm_shim.c).
let floatPowInt (x0: float) (e: int64) : float =
    let mutable n = if e < 0L then 0UL - uint64 e else uint64 e
    let mutable x = x0
    let mutable y = if n &&& 1UL <> 0UL then x0 else 1.0
    n <- n >>> 1
    while n <> 0UL do
        x <- x * x
        if n &&& 1UL <> 0UL then y <- y * x
        n <- n >>> 1
    if e < 0L then 1.0 / y else y

/// The constrained-index counting layer's PER-CELL budget, spent afresh on
/// every conjunct at every box cell (StructIdxFence.evalConjunctsAtCell).
///
/// Much smaller than the default ON PURPOSE, by cost model, not caution: a
/// cell predicate is a boolean over a handful of already-bound integer
/// fields, a few dozen nodes at the outside, so 10,000 steps is already
/// three orders of magnitude of slack, while the default budget would let
/// one pathological conjunct do 100,000 steps' work at each of up to
/// `StructIdxSpec.maxBoxCells` cells. Depth 512 is likewise far past any
/// real conjunct and well under the stack.
let cellBudget = { Steps = 10_000; Depth = 512 }

/// PPL license conjuncts (`__ppl_indep(...)`) are static LICENSES, not
/// value predicates -- present only at the pre-elaborator Unfold call site
/// and never denoting a truth about field values. Both readings of a
/// struct's conjunct list skip them (the CONSTRUCTION reading below, and
/// the ENUMERATION reading in StructIdxFence), one definition, so a
/// licensed struct cannot be constructible in one world and empty in the
/// other.
let isPplLicenseConjunct (c: Expr) : bool =
    match c.Kind with
    | ExprKind.ExprApp ({ Kind = ExprKind.ExprVar f }, _) -> f.StartsWith "__ppl_"
    | _ -> false

/// Fold a provider read's operand (`root.vars.A` / `root.dims.x`) through
/// the registered compile-time reader. Shared by the qualified-application
/// form (`alias.read(inner)`) and the legacy ExprRead node.
let private foldProviderRead (env: StaticEnv) (inner: Expr) : Result<StaticValue, string> =
    let resolved =
        match inner.Kind with
        | ExprKind.ExprField ({ Kind = ExprKind.ExprField ({ Kind = ExprKind.ExprVar root }, _) }, varName)
        | ExprKind.ExprField ({ Kind = ExprKind.ExprVar root }, varName) ->
            Map.tryFind root env.ProviderRoots
            |> Option.map (fun (provider, path) -> (provider, path, varName))
        | _ -> None
    match resolved, providerReader with
    | Some (provider, path, varName), Some reader -> reader provider path varName
    | Some _, None ->
        Error "Static evaluation: no compile-time provider reader is installed (provider data folds need the provider's runtime loadable by the compiler)"
    | None, _ ->
        Error "Static evaluation: `alias.read(...)` folds only over a provider-backed variable (root.vars.<name> where root = alias.load(\"store\"))"

/// The evaluator proper. `depth` is the nesting level of THIS node; every
/// child is visited at `depth + 1`, and every visit costs one step out of
/// the shared pool. Both guards are checked before the node is looked at,
/// so a runaway is refused rather than half-evaluated.
let rec private evalCore (env: StaticEnv) (fuel: Fuel) (depth: int) (expr: Expr) : Result<StaticValue, string> =
    if depth > fuel.MaxDepth then
        Error $"Static evaluation: nesting depth limit exceeded ({fuel.MaxDepth} levels -- possible infinite recursion)"
    elif fuel.Left <= 0 then
        Error "Static evaluation: step limit exceeded (possible infinite recursion)"
    else
    fuel.Left <- fuel.Left - 1
    match expr.Kind with
    // A LITERAL-ONLY subtree (literals, unary -, + - * / % ^, math
    // intrinsics over literal-only arguments) reached with nothing to adapt
    // it: each class at the WIDER of its leaves' defaults (round 6: two
    // literal-only operands join to the wider default), then evaluated at
    // that width throughout -- `0.1 * 3.0` under a Float32 default is two
    // float32 literals and a float32 product.
    | _ when isLitOnly expr ->
        let (fw, iw) = litDefaultWidths expr
        evalLitTree env fuel depth fw iw expr
    | ExprKind.ExprLit (LitBool b) -> Ok (SVBool b)
    | ExprKind.ExprLit (LitString s) -> Ok (SVString s)
    | ExprKind.ExprLit LitUnit -> Ok SVUnit

    | ExprKind.ExprVar name ->
        match Map.tryFind name env.Values with
        | Some v ->
            // A Float32 / Int32 static reads back at its width.
            (match Map.tryFind name env.Widths, v with
             | Some Blade.Types.ETFloat32, SVFloat f -> Ok (SVFloat32 (float32 f))
             | Some Blade.Types.ETInt32, SVInt n -> Ok (SVInt32 (int32 n))
             | _ -> Ok v)
        | None ->
            // Could be a static function used as a value (shouldn't happen normally)
            Error $"Static evaluation: undefined variable '{name}'"

    // `&&` / `||` SHORT-CIRCUIT, exactly as the runtime does (the interpreter's
    // IRAnd/IROr arms and C++ `&&`/`||`): the right operand is not visited
    // when the left one decides. Evaluating both meant `false && (1 / 0 == 1)`
    // was a static division-by-zero error while the same text at runtime is
    // simply `false` -- a guard changed meaning by being moved into a
    // `let static`.
    | ExprKind.ExprBinOp (_, OpAnd, l, r) ->
        evalCore env fuel (depth + 1) l |> Result.bind (fun lv ->
            match lv with
            | SVBool false -> Ok (SVBool false)
            | SVBool true ->
                evalCore env fuel (depth + 1) r |> Result.bind (fun rv -> evalBinOp OpAnd lv rv)
            | _ -> Error (sprintf "Static evaluation: cannot apply %A to %A" OpAnd lv))
    | ExprKind.ExprBinOp (_, OpOr, l, r) ->
        evalCore env fuel (depth + 1) l |> Result.bind (fun lv ->
            match lv with
            | SVBool true -> Ok (SVBool true)
            | SVBool false ->
                evalCore env fuel (depth + 1) r |> Result.bind (fun rv -> evalBinOp OpOr lv rv)
            | _ -> Error (sprintf "Static evaluation: cannot apply %A to %A" OpOr lv))

    // A literal-only operand beside a non-literal one ADAPTS (as a whole) to
    // the width its partner fixes, within its class (round 6 rule 5): the
    // partner is evaluated first, then the literal subtree at its width.
    // `^` is NOT a partner seam: base and exponent keep their own widths
    // (the exponent is polymorphic; `x ^ int` keeps the base's type), so a
    // `^` with exactly one literal-only side falls to the plain arm below,
    // where that side folds at its own default -- the checker's typing.
    | ExprKind.ExprBinOp (_, op, l, r) when op <> OpCaret && isLitOnly l <> isLitOnly r ->
        let litSide, other = if isLitOnly l then l, r else r, l
        evalCore env fuel (depth + 1) other |> Result.bind (fun ov ->
            let (fw0, iw0) = litDefaultWidths litSide
            let fw = match ov with SVFloat32 _ -> Blade.Types.ETFloat32 | SVFloat _ -> Blade.Types.ETFloat64 | _ -> fw0
            let iw = match ov with SVInt32 _ -> Blade.Types.ETInt32 | SVInt _ -> Blade.Types.ETInt64 | _ -> iw0

            evalLitTree env fuel (depth + 1) fw iw litSide |> Result.bind (fun lv' ->
                if isLitOnly l then evalBinOp op lv' ov else evalBinOp op ov lv'))
    | ExprKind.ExprBinOp (_, op, l, r) ->
        // Both operands are visited at depth + 1 and BOTH draw from the same
        // step pool. Under the old `fuel - 1`-per-child threading they each
        // received a private copy of the parent's remainder, which is what
        // made the budget a depth bound with a step bound's name.
        evalCore env fuel (depth + 1) l |> Result.bind (fun lv ->
        evalCore env fuel (depth + 1) r |> Result.bind (fun rv ->
            evalBinOp op lv rv))

    | ExprKind.ExprUnaryOp (op, e) ->
        evalCore env fuel (depth + 1) e |> Result.bind (fun v ->
            evalNeg op v)

    // Syntactic builtins (`idx_card(R)`): the argument names a DECLARATION,
    // so it is handed over unevaluated. Checked before the static-function
    // and evaluated-args paths -- see registerSyntacticStaticBuiltin.
    | ExprKind.ExprApp ({ Kind = ExprKind.ExprVar fname }, args) when (trySyntacticBuiltin fname).IsSome ->
        (trySyntacticBuiltin fname).Value env fuel.Left args

    // A WIDTH CAST folds (round 6 rule 7): a literal-only operand takes the
    // cast's width within its class (`Float64(0.1)` is the double nearest
    // 0.1, never the Float32 0.1 widened); anything else converts once from
    // its own width -- the runtime's static_cast, BL8014's range refusal
    // included.
    | ExprKind.ExprApp ({ Kind = ExprKind.ExprVar ("Float32" | "Float64" | "Int32" | "Int64" as cast) }, [ arg ])
            when not (Map.containsKey cast env.Functions) ->
        let target = castTarget cast
        let operand =
            if isLitOnly arg then
                let (fw0, iw0) = litDefaultWidths arg
                let fw = match target with Blade.Types.ETFloat32 | Blade.Types.ETFloat64 -> target | _ -> fw0
                let iw = match target with Blade.Types.ETInt32 | Blade.Types.ETInt64 -> target | _ -> iw0
                evalLitTree env fuel (depth + 1) fw iw arg
            else evalCore env fuel (depth + 1) arg
        operand |> Result.bind (convertStatic target)
    | ExprKind.ExprApp ({ Kind = ExprKind.ExprVar fname }, args) ->
        match Map.tryFind fname env.Functions with
        | Some funcDef ->
            env.CalledFunctions.Value <- Set.add fname env.CalledFunctions.Value
            evalArgs env fuel depth args |> Result.bind (fun argVals ->
                if argVals.Length <> funcDef.Params.Length then
                    Error ($"Static function '{fname}' expects {funcDef.Params.Length} args, got {argVals.Length}")
                else
                    // DEFINITION-SITE ENVIRONMENT: the body sees the module's
                    // statics (`Globals`) and its own parameters -- never the
                    // caller's parameters or block-locals, which `env.Values`
                    // also holds at this point. See `StaticEnv.Globals`.
                    let bodyEnv =
                        (funcDef.Params, argVals) ||> List.zip
                        |> List.fold (fun e (p, v) ->
                            { e with Values = Map.add p v e.Values })
                            { env with Values = env.Globals }
                    // A CALLEE'S BODY IS A CHILD FOR DEPTH PURPOSES even
                    // though it is not one syntactically: it is entered from
                    // this frame and returns to it, so the stack grows exactly
                    // as it does for a real subexpression. Charging it is what
                    // makes an unbounded static recursion hit `maxDepth`.
                    evalCore bodyEnv fuel (depth + 1) funcDef.Body)
        | None ->
            // Try as a built-in static function
            evalBuiltin env fuel depth fname args

    // Provider payload fold: `alias.read(root.vars.A)` where root is a
    // provider-backed binding (env.ProviderRoots). The registered reader
    // pulls the data through the provider at compile time -- the same
    // value the runtime read would produce, so folding is unobservable
    // except in cost. Matched by the "read" field name; the operand's root
    // decides the provider, so a non-provider `alias.read(...)` falls out
    // with foldProviderRead's steering error.
    | ExprKind.ExprApp ({ Kind = ExprKind.ExprField ({ Kind = ExprKind.ExprVar _alias }, "read") }, [inner]) ->
        foldProviderRead env inner

    | ExprKind.ExprApp (func, args) ->
        // Non-variable function position -- try evaluating
        Error "Static evaluation: unsupported function form in call"

    | ExprKind.ExprIf (cond, thenBr, elseBr) ->
        evalCore env fuel (depth + 1) cond |> Result.bind (fun cv ->
            match cv with
            | SVBool true -> evalCore env fuel (depth + 1) thenBr
            | SVBool false -> evalCore env fuel (depth + 1) elseBr
            | _ -> Error "Static evaluation: if condition must be Bool")

    | ExprKind.ExprTuple es ->
        evalArgs env fuel depth es |> Result.map SVTuple

    | ExprKind.ExprArrayLit es ->
        evalArgs env fuel depth es |> Result.map SVTuple  // static arrays as tuples

    | ExprKind.ExprLet (binding, body) ->
        evalCore env fuel (depth + 1) binding.Value |> Result.bind (fun v ->
            let env' = bindPattern env binding.Pattern v
            evalCore env' fuel (depth + 1) body)

    | ExprKind.ExprMatch (scrutinee, cases) ->
        evalCore env fuel (depth + 1) scrutinee |> Result.bind (fun sv ->
            evalMatch env fuel depth sv cases)

    | ExprKind.ExprBlock (stmts, finalExpr) ->
        evalBlock env fuel depth stmts finalExpr

    // Module-qualified static access (`M.k`): imported statics are seeded
    // into Values under their qualified name by checkModule's pre-pass --
    // consult that before treating the field access as a structural read.
    | ExprKind.ExprField ({ Kind = ExprKind.ExprVar objName }, field) when Map.containsKey $"{objName}.{field}" env.Values ->
        Ok env.Values.[$"{objName}.{field}"]

    | ExprKind.ExprField (obj, field) ->
        evalCore env fuel (depth + 1) obj |> Result.bind (fun ov ->
            match ov with
            | SVStruct (sname, sfields) ->
                match sfields |> List.tryFind (fun (fn, _) -> fn = field) with
                | Some (_, v) -> Ok v
                | None -> Error $"Static evaluation: struct {sname} has no field '{field}'"
            | _ -> Error $"Static evaluation: field access '{field}' not supported on static values")

    | ExprKind.ExprStruct (name, fields, spread) ->
        // Evaluate all field values, stored as an SVStruct (name + named
        // fields) so the folded value keeps nominal identity and splices
        // back as a designated struct literal. A `..base` spread folds the
        // base and inherits its missing fields by name. A CONSTRAINED
        // struct folding here runs its conjuncts with the field values
        // bound by name, failing the fold on violation (let-static
        // assertion semantics) instead of waiting for a runtime guard.
        let providedR =
            fields |> List.map (fun (fn, e) -> evalCore env fuel (depth + 1) e |> Result.map (fun v -> (fn, v)))
            |> List.fold (fun acc r ->
                acc |> Result.bind (fun xs -> r |> Result.map (fun x -> xs @ [x]))) (Ok [])
        let fieldValsR =
            providedR |> Result.bind (fun provided ->
                match spread with
                | None -> Ok provided
                | Some baseExpr ->
                    match Map.tryFind name env.Structs with
                    | None -> Error $"Static evaluation: cannot fold '..' spread for struct {name} (unknown field layout)"
                    | Some info ->
                        evalCore env fuel (depth + 1) baseExpr |> Result.bind (fun bv ->
                            match bv with
                            | SVStruct (_, bfields) when bfields.Length = info.Fields.Length ->
                                let providedNames = provided |> List.map fst
                                let inherited =
                                    bfields |> List.filter (fun (fn, _) -> not (List.contains fn providedNames))
                                Ok (provided @ inherited)
                            | SVTuple bvals when bvals.Length = info.Fields.Length ->
                                let providedNames = provided |> List.map fst
                                let inherited =
                                    List.zip info.Fields bvals
                                    |> List.filter (fun (fn, _) -> not (List.contains fn providedNames))
                                Ok (provided @ inherited)
                            | _ -> Error $"Static evaluation: '..' spread base for struct {name} did not fold to a {info.Fields.Length}-field struct"))
        fieldValsR
        |> Result.bind (fun fieldVals ->
            // Field order follows DECLARATION order when known (the spread
            // path requires it, and splice-back emits C++ designated
            // initializers which demand it); plain literals with an
            // unknown layout keep written order.
            let orderedFields =
                match Map.tryFind name env.Structs with
                | Some info when info.Fields.Length = fieldVals.Length
                              && (info.Fields |> List.forall (fun f -> fieldVals |> List.exists (fun (fn, _) -> fn = f))) ->
                    info.Fields |> List.map (fun f -> fieldVals |> List.find (fun (fn, _) -> fn = f))
                | _ -> fieldVals
            let result = SVStruct (name, orderedFields)
            match Map.tryFind name env.Structs with
            | Some info when not info.Conjuncts.IsEmpty ->
                let bodyEnv =
                    { env with Values = fieldVals |> List.fold (fun m (fn, v) -> Map.add fn v m) env.Values }
                let total = info.Conjuncts.Length
                let rec checkAll i cs =
                    match cs with
                    | [] -> Ok result
                    | (c: Expr) :: rest ->
                        if isPplLicenseConjunct c then checkAll (i + 1) rest
                        else
                            match evalCore bodyEnv fuel (depth + 1) c with
                            | Ok (SVBool true) -> checkAll (i + 1) rest
                            | Ok (SVBool false) ->
                                if total = 1 then Error $"Constraint violation in {name} (static)"
                                else Error $"Constraint violation in {name} (static, conjunct {i})"
                            | Ok _ -> Error $"constraint of {name} is not a boolean at compile time"
                            | Error why -> Error $"constraint of {name} cannot fold: {why}"
                checkAll 1 info.Conjuncts
            | _ -> Ok result)

    | ExprKind.ExprRead inner ->
        // Legacy AST node (no longer produced by the parser); folds the
        // same way as the qualified-application form above.
        foldProviderRead env inner

    | _ ->
        Error "Static evaluation: unsupported expression form"

/// Unary `-` / `!` on a folded value, at the operand's width (an Int32
/// negation wraps: -MIN is MIN).
and private evalNeg (op: UnaryOp) (v: StaticValue) : Result<StaticValue, string> =
    match op, v with
    | OpNeg, SVInt n -> Ok (SVInt (-n))
    | OpNeg, SVFloat f -> Ok (SVFloat (-f))
    | OpNeg, SVInt32 n -> Ok (SVInt32 (0 - n))
    | OpNeg, SVFloat32 f -> Ok (SVFloat32 (-f))
    | OpNot, SVBool b -> Ok (SVBool (not b))
    | _ -> Error (sprintf "Static evaluation: cannot apply %A to %A" op v)

/// Evaluate a LITERAL-ONLY subtree (isLitOnly) with its float literals at
/// width `fw` and its integer literals at width `iw`. A float literal's value
/// is float32(double(text)) at Float32 -- the double the lexer kept, rounded
/// once -- which is exactly the literal every lane emits.
and private evalLitTree env fuel depth (fw: Blade.Types.ElemType) (iw: Blade.Types.ElemType) (e: Expr) : Result<StaticValue, string> =
    let recur = evalLitTree env fuel (depth + 1) fw iw
    match e.Kind with
    | ExprKind.ExprLit (LitInt n) ->
        Ok (if iw = Blade.Types.ETInt32 then SVInt32 (int32 n) else SVInt n)
    | ExprKind.ExprLit (LitFloat f) ->
        Ok (if fw = Blade.Types.ETFloat32 then SVFloat32 (float32 f) else SVFloat f)
    | ExprKind.ExprUnaryOp (op, x) -> recur x |> Result.bind (evalNeg op)
    | ExprKind.ExprBinOp (_, op, l, r) ->
        recur l |> Result.bind (fun lv -> recur r |> Result.bind (fun rv -> evalBinOp op lv rv))
    | ExprKind.ExprApp ({ Kind = ExprKind.ExprVar fname }, args) ->
        args |> List.map recur |> seqResults |> Result.bind (applyBuiltin fname)
    | _ -> evalCore env fuel depth e

/// `depth` here (and in evalMatch/evalBlock/evalBuiltin below) is the depth of
/// the PARENT node; the arguments themselves are its children, hence + 1.
and private evalArgs env fuel depth (args: Expr list) : Result<StaticValue list, string> =
    args |> List.map (evalCore env fuel (depth + 1)) |> seqResults

and private seqResults (results: Result<StaticValue, string> list) : Result<StaticValue list, string> =
    results |> List.fold (fun acc r ->
        match acc, r with
        | Ok xs, Ok x -> Ok (xs @ [x])
        | Error e, _ -> Error e
        | _, Error e -> Error e) (Ok [])

and bindPattern (env: StaticEnv) (pat: Pattern) (value: StaticValue) : StaticEnv =
    match pat.Kind with
    | PatternKind.PatVar name -> { env with Values = Map.add name value env.Values }
    | PatternKind.PatTuple pats ->
        match value with
        | SVTuple vs when vs.Length = pats.Length ->
            (pats, vs) ||> List.zip |> List.fold (fun e (p, v) -> bindPattern e p v) env
        // Positional destructure of a folded struct -- the pre-SVStruct
        // behavior (structs folded as bare tuples), kept for compatibility.
        | SVStruct (_, fs) when fs.Length = pats.Length ->
            (pats, fs |> List.map snd) ||> List.zip |> List.fold (fun e (p, v) -> bindPattern e p v) env
        | _ -> env
    | PatternKind.PatStruct (_, fieldPats) ->
        match value with
        | SVStruct (_, fs) ->
            fieldPats |> List.fold (fun e (fn, p) ->
                match fs |> List.tryFind (fun (n, _) -> n = fn) with
                | Some (_, v) -> bindPattern e p v
                | None -> e) env
        | _ -> env
    | PatternKind.PatTyped (p, _) -> bindPattern env p value
    | PatternKind.PatWildcard -> env
    | _ -> env  // other patterns: no binding in static context

and private evalMatch env fuel depth (scrutinee: StaticValue) (cases: MatchCase list) : Result<StaticValue, string> =
    match cases with
    | [] -> Error "Static evaluation: no matching case in match expression"
    | case :: rest ->
        match tryMatchPattern scrutinee case.Pattern with
        | Some bindings ->
            let env' = bindings |> List.fold (fun e (n, v) -> { e with Values = Map.add n v e.Values }) env
            // Check guard if present
            match case.Guard with
            | Some guard ->
                evalCore env' fuel (depth + 1) guard |> Result.bind (fun gv ->
                    match gv with
                    | SVBool true -> evalCore env' fuel (depth + 1) case.Body
                    | SVBool false -> evalMatch env fuel depth scrutinee rest
                    | _ -> Error "Static evaluation: match guard must be Bool")
            | None ->
                evalCore env' fuel (depth + 1) case.Body
        | None ->
            evalMatch env fuel depth scrutinee rest

and tryMatchPattern (value: StaticValue) (pat: Pattern) : (string * StaticValue) list option =
    match pat.Kind with
    | PatternKind.PatWildcard -> Some []
    | PatternKind.PatVar name -> Some [(name, value)]
    | PatternKind.PatLit lit ->
        let matches =
            match lit, value with
            | LitInt n, SVInt m -> n = m
            | LitFloat f, SVFloat g -> f = g
            | LitInt n, SVInt32 m -> n = int64 m
            | LitFloat f, SVFloat32 g -> float32 f = g
            | LitBool a, SVBool b -> a = b
            | LitString a, SVString b -> a = b
            | _ -> false
        if matches then Some [] else None
    | PatternKind.PatTuple pats ->
        let elems =
            match value with
            | SVTuple vs -> Some vs
            // Positional match against a folded struct (pre-SVStruct compat).
            | SVStruct (_, fs) -> Some (fs |> List.map snd)
            | _ -> None
        match elems with
        | Some vs when vs.Length = pats.Length ->
            let results = (pats, vs) ||> List.zip |> List.map (fun (p, v) -> tryMatchPattern v p)
            if results |> List.forall Option.isSome then
                Some (results |> List.choose id |> List.concat)
            else None
        | _ -> None
    | PatternKind.PatStruct (pname, fieldPats) ->
        match value with
        | SVStruct (sname, fs) when pname = sname ->
            let results =
                fieldPats |> List.map (fun (fn, p) ->
                    fs |> List.tryFind (fun (n, _) -> n = fn)
                       |> Option.bind (fun (_, v) -> tryMatchPattern v p))
            if results |> List.forall Option.isSome then
                Some (results |> List.choose id |> List.concat)
            else None
        | _ -> None
    | PatternKind.PatVariant (tag, payloadPat) ->
        // Simplified: full variant matching would need the static value
        // to carry a tag.
        None
    | _ -> None

and private evalBlock env fuel depth (stmts: Stmt list) (finalExpr: Expr option) : Result<StaticValue, string> =
    // Statements are SIBLINGS, so the depth passed on is the block's own --
    // a long block is wide, not deep, and only the step pool should feel it.
    match stmts with
    | [] ->
        match finalExpr with
        | Some e -> evalCore env fuel (depth + 1) e
        | None -> Ok SVUnit
    | StmtSpanned (inner, _) :: rest ->
        // Span annotations are transparent to static evaluation.
        evalBlock env fuel depth (inner :: rest) finalExpr
    | StmtLet binding :: rest ->
        evalCore env fuel (depth + 1) binding.Value |> Result.bind (fun v ->
            let env' = bindPattern env binding.Pattern v
            evalBlock env' fuel depth rest finalExpr)
    | StmtExpr e :: rest ->
        evalCore env fuel (depth + 1) e |> Result.bind (fun _ ->
            evalBlock env fuel depth rest finalExpr)
    | StmtAssign _ :: rest ->
        evalBlock env fuel depth rest finalExpr
    | StmtForIn _ :: rest ->
        evalBlock env fuel depth rest finalExpr  // Skip for-in loops in static eval

/// Built-in static functions (abs, min, max, length, etc.)
and private evalBuiltin env fuel depth (name: string) (args: Expr list) : Result<StaticValue, string> =
    evalArgs env fuel depth args |> Result.bind (applyBuiltin name)

/// A built-in over already-folded arguments. Float32 operands follow the
/// arithmetic contract (formalism 2.4): a math intrinsic is the double
/// function rounded ONCE to float (exactly the float operation for sqrt /
/// floor / ceil / abs), fma of three Float32s is fmaf, min / max / abs keep
/// the width. Every other builtin sees normalized (wide) arguments.
and private applyBuiltin (name: string) (argVals0: StaticValue list) : Result<StaticValue, string> =
    let narrowMath (f: float -> float) (x: float32) = SVFloat32 (float32 (f (float x)))
    match name, argVals0 with
    | _, [ SVFloat32 x ] when litMathNames.Contains name && name <> "abs" && name <> "min" && name <> "max" && name <> "atan2" && name <> "log_base" && name <> "fma" ->
        let fn =
            match name with
            | "exp" -> exp | "log" -> log | "log10" -> log10 | "sqrt" -> sqrt
            | "sin" -> sin | "cos" -> cos | "tan" -> tan
            | "sinh" -> sinh | "cosh" -> cosh | "tanh" -> tanh
            | "asin" -> asin | "acos" -> acos | "atan" -> atan
            | "floor" -> floor | _ -> ceil
        Ok (narrowMath fn x)
    | "atan2", [ SVFloat32 y; SVFloat32 x ] -> Ok (SVFloat32 (float32 (atan2 (float y) (float x))))
    | "log_base", [ SVFloat32 x; SVFloat32 b ] ->
        // each log rounded to float, then the float quotient (blade_libm's
        // float overloads, the interpreter's log_base arm)
        Ok (SVFloat32 (float32 (log (float x)) / float32 (log (float b))))
    | "fma", [ SVFloat32 a; SVFloat32 b; SVFloat32 c ] -> Ok (SVFloat32 (System.MathF.FusedMultiplyAdd (a, b, c)))
    | "abs", [ SVFloat32 f ] -> Ok (SVFloat32 (abs f))
    | "abs", [ SVInt32 n ] -> Ok (SVInt32 (if n < 0 then 0 - n else n))
    | "min", [ SVFloat32 a; SVFloat32 b ] -> Ok (SVFloat32 (min a b))
    | "max", [ SVFloat32 a; SVFloat32 b ] -> Ok (SVFloat32 (max a b))
    | "min", [ SVInt32 a; SVInt32 b ] -> Ok (SVInt32 (min a b))
    | "max", [ SVInt32 a; SVInt32 b ] -> Ok (SVInt32 (max a b))
    | _ ->
    let argVals = argVals0 |> List.map normalizeStatic
    (
        // Scalar math intrinsics: same whitelist as TypeCheck.mathIntrinsics
        // (runtime form renders std::<name>); int operands promote to float.
        let asFloat = function SVInt n -> Some (float n) | SVFloat f -> Some f | _ -> None
        let mathFns : Map<string, float -> float> =
            Map.ofList [
                "exp", exp; "log", log; "log10", log10; "sqrt", sqrt
                "sin", sin; "cos", cos; "tan", tan
                "sinh", sinh; "cosh", cosh; "tanh", tanh
                "asin", asin; "acos", acos; "atan", atan
                "floor", floor; "ceil", ceil
            ]
        // Binary math intrinsics (Grad.binaryMathIntrinsics). Same whitelist
        // discipline as the unary table, one arity up; log_base folds through
        // the same log/log quotient CodeGen emits, so a static fold and a
        // runtime evaluation agree.
        let mathFns2 : Map<string, float -> float -> float> =
            Map.ofList [
                "atan2", (fun y x -> atan2 y x)
                "log_base", (fun x b -> log x / log b)
            ]
        match name, argVals with
        | _, [v] when (Map.containsKey name mathFns) && (asFloat v).IsSome ->
            Ok (SVFloat (mathFns.[name] (asFloat v).Value))
        | _, [a; b] when (Map.containsKey name mathFns2) && (asFloat a).IsSome && (asFloat b).IsSome ->
            Ok (SVFloat (mathFns2.[name] (asFloat a).Value (asFloat b).Value))
        // fma: the same correctly-rounded FusedMultiplyAdd the interpreter
        // evaluates, so a static fold and a runtime evaluation agree.
        | "fma", [a; b; c] when (asFloat a).IsSome && (asFloat b).IsSome && (asFloat c).IsSome ->
            Ok (SVFloat (System.Math.FusedMultiplyAdd ((asFloat a).Value, (asFloat b).Value, (asFloat c).Value)))
        | "abs", [SVInt n] -> Ok (SVInt (abs n))
        | "abs", [SVFloat f] -> Ok (SVFloat (abs f))
        | "min", [SVInt a; SVInt b] -> Ok (SVInt (min a b))
        | "max", [SVInt a; SVInt b] -> Ok (SVInt (max a b))
        | "min", [SVFloat a; SVFloat b] -> Ok (SVFloat (min a b))
        | "max", [SVFloat a; SVFloat b] -> Ok (SVFloat (max a b))
        | "length", [SVTuple xs] -> Ok (SVInt (int64 xs.Length))
        | "prodsum", (SVTuple _ :: _) when argVals |> List.forall _.IsSVTuple ->
            // Static mirror of the runtime prodsum intrinsic: sum_t prod_l
            // x_l(t), over equal-length static arrays (fold as SVTuple).
            let tuples = argVals |> List.map (function SVTuple xs -> xs | _ -> [])
            let n = tuples.Head.Length
            if tuples |> List.exists (fun t -> t.Length <> n) then
                Error "prodsum: static operands must share one length"
            else
                let asF = function SVInt i -> Ok (float i) | SVFloat f -> Ok f | v -> Error (sprintf "prodsum: non-numeric static element %A" v)
                let folded =
                    [0 .. n - 1] |> List.fold (fun acc t ->
                        acc |> Result.bind (fun s ->
                            tuples |> List.fold (fun p tup -> p |> Result.bind (fun pv -> asF tup.[t] |> Result.map (fun x -> pv * x))) (Ok 1.0)
                            |> Result.map (fun prod -> s + prod))) (Ok 0.0)
                folded |> Result.map SVFloat
        | _ ->
            // External registry (domain layers, e.g. the ML module's sizing
            // builtins). Consulted after the core table misses so core
            // names cannot be overridden.
            match externalBuiltins.TryGetValue name with
            | true, f -> f argVals
            | _ -> Error $"Static evaluation: unknown function '{name}' or wrong arguments")

/// The scalar width a cast name converts to.
and private castTarget (name: string) : Blade.Types.ElemType =
    match name with
    | "Float32" -> Blade.Types.ETFloat32
    | "Int32" -> Blade.Types.ETInt32
    | "Int64" -> Blade.Types.ETInt64
    | _ -> Blade.Types.ETFloat64

/// Convert a folded scalar to a width, as the runtime cast does: float ->
/// float rounds once (an Int64 straight to float, never through a double),
/// an integer narrowing wraps, a float -> integer truncation outside the
/// target's range refuses the fold (it panics BL8014 at run time).
and convertStatic (target: Blade.Types.ElemType) (v: StaticValue) : Result<StaticValue, string> =
    let f2i (lo: float) (hi: float) (x: float) (mk: float -> StaticValue) =
        if x >= lo && x < hi then Ok (mk x)
        else Error "Static evaluation: float-to-integer conversion of NaN or an out-of-range value"
    match target, v with
    | Blade.Types.ETFloat32, SVFloat32 _ -> Ok v
    | Blade.Types.ETFloat32, SVFloat f -> Ok (SVFloat32 (float32 f))
    | Blade.Types.ETFloat32, SVInt n -> Ok (SVFloat32 (float32 n))
    | Blade.Types.ETFloat32, SVInt32 n -> Ok (SVFloat32 (float32 n))
    | Blade.Types.ETFloat64, SVFloat _ -> Ok v
    | Blade.Types.ETFloat64, SVFloat32 f -> Ok (SVFloat (float f))
    | Blade.Types.ETFloat64, SVInt n -> Ok (SVFloat (float n))
    | Blade.Types.ETFloat64, SVInt32 n -> Ok (SVFloat (float n))
    | Blade.Types.ETInt32, SVInt32 _ -> Ok v
    | Blade.Types.ETInt32, SVInt n -> Ok (SVInt32 (int32 n))
    | Blade.Types.ETInt32, SVFloat f -> f2i -2147483648.0 2147483648.0 f (fun x -> SVInt32 (int32 x))
    | Blade.Types.ETInt32, SVFloat32 f -> f2i -2147483648.0 2147483648.0 (float f) (fun x -> SVInt32 (int32 x))
    | Blade.Types.ETInt64, SVInt _ -> Ok v
    | Blade.Types.ETInt64, SVInt32 n -> Ok (SVInt (int64 n))
    | Blade.Types.ETInt64, SVFloat f -> f2i -9223372036854775808.0 9223372036854775808.0 f (fun x -> SVInt (int64 x))
    | Blade.Types.ETInt64, SVFloat32 f -> f2i -9223372036854775808.0 9223372036854775808.0 (float f) (fun x -> SVInt (int64 x))
    | _ -> Error (sprintf "Static evaluation: cannot convert %A to %A" v target)


/// Evaluate binary operations with type promotion
and evalBinOp (op: BinOp) (lv: StaticValue) (rv: StaticValue) : Result<StaticValue, string> =
    // ROUND 6 WIDTHS. Two Float32s: float32 arithmetic, ROUNDED AFTER EVERY
    // OPERATION (.NET's float32 operators are single-rounded on x64 RyuJIT,
    // SSE scalar single -- what the box runs). Two Int32s: 32-bit WRAPPING
    // arithmetic (unchecked .NET int32), MIN / -1 = MIN, MIN % -1 = 0. An
    // Int32 static beside an Int64 widens (integer widths adapt); a Float32
    // beside a Float64 cannot reach here (the checker refuses the mix) and
    // widens too, defensively.
    match op, lv, rv with
    | OpCaret, SVFloat32 a, (SVInt _ | SVInt32 _) ->
        // Float32 ^ Int: the double repeated squaring rounded ONCE to float
        // (blade_arith::fpowif).
        let e = match rv with SVInt32 n -> int64 n | SVInt n -> n | _ -> 0L
        Ok (SVFloat32 (float32 (floatPowInt (float a) e)))
    | OpCaret, SVInt32 _, SVInt32 b when b < 0 ->
        Error "Static evaluation: integer power with a negative exponent -- convert to Float64 first for a real power"
    | OpCaret, SVInt32 a, (SVInt32 _ | SVInt _) ->
        let e = match rv with SVInt32 n -> int64 n | SVInt n -> n | _ -> 0L
        if e < 0L then Error "Static evaluation: integer power with a negative exponent -- convert to Float64 first for a real power"
        else
            let rec pow (acc: int32) (bse: int32) (e: int64) =
                if e = 0L then acc
                else pow (if e % 2L = 1L then acc * bse else acc) (bse * bse) (e / 2L)
            Ok (SVInt32 (pow 1 a e))
    | _, SVFloat32 a, SVFloat32 b ->
        (match op with
         | OpAdd -> Ok (SVFloat32 (a + b))
         | OpSub -> Ok (SVFloat32 (a - b))
         | OpMul -> Ok (SVFloat32 (a * b))
         | OpDiv -> Ok (SVFloat32 (a / b))
         | OpEq -> Ok (SVBool (a = b))
         | OpNeq -> Ok (SVBool (a <> b))
         | OpLt -> Ok (SVBool (a < b))
         | OpLe -> Ok (SVBool (a <= b))
         | OpGt -> Ok (SVBool (a > b))
         | OpGe -> Ok (SVBool (a >= b))
         | OpMath2 "atan2" -> applyBuiltin "atan2" [ lv; rv ]
         | OpMath2 "log_base" -> applyBuiltin "log_base" [ lv; rv ]
         | _ -> Error (sprintf "Static evaluation: cannot apply %A to %A and %A" op lv rv))
    | _, SVInt32 a, SVInt32 b ->
        (match op with
         | OpAdd -> Ok (SVInt32 (a + b))
         | OpSub -> Ok (SVInt32 (a - b))
         | OpMul -> Ok (SVInt32 (a * b))
         | OpDiv when b = 0 -> Error "Static evaluation: division by zero"
         | OpDiv when b = -1 -> Ok (SVInt32 (0 - a))
         | OpDiv -> Ok (SVInt32 (a / b))
         | OpMod when b = 0 -> Error "Static evaluation: modulo by zero"
         | OpMod when b = -1 -> Ok (SVInt32 0)
         | OpMod -> Ok (SVInt32 (a % b))
         | OpEq -> Ok (SVBool (a = b))
         | OpNeq -> Ok (SVBool (a <> b))
         | OpLt -> Ok (SVBool (a < b))
         | OpLe -> Ok (SVBool (a <= b))
         | OpGt -> Ok (SVBool (a > b))
         | OpGe -> Ok (SVBool (a >= b))
         | _ -> Error (sprintf "Static evaluation: cannot apply %A to %A and %A" op lv rv))
    // Mixed widths: widen the narrow side and fold at the wide width.
    | _, (SVFloat32 _ | SVInt32 _), _ | _, _, (SVFloat32 _ | SVInt32 _) ->
        evalBinOp op (normalizeStatic lv) (normalizeStatic rv)
    | _ ->
    match op, lv, rv with
    // Float ^ Int: the exponent is NOT promoted (formalism 2.4) -- repeated
    // squaring, the run-time algorithm, so the fold is the run-time value.
    // (Float ^ Float is libm pow, never folded: it falls to the refusal below.)
    | OpCaret, SVFloat a, SVInt b -> Ok (SVFloat (floatPowInt a b))
    | _ ->
    // Promote int to float if mixed
    let lv', rv' =
        match lv, rv with
        | SVInt a, SVFloat _ -> SVFloat (float a), rv
        | SVFloat _, SVInt b -> lv, SVFloat (float b)
        | _ -> lv, rv
    match op, lv', rv' with
    // Integer arithmetic
    | OpAdd, SVInt a, SVInt b -> Ok (SVInt (a + b))
    | OpSub, SVInt a, SVInt b -> Ok (SVInt (a - b))
    | OpMul, SVInt a, SVInt b -> Ok (SVInt (a * b))
    // The arithmetic contract (docs/formalism.md section 2.4) at compile time:
    // MIN / -1 wraps to MIN and MIN % -1 is 0 (.NET would throw
    // OverflowException out of the compiler); a zero divisor refuses.
    | OpDiv, SVInt _, SVInt 0L -> Error "Static evaluation: division by zero"
    | OpDiv, SVInt a, SVInt -1L -> Ok (SVInt (0L - a))
    | OpDiv, SVInt a, SVInt b -> Ok (SVInt (a / b))
    | OpMod, SVInt _, SVInt 0L -> Error "Static evaluation: modulo by zero"
    | OpMod, SVInt _, SVInt -1L -> Ok (SVInt 0L)
    | OpMod, SVInt a, SVInt b -> Ok (SVInt (a % b))
    // Integer power: exact and WRAPPING like `*` (square-and-multiply modulo
    // 2^64, `0 ^ 0 = 1`), the runtime contract (formalism 2.4); a negative
    // exponent, which panics BL8013 at run time, refuses the fold.
    | OpCaret, SVInt _, SVInt b when b < 0L ->
        Error "Static evaluation: integer power with a negative exponent -- convert to Float64 first for a real power"
    | OpCaret, SVInt a, SVInt b ->
        let rec pow (acc: int64) (bse: int64) (e: int64) =
            if e = 0L then acc
            else pow (if e % 2L = 1L then acc * bse else acc) (bse * bse) (e / 2L)
        Ok (SVInt (pow 1L a b))
    // Float arithmetic
    | OpAdd, SVFloat a, SVFloat b -> Ok (SVFloat (a + b))
    | OpSub, SVFloat a, SVFloat b -> Ok (SVFloat (a - b))
    | OpMul, SVFloat a, SVFloat b -> Ok (SVFloat (a * b))
    | OpDiv, SVFloat a, SVFloat b -> Ok (SVFloat (a / b))
    // Integer comparisons
    | OpEq,  SVInt a, SVInt b -> Ok (SVBool (a = b))
    | OpNeq, SVInt a, SVInt b -> Ok (SVBool (a <> b))
    | OpLt,  SVInt a, SVInt b -> Ok (SVBool (a < b))
    | OpLe,  SVInt a, SVInt b -> Ok (SVBool (a <= b))
    | OpGt,  SVInt a, SVInt b -> Ok (SVBool (a > b))
    | OpGe,  SVInt a, SVInt b -> Ok (SVBool (a >= b))
    // Float comparisons
    | OpEq,  SVFloat a, SVFloat b -> Ok (SVBool (a = b))
    | OpNeq, SVFloat a, SVFloat b -> Ok (SVBool (a <> b))
    | OpLt,  SVFloat a, SVFloat b -> Ok (SVBool (a < b))
    | OpLe,  SVFloat a, SVFloat b -> Ok (SVBool (a <= b))
    | OpGt,  SVFloat a, SVFloat b -> Ok (SVBool (a > b))
    | OpGe,  SVFloat a, SVFloat b -> Ok (SVBool (a >= b))
    // Boolean
    | OpAnd, SVBool a, SVBool b -> Ok (SVBool (a && b))
    | OpOr,  SVBool a, SVBool b -> Ok (SVBool (a || b))
    // String equality
    | OpEq,  SVString a, SVString b -> Ok (SVBool (a = b))
    | OpNeq, SVString a, SVString b -> Ok (SVBool (a <> b))
    | _ -> Error (sprintf "Static evaluation: cannot apply %A to %A and %A" op lv rv)

/// Fold an expression under an explicit budget. Each call starts a FRESH
/// pool at depth zero -- the budget bounds one top-level fold, not the compiler.
let evalExprWith (env: StaticEnv) (budget: Budget) (expr: Expr) : Result<StaticValue, string> =
    evalCore env { Left = budget.Steps; MaxDepth = budget.Depth } 0 expr
    |> Result.map normalizeStatic

/// The fold WITHOUT normalization: a Float32 / Int32 result stays SVFloat32 /
/// SVInt32 (resolveStatics records the width of the static it binds).
/// Unfold also needs these tags when it reifies a folded value as source.
let internal evalExprNarrow (env: StaticEnv) (fuel: int) (expr: Expr) : Result<StaticValue, string> =
    evalCore env { Left = fuel; MaxDepth = defaultBudget.Depth } 0 expr

/// Fold an expression under a STEP budget of `fuel`, at the default depth
/// ceiling. The historical signature; every existing call site passes
/// `maxSteps`. Reach for `evalExprWith` when the caller's cost model
/// differs from `let static` folding's (the counting layer's per-cell
/// budget is the one such caller today).
let evalExpr (env: StaticEnv) (fuel: int) (expr: Expr) : Result<StaticValue, string> =
    evalExprWith env { defaultBudget with Steps = fuel } expr

// Static Resolution -- Main Entry Point

/// A `let static` declaration whose right-hand side did not evaluate at
/// compile time. `let static` is an assertion -- fold or fail loudly -- so
/// the type-checker turns these into compile errors. A bare `let` remains
/// free to stage its work at runtime; only the annotated form demands
/// folding.
type StaticFailure = {
    /// Names bound by the declaration's pattern (one for `let static x`,
    /// several for tuple destructuring).
    Names: string list
    /// The evaluator's reason for the failure.
    Reason: string
    /// The declaration's source span.
    Span: Span
}

/// One `let static` declaration collected in Phase 1, carrying what Phase 3
/// needs to evaluate it once and report a failure against source.
type private PendingStatic = {
    Id: int
    Pattern: Pattern
    Names: string list
    Expr: Expr
    Span: Span
    /// The declared type (`let static x: Float32 = ...`): a numeric WIDTH
    /// annotation fixes the width a literal-only value adapts to (round 6
    /// rule 5) and the width the folded value is converted to.
    Annot: TypeExpr option
}

/// The scalar width a `let static` annotation names, if it is one of the
/// bit-sized numeric types (units ride along: `Float32<m>` is a Float32).
let private annotWidth (t: TypeExpr option) : Blade.Types.ElemType option =
    match t with
    | Some (TyNamed (("Float32" | "Float64" | "Int32" | "Int64") as n, _)) ->
        Some (match n with
              | "Float32" -> Blade.Types.ETFloat32
              | "Float64" -> Blade.Types.ETFloat64
              | "Int32" -> Blade.Types.ETInt32
              | _ -> Blade.Types.ETInt64)
    | _ -> None

/// A lambda-valued `let static` declares a function (the marker means
/// immutability there), not a foldable value -- the fold assertion skips it.
let rec private isLambdaExpr (expr: Expr) : bool =
    match expr.Kind with
    | ExprKind.ExprLambda _ -> true
    | ExprKind.ExprTyped (e, _) -> isLambdaExpr e
    | _ -> false

/// Resolve all static declarations in a module.
/// Returns the environment of folded values (tuple-destructured statics
/// bind their leaf names) plus one StaticFailure per `let static` whose
/// right-hand side did not evaluate. The Error case is reserved for a
/// circular dependency among static values.
/// The static segment tables of a module's `type A = Chunked<I, K>`
/// declarations: I a `type I = Idx<n>` alias with a literal n (or a literal
/// `Idx<n>` inline), K a literal edge. Offsets `[0; K; 2K; ..; n]`.
let segmentTables (decls: Located<Decl> list) : Map<string, int64 list> =
    let idxExtents =
        decls |> List.choose (fun d ->
            match d.Value with
            | DeclType (TyDeclAlias (name, _, TyIdx { Kind = ExprKind.ExprLit (LitInt n) })) -> Some (name, n)
            | _ -> None)
        |> Map.ofList
    decls |> List.choose (fun d ->
        match d.Value with
        | DeclType (TyDeclAlias (name, _, TyChunked (inner, { Kind = ExprKind.ExprLit (LitInt k) }))) when k >= 1L ->
            let extent =
                match inner with
                | TyNamed (i, []) -> Map.tryFind i idxExtents
                | TyIdx { Kind = ExprKind.ExprLit (LitInt n) } -> Some n
                | _ -> None
            extent |> Option.map (fun n ->
                let cuts = [ for b in 0L .. k .. n - 1L -> b ] @ [ n ]
                (name, cuts))
        | _ -> None)
    |> Map.ofList

/// `segments(A)` in a static position evaluates to the tuple of GROUP SIZES
/// (what `extents(gk)` answers at run time), so `length(segments(A))` is the
/// segment count and its sum the axis extent. Registered as a syntactic
/// builtin: the argument names a type, not a value.
do registerSyntacticStaticBuiltin "segments" (fun env _ args ->
    match args with
    | [ { Kind = ExprKind.ExprVar alias } ] ->
        (match Map.tryFind alias env.Segments with
         | Some offsets ->
             let sizes = offsets |> List.pairwise |> List.map (fun (lo, hi) -> SVInt (hi - lo))
             Ok (SVTuple sizes)
         | None -> Error $"Static evaluation: segments({alias}): '{alias}' is not a `Chunked<Idx<n>, k>` alias with literal extent and edge")
    | _ -> Error "Static evaluation: segments takes one argument, the name of a `Chunked<..>` alias")

let resolveStatics (decls: Located<Decl> list) : Result<StaticEnv * StaticFailure list, string> =
    // Phase 1: Collect static function definitions and static value decls
    let mutable staticFuncs : Map<string, StaticFuncDef> = Map.empty
    let mutable pendingRev : PendingStatic list = []

    let mutable structInfos : Map<string, StructStaticInfo> = Map.empty

    for locDecl in decls do
        match locDecl.Value with
        | DeclFunction fd when fd.IsStatic ->
            staticFuncs <- Map.add fd.Name {
                Name = fd.Name
                Params = fd.Params |> List.map (_.Name)
                Body = fd.Body
            } staticFuncs
        | DeclType (TyDeclStruct (sname, _, sfields, sconstraints, sIsStatic)) ->
            // Full pre-scan (struct/static decl order is irrelevant): the
            // fold-time conjunct list mirrors the checker's via the shared
            // Ast.structConjuncts helper.
            structInfos <- Map.add sname {
                Fields = sfields |> List.map (_.Name)
                Conjuncts = structConjuncts sfields sconstraints
                FieldDecls = sfields
                Declared = sconstraints
                IsStatic = sIsStatic
            } structInfos
        | DeclStatic binding ->
            // Any pattern that binds at least one name participates; a
            // pure-wildcard static asserts nothing observable.
            let names = collectPatternBindings binding.Pattern |> Set.toList
            if not names.IsEmpty then
                pendingRev <- { Id = List.length pendingRev
                                Pattern = binding.Pattern
                                Names = names
                                Expr = binding.Value
                                Span = locDecl.Span
                                Annot = binding.Type } :: pendingRev
        | _ -> ()

    let pending = List.rev pendingRev
    let staticNames = pending |> List.collect (_.Names) |> Set.ofList

    // Provider-backed roots: `import netcdf as nc` provider-module aliases
    // plus the bindings that load through them (`let sample =
    // nc.load("file")`), giving the provider-read fold its name -> (provider,
    // path) map. Both plain and static load bindings are recognized.
    let providerAliases =
        decls |> List.fold (fun acc d ->
            match d.Value with
            | DeclImport ([pname], ImportQualified aliasOpt) when isProviderModuleName pname ->
                let alias = aliasOpt |> Option.defaultValue pname
                Map.add alias pname acc
            | _ -> acc) Map.empty
    let providerRoots =
        decls |> List.fold (fun acc d ->
            match d.Value with
            | DeclLet { Pattern = { Kind = PatternKind.PatVar root }; Value = { Kind = ExprKind.ExprApp ({ Kind = ExprKind.ExprField ({ Kind = ExprKind.ExprVar alias }, "load") }, [{ Kind = ExprKind.ExprLit (LitString path) }]) } }
            | DeclStatic { Pattern = { Kind = PatternKind.PatVar root }; Value = { Kind = ExprKind.ExprApp ({ Kind = ExprKind.ExprField ({ Kind = ExprKind.ExprVar alias }, "load") }, [{ Kind = ExprKind.ExprLit (LitString path) }]) } }
                when Map.containsKey alias providerAliases ->
                Map.add root (providerAliases.[alias], path) acc
            | _ -> acc) Map.empty

    // Phase 2: Dependency graph over bound names -- a destructured decl's
    // names share the decl's dependencies -- and topological sort.
    // A static CALLEE'S body is part of the initializer's dependency set: `let
    // static a = f()` with `static function f() = z` needs `z` folded before
    // `a`, and nothing about the call text names `z`. Walked transitively
    // (f may call g may read z) with a visited set, so a self- or mutually-
    // recursive static function terminates rather than looping; the callee's
    // own parameters are its locals and are subtracted, never dependencies.
    let rec namesThroughStaticCallees (visited: Set<string>) (names: Set<string>) : Set<string> =
        let callees =
            names |> Set.filter (fun n -> Map.containsKey n staticFuncs && not (Set.contains n visited))
        if Set.isEmpty callees then names
        else
            let visited' = Set.union visited callees
            let fromBodies =
                callees
                |> Set.toList
                |> List.map (fun f ->
                    let fd = staticFuncs.[f]
                    Set.difference (collectFreeNames fd.Body) (Set.ofList fd.Params))
                |> Set.unionMany
            namesThroughStaticCallees visited' (Set.union names fromBodies)
    let deps =
        pending
        |> List.collect (fun pd ->
            let direct = namesThroughStaticCallees Set.empty (collectFreeNames pd.Expr)
            // NAMING A STRUCT PULLS IN THE STRUCT'S OWN STATICS: a static
            // expression that mentions a struct TYPE by name (`idx_card(R)`)
            // is going to fold that struct's field bounds and conjuncts,
            // which may name statics the mentioning expression never does
            // (`static struct R { m: Int<min=-L, max=L> }` with
            // `let static L = 1` gives `idx_card(R)` a real dependency on L
            // no walk of the CALL can see). Without this edge the
            // topological sort could fold the call first and fail with
            // "undefined variable 'L'" against a perfectly good program.
            // Field names themselves are excluded: bound per cell, not
            // statics. Struct names reach this fold two ways, neither
            // covering the other: MENTIONED as a value (an ExprVar, already
            // in `direct`), or CONSTRUCTED as a literal (an ExprStruct,
            // whose name is not an ExprVar) -- seeded into the lookup only,
            // never into `refs`, so a static sharing a struct's name cannot
            // gain a spurious self-edge.
            let mentioned = Set.union direct (collectStructLitNames pd.Expr)
            let structRefs =
                mentioned |> Set.fold (fun acc n ->
                    match Map.tryFind n structInfos with
                    | None -> acc
                    | Some info ->
                        let fieldNames = Set.ofList info.Fields
                        let bounds =
                            info.FieldDecls
                            |> List.collect (fun f ->
                                match f.Bound with
                                | Some b -> [ b.Lo; b.Hi ] |> List.choose id
                                | None -> [])
                        let named =
                            (bounds @ info.Conjuncts)
                            |> List.fold (fun s e -> Set.union s (collectFreeNames e)) Set.empty
                        Set.union acc (Set.difference named fieldNames)) Set.empty
            let refs = Set.union direct structRefs
            // Only dependencies on OTHER static values (not functions, not
            // names bound by this same declaration)
            let declDeps = Set.difference (Set.intersect refs staticNames) (Set.ofList pd.Names)
            pd.Names |> List.map (fun n -> (n, declDeps)))
        |> Map.ofList

    match topoSort deps with
    | Error cycle ->
        Error ($"""Static evaluation: circular dependency among: {(cycle |> String.concat ", ")}""")
    | Ok evalOrder ->
        // Phase 3: Evaluate each declaration once, in dependency order.
        // Duplicate names across decls: Map.ofList keeps the last decl,
        // matching the pre-assertion shadowing behavior.
        let nameToDecl =
            pending
            |> List.collect (fun pd -> pd.Names |> List.map (fun n -> (n, pd)))
            |> Map.ofList
        let calledRef = ref Set.empty
        let mutable env = { Values = Map.empty; Globals = Map.empty; Functions = staticFuncs; CalledFunctions = calledRef; ProviderRoots = providerRoots; Structs = structInfos; Segments = segmentTables decls; Widths = Map.empty }
        let mutable failures : StaticFailure list = []
        let mutable evaluated = Set.empty

        for name in evalOrder do
            match Map.tryFind name nameToDecl with
            | Some pd when not (Set.contains pd.Id evaluated) ->
                evaluated <- Set.add pd.Id evaluated
                if isLambdaExpr pd.Expr then
                    ()  // function definition, lowered as an ordinary closure
                else
                    // A width annotation: a literal-only value adapts to it
                    // as a whole (`let static x: Float32 = 0.1 * 3.0` is two
                    // float32 literals and a float32 product), any other value
                    // is folded at its own width and converted once.
                    let folded =
                        match annotWidth pd.Annot with
                        | Some w when isLitOnly pd.Expr ->
                            let lit =
                                match w with
                                | Blade.Types.ETFloat32 | Blade.Types.ETFloat64 ->
                                    { pd.Expr with Kind = ExprKind.ExprApp ({ pd.Expr with Kind = ExprKind.ExprVar (if w = Blade.Types.ETFloat32 then "Float32" else "Float64") }, [ pd.Expr ]) }
                                | _ ->
                                    { pd.Expr with Kind = ExprKind.ExprApp ({ pd.Expr with Kind = ExprKind.ExprVar (if w = Blade.Types.ETInt32 then "Int32" else "Int64") }, [ pd.Expr ]) }
                            evalExprNarrow env maxSteps lit
                        | Some w ->
                            evalExprNarrow env maxSteps pd.Expr
                            |> Result.bind (fun v ->
                                match v with
                                | SVFloat _ | SVFloat32 _ | SVInt _ | SVInt32 _ -> convertStatic w v
                                | _ -> Ok v)
                        | None -> evalExprNarrow env maxSteps pd.Expr
                    match folded with
                    | Ok value ->
                        // Top level: every folded static is a module global,
                        // so `Globals` tracks `Values` exactly here. Values
                        // hold the normalized spelling; a Float32 / Int32
                        // static's width goes to `Widths` (read back at width).
                        let bound = bindPattern env pd.Pattern (normalizeStatic value)
                        let widths =
                            match pd.Pattern.Kind, narrowWidthOf value with
                            | PatternKind.PatVar n, Some w -> Map.add n w env.Widths
                            | PatternKind.PatVar n, None -> Map.remove n env.Widths
                            | _ -> env.Widths
                        env <- { bound with Globals = bound.Values; Widths = widths }
                    | Error reason ->
                        failures <- failures @ [{ Names = pd.Names; Reason = reason; Span = pd.Span }]
            | _ -> ()

        Ok (env, failures)

/// Convert a StaticValue to a printable string (for debugging)
let rec ppStaticValue (v: StaticValue) : string =
    match v with
    | SVInt n -> string n
    | SVFloat f -> sprintf "%g" f
    | SVInt32 n -> string n
    | SVFloat32 f -> sprintf "%g" f
    | SVBool b -> if b then "true" else "false"
    | SVString s -> $"\"{s}\""
    | SVUnit -> "()"
    | SVTuple vs -> $"""({(vs |> List.map ppStaticValue |> String.concat ", ")})"""
    | SVStruct (n, fs) ->
        $"""{n} {{ {(fs |> List.map (fun (fn, v) -> $"{fn} = {ppStaticValue v}") |> String.concat ", ")} }}"""
