/// Linear-algebra dispatch classification — Phase 5 of
/// docs/plan-cpp-perf-exploitation.md.
///
/// PURPOSE. CodeGen must never decide for itself what a loop nest "is" in BLAS
/// terms. This module owns that judgement: it maps an IR node onto a typed
/// `LinAlgCall` descriptor naming a routine, the operand roles, the dimensions
/// and the transpose flags, and it owns the POLICY table that says whether a
/// recognised shape is actually routed through the `blade_linalg.hpp` shim or
/// deliberately left to Blade's own loop nest. CodeGen consults; it does not
/// pattern-match BLAS shapes.
///
/// WHY A SEPARATE MODULE. Blade's uniform representation wraps every operation
/// — even a scalar `T^0 -> T^0 -> T^0` binop like `*` — in at least a trivial
/// loop nest by codegen time (`lowerArrayBinOpsModule` rewrites raw array
/// binops into combinator form), so outside the two nodes v1 handles there is
/// no distinguished "matmul node" to hook. Recognising the rest means matching
/// LOOP-NEST SHAPES, jointly on (kernel op x fold op x operand ranks x index
/// ties): the same `*` is Hadamard at equal ranks, `scal` against a scalar,
/// half of `dot` under a `(+)`-reduce, and `gemm` under a contraction tie. That
/// enumeration is per-op and per-level and wants its own home, its own tests,
/// and its own growth path — not another arm inside a 14k-line codegen file.
///
/// V1 SCOPE (node matching).
///   * `IRGram(l, r, sameArray)` -> Syrk (same array) / Gemm (distinct).
///   * `IRMatmul(a, b)`          -> Gemm.
/// Both are FIRST-CLASS IR NODES, so their classification is exact rather than
/// inferred; they need no nest matching and cannot be fooled by a coincidence
/// of shape.
///
/// PHASE 5b SCOPE (nest matching — the growth path, now live).
///   * `(|BlasL1|_|)` -> Dot:  a `reduce` over an UNFORCED deferred zip whose
///     kernel is the product of the two co-iterated rank-1 f64 leaves, folded
///     by the builtin `+`.
///   * `(|BlasL2|_|)` -> Gemv: a materialising per-row apply over ONE rank-2
///     f64 operand whose kernel body is `prodsum(<peeled row>, <rank-1 f64
///     vector>)`, producing a rank-1 f64 output.
/// Both are matched against `LoopNestCodeGen` — the fully-built nest — rather
/// than against the surface combinator tree, because that is the structure the
/// emission site is holding and the only one where "which array feeds which
/// kernel parameter at which level" is already resolved. The patterns are
/// consumed by one more `try*` in codegen's shortcircuit chain (precedent:
/// `tryGenFlatElementwiseNest` at the apply-combinator site), so the seam is
/// "BLAS match -> flat elementwise -> nested emitter", each arm falling through
/// on None.
///
/// WHAT THIS MODULE STILL DOES NOT DO: invent C++ identifiers. A nest-matched
/// descriptor names its operands by echoing a name `LoopNestCodeGen` already
/// holds, or by handing back the IR expression for CodeGen to resolve through
/// its own name map (`NestOperandSource`).
module Blade.LinAlgPatterns

open Blade.IR
open Blade.Types

// ============================================================================
// Availability gate
// ============================================================================

/// THE BLAS availability gate — the single source of truth, consulted by BOTH
/// `shimEntryPoint` below (which decides whether a program emits shim calls at
/// all) and `Build.fs` (which decides whether the g++ line carries
/// `-DBLADE_HAS_BLAS` plus the include/link flags). Two copies of this
/// predicate could disagree, and a disagreement is exactly the configuration
/// where a program emits `blade_linalg::` calls into a header that will not
/// compile. One definition, referenced twice.
///
///   BLADE_BLAS=1|on   -> force on
///   BLADE_BLAS=0|off  -> force off
///   unset             -> follow OPENBLAS_DIR (set = on)
///
/// Default-off is deliberate and unchanged: BLAS may differ in the last ULP,
/// and the interpreter/oracle differentials demand byte-identical output, so
/// Blade's own emitted loops remain the verification truth.
///
/// A FUNCTION, never a module-level `let`, for the reason `Build.optFlags`
/// became one: a module-level binding freezes the environment read at first
/// touch, which would make a mid-process pin (a test's use-guard, a hand-run)
/// silently ineffective. Every consultation re-reads.
///
/// ARCHITECTURE (Phase 5c, user-directed). Blade knows at ITS compile time
/// whether BLAS will be available, so the choice belongs here and not in a C++
/// `#ifdef`. Gate off => no route is emitted => the native math comes from the
/// PRE-EXISTING emission paths (gram/matmul's own loops, and for dot/gemv the
/// ordinary loop-nest emitters), which are the paths the interpreter
/// differential has always covered. That is why `blade_linalg.hpp` no longer
/// carries hand-written fallbacks: there is nothing for them to be a fallback
/// FOR, and a second copy of the same arithmetic is a byte-identity obligation
/// maintained by discipline rather than by construction.
let blasAvailable () : bool =
    match System.Environment.GetEnvironmentVariable("BLADE_BLAS") with
    | "1" | "on" -> true
    | "0" | "off" -> false
    | _ ->
        match System.Environment.GetEnvironmentVariable("OPENBLAS_DIR") with
        | null | "" -> false
        | _ -> true

// ============================================================================
// Descriptor
// ============================================================================

/// The BLAS level a routine sits at. Recorded on every policy row because the
/// routing decision is made per LEVEL, not per routine: L3 pays enormously
/// (blocking and microkernels are unreachable from emitted loops), L2 pays
/// modestly, L1-elementwise does not pay at all.
type BlasLevel =
    | L1
    | L2
    | L3

/// The routines this layer can name. `Dot`/`Gemv`/`Axpy`/`Scal` are declared —
/// not yet matched — so the policy table below can state their routing decision
/// explicitly rather than leaving it as an undocumented gap.
type LinAlgRoutine =
    /// C = A * B (general matrix product) — `blade_gemm`.
    | Gemm
    /// C = A * A^T, one triangle (symmetric rank-k update) — `blade_syrk`.
    | Syrk
    /// y = A * x (matrix-vector).
    | Gemv
    /// s = x . y (inner product).
    | Dot
    /// s = ||x||_2. Named so the policy table can state its decision; NOT
    /// matched in v1 (matching it means recognising a `sqrt` wrapped around a
    /// self-dot, which is a shape the classifier has no case for yet).
    | Nrm2
    /// y = alpha * x + y.
    | Axpy
    /// x = alpha * x.
    | Scal

/// Where a recognised shape is actually EXECUTED.
type Routing =
    /// Through `blade_linalg.hpp` — which itself resolves to cblas or to the
    /// contract-preserving native fallback depending on the BUILD.
    | ViaShim
    /// Deliberately left to Blade's own emitted loop nest. A `Native` row is a
    /// RECORDED DECISION ("matched but routed native"), not a missing feature.
    | Native

/// The role an operand plays in a call, kept separate from the IR expression so
/// the emission site can decide how to obtain the pointer (pool base, staged
/// copy, ...) without re-deriving what the operand IS.
type OperandRole =
    /// Left/first factor.
    | RoleA
    /// Right/second factor.
    | RoleB
    /// Result.
    | RoleC

/// The concrete `blade_linalg.hpp` adapter a routed call lands on. A ROUTE is
/// narrower than a routine: `gram(A, A)` and `gram(A, B)` are both rank-k
/// updates in BLAS terms but reach different entry points because Blade's
/// symmetric result is PACKED triangular storage while its general result is a
/// dense pool — a difference the shim (not codegen) has to absorb.
type LinAlgRoute =
    /// `blade_gram_same` — A·Aᵀ into packed upper-triangular symmetric storage.
    | RouteGramSame
    /// `blade_gram_distinct` — A·Bᵀ into a dense pool.
    | RouteGramDistinct
    /// `blade_matmul` — A·B into a dense pool.
    | RouteMatmul
    /// `blade_dot` — s = seed + x·y over two rank-1 pools.
    | RouteDot
    /// `blade_gemv` — y = A·x, row skeleton in, rank-1 pool out.
    | RouteGemv

/// An operand as classified: the IR expression, its role, and whether the call
/// consumes it transposed.
type LinAlgOperand = {
    Role: OperandRole
    Expr: IRExpr
    Transposed: bool
}

/// How the emission site obtains a pointer to a NEST-matched operand.
///
/// The distinction matters because a nest's operands reach C++ by three
/// different routes and this module must not guess which: an input array is
/// already named by `LoopNestCodeGen.InputArrayNames`, the output by
/// `OutputName`, but a value the kernel BODY references (a capture, or an
/// enclosing let-binding) has no name here at all — only CodeGen's name map
/// knows it, so the IR expression is handed back untouched.
type NestOperandSource =
    /// A loop-nest input array, under the name the nest already uses.
    | FromNestArray of name: string
    /// A value referenced by the kernel body. CodeGen resolves it through its
    /// own name map; a failure to resolve is a decline, not a guess.
    | FromKernelRef of expr: IRExpr
    /// The nest's freshly-allocated output array.
    | FromNestOutput of name: string

/// How the extents of a call are named. v1 always resolves them at RUNTIME off
/// the operands' `.extents[]`, exactly as the pre-shim emission did — the
/// descriptor records WHICH extent of WHICH operand, so the emission site
/// spells the accessor and this module stays free of C++ text.
type DimSource = {
    /// Index into the call's operand list.
    Operand: OperandRole
    /// Which axis of that operand.
    Axis: int
}

/// A classified linear-algebra call.
type LinAlgCall = {
    Routine: LinAlgRoutine
    Route: LinAlgRoute
    Level: BlasLevel
    Routing: Routing
    /// NODE-matched routes only (gram/matmul): the operands as IR expressions.
    /// Empty for nest-matched routes, which use `NestOperands` instead.
    Operands: LinAlgOperand list
    /// NEST-matched routes only (dot/gemv): where each operand's pointer comes
    /// from, in the shim call's own argument order. Empty for node routes.
    NestOperands: (OperandRole * NestOperandSource) list
    /// Rows of the result. `None` for the nest routes, whose extents come from
    /// the built loop nest (`genLoopBoundExpr`) rather than from an operand
    /// axis this module could name — recording a made-up axis would be worse
    /// than recording nothing.
    M: DimSource option
    /// Columns of the result (for Syrk this equals M).
    N: DimSource option
    /// The contracted extent.
    K: DimSource option
    /// Element type of the contraction. v1 shim routes require Float64.
    ElemType: IRType
    /// True when the result is written into Blade's packed triangular
    /// (symmetric) storage rather than a dense pool.
    PackedTriangularResult: bool
}

// ============================================================================
// Policy table
// ============================================================================

/// The routing policy, stated once and explicitly.
///
/// L1-ELEMENTWISE STAYS NATIVE. `axpy`/`scal` shapes are bandwidth-bound and
/// the flat elementwise loop (Phase 3) already vectorises them; a BLAS call
/// boundary buys nothing and costs a function call plus a staging decision.
/// L1 REDUCTION shapes (`dot`, `nrm2`) and all of L2/L3 are the paying routes,
/// with L3 >> L2 because blocking and microkernels are simply unreachable from
/// generated loop code.
///
/// A row exists for every routine this module can NAME, including the ones it
/// cannot yet MATCH, so "matched but routed native" and "not yet matched" are
/// distinguishable by reading one table.
let policy : (LinAlgRoutine * BlasLevel * Routing * string) list =
    [ Gemm, L3, ViaShim,
      "the one shape emitted loop code cannot approach; blocking/microkernels pay by orders of magnitude"
      Syrk, L3, ViaShim,
      "same as gemm, and it halves the work by computing one triangle — which is also Blade's storage"
      Gemv, L2, ViaShim,
      "pays modestly (bandwidth-bound but cache-blocked); MATCHED (Phase 5b) on the per-row prodsum-fiber nest"
      Dot,  L1, ViaShim,
      "an L1 REDUCTION, unlike axpy/scal: the serial FP chain is the bottleneck and BLAS breaks it; MATCHED (Phase 5b) on reduce-over-deferred-zip-product. PRECEDENCE: an `omp`-licensed fold kernel WINS — an explicit user reorder licence beats a dispatch heuristic, and under no-BLAS this route's fallback is serial, so firing would silently strip licensed parallelism"
      Nrm2, L1, ViaShim,
      "same paying L1-reduction argument as dot, but NOT MATCHED in v1: recognising it means seeing a `sqrt` wrapped around a SELF-dot, and no sqrt-shape case exists in the classifier yet"
      Axpy, L1, Native,
      "bandwidth-bound elementwise; the Phase 3 flat loop already vectorises it, so a call boundary is pure loss"
      Scal, L1, Native,
      "same as axpy — an elementwise scale is one vectorised pass either way" ]

/// The routing decision for a routine, from the table above.
let routingOf (r: LinAlgRoutine) : Routing =
    policy
    |> List.tryPick (fun (rr, _, routing, _) -> if rr = r then Some routing else None)
    |> Option.defaultValue Native

/// The BLAS level of a routine, from the table above.
let levelOf (r: LinAlgRoutine) : BlasLevel =
    policy
    |> List.tryPick (fun (rr, lvl, _, _) -> if rr = r then Some lvl else None)
    |> Option.defaultValue L1

/// The C++ entry point a routed call lands on. Kept here (not in CodeGen) so
/// the name of every shim function this compiler can emit is enumerable from
/// one place — the same reason `runtimeHeaderNames` is a single source of truth.
///
/// THE AVAILABILITY GATE IS CONSULTED HERE, AND ONLY HERE (Phase 5c). Every
/// route — the two node classifications and both nest patterns — funnels
/// through this function on its way to emitted text, so one conjunct at this
/// point disables dispatch globally. Deliberately NOT folded into `routingOf`:
/// that field is POLICY ("is this shape worth a BLAS call at all"), which is a
/// property of the routine and is pinned by tests that must not depend on
/// whether OpenBLAS happens to be installed. Availability is a property of the
/// BUILD. Keeping them separate is what lets the policy table stay a readable,
/// environment-independent statement while the gate stays a one-line
/// conjunction.
///
/// A call that classifies but gets no entry point is a DECLINED DISPATCH, and
/// all four emission sites already spell that case: gram and matmul fall to
/// their own scalar loops, dot and gemv fall through to the ordinary loop-nest
/// emitters. So "gate off" and "shape not recognised" reach the same, already
/// exercised, code.
let shimEntryPoint (call: LinAlgCall) : string option =
    if not (blasAvailable ()) then None else
    match call.Routing with
    | Native -> None
    | ViaShim ->
        match call.Route with
        | RouteGramSame -> Some "blade_linalg::blade_gram_same"
        | RouteGramDistinct -> Some "blade_linalg::blade_gram_distinct"
        | RouteMatmul -> Some "blade_linalg::blade_matmul"
        | RouteDot -> Some "blade_linalg::blade_dot"
        | RouteGemv -> Some "blade_linalg::blade_gemv"

// ============================================================================
// Classification entry points (v1)
// ============================================================================

/// v1 shim routes are real-Float64 only: `dsyrk`/`dgemm` and their native
/// fallbacks. Complex (`zherk`/`zgemm`) and float32 (`ssyrk`/`sgemm`) keep the
/// compiler's scalar loops — the SAME restriction the pre-shim BLAS lowering
/// carried, so this changes nothing about which programs use which arithmetic.
let private isRealDouble (t: IRType) =
    match t with
    | IRTScalar ETFloat64 -> true
    | _ -> false

let private elemOf (e: IRExpr) : IRType option =
    match typeOf e with
    | ArrayElem a -> Some a.ElemType
    | _ -> None

/// Classify `gram(l, r)`.
///
///   sameArray -> Syrk: square m x m written into PACKED upper-triangular
///                symmetric storage (Blade's own layout for the result type).
///   distinct  -> Gemm with B transposed: C(m x p) = A(m x n) * B(p x n)^T,
///                dense result.
///
/// Returns None (→ caller keeps its scalar loops) when either operand is not a
/// real-Float64 array.
let classifyGram (l: IRExpr) (r: IRExpr) (sameArray: bool) : LinAlgCall option =
    match elemOf l, elemOf r with
    | Some le, Some re when isRealDouble le && isRealDouble re ->
        if sameArray then
            Some { Routine = Syrk
                   Route = RouteGramSame
                   Level = L3
                   Routing = routingOf Syrk
                   Operands = [ { Role = RoleA; Expr = l; Transposed = false } ]
                   NestOperands = []
                   // C is m x m from A's leading axis; the contracted extent is
                   // A's trailing axis.
                   M = Some { Operand = RoleA; Axis = 0 }
                   N = Some { Operand = RoleA; Axis = 0 }
                   K = Some { Operand = RoleA; Axis = 1 }
                   ElemType = le
                   PackedTriangularResult = true }
        else
            Some { Routine = Gemm
                   Route = RouteGramDistinct
                   Level = L3
                   Routing = routingOf Gemm
                   Operands = [ { Role = RoleA; Expr = l; Transposed = false }
                                { Role = RoleB; Expr = r; Transposed = true } ]
                   NestOperands = []
                   M = Some { Operand = RoleA; Axis = 0 }
                   N = Some { Operand = RoleB; Axis = 0 }
                   K = Some { Operand = RoleA; Axis = 1 }
                   ElemType = le
                   PackedTriangularResult = false }
    | _ -> None

/// Classify `matmul(a, b)`: C(m x n) = A(m x k) * B(k x n), dense result, no
/// transposes. The first-class intrinsic's only classification.
let classifyMatmul (a: IRExpr) (b: IRExpr) : LinAlgCall option =
    match elemOf a, elemOf b with
    | Some ae, Some be when isRealDouble ae && isRealDouble be ->
        Some { Routine = Gemm
               Route = RouteMatmul
               Level = L3
               Routing = routingOf Gemm
               Operands = [ { Role = RoleA; Expr = a; Transposed = false }
                            { Role = RoleB; Expr = b; Transposed = false } ]
               NestOperands = []
               M = Some { Operand = RoleA; Axis = 0 }
               N = Some { Operand = RoleB; Axis = 1 }
               K = Some { Operand = RoleA; Axis = 1 }
               ElemType = ae
               PackedTriangularResult = false }
    | _ -> None

/// The single entry point CodeGen calls: classify whatever node it is holding.
/// Returns None for everything this layer does not (yet) recognise, which is
/// the caller's signal to emit its ordinary loop nest.
let classify (e: IRExpr) : LinAlgCall option =
    match e with
    | IRGram (l, r, sameArray) -> classifyGram l r sameArray
    | IRMatmul (a, b) -> classifyMatmul a b
    | _ -> None

// ============================================================================
// Nest matching (Phase 5b) — shared shape predicates
// ============================================================================

/// Is this index slot an ORDINARY dense axis: one index component, no
/// symmetry, no reserved kind, no reserved tag, no dependence on an outer
/// loop index?
///
/// Every one of those five refusals is load-bearing for a BLAS route:
/// symmetry means the pool is a packed triangle rather than a rectangle; a
/// reserved `IxKind` (compound / sparse / ragged / dep / group / orbit) means
/// the axis iterates something other than `[0, extent)`; a `__`-prefixed tag
/// marks a halo window or kind sentinel; and a dependence makes the level
/// triangular. BLAS knows about none of these.
let private isPlainDenseAxis (ix: IRIndexType) =
    ix.Rank = 1
    && ix.Symmetry = SymNone
    && ix.IxKind = IxKPlain
    && ix.Dependencies.IsEmpty
    && (match ix.Tag with Some t -> not (t.StartsWith "__") | None -> true)

/// A real (non-virtual) f64 array of exactly `rank` ordinary dense axes.
/// Virtual operands are refused because a `range`/`reverse` view has no pool
/// to point at — it inlines into index arithmetic at every use.
let private isDenseF64OfRank (rank: int) (t: IRArrayType) =
    not t.IsVirtual
    && isRealDouble t.ElemType
    && List.length t.IndexTypes = rank
    && t.IndexTypes |> List.forall isPlainDenseAxis

/// A rank-1 f64 operand that is only ever READ elementwise — never iterated as
/// a loop level and never peeled. `IxKIrreps` is admitted HERE and nowhere
/// else: an irreps axis is a block-structured but ordinary contiguous dense
/// axis (the same judgement IR's dense-rank-1-factor rule already makes, where
/// `IxKPlain` and `IxKIrreps` are the two accepted kinds), so `v.data[t]` is
/// the identical object `v[t]` denotes. That is the whole of what the shared
/// vector of a gemv needs, and it is what the real corpus shape uses
/// (`ml-equiv/018`, `019`: `prodsum(row, fx)` with `fx : Array<Float like
/// IrrepsIdx<...>>`).
///
/// It is deliberately NOT admitted for an array the nest ITERATES (dot's two
/// operands, gemv's matrix, gemv's output): those positions decide loop bounds
/// and peel structure, where the extra tag is a difference this classifier has
/// not established is inert.
let private isReadOnlyF64Vector (t: IRArrayType) =
    not t.IsVirtual
    && isRealDouble t.ElemType
    && (match t.IndexTypes with
        | [ ix ] ->
            ix.Rank = 1
            && ix.Symmetry = SymNone
            && (ix.IxKind = IxKPlain || ix.IxKind = IxKIrreps)
            && ix.Dependencies.IsEmpty
        | _ -> false)

/// The one loop level of a depth-1 nest, provided it really iterates
/// `[0, extent)`: rectangular (no bound dependencies, no strict offset) and
/// not a fused joint level (whose bound is a PRODUCT of source extents, i.e.
/// a different iteration space than the one the routine's `m`/`n` describe).
let private singleRectangularLevel (cg: LoopNestCodeGen) : LoopIndexBinding option =
    match cg.Bindings with
    | [ b ] when b.BoundDependencies.IsEmpty && b.StrictOffset = 0 && b.FusedRank.IsNone ->
        Some b
    | _ -> None

/// Gates that hold for EVERY nest-matched route: the nest must be an ordinary
/// serial traversal of a real pool, with none of the modes that change what the
/// loop body means or where it runs.
///
/// `HasReynolds`/`IsAntisymmetric` — the body reads PERMUTED coordinates.
/// `MpiSlab` — the outer level iterates a rank slab, not the whole extent.
/// A streamed source has no materialised pool at all.
let private nestModeOk (streamedCount: int) (cg: LoopNestCodeGen) =
    streamedCount = 0
    && not cg.MpiSlab
    && not cg.HasReynolds
    && not cg.IsAntisymmetric

// ============================================================================
// (|BlasL1|_|) — dot
// ============================================================================

/// What the caller must tell the L1 pattern about the FOLD, which lives on the
/// reduce node rather than on the nest.
type DotFoldFacts = {
    /// The fold kernel's body is exactly the builtin `+` over its two
    /// parameters (`CodeGen.foldKernelBuiltinOp` = `Some IRAdd`). Anything else
    /// — a user lambda, `*`, `max` — is a different accumulation and `blade_dot`
    /// would compute the wrong thing.
    FoldIsBuiltinAdd: bool
    /// The fold kernel carried `where ... omp`. See the PRECEDENCE note below.
    FoldRequestedOmp: bool
}

/// `s = reduce(<unforced zip of x and y under `*`>, (+))`  ->  `blade_dot`.
///
/// SHAPE MATCHED, exactly:
///   * depth-1 rectangular nest accumulating through a fold wrapper
///     (`FoldWrapper.IsSome`), i.e. the reduce-over-deferred-computation path;
///   * exactly TWO input arrays, both real f64 of ONE ordinary dense axis;
///   * the single level carries exactly two element bindings, one per operand
///     position, each a real full-depth scalar peel (`ArrayRank = 1`,
///     `RankComponent = 0`) at that level;
///   * the kernel body is exactly `p_a * p_b` over those two peel parameters,
///     `p_a` and `p_b` distinct — so no capture, no index variable and no
///     third term can appear in it;
///   * the fold kernel is the builtin `+`.
///
/// PRECEDENCE (fixed by design; see the `Dot` policy row). If the fold kernel
/// is `omp`-licensed, this pattern DECLINES and Phase 2's chunked parallel fold
/// keeps the nest. An explicit user reorder licence outranks a dispatch
/// heuristic, and it has to: in a build without BLAS this route's fallback is a
/// serial loop, so firing here would silently convert licensed parallelism into
/// serial code with nothing in the emitted text to show for it.
///
/// The SEED is not part of the match. `reduce`'s seed (the implicit `(+)`
/// identity, or a user `init`) is passed through to the shim, whose native
/// fallback starts its accumulator from it — which is what makes the fallback
/// byte-identical to the loop for ANY seed rather than only for `0.0`.
let (|BlasL1|_|) ((streamedCount, facts, operandTypes, cg): int * DotFoldFacts * IRArrayType list * LoopNestCodeGen)
        : LinAlgCall option =
    if not facts.FoldIsBuiltinAdd then None
    elif facts.FoldRequestedOmp then None          // precedence: Phase 2 wins
    elif cg.FoldWrapper.IsNone || cg.FoldChunk.IsSome then None
    elif not (nestModeOk streamedCount cg) then None
    else
    match singleRectangularLevel cg with
    | None -> None
    | Some level ->
        let names = cg.InputArrayNames
        if List.length names <> 2 then None
        // Both operands: real f64, ONE ordinary dense axis, non-virtual. This
        // is what rules out the rank-1 axes that are not `[0, extent)` sweeps
        // over a plain pool — sparse/compound key spaces, dependent and ragged
        // axes, orbit classes — none of which `blade_dot`'s pointer pair can
        // describe. (`operandTypes` is positionally parallel to
        // `InputArrayNames`; the caller passes them together for that reason.)
        elif List.length operandTypes <> 2 then None
        elif not (operandTypes |> List.forall (isDenseF64OfRank 1)) then None
        else
        match level.Elements with
        | [ e0; e1 ] when e0.ArrayPosition <> e1.ArrayPosition ->
            let peelOk (e: ElementBinding) =
                (match e.Virtual with RealArray -> true | _ -> false)
                && e.RankComponent = level.Level
                && e.ArrayRank = 1
                && e.DimIndex = 0
                && e.ArrayPosition >= 0 && e.ArrayPosition < 2
                && e.ArrayName = List.item e.ArrayPosition names
                && (match e.SlotTag with Some t -> not (t.StartsWith "__") | None -> true)
                && isRealDouble e.ArrayElemType
            if not (peelOk e0 && peelOk e1) then None else
            // Body is exactly `<peel> * <peel>` over the two distinct params.
            match cg.KernelExpr with
            | IRBinOp (_, IRMul, IRVar (lId, _), IRVar (rId, _)) when lId <> rId ->
                let byParam id =
                    [ e0; e1 ] |> List.tryFind (fun e -> e.ParamVarId = id)
                match byParam lId, byParam rId with
                | Some le, Some re ->
                    Some { Routine = Dot
                           Route = RouteDot
                           Level = L1
                           Routing = routingOf Dot
                           Operands = []
                           NestOperands = [ RoleA, FromNestArray le.ArrayName
                                            RoleB, FromNestArray re.ArrayName ]
                           M = None; N = None; K = None
                           ElemType = le.ArrayElemType
                           PackedTriangularResult = false }
                | _ -> None
            | _ -> None
        | _ -> None

// ============================================================================
// (|BlasL2|_|) — gemv
// ============================================================================

/// `y = method_for(A) <@> lambda(row) -> prodsum(row, x) |> compute`
///   ->  `blade_gemv`.
///
/// WHY THIS SHAPE. It is how matrix-vector actually appears in Blade programs:
/// `prodsum` is a first-class fused product-sum over rank-1 fibers, and the
/// per-row apply is the only way to reach it with a matrix. The corpus writes
/// it verbatim (`ml-equiv/018_certificate_derive_linear.blade`,
/// `ml-equiv/019_certificate_derive_tp.blade`). There is no `matvec` keyword
/// and no rank-2/rank-1 contraction node to hook instead.
///
/// SHAPE MATCHED, exactly:
///   * a depth-1 rectangular MATERIALISING nest (no fold wrapper);
///   * exactly ONE input array, real f64, of TWO ordinary dense axes;
///   * that level carries exactly ONE element binding: a real peel of dim 0 of
///     a rank-2 array — `ArrayRank (2) > depth (1)` is precisely what makes it
///     a FIBER argument rather than a scalar leaf, and it is what separates
///     this from the Phase 3 flat-elementwise shape;
///   * the output is a real f64 array of ONE ordinary dense axis;
///   * the kernel body is exactly `prodsum(<the peeled row>, <v>)` with the
///     peeled row FIRST and exactly two arguments, `v` an `IRVar` of a real f64
///     array with one ordinary dense axis.
///
/// WHY THE ROW MUST COME FIRST. `IRProdSum` takes its loop bound from its FIRST
/// operand (`CodeGen`'s renderer: `<arg0>.extents[0]`). With the row first that
/// bound is A's trailing extent, which is the `n` this routine is defined over.
/// Reversed (`prodsum(x, row)`) the emitted loop would be bounded by the
/// VECTOR's extent instead — a different iteration count whenever the two
/// disagree, which Blade's unify does not rule out. Declining is the honest
/// answer; the reversed form keeps its loop.
///
/// PRECEDENCE, same rule as dot: an `omp` request on the row kernel declines.
/// A parallel row map is exactly what `#pragma omp parallel for` over the outer
/// level already gives, and the no-BLAS fallback here is serial.
let (|BlasL2|_|) ((streamedCount, ompRequested, operandTypes, cg): int * bool * IRArrayType list * LoopNestCodeGen)
        : LinAlgCall option =
    if cg.FoldWrapper.IsSome || cg.FoldChunk.IsSome then None
    elif ompRequested then None                    // precedence: keep the pragma
    elif not (nestModeOk streamedCount cg) then None
    else
    match singleRectangularLevel cg, cg.InputArrayNames, operandTypes with
    | Some level, [ aName ], [ aTy ] when isDenseF64OfRank 2 aTy ->
        // Output: rank-1 dense real f64.
        let outOk =
            match cg.OutputType with
            | ArrayElem outTy -> isDenseF64OfRank 1 outTy
            | _ -> false
        if not outOk then None else
        match level.Elements with
        | [ e ] when (match e.Virtual with RealArray -> true | _ -> false)
                     && e.ArrayPosition = 0
                     && e.ArrayName = aName
                     && e.RankComponent = level.Level
                     && e.ArrayRank = 2
                     && e.DimIndex = 0
                     && isRealDouble e.ArrayElemType
                     && (match e.SlotTag with Some t -> not (t.StartsWith "__") | None -> true) ->
            (match cg.KernelExpr with
             | IRProdSum [ IRVar (rowId, _); (IRVar (vecId, _) as vecExpr) ] when rowId = e.ParamVarId ->
                // The second operand must be a value from OUTSIDE the kernel —
                // a capture or an enclosing binding — never a kernel parameter.
                // `prodsum(er, er)` (the per-row SELF-dot the math corpus uses
                // for row norms) matches everything above and is emphatically
                // not a matrix-vector product: routing it would pass the peeled
                // row as `x` for every row. This is the guard that separates
                // "one shared vector" from "the row against itself".
                let isKernelParam id =
                    cg.KernelParams |> List.exists (fun (p: IRParam) -> p.VarId = id)
                let vecOk =
                    vecId <> rowId
                    && not (isKernelParam vecId)
                    && (match typeOf vecExpr with
                        | ArrayElem vt -> isReadOnlyF64Vector vt
                        | _ -> false)
                if not vecOk then None
                else
                    Some { Routine = Gemv
                           Route = RouteGemv
                           Level = L2
                           Routing = routingOf Gemv
                           Operands = []
                           NestOperands = [ RoleA, FromNestArray aName
                                            RoleB, FromKernelRef vecExpr
                                            RoleC, FromNestOutput cg.OutputName ]
                           M = None; N = None; K = None
                           ElemType = e.ArrayElemType
                           PackedTriangularResult = false }
             | _ -> None)
        | _ -> None
    | _ -> None

// ============================================================================
// Still planned — L3 nest matching (skeleton only)
// ============================================================================
//
// let (|BlasL3|_|) (...) : LinAlgCall option = ...
//
// Left as a comment rather than as a `None`-returning stub on purpose: a stub
// that always declines is indistinguishable at the call site from a pattern
// that has been implemented and simply did not match, which is exactly the
// confusion the policy table exists to prevent. L3 nest matching (a genuine
// three-level contraction written as loops, rather than the `matmul`/`gram`
// NODES v1 already routes) arrives with its first real case.
//
// PACKED-SYMMETRIC (dspmv) — the shim entry `blade_symv` EXISTS and its layout
// premise is PROVEN (see `blade_linalg.hpp` and the plan's Phase 5b section:
// Blade's rank-2 sym-compact DFS pool order is byte-for-byte BLAS row-major
// UPPER packed order, so the route needs zero staging). There is deliberately
// no pattern for it, because no Blade surface form can currently produce a
// sym-compact matvec: peeling a rank-2 compact group into rank-1 fibers is
// refused at typecheck (BL4004 — "a rank-k compact group is ONE index slot
// covering k dimensions"), and `reduce`/`prodsum` over compact storage is
// refused as well. `decompact` first, and the operand is dense — which is the
// gemv route above. The entry point is the route waiting for a surface.
