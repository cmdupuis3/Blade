# D09-I1: restore the streamed-value boundary before row-table work

Status: source-only repair proposal for root review. No production/test edits,
lowering, emission, build, native compilation/execution, tests, benchmark or
Git mutation were performed. This note is the only new file in this checkpoint.
The observed D09 census was produced by Luna, not rerun here. Lane HEAD is
5d2bf4032bc0d4e43fb0d20ce42b7946d2827a7e; production remains the D08 snapshot
717790c4. Root accepted D09 as topology evidence, not runtime validation.

## Recommended one-card fix

Remove the segment scope's _runAliasMask in CodeGenCuda.genApplyCombinator
(3748–3758), retaining the adjacent removal of selected sources from
ctx.StreamedArrays. Keep all existing rank-two bands, full-height input row
tables, aliases, readers, scans, output allocations and cleanup. This is a
correctness prerequisite, with no metadata/copy saving or performance claim.

The band wrapper is a borrowed view usable by the ordinary element-binding
peels. It must not authorize arbitrary value-level rendering of the original
streamed binding. Those peels generate their source names directly; they do
not require a streamed-value mask. With the mask removed, existing
streamedValueSentinel/captureForwardArgs/settleStreamedValueLeaks already
provide the deliberate BL7004 refusal for the observed invalid uses.

Do not remove the different _captureMask in CodeGen.genFuncDef
(CodeGen.fs:1525–1536). A lifted formal capture really receives an Array
parameter. Its definition can be valid and unused; a caller lacking a real
array is the site that must refuse forwarding it. Do not alter the sentinel's
identity/name rules or suppress its settled refusal.

No new broad value-analysis framework or rank-two local-read certificate is
needed for I1. The later row-table elimination needs a separate complete
certificate before it removes wrappers or owners. I1 must not imply that such
a certificate, a bounded run cursor, or a general rank-two streaming gather
implementation now exists.

## Observed defects and exact cause

The raw evidence is lane bench/.build/d9p, supplemented by integration's
bench/notes/round4/D09-tests.md. All four cases lowered, validated and emitted
without an error directive in the completed emission-only census. No native
program was compiled or executed.

| Case | Actual typed payload and C++ witness | Why the band is insufficient |
|---|---|---|
| 01-materialized | Scalar kernel parameters a(id6), b(id7), two rank-two operands A/B, output 5x3. CPP231/234 allocate five-entry input row tables; CPP230/233 allocate six-double windows. | The two ordinary peel chains read only current-band rows. These remain supported and unchanged by I1. |
| 02-terminal | A rank-two producer __v19 is streamed by bands, then a separate nested 5x3 fold forms term. CPP338 onward performs that fold after deleting the windows/row tables. | It consumes the owned materialized producer, not a retained borrowed input alias. Preserve this exact route. |
| 03-gather | Callable id15 has scalar parameters id6/id7 and capture B(id5), with body IRAdd(IRVar6, IRIndex(IRVar5,[0,0])). CPP327 reads B[0L][0L]. | Global row0 is installed during the first band, but its pointer continues to address the window base when rows2/3, then row4, refill the same buffer. The value becomes the current band's row0, not original B(0,0). |
| 04-array-escape | Callable id19 has scalar parameters id6/id7, captured B(id5), and returns IRVar5 as a rank-two array; out is rank4, 5x3x5x3. CPP326–330 takes auto __rowv=B and copies15 cells via pool_base(__rowv.data). | B.data is the separate five-pointer borrowed row table, not an allocator-owned pool skeleton. Even a hypothetical direct base to its six-cell value window would not supply the required15 cells. |

Both negative callables describe B as IsMutable=true because the outer binding
can be reassigned. That descriptor is not an executed assignment and is not
the refusal reason. Similarly, scalar return versus array return or an
EffectSummary.Unknown bit is not a sufficient admission/refusal rule. The
decisive facts are the original streamed source's value use and absent full
storage, not those incidental flags. A source-free array literal or fresh
array-valued callee result must remain legal.

The actual initial CG projection confirms two ordinary levels with extents5/3,
unshifted RealArray peels for A/B and scalar final parameters. It is a
reconstruction, not a final-emission certificate. The actual CPP above is the
authority. Full typed IR, callable records and projection records are retained
for all cases; no truncated structural summary was used as the full graph.

## Earliest authoritative check and why existing mechanisms suffice

1. Optimize.streamedReadOf/recordSegmentStreaming (Optimize.fs:411–508) only
   predicts a route from consumer shape. Its applied decision is not an
   executable storage/locality proof. Rejecting all captures or gathers there
   would also predate final callable lifting/rendering and conflate the fiber
   and ordinary band routes. Do not use that decision as authorization to
   materialize a streamed source name.
2. CodeGenCuda.genApplyCombinator builds and resolves the actual nest through
   routeKernelBodyThroughCall, then its statement-form body reroute
   (3447–3481). segmentRun (3579–3664) selects operands from
   ctx.StreamedArrays and excludes fiber-bound kernel parameters. The current
   mask after the rank-one certificate is the first erroneous grant: it
   authorizes all uses of a source name for the rest of this whole emission,
   not merely the current-band peels.
3. Actual ordinary values pass through CodeGenExpr.exprToCppCore's IRVar arm
   (136–179). It checks binding ID and resolved emitted name using
   CodeGenState.streamedValueSentinel (1724 onward). Removing the band mask
   restores that check for IRIndex's base and an array-valued return alike.
   Direct IRApp capture arguments and lifted closure values use
   CodeGenExprSupport.captureForwardArgs (881–894), which applies the same
   check. This is authoritative for actual rendered source-value uses, rather
   than a source-syntax guess or a capture-list-only refusal.
4. CodeGenState.settleStreamedValueLeaks (1745–1769), called by whole-program
   assembly at CodeGen.fs:4047, retains only sentinels that reached the actual
   translation unit, appends #error and records the existing BL7004 channel.
   The driver refuses before native compilation; direct emission tests can
   inspect #error and the drained spanned refusal diagnostics. A render made
   speculatively and then discarded does not falsely reject a valid route.

The codegen binding for .stream registers its source ID/name and emits reader
state only (CodeGenBinding.fs:2170–2208); there is no real full-array binding to
recover through another ordinary renderer. I1 changes that erroneous scope
grant only. It does not rename original source IDs to peeled locals or change
the sentinel's legitimate exemption for an actually materialized alias.

## Whole consumer/callable/type-use audit boundary

This is the source audit explaining the small guard change, not a proposal to
duplicate these facts in a new compiler planner. Focused tests should exercise
the following complete classes of uses before declaring the guard sufficient:

- Original source occurrences in ApplyInfo.Arrays and the matching IRMethodFor
  operand/provenance records establish the ordinary input positions. Preserve
  ArrayTypes, Identities, SharedIndexTypes, KernelTDims, input ranks/strategies,
  output index provenance, Reynolds/triangular metadata and resolved callable
  metadata. An AIDVariable("B") identity or an axis ID is provenance, not an
  executed full-array read; do not reject metadata merely because it names B.
- The valid data access is the generated outer RealArray row peel followed by
  the same operand's inner scalar peel (LoopNest.fs:402–461,489–522). These
  render direct C++ names and paramFinalNames replaces kernel parameter IDs
  with scalar locals. The segment loop bounds the outer index to the filled
  band; normal inner column bounds remain unchanged. No original source IRVar
  is rendered as a value by these peels, so removal of the mask preserves them.
- A raw source in the final kernel's IRIndex, IRVar return, IRLet alias,
  tuple/record, closure, call argument or assignment is an actual value use.
  It must retain the ordinary streamed-value check. The existing original-name
  rendering cannot be licensed just because B is also an operand. Neither a
  leftover capture descriptor nor IsMutable alone proves such a use occurs.
- A direct or aliased named callee, nested helper, lifted callable or closure
  can hide the original source in its capture list/body. Actual callable
  references must resolve through resolveKernel/resolveCallable and their
  parameter/return/capture types, bodies and forwarding behavior must be
  inspected in regression coverage. Inlined bodies eventually render IRVar;
  an actual call forwarding a streamed capture reaches captureForwardArgs.
  A function definition's formal-capture mask remains valid, but its caller
  receives no band-wide grant. Source-free callees, resident captures and
  recursion do not acquire a new blanket refusal in I1.
- The array-return arm in genLoopNestStreamedCore (LoopNest.fs:2360 onward)
  first renders the actual return expression. Thus 04's raw source returns a
  sentinel before generic pool_base/copy text can form runnable code. Keep the
  arm for fresh array returns, resident array returns and literal rows. Do not
  reject merely because output rank exceeds nest depth.
- Type/index extent expressions are outside generic childrenOf traversal;
  Optimize.irTypeExtentExprs/embeddedExtentExprs and the retained full IR expose
  those payloads. A statically evaluated IRExtent is allowed: renderExtentExpr
  (Expr.fs:1897 onward) returns a type-derived literal without rendering the
  array. A dynamic extent that really needs an Array renders its base through
  the checked expression path. IRRank likewise asks the type, not source data.
  Preserve passive shape queries rather than declaring every source-name
  occurrence an escape. Embedded executable expressions that actually use
  values, nested loop operands and call/return type payloads must not gain a
  mask through this card.
- Both later kernel rewrites and epilogue/alternative-renderer work occur
  after the old mask installation (Cuda.fs:4005–4090). With no band mask they
  cannot inherit that broad source-value authorization. Normal final render
  and capture forwarding remain checked. Direct-name peels stay the intended
  exception; do not add a different raw-name escape to make a refused case
  compile. A new renderer bypass found during implementation is a review
  blocker, not permission to broaden I1 into a new value-analysis framework.

No binding is omitted and no storage is removed in I1. Therefore a new
whole-module ownership/demand analysis or reference-keyed stale-certificate
protocol is unnecessary here. The future row-table card still needs its own
complete final-kernel/peel/capture/type/side-use certificate before deleting
any alias or owner; I1's restored guard does not supply that certificate.

## Safe fallback and refusal boundaries

The two observed raw captures need a whole original B that .stream never
materialized. For them the existing deliberate BL7004 refusal, advising .read
or a supported consumer, is the minimal safe behavior. Do not set segmentRun
to None and claim that is a safe fallback: the old fiber reader explicitly
refuses a scalar leaf when resultRank<=0 (LoopNest.fs:575–614), and no full
source Array magically appears. The tile slab only changes traversal bounds;
it also does not create the missing full array. Keeping the full-height row
table does not retain old values, and does not turn it into an owned pool.

Do not insert an implicit .read for B. That would add resident storage and
change source read/validation timing and failure order. An explicitly authored
.read is already the supported full-array route and must continue to support
ordinary global gathers/returns. Legal fiber kernels obtain a real trailing
fiber from GenStreamFiber and may gather within that fiber; their route is
selected before the ordinary band path and must be unchanged. Resident gathers,
static metadata queries, source-free scalar kernels, fresh array results,
terminal/fiber/tile/group routes and mere future optimization nonadmissions do
not justify blanket rejection.

In particular, I1 does not pronounce every gather unsupported. It restores the
existing .stream value boundary for an original source that has no full-array
value. Supporting a new explicitly captured rank-two in-window index pattern
would require its own locality/rendering proof; retaining stale source aliases
is not that proof. Supported current routes keep their normal fallback.

## Bounded tests to author only after approval

Reuse one fresh two-source v2 store of shape5x3/chunks2x2, twelve32-byte chunks,
A(i,j)=10*i+j and B(i,j)=100+10*i+j. No large fixtures or benchmark are needed.
Pin/restore optimizer defaults, FP repro/contractoff, BLASoff and OMPthreads1.

| Focused check | Required evidence |
|---|---|
| Frozen 01 positive | No refusal; same band route, both original row tables/windows and required output remain. Independent 5x3 values are [[200,203,206],[230,233,236],[260,263,266],[290,293,296],[320,323,326]] as F64. Check final row/nonzero band origin. |
| Frozen 02 terminal | No refusal; retain streamed materialized producer then separate fold, result3945.0. Do not replace this route as part of I1. |
| Frozen 03 and04 | Assert deliberate BL7004 with source/declaration span and .stream value reason, plus #error for direct CPP emission. Never launch generated CPP that contains refusal. No runnable stale B[0][0] or borrowed full-array pool copy is an accepted fallback. |
| Resident B controls | Change B to explicit .read; global B(0,0) gather remains legal, with output100+10*i+j. Whole resident B return remains legal and copies a real full pool. This proves no blanket gather/array-return ban. |
| Legal stream fiber control | Reuse the existing fiber differential harness, including indexing within the row parameter. A row(0) projection gives [0,10,20,30,40]; .stream versus .read must agree on demanded output. |
| Source-free array return | A scalar zip returning a small literal row [a,b] remains supported; every output cell pair equals [A(i,j),B(i,j)]. Original source identity in output provenance alone must not reject it. |
| Callable/capture controls | Named/two-level helper and callable alias forwarding raw B must refuse; a capture-free scalar helper and a resident-array capture/gather must remain valid. Exercise actual call forwarding as well as inline kernel rendering. |
| Alias/type controls | A kernel-local alias/tuple/return of original B cannot erase the guard. A static IRExtent(B,0) represented in a synthetic exact typed test remains a literal metadata query; dynamic value-dependent type/extent payload cannot acquire a full-array grant. Check inactive/discarded renders do not create false refusals. |
| State restoration | A rejected program followed by 01, and a rejected direct emitter followed by another valid emitter, must not retain sentinel/refusal/mask state. Keep the separate lifted-formal capture semantics exercised. |
| Accepted-route failure sequence | In a positive band map, A's later short/null chunk in the same window beats earlier A nonfinite; A's completed-window missing scan beats a B chunk failure; an earlier A-window missing scan beats future-window corruption. Preserve source reversal behavior and final-band global coordinates. |

All accepted programs retain the original per-band sequence: A chunks in the
current grid order, A normal missing scan, B chunks, B scan, then computation.
No new I/O, prefetch/cache, source reread, metadata probe, output publication,
allocation or cleanup ordering is introduced. The unsupported cases refuse at
codegen, not via an altered runtime data-failure policy.

Expected implementation ownership is CodeGenCuda.fs for the tiny segment-mask
removal/comment and tests/ZarrTests.fs for the focused coverage. No provider,
runtime, Types, Optimize planner or allocator change is required. If a test
needs to exercise existing helper APIs it should do so without creating a new
production diagnostic API. Root should review the complete small diff before
Luna's fresh build and bounded focused gate; broader Zarr validation is a later
root dispatch. No execution is authorized by this source-only proposal.

## Frozen evidence identities

- CodeGen.fs: 693744E92885DD0B292CF7419833F4D5E9991B261EEDCC9685D409E8EC98495A
- CodeGenBinding.fs: 829568D273B59E26CE1233C2848A0A387343537E49A1BAF6B803BEDF507AC597
- CodeGenCuda.fs: 467C733B3F95D321ADB17E271C6E5110D7DC97E7549F4EBA08D674F67E09EF5C
- CodeGenLoopNest.fs: 88BCA6E982028F4DABDCD086C28E2614E3109B9B715C2B71B45E07F100701D1A
- CodeGenState.fs: 1581C1ED971FA5E7B5BC410E7EF6052DB451916142F6BD4375F10E5C08E9762F
- CodeGenExprSupport.fs: 99AE0872F95E31CC8EE64318860257544AFBAFF389172F03E766E9FE940D3480
- CodeGenExpr.fs: 3E9B3D035BF268C969B578D1318965D9B98F5ADD48D8399071F6696233853BD5
- Optimize.fs: 96452127C6A298BC4C2AB2B8DEC81BF4DCC8B968F1C4F890DF42ACA613F0E36A
- IR.fs: 90FCF321A3A019F2EE75C519DCA44B9C1127881271D509FB054212DB0EBD6216
- Observed03 CPP: C88C4311045829B7FF0CD0FD670BF59767D500883B8689E746DE23C4F1FBC54A
- Observed04 CPP: 5104663131F4DB5F8CC87ACC27C99D2CE3A85EFEE02A11EB02C47D93840A7D3F
- Integration D09-tests.md: 62805AAF6DF7930034BEA0CAF323F430B9471DD9FE222BAACA125500642D4D59

The original D09 bridge, four fixtures, request, manifest and observed artifacts
remain unchanged. Graft found no F# coverage for the streaming symbols;
targeted source/artifact reads supplied the anchors above.
