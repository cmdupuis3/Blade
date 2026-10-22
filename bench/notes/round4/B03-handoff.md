# B03 implementation handoff: opt-in scalar-pool API capacity

Authored after root design approval; **unexecuted, frozen for root diff review**.
2026-10-22. Checkout `C:/Users/cdupu/Documents/GitHub/Blade/.round4-worktrees/b-measurement`,
branch `codex/round4-b-measurement`, base `2eb6c0ad`. No commit/integration by coder.

## One-card scope and actual diff

- `src/cpp/blade_pool_capacity.hpp`: completely gated allocation-free global ledger;
  checked transactional current/peak/witness/counter state, optional numeric snapshots,
  native-success/failure wrappers, test-only injection/invalidity seams. No registry,
  dynamic storage, output formatting or reset API.
- `src/cpp/nested_array_utilities.hpp`: conditional include/hooks in cache-on/off paths,
  typed native allocation/deletion, locked cache transfers, explicit non-null-only
  unknown-size invalidation at both null-extents teardown bypasses. Existing Stats
  definition/record, cache constants/eligibility/order/retry policy and Reaper unchanged.
- `bench/diagnostics/pool-capacity/probe.cpp`: standalone independently expected serial,
  exception, thread/cache-off/sanitizer/disabled/provenance witnesses; JSON sidecars.
- `bench/diagnostics/pool-capacity/spec.md`, `bench/notes/round4/B03-design.md`: approved
  contract and implemented scenario map. This handoff supplies frozen source identity.

No LLVM/provider/optimization/F# or project-file edits. Blade.fsproj explicitly deploys
headers; its list is unchanged. This opt-in pilot is **source-tree/standalone only**.
Normal generated programs preprocess the helper include out. Packaging/driver support
for instrumented deployed/generated programs requires a separate reviewed step.

## Measurement, validity and safety boundaries

Build opt-in is `BLADE_POOL_CAPACITY_DIAGNOSTICS=1`. No macro/zero means no ledger helper
include, state, hooks, locking or diagnostic work. The additional
`BLADE_POOL_CAPACITY_TESTING=1` controls the failure callback/invalidity seam; no reset.

Counts are actual API element slots (`cells*sizeof(T)`), including allocate's physical
one-cell sentinel, not new[] cookies, heap usable bytes or allocator slack. Both cache
branches and non-arithmetic types are covered **through the API**, subject to its existing
matching-count/type caller contract. No pointer registry masks ASan leak roots. Unknown
direct deallocation of actual non-null storage invalidates completeness; null deletion
does not fabricate unknown bytes. Higher-rank zero-leaf sentinels remain active because
the existing teardown cannot recover them. No fake closure or leak repair.

One allocation-free short lock serializes current A/R/A+R, independent peaks, the
combined-peak witness, counters, sequence and snapshot copy. It never covers native
new/delete, constructors/destructors, failure callbacks, Reaper registration or reporting.
In-flight native operations are exposed. Metrics are **API-linearized accounted element
capacity**, not an instantaneous physical native-live peak; concurrent native operations
can overlap outside the ledger event order. Physical-native live, logical liveness,
RSS/commit/DRAM, LLVM, pointer skeletons, provider buffers and library workspace remain
unmeasured/uncovered. Do not add component peaks into a false simultaneous peak.

Failure/overflow/underflow/unknown-release/unsupported-policy completeness is sticky.
Snapshots then have absent numeric values; JSON capacity and quiescence are **null**.
Numeric/in-flight bookkeeping stops after invalidation, so no stale final counts are
published. The original allocation/deletion/exception behavior continues. Native failed
attempts add no successful bytes; OOM flush/retry follows the existing source path once.
Throwing-destructor native-delete outcome is explicitly unknown and invalidates metrics.

Diagnostic static storage is reported separately; probe snapshot/report metadata and
runtime formatting are outside tracked bytes. Named snapshots are taken at explicit
probe endpoints, not an automatic process-exit closure. Later empty Reaper flushes are
not silently folded into earlier snapshot event counts.

Root-review observer strengthening: four mandatory two-phase live-allocation checkpoints
now hold both workers before their 1st/17th/33rd/49th releases. The observer asserts
exactly four samples and A=2B/R=0 at each; workers cannot skip those observations by
finishing early. Between checkpoints, bounded optional stress sampling may see zero
copies depending on scheduling; its count is printed separately and makes no guaranteed
simultaneous-native-mutation claim. Independent final totals remain 128 public successes,
two native successes, 126 hits, two evictions, PC=2B and joined A=R=0. No sleeps.

## Frozen source identities

Raw working-tree SHA-256 (not normalized Git blob hash):

| file | SHA-256 |
|---|---|
| src/cpp/nested_array_utilities.hpp | FA12D552F27D4757C73B764E384A3F883510BFDBE15655DA352CEE4E32146DB9 |
| src/cpp/blade_pool_capacity.hpp | F5E2A36C342352DD05A44D7307613A66011A4489B5CCCF1927E4C23A38ECA6D4 |
| bench/diagnostics/pool-capacity/probe.cpp | DB8C437A04DFD98689369602CC5816B9CC26855A3E4D67EEB3287BE2C82F58C3 |
| bench/diagnostics/pool-capacity/spec.md | DAF5920C9E38FFEEA95E0884B787BF147C6875DD138626904D1994B217FAD984 |
| bench/notes/round4/B03-design.md | 707FF9AEBCD5FBC2B574DF34A59C0D230893DD4DFE66800227C6C7BB57C96311 |

Static-only `git diff --check` is clean. Existing header preserves index LF / worktree
CRLF with a localized diff. No compilation, preprocessing, syntax check, native API,
probe, test, sanitizer, benchmark or oracle execution has been performed by the coder.
Everything below is a **request for sole Luna after root reviews the actual diff**.

## Runner compile/preprocess request

No Blade/.NET build or full/default suite is requested. Use the exact source-tree header,
not a deployed copy, and private **absolute** lane paths. Compile/execute serially; keep
all compiler commands/stdout/stderr and compiler version/executable hashes.

```powershell
$b03Repo = 'C:\Users\cdupu\Documents\GitHub\Blade\.round4-worktrees\b-measurement'
$b03Out = Join-Path $b03Repo 'bench\.build\B03-runner'
$b03Source = Join-Path $b03Repo 'bench\diagnostics\pool-capacity\probe.cpp'
$b03Include = Join-Path $b03Repo 'src\cpp'
$b03Gpp = 'C:\msys64\ucrt64\bin\g++.exe'
New-Item -ItemType Directory -Force -Path $b03Out | Out-Null
Set-Location -LiteralPath $b03Out
& $b03Gpp --version
& $b03Gpp -std=c++20 -O2 -pthread -DBLADE_POOL_CAPACITY_DIAGNOSTICS=1 -DBLADE_POOL_CAPACITY_TESTING=1 -DBLADE_POOL_CACHE_STATS=1 -I $b03Include $b03Source -o "$b03Out\diagnostic.exe"
& $b03Gpp -std=c++20 -O2 -pthread -DBLADE_POOL_CAPACITY_DIAGNOSTICS=1 -I $b03Include $b03Source -o "$b03Out\production-optin.exe"
& $b03Gpp -std=c++20 -O2 -pthread -DBLADE_POOL_CAPACITY_DIAGNOSTICS=1 -DBLADE_POOL_CAPACITY_TESTING=1 -DBLADE_POOL_CACHE_STATS=1 -DBLADE_POOL_CACHE_OFF=1 -I $b03Include $b03Source -o "$b03Out\cache-off.exe"
& $b03Gpp -std=c++20 -O2 -pthread -I $b03Include $b03Source -o "$b03Out\disabled.exe"
& $b03Gpp -std=c++20 -O2 -pthread -DBLADE_POOL_CACHE_STATS=1 -I $b03Include $b03Source -o "$b03Out\legacy-only.exe"
& $b03Gpp -std=c++20 -O2 -pthread -DBLADE_POOL_CAPACITY_DIAGNOSTICS=1 -DBLADE_POOL_MAX_BLOCK=1 -DBLADE_POOL_BUDGET=18446744073709551615ULL -I $b03Include $b03Source -o "$b03Out\unsupported-policy.exe"
& $b03Gpp -std=c++20 -E -P -I $b03Include $b03Source -o "$b03Out\disabled.ii"
& $b03Gpp -std=c++20 -E -P -DBLADE_POOL_CAPACITY_DIAGNOSTICS=0 -I $b03Include $b03Source -o "$b03Out\zero-defined.ii"
& $b03Gpp -std=c++20 -E -H -I $b03Include $b03Source -o "$b03Out\disabled-includes.ii" 2> "$b03Out\disabled-includes.txt"
& $b03Gpp -std=c++17 -fsyntax-only -pthread -DBLADE_POOL_CAPACITY_DIAGNOSTICS=1 -I $b03Include "$b03Repo\src\cpp\alloc_layout_tests.cpp"
```

Stop on a nonzero compile/preprocessor result; do not run an old executable. All are
unexecuted requests. The unusual policy is an isolated **negative admission probe**;
no production default/policy source changes. Exact default and cache-off bytes are pinned
by source-independent constants in the ordinary probe, not this negative policy.

Inspect both disabled preprocessor artifacts for **absence** of `namespace capacity_detail`,
`capacity_snapshot_v1`, `native_new_test_hook`, `kCapacityPolicySupported`, ledger state
and gate declarations. The disabled include trace must not include
`blade_pool_capacity.hpp`; no compiler optimizer is relied on to erase diagnostic work.
Production-opt-in must omit test-hook/overflow seam definitions. Confirm the normal
header's legacy Stats record text/meaning is unchanged. The C++17 syntax-only request
checks compatibility with the existing runtime test translation unit (probe uses C++20
barriers); it launches no allocation-layout test execution or Blade suite.

## Timeout-protected independent probe execution

Luna only. Set `$env:B03_REPO=$b03Repo` and `$env:B03_OUT=$b03Out`; then this authored,
**unexecuted** snippet reuses the existing parent process-capture/watchdog. It uses no
implicit cwd destination. Each scenario gets a fresh child and an absolute JSON path.

```powershell
@'
import hashlib, json, os, sys
from pathlib import Path
repo = Path(os.environ['B03_REPO']).resolve(strict=True)
out = Path(os.environ['B03_OUT']).resolve(strict=True)
sys.path.insert(0, str(repo / 'bench/harness'))
import run as h
def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()
sources = [repo/'src/cpp/nested_array_utilities.hpp', repo/'src/cpp/blade_pool_capacity.hpp',
           repo/'bench/diagnostics/pool-capacity/probe.cpp', repo/'bench/diagnostics/pool-capacity/spec.md']
identity = {'repo': str(repo), 'cwd': str(out), 'sources': {str(p): digest(p) for p in sources}, 'runs': []}
groups = {
 'diagnostic': ['small','null','zero','sentinel','strict-sentinel','direct','reuse','different-count',
                'slots','budget','unknown','unknown-strict','ctor','dtor','oom1','oom2','fail-small',
                'overflow','underflow','sequence-overflow','inflight-overflow','counter-overflow','product-overflow',
                'origin-exit','threads','retainer-exit','late','reentry','observer','semantic'],
 'production-optin': ['small','reuse','ctor','threads','null'],
 'cache-off': ['small','cache-off','origin-exit','null','fail-small','fail-eligible-off','ctor'],
 'disabled': ['semantic'], 'legacy-only': ['semantic'],
 'unsupported-policy': ['unsupported-policy'],
}
invalid = {'unknown':'unknown_release','unknown-strict':'unknown_release','dtor':'native_delete_exception',
           'overflow':'arithmetic','underflow':'arithmetic','sequence-overflow':'arithmetic',
           'inflight-overflow':'arithmetic','counter-overflow':'arithmetic','product-overflow':'arithmetic',
           'unsupported-policy':'unsupported_policy'}
for build, modes in groups.items():
    exe = (out / (build+'.exe')).resolve(strict=True)
    for mode in modes:
        tag = build+'-'+mode
        report = out / (tag+'.json')
        argv = [str(exe),mode,str(report)]
        result = h.run_proc(argv, env=h.make_env({}), cwd=out, timeout=30)
        (out/(tag+'.stdout.txt')).write_text(result.stdout, encoding='utf-8')
        (out/(tag+'.stderr.txt')).write_text(result.stderr, encoding='utf-8')
        item = {'argv':argv,'exe_sha256':digest(exe),'returncode':result.returncode,
                'timed_out':result.timed_out,'launch_error':result.launch_error}
        identity['runs'].append(item)
        (out/'runner-identity.json').write_text(json.dumps(identity,indent=2)+'\n',encoding='utf-8')
        assert not result.launch_error and not result.timed_out and result.returncode == 0, (tag,result.stderr)
        record = json.loads(report.read_text(encoding='utf-8'))
        assert record['checks'] > 0 and record['failed'] == 0, tag
        if mode in invalid:
            assert any(s['reason']==invalid[mode] and not s['valid'] and s['api_accounted_element_capacity'] is None
                       and s['quiescent'] is None for s in record['snapshots']), tag
        if build == 'cache-off':
            assert record['cache_on'] is False, tag
        print(tag, ':', record['checks'], 'assertions passed')
'@ | & 'C:\Python314\python.exe' -
```

Requested count is **45 fresh scenario processes** (30 diagnostic, 5 production-opt-in,
7 cache-off, 1 disabled, 1 legacy-only, 1 unsupported-policy). Assertion totals are printed
and must be recorded exactly by the runner; no pass total is claimed in advance. Root may
trim serial repeats based on review, but every required design cell remains accounted.
Probes with expected invalidity/sentinels pass only their explicit negative assertions;
they are never reported as complete zero-capacity runs. OOM2's legacy fresh remains 2
while successful native allocations are 1, establishing that the old event counts were
not relabelled byte/success counters. Watchdog timeouts are failures, not skips.

## Sanitizer request, if supported (explicit availability/skip)

After ordinary witnesses pass, compile the same source with the available clang64 ASan
toolchain (no Blade rebuild, no compiler policy edit). Keep compiler identity and exact
native runtime path. Example runner request:

```powershell
$b03Clang = 'C:\msys64\clang64\bin\clang++.exe'
& $b03Clang -std=c++20 -O1 -g -pthread -fsanitize=address -fno-omit-frame-pointer -DBLADE_POOL_CAPACITY_DIAGNOSTICS=1 -DBLADE_POOL_CAPACITY_TESTING=1 -DBLADE_POOL_CACHE_STATS=1 -I $b03Include $b03Source -o "$b03Out\asan.exe"
```

Use the same parent run_proc watchdog and absolute JSON paths for ASan `small`, `cache-off`,
`null`, `origin-exit`, `fail-eligible-off`, `direct`. Its header must auto-disable caching;
JSON cache_on=false, positive assertion totals and no sanitizer errors are required.
Place `C:/msys64/clang64/bin` first in that child's PATH if the ASan runtime needs it;
do not alter global machine state. Missing compiler/runtime or unsupported sanitizer
capability is an explicit skipped cell, never "0 failures" as if exercised.

Only if LeakSanitizer is supported: run the separate ASan `leak` negative with leak
detection enabled. Expected nonzero exit must contain genuine LeakSanitizer evidence of
the deliberately unreleased 24-byte pool. A crash or an "unsupported detect_leaks"
message is not a successful leak-visibility witness. `sentinel`/`strict-sentinel` also
intentionally leave capacity active and are **not** leak-free ASan positives. No global
application pointer is stored by the ledger; a volatile allocation-call indirection in
the expected-leak negative prevents allocation-elision while not rooting the pointer.

## Review/acceptance boundary

Root reviews the actual header/probe diff before any runner request. All source and
commands above are unexecuted. No benchmark, performance ratio, physical allocator peak,
LLVM/provider coverage or default retention verdict follows from this diagnostic card.
Stop after this coherent checkpoint; do not self-assign packaging, event identities,
native-live tracing or another instrumentation family.
