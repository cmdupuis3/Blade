"""Independent numpy references for the round-4 wave-5 measurement controls (group M):
pm49_* (streaming tiers), pm50_stride, pm51_gather, pm53_triad_omp, pm56_mask, pm56_member_*,
pm08_churn, st05_cheap, st05_costly -- the programs are in bench/kernels/<name>/<name>.blade, their
definitions in each source's header comment and bench/RESULTS.md section 20.

Writes bench/validation/pins.json (every size of every kernel.json above).
Run: C:\\Python314\\python.exe -P bench/validation/ref_controls.py [kernel ...]
"""
import json
import math
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import benchgen as g  # noqa: E402
import numpy as np  # noqa: E402

HERE = os.path.dirname(os.path.abspath(__file__))
KERNELS = os.path.join(HERE, "..", "kernels")
S1, S2 = 10000019, 20000038


def fsum(a):
    return math.fsum(np.asarray(a, dtype=np.float64).ravel().tolist())


def pm49(sz, triad, f32):
    n, k = sz["n"], sz["k"]
    dt = np.float32 if f32 else np.float64
    x = g.gen(np.arange(n, dtype=np.int64), S1, 0.0, 1.0).astype(dt)
    y = g.gen(np.arange(n, dtype=np.int64), S2, 0.0, 1.0).astype(dt)
    z = x.copy()
    one = dt(1.0)
    half = dt(0.5)
    for _ in range(k - 1):
        z = z * half + (y if triad else one)
    z64 = z.astype(np.float64)
    ins = fsum(x) + (fsum(y) if triad else 0.0)
    return fsum(z64 * z64), ins


def wts(k):
    return [1.0 + 0.001 * float(t) for t in range(k)]


def pm50(sz):
    r, s, k = sz["r"], sz["s"], sz["k"]
    j = np.arange(r, dtype=np.int64)[:, None]
    c = np.arange(s, dtype=np.int64)[None, :]
    y = g.gen(j + c * 1000003, S1, 0.0, 1.0)
    return math.fsum(float(np.sum(y * w)) for w in wts(k)), fsum(y)


def index_of(i, n, kind):
    b = i // 1021
    whole = (b + 1) * 1021 <= n
    if kind == 1:
        return i
    if kind == 2:
        return (i * 3708013) % n
    if kind == 3:
        return np.where(whole, b * 1021 + ((i % 1021) * 523) % 1021, i)
    h = g.gen_int(i, 30000057)
    if kind == 4:
        return h % n
    return np.where(whole, b * 1021 + h % 1021, i)


def pm51(sz):
    n, k, kind = sz["n"], sz["k"], sz["kind"]
    i = np.arange(n, dtype=np.int64)
    y = g.gen(i, S1, 0.0, 1.0)
    idx = index_of(i, n, kind)
    gy = y[idx]
    return math.fsum(float(np.sum(gy * w)) for w in wts(k)), fsum(y) + float(int(idx.sum()))


def pm56_mask(sz):
    n, sel, run = sz["n"], sz["sel"], sz["run"]
    i = np.arange(n, dtype=np.int64)
    x = g.gen_int(i // run, S1) % 100
    yv = x[x < sel]
    return float(int((yv * yv).sum())) + float(len(yv)), float(int(x.sum()))


def pm56_member(sz):
    n, m, gap = sz["n"], sz["m"], sz["gap"]
    st = np.arange(m, dtype=np.int64) * gap
    probe = g.gen_int(np.arange(n, dtype=np.int64), S1) % (m * gap)
    ids = probe[np.isin(probe, st)]
    return float(int(ids.sum())) + float(len(ids)), float(int(st.sum())) + float(int(probe.sum()))


def pm08(sz):
    m, k = sz["m"], sz["k"]
    big = np.arange(m, dtype=np.float64)
    small = np.arange(513, dtype=np.float64)
    return math.fsum(float(np.sum(big * w)) + float(np.sum(small * w)) for w in wts(k)), 0.0


def st05(sz, cost):
    n = sz["n"]
    i = np.arange(n, dtype=np.int64)
    x = g.gen(i, S1, 0.0, 1.0)
    y = g.gen(i, S2, 0.0, 1.0)
    p = 2.0 * x + 1.0 if cost == 1 else np.exp(np.sin(x)) * np.cos(x)
    return fsum(p) + fsum(x * y) + (1.0 + fsum(p)), fsum(x) + fsum(y)


REF = {
    "pm49_copy64": lambda s: pm49(s, False, False), "pm49_triad64": lambda s: pm49(s, True, False),
    "pm49_copy32": lambda s: pm49(s, False, True), "pm49_triad32": lambda s: pm49(s, True, True),
    "pm53_triad_omp": lambda s: pm49(s, True, False),
    "pm50_stride": pm50, "pm51_gather": pm51, "pm56_mask": pm56_mask,
    "pm56_member_small": pm56_member, "pm56_member_large": pm56_member,
    "pm08_churn": pm08, "st05_cheap": lambda s: st05(s, 1), "st05_costly": lambda s: st05(s, 2),
}


def main(only):
    out_path = os.path.join(HERE, "pins.json")
    pins = {}
    if os.path.isfile(out_path):
        pins = json.load(open(out_path, encoding="utf-8")).get("kernels", {})
    for name, fn in REF.items():
        if only and name not in only:
            continue
        sizes = json.load(open(os.path.join(KERNELS, name, "kernel.json"), encoding="utf-8"))["sizes"]
        per = {}
        for sname, sz in sizes.items():
            cs, ics = fn(sz)
            per[sname] = {"sizes": sz, "checksum": cs, "input_checksum": ics}
            print(f"{name} {sname}: checksum {cs!r} input_checksum {ics!r}", flush=True)
        pins[name] = per
    doc = {"_doc": "pins of the group-M measurement controls; written by ref_controls.py (numpy)",
           "kernels": dict(sorted(pins.items()))}
    with open(out_path, "w", encoding="utf-8", newline="\n") as f:
        json.dump(doc, f, indent=1)
        f.write("\n")


if __name__ == "__main__":
    main(set(sys.argv[1:]))
