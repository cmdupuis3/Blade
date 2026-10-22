# A03 frozen handoff: LLVM ordinary array-return donor

Status: implemented, self-reviewed statically, uncommitted; awaiting root source review and sole-Luna validation. Base `c594faec4ad726f9a0e33ebb6c412e669a08a7d7` includes root's combined integration4480ef45, accepted A02 controls and C02-L1. The approved source/design is the unchanged A03-census.md (SHA2561e754ee59ac6a1ce9a77a31f6c24c6fc3e7120dcffcb42f2d508b565b9aa1bcb); root review is integration A03-review.md.

No build, lower, emit, compile, run, test, sanitizer, benchmark or output-pin generation was performed by this lane. No commit/push/merge. Static git diff --check passes. Tracked source working endings remain CRLF with LF in the index. New corpus is authored LF, without reformatting earlier assets. No new F# file/fsproj entry.

## Implemented contract

Only ordinary outlined array returns with an existing numeric PoolReuseTable.tryReturnDonor(cl.Id) entry are considered. The outer IRLet continuation uses the exact existing bindLetIn once per value, preserving original evaluation order. At the computed map tail, a read-only preflight requires the planned donor's direct fresh body-let identity, already stored APool, exact current ordinary function scope (one active scope, no InlineId, donor not in KnownIds), and one matching allocation/tracking-slot record. There is no new lifetime side table and no new plan or in-place input update.

The input must already be a completed APool of the same physical shape, distinct from the donor, with a separate exact owned allocation entry or the unchanged original formal-array pointer. A different SSA name, row GEP, loaded pointer or equal size alone is insufficient. A deferred input declines without being forced. The tail kernel is a resolved one-scalar Float64 callable whose body is only the original Float64 literals/variables and +,-,*,/ expression; captures must be already-bound local scalar snapshots or scalar parameters. Array captures, keyed reads, calls, source effects and unknown producers decline.

Scope/capability bounds are deliberately narrow: capture-free nonrecursive serial ordinary function; one static plain stored rank-one positive literal extent; bare Float64 element; spatial axis with no dependencies; no units, virtual array, packed/symmetric/ragged/strided/dynamic shape, Reynolds or kernel/output-rank/peel strategy. Functions containing any A01 event or any let-donor plan decline. The original tuple/capturing/kernel routes are untouched.

`plainShape` intentionally does NOT strip unit wrappers: outer IRTUnitAnnotated does not match ArrayElem, element must be exactly IRTScalar ETFloat64, and no unit/layout conversion is performed. Axis Id/Tag and array identity are logical metadata, not allocation ownership evidence. As in the approved upstream equal-physical-shape plan, storage compatibility uses literal extent; existing logical axis metadata stays on the original producer/result and is never replaced with donor metadata. Temporal/dependent axes now explicitly decline. No broader unit/index conversion admission is claimed.

On admission the combined existing applyToArr runs once. Its normal dense AVirt is filled into the existing donor backing by fillDensePool, factored verbatim from materialize's dense branch. No extra allocation/tracking record, pointer rebinding or new cleanup rule is added. Result carries the original producer metadata with Src=APool donor. Existing popPoolScope(Some donor) spares that one root, frees the independent input temporary once and returns the donor pointer. Unsupported preflight takes the old fresh-result tail once; an actual prepared shape contradicting preflight explicitly refuses LLVM rather than rendering twice.

`pool-reuse-llvm` version1 reports Applied/Declined independently of the shared `pool-reuse` planner record. Evidence includes numeric function/donor IDs and actual backing/slot. A raw A03 marker locates the fill boundary, but tests additionally trace actual storage/dataflow. Gate-off remains the existing absence of a recorded donor; no environment caching or new default policy was introduced.

## Owned delta and static checks

- src/EmitLlvm.fs: shared unchanged dense fill, read-only return preflight and the ordinary return consumer.
- tests/LlvmTests.fs: two positive on/off facts, owned backing/slot/free/return checks, exact backend decisions and eight refusal facts.
- tests/corpus/memfree/032_return_reuse_scalar_snapshot.blade: two required live returned arrays, a tail capturing the mean scalar computed from the old donor, and required unmodified caller inputs.
- A03-census.md: preserved approved design; no edits.

Self-review compared source blocks with HEAD: applyToArr including C02-L1, materializeExpr and popPoolScope are byte-identical. The dense fill's emitNest/readCell/coerce/storeCell sequence is moved without arithmetic/traversal/flag changes. Optimizer, IR, runtime and C++ source are unchanged. A02 controls/census/request/manifest are unchanged.

## Authored evidence, not executed results

Positive facts use frozen memfree030 plus new032 under reuse1/0, CSE0, repro1, contractoff, BLASoff and serial emission. They require one backend admission, exact ordinary call count (one/two), two versus three scoped allocations, one unique tracking store per backing, no later slot overwrite, returned root ownership/no free, and exactly one free of every other root. The admitted return's GEP stores must target the earlier donor, read the independent input backing after its prior fill, and return the donor pointer. The total free count rejects unrelated/additional frees.

The test compares arithmetic instruction/flag SEQUENCES only. It does not compare operands and does not claim automated SSA graph identity; root raw-IR/emission review must check operand graph identity. Source uses the same original kernel/body and shared fill with no expression rewrite.

Refusals cover forged caller-owned target, same input/destination root, a general let-donor plan, latent producer input, hidden array capture, a real released A01 event root after its completed scalar snapshot, mismatched extent, and a row-view pointer without owned root origin. These mutations validate IR first; malformed/stale table entries cannot bypass backend ownership. The latent case also requires two allocations (old donor plus fresh result), a fresh returned pointer and one temporary free: no speculative third input pool. Refusal sources inherit baseline comment pins but are emission-only structural probes; do not run those modified strings as value-pin fixtures.

New032 independent analytic pins: x=0..6 remains unchanged; first p=1,3,..13 has mean7, half-centered q plus mean returns4..10, sum49. y=2,4,..14 remains unchanged; second p=5,9,..29 has mean17, result11,13,..23, sum119. Both results remain required after the second call. No generated output was used for pins.

Expected030/032 allocation change, pending runner: p+q+fresh return becomes p+q; the final fill writes p, q is freed once, p is returned and survives caller reads. Off remains three pools/two temporary frees. Register numbers are not pinned. Main's required input/output ownership is unchanged.

## Staged sole-runner request

Root finalizes the queue. Validate the frozen content manifest and base before starting. Use the isolated a-lifetimes root, private TEMP/TMP and serialized processes, with complete logs/source/binary/env identity under bench/.build/A03-runner. No full/default suite, golden regeneration, oracle, timing or performance measurement.

1. One `dotnet build Blade.fsproj -c Release`; then `blade test llvm facts` and `blade test optimize`. Stop unexpected failures and return exact diagnostics to root before broadening. The ten new LLVM facts are the two return controls plus eight named refusal cases. Report full pass/fail/skip totals, including existing C02-L1 assertions in optimize. No coder execution is claimed.
2. After primary gates pass: `blade test memfree`, `blade test interp memfree`, `blade test llvm memfree`, and `blade test llvm goldens` (no regeneration). Record actual native LLVM versus fallback/skip. A02's broad categories and ASan were not previously run; these gates consolidate that pending follow-up and must not be reported as prior A02 passes.
3. Save raw IR/shared donor/backend admission/.ll/.cpp for memfree030 and032 with BLADE_POOL_REUSE=1/0, BLADE_FP_REPRO=1, BLADE_FP_CONTRACT=off, BLADE_BLAS=off, BLADE_CSE=0, BLADE_OMP_THREADS=1, OMP_NUM_THREADS=1, all outputs required. Count precise function backing sites2/3, trace root/slot/destination/return and the other root's single free, and inspect operands/flags/control-flow completion before overwrite. Execute direct C++/LLVM plus interpreter all-output pins for030/032. No need to repeat A02's whole24-run matrix before reviewing the changed route.
4. Preserve A01/A02 refusal controls: raw031 must retain begin/release, one scratch free before caller maps and exact whole-function reuse refusal. Existing LLVM facts cover A01 lifecycle. Check A02/027/028 remain unconsumed tuple-chain plans and029's documented physical copy/retention behavior is not silently attributed to this card. Root may select bounded additional affected controls rather than rerun unrelated matrices.
5. Only after primary ownership/value gates pass: recheck combined C02-L1 native/fact controls, especially sql-reduce036_private_partial_producer_failure_first (producer line8 before fold),037_private_partial_producer_before_seed (producer before seed),038_private_shared_partial_source, stored/packed row and primary two/three-pool cases. Preserve original fusion on/off failure sites/frames and one-time input forcing. applyToArr is unchanged; the shared materialize fill must not perturb this boundary.
6. Then bounded ASan/memcheck cache-off reuse1/0 mirrors of030/032 (and root-selected031 refusal) in both supported actual backends, with all required values read. Record UAF/double-free/census coverage and missing-tool skips. Cache-on value mirrors are56-byte payloads below16KiB and do not exercise eligible block retention. Outstanding bytes are not a zero criterion for required main-owned arrays; logical pool-peak estimates/call counts are not process capacity.

A02's accepted observation is an existing C++ route and absent LLVM consumer; A03 implements only its ordinary return subset. General LLVM let-chain reuse, tuple-call cleanup and C++ alias-copy escape retention remain separate work. No measured memory, capacity, RSS, bandwidth or timing gain is claimed. Stop after this kernel for root review and sole-runner evidence.

Frozen content identity: A03-content-sha256.json covers both source/test files, new032 and approved design. This handoff is excluded to avoid self-hashing. The manifest SHA256 is reported in the freeze message.
