"""Independent numpy reference for `gram` (bench/kernels/gram/spec.md).

G(i, j) = sum_s X(i, s) X(j, s), X = (p, n), k = i*n + s, seed 10000019, [0, 1).
checksum = sum over the full logical p x p matrix of G(i, j)^2; input_checksum = sum X.

Run: C:\\Python314\\python.exe -P ref_gram.py [size ...]   (default: every size in kernel.json)
"""
import pathlib
import sys

# run with -P (numpy is in the user site), which does not put this directory on sys.path
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))

import numpy as np  # noqa: E402

import ref_common_A as rc  # noqa: E402

NAME = "gram"


def inputs(p, n):
    return {"X": rc.gen_array((p, n), 1, 0.0, 1.0)}


def output(p, n):
    """The full logical output G (dense p x p)."""
    x = inputs(p, n)["X"]
    return x @ x.T


def checksums(p, n):
    x = inputs(p, n)["X"]
    g = x @ x.T
    return rc.exact_sum(g * g), rc.exact_sum(x)


if __name__ == "__main__":
    rc.main(NAME, checksums, sys.argv[1:])
