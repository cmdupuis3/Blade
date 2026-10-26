"""Write bench/validation/pins-A.json: the numpy pins of the group-A kernels.

Schema: {kernel: {size: {"checksum": float, "input_checksum": float, "sizes": {param: int}}}}
with 17 significant digits; "sizes" is the exact parameter map the pin was computed at
(from kernel.json), so the harness can refuse a stale pin. The harness merges pins-A.json and pins-B.json.
Run: C:\\Python314\\python.exe -P pins_A.py [size ...]   (default: every size in kernel.json;
full sizes take a few seconds of single-threaded numpy each).
"""
import json
import pathlib
import sys

# run with -P (numpy is in the user site), which does not put this directory on sys.path
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))

import ref_antisym  # noqa: E402
import ref_comoment3
import ref_common_A as rc
import ref_gram
import ref_pairaction
import ref_pairkernel

MODULES = (ref_gram, ref_comoment3, ref_antisym, ref_pairkernel, ref_pairaction)


def main(which):
    out = {}
    for m in MODULES:
        out[m.NAME] = rc.pins(m.NAME, m.checksums, which or None)
        for name, v in out[m.NAME].items():
            print("%s %s: checksum = %.17g  input_checksum = %.17g" % (m.NAME, name, v["checksum"], v["input_checksum"]), flush=True)
    path = rc.HERE / "pins-A.json"
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        # repr of a Python float round-trips, so 17 significant digits are never lost
        strict = {k: {s: {"checksum": v["checksum"], "input_checksum": v["input_checksum"],
                          "sizes": {p: int(x) for p, x in v["params"].items()}}
                      for s, v in d.items()} for k, d in out.items()}
        json.dump(strict, f, indent=2, sort_keys=True)
        f.write("\n")
    print("wrote", path)


if __name__ == "__main__":
    main(sys.argv[1:])
