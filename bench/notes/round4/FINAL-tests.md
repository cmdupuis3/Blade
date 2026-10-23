# Final combined integration checks

The combined integration source snapshot `3275b88646ec9ff680f0ec2d20a6fa431c7fa393` built successfully in Release. A documentation-only commit advanced the checkout to `ee4b1a0b895585036d659462fd1504313c941a83` before the three test gates; no compiler, test, or project source changed, and each gate used the same fresh binary: `Blade.dll` SHA-256 `5FF48FB54403770F5E5CE27777F3D4584B12ABF53273AEE324E976571579AE57`.

- Build: `dotnet build Blade.fsproj -c Release -p:RestoreIgnoreFailedSources=true` — exit 0, 10 warnings and 0 errors. Warnings were NU1900 vulnerability-feed access plus existing F# reserved-name warnings.
- LLVM goldens: `dotnet bin/Release/net10.0/Blade.dll test llvm goldens` — exit 0. LLVM compare rules 23/0/0, LLVM emission pins 3/0/0, LLVM Fact Layer 152/0/0, and Simplex Blocks Agreement 19/0/0. No skips. This command includes the LLVM facts; facts were not run separately.
- Standalone Zarr: `dotnet bin/Release/net10.0/Blade.dll test zarr` — exit 0, 602 passed and 0 failed. The log contains one internal NetCDF stream differential skip because the native NetCDF library is unavailable. Embedded private-spool and rank-two streamed-value gates reported 82/0/0 and 123/0/0 respectively; these are included within the Zarr run, not additional invocations.
- Interpreter sql-reduce: `dotnet bin/Release/net10.0/Blade.dll test interp sql-reduce` — exit 0, 61 passed, 0 failed, 0 skip-unsupported, and 0 skipped. The output also includes the harness's single hermetic Zarr provider-read check.

All four pre-suite commands ran serially from the integration checkout root with separate private `TEMP`/`TMP` paths under `bench/.build/FINAL-runner`, complete stdout/stderr and provenance files. The full suite was then launched exactly once after the explicit authorization marker was written; its result is recorded below. No benchmark or performance measurement was run.

| Gate | Stdout SHA-256 | Stderr SHA-256 | Provenance SHA-256 |
|---|---|---|---|
| Release build | `704344E4035D9E3E47D7029D0B1011CEEC00875D8A55BFF827D8599C183279CE` | `E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855` | `A7C2A4AF24587B49892BB37778A0DF9BE70DE26F6E8701E92149253F226B6B4A` |
| LLVM goldens | `8DD489F993FB303B6B4BED33C977D501DA6DDACE1629CF1B5A7B41860D91B64E` | `DDED3B172D9341D023871DB7CE3EC62F6484904CE716D20817A44ADF88E393C2` | `62361FF62BBF201032A4042FD551012139A42ACE8F2E00026B48CACB1B3233CF` |
| Zarr | `667FB6C0C05314C899277A2E6CD3A499C859D91289D380FCD410C76EDFF97C8C` | `2DFFC42C8783F8B9F8AB0CD9EEF29DF957BC3CBD17EE67D221320F60480B8478` | `8389592D7340867F33684360C652C053F83150C4DDADEA80DBA76298040D99B1` |
| Interpreter sql-reduce | `2C435EE7B08B0711D891F41B0CBB4F5E5C8A71E1667E7D108049D605D194C87C` | `E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855` | `9291DCAC1905D93F5A435E2927E5BD63F5D6777A71CD4BA70632329E5B5329F4` |

- [Build stdout](../../.build/FINAL-runner/build.stdout.log), [stderr](../../.build/FINAL-runner/build.stderr.log), [provenance](../../.build/FINAL-runner/build.provenance.txt)
- [LLVM goldens stdout](../../.build/FINAL-runner/llvm-goldens.stdout.log), [stderr](../../.build/FINAL-runner/llvm-goldens.stderr.log), [provenance](../../.build/FINAL-runner/llvm-goldens.provenance.txt)
- [Zarr stdout](../../.build/FINAL-runner/zarr.stdout.log), [stderr](../../.build/FINAL-runner/zarr.stderr.log), [provenance](../../.build/FINAL-runner/zarr.provenance.txt)
- [Interpreter sql-reduce stdout](../../.build/FINAL-runner/interp-sql-reduce.stdout.log), [stderr](../../.build/FINAL-runner/interp-sql-reduce.stderr.log), [provenance](../../.build/FINAL-runner/interp-sql-reduce.provenance.txt)

## Authorized full-suite run

The single authorized default invocation was `Blade.exe test`, run from the integration checkout at `ee4b1a0b895585036d659462fd1504313c941a83` with `BLADE_PRINT` and all policy gates unset. It used the Release compiler above (SHA-256 `7506E38D9E3A5978324ABE41540FC4F63891CA06A1D9188EF25627C0464D67EC`), private `TEMP`/`TMP` under `bench/.build/FINAL-runner/full-suite`, and exited 1. The final summary is **8,279 passed, 11 failed, 1 skipped**. The skip is the real GR render because `GRDIR` is unset.

Eight failures are in the 3,041-case `All` corpus block: five arity cases could not start their generated executables; three compile cases (`Late Broadcast Over An Empty Array`, `Rand Poisson Large Lambda`, and `Recursive Array Nested DP`) hit generated C++ identifiers such as `far`/`pascal` that the compiler parsed as invalid declarations. The other three failures are Diagnostics Core's panic-containment scan (it found `blade_rt::panic` in `src/cpp/private_spool_tests.cpp:318`) and two Surface checks (`protocol/surface.json` differs from the current render, and `protocol/data/diagnostics.json` lacks BL8016). No second full-suite run was made. Targeted repair validation is recorded in [FINAL-repair-tests.md](FINAL-repair-tests.md).

The one-time authorization marker is [full-suite.started](full-suite.started). Marker SHA-256: `565D4729D1E4FF504FB44FAFAA4D55625A5B319FC8491B8ACB4DB42B1D6C72B2`. Full-suite stdout SHA-256: `A11D6535CDF9E1045E09602DCCE94F20BC19DF28EAA72DF6BC3C68975493BA4C`; stderr: `CFEA1D1B9E88C3DF445BF465A350B203F9D7FE85D56AB08DC0C3E61C967018CC`; provenance: `A5AFA8065F07C4CB55A565CFE2DC9F543ADE97483E76C8F68CD7E4BC2BC77DB1`.

- [Full-suite stdout](../../.build/FINAL-runner/full-suite/full-suite.stdout.log), [stderr](../../.build/FINAL-runner/full-suite/full-suite.stderr.log), [provenance](../../.build/FINAL-runner/full-suite/full-suite.provenance.txt)
