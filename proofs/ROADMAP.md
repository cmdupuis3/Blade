# Blade Proof System -- Roadmap for Open Items

This document lists every remaining deferred item, with a concrete
design where the direction is settled and a confidence note
where it is not.


## Open items

### 1. Surface-calculus progress/preservation (large; sequencing note)

The one species of theorem the tower still lacks: type soundness for
a Blade-core surface calculus elaborating into BladeCore section 7's
intrinsic types (where bounds safety is definitional). Estimate 500+
lines and, more importantly, it hard-codes surface syntax -- so it
should WAIT until the compiler rewrite settles the surface language,
or it will be proved twice.

## Consolidation note (no action yet)

Three BladeCore results are now subsumed by general theorems:
lj2/lj3 (by BladeDMWF's lj/unlj), diagonal_swap_is_symmetry (by
diagonal_group_law). Keeping them costs nothing and preserves the
readable r = 2 entry points; prune only if the tower is ever
published as a standalone artifact where redundancy reads as padding.

## Standing tactic-trap ledger (Coq)

1. `subst x` with multiple defining equations for x may consume the
   wrong hypothesis (three occurrences to date). After `inversion`,
   prefer Forall_inv-style projections or targeted substitution.
2. Fixpoints unfold only when the struct argument is
   constructor-headed (C n 0 does not reduce for variable n).
3. `repeat split` / `constructor` can close goals by conversion,
   desynchronizing bullet scripts. Use explicit `split; [| split]`
   when bullets carry structure.
4. `simpl` pre-reduces singleton maps and under binders; `in_map_iff`
   and staged brackets may then fail. Destruct literal membership;
   fuse brackets into single sentences with `;`.
5. Quartic `nia` can OOM; factor inequalities to quadratic cores.
6. Rewriting with a lemma whose LHS pattern matches its own RHS
   (swap lemmas) loops under `repeat`.
