"""Independent stdlib R5 reference. Runner only; no committed generated numeric pins."""
import argparse
import hashlib
import json
import math
from pathlib import Path

CONTRACT = "PM52-immediate-two-stores-v1"
N = 2053


def r5(k, seed):
    # Python integers give exact arithmetic; this does not import benchgen/compiler output.
    modulus = 2**31
    h1 = ((k + seed) * 1103515245 + 12345) % modulus
    h2 = (h1 * (h1 // 4096 + 1)) % modulus
    h3 = (h2 * 1103515245 + 12345) % modulus
    h4 = (h3 * h3 // 65536) % modulus
    h5 = (h4 * 1103515245 + 12345) % modulus
    return float(h5) / float(modulus)


def serial_sum(values):
    acc = 0.0
    for value in values:
        acc = acc + value
    return acc


def from_inputs(x, y):
    if len(x) != len(y):
        raise ValueError("input extent mismatch")
    # Separate operations preserve the contract-off graph; no fma or norm identity.
    p = [(1.5 * a) + b for a, b in zip(x, y)]
    q = [v * v for v in p]
    return {
        "n": len(x), "p": p, "q": q,
        "checksum_serial": serial_sum(q), "checksum_fsum": math.fsum(q),
        "input_checksum_serial": serial_sum(x) + serial_sum(y),
        "input_checksum_fsum": math.fsum(x) + math.fsum(y),
    }


def compute():
    x = [r5(k, 10000019) for k in range(N)]
    y = [r5(k, 20000038) for k in range(N)]
    return {"schema": 1, "contract": CONTRACT, "generator": "R5-fixed",
            "seeds": [10000019, 20000038], **from_inputs(x, y)}


def absolute_path(value):
    path = Path(value)
    if not path.is_absolute():
        raise argparse.ArgumentTypeError("an absolute path is required")
    return path.resolve()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--out", type=absolute_path, required=True)
    args = parser.parse_args()
    result = compute()
    result["reference_sha256"] = hashlib.sha256(Path(__file__).read_bytes()).hexdigest()
    args.out.write_text(json.dumps(result, indent=2, allow_nan=False) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
