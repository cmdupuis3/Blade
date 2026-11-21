# `Moments<r, T>` — ensembles observed through their moments

**Status 2026-09-23: PROPOSED, nothing built.** The mathematics is proved
(§1):
- the exact reduction in shifted coordinates, over any commutative ring,
  with the exact invariance of ρ = M₁²/(N·M₂)
  (`proofs/BladeShiftedMoments.v`, 21 theorems, no axioms);
- its floating-point bound with coefficients from the input
  (`proofs/reals/BladeShiftedRounding.v`, 16);
- its floating-point bound with coefficients **computed from the summary
  itself** (`proofs/reals/BladeShiftedFeedback.v`, 68), including the
  **variance and standard deviation** in a coefficient
  (`proofs/reals/BladeShiftedVariance.v`, 39);
- binary64 witnesses (`proofs/floats/BladeBinary64Witness.v`, 10).

The reals files are conditional on Coq's real-number axioms and state the
rounding model as a hypothesis. The 2026-10-15 audit of these files
(RF-1 to RF-5) is answered by theorems: a mixed relative/absolute seed,
moments read vs. not read, a certified run-time ρ, the particle count bound
to N, an explicit domain for the √M₂ normalization, and a non-compounding
radius for that normalization (§1).

User decisions taken 2026-09-23:
1. the surface is a **`Moments<…>` element type**, not a `where` clause;
2. the feedback proof came first, then the variance lemma; both are done;
3. the integer shift is left to this plan (§3: c = 0).

The compiler hooks in §5 were read from `src/` at master `21eb0b5`. One of
them was checked by running the compiler (§5.3).

---

## 0. Summary

A `Moments<r, T>` value is an ensemble of `T`-valued particles known only
through its count and its moments up to order r. Its representation is
(N, c, M₁, …, M_r): a shift c and the shifted sums Mₖ = Σ(xᵢ − c)ᵏ. An affine
map applied to every particle is an operation on the value. So a particle
simulation whose program only ever asks for moments carries r + 2 numbers
per step instead of N:

```blade
type Step = Idx<500>
type P    = Idx<100003>

let x0: Array<Float64<meters> like P> = ...
let gain:  Array<Float64 like Step> = ...
let drift: Array<Float64<meters> like Step> = ...

let rec cloud: Array<Moments<2, Float64<meters>> like Step> =
    match cloud with
    | zero -> zero
    | zero :: s -> zero :: summarize(x0, 2)
    | prefix :: n -> prefix :: prefix(n - 1) * gain(n) + drift(n)

let m   = mean(cloud((499 : Step)))       // Float64<meters>
let var = variance(cloud((499 : Step)))   // Float64<meters^2>
```

Storage is 500 × 4 numbers instead of 500 × 100003. Each step is O(r) work,
and `summarize` is the one O(N·r) pass. Because the particle array never
exists, **nothing is rewritten**. The program *is* the reduced program, and
the type says so. This is why the type is better than the `where` clause of
the first draft of this plan:
- there is no recognizer;
- there is no whitelist of allowed reads;
- no "declared semantics" is attached to an ordinary-looking array;
- units come out right by construction (§3).

## 1. What is proved

| Statement | Artifact |
|---|---|
| Move the shift like a particle (c′ = a·c + b) and Mₖ′ = aᵏ·Mₖ: a pure rescaling, any commutative ring, no division | `BladeShiftedMoments.smom_aff` |
| The summary answers exactly what the raw power sums answer, both ways; every refusal and lower bound of the raw summary carries over | `raw_from_shifted`, `shifted_from_raw` |
| Soundness for **any** starting shift (a rounded mean included); started at the mean it stays the mean | `shifted_reduction_sound`, `central_preserved`, `mean_tracked` |
| Float, coefficients from the input: Mₖ within relative error (1+e₀)(1+u)^(kT) − 1 of the true moment; no N, no spread, no condition number | `BladeShiftedRounding.shifted_moment_float_bound`, `central_moment_float_bound` |
| The same from a **mixed seed** M̂ₖ = θ·Mₖ + e, \|e\| ≤ d, which a two-pass seed always has even where it has no relative error (a zero moment): relative radius ℓ₀ + T·k·λ, absolute part exp(T·k·λ)·\|a₁⋯a_T\|^k·d | `BladeShiftedFeedback.mixed_seed_input_bound`, `mcl_step` |
| Float, **coefficient computed from the rounded moments**: log-relative error ≤ rad T, with rad T = (1+rL)^T·ℓ₀ + r(ε + λ)·((1+rL)^T − 1)/(rL), where λ = −ln(1−u); linear in T when L = 0. A conservative worst-case upper bound, not a lower bound | `BladeShiftedFeedback.feedback_particles`, `rad_closed`, `rad_no_feedback` |
| The same, parameterized by the moments the coefficient **reads**: only those need a relative seed; every other moment may carry an absolute seed error, transported by \|∏ aₜ\|^k (the factor that scales Mₖ itself) times exp(rad T − ℓ₀). Pinned obstruction: [3, −10¹⁶, 10¹⁶] about its binary64 mean 1 has true M₁ = 0, computed 2, and no relative radius | `feedback_bound_read`, `normalized_ensemble_bound_seed`, `seed_no_relative_radius`, `floats/…binary64_two_pass_seed_first_moment` |
| The stability constant L for the coefficients people write: products add, powers scale by \|p\|, reciprocals keep, square roots halve, sums of non-negative terms take the max | `rcl_mul`, `rcl_pow`, `rcl_inv`, `rcl_sqrt`, `rcl_add_pos` |
| Worked instance: renormalizing by the tracked sum, a = fl(g / fl(√M₂)), gives L = ½, ε = 2λ. With M₂ > 0 and g ≠ 0 both runs stay in the domain at every step (positive radicand, nonzero divisor); without it the real model silently uses 1/0 = 0 and √(negative) = 0 where binary64 gives inf/NaN | `normalizing_coefficient_stable`, `normalized_ensemble_bound`, `normalized_ensemble_bound_dom`, `binary64_zero_spread_normalization_nan` |
| **The √M₂ normalization does not compound.** Exactly, (g/√M₂)²·M₂ = g², so M₂ = g² after every step from any positive start. Rounded, the float M₂ is within log-error 6λ of the true M₂ after every step ≥ 1, whatever the seed error; every other moment's radius is LINEAR in T: ℓ₀ + k(ℓ₂/2 + 3λ) + 6kλ·T | `normalized_exact_resets`, `normalized_m2_radius_constant`, `normalized_particles_linear` |
| **Subtraction with a dominant minuend:** 0 ≤ z ≤ ρ·y, ρ < 1, operands at log-errors ℓ_y, ℓ_z → y − z at log-error −ln Lo, Lo = (e^(−ℓ_y) − e^(ℓ_z)·ρ)/(1 − ρ), while Lo > 0; with an extra absolute error \|e\| ≤ δ·y on z, −ln(Lo − δ/(1−ρ)) | `BladeShiftedVariance.rcl_diff_dominant`, `rcl_diff_dominant_mixed` |
| ρ = M₁²/(N·M₂) is exactly invariant under affine steps; N·M₂ − M₁² does not depend on the shift | `BladeShiftedMoments.rho_invariant`, `variance_numerator_shift_free`, `variance_numerator_aff` |
| The float variance fl(fl(M₂/N) − fl(q·q)), q = fl(M₁/N), from moments at log-error ℓ: log-error DV(ρ, ℓ) = λ − ln Lo(λ+ℓ, 3λ+2ℓ, ρ). With M₁ also off by an absolute s·√(N·M₂): δ = e^(3λ)(2e^ℓ·√ρ·s + s²) — at an exact mean (ρ = 0) only s², so the M₁ error enters **squared** | `variance_rcl`, `variance_rcl_mixed`, `deltam_exact_mean` |
| The feedback theorem for a general (non-linear) error rule φ and an invariant of the true run, while the radius stays where φ is valid; instance: renormalizing by the float standard deviation, from a relative or a mixed M₁ seed | `feedback_bound_inv`, `feedback_bound_rel`, `renormalized_by_stddev_bound`, `renormalized_by_stddev_mixed` |
| **Certified run-time ρ.** From M̂₁ at (ℓ₁, D) and M̂₂ at ℓ₂: true ρ ≤ e^(2ℓ₁+ℓ₂)·(\|M̂₁\|+D)²/(N·M̂₂) (e^(3ℓ) when D = 0). The raw ratio can underestimate: [0, 1] with M̂₁ = ½, M̂₂ = 2 gives 1/16 against a true ½. A check on float data only (M̂₂ > 0, inflated ratio ≤ ρ_c < 1, Lo > 0 at ρ_c) implies the theorem's premises | `rho_certified_upper`, `stddev_admission_sound`, `renormalized_by_stddev_admitted`, `renormalized_by_stddev_mixed_admitted`, `uninflated_rho_fails_true_invariant` |
| N is the particle count: the particle iteration keeps it, and vex N is then the particles' population variance at every step | `piter_length`, `vex_is_population_variance`, `renormalized_particles_bound`, `renormalized_particles_admitted` |
| A degree ≥ 2 particle update has **no** summary of this kind at all, from N ≥ d·r particles (Reals axioms) | `BladeRealDensity.closure_refused_R_polynomial` |
| x ↦ xᵈ: any pair that agrees on p₀…p_r and disagrees at some order m with r < m ≤ d·r refutes every G; so N ≥ 2^r suffices, axiom-free, over any characteristic-0 integral domain (was 2^(dr−1)). (0,3,3) / (1,1,4) refutes squaring at r = 2, N = 3 | `BladeProuhetShift.power_refused_translated`, `power_never_closes_2r_Z`, `squaring_refused_at_3` |
| Leading coefficient a_d a nonzero **polynomial** in the summary, the lower aⱼ arbitrary: refused from N ≥ 2^(dr−1) + deg(a_d)·(2^r − 1), axiom-free | `BladeProuhetShift.closure_refused_polynomial` |
| Raw power sums are the wrong representation in floats (variance 2 instead of 2/3 at 10⁸, 10⁸+1, 10⁸+2) | `binary64_raw_moments_lose_the_variance` |

**The generic feedback bound grows geometrically, at rate 1 + rL per step.**
That is a conservative worst-case upper bound under the abstract stability
hypothesis, not a proved cost of feedback: it bounds the coefficient and the
moment it multiplies separately, and so discards their correlation. No lower
bound is proved. For the √M₂ normalization the correlation is total and the
radius does not compound at all (the reset row above). `blade plan` reports
the generic number when no sharper instance applies (§5.5).

**Not proved:**
- A coefficient that reads the **mean** (the shift c). Its error is coupled
  to b.
- A coefficient containing a **general subtraction** of moment-dependent
  terms. Only the dominant-minuend case is proved, which is what the variance
  needs.
- **Sharpness** of the generic geometric radius: no adversarial coefficient
  family attaining it is exhibited.
- A non-compounding radius for the **standard-deviation** normalization.
  Exactly, the variance after each step is g², as for g/√M₂, but the rounded
  argument would have to pass through the variance's subtraction; only the
  g/√M₂ case is proved.
- AD through a `Moments` recurrence.
- **Any refusal for Int.** Every refusal theorem assumes a characteristic-0
  integral domain. Int64 wraps, so its ring is ℤ/2⁶⁴, and some degree-2
  updates really do close there (§3, "Refusals under wrap-around").
- **The sharp real threshold.** An outside review (2026-10-15) argues on
  paper that over ℝ or ℂ every polynomial update of degree ≥ 2 is refused
  from N ≥ r + 2. Its argument is the term e_(r+1)^a·e_(r+2)^b of p_(dj) in
  elementary symmetric functions, whose coefficient no function of the
  retained moments can cancel. It also argues for N ≥ r + 1 when r ≥ 3, via
  Ritt's second theorem. Neither is mechanized. Closure does exist for every
  update at N ≤ r, where the moments determine the ensemble.
- The link to `src/`, as for every row of `docs/proofs.md`.

`BladeProuhetShift.v` (52 theorems) is on branch
`claude/pensive-proskuriakova-297cea`, commit `60b38a08`, not yet on master.

## 2. Surface

**The type.** `Moments<r, T>`:
- `r` is a statically evaluable order ≥ 1, the same contract as `Dist`'s
  order;
- `T` is the particle's scalar type, carrying its unit.

It is a strict nominal type, like `Dist<r, T like axes>`
(`docs/features/ppl.md` §5): orders must match exactly, and a bare tuple
never flows into one. v1 is scalar particles only. A vector particle would
be `Moments<r, T like I>`, following `Dist`'s `like` convention for the
particle's own axes (the comoment tower, later).

**Why not `Dist`.** `Dist` carries the *cumulants of a random variable*, and
`+` on it is the sum of *independent variables* (convolution). `Moments`
carries a *finite ensemble* with its count, and combining two means pooling
them (§4, later). The two share the scaling law (κₖ and Mₖ both scale by aᵏ),
not the algebra. Conversion either way is later work.

**Construction.**
- `summarize(A, r)`, for `A: Array<T like P>` (rank 1, v1), builds the seed.
  For Float it uses c = the two-pass mean and one fused pass for M₁..M_r
  (`<&!>`). For Int it uses c = 0 (§3).
- `count(m)` is N.
- The name `moments` is taken by PPL (`src/ppl/compiler/PplElaborate.fs:84`),
  hence `summarize`.

**Operations** (typed at inference, like `Dist`'s operator dispatch):

| Expression | Type rule | Meaning |
|---|---|---|
| `m * a`, `a * m` | a: dimensionless scalar | c ← a·c, Mₖ ← aᵏ·Mₖ |
| `m + b`, `b + m`, `m - b` | b: scalar in T's unit | c ← c + b |
| `mean(m)` | T | c + M₁/N |
| `variance(m)` | T² | M₂/N − (M₁/N)² |
| `stddev(m)` | T | √variance |
| `central(m, k)`, k ≤ r static | Tᵏ | Mₖ about the mean, via `shifted_from_raw` |
| `raw(m, k)`, k ≤ r static | Tᵏ | Σ xᵏ via `raw_from_shifted` |
| `shifted(m, k)`, k ≤ r static | Tᵏ | Mₖ, the tracked sum itself |
| `count(m)` | Int64 | N |

**Refusals.** One new code, **BL4022** (next free in the 4xxx band;
`src/Diagnostics.fs:289-391`). Its note names the reason:

- **`m * m`, `m ^ k`, `sqrt(m)`, or any other function applied to a
  `Moments` value.** The note names the degree and shows a witness:

  > each particle's update has degree 2, and no function of the first 2
  > moments gives the next ones: the ensembles (0, 3, 3) and (1, 1, 4) have
  > the same count and the same first 2 moments, but after the update their
  > 2nd moments are 162 and 258

  The note gives **no particle-count threshold.** An earlier draft said "once
  the ensemble has at least d·r particles". That is the Reals theorem's
  bound, and it is far from sharp: for x ↦ xᵈ the axiom-free bound is 2^r,
  the witnesses below have only r + 1 particles, and over ℝ the paper bound is
  r + 2 (§1, "Not proved"). N is usually a run-time count in any case, so the
  refusal does not depend on it.

  The special-parameter case `m + theta * m * m` is refused by the same rule,
  whatever `theta` is at run time. This refusal is conservative, not a
  theorem for every value: at `theta = 0` the update is affine and closes. The
  note therefore says "for every nonzero `theta`"
  (`closure_refused_R_polynomial`, with the constant a_d = θ).
- **`m + m2` for two `Moments` values.** That would be the elementwise sum of
  two ensembles, which moments do not determine. Pooling is a different
  operation (§4).
- **A Float coefficient that reads `mean(...)`, or contains a subtraction of
  moment-dependent terms.** No float bound exists (§1). The note names the
  offending subterm. Int is exact, so it is not refused.

**The witness table.** For every v1 order r ≤ 6 there is an *ideal*
Prouhet–Tarry–Escott pair: two r + 1-particle integer ensembles that agree on
p₀…p_r and first differ at p_(r+1). By `power_refused_translated`, one pair per
r serves **every** degree d ≥ 2, because r + 1 ≤ d·r.

| r | ensemble a | ensemble b | Δp_(r+1) |
|---|---|---|---|
| 1 | (0, 2) | (1, 1) | 2 |
| 2 | (0, 3, 3) | (1, 1, 4) | −12 |
| 3 | (0, 4, 7, 11) | (1, 2, 9, 10) | 720 |
| 4 | (0, 4, 8, 16, 17) | (1, 2, 10, 14, 18) | −25200 |
| 5 | (0, 5, 6, 16, 17, 22) | (1, 2, 10, 12, 20, 21) | 604800 |
| 6 | (0, 18, 19, 50, 56, 79, 81) | (1, 11, 30, 39, 68, 70, 84) | −36021585600 |

How the compiler uses it:
- **Find the differing moment.** Apply the update exactly (big integers) to
  both ensembles shifted by t. Try t = 0, 1, −1, 2, … and take the first t
  where some output moment j ≤ r differs.
  - For x ↦ xᵈ, the difference at order j is a nonzero polynomial in t of
    degree ≤ d·j, so at most d·j + 1 tries are needed (`many_roots_dom`).
  - For every row and d = 2…6, t = 0 already works. Checked by script
    2026-10-15, both over ℤ and mod 2⁶⁴.
- **Other fixed-coefficient polynomial updates** (`m + theta * m * m` with
  `theta` static) use the same search. Termination when the leading
  coefficient is nonzero is a paper argument (the 2026-10-15 review, §6.8),
  not mechanized.
- **For r > 6**, use Prouhet's pair at stage r + 1 (2^r particles), which
  the compiler can build on the fly (`pte`).
- **Corpus pins.** BL4022 tests pin the note with `// ERROR-CONTAINS:`, so the
  table is checked by the same harness that checks the code.

## 3. Semantics and numerical contract

A `Moments` value denotes the moments of an ensemble, so there is nothing to
preserve bitwise. No array exists to compare against.

**Int64 / Int32.**
- **Exact** under wrap-around arithmetic
  (`BladeNumericContract.wrapped_moment_reduction_sound`; the C++ lane wraps
  since `21eb0b5`).
- **Shift c = 0**, i.e. raw sums. This needs no division and no mean. A
  nonzero integer shift would only reduce magnitudes, and wrap-around already
  makes the result exact.
- `mean`/`variance` reproduce `stdlib/stats.blade` bit for bit on the particle
  twin (`T^1 -> T^0`: Int in, Int out, integer division).
  - `mean` = S div N.
  - `variance` = (Q − 2mS + N·m²) div N with m = S div N. That is
    stats.variance's Σ(x − m)² expanded, a ring identity, so it holds under
    wrap-around too.

**Refusals under wrap-around.** The positive half survives wrap-around. The
negative half does not: every refusal theorem assumes a characteristic-0
integral domain, and ℤ/2⁶⁴ has zero divisors. Some degree-2 updates really do
close there, at r = 2:
- `2^32 * x * x`: the output p₁ is 2³²·p₂, and the output p₂ is 2⁶⁴·p₄ ≡ 0.
- `x + 2^63 * x * x`: since (x + 2⁶³x²)² ≡ x², the output p₂ is the input p₂,
  and the output p₁ is p₁ + 2⁶³·p₂.

Over ℤ, neither closes (the witness distinguishes them). The 2026-10-15
script confirms both on sampled collisions. Int32 is the same story mod 2³².
So for Int, BL4022 is policy, not a theorem. The compiler decides which
note to give:
- **Static coefficients:** the compiler evaluates the witness (above) mod 2⁶⁴
  (or 2³²).
  - **Nonzero difference:** the refusal is proved for that program. The note
    is the same as for Float.
  - **Zero difference:** the update closes only because of wrap-around. v1
    still refuses, since that is almost certainly not what was meant. The note
    says so, instead of claiming that no closure exists.
- **Run-time coefficients:** refused, and the note says the refusal is
  conservative under wrap-around.
- Pure powers x ↦ xᵈ have leading coefficient 1, and the table's differences
  are nonzero mod 2⁶⁴ for r ≤ 6 and d ≤ 6. So the common case keeps a proved
  note.

**Float64 / Float32.**
- **The seed's error is mixed, not relative.** A two-pass seed has no
  relative error bound at a moment whose true value is 0: on
  [3, −10¹⁶, 10¹⁶] the binary64 mean is exactly 1, the true M₁ about it is
  0, and the computed M₁ is 2. Its error is a log-relative factor plus an
  absolute part, M̂ₖ = θ·Mₖ + e with \|e\| ≤ dₖ (`mcl`). Odd moments and
  anything near cancellation get such a dₖ.
- **Coefficients from literals, constants or step-indexed inputs:** relative
  error ≤ (1+e₀)(1+u)^(kT) − 1 on each Mₖ when the seed gives Mₖ to
  relative error e₀; from a mixed seed, relative radius ℓ₀ + T·k·λ plus an
  absolute part exp(T·k·λ)·\|a₁⋯a_T\|^k·dₖ (`mixed_seed_input_bound`) — the
  seed's absolute error is carried by the same factor that scales Mₖ.
- **Coefficients computed from `shifted`, `count`, `variance`, `stddev` of
  the same ensemble:** log-relative error ≤ the radius of §5.4 on the
  moments the coefficient reads; those, and only those, need a relative seed
  (`feedback_bound_read`). Every other moment keeps a mixed bound. For
  `variance`/`stddev`, M₁ may itself be seeded mixed: its absolute error
  enters the variance squared at an exact mean
  (`renormalized_by_stddev_mixed`). The compiler builds the error rule from
  the coefficient's syntax, and the program evaluates the radius at run time
  from a **certified upper bound** on ρ (§5.4), never from the raw ratio.
- **Normalization by `shifted(m, 2)`** (a = g/√M₂) does not accumulate:
  the float M₂ is within 6λ of the true M₂ = g² after every step, and the
  other moments' radii grow linearly in T (`normalized_m2_radius_constant`,
  `normalized_particles_linear`). Both runs stay in the domain (positive
  radicand, nonzero divisor) when the seed's M₂ > 0 and g ≠ 0
  (`normalized_ensemble_bound_dom`); the program checks M̂₂ > 0 at the
  seed, since a zero-spread ensemble gives inf/NaN in binary64 where the
  real model silently gives 0.
- **The mean** has one particle's accuracy in the original program.

**Both lanes** run the same arithmetic. `Moments` erases before IR (§5.3), so
the interpreter/codegen differential gates stay byte-identical. `where repro`
composes: it pins the operation order of that arithmetic.

**Units.** c and b carry T's unit, a must be dimensionless, and Mₖ carries
unitᵏ (`affine_update_unit_covariant`). All of this is checked on the typed
`Moments` value, before erasure throws the units away (§5.3). This is where
the type beats the `where` clause: a heterogeneous state gets a
heterogeneous type.

## 4. v1 scope, and what waits

| v1 | Later |
|---|---|
| scalar particles, `Moments<r, T>`, r ≤ 6 (PPL's `mstate` bound) | vector particles `Moments<r, T like I>` (comoments) |
| affine updates; Float coefficients from inputs, or from `shifted`/`count`/`variance`/`stddev` | `mean` in a coefficient (needs the coupled c/M bound); general subtraction |
| `summarize`, the accessors, the arithmetic of §2 | pooling `m1 ++ m2` (Chan/Pébay merge — PPL's `mstate_merge`); conversion to/from `Dist` |
| `let rec` over `Array<Moments<r, T> like Step>` | AD (`ad.*` refused with BL5500 and a note until Grad*.fs takes rank-2 carries and scalar×array broadcasts in reverse mode) |
| — | the `while` guard arm on a `Moments` recurrence |

## 5. Implementation hooks (verified against master `21eb0b5`)

**5.1 Type syntax.** Follow `Dist`, the closest precedent:
- **Parser:** a `Moments` arm in `ParserTypes.fs` next to `Dist`'s
  (`src/ParserTypes.fs:303`). It parses `Moments<order, Elem>`, with the order
  as a static expression and an optional `like` for later.
- **AST:** a `TyMoments` case beside `TyDist` (`src/Ast.fs:201`).
- **Checker type:** an `IRTMoments` next to `IRTDist` (`docs/features/ppl.md`
  §5 points at `src/Types.fs`).
- **Concrete-name lists:** `isConcreteTypeBaseName` (`src/TypeLower.fs:255`)
  gains `"Moments"`.

**5.2 Checking.** All `Moments`-aware checking happens during inference,
exactly like `Dist`:
- order equality in unification;
- the operator dispatch of §2 (`*`, `+`, `-` with one `Moments` operand);
- the accessors;
- BL4022, including the L-inference of §5.4 for Float coefficients.

`summarize(A, r)` is typed like `dist(A, r)` (`docs/features/ppl.md` §2.2).

**5.3 Erasure, and the `let rec` finding.** `Dist` erases at
`Zonk.zonkType` to a tuple of its component arrays. `Moments` cannot erase
to a tuple *inside a recursive array*. Running the compiler on a
`let rec s: Array<(Float64, Float64) like Step>` returns

    error[BL3999]: recursive array 's': only Float/Int/Complex element types
    are supported (record/tuple slices land with the IR-level alloc)

So erase `Array<Moments<r, T> like Step>` to a **rank-2 row**,
`Array<T like Step, Idx<r + 2>>`, holding (N, c, M₁…M_r). Rank-2
recursive arrays are supported (`tests/corpus/recursive-arrays/003`). A
scalar `Moments` outside a recursion erases to the same length-(r+2) row.

The row is homogeneous in T:
- **Float:** N is stored as a float, which is exact up to 2⁵³.
- **Int:** everything is Int already.
- **Units:** checked in §5.2, before erasure, so a unit-less row is safe.

The erased code is ordinary, so neither backend changes (`src/Optimize.fs:12-16`,
"TWIN-SAFE", holds by construction).

The arithmetic each operation erases to is exactly what the proofs count:

| Operation | Erases to |
|---|---|
| `m * a` | c ← fl(a·c); Mₖ ← fl(fl(aᵏ)·Mₖ), with aᵏ by repeated multiplication (`fpow`) |
| `m + b` | c ← fl(c + b) |
| `summarize` | the two-pass mean, then one fused pass |

**5.4 The error rule of a Float coefficient.** A syntactic pass over the
coefficient expression a builds a function φ(ℓ): moments known to
log-error ℓ give a to log-error φ(ℓ). Each rule is a proved lemma:

| Subterm | φ |
|---|---|
| constant or input | 0 |
| `shifted(m, k)`, `count(m)` | ℓ (the moments themselves) |
| `variance(m)` | DV(ρ, ℓ) = λ − ln Lo(λ+ℓ, 3λ+2ℓ, ρ) (`variance_rcl`) |
| `stddev(m)` | λ + DV(ρ, ℓ)/2 (`rcl_sqrt`, one rounding) |
| `x * y` | φx + φy (`rcl_mul`) |
| `x / y`, `1 / x` | φx + φy; a reciprocal keeps it (`rcl_inv`) |
| `x ^ p`, literal p | \|p\|·φx (`rcl_pow`) |
| `sqrt(x)` | φx / 2 (`rcl_sqrt`) |
| `x + y`, both non-negative by construction | max(φx, φy) (`rcl_add_pos`) |
| every floating operation | + λ (`rnd_rcl`) |

Anything else in a Float coefficient that reads a moment is refused with
BL4022: `mean`, a general subtraction, `raw`/`central` of order ≥ 3 (their
binomial expansions alternate in sign), or an intrinsic with no rule.

**The radius** iterates the rule: ℓ ← ℓ + r·(φ(ℓ) + λ), T times, from the
seed's error ℓ₀ (`grad`, `feedback_bound_inv`). With no variance in a,
φ is linear and the closed form of §1 applies. The radius is a conservative
worst-case upper bound. Only the moments a reads need ℓ₀ to be a relative
error (`feedback_bound_read`). With `variance`/`stddev` and an absolute M₁
seed error s·√(N·M₂), use `variance_rcl_mixed`'s rule:
DV = λ − ln(Lo(λ+ℓ, 3λ+2ℓ, ρ) − δ/(1−ρ)), δ = e^(3λ)(2e^ℓ·√ρ·s + s²),
with s growing as s₀·e^(ℓ−ℓ₀) (`renormalized_by_stddev_mixed`). For
a = g/√M₂ alone, report the non-compounding radius of §3 instead.

**ρ is a property of the data, so part of the radius is evaluated at run
time.**
- ρ = M₁²/(N·M₂) is fixed at the seed and exactly invariant afterwards
  (`rho_invariant`).
- After a two-pass seed, ρ ≈ (u·|mean| / spread)² (an estimate, not a
  theorem). It is negligible unless the spread is below the float resolution
  of the mean, where the variance is meaningless anyway.
- The theorems need ρ to bound the **true** ratio. The raw float ratio
  M̂₁²/(N·M̂₂) does not: on [0, 1] (true ρ = ½), M̂₁ = ½ and M̂₂ = 2 are both
  within log-error ln 2 and give 1/16 (`uninflated_rho_fails_true_invariant`).
  The program computes the **certified** bound
  ρ_c ≥ e^(2ℓ₁+ℓ₂)·(\|M̂₁\| + D)²/(N·M̂₂) — e^(3ℓ₀)·M̂₁²/(N·M̂₂) for a
  relative seed — rounded outward (`rho_certified_upper`; tight on [0, 1]:
  e^(3 ln 2)/16 = ½). D is M̂₁'s absolute seed error, which handles M₁ ≈ 0.
- **Admission** (O(1), float data only): M̂₂ > 0, ρ_c < 1, and Lo > 0 at the
  final radius evaluated with ρ_c — for a mixed M₁ seed also D² ≤
  s₀²·N·M̂₂·e^(−ℓ₀) and the δ-reduced Lo. This check implies `Inv_sd` for the
  true ensemble and the positive-Lo premise (`stddev_admission_sound`,
  `renormalized_by_stddev_admitted`, `renormalized_by_stddev_mixed_admitted`).
  With N the particle count the conclusion is about the particles'
  population variance at every step (`renormalized_particles_admitted`).
  The program records ρ_c and the radius in the run record.
- When the check fails at the horizon, the contract is vacuous: warn at run
  time. `blade plan` reports the rule and the radius at ρ = 0, a lower bound.

**5.5 `blade plan`.** Record an `Effects.Decision` (`src/Effects.fs:107`)
with rule `moments-summary` per `Moments` recurrence. Its Evidence:
- r and T;
- storage (the T·(r+2) row against T·N, when N is static);
- L and ε;
- the radius:
  - `exp(rad T) − 1`, evaluated with u = 2⁻⁵³ or 2⁻²⁴;
  - (1+u)^(rT) − 1 when L = 0;
  - "exact under wrap-around" for Int.

When the radius exceeds 1, **warn**: the contract is vacuous at that horizon.
The generic radius is a worst-case upper bound, so the warning says the
*guarantee* is gone, not that the run is wrong. Where a sharper instance is
proved (a = g/√M₂: constant 6λ on M₂, linear on the rest), report that one.

**5.6 Docs.**
- `docs/formalism.md` §2.4: value types, beside `Dist`.
- `docs/features.md` §16.1: the numerical contract, next to `fma` and
  `BLADE_FP_REASSOC`.
- `docs/features/ppl.md`: a cross-reference, since `Moments` and `Dist` share
  the scaling law.
- `CLAUDE.md` idiom table: "a particle ensemble you only observe through
  moments → `Array<Moments<r, T> like Step>`".

## 6. Tests

New directory `tests/corpus/moments/`. Register it in `Corpus.fs`, the
`CliSelfTests.fs` key map, and the interp slice (`tests/InterpDiff.fs`
`currentSlice`).

- **Float seed:** the 10⁸ / 10⁸+1 / 10⁸+2 seed, with variance pinned at
  0.6666666666666666 (the raw formula gives 2).
- **Seed obstruction:** [3, −10¹⁶, 10¹⁶]: `shifted(m, 1)` is 2 where the
  true value is 0 (`binary64_two_pass_seed_first_moment`); a `stddev`
  feedback from it is admitted through the mixed-seed check. Also pin an
  exact-zero odd moment, a sign change near zero, and a zero-spread ensemble
  (refused at admission: M̂₂ = 0).
- **Float recurrence** with `gain(n)`, `drift(n)`: a boolean pin comparing
  mean and variance against an explicit particle twin within the reported
  radius.
- **Feedback:** `prefix(n - 1) * (g / stddev(prefix(n - 1))) + drift(n)`,
  the case `renormalized_particles_admitted` proves. Exactly, the variance
  after every step is g². The pin checks it within the run-time radius.
  Also `prefix(n - 1) * (g / sqrt(shifted(prefix(n - 1), 2)))`: M₂ within
  6λ of g² at every step (`normalized_m2_radius_constant`).
- **Int64:**
  - raw sums exact, including a wrapped value (pairs with `functions/133`);
  - integer `mean` equals `stats.mean` on the particle twin.
- **Units:** `variance` of `Moments<2, Float64<meters>>` is `meters^2`; a
  `b` in the wrong unit is BL3006.
- **Refusals** (BL4022):
  - `m * m`;
  - `m + theta * m * m`;
  - `m + m2`;
  - a Float coefficient reading `mean(m)`;
  - a Float coefficient `variance(m) - 1.0`.
- **`ad.grad`** through a `Moments` recurrence: BL5500 with a note.

Beyond the corpus:
- **Accuracy harness:** a Python script using exact rationals, like the one
  in §1 (the 1000-particle experiment). It asserts the observed errors are
  inside the proved radius, for both coefficient regimes.
- **`blade test interp moments`** must be byte-identical.

## 7. Cost model

| | Particle array | `Array<Moments<r, T> like Step>` |
|---|---|---|
| storage | T·N | T·(r+2) |
| seed | — | O(N·r), two passes |
| per step | O(N) | O(r²) with `fpow`, O(r) with a running power |
| observation | O(N) per reduction | O(r) |

## 8. Milestones

- **M0 — type only.** Parse, check and erase `Moments<r, T>`, plus
  `summarize` and the accessors, with no `let rec`. Corpus: seeds, accessors,
  units, refusals.
- **M1 — Int64 recurrences.** Rank-2-row erasure inside `let rec`, the affine
  operations, exact pins.
- **M2 — Float recurrences with input coefficients.** The (1+u)^(kT) radius in
  `blade plan`; the accuracy harness.
- **M3 — feedback coefficients.** The error-rule pass of §5.4, BL4022 for the
  unbounded cases, the radius (closed form in `blade plan`, run-time
  evaluation with the certified ρ_c and the admission check of §5.4 when the
  variance is read), and the vacuous-contract warning.
- **M4 — docs and idiom table.**
- **Later.** Pooling, vector particles, `Dist` conversion, AD.

## 9. Open questions (none blocking M0)

1. **The name `summarize`.** It is chosen to avoid PPL's `moments`. The
   alternative is `Moments<r, T>(A)`, i.e. the type in call position, as
   numeric casts are spelled. That would be the first cast that does real
   work.
2. **Storing N as a float in the Float row.** It is exact to 2⁵³ particles.
   A separate Int64 recursive array for N is the alternative, but N never
   changes under the v1 operations, so it could even be a compile-time
   constant when `P` is static.
