"""Compare a smoke_B.sh output log with bench/validation/pins-B.json (smoke entries).

usage: C:\\Python314\\python.exe -P bench/validation/tools/check_smoke_B.py <smoke log>
Prints one line per (kernel or twin, build): the relative error of checksum and input_checksum
against the pin, and any LLVM-lane refusal; exit status 1 if any error exceeds 1e-12.
"""
import json
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
pins = json.load(open(os.path.join(HERE, "..", "pins-B.json"), encoding="utf-8"))
bad = False
stem, build, vals = None, None, {}


def flush():
    global bad
    if stem is None or build is None or not vals:
        return
    k = stem.replace("_inputonly", "")
    p = pins[k]["smoke"]
    out = []
    for name in ("checksum", "input_checksum"):
        if name not in vals:
            out.append(f"{name} MISSING")
            bad = True
            continue
        ref = 0.0 if (name == "checksum" and stem.endswith("_inputonly")) else p[name]
        err = abs(vals[name] - ref) / max(1.0, abs(ref))
        bad |= err > 1e-12
        out.append(f"{name} rel err {err:.1e}")
    print(f"{stem:22s} {build:16s} " + " | ".join(out) + (" | " + vals["refused"] if "refused" in vals else ""))


for line in open(sys.argv[1], encoding="utf-8", errors="replace"):
    line = line.rstrip("\n")
    m = re.match(r"^######## \[\w+\] (\S+)", line)
    if m:
        flush(); stem, build, vals = m.group(1), None, {}
        continue
    m = re.match(r"^-- (.*)$", line)
    if m:
        flush(); build, vals = m.group(1), {}
        continue
    if "llvm lane refused" in line:
        vals["refused"] = "LLVM FALLBACK"
    m = re.match(r"^(checksum|input_checksum) = (\S+)$", line)
    if m:
        vals[m.group(1)] = float(m.group(2))
flush()
print("ALL WITHIN 1e-12" if not bad else "FAILURES")
sys.exit(1 if bad else 0)
