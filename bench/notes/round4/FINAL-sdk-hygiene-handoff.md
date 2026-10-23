# Final-suite repair: SDK macro containment and standalone harness placement

UNRUN source preparation only. No build, emit, compile, execution, tests,
benchmark, commit or integration mutation. Base lane HEAD615cd2202489c26e4045feff56cf2c08f8a15317.
The initial integration full suite remains untouched; root integrates only after
it finishes. No kernel, naming policy, arithmetic or language change.

## Static diagnosis and exact repair

All three reported compile errors are introduced by D08 commit6095ffc7, not
master361504b6. The three corpus sources are byte-unchanged from master:

- functions/194_late_broadcast_over_empty_array.blade: generated CPP609
  declares Array<double,1> far.
- rand/018_poisson_large_lambda.blade: generated CPP233 declares far.
- recursive-arrays/008_nested_dp.blade: generated CPP228 declares pascal.

Every failed CPP includes blade_run_record.hpp at24; none includes the private
spool header. The always-used run-record header now includes libloaderapi.h,
which includes minwindef.h at10. The installed SDK minwindef.h69-78 undefines
then defines far/near empty and pascal as __stdcall. This exactly explains the
missing identifier and unexpected attribute errors. Master used a manual
GetModuleFileNameA declaration and did not import these SDK macros. The current
SDK declaration remains necessary for accepted include-order ABI compatibility.
Source/deployed header hashes matched while diagnosing.

Both SDK include boundaries now save and restore far/near/pascal via
push_macro/pop_macro, preserving the caller's defined OR undefined state:

- src/cpp/blade_run_record.hpp around libloaderapi.h.
- src/cpp/blade_private_spool.hpp around windows.h, covering policy-bound
  programs as well as ordinary ones.

No ABI, runtime protocol, grant, format, quota, cleanup or provider change.
No identifier renaming or blanket undefining of caller macros. Other frozen
runtime/B07 headers remain untouched.

The ninth reported failure is the existing panic-containment scan correctly
including src/cpp/private_spool_tests.cpp's deliberate panic fixture. The
standalone harness is now tests/runtime/private_spool_tests.cpp, byte-identical
SHA256 F18CE2F8B7EAA131DDBBBD02B41D04EA2C0AB86967D256D1BE711FAE20B464CB.
No project/deployment reference required updating. The production scan and
its assertions are unchanged. D06-protocol.md carries current path/C++17
instructions; historical D06/D08 reports and hash manifests stay unchanged.
This handoff supersedes their old harness source-path commands.

## Authored bounded verification assets

FINAL-sdk-hygiene-inputs/record-first.cpp and spool-first.cpp each check macro
state after EACH include, using the intentionally repeatable check-state.hpp.
Compile each with macros initially undefined and with predefined numeric
sentinels, under default STRICT and NO_STRICT: eight C++17 object compiles.
Undefined cases also declare/use ordinary far, near and pascal locals;
sentinel cases prove original replacement values survive. Both invoke the
existing public run-record and spool APIs, retaining ABI/include-order checks.

run-targeted.fsx invokes the existing full corpus runner on exactly the three
failed corpus sources, including all EXPECT/warning pins, then DiagnosticsCore
only if all three pass without skips. It uses the established FSI base-directory
override, prints DLL/header identities, restores host/CWD state, and accepts a
new private output directory. No new compiler or diagnostic API.

## Sole-Luna request after root review and initial full-suite completion

Verify FINAL-sdk-hygiene-source-hashes.txt; capture source HEAD/diff, current
tool/SDK identity, actual environment, CWD, private TEMP/TMP, commands and raw
stdout/stderr/exit using the established reliable capture wrapper. Preserve the
initial full-suite failure evidence. Do not run another full suite or Zarr
category as part of this targeted repair gate.

1. Fresh Release build/deployment after root integration; verify deployed
   run-record/spool header hashes equal source. Stop on unexpected failure.
2. Eight compile-only header checks (no executables/native execution). For each
   of record-first.cpp/spool-first.cpp, compile default, -DNO_STRICT,
   -DBLADE_MACRO_PREDEFINED, and both defines. Use private output objects:

       g++ -std=c++17 -O2 -Wall -Wextra -I<checkout>/src/cpp -c <fixture> -o <private-object>

3. Fresh bounded D06 regression from its NEW path:

       g++ -std=c++17 -O2 -Wall -Wextra -I<checkout>/src/cpp <checkout>/tests/runtime/private_spool_tests.cpp -o <private>/protocol.exe
       <private>/protocol.exe <fresh-existing-empty-ASCII-scratch-root>

   Existing native child helpers remain hidden/bounded. Expect256 passes,
   zero failures and the one previously accepted symlink privilege skip on
   this host; verify the owned scratch root is empty afterwards.
4. Run only the three all-pin corpus cases and DiagnosticsCore:

       dotnet fsi --reference:<fresh-Blade.dll> --exec <checkout>/bench/notes/round4/FINAL-sdk-hygiene-inputs/run-targeted.fsx <checkout> <new-private-output>

   Use the initial full-suite environment and known g++ host, not a changed FP
   policy; record actual values. Expect3/0/0 followed by diagnostics zero
   failures/skips. Retain emitted CPP/native outputs and diagnostics totals.
   Output path must be at most160 chars; record executable lengths below240.

## First fixture failure and revised freeze

The sole runner's first record-first/default C++17 fixture failed in SDK
mmeapi.h498: MIDIHDR contains a far pointer. The run-record include had set
minwindef's include guard, then correctly restored far to undefined; the later
windows.h include could therefore not reintroduce its required legacy macros.
No other gate result is claimed by this revision.

Both SDK boundaries now push the prior macro state, explicitly undefine the
three names, establish the installed minwindef definitions DURING the include
(far/near empty; pascal empty for _ARM_, otherwise __stdcall), and pop the exact
prior state afterwards. This also prevents predefined fixture/caller sentinels
from entering SDK parsing when minwindef is already guarded. Only the two
runtime headers changed; fixtures, bridge and relocated harness are unchanged.
The same eight compile-only fixtures remain the first retry gate; no broad
rerun is added. This revision was statically prepared without compilation/run.

Root reviews targeted evidence before deciding any remaining validation.
