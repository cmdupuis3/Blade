# Blade Documentation Set (v11)

This directory supersedes `blade_formalism_v10.md` as the canonical documentation.
The v10 formalism served as an all-purpose oracle — formalism, proofs, feature
catalog, implementation notes, speculation, and editorializing in one file. It is
now split into focused documents, each with a single job. The v10 files remain in
`rewrite/` as legacy references and are no longer edited.

## The documents

| Document | Job | Canonical for |
|----------|-----|---------------|
| [formalism.md](formalism.md) | The language semantics: types, index types, loop objects, combinators, symmetry system, operational semantics, concrete syntax | What Blade programs *mean* |
| [proofs.md](proofs.md) | Theorem-by-theorem correspondence to the Coq tower (blade-proofs-v16, 241 theorems) | What is *proved*, and exactly how much |
| [features.md](features.md) | Catalog of every language feature with one-paragraph semantics and pointers | What Blade *has* |
| [features/sql.md](features/sql.md) | SQL-like / relational operations (implemented in v7, absent from v10 formalism) | Relational feature module |
| [features/equivariant-nn.md](features/equivariant-nn.md) | Equivariant ML: irreps, CG tensor products, spherical harmonics, message passing | ML feature module |
| [features/graphs-trees.md](features/graphs-trees.md) | Tree structures and graph types via trace indices | Graph/tree feature module |
| [quickstart-1.md](quickstart-1.md) | Quickstart part 1: basics through arity polymorphism and units | Tutorial |
| [quickstart-2.md](quickstart-2.md) | Quickstart part 2: advanced features (completed; was QUIKSTARTp2.md stubs) | Tutorial |
| [examples.md](examples.md) | Worked end-to-end examples | Cookbook |
| [future.md](future.md) | Future plans: v10 §18, extensions research directions, Coq roadmap open items, speculation removed from the formalism | What Blade *might have* |

Related documents that already existed and stay where they are:

- `blade_literature_survey.md` — related-work survey (formalism §19 now just points here)
- `proofs/` (Coq sources, `proofs.zip`) — the machine-checked kernel that proofs.md mirrors

## What changed from v10 (semantic corrections)

These are not editorial. The Coq tower (v16) refuted or sharpened several v10 claims,
and the new formalism states the corrected versions. Details and citations in
[proofs.md](proofs.md).

1. **Product symmetry corrected (v10 §14.5, §14.6, §10.9.5 — the (r!)^d claim).**
   A single identity group over d-dimensional arrays licenses only the *joint*
   (diagonal) symmetry: swapping whole argument index tuples. The per-dimension
   product swap is refuted (BladeCore `Group Law`, second half), and no lossless
   per-dimension product layout exists for r ≥ 2, d ≥ 2 (BladeCounting,
   `counting_general_C`). Output type is `SymIdx<r, compound>` over compound index
   tuples — speedup r!, not (r!)^d. Genuine product factors multiply across
   *distinct commutativity groups*, not across the dimensions of one group.
   At r = 2 the Cauchy storage split (BladeCauchy) recovers per-dimension
   product-canonical storage via two sign-tracked components (sym⊗sym ⊕
   antisym⊗antisym) with exact cell accounting — a structural win, not fewer cells.
   r ≥ 3 is genuinely open (mixed Schur components).

2. **Shared index spaces do NOT license symmetry with distinct arrays (v10 §14.6.2
   middle example removed).** Checked: shared units are insufficient (BladeLowering,
   `shared_units_insufficient`, v10 Thm 9.17); array identity is required. The
   H ∩ Stab law is now an exactness (iff) result (BladeCompleteness,
   `license_exactness`), so the largest sound grant is exactly H ∩ Stab.

3. **Compound-index application canonicalized (v10 §4.5 double-paren vs §5.3    single-paren) — resolved for the TUPLE form** (revised during the Phase 5 SQL    arc, after v7 surfaced its rationale). A rank-k compound axis is ONE index    slot whose domain is k-tuples, applied with one tuple value: `B((lat, lon))`,    wildcards inside the tuple `B((lat, _))`, short tuples pin a leading prefix,    rank-1 compounds take a bare scalar (1-tuples collapse). The flat form    `B(lat, lon)` is a type error with a steering diagnostic: under    `A(i, j) ≡ A(i)(j)` sugar it would claim two slots, and it turns ambiguous    once wildcards meet trailing dims (`B(a, _, t)`). v10 §5.3's flat examples    are the superseded side.

4. **`compound(dense, mask)` and the residual-compound representation documented**
   (previously implementation-only). Partial indexing of a CompoundIdx yields a
   FilteredIdx residual (`has_completion` is its executable form; BladeCompound).

5. **Trinity presentation updated (v10 §9.7).** Restated per BladeTrinityAsym: two
   generators (loop reification, dimensional currying) plus forced closure (arity
   polymorphism), rather than three co-equal features.

6. **MonadPlus laws pinned to the checked set (v10 §12.9).** Left zero, both
   identities, LEFT distribution (+ bonus right zero). Right distribution provably
   fails for the computation monad (BladeMonad) — the rewrite must not assume it.

## What moved where (v10 section routing)

| v10 section | Destination |
|-------------|-------------|
| Abstract, §1 Introduction | features.md (motivation), quickstart-1.md |
| §2.1–2.3, 2.6–2.8 S/T vs T/S | formalism.md §1 (compressed) |
| §2.4–2.5, 2.10–2.11 theorems | proofs.md (statements + Coq refs); formalism cites |
| §2.9 linguistic parallel, "why not discovered" remarks | future.md (speculation appendix) |
| §3 Preliminaries | formalism.md §2 |
| §4 Index types | formalism.md §3 (with corrections 3, 4) |
| §4.15.4 EquivIdx foundation, §8 Equivariance system | features/equivariant-nn.md; formalism keeps the annotation hook |
| §5 Array types | formalism.md §4 |
| §6 Functions (incl. Reynolds §6.4) | formalism.md §5 |
| §7 Core operations | formalism.md §6 |
| §9.1–9.5, 9.8–9.9 Loop objects | formalism.md §7 |
| §9.6–9.7 Trinity/uniqueness proofs | proofs.md; formalism states results |
| §10 Arity polymorphism (§10.9 corrected) | formalism.md §8 |
| §11 Dimensional currying | formalism.md §9 |
| §12 Combinator algebra | formalism.md §10 (laws; proofs in proofs.md) |
| §13 Symmetry system (corrected) | formalism.md §11 |
| §14 Triangular iteration (§14.5/14.6 corrected) | formalism.md §12 |
| §15 Type system | formalism.md §13 |
| §16 Operational semantics | formalism.md §14 |
| §17 Concrete syntax | formalism.md §15 |
| (not in v10) SQL-like operations | features/sql.md + formalism.md §15 pointer |
| §18 Future work | future.md |
| §19 Related work | blade_literature_survey.md (pointer only) |
| §19.8 novelty/impact scoring, §20 conclusion/canonicity | dropped (editorializing); §20.2 claims absorbed into proofs.md coverage table |
