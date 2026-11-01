"""PM52 timed stage over the ACCEPTED B07 executables (round 4 wave 3, item E3).

B05 chose the boundary (an external driver brackets ONE opaque native task call
between QPC t0 and t1); B07 built it and root accepted the native enclosure of
eight exact executables (`bench/notes/round4/B07-native-audits/<arm>.json` at
git 42f9e26b: thirteen gates, each with disassembly evidence). Each run of those
binaries still reports `performance_eligible:false`, because a single
diagnostic process is not a measurement. This stage is the measurement:

* the executables measured are EXACTLY the audited ones -- each audit is read
  from git (`git show 42f9e26b:...`), every one of its thirteen native gates
  must pass, and the binary on disk must hash to the audit's executable sha256
  (copied byte-for-byte into the private output root first; never rebuilt);
* `--runs` fresh processes per arm, the arms interleaved round-robin in a
  per-round shuffled order (seeded, recorded), each with a private TEMP/TMP,
  OMP/OpenBLAS single-threaded and every BLADE_* variable removed;
* EVERY report must pass B07's own validator (`validate.numeric_errors`, the
  pinned independent R5 reference, tolerance 1e-12) and every post-stop
  check flag, or the arm is not eligible;
* the statistic is the median (with min / max) of `opaque_task_interval_s`,
  the one timed interval that encloses the task; observer and cleanup
  intervals are reported, never subtracted.

An arm is PERFORMANCE ELIGIBLE iff all of the above hold. Eligibility says the
interval encloses the declared work (compiler ordering, per the audit); it is
not a DRAM-completion claim, and medians from one box are indicative. The
task is the fixed 2053-cell shape, so the interval is microseconds: QPC
resolution and call overhead are a visible fraction of it, and the summary
says so. Run under the build lock on a quiet box.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import random
import shutil
import statistics
import subprocess
import sys
sys.dont_write_bytecode = True
import validate

ROOT = Path(__file__).resolve().parent
AUDIT_COMMIT = "42f9e26b"
AUDIT_DIR = "bench/notes/round4/B07-native-audits"
ARMS = [a + "-" + r for a in ("cpp", "llvm", "natural", "expert") for r in ("licensed", "repro")]


def sha256(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def audit_of(arm):
    raw = subprocess.run(["git", "-C", str(ROOT), "show", f"{AUDIT_COMMIT}:{AUDIT_DIR}/{arm}.json"],
                         capture_output=True, check=True).stdout
    return json.loads(raw), hashlib.sha256(raw).hexdigest()


def load_reference_lf():
    """validate.load_reference pins the reference's sha256 over its RAW bytes,
    which are LF in the index but CRLF in a core.autocrlf checkout (this
    repository's default), so the pin refuses there. Same pin, over the
    LF-normalized bytes (the index form); the module is then loaded as usual."""
    import importlib.util
    path = ROOT.parent / "store-consume" / "reference.py"
    if hashlib.sha256(path.read_bytes().replace(b"\r\n", b"\n")).hexdigest() != validate.REFERENCE_SHA:
        raise ValueError("independent reference identity changed")
    spec = importlib.util.spec_from_file_location("b07_independent_reference", path)
    ref = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(ref)
    return ref


def gate_failures(audit):
    gates = audit.get("gates", {})
    return [g for g in validate.NATIVE_GATES if not gates.get(g, {}).get("pass")]


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--out", required=True, help="new absolute output directory")
    ap.add_argument("--runs", type=int, default=5)
    ap.add_argument("--seed", type=int, default=20261101)
    ap.add_argument("--path-prefix", default=r"C:\msys64\ucrt64\bin",
                    help="directory prepended to PATH for the runtime DLLs (libgfortran, libstdc++)")
    args = ap.parse_args()
    out = Path(args.out)
    if not out.is_absolute():
        ap.error("--out must be absolute")
    if args.runs < 5:
        ap.error("--runs must be at least 5 (the campaign's interleaved-median rule)")
    out.mkdir(parents=True, exist_ok=False)

    arms = {}
    for arm in ARMS:
        audit, audit_sha = audit_of(arm)
        exe = Path(audit["evidence_files"]["executable"]["path"])
        want = audit["evidence_files"]["executable"]["sha256"]
        why = []
        if audit.get("contract") != validate.CONTRACT:
            why.append("audit contract differs")
        failed = gate_failures(audit)
        if failed:
            why.append("native gates not passed: " + ", ".join(failed))
        local = out / arm / "boundary.exe"
        local.parent.mkdir()
        if not exe.is_file():
            why.append(f"audited executable missing: {exe}")
        else:
            shutil.copyfile(exe, local)
            got = sha256(local)
            if got != want:
                why.append(f"executable sha256 {got} != audited {want}")
        arms[arm] = {"audit_sha256": audit_sha, "executable_sha256": want, "source": str(exe),
                     "regime": arm.split("-")[1], "blocked": why, "runs": []}

    env = os.environ.copy()
    for name in list(env):
        if name.upper().startswith("BLADE_") or name.upper() in ("OPENBLAS_DIR",):
            env.pop(name)
    env.update(OMP_NUM_THREADS="1", OMP_DYNAMIC="FALSE", OPENBLAS_NUM_THREADS="1")
    env["PATH"] = args.path_prefix + os.pathsep + env.get("PATH", "")
    validate.load_reference = load_reference_lf
    truth = validate.expected(False)
    rng = random.Random(args.seed)
    orders = []
    for r in range(args.runs):
        order = [a for a in ARMS if not arms[a]["blocked"]]
        rng.shuffle(order)
        orders.append(order)
        for arm in order:
            tmp = out / arm / f"tmp{r}"
            tmp.mkdir()
            report = out / arm / f"report{r}.json"
            e = dict(env, TEMP=str(tmp), TMP=str(tmp))
            proc = subprocess.run([str(out / arm / "boundary.exe"), str(report)], env=e,
                                  capture_output=True, timeout=60)
            entry = {"round": r, "exit": proc.returncode}
            try:
                rep = json.loads(report.read_text())
                errs = validate.numeric_errors(rep, truth, arms[arm]["regime"])
                errs += [f for f in validate.FLAGS if rep.get(f) is not True]
                if rep.get("failed") != 0 or rep.get("task_status") != 0:
                    errs.append("driver assertions failed")
                entry.update(task_s=rep["opaque_task_interval_s"], observer_s=rep["observer_s"],
                             cleanup_s=rep["cleanup_s"], errors=errs)
            except Exception as ex:  # no report = an invalid run, never a timing
                entry.update(errors=[f"report unreadable: {ex}"])
            if proc.returncode != 0:
                entry["errors"].append(f"exit {proc.returncode}")
            arms[arm]["runs"].append(entry)

    summary = {"contract": validate.CONTRACT, "audit_commit": AUDIT_COMMIT, "runs_per_arm": args.runs,
               "seed": args.seed, "orders": orders, "arms": {}}
    lines = ["arm               eligible  median_task_us  min_us   max_us   median_observer_us"]
    for arm in ARMS:
        a = arms[arm]
        bad = [x for x in a["runs"] if x["errors"]]
        eligible = not a["blocked"] and len(a["runs"]) == args.runs and not bad
        t = [x["task_s"] for x in a["runs"] if "task_s" in x]
        o = [x["observer_s"] for x in a["runs"] if "observer_s" in x]
        rec = {"performance_eligible": eligible, "blocked": a["blocked"],
               "invalid_runs": [{"round": x["round"], "errors": x["errors"]} for x in bad],
               "audit_sha256": a["audit_sha256"], "executable_sha256": a["executable_sha256"],
               "task_s": t, "observer_s": o}
        if t:
            rec.update(median_task_s=statistics.median(t), min_task_s=min(t), max_task_s=max(t))
            lines.append(f"{arm:17s} {str(eligible):9s} {statistics.median(t)*1e6:14.2f} {min(t)*1e6:8.2f} {max(t)*1e6:8.2f} "
                         f"{statistics.median(o)*1e6:12.1f}")
        else:
            lines.append(f"{arm:17s} {str(eligible):9s} (no timing: {'; '.join(a['blocked']) or 'no valid run'})")
        summary["arms"][arm] = rec
    summary["note"] = ("eligibility = the audited enclosure + identical executable + every run validated; "
                       "the 2053-cell task is microseconds long, so QPC resolution and call overhead are a "
                       "visible share of each interval; medians from one box are indicative")
    (out / "summary.json").write_text(json.dumps(summary, indent=1))
    print("\n".join(lines))
    print("summary:", out / "summary.json")
    return 0 if all(r["performance_eligible"] for r in summary["arms"].values()) else 1


if __name__ == "__main__":
    sys.exit(main())
