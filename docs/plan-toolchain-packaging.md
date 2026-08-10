# Toolchain Packaging & Dependency Setup

Status: approved 2026-08-09. Spans two repos: this one (compiler) and
`../Blade-REPL` (extension; its vendor/fetch work is tracked there).
Goal: someone who clones either repo can get a working build environment
with one command, on any OS.

---

## Principle: .NET is the only orchestration runtime

.NET is the one hard dependency, so ALL setup/diagnostic logic lives in F# —
no PowerShell/bash twin scripts carrying logic (twins diverge). The bootstrap
story is identical on every OS:

```
git clone <repo> && cd Blade && dotnet build -c Release && blade setup
```

This works because `dotnet build` needs nothing but the SDK (zero NuGet
references), so the compiler binary always exists *before* any native
toolchain does — no chicken-and-egg. Optional `setup.sh`/`setup.cmd`
wrappers are 3 lines each (check dotnet → build → exec) and contain no
decisions.

Per-OS knowledge is quarantined as DATA, never control flow:

- **`src/Platforms.fs`** (landed) — the one module that knows how the host
  OS spells toolchain artifacts: exe/obj/shared-lib extensions, the MPI link
  flag (`-lmsmpi` / `-lmpi`), libm's name (`ucrtbase.dll` / `libm.so.6` /
  `libSystem.dylib`), install-prefix library search (`findSharedLib`).
  Build.fs, LinAlgPatterns.fs and the interpreter consult it; nothing else
  branches on `RuntimeInformation`.
- **`src/Toolchain.fs`** (landed) — `blade.toolchain.json`, the durable,
  OS-neutral configuration record written by `blade setup` and read
  env-first (`Toolchain.get`: live env var > cached file > absent). Keys are
  spelled exactly like the env vars they mirror. A shell env file would be
  OS-flavored; a JSON the compiler reads is not.

## BLAS/LAPACK: the four-tier contract (landed)

OpenBLAS is the default *implementation*; resolution is vendor-neutral.
`LinAlgPatterns.resolveBlasTier`, first match wins:

| Tier | Trigger | Meaning |
|---|---|---|
| `TierOff` | `BLADE_BLAS=0`, or nothing configured | No-BLAS fallback: emitted loop nests, synthesized Jacobi for `eigh`. Always works; stays the default (byte-identity differentials). |
| `TierExplicit` | `BLADE_BLAS_LINK` (+ optional `BLADE_BLAS_INCLUDE`, `BLADE_LAPACK_LINK`, `BLADE_LAPACK_INCLUDE`) | Verbatim include dirs (PathSeparator-delimited) + link inputs. **The MKL/BLIS door**: e.g. `BLADE_BLAS_INCLUDE=$MKLROOT/include`, `BLADE_BLAS_LINK="-L$MKLROOT/lib/intel64 -lmkl_rt"`, `BLADE_BLAS_FLAVOR=mkl`. |
| `TierOpenBlasDir` | `OPENBLAS_DIR` | Convenience shorthand for an OpenBLAS install prefix; expanded via `Platforms.findSharedLib` (direct DLL/.so path, `-L`/`-l` fallback). |
| `TierSystem` | `BLADE_BLAS=1` alone | Bare `-lopenblas` on default search paths (pacman/apt/brew installs). |

Decoupling: on `TierExplicit`, `lapackAvailable` requires its own
`BLADE_LAPACK_LINK` — a BLAS-only install (BLIS without LAPACKE) dispatches
contractions while `eigh` still falls back to Jacobi. On the OpenBLAS tiers
LAPACK rides the BLAS resolution (LAPACKE is bundled).

Header flavor: `BLADE_BLAS_FLAVOR=mkl` adds `-DBLADE_BLAS_MKL`, under which
`blade_linalg.hpp`/`blade_lapack.hpp` include `mkl_cblas.h`/`mkl_lapacke.h`
(MKL implements the standard interfaces but not the netlib header names).

The tier's expansion into compile/link halves is
`LinAlgPatterns.blasBuildFlags` — one gate, one expansion, consumed by
`Build.compileCppWithExtra`. Tests: `blade test linalg` → "BLAS Tier
Resolution" block (pure in-process, whole env surface pinned).

## `blade doctor` (next)

F# verb reusing Build.fs probes, but with **real compile/link probes, not
PATH probes**:

- compile+run a hello.cpp — the only reliable catch for the UCRT64/mingw64
  PATH-shadow trap (`which g++` succeeds, every compile dies silently)
- link a one-call `cblas_dgemm` probe against the resolved tier; same for
  `LAPACKE_dsyevd`
- netcdf compile+link; MPI compile+link + `mpiexec` resolution; `nvcc` + its
  host compiler (cl.exe on Windows)
- prints per dependency: resolved tier/state + which source configured it
  (env var / toolchain.json / probe); `--json` for the extension and CI

Every probe that exists in Build.fs stays there; doctor only orchestrates
and reports.

## `blade setup` (after doctor)

`blade setup [--minimal|--default|--full] [--blas=source|prebuilt|system|none]`,
idempotent throughout; ends by running doctor's relevant probes so setup
cannot report success on a config that doesn't link. Writes
`blade.toolchain.json` beside the binary.

- `--blas=source` — shallow-clone OpenMathLib/OpenBLAS at the tag pinned in
  `deps.json` into `$BLADE_TOOLS/OpenBLAS-src`, `make -j` +
  `make install PREFIX=$BLADE_TOOLS/openblas`, skip entirely if the
  installed lib exists (`--force` rebuilds). Same commands on all three OSes
  (MSYS2 make on Windows). Needs make + gfortran (LAPACK half); without
  gfortran offer `NO_LAPACK=1` and leave the LAPACK gate off. Writes
  `OPENBLAS_DIR`.
- `--blas=prebuilt` — point at any existing build via
  `--blas-include/--blas-link/--lapack-link/--flavor` → writes tier-1
  values. Covers MKL, vendor OpenBLAS releases, BLIS.
- `--blas=system` — print (optionally run) the package-manager line from the
  per-OS data table; writes `BLADE_BLAS=1`.
- `--blas=none` — configures nothing; TierOff.
- `--default` adds NetCDF; `--full` adds MPI + CUDA checks.

`deps.json` at the repo root pins names/versions/URLs (the OpenBLAS tag
today; grows with setup). `../Blade-REPL/deps.json` is its sibling for
plotly/GR.

## OS-independence gaps being fixed on this branch

1. **MPI link line was Windows-spelled unconditionally** (`-lmsmpi`) — now
   `Platforms.mpiLinkFlag`; the mpiexec prober keeps its MS-MPI arms
   Windows-gated with a plain `mpiexec` fallback.
2. **The interpreter was Windows-pinned**: `src/Interp/Numerics.fs` libm
   shims hard-coded `DllImport("ucrtbase.dll")` — being replaced by a
   DllImportResolver mapping a logical `blade_libm` to `Platforms.libmName`.
   Byte-identity is a per-platform property: the identity claim is against
   the *local* g++ binary, which calls the same platform libm.

## CI as the proof (last)

`.github/workflows/dotnet.yml` is currently decorative (`dotnet test` on an
`Exe` project discovers nothing; it also watches a `main` branch that
doesn't exist — the default branch is `master`). Rework to ubuntu:
`apt install g++ libopenblas-dev` → `dotnet build` → `Blade test`. A green
Linux CI is the demonstration that packaging is OS-independent, and what
keeps gaps 1–2 fixed. Blocked on validating the suite on Linux at least
once (the interp byte-identity blocks compare against glibc libm there —
expected to hold per-platform, never yet run).

## Phasing

1. ~~Platforms.fs + Toolchain.fs + BLAS tiers~~ (landed, this branch)
2. ~~Portability fixes (MPI spelling~~ landed~~; libm resolver~~ in progress)
3. `blade doctor`
4. `blade setup` + deps.json growth
5. Blade-REPL vendor fetcher + tracking fixes (in progress, its repo)
6. Linux CI
