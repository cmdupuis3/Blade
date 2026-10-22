# B05 source/design checkpoint: opaque task and output boundary

2026-10-22. **Design only. No implementation, compilation, execution or performance claim.**
Checkout `C:/Users/cdupu/Documents/GitHub/Blade/.round4-worktrees/b-measurement`, branch
`codex/round4-b-measurement`, HEAD `b02ac335`; B04 integrated as `de03addd`.
The only owned edit is this document. Frozen B04 sources, handoff and artifact identities
are unchanged. Graft returned no indexed src definitions; targeted source reads followed.

Recommendation: prove a **benchmark-only opaque task ABI** with an external driver
and a tightly checked, diagnostic-only adapter of the already emitted PM52 artifacts.
Prefer that zero-production-surface experiment before a new compiler ABI. Timestamp the
opaque task call; require it to publish both complete output buffers and finished
checksum scalars before returning. Perform full-buffer observation and cleanup afterward.
A no-argument clock call or a post-stop checksum sink alone is insufficient.

This adapted executable would be a new diagnostic identity, not the unchanged B04
binary or an ordinary Blade compile result. Its adapter, source graph and native result
must each be reviewed. General timed-harness admission remains a separate gate.

## Observed problem and existing capabilities

B04 root-reviewed exact executables establish that complete p/q stores and post-stop
all-cell observation do not enclose the scalar checksum work. Repro C++ stop is at
`bfcc`, with final arithmetic including `c18e` and `c1ee..c1fa` afterward. Repro LLVM
stop is at `1ccd`, with final additions at `1d92..1e04` afterward, including after array
observation. Prefix is `14000`. These are historical native observations, not a new run.
They remain diagnostic passes with performance eligibility false. See integration
`bench/notes/round4/B04-review.md` and the immutable per-arm root-review.md files.

The saved repro generated artifacts expose a narrow adaptation point without arithmetic
rewrites. C++ main computes its two reductions and `double input_checksum = (__v70 +
__v71);` before `auto __blade_t1 = TIME;`. LLVM main calls inputs/producer/consumer into
`%r2/%r3/%r4/%r5`, stores the checksum in `%r24`, and computes/stores final `%r61 = fadd`
into `%r62` before the stop `%r63 = call double @blade_now()`. Thus these raw source
graphs already have complete scalar arithmetic in the task prefix; the native optimizer
can sink it when only printing consumes the values. Preserve that entire prefix, rather
than reconstructing a checksum from its printed value or inventing a new sum association.
These exact names are artifact-specific manifest facts, never portable compiler APIs.

| Current source | What exists / what it does not establish |
|---|---|
| `src/CodeGen.fs:3578`, `:3603`, `:3638` | genMainWrapper emits body, a no-value-dependent stop timestamp, then timing/scalar/array printing. A source order is not a dependency forcing scalar arithmetic before stop. |
| `src/CodeGen.fs:3653`, `:3868` and `src/CodeGenState.fs:672` | Existing opt-in split timing separates setup/compute source code but uses the same clock shape. It does not solve scalar sinking or establish native enclosure. |
| `src/CodeGen.fs:2279`, `:3828`, `:3964` | Public genModule returns generated functions/statements; complete program assembly is separate. These are potential structured C++ emission ingredients, not a public native result-buffer/task ABI. Do not infer that textual names or ownership are exported. |
| `src/EmitLlvm.fs:1245`, `:8557`, `:8621` | blade_now has no checksum arguments. emitMain binds statements, calls blade_now, then emitPrints. The comment at `:8630` explicitly makes main the only external definition; ordinary Blade functions are internal. |
| `src/EmitLlvm.fs:274`, `:306` | Clock extern group has nofree/nounwind/willreturn, without a memory(none) claim. Even an effectful external call does not force unrelated register arithmetic to complete before it. B04 proves the gap in this actual toolchain. |
| `src/cpp/blade_llvm_shim.c:429..464` | blade_now uses QPC on Windows, returning a time value. It is not a full-task observation/serialization protocol. Changing this no-argument function alone cannot convey missing scalars/buffers. |
| `bench/fortran/bench_support.f90:59`, `:135`, `:143` | C-interoperable QPC declarations; source-level bench_start/stop/report and post-stop dump helpers already exist. Neither the clock nor source order alone is a portable anti-motion proof for every native arm. |
| `src/Build.fs:105`, `:1468`, `:1490` | C++ and LLVM use O3/native. LLVM compiles shim separately at O2 (`:1271`) and links its native object with program IR; optional tiles are another known native object. This is useful linkage precedent, not an observer entry API. |
| `src/Build.fs:893`, `:999`, `:1087` | C++ Build helper accepts explicit extraLinkInputs. Normal CLI compile does not expose arbitrary observer calls or an output-buffer export ABI. LLVM compileLlvmProgram has fixed shim/tiles linkage, with no arbitrary extra-object parameter. |
| `src/cpp/blade_portability.hpp:276` | BLADE_REPRO_FN keeps a repro function from reinlining and sets GCC contraction policy; it is not an all-work timing or opacity contract. Do not repurpose source `where repro` as a benchmark barrier. |
| `bench/harness/run.py:1080`, `:1341`, `:1390` | Harness compiles scalar-only printing and parses the three lines from a fresh child. It has no successful full-output observer record, ABI/lifetime proof, or checksum enclosure gate. Its whole-child memory counters cannot supply those facts. |

No inspected ordinary build/flag configuration enables LTO; the existing LLVM shim
native-object split is useful precedent. This is a source census, not a guarantee for
arbitrary inherited flags, a future build mode or a hand-linked executable. Capture the
actual commands, object formats and link inputs in every admitted case. B04's saved
version logs identify MSYS2 gfortran 16.2.0 and clang 22.1.8, x86_64 Windows GNU target.

## Alternatives and selected prerequisite

| Route | Assessment for this card |
|---|---|
| Same-TU empty/noinline sink, volatile scalar, memory-clobber inline assembly | Decline as the primary contract. noinline is not general opacity; a memory-only barrier does not consume all register arithmetic. Volatile payloads change the measured algorithm; inline assembly is toolchain-specific and changes the suite policy. |
| External stop routine, checksum/input_checksum by value plus output pointers/counts | Feasible narrow **stop-side** improvement: argument values must be supplied to that call and unknown external use makes escaped outputs observable. Timestamp must be inside that opaque routine before full scans. Does not by itself prevent pure work hoisting before the start clock; do not certify a complete interval from this alone. |
| Separately compiled native task object + external driver | **Selected.** Driver cannot inspect task body; task cannot inspect timestamps/observer. Driver brackets an opaque task call; task must return completed scalars and live array roots. Separate native objects, no LTO/IPO, conservatively effectful declarations, and exact executable audit establish the intended compiler boundary. Kernel internals remain optimized at the declared FP regime. |
| Shared DLL with C ABI/import table | Feasible stronger module-separation fallback, with loading outside the interval. Adds DLL identity/deployment, CRT/allocator and failure/cleanup concerns. Existing CUDA DLL machinery is specialized; it is not a general Blade task export. Do not expand to it before native-object route evidence fails. |
| Diagnostic-only, exact-artifact source/IR adapter | Feasible smallest first experiment: preserve the complete audited task prefix, replace only main/timer/print scaffolding with a versioned task/result ABI, compile as a separate native object. Accept only an allowlisted input hash, exact structure/identifiers and complete statement/instruction preservation checks. No broad substitutions or arbitrary program support. New adapted-source identity; not an ordinary emitted-program pass. |
| General string surgery/main rename, wrapping the existing executable, print or dump twin | Decline for admitted Blade benchmarks. A subprocess encloses startup/formatting and cannot export live roots; a main rename alone still includes auto-printing/current timing and exports no descriptors. Unchecked replacement risks changing scopes, graph or cleanup. |

GCC documents that noinline may still permit call removal and distinguishes the broader
noipa attribute. This supports avoiding noinline as the sole proof; GCC-specific noipa
may be an extra checked aid, not the portable mechanism.
[GCC function attributes](https://gcc.gnu.org/onlinedocs/gcc/Common-Attributes.html).
LLVM likewise defines noinline as an inliner restriction; memory effects constrain
different optimizations. Do not attach pure/readnone/memory(none), speculatable or
unjustified readonly/nocapture assumptions to the task, clock or observer boundary.
Do not assume the current online noipa spelling is supported by the installed clang
without a version-specific check by Luna.
[LLVM function attributes](https://llvm.org/docs/LangRef.html#function-attributes).
GCC can perform cross-language LTO, so different source languages or separate source
files alone are insufficient: compile real native objects with explicit -fno-lto,
do not combine task and driver in one compiler invocation, and link without LTO/IPO.
[GCC optimization options](https://gcc.gnu.org/onlinedocs/gcc/Optimize-Options.html).
Fortran BIND(C)/ISO_C_BINDING supplies an existing C-interoperability route; exact
layouts, by-value scalars and pointer ownership still need native witnesses.
[GNU Fortran interoperability](https://gcc.gnu.org/onlinedocs/gfortran/Interoperability-with-C.html).

## One narrow ABI/interval proposal

One serial Float64 rank-one PM52 task only: n=2053, fixed R5 x/y, p=1.5*x+y, q=p*p,
checksum=sum(q), input_checksum=sum(x)+sum(y). Retain B04's per-cell FP graph and
default-licensed versus repro/contract-off distinction. No generic user language feature,
new optimization, per-cell volatile access, cache flush, NT instruction or default hook.

Conceptual concrete ABI, **not implemented**: a C-compatible B05ResultV1 struct has
int64_t abi_version/n; double* x/y/p/q; double checksum/input_checksum; and int32_t
status/ownership. The only entries are `int b05_pm52_task(B05ResultV1* out)` and
`int b05_pm52_cleanup(B05ResultV1* out)`, with C linkage and one task object per executable.
The task returns success only after writing all complete fields; caller supplies no
payload storage. Pointers are owned by the task/runtime until matching cleanup or
declared process-exit reclamation. Export rank-one flat pools and n, never dangling
local extent-array addresses or native C++/Fortran descriptors. Require actual ABI size,
alignment/offset, scalar calling convention and nonaliasing witnesses. A cleanup entry
releases owned storage using the task's allocator after observation, or explicitly
records program-lifetime ownership if that route has no explicit release. No pointer
registry or caller-owned substitute buffers silently changes allocation/first-touch work.

The external driver, compiled separately from each task, follows this order:

1. Initialize fixed-size result/report metadata and QPC frequency outside the interval;
   allocate or touch no input/output payload. Observer initialization/library loading is
   explicit setup, not silently billed to one arm. Use a fresh process per diagnostic.
2. Read t0; call the external opaque task entry. Inside it, allocate/first-touch and store
   x/y/p/q, perform producer/consumer work and both complete checksum graphs, and publish
   the scalar **values** and live roots before returning. No timing or formatting here.
3. Read t1 immediately after task return. Final input-sum combination and any horizontal
   output-sum finalization must already have happened in the task. Loading an already
   completed result for later reporting is distinct from performing its arithmetic late.
   Optional by-value stop receipt can additionally latch scalars, but is not the sole
   start-side enclosure argument.
4. Validate returned status/ABI/count/pointers, then observe every actual p/q cell from
   those roots after t1. Compare full cells/counts independently, including the tail;
   verify required original input values as appropriate. No regeneration in place of
   buffer loads, endpoint sampling, checksum-only acceptance or alternate executable.
5. Publish verification/coverage/provenance and the captured scalar values; then run
   matching cleanup exactly once. Failure after task return still needs safe cleanup;
   partial/OOM task failure is not a successful interval. Do not transfer frees across
   CRT/allocator boundaries. Process exit ownership is a declared limitation, not a free.

This proposes **compiler/native-call enclosure**, not a CPU serialization/DRAM-completion
claim. Do not add hardware fences or describe QPC as a promise that every store reached
DRAM. Exact native call/dependency/store/return/timestamp evidence remains mandatory.
The interval includes task entry/return, allocation/first touch, generation, both output
stores, both checksums and result publication. It excludes full verification/formatting
and declared post-stop cleanup. Boundary overhead is nonzero and reported as such;
never subtract an empty-task estimate to manufacture net work or a speedup. A future
quiet calibration is separate work, not authorized by this design card.

## Precise missing Blade prerequisite and bounded next implementation

Current generated main does not return a typed result with retained roots. Its internals
cannot be recovered by an opaque linker flag. There are two honest integration routes:

**A. Bench-only diagnostic adaptation, recommended first.** Own a tiny adapter plus
explicit per-artifact manifests under bench. Input is an immutable saved or newly emitted
PM52 C++/LLVM artifact and exact runtime headers/shim identity, never a generated file
edited in place. Write new task source in a private absolute output directory; preserve
original files and record both hashes and a complete adaptation diff.

- For C++, preserve original function definitions/runtime preamble and the complete main
  task prefix from x construction through both finished scalar bindings, inside its
  existing failure-routing context. Export the four flat pools and scalar values to the
  result before return. Remove only default clock/formatting scaffolding, not reductions,
  allocations, loops, exceptions or failure checks. An exact source-aware wrapper template
  is acceptable for these hash-pinned fixtures; global regex replacement is not.
- For LLVM, preserve helper bodies, task allocas/blocks and dataflow up to the original
  stop. Replace main's signature with the explicit task signature; remove start/stop/
  print-only CFG and publish pointers `%r2..%r5` and **loaded values** from `%r24/%r62` for
  the read repro manifest. Reject task uses of removed clock values, unmatched blocks/
  PHIs/uses, missing completed scalar stores or changed FP flags/instructions. Do not
  carry old main's analysis-derived attributes blindly onto a new exported ABI: use
  conservative task/call declarations while preserving arithmetic flags. Keep the
  original shim/panic/allocation behavior in its separately compiled native object.
- Prove each default/repro manifest independently; differing raw IDs/CFGs are refusals
  until reviewed. The adapter cannot repair a missing scalar graph, change seed/extent/
  association, suppress diagnostics or guess a root. Handwritten LLVM is only an ABI
  witness; adapted emitted artifacts are labelled compiler-originated/adapted, not an
  unmodified emitted-Blade pass.
- Cleanup stays in the task object: use the exact typed C++ allocator release or LLVM
  blade_free only after rank/type/count provenance is established. Otherwise declare
  process-lifetime ownership and refuse an explicit-release claim. Fortran can expose
  BIND(C) task/cleanup with SAVE allocatable TARGET owners, one outstanding task per
  fresh process, c_loc views and matching deallocate after observation. The C driver
  never free()s foreign-owned pointers. After explicit cleanup clear descriptors/status;
  reject repeated cleanup without dereferencing freed memory. Partial/panic task failure
  retains original failure semantics, not successful output or invented cleanup.

This route changes **no production compiler/build/runtime surface**. It pays in explicit
artifact coupling, manifest upkeep, adapter validation and separate native link commands.
That is appropriate for one diagnostic proof, not an unreviewed shortcut for arbitrary
benchmark kernels or a transparent harness default.

**B. Structured opt-in emitter integration, later if justified.** For maintained ordinary
Blade benchmark admission, a narrow PM52 task-entry emission path could instead
resolve the four named dense Float64 rank-one roots and two scalar bindings from typed
IR, emit the normal optimized body without its default timer/print wrapper, and publish
those exact roots/finished scalars to the ABI before a return. Preserve current ownership
certificates; refuse an escaping local that is freed before publication, unsupported
shape/type, missing/ambiguous binding, changed graph, or inability to retain required
roots. Do not introduce arbitrary source-level extern/export semantics.

C++ public genModule may supply some structured ingredients, but full-program assembly
also installs includes/state/refusal handling. A bench host must not accidentally bypass
those checks. LLVM's private emitMain and internal function/linkage construction require
an explicit narrow emitter adapter; do not assume the C++ mechanism transfers. An
explicit bench-only native link step must supply the driver/observer object and the
same hashed runtime shim/headers, with no hidden fallback. Ordinary CLI/default emission
must remain unchanged in symbols/includes/calls/state/work; inactive mode must not even
emit an observer declaration. These source changes are **not authorized by this card**.
Production cost includes both CodeGen/EmitLlvm assembly/linkage paths, conservative
effect attributes, ownership escape handling, explicit link-input/cache identity
plumbing, inactive-mode regressions and possibly CLI/harness protocol exposure. This
is larger than a bench adapter and must not be disguised as a header-only observer.

Smallest proposed next card: route A, a **bench-owned ABI/optimizer-boundary proof** on
this one PM52 shape, before touching production emitter/build paths. Proposed owned files
under `bench/diagnostics/output-boundary/`: one C ABI header; one external clock/observer/
driver translation unit; exact-artifact adapter/manifests; gfortran task mirrors;
focused pure adapter/provenance/full-output fixtures; README/handoff. Reuse frozen B04 independent
reference through a read-only import/copy-with-identity, never modify its historical
files. Produce new source/object/executable identities for all adapted tasks.
No harness registration, production runtime include, project packaging or global flag
change. Even this next implementation needs root approval and actual-diff review.

If root wants ordinary unadapted emitted Blade first, the blocker is route B's task-entry/
retained-result adapter; it must receive bounded source authorization. Route A's proof
does not close that prerequisite or admit scalar-only benchmarks automatically.

## Targeted evidence request after an implementation is approved

No commands are launched or ready-to-run files asserted here. Sole Luna owns all future
native compilation, raw IR compilation, test/oracle generation and execution. Initial
scope stays one 2053-cell shape, serial, default licensed and repro contract-off. At most
the B04-sized four-task/eight-positive-case matrix (adapted C++/LLVM native, gfortran
natural/expert), plus explicitly approved deterministic mutation/refusal cases. No size,
thread, provider, BLAS, ifx or benchmark sweep; no full suite.

- **Object opacity/provenance:** record source/compiler/flags/native task/driver/shim/
  final executable hashes and complete link lines. Driver compilation cannot see task
  body, task compilation cannot see driver/observer implementation. Reject bitcode/LTO/
  IPO/unity builds, direct inlining/cloning through the boundary, unsupported C ABI,
  missing audit or fallback. GCC noipa is optional defence; no attribute alone is proof.
- **Native enclosure:** annotate t0, the real task call, all x/y/p/q allocations/stores,
  producer v/v*v, final checksum chains and final sx+sy, result publication, task return,
  then t1. Reject late arithmetic, pre-t0 payload work or an elided task. Do not judge by
  source order, a three-line report, noinline spelling, or .ll presence alone.
- **Anti-DCE/all-cell observation:** preserve two mandatory distinct output store streams
  and trace actual post-t1 buffer loads for all 2053 cells. Independent numeric checks,
  physical store evidence and endpoint/ABI evidence are separate gates. A right checksum
  with missing stores/reads remains failure; missing evidence is null/unmeasured.
- **Input/observer mutation witnesses:** a TESTING-only, explicitly costed diagnostic
  callback can mutate one prepared input cell before producer/checksums; p/q and both
  scalar results must follow the changed input and original-source order. Separately,
  mutate x/y only **after t1**: already published checksums and stored p/q remain the
  pre-mutation result. This catches late recomputation or illicit input/output aliasing.
  Corrupt one interior/tail p/q cell after t1 but before observation: full verification
  must fail, proving it reads the actual result rather than a regenerated/clone buffer.
  These modes are different diagnostic tasks, never headline performance arms.
- **Lifetime/failure:** output roots survive task return/stop/observation; observation
  precedes explicit cleanup or correctly declared process reclamation. OOM/partial output,
  wrong n/ABI/null/alias, early free, duplicate cleanup and missing observer success are
  refusals, not successful timing records. Native lifetime errors require the appropriate
  supported sanitizer witness after review, not deliberate undefined reads in normal runs.
- **Observer cost/coverage:** report stop-before-verification plus successful full-cell
  counts and verification/cleanup completion. Preserve process wall time/whole-process
  resident/commit/fault coverage separately (missing=null). Observation can raise those
  peaks and affect caches; no kernel-local capacity or DRAM inference. Do not subtract
  observer/process peaks or empty-call costs. No assertion that RSS falls after cleanup.
  Optional single-run observer_elapsed_s/cleanup_elapsed_s are separately scoped
  diagnostic costs, null until actually collected; neither is part of the task interval
  or a new benchmark sweep. B03 remains source-tree C++ pool API accounting, not a
  substitute for allocation coverage in emitted LLVM, provider scratch or full processes.
- **Uninstrumented route:** if a later adapter is approved, verify inactive emitter output
  has no new symbol/include/call/lock/state and unchanged relevant default source behavior.
  Admit actual generated C++ and LLVM only after their own native evidence; standalone
  witnesses do not certify compiler emission. Retain default/repro graph/association rules.

## Source and historical evidence identities

Raw working-tree SHA-256 at this census; no execution used to obtain these identities.

| file | SHA-256 |
|---|---|
| src/CodeGen.fs | 531D38D9BD44101C67EDB8D00555A8397A15CA189B1B8EA39D89F08B5E98C4AA |
| src/EmitLlvm.fs | 6A402EBF9F30687EA5ED6419A657C6B1AC41D234D0AC6A86EAAEEBBEBC3BA5C3 |
| src/Build.fs | 0A1C4FCF18BCBF9CB3B4D916BA560D30487F23D197BD5F635AA1528EA60DECD9 |
| src/CliCommands.fs | 85518EF3F172B4E3950F4382A9ACE29FA5350DA20B587C146A2DD5D117EEAF38 |
| src/cpp/blade_llvm_shim.c | 172754BE5A4E4A1976C8E0762304C90F87D79664CBE09695D71645FE05BA7888 |
| src/cpp/blade_portability.hpp | B831ADDE94B841D9BD548810E137A2E79377CDDAEA608A4D3A0462C52F0C98CE |
| bench/fortran/bench_support.f90 | 59B23204391C9B4E4F6EC5BB6D0A0870B2DB113B791D155A5F2C1ACDE3EF9760 |
| bench/harness/run.py | 1915F56D9323B15B6E117C66AD7071DEBF57FEFB9E19B00B9B96CC0201939FDC |
| bench/.build/B04-runner/blade-cpp-repro/generated/pm52_store_consume.cpp | 5F23272C53B3E4F3C6B4DFA1E1B88FAC9FD82E0983D13A76E9F3C97FEFA22D94 |
| bench/.build/B04-runner/blade-llvm-repro/generated/pm52_store_consume.ll | 6D8F5E274E5225A7C21F3D5571A7715ED71EC76289B8164FEE96A573D63EF0C5 |
| bench/.build/B04-runner/blade-cpp-repro/root-review.md | 44DE98D093BEC00E25D650278733FC7A3DC20C547E3CC096D604C279A0FAD664 |
| bench/.build/B04-runner/blade-llvm-repro/root-review.md | 1892AA077A5202955763A9BDD416758F4F20D33C75BA43F1A8A9769A2B687026 |

The review files are immutable local runner artifacts, not new committed benchmark
sources. B04 source/handoff remain historical validation identities. This card closes
only a source/design checkpoint; the observer, native ABI proof, adapter and timed
admission are not implemented or measured. Stop here for root review.
