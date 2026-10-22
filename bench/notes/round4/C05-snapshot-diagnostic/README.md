# C05 snapshot diagnostic — runner only

Source/tests remain frozen at `dea61728b21a46e29e5a3b8a3587f102299d6fdc`.
Attempt2 Release built successfully; optimize reported 744 pass / 1 fail.
Only `sparse_wildcard_canonical_interpreter_and_cpp_snapshot_mutation` missed
its unchanged total7423 and after99 pins. The proof/emission check passed.
The failing check passes only run.Stdout to its reporter, hiding run.Stderr.

Preserved log: `bench/.build/C05-implementation-runner/attempt2/optimize.stdout.log`,
SHA256 `BDBE53BD5EAB64D6A760B4D162D44721F356265997888323E54DE2973601C018`.
Successful DLL: `bin/Release/net10.0/Blade.dll`, SHA256
`85A5F249C026410751F94A2DB2396DE406BA83D1D24F0098620CAE038C9578BF`.
No new build or native compilation/execution is requested.

## Provisional source diagnosis

`src/Interp/Core.fs:1240-1249` forces an indexed assignment target and accepts
only VArray. Its other-value arm raises InterpUnsupported with
`IRAssign to non-array index target`. Sparse values are VSparse. Therefore the
mutation may stop the interpreter before either requested output is printed.
This is a hypothesis to check against raw stderr/status, not a diagnosed result.
`src/Interp/ArrayOps.fs:1846-1862` already gathers matched sparse cells into a new
store in supplied key order; the source does not suggest a live alias there.
No optimizer/interpreter/emitter repair or pin relaxation is authorized here.

## Exact diagnostic scope

The adapter embeds the exact OptimizeTests primary/beforeTotal/snapshot string
construction and uses the exact withTransposeRows/withPartialRows gates:
PRINT empty, REPRO1, CONTRACToff, FUSIONon, BLASoff, OMP_THREADS1. It restores all
six afterward and prints other relevant inherited gates. The second input reads
unchanged corpus `tests/corpus/sql-reduce/050_sparse_private_snapshot_mutation.blade`.
Both inputs run once; this is two diagnostic cases, not a new test matrix.

Each case uses Lowering.lower (same entry as the test), validates typed IR,
generates C++ SOURCE with genSelfContainedProgramFromIR, then calls runProgram
in exactly the failing test's order. It saves raw interpreter stdout/stderr and
ExitCode, full decisions from lowering and C++ generation, actual host shape and
certificate applicability, generated C++, and untruncated typed IR including
function bodies/types/capture/side payloads. A function-expression host is
reported as inapplicable, rather than constructing a fake ordinary host for the
certificate. Decisions use the compiler's renderer. Full IR uses the accepted
C04 walker including explicit SrcLoc.Span handling, never `%A` on whole callables.
Raw results for both cases are persisted before the reflected full-IR dump.
Per-case collection errors are saved separately; an interpreter nonzero status
is primary diagnostic evidence, not a bridge failure.

## Bounded sole-Luna request

Root reviews/freezes this bundle first. Use the existing exact DLL above,
private TEMP/TMP, and a fresh output directory; keep every attempt2 artifact.
Record source/bundle/binary/tool hashes and command/exit provenance. From the
c-terminals checkout:

```powershell
dotnet fsi --reference:bin/Release/net10.0/Blade.dll bench/notes/round4/C05-snapshot-diagnostic/snapshot-probe.fsx bench/.build/C05-snapshot-diagnostic-runner/attempt1-output
```

Capture the adapter's complete stdout/stderr/exit. Return both raw interpreter
triples, lower/validate status, certificate/decisions, generated C++ and full IR
to root. Stop on an adapter/validation/dump failure and retain partial evidence.
Do not run native code, another build/test/CLI emit/plan, generate expected pins,
or change any program/source. Root reviews results before any next action.

Coder performed only source inspection, adapter/document authoring and static
hash/diff checks; no lowering, emission, interpretation or execution was done.
`SHA256SUMS.txt` freezes adapter, README and unchanged test/corpus/DLL inputs.
Its own hash is reported externally. `.r4tmp` is preserved.
