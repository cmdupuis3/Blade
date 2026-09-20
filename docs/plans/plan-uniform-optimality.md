# The uniform optimality theorem — the orbit bound on kernel work and storage

**Status 2026-09-19: T1, T1′, T2, T3, T4 MECHANIZED** (`proofs/BladeOptimal.v`,
73 theorems; `proofs/BladeOrbitWork.v`, 42; `proofs/BladeDeduceExact.v`, 34;
`coqc` + `coqchk` on Rocq 9.0.1, no axioms, nothing admitted — §10 maps
statement to artifact and lists what the mechanization does NOT cover). **T4
found a precision gap in `src/Deduce.fs`, confirmed against the compiler and
fixed** (§11). The P0 census and the `blade plan` instrument are untouched. A
proof plan, not a feature plan: the deliverable is a theorem, and the compiler
work it justifies is one instrument in `blade plan`. Written from a read of the
tower's exactness, counting, layout and deduction files
(`docs/proofs.md`; `proofs/BladeCompleteness.v`, `BladeCounting.v`,
`BladeLayout.v`, `BladeDeduce.v`) and of `src/Deduce.fs`. **Two literature
passes ran 2026-09-19** (§7): neither kill condition fired; this doc's account
of the nearest system (SySTeC) was wrong in the first draft and over-corrected
in the second, and §7.1 now says only what the paper's text supports; five
items remain unverified and are listed as such (§7.3).

Companions: `plan-rank-r-former.md` and `structural/05-structured-operators.md`
sit exactly on the far side of the boundary §5.2 draws;
`plan-compact-sym-folds.md` is the fold corollary's customer; the unmerged
`plan-fastest-way-principles.md` (branch `feat/fastest-way-principles`) owns the
advisory channel §6 would report through.

---

## 0. Executive summary

"The fastest way is the only way" is a slogan with half a theorem under it. The
tower proves the licence group is **sound** (`output_symmetry_soundness`) and
**exact** (`license_exactness`: the largest grant valid for every H-kernel and
all data is H ∩ Stab), and that canonical layout **minimizes stride cost**
(`canonical_is_cost_minimal`). BladeLayout states its own limit in-file: cost 0
is necessary, not sufficient, for fastest. Nothing in the tower says that no
*other program* — one that is not a group-structured rewrite at all — could do
less work.

The missing statement:

> **Uniform optimality.** Any program that computes the output correctly for
> every kernel obeying the declared laws must evaluate the kernel at least once
> per free orbit of the licence group G = H ∩ Stab, and must hold at least that
> many result cells. Blade's canonical enumeration evaluates it exactly once per
> free orbit and stores exactly that many cells.

Four things make this worth a plan:

1. **It is a statement about programs, not grants.** Exactness quantifies over
   permutations a compiler might licence; this quantifies over everything a
   compiler might emit. It strictly strengthens the tower's headline.
2. **The proof is an adversary argument, not representation theory.** A kernel
   is an oracle; its values on distinct H-orbits of argument tuples are
   independently choosable inside the law class; an unqueried orbit is an
   unknowable cell. The single-nest case is short. That is a warning as much as
   a virtue — short proofs are the ones someone has already written (§7).
3. **The composed case is where the content is**, and it produces a formula the
   compiler can print: the **orbit work** of a pipeline, Σ over kernel nodes of
   the free-orbit count of that node's own group on that node's own support. The
   gap between that number and what Blade emits decomposes into three named,
   countable causes (§3.3).
4. **It draws the boundary of structural optimization exactly.** The bound is
   tight for the law vocabulary Blade has (`comm`, `anticomm`, licensed
   associativity). Every known way to beat it needs a law Blade does not have —
   distributivity — which is precisely what `gram`, `prodsum` and `gram_apply`
   name. The theorem says where declaring structure stops paying and where
   algebraic nodes must take over.

The honest grade, after the first sweep (§7): T1 is a folklore-simple bound that
nobody appears to have *stated* — the sweep found no citable form, so the proof
must be self-contained. T2's attainment is matched in practice by SySTeC for a
fixed operator table, without a proof or a bound. T3 (the pipeline formula) is
the strongest candidate: both nearest systems are single-statement, and its two
ingredients exist only separately (Lazy Code Motion for placement, FAQ variable
elimination for support). T4 turned out stronger than drafted and found a
compiler gap (§11); what it is *not* is a claim to see more symmetry than
enumeration would. What is not in doubt after §10–§11: none of it had been
*formalized* anywhere the sweeps reached, the same-array rule is not even
*stated* in the nearest system's formalism, and all of it now is.

---

## 1. What the tower has, and the hole

| Have | Artifact | Says |
|---|---|---|
| Soundness | `output_symmetry_soundness`, `output_antisymmetry_soundness` | reading through G is value-preserving (up to sign) |
| Exactness | `license_exactness`, `stab_violation_detected`, `kernel_invariance_necessary` | no permutation outside H ∩ Stab is uniformly sound |
| Counting | `storage_cardinality`, `shapeCard_binom`, `mixed_radix_bijection` | canonical cells = C(n+r−1, r), products across identity groups |
| Enumeration | `enum_sound/complete/NoDup`, `enumA_lex_sorted` | the loop visits each canonical tuple once, in storage order |
| Layout | `canonical_is_cost_minimal` | stride cost is minimized over the licensed space |
| Detection | `two_maximal_curryings_general` | what each currying can know |

The hole: all of these compare Blade's choice against **other licensed
rewrites of the same nest**. None compares it against an arbitrary program. A
reviewer's question — "could a cleverer scheme, not a symmetry at all, evaluate
the kernel fewer times?" — has no checked answer. The answer is no, and the
hypotheses under which it is no are the interesting part.

---

## 2. Setting

**One nest.** Positions 1..r. Arrays A_p : [n_p] → T. Positions p, q are in the
same *identity class* iff they hold the same array (identity, not equal values —
`shared_units_insufficient`). Kernel k : T^r → U. Output

    Out(ix) = k(A_1(ix_1), …, A_r(ix_r)),      ix ∈ X = ∏ [n_p].

**Law class.** H ≤ S_r with a character χ : H → {±1} (`comm` blocks have χ = +1,
`anticomm` blocks χ = sign; BladeLayout's `grant_forms_exhausted` says there is
no third form). K_H = { k : k∘s = χ(s)·k for all s ∈ H }. Stab = permutations
preserving identity classes. G = H ∩ Stab acts on X by permuting coordinates.

**Free orbits.** An orbit O ⊆ X is *free* iff the point stabilizer of one (any)
of its elements lies in ker χ. Non-free orbits are forced to zero whenever 2 is
cancellable in U (the antisymmetric diagonal). Write **ω(G, X)** for the number
of free orbits. For G = S_r on [n]^r: ω = C(n+r−1, r) unsigned, C(n, r) signed.

**Uniform program.** A deterministic algorithm with read access to the arrays
and **oracle access to k**, possibly adaptive, that must produce Out correctly
for *every* k ∈ K_H and *all* data. Cost = number of oracle calls. A program is
*data-oblivious* if its query sequence does not depend on the values read — every
compiled Blade nest is. *Generic data*: each array injective, distinct arrays
with disjoint value sets.

This is the compiler's actual epistemic situation (the phrase is
BladeCompleteness's): it knows H and array identity, and nothing else about k.

---

## 3. The statements

### 3.1 T1 — the orbit lower bound (one nest)

> **T1 (work).** On generic data, every uniform program makes at least ω(G, X)
> oracle calls. For a data-oblivious program the bound holds on every input.
>
> **T1′ (storage).** Any representation of Out as m cells, each holding one
> oracle result, read through a data-oblivious access map X → [m] × {±1}, has
> m ≥ ω(G, X). The same bound holds for arbitrary encodings when U is finite
> (counting) and for linear encodings when U is a field (dimension).

### 3.2 T2 — attainment

> **T2.** When G is a signed Young subgroup (a product of full symmetric groups
> on identity classes, each block `comm` or `anticomm`), Blade's canonical
> enumeration makes exactly ω(G, X) kernel calls and its compact storage has
> exactly ω(G, X) cells.

T2 is assembly, not new proof: `enum_sound/complete/NoDup` +
`storage_cardinality` per block, `mixed_radix_bijection` across blocks, the
strict (δ) corollaries of `dlj/dunlj_correct` for the antisymmetric count.

**Corollary (uniform optimality).** Under T2's hypothesis Blade's nest is
call-optimal and cell-optimal among all uniform programs — simultaneously, which
is not automatic: a scheme can be cell-optimal and need wide access (that is
BladeDichotomy's subject, and the reason T1′ restricts to one-cell access).

### 3.3 T3 — the orbit work formula (pipelines)

A pipeline's output cells are terms over kernel symbols k_1..k_m (each with its
own law class) and fold symbols. Normalize the pipeline to its *schema DAG*:
common subexpressions merged modulo the laws, each kernel node v carrying its
**support** X_v (the index axes its value actually depends on) and its group
G_v (the automorphisms of the schema below v that permute X_v).

> **T3.** Every uniform program for the pipeline makes at least
>
>     W(P) = Σ_v ω(G_v, X_v)
>
> kernel calls on generic data, and a plan that materializes every node as an
> array over its own support, iterated modulo its own group, attains W(P).

The attaining plan is **dimensional currying read as a cost statement**: an
intermediate is an array over exactly the indices it depends on. T3 then
decomposes Blade's distance from W(P) into three causes, each countable:

| Gap | Cause | Example |
|---|---|---|
| **Δ_group** | the deduced group is smaller than G_v | a symmetry `Deduce` cannot express (§3.4) |
| **Δ_support** | a node is evaluated over a consumer's larger support | `f(g(a), g(b))` in a pair kernel calls g ~n² times; g has support n |
| **Δ_cse** | two nodes equal modulo the laws are both computed | `k(a, b)` and `k(b, a)` under `comm` in one body |

Conjecture to check in P0, not a claim: co-iteration fusion is
support-preserving and therefore Δ_support-neutral, which would be a
call-count reading of why the fuse pass's `IsCoIteration` gate is load-bearing.

**Folds.** A fold symbol ⊕ is an oracle too. With a reassociation licence
(AC ⊕), a fold over a fiber of m distinct terms needs ≥ m − 1 calls, and
**rectangular** folds (pairwise disjoint fibers) cannot share work, so Blade's
m − 1 per cell is tight. Without the licence ⊕ is a free magma, the term is a
fixed tree, and m − 1 is forced trivially. Overlapping fibers are §5.3.

### 3.4 T4 — what `Deduce` is complete for

`Deduce` answers a parity per **adjacent** parameter pair, so the groups it can
produce are signed Young subgroups on contiguous blocks (`BladeDeduce.v`:
`adjacent_transpositions_generate`). The completeness statement to aim at:

> **T4.** For kernel bodies in the table fragment, the deduced group is the
> largest signed contiguous-block Young subgroup of the schema automorphism
> group Aut(k) — and Aut(k) is the uniform symmetry group of k in the free
> model.

The second clause is `license_exactness` lifted from fsum to schemas. The first
is the honest scope: three tiers of symmetry lie outside it — non-adjacent
transpositions (`k(a, b, c)` symmetric in a, c only), non-Young groups (cyclic,
dihedral, and the wreath product S_r ≀ S_2 that `BladeWreath.v` already proves
sound), and the Hermitian PConj element, which `Deduce` detects but no storage
class consumes. T4 turns "Deduce is conservative" into a measured quantity:
Δ_group = ω(Deduce_v) − ω(Aut_v).

**As proved (§11), T4 is sharper than this.** `mirrorEq` already walks modulo
commutativity, so the rule is not merely "the largest Young subgroup it
happens to find": per transposition it is EXACT — after one repair, which the
proof located. The three tiers above survive as two closed witnesses
(non-adjacent, non-Young); the Hermitian tier is outside the unsigned
fragment the mechanization covers.

Complexity note, so nobody over-worries: Aut of a labelled DAG is
graph-isomorphism-complete in general, and canonicalizing a tuple under an
arbitrary permutation group is the constructive orbit problem (GI-hard). Both
are in the size of the *kernel schema* (r ≤ ~6), never the data, and the Young
fragment's canonical form is a sort — which is what SymIdx is. Brute force over
S_r is the right P0 instrument.

---

## 4. Proof sketches

**T1.** Fix generic data and run the program against some k₀ ∈ K_H; let Q be
the set of argument tuples it queried.

*Separation.* For aligned tuples t(ix) = (A_p(ix_p))_p, genericity gives
h·t(ix) = t(iy) iff h preserves identity classes and iy = ix∘h — that is, iff
h ∈ G. So distinct G-orbits of X have argument tuples in distinct H-orbits of
T^r. A query need not be aligned (k(A_2(5), A_1(3)) is legal), but it lies in
exactly one H-orbit, so it serves at most one G-orbit of X.

*Adversary.* Suppose a free orbit O has no query in H·t(O). Define k₁ = k₀ off
H·t(O), and k₁(h·t) = k₀(h·t) + χ(h)·δ on it, δ ≠ 0. This is well defined
exactly because O is free (the stabilizer of t lies in ker χ), and k₁ ∈ K_H. By
induction on the adaptive run the program sees identical answers, so produces
identical output, but Out differs on O. Hence |Q| ≥ ω(G, X).

*Oblivious.* The query set is input-independent and must be right on generic
data, so the count holds on every input.

**T1′.** Opaque cells: each cell is some query's result, so m ≥ the number of
H-orbits that must be queried. Finite U: the achievable outputs number |U|^ω and
the encoding must be injective on them. Field U: the achievable outputs span a
space of dimension ω.

**T3.** Induction on the schema DAG. A U-sorted value can only be produced by a
call whose arguments are (modulo the node's laws) the node's argument classes;
those of kernel-result sort were themselves produced by calls. The adversary
perturbs one (node, free orbit) class at a time; CSE-normalization is what makes
distinct (v, orbit) pairs independent. Attainment is the materialize-on-support
plan, by T2 per node.

**Fold bound.** In the free commutative semigroup, a call's generator multiset
is the union of its arguments'. Reaching m generators from singletons takes
≥ m − 1 calls; an intermediate containing a generator of fiber F is useless to
any cell whose fiber is disjoint from F.

---

## 5. Where it is false — every hypothesis is load-bearing

These are the theorem's refutation suite, in the tower's house style: each
should become a named counterexample, and where possible a corpus test.

### 5.1 Data-dependent programs beat it on non-generic data
A program that inspects values can memoize (A(3) = A(7) ⇒ skip). Blade does not,
by design; the bound is worst-case for adaptive programs and everywhere for
oblivious ones. State both; do not blur them.

### 5.2 Interpreted kernels beat it — the distributivity frontier
With k = ring multiplication *and a fold after it*, symmetry-preserving bilinear
algorithms (Solomonik–Demmel; §7) compute symmetric contractions with fewer
multiplications than the orbit count, by trading multiplications for additions
through distributivity. Distributivity is not in Blade's law vocabulary, so this
is outside the uniform model — and that is the point: **the orbit bound is the
ceiling of what `where` clauses can buy; past it, the saving must be named by a
node** (`gram`, `prodsum`, `gram_apply`, the rank-r former). Note the break
needs the fold: for a bare outer product the n(n+1)/2 products a_i·a_j are
linearly independent quadratic forms, so the orbit count is optimal even
interpreted.

### 5.3 Overlapping folds are NP-hard to share optimally
Computing a family of AC-sums over overlapping fibers with the fewest binary
calls is Ensemble Computation (Garey & Johnson). No compiler theorem of this
shape survives there. Blade's position is already the right one: overlapping
sharing is *declared* (`let rec` for prefixes, `halo` for windows), never
searched for. T3 claims rectangular folds only.

### 5.4 Calls are not seconds
T1–T3 bound kernel evaluations and cells. They say nothing about cache,
vectorization, or the dependent-FMA chain `plan-rank-r-former.md` §0 identifies
as the real rank-3 bottleneck. With `canonical_is_cost_minimal` the tower would
hold two necessary conditions for fastest and still no sufficient one. Keep
BladeLayout's honesty line.

### 5.5 Non-Young groups: attainment is open, the bound is not
T1 holds for any G. T2 is proved only where Blade's storage is an orbit
transversal. For the wreath product, OrbIdx classes, and Hermitian pairs, Blade
may emit more than ω — that is Δ_group, a finding, not a flaw in the theorem.

### 5.6 `reynolds(g)` is tight too, at the dense count
With g an oracle under no laws, Out(ix) = Σ_σ ±g(x_σ) depends on every one of
the n^r values independently, so n^r calls are forced; canonical cells × orbit
sizes = n^r. Worth a line in the write-up because it looks like a
counterexample and is not.

---

## 6. What the compiler gains

One instrument, reported through channels that already exist. No emission
changes — this plan follows the advise-never-rewrite doctrine.

1. **Orbit census in `blade plan`.** Per binding: kernel evaluations emitted,
   ω for the deduced group, and (from P0's brute-force oracle, schema-sized)
   ω for Aut. `--json` carries the three numbers and the Δ decomposition. A
   binding at equality prints `optimal among uniform programs (T2)`.
2. **Δ_support as an advisory.** "g has support {i}; it is evaluated over
   {i, j}" with the hoist spelled — the whole-program advisory class
   `plan-fastest-way-principles.md` P3 reserves, same propose-don't-export
   channel as BL4010.
3. **A principled optimizer worklist.** Δ_group rows rank which non-Young
   symmetry is worth a storage class by measured frequency in the corpus and
   `examples/`, instead of by taste. If the census finds Δ_group ≈ 0 everywhere,
   that is a publishable negative and closes a question cheaply.
4. **The boundary as doctrine.** §5.2 gives `docs/formalism.md` a sentence it
   lacks: why structured-operator nodes exist at all, and why no `where` clause
   will ever subsume them.

---

## 7. Prior art — first sweep 2026-09-19

A sonnet agent ran ~28 searches; the four highest threats were then re-read
directly. Provenance is marked per line: **[read]** = the source text was read
for this doc, **[agent]** = search-snippet level only, **[open]** = still
unverified. Neither kill condition fired.

### 7.1 Read directly

- **Shi, Chou, Kjolstad, Amarasinghe, "An Attempt to Generate Code for Symmetric
  Tensor Computations", arXiv:2110.00186 (2021, arXiv only). [read]** Symmetry is
  a partition of a tensor's dimensions; output symmetry is the "greatest common
  symmetry" across the statement's tensors. §4.2 lists what that cannot see:
  value-dependent symmetry (commuting inputs), **the same tensor appearing
  twice** (C = X A Xᵀ, which it calls no longer detectable once written
  index-wise), antisymmetry and equal-up-to-transformation symmetry, block
  symmetry, and symmetry under a proper subgroup. No fix proposed, no optimality
  or bound claimed, single statements only. This is the cleanest external
  statement of why Stab has to be in the licence group. It does **not**
  enumerate permutations: its code generation prunes loop orderings that are
  non-canonical relative to the gcs — a different sub-problem from SySTeC's
  generate-normalize-merge, so "more naive across the board" would be wrong.
- **Patel, Ahrens, Amarasinghe, "SySTeC: A Symmetric Sparse Tensor Compiler",
  CGO 2025 (1–5 March 2025), arXiv:2406.09266 (v1 2024-06-13). [read — twice
  revised; every read is through a summarizing fetch, so the author should open
  §4.1, §4.2.2 and §5.2.4 directly before citing]** Second dedicated pass
  2026-09-19 (sonnet agent, then four pointed re-reads of mine). What the text
  supports:
  - *Symmetrization enumerates.* §4.1 / Fig. 3: for each **equivalence group** E
    of the permutable indices P (which indices are forced equal — the diagonals),
    build the group S_{P|E} and apply **every** σ in it to the assignment, then
    normalize (sort commutative operands, sort tensors, sort each symmetric
    tensor's own indices) so equal right-hand sides become syntactically equal
    and merge. For the generic group that is all of S_r: §3.1 says the compiler
    must perform n! assignments per iteration. So "r! rewrites" is right **per
    equivalence group**, with several groups per statement (four in the paper's
    3-D MTTKRP walk-through), not once per statement.
  - *P is built from declared INPUT symmetry only.* The defining formula is the
    union, over input tensors, of the blocks of each tensor's declared partition
    Πᵢ, and the paper calls it an over-approximation. It mentions neither output
    indices nor repeated tensors.
  - *Same tensor twice is exploited, but the rule is not stated.* §3.2.1
    motivates "visible output symmetry" with SSYRK, C[i,j] += A[i,k]·A[j,k]; §5.2.4
    evaluates it with A explicitly **not** symmetric, says C is symmetric "by
    nature of the computation", and reports half the computations and writes
    (2.20× over naive Finch) via pass §4.2.2, which merges equal assignments to
    mirrored output coordinates. By §4.1's own formula P is empty for that
    kernel, and **no sentence says how {i, j} becomes permutable** — detected by
    comparing normalized right-hand sides, or supplied. The mechanism works in
    the evaluation; the general rule is absent from the formalism. *This
    corrects this doc twice over:* the first draft said SySTeC has no
    same-tensor mechanism (wrong — it exploits one); the second said it
    "compares accesses by tensor name … that is H ∩ Stab in practice" (ahead of
    the text — the paper never states that rule).
  - *Laws.* Commutativity throughout; a small built-in operator set described as
    extensible (min-plus Bellman–Ford shown), with no per-kernel declaration
    syntax found. "Distributive Assignment Grouping" (§4.2.7) only turns N
    identical additions into one addition times N — no factoring across distinct
    terms, so nothing beyond "each unique entry once".
  - *Classes.* Full and partition (Young) symmetry only; §7 lists antisymmetry,
    block and cyclic symmetry as future work.
  - *Scope and claims.* One Finch assignment in, one kernel out; no theorem,
    lemma or proof anywhere; strongest claim "up to a factor of n!" and measured
    1/6, 1/24, 1/120 of the symmetric factor's values for 3-/4-/5-D MTTKRP.
    Highest order evaluated is 5; **no compile time, code size or case count is
    reported, and factorial growth is not acknowledged or listed as a
    limitation.**
  - *Vocabulary.* None of "adjacent transposition", "generator", "curry",
    "Young subgroup", "stabilizer", "orbit", "Burnside", "automorphism" appears in
    SySTeC or in Shi et al.; "canonical" is used only as a computational device.
  - *Follow-ups (snippet level).* Patel's MIT MEng thesis (2024) is the same
    project; Finch (OOPSLA 2025) is the backend; Galley (arXiv:2408.14706, read)
    never mentions symmetry. Nothing found in 2024–2026 states a lower bound on
    kernel evaluations under symmetry.
- **Solomonik, Demmel, Hoefler, "Communication Lower Bounds of Bilinear
  Algorithms for Symmetric Tensor Contractions", arXiv:1707.04618, SISC 2021.
  [read: abstract]** The whole framework is bilinear algorithms; the bounds say
  the symmetry savings in arithmetic cost are paid for in data movement. No
  uninterpreted-kernel statement. Two consequences: it does not touch T1, and it
  is a *second* reason §5.4 matters — past the orbit bound, fewer multiplications
  provably cost more communication.
- **Järvisalo, Kaski, Koivisto, Korhonen, "Finding Efficient Circuits for
  Ensemble Computation", SAT 2012. [read]** Confirms §5.3: SUM-Ensemble
  Computation (build every set of a family from singletons with ≤ b *disjoint*
  unions, intermediates shared) is NP-complete, cited to Garey & Johnson 1979;
  it stays NP-complete with the disjointness requirement dropped (the idempotent
  OR variant). The disjoint form is exactly the non-idempotent AC fold. The
  in-book problem code is **[open]** — cite this paper plus the book, no code.

### 7.2 What this does to the four statements

- **T1** — nothing found that states it: query-complexity work on symmetric
  functions concerns joint permutation of all input bits, a different structure;
  no parametricity-based call-count bound surfaced. Self-contained proof
  required; keep the folklore caveat.
- **T2** — SySTeC reaches the same attainment for the unsigned Young case over
  ring-like operators, for DECLARED input symmetry, and — in its evaluation,
  by a rule its formalism does not state (§7.1) — for one same-tensor kernel.
  It is not *prior* to Blade: kernel commutativity groups
  driving symmetric iteration in `method_for` are in this repository's first
  commit (`e7f7242`, 2019-04-10 — `commutativity_groups` on the closure type, a
  commutativity vector on `method_for`, "the number of symmetric dimensions for
  a set of commutative arguments"), ahead of Shi et al. (2021) and SySTeC
  (2024). Independent discovery, earlier record; cite both and say so. Blade's
  residue beyond priority is the signed case, declared laws on arbitrary
  kernels, and — the point of this plan — the matching lower bound, which
  nobody had formalized.
- **The simplification SySTeC does not make.** Three layers, kept apart:
  - *Verifiable.* SySTeC applies every permutation of each equivalence group and
    normalizes each (§7.1). Blade curries: an array is `I → J → T`, a transpose
    is a reordering of applications, and deduction reduces to one parity per
    ADJACENT parameter pair — r − 1 checks. Neither paper in the lineage uses
    generators, adjacent transpositions or currying at all.
  - *Now proved* (`BladeDeduceExact.v`, §11). Those r − 1 checks are not a
    heuristic: per transposition the rule is EXACT, and the group they generate
    is the largest contiguous-block Young subgroup of the body's symmetry group.
    So for the Young shape — the only shape SySTeC exploits — r − 1 checks
    provably find everything r! rewrites would.
  - *Author's assessment, not supported by the text.* That SySTeC "got stuck"
    for want of this move. The paper reports no compile cost, tests nothing past
    order 5 (120 rewrites), and lists no scaling limitation; it neither claims
    nor concedes a problem. Two honest counterweights: part of its r! is not
    detection at all but the replication a DENSE output needs (compact output
    storage, as in Blade, makes that vanish); and this plan's own §3.4 endorses
    brute force over S_r as the instrument for the non-Young tier — the same
    tactic, for the tier adjacent checks cannot reach.
  - Dimensional currying was adopted to tame deduction under arbitrary
    transposes; it then turned out to be the right abstraction for cache order
    (BladeLayout) and — T3 — for *work*: the attaining plan materializes every
    intermediate over exactly its own support, which is what a curried array is.
- **T3** — not found. Nearest relatives, both **[agent]**: Knoop, Rüthing,
  Steffen, "Lazy Code Motion" (PLDI 1992) / "Optimal Code Motion" (TOPLAS 1994),
  whose computational optimality is per-path evaluation counts of uninterpreted
  terms in a CFG (the Δ_support idea, with no index spaces or groups); and
  Abo Khamis, Ngo, Rudra, "FAQ" (PODS 2016), where each factor is computed over
  exactly its support, for interpreted semirings. No source combines support
  placement with orbit counting. Both must be read before P2 claims anything.
- **T4** — restored, on different grounds. The second pass shows the lineage
  has NOT written down the same-array rule: Shi et al. name it undetectable,
  SySTeC exploits one instance without stating how. Blade has the rule
  (H ∩ Stab), its exactness (`license_exactness`), and now the exactness of the
  deduction that feeds it (§11). What T4 is *not*: a claim that Blade sees more
  symmetry than brute-force enumeration would — it provably sees less (the two
  witnesses in §11), by design.
  **[open]**: Avenhaus & Plaisted, "General Algorithms for Permutations in
  Equational Inference", JAR 26(3), 2001 — abstract read only (permutation groups
  arising among terms in equational inference, general group algorithms applied
  to rewriting); paywalled, and the closest topical match to
  `uniform_equality_exact`. Ground equality modulo permutative equations is
  classical; whether the *symmetry-group* reading is stated there is unknown.

### 7.3 Still open, by section

- §5.2's claim that a bare outer product is orbit-optimal even interpreted is an
  inference (linear independence of the a_i·a_j) — no bilinear-complexity
  citation yet. **[open]**
- Solomonik & Demmel, "Contracting symmetric tensors using fewer
  multiplications" (ETH TR 2015) and "Fast Bilinear Algorithms for Symmetric
  Tensor Contractions" (CMAM 21(1), 2021): titles and venues **[agent]**, the
  exact reduction factors and whether every saving needs a contraction **[open]**.
- TCE operation minimization is NP-hard; the attribution to Lam, Sadayappan,
  Wenger (1997) is **[open]**.
- Donaldson & Miller (FM 2005) and Clarke–Emerson–Jha–Sistla (CAV 1998) for the
  §3.4 complexity note: **[agent]**, consistent with the July assessment.
- Motivating instances for the write-up, all **[agent]**: the 8-fold symmetry of
  two-electron integrals, BCSS (Schatz et al., SISC 2014), Ravanbakhsh et al.
  "Equivariance Through Parameter-Sharing" (ICML 2017) — orbit counting
  rediscovered three times, each time as practice, never as a bound.

**Kill conditions (unchanged, neither fired).** (a) T3's formula appears in the
tensor-compiler or polyhedral literature as an optimality statement → downgrade
to "mechanize and cite", keep §6. (b) The P0 census finds Blade above ω on
*Young* licences → that is a compiler bug census, and it outranks the theorem.

---

## 8. Phases and gates

**P0 — census and sweep (days; can kill or reshape everything).**
- Schema-automorphism brute force (S_r, r ≤ 6) over every kernel in
  `tests/corpus/` and `examples/`; tabulate Deduce group vs Aut.
- Emitted kernel-evaluation and cell counts vs ω for each licence form the
  compiler can produce: Sym, Antisym, mixed blocks, distinct identity groups,
  compound (lat, lon) joint symmetry, wreath, OrbIdx, Hermitian, `reynolds`.
- Close §7.3's open items; read Lazy Code Motion, FAQ, and Avenhaus & Plaisted
  in full (the first sweep reached them at snippet/abstract level only).
- *Gate:* a table with no unexplained row. Any Young-licence row above ω is
  filed as a bug before P1 starts.

**P1 — T1, T1′, T2 on paper, then `proofs/BladeOptimal.v`. DONE 2026-09-19**
(ahead of P0's census, which therefore still owes its table; see §10 for the
scope actually reached).
- Programs as query lists (oblivious) first; adaptive decision trees second.
- Reuse BladeCompleteness's `perm_pair` and its indicator-data technique — the
  adversary's δ-perturbation is `stab_violation_detected` generalized from one
  violating permutation to one unqueried orbit.
- Signed case and the free-orbit definition; the antisymmetric diagonal as the
  worked corollary (C(n, r), "no stored diagonal" is forced, not chosen).
- *Gate:* `coqc` + `coqchk`, no axioms, `count-theorems.ps1 -Check` updated;
  §5.1 and §5.6 as named refutation/consistency theorems.

**P2 — T3. DONE 2026-09-19 as `proofs/BladeOrbitWork.v`**, over a term model
rather than BladeCompute's shape+kernel plans (a plan has one kernel; T3 needs
several oracles feeding each other). The third gate item — three corpus
programs matched by hand — waits on P3.
- Extend the shape+kernel plan with `pipe` and node supports; define W(P).
- Lower bound by DAG induction; attainment by T2 per node.
- Rectangular-fold bound; §5.3 stated as scope, with a two-cell overlapping
  witness where sharing beats per-cell folding.
- *Gate:* as P1, plus three corpus programs whose `blade plan` numbers (P3)
  match the Coq-side W(P) by hand.

**P3 — the `blade plan` orbit census.** Lands only after P0's table exists
(it is the same computation, productized). Emission byte-identity with the
census off is the regression gate.

**P4 — T4 and the write-up. T4 DONE 2026-09-19** (`proofs/BladeDeduceExact.v`,
§11) — without the measured-frequency half, which needs P0's census; the
write-up is open. Completeness of the table fragment against Aut;
the three-tier gap list with measured frequencies; `docs/proofs.md` section,
`docs/formalism.md` boundary sentence, and the paper section.

P1 and P0 are independent after the sweep; P2 needs P1; P3 needs P0 only.

---

## 9. Decisions held for the user

1. ~~Is a likely-folklore T1 worth mechanizing?~~ **Settled: done.** One
   correction to the recommendation's reasoning — P2 did *not* need T1 as a
   lemma. The one-nest adversary perturbs a single kernel; in a pipeline that
   changes every downstream argument, so T3 has its own proof (a free model
   plus a taint argument). The two files share only the oracle-program model.
2. **Cost model.** Kernel calls and cells only (recommended), or also ⊕-calls
   for folds as a first-class count? The second makes §5.3 unavoidable in the
   statement rather than a scope note.
3. **Where the census reports.** `blade plan` only (recommended), or also a
   BL-coded advisory for Δ_support? The latter should wait for the
   fastest-way-principles branch's advisory channel rather than mint its own.
4. **Naming.** "Uniform optimality" / "orbit work" / "free orbit" are this
   doc's coinages; nothing in the tower uses them yet.

---

## 10. What is proved (2026-09-19)

`coqc` and `coqchk` clean on Rocq 9.0.1; `Print Assumptions` reports every
headline closed under the global context; `count-theorems.ps1 -Check` passes at
913. Not re-run on Coq 8.18 (not installed here) — the files use only names
present in both.

| Statement | Artifact | Reached |
|---|---|---|
| Oracle model | `prog`, `run`, `trace`, `run_agree` | adaptive deterministic programs; answers of any type |
| T1 abstract | `orbit_lower_bound` | any law class, via *perturbable* classes — no group theory in the core |
| T1′ | `orbit_storage_bound` | opaque-cell stores (a store is the program that asks every cell) |
| T1 + T2, H = S_r, one array | `sym_nest_lower_bound`, `sym_nest_storage_bound`, `sym_nest_attained`, `uniform_optimality_sym` | any r, any n; C(n+r−1, r); attaining program serves the DENSE index space |
| Law class = the tower's | `Kcls_Ksym`, `Ksym_Kcls` | `invariant_under` every `perm_pair` ⟺ constant on sorted tabulations |
| T1 + T2, G = H ∩ Stab | `young_nest_lower_bound`, `young_nest_attained`, `uniform_optimality_young(_nat)`, `shargs_is_Out` | H = S_R over several arrays, any shape; ∏ C(nⱼ+rⱼ−1, rⱼ); genericity discharged by Cantor pairing |
| T1 + T2 signed | `antisym_lower_bound`, `antisym_attained`, `antisym_diagonal_forced_zero` | r = 2; C(n, 2); diagonal forced to zero and free of cost |
| §5.1 refutation | `nongeneric_data_beats_bound` | constant data: one query |
| T3 lower bound | `orbit_work_lower_bound` | one question per subterm class modulo the laws |
| T3 attainment | `memo_prog_correct`, `memo_prog_cost`, `orbit_work_attained`, `uniform_optimality_pipeline(…_nat)` | memoized evaluator; ≤ one per class always, = in the free model |
| Orbit work formula | `two_node_orbit_work`, `two_node_fused_work`, `two_node_optimal`, `two_node_computed` | W = n + C(n+1, 2) vs 3n² fused, for g(h(A i), h(A j)) |

| T4 exactness | `uniform_equality_exact`, `uniform_symmetry_is_automorphism`, `mirror_spec`, `parfix_exact` | unsigned fragment; per transposition, any (not only adjacent) |
| T4 granted group | `deduced_group_sound`, `deduced_is_maximal`, `deduced_group_largest`, `young_generated` | largest contiguous-block Young subgroup |
| T4 gaps | `shipped_rule_incomplete` (fixed), `nonadjacent_symmetry_missed`, `wreath_symmetry_missed` | closed witnesses |

(Tower total after T4: 947; the 913 above was the count after T3.)

**Not reached, and why it matters.**

- **A proper subgroup H < S_R.** The instances take the kernel fully symmetric
  and let the *binding* cut G down — the same move `license_exactness` makes
  with `fsum`. A kernel commutative in only some positions has a bigger law
  class; the abstract theorem covers it, but no instance supplies its class
  function. For block-Young H this is a blockwise sort and should be short.
- **Signed at general r.** Needs the sign of a position permutation; r = 2
  avoids it with `swap`.
- **Non-opaque storage** (finite-U counting, linear encodings): prose only.
- **T3 beyond arity 2 and commutativity; folds.** §5.3 says why folds stay out.
  Arity r inside a pipeline wants a sortable canonical query — mechanical.
- **The schema-level formula.** `orbit_work_lower_bound` counts classes of a
  *term family*; that the count equals Σᵥ ω(G_v, X_v) is proved for one schema,
  not for schemas in general.
- **§5.6 (`reynolds` is dense-tight)** and the **data-oblivious corollary**
  ("holds on every input") are still prose.

One thing the mechanization taught that the plan had wrong: §4's T3 sketch
("the adversary perturbs one (node, free orbit) class at a time") is not a
proof. The perturbation must be at a *minimal* unasked subterm (`min_bad`), or
the subterm's own children may sit in the perturbed class; and it needs an
invariant that survives upward (`taint` / `clean`), because a changed argument
could in principle collide back onto the old value. Both are forced by the
free model's injectivity — and both are absent from the one-nest case.

---

## 11. T4 as proved, and what it found (2026-09-19)

**The theorem.** In BladeOrbitWork's term model — a kernel body is a term over
its parameters, operator symbols uninterpreted up to a declared commutativity:

1. `uniform_equality_exact` — two bodies agree under every interpretation
   respecting the laws, on all data, **iff** they are equal modulo the laws. The
   hard direction is one line once T3's free model exists: a perfect hash of
   canonical queries is *faithful*, so equal values mean equal terms.
2. `uniform_symmetry_is_automorphism` — hence the uniform symmetry group of a
   body is its syntactic automorphism group modulo the laws, decidable by a
   structural walk.
3. `mirror_spec` — `src/Deduce.fs`'s `mirrorEq`, modelled node for node, is that
   walk composed with the swap. This is the fact §3.4's draft missed: the mirror
   rule was never merely syntactic.
4. `parfix_exact` — the rule answers PInv on a transposition exactly when the
   transposition is a uniform symmetry.
5. `deduced_group_sound` / `deduced_group_largest` / `young_generated` — the
   granted group is generated by the certified adjacent transpositions, is sound,
   and contains every adjacent-generated group inside the symmetry group: the
   largest contiguous-block Young subgroup.

**The finding.** Modelling the rule *in the order it shipped* (`par`: mirror
first, a mirror hit answering from the op's swap class alone) and trying to
prove it exact failed at one case, and the failed case was a real one:

    lambda(x, y) -> (x + y) / (y + x)

The operands mirror each other; `/` has no swap class; the rule answered
PBottom without looking at the operands, each of which is invariant on its own.
Against the shipped binary, before the fix:

| Program | Before | After |
|---|---|---|
| `(x + y) / (y + x)`, unpinned | no diagnostic | BL4010 (deduces commutative) |
| same, `where comm(x, y)` | accepted | accepted |
| same, `where anticomm(x, y)` — a FALSE declaration | **accepted** | **BL4013** |
| `(x * y) / (x + y)` (no mirror hit) | BL4010 | BL4010 |

The third row is why this is more than precision: the silent rule could not
refute a wrong pin whose storage would negate half the output. The fix is one
condition in `parityOf` (`mirrorEq … && opSwapClass op <> PBottom`, else fall
through to the chain rule); `tests/corpus/symmetry/049`–`051` pin the deduction,
the pinned twin, and the refusal; `blade test symmetry` 51/51.

**What stays out of reach, by design** — both closed witnesses:

- `nonadjacent_symmetry_missed`: g(h(a, c), b) is symmetric under (0 2) and under
  neither adjacent transposition. Reachable only by reordering parameters.
- `wreath_symmetry_missed`: g(h(a, b), h(c, d)) has the order-8 wreath product;
  the deduction grants S₂ × S₂. This is the Δ_group of §3.3 with a number on it.

**Not covered.** The signed rules (PNeg chain rule, PConj), call summaries,
`reduce`, `if`/`match`, let-flattening — sound by BladeDeduce, completeness
unknown. The same mirror-first shape recurs nowhere else in `parityOf`, but
`signParityOf` and `conjMirrorEq` were not audited for analogous early exits.
