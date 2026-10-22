# D06 standalone private-spool harness validation

The fixture-only repair passed its single bounded Windows harness run. The run reported **256 passed, 0 failed, 1 skipped**, exit 0. The sole skip was the expected directory-symlink privilege probe. The previous failed run remains recorded below with its original logs. The repaired run left the fresh scratch root empty. No provider, compiler emission, full-suite, benchmark, disk-exhaustion, or memory-exhaustion validation was performed. Full-suite launch count remains 0.

## Identity and pins

- Checkout: `C:\Users\cdupu\Documents\GitHub\Blade\.round4-worktrees\d-streaming`, clean at `6afc9fbae92d758239f45637bb443e45ec2f3428`.
- Final repair handoff SHA256: `00B61FEE129B8C8AEE5130448238B8F4D73A26FE4517E1E0543B74323711C62D`.
- Revised harness `src/cpp/private_spool_tests.cpp` SHA256: `F18CE2F8B7EAA131DDBBBD02B41D04EA2C0AB86967D256D1BE711FAE20B464CB`.
- Protocol SHA256: `A63006BB84800548E3D91242695C78C263AC18039F5D7E8A0A9447428B0BD308`.
- Unchanged production pins matched: `Blade.fsproj` `0F35B0948E914597FB813CDAFFFF326AF6FA9711974A54A01DA6ECC18ADA1224`; `blade_runtime.hpp` `DAB931642C6936222AB91EC888DBDDD52A553EC1D327BE1EB6E275B678C079DE`; `blade_private_spool.hpp` `BCDD68EB1957ECE5846F1F12AD3439BA652C8613F7A498C763346E58DDECDDB9`; D06 census `0678D6D66C0EDA345BC62B1F85BDAD5A9E841E62D6FE394199EA2A6D5C230E3A`.
- All four unchanged D05 guards matched: `Types.fs` `78E7C0BD662B7EEA7640FDA0E39C8891A8968A37A788C45CFB7130D0A340AE3A`; `TypeCheckInfer.fs` `DE01C2129AFD253BA53998EE78E964AA1DE2709E708A1AFDE7AB7E2AD3A195D3`; `CodeGenCuda.fs` `467C733B3F95D321ADB17E271C6E5110D7DC97E7549F4EBA08D674F67E09EF5C`; `ZarrTests.fs` `071018D5D171C7ABDB1A4A8FD7ED8D36E968ED16EEB123A93404976C0C6F795A`.

## Repaired gate

No Release rebuild was requested because the repair changed only the harness and docs; the prior Release build remains the production build evidence. The earlier build was successful with 4 `NU1900` warnings, and the D06 production header/runtime/deployment pins above are unchanged.

From the checkout root, with `TEMP` and `TMP` both set to `bench/.build/D06-runner-repair/tmp`, only the harness was recompiled:

`C:\msys64\ucrt64\bin\g++.exe -std=c++20 -O2 -Wall -Wextra -Isrc/cpp src/cpp/private_spool_tests.cpp -o C:\Users\cdupu\Documents\GitHub\Blade\.round4-worktrees\d-streaming\bench\.build\D06-runner-repair\private_spool_tests.exe`

Compilation exited 0 with no diagnostics. Compiler: MSYS2 ucrt64 g++ 16.2.0, executable SHA256 `58EFE82FA3CE35005F89D46FE439D7EE54E6889CB7DB894019202DA691A529A5`. Harness executable SHA256: `D635EE678936246C9F8FE6B3A8FCDD72406D43F1035938BAEEA4E5FEC510D83C`.

The harness ran once with the fresh, existing, empty ASCII local scratch root `C:\Users\cdupu\Documents\GitHub\Blade\.round4-worktrees\d-streaming\bench\.build\D06-runner-repair\scratch` (106 characters):

`C:\Users\cdupu\Documents\GitHub\Blade\.round4-worktrees\d-streaming\bench\.build\D06-runner-repair\private_spool_tests.exe C:\Users\cdupu\Documents\GitHub\Blade\.round4-worktrees\d-streaming\bench\.build\D06-runner-repair\scratch`

It exited 0 with **256 passed, 0 failed, 1 skipped**. The exact skip was `directory symlink creation unavailable; reparse root live probe`; this is the authorized Windows privilege-dependent probe. Stdout contained no other `SKIP` markers and stderr was empty. The scratch root had zero entries after the run, including after all injected faults. This validates the bounded harness fixtures only; it makes no disk-capacity, memory-capacity, provider, or performance claim.

## Repaired-run logs and provenance

All files are under `bench/.build/D06-runner-repair` in the frozen D06 checkout:

- `native-compile.log` SHA256 `E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855` (empty; no diagnostics).
- `native-compile.provenance.txt` SHA256 `7C079E3A9DDCAA80C416203589F9CEE812ED0833035F2603846D2F010DDAEEA5`.
- `native-run.stdout.log` SHA256 `8A2A86706BC775A2400854BA5AC62F9179C87781773865FD16BB098F49A9DDAE`.
- `native-run.stderr.log` SHA256 `E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855` (empty).
- `native-run.provenance.txt` SHA256 `F5A6EC73B98721C4D5CF69C1A426D64854A3ED5664790C5B2D79C008F5ECFB0F`.

Provenance records the checkout CWD, private temp paths, exact tool/version/argv, source and production-header hashes, executable hash, scratch-root path and pre-run empty state, output paths and exit codes.

## Earlier failed gate retained

The first D06 native gate at commit `2ea43cbc0070dc9d2462b4641e33b96ba04853d4` reported **242 passed, 11 failed, 1 skipped**, exit 1. The failures began with two MoveFile-based missing/replacement fixture setups and cascaded through unchecked dependent steps. No failing MoveFile native error was captured, so an ancestor-sharing explanation remains an inference. The fixture-only repair switched to verified-handle disposition, proved the replacement file identity differed, and checked that runtime cleanup refused it while preserving owner evidence before harness cleanup. The original attempt is preserved unchanged under `bench/.build/D06-runner`, including `release-build.log`, `native-compile.log`, `native-run.stdout.log`, `native-run.stderr.log`, and provenance. It is historical and superseded for the repaired harness gate; it is not erased or counted as the repaired result.