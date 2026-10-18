"""Independent numpy reference for dot (bench/kernels/dot/spec.md).

    checksum = sum a*b;  input_checksum = sum a + sum b
    a = gen(k, 10000019, 0, 1), b = gen(k, 20000038, 0, 1)
Run: C:\\Python314\\python.exe -P bench/validation/ref_dot.py
"""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import benchgen as g  # noqa: E402

SIZES = {"smoke": {"n": 30011}, "full": {"n": 3000017}}


def compute(size):
    n = SIZES[size]["n"]
    a = g.gen_array((n,), 1, 0.0, 1.0)
    b = g.gen_array((n,), 2, 0.0, 1.0)
    return {"checksum": g.exact_sum(a * b), "input_checksum": g.exact_sum(a) + g.exact_sum(b),
            "output": None}


if __name__ == "__main__":
    for s in SIZES:
        r = compute(s)
        print(f"dot {s}: checksum {r['checksum']!r} input_checksum {r['input_checksum']!r}")
