"""Independent numpy reference for onepass (bench/kernels/onepass/spec.md).

    S1 = sum x, S2 = sum x*x, S3 = sum (x*x)*x, S4 = sum ((x*x)*x)*x
    checksum = S1 + 2 S2 + 3 S3 + 4 S4;  input_checksum = sum x;  x = gen(k, 10000019, 0, 2)

Each S_k is a correctly rounded sum (math.fsum); the final combination is evaluated in
float64 as written. Run: C:\\Python314\\python.exe -P bench/validation/ref_onepass.py
"""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import benchgen as g  # noqa: E402

SIZES = {"smoke": {"n": 50021}, "full": {"n": 5000011}}


def compute(size):
    n = SIZES[size]["n"]
    x = g.gen_array((n,), 1, 0.0, 2.0)
    x2 = x * x
    x3 = x2 * x
    x4 = x3 * x
    s1, s2, s3, s4 = (g.exact_sum(t) for t in (x, x2, x3, x4))
    return {"checksum": s1 + 2.0 * s2 + 3.0 * s3 + 4.0 * s4, "input_checksum": s1, "output": None}


if __name__ == "__main__":
    for s in SIZES:
        r = compute(s)
        print(f"onepass {s}: checksum {r['checksum']!r} input_checksum {r['input_checksum']!r}")
