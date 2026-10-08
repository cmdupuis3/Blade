# Latent symmetry learning — evidence

Supporting material for [`docs/plans/plan-latent-symmetry-learning.md`](../../plans/plan-latent-symmetry-learning.md)
(2026-10-08). Nothing here is part of the build or the test suite.

- `RIGOR.md` — the rigor review: theorems with proofs, corrections to the brief, a
  30-row claim ledger (theorem / heuristic / open), references. The plan's §3 is its
  condensation.
- `checks/*.py` — the numerical spot-checks the review cites (numpy/scipy): the
  poly-MMD = moment-matching identity, the factor-2 Hessian identity, the finite-sample
  moment nullspace, the full `so(3)` canonicalization against the certifier's
  `MLLieDischarge.blockGenerator` tables, the small-algebra invariants table, the
  Taylor-18 `expm` thresholds and Fréchet-derivative pins.
- `probes/*.blade` — the 46 probe programs behind the plan's §2 capability table, run
  against the 2026-10-07 binary with `blade run`; `probes/refusals/` holds the
  minimal programs for each spelling rule in §2.2; `probes/ref/*.py` are the hand
  values; `probes/checkpins.py` re-runs a probe and checks its `// EXPECT:` lines.
  These are NOT corpus tests (several are deliberate refusals, pins are informal);
  P0 turns them into `tests/corpus/lie/`.
