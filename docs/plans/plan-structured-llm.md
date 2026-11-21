# A structure-first language model: group-polymorphic attention, adaptive-order moments, perceptual symmetry

Status: DESIGN 2026-11-13, extended 2026-11-14 with section 7 (perceptual modalities) --
nothing built in the compiler. Five witnesses (section 9: four programs that run and two
refusals) are checked by `blade test docs`. The first two were run on the wave-7 master
tree (`cf43784a`) in the interpreter and in the C++ lane, which agree to every printed
digit; all five pass the doc test on a build of master `1e6a871a` (2026-11-14).
The architecture's hypothesis is UNTESTED: P1 (section 11) is an experiment OUTSIDE
Blade, and it gates every compiler item in this plan. Origin: a design conversation
(2026-11-01 to 2026-11-14). Each literature figure is marked `checked` (confirmed against
a search summary on 2026-11-02, 2026-11-13 or 2026-11-14, not against the paper text) or
`recalled` (not re-read) in section 13.

## 0. The pitch

Four ideas, in the order they depend on each other.

1. **A transformer is already a Blade program.** Axes are index types, attention is a
   fold, the causal mask is an index domain, decoding and training are recursive arrays,
   expert routing is `group_by`. Nothing here needs a kernel library: the compiler owns
   the blocking a hand-written attention kernel implements (section 3).
2. **Positional structure is a group acting on token coordinates.** Attention logits must
   be invariant under that action and values must transform covariantly. Choosing the
   group classifies the positional encodings the field uses, and makes the choice a type
   (section 4).
3. **Spend higher order where pairs fail.** Third and fourth moments carry composition,
   analogy and sense structure, but they are too large to form over all tokens. A pairwise
   layer already yields, as a by-product, an estimate of where it is failing; a fixed
   budget of higher-order terms goes there and nowhere else (sections 5 and 6).
4. **The symmetry is where language meets arrays.** Text alone has few exact symmetries.
   Gridded and volumetric data have many, known in advance, and so do the quantities they
   hold. The gain concentrates in models that read both, and the group theory that matters
   is in the coupling: grounding becomes a selection rule (section 7).

What this is not: a route to training a frontier model. Blade has no half-precision
width, its GPU lane is narrow, and its reverse-mode AD refuses the attention folds
(section 10). The honest target is a research-grade model whose kernels are derived, whose
symmetries are certificates, and whose execution is an auditable plan.

## 1. Claims and non-goals

The claims are of three kinds and must not be averaged.

| Kind | Examples | What it is worth |
|---|---|---|
| Derived, not better | linear-memory attention, causal and banded block skipping, relative positions | parity with a well-engineered baseline, from a few lines of folds and types; an engineering saving, not a benchmark gain |
| Exact properties | bitwise-reproducible inference by declaration; zero order bias over a certified axis; a declared symmetric relation that cannot be asymmetric; certified equivariance of a geometric branch; an answer that swaps left and right when the input is mirrored; answers that carry checked units | guarantees, not percentages; decisive for evaluation, science and audit, modest for a chatbot |
| Speculative | higher-order terms improving language quality; a gate that finds where they are needed | unknown; P1 decides |

Non-goals: frontier-scale training; a tokenizer; a claim to a novel FlashAttention
([structural/03](structural/03-streaming-reductions.md) already declines that claim); any
compiler work before P1 reports.

Terms coined here, used with these meanings only: the **gate** (the per-token score that
allocates order), the **dispersion** of an attention row (section 6.2), the **connected
term** (the third-order correction of section 6.4) and its **connected mass**, a
**representation-typed word** (section 7.4), an **orbit stencil** (section 7.5) and the
**subgroup gate** (section 7.6).

## 2. What the repo already has

Every row was checked against the tree on 2026-11-13; the last seven on 2026-11-14.

| Piece | Witness | State |
|---|---|---|
| Online-softmax merge as a tuple-state fold; the two-pass softmax as a max fold plus a `<&!>` reduction join | [examples/10](../../examples/10_pairwise_normalized_sums.blade), corpus [loops/200](../../tests/corpus/loops/200_per_row_reduction_join_streams.blade), [loops/201](../../tests/corpus/loops/201_tuple_state_online_fold.blade) | runs in both lanes; the score there is a toy distance kernel, not a learned dot product |
| Constrained index domains enumerated in closed form, no mask | corpus [domains/004](../../tests/corpus/domains/004_strict_triangle_sum.blade) (`i < j`), [index-types/489](../../tests/corpus/index-types/489_domain_lower_bound_with_other_constraints.blade) (a `j <= i` conjunct); [structural/06](structural/06-enumerable-domains.md); formalism 3.5 | built |
| Budgeted recurrence with a `while` guard | corpus [recursive-arrays/013](../../tests/corpus/recursive-arrays/013_while_guard_budget_aborts.blade); formalism 7.5 | built; BL8010 when the budget ends with the guard true; not reverse-differentiable |
| Training loop as a `let rec` over an epoch axis, `ad.grad` inside the step | corpus [ml-e2e/001](../../tests/corpus/ml-e2e/001_invariant_regression_training.blade), [ml-e2e/002](../../tests/corpus/ml-e2e/002_ops_elaboration_training.blade) | built |
| Equivariant module: `IrrepsIdx<spec>`, Clebsch-Gordan tables, `tensor_product` / `linear` / `gated`, `ml.derive_poly<k>` (k <= 4), `grad` through all of it | [features/equivariant-nn.md](../features/equivariant-nn.md) | built; **attention is open item 2 of its section 12** |
| Certificates `where ml.equiv(G)`, `ml.galilean`, `ml.perm_equiv(N)` | corpus [ml-equiv/001](../../tests/corpus/ml-equiv/001_certified_identity.blade), [ml-equiv/039](../../tests/corpus/ml-equiv/039_perm_certified.blade); `src/ml/compiler/MLPerm.fs` | built; a `reduce` over a node-covariant value is refused (BL4012: the combiner's commutativity is not checked) |
| Group averaging as a primitive | `reynolds(g)`, formalism 5.3; [literature survey](../blade_literature_survey.md) ("frame averaging is the Reynolds operator") | built for scalar lambdas |
| Rank-3 symmetric comoments: `where comm(a, b, c)` over `prodsum(a, b, c)`, packed simplex storage and tile | [plan-paper-1-comoment.md](plan-paper-1-comoment.md); [plan-rank-r-former.md](plan-rank-r-former.md) (design) | rank 3 built |
| Matrix-free Gram action `gram_apply(A, B, x)` | formalism 6.4; [structural/05](structural/05-structured-operators.md) | built, differentiable |
| Dispatch by key | `group_keys` / `group_by`, corpus [sql-group-by/001](../../tests/corpus/sql-group-by/001_groupby_idx_first.blade); [features/sql.md](../features/sql.md) | built |
| Argsort | `sort(range<I>, key)`, corpus `sql-sort/007` | built, C++ lane only |
| Address-indexed draws | `r.uniform_at(key, stream, offset, n)`, corpus [rand/019](../../tests/corpus/rand/019_indexed_uniform_addressed_samples.blade) | built |
| Reproducibility contract | `where repro`, `BLADE_FP_REPRO=1 BLADE_FP_CONTRACT=off`; formalism 2.4 | built |
| Index provenance | `Nat<Lat>` rejects `Nat<Lon>` at equal extent, corpus [index-types/133](../../tests/corpus/index-types/133_nat_named_alias_mismatch_rejects.blade) (BL3001); formalism 3.10 | built |
| Reverse-mode AD | `src/GradExpand.fs`; features/equivariant-nn.md section 11 | v1 subset: refuses `if`/`else`, `match`, tuple values, lambda fold kernels, `while`-guarded recurrences |
| Point-group equivariance on a grid: `ml.derive_pg_linear`, `PgIrrepsIdx<GROUP, spec>` | corpus [ml-equiv/045](../../tests/corpus/ml-equiv/045_pg_certified.blade) | built for C4 and D4 only |
| Spherical harmonics `ml.y_to` | features/equivariant-nn.md | built to degree 2; the recurrence for higher degree is pending |
| Galilean certificate and subgrid closures | corpus [ml-equiv/024](../../tests/corpus/ml-equiv/024_galilean_closure.blade); [examples/07](../../examples/07_subgrid_closure_discovery.blade), [examples/08](../../examples/08_burgers_les.blade) | built |
| Units as a checked discipline; a transcendental only on a dimensionless value | BL3006, corpus [unit-errors/017](../../tests/corpus/unit-errors/017_exp_complex_dimensioned.blade) | built |
| Clebsch-Gordan selection rules as a dependent record and a counted domain; static sizing `ml.hom_dim`, `ml.tp_spec` | [examples/06](../../examples/06_cg_selection_rules.blade); corpus [ml-ops/011](../../tests/corpus/ml-ops/011_schur_no_map.blade) (BL4007) | built |
| Two-axis halo stencils | corpus [loops/474](../../tests/corpus/loops/474_hot_row_fold_behind_halo_producer.blade) | built |
| Gridded, crystal and molecule worked examples | [examples/09](../../examples/09_qg_atmosphere.blade); `examples/cod_crystals.bladenb`, `examples/aspirin_energy.bladenb` | exist |

## 3. The base transformer in Blade idioms

- **Axes are types with provenance.** `Seq`, `Model`, `Head`, `HeadDim`, `Vocab`. A
  sequence position in a model-dimension subscript is BL3001 even at equal extents. A
  softmax over the wrong axis does not typecheck: `reduce` peels the innermost axis, and
  the axis is in the type. Dimensional currying makes `Q(h)` the per-head matrix with no
  reshape or view API.
- **Attention is a fold.** State `(m, l, o)` -- running max, normalizer, weighted value --
  merged by rescaling both sides to the larger max. It is the recurrence linear-memory
  attention kernels are built on, written as the left fold of examples/10.
- **The causal mask is a domain.** `static struct Causal { i, j } where j <= i` enumerates
  the triangle as an affine nest; a sliding window is the band `abs(i - j) <= w`, which a
  licensed fold walks by diagonal. No mask array, no wasted half.
- **Decoding is `let rec` on the token axis.** The inductive arm with a `while` guard over
  a budget extent is the decode loop; the trajectory is the cache; reads past the prefix
  are zero, so causality is structural. The guard must include the token limit, since an
  exhausted budget with the guard still true is BL8010, not truncation.
- **Training is `let rec` over an epoch axis**, as ml-e2e/002 does today.
- **Expert routing is `group_by`** on an expert key; top-k selection is an argsort.
- **Sampling is address-indexed**, so a decode's randomness is a function of key, stream
  and position, independent of batching.
- **`blade plan` is the model card.** Every kernel decision is recorded with its legality
  and cost.

```blade sketch
type Seq = Idx<2048>
type HeadDim = Idx<64>

static struct Causal {
    i: Int32<min=0, max=2047>,
    j: Int32<min=0, max=2047>
} where j <= i

// One row of causal attention: the tuple-state fold of examples/10 over the
// row's admissible j. `score` is the learned bilinear pairing of q(i) and k(j).
function attend_row(i: Nat<Seq>) -> Array<Float64 like HeadDim> = {
    let s = reduce(method_for(row_of<Causal>(i)) <@> lambda(j) -> (score(i, j), 1.0, v(j)),
                   online_softmax_merge, (-1.0e300, 0.0, zero_value))
    s[2] / s[1]
}

// Decode until end-of-sequence or the token limit, inside a static budget.
let rec toks: Array<Int64 like Budget> =
    match toks with
    | zero -> zero
    | zero :: s -> zero :: bos
    | prefix :: n while prefix(n - 1) != eos && n < limit -> prefix :: sample(prefix, n)
```

`row_of<Causal>(i)` (one row of a domain as a range) and a tuple state with an array
member are not verified spellings; P0 finds out what the surface admits.

## 4. Group-polymorphic attention

### 4.1 Definition

Each token has a content vector `h` and a coordinate `c` in a coordinate type that carries
a group action. Attention is one definition: logits are a pairing
`kappa(c_i, c_j, h_i, h_j)` that is INVARIANT (`kappa(g c_i, g c_j, ..) = kappa(c_i, c_j, ..)`),
weights are a softmax over `j`, the output is the weighted sum of COVARIANT values. The
group decides what `kappa` may be.

### 4.2 The classification

| Group on coordinates | Admissible pairing | Known as |
|---|---|---|
| trivial (position is content) | anything | learned absolute positions |
| translations of the line | `f(p_i - p_j, h_i, h_j)` | relative position encodings |
| translations, pairing bilinear in `h` | the character basis: query and key pairs rotated by `theta * p` | RoPE |
| translations, pairing an additive scalar | `g(p_i - p_j)` | ALiBi, relative-position buckets |
| translations of the plane | two-dimensional characters | axial / 2-D RoPE |
| the symmetric group on the axis | no dependence on position | set attention, no positional encoding |
| E(3) on point coordinates | distance, spherical harmonics of the direction, Clebsch-Gordan coupling of `h_i` with `h_j` | SE(3)-Transformer, Equiformer |

### 4.3 Two consequences

**RoPE is derived, not designed.** A translation-invariant bilinear form on per-token
features is a convolution in position, and a convolution is diagonal in the Fourier basis
of the translation group. That diagonal form is the rotation of query and key pairs.
Bochner's theorem makes the invariant positive-definite kernels the Fourier transforms of
positive measures: the rotation frequencies are the support of a spectral measure, and
length extrapolation is a question about where that measure puts mass. The compiler could
derive the encoding from the declaration the way it derives triangular storage from
`comm`. For SO(3) the same derivation goes through the Clebsch-Gordan tables the compiler
already owns; for the symmetric group the realization is "drop the coordinate".

**The causal relation is typed.** `j <= i` is translation-invariant, so causal attention
with relative positions is well typed. It is not permutation-invariant, so a causal mask
over an axis certified `ml.perm_equiv` is a contradiction the checker should refuse.

Product groups give the multimodal cases: text positions times E(3) for a molecule's
atoms; text positions inside each retrieved document times the symmetric group over the
documents, which is a wreath product -- the algebra of Blade's wreath index types.

### 4.4 What symmetry in text means

Three layers, mapped onto three mechanisms the language already separates.

| Layer | Examples | Mechanism |
|---|---|---|
| Exact, in the context's STRUCTURE | translation of a stream; permutation of retrieved documents, few-shot examples, answer options, table rows, tool definitions; a relation declared symmetric; reflection for a bidirectional encoder | certificates and types |
| Approximate, in the CONTENT | paraphrase, case, commutative conjunction, synonymy | `reynolds(g)`: group averaging over a declared subgroup, never a certificate |
| To be discovered | which spans are order-free, which relations commute | [plan-latent-symmetry-learning.md](plan-latent-symmetry-learning.md): infer generators, then promote them to a declaration |

Two readings worth keeping. A decode step that reads only `prefix(n - c)` and never `n`
is an autonomous recurrence, which IS translation equivariance, and it is a syntactic
check. The reversal failure (a model trained on "A is B's sibling" failing "B is A's
sibling") is a missing `comm` on a relation: a pair kernel with `where comm(a, b)` scores
both directions equally by construction, but only for facts routed through a typed
relation store, not for free text.

Not symmetries, though they look like them: token relabeling (a gauge freedom of the
embedding table), hidden-unit permutation (a gauge freedom of the weights), and length
generalization (an inclusion of one translation group in a larger one).

### 4.5 What must be built

```blade sketch
// Not a current spelling: a coordinate type that carries its group action.
type Pos = Idx<2048> with Translation
function attend(q: Array<T like Pos, D>, k: Array<T like Pos, D>, v: Array<T like Pos, D>)
    where ml.equiv(Translation(Pos)) -> Array<T like Pos, D> = ...
```

- An admission rule for a NORMALIZATION over an invariant logit axis in the permutation
  checker. Today a `reduce` over a node-covariant value is BL4012, which refuses every
  softmax.
- Equivariant attention as an elaborated op: logits from the scalar channel of
  `tensor_product(q_i, k_j)` (invariant by Schur's lemma), values equivariant, the output
  irreps deduced or BL4007. Closes open item 2 of features/equivariant-nn.md section 12.
- Coordinate types that carry an action, starting with translations (derive the rotary
  form) and the symmetric group (drop the coordinate). This is the largest design item and
  needs its own plan.

## 5. What third and fourth moments mean

A cumulant of order k measures exactly what no combination of lower orders explains.
Order 2 is the Gaussian world: a word embedding factorizes a pairwise co-occurrence
matrix, and an attention logit is a bilinear pairing of two tokens. Orders 3 and 4 are the
first irreducibly non-pairwise structure -- the connected three- and four-point functions.

### 5.1 Across tokens

| Order | Encodes | Reading |
|---|---|---|
| 2 | a relation between two tokens | attention, co-occurrence, similarity |
| 3, path-shaped | two relations composed through a middle term | multi-hop inference, transitivity |
| 3, symmetric | an unordered triple that belongs together | coreference clusters, coordinated lists, hyperedges |
| 3, signed cycle | the product of pairwise signs around a triangle | consistency: A agrees with B, B with C, A contradicts C |
| 3, cumulant | joint occurrence that pairs mispredict | polysemy: "bank" pairs with "river" and with "loan", the triple almost never occurs |
| 4 | a relation between two relations | analogy and parallel structure; the in-context copying pattern "A B ... A, then B" |
| 4, cumulant | heavy tails | burstiness; which directions are real features |

With the translation group on positions the ladder is spectral: order 2 is the power
spectrum, blind to arrangement; order 3 is the bispectrum with the selection rule
`k1 + k2 + k3 = 0`, which keeps arrangement up to a shift. That is the object of
[plan-cosmology-nongaussianity.md](plan-cosmology-nongaussianity.md), and the selection
rule is a linear-conjunct domain Blade enumerates in closed form.

### 5.2 Across features

Take the embeddings of a context as a sample. The mean is the topic centroid. The
covariance says which features vary together and cannot tell a direction from its
negation. The third cumulant is odd under a sign flip: it carries polarity, markedness,
and asymmetric relations such as entailment. The fourth cumulant is sparsity: independent
component analysis uses kurtosis to fix the feature directions covariance leaves rotated,
which is the statistical core of dictionary learning for interpretability. It also
separates ambiguity (two discrete senses, a bimodal spread, low kurtosis) from vagueness
(one broad mode); variance cannot.

### 5.3 Order trades against depth

Pairwise layers synthesize higher orders through depth, so native higher orders add no
expressivity in the limit. They change the cost of reaching a given order. Evidence:

- A single layer of standard attention cannot solve a triple-matching task unless its
  width grows polynomially in the sequence length; one unit of third-order attention
  solves it (Sanford, Hsu, Telgarsky; `checked`). The multi-layer lower bound is a
  conjecture of that paper, not a theorem.
- In equivariant chemistry, four-body messages reduce the message-passing iterations
  needed from five or six to two (MACE; `checked`).
- Topic models, mixtures and hidden Markov models are estimated by decomposing a symmetric
  tensor built from second- and third-order moments (Anandkumar et al.; `checked` for the
  method, the identifiability conditions not re-read).

### 5.4 Cost

| Axis | Extent | Symmetric order 3 | Symmetric order 4 |
|---|---|---|---|
| all tokens | 2048 | 1,433,753,600 cells | about 7.3e11 |
| features of one head | 64 | 45,760 | 766,480 |
| retrieved documents | 20 | 1,540 | 8,855 |

Symmetric storage divides by 6 and 24 and does not rescue the token axis. The affordable
placements are small axes -- features, entity or latent slots, documents, banded windows --
and the adaptive allocation of section 6.

## 6. The adaptive-order layer

### 6.1 The first layer is a gate on the complexity of a token's meaning

A pairwise layer is run for its usual output AND for an estimate of where it fails. A
token's meaning can be complex for two different reasons, which want different treatment.

- **Valence.** Some tokens mean a relation among other tokens. "Between" is ternary;
  "than" and "respectively" relate two pairs; negation and quantifiers take scope. This is
  mostly a property of the word (valency, semantic type order), so it is a learned prior on
  the embedding that costs nothing at run time.
- **Ambiguity.** Other tokens are simple in principle and unresolved in this context.
  "Bank" beside "river" needs no help; "bank" pulled two ways does. This is contextual and
  is what the dispersion measures.

The gate is `valence prior + alpha * dispersion`, restricted to bimodal rows (6.2). It can
run in a narrow projection: a gate needs a reliable estimate, not a good representation.
It must sit after a local merge step or on word-level units, since complexity of meaning
is not defined on subword fragments.

### 6.2 The dispersion is free

With unit-normalized values `h` and one row of attention weights `a`,

    dispersion(i) = 1 - | sum_j a_ij h_j |^2 = sum_jk a_ij a_ik (1 - s_jk)

It is one minus the squared length of the attention output: consistent neighbours add to a
long vector, conflicting ones cancel. It is the expected dissimilarity of two neighbours
drawn from the row, and in Blade it is `1.0 - prodsum(a, gram_apply(h, h, a))` -- no
pairwise array (witness 9.1).

A diffuse row also scores high, and higher order does not help it. The case worth the
budget is a neighbourhood that splits into two coherent clusters: the attended set's
weighted covariance has one dominant direction (a few power-iteration steps on the
candidates only), where a diffuse row is isotropic.

### 6.3 Allocation: fixed budgets, a centre plus a pair

Top `M3` tokens by gate score; for each, its top `P` neighbours by attention weight; the
third-order object is over UNORDERED PAIRS of those neighbours -- does the pair `(j, k)`
jointly fit the centre `i`.

| Choice | Triples |
|---|---|
| all tokens, 2048, fully symmetric | 1,433,753,600 |
| `M3 = 64` centres, `P = 32` neighbours, pairs `j < k` | 31,744 |
| order 4: `M4 = 16` centres, `P = 16`, triples `j < k < l` | 8,960 |

Two consequences for Blade. Fixed budgets keep every extent static, which is what the
tiles and fusions want; a threshold would give run-time extents and lose them. And the
symmetry is in the neighbour pair only (a factor of 2, not 6); the full threefold symmetry
applies only to a tensor among the selected tokens themselves, which answers a different
question (which confused tokens resolve each other).

An ambiguity token wants a symmetric pair kernel (`comm`); a valence token binds its
arguments in roles, symmetric for "between Y and Z" and not for "give X to Y" (ordered
pairs, two key projections, no `comm`). The storage and walk differ by declaration.

### 6.4 The higher-order term is a correction to the pairwise prediction

For a selected centre `i` and pairs `j < k` of its neighbours, with `s` the pairwise logits:

    base(i; j, k)  = s_ij + s_ik
    joint(i; j, k) = s_ij + s_ik + gamma * tau_i(j, k)
    tau_i(j, k)    = sum_f q_i[f] k_j[f] k_k[f]              -- prodsum(q, k_j, k_k)

    b_i = softmax over pairs of base        -- two distinct neighbours drawn independently
    w_i = softmax over pairs of joint
    delta_i = sum_{j<k} (w_i(j, k) - b_i(j, k)) * u(v_j, v_k)
    x_i <- x_i + o_i + g_i * delta_i

`b_i` is exactly what the pairwise layer predicts for a pair, so `delta_i` is the
connected part: joint minus pair-explained. When `gamma = 0`, or `tau` is constant over
the pairs, `delta_i` is exactly zero and the layer IS pairwise attention. The trilinear
form is the comoment kernel. The pairwise layer is therefore both the lowest-order term of
the expansion and the estimate of where the expansion fails; discarding its output would
discard the baseline the correction is defined against.

The **connected mass** `D_i = 1/2 sum_{j<k} |w_i - b_i|` is the total-variation distance
between the joint and the independent pair distributions (witness 9.2). It is a
diagnostic, and the natural gate for the next order: order `k + 1` is allocated where the
order-`k` correction is large and still unresolved. The whole is a perturbation series
with adaptive truncation.

Each centre's term is a fold over the `j < k` domain of its neighbour slots, so NO triple
tensor is stored: the online-softmax fold of section 3 over a triangle domain.

### 6.5 The layer

```blade sketch
// Per block. M3 and P are static budgets, so every extent below is static.
// The function names are placeholders for elaborated ops, not current spellings.
let a      = attention_rows(x)                                   // pairwise weights
let o      = attend_values(a, x)                                 // the order-2 term
let score  = valence(x) + alpha * dispersion(a, x)               // the gate
let chosen = top(score, M3)                                      // Idx<M3> of positions; constant under AD
let nbrs   = method_for(chosen) <@> lambda(i) -> top(a(i), P)    // Idx<M3> by Idx<P>
let delta  = method_for(chosen) <@> lambda(i) -> connected_term(x, i, nbrs)
let x_next = x + o + scatter(chosen, score * delta)
```

- **Dispatch** is the mixture-of-experts shape: bucket tokens by allocated order with
  fixed bucket sizes, run each bucket's kernel.
- **Re-select at every block.** Confusion after the first layer is lexical (polysemy,
  pronouns, prepositions); compositional confusion appears only after context has mixed.
- **Training.** The selected indices are constants; gradients flow through the gathered
  values (a gather's adjoint is a scatter, which reverse mode has). The gate learns
  because its score multiplies the correction, as the router weight does in
  mixture-of-depths (`checked`).
- **The gate is a plan record.** Its output is a per-token map of where the model believes
  the text is hard: the run-time analogue of `blade plan`.

### 6.6 Known weaknesses

- **No pairwise footprint.** A third cumulant is by definition what pairs do not explain,
  so a pairwise screen is not a sound bound on it: three things pairwise unrelated and
  jointly dependent are invisible to the dispersion. Mitigations, not proofs: natural
  language rarely has dependencies with no pairwise trace; the embeddings are trained end
  to end and can learn to expose "this token needs triples"; a small learned head takes
  the dispersion as an input; a few randomly chosen tokens join the budget. P1/E2 measures
  the residual.
- **Top-k is not causal.** Selection across positions looks at tokens that do not exist
  yet during decoding, and a token's status may change as the context grows, which breaks
  cache reuse. Mixture-of-depths met the same problem and trained an auxiliary predictor
  on each token's own embedding (`checked`). The alternative here is a quota per window of
  positions: static extents, causal at window granularity. Open (section 12).
- **Reverse mode refuses the attention folds today** (section 10).

## 7. Perceptual modalities: where the symmetry is

### 7.1 The claim

Text alone has few exact symmetries: section 4.4 found them in the structure of the
context, and everything in the content approximate or learned. Gridded and volumetric data
are the opposite case. Their groups are exact and known in advance, and so are the groups
of the quantities they hold. So the design's value concentrates in models that read BOTH:
language supplies the question and the form of the answer, the arrays supply the groups.

The group theory that matters is in the coupling. Each modality has its own group, and a
cross-modal map must commute with the JOINT action:

    f(g . array, g . text) = g . f(array, text)

A question that names a place ("the top left") breaks the array's symmetry on its own. The
pair is still equivariant, because turning the array and rewriting the question gives the
turned answer. The certificate therefore sits on the two-argument function, and grounding
becomes a selection rule: which words may bind to which features is decided by their
representations, not learned.

### 7.2 The inventory

| Where | Group | Blade today |
|---|---|---|
| Overhead or microscope images | translations, rotations and flips of the grid | two-axis halo stencils; C4 and D4 shipped |
| Volumes | translations, the octahedral group of the grid, O(3) on features | O(3) and SO(3) certificates; harmonics to degree 2 |
| Global fields on the sphere | SO(3), cut to rotation about the axis by the planet's spin | spherical harmonics are the irreps; higher degree pending |
| Fluid fields | Galilean boosts | `where ml.galilean`, with the subgrid-closure examples |
| Any measured quantity | rescaling of units | the unit system (7.3) |
| Tensor-valued data | permutations of the tensor's own indices | `SymIdx`, `AntisymIdx`, wreath index types |
| Sets of images, series, documents | the symmetric group, wreathed with the group inside each member | `where ml.perm_equiv` |
| The scene | restriction to a subgroup | `ml.restrict`, deferred |

The group belongs to the data source, not to the modality. Gravity breaks rotation in
natural photographs; overhead, microscope and volumetric data keep it. A planet's spin
cuts SO(3) to rotations about its axis, and topography breaks the rest. A symmetry-breaking
field is an INPUT that transforms with the data, never a reason to drop the certificate,
and a model needs a lattice of subgroups, not one group (7.6).

### 7.3 Units are a group

Rescaling units is an abelian group. A quantity's unit exponents are its representation
label, and they add under multiplication exactly as charges do. Three consequences:

- Unit checking IS a selection rule, of the same kind as the Clebsch-Gordan rule.
- "A nonlinearity may act only on an invariant" is already enforced on both sides: BL3006
  refuses a transcendental of a dimensioned value (witness 9.5), and `ml.gated` applies
  its nonlinearity to scalars and only scales the other blocks by them.
- The dimensionless products of the Buckingham Pi theorem are the invariants of the
  scaling group. Prior work learns them with a penalty term, because its architecture
  alone does not guarantee them (`checked`); a layer whose inputs carry unit types
  guarantees them by construction.

Blade runs both disciplines today and has not treated them as one.

### 7.4 Words carry representation content

| Kind of word | Examples | Under the perceptual group | How it binds |
|---|---|---|---|
| invariant | near, three, red, larger | a scalar | to scalar features, norms and dot products |
| covariant | left, above, north (vectors); clockwise, left-handed (pseudoscalars); elongated (rank 2) | a non-trivial representation | to features of its own representation, through Clebsch-Gordan paths |
| operator | rotate, mirror, zoom, earlier | a group element | applied exactly as the representation matrix; nothing is learned |

A frame word ("my left", "the driver's left") selects the group element relating two
frames. The representation joins the valence prior of section 6.1: a token's static type
is its order, its representation and its units.

The selection rule is computable today (witness 9.3). A scalar word has NO linear map from
a vector feature, and writing one is BL4007. A vector word has exactly one. A pair of
vectors reaches a scalar through the dot product. And in three dimensions a pseudoscalar
word is unreachable from any PAIR of vectors and reachable from a TRIPLE: handedness is
irreducibly third order. That is section 5's claim in geometric form, and the
adaptive-order layer is what supplies the triple. In the plane the antisymmetric pair
suffices, which is an `anticomm` kernel.

This is where current models fail. On a benchmark that varies only the spatial relation,
all 18 general vision-language models tested did poorly; one that nears human level on a
standard question-answering benchmark scored 56 percent against 99 for humans
(`checked`). Left-right confusion is also a recognized failure of generated radiology
reports (`checked`).

### 7.5 Orbit stencils

A convolution kernel that commutes with the grid's point group has its weights tied over
each orbit of the stencil offsets. The compiler can sum each orbit and multiply once: the
orbit-class fold applied to weights instead of data.

| Stencil | Group | Free weights |
|---|---|---|
| 3 x 3 | D4 | 3 of 9 |
| 5 x 5 | D4 | 6 of 25 |
| 3 x 3 x 3 | octahedral | 4 of 27 |
| 5 x 5 x 5 | octahedral | 10 of 125 |

Witness 9.4 writes the 3 x 3 case by hand: the tied kernel commutes with a quarter turn
exactly, an untied one of the same support does not. This covers scalar channels. Channels
in other representations need a steerable basis, the invariant subspace of the kernel
space, which is what `ml.derive_pg_linear` computes for a linear map today. The stencil
form is unbuilt.

### 7.6 The subgroup gate

A second gate beside the order gate of section 6. Over a small lattice of subgroups (for a
grid: none, one mirror, C4, D4) it picks the largest one the input respects, scored by an
equivariance error: how far the features move under the group. That is the analogue of
the dispersion. Each branch carries its own certificate, and the passage between branches
is restriction. The text may also declare the group ("an overhead view"). Speculative, and
it waits on `ml.restrict`.

### 7.7 Typed answers

An answer need not be text. A scalar is rendered as words. A vector or tensor is returned
with its units and transforms with the input. A tensor's storage class and its count of
independent components are deduced (`ml.hom_dim`), so a result that violates the declared
symmetry cannot be constructed.

### 7.8 What the third-order term means for arrays

The second-order statistics of an image are its power spectrum, which is texture.
Arrangement lives in phase, and the bispectrum is the lowest order that keeps it. In a
fluid the nonlinear term couples wavenumber triads under `k1 + k2 + k3 = 0`. So the
connected term of section 6.4 has a direct perceptual and physical reading (`recalled`).

### 7.9 Three applications

**Earth-system fields with language.** Reanalysis and model output on the sphere, queried
and described in text. Groups: rotation about the axis, time translation and the annual
cycle, Galilean boosts for closures, unit rescaling. Blade has the chunked Zarr and NetCDF
providers, units, the quasi-geostrophic and subgrid-closure examples, and the Galilean
certificate. The third-order term is the triad interaction itself. Spherical Fourier
neural operators already forecast equivariantly (`checked`); the addition here is typed
units, certified closures and the language coupling, not a better forecaster. Obstacle:
harmonics above degree 2.

**Crystals and molecules with literature.** Structure and paper text in, tensor properties
out. Groups: E(3), the crystal's point group, the index symmetries of the property tensor,
permutation of identical atoms, unit rescaling. MatTen decomposes the elasticity tensor
into irreducible pieces by hand, and cubic symmetry leaves three independent components
(`checked`); here the count is a static sizing builtin and the storage class is deduced.
The crystal and aspirin notebooks are a start. Obstacle: only C4 and D4 are shipped, and
restriction is deferred.

**Volumetric medical imaging with reports.** Groups: pose, voxel spacing as units,
permutation over series, and an approximate mirror symmetry whose ODD part is the signal.
The guarantee is laterality: mirror the scan and the report swaps left and right by
construction. Bitwise reproducibility and an auditable plan suit a regulated field, and
orbit stencils give small models for small datasets. Obstacles: three-dimensional orbit
stencils and the octahedral group are unbuilt, and clinical validation is outside a
compiler project.

### 7.10 Prior art, from one search each

An equivariant decoder on a pretrained image-text encoder exists (CLIPSym), and so does
canonicalization of a non-equivariant vision-language stream (Eq.Bot). Representation-typed
words were not found. This is not a literature review; P1/E4 should begin with one.

## 8. Expected gains

Against a competently engineered baseline, not a naive one.

| Property | Baseline | This design | Kind | Confidence |
|---|---|---|---|---|
| Attention memory | linear, hand-written kernel (2-4x faster and 10-20x less memory than quadratic at 2k-4k, the kernel authors' figures; `checked`) | linear, derived from the fold | derived | high that it is parity |
| Causal and banded work | block skipping | the domain type | derived | high |
| Symmetric pair tensors | dense | exactly 2x less | exact | high, where the structure is symmetric; a query-key product with distinct operands is not |
| Three-body terms | dense, rarely affordable | 6x less dense; four to five orders of magnitude less with the gate | exact on cost | benefit for language unknown |
| Reproducible inference | 80 distinct completions in 1000 runs at temperature 0; batch-invariant GPU kernels cost roughly a third to two thirds of throughput (`checked`, secondary summaries) | bitwise by `where repro`, the cost confined to functions that declare it | exact | high on the property |
| Order bias over document, option or tool axes | present (`checked` that it exists; the magnitudes are `recalled`: about 20 points mid-context on multi-document QA) | zero over a certified axis | exact | high that it vanishes; medium on the size, since frontier models have shrunk it and invariance discards order when order is informative |
| Reverse-direction facts | 33 versus 79 percent on real facts for one model (`checked`) | parity for relations declared `comm`, through a typed relation store only | exact | medium: needs a hybrid memory |
| Positional generalization | hand-chosen rotary frequencies | the same form, derived | derived | high that it is equal |
| Geometric modalities | non-equivariant branch | certified E(3) branch; up to three orders of magnitude less training data reported for interatomic potentials (`checked`) | exact + known | high |
| Compositional tasks (multi-hop, transitivity, analogy) | learned circuits over depth | single operations where the gate fires | speculative | P1/E1 |
| Spatial relations in grounded answers | all 18 general models tested did poorly on a benchmark that varies only the relation; a strong one scored 56 percent against 99 for humans (`checked`) | a spatial word binds only to features of its own representation; mirroring the input swaps left and right exactly | exact | high on the property; P1/E4 measures the accuracy |
| Tensor-valued answers | output symmetry enforced by a hand-built decomposition (MatTen; `checked`) | the storage class and the count of independent components are deduced | exact | high where the groups are shipped |
| Local kernels on a symmetric grid | 9 or 27 free weights per channel pair | 3 or 4, tied by orbit, one multiply per orbit | derived | high for scalar channels (witness 9.4) |
| General web-text perplexity | baseline | about the same | -- | high that it is small |
| Throughput per dollar today | half precision on GPUs | Float32 on CPUs | -- | high that Blade loses by a large factor |

## 9. Witnesses in today's Blade

No compiler change. Witnesses 9.1 and 9.2 use five unit vectors: tokens 0 and 1 form
one sense cluster, 2 and 3 another, token 4 sits between them. Witnesses 9.3 to 9.5
belong to section 7.

### 9.1 The gate score

```blade check
type Tok = Idx<5>
type Feat = Idx<3>

let h: Array<Float64 like Tok, Feat> = [[1.0, 0.0, 0.0],
                                         [0.8, 0.6, 0.0],
                                         [0.0, 0.0, 1.0],
                                         [0.0, 0.6, 0.8],
                                         [0.6, 0.0, 0.8]]
let beta: Float64 = 4.0

// One row: softmax weights over the tokens, then one minus the squared length
// of the attention output, through the Gram action (no Tok x Tok array).
function confusion(q: Array<Float64 like Feat>) -> Float64 = {
    let sc = method_for(h) <@> lambda(row) -> beta * prodsum(q, row) |> compute
    let m = reduce(sc, lambda(a, b) -> if a > b then a else b)
    let e = method_for(sc) <@> lambda(s) -> exp(s - m) |> compute
    let z = reduce(e, (+))
    let a = e / z
    1.0 - prodsum(a, gram_apply(h, h, a))
}

let gate = method_for(h) <@> lambda(row) -> confusion(row) |> compute
// EXPECT: gate = [0.189594190648359, 0.209615856955558, 0.165635428481316, 0.200782131602597, 0.276791586942083]
```

Token 4 scores highest. The differences are small because every row includes its own
token; a real gate would mask the diagonal and use learned projections.

### 9.2 The connected mass, streamed over a triangle domain

```blade cont
static struct Pair {
    j: Int32<min=0, max=4>,
    k: Int32<min=0, max=4>
} where j < k

let gamma: Float64 = 6.0

function pair_base(q: Array<Float64 like Feat>, hj: Array<Float64 like Feat>, hk: Array<Float64 like Feat>) -> Float64 =
    beta * (prodsum(q, hj) + prodsum(q, hk))

function pair_joint(q: Array<Float64 like Feat>, hj: Array<Float64 like Feat>, hk: Array<Float64 like Feat>) -> Float64 =
    pair_base(q, hj, hk) + gamma * prodsum(q, hj, hk)

// Total-variation distance between the joint pair distribution and the one the
// pairwise layer predicts. Three folds over the j < k domain; no pair array.
function connected(q: Array<Float64 like Feat>) -> Float64 = {
    let zb = reduce(method_for(range<Pair>) <@> lambda(j, k) -> exp(pair_base(q, h((j : Tok)), h((k : Tok)))), (+))
    let zw = reduce(method_for(range<Pair>) <@> lambda(j, k) -> exp(pair_joint(q, h((j : Tok)), h((k : Tok)))), (+))
    0.5 * reduce(method_for(range<Pair>) <@> lambda(j, k) ->
        abs(exp(pair_joint(q, h((j : Tok)), h((k : Tok)))) / zw - exp(pair_base(q, h((j : Tok)), h((k : Tok)))) / zb), (+))
}

let third = method_for(h) <@> lambda(row) -> connected(row) |> compute
// EXPECT: third = [0.297870732758093, 0.314735933872405, 0.152454142990608, 0.243332173929418, 0.352343398108597]
```

Token 4 has the largest connected mass as well. This witness uses every token as a
neighbour and is not the allocated form: selection (an argsort and a gather), a
vector-valued correction, and the one-pass join of the three folds are P0 items.

### 9.3 The grounding selection rule

```blade check
import ml as ml
// A spec is a list of (l, parity, multiplicity); parity 0 is even, 1 is odd.
let static NEAR = [(0, 0, 1)]
let static LEFT = [(1, 1, 1)]
let static TURN = [(0, 1, 1)]
let static VEC = [(1, 1, 1)]
let static PAIR = ml.tp_spec(VEC, VEC)
let static TRIPLE = ml.tp_spec(PAIR, VEC)

// The number of independent equivariant linear maps from the features to the word.
let static H_NEAR = ml.hom_dim(VEC, NEAR)
let static H_LEFT = ml.hom_dim(VEC, LEFT)
let static H_DOT = ml.hom_dim(PAIR, NEAR)
let static H_TURN2 = ml.hom_dim(PAIR, TURN)
let static H_TURN3 = ml.hom_dim(TRIPLE, TURN)
let near_from_vector = H_NEAR
let left_from_vector = H_LEFT
let near_from_pair = H_DOT
let turn_from_pair = H_TURN2
let turn_from_triple = H_TURN3
// EXPECT: near_from_vector = 0
// EXPECT: left_from_vector = 1
// EXPECT: near_from_pair = 1
// EXPECT: turn_from_pair = 0
// EXPECT: turn_from_triple = 1
```

`NEAR` is a scalar word, `LEFT` a vector word, `TURN` a handedness word, `VEC` a vector
feature of the array. A scalar word cannot bind to a vector feature; a vector word can;
two vectors reach a scalar; a handedness word needs three. The first case is a compile
error when written as a layer:

```blade rejects
import ml as ml
let static VEC = [(1, 1, 1)]
let static NEAR = [(0, 0, 1)]
let bad = ml.linear(VEC, NEAR, [0.0], [1.0, 2.0, 3.0])
// ERROR: BL4007
```

### 9.4 An orbit stencil

```blade check
type Y = Idx<7>
type X = Idx<7>
let u = method_for(range<Y, X>) <@> lambda(i, j) ->
    Float64(3 * Int64(i) * Int64(i) + 7 * Int64(j) + Int64(i) * Int64(j) * Int64(j)) |> compute

// Three weights for nine taps: one per orbit of the offsets under D4.
function tied(a: Array<Float64 like Y, X>) = {
    method_for(range<halo<Y, [-1, 0, 1]>, halo<X, [-1, 0, 1]>>) <@> lambda(wr, wc) ->
        2.0 * a(wr(0), wc(0))
        + 3.0 * (a(wr(-1), wc(0)) + a(wr(1), wc(0)) + a(wr(0), wc(-1)) + a(wr(0), wc(1)))
        + 5.0 * (a(wr(-1), wc(-1)) + a(wr(-1), wc(1)) + a(wr(1), wc(-1)) + a(wr(1), wc(1))) |> compute
}

// An untied kernel of the same support: one direction weighted differently.
function untied(a: Array<Float64 like Y, X>) = {
    method_for(range<halo<Y, [-1, 0, 1]>, halo<X, [-1, 0, 1]>>) <@> lambda(wr, wc) ->
        2.0 * a(wr(0), wc(0)) + 3.0 * a(wr(-1), wc(0)) + 4.0 * a(wr(1), wc(0)) |> compute
}

// A quarter turn of the grid.
let ru = method_for(range<Y, X>) <@> lambda(i, j) -> u((Int64(j) : Y), (6 - Int64(i) : X)) |> compute

let t_u = tied(u)
let t_ru = tied(ru)
let n_u = untied(u)
let n_ru = untied(ru)
let tied_error = reduce(method_for(0..5, 0..5) <@> lambda(i, j) -> abs(t_ru(i, j) - t_u(j, 4 - i)), (+), axes = 2)
let untied_error = reduce(method_for(0..5, 0..5) <@> lambda(i, j) -> abs(n_ru(i, j) - n_u(j, 4 - i)), (+), axes = 2)
// EXPECT: tied_error = 0.0
// EXPECT: untied_error = 1350.0
```

The tied kernel applied to the turned grid equals the turned result of the tied kernel,
cell for cell. The values are integers, so the zero is exact. The untied kernel fails the
same test. The tying is written by hand here; deducing it from a certificate is P6.

### 9.5 The unit rule

```blade rejects
Unit meters
let x: Float64<meters> = 2.0
let bad = exp(x)
// ERROR: BL3006
```

A transcendental is defined only on a dimensionless value. It is the scaling group's form
of "a nonlinearity acts only on invariants" (section 7.3).

## 10. Gaps

| Gap | Where | Blocks |
|---|---|---|
| Reverse mode refuses `if`/`else`, `match`, tuple values, lambda fold kernels (so the max fold and the tuple-state online softmax), `while`-guarded recurrences | `src/GradExpand.fs`; "remaining work" in features/equivariant-nn.md section 11 | training any attention written as section 3 writes it |
| Reverse rules not verified for: a fold over a `static struct` domain, three-operand `prodsum`, a gather by computed indices inside a kernel | to be probed in P0 | the connected term |
| No partial selection (top-k); argsort exists in the C++ lane only | corpus `sql-sort/007`; the LLVM lane has no sort arm | the allocation step in the LLVM lane |
| The permutation checker refuses every normalization over a node axis | `src/ml/compiler/MLPerm.fs` (BL4012) | certified set attention |
| Equivariant attention | features/equivariant-nn.md section 12, item 2 | the certified geometric head |
| Coordinate types carrying a group action | unbuilt; the transforms-as-types plan is retired into the equivariant module | group-polymorphic attention |
| A matrix-free third-order action (the analogue of `gram_apply`) | unbuilt | feature-axis cumulants on large axes |
| Dense centre axis followed by a symmetric pair group as STORED layout | not verified (packed sources today are a leading symmetric group plus dense trailing dims) | only if the triple tensor is ever stored; the fold form of 6.4 avoids it |
| Widths: Float32 and Float64 only; no half precision, no 8-bit; the Zarr reader refuses half precision | `src/Types.fs` | memory parity with production models |
| Scale: the `where cuda` lane is narrow and refuses `repro`; MPI replicates storage | `src/CodeGenCuda.fs`; [plan-distributed-memory.md](plan-distributed-memory.md) | training beyond CPU scale |
| Dynamic lengths: named `Idx` and the `let rec` axis are static; no attention over a ragged batch | formalism 3.10, 7.5 | variable-length batches |
| Spherical harmonics above degree 2 | `ml.y_to`; the recurrence is pending | global fields at useful resolution |
| Point groups beyond C4 and D4 (the crystal classes, the octahedral group), space groups, and restriction `ml.restrict` | features/equivariant-nn.md, deferred | crystals; volumetric grids; the subgroup gate |
| Orbit stencils as a construct: weights tied by orbit from a certificate, and steerable bases for non-scalar channels | unbuilt; witness 9.4 writes the tying by hand | the perceptual front end |
| A representation type on vocabulary entries; operator words applied as representation matrices | unbuilt | typed grounding |
| Unit exponents as weights of the scaling group, in one checker with the irreps discipline | two separate checkers today | dimensionally consistent layers by type |
| Embedding an array into tokens; rendering typed numeric outputs into text | unbuilt | any multimodal model |

## 11. Phases

**P0 -- forward witnesses in today's Blade (no compiler change).** Extend section 9 to:
a causal single-head row as the online-softmax fold over `j <= i`; the allocation step
(argsort, take the first `M3`, gather); a vector-valued connected term; the three folds as
one reduction join. Done: each runs in both lanes and under `blade test interp`; every
refusal met is added to section 10 with its diagnostic code. Probe the unverified reverse
rules with `ad.grad` and record the refusals. Cost: days. Section 7 adds a
three-dimensional orbit stencil written by hand and a typed answer: a vector with units
that turns with its input. Witnesses 9.3 to 9.5 are done.

**P1 -- the experiment, outside Blade (the gate on everything below).** Small models,
matched parameters and matched FLOPs, in any framework with reverse mode.

- E1, order for depth. Triple matching, two-hop relation composition and four-term
  analogy, synthetic. An L-layer pairwise model against an (L - 1)-layer model with the
  adaptive third-order layer, and against a DENSE third-order layer on a short context
  (the upper bound; its gap to the adaptive layer is the gate's regret).
- E2, gate validity. On the dense model, the rank correlation between the gate score and
  the norm of the dense correction per token; and a parity-style control with no pairwise
  footprint, to measure what the gate misses.
- E3, certified context. A permutation-invariant document axis against a vanilla model on
  synthetic multi-document question answering with the gold document's position shuffled.
  The certified model's order sensitivity must be exactly zero; the question is accuracy
  at equal training.
- E4, typed spatial words. Synthetic images with one or two objects, and questions that
  use left, right, above, below and a handedness word. A model whose spatial words
  carry a representation type and bind through the selection rule of section 7.4,
  against an untyped twin. Mirroring or turning the input must transform the typed
  model's answer exactly. The questions are accuracy and sample efficiency at equal
  training, and whether the handedness word needs the third-order term in three
  dimensions, as witness 9.3 predicts.

Stop rules (thresholds PROVISIONAL, to be fixed before the runs): no E1 gain over the
depth- and FLOP-matched baseline on any of the three task families ends the adaptive-order
line (sections 4 and 7 stand on their own). An E2 rank correlation below 0.5 sends the gate to a
learned head; if that also fails, allocation is dead and higher order is kept to small
axes only. No E4 gain in accuracy or sample efficiency leaves the typed vocabulary as a
guarantee only: the exact transformation property stands and the learning claim is
dropped. Results are recorded in this section.

**P2 -- reverse-mode prerequisites.** `if`/`else` and tuple-valued fold state in reverse;
the max fold; whatever P0's probes refused. Scheduled only if P1 passes.

**P3 -- equivariant attention.** The normalization admission rule in the permutation
checker; the elaborated equivariant head. Independent of P1's adaptive-order result:
it closes an existing open item and E3 alone justifies it.

**P4 -- group-polymorphic surface.** Its own plan; translations and the symmetric group
first.

**P5 -- the adaptive-order layer as an elaborated op**, with a `blade plan` record of the
budgets, the storage it did not allocate, and each lane's form.

**P6 -- the perceptual front end.** Orbit stencils as a construct, the remaining point
groups, restriction, higher-degree harmonics, a typed vocabulary. Independent of the
adaptive-order result: equivariant stencils and typed tensor outputs have value without
a language model, and E4 alone justifies the typed vocabulary. Needs its own sizing; the
first application of section 7.9 decides which groups come first.

Out of scope: half precision and the GPU lane. They decide whether any of this matters at
scale and belong to their own plans.

## 12. Open questions

1. Causal allocation: a per-window quota, or an auxiliary predictor on the token's own
   embedding, or both.
2. Is the symmetric trilinear form expressive enough for ambiguity, or does it need the
   two-projection form valence tokens use.
3. Order 4: a centre plus an unordered triple, or a pair of pairs (the analogy shape).
4. Does the escalation ladder (order `k + 1` gated by the order-`k` connected mass) beat a
   single gate that allocates all orders at once.
5. How the valence prior interacts with subword tokenization, and where the merge step
   sits.
6. Whether the gate's projection can be shared across heads.
7. Whether a typed relation store (the `comm` route to the reversal failure) belongs in
   this plan or its own.
8. Which application of section 7.9 comes first; it fixes the groups P6 builds.
9. Whether a word's representation type is assigned by hand, learned as a discrete
   choice, or soft; and how frame words select a group element.
10. How a symmetry-breaking input (topography, gravity, a scanner's axis) is declared,
    so that the model stays equivariant with the field as an argument.
11. Whether the unit-scaling group and the irreps discipline share one checker, and
    what the type of a mixed quantity (a vector with units) is in it.
12. The lattice the subgroup gate ranges over, and whether its choice is per sample,
    per region, or declared by the text.

## 13. References

`checked` = confirmed against a search summary on the date given, not against the paper
text. `recalled` = cited from memory.

- Sanford, Hsu, Telgarsky, "Representational Strengths and Limitations of Transformers",
  NeurIPS 2023, [arXiv:2306.02896](https://arxiv.org/abs/2306.02896). Triple matching:
  Theorem 7 (single-layer lower bound), Theorem 18 (third-order attention); the
  multi-layer bound is a conjecture. `checked` 2026-11-13.
- Batatia, Kovacs, Simm, Ortner, Csanyi, "MACE: Higher Order Equivariant Message Passing
  Neural Networks for Fast and Accurate Force Fields",
  [arXiv:2206.07697](https://arxiv.org/abs/2206.07697). `checked` 2026-11-13.
- Anandkumar, Ge, Hsu, Kakade, Telgarsky, "Tensor Decompositions for Learning Latent
  Variable Models", JMLR 2014, [arXiv:1210.7559](https://arxiv.org/abs/1210.7559).
  `checked` 2026-11-13 (method); identifiability conditions `recalled`.
- Raposo et al., "Mixture-of-Depths: Dynamically allocating compute in transformer-based
  language models", [arXiv:2404.02258](https://arxiv.org/abs/2404.02258). `checked`
  2026-11-13.
- Batzner et al., "E(3)-equivariant graph neural networks for data-efficient and accurate
  interatomic potentials", [arXiv:2101.03164](https://arxiv.org/abs/2101.03164).
  `checked` 2026-11-02.
- Berglund et al., "The Reversal Curse",
  [arXiv:2309.12288](https://arxiv.org/abs/2309.12288). `checked` 2026-11-02.
- Zheng et al., "Large Language Models Are Not Robust Multiple Choice Selectors", ICLR
  2024, [arXiv:2309.03882](https://arxiv.org/abs/2309.03882). Existence of the bias
  `checked` 2026-11-02; magnitudes not confirmed.
- Liu et al., "Lost in the Middle: How Language Models Use Long Contexts",
  [arXiv:2307.03172](https://arxiv.org/abs/2307.03172). Finding `checked` 2026-11-13; the
  20-point figure `recalled`.
- Thinking Machines, "Defeating Nondeterminism in LLM Inference", 2025
  (thinkingmachines.ai/blog/defeating-nondeterminism-in-llm-inference); "LLM-42: Enabling
  Determinism in LLM Inference with Verified Speculation",
  [arXiv:2601.17768](https://arxiv.org/abs/2601.17768). `checked` 2026-11-02, secondary
  summaries.
- Dao et al., "FlashAttention", [arXiv:2205.14135](https://arxiv.org/abs/2205.14135).
  Figures are the project's own benchmarks; `checked` 2026-11-02.
- Kamath, Hessel, Chang, "What's 'up' with vision-language models? Investigating their
  struggle with spatial reasoning", EMNLP 2023,
  [arXiv:2310.19785](https://arxiv.org/abs/2310.19785). `checked` 2026-11-14; a secondary
  summary gives a lower model figure.
- Laterality in generated radiology reports: CorBenchX,
  [arXiv:2505.12057](https://arxiv.org/abs/2505.12057); ELIXR,
  [arXiv:2308.01317](https://arxiv.org/abs/2308.01317). A recognized failure mode,
  `checked` 2026-11-14; no error rate was found.
- Bakarji, Callaham, Brunton, Kutz, "Dimensionally Consistent Learning with Buckingham
  Pi", [arXiv:2202.04643](https://arxiv.org/abs/2202.04643). `checked` 2026-11-14: the
  architecture alone does not guarantee dimensionless groups.
- Wen, Horton, Munro, Huck, Persson, "An equivariant graph neural network for the
  elasticity tensors of all seven crystal systems" (MatTen),
  [arXiv:2307.15242](https://arxiv.org/abs/2307.15242). `checked` 2026-11-14.
- Bonev et al., "Spherical Fourier Neural Operators: Learning Stable Dynamics on the
  Sphere", ICML 2023, [arXiv:2306.03838](https://arxiv.org/abs/2306.03838). `checked`
  2026-11-14.
- Yang et al., "CLIPSym", ICCV 2025, [arXiv:2508.14197](https://arxiv.org/abs/2508.14197);
  "Eq.Bot", [arXiv:2511.15194](https://arxiv.org/abs/2511.15194). `checked` 2026-11-14,
  one search.
- `recalled`, not re-read: Elhage et al., "A Mathematical Framework for Transformer
  Circuits" (skip-trigrams, the copying pattern); Levy and Goldberg, "Neural Word
  Embedding as Implicit Matrix Factorization"; Arora et al., "Linear Algebraic Structure
  of Word Senses"; Su et al., "RoFormer" (RoPE); Press et al. (ALiBi); Fuchs et al.,
  "SE(3)-Transformers"; Liao and Smidt, "Equiformer"; Lee et al., "Set Transformer";
  Jumper et al., AlphaFold 2 (triangle updates); Kakarala (completeness of the
  bispectrum); Gentner, "Structure-Mapping"; Graves, "Adaptive Computation Time"; Alman
  and Song (tensor attention complexity); Geirhos et al. (texture bias of convolutional
  networks); Oppenheim and Lim (the importance of phase in signals); Weiler and Cesa
  (steerable convolutions for E(2)); Wang, Walters and Yu (approximately equivariant
  networks).
