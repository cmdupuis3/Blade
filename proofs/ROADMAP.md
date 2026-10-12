# Blade Proof System -- Roadmap for Open Items

This document lists every remaining deferred item, with a concrete
design where the direction is settled and a confidence note
where it is not.


## Open items

### 1. Surface-calculus progress/preservation -- CLOSED 2026-10-12

`BladeSurface.v` (68 theorems): progress, preservation, normalization
and a denotation into the one object (`BladeObject.v`) for the
structure-first core -- loop objects from either side, application,
compute, the elementwise pipe -- over a top-level binding environment,
with the licence (`out_shape`) in the typing rule for application and
the quotient proved as a theorem about typing derivations
(`typed_app_is_quotient`). The syntax it hard-codes is the formalism's
section 7/8/13 core, which the compiler rewrite has settled. What is
NOT covered, each a plan item of
`docs/plans/plan-one-model-surface-calculus.md`: T-dimensions and
`irank` slices, compound identity groups (`s > 1`), local `let`
(substitution), folds, `let rec`; values are `nat`.

### 1b. The remaining plan items (3-7) -- BUILT 2026-10-12, scopes stated

`BladeTransport.v` (item 3), `BladeAttain.v` (item 4, partial),
`BladeInduction.v` (item 5), `BladeLicence.v` (item 6), `BladeBoundary.v`
(item 7); item 8 is `docs/research/orbit-work-measurements.md`. Each
file's header lists what it does NOT cover; the plan's status lines and
docs/proofs.md "What remains unproved" collect them. The largest open
piece is decision D1 (compound identity groups are left dense by the
typing rule, so the typed orbit work is above the free-model optimum:
`d1_not_built_pin`), then general split identity classes (a
position-permuted layout), the pipeline lower bound beyond the two-node
schema, and local `let` / `ERec` in the calculus.

## Consolidation note 2 (no action yet): lemmas proved in the item 3-7 files that belong in older files

Builders were barred from editing existing files, so these sit locally
under distinct or `my_`-prefixed names. Move them when the next change
touches the target file: BladeSurface -- `rle_mult_pos` (an inline
sub-proof of `apply_kernel_canon`), `dense_shape_app`, `bind_names_app`,
`out_shape_rename`, `enum_out_in_dense`, `quotient_never_costs`,
`rle_in`, `rle_length`, `surface_data_bridge`; BladeObject --
`block_perm_app`, `scanon_idem`, `scanon_length_w`,
`nondecr_firstn`/`nondecr_skipn`, `insert_nondecr`/`isort_nondecr`, the
rank-1 leading-record facts (`enumShape_lead`, `scanon_lead`,
`block_perm_lead`, `shape_dense_lead`); BladeOptimal -- `tab_perm`,
`tab_local`, `cD_inj2`, `svals_sep_gen` (generalizes `svals_separate`),
`pbind`/`tr_prog` (program sequencing), `support_lower_bound` (beside
`orbit_lower_bound`); BladeArrow -- `strict_length` (the antisymmetric
arrow has C(u - l, r) elements), `seq_canonical`; BladeCompleteness --
`perm_pair_2`; BladeMixedRadix -- `shapeCard_app`; BladeBinomial --
`tr_C_n_1`; BladeDMWF -- `canonical2_inv`.

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
7. `simpl` unfolds `seq start (S n)` to a cons, so a membership
   hypothesis produced by `in_map_iff` no longer matches `in_seq`.
   Unfold only the fixpoint you meant to: `cbn [f]`
   (BladeSymPower s2_cells_aux_spec).
8. `apply` unification does not reduce truncated subtraction: a goal
   `_ = S m * (S m - 1)` will not unify with a lemma stated at
   `_ = S m * m`. `replace ... by lia` first.
9. `map_ext` is a rewrite, not a closer: on `lsum (map f l) =
   lsum (map g l)` you must `f_equal` first, or `apply map_ext` fails
   against the `lsum` head. And after a `cbn` has already reduced the
   body, `rewrite (map_ext (fun j => 0) (fun _ => 0))` is a no-op --
   Coq reports "generated a subgoal identical to the original goal",
   which reads like a tactic bug and is really "you already have it"
   (BladePartition fibre_sum / filter_le_fibres).
10. Two recurrences for one function (peel-first vs peel-last) meet in
   a goal where the SAME term must expand differently on each side.
   Plain `rewrite H` hits both occurrences and destroys the identity;
   use positional `rewrite H at 1`, then rewrite the other equation to
   catch the survivor, then `nia`
   (BladePartition stir_open_peel_last).
11. The other end of trap 9: `f_equal` on `lsum (map f l) = lsum (map g
   l)` may CLOSE the goal outright when `f` and `g` became convertible
   under an intervening rewrite. The next tactic then fails with "No
   such goal", which again reads like a tactic bug and is really "you
   already have it" -- drop the `f_equal; apply map_ext` pair for a bare
   `reflexivity` (BladePointGroup pg_hom_dim_spec_sum, after
   `lsum_filter_guard`).
13. `O` in a PATTERN is nat's zero, not a variable: `| EAppO O _ => ...`
   is refused with "Found a constructor of inductive type nat while a
   constructor of expr is expected". Binders named `O` in `forall` are
   fine, which is why the inductive compiled and the `Fixpoint` after it
   did not (BladeSurface `size`).
14. `rewrite <- L` with `L : length t = n` rewrites EVERY `n`, including
   the ones inside hypotheses you are about to `exact`, and `rewrite <- L
   at k` counts occurrences in display order, which is not the order you
   wrote. State the split lemma with the length as a premise
   (`firstn_app_exact : length t1 = n -> firstn n (t1 ++ t2) = t1`,
   BladeObject; `tab_nth_r`, BladeSurface) and never rewrite the length.
15. `f_equal` on `map f l = map g l` closes the goal by conversion when
   `f` and `g` compute to the same thing on a VARIABLE argument
   (`group_shape false g` is `dshape g` by `false && _`), and the
   `apply map_ext` after it then fails with "No such goal" -- the same
   shape as trap 11, one level up (BladeSurface `comm_license_needed`).
16. A Section-local type alias (`cquery`) is not syntactically equal to
   its unfolding under `rewrite`; use `etransitivity; [exact H |]`
   (BladeInduction).
17. Stdlib `ListDec.NoDup_dec` ends in Qed, so a definition that branches
   on it does not `vm_compute`; use a transparent boolean test with a
   `reflect` lemma when a pin must compute (BladeAttain `nodupb`).
18. Import order decides name clashes: BladeLicence imports
   BladeNumericContract FIRST so the later imports win `tab`.
12. A double quote inside a Coq comment opens a STRING, and a `*)` that
   falls inside it does not close the comment. The tower's box-comment
   headers are one `(* ... *)` per line, so a quotation that opens on
   one line and closes on a later one silently swallows every `*)`
   between -- Rocq 9 reports it as `comment-terminator-in-string`
   warnings, one per line, and the header still parses only by luck.
   Keep quoted phrases inside a single line, or use dashes.
