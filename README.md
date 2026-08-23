# Blade

[![Project Status: WIP](https://www.repostatus.org/badges/latest/wip.svg)](https://www.repostatus.org/#wip)
[![nightly](https://img.shields.io/github/actions/workflow/status/cmdupuis3/Blade/ci.yml?event=schedule&label=nightly)](https://github.com/cmdupuis3/Blade/actions/workflows/ci.yml?query=event%3Aschedule)
[![build](https://img.shields.io/github/actions/workflow/status/cmdupuis3/Blade/ci.yml?branch=master&event=push&label=build)](https://github.com/cmdupuis3/Blade/actions/workflows/ci.yml?query=branch%3Amaster+event%3Apush)

Blade is a general purpose array-functional programming language. 

Blade is primarily built to solve array problems with complex grid structures, particularly involving symmetric arrays. 
The syntax extends the classic ML-style grammar with cutting-edge concepts like rank and arity polymorphism, iteration patterns as first class values, and symmetry deduction from kernel annotations.

Spanning the simplest of arithmetic functions to powerful combinators of combinators, Blade guarantees *the **fastest** way is the **only** way*.

## IDE Support

A VS Code extension is available [here](https://github.com/cmdupuis3/Blade-REPL). Blade-REPL provides a REPL, tooltips for most objects, and full in-editor type deduction.


<img width="639" height="334" alt="blade_fullsc" src="https://github.com/user-attachments/assets/5c64c845-b0a7-4267-8b43-b5b07c51ced6" />

## Requirements

Building the compiler needs only the **.NET SDK 10** (F# 10; zero NuGet dependencies):

```bash
git clone https://github.com/cmdupuis3/Blade && cd Blade && dotnet build -c Release
```

Running Blade programs additionally needs **g++ with OpenMP** (C++17; MSYS2
UCRT64 on Windows, any recent GCC elsewhere). Check your environment with:

```bash
blade doctor
```

which compile-and-run probes every dependency and reports what is configured,
from where, and what is broken (`--json` for tooling).

Optional dependencies, all probed by `doctor`:
* **BLAS/LAPACK** — OpenBLAS by default (`OPENBLAS_DIR`), or any CBLAS/LAPACKE
  implementation (e.g. MKL) via `BLADE_BLAS_LINK`/`BLADE_BLAS_INCLUDE`/
  `BLADE_BLAS_FLAVOR`; off by default, in which case Blade emits its own loops
* **NetCDF** (`NETCDF_DIR` or system libnetcdf) — Zarr and CSV need no library
* **MPI** — MS-MPI on Windows, OpenMPI/MPICH elsewhere
* **CUDA** — nvcc (+ MSVC Build Tools on Windows)

Configuration is env vars or a `blade.toolchain.json` beside the binary (env
wins per key); see [docs/plans/plan-toolchain-packaging.md](docs/plans/plan-toolchain-packaging.md).

## Current State

Blade is currently in development. The progenitor to Blade, Blade-DSL, is now in `/legacy`.
