#!/bin/sh
# Store / vectorization audit of the group-B + control Blade kernels (methodology review M10, M8).
# Builds each kernel's FULL-size executable in both lanes -- COMPILE ONLY, never runs it --
# disassembles it, and prints a loop summary of the user functions (bench/validation/tools/asmfn.py).
#
# usage: sh bench/validation/tools/audit_B.sh <licensed|repro> <outdir> [kernel ...]
#   licensed = BLADE_FP_REASSOC=1 (LLVM also BLADE_FP_CONTRACT=fast); repro = knob unset.
#   (After the compiler's FP-default flip, "repro" must set BLADE_FP_REPRO=1 instead; edit the
#   two export lines below accordingly.)
# Serial build (BLADE_OMP_THREADS=1), BLADE_BLAS=0, BLADE_EXE_CACHE=0. Needs MSYS2 ucrt64 (g++,
# objdump) and clang64 (clang). Python: C:\Python314\python.exe.
set -u
HERE=$(cd "$(dirname "$0")" && pwd)
REPO=$(cd "$HERE/../../.." && pwd)
K=$REPO/bench/kernels
B=$REPO/bin/Release/net10.0/Blade.exe
PY=/c/Python314/python.exe
export PATH="/c/msys64/ucrt64/bin:$PATH"
reg=$1
out=$2
shift 2
kernels=${*:-"fusedchain onepass stencil recur maskagg dot saxpy"}
export BLADE_BLAS=0 BLADE_OMP_THREADS=1 BLADE_EXE_CACHE=0
if [ "$reg" = "licensed" ]; then export BLADE_FP_REASSOC=1; else unset BLADE_FP_REASSOC; fi
for k in $kernels; do
  for lane in cpp llvm; do
    d=$out/$reg/$lane
    mkdir -p "$d"
    cp "$K/$k/$k.blade" "$d/$k.blade"
    rm -f "$d/$k.exe"
    if [ $lane = llvm ]; then
      BLADE_LLVM=1 BLADE_LLVM_CLANG=C:/msys64/clang64/bin/clang.exe BLADE_FP_CONTRACT=fast \
        "$B" compile "$d/$k.blade" --print checksum,input_checksum > "$d/$k.build.log" 2>&1
    else
      "$B" compile "$d/$k.blade" --print checksum,input_checksum > "$d/$k.build.log" 2>&1
    fi
    if [ ! -f "$d/$k.exe" ]; then echo "$reg/$lane/$k: BUILD FAILED"; tail -3 "$d/$k.build.log"; continue; fi
    objdump -d --no-show-raw-insn -C "$d/$k.exe" > "$d/$k.s"
    echo "######## $reg/$lane/$k ($(grep -c 'llvm lane refused' "$d/$k.build.log") llvm refusal)"
    if [ $lane = cpp ]; then fns="<kernel( <inputs( <step( <rhs( <main>"; else fns="<main>"; fi
    for fn in $fns; do
      "$PY" -I "$HERE/asmfn.py" "$d/$k.s" "$fn" 2>/dev/null | grep -v "ostream\|panic" | cut -c1-240
    done
  done
done
