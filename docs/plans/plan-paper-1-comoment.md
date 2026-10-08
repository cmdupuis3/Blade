# Paper 1 — introducing Blade through the comoment tensor

**Status 2026-10-07: VENUE DECIDED, JFP** (author, 2026-10-07; ECOOP R2 stays the
conference alternative). The author's framing note of the same day: symmetry
tracking matters most under DOMAIN DECOMPOSITION of comoment problems, where the
sub-problems run through every combination of commutativity -- the draft now
motivates with it (§1, §2, the §3.4 block listing, §4.6).

**Status 2026-10-05: FIRST DRAFT WRITTEN**
(`docs/research/paper-1-comoment-draft.md`, 16 listings checked by
`blade test docs paper-1`, on master `4fe4e1a4`). Since the plan was written:
E1 (type display), E2 (mean hoist), E3 (`>>@` ICE) and E5 (fiber provenance)
are FIXED on master; E4 (false `comm` on row kernels) is open; and drafting
found two new items, E14 and E15 in §10. **E14 contradicts §4 below as first
written**: the compiler does not leave `f(A, B, A)` dense, it regroups.

**Plan of 2026-10-04.** Synthesized from a three-agent pass
(formal core and theorem inventory; related work and priority timeline; running
example and evidence) plus direct re-runs of the load-bearing findings on master
`4dfda436` (binary 0.20.0). Everything marked *verified* below was run or read at
the cited place on that commit; everything else says what it rests on.

The paper introduces Blade through **one problem**: the order-r comoment tensor
of n series,

    M_r(i₁, …, i_r) = (1/T) Σ_t Π_k (x_{i_k, t} − μ_{i_k}),

covariance at r = 2, coskewness at r = 3, cokurtosis at r = 4. It is symmetric
under every permutation of its indices, so it has C(n+r−1, r) distinct entries,
not n^r. The paper shows four language mechanisms composing on it — index
types, loop objects, commutativity, arity polymorphism — so that one short
function yields every order, each stored and computed once per distinct entry,
and the saving is refused when the program does not license it.

---

## 1. Thesis, audience, contributions

**Thesis.** Declaring structure is the optimization interface. The programmer
states what is true of the kernel (`where comm`), of the index domain (index
types) and of the call (which arrays, how many); the compiler derives the loop
nest, the storage layout and the iteration bounds, and reports what it applied
and what it declined.

**Audience.** Array-language and tensor-compiler readers (the Dex / Remora /
Futhark / TACO / Finch community). The paper must be readable without knowing
Blade and without group theory beyond "permutations of argument positions".

**Contributions, scoped to what the evidence supports.**

| # | Contribution | Rests on |
|---|---|---|
| C1 | A language design in which index types, reified loops, kernel laws and argument count compose; shown end to end on one program | the listing ladder (§3), all verified |
| C2 | A licensing law for loop-generated arrays: the output is symmetric under G = H ∩ Stab(B), H the kernel's invariance group and Stab the permutations fixing the argument binding. Soundness is general; necessity is shown by closed counterexamples; the compiler deliberately grants the contiguous-block part (linear to check, and the only layout-optimal spelling), characterized exactly by a theorem | `BladeLowering.v`, `BladeCompleteness.v`, `BladeCore.v`, `BladeCounting.v`; `blade plan` |
| C3 | Arity polymorphism with concatenating frames: the number of array arguments sets output rank, loop depth and symmetry; one typing rule | formalism §8, §13.2 (rule unmechanized) |
| C4 | A lower bound in the uniform model: any program correct for every kernel obeying the declared laws needs C(n+r−1, r) kernel calls and cells, and the emitted nest attains it | `BladeOptimal.v` (decision D2: main text or deferred) |
| C5 | An implementation (F# to C++17, interpreter twin) and an evaluation against the finite-n ceiling and external baselines | §7; counts exist, timings must be produced |
| C6 | A mechanized artifact: the proof files touching these pillars, Rocq 9.0.1, no axioms, no `Admitted` | about 541 of the tower's 1362 statements |

**Stated non-contributions.** No new mathematics (symmetric storage and the
stars-and-bars count are classical); no type-soundness theorem; no claim that
the compiler reaches all of H ∩ Stab.

---

## 2. Outline and page budget

Sized for roughly 20 pages single-column (PACMPL style); a 12-page two-column
venue drops §6 to a page and moves the appendix theorems to the artifact.

| § | Section | Pages | Content |
|---|---|---|---|
| 1 | Introduction | 1.5 | the comoment problem; what people write today (dense einsum, a hand-written triangular nest per order, a symmetric-tensor library class); the headline listing; contributions |
| 2 | The problem | 1 | definition, symmetry, the count, where it is used (finance, turbulence closure, cosmology polyspectra, latent-variable tensor methods) |
| 3 | Blade by example | 5 | the ladder L1–L6 (§3 below), one mechanism per subsection, each ending in something observable |
| 4 | Formal core | 2.5 | one syntax figure, one typing figure (four rules), the semantic model, the licence law, five theorems |
| 5 | Compilation | 2 | σ vector to index type to loop nest; packed pool offsets; canonicalizing reads; BLAS route at r = 2; OpenMP placement; `blade plan` |
| 6 | Evaluation | 3 | RQ1–RQ6 (§7 below) |
| 7 | Related work | 2 | per pillar; symmetric-tensor compilers; the priority statement |
| 8 | Limitations | 0.75 | the conservative grant; trusted declarations; floating point; cost of the definitional form |
| 9 | Conclusion | 0.25 | |
| A | Appendix / artifact | — | remaining theorems, Coq name table, full listings, benchmark protocol |

Dimensional currying (an `Array<T like I, J>` is the function I → J → T) is the
glue of §3.1, not a fifth pillar.

---

## 3. The listing ladder

Six listings, one data prelude, each verified on the current binary. The paper
freezes them as corpus tests (`tests/corpus/paper-1/`, discovered by the default
suite and the interpreter differential) so the text cannot drift from the
compiler; figures are generated from those files.

| | Shows | What makes it observable | State |
|---|---|---|---|
| L1 | covariance: a `where comm(a, b)` kernel applied by `method_for(R, R)` | 10 packed cells for n = 4; `C(0,1) = C(1,0)`; triangular bound `i1 < 4 - i0` in the emitted C++ | verified; use the block-bodied `cov` (E2) |
| L2 | **headline**: one `comoment` over `Poly<T^1>`, applied at arity 2, 3, 4 through a stored `object_for` | 10 / 20 / 35 cells; plan lines "triangular levels 1, 2, 3"; `C3(0,1,2)` equals the kernel called directly on rows 2, 1, 0 | verified (block below) |
| L2′ | rank polymorphism on the same function: `M <@> (G, G, G)` with `G : X × Y × Day` | 20 cells, numerically equal to C3: symmetry is joint over the compound (X, Y) space | verified; output type is anonymous `SymIdx<3, 4>` (E8) |
| L2″ | mixed operands: `M <@> (R, R, S)` | σ = (1, 1, 2), 10 × 4 = 40 cells, nest `i1 < 4 - i0`, `i2 < 4` | verified |
| L3 | the loop as a value: `object_for(center) >>@ object_for(unit_scale)`; `method_for(Z, Z)` and `method_for(Z, Z, Z)` stored, applied later to `prodsum` kernels | loop objects emit no code until applied; the pipeline is one loop over rows; r = 2 routes to syrk, r = 3 has no BLAS route | verified, but blocked on E2 or E3 for the timed form |
| L4 | index types: ascribe `SymIdx<2, AssetIdx>`; read the diagonal with `range<AssetIdx>` | accepted; `range<DayIdx>` at the same extent refused BL4003; a dense ascription refused BL3001 | verified; the deduced type cannot be displayed (E1) |
| L5 | the symmetry refusal: `method_for(R, S) <@> cov` with S a different array over the same index space | dense 4 × 4, `cross(0,1) ≠ cross(1,0)`; plan: "declined -- the commuting positions hold different arrays (R, S)" | verified |
| L6 | the unlicensed claim: `where comm(x, y)` on `x * x * y` | BL4013 with a concrete counterexample; `reynolds(...)` as the licensed repair | verified for scalar kernels only (E4) |

The headline listing. The `| 1 ->` base arm is deliberate: with it the compiler
*deduces* commutativity over the pack at every arity and asks for the pin
(BL4010); with the quickstart's `| 0 -> 1.0` arm nothing is deduced and the
`comm` is a trusted declaration (both verified).

```blade check
from stats import mean
type AssetIdx = Idx<4>
type DayIdx = Idx<6>
let R: Array<Float64 like AssetIdx, DayIdx> = [
    [1.0, -2.0, 3.0, 0.5, -1.5, 2.0], [0.5, 0.8, -0.2, 0.4, 0.1, 0.6],
    [2.0, 1.0, 0.0, 1.0, 3.0, -1.0], [-1.0, 0.0, 1.5, 2.5, 0.5, 1.0]]

function centered_prod(xs: Poly<T^1>) -> T^1 = {
    match arity(xs) with
    | 1 ->
        let head :: tail = xs
        head - mean(head)
    | _ ->
        let head :: tail = xs
        (head - mean(head)) * centered_prod(tail)
}
function comoment(xs: Poly<T^1>) where comm(xs) -> T^0 = mean(centered_prod(xs))

let M = object_for(comoment)
let C2 = M <@> (R, R) |> compute           // covariance: 10 cells
let C3 = M <@> (R, R, R) |> compute        // coskewness: 20 cells
let C4 = M <@> (R, R, R, R) |> compute     // cokurtosis: 35 cells
let direct = comoment(R(2), R(1), R(0))    // the kernel itself, at arity 3
let cell = C3(0, 1, 2)                     // any argument order reads the same cell
// EXPECT: direct = 0.308333333333333
// EXPECT: cell = 0.308333333333333
```

**Figures.** F1 the Python loop nest beside L2. F2 the call-site table (operands
→ σ → output type → cells), the paper's central figure. F3 the emitted
triangular nest and pool offset for C3. F4 `blade plan` applied / declined
lines. F5 the two refusals (BL4003, BL4013). F6 realized ratio against the
finite-n ceiling. F7 wall time against baselines.

**Two forms, both shown.** L2 is the definitional program: it reads like the
formula, and it recomputes each row's mean inside the r-ary nest. L3 is the
fast program: center once in a pipeline, then `prodsum`. The paper says so
plainly and times L3; the gap between them is exactly what the orbit-work
theorem (appendix, `two_node_orbit_work`) counts.

---

## 4. Formal core (about 2.5 pages)

**Figure 1, syntax.** Index types {`Idx<n>`, `SymIdx<r, I>`, products};
`Array<T like I…>` and abstract `T^r`; kernels with `where comm`; `method_for`,
`object_for`, `<@>`, `<*>`, `>>@`, `compute`; `Poly<T^k>`; application `A(i)`.

**Figure 2, typing — four rules.**

1. Nominal subscript: a subscript into slot I needs a `Nat<I>` (formalism §3.10).
2. `Poly` kernels (§8.2).
3. App-Object (§13.2): for `O <@> (A₁ … Aₙ)`, output rank r′ = Σᵢ(rank Aᵢ −
   irank(f, i)) + orank(f), symmetry σ′ = OutputSymmetry(A₁ … Aₙ, f).
4. The identity-group judgment: a run of g > 1 neighbouring identical arguments
   inside one comm block contributes `SymIdx<g, I₁ × … × I_s>` over the compound
   frame; contributions concatenate.

**Semantic model, one paragraph.** A call site denotes
`Out_B(ix) = f(λp. D(B p)(ix p))`; a loop object denotes `map f (enum S)`.

**The law.** H = {s : f ∘ s = f}, Stab(B) = {s : B ∘ s = B}, and the output is
invariant under G = H ∩ Stab(B). By formalism §8.3 the grant is comm blocks
intersected with runs of neighbouring identical arguments, a subgroup of G, so
that `f(A, B, A)` stays dense although G contains the (0 2) swap. **The
compiler does not do this (E14, verified 2026-10-05):** `rawAxisGroups`
(`src/IRLoopStructure.fs:192`) merges a level with ANY prior level of the same
array in the same comm group, and `buildLoopLevelStructure` reorders the nest
so the group is contiguous. For `(R, S, R)` the nest runs (R, R, S), the type
is `SymIdx<2, AssetIdx>, AssetIdx`, and `K(i, j, k)` returns f(Rᵢ, S_k, Rⱼ).
The paper cannot be frozen until the language picks one: dense in operand
order (the formalism), a refusal with a "reorder to (A, A, B)" hint, or
regrouping with a documented axis order.

**The adjacency discipline is a design decision, not a gap, and the paper
presents it that way.** Two reasons (author, 2026-10-05):

1. *Checkability.* Testing r − 1 adjacent positions is linear and composes
   under recursion; testing all permutations does not. This mattered from the
   start: the 2019 rule (`c786ed97`, `fs/parser.fsx:218`) already compares
   position `index + 1` with `index` inside recursive pack expansion.
2. *Only the optimal path is admitted.* A non-adjacent licence is reachable by
   reordering the kernel's parameters (`g(x, z, y) = f(x, y, z)`, called as
   `g(A, A, B)`), which puts the symmetric slots next to each other, so the
   packed simplex stays one curried block of the output. Accepting the
   non-adjacent spelling would admit a second, worse-laid-out route to the
   same orbits, against "the fastest way is the only way".

The proof tower already says exactly this: `deduced_group_largest`
(BladeDeduceExact.v:488) characterizes the grant as the largest
contiguous-block Young subgroup of the symmetry group, and the comment on
`nonadjacent_symmetry_missed` (:526–530) calls the skipped case "reachable
only by reordering the parameters". So the paper states a precise theorem
about what is granted rather than an approximation of H ∩ Stab. Expect one
reviewer push-back: the kernel-call count of `f(A, B, A)` equals that of
`g(A, A, B)`, so the reordering argument is about layout and currying, not
about call count; say that explicitly.

**One equation for storage.** `A(i) = buf[off(lj(sort i))]`, with `lj` the
successive-difference (left-justified) coordinates and |SymIdx<r, n>| = C(n+r−1, r).

**Arity versus rank polymorphism, stated exactly.** Remora-style lifting makes
argument frames agree and co-iterates; in Blade's kernel position each
argument's frame is iterated independently and the frames concatenate, so a
3-ary rank-1 kernel over (A, A, A) with A : N × T yields the full rank-3 tensor
where frame agreement would yield its diagonal. Co-iteration is the explicit
`zip`. Identity groups are by binding name, not by type: `f(A, A)` and `f(A, B)`
have different types even when A and B do.

**Leave out.** Compound / sparse / ragged / orbit / irreps index types, the
MonadPlus combinators, `let rec`, the Cauchy split and the rank dichotomy,
wreath products, `Tuple<N>` schemas, and `range<SymIdx>` in any example (it
hands the kernel packed offsets; formalism §7.3 records that as observed
behaviour, not endorsed semantics).

---

## 5. Theorem shortlist

All Qed, no axioms. Names are the Coq identifiers the paper's table cites.

| | Statement | Coq | Scope the paper must state | Kind |
|---|---|---|---|---|
| Thm 1 | The `SymIdx` enumeration is sound, complete, duplicate-free, has C(n+r−1, r) cells, and offset order is lex order | `storage_cardinality` (BladeBinomial), `enum_sound` / `enum_complete` / `enum_NoDup`, `lj_correct` (BladeDMWF), `enum_offset_respects_lex` (BladeLex) | general r, n; the r! ratio is not mechanized | formalized folklore |
| Thm 2 | Licence soundness: s ∈ H and s ∈ Stab imply Out ∘ s = Out; the joint swap holds over compound indices | `output_symmetry_soundness`, `diagonal_group_law` (BladeLowering) | any r, opaque index type | the framing is the contribution |
| Thm 3 | Necessity by counterexample: a comm kernel over distinct arrays on a shared index space is not symmetric; symmetric input without H gives nothing; the per-dimension swap is not a symmetry; no lossless per-dimension product layout exists | `shared_units_insufficient`, `input_symmetry_not_sufficient` (BladeLowering), `per_dim_swap_not_symmetry` (BladeCore), `counting_general_C` (BladeCounting) | first three are r = 2 witnesses | new as a packaged side-condition set |
| Thm 4 | The pack licence: an associative-commutative fold over a pack is invariant under every permutation at every arity; there is no signed analogue | `packfold_permutation`, `signed_exchange_collapse` (BladeDeduce) | exact arithmetic | formalized folklore |
| Thm 4′ | The grant is exact for its design: the deduced group is generated by the certified adjacent transpositions and contains every group so generated inside the symmetry group, i.e. the largest contiguous-block Young subgroup | `deduced_group_largest`, `young_generated` (BladeDeduceExact) | unsigned fixed-arity fragment | new as a statement about a compiler's grant |
| Thm 5 | Uniform optimality: any program correct for every S_r-invariant kernel asks at least C(n+r−1, r) questions; the canonical enumeration attains it; with several identity groups the bound is the product | `uniform_optimality_sym`, `uniform_optimality_young_nat` (BladeOptimal) | H = S_R only; generic data; counts calls and cells, not time | no published statement found |

Appendix or artifact: licence exactness (`license_exactness`, an iff only at
H = S_r); the rest of deduction exactness and its witnesses (`parfix_exact`,
`nonadjacent_symmetry_missed`, `wreath_symmetry_missed`), unsigned fixed-arity
fragment only; the pipeline orbit-work theorem instantiated at centered
covariance (`two_node_orbit_work`: n + C(n+1, 2) against 3n²); the loop algebra
(`trinity_fold_closure`, `compose_apply_duality`). There is no literal
curry ≅ uncurry theorem; `two_maximal_curryings` is about loop-object binding
and must not be cited for array currying.

---

## 6. Claims the paper must not make

1. **"r! speedup."** r! is the asymptotic ceiling and has no Coq lemma. The
   realized cell ratio n^r / C(n+r−1, r) is what the paper reports (table in
   §7). `blade plan` prints "speedup x2 / x6 / x24"; quickstart-1 labels
   2x / 6x / 24x at n = 3, where the real ratios are 1.5 / 2.7 / 5.4.
2. **"The compiler achieves H ∩ Stab."** It grants neighbouring identical runs,
   by design (§4): state the grant as the largest contiguous-block Young
   subgroup (`deduced_group_largest`) and the non-adjacent case as reachable by
   reordering parameters, not as a missed optimization.
3. **"Symmetry is inferred."** For primitive operators, yes. For named kernels
   deduction produces a confirm-and-pin suggestion (BL4010) and the pin is what
   licenses storage. A `comm` the deducer cannot decide is trusted; BL4013
   refutes only by a sign law or a sampled scalar counterexample (E4).
4. **Bitwise equality with the dense result.** The pack rule treats float `+`
   and `*` as associative; say "symmetric by construction, each orbit computed
   once".
5. **Type soundness, or a proved rank deduction.** Neither exists; §13.2 has no
   Coq counterpart.
6. **Optimal time.** Thm 5 counts kernel calls and cells under generic data.
7. **1362 theorems.** Cite the pillar subset (about 541) and "Rocq 9.0.1";
   88 of the total are `Example` pins, about 415 back the recurrence research
   and about 202 the ML seams.
8. **Novel index arithmetic.** The mixed-radix offset bijection has prior art in
   CTF; licence exactness in Donaldson and Miller (FM 2005).
9. **Cache optimality by construction** (formalism §9): the layout proofs make
   no cache claim.
10. **Product symmetry (r!)^d.** Withdrawn in formalism §12.4; the literature
    survey still says it.

---

## 7. Evaluation

Proportionate to an introductory paper: structural counts first, then one
controlled timing study, then external baselines.

Realized ratio n^r / C(n+r−1, r) (arithmetic; the emitted pool constants agree at
n = 7 and 11):

| n | r = 2 | r = 3 | r = 4 | r = 5 |
|---|---|---|---|---|
| 7 | 1.75 | 4.08 | 11.4 | 36.4 |
| 11 | 1.83 | 4.65 | 14.6 | 53.6 |
| 61 | 1.97 | 5.72 | 21.8 | 102 |
| 301 | 1.99 | 5.94 | 23.5 | 116 |
| ceiling r! | 2 | 6 | 24 | 120 |

| RQ | Question | Evidence today | To produce | Effort |
|---|---|---|---|---|
| 1 | Economy of expression: one definition for r = 2..5 at any input rank, against per-order einsum strings, hand-written triangular C, Cumulants.jl | none | line counts, side-by-side listing | 1 day |
| 2 | Exact structural savings: cells, kernel calls, bytes including the pointer skeleton | kernel-call counts measured at n = 7 (28 / 84 / 210 / 462 against 49 / 343 / 2401 / 16807); pool constants at n = 7, 11 | extend to n ∈ {61, 101, 301} by emission; ideally the counts in `blade plan` (E9) | 0.5 day, or 1–2 days with the plan change |
| 3 | Realized versus ceiling in one compiler: the same file with `comm` deleted | scalar-map control only (plan-simplex-blocked-compute §0a/§0b: r = 3, n = 301 at 5.39x of 5.94; r = 4, n = 61 at 16.0x of 21.8) | the fiber comoment (`prodsum` on centered rows), r = 2–4, n ∈ {61, 101}, T ≈ 2003, serial and OpenMP | 2 days after E2/E3 |
| 4 | External baselines | none reproducible (the August cross-framework numbers lost their harness) | NumPy `einsum` / `opt_einsum`; a hand-written triangular C nest (the ceiling); `dsyrk` and Eigen `rankUpdate` at r = 2; **Cumulants.jl** (the closest prior system, so the one that cannot be skipped); R `PerformanceAnalytics` `M3.MM` / `M4.MM`; SySTeC/Finch if runnable (one-day spike, drop if not; its own artifact excludes SSYRK) | 4–6 days |
| 5 | Parallel scaling | OpenMP placement verified (outer triangular level, dynamic schedule) | thread sweep; note the skewed per-row work | 1 day |
| 6 | Memory | `examples/tools/peakmem.fsx` exists; the earlier 15.5x figure is not reproducible | peak working set, Blade against dense | 0.5 day |

**Protocol (standing rules).** No power-of-two extents on dense arms;
interleaved A/B arms; medians of at least 9 repetitions over 3 rounds;
checksum-pinned outputs; one session, AC power, one process at a time on this
host. The 2023 `legacy/blade_timings*.ods` sheets are history, not evidence.

---

## 8. Related work and positioning

### 8.1 Closest prior work per pillar

| Pillar | The "isn't this just X?" | What to concede | What Blade adds |
|---|---|---|---|
| Index types | **Dex** (ICFP 2021) | arrays as curried tables over typed index sets, partial indexing, user-defined index sets | symmetric and antisymmetric *quotient* index types deduced from a kernel licence; nominal identity at equal extent deciding proven versus checked subscripts; storage and iteration order derived from the type |
| Loop objects | **Repa delayed arrays / push arrays**; Kokkos policy + functor as a cousin | `method_for(A, B) <@> f \|> compute` is roughly a partially applied delayed map | the *traversal* (which arrays, outer product or zip, the domain, the symmetry licence) is reified separately from the kernel; either can be bound first; combinators fuse. Against Halide: no schedule language, by design |
| Symmetry | **SySTeC** (CGO 2025), **StructTensor** (OOPSLA 2023) | a repeated operand giving a symmetric result is textbook (SYRK 1990; Schatz et al. 2014) and both systems exploit it | the licence is a declaration on a user kernel and becomes a type; a stated rule with a refusal when the arrays differ; compact typed output; antisymmetry; mechanized soundness and a lower bound |
| Arity polymorphism | **Remora** (ESOP 2014) | the mechanism is monomorphization, as with variadic templates; APL's `∘.f` is the two-argument case | polymorphism in argument *count*, with output rank the sum of frames and symmetry deduced from repeated arguments: the arity → rank → symmetry link |
| The application | **Domino, Gawron, Pawela** (SISC 2018; Cumulants.jl, SymmetricTensors.jl) | arbitrary-order moment and cumulant tensors in block-symmetric storage with about d! savings already exist | the same saving from a generic kernel plus deduction, not a purpose-built algorithm. Any performance claim needs the head-to-head (RQ4) |

Also to cite: Shi, Chou, Kjolstad, Amarasinghe (arXiv 2110.00186), whose §4.2
names the repeated-tensor case as undetectable in their framework — lead the
symmetry paragraph with it; CTF; Ballard–Kolda–Plantenga 2011 for the
C(m+n−1, m) storage; Donaldson and Miller (FM 2005); Futhark size types,
Naperian functors, DML for index-typed arrays; Looplets and Finch; Feldspar,
Obsidian, Accelerate, Lift, MDH for deferred arrays; Weirich–Casinghino and
Strickland et al. for arity genericity; Sherman–Kolda (implicit moment
tensors win asymptotically at large n and r) and Solomonik–Demmel–Hoefler
(fewer operations can cost more communication) as honest limits.

### 8.2 What SySTeC states (arXiv 2406.09266 v2, read directly)

- Input: one Finch assignment plus a list of symmetric tensors with their
  partitions; full and partition (Young) symmetry only.
- The permutable set is built from the declared input partitions (§4.1). The
  repeated-operand case appears in prose (§3 Example 3.1; §3.2.2: "Invisible
  output symmetry often presents itself when there are multiple of the same
  operands in an assignment") and in the SSYRK evaluation (§5.2.4), but §4
  states no detection rule for it. The earlier note that "no sentence states"
  it was too strong and is corrected here.
- Dense symmetric output is not compact: the canonical triangle is computed,
  then copied to the full output, and the copy is excluded from timings (§5.2).
- No optimality claim, no antisymmetry, order ≤ 5 evaluated, single core,
  sparse through Finch.

Genuine overlap: canonical-simplex iteration; output symmetry from a repeated
operand under commutativity; diagonal handling. Only SySTeC: sparse and
structured formats, reuse of reads from declared-symmetric inputs, several
misaligned symmetric inputs in one einsum, a peer-reviewed evaluation against
TACO / MKL / SPLATT. Only Blade: the items in the table above, plus OpenMP
code generation. Blade is dense-only; say so.

### 8.3 The priority record

| Date | Event | Evidence |
|---|---|---|
| 1990 | BLAS-3 SYRK: the textbook repeated-operand symmetric result | — |
| 2019-04-10 | repository created (public today, then named `EDGI_nested_iterators`); first source commit has reified loops (`method_for`, `object_for`), triangular iteration from *declared* input symmetry, and an unused commutativity-group scaffold | GitHub API `created_at`; `e7f72422` |
| 2019-06-11 | commutativity group **and same array** ⇒ cross-argument triangular loops, as a sketch | `f41d17dc`, `fs/sketches.fsx:116` |
| 2019-11-14 | the same rule wired into code generation; tag 0.1.0, GitHub release the next day | `c786ed97`, `fs/parser.fsx:218` |
| 2020-05-07 | first full Software Heritage visit of the origin, containing all of the above | SWH snapshot `6b02ec2d` (re-checked 2026-10-04) |
| 2021-10-01 | Shi et al. on arXiv | |
| 2022-11-18 | StructTensor on arXiv (self-multiplication rule; covariance as motivation) | |
| 2023-03 | Blade-DSL README states "fast calculation of full comoment tensors"; 8-way same-array benchmarks | `1a541677`, `59909f94` |
| 2023-09-16 | SySTeC repository created | |
| 2024-06-13 | SySTeC on arXiv; CGO, March 2025 | |
| 2026-01 → | named index types, `where comm` syntax, the stated rule and its refusal, flat packed storage, the proofs | `f3ae9954` onward |

**The sentence the record supports.** "Triangular iteration for a kernel
declared commutative in argument positions that receive the same array was
automated in Blade's predecessor in 2019 (release of 2019-11-15; archived by
Software Heritage on 2020-05-07), before Shi et al. (2021), StructTensor (2022)
and SySTeC (2024), which arose independently. The underlying observation is
older (SYRK; Schatz et al.)."

**What the record does not support.** Priority for index types, the `comm`
syntax, the refusal, packed C(n+r−1, r) storage, or any proof: all are 2026.
The first commit alone supports only declared-input-symmetry iteration, so the
claim dates from June / November 2019, not April. Two things to state rather
than have found: the 2019–2020 `covariance.edgi` kernel computes a product of
centered sums (identically zero) and may be cited for its structure only; and
the 2020 output-symmetry function grouped by commutativity group without
checking array identity. The exact date the repository became public cannot be
determined; the Software Heritage visit is the earliest proof of public
availability.

---

## 9. Venue

Deadlines as found on 2026-10-04; OOPSLA and ECOOP were read on their official
pages, the rest by a delegated search. Re-check before committing.

| Venue | Fit | Format | Next deadline |
|---|---|---|---|
| OOPSLA 2027 | strong | acmsmall, 23 pp | R1 2026-10-14 (not feasible); R2 2027-04-07 |
| ECOOP 2027 | good | LIPIcs, 25 pp camera-ready | R1 2026-11-19; R2 2027-02-11 |
| ICFP 2027 | good if framed around semantics and the Coq artifact | acmsmall, 25 pp | 2027-02-25 |
| PLDI 2027 | risky with a modest evaluation | — | 2026-11-12 |
| ARRAY 2027 (at PLDI) | best topical fit; a workshop | 12 pp paper (archival, counts against later novelty) or a 2 pp abstract (does not) | about 2027-04-01, estimated from 2026 |
| ⟨Programming⟩ | good | — | 2027-02-01 |

**Decision (2026-10-07): Journal of Functional Programming.** Rolling
submission, no page limit, the Dex/Futhark/Remora lineage's home, mechanized
proofs valued, a modest evaluation acceptable when the formal half carries the
paper; the risk is that §5–§6 get less credit than at a compiler venue. ECOOP
R2 (2027-02-11) remains the conference alternative for the same manuscript;
the TOMS framing (software for comoment/cumulant tensors, Cumulants.jl
head-to-head) is held for the evaluation-heavy follow-up. Post to arXiv when
the draft is stable; an ARRAY two-page abstract for visibility.

*Superseded recommendation (2026-10-04):* ECOOP R2 first, ICFP as the
alternative framing, OOPSLA R2 next.

Three process facts. arXiv's cs.PL now needs a prior arXiv authorship or a
personal endorser, so start that now. OOPSLA, ECOOP and PLDI review
double-blind, so the priority evidence is cited with links removed. The
repository's public history shows AI-assisted authorship of the 2026 formalism
and compiler; ACM's authorship policy asks for generative-AI use to be
disclosed (check each venue's own wording), and no 2026 text may be offered as
evidence of independent discovery.

---

## 10. Pre-paper engineering

Found while building the ladder; repros in the appendix. P0 items block a
listing or a claim.

| | Item | Why it matters to the paper | Class |
|---|---|---|---|
| E2 | `x - mean(x)` and `mean((a - mean(a)) * (b - mean(b)))` in a generic `T^1` function evaluate `mean` inside the per-element loop: O(T²). The `let`-hoisted block form is linear | the quadratic spelling is the one in CLAUDE.md and quickstart-1; no timing is meaningful before this | P0 perf |
| E3 | a block-bodied `T^1` stage inside `>>@` is BL9002 (the staged nest maps elements, not rows) | E2's workaround cannot be used in the L3 pipeline | P0 bug |
| E1 | type display drops `SymIdx` over a named index: `C` shows as `Array<Float64 like AssetIdx>` in `ide check` / MCP / REPL; `object_for(comoment)` shows as `Void` (`src/IRPrint.fs:193-198`) | F2, the central figure, cannot be produced from the tool | P0 display |
| E4 | a false `comm` on a row-valued kernel is trusted: `mean(a * a * b)` with `comm(a, b)` stores a triangle and returns a wrong `C(1, 0)` | the running example's own kernel class is outside the BL4013 check; quickstart-1 §9 implies otherwise | P0 soundness scope (decision D3) |
| E5 | index provenance is not checked across a kernel's fibers: `cov` over rows of a `DayIdx` array and an `HourIdx` array of equal extent compiles and runs, though `R(0) * H(0)` at top level is refused | pillar 1's claim, on the running example | P0 hole |
| E9 | `blade plan` states the asymptotic r!, not cells or kernel calls | RQ2 from the tool rather than arithmetic; the same instrument plan-uniform-optimality leaves open | P1 |
| E8 | compound symmetric output is anonymous (`SymIdx<2, 6>`), and formalism §8.4's `SymIdx<3, <Idx<M>, Idx<N>>>` spelling does not parse | L2′'s type in F2 | P1 |
| E6 | BL4013 advises `reynolds(...)`, but a bare `reynolds(g)` over (r, r) is stored dense with no plan line | L6's repair | P1 |
| E7 | BL4013's caret points at the second operand, not the `comm`; errors in applying a stored loop object are reported at its definition | F5 | P1 |
| E10, E11 | `<name> completed in <t>s` precedes values on stdout; rank-2 symmetric results print ragged, rank ≥ 3 flat | listings need a documented decoding | P2 |
| E13 | the `Poly` generator allocates r temporaries and recomputes r means per cell; never BLAS-routed | why L3, not L2, is the timed form | note |
| E14 | interleaved identical operands in a comm group are regrouped: `method_for(R, S, R) <@> k` emits the nest (R, R, S) and permutes the output axes to match, silently; contradicts formalism §8.3 / §8.4 step 4, quickstart-2, and `<*>` as shape concatenation; no corpus test covers the shape | §4.4 and §8 of the draft state the neighbouring-run rule; a reader's `K(0, 1, 2)` is not f(R₀, S₁, R₂) | P0 semantics (decision D9) |
| E15 | a six-subscript read `G3(x₁, y₁, x₂, y₂, x₃, y₃)` into a compound symmetric output passes `blade check` and is BL9002 at g++; the three-subscript flat read works | L2′ can only be shown with flat grid positions | P1 bug |

Status 2026-10-05: E1, E2, E3, E5 fixed on master (`76c1714a`, `9de90481`,
`4fe4e1a4`); E4 open (D3); E14, E15 new.

**Doc corrections to land with or before the paper:** quickstart-1 lines
521–523 (asymptotic labels at n = 3); formalism §12.4 item 4 ("r ≥ 3: open",
closed by `BladeDichotomy.v`) and the §8.4 cross-references to "§12.5";
proofs.md:2030 ("FULL, now an IFF"); proofs/README.md:3 ("Coq 8.18.0") and its
mixed-radix novelty claim; `docs/blade_literature_survey.md` rows still claiming
product symmetry, and its citation errors (Shi et al. are Shi, Chou, Kjolstad,
Amarasinghe; STUR is Ghorbani, Huot, Hashemian, Shaikhha; Dex's title ends
"Pointful Array Programming"; MDH is IJPP 2018; Moggi 2000 is a talk);
`tests/Benchmarks.fs:38-40` (index-type identity, stale).

---

## 11. Milestones

| | Milestone | Depends on | Rough size |
|---|---|---|---|
| M0 | Author decisions D1–D8; start the arXiv cs.PL endorsement | — | — |
| M1 | P0 engineering (E1–E5) and the doc corrections | D3 | 1–2 weeks |
| M2 | Freeze the listings as `tests/corpus/paper-1/`; generate F2–F5 from them | M1 | 2–3 days |
| M3 | Evaluation harness and runs (RQ1–RQ6), harness kept in the repo this time | M1 | about 2 weeks |
| M4 | Draft: §3 and §4 first (they fix notation), then §5–§7, introduction last | M2 | 2–3 weeks |
| M5 | Artifact: the pillar proof subset with a build script, the listing corpus, the benchmark harness | M3 | 3–4 days |
| M6 | Hostile read: one pass per section against §6's list and against SySTeC's text | M4 | 2–3 days |

---

## 12. Decisions for the author

| | Decision | Recommendation |
|---|---|---|
| D1 | Venue and length | **DECIDED 2026-10-07: JFP** (no page limit; ECOOP R2 2027-02-11 as the conference alternative) |
| D2 | Optimality (Thm 5) in the main text, or deferred | one theorem in the main text: it is the stated half of "the fastest way is the only way". The pipeline and deduction-exactness results go to the appendix and to a second paper |
| D3 | E4: extend the BL4013 witness to row-valued kernels, or scope the claim | extend it (sample short vectors); "refusals are features" is weak if the running example's kernel class is unchecked. Either way the paper states that an undecided `comm` is trusted |
| D4 | Which form is "the" program | both: L2 as the definition, L3 as the timed form, with the gap named |
| D5 | Priority wording | the sentence in §8.3: independent and earlier for the same-array commutativity mechanism (2019), nothing claimed for the 2026 layers, the older observation credited |
| D6 | PPL formers (`ppl.moments`, `ppl.cumulants`) | one sentence: library sugar over the same mechanism; `ppl.comoments(R, 3)` is BL5100 today, so they cannot be the r ≥ 3 baseline |
| D7 | Title | "One Function, Every Comoment: Index Types, Loop Objects and Arity Polymorphism in Blade" |
| D9 | E14: what `f(A, B, A)` means | match the formalism (three singleton groups, dense, axes in operand order) and add a BL4010-style hint to reorder; it is the written spec, it keeps `<*>` a shape concatenation, and it is the rule the author's two reasons (§4) argue for |
| D8 | How to present the lineage and the AI-assisted 2026 work | a short "history" paragraph in §7 of the paper: a 2019 C++ template library and pragma preprocessor, rewritten as a compiler in 2026 with AI assistance, disclosed per the venue's policy |

---

## Appendix: repros for §10

E3, internal compiler error:

```blade
from stats import mean
type AssetIdx = Idx<2>
type DayIdx = Idx<3>
let R: Array<Float64 like AssetIdx, DayIdx> = [[1.0, 2.0, 6.0], [0.5, 0.8, -0.2]]
function center(x: T^1) -> T^1 = { let m = mean(x)
    x - m }
function unit_scale(x: T^1) -> T^1 = x / sqrt(mean(x * x))
let Z = (object_for(center) >>@ object_for(unit_scale)) <@> R |> compute
```

E4, a false `comm` trusted (`c10` prints 0.7708, `truth10` prints −0.0208):

```blade
from stats import mean
type AssetIdx = Idx<4>
type DayIdx = Idx<6>
function m_aab(a: T^1, b: T^1) where comm(a, b) -> T^0 = mean(a * a * b)
let R: Array<Float64 like AssetIdx, DayIdx> = [
    [1.0, -2.0, 3.0, 0.5, -1.5, 2.0], [0.5, 0.8, -0.2, 0.4, 0.1, 0.6],
    [2.0, 1.0, 0.0, 1.0, 3.0, -1.0], [-1.0, 0.0, 1.5, 2.5, 0.5, 1.0]]
let C = method_for(R, R) <@> m_aab |> compute
let c10 = C(1, 0)
let truth10 = m_aab(R(1), R(0))
```

E5, provenance not checked across fibers (compiles and runs):

```blade
from stats import mean
type AssetIdx = Idx<2>
type DayIdx = Idx<3>
type HourIdx = Idx<3>
function cov(a: T^1, b: T^1) where comm(a, b) -> T^0 = {
    let ma = mean(a)
    let mb = mean(b)
    mean((a - ma) * (b - mb))
}
let R: Array<Float64 like AssetIdx, DayIdx> = [[1.0, 2.0, 3.0], [4.0, 5.0, 9.0]]
let H: Array<Float64 like AssetIdx, HourIdx> = [[3.0, 1.0, 2.0], [0.0, 5.0, 6.0]]
let X = method_for(R, H) <@> cov |> compute
```

E2: compile L1 with the one-line `cov` and read the emitted function body; the
subtraction loop calls `stats__mean` once per element.
