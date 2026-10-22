# D08 implementation handoff — one private statistic barrier

Source-only authoring at lane base `1fb8a3d9` (combined production identical to
integration `5e208a77`). The observed probe graph is p6/t7/m8/result13; actual
IR and baseline emission were read from the sole-runner artifacts, not rerun.
Coder performed no build, lower, emit, C++ compile, execution, tests or benchmark.
This handoff is pending root review and sole-Luna validation, not acceptance.

## Validation history and test-only repair

Sole-Luna attempt1 built reviewed checkpoint `e9790f56` and stopped with two
FS0597 test-syntax errors at PrivateSpoolTests.fs lines88 and126. No focused
adapter or native case ran. Complete log:
`bench/.build/D08-implementation-runner/attempt1/build.stdout.log`, SHA256
`AB0A000FA3C4113ACC77258DE15DA820C08C0EA344045A3E6069106A1538660B`.

Repair adds parentheses around only the two `OperatingSystem.IsWindows()`
arguments to `not`. Static inspection of the complete new test found no other
unparenthesized `not` method-call forms. Production and test intent are
unchanged. Coder performed no execution; this repair is frozen without commit
for root review, then the same build/focused gate request below.

### Attempt2 admission diagnostic (sole-Luna completed; historical bridge)

Reviewed repair checkpoint `383bbd20` builds successfully. Sole-Luna attempt2's
focused gate then stops at its first positive admission assertion, before any
native case: 0 passed, 1 failed, 0 skipped. Preserved `focused.stdout.log` SHA256
`C7B2E4B0456E8E024E3D8493016A5D1B97FEF5ADCEF9DC7BC4960A35CF98FB0F`.
The loaded DLL SHA256 is
`E27D59F80593A9E8DA752D8A2F4793C6FC72D7D1FA4963CD2100DF3D052BFE88`.
This log does not contain the planner reason/decisions or generated C++; it
cannot establish which admission condition failed. No guards were weakened for the diagnostic. The evidence-based representation
repair is recorded next.

Source-only `D08-runner-inputs/diagnose-admission.fsx` asserts that exact DLL,
reuses attempt2's existing input_n5/owned paths without changing their files,
and requires a new private diagnostic output directory. Its source/policy/FP
settings match the focused fixture. It lowers once, saves/flushes the planner
reason, lower decisions and current ownership census first, validates the same
graph, then follows the focused test's resident-emit/options-emit sequence.
It saves decisions and owner facts at both boundaries before separately guarded
full typed binding/callable/type/provider/demand-root dumps. Generated resident
and options C++ are saved as text only. No JSON serializer, fixture recreation,
native compilation/execution, test-category call or new build is requested.
The prescribed diagnostic output path is 87 ASCII characters; its options.cpp
path and projected (never generated) options.exe path are each 99. The reused
input_n5 path is 138 and the D06 owned root is 135, below 240. The longest authored
focused native executable name under the prior attempt2 root is 166 characters.
The bridge logs actual path lengths/ASCII status and refuses diagnostic paths
at or above 240. Future native gates should retain short private TEMP/TMP and
case roots; this bridge does not build or launch executables. Use Luna's
established capture/provenance wrapper with private TEMP/TMP:

```powershell
$d08Checkout = 'C:\Users\cdupu\Documents\GitHub\Blade\.round4-worktrees\d-streaming'
$d08Dll = Join-Path $d08Checkout 'bin\Release\net10.0\Blade.dll'
$d08Diagnostic = Join-Path $d08Checkout 'bench\notes\round4\D08-runner-inputs\diagnose-admission.fsx'
$d08ExistingCases = Join-Path $d08Checkout 'bench\.build\D08-implementation-runner\attempt2\focused-cases'
$d08NewDiagnostic = Join-Path $d08Checkout 'bench\.build\d8diag'
& dotnet fsi ("--reference:" + $d08Dll) --exec $d08Diagnostic $d08ExistingCases $d08NewDiagnostic
```

Stop after this emission-only diagnostic and return `admission.txt`, generated
C++ and captured process logs for root/source review. A refusal is a diagnostic
outcome, not native-gate acceptance. Coder has not executed this bridge; the sole-Luna result is recorded next.

### Proven fresh-axis mismatch and narrow repair (awaiting validation)

Sole-Luna diagnostic exits0. Raw evidence is
`bench/.build/d8diag/diagnostic-out/admission.txt`, SHA256
`5498BAA74A8F961874647B3F33AE41BCAE3AA29567D678A81B78F5BEF857C5AB`.
Its lines24/45/64 record the same candidate-pattern refusal before/after both
emissions, and lines30/49/68 show no storage-owner conflict. Binding dumps at
lines86/98/225 show the exact axis roles: source A anonymous ID1; producer
map output, p.Type and all p references ID5; terminal map input ID5 and fresh
output ID12. All are ordinary rank-one F64 extent5 with unchanged index fields.
The expected baseline cell-epilogue decline is not this admission's cause.

The incorrect guards required sameIndex between these distinct anonymous roles:
producer output5 against source1; replay input5/output12 against source1; and
p/fold/extent type5 against source1. `IRTotality.sameIndex` at lines68-77 correctly
compares anonymous IDs and remains unchanged. The derivation is explicit in
`IRLoopStructure.fs:853-866`: a size-one group copies the source index verbatim
and refreshes only Id. No shared-index or general equal-extent equivalence is
licensed by that rule.

The repaired certificate therefore requires each input IRVar type and complete
ArrayTypes entry to equal its actual certified binding type, with coherent
loop/apply identities equal to that binding's AIDVariable name. Both maps must
have a plain output whose index record equals the input record with ONLY Id
changed to a distinct fresh nonnegative ID; output value identity must be None.
Tag, Kind, IxKind, rank, symmetry, extent and dependencies stay exact. Producer
binding type must equal pi.OutputType exactly. First-fold/extent p references
and replay's input retain the exact p type/axis; only the full terminal scalar
fold consumes replay's fresh output. Ordinal correspondence follows solely
from the already strict scalar unary-map metadata/body and this copy rule.

No scalar arithmetic, FP, shape/cardinality, callable, source/capture/type/side
root, assignment, print, ownership, emitted order, provider validation/failure,
allocation, protocol or publication rule changed. Emitted runtime/provider code
and D06 headers are byte-unchanged. The only production edit is the narrow
representation match in OptimizePrivateSpool.fs. Coder performed no execution.

Authored unrun regression adds the actual three-role fresh-axis witness and six
independent refusals: forged same-extent source axis; producer output disagreeing
with its binding; forged same-extent replay input; changed replay output tag;
inherited replay value identity; and coherently changed loop/apply identities
that name another source. Existing N5=382/N1/empty/fill/nonfinite actual-route,
FP/print/owner/type/refusal and fault-order gates remain. Next request after root
source review: source-matching Release rebuild then ONLY the focused FSI gate;
stop for root review before any broader category. The historical diagnostic
bridge pins its old DLL deliberately and is not the retry entry.

The diagnostic runner used output `bench/.build/d8diag/diagnostic-out` (102
ASCII characters), options.cpp/projected options.exe114, input138 and owned135;
no executable was generated. Short private TEMP/TMP and native case roots stay
required; no path/ownership guard was relaxed.

### Role-aware gate host setup failure and bridge-only repair

Sole-Luna built `d5b2599a` successfully; loaded Blade.dll SHA256 is
`A072EB74B14DEDB09CD7E05F8210FD36A2AEDAF5258E1EE51358C47710D205A0`.
The focused gate passed its three admission/source-order assertions and then
stopped before its first native compilation with BL7001. Full failure evidence
is preserved at `bench/.build/d8rr/focused.stdout.log`, SHA256
`B2C32A6253A1B715D6B7DD7E37FCB195B6E588D85B1A21F47DD4E57D0CB0C232`;
provenance SHA256 is
`475F851EE802B4AEAE9368BD2D6337956BD6200181EB3BACD34821347AD2F676`.
Totals3 passed/1 failed/0 skipped are setup evidence, not a native verdict.

`CodeGenLoopNest.fs:3869-3871` resolves each header from
AppContext.BaseDirectory/cpp; deployment at4045 reads the registry through that
resolver. Under FSI the base is the dotnet SDK's FSharp directory. The fsproj
copy entries at773-780 instead place the frozen D06 headers beside Blade.dll
in cpp/. The public .NET10 implementation reads the string host-data key
APP_CONTEXT_BASE_DIRECTORY before its cached default, and AppContext.SetData
sets that data: [official runtime source](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Private.CoreLib/src/System/AppContext.cs#L23-L63).

The focused FSI bridge now saves/restores that host datum in try/finally,
sets it to the directory of the already loaded Blade.dll BEFORE any gate/header
access, and verifies the observed base directory. It records the assembly
path/hash/FullName/MVID and actual header paths/hashes, including registry
membership. All registered headers plus blade_private_spool.hpp must exist;
the two D06 header hashes must equal the accepted frozen bytes. The loaded
assembly object/hash is rechecked before invoking the unchanged compiled gate.
No alternate assembly load, bridge-side header copy, production resolver change,
compiled test edit or resource policy change is part of this host repair.
Coder has not executed it. A new short private ASCII case root and Luna's
existing private TEMP/TMP/capture wrapper remain required.

The separate static deployment gap is now repaired with root authorization:
runtimeHeaderNames now registers blade_private_spool.hpp immediately after
blade_runtime.hpp. The existing shared deployment/cache/cleanup machinery then
handles this header uniformly; fsproj already copies its unchanged accepted
bytes beside Blade.dll. There is no bridge-side copy, resolver special case,
new disk authority or compiler admission change. The bridge will observe this
header as REGISTERED=true after the required fresh source-matching build.
It refuses a DLL whose registry omits that header before entering the gate.

This checkpoint therefore freezes the host bridge plus ONE production header
registration prerequisite. The formerly built A072EB74... DLL is historical
and must not be used for the next focused retry, because its registry omits the
header. Sole-Luna must rebuild Release once, record the fresh DLL/header hashes,
then run only the focused FSI gate with the revised bridge and a new short
private ASCII case directory. Stop after the focused result for root review;
no broader category/full suite/benchmark. Coder performed no execution.

### First native compile failure and C++17/header compatibility repair

Sole-Luna rebuilt checkpoint `427a8903` and reached the first native compilation;
no native program executed. Exact generated C++ is preserved at
`bench/.build/D08-runner-427a8903/c/D08_n5.cpp`, SHA256
`653A7E027B601E6337AFF1DF083301D03F954471FAE13F80FFCAD5302BAFFC46`;
its `.compile-error.log` SHA256 is
`768BBEE840F80914B4583B29E3D515EE0C36A84B7B3F722434C8FBE6070BCFF9`.
Focused stdout SHA256 is
`1BA3819B0B468EDDD9E3CF84A896BDDAC85F7AFB5D98E34A3BAB44EB2DD02552`.
Fresh DLL at that attempt was
`0715071C700C7F9EB15092C6C699898DED9E0239CF84F0C2C51E95086DC29E8D`.
The raw command is production Build.compileCpp's `-std=c++17`, matching
Build.fs:1021 (also ordinary memcheck/CUDA use17), not a test-only override.
The D06 protocol harness had previously compiled with C++20.

Two exact source causes were proven before edit. Emitted includes load
blade_run_record.hpp at line24 before spool/windows.h at170. Its handwritten
GetModuleFileNameA(void*,...) declaration conflicts with the SDK HMODULE type
(default STRICT gives HINSTANCE__*). Installed ucrt64 libloaderapi.h directly
includes minwindef.h/minwinbase.h; minwindef defaults STRICT unless NO_STRICT
and defines HMODULE accordingly. The shared run-record header now uses that
SDK declaration instead of reproducing its ABI, supporting either include order
and either actual macro context. NOMINMAX is set before this narrow SDK header
to avoid poisoning later standard-library headers. Non-Windows unistd branch
is unchanged; full windows.h is still not included by run-record. Additional
SDK parse cost is unmeasured; no compile-time performance claim is made.

Second, std::endian in spool's plan() is C++20-only. A fixed noexcept memcpy
probe now checks uint64 0x0102030405060708 bytes and IEEE64 double1.0 bytes
against their exact little-endian encodings, retaining size8/8-bit-byte/
is_iec559 gates and unsupported status. It allocates no heap, assumes no OS
endianness and keeps format/version/CRC, quota arithmetic, ownership, exit hooks,
read/write/validation and cleanup sequences unchanged. The test-only computed
Inf replay assertion also uses memcpy into uint64 instead of C++20 bit_cast.
No Build flags, compiler admission or resource policy changed.

Intentional compatibility header revision (not accidental drift): historical
accepted D06 spool hash was
`BCDD68EB1957ECE5846F1F12AD3439BA652C8613F7A498C763346E58DDECDDB9`;
revised spool hash is
`26EA91E31EF33259C95EECE1E5391CBBDA13BD8EE3BA9694FA28F7B1E6EA0EAF`.
Historical run-record hash was
`C0DFBB46C7C990EF2CE75DFA292B70AC0247AC82AE3F73A33342A2BC0CD97F68`;
revised run-record hash is
`74DD8634B62156E13120300479CBBBC2E616A7D44CED3F35B2E213B78D5A6EDB`.
The failure-hook/runtime D06 header hash DAB93164... remains unchanged. The
focused bridge pins the revised spool/run-record and unchanged runtime hashes.

Authored compile-only fixtures are header-order-record-first.cpp and
header-order-spool-first.cpp in D08-runner-inputs. They invoke both public
interfaces in function bodies and have no main. After root actual-diff review,
sole-Luna request is one fresh Release build, then four C++17 object compiles
(both include orders, default STRICT and NO_STRICT), then a fresh bounded D06
protocol compile/run using C++17, then the focused FSI gate only if those pass.
Stop at the first failure and retain all artifacts. Root reviews focused output
before broader Zarr; no full suite/benchmark. Use existing reliable capture
wrapper, fresh short private ASCII output/TEMP/TMP and existing empty D06 root.
Exact native commands below assume Luna prepared the new output/owned roots;
no command here has been executed by the coder:

```powershell
$d08Checkout = 'C:\Users\cdupu\Documents\GitHub\Blade\.round4-worktrees\d-streaming'
$d08Compat = Join-Path $d08Checkout 'bench\.build\d8compat'
$d08Headers = Join-Path $d08Checkout 'bin\Release\net10.0\cpp'
$d08Fixtures = Join-Path $d08Checkout 'bench\notes\round4\D08-runner-inputs'
& g++ -std=c++17 -O2 -Wall -Wextra ("-I" + $d08Headers) -c (Join-Path $d08Fixtures 'header-order-record-first.cpp') -o (Join-Path $d08Compat 'rr-first.o')
& g++ -std=c++17 -O2 -Wall -Wextra ("-I" + $d08Headers) -c (Join-Path $d08Fixtures 'header-order-spool-first.cpp') -o (Join-Path $d08Compat 'spool-first.o')
& g++ -std=c++17 -O2 -Wall -Wextra -DNO_STRICT ("-I" + $d08Headers) -c (Join-Path $d08Fixtures 'header-order-record-first.cpp') -o (Join-Path $d08Compat 'rr-first-nonstrict.o')
& g++ -std=c++17 -O2 -Wall -Wextra -DNO_STRICT ("-I" + $d08Headers) -c (Join-Path $d08Fixtures 'header-order-spool-first.cpp') -o (Join-Path $d08Compat 'spool-first-nonstrict.o')
& g++ -std=c++17 -O2 -Wall -Wextra ("-I" + $d08Headers) (Join-Path $d08Checkout 'src\cpp\private_spool_tests.cpp') -o (Join-Path $d08Compat 'protocol.exe')
& (Join-Path $d08Compat 'protocol.exe') (Join-Path $d08Compat 'owned')
```

Run the focused FSI command below with a separate new short private case root
and freshly built DLL. Expected D06 is the existing256 pass/0fail; its one
privilege-dependent symlink skip is allowed and must be reported. D08 focused
requires zero skips. Coder performed no build/compile/emit/run/tests; all repair
assertions and fixtures remain unrun and uncommitted for root review.

### Compatibility gates pass; exact Float64 display-pin repair

Sole-Luna reports the fresh build, all four C++17 header-order object compiles
and fresh D06 C++17 protocol regression passed (256 passed/0 failed/1 expected
symlink-privilege skip), with empty scratch. Focused D08 then compiled and ran
its first native N5 case successfully: exit0, `result = 382.0`, payload_written40,
payload_read40, logical_disk_high_water496, aggregate_tracked_capacity6176,
reserved_disk0. The owned root is empty. This is actual numeric/protocol evidence;
the gate failed solely because its exact string pin expected `result = 382`.
Preserved raw output is
`bench/.build/d8compat/cases-12fd/D08_n5.stdout-stderr.log`, SHA256
`917FC40E95448D95DCF10FD9FF345F14196916FCC5000BF9C911531155B617A5`;
exit record SHA256
`5FECEB66FFC86F38D952786C6D696C79C2DBC239DD4E91B46729D73A27FB57E9`.
No preserved artifact was changed or rerun by the coder.

The ONLY test edit is five exact Float64 display expectations: N5 and resident
reference382.0; N1=28.0; empty=3.0; finite-fill=182.0. It does not introduce a
numeric tolerance/parser, alter values, skip cases or weaken actual-route,
counters, fault/failure-order, NaN-category or scratch-retirement checks. No
production file, bridge or compiled test-function contract changed. No resume/
skip API is added. Coder performed no execution. This test-only repair is
uncommitted and frozen for root review.

Current next validation is a fresh source-matching Release build for the changed
compiled test pins, then the unchanged focused bridge/gate in a new short
private case root when root dispatches it. The already successful header-order
and D06 compatibility gates need not be repeated for this matcher-only edit.
Root may count the preserved N5 numeric/counter evidence while choosing its
bounded retry; the authored gate itself retains its complete original sequence
and will rerun N5 on a normal invocation. No implicit prefix reuse is claimed.

## Change and admission

New `Types.PrivateSpoolPolicy` holds explicit Root, TrackedWorkspaceBytes,
LogicalDiskBytes and BlockCells. `CodeGen.genSelfContainedProgramFromIRWithOptions`
accepts `{PrivateSpool=Some policy}` and deliberately binds that policy to the
artifact. Existing signature delegates with None; generic module/split/provider
APIs and default contexts have no disk authority. No CLI, environment backdoor,
launch token, runtime human reauthorization, default disk strategy or source
snapshot. Malformed policy throws a clear options argument error. Unsupported
graphs/modes retain their original resident schedule; that does NOT satisfy an
enforced global memory bound, which this optional capability does not implement.

`OptimizePrivateSpool.fs` contains one final-IR certificate. Exactly one module,
one source and one adjacent computed rank-one unitless F64 producer -> literal-
seeded addition -> sum/Float64(extent) -> literal-seeded transformed addition.
One immutable scalar capture m; its source IsMutable flag is permitted only
because the whole-module expression/type/side-root walk proves no assignments
or explicit lifetime nodes. Closed scalar elementwise F64 +/-/*// and negation
only, no calls/indexes/casts/effects in either cell body. Exact callable arrows,
params/ret/variables/capture types, static/arity/parallel metadata, full
IRMethodFor provenance/operands/types/identities, loop metadata and exact input/binding/output-role
provenance with the copy-and-fresh-ID output rule are checked. Every private external value/function/type/extent/
capture/side/source/name demand refuses. Multiple modules are deliberately
declined, supplying explicit whole-program evidence rather than an incomplete
foreign-reference assumption. Print selection must name only result, and any
terminal observer beyond that final print (binding/callable/capture/type or
side root) refuses this first route to avoid hoist/lifetime changes.

Existing pool/reuse-return donor KEYS AND VALUES/current donor, hot-row,
cell-fold/rec-step, epilogue, membership, compaction/compact-row/orbit and tile
owners refuse. A fresh immutable plan reaches module context explicitly. The
producer binding must be the certified object, graph/callables and current
print/FP/owners must still match, and the full region string preflight completes
before committing omission. Only the exact three later certified binding
objects are skipped. No mutable ambient grant or ID-only stale authorization.
CodeGen's actual derived-origin emission binding order is checked separately:
the source must precede the same four consecutive certified binding objects.
A reordered emission declines before any omission. Split-timing mode is also
declined, including an explicit result-only target, retaining original timing
placement. Assertions pin both boundaries and terminal callable/alias refusals.

The new emitter requires serial/repro AND explicit contraction off. It renders
the closed arithmetic with the existing scalar renderer; producer cells occupy
F64 caller-window slots before both serial first-fold use and append. Replay
uses exactly those stored bits, with each seed and ascending chain preserved.
No online variance, reassociation, partial sums, provider reread or FP identity
claim under contraction fast. Named arbitrary helpers and wider kernels remain
outside admission.

## Provider order, storage and publication

The Zarr seam is `CppZarr.genPrivateSpoolRows`, a narrow exact-capacity F64
entry to the SAME `genStreamWindowCore None`, with a borrowed decode pointer.
All existing window/terminal callers keep their old owned-buffer/tally choices.
No generic ProviderRegistry callback changed. `rank1TerminalChunk` supplies the
local dense f8/identity-codec v2/v3 metadata capability, but its WHOLE-READ tally
reader is never used for this region. Original source prologue remains at A.

Cursor uses lo=next, step=min(C,N-lo), hi=lo+step and next=hi. Normal windows
assemble/validate before producer arithmetic; normal global missing coordinates
and within-/between-window failure order remain. No static boundary table,
ghost cache, prefetch, catalog or immutable source contract. Once append fails,
latch its first status, disable further producer/fold/spool work and drain the
remaining original provider windows with retained input/decode buffers. A later
provider exit/panic stays primary; D06's registered std::exit and failure-exit
hooks clean the persistent spool even without C++ unwinding. Caller heap buffers
are reclaimed by process teardown on non-unwinding exit. If the source succeeds,
report the first append failure after an explicit owned-token cancel.

Writer -> sealed -> reader moves use the accepted D06 primitive unchanged.
Seal precedes the scalar barrier; second accumulation stays private. Extra zero-
count replay read validates footer/EOF, then reader finish retires scratch,
then the IIFE returns the pending value to initialize result. Every protocol/
cleanup error prevents that return/publication. New BL8016 identifies private
intermediate resource/protocol failures, with generated source-span panic call.
The error path cancels the current move token BEFORE reporting secondary status,
marks unattempted setup cleanup distinctly and preserves the primary error.
Post-success final-close failure can report an error with no remaining scratch;
it is not described as a retained manifest. Exit hooks remain the backstop when
cancel cannot retire ownership. D06 ownership/recovery/known-file deletion and
no-header-panic protections are unchanged.

## Tracked resources and limits

Preallocation gate subtracts 16*C plus sizeof(region state/Grant/Plan)+512 fixed
reserve from the explicit tracked allowance; D06 then checks its 8*C serialized
buffer plus sizeof(Control)+4096 and complete logical disk reservation BEFORE
input/decode/protocol allocation. The checked local capability bounds N/chunk
arithmetic; B=C<=1048576, cursor advances positively without N+C-1. Input slots
are reused in place for p and replay. Logical p storage 8*N is removed; result
is still the required scalar. N0 retains both explicit seeds, evaluates the
original floating barrier (5/0), and returns second seed3 after empty replay.

Tracked capacity is bounded by 24*C plus the listed fixed state/reserves,
independent of N. This allowance excludes untracked stdlib stream internals,
allocator/kernel/filesystem metadata and page cache, and is NOT a whole-process
RSS or hard global memory claim. D06 at most four native handles are additional
to provider/process handles. Serialized disk is 72+8*N+64*G+64+128, G=N/C+(N%C!=0),
excluding filesystem allocation metadata; no physical disk reservation or
aggregate shared-root quota. Compiler-side Zarr storeFiles/fingerprints still
enumerate files. No measured speed/RSS gain is claimed; tiny/chunk-dominated
cases can have larger fixed overhead than resident execution.

## Exact modified files

- Blade.fsproj: dependency-ordered certificate/emitter/test entries.
- src/Types.fs: policy and read-only return-donor census API.
- src/OptimizePrivateSpool.fs: complete narrow final-IR certificate.
- src/CodeGenState.fs: optional emission options and explicit region context.
- src/CodeGenPrivateSpool.fs: preflight, retained-window emitter and error ownership.
- src/CodeGen.fs: options-bearing whole-program wrapper and exact region commitment.
- src/CodeGenLoopNest.fs: register the unchanged private-spool header for shared deployment/cache/cleanup.
- src/providers/ZarrProvider.fs: borrowed decode storage in same normal reader core.
- src/Diagnostics.fs: BL8016 catalog entry.
- src/cpp/blade_private_spool.hpp: C++17 byte-representation compatibility probe.
- src/cpp/blade_run_record.hpp: official Win32 SDK declaration for either include order.
- tests/PrivateSpoolTests.fs: focused positive/refusal/resource/fault assertions.
- tests/ZarrTests.fs: invoke the focused gate from standalone Zarr.
- bench/notes/round4/D08-runner-inputs/run-focused.fsx, diagnose-admission.fsx and both header-order-*.cpp fixtures,
  D08-source-hashes.txt and this handoff.

Original D08 topology bundle/design remains unchanged. No other lane imported.
All focused assertions are authored and UNRUN. Root final review is required.

## Sole-Luna request after root dispatch

Use the frozen source/hash manifest and new private ASCII case/output roots.
The compatibility gates above are already complete. For the current matcher-only
repair, build Release once for the changed compiled pins, stop on build failure,
then use that fresh build for ONLY the focused adapter below; it calls the compiled
public test function and contains the known options bridge. Luna must not
invent a policy or fault adapter or run the entire Zarr category first:

Use Luna's established capture wrapper for build and process provenance. After
a successful source-matching Release build, the exact focused command is:

```powershell
$d08Checkout = 'C:\Users\cdupu\Documents\GitHub\Blade\.round4-worktrees\d-streaming'
$d08Dll = Join-Path $d08Checkout 'bin\Release\net10.0\Blade.dll'
$d08Probe = Join-Path $d08Checkout 'bench\notes\round4\D08-runner-inputs\run-focused.fsx'
$d08Cases = Join-Path $d08Checkout 'bench\.build\D08-runner\focused-cases'
& dotnet fsi ("--reference:" + $d08Dll) --exec $d08Probe $d08Cases
```

Stop after build/focused D08 and return raw emitted C++/logs/counters and all
refusals for root review. Only after that review should root authorize broader
standalone Zarr validation of other normal-window callers. That category also
contains the focused gate; do not automatically run it in this first request.
No default/full suite or benchmark.

The compiled gate creates exactly its new private directory, explicit owned
root and tiny source stores. It compiles/runs at most sixteen small native
programs with existing hidden-process/timeout helpers, never exhausts real disk,
and retains all .cpp/exe/input evidence. Its first unexpected native build/run/
verdict OR scratch-retirement failure stops the focused block. It checks N5=382,
N1=28, empty=3 and finite-fill=182, plus finite provider values overflowing to
positive-Inf producer cells (exact replay bits) and final NaN category, never a
new provider-missing refusal. It checks exact payload/disk counters after retirement; actual no-p-pool,
no-table, one-reader-site admission; capture/type/loop/metadata/provenance/
foreign/payload/assignment/owner/stale refusals; None/default-print/contractfast/
nonrepro/chunk mismatch fallback; generic API provenance; malformed policy;
latched append vs later short/null/missing diagnostics and earlier missing vs
future short; source-valid append/seal/footer/final-close failures with no
published result; aggregate workspace and serialized-disk quota denial before
allocations. Test-only C++ fault macro uses existing D06 seams, no production
configuration backdoor. Each native case requires empty owned root afterwards.
Each compilation/execution also writes its own result/error, merged raw stdout/
stderr and exit record beside the generated C++. Positive cases print the
observed payload/disk/aggregate tracked-capacity counters into those raw logs.

Expected focused skips on supported native Windows with g++: zero. Unsupported
platform or missing g++ skips cannot satisfy acceptance. Standalone Zarr retains
its existing optional internal NetCDF differential skip; report that suffix
separately. No extra symlink protocol probe is requested. Do not rerun D06's
accepted native harness beyond the fresh C++17 compatibility regression requested above.
