# Checkpoint waves

Each entry is one independently reviewed and tested card. These are ordered assignments,
not permission for a worker to start another card. A completed source census can replace
a proposed implementation with an existing-route control, but must identify that outcome.
The full catalog remains in the campaign README; it is not silently considered complete.

| Wave | Lane A | Lane B | Lane C | Lane D |
|---|---|---|---|---|
| 1, accepted | A01: repair retained inline call lifetime boundaries in both backends | B01: per-run process-memory reports and the existing PM49 triad control | C01: one private stored-input selected seeded terminal | D01: one rank-1 Zarr producer feeding joined scalar folds |
| 2, after wave-1 acceptance | A02: PM01 barrier-chain census/control; prove reuse cannot cross a released inline region | B02: one PM54 touch/release diagnostic control to check resident/commit interpretation; no live-capacity inference | C02: PM10 rank-2 deferred partial-fold census, then one missing admission if established | D02: PM43 one provider's decode/convert-to-required-destination census, then one missing copy-elision admission |
| 3, conditional on wave-2 findings | A03: PM02 last-use release pilot with one explicit backing-root/alias proof | B03: one instrumented pool allocation/release accounting pilot, with coverage bounded to the instrumented allocator | C03: PM19 multiple independently seeded terminals over the same private selection | D03: PM42 one adjacent-chunk halo reuse pilot with a fixed cache bound |

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

Checkpoint update: A01, B02 and D02 are integrated. All 17 A01 targeted categories
passed; combined integration checks remain pending. C02 is repairing test setup
errors found by the build before its targeted execution. A02's existing-route census
is in progress. B03's allocator-accounting implementation has passed source review
and is frozen for the sole runner's validation.

D03's source census establishes repeated halo chunk reads but does not admit a
cache: no runtime immutable-source identity is proved, and window validation
differs from whole-read validation. Its disposition is an evidenced prerequisite,
not an implemented cache. The same census exposed a pointer-before-allocation
window alias. A separate D04 safe-addressing design precedes cache work; bounded
cursor metadata and immutable source identity remain separate prerequisites.
