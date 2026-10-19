"""Independent numpy reference for `pairkernel` (bench/kernels/pairkernel/spec.md).

n points (x_i, y_i, z_i); x, y, z each (n), k = i, seeds 10000019, 20000038, 30000057, [0, 1).
r2 = dx*dx + dy*dy + dz*dz (left to right), a = sqrt(5 r2) / 0.7,
K(i, j) = (1 + a + a*a/3) exp(-a). checksum = sum over the full n x n matrix of K^2;
input_checksum = sum x + sum y + sum z.

Run: C:\\Python314\\python.exe -P ref_pairkernel.py [size ...]
"""
import pathlib
import sys

# run with -P (numpy is in the user site), which does not put this directory on sys.path
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))

import numpy as np  # noqa: E402

import ref_common_A as rc  # noqa: E402

NAME = "pairkernel"
BLOCK = 256  # rows per block, keeps the full-size temporaries small


def inputs(n):
    return {c: rc.gen_array((n,), j, 0.0, 1.0) for j, c in ((1, "x"), (2, "y"), (3, "z"))}


def _rows(x, y, z, lo, hi):
    dx = x[lo:hi, None] - x[None, :]
    dy = y[lo:hi, None] - y[None, :]
    dz = z[lo:hi, None] - z[None, :]
    r2 = dx * dx + dy * dy + dz * dz
    a = np.sqrt(5.0 * r2) / 0.7
    return (1.0 + a + a * a / 3.0) * np.exp(-a)


def output(n):
    d = inputs(n)
    return _rows(d["x"], d["y"], d["z"], 0, n)


def checksums(n):
    d = inputs(n)
    parts = []
    for lo in range(0, n, BLOCK):
        k = _rows(d["x"], d["y"], d["z"], lo, min(n, lo + BLOCK))
        parts.append(rc.exact_sum(k * k))
    return rc.exact_sum(parts), rc.exact_sum(np.concatenate([d["x"], d["y"], d["z"]]))


if __name__ == "__main__":
    rc.main(NAME, checksums, sys.argv[1:])
