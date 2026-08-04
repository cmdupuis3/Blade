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

/// THE LAPACK availability gate (Phase 6 / Round B). Rides the SAME
/// environment resolution as `blasAvailable` — OpenBLAS bundles LAPACKE, so
/// one install answers both — but it is a SEPARATE FUNCTION with a separate
/// C++ define (`-DBLADE_HAS_LAPACK`) and its own Build.fs include-sniff arm.
///
/// Why separate rather than an alias: the two headers are independently
/// includable, so a BLAS-only program and a LAPACK-carrying one must stay
/// distinguishable at the g++ line — otherwise every gram/matmul program would
/// start advertising a LAPACK dependency it does not have. Keeping the
/// predicate its own function is also what lets the LAPACK gate be tightened
/// later (a LAPACKE-less BLAS install, a separate LAPACK_DIR) without touching
/// the BLAS one.
///
/// A FUNCTION, never a module-level `let`, for the same reason as its sibling.
///
/// NUMERICS WARNING, recorded here because this is the gate that turns it on:
/// unlike the BLAS routes — which differ from Blade's loops only in the last
/// ULP — an eigensolver's OUTPUT IS NOT UNIQUE (eigenvector signs are
/// arbitrary; within a degenerate eigenvalue's subspace any orthonormal basis
/// is correct). The shim normalises the two determinate parts (descending
/// order, sign fix) but cannot normalise the basis choice. So gate-ON results
/// are correct and NOT bit-reproducible against the native Jacobi path, and
/// `interp` / `diff-oracle` must never run with this gate set.
let lapackAvailable () : bool =
    match System.Environment.GetEnvironmentVariable("BLADE_BLAS") with
    | "1" | "on" -> true
    | "0" | "off" -> false
    | _ ->
        match System.Environment.GetEnvironmentVariable("OPENBLAS_DIR") with
        | null | "" -> false
        | _ -> true

// ============================================================================
// Backend mode
// ============================================================================

/// The codegen EMISSION MODE a dispatch decision is being made for.
///
/// USER-DIRECTED ARCHITECTURE. The alternative — copying the whole binop /
/// nest-shape dispatch apparatus once per target — is brittle: the classifiers
/// would drift apart the first time a pattern is fixed on one side and not the
/// other, and the shapes they recognise are a property of Blade's IR, not of
/// any backend. So the CLASSIFIERS ARE MODE-AGNOSTIC (nothing below
/// `classifyGram` / `(|BlasL1|_|)` / `(|BlasL2|_|)` mentions a backend) and the
/// mode is threaded into the ONE funnel that turns a classification into a C++
/// name: `shimEntryPoint`. Adding a target is then a new arm there plus its
/// entry-point table, never a second copy of the matching logic.
type LinAlgBackend =
    /// Host CPU, cblas through `blade_linalg.hpp`. Availability is
    /// `blasAvailable ()` (BLADE_BLAS / OPENBLAS_DIR).
    | HostBlas
    /// Device, cuBLAS. DECLARED, NOT IMPLEMENTED — `shimEntryPoint` returns
    /// None for every route under this mode, so a caller that asks for it emits
    /// its ordinary host loops rather than anything wrong.
    ///
    /// What the row is holding a place for (see the plan's CUDA notes):
    ///   * availability will be `cudaEmitModeEnabled () && <cuBLAS resolved>`,
    ///     the same shape as the host gate but with two conjuncts;
    ///   * cuBLAS is COLUMN-MAJOR with no row-major mode, so `C = A·B`
    ///     row-major is emitted as `gemm(B, A)` with the operands SWAPPED and
    ///     the result read back as its own transpose — a per-route rewrite that
    ///     belongs in the entry-point table, not in the classifiers;
    ///   * the LAPACK-level sibling is cuSOLVER, not "cuLAPACK";
    ///   * a cuBLAS handle is a per-program resource (create once, destroy at
    ///     exit), so the emission site needs a program-level prologue slot that
    ///     the host path has no analogue of.
    | CudaBlas

// ============================================================================
// Precision
// ============================================================================

/// The BLAS precision letter an element type dispatches to. This is the second
/// axis of the (routine × precision × symmetry) matrix: the routine FAMILY is
/// chosen by symmetry (see `LinAlgRoute`), the letter by this.
///
/// Everything not listed — integers, booleans, structs, unit-annotated
/// scalars — has no BLAS analogue at all and answers None, which is a decline
/// at every classifier.
type Precision =
    /// float32 — `s` routines.
    | PrecS
    /// float64 — `d` routines.
    | PrecD
    /// complex<float> — `c` routines.
    | PrecC
    /// complex<double> — `z` routines.
    | PrecZ

/// The BLAS precision of an element type, or None when the type has no routine
/// family. Replaces the old boolean `isRealDouble`: a boolean could only ever
/// answer "is this the one type we support", which stops being the right
/// question the moment a second precision is routed.
let precisionOf (t: IRType) : Precision option =
    match t with
    | IRTScalar ETFloat32 -> Some PrecS
    | IRTScalar ETFloat64 -> Some PrecD
    | IRTScalar ETComplex64 -> Some PrecC
    | IRTScalar ETComplex128 -> Some PrecZ
    | _ -> None

/// The one-letter cblas prefix, used to build entry-point names.
let precisionLetter (p: Precision) : string =
    match p with
    | PrecS -> "s"
    | PrecD -> "d"
    | PrecC -> "c"
    | PrecZ -> "z"

/// Is this precision COMPLEX? The distinction is load-bearing rather than
/// cosmetic: for a same-array gram it selects `herk` over `syrk` (Blade's
/// complex gram is HERMITIAN — its scalar loop already conjugates the second
/// factor), and for an inner product it is the reason the route must be
/// `dotu` and never `dotc`.
let isComplexPrecision (p: Precision) : bool =
    match p with
    | PrecC | PrecZ -> true
    | PrecS | PrecD -> false

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
    /// C = A * A^T (real) or A * A^H (complex), one triangle — `blade_syrk`.
    /// The complex instance is a HERMITIAN rank-k update (`cherk`/`zherk`), not
    /// a symmetric one: see `LinAlgRoute.RouteGramSame`.
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
    /// Symmetric / Hermitian eigendecomposition — LAPACK, not BLAS.
    /// `?spev`/`?hpev` for packed operands, `?syev`/`?heev` for dense.
    | Eigh

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
    /// `blade_gram_same_<p>` — one triangle into Blade's PACKED upper-triangular
    /// storage.
    ///
    /// REAL:    C = A·Aᵀ, a symmetric rank-k update  (`ssyrk` / `dsyrk`).
    /// COMPLEX: C = A·A^H, a HERMITIAN rank-k update (`cherk` / `zherk`).
    ///
    /// The complex case is not a naming quibble. Blade's own scalar loop for a
    /// complex gram accumulates `A[i][k] * conj_scalar(A[j][k])` — conjugating
    /// the SECOND factor — which is exactly A·A^H and exactly what `herk`
    /// computes. Binding the complex instance to `zsyrk` (the name-symmetric
    /// choice) would silently compute a different matrix, and would also
    /// disagree with the Hermitian storage class Blade deduces for the result.
    | RouteGramSame
    /// `blade_gram_distinct_<p>` — into a dense pool.
    ///
    /// REAL:    C = A·Bᵀ  (`sgemm`/`dgemm`, transB = Trans).
    /// COMPLEX: C = A·B^H (`cgemm`/`zgemm`, transB = **ConjTrans**), because
    /// the scalar loop conjugates B's element exactly as in the same-array case.
    | RouteGramDistinct
    /// `blade_matmul_<p>` — A·B into a dense pool. f64 only at the SURFACE
    /// (`TypeCheck.inferMatmul`), so only the `d` instance is reachable today;
    /// widening the surface is a separate language decision.
    | RouteMatmul
    /// `blade_dot_<p>` — s = seed + x·y over two rank-1 pools.
    ///
    /// COMPLEX IS `dotu`, NEVER `dotc`. The matched shape is
    /// `reduce(zip(x, y) under *, (+))`, whose kernel is `a * b` with NO
    /// conjugation anywhere. `dotc` computes Σ conj(x_i)·y_i and would silently
    /// return a different number — most visibly for `dot(x, x)`, where `dotc`
    /// gives the real squared norm and Blade's fold gives the complex Σ x_i².
    /// A conjugating inner product is a DIFFERENT operation and needs its own
    /// surface form (a `prodsum` with an explicit `conj`, matched as its own
    /// pattern); it is deliberately not implemented here.
    | RouteDot
    /// `blade_gemv_<p>` — y = A·x, row skeleton in, rank-1 pool out. The matched
    /// body is `prodsum(row, x)`, which conjugates nothing, so every precision
    /// uses `CblasNoTrans` — including the complex ones.
    | RouteGemv
    /// `blade_eigh_packed_<p>` — eigendecomposition of a rank-2 COMPACT
    /// operand, straight off `pool_base`. Real is `?spev`; complex is
    /// `?hpev` (Hermitian). THE ZERO-CONVERSION ROUTE: Blade's row-major-upper
    /// packed pool IS col-major-lower packed for a real symmetric matrix
    /// (measured n = 1..6), so LAPACK reads it as it stands.
    | RouteEighPacked
    /// `blade_eigh_dense_<p>` — eigendecomposition of a dense rank-2 operand
    /// asserted symmetric (real, `?syev`) or Hermitian (complex, `?heev`).
    | RouteEighDense

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
    // NOTE: there is deliberately NO `Routing` field. Routing is a function of
    // (routine × BACKEND), and this descriptor is produced by mode-agnostic
    // classifiers — carrying a mode-dependent answer on a mode-independent
    // record is exactly the inconsistency the backend DU exists to prevent.
    // `shimEntryPoint` reads `routingOf backend call.Routine` instead.
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
    /// Element type of the contraction, as classified.
    ElemType: IRType
    /// `ElemType`'s BLAS precision — the second dispatch axis. Filled by every
    /// classifier and READ by `shimEntryPoint` to pick the routine letter, so a
    /// call can never reach an entry point of the wrong width.
    Precision: Precision
    /// True when the result is written into Blade's packed triangular
    /// (symmetric or Hermitian) storage rather than a dense pool.
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
/// A row exists for every (routine × BACKEND) pair this module can NAME,
/// including the ones it cannot yet MATCH or EMIT, so "matched but routed
/// native", "not yet matched" and "backend not implemented" are all
/// distinguishable by reading one table.
let private cudaPending =
    "CudaBlas is DECLARED, NOT IMPLEMENTED: every route routes Native under this mode, so a caller asking for it emits its ordinary host loops rather than anything wrong. See LinAlgBackend.CudaBlas for what landing it requires (column-major operand swap, cuSOLVER for the LAPACK level, per-program handle lifecycle)"

let policy : (LinAlgRoutine * BlasLevel * LinAlgBackend * Routing * string) list =
    [ Gemm, L3, HostBlas, ViaShim,
      "the one shape emitted loop code cannot approach; blocking/microkernels pay by orders of magnitude"
      Syrk, L3, HostBlas, ViaShim,
      "same as gemm, and it halves the work by computing one triangle — which is also Blade's storage. COMPLEX instances are HERMITIAN (cherk/zherk), matching what Blade's own complex loop already computes"
      Gemv, L2, HostBlas, ViaShim,
      "pays modestly (bandwidth-bound but cache-blocked); MATCHED (Phase 5b) on the per-row prodsum-fiber nest"
      Dot,  L1, HostBlas, ViaShim,
      "an L1 REDUCTION, unlike axpy/scal: the serial FP chain is the bottleneck and BLAS breaks it; MATCHED (Phase 5b) on reduce-over-deferred-zip-product. COMPLEX instances are dotu, NEVER dotc (Blade's fold does not conjugate). PRECEDENCE: an `omp`-licensed fold kernel WINS — an explicit user reorder licence beats a dispatch heuristic, and under no-BLAS this route's fallback is serial, so firing would silently strip licensed parallelism"
      Nrm2, L1, HostBlas, ViaShim,
      "same paying L1-reduction argument as dot, but NOT MATCHED: recognising it means seeing a `sqrt` wrapped around a SELF-dot, and no sqrt-shape case exists in the classifier yet"
      Axpy, L1, HostBlas, Native,
      "bandwidth-bound elementwise; the Phase 3 flat loop already vectorises it, so a call boundary is pure loss"
      Scal, L1, HostBlas, Native,
      "same as axpy — an elementwise scale is one vectorised pass either way"
      Eigh, L3, HostBlas, ViaShim,
      "LAPACK, not BLAS: an eigensolver is unreachable from emitted loop code at any quality, and Blade's synthesized cyclic Jacobi is O(sweeps·n^3) against LAPACK's blocked tridiagonal reduction. Gated separately (lapackAvailable / -DBLADE_HAS_LAPACK) and PERMANENTLY outside byte-identity: eigenvector sign and degenerate-subspace basis are not unique"
      Gemm, L3, CudaBlas, Native, cudaPending
      Syrk, L3, CudaBlas, Native, cudaPending
      Gemv, L2, CudaBlas, Native, cudaPending
      Dot,  L1, CudaBlas, Native, cudaPending
      Nrm2, L1, CudaBlas, Native, cudaPending
      Axpy, L1, CudaBlas, Native, cudaPending
      Scal, L1, CudaBlas, Native, cudaPending
      // The device eigensolver sibling is cuSOLVER (`cusolverDnDsyevd` /
      // `cusolverDnZheevd`), NOT cuBLAS and not "cuLAPACK" — a separate
      // library, a separate handle, and a workspace-query protocol cuBLAS has
      // no analogue of. Declared Native for the same reason as the rows above.
      Eigh, L3, CudaBlas, Native,
      "cuSOLVER (cusolverDnDsyevd / cusolverDnZheevd), not cuBLAS: separate library, separate handle, explicit workspace query. " + cudaPending ]

/// The routing decision for a routine UNDER A BACKEND, from the table above.
let routingOf (backend: LinAlgBackend) (r: LinAlgRoutine) : Routing =
    policy
    |> List.tryPick (fun (rr, _, bb, routing, _) ->
        if rr = r && bb = backend then Some routing else None)
    |> Option.defaultValue Native

/// The BLAS level of a routine, from the table above. Backend-independent: the
/// level is a property of the operation's arithmetic intensity, not of where it
/// runs, so it is read off whichever row names the routine first.
let levelOf (r: LinAlgRoutine) : BlasLevel =
    policy
    |> List.tryPick (fun (rr, lvl, _, _, _) -> if rr = r then Some lvl else None)
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
/// emitters. So "gate off", "backend not implemented" and "shape not
/// recognised" all reach the same, already exercised, code.
///
/// THE BACKEND MODE ENTERS HERE AND ONLY HERE. The classifiers above never see
/// it, which is what keeps one copy of the shape-matching logic across targets.
///
/// THE PRECISION LETTER IS APPENDED HERE, so the emitted TEXT names the exact
/// routine family and width — `blade_gram_same_z` is visibly a `zherk` call and
/// `blade_gram_same_d` a `dsyrk` one. That is deliberate: it makes the routing
/// decision assertable from generated source, which is the only place a
/// mis-dispatch would show (the values agree to a ULP either way).
let shimEntryPoint (backend: LinAlgBackend) (call: LinAlgCall) : string option =
    let available =
        match backend with
        // The LAPACK routes ride their own gate and their own header, so the
        // availability question is per-ROUTINE, not per-backend alone. Routing
        // it here keeps the single-funnel property: still one place, one
        // conjunct, now reading the right predicate for the routine at hand.
        | HostBlas when call.Routine = Eigh -> lapackAvailable ()
        | HostBlas -> blasAvailable ()
        // Declared, not implemented — see LinAlgBackend.CudaBlas. Returning
        // None here (rather than omitting the arm) is what makes "asked for a
        // backend that does not exist yet" a clean fall-through to host loops
        // instead of a compile error or, worse, a host call in device code.
        | CudaBlas -> false
    if not available then None else
    match routingOf backend call.Routine with
    | Native -> None
    | ViaShim ->
        let p = precisionLetter call.Precision
        match call.Route with
        | RouteGramSame -> Some (sprintf "blade_linalg::blade_gram_same_%s" p)
        | RouteGramDistinct -> Some (sprintf "blade_linalg::blade_gram_distinct_%s" p)
        | RouteMatmul -> Some (sprintf "blade_linalg::blade_matmul_%s" p)
        | RouteDot -> Some (sprintf "blade_linalg::blade_dot_%s" p)
        | RouteGemv -> Some (sprintf "blade_linalg::blade_gemv_%s" p)
        // Different namespace AND different header: `blade_lapack.hpp` carries
        // its own `#ifndef BLADE_HAS_LAPACK #error`, so a program that names
        // these advertises a LAPACK dependency distinct from a BLAS one.
        | RouteEighPacked -> Some (sprintf "blade_lapack::blade_eigh_packed_%s" p)
        | RouteEighDense -> Some (sprintf "blade_lapack::blade_eigh_dense_%s" p)

// ============================================================================
// Classification entry points
// ============================================================================

/// The precision two operands AGREE on, or None. Mixed precisions decline
/// rather than promote: BLAS has no mixed-width routine, and silently widening
/// one operand would be a storage change the caller never asked for. (Blade's
/// own scalar loops promote per the element-type rules, which is exactly what a
/// declined route falls back to — so declining costs the optimisation and
/// changes no value.)
let private agreedPrecision (a: IRType) (b: IRType) : Precision option =
    match precisionOf a, precisionOf b with
    | Some pa, Some pb when pa = pb -> Some pa
    | _ -> None

let private elemOf (e: IRExpr) : IRType option =
    match typeOf e with
    | ArrayElem a -> Some a.ElemType
    | _ -> None

/// Classify `gram(l, r)`.
///
///   sameArray -> Syrk: square m x m written into Blade's PACKED
///                upper-triangular storage. REAL is A·Aᵀ (`ssyrk`/`dsyrk`);
///                COMPLEX is A·A^H, a HERMITIAN rank-k update
///                (`cherk`/`zherk`) — which is what Blade's own complex scalar
///                loop already computes, since it conjugates the second factor.
///   distinct  -> Gemm with B transposed: C(m x p) = A(m x n) · B(p x n)ᵀ for
///                real, and A · B^H for complex (`ConjTrans`), matching the
///                same scalar loop. Dense result.
///
/// Returns None (→ caller keeps its scalar loops) when either operand is not an
/// array, when their element types have no BLAS precision (integers, structs),
/// or when the two precisions disagree.
let classifyGram (l: IRExpr) (r: IRExpr) (sameArray: bool) : LinAlgCall option =
    match elemOf l, elemOf r with
    | Some le, Some re ->
        match agreedPrecision le re with
        | None -> None
        | Some prec ->
        if sameArray then
            Some { Routine = Syrk
                   Route = RouteGramSame
                   Level = L3
                   Operands = [ { Role = RoleA; Expr = l; Transposed = false } ]
                   NestOperands = []
                   // C is m x m from A's leading axis; the contracted extent is
                   // A's trailing axis.
                   M = Some { Operand = RoleA; Axis = 0 }
                   N = Some { Operand = RoleA; Axis = 0 }
                   K = Some { Operand = RoleA; Axis = 1 }
                   ElemType = le
                   Precision = prec
                   PackedTriangularResult = true }
        else
            Some { Routine = Gemm
                   Route = RouteGramDistinct
                   Level = L3
                   // `Transposed` on B is the REAL reading; for a complex
                   // precision the emission site spells it ConjTrans, which is
                   // the same flag with the conjugation the scalar loop applies.
                   Operands = [ { Role = RoleA; Expr = l; Transposed = false }
                                { Role = RoleB; Expr = r; Transposed = true } ]
                   NestOperands = []
                   M = Some { Operand = RoleA; Axis = 0 }
                   N = Some { Operand = RoleB; Axis = 0 }
                   K = Some { Operand = RoleA; Axis = 1 }
                   ElemType = le
                   Precision = prec
                   PackedTriangularResult = false }
    | _ -> None

/// Classify `matmul(a, b)`: C(m x n) = A(m x k) * B(k x n), dense result, no
/// transposes. The first-class intrinsic's only classification.
///
/// Only the `d` instance is reachable: `TypeCheck.inferMatmul` requires Float64
/// elements at the SURFACE (BL3999), so a f32/complex/int call never gets here.
/// The precision generalisation below is therefore a backstop that keeps this
/// classifier honest if the surface is ever widened — but widening it is a
/// LANGUAGE decision (what should `matmul` mean for complex operands: A·B, or
/// A·B^H?) and is deliberately not taken here.
let classifyMatmul (a: IRExpr) (b: IRExpr) : LinAlgCall option =
    match elemOf a, elemOf b with
    | Some ae, Some be ->
        match agreedPrecision ae be with
        | None -> None
        | Some prec ->
            Some { Routine = Gemm
                   Route = RouteMatmul
                   Level = L3
                   Operands = [ { Role = RoleA; Expr = a; Transposed = false }
                                { Role = RoleB; Expr = b; Transposed = false } ]
                   NestOperands = []
                   M = Some { Operand = RoleA; Axis = 0 }
                   N = Some { Operand = RoleB; Axis = 1 }
                   K = Some { Operand = RoleA; Axis = 1 }
                   ElemType = ae
                   Precision = prec
                   PackedTriangularResult = false }
    | _ -> None

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
/// Classify `eigh(S)` — the symmetric/Hermitian eigendecomposition — on the
/// OPERAND'S TYPE alone. This is the (routine × precision × SYMMETRY) decision
/// in one place, and symmetry is the axis that picks the routine FAMILY:
///
///   | operand                            | s      | d      | c      | z      |
///   |------------------------------------|--------|--------|--------|--------|
///   | rank-2 compact SymSymmetric        | sspev  | dspev  | DECLINE| DECLINE|
///   | rank-2 compact SymHermitian        | sspev  | dspev  | chpev  | zhpev  |
///   | dense rank-2 (symmetry ASSUMED)    | ssyev  | dsyev  | cheev  | zheev  |
///   | anything else (antisym, int, …)    | DECLINE                            |
///
/// THE COMPLEX-SYMMETRIC TRAP. A complex array carrying `SymSymmetric` is
/// complex-SYMMETRIC (A = Aᵀ, no conjugation), which is NOT Hermitian and is
/// not normal in general. LAPACK has no eigensolver for it at all — there is no
/// `zsyev` and no `zspev`. Its spectrum is COMPLEX and its eigenvectors are not
/// orthogonal, so the right routine is the general `zgeev`, which returns a
/// different result TYPE — a different operation, not a precision swap. The
/// route therefore DECLINES to the native path. This is the row a
/// precision-only widening would silently get wrong, exactly as
/// `zsyrk`-for-`zherk` would at the BLAS level.
///
/// REAL + `SymHermitian` routes to the REAL packed entry point, and that is a
/// theorem rather than a convenience: a real Hermitian matrix IS symmetric, and
/// `?spev` is the routine for it.
///
/// DENSE ASSUMES SYMMETRY, inheriting the surface's own domain — `math.eigh`
/// documents "symmetry is ASSUMED, not checked". A dense COMPLEX operand is
/// assumed HERMITIAN, which is what the `h` in `eigh` means and the only
/// reading under which the operation is defined.
let classifyEigh (operand: IRArrayType) : LinAlgCall option =
    if operand.IsVirtual then None else
    match precisionOf operand.ElemType with
    | None -> None                                   // int, bool, struct: no family
    | Some prec ->
        let complexOperand = isComplexPrecision prec
        match operand.IndexTypes with
        // ---- rank-2 COMPACT group: the packed route --------------------------
        | [ ix ] when ix.Rank = 2 && ix.IxKind = IxKPlain && ix.Dependencies.IsEmpty ->
            let packedOk =
                match ix.Symmetry with
                | SymSymmetric -> not complexOperand   // the complex-symmetric trap
                | SymHermitian -> true                 // real Hermitian == symmetric
                | _ -> false                           // antisym / wreath: no route
            if not packedOk then None
            else
                Some { Routine = Eigh
                       Route = RouteEighPacked
                       Level = L3
                       Operands = []
                       NestOperands = []
                       M = None; N = None; K = None
                       ElemType = operand.ElemType
                       Precision = prec
                       PackedTriangularResult = false }
        // ---- dense rank-2: symmetry asserted by the caller -------------------
        | [ i0; i1 ] when isPlainDenseAxis i0 && isPlainDenseAxis i1 ->
            Some { Routine = Eigh
                   Route = RouteEighDense
                   Level = L3
                   Operands = []
                   NestOperands = []
                   M = None; N = None; K = None
                   ElemType = operand.ElemType
                   Precision = prec
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
/// A non-virtual, BLAS-precision array of exactly `rank` ordinary dense axes;
/// answers its precision so the caller can require agreement across operands.
/// Virtual operands are refused because a `range`/`reverse` view has no pool
/// to point at — it inlines into index arithmetic at every use.
let private denseBlasArrayOfRank (rank: int) (t: IRArrayType) : Precision option =
    if t.IsVirtual then None
    elif List.length t.IndexTypes <> rank then None
    elif not (t.IndexTypes |> List.forall isPlainDenseAxis) then None
    else precisionOf t.ElemType

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
let private readOnlyBlasVector (t: IRArrayType) : Precision option =
    if t.IsVirtual then None else
    match t.IndexTypes with
    | [ ix ] when ix.Rank = 1
                  && ix.Symmetry = SymNone
                  && (ix.IxKind = IxKPlain || ix.IxKind = IxKIrreps)
                  && ix.Dependencies.IsEmpty -> precisionOf t.ElemType
    | _ -> None

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
        else
        match operandTypes |> List.map (denseBlasArrayOfRank 1) with
        | [ Some p0; Some p1 ] when p0 = p1 ->
            let prec = p0
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
                    && precisionOf e.ArrayElemType = Some prec
                if not (peelOk e0 && peelOk e1) then None else
                // Body is exactly `<peel> * <peel>` over the two distinct params.
                // NO CONJUGATION appears here, at any precision — which is why a
                // complex instance of this route must be `dotu` and never
                // `dotc`. See `RouteDot`.
                match cg.KernelExpr with
                | IRBinOp (_, IRMul, IRVar (lId, _), IRVar (rId, _)) when lId <> rId ->
                    let byParam id =
                        [ e0; e1 ] |> List.tryFind (fun e -> e.ParamVarId = id)
                    match byParam lId, byParam rId with
                    | Some le, Some re ->
                        Some { Routine = Dot
                               Route = RouteDot
                               Level = L1
                               Operands = []
                               NestOperands = [ RoleA, FromNestArray le.ArrayName
                                                RoleB, FromNestArray re.ArrayName ]
                               M = None; N = None; K = None
                               ElemType = le.ArrayElemType
                               Precision = prec
                               PackedTriangularResult = false }
                    | _ -> None
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
    | Some level, [ aName ], [ aTy ] when (denseBlasArrayOfRank 2 aTy).IsSome ->
        let prec = (denseBlasArrayOfRank 2 aTy).Value
        // Output: rank-1 dense, SAME precision as the matrix.
        let outOk =
            match cg.OutputType with
            | ArrayElem outTy -> denseBlasArrayOfRank 1 outTy = Some prec
            | _ -> false
        if not outOk then None else
        match level.Elements with
        | [ e ] when (match e.Virtual with RealArray -> true | _ -> false)
                     && e.ArrayPosition = 0
                     && e.ArrayName = aName
                     && e.RankComponent = level.Level
                     && e.ArrayRank = 2
                     && e.DimIndex = 0
                     && precisionOf e.ArrayElemType = Some prec
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
                    // Same precision as the matrix and the output: a mixed-width
                    // gemv has no BLAS routine and declining costs only the
                    // optimisation.
                    && (match typeOf vecExpr with
                        | ArrayElem vt -> readOnlyBlasVector vt = Some prec
                        | _ -> false)
                if not vecOk then None
                else
                    // `prodsum(row, x)` conjugates nothing, so every precision —
                    // complex included — emits CblasNoTrans.
                    Some { Routine = Gemv
                           Route = RouteGemv
                           Level = L2
                           Operands = []
                           NestOperands = [ RoleA, FromNestArray aName
                                            RoleB, FromKernelRef vecExpr
                                            RoleC, FromNestOutput cg.OutputName ]
                           M = None; N = None; K = None
                           ElemType = e.ArrayElemType
                           Precision = prec
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
