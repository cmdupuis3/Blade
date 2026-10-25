# Identity, structural transpose, and compute

Status: DESIGN revised 2026-10-24 (Claude, after review and a Codex consultation;
the original draft is Codex's, on `codex/structural-transpose-proofs` at c6a9b8b3,
base master 361504b6). WAVE 1 BUILT 2026-10-25 on
`claude/structural-transpose-proofs-review-f6bd01` (a fast-forward of the Codex branch
plus master 94487a90): the P1, P2 and P3 ENTRY POINTS of §6, each marked BUILT there
with its open remainder. The four Rocq models compile on Rocq 9.0.1, are axiom-free,
pass `coqchk` and the assumption audit (2205 results); their prose in `proofs/README.md`
and `docs/proofs.md` is re-scoped to §7 (40fd0a5f).
The review that drove this revision is
[review-identity-transpose-compute-2026-10-24.md](review-identity-transpose-compute-2026-10-24.md).

The goal is to preserve Blade's structural index types and canonical evaluation
while making array identity independent of variable spelling, treating transpose
as a typed structural function, and letting `|> compute` participate in planning
across functions and files without forcing intermediate allocations.

[`formalism.md`](../formalism.md) remains the language specification. The owner has
allowed small semantic changes to it for this work; each one is named below as a
LANGUAGE CHANGE with its migration, and lands with its implementation, never ahead of it.

## 0. Why: the performance case, measured

The design invariant is "the fastest way is the only way": storage, iteration and
routing derive from structure, never from how a program happens to be spelled. Today
identity is spelled. Three probes on master cf2028cc (sources in the review):

```blade sketch
let A = [1.0, 2.0, 3.0]
let B = A
function f(x: T^1, y: T^1) where comm(x, y) =
    method_for(x, y) <@> lambda(a, b) where comm(a, b) -> a * b |> compute
function g(x: T^1) =
    method_for(x, x) <@> lambda(a, b) where comm(a, b) -> a * b |> compute
let PAA = f(A, A)   // DENSE today: "the commuting positions hold different arrays (x, y)"
let PAB = f(A, B)   // dense
let GA  = g(A)      // packed, "triangular-iteration ... speedup x2"
```

- Extracting a two-parameter helper loses the packed layout and the x2 (r! for an
  r-way group) for every caller, including `f(A, A)`. The body never sees call-site
  sharing: `IRMono.canonTypeKey` ignores identity, so `f(A, A)` and `f(A, B)` are ONE
  instance.
- A deferred-map alias is already the SAME value by the formalism (§10.2, W4-1), yet
  `method_for(g, g2)` with `let g2 = g` prints dense while `method_for(g, g)` prints
  packed. The language contradicts itself on this point today.
- `transpose(transpose(M))` emits two full copies with zero plan decisions; a SymIdx
  transpose is erased at typecheck but the alias binding still copies the pool; an
  antisym transpose negates into a fresh pool.
- A single-consumer `|> compute` always allocates a pool at top level; the same program
  without `compute` fuses with no pool. Inside function bodies every let and return
  combinator is forced (`Lowering.forceCallableBody`, Lowering.fs:490), so helpers
  allocate what top level would fuse.

Expected gains (operation and storage counts, not measured speedups): a shared pair
group goes from n^2 to n(n+1)/2 cells (factor 2n/(n+1)); an r-way group from n^r to
C(n+r-1, r) (approaching r!); a double transpose from two copies to none; a read-only
compact transpose alias from one pool copy to none; a single-consumer pure producer
from one N-cell pool plus N writes and N reads to none. Possible losses, each a reason
to keep the materialized candidate and measure: recomputing an expensive producer for
several consumers, retaining an old input across a write, a packed-to-dense boundary
conversion, a lost BLAS or vectorized route, extra specializations.

## 1. Decisions and invariants

1. Binding identifiers, semantic operand keys, and storage identities are distinct.
2. An identity-transparent alias preserves the complete semantic operand key.
3. Transpose composes a typed structural map. It is not an ordinary strided array
   view and does not create a new logical root.
4. Canonical applications commit to their sharing relation, descriptor,
   reconstruction rules and numerical evaluation contract during elaboration.
5. `compute : Computation<T> -> T` commits a logical value and its observable
   obligations (§4). Allocation and physical realization are planner choices.
6. Every surviving bulk traversal satisfies the forward, contiguous curried storage
   contract. A conversion kernel has its own checked contract; increasing destination
   addresses are not a universal requirement.
7. Materialization elimination preserves snapshots, required effects, failures and
   numerical observations. It does not imply that elimination is cheaper.
8. `Optimize.fs` remains the home for deterministic semantic-equivalence rewrites;
   realization decisions are SIDE TABLES read by both emitters (as `PoolReuseTable`,
   `HotRowTable` are), never tree rewrites, so the eager interpreter stays an
   independent oracle (§8).
9. A declared type fixes the layout AND the elaboration at that boundary (§2.3).
10. AD elaborates its differentiated function with independent argument slots (§2.4).
11. Alias transparency is a LANGUAGE CHANGE, never a gated optimization: no knob may
    change meaning.

## 2. Identity and alias semantics

Conceptual descriptor (not a final F# API):

```text
BindingId       = existing scoped IR identifier
ValueVersion    = stable logical definition / SSA version (globally unique token)
OperandKey      = (ValueVersion, normalized structural map/action, ordered frame)
Representation  = (allocation, storage layout, ownership, lifetime, residency)
```

Aliases copy `OperandKey`. A mutation mints a fresh `ValueVersion` for the affected
value; at control-flow joins a new version is used unless equality is established.
Two independently mutated copies of one origin must never collide.

**The existing copy contract is preserved.** `let B = A` at top level deep-copies
(loops/081); `B(0) = 9.0` leaves `A` intact; a `mut` parameter's element writes alias
the caller's array (functions/145, BL4005 on a plain `let` argument). Semantic key
equality decides structural sharing at a canonical application; storage alias analysis
decides which live descriptors a write invalidates. Neither replaces the other.

Do not compare contents or pointers to establish identity. Separately defined
computations keep different keys even when equal in value. Optimizer CSE may share a
buffer without merging the keys of already-elaborated applications.

### 2.1 Where to start: deferred-map aliases (P1 entry point)

Master's §10.2 (W4-1, 5d4a28d0) already says a top-level `let g2 = g` over a pure
deferred map binds the SAME deferred value, that the map and its aliases form one alias
class fixed at its first forcing event, and that writes through any member are BL4005.
No write is possible, so the equality witness is free. P1 begins by making the identity
groups of §8.3 see one array through such an alias, which repairs today's contradiction
(`method_for(g, g2)` dense) with no version machinery. Stored arrays with provable
no-write intervals come second; function parameters (§2.2) third.

### 2.2 Sharing-dependent calls

For adjacent inputs and a licensed commutative pair kernel, a call with partition
`[0,0]` returns a symmetric pair space and `[0,1]` a dense one; the argument TYPES are
identical. The relationship belongs in the call judgment and the inferred return type.

Identity-sensitive functions (a body whose comm/anticomm application involves two or
more parameters, directly or through resolved callees) are specialized on the
normalized sharing partition of their relevant operands plus the supported structural
map relations between them, in addition to ordinary type information: at most Bell(k)
instances for k relevant operands, only the partitions actually encountered, never root
ids or caller names. Identity-insensitive functions keep today's specialization.

Positions are preserved: `[0,1,0]` does not license regrouping `(A, B, A)`; the
adjacent-only rule of §8.3 (f882eb22, TypeCheckInfer.fs:10650-10709) and its BL4010
advisory stay until a separate implementation justifies more.

A higher-order or exported interface that hides sharing must fix a representation
contract, carry a witness, or refuse. Assuming hidden aliases distinct is not this
model; runtime pointer equality is not a witness.

### 2.3 Declared signatures and ascriptions (LANGUAGE CHANGE)

Today `let P: Array<Float64 like I, I> = method_for(A, A) <@> comm-kernel |> compute`
is BL3001 (got `SymIdx<2, I>`), and a function declared `-> Array<Float64 like I, I>`
with a comm body called as `mk(A, A)` returns dense (index-types/242 `make_g`).

Rule: an explicit annotation (a `let` ascription or a declared return type) fixes the
externally visible descriptor AND the elaboration at that boundary: the body is
elaborated to the declared layout exactly as today, so declared-dense results keep
today's cells, bits and pins (index-types/242 unchanged). A declaration is the
optimization interface; writing a dense type is opting out of sharing specialization
there. The top-level ascription above therefore becomes ACCEPTED with dense elaboration
(migration: today's BL3001 cases). An UNANNOTATED helper infers its layout from the
partition and may return packed.

Recorded alternative (Codex): elaborate the body to its sharing-specialized canonical
meaning and coerce compact to dense at the boundary by reconstructing logical reads.
Same cells for kernels whose canonical and per-cell evaluations agree bitwise, but a
changed canonical evaluation for kernels like `(x + y) + z` (the paper's incompatibility
proposition) and a dense fill either way. Deferred until P2's reconstruction laws exist
and a case needs it.

### 2.4 AD (contract, new)

`ad.grad`, `ad.jvp`, `ad.vjp` elaborate their function with INDEPENDENT formal argument
slots and freeze that instance's canonical descriptors before transformation. Equal
primal arguments never collapse slots: `ad.grad(floss)(A, A, ...)` over
`floss(a, b) = gram(a, b)` differentiates the two-input dense-gram contract with
distinct per-slot cotangents, and its returned primal belongs to that AD instance (never
replaced by a sharing-specialized ordinary call whose canonical evaluation may differ).
Tied differentiation is explicit: `h(a) = floss(a, a)`. A packed pullback needs the
adjoint of reconstruction (orbit multiplicities, sign and conjugation actions): a
separate contract, not implied by equal primals. ad-jvp-comb/020 and /091 are the pins.

### 2.5 The commutativity sampler (LANGUAGE CHANGE)

formalism.md:1332 today: "two names bound to one array (`let v = w`) are sampled
independently ... a claim true only because they alias is refused." Replacement rule:

> Captured array reads are sampled by semantic operand key and canonical coordinate,
> honouring reconstruction. Identity-transparent aliases of one observed version share
> samples; distinct or unproved keys are sampled independently. A write mints a fresh
> version and invalidates every equality witness that depended on the old one; a law
> checked under capture equality applies only while that equality holds, else it is
> rechecked without it or refused. Formal kernel arguments stay independently sampled
> unless the law carries a sharing constraint.

A version minted after a write never retroactively repairs an unconditional certificate
accepted earlier; a deferred capture needs invocation-time validity or a lifetime proof.

### 2.6 Flow-sensitivity cost (stated, accepted)

Because a write mints a version, the output TYPE of `method_for(A, B)` with `B = A`
depends on whether a write to either can occur between the alias and the application.
That proof happens at elaboration, while today's write analysis is IR-level
(`Optimize.scheduleMayWriteIn` and friends). A conservative elaboration-time analysis
means a program's type can change when an unrelated helper gains a write. Accepted: the
analysis is fail-closed (dense), its decision is recorded by `blade plan`, and the
deferred-alias entry point (§2.1) needs none of it.

### 2.7 Identity minting sites (audit list)

Not only `sameIdentity` (Types.fs:105-110, where `AIDParameter` is never constructed
and `AIDDerived` never compares equal). Identities are minted from spelling at about ten
TypeCheckInfer.fs sites (9714, 9923, 10069, 14699, 15237, 15586, 16340, 16592), by
IRMono `coIterIdentityOf` (1839-1842; keyed by var id, with one shared
`AIDVariable "__coi"` fallback for every non-variable operand), by providers from file
variable names (CsvProvider.fs:302, NetcdfProvider.fs:482/536, ZarrProvider.fs:1176: two
stores with a same-named variable may collide today), and read by CodeGenCuda.fs:1946-1971
and Optimize.fs:2828/6496. P1 replaces every minting site.

## 3. Transpose as a structural function

Represent supported coordinate permutations and payload actions explicitly, retaining
the source's canonical read and ordered frame. Normalize composition, identity and
inverse; consume index-type certificates for symmetric identity, antisymmetric negation
and Hermitian conjugation (`inferTranspose` already selects among these,
TypeCheckInfer.fs:5138-5146, intra-slot only; `TDataMove` within a slot is refused).

An ordinary transpose keeps the origin and version but changes the oriented key. A
symmetric certificate may erase that distinction. Negation and conjugation stay explicit
actions unless a carrier-specific law simplifies them. Absorbing a transpose uses the
source's reconstruction law: never re-evaluate the producer in a new canonical region
or change its floating-point tree; an antisymmetric implicit diagonal zero keeps its
prescribed representation (no blind negation of every logical read).

**Fold order under absorption (new rule).** `reduce` is a left fold in ascending storage
order of its operand, pinned bitwise by `test interp` and `diff-oracle` under
`BLADE_FP_REPRO=1 BLADE_FP_CONTRACT=off`. A general dense transpose feeding a strict
fold (repro build, `where repro`, or an unlicensed kernel) keeps the transpose or
conversion unless an exact-order supported traversal is proved: the consumer's operand
sequence, association, seed, empty-input behaviour and failures are preserved, not only
the producer's tree. In a licensed fast build a contiguous source-order reduction may
replace the transposed traversal only when the kernel's own licence admits that
permutation and the logical domain is unchanged; a partial reduction must also keep
which output each cell feeds. Double transpose, certified symmetric identity and other
order-preserving cases are eligible in every build. No unrestricted strided walk.

A transformed value is realized by certified structural simplification, absorption
into a compatible consumer, a supported explicit materialization or conversion, or
rejection with a refactoring diagnostic. Compact-boundary refusals stay until their
target descriptors and lowering exist; nothing is silently decompacted.

## 4. Compute semantics and whole-program planning

`|> compute` keeps its typing and its place in dependency analysis. It COMMITS, at its
program point: a snapshot of its inputs' versions; complete evaluation of its domain
(so an empty-input BL8003 or a failing cell stays at the compute's site, in its order);
once-only effects of a writing producer; a stable result with fixed descriptor and
numerical contract; an ordinary value eligible for independent mutation and for the
existing printing rules. A pure deferred `let` commits none of these: its fused
consumers read CURRENT inputs, and its forcing and alias-class printing follow §10.2.
That is the observable difference between the spellings after P3, and it is why the
BL4005 remedy "bind `w |> compute` first" keeps its meaning: writing through the result
needs independent writable storage before the write. Only after these obligations are
explicit in the core may the marker stop emitting a loop or an allocation.

The whole-program graph is rooted in all observable values and ordered events (returns,
output, visible writes, external reads, failures, interface demands). Libraries and
unknown callees need explicit summaries; this is not a closed-world rule.

```text
typed canonical value graph + ordered obligations
    -> legal realization candidates
    -> deterministic cost selection
    -> explicit storage / fused / streamed / conversion plan (side tables)
    -> backend emission
```

Snapshots are preserved by static provenance, ownership and dependency analysis; keep
the materialization when old data cannot be proved available. A version label is not an
availability proof: eliminating a transpose buffer before a later input write requires
scheduling the reads first, retaining the old backing, or copying. Runtime version
counters, borrowing syntax, immutable storage and copy-on-write are later options.

Failing and effectful producers stay opaque in the first planner: an unused `compute`
that must fail is not dropped; a consumer's conditional read cannot suppress a required
failure; moving a check needs proof of identical success/failure and order; termination
is a precondition for pure elimination.

**Function boundaries ("THE CALLEE FORCES", Lowering.fs:458-475).** The documented
decision stands at every SURVIVING boundary: a call that is not inlined or jointly
lowered, an export, an unknown callback, a foreign interface keeps a materialized-array
ABI and the callee's logical forcing point. P4 relaxes only the mandatory PHYSICAL
allocation across analyzable internal boundaries by inlining or jointly lowering a
supported call after its commitments are preserved; when that proof fails the call and
its buffer stay. P1's inferred return types vary by sharing partition only (§2.2), never
by whether a caller writes `compute`.

## 5. What already exists and what changes

| Area | Existing implementation | Required extension |
|---|---|---|
| Identity | `Types.fs` `ArrayIdentity`/`sameIdentity`; `TypeEnv.fs` `VarInfo.Identity`; the minting sites of §2.7 | Versioned operand descriptor; every minting site replaced |
| Aliases and calls | `TypeCheckInfer.fs` name-derived identities; §10.2 deferred alias classes (`Lowering.deferredAliasClasses`, `refuseDeferredWritesAndUnorderedIfs`) | Propagate keys through the alias class first (§2.1); sharing-sensitive return typing |
| Specialization | `IRMono.fs` `canonTypeKey` (ignores identity) | Partition-keyed instances for identity-sensitive functions only |
| Iteration/storage | `IRLoopStructure.fs` `partitionIntoIdentityGroups`; `IR.fs` `ApplyInfo.Identities` | Consume resolved keys; freeze descriptor metadata |
| Transpose | `IRStorage.fs` `TransposeWithin`/`ReadTransform`; `inferTranspose`; `negate_pool`/`conjugate_pool` (nested_array_utilities.hpp:887-895) | Typed normalizer, certificates, the fold-order rule of §3 |
| Compute | `IRTComputation`, `TExprCompute`, `IRCompute`; `Lowering.forceCallableBody` (490) and `forceReturnCombinator`; G45 (writing maps computed at definition) | Commitments represented independently of allocation; the forcing kept as logical, not physical |
| Write legality | `Optimize.fs` `intervalPayloadOf` (1501), `scheduleMayWriteIn` (1531), `scheduleMayWrite` (1591), `storedArrayProof` (3688), `callbackTable`, the `IRTotality` whitelist, the five-party audit matrix | The producer graph generalizes these and inherits their audits |
| Existing eliminations | compaction fusion v2, hash join, group-aggregate, slice-write fusion, row-fold fusion, cell epilogues, the A7c kept inline (`BLADE_EPILOGUE_LEGS`, opt-in, G69 open) | Subsume as instances of the realization plan; do not duplicate |
| Effects | `Effects.fs` summaries (`isMovable` has no readers) | Read/write/version dependencies, demand and termination evidence |
| Program visibility | `Lowering.fs` whole-program shape specialization; `Optimize.optimizeModule` per module (3136) | Cross-module use census and producer graph |
| Validation | `IRValidate.fs`, decision records, lane differentials, `opt-diff` | Descriptor/version/plan consistency checks; migration pins (§8) |

Do not remove `IRCompute` patterns wholesale: lowering and emitter dispatch use them to
tell deferred computations from values.

## 6. Implementation sequence and exit gates

### P0 — Characterize the existing contract (mostly done by W4-1)

Master's W4-1 (5d4a28d0) pins alias and copy behaviour, compute force points, tuple
member ordering, `if`-value refusals and failure ordering across module, block,
function and lambda scope (loops/540-561, tuples/044-067), and records the open lane
holes G60, G77, G79; W4-2 (2bd7b485) covers function-body folds, captures and scope-exit
frees; W4-3 (f46fdfc0) makes the unseeded fold seed route-dependent (G80 open). P0 adds
only what those lack: sharing-dependent output types through helpers and aliases
(§0's probes as corpus tests), the comm-sampler alias case, declared-signature cases,
the AD slot cases, and the transpose emission baselines.

Exit: every behavioural difference in this plan is classified as a language change
(§2.3, §2.5, alias transparency), an exact optimization, or unsupported, and each
language change has a pinned before-case.

### P1 — Stable identities and typed sharing

Deferred-alias classes first (§2.1), then stored arrays with a fail-closed no-write
interval, then parameters (§2.2). Build the sharing partition before output deduction;
extend instance keys and call typing; replace every minting site of §2.7; implement
the declared-signature rule (§2.3) and the sampler rule (§2.5); update the formalism
wording in the same change.

Exit: aliases, alpha-renaming and helper extraction preserve canonical structure (the
§0 probes print packed); `[0,0]` and `[0,1]` calls do not share an instance; independent
copies stay independent; `(A, B, A)` keeps the adjacent-only rule; declared-dense
signatures keep today's bits; AD pins unchanged; a migration manifest lists every
changed expected type or output.

BUILT (wave 1, §2.1 only): a module-level `let g2 = g` over a module-level pure
deferred map takes g's identity (`Types.AIDAlias`, seen through by `sameIdentity`;
`TypeEnv.DeferredAliasRoots`; `TypeCheckSupport.loopOperandIdentity` at every spelled
operand site), so `method_for(g, g2)`, chains, `object_for` packs and `zip` behave as
the same-array spelling; both lanes and the interpreter unchanged below typecheck. Fails
closed (fresh identity, dense) unless the root is a plain `let` of a repeatable,
write-free apply combinator whose outside reads are transitively admissible, the alias
a plain unascribed `let` of a bare same-module name, and the use at module level in that
module. Corpus symmetry/146-148 (packed), 149-151 (the P0 before-cases: helper, stored
alias, scoped alias, all dense), 152-153 (fail-closed edges); formalism §8.3 and §10.2
reworded. A sweep of the 112 corpus/example files with a top-level bare alias changed
only 146-148. Open: stored arrays with a no-write interval, parameters / the sharing
partition (§2.2), §2.3, §2.4, §2.5 (the sampler is still binder-keyed), the IRMono
`__coi` fallback and the provider minting sites (§2.7). Noted: a module deferred alias
read only by a function body is BL9002 on master (pre-existing).

BUILT (wave 2, workstream B: §2.1's second step, §2.5, §2.7): a module-level
plain `let B = A` (chains included) over a STORED root -- an array literal, a
`|> compute` of a computed value, a call of a declared function -- of a plain or
compact array type takes A's identity (`AIDAlias`; `TypeEnv.StoredAliasRoots`,
`TypeCheckSupport.recordStoredAliasMember`, `aliasRootOf`) when the module's
NEVER-WRITTEN PROOF holds: a PRE-PASS over the module's SURFACE declarations
(`TypeCheck.storedAliasWriteProof`, installed before the first declaration is
checked, because identity is decided at typecheck and the write may come later
in the source -- lifting the typed-AST check was not possible, the later
declarations are untyped when the identity is needed), syntactic, module-wide and
fail-closed, mirroring `Optimize.aliasWriteClosure` and stricter than it at
every point (members = seeds plus every top-level binder whose declaration names
a member; refused when a member-naming declaration holds any assignment --
lambda parameter defaults, align pads and halo offsets scanned too -- names
an import or a qualified name, or names a writing definition, transitively; a
root that may share what it names -- a call, a `|> compute` of anything but a
map -- adds its whole reach to the seeds and is judged by its own and its
reach's declarations; any importer module refuses). The read-only alias plan
gained the same roots with the same reach (`aliasPlanChain`; a reach that names
a binding this module cannot see -- another module's array -- declines), so
every identity alias is one the plan may share; Lowering records
`[alias-identity v1]` per judged alias and cross-checks each applied one
against the plan's admission (`recordAliasIdentity`: a disagreement is an
internal error, not a record -- a Codex review found three holes, all closed
and pinned, symmetry/155, 160, `blade test optimize`); `[symmetric-storage v1]`
names `identity-transparent aliases: B = A`. `gram(M, N)` with `let N = M` is
the same-array gram. `BLADE_TRANSPOSE_ELIDE=0` changes the storage only. The
comm sampler (§2.5) keys captured array reads by `witnessArrayKey` (the root for
both alias kinds, at module level; binder keys elsewhere; formal arguments
unchanged). §2.7: IRMono's `coIterIdentityOf` mints a fresh literal identity per
non-variable operand (no shared `__coi`); provider identities carry the store
(`Types.providerVarIdentity`, `<provider>:<binding>.<var>`); `AIDDerived` is
retired (never constructed, never equal), `AIDParameter` kept with its
definition for §2.2. Corpus symmetry/154-160, loops/598; migration manifest
[plan-identity-transpose-compute-w2b-manifest.md](plan-identity-transpose-compute-w2b-manifest.md);
formalism §8.3 and the
sampler passage of §4 reworded. Open: provider-read roots (a provider load names
an import, so the proof declines them), a write-free import judged through its
callee's effects (imports decline today), the module-level-only scope (a
function body reading two aliases keeps two identities), an incremental notebook
path that checks an early cell before a later write.

BUILT (wave 2 workstream A, §2.2-§2.4): SHARING-PARTITION INSTANCES. After its
generic check a declaration's RELEVANT parameters are recorded
(`TypeCheckSupport.noteSharingRelevance`, `TypeEnv.SharingDeclSources`): array
parameters filling two or more bare-operand positions of one comm / anticomm
(or `reynolds`, or let-bound-kernel) application, a `gram`, or an eligible
identity-sensitive callee's relevant positions. A call whose relevant
arguments share an identity (`loopOperandIdentity`: name, deferred alias class,
an enclosing instance's parameter class -- any future key flows through) is the
call of the instance over that first-occurrence-normalized partition
(`TypeCheckInfer.checkSharingVariant`, cached per (origin, partition), named
`f__shareN`, placed after its origin like an arrow instance), whose relevant
parameters carry `AIDAlias (AIDParameter (instance, class), name)`
(`TypeEnv.SharingParamIdentities`): the first construction of `AIDParameter`.
The C++ / LLVM lanes and the interpreter consume the same specialized IR.
Fails closed to the generic (all-distinct) declaration with a `[sharing-partition
v1] f: declined -- ...` record: callbacks / lambda values / unresolved heads,
recursion, `static` / `mpi` / `cuda`, packs, array instances, `mut` or assigned
relevant parameters, synthesized declarations (`__` names, generated decls: the
AD bodies, §2.4), a declared non-scalar return type (§2.3), and an instance that
does not check. §2.3's ascription half: a dense `let` ascription the ordinary
check does not meet is re-checked with fresh operand identities at its own level
(`TypeEnv.DenseAscription`), accepted dense (was BL3001). `gram`'s same-array
test keys on identity, not spelling. The same re-check covers a declared dense
return's BODY and a tuple holding a dense array, transactionally (substitution
snapshot, instance caches, side channels, plan records). A call through a `let`
alias of the declaration is never re-targeted (the alias is reassignable).
Corpus symmetry/149 (pin updated: packed), 161-167 (renumbered at the merge: 154-160 are workstream B's), ad-jvp-comb/115,
multifile/043; `tests/OptimizeTests.fs` `plan_sharing_partition_*`; formalism
§5.1 and §8.3; migration manifest
[identity-transpose-compute-wave2a-manifest.md](identity-transpose-compute-wave2a-manifest.md)
(1252 candidates swept: only symmetry/149 changes). Open: the IRMono `__coi` fallback and provider minting
sites (§2.7), stored arrays with a no-write interval, §2.5 (the sampler), and
the structural-map relations of §2.2 (a transposed argument is its own class).

### P2 — Structural transpose normal forms

Typed map/action algebra, certificates, the fold-order rule of §3. Normalize before
sharing-dependent elaboration where it affects identity; after elaboration, rewrites
preserve the selected canonical application.

Exit: double transpose restores the original operand and frame with zero copies; a
read-only compact transpose alias copies nothing; symmetric collapse and
antisymmetric/Hermitian actions agree with existing reads; unsupported compact
transforms keep their diagnostics; strict-build fold bytes unchanged.

BUILT (wave 1, the two easy exits; `BLADE_TRANSPOSE_ELIDE`, an optimizer gate, rule
`transpose-normal-form v1`): `Optimize.normalizeTransposesModule` composes a transpose
chain to one permutation rebuilt as its minimal swaps and makes an identity chain its
base (an IR normal form every lane and the interpreter consume; admitted over a fresh
base, as an assignable binding's whole value, or read in place by a top-level expression
whose never-written closure holds; declined as a function's return value); and the
READ-ONLY ALIAS PLAN (`planAliasElision`, side table `AliasElideTable`, both emitters,
main's top level only, the interpreter keeps its copy): a never-written top-level
`let X = Y` over a stored fresh-rooted plain or compact array shares Y's storage --
this covers the erased SymIdx transpose `let ST = S` and, deliberately, EVERY plain
alias (limiting it to transposes would key on spelling). The never-written proof is the
syntactic `aliasWriteClosure` (assignments, writing statements and callees, other
modules), not `scheduleMayWriteIn`, which treats writes to fresh local storage as
private -- exactly the copies this removes (two Codex-found holes closed that way).
Corpus index-types/545-555; antisym / Hermitian actions kept and recorded as declines.
Open: chains through named bindings, the typed map/action algebra beyond swaps,
negation / conjugation simplification, absorption (and the fold-order rule above);
unchecked: an incremental notebook path that compiles an early cell before a later
write.

### P3 — Compute commitments and local materialization planning

Extract the graph of fixed canonical producers in finite, terminating, pure regions;
opaque boundaries for effects, partial operations without a certificate, unknown calls,
unsupported traversal, writable or external representations. Realization decisions are
side tables; the current materialized plan is the always-available baseline.

Exit: a single-consumer pure `compute` fuses without a pool at top level and inside a
function body; repeated consumers share or recompute only under the declared contract;
a write after `compute` cannot change the computed value; unused required failures and
empty-input BL8003 still occur at the compute's site; `test interp` passes with the
interpreter eager.

BUILT (wave 1, function / lambda / block bodies; `BLADE_COMPUTE_ELIDE`, an optimizer
gate, rule `compute-elision v1`; `Optimize.elideSingleConsumerComputesModule`): a
computed let with exactly ONE reader that is a fold (`reduce(x, k[, s])` as the whole
value of a later statement of x's spine) becomes the fused `IRReduceCompute`, or a
pointwise co-iteration reading x's cell once takes x's map as its operand and
elementwise fusion splices it. Admission (fail closed): one reader per function body; a
plain dense total (IRTotality) write-free producer over stored names or ranges with
literal-or-affine extents; no write or provider effect from the let through the
consumer (`scheduleMayWriteIn`); an unseeded builtin fold only over a literal extent
>= 1, its seed by W4-3's route rule (`Lowering.lowerUnseededSeed`: the exact identity on
a strict route, the base seed on a licensed one); CUDA modules, static / MPI /
arity-polymorphic callables skipped. A TREE REWRITE before the lane split (rule 8's
side-table form NOT built), so the interpreter runs the rewritten tree and `opt-diff` is
the gate. Corpus functions/539-547; `blade test optimize` pins no `allocate<` /
`blade_alloc_cells` in either lane for an elided x; the two pool-reuse fixtures run with
the gate off (accepted). WAVE 2 (workstream C): ONE LEVEL DEEPER -- a producer whose
operand is an elided map that passes the producer admission itself is admitted, so
`x0 -> x1 -> fold` reads both maps in place (functions/565; a third level declines);
TOP LEVEL UNDER A PRINT SELECTION -- in a single-module program with `BLADE_PRINT` /
`--print` set, the binding list is one spine and a binding the selection excludes is a
candidate (the record says the selection licensed it; printed bindings, provider and
side-table bindings are never candidates; `blade test` refuses `--print`, so its pins are
`blade test optimize` cases). Open: deeper chains, run-time-extent unseeded folds (need an emptiness
guard reproducing BL8003 in every lane), the side-table form; a BL8011 extent-mismatch
message after a pointwise elision names the producer's input rather than the temporary.

### P4 — Whole-program visibility

Cross-module producer/consumer and capture analysis after specialization; qualified ids,
export contracts, module initialization order and callable summaries kept consistent;
inline or jointly lower supported calls under the boundary rule of §4; recursive SCCs,
unknown higher-order calls and foreign interfaces opaque.

Exit: splitting a supported computation across files or helpers forces no avoidable
intermediate; effects keep their order; every escaping value has a realizable lifetime;
`blade run --memcheck` (ASan) and `test memfree-stress` are green on the new fixtures
(W4-2's merge-time fix and G69 are the precedent: the likely P4 failure is a
use-after-free at a scope exit, which value parity cannot see).

BUILT (wave 2, helpers in ONE module; `BLADE_CALL_ELIDE`, an optimizer gate, rule
`call-elision v1`; `Optimize.elideCallsModule`): a DIRECT call that is a statement's only
call and the sole operand of a fold (`reduce(prep(A), k[, s])`) or one operand of a
pointwise map / co-iteration is jointly lowered at that site -- the callee's scalar let
spine inlined under fresh ids, its one dense `|> compute` map (parameters replaced by the
arguments) the fused fold's `IRReduceCompute` operand or the map's operand (elementwise
fusion splices it) -- so the call allocates no return pool; the callee keeps its ABI and
forcing point at every other site (§4). LIFETIME ARGUMENT: the inline owns no allocation
(lets must be scalars; the map is read in place, or is the consumer's computed operand when
fusion declines), so no return-boundary event is needed (G69's IRReleasePools is not
used). A map kernel capturing the callee's scalars is CLONED onto the inlined copies
(DerivedFuncOrigins keeps it at its origin). Admission fails closed: another module's
callee, static / generic / repro / omp / cuda / mpi callees, a repro caller, captures,
recursion, a callee referenced as a value, non-stored arguments (deferred, streamed,
function-valued, `mut` / assigned), a writing callee, a body that is not a scalar let
spine ending in one plain dense total map over its parameters, names the body or kernel
does not bind, parameter-dependent map extents, a declared return layout the map does not
produce, a writing statement (the snapshot), several calls in one statement; the fold by
compute elision's rule (`elidedFoldSeed`, now shared). Corpus functions/554-564 (ASan clean
under `--memcheck`, both lanes where the LLVM lane takes the program), `blade test
optimize` call-elision cases (no call and no allocation at the elided site in C++ and
LLVM; the declines keep their calls). FOUND AND FIXED on the way (not gated): both C++
reduce emitters spell an inline array-returning CALL operand at every element read, so
`reduce(prep(A), k)` called prep n + 1 times (O(n^2)), leaked n + 1 pools and let a
writing fold kernel change the values later calls read; a fold call the pass KEEPS (a
decline, or the gate off) at statement level is now bound to its own let right before its
statement, evaluated once (functions/561 pins the snapshot). A first attempt in `IRLift`
was withdrawn after a Codex review: IRLift's call-argument walker hoists such a let ahead
of EARLIER arguments (`combine(bump(M), reduce(prep(M), (+)))` then printed 12 for 30;
functions/564 pins the order). Codex's other finding, a capturing lambda applied in a
callee's scalar let, now declines (functions/564). Open: the per-cell call in a fold
NESTED inside a larger expression or a kernel body (both C++ emitters), calls across
modules, a let-bound single-reader call (`let x = prep(A); reduce(x)`), kernel-body
callers, a non-scalar let in the callee (needs the return-boundary event), run-time-extent
unseeded folds (as in P3).

### P5 — Broader deterministic selection

A bounded family of storage, fusion, streaming, conversion and recomputation
alternatives in Optimize; the decision record reports the rule, facts and declines;
boundaries carry live versions, frames, layouts, sharing and effect progress; prune only
same-interface resource-dominated alternatives; preserve ties later consumers
distinguish. Optimality is claimed only for the enumerated family and stated cost model.

## 7. Proof correspondence (scoped to what the files prove)

| Artifact | Established result | What it does NOT establish |
|---|---|---|
| [BladeStructuralIdentity.v](../../proofs/BladeStructuralIdentity.v) (new, 18) | Key laws: transpose is an involution on the oriented key and keeps origin/epoch; `bind_alias` copies the key (by definition); same keys select `SymmetricPair`, distinct keys `DensePair` (by definition); two computed witnesses (`ordinary_types_do_not_determine_result_layout`, `distinct_add_outer_need_not_be_symmetric`) | Layout soundness (no theorem links `SymmetricPair` to a symmetric read); any mutation model (`epoch` is never incremented); the paper's alias-substitution theorem (prose only) |
| [BladeMaterialization.v](../../proofs/BladeMaterialization.v) (new, 21) | `packed_read_equals_virtual`: a finite buffer tabulated from a descriptor reads back the virtual reader (obligation `locate_bound`), with structural zeros ahead of actions; lifted by congruence to graphs (`pure_graph_materialization_elimination`) and to an ordered effect/snapshot skeleton (`materialization_elimination`: final store, trace and result equal for every retention policy) | Anything operational: the policy selects between two readers defined from the SAME tree over a persistent environment, so allocation counts, evaluation counts, lifetimes and old-version availability are untouched; live reads of mutable backing (pure nodes never read the store); failing or effectful cells (scalars are total) |
| [BladeLayout.v](../../proofs/BladeLayout.v) (pre-existing) | Licensed transpose/direction laws; minimum summed-Kendall schedule | A cache model |
| [BladeLayoutOptimizationBridge.v](../../proofs/BladeLayoutOptimizationBridge.v) (new, 10) | `existing_canon_if_admissible` reuses BladeLayout's minimality on an admissible subset; argmin properties | "Forward" (schedules carry no direction; `admissible` is an opaque bool); resource dominance (assumed by `resource_factor`/`resource_monotone`) |
| [BladeCompositionalOptimization.v](../../proofs/BladeCompositionalOptimization.v) (new, 35) | Correct Pareto-frontier pruning at a fixed depth: `dominates` is a partial order, the frontier covers the exhaustive set, an optimum survives for every `cost_le`-monotone objective; reindex/packing laws under assumed `rank`/`enumerate` bijections | That pruning prunes (all theorems hold for `prune := id`); depth-varying plans; the paper's composite theorem (tex l.563, not mechanized); any link to BladeDMWF's proved bijections |

Before the proofs merge: the README entries and `docs/proofs.md` must say the above
(per-file `##` sections, "at a glance" rows, "What remains unproved" entries, README
entries appended in `_CoqProject` order, trailing `Print Assumptions` removed, the tex's
"validation records accompany" sentence removed). Codex has accepted this re-scoping.

## 8. Validation matrix

| Case | Required observation / evidence |
|---|---|
| Alias, alpha-renaming, helper extraction | Same key relationships, packed result, exact reads; the §0 probes as pins |
| Same/distinct arguments with identical types | Correct return layout; distinct instances per partition |
| Two copies independently mutated from one origin | Unique versions; no semantic or storage alias |
| Declared dense signature / ascription with shared arguments | Dense, today's bits; index-types/242 unchanged; the former BL3001 case accepted |
| `ad.grad` / `jvp` / `vjp` over a two-parameter body at `(A, A)` | Independent slots; ad-jvp-comb/020, /091 unchanged |
| Comm sampler over an alias, with and without an intervening write | Claim accepted / refused per §2.5 |
| Double transpose; Sym/Antisym/Herm inputs; negated compact reads | Normal form and reconstruction agreement, zeros and fixed points, signed-zero cases |
| Transposed consumer of a strict fold | Bytes equal to today under `BLADE_FP_REPRO=1 BLADE_FP_CONTRACT=off` |
| Pure compute with one/many consumers, empty domain, in a helper | Values; justified allocation and evaluation counts; BL8003 at the compute's site |
| Producer may fail in an unrequested cell; conditional consumer | Required failure preserved; output prefix and abort code |
| Snapshot, input write, transposed consumer | Reads the earlier version; backing/copy/scheduling accounted for |
| Writing producer with several consumers | Once-only writes in order; never duplicated by recomputation |
| Program split across modules or helpers | Same observations; no accidental module barrier; ASan clean |
| Foreign/writable return or unknown call | Explicit boundary or conservative refusal |

Gates. A non-gated elaboration change is invisible to `opt-diff`, and to `test interp`
and `diff-oracle` (the interpreter consumes the same typed IR: Interp/Loops.fs:1110
reads `info.Identities`). Only corpus pins catch a P1 regression, and today NO corpus
file feeds a plain `let X = Y` array alias into a comm or gram application: P1 ships a
W4-1-style refusal-and-emission sweep with a manifest
(`bench/notes/round3/w4-1-sweeps/` is the template) and new pins, not just updated
ones. P3/P4 realization decisions are side tables, so `test interp`, `diff-oracle` and
`opt-diff` all keep their power; `--memcheck` is part of P4's gate. Pin numerical modes
where a bit-exact comparison needs them; measure cost separately from correctness, at
non-power-of-two extents with interleaved A/B runs.

## 9. Non-goals and follow-up decisions

No live mutable transpose view, no content-based identity, no unrestricted strided
traversal, no runtime COW by default, no claim of globally fastest hardware execution.
The normalizer is bounded. Before P4: the exported sharing-witness ABI, the resource
model and candidate family, the ownership/borrowing policy for external and writable
values, and whether the compact-to-dense boundary coercion of §2.3 is wanted. None is a
prerequisite for P1 or P2.

Incidental: a `Poly<T^1>` tail-pack kernel applied via `object_for(cg) <@> (A, A)`
raises BL6001 "dangling VarId reference" on master (review §2.12); unrelated, to be
filed separately.

## Appendix: Codex consultation (2026-10-24, read-only, PAL clink)

Codex, asked the eight questions behind §2.3-§2.5, §3's fold rule, §4's boundary rule,
§0 and §7: proposed the sampler wording adopted in §2.5; confirmed the compute
commitments of §4 (adding the BL8003 empty-input point); proposed keeping a materialized
ABI at surviving boundaries while relaxing physical allocation internally (adopted, §4);
proposed compact-to-dense boundary coercion (recorded as the alternative in §2.3; the
simpler declared-layout rule was chosen); proposed independent AD slots (adopted, §2.4);
proposed the strict/licensed fold-order split (adopted, §3); gave the gain and loss
inventory of §0; accepted re-scoping the proof prose, holding that
`materialization_elimination` does conclude full observation equality and that
`snapshot_read_preserved_by_later_write` does model a store write followed by a
persistent snapshot read (both true and both reflected in §7's wording). Its factual
corrections to the review: "no mutation" should read "no live reads of mutable
backing" (`write_store` exists), "nothing is stored" should read "no operational
retention semantics" (a finite list is defined), and the paper does state the
reduction-order obligation (tex l.355-356) without a concrete rule.
