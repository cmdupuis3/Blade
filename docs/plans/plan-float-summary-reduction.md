# `Moments<r, T>` — ensembles observed through their moments

**Status 2026-09-23: PROPOSED, nothing built.** The mathematics is proved
(§1):
- the exact reduction in shifted coordinates, over any commutative ring,
  with the exact invariance of ρ = M₁²/(N·M₂)
  (`proofs/BladeShiftedMoments.v`, 21 theorems, no axioms);
- its floating-point bound with coefficients from the input
  (`proofs/reals/BladeShiftedRounding.v`, 16);
- its floating-point bound with coefficients **computed from the summary
  itself** (`proofs/reals/BladeShiftedFeedback.v`, 36), including the
  **variance and standard deviation** in a coefficient
  (`proofs/reals/BladeShiftedVariance.v`, 14);
- binary64 witnesses (`proofs/floats/BladeBinary64Witness.v`).

The reals files are conditional on Coq's real-number axioms and state the
rounding model as a hypothesis.

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
| Float, **coefficient computed from the rounded moments**: log-relative error ≤ rad T, with rad T = (1+rL)^T·ℓ₀ + r(ε + λ)·((1+rL)^T − 1)/(rL), where λ = −ln(1−u); linear in T when L = 0 | `BladeShiftedFeedback.feedback_particles`, `rad_closed`, `rad_no_feedback` |
| The stability constant L for the coefficients people write: products add, powers scale by \|p\|, reciprocals keep, square roots halve, sums of non-negative terms take the max | `rcl_mul`, `rcl_pow`, `rcl_inv`, `rcl_sqrt`, `rcl_add_pos` |
| Worked instance: renormalizing by the tracked sum, a = fl(g / fl(√M₂)), gives L = ½, ε = 2λ | `normalizing_coefficient_stable`, `normalized_ensemble_bound` |
| **Subtraction with a dominant minuend:** 0 ≤ z ≤ ρ·y, ρ < 1, operands at log-errors ℓ_y, ℓ_z → y − z at log-error −ln Lo, Lo = (e^(−ℓ_y) − e^(ℓ_z)·ρ)/(1 − ρ), while Lo > 0 | `BladeShiftedVariance.rcl_diff_dominant` |
| ρ = M₁²/(N·M₂) is exactly invariant under affine steps; N·M₂ − M₁² does not depend on the shift | `BladeShiftedMoments.rho_invariant`, `variance_numerator_shift_free`, `variance_numerator_aff` |
| The float variance fl(fl(M₂/N) − fl(q·q)), q = fl(M₁/N), from moments at log-error ℓ: log-error DV(ρ, ℓ) = λ − ln Lo(λ+ℓ, 3λ+2ℓ, ρ) | `variance_rcl` |
| The feedback theorem for a general (non-linear) error rule φ and an invariant of the true run, while the radius stays where φ is valid; instance: renormalizing by the float standard deviation | `feedback_bound_inv`, `renormalized_by_stddev_bound` |
| A degree ≥ 2 particle update has **no** summary of this kind at all | `BladeRealDensity.closure_refused_R_polynomial` |
| Raw power sums are the wrong representation in floats (variance 2 instead of 2/3 at 10⁸, 10⁸+1, 10⁸+2) | `binary64_raw_moments_lose_the_variance` |

**The feedback bound grows geometrically, at rate 1 + rL per step.** That is
the real price of a coefficient that reads rounded data, not an artefact of
the proof: a coefficient that reads the variance amplifies errors in the
variance. `blade plan` must report the number (§5.5).

**Not proved:**
- A coefficient that reads the **mean** (the shift c). Its error is coupled
  to b.
- A coefficient containing a **general subtraction** of moment-dependent
  terms. Only the dominant-minuend case is proved, which is what the variance
  needs.
- AD through a `Moments` recurrence.
- The link to `src/`, as for every row of `docs/proofs.md`.

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
  `Moments` value.** The note: "each particle's update is not affine; no
  function of the first r moments gives the next ones once the ensemble has at
  least d·r particles" (`closure_refused_R_polynomial`), naming the degree.
  The special-parameter case `m + theta * m * m` is refused by the same rule
  whatever `theta` is at run time.
- **`m + m2` for two `Moments` values.** That would be the elementwise sum of
  two ensembles, which moments do not determine. Pooling is a different
  operation (§4).
- **A Float coefficient that reads `mean(...)`, or contains a subtraction of
  moment-dependent terms.** No float bound exists (§1). The note names the
  offending subterm. Int is exact, so it is not refused.

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

**Float64 / Float32.**
- **Coefficients from literals, constants or step-indexed inputs:** relative
  error ≤ (1+e₀)(1+u)^(kT) − 1 on each Mₖ, where e₀ is the error of the
  two-pass seed.
- **Coefficients computed from `shifted`, `count`, `variance`, `stddev` of
  the same ensemble:** log-relative error ≤ the radius of §5.4. The
  compiler builds the error rule from the coefficient's syntax, and the
  program evaluates the radius at run time from the seed's ρ.
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
φ is linear and the closed form of §1 applies.

**ρ is a property of the data, so part of the radius is evaluated at run
time.**
- ρ = M₁²/(N·M₂) is fixed at the seed and exactly invariant afterwards
  (`rho_invariant`).
- After a two-pass seed, ρ ≈ (u·|mean| / spread)². It is negligible unless
  the spread is below the float resolution of the mean, where the variance is
  meaningless anyway.
- The rule needs Lo > 0 at the final radius. The program therefore computes ρ
  from the seed row (O(1)), evaluates the radius, and records both in the
  run record.
- When Lo ≤ 0 at the horizon, the contract is vacuous: warn at run time.
  `blade plan` reports the rule and the radius at ρ = 0, a lower bound.

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
That is the honest consequence of feedback, and the user should see it.

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
- **Float recurrence** with `gain(n)`, `drift(n)`: a boolean pin comparing
  mean and variance against an explicit particle twin within the reported
  radius.
- **Feedback:** `prefix(n - 1) * (g / stddev(prefix(n - 1))) + drift(n)`,
  the case `renormalized_by_stddev_bound` proves. Exactly, the variance after
  every step is g². The pin checks it within the run-time radius.
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
  evaluation with the seed's ρ when the variance is read), and the
  vacuous-contract warning.
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
