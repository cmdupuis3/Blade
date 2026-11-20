#!/usr/bin/env bash
# Regenerates src/CppReservedNames.txt: the identifiers the C++ translation unit
# Blade emits can see that a user spelling must not take (gap G252). Run from
# the repo root with the MSYS2 ucrt64 g++ first on PATH:
#     bash -o igncr tools/cpp-reserved-names/generate.sh
# (`-o igncr`: with core.autocrlf and no .gitattributes the checkout is CRLF,
# which MSYS2 bash otherwise refuses.)
# It writes a TU including every standard and runtime header the emitter can
# include (headers that do not preprocess on this box are skipped and listed),
# then reads (1) the macro table, `g++ -dM -E` (object-like and function-like
# kept apart), and (2) the namespace-scope declarations, `g++ -fdump-lang-raw`
# (functions and function templates of EVERY namespace, global included, for
# the ADL / overload set a user function name must avoid; types and variables
# of the global namespace and of nested_array_utilities, which the runtime
# hoists with a using-directive, for the names a user value must not shadow).
set -euo pipefail
root=$(pwd)
out="$root/src/CppReservedNames.txt"
work=$(mktemp -d)
cd "$work"
std="cstdint cstdlib cstddef cstdio cstring cerrno cmath complex functional tuple variant string iostream
     iomanip sstream fstream chrono algorithm numeric vector unordered_map unordered_set exception charconv
     type_traits memory limits filesystem omp.h"
rt="nested_array_utilities.hpp nested_array_types.hpp orbit_wreath_utilities.hpp rand_runtime.hpp
    blade_runtime.hpp blade_run_record.hpp linearized_storage.hpp blade_fold_lanes.hpp blade_orbit_scatter.hpp
    blade_packed_gemm.hpp blade_member.h blade_tilecache.hpp
    blade_memcheck.hpp blade_linalg.hpp blade_lapack.hpp cblas.h lapacke.h netcdf.h mpi.h"
# BLADE_HAS_BLAS / BLADE_HAS_LAPACK: the BLAS-routed programs' defines (blade_linalg.hpp refuses without).
# NOT covered: blade_private_spool.hpp and blade_external_sort.hpp (they include <windows.h>, whose ~27,000 macros only a granted
# spool program sees -- recorded in gap row G268) and the CUDA / MPI device headers.
flags=(-std=c++17 -fopenmp -DBLADE_HAS_BLAS -DBLADE_HAS_LAPACK "-I$root/src/cpp")
# The BLAS / LAPACK and NetCDF roots the emitter's own builds use (CLAUDE.md env table).
if [ -n "${OPENBLAS_DIR:-}" ]; then flags+=("-I$(cygpath -u "$OPENBLAS_DIR")/include"); fi
if [ -n "${NETCDF_DIR:-}" ]; then flags+=("-I$(cygpath -u "$NETCDF_DIR")/include"); fi
: > tu.cpp
skipped=""
for h in $std; do echo "#include <$h>" > one.cpp
  if g++ "${flags[@]}" -E one.cpp -o /dev/null 2>/dev/null; then echo "#include <$h>" >> tu.cpp; else skipped="$skipped $h"; fi; done
for h in $rt; do cp tu.cpp one.cpp; echo "#include \"$h\"" >> one.cpp
  if g++ "${flags[@]}" -fsyntax-only one.cpp 2>/dev/null; then echo "#include \"$h\"" >> tu.cpp; else skipped="$skipped $h"; fi; done
g++ "${flags[@]}" -dM -E tu.cpp > macros.txt
g++ "${flags[@]}" -fsyntax-only -fdump-lang-raw tu.cpp
# The emitter's own preamble defines (TIME, TIME_DIFF, ...), `NAME(` for a function-like one.
grep -ohE '"#define +[A-Za-z_][A-Za-z0-9_]*\(?' "$root"/src/*.fs "$root"/src/*/*.fs "$root"/src/*/*/*.fs 2>/dev/null \
  | sed -E 's/^"#define +//' | sort -u > emitter-macros.txt
"${PYTHON:-python}" -I "$root/tools/cpp-reserved-names/extract.py" macros.txt $(ls *.raw | head -n 1) \
  "$(g++ --version | head -n 1)" "$skipped" emitter-macros.txt > "$out"
cd "$root"; rm -rf "$work"
echo "wrote $out (skipped:$skipped)"
