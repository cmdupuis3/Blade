# A01 orchestrator review

2026-10-22. Reviewed the actual diff in `a-lifetimes`, base `361504b6`.
Initial reviewed content manifest SHA256:
`b8e5529e8e4b57bb768c157e69244544a53613b1c5f09a1143dbff549492704f`.
Disposition: accepted after all 17 requested targeted categories passed on
freeze `bafdfccef81332208c2aad293bf80a40525b4f8c83d7fd093d041c873cdc1a1b`.
Root rechecked all 29 content hashes, committed source `12b43b4f` and integrated
it as `4189da71`. Combined integration validation remains pending.

The first two build attempts exposed test-helper syntax and overload-resolution
errors. Both were returned to A; the current freeze preserves assertions and adds
explicit helper formatting and `needle: string option`. No category ran against
those failed builds. The subsequent build passed; optimize reported 510 passes
and one diagnostic assertion failure. The inline refusal already retained the
original call, but its reason appeared only in an independent applied group's
evidence. The latest correction records one explicit capability decline per
cached call candidate, with call identity, alongside independent group outcomes.
The test keeps its original assertions and adds an eligible baseline, exactly-one
decline and independent applied-group checks. This diagnostic correction passed
rebuild and optimize (511/0), sibling-folds (37/0/0), and cell-epilogue (87/0/0).
Lowering/admission behavior was unchanged by the diagnostic repair.

LLVM facts then reported 114 passes and one failure: case 019's optimizer-off
control had no free for its private mapped pool. The runner preserved the exact
IR and LLVM. Root traced the scalar kernel call in a tuple member to
EmitLlvm.emitTupleParts: its tuple-returning-call branch matched every IRApp,
including scalar results. The control therefore bypassed normal scalar-call
ownership cleanup. The source call had not been optimized away. The outlined
function was absent, and both allocated pools remained in main without a free.

A narrowed that branch to actual tuple return types, with allocation-specific
outlined cleanup and repeated scalar/escaping array/real tuple controls. Broad
applyKernel ownership changes are outside this correction. An interrupted LLVM
sibling-folds run (22 passes, one skip) is incomplete and establishes no gate.

The replacement `24b64af9` built and passed all allocation-specific lifetime and
scalar/array tuple-member checks. LLVM facts reported 117/1: the genuine tuple
control incorrectly required a frame string. The raw 040 probe retained a real
IR tuple-return call and emitted both reductions inline, with both scalar outputs.
Root inspected resolveFrames: it intentionally removes nonfailing regions' frame
markers and names. The final test-only correction checks the same validated IR
passed to emission, both actual reduction chains and scalar outputs, and absence
of an outlined tuple ABI. It preserves the independent runtime pins. Production
code is unchanged from `24b64af9`. The new freeze still needs its full targeted
queue; prior completed gates were not silently attributed to it. That queue has
now completed: LLVM facts 118, C++ sibling-folds 40, cell-epilogue 87, interpreter
41, optimize 511 and the remaining named gates all passed, including seven clean
ASan lifetime witnesses. Unsupported LLVM/reject probes and 78 documentation
sketches remain explicit skips in [runner results](A01-tests.md).

The change replaces the deletable return-boundary side table with explicit begin/release
IR statements. Reconstruction preserves release even if a final join disappears. Pool
and scope identities are ordinary IR children. C++ statement renderers free through their
existing ownership frames; LLVM uses a nested tracking scope so parent cleanup cannot
free the same allocation again. Interpreter events have no value-level effect.

Review corrections incorporated:

- Check every owned computed pool against the supported dense rank-one cleanup capability
  before retaining any inline, including older join-driven inlining.
- Exclude event-bearing functions from CSE and scratch reuse until those optimizations
  have a region-aware ownership proof.
- Keep resource boundaries distinct from source writes, but make downstream motion
  planners respect a separate transitive lifetime barrier.
- Validate ownership and post-release reads through aliases, captures and embedded type
  extents. Add malformed-IR and freshening witnesses, not just generated comment checks.
- Decline expression/kernel renderers and OMP coverage mode before committing the inline.
  Ordinary direct-call scalar drivers still use the tracked function-body renderer.

The authored tests cover each carried-over failure, CSE on/off, multiple plans per call,
callee scratch-shaped storage, caller failure order, actual LLVM alloc/tracking-slot/free
relationships and strict runtime twins. The LLVM dispatch correction passed all 17
targeted categories on the final frozen binary, including the allocation-specific LLVM
witnesses and seven clean ASan cases; exact totals and skips are in A01-tests.md.
The accepted source is integrated as 4189da71. Combined integration checks remain pending.
The implementation deliberately loses some CSE and reuse opportunities; no performance
or peak-memory improvement is established by these correctness checks.

Historical LLVM lifetime-matched saxpy attribution remains withdrawn. A01 correctness
gates and B01 report validation do not by themselves establish full-size machine-code
stores or fair timing endpoints. No full-suite launch is authorized at this checkpoint.
