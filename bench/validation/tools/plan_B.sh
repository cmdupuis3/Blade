#!/bin/sh
# blade plan for every group-B + control kernel at FULL size, thread pragmas enabled (the mt8
# build): which loops are threaded (methodology review M24) and, since the FP-default flip, the
# `fold form:` line per C++ fold site. Compile-time only.
# usage: sh bench/validation/tools/plan_B.sh [licensed|repro]   (default licensed = no FP knob;
#        repro = BLADE_FP_REPRO=1)
HERE=$(cd "$(dirname "$0")" && pwd)
REPO=$(cd "$HERE/../../.." && pwd)
K=$REPO/bench/kernels
B=$REPO/bin/Release/net10.0/Blade.exe
export BLADE_BLAS=0
unset BLADE_FP_REASSOC BLADE_FP_CONTRACT
if [ "${1:-licensed}" = "repro" ]; then export BLADE_FP_REPRO=1; else unset BLADE_FP_REPRO; fi
for k in fusedchain onepass stencil recur maskagg dot saxpy; do
  echo "== $k [${1:-licensed}]"
  "$B" plan "$K/$k/$k.blade" 2>&1 | grep -v "^plan:\|^inputs:"
done
