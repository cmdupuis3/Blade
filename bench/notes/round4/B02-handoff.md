# B02 handoff: PM54 page-touch/release diagnostic

Implemented, unexecuted, frozen for root review and sole Luna runner, 2026-10-22.
Checkout `C:/Users/cdupu/Documents/GitHub/Blade/.round4-worktrees/b-measurement`, branch
`codex/round4-b-measurement`, base `3bec8a80` (B01; integrated counterpart `38c41096`).
No commit, integration, build, test, native API execution, benchmark or oracle run by coder.

## Census and owned diff

Graft's file API was queried first and has no indexed definitions for run.py. Direct
inspection found `run_proc` already collects Win32 child lifetime peaks/faults, and B01's
Sample/summarize_memory/render_memory_report already supply distributions and coverage.
The PM54 planning contract was read from the integration worktree and original sketch.
No existing page reservation/touch/release diagnostic was found in the harness census.

One diagnostic control, three distinct workloads, no equivalent-work arms. The approved
design uses Win32 VirtualAlloc in a fresh Python child per workload; no compiler/runtime
source or allocator policy changes, no new benchmark kernel/source/pins.

Changed/added files:

- `bench/harness/run.py`: small `diagnose-memory` dispatch, diagnostic report annotation,
  harness version 2.2. Existing benchmark parsing/statistics remain unchanged.
- `bench/harness/pm54.py`: portable geometry, protocol, sidecar validation, report and
  orchestration through existing parent run_proc / Sample / memory reporting.
- `bench/harness/pm54_win32.py`: native child only; VirtualAlloc, GetSystemInfo,
  GetProcessMemoryInfo, byte writes/read-back and VirtualFree.
- `bench/harness/test_pm54.py`: eleven pure fixture tests; no native calls or real child.
- `bench/diagnostics/pm54/spec.md`: independently specified task, read/write coverage,
  stage/release/output contract, counter provenance and limitations.
- `bench/harness/README.md` and this handoff.

## Contract and boundaries

`PM54-page-touch-release-v1`: actual page size P and address granularity from GetSystemInfo;
checked SIZE_T rounding R=ceil(N/P)*P. Default N=16,000,003; requested ceiling 256,000,003.

| workload | commit request | deliberately stored/read bytes | dense completion |
|---|---:|---:|---|
| reserve-only | 0 | 0; never accesses region | false |
| sparse | R | one byte at 0,17P,34P,... below N | false |
| dense | R | every requested byte, N stores AND N checksum reads | true only after read-back |

Each assigned byte is 1; checksum is its observed-byte sum. Distinct deliberately touched
pages and byte-store counts are separate, not DRAM traffic. Rounded tail is never read.
All children attempt release of the original reservation base via
`VirtualFree(base,0,MEM_RELEASE)` in a finally boundary. Work success/dense completion and
explicit release attempt/success are separate; release failure invalidates the diagnostic.

Stages baseline/reserved/committed/touched/released contain CURRENT WorkingSetSize and
PagefileUsage, lifetime PeakWorkingSetSize/PeakPagefileUsage and cumulative PageFaultCount.
Reserve-only's committed/touched stages are explicitly skipped. Touched follows both
stores and checksum read-back; released follows the explicit release attempt. Missing
native queries produce null/unmeasured counters. High waters and fault totals never
reset after release; current resident is NOT required to decline.

Independent parent process peaks stay separate from child stage snapshots. The parent
does not fill missing process counters from available stage counters. Whole-process
observations include startup, Python, metadata/read-back/JSON and teardown. Sidecar
serialization follows the last snapshot, so the parent can observe a higher lifetime
peak. Stage snapshots can miss an intervening spike. No allocation-live/retained-byte,
DRAM, equivalent-work, bandwidth or performance claim is made.

Parent session schema 2 + diagnostic_schema 1 + memory-report schema 1 are additive.
`session.json` retains exact absolute child argv, source_root + source hashes, Python
path/version/hash, raw diagnostics, independent parent samples and validation. Each
workload gets a fresh absolute child/report directory; all output/script/executable
paths passed to the child are resolved absolute. `memory.md` reuses B01 process
distributions; `diagnostic.md` contains snapshots and workload/release coverage. Normal
three-line benchmark stdout/checksum contract and timing ratio pipeline are untouched.

## Frozen working-tree identities

Raw working-tree SHA-256 (not normalized Git blob hashes):

| file | SHA-256 |
|---|---|
| bench/harness/run.py | 1915F56D9323B15B6E117C66AD7071DEBF57FEFB9E19B00B9B96CC0201939FDC |
| bench/harness/pm54.py | 98AE2446941B5C23FC65879122D7F2F72676B8CE835A8D567B28CFE8516B0A00 |
| bench/harness/pm54_win32.py | 9437282CE04A92771571B0D385436F07B29D2B5F150B878A5B9E7468CEC48BBB |
| bench/harness/test_pm54.py | 0DF2155231EBCADB316075E8F8328311D272776CDF401B5DF3C118E023A410AE |
| bench/harness/README.md | D2C1D49835655821B19865E8D49A7403212CE7BEC84B9E7929E0445390E4AFDA |
| bench/diagnostics/pm54/spec.md | 18A2A7125AFC1D36FB591A8C5DCA9457B1864087A6F67E2F89076F0218E594EA |

Static-only `git diff --check` is clean. Existing tracked run.py/README retain LF in the
index / CRLF in the worktree; their diffs are small, with no whole-file newline flip.
New tests and all runtime/native paths remain **unrun**.

## Exact runner request (root approval first)

No build is needed for this bench-only diagnostic. Luna alone executes, after source
hash verification, with no simultaneous edits to this tree. These are targeted correctness
checks; no full/default suite, compilation or timing benchmark requested.

Every output path below is absolute beneath the **lane checkout**, not the original root.
Record `$b02Repo`, current branch/commit, hashes, cwd and all actual result directories;
do not use `Path.cwd()` as an implicit artifact destination.

```powershell
$b02Repo = 'C:\Users\cdupu\Documents\GitHub\Blade\.round4-worktrees\b-measurement'
$b02Scratch = Join-Path $b02Repo 'bench\.build\B02-runner'
New-Item -ItemType Directory -Force -Path $b02Scratch | Out-Null
Set-Location -LiteralPath $b02Scratch
& 'C:\Python314\python.exe' -m unittest discover -s "$b02Repo\bench\harness" -p test_pm54.py -v
& 'C:\Python314\python.exe' -m unittest discover -s "$b02Repo\bench\harness" -p test_memory.py -v
& 'C:\Python314\python.exe' "$b02Repo\bench\harness\run.py" diagnose-memory --bytes 65539 --out-dir "$b02Scratch\single-probe"
& 'C:\Python314\python.exe' "$b02Repo\bench\harness\run.py" diagnose-memory --bytes 1000003 --out-dir "$b02Scratch\multiple-probes"
```

Expected pure fixtures: eleven PM54 tests plus B01's ten tests, zero expected skips on
this Windows host. PM54 pure tests also avoid loading run.py/native Win32 APIs, so their
contract portion is portable. They cover rounding/overflow/actual page geometry, sparse
store-page distinction, reserve-only no commit/access/dense claim, dense/sparse read-back
before release, read-back failure cleanup, separate release failure, null counters,
monotonic peaks with increasing current footprint after release, dishonest metadata/
checksum/stage rejection, malformed sidecars and absolute-path/missing-parent-counter
orchestration using a mock process capture.

Each native command should return 0 and report 3/3 validated diagnostic workloads.
Counter queries may be unavailable; this remains unmeasured rather than a fabricated
zero or failed work classification. Failed reserve/commit/read/release, missing sidecar,
bad geometry/checksum/stages or watchdog timeout returns 1. Invalid parent args returns 2.
Do not use footprint deltas as pass/fail thresholds and do not infer resident release
from a successful VirtualFree. Explicitly record native metric availability.

Inspect each session.json / diagnostic.md / memory.md and all three child.json files.
Dense must report N actual byte stores and checksum reads; sparse reports only its
calculated probes; reserve-only reports zero stores/reads/commit with skipped actions
and no dense completion. At 4 KiB pages the first request probes once and the second
probes fifteen times; if page size differs, use the recorded P and contract formula.
All use distinct child processes and return explicit release success. Parent process
peaks must be preserved independently, not copied from stage values. Sample.time_s must
remain null; no timing distribution, ratio, bandwidth rate or dense-equivalence score.

No default-size measurement is requested now. Quiet repeated observations/scaling and
any allocation-instrumentation control are later, separately authorized cards. This
card stops at a coherent implemented diagnostic plus runner validation after root review.
