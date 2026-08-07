# Plan: ppl as a proper probabilistic programming module

Status: PROPOSED (2026-08-05). Nothing implemented. Source of the driving
critique: the 2026-07-28 novelty assessment — "no sampling-based inference, no
prior/posterior modeling, no MCMC/variational engine, and no `sample`/`observe`
primitives — so it is not probabilistic programming in the WebPPL/Stan/Pyro
sense." This plan closes that gap without abandoning what makes the module
distinctive: the typed, exact, deterministic moment/cumulant algebra becomes
*one inference backend among several*, and samples round-trip into towers
through the existing `dist(A, r)` former.

## 0. Context — what exists and what is missing

The module today is a compile-time moment algebra, in two disjoint layers:

- **Compiler layer** (`src/ppl/compiler/PplElaborate.fs`, 2665 lines, wired at
  `src/TypeCheck.fs:12351`, codes BL5100): 23 formers — estimators
  (`moments`/`comoments`/`cumulants`/`mixed_cumulants`/`free_cumulants`),
  streaming monoids (`mstate`/`mstate_merge`/`mstate_cumulants`), the `Dist`
  tower (`dist`, `dist_add/scale/affine`, `+ - *` via `DistSynth`), Faà di
  Bruno pushforwards (`dist_jet*`, `dist_map*` with symbolic differentiation),
  tower Bayes (`dist_expect`/`dist_reweight`/`dist_mix` — **univariate only**,
  `univariateOnly` at PplElaborate.fs:1500), and quasi-distributions
  (`dist_atoms`/`dist_negativity`). Everything lowers to plain Blade source;
  `Dist<r,T>` is erased at `src/Zonk.fs:120-125` to a tuple of packed
  `SymIdx` cumulant arrays, so both backends (C++ and interp) come for free.
- **Reference prototype** (`src/ppl/*.fs`, standalone `MomentAlgebra.fsproj`,
  not referenced by `Blade.fsproj`): the independent oracle that generates the
  EXPECT pins (`dump-cumulants`/`dump-jet` verbs in `src/ppl/Program.fs`).

What is missing, concretely (verified 2026-08-05):

1. **No named distributions at the surface.** Gaussian/Exponential/Gamma/
   Poisson exist only as cumulant formulas in the unreferenced prototype
   (`src/ppl/Dist.fs:28-40`).
2. **No density, log-density, CDF, or quantile** anywhere.
3. **No sampling from any distribution object.** The only RNG is the separate
   `rand` module (keyed batch fills, uniform/normal only,
   `src/cpp/rand_runtime.hpp`).
4. **No `sample`/`observe`/model syntax**, no prior/posterior workflow beyond
   polynomial-likelihood `dist_reweight`.
5. **No inference engine** — no MH, HMC, VI, SMC, conjugate updates, or
   diagnostics.
6. **Univariate-only tower Bayes**; no multivariate conditioning even for the
   Gaussian (r=2) case where it is exact.
7. **No documentation** (no `src/ppl/README.md`, no `docs/features/ppl.md`;
   `ppl/NOTES.md` is referenced from `src/TypeCheck.fs:122` and
   `tests/corpus/ppl/019` but does not exist).

## 1. Design principles

- **Density-based (Stan-style), not trace-based (WebPPL/Gen-style).** A model
  is an ordinary named Blade function from latents to a `Float` log-density,
  assembled from log-prob formers. This needs no effect system, no program
  tracing, and rides `ad.grad` (`src/Grad.fs`) directly. Trace-based semantics
  is an explicit non-goal (§9).
- **Static shapes throughout.** Chain length, warmup, leapfrog steps, particle
  counts are compile-time ints — same regime as everything else in Blade.
  Data-dependent trip counts (NUTS tree doubling, adaptive rejection at the
  Blade level) are out of scope; rejection loops live *inside* C++ runtime
  primitives where they are legal.
- **Determinism via keys.** Every random former takes an explicit `Int64` key
  and is bit-reproducible across C++/interp, extending the `rand` contract
  (fresh `mt19937_64` per call, `mix64` finalizer). Subkey derivation is
  `mix64(key ^ site_constant ^ index)` — the seam `rand_runtime.hpp:16`
  already reserves for a future Philox backend.
- **Refuse rather than silently approximate.** Where the algebra is exact only
  under conditions (Gaussian conditioning at r=2, order budgets in reweight),
  violating the condition is a BL5100 error. Deliberately approximate
  constructs carry it in the name (`_approx`), mirroring the existing
  `dist_map` / `dist_map_closed` convention.
- **Source synthesis first.** Every feature that can be emitted as plain Blade
  source is (both backends free, per the sgs/spectra/ppl precedent). New IR
  reaches only where randomness must (new `__rand_*` intrinsics), following
  the exact `rand` template: TypeCheck arm (`src/TypeCheck.fs:3000-3018`),
  `Lowering.RandomInits` (`src/Lowering.fs:1821`), `CodeGen.genRandGenBinding`
  (`src/CodeGen.fs:12710`), bit-exact mirror in `src/Interp/RandMirror.fs`.
- **AD-able by construction.** Synthesized log-density code must stay inside
  the AD-able subset documented at `src/Grad.fs:27-50`: for-loop + `+=`
  accumulation, no if/match, no combinator computations, intrinsics limited to
  exp/log/sqrt/trig. This constrains *how* loglik loops are emitted (scalar
  accumulation loops, not `method_for` pipelines) and *which* families are
  HMC-able before new intrinsics land.

## 2. P1 — Named families, log-densities, and the approximate tower bridge

Pure algebra; no new runtime. All new formers join `formerNames`
(PplElaborate.fs:73) and are user-shadowable like the rest.

**Family constructors.** `ppl.gaussian(mu, s2)`, `ppl.exponential(rate)`,
`ppl.gamma(shape, rate)`, `ppl.poisson(lam)`, `ppl.uniform(a, b)`,
`ppl.beta(a, b)`, `ppl.bernoulli(p)`, `ppl.lognormal(mu, s2)`, and
multivariate `ppl.mvgaussian(mean, cov)`. A constructor application is a
*syntactic form* the elaborator recognizes (precedent: `dist_map` recognizes
lambda arguments):

- In **value position** — `let d = ppl.gaussian(mu, s2, r)` — it elaborates to
  the order-r cumulant tower (`__dist_pack`), formulas from the prototype's
  `Dist.fs:28-40`; parameters may be runtime scalars since cumulant formulas
  are ordinary arithmetic. Families with undefined higher cumulants at the
  requested order (future Student-t) refuse.
- In **argument position of a family-aware former** (`ppl.logpdf`,
  `ppl.observe`, `ppl.sample_*`, conjugate formers) it is used symbolically as
  (tag, param exprs) and never materializes a tower.

**Log-densities.** `ppl.logpdf(family(params), x)` → scalar;
`ppl.loglik(family(params), A)` → sum of per-sample logpdfs over the sample
axis (last declared index, the existing module contract), emitted as an
AD-able accumulation loop. Gamma/Poisson/Beta need `lgamma`, which does not
exist in the language: add it as a scalar intrinsic with a hand-rolled Lanczos
implementation in a runtime header **mirrored bit-exactly in F#** (the
Box-Muller precedent — `std::lgamma` vs `System.Math` would break the
byte-for-byte interp twin). Until it lands, ship Gaussian/Exponential/Uniform/
LogNormal logpdfs (lgamma-free) first.

**Approximate tower bridge** (the distinctive piece — pdf/quantiles for
empirical `Dist` towers, which have no exact density):

- `dist_pdf_approx(d, x)` — Edgeworth/Gram-Charlier expansion from κ₁..κ_r.
- `dist_quantile_approx(d, p)` — Cornish-Fisher.
- Pair both with `dist_negativity` in docs as the honesty check (Edgeworth
  densities can go negative; the module already measures exactly that).

**Oracle**: new prototype verbs `dump-logpdf`, `dump-edgeworth`,
`dump-cornish-fisher` in `src/ppl/Program.fs` generate the EXPECT pins.

## 3. P2 — Sampling primitives

Extends the `rand` runtime; this is the phase that touches C++ and the interp
mirror.

**New batch fills in `src/cpp/rand_runtime.hpp`** (each keyed, stateless,
rejection loops legal here): `exponential(rate)`, `gamma(shape, rate)`
(Marsaglia–Tsang; the rejection loop consumes a deterministic mt19937_64
stream, so the F# mirror stays bit-exact), `poisson(lam)`,
`beta(a, b)` (two gammas), `bernoulli(p)`, `categorical(weights, n)` (returns
`Int64` indices; needed later by SMC resampling and discrete latents). Each
gets: a `__rand_<fam>` intrinsic (TypeCheck arm, Lowering, CodeGen per the
`rand` template above), a `RandMirror.fs` mirror, and 3–4 deterministic corpus
pins in `tests/corpus/rand/`.

**ppl surface.** `ppl.sample(family(params), key, n)` (and shaped variants)
elaborates directly to the `__rand_*` intrinsics — legal without `import rand`
because the intrinsics are checker-level builtins, and elaboration order
(PPL at `TypeCheck.fs:12351` before rand at `:12357`) is irrelevant to them.
`ppl.mvgaussian` sampling = normal fill + Cholesky transform (synthesized
source; refuse non-PD at runtime via the existing panic path).

**Tower sampling.** `dist_sample_approx(d, key, n)` — Cornish-Fisher transform
of a uniform fill; pure synthesized source over the existing
`__rand_uniform`, so it can land before the new C++ fills.

**Key discipline.** `ppl.key(key, i)` = `mix64`-based subkey former, and every
synthesized multi-draw site derives per-site constants automatically, so
chains and plates never reuse a stream. Document the convention in the P6 doc.

**Grad boundary unchanged**: `__rand_*` output stays non-differentiable
(`src/rand/compiler/RandElaborate.fs:15-16`). The reparameterization pattern —
draw noise outside the differentiated function, transform inside — is the
documented idiom and is exactly what P5's ADVI uses.

## 4. P3 — The model layer (`sample`/`observe` in density form)

- `ppl.lp(family(params), x)` — one log-prob term (prior or likelihood alike;
  a "sample statement" in density form).
- `ppl.observe(family(params), A)` — sugar for the summed loglik of a data
  array; multiple observes in one function just add.
- A **model** is a named function `function logpost(theta...) -> Float`
  whose body sums `ppl.lp`/`ppl.observe` terms. No parser changes, no new
  types; the inference formers of P4 take the function *name* (precedent:
  `dist_map` resolves and inlines named same-module functions,
  PplElaborate.fs:1793).
- **Transforms with log-Jacobians**, for unconstrained sampling:
  `ppl.tr_log`/`ppl.tr_logit`/`ppl.tr_simplex`/`ppl.tr_cholcorr`, each a pair
  of synthesized AD-able functions (forward transform, log|J|). Composition is
  ordinary function composition in the model body.
- **Forward simulation** needs no feature: a generative function is ordinary
  code calling `ppl.sample` with split keys. Add corpus examples, not
  machinery.
- Optional later sugar: a `~`-block former rewriting to the above. Parser
  work; explicitly deferred.

## 5. P4 — Sampling inference: MH, HMC, diagnostics, and the round-trip

**Chains are recursive arrays** — Blade's sole sequential construct is exactly
a Markov chain: `chain[t] = step(chain[t-1], subkey(t))`, constant-stack by
TRMC, statically sized.

- `ppl.mh(logpost, init, n, scale, key)` — random-walk Metropolis. The
  accept/reject branch is a *conditional expression* inside the step (legal;
  only grad-side if/match is restricted, and the sampler loop is never
  differentiated).
- `ppl.hmc(logpost, init, n, eps, L, key)` — fixed-leapfrog HMC. Gradient via
  the `ad` module: the elaborator emits the `ad.grad(logpost)` surface form.
  Gate: require `import ad` in the program (BL5100 steering error if absent)
  — or extend Grad's import gate to accept an elaborator-planted marker; pick
  whichever is smaller when implementing. HMC coverage is bounded by the
  AD-able subset (`src/Grad.fs:27-50`); P2/P3 emit log-densities inside it by
  construction, and the corpus must include one deliberate
  "model outside the subset → clean BL5500/BL5100 refusal" probe.
- Warmup/thinning as static slicing formers (`ppl.warmup(chain, w)` etc.);
  step-size adaptation, if wanted, as a fixed-length dual-averaging warmup
  schedule (plain arithmetic per iteration) — NUTS stays out of scope.
- **Diagnostics are moment algebra**: `ppl.ess(chain)` (autocovariance via
  the existing estimator machinery), `ppl.rhat(chains)` (split-R̂),
  `ppl.autocorr(chain, maxlag)`. Natural reuse of `Streaming`-style plans.
- **The round-trip that unifies the module**: `dist(samples, r)` (already
  shipped) lifts any chain back into a `Dist` tower, so posterior expectations,
  jet pushforwards, and mixing all reuse the existing algebra. Corpus test:
  conjugate posterior computed exactly (P5) vs `dist(ppl.mh(...), 2)` within
  tolerance — expressed as deterministic booleans since keys are fixed (the
  `tests/corpus/rand` idiom).

## 6. P5 — Exact and moment-native inference (the distinctive backend)

- **Conjugate updates** as closed-form posterior formers:
  `ppl.bayes(gaussian(mu0, s0), gaussian_lik(s), A)` → Gaussian posterior;
  Beta–Bernoulli, Gamma–Poisson, Normal–InverseGamma likewise. Pure source
  synthesis; oracle verb `dump-conjugate`.
- **Multivariate Gaussian conditioning**: `dist_condition(d, idxs, values)` —
  Schur complement on the κ₂ block. Exact only at order 2: refuse (BL5100)
  for r > 2 rather than truncate, per module convention.
- **Lift `univariateOnly` (PplElaborate.fs:1500)**: multivariate
  `dist_expect`/`dist_reweight`/`dist_mix` with multi-index polynomial
  likelihoods; the order-spending account generalizes by total degree.
- **Laplace approximation**: `ppl.laplace(logpost, mode_init, ...)` — mode by
  a fixed number of gradient steps (`ad.grad`), Hessian via the module's
  existing jet machinery or forward-over-reverse; result is a Gaussian `Dist`
  tower. Bridges models → towers without sampling.

## 7. P6 — Optional/advanced engines (each independently shippable)

- **ADVI**: mean-field Gaussian q; reparameterized ELBO gradient (noise drawn
  outside `ad.grad`, affine transform inside — compatible with the rand/grad
  boundary as-is); fixed-iteration SGD loop as a recursive array. Check
  `src/ml` for an existing optimizer loop to reuse before writing one.
- **SMC / particle filter** for state-space models: recursive array over time,
  `categorical` resampling (P2), streaming-weight normalization. Pairs
  naturally with `mstate` for online summaries.
- **Expectation propagation**: iterated Gaussian moment-matching over factor
  lists — the moment algebra is literally the substrate; scope the first
  deliverable to factorized likelihoods over `mvgaussian` priors.
- **Discrete-latent enumeration**: exact marginalization over small static
  supports (weights via `ppl.lp`, normalize, `dist_mix` the branches — mostly
  existing pieces).
- **Posterior predictive checks**: forward simulate from posterior draws,
  compare moment towers — pure composition of P2–P5; ship as examples + a
  `ppl.ppc_pvalue` former if a former earns its keep.

## 8. Cross-cutting work items

- **`lgamma` intrinsic** (P1): hand-rolled Lanczos in a runtime header + F#
  mirror; both sides bit-exact. Blocks Gamma/Poisson/Beta densities.
- **Docs** (start in P1, consolidate at the end): `docs/features/ppl.md` (the
  full surface: formers, key discipline, exactness/approximation ledger),
  `src/ppl/README.md` (two-layer architecture, oracle workflow), and create
  the missing `ppl/NOTES.md` or repoint `src/TypeCheck.fs:122` and corpus 019.
- **Diagnostics**: continue in the BL5100 block (`src/Diagnostics.fs:180`);
  every refusal named in this plan gets a specific message + a `(rejects)`
  corpus probe.
- **Tests**: continue `tests/corpus/ppl/` numbering from 070; new `__rand_*`
  pins go in `tests/corpus/rand/`. If the category passes ~120 files, split a
  `ppl-infer` key at `src/Cli.fs:1399-1437` (3-line change + `Test_*.fs` +
  `RunAll.fs`). Statistical assertions are deterministic booleans (fixed
  keys), never tolerance-free floating pins.
- **Oracle**: every new exact former gets a `dump-*` verb in
  `src/ppl/Program.fs`; the prototype's duplicated algebra is deliberate
  (independent oracle) — keep it that way.
- **Interp slice**: consider adding `ppl` to `InterpDiff.currentSlice`
  (`tests/InterpDiff.fs:192`) once samplers land, since sampler formers are
  the first ppl code where a C++/interp divergence could hide.

## 9. Non-goals (explicit)

- Trace-based/effect-handler PPL semantics (Gen/WebPPL-style program traces).
- NUTS or any data-dependent trip count at the Blade level.
- Dynamic model structure (open-universe, RJ-MCMC), stochastic control flow
  over latents beyond static enumeration.
- GPU inference; distribution families with undefined moments as tower values
  (Cauchy, low-ν Student-t) — they may appear later behind logpdf/sample only.

## 10. Verification

Per phase: `blade test ppl` (and `blade test rand` for P2) after each former
lands — remember the msys64 ucrt64 PATH requirement for compiled runs. Oracle
pins come from `dotnet run` on `MomentAlgebra.fsproj` dump verbs. End-to-end
acceptance, in order of increasing machinery:

1. P1: `ppl.logpdf(gaussian(0,1), 0)` pins `-0.9189385...`; Edgeworth pdf of
   an empirical exponential tower matches the `dump-edgeworth` oracle.
2. P2: keyed `ppl.sample(gamma(3,2), k, n)` byte-identical between compiled
   binary and `blade run` interp; moments of the fill match `dist` estimates.
3. P3+P4: 8-schools-shaped hierarchical Gaussian model — `ppl.mh` and
   `ppl.hmc` chains, `ppl.rhat` < 1.01 as a deterministic boolean, posterior
   mean via `dist(chain, 2)` within fixed tolerance of the P5 conjugate
   answer.
4. P5: conjugate vs `dist_reweight` cross-check where both apply (polynomial
   likelihood ∩ conjugate family); `dist_condition` vs the prototype's
   `affine`+Schur oracle.
5. Full suite green (`blade test`) with zero regressions in the existing 69
   ppl corpus files.
