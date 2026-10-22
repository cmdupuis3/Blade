# B07 opaque boundary implementation handoff

2026-10-22. **Source-only implementation, unexecuted; freeze for root review.**
Checkout `C:/Users/cdupu/Documents/GitHub/Blade/.round4-worktrees/b-measurement`,
branch `codex/round4-b-measurement`, HEAD `b02ac335`. No coder commit/merge,
adapter execution, preprocessing, syntax check, compilation, emission, fixture,
oracle, native run or benchmark. Only read-only source/git inspection, bench-owned
edits, raw-file copying and SHA-256 metadata inspection were performed.

## Scope and source census

This implements accepted route A from integration's exact B05-census/B05-review,
with B06's disposition retained: B03 is not whole-kernel capacity coverage.
Existing B04 source/handoff and historical native evidence remain untouched.
Ordinary emitted C++/LLVM final checksum arithmetic moved after B04's stop despite
complete stored arrays. This card therefore supplies a **separately identified,
adapted benchmark-only native task**, not a production emitter ABI or ordinary
Blade performance admission. No compiler/runtime/header/harness/kernel.json edit.

Owned files are only `bench/diagnostics/output-boundary/` and this B07 handoff.
The diagnostic contains the C ABI, driver, separately compiled observer, strict
adapter/manifest, Fortran natural/expert task, pure fixtures, staged runner,
validator, null native-audit template, README and distributable frozen inputs.
Untracked B05/B06 lane documents predate this card and are preserved.

Frozen originals were inventoried in this lane before copying:

- `bench/.build/B04-runner/blade-cpp-licensed/generated/pm52_store_consume.cpp`
- `bench/.build/B04-runner/blade-cpp-repro/generated/pm52_store_consume.cpp`
- `bench/.build/B04-runner/blade-llvm-licensed/generated/pm52_store_consume.ll`
- `bench/.build/B04-runner/blade-llvm-repro/generated/pm52_store_consume.ll`

Runtime headers come from that C++ licensed generated directory; the LLVM shim
comes from that LLVM licensed generated directory. The support module is the
unchanged `bench/fortran/bench_support.f90`. The new `frozen/` files retain their
raw working-tree byte identities. They are distributable inputs, not references
to ignored scratch required at replay time. Local `.gitattributes` disables text
normalization only for frozen evidence and its manifest. Read-only `git check-attr`
confirmed `text: unset`. All 27 frozen files are covered by the manifest (four
programs, 21 runtime headers, shim, support); original runtime policies stay intact.

The task has a fixed 2053-cell R5 shape, seeds 10000019/20000038, distinct complete
p=1.5*x+y and q=p*p store streams, checksum=sum(q), input_checksum=sum(x)+sum(y).
Original helper and arithmetic-prefix bytes, including licensed/repro flags and
checksum association graphs, remain unchanged in canonical adaptation. Only the
entry/timer/formatting wrapper is exchanged for task publication and cleanup.
Unknown raw hash/manifest/structure refuses; no broad source substitution.

## Boundary, lifetime and diagnostic contract

Separate native task, driver and observer objects; LLVM adds its frozen shim as a
fourth object. Every compilation and final link carries `-fno-lto`. No source-at-
link, bitcode/LTO/IPO/unity, effect annotation across the C ABI or production change.
Native format, no cross-boundary clone/inlining and actual execution order remain
review gates, not properties inferred merely from these flags.

The C ABI is 72 bytes on reviewed x64; static C/C++ size/offset assertions, Fortran
c_sizeof and runtime descriptor checks apply. The task starts from a zero result;
all four allocations, R5 fills, both output store streams, checksum folds and final
input-checksum add must finish before passing scalar values/roots to separately
compiled `b07_publish`, returning, and driver t1. Native evidence must confirm it.
The driver calls exactly one task between t0/t1 per native process. Pointer spans
are checked for null/alignment/overflow/overlap before observation or cleanup;
this is a trusted single-task descriptor, not a general pointer-ownership registry.

After t1, five separately compiled full-buffer observer calls check actual p/q
storage. Initial/final calls also check every x/y cell; driver independently checks
both scalars. Post-t1 x[0]/y[2052] changes must leave stored p/q/scalars unchanged;
inputs restore from saved exact values. Interior p[1026] and tail q[2052] corruptions
must each produce exactly one observer mismatch with complete counts; exact values
restore before final all-buffer verification, full JSON array reads and cleanup.
The opaque observer object prevents driver-side folding through its implementation.
All-cell native load evidence is still required. Six cleanup calls consist of four
invalid descriptor copies (extent/ABI/alias/null), one real release and one duplicate
refusal. No corrupt state is intentionally left for later lifetime checks.

C++ cleanup uses its original typed rank-one count-aware release; LLVM uses
`blade_free` for the aligned/header-aware allocation; Fortran owns/deallocates its
saved allocatables. The driver never frees these buffers. Ordinary cache retention,
allocator retention and exit reclamation remain possible; no RSS-drop assertion,
capacity/retention/DRAM measurement, B03 enablement or physical-live inference.
Requested payload 65696 bytes is a contract, not measured capacity. Process counters
are unmeasured, not zero. C++/LLVM preserve their original fatal exception/OOM route;
Fortran partial allocation failure releases what allocated and returns failure.
Failure/absent output cannot become a successful timing record. No deliberate
dangling-pointer test or sanitizer execution is included in this primary request.

`prepared` is a **separate TESTING task/identity** for a later root-selected extra
process. One opaque callback mutates x[1026] by +0.125 immediately after prepared
input fills and before the unchanged producer/checksum graph. Changed p/q and both
scalars must follow this dependency. Canonical adaptation has no callback. C++/LLVM
use `--prepared-mutation`; Fortran uses `B07_PREPARED_MUTATION`; driver uses
`B07_EXPECT_PREPARED_MUTATION=1`. Its callback cost belongs to that task interval.
It is distinct from post-t1 mutation; the primary stage never dispatches it.

Timing fields describe this diagnostic only: task interval includes boundary/call/
publication/descriptor costs, observer interval includes independent checks, five
observer calls, mutations and actual full-array JSON output; cleanup interval
includes six cleanup calls and descriptor checks. The final scalar JSON suffix/file
close follows that interval. No overhead subtraction, DRAM-completion claim, native
kernel speedup, quiet measurement or timed sweep. `performance_eligible:false` always.

## Frozen implementation identities

Raw working-tree SHA-256; `diag/` means `bench/diagnostics/output-boundary/`.
The manifest pins every frozen input; adapted source/object/executable hashes do not
exist yet and must be produced and retained only by Luna after root review.

| file | SHA-256 |
|---|---|
| diag/.gitattributes | A2F95585F511C093CEF13DDC2336F243178CE73AD6B1089AA0BC709CF91815F2 |
| diag/README.md | 53FCBC7AD723DB30C866E82BCB854E331A4707100E6596F01450D075F5D68F59 |
| diag/adapt.py | F90C7DF3CC8297A1F090144DE4873722AEE096CAD8263A7A9F5419B44879C925 |
| diag/audit-template.json | 807194E506E3CC99034E56919774E70C4CE696717445A50E6BE026943E20FDC6 |
| diag/b07_abi.h | 2F40E0594A6CBADC604C943106AD5BB55E50DAB7ED5C4E3881358BEDFF5A29FB |
| diag/driver.c | 62C836FE5893BEC974A6B4878B3DE7611A5EC2C1C064A74A435D2FC72518E97C |
| diag/observer.c | 666D3FC8F0D829BA8046A971C811C511138385EB52EB1A3BA6D3C04877294927 |
| diag/runner.py | 77E15567DC72D8FA9EADEB852AE6499271887E818326EDA077F3CD46D445143B |
| diag/task.f90 | A552D3D77463928E369D5E438B6CEC8390190D6AF6D3BAD68D451909BBFD80BC |
| diag/test_boundary.py | 01B946E51D8E8CAEBE101BB683C3CBAADD531A90D006EF69179B2EB851DF2FFF |
| diag/validate.py | B13F6936B8A006F98728C90B73950EBCF17AFE9F1C3EB610B9EE0EB69E38B832 |
| diag/frozen-manifest.json | 56B53905E7A28D96B1AE4F1A3257EA47BECE002BAA1D26F7FE970EDFD83082C9 |
| diag/frozen/cpp-licensed.cpp | 104E53156DCD3DD716D8B6D263AB6DDBE8198A49D08C32119783B7D58A570386 |
| diag/frozen/cpp-repro.cpp | 5F23272C53B3E4F3C6B4DFA1E1B88FAC9FD82E0983D13A76E9F3C97FEFA22D94 |
| diag/frozen/llvm-licensed.ll | CB893595EB117CBB59207D4C17C685C6232C39844152D98E1C194BB45354F73D |
| diag/frozen/llvm-repro.ll | 6D8F5E274E5225A7C21F3D5571A7715ED71EC76289B8164FEE96A573D63EF0C5 |
| unchanged B04 reference.py | D4207199F19F2DB8E9BEF8CCE0C8B338EDD0F7D47A582816D0FD69C508212FB0 |
| unchanged B04-handoff.md | CB91246CFFB8AA39D4A9E48F59DC7892A22778F933AC844AFFA674419B41357E |
| unchanged B05-census.md | EDF4D41477D9414EBE952040800BEF06BA6D3C162EDB756FB545E5C66910A418 |
| unchanged B06-census.md | 1D9704B3E9A4EAF5D2086F8CEDA14B339B62B8697FBF3242A013C3B9372DEE88 |

## Staged request to sole Luna, only after root review

Stage 1: execute the focused pure fixtures then at most the eight canonical arms,
each once: C++ licensed/repro, LLVM licensed/repro, natural licensed/repro, expert
licensed/repro. The runner writes generated adaptations into new private arm dirs,
separate native objects, exact commands/return codes/compiler hashes/versions,
object-format/section/disassembly evidence, executable/report hashes and provenance.
Each arm's full-cell/scalar numeric/provenance check must pass before the next arm.
No .NET build or compiler emission is needed. No full suite, ifx, threads, BLAS,
new shapes, fallback or source substitution. Toolchain absence/build error/refusal
stops with preserved logs; report actual failure/skip rather than changing an arm.

Every script/tool/source/object/report/work/log/temp path is absolute, with private
base TEMP/TMP created before tool/fixture calls and per-arm TEMP/TMP before compilation.
New output root must be under this checkout's bench/.build; existing roots refuse.
The following is an **authored request, not an observed execution**:

```powershell
& 'C:\Python314\python.exe' 'C:\Users\cdupu\Documents\GitHub\Blade\.round4-worktrees\b-measurement\bench\diagnostics\output-boundary\runner.py' `
  --stage primary `
  --out 'C:\Users\cdupu\Documents\GitHub\Blade\.round4-worktrees\b-measurement\bench\.build\B07-runner' `
  --gcc 'C:\msys64\ucrt64\bin\gcc.exe' --gxx 'C:\msys64\ucrt64\bin\g++.exe' `
  --clang 'C:\msys64\clang64\bin\clang.exe' --gfortran 'C:\msys64\ucrt64\bin\gfortran.exe' `
  --objdump 'C:\msys64\ucrt64\bin\objdump.exe'
```

Expected diagnostic cost accounting if all eight complete: eight native processes,
eight task calls/intervals, forty observer calls and forty-eight cleanup calls.
Version/fixture/adapter/validator/inventory tools are additional non-task child
processes; do not call the complete request "eight processes". All counts/pass/fail/
skip totals are unobserved until Luna executes. Fixtures include exact-prefix
conservation, unknown artifact/manifest/regime refusal, hand-specified small graph,
full-cell/corruption/null/count/lifetime flags, pre-producer dependency truth, native
audit missing/null/false distinction, identity binding and no-LTO refusals.

Stage 2: inspect saved native code **for these exact executables** before acceptance.
Copy audit-template.json into each arm as audit.json; keep all fields null until
evidenced. Bind provenance_sha256, reviewer, absolute hashed evidence_files and
annotated native address/dataflow evidence per gate. Inspect all three/four native
object formats (reject bitcode/LTO sections), full link lines, no cross-boundary
cloning, ABI offsets/values, QPC t0 -> real task call -> t1, x/y generation/stores,
p/q full stores, scalar checksum chains and final sx+sy before publication/return,
five observer calls with actual all-cell loads outside t1, output lifetime through
JSON-array reads, and matched release afterward. A marker, source order, right
checksum, noinline spelling or flags alone is not native proof. The validator checks
metadata/identities, not assembly interpretation; root reviews the annotations.

Revalidate saved reports with **no native rerun**; for each arm substitute its exact
name in the paths below (cpp-licensed shown). Run with private per-arm TEMP/TMP and
PYTHONDONTWRITEBYTECODE=1; this only reads saved evidence and writes validation:

```powershell
$env:TEMP = 'C:\Users\cdupu\Documents\GitHub\Blade\.round4-worktrees\b-measurement\bench\.build\B07-runner\cpp-licensed\temp'
$env:TMP = $env:TEMP
$env:PYTHONDONTWRITEBYTECODE = '1'
& 'C:\Python314\python.exe' 'C:\Users\cdupu\Documents\GitHub\Blade\.round4-worktrees\b-measurement\bench\diagnostics\output-boundary\validate.py' `
  --report 'C:\Users\cdupu\Documents\GitHub\Blade\.round4-worktrees\b-measurement\bench\.build\B07-runner\cpp-licensed\report.json' `
  --regime licensed `
  --provenance 'C:\Users\cdupu\Documents\GitHub\Blade\.round4-worktrees\b-measurement\bench\.build\B07-runner\cpp-licensed\provenance.json' `
  --audit 'C:\Users\cdupu\Documents\GitHub\Blade\.round4-worktrees\b-measurement\bench\.build\B07-runner\cpp-licensed\audit.json' `
  --out 'C:\Users\cdupu\Documents\GitHub\Blade\.round4-worktrees\b-measurement\bench\.build\B07-runner\cpp-licensed\audited-validation.json'
```

Stage 1 numeric-only success deliberately leaves native enclosure null and diagnostic
acceptance false. The null template also remains unmeasured, and explicit failed
gates remain false. Stage 2 may accept only this diagnostic after identity/numeric/
native gates pass; every report still has performance_eligible=false.

Stage 3 is **not dispatched**. Root chooses a bounded extra prepared-mutation arm
or arms after reviewing Stage 1/2 evidence. Its exact invocation uses the same runner
and tool args, `--stage prepared --arm <one root-selected route-regime>` and a new
absolute output root, e.g. B07-prepared-cpp-repro. No placeholder is a runnable
authorization. Every selected extra has one new native task call/interval, five
observer calls/six cleanup calls, distinct adapted source/macro/object/executable
identity and the mutated independent reference; root reviews its own native graph.
Any audited revalidation of such a saved report also requires --prepared-mutation.
No implicit extra native runs, sanitizer builds or broad sweeps in this checkpoint.

Stop with actual-diff review and frozen sources; root owns validation dispatch and
final disposition. No implementation/run result is asserted by this handoff.

## Stage 1 exact-suffix repair, source-only

Repair base is reviewed checkpoint `25c14a87` plus root's force-tracked frozen shim
commit `fa4fa882` (current HEAD). Root reported the first staged request stopped
before any native process: 14 pure fixtures, 13 pass and one error in
test_four_adaptations_preserve_exact_arithmetic_prefix. Logs remain in
`bench/.build/B07-runner`; these are runner results, not coder executions.

Read-only source/hex inspection shows both pinned C++ inputs end with the final
closing `}` byte and **no final newline**. The strict suffix check mistakenly
required a newline after that brace. Repair only removes that incorrect terminal
newline from the required suffix. It does not strip/normalize arbitrary trailing
whitespace, admit another hash, change the manifest/frozen inputs, or change any
helper/arithmetic-prefix bytes. Fixture assertions explicitly pin the two C++ raw
endings; a parser fixture also requires an added final newline to refuse, while
the existing four canonical/prepared prefix-conservation checks remain required.

Only adapt.py, test_boundary.py and this handoff changed. Updated source hashes are
in the table above. No adapter/fixture/native execution, build or commit by coder.
Root actual-diff review is required before Luna retries. The initial command and
saved-audit examples above describe the preserved first-attempt root; the retry
must use a new absolute output root and keep the old failed attempt intact.
Exact retry request, with all other staged limits unchanged:

```powershell
& 'C:\Python314\python.exe' 'C:\Users\cdupu\Documents\GitHub\Blade\.round4-worktrees\b-measurement\bench\diagnostics\output-boundary\runner.py' `
  --stage primary `
  --out 'C:\Users\cdupu\Documents\GitHub\Blade\.round4-worktrees\b-measurement\bench\.build\B07-runner-a2' `
  --gcc 'C:\msys64\ucrt64\bin\gcc.exe' --gxx 'C:\msys64\ucrt64\bin\g++.exe' `
  --clang 'C:\msys64\clang64\bin\clang.exe' --gfortran 'C:\msys64\ucrt64\bin\gfortran.exe' `
  --objdump 'C:\msys64\ucrt64\bin\objdump.exe'
```

If the retry reaches saved-audit review, use that new B07-runner-a2 root consistently
for report/provenance/audit/validation/TEMP/TMP paths. No native success or diagnostic
acceptance is established by this source repair; native and performance admission
remain separately pending/forbidden as specified above.
