#!/usr/bin/env python3
import json, re, statistics, sys
from pathlib import Path
d = Path(sys.argv[1]); res = json.loads((d / "results.json").read_text())
def q(v, p):
    s = sorted(v); i = (len(s) - 1) * p; lo = int(i); hi = min(lo + 1, len(s) - 1)
    return s[lo] + (s[hi] - s[lo]) * (i - lo)
print("| kernel | serial build | mt1 | mt2 | mt3 | mt4 | mt4 vs serial | mt4 vs mt1 | mt1 overhead | max IQR/med | checksum rel spread |")
print("|---|---|---|---|---|---|---|---|---|---|---|")
for k, r in res.items():
    t = r["times"]; med = {a: statistics.median(v) * 1e3 for a, v in t.items()}
    iqr = max((q(v, .75) - q(v, .25)) / statistics.median(v) for v in t.values())
    cs = [float(c) for c in r["checksums"].values()]
    spread = (max(cs) - min(cs)) / max(abs(c) for c in cs) if any(cs) else 0.0
    f = lambda a: f"{med[a]:.2f}"
    print(f"| {k} | {f('serial')} | {f('mt1')} | {f('mt2')} | {f('mt3')} | {f('mt4')} | "
          f"{med['serial']/med['mt4']:.2f}x | {med['mt1']/med['mt4']:.2f}x | {med['mt1']/med['serial']:.2f} | "
          f"{iqr:.1%} | {spread:.1e} |")
print()
for k in res:
    plan = (d / k / "plan.txt").read_text().splitlines()
    lines = [l.strip() for l in plan if re.search(r"\[omp\]|requested but emitted serial|threaded|thread team|persistent team|row-owned pull", l)]
    print(f"### {k}")
    for l in lines[:8]: print("-", l[:260])
    print()
