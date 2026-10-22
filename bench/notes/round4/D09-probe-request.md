# D09: frozen source-only rank-two row-table diagnostic

State: authored and UNRUN; root review precedes sole-Luna dispatch. No compiler
or runtime implementation is included. D08 artifacts remain unchanged.
Lane base is `717790c4c907684c98b3b685fadd784a6b1d7fd1`. The available reviewed
D08 DLL is pinned by the bridge to SHA256
`4F60DD09CCEA1970B7DCAF6201C477BE73DF71268DBCE31E7DC8EBDD28B4F54D`.
Use that DLL without a rebuild; the source/hash manifest records inspected
compiler inputs and the exact binary, alongside all six authored assets.

## Scope and fixture bound

The proposal targets redundant input `T** rows = new T*[N]()` storage only.
Run offsets remain O(number of bands), required output remains resident, and
its own row skeleton remains required. No measured memory/performance gain or
implementation admission is claimed. This diagnostic changes no refusal logic.
Mere optimization nonadmission must preserve an existing valid route; only a
proved unsupported nonlocal read/escape can justify a later explicit refusal.
Current acceptance or refusal of either adversarial source is evidence to read,
not a reason to execute it or force a new blanket backend rejection.

The four frozen Blade inputs share one newly created Zarr v2 group. The bridge
uses the existing compiled ZarrWrite helper, exactly two variables A/B, shape
5x3, chunks 2x2, `<f8`, C order, finite fill 0, dimension names `[row,col]`, and no
compression/filters/packed extension. A(i,j)=10*i+j; B(i,j)=100+10*i+j. Exactly
twelve 32-byte padded payload chunks (384 bytes total) and five metadata files
are created by the runner, exclusively inside the new output directory.
The bridge verifies count/length and records every chunk's hex/hash plus full
metadata before lowering. It never overwrites/deletes a previous output root.

| Source | Selected printed binding | Observation requested |
|---|---|---|
| 01-materialized.blade | out | Two-source scalar zip -> demanded rank-two resident array. Capture allocation/row peels, read/scan order and final partial band. |
| 02-terminal.blade | term | Full scalar reduction of the same deferred map. Record actual fold/materialization path; do not assume the output route is reused. |
| 03-gather.blade | out | Captured B(0,0) is global across bands. Negative-control scalar parameter annotations prevent unused b from being inferred as a fiber parameter. Record actual source/capture/index and current outcome. |
| 04-array-escape.blade | out | Whole captured B is returned as an array. Scalar parameter annotations keep the intended operand roles; record actual output rank, captures and earliest refusal/current emission. Never execute. |

Independent hand-derived positive values, for future validation only:
`[[200.0,203.0,206.0],[230.0,233.0,236.0],[260.0,263.0,266.0],[290.0,293.0,296.0],[320.0,323.0,326.0]]`.
The terminal is 3945.0. Bands are `[0,2)`, `[2,4)`, `[4,5)`; final row uses local
row0 rather than global4. For each source, expected physical chunk-key groups
are `0.0,0.1`, `1.0,1.1`, `2.0,2.1`. Those are prospective runtime facts, not
observed reads in an emission-only diagnostic.

## Sole-Luna command after root review

Do not build, compile C++, run a generated program/test/category, benchmark,
invoke another model, or modify the four sources. Use the existing reliable
process capture wrapper, private TEMP/TMP, and a new short ASCII output path.
The bridge rejects an existing root and paths longer than 160 characters.

```powershell
$d09Checkout = 'C:\Users\cdupu\Documents\GitHub\Blade\.round4-worktrees\d-streaming'
$d09Dll = Join-Path $d09Checkout 'bin\Release\net10.0\Blade.dll'
$d09Bundle = Join-Path $d09Checkout 'bench\notes\round4\D09-census-inputs'
$d09Probe = Join-Path $d09Bundle 'probe.fsx'
$d09Output = Join-Path $d09Checkout 'bench\.build\d9p'
& dotnet fsi ("--reference:" + $d09Dll) --exec $d09Probe $d09Bundle $d09Output
```

Verify D09-source-hashes.txt before dispatch; capture command, source HEAD,
DLL/script/fixture hashes, dotnet identity, actual environment/TEMP/TMP, CWD,
stdout/stderr/exit. The bridge records its loaded assembly FullName/MVID/hash
and temporarily uses the already proved AppContext host-data override to that
DLL directory, restoring it in finally. Environment/print selection and split
timing flags are also restored. Every key in the loaded compiler's
`Optimize.optimizerGates` is cleared to its default for this baseline, saved
beforehand and restored in finally. `BLADE_OMP_THREADS=1` is likewise pinned
and restored. The actual values of all controlled keys, plus `OPENBLAS_DIR`,
`TEMP` and `TMP`, are recorded for every case. No private-spool grant or
options API is used.

For each case it saves the substituted source, calls lowerCaptured once,
validates once if lowering succeeds, and emits once if validation succeeds.
Lower diagnostics and decision reasons are flushed before rich IR formatting;
emit warnings/diagnostic refusals or exceptions and complete decisions are
recorded before independently guarded full IR/module/type/provider/binding/
callable/capture/side-root dumps. These use the exhaustive reflected walker
from executed `C05-snapshot-diagnostic/snapshot-probe.fsx` (precedent SHA256
`1EF5DC67464A14BAABB2FE6131B25FB90ECD4AA9F480134AC7828956F94F0ED4`),
including the explicit `SrcLoc.Span` schema case. It enumerates every record,
union, tuple, map entry and collection member without collection/depth
truncation; an unknown payload type explicitly fails that artifact after the
primary reasons have been flushed. The dedicated `*.typed-ir.txt` is the
complete typed program; separate exhaustive binding/callable/side-root and
provider/type files expose the corresponding metadata. Structural `%A`
formatting is used only for primitive leaves/environment strings, never as a
full IR or projection dump. Successful C++ text is saved in full even if
it contains a refusal directive. An ordinary compiler refusal is a diagnostic
observation; unexpected exceptions/artifact formatting errors make exit2, and
setup failures remain nonzero. Exit0 means the census completed, not acceptance
of any program or optimization.

The public buildLoopNestCodeGen + routeKernelBodyThroughCall APIs are additionally
used AFTER main emission for a labeled initial projection of each ApplyInfo's
LoopIndexBinding/ElementBinding chain, resolved params, captures and kernel.
The entire initial LoopNestCodeGen record is serialized with the same exhaustive
walker to each `*.initial-cg.txt`. Its callable context is explicitly
installed/restored. These are reconstructed objects, **not** the actual final
emitted identity certificate, slab/epilogue/fold state. No production hook is
introduced. Compare this projection with the actual full C++ before deciding
an implementation certificate seam.

## Review outputs, then stop

Return probe.txt, four substituted sources and their complete IR/C++ where
available, all refusals and process capture. In the positive C++, identify both
full-height row table declarations/initialization loops/deletions, actual outer
row and inner scalar peels, both value-band and required output allocations,
the retained run table, both source read/normal missing-scan order, and final
nonzero-origin band bounds. Report all source/capture/ApplyInfo/index identities
and any codegen path that differs from the proposal. The terminal control must
be assessed separately from the demanded materialized output.

Do not infer that every future optimization nonadmission warrants rejection:
preserve supported fallback routes, especially the independent terminal/fiber/
tile forms. Raw gather/escape evidence is for proving exact locality failures.
Root must review these observations before authorizing production/test work.
No further gate follows automatically.
