"""Independent numpy reference for `antisym` (bench/kernels/antisym/spec.md).

L(i, j) = sum_s (X(i, s) Y(j, s) - X(j, s) Y(i, s)) = (X Y^T - Y X^T)(i, j);
X, Y = (p, n), k = i*n + s, seeds 10000019 (X) and 20000038 (Y), [0, 1).
checksum = sum over the full logical p x p matrix of L^2; input_checksum = sum X + sum Y.

Run: C:\\Python314\\python.exe -P ref_antisym.py [size ...]
"""
import pathlib
import sys

# run with -P (numpy is in the user site), which does not put this directory on sys.path
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))

import numpy as np  # noqa: E402

import ref_common_A as rc  # noqa: E402

NAME = "antisym"


def inputs(p, n):
    return {"X": rc.gen_array((p, n), 1, 0.0, 1.0), "Y": rc.gen_array((p, n), 2, 0.0, 1.0)}


def output(p, n):
    """The full logical output L (dense p x p, zero diagonal, L^T = -L)."""
    d = inputs(p, n)
    x, y = d["X"], d["Y"]
    return x @ y.T - y @ x.T


def checksums(p, n):
    d = inputs(p, n)
    x, y = d["X"], d["Y"]
    l = x @ y.T - y @ x.T
    return rc.exact_sum(l * l), rc.exact_sum(np.concatenate([x.ravel(), y.ravel()]))


if __name__ == "__main__":
    rc.main(NAME, checksums, sys.argv[1:])
