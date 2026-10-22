# B03 design disposition

Final disposition: accepted and integrated as `307ba755`, source `a7048cd2`.
All six build modes, disabled/zero preprocessing, C++17 syntax and 45 fresh
scenario processes passed (494 assertions). Six ASan positives passed (81
assertions); LeakSanitizer explicitly reported unsupported and is skipped.
Root inspected full scenario/sanitizer logs and the observer, OOM and sticky
invalidity JSON witnesses. See [B03-tests.md](B03-tests.md). The earlier design
and source-review stages below are retained as review history.

2026-10-22. Root reviewed B03-design.md
(`7A100C38B102B1D5E6C9B08D3BD6CDEAD02322A9878DF7EA1B6CC1863BAE783D`)
and the standalone pool-capacity probe specification
(`4836B7DF19BC859DE414AA331A0366A7BA7C40B5B8FB8C55205318AE1F6C52AF`).
At that design stage, approved for one implementation checkpoint only.

The admitted metric is coherent API-accounted element-slot capacity at the
C++ blade_pool boundary. Active, retained and combined capacity share one
transaction; their separate peaks cannot be added. A combined-peak witness
records its actual component state. Native operations can overlap outside
the ledger lock, so the API event peak is explicitly not instantaneous native
heap high water. Quiescent snapshots support the deterministic current-state
checks under the existing matching count/type API contract.

The design avoids a global pointer registry, preserves sanitizer visibility,
keeps native allocation/deletion/TLS registration/reporting outside the lock,
and compiles all diagnostic work out when disabled. Failed allocations add no
successful capacity. Known unknown-size releases invalidate completeness;
reported capacity becomes unmeasured rather than falsely precise. Null deletes
must not create an unknown-capacity event. Unrecoverable empty higher-rank
sentinels remain outstanding, with no forged closure or leak repair.

Scope excludes pointer skeletons, provider chunk buffers, library workspaces,
LLVM, allocator cookies/slack, and physical memory counters. Existing cache
policy and legacy statistics remain unchanged. The implementation may add only
gated runtime helpers, a standalone probe and documentation. No compiler flag,
runtime policy, last-use optimization or general allocator redesign is admitted.

The probe specification supplies independently derived serial transitions,
OOM/constructor failure behavior, unknown-release/overflow invalidity, coherent
thread/cross-thread/exit cases, cache-off and sanitizer controls. Root must read
the actual diff and frozen hashes before Luna executes any of them. The full
suite remains reserved for the end of the round.

## Actual diff review

Root subsequently read the complete conditional runtime diff, gated helper and
standalone probe from handoff `08F0486DA1C1257BFCCA39E4501806BF356C2BFC9E0AE952E0BA39172AB3CDB8`.
The production hooks preserve native behavior and legacy statistics. Multi-field
updates use one locked proposed state, failed transactions invalidate completeness,
and the nullable snapshot suppresses stale numeric results after invalidation.
Native new/delete and Reaper registration remain outside the lock. Null bypasses
do not invalidate, and actual unknown-size bypasses do.

The helper is source-tree/standalone only in this card. The default generated
program preprocessor path has no helper dependency; instrumented deployment is
not packaged or claimed. Runtime-header deployment would need separate work.

One probe correction is required before execution: observer() can currently
perform zero concurrent observations if workers finish before its loop begins.
The final totals still test accounting, but cannot establish the named observer
claim. B must add bounded synchronized in-progress observation and explicitly
assert its count, without sleeps or overstating nondeterministic stress evidence.
No other production blocker was found in this read. Root reviewed the corrected
observer in handoff `6290B90CCA0CEB191A90D391CF93E7889A01824350EE98FDFC576F42A68FA2EB`:
four two-phase checkpoints hold both workers' pools active until each required
snapshot completes. The test asserts all four observations and the independent
final event/capacity totals. Optional stress observations are separately counted
and may be zero; they are not claimed as guaranteed simultaneous mutation.
Production headers are unchanged. The corrected freeze is approved for Luna's
compile/preprocessor/native/sanitizer validation, which has not yet run.
