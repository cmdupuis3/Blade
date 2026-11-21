# Peakmem implementation sketchbook

Status: SKETCHED AND STATICALLY REVIEWED, 2026-10-21.
**Not compiled, emitted, run, tested or benchmarked.**
Source baseline: `667250cd`. This directory contains illustrative implementation shapes
for the [60-family memory plan](../peakmem-microkernel-plan.md) and the
[18-pipeline streaming extension](../peakmem-streaming-optimizer-plan.md).

The code blocks describe what the compiler might generate or what private compiler/runtime
helpers might do. They are not production code, build inputs, new Blade APIs or benchmark
fixtures. Named helper operations include explicit contracts; syntax and integration are
intentionally incomplete. Loops in C++/pseudocode are emitted implementation details,
not suggested imperative Blade source.

| File | Coverage | What is sketched |
|---|---|---|
| [00-region-runtime.md](00-region-runtime.md) | R01–R08 | Region discovery/admission/ownership, capacity ledger, borrowed windows, actual rank-1 map-to-joined-fold loop, checked sequential spool, recovery and sink lifecycle |
| [01-lifetimes-folds-layout.md](01-lifetimes-folds-layout.md) | PM01–PM16, PM25–PM32 | Storage donation/release, strict scalar/partial/joined folds, normalization, permutation/layout consumers and structured operators |
| [02-selection-relational.md](02-selection-relational.md) | PM17–PM24, PM55–PM58 | Stable selection/group/join algorithms, membership representation, capacity policy, masks, structural segments and sparse terminals |
| [03-recurrence-ad-providers.md](03-recurrence-ad-providers.md) | PM33–PM48, PM59 | Ring/snapshot/checkpoint forms, cotangent parentheses, chunk/halo/decode/sink/replay/RNG mechanisms and primal lifetimes |
| [04-controls-and-runtime-shapes.md](04-controls-and-runtime-shapes.md) | PM49–PM54, PM60 | Bandwidth/locality/worker/page controls and runtime-shape storage transfer |
| [05-composed-pipelines.md](05-composed-pipelines.md) | ST01–ST18 | How the individual kernels compose across shared streams, barriers, external operators, sinks and AD |
| [SECOND-LOOK.md](SECOND-LOOK.md) | Static review | Findings, corrections, unresolved implementation questions, and what was not verified |
| [CLAUDE-REVIEW.md](CLAUDE-REVIEW.md) | Independent review | Claude's read-only review of the initial shared runtime, recurrence/AD/provider and pipeline drafts; dispositions are in SECOND-LOOK |

Read the shared runtime first for proposed ownership/reader/spool vocabulary, then a
kernel family and its composed pipeline. Every PM family has its own section; ST sections
specify the inter-stage schedule rather than duplicating each inner loop.

## Contracts that every sketch inherits

- Required arrays remain allocated/stored through their stated observation lifetime.
  A terminal-only task is a separate contract, not a faster replacement for a resident one.
- Strict folds retain the original seed, cell order and arithmetic parentheses. Scalar
  fusion does not force FMA when contraction is disabled. Licensed algorithm alternatives
  need separately stated trees and numerical expectations.
- A complete demand census includes aliases, captures, printing, index/extent uses,
  provider side tables and callbacks. Full backing-root lifetime is what permits reuse.
- Initial moved/interleaved compute is pure/total under a concrete admission. Provider
  failures preserve their validation order; generic ordered-failure proofs are deferred.
- Storage bounds count physical capacity, metadata, codecs, queues, workers and retained
  pools. Existing inputs and required outputs are included exactly once. They do not
  claim to cap RSS, OS page cache or untracked library storage.
- New scratch/resource faults belong to an explicit resource policy. They are not silently
  equated with failures of a program that never used disk. Public output remains behind
  the required validation/commit boundary.
- These sketches do not take ownership of work already assigned in the speed campaign.
  Shared selection/group/join, N09 tensor work and PM33/N01 windowing retain their owners.

## Suggested first implementation cuts, when implementation is authorized

1. Census an actual missing terminal-selection or partial-fold admission against the best
   existing source idiom; do not build a replacement for storage already eliminated.
2. Implement one private, pure/total stored-input terminal with a complete demand proof.
   Keep it disjoint from existing storage planners until shared ownership is available.
3. Connect the existing rank-1 Zarr block reader to composed total maps and independently
   typed fold legs (R06/ST02). Require route evidence and fixed-block capacity scaling.
4. Add one fixed-width sequential spool for the unchanged two-pass statistic graph
   (R07/ST09), with an explicit disk budget and owned cleanup lifecycle.

External sort/group/join, public sinks, concurrency and broad AD replay have their own
preconditions. Their sketches make the work concrete; they do not make it small or proved.
The second look records the issues found by reading rather than claiming execution evidence.
