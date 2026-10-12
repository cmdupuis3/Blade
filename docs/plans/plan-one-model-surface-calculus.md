# Plan: one model, one calculus — making the tower a theory

Status: **ALL EIGHT ITEMS ADDRESSED 2026-10-12** on `feat/one-model-surface-calculus`.
Items 1–2 built (`proofs/BladeObject.v` 40, `proofs/BladeSurface.v` 68). Items 3–7 built
by five Opus agents, each file independently reviewed by an adversarial Opus reviewer
and revised against every finding (`BladeTransport.v` 103, `BladeAttain.v` 115 — partial,
`BladeInduction.v` 82, `BladeLicence.v` 87, `BladeBoundary.v` 51). Item 8 measured
(`docs/research/orbit-work-measurements.md`, reviewed and revised likewise). Tower total
1908; `coqc` + `coqchk` clean; every headline closed under the global context (no axioms).
Each item's status line says what is checked and what is not. §3 records what the
mechanization and the measurement taught; §4 records the decisions: D1 is NOT built
(and matters for optimality), D2/D3 were resolved by items 6/5, and a new D4 (= paper
plan E14) is the user's.

Companions: `plan-uniform-optimality.md` (the orbit bound this plan restates as a theorem
about typing derivations), `plan-fastest-way-principles.md` (the principles the bound
would make enforceable), `docs/proofs.md` (the map of the tower), `docs/formalism.md`
(§1 the S/T model, §2.7 the DMWF, §3.9 symmetry in the index type, §7 loop objects,
§8 arity polymorphism, §11 the lowering law, §13 the type system).

---

## 0. The claim, in one sentence

An index type is a finite set with two kinds of structure — a group action and a
well-founded order on its leading axis; arrays are functions on it; programs are maps
that respect that structure; and compilation is the computation of the quotient.

The tower already proves most of the local theorems this sentence needs (roughly 1,360
statements over 44 files). What it lacks is what makes a collection of theorems a
*theory*: one semantic object every statement is about, a surface calculus that
denotes into it, and the headline stated at full generality rather than at the cases
that were convenient to check. This plan is the list of those gaps in dependency order,
and the build log of the first two.

**The DMWF is the core.** The user's standing position, and the one this plan adopts as
its organizing principle: the Double Metamorphism With Feedback (`proofs/BladeDMWF.v`,
formalism §2.7) is not one layer among many but the object the rest of the tower is
*about*. Concretely, the DMWF's enumeration of a record `(r, l, u)` — a structural
unfold whose seed is the residual index type, the emitted index feeding back as the
residual's lower bound — produces exactly one representative of every orbit of the
symmetric group `S_r` on the dense box `[l, u)^r`. That is the quotient. The group
action is not a second structure laid beside the DMWF; the feedback *is* the group
action, computed. Item 1 proves this as a theorem rather than a reading, and the one
model is then the DMWF with its quotient property made explicit, not a new object the
DMWF happens to instantiate.

## 1. What a proper theory needs (the eight items)

Numbered in dependency order. Each names what exists, what is missing, and the
artifact that would close it.

### 1. One model — STATUS: BUILT (`proofs/BladeObject.v`)

**Exists.** Four models, each the right one for its file: the index universe and its
enumeration (`BladeDMWF`, `BladeArrow`, `BladeShape`), the materialized-value
semantics `Arr U = Shape × (list nat → U)` with `veval` (`BladeCompute`, `BladeMonad`),
the oracle model of adaptive programs (`BladeOptimal`'s `prog`/`run`/`trace`), and the
free term model where a value is the name of a subterm class (`BladeOrbitWork`,
`BladeDeduceExact`). Nothing states how they are the same object.

**Missing.** The definition every theorem refers to, and the proofs that the four are its
projections.

**Built.** `BladeObject.v` states the object as the DMWF with its quotient property:

- *The dense box and the sorted characterization.* `box r l u` enumerates `[l, u)^r`
  (`box_spec`, `box_NoDup`, `box_length`); `canonical_iff_nondecr`: the DMWF's canonical
  tuples are exactly the dense tuples that are nondecreasing.
- *The transversal theorem* (the quotient). The orbit relation of `S_r` on tuples is
  list `Permutation`. `enum_hits_every_orbit`: every dense tuple is a permutation of a
  canonical one (its insertion sort). `enum_hits_each_orbit_once`: two canonical tuples
  in the same orbit are equal. `enum_is_quotient`: the DMWF's enumeration is the image
  of the box under the canonical-form map, whose fibres are exactly the orbits
  (`isort_fibres_are_orbits`). So `length (enum r l u)` IS the orbit count, and
  `BladeBinomial`'s `C(u − l + r − 1, r)` is the number of orbits.
- *Blockwise, for shapes.* The Young action permutes within each record's block;
  `block_perm` is its orbit relation, `shape_dense` / `shape_box` the dense box of a
  shape, `scanon` (`BladeOptimal`) the blockwise canonical form. `scanon_in_shape`,
  `scanon_block_perm`, `enumShape_hits_each_orbit_once`, `scanon_fibres_are_orbits` and
  `enumShape_is_quotient` restate the transversal theorem for every shape;
  `shapeCard_is_orbit_count` against `shape_box_length` is orbits versus dense cells.
- *Arrays on the quotient; access is canonicalization.* An `Arr U` is a function on the
  transversal. `qread A t = snd A (scanon (fst A) t)` is the canonicalizing read of
  formalism §3.9 and §12.2; `qread_invariant` says it is constant on orbits
  (`A(3, 1) = A(1, 3)`), `qread_fixes_transversal` that it is the identity on canonical
  tuples, and `qread_is_storage` that it equals the lookup at the tuple's position in
  the materialized list — storage, iteration and access as one object.
- *Projection to the oracle model.* `object_prog s = ask_all (enumShape s)`:
  `object_prog_run` says its result is `veval`, `object_prog_trace` that its questions
  are the transversal, `object_work` that it asks exactly `shapeCard s` questions.
  `young_prog_is_object_prog`: `BladeOptimal`'s attaining program is the object's
  program read through the data. With `uniform_optimality_young_nat` this closes the
  circle: the orbit count is forced by every correct program and attained by the
  object.
- *Projection to the free model* is the licence bridge in item 2's file
  (`BladeSurface.deduced_comm_is_licence`): a kernel body that is equal modulo the
  declared laws to its own swap is `invariant_under` that swap, by
  `BladeDeduceExact.teq_sound`.

### 2. A surface calculus with progress and preservation, denoting into the model — STATUS: BUILT (`proofs/BladeSurface.v`)

**Exists.** The formalism's typing rules (§13.2) and operational model (§14) as prose;
`proofs/ROADMAP.md` lists "surface-calculus progress/preservation" as the one missing
species of theorem and sequences it after the surface syntax settles.

**Missing.** Until it exists, every theorem is about a model and none is about a Blade
program. The typing judgment must carry the licence group, and "compilation is the
quotient" must be a theorem about typing derivations.

**Built.** `BladeSurface.v` is the structure-first core, closed over a global binding
environment (Blade's top level: a flat sequence of `let`s; identity is by name, §8.3):

- *Syntax.* Arrays `EArr s k`; names `EVar x`; loop objects `EMethod xs` (the outer
  product, §10.2's `<*>`) and `EZip xs` (co-iteration); `EObject f`; application
  `EAppM L f` / `EAppO O xs` (`<@>` from either side, §7.1); `ECompute c`
  (`|> compute`); `EPipe e g` (the elementwise `>>@` fragment). A kernel is an arity,
  a body over position-indexed values, and a `comm` flag; `kwf` is the well-formedness
  premise of §13.2's Fun-Intro: locality, and `comm = true` only when the body is
  invariant under every permutation of its positions (`BladeLowering.invariant_under`,
  `BladeCompleteness.perm_pair`).
- *Types.* `TArr s`, `TLoop b` (the binding: operand names with their shapes — the
  loop's structure, §7.2), `TZipLoop s`, `TObj f`, `TComp s`. The licence lives in the
  rule for application: `out_shape (k_comm f) b` groups neighbouring equal names and,
  under `comm`, replaces a group of `g` rank-1 operands over `Idx<l, u>` by the record
  `SymIdx<g, l, u>` — §8.4's step 3 at `s = 1`, §11.2's `H ∩ Stab` made a function of
  the derivation.
- *Semantics.* Small-step `step ρ e e'` (§14.4's `compute` as reduction); `value`;
  the kernel reads each operand at its segment of the output tuple (`apply_kernel`).
  `progress`, `preservation`, and `normalizes` (`step_size`: a size measure strictly decreases),
  under `env_ok G ρ`.
- *Denotation.* `den ρ e : Arr nat` into the one object; `step_den`: every step
  preserves it on the nose; `eval_sound`: every well-typed array term evaluates to an
  `EArr` whose kernel is the denotation's, so `veval` of the denotation IS what the
  program computes (the materialized-value semantics is the calculus's).
- *The quotient, as a theorem about typing derivations.* `typed_app_is_quotient`: for a
  well-typed `L <@> f`, the dense output (every tuple of the ungrouped shape — what a
  collection-first system would compute) is recovered from the typed output by
  canonicalizing access, and `shapeCard` of the typed shape is the orbit count of the
  identity-group action; with `object_work` the typed shape is exactly the work the
  object does. `comm_license_needed`: with `comm` off the typed shape is the dense one
  (nothing is quotiented without a declaration). `method_vs_zip_rank`: the outer product
  and co-iteration of the same operands have different output ranks (the documented
  bug class, CLAUDE.md "Rules that prevent real bugs").
- *The free-model bridge.* `deduced_comm_is_licence`: a term body equal modulo the
  declared laws to its own swap yields a kernel that satisfies `kwf` with `comm = true`
  — deduction (`src/Deduce.fs`, proved exact in `BladeDeduceExact`) is a licence source.

**Scope, stated once.** Rank-0 kernels (no T-dimensions, §7.2, no `irank` slices);
identity groups quotiented only over rank-1 operands (§8.4's `s = 1`; compound
`SymIdx<g, I₁ × … × I_s>` is item 3); values are `nat` (the tower's convention; the
Cantor-pairing closure of `BladeOptimal` is what makes "generic data" a closed
statement); no local `let` (expressions are closed over the top-level environment);
no folds, no `let rec` (item 5). Each is a mechanical extension or a listed item, not a
hole in the argument.

### 3. Functoriality of the quotient through every combinator — STATUS: BUILT (`proofs/BladeTransport.v`, 103)

The group of an outer product is the product of the groups; of a zip, the pullback
along the diagonal (the common group); of `>>@`, the group of the first stage; of `<&!>`,
the meet. `BladeMonad` has the value-level laws; the transport of the licence group
through each combinator does not exist. This is also what closes the orbit work formula
`W = Σ_v ω(G_v, X_v)` over an arbitrary schema (`BladeOrbitWork` proves the term form and
one two-node instance). Artifact: a `group_of : expr → Shape` function on the calculus of
item 2 with one law per constructor, and `orbit_work_schema` by induction on it.

**Built.** A shape IS the group data, so the group of a computation is `fst (den e)` and
`group_of` reads it off the syntax (`group_of_den`, `group_of_laws`). `<*>`: the
identity groups of `method_for(xs1 ++ xs2)` are the factors' groups GLUED at the seam
(`rle_app`, `out_shape_app`); concatenation and multiplicative cardinality hold when no
group straddles the seam (`out_shape_app_clean`, `shapeCard_product_clean`), and in
general the count is at most the product (`shapeCard_product_seam`) — the product of
the groups is a supergroup, not the product (§3). zip: the common group
(`zip_transport`; the pullback is trivial because `T_Zip` refuses mismatched shapes).
`>>@`: the first stage's group, `qread` commutes (`pipe_transport`). A pair layer above
the calculus for `<&>` / `<&!>` (`has_ty2`, `den2`, `step2`, `pair_sound`):
`par_group_product`, `fuse_meet` (the meet is boolean conjunction of the two licences;
`meet_necessary_pin`), `fuse_is_par`, `fuse_object_prog` (one traversal of the meet),
and `fusion_evaluations`: fusion never saves kernel evaluations and costs some when the
licences disagree (50 vs 40 at n = 5). Orbit work: `orbit_work` = Σ `shapeCard` over a
pipeline's stages; `pipeline_attains_orbit_work` (the composed object program asks
exactly that many questions and every stage is `veval` of its denotation);
`stage_is_surface` (every stage, `Prev` sources included, is a typed surface term whose
`group_of` is its summand); `two_node_translation` (for `g(h(A i), h(A j))` the
calculus's program relabelled to BladeOrbitWork's symbols is a free-model program that
meets and attains that file's bound `n + C(n+1, 2)`).

**Not built.** The free-model lower bound beyond the two-node schema: a general
translation gives free bound ≤ `orbit_work`, with equality only when each comm stage's
typed group is its whole licence (`typed_work_not_optimal_pin`: 12 vs 9). Compound and
symmetric-operand groups are left dense (D1; `d1_not_built_pin`: 16 vs 10). Folds out.

### 4. Attainment for every group the type system can express — STATUS: BUILT, PARTIAL (`proofs/BladeAttain.v`, 115)

`BladeOptimal` attains the bound at `H = S_R` for every binding and at the sign
character for `r = 2`. Missing: a proper subgroup `H < S_R` (a kernel commutative in only
some positions; block-Young `H` is a blockwise sort and should be short), the signed
bound at general `r` (needs the sign of a position permutation), kernels of arity above
two inside pipelines, and folds. Without these the headline reads "optimal in the cases
we checked". Artifact: `uniform_optimality_subgroup`, `antisym_lower_bound_r`.

**Built.** Block licences (`Klab`, `Kblock`: commutative only inside declared blocks;
`Kblock_single_is_Ksym`; explicit members in and out of `Ksym`), and THE MEET of the
kernel's blocks with the binding's cut: `meet_lower_bound`, `meet_storage_bound`,
`meet_attained`, `meet_prog_is_object_prog`, `uniform_optimality_subgroup` (closed with
Cantor data), with the kernel cut alone (`uniform_optimality_block_nat`, blocks [2;1]
at n = 4: 40 forced) and the data cut alone (BladeOptimal's Young nest,
`margs_id_is_shargs`) as instances, and `meet_ex_forced` (400 forced). The surface
rule: `out_shape` under comm IS the meet when the groups have distinct names
(`surface_comm_optimal`), `den` of a typed application is the kernel at that call site
(`surface_den_is_call_site`), and without comm the dense shape is forced
(`surface_nocomm_optimal`). The boundary: `aba_beats_surface_rule`,
`aba_orbit_count_forced`, `aba_gap_strict` — formalism §8.3's neighbour rule is SOUND
(BladeSurface) but NOT OPTIMAL at (A, B, A). Signed at every r: `Kalt` (adjacent
anti-invariance only) is the full sign character (`Kalt_sign_character`),
`alt_vanishes`, `antisym_lower_bound_r` (C(n, r) forced over BladeArrow's strict arrow),
`antisym_attained_r` with a COMPUTING signed read (`alt_read_pin`, Vandermonde),
`Kalt_2_is_Kanti`.

**Not built.** General split classes (`comm(a, c)` over (A, B, A): needs a
position-permuted layout); signed kernels over several arrays; mixed
symmetric/antisymmetric blocks; Hermitian conjugation; arity > 2 inside pipelines
(prose); folds (overlapping-fibre sharing is Ensemble Computation).

### 5. The inductive side — STATUS: BUILT (`proofs/BladeInduction.v`, 82)

The `let rec` arm (formalism §7.5) appears only in the recurrence-reduction research
files, as a model fragment that backs no shipped feature. The one object reserves the
slot: a leading axis carries a well-founded order, and a recursive array is the
structural induction along it. Needed: the one-buffer semantics as a well-founded
fixpoint with the zero-past-prefix rule (§7.5 "implicit zero history"), its work bound
(cells on the leading axis × orbit count on the rest), and the composition law with the
group side (the recursion axis leads; the quotient survives on the remaining axes).
Artifact: `BladeInduction.v`, `letrec_is_wf_fixpoint`, `letrec_work`,
`letrec_commutes_with_quotient`.

**Built.** A step `nat -> (Z -> V) -> V` (the ordinal and the prefix read; reads are
integer-indexed, see §3), the one buffer `lr_buf`, `zread` zero outside `[0, n)`.
`letrec_fixpoint_self` + `letrec_unique`: `rec_build` is THE solution of the
self-referential zero-extended recurrence; `letrec_prefix_stable`;
`letrec_zero_history` (the AB3 law: an unconditional `prefix(n − j)` equals the
hand-guarded form). Work in the oracle model: `letrec_work` (N ordered questions),
`letrec_lower_bound` / `letrec_work_optimal` (N forced for every local class, attained),
`letrec_work_shape_optimal` (N × `shapeCard s`, one question per (ordinal, orbit),
forced for orbit-invariant steps and attained). Composition: `traj` over
`mkIx 1 0 N :: s`; `block_perm_lead` (the leading ordinal is never permuted);
`dense_agrees_with_quotient` (for an orbit-invariant step reading its prefix at dense
tuples, the quotiented trajectory agrees with the DENSE recurrence at every dense cell —
`orb_inv_needed_pin` shows it fails without invariance); `letrec_commutes_with_quotient`
packages storage, agreement, storage lookup and per-slice symmetry. The `while` guard:
`gbuf_spec`, `letrec_guard_prefix_agrees`, `letrec_guard_frozen`, `letrec_guard_budget`
(BL8010 iff the guard never fails), `letrec_seeded_guard`. `rec_arr` / `letrec_veval`:
the object an `ERec` constructor would denote.

**Not built.** The `ERec` constructor in BladeSurface itself; the storage policy
(rolling window); nested `let rec`; AD through recurrences.

### 6. Numerics as a parameter — STATUS: BUILT (`proofs/BladeLicence.v`, 87)

The group acts on indices and promises nothing about floats. `BladeNumericContract`
shows which fragments are exact (wrapping integers, rationals) and that a reassociation
licence is not a distributivity licence. The main theorem should be stated relative to a
declared value equivalence, so the licence mechanism (`omp` on a reduce,
`BLADE_FP_REASSOC`) is inside the theory rather than beside it. Artifact: `kwf` and
`invariant_under` parameterized by an equivalence on values.

**Built.** `invariant_under_R`, `Ksym_R`, `Kcls_R`, `kwf_R` (R = eq gives the tower back:
`Ksym_eq_iff`, `kwf_eq_iff`), and — so the licence is inside the TYPING, not beside it —
`has_ty_R` with `kwf_R` in the three kernel rules, `has_ty_R_eq_iff`, and `progress_R`,
`preservation_R`, `eval_sound_R`. `typed_app_is_quotient_R`: the quotient theorem from a
`has_ty_R` derivation, up to R, needing only reflexivity and transitivity (§3).
`licensed_not_exact`: a kernel refused by `has_ty` and typed by `has_ty_R` (parity).
Reduce: `fold_perm_invariant` (exact for an associative-commutative op),
`fold_perm_invariant_R` (reordering under a congruence), `reassoc_licence` /
`tree_reduce_R` (every bracketing of every reordering, which needs the seed to be an
R-identity: `fold_split_needs_identity`). Rounding at every precision p ≥ 2:
`kfold_opr_not_kwf`, `rounded_fold2_exact`, and `assoc_congruence_collapses` /
`rounded_licence_coarse` — any R under which rounded addition is associative already
relates 2^p and 2^p + 2, so a float licence must be DECLARED and is necessarily coarse.

**Not built.** The orbit LOWER bound relative to R (only attainment is restated; a
total R would defeat the adversary); R as a model of IEEE rounding (the error bounds
live in `proofs/reals/`, outside the tower); a tolerance licence (`|x − y| ≤ ε` is not
transitive, so it is not a licence here).

### 7. A stated scope boundary — STATUS: BUILT (`proofs/BladeBoundary.v`, 51)

The bound is for kernel-generic programs. `nongeneric_data_beats_bound` refutes it for
non-generic data; the analogous statement for structured kernels (an FFT beats the
bound) does not exist. Define the free class precisely and prove the bound tight exactly
there. Artifact: `structured_kernel_beats_bound` (a witness) and the class definition.

**Built.** Freeness: `free_on` (every target class perturbable within the law class —
`orbit_lower_bound`'s own hypothesis, named), `indicator_closed` (an intrinsic form that
implies it), `Ksym_free`, `free_class_bound` + `canonical_task_attained` (the bound holds
and is attained on every free class). The structured witness is the SEPARABLE class
`Ksep` (`f(a, b) = g a + g b`), not the FFT (which compares different things and is kept
as an explicit analogy): `Ksep ⊆ Ksym`, `sep_determined`, `separable_nowhere_free`;
`sep_prog` answers every dense cell of the n × n square with n questions
(`structured_kernel_beats_bound`, n < C(n+1, 2)); its query complexity is settled —
worst case exactly n (`sep_query_complexity`), per kernel ≥ ⌈n/2⌉ with ⌈n/2⌉ attained at
the zero kernel; and `sep_storage_bound`: the STORAGE bound still holds (a single-cell
store needs C(n+1, 2) cells), so the witness beats kernel evaluations only, by moving
work into its reader. `orbit_bound_boundary` packages it; `genericity_two_sides` pairs
the data-side and kernel-side failures. `support_lower_bound` is the width-w
generalization of the orbit bound that the separable lower bound needed.

**Not built.** The converse (not-free does not imply beatable: an inequality law
between orbits blocks perturbation without saving worst-case queries); r > 2 separable
classes.

### 8. Falsifiable claims about other systems — STATUS: MEASURED (`docs/research/orbit-work-measurements.md`)

The collection-first impossibility results (`tdim_relationality`,
`residual_not_constant`, `output_family_not_constant`) are the only statements about
systems other than Blade. Predict orbit work for a published hand-optimized symmetric
kernel in another framework and measure whether it attains the bound; SySTeC is the
first target (`plan-uniform-optimality.md` §7.1). Artifact: a measured table, not a
theorem.

**Measured.** Blade's kernel-call counts (a captured `let mut` counter in a serial
kernel) and stored cells at n = 7, 11, 23, 45: `(A, A)` comm 28 / 66 / 276 / 1035 =
C(n+1, 2); `(A, A, A)` comm C(n+2, 3); `(A, A, B, B)` C(n+1, 2)·C(m+1, 2); the negatives
`(A, A)` without comm and `(A, B)` with comm are dense. NumPy (3.14.7, 2.4.6) is dense on
every row; BLAS `dsyrk` through SciPy writes exactly C(n+1, 2) cells into an n² array.
SySTeC is specified with refutation conditions (Julia is not installed). The one
mismatch is `(A, B, A)` with comm: the compiler counts n·C(n+1, 2) — the by-name quotient
item 4 proves optimal — but types the result with the group axes concatenated,
(A, A, B), so source-order subscripts read the wrong cells (36.0 vs 44.0; two cells the
declaration says are equal read 36.0 and 50.0) and a valid read crashes at mixed
extents. That is a compiler defect, filed as a separate task; the theory side is
decision D4 below.

## 2. What items 1 and 2 change in the tower

- Two new files at the end of `_CoqProject` (`BladeObject.v` after `BladeDeduceExact.v`,
  `BladeSurface.v` after it). Both import the tower's existing definitions rather than
  restating them: the object is `BladeDMWF.enum` / `enumShape`, arrays are
  `BladeCompute.Arr`, programs are `BladeOptimal.prog`, canonicalization is
  `BladeOptimal.scanon`. No existing file is modified, so every existing theorem name
  is stable.
- `docs/proofs.md` gains one section per file and two rows in the coverage table
  (§13.2 typing rules; §14.4 compute); the headline count moves; "What remains unproved"
  drops surface-calculus progress/preservation and gains the item-3/4/5 statements in
  its place. `proofs/README.md` gains the two per-file entries. `proofs/ROADMAP.md`'s
  item 1 is closed with a pointer here.
- Nothing in `src/` changes. The calculus models `TypeCheckInfer`'s loop-object judgment
  and `Deduce.fs`'s licence; it is not extracted from them. The tripwire that keeps the
  model honest is the existing corpus (`tests/corpus/symmetry/`), which pins the
  behaviours the calculus's rules assign — a divergence there is a bug in one or the
  other and the plan says which: the corpus is the compiler's spec, the calculus is the
  formalism's.

## 3. What the mechanization is expected to teach (and did)

Recorded as the build proceeds, in the spirit of `plan-uniform-optimality.md` §10's
"one thing the mechanization taught".

- The transversal theorem needs the general-`l` form of `BladeOptimal.isort_canon`
  (stated there at `l = 0` only); `insert_canon` already carries the general bound, so
  the general statement is one induction.
- The identity-group semantics is simplest when a kernel's value tuple is a LIST (one
  value per position) and the kernel reads positions through `nth`: then sorting a
  group's index segment is a `Permutation` of the value list by `Permutation_map`, and
  `BladeOptimal.Ksym_Kcls` (invariance under every position permutation ⟹ constant on
  the sorted value tabulation) discharges the licence without ever constructing the
  position permutation that the sort induces. The position permutation is what
  `BladeLowering` states the licence with; the value-multiset form is what the proof
  wants. Both are in the tower; the calculus uses the second.
- The one place the DMWF's feedback is *visible* in the calculus is `out_shape`: a
  group of `g` operands over `Idx<l, u>` becomes the single record `(g, l, u)`, whose
  enumeration is the DMWF unfold with feedback. Everything else — reading, storage,
  work — is inherited from the object. That is the sense in which the DMWF is the core:
  the surface rule that introduces structure introduces exactly a DMWF record.
- Identity by NAME, not by value, decided the calculus's shape. A substitution-based
  `let` would have to carry each array's binding name through evaluation (so that
  `method_for(A, A)` still sees one identity after `A` is replaced by its value) and
  prove an invariant that no two different arrays ever share a tag in one operand
  list. Closing the calculus over the top-level environment instead — Blade's actual
  top level — makes identity the environment's lookup and the invariant vacuous. Local
  `let` is item 2's first extension, and it should be built as a *scoped* environment,
  not as substitution.
- Grouping never changes the output rank (`swidth_out_shape`), only which tuples are
  stored. The formalism states this nowhere as a theorem; it is what makes the dense
  tuple a valid read of the quotiented array (`shape_dense` of the typed shape) without
  any reindexing, and it is why `method_vs_zip_rank` is a statement about the operands,
  not about the licence.

From items 3–8 (each confirmed by the file's reviewer):

- *The seam.* "`<*>` is shape concatenation" (§10.2) is true of the loop and false of
  the typed output under a licence: `(method_for(A) <*> method_for(A)) <@> comm` glues
  the seam into S₂ ⊋ S₁ × S₁ (15 cells vs 25 at n = 5). σ of a product is the product
  of the groups only when no identity group straddles the seam. Formalism §10.2 now
  says so.
- *D1 matters for optimality, not just expressiveness.* The typing rule leaves compound
  and symmetric-operand groups dense, so the typed orbit work is strictly above the
  free-model optimum there (16 vs 10; 12 vs 9). The free-model pipeline bound is ≤ the
  typed work in general, equal only when each stage's typed group is its whole licence.
- *`<&!>` with mismatched licences* forces the dense traversal for both kernels: it
  saves traversals and costs evaluations (50 vs 40). The meet of two licences in the
  calculus is boolean conjunction.
- *Bodies live in the oracle.* The composed program depends on its stages only through
  operands and licences (`pipe_prog_shape_only`); that is what let the free-model
  bridge be a statement about one program rather than a coincidence of two counts.
- *§8.3's neighbour rule is sound and not optimal.* The mismatch is INSIDE the
  formalism: §8.3/§11.1 (`Aᵢ = Aᵢ₋₁`) grant strictly less than §11.2's "exact" H ∩ Stab.
  Mechanized at (A, B, A): n³ stored where n·C(n+1, 2) is forced and attained. Closing
  it needs a position-permuted storage layout — which is exactly what the compiler
  attempts and gets wrong (item 8).
- *The kernel cut and the data cut are one lemma.* Tagging each value with its block
  label makes the per-record pair (block, array) behave as data, and one separation
  argument (`svals_sep_gen`) covers both cuts and their meet.
- *Adjacent anti-invariance is the full sign character, and the proof is insertion
  sort*: a product of adjacent swaps whose count is the inversion count (`isort_sw_inv`).
  General transpositions are never needed.
- *Implicit zero history needs the integer ordinal.* With nat reads, `prefix(n − 3)` at
  n < 3 silently reads slice 0; the AB3 law holds only because reads are on Z. Lag
  arithmetic must never be lowered into unsigned ordinals.
- *The recurrence lower bound is a re-proof, not an instance of `orbit_lower_bound`*:
  the output at ordinal n depends on the oracle's earlier answers, so the adversary
  needs `lr_buf_agree_below` (perturbing ordinal n leaves the prefix below n untouched).
  With array slices the classes are (ordinal, orbit) pairs and N × `shapeCard s` falls
  out.
- *The canonicalizing read proves nothing about symmetry by itself.* The stored
  trajectory canonicalizes by construction; the content is its agreement with the dense
  recurrence, which needs exactly orbit invariance of the step and prefix reads at dense
  tuples (`orb_inv_needed_pin` fails without the first).
- *The quotient theorem needs only a preorder.* Typing, soundness and the quotient use
  reflexivity and transitivity of R; symmetry never. Transitivity is the real constraint,
  and it is what rules out the natural float licence `|x − y| ≤ ε`.
- *A floating-point licence cannot come from the op's algebra.* Any R making rounded
  addition associative already relates 2^p and 2^p + 2; so a float licence is declared
  as `kwf_R` and is necessarily coarse. Reordering (a congruence) and re-bracketing (a
  congruence plus an identity) are different licences; arity 3 is the least at which
  reordering a rounded reduce of representable data changes the result.
- *The separable witness beats kernel evaluations, not storage* (`sep_storage_bound`);
  the saving comes from a reader that combines two answers, which the oracle model does
  not charge for. And `Ksep` is free on NO class, so its lower bound needed the width-w
  support bound, not the orbit bound.
- *The per-kernel vs worst-case gap (⌈n/2⌉ vs n) is a nat-domain artefact*: g ≥ 0 acts
  as an inequality law that lets a zero sum pin both summands; at every kernel with
  g ≥ 1 the count is n.
- *Measured.* Blade attains the orbit count on every distinct-name row; the (A, B, A)
  row shows the compiler already takes the optimal by-name quotient and that its
  defect is purely the reindexing of the output. NumPy is dense; `dsyrk` writes the
  triangle. Python IS installed on this machine (`C:\Python314`); the `python3` alias is
  a Store stub.

## 4. Decisions

- D1 — OPEN, and now known to matter. Whether the compound identity group
  (`SymIdx<g, I₁ × … × I_s>`, §8.4 step 3 at `s > 1`) is modelled by flattening to one
  record over the product extent (the compiler's `IR.fuseJointSLevels`) or by a new
  record kind in the index universe. NEITHER is built: the typing rule quotients only
  groups of rank-1 operands and leaves every other group dense, so the typed orbit work
  is above the free-model optimum there (`BladeTransport.d1_not_built_pin`, 16 vs 10).
  Flattening keeps the object as it is; a new record kind changes `BladeDMWF.IxRec`.
  Recommendation: flattening, since it is what the compiler does and the object's
  transversal theorem already covers any record.
- D2 — RESOLVED by item 6: values stay `nat` as the carrier; what varies is the
  declared relation R on them (`BladeLicence`). A generic carrier with decidable
  equality remains a mechanical generalization (the position-permutation route) if ever
  wanted.
- D3 — RESOLVED by item 5: built on this calculus and the one object
  (`BladeInduction.rec_arr` is the object an `ERec` would denote); not on
  `BladeReduceCompiler`.
- D4 — RESOLVED 2026-10-12 as the paper plan's D9 (= E14): the compiler now implements
  formalism §8.3 — a non-adjacent repeat is stored dense in operand order and earns a
  BL4010 advisory naming the adjacent spelling (`src/IRLoopStructure.fs`
  `rawAxisGroups.mergesWith.acrossArray` and `fuseJointSLevels.hasIdentityPartner`
  require adjacency; `tests/corpus/symmetry/077`–`079`). The calculus's `out_shape`
  and the compiler agree again (`aba_unlicensed`). The by-name quotient is the optimal
  count (`BladeAttain.aba_orbit_count_forced`); presenting it in SOURCE axis order needs
  a position-permuted storage layout and stays open as an optimization, the same open
  piece as item 4's "general split classes".
