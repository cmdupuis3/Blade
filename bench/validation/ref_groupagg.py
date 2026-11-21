"""Independent reference for groupagg and groupagg_dyn (bench/kernels/groupagg/spec.md).

    key(k) = floor(ngroups * u(k)), u = gen(k, 10000019, 0, 1)       (groupagg: positional)
    key(k) = 7919 * floor(ngroups * u(k)) + 13                       (groupagg_dyn: dynamic,
                                                                      groups by FIRST occurrence)
    x(k) = gen(k, 20000038, 0, 2)
    s(g) = sum x, q(g) = sum x^2, c(g) = count, over key(k) = group g
    checksum = sum_g s(g)^2 + sum_g q(g) + sum_g c(g)^2
    input_checksum = sum x + sum key

The pins are the ORDERED SCALAR ORACLE (spec "Chain order"): every per-group chain in
ascending row order from 0 (0 + x = x exactly, x >= 0), then the three group folds in group
order, then the input sums ascending -- the strict association, which Blade's repro build and
every expert's serial loop evaluate bit for bit. The correctly rounded (fsum) values are
printed beside them; they agree to far below the harness tolerance.
Run: C:\\Python314\\python.exe -P bench/validation/ref_groupagg.py
"""
import math
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import benchgen as g  # noqa: E402
import numpy as np  # noqa: E402

SIZES = {"smoke": {"n": 30011, "ngroups": 1009}, "full": {"n": 1000003, "ngroups": 1009}}


def inputs(size, dynamic):
    n, ng = SIZES[size]["n"], SIZES[size]["ngroups"]
    u = g.gen_array((n,), 1, 0.0, 1.0)
    pos = np.floor(u * float(ng)).astype(np.int64)  # ng * u is exact: a 31-bit fraction times 11 bits
    key = 7919 * pos + 13 if dynamic else pos
    x = g.gen_array((n,), 2, 0.0, 2.0)
    return key, x


def groups(size, dynamic):
    """(s, q, c) per group, in the regime's group order, by the ordered scalar chains."""
    key, x = inputs(size, dynamic)
    ng = SIZES[size]["ngroups"]
    if dynamic:
        number = {}  # dict insertion order = first occurrence
        for k in key.tolist():
            if k not in number:
                number[k] = len(number)
        gid = [number[k] for k in key.tolist()]
        G = len(number)
    else:
        gid = key.tolist()
        G = ng
    s = [0.0] * G
    q = [0.0] * G
    c = [0] * G
    for gi, v in zip(gid, x.tolist()):
        s[gi] += v
        q[gi] += v * v
        c[gi] += 1
    return s, q, c, key, x


def compute_variant(size, dynamic):
    s, q, c, key, x = groups(size, dynamic)
    a1 = 0.0
    for v in s:
        a1 += v * v
    a2 = 0.0
    for v in q:
        a2 += v
    ic = 0
    for m in c:
        ic += m * m
    checksum = a1 + a2 + float(ic)
    sx = 0.0
    for v in x.tolist():
        sx += v
    sk = 0.0
    for k in key.tolist():
        sk += float(k)
    exact = math.fsum([v * v for v in s]) + math.fsum(q) + float(ic)
    return {"checksum": checksum, "input_checksum": sx + sk, "checksum_fsum": exact,
            "input_checksum_fsum": g.exact_sum(x) + math.fsum(float(k) for k in key.tolist()),
            "groups": len(c), "empty": sum(1 for m in c if m == 0)}


def compute(size):
    return compute_variant(size, False)


if __name__ == "__main__":
    for name, dyn in (("groupagg", False), ("groupagg_dyn", True)):
        for s in SIZES:
            r = compute_variant(s, dyn)
            print(f"{name} {s}: checksum {r['checksum']!r} (fsum {r['checksum_fsum']!r}) "
                  f"input_checksum {r['input_checksum']!r} (fsum {r['input_checksum_fsum']!r}) "
                  f"groups {r['groups']} empty {r['empty']}")
