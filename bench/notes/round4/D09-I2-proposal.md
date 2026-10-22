# D09-I2: certified rank-two zip row peels without full-height input tables

Status: notes only, for root selection/review. No implementation, build,
lowering, emission, native compilation/run, test, benchmark or Git operation
was performed. The I1 production/test/hash files and all earlier artifacts
remain unchanged. Root reported I1 accepted after fresh build, focused59/0/0
(16 native cases), and standalone602/0 with one unavailable NetCDF comparison;
these results were not rerun by this lane.

## One recommended card

Elide the input full-height row-pointer tables for the observed ordinary
two-operand, scalar Float64 rank-two zip. Allow one or two distinct direct
local Zarr streams, including repeated A,A positions only when their final
peel/dedup identities certify. Start with v2, uncompressed identity f8, identical
positive N x M dense shapes, common positive leading chunk edge K, plain
two-axis co-iteration and a capture-free scalar kernel/callee closure.

Keep the existing value windows and provider calls, normal scans, source
ordering, explicit run descriptor, global output coordinates, required output
allocation and remaining cleanup. Remove only each admitted input's table
allocation, live-row installation, table deletion and now-unneeded band Array
wrapper/its private fixed two-word extent table. Count the saving as the
removed N-pointer heap tables, not a source-data copy or measured performance.
The owned output row skeleton remains.

Unary maps, mixed streamed/resident operands, scalar captures, source metadata
captures, row-valued/literal outputs and all other nonadmissions keep their safe
I1 route. They must not become new language/backend errors merely because this
optimization declines. Existing BL7004 stays authoritative for a raw streamed
array value such as the original gather03/escape04. No segment-wide mask is
restored. This is narrower than a generic rank-two window/access planner.

## Observed final sites and the actual storage

The completed I1 focus artifacts are lane bench/.build/d9i1. In positive.cpp,
the final emitted nest has this normalized shape (annotation/parenthesization
are omitted here; the full retained CPP is the authority):

    for (size_t __i0 = __blade_mpi_lo_out; __i0 < __blade_mpi_hi_out; ++__i0) {
        double* BLADE_RESTRICT A____i0 = A.data[__i0];
        double* BLADE_RESTRICT B____i0 = B.data[__i0];
        double* BLADE_RESTRICT __orow_out = out[__i0];
        for (size_t __i1 = 0; __i1 < 3; ++__i1) {
            double A____i0____i1 = A____i0[__i1];
            double B____i0____i1 = B____i0[__i1];
            __orow_out[__i1] = A____i0____i1 + 2.0 * B____i0____i1;
        }
    }

This reflects the actual final route, not merely the initial diagnostic CG
projection. The two outer row peels are the replacement sites. Their inner
scalar peels, scalar locals, output row and kernel expression need no change.
Source files are read/scanned before entering this nest for each band.

- Cuda.genApplyCombinator:3447–3481 builds the CG, routes a statement-form
  kernel through its lifted callable if needed, then derives segmentRun.
- Cuda:4009 onward sets MpiSlab for the segment, attaches any row/flat/compact
  epilogues, then selects a final renderer around4085. Segments already bypass
  BLAS, row-fold-jam and flat collapse; device/MPI and other earlier routes must
  decline I2 before omission.
- LoopNest.genLoopNestStreamedCore:1677 onward derives final currentNames,
  headers, output-row hoisting and actual scalar parameter bindings.
  Its element loop at2246–2283 handles exact repeated declarations and is the
  correct place for a certified outer-peel replacement.
- LoopNest.genElementBindingPeel:402–461 distinguishes original source rows
  from already sliced rows. restrictPeelSites:489–522 proves the intermediate
  row is overwritten by a deeper scalar peel, but does not prove the source
  Array has no other uses; it is one required ingredient, not the certificate.
- LoopNest:2288 onward overlays final scalar parameter names over outerNames;
  captures are fallback names. The certified kernel must use only these exact
  scalar parameters/local scalars and certified capture-free scalar callees.
- Cuda:4280 onward creates the windows/tables and the per-band aliases after
  each GenStreamRows call. The returned certificate must also control this
  later owner/scaffolding construction; it cannot be local only to LoopNest.

For N5/M3/K2, positive.cpp has two six-double windows, two five-pointer input
tables, two four-double decode vectors coexisting in the band, and a four-word
run table {0,2,4,5}. The required output is15 doubles plus its five-pointer
owned skeleton. Existing prologues also allocate A_fseg/B_fseg (two doubles
each) and carry fixed fiber extent/fill state; I2 leaves those existing
allocations/lifetimes unchanged. It makes no cleanup claim for those other
buffers and does not expand into a prologue cleanup card.

Only the input table term S*N*sizeof(pointer) is targeted:80 emitted heap bytes
for the two-source example on a64-bit host. Windows, decode capacity, output,
provider stream/string internals and the prologue buffers are separate retained
terms. ExplicitRuns, emitted run boundaries and compiler offset/string work
remain O(ceil(N/K)); storeFiles/metadata enumeration remains another compiler
obligation. There is no end-to-end bounded metadata, compiler-memory or RSS
claim, and no measured peak/performance claim.

## Explicit plan and exact final-emission ownership

Proposed internal API in CodeGenLoopNest.fs, after the existing rank-one
certificate definition and before the private core:

    tryCertifyRank2BandPeels :
        ApplyInfo -> LoopNestCodeGen -> checked-source/geometry evidence
        -> Result<Rank2BandPeels, string>

    tryGenLoopNestRank2Band :
        Rank2BandPeels -> streamed-map -> LoopNestCodeGen -> names -> indent
        -> Result<string list, string>

Rank2BandPeels is private and immutable, with the final CG object/kernel,
ordered binding references, exact outer and successor ElementBinding object
references for every source position, original source IDs/specs/names,
per-distinct-source window name, N/M/W and checked capacities. Also retain the
resolved capture-free callee references used by the proof. This is a local
per-emission value, not a mutable ambient flag, global ID table or cached plan.
The exact names may follow repository conventions; the contract is explicit.

The private core can take a second optional rank-two plan beside the existing
rank-one option. Existing public genLoopNest/genLoopNestStreamed and the
rank-one internal wrapper pass None for rank two, preserving their signatures
and default behavior. Do not reinterpret Rank1WindowReads as a rank-two grant.

In Cuda's final host-array renderer selection, after MpiSlab and all epilogue
attachments, form a single all-or-none candidate from the actual final CG.
Require no scheduled epilogue. Pure preflight failure selects the ordinary I1
emitter and retains every input table/alias. On success, the explicit checked
renderer returns the loop code and the same plan is carried to the later
window-owner builder. No table/alias is omitted before the final renderer has
validated and accepted that exact plan.

At entry, before rendering or mutating emission channels, validate the final
CG/kernel/binding/peel references, output name/type, source/name positions and
resolved callee identities. A stale plan returns Error with no partial render;
Cuda discards it and selects ordinary I1 emission with all tables. A mismatch
found after an invariant-checked core entry is a hard internal refusal, never
a silently generic loop with owners already omitted. Do not speculate both
renderers and keep whichever text looks suitable.

The rank-two plan's only substitution is in the non-fiber RealArray branch at
LoopNest:2269–2273 for the exact certified outer ElementBinding. Use the same
newName/currentNames/paramFinalNames and the existing identical(name,code)
dedup mechanism. Inner scalar peels remain ordinary. The certificate must
prove each parameter ultimately maps to the inner scalar name, not its
temporary outer row pointer. Validate the actual final mapping before kernel
rendering; a parameter/source-ID collision or missing successor cannot pass.

## Complete admission evidence for this pilot

1. Source capability: ProviderReadSpec is streamed local zarr, no window,
   mask/present/missing relaxation or virtual/packed layout. Use existing
   ZarrProvider.load/tryFindArray for checked metadata matching; they compile
   before CodeGenLoopNest/Cuda already. Require v2, CodecIdentity, f8, exact
   Float64 (no stripped units), two positive literal shape/chunk dimensions and
   exact spec/type agreement. No new provider registry callback is needed.
   GenStreamRows is unchanged and remains the normal None-validation core.
2. Identity and axes: bind each actual IRVar operand ID to its unique emitted
   name/spec in the current context and authoritative ProviderReads. Require
   exact input type/index provenance in its own role, not extent equality.
   For the initial zip, both source row/column records agree with the two
   SharedIndexTypes, each is rank1/S-dimension/IxKPlain/SymNone with no
   dependency. Full records, identities and all metadata must agree.
   Output has exactly these two plain axes and scalar F64 cells, with the
   canonical output identity. Never globally weaken sameIndex.
3. Apply/loop metadata: require the actual plain two-operand co-iteration
   form, matching IRMethodFor Arrays/ArrayTypes/Identities, SDimsPerArray,
   TotalSDims and shared records. Check ApplyInfo's arrays, output,
   identities, shared records, scalar input ranks, zero kernel output rank,
   empty KernelTDims, ordinary SymcomStates/TriangularLevels, speedups1 and
   Reynolds flags. Source-only provenance is not executable hidden operand
   work. Unknown/composed/outer-product/fused/halo strategies decline I2.
4. Final geometry/peels: exactly two unshifted nonparallel dense levels, with
   literal N/M bounds, ExtentDimRef0/1, no fused rank, bound dependencies,
   strict offsets or special SlotTag/VirtualKind. Every operand position has
   one exact outer RealArray rank2/component0/dim0 peel and one successor
   rank2/component1/dim1 peel feeding the same scalar parameter. All selected
   source sites must be in these pairs; there is no unaccounted third site.
   Their outer loops use this band's MpiSlab bounds. Real MPI, device, fold,
   share, threaded/Reynolds, epilogues or alternate schedule forms decline.
5. Kernel/callables: initial CG captures are empty. The final scalar grammar
   admits only exact scalar parameters, scalar literals/local lets/arithmetic
   and resolved pure capture-free scalar callees with matching reference,
   parameter, argument and return types. Audit every reachable callee body
   and its own metadata; recursive/opaque/unknown forms decline I2. No source
   ID can occur in the actual body, arguments, return, array alias, tuple,
   assignment or capture. No source Array can escape through a callee. This
   is a small closed admission, not a new general value-analysis framework.
6. Metadata and every possible render: use the existing embeddedExtentExprs/
   irTypeExtentExprs machinery plus full ApplyInfo/IRMethodFor/source/callee
   payloads. Require passive literal type extents for the pilot. All bounds,
   extent guards, output shape expressions and any renderer prerequisites
   must use certified constants/locals, never a removed source wrapper.
   I1's guard remains active even after this proof; no original source ID is
   remapped to a window and no streamed name is masked.

The observed zip output uses shared row/column IDs1/2. Unary output may use
fresh copies (deduceOutputType refreshes record IDs); unary is a deliberate
I1 fallback in this first card, not permission to compare anonymous axes by
extent. Source/named scalar captures and static IRExtent metadata also remain
valid I1 fallbacks. Their original row tables must remain when not certified.

Repeated A,A requires exact two-position proof, not just one source recipe.
Existing segmentRun dedups by emitted source name, so it reads/scans A once
and owns one window/table. Both distinct outer and inner ElementBinding
instances must certify to that same source/window. Replacement code/newName
must be identical so the existing declaration dedup leaves one row and one
scalar local, while both parameter IDs map to that scalar. Never dedup two
different source bindings merely because file/variable paths happen to match;
their original repeated reads and failure points remain.

Mixed stream/resident zip conservatively declines I2. It is a concrete safe
fallback witness: only streamed inputs retain their band table; resident B
keeps its real array and legal global gathers/returns. Likewise row-valued
literal output (the executed I1 literal case), fresh array-returning helpers,
and fiber parameters retain their existing routes. A row-valued return is not
in itself a stream-value leak. Raw original B escapes still fail BL7004 under
I1; optimization nonadmission never replaces that guard with an unsafe alias.

## Address and capacity proof

Keep the original ExplicitRuns and clamps. For the pilot, cMin=0/cMax=N,
rLo=rHi=start=0, and each raw run is a positive interval [lo,hi) within [0,N].
Require the original descriptor agrees with the checked physical K grid; do
not create a second boundary catalog. W is the same retained maximum band
height, min(K,N), checked against all existing offsets.

GenStreamRows reads [lo,hi) x [0,M) into the same window. Its actual trailing
origin is0 and window row stride M; physical chunk columns need not divide M.
The new outer peel is conceptually:

    double* BLADE_RESTRICT A____i0 =
        out__win0
        + static_cast<size_t>(static_cast<int64_t>(__i0)
                              - static_cast<int64_t>(__w_lo)) * M;

N/M/K and every shape/chunk product are first checked with bigint. N/M and
chunk dimensions must fit the provider's actual int narrowing; padded
chunkCells*diskBytes must fit its int chunkBytes calculation. Check W*M*8,
N*M*8 and retained output skeleton N*sizeof(pointer) against the actual
size_t/ptrdiff/allocation limits before narrowing. The plan's exact checked
capacity must equal the existing window allocation; no unchecked new
maxWin*trailing result can serve as admission evidence.

For every executed row, lo<=i<hi, so 0<=i-lo<hi-lo<=W. Because M>0, the row
start is within the allocation and every scalar offset (i-lo)*M+j is below
W*M for 0<=j<M. Signed subtraction is representable under the checked
endpoints; its nonnegative result is narrowed only afterwards. No
win-lo*M, rows-lo, pointer-before-allocation, undersized global table or
one-past row is formed. Keep global i/j for output and provider diagnostics.
N=0/M=0, bad descriptors/capacity or unsupported metadata decline I2 without
broadening zero-extent frontend syntax; enormous capacity tests are static
only, never resident-output executions.

The replacement row is nonowning and must never be registered for deallocation
or treated as an Array pool. Each original win keeps its existing new[] and
single delete[] after the complete run loop. The output allocator/layout/free
and all other source/prologue/decode owners remain unchanged.

## Validation order and bounded acceptance tests

Provider calls and scans stay byte-for-byte in their existing relative order:
A chunks then A normal scan, B chunks then B scan, then the consumer. Removing
the table installation after each scan does not move source data work or
evaluation. Same-window later chunk corruption still beats earlier NaN;
A missing still beats B corruption; earlier-window missing beats future
corruption. Global coordinates, finite fill, partial chunk geometry and
derived nonfinite outputs stay as before. OOM identity is not promised when a
table allocation is removed. No source immutability/replay/cache/prefetch
assumption or whole-read tally policy is introduced.
No arithmetic node, operation order, intermediate rounded cell or source
validation boundary is removed by the address substitution. Strict-value tests
pin repro/contraction-off; no broader FP policy or contraction licence changes.

After root approval, author one focused extension beside I1:

- Update ONLY I1 positive/terminal route assertions conditional on actual
  certification: no input row allocations/fills/deletes or source Array
  wrapper, actual local row pointer plus unchanged scalar peels, windows,
  run table and required output. Pin the complete independent15 values and
  term3945.0. Do not weaken the assertions to generic no-#error success.
- Add independent I1 fallback-table witnesses: actual mixed stream/.read
  gather/return, scalar capture, static source metadata and the row literal
  output must retain their input table(s) and values. The existing fiber
  control must retain its fiber reader. Unary remains a table-backed control.
  An accidental broad deletion must fail these structural controls.
- Repeated A,A: assert one source read/scan site, one window, no input table,
  one deduped row/scalar declaration and two correct parameter uses; independent
  values for a+2*b are3*A. Two distinct IDs of the same physical variable must
  preserve two read/scan sites and owners.
- Keep03/04 and indirect capture/alias BL7004 controls. No native launch of
  refused CPP. Fresh source-free row results must keep their valid fallback,
  not acquire a blanket array-return refusal.
- Test stale final kernel/bindings/inner peel/callee/context and changed
  metadata/axis identity/shape/stride/run capacity. Pure nonadmission retains
  I1 rows; a stale explicit renderer must produce no runnable omitted-owner
  loop. Assert exact source identity rather than shape/name substrings.
- Reuse I1's finite-fill, final nonzero-origin row, source reversal and six
  fault-order checks; add N1/K>N small geometry if representable without new
  syntax. Derived F64 overflow from finite input remains a computed result,
  never a new provider-missing rejection. No new reassociation or output scan.
- Inspect hand examples N5/M3/K2 -> bands[0,2),[2,4),[4,5), row4 address win+0;
  N7/M3/K3 -> [0,3),[3,6),[6,7); N1/M3/K2 -> [0,1).

Only CodeGenCuda.fs, CodeGenLoopNest.fs, tests/ZarrTests.fs and checkpoint
notes/bridge should be needed. No runtime/provider/Types/SegmentTable/planner
framework change is proposed. Root already corrected CodeGenState's stale
segment-mask explanatory comment in integration; leave the frozen lane file
untouched and do not reintroduce that old prose during integration.

## Source/artifact identities read for this proposal

- Lane Cuda: 1C165A57E4D3D936F4DB32094028120104A8F5C011BA5D40C7600A3922164269
- LoopNest: 88BCA6E982028F4DABDCD086C28E2614E3109B9B715C2B71B45E07F100701D1A
- IRStorage: A656B1E1543D6868AE1AFAFA198C63B7F814C8A07EF85E9DFDD1B9E9FD7C802C
- Frozen lane State: 1581C1ED971FA5E7B5BC410E7EF6052DB451916142F6BD4375F10E5C08E9762F
- ZarrProvider: 2A6DD854966437E9C059A884C1F76947D3D748720BAB4387B77737D1DF6AF181
- Frozen ZarrTests: 080EFCC0D1DBC39E6DBC646D65CF50D4A87AB80AC0109DB52A9D51894E50200E
- Executed I1 positive CPP: 3399630FDA9C2A28A50EE40D8D4E304DA06D1DFC01E28E6DF1CD27413BA29B12
- Executed I1 literal CPP: C0DD40C219BBB606595DC19B31E00ABF816EF9E2A64F068A77D34FDE6DAC63C0
- Executed I1 metadata CPP: 24B017E5E80928D1B8D289D4814820F2A957537FE914A24C044B6C55A6672DE7

Graft had no F# coverage for genLoopNestStreamedCore; targeted reads supplied
the source anchors. This checkpoint ends at the proposal; no implementation
or further gate is authorized until root reviews it.
