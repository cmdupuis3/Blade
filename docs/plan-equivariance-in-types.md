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
- C1. Declared-cert validation at typecheck via the same walker
  (composition fragment). Elaboration checking stays on; disagreement is
  a compiler bug (the LieGuardFailure posture).
- C2. Engine port: PolyExtract gets a TypedExpr extractor (discharge —
  finite elements and the radical-vector Lie identity — is already
  IR-agnostic; only extraction walks syntax).
- C3. Retire the seam walkers; MLElaborate keeps synthesis + stamping
  only. Galilean and perm land as discipline instances on the generic
  engine (galilean's table is small; perm gains inference per §0.2).

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
