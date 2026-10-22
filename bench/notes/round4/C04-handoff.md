# C04 handoff: one private transpose-to-partial-fold arm

Frozen implementation for root review on C branch, base
`41ae781809f9fe80c097b0ba39ed263d293d123b` (relevant combined source matches
integration 5e208a77). Coder performed source inspection/editing and static diff
checks only. No build, lowering, emit, interpreter, test, generated execution,
oracle or benchmark was performed. Luna owns all validation; full suite stays 0.

## Scope and implementation

The preimplementation census/review proved the exact missing early shape and
existing target capability: anonymous IRLet transpose -> immediate row apply;
baseline LLVM input/transpose/output payload layouts 6/6/3 doubles; existing
strided idiom 6/3. Those are earlier emission observations, not new implementation
results. See C04-review.md/C04-census-results.md and integration's raw
`bench/.build/C04-D08-runner/c04` evidence.

One new operand-kind arm lives in the existing partial-row rewrite in Optimize.
It matches ONLY `IRLet(pid, IRTranspose(IRVar M,0,1), IRCompute(rowApply))`.
No generated-prefix proof, arbitrary scope walk, named transpose, scalar/full
fold, general view, backend path or new user knob. C02's map branch is unchanged.
C02-L1's eager ARRAY/row input boundary remains unchanged; the manufactured
virtual ranges have scalar input rank 0.

The new arm verifies stored static plain Float64 M, scalar literal seed,
closed capture-free total fold/callee graph, immediate sole row consumption,
complete consumer/MethodFor scalar/rank/axis/strategy metadata, and original
R/C index provenance. Original inferred row parameter extents are NOT copied
into generated coordinates. Source R supplies the inner range and source C
the outer range. Fresh scalar gather/column callables and plain arrows carry
origins and certified M/c captures; user-fold captures stay outside admission.
The generated read is M(r,c), with each coordinate drawn from that exact source
index space. Each c seeds once and folds r ascending. R=0 returns each exact
seed; C=0 executes no column or seed. No new reassociation/parallel licence.

Important reviewed correction: the observed host C has numeric ID 5 while
consumer.OutputType C has 14 and source C has 3. Validation uses sameAxes/
IRTotality.sameIndex, scalar type and dimension roles. The replacement outer
OutputType is **host.Type exactly**, retaining its nominal output and binding
ID/name. A regression asserts that the actual differing numeric IDs admit.

The residual census retains every host child/type/payload except the two
certified private input occurrences (actual Arrays and MethodFor.Arrays).
Their types remain visible through typed zero placeholders in the audit.
Exactly two uses, exact metadata and immediate scope are required. Every other
binding, reference-equal host tree, function/capture/signature, declared/embedded
type, side execution payload, provider/name reference and foreign demand stays
audited. Constant execution side payloads also refuse. Full payload scheduling
checks reject writes/lifetime events and unsafe stored initializer/alias chains;
providers/deferred roots and unsupported geometry refuse. No work moves across
an intervening call, mutation, abort or alias statement.

## Complete-census interface

The existing `optimizeModule` signature remains unchanged and delegates with
NO foreign-census certificate. The public C02 helper also keeps its signature.
Without explicit evidence, the new transpose arm declines. C02 behavior stays
as before for both entries. An internal `optimizeModuleWithForeignCensus` is
used only by the whole-program Lowering driver. Its lazy moduleReferencedIds
census includes binding values, types, callable/capture and side roots; the
old CompoundInits.IsEmpty shortcut is removed, because private transpose has
no compound entry. Single-module Set.empty is explicit complete evidence;
multi-module references are unioned once from the existing lazy collector.
No changes to emitter/interpreter/provider/planner or lifetime APIs.

## Focused assertions and independent pins

OptimizeTests adds actual rewritten route/metadata and default-entry refusal,
CPP rank-2 allocation 1 ON vs 2 OFF, actual LLVM layouts [6x8,3x8] ON vs
[6x8,6x8,3x8] OFF/demanded, required M/rows/T print, seed/serial arithmetic,
and interpreter ON/OFF with independent pins. It retains all C02/C02-L1 and
C03 checks in the optimize gate. Counts are included in assertion failures.

The LLVM input GEP/mul-by-3/loop regex is a **smoke check**, not a complete
dataflow proof. Root/Luna must inspect the saved actual index operands and
trace r*3+c to M loads and row-result stores. The C++ callable subscript check
and exact constructed-IR test are complementary. None of these emitted-source
checks is native execution evidence or a measured memory result.

Interpreter pins cover [714,725,736], R0 [-7.25,-7.25,-7.25], C0 [], R0
negative-zero bits, cancellation [0,1], stored alias and total block helper.
Failure controls preserve deferred source completion before the first failing
fold and refuse a failing nonliteral seed. Refusals cover named/full-scalar,
alias/capture/deferred/strategy/seed cases, wrong nominal C, hidden declared/
operand/shared/kernel/MethodFor types, foreign type-only demand, constant side
payload, unresolved fold, reference-equal other host, lifetime event and intervening write/alias.

Corpus sql-reduce/040-047 has independent output/failure pins. 044 requires a
named transpose snapshot, mutates M(0,0) to 99 afterward, and requires original
T and rows. Its T pin is nested to check structure where the printer exposes
it. 043's source `where repro` addition also pins the default materialized arm.
045 must fail in `late_transpose_source`, never `early_transpose_fold`.
046 must fail in `transpose_seed_failure`. Empty seeds retain the sign of zero.

Modeled storage change is removal of the private 8RC transpose payload plus
its own layout scaffolding. Required stored M and 8C output remain. No full
mapped temporary is introduced. Strided reads can reduce locality; no speed,
bandwidth, peak-live-byte or runtime memory claim is made before measurement.

## Bounded serialized Luna request

Root reviews/freezes first. Use this private checkout with source/binary hashes,
private TEMP/TMP and complete logs under `bench/.build/C04-implementation-runner`.
Do not mutate sources/regenerate pins. Stop and return unexpected failures.

1. `dotnet build Blade.fsproj -c Release`, then
   `bin/Release/net10.0/Blade.exe test optimize`. Stop on build/optimize failure.
   Record all pass/fail/skip totals and every transpose_rows result; this is
   where the internal interface/foreign certificate and synthetic census
   refusals are exercised. No full/default suite.
2. On root acceptance, run corpus sql-reduce/040-047 individually using
   `Blade.exe run <absolute-source> --verbose`, actual C++ and actual LLVM
   separately, each with BLADE_FUSION=on and 0. Pin BLADE_FP_REPRO=1,
   BLADE_FP_CONTRACT=off, BLADE_BLAS=off, BLADE_OMP_THREADS=1 and PRINT unset;
   set BLADE_LLVM=off/on explicitly. Require genuine LLVM provenance: a C++
   fallback is not a supported LLVM arm. Check independent pins as well as
   normalized output/error/status agreement. Both evaluators use optimized IR.
3. Retain ON/OFF C++ and LLVM `emit` + `plan --json` for 040, 044, 045 and 046.
   040 must remove the transpose pool only ON; 044 keeps a distinct T snapshot;
   045/046 decline. Trace actual R inner/C outer loops, exact r*3+c M address,
   original seed and ascending fold graph, and required source/output prints.
   The regex smoke assertion is insufficient for this review. In failures,
   inspect source site/frames and exclude the competing early fold failure.
4. Run sql-reduce's native category plus `test interp sql-reduce` and
   `test opt-diff sql-reduce` only after the focused arms pass and root accepts
   them. Generic gates may clear repro/print environment; custom optimize
   interpreter ON/OFF and step 2 are the positive-route differential pins.
   Root may select existing C02-L1 036-039 controls if targeted results reveal
   a boundary concern; no backend source changed in this card.

No full suite, broad unrelated category, oracle, sanitizer, timing or memory
benchmark is requested. Acceptance remains pending the C04-I1 repair below.

## Build failures and layout-only repairs

Root committed the initial source checkpoint as `e1cca133`. Luna's first Release
build stopped with five F# layout errors at OptimizeTests.fs:2039-2043 in the
unknownFold witness; no optimize gate or program execution ran. Evidence remains
in `bench/.build/C04-implementation-runner/build.stdout.log` (SHA256
`25A8A52A885DFF85607D5A421E36D414F984EE894B5B3B3251FCD7E933FB50F8`),
with root's report in integration's C04-tests.md. The repair only binds the
Functions pipeline separately before constructing the module record, preserving
the witness logic. Production and all eight corpus hashes remain unchanged.
Root committed that layout repair as `0f4b48cc`. Luna's second Release build
stopped with three F# errors at OptimizeTests.fs:2020 in the foreignModule
record: the misaligned ProviderReads field was parsed as part of RandomInits.
No optimize gate or program execution ran. Evidence remains in
`bench/.build/C04-implementation-runner/attempt2/build.stdout.log` (SHA256
`B5DE208EC56F77B5C1293F5C3AB695D4D7CA1E1AF5C756D1A55FEB51336FE8FC`).
The second repair only lays out every foreignModule field on its own line at
the same indentation. All newly added C04 test records were inspected for the
same multiline field-layout problem; no further occurrence was found.
Production and all eight corpus hashes remained unchanged through these layout
repairs. Root committed the second repair as `89b49014`.

## C04-I1: preserve an empty rectangular literal's runtime rank

Luna's attempt3 Release build passed. Optimize then reported **691 pass, 2 fail,
0 skip**: only the R0 and negative_zero interpreter ON/OFF exact comparison
failed; each independent ON pin/admission passed. The optimize log is
`bench/.build/C04-implementation-runner/attempt3/optimize.stdout.log` (SHA256
`38BC185FF72CDCF7AB5517A5B241F0721FE3803D7134267BEB79B21036B5AD37`).

The diagnostic-only FSI bridge under C04-empty-diagnostic records its first
collector failure and the SrcLoc.Span bridge repair. Luna's retry completed all
four arms, exit0, against the existing attempt3 DLL (SHA256
`580D8378F2090290D310B3618168F1B77F546B6BBCC7C4EB2811B2031CE05CF8`).
Raw results and full typed IR remain in
`bench/.build/C04-empty-diagnostic-runner/retry1-output`:

| Case | ON | OFF |
| --- | --- | --- |
| R0 | status0, M=[], rows=[-7.25,-7.25,-7.25], empty stderr | status125, empty stdout, interp-unsupported: transpose: dimension index out of range |
| negative_zero | status0, M=[], rows=[-0.0,-0.0,-0.0], empty stderr | status125, empty stdout, same unsupported message |

The ON stdout also includes the normal test-name `completed in 0s` line. The
retry transcript hash is `873A77B3FB7D9FC149A4A1A335116896FB226C9F633EA8A959CC3513771F79A4`.
This confirms a preexisting interpreter shape bug, rather than a wrong seed:
arrayLitFromValues collapsed an empty literal to Extents=[0] despite its typed
R0,C3 axes. The OFF transpose therefore rejected axis1. ON has no transpose or
M cell reads for R0. C0 has exemplar empty rows, so its original [2;0] shape
already survived and passed. Original source seed pins and exact ON/OFF
output/error/status comparison are unchanged.

The separate C04-I1 production repair changes only Interp/ArrayOps.fs. For an
empty literal with at least two plain, rank1, SymNone, dependency-free axes,
all statically known nonnegative managed extents, and first extent0, it calls
the existing allocDense with every declared extent and exact index records.
Leading zero creates a zero-cell backing store and empty SNested structure;
there is no exemplar row or cell read. The managed-static extent guard bounds
this repair's scope; it is not a new language restriction. Rank1/nonempty,
ragged, compact, dependent, unresolved and other excluded metadata retain the
existing branches. Optimize.fs, Lowering.fs and all eight corpus files remain
unchanged.

Thirteen new direct optimize-gate witnesses exercise the literal constructor
without lowering: rank2[0;3] plus transpose[3;0], rank3[0;3;2] plus
transpose[2;3;0], exact nominal metadata/zero cell stores, rank1 empty/nonempty,
existing rectangular nonempty/zero-inner, seven excluded-metadata controls,
and existing nonempty ragged CSR/compact routes. The four observed diagnostic
arms are separate from these thirteen authored unit witnesses. No new native
evidence or repaired-source execution is claimed.

Luna next request: after root reviews/commits this repair together with the
diagnostic bundle, rebuild then run optimize, stopping on unexpected failures.
Require all thirteen `transpose_rows_I1_` witnesses and the unchanged original
R0/negative_zero comparisons. Then resume the previously bounded native
C++/actual-LLVM ON/OFF corpus matrix and dataflow review above; those native
twins must establish the generated backends' shape/seed behavior independently.
No full suite or broad interpreter category is added. The coder performed no
build, lowering, emission, interpreter or test execution for this repair.

## Frozen working-file SHA256

| File | SHA256 |
| --- | --- |
| src/Optimize.fs | `6263600DF6B8A1EC82225E5BFB3B456F8563F57566A0A597C414326081C92F01` |
| src/Lowering.fs | `D04639FFDB34635D3D4E06295E49901547042795BA7CEE3AB7E021B739D5718E` |
| src/Interp/ArrayOps.fs | `83CD87F4904A3714B24B80C3D19F859C2EA4014A6E3D803E3F74377D69B76FBE` |
| tests/OptimizeTests.fs | `BC5A64D421394778385E18E8583221A92453C76E86D6E49066EB9CB6CBC2C716` |
| C04-empty-diagnostic/empty-axis-probe.fsx | `24EBB9A29E9E5EB5399A9506805B6A17D6A53CBADACD1FA2186D6417C95B01F2` |
| C04-empty-diagnostic/README.md | `466DA4D591C7786E1C836A0778065E1CFA9E184C80D436FB67039130EBB814AA` |
| 040_private_transpose_ordered_columns.blade | `995E1A0BAC2F6D3312B266CC5D83A5506B1A09B69C9CCE3F7E7A96CA7145C26D` |
| 041_private_transpose_zero_rows.blade | `33668D3C9328FF6940AB47823B029FE0BAAADE290896A87B22B7396A43BDD32E` |
| 042_private_transpose_zero_columns.blade | `81AF144C761058FF154A079BD7306E7411FD314FDAE3079E1E8BBE92AE54DF4C` |
| 043_private_transpose_cancellation.blade | `EFB07AFCF8F7EAC0A765764ECB89DC4CC81C604726A4677ABFF2D4ADFE20E106` |
| 044_demanded_transpose_retains_snapshot.blade | `8C113E86E3FAA5771A5250FE8B3CC4A241259FD728E704BC94D9181B56D23488` |
| 045_transpose_deferred_source_failure_first.blade | `A85F732D868C78CE67D291D43718D160FA5861A8C03D0B9E0919F82066FACC64` |
| 046_transpose_failing_seed_refusal.blade | `99D7E86BB73B308B5B4925434C0C7C2025B96FBA95203A86E3362474A709BD0F` |
| 047_private_transpose_negative_zero_seed.blade | `8DE60F29FCA299D79CAE1BF459C58CA01D33D010D8EB2303EEF8CE844F274CE5` |

Corpus paths are under tests/corpus/sql-reduce. Handoff's own hash is supplied
externally to avoid self-hashing. Static diff-check passes; existing tracked
files retain LF index/CRLF worktree convention. No further coder edits until
root requests a repair; root owns the coherent review/test checkpoint commit.
