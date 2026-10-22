# D06 review: private sequential spool prerequisite

Root accepts the source/design census with SHA256
`048FED9F9611975B8F07AC832F12608EE2348F6FDE0B9123F80EF9065D174E8D`.
The statistical-barrier witness requires computed values to survive a first fold
before a second pass; rereading a mutable provider is not equivalent. This is a
justification for exact private replay, not an admission to omit its resident
intermediate yet.

The next bounded checkpoint implements the runtime protocol and focused harness:
an explicit typed grant, checked disk/workspace bounds, fixed metadata, exact
floating-point bits, versioned integrity, exclusive ownership, one sealed replay,
cleanup and explicit conservative recovery. No CLI policy, compiler transformation,
provider integration, default disk use, or measured memory claim is included.

Root inspected the runtime's eight-slot failure-hook registry and `_Exit` path.
Registration currently drops exhaustion silently; the new facility needs a checked
registration path while preserving existing callers. Panic cleanup cannot rely on
destructors or atexit, allocate, throw, or replace the primary failure. The header
must return structured errors rather than call panic behind the compiler's shadow
frame analysis. Active cleanup state must outlive callbacks.

Recovery must recognize its own versioned manifest, acquire the ownership lock,
and prove the recorded process incarnation is dead. Unknown identity, unsupported
platform proof, foreign paths, or uncertain liveness retain the files. There is no
age sweep. Tests use tiny injected partial/zero writes, quota and lifecycle failures;
they must not exhaust real disks or allocate enormous witnesses.

The future compiler route remains separately gated on whole-program demand,
continuous serial fold association, original provider validation/failure precedence,
and bounded segmentation metadata. In particular, D01's whole-read missing tally
cannot substitute for the existing per-window validation of this proposed route.

Only Luna executes the frozen protocol tests after source review. No full suite
has been launched.

## Implementation review before the first execution gate

Root read the protocol header and complete standalone harness. The first draft
deleted its in-directory manifest before late lock/directory cleanup, which could
lose recovery evidence. It also closed the verified directory handle before a
path-based removal. That draft was not sent to the runner.

The revised protocol retains an exclusive nonce-derived `.owner` sidecar under
the protected root. It deletes the known data file and empty run directory through
verified handles, confirms directory retirement, and retires the owner sidecar
last. Explicit recovery accepts either a matching recorded directory or an
already-removed directory. Separate late-directory, manifest and owner-close
faults exercise retained evidence; a simulated close failure after successful
retirement must report absent scratch rather than invent retained evidence.

The runtime gate covers this protocol only. The logical quota includes serialized
file bytes, not filesystem allocation units or process memory. Windows ownership
support is bounded to canonical ASCII fixed-drive roots; other platforms decline.
The harness uses tiny payloads, injected faults and bounded hidden child processes.
Root found no additional blocker in the reviewed revision; exact freeze hashes,
compilation and native results remain required before acceptance.

## First native gate and fixture repair

Release build and standalone compilation passed; the first harness run reported
242 passed, 11 failed and one unavailable symlink probe. Root read the complete
log. The failures began in two rename-based fixture setup operations and then
cascaded; their native errors were not captured, so the precise cause is not
established. The lifecycle/recovery checks passed and the final scratch root was
empty. The failed run is retained in D06-tests.md.

Root reviewed the fixture-only repair `6afc9fba`. It deletes the exact original
file by verified handle, tests the missing path, and separately creates a proven
different file identity. Production cleanup must refuse that replacement and
retain ownership evidence before the harness removes its own verified file.
Failed preconditions stop the fixture with native diagnostics. Production header,
hook implementation and deployment remain byte-identical; no protection is relaxed.

Final repaired handoff SHA256 is
`00B61FEE129B8C8AEE5130448238B8F4D73A26FE4517E1E0543B74323711C62D`;
harness is `F18CE2F8B7EAA131DDBBBD02B41D04EA2C0AB86967D256D1BE711FAE20B464CB`.
Approve recompilation and one fresh bounded harness run, without a redundant
Release build. This repair is not yet a passing validation result.

## Accepted runtime prerequisite

The repaired gate passed 256 checks, failed none, and skipped the unavailable
Windows directory-symlink privilege probe. Root read the actual repaired identity,
cleanup and late-exit/recovery results, checked stdout SHA256
`8A2A86706BC775A2400854BA5AC62F9179C87781773865FD16BB098F49A9DDAE`,
and confirmed the scratch root was empty. The original failed run remains retained.
No production fix was needed after the first source freeze.

Accept and integrate the runtime checkpoint as `02325693` plus its fixture repair
`fafd232a`. This closes the explicit-grant, bounded Windows protocol prerequisite
only. The source omits no Blade intermediate; compiler admission, original source
validation/failure precedence, user-visible resource policy and actual pipeline
storage evidence remain separate work. No speed or process-memory claim is made.
