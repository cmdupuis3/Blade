#!/usr/bin/env bash
# Linux counterpart of build_gfortran.ps1: build_gfortran.sh <out> <flags...> -- <sources...>
# Adds -cpp -fno-external-blas, this directory to the include path, and qpc_linux.c (the Win32
# timer calls bench_support.f90 binds). The Windows build's 256 MB stack is a run-time rlimit here.
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
out="$1"; shift
flags=(); srcs=(); mode=flags
for a in "$@"; do
  if [[ $mode == flags && $a == -- ]]; then mode=srcs; continue; fi
  if [[ $mode == flags ]]; then flags+=("$a"); else srcs+=("$a"); fi
done
(( ${#srcs[@]} )) || { echo "build_gfortran: no sources after --" >&2; exit 2; }
gcc -O2 -c "$here/qpc_linux.c" -o "$out.qpc.o"
cmd=(gfortran "${flags[@]}" -cpp -fno-external-blas "-I$here" "${srcs[@]}" "$out.qpc.o" -o "$out")
echo "+ ${cmd[*]}" >&2
exec "${cmd[@]}"
