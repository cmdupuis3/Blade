# C06 conditional implementation proposal: one private full-fold arm

Only observed01 is settled here; remaining controls may change this proposal.
Read against lane `cd23a7b35b0898d5cd3e9930273da119e5fd0d36` and the exact
census DLL `97CD9919D5CA349C83CE680117C5F8AF1CDBC3B924F5F178C3CF0DDFA2D351A6`.
No production/test/adapter/old-census edit or coder execution is authorized.
Input06's separate reserved-name repair is recorded in C06-census-repair.md.

## Observed01, including administrative nodes

Both early/final summaries and full typed payload retain this exact shape:

```text
host total#20 : Float64 =
  IRLet(p14, IRApplyCombinator info,
    IRLet(a15, Float7,
      IRLet(u25, IRForRange(r16, Int0, Int3,
        IRLet(u24, IRForRange(c17, Int0, Int3,
          IRLet(u23, IRAssign(IRVar(a15,F64),
            IRApp(IRVar(fold22,F64*F64->F64),
              [IRVar(a15,F64); IRIndex(IRVar(p14,privateType),
                [IRVar(r16,I64); IRVar(c17,I64)], Some identity)], F64)), Unit)), Unit)),
        IRVar(a15,F64))))
```

u23/u24 continue with literal Unit; u25 continues with the exact final a15.
They are bindings of unit-valued statements, not arbitrary lets to strip.
Map21 is `(v11*v11)+1`, fold22 is `(a18*2)+b19`; parameters/returns are concrete
F64, no captures/strategies, and BOTH effect summaries have Unknown=true.
M4 is an actual3x3 dense literal; its ordinary IsMutable=true flag is not a write.
MutableArrayLets contains p14 ONLY. All provider/random/compound/sparse tables
and derived-origin/alias-name tables are empty in this observed module.

Stored input axes are R#2,C#3; mapped/indexed axes R#12,C#13, all plain rank1,
SDimension, extent3, SymNone, no dependencies. Equal numeric IDs are not required;
IRTotality.sameIndex pairs the same named R/C provenance. R and C each have3
cells, so swapping them must still fail. IRIndex.identity names `__rksrc10`;
that spelling is diagnostic metadata, never a proof of compiler generation.

Actual C++ has required M9 and mapped __v14 nine-cell allocations, followed by
two serial loops with one scalar state and `fold22(state,__v14[r][c])`. No row
vector exists. Its deferred-map comment and scalar seed7 assignment precede
materialization of __v14, which is forced before the fold nest. Thus IR ordering
is producer-binding then seed, while emitted forcing is seed then producer-work;
literal seed motion has no observable effect. Do not generalize this observation
to a failing/effectful seed. No explicit main cleanup frees the mapped pool.

Evidence in bench/.build/C06-census-runner: early typed-ir SHA256
`9253542E98C9CDF625A9D77C0FC8A3FA40B30E4901A75E4EFB4DFA63B79B2D14`,
final typed-ir `A2C78BAE6CA6BC9B76A508810A641AF3D233DAC3B5786EB919EC8259534D1603`,
01-fusion-on.cpp `4547FA549B5B910C9A823491FC8219960B2545DED43CE2562C9545FEEA1D0948`.
These establish materialization, not native output or measured memory savings.

## Exact matcher and replacement

Extend the existing early terminal entry
Optimize.fusePartialRowTerminalsWithCensusModule (8021) with ONE scalar-host arm,
ahead of its existing array-result cases. Keep default optimizeModule signature
and behavior: only foreignCensus=Some complete set authorizes this new arm.
Read FUSION/repro per call. Match the exact whole host Value above, including
all three unit-valued wrappers, literal nonnegative bounds, direct IRApp fold,
ordered `[acc; fullMappedCell]` arguments and scalar final accumulator. No generic
let/unit normalization, section/binop variant or nested-row drain is proposed.

Require p,a,r,c,u23,u24,u25 distinct and uniquely defined within the program;
loop indices are I64, accumulator/read/store/return/host are F64. p has exactly
one executable read, a exactly the store target/first fold argument/final result,
r/c only their respective full-index positions, and unit binders have no reads.
Reject swapped/repeated indices, nonzero lower bounds, wrong upper bounds,
reversed fold arguments, extra accumulator reads/writes, additional statements,
changed unit continuations, checks/casts/slices, duplicate binders or a different
final value. No source-name or fixed-ID heuristic.

Replacement is host.Value = IRReduceCompute(IRApplyCombinator info, original
fold reference, original literal seed). Preserve host ID/name/type/print policy,
original map/fold references and every validated ApplyInfo field. No callable,
parameter, binder or arrow is minted: there is no freshness problem or origin
proxy to invent. Removed local IDs are audited as dead, not reused. Retain all
unrelated types/functions/origins/side facts. Drop ONLY p from MutableArrayLets
when present; any other eliminated local ID in that table is malformed/refused.

The entry currently returns the original module when minted.Count=0 (8387).
The new arm mints no callable, so track its successful rewrite/removal explicitly
and return changed Bindings/MutableArrayLets even when minted is empty. Preserve
existing C02/C04 minted-callable ordering and DerivedFuncOrigins behavior. Do not
clean their historical side metadata as an unrelated fix.

## Metadata, totality, versions and complete residual census

Check the actual records, not just info.Arrays:

- info: one stored Var(M) with its exact source type; KernelInputRanks=[0],
  KernelOutputRank=0, KernelTDims=[], SDimsPerArray=[2], SharedIndexTypes=[],
  SymcomStates=[SCNeither;SCNeither], TriangularLevels=[false;false], no coiteration
  or Reynolds, both speedups1, F64 plain rank2 output with no dependencies.
- Loop must be IRMethodFor with the SAME source ID/type/identity (not object identity),
  matching ArrayTypes, SDimsPerArray=[2], TotalSDims=2 and SharedIndexTypes=[].
  Both Identities lists agree with the actual stored source identity. The map's
  source/output/private indexed types use sameIndex in axis order, with element,
  rank, storage/SDimension roles and other metadata checked independently.
  Bounds equal their matching static extents; structural axis equality fails01.
- Resolve concrete one-parameter map and two-parameter fold, unique positional
  param IDs, F64 returns and plain SVal function arrows. Use IRTotality.auditCallable
  plus closed transitive callable/reference audit. Unknown=true is neither
  purity evidence nor an automatic rejection. Reject capture/global/callback,
  unknown/recursive/failing/writing/resource/device/OMP/arity/commutation or
  hidden strategy metadata; preserve exact serial association under repro.

Use storedArrayProof on the earlier M binding/alias chain, check dominance,
actual payload writes and lifetime events, and reject provider/deferred inputs.
Do not reject IsMutable alone. Match/certify the sole local accumulator write
before effect auditing: a naive writes(host.Value) would reject every valid01.
For residual write/demand scans substitute the proposed IRC ONLY at the actual
host binding ID, never every reference-equal expression in another root. The
matched unit/accumulator region has no effects except the certified local update;
all other source/version writes, calls or events still undergo the full audit.

compactionRoots over module plus program callables supplies binding values,
side execution operands, declared/embedded types, every callable's unused
param/return/capture types and source/name references. Retain host.Type and its
side roots even when replacing its Value. Reject any residual reference to
eliminated IDs, aliases, captures, array/row/extent/print observers, hidden type
or Apply/MethodFor payload demand, stale owner overlap or ambiguous name roots.
An additional binding sharing the SAME Value object is a refusal. Explicitly
inspect MutableArrayLets as described above; it is not a compactionRoots item.
Require an ordinary host with no execution side payload even if it does not
mention p. Default demanded top-level P/rows stay fallback; M print remains.

Foreign refs are already computed unconditionally by Lowering3119-3144 and
passed to optimizeModuleWithForeignCensus. Missing evidence declines; any
foreign reference to eliminated locals or the stored source/version chain
conservatively declines the initial route (a set alone cannot certify a foreign
writer). Do not reintroduce the old empty-CompoundInits shortcut. Placement stays
before IRLift/CSE/pool/hot-row/membership planning; no late table rebuild or new
ownership rule. Already event-containing modules retain their existing guard.

## Expected files and focused verification after authorization

Likely production ownership: existing Optimize early-terminal region only;
OptimizeTests plus focused sql-reduce corpus/handoff witnesses. No lowering,
IR/backend/runtime, sibling-fold, provider or broad ownership change. Root must
first review remaining02-08 topology/emissions; unexpected existing gains/refusals
may close this as a census or narrow admission further.

Planned tests: actual early01 shape including unit wrappers and differing axis
IDs; successful IRC route + cleared p-only mutable-array metadata; arbitrary
fresh renumbering; duplicate/reused IDs and each malformed wrapper/read/write/
index/bound/axis/metadata alternative; hidden host execution, shared-object second
root, unused callable/type/capture and foreign-demand refusals; actual stored
source/alias writes; transitive closed helper positive and unknown/failing/captured/
parallel helpers refused; FUSIONoff/reprooff/missing-census fallback. New tests must
use correct five-argument IRBinOp and pattern-match interpreter numeric Values.

C++ allocation/dataflow assertions distinguish retained M9 from eliminated mapped9,
match the exact fold argument order/seed count and require all original prints.
LLVM gets real source/allocation assertions only after its census emission passes.
Independent interpreter/native ON/OFF pins include7044, cancellation1 versus
nested0, demanded/nested1206 controls, zero-axis single seeds including -0.0, and
late producer failure precedence. Both lanes consume optimized IR, so independent
pins plus optimizer-OFF comparisons are required. Native evidence is separate
from emitted text. Bounded first implementation gate is fresh build+optimize;
later runner stages and implementation itself require root dispatch. No full suite.
