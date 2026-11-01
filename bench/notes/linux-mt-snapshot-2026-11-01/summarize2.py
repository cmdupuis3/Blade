#!/usr/bin/env python3
import json, statistics, sys
from pathlib import Path
d = Path(sys.argv[1]); res = json.loads((d / "results.json").read_text())
TC = ["blade", "gfortran", "ifx"]; COLS = ["off", "mt1", "mt2", "mt3", "mt4"]
med = lambda v: statistics.median(v) * 1e3

print("## Medians (ms)\n")
print("| kernel | toolchain | off | mt1 | mt2 | mt3 | mt4 | mt4 vs off | mt4 vs mt1 |")
print("|---|---|---|---|---|---|---|---|---|")
for k, r in res.items():
    t = r["times"]
    for tc in TC:
        m = {c: med(t[f"{tc}/{c}"]) for c in COLS if t.get(f"{tc}/{c}")}
        cells = [f"{m[c]:.2f}" if c in m else "-" for c in COLS]
        s1 = f"{m['off']/m['mt4']:.2f}x" if "off" in m and "mt4" in m else "-"
        s2 = f"{m['mt1']/m['mt4']:.2f}x" if "mt1" in m and "mt4" in m else "-"
        print(f"| {k if tc == 'blade' else ''} | {tc} | " + " | ".join(cells) + f" | {s1} | {s2} |")

print("\n## Blade vs Fortran at equal threads (Fortran time / Blade time; >1 = Blade faster)\n")
print("| kernel | vs gfortran off | vs gfortran mt4 | vs ifx off | vs ifx mt4 | fastest at mt4 |")
print("|---|---|---|---|---|---|")
for k, r in res.items():
    t = r["times"]; g = lambda a: med(t[a]) if t.get(a) else None
    row = []
    for tc in ["gfortran", "ifx"]:
        for c in ["off", "mt4"]:
            b, f = g(f"blade/{c}"), g(f"{tc}/{c}")
            row.append(f"{f/b:.2f}" if b and f else "-")
    best = min(((g(f"{tc}/mt4"), tc) for tc in TC if g(f"{tc}/mt4")), default=(None, "-"))[1]
    print(f"| {k} | " + " | ".join(row) + f" | {best} |")

print("\n## Checksum agreement (max relative spread over every arm) and errors\n")
for k, r in res.items():
    cs = [float(c[0]) for c in r["checksums"].values()]; ics = [float(c[1]) for c in r["checksums"].values()]
    sp = lambda v: (max(v) - min(v)) / max(abs(x) for x in v) if any(v) else 0.0
    print(f"- {k}: checksum {sp(cs):.1e}, input_checksum {sp(ics):.1e}" + (f"; ERRORS {r['errors']}" if r["errors"] else ""))
