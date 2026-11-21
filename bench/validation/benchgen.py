"""The suite's input generator (README section 5), vectorized in numpy.

Independent of Blade and Fortran: a direct transcription of the three-round formula,
exact in int64 (every intermediate is below 2^56).

    h1 = ((k + seed) * 1103515245 + 12345) mod 2^31
    h2 = (h1 * (h1 div 4096 + 1)) mod 2^31
    h3 = (h2 * 1103515245 + 12345) mod 2^31
    u  = h3 / 2^31
    value = lo + (hi - lo) * u

Run the scripts in this directory with `C:\\Python314\\python.exe -P <script>`. NOT `-I`:
this host's NumPy lives in the user site-packages, which `-I` hides.
"""
import math
import os

# Keep any BLAS that numpy calls single-threaded (host safety: no all-core AVX spikes).
for _v in ("OPENBLAS_NUM_THREADS", "OMP_NUM_THREADS", "MKL_NUM_THREADS"):
    os.environ.setdefault(_v, "1")

import numpy as np  # noqa: E402

M31 = np.int64(2147483648)
A = np.int64(1103515245)
C = np.int64(12345)
SEED = 10000019  # seed of the j-th input array is SEED * j


# Which formula the suite uses. "R3" = the README section 5 formula as adopted on
# 2026-10-17 (affine, quadratic, affine). "R5" = R3 plus a middle-square round and a final
# affine round (blade-B's proposal "D" in notes/generator-check.md). Overridable for
# comparisons with the environment variable BENCH_GENERATOR.
VARIANT = os.environ.get("BENCH_GENERATOR", "R5")


def gen_int(k, seed, variant=None):
    """The final integer state (h3 for R3, h5 for R5) for linear indices k and a seed."""
    v = variant or VARIANT
    k = np.asarray(k, dtype=np.int64)
    h1 = ((k + np.int64(seed)) * A + C) % M31
    h2 = (h1 * (h1 // np.int64(4096) + np.int64(1))) % M31
    h3 = (h2 * A + C) % M31
    if v == "R3":
        return h3
    if v == "R5":
        h4 = (h3 * h3 // np.int64(65536)) % M31  # h3 * h3 < 2^62: exact in int64
        return (h4 * A + C) % M31
    raise ValueError(f"unknown generator variant {v!r}")


def gen(k, seed, lo, hi, variant=None):
    """Input values lo + (hi - lo) * u for linear indices k."""
    u = gen_int(k, seed, variant).astype(np.float64) / 2147483648.0
    return lo + (hi - lo) * u


def gen_array(shape, j, lo, hi, variant=None):
    """The j-th input array (seed = 10000019 * j) of the given row-major shape."""
    n = int(np.prod(shape))
    return gen(np.arange(n, dtype=np.int64), SEED * j, lo, hi, variant).reshape(shape)


def exact_sum(a):
    """Correctly rounded sum of the float64 terms (order-independent truth for the pins)."""
    return math.fsum(np.asarray(a, dtype=np.float64).ravel().tolist())


def fmt17(x):
    return float("%.17g" % x)
