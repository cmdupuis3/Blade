"""W3-3 (D/P1) measurement matrix driver for p1_commit.exe -- QUIET BOX ONLY.

Usage (PowerShell, from anywhere):
    C:\\Python314\\python.exe -I run_matrix.py <p1_commit.exe> <out_dir> [rounds=21] [seed=20261021]
    C:\\Python314\\python.exe -I run_matrix.py --analyze <out_dir>

Every round runs EVERY configuration once, as its own process, in a freshly
shuffled order (interleaved A/B, the bench protocol), under
OMP_DYNAMIC=false. The build is already REPRO + CONTRACT=off (the prototype
is compiled with -ffp-contract=off and its folds are the strict serial forms);
BLADE_FP_REPRO=1 BLADE_FP_CONTRACT=off are exported too, for the record.

Team settings (the brief's T in {1, 3, 8} plus a restricted ACTUAL team):
    T1  OMP_NUM_THREADS=1, pinned to CPU 2 (the harness's serial pin)
    T3  OMP_NUM_THREADS=3, unbound
    T8  OMP_NUM_THREADS=8, unbound (the harness's mt8: libgomp cannot bind on Windows)
    R2  OMP_NUM_THREADS=8 OMP_THREAD_LIMIT=2 (requested 8, actual 2)
Configurations per team setting:
    a           producer alone (control)
    a2:G        producer through the commit's claim loop, no consumer (G = 1, 4, 8)
    b:G         producer + the serial ordered scatter (G = 4 is today's default; 1, 8 too)
    c           producer + the G35 row slots
    d:G         producer + the ticket commit (G = 1, 4, 8)
    di:G        d:G with the instruments (rdtsc per group)
The analysis reads raw.csv: per (team, config) the median end-to-end time and
a bootstrap 95% CI; PAIRED per-round differences (b:4 - d:G, b:4 - c,
d:G - a2:G, b:4 - a, ...) with the median and a bootstrap 95% CI of the median
difference; the instrument medians; every run's checksum bits, R hash and pool
hash checked against the b:4 reference (a mismatch is a BUG and is reported
first); the decision rule of the brief (W3-3) at T8.
"""
import csv
import json
import os
import random
import re
import statistics
import subprocess
import sys

TEAMS = {
    "T1": {"OMP_NUM_THREADS": "1", "P1_AFFINITY": "0x4"},
    "T3": {"OMP_NUM_THREADS": "3"},
    "T8": {"OMP_NUM_THREADS": "8"},
    "R2": {"OMP_NUM_THREADS": "8", "OMP_THREAD_LIMIT": "2"},
}
CONFIGS = ["a", "a2:1", "a2:4", "a2:8", "b:1", "b:4", "b:8", "c", "d:1", "d:4", "d:8", "di:1", "di:4", "di:8"]
BASE_ENV = {"OMP_DYNAMIC": "false", "BLADE_FP_REPRO": "1", "BLADE_FP_CONTRACT": "off", "KMP_AFFINITY": "disabled"}
STRIP = ("OMP_", "GOMP_", "KMP_", "P1_")

RE_TIME = re.compile(r"pairkernel completed in\s+([0-9.eE+-]+)s")
RE_KV = re.compile(r"(\w+)=([-0-9.eE+]+|[0-9a-f]+)")


def run_one(exe, team, cfg):
    env = {k: v for k, v in os.environ.items() if not k.upper().startswith(STRIP)}
    env.update(BASE_ENV)
    env.update(TEAMS[team])
    mode, _, g = cfg.partition(":")
    args = [exe, mode, g or "4"]
    p = subprocess.run(args, env=env, capture_output=True, text=True, timeout=120)
    out = p.stdout
    rec = {"team": team, "config": cfg, "rc": p.returncode}
    m = RE_TIME.search(out)
    rec["ms"] = float(m.group(1)) * 1e3 if m else None
    for line in out.splitlines():
        if line.startswith("checksum = "):
            rec["bits"] = line.split("bits=")[1].strip()
        elif line.startswith("pool_fnv = "):
            rec["pool_fnv"] = line.split("=")[1].strip()
        elif line.startswith("R_fnv = "):
            rec["R_fnv"] = line.split("=")[1].strip()
        elif line.startswith("actual_team = "):
            rec["actual"] = int(line.split()[2])
        elif line.startswith("instr:"):
            for k, v in RE_KV.findall(line):
                rec["i_" + k] = v
    if p.returncode != 0:
        rec["stderr"] = p.stderr[-400:]
    return rec


def measure(exe, out_dir, rounds, seed):
    os.makedirs(out_dir, exist_ok=True)
    rng = random.Random(seed)
    jobs = [(t, c) for t in TEAMS for c in CONFIGS]
    rows = []
    for r in range(rounds):
        order = jobs[:]
        rng.shuffle(order)
        for t, c in order:
            rec = run_one(exe, t, c)
            rec["round"] = r
            rows.append(rec)
        print(f"round {r + 1}/{rounds} done", flush=True)
    keys = sorted({k for row in rows for k in row})
    with open(os.path.join(out_dir, "raw.csv"), "w", newline="") as fh:
        w = csv.DictWriter(fh, fieldnames=keys)
        w.writeheader()
        w.writerows(rows)
    with open(os.path.join(out_dir, "meta.json"), "w") as fh:
        json.dump({"exe": exe, "rounds": rounds, "seed": seed, "teams": TEAMS, "configs": CONFIGS, "base_env": BASE_ENV}, fh, indent=1)


def boot_ci(xs, stat=statistics.median, n=4000, seed=7):
    rng = random.Random(seed)
    if not xs:
        return (float("nan"), float("nan"))
    bs = sorted(stat([rng.choice(xs) for _ in xs]) for _ in range(n))
    return (bs[int(0.025 * n)], bs[int(0.975 * n) - 1])


def analyze(out_dir):
    with open(os.path.join(out_dir, "raw.csv")) as fh:
        rows = list(csv.DictReader(fh))
    lines = []
    # identity first
    ref = {}
    for row in rows:
        if row["config"] == "b:4" and row.get("bits"):
            ref.setdefault("bits", row["bits"]); ref.setdefault("R_fnv", row["R_fnv"]); ref.setdefault("pool_fnv", row["pool_fnv"])
    bad = []
    for row in rows:
        if row["rc"] != "0":
            bad.append(f"rc={row['rc']} {row['team']} {row['config']} round {row['round']}")
            continue
        if row["config"].startswith(("b", "c", "d")):
            for k in ("bits", "R_fnv", "pool_fnv"):
                if row.get(k) != ref.get(k):
                    bad.append(f"{k} {row.get(k)} != {ref.get(k)}: {row['team']} {row['config']} round {row['round']}")
        elif row.get("pool_fnv") != ref.get("pool_fnv"):
            bad.append(f"pool_fnv: {row['team']} {row['config']} round {row['round']}")
    lines.append(f"## identity: {len(rows)} runs; reference (b:4) checksum bits {ref.get('bits')}, R_fnv {ref.get('R_fnv')}, pool_fnv {ref.get('pool_fnv')}")
    lines.append(f"mismatches / failed runs: {len(bad)}" + ("" if not bad else "\n  " + "\n  ".join(bad[:40])))
    # per-round matrix
    by = {}
    for row in rows:
        if row["rc"] == "0" and row["ms"]:
            by.setdefault((row["team"], row["config"]), {})[int(row["round"])] = row
    lines.append("\n## end-to-end ms (the emitted program's own timer): median [95% bootstrap CI], actual team")
    lines.append("| team | " + " | ".join(CONFIGS) + " |")
    lines.append("|---|" + "---|" * len(CONFIGS))
    for t in TEAMS:
        cells = []
        for c in CONFIGS:
            xs = [float(r["ms"]) for r in by.get((t, c), {}).values()]
            if not xs:
                cells.append("--"); continue
            lo, hi = boot_ci(xs)
            act = sorted({r.get("actual") for r in by[(t, c)].values() if r.get("actual")})
            cells.append(f"{statistics.median(xs):.3f} [{lo:.3f}, {hi:.3f}]" + (f" a={'/'.join(act)}" if act else ""))
        lines.append(f"| {t} | " + " | ".join(cells) + " |")

    def paired(t, x, y):
        rx, ry = by.get((t, x), {}), by.get((t, y), {})
        ds = [float(rx[k]["ms"]) - float(ry[k]["ms"]) for k in rx if k in ry]
        if not ds:
            return None
        lo, hi = boot_ci(ds)
        return statistics.median(ds), lo, hi, len(ds)

    pairs = [("b:4", "d:1"), ("b:4", "d:4"), ("b:4", "d:8"), ("b:4", "c"), ("c", "d:4"), ("b:4", "a"),
             ("d:1", "a2:1"), ("d:4", "a2:4"), ("d:8", "a2:8"), ("a2:4", "a"), ("di:4", "d:4"), ("b:1", "b:4"), ("b:8", "b:4")]
    lines.append("\n## paired per-round differences x - y (ms): median [95% bootstrap CI of the median], rounds")
    lines.append("| team | " + " | ".join(f"{x} - {y}" for x, y in pairs) + " |")
    lines.append("|---|" + "---|" * len(pairs))
    for t in TEAMS:
        cells = []
        for x, y in pairs:
            p = paired(t, x, y)
            cells.append("--" if p is None else f"{p[0]:+.3f} [{p[1]:+.3f}, {p[2]:+.3f}] n={p[3]}")
        lines.append(f"| {t} | " + " | ".join(cells) + " |")
    # instruments
    keys = ["actual", "ng", "wall_ns", "prod_ns", "commit_ns", "wait_ns", "wait_frac_core", "startup_ns", "drain_ns",
            "max_wait_ns", "max_wait_g", "waited", "yields", "handoffs", "prod_med", "prod_max", "prod_first8",
            "prod_last8", "commit_med", "commit_max", "wait_med"]
    lines.append("\n## instruments (di:G): medians over rounds. Times in ns; prod / commit / wait are SUMMED over groups "
                 "(core-time); wait_frac_core = summed worker waits / (actual team x region wall); the bound column is "
                 "max(S, (P + S) / actual) from the run's own summed production P and commit service S")
    lines.append("| team | G | " + " | ".join(keys) + " | bound_ns | region / bound |")
    lines.append("|---|---|" + "---|" * (len(keys) + 2))
    for t in TEAMS:
        for g in ("1", "4", "8"):
            rs = list(by.get((t, "di:" + g), {}).values())
            if not rs:
                continue
            med = {k: statistics.median(float(r["i_" + k]) for r in rs) for k in keys}
            bounds = [max(float(r["i_commit_ns"]), (float(r["i_prod_ns"]) + float(r["i_commit_ns"])) / float(r["i_actual"])) for r in rs]
            ratio = statistics.median(float(r["i_wall_ns"]) / b for r, b in zip(rs, bounds))
            lines.append(f"| {t} | {g} | " + " | ".join(f"{med[k]:.4g}" for k in keys) + f" | {statistics.median(bounds):.4g} | {ratio:.3f} |")
    # decision
    lines.append("\n## decision rule (brief W3-3), at T8: d:G beats b:4 by >= 1.5 ms end-to-end (the paired CI's LOWER bound "
                 ">= 1.5 ms) AND the ticket waits are below 20% (median wait_frac_core of di:G < 0.20)")
    for g in ("1", "4", "8"):
        p = paired("T8", "b:4", "d:" + g)
        rs = list(by.get(("T8", "di:" + g), {}).values())
        wf = statistics.median(float(r["i_wait_frac_core"]) for r in rs) if rs else float("nan")
        if p is None:
            lines.append(f"- G={g}: no data"); continue
        ok = p[1] >= 1.5 and wf < 0.20
        lines.append(f"- G={g}: b:4 - d:{g} = {p[0]:+.3f} ms [{p[1]:+.3f}, {p[2]:+.3f}]; wait_frac_core {wf:.3f} -> {'TAKE (planner item follows)' if ok else 'DROP'}")
    text = "\n".join(lines) + "\n"
    with open(os.path.join(out_dir, "summary.md"), "w") as fh:
        fh.write(text)
    print(text)


if __name__ == "__main__":
    if len(sys.argv) >= 3 and sys.argv[1] == "--analyze":
        analyze(sys.argv[2])
    elif len(sys.argv) >= 3:
        exe, out_dir = sys.argv[1], sys.argv[2]
        rounds = int(sys.argv[3]) if len(sys.argv) > 3 else 21
        seed = int(sys.argv[4]) if len(sys.argv) > 4 else 20261021
        measure(exe, out_dir, rounds, seed)
        analyze(out_dir)
    else:
        print(__doc__)
        sys.exit(2)
