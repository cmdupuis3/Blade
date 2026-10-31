# B07 opaque output boundary, diagnostic only

This is a benchmark-only prerequisite experiment on one fixed PM52 shape: 2053
binary64 cells, fixed R5 seeds 10000019 and 20000038, p=1.5*x+y, q=p*p,
checksum=sum(q), input_checksum=sum(x)+sum(y). Both complete p and q buffers are
mandatory stored outputs. This is **not a performance result or an ordinary Blade
benchmark route**. It does not change the compiler, runtime, harness, default flags
or B04's accepted diagnostic-only disposition.

`frozen/` distributes the four actual B04 emitted C++/LLVM inputs and their runtime
headers/shim/support, inventoried and copied from this lane's B04 runner evidence.
`frozen-manifest.json` binds their raw bytes; its identity and the four program
identities are pinned in `adapt.py`. Local `.gitattributes` preserves those bytes
on checkout. Unknown manifest, extra/missing frozen files, changed artifact, or
unrecognized wrapper structure refuses. No broad replacement changes arithmetic.
The adapter preserves the exact helper definitions and task arithmetic prefix,
removes only the original entry/timer/formatting wrapper, adds conservative C ABI
publication/cleanup, and writes a separate adaptation identity. It must never be
presented as unadapted emitted Blade. The adapter itself is runner-executed only.

`b07_abi.h` specifies a 72-byte x64 C descriptor. The task receives an all-zero
descriptor and prepares all four disjoint buffers, completed checksum values,
status and ownership. `b07_publish`, defined in the separately compiled driver,
receives scalar values by value and publishes the roots before task return.
No declaration asserts pure/readonly/nofree or cross-boundary optimizer effects.
`driver.c` takes exactly one absolute report filename and makes **one** opaque task
call between QPC t0 and t1. Payload allocation, R5 generation, p/q stores and both
completed checksums belong inside that task. This is compiler ordering; it does
not establish when writes reach DRAM. Boundary calls/descriptor work are task cost.

The task, driver and full-buffer observer must become separate native objects using
`-fno-lto` at every compile and link. No bitcode object, LTO/IPO/unity build,
cross-boundary inlining, cloning or unreviewed linker route is admitted. The LLVM
shim is a fourth native object. `task.f90` provides natural (two array assignments then checksum readback)
and expert (scalar v, p store, v*v, q store, checksum update) mirrors; they publish
through the identical ABI. Default licensed folds may reassociate, with contraction
fast; repro uses serial source association and contraction off. There is no claim
of binary identity: all-cell/scalar numeric checks use tolerance 1e-12 with unit
floor against the pinned independent B04 R5 reference. The ordinary FP graph is
unchanged; R5 integer intermediates fit their specified widths for this fixed shape.

All observation follows t1. The separate `observer.c` reads every p/q cell and
verifies x/y against an independently written R5/graph; the driver checks both
published scalars independently. The driver then changes x[0] and
y[2052], verifies stored outputs/scalars remain unchanged, and restores the exact
saved inputs. Separately it corrupts p[1026] and q[2052], requires one actual
observer mismatch for each, restores each exact saved output, and verifies all
four buffers again. The opaque observer call prevents driver-side folding of the
corrupt/check/restore sequence; native all-cell load evidence is still required.
These five observer calls and six cleanup calls are reported separately. These are post-stop diagnostic checks, not task calls or new
timed intervals. Arrays are streamed from actual buffers into JSON before release;
no full-output clone is retained by the driver. Nonfinite output becomes null and
fails numeric validation rather than producing an invalid JSON number.

A **different TESTING artifact**, requested only in the later root-selected
`prepared` stage, calls `b07_mutate_prepared` once after x/y generation and just
before the original producer. It changes x[1026] by exactly 0.125. The original
producer, consumer, checksum and input-checksum graph must follow that change.
The adapter option / Fortran macro and driver expected-mutation macro are bound to
new source/object/executable identities; canonical task code contains no callback.
It adds a costed opaque call inside that diagnostic interval. Post-t1 mutation is
not a substitute for this independent input-dependency witness. Neither artifact
is performance eligible, and the primary eight-arm stage never runs it implicitly.

Cleanup follows all observation/report-array loads. C++ uses its frozen typed
rank-one deallocation with the exact 2053 count; LLVM uses its frozen `blade_free`
header-aware aligned allocator pair; Fortran deallocates its own saved allocatables.
The C driver never frees a foreign pointer. Wrong extent, ABI, null root and root
overlap copies are refused before freeing; real cleanup clears the descriptor,
then duplicate cleanup refuses. Descriptors are trusted outputs from this single
task, not a general forged-pointer ownership registry or reentrant/concurrent API.
Fortran checks roots against its actual saved arrays as well. Partial allocation
failure returns failure (Fortran) or follows the original fatal route (C++/LLVM);
neither yields successful output. Deliberate dangling-pointer/early-free reads are
not normal negative tests. Native lifetime sanitizer evidence, if needed, requires
a separate root-approved runner request with a new instrumented identity.

Logical release does not imply immediate OS reclamation. Normal C++/LLVM pool caches
and native allocator retention may remain after cleanup; process exit handles their
usual endpoints. Payload requested is 4*2053*8=65696 bytes; it is not a measurement
of physical/live/retained capacity. No B03 capacity hook is enabled, no process RSS
is interpreted as allocation peak, and no DRAM/cache traffic is inferred. Process
commit/resident/fault counters are unmeasured here; absent metrics remain null.

`opaque_task_interval_s` includes QPC boundary/call/publication overhead; `observer_s`
also includes reference checks, post-stop mutation checks and full-array
JSON output; `cleanup_s` includes malformed-descriptor refusal probes, real release,
duplicate cleanup and descriptor checks. The post-release scalar JSON suffix and
file close are outside those intervals. These are descriptive diagnostic costs,
never net task performance or subtractable overhead estimates. No quiet/timed sweep.

`runner.py` is a staged request for sole Luna after root actual-diff review. It
creates a new absolute output root, private base/per-arm TEMP/TMP, logs exact argv,
compiler versions, native object/executable inventory/disassembly and hashes. It
executes pure fixtures, then eight canonical arms (C++/LLVM/natural/expert, each
licensed/repro), one native process per arm, with numeric/provenance checks before
advancing. Any build/launch/timeout/validation error stops; no fallback or overwrite.
No .NET build, Blade emission or compiler execution is needed: the inputs are frozen.

Numeric/provenance success leaves `compiler_enclosure_established:null` and
`diagnostic_accepted:false`. The exact executable still needs manual annotated
native review using `audit-template.json`: t0/task/t1 order, actual payload work,
two full store streams, final checksum operations/publication inside the call,
all-cell actual reads after t1, lifetime and matched cleanup. Object formats and
commands must prove the no-LTO boundary. Review notes/disassembly need absolute
paths and content hashes bound to provenance. The validator enforces identities
and required evidence fields; it cannot interpret assembly or authorize its own
claims. Missing review is unmeasured; marker/source-only proof is insufficient.
Root reviews native evidence. Even accepted diagnostic results stay
`performance_eligible:false`. See the B07 handoff for the exact staged request.
