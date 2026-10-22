# D08 — one statistical-barrier topology probe and proposed API seam

Design/input authorship only at source
`f9abed3bd9f6042656a750f2ccbb8ea6c963098a`. D07 is accepted census; D06 is
accepted runtime protocol, not compiler admission. No source/test production
changes, build, lower, emission, compilation, program execution or benchmark
was performed by this author. Own only this note and D08-census-inputs/.
Root reviews the probe before sole-Luna execution; implementation waits for
observed typed topology. The user has authorized the round; this is not a
request for new human permission.

Graft found no relevant F# definitions among its 68 indexed files; its weak
hits concerned Icechunk Python fixtures and protocol types. Targeted reads
therefore supplied the compiler/protocol anchors. No catalog of other kernels
or general disk strategy is proposed.

## Exact authored input, not an observed topology

`D08-census-inputs/01-statistic-barrier.blade` is the single D07 idiom, with the
producer apply parenthesized to make the materialization boundary explicit:

```blade
import zarr as z
let s = z.load("fixture.zarr")
let A = s.vars.A |> z.stream
let p = (method_for(A) <@> lambda(x) -> x * x + 1.0) |> compute
let t = reduce(p, (+), 5.0)
let m = t / Float64(extents(p))
let result = reduce(method_for(p) <@> lambda(v) -> (v - m) * (v - m), (+), 3.0)
```

Select only result. Input is Zarr v2, N=5, C=2, uncompressed little-endian f8,
axis x, finite fill zero. The three authored hexadecimal payloads decode to
chunks [1,2], [3,4], [5,0]; the final zero is padded, outside the logical shape.
There are 48 payload bytes, three 16-byte files. JSON/hex are text inputs;
only Luna's adapter materializes binary chunks under a new private output
directory. It never edits a checked-in source fixture or deletes old evidence.

Hand-derived values: p=[2,5,10,17,26], t=5+60=65, m=13; second-pass values
[121,64,9,16,169], result=3+379=382. Expected ordinary source windows are [0,2),
[2,4), [4,5). These are independent arithmetic/geometry expectations; this
probe does not evaluate the program or establish runtime values.

Hypothesis to verify, not a recognizer already implemented:

| Site | Canonical IR question |
| --- | --- |
| p | Is the binding `IRCompute(IRApplyCombinator ...)`, with one A operand, scalar F64 kernel and dense rank-one F64 result? |
| t | Is it `IRReduce(IRVar p, add64, Some 5.0)` reading stored p, or has normalization selected another form? |
| m | What is the exact extent/cast/division tree? Is p's extent already constant, or does it remain an explicit typed metadata dependency? |
| result | Is it `IRReduceCompute(IRApplyCombinator ..., add64, 3.0)` with one p operand? How is m represented in the final lifted callable signature/capture arguments? |

Record actual positions/IDs, type/axis identities, all callable bodies,
parameters/captures, complete binding side operands and scheduling decisions.
Do not infer these from generated names. If the exact source refuses or the
shape differs, stop and return that evidence; no alternate source spellings,
synthetic replacement IR or speculative admission broadening in this probe.

## Minimal sole-Luna probe request — pending root dispatch

There is no CLI IR-dump verb. The new `probe.fsx` follows the existing
C02-census-inputs/probe-ir.fsx adapter, using current public
`Lowering.lowerCaptured`, `IRValidate.validateIR`, IR printers and the ordinary
whole-program C++ emitter. It prepares only the fixed tiny store, lowers once,
validates once and emits once to baseline.cpp. It prints diagnostics, decisions,
source/type/reference facts and the exact compiler assembly path. A nonzero
status stops the census. No backend compilation, interpreter, tests, source
program evaluation, runtime header deployment, runtime spool or benchmark.

Use an **already-built** DLL whose recorded provenance matches f9abed3b, or a
root-approved source-identical combined integration build. Do not assume an
older D06 lane DLL represents the merged source. If no such binary is
available, report that gap and stop; this request does not authorize a build.
Record DLL/executable hashes and source identity before the probe. Exactly one
FSI invocation in Luna's serialized queue is sufficient:

```powershell
$d08Checkout = 'C:\Users\cdupu\Documents\GitHub\Blade\.round4-worktrees\d-streaming'
$d08Bundle = Join-Path $d08Checkout 'bench\notes\round4\D08-census-inputs'
$d08Dll = Join-Path $d08Checkout 'bin\Release\net10.0\Blade.dll'
$d08OutParent = Join-Path $d08Checkout 'bench\.build\D08-census'
$d08Case = Join-Path $d08OutParent 'n5-c2'
New-Item -ItemType Directory -Force -Path $d08OutParent | Out-Null
Get-FileHash -LiteralPath $d08Dll -Algorithm SHA256 |
    Format-List | Out-File -Encoding utf8 (Join-Path $d08OutParent 'assembly-hash.txt')
& dotnet fsi ("--reference:" + $d08Dll) --exec (Join-Path $d08Bundle 'probe.fsx') $d08Bundle $d08Case *> (Join-Path $d08OutParent 'probe.log')
$d08Exit = $LASTEXITCODE
[string]$d08Exit | Out-File -Encoding utf8 (Join-Path $d08OutParent 'probe.exit.txt')
```

The adapter requires n5-c2 not to exist and never overwrites it; a repeat needs
a new root-approved case/log name, not deletion of the first result. Environment
and process working directory are restored with finally. Pins: REPRO=1,
FP_REASSOC unset, FP_CONTRACT=off, BLAS/LLVM off, PRINT=result, FUSION on and
TILE_CACHE unset. Print relevant inherited optimizer gates in provenance too;
do not quietly toggle schedules just to obtain a desired graph.

Return to root: (1) exact lowered p/t/m/result excerpts and all referenced
specialized callable definitions; (2) p's allocation/fill plus both original
fold read sites in baseline.cpp; (3) source-open/probe placement, converted
window/decode allocations, boundary table and normal window validation/read
order; (4) existing schedule owners/decisions and any refusals/warnings.
Highlight whether p is still stored and result is the only printed binding.
This baseline MUST NOT contain an admitted private-spool route. Literal marker
counts or IR syntax traversal counts are not execution evidence. If FSI or
emission fails, retain the full first diagnostic and stop; do not rebuild or
invent new tests. No category/full-suite gate or skip claim belongs here.

## Concrete smallest options-bearing API (proposed only)

Place the policy value in `Types.fs`, before IR/Optimize; it contains no IR
types. Put emission options in CodeGenState, after Optimize and before CodeGen.
`Blade.fsproj:49,132,145,390` confirms dependency direction. Proposed signatures:

```fsharp
// Blade.Types; explicit policy bound to the deliberately generated artifact.
type PrivateSpoolPolicy = {
    Root: string
    TrackedWorkspaceBytes: uint64
    LogicalDiskBytes: uint64
    BlockCells: uint64
}

// Blade.CodeGenState
type ProgramEmissionOptions = {
    PrivateSpool: PrivateSpoolPolicy option
}

// Blade.CodeGen
genSelfContainedProgramFromIRWithOptions
    (options: ProgramEmissionOptions) (program: IRProgram) (testName: string)
    : string * string list
```

Existing `genSelfContainedProgramFromIR program name` delegates with None and
keeps resident behavior. No existing caller, public genModule/genModuleSplit,
generic provider API or default context acquires policy. The options-bearing
whole-program call explicitly embeds the supplied root/budgets/block choice;
its documented persistent behavior is that later artifact executions may use
that declared private disk storage. No repeated human consent, launch token,
CLI/language syntax, implicit environment/temp inference or default disk use.
Any future artifact-cache caller must include complete normalized policy,
schema, print/FP settings and relevant source/plan inputs in cache identity.
This API-first checkpoint need not alter CLI/cache code: current CLI calls the
old None API, and a future caller cannot claim cache support without that key.

Validate root/path bounds, limits and block choice without accessing source
data; runtime still validates the actual Windows-local-ASCII root/handles.
Compiler derives N, axis descriptor identity and exact F64 format. First kernel
requires BlockCells=C; a different policy block cap declines the disk route,
leaving the resident program. Malformed explicit policy should produce a clear
policy error; valid policy with an unsupported graph produces a recorded
resident-route decline. A runtime resource failure after an admitted omission
is an error, not automatic resident retry. No hard RSS/physical disk quota is
claimed by these field names.

This is an optional private-spool capability, not an enforced whole-program
memory budget. A resident fallback may exceed the tracked spool allowance and
does not claim to satisfy a global bound. An enforced-bound request is a
different, unimplemented contract and must refuse rather than silently use
resident fallback. Policy root text must use the existing safe C++ literal
encoder; serialize exact normalized allowances into artifact/cache identity.

## One final-IR region plan and one emitter

After the probe confirms topology, add exactly one pure Optimize entry, for
example `tryPlanPrivateStatisticSpool policy printDemand program`. It takes the
final normalized IRProgram and returns one immutable region plan or decline;
it is not a table of generic external operators. Call it from the explicit
whole-program assembly entry after `beginProgramAssembly` and before the first
binding is emitted. Existing Lowering schedules are already established; do
not independently rewrite lowering or give the interpreter disk behavior.
Optimize precedes ProviderRegistry/ZarrProvider in compile order
(`Blade.fsproj:145,151,153`), so it cannot call their metadata capability.
Keep the structural proof in Optimize; have the later whole-program assembly
obtain/validate the narrow Zarr descriptor and pass its checked N/C/F64 facts
into the final plan/preflight. Bind those facts to the exact source spec and
IR certificate, never a global provider/grant cell or an assumed chunk edge.

Initially require exactly one user module with explicit whole-program evidence
(confirm actual module shape in the probe). Match ordered contiguous p/t/m/
result bindings, a single earlier local Zarr streamed source, one ordinary
plain rank-one F64 axis/extent, one producer and two literal-seeded additive
serial chains. Use actual binding positions, shared `compactionRoots`/
`moduleReferencedIds`, `bindingPayload` and name roots/capture/type/extent
demands (`Optimize.fs:1480-1515,3637-3700`). Require no other source traversal
or p consumer, alias/escape, mutation, provider write, RNG, packed shape or
required intermediate print. PRINT=result makes this private; default print
or requested p/t/m keeps the original resident graph.

Conflict refusal includes existing pool reuse/lifetime, hot-row/cell-fold,
epilogue, membership, compaction/compact-row, orbit and tile owners of any
region node. Check actual tables, not only decision logs. Do not clear another
plan to make this route fit. All kernels/seeds/barrier must pass a closed
purity/totality/capture audit with final resolved arguments; only the expected
immutable m dependency is allowed in the replay transform. Preserve scalar
rendering, FP policy/rounding and any renderer prelude. No parallel/Reynolds,
tuple/unseeded fold, unknown helper, hidden array dependency or general scope
escape. Unsupported or reassociated mode keeps resident execution.

Plan fields cover source/region IDs and payload/type/callable identity,
positions, N/C/B, scalar seeds/barrier/capture substitution, print/FP/policy,
complete external demand and existing-owner facts. CodeGenContext receives
that plan explicitly with authorized producer ID; genModuleBinding requires
exact binding identity. Preflight both complete cell renderings, source reader,
capacity checks and all four binding dispositions BEFORE omitting any storage.
Any graph/kernel/owner mutation invalidates the certificate. At p's site emit
the entire contiguous region, then suppress only those exact four original
bindings; register only the committed result for later normal consumers/print.
Do not fall through a half-omitted region to the generic renderer, and do not
reuse authorization in nested bindings or later compilations.

## Tracked capacity and ordinary-reader prerequisite

Logical removed intermediate is 8*N bytes, not the required scalar output.
For this narrow pure one-source producer the validated F64 input window can be
overwritten in place with p's rounded F64 values. The first fold and append
consume those same bits. Replay reuses that caller window. Conservatively
account for 8*C caller window + 8*C padded decode + 8*C D06 serialization block,
plus D06's `sizeof(Control)+4096` and the fixed region-owned objects/metadata.
No per-block metadata or extra producer buffer. Use checked wide arithmetic
before allocation/narrowing; compare the AGGREGATE against policy, and pass
only the remainder after nonspool ownership to D06 memory_bytes. Its own plan
does not account for the provider (`blade_private_spool.hpp:139-150`). Disk
reservation remains 72+8*N+64*G+64+128, G=N/C+(N%C!=0), logical serialized bytes.

**Concrete gap:** the normal `genStreamWindowCore None` currently allocates its
own `std::vector<diskCpp>` inside each call (`ZarrProvider.fs:2170-2194`). Thus
merely calling GenStreamRows in a fault-drain loop does not establish retained,
preallocated decode capacity. The kernel needs one narrow borrowed-workspace
entry to that SAME core: known rank-one identity-codec F64, caller decode
pointer and checked cell capacity, same normal None scan arm. Existing callers
retain their owned-vector path. This is an internal reader-storage contract,
not a sink/provider semantic extension, string replacement on generated C++,
or use of D01's whole-read tally. If root does not approve that small seam,
leave the provider-backed spool admission deferred; do not claim fault drain
has reserved its storage. Exact interface can follow the observed prologue.

Allocate caller input/decode and protocol storage before consuming the first
window; checked hook/owner setup must succeed first. Count actual owned
capacities and fixed objects. Standard-library stream internals, allocator/
filesystem metadata and page cache are not covered by a tracked-buffer quota;
document that explicitly rather than presenting it as a bound on every native
allocation or RSS. Spool's at-most-four native handles are additional to the
provider handle and normal process facilities. Header path/owner/catalog
metadata stays fixed; Zarr storeFiles/fingerprints remain compiler-side O(files).

Use a local arithmetic cursor, not current elementwise ExplicitRuns
(`CodeGenCuda.fs:3663`): lo=next; step=min(C,N-lo); hi=lo+step; next=hi.
For executed iterations C>0 and N-lo>0 prove step>0 and hi<=N; no overflow-prone
N+C-1. Same source windows/global coordinates and odd tail as baseline, no
cache/prefetch/revisit/source identity assumption or O(N/C) offset catalog.

## Ordinary validation, spool faults and atomic terminal publication

Keep the original source-open/probe site and GenStreamRows window order.
Every full window read and normal missing-value scan completes before any
producer arithmetic. Within a window a later short/null chunk can precede its
missing scan; across windows an earlier invalid window prevents later reads.
Never select `genStreamRowsTerminal`'s deferred whole-read missing tally here.
For C-aligned ordinary chunks this tiny input has one chunk per window; paired
multi-chunk/same-window precedence remains a normal-reader control, not a new
geometry feature to add to this kernel.

Producer, first seed/fold and barrier are certified total: F64 nonfinite
arithmetic stays valid computed data, not a new provider validation. Update the
original serial first chain and append exact F64 bits; seal after all source
windows. An append failure latches its first status and disables further
spool work, but retained input/decode buffers drive all remaining ORIGINAL
window reads/validations. A later original provider failure stays primary;
its existing exit/panic cleanup must not be replaced by a spool diagnostic.
If the source completes, report the first latched spool failure with separate
cleanup status. Begin/setup denial before consumption is an explicit resource
error; OOM equivalence is not promised. Seal/flush/close failure occurs after
source completion and prevents barrier/replay publication. No source reread,
spool retry, skip-validation shortcut or fallback after partial consumption.

Successful seal -> evaluate m once -> unique replay reader -> original second
seed/ascending transform fold into private pending_result -> terminal zero-
count read verifies footer/EOF -> Reader.finish closes/retires scratch -> only
then declare/assign the public result. Integrity/replay/cleanup faults publish
no result; D06's post-success owner-close error may have no scratch left, and
must still be reported accurately. Atomic means this terminal's publication,
not rollback of unrelated earlier stdout. Generated calls carry Blade spans;
header panic prohibition, sidecar evidence and explicit recovery remain intact.

## Scope after evidence

This card supplies the fixture/probe and exact API/region design only. After
root reads actual topology, implementation can target one kernel with explicit
options, one Optimize certificate, one region emitter and the narrow borrowed
normal-reader workspace seam. It should not become a generic policy planner.
Tests then need independently pinned 382 and N1/empty controls, actual no-p-
allocation/no-source-reread/no-boundary-table evidence, default/printed/stale/
ownership refusals, aggregate quota boundaries, append-fault/provider drain
ordering and footer/cleanup-before-publication. D06 protocol faults already
have accepted coverage; do not rerun/expand that harness in this topology probe.
No implementation gate is authorized by this document alone.
