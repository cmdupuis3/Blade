"""Independent numpy reference for fusedchain (bench/kernels/fusedchain/spec.md).

    s1 = 1.5 a + 0.25;  s2 = s1 / (1 + |s1|);  s3 = s2 * s2 - 0.5 * s2;  checksum = sum s3^2
    input_checksum = sum a;  a = gen(k, 10000019, 0, 1)

Sums are correctly rounded (math.fsum) sums of the float64 terms, so the pin does not depend on
a summation order. Run: C:\\Python314\\python.exe -P bench/validation/ref_fusedchain.py
"""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import benchgen as g  # noqa: E402
import numpy as np  # noqa: E402

SIZES = {"smoke": {"n": 20011}, "full": {"n": 2000003}}


def compute(size):
    n = SIZES[size]["n"]
    a = g.gen_array((n,), 1, 0.0, 1.0)
    s1 = 1.5 * a + 0.25
    s2 = s1 / (1.0 + np.abs(s1))
    s3 = s2 * s2 - 0.5 * s2
    return {"checksum": g.exact_sum(s3 * s3), "input_checksum": g.exact_sum(a), "output": None}


if __name__ == "__main__":
    for s in SIZES:
        r = compute(s)
        print(f"fusedchain {s}: checksum {r['checksum']!r} input_checksum {r['input_checksum']!r}")
