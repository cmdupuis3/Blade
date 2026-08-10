# Blade Feature Module: Probabilistic Programming (`ppl`)

Status: **implemented and corpus-tested** (133 files in `tests/corpus/ppl/`,
plus supporting pins in `tests/corpus/rand/` and `tests/corpus/ad/`, plus two
worked examples, `examples/02_portfolio_moments.blade` and
`examples/05_streaming_telemetry.blade`). The module started as a compile-time
moment/cumulant algebra with no named distributions, no density, and no
inference engine; `docs/plan-ppl-proper.md` (2026-08-05) closed most of that
gap, and the module is no longer only a compile-time moment algebra. It now
has named distribution families with closed-form log-densities, exact and
approximate sampling, two general-purpose MCMC engines (Metropolis-Hastings
and Hamiltonian Monte Carlo) riding the language's own recursive arrays and
`ad.grad`, chain diagnostics, closed-form conjugate Bayesian updates, and
multivariate Gaussian conditioning — all still elaborating to ordinary Blade
source ahead of type checking, so both backends keep getting the module for
free. See the Roadmap section at the bottom for what is implemented (most of
the plan's P1-P5) versus what remains (multivariate towers beyond order-2
conditioning, Normal-InverseGamma, Laplace/ADVI/SMC/EP, and a few smaller
items). This document is the canonical description of the surface that exists
today.

Design stance: every `ppl` former elaborates to ordinary Blade source —
loop-object pipelines (`method_for` / `<@>` / `reduce`), straight-line scalar
arithmetic, or (for `mh`/`hmc`) the language's own recursive-array construct
— **before type checking**, in `src/ppl/compiler/PplElaborate.fs` (4506
lines), wired into the pipeline at `src/TypeCheck.fs:12483` (between `sgs`
and `math` elaboration, all under `checkModule`). The sampling primitives
(`sample`, `dist_sample_approx`, and the `mh`/`hmc` chain machinery) are the
first part of the module to reach past pure source-to-source rewriting: they
emit calls to the `__rand_*` keyed-batch-fill intrinsics the separate `rand`
module also uses, and `hmc` emits the `<ad-alias>.grad(logpost)` surface form
for the `Grad` pass (which runs after `ppl` elaboration) to expand. Both are
still zero new IR nodes contributed by `ppl` itself. Elaboration failures
surface as a single generic error code, `BL5100`, except for two errors that
belong to other passes reached through elaboration output: `Grad`'s own
`BL5500` when an `hmc` model body falls outside the AD-able subset, and the
checker's `BL3007` for `cumulant(d, k)` projection (§5).

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
(`pplAliasesOf`, PplElaborate.fs:3939). The module is qualified-only by
design: every former is reached as `alias.former(...)`, never as a bare
top-level name, so it can never silently collide with a user identifier that
happens to share a former's short name.

The elaborator strips the qualification before the former-recognition pass
runs (`stripQualified`, PplElaborate.fs:3953): `alias.moments(...)` becomes
the internal bare `moments(...)` node, `alias.cumulant(d, k)` becomes the
internal marker `__ppl_cumulant(d, k)` (consumed later by the type checker,
see §5), and `alias.indep(a, b)` in a `where`-clause becomes `__ppl_indep(a,
b)`. A former written unqualified (bare `moments(...)` with no `import ppl`
in scope) never resolves — the module is import-gated, not language-wide.

**User definitions shadow the formers entirely** — the literal rule, shared
verbatim with the `ml` and `math` elaborators (PplElaborate.fs:4070-4076): if
the enclosing module declares a top-level `function moments(...)`, the
`moments` former is deactivated project-wide in that module and every
`moments(...)` call resolves to the user's function instead. The elaborator
computes this by collecting all `DeclFunction` names in the module
(`declNames`) and gating each former's recognition on `not (Set.contains
name declNames)`.

## 2. Former reference

49 formers total (`formerNames`, PplElaborate.fs:84), each of which must be
the **entire right-hand side of a top-level `let`**, with two exceptions: the
checker-level `cumulant` projection (§5), which can appear anywhere a
Dist-typed value is in scope including inside function bodies; and `logpdf`/
`loglik`, which are additionally legal in **expression position inside a
top-level function body** (§2.8) — that is the density-form model layer, and
every other former still stays decl-RHS only. A stray former reference
anywhere else — inside an arbitrary expression, as a sub-term of a larger
call — is rejected in a dedicated misplaced-former pass (PplElaborate.fs
:4450-4460, "pass 3"), which runs after the density-form rewrite (Pass 1.5,
§2.8) and still refuses anything that rewrite did not itself hoist.

Sections 2.1-2.6 below are the original moment/cumulant surface (unchanged by
this arc). Sections 2.7-2.13 are the P1-P5 additions: named families and
their densities, exact and approximate sampling, the two MCMC engines and
their diagnostics, and closed-form/exact multivariate inference.

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

(Doc block, PplElaborate.fs:1448-1472.) Conditioning on a finite-support
variable is `dist_reweight` by its Lagrange indicator polynomial;
disintegrate-then-`dist_mix` is the law of total probability; sequential
Bayes is chained `dist_reweight`.

All three are gated `univariateOnly` (PplElaborate.fs:1509): calling any
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

(Doc block, PplElaborate.fs:1682-1697.) `dist_atoms` accepts a **negative**
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

### 2.7 Named distribution families

`ppl.gaussian`/`exponential`/`gamma`/`poisson`/`uniform`/`lognormal`/
`bernoulli`/`beta` are **syntactic forms** with two readings, the same
recognition trick `dist_map` uses for its lambda argument
(`familyParams`, PplElaborate.fs:1811-1820; `familyArg`, PplElaborate.fs:1956-1964):

| Family | Parameters |
|---|---|
| `gaussian(mu, s2)` | mean, variance |
| `exponential(rate)` | rate |
| `gamma(shape, rate)` | shape, rate |
| `poisson(lam)` | rate |
| `uniform(a, b)` | lower, upper |
| `lognormal(mu, s2)` | log-mean, log-variance |
| `bernoulli(p)` | success probability |
| `beta(a, b)` | shape a, shape b |

- **Value position** — `let d = ppl.gaussian(mu, s2, r)` — materializes the
  order-`r` (static, `1..6`) univariate cumulant tower as a registered `Dist`
  binding, exactly like `dist(A, r)`. Parameters may be arbitrary runtime
  `Float64` expressions (the cumulant formulas are plain arithmetic).
  Gaussian/exponential/gamma/poisson/uniform use closed-form cumulant ladders
  (e.g. gaussian: `kappa_1 = mu`, `kappa_2 = s2`, higher cumulants zero;
  exponential/gamma: `kappa_k = (k-1)!/rate^k`, gamma additionally scaled by
  `shape`; poisson: every cumulant equals `lam`; uniform's even cumulants are
  the Bernoulli-number ladder, odd orders `>= 3` vanish). Lognormal, bernoulli,
  and beta have no closed cumulant ladder, so they go through raw moments
  (`exp(j*mu + j^2*s2/2)` for lognormal; a one-factor-at-a-time recursion for
  beta's `m_j`; `p` itself for every bernoulli raw moment) followed by Möbius
  inversion over the set-partition lattice, same as any other moment-to-
  cumulant conversion in the module. **A family tower's `Sources` set is
  empty** — a closed-form family carries no data provenance, so `dist_add`/`+`
  between two family towers needs no `independent(...)` declaration (the
  same convention `dist_atoms` already uses for quasi-distributions, §2.6).
- **Argument position of a family-aware former** — `logpdf`, `loglik`,
  `sample`, `bayes`'s prior slot — the application is read symbolically as a
  `(tag, param exprs)` pair; no tower ever materializes and the family takes
  **no order argument** here (a stray trailing static int is a specific
  steering error: *"`fam` takes no order argument in `<position>` — the
  family is symbolic here"*).

A user definition of a family's name shadows it exactly like the estimator
formers — `gaussian(...)` stops being a family constructor project-wide in a
module that defines its own `function gaussian(...)`.

Example (`tests/corpus/ppl/070_family_gaussian_tower.blade`):

```blade
let d = ppl.gaussian(1.5, 2.0, 4)
let k1 = ppl.cumulant(d, 1)   // EXPECT: [1.5]
let k2 = ppl.cumulant(d, 2)   // EXPECT: [2.0]
let e2 = ppl.dist_expect(d, 0.0, 0.0, 1.0)   // E[X^2] = mu^2 + s2
// EXPECT: e2 = 4.25
let dm = ppl.dist_map(d, 2, lambda(x) -> 2.0 * x + 1.0)   // exact affine push
```

Tests: `ppl/070-074` (towers per family), `ppl/080-081` (nonstatic order /
wrong arity rejects), `ppl/085` (beta, including the `beta(1,1) == uniform(0,1)`
cross-family identity).

### 2.8 Log-densities: `logpdf` / `loglik`

```
logpdf(family(params), x)   the scalar log-density at x: closed-form
                             arithmetic over once-bound parameters,
                             ON-SUPPORT by design (no branching -- an
                             if/match would leave the AD-able subset).
loglik(family(params), A)   the SUMMED log-density over A's sample axis
                             (its last -- and only -- declared index), an
                             AD-able scalar accumulation loop (`let mut` +
                             for + `+=`) with per-family constants hoisted
                             out of the loop. Leading variable axes are
                             refused -- a univariate family has no
                             per-coordinate loglik.
```

(`logPdfParts`/`logLikParts`, PplElaborate.fs:1980-2159.) Gaussian,
exponential, uniform, and lognormal are closed forms with no special
function. **Gamma, poisson, and beta need the `lgamma` intrinsic** for their
normalizing constant (`shape*log(rate) - lgamma(shape)`, `-lgamma(k+1)`, and
`lgamma(a) + lgamma(b) - lgamma(a+b))` respectively) — bernoulli's
log-density has no gamma-function term despite landing in the same
implementation batch. `lgamma` is a hand-rolled Lanczos series
(`tests/corpus/intrinsics/008-009`) bit-exact between the C++ runtime and the
interpreter, deliberately **not** in `StaticEval`'s compile-time fold table so
every call is forced through the runtime series both backends share.

**AD-ability.** `logpdf`/`loglik` bodies stay inside the AD-able subset
(`src/Grad.fs:27-50`) for every family, gamma/poisson/beta included: `lgamma`
differentiates to `digamma` (`derivRule`, `src/Grad.fs`), landed alongside the
`digamma` intrinsic (`tests/corpus/intrinsics/010-011`), so a gamma-family
log-density is fully `ad.grad`-able — this is what makes HMC over
gamma/poisson/beta models possible (§2.11; `tests/corpus/ad/015_gamma_loglik_grad.blade`
pins the gradient directly, and `tests/corpus/ppl/117` is the end-to-end HMC
payoff). `digamma` itself has no differentiation rule (its derivative,
trigamma, does not exist in the language yet — see Roadmap).

Both formers work **inside top-level function bodies**, in expression
position, not just as a top-level `let` RHS — this is the density-form model
layer (`docs/plan-ppl-proper.md` §4): a "model" is an ordinary named function
summing `logpdf`/`loglik` terms, and each call site rewrites to the same
closed-form arithmetic the decl-position formers emit, hoisted as statement
lets into the enclosing block (`rewriteBodyFormers`, PplElaborate.fs:2307,
Pass 1.5 — see `src/ppl/README.md` §1 for where this sits in the pipeline).
`loglik` keeps its accumulation-loop shape at statement level specifically so
the surrounding function body stays inside `Grad`'s AD-able subset — every
other former stays decl-RHS only; a misplaced-use pass still rejects them
inside a function body.

Example (`tests/corpus/ppl/103_density_formers_in_model_body.blade`):

```blade
let data: Array<Float64 like Idx<5>> = [1.2, 0.7, 2.3, -0.4, 1.9]
function two_terms(t: Float64) -> Float64 =
    ppl.logpdf(gaussian(0.0, 1.0), t) + ppl.logpdf(exponential(1.0), t)
function data_ll(theta: Float64) -> Float64 = ppl.loglik(gaussian(theta, 2.0), data)
function gamma_lp(t: Float64) -> Float64 = ppl.logpdf(gamma(2.0, 1.0), t)   // lgamma path, AD-able
```

Tests: `ppl/075-079` (logpdf/loglik per family, lgamma-free), `ppl/082`,
`ppl/087-089` (gamma/poisson/bernoulli/beta, including the lgamma families),
`ppl/083-084`/`086` (unknown family / stray order argument / non-rank-1 data
rejects), `ppl/103` (expression position).

### 2.9 Exact sampling: `sample`

```
sample(family(params), key, n)   a keyed batch fill of n draws from the
                                  named family, elaborating DIRECTLY to the
                                  matching __rand_* intrinsic.
```

(PplElaborate.fs:2871-2920.) Legal without `import rand` — the `__rand_*`
names are checker-level builtins, not something the separate `rand` module
owns. Exponential/gamma/poisson/bernoulli/beta lower straight to their
keyed fill (`__rand_exponential`, `__rand_gamma`, ...); gaussian/lognormal/
uniform are synthesized as elementwise affine/exp transforms over the
existing normal/uniform fills (`gaussian(mu, s2) -> mu + sqrt(s2) *
__rand_normal(key, n)`; `lognormal` is `exp` of the same construction;
`uniform(a, b) -> a + (b - a) * __rand_uniform(key, n)`). The result is an
ordinary `Array<Float64 like Idx<n>>`; `__rand_*` output is not
differentiable (the existing `rand`/`grad` boundary is unchanged — draw noise
outside a differentiated function, transform inside, is still the pattern).

**Key discipline.** Every keyed former in the module (`sample`,
`dist_sample_approx`, and every random draw inside `mh`/`hmc`, §2.11) reads a
plain `Int64` key and reseeds a fresh `mt19937_64` stream through the
`rand` module's `mix64`/SplitMix64 finalizer per call — which decorrelates
even *adjacent* keys (`tests/corpus/rand/004`). Distinct **additive site
constants** (e.g. `key + 1000003` for `mh` proposals vs `key + 2000003` for
its accept draws) are therefore enough to keep two draws under the same user
key independent; site constants are spaced `>= 1e6` apart so two *different*
formers under the same key cannot collide unless the user keys themselves
differ by exactly the site-constant gap.

Example (`tests/corpus/ppl/095_sample_families_determinism.blade`):

```blade
let e  = ppl.sample(exponential(2.0), 11, 6)
let gm = ppl.sample(gamma(2.0, 1.0), 77, 4)     // byte-identical to rand.gamma(77, 2.0, 1.0, 4)
let gs = ppl.sample(gaussian(1.0, 4.0), 5, 4)
let ln = ppl.sample(lognormal(1.0, 4.0), 5, 4)  // ln(i) == exp(gs(i)) exactly: same normal fill
```

Tests: `ppl/095-096` (determinism, and byte-identity with `dist(A, r)` over
the same fill), `ppl/100-101` (nonstatic sample-count rejects).

### 2.10 The approximate tower bridge

An honest approximate density/quantile for **any** carried univariate tower —
family-constructed or data-estimated alike, since a `Dist` estimated from
data has no exact density at all:

```
dist_pdf_approx(d, x)        Edgeworth/Gram-Charlier density from
                              kappa_1..kappa_r. NOT a probability density in
                              general -- the expansion can go negative in
                              the tails; pair it with dist_negativity as the
                              honesty check for exactly that failure mode.
dist_quantile_approx(d, p)   Cornish-Fisher quantile: the formal series
                              inverse of the Edgeworth CDF, built on the
                              standard-normal quantile (Wichura AS241
                              PPND16, emitted as plain branching arithmetic).
dist_sample_approx(d, key, n) a keyed __rand_uniform fill mapped through
                              PPND16 and then the Cornish-Fisher transform --
                              approximate tower sampling, deterministic
                              given the key, usable even for a family with
                              no dedicated runtime fill.
```

(Section comment, PplElaborate.fs:2448-2484.) Both expansions are generated
by the same formal-series construction the reference oracle uses
(`src/ppl/Density.fs`, module `Expansion`) — a symbolic coefficient ring over
`z`, `phi(z)`, and the standardized cumulants `lambda_3..lambda_6`, collapsed
to straight-line arithmetic over runtime-read kappas rather than floats.
`r = 2` carries no correction terms at all — the Edgeworth density of a
Gaussian tower **is** the exact Gaussian pdf, and Cornish-Fisher degenerates
to `mu + sd * PPND(p)` exactly; both formers refuse univariate-only and
`r < 2` rather than truncate. `kappa_2 <= 0` (lawful for a quasi-distribution
tower, §2.6) is not refused at elaboration time: `sd = sqrt(kappa_2)`
evaluates to `NaN` at runtime and propagates through every read, the honest
answer for a tower with no Gaussian anchor.

Example (`tests/corpus/ppl/094_approx_empirical_tower.blade` — the bridge
applied to a DATA-ESTIMATED tower, not a family constructor, which is its
whole point):

```blade
type OneIdx = Idx<1>
type TimeIdx = Idx<6>
let A: Array<Float64 like OneIdx, TimeIdx> = [[1.0, 2.0, 4.0, 6.0, 0.0, 3.0]]
let d2 = ppl.dist(A, 2)
let pdf2 = ppl.dist_pdf_approx(d2, 2.0)         // EXPECT: 0.191064706193648
let q2 = ppl.dist_quantile_approx(d2, 0.975)    // EXPECT: 6.53176776818026
let d4 = ppl.dist(A, 4)
let pdf4 = ppl.dist_pdf_approx(d4, 2.0)         // EXPECT: 0.180731399197857
```

Oracle pins for the whole bridge (per-family Edgeworth densities and
Cornish-Fisher quantiles at several orders, plus accuracy tables against the
exact laws) are catalogued in `src/ppl/ORACLE_PINS.md` under `dump-edgeworth`
and `dump-cf` — see `src/ppl/README.md` for the oracle workflow.

Tests: `ppl/090-093` (Edgeworth-exact-on-Gaussian, oracle-pinned Edgeworth/CF
values), `ppl/094` (empirical towers, and packed-vs-flat kappa-read parity
against `dist_atoms`), `ppl/097` (approximate sampling), `ppl/098-099`
(multivariate / `r < 2` rejects).

### 2.11 Sampling inference: `mh`, `hmc`

Chains are **recursive arrays** — Blade's own sequential construct is exactly
a Markov chain, `chain[t] = step(chain[t-1], subkey(t))`, constant-stack by
TRMC and statically sized, so no new control-flow machinery was needed:

```
mh(logpost, x0, n, scale, key)        random-walk Metropolis.
hmc(logpost, x0, n, eps, L, key)      fixed-leapfrog Hamiltonian Monte Carlo.
```

(Section comment, PplElaborate.fs:2922-3012.) `logpost` must **name** a
top-level same-module `Float64 -> Float64` function (called by name at each
step — no inlining, unlike `dist_map`'s symbolic differentiation). `x0` is
the scalar seed state; `n` is the static (`>= 2`) chain length; `key` is the
`Int64` stream key. **Convention: `chain(0) = x0`**; `chain(t)` for `t >= 1`
are the sampler states, so a length-`n` chain carries `n - 1` transitions.
Both emit the corpus-blessed block-wrapped `let rec` recursive-array shape,
with all randomness pregenerated *outside* the chain as keyed batch fills
(so the sweep itself is deterministic arithmetic reading pre-filled arrays):

- **`mh`** draws proposals from `__rand_normal(key + 1000003, n)` and accept
  draws from `__rand_uniform(key + 2000003, n)`; the step function is the
  proposal plus the accept/reject conditional — legal there because nothing
  in the sampler loop is ever differentiated.
- **`hmc`** draws momenta from `__rand_normal(key + 3000017, n)` and accept
  draws from `__rand_uniform(key + 4000037, n)` (different site constants
  from `mh`, so an `mh` and an `hmc` chain under the *same* user key stay
  decorrelated). **Energy convention**: `H(q, p) = -logpost(q) + p^2/2` (unit
  mass); the MH correction accepts when `log u < (logpost(q_L) -
  logpost(q_0)) + (p_0^2 - p_L^2)/2`. The step function runs the standard
  `L`-step velocity-Verlet sweep (`L + 1` gradient evaluations: an initial
  half momentum step, `L - 1` interleaved full steps, a final position
  update and half momentum step), with each gradient obtained by calling the
  `<ad-alias>.grad(logpost)` **surface form** for the `Grad` pass (which runs
  after `ppl` elaboration) to expand — so **`import ad` is required**: without
  it `hmc` refuses immediately with steering rather than emitting an unbound
  call. The model body must sit inside the AD-able subset
  (`src/Grad.fs:27-50`); a model outside it is refused by `Grad` itself with
  `BL5500` — a different, honest boundary from `hmc`'s own `BL5100` argument
  checks (`hmc`'s own guards pass; the refusal belongs to `Grad`). `mh` never
  differentiates its model, so it accepts models `hmc` rejects — the
  documented `mh`/`hmc` split (`tests/corpus/ppl/120`).

Example — a model function plus `mh`, Normal-Normal conjugate acceptance
(`tests/corpus/ppl/108_mh_normal_normal_conjugate.blade`, trimmed):

```blade
let data: Array<Float64 like Idx<5>> = [1.2, 0.7, 2.3, -0.4, 1.9]
function logpost(theta: Float64) -> Float64 =
    ppl.logpdf(gaussian(0.0, 4.0), theta) + ppl.loglik(gaussian(theta, 2.0), data)

let c1 = ppl.mh(logpost, 0.0, 4096, 1.0, 1234)
let m1 = ppl.chain_mean(c1, 512)   // post-burn mean vs the exact posterior
let v1 = ppl.chain_var(c1, 512)
let rh = ppl.rhat(c1, ppl.mh(logpost, 0.0, 4096, 1.0, 987654321))
// EXPECT: mean_ok_1 = true   (|m1 - 1.0363636363636362| < 0.05)
// EXPECT: rhat_ok = true     (rh < 1.01)
```

The HMC gamma-model payoff (`tests/corpus/ppl/117_hmc_gamma_poisson_lgamma.blade`)
is the point of the `lgamma`/`digamma` work: HMC over a Gamma-prior,
Poisson-likelihood model, sampled unconstrained as `t = log(lam)` with the
log-Jacobian folded straight into the log-posterior:

```blade
let pdata: Array<Float64 like Idx<5>> = [3.0, 1.0, 4.0, 1.0, 5.0]
function logpost(t: Float64) -> Float64 =
    ppl.logpdf(gamma(2.0, 1.0), exp(t)) + t + ppl.loglik(poisson(exp(t)), pdata)

let ct = ppl.hmc(logpost, 1.0, 4096, 0.1, 4, 4242)
// pushed back to lam-space by an ordinary elementwise map, then read through
// chain_mean/chain_var like any other rank-1 chain -- matching the exact
// Gamma(16, 6) posterior (ppl.bayes, tower r=4) to within sampling tolerance.
```

Tests: `ppl/102` (mh determinism), `ppl/108` (mh vs conjugate truth),
`ppl/109-114` (mh/hmc/diagnostic argument-shape rejects), `ppl/115-116` (hmc
determinism, hmc vs conjugate truth), `ppl/117` (the gamma/poisson lgamma
payoff), `ppl/118-120` (missing `import ad`, nonstatic `L`, model-outside-
AD-subset rejects).

### 2.12 Chain diagnostics and the `dist(chain, r)` round-trip

```
chain_mean(c, burn) / chain_var(c, burn)   post-burn moments of c(burn..n-1),
    population-normalized (/m), the same convention as the moment estimators.
autocorr(c, maxlag)   lag-0..maxlag autocorrelation array (biased estimator,
    the standard choice for ESS); rho_0 = 1 by construction.
ess(c)   effective sample size via Geyer's initial positive sequence: pair
    sums G_k = rho_2k + rho_2k+1 for k = 0..P-1, P = min((n-2)/2, 128);
    truncate at the FIRST non-positive pair; tau = -1 + 2*sum(surviving G_k);
    ess = n / tau. No monotonicity pass, and tau is not clamped -- an
    antithetic chain (HMC, typically) may lawfully report ess > n.
rhat(c1, c2)   split-Rhat over two equal-length chains: each splits in half
    (J = 4 segments of m = n/2 total); within-variance W (mean of the 4
    segment variances), between-variance B (m * variance of the 4 segment
    means), Rhat = sqrt(((m-1)/m * W + B/m) / W).
```

(Section comment, PplElaborate.fs:2922-3012.) All four accept either an
`mh`/`hmc` chain from this module (its length is looked up in an
elaboration-time chain registry) or any module-level rank-1 `Float` array
with a static extent — so hand-written or transformed arrays (like the
`exp`-mapped lam-space chain in the HMC example above) ride the same
formers. `autocorr`/`ess`/`rhat` lower to synthesized top-level functions
(the same mut-array + element-write + for-loop shape `grad` already
generates); `chain_mean`/`chain_var` are module-block accumulation loops.

**`dist(chain, r)`** — already shipped as the general `dist` former, and the
literal round-trip that unifies the module: a chain binding has no declared
type annotation, so the ordinary `dist` shape-inference routes cannot see it;
the elaborator instead consults the chain registry and wraps the rank-1
chain to `Idx<1> x Idx<n>` (the same range-kernel idiom `tests/corpus/ppl/096`
writes by hand) before running the ordinary `dist` elaboration. Once wrapped,
every tower consumer — `cumulant`, `dist_expect`, `dist_map`, the approximate
bridge — composes with a sampled chain exactly as it would with a `moments`-
estimated `Dist`.

Example (`tests/corpus/ppl/121_dist_over_chain_roundtrip.blade`):

```blade
function lp(x: Float64) -> Float64 = 0.0 - x * x / 2.0
let c = ppl.hmc(lp, 0.3, 512, 0.5, 4, 7)
let d = ppl.dist(c, 2)
let m1 = ppl.dist_expect(d, 0.0, 1.0)
let cm = ppl.chain_mean(c, 0)
let mean_rt = abs(m1 - cm) < 0.000000000001   // EXPECT: true -- same sums, different loop shapes
```

Tests: `ppl/104-107` (hand-checked chain moments/autocorr/ess/rhat over a
literal array), `ppl/113-114` (autocorr/rhat argument-shape rejects),
`ppl/121` (the `dist(chain, r)` round-trip for both sampler kinds).

### 2.13 Exact inference: `bayes` and `dist_condition`

```
bayes(prior(hyper), <family>_lik(params), A, r)
    the closed-form conjugate posterior AS AN ORDINARY FAMILY TOWER of
    static order r. Supported pairs:
      gaussian(m0, v0)   + gaussian_lik(s2) -> gaussian   (Normal-Normal,
                                                            s2 KNOWN)
      beta(a, b)         + bernoulli_lik()  -> beta       (Beta-Bernoulli)
      gamma(shape, rate) + poisson_lik()    -> gamma      (Gamma-Poisson)
dist_condition(d, i, x)
    condition a MULTIVARIATE order-2 tower on coordinate i taking value x --
    the Schur complement on the kappa_2 block:
      mean'_j = mu_j + k2[j,i]/k2[i,i] * (x - mu_i)
      cov'_ab = k2[a,b] - k2[a,i] k2[i,b] / k2[i,i]
```

(Section comments, PplElaborate.fs:2161-2180 and :1613-1629.) `bayes` mirrors
the reference prototype's `Density.fs` `Conjugate` module (oracle verb
`dump-conjugate`, see `src/ppl/ORACLE_PINS.md`): the sufficient statistic
(the sample-axis sum) is read off the data array once via `loglik`'s own
accumulation idiom, the posterior hyperparameters are once-bound scalar
arithmetic (Density.fs's formulas verbatim), and the posterior tower is
re-emitted through the ordinary family constructor (§2.7) — so the result is
a fully composable registered `Dist`. The posterior's `Sources` is the data
array's name (not empty, unlike a bare family constructor): combining two
posteriors computed over the *same* data with `+` demands an
`independent(...)` license that data-dependence makes structurally
impossible to satisfy, rather than silently adding dependent cumulants.
**Normal-InverseGamma (unknown mean AND variance) is deliberately deferred**
— see Roadmap.

`dist_condition` is **exact only at order 2**: an order-2 tower is the
Gaussian truncation, and Gaussian conditionals are again Gaussian with
exactly the two Schur-complement blocks above, so conditioning is closed on
order-2 towers. At order `> 2` the conditional cumulants are not a function
of the carried tower at all (the truncation loses the information), so the
module refuses (`BL5100`) rather than silently truncating — order 1 (mean
only, no `kappa_2` to condition on) and univariate towers (no "rest" left
after fixing the one coordinate) are refused for the same reason. The result
registers as a **flat** `(D-1)`-dimensional order-2 dist, so it composes with
`cumulant`/`moments`/`dist_affine`/`dist_expect` downstream, and a `D = 2`
input conditions all the way down to an ordinary univariate tower. `k2[i,i]`
is a runtime value — conditioning on a zero-variance coordinate is the usual
runtime division hazard, not a compile-time refusal.

Example, `bayes` (`tests/corpus/ppl/122_bayes_normal_normal.blade`):

```blade
let data: Array<Float64 like Idx<5>> = [1.2, 0.7, 2.3, -0.4, 1.9]
let post = ppl.bayes(gaussian(0.0, 4.0), gaussian_lik(2.0), data, 4)
let k1 = ppl.cumulant(post, 1)   // EXPECT: [1.0363636363636362]
let k2 = ppl.cumulant(post, 2)   // EXPECT: [0.36363636363636365]
```

Example, `dist_condition` — the 2D regression-line case
(`tests/corpus/ppl/128_dist_condition_2d.blade`):

```blade
let A: Array<Float64 like Idx<2>, Idx<4>> = [[1.0, 2.0, 3.0, 6.0], [2.0, 3.0, 5.0, 10.0]]
let d = ppl.dist(A, 2)
let c = ppl.dist_condition(d, 0, 4.0)   // condition x=4, read off E[y|x=4]
let cm = ppl.cumulant(c, 1)             // EXPECT: [6.642857142857143]
```

Both close the loop against sampling. `tests/corpus/ppl/133_bayes_vs_mcmc_agreement.blade`
computes the same Normal-Normal posterior three ways — `bayes` exactly, an
`mh` chain lifted through `dist(chain, 2)`, and an `hmc` chain through
`chain_mean`/`chain_var` — and asserts all three agree within sampling
tolerance:

```blade
let post = ppl.bayes(gaussian(0.0, 4.0), gaussian_lik(2.0), data, 2)
let pm = ppl.dist_expect(post, 0.0, 1.0)          // exact posterior mean

let cmh = ppl.mh(logpost, 0.0, 4096, 1.0, 1234)
let dmh = ppl.dist(cmh, 2)
let sm = ppl.dist_expect(dmh, 0.0, 1.0)           // mh, via the round-trip
let mh_mean_agrees = abs(sm - pm) < 0.05          // EXPECT: true

let chmc = ppl.hmc(logpost, 0.0, 4096, 0.25, 4, 1234)
let hm = ppl.chain_mean(chmc, 512)                // hmc, via chain_mean
let hmc_mean_agrees = abs(hm - pm) < 0.05         // EXPECT: true
```

Tests: `ppl/122-124` (the three conjugate pairs), `ppl/125-127` (non-
conjugate pair, nonstatic order, non-rank-1 data rejects), `ppl/128-129`
(2D and 3D `dist_condition`, the latter also composing with `dist_affine`
downstream), `ppl/130-132` (order, univariate, out-of-range coordinate
rejects), `ppl/133` (the three-way agreement).

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
  (`inferDistBinOp`, TypeCheck.fs:5902-5946) requires a `where
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
  `TypeCheck.inferCumulantProj`, TypeCheck.fs:4170-4186) or if `k < 1`
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

`docs/plan-ppl-proper.md` (2026-08-05) laid out six phases (plus cross-cutting
work) to grow the module from a compile-time moment algebra into a proper
sampling-based PPL system. §2.7-2.13 above are the result: P1 through P4 and
the core of P5 are implemented and corpus-tested. What follows is what
genuinely remains, phase by phase, with a one-line reason from the section
comments for each open item.

- **P1 — Named families, log-densities, and the approximate tower bridge**
  (IMPLEMENTED — §2.7-2.10; `ppl/070-101`, `intrinsics/008-011`): all eight
  planned univariate families, `logpdf`/`loglik` (including the density-form
  model-body position §2.8 adds beyond the original plan), and the
  Edgeworth/Cornish-Fisher approximate bridge. `mvgaussian` was **not**
  built — see the P5 multivariate item below, which is what actually blocks
  it.
- **P2 — Sampling primitives** (IMPLEMENTED — §2.9; `rand/005-017`,
  `ppl/095-101`): keyed batch fills for exponential/gamma/poisson/beta/
  bernoulli in `src/cpp/rand_runtime.hpp`, each with a bit-exact interpreter
  mirror; `ppl.sample` rides five of them directly and synthesizes
  gaussian/lognormal/uniform as transforms over the normal/uniform fills
  (§2.9); `dist_sample_approx` covers approximate sampling for any tower via
  the Cornish-Fisher transform. `rand` also gained a `categorical` fill
  (`rand/013-017`) for the plan's SMC/discrete-latent-resampling use case,
  but no `ppl` former surfaces it yet — P6 is where that would be used.
- **P3 — The model layer** (PARTIALLY IMPLEMENTED): the design point —
  "a model is an ordinary named function summing log-prob terms, no effect
  system needed" — shipped, but through `logpdf`/`loglik` in **expression
  position** (§2.8, `rewriteBodyFormers`) rather than through the separate
  `ppl.lp`/`ppl.observe` sugar the plan proposed; those two names do not
  exist as formers. **Log-Jacobian transforms** (`tr_log`/`tr_logit`/
  `tr_simplex`/`tr_cholcorr`) were not built either — `ppl/117`'s
  unconstrained `t = log(lam)` reparameterization folds its Jacobian in by
  hand (a bare `+ t` term in the log-posterior) rather than through a
  dedicated former; see "transforms-with-Jacobians" below.
- **P4 — Sampling inference: MH, HMC, diagnostics** (IMPLEMENTED — §2.11-
  2.12; `ppl/102-121`): `mh`/`hmc` as recursive-array chains, `hmc` riding
  `ad.grad` inside the AD-able subset, `chain_mean`/`chain_var`/`autocorr`/
  `ess`/`rhat`, and the `dist(chain, r)` round-trip. **Warmup/thinning
  formers** (`ppl.warmup(chain, w)` etc., proposed as static-slicing sugar)
  were not built — `chain_mean`/`chain_var`'s own `burn` argument covers the
  immediate need ad hoc, but nothing separates a chain into a reusable
  post-warmup array former.
- **P5 — Exact and moment-native inference** (CORE IMPLEMENTED, rest open
  — §2.13; `ppl/122-133`): the three conjugate `bayes` pairs (Normal-Normal
  known-variance, Beta-Bernoulli, Gamma-Poisson) and `dist_condition`'s
  order-2 Schur-complement conditioning are done, cross-checked against `mh`
  and `hmc` in the same file (`ppl/133`). Open:
  - **Normal-InverseGamma** (unknown mean *and* variance): deliberately
    deferred (PplElaborate.fs:2174-2179) — its prior is not a family tower
    (the μ-margin is Student-t, whose cumulants past order `2*alpha` do not
    exist), and its posterior is a 4-hyperparameter object of which only the
    precision margin is a Gamma tower, so it needs a **tuple-former surface**
    (multiple named outputs), not `bayes`'s prior-in/posterior-out shape.
  - **`mvgaussian` and the flat/packed combine seam**: a multivariate family
    constructor needs a decision about how its components are stored
    (`Flat` registry components like every univariate family constructor,
    §2.7, vs. `method_for`-packed `SymIdx` storage like a data-estimated
    `dist(A, r)`, `src/ppl/NOTES.md` §7) and how `dist_add`
    combines the two representations for a multivariate tower; unresolved,
    so no `mvgaussian` constructor exists.
  - **Multivariate tower Bayes lift**: `dist_expect`/`dist_reweight`/
    `dist_mix` still refuse anything but a univariate tower
    (`univariateOnly`, PplElaborate.fs:1509) — `dist_condition` covers
    *conditioning* a multivariate Gaussian tower, not multi-index polynomial
    likelihood reweighting/mixing.
  - **`ppl.laplace`**: mode-finding via `ad.grad` plus the module's jet
    machinery for the Hessian, to bridge an arbitrary model straight to a
    Gaussian `Dist` tower without sampling — no former of this name exists.
- **P6 — Optional/advanced engines** (NOT STARTED, each independently
  shippable): mean-field ADVI; an SMC/particle filter for state-space
  models (pairing naturally with `mstate` for online summaries);
  expectation propagation over `mvgaussian` factor lists (blocked on the P5
  `mvgaussian` item above); exact discrete-latent enumeration; posterior
  predictive checks.
- **Cross-cutting**:
  - **`lgamma`/`digamma` intrinsics** — DONE (`intrinsics/008-011`); `lgamma`
    differentiates to `digamma` (`src/Grad.fs` `derivRule`), which is what
    makes gamma/poisson/beta `logpdf`/`loglik` HMC-able (§2.8, `ppl/117`).
    **`trigamma` does not exist** — `digamma` itself therefore has no
    derivative rule and is refused inside `ad.grad` (`src/Grad.fs`: "its
    derivative is the trigamma function, which the language does not have"),
    which blocks differentiating anything that calls `digamma` directly (not
    the gamma/poisson/beta *log-densities*, which never call `digamma`
    themselves — only `ad.grad` of a hand-written `digamma` call would need
    the rule).
  - Corpus numbering continued past 069 through **133**; new `__rand_*` pins
    landed in `tests/corpus/rand/005-017`. Both DONE.
  - **Oracle `dump-*` verbs** — DONE: `dump-logpdf`, `dump-edgeworth`,
    `dump-cf`, `dump-conjugate` in `src/ppl/Program.fs`, pinned in
    `src/ppl/ORACLE_PINS.md` (see `src/ppl/README.md` §5 for the workflow).
  - **`ppl` in `InterpDiff.currentSlice`** — was already present
    (`tests/InterpDiff.fs`'s `m5Slice`) before this arc started, covering the
    module generally rather than being added specifically "once samplers
    landed" as the plan phrased it; its snapshot count comment there predates
    corpus 070-133 and is now stale (a test-infra bookkeeping detail outside
    this document's scope, not a missing feature).

**Explicit non-goals** (per the plan's §9, unchanged): trace-based/effect-
handler PPL semantics (Gen/WebPPL-style program traces — this module stays
density-based, Stan-style); NUTS or any other data-dependent trip count at
the Blade level (both `mh` and `hmc` use static, fixed-length chains and a
static leapfrog step count `L`); dynamic model structure (open-universe
models, RJ-MCMC); GPU inference; distribution families with undefined
moments as tower values (Cauchy, low-ν Student-t may appear later behind
`logpdf`/`sample` only, never as a `Dist` tower value).
