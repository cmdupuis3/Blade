# C06 frozen implementation handoff

Base: `566d3ec1029eee444906385435b3b65662353aaf`. Source-only checkpoint; no coder build, lowering, emission, test or execution.

The existing early terminal entry now recognizes precisely the observed private rank2 map / unit-wrapped full fold. It replaces that region with existing `IRReduceCompute`, preserving the original map/fold references, literal seed, ordered source axes, scalar host identity and one ascending accumulator graph. It mints no IDs/callables; changed tracking retains this no-mint rewrite and removes only the eliminated producer from `MutableArrayLets`. Existing C02/C04 arms and default optimizeModule behavior remain intact. No IR/backend/runtime changes.

Admission requires per-call FUSION+repro, complete foreign census, dominating stored plain Float64 source, exact Apply/MethodFor metadata and seven unique local definitions, structural closed/transitive totality plus serial metadata, and complete residual type/capture/side-payload/write/lifetime/owner census. Unknown effect summaries alone confer no licence. Missing evidence, demanded/named intermediates, malformed loops, hidden observers, effects/unknown calls, source writes, strategies or owner overlap retain fallback. The global write/event refusal is deliberately conservative.

Static API review confirmed all owner-table accessors against this checkout, including donor and return-donor endpoints and MemberHoistPlan's HostId/SetId/Kernels:Set<IRId>. BinderShape contributes exactly seven observed local definitions: p14, a15, r16, c17, u23, u24, u25. Equal extents do not substitute for sameIndex axis provenance. Production preserves existing C02/C04 code outside its final dispatch/changed tracking.

Focused OptimizeTests add actual-route/ownership/provenance checks, 23 synthetic refusals, C++/real LLVM allocation checks, and seven independent interpreter ON/OFF cases. LLVM print checks bind the actual `total` string global to blade_print_f64, rather than expecting the runtime separator in the label. C++/LLVM primary expects retained M9 only ON versus M9+private9 OFF; demanded P remains materialized. Evaluation reads from the retained input are checked before print. These are requested emitted-route assertions, not new native evidence.

Corpus054–060 are byte-identical copies of accepted census01/02/03/05/06/07/08 (SHA comparisons performed). In particular058 already has the load-bearing `(aborts)` suffix in BOTH original and copy; no header/body difference was needed. Independent pins retain7044, nested1206, flat1 versus nested0, demanded rows84/213/468, literal empty seeds7/-3/25/-0, and late producer failure priority. Original census/proposal/adapter are unchanged.

Modeled primary saving: one9-cell Float64 payload (72 bytes), with required printed M retained. This is not a measured peak-memory/timing claim; original main has no explicit private-pool cleanup.

## Sole-Luna request, staged

After root source review/checkpoint, from the c-terminals worktree root:

1. `dotnet build Blade.fsproj -c Release` (save complete stdout/stderr, exact commit and DLL SHA).
2. Only if build passes: `dotnet bin/Release/net10.0/Blade.dll test optimize` (save complete results; stop on unexpected failure). New tests themselves exercise C++ and true LLVM emission plus interpreter ON/OFF. Existing C02/C04 tests stay enabled.

Do not start later gates without root dispatch. Suggested bounded next stage: corpus054–060 native ON/OFF (14 arms), BLADE_FP_REPRO=1, BLADE_FP_CONTRACT=off, BLADE_BLAS=off, BLADE_OMP_THREADS=1, default printing; verify independent pins/status/failure text, not only lane equality. Retain actual primary emitted C++/LLVM ON/OFF artifacts and inspect input-to-fold dataflow and allocation sizes; demanded/nested/rowwise controls remain distinct. Root may then request sql-reduce/interp/opt-diff categories. No full suite or benchmark requested.

Content identities: C06-implementation-SHA256SUMS.txt covers the two modified source/test files plus seven corpus files. Handoff/manifest hashes are reported separately. Static `git diff --check` passes; tracked F# working endings remain CRLF/index LF. `.r4tmp` and all prior evidence untouched. No Git mutation/commit by coder. Frozen pending root review and sole-runner validation.

## Attempt1 syntax repair

Root checkpoint `cf2602e41b1f58f0c91e2da406bf7e67077d93ad` failed the first Release build before tests: OptimizeTests2674 FS0058/FS0010, offside of inline list-comprehension context2673:31. Preserved log `bench/.build/C06-implementation-runner/attempt1/build.stdout.log`, SHA256 `71792FA6DB4751963A57732E82F8759D26E377E5F2EA16BC303A17A6928D76CD` (3 diagnostics, one layout defect). Replace only the inline cons/comprehension with a separately bound List.map refusal list and explicit branch bodies; same23 cases, result reporting/order/booleans, and positive-first result remain. Production and all seven corpus hashes unchanged. No tests ran; no coder execution or Git mutation. Refrozen for root checkpoint and the same build-then-optimize request.

## Attempt2 type-inference repair

Root checkpoint `eff9da9d95453af3a9497c1028b7f3a44902c7e6` reached production compilation but failed before tests: Optimize8476–8477 FS0001 inferred the ambiguous `.Extent` record as SfMember (int64 extent), rather than IRIndexType (IRExpr extent). Preserved attempt2/build.stdout.log SHA256 `5284014B907E90D097E9336D4CDA4BABEA7B2FD9B17F5921BBF62FCF60E3FE84`. Add explicit `(hi: IRExpr) (ix: IRIndexType)` lambda annotations at that single List.forall2; algorithm, comparison and admission unchanged. Tests/corpus unchanged. No coder build/execution or Git mutation; refrozen for root checkpoint and same staged retry.
