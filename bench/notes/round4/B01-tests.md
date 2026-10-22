# B01 targeted validation

Runner: Luna/high. Frozen source checkout `codex/round4-b-measurement`, commit `361504b60a1edd8a5c06993976daae7d7d25d9d3`. All 12 handoff source hashes matched before execution. `Blade.dll` SHA-256 `ee5bbb5553806c8921ce23a9248736f5f7ff3997515f8d184f8b3da2084b3c70`; `Blade.exe` SHA-256 `00522c896f3b5d992e24d4be54170e114d68e5a059c7d3d5b852d16e1826c622`; compiler reports version 0.20.0. Runtime toolchains were g++ 16.2.0, clang 22.1.8, gfortran 16.2.0 and ifx 2026.1.0.

## Results

- Python memory fixtures: **10/10 passed** using `C:\Python314\python.exe` 3.14.7. The first attempt used the sandbox's default temporary directory and two fixtures failed because the sandbox denied temporary-directory writes/cleanup. Re-running the same command with `TEMP` and `TMP` set to the private B01 scratch directory passed all ten; no fixture or source changed.
- Exact-source Release build: **passed**, 0 errors, 2 NU1900 warnings because NuGet vulnerability metadata was unreachable. An initial build invocation exceeded the 30-second wait and its session id was hidden by the result wrapper; I then started a no-restore build before resolving the first process. This created a possible overlap. After the no-restore build exited successfully, a process check found no remaining `dotnet` process, and the compiler DLL/executable hashes above were stable and recorded in both run reports. The initial invocation's independent exit status could not be recovered.
- `check saxpy --regimes serial-licensed-native,serial-repro-native`: **24/24 arms passed**, covering Blade LLVM/C++, gfortran and ifx natural/expert arms, and all input-only twins. Both report pins passed.
- `check saxpy --size control --regimes serial-repro-native --arms gfortran-expert`: **2/2 arms passed**, including the input-only twin at n=700,001.
- Independent `ref_saxpy.py`: **passed**; recomputed smoke, full and control checksum/input-checksum pairs matched the existing `pins-B.json` values. No pins were rewritten.

Exact focused commands, working directory, process reports and session artifacts are under `bench/.build/B01-runner` in the frozen B checkout. The smoke report `reports/session-092955-check` and control report `control-reports/session-093129-check` both retain session.json and memory.md. They report Win32 `PeakWorkingSetSize` and `PeakPagefileUsage` in bytes, PageFaultCount in counts, and display memory in MiB. The check runs contribute only to `successful_contracts`; they do not manufacture measured rounds. Total and input-only process peaks remain separate. Live allocation capacity, retained allocator capacity, traffic, external workspace and required-store audit are explicitly unmeasured. These observations do not validate A01 lifetime behavior.

The exact command forms from the handoff were run with the checkout root in `$b01Repo`,
`$b01Scratch` set to `bench/.build/B01-runner`, and `TEMP`/`TMP` directed into that
scratch directory for every process. Python fixture command:
`C:\Python314\python.exe -m unittest discover -s "$b01Repo\bench\harness" -p test_memory.py -v`.
Build command: `dotnet build "$b01Repo\Blade.fsproj" -c Release` (followed by a serialized
no-restore rebuild to resolve the hidden-session ambiguity described above). Check commands:
`C:\Python314\python.exe "$b01Repo\bench\harness\run.py" check saxpy --regimes serial-licensed-native,serial-repro-native --build-dir "$b01Scratch\build" --out-dir "$b01Scratch\reports"` and
`C:\Python314\python.exe "$b01Repo\bench\harness\run.py" check saxpy --size control --regimes serial-repro-native --arms gfortran-expert --build-dir "$b01Scratch\control-build" --out-dir "$b01Scratch\control-reports"`.
The reference command was `C:\Python314\python.exe -P "$b01Repo\bench\validation\ref_saxpy.py"`.
The check reports are linked here: [smoke session report](../../../../b-measurement/bench/.build/B01-runner/reports/session-092955-check)
and [control session report](../../../../b-measurement/bench/.build/B01-runner/control-reports/session-093129-check).

No benchmark or full/default `blade test` suite was run. The authored diagnostic full-array dump gate passed in all 10 backend/policy combinations: Blade C++ plus gfortran and ifx natural/expert under licensed and repro policies. Each run checked all 20,011 values and both checksums against the independent reference and existing pins. The run report/handoff records the intended private B scratch CWD, but the original checkout currently contains the snippet's exact implicit `Path.cwd()` output directories (`dump-build-licensed`, `dump-build-repro`, `dump-tools-licensed`, `dump-tools-repro`), created at 09:40:39–09:40:56 on 2026-10-22. That observed filesystem provenance conflicts with the private-CWD claim; the exact execution CWD cannot be independently recovered, so treat those outputs as likely attributable to this diagnostic and preserve them. No rerun is authorized or needed for this discrepancy. These diagnostic builds provide value coverage only; they do not establish full-size stores or lifetime correctness. Full-size cleanup/store evidence remains deferred until A01 is integrated and reviewed. Full-suite launch count remains zero.


