# B07 proposal: one prepared-mutation LLVM repro diagnostic

2026-10-22. **Source-only dispatch design for root review; no execution authorized
by this document itself.** Root reports canonical native audit acceptance and
eight saved-output validations queued with sole Luna. This proposal does not
assert their completion. No frozen bundle, saved native artifact, report, audit,
compiler, runtime, harness or production source is changed. Only this note is
authored; no build/emission/adapter/test/validator/native execution or git mutation.

## One selected arm and exact dispatch

Recommend **llvm-repro**, one prepared-mutation native process, to exercise the
opaque callback in the adapted IR route under the original ordered fold and
contract-off graph. Fixed N=2053 and R5 seeds 10000019/20000038 remain unchanged.
No canonical rerun, eight-arm rerun, new shape, licensed comparison, Fortran arm,
sanitizer request or performance sweep. Existing `runner.py` is the dispatch
entry; this is not a request to use `bench/harness/run.py`.

The new absolute output root below did not exist at proposal inspection. Existing
roots refuse; preserve B07-runner and B07-runner-a2 and all their saved evidence.
Only after root explicitly dispatches to sole Luna, the complete invocation is:

```powershell
$env:PYTHONDONTWRITEBYTECODE = '1'
& 'C:\Python314\python.exe' 'C:\Users\cdupu\Documents\GitHub\Blade\.round4-worktrees\b-measurement\bench\diagnostics\output-boundary\runner.py' `
  --stage prepared --arm llvm-repro `
  --out 'C:\Users\cdupu\Documents\GitHub\Blade\.round4-worktrees\b-measurement\bench\.build\B07-prepared-llvm-repro-a1' `
  --gcc 'C:\msys64\ucrt64\bin\gcc.exe' --gxx 'C:\msys64\ucrt64\bin\g++.exe' `
  --clang 'C:\msys64\clang64\bin\clang.exe' --gfortran 'C:\msys64\ucrt64\bin\gfortran.exe' `
  --objdump 'C:\msys64\ucrt64\bin\objdump.exe'
```

All tool arguments are required by the existing parser, including g++/gfortran,
but this selected stage compiles/runs no C++ or Fortran workload. The runner first
records tool versions/identities and reruns its existing focused pure fixture
suite. It creates private base TEMP/TMP before those child calls and private
llvm-repro TEMP/TMP before native compilation. The workload is exactly **one task
process/call/interval**, plus five post-t1 observer calls and six cleanup calls.
Tool-version, fixture, adapter, inventory and validator processes are additional
non-task children; do not describe the entire request as one OS process.

The runner invokes the adapter with `--kind llvm --regime repro
--prepared-mutation`, writes `llvm-repro/adapted/task-prepared.ll`, and compiles
driver.c with `-DB07_EXPECT_PREPARED_MUTATION=1`. Observer.c and the frozen shim
remain the same sources. Task/driver/observer/shim become four separate native
objects, each with `-fno-lto`, linked by clang with `-fno-lto` and object inputs
only. LLVM instruction flags in the frozen repro IR define its FP regime; no
environment switch or global policy change substitutes for that source identity.
The runner invokes `validate.py` with `--regime repro --prepared-mutation` on the
one saved report/provenance before completing the stage. Any fixture, structure,
build, launch, timeout, numeric or provenance error stops with preserved logs.
No fallback or successful-process repeat is proposed.

## Independently specified expected change

All indices below are zero-based. Let m=1026, x_i=R5(i,10000019),
y_i=R5(i,20000038), using the pinned independent B04 integer R5 definition.
Let RN denote binary64 rounding in the preserved contract-off graph, and let
S(a) be ascending serial addition from initial +0.0 through all 2053 cells.

| quantity | prepared-mutation expectation |
|---|---|
| x | x'_m=x_m+1/8; all other x cells unchanged |
| y | all cells unchanged |
| producer | v'_i=RN(RN(1.5*x'_i)+y_i); store every p'_i=v'_i |
| consumer | w'_i=RN(v'_i*v'_i); store every q'_i=w'_i |
| selected output | p'_m=p_m+3/16 exactly for these R5 operands; q'_m=RN((p_m+3/16)^2) |
| other outputs | unchanged p_i/q_i values for i != m under the same FP graph |
| checksum | S(q'), completing the entire original fold; it must follow the changed q_m |
| input_checksum | RN(S(x')+S(y)); canonical input_checksum + 1/8 exactly for these bounded R5 inputs |

The input and producer increments are analytic binary-rational expectations,
not generated runtime pins. Each R5 value is h/2^31 with h<2^31. All positive
partial input sums, including the added 1/8, have integer numerators below 2^44,
so these input sums/final add are exact in binary64. The producer's products/adds
also fit binary64 exactly for these operands, giving the exact 3/16 shift.
Before rounding, the square's real-number increase is 3*p_m/8+9/256. **Do not
use that expression as a replacement floating-point graph or claim the reported
checksum difference equals it exactly.** q_m is rounded and the later serial
checksum additions round again; the existing independent reference computes the
whole specified graph. No concrete decimal result has been generated by the coder.

Existing validator `expected(True)` constructs the independent R5 arrays, changes
only x[N//2] by 0.125, then calls the unchanged B04 `from_inputs` reference.
`--regime repro` selects its serial checksum and serial input-checksum expectations.
Every p/q cell and both scalar results must pass its 1e-12 tolerance with unit
floor; formatted JSON does not establish bit identity. Keep
`bit_identity_established:false`. Initial and final native observers also inspect
every x/y cell against their prepared expectation. The saved report does not dump
full x/y arrays, so their coverage is the observer's actual native loads/checks,
not an invented full-input JSON endpoint.

Prepared mutation is exactly one callback after both original input fills and
**before** the original producer/checksum graph. It writes x[m], not y, n or an
output. `prepared_calls` must be 1, `prepared_mutation:true`, `canonical_bad:0`
(this historical field name means the initial full check against this arm's
prepared expectation), task_status/failed=0, p_count=q_count=2053, ABI size=72.
The unchanged source prefix identity is required; only the explicitly identified
callback/declaration adaptation may differ from the canonical task.

## Controls reused and new native-review obligations

The primary eight artifacts remain immutable controls. Use saved canonical LLVM
repro report/evidence as the unmutated contract, with executable SHA-256
`cb3d2084e3012ff229998e1271bf5b4f809cb2783bc0055ffbae637520fd01c6`.
Do not copy its native addresses/audit acceptance onto the new executable.

The existing five post-t1 observer calls remain: full prepared-result check;
post-t1 x[0]/y[2052] mutation with unchanged stored p/q/scalars; one interior p
corruption detection; one tail q corruption detection; full restored-buffer check.
Each deliberate temporary cell change restores its saved exact value. Restoring
the p corruption restores **p'_m**, not canonical p_m; the intended pre-producer
x'_m mutation remains the task's input throughout. Both complete arrays are then
read into JSON, followed by four malformed-descriptor refusals, real matched
cleanup and duplicate refusal. Task/observer/cleanup counts remain 1/5/6.
Post-t1 controls do not replace the independent pre-producer dependency witness.

Root must review saved native code for this **new exact executable** after numeric
success, with fresh address/dataflow annotations and hashes:

1. Four distinct native COFF objects, no bitcode/LTO payload or cross-boundary
   task/driver/observer clone/inlining; conservative callback/publication effects.
2. Actual t0 -> real task -> t1. Inside the call, both input fill/store streams
   finish before the callback. Trace x/y roots and n=2053 into its C ABI arguments.
3. Exactly one actual callback invocation, its count increment and store at
   x+0x2010 (=1026*8), adding 0.125. Trace the **updated storage** into p'_m and
   q'_m, rather than a pre-callback scalar/constant or regenerated canonical input.
   This may change optimizer decisions; do not infer identical materialization
   from the accepted canonical task's loop shapes.
4. Both full p/q store streams, including index 2052 and distinct retained roots;
   actual v computation, v*v, ordered checksum chain and final S(x')+S(y) finish
   before value publication/return. Callback cost belongs to this interval.
5. Full actual observer loads for all 2053 cells, expected prepared flag=1 and
   mutation-dependent comparisons after t1; actual corrupt/check/restore behavior,
   full JSON reads, retained lifetimes and matched blade_free cleanup afterward.

Existing audit gate names/format need no code expansion. Attach the new
callback/dependency evidence as hashed review notes and include it in the relevant
payload/store/checksum/lifetime gate evidence. Missing evidence remains null or
refused; numeric success alone is not acceptance. Even passing this witness says
nothing about DRAM completion, allocator live/retained bytes, RSS reduction,
ordinary emitted Blade, or task performance. `performance_eligible:false` always.

## Provenance and staged completion

Use one new arm directory under the proposed output root. Retain its adapted IR,
adaptation.json, four objects, compiler/tool hashes/versions, commands, native
format/section/disassembly text, boundary.exe, report.json and provenance.json.
Prepared source/driver object/executable identities must be recorded distinctly;
do not relabel the canonical artifacts. Actual new hashes/addresses are unmeasured
until Luna runs. Observer/shim hashes may coincide with canonical objects when
rebuilt identically, but their linked addresses and the new executable still need
review. The adapter identity must include prepared_mutation=true and the exact
frozen origin hash. The unchanged canonical-prefix hash expected from the saved
canonical LLVM repro adaptation is
`ee1271c18b9312954dd0e69be678e3d6dcb5b7883d57cdf672e1daea3cbc7ee5`.

Unchanged reviewed source identities used by this request:

| file | SHA-256 |
|---|---|
| runner.py | 77E15567DC72D8FA9EADEB852AE6499271887E818326EDA077F3CD46D445143B |
| adapt.py | F90C7DF3CC8297A1F090144DE4873722AEE096CAD8263A7A9F5419B44879C925 |
| validate.py | B13F6936B8A006F98728C90B73950EBCF17AFE9F1C3EB610B9EE0EB69E38B832 |
| driver.c | 62C836FE5893BEC974A6B4878B3DE7611A5EC2C1C064A74A435D2FC72518E97C |
| observer.c | 666D3FC8F0D829BA8046A971C811C511138385EB52EB1A3BA6D3C04877294927 |
| b07_abi.h | 2F40E0594A6CBADC604C943106AD5BB55E50DAB7ED5C4E3881358BEDFF5A29FB |
| frozen/llvm-repro.ll | 6D8F5E274E5225A7C21F3D5571A7715ED71EC76289B8164FEE96A573D63EF0C5 |
| frozen/runtime/blade_llvm_shim.c | 172754BE5A4E4A1976C8E0762304C90F87D79664CBE09695D71645FE05BA7888 |
| frozen-manifest.json | 56B53905E7A28D96B1AE4F1A3257EA47BECE002BAA1D26F7FE970EDFD83082C9 |
| B04 reference.py | D4207199F19F2DB8E9BEF8CCE0C8B338EDD0F7D47A582816D0FD69C508212FB0 |

After root's new native review, sole Luna may revalidate the **saved** prepared
output without another task execution. The exact one-arm command is below; its
audit.json is a later root-authored, hash-bound review artifact, not yet present
or approved by this proposal:

```powershell
$env:TEMP = 'C:\Users\cdupu\Documents\GitHub\Blade\.round4-worktrees\b-measurement\bench\.build\B07-prepared-llvm-repro-a1\llvm-repro\temp'
$env:TMP = $env:TEMP
$env:PYTHONDONTWRITEBYTECODE = '1'
& 'C:\Python314\python.exe' 'C:\Users\cdupu\Documents\GitHub\Blade\.round4-worktrees\b-measurement\bench\diagnostics\output-boundary\validate.py' `
  --regime repro --prepared-mutation `
  --report 'C:\Users\cdupu\Documents\GitHub\Blade\.round4-worktrees\b-measurement\bench\.build\B07-prepared-llvm-repro-a1\llvm-repro\report.json' `
  --provenance 'C:\Users\cdupu\Documents\GitHub\Blade\.round4-worktrees\b-measurement\bench\.build\B07-prepared-llvm-repro-a1\llvm-repro\provenance.json' `
  --audit 'C:\Users\cdupu\Documents\GitHub\Blade\.round4-worktrees\b-measurement\bench\.build\B07-prepared-llvm-repro-a1\llvm-repro\audit.json' `
  --out 'C:\Users\cdupu\Documents\GitHub\Blade\.round4-worktrees\b-measurement\bench\.build\B07-prepared-llvm-repro-a1\llvm-repro\audited-validation.json'
```

Without that reviewed audit, native enclosure remains null and diagnostic
acceptance false. Root owns dispatch and final disposition. Stop after this
proposal; no pending native action is performed by the coder.
