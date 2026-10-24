# Review: `codex/structural-transpose-proofs` (c6a9b8b3)

Reviewed 2026-10-24 against master cf2028cc. Reviewers: Claude (lead) plus two Opus
sub-reviews (Rocq models; plan-versus-compiler). Probes ran on a fresh build of
master cf2028cc; probe sources are listed at the end.

## Verdict

**Proofs: mergeable after doc fixes. Plan: send back for one revision.**

The branch is two commits, ten files, docs and Rocq only. It changes no compiler code
and no syntax. All four new `.v` files compile on Rocq 9.0.1, every counted result is
closed under the global context (84 probes), `coqchk` passes, `count-theorems.ps1
-Check` confirms 2205, index line endings are LF, and the branch merges onto master
with no conflicts (master has not touched `proofs/` or `docs/proofs.md` since the base
361504b6).

What the proofs establish is much smaller than the README and `docs/proofs.md`
paragraphs say. The plan's direction is right and its safety stance is sound, but it
was written against a base that predates round-3 wave 4, it never analyses the
function-boundary problem that will actually decide P3/P4, and it names none of the
concrete migration breaks alias transparency causes today.

## 1. The Rocq files

| File | Verdict | One-line content |
|---|---|---|
| BladeStructuralIdentity.v (18) | sound, overclaimed | definitional unfoldings plus two `vm_compute` witnesses; no mutation model |
| BladeMaterialization.v (21) | sound, overclaimed | one in-bounds tabulation lemma (`nth k (map f (seq 0 n)) = f k`) lifted by congruence |
| BladeCompositionalOptimization.v (35) | sound, mildly overclaimed | a correct, standard Pareto-frontier (label-setting) argument at fixed depth |
| BladeLayoutOptimizationBridge.v (10) | sound, thin | argmin over a filtered list; `checker_sound` carries the whole conclusion |

### 1.1 The retention policy is semantically inert (HIGH, BladeMaterialization.v:134-158, 247-249)

`realize retain env n` is `packed_read d (cells env n)` when retained and
`reconstruct d (cells env n)` when eliminated. Both are readers closing over the same
predecessor `env` and evaluating the same scalar tree at read time; the retained branch
only adds a tabulate-then-`nth` round trip. Nothing is stored, no allocation exists, and
`run_program` passes the policy only to `PureBlock`. `materialization_elimination` and
its corollaries are therefore `packed_read_equals_virtual` (whose one real obligation is
`locate_bound`) lifted through `env_equiv`. "No reassociation" (l.92) holds vacuously
because both branches use one tree. Evaluation counts are not modelled.

The header's "A retained node stores a real finite list" and the README's "Arbitrary
buffer-retention policies preserve ..." should say: denotational model, both readers are
defined from the same producer tree, the theorem is the readback lemma. The plan §7
scope column is nearer the truth than the README.

### 1.2 There is no mutation and no live view (HIGH, BladeMaterialization.v:206-230, 259-272; BladeStructuralIdentity.v:118-130)

Pure nodes read `env`, never the store `s`; the only store read is `Snapshot`, which
binds a reader over a persistent functional store. So the one case where eliminating a
buffer is dangerous, a deferred read after a write to its input, cannot be expressed.
`snapshot_read_preserved_by_later_write` contains no `PureBlock`.
`unconsumed_failure_cannot_be_erased` is `Aborted e <> Returned []` by `discriminate` and
involves no elimination; a failing cell that is never read and gets eliminated is
excluded because scalars are total. In StructuralIdentity, `epoch` is never incremented
anywhere and `mutation_distinguishes_snapshot_from_live_view` only shows two different
functions have different transposes at one point. README's "Proves snapshot stability
across writes" and the "snapshot/live-view distinction" claim are oversold.

### 1.3 Layout selection has no soundness theorem (MEDIUM, BladeStructuralIdentity.v:86-104)

`pair_result_layout` is `if key_eq_dec a c then SymmetricPair else DensePair`;
`exchange_licensed` is a bare `bool` tied to no kernel law; no theorem connects
`SymmetricPair` to a symmetric `read_key`. `alias_preserves_key` holds because
`bind_alias` is defined as key copying. The paper's "Theorem (Alias substitution)"
(tex l.149) has a prose proof only; its Coq counterpart is this update lemma.

### 1.4 Frontier pruning does not prove it prunes (MEDIUM, BladeCompositionalOptimization.v:223-296, 363-402)

`prune_sound`, `prune_covers`, `frontier_sound`, `frontier_complete_up_to_dominance` and
`optimal_frontier_is_globally_optimal` all hold for `prune := id`. No antichain or size
bound is proved. What is proved is correct and useful: `dominates` is a partial order
(equal state required), and because the frontier does not depend on the objective, an
optimum is preserved for every `cost_le`-monotone objective. Optimality is at one fixed
depth n; terminals reached at depth < n are not compared, and the header's "a bound can
collect these frontiers" (l.10-11) is not proved. The packing section's
`enumerate`/`rank` bijection and `reconstruction` are assumed outright, not connected to
BladeDMWF / BladeMixedRadix's proved bijections, and no Section is instantiated.

### 1.5 The bridge's "forward" is a name (MEDIUM, BladeLayoutOptimizationBridge.v)

Imported names match BladeLayout.v (`sched` l.1543, `sched_cost` l.1545, `canon_sched`
l.1688, `foldpick_*` l.1663/1674, `canonical_is_cost_minimal` l.1706,
`direction_is_free_gauge` l.1314). But `sched` has no direction component, `admissible`
is an opaque `bool`, `forward_reduction_certificate` is `exact B.direction_is_free_gauge`
re-exported and counted as new, and `layout_minimum_lifts_to_resource_dominance` holds
because `resource_factor` and `resource_monotone` are hypotheses. The genuine reuse is
`existing_canon_if_admissible`.

### 1.6 The composite theorem is paper-only (MEDIUM, tex l.563, l.615)

"Compositional optimization with structural transpose" has no Coq counterpart: `Frontier.succ`
is never tied to `certified_successors`, the Bridge never instantiates `Frontier`,
Materialization never discharges the Frontier `invariant`. tex l.615 says validation
records accompany the sources; the diff adds none.

### 1.7 Conventions (LOW)

- README: the four entries sit at the top of Contents in the order Materialization,
  StructuralIdentity, Compositional, Bridge; the list's convention is append at the bottom
  in `_CoqProject` order. Blank lines between the new entries are inconsistent.
- `docs/proofs.md`: every one of the 53 existing files has a `## BladeX.v (N theorems)`
  section, a row in "The tower at a glance" and entries under "What remains unproved".
  The new files get one preamble paragraph that names no theorem, which breaks the
  document's own rule "every claim here names its Coq artifact".
- All four files end in `Print Assumptions`; no other tower file does (the tower uses
  `check-assumptions.ps1`). BladeMaterialization.v redefines `environment`, shadowing the
  import. Constructors `Check`, `Abort`, `Return` shadow vernacular words.

## 2. The plan

### 2.1 It predates W4-1 and P0 is partly done (HIGH, plan l.39-67, 187-195)

Master commit 5d4a28d0 added formalism §10.2 "Aliases and printing of deferred maps"
(docs/formalism.md:2152): a top-level `let g2 = g` over a deferred map IS the same deferred
value ("substitution holds"), the map and its aliases form one alias class whose value is
fixed at the first forcing event, a block or function-body alias is a forcing copy, writes
through a deferred map or its aliases are BL4005 by a program-wide fixpoint
(`Lowering.refuseDeferredWritesAndUnorderedIfs`). Tuple members evaluate at construction;
`if` values selecting computed arrays are BL6002. All pinned in loops/540-561 and
tuples/044-067 across module, block and function scope. That is most of P0's list. W4-1
also left open lane holes P0 should inherit (G60, G77, G79). W4-3 made the unseeded fold
seed route-dependent, which any re-planned fold must honour (G80 is a related hole).
None of this exists at the branch base; the tex's "inspected connection points" (l.621)
lists §5.2 and §8.3 only.

### 2.2 The current contract is already inconsistent on aliases, and that is the right P1 entry (HIGH)

Probe (master cf2028cc):

```blade
let x = [1.0, 2.0, 3.0]
let g = method_for(x) <@> lambda(v) -> v * 2.0
let g2 = g
let Pgg  = method_for(g, g)  <@> lambda(a, b) where comm(a, b) -> a * b |> compute
let Pgg2 = method_for(g, g2) <@> lambda(a, b) where comm(a, b) -> a * b |> compute
```

prints `Pgg = [[4.0, 8.0, 12.0], [16.0, 24.0], [36.0]]` (packed) and
`Pgg2 = [[4.0, 8.0, 12.0], [8.0, 16.0, 24.0], [12.0, 24.0, 36.0]]` (dense). §10.2 says
the alias is the same value; §8.3's name-keyed identity groups see two arrays. Aliases of
deferred maps are provably the same value with no write possible: the lowest-risk first
step, better than general versioned SSA keys.

Note the formalism also documents the opposite rule deliberately for the comm sampler
(docs/formalism.md:1332: "two names bound to one array (`let v = w`) are sampled
independently ... a claim true only because they alias is refused"). The plan must say
what happens to that rule.

### 2.3 Alias transparency breaks ascriptions, declared returns, printing and possibly AD; the plan names none (HIGH, plan l.91-94, 197-208)

- `let P: Array<Float64 like I, I> = method_for(A, A) <@> comm-kernel |> compute` is
  BL3001 today (got `SymIdx<2, I>`). Every program ascribing a dense type to
  `method_for(A, B)` with `B = A` becomes a type error under transparency.
- A function declared `-> Array<Float64 like I, I>` with a comm body, called `mk(A, A)`,
  returns dense today (index-types/242 `make_g` is this shape). Sharing-sensitive
  specialization contradicts the declared signature. The plan needs a rule: the
  signature fixes the layout, or a subsumption / decompaction rule.
- Printing changes shape even where values do not (`gram(A, A)` prints packed,
  `gram(A, D)` with `D = A` prints dense today).
- AD is never mentioned (grad, jvp, vjp, Grad*). ad-jvp-comb/020 and /091 take
  `ad.grad` over a two-parameter `gram(a, b)` body; a [0,0] elaboration would route
  per-argument cotangents through a packed-gram adjoint. §2's "fixed representation
  contract for higher-order interfaces" must name AD.

### 2.4 P3's payoff at top level is already a user choice; the real gap is function bodies (HIGH, plan l.125-163, 221-233)

Today `let x = ... |> compute; let s = reduce(x, (+))` allocates a pool for `x` and plans
`reduce over a materialized array`; the same program without `|> compute` allocates
nothing and plans `reduce over a deferred computation (one level)`. So at top level the
user already picks storage + snapshot + failure site (compute) versus fused reading of
current inputs (deferred); a write-after-compute probe confirms the snapshot
(`y = [2.0, 4.0, 6.0]`, `z = [2.0, 100.0, 6.0]`). P3 must say what `|> compute` still
means relative to a deferred `let`, or the BL4005 remedy "bind `|> compute` first" loses
its operational meaning.

The structural allocation forcing is in function bodies: `Lowering.forceCallableBody`
(Lowering.fs:490) wraps every function-body let and return combinator in `IRCompute`,
and G45 makes writing maps computed at their definition in every scope. `§5` lists
none of `forceCallableBody`, `forceReturnCombinator` or G45.

### 2.5 P4 reverses a documented decision and walks into G69 (HIGH, plan l.235-246)

Lowering.fs:461-469 records "THE CALLEE FORCES": the emitted signature already promises
a materialized array, laziness never crosses a named-function boundary, and sinking the
caller's compute into the callee "would make the return type vary by call site -- a
monomorphization axis with no payoff". P1's sharing-sensitive returns and P4's
cross-function elimination reopen exactly that; the plan should cite it and argue.
Master's only cross-function materialization motion (A7c kept one-level inline,
`BLADE_EPILOGUE_LEGS`) took nine review rounds, ended opt-in with a BL7005 warning, and
its lifetime audit is still incomplete (G69). W4-2's merge-time fix was a use-after-free
at a scope exit. P4's exit gate needs memcheck/ASan evidence, not value parity alone.

### 2.6 §5's Effects row points at the wrong machinery (MEDIUM, plan l.178)

`Effects.isMovable` has no readers. The write-legality machinery every eliminating pass
uses is in Optimize.fs: `intervalPayloadOf` (1501), `scheduleMayWriteIn` (1531),
`scheduleMayWrite` (1591), `storedArrayProof` (3688), `callbackTable`, the `IRTotality`
whitelist and the five-party write-audit matrix. Several passes already eliminate
intermediates under fail-closed demand censuses (compaction v2, hash join, group-agg,
slice-write fusion). P3's producer graph should generalize these and inherit their audits.

### 2.7 P2 cannot absorb a transpose into a fold without changing its order (MEDIUM, plan l.96-123, 210-219)

`reduce` is a left fold in ascending storage order. Absorbing `transpose(M)` into a
consumer fold means a strided walk (violating the plan's own decision 6) or a changed
association (breaking `test interp` / `diff-oracle` byte identity under
`BLADE_FP_REPRO=1 BLADE_FP_CONTRACT=off`). The plan guards the producer's tree but not
the consumer's. Baseline today: `transpose(transpose(M))` emits two full copies with zero
plan decisions; a SymIdx transpose is erased at typecheck but the alias binding still
copies the pool; antisym emits `negate_pool` into a fresh allocation. Double transpose
and alias-copy elimination are easy, unnamed P2/P3 targets.

### 2.8 The identity audit is under-scoped (MEDIUM, plan l.202)

Identities are minted from spelling at about ten TypeCheckInfer.fs sites (9714, 9923,
10069, 14699, 15237, 15586, 16340, 16592), by IRMono `coIterIdentityOf` (1839-1842,
keyed by var id with a shared `AIDVariable "__coi"` fallback), and by providers from file
variable names (CsvProvider.fs:302, NetcdfProvider.fs:482/536, ZarrProvider.fs:1176).
`AIDParameter` is never constructed; `AIDDerived` never compares equal. `canonTypeKey`
ignores identity, so `f(A, A)` and `f(A, B)` share one instance today and a function body
never sees call-site sharing: "sharing-dependent calls" is a new capability, not an
extension. (`object_for(cg) <@> (A, A)` is packed, `(A, D)` with `D = A` is dense,
`wrap1(X) = L <@> (X, X)` packed, `wrap2(A, A) = L <@> (X, Y)` dense.)

### 2.9 Mutation makes output types flow-sensitive (MEDIUM, plan l.50-55, 199-204)

`let B = A` deep-copies at top level and `B(0) = 9.0` leaves `A` intact (loops/081 holds).
The plan's headline example, `let B = A; method_for(A, B)` symmetric, therefore requires
proving no write to either between alias and application, during type elaboration,
while today's write analysis is IR-level in Optimize. A conservative analysis means a
program's TYPE changes when an unrelated helper gains a write. Two by-spelling contracts
must stay storage-level and should be named: BL4005 on `mut` arguments and
functions/145 "same binding".

### 2.10 No gate sees a non-gated elaboration change (MEDIUM, plan l.91-94, 290-313)

An elaboration change has no knob, so `opt-diff` cannot see it; `test interp` and
`diff-oracle` consume the same typed IR (Interp/Loops.fs:1110 reads `info.Identities`).
Only corpus EXPECT pins catch a P1 regression, and no corpus file today feeds a plain
`let X = Y` array alias into a comm/gram application: the behaviour being changed is
essentially unpinned. "Migration cases" is realistic in volume (a W4-1-style sweep with
a manifest), but it is mostly new pins. For P3/P4, implement realization decisions as
side tables read by the emitters (as `PoolReuseTable`, `HotRowTable` do) rather than tree
rewrites, so the eager interpreter stays an independent oracle.

### 2.11 Status claims (LOW)

Base, file list, proof counts and clean merge all check out. The "assumption audit passed
for all 2,205 results" line is true but is not evidence for P1-P4's real risks (physical
availability, source-to-graph lowering, lifetimes), which no theorem touches. §7 does not
distinguish pre-existing BladeLayout.v from the new files.

### 2.12 Incidental ICE found while probing (LOW, unrelated)

A `Poly<T^1>` kernel `let a :: b = A; mean((a - mean(a)) * (b(0) - mean(b(0))))` applied
via `object_for(cg) <@> (A, A)` gives `error[BL6001]: [IR Validation] in function 'cg':
dangling VarId reference: v35` on master. Probably a tail-pack misuse, but it should be a
diagnostic, not an internal error. Probe: `p4c_bad.blade`.

## 3. What is right

- Binding ids, semantic keys and storage identity are separated; loops/081 copy
  independence is kept; (A, B, A) stays on the §8.3 adjacent-only rule (f882eb22).
- Alias transparency is called a language change, never a gated optimization.
- Eliminated buffers need an availability proof, not a version label; failing and
  effectful producers stay opaque; unused required failures survive; termination is a
  precondition; `IRCompute` patterns are not stripped wholesale; `opt-diff` is required
  on top of lane parity.
- Every §5 identifier exists where named and most descriptions are fair.
- The Rocq is axiom-free and funext-free (`env_equiv` + `scalar_congruent`), `locate_bound`
  is the right obligation, the frontier development is clean, `existing_canon_if_admissible`
  is a real reuse of BladeLayout's minimality theorem, `locally_cheapest_is_globally_worse`
  and `interface_collision_refutes_cost_factorization` are honest witnesses.

## 4. Requested changes

Before merging the proofs:
1. Rewrite the README entries and the `docs/proofs.md` paragraph to the scope the files
   actually have (1.1, 1.2, 1.3); add the per-file `##` sections, "at a glance" rows and
   "What remains unproved" entries the document requires; move the README entries to the
   bottom in `_CoqProject` order; drop the trailing `Print Assumptions`.
2. Either mechanize or de-headline the tex's composite theorem (1.6) and remove the
   "validation records accompany" sentence.

Before accepting the plan as the design doc:
3. Rebase P0 on W4-1's §10.2 contract and inherit G60/G77/G79/G80; make the deferred-alias
   inconsistency (2.2) the P1 entry point; state what happens to the comm sampler's
   independent-sampling rule (formalism:1332).
4. Add a migration section covering ascriptions, declared return types, printing shape
   and AD (2.3), with the rule for declared signatures.
5. Define `|> compute` versus a deferred `let` after P3 (2.4); add `forceCallableBody`,
   `forceReturnCombinator`, G45 and the Optimize.fs write-audit machinery to §5 (2.6).
6. Cite and answer "THE CALLEE FORCES" and G69 in P4; require memcheck evidence at its
   exit gate (2.5).
7. Address consumer fold order under transpose absorption (2.7); widen the identity
   audit list (2.8); state the flow-sensitivity cost (2.9); commit to side-table
   realization decisions and a pin-writing sweep (2.10).

## Probes

Fresh master cf2028cc build and probe sources are under the session scratchpad,
`agentB\master` and `agentB\probes` (p1 alias/print, p2 fn instance sharing, p3/p8
transpose emission, p4/p4b pack sharing, p4c ICE, p5* writes and mut, p6* compute versus
deferred fusion, p7 deferred alias comm, p9/p9b ascription and declared return).
Rocq probe (84 `Print Assumptions` queries) under `agentA\probe_new.v`.
