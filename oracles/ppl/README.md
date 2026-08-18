# ppl/ — moment/cumulant algebra, in two deliberately separate layers

| Layer | Where | Compiled by | Role |
|---|---|---|---|
| **Compiler layer** (the real thing) | `ppl/compiler/PplElaborate.fs` | `Blade.fsproj` (the main compiler) | the `ppl` module in the language — source-to-source elaboration, pre-typecheck |
| **Reference prototype** (this README's main subject) | `ppl/*.fs`, `MomentAlgebra.fsproj` | `dotnet run --project oracles/ppl` — its own exe; `Blade.fsproj` does NOT reference it | independent oracle + EXPECT-pin generator for the corpus |

The user-facing surface (formers, independence licensing, order accounting,
the `Dist<r,T>` type) is documented in full at `docs/features/ppl.md`. This
README is about the two-project architecture and the prototype's internals.

---

## 1. Compiler layer — `ppl/compiler/PplElaborate.fs`

4506 lines, module `Blade.Ppl.Elaborate`. Wired into the main compile
pipeline at `src/TypeCheck.fs:12483` (`Blade.Ppl.Elaborate.expand program`,
inside `checkModule`, running after `sgs` elaboration and before `math`).
Every former (`moments`, `dist`, `dist_map`, `gaussian`, `mh`, `bayes`, ...
— the full 49-name list at `formerNames`, PplElaborate.fs:84) is recognized
as call-shaped source and rewritten to ordinary Blade source **before type
checking ever starts** — loop-object pipelines, straight-line scalar
arithmetic, or (for the P4 sampling-inference formers) the language's own
recursive-array construct, nothing that needs a new IR node. The P4/P2
sampling primitives (`mh`/`hmc`, `sample`, `dist_sample_approx`) are the
first formers to emit calls into runtime intrinsics (`__rand_*`, and
`hmc`'s emitted `ad.grad(logpost)` surface form) rather than staying pure
source-to-source rewriting — see `docs/features/ppl.md`'s overview for that
distinction. Elaboration failures surface under a single generic code,
`BL5100`, except for `hmc`'s `BL5500` refusal when the model body leaves the
AD-able subset (that one belongs to the `Grad` pass, not the elaborator).

One checker-level piece lives outside the elaborator: `cumulant(d, k)`
projection on a typed `Dist<r,T>` value (including a Dist-typed function
*parameter*, which elaboration can never see) is
`TypeCheck.inferCumulantProj`, gated by the finer-grained `BL3007` order
guard. See `docs/features/ppl.md` §5 and `oracles/ppl/NOTES.md` for the full
typed-Dist arc from surface syntax through checker-level nominal typing to
erasure at `Zonk.fs`.

**`expand`'s passes, for orientation.** The P1-P5 sampling/inference arc
(`docs/plan-ppl-proper.md`) added machinery to `expand` beyond the original
single decl-rewrite pass, documented in full at `docs/features/ppl.md`
§2.7-2.13 (the user-facing surface) but worth naming here so a newcomer can
find them in the file:

- **Pass 1.5, `rewriteBodyFormers`** (PplElaborate.fs:2307) runs before the
  main decl-rewrite pass and handles `logpdf`/`loglik` used in **expression
  position inside a top-level function body** — the density-form model layer
  (plan §4). Each call site is rewritten to the same closed-form arithmetic
  the decl-position formers emit, with the bindings hoisted as statement lets
  into the enclosing block (function body or innermost loop body) so
  `loglik`'s accumulation loop stays at statement level and the function
  stays inside `Grad`'s AD-able subset. Every other former is still refused
  outside decl-RHS position by the later misplaced-use pass.
- **The chain registry** (`chainLens : Map<string, int>`, threaded through
  the main pass starting around PplElaborate.fs:4276) records each `ppl.mh`/
  `ppl.hmc` chain's static length as it is elaborated. It is what lets
  `chain_mean`/`chain_var`/`autocorr`/`ess`/`rhat`, and `dist(chain, r)`'s
  round-trip wrapper, accept a chain *binding* even though a chain carries no
  declared `Array` annotation the ordinary shape-inference routes could see.

## 2. Reference prototype — why the duplication is deliberate

`oracles/ppl/*.fs` (`Combinatorics.fs`, `SymTensor.fs`, `MomentCumulant.fs`,
`Dist.fs`, `Density.fs`, `Streaming.fs`, `Oracle.fs`, `TestHarness.fs`,
`Program.fs`) plus `MomentAlgebra.fsproj` is a **standalone F# executable**, deliberately NOT
listed in `Blade.fsproj`'s compile items and never referenced by the
compiler at any point. It re-implements the same moment/cumulant algebra
— set-partition Möbius inversion, packed symmetric tensor storage, the
Pébay streaming merge kernels, Faà di Bruno pushforwards — from scratch, in
plain F#, with no shared code path with `PplElaborate.fs`.

This is by design, not an accident of history: it is the module's
**independent oracle**. If the compiler's elaborated Blade source and this
prototype's hand-rolled F# arithmetic agree on the same inputs, that is
real evidence the algebra is right — a bug shared between the two would
have to be a coincidence of two independently-written implementations
landing on the same wrong answer, not a single point of failure re-checked
twice. Every `EXPECT` value pinned in `tests/corpus/ppl/*.blade` was
generated by running this prototype, not derived from the compiler's own
output — the corpus never grades the compiler against itself. Keep it this
way: when a new exact former lands in `PplElaborate.fs`, give the
prototype its own from-scratch implementation and a new `dump-*` verb
(§4) rather than importing or delegating to any compiler code.

## 3. Running the prototype

```
dotnet run --project oracles/ppl
```

With no arguments, this runs the full self-test suite
(`TestHarness`-based: `testCombinatorics`, `testSymTensor`,
`testMomentCumulant`, `testDistTower`, `testJetPushforward`,
`testJetPushforwardVec`, `testStreaming`, `testStability`,
`demoDerivedFormulas`, `testFullCircle`, all invoked from `Program.fs`'s
`main`) — silent per-check on pass, `FAIL <name>` printed per failure, and a
`MomentAlgebra: N passed, M failed` summary with a nonzero exit code if
anything failed.

Two extra verbs print oracle datasets to stdout instead of running the
self-tests:

```
dotnet run --project oracles/ppl -- dump-cumulants
dotnet run --project oracles/ppl -- dump-jet
```

## 4. How a corpus pin is produced

The `dump-cumulants`/`dump-jet` verbs (`Program.fs:921` and `Program.fs:987`
respectively) are not general-purpose tools — each one is a fixed sequence
of hard-coded datasets matching specific corpus scenarios, printed at full
`%.12g` precision so the digits can be copied straight into a `.blade`
file's `// EXPECT:` comments.

Concrete example: `dump-cumulants` prints a section headed

```
-- data Z4: [[1,2,4,6],[3,5,4,2]] (N=4) -- 2-chunk merge oracle
```

That literal `[1,2,4,6]` / `[3,5,4,2]` dataset is `tests/corpus/ppl/
025_mstate_merge.blade`'s two shards concatenated: `XA = [[1,2],[3,5]]` and
`XB = [[4,6],[4,2]]` laid end-to-end along the sample axis. The corpus test
streams `XA`/`XB` through `mstate`/`mstate_merge`/`mstate_cumulants` inside
the compiler; the printed `k1`/`k2`/`k3`/`k4` values from this prototype's
from-scratch batch computation over the concatenated array are the
`// EXPECT:` lines in that file — streamed (chunked) and batch cumulants
must agree exactly, which is both the merge-monoid law and the oracle
cross-check in one pin. The same pattern holds throughout: pick (or add) a
`dump-*` section whose printed dataset matches the corpus scenario, run the
prototype, and copy the printed values into the `.blade` file's `EXPECT`
comments.

When a new exact former needs oracle coverage, add a new dataset block (or
a new verb entirely, following the `dump-cumulants`/`dump-jet` pattern) to
`Program.fs` rather than reusing an existing block for an unrelated
scenario — each printed section is meant to be traceable to exactly the
corpus file(s) that cite it.

## 5. The P1/P5 oracle — `Density.fs` and `ORACLE_PINS.md`

The P1 (named families, log-densities, approximate bridge) and P5 (conjugate
posteriors) arcs added a fifth prototype module, `Density.fs`, and a second
pin sheet, `oracles/ppl/ORACLE_PINS.md`, following the exact `dump-cumulants`/
`dump-jet` pattern above rather than inventing a new one:

- **`Density.fs`** (520 lines, compiled between `Dist.fs` and `Streaming.fs`
  in `MomentAlgebra.fsproj`) is three modules in one file, explicitly marked
  ORACLE CODE (its own header comment) because every formula in it duplicates
  what `PplElaborate.fs` emits independently:
  - `Density` — closed-form `logpdf` for every named family, including a
    from-scratch Lanczos `lgamma` (g=7, n=9) that is deliberately **not**
    the bit-exact runtime/interp mirror the compiler needs (that one lives in
    `src/cpp/blade_runtime.hpp` and `src/Interp/Numerics.fs`) — cross-checks
    between the oracle and the compiler are numeric (~1e-12 relative), never
    bitwise, for anything touching `lgamma`.
  - `Expansion` — the Edgeworth/Gram-Charlier density and Cornish-Fisher
    quantile series from a univariate cumulant tower, in plain floating
    point. `PplElaborate.fs`'s approximate-bridge formers (§2.10 of
    `docs/features/ppl.md`) mirror this module's algorithm line for line,
    but over a *symbolic* coefficient ring (`z`, `phi(z)`,
    `lambda_3..lambda_6`) instead of floats, so the collapsed series can be
    emitted as straight-line arithmetic over runtime-read kappas.
  - `Conjugate` — the four closed-form conjugate posterior updates (Normal-
    Normal, Beta-Bernoulli, Gamma-Poisson, and Normal-InverseGamma, the last
    of which the compiler side does not implement yet — see the Roadmap in
    `docs/features/ppl.md`), each cross-checked in the self-test suite
    against brute-force numeric integration of prior times likelihood.
- **Four new `dump-*` verbs** in `Program.fs`, same convention as
  `dump-cumulants`/`dump-jet`: `dump-logpdf` (Program.fs:1050), `dump-
  edgeworth` (Program.fs:1062), `dump-cf` (Program.fs:1076), and `dump-
  conjugate` (Program.fs:1088).
- **`ORACLE_PINS.md`** is where their output was captured and annotated —
  parallel to this README's §4 walkthrough of `dump-cumulants`, but with its
  own precision/tolerance notes up front (values at `%.17g`; compare
  `lgamma`-touching numbers with a tolerance, never bitwise; `-inf` means
  "outside the support" while an invalid *parameter* raises; the Edgeworth
  density's documented negative-tail failure mode; AS241's expected
  floating-point near-antisymmetry). Corpus authors pinning a new
  `logpdf`/`loglik`/`dist_pdf_approx`/`dist_quantile_approx`/`bayes` test copy
  straight out of the matching `ORACLE_PINS.md` block, the same way `dump-
  cumulants`/`dump-jet` pins are copied per §4 above; regenerate the file
  with the command line printed above each block after any change to
  `Density.fs`, and re-diff before touching a corpus `EXPECT`.

## 6. File-by-file tour (compile order, per `MomentAlgebra.fsproj`)

- **`Combinatorics.fs`** — compile-time combinatorics: factorial, binomial,
  the full set-partition lattice (`setPartitions`, Bell numbers), subset
  masks, multinomial compositions. Everything downstream builds on this
  lattice; in a full Blade integration this is machinery the *compiler*
  would run once during code generation, cached, not re-derived per value.
- **`SymTensor.fs`** — packed symmetric tensor storage: rank `r` over
  dimension `d`, one float per canonical (non-decreasing) multiset,
  `C(d+r-1, r)` entries. Hand-rolled model of exactly what Blade's
  `SymIdx<r, d>` inclusive-combinadic placement class (`PlaceCombinatorial
  SymSymmetric`) does in the real compiler.
- **`MomentCumulant.fs`** — moment ↔ cumulant conversion as Möbius
  inversion on the set-partition lattice: one weighted convolution shared
  by both directions, differing only in the per-block-count weight. This is
  the load-bearing algebra of the whole module.
- **`Dist.fs`** — the cumulant numeric tower (`Dist.T`, `Dim`/`Order`/
  `Kappa` array): named-family cumulant formulas (Gaussian/Exponential/
  Gamma/Poisson, `Dist.fs:28-40`), independent sum/scale/affine pushforward
  (all exact), scalar and vector Faà di Bruno jet pushforward
  (`jetPushforward`/`jetPushforwardVec`, both with a strict-vs-closed order
  budget), and exact polynomial moment expansion (`polyMoments`).
- **`Density.fs`** — log-densities, the Edgeworth/Cornish-Fisher approximate
  bridge, and conjugate posterior updates (§5 above); the P1/P5 oracle.
- **`Streaming.fs`** — derived mergeable central-comoment accumulators: the
  arbitrary-order, multivariate generalization of Welford's algorithm
  (Pébay's formulas). The merge kernel is *derived* once per `(d, r)` from
  the subset-lattice expansion, not hand-coded per order — the same
  "compiler emits a plan, plan is straight-line code" spirit as the rest of
  the module.
- **`Oracle.fs`** — differential-oracle support: seeded samplers
  (Gaussian/Exponential/Gamma/Poisson via `System.Random`) plus a two-pass
  direct reference for central comoment sums (`twoPassCentral`) — the ground
  truth that validates the *derived* streaming kernel against a dumb,
  unoptimized computation of the same quantity.
- **`TestHarness.fs`** — minimal pass/fail harness mirroring BladeML's
  style: `check`/`checkClose`/`checkCloseRel`/`checkArrayClose`/
  `checkThrows`, silent on pass, `FAIL <name>` on failure, a summary + exit
  code at the end.
- **`Program.fs`** — the entry point: the full self-test suite (default,
  no-argument invocation) plus six oracle-dump verbs: `dump-cumulants`
  (`Program.fs:921`) and `dump-jet` (`Program.fs:987`), described in §4
  above, and `dump-logpdf`/`dump-edgeworth`/`dump-cf`/`dump-conjugate`
  (`Program.fs:1050/1062/1076/1088`), described in §5.
