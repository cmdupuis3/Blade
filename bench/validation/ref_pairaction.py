"""Independent reference for `pairaction` (bench/kernels/pairaction/spec.md): an ORDERED scalar oracle.

pairkernel's points (x, y, z: inputs 1-3, k = i, [0, 1)) and Matern-5/2 matrix
W(i, j) = (1 + a + a*a/3) exp(-a), a = sqrt(5 r2) / 0.7, r2 = dx*dx + dy*dy + dz*dz (left to right);
R = 3 right-hand sides v_r (inputs 4, 5, 6, k = i, [0, 1)).
  z(i)   = fold(+, 0.0, [ W(i, j) for j = 0 .. n-1 ])          -- the ascending chain
  u_r(i) = fold(+, 0.0, [ W(i, j) * v_r(j) for j = 0 .. n-1 ])  -- the ascending chain
  y_r(i) = u_r(i) / z(i)
Each chain is evaluated IN ITS SOURCE ORDER (numpy's add.accumulate along a row is the sequential
left fold, seed first), so z / u_r / y_r are the strict values every repro arm computes (up to the
platform libm's last bit in exp / sqrt; the spec's "libm" risk).
checksum = sum_ij W(i, j) (the full logical matrix) + sum z + sum u_0 + sum u_1 + sum u_2
           + sum y_0 + sum y_1 + sum y_2, each term an exact sum, combined left to right;
input_checksum = sum (x + y + z) + sum v_0 + sum v_1 + sum v_2 (exact sums, left to right).

Run: C:\\Python314\\python.exe -P ref_pairaction.py [size ...]
"""
import pathlib
import sys

# run with -P (numpy is in the user site), which does not put this directory on sys.path
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))

import numpy as np  # noqa: E402

import ref_common_A as rc  # noqa: E402

NAME = "pairaction"
BLOCK = 128  # rows per block, keeps the full-size temporaries small


def inputs(n):
    d = {c: rc.gen_array((n,), j, 0.0, 1.0) for j, c in ((1, "x"), (2, "y"), (3, "z"))}
    for r in range(3):
        d["v%d" % r] = rc.gen_array((n,), 4 + r, 0.0, 1.0)
    return d


def _rows(x, y, z, lo, hi):
    dx = x[lo:hi, None] - x[None, :]
    dy = y[lo:hi, None] - y[None, :]
    dz = z[lo:hi, None] - z[None, :]
    r2 = dx * dx + dy * dy + dz * dz
    a = np.sqrt(5.0 * r2) / 0.7
    return (1.0 + a + a * a / 3.0) * np.exp(-a)


def _chains(terms):
    """Each row's ascending left fold from the seed 0.0 (sequential: add.accumulate)."""
    seeded = np.concatenate([np.zeros((terms.shape[0], 1)), terms], axis=1)
    return np.add.accumulate(seeded, axis=1)[:, -1]


def outputs(n):
    d = inputs(n)
    vs = [d["v%d" % r] for r in range(3)]
    zs, us, wsum = [], [[], [], []], []
    for lo in range(0, n, BLOCK):
        w = _rows(d["x"], d["y"], d["z"], lo, min(n, lo + BLOCK))
        wsum.append(rc.exact_sum(w))
        zs.append(_chains(w))
        for r in range(3):
            us[r].append(_chains(w * vs[r][None, :]))
    z = np.concatenate(zs)
    u = [np.concatenate(us[r]) for r in range(3)]
    y = [u[r] / z for r in range(3)]
    return d, rc.exact_sum(wsum), z, u, y


def checksums(n):
    d, sw, z, u, y = outputs(n)
    cs = sw
    for t in [z] + u + y:
        cs = cs + rc.exact_sum(t)
    ics = rc.exact_sum(d["x"] + d["y"] + d["z"])
    for r in range(3):
        ics = ics + rc.exact_sum(d["v%d" % r])
    return cs, ics


if __name__ == "__main__":
    rc.main(NAME, checksums, sys.argv[1:])
