#!/bin/sh
# Smoke verification of the group-B + control kernels and their inputonly twins (tiny runs only):
# copies each source to <outdir> with the SIZE literals rewritten to the spec's smoke sizes, then
# runs `blade check`, the C++ lane serial (BLADE_OMP_THREADS=1), the C++ lane with 2 threads and
# the LLVM lane, all with --print checksum,input_checksum. Compare against
# bench/validation/pins-B.json (smoke) by eye or with the harness.
# usage: sh bench/validation/tools/smoke_B.sh <licensed|repro> <outdir> [kernel ...]
#   licensed = no FP knob (the default); repro = BLADE_FP_REPRO=1.
set -u
HERE=$(cd "$(dirname "$0")" && pwd)
REPO=$(cd "$HERE/../../.." && pwd)
K=$REPO/bench/kernels
B=$REPO/bin/Release/net10.0/Blade.exe
export PATH="/c/msys64/ucrt64/bin:$PATH"
export BLADE_BLAS=0 BLADE_EXE_CACHE=0
unset BLADE_FP_REASSOC BLADE_FP_CONTRACT
reg=$1
out=$2
shift 2
if [ "$reg" = "repro" ]; then export BLADE_FP_REPRO=1; else unset BLADE_FP_REPRO; fi
want=${*:-"fusedchain onepass stencil recur maskagg dot saxpy"}
out=$out/$reg
mkdir -p "$out"
for spec in fusedchain:n=20011 onepass:n=50021 stencil:ny=31,nx=41 recur:nt=11,nx=211 \
            maskagg:n=30011 dot:n=30011 saxpy:n=20011; do
  k=${spec%%:*}
  case " $want " in *" $k "*) ;; *) continue ;; esac
  sizes=${spec#*:}
  for f in $k inputonly; do
    [ -f "$K/$k/$f.blade" ] || continue
    stem=$k; [ $f = inputonly ] && stem=${k}_inputonly
    dst=$out/$stem.blade
    cp "$K/$k/$f.blade" "$dst"
    for kv in $(echo "$sizes" | tr ',' ' '); do
      p=${kv%%=*}; v=${kv#*=}
      sed -i -E "/\/\/ SIZE $p\$/ s/[0-9]+/$v/" "$dst"
    done
    echo "######## [$reg] $stem ($sizes)"
    "$B" check "$dst" 2>&1 | tail -1
    echo "-- cpp serial";  BLADE_OMP_THREADS=1 "$B" run "$dst" --print checksum,input_checksum 2>&1 | grep -v "completed in"
    echo "-- cpp 2 threads"; OMP_NUM_THREADS=2 "$B" run "$dst" --print checksum,input_checksum 2>&1 | grep -v "completed in"
    echo "-- llvm"; BLADE_LLVM=1 "$B" run "$dst" --print checksum,input_checksum 2>&1 | grep -v "completed in"
  done
done
