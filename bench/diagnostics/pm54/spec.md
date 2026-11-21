# PM54 page-touch/release diagnostic, v1

Contract `PM54-page-touch-release-v1`, B02, 2026-10-22. Three **distinct workloads** in
three fresh Windows child processes. This is an OS/API diagnostic, not a Blade-vs-Fortran
benchmark kernel, equivalent-work comparison, bandwidth result or optimization claim.
No compiler/runtime source, allocator policy, benchmark input source or pin is changed.

## Requested storage and actual actions

Default requested byte extent is the non-power-of-two `16,000,003`; runner smoke is
`65,539`. The command accepts 1..256,000,003 bytes as a diagnostic ceiling. `GetSystemInfo`
supplies the actual page size **P** and address allocation granularity **G**; neither is
guessed or conflated. Checked `SIZE_T` arithmetic rounds requested extent **N** to
`R = ceil(N/P)*P` and checks sparse stride `17P`. `VirtualAlloc(NULL,R,MEM_RESERVE,...)`
reserves one region; dense/sparse then commit that same region with
`VirtualAlloc(base,R,MEM_COMMIT,PAGE_READWRITE)`. Reservation base and range are checked
before access. Reported reservation/commit lengths are the successful API-request
geometry, not whole-process native allocation capacity or a VirtualQuery census.

| workload | successful commit request | assigned byte addresses | bytes read for checksum | dense work completed |
|---|---:|---|---:|---|
| reserve-only | 0 | none; no region bytes read | 0 | false |
| sparse | R | 0, 17P, 34P, ... strictly below N | `floor((N-1)/(17P))+1` | false |
| dense | R | every address 0..N-1 | N | true only after successful full read-back |

Each assigned byte receives **1**. The independently specified checksum is the sum of
the assigned bytes actually read: 0 for reserve-only's empty observation, the probe count
for sparse, N for dense. Dense uses a native memset followed by a zero-copy byte view
read of **every** assigned byte; sparse reads only its initialized probes. Rounded tail
bytes are never part of the output/checksum. Reserve-only never reads uncommitted memory.
No untouched or partially touched arm scores as completed dense output. A successful
reservation alone is useful diagnostic work, but has no dense-work performance score.

Deliberate byte stores and **distinct deliberately touched pages** are separate fields.
Dense stores N bytes across ceil(N/P) pages; sparse stores one byte on each of its probe
pages. These count source-intended stores/reads, not hardware transfers or pages the OS
may touch incidentally. Inputs are integer byte/page geometry only; there is no array
input checksum. The normal benchmark three-line stdout contract is untouched: this verb
uses a separate structured JSON sidecar and prints no child timing line.

## Observation, release and snapshots

Five ordered stage snapshots are retained:

1. **baseline**: after loading diagnostic APIs/metadata, before reservation.
2. **reserved**: after successful MEM_RESERVE, before commitment.
3. **committed**: after successful MEM_COMMIT; explicitly skipped in reserve-only.
4. **touched**: after all intended stores **and checksum read-back**; explicitly skipped
   in reserve-only, with its empty checksum recorded separately.
5. **released**: after `VirtualFree(original_base,0,MEM_RELEASE)` was attempted.

Release is in a `finally` boundary even after commitment/read-back failure. Its attempt
and Boolean API success are recorded independently from work/checksum success. A release
failure invalidates the diagnostic even if dense read-back already completed; a timeout
or crash without a sidecar cannot establish the explicit release boundary. OS cleanup at
process exit is not substituted for an observed successful VirtualFree.

At each stage, `GetProcessMemoryInfo` records **CURRENT** `WorkingSetSize` (resident bytes)
and `PagefileUsage` (commit bytes), plus lifetime `PeakWorkingSetSize`,
`PeakPagefileUsage` and cumulative `PageFaultCount` (all faults, not only hard faults).
Failed queries leave fields **null/unmeasured**, never synthetic zero. Peaks and fault
counts must not decrease in an otherwise valid complete trace. Current counters may rise
or fall; successful release does **not** promise an immediate resident-memory decline.
A peak remaining high after release is expected high-water semantics, not retained live
allocation. Current commit is also a process counter, never an allocator ledger.

The existing harness parent `run_proc` independently captures fresh-child lifetime
resident/commit peaks and faults after exit. Those observations include Python startup,
API setup, diagnostic metadata, read-back, sidecar serialization, libraries and teardown.
They may exceed the stage-snapshot peaks because the last snapshot precedes JSON writing.
Snapshots are not continuous traces and can miss an intervening current-footprint spike.
No phase deltas are advertised as allocation capacity. No child peak is subtracted from
another process or from a stage counter.

## Outputs, validation and identity

`run.py diagnose-memory --bytes N --out-dir DIR` creates a fresh immutable session directory
under DIR. Each workload has `child.json`; parent `session.json` uses session schema 2 plus
`diagnostic_schema: 1`, the B01 memory sub-schema and independent parent raw samples.
`memory.md` reuses B01 process distributions; `diagnostic.md` reports stage current/peak
counters, action/coverage/checksum/release status. There are **no** timing distributions,
time/bootstrap ratios, normal-kernel pins, equivalent-work scores or inferred bandwidth.
Parent wall duration is watchdog/bookkeeping only, never a kernel time.

The parent independently validates integer geometry, assigned/read byte counts, checksum,
dense-completion classification, required stage order/actions, explicit release success
and cumulative/high-water monotonicity. Pure fixtures use a fake region/backend to check
those contracts, failure cleanup and rejection of dishonest dense completion; no fake
counter distribution establishes real resident/commit behavior. Native execution and
counter availability remain the sole runner's job. OS-dependent numeric footprint deltas
are observations, never brittle pass/fail thresholds.

`session.json` identifies the Python executable path/version/SHA-256 and SHA-256 of
`run.py`, `pm54.py`, `pm54_win32.py` and this spec; it retains the exact child argv, geometry,
raw sidecar and parent observations. There is no compiler or Blade lane in this control.
The runner records the frozen Git/source identity alongside its request and results.

Coverage stays limited to this one private VirtualAlloc region's requested operations
and process counters. Blade pools, retained allocator bytes, whole-program native live
capacity, allocation churn, stack/thread/provider/BLAS storage and DRAM traffic are
**unmeasured**. This fixture cannot validate a general allocator ledger or retention
policy; native byte/event instrumentation remains a later card.

Primary OS references:

- [VirtualAlloc](https://learn.microsoft.com/en-us/windows/win32/api/memoryapi/nf-memoryapi-virtualalloc):
  reservation, commitment and page rounding; committed physical pages need not be resident
  before access.
- [VirtualFree](https://learn.microsoft.com/en-us/windows/win32/api/memoryapi/nf-memoryapi-virtualfree):
  original reservation base and zero size for MEM_RELEASE.
- [SYSTEM_INFO](https://learn.microsoft.com/en-us/windows/win32/api/sysinfoapi/ns-sysinfoapi-system_info):
  separate page size and allocation-address granularity.
- [PROCESS_MEMORY_COUNTERS](https://learn.microsoft.com/en-us/windows/win32/api/psapi/ns-psapi-process_memory_counters):
  current/peak working-set and commit semantics, fault count.
