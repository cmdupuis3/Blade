#!/bin/sh
# Fold-form evidence (F11 / F15) for the group-B + control kernels: emits each FULL-size source
# (.cpp and, with BLADE_LLVM=1, .ll; no compile, no run) under both FP regimes and records
#   cpp: BLADE_OMP_SIMD_REDUCTION sites and 8-lane fold sites ("__rlane0 = __wrap"; one input
#        fold contributes 9 matches); ll: "fadd reassoc" among all fadd;
#   plan: every `fold form:` line `blade plan` prints (one per C++ fold site).
# usage: sh bench/validation/tools/foldform_B.sh <outdir>
# Regimes (after the FP-default flip): licensed = no FP knob (the default honours every
# licence in both lanes; the LLVM lane contracts by default); repro = BLADE_FP_REPRO=1.
set -u
HERE=$(cd "$(dirname "$0")" && pwd)
REPO=$(cd "$HERE/../../.." && pwd)
K=$REPO/bench/kernels
B=$REPO/bin/Release/net10.0/Blade.exe
out=$1
mkdir -p "$out"
export BLADE_BLAS=0 BLADE_OMP_THREADS=1
unset BLADE_FP_REASSOC BLADE_FP_CONTRACT
for k in fusedchain onepass stencil recur maskagg dot saxpy; do
  for tag in licensed repro; do
    if [ $tag = repro ]; then export BLADE_FP_REPRO=1; else unset BLADE_FP_REPRO; fi
    "$B" emit "$K/$k/$k.blade" --print checksum,input_checksum > "$out/$k.$tag.cpp" 2>&1
    BLADE_LLVM=1 "$B" emit "$K/$k/$k.blade" --print checksum,input_checksum > "$out/$k.$tag.ll" 2>&1
    "$B" plan "$K/$k/$k.blade" > "$out/$k.$tag.plan" 2>&1
    simd=$(grep -c "SIMD_REDUCTION(" "$out/$k.$tag.cpp")
    lanes=$(grep -o "__rlane0 = __wrap" "$out/$k.$tag.cpp" | wc -l)
    fr=$(grep -c "fadd reassoc" "$out/$k.$tag.ll")
    fa=$(grep -c "fadd " "$out/$k.$tag.ll")
    ref=$(grep -c "refused" "$out/$k.$tag.ll")
    echo "$k [$tag]: cpp SIMD_REDUCTION sites=$simd, 8-lane matches=$lanes | ll: fadd reassoc=$fr of $fa fadd, llvm refused=$ref"
    grep -i "fold form" "$out/$k.$tag.plan" | sed "s|$K/||; s/^/    plan: /"
  done
done
