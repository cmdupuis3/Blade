"""Independent numpy reference for twofir (bench/kernels/twofir/spec.md).

    y1(i) = 0.0625 x(i-3) + 0.125 x(i-2) + 0.1875 x(i-1) + 0.25 x(i)
            + 0.1875 x(i+1) + 0.125 x(i+2) + 0.0625 x(i+3)            centres 3 .. n-4  (n - 6 cells)
    y2(i) = 0.5 x(i) - 0.25 x(i-4) - 0.25 x(i+4) + 0.125 x(i-8) + 0.125 x(i+8)
                                                                      centres 8 .. n-9  (n - 16 cells)
    each evaluated left to right as written, every product rounded (no FMA);
    checksum = s1 + s2 with s1 = sum y1^2, s2 = sum y2^2 (two separate sums, then one add);
    input_checksum = sum x;  x = gen(k, 10000019, 0, 1)

The outputs are y1 and y2 (in index order).
Run: C:\\Python314\\python.exe -P bench/validation/ref_twofir.py
"""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import benchgen as g  # noqa: E402

SIZES = {"smoke": {"n": 20011}, "full": {"n": 2000003}}


def outputs(x):
    n = x.shape[0]

    def tap(d, lo, hi):            # x(i + d) for centres i = lo .. hi (inclusive)
        return x[lo + d: hi + d + 1]

    a1, b1 = 3, n - 4
    y1 = (((((0.0625 * tap(-3, a1, b1) + 0.125 * tap(-2, a1, b1)) + 0.1875 * tap(-1, a1, b1))
            + 0.25 * tap(0, a1, b1)) + 0.1875 * tap(1, a1, b1)) + 0.125 * tap(2, a1, b1)) + 0.0625 * tap(3, a1, b1)
    a2, b2 = 8, n - 9
    y2 = (((0.5 * tap(0, a2, b2) - 0.25 * tap(-4, a2, b2)) - 0.25 * tap(4, a2, b2))
          + 0.125 * tap(-8, a2, b2)) + 0.125 * tap(8, a2, b2)
    return y1, y2


def compute(size):
    n = SIZES[size]["n"]
    x = g.gen_array((n,), 1, 0.0, 1.0)
    y1, y2 = outputs(x)
    assert y1.shape[0] == n - 6 and y2.shape[0] == n - 16
    s1 = g.exact_sum(y1 * y1)
    s2 = g.exact_sum(y2 * y2)
    return {"checksum": s1 + s2, "input_checksum": g.exact_sum(x),
            "output": (y1, y2) if size == "smoke" else None}


if __name__ == "__main__":
    for s in SIZES:
        r = compute(s)
        print(f"twofir {s}: checksum {r['checksum']!r} input_checksum {r['input_checksum']!r}")
