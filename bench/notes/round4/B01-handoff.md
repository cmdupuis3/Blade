# B01 handoff: process-memory report and one PM49 triad control

Implemented, unexecuted, frozen for root review / sole Luna runner, 2026-10-22.
Checkout: `C:/Users/cdupu/Documents/GitHub/Blade/.round4-worktrees/b-measurement`.
Branch `codex/round4-b-measurement`, base master `361504b6`. No commit or integration made.

## Scope and census

One control: reuse `saxpy` as `PM49-triad-materialized-v1`. Its stored output
`y2=1.5*x+y`, distinct stored inputs, BOTH checksums, natural/expert Fortran, input-only
twins, independent reference/pins and odd sizes already exist. The arithmetic, benchmark
sources, pins, flags and allocator defaults are unchanged. No duplicate triad is added.

The graft file API was queried before source inspection; it reported no definitions
indexed for `bench/harness/run.py`, so direct inspection was necessary. No compiler/runtime
source is edited. The existing four-hole inline-lifetime brief was read; PM49's current
route/lifetime/store-audit status is explicitly unverified, with LLVM lifetime-matched
attribution withdrawn pending A01 and fresh artifact evidence.

Owned changed/new files:

- `bench/harness/run.py`: peak commit propagation, additive versioned memory distributions,
  per-checkpoint partial-block/control records, `memory.md`, accurate legacy WS label.
- `bench/harness/test_memory.py`: ten focused unit fixtures; mock native capture, no actual
  child/compile/benchmark from these tests.
- `bench/harness/README.md`: schema, counter provenance/coverage and units/populations.
- `bench/kernels/saxpy/kernel.json`, `spec.md`: PM49 source/output/cleanup contract,
  analytical payload/operation bytes and explicit current audit limitations.
- `bench/validation/dump/saxpy_dump.blade`: diagnostic smoke full-array exposure; changed
  lifetime/printing means its time/peaks are never benchmark measurements.
- This handoff.

## Measurement contract

Session schema remains **2**, harness version **2.1**, additive memory sub-schema **1**.
Legacy raw `peak_working_set`, `page_faults` and all-sample `peak_working_set_max` remain;
`peak_commit_bytes` retains the already-captured Win32 `PeakPagefileUsage`. JSON units are
bytes/count; displayed units are MiB/count. The Win32 source definitions are linked in
the docs/report: <https://learn.microsoft.com/en-us/windows/win32/api/psapi/ns-psapi-process_memory_counters>.

Successful measured rounds are the primary memory population, including timing-excluded
excursions. Warm-ups, contract runs, drift controls and failures remain separate.
Timing exclusions have an explicitly overlapping subset. Missing/null counters remain
unmeasured; zero is valid. Each metric records available/missing counts and n, min,
median, quartiles, IQR, max and mean. Earlier successes in a subsequently invalid arm
remain descriptive with arm status visible. No memory CI or subtraction of process peaks.

Process high waters include the whole fresh child's lifetime, runtime/libraries and
cleanup. They cannot identify an allocation peak or cleanup point. Demand/backing models,
live allocation capacity, retention, allocation churn, external/worker/provider workspace,
cache/DRAM traffic and required-store audit are explicitly **unmeasured**. No live-byte or
retention result is inferred from process counters. Native allocation/event accounting
and real allocate/release/retention/untouched-capacity fixtures are later work.

Saxpy's metadata describes source-intended cleanup, not established emitted behavior:
Blade function-local y2 at kernel return and inputs at block exit before stop; Fortran
arrays after stop/reporting/dump. Existing A01 defects prevent claiming those endpoints
are matched today. Source models (16n input bytes, 8n output bytes, triad 16n read/8n write)
are separately labelled, for totals only. Input generation/checksums/cleanup are included
in timed totals, so the report derives no bandwidth from 24n / total time.

## Static inspection and source identity

Only read-only inspection and edits were performed. `git diff --check` was clean. The
four edited tracked files retain index LF / working-tree CRLF; no whole-file newline
conversion appears in the diff. No build, emit, compile, test, benchmark, oracle execution
or generated validation snippet was run by the coder. All ten fixtures are **unrun**.

Working-tree SHA-256 identities (raw bytes; excluding this handoff's self-hash):

| file | SHA-256 |
|---|---|
| bench/harness/run.py | B413D5F038F3C144810A79B0E0CA82C281E3E1EA60E553D5E4E45A625F099E3A |
| bench/harness/README.md | B821DF55AFBA790A448C9E4AC8CE06A44EF8F55A70D58DEA48B40E2AD11AE290 |
| bench/harness/test_memory.py | DE4519BF908217F1755D11E422D574C38D6680075098E5058AA7AEBBD7AD7902 |
| bench/kernels/saxpy/spec.md | 537720A349CA1389A58AE0EAFEE02CD4FAE798BC2B356EA628EE0845BFBA7FB6 |
| bench/kernels/saxpy/kernel.json | AA859DCC3BAD46C475D94A3F6B62101A45B94D26700C4466C6B85453F2393F42 |
| bench/validation/dump/saxpy_dump.blade | 79F45A5FE04616921AD5FE4C8AD4190A7E1764F152BD082ED5D3DF9A98046FCA |
| bench/kernels/saxpy/saxpy.blade (unchanged) | 1476356CE91027413473CB3B099FEF28605C0E8D78FB73CB86C4DE1FD2BE3F1A |
| bench/kernels/saxpy/natural.f90 (unchanged) | 463412FD6CA9B22984838692265D48A2600B4E57BAB167B88CB2EA5AB7FBFE20 |
| bench/kernels/saxpy/expert.f90 (unchanged) | 0B5F25C06FF776A90088ECD0B836685D6F2E1F0273A3E23E38E053A59EE57B0B |
| bench/kernels/saxpy/inputonly.blade (unchanged) | 48C8C5590C317F3993374DD1BF4AC64531F18261A79B9FED7305F8E4E2C26618 |
| bench/validation/ref_saxpy.py (unchanged) | 2B0AFE038DFF56C3462F58B42611924A570F0471BCFFDE26EEC233A65EC34CD7 |
| bench/validation/pins-B.json (unchanged) | 674CA1D32C97F62431F45FB6DEF75C20AEB241F8D4153F8C047BD00DE7749ED2 |

Runner: verify frozen hashes before execution; record exact source/compiler identity,
commands, pass/fail/skip totals and artifacts. If testing a later integration+A01 revision,
record that revision separately; it is not this base checkout. Do not test while edited.

## Runner commands: focused checks only

All following commands are **for Luna only**. No full/default suite; no quiet-machine
performance measurement is requested while coding continues. Work from a private cwd.

```powershell
$b01Repo = 'C:\Users\cdupu\Documents\GitHub\Blade\.round4-worktrees\b-measurement'
$b01Scratch = Join-Path $b01Repo 'bench\.build\B01-runner'
New-Item -ItemType Directory -Force -Path $b01Scratch | Out-Null
Set-Location -LiteralPath $b01Scratch
& 'C:\Python314\python.exe' -m unittest discover -s "$b01Repo\bench\harness" -p test_memory.py -v
```

Ten expected unit tests, no skip expected on this Windows host. They cover memory-only
population selection including an excluded timing spike, empty/failed populations,
missing/null/zero, independent commit/resident (without live/retention inference), native
capture propagation and unavailable counters, schema/legacy max, partial checkpoints,
legacy JSON rendering/units, and malformed metadata rejection before execution.

If no exact-source Blade Release build exists, the sole runner builds once:

```powershell
dotnet build "$b01Repo\Blade.fsproj" -c Release
```

Run the existing all-arm smoke contract gate in the two serial policies, including twins.
This validates the runner's process counters/report plumbing plus the unchanged pins;
it is a targeted correctness check, not timing evidence:

```powershell
& 'C:\Python314\python.exe' "$b01Repo\bench\harness\run.py" check saxpy --regimes serial-licensed-native,serial-repro-native --build-dir "$b01Scratch\build" --out-dir "$b01Scratch\reports"
& 'C:\Python314\python.exe' "$b01Repo\bench\harness\run.py" check saxpy --size control --regimes serial-repro-native --arms gfortran-expert --build-dir "$b01Scratch\control-build" --out-dir "$b01Scratch\control-reports"
& 'C:\Python314\python.exe' -P "$b01Repo\bench\validation\ref_saxpy.py"
```

Compare the last command's independently recomputed three sizes to existing pins-B.json;
do not overwrite pins. Toolchain failures/skips must be reported, not hidden with
`--allow-unbuilt`. Inspect `session.json` and `memory.md`: raw peaks preserved, correct
units/provenance, contract population only for checks, no manufactured round data,
source/cleanup caveats, total/twin separate and no peak subtraction. If Win32 queries
return no data, the correct result is unmeasured, not an invented process footprint.

## Runner diagnostic: every PM49 smoke output cell

The following is an authored, **unexecuted** runner snippet. It builds/runs only the new
Blade smoke dump and existing natural/expert Fortran `BENCH_DUMP` paths, checks every
output cell against the existing independent reference, and checks both scalars. It
deliberately uses diagnostic builds; their times/peaks are discarded. Missing compilers
are reported and fail this request rather than being mistaken for completed validation.

Set `$env:B01_REPO = $b01Repo`, then execute from `$b01Scratch`:

```powershell
@'
import ast, copy, json, math, os, sys
from pathlib import Path
repo = Path(os.environ['B01_REPO'])
sys.path[:0] = [str(repo / 'bench/harness'), str(repo / 'bench/validation')]
import run as h
import ref_saxpy
ref = ref_saxpy.compute('smoke')
pins = json.loads((repo / 'bench/validation/pins-B.json').read_text())['saxpy']['smoke']
for name in ('checksum', 'input_checksum'):
    assert math.isclose(ref[name], pins[name], rel_tol=1e-14), name
def check(label, values, scalars):
    expected = ref['output'].ravel().tolist()
    assert len(values) == len(expected) == 20011, label + ': shape'
    for i, (a, b) in enumerate(zip(values, expected)):
        assert math.isfinite(a) and abs(a-b) <= 1e-12 * max(1.0, abs(b)), (label, i, a, b)
    for name in ('checksum', 'input_checksum'):
        tolerance = 1e-14 if name == 'input_checksum' else 1e-12
        assert math.isfinite(scalars[name]) and math.isclose(scalars[name], pins[name], rel_tol=tolerance), (label, name)
    print(label, ': every cell and both scalars passed')
def scalars(text):
    result = {}
    for line in text.splitlines():
        if ' = ' in line:
            name, value = line.split(' = ', 1)
            if name.strip() in ('checksum', 'input_checksum'):
                result[name.strip()] = float(value)
    return result
for fp in ('licensed', 'repro'):
    regime = h.Regime.parse('serial-' + fp + '-native')
    config = h.load_config(repo / 'bench/harness/config.json')
    env = h.make_env(h.blade_knobs(regime, 'cpp', config))
    result = h.run_proc([str(h.BLADE_EXE), 'run', str(repo / 'bench/validation/dump/saxpy_dump.blade'), '--print', 'y2,checksum,input_checksum'], env=env, cwd=Path.cwd(), timeout=600)
    assert result.returncode == 0, result.stderr
    array_lines = [line.split(' = ', 1)[1] for line in result.stdout.splitlines() if line.startswith('y2 = ')]
    assert len(array_lines) == 1, result.stdout[:500]
    check('blade-cpp dump/' + fp, ast.literal_eval(array_lines[0]), scalars(result.stdout))
    tool_root = Path.cwd() / ('dump-tools-' + fp)
    tool_root.mkdir(parents=True, exist_ok=True)
    tools = h.Toolchain(tool_root)
    kernel = h.load_kernel(repo / 'bench/kernels/saxpy')
    for family in ('gfortran', 'ifx'):
        diagnostic_config = copy.deepcopy(config)
        diagnostic_config['fortran_flags'][family][fp].append('-DBENCH_DUMP' if family == 'gfortran' else '/DBENCH_DUMP')
        for tier in ('natural', 'expert'):
            arm = h.ArmSpec.parse(family + '-' + tier)
            build = h.build_arm(kernel, arm, regime, 'smoke', Path.cwd() / ('dump-build-' + fp), tools, diagnostic_config, 600)
            assert build.status == 'ok', (arm.name, build.status, build.reason)
            result = h.run_proc([build.exe], env=h.make_env(build.run_knobs, intel=family == 'ifx'), cwd=Path(build.cwd), timeout=10)
            assert result.returncode == 0, result.stderr
            values = [float(line) for line in (Path(build.cwd) / 'saxpy_dump.txt').read_text().splitlines()]
            check(arm.name + '/' + fp, values, scalars(result.stdout))
'@ | & 'C:\Python314\python.exe' -
```

The snippet uses existing harness build/policy helpers and independent reference; the
sole runner may fix command plumbing if necessary, recording exactly what changed.
It must not weaken cells/checksum/store/lifetime requirements to make validation pass.

## Deferred evidence and acceptance boundary

After A01 is accepted and the integrated source identity is frozen, request full-size
`check saxpy --size full --regimes serial-licensed-native,serial-repro-native` in a private
build/report directory. Inspect the retained generated source/plan and optimized executable
for stores to both input arrays and **all** y2 cells; record compiler fusion and actual
cleanup, both Blade lanes and Fortran tiers. Diagnostic dumps alone cannot establish
full-size stores, and compiler-store refusal stays explicit.

No benchmark run is requested by B01. Quiet, interleaved timing/peak distributions and
new scaling points are separate runner requests after root approves source/lifetime
evidence. The current card delivers a reporting foundation and an honestly specified,
reused control; all performance and live-capacity/retention/DRAM claims remain unmeasured.
Stop after B01; no next-card assignment is inferred.
