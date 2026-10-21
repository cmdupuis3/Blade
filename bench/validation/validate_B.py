"""Cell-by-cell validation of the group-B Blade outputs against the numpy references
(methodology review M11), at SMOKE size, in the Blade C++ lane.

    C:\\Python314\\python.exe -P bench/validation/validate_B.py [--regime licensed|repro]
                                                                [--blade PATH] [--saved DIR]

For each dump variant in bench/validation/dump/ it compiles and runs the program (C++ lane,
BLADE_BLAS=0, BLADE_OMP_THREADS=1, BLADE_EXE_CACHE=0, ucrt64 first on PATH; FP regime
licensed = no FP knob, the default; repro = BLADE_FP_REPRO=1), parses the
printed bindings, and compares them with ref_<kernel>.py:

  * stencil: lap, (ny-2) x (nx-2), every cell;
  * recur:   the trajectory U(t, x), nt x nx, every cell;
  * maskagg: y has a runtime extent and Blade gives it no printed form (BL7004), so the
             dump prints an order-sensitive fingerprint (count, first and last cell, two
             Horner folds, the sum); each is recomputed from numpy's y;
  * twofir:  y1 (n - 6) and y2 (n - 16), every cell;
  * tensorop: Y, n1 x n2 x n3 (printed flat, row-major), every cell;
  * haloadjoint: y (n - 4) and dx (n), every cell (dx = ad.grad's J^T w against the ordered
             update list; the list's ORDER is checked by exact numeric equality, ==, in
             dump/haloadjoint_order.blade);
  * all:     checksum and input_checksum against the numpy pins.

Tolerance: |blade - ref| <= 1e-12 * max(1, |ref|) per cell. Blade prints 15 significant
digits, and its C++ lane contracts a*b+c into FMAs (-ffp-contract=fast), so exact equality is
not expected. Exit status 0 iff every comparison passes.
"""
import argparse
import ast
import math
import os
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import benchgen as g  # noqa: E402,F401
import ref_band  # noqa: E402
import ref_haloadjoint  # noqa: E402
import ref_maskagg  # noqa: E402
import ref_recur  # noqa: E402
import ref_stencil  # noqa: E402
import ref_tensorop  # noqa: E402
import ref_twofir  # noqa: E402

DEFAULT_BLADE = os.path.normpath(os.path.join(HERE, "..", "..", "bin", "Release", "net10.0", "Blade.exe"))
UCRT = r"C:\msys64\ucrt64\bin"
TOL = 1e-12

DUMPS = {
    "stencil": ["lap", "checksum", "input_checksum"],
    "recur": ["traj", "checksum", "input_checksum"],
    "maskagg": ["m", "y_first", "y_last", "horner_a", "horner_b", "sum_y", "checksum", "input_checksum"],
    "band": ["cells", "lexsum", "horner", "checksum", "input_checksum"],
    "twofir": ["y1", "y2", "checksum", "input_checksum"],
    "tensorop": ["y", "checksum", "input_checksum"],
    "haloadjoint": ["y", "dx", "checksum", "input_checksum"],
}


def run_dump(blade, kernel, saved, regime):
    if saved:
        with open(os.path.join(saved, f"{kernel}_dump.out"), encoding="utf-8") as f:
            return f.read()
    env = dict(os.environ)
    env["PATH"] = UCRT + os.pathsep + env.get("PATH", "")
    env.update({"BLADE_BLAS": "0", "BLADE_OMP_THREADS": "1", "BLADE_EXE_CACHE": "0"})
    for v in ("BLADE_LLVM", "BLADE_FP_REASSOC", "BLADE_FP_CONTRACT", "BLADE_FP_REPRO"):
        env.pop(v, None)
    if regime == "repro":
        env["BLADE_FP_REPRO"] = "1"
    src = os.path.join(HERE, "dump", f"{kernel}_dump.blade")
    r = subprocess.run([blade, "run", src, "--print", ",".join(DUMPS[kernel])],
                       capture_output=True, text=True, env=env, timeout=600)
    if r.returncode != 0:
        raise RuntimeError(f"{kernel}: blade run failed ({r.returncode}): {r.stderr[-2000:]}")
    return r.stdout


def parse(text):
    vals = {}
    for line in text.splitlines():
        if " = " not in line or line.endswith("s") and " completed in " in line:
            continue
        name, rhs = line.split(" = ", 1)
        try:
            vals[name.strip()] = ast.literal_eval(rhs.strip())
        except (ValueError, SyntaxError):
            pass
    return vals


class Report:
    def __init__(self):
        self.lines, self.ok = [], True

    def cmp_scalar(self, what, got, ref):
        err = abs(got - ref) / max(1.0, abs(ref))
        good = err <= TOL
        self.ok &= good
        self.lines.append(f"  {what}: blade {got!r} ref {ref!r} rel err {err:.2e} {'ok' if good else 'FAIL'}")

    def cmp_array(self, what, got, ref):
        flat_g = [float(v) for row in got for v in (row if isinstance(row, list) else [row])]
        flat_r = [float(v) for v in ref.ravel()]
        if len(flat_g) != len(flat_r):
            self.ok = False
            self.lines.append(f"  {what}: SHAPE MISMATCH blade {len(flat_g)} cells, ref {len(flat_r)}")
            return
        worst, at = 0.0, -1
        for i, (a, b) in enumerate(zip(flat_g, flat_r)):
            e = abs(a - b) / max(1.0, abs(b))
            if e > worst:
                worst, at = e, i
        good = worst <= TOL
        self.ok &= good
        self.lines.append(f"  {what}: {len(flat_r)} cells, worst rel err {worst:.2e} at flat index {at} "
                          f"{'ok' if good else 'FAIL'}")


def horner(y, c):
    a = 0.0
    for v in y:
        a = c * a + float(v)
    return a


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--blade", default=DEFAULT_BLADE)
    ap.add_argument("--saved", default=None, help="directory of saved <kernel>_dump.out files")
    ap.add_argument("--regime", choices=("licensed", "repro"), default="licensed",
                    help="licensed = no FP knob (the default); repro = BLADE_FP_REPRO=1")
    args = ap.parse_args()
    rep = Report()
    rep.lines.append(f"regime: {args.regime}")
    for kernel in DUMPS:
        rep.lines.append(f"{kernel}:")
        v = parse(run_dump(args.blade, kernel, args.saved, args.regime))
        if kernel == "stencil":
            r = ref_stencil.compute("smoke")
            rep.cmp_array("lap", v["lap"], r["output"])
        elif kernel == "recur":
            r = ref_recur.compute("smoke")
            rep.cmp_array("traj", v["traj"], r["output"])
        elif kernel == "twofir":
            r = ref_twofir.compute("smoke")
            rep.cmp_array("y1", v["y1"], r["output"][0])
            rep.cmp_array("y2", v["y2"], r["output"][1])
        elif kernel == "haloadjoint":
            r = ref_haloadjoint.compute("smoke")
            rep.cmp_array("y", v["y"], r["output"][0])
            rep.cmp_array("dx", v["dx"], r["output"][1])
        elif kernel == "tensorop":
            r = ref_tensorop.compute("smoke")
            rep.cmp_array("y", v["y"], r["output"])
        elif kernel == "band":
            r = ref_band.compute("smoke")
            n, w = ref_band.SIZES["smoke"]["n"], ref_band.SIZES["smoke"]["w"]
            x = g.gen_array((n,), 1, 0.0, 1.0)
            cells = [(i, j) for i in range(n) for j in range(max(0, i - w), min(n, i + w + 1))]
            good = int(v["cells"]) == len(cells)
            rep.ok &= good
            rep.lines.append(f"  cells: blade {v['cells']} ref {len(cells)} {'ok' if good else 'FAIL'}")
            lex = sum(i * 1000 + j for i, j in cells)
            good = int(v["lexsum"]) == lex
            rep.ok &= good
            rep.lines.append(f"  lexsum: blade {v['lexsum']} ref {lex} {'ok' if good else 'FAIL'}")
            a = 0.0
            for i, j in cells:  # lexicographic order: i ascending, j ascending
                a = 0.999 * a + (ref_band.phi(i - j, w) * float(x[i])) * float(x[j])
            rep.cmp_scalar("horner (lexicographic order)", float(v["horner"]), a)
        else:
            r = ref_maskagg.compute("smoke")
            y = r["output"]
            good = int(v["m"]) == int(y.size)
            rep.ok &= good
            rep.lines.append(f"  m: blade {v['m']} ref {y.size} {'ok' if good else 'FAIL'}")
            rep.cmp_scalar("y_first", float(v["y_first"]), float(y[0]))
            rep.cmp_scalar("y_last", float(v["y_last"]), float(y[-1]))
            rep.cmp_scalar("horner_a (0.5)", float(v["horner_a"]), horner(y, 0.5))
            rep.cmp_scalar("horner_b (0.999)", float(v["horner_b"]), horner(y, 0.999))
            rep.cmp_scalar("sum_y", float(v["sum_y"]), math.fsum(y.tolist()))
        rep.cmp_scalar("checksum", float(v["checksum"]), r["checksum"])
        rep.cmp_scalar("input_checksum", float(v["input_checksum"]), r["input_checksum"])
    print("\n".join(rep.lines))
    print("ALL OK" if rep.ok else "FAILURES")
    sys.exit(0 if rep.ok else 1)


if __name__ == "__main__":
    main()
