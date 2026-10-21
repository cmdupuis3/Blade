"""Write bench/validation/pins-B.json from the independent numpy references of the group-B
and control kernels: {kernel: {size: {"checksum": x, "input_checksum": y, "sizes": {param:
int}}}}, 17 significant digits, for size in (smoke, full). "sizes" is the exact parameter map
the pin was computed at (ref_<kernel>.SIZES), so a consumer can refuse a stale pin. The generator variant is benchgen.VARIANT (R3 unless
BENCH_GENERATOR says otherwise) and is printed; the JSON carries no metadata key so the
harness can merge it with pins-A.json unchanged.

Run: C:\\Python314\\python.exe -P bench/validation/make_pins_B.py
"""
import importlib
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import benchgen as g  # noqa: E402

KERNELS = ["fusedchain", "onepass", "stencil", "recur", "maskagg", "dot", "saxpy", "band", "twofir", "tensorop"]


def num17(x):
    return "%.17g" % x


def main():
    pins = {}
    for k in KERNELS:
        mod = importlib.import_module("ref_" + k)
        pins[k] = {}
        for size in mod.SIZES:  # smoke, full, and any extra variant (e.g. saxpy's drift "control")
            r = mod.compute(size)
            pins[k][size] = (r["checksum"], r["input_checksum"], dict(mod.SIZES[size]))
            print(f"{k:10s} {size:7s} checksum {num17(r['checksum'])} input_checksum {num17(r['input_checksum'])}")
    lines = ["{"]
    for i, k in enumerate(KERNELS):
        lines.append(f'  "{k}": {{')
        names = list(pins[k])
        for j, size in enumerate(names):
            c, ic, sz = pins[k][size]
            comma = "," if j < len(names) - 1 else ""
            szs = ", ".join(f'"{p}": {int(v)}' for p, v in sz.items())
            lines.append(f'    "{size}": {{"checksum": {num17(c)}, "input_checksum": {num17(ic)}, '
                         f'"sizes": {{{szs}}}}}{comma}')
        lines.append("  }" + ("," if i < len(KERNELS) - 1 else ""))
    lines.append("}")
    dest = os.path.join(HERE, "pins-B.json")
    with open(dest, "w", encoding="utf-8", newline="\n") as f:
        f.write("\n".join(lines) + "\n")
    print(f"generator variant {g.VARIANT}; wrote {dest}")


if __name__ == "__main__":
    main()
