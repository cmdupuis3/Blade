# A02: barrier-chain reuse control and lifetime boundary census

Scoped only; prerequisite A01 is not yet accepted. Do not assign implementation
or infer a missing optimization until its final targeted gates close.

One PM01 control: a function-local chain of equal-shape plain dense maps with
real scalar observations between stages. Keep the final array and every scalar
required. Use odd extents and at least two chain lengths. Establish the actual
IR, planner and emitted allocations first: source compute/barrier spelling
does not prove that fusion left the intended workload in place.

Existing planPoolReuse already donates dead, unaliased equal-shape local pools.
The first objective is to distinguish that implementation from a missing route,
and verify that A01's released inline region cannot become a later reuse donor.
The planner's logical peak estimate is not physical capacity or process memory.
Count actual backings and release locations, and state retained-cache coverage.

Required controls: knob-off mirror with the same outputs; an alias/view retained
until after a later map; an inline call with explicit A01 begin/release events;
and a supported ordinary function without events where existing reuse still
applies. Ensure root identity and alias metadata, not printed names, establish
the relationships. Do not add in-place updates or early last-use free in this
card; those require different proofs and belong to PM02/PM03.

The source census should identify whether C++ and LLVM honor each existing
reuse plan and how function-return ownership interacts with the final required
array. Do not claim backend parity merely from numerical output. A01 event
functions intentionally decline reuse; preserving that refusal is a success.

If no missing admission is found, close as an existing-route control with
independent output pins and actual allocation/lifetime evidence. If a hole is
found, stop at a precise proposal for root review. Luna alone generates and
executes diagnostics, category gates or sanitizer checks. No full suite or
performance measurement is authorized by this brief.
