"""Independent numpy reference for hashjoin (bench/kernels/hashjoin/spec.md).

    lk = floor(gen(i, 10000019, 0, K)), la = floor(gen(i, 20000038, 0, 1024))   (i < N)
    rk = floor(gen(j, 30000057, 0, K)), ra = floor(gen(j, 40000076, 0, 1024))   (j < M)
    matched = (la(i) * ra(j) : lk(i) == rk(j)), (i, j) in lex order (i ascending, then j)
    checksum = sum matched + |matched| + fp,  fp = left fold a -> (3 a + b) mod 1000000007 from 0
    input_checksum = sum lk + sum la + sum rk + sum ra
Output: matched (the relation, lex order). Computed by brute force over all N x M pairs
(np.nonzero of the equality matrix is row-major: exactly the lex order), independent of
any CSR.
Run: C:\\Python314\\python.exe -P bench/validation/ref_hashjoin.py
"""
import os
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import benchgen as g  # noqa: E402

SIZES = {"smoke": {"n": 211, "m": 101, "k": 41}, "full": {"n": 2003, "m": 1009, "k": 401}}
MOD = 1000000007


def inputs(size):
    n, m, k = SIZES[size]["n"], SIZES[size]["m"], SIZES[size]["k"]
    lk = np.floor(g.gen_array((n,), 1, 0.0, float(k))).astype(np.int64)
    la = np.floor(g.gen_array((n,), 2, 0.0, 1024.0)).astype(np.int64)
    rk = np.floor(g.gen_array((m,), 3, 0.0, float(k))).astype(np.int64)
    ra = np.floor(g.gen_array((m,), 4, 0.0, 1024.0)).astype(np.int64)
    return lk, la, rk, ra


def compute(size):
    lk, la, rk, ra = inputs(size)
    ii, jj = np.nonzero(lk[:, None] == rk[None, :])   # row-major = lex order
    matched = la[ii] * ra[jj]
    total = int(matched.sum())
    fp = 0
    for v in matched.tolist():
        fp = (3 * fp + v) % MOD
    checksum = float(total) + float(matched.size) + float(fp)
    input_checksum = float(int(lk.sum()) + int(la.sum()) + int(rk.sum()) + int(ra.sum()))
    return {"checksum": checksum, "input_checksum": input_checksum,
            "output": matched if size == "smoke" else None, "count": int(matched.size),
            "total": total, "fp": fp}


if __name__ == "__main__":
    for s in SIZES:
        r = compute(s)
        print(f"hashjoin {s}: checksum {r['checksum']!r} input_checksum {r['input_checksum']!r} "
              f"J {r['count']} total {r['total']} fp {r['fp']}")
