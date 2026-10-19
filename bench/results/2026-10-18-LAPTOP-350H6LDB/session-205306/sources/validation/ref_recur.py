"""Independent numpy reference for recur (bench/kernels/recur/spec.md).

RK4 for du/dt = u - u*u*u, h = 0.01, constants spelled as in the spec:
    k1 = f(u); k2 = f(u + 0.005 k1); k3 = f(u + 0.005 k2); k4 = f(u + 0.01 k3)
    u' = u + (0.01 / 6.0) * (k1 + 2 k2 + 2 k3 + k4)      (bracket summed left to right)
Output: the trajectory U(t, x), t = 0..nt-1, U(0, :) = u0 = gen(k = x, 10000019, 0, 2).
checksum = sum U^2;  input_checksum = sum u0.
Run: C:\\Python314\\python.exe -P bench/validation/ref_recur.py
"""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import benchgen as g  # noqa: E402
import numpy as np  # noqa: E402

SIZES = {"smoke": {"nt": 11, "nx": 211}, "full": {"nt": 301, "nx": 20011}}


def f(u):
    return u - u * u * u


def compute(size):
    nt, nx = SIZES[size]["nt"], SIZES[size]["nx"]
    u0 = g.gen_array((nx,), 1, 0.0, 2.0)
    U = np.empty((nt, nx))
    U[0] = u0
    u = u0
    for t in range(1, nt):
        k1 = f(u)
        k2 = f(u + 0.005 * k1)
        k3 = f(u + 0.005 * k2)
        k4 = f(u + 0.01 * k3)
        u = u + (0.01 / 6.0) * (((k1 + 2.0 * k2) + 2.0 * k3) + k4)
        U[t] = u
    return {"checksum": g.exact_sum(U * U), "input_checksum": g.exact_sum(u0),
            "output": U if size == "smoke" else None}


if __name__ == "__main__":
    for s in SIZES:
        r = compute(s)
        print(f"recur {s}: checksum {r['checksum']!r} input_checksum {r['input_checksum']!r}")
