"""Shared helpers for blade-A's numpy references (gram, comoment3, antisym, pairkernel).

The generator itself is `benchgen.py` (the suite's one numpy transcription of README
section 5); this module only adds size lookup from each kernel's kernel.json and the
command-line driver. Run every script here with `C:\\Python314\\python.exe -P` (not -I:
this host's numpy lives in the user site-packages).
"""
import json
import pathlib
import sys

import benchgen
from benchgen import exact_sum  # noqa: F401  (re-exported for the ref scripts)

HERE = pathlib.Path(__file__).resolve().parent

# The adopted generator is variant D = benchgen's "R5" (2026-10-17). Pinned here so a stale
# default in benchgen cannot silently produce pins for the wrong inputs.
GENERATOR = "R5"
if benchgen.VARIANT != GENERATOR:
    print("ref_common_A: note: benchgen.VARIANT is %r; group-A references use %r" % (benchgen.VARIANT, GENERATOR), file=sys.stderr)


def gen_array(shape, j, lo, hi):
    """The j-th input array (seed 10000019 * j) under the adopted generator."""
    return benchgen.gen_array(shape, j, lo, hi, variant=GENERATOR)
KERNELS = HERE.parent / "kernels"


def sizes(kernel):
    """{size_name: {param: value}} from bench/kernels/<kernel>/kernel.json."""
    with open(KERNELS / kernel / "kernel.json", encoding="utf-8") as f:
        return json.load(f)["sizes"]


def call_with(fn, params, kernel):
    """Call fn with the kernel's parameters in its declared order."""
    order = {"gram": ("p", "n"), "comoment3": ("p", "n"), "antisym": ("p", "n"), "pairkernel": ("n",)}[kernel]
    return fn(*(int(params[k]) for k in order))


def pins(kernel, checksums, which=None):
    out = {}
    for name, params in sizes(kernel).items():
        if which and name not in which:
            continue
        cs, ics = call_with(checksums, params, kernel)
        out[name] = {"params": params, "checksum": float("%.17g" % cs), "input_checksum": float("%.17g" % ics)}
    return out


def main(kernel, checksums, which):
    for name, v in pins(kernel, checksums, which or None).items():
        print("%s %s %s: checksum = %.17g  input_checksum = %.17g" % (kernel, name, v["params"], v["checksum"], v["input_checksum"]))
