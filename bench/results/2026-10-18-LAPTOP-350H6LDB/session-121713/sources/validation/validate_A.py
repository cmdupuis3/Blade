"""Cell-by-cell validation of the group-A Blade kernels against the numpy references (M11).

For each kernel, runs bench/validation/dump/<kernel>_dump.blade (SMOKE size) through
Blade.exe, parses the printed arrays, and compares
  * the dense LOGICAL output (G / T / L / K, read through Blade's canonicalizing accessor)
    against ref_<kernel>.output(...), every cell;
  * the PACKED storage itself (g / t / l / kmat, printed in storage order) against the
    reference's canonical cells in Blade's left-justified order (i <= j [<= k]; antisym i < j);
  * checksum and input_checksum against the numpy pins.
Blade prints 15 significant digits, so agreement is expected at ~1e-15 relative.
Regimes: `licensed` (default) = no FP knob; `repro` = BLADE_FP_REPRO=1; no other FP knob is
passed (BLADE_FP_CONTRACT is cleared: the LLVM lane contracts by default since the flip).

Run: C:\\Python314\\python.exe -P validate_A.py [--lanes cpp,llvm] [--regime licensed|repro] [kernel ...]
The C++ lane honours --print; the LLVM lane prints every top-level binding (also parsed).
Smoke only: each run finishes in milliseconds.
"""
import ast
import os
import pathlib
import subprocess
import sys

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))

import numpy as np  # noqa: E402

import ref_antisym  # noqa: E402
import ref_common_A as rc  # noqa: E402
import ref_comoment3  # noqa: E402
import ref_gram  # noqa: E402
import ref_pairkernel  # noqa: E402

HERE = rc.HERE
BLADE = HERE.parent.parent / "bin" / "Release" / "net10.0" / "Blade.exe"
UCRT = r"C:\msys64\ucrt64\bin"
REGIME = "licensed"  # the headline Blade regime; `--regime repro` = knobs off

# kernel -> (module, dense output name, packed name, packed kind)
KERNELS = {
    "gram": (ref_gram, "G", "g", "sym2"),
    "comoment3": (ref_comoment3, "T", "t", "sym3"),
    "antisym": (ref_antisym, "L", "l", "anti2"),
    "pairkernel": (ref_pairkernel, "K", "kmat", "sym2"),
}


def run_blade(path, names, lane):
    env = dict(os.environ)
    env["PATH"] = UCRT + os.pathsep + env.get("PATH", "")
    env["BLADE_BLAS"] = "0"
    env.pop("BLADE_LLVM", None)
    # FP knobs (after the 2026-10-17 default flip): `licensed` = no FP knob at all (the default
    # honours every licence in both lanes and the LLVM lane contracts by default);
    # `repro` = BLADE_FP_REPRO=1. Every other FP knob is cleared so the defaults govern
    # (BLADE_FP_CONTRACT is deliberately NOT passed any more).
    for v in ("BLADE_FP_CONTRACT", "BLADE_FP_REASSOC", "BLADE_FP_REPRO"):
        env.pop(v, None)
    if REGIME == "repro":
        env["BLADE_FP_REPRO"] = "1"
    elif REGIME != "licensed":
        raise ValueError("unknown regime %r" % REGIME)
    if lane == "llvm":
        env["BLADE_LLVM"] = "1"
    cmd = [str(BLADE), "run", str(path), "--print", ",".join(names), "--no-cache"]
    p = subprocess.run(cmd, capture_output=True, text=True, env=env, timeout=600)
    if p.returncode != 0:
        raise RuntimeError("blade failed (%d): %s\n%s" % (p.returncode, p.stdout[-2000:], p.stderr[-2000:]))
    fallback = "llvm lane refused" in p.stderr
    vals = {}
    for line in p.stdout.splitlines():
        if " = " in line and "completed in" not in line:
            name, _, rhs = line.partition(" = ")
            try:
                vals[name.strip()] = ast.literal_eval(rhs.strip())
            except (ValueError, SyntaxError):
                pass
    return vals, fallback


def packed_reference(dense, kind):
    p = dense.shape[0]
    if kind == "sym2":
        return [dense[i, i:].tolist() for i in range(p)]
    if kind == "anti2":
        return [dense[i, i + 1:].tolist() for i in range(p)]
    if kind == "sym3":
        return [dense[i, j, k] for i in range(p) for j in range(i, p) for k in range(j, p)]
    raise ValueError(kind)


def flat(x):
    if isinstance(x, np.ndarray):
        return x.astype(np.float64).ravel().tolist()
    if isinstance(x, list):
        out = []
        for v in x:
            out.extend(flat(v))
        return out
    return [float(x)]


def compare(got, ref):
    g = np.asarray(flat(got), dtype=np.float64)
    r = np.asarray(flat(ref), dtype=np.float64)
    if g.shape != r.shape:
        return {"ok": False, "why": "cell count %d != %d" % (g.size, r.size)}
    err = np.abs(g - r)
    scale = np.maximum(np.abs(r), np.finfo(np.float64).tiny)
    nz = np.abs(r) > 0
    return {
        "ok": True,
        "cells": int(g.size),
        "max_abs": float(err.max()) if g.size else 0.0,
        "max_rel": float((err[nz] / scale[nz]).max()) if nz.any() else 0.0,
        "zero_ref_cells_nonzero": int(np.count_nonzero(g[~nz])),
    }


def rel(a, b):
    return abs(a - b) / max(abs(b), 1e-300)


def validate(kernel, lane):
    mod, dense_name, packed_name, kind = KERNELS[kernel]
    params = rc.sizes(kernel)["smoke"]
    dense_ref = rc.call_with(mod.output, params, kernel)
    cs_ref, ics_ref = rc.call_with(mod.checksums, params, kernel)
    path = HERE / "dump" / ("%s_dump.blade" % kernel)
    vals, fallback = run_blade(path, [dense_name, packed_name, "checksum", "input_checksum"], lane)
    res = {"kernel": kernel, "lane": lane, "params": params, "llvm_fallback": fallback}
    res["dense"] = compare(vals[dense_name], dense_ref)
    res["packed"] = compare(vals[packed_name], packed_reference(dense_ref, kind))
    if kind == "sym2" or kind == "anti2":
        res["packed_rows"] = [len(r) for r in vals[packed_name]][:3] + ["..."]
    res["checksum_rel"] = rel(float(vals["checksum"]), cs_ref)
    res["input_checksum_rel"] = rel(float(vals["input_checksum"]), ics_ref)
    return res


def main(argv):
    global REGIME
    lanes = ["cpp"]
    while argv and argv[0] in ("--lanes", "--regime"):
        if argv[0] == "--lanes":
            lanes = argv[1].split(",")
        else:
            REGIME = argv[1]
        argv = argv[2:]
    print("regime: %s (licensed = no FP knob, the default; repro = BLADE_FP_REPRO=1)" % REGIME)
    kernels = argv or list(KERNELS)
    worst = 0.0
    for lane in lanes:
        for k in kernels:
            r = validate(k, lane)
            d, pk = r["dense"], r["packed"]
            worst = max(worst, d.get("max_rel", 1.0), pk.get("max_rel", 1.0), r["checksum_rel"], r["input_checksum_rel"])
            print("%-10s %-4s %s  dense: %s  packed: %s  checksum rel %.3g  input rel %.3g%s" % (
                k, lane, r["params"], d, pk, r["checksum_rel"], r["input_checksum_rel"],
                "  [LLVM FALLBACK]" if r["llvm_fallback"] else ""), flush=True)
    print("worst relative error over every compared quantity: %.3g" % worst)
    return 0 if worst < 1e-12 else 1


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
