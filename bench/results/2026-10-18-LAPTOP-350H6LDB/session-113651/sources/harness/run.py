"""Blade-vs-Fortran benchmark driver (Python 3.14, standard library only).

Run with ``C:\\Python314\\python.exe bench\\harness\\run.py <verb> ...`` (``python3`` on this
box is a Store stub).  Verbs:

    list                     the discovered kernels, their sizes, regimes and sources
    check <kernel>           build every arm (smoke size by default), run each once, validate
    smoke                    every kernel at smoke size, one validated run per arm
    bench                    the measured protocol -> an immutable bundle under bench/results/

The protocol is bench/README.md (section 6) as amended by the method review
(bench/notes/review-method.md: M3, M9, M12, M14-M21, M24, M25, M27, M28).  The JSON fields
and the kernel.json / config.json / pins.json schemas are documented in
bench/harness/README.md.  Every child process goes through :func:`run_proc`, which builds the
environment, captures stdout/stderr, kills the process tree on timeout, optionally pins the
child to one CPU and reads its peak working set.
"""

from __future__ import annotations

import argparse
import ctypes
import dataclasses
import datetime as dt
import hashlib
import json
import math
import os
import platform
import random
import re
import shutil
import statistics
import subprocess
import sys
import time
import traceback
import winreg
from ctypes import wintypes
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Iterable

HARNESS_VERSION = "2"

# ---------------------------------------------------------------------------
# Fixed locations and toolchain facts (charter section 2)
# ---------------------------------------------------------------------------

HARNESS_DIR = Path(__file__).resolve().parent
BENCH_DIR = HARNESS_DIR.parent
REPO_DIR = BENCH_DIR.parent
KERNELS_DIR = BENCH_DIR / "kernels"
FORTRAN_DIR = BENCH_DIR / "fortran"
VALIDATION_DIR = BENCH_DIR / "validation"
RESULTS_DIR = BENCH_DIR / "results"
BUILD_ROOT = BENCH_DIR / ".build"
CONFIG_FILE = HARNESS_DIR / "config.json"
SMOKE_EVIDENCE = BUILD_ROOT / "smoke-evidence.json"

BLADE_EXE = REPO_DIR / "bin" / "Release" / "net10.0" / "Blade.exe"
UCRT_BIN = Path(r"C:\msys64\ucrt64\bin")
GFORTRAN = UCRT_BIN / "gfortran.exe"
OBJDUMP = UCRT_BIN / "objdump.exe"
CLANG = Path(r"C:\msys64\clang64\bin\clang.exe")
IFX_WRAPPER = FORTRAN_DIR / "build_ifx.cmd"
GFORTRAN_WRAPPER = FORTRAN_DIR / "build_gfortran.ps1"
BENCH_SUPPORT = FORTRAN_DIR / "bench_support.f90"
INTEL_BIN = Path(r"C:\Program Files (x86)\Intel\oneAPI\compiler\latest\bin")
BLADE_TEMP_ROOT = Path(os.environ.get("TEMP", r"C:\Windows\Temp")) / "blade-build"
SYSTEM32 = Path(os.environ.get("SystemRoot", r"C:\Windows")) / "System32"

BASE_ARMS = ["blade-llvm", "blade-cpp", "gfortran-natural", "gfortran-expert", "ifx-natural", "ifx-expert"]
TWIN_ARMS = [a + "-inputonly" for a in BASE_ARMS]
ALL_ARMS = BASE_ARMS + TWIN_ARMS
FP_REGIMES = ("licensed", "repro", "fastmath")      # licensed = HEADLINE (README 3)
# Order of work: licensed serial, licensed mt8, repro serial, repro mt8, fastmath, blas.
REGIME_ORDER = [f"{t}-{fp}-{bl}" for bl in ("native", "blas") for fp in FP_REGIMES for t in ("serial", "mt8")]
DEFAULT_SMOKE_REGIME = "serial-licensed-native"
# Blade prints Float64 with setprecision(15) in both lanes (Fortran prints 17 digits):
# "exact" input agreement = equal after rounding both to 15 significant digits.
BLADE_PRINT_DIGITS = 15
# input_checksum agreement (2026-10-18): relative error <= 1e-14.  Blade prints 15 digits, and
# a pin whose double sits within an ulp of a 15-digit half-way point can render either way
# (pairkernel smoke: pin 73.6780804120935499..., Blade's sum 6.8e-16 lower); the inputs are
# dyadic with exact sums, so a real mismatch is orders of magnitude larger than 1e-14.
INPUT_REL_TOL = 1e-14

MIN_RUN_COOLDOWN_S = 2.0
MIN_KERNEL_COOLDOWN_S = 10.0
DQ_RUN_S = 2.5                     # README 6: a run longer than 2.5 s disqualifies the arm
RUN_TIMEOUT_CAP_S = 3 * DQ_RUN_S   # M19: child watchdog = 3x the run cap
MEMORY_BOUND_S = 0.05              # README 6: < 50 ms full-size -> memory-bound rounds
MIN_ROUNDS = {"serial": 11, "mt8": 7, "memory_bound": 21}   # M15
THERMAL_FACTOR = 3.0               # M19: any run > 3x its running median aborts the session
THERMAL_MIN_MEDIAN_S = 0.005       # ... once that median is above the timer-noise floor
TREND_LIMIT = 0.05                 # M18: first-half vs second-half median within a block
CONTROL_LIMIT = 0.07               # M18: rolling control threshold

STRIPPED_ENV_PREFIXES = ("BLADE_", "OMP_", "GOMP_", "KMP_")
STRIPPED_ENV_NAMES = {"OPENBLAS_NUM_THREADS", "MKL_NUM_THREADS", "GOTO_NUM_THREADS"}

_T0 = time.monotonic()


def log(msg: str) -> None:
    """Print one timestamped console line (flushed: the runner tails it)."""
    now = dt.datetime.now().strftime("%H:%M:%S.%f")[:-3]
    print(f"[{now} +{time.monotonic() - _T0:7.1f}s] {msg}", flush=True)


def stable_seed(*parts: Any) -> int:
    """A reproducible 31-bit seed (Python's str hash is salted per process)."""
    return int(hashlib.sha256("|".join(map(str, parts)).encode()).hexdigest()[:8], 16) & 0x7FFFFFFF


class HarnessError(Exception):
    """A configuration or contract problem that must stop the harness loudly."""


class SessionAbort(Exception):
    """Thermal excursion, battery, or another condition that ends the session (M19)."""


# ---------------------------------------------------------------------------
# Win32 helpers: affinity, peak working set, power
# ---------------------------------------------------------------------------

_k32 = ctypes.WinDLL("kernel32", use_last_error=True)
_psapi = ctypes.WinDLL("psapi", use_last_error=True)
_k32.GetCurrentProcess.restype = wintypes.HANDLE
_k32.GetProcessAffinityMask.argtypes = [wintypes.HANDLE, ctypes.POINTER(ctypes.c_size_t), ctypes.POINTER(ctypes.c_size_t)]
_k32.SetProcessAffinityMask.argtypes = [wintypes.HANDLE, ctypes.c_size_t]


class _PMC(ctypes.Structure):
    _fields_ = [("cb", wintypes.DWORD), ("PageFaultCount", wintypes.DWORD),
                ("PeakWorkingSetSize", ctypes.c_size_t), ("WorkingSetSize", ctypes.c_size_t),
                ("QuotaPeakPagedPoolUsage", ctypes.c_size_t), ("QuotaPagedPoolUsage", ctypes.c_size_t),
                ("QuotaPeakNonPagedPoolUsage", ctypes.c_size_t), ("QuotaNonPagedPoolUsage", ctypes.c_size_t),
                ("PagefileUsage", ctypes.c_size_t), ("PeakPagefileUsage", ctypes.c_size_t)]


_psapi.GetProcessMemoryInfo.argtypes = [wintypes.HANDLE, ctypes.POINTER(_PMC), wintypes.DWORD]


def _affinity() -> tuple[int, int]:
    proc, sysm = ctypes.c_size_t(), ctypes.c_size_t()
    _k32.GetProcessAffinityMask(_k32.GetCurrentProcess(), ctypes.byref(proc), ctypes.byref(sysm))
    return proc.value, sysm.value


def _set_affinity(mask: int) -> bool:
    return bool(_k32.SetProcessAffinityMask(_k32.GetCurrentProcess(), mask))


def _memory_info(handle: int) -> dict[str, int] | None:
    m = _PMC()
    m.cb = ctypes.sizeof(_PMC)
    if _psapi.GetProcessMemoryInfo(wintypes.HANDLE(handle), ctypes.byref(m), m.cb):
        return {"peak_working_set": m.PeakWorkingSetSize, "page_faults": m.PageFaultCount,
                "peak_pagefile": m.PeakPagefileUsage}
    return None


class _SystemPowerStatus(ctypes.Structure):
    _fields_ = [("ACLineStatus", ctypes.c_ubyte), ("BatteryFlag", ctypes.c_ubyte),
                ("BatteryLifePercent", ctypes.c_ubyte), ("SystemStatusFlag", ctypes.c_ubyte),
                ("BatteryLifeTime", ctypes.c_ulong), ("BatteryFullLifeTime", ctypes.c_ulong)]


class _MemoryStatusEx(ctypes.Structure):
    _fields_ = [("dwLength", ctypes.c_ulong), ("dwMemoryLoad", ctypes.c_ulong),
                ("ullTotalPhys", ctypes.c_ulonglong), ("ullAvailPhys", ctypes.c_ulonglong),
                ("ullTotalPageFile", ctypes.c_ulonglong), ("ullAvailPageFile", ctypes.c_ulonglong),
                ("ullTotalVirtual", ctypes.c_ulonglong), ("ullAvailVirtual", ctypes.c_ulonglong),
                ("ullAvailExtendedVirtual", ctypes.c_ulonglong)]


def ac_line() -> str:
    """'ac', 'battery' or 'unknown' (GetSystemPowerStatus)."""
    st = _SystemPowerStatus()
    if not _k32.GetSystemPowerStatus(ctypes.byref(st)):
        return "unknown"
    return {0: "battery", 1: "ac"}.get(st.ACLineStatus, "unknown")


# ---------------------------------------------------------------------------
# Process helper
# ---------------------------------------------------------------------------

@dataclass
class ProcResult:
    """What one child process did."""
    argv: list[str]
    cwd: str
    returncode: int | None
    stdout: str
    stderr: str
    wall_s: float
    timed_out: bool
    launch_error: str | None = None
    memory: dict[str, int] | None = None
    affinity_mask: int | None = None


def openblas_dir() -> Path | None:
    """The OpenBLAS install root from the inherited environment, if any."""
    v = os.environ.get("OPENBLAS_DIR")
    return Path(v) if v else None


def make_env(knobs: dict[str, str], *, intel: bool = False, blas: bool = False) -> dict[str, str]:
    """The environment for one compile or run.

    The inherited environment minus every BLADE_*/OMP_*/GOMP_*/KMP_* knob and the BLAS
    thread counts, ucrt64 FIRST on PATH (Git's mingw64 shadows it and the GNU compilers die
    silently otherwise), then the Intel runtime (ifx arms) and OPENBLAS_DIR\\bin (blas), then
    ``knobs``.
    """
    env = {k: v for k, v in os.environ.items()
           if not k.upper().startswith(STRIPPED_ENV_PREFIXES) and k.upper() not in STRIPPED_ENV_NAMES}
    prepend = [str(UCRT_BIN)]
    if intel:
        prepend.append(str(INTEL_BIN))
    if blas and openblas_dir() is not None:
        prepend.append(str(openblas_dir() / "bin"))
    path_key = next((k for k in env if k.upper() == "PATH"), "PATH")
    env[path_key] = ";".join(prepend + [env.get(path_key, "")])
    env.update(knobs)
    return env


def env_path(env: dict[str, str]) -> list[str]:
    return next((v for k, v in env.items() if k.upper() == "PATH"), "").split(";")


def kill_tree(pid: int) -> None:
    """Kill a process and all of its children (cmd /c wrappers spawn grandchildren)."""
    subprocess.run(["taskkill", "/T", "/F", "/PID", str(pid)],
                   stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, check=False)


def run_proc(argv: list[str], *, env: dict[str, str], cwd: Path, timeout: float,
             affinity: int | None = None) -> ProcResult:
    """Run one child process: captured stdout/stderr, hard timeout, optional CPU pin.

    The ONLY place the harness spawns a process.  On timeout the whole process tree is
    killed.  ``affinity`` pins the child from its first instruction: the harness sets its
    OWN affinity mask before spawning (children inherit it) and restores it afterwards (the
    harness only blocks on the pipes meanwhile).  The child's peak working set is read from
    its handle after it exits (M21).
    """
    saved: int | None = None
    if affinity is not None:
        saved = _affinity()[0]
        if not _set_affinity(affinity):
            raise HarnessError(f"SetProcessAffinityMask({affinity:#x}) failed")
    t0 = time.perf_counter()
    try:
        try:
            p = subprocess.Popen(argv, cwd=str(cwd), env=env, stdin=subprocess.DEVNULL,
                                 stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                                 creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
        except OSError as ex:
            return ProcResult(argv, str(cwd), None, "", "", 0.0, False, launch_error=str(ex))
        timed_out = False
        try:
            out, err = p.communicate(timeout=timeout)
        except subprocess.TimeoutExpired:
            timed_out = True
            kill_tree(p.pid)
            try:
                out, err = p.communicate(timeout=30)
            except subprocess.TimeoutExpired:
                p.kill()
                out, err = b"", b""
        wall = time.perf_counter() - t0
        handle = getattr(p, "_handle", None)
        mem = _memory_info(int(handle)) if handle is not None else None
        return ProcResult(argv, str(cwd), p.returncode, out.decode("utf-8", errors="replace"),
                          err.decode("utf-8", errors="replace"), wall, timed_out,
                          memory=mem, affinity_mask=affinity)
    finally:
        if saved is not None:
            _set_affinity(saved)


def tail(text: str, lines: int = 40) -> str:
    """The last ``lines`` lines of a captured stream."""
    parts = text.replace("\r\n", "\n").rstrip("\n").split("\n")
    return "\n".join(parts[-lines:])


_HASH_CACHE: dict[tuple[str, float, int], str] = {}


def sha256_file(path: Path) -> str:
    """SHA-256 of a file's bytes, hex (cached by path, mtime, size)."""
    st = path.stat()
    key = (str(path).lower(), st.st_mtime, st.st_size)
    if key not in _HASH_CACHE:
        h = hashlib.sha256()
        with open(path, "rb") as f:
            for chunk in iter(lambda: f.read(1 << 20), b""):
                h.update(chunk)
        _HASH_CACHE[key] = h.hexdigest()
    return _HASH_CACHE[key]


def write_json_atomic(path: Path, data: Any, attempts: int = 10, backoff_s: float = 0.05) -> None:
    """Checkpoint-safe JSON write (temp file + replace).  os.replace can hit a transient
    Windows sharing violation (an indexer or AV scanner holding the target): retried with a
    bounded linear backoff before the error propagates."""
    tmp = path.with_suffix(path.suffix + ".tmp")
    tmp.write_text(json.dumps(data, indent=1, default=str), encoding="utf-8")
    for i in range(attempts):
        try:
            os.replace(tmp, path)
            return
        except PermissionError:
            if i == attempts - 1:
                raise
            time.sleep(backoff_s * (i + 1))


def sleep_logged(seconds: float, why: str) -> None:
    """Cooldown sleep (logged only when long enough to notice)."""
    if seconds <= 0:
        return
    if seconds >= 5:
        log(f"cooldown {seconds:.0f}s ({why})")
    time.sleep(seconds)


# ---------------------------------------------------------------------------
# Configuration (M3: required, no silent defaults)
# ---------------------------------------------------------------------------

REQUIRED_CONFIG = ("controls", "fortran_flags", "fortran_twin_flags", "blade_knobs",
                   "serial_affinity_cpu", "run_env", "verdict_delta", "tolerance")


def load_config(path: Path = CONFIG_FILE) -> dict[str, Any]:
    """bench/harness/config.json; every key in REQUIRED_CONFIG must be present."""
    if not path.is_file():
        raise HarnessError(f"{path} is required (no silent defaults, review M3)")
    cfg = json.loads(path.read_text(encoding="utf-8-sig"))
    missing = [k for k in REQUIRED_CONFIG if k not in cfg]
    if missing:
        raise HarnessError(f"{path}: missing required key(s) {missing}")
    for comp in ("gfortran", "ifx"):
        t = cfg["fortran_flags"].get(comp)
        if not isinstance(t, dict) or any(not isinstance(t.get(k), list) for k in (*FP_REGIMES, "serial_extra", "mt8_extra")):
            raise HarnessError(f"{path}: fortran_flags.{comp} needs list keys {FP_REGIMES}, serial_extra, mt8_extra")
        if not isinstance(cfg["fortran_twin_flags"].get(comp), list):
            raise HarnessError(f"{path}: fortran_twin_flags.{comp} must be a list")
    for c in cfg["controls"]:
        for k in ("name", "kernel", "arm", "regime", "size"):
            if k not in c:
                raise HarnessError(f"{path}: controls[] entries need {k!r}")
    for fp in FP_REGIMES:
        if not isinstance(cfg["tolerance"].get(fp), (int, float)):
            raise HarnessError(f"{path}: tolerance.{fp} must be a number")
    for k in ("all", "llvm", *FP_REGIMES):
        if not isinstance(cfg["blade_knobs"].get(k), dict):
            raise HarnessError(f"{path}: blade_knobs.{k} must be an object")
    for k in ("serial", "mt8"):
        if not isinstance(cfg["run_env"].get(k), dict):
            raise HarnessError(f"{path}: run_env.{k} must be an object")
    return cfg


def config_hash(cfg: dict[str, Any]) -> str:
    return hashlib.sha256(json.dumps(cfg, sort_keys=True).encode()).hexdigest()


def _drop_comments(x: Any) -> Any:
    if isinstance(x, dict):
        return {k: _drop_comments(v) for k, v in x.items() if not str(k).startswith("_")}
    return x


def build_config_hash(cfg: dict[str, Any]) -> str:
    """Hash of the config keys that change what is BUILT or how it RUNS (flags, knobs, run
    environment, tolerances), comments excluded -- the part smoke evidence is keyed on, so
    editing controls or comments does not invalidate it."""
    sub = {k: _drop_comments(cfg[k]) for k in ("fortran_flags", "fortran_twin_flags", "blade_knobs", "run_env", "tolerance")}
    return hashlib.sha256(json.dumps(sub, sort_keys=True).encode()).hexdigest()


# ---------------------------------------------------------------------------
# Regimes and arms
# ---------------------------------------------------------------------------

@dataclass(frozen=True)
class Regime:
    """One cell of the regime matrix, named ``<threads>-<fp>-<blas>``."""
    threads: str
    fp: str
    blas: str

    @property
    def name(self) -> str:
        return f"{self.threads}-{self.fp}-{self.blas}"

    def serial_twin(self) -> "Regime":
        """The serial regime an mt8 regime is gated on (charter 2.3, review M12)."""
        return Regime("serial", self.fp, self.blas)

    @staticmethod
    def parse(name: str) -> "Regime":
        parts = name.strip().split("-")
        if (len(parts) != 3 or parts[0] not in ("serial", "mt8")
                or parts[1] not in FP_REGIMES or parts[2] not in ("native", "blas")):
            raise HarnessError(f"bad regime name {name!r} (want <serial|mt8>-<licensed|repro|fastmath>-<native|blas>)")
        return Regime(*parts)


@dataclass(frozen=True)
class ArmSpec:
    """What an arm name means.  Every base arm has its own twin `<arm>-inputonly` (M8)."""
    name: str
    family: str          # blade | gfortran | ifx
    lane: str | None     # llvm | cpp (blade only)
    tier: str            # blade | natural | expert
    twin: bool

    @property
    def base(self) -> str:
        return self.name[: -len("-inputonly")] if self.twin else self.name

    @staticmethod
    def parse(name: str) -> "ArmSpec":
        twin = name.endswith("-inputonly")
        base = name[: -len("-inputonly")] if twin else name
        if base in ("blade-llvm", "blade-cpp"):
            return ArmSpec(name, "blade", base.split("-")[1], "blade", twin)
        fam, _, tier = base.partition("-")
        if fam in ("gfortran", "ifx") and tier in ("natural", "expert"):
            return ArmSpec(name, fam, None, tier, twin)
        raise HarnessError(f"unknown arm {name!r} (arms: {', '.join(ALL_ARMS)})")


def twin_of(arm: str) -> str:
    return arm + "-inputonly"


# ---------------------------------------------------------------------------
# Kernel discovery (kernel.json)
# ---------------------------------------------------------------------------

@dataclass
class Kernel:
    """A discovered kernel: its directory plus the validated kernel.json."""
    name: str
    dir: Path
    group: str
    sizes: dict[str, dict[str, int]]
    arms: list[str]
    regimes: list[str]
    tolerance: dict[str, float]
    input_tolerance: float
    llvm_expected: bool
    sources: dict[str, str]
    inputonly: bool
    inputonly_blade: str
    rounds: dict[str, int]
    hypothesis: dict[str, str]
    regime_arms: dict[str, list[str]]
    blas_sources: dict[str, str]
    no_parallel_form: list[str]
    parallel_notes: dict[str, str]
    raw: dict[str, Any]

    def source_for(self, arm: ArmSpec, regime: Regime | None = None) -> Path:
        """The committed source an arm builds from.  Fortran twins build from their TIER
        source with the twin defines (M8); Blade twins from `inputonly.blade`."""
        if arm.family == "blade":
            return self.dir / (self.inputonly_blade if arm.twin else self.sources["blade"])
        if regime is not None and regime.blas == "blas" and arm.tier in self.blas_sources:
            return self.dir / self.blas_sources[arm.tier]
        return self.dir / self.sources[arm.tier]

    def arms_in(self, regime: Regime) -> list[str]:
        """The arms a regime runs (kernel.json `regime_arms` narrows a regime's base arms;
        twins follow their base arm)."""
        sel = self.regime_arms.get(regime.name)
        return list(self.arms) if sel is None else [a for a in self.arms if ArmSpec.parse(a).base in sel]

    def blade_has_parallel_form(self, regime: Regime) -> bool:
        return not (regime.threads == "mt8" and ("all" in self.no_parallel_form or regime.name in self.no_parallel_form))


def load_kernel(kdir: Path) -> Kernel:
    """Read and validate ``<kdir>/kernel.json`` (schema: bench/harness/README.md)."""
    path = kdir / "kernel.json"
    try:
        raw = json.loads(path.read_text(encoding="utf-8-sig"))
    except (OSError, json.JSONDecodeError) as ex:
        raise HarnessError(f"{path}: {ex}") from ex

    def need(key: str, typ: type) -> Any:
        if key not in raw or not isinstance(raw[key], typ):
            raise HarnessError(f"{path}: field {key!r} missing or not a {typ.__name__}")
        return raw[key]

    name = need("name", str)
    if name != kdir.name:
        raise HarnessError(f"{path}: name {name!r} differs from its directory {kdir.name!r}")
    sizes = need("sizes", dict)
    for req in ("smoke", "full"):
        if req not in sizes:
            raise HarnessError(f"{path}: sizes.{req} is required")
    params: set[str] | None = None
    for sname, sz in sizes.items():
        if not isinstance(sz, dict) or not sz:
            raise HarnessError(f"{path}: sizes.{sname} must be a non-empty {{param: int}} map")
        for p, v in sz.items():
            if not isinstance(v, int) or isinstance(v, bool) or v <= 0:
                raise HarnessError(f"{path}: sizes.{sname}.{p} must be a positive integer")
        if params is None:
            params = set(sz)
        elif set(sz) != params:
            raise HarnessError(f"{path}: sizes.{sname} declares {sorted(sz)}, others declare {sorted(params)}")
    arms = list(raw.get("arms", BASE_ARMS))
    for a in arms:
        if ArmSpec.parse(a).twin:
            raise HarnessError(f"{path}: list base arms only; twins follow `inputonly`")
    inputonly = bool(raw.get("inputonly", False))
    if inputonly:
        arms += [twin_of(a) for a in arms]
    regimes = need("regimes", list)
    for r in regimes:
        Regime.parse(r)
    regime_arms = raw.get("regime_arms", {})
    for rn, al in regime_arms.items():
        Regime.parse(rn)
        for x in al:
            ArmSpec.parse(x)
    rounds = raw.get("rounds", {})
    for key, v in rounds.items():
        if key not in ("serial", "mt8") or not isinstance(v, int) or v < 1:
            raise HarnessError(f"{path}: rounds.{key} must be serial|mt8 -> positive int")
    tol = {fp: float(v) for fp, v in raw.get("tolerance", {}).items()}   # overrides of config.json
    sources = {"blade": f"{name}.blade", "natural": "natural.f90", "expert": "expert.f90"}
    sources.update(raw.get("sources", {}))
    npf = raw.get("no_parallel_form", [])
    if npf is True:
        npf = ["all"]
    return Kernel(name=name, dir=kdir, group=str(raw.get("group", "?")), sizes=sizes, arms=arms,
                  regimes=list(regimes), tolerance=tol,
                  input_tolerance=float(raw.get("input_tolerance", 0.0)),
                  llvm_expected=raw.get("expected_llvm", "lane") != "fallback", sources=sources,
                  inputonly=inputonly, inputonly_blade=str(raw.get("inputonly_blade", "inputonly.blade")),
                  rounds=rounds, hypothesis=raw.get("hypothesis", {}), regime_arms=regime_arms,
                  blas_sources=raw.get("blas_sources", {}), no_parallel_form=list(npf),
                  parallel_notes=raw.get("parallel_notes", {}), raw=raw)


def discover(kernels_dir: Path) -> list[Kernel]:
    """Every ``<kernels_dir>/<name>/kernel.json``, sorted by (group, name)."""
    if not kernels_dir.is_dir():
        return []
    found = [load_kernel(d) for d in sorted(kernels_dir.iterdir())
             if d.is_dir() and (d / "kernel.json").is_file()]
    return sorted(found, key=lambda k: (k.group, k.name))


# ---------------------------------------------------------------------------
# Correctness oracle (M12): bench/validation/pins*.json
# ---------------------------------------------------------------------------

def rel_err(a: float, ref: float) -> float:
    """|a - ref| / |ref| (absolute when ref is 0)."""
    if a == ref:
        return 0.0
    return abs(a - ref) / abs(ref) if ref != 0 else abs(a - ref)


def round_digits(x: float, digits: int = BLADE_PRINT_DIGITS) -> float:
    """``x`` rounded to ``digits`` significant decimal digits (as `%.15g` prints it)."""
    return float(f"{x:.{digits}g}")


class Pins:
    """Independent reference checksums, merged from pins.json, pins-A.json, pins-B.json.

    Entry: ``kernels.<kernel>.<size name> = {"sizes": {...}, "checksum": x,
    "input_checksum": y, ...}``.  A pin is used only when its `sizes` equal kernel.json's
    sizes for that size name.  The same entry in two files must agree to 1e-15.
    """

    def __init__(self, vdir: Path = VALIDATION_DIR) -> None:
        self.entries: dict[tuple[str, str], dict[str, Any]] = {}
        self.files: dict[str, str] = {}
        for fname in ("pins.json", "pins-A.json", "pins-B.json"):
            f = vdir / fname
            if not f.is_file():
                continue
            self.files[str(f)] = sha256_file(f)
            raw = json.loads(f.read_text(encoding="utf-8-sig"))
            ks = raw.get("kernels", raw)
            for kname, per in ks.items():
                if kname.startswith("_") or not isinstance(per, dict):
                    continue
                for sname, e in per.items():
                    if not isinstance(e, dict) or "checksum" not in e or "input_checksum" not in e:
                        continue
                    key = (kname, sname)
                    if key in self.entries:
                        old = self.entries[key]
                        for fld in ("checksum", "input_checksum"):
                            if rel_err(float(e[fld]), float(old[fld])) > 1e-15:
                                raise HarnessError(f"pins disagree for {kname}/{sname}.{fld}: {old['_file']} vs {f}")
                    self.entries[key] = {**e, "_file": str(f)}

    def get(self, k: Kernel, size_name: str) -> dict[str, Any] | None:
        e = self.entries.get((k.name, size_name))
        if e is None:
            return None
        if e.get("sizes") is not None and e["sizes"] != k.sizes[size_name]:
            raise HarnessError(f"pin {k.name}/{size_name} is for sizes {e['sizes']}, kernel.json says {k.sizes[size_name]}")
        return e


# ---------------------------------------------------------------------------
# Size substitution (M28: one marker per param, one literal per marked line)
# ---------------------------------------------------------------------------

_MARK_BLADE = re.compile(r"//\s*SIZE\s+([A-Za-z_]\w*)\s*$")
_MARK_F90 = re.compile(r"!\s*SIZE\s+([A-Za-z_]\w*)\s*$")
_STRAY_BLADE = re.compile(r"//\s*SIZE\s+[A-Za-z_]\w*")
_STRAY_F90 = re.compile(r"!\s*SIZE\s+[A-Za-z_]\w*")
# An integer literal not glued to an identifier or a decimal point; a Fortran kind suffix
# (`6006_int64`) is allowed and kept.
_INT_LIT = re.compile(r"(?<![\w.])(\d+)(?=(_\w+)?(?![\w.]))")


def substitute_sizes(text: str, *, fortran: bool, sizes: dict[str, int], full: dict[str, int],
                     origin: str, exempt: Iterable[str] = ()) -> tuple[str, dict[str, int]]:
    """Rewrite the ONE integer literal on each ``SIZE <param>`` marker line.

    Fails loudly (review M28) when: a declared param has no marker line; a param is marked
    twice; a marker names an undeclared param; a marked line carries zero or several
    integer literals; a `SIZE` marker is not the last thing on its line; or the committed
    literal differs from ``sizes.full`` (sources are committed at FULL size).
    """
    mark, stray = (_MARK_F90, _STRAY_F90) if fortran else (_MARK_BLADE, _STRAY_BLADE)
    lines = text.splitlines(keepends=True)
    seen: dict[str, int] = {}
    for i, line in enumerate(lines):
        body = line.rstrip("\r\n")
        m = mark.search(body)
        if not m:
            if stray.search(body):
                raise HarnessError(f"{origin}:{i + 1}: a SIZE marker that is not the last thing on its line")
            continue
        param = m.group(1)
        if param not in sizes:
            raise HarnessError(f"{origin}:{i + 1}: marker SIZE {param} names a param kernel.json does not declare ({sorted(sizes)})")
        if param in seen:
            raise HarnessError(f"{origin}:{i + 1}: SIZE {param} marked twice (first on line {seen[param]})")
        code = body[: m.start()]
        lits = list(_INT_LIT.finditer(code))
        if len(lits) != 1:
            raise HarnessError(f"{origin}:{i + 1}: marker SIZE {param} on a line with {len(lits)} integer literals (exactly one required)")
        lit = lits[0]
        committed = int(lit.group(1))
        if committed != full[param]:
            raise HarnessError(f"{origin}:{i + 1}: committed {param} = {committed}, but kernel.json sizes.full.{param} = {full[param]}")
        lines[i] = code[: lit.start(1)] + str(sizes[param]) + code[lit.end(1):] + body[m.start():] + line[len(body):]
        seen[param] = i + 1
    missing = [p for p in sizes if p not in seen and p not in set(exempt)]
    if missing:
        raise HarnessError(f"{origin}: no `SIZE <param>` marker line for {missing}")
    return "".join(lines), seen


# ---------------------------------------------------------------------------
# Toolchain identity, DLL resolution, power settings
# ---------------------------------------------------------------------------

class Toolchain:
    """Compiler identities, probed once per session and cached."""

    def __init__(self, build_root: Path) -> None:
        self.build_root = build_root
        self._cache: dict[str, dict[str, Any]] = {}

    def _first_line(self, argv: list[str]) -> str:
        r = run_proc(argv, env=make_env({}), cwd=self.build_root, timeout=60)
        return next((l.strip() for l in (r.stdout + "\n" + r.stderr).splitlines() if l.strip()), "")

    def blade(self) -> dict[str, Any]:
        if "blade" not in self._cache:
            dll = BLADE_EXE.with_suffix(".dll")
            info: dict[str, Any] = {"exe": str(BLADE_EXE), "exists": BLADE_EXE.is_file(),
                                    "toolchain_json_beside_exe": (BLADE_EXE.parent / "blade.toolchain.json").is_file()}
            if BLADE_EXE.is_file():
                info["exe_sha256"] = sha256_file(BLADE_EXE)
                info["dll"] = str(dll)
                info["dll_sha256"] = sha256_file(dll) if dll.is_file() else None
                info["dll_mtime"] = dt.datetime.fromtimestamp(dll.stat().st_mtime).isoformat() if dll.is_file() else None
                info["version"] = self._first_line([str(BLADE_EXE)])
            self._cache["blade"] = info
        return self._cache["blade"]

    def simple(self, key: str, argv: list[str]) -> dict[str, Any]:
        if key not in self._cache:
            self._cache[key] = {"path": argv[0], "exists": Path(argv[0]).is_file(), "version": self._first_line(argv)}
        return self._cache[key]

    def clang(self) -> dict[str, Any]:
        return self.simple("clang", [str(CLANG), "--version"])

    def gpp(self) -> dict[str, Any]:
        return self.simple("gpp", [str(UCRT_BIN / "g++.exe"), "--version"])

    def gfortran(self) -> dict[str, Any]:
        return self.simple("gfortran", [str(GFORTRAN), "--version"])

    def ifx(self) -> dict[str, Any]:
        """ifx version via the Fortran agent's wrapper (`/QV` on a trivial source)."""
        if "ifx" not in self._cache:
            info: dict[str, Any] = {"wrapper": str(IFX_WRAPPER), "exists": IFX_WRAPPER.is_file()}
            if IFX_WRAPPER.is_file():
                d = self.build_root / "_tools" / "ifxver"
                d.mkdir(parents=True, exist_ok=True)
                (d / "ver.f90").write_text("program ver\nend program ver\n", encoding="ascii")
                r = run_proc(["cmd", "/c", str(IFX_WRAPPER), str(d / "ver.exe"), "/QV", "--", str(d / "ver.f90")],
                             env=make_env({}), cwd=d, timeout=600)
                text = r.stdout + "\n" + r.stderr
                info["version"] = next((l.strip() for l in text.splitlines() if "Intel(R) Fortran" in l), None)
            self._cache["ifx"] = info
        return self._cache["ifx"]

    def all(self) -> dict[str, Any]:
        return {"blade": self.blade(), "gpp": self.gpp(), "clang": self.clang(),
                "gfortran": self.gfortran(), "ifx": self.ifx()}


def pe_info(exe: Path) -> dict[str, Any]:
    """Imports and stack/heap reserve and commit of a PE file (objdump -p; M25)."""
    if not OBJDUMP.is_file():
        return {}
    r = run_proc([str(OBJDUMP), "-p", str(exe)], env=make_env({}), cwd=exe.parent, timeout=60)
    info: dict[str, Any] = {"imports": sorted({m.group(1) for m in re.finditer(r"DLL Name:\s*(\S+)", r.stdout)}, key=str.lower)}
    for fld in ("SizeOfStackReserve", "SizeOfStackCommit", "SizeOfHeapReserve", "SizeOfHeapCommit"):
        m = re.search(rf"{fld}\s+([0-9a-fA-F]+)", r.stdout)
        if m:
            info[fld] = int(m.group(1), 16)
    return info


def resolve_dlls(exe: Path, imports: list[str], env: dict[str, str]) -> dict[str, Any]:
    """Resolve each imported DLL the way the loader would for this run environment (exe
    directory, System32, Windows, then PATH) and hash it by its resolved path (M25)."""
    out: dict[str, Any] = {}
    dirs = [exe.parent, SYSTEM32, SYSTEM32.parent] + [Path(p) for p in env_path(env) if p]
    for name in imports:
        if name.lower().startswith(("api-ms-win-", "ext-ms-win-")):
            continue
        hit = next((d / name for d in dirs if (d / name).is_file()), None)
        out[name] = {"path": str(hit), "sha256": sha256_file(hit)} if hit else {"path": None}
    return out


def processor_power_settings() -> dict[str, Any]:
    """AC state and the active scheme's processor settings: max/min state, boost (M19)."""
    out: dict[str, Any] = {}
    for alias in ("PROCTHROTTLEMAX", "PROCTHROTTLEMIN", "PERFBOOSTMODE"):
        r = run_proc(["powercfg", "/q", "SCHEME_CURRENT", "SUB_PROCESSOR", alias],
                     env=make_env({}), cwd=BENCH_DIR, timeout=30)
        ac = re.search(r"Current AC Power Setting Index:\s*0x([0-9a-fA-F]+)", r.stdout)
        dc = re.search(r"Current DC Power Setting Index:\s*0x([0-9a-fA-F]+)", r.stdout)
        out[alias] = {"ac": int(ac.group(1), 16) if ac else None, "dc": int(dc.group(1), 16) if dc else None}
    r = run_proc(["powercfg", "/getactivescheme"], env=make_env({}), cwd=BENCH_DIR, timeout=30)
    out["active_scheme"] = r.stdout.strip()
    out["ac_line"] = ac_line()
    out["when"] = dt.datetime.now().isoformat(timespec="seconds")
    return out


def fingerprint() -> dict[str, Any]:
    """CPU, core counts, RAM, OS, Python, git SHA and the inherited thread env."""
    fp: dict[str, Any] = {"host": platform.node(), "os": platform.platform(),
                          "python": sys.version.split()[0], "logical_processors": os.cpu_count()}
    try:
        with winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE, r"HARDWARE\DESCRIPTION\System\CentralProcessor\0") as k:
            fp["cpu"] = winreg.QueryValueEx(k, "ProcessorNameString")[0].strip()
            fp["cpu_mhz_nominal"] = winreg.QueryValueEx(k, "~MHz")[0]
    except OSError:
        fp["cpu"] = platform.processor()
    r = run_proc(["powershell", "-NoProfile", "-Command",
                  "(Get-CimInstance Win32_Processor | Measure-Object -Property NumberOfCores -Sum).Sum"],
                 env=make_env({}), cwd=BENCH_DIR, timeout=60)
    fp["physical_cores"] = int(r.stdout.strip()) if r.stdout.strip().isdigit() else None
    ms = _MemoryStatusEx()
    ms.dwLength = ctypes.sizeof(_MemoryStatusEx)
    if _k32.GlobalMemoryStatusEx(ctypes.byref(ms)):
        fp["ram_gb"] = round(ms.ullTotalPhys / 2**30, 2)
        fp["ram_available_gb_at_start"] = round(ms.ullAvailPhys / 2**30, 2)
    g = run_proc(["git", "rev-parse", "HEAD"], env=make_env({}), cwd=REPO_DIR, timeout=30)
    fp["git_sha"] = g.stdout.strip() or None
    fp["inherited_env"] = {k: os.environ.get(k) for k in
                           ("OMP_NUM_THREADS", "OPENBLAS_DIR", "OPENBLAS_NUM_THREADS", "OMP_PROC_BIND", "OMP_PLACES")}
    fp["inherited_blade_knobs"] = {k: v for k, v in os.environ.items() if k.upper().startswith("BLADE_")}
    fp["harness_affinity"] = {"process": hex(_affinity()[0]), "system": hex(_affinity()[1])}
    return fp


def record_run_env(fp: dict[str, Any], config: dict[str, Any]) -> None:
    """The run environment per thread regime and the serial pin, in the fingerprint (M20)."""
    fp["run_env"] = {t: {k: v for k, v in config["run_env"][t].items() if not k.startswith("_")} for t in ("serial", "mt8")}
    fp["serial_affinity_cpu"] = config["serial_affinity_cpu"]
    fp["mt8_binding"] = "unbound" if not any(k in fp["run_env"]["mt8"] for k in ("OMP_PLACES", "OMP_PROC_BIND")) else "bound"


# ---------------------------------------------------------------------------
# Knobs and flags
# ---------------------------------------------------------------------------

def blade_knobs(regime: Regime, lane: str, config: dict[str, Any]) -> dict[str, str]:
    """Compile-time Blade knobs for an arm (README 3; config.json `blade_knobs`)."""
    knobs = {"BLADE_BLAS": "1" if regime.blas == "blas" else "0", "BLADE_EXE_CACHE": "0",
             "BLADE_LLVM_CLANG": str(CLANG)}
    knobs.update({k: str(v) for k, v in config["blade_knobs"]["all"].items() if not k.startswith("_")})
    if lane == "llvm":
        knobs["BLADE_LLVM"] = "1"
        knobs.update({k: str(v) for k, v in config["blade_knobs"]["llvm"].items() if not k.startswith("_")})
    knobs.update({k: str(v) for k, v in config["blade_knobs"][regime.fp].items() if not k.startswith("_")})
    if regime.threads == "serial":
        knobs["BLADE_OMP_THREADS"] = "1"
    return knobs


def run_knobs(regime: Regime, config: dict[str, Any]) -> dict[str, str]:
    """Run-time knobs (config.json `run_env`): thread counts, OMP_DYNAMIC, placement."""
    return {k: str(v) for k, v in config["run_env"][regime.threads].items() if not k.startswith("_")}


def fortran_flags(family: str, regime: Regime, config: dict[str, Any], twin: bool) -> list[str]:
    """The regime's flags for gfortran or ifx (never per kernel), plus the twin defines."""
    t = config["fortran_flags"][family]
    flags = list(t[regime.fp]) + list(t["serial_extra" if regime.threads == "serial" else "mt8_extra"])
    if twin:
        flags += list(config["fortran_twin_flags"][family])
    return flags


def fortran_blas_link(family: str) -> list[str]:
    """Link inputs for the blas regime's Fortran arms (OpenBLAS from OPENBLAS_DIR)."""
    ob = openblas_dir()
    if ob is None:
        raise HarnessError("blas regime needs OPENBLAS_DIR")
    if family == "gfortran":
        # `-L` and the directory as SEPARATE arguments: through `powershell -File`, a single
        # `-LC:\...` token is parsed as a PowerShell parameter `-LC:` and the drive is lost.
        return ["-L", str(ob / "lib"), "-lopenblas"]
    return [str(ob / "lib" / "libopenblas.lib")]


# ---------------------------------------------------------------------------
# Building one arm
# ---------------------------------------------------------------------------

_RE_FALLBACK = re.compile(r"^\[blade\] llvm lane refused: (.*); falling back to C\+\+\s*$", re.M)
_RE_EMIT = re.compile(r"^\[Emit\] (.+?)\s*$", re.M)
_RE_KEPT = re.compile(r"^\[Build\] kept (.+?)\s*$", re.M)
_RE_CPP_THREADS = re.compile(r"\bBLADE_OMP_PARALLEL\w*|#\s*pragma\s+omp\s+parallel|_Pragma\(\"omp parallel")
# Macro INVOCATIONS only (the header comment of every .ll mentions "reassoc"; .cpp comments may
# name macros): C++ `BLADE_OMP_[PARALLEL_FOR_]SIMD_REDUCTION(`, LLVM a fast-math flag on an
# instruction with comments stripped.
_RE_CPP_SIMDRED = re.compile(r"\bBLADE_OMP_(?:PARALLEL_FOR_)?SIMD_REDUCTION\(")
_RE_CPP_SERIAL_MARK = re.compile(r"\[omp\] requested but emitted serial")
_RE_LL_THREADS = re.compile(r"__kmpc_fork_call|\bGOMP_parallel\w*|\bomp_get_num_threads\b")
_RE_LL_REASSOC = re.compile(r"=\s*f(?:add|mul|sub|div|neg)\s+(?:(?:nnan|ninf|nsz|arcp|contract|afn)\s+)*(?:reassoc|fast)\b")


def _strip_ll_comments(text: str) -> str:
    return "\n".join(l.split(";", 1)[0] for l in text.splitlines())

# Build statuses: ok | failed | fallback | serial-only | no-blas | no-parallel-form | unbuilt | gated


@dataclass
class Build:
    """The record of one arm's build (serialized verbatim into the session JSON)."""
    arm: str
    regime: str
    size: str
    status: str = "unbuilt"
    reason: str = ""
    exe: str | None = None
    exe_sha256: str | None = None
    command: list[str] = field(default_factory=list)
    cwd: str = ""
    knobs: dict[str, str] = field(default_factory=dict)
    run_knobs: dict[str, str] = field(default_factory=dict)
    flags: list[str] = field(default_factory=list)
    compiler: dict[str, Any] = field(default_factory=dict)
    source: dict[str, Any] = field(default_factory=dict)
    build_wall_s: float = 0.0
    returncode: int | None = None
    stdout_tail: str = ""
    stderr_tail: str = ""
    evidence: dict[str, Any] = field(default_factory=dict)
    warnings: list[str] = field(default_factory=list)
    pe: dict[str, Any] = field(default_factory=dict)
    resolved_dlls: dict[str, Any] = field(default_factory=dict)
    expect_names: list[str] = field(default_factory=list)
    twin: bool = False
    lane: str | None = None


def prepare_source(kernel: Kernel, arm: ArmSpec, regime: Regime, size_name: str, armdir: Path,
                   stem: str) -> tuple[Path, dict[str, Any]]:
    """Copy the arm's committed source into ``armdir/src`` with sizes substituted."""
    src = kernel.source_for(arm, regime)
    text = src.read_bytes().decode("utf-8")
    new, lines = substitute_sizes(text, fortran=arm.family != "blade", sizes=kernel.sizes[size_name],
                                  full=kernel.sizes["full"], origin=str(src),
                                  exempt=kernel.raw.get("size_exempt", {}).get(src.name, []))
    out = armdir / "src" / (stem + src.suffix)
    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_bytes(new.encode("utf-8"))
    info = {"committed": str(src), "committed_sha256": sha256_file(src), "compiled": str(out),
            "compiled_sha256": sha256_file(out), "size_lines": lines}
    if arm.family != "blade" and BENCH_SUPPORT.is_file():
        info["bench_support_sha256"] = sha256_file(BENCH_SUPPORT)
        shutil.copy2(BENCH_SUPPORT, out.parent / "bench_support.f90.copy")
    return out, info


def build_arm(kernel: Kernel, arm: ArmSpec, regime: Regime, size_name: str, root: Path,
              tools: Toolchain, config: dict[str, Any], timeout: float) -> Build:
    """Build one arm of one kernel in one regime; never raises for a build failure."""
    b = Build(arm=arm.name, regime=regime.name, size=size_name, run_knobs=run_knobs(regime, config),
              twin=arm.twin, lane=arm.lane)
    armdir = root / kernel.name / size_name / regime.name / arm.name
    if armdir.exists():
        shutil.rmtree(armdir)
    armdir.mkdir(parents=True)
    b.cwd = str(armdir)
    src = kernel.source_for(arm, regime)
    if not src.is_file():
        b.status, b.reason = "unbuilt", f"source missing: {src}"
        return b
    stem = kernel.name + ("_inputonly" if (arm.twin and arm.family == "blade") else "")
    b.expect_names = [kernel.name] + ([stem] if stem != kernel.name else [])
    try:
        src_copy, b.source = prepare_source(kernel, arm, regime, size_name, armdir, stem)
    except HarnessError as ex:
        b.status, b.reason = "failed", f"size substitution: {ex}"
        return b
    if arm.family == "blade":
        _build_blade(b, kernel, arm, regime, src_copy, armdir, stem, tools, config, timeout)
    else:
        _build_fortran(b, arm, regime, src_copy, armdir, tools, config, timeout)
    if b.exe:
        run_env = make_env(b.run_knobs, intel=arm.family == "ifx", blas=regime.blas == "blas")
        b.resolved_dlls = resolve_dlls(Path(b.exe), b.pe.get("imports", []), run_env)
    (armdir / "build-command.txt").write_text(subprocess.list2cmdline(b.command) + "\n"
                                              + json.dumps(b.knobs) + "\n", encoding="utf-8")
    return b


def _finish(b: Build, r: ProcResult, exe: Path) -> bool:
    """Common post-processing of a compile; True when an executable came out."""
    b.returncode = r.returncode
    b.build_wall_s = round(r.wall_s, 3)
    b.stdout_tail, b.stderr_tail = tail(r.stdout), tail(r.stderr)
    Path(b.cwd, "build-stdout.txt").write_text(r.stdout, encoding="utf-8")
    Path(b.cwd, "build-stderr.txt").write_text(r.stderr, encoding="utf-8")
    if r.launch_error:
        b.status, b.reason = "failed", f"could not launch: {r.launch_error}"
        return False
    if r.timed_out:
        b.status, b.reason = "failed", f"build timed out after {r.wall_s:.0f}s"
        return False
    if r.returncode != 0 or not exe.is_file():
        first = next((l for l in (r.stderr + "\n" + r.stdout).splitlines() if "error" in l.lower()), "")
        b.status, b.reason = "failed", f"exit {r.returncode}: {first.strip()[:300]}"
        return False
    b.exe, b.exe_sha256 = str(exe), sha256_file(exe)
    b.pe = pe_info(exe)
    return True


def _build_blade(b: Build, k: Kernel, arm: ArmSpec, regime: Regime, src: Path, armdir: Path, stem: str,
                 tools: Toolchain, config: dict[str, Any], timeout: float) -> None:
    blade = tools.blade()
    if not blade["exists"]:
        b.status, b.reason = "unbuilt", f"no Blade.exe at {BLADE_EXE}"
        return
    if blade["toolchain_json_beside_exe"]:
        b.status, b.reason = "failed", "a blade.toolchain.json beside Blade.exe would flip unset gates; refusing"
        return
    exe = armdir / f"{stem}.exe"
    b.knobs = blade_knobs(regime, arm.lane or "cpp", config)
    b.flags = ["--verbose", "--print", "checksum,input_checksum"]
    b.command = [str(BLADE_EXE), "compile", str(src), "-o", str(exe), *b.flags]
    b.compiler = {"blade": blade, "gpp": tools.gpp()}
    if arm.lane == "llvm":
        b.compiler["clang"] = tools.clang()
    env = make_env(b.knobs, blas=regime.blas == "blas")
    r = run_proc(b.command, env=env, cwd=armdir, timeout=timeout)
    ok = _finish(b, r, exe)
    kept = _RE_KEPT.search(r.stderr)
    emits = _RE_EMIT.findall(r.stderr)
    fb = _RE_FALLBACK.search(r.stderr)
    kept_dir = Path(kept.group(1)) if kept else None
    gen_dir = armdir / "generated"
    artifacts: list[Path] = []
    headers: dict[str, str] = {}
    if kept_dir is not None and kept_dir.is_dir():
        gen_dir.mkdir(exist_ok=True)
        for f in kept_dir.iterdir():
            if f.suffix in (".ll", ".cpp", ".cu", ".log", ".c"):
                shutil.copy2(f, gen_dir / f.name)
                artifacts.append(gen_dir / f.name)
            elif f.suffix in (".hpp", ".h"):
                headers[f.name] = sha256_file(f)
                shutil.copy2(f, gen_dir / f.name)
        try:
            if BLADE_TEMP_ROOT.resolve() in kept_dir.resolve().parents:
                shutil.rmtree(kept_dir, ignore_errors=True)
        except OSError:
            pass
    ev: dict[str, Any] = {"emitted": emits, "kept_dir": str(kept_dir) if kept_dir else None,
                          "artifacts": [str(a) for a in artifacts], "runtime_headers_sha256": headers}
    b.evidence = ev
    lls = [a for a in artifacts if a.suffix == ".ll"]
    cpps = [a for a in artifacts if a.suffix in (".cpp", ".cu")]
    if lls:
        text = _strip_ll_comments(lls[0].read_text(encoding="utf-8", errors="replace"))
        ev.update(lane="llvm", thread_constructs=len(_RE_LL_THREADS.findall(text)),
                  reassoc_flags=len(_RE_LL_REASSOC.findall(text)))
    elif cpps:
        text = cpps[0].read_text(encoding="utf-8", errors="replace")
        ev.update(lane="cpp", thread_constructs=len(_RE_CPP_THREADS.findall(text)),
                  simd_reductions=len(_RE_CPP_SIMDRED.findall(text)),
                  omp_emitted_serial_markers=len(_RE_CPP_SERIAL_MARK.findall(text)))
    # The arm's ACTUAL fold form, from the artifact (blade plan is silent about it).
    nre = ev.get("simd_reductions", 0) + ev.get("reassoc_flags", 0)
    ev["fold_form"] = (f"reordered ({ev.get('simd_reductions', 0)} SIMD_REDUCTION)" if ev.get("simd_reductions")
                       else f"reordered ({ev.get('reassoc_flags', 0)} reassoc flags)" if ev.get("reassoc_flags")
                       else "in-order") if ev.get("lane") else None
    if not ok:
        return
    _blade_plan(b, src, armdir, env, timeout)
    if fb:
        ev["fallback_reason"] = fb.group(1)
    if arm.lane == "llvm":
        if fb:
            b.status, b.reason = "fallback", fb.group(1)
            return
        if ev.get("lane") != "llvm" or not any(e.endswith(".ll") for e in emits):
            b.status, b.reason = "failed", "no positive LLVM-lane evidence ([Emit] <stem>.ll + a .ll in the build dir)"
            return
        if cpps:
            b.status, b.reason = "failed", "LLVM arm's build dir also holds a C++ translation unit"
            return
    elif ev.get("lane") != "cpp":
        b.status, b.reason = "failed", "no positive C++-lane evidence ([Emit] <stem>.cpp)"
        return
    if regime.threads == "serial" and ev.get("thread_constructs", 0) > 0:
        b.status, b.reason = "failed", f"serial build carries {ev['thread_constructs']} thread construct(s)"
        return
    # M3: repro serial acceptance -- no reordered fold may reach a serial repro build.
    if regime.threads == "serial" and regime.fp == "repro":
        if ev.get("simd_reductions", 0) > 0:
            b.status, b.reason = "failed", f"repro serial build carries {ev['simd_reductions']} SIMD_REDUCTION expansion(s) (M3)"
            return
        if ev.get("reassoc_flags", 0) > 0:
            b.status, b.reason = "failed", f"repro serial build carries {ev['reassoc_flags']} reassoc FP flag(s) in the .ll (M3)"
            return
    if regime.fp in ("licensed", "fastmath") and arm.lane == "llvm" and ev.get("reassoc_flags", 0) == 0:
        b.warnings.append(f"{regime.fp} regime: the LLVM artifact carries no reassoc FP flags (no licensed fold reordered)")
    ev["blas_routed"] = any(d.lower().startswith("libopenblas") for d in b.pe.get("imports", []))
    if regime.blas == "blas" and not ev["blas_routed"]:
        if arm.lane == "llvm":
            # Observed 2026-10-17: `blade plan` reports [blas-routing v1] applied, but the LLVM
            # lane computes natively and links no OpenBLAS.  Not a blas measurement.
            b.status, b.reason = "no-blas", "LLVM lane does not route to BLAS (the exe imports no libopenblas.dll)"
            return
        b.status, b.reason = "failed", "blas regime: the executable imports no libopenblas.dll (nothing was routed)"
        return
    if regime.threads == "mt8":
        if arm.lane == "llvm":
            # The LLVM lane has no OpenMP: a licence compiles serial with no refusal line.
            b.status, b.reason = "serial-only", "LLVM lane has no OpenMP"
            return
        if not k.blade_has_parallel_form(regime):
            b.status, b.reason = "no-parallel-form", "kernel.json no_parallel_form (spec: no licensed parallel spelling)"
            return
        if ev.get("thread_constructs", 0) == 0:
            if regime.blas == "blas" and ev["blas_routed"]:
                # the parallelism is OpenBLAS's own threads, not an OpenMP region
                ev["parallelism"] = f"library threads (OPENBLAS_NUM_THREADS={b.run_knobs.get('OPENBLAS_NUM_THREADS')})"
                b.status = "ok"
                return
            b.status, b.reason = "no-parallel-form", "the mt8 build contains no thread-parallel construct"
            return
        ev["parallelism"] = f"openmp ({ev['thread_constructs']} construct(s))"
        if regime.fp == "repro":
            # BLADE_FP_REPRO=1 serializes the mt8 fold teams as well
            ev["parallelism"] += "; threaded maps, serial folds (BLADE_FP_REPRO)"
    elif regime.blas == "blas" and ev["blas_routed"]:
        ev["parallelism"] = f"library threads (OPENBLAS_NUM_THREADS={b.run_knobs.get('OPENBLAS_NUM_THREADS')})"
    else:
        ev["parallelism"] = "serial"
    b.status = "ok"


def _blade_plan(b: Build, src: Path, armdir: Path, env: dict[str, str], timeout: float) -> None:
    """Record `blade plan` under the arm's own knobs (README 6; M24: its omp lines)."""
    r = run_proc([str(BLADE_EXE), "plan", str(src)], env=env, cwd=armdir, timeout=timeout)
    out = armdir / "plan.txt"
    out.write_text(r.stdout + ("\n--- stderr ---\n" + r.stderr if r.stderr.strip() else ""), encoding="utf-8")
    lines = r.stdout.splitlines()
    b.evidence["plan_file"] = str(out)
    b.evidence["plan_returncode"] = r.returncode
    b.evidence["plan_decisions"] = [l.strip() for l in lines if re.search(r"\b(applied|declined)\b", l)][:200]
    b.evidence["plan_omp"] = [l.strip() for l in lines if "[omp" in l][:50]
    # `fold form: ...` per C++ fold site (5f4b33ea); covers the C++ lane only, so the
    # artifact-derived counts (simd_reductions / reassoc_flags) are kept beside it.
    b.evidence["plan_fold_form"] = [l.strip() for l in lines if "fold form:" in l][:100]


def _build_fortran(b: Build, arm: ArmSpec, regime: Regime, src: Path, armdir: Path, tools: Toolchain,
                   config: dict[str, Any], timeout: float) -> None:
    exe = armdir / f"{arm.name}.exe"
    b.flags = fortran_flags(arm.family, regime, config, arm.twin)
    link = fortran_blas_link(arm.family) if regime.blas == "blas" else []
    if arm.family == "gfortran":
        if not GFORTRAN.is_file() or not GFORTRAN_WRAPPER.is_file():
            b.status, b.reason = "unbuilt", "gfortran or bench/fortran/build_gfortran.ps1 missing"
            return
        b.command = ["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", str(GFORTRAN_WRAPPER),
                     str(exe), *b.flags, "--", str(src), *link]
        b.compiler = {"gfortran": tools.gfortran(), "wrapper": str(GFORTRAN_WRAPPER),
                      "wrapper_sha256": sha256_file(GFORTRAN_WRAPPER)}
    else:
        if not IFX_WRAPPER.is_file():
            b.status, b.reason = "unbuilt", f"ifx wrapper {IFX_WRAPPER} not present"
            return
        b.command = ["cmd", "/c", str(IFX_WRAPPER), str(exe), *b.flags, "--", str(src), *link]
        b.compiler = {"ifx": tools.ifx(), "wrapper": str(IFX_WRAPPER), "wrapper_sha256": sha256_file(IFX_WRAPPER)}
    r = run_proc(b.command, env=make_env({}), cwd=armdir, timeout=timeout)
    if arm.family == "ifx" and r.returncode == 4 and "ifx not on PATH" in r.stderr:
        b.status, b.reason = "unbuilt", "build_ifx.cmd: ifx not on PATH after environment setup"
        b.stderr_tail = tail(r.stderr)
        return
    plus = [l[2:].strip() for l in r.stderr.splitlines() if l.startswith("+ ")]
    if plus:
        b.evidence["effective_command"] = plus[-1]
    chatter = [l for l in r.stdout.splitlines() + r.stderr.splitlines()
               if l.strip() and not l.startswith("+ ") and ("warning" in l.lower() or "remark" in l.lower())]
    if chatter:
        b.warnings.append(f"compiler diagnostics: {chatter[0].strip()[:200]}")
    if _finish(b, r, exe):
        imports = [d.lower() for d in b.pe.get("imports", [])]
        b.evidence["blas_routed"] = any(d.startswith("libopenblas") for d in imports)
        if regime.blas == "blas" and not b.evidence["blas_routed"] and not arm.twin:
            b.status, b.reason = "failed", "blas regime: the executable imports no libopenblas.dll (no dsyrk route)"
            return
        omp_rt = any(d.startswith(("libgomp", "libiomp")) for d in imports)
        if regime.blas == "blas" and b.evidence["blas_routed"]:
            par = f"library threads (OPENBLAS_NUM_THREADS={b.run_knobs.get('OPENBLAS_NUM_THREADS')})"
            b.evidence["parallelism"] = par + (" + openmp runtime" if (omp_rt and regime.threads == "mt8") else "")
        elif regime.threads == "mt8":
            b.evidence["parallelism"] = "openmp runtime linked" if omp_rt else "serial (no OpenMP runtime imported)"
        else:
            b.evidence["parallelism"] = "serial"
        b.status = "ok"
        if arm.family == "ifx" and regime.threads == "mt8" and not (Path(b.cwd) / "libiomp5md.dll").is_file():
            b.warnings.append("mt8 ifx build has no libiomp5md.dll beside the exe")


# ---------------------------------------------------------------------------
# Running one arm and parsing the output contract
# ---------------------------------------------------------------------------

_NUM = r"[-+]?(?:\d+\.?\d*|\.\d+)(?:[eEdD][-+]?\d+)?|[-+]?(?i:nan|inf(?:inity)?)"
_RE_TIME = re.compile(rf"^\s*(\S+) completed in\s+({_NUM})\s*s\s*$")
_RE_CHECK = re.compile(rf"^\s*checksum\s*=\s*({_NUM})\s*$")
_RE_INPUT = re.compile(rf"^\s*input_checksum\s*=\s*({_NUM})\s*$")
_RE_SCALAR_EXTRA = re.compile(rf"^\s*[A-Za-z_]\w*\s*=\s*(?:{_NUM}|true|false)\s*$")


def _num(s: str) -> float:
    return float(s.replace("d", "e").replace("D", "e"))


@dataclass
class Sample:
    """One run of one arm (or of a control)."""
    arm: str
    kind: str                       # contract | warmup | round | control
    ok: bool = False
    reason: str = ""
    time_s: float | None = None
    checksum: float | None = None
    input_checksum: float | None = None
    checksum_text: str | None = None
    input_checksum_text: str | None = None
    name: str | None = None
    wall_s: float = 0.0
    returncode: int | None = None
    extra_stdout_lines: int = 0
    stderr_tail: str = ""
    round: int | None = None
    position: int | None = None
    started: str = ""
    peak_working_set: int | None = None
    page_faults: int | None = None
    affinity_mask: str | None = None
    excursion_dropped: bool = False
    excursion_ratio: float | None = None
    validation: dict[str, Any] = field(default_factory=dict)


def parse_contract(s: Sample, stdout: str, stderr: str, returncode: int | None, *,
                   expect_names: list[str], allow_scalar_extras: bool) -> None:
    """Check the three-line output contract (README 5, review M14/M28) into ``s``."""
    s.returncode, s.stderr_tail = returncode, tail(stderr, 10)
    problems: list[str] = []
    counts = {"time": 0, "checksum": 0, "input": 0}
    for line in stdout.replace("\r\n", "\n").split("\n"):
        if not line.strip():
            continue
        if m := _RE_TIME.match(line):
            counts["time"] += 1
            s.name, s.time_s = m.group(1), _num(m.group(2))
        elif m := _RE_CHECK.match(line):
            counts["checksum"] += 1
            s.checksum_text, s.checksum = m.group(1), _num(m.group(1))
        elif m := _RE_INPUT.match(line):
            counts["input"] += 1
            s.input_checksum_text, s.input_checksum = m.group(1), _num(m.group(1))
        else:
            s.extra_stdout_lines += 1
            # Only the LLVM lane's scalar echoes are tolerated (it ignores --print and prints
            # every top-level binding, e.g. `p = 61`); an echoed array or any other line fails.
            if not (allow_scalar_extras and _RE_SCALAR_EXTRA.match(line)):
                problems.append(f"unexpected stdout line: {line.strip()[:80]!r}")
    for key, label in (("time", "`<name> completed in <t>s`"), ("checksum", "`checksum = <v>`"),
                       ("input", "`input_checksum = <v>`")):
        if counts[key] == 0:
            problems.append(f"no {label} line")
        elif counts[key] > 1:
            problems.append(f"duplicate {label} lines ({counts[key]})")
    if returncode != 0:
        problems.append(f"exit code {returncode}")
    if re.search(r"error", stderr, re.I):
        problems.append("stderr mentions 'error': " + tail(stderr, 3).replace("\n", " | ")[:200])
    if s.time_s is not None:
        if s.name not in expect_names:
            problems.append(f"timing line names {s.name!r}, expected {' or '.join(expect_names)}")
        if not math.isfinite(s.time_s) or s.time_s <= 0.0:
            problems.append(f"nonpositive or non-finite duration {s.time_s!r} (M14)")
    for label, v in (("checksum", s.checksum), ("input_checksum", s.input_checksum)):
        if v is not None and not math.isfinite(v):
            problems.append(f"{label} is not finite")
    s.ok = not problems
    s.reason = "; ".join(problems[:4])


def run_exe(b: Build, regime: Regime, timeout: float, kind: str, affinity: int | None) -> Sample:
    """Run a built executable directly (never through `blade run`) and parse its contract."""
    assert b.exe is not None
    env = make_env(b.run_knobs, intel=b.arm.startswith("ifx"), blas=regime.blas == "blas")
    s = Sample(arm=b.arm, kind=kind, started=dt.datetime.now().isoformat(timespec="milliseconds"))
    r = run_proc([b.exe], env=env, cwd=Path(b.cwd), timeout=timeout, affinity=affinity)
    s.wall_s = round(r.wall_s, 4)
    s.affinity_mask = hex(affinity) if affinity is not None else None
    if r.memory:
        s.peak_working_set, s.page_faults = r.memory["peak_working_set"], r.memory["page_faults"]
    if r.launch_error or r.timed_out:
        s.ok, s.reason = False, r.launch_error or f"timed out after {timeout:.1f}s (watchdog)"
        return s
    parse_contract(s, r.stdout, r.stderr, r.returncode, expect_names=b.expect_names,
                   allow_scalar_extras=b.lane == "llvm")
    return s


def validate(s: Sample, ref: dict[str, Any], *, tol: float, input_tol: float, twin: bool) -> None:
    """Validate BOTH checksums of one sample against the reference (M12); updates ``s``.

    input_checksum: relative error <= INPUT_REL_TOL (1e-14), or a larger explicit
    kernel.json input_tolerance.  checksum: relative error <= the regime tolerance; a twin
    must print exactly `checksum = 0.0` (M28).
    """
    if not s.ok:
        return
    assert s.checksum is not None and s.input_checksum is not None
    problems = []
    ierr = rel_err(s.input_checksum, ref["input_checksum"])
    itol = max(INPUT_REL_TOL, input_tol)
    if not ierr <= itol:
        problems.append(f"input_checksum mismatch, rel err {ierr:.3g} (tol {itol:g})")
    s.validation = {"input_rel_err": ierr, "reference": ref.get("_source")}
    if twin:
        if s.checksum != 0.0:
            problems.append(f"twin printed checksum = {s.checksum_text}, must be 0.0")
    else:
        cerr = rel_err(s.checksum, ref["checksum"])
        s.validation["checksum_rel_err"] = cerr
        if cerr > tol:
            problems.append(f"checksum mismatch, rel err {cerr:.3g} (tol {tol:g})")
    if problems:
        s.ok, s.reason = False, "DQ: " + "; ".join(problems)


# ---------------------------------------------------------------------------
# Statistics (M15/M16/M17): round-block bootstrap, incremental differences
# ---------------------------------------------------------------------------

def describe(xs: list[float]) -> dict[str, Any]:
    """n, min, median, quartiles and IQR of a sample (seconds)."""
    if not xs:
        return {"n": 0}
    if len(xs) >= 2:
        q = statistics.quantiles(xs, n=4, method="inclusive")
        q1, q3 = q[0], q[2]
    else:
        q1 = q3 = xs[0]
    return {"n": len(xs), "min": min(xs), "median": statistics.median(xs), "q1": q1, "q3": q3,
            "iqr": q3 - q1, "max": max(xs), "mean": statistics.fmean(xs)}


def verdict_of(ratio: float, lo: float, hi: float, delta: float) -> str:
    """M17 with practical threshold delta (ratio = median(arm)/median(Blade ref); > 1 = Blade
    faster): win (CI > 1 and ratio >= 1+delta), detectable-win (CI > 1, ratio < 1+delta),
    loss / detectable-loss symmetric, equivalent (CI within [1/(1+delta), 1+delta]),
    otherwise inconclusive.  Intervals are pointwise."""
    up, down = 1.0 + delta, 1.0 / (1.0 + delta)
    if lo > 1.0:
        return "win" if ratio >= up else "detectable-win"
    if hi < 1.0:
        return "loss" if ratio <= down else "detectable-loss"
    if lo >= down and hi <= up:
        return "equivalent"
    return "inconclusive"


def _pct(xs: list[float]) -> tuple[float, float]:
    xs = sorted(xs)
    return xs[int(0.025 * (len(xs) - 1))], xs[int(math.ceil(0.975 * (len(xs) - 1)))]


class RoundBootstrap:
    """Resamples whole ROUNDS jointly (M15/M16): every statistic of one draw is computed
    from the same resampled set of rounds, so shared twins, the reference and the control
    stay correlated as they were measured.  No draw is ever discarded."""

    def __init__(self, series: dict[str, dict[int, float]], resamples: int, seed: int) -> None:
        self.series = series
        # The union of rounds: an arm may lack a round whose sample was dropped as an isolated
        # excursion; each draw then uses that arm's samples in the drawn rounds it has.
        self.rounds = sorted(set().union(*(set(v) for v in series.values()))) if series else []
        rng = random.Random(seed)
        self.draws = [[rng.choice(self.rounds) for _ in self.rounds] for _ in range(resamples)] if self.rounds else []
        self.resamples, self.seed = resamples, seed
        self._med: dict[str, list[float]] = {}

    def medians(self, arm: str) -> list[float]:
        if arm not in self._med:
            s = self.series[arm]
            out = []
            for d in self.draws:
                vals = [s[r] for r in d if r in s]
                out.append(statistics.median(vals) if vals else math.nan)
            self._med[arm] = out
        return self._med[arm]

    def point(self, arm: str) -> float:
        return statistics.median(list(self.series[arm].values()))

    def ratio(self, arm: str, ref: str, delta: float) -> dict[str, Any]:
        """median(arm) / median(ref) with a 95% percentile interval."""
        if len(self.rounds) < 2 or arm not in self.series or ref not in self.series:
            return {"ratio": None, "note": "fewer than 2 shared valid rounds"}
        pr = self.point(ref)
        if pr <= 0:
            return {"ratio": None, "note": "reference median not positive"}
        rs = [a / r for a, r in zip(self.medians(arm), self.medians(ref))]
        lo, hi = _pct(rs)
        point = self.point(arm) / pr
        return {"ratio": point, "ci95": [lo, hi], "verdict": verdict_of(point, lo, hi, delta),
                "rounds": len(self.rounds), "resamples": self.resamples, "seed": self.seed,
                "method": "round-block bootstrap"}

    def incremental(self, arm: str, twin: str) -> dict[str, Any]:
        """median(arm) - median(twin): the increment over the arm's OWN twin (M9), not
        kernel time."""
        if len(self.rounds) < 2 or arm not in self.series or twin not in self.series:
            return {}
        d = [a - t for a, t in zip(self.medians(arm), self.medians(twin))]
        lo, hi = _pct(d)
        return {"incremental": self.point(arm) - self.point(twin), "ci95": [lo, hi], "twin": twin}

    def incremental_ratio(self, arm: str, twin: str, ref: str, ref_twin: str, delta: float) -> dict[str, Any]:
        """inc(arm) / inc(ref), defined ONLY when the reference's incremental interval
        excludes 0 (M16); every draw is kept (a zero denominator draw yields +-inf)."""
        ri = self.incremental(ref, ref_twin)
        if not ri:
            return {"ratio": None, "note": "reference has no valid twin"}
        if not ri["ci95"][0] > 0:
            return {"ratio": None, "note": "undefined: the reference's incremental interval includes 0"}
        num = [a - t for a, t in zip(self.medians(arm), self.medians(twin))]
        den = [a - t for a, t in zip(self.medians(ref), self.medians(ref_twin))]
        rs = [n / d if d != 0 else math.copysign(math.inf, n) for n, d in zip(num, den)]
        lo, hi = _pct(rs)
        point = (self.point(arm) - self.point(twin)) / ri["incremental"]
        return {"ratio": point, "ci95": [lo, hi], "verdict": verdict_of(point, lo, hi, delta)}


def trend(xs_by_round: dict[int, float]) -> float | None:
    """|median(second half) / median(first half) - 1| over a block's rounds (M18)."""
    rs = sorted(xs_by_round)
    if len(rs) < 4:
        return None
    h = len(rs) // 2
    a = statistics.median([xs_by_round[r] for r in rs[:h]])
    b = statistics.median([xs_by_round[r] for r in rs[-h:]])
    return abs(b / a - 1.0) if a > 0 else None


# ---------------------------------------------------------------------------
# The session
# ---------------------------------------------------------------------------

@dataclass
class Options:
    """Everything a verb decided from the command line."""
    mode: str                       # smoke | check | bench
    kernels: list[Kernel]
    regimes: list[Regime] | None
    arms: list[str] | None
    size: str
    rounds: int | None
    cooldown: float
    kernel_cooldown: float
    run_timeout: float
    build_timeout: float
    resamples: int
    seed: int
    allow_battery: bool
    allow_unpinned: bool
    skip_smoke_check: bool
    strict_unbuilt: bool
    build_root: Path
    out_dir: Path | None
    config: dict[str, Any]
    argv: list[str]
    validation_dir: Path = VALIDATION_DIR


ACCEPTED_SMOKE_STATUSES = ("ok", "fallback", "serial-only", "no-blas", "no-parallel-form")
GATE_PASS = ("ok", "fallback", "no-blas")


class Session:
    """One harness invocation: builds, runs, validation, statistics, outputs."""

    def __init__(self, opt: Options) -> None:
        self.opt = opt
        self.started = dt.datetime.now()
        self.tools = Toolchain(opt.build_root)
        self.pins = Pins(opt.validation_dir)
        self.failures: list[str] = []
        self.flags: list[str] = []
        self.warnings: list[str] = []
        self.blocks: list[dict[str, Any]] = []
        self.controls: list[dict[str, Any]] = []
        self._ctl: list[dict[str, Any]] = []         # live control objects (Build, refs)
        self.serial_valid: dict[tuple[str, str, str], str] = {}    # (kernel, serial regime, arm) -> status
        self.manifest: list[dict[str, Any]] = []
        self.data: dict[str, Any] = {"schema": 2, "harness_version": HARNESS_VERSION, "mode": opt.mode,
                                     "argv": opt.argv}
        self.delta = float(opt.config["verdict_delta"])
        host = platform.node()
        if opt.mode == "bench":
            base = (opt.out_dir.resolve() if opt.out_dir else RESULTS_DIR / f"{self.started:%Y-%m-%d}-{host}")
            self.bundle = base / f"session-{self.started:%H%M%S}"
            self.build_root = self.bundle / "build"
        else:
            base = (opt.out_dir.resolve() if opt.out_dir else opt.build_root / "sessions" / f"{self.started:%Y-%m-%d}-{host}")
            self.bundle = base / f"session-{self.started:%H%M%S}-{opt.mode}"
            self.build_root = opt.build_root
        self.bundle.mkdir(parents=True, exist_ok=False)
        self.serial_mask = 1 << int(opt.config["serial_affinity_cpu"])

    # -- helpers ---------------------------------------------------------------

    def fail(self, msg: str) -> None:
        self.failures.append(msg)
        log(f"  FAIL: {msg}")

    def checkpoint(self) -> None:
        """Write the session JSON after every run (M27)."""
        self.data.update({"failures": self.failures, "flags": self.flags, "warnings": self.warnings,
                          "blocks": self.blocks, "controls": self.controls, "manifest": self.manifest,
                          "checkpoint": dt.datetime.now().isoformat(timespec="seconds")})
        try:
            write_json_atomic(self.bundle / "session.json", self.data)
        except OSError as ex:
            # a failed checkpoint is logged, never fatal: the next one rewrites everything
            self.data.setdefault("checkpoint_failures", []).append(f"{dt.datetime.now().isoformat(timespec='seconds')}: {ex!r}")
            log(f"  WARNING: checkpoint failed ({ex!r}); continuing")

    def tol(self, k: Kernel, regime: Regime) -> float:
        """checksum tolerance: kernel.json override, else config.json `tolerance`."""
        return float(k.tolerance.get(regime.fp, self.opt.config["tolerance"][regime.fp]))

    def affinity_for(self, regime: Regime) -> int | None:
        return self.serial_mask if regime.threads == "serial" else None

    def run_timeout(self) -> float:
        return min(self.opt.run_timeout, RUN_TIMEOUT_CAP_S) if self.opt.mode == "bench" else self.opt.run_timeout

    def regimes_for(self, k: Kernel) -> list[Regime]:
        if self.opt.regimes is not None:
            wanted = [r for r in self.opt.regimes if r.name in k.regimes]
        elif self.opt.mode == "bench":
            wanted = [Regime.parse(r) for r in k.regimes]
        else:
            wanted = [Regime.parse(DEFAULT_SMOKE_REGIME)]
        return sorted(wanted, key=lambda r: REGIME_ORDER.index(r.name))

    def arms_for(self, k: Kernel, regime: Regime) -> list[ArmSpec]:
        names = k.arms_in(regime)
        if self.opt.arms is not None:
            names = [a for a in names if a in self.opt.arms or ArmSpec.parse(a).base in self.opt.arms]
        return [ArmSpec.parse(a) for a in names]

    def reference_for(self, k: Kernel, size: str) -> dict[str, Any] | None:
        pin = self.pins.get(k, size)
        if pin is None:
            return None
        return {"checksum": float(pin["checksum"]), "input_checksum": float(pin["input_checksum"]),
                "_source": f"pin {Path(pin['_file']).name}"}

    def _evidence_hashes(self, src: Path, a: ArmSpec) -> dict[str, str | None]:
        return {"source": sha256_file(src),
                "bench_support": sha256_file(BENCH_SUPPORT) if (a.family != "blade" and BENCH_SUPPORT.is_file()) else None,
                "blade_dll": self.tools.blade().get("dll_sha256") if a.family == "blade" else None,
                "config": build_config_hash(self.opt.config)}

    # -- preflight ---------------------------------------------------------------

    def preflight(self) -> None:
        log("fingerprinting the machine")
        self.data["machine"] = fingerprint()
        record_run_env(self.data["machine"], self.opt.config)
        self.data["power"] = processor_power_settings()
        p = self.data["power"]
        log(f"power: {p['ac_line']}, max processor state AC {p['PROCTHROTTLEMAX']['ac']}%, "
            f"boost mode AC {p['PERFBOOSTMODE']['ac']}, {p['active_scheme']}")
        if self.opt.mode == "bench":
            if p["ac_line"] == "battery":
                if not self.opt.allow_battery:
                    raise HarnessError("on battery: refusing to benchmark (pass --allow-battery to taint the session)")
                self.flags.append("tainted: ran on battery (--allow-battery)")
            elif p["ac_line"] == "unknown":
                self.flags.append("AC line state unknown")
        self.data["toolchain"] = self.tools.all()
        if self.tools.blade()["toolchain_json_beside_exe"]:
            raise HarnessError("blade.toolchain.json beside Blade.exe would flip unset gates; remove it")
        self.data["pins"] = {"files": self.pins.files, "entries": len(self.pins.entries)}
        self.data["protocol"] = {
            "rounds_override": self.opt.rounds, "min_rounds": MIN_ROUNDS, "cooldown_s": self.opt.cooldown,
            "kernel_cooldown_s": self.opt.kernel_cooldown, "run_timeout_s": self.run_timeout(),
            "build_timeout_s": self.opt.build_timeout, "bootstrap": "round-block", "resamples": self.opt.resamples,
            "seed": self.opt.seed, "order": "seeded random permutation per round (arms, twins, controls)",
            "thermal_factor": THERMAL_FACTOR, "thermal_min_median_s": THERMAL_MIN_MEDIAN_S, "dq_run_s": DQ_RUN_S,
            "trend_limit": TREND_LIMIT, "control_limit": CONTROL_LIMIT, "verdict_delta": self.delta,
            "memory_bound_s": MEMORY_BOUND_S, "serial_affinity_mask": hex(self.serial_mask),
            "config": self.opt.config, "config_sha256": config_hash(self.opt.config)}

    def presession_checks(self) -> None:
        """M28, before a production session: pins exist, required twins exist, and every
        planned cell has passing smoke evidence for the exact current source hashes."""
        evidence = json.loads(SMOKE_EVIDENCE.read_text(encoding="utf-8")) if SMOKE_EVIDENCE.is_file() else {}
        problems: list[str] = []
        for k in self.opt.kernels:
            if self.pins.get(k, self.opt.size) is None and not self.opt.allow_unpinned:
                problems.append(f"{k.name}: no pin for size {self.opt.size!r} in bench/validation/pins*.json")
            for regime in self.regimes_for(k):
                for a in self.arms_for(k, regime):
                    src = k.source_for(a, regime)
                    if not src.is_file():   # any unbuilt planned cell refuses a production session
                        problems.append(f"{k.name}/{regime.name}/{a.name}: required source {src.name} missing")
                        continue
                    if self.opt.skip_smoke_check:
                        continue
                    key = f"{k.name}|{regime.name}|{a.name}"
                    e = evidence.get(key)
                    if e is None or e.get("hashes") != self._evidence_hashes(src, a) or e.get("status") not in ACCEPTED_SMOKE_STATUSES:
                        why = "none" if e is None else ("stale hashes" if e.get("hashes") != self._evidence_hashes(src, a) else e.get("status"))
                        problems.append(f"{key}: no passing smoke evidence for the current sources ({why}); "
                                        f"run `smoke --kernels {k.name} --regimes {regime.name}`")
        if self.opt.skip_smoke_check:
            self.flags.append("tainted: --skip-smoke-check")
        if self.opt.allow_unpinned:
            self.flags.append("tainted: --allow-unpinned (blade-cpp cross-check instead of the oracle)")
        if problems:
            raise HarnessError("pre-session checks failed:\n  " + "\n  ".join(problems[:40])
                               + (f"\n  ... {len(problems) - 40} more" if len(problems) > 40 else ""))

    def record_smoke_evidence(self) -> None:
        """After smoke/check: remember which (kernel, regime, arm) passed for which hashes."""
        evidence = json.loads(SMOKE_EVIDENCE.read_text(encoding="utf-8")) if SMOKE_EVIDENCE.is_file() else {}
        kmap = {k.name: k for k in self.opt.kernels}
        for blk in self.blocks:
            if blk["size"] != "smoke" or blk["kernel"] not in kmap:
                continue
            k = kmap[blk["kernel"]]
            regime = Regime.parse(blk["regime"])
            for arm, e in blk["arms"].items():
                a = ArmSpec.parse(arm)
                src = k.source_for(a, regime)
                if not src.is_file():
                    continue
                evidence[f"{k.name}|{regime.name}|{arm}"] = {
                    "status": e["status"], "pinned": blk.get("checksum_reference", "").startswith("pin"),
                    "hashes": self._evidence_hashes(src, a),
                    "when": dt.datetime.now().isoformat(timespec="seconds"), "session": str(self.bundle)}
        SMOKE_EVIDENCE.parent.mkdir(parents=True, exist_ok=True)
        write_json_atomic(SMOKE_EVIDENCE, evidence)

    # -- controls (M18) -----------------------------------------------------------

    def setup_controls(self) -> None:
        if self.opt.mode != "bench":
            return
        allk = {k.name: k for k in discover(self.opt.kernels[0].dir.parent)}
        allk.update({k.name: k for k in self.opt.kernels})
        for spec in self.opt.config["controls"]:
            if spec.get("name", "").startswith("_"):
                continue
            rec: dict[str, Any] = {"spec": spec, "build": None, "samples": [], "valid": False, "per_block": {},
                                   "baseline": None}
            self.controls.append(rec)
            k = allk.get(spec["kernel"])
            if k is None:
                self.flags.append(f"control {spec['name']}: kernel {spec['kernel']!r} not found")
                continue
            regime = Regime.parse(spec["regime"])
            log(f"control {spec['name']}: {k.name}/{spec['arm']}/{regime.name}/{spec['size']}")
            b = build_arm(k, ArmSpec.parse(spec["arm"]), regime, spec["size"], self.build_root / "_controls",
                          self.tools, self.opt.config, self.opt.build_timeout)
            rec["build"] = dataclasses.asdict(b)
            if b.status != "ok":
                self.flags.append(f"control {spec['name']}: build {b.status}: {b.reason}")
                continue
            ref = self.reference_for(k, spec["size"])
            if ref is None:
                self.flags.append(f"control {spec['name']}: no pin for {k.name}/{spec['size']}; control runs unvalidated")
            live = {"rec": rec, "b": b, "regime": regime, "ref": ref, "k": k}
            s = self.run_control(live, "warmup", None, None)
            if s.ok:
                rec["valid"] = True
                self._ctl.append(live)
            else:
                self.flags.append(f"control {spec['name']}: warm-up invalid: {s.reason}")

    def run_control(self, live: dict[str, Any], kind: str, rnd: int | None, block_key: str | None) -> Sample:
        rec, b, regime, ref, k = live["rec"], live["b"], live["regime"], live["ref"], live["k"]
        s = run_exe(b, regime, self.run_timeout(), kind, self.affinity_for(regime))
        s.round = rnd
        if ref is not None:
            validate(s, ref, tol=self.tol(k, regime), input_tol=k.input_tolerance, twin=False)
        prev = [x["time_s"] for x in rec["samples"] if x["ok"] and x["time_s"] is not None]
        d = dataclasses.asdict(s)
        d["block"] = block_key
        rec["samples"].append(d)
        if block_key is not None and rnd is not None:
            rec["per_block"].setdefault(block_key, {})[str(rnd)] = s.time_s if s.ok else None
        self._thermal(f"control {rec['spec']['name']}", s, prev)
        return s

    # -- one kernel x regime -------------------------------------------------------

    def run_block(self, k: Kernel, regime: Regime, size: str, gate_only: bool = False) -> dict[str, Any]:
        """Build, validate, time and summarize every arm of one kernel in one regime.

        The block dict is appended to the session BEFORE anything runs and filled in place,
        so an abort keeps its partial evidence (M27)."""
        opt = self.opt
        bench = opt.mode == "bench" and not gate_only
        mt8 = regime.threads == "mt8"
        key = f"{k.name}/{regime.name}"
        blk: dict[str, Any] = {"kernel": k.name, "group": k.group, "regime": regime.name, "size": size,
                               "sizes": k.sizes[size], "tolerance": self.tol(k, regime),
                               "input_tolerance": k.input_tolerance, "hypothesis": k.hypothesis,
                               "gate_only": gate_only, "started": dt.datetime.now().isoformat(timespec="seconds"),
                               "flags": [], "arms": {}}
        self.blocks.append(blk)
        if bench:
            blk["power"] = processor_power_settings()
            if blk["power"]["ac_line"] == "battery" and not opt.allow_battery:
                raise SessionAbort("AC power lost during the session")
            if blk["power"]["PROCTHROTTLEMAX"] != self.data["power"]["PROCTHROTTLEMAX"]:
                blk["flags"].append("processor max-state setting changed since session start")
        ref = self.reference_for(k, size)
        blk["checksum_reference"] = ref["_source"] if ref else None
        arms = self.arms_for(k, regime)
        builds: dict[str, Build] = {}
        state: dict[str, dict[str, Any]] = {}
        # 1. build every arm up front (mt8 arms only when their own serial arm validated)
        for a in arms:
            entry: dict[str, Any] = {"status": "unbuilt", "reason": "", "samples": []}
            blk["arms"][a.name] = entry
            state[a.name] = entry
            if mt8 and not self._serial_gate_ok(k, regime, a):
                b = Build(arm=a.name, regime=regime.name, size=size, status="gated",
                          reason=f"its own serial arm ({regime.serial_twin().name}) did not validate",
                          twin=a.twin, lane=a.lane)
            else:
                log(f"build {k.name} {regime.name} {size} {a.name}")
                b = build_arm(k, a, regime, size, self.build_root, self.tools, opt.config, opt.build_timeout)
            builds[a.name] = b
            entry["build"] = dataclasses.asdict(b)
            entry["status"], entry["reason"] = b.status, b.reason
            if regime.threads == "serial" and b.status != "ok":
                self.serial_valid[(k.name, regime.name, a.name)] = b.status
            if mt8 and a.name in k.parallel_notes:
                entry["note"] = k.parallel_notes[a.name]
            log(f"  -> {b.status}" + (f": {b.reason}" if b.reason else "") + f" ({b.build_wall_s:.1f}s)"
                + "".join(f"\n     note: {w}" for w in b.warnings))
            if b.status == "failed":
                self.fail(f"{key}/{a.name}: build failed: {b.reason}")
            elif b.status == "fallback":
                if k.llvm_expected:
                    self.fail(f"{key}/{a.name}: UNEXPECTED LLVM FALLBACK: {b.reason}")
                else:
                    entry["note"] = f"expected fallback ({k.raw.get('expected_llvm_reason', '')})"
            elif b.status == "unbuilt":
                if opt.strict_unbuilt or bench:
                    self.fail(f"{key}/{a.name}: UNBUILT: {b.reason}")
                else:
                    self.warnings.append(f"{key}/{a.name}: UNBUILT (allowed by --allow-unbuilt): {b.reason}")
            elif b.status == "gated":
                self.warnings.append(f"{key}/{a.name}: gated: {b.reason}")
            if a.lane == "llvm" and not a.twin and not k.llvm_expected and b.status not in ("fallback", "unbuilt", "gated", "failed"):
                self.warnings.append(f"{key}/{a.name}: kernel.json expects an LLVM fallback, but the lane accepted the program")
        self.checkpoint()
        live = [a for a in arms if builds[a.name].status == "ok"]
        # 2. without a pin: no measurement in bench; in smoke/check a labelled cross-check
        if ref is None:
            if bench and not opt.allow_unpinned:
                blk["checksum_reference"] = None
                self.fail(f"{key}/{size}: no pin; block not measured")
                live = []
            else:
                blk["checksum_reference"] = "UNPINNED: contract only (no pin, no blade-cpp)"
                cpp = builds.get("blade-cpp")
                if cpp is not None and cpp.status == "ok":
                    s = run_exe(cpp, regime, self.run_timeout(), "contract", self.affinity_for(regime))
                    if s.ok and s.checksum is not None and s.input_checksum is not None:
                        ref = {"checksum": s.checksum, "input_checksum": s.input_checksum,
                               "_source": "blade-cpp (unpinned)"}
                        blk["checksum_reference"] = "UNPINNED: cross-checked against blade-cpp only"
                if ref is None:
                    self.warnings.append(f"{key}: no pin and no passing blade-cpp; arms only contract-checked")
        # 3. warm-up / contract run of every live arm, validated
        for a in list(live):
            s = self._run_validated(k, regime, a, builds[a.name], ref, "warmup" if bench else "contract", None, state)
            if not s.ok:
                live.remove(a)
            self.checkpoint()
            sleep_logged(opt.cooldown, "between runs")
        # 4. rounds: a seeded random permutation of arms, twins and controls each round (M15)
        ratio_ref = self._ratio_reference(regime, live)
        blk["ratio_reference"] = ratio_ref
        nrounds, rule, taint = self._round_count(k, regime, size, ratio_ref, state)
        blk["rounds"] = {"n": nrounds, "rule": rule}
        if taint and bench:
            self.flags.append(f"tainted: {key}: {taint}")
        if bench and live:
            rng = random.Random(stable_seed(opt.seed, k.name, regime.name, "order"))
            blk["order_seed"] = stable_seed(opt.seed, k.name, regime.name, "order")
            log(f"{key}: {nrounds} rounds x {len(live)} arms + {len(self._ctl)} control(s) ({rule}); ratio reference {ratio_ref}")
            for rnd in range(nrounds):
                units: list[Any] = list(live) + list(self._ctl)
                rng.shuffle(units)
                parts = []
                for pos, u in enumerate(units):
                    if isinstance(u, dict):
                        cs = self.run_control(u, "control", rnd, key)
                        u["rec"]["samples"][-1]["position"] = pos
                        name = u["rec"]["spec"]["name"]
                        if not cs.ok:
                            blk["flags"].append(f"control {name} invalid in round {rnd}: {cs.reason}")
                        parts.append(f"[{name}]={cs.time_s * 1e3:.4g}ms" if cs.ok and cs.time_s else f"[{name}]=FAIL")
                    else:
                        if u not in live:
                            continue
                        s = self._run_validated(k, regime, u, builds[u.name], ref, "round", rnd, state)
                        state[u.name]["samples"][-1]["position"] = pos
                        if not s.ok:
                            live.remove(u)
                        parts.append(f"{u.name}={s.time_s * 1e3:.4g}ms" if s.ok and s.time_s else f"{u.name}=FAIL")
                    self.checkpoint()
                    sleep_logged(opt.cooldown, "between runs")
                log(f"  round {rnd + 1}/{nrounds}: " + " ".join(parts))
        # 5. statistics
        self._statistics(k, regime, blk, state, ratio_ref, bench)
        blk["finished"] = dt.datetime.now().isoformat(timespec="seconds")
        self.checkpoint()
        return blk

    def _serial_gate_ok(self, k: Kernel, regime: Regime, a: ArmSpec) -> bool:
        """M12: each mt8 arm is gated on ITS OWN validated serial arm.  When the serial
        counterpart was not part of this session it is validated at smoke size first."""
        key = (k.name, regime.serial_twin().name, a.name)
        if key not in self.serial_valid:
            if regime.serial_twin().name not in k.regimes:
                self.warnings.append(f"{k.name}/{regime.name}: no serial counterpart declared; {a.name} ungated")
                return True
            log(f"{k.name} {regime.name}: serial counterpart not run in this session; validating it at smoke size")
            self.run_block(k, regime.serial_twin(), "smoke", gate_only=True)
        # a serial arm that is not measured BY DESIGN (expected LLVM fallback, no BLAS route)
        # does not block its mt8 build; one that failed, was disqualified or is missing does.
        return self.serial_valid.get(key, "unrun") in GATE_PASS

    def _run_validated(self, k: Kernel, regime: Regime, a: ArmSpec, b: Build, ref: dict[str, Any] | None,
                       kind: str, rnd: int | None, state: dict[str, dict[str, Any]]) -> Sample:
        """Run one arm once, validate BOTH checksums (M12), apply the 2.5 s DQ and the
        thermal rule (M19).  Any failure invalidates the arm for the whole block."""
        s = run_exe(b, regime, self.run_timeout(), kind, self.affinity_for(regime))
        s.round = rnd
        if ref is not None:
            validate(s, ref, tol=self.tol(k, regime), input_tol=k.input_tolerance, twin=a.twin)
        if s.ok and s.time_s is not None and s.time_s > DQ_RUN_S:
            s.ok, s.reason = False, f"DQ: run longer than {DQ_RUN_S} s ({s.time_s:.2f} s)"
        entry = state[a.name]
        prev = [x["time_s"] for x in entry["samples"]
                if x["ok"] and x["time_s"] is not None and not x.get("excursion_dropped")]
        ratio = self._excursion(s, prev)
        if ratio is not None:
            self._arm_excursion(k, regime, a, s, ratio, kind)
        entry["samples"].append(dataclasses.asdict(s))
        if not s.ok:
            entry["status"] = "dq" if s.reason.startswith("DQ") else "invalid"
            entry["reason"] = f"{kind}{'' if rnd is None else ' round ' + str(rnd)}: {s.reason}"
            if a.lane == "llvm" and not k.llvm_expected:
                entry["note"] = "expected-unmeasurable arm failed"
                log(f"  {a.name}: {entry['reason']} (expected-unmeasurable arm)")
            else:
                self.fail(f"{k.name}/{regime.name}/{a.name}: {entry['reason']}")
        elif kind in ("warmup", "contract"):
            log(f"  {a.name}: ok t={s.time_s:.4g}s checksum={s.checksum_text} input={s.input_checksum_text}"
                + (f" peakWS={s.peak_working_set / 2**20:.1f}MB" if s.peak_working_set else ""))
        if regime.threads == "serial":
            self.serial_valid[(k.name, regime.name, a.name)] = entry["status"]
        return s

    @staticmethod
    def _excursion(s: Sample, prev: list[float]) -> float | None:
        """The run's multiple of its running median when that exceeds THERMAL_FACTOR (once the
        median is above the timer-noise floor), else None."""
        if not s.ok or s.time_s is None or not prev:
            return None
        med = statistics.median(prev)
        if med >= THERMAL_MIN_MEDIAN_S and s.time_s > THERMAL_FACTOR * med:
            return s.time_s / med
        return None

    def _arm_excursion(self, k: Kernel, regime: Regime, a: ArmSpec, s: Sample, ratio: float, kind: str) -> None:
        """Charter 2.5 as amended 2026-10-18 (user-approved): an ISOLATED arm excursion -- one
        run > 3x the arm's running median in this block -- is dropped from inference and
        recorded; a SECOND excursion in the same block aborts the session.  Control
        excursions always abort (see `_thermal`); a control excursion in this round would
        already have aborted, so reaching here means the round's controls were in bounds."""
        blk = self.blocks[-1]
        what = f"{k.name}/{regime.name}/{a.name} " + (f"round {s.round}" if kind == "round" else kind)
        prior = blk.setdefault("excursions", [])
        if prior:
            raise SessionAbort(f"second excursion in block {k.name}/{regime.name}: {what} took {s.time_s:.3f}s, "
                               f"{ratio:.1f}x its running median (first: {prior[0]['what']}) -- stopping (charter 2.5)")
        s.excursion_dropped, s.excursion_ratio = True, round(ratio, 2)
        prior.append({"what": what, "time_s": s.time_s, "ratio": round(ratio, 2)})
        self.warnings.append(f"excursion dropped (isolated; recorded): {what}: {s.time_s:.3f}s = {ratio:.1f}x running median")
        log(f"  EXCURSION (dropped, recorded): {what}: {s.time_s:.3f}s = {ratio:.1f}x running median")

    def _thermal(self, what: str, s: Sample, prev: list[float]) -> None:
        """Controls: any control run > 3x its own running median aborts the session (M19)."""
        ratio = self._excursion(s, prev)
        if ratio is not None:
            raise SessionAbort(f"{what}: run took {s.time_s:.3f}s, {ratio:.1f}x its running median -- "
                               f"stopping (charter 2.5)")

    def _ratio_reference(self, regime: Regime, live: list[ArmSpec]) -> str | None:
        names = {a.name for a in live}
        if regime.threads == "serial" and "blade-llvm" in names:
            return "blade-llvm"
        return "blade-cpp" if "blade-cpp" in names else None

    def _round_count(self, k: Kernel, regime: Regime, size: str, ratio_ref: str | None,
                     state: dict[str, dict[str, Any]]) -> tuple[int, str, str | None]:
        if self.opt.mode != "bench":
            return 1, "smoke/check: one validated run per arm", None
        warm = [x["time_s"] for name, e in state.items() if not ArmSpec.parse(name).twin
                for x in e["samples"][:1] if x["ok"]]
        memory_bound = size == "full" and bool(warm) and statistics.median(warm) < MEMORY_BOUND_S
        minimum = MIN_ROUNDS["memory_bound"] if memory_bound else MIN_ROUNDS[regime.threads]
        minimum = max(minimum, k.rounds.get(regime.threads, 0))
        rule = f"minimum {minimum} ({'memory-bound: median warm-up < 50 ms' if memory_bound else regime.threads}"
        rule += (f", kernel.json rounds.{regime.threads}" if regime.threads in k.rounds else "") + ")"
        if self.opt.rounds is not None:
            taint = f"--rounds {self.opt.rounds} below the minimum {minimum}" if self.opt.rounds < minimum else None
            return self.opt.rounds, f"--rounds {self.opt.rounds}; {rule}", taint
        return minimum, rule, None

    def _statistics(self, k: Kernel, regime: Regime, blk: dict[str, Any], state: dict[str, dict[str, Any]],
                    ratio_ref: str | None, bench: bool) -> None:
        key = f"{k.name}/{regime.name}"
        series: dict[str, dict[int, float]] = {}
        for name, e in state.items():
            good = [x for x in e["samples"] if x["ok"] and x["time_s"] is not None and not x.get("excursion_dropped")]
            if any(x.get("excursion_dropped") for x in e["samples"]):
                e["excursions_dropped"] = sum(1 for x in e["samples"] if x.get("excursion_dropped"))
            timed = [g for g in good if g["kind"] == "round"] if bench else good
            e["stats"] = describe([x["time_s"] for x in timed])
            ws = [x["peak_working_set"] for x in e["samples"] if x.get("peak_working_set")]
            if ws:
                e["peak_working_set_max"] = max(ws)
            if bench and e["status"] == "ok":
                series[name] = {x["round"]: x["time_s"] for x in timed}
        if not bench:
            return
        # M18: within-block trends (arms and controls) and the rolling control check
        for name, ser in series.items():
            t = trend(ser)
            state[name]["trend"] = t
            if t is not None and t > TREND_LIMIT:
                blk["flags"].append(f"trend: {name} first-half vs second-half median moved {t * 100:.1f}%")
        for live in self._ctl:
            rec = live["rec"]
            per = rec["per_block"].get(key, {})
            vals = {int(r): v for r, v in per.items() if v is not None}
            if not vals:
                continue
            med = statistics.median(vals.values())
            blk.setdefault("controls", {})[rec["spec"]["name"]] = describe(list(vals.values()))
            if rec["baseline"] is None:
                rec["baseline"] = med
            elif abs(med / rec["baseline"] - 1.0) > CONTROL_LIMIT:
                blk["flags"].append(f"control {rec['spec']['name']} median {med:.4g}s moved "
                                    f"{(med / rec['baseline'] - 1) * 100:+.1f}% vs the session baseline {rec['baseline']:.4g}s")
            t = trend(vals)
            if t is not None and t > TREND_LIMIT:
                blk["flags"].append(f"trend: control {rec['spec']['name']} moved {t * 100:.1f}% within the block")
        if not self._ctl:
            blk["flags"].append("no valid control in this session")
        blk["suppressed"] = bool(blk["flags"])
        if blk["flags"]:
            self.flags.append(f"{key}: inference suppressed: " + "; ".join(blk["flags"]))
        if ratio_ref is None or ratio_ref not in series:
            blk["ratio_note"] = "no valid Blade reference arm in this block: no ratios"
            return
        boot = RoundBootstrap(series, self.opt.resamples, stable_seed(self.opt.seed, k.name, regime.name, "boot"))
        blk["bootstrap"] = {"rounds": boot.rounds, "resamples": boot.resamples, "seed": boot.seed}
        ref_twin = twin_of(ratio_ref)
        for name, e in state.items():
            a = ArmSpec.parse(name)
            if a.twin or name not in series:
                continue
            if name != ratio_ref:
                e["ratio"] = boot.ratio(name, ratio_ref, self.delta)
                if ratio_ref == "blade-llvm" and "blade-cpp" in series and name != "blade-cpp":
                    e["ratio_vs_blade_cpp"] = boot.ratio(name, "blade-cpp", self.delta)
            tw = twin_of(name)
            if tw in series:
                e["incremental"] = boot.incremental(name, tw)
                if name != ratio_ref:
                    e["incremental_ratio"] = (boot.incremental_ratio(name, tw, ratio_ref, ref_twin, self.delta)
                                              if ref_twin in series else {"ratio": None, "note": "reference twin not valid"})
            elif k.inputonly:
                e["incremental"] = {"note": f"undefined: twin {tw} not valid in this block"}
            if blk["suppressed"]:
                for fld in ("ratio", "ratio_vs_blade_cpp", "incremental_ratio"):
                    if isinstance(e.get(fld), dict) and e[fld].get("verdict"):
                        e[fld]["verdict_unsuppressed"] = e[fld]["verdict"]
                        e[fld]["verdict"] = "suppressed"

    # -- the whole session -----------------------------------------------------------

    def plan_manifest(self) -> None:
        """M27: the planned cells, each later resolved to its final status."""
        for k in self.opt.kernels:
            if self.opt.regimes is not None:
                regs = self.opt.regimes
            elif self.opt.mode == "bench":
                regs = [Regime.parse(x) for x in k.regimes]
            else:
                regs = [Regime.parse(DEFAULT_SMOKE_REGIME)]
            for r in regs:
                if r.name not in k.regimes:
                    self.manifest.append({"kernel": k.name, "regime": r.name, "arm": "*",
                                          "status": "unsupported (regime not declared by the kernel)"})
                    continue
                for a in self.arms_for(k, r):
                    self.manifest.append({"kernel": k.name, "regime": r.name, "arm": a.name, "status": "unrun"})

    def resolve_manifest(self) -> None:
        idx = {(b["kernel"], b["regime"]): b for b in self.blocks if not b["gate_only"]}
        for cell in self.manifest:
            if cell["arm"] == "*":
                continue
            b = idx.get((cell["kernel"], cell["regime"]))
            if b is None or cell["arm"] not in b["arms"]:
                continue
            e = b["arms"][cell["arm"]]
            if e["status"] == "ok":
                cell["status"] = "measured" if self.opt.mode == "bench" else "validated"
                if b.get("suppressed"):
                    cell["status"] += " (inference suppressed)"
            else:
                cell["status"] = e["status"]
            cell["reason"] = e.get("reason", "")

    def run(self) -> int:
        aborted: str | None = None
        try:
            self.preflight()
            self.plan_manifest()
            if self.opt.mode == "bench":
                self.presession_checks()
            self.checkpoint()
            self.setup_controls()
            for ki, k in enumerate(self.opt.kernels):
                if ki > 0:
                    sleep_logged(self.opt.kernel_cooldown, "between kernels")
                for regime in self.regimes_for(k):
                    self.run_block(k, regime, self.opt.size)
        except SessionAbort as ex:
            aborted = str(ex)
            self.fail(f"ABORTED: {aborted}")
        except HarnessError as ex:
            aborted = f"harness error: {ex}"
            self.fail(aborted)
        except KeyboardInterrupt:
            aborted = "interrupted (Ctrl-C)"
            self.fail(aborted)
        except Exception as ex:  # a harness bug: keep the partial record, then fail
            aborted = f"harness exception: {ex!r}"
            self.data["traceback"] = traceback.format_exc()
            self.fail(aborted)
            log(self.data["traceback"])
        self.resolve_manifest()
        self.finish(aborted)
        if self.opt.mode in ("smoke", "check"):
            self.record_smoke_evidence()
        if self.failures:
            return 1
        return 3 if (self.flags and self.opt.mode == "bench") else 0

    # -- outputs ---------------------------------------------------------------------

    def finish(self, aborted: str | None) -> None:
        end = dt.datetime.now()
        self.data.update({"size": self.opt.size, "started": self.started.isoformat(timespec="seconds"),
                          "finished": end.isoformat(timespec="seconds"),
                          "duration_s": round((end - self.started).total_seconds(), 1), "aborted": aborted})
        self.checkpoint()
        md = render_summary(self.data)
        (self.bundle / "summary.md").write_text(md, encoding="utf-8")
        write_json_atomic(self.bundle / "manifest.json", {"argv": self.opt.argv, "cells": self.manifest})
        if self.opt.mode == "bench":
            self.archive_sources()
            (self.bundle.parent / "summary.md").write_text(md, encoding="utf-8")
        print()
        print(md)
        log(f"wrote {self.bundle}")

    def archive_sources(self) -> None:
        """M27: freeze every input of the session inside the bundle."""
        dst = self.bundle / "sources"
        roots = {"kernels": self.opt.kernels[0].dir.parent, "validation": self.opt.validation_dir}
        for rel in ("kernels", "fortran", "harness", "validation", "README.md", "notes/charter.md", ".gitignore"):
            src = roots.get(rel, BENCH_DIR / rel)
            if src.is_dir():
                shutil.copytree(src, dst / rel, dirs_exist_ok=True,
                                ignore=shutil.ignore_patterns("__pycache__", "*.exe", "*.obj", "*.mod"))
            elif src.is_file():
                (dst / rel).parent.mkdir(parents=True, exist_ok=True)
                shutil.copy2(src, dst / rel)
        hashes = {str(p.relative_to(dst)): sha256_file(p) for p in sorted(dst.rglob("*")) if p.is_file()}
        write_json_atomic(self.bundle / "sources-sha256.json", hashes)


# ---------------------------------------------------------------------------
# Markdown summary
# ---------------------------------------------------------------------------

def _s(x: float | None) -> str:
    return "--" if x is None else f"{x:.4g}"


def _ci(r: dict[str, Any] | None) -> str:
    if not r:
        return ""
    if r.get("ratio") is None:
        return r.get("note", "")
    return f"{r['ratio']:.3f} [{r['ci95'][0]:.3f}, {r['ci95'][1]:.3f}]"


def render_summary(data: dict[str, Any]) -> str:
    """One table per regime, rows = kernel x arm, the reference named per kernel."""
    m, p, tc = data.get("machine", {}), data.get("power", {}), data.get("toolchain", {})
    bench = data["mode"] == "bench"
    lines = [f"# Blade vs Fortran -- {data['mode']} session {data.get('started')}", "",
             f"- host {m.get('host')} | {m.get('cpu')} | {m.get('physical_cores')}C/{m.get('logical_processors')}T | "
             f"{m.get('ram_gb')} GB | power {p.get('ac_line')} | max processor state AC "
             f"{(p.get('PROCTHROTTLEMAX') or {}).get('ac')}% | boost AC {(p.get('PERFBOOSTMODE') or {}).get('ac')} | "
             f"{p.get('active_scheme', '')}",
             f"- git {m.get('git_sha')} | Blade.dll {str(tc.get('blade', {}).get('dll_sha256', '?'))[:16]} | "
             f"{tc.get('gfortran', {}).get('version', '')} | {tc.get('ifx', {}).get('version', '')} | "
             f"{tc.get('clang', {}).get('version', '')}",
             f"- size `{data.get('size')}` | duration {data.get('duration_s')} s | pins: "
             f"{', '.join(Path(f).name for f in (data.get('pins') or {}).get('files', {})) or 'none'}"]
    if bench:
        pr = data.get("protocol", {})
        lines.append(f"- PRIMARY estimand: the complete timed region. ratio = median(arm) / median(Blade reference), "
                     f"round-block bootstrap 95% CI ({pr.get('resamples')} resamples); ratio > 1 = Blade faster. "
                     f"Verdicts (delta {pr.get('verdict_delta')}): win / detectable-win / equivalent / detectable-loss / "
                     "loss / inconclusive; `suppressed` = the block failed a drift, trend or control check. "
                     "Intervals are pointwise; no suite-wide claim. incremental = median(arm) - median(its own "
                     "input-only twin), NOT kernel time. Reference: blade-llvm (serial), blade-cpp (mt8, or when "
                     "the LLVM lane refused). Fresh-process, allocation-inclusive measurements.")
    if data.get("aborted"):
        lines.append(f"- **ABORTED**: {data['aborted']}")
    for f in data.get("flags", []):
        lines.append(f"- **FLAG**: {f}")
    blocks = [b for b in data.get("blocks", []) if not b.get("gate_only")]
    regimes = sorted({b["regime"] for b in blocks}, key=lambda r: REGIME_ORDER.index(r) if r in REGIME_ORDER else 99)
    for rg in regimes:
        rows = [b for b in blocks if b["regime"] == rg]
        has_inc = any("incremental" in e for b in rows for e in b["arms"].values())
        lines += ["", f"## {rg}", "",
                  "Blade reference per kernel: " + ", ".join(f"{b['kernel']}={b.get('ratio_reference')}" for b in rows), ""]
        head = ["kernel", "arm", "status", "n", "min (s)", "median (s)", "IQR (s)", "ratio vs ref [95% CI]",
                "verdict", "hypothesis", "fold form", "parallelism", "peak WS (MB)"]
        if has_inc:
            head += ["incremental (s) [CI]", "incremental ratio [CI]"]
        head.append("notes")
        lines += ["| " + " | ".join(head) + " |", "|" + "---|" * len(head)]
        for b in rows:
            for arm in [a for a in ALL_ARMS if a in b["arms"]]:
                e = b["arms"][arm]
                st = e.get("stats", {})
                is_ref = arm == b.get("ratio_reference")
                tier = "natural" if "-natural" in arm else "expert" if "-expert" in arm else None
                hyp = (b.get("hypothesis") or {}).get(tier, "") if tier and not arm.endswith("-inputonly") else ""
                notes = [x for x in (e.get("reason"), e.get("note")) if x]
                ws = e.get("peak_working_set_max")
                row = [b["kernel"], arm, e["status"], str(st.get("n", 0)), _s(st.get("min")), _s(st.get("median")),
                       _s(st.get("iqr")), "ref" if (is_ref and bench) else _ci(e.get("ratio")),
                       (e.get("ratio") or {}).get("verdict", ""), hyp,
                       ((e.get("build") or {}).get("evidence") or {}).get("fold_form") or "",
                       ((e.get("build") or {}).get("evidence") or {}).get("parallelism") or "",
                       f"{ws / 2**20:.1f}" if ws else ""]
                if has_inc:
                    inc = e.get("incremental") or {}
                    row += [f"{inc['incremental']:.4g} [{inc['ci95'][0]:.4g}, {inc['ci95'][1]:.4g}]"
                            if "incremental" in inc else inc.get("note", ""),
                            "ref" if (is_ref and "incremental" in inc) else _ci(e.get("incremental_ratio"))]
                row.append("; ".join(notes))
                lines.append("| " + " | ".join(str(c).replace("|", "/") for c in row) + " |")
            if b.get("flags"):
                lines.append(f"\n> {b['kernel']} {rg}: inference suppressed -- " + "; ".join(b["flags"]) + "\n")
            for x in b.get("excursions", []):
                lines.append(f"\n> {b['kernel']} {rg}: isolated excursion DROPPED from inference (sample kept, "
                             f"flagged excursion-dropped): {x['what']} {x['time_s']:.4g} s = {x['ratio']}x running median\n")
    if data.get("controls"):
        lines += ["", "## Controls", ""]
        for c in data["controls"]:
            vals = [x["time_s"] for x in c.get("samples", []) if x["ok"] and x["kind"] == "control"]
            sp = c["spec"]
            lines.append(f"- {sp['name']} ({sp['kernel']}/{sp['arm']}/{sp['regime']}/{sp['size']}): n={len(vals)} "
                         f"median {_s(statistics.median(vals)) if vals else '--'} s, baseline {_s(c.get('baseline'))} s"
                         + ("" if c.get("valid") else " -- INVALID or not built"))
    if data.get("failures"):
        lines += ["", "## Failures", ""] + [f"- {f}" for f in data["failures"]]
    if data.get("warnings"):
        lines += ["", "## Warnings", ""] + [f"- {w}" for w in data["warnings"]]
    notes = [(b["kernel"], b["regime"], a, w) for b in data.get("blocks", [])
             for a, e in b["arms"].items() for w in (e.get("build") or {}).get("warnings", [])]
    if notes:
        lines += ["", "## Arm notes", ""] + [f"- {k}/{r}/{a}: {w}" for k, r, a, w in notes]
    return "\n".join(lines) + "\n"


# ---------------------------------------------------------------------------
# CLI
# ---------------------------------------------------------------------------

def _csv(s: str | None) -> list[str] | None:
    return [x.strip() for x in s.split(",") if x.strip()] if s else None


def cmd_list(kernels: list[Kernel]) -> int:
    """Print the discovered kernels."""
    if not kernels:
        print("no kernels discovered")
        return 0
    for k in kernels:
        print(f"{k.name}  (group {k.group})")
        for sname, sz in k.sizes.items():
            print(f"    size {sname:8s} {sz}")
        print(f"    regimes  {', '.join(k.regimes)}")
        for a in k.arms:
            arm = ArmSpec.parse(a)
            src = k.source_for(arm)
            print(f"    arm {a:28s} {src.name:20s} {'present' if src.is_file() else 'MISSING'}")
            if arm.tier in k.blas_sources and not arm.twin:
                bs = k.dir / k.blas_sources[arm.tier]
                print(f"    arm {a + ' (blas)':28s} {bs.name:20s} {'present' if bs.is_file() else 'MISSING'}")
        if not k.llvm_expected:
            print(f"    expected_llvm = fallback ({k.raw.get('expected_llvm_reason', '')})")
        if k.no_parallel_form:
            print(f"    no_parallel_form (Blade, mt8): {k.no_parallel_form}")
    return 0


def print_check(sess: Session) -> None:
    """`check`: one line per arm with its status and the two checksums."""
    for b in sess.blocks:
        print(f"{b['kernel']} {b['regime']} size={b['size']} {b['sizes']} reference={b.get('checksum_reference')}"
              + (" (serial gate)" if b.get("gate_only") else ""))
        for arm, e in b["arms"].items():
            c = e["samples"][0] if e["samples"] else {}
            rs = e.get("reason") or ""
            print(f"  {arm:28s} {e['status']:16s} checksum={c.get('checksum_text')}  input_checksum={c.get('input_checksum_text')}"
                  f"  t={c.get('time_s')}" + (f"  [{rs}]" if rs else ""))


def main(argv: list[str] | None = None) -> int:
    raw_argv = list(sys.argv if argv is None else ["run.py", *argv])
    ap = argparse.ArgumentParser(prog="run.py", description=__doc__.split("\n\n")[0])
    sub = ap.add_subparsers(dest="verb", required=True)

    def common(p: argparse.ArgumentParser) -> None:
        p.add_argument("--kernels-dir", type=Path, default=KERNELS_DIR, help="where <name>/kernel.json live")
        p.add_argument("--regimes", help="comma list, e.g. serial-licensed-native,mt8-licensed-native, or 'all'")
        p.add_argument("--arms", help="comma list of arms (a base arm also selects its twin)")
        p.add_argument("--build-dir", type=Path, default=BUILD_ROOT, help="private build root (gitignored)")
        p.add_argument("--out-dir", type=Path, help="where the session bundle goes")
        p.add_argument("--config", type=Path, default=CONFIG_FILE)
        p.add_argument("--validation-dir", type=Path, default=VALIDATION_DIR, help="where pins*.json live")
        p.add_argument("--run-timeout", type=float, default=RUN_TIMEOUT_CAP_S)
        p.add_argument("--build-timeout", type=float, default=600.0)
        p.add_argument("--allow-unbuilt", action="store_true",
                       help="smoke/check: report unbuilt arms (missing source) as warnings instead of failures")

    sub.add_parser("list").add_argument("--kernels-dir", type=Path, default=KERNELS_DIR)
    pc = sub.add_parser("check", help="build + run once (smoke size by default), validate, print checksums")
    pc.add_argument("kernel")
    pc.add_argument("--size", default="smoke")
    common(pc)
    ps = sub.add_parser("smoke", help="every kernel at smoke size, one validated run per arm")
    ps.add_argument("--kernels")
    common(ps)
    pb = sub.add_parser("bench", help="the measured protocol (RUNNER ONLY)")
    pb.add_argument("--kernels")
    pb.add_argument("--size", default="full")
    pb.add_argument("--rounds", type=int, help="override the round count (below the minimum taints the session)")
    pb.add_argument("--cooldown", type=float, default=MIN_RUN_COOLDOWN_S)
    pb.add_argument("--kernel-cooldown", type=float, default=MIN_KERNEL_COOLDOWN_S)
    pb.add_argument("--resamples", type=int, default=10000)
    pb.add_argument("--seed", type=int, default=20261017)
    pb.add_argument("--allow-battery", action="store_true", help="run on battery; taints the session")
    pb.add_argument("--allow-unpinned", action="store_true", help="measure kernels without pins; taints the session")
    pb.add_argument("--skip-smoke-check", action="store_true", help="skip the smoke-evidence check; taints the session")
    common(pb)
    a = ap.parse_args(argv)

    try:
        kernels = discover(a.kernels_dir)
        if a.verb == "list":
            return cmd_list(kernels)
        config = load_config(a.config)
        if a.verb == "check":
            kernels = [k for k in kernels if k.name == a.kernel]
            if not kernels:
                raise HarnessError(f"no kernel {a.kernel!r} under {a.kernels_dir}")
        elif a.kernels:
            want = _csv(a.kernels) or []
            missing = [w for w in want if w not in {k.name for k in kernels}]
            if missing:
                raise HarnessError(f"unknown kernel(s) {missing}")
            kernels = [k for k in kernels if k.name in want]
        if not kernels:
            raise HarnessError(f"no kernels discovered under {a.kernels_dir}")
        size = getattr(a, "size", "smoke") if a.verb != "smoke" else "smoke"
        for k in kernels:
            if size not in k.sizes:
                raise HarnessError(f"kernel {k.name} has no size {size!r} (has {sorted(k.sizes)})")
        regimes = None
        if a.regimes:
            names = REGIME_ORDER if a.regimes == "all" else _csv(a.regimes) or []
            regimes = [Regime.parse(r) for r in names]
        arms = _csv(a.arms)
        for x in arms or []:
            ArmSpec.parse(x)
        bench = a.verb == "bench"
        if bench and (a.cooldown < MIN_RUN_COOLDOWN_S or a.kernel_cooldown < MIN_KERNEL_COOLDOWN_S):
            raise HarnessError(f"cooldowns may not go below {MIN_RUN_COOLDOWN_S}s / {MIN_KERNEL_COOLDOWN_S}s outside smoke")
        if bench and a.resamples < 2000:
            raise HarnessError("--resamples must be >= 2000")
        opt = Options(mode=a.verb, kernels=kernels, regimes=regimes, arms=arms, size=size,
                      rounds=getattr(a, "rounds", None),
                      cooldown=a.cooldown if bench else 0.0, kernel_cooldown=a.kernel_cooldown if bench else 0.0,
                      run_timeout=a.run_timeout, build_timeout=a.build_timeout,
                      resamples=getattr(a, "resamples", 10000), seed=getattr(a, "seed", 20261017),
                      allow_battery=getattr(a, "allow_battery", False),
                      allow_unpinned=getattr(a, "allow_unpinned", False),
                      skip_smoke_check=getattr(a, "skip_smoke_check", False), strict_unbuilt=not getattr(a, "allow_unbuilt", False),
                      build_root=a.build_dir.resolve(), out_dir=a.out_dir, config=config, argv=raw_argv,
                      validation_dir=a.validation_dir)
        opt.build_root.mkdir(parents=True, exist_ok=True)
        log(f"{a.verb}: {len(kernels)} kernel(s): {', '.join(k.name for k in kernels)}; size {size}")
        sess = Session(opt)
        rc = sess.run()
        if a.verb == "check":
            print_check(sess)
        log(f"exit {rc}: {len(sess.failures)} failure(s), {len(sess.flags)} flag(s)")
        return rc
    except HarnessError as ex:
        log(f"ERROR: {ex}")
        return 2


if __name__ == "__main__":
    sys.exit(main())
