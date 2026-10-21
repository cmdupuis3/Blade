"""Independent numpy reference for haloadjoint (bench/kernels/haloadjoint/spec.md).

    F_c(x) = ((x(c-1) * x(c+1)) + (sin(x(c)) * x(c+2))) - ((0.5 * x(c-2)) * x(c-2))   c = 2 .. n-3
    y(c)   = F_c(x)                                             (n - 4 cells, index order)
    dx     = J^T w by the spec's EXPLICIT ORDERED UPDATE LIST   (n cells):
             dx = +0, then for every destination j, in this order, each update present only
             when its window centre c = j - offset lies in 2 .. n-3:
               U1 offset +2  (c = j-2)  +=  w(c) * sin(x(c))
               U2 offset +1  (c = j-1)  +=  w(c) * x(c-1)
               U3 offset  0  (c = j)    +=  (w(c) * x(c+2)) * cos(x(c))
               U4 offset -1  (c = j+1)  +=  w(c) * x(c+1)
               U5 offset -2  (c = j+2)  +=  ((-w(c)) * x(c-2)) * 0.5      (left occurrence)
               U6 offset -2  (c = j+2)  +=  (-w(c)) * (0.5 * x(c-2))      (right occurrence)
    checksum = sum y^2 + sum dx^2;  input_checksum = sum x + sum w
    x = gen(k, 10000019, 0, 1), w = gen(k, 20000038, 0, 1)

An ORDERED oracle, not einsum: y is evaluated with numpy's elementwise operations in the
spec's parenthesization (every product and sum rounded, no FMA); dx is six whole-array passes
in the list's order, each `dx[dest] = dx[dest] + term` (one rounded add per cell per update),
so every cell receives its contributions in the list's order -- the same chain the Blade
reverse sweep (one pass per update, descending offset) and both Fortran twins form. sin and
cos are Python's math module (the C runtime's libm), per element; the arms' libms may differ
in the last place, which the 1e-12 tolerances absorb. The checksum and the input sums are the
correctly rounded `math.fsum` (order-independent truth).

Each update is the analytic partial derivative of w(c) F_c (dF/dx(c+2) = sin x(c),
dF/dx(c+1) = x(c-1), dF/dx(c) = cos(x(c)) x(c+2), dF/dx(c-1) = x(c+1), and the two halves of
dF/dx(c-2) = -x(c-2) from the product's two occurrences); `selfcheck()` confirms the list
against a central finite difference of the loss at a few cells, independently of any arm.

The outputs are y (centres 2 .. n-3) and dx (0 .. n-1), in index order.
Run: C:\\Python314\\python.exe -P bench/validation/ref_haloadjoint.py
"""
import math
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import benchgen as g  # noqa: E402
import numpy as np  # noqa: E402

SIZES = {"smoke": {"n": 1013}, "full": {"n": 50021}}


def inputs(n):
    return g.gen_array((n,), 1, 0.0, 1.0), g.gen_array((n,), 2, 0.0, 1.0)


def primal(x):
    n = x.shape[0]
    s = np.array([math.sin(v) for v in x[2:n - 2]])
    return ((x[1:n - 3] * x[3:n - 1]) + (s * x[4:n])) - ((0.5 * x[0:n - 4]) * x[0:n - 4])


def adjoint(x, w):
    """dx by the ordered update list (six passes, list order)."""
    n = x.shape[0]
    s = np.array([math.sin(v) for v in x])
    c = np.array([math.cos(v) for v in x])
    wc = w[2:n - 2]                       # w(c) for the centres c = 2 .. n-3
    dx = np.zeros(n)
    dx[4:n] = dx[4:n] + wc * s[2:n - 2]                          # U1: dest c+2
    dx[3:n - 1] = dx[3:n - 1] + wc * x[1:n - 3]                  # U2: dest c+1
    dx[2:n - 2] = dx[2:n - 2] + (wc * x[4:n]) * c[2:n - 2]       # U3: dest c
    dx[1:n - 3] = dx[1:n - 3] + wc * x[3:n - 1]                  # U4: dest c-1
    dx[0:n - 4] = dx[0:n - 4] + ((-wc) * x[0:n - 4]) * 0.5       # U5: dest c-2, left
    dx[0:n - 4] = dx[0:n - 4] + (-wc) * (0.5 * x[0:n - 4])       # U6: dest c-2, right
    return dx


def compute(size):
    n = SIZES[size]["n"]
    x, w = inputs(n)
    y = primal(x)
    dx = adjoint(x, w)
    assert y.shape[0] == n - 4 and dx.shape[0] == n
    return {"checksum": g.exact_sum(y * y) + g.exact_sum(dx * dx),
            "input_checksum": g.exact_sum(x) + g.exact_sum(w),
            "output": (y, dx) if size == "smoke" else None}


def selfcheck():
    """The update list is the gradient of L = sum_c w(c) F_c(x): central differences at the
    boundary cells and a few interior cells (smoke inputs), relative agreement 1e-6."""
    n = SIZES["smoke"]["n"]
    x, w = inputs(n)
    dx = adjoint(x, w)

    def loss(xv):
        return math.fsum((w[2:n - 2] * primal(xv)).tolist())

    worst = 0.0
    for j in (0, 1, 2, 3, 4, 500, n - 5, n - 4, n - 3, n - 2, n - 1):
        h = 1e-6
        xp, xm = x.copy(), x.copy()
        xp[j] += h
        xm[j] -= h
        fd = (loss(xp) - loss(xm)) / (2 * h)
        worst = max(worst, abs(fd - dx[j]) / max(1.0, abs(dx[j])))
    assert worst < 1e-6, worst
    return worst


if __name__ == "__main__":
    print(f"haloadjoint selfcheck: worst finite-difference rel err {selfcheck():.1e}")
    for s in SIZES:
        r = compute(s)
        print(f"haloadjoint {s}: checksum {r['checksum']!r} input_checksum {r['input_checksum']!r}")
