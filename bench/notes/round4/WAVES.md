# Checkpoint waves

Each entry is one independently reviewed and tested card. These are ordered assignments,
not permission for a worker to start another card. A completed source census can replace
a proposed implementation with an existing-route control, but must identify that outcome.
The full catalog remains in the campaign README; it is not silently considered complete.

| Wave | Lane A | Lane B | Lane C | Lane D |
|---|---|---|---|---|
| 1, accepted | A01: repair retained inline call lifetime boundaries in both backends | B01: per-run process-memory reports and the existing PM49 triad control | C01: one private stored-input selected seeded terminal | D01: one rank-1 Zarr producer feeding joined scalar folds |
| 2, after wave-1 acceptance | A02: PM01 barrier-chain census/control; prove reuse cannot cross a released inline region | B02: one PM54 touch/release diagnostic control to check resident/commit interpretation; no live-capacity inference | C02: PM10 rank-2 deferred partial-fold census, then one missing admission if established | D02: PM43 one provider's decode/convert-to-required-destination census, then one missing copy-elision admission |
| 3, conditional on wave-2 findings | A03: bounded LLVM return-donor realization established by A02; general last-use release remains separate | B03: one instrumented pool allocation/release accounting pilot, with coverage bounded to the instrumented allocator | C03: PM19 multiple independently seeded terminals over the same private selection | D03: PM42 one adjacent-chunk halo reuse pilot with a fixed cache bound |

Priority rationale: repair ownership first, make measurement honest, then remove a whole
private selection or mapped provider intermediate. The next wave distinguishes already
available routes from missing ones before extending lifetime, partial-fold or decode
ownership. External sorting, join spill, recurrence rings and AD replay require the
separate design gates in the full plan; they are not a justification for unsafe shortcuts.

With three available child slots, the actual dispatch is staggered: A/B/C started first,
D took B's completed coding slot, and the sole Luna runner took C's completed coding
slot. Coding and targeted tests may overlap on different frozen checkouts. Benchmarks
that support performance claims wait for a quiet period. No worker tests its own code.

Only the orchestrator closes a card, after reading the actual diff and the runner's
evidence. Failures return to the same coding lane before later cards are assigned.
The final full suite remains reserved until the entire round's accepted scope is closed.

After B01 and C01 acceptance, B02's independent diagnostic and C02's source-only
census may proceed while Luna validates A01/D01. This does not admit C02 compiler
changes before census review or relax A01/D01 acceptance gates.

Checkpoint update: A01, B02, B03 and D02 are integrated. All 17 A01 targeted categories
passed; combined integration checks remain pending. C02 now builds and is investigating
one LLVM allocation assertion after 560 optimize checks passed. A02's existing-route
census is reviewed for an initial IR/emission diagnostic stage. B03 passed 45 ordinary
scenarios and six ASan positives; B04 is scoping a single PM52 store/consume control.

D03's source census establishes repeated halo chunk reads but does not admit a
cache: no runtime immutable-source identity is proved, and window validation
differs from whole-read validation. Its disposition is an evidenced prerequisite,
not an implemented cache. The same census exposed a pointer-before-allocation
window alias. A separate D04 safe-addressing design precedes cache work; bounded
cursor metadata and immutable source identity remain separate prerequisites.

Latest checkpoint update: D04 safe addressing is integrated as `f8db0d1f` after 551
Zarr passes and one unavailable NetCDF comparison. D05 is a separate source/design
census for uniform run descriptors and an arithmetic cursor. C02 is held: both LLVM
fusion settings violate the paired-error order; C02-L1 restores eager array-valued
kernel inputs before acceptance. A02's initial diagnostics confirm C++ planned reuse
and an LLVM plan-consumer gap. B04's bounded diagnostic source is approved for Luna;
manual exact-executable store/observation audit remains a separate acceptance gate.

Current validation checkpoint: B04 integrated as `de03addd` after all eight native
and saved-output diagnostic gates passed; no performance eligibility. D05 integrated
as `b6f4beec` after 600 Zarr checks and one internal NetCDF skip. C02-L1's latest
test freeze builds and passes 586 optimizer checks; native failure-order and row
controls are next. Earlier test-source failures remain documented. A02 passed all
24 default-all-output native controls and is integrated as a census/control;
its broad category/sanitizer follow-up is consolidated into A03's ownership change.
A03's return-only design is approved, waiting for accepted C02-L1 integration.
B05 designs benchmark observation/timing boundaries. D06's design is approved for
one explicitly granted private spool runtime protocol, with no compiler admission.
No full-suite launch has occurred.
