"""Independent reference for groupagg_dyn (bench/kernels/groupagg_dyn/spec.md): the dynamic-key
variant of ref_groupagg.py (groups numbered by FIRST occurrence), the same ordered scalar oracle.
Run: C:\\Python314\\python.exe -P bench/validation/ref_groupagg_dyn.py
"""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import ref_groupagg as base  # noqa: E402

SIZES = base.SIZES


def compute(size):
    return base.compute_variant(size, True)


if __name__ == "__main__":
    for s in SIZES:
        r = compute(s)
        print(f"groupagg_dyn {s}: checksum {r['checksum']!r} input_checksum {r['input_checksum']!r} groups {r['groups']}")
