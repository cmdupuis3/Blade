# Blade Feature Module: Probabilistic Programming (`ppl`)

Status: **implemented and corpus-tested** (69 files in `tests/corpus/ppl/` plus
two worked examples, `examples/02_portfolio_moments.blade` and
`examples/05_streaming_telemetry.blade`) as a **compile-time moment/cumulant
algebra** — but it is NOT yet a sampling-based probabilistic programming
system in the WebPPL/Stan/Pyro sense. There are no named distribution objects
at the surface, no density/log-density, no `sample`/`observe` primitives, and
no inference engine (MH/HMC/VI/SMC). See the Roadmap section at the bottom for
what closing that gap looks like — none of it is implemented yet. This
document is the canonical description of the surface that DOES exist today.

Design stance: every `ppl` former elaborates to ordinary Blade source —
loop-object pipelines (`method_for` / `<@>` / `reduce`) and straight-line
scalar arithmetic — **before type checking**, in
`src/ppl/compiler/PplElaborate.fs` (2665 lines), wired into the pipeline at
`src/TypeCheck.fs:12393` (between `sgs` and `math` elaboration, all under
`checkModule`). Both backends (C++ codegen and the interpreter) get the whole
module for free with zero new runtime machinery — no new IR nodes, no new
intrinsics. Elaboration failures surface as a single generic error code,
`BL5100`.

```blade
import ppl as ppl
type TimeIdx = Idx<6>
let A: Array<Float64 like Idx<1>, TimeIdx> = [[1.0, 2.0, 4.0, 6.0, 0.0, 3.0]]
let mu = ppl.moments(A, 1)          // mean
let d  = ppl.dist(A, 4)             // order-4 cumulant tower
let y  = ppl.dist_map(d, 2, lambda(x) -> x * x)   // exact pushforward through x -> x^2
```

---

## 1. Import gating

Only `import ppl [as <alias>]` is legal. `alias` defaults to `"ppl"` when
omitted. A selective import —

```blade
from ppl import moments   // ERROR
```

— is a compile error: *"`ppl` supports only `import ppl [as <alias>]`; a
selective `from ppl import ...` would reintroduce global names"*
(`pplAliasesOf`, PplElaborate.fs ~2246-2256). The module is qualified-only by
design: every former is reached as `alias.former(...)`, never as a bare
top-level name, so it can never silently collide with a user identifier that
happens to share a former's short name.

The elaborator strips the qualification before the former-recognition pass
runs (`stripQualified`, PplElaborate.fs ~2262): `alias.moments(...)` becomes
the internal bare `moments(...)` node, `alias.cumulant(d, k)` becomes the
internal marker `__ppl_cumulant(d, k)` (consumed later by the type checker,
see §5), and `alias.indep(a, b)` in a `where`-clause becomes `__ppl_indep(a,
b)`. A former written unqualified (bare `moments(...)` with no `import ppl`
in scope) never resolves — the module is import-gated, not language-wide.

**User definitions shadow the formers entirely** — the literal rule, shared
verbatim with the `ml` and `math` elaborators (PplElaborate.fs:2378): if the
enclosing module declares a top-level `function moments(...)`, the `moments`
former is deactivated project-wide in that module and every `moments(...)`
call resolves to the user's function instead. The elaborator computes this by
collecting all `DeclFunction` names in the module (`declNames`) and gating
each former's recognition on `not (Set.contains name declNames)`.

## 2. Former reference

23 formers total (`formerNames`, PplElaborate.fs:73), each of which must be
the **entire right-hand side of a top-level `let`** (with the sole exception
of the checker-level `cumulant` projection, §5, which can appear anywhere a
Dist-typed value is in scope, including inside function bodies). A stray
former reference anywhere else — inside an arbitrary expression, as a
sub-term of a larger call — is rejected in a dedicated misplaced-former pass
(PplElaborate.fs ~2609-2619).

### 2.1 Estimators

Single-sweep, fused sufficient-statistic pipelines over a compile-time-shaped
array. The **last declared index of the source array is always the sample
(fiber) axis**; its static extent is `N`, the estimator's normalizer.

| Former | Signature (informal) | What it computes |
|---|---|---|
| `moments(A, k)` | `Array<T like I..., N> -> Array<T like SymIdx<k, D>>` | raw (non-central) order-`k` comoment tensor, packed symmetric over the fused leading axes |
| `comoments(A, 2)` | same | central pair comoment (covariance); orders > 2 deferred to the subset-lattice expansion |
| `comoments(X, Y)` | rectangular | central cross-covariance block between two arrays (no `comm` clause — not symmetric) |
| `cumulants(A, k)` | | joint cumulant tensors 1..k via Möbius inversion over the set-partition lattice (Bell(k) partitions) of the raw power sums |
| `mixed_cumulants(A, B, p, q)` | | the `(p, q)` joint-cumulant block between two named sources (`A`-slots major, `B`-slots inner); structurally zero at every order for a declared-independent pair |
| `free_cumulants(A, k)` | | non-crossing-partition-lattice cumulants (free probability, as opposed to `cumulants`' classical/all-partitions lattice) |

`moments(A, k)` elaborates (as the doc comment at PplElaborate.fs:8-11
states) to exactly:

```
method_for(A, ..., A) <@> lambda(x1..xk)
  where comm(x1..xk) -> prodsum(x1..xk)/N
|> compute
```

Example (`tests/corpus/ppl/001_moments_pair.blade`):

```blade
import ppl as ppl
type TimeIdx = Idx<2>
let A: Array<Float64 like Idx<2>, TimeIdx> = [[1.0, 2.0], [3.0, 4.0]]
let m2 = ppl.moments(A, 2)
// EXPECT: m2 = [2.5, 5.5, 12.5]
```

`comoments(A, 2)` also has a **reconstruction** mode: `moments(d, k)` on a
`Dist` binding rebuilds raw moments from the carried cumulants (partition
sums, oversized blocks contributing zero — the Isserlis closure at order 4
from an order-2 dist). See `examples/05_streaming_telemetry.blade` part (e).

Tests: `ppl/001-010`, `ppl/027-029`, `ppl/031`, `ppl/065`, `ppl/068-069`.

### 2.2 Independence and the Dist tower

| Former | What it does |
|---|---|
| `independent(X, Y)` | declares a module-level independence fact: `let _ = ppl.independent(X, Y)`; the pair's subsequent `comoments(X, Y)` elaborates to a literal zero block instead of the cross computation (exact for central pair comoments) |
| `dist(A, r)` | packs `A` into an order-`r` `Dist<r, Elem like axes>` tower (the checker-level nominal type, §5) |
| `dist_add(d1, d2)` / `+` | tensor addition of cumulants — exact **only** for independent operands |
| `dist_scale(c, d)` / `*` | `kappa_k` scales by `c^k` (pure multilinearity — no independence needed, works even on Dist-typed function parameters with unknown provenance) |
| `dist_affine(W, d)` | exact linear pushforward `Y = WX`: `kappa'_r` contracts with `W^(⊗r)`, transporting ALL carried orders in one shot, no re-pass over samples |

Two independence-checking regimes coexist, matching the module's two layers
(§5): a **module-level `let`** combination checks declared independence
against the elaboration-time registry built from `independent(...)`
declarations; a combination **inside a function body**, where a `Dist`-typed
parameter's provenance is invisible to elaboration, is checked by the type
checker against `where <alias>.indep(a, b)` **licenses** on the function's
signature (see §3 below).

Estimator/Dist-tower example combining both
(`examples/02_portfolio_moments.blade`, part (e)):

```blade
let _ = ppl.independent(X, Y)
let dx = ppl.dist(X, 3)
let dy = ppl.dist(Y, 3)
let port = 0.6 * dx + 0.4 * dy      // module-level: license from `independent`
let c1 = ppl.cumulant(port, 1)
let c2 = ppl.cumulant(port, 2)

function combine(a: Dist<3, Float64 like Idx<2>>, b: Dist<3, Float64 like Idx<2>>)
  where ppl.indep(a, b) -> Dist<3, Float64 like Idx<2>> =
  a + b                              // function body: license from `where`

let total = combine(dx, dy)
```

Tests: `ppl/011-024`, `ppl/030`, `ppl/032`, `ppl/047`.

### 2.3 Streaming monoids

Arbitrary-order, mergeable central-comoment accumulators — the
multivariate generalization of Welford's algorithm (Pébay's formulas),
derived once per `(d, r)` at elaboration time as straight-line code, not
hand-coded per order.

| Former | What it does |
|---|---|
| `mstate(A, r)` | compresses `A` into a fixed-size sufficient-statistic state: `(n, mean, central comoment sums M_2..M_r)` |
| `mstate_merge(sA, sB)` | combines two states by the derived subset-lattice kernels; associative and commutative — a true monoid, so chunking/grouping order never matters |
| `mstate_cumulants(s)` | freezes a state into `kappa_1..kappa_r` (singleton-pruned partition formula; `kappa_1` is the mean) |
| `comoments_merge(c1, m1, n1, c2, m2, n2)` | closed-form pooled-covariance merge, `C = (n_A C_A + n_B C_B)/n + (n_A n_B / n^2) dd^T` — the `k = 2` specialization, usable without going through `mstate` |

Example (`tests/corpus/ppl/025_mstate_merge.blade`):

```blade
let sA = ppl.mstate(XA, 4)
let sB = ppl.mstate(XB, 4)
let s = ppl.mstate_merge(sA, sB)
let (k1, k2, k3, k4) = ppl.mstate_cumulants(s)
```

The full three-shard collector-with-merge pipeline, cross-checked against the
batch cumulants of the concatenated stream, is
`examples/05_streaming_telemetry.blade` parts (a)-(c).

Tests: `ppl/025-026`.

### 2.4 Faà di Bruno pushforwards

Exact polynomial (or truncated-jet) pushforward of a `Dist` through a smooth
map, via symbolic differentiation of the map at elaboration time.

| Former | What it does |
|---|---|
| `dist_jet(d, q, g0, D1, ..., Ds)` | hand-supplied jet form: `g0 = g(mu)` and the rank-k symmetric derivative tensors `D_k = g^(k)(mu)` as trailing array-literal arguments; needs input moments up to order `q*s` — **refuses** (BL5100, "insufficient stochastic order") if `d`'s carried order is short |
| `dist_jet_closed(...)` | same, but silently zero-fills cumulants beyond the carried order instead of refusing (the `moments(d,k)` closure convention) |
| `dist_map(d, q, lambda(x) -> ...)` | the symbolic front-end: differentiates the lambda itself (product rule; degree bounded by the module's generation limit), evaluates `g` and its derivatives at the runtime-read mean, and delegates to `dist_jet` |
| `dist_map_closed(...)` | `dist_map`'s closed-budget sibling |

Vector-valued forms exist for all four (`dist_jet` on a `[g0...]` /
`[[D1..],...]` argument list; see `ppl/058-064`), producing a **joint**
output `Dist` over multiple output coordinates.

Example (`tests/corpus/ppl/039_dist_map.blade`, matching the hand-jetted
`ppl/033`):

```blade
let xd = ppl.dist(A, 6)
let dy = ppl.dist_map(xd, 3, lambda(x) -> x * x)
let y1 = ppl.cumulant(dy, 1)   // EXPECT: [11.0]
let y2 = ppl.cumulant(dy, 2)   // EXPECT: [154.0]
let y3 = ppl.cumulant(dy, 3)   // EXPECT: [2178.0]
```

The order-budget refusal is elaboration-time `BL5100`
(`tests/corpus/ppl/037_dist_jet_order_guard.blade`, `042`, `062`, `064`) —
distinct from the checker-level `BL3007` order guard on `cumulant(d, k)`
projection (§5); `dist_map`/`dist_jet` operate on the elaboration-time
`DistInfo` registry, not the typed `Dist<r,T>` value.

Tests: `ppl/033-042`, `ppl/053-064`, `ppl/066-067`.

### 2.5 Tower Bayes (univariate only)

```
dist_expect(d, c0, ..., cq)    E[c0 + c1 X + ... + cq X^q]: a scalar.
                                Model-evidence / normalizer primitive.
dist_reweight(d, c0, ..., cq)  the tower of X under the reweighted law
                                dm' = (c0 + ... + cq x^q) dm / Z --
                                Bayes with a polynomial likelihood.
dist_mix(w1, d1, w2, d2)       the normalized mixture (w1 m1 + w2 m2)
                                / (w1 + w2). No independence demanded:
                                mixing is always lawful.
```

(Doc block, PplElaborate.fs:1437-1461.) Conditioning on a finite-support
variable is `dist_reweight` by its Lagrange indicator polynomial;
disintegrate-then-`dist_mix` is the law of total probability; sequential
Bayes is chained `dist_reweight`.

All three are gated `univariateOnly` (PplElaborate.fs:1498-1501): calling any
of them on a `Dist` whose declared/inferred dimension is greater than 1
refuses with *"univariate dists only so far — marginalize or push forward
(`dist_map`/`dist_affine`) first, then condition"*. Multivariate tower Bayes
(exact Gaussian conditioning at `r=2` in particular) is unimplemented — see
Roadmap P5.

`dist_reweight` is **order-accounted**: a degree-`q` weight polynomial spends
`q` orders of the prior tower (`rOut = info.Order - q`), and the call refuses
(BL5100) if that leaves `rOut < 1` — see §4 below.

Example (`tests/corpus/ppl/045_dist_reweight_bayes.blade` — a runtime-centered
quadratic likelihood, prior over `{0, 1, 2}`):

```blade
let vhat = 1.6
let d = ppl.dist(A, 4)   // order-4 prior
let evidence = ppl.dist_expect(d, 1.0 - 0.2*vhat*vhat, 0.4*vhat, 0.0 - 0.2)
let post = ppl.dist_reweight(d, 1.0 - 0.2*vhat*vhat, 0.4*vhat, 0.0 - 0.2)
// degree-2 weight: order-4 prior -> order-2 posterior
let p1 = ppl.cumulant(post, 1)
let p2 = ppl.cumulant(post, 2)
// EXPECT: evidence = 0.794666666666667
// EXPECT: p1 = [1.20134228187919]
// EXPECT: p2 = [0.570199540561236]
```

Tests: `ppl/043-048`.

### 2.6 Quasi-distributions

```
dist_atoms(r, x1, w1, ..., xk, wk)  the order-r tower of the atomic
                                     measure sum_i w_i delta(x_i),
                                     normalized by sum w_i. Weights may
                                     be negative (non-classical towers,
                                     negative variance included, are
                                     carryable values).
dist_negativity(d, x1, ..., xs)     the L1 negativity of d read as a
                                     quasi-distribution on the claimed
                                     support {x_1..x_s}: cells by
                                     Lagrange indicators (exact when
                                     s - 1 <= carried order), N = sum
                                     max(0, -cell). Zero iff the tower
                                     is a genuine probability on that
                                     support.
```

(Doc block, PplElaborate.fs:1602-1617.) `dist_atoms` accepts a **negative**
weight — a `Dist` value can carry negative variance as a first-class value;
`dist_negativity` is the honesty meter for exactly that. Example
(`tests/corpus/ppl/050_negative_variance_tower.blade`, the Tsirelson-bound
CHSH marginal — a two-atom quasi-distribution with one negative weight):

```blade
let ts = ppl.dist_atoms(2, 2.0, 1.20710678118655, -2.0, -0.207106781186548)
let t2 = ppl.cumulant(ts, 2)               // EXPECT: [-4.00000000000001] -- negative variance
let tneg = ppl.dist_negativity(ts, -2.0, 2.0)
// EXPECT: tneg = 0.207106781186548
```

Tests: `ppl/049-052`.

---

## 3. Independence licensing (`where indep`)

Cumulant addition (`dist + dist`, and by extension `dist - dist`) is exact
**only** for independent operands — convolution becomes tensor addition of
cumulants precisely because independence makes the cross terms vanish. Blade
never assumes this silently; it demands a **license**, checked in two places
depending on where the combination happens:

- **Module level** — a top-level `let s = dx + dy` (or any elaboration-time
  `dist_add`/`dist_scale`/`dist_affine` combination) is checked against the
  elaboration-time registry populated by `let _ = ppl.independent(X, Y)`
  declarations. Missing declaration is an elaboration error (BL5100).
- **Function body** — a combination of `Dist`-typed *parameters* has no
  visible provenance to elaboration (elaboration runs source-to-source,
  before the function is even fully typed), so the checker
  (`inferDistBinOp`, TypeCheck.fs:5812-5856) requires a `where
  <alias>.indep(a, b)` clause on the enclosing function's signature. The
  clause **promotes** `combine` to a PPL-aware function: the parser records
  the unnamed conjunct as data (`WhereClause.Custom`), the checker dispatches
  it through `Blade.Constraints`, and PPL's handler licenses the parameter
  pair for the body — `a + b` type-checks inside `combine` only because of
  that pin. Every **call site** of `combine` then has to *discharge* the
  license against the actuals' declared sources (i.e. `combine(dx, dy)` only
  type-checks if `dx`/`dy` trace back to a module-level `independent(...)`
  declaration or another already-licensed pair).
- The same `where ppl.indep(X, Y)` conjunct works on a **struct** definition
  (`ppl/016_struct_indep.blade`): a constrained record can carry its
  independence structure as a static field of the type, stripped from the
  runtime `validate()` and consumed into the relation.
- Unlicensed use inside a function body is a **type error**, `BL3007`
  (`DistNotIndependent`), with different steering text depending on whether
  the unrelated sources are module-level names or function-parameter tokens
  (`"func.param"`-shaped source ids steer to a `where` clause; plain names
  steer to a module-level `independent(...)` declaration).
- `dist * dist` is undefined entirely (`DistOpUndefined`) — cumulants are
  additive under independent sums and multilinear under scalar scaling, but
  a *product* of random variables needs the moment/Wick machinery, which the
  module does not yet offer as an operator (only `dist_map`/`dist_jet` reach
  polynomial pushforwards, and those need the map spelled out).
- Scalar `*` needs **no** independence license at all — pure multilinearity
  (`kappa_k(cX) = c^k kappa_k(X)`) works on any Dist value including
  unknown-provenance function parameters.

Rejection examples: `ppl/021_dist_add_needs_license.blade` (BL3007, no
`where` clause), `ppl/024_dist_add_unlicensed.blade` (BL3007, declared but
unrelated sources). Licensed examples: `ppl/022`, `ppl/023`,
`examples/02_portfolio_moments.blade` part (e)/(f).

## 4. Order accounting

Every carried `Dist` has a **static** order `r` — the number of cumulant
tensors it packs (`kappa_1..kappa_r`). Two independent guards enforce that
nothing reads past what a tower actually carries; they live in different
layers and carry different codes:

- **Checker-level, `BL3007`**: `cumulant(d, k)` — a *projection* on a
  checker-typed `Dist<r,T>` value, including a Dist-typed function
  parameter — refuses if `k > r` (`CumulantOrderExceeds`,
  `TypeCheck.inferCumulantProj`, TypeCheck.fs:4080-4096) or if `k < 1`
  (`CumulantOrderPositive`), with steering: *"insufficient stochastic
  order. Construct with a higher order (`dist(A, k)`) or project a carried
  component."* This is the guard that fires **inside a function body on a
  parameter** — a position the old elaboration-level registry could never
  see (`tests/corpus/ppl/019_dist_order_guard.blade`).
- **Elaboration-level, `BL5100`**: every former that *spends* order —
  `dist_jet`/`dist_map` (needs input order `q * s` for a degree-`s` jet and
  `q` output orders; `tests/corpus/ppl/037`, `042`, `062`, `064`) and
  `dist_reweight` (a degree-`q` polynomial weight spends `q` orders:
  `rOut = priorOrder - q`, refused if `rOut < 1`;
  `tests/corpus/ppl/048_reweight_needs_order.blade`) — refuses at
  elaboration time rather than silently truncating. Both error families
  carry the same *"refuse rather than silently approximate"* stance: no
  former ever pads a missing cumulant with a default value.

The general rule: reading order costs nothing (`moments`/`cumulants`
estimators just compute whatever order you ask for from the raw data), but
**transporting or updating** an already-packed `Dist` spends order — a
pushforward or a Bayesian update can only be as precise as the carried tower
allows, and Blade would rather refuse to compile than answer with silently
missing terms.

## 5. The `Dist<r, T like axes>` type

`Dist` is a first-class **surface type**, parsed at `src/Parser.fs:662-679`
as `Dist<order, Elem like I1, ..., Ik>` — `order` is any statically-evaluable
int expression (literal, `let static`, or static-function call, the same
"replicate-count" contract used elsewhere), and the `like` axes list is the
random vector's variable-axis index types, using the identical syntax as
`Array`'s index list. At the checker level (`IRTDist`, `src/Types.fs:518`)
this is a **strict, nominal** type: unification requires the carried orders
to match exactly (no covariance — a `Dist<2,...>` never unifies with a
`Dist<3,...>`) and the axes to agree positionally, just like `IRTIdxTagged`.
Only the `dist(...)` intrinsic and Dist-typed operators ever produce a
`Dist` value; a bare tuple of arrays never flows into one.

Below the type checker, a `Dist` value **is** the tuple of its packed
`kappa_1..kappa_r` cumulant component arrays — `Zonk.zonkType`
(`src/Zonk.fs:120-128`) is the erasure point: `IRTDist (order, elem, axes)`
zonks to `IRTTuple (distComponentTypes order elem axes)`. All Dist-aware
checking (the order guard, operator dispatch, independence-license
dispatch, signature unification) happens during inference, strictly before
zonking; downstream of the checker, Lowering/IR/CodeGen never see `IRTDist`
at all (a sentinel arm in `CodeGen.irTypeToCpp` is the backstop — reaching it
means Dist erasure was skipped somewhere upstream, and it is a compiler bug,
not a user-facing error). This is why both backends "come for free": by the
time either one runs, a Dist is nothing more exotic than a tuple of ordinary
packed `SymIdx` arrays. The internal architecture of this arc — the typed
surface type, its checker-level guards, and the erasure boundary — is
documented in more depth for compiler maintainers at `src/ppl/NOTES.md`.

---

## Roadmap

The sections above describe the **entire implemented surface** as of this
writing. `docs/plan-ppl-proper.md` (status: PROPOSED, 2026-08-05) lays out
six phases (plus cross-cutting work) to grow this into a proper
sampling-based PPL system, motivated by the critique that a module with no
`sample`/`observe` primitives and no inference engine "is not probabilistic
programming in the WebPPL/Stan/Pyro sense." **Nothing in this Roadmap is
implemented.** Every item below is a proposal only.

- **P1 — Named families, log-densities, and the approximate tower bridge**
  (NOT STARTED): `ppl.gaussian`/`exponential`/`gamma`/`poisson`/`uniform`/
  `beta`/`bernoulli`/`lognormal`/`mvgaussian` as syntactic-form constructors
  (value position materializes a cumulant tower; argument position of a
  density-aware former stays symbolic); `ppl.logpdf`/`ppl.loglik`
  log-density formers as AD-able accumulation loops; an Edgeworth/
  Cornish-Fisher "approximate tower bridge" (`dist_pdf_approx`,
  `dist_quantile_approx`) giving empirical `Dist` towers an honest
  approximate density, paired with `dist_negativity` as the honesty check.
  Blocked in part on a new `lgamma` intrinsic (hand-rolled Lanczos, bit-exact
  F# mirror) for Gamma/Poisson/Beta.
- **P2 — Sampling primitives** (NOT STARTED): new keyed batch fills in
  `src/cpp/rand_runtime.hpp` (`exponential`, `gamma`, `poisson`, `beta`,
  `bernoulli`, `categorical`), each with a `__rand_<fam>` intrinsic (the
  existing `rand` module's TypeCheck/Lowering/CodeGen/`RandMirror.fs`
  template) and a bit-exact F# interpreter mirror; `ppl.sample(family(...),
  key, n)` elaborating directly to those intrinsics; `dist_sample_approx` via
  a Cornish-Fisher transform of a uniform fill. This is the first phase that
  touches C++ codegen and the interpreter directly, rather than staying pure
  source-to-source elaboration.
- **P3 — The model layer** (NOT STARTED): `ppl.lp`/`ppl.observe` log-prob
  terms; a "model" is just an ordinary named function summing them; log-
  Jacobian transforms (`ppl.tr_log`/`tr_logit`/`tr_simplex`/`tr_cholcorr`)
  for unconstrained sampling. No parser changes needed — the density-based
  (Stan-style) design principle means a model needs no effect system or
  program tracing.
- **P4 — Sampling inference: MH, HMC, diagnostics** (NOT STARTED):
  `ppl.mh`/`ppl.hmc` as recursive-array Markov chains (Blade's TRMC recursive
  arrays are literally `chain[t] = step(chain[t-1], subkey(t))`); HMC riding
  `ad.grad` directly, bounded by the AD-able subset (`src/Grad.fs:27-50`);
  `ppl.ess`/`ppl.rhat`/`ppl.autocorr` diagnostics as moment-algebra reuse; and
  the round-trip that "unifies the module" — `dist(samples, r)` (already
  shipped) lifts any sampled chain back into the existing `Dist` tower
  algebra.
- **P5 — Exact and moment-native inference** (NOT STARTED): closed-form
  conjugate-update formers (`ppl.bayes` for Gaussian-Gaussian, Beta-
  Bernoulli, Gamma-Poisson, Normal-InverseGamma); `dist_condition` —
  multivariate Gaussian conditioning via Schur complement on the `kappa_2`
  block, exact only at order 2; lifting today's `univariateOnly` restriction
  (§2.5) on `dist_expect`/`dist_reweight`/`dist_mix` to multi-index
  polynomial likelihoods; `ppl.laplace` mode-finding via `ad.grad` plus the
  module's existing jet machinery for the Hessian.
- **P6 — Optional/advanced engines** (NOT STARTED, each independently
  shippable): mean-field ADVI; an SMC/particle filter for state-space
  models (pairing naturally with `mstate` for online summaries);
  expectation propagation over `mvgaussian` factor lists; exact
  discrete-latent enumeration; posterior predictive checks.
- **Cross-cutting** (PARTIALLY STARTED — this documentation deliverable is
  itself one of the listed cross-cutting items): the `lgamma` intrinsic;
  continuing the `tests/corpus/ppl/` numbering from 070 for new formers, with
  new `__rand_*` pins in `tests/corpus/rand/`; an oracle `dump-*` verb in
  `src/ppl/Program.fs` per new exact former (see `src/ppl/README.md`); adding
  `ppl` to `InterpDiff.currentSlice` once samplers land, since sampler
  formers are the first `ppl` code where a C++/interp divergence could hide.

**Explicit non-goals** (per the plan's §9): trace-based/effect-handler PPL
semantics (Gen/WebPPL-style program traces — this module stays density-based,
Stan-style); NUTS or any other data-dependent trip count at the Blade level;
dynamic model structure (open-universe models, RJ-MCMC); GPU inference;
distribution families with undefined moments as tower values (Cauchy,
low-ν Student-t may appear later behind logpdf/sample only, never as a
`Dist` tower value).
