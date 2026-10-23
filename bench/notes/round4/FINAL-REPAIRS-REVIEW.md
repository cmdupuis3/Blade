# Repairs found by the single final suite

The full suite launched once at ee4b1a0b with compiler DLL5FF48FB54403770F5E5CE27777F3D4584B12ABF53273AEE324E976571579AE57. It finished with8279 passes,11 failures and1 skip (GRDIR unavailable), exit1. Its first pipeline block reports3033/8/0; later diagnostics and surface checks add three failures. This review does not relabel that invocation as green. Repairs were prepared in separate lanes; no integration source/test changes occurred during that invocation.

## Native artifact filenames

Five arity fixtures failed before program start because their expected executable paths did not exist. Root read the actual process-start errors; C traced generated source basenames containing U+2014 to executable basenames containing U+FFFD. Paths are below172 characters. Build.fs, Runner.fs and all five corpus inputs are byte-identical to master361504b6, so this is an existing filename/toolchain boundary defect, not evidence of a kernel value failure.

Reviewed repair ad8b8447 preserves the existing ASCII sanitizer exactly. Only Unicode names receive an ASCII projection and a fixed-size suffix derived from the original UTF-8 spelling. Displayed names and corpus bytes stay unchanged. The digest distinguishes readable-projection collisions without claiming mathematical injectivity. Root corrected two initially mistyped ASCII test expectations before freezing. Sixteen setup assertions and the five original arity failures require targeted validation, including actual executable existence and all original pins.

## Windows header macros

Three failures are introduced by the SDK include in blade_run_record.hpp: libloaderapi.h reaches minwindef.h, which defines far/near/pascal. The generated programs use far and pascal as ordinary Blade names; the C++ diagnostics show those tokens disappearing or expanding to a calling-convention attribute. Master used a manual declaration instead of that SDK include. Root read the exact errors and both include-boundary repairs.

Reviewed repair d8b2e521 preserves each macro's prior defined/undefined state around both SDK includes, in run-record and private-spool headers. It changes no kernel algorithm, user identifier or global compiler flag. Eight C++17 object-only controls cover both header orders, undefined/predefined macro states and STRICT/NO_STRICT. The three original corpus failures require all-pin native reruns; the bounded spool protocol must still pass. Frozen B07 diagnostic headers remain untouched.

## Standalone test placement

The diagnostics safety scan deliberately inspects runtime C++ sources for hidden panic calls. It correctly encountered the standalone protocol harness's deliberate panic probe because that harness was placed under src/cpp. Root read the scan and retained its complete scope and anti-vacuity checks. d8b2e521 moves the harness byte-identically to tests/runtime/private_spool_tests.cpp; Git detects a100% rename. Its SHA-256 remains F18CE2F8B7EAA131DDBBBD02B41D04EA2C0AB86967D256D1BE711FAE20B464CB. No production panic path or diagnostic guard changes. DiagnosticsCore and the moved protocol harness require targeted validation.

## Validation boundary

After the original full invocation terminated, both reviewed repairs were integrated as f7c3d76b and98ed6d77. Two final Surface failures also require the renderer's current protocol/surface.json and a knowledge-base entry for registered BL8016. Root added that entry with its exact registry title and existing PrivateSpoolTests example in92da3d80; the sole runner is authorized to regenerate the surface from the fresh compiler using ide surface, UTF-8 without BOM and an LF terminator.

Build once and run only setup/filename, Surface, affected arity, header-order, protocol, three original macro cases and DiagnosticsCore checks. Preserve original failure logs and the one-launch marker. No second full suite is authorized. Append actual results and final disposition after those gates complete.

## Header-order correction

The first record-first/default C++17 object control failed in mmeapi.h498: the earlier libloaderapi include had set minwindef's guard, then the restored undefined far token remained undefined when windows.h subsequently parsed MIDIHDR. The runner stopped immediately; no other pending gate ran. Root independently read the installed minwindef definitions and the two revised include boundaries.

Integrated2a11bb5f now supplies the SDK-compatible far/near/pascal definitions inside each saved macro scope, including when minwindef is already guarded. Each prior caller definition is restored afterward. The fixtures and their after-each-include checks are unchanged. The same eight objects, protocol harness, three corpus cases and DiagnosticsCore are authorized; only the changed runtime headers need redeployment, and the validated compiler binary is unchanged. This is a correction to the repair, not an additional kernel. Prior failed evidence remains intact.

Root also inspected the generated Surface diff: its only change is the registered BL8016 entry. Setup43/0, Surface7/0 and arity79/0 are accepted, with all five originally missing executables now present under ASCII digest-suffixed paths. These gates need no repetition for a C++ header-only correction.

## Final acceptance

All eight intended header objects compiled successfully. Four record-first modes passed on retry1 and four spool-first modes on retry2. A malformed runner argument-table invocation between them is retained and excluded from the eight accepted checks. The source fixtures were not weakened or changed.

The relocated protocol harness compiled and passed 256 checks with zero failures and the documented Windows directory-symlink privilege skip; its owned scratch directory was empty afterward. The exact three formerly failing corpus programs then passed compile/run/all-pin checks, followed by Diagnostics Core 37/0. Root read the actual logs, including the unchanged safety scan covering 33 runtime files, and the successful exit provenance. The bridge used the previously validated DLL 5BD62B4B231CBC0DE863E0BD2FE66D99E196F938577EB10E7A580D16F2E39595 with both deployed headers matching their reviewed source hashes.

All eleven original failures have targeted passing evidence. Repairs are accepted; no unresolved required repair remains. The original full-suite 8,279/11/1 result and launch count one are preserved. See FINAL-repair-tests.md for commands, exact artifact identities and retained initial failures.
