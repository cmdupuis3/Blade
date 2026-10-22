# B04 proposed contract: PM52-immediate-two-stores-v1

**Design accepted; diagnostic implementation authored, unexecuted.** Source identity
and timed-admission gate: [B04-census.md](B04-census.md). Concrete sources and validation
contract: [diagnostic README](../../diagnostics/store-consume/README.md).

For every k in ascending logical order 0 through n-1, stored Float64 x/y use the existing
suite R5 generator, seeds 10000019/20000038, interval [0,1). Use n=2053 for a modest
full-cell diagnostic, n=20011 for a smoke-size audit and n=2000003 only for future
full-size source/emission/store audits. All are odd, not powers of two. Initial
implementation need only execute 2053 through Luna after review; no size sweep claim.

Per cell, preserve this FP dependency graph:

1. v = (1.5*x[k]) + y[k], with multiplication/addition contraction governed by regime.
2. p[k] = v, mandatory store to the first distinct output buffer.
3. w = v*v (or p[k]*p[k] when reading back), one rounded multiplication, no algebraic shortcut.
4. q[k] = w, mandatory store to the second distinct output buffer.

checksum = fold of q with addition, seed 0.0. input_checksum = fold(x,+,0.0) +
fold(y,+,0.0), combining the two completed input sums in that order. No replacement
by a norm identity, input-expression sum, or checksum-only fused loop is admitted.
Forwarding v into the consumer is allowed **only while both mandatory stores survive**.

Default backend association follows the licensed builtin (+) folds and backend policy;
it is not promised bit-identical to an ascending serial chain. A reproducible comparison
sets BLADE_FP_REPRO=1 and BLADE_FP_CONTRACT=off, with matched Fortran flags: ascending
single-chain sums and the same unfused v graph. Licensed or contraction-enabled results
use the existing declared numerical tolerances and record actual association/contract
flags. Never compare repro serial against a reassociated expert reduction as one regime.
The map bodies are pure, total on these finite inputs, with identical per-cell graph.

## Required outputs, observation and endpoints

| stage | obligation |
|---|---|
| before timer | no input/output payload allocation or first touch |
| timed preparation | allocate distinct x,y,p,q; generate/store every x/y cell |
| timed producer and consumer | store every p and q cell, then finish both scalar checksums; p/q cannot alias x/y or each other |
| stop line | x/y unchanged; complete p and q buffers remain live and addressable |
| post-stop observer | visit every p and every q element from those buffers, validate full-cell values/counts; prohibit recomputing from inputs/scalars |
| cleanup | record each explicit release after observation if present, or classify module-root program-lifetime ownership/process-exit reclamation; never invent a pool free; record input cleanup and policy retention separately |

The initial diagnostic formats full p/q arrays after stop in Blade; Fortran dumps p
then q after stop. Its kernel timer is not a performance result. Whole-process time,
high-water memory and faults include formatting and teardown. An eventual timed arm
requires the same-binary opaque observer gate from the census, without relying on a
different dump build or checksums to establish timed-arm materialization.

Immediate means q consumes newly produced p in the same producer/consumer task, with no
intervening delay workload. The expert may fuse p/q stores in one loop. The natural
source uses p assignment then q assignment. These are same-required-output mirrors;
their compilers may optimize them, so report the actual route. No-reuse omits this
consumer and is a separate contract; distant reuse inserts real work and live roots
and is another separate contract. Neither is a faster/slower equivalent arm here.

## Payload and traffic model, never DRAM measurements

At 8 bytes/cell: input payload 16n bytes, mandatory output payload 16n, combined required
payload 32n. At n=2053 this is 32848 input + 32848 output = 65696 bytes, excluding all
metadata, allocator capacity/slack and process/runtime storage. Same output requirement
in every admitted mirror. No claim allocation capacity equals these payload bytes.

Producer reads 16n and writes 8n. Separate q consumer reads 8n p and writes 8n q;
scalar forwarding can remove that 8n logical reread while preserving all 16n output
stores. Separate checksum reads q (8n), input sums read x/y (16n), and post-stop full
observation reads p/q (16n) plus formatting/observer work. Compiler fusion of checksums
may change passes; audit and state it. Do not divide a selected byte model by total
task time to claim DRAM bandwidth. Do not subtract process high-water values.

## Exact actual-store/lifetime audit request for Luna after implementation review

For each admitted backend/tier/FP configuration and exact hashed executable:

- Save source, compiler identity, knobs/flags, plan, IR/raw C++ or LLVM, optimized
  disassembly and audit annotations. Inspect default plus reproducible serial first;
  no automatic mt8/fastmath/BLAS expansion.
- Trace x/y generation stores, distinct p/q allocation roots, all-cell p/q stores,
  per-cell v and v*v arithmetic, and consumer forwarding versus actual p rereads.
  A source assignment, allocation call or vectorization remark alone is insufficient.
- Trace the stop timestamp and every post-stop observer p/q load. Verify loop bounds
  cover n cells exactly, no truncation, scalar substitution or recomputation. In a
  future opaque arm verify separate object, no LTO, opaque call and observer success.
- Show both output roots remain valid through observation, then record release or
  intentional program-lifetime ownership. Do not call an OS-at-exit reclamation an
  explicit pool release. Audit inline and outlined boundaries on the actual combined
  A01 revision; do not transfer source-line predictions to an emitted-binary claim.
- Validate every p/q cell at 2053, both scalar checksums and counts against the independent
  reference. Record skips/refusals/store elimination honestly. A checksum-only arm fails
  this contract even when all scalar pins pass. A dump twin does not rescue it.
- Process resident/commit/fault counters remain whole-child observations when available,
  null otherwise. B03 C++ source-tree API capacity is a separate optional diagnostic;
  LLVM/provider scratch/full-process allocation/retained/live/DRAM values stay unmeasured.

Accept only numerical plus physical-store plus endpoint evidence for this one control.
Quiet interleaved performance measurement, if later authorized, is a separate stage.
