# Plan: the fastest-way principles (determinism, dominance-or-gate, advise-don't-apply)

Status: **DESIGN — nothing built.** Makes "the fastest way is the only way"
enforceable rather than aspirational: three principles, each with a suite-level
instrument, calibrated on mechanisms the repo already runs (the omp census
marker, the BL4010/BL4011/BL4014 suggestion channels, `--strict-pins`, the
Surface block's generated-manifest guard, `toolchain.json`). Revised same-day:
T1 gained equivalence-class convergence and the route enumerator; T2's
taxonomy gained the machine-parametric class; P3 widened to whole programs.
Line numbers cite the working tree as of this writing and will drift.

## 1. What the slogan can honestly claim

The absolute reading — *every emission is the fastest possible* — is
unprovable: "fastest" is a machine property, not an IR property, and our own
measurements document the inversions (reassociated folds ~10% *slower* on
mirror folds; power-of-two extents ~7x on dense layouts; the 1.53x flat-write
result was alloc/teardown, not addressing). There is also no proving a
candidate set complete — there is always an emission nobody enumerated.

What the slogan *is* claiming splits into two theorems and one invariant:

- **T1 (determinism and convergence).** Emission is a pure function of the
  typed program, its source declarations, and the recorded environment
  manifest θ (§2). Same inputs → byte-identical output. Moreover, *provably
  equivalent spellings of the same computation converge*: the compiler owns a
  syntactic layer whose job is to prove equivalence and take the faster path,
  so equivalent formulations do not emit different (and differently-fast)
  code.
- **T2 (determined-up-to-knowledge).** Every choice between semantically
  equal emissions is determined by something we can point at: a
  machine-independent dominance proof, a recorded fact about this machine, or
  a declaration in the user's source. No choice rests on a guess. Heuristics
  are precisely the branches that would rest on one.
- **P3 (advise, never rewrite).** The compiler may analyze the *whole
  program* — across bindings, across independent deferred computations — and
  name a faster formulation; but restructurings of what the user wrote are
  suggestions in Blade surface syntax, applied by the user, never silently by
  the compiler. Advisory analyses are read-only: emission is byte-identical
  with the advisor on or off.

The dividing lines, stated once: **normalization within a computation, advice
across computations, licenses wherever bits change.** Spelling variants of
one computation are the compiler's to canonicalize (T1); combining or
restructuring distinct computations changes which program the source *is*,
and the surface program is the unit of meaning — that freedom is the user's
(P3); and any choice that changes observable bit patterns (reassociation,
parallel folds, BLAS's last ULP) is decided by nobody's cost model — it is
declared (T2's licensed class).

Two structural facts make this feasible here and not in a conventional
compiler. Deferral makes the route space well-defined: an `object_for`
pipeline or deferred reduce presents its whole DAG before anything is
scheduled, and eagerness is itself a typecheck decision (the four
`inferReduce` routes, `TypeCheckInfer.fs:1605/2146/11563/11570`), so the
routes for a computation form a finite enumerable set, not an open rewrite
space. And static index types put extents in the type, so extent-dependent
crossovers resolve deterministically *per instantiation* without profiling.
The genuine boundary is runtime extents (`RaggedIdx`, dynamic `group_by`):
decisions there must be dominant independent of n, or θ/license-resolved.

## 2. The cost model M(θ) and the environment manifest

M = work counts over the typed IR with static extents substituted: element
reads, element writes, flops, bytes allocated. Machine-independent dominance
means ≤ on every counter for every input, < on at least one.

θ = the recorded description of this machine: cache geometry, SIMD width,
core count, BLAS/CUDA presence, toolchain identity. θ lives in the
`toolchain.json` regime (plan-toolchain-packaging.md — LANDED, doctor/setup
phases open): probed by `blade doctor`/`blade setup`, written once, versioned,
inspectable. **Compile time never probes hardware; it reads the manifest.**
That is what keeps T1 whole while admitting machine facts: emission is
reproducible given (program, declarations, θ), including on a *different*
machine handed the same manifest — which is also what makes perf regressions
bisectable.

So a choice is *determined* when it is a function of things we can name:
proof (M), manifest (θ), or declaration. The old formulation — "machine-tied
therefore gate it to the user" — was too coarse: if we genuinely know how
this architecture prices bricked vs. linear addressing, the compiler should
decide, not the user. The user-facing knob is reserved for what no model can
decide: semantics (§3, LICENSED).

## 3. T2 — the decision taxonomy and the census

**Decision point**: a branch in TypeCheck/Lowering/IR passes/CodeGen
selecting between semantically equal emissions. Branches on typed structure
(rank, symmetry class, index kind, static extents) select semantics-carrying
representations and are not census subjects. Every decision point is
registered as exactly one of:

- `DOMINANT(name, proof-ref)` — never worse under machine-independent M, for
  all inputs. Obligation: a `docs/proofs.md` row, machine-checked (the rank
  dichotomy is the exemplar) or corpus/bench-pinned.
- `PARAMETRIC(name, θ-keys, rule-ref)` — the winner depends on the machine,
  and the machine is *known*: selection is a stated, pinned function of
  (program shape, θ). Deterministic given the manifest; bench obligation on
  the rule, not on each compile.
- `LICENSED(name, declaration)` — the choice changes observable results
  (bit patterns, evaluation nondeterminism) and therefore belongs to no cost
  model. Resolved only by user declaration: `where omp`, `BLADE_FP_REASSOC`,
  byte-exactness opt-outs.

**Seed census** (P1 must make this exhaustive):

| Decision | Class | Resolution |
|---|---|---|
| BLAS routing | PARAMETRIC + LICENSED opt-out | availability is θ (today: `OPENBLAS_DIR` present ⇒ on; `Build.fs:824` four-state) — **default-on when available stands**: the fastest way ships. Byte-identity with the interpreter is a *testing* license, not the shipping default: the diff-oracle lane pins `BLADE_BLAS=0`, and `BLADE_BLAS=0` remains the user's exactness opt-out |
| cuBLAS L3 routing | PARAMETRIC (θ: device present) — today strictly opt-in via `BLADE_CUBLAS`; graduates to θ-resolved when the manifest records the device and the crossover rule is pinned (`CodeGenExpr.fs:2403` site ordering) |
| Simplex-blocked bricks | PARAMETRIC candidate | the "unset knob auto-bricks" default is legitimate iff bricking is a stated function of (fold shape, θ-cache-geometry). Grounding exists both ways: licence×bricks measured 6.2x on gram, S0 measured ~7% cost for no win (plan-simplex-blocked-compute.md) — so the rule must select, not blanket. P1 obligation: state the rule, pin it, and until then this row is the one known unclassified branch |
| Reassociated folds | LICENSED | `BLADE_FP_REASSOC` changes bits; also measured non-dominant (mirror-fold −10%), so it could not even be PARAMETRIC-defaulted without a shape-aware rule |
| OMP emission | LICENSED | parallel folds reassociate; that is exactly why BL4016 refuses an `omp` fold with no reorder licence. `BLADE_OMP_THREADS` suppression stays the declared override (`CodeGenFusion.fs:190`) |
| Eager vs. deferred | route family (§4) | four `inferReduce` routes + the five deferred-reduce declines (plan-deferred-combinators.md); P1 classifies each decline as expressibility (fine) vs. profitability (must become a route comparison) |
| `-march` / `-ffp-contract` | PARAMETRIC / LICENSED | `-march` is θ (native = this machine's ISA, recordable in the manifest); `-ffp-contract` changes bits → licensed, with the shipping default a declared one |
| Exe cache (`--no-cache`) | out of scope | not an emission decision; identical emission is T1's business |

**Mechanism.** The omp census marker is the worked precedent
(`CodeGenExpr.fs:980`; block form `CodeGenState.fs:659`): "never asked" and
"asked, couldn't honour" must not be byte-identical — the special case of the
general rule that no route decision is silent. Generalize: each decision site
calls a registrar (an F# function — sites greppable, census is code); a suite
block diffs the registered census against a committed manifest, the way the
Surface block guards `surface.json` against `diagnostics.json`. Unregistered
new branch → red; manifest entry with no live site → red (the WARN-pin rule,
strict both directions). PARAMETRIC rows additionally name their θ-keys, so
the gate-sensitivity check (§5) knows which manifest edits may move which
emissions.

## 4. The route enumerator — selection as minimization over a bounded space

T2 as stated is branch-by-branch. The end-state architecture the deferred
representation makes possible is stronger: for each deferred computation,
**enumerate the route space, cost each route under M(θ), take the minimum,
break ties canonically.** Because Blade deliberately has no discovery
rewrites — fusion is written (`>>@`), not found — the route space per node
family is a small closed grammar, not a search: a deferred reduce's routes
are (eager materialize + fold | streamed deferred fold | compact/sym fold |
`gram`/BLAS route | CUDA route | …), each already existing as an emitter.
Selection by explicit argmin gives, by construction, exactly the three
properties the principles demand: determinism (pure function + canonical
tie-break), optimality-within-space (argmin), and a self-documenting census
(the decision points *are* the enumerator's comparisons; the manifest lists
route families instead of scattered branches).

This is a refactor of how decisions are organized, not new optimization
power — which is the point. **Pilot: the deferred-reduce family**, absorbing
plan-deferred-combinators.md's decline-removal work: each of the five
declines either becomes a route that loses the argmin (fine, and now visible)
or a route that wins somewhere (a decline that was costing us). Expansion to
other families (contraction routing, materialization points, loop-nest
orders) follows the same shape. Not phase-early: it presupposes the census
(you must know the branches before you can reorganize them) and the θ
manifest (the argmin's inputs).

## 5. T1 — the determinism and convergence gates

Current posture is close by construction: env gates are deliberately
per-call functions (`Build.fs:499` cluster), culture hazards closed by
InvariantGlobalization. Missing is the instrument. Three, in order of
strength — all **emit-only**: byte-compare `.cpp` (and `.ll` on the LLVM
lane); nothing needs to compile or run.

**5a. A/A gate.** Emit the corpus twice, hash, red on any disagreement with
the file named. Alternate fresh-state and reused-state runs (the
compile-speed work documented a parser-state race — both "state leaks in"
and "state was required" must be caught). Threats it watches for: dictionary
iteration order reaching emission, traversal-order-seeded gensyms, absolute
paths/timestamps in emitted text.

**5b. Equivalence-class convergence.** The new test family: two or more
*valid, equivalent* spellings of the same computation, pinned to identical
emission. This is the gate that gives the syntactic layer its job — prove
the equivalence, choose the faster path — and the corpus is where "these are
the same program" becomes a checked fact instead of folklore. Candidate
classes (each must be *proved* bit-identical before pinning, not assumed):

- `A + B` ≡ `method_for(zip(A, B)) <@> lambda` add ≡ implicit lifting
- `abs(A)` ≡ `method_for(A) <@> lambda(x) -> abs(x)` (the intrinsic-lifting
  eager path vs. the spelled map)
- `A [*] B` ≡ `method_for(A, B) <@>` multiply
- `A(i, j)` ≡ `A(i)(j)` (dimensional currying — likely already converges)
- `reduce(A, (+), axes = 2)` ≡ `reduce(reduce(A, (+)), (+))` (same
  innermost-first order both ways)
- the idiom-ladder rungs where semantically equal (implicit lifting vs.
  `method_for(range)` spellings of the same elementwise map)

Harness shape: a new corpus category with a `// SAME-EMIT: <group>` pin;
files sharing a group must emit identically after stripping the file-name
banner (the one legitimate difference). P0 seeds only pairs that *already*
converge; the sweep's divergence list is the normalization layer's worklist,
landed pair by pair — each new convergence is a small proved rewrite to a
normal form, never a heuristic. A divergence that resists normalization is
information too: either the spellings are not equivalent (document why) or a
route decision is leaking spelling-sensitivity (a T2 violation with a test
case attached).

**5c. Gate-sensitivity.** Flipping one declaration or θ-key changes emission
only at sites registered to it (§3) — a diff assertion once decision sites
carry markers.

## 6. P3 — the advisory channel (whole-program analysis, userland rewrites)

The channel exists three times over: BL4010 confirm-and-pin storage +
`--strict-pins` (a stripped build MODE, `Cli.fs:20-30`, own harness block);
BL4011 running the equivariance judgment *speculatively* and proposing the
pin — "propose, don't export", always a warning, deliberately no strict-pins
arm (`Diagnostics.fs:270-277`); BL4014, its galilean twin. This phase
generalizes the pattern from certificates to performance formulations — and
the analysis unit is the **whole typed program**, not one deferred DAG.
Deferral is what makes this tractable (every computation is a visible value
with a visible traversal), but the payoff is cross-computation: the advisor
sees that two *independent* deferred computations, possibly bound pages
apart, would be faster combined.

Why suggestions and not rewrites, given some combinations are bit-identical:
because §1's line holds — combining computations changes which program the
source is, structure is meaning in a structure-first language, and the
legibility contract (you can point at the loop object that became this nest)
survives only if the compiler compiles the program you wrote along its best
route rather than a program it invented. The measured accumulator ceiling
(gcc FMA-contracts at ≥8 accumulators under shipping `-ffp-contract=fast`,
plan-unroll-and-jam.md §1) shows even bit-identical fusions have θ-shaped
profitability — and with θ recorded, the *advice* can be quantitative: "on
this machine's manifest, fusing these 3 folds is proven; fusing all 9 would
contract."

**Hard-principle invariants:**

1. **Advisor-off identity.** Advisory analyses run read-only after route
   selection; a suite check pins emission byte-identical with the advisor on
   and off. A suggestion that influences emission is a censused violation.
2. **Suggestions are surface syntax.** Every advisory names the exact source
   rewrite (`reduce((L <@> k1) <&!> (L <@> k2), (+))`, `where comm(a, b)`,
   `axes = rank`, `>>@`). If the rewrite can't be written in the surface
   language, it isn't a suggestion — it's a missing language feature.
3. **Pinned like every diagnostic.** Warnings with BL codes, WARN-pin corpus
   coverage (strict both directions), a `--strict-suggestions` MODE for CI
   modeled on `--strict-pins` but separate — BL4011's comment already
   establishes strict-pins owns only the storage decision.

**Candidate analyses** (detection reuses shipped machinery):

- **Cross-computation fusion** (the `<&!>` advisory): k `reduce` nodes —
  same scope or not — whose sources are the same array, loop object, or
  provably co-iterable index space → one-traversal accumulator product,
  θ-bounded k. The flagship whole-program analysis.
- **Pipeline joining**: two deferred pipelines traversing the same source
  (`condition <@> r0`, `diagnose <@> r0`) → one composed pass; eager
  `|> compute` between composable `object_for` stages → `>>@`.
- **Shared-subcomputation hoisting**: a subcomputation appearing under two
  bindings → propose the named intermediate (CSE as a suggestion, since
  materialization is a semantic decision about storage).
- **Symmetry proposal**: the BL4013 contradiction walker in reverse — a
  kernel provably swap-invariant with no `where comm` gets the clause
  proposed, naming the r! saving. Pure BL4011 pattern.
- **Reorder-licence proposal**: the BL4016 judgment run speculatively — a
  provably commutative fold kernel with no `omp` gets the license proposed.
- **Fold-spelling advisory**: the expression-position decline at
  `CodeGenExpr.fs:985-992` already knows a licensed rank≥2 reduce without
  `axes = rank` bought no parallelism and which spelling would; today that
  knowledge is a comment in the emitted C++. Surface it as a spanned source
  diagnostic (the census marker stays — it serves T2).
- **Extent advisory**: power-of-two dense extents on contraction paths
  (measured ~7x) are statically visible in `Idx<N>`; bench-grounded warning.

**Surfacing.** CLI: warnings. IDE: the existing `ide surface` lane (own
harness block, committed `protocol/` snapshots) with a distinct suggestion
kind carrying the replacement text from invariant 2, so the front end renders
quick-fix-style hints. New codes continue the BL40xx band (4019+) — band
`'4'` maps to the constraints phase (`Diagnostics.fs:392`) and
BL4010/4011/4014 establish the suggestion channel lives there; each code pays
the usual five touch points including `diagnostics.json`, guarded by the
Surface block.

## 7. Phases

- **P0 — T1 gates.** The A/A gate (5a) and the SAME-EMIT harness (5b) with
  seed pairs that already converge; both emit-only, default suite. The
  convergence sweep's divergence list becomes the normalization worklist.
  Prerequisite for everything: census diffs and advisor-off identity
  presuppose reproducible emission.
- **P1 — census sweep + θ schema.** Exhaustive decision-point enumeration
  (start from `Build.fs:499` and §3's table), manifest with every row
  classified; the `toolchain.json` arch block (θ-keys: cache geometry, SIMD
  width, cores, BLAS/CUDA presence) probed by doctor/setup. Resolves the
  simplex auto-brick row: a stated (shape, θ) rule, or a declared default.
  If a branch is genuinely irreducible to proof/θ/license (none currently
  known), the scope statement narrows *explicitly* here, not silently in code.
- **P2 — census enforcement.** Registrar + suite block diffing the manifest,
  strict both directions; gate-sensitivity (5c) rides the same markers.
- **P3 — proof obligations.** A `docs/proofs.md` row per DOMINANT entry and
  per PARAMETRIC *rule*; mechanize the load-bearing ones in `proofs/` (rank
  dichotomy done; compact-storage dominance next).
- **P4 — advisory channel.** Advisor-off identity gate first; then
  cross-computation fusion and the symmetry proposal end to end (BL4019+,
  WARN pins, `--strict-suggestions`, ide-surface kind); remaining analyses
  one at a time, each with its own pins.
- **P5 — route enumerator pilot.** The deferred-reduce family reorganized as
  enumerate → cost under M(θ) → argmin → canonical tie-break, absorbing the
  five deferred-reduce declines. Success criterion: zero emission changes on
  the convergence corpus except ones the argmin can name and defend, and the
  census for that family shrinks to one registered enumerator.

## 8. What this buys

The claim becomes: *every Blade optimization decision is proved never-worse,
computed from a recorded description of your machine, or visible in your
source — and anything faster the compiler can see across your whole program,
it tells you in your own syntax.* Equivalent spellings compile to the same
code; the same program, declarations, and manifest compile to the same bytes
on any machine; and after P2 none of this is design intention — it is a suite
invariant, enforced the way the omp census and the WARN pins already are.
