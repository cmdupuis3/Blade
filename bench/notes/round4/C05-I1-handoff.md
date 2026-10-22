# C05-I1 handoff: scalar sparse indexed assignment in the interpreter

Bounded prerequisite repair on `6afb623e1ae9d1293cdbf347a68f3e6e33ae6e66`.
No build, lowering, emission, interpreter/native execution, test or oracle was
performed by the coder. Source is frozen for root review; Luna alone validates.
Existing `.r4tmp`, all prior runner artifacts and all C05 corpus pins are kept.

## Confirmed failure and existing semantics

C05 checkpoint dea61728 built; optimize returned 744 pass / 1 fail. The only
failure was canonical snapshot_mutation, missing total7423 and after99.
The original test reports only stdout, so the reason was absent from its log.
Root-authorized diagnostic6afb623e replayed the exact synthetic source/gates and
unchanged corpus050 using successful DLL85A5F249...C9578BF. Both lower/validate
succeeded; both interpreter results were exit125, empty stdout, and stderr
`interp-unsupported: IRAssign to non-array index target`. Both actual ordinary
hosts were function calls, correctly outside the new C05 terminal certificate.

Evidence remains under `bench/.build/C05-snapshot-diagnostic-runner/attempt1-output`:
synthetic/corpus050 `.status.txt`, `.stdout.raw.txt`, `.stderr.raw.txt`, full
`.typed-ir.txt`, `.cpp`, `.certificate.txt`, `.decisions.txt` and validation files.
Both raw stderr files have SHA256
`4DB0788720333380F7BA2BA2635BAFC900D51D9EE5FB2F0746944E9DCBED8BB1`.
The earlier optimize log stays in C05-implementation-runner/attempt2; SHA256
`BDBE53BD5EAB64D6A760B4D162D44721F356265997888323E54DE2973601C018`.

Before this repair Core.evalAssign1240-1249 accepted VArray only for LVIndex.
The generated snapshot C++ already emits
`p(std::array<size_t, 2>{(size_t)(4L), (size_t)(1L)}) = 99.0`.
Sparse::operator() (`src/cpp/nested_array_types.hpp:184`) returns the existing
compact cell by `idx->linearize(key)`; it never inserts or reorders keys.
The sparse index's missing-key lookup raises the established BL8006/message
(index_types.h213-216; ArrayOps.sparseLinearize1817-1820).
ArrayOps.sparsePartial1846-1862 already copies matched cells in supplied order.
Core.evalCall1114-1147 binds the evaluated VSparse to formal parameters using
the same backing; ordinary sparse aliases also keep their backing because the
copy-on-bind policy is dense-only (Core1420 onward, CodeGen.genVarAliasBinding).
None of these semantics or copies is changed.

## Exact repair and boundary

Only production change: one 27-line VSparse arm in Core.evalAssign. No shared
ArrayOps helper is edited. It accepts a full packed key of exactly LeadRank,
using classifyCompoundIndexTuple as well as arity, and normalizes the existing
rank1 scalar-key sugar to a one-element packed key as sparse reads do.
It requires exactly one stored index slot, TrailingStride=1 and NO supplied
trailing indices. Partial/wildcard/prefix/overlong targets and all trailing
axes, row writes or unrepresented multi-trailing layouts remain unsupported.
This restriction is this interpreter repair's boundary, not a new type rule.

The existing evalExpr evaluates RHS first; evalAssign evaluates/forces the
target once. The new arm evaluates each packed coordinate once in list order,
then calls the existing sparseLinearize. A missing key raises the same BL8006
and exact message before any write; there is no insertion, new key, sorting or
iteration change. A small flat BladeArray descriptor points to the EXACT same
Store and existing writeCell performs the typed scalar store at the found
ordinal. No data allocation/copy or sparseToDense call occurs. Sparse key/rank
objects, cardinality, index metadata and nominal provenance remain unchanged.

Root rejected an initial optional-trailing-offset draft: an unchecked offset
could address another key through the flat pool. That draft is removed. There
is no trailing-offset arithmetic or newly accepted trailing write in this freeze.
Root also caught Value's documented physical equality; final numeric tests
pattern-match VFloat/VInt and compare extracted primitive values/lists. Only
explicit backing/key/rank identity checks use ReferenceEquals.

Optimizer/certificate, C++/LLVM codegen, parent ownership, wildcard snapshots,
existing tests and every corpus source/pin are unchanged. In particular original
snapshot total7423/after99 checks remain independent and unchanged. This repairs
an existing unsupported interpreter path; it does not expand C05 admission.

## Authored focused checks (not execution results)

Twelve checks registered in the existing optimize gate:

- Nine direct interpreter cases: same Store/key-array/rank-map objects and exact
  nominal metadata; present-key aliases and residual independent snapshot;
  RHS-target-coordinate sequence1234 exactly once; missing-key BL8006/no insertion
  with the same evaluation order and every compact cell unchanged; rank1 scalar
  key sugar using key2 rather than compact ordinal2; wildcard/prefix/overlong
  refusal; single-trailing/row/multi-trailing refusal with backing unchanged.
- Three source cases: a `let mut` sparse alias passed to a mutable formal yields
  total17/original17/untouched4; computed absent key raises BL8006 (not125); dense
  mutable-formal control remains total9/after9. These checks include raw stderr
  and exit status in their failure report.

Original ten C05 canonical cases, twenty-six census refusals and all existing
route tests remain. The existing snapshot case now has a supported write path;
its expected values are not changed. Runner results are recorded below. Direct
internal tests and the two diagnostic cases are separate counts.

## First repair build failure and test-only constructor correction

Root committed the repair checkpoint as `2429ece2`. Luna's first C05-I1
Release build stopped before tests at OptimizeTests.fs2422: FS0019, IRBinOp
received three arguments but requires five. The test's two counter-arithmetic
nodes now explicitly use IRElementwise and SrcLoc.Nowhere, matching existing
synthetic scalar arithmetic in IR.fs1185/3610. The multiplication is separately
bound before the addition; the counter expression and evaluation order are
unchanged. Other new IR constructors were checked against their declarations.
Core production and all other tests/corpus remain unchanged by this correction.

Failure evidence stays at `bench/.build/C05-I1-runner/attempt1/build.stdout.log`,
SHA256 `A2B2E76729A5A8FFF900CEC3BB0E71A4D6E104F2C49F2B11D4156A7E233F7D0E`.
No retry build/test/lowering/emission/execution was performed by the coder.

## Second attempt and test-only mutable-alias correction

Root committed the constructor correction as `99108a19`. Luna's second repair
build passed; optimize returned 756 pass / 1 fail. The original snapshot case
passed, as did eleven of the twelve I1 checks. The sole failing new fixture
passed plain `let alias = S` to a mutable parameter, correctly triggering BL4005.
The fixture now declares `let mut alias = S`, preserving the language's mutation
rule and unchanged total17/original17/untouched4 pins. Production is unchanged.
No broader test or admission change is included.

Evidence remains at `bench/.build/C05-I1-runner/attempt2/optimize.stdout.log`,
SHA256 `35D4DC3540D849F8094C4DC8F559541C2DCC5EDF8FEE4BD17356858E8666738D`.
No retry build/test/lowering/emission/execution was performed by the coder.

## Sole-Luna staged request

After root review/commit, with private TEMP/TMP and output under
`bench/.build/C05-I1-runner/attempt3`, record source/content/binary/tool hashes:

1. `dotnet build Blade.fsproj -c Release`. Stop on unexpected failure.
2. `bin/Release/net10.0/Blade.exe test optimize`. Capture complete stdout/stderr,
   exit and pass/fail/skip counts; report every sparse_wildcard_I1 result and the
   unchanged snapshot_mutation result. Stop on unexpected failure and return
   to root. This gate includes the authored twelve cases; no full suite.

After that result, root may explicitly dispatch the existing diagnostic adapter
against the NEW verified DLL to a fresh directory:

```powershell
dotnet fsi --reference:bin/Release/net10.0/Blade.dll bench/notes/round4/C05-snapshot-diagnostic/snapshot-probe.fsx bench/.build/C05-snapshot-diagnostic-runner/repair1-output
```

The adapter's original README/DLL hash describes the historical failure replay;
this separate repair replay requires the newly built binary's recorded hash.
Expected independent results for BOTH sources remain status0, total7423.0 and
after99.0. Save all raw outputs, full IR, CPP, decisions and certificate report;
function-expression certificate remains inapplicable. Preserve attempt1-output.
No automatic replay or native/category launch is requested before root reviews
build+optimize. Original C05 native ON/OFF corpus048-053 and category gates remain
for later root dispatch. No expected-pin regeneration, broad interpreter
refactor, full suite, benchmark or measurement is requested.

## Freeze

C05-I1-SHA256SUMS.txt records changed Core/tests plus unchanged optimizer/emitter,
shared owner interfaces, corpus050 and frozen diagnostic adapter. Original C05
manifest is retained as the original checkpoint record. Static diff-check and
manifest verification pass; source working files retain CRLF/indexLF. Handoff
and manifest hashes are sent separately. No git mutation/commit by coder.
