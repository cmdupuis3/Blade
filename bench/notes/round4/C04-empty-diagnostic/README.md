# C04 empty-axis interpreter diagnostic (runner only)

Attempt3 built checkpoint 89b49014; optimize reported 691 pass, 2 fail, 0 skip.
Only R0 and negative_zero failed ON/OFF output/error/status equality, while
their independent ON seed pins and admission passed. No expectation is changed.

Confirmed cause in the attempt3 binary: Interp/ArrayOps.fs:784-792 gives every empty literal runtime
Extents=[|0|], including M declared Array<Float64 like R0,C3>. Loops.fs:2553-2554
forces M and calls transposeArray; ArrayOps.fs:988-991 rejects axis 1 against
runtime rank 1. ON never transposes/reads M for R0 and returns the right seeds.
C0 uses [[],[]], so the nonempty outer literal preserves runtime [2;0]. This
was confirmed by Luna's successful bridge retry: all four arms completed;
ON returned status0 with the exact -7.25/-0.0 seeds, OFF returned status125,
empty stdout and `interp-unsupported: transpose: dimension index out of range`.
Complete typed IR and raw files remain in
`bench/.build/C04-empty-diagnostic-runner/retry1-output`; the retry transcript is
retry1.stdout.log in its parent (SHA256
`873A77B3FB7D9FC149A4A1A335116896FB226C9F633EA8A959CC3513771F79A4`).

The bridge embeds exactly the two OptimizeTests strings/transforms and the same
gates (PRINT empty, REPRO1, CONTRACToff, FUSIONon/0, BLASoff, OMP_THREADS1).
It uses the test's Lowering.lower entry, validates each resulting optimized IR,
then saves raw interpreter stdout/stderr/status and escaped stdout/stderr for
all four arms BEFORE dumping full typed programs with an untruncated reflection
walker. A collector failure cannot suppress the four primary raw results.
SrcLoc is explicitly serialized through its sole Span payload. A static class
audit of IR.fs and referenced Types.fs/Ast.fs found no other reachable custom
class payload here: IRBuilder is a separate utility, not an IRProgram field.
The test name matches the harness.
This is post-lowering optimized IR, not the earlier topology probe phase.

The first bridge attempt (script SHA256
`326B1CE2738BE45778F5CFE8830FB9B115A8FFE1F11AAD723E3CA6E1FD71748F`)
failed before interpreter execution on R0-on because SrcLoc was unhandled.
Logs remain in `bench/.build/C04-empty-diagnostic-runner/{stdout,stderr}.log`;
output/R0-on.typed-ir.txt is a partial 7752-byte artifact, not complete IR.
No raw interpreter results were obtained. The retry changes the bridge only,
preserves those artifacts, and uses a fresh retry1-output directory.

Luna request: use the existing attempt3 binary, no rebuild. Verify Blade.dll
SHA256 `580D8378F2090290D310B3618168F1B77F546B6BBCC7C4EB2811B2031CE05CF8`.
From the c-terminals checkout, run this single bounded diagnostic with private
TEMP/TMP, capture stdout/stderr/exit/provenance and return all four raw results:

```powershell
dotnet fsi --reference:bin/Release/net10.0/Blade.dll bench/notes/round4/C04-empty-diagnostic/empty-axis-probe.fsx bench/.build/C04-empty-diagnostic-runner/retry1-output
```

No native compiler/emitter, new expected pin, corpus mutation or full suite is
requested. Stop if script lowering/validation/dump fails. Root reviews evidence
before authorizing any repair. These instructions replay the frozen attempt3
binary, not the subsequently authored C04-I1 source repair. See C04-handoff.md
for that separate repair and its thirteen direct unit witnesses. Existing C04
source seeds and ON/OFF comparison were preserved; the diagnostic bridge and
its four observed arms are separate from those unit-witness counts.
