#!/usr/bin/env python3
"""Quick Linux multithread snapshot (not the harness): Blade C++ lane, licensed native, full size.
Arms per kernel: serial build (BLADE_OMP_THREADS=1, pinned to one CPU) and the threaded build at
OMP_NUM_THREADS = 1..maxN (unbound). Rounds interleaved, arm order rotated per round."""
import json, os, re, statistics, subprocess, sys, time
from pathlib import Path

REPO = Path("/home/user/Blade")
BLADE = REPO / "bin/Release/net10.0/Blade"
OUT = Path(sys.argv[1]).resolve()
KERNELS = sys.argv[2].split(",")
ROUNDS = int(sys.argv[3]) if len(sys.argv) > 3 else 9
THREADS = [1, 2, 3, 4]
RE_T = re.compile(r"completed in\s+(\S+)s")
RE_C = re.compile(r"^checksum\s*=\s*(\S+)", re.M)

base = {k: v for k, v in os.environ.items() if not k.startswith("BLADE_") and not k.startswith("OMP_")}
base.update({"BLADE_BLAS": "off", "BLADE_EXE_CACHE": "0", "OMP_DYNAMIC": "FALSE"})

def build(k, serial):
    d = OUT / k; d.mkdir(parents=True, exist_ok=True)
    exe = d / ("serial" if serial else "mt")
    env = dict(base)
    if serial: env["BLADE_OMP_THREADS"] = "1"
    src = REPO / "bench/kernels" / k / f"{k}.blade"
    t0 = time.time()
    r = subprocess.run([str(BLADE), "compile", str(src), "-o", str(exe), "--print", "checksum,input_checksum"],
                       env=env, capture_output=True, text=True, cwd=d)
    (d / f"build-{exe.name}.log").write_text(r.stdout + r.stderr)
    if r.returncode != 0: raise SystemExit(f"{k} build failed: {(r.stdout + r.stderr)[-800:]}")
    if not serial:
        p = subprocess.run([str(BLADE), "plan", str(src)], env=env, capture_output=True, text=True, cwd=d)
        (d / "plan.txt").write_text(p.stdout + p.stderr)
    return exe, time.time() - t0

def run(exe, n, pin):
    env = dict(base); env["OMP_NUM_THREADS"] = str(n)
    cmd = (["taskset", "-c", "1"] if pin else []) + [str(exe)]
    r = subprocess.run(cmd, env=env, capture_output=True, text=True, timeout=600)
    if r.returncode != 0: return None, None
    return float(RE_T.search(r.stdout).group(1)), RE_C.search(r.stdout).group(1)

res = {}
for k in KERNELS:
    se, bs = build(k, True); me, bm = build(k, False)
    arms = [("serial", se, 1, True)] + [(f"mt{n}", me, n, False) for n in THREADS]
    for a in arms: run(a[1], a[2], a[3])  # warm-up, discarded
    times = {a[0]: [] for a in arms}; cks = {}
    for rd in range(ROUNDS):
        rot = arms[rd % len(arms):] + arms[:rd % len(arms)]
        for name, exe, n, pin in rot:
            t, c = run(exe, n, pin); time.sleep(0.3)
            if t is not None: times[name].append(t); cks[name] = c
    res[k] = {"times": times, "checksums": cks, "build_s": [bs, bm]}
    med = {a: statistics.median(v) * 1e3 for a, v in times.items() if v}
    print(k, " ".join(f"{a}={m:.3f}ms" for a, m in med.items()), flush=True)
    (OUT / "results.json").write_text(json.dumps(res, indent=1))
