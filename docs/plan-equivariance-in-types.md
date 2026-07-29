# Plan: equivariance in types — the typecheck-resident deduction

Status: PHASES A+B IN PROGRESS (2026-07-28); pause before C per review.
Sequel to plan-transforms-as-types.md (stages 1-6 landed) and
plan-equivariance-deduction.md (landed 2026-07-28). Goal: all equivariance
disciplines live under one roof, at typecheck time, deduced the same way
symmetry is — with the exact index types in hand.

REVIEW DECISIONS (2026-07-28):
1. Claim-as-deduced-attribute CONFIRMED, with a refinement that sharpens
   §0 rather than bending it: a pin IS type-relevant — as a POLYMORPHISM
   LICENSE. `comm` on a kernel licenses the compact retyping of one and
   the same function (`... -> Idx<M> -> Idx<N> -> T` vs
   `... -> SymIdx<2,M> -> SymIdx<2,N> -> T` are two signatures of a single
   commutative function; the pin permits the dimension transposition).
   Post-A3, an `ml.equiv(O3)` claim licenses viewing the function at
   restricted subgroup types the same way. So: claims live in SIGNATURES
   as licenses for type transformations — they are part of the type in
   the broad sense — but they are not solved per-expression by the
   unifier. The lattice deduces them; the pin exercises the license.
2. `IrrepsIdx` stays GROUP-LESS (A2 confirmed). An API-shape discussion
   is owed once A+B land — see §6 (draft surface for that conversation).
3. Phase C approved in principle; execution PAUSES after A and B for
   review. B alone delivers both §0 payoffs.

## 0. The thesis: split the rep DATA from the rep CLAIM

"Equivariance in types" can mean two things, and the symmetry precedent
decides between them:

- **The DATA** — which group acts on this axis, via which representation —
  belongs IN the type, unified and propagated by the type checker. This is
  MOSTLY ALREADY TRUE: `IrrepsIdx<spec>`/`PgIrrepsIdx<g,spec>` ride
  `IRIndexTypeG.Tag` through unification today (spec identity at
  Unify.fs's BlockSpecTag arm, gradual adoption against plain
  `Idx<total_dim>`). What unification propagates, every call site knows.
- **The CLAIM** — "this function is equivariant" — is a theorem about a
  body, NOT a fact about a layout. It belongs where comm/antisymm live: a
  deduction lattice over TypedAst that proposes, a `where` clause that
  pins, per-primitive transfer tables, interprocedural summaries keyed by
  binder IRId. NOT in the unifier.

This is exactly how symmetry is factored: extents and compact storage are
types; the comm/antisymm parity is a deduced-then-pinned attribute. Pushing
the claim into every expression's type (full `equiv(G, ρ)` refinements in
the unifier, the literal equivariant-nn.md §1-§4 surface) would entangle
subsumption and variance with the solver and bloat every diagnostic; the
lattice-plus-pins pattern has now shipped three times (rank, symmetry,
arity) and is the recommended vehicle. The per-expression rep status the
old design wanted is the ANALYSIS state of the walker — same as parity —
not a type.

What the move buys, concretely (both are named deferrals today):

1. **Partial annotation dies as a limitation.** Stage 6a requires fully
   annotated signatures because it runs at elaboration, before unification
   and the rank deduction exist. At typecheck the types are CLOSED — an
   unannotated param that a call site instantiates at
   `Array<F like IrrepsIdx<S>>` is classifiable.
2. **Perm inference becomes feasible.** `perm_equiv`'s flat-extent keying
   is ambiguous at a surface signature; at typecheck, monomorphized
   extents make N concrete. The strongest argument for the user's thesis:
   the exact type literally unblocks the deferral.

## 1. Why the current machinery cannot just be moved

The equivariance walkers run at the MLElaborate pass-1/pass-2 seam because
`ml.*` ops are still surface-visible there. After pass 2, `derive_linear`
calls are REWRITTEN into generated loop nests over baked CG tables — at
typecheck the ml vocabulary is gone, and judging a synthesized CG
contraction equivariant from first principles is the polynomial engine's
job, not a transfer table's. The resolution is not to move the seam but to
make the elaborator STAMP what it knows (§3, A2): synthesized functions
are equivariant BY CONSTRUCTION (Schur bases; the proof tower), so the
elaborator pins them, and the typecheck-time walker consumes those pins as
axioms exactly the way a certified callee is consumed today.

## 2. The target architecture

A DISCIPLINE is data: a status lattice, a typed signature classifier, a
per-primitive transfer table, a claim vocabulary (`ml.equiv(G)` /
`ml.galilean(u,..)` / `ml.perm_equiv(..)`), and a mode (always-on /
signature-seeded / clause-seeded). One generic engine in Deduce.fs runs
them all:

- **Classifier**: IRType -> status, reading `IrrepsTag`/`PgIrrepsTag` off
  `ArrayElem.IndexTypes` (the accessors exist in Types.fs). The typed twin
  of `statusOfType`, shared verbatim between checking and deduction so
  Propose ⊆ Check-accept survives the move.
- **Walker**: bottom-up over TypedExpr, `flattenBindings` first (the
  binding-descent problem is ALREADY SOLVED at this level — the seam
  walkers each re-solved it with env threading), match/if arms-agree
  rules, PBottom discipline ("no claim", never unsound).
- **Summaries**: `FuncRepStatus : Dictionary<IRId, ...>` beside
  FuncSignParities — deduced facts flow to callers without pins, AS
  ANALYSIS; only source-written pins license checking (§4b of the
  transforms plan, unchanged).
- **Early/late split**: non-polymorphic deduction early (decl close),
  polymorphic late at monomorphized call sites — the comm precedent,
  reused wholesale.
- **Surfacing**: the existing channels (CertSuggestions/BL4011,
  GalCertSuggestions/BL4014, CertFacts -> deduced[]) move their producers;
  consumers unchanged.

## 3. Stages

**A. Data model (each independently useful now)**
- A1. Elaborator stamping: `derive_*`/`tp`/`poly` emitters attach the
  `where ml.equiv(G)` conjunct to the FunctionDecls they synthesize
  (provable by construction; they already stamp IrrepsIdx signatures).
  Verify the sgs elaborator's synthesized stencils can carry galilean
  stamps the same way. Immediate payoff before any other stage: stage 6a's
  recall improves (certified-callee arm fires on generated helpers).
- A2. Group at the claim level, not the type: keep `IrrepsIdx` group-less
  and keep the O3-then-SO3 candidate ladder. Adding G to the index type
  buys nothing the ladder doesn't (the spec's parity data already
  distinguishes what O3 needs) and forks the tag format. Revisit only if
  D1's meets demand it.
- A3. `ml.restrict` + branching rules (O(3) irreps -> point-group irreps;
  character theory, finite tables like WignerTables). Prerequisite for
  cross-group meets (D1) and independently a shipped §3.6 promise.

**B. The typed lattice (the core move)**
- B1. Typed classifier + core transfer table: literals/Inv arithmetic,
  Rep s ± Rep s, scalar·Rep (InvScalar only), Rep·Rep -> Bottom, indexing
  a Rep -> Bottom (static invariant-offset reads later), reduce, block/
  let/match via flattenBindings. Every rule carries a soundness comment;
  the table is the Coq obligation (B4).
- B2. Interprocedural: pinned callee -> declared signature (trust, as
  today); stamped callee (A1) -> same; unpinned callee -> its DEDUCED
  summary at suggestion strength only. Late tier for polymorphic helpers.
- B3. Deduction parity gate: run B1/B2 proposals DIFFERENTIALLY against
  stage 6a over the whole corpus — same programs, proposals compared.
  Ship when typed recall ⊇ seam recall with zero false proposals (every
  proposal's pinned twin must check under the SEAM checker, which remains
  the checking authority through all of B).
- B4. Proofs: transfer-table lemmas in Rocq (BladeDeduce.v style — the
  composition rules are the same shape as the parity exchange lemmas).

**C. Checking migration (only after B3 holds)**
- C1. LANDED (2026-07-28). Declared-cert validation at typecheck via the
  same walker (composition fragment). Elaboration checking stays on;
  disagreement is a compiler bug (the LieGuardFailure posture, BL9004,
  build-stopping). Outcomes are CONFIRM / ABSTAIN / DISAGREE with
  abstention the default at every boundary the B3 differential showed
  the walkers legitimately diverge; DISAGREE is deliberately narrow
  (definite TRep vs TRep with different specs). Census on landing:
  130 certified decls, 54 confirm / 76 abstain / 0 disagree
  (`blade test rep-check`). Self-reference is assumed, not refused —
  validation is an assume-guarantee obligation, inverting deduction's
  rule.
- C2. LANDED (2026-07-28). MLPolyExtractTyped ports EXTRACTION to
  TypedExpr (discharge confirmed IR-agnostic and reused, zero math
  reimplemented); stitched into DeduceRep's EngineDischarge hook at both
  the checking site (abstain -> confirm/disagree) and the deduction site
  (composition-declined attempts, strongest-first preserved). The two
  `TYPED-EXEMPT: engine` exemptions are GONE — the differential is
  16/16 matched, 0 exempt. STEADY STATE NOTE: generated `derive_*`
  bodies (63 of the 76 abstentions) do NOT discharge and are not meant
  to — they are method_for CG loop nests, not polynomial normal form,
  and their warrant is the elaborator stamp (axioms by construction,
  emitter verification + proof tower), not re-derivation. Closing them
  would be structural CG recognition, a different feature; abstention
  there is benign and permanent until someone wants that feature.
- C3. Retire the seam walkers; MLElaborate keeps synthesis + stamping
  only. Galilean and perm land as discipline instances on the generic
  engine (galilean's table is small; perm gains inference per §0.2).
  NOT STARTED — the checking-authority flip lives here, and with it the
  diagnostic-parity question (every rejects pin is seam-worded today).

**D. Meets and modes (the previous conversation, now cheap)**
- D1. Collision = subgroup meet via A3's branching rules: O3 ⊓ SO3 = SO3
  (shipped as the ladder), SO3 ⊓ C4 = C4 via restriction, C4 ⊓ C3 =
  trivial -> non-vacuity silence.
- D2. Modes as data: rank/symmetry always-on; equivariance
  signature-seeded (a Rep-classifiable type anywhere in the signature) or
  clause-seeded (pins extend tracking to the call neighborhood). The mode
  field replaces today's ad-hoc gates (import-ml, candidatesFor,
  galilean's gateless sweep).

## 4. Invariants that hold through every stage

- Equivariance claims never change codegen or storage (unlike symmetry
  pins) — no strict-pins arm, ever; BL4011/BL4014 stay warnings.
- The seam checker is the checking authority until C3; every stage keeps
  the full corpus + the 92-file SUGGEST block green.
- Propose ⊆ Check-accept, enforced by the shared classifier and the
  differential gate.
- Deduction proposes; only source-written pins export (separate
  compilation unchanged).

## 5. Open decisions (for review before scheduling)

1. §0's thesis itself: claim-as-deduced-attribute (recommended) vs claim
   in the unifier. The full-refinement road stays open later — the
   lattice is the analysis it would need anyway.
2. A2: group-less IrrepsIdx confirmed, or `IrrepsIdx<G, spec>` now.
3. Whether C (checking migration) is wanted at all, or whether
   B-forever (deduction typed, checking at the seam) is a stable end
   state. B alone delivers both §0 payoffs.

## 6. API sketch — draft surface for discussion

**DRAFT FOR DISCUSSION. Not a decision, not a spec.** Review decision 2
owes the user this conversation ("how the API is going to look once this
plan is implemented") before A2 is revisited. Written as the POST-A+B
state — i.e. as if stages A (A1 elaborator stamping, A2 group-less
IrrepsIdx kept, A3 restriction) and B (the typed lattice) have landed —
even though, as of this writing (2026-07-28), A and B are IN PROGRESS in
parallel worktrees and C has not been scheduled. Nothing below is grounded
in anything past what §3's stages actually build; where a stage is still
open (A3, C3's perm inference), that is stated inline.

### 6.1 What a user writes

**The pin vocabulary does not change.** `where ml.equiv(G)` and `where
ml.galilean(u, ...)` are unchanged surface — post-A+B is a change to WHEN
and HOW MUCH the compiler can deduce on the user's behalf, not to what a
pin looks like once written. What changes is how much annotation a
function needs before deduction can say anything about it at all.

Today (stage 6a, elaboration-time), a signature must be FULLY annotated to
classify: an unannotated parameter makes the whole signature opaque to the
walker, silently — no suggestion, no error, nothing. Example, as it stands
today:

```blade
// `a` carries no IrrepsIdx annotation. The signature does not classify,
// so inferCertificates skips this function outright.
function combine(a, b: Array<Float like IrrepsIdx<S>>) =
    a + b

let out = combine(u, v)   // u, v : Array<Float like IrrepsIdx<S>>
// today: silence. combine is equivariant, and nothing says so.
```

Post-B, the walker runs at typecheck, after unification has closed `a`'s
type from the call site (§0's point 1 — "an unannotated param that a call
site instantiates at `Array<F like IrrepsIdx<S>>` is classifiable"). The
SOURCE is identical; only the point in the pipeline where classification
happens moves:

```blade
function combine(a, b: Array<Float like IrrepsIdx<S>>) =
    a + b

let out = combine(u, v)   // u, v : Array<Float like IrrepsIdx<S>>
// post-B: BL4011 on combine's declaration —
//   function 'combine' judges O3-equivariant: add 'where ml.equiv(O3)'
```

`a` itself stays unannotated in the source; the deduction closes its type
from call-site evidence rather than requiring the user to write it. This
is a RECALL change, not a syntax change — nothing a user could write
before stops working, and nothing new becomes writable.

One discipline is explicitly NOT affected by this round: `ml.perm_equiv`
(Sₙ) certificate recall stays exactly where it is today. §0.2's claim that
"perm inference becomes feasible" at typecheck is about the underlying
obstruction (ambiguous flat-extent keying resolves once extents are
monomorphized) — actually shipping perm inference is a stage-C3 item
(`plan-transforms-as-types.md` §7), not an A+B deliverable. Anyone reading
this section expecting `ml.perm_equiv` proposals post-A+B should not.

### 6.2 What the compiler says

Three channels, one of them new in kind rather than just in volume:

- **BL4011 / BL4014** (unchanged wording, higher recall). The suggestion
  text a user reads is byte-identical to today's — "function 'f' judges
  O3-equivariant: add 'where ml.equiv(O3)'", "(also requires pinning:
  ...)" for dependency closures, the SO3-upgrade lint's wording. What
  changes is which functions trigger it (partial-annotation recall, §6.1).
- **`ide check --json` `deduced[]` entries** (unchanged schema). `kind`
  "equiv" | "galilean", `owner`, `name` (group, or the comma-joined
  velocity params), `left` (the dependency closure). Post-B populates this
  array from the typed walker's summaries instead of the seam walker's,
  with the same field mapping — a consumer parsing this JSON today needs
  no changes.
- **Synthesized `ml.derive_*` functions visibly carry their certificates**
  — NEW this round, from stage A1. Today, a function synthesized by
  `derive_linear`/`derive_tp`/`derive_poly`/etc. is equivariant by
  construction but UNCERTIFIED in the visible declaration — stage 6a's
  "certified-callee arm" cannot fire on it, so a user function that calls
  a `derive_*` helper gets a deduced (unpinned) summary for that call,
  not a certified one. Post-A1, the elaborator stamps the conjunct
  directly onto the synthesized `FunctionDecls`:

  ```blade
  // synthesized by ml.derive_linear(SIN, SOUT, x, w) — post-A1, this
  // declaration is stamped, not just correct:
  function __derived_linear_3(w, x) where ml.equiv(O3) = ...
  ```

  The payoff is entirely in recall at call sites: a user function
  composing several `derive_*`-synthesized layers now sees CERTIFIED
  callees throughout, the same way a hand-written pinned helper is
  consumed today (§0's "elaborator STAMPs what it knows... typecheck-time
  walker consumes those pins as axioms exactly the way a certified callee
  is consumed today"). Nothing about `ml.derive_*` call syntax changes;
  the stamp is only visible if a user inspects the generated declaration
  (e.g. via a dump flag) or reads a suggestion that now reflects the
  stronger evidence.

### 6.3 What restriction adds (stage A3) — DECIDED 2026-07-28

DECISION (user, 2026-07-28, on the strength of
`exploration-equivariant-bijections.md`): **A+B sequenced.** Explicit
`ml.restrict` ships as the mechanism, implemented as a ZERO-COPY VIEW
where the signed-permutation table exists (the aligned axial class —
measured: both shipped groups, all l ≤ 8, exact) and as a real change of
basis where it does not (non-axial l ≥ 3, misaligned embeddings). The
bijection verdict is computed AT GROUP REGISTRATION as a seventh
integrity family and frozen beside the character table, so §6.4 q4's
answer is "the verdict is mechanical data", not a principle or a per-pair
call. The implicit signature-level license (surface (ii) below) is
deferred as future sugar — a checker-inserted `ml.restrict` under an
`ml.equiv(O3)` pin — gated on settling the weight-sizing spelling (the
restricted spec must be nameable at the call site; the exploration's
§6.3 sizing objection). The original two-surface analysis is kept below
for the record.

### 6.3-orig The two candidate surfaces, as originally drafted

A3 gives an O(3)/SO(3)-typed value a POINT-GROUP view: given an
`IrrepsIdx<spec>`-typed value and a registered point group `g`, the
branching rules (character theory; `plan-transforms-as-types.md` §3.6's
finite tables, `WignerTables`-style) say how each O(3) block splits under
`g` — e.g. an `l=2` block splits into `A1 ⊕ E` under `C4`. This is the
`ml.restrict` promise named in `plan-transforms-as-types.md` §3.6 ("the
hottest adjacent feature") and independently the prerequisite for D1's
cross-group meets (O3 ⊓ Point g). Two surfaces are live candidates; A3
does not choose between them, and neither should this document —
that choice is the user's, which is the point of writing both down.

**Surface (i) — explicit op.** `ml.restrict(x, G)` (or a static
`ml.restrict(SPEC, G)` at the type level) is a real, source-visible
operation the user calls. It produces a value typed under
`PgIrrepsIdx<G, restricted_spec>`, and the branching computation — which
is a nontrivial character-table lookup, not free, and can multiply block
counts (crystal-field splitting) — happens textually at the call site.
The equivariance walker treats it as an ordinary typed primitive with its
own transfer rule, the same way `sym_lift` or `y_to` are primitives today.

```blade
function crystal_field(x: Array<Float like IrrepsIdx<SIN>>)
    where ml.equiv(O3) = ...

let x_c4 = ml.restrict(x, C4)   // explicit, visible, its own line
let y    = crystal_field_view(x_c4)   // consumes the restricted value
```

**Surface (ii) — implicit signature-level classification under a `Point
g` cert.** Extending the polymorphism-license reading from §0: post-A3,
an `ml.equiv(O3)` claim licenses viewing the SAME function at a
`Point g`-restricted signature, the way `comm` already licenses viewing a
kernel at a compact `SymIdx` signature. No `ml.restrict` call appears in
source; a caller expecting a `PgIrrepsIdx<C4, ...>`-typed argument (or a
function certified `ml.equiv(Point C4)`) can pass an O(3)-typed value (or
an `ml.equiv(O3)`-pinned function) directly, and the compiler applies the
restriction under the existing pin's license.

```blade
function crystal_field(x: Array<Float like IrrepsIdx<SIN>>)
    where ml.equiv(O3) = ...

// no ml.restrict call; the O3 pin is read as also licensing the C4 view
// at a call site that only needs C4:
let y = crystal_field(x_c4)   // x_c4 : PgIrrepsIdx<C4, ...>-typed
```

**The tradeoff, not a recommendation.** Surface (i) keeps the branching
computation — and the associated storage/block-count change — visible at
the point where it happens, matching how `sym_lift` and other
compaction-adjacent primitives are already written explicitly rather than
inferred. Surface (ii) is more ergonomic and consistent with how the
polymorphism license already works for `comm`/`SymIdx` and for the
O3-then-SO3 ladder (a `Point g` view falls out of the SAME license that
already lets an `ml.equiv(O3)`-pinned function be called where SO3 is
needed) — but restriction is not a re-view of the SAME data the way
O3→SO3 is (same layout, different label): it can REINDEX into new blocks.
Folding that into an implicit license risks hiding a real computation
(and a real storage change) behind what reads like a free type coercion,
which is close to the exact concern review decision 1 raised about
folding the CLAIM into the unifier in the first place. Whether that
concern applies with the same force to a signature-level license as it
does to a per-expression unifier constraint is an open question, not
answered here.

### 6.4 Open API questions to discuss

1. **Passive surfacing.** Should a deduced-but-unpinned equivariance fact
   ever appear in hover/tooltips by default, or only on request (`ide
   check --json`, BL4011/BL4014 warnings)? Unlike a comm/antisymm
   deduction, an equivariance deduction changes no storage — surfacing it
   costs nothing at the type level — but passively showing it may read as
   an implicit correctness claim stronger than what generator discharge
   or composition actually proved.
2. **Near-miss ladder notes.** The candidate ladder is strongest-first
   (O3, then SO3). When SO3 holds and O3 fails purely on a parity
   mismatch at one block, should the suggestion say so — "would be
   O3-equivariant if block N were (l, even) instead of (l, odd)" — the
   way 6c's near-miss diagnostic already does for coefficient near-misses?
   Or is that too much for a warning channel that today just names the
   group that passed?
3. **Post-C convergence.** Once C lands and galilean/perm become
   discipline instances on the generic typed engine (`plan-transforms-
   as-types.md` §7 stage-6 D2), do their pin spellings (`ml.galilean(u,
   ...)`, `ml.perm_equiv(N)`) and suggestion codes (BL4014, BL4012) stay
   exactly as they are, or does sharing one engine argue for one
   vocabulary across the three disciplines?
4. **One restriction principle, or a per-relationship choice?** §6.3's
   two surfaces are framed as a single either/or, but D1's meet lattice
   has more than one subgroup relationship (O3 ⊃ SO3, O3/SO3 ⊃ Point g).
   Does "same data re-viewed" (O3→SO3) always get the implicit license
   while "data reindexed under branching" (O3→Point g) always gets the
   explicit op — a general principle — or is that a per-relationship call
   the registry has to make one subgroup pair at a time?
5. **Backfilling the annotation itself.** Partial-annotation deduction
   (§6.1) proposes a `where` pin on the FUNCTION once the call site closes
   an unannotated parameter's type. Should it also propose the missing
   `IrrepsIdx<S>` annotation on the parameter itself — the deduction
   already has the closed type in hand to state it precisely — or does
   that blur the line between "the compiler proved a fact" (the pin) and
   "the compiler is rewriting your signature" (an annotation edit)?
