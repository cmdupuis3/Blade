# D01: rank-1 provider map to joined scalar terminals

One card, lane D (`gpt-6.1-sol`, high). Base master `361504b6`; checkout
`.round4-worktrees/d-streaming`, branch `codex/round4-d-streaming`.
Read the campaign README, the S2 stage in the streaming plan, and R06/PM41 sketches.

Implement one genuinely missing route: a single local uncompressed dense rank-1 Zarr
source, pure/total scalar producer, independently seeded scalar fold legs, emitted as
one bounded arithmetic block loop. Refresh the source census first. The current direct
streamed fold and the joined fiber emitter are different paths; the latter's scalar
element consumer refusal is not solved by falling back to a materialized map.

The first useful contract is a shared affine producer followed by seeded sum and sumsq
(optionally an independently typed count only if that exact source form is reachable).
Each leg keeps its original seed, operation and ascending chain; no block partial sums.
Keep FP contraction policy, global logical coordinates, zero/odd-tail semantics and
provider validation order. Narrow to reproducible/strict routes if required rather than
silently claiming identical licensed trees.

Admission must be source-reachable and complete before any original binding is skipped.
Refuse surviving printed/escaped/indexed output demands, effects, unknown source versions,
unsupported codec/backend/shape and overlapping old planner ownership. One provider only;
no asynchronous prefetch or cross-source failure-order proof in this card.

Memory evidence must count actual provider decode granularity, input/staging capacities,
metadata and fold state. Do not use the ordinary streamed map wrapper if it keeps the
full result, full run-offset table or full-height pointer skeleton. Reader diagnostics
that accumulate over a whole read finalize at the original boundary before publication;
do not raise a first-block missing-value diagnostic early.

Add focused positive/refusal witnesses and route assertions. Compare a materialized
`.read` reference with the same print/output selection and independent known values.
Request standalone Zarr tests from Luna; interpreter/LLVM refusal is explicit where
those lanes do not implement provider streaming. An optimizer-inert differential pass
does not prove this route ran.

Only Luna executes builds/emits/tests/benchmarks. The coder writes code and test requests,
reports the precise owned files early, then hands back one frozen candidate for root
review. No next kernel, no independent commit/merge/push, no Claude or delegation.
