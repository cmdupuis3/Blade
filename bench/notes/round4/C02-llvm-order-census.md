# C02-L1: LLVM partial-row input forcing boundary

Final approved design: materialize real inputs passed as whole arrays/rows
before the outer row allocation, seed and kernel nest. No new streaming
certificate or context set. The repaired reference has M + mapped input +
rows (three pools); C02's proven ON rewrite still has only M + rows (two).
This is a semantic prerequisite, independent of optimizer and FP gates.

## Baseline diagnosis and canonical contract

Runner evidence in `bench/.build/C02-runner/runtime-cpp` reports BL8013 at
corpus 036 line 8, `late_producer_failure`, for both fusion settings. LLVM's
`runtime-llvm/036...fusion-{0,on}-verbose.log` instead reports line 12,
`early_fold_failure`, for both. The saved LLVM nests producer(M[0,0]) and the
failing fold step before M[1,2], where the complete producer should fail.
C02 declines that candidate and leaves its private IRLet intact. This was an
existing LLVM failure-order bug, not a faulty C02 tree rewrite.

The original IR is a scoped IRLet of a rank-2 scalar map, followed immediately
by IRCompute of a method_for row apply over the private ID. The row kernel
directly IRReduces its row parameter with an explicit seed.

The boundary is semantic, not accidental C++ eagerness. Canonical
`docs/formalism.md:345`–`350` says ArrayExpr is unevaluated, but method_for
materializes an ArrayExpr input before loop construction. The interpreter
forces/memoizes the deferred name before the outer row nest
(`Interp/Loops.fs:968`, `1820`–`1840`, resolveArraySource); its IRLet itself
stores the deferred value and continues (`Interp/Core.fs:598`–`627`). C++
forces deferred input names before its apply nest
(`CodeGenBinding.fs:1411`–`1427`, `2958`–`2976`). G45's separate rule forces
WRITING named computations at their declaration; it does not make a
nonwriting may-fail map total or remove method_for's boundary.

Exact baseline LLVM call path:

- emitArr(IRLet), line 5171, calls bindLetIn -> bindLet -> bindNamed ->
  classifyValue. A bare apply is recorded as an AVirt producer in ArrSlots.
- applyToArr's old operand preparation at 6175–6182 uses emitArr directly.
  It bypasses forcing; ReadOnce is not this outer input decision.
- Its KArray branch at 6412 passes rowView(AVirt). rowView at 2398 onward
  composes the row coordinates with the original producer.
- bindKernelParam publishes the producer row; emitReduceCore consumes it;
  readCell at 2078 invokes the producer inside each fold step.

Thus materializing each row inside rowView or emitReduce is too late. Forcing
every bindNamed map would be too early and would change deferred semantics.

## Implemented boundary for review

The ordinary applyToArr real-operand preparation uses materializeExpr for
arguments that actually bind an array/row; scalar-peel operands retain emitArr.
Existing APool input is reused without copy/allocation. A producer is filled
completely before the consumer is constructed. materializeExpr updates the
actual named slot and retains existing forced-print, statement-hoist and
PoolScopes/keepPool behavior (5867–5922). It is invoked once, not after an
initial emitArr: array calls and extent computations must not evaluate twice.

The actual KArray criterion is peeled levels < operand scalar-axis Rank.
Preclassification uses raw operand type rank and levelsOf; emission verifies
that rank, then the actual KArray choice checks the same classification.
Negative ranks and inconsistent metadata refuse explicitly. Concrete parameter
record-count rank agrees with KernelInputRanks. These are distinct counts:
a packed SymIdx<2> parameter is one index record but two scalar axes.

Virtual sources remain virtual. As IRStorage's existing paramSpan/paramStart
mapping specifies, a range<R,C> operand contributes multiple scalar kernel
parameters; every virtual slot's rank must be zero. A malformed virtual-row
request refuses before loop construction. Scalar range/reverse, scalar-input
maps, direct scalar IRReduceCompute and existing special domain/ragged/compound
paths are not given a blanket materialization rule.

There is no new optimization proof. C02's existing closed pure/total map/fold
and full demand/metadata proof remains its one storage-elision licence. Its
rewritten outer input is stored M, with inner scalar IRReduceCompute. OFF now
correctly materializes the full mapped input. The old LLVM OFF two-pool evidence
remains historical evidence of the baseline route, not evidence of the repaired
reference. No preserving-unsafe-OFF exemption is needed.

Aliases/observers cannot waive the boundary. Repeated use of one named input
reuses its upgraded slot; existing copy-on-alias rules remain. Captures and
consumer writes see the complete stored input snapshot. Real operands prepare
in their original list order. A local pool is allocated in the containing
scope before rows; existing keepPool handling protects an outer slot upgraded
inside a nested scope. Unsupported materialization remains an explicit LLVM
refusal, never permission to stream. General IRIndex slicing outside an apply
and special source families remain separate audits.

Dedicated witnesses and exact runner request are in `C02-L1-handoff.md`.
No worker build, lower, emit, compile, execution or test was performed. Root
must review the frozen source before the sole Luna runner validates it.
