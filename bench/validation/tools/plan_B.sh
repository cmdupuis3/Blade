#!/bin/sh
# blade plan for every group-B + control kernel at FULL size, thread pragmas enabled (the mt8
# build): which loops are threaded (methodology review M24). Compile-time only.
# usage: sh bench/validation/tools/plan_B.sh
HERE=$(cd "$(dirname "$0")" && pwd)
REPO=$(cd "$HERE/../../.." && pwd)
K=$REPO/bench/kernels
B=$REPO/bin/Release/net10.0/Blade.exe
export BLADE_BLAS=0
for k in fusedchain onepass stencil recur maskagg dot saxpy; do
  echo "== $k"
  "$B" plan "$K/$k/$k.blade" 2>&1 | grep -v "^plan:\|^inputs:"
done
