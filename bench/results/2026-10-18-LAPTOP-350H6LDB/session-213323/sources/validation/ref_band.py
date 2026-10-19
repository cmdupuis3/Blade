"""Independent numpy reference for band (bench/kernels/band/spec.md).

    s = sum over 0 <= i, j < n with |i - j| <= w of phi(i, j) x_i x_j
    phi(i, j) = (2 + d / (w + 1)) / (1 + d * d),  d = Float64(i - j)
    checksum = s;  input_checksum = sum x;  x = gen(k = i, 10000019, 0, 1)
The terms are formed per diagonal offset d (vectorized, the same float64 arithmetic per cell
as the spec: (phi * x_i) * x_j), and summed exactly (math.fsum), so the pin does not depend on
the band's summation order.
Run: C:\\Python314\\python.exe -P bench/validation/ref_band.py
"""
import math
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import benchgen as g  # noqa: E402
import numpy as np  # noqa: E402

SIZES = {"smoke": {"n": 211, "w": 5}, "full": {"n": 20011, "w": 101}, "narrow": {"n": 20011, "w": 11}}


def phi(d, w):
    d = float(d)
    return (2.0 + d / float(w + 1)) / (1.0 + d * d)


def terms(n, w, x):
    """All band terms phi(i, j) * x_i * x_j, as a list of per-offset arrays (d = i - j)."""
    out = []
    for d in range(-w, w + 1):
        i = np.arange(max(0, d), min(n, n + d))   # rows with 0 <= i - d < n
        j = i - d
        out.append((phi(d, w) * x[i]) * x[j])
    return out


def compute(size):
    n, w = SIZES[size]["n"], SIZES[size]["w"]
    x = g.gen_array((n,), 1, 0.0, 1.0)
    t = terms(n, w, x)
    s = math.fsum(v for arr in t for v in arr.tolist())
    cells = sum(arr.size for arr in t)
    assert cells == n * (2 * w + 1) - w * (w + 1), cells
    return {"checksum": s, "input_checksum": g.exact_sum(x), "output": None, "cells": cells}


def band_matrix_smoke():
    """Dense B at smoke size, for cell-by-cell checks of a dump (n x n, zeros outside the band)."""
    n, w = SIZES["smoke"]["n"], SIZES["smoke"]["w"]
    B = np.zeros((n, n))
    for i in range(n):
        for j in range(max(0, i - w), min(n, i + w + 1)):
            B[i, j] = phi(i - j, w)
    return B


if __name__ == "__main__":
    for s in SIZES:
        r = compute(s)
        print(f"band {s}: checksum {r['checksum']!r} input_checksum {r['input_checksum']!r} cells {r['cells']}")
