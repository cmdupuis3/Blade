"""Independent numpy reference for maskagg (bench/kernels/maskagg/spec.md).

    y = (x(k) : x(k) > 1.0, k ascending);  m = |y|;  checksum = sum y^2 + m
    input_checksum = sum x;  x = gen(k, 10000019, 0, 2)
Output: y (the compacted subsequence, order preserved).
Run: C:\\Python314\\python.exe -P bench/validation/ref_maskagg.py
"""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import benchgen as g  # noqa: E402

SIZES = {"smoke": {"n": 30011}, "full": {"n": 1000003}}


def compute(size):
    n = SIZES[size]["n"]
    x = g.gen_array((n,), 1, 0.0, 2.0)
    y = x[x > 1.0]
    return {"checksum": g.exact_sum(y * y) + float(y.size), "input_checksum": g.exact_sum(x),
            "output": y if size == "smoke" else None, "count": int(y.size)}


if __name__ == "__main__":
    for s in SIZES:
        r = compute(s)
        print(f"maskagg {s}: checksum {r['checksum']!r} input_checksum {r['input_checksum']!r} m {r['count']}")
