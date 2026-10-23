# Identity, structural transpose, and compute

Status: DESIGN; compiler changes not implemented. Abstract proof models are
implemented on `codex/structural-transpose-proofs`, based on master
`361504b60a1edd8a5c06993976daae7d7d25d9d3`. The first three models were committed
in `71758a73`; the materialization-elimination extension accompanies this plan.
Rocq 9.0.1 compilation and independent kernel checking passed for the new module
and its dependencies; the extended assumption audit passed for all 2,205 results.

The goal is to preserve Blade's structural index types and canonical evaluation
while making aliases independent of variable spelling, treating transpose as a
typed structural function, and allowing `compute` to participate in planning
across functions and files without forcing intermediate allocations.

The [semantics and mathematical argument](../research/identity-transpose-compute.tex)
and the `.v` sources below describe the proposed model. Existing
[`formalism.md`](../formalism.md) remains the current language specification;
its alias and compute wording must change with the corresponding implementation,
not merely because this plan exists.

## 1. Decisions and invariants

1. Binding identifiers, semantic operand keys, and storage identities are distinct.
2. An identity-transparent alias preserves the complete semantic operand key.
3. Transpose composes a typed structural map. It is not an ordinary strided array
   view and does not inherently create a new logical root.
4. Canonical applications commit to their sharing relation, descriptor,
   reconstruction rules, and numerical evaluation contract during elaboration.
5. `compute : Computation<T> -> T` commits a logical value and its observable
   evaluation obligations. Allocation and physical realization are planner choices.
6. Every surviving bulk traversal satisfies the supported forward, contiguous
   curried storage contract. A conversion kernel has its own checked contract;
   increasing destination addresses are not a universal requirement.
7. Materialization elimination preserves snapshots, required effects, failures,
   and numerical observations. It does not imply that elimination is cheaper.
8. Optimize.fs remains the home for deterministic semantic-equivalence rewrites.
   The interpreter and compiled backends implement one observable language.

## 2. Identity and alias semantics

Use a descriptor along these lines; these are conceptual fields, not a final F# API:

```text
BindingId       = existing scoped IR identifier
ValueVersion    = stable logical definition / SSA version
OperandKey      = (ValueVersion, normalized structural map/action, ordered frame)
Representation  = (allocation, storage layout, ownership, lifetime, residency)
```

Aliases copy `OperandKey`. Initially equal values can occupy independent copied
storage; later writes to one copy must not alter the other. A mutation creates a
fresh logical version for the affected value. Versions must be globally distinct
SSA definitions or equivalent unique tokens: two independently mutated copies of
one origin must not collide because both incremented a local counter to one.
At control-flow joins, use a new value version unless equality is established.

Preserve the existing assignment/copy contract. In particular,
[`loops/081`](../../tests/corpus/loops/081_mut_array_copy_semantics.blade) pins
independence of assignable copies. Semantic alias propagation is not permission
to replace those copies with observably shared mutable storage. Storage alias
analysis determines which live descriptors a write invalidates; semantic key
equality determines structural sharing at canonical application.

Do not compare contents or physical pointers to establish semantic identity.
Separately defined computations may retain different keys even when equal in
value. Optimizer CSE may share a buffer without merging the keys used by the
already-elaborated canonical applications.

### Sharing-dependent calls

For adjacent rank-one inputs and a licensed commutative pair kernel, a call with
partition `[0,0]` can return a symmetric pair space, while `[0,1]` returns a dense
pair space. The individual argument types can be identical. The relationship
therefore belongs in the call judgment and return type before storage is chosen.

Specialize identity-sensitive functions on normalized sharing partitions and
necessary structural-map relations alongside ordinary type information. Do not
specialize on literal root IDs or caller variable names. Identity-insensitive
functions keep their existing specialization behavior.

Preserve operand positions and current structural support. In particular,
partition `[0,1,0]` does not license regrouping `(A,B,A)` or inventing a
position-permuted compact representation. Retain the existing diagnostics and
supported contiguous-group rule until a separate implementation justifies more.

For a higher-order/exported interface that hides sharing, explicitly choose a
fixed representation contract, carry a witness, or reject the unsupported case.
Assuming hidden aliases are distinct is not the proposed alias-transparent model.
Runtime pointer equality is not a substitute for a semantic witness.

Alias transparency can change output structure and canonical evaluation relative
to today's spelling-based semantics. Treat that as a language/elaboration change,
with migration cases and revised expected types. It is not a late optimization
whose on/off gate is allowed to change meaning.

## 3. Transpose as a structural function

Represent supported coordinate permutations and payload actions explicitly,
retaining the source's canonical read and ordered frame. Normalize composition,
identity and inverse maps; add index-type certificates for symmetric identity,
antisymmetric negation, and Hermitian conjugation.

An ordinary transpose preserves the underlying logical origin/version but changes
its oriented operand key. A symmetric certificate may erase that distinction.
Negation/conjugation remain explicit actions unless a valid carrier-specific law
simplifies them. Arbitrary computed equality is outside this normalizer.

Use the source reconstruction law when absorbing a transpose. Do not re-evaluate
the producer kernel in a new canonical region or change its floating-point tree.
Payload negation followed by compact reconstruction is not blindly equivalent to
negating every logical read: an antisymmetric implicit diagonal zero must retain
its prescribed representation. Sign, conjugation, fixed-point constraints, NaNs,
and signed zeros belong in the exact rule's admission evidence.

A transformed value is realized by one of:

- certified structural simplification;
- absorption into a compatible consumer;
- a supported explicit materialization/conversion;
- rejection with a refactoring diagnostic when no supported traversal exists.

Keep current compact-boundary refusals until their target descriptors and lowering
are implemented. Do not silently decompact an unsupported map.

## 4. Compute semantics and whole-program planning

Keep `compute` through typing and semantic dependency analysis. Its logical result
has a stable key, fixed canonical descriptor, numerical contract, and particular
source versions. Preserve any required once-only effects and failures. Once these
obligations are explicit in the core, the marker need not emit a loop or allocation.

The whole-program graph is rooted in all observable values and ordered events:
returns, output, visible writes, external reads, failures, and interface demands.
This is not a rule to delay all effects until process exit or to evaluate every
unused declaration. Libraries and unknown callees need explicit summaries and
representation contracts; they are not automatically closed-world programs.

Separate logical computation from physical planning:

```text
typed canonical value graph + ordered obligations
    -> legal realization candidates
    -> deterministic cost selection
    -> explicit storage / fused / streamed / conversion plan
    -> backend emission
```

The first implementation should preserve snapshots using static provenance,
ownership and dependency analysis. Keep the existing materialization when it
cannot prove old data remains available. Runtime version counters, borrowing
syntax, immutable runtime storage, and copy-on-write are optional later choices.

A pure kernel's reads still depend on source versions. Eliminating a transpose
buffer before a later input write requires scheduling all necessary reads before
the write, retaining the old backing, or copying enough data. A logical old-version
label alone is not an availability proof.

Keep failing/effectful producers opaque in the initial planner. In particular,
an unused `compute` that must fail cannot be dropped, and a consumer's conditional
read cannot suppress a required producer failure. Moving or extracting checks
requires proof of the same success/failure behavior and ordering. Termination is
also a precondition for pure elimination; the current effect flags alone do not
establish it for arbitrary recursive code.

## 5. What already exists and what changes

| Area | Existing implementation | Required extension |
|---|---|---|
| Identity | `Types.fs`: `ArrayIdentity`, `sameIdentity`; `TypeEnv.fs`: per-binding identity | Stable versioned operand descriptor separate from names and storage |
| Aliases and calls | `TypeCheckInfer.fs`: name-derived identities at bindings/applications | Propagate semantic keys; instantiate sharing-sensitive return structure |
| Specialization | `IRMono.fs`: `canonTypeKey`, arity/HM/shape specialization | Add normalized sharing information only to identity-sensitive instances |
| Iteration/storage | `IRLoopStructure.fs`: shared canonical grouping rule; `IR.fs`: `ApplyInfo` | Retain grouping rule; consume resolved keys and freeze descriptor metadata |
| Transpose laws | `IRStorage.fs`: `TransposeWithin`, `ReadTransform`; `inferTranspose` already selects identity/negation/conjugation | Shared typed structural normalizer and transport evidence |
| Runtime realization | `nested_array_utilities.hpp`: flat negate/conjugate, lazy canonical reads; C++/LLVM/interpreter transpose paths | Preserve these as valid materialized implementations; add certified elimination |
| Compute | `IRTComputation`, `TExprCompute`, `IRCompute`, interpreter force points | Represent value/effect commitments independently of buffer allocation |
| Cross-compute optimization | `IRMono.fs` elementwise fusion; Optimize row-fold, sharing and reuse passes | Generalize legal producer/consumer plans while preserving existing guards |
| Program visibility | `Lowering.fs`: whole-program shape specialization and callable summaries; Optimize invoked per module | Cross-module use/dependency census and analyzable producer graph |
| Effects | `Effects.fs`: repeatable/movable summaries; forced writing maps in Lowering | Read/write/version dependencies, demand and termination evidence; retain conservative boundaries |
| Validation | `IRValidate.fs`, decision records, interpreter/backend comparisons, opt-diff gates | Descriptor/version/plan consistency checks and optimization-off equivalence tests |

Do not simply remove all `IRCompute` patterns: existing lowering and emitter
dispatch uses them to distinguish deferred computations from values. Replace
their physical role only after an explicit plan supplies that distinction.

## 6. Implementation sequence and exit gates

### P0 — Characterize the existing contract

Record alias/copy behavior, sharing-dependent output types, compute force points,
canonical reads, and mutation/failure ordering in focused corpus cases. Include
module-level, block, function and lambda scopes. Separate intended source changes
from optimization equivalences. Keep current compiler emission as a reference.

Exit: every behavioral difference contemplated by this plan is classified as
an elaboration change, an exact optimization, or an unsupported case.

### P1 — Stable identities and typed sharing

Introduce logical versions and oriented operand descriptors in the type/IR layer;
preserve them through aliases, calls, returns, and physical copies. Build the
sharing partition before output deduction. Extend instance keys and call return
typing where needed. Audit name-based identity construction sites, not just
`sameIdentity`. Keep independent mutable storage and invalidate the correct values
on writes. Update the source formalism with the implemented alias rule.

Exit: aliases, alpha-renaming and function extraction preserve canonical structure;
`[0,0]` and `[0,1]` calls do not share an incompatible specialization; writes to
independent copies remain independent; `(A,B,A)` keeps the supported layout rule.

### P2 — Structural transpose normal forms

Add the supported typed map/action algebra and consume index-type certificates.
Preserve map, frame, source version, and reconstruction in the core. Normalize
before sharing-dependent elaboration when it affects identity. After elaboration,
rewrites must preserve the selected canonical application rather than recompute it.

Exit: double transpose restores the original operand/frame; symmetric collapse
and antisymmetric/Hermitian actions agree with existing reads; unsupported compact
transforms retain diagnostics; canonical scalar evaluations are unchanged.

### P3 — Compute commitments and local materialization planning

Extract a graph of fixed canonical producers in finite, terminating, pure regions.
Retain opaque boundaries for effects, partial operations without a certificate,
unknown calls, unsupported traversal, and writable/external representation needs.
Introduce explicit realization decisions and start with the current materialized
plan as the always-available baseline. Let Optimize eliminate selected buffers;
validate old-version availability and all surviving traversals before emission.

Exit: a pure computed intermediate can fuse without a pool; repeated consumers
share or recompute only under the declared value/cost contract; a write after
compute cannot change the computed value; unused required failures still occur.
The interpreter can remain eager for reference execution.

### P4 — Whole-program visibility

Add cross-module producer/consumer and capture analysis after specialization has
established concrete types and sharing. Keep qualified IDs, export contracts,
module initialization order, and refreshed callable summaries consistent with
newly generated kernels. Inline/analyze supported calls or use verified summaries.
Treat recursive SCCs, unknown higher-order calls and foreign interfaces as opaque
until separately supported. Do not flatten modules merely to bypass their contracts.

Exit: splitting a supported computation across files/functions does not force
avoidable intermediates; effects retain their required order; an escaping value
has a realizable lifetime and representation.

### P5 — Broader deterministic selection

Enumerate a bounded set of legal storage, fusion, streaming, conversion, and
recomputation alternatives in Optimize. Use the existing decision record to report
the chosen rule, facts and declined cases. Include live source versions, frames,
resident layouts, sharing, effect progress and cost-relevant state in boundaries.
Prune only same-interface resource-dominated alternatives. Preserve ties when
later consumers distinguish them. A general search engine is not required for
earlier phases' individually certified exact rewrites.

Exit: each route decision states its legality and cost basis; eliminating a buffer
is never assumed to improve work or traffic merely because it reduces allocation.
Report optimality only for the enumerated plan family and stated cost model.

## 7. Proof correspondence and remaining obligations

| Artifact | Established result | Explicit scope |
|---|---|---|
| [BladeStructuralIdentity.v](../../proofs/BladeStructuralIdentity.v) | Alias-preserving keys; transpose orientation/involution; sharing-dependent layout example; snapshot boundary | Small rank-two key model, not the full F# type system |
| [BladeMaterialization.v](../../proofs/BladeMaterialization.v) | Finite buffer readback, pure graph elimination, and exact final-store/trace/result preservation under arbitrary retention policies | Fixed graph/descriptors/scalar trees; pure total operations; unchanged effect/snapshot skeleton |
| [BladeLayout.v](../../proofs/BladeLayout.v) | Licensed transpose/direction laws and minimum summed-Kendall schedule selection | Stated carrier/group hypotheses; stride coherence is not a full cache model |
| [BladeLayoutOptimizationBridge.v](../../proofs/BladeLayoutOptimizationBridge.v) | Admissible schedule selection and conditional contextual resource replacement | Requires checker soundness and additional resource dominance evidence |
| [BladeCompositionalOptimization.v](../../proofs/BladeCompositionalOptimization.v) | Same-interface dominance survives continuation; frontier pruning preserves optimal candidates | Supplied transition system at fixed depth, sufficient states and monotone costs |

The materialization model has real finite lists for retained canonical payloads
and readers over lexical predecessor environments for eliminated ones. Descriptor
lookup is bounded and can return structural zero or a slot plus a reconstruction
action. Scalar trees allow value-dependent reads and conditionals. Its ordered
program skeleton includes output, writes, snapshots, checks, abort and return.
The theorem preserves the complete observation, not merely the final array.
The graph uses common coordinate and scalar carriers, parameterized in Rocq;
a heterogeneous typed compiler graph needs an encoding or a typed extension.

It does not prove source-to-graph lowering, arbitrary effect motion, failing-kernel
extraction, numerical reassociation, physical availability of old values, a
traversal scheduler, or a cheaper implementation. The mathematical snapshot store
is persistent; a compiler must supply a physical strategy that realizes that
persistence where needed. Arbitrary retention policies are denotationally equal
in the model, but only structurally legal and realizable policies enter Blade's
planner. The descriptors' correspondence to actual index types is also an
instantiation obligation.

## 8. Validation matrix

| Case | Required observation / evidence |
|---|---|
| Alias, alpha-renaming, helper extraction | Same key relationships, canonical result structure and exact reads |
| Same/distinct arguments with identical ordinary types | Correct return layout and distinct instances where necessary |
| Two copies independently mutated from one origin | Unique logical versions; no accidental semantic or storage alias |
| Double transpose; symmetric, antisymmetric, Hermitian inputs | Normal-form and reconstruction agreement, including zeros and fixed points |
| Negative/conjugated compact reads | Preserve implicit zero policy and numeric contract, including signed-zero cases |
| Pure compute with one/many consumers and empty domains | Correct values; justified allocation and evaluation counts |
| Producer may fail in an unrequested cell | Preserve the required failure; decline elimination absent a complete obligation proof |
| Conditional consumer, output before/after failure | Preserve executed branches, output prefix and abort code |
| Snapshot, then input write, then transposed consumer | Read the earlier version; account for retained backing/copy/scheduling |
| Writing producer with multiple consumers | Required once-only writes and order; never duplicate through recomputation |
| Program split across modules or helpers | Same observations; eligible equivalent plans without accidental module barriers |
| Foreign/writable return or unknown call | Explicit materialization/ownership boundary or conservative refusal |
| Compact, dependent and nonadjacent layouts | Valid structural traversal and existing unsupported-case diagnostics |

Use optimization gates off/on comparisons (`opt-diff`) in addition to interpreter,
C++ and LLVM parity: all lanes can consume the same incorrectly optimized IR.
Inspect allocation/plan output for intended buffer removal; use cost/performance
measurements separately from value-correctness tests. Pin numerical modes when a
bit-exact comparison requires them. Run targeted gates after each phase and the
repository's required integration suites before merging compiler changes.

## 9. Non-goals and follow-up decisions

This plan does not introduce a live mutable transpose view, content-based array
identity, unrestricted strided bulk traversal, runtime COW by default, or a promise
of globally fastest hardware execution. The initial normalizer is deliberately
bounded; richer structural maps need their own typed laws and implementations.

Before expanding beyond P3, decide the exported sharing-witness ABI, the resource
model and bounded candidate family, and the supported ownership/borrowing policy
for external and writable values. None of those decisions is a prerequisite for
alias-preserving elaboration or conservative local elimination.
