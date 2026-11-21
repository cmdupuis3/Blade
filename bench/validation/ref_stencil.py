"""Independent numpy reference for stencil (bench/kernels/stencil/spec.md).

    lap(i, j) = u(i-1, j) + u(i+1, j) + u(i, j-1) + u(i, j+1) - 4 u(i, j)   (left to right)
    on the interior, (ny-2) x (nx-2);  checksum = sum lap^2;  input_checksum = sum u
    u = gen(k = i*nx + j, 10000019, 0, 1)

The output is lap (row-major, interior rows 1..ny-2, columns 1..nx-2).
Run: C:\\Python314\\python.exe -P bench/validation/ref_stencil.py
"""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import benchgen as g  # noqa: E402

SIZES = {"smoke": {"ny": 31, "nx": 41}, "full": {"ny": 1501, "nx": 2003}}


def compute(size):
    ny, nx = SIZES[size]["ny"], SIZES[size]["nx"]
    u = g.gen_array((ny, nx), 1, 0.0, 1.0)
    lap = (((u[:-2, 1:-1] + u[2:, 1:-1]) + u[1:-1, :-2]) + u[1:-1, 2:]) - 4.0 * u[1:-1, 1:-1]
    return {"checksum": g.exact_sum(lap * lap), "input_checksum": g.exact_sum(u),
            "output": lap if size == "smoke" else None}


if __name__ == "__main__":
    for s in SIZES:
        r = compute(s)
        print(f"stencil {s}: checksum {r['checksum']!r} input_checksum {r['input_checksum']!r}")
