# D09-I1 frozen implementation handoff

Status: authored, statically reviewed and UNRUN. Root review precedes Luna's
fresh build/focused gate. No build, lowering, emission, C++ compilation/native
execution, tests, benchmark or Git commit was performed by the coding lane.
Base HEAD is 5d2bf4032bc0d4e43fb0d20ce42b7946d2827a7e; the accepted D09-I1
proposal is A7E2ECB06357222B02ABAFEEF879E12AD92901AA34378239F8D75389257F525E.

## Exact production scope

CodeGenCuda.genApplyCombinator no longer installs the segment-wide
_runAliasMask. A replacement comment states that its borrowed rank-two row
alias is for direct element peels, not a materialized full-array value.
Selected-source removal from ctx.StreamedArrays is unchanged. No windows,
input row tables, source aliases, run tables, provider calls/scans, output
allocation, bounds or cleanup were changed.

CodeGen.fs's formal-capture _captureMask and the existing streamed-value
sentinel/forwarding/settlement semantics remain unchanged. Actual original B
value reads and capture forwarding now reach the existing guard. The observed
global B(0,0) and whole B return should settle as BL7004 with #error rather than
become runnable stale-window/full-pool code. Legitimate scalar element peels
render direct names and require no value-level mask.

This card does not remove rank-two row tables, add a locality certificate,
change Optimize planning, or claim a memory/performance improvement. The
future row-table elimination still needs its independent complete certificate.

## Modified and authored files

- src/CodeGenCuda.fs: one removed mask scope and its explanatory comment.
- tests/ZarrTests.fs: focused runRank2StreamedValueBoundaryTests entry plus one
  invocation from standalone Zarr. Existing APIs perform lowering/validation,
  emission, BL7004 draining, runtime header deployment and native compile/run.
- bench/notes/round4/D09-I1-runner-inputs/run-focused.fsx: minimal reviewed
  bridge to the compiled focused test function. It records the loaded assembly
  hash/MVID and deployed header hashes, applies/restores the proved FSI
  AppContext host-directory override, and calls the gate on the large stack.
- This handoff and D09-I1-source-hashes.txt. The previously approved
  D09-I1-proposal.md is unchanged and included as a frozen design reference.

No new F# project file or production diagnostic API was introduced. All D08
and original D09 diagnostic assets/artifacts remain unchanged.

## Focused gate and evidence

The gate requires a new private ASCII directory of at most160 characters.
It retains all case files and refuses to overwrite an earlier directory.
Runtime helper processes use the existing Build.runExecutable hidden-process
and timeout behavior. With the runner's short case root, every emitted
CPP/executable is below240 characters; record actual full paths/lengths in
provenance. No new wrapper launches other categories.

Every store is exactly two F64 variables with shape5x3/chunks2x2; at most
twelve padded32-byte payload chunks per store. A=10*i+j, B=100+10*i+j.
All optimizerGates are cleared to defaults, with saved/restored FP repro,
contraction off, BLAS off, LLVM off, OMPthreads1, print and tile-cache settings.
Split timing flags are saved/disabled/restored. Environment.txt records actual
controlled values; each case's demand is set before lowering and emission and
saved in its demand.txt. Fixtures/CPP/refusal text/output/exit or compile errors are
retained. Unexpected failures stop this focused gate immediately. Toolchain
absence is a reported skip; it must not be called a completed native gate.

Authored checks, all UNRUN:

- The two-source demanded map keeps both six-double windows, both five-entry
  row tables and frees, four-entry run table and required output allocation.
  It pins scalar peels/read-scan order and all15 independent output cells,
  including final nonzero-origin row4.
- The terminal control keeps the materialized streamed producer then separate
  fold; its scalar is parsed numerically and compared to3945.0, avoiding a
  formatting-only F64 mismatch.
- Direct gather03 and whole-array escape04 must lower/validate, emit #error,
  and drain a spanned BL7004 identifying B's .stream value use. Refused CPP
  never enters the native harness.
- Explicit .read B preserves the global gather (A+100) and full-array return
  (225 cells repeating the actual15 B values), rather than blanket rejecting
  gathers or array returns.
- Legal trailing fiber indexing produces [0,10,20,30,40]. A source-free literal
  [a,b] return, pure named helper and resident-array captured helper retain
  independent values. Named/two-level streamed captures, a callable alias and
  local alias of original B must refuse.
- Static metadata uses a reviewed synthetic typed rewrite of the exact gather
  program: IRIndex(B,[0,0]) becomes Float64(IRExtent(B,0)), with source/capture
  types intact. The gate revalidates the rewritten program and checks A+5;
  metadata.rewrite.txt states that the saved metadata_base.blade is the
  pre-rewrite source, not the executed source graph.
- Repeat direct emission of an already-lowered positive object after refusal
  and a final fresh lowering/emission check sentinel/refusal state restoration.
- A finite omitted B chunk with fill-1 checks fill semantics and tail padding.
- Six separate tiny failure cases pin: same-window later short/null A chunk
  before earlier A NaN; A missing scan before B corruption; earlier A-window
  missing before future A corruption; reversed operand read priority; and
  final-window global location [4,1]. No demanded out is printed on failure.

The focused gate does not run the NetCDF differential or any other category,
and expects zero failures and zero skips on this known Windows/g++ host.
Standalone Zarr includes this gate but is a separate later root dispatch;
its existing internal NetCDF skip remains unrelated.

## Exact sole-Luna request after root source review

Verify D09-I1-source-hashes.txt, capture source HEAD/diff, dotnet/g++ identities,
fresh DLL hash/MVID, full command/environment/CWD, private TEMP/TMP, and raw
stdout/stderr/exit using the established reliable runner capture wrapper.
Do not reuse the old D09/D08 DLL: this change requires a fresh Release build.
Stop at the first unexpected build/focused verdict and return evidence for
repair. Do not start standalone Zarr or a full suite automatically.

    dotnet build Blade.fsproj -c Release

Then, from d-streaming and with a new short case directory:

    $i1Checkout = 'C:\Users\cdupu\Documents\GitHub\Blade\.round4-worktrees\d-streaming'
    $i1Dll = Join-Path $i1Checkout 'bin\Release\net10.0\Blade.dll'
    $i1Bridge = Join-Path $i1Checkout 'bench\notes\round4\D09-I1-runner-inputs\run-focused.fsx'
    $i1Cases = Join-Path $i1Checkout 'bench\.build\d9i1'
    & dotnet fsi ("--reference:" + $i1Dll) --exec $i1Bridge $i1Cases

Return build log, bridge stdout/stderr/provenance and the complete case tree.
Root should inspect actual negative directives/BL7004 records and retained
positive/terminal CPP plus numerical/fault outputs before any broader gate.
No build or test result is claimed by this freeze.
