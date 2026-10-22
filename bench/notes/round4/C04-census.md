# C04 source census: private transpose into a seeded partial fold

Source/design only at C checkout `12a9d632e600d4327e8c7abbae65bf3e39246023`.
No production edit, build, lowering, emission, execution, test or benchmark.
Graft found no F# transpose entries (one indexed file), so targeted direct
source reads supplied the anchors below. Root reports integration's later D
runtime/docs changes leave these relevant optimizer sources unchanged.

## Select one missing route

Propose ONLY the anonymous rank-2 hard transpose immediately consumed by one
literal-seeded scalar row fold, producing a required rank-1 result. Initial
source is a dominating stored plain static Float64 matrix, with reproducible
serial execution and a closed total fold. No top-level named transpose
elision, full scalar-fold grammar, generic transpose fusion or new view type.

Representative diagnostic in `C04-census-inputs/01_private_partial.blade`:

```blade
type R = Idx<2>
type C = Idx<3>
let M: Array<Float64 like R, C> = [[1.0, 2.0, 3.0], [4.0, 5.0, 6.0]]
let rows: Array<Float64 like C> = reduce(transpose(M, [0, 1]), lambda(a: Float64, b: Float64) -> a * 10.0 + b, 7.0)
```

Independent expected rows are `[714,725,736]`: each original column has its
own seed 7 and ascending R chain. Default printing requires M and rows;
the anonymous transpose has no top-level print name. No special print knob
is needed to make this initial private case useful.

## Existing behavior and controls

* `Lowering.fs:999-1000` lowers transpose directly to `IRTranspose`.
  `IR.fs:3314` computes its swapped index records. Nominal R/C identities
  travel with the swapped records, not just equal numeric extents.
* C++ `genTransposeBinding` (`CodeGenBinding.fs:3839-3855`) forces a deferred
  source, then calls the hard materializer. `CodeGenExpr.fs:2636-2681`
  allocates swapped extents and copies source `(r,c)` into destination `(c,r)`.
  This is explicitly an independent owning snapshot, not a soft view.
* LLVM `EmitLlvm.fs:5399-5422` consumes the operand, validates dense groups and
  swapped result shape, allocates a full pool and copies swapped coordinates.
  Interpreter `Interp/Loops.fs:2553` calls `ArrayOps.transposeArray` (987).
  Canonical formalism's combinator table (`docs/formalism.md:362`) calls this
  a hard transpose. Avoiding the copy needs a legality proof, not a global
  reinterpretation of transpose as a view.
* No `IRTranspose` match exists in Optimize. C02's
  `fusePartialRowTerminalsModule` (`Optimize.fs:8021`) accepts only a private
  scalar **ApplyCombinator map** producer (`peel`, 8084). A transpose producer
  does not enter that admission. Its stored-source, closed-total-fold and
  immediate row-consumer proofs are useful infrastructure, not an existing
  transpose-elision implementation.
* Partial fold inference (`TypeCheckInfer.fs:3380-3545`) uses a named row
  source. Its special two-vector outer-product rewrite does not match
  transpose syntax. The fallback binds anonymous operands once, then computes
  a row apply whose body is a rank-1 fold. `Lowering.fs:974-995` distinguishes
  ordinary `IRReduce` from deferred `IRReduceCompute`.
* Full scalar `axes=2` is DIFFERENT: `rankKDesugar`
  (`TypeCheckInfer.fs:3717-3854`) binds an anonymous transpose, initializes one
  accumulator and constructs nested sequential loops/assignments. Its order
  is C outer, R inner after transpose. Diagnostic 02 expects **7142536**, not
  source-row-major 7123456. Recognizing that imperative-shaped desugar would
  be a second grammar; scalar consumption remains a control in C04.
* Source already has a declarative low-storage column-fold idiom: outer
  `method_for(range<C>)`, inner fused fold over `range<R>` reading `M(r,c)`.
  Diagnostic 04 spells it using existing constructs. Also, the leading-axis
  array-valued fold in `tests/corpus/loops/134_leading_axis_fold.blade` avoids
  transpose through another source spelling; that lowering can use vector
  seeds/step buffers. Neither establishes automatic elision for diagnostic 01.

These are static findings. No actual C04 IR or emitted allocation count has
been observed. The four authored inputs are diagnostic hypotheses, not corpus
tests or evidence of a working optimization.

## Required observed shape and target

Expected early partial shape, to verify before implementation:

```text
host rows = IRLet(tid, IRTranspose(IRVar M,0,1),
                 IRCompute(IRApplyCombinator consumer))
consumer.Arrays = [IRVar tid], KernelInputRanks=[1]
consumer.Kernel = row(rowParam:Array<Float64 like inferred R>)
row.Body = IRReduce(IRVar rowParam, fold, Some literalSeed)
consumer.OutputType = original Array<Float64 like C>
```

An incidental `IRCompute` wrapper around the transpose is a hypothesis to
record, not permission to accept arbitrary scope spines. Match IDs and exact
one-use scope, never `__pfsrc` prefixes. Stop for root review on another shape.

Target is the already expressible diagnostic 04: an outer virtual C range
apply, whose scalar kernel contains an inner `IRReduceCompute` over a virtual
R range map. The map reads `M(r,c)` using exact R/C coordinates. Preserve the
original host/output type and C identity. Manufacture fresh callable/arrow
IDs and origins, and complete ApplyInfo/MethodForInfo/range metadata; do not
copy the old inferred row parameter's abstract index into the new kernel.
The source R record supplies the inner coordinate; C supplies the outer one.
These are scalar-input range maps, so C02-L1's correct ARRAY/row-argument
materialization boundary must stay intact.

Every output c evaluates the original literal seed once, then visits
`M(0,c), M(1,c), ...` in that order. C=0 executes no row/seed; R=0 emits each
exact seed, including signed zero. Do not rewrite this into one shared scalar
accumulator, fold M in source row-major order, inherit parallel/reassociation
licences, or claim that a metadata-swapped contiguous row view is a column.
Generated M/c captures are certified construction inputs; arbitrary user-fold
captures remain refused. Generated indexed reads must have a separate static
bounds/index-identity proof, not be described as capture-free user kernels.

## Legality, demands and phase

Use the existing C02 plain-array, literal-seed, storedArrayProof and closed
IRTotality callable-graph audit (Optimize.fs:8029-8076). Refuse providers,
deferred sources/aliases, dynamic extents, compact/ragged/structured axes,
non-Float64 elements, unresolved/recursive/captured/effectful/may-fail folds,
nonliteral or failing seeds, and OMP/CUDA/MPI/arity/static strategy metadata.
Confirm the input is stored before the host and reject hidden initializer/
alias writes and lifetime events through complete payload analysis. The
immediate producer/consumer scope excludes intervening mutation or abort.
Do not bypass deferred-source forcing: otherwise a late producer failure
could incorrectly move after a first-row failure, the C02-L1 class of defect.

Full residual roots must audit every other occurrence of tid and its types:
binding values, host side execution payloads even if constant, captures,
callable bodies/signatures, declared types/embedded extents, ApplyInfo and
MethodForInfo shared/kernel axes, provider IDs/names and foreign references.
Subtract only the precisely certified producer/input uses in the actual host
ID; another binding containing the same expression object remains a demand.
Certify all removed transpose/type and consumer metadata. Escaping, returned,
printed, keyed, aliased or multiply consumed transpose stays materialized.
Top-level named T stays outside this first arm even when print selection hides
it; diagnostic 03 explicitly demands T and a keyed sample.

Prefer an additional operand-kind arm in the existing early partial-terminal
rewrite, reusing/locally factoring its proofs, with disjoint ApplyCombinator
and IRTranspose producers. Keep it before IRLift/CSE/pool reuse/planner tables
(`Optimize.fs:8267`; `Lowering.fs:3142-3146`). Reuse per-call BLADE_FUSION and
repro gates. No user API, layout flag, backend mechanism or new knob is needed.

One internal interface detail needs root review before implementation:
`optimizeModule`/the C02 helper currently receive all program callables but
no foreign binding/type reference set. Lowering's existing foreignRefs
collection (3119) short-circuits when CompoundInits is empty. C04 must not
reuse that empty set as a whole-program proof. Thread the existing module
reference census to the new arm (including foreign types/side payloads),
without widening the older C02 admission. There is one production
optimizeModule caller (`Lowering.fs:3142`). Local lexical privacy prevents
ordinary exported references, but is not a substitute for the requested
defensive foreign-demand census over IR.

Expected future owned files: Optimize.fs (existing partial terminal function/
shared local proofs), Lowering.fs (internal foreign-census plumbing if needed),
focused OptimizeTests/corpus and handoff. No emitter, interpreter, sibling-fold,
round-3 layout/ownership infrastructure or lifetime API change is proposed.

## Storage and bounded sole-Luna request

Modeled payloads: retain input 8RC and required output 8C; remove private
transpose 8RC plus its own layout headers/row pointers. No R-cell mapped
temporary should replace it. Baseline partial is expected to have M+T+rows,
target M+rows; scalar control M+T, demanded control M+T+rows. For the 2x3
fixture, predicted LLVM payload layouts are `[6x8,6x8,3x8]` versus target
`[6x8,3x8]`. These are predictions for census, not measured memory results.
Strided source reads may hurt locality; no bandwidth/speed win is claimed.

Ask ONLY Luna, through root, to reuse an exact validated C03 compiler whose
source/binary identity matches this checkout (or build once if root requires).
Use the four files in C04-census-inputs; default BLADE_PRINT unset, repro=1,
contract=off, BLAS=off, OMP_THREADS=1. Obtain:

1. Early IR immediately before optimizeModule for all four sources, with
   source/host/private IDs, scope, complete types and consumer/callable
   metadata. Confirm 01's exact immediate row shape and 02's different
   rankKDesugar topology. No generated-name-only assertion.
2. C++ and actual LLVM `emit <absolute-source>` plus `plan --json` for the four
   sources. Record actual backend/fallback, allocations, copy/read dataflow,
   required output identity/prints and whether 04 avoids any full R*C pool.
   Read-only FSI against the exact DLL may capture early IR as in C03; no
   compiler production hook is needed merely for this diagnostic.
3. STOP if 01 already elides transpose, the proposed shape differs, 04
   materializes a full map, output C identity differs, or either renderer
   refuses. Return evidence for review; do not implement a second route or
   silently count a C++ fallback as LLVM capability. No runtime needed yet.

If that census confirms the gap and target capability, subsequent focused
tests should pin ordered columns, distinct R/C identity, R0/C0, negative-zero
seed, and cancellation columns `[1e16,1,-1e16]`/`[1e16,-1e16,1]` -> `[0,1]`.
Refusal/order controls: demanded/named/aliased/hidden/foreign transpose,
unproven/deferred/structured source, mutation/side payload/lifetime event,
captured/unknown/parallel/failing fold and failing seed. A paired late deferred
producer vs early fold failure must retain the materialized order. Require
interpreter AND native optimizer ON/OFF plus independent pins after approval:
both lanes consume optimized IR. No full suite or benchmark requested.

Status: bounded partial-fold proposal only, awaiting root review and sole-Luna
topology/emission census. Scalar/full-fold extension is explicitly deferred.
