# Targeted final-suite repair checks

The original one-time full-suite result remains recorded in [FINAL-tests.md](FINAL-tests.md): 8,279 passed, 11 failed, 1 GR-render skip, exit 1. This repair validation ran against integration HEAD `92da3d804ae2013cc3408e01e7ed8be347a4c628`; no second full suite was launched.

## Build and generated Surface

All nine entries in `FINAL-sdk-hygiene-source-hashes.txt` matched before execution. The fresh Release build used `dotnet build Blade.fsproj -c Release -p:RestoreIgnoreFailedSources=true` and exited 0 with 10 warnings and 0 errors (NU1900 vulnerability-feed warnings and existing F# reserved-name warnings). SDK/tool identities were .NET `10.0.400` and ucrt64 g++ `16.2.0`. The resulting compiler identities are Blade.exe SHA-256 `CE8899D60523D7D9142B4AACC0FF1FEE21EE19E422B63A4D3774AD4658C9413B` and Blade.dll `5BD62B4B231CBC0DE863E0BD2FE66D99E196F938577EB10E7A580D16F2E39595`. The deployed run-record and private-spool headers matched their then-current source hashes exactly (`F5475484…628ECF1` and `8B58EE1B…15EEB05`); the later reviewed header-state repair is verified below.

`Blade.exe ide surface` exited 0. Its raw stdout was valid JSON with one CRLF terminator; the committed artifact was generated from that output by normalizing line endings to LF and writing UTF-8 without a BOM. `protocol/surface.json` changed from SHA-256 `E3B21CA949C03CA885D485AC0996451941EE6979A5ACFAB266F198663ECC4EA1` to `C233ED810A6FD42CBFA70B273E4768761E45D6BCE83E7B3E67F5E51BD84AD3A8`. `Blade.exe test setup` passed 43/0/0, including the repair's 16 setup assertions. `Blade.exe test surface` passed 7/0, including both previously failing protocol checks.

## Arity and SDK header controls

`Blade.exe test arity` passed 79/0/0. All five fixtures from the full-suite failure passed compile, execution and value pins. Each produced an existing ASCII-only generated executable under `generated_cpp_tests`; the absolute paths were 161–189 characters. Their digest-suffixed basenames were:

- `comoment_prod___recursive_centered_product_over_a_pack_of_arrays_u479E03529004B199.exe`
- `Covariance_matrix___object_forcomoment_over_2D_data_u26F58C15F0E403D7.exe`
- `Deferred_former___bare_object_fornamed_poly_kernel__let-bound_then_applied_uD883984379BEC94C.exe`
- `One_recursive_poly_kernel_specialized_at_multiple_arities___no_C___redefinition_u89B543DDF18BB7C2.exe`
- `Symmetric_arity-poly_storage___comm_pack_over_identical_arrays_-__SymIdx_u049C4ABA60581057.exe`

The original next gate stopped at the first header fixture, as directed. The default-macro-state compile of `record-first.cpp` with C++17 exited 1 before producing an object. g++ 16.2.0 reported `mmeapi.h:498:22: error: field 'far' has incomplete type 'midihdr_tag'`, followed by an expected-semicolon diagnostic at `far`; the include stack is `windows.h` → `blade_private_spool.hpp:26` → `record-first.cpp:12`. The exact command was `g++ -std=c++17 -O2 -Wall -Wextra -I<checkout>/src/cpp -c <checkout>/bench/notes/round4/FINAL-sdk-hygiene-inputs/record-first.cpp -o <private>/01-record-first-default.o`. Its original logs remain historical. The later frozen header repair and successful retry are recorded below.

All commands ran from the integration checkout root with per-gate private `TEMP`/`TMP` beneath `bench/.build/FINAL-repair-runner`. The tested policy variables (`BLADE_PRINT`, FP/BLAS/OMP, tile/executable cache and F# test parallelism) were unset. Logs are preserved here:

- [Release build stdout](../../.build/FINAL-repair-runner/build/build.stdout.log), [stderr](../../.build/FINAL-repair-runner/build/build.stderr.log)
- [Raw generated Surface stdout](../../.build/FINAL-repair-runner/surface/surface.raw.stdout.log), [stderr](../../.build/FINAL-repair-runner/surface/surface.stderr.log)
- [Setup stdout](../../.build/FINAL-repair-runner/test-setup/stdout.log), [stderr](../../.build/FINAL-repair-runner/test-setup/stderr.log)
- [Surface stdout](../../.build/FINAL-repair-runner/test-surface/stdout.log), [stderr](../../.build/FINAL-repair-runner/test-surface/stderr.log)
- [Arity stdout](../../.build/FINAL-repair-runner/arity/stdout.log), [stderr](../../.build/FINAL-repair-runner/arity/stderr.log)
- [First header compile stdout](../../.build/FINAL-repair-runner/header-objects/01-record-first-default.stdout.log), [stderr](../../.build/FINAL-repair-runner/header-objects/01-record-first-default.stderr.log)

| Artifact | SHA-256 |
|---|---|
| Build stdout | `1FA89819B3E7354CCA3F0DB815A51934C93ACB4EBFDFE629CE427EEC6B629883` |
| Build stderr | `E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855` |
| Raw Surface stdout | `ADBBC782D5874F8AF1D33C048E9A1FBCBC51DEFDF000D8CFBC948A963C6296F3` |
| Generated `protocol/surface.json` | `C233ED810A6FD42CBFA70B273E4768761E45D6BCE83E7B3E67F5E51BD84AD3A8` |
| Setup stdout | `242F1A8FE7BEDC080CB9E8FD74708EAC04513F37FEBF18B6EB81BA502FD1F9C1` |
| Surface stdout | `A4F222983BA8F034A5E99017A49ED33322903337A9FC78C2932B3F3F04A90FF8` |
| Arity stdout | `4650B8110417A1FF17BE3EAC291DF4E532D98D99626171B927B4957BF38F0687` |
| First header compile stderr | `6EBB4A506C0700199F05A0C2F7753CBD451639106384958017A106F00A2B2D60` |

## Reviewed header retry, D06 protocol and exact corpus bridge

The reviewed integration freeze was `2a11bb5f900acc1e8d99e0b7702bda46e2d9d563`; all nine entries in `FINAL-sdk-hygiene-source-hashes.txt` matched. The two SDK headers changed after the earlier compiler build, so I copied only those reviewed headers to the existing fresh Release deployment and verified source/deployed identities: run-record `D237329E53EF3077B19A905A23D5BEE374D8D2472145360BD66F359B60245BF7`; private-spool `E05E50AA09D679CD86B7F193BEF879BDEDDCBD029A484F3654223DA116D919B1`. Blade.dll stayed `5BD62B4B231CBC0DE863E0BD2FE66D99E196F938577EB10E7A580D16F2E39595`; Blade.exe stayed `CE8899D60523D7D9142B4AACC0FF1FEE21EE19E422B63A4D3774AD4658C9413B`. No compiler rebuild was needed for the header-only repair.

All eight requested C++17 object modes passed: record-first and spool-first, each with default macros, `NO_STRICT`, predefined sentinels, and both defines. The four record-first objects are in `header-objects-retry1`; the four valid spool-first objects are in `header-objects-retry2`. A malformed PowerShell argument-flattening attempt between those batches is preserved in retry1 and excluded from the eight successes. Each valid compile used `g++ -std=c++17 -O2 -Wall -Wextra -I<checkout>/src/cpp -c <fixture> -o <private-object>` and exited 0 with empty stdout/stderr. The record-first objects are 4,461 bytes (SHA-256 `4EC14A41BE2C386916E635ADE66471D508E0A5A3FBEBB971F023A2930CFF860B`); spool-first objects are 4,459 bytes (SHA-256 `F9FFF29811B0FA26CBEF27B402BD5A2FAA79CAD87ABCC83B8438DEF80F6AE353`).

The moved `tests/runtime/private_spool_tests.cpp` (SHA-256 `F18CE2F8B7EAA131DDBBBD02B41D04EA2C0AB86967D256D1BE711FAE20B464CB`) was compiled alone using ucrt64 g++ 16.2.0 with `-std=c++17 -O2 -Wall -Wextra`, then run once against a fresh existing private ASCII scratch root. The harness exited 0: 256 passed, 0 failed, 1 skipped. The sole skip was the documented Windows directory-symlink privilege probe. The scratch root had zero remaining entries after execution. Its executable SHA-256 is `1A82D206D458A729D92F2CDE48B3D84E2FDB5239F1CDF6B2B8BC73E95DDF369D`.

The compiled `run-targeted.fsx` bridge then ran against the same DLL after deployed-header verification. It executed exactly the three pinned corpus programs and DiagnosticsCore: corpus 3/3 passed with value pins; DiagnosticsCore reported 37 passed, 0 failures, 0 skips; process exit was 0. The FSI output identifies the DLL and both deployed header hashes. No standalone Zarr, broad category, or second full-suite run was launched.

Full logs and provenance are under [FINAL-repair-runner](../../.build/FINAL-repair-runner): [D06 compile stdout](../../.build/FINAL-repair-runner/d06-retry1/compile.stdout.log), [D06 compile stderr](../../.build/FINAL-repair-runner/d06-retry1/compile.stderr.log), [D06 run stdout](../../.build/FINAL-repair-runner/d06-retry1/run.stdout.log), [D06 run stderr](../../.build/FINAL-repair-runner/d06-retry1/run.stderr.log), [D06 run provenance](../../.build/FINAL-repair-runner/d06-retry1/run.provenance.txt), [FSI bridge stdout](../../.build/FINAL-repair-runner/bridge-retry1/fsi.stdout.log), [FSI bridge stderr](../../.build/FINAL-repair-runner/bridge-retry1/fsi.stderr.log), and [FSI bridge provenance](../../.build/FINAL-repair-runner/bridge-retry1/fsi.provenance.txt). D06 stdout SHA-256 is `8A2A86706BC775A2400854BA5AC62F9179C87781773865FD16BB098F49A9DDAE`; FSI stdout SHA-256 is `268CACBC2675CF07FAC9B5560E38913F18BAA46700D090B0F8BAF54E50E65CC5`.
