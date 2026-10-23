# Round 4: peak memory, bandwidth and streamed computation

Status: STARTED, 2026-10-22. Integration branch: `codex/round4-peakmem`.
Base: master `361504b6`. No round-3 worktree is reset, merged or modified by this round.
The [60-family plan](../peakmem-microkernel-plan.md), [streaming plan](../peakmem-streaming-optimizer-plan.md)
and [reviewed sketches](../peakmem-sketches/README.md) are the backlog, not implementation evidence.
Their source anchors describe an older snapshot and must be refreshed per kernel.

## Ownership and checkpoint rules

The user requested four Sol/high implementation agents, one Luna/high test runner,
and the orchestrator as final reviewer. Coding lanes A–D use `gpt-6.1-sol` / high;
the sole runner uses `gpt-6-luna` / high. No Claude calls while its token budget is exhausted.
This session currently exposes only three active child-agent slots. The user selected
rotating three coding agents through the four lanes and allocating the runner a slot
at checkpoints; never claim all five child agents are simultaneously active.

Each lane has an isolated checkout and branch beneath `.round4-worktrees/` in the original
repository. The integration checkout is the only place the orchestrator combines work.
Agents own one kernel/card at a time and stop after handing it off. No worker self-assigns
the next item, merges into integration, pushes, or launches a build/test/benchmark.

| Lane | Branch / checkout suffix | First card | Later emphasis |
|---|---|---|---|
| A | `codex/round4-a-lifetimes` / `a-lifetimes` | A01: repair the carried-over inline lifetime boundary before expanding reuse | Root ownership, reuse, layouts, recurrences and AD |
| B | `codex/round4-b-measurement` / `b-measurement` | B01: memory-report foundation with one bandwidth control | Honest measurement, one concrete control/kernel per checkpoint, allocator instrumentation |
| C | `codex/round4-c-terminals` / `c-terminals` | C01: PM19 stored-input selected terminal, after emission census | Terminal folds, selection, groups and relational consumers |
| D | `codex/round4-d-streaming` / `d-streaming` | D01: S2/PM41 rank-1 Zarr map-to-joined-fold bridge | Bounded providers, shared region facts, spool and external operators |

Master includes round-3 waves 1–2, including membership/shared-selection and retention
work. It also explicitly records four remaining inline-lifetime holes. A01 must follow
the [existing defect brief](../round3/briefs/wave2-builder-briefs.md#w2-4-round4-lifetime)
and its review, not build a competing lifetime fix. Unfinished speed work outside master
remains separate; refresh its ownership before assigning overlapping later kernels.

Checkpoint state machine:

1. **Scoped:** record the exact kernel contract, observed missing route, owned files,
   expected storage change, refusal cases and targeted validation request.
2. **Implemented:** one coherent diff plus witnesses and a handoff; no worker execution.
3. **Reviewed:** orchestrator reads the actual diff, effects/aliases/failure order, numeric
   graph and ownership. Fixes return to the same lane before acceptance.
4. **Targeted-tested:** Luna builds and tests the exact snapshot in isolation. Record
   commands, revision/content identity, pass/fail/skip totals and actual route evidence.
5. **Integrated:** orchestrator combines the reviewed diff, requests affected integration
   checks, and makes a single-kernel checkpoint commit. No unrelated worker changes.
6. **Measured or explicitly unmeasured:** memory and timing claims require suitable
   evidence; noisy runs are not a reason to declare a win. Then assign the next kernel.

An empty/no-gain census is a useful checkpoint, but does not count as an implementation.
A test that accepts an unchanged fallback does not establish route completion.

## Wave order and complete coverage

[Concrete checkpoint waves](WAVES.md) name the first three proposed assignments per lane.
Later waves are conditional on each preceding source census and reviewed test result.

These are dependency bands. A lane takes one item from its current band; the list is not
permission to implement every item in that cell in one change. Within a band prefer the
largest removable intermediate with the narrowest established legality proof. A required
output stays required. Already-landed routes become controls rather than duplicate passes.

| Band | Kernel families | Pipeline coverage | Dependency / gate |
|---|---|---|---|
| 0: safety and evidence | A01 lifetime defect; PM49 first bandwidth control; measurement foundation | S0 allocation/capability census | Preserve master behavior, add measurement coverage labels; no invented live-byte metrics |
| 1: first storage wins | PM01, PM02, PM09, PM10, PM17, PM19, PM41 | ST01, ST02, ST06 | Close lifetime defect; stored-input totality; provider whole-read validation and actual route evidence |
| 2: sharing and local storage | PM03, PM04, PM06, PM07, PM08, PM11, PM18, PM52, PM53, PM60 | ST04 | Root/alias certificates, independent fold graphs, execution-wide capacity accounting |
| 3: row and layout consumers | PM12, PM13, PM14, PM15, PM25, PM26, PM27, PM28, PM29, PM30, PM31, PM32, PM50, PM51, PM57, PM58 | ST07, ST12 resident arms, ST13 | Exact traversal and contraction order, source-version preservation; coordinate tensor speed owner |
| 4: grouping and joins | PM20, PM21, PM22, PM23, PM24, PM56 | ST03 selection/group variants, ST08 resident arm | Reuse membership/shared-selection owners; key regimes, empty groups and stable pair order |
| 5: recurrences and halos | PM33, PM34, PM35, PM36, PM37, PM42, PM43, PM44 | ST10, ST11 | Shared PM33/N01 port; physical boundary geometry, bounded metadata, decoder and ghost ownership |
| 6: region sharing and sequential spill | PM05, PM46, PM55 | ST05, ST09; S3/S4 | Whole-region demand/ownership, exact replay, explicit memory/disk policy, checked spool and cleanup |
| 7: external barriers and sinks | PM16, PM45; external extensions of PM20/21/23/24 | ST08 external, ST12 external, ST14, ST15, ST16 | Stable sort domain, bounded merge progress, skew-safe join, provider commit/reconciliation |
| 8: derivatives and reproducible reuse | PM38, PM39, PM40, PM47, PM48, PM59 | ST17, ST18 | Exact original adjoint graph, checkpoint boundaries, addressed RNG reachability and immutable identity |
| 9: policy and capacity closure | PM54; repeat relevant PM06/08/49–55 controls | S6 policy across admitted ST cases | No silent retention/default change; quiet interleaved odd-size measurements and declared coverage |

Every PM01–PM60 and ST01–ST18 has an owner/band. Later research gates are real work,
not silently dropped tasks. A card can close as already-covered, implemented, measured,
or a specific evidenced refusal requiring a new design; the ledger must distinguish them.
Do not claim the whole round complete while required implementation or review remains.

## Final-wave scope update

The user closed further kernel expansion because tokens are running low. Finish the current A05, B08, C06 and D09 implementation/validation cards, then review and validate the integrated branch. A06 remains a source-only planning note; all broader unimplemented PM/ST catalog entries are explicitly deferred beyond this round. No additional kernel or research wave is required for round closure. Preserve the single final full-suite launch below.

## One test runner; one full-suite launch

Luna is the only agent allowed to build, emit, compile, execute fixtures, run tests,
generate executed oracle pins, run sanitizers or benchmark. Coding agents provide exact
requests and may read files or inspect diffs. This centralizes compilation as well as
runtime scratch access. The runner never tests a checkout while its coder is editing it:
use a committed snapshot or an explicitly frozen handoff with content hashes.

Maintain a single serialized queue in `TEST-QUEUE.md`. Each checkout is a private
working directory because `generated_cpp_tests/` is cwd-relative. Run Blade test categories
from that isolated worktree root: some fixtures intentionally use repository-relative
paths. Harness reports and independent diagnostics can use a scratch subdirectory.
Save complete stdout/stderr logs for every test category. Builds against the
same source tree do not overlap either. Do not run hidden full/default suites through
scripts, aliases, chained wave gates or delegated commands.

Per kernel: run the smallest relevant build, positive/refusal cases, optimizer on/off
and interpreter/backend checks the route actually supports. Record unsupported-provider
and missing-toolchain skips honestly. Integration checks broaden only for changed shared
code. Performance runs require a quiet period: pause coding/build activity first.

**The full default `blade test` suite is reserved for the end of the entire round and
may launch once.** Before launch, the orchestrator confirms all implementation/review
checkpoints are closed and targeted gates pass. The runner writes `full-suite.started`
with the exact integration revision before invoking it, and then records the result.
If that run fails, fix and run targeted regressions; do not silently launch the full
suite a second time. Optional OMP/LLVM/interpreter/provider gates are explicitly named
targeted requests, not extra full-suite sweeps. The single full-suite launch has completed; its original result is preserved below.

## Current checkpoint ledger

| Card | Owner | State | Evidence |
|---|---|---|---|
| Setup | Orchestrator | Branch created | `codex/round4-peakmem` from master `361504b6`; original checkout untouched |
| A01 | Sol/high A | Integrated `4189da71` | All 17 targeted categories passed, including seven clean ASan witnesses; explicit LLVM/docs skips in [results](A01-tests.md); combined checks recorded in FINAL-tests.md |
| A02 | Sol/high A | Census/control integrated `d42aec80` | Diagnostics plus 24 all-output native controls; broad ownership gates move to A03's implementation; [review](A02-review.md), [results](A02-tests.md) |
| A03 | Sol/high A | Integrated `f10ca17f` | Facts 128/0/0, optimize 608/0, affected categories, 12 LLVM partial-fold controls and 10 sanitizer executions pass; [review](A03-review.md), [results](A03-tests.md) |
| B01 | Sol/high B | Integrated `38c41096` | 10 unit fixtures, 26 check arms, full-cell diagnostics across 10 backend/policy combinations; [results](B01-tests.md); performance unmeasured |
| C01 | Sol/high C | Integrated `8c6571a7` | 524 optimize passes; C++ and LLVM ON/control runtime twins; [results](C01-tests.md); combined checks recorded in FINAL-tests.md |
| D01 | Sol/high D | Integrated `f0ee9797` | Zarr: 389 passes, one NetCDF library skip; [review](D01-review.md); combined checks recorded in FINAL-tests.md |
| B02 | Sol/high B | Integrated `b4ac861e` | 21 unit fixtures and six native diagnostic workloads passed; [review](B02-review.md); no performance claim |
| C02 | Sol/high C | Integrated `0c5c3e7d` with LLVM eager row-input repair | 586 optimizer checks, 36 native arms, facts/goldens and SQL/interp/opt-diff pass; eight bounded LLVM refusals recorded; [review](C02-review.md), [results](C02-L1-tests.md); combined checks recorded in FINAL-tests.md |
| C03 | Sol/high C | Integrated `6fbe765b` + `302405bf` | 653 optimizer checks, 48 unique native arms and 39 plan/emission checks pass; fair baseline and refusals reviewed; [review](C03-review.md), [results](C03-tests.md); combined checks recorded in FINAL-tests.md |
| D02 | Sol/high D | Integrated `e43d14d3` | PM43 required resident destination; 510 Zarr passes, one internal NetCDF skip; [review](D02-review.md); combined checks recorded in FINAL-tests.md |
| B03 | Sol/high B | Integrated `307ba755` | 45 scenarios/494 assertions and six clean ASan positives/81 assertions; LSan unsupported; [results](B03-tests.md); source-tree pool API accounting only |
| B04 | Sol/high B | Integrated `de03addd` | One PM52 producer/consumer control retaining both full outputs; all eight numeric/provenance/store/observation gates pass; [results](B04-tests.md); performance unmeasured |
| B05 | Sol/high B | Design checkpoint accepted; implementation gated | External opaque task boundary recommended; [review](B05-review.md); B04 remains performance-ineligible |
| D03 | Sol/high D | Census closed, implementation gated | Integrated `45f5e9b8`; repeated chunk reads evidenced, but runtime source identity and bounded cursor/addressing prerequisites remain; [census](D03-census.md) |
| D04 | Sol/high D | Integrated `f8db0d1f` | Safe rank-one streaming-window addressing; 551 Zarr passes, one internal NetCDF skip; [review](D04-review.md), [results](D04-tests.md); combined checks recorded in FINAL-tests.md |
| D05 | Sol/high D | Integrated `b6f4beec` | Uniform segmentation descriptor and arithmetic cursor; 600 Zarr passes, one internal NetCDF skip; [review](D05-review.md), [results](D05-tests.md); combined checks recorded in FINAL-tests.md |
| D06 | Sol/high D | Integrated `02325693` + `fafd232a` | Repaired harness 256/0/1 (unavailable symlink probe); original failed run retained. Explicit-grant runtime prerequisite only; no compiler admission/default disk use; [review](D06-review.md), [results](D06-tests.md) |
| D07 | Sol/high D | Design checkpoint accepted `363269a7` | Exact statistical-barrier target and missing policy/accounting/fault-drain obligations; [census](D07-census.md), [review](D07-review.md); compiler admission remains unimplemented |
| D08 | Sol/high D | Integrated499e1781 | C++17 compatibility, D06 protocol256/0/1, D08 focused82/0/0 and standalone Zarr601 outer checks pass; one optional NetCDF skip; combined checks recorded in FINAL-tests.md; [review](D08-review.md), [results](D08-tests.md) |
| B06 | Sol/high B | Census accepted | Existing pool ledger misses the selection allocations; no measured whole-kernel saving; [review](B06-review.md) |
| C04 | Sol/high C | Integrated c521a81b (kernel bfefabe5) | 706 optimizer checks,32 native arms,32 emit/plan checks and final SQL/interpreter/opt-diff gates pass; [review](C04-review.md), [census](C04-census-results.md) |
| A04 | Sol/high A | Integrated38256bb1 (kernel6c9d6e1d) | 151 facts,653 optimizer checks, affected categories, all-output native controls and four033 sanitizer arms pass; combined checks recorded in FINAL-tests.md; [review](A04-review.md), [results](A04-tests.md) |
| B07 | Sol/high B | Integrated27d31e74; diagnostic checkpoint accepted | 14 fixtures and8 canonical native numeric checks pass; opaque task, separate observer and matched cleanup; [review](B07-review.md); all eight canonical and one prepared native enclosure audits and saved validators pass; performance ineligible |
| C05 | Sol/high C | Integrated78c0e903 | 757 optimizer checks,12 native arms and SQL53/interp54/opt-diff53 pass (two expected reject skips); [review](C05-review.md); combined checks recorded in FINAL-tests.md |
| A05 | Sol/high A | Integrated5c31834e + build repairb5fc1bb6 | All targeted categories, six paired raw emissions and six strict native/sanitizer arms accepted; [review](A05-review.md), [results](A05-tests.md) |
| A06 | Sol/high A | Deferred implementation; census integratedce5f1292 | C++ same-cell return parity planning only; [census](A06-census.md) |
| D09 | Sol/high D | I1 integrated59e261b4; I2 integratedb7a8b8fe | Guard and certified direct-row paths accepted; I2 focused123/0/0; combined Zarr602/0, one internal NetCDF skip; [review](D09-review.md), [results](D09-I2-tests.md) |
| B08 | Sol/high B | Integrated947549d3 | 561 optimizer checks, both actual emission controls and index-types409/0/0 accepted; [review](B08-review.md), [results](B08-tests.md) |
| C06 | Sol/high C | Integrated0672123a + repairs2352b78c/3275b886 | 789 optimizer checks and28 strict native arms pass; actual allocation/order proof accepted; [review](C06-review.md), [results](C06-tests.md) |
| Full suite | Luna/high runner | Completed once; all eleven repairs accepted | 8,279 passed, 11 failed, 1 GR-render skip; original result retained in [results](FINAL-tests.md); repair evidence in [targeted results](FINAL-repair-tests.md) |

Per-card handoffs and reviewer dispositions live beside this file. Historical plans and
sketches retain their original dates and unimplemented status; this ledger records new work.

## Round closure

Round 4 is complete. Root accepted the final four kernel cards and the combined LLVM/Zarr/interpreter checks. The single full suite reported 8,279 passed, 11 failed and one GR-render skip. All eleven failures were repaired and passed targeted validation; the full suite was not repeated. [Final review](FINAL-REVIEW.md), [original suite evidence](FINAL-tests.md), [repair review](FINAL-REPAIRS-REVIEW.md) and [targeted repair evidence](FINAL-repair-tests.md) are authoritative over historical pending states in earlier cards.

Production repairs end at 2a11bb5f. The final documentation checkpoint also records the generated BL8016 Surface entry. Optional native NetCDF and Windows symlink checks remain explicitly skipped where recorded. Peak-memory and bandwidth measurements remain unclaimed; diagnostic controls do not establish performance eligibility. A06 and broader unimplemented PM/ST entries remain deferred.
