"""Independent numpy reference for `comoment3` (bench/kernels/comoment3/spec.md).

T(i, j, k) = (1/n) sum_s X(i, s) X(j, s) X(k, s), X = (p, n), k = i*n + s, seed 10000019,
[0, 1). checksum = sum over the full logical p^3 cube of T^2; input_checksum = sum X.
Per cell the sum is divided by n once (as the Blade source and spec sketch do).

Run: C:\\Python314\\python.exe -P ref_comoment3.py [size ...]
"""
import pathlib
import sys

# run with -P (numpy is in the user site), which does not put this directory on sys.path
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))

import numpy as np  # noqa: E402

import ref_common_A as rc  # noqa: E402

NAME = "comoment3"


def inputs(p, n):
    return {"X": rc.gen_array((p, n), 1, 0.0, 1.0)}


def output(p, n):
    """The full logical output T (dense p x p x p), built one i-slab at a time."""
    x = inputs(p, n)["X"]
    t = np.empty((p, p, p))
    for i in range(p):
        t[i] = ((x[i] * x) @ x.T) / float(n)
    return t


def checksums(p, n):
    x = inputs(p, n)["X"]
    parts = []
    for i in range(p):
        slab = ((x[i] * x) @ x.T) / float(n)
        parts.append(rc.exact_sum(slab * slab))
    return rc.exact_sum(parts), rc.exact_sum(x)


if __name__ == "__main__":
    rc.main(NAME, checksums, sys.argv[1:])
