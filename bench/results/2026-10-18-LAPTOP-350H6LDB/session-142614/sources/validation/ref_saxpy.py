"""Independent numpy reference for saxpy (bench/kernels/saxpy/spec.md).

    y2 = 1.5 x + y;  checksum = sum y2^2;  input_checksum = sum x + sum y
    x = gen(k, 10000019, 0, 1), y = gen(k, 20000038, 0, 1)
(The output y2 is a named output but not dumped: it is a single elementwise expression.)
Run: C:\\Python314\\python.exe -P bench/validation/ref_saxpy.py
"""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import benchgen as g  # noqa: E402

SIZES = {"smoke": {"n": 20011}, "full": {"n": 2000003}, "control": {"n": 700001}}


def compute(size):
    n = SIZES[size]["n"]
    x = g.gen_array((n,), 1, 0.0, 1.0)
    y = g.gen_array((n,), 2, 0.0, 1.0)
    y2 = 1.5 * x + y
    return {"checksum": g.exact_sum(y2 * y2), "input_checksum": g.exact_sum(x) + g.exact_sum(y),
            "output": y2 if size == "smoke" else None}


if __name__ == "__main__":
    for s in SIZES:
        r = compute(s)
        print(f"saxpy {s}: checksum {r['checksum']!r} input_checksum {r['input_checksum']!r}")
