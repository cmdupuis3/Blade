# Fortran notes (fortran agent)

Owner: the fortran agent. Covers `bench/fortran/**`, every `natural.f90` / `expert.f90`, and
the decisions behind them. Everything below was measured on this host on 2026-10-17 unless
marked otherwise; probe sources are kept in `bench/fortran/*probe.f90` so every claim can be
re-run. Timings quoted here are informal single runs at reduced sizes, made only to choose
between code variants; they are NOT results.

## 1. Toolchain and versions

| compiler | version string |
|---|---|
| gfortran | `GNU Fortran (Rev4, Built by MSYS2 project) 16.2.0` (`C:\msys64\ucrt64\bin\gfortran.exe`); `-march=native` resolves to `znver3` (`-mavx2 -mfma`, no AVX-512) |
| ifx | `Intel(R) Fortran Compiler for applications running on Intel(R) 64, Version 2026.1.0 Build 20260617`; `/QxHost` emits AVX2 + FMA (ymm, `vfmadd*pd` seen), runs on the Ryzen (no Intel-only dispatch stub) |
| linker for ifx | MSVC from Visual Studio 2022 Community (vcvars64 banner "v17.0"), `link.exe` |

### The ifx environment recipe (what works)

`setvars.bat` from a script FAILED in the orchestrator's probe: its log shows
`'vswhere.exe' is not recognized` and `'vars.bat' is not recognized` for every component, so
ifx never reached PATH. Working recipe (in `bench/fortran/build_ifx.cmd`):

1. `set "PATH=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer;%PATH%"` on a line of its
   own (silences vcvars' vswhere complaint), then
   `call "C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvars64.bat"`
   (fallback: `C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools\...\vcvars64.bat`).
2. `set SETVARS_CALL=1` then `call "C:\Program Files (x86)\Intel\oneAPI\compiler\latest\env\vars.bat"`.
   Because `VSCMD_VER` is now defined, vars.bat skips its own Visual Studio discovery and goes
   straight to the Intel compiler setup (puts `compiler\latest\bin` on PATH).
3. `ifx /nologo <flags> ... /Fe:<out.exe>`.

Proved: `where ifx` succeeds after step 2; `ifx /QV` prints the version above; a `/Qopenmp`
program reports `omp_get_num_threads() = 8` under `OMP_NUM_THREADS=8` and 16 without it.
Environment setup costs ~2 s per build (vs ~10 s for setvars).

### Run-time DLLs

- ifx links the Fortran runtime statically; an OpenMP build imports only `libiomp5md.dll`.
  OUTSIDE the Intel environment the DLL search finds
  `C:\Program Files (x86)\Common Files\Intel\Shared Files\cpp\Bin\Intel64\libiomp5md.dll`
  (file version **20100603**, on the system PATH) or anaconda's copy, not the 2026.1 runtime
  (`compiler\latest\bin\libiomp5md.dll`, version 20260213). The wrapper therefore copies the
  2026.1 DLL beside the exe whenever a flag contains `openmp` other than `/Qopenmp-simd`
  (the application directory is searched first). Hash/record the copied DLL per ifx-mt8 exe.
- gfortran exes import `libgfortran-5.dll`, `libquadmath-0.dll`, `libgcc_s_seh-1.dll`,
  `libwinpthread-1.dll` (+ `libgomp-1.dll` with `-fopenmp`) from ucrt64: **run them with
  `C:\msys64\ucrt64\bin` first on PATH** (anaconda/Git carry other copies). Blade's g++ exes
  need the same PATH, so one run environment serves both.

### OpenMP thread count and placement (review M20) -- `bench/fortran/ompprobe.f90`

Run once per runtime with `OMP_NUM_THREADS=8 OMP_PLACES=cores OMP_PROC_BIND=close
OMP_DYNAMIC=FALSE`; each thread spins ~20 ms, then reports its OpenMP place and its logical
CPU (`GetCurrentProcessorNumber`):

| runtime | threads | places | placement observed |
|---|---|---|---|
| ifx 2026.1 `libiomp5md` | 8 | 8 (`cores`) | threads 0..7 on logical CPUs 1, 2, 4, 6, 9, 11, 13, 14: **one per physical core** (SMT pairs are (0,1), (2,3), ...) |
| gfortran 16.2 `libgomp` (MSYS2) | 8 | **0** | prints **`libgomp: Affinity not supported on this configuration`**: OMP_PLACES / OMP_PROC_BIND are IGNORED; threads float (sample: CPUs 6, 2, 8, 10, 3, 5, 0, 6 -- threads 0 and 7 on the same logical CPU, threads 1 and 4 on SMT siblings of one core) |

**Finding for the harness / method reviewer:** under libgomp on Windows the requested
binding does not happen. This hits the gfortran-mt8 arms AND Blade's C++-lane mt8 arms (same
MSYS2 libgomp), so those two are affected alike; the ifx-mt8 arms are pinned one per core.
Thread COUNT is right everywhere (8). If pinning matters, the harness could set a process
affinity mask of one logical CPU per core (0x5555) on the mt8 children of libgomp arms --
not done by me; harness decision.

Serial builds create no threads: the serial (`-fopenmp-simd` / `/Qopenmp-simd`) exes import
no OpenMP runtime at all (gfortran: no `libgomp-1.dll`; ifx: only `KERNEL32.dll`,
`imagehlp.dll`), checked on `ompsimdprobe` and on `gram/natural.f90`. `/Qparallel` is never
passed and ifx does not auto-parallelize without it (no runtime import is possible to use).

### ifx mt8 start-up jitter (coordinator item, 2026-10-17) -- `bench/fortran/ompstartprobe.f90`

Question: does OpenMP team start-up land inside the timed region? Yes -- the clock starts in
the program body and the first `!$omp parallel` (in every kernel: the input fill) creates
the thread pool. Measured (fresh processes, `OMP_NUM_THREADS=8`):

| runtime / environment | first region (team start) | later region |
|---|---|---|
| ifx, harness env (`OMP_PLACES=cores OMP_PROC_BIND=close OMP_DYNAMIC=FALSE`) | median 0.46 ms, p90 0.66, max 1.5-6.4 ms | ~0 ms |
| ifx, + `KMP_AFFINITY=disabled` | median 0.40 ms, max 0.54 ms | ~0 ms |
| gfortran (libgomp, no affinity on Windows) | median 0.85 ms, max 1.2 ms | 0.06 ms |

Team start-up itself (0.4-0.9 ms, inside the region in every OpenMP arm, Blade's C++ lane
included) is NOT the harness's 1-18 ms jitter. Reproduced the jitter on whole smoke-size
kernels (30 fresh runs each):

| exe (smoke) | harness env: min / med / p90 / max ms | + `KMP_AFFINITY=disabled` |
|---|---|---|
| dot expert ifx mt8 | 1.99 / 2.33 / 17.0 / 25.6 | 1.57 / 1.64 / 1.73 / 1.89 |
| recur expert ifx mt8 | 1.99 / 3.32 / 20.0 / 48.5 | 1.50 / 1.65 / 1.81 / 2.12 |
| saxpy natural ifx mt8 | 2.01 / 2.64 / 25.4 / 33.2 | 1.56 / 1.67 / 1.83 / 2.31 |
| dot expert gfortran mt8 | 0.92 / 1.04 / 1.17 / 1.37 | (libgomp ignores binding anyway) |

`KMP_BLOCKTIME=0`, `OMP_WAIT_POLICY=PASSIVE|ACTIVE`, `OMP_PROC_BIND=false` (without
`KMP_AFFINITY`), `OMP_PLACES=threads` or `spread` do NOT remove it (p90 4-24 ms). The cause is
libiomp5's thread BINDING on this host: binding a worker to a specific core makes it wait
for that core (my reading: Windows core parking on this laptop power plan -- a pinned thread
cannot run elsewhere while its core is parked/waking; not proven beyond the measurement).
The DLL copy beside the exe is not involved (the import is resolved before main).
**Recommendation:** run the ifx mt8 arms with `KMP_AFFINITY=disabled` (iomp then ignores
OMP_PLACES/OMP_PROC_BIND and prints `OMP: Warning #182 ... ignored` on STDERR, harmless; or
drop OMP_PLACES/OMP_PROC_BIND for those arms and set only `KMP_AFFINITY=disabled`). That
makes all three OpenMP runtimes UNBOUND (libgomp cannot bind on this platform anyway, so the
gfortran and Blade-cpp mt8 arms are already unbound): one threading condition for all mt8
arms. With it, ifx's 8 threads were spread by the OS over 8 distinct logical CPUs in the
probe (4, 12, 2, 14, 1, 5, 9, 8 -- not guaranteed one per physical core, same as libgomp).
Do not set `KMP_BLOCKTIME=0` (it makes every later parallel region pay a wake-up: warm
region 0.00 -> 0.04 ms, which recur's 600 regions would multiply).

## 2. The build wrappers (interface for the harness)

```
powershell -NoProfile -ExecutionPolicy Bypass -File bench\fortran\build_gfortran.ps1 <out.exe> <flags...> -- <kernel.f90>
cmd /c bench\fortran\build_ifx.cmd <out.exe> <flags...> -- <kernel.f90>
```

- Pass **only the kernel source** (`natural.f90` / `expert.f90`). Each kernel does
  `include 'bench_support.f90'`; the wrappers add `bench\fortran` to the include path. Do not
  also pass `bench_support.f90` (the module would be defined twice). Why include rather than
  link: a separately compiled module procedure is an opaque call per element without LTO/IPO
  (checked: with the include, neither compiler emits a call to `lcg` in the fill loop; both
  inline it). Inclusion does NOT suppress `bench_support.mod` (it is still written into the
  build cwd; review F14) -- per-arm build directories keep builds from colliding on it. ifx's
  object goes to `<out.exe>.obj` beside the exe.
- **Fixed additions, every arm, every regime** (echoed in the `+ ...` command line):
  - gfortran: `-cpp -fno-external-blas -fno-external-blas64 -I<bench\fortran> -Wl,--stack,268435456`
  - ifx: `/fpp /F268435456 /I<bench\fortran> /Fo:<out.exe>.obj`
  - `-cpp` / `/fpp`: every source is preprocessed so the twins and dumps build from the same
    file (section 5); no codegen change for a normal build.
  - `-fno-external-blas -fno-external-blas64` (reviews M22, F11): explicit, although they are
    gfortran's defaults.
  - 256 MB main-thread stack RESERVE: measured with `bench/fortran/tempprobe.f90` (an 8 MB
    array-expression temporary): ifx (any flags) and gfortran `-fopenmp` (which implies
    `-frecursive`) put array temporaries on the stack and crash on the 1-2 MB Windows default
    (`forrtl: severe (170) stack overflow` / `0xC00000FD`); serial gfortran puts them on the
    heap. The reserve is virtual (pages commit on touch) and keeps each compiler's DEFAULT
    temporary placement (no `/heap-arrays`, no `-fstack-arrays`). OpenMP worker threads keep
    their default stacks (`OMP_STACKSIZE` unset); no kernel creates a large temporary inside
    a parallel region (review M25).
- Both wrappers echo the exact compiler command line to stderr as `+ ...`. ifx wrapper:
  env-setup chatter goes to stderr; stdout carries only ifx's own messages.
- Exit code = the compiler's (ifx wrapper: 2 usage, 3 no vcvars, 4 ifx not found, 5 DLL copy
  failed).
- ifx wrapper parsing: flags are taken verbatim from the raw command line, so `=` survives
  (`-mllvm -fp-contract=fast` needs it; cmd's `%1` would split there). `<out.exe>` must be the
  first argument; the separator is the first ` -- `; no `!` characters anywhere. Paths with
  spaces work when quoted (tested). Anything after ` -- ` (sources, `.lib` files) is passed on.
- gfortran wrapper parsing: the arguments are read from the RAW process command line
  (`[Environment]::GetCommandLineArgs()` after the script's own path), not from `$args`:
  PowerShell's `-File` binder splits an argument like `-LC:\dir` at the colon, which broke
  `-L<path>` link flags (found building the blas arm). `--` separates flags from sources;
  `---` is a synonym; link arguments (`-L... -lopenblas`, `.lib` files) go after the
  sources.

## 3. Floating-point regimes and flags

Probes: `bench/fortran/fmaprobe.f90` (a*b - c with a = b = 1 + 2^-27 at run time; 0 unfused,
2^-54 = 5.55e-17 fused) and `bench/fortran/reassocprobe.f90` (1 + 4000 x 2^-54 summed left to
right is exactly 1; any reordering prints > 1). Both probe a vectorizable DO loop; the
reassoc probe also probes `SUM`.

| build | FMA contraction (loop) | reassociation (loop / SUM) |
|---|---|---|
| gfortran `-O3 -march=native` | yes | no / no |
| gfortran `-O3 -march=native -ffp-contract=fast` | yes | no / no |
| gfortran `-O3 -march=native -ffp-contract=off` | no | -- |
| gfortran `-O3 -march=native -ffast-math` | yes | **yes / yes** |
| gfortran `-O3 -march=native -ffp-contract=fast -fopenmp` | yes | no / no |
| ifx `/O3 /QxHost` (no /fp: the default is fast) | yes | **yes / yes** |
| ifx `/O3 /QxHost /fp:precise` | **NO** | no / no |
| ifx `/O3 /QxHost /fp:precise /Qfma` (either order) | **NO** -- /Qfma does not re-enable it | -- |
| ifx `/O3 /QxHost /fp:precise -mllvm -fp-contract=fast` | yes (asm: `vfmsub213pd`) | no / no |
| ifx same + `/Qopenmp` | yes | no / no |
| ifx `/O3 /QxHost /fp:fast` | yes | **yes / yes** |
| ifx `/fp:strict`, `/fp:consistent` | no | -- |

- ifx 2026.1's `/fp:precise` does NOT contract and `/Qfma` does not override it;
  `-mllvm -fp-contract=fast` hands contraction-only to the LLVM back end (reassociation stays
  off: reassoc probe). `/Qfp-contract`, `/clang:-ffp-contract=fast`, `/Qoption,...` are
  unknown to ifx (warning #10006). ifx with no `/fp:` flag is `/fp:fast`: never omit it.
- gfortran `-ffast-math` does NOT imply `-fno-protect-parens` (only `-Ofast` does); ifx's
  default is `/Qprotect-parens-`. No kernel relies on parentheses for accuracy.
- Serial builds carry the simd-only OpenMP flag (Blade's serial build keeps its `omp simd`
  pragmas): `-fopenmp-simd` / `/Qopenmp-simd` -- `!$omp simd` honoured, threading
  directives ignored, no runtime imported (`ompsimdprobe.f90`). Traps found: **gfortran
  `-fopenmp-simd` COMPILES `!$` conditional-compilation lines** (`!$ n = omp_get_num_threads()`
  -> undefined reference), so no kernel uses `!$` lines; and **ifx `/Qopenmp-simd` IGNORES a
  combined `!$omp parallel do simd`** (it honours only standalone `!$omp simd`; gfortran honours
  the simd part of the combined construct) -- hence the `_OPENMP` split in section 5.

### The regime axis (coordinator, 2026-10-17): `licensed` (headline), `repro`, `fastmath`

| regime | gfortran (harness flags) | ifx (harness flags) |
|---|---|---|
| serial x licensed | `-O3 -march=native -ffp-contract=fast -fopenmp-simd -DBENCH_LICENSED` | `/O3 /QxHost /fp:precise -mllvm -fp-contract=fast /Qopenmp-simd /DBENCH_LICENSED` |
| mt8 x licensed | `-O3 -march=native -ffp-contract=fast -fopenmp -DBENCH_LICENSED` | `/O3 /QxHost /fp:precise -mllvm -fp-contract=fast /Qopenmp /DBENCH_LICENSED` |
| serial x repro | `-O3 -march=native -ffp-contract=fast -fopenmp-simd` | `/O3 /QxHost /fp:precise -mllvm -fp-contract=fast /Qopenmp-simd` |
| mt8 x repro | `-O3 -march=native -ffp-contract=fast -fopenmp` | `/O3 /QxHost /fp:precise -mllvm -fp-contract=fast /Qopenmp` |
| serial x fastmath (optional) | `-O3 -march=native -ffast-math -fopenmp-simd` | `/O3 /QxHost /fp:fast /Qopenmp-simd` |
| mt8 x fastmath (optional) | `-O3 -march=native -ffast-math -fopenmp` | `/O3 /QxHost /fp:fast /Qopenmp` |

(The wrappers add `-cpp`/`/fpp`, include path, stack reserve, `-fno-external-blas`.)
`licensed` = `repro` + `BENCH_LICENSED`: only the EXPERT tier changes, and only on the fold
loops whose Blade counterpart carries a reassociation licence (section 6 lists each loop);
there a guarded `!$omp simd reduction(+:...)` (or `parallel do simd reduction` in mt8)
licenses the reordering. `repro` keeps every fold in order (one accumulator per sum,
ascending). Natural tier identical in every regime. An OpenMP `reduction(+:)` across threads
reorders a sum in every mt8 regime (allowed, README §3).

### Array temporaries and other codegen-relevant defaults (natural tier)

- Natural tier = each compiler's default at -O3: no `-fstack-arrays`, no `/heap-arrays`, no
  `/assume:` changes; `-frealloc-lhs` (gfortran) and `/assume:realloc_lhs` (ifx) left on.
- Every size is a compile-time constant (`integer, parameter :: n = ...  ! SIZE n`), the same
  information Blade gets from `Idx<N>`. All arrays are ALLOCATABLE, allocated after the clock
  starts (README 1.6). The retained program arrays are deallocated after the stop line
  (review M21); worker panels have their own lifetimes -- comoment3 expert's per-thread
  `bp` panels are freed INSIDE the parallel region, i.e. inside the timed region (eight
  small frees, recorded per review F13) -- and so do compiler-created temporaries.
- PARALLEL WORKSHARE is weak in both compilers: a reduction intrinsic (SUM, DOT_PRODUCT) in
  a workshare runs on one thread (gfortran emits `GOMP_single_start`; ifx warns #5474), and
  ifx also serializes some plain array statements (`s1/(1+abs(s1))`, the whole-grid stencil
  section statement, user-elemental calls). So where the natural tier must thread, it uses
  `!$omp parallel do` per pass/statement (kernels/README.md §1 allows it), keeping natural's
  structure (named stage arrays, one pass each). `dot/natural` keeps DOT_PRODUCT in a
  workshare: a serial kernel with parallel preparation (recorded below).

## 4. bench_support.f90

- `lcg(k, seed, lo, hi)`: generic (k integer(4) or integer(8)), `pure elemental`, exact in
  integer(8). The CURRENT generator ("D", five rounds, coordinator 2026-10-17):
  h1 = ((k + seed) * 1103515245 + 12345) mod 2^31; h2 = (h1 * (h1 div 4096 + 1)) mod 2^31;
  h3 = (h2 * 1103515245 + 12345) mod 2^31; h4 = (h3 * h3 div 65536) mod 2^31;
  h5 = (h4 * 1103515245 + 12345) mod 2^31; u = h5 * 2^-31; value = lo + (hi - lo) * u.
  `mod 2^31` is `iand(., 2^31 - 1)`, `div 4096` / `div 65536` are `ishft(., -12)` /
  `ishft(., -16)` (identical for the nonnegative operands); every product < 2^62.
- `lin2(i0, j0, n1) = i0*n1 + j0`, `lin3(i0, j0, l0, n1, n2) = (i0*n1 + j0)*n2 + l0`: 0-based
  MATHEMATICAL row-major indices, whatever the Fortran storage orientation.
- Timer: **QueryPerformanceCounter** via `bind(C)`, integer(8) count and frequency (10 MHz),
  not `system_clock` (kernels/README.md §3 still says system_clock; review M14 records QPC for
  every lane): measured `system_clock` granularity with integer(8) arguments is 1 count at
  rate 1e7 under gfortran, but under **ifx 2026.1 the rate reports 1e6 while the count only
  advances in steps of 1000 (1 ms granularity)**.
- Printing: `es24.16e3` then `trim(adjustl(...))`: 17 significant digits, e.g.
  `checksum = 3.4272198544854007E+000`, `selfcheck completed in 1.2345678901234567E-002s`.
- Dumps (`bench_dump_open(name)`, `bench_dump(v)`, `bench_dump_close()`): write
  `<name>_dump.txt` in the cwd, one value per line, same 17-digit format. Used only under
  `-DBENCH_DUMP`, after the stop line. Nothing in the module writes anything else to stdout.

### Cross-language generator check values

seed 10000019 (input j = 1), Python big-int reference = Fortran module:

| k | h1 | h2 | h3 | h4 | h5 | u |
|---|---|---|---|---|---|---|
| 0 | 126555856 | 1902599328 | 184246361 | 442304200 | **408748897** | 0.19033853756263852 |
| 1 | 1230071101 | 187734395 | 43451288 | 891526492 | **189020261** | 0.08801941806450486 |
| 2 | 186102698 | 1119064152 | 411006385 | 629615545 | **1945512958** | 0.9059500684961677 |

Hand check of k = 0: (0 + 10000019) * 1103515245 + 12345 = 11035173416802000, mod 2^31 =
126555856; 126555856 div 4096 = 30897, so h2 = (126555856 * 30898) mod 2^31 = 1902599328;
h3 = 184246361 (the old three-round output); h4 = (184246361^2 div 65536) mod 2^31 =
442304200; h5 = 408748897. (h5 matches the coordinator's reference.)

`bench/fortran/selfcheck.f90` (x(k) = lcg(k, 10000019, -1, 1), k = 0..9; input_checksum =
sum, checksum = sum of squares), identical under gfortran and ifx, = the Python reference and
the coordinator's D values (-3.1010268041864, 3.4272198544854):

```
selfcheck completed in <t>s
checksum = 3.4272198544854007E+000
input_checksum = -3.1010268041864038E+000
```

Blade prints 15 significant digits; compare at 15 digits. The Fortran arms print 17.

FMA hazard (still valid): `lo + (hi - lo) * u` is a multiply-add; it is contraction-proof
exactly when `hi - lo` has <= 22 significant bits. Every spec range ([0,1), [0,2)) is.

## 5. Build variants of every kernel source (preprocessor)

| define | effect | used by |
|---|---|---|
| `BENCH_INPUTONLY` | kernel and checksum skipped, `checksum = 0.0` printed; allocation, input generation and input_checksum unchanged (the twin of THAT tier, review M8) | harness inputonly arms (`-DBENCH_INPUTONLY` / `/DBENCH_INPUTONLY`) |
| `BENCH_DUMP` | after the stop line, the named output array is written to `<kernel>_dump.txt` in row-major LOGICAL order, every logical cell (packed symmetric / antisymmetric / simplex storage expanded, antisym signs applied); nothing changes in the timed region; stdout still has exactly 3 lines | `bench/validation` cell-by-cell comparison (review M11); not for timing |
| `BENCH_LICENSED` | expert only: the guarded `simd reduction` directives of section 6 | the licensed regime |

The licensed directives on a top-level fold loop are written
`#if defined(BENCH_LICENSED) && defined(_OPENMP)` -> `!$omp parallel do simd reduction(...)`,
`#elif defined(BENCH_LICENSED)` -> `!$omp simd reduction(...)` (serial build), `#else` the
plain `!$omp parallel do reduction(...)`, because ifx `/Qopenmp-simd` ignores the combined
construct (verified in the saxpy assembly: before the split the ifx licensed and repro
serial asm were identical; after it, a 2 x 4-lane `vfmadd` reduction). Dumps exist for gram,
comoment3, antisym, pairkernel, stencil, recur, maskagg, saxpy (fusedchain, onepass, dot have
scalar outputs only).

## 6. Per-kernel decisions (tiers, pragma placement M24, licensed loops)

Notation: "fill" = the input-generation loop, "ics" = the input_checksum computation. The
input_checksum is always summed in the ORDER of the Blade source (per-row sums, then rows;
two-array kernels as Blade combines them), serially in the final combination, so it agrees
with Blade to all 15 printed digits in every arm and regime.

### gram (G = X X^T, p x n)
- natural: `xs(n, p)`, `g = matmul(transpose(xs), xs)`, `sum(g*g)`. MATMUL routing (M22):
  gfortran calls libgfortran's `_gfortran_matmul_r8` (serial; `-finline-matmul-limit`
  default is 30, so it is never inlined at these sizes; `-fno-external-blas` explicit); ifx
  INLINES matmul (no runtime call at all; `/Qopt-matmul` is off by default -- passing
  `/Qopt-matmul` explicitly changes nothing without `/Qparallel`; only `_intel_fast_memcpy`
  appears, for the transpose). mt8: only the fill is threaded -- **parallel preparation,
  serial kernel** (neither compiler threads MATMUL). The dot_product form was rejected for
  natural: one latency-bound FMA chain per cell over 2 x 2.57 GF would take ~2.5 s+ serially.
- expert: upper triangle packed by columns (`gp(j*(j-1)/2 + i)`); X packed into 8-wide
  (rows i) and 6-wide (columns j) panels with the sample axis zero-padded to NBLK*KC
  (KC <= 256, a compile-time constant dividing the padded axis); an 8 x 6 register tile
  accumulated in outer-product order over s, vectorized over i: every cell is ONE sequential
  FMA chain over s (bitwise the same as an ascending dot loop) -- no reassociation needed in
  any regime, so no licensed directive (the README's `do s; do j; do i <= j` form, blocked).
  Sample axis blocked by KC (accumulators reloaded from G per block: order preserved).
  **licensed:** `simd reduction(+:coff)` on the checksum's packed-column read-back (review
  F5). Input sums (review F6): 8 adjacent variables together (the x(p, n) storage was
  walked at a 12.8 KB stride per variable), each row still ascending in s.
  `!$omp simd` (non-reduction) on the tile's i loop: without it ifx spilled the tile in the
  blocked version (~2.5x slower); gfortran unaffected. Checksum read back: diag + 2 x upper.
  mt8: `parallel do schedule(dynamic)` over column panels, largest first (kernel); fill,
  packing, checksum, per-row input sums are `parallel do`. gfortran keeps `tile` out of line
  (`tile.0`), ifx inlines it; both measured ~equal.
  Informal (p = 401, n = 1001, serial): natural ~28 ms gf / ~52 ms ifx; expert ~7.5 ms both.
- blas regime (review F5 applied: `simd reduction(+:coff)` on its checksum read-back under
  BENCH_LICENSED; no OpenMP directive around DSYRK; F12 confirmed UPLO/TRANS/LDA/LDC):
  `bench/kernels/gram/expert_blas.f90` -- the same inputs (`xs(n, p)`), then
  `DSYRK('U', 'T', p, n, 1, xs, n, 0, g, p)` through a `bind(C, name='dsyrk_')` interface
  (OpenBLAS 0.3.34 at `%OPENBLAS_DIR%`: exports `dsyrk_`/`dsyrk`/`DSYRK`, 32-bit integers --
  `OPENBLAS_USE64BITINT` undefined), checksum diag + 2 x upper, `-DBENCH_INPUTONLY` /
  `-DBENCH_DUMP` as the other sources. Link: gfortran `-L%OPENBLAS_DIR%\lib -lopenblas`
  (picks `libopenblas.dll.a`), ifx `%OPENBLAS_DIR%\lib\libopenblas.lib`, both after the
  source; both import `libopenblas.dll` (run with `%OPENBLAS_DIR%\bin` on PATH or the DLL
  copied beside the exe; `OPENBLAS_NUM_THREADS=1` serial, 8 mt8). Verified at smoke size,
  gfortran serial / mt8 / twin and ifx serial / mt8: checksum 2.3792471741304459E+05
  (natural 2.3792471741304470E+05, rel 5e-16), input_checksum identical.

### comoment3 (T(i,j,k) = (1/n) sum_s X(i,s)X(j,s)X(k,s))
- natural: `x(n, p)`, dense `t(p,p,p)`, `t(i,j,k) = sum(x(:,i)*x(:,j)*x(:,k)) / n` in
  column-major order (sample loop inside); mt8 `parallel do` on k. Informal p = 61: ~30 ms
  gf / ~42 ms ifx (full p = 171 extrapolates to ~0.7-0.9 s; not measured).
- expert: simplex i <= j <= k packed by k, then j, then i (`(k-1)k(k+1)/6 + (j-1)j/2 + i`);
  for each j the slab {i <= j} x {k >= j} is a GEMM-like product: for each 8-wide i panel the
  weighted panel W(i,s) = X(i,s) * X(j,s) is formed once and multiplied against raw X(k,s)
  6-wide panels (k >= j) by the gram micro-kernel; each cell one sequential FMA chain over
  ascending s with the CANONICAL grouping (Xi*Xj)*Xk, i <= j <= k -- exactly Blade's
  `a[s]*b[s]*c[s]`. **Review F1 (blocker, fixed):** the first version formed Y = Xj*Xk and
  accumulated Xi*(Xj*Xk), a one-ulp regrouping at some cells (the reviewer found (0,0,6));
  a dense natural arm still evaluates each permutation independently (permitted
  permutation-rounding difference), but the canonical cell is no longer regrouped. One
  division by n per cell (as Blade). mt8: `parallel do schedule(dynamic)` over j, each thread
  its own X(k) panel buffer (freed inside the region, F13) and W panel. **licensed:**
  `simd reduction(+:w)` on the checksum read-back (F5). Input sums: 8 adjacent variables
  together, each row ascending in s (F6).
- Cell count: C(173,3) = 173*172*171/6 = **848,046** packed cells (being corrected in the
  spec).

### antisym (L = X Y^T - Y X^T)
- natural: full p x p nest, `l(i,j) = dot_product(x(:,i), y(:,j)) - dot_product(x(:,j), y(:,i))`,
  dense; mt8 `parallel do` on j. (Two independent dot chains per cell; ~0.6 s at full size by
  extrapolation; the matmul form would be faster but the dot form is the spec's first
  spelling and the only one with an mt8 pragma.)
- expert: strict upper triangle packed (`(j-1)(j-2)/2 + i`); per tile the two products
  (X Y^T and Y X^T, the gram micro-kernel on X/Y panels) subtracted once per cell; no
  sample-axis blocking (n = 401: a panel pair is cache-resident). mt8 dynamic over column
  panels. Informal p = 301, n = 401: natural 54 ms gf / 24 ms ifx; expert ~3 ms.
  **licensed:** the strict-triangle checksum loop becomes `parallel do simd reduction(+:cs)` /
  `simd reduction(+:cs)` (review F5). Input sums: 8 adjacent particles together, each row
  ascending in s (F6). The two-MATMUL natural alternative is equally natural and not
  measured (review F9, recorded).

### pairkernel (Matern-5/2 kernel matrix, n points in 3-D)
- natural: x, y, z vectors; full n x n nest, every cell (diagonal included) evaluated in the
  spec's operation order; `sum(k*k)`; mt8 `parallel do` on j.
- expert: upper triangle packed; the column loop i = 1..j-1 is branch-free and unit stride;
  diagonal stored as the spec's exact 1.0 (not evaluated -- the formula gives exactly 1.0
  anyway); checksum fused per column (re-read from L1 right after the column is written).
  mt8 dynamic over columns. **licensed:** `!$omp simd reduction(+:w)` on the per-column
  checksum sum (blade-A: Blade's checksum fold is licensed).
- Vector math (M23): **both compilers call scalar `exp`** (gfortran: mingw/ucrt libm, no
  libmvec on Windows; ifx `/fp:precise`: scalar `exp` from its static libm -- the loop is
  NOT vectorized to SVML, even with `!$omp simd` on it, tried and dropped). gfortran and ifx
  gave bit-identical checksums at smoke size. Informal n = 701: natural ~6.3 ms, expert
  ~3.3 ms both compilers (exp-bound, ~13 ns per cell).

### fusedchain
- natural: three named stage arrays s1, s2, s3, one pass each, then the sum (4 passes);
  mt8 `parallel do` per pass + `reduction(+:)` (workshare would serialize two of the four
  statements in ifx and the SUM in both).
- expert: one fused loop, scalar stages, one accumulator. Tried and dropped: a strip-mined
  form (vectorized stage block into an L1 buffer, then the sequential sum) -- SLOWER with both
  compilers; blocking the chain FMA with `cs + (u3*u3)` -- no difference. ifx fuses this loop
  with the fill and input-sum loops by itself (the input array is still stored).
  **licensed:** the fused loop becomes `parallel do simd reduction(+:cs)` / `simd
  reduction(+:cs)` (Blade: `lambda(p, q) where omp -> p + q` on this fold); input sum `simd
  reduction(+:ics)` (F3). Compiler-generated fusion of fill + kernel + input sum by ifx is
  permitted by the revised protocol (F7: the SOURCE does not fuse generation; the inputs are
  stored -- see the store audit).

### onepass
- natural: four passes, one `parallel do reduction` loop each (powers written
  `((x*x)*x)*x` in both tiers: `x**4` is expanded `(x*x)**2`, a different rounding).
- expert: one loop, four accumulators, v2 = v*v, v3 = v2*v, v4 = v3*v. mt8:
  `parallel do reduction(+:s1,s2,s3,s4)` (Blade has no parallel form: this cell is the
  expected Fortran win). **licensed:** `parallel do simd reduction(+:s1,s2,s3,s4)` /
  `simd reduction(...)` (the builtin (+) fold of the `<&!>` join is reassociation-licensed in
  the Blade source -- settled by the architect/reviewer, F14). Input sum licensed too (F3).

### stencil
- orientation `f(nx, ny)` (a mathematical row contiguous) in both tiers.
- natural: one array-section statement per ROW (`lap(:,i) = f(2:nx-1,i) + ...`) under
  `parallel do` over rows -- the whole-grid statement in a WORKSHARE is serialized by ifx
  (#5474); checksum `sum(lap(:,i)**2)` per row, `parallel do reduction` over rows.
- expert: explicit row loop; each output row is a branch-free vectorized loop, then its
  squares are summed while the row is in L1 (stores to lap happen). mt8 `parallel do` over
  rows with `reduction(+:cs)`. **licensed:** `!$omp simd reduction(+:w)` on the per-row
  checksum sum (matches the recorded per-row `prodsum` licence, reviewer F9) and on the
  per-row input sums (F3).

### recur (RK4 trajectory, nt x nx)
- natural: inside `do t`, each stage its own pass into k1..k4, then the step; mt8 (review
  M5): one parallel region around the time loop with `!$omp do` (static) on each pass (the
  implicit barriers order the stages; each trajectory's arithmetic unchanged); checksum
  `parallel do reduction` over steps of per-step sums.
- expert: per step ONE fused loop (four scalar stages, no stage arrays, store u(x, t)),
  vectorized (`!$omp simd`, no reduction); then the step's squares summed while the row is
  hot, one accumulator per step (summing straight into one running total made ifx ~2x
  slower). **mt8 (review F4):** one parallel region; each thread OWNS a contiguous x-range
  (`omp_get_thread_num`/`omp_get_num_threads`) for every time step -- no per-step
  synchronization (the previous version ended 601 worksharing constructs); per-thread
  checksum combined once at exit (`reduction(+:cs)`). **licensed:** `simd reduction(+:w)` on
  the checksum sums and `simd reduction(+:ics)` on the input sum (F3). Tried and dropped
  (serial): x-blocks outermost with all steps inside -- 30-40% slower. Drift-control binary =
  gfortran serial build.
### maskagg
- natural: `y = pack(x, x > 1.0)` (allocatable, allocated by the assignment), `size`,
  `sum(y*y) + m`. Serial only, no OpenMP directives.
- expert: ONE branch-free compaction pass into a pre-sized y(n) (store at y(m+1) always,
  `m = m + merge(1, 0, x > 1)`), then a sequential sum over y(1:m). Measured faster than the
  branch-free fused-sum variant (which adds an exact 0.0 for rejected cells and doubles the
  add chain) and than the branchy `if` form (informal n = 1,000,003: 4.1 / 3.6 ms vs 6.8 /
  6.1 ms vs 6.0 / 6.2 ms gf / ifx). **licensed (review F2, blocker, fixed):** `simd
  reduction` on the output sum of squares and on the input sum -- the archived licensed
  Blade emission has 8-lane accumulators for both (the earlier "unlicensed" claim was
  wrong); the compaction stays sequential.

### dot
- natural: `dot_product(a, b)` in serial builds; **mt8 (review F8):** an explicit `parallel do
  reduction(+:cs)` loop under `#ifdef _OPENMP`, consistent with onepass/fusedchain natural (a
  DOT_PRODUCT inside PARALLEL WORKSHARE runs on ONE thread in both compilers: ifx warns #5474,
  gfortran serializes silently via GOMP_single -- the previous spelling, now replaced).
- expert: one loop, one accumulator; mt8 `parallel do reduction(+:cs)`. **licensed:**
  `parallel do simd reduction(+:cs)` / `simd reduction(+:cs)`; input sums `simd
  reduction(+:sa, sb)` (F3).

### saxpy
- natural: `y2 = 1.5*x + y` in a PARALLEL WORKSHARE (both compilers divide this plain
  assignment), checksum `parallel do reduction` loop.
- expert: one loop storing y2 and accumulating the checksum. **licensed:**
  `parallel do simd reduction(+:cs)` / `simd reduction(+:cs)` (verified in the full-size
  assembly: ifx 2 x 4-lane `vfmadd213pd` accumulators with the y2 store kept; gfortran a
  4-lane `vfmadd231pd` accumulator with the store kept). Input sums `simd reduction(+:sx,
  sy)` under licensed (F3).

### band (restored 2026-10-18: s = x^T B x over the band |i - j| <= w)
- natural (dense-predicate straw man, per spec): full n x n nest, `if (abs(i - j) <= w)`
  around the cell, s accumulated in lexicographic (i, j) order; mt8 `parallel do
  reduction(+:s)` on i.
- expert: clipped bounds `do j = max(1, i-w), min(n, i+w)`, per-cell phi exactly as the spec
  writes it, term `phi * x(i) * x(j)` left to right. repro: lexicographic order, one
  accumulator (mt8: `parallel do reduction(+:s)` on i). **licensed:** the spec's diagonal
  ordering `do k = -w, w; do i = max(1, 1+k), min(n, n+k)` with phi(k) computed once per
  diagonal (it is loop-invariant there), inner `!$omp simd reduction(+:sd)`, mt8 `parallel do
  schedule(static) reduction(+:s)` over the 2w+1 diagonals; input sum `simd reduction`.
  Alternative considered and NOT used: a phi(d) table in the repro row order (it would turn
  the per-cell division the spec makes part of the kernel into 2w+1 divisions -- a Toeplitz
  shortcut Blade's source does not spell; architect may decide otherwise).
- Informal n = 4001, w = 101 (0.8M cells), serial: expert repro ~0.9 ms, licensed ~0.2-0.25 ms
  (both compilers). Not results.
- Verified at smoke (n = 211, w = 5): both compilers x {natural, expert} x {repro, licensed}
  x {serial, mt8} + twins: contract ok, max relative checksum difference 7.0e-16, input
  identical; Blade (rebuilt binary) C++ 325.099853480187 (1.75e-15), LLVM 325.099853480188
  (1.4e-15), input 105.743473788723 15-digit equal. Scalar output: no dump; pins-B.json has
  no band entry yet.

## 7. Audits (review items)

- **M10 (stores survive), full-size builds, assembled only, never run:** for every arm with a
  named array output, the main program's production loop contains the output store
  (gram natural gf: inside libgfortran's matmul, then re-read by the checksum; every other
  arm: `vmovupd`/`vmovsd`/`vmovhpd` to the output array in the kernel loop, seen in
  `bench/.build`-independent scratch asm -- e.g. saxpy expert gf `vmovupd %ymm3,(%rdi,%rax)`
  next to the `vfmadd213pd`, ifx `vmovupd %ymm2,(%r13,%rbp,8)`; stencil, recur, pairkernel,
  comoment3 (`vdivpd` + store), antisym (`vsubpd` + store) likewise). Structural guarantee as
  well: every output is either re-read after its producing loop (checksum read-back) or, for
  saxpy/stencil/pairkernel/recur experts, stored in the same iteration that feeds the
  checksum. maskagg's compaction loop has no arithmetic (store + increment) and y is re-read
  by the sum. No arm is "checksum-only". (Review F13: source-level arguments are not a
  guarantee -- the machine-code store audit of each frozen build is.)
- **M21:** every source deallocates its arrays after the stop line (outside the region).
- **M22/M23/M20/M25:** sections 1, 2, 6 (matmul routing, vector math, placement, stacks).
- **M24 (pragma placement):** section 6 per kernel and tier; "parallel preparation, serial
  kernel" applies to gram-natural and dot-natural in mt8; maskagg has no mt8 arm.

## 8. Verification status (smoke sizes, 2026-10-17, generator D)

Every cell below: built with the wrapper, ran, exactly three contract lines, natural and
expert agree, and the Blade C++ and LLVM lanes (bin\Release\net10.0\Blade.exe,
`BLADE_BLAS=0`, serial) agree: input_checksum equal at 15 digits in EVERY arm, checksum
within the stated relative difference (largest over all Fortran arms and regimes; vs Blade
in parentheses). Regimes run per kernel: repro serial, licensed serial, repro mt8, licensed
mt8 (maskagg: serial only), each for natural, expert and both `-DBENCH_INPUTONLY` twins
(twins print checksum = 0 and the same input_checksum), with gfortran and ifx; dump builds
compared natural vs expert cell by cell (gfortran).

| kernel | smoke size | gf nat | gf exp | ifx nat | ifx exp | twins | dump nat vs exp (cells, worst rel) | max rel checksum, Fortran arms (vs Blade) |
|---|---|---|---|---|---|---|---|---|
| gram | p=61 n=31 | ok | ok | ok | ok | ok | 3721, 5.4e-16 | 4.9e-16 (1.2e-15) |
| comoment3 | p=13 n=11 | ok | ok | ok | ok | ok | 2197, 4.4e-16 | 2.5e-15 (1.7e-15) |
| antisym | p=23 n=11 | ok | ok | ok | ok | ok | 529, 0 | 1.0e-15 (0) |
| pairkernel | n=53 | ok | ok | ok | ok | ok | 2809, 0 | 6.5e-16 (1.5e-15) |
| fusedchain | n=20011 | ok | ok | ok | ok | ok | scalar | 1.7e-15 (1.5e-16) |
| onepass | n=50021 | ok | ok | ok | ok | ok | scalar | 2.5e-15 (2.7e-15) |
| stencil | ny=31 nx=41 | ok | ok | ok | ok | ok | 1131, 0 | 2.5e-16 (8.7e-16) |
| recur | nt=11 nx=211 | ok | ok | ok | ok | ok | 2321, 0 | 1.7e-16 (1.3e-15) |
| maskagg | n=30011 | ok | ok | ok | ok | ok | 15012, 0 | 0 (7.3e-16) |
| dot | n=30011 | ok | ok | ok | ok | ok | scalar | 1.7e-15 (6.1e-16) |
| saxpy | n=20011 | ok | ok | ok | ok | ok | 20011, 0 | 6.2e-15 (6.0e-16) |

Smoke reference values (gfortran natural, repro serial; 17 digits) -- they equal the Blade
pins under generator D: gram 2.3792471741304470E+05 / 9.5245630735345180E+02; comoment3
4.5007615475678720E+01 / 7.1523829593788830E+01; antisym 3.9060724195491300E+02 /
2.3951306393602863E+02; pairkernel 1.0518012539694084E+03 / 7.3678080412093550E+01;
fusedchain 4.6304649681954004E+01 / 1.0027016977054533E+04; onepass 1.1229114825406770E+06 /
5.0086334820955060E+04; stencil 1.8324204836448716E+03 / 6.3653345707757400E+02; recur
2.7297354831553360E+03 / 2.1148694757744670E+02; maskagg 5.0009275003391565E+04 /
3.0049359229411000E+04 (m = 15012 at smoke n = 30011 under generator D); dot
7.4424875710150345E+03 / 2.9915989193804096E+04; saxpy 3.6580347055559780E+04 /
1.9956494551840704E+04.

The LLVM lane refused maskagg (`a compound-index initializer; falling back to C++`), as
the spec expects.

## 8b. Codex Fortran review (bench/notes/review-fortran-codex.md (removed 2026-10-24; in git history at cf2028cc)): what was applied

| # | finding | applied |
|---|---|---|
| F1 | comoment3 expert regrouped Xi*(Xj*Xk) | FIXED as proposed: per i-panel W = Xi*Xj, multiplied against raw X(k) panels -> canonical (Xi*Xj)*Xk, ascending s, same micro-kernel. Informal p = 61, n = 201: ~0.9 ms (was ~0.8 ms) |
| F2 | maskagg licensed = repro was wrong | `simd reduction` on the output sum and the input sum under BENCH_LICENSED; compaction sequential |
| F3 | expert input-checksum folds serial under licensed | `simd reduction` under BENCH_LICENSED on the input sums of fusedchain, onepass, dot (sa, sb), saxpy (sx, sy), stencil (per row), recur. Grouping between inputs/rows kept |
| F4 | recur expert mt8: 601 worksharing constructs | each thread owns a contiguous x-range for all steps in one parallel region, checksum combined once (`reduction(+:cs)`); serial path unchanged. Informal nt = 101, nx = 20011, mt8: ~3.2-4.3 ms (gf/ifx) |
| F5 | contraction checksums scalar under licensed | `simd reduction` under BENCH_LICENSED: gram (coff), comoment3 (w), antisym (whole strict-triangle loop, `_OPENMP` split), expert_blas (coff) |
| F6 | gram input sums at a 12.8 KB stride | 8 adjacent variables summed together (each row ascending in s) in gram, comoment3 and antisym (x and y) |
| F7 | ifx fuses generation + kernel in fusedchain | protocol revision (ARCH): compiler fusion permitted, inputs must be stored; recorded in the fusedchain section |
| F8 | dot natural mt8 workshare serial | mt8 = explicit `parallel do reduction(+:cs)` under `_OPENMP`; serial keeps `dot_product` |
| F9, F10, F12 | notes | antisym two-MATMUL alternative recorded; tiles kept; DSYRK arguments confirmed |
| F11 | `-fno-external-blas64` missing | added to build_gfortran.ps1 (accepted by gfortran 16.2) |
| F13 | comoment3 panels freed before the stop | kept and RECORDED (source comment + section 3); blanket claim fixed |
| F14 | stale comments | onepass expert licence comment and bench_support `.mod` comment fixed; this file updated |

Input-checksum exactness under the licensed regime: every input value is a multiple of 2^-31
(range [0,1)) or 2^-30 ([0,2)), and every input sum in this suite stays below 2^22 / 2^23 at
FULL size, so all partial sums are EXACT doubles: the input_checksum is identical in every
summation order (licensed SIMD, thread reductions, Blade's 8-lane folds). Confirmed: every
licensed arm printed the repro input_checksum double at smoke size; a Python exact-rational
sum equals the sequential sum.

Re-verification after the patches (2026-10-17, smoke sizes, generator D): all 11 kernels x
{natural, expert} x {gfortran, ifx} x {repro, licensed} x {serial, mt8} (maskagg serial only)
plus each tier's `-DBENCH_INPUTONLY` twin: contract ok in every build; input_checksum equal
to the reference double in every arm; largest checksum relative difference across all
Fortran arms: gram 4.9e-16, comoment3 2.5e-15, antisym 8.7e-16, pairkernel 6.5e-16,
fusedchain 1.7e-15, onepass 2.5e-15, stencil 2.5e-16, recur 1.7e-16, maskagg 5.8e-16, dot
1.7e-15, saxpy 6.2e-15; Blade C++ and LLVM lanes agree (input 15-digit equal everywhere;
checksum within 2.7e-15); dumps natural vs expert cell by cell: gram 5.4e-16, comoment3
4.1e-16, every other kernel 0. Store audit (full size, compile-only) re-done for every
patched arm with array output (gram, comoment3, antisym, maskagg, recur, saxpy, stencil
experts, both compilers): the production store is present in each.

## 8c. Why ifx-expert's repro incrementals are small on dot / saxpy / onepass (audit 2026-10-18)

Claim checked: "ifx vectorizes its unlicensed reductions under /fp:precise". **False.**
Read-only audit of session `results/2026-10-18-LAPTOP-350H6LDB/session-130438`,
serial-repro-native: the archived build commands have no `BENCH_LICENSED`
(`/O3 /QxHost /fp:precise -mllvm -fp-contract=fast /fpp /Qopenmp-simd`; gfortran likewise
without `-DBENCH_LICENSED`). The archived sources were re-assembled with the archived flags
(`/S`), and the resulting loops were found verbatim in `objdump -d` of the archived exes. The
ifx exes contain NO packed ymm add/FMA reduction at all (the one packed `vaddpd` in onepass is
the fill's `2*u`).

Mechanism: **ifx FUSES the kernel loop with the input-checksum loop** (both only read the
stored inputs) into ONE pass, keeping every accumulator a single in-order dependent chain,
unrolled x8. Exe excerpt (dot, ifx-expert):

```
vfmadd231sd %xmm5,%xmm3,%xmm0    ; cs = fma(a_k, b_k, cs)   -- one chain, ascending k
vaddsd      %xmm3,%xmm2,%xmm2    ; sa = sa + a_k            -- one chain
vaddsd      %xmm5,%xmm1,%xmm1    ; sb = sb + b_k            -- one chain
vfmadd231sd %xmm6,%xmm4,%xmm0    ; next k ...
```

onepass: one pass with five scalar chains (s1, s2, s3, `vfmadd231sd` for s4, and ics).
saxpy: one pass storing y2 (`vmovsd %xmm7,-0x18(%rsi,%r15,1)`), cs as one `vfmadd231sd`
chain, and the two input sums SLP-packed as the lane pair [sx, sy] in one xmm
(`vunpcklpd` + `vaddpd`): each sum still in ascending order, so no reassociation. The input
fill is a SEPARATE loop whose stores are kept (vectorized 4-wide in 32-bit integer SIMD,
`vpmulld`: exact, because mod 2^31 needs only the low 32 bits). So this is not F7-style
generation fusion. The input-only twin runs the input-sum pass alone. The full arm does the
kernel in that same pass, and its extra chains overlap in the out-of-order core, so the
increment over the twin is small. gfortran-expert keeps separate passes (dot: a
`vmulpd` + in-order `vaddsd` chain loop, then a separate two-chain input-sum loop), hence its
larger increment. Not a flag defect: the regime flags hold (no reassociation in any repro
ifx exe); it is a compiler loop-fusion property, permitted by the revised protocol (F7: compiler
fusion allowed, sources may not fuse, inputs stored).

## 9. Open items

- libgomp ignores OMP_PLACES/OMP_PROC_BIND on this platform, and libiomp5's binding causes
  multi-ms jitter: recommend `KMP_AFFINITY=disabled` for ifx mt8 arms (section 1) --
  harness decision.
- comoment3 cell count typo (848,146 -> 848,046) in the spec and the coordinator's list.
