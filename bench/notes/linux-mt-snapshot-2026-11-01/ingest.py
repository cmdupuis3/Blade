#!/usr/bin/env python3
"""Append this snapshot's cells to bench/results/stats.csv (label `cloud-xeon4`). Not a harness
session: no controls, no bootstrap, so ratio / ci / verdict stay empty and every block is
`indicative`. Idempotent on stats.py's KEY (bundle, kernel, regime, size, arm)."""
import csv, hashlib, json, statistics, sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
BENCH = HERE.parents[1]
sys.path.insert(0, str(BENCH / "harness"))
import stats as S  # noqa: E402

EXE_DIR = Path(sys.argv[1]) if len(sys.argv) > 1 else None  # the local build dir, for exe hashes
RUNS = {  # bundle id -> (session start, results file, arm-name map)
    "2026-11-01-vm/snapshot-run1": ("2026-11-01T00:18:21", HERE / "run1/results.json", "mtsnap"),
    "2026-11-01-vm/snapshot-run2": ("2026-11-01T00:29:22", HERE / "run2/results.json", "allsnap"),
}
ARM = {"blade": "blade-cpp", "gfortran": "gfortran-expert", "ifx": "ifx-expert"}
FLAGS = ("non-harness Linux driver (bench/notes/linux-mt-snapshot-2026-11-01); shared 4-vCPU cloud VM, "
         "no drift controls; serial = threaded-off build pinned to CPU 1, mt<N> = threaded build unbound")

def q(v, p):
    s = sorted(v); i = (len(s) - 1) * p; lo = int(i); hi = min(lo + 1, len(s) - 1)
    return s[lo] + (s[hi] - s[lo]) * (i - lo)

def exe_sha(run, kernel, tc, cls):
    if EXE_DIR is None: return ""
    # run2 reuses run1's Blade builds; the Fortran builds are run2's own
    p = EXE_DIR / "run1" / kernel / cls if tc == "blade" else EXE_DIR / "run2" / kernel / f"{tc}-expert" / cls
    return hashlib.sha256(p.read_bytes()).hexdigest()[:16] if p.is_file() else ""

def main():
    rows = S.load(); have = {tuple(str(r[k]) for k in S.KEY) for r in rows}
    new = []
    for bundle, (started, path, kind) in RUNS.items():
        run = bundle.rsplit("-", 1)[1]
        res = json.loads(path.read_text())
        for kernel, r in res.items():
            kj = json.loads((BENCH / "kernels" / kernel / "kernel.json").read_text())
            for arm_key, times in r["times"].items():
                tc, cls = arm_key.split("/") if "/" in arm_key else ("blade", arm_key)
                cls = "serial" if cls in ("off", "serial") else cls
                regime = f"{'serial' if cls == 'serial' else cls}-licensed-native"
                ck = r["checksums"].get(arm_key)
                ck = ck[0] if isinstance(ck, list) else ck
                row = {
                    "session": started, "label": "cloud-xeon4", "bundle": bundle, "host": "vm",
                    "git_sha": "5ed8af20", "blade_dll_sha": "d041d86bd92e94d3" if tc == "blade" else "",
                    "harness": "linux-snapshot-1", "mode": "snapshot", "size_mode": "full",
                    "kernel": kernel, "group": kj.get("group", ""), "regime": regime, "size": "full",
                    "sizes": json.dumps(kj["sizes"]["full"], separators=(",", ":")),
                    "arm": ARM[tc], "lane": "cpp" if tc == "blade" else "", "status": "ok", "reason": "",
                    "n": len(times), "min_s": min(times), "median_s": statistics.median(times),
                    "q1_s": q(times, .25), "q3_s": q(times, .75), "iqr_s": q(times, .75) - q(times, .25),
                    "mean_s": statistics.mean(times), "max_s": max(times),
                    "block_class": "indicative", "block_flags": FLAGS, "rounds": len(times),
                    "exe_sha": exe_sha(run, kernel, tc, "serial" if cls == "serial" else "mt"),
                    "checksum": ck or "",
                    "note": "Intel Xeon fam 6 model 207, 4 vCPU no SMT; g++ 13.3 / gfortran 13.3 / ifx 2026.2.0 (Linux)",
                }
                key = tuple(str(row[k]) for k in S.KEY)
                if key not in have: new.append(row); have.add(key)
    with S.CSV.open("a", encoding="utf-8", newline="") as f:
        w = csv.DictWriter(f, fieldnames=S.COLUMNS, lineterminator="\n")
        for row in new: w.writerow({c: row.get(c, "") for c in S.COLUMNS})
    print(f"appended {len(new)} row(s) to {S.CSV}")

main()
