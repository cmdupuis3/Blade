#!/bin/sh
# usage: sh bench/validation/tools/plan_one.sh <kernel> [licensed|repro] [serial|mt]
# blade plan of one committed FULL-size kernel source (compile-time only), paths shortened.
HERE=$(cd "$(dirname "$0")" && pwd)
REPO=$(cd "$HERE/../../.." && pwd)
B=$REPO/bin/Release/net10.0/Blade.exe
export BLADE_BLAS=0
unset BLADE_FP_REASSOC BLADE_FP_CONTRACT
if [ "${2:-licensed}" = "repro" ]; then export BLADE_FP_REPRO=1; else unset BLADE_FP_REPRO; fi
if [ "${3:-mt}" = "serial" ]; then export BLADE_OMP_THREADS=1; else unset BLADE_OMP_THREADS; fi
"$B" plan "$REPO/bench/kernels/$1/$1.blade" 2>&1 | sed "s|$REPO/bench/kernels/||g; s|C:/Users/[^ ]*/bench/kernels/||g"
