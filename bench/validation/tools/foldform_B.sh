#!/bin/sh
# Fold-form evidence (F11 / F15) for the group-B + control kernels: emits each FULL-size source
# (.cpp and, with BLADE_LLVM=1, .ll; no compile, no run) under both FP settings and counts
#   cpp: BLADE_OMP_SIMD_REDUCTION sites and 8-lane fold sites ("__rlane0 = __wrap"; one input
#        fold contributes 9 matches), ll: "fadd reassoc" among all fadd.
# usage: sh bench/validation/tools/foldform_B.sh <outdir>
# The two settings are knob-unset and BLADE_FP_REASSOC=1. After the compiler's FP-default flip,
# replace them with knob-unset (licensed by default) and BLADE_FP_REPRO=1 (edit SETTINGS below).
set -u
HERE=$(cd "$(dirname "$0")" && pwd)
REPO=$(cd "$HERE/../../.." && pwd)
K=$REPO/bench/kernels
B=$REPO/bin/Release/net10.0/Blade.exe
out=$1
mkdir -p "$out"
export BLADE_BLAS=0 BLADE_OMP_THREADS=1
SETTINGS="unset:BLADE_FP_REASSOC REASSOC1:BLADE_FP_REASSOC=1"
for k in fusedchain onepass stencil recur maskagg dot saxpy; do
  for s in $SETTINGS; do
    tag=${s%%:*}
    kv=${s#*:}
    unset BLADE_FP_REASSOC BLADE_FP_REPRO
    case "$kv" in *=*) export "$kv" ;; esac
    "$B" emit "$K/$k/$k.blade" --print checksum,input_checksum > "$out/$k.$tag.cpp" 2>&1
    BLADE_LLVM=1 BLADE_FP_CONTRACT=fast "$B" emit "$K/$k/$k.blade" --print checksum,input_checksum > "$out/$k.$tag.ll" 2>&1
    simd=$(grep -c "SIMD_REDUCTION(" "$out/$k.$tag.cpp")
    lanes=$(grep -o "__rlane0 = __wrap" "$out/$k.$tag.cpp" | wc -l)
    fr=$(grep -c "fadd reassoc" "$out/$k.$tag.ll")
    fa=$(grep -c "fadd " "$out/$k.$tag.ll")
    ref=$(grep -c "refused" "$out/$k.$tag.ll")
    echo "$k [$tag]: cpp SIMD_REDUCTION sites=$simd, 8-lane matches=$lanes | ll: fadd reassoc=$fr of $fa fadd, llvm refused=$ref"
  done
done
