# A02: PM01 barrier-chain source census and controls

Status: source/control checkpoint only; frozen for root review and Luna diagnostics. No compiler behavior changed. Base is A01 commit `12b43b4f`; root reports all 17 A01 targeted categories passed and integration `4189da71` is pending combined checks. The opening prerequisite warning in the earlier A02 brief predates that acceptance.

No build, lowering, emission, compilation, execution, test, sanitizer, benchmark or generated output pin was performed by this lane. Graft searched the one-file index and returned no F# hits; the following is targeted direct source inspection, permitted for the unindexed compiler. Full/default suite launches remain zero.

## Contract and owned files

Five new `tests/corpus/memfree` assets (027-031), this census, `A02-runner-request.md` and `A02-content-sha256.json`. No F# file, fsproj entry, pass, ownership algorithm, in-place update or last-use release changes.

The principal controls are plain dense Float64 function-local chains at odd extents 7 and 11, with three and five materialized map stages respectively. A sum over each stage feeds the next map, so it is a real whole-array dependency. The final array and every scalar observation are returned and independently pinned. Source `compute` syntax is only a candidate workload: the runner must prove the intended maps survived fusion/terminal folding before counting this as PM01 reuse evidence. All values are small exact integers representable in Float64; no reassociation or FMA change is licensed by this card.

| File / callable | Required output and control | Conditional source-level reuse expectation |
| --- | --- | --- |
| 027 / `barrier_three` | final seven cells and s1/s2/s3 | If all three map pools survive, r can take p's root; q reads p and r reads q, so neither adjacent pair can overlap. Two actual backings are expected rather than three. |
| 028 / `barrier_five` | final eleven cells and s1-s5 | If all five map pools survive, p/r/v share one root and q/u share another. Two actual backings are expected rather than five. |
| 029 / `barrier_alias` | held p, final r, s1/s2/s3 and a later sum of held | The held array aliases p and is read after r. p must not donate; q still feeds r. If all map pools survive, all three backings stay distinct. |
| 030 / `returned_pool` | ordinary array-return result and caller sum | Odd-extent mirror of existing demean return-donor proof: final q*0.5 takes dead p's root while reading distinct q; returned root escapes and q is freed. This isolates the ordinary array-return ABI from tuple return handling. |
| 031 / `region_chain` | final chain array, kernel k, input sum t and s1/s2/s3 | Kernel's stored producer plus caller input folds should retain a scalar inline with A01 begin/release. The entire event-bearing driver must decline reuse. The freed callee root cannot donate to p/q/r, and caller stages remain distinct. A fallback without events is not a passing refusal witness. |

The same exact source is the BLADE_POOL_REUSE=0 mirror; do not construct a different algorithm for the off route. For 031 also compare EPILOGUE_LEGS=0, preserving all outputs; a function without events may then use the established caller-chain reuse. That difference does not weaken the on-route whole-function refusal.

## Existing admission and identity proof

`src/Lowering.fs:3133-3174` optimizes first, lifts inline forms, forces remaining function-local computations, runs CSE, then calls `Optimize.planPoolReuse`. Reuse is a plan over that final shape, not an IR rewrite. `Types.PoolReuseTable` (`src/Types.fs:561`) is AsyncLocal and reset once per lowerTypedProgram, with reuser-ID -> donor-root-ID and function-ID -> return-donor-ID maps. Dump it before another lower resets it.

`src/Optimize.fs:557` accepts equal bare element type and literal positive extents over plain dense rank-one index slots (multiple ordinary slots may form higher-rank dense arrays). `planPoolReuse` (`:571-748`) considers forced apply lets and the eligible return. It resolves deferred reads at consumers and callable captures, rejects retained handles/aliases/views/call arguments/assignment and chooses a root only when none of its existing members is read at or after the reuser. A fresh map that still reads a donor therefore cannot overwrite it. The root/member group is keyed by actual IR IDs; identical printed names or equal extents alone prove nothing about aliasing.

A01's first guard in `planPoolReuse` scans each function for IRBeginPoolScope or IRReleasePools. The exact expected decline is `inline lifetime region: scratch reuse requires a region ownership proof`; no reuse entries should be recorded for any candidate in that event-bearing function. The resource events remain distinct from source writes. The negative is preserved deliberately, even if it prevents reuse among caller-only pools after the release.

Existing highest-value controls are `tests/OptimizeTests.fs:513-571` (return takes a dead pool; two chain reusers share a root), `:3037` (knob-off emits no alias), and `tests/corpus/memfree/022_scratch_reuse_dead_pools.blade` (values, repeated calls, retained view negative). Existing tests inspect alias declarations/comments and numerical output; they do not by themselves establish physical process capacity or all backend ownership. A02 adds whole-array/scalar demand controls and requests allocation-specific diagnostics rather than treating those old checks as the complete proof.

## C++ consumption and function-return ownership

`src/CodeGen.fs:762-789` consults tryDonor for body lets and calls `rewritePoolAlias`; only a matched allocation-declaration rewrite suppresses the reuser's scope free. Return-position maps use currentReturnDonor (`:817`), suppress the escaping donor only after a matched rewrite, and spare the returned pool. `src/CodeGenLoopNest.fs:4821` rewrites exactly one static dense allocation into `{ donor.data, own_extents }`; a mismatch leaves the independent allocation and its cleanup intact. Therefore a planner Applied outcome without matching emitted alias/dataflow is not route success.

`computeScopeEscapes` (`src/CodeGenLoopNest.fs:4414`, reuse propagation near `:4527`) propagates an escaping reuser to its donor root, but stops following reads through scalar snapshots and fresh independent pools. The allocation frame renderer (`:4915-4955`) skips escaping owners or matched suppressed names and emits one free for each remaining backing. Ordinary functions use genFuncBody's SFunc allocation frame (`src/CodeGen.fs:1099`). For a tuple returning the final array plus scalars, the actual return and root escape must be checked in emission: scalar names must not pin input arrays, and a final root shared by older dead members must not be freed in the callee.

Conditional expected C++ free locations: 027/028 each leave the root carrying the final array alive and free the other root once before return; off leaves the last stage alive and frees the other 2/4 backings. 029 leaves held p and final r alive and frees q once, with no p/r alias. 030 leaves the returned donor alive and frees q once; off leaves a fresh return alive and frees p and q. Caller-required module outputs may remain owned through program exit; absence of a main exit free is not automatically a callee leak. Count input literal/call pools and stack/extents metadata separately from the function-local map roots.

## LLVM source finding: no reuse-plan consumer found

An exhaustive search for PoolReuseTable consumers finds CodeGen.fs, CodeGenLoopNest.fs, Lowering.fs, Optimize.fs and Types.fs; none in EmitLlvm.fs or its helper files. `EmitLlvm.materialize` (`src/EmitLlvm.fs:5559`) returns an existing APool as-is but allocates each virtual fresh map through allocPool (`:2063`). allocPool emits blade_alloc_cells and tracks it only if a scope is active. This source evidence predicts the shared planner can record Applied while LLVM still emits independent pools; emitted register/backing evidence is required to confirm it. Correct outputs alone would not prove parity with the C++ reuse plan.

Ordinary scalar/array calls use emitOutlinedFunction's existing pool scope; scalar returns free tracked owned pools, array returns spare their actual APool result and free other known-owned pools (`src/EmitLlvm.fs` near 7750-7807). 030 isolates that route. Genuine tuple calls are deliberately expanded by emitTupleParts (`:7556-7619`) because this lane has no aggregate-return ABI; that path does not itself open a function allocation scope. Thus 027-029/031 also require an explicit accounting of which generated context owns each pool and whether it remains at main lifetime. A01 events still open/pop their own scope and free their exact named pools. Do not infer a general tuple-frame cleanup fix from A01's scalar-return inline repair.

No missing optimization is implemented or numerically/physically claimed. If diagnostics confirm an Applied map with distinct LLVM allocations, the precise follow-on proposal is an LLVM reuse-plan consumer guarded by the same root/member liveness and type/layout facts, with result-escape/slot tracking and alias restrictions reviewed before coding. Genuine tuple-call cleanup is a separate ownership concern if the census demonstrates unneeded backings lasting to main exit; it must not be hidden by a reuse implementation. Both require root approval and their own bounded card.

## Pins, memory claims and retained-cache bounds

Analytic pins: 027 p=1..7 sums 28,224,1792 and final=253..259. 028 p=1..11 sums 66,792,9504,114048,1368576 and final=124411..124421. 029 retains 1..7 (sum28) alongside 027's final/scalars. 030 returns -3..3 (sum0). 031 kernel=(1,3,...,13) squares ->455; x/y input sums ->49; p=504..510, chain sums 3549,28392,227136 and final32445..32451. These were derived without executing Blade or generating output pins.

The plan's peak-live-pool-bytes numbers are logical intervals, not allocated capacity: codegen normally releases nonescaping roots only at function return. A dead pool with no reuse can remain physically allocated after its logical last read. We make no RSS/peak/capacity/bandwidth/timing improvement claim from that estimate or from static source counts.

These bounded controls use 56/88-byte Float64 map payloads, below the retained-cache 16-KiB admission threshold in both C++ (`src/cpp/nested_array_utilities.hpp:35-153`) and LLVM shim (`src/cpp/blade_llvm_shim.c:193-430`). Cache-on numerical twins are valid but do NOT exercise eligible block retention. Memcheck/ASan disables cache and turns releases into real allocator frees. Cache fresh/hit/retained/evicted counters, if collected, are eligible-block counters rather than counts of all map allocation calls. No large-block retention or process-capacity coverage is claimed; scaled retention controls and instrumentation are separate work. Do not equate a released pool with OS memory returned under ordinary cache-on execution.

## Disposition

This is a source-only existing-route/control census with a specific LLVM consumption hypothesis and a tuple-return ownership question, both pending emitted diagnostics. The exact staged request is in A02-runner-request.md: the initial queued job collects IR/emission only and returns to root before any broader category or ASan follow-up. Changed workload shape is an immediate stop boundary. No route is marked complete and no compiler implementation is authorized by this checkpoint. Stop for root review and sole-runner evidence.
