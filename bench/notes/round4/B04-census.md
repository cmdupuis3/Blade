# B04 design checkpoint: PM52 immediate stored-array consumer

2026-10-22. **Design accepted; diagnostic implementation authored, unexecuted. No speed claim.**
Checkout `C:/Users/cdupu/Documents/GitHub/Blade/.round4-worktrees/b-measurement`,
branch `codex/round4-b-measurement`. Initial census source base was `a7048cd2`.
Root then merged accepted integration: current base `2fd60cb8`, tracked tree matching
integration `60e320f6`, including A01. The observations below retain their historical
source identity; fresh executable evidence is still required on the combined base.
Owned files: this census, [B04-contract.md](B04-contract.md), the historical
[B04-fixture-sketches.md](B04-fixture-sketches.md), the concrete diagnostic directory
`bench/diagnostics/store-consume/` and [B04-handoff.md](B04-handoff.md).
No production/harness/policy edits.

## Choice and source census

Choose exactly one contract: **PM52 immediate materialized array consumer**,
`p = 1.5*x+y; q=p*p; checksum=sum(q)`. Both full arrays p and q remain required
outputs through an observation after the stop line. Original x/y are stored immutable
inputs and feed the separate input checksum. This is a diagnostic control proposal,
not a new compiler optimization. Distant and no reuse are different tasks, excluded.

The integration plan PM52 row (`bench/notes/peakmem-microkernel-plan.md:412`) and
`bench/notes/peakmem-sketches/04-controls-and-runtime-shapes.md:67` require mandatory
producer stores even when the consumer forwards a scalar. The latter explicitly says
that removing a reread does not remove required output capacity. Both were read from
the integration checkout, since the lane copy is absent.

Existing `bench/kernels/saxpy/saxpy.blade:30` already forces y2 and then reduces its
squares; natural Fortran assigns y2 then reads it, expert writes y2 and accumulates
from scalar v. Its B01 PM49 spec already permits the scalar-consumer fusion. Repeating
that scalar-only control would add little. B04 makes the consumer output q mandatory
as well: it tests two retained store streams, not just scalar arithmetic or a terminal
fold that can discard its producer. Its scalar checksums mathematically match saxpy,
which is useful pin cross-evidence, but **does not validate q stores**.

Current C++ source dispatch supports forced materialization:

- `src/CodeGenBinding.fs:668` dispatches IRCompute to genComputeBinding.
- `src/CodeGenBinding.fs:1410` forces deferred map inputs before the consumer nest;
  `:1443` calls genApplyCombinator for the selected output binding.
- `src/CodeGen.fs:329` handles a function-body forced map by the statement-form
  apply emitter, preserving allocation/extents/loop names for later consumers.
- `src/CodeGenLoopNest.fs:3990` explains module-level allocations survive for
  compute/print/exit; scoped function/loop allocations use separate cleanup frames.

This supports a source expectation of explicit p and q construction from two forced
bindings. It does not prove the optimized route, fusion, allocation count, stores,
non-aliasing, lifetime or native traffic. No new cross-binding store-preserving fusion
is established by this census. Native g++/clang may transform the emitted code again.
Fresh plan/IR/emission and exact executable audits are necessary, in both lanes.

`src/EmitLlvm.fs:8087` now applies print selection, unlike the historical claim in
`bench/kernels/README.md:169`. This is a source finding, not a passing backend witness.
At the initial `a7048cd2` census the lane ancestry was B01/B02/B03 over master and did
not contain A01. Root's subsequent merge `2fd60cb8` now includes accepted A01. This
corrects ancestry, not executable evidence: do not reuse earlier emitted binaries.
Validation must record the combined reviewed source and newly compiled identities.

## Exact observer gate

**Existing harness cannot express an opaque full-storage observer outside timing.**
`bench/harness/run.py:1080` compiles Blade with `--print checksum,input_checksum`;
`:1341` parse_contract accepts the three contract lines and rejects array extras.
There is no output-buffer sink, opaque call, or full-storage observation metadata in
that path. Windows process peaks cannot establish stores or buffer survival.

C++ `src/CodeGen.fs:3570` genMainWrapper places body, stop timestamp, then printCode.
Its `genPrintArrayFlat` (`:2533`) visits rank-one array cells. LLVM selects arrays in
emitPrints and routes to emitArrayPrint (`:7971`, `:8147`); its exact timing/cleanup
placement must be checked in the emitted fixture. Selecting p/q can provide a full
formatted diagnostic after the kernel timer, but top-level print support alone is
**not** store evidence. The exact binary must still contain distinct stored arrays,
post-stop loads from both buffers and no early cleanup. Formatting also contaminates
whole-process wall time and process peaks; those runs are diagnostic-only.

Fortran `bench/fortran/bench_support.f90` has BENCH_DUMP helpers and saxpy invokes them
after stop, but these are explicitly separate diagnostic builds. A dump-only twin
cannot establish that the scalar-only timed executable retained its stores.

Therefore normal scalar-only harness admission is **gated**, not completed by this
proposal. Before any timing comparison, root must approve a same-binary post-stop
observer contract for every admitted arm: an opaque routine in a separately compiled
translation unit, given both live buffers/counts, consumes every cell after stop and
before explicit release; its implementation and linkage must prevent elimination,
scalar substitution or LTO peeking. It must report observation success separately
from the two numerical checksums, without full-array formatting in the timed region.
Current Blade sources/harness expose no established hook for that observer. Designing
its minimal bench-only driver seam is a separate approval gate, not permission here
to inject generated code, alter runtime/compiler, silently disable optimization, or
register an unobservable benchmark. No ABI/FFI capability is assumed.

## Narrow implementation proposal for root disposition

Approve only an initial **diagnostic-only** implementation of this one contract:
one idiomatic Blade source with top-level p/q, natural/expert Fortran mirrors, one
independent R5 reference and focused cell/output checks. Use existing post-stop print
and BENCH_DUMP mechanisms outside the harness headline path, at modest odd extent.
Save exact artifacts and audit stores/reads/lifetimes. Do not add kernel.json, change
run.py/packaging, or claim a timed control yet. A full-size census can be emit/audit
only; large formatting runs are unnecessary for this first checkpoint.

If root instead requires immediate timed admission, resolve the opaque-observer seam
first; this census does not meet that gate. No no-reuse/delay arm, non-temporal intrinsic,
cache-flush trick, allocator-policy arm, compiler pass or instrumentation expansion is
included. B03 remains source-tree C++ pool API accounting, not emitted LLVM/provider
or full-process allocation coverage.

## Source identity and next runner audit

Historical raw working-tree SHA-256 at initial `a7048cd2`, source-only reads. These
are not the current frozen implementation manifest; see B04-handoff.md for that.

| file | SHA-256 |
|---|---|
| bench/kernels/saxpy/saxpy.blade | 1476356CE91027413473CB3B099FEF28605C0E8D78FB73CB86C4DE1FD2BE3F1A |
| bench/kernels/saxpy/spec.md | 537720A349CA1389A58AE0EAFEE02CD4FAE798BC2B356EA628EE0845BFBA7FB6 |
| bench/harness/run.py | 1915F56D9323B15B6E117C66AD7071DEBF57FEFB9E19B00B9B96CC0201939FDC |
| src/CodeGen.fs | 809958F8C4FF592FB599EF36D38DC0A5170B7292B63EE3C0AB5EA8381CA1C59F |
| src/CodeGenBinding.fs | A7F8A79CF027727119A1739515CD3C2109164C9C263CCAA372405D79F43154E5 |
| src/CodeGenLoopNest.fs | 63E5EAE269B60B5F52EDFCCABB84C16E0A81EBACBB9D938292DD562D8711589E |
| src/EmitLlvm.fs | 7662EF14B12E1E0B06D1489BAF6F8F672A11364EB780C2B7172343253A779B37 |

The design checkpoint authorized no runner launch. Root has since authorized exactly
the narrow diagnostic implementation, not timed admission. The handoff now gives the
eight-arm maximum request for sole Luna, absolute scratch paths and concrete hashes.
No build/emit/test/performance run was performed by the coder. Existing LF-index/CRLF
worktree source assets remain untouched.
