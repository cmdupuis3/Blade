# D06 runtime protocol frozen handoff

Candidate checkpoint for root review and the sole Luna runner. Base `029448fe4b74de57b75ed50afd2755661dca93a1` on `codex/round4-d-streaming`. Root reviewed the runtime and standalone harness. The first native gate passed build/compilation but found fixture failures; the revised fixture-only gate is pending. This handoff belongs to the runtime prerequisite only, not compiler admission.

## Exact scope

- `src/cpp/blade_private_spool.hpp`: explicit typed grant; checked workspace/logical disk quota; fixed-width little-endian IEEE bits; versioned header/block/footer plus CRC64; one stable bounded owner and move-only writer/sealed/reader; checked native transfers, seal/flush/close/reopen/integrity; normal/exit/failure cleanup; explicit Windows ownership recovery.
- `src/cpp/blade_runtime.hpp`: checked `try_on_failure_exit` registration. The existing void `on_failure_exit` API and existing caller behavior are retained.
- `Blade.fsproj`: deploy the new runtime header only. The standalone source harness is deliberately NOT copied to the output directory.
- `src/cpp/private_spool_tests.cpp`: dedicated bounded standalone protocol harness; no compiler/provider emission and no F# category registration.
- `bench/notes/round4/D06-census.md`: the previously approved source/design checkpoint; content unchanged, with new-file line endings normalized at freeze.
- `bench/notes/round4/D06-protocol.md`: final API/resource/format/lifecycle/runner specification.
- `bench/notes/round4/D06-handoff.md`: this freeze record.

No default disk use, CLI/environment option, compiler omission/admission, provider integration, public sink, caching, prefetch, source replay or immutable-source claim is introduced. D05 sources/docs and all F# production emitters remain unchanged. The prospective provider drain/failure-precedence policy remains design only.

## Reviewed lifecycle correction

A fixed nonce-derived `<run-name>.owner` sidecar under the declared root is held exclusively as BOTH manifest and ownership lock. The run directory contains only `values.bin`. Data deletion and empty-directory deletion use verified handles; production code never calls path-based RemoveDirectory or recursive deletion. Bounded enumeration refuses unexpected entries and never chooses deletion paths.

The sidecar remains readable through data, directory, root-close and pre-disposition owner failures. Run-directory absence is checked before root/buffer retirement; the sidecar's handle is marked/closed LAST. Recovery can therefore finish a recognized sidecar whose recorded run directory has already been removed; if present, the directory must match its recorded file identity. Root/path/reparse/manifest/lock/liveness uncertainty retains evidence.

Distinct late-stage child faults cover directory removal, manifest disposition, owner-lock close before deletion marking, and close-after-actual-success. The first three assert readable sidecar evidence and recovery after a real child has exited. The last reports a close error with scratch absent and reservation zero; it does not claim a nonexistent retained manifest. A genuine final owner close failure after deletion marking can leave only a delete-pending handle, which OS teardown may retire; the protocol states this precise limit. PID reuse is admitted for recovery only when the recorded process-start identity differs, proving that old owner incarnation dead; a live matching PID/start pair retains.

## Capability and bounds

The file/ownership route supports native Windows on an IEEE754 little-endian binary64 host, existing canonical ASCII fixed-local-drive roots, non-reparse paths and nonzero file identities. Full path buffers are capped at 240 bytes. Other platforms return unsupported without disk operations; no portable-support claim is made.

One active intermediate has one `8*B` buffer plus `sizeof(Control)+4096` fixed metadata capacity, with B capped at 1,048,576. At most FOUR library native handles are live, including protected root/run directory handles, the owner-sidecar handle and the transient data/process/find handle. Caller buffers, Windows internals and process RSS are outside this tracked capacity claim.

Quota is explicitly LOGICAL SERIALIZED FILE BYTES: `72 + 64*G + 8*N + 64 + 128`, G computed without overflow. It covers format bytes and the owner sidecar. Filesystem directory/allocation metadata, physical block rounding and kernel buffering are excluded; no physical disk reservation or shared-root aggregate quota is claimed. All real write/flush errors remain checked. No unbounded block catalog or per-block hook registration exists. No performance claim is made.

## Authored gates; revised fixtures unrun

The harness covers metadata-only arithmetic overflow/quota bounds; small zero/one/odd-tail exact-bit round trips (largest N=257); CRC/shape/version/axis/nonce/sequence/origin/length/footer/trailer/truncation failures; move/state identity; partial/zero/error transfers; allocation/flush/close/cleanup faults; missing/replaced replay file identity; exception/cancel cleanup; unexpected-entry retention; checked hook exhaustion/registration retry; hidden bounded child return/exit/panic/forced orphan/live-owner/kill cases; malformed/root-mismatched/live/PID-reused/uncertain ownership; and late-stage evidence/recovery.

Initial sole-runner request (completed; failed native gate recorded below). The revised request at the end supersedes this list:

1. Verify these hashes. Build Blade.fsproj Release once to validate deployment and unchanged F# surface.
2. Compile ONLY `src/cpp/private_spool_tests.cpp` directly using campaign ucrt64 g++, C++20, `-O2 -Wall -Wextra -Isrc/cpp`, into a private D06 runner directory. No compiler/provider emission is needed.
3. Run that one executable with one fresh existing empty ASCII fixed-local scratch root argument (Windows backslashes). Keep executable/root full paths short enough for the 240-byte bound. Capture full build/compile/run logs and exit code. All harness children use CREATE_NO_WINDOW and private child roots; parent waits are bounded and stalled children are terminated.
4. Require zero failures; stop unexpected build/compile/runtime failures for repair before continuing. Report the exact skipped suffix and any retained scratch evidence. Directory-symlink probe may skip when Windows denies creating a test symlink; non-Windows native protocol skips do not satisfy this acceptance gate.

Do not execute the huge metadata-only shapes, exhaust real disk/memory, run a provider/full suite, or benchmark this card. No tests/build/compile/emission/execution/benchmark were performed by coder. The failed first native gate is recorded below; no revised validation result is claimed.

## SHA256 freeze (working-tree content)

| File | SHA256 |
|---|---|
| Blade.fsproj | 0F35B0948E914597FB813CDAFFFF326AF6FA9711974A54A01DA6ECC18ADA1224 |
| src/cpp/blade_runtime.hpp | DAB931642C6936222AB91EC888DBDDD52A553EC1D327BE1EB6E275B678C079DE |
| src/cpp/blade_private_spool.hpp | BCDD68EB1957ECE5846F1F12AD3439BA652C8613F7A498C763346E58DDECDDB9 |
| src/cpp/private_spool_tests.cpp | F18CE2F8B7EAA131DDBBBD02B41D04EA2C0AB86967D256D1BE711FAE20B464CB |
| bench/notes/round4/D06-census.md | 0678D6D66C0EDA345BC62B1F85BDAD5A9E841E62D6FE394199EA2A6D5C230E3A |
| bench/notes/round4/D06-protocol.md | A63006BB84800548E3D91242695C78C263AC18039F5D7E8A0A9447428B0BD308 |

Unchanged D05 source guards:

| File | SHA256 |
|---|---|
| src/Types.fs | 78E7C0BD662B7EEA7640FDA0E39C8891A8968A37A788C45CFB7130D0A340AE3A |
| src/TypeCheckInfer.fs | DE01C2129AFD253BA53998EE78E964AA1DE2709E708A1AFDE7AB7E2AD3A195D3 |
| src/CodeGenCuda.fs | 467C733B3F95D321ADB17E271C6E5110D7DC97E7549F4EBA08D674F67E09EF5C |
| tests/ZarrTests.fs | 071018D5D171C7ABDB1A4A8FD7ED8D36E968ED16EEB123A93404976C0C6F795A |

Static diff review and whitespace checks only. All new files use LF in the index and CRLF in this checkout; the earlier census working-tree hash changed only with newline normalization. Commit identity and this handoff's own content hash are reported separately to avoid a self-referential manifest. Stop after this one checkpoint.
## Fixture repair after first native gate

Repair base: `2ea43cbc0070dc9d2462b4641e33b96ba04853d4`. Luna passed Release build and g++ harness compilation, then reported 242 passes / 11 failures / one directory-symlink skip. Every failure began with the two MoveFile fixture setups and cascaded through unchecked dependent steps; late cleanup/recovery gates and final empty root passed. Old logs/provenance remain untouched in bench/.build/D06-runner. No native error was captured for those old MoveFile calls, so the sharing/ancestor explanation remains an inference.

Only the harness and this handoff/protocol prose change in the repair. Native handle disposition replaces the fixture renames. The replacement fixture must prove a different file ID, then observe runtime cleanup REFUSE that replacement and retain its identity plus owner evidence BEFORE the harness unlinks its separately owned replacement. Missing/replaced original paths then use normal runtime cleanup without restoring a file or altering remembered IDs. Setup failures return immediately with bounded status/native-error diagnostics; pending ownership stops subsequent sections rather than cascading.

The runtime header, checked hook implementation, deployment entry, D05 sources and original failure logs are byte-for-byte unchanged. No runtime protection or sharing mode was relaxed. Revised sole-runner request: recompile ONLY the changed native harness, then execute only that bounded protocol harness in a fresh private root after root dispatch; no Release rebuild/provider/full suite/benchmark is required for this harness/docs-only repair. Stop unexpected failures for repair. Coder did not execute anything.
