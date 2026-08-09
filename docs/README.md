# Blade Documentation Set

Hub for most documentation, including guides, formalisms, and proof explanations.

## The documents

| Document | Description | Canonical For |
|----------|-----|---------------|
| [formalism.md](formalism.md) | The language semantics: types, index types, loop objects, combinators, symmetry system, operational semantics, concrete syntax | What Blade programs *mean* |
| [proofs.md](proofs.md) | Theorem-by-theorem correspondence to the Coq proof stack (241 theorems) | What is *proved*, and exactly how much |
| [features.md](features.md) | Catalog of every language feature with one-paragraph semantics and pointers | What Blade *has* |
| [quickstart-1.md](quickstart-1.md) | Quickstart part 1: basics through arity polymorphism and units | Tutorial |
| [quickstart-2.md](quickstart-2.md) | Quickstart part 2: advanced features | Tutorial |
| [examples.md](examples.md) | Worked end-to-end examples | Cookbook |
| [features/sql.md](features/sql.md) | SQL-like / relational operations | Relational feature module |
| [features/equivariant-nn.md](features/equivariant-nn.md) | Equivariant ML: irreps, CG tensor products, spherical harmonics, message passing | ML feature module |
| [features/graphs-trees.md](features/graphs-trees.md) | Tree structures and graph types via trace indices | Graph/tree feature module sketch |

Related documents that already existed and stay where they are:

- `blade_literature_survey.md` — related-work survey 
- `/proofs/` — the machine-checked kernel that proofs.md mirrors

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

3. **Compound-index application canonicalized (v10 §4.5 double-paren vs §5.3
   single-paren) — resolved for the FLAT form.** A rank-k compound axis indexes
   like `SymIdx`: k positional subscripts, `B(lat, lon)`, with trailing regular
   dims appended (`B(lat, lon, t)`; omitting the trailing index yields the
   trailing-row sub-view). v10 §5.3's flat examples are the surviving side.
   (The tuple form `B((lat, lon))` was canonical during the Phase 5 SQL arc
   because wildcards inside the tuple needed a joint domain; with partial
   reads moved to `SparseIdx` — see 4 — that motivation is gone, and the tuple
   spelling is now a type error steering there.)

4. **Partial/wildcard indexing belongs to `SparseIdx<keys>`, not `CompoundIdx`.**
   A compound's mask makes its valid-tuple table lex-sorted by construction, so
   only leading-prefix pins were cheap there; a sparse key set is hashed and
   unordered, so every wildcard position costs the same gather. `SparseIdx`
   takes the tuple-with-wildcard form (`S((lat, _))`, short prefixes, residual
   reads); the residual of a key set is a key set. `compound(dense, mask)` and
   `sparse(values, keys)` are the two runtime builders. (`has_completion` is
   the residual's executable form; BladeCompound.)

5. **Trinity presentation updated (v10 §9.7).** Restated per BladeTrinityAsym: two
   generators (loop reification, dimensional currying) plus forced closure (arity
   polymorphism), rather than three co-equal features.

6. **MonadPlus laws pinned to the checked set (v10 §12.9).** Left zero, both
   identities, LEFT distribution (+ bonus right zero). Right distribution provably
   fails for the computation monad (BladeMonad) — the rewrite must not assume it.
