"""Independent numpy reference for tensorop (bench/kernels/tensorop/spec.md).

    T1(a, j, k) = sum_i A(a, i) X(i, j, k)          (mode I first)
    T2(a, b, k) = sum_j B(b, j) T1(a, j, k)         (then mode J)
    Y(a, b, c)  = sum_k C(c, k) T2(a, b, k)         (then mode K)
    checksum = sum Y^2;  input_checksum = sum X + sum A + sum B + sum C
    X = gen(k = (i*n2 + j)*n3 + k, 10000019, 0, 1), A = gen(a*n1 + i, 20000038, 0, 1),
    B = gen(b*n2 + j, 30000057, 0, 1),               C = gen(c*n3 + k, 40000076, 0, 1)

An ORDERED scalar oracle, not einsum: every output cell is one chain seeded with +0.0 and
accumulated in ascending contraction index, each product rounded before its add (the
Blade source's `m.mode_product` chain: `acc = 0.0; acc = acc + U(r, t) * X(.., t, ..)`).
The loop over the contraction index is a Python loop; numpy only broadcasts the
independent cells, so no sum is ever reassociated. The research sketch's unseeded first
two chains (start from the first product) give the same bits here, since every term is a
product of non-negative finite inputs (+0.0 + p == p exactly); the committed source and
this oracle both use the +0 seed. The checksum and the input sums are the correctly
rounded `math.fsum` (order-independent truth; every arm is compared at 1e-12 / 1e-14).

The output is Y (row-major a, b, c).
Run: C:\\Python314\\python.exe -P bench/validation/ref_tensorop.py
"""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import benchgen as g  # noqa: E402
import numpy as np  # noqa: E402

SIZES = {"smoke": {"n1": 11, "n2": 13, "n3": 17}, "full": {"n1": 89, "n2": 97, "n3": 101}}


def inputs(n1, n2, n3):
    x = g.gen_array((n1, n2, n3), 1, 0.0, 1.0)
    a = g.gen_array((n1, n1), 2, 0.0, 1.0)
    b = g.gen_array((n2, n2), 3, 0.0, 1.0)
    c = g.gen_array((n3, n3), 4, 0.0, 1.0)
    return x, a, b, c


def contract(x, u, mode):
    """Y = X x_mode U, Y(.., r, ..) = sum_t U(r, t) X(.., t, ..): one +0-seeded chain per
    cell, ascending t, each product rounded (no FMA, no reassociation)."""
    xm = np.moveaxis(x, mode, 0)              # t first
    acc = np.zeros((u.shape[0],) + xm.shape[1:], dtype=np.float64)
    shape = (u.shape[0],) + (1,) * (xm.ndim - 1)
    for t in range(u.shape[1]):
        acc = acc + u[:, t].reshape(shape) * xm[t][np.newaxis, ...]
    return np.moveaxis(acc, 0, mode)


def compute(size):
    n1, n2, n3 = (SIZES[size][p] for p in ("n1", "n2", "n3"))
    x, a, b, c = inputs(n1, n2, n3)
    t1 = contract(x, a, 0)
    t2 = contract(t1, b, 1)
    y = contract(t2, c, 2)
    ics = g.exact_sum(x) + g.exact_sum(a) + g.exact_sum(b) + g.exact_sum(c)
    return {"checksum": g.exact_sum(y * y), "input_checksum": ics,
            "output": y if size == "smoke" else None}


if __name__ == "__main__":
    for s in SIZES:
        r = compute(s)
        print(f"tensorop {s}: checksum {r['checksum']!r} input_checksum {r['input_checksum']!r}")
