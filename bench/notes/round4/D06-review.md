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
