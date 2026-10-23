# Final-suite generated filename repair

Frozen source-only change in c-terminals, base7576d52eb1713d9b38a1f6eb8fbdef58d19d49f8. Integration and its running full-suite/logs/artifacts remain untouched. No coder build, emission, native execution, tests or Git mutation.

## Evidence and boundary

The five arity failures never started their programs: .NET reported executable file-not-found. The saved C++ names contain U+2014; actual executable names contain U+FFFD. Their full paths are143–171 characters. This is evidence of a native filename-transport mismatch, not a numeric/lifetime failure. The specific g++ encoding mechanism is inferred, not reproduced here. Build.fs, Runner.fs and all five corpus files have identical Git blobs in master361504b6 and integration's final-suite source; no baseline runtime claim is made. Build.fs returns the requested output path on compiler exit0; no missing-output guard is added in this repair.

sanitizeFileName is shared by CLI compile scratch (CliCommands472); Runner C++/LLVM, multifile and gen paths (286/294/1265/1572); DiffOracle154; OptDiff171/185; InterpDiff350; CUDA204; MPI64; OMP2060/2218/2323/2446/2596/2694/2958; LLVM2880/3161/3195/3459/3460/3626. These all derive artifact paths through the same function; displayed test/program names remain their original values.

## Change

Keep every existing ASCII replacement byte-for-byte. Only the non-ASCII path projects non-ASCII UTF-16 code units to underscores and appends `_u` plus16 uppercase hex digits of SHA256(original UTF-8 name). This makes generated basenames ASCII, distinguishes Unicode spellings with the same readable projection, and adds a fixed18 characters rather than expanding every character. Existing ASCII behavior and its pre-existing many-to-one punctuation mapping remain; this is collision-resistant naming, not a new injectivity guarantee. No Unicode normalization or corpus-header renaming.

Owned files: src/Build.fs and existing tests/SetupTests.fs. Sixteen pure checks cover all five exact em-dash names (ASCII output plus determinism/bounded suffix), five exact legacy ASCII controls including punctuation/empty names, and em/en/replacement/ASCII projection distinctions. They use the existing standalone setup block, with no new category/fsproj entry. All corpus bytes and compiler semantics remain unchanged. No optional guard/backend/cache expansion.

## Sole-Luna request after root review/integration and full-suite completion

1. Fresh Release build of reviewed source, preserving complete logs and DLL/source identity.
2. `dotnet bin/Release/net10.0/Blade.dll test setup` for the pure filename assertions plus existing setup checks. Stop unexpected failure.
3. Run the existing five actual fixtures with original TEST names, values and default native gates. A minimal FSI adapter may use Runner.runTestCategoryFull on the five existing corpus entries to keep pins and report generated source/executable filenames; alternatively `test arity` is the bounded existing category. Target files021_comoment_prod,022_comoment_covariance_matrix,025_deferred_named_former,026_multi_arity_dedup,027_symmetric_pack_comm under tests/corpus/arity. Require successful compile, file existence at the exact returned ASCII path, actual execution and original pins; no renamed fixture copies and no second full suite. Save filenames/codepoints and results.

Static diff --check passes; F# CRLF working/LF index endings retained. `.r4tmp` untouched. FINAL-filename-SHA256SUMS.txt pins the two source/test files; handoff hash reported separately. Frozen pending root review and sole-runner evidence.
