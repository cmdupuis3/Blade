# Round 3 record

Round 3 of the microkernel work (2026-10-20 .. 2026-10-21): a research round, then three waves on the integration branch
`worktree-bench-fortran`, one squashed commit per item, each after an independent Codex diff review and its fix round.
RESULTS section 17 is the summary; this directory is the evidence. Process artifacts of a finished wave (the per-item
builder briefs and Codex diff reviews) are condensed into one file per wave, one section per former file, verbatim.

- `../round3-microkernel-plan.md` -- the staged plan (waves 1-3, deferred items, the user's decisions incl. A4) and, in its
  section 7, the per-wave execution log (items, review verdicts, fix rounds, integration commits, block checks).
- `research/` -- the four research briefs: A census + remaining losses, B relational region track, C the N01-N10 workloads,
  D orbit / strict-symmetry regions (`PREAMBLE.md` is the agents' contract: a read-only round while a timing session ran).
- `reviews/codex-review-A.md` .. `codex-review-D.md` -- the Codex attack reviews of the research briefs (verdicts per item;
  findings closed by the builder briefs).
- `reviews/wave1-codex-reviews.md`, `reviews/wave2-codex-reviews.md` -- every item's Codex diff review (W2-1 two looks, W2-4
  the looks that gated its rounds) and the wave's merge-level review (wave 2: HOLD on W2-4's lifetime class), one section per
  former file; cite a section as `reviews/wave2-codex-reviews.md#codex-diff-w2-3`.
- `briefs/wave1-builder-briefs.md` (the shared builder preamble with hard constraints 1-7, the wave-1 item briefs and the
  runner brief), `briefs/wave2-builder-briefs.md` (the wave-2 item briefs incl. W2-4's round-4 lifetime brief).
- `w1-ab.md`, `w2-ab.md` -- the harness A/B records: protocol, mirrors, the headline, the decisions each measurement settles,
  the analyst tables (pairwise medians with bootstrap CIs, arm medians, expert ratios, blocks, slower-than-old, sessions;
  wave 2 adds peak working set).
- `gates/w1-gates-summary.md`, `gates/w2-gates-summary.md` -- the five gates on each wave's tip, the examples sweep, the chain
  and A/B driver logs, the merge-level block checks after each code merge.
- `w2-scripts/` -- what defines the wave-2 measurement: `wave2-end.ps1` (the chain: build, gates, examples, mirrors, smoke, A/B,
  analysis), `mirrors-w2.ps1`, `smoke-w2.ps1`, `driver-w2.ps1`, `analyze-w2.py`, and the two post-chain arms `legs0-fixup.ps1`
  (the legs-off saxpy re-run) and `g35-arm.ps1` (the row slots proper at rank 3). One-off merge checks and conflict resolvers
  are not kept.

Gap rows opened or rewritten this round: G51-G53 (wave 3 of round 2, renumbered), G54-G64 (wave 1), G65-G70 (wave 2), and
the rewritten G1 / G35 / G44 / G52 / G69.
