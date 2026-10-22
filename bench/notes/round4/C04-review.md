# C04 topology-probe disposition

Root read the complete census, four sources and early-IR adapter. Approve only
the bounded census: four early-IR captures, four ordinary C++ emissions, four
actual LLVM emissions and four plans against one fresh combined compiler.
No native execution, category tests or production implementation in this job.
Default all-output demand stays enabled; strict serial/repro and contraction-off
settings preserve the ordered-column hypothesis.

Stop this card on the first primary topology mismatch, existing transpose
elision, target-idiom full-map allocation, wrong output index identity or backend
refusal. Preserve evidence rather than adapting sources to force admission.

If actual topology supports implementation, preserve optimizeModule's existing
signature/default behavior. The new arm must decline without explicit complete
foreign-census evidence; Lowering can supply an internal options-bearing entry.
Its existing empty-compound shortcut is not a complete reference proof. Do not
widen C02 or change its eager row-input materialization boundary.

Census SHA256: 51B3A5DBCC6E2B8E0EE6A60F84FBC3467273EAD455598680368706B2322866FA.
Adapter SHA256: CD60E130294C2EFD7783EB6F1044ECBF62BDACBDF400DB6B26B2173450E0975B.
Manifest SHA256: 61B4698383BBB504A7E35D705EB2FEDAF59D88E1E0B12D20C21FE4D56034686B.

## Observed topology and implementation scope

All sixteen sole-runner captures passed against combined source5e208a77 and
Blade.dll FE12B5CEC4CAFC311D393160835278EDC93828AFEDC2368F820F5CA0CCE172AD.
Root read the primary early IR: M4, host rows15, private transpose9, row callable17
and fold16. The consumer is the exact immediate row application; the row parameter
has a symbolic inferred extent. The output retains C under IRTotality.sameIndex
(its numeric IR ID differs), so a byte-identical index-record comparison is wrong.

Root read both primary allocation paths and the direct idiom's C++ body/LLVM
allocation and load graph. Primary LLVM allocates 6/6/3 double cells; the idiom
allocates 6/3 and reads source M at r*3+c, folding r in ascending order from7.
The C++ idiom independently uses M[r][c] with the same fold. No generated program
was run, and no runtime storage or speed result is claimed.

Approve implementation of this one anonymous rank-two transpose-to-partial-fold
arm, subject to the full proofs and default-decline census interface above.
Keep the scalar control and named transpose outside admission. Preserve source
R/C provenance, required M/rows prints, empty-axis and seed behavior, exact fold
association, full residual demands and failure order. Existing C02 stays unchanged.

## Implementation checkpoint and empty-axis investigation

Root reviewed and checkpointed the complete rewrite/tests at e1cca133. Two
subsequent test-only F# layout fixes (0f4b48cc,89b49014) preserve the production
and corpus bytes. The third build succeeds. Optimize reports 691 passes and two
failures: independent optimized R0/negative-zero seed pins pass, while exact
interpreter ON/OFF output/error/status comparison fails. No comparison is waived.
The native matrix remains unrun.

Root and C independently found a likely older interpreter defect:
arrayLitFromValues's empty literal fallback records only Extents=[0], even for
a declared [0;3] matrix. transposeArray then sees rank1 and rejects dimension1;
the transformed zero-row fold never needs that transpose. C prepares a bounded
ON/OFF diagnostic against the existing binary before any repair. This is a
source hypothesis, not yet an observed OFF result. A confirmed repair must
preserve declared empty rectangular shape and independent seed/negative-zero pins,
not weaken the optimizer comparison or hide the case behind a fallback.

The repaired diagnostic confirms that hypothesis. Root read both raw streams and
the complete OFF IRArrayLit payload: declared static [0;3] is present in the IR,
but OFF interpretation rejects the transpose with status125. Authorize C04-I1:
retain statically known empty rectangular dimensions in the interpreter, with
rank2/rank3 and unaffected rank1/nonempty/zero-inner controls. Preserve all
optimizer expectations and test both native lanes after the repaired gate passes.

## C04-I1 source review at 1331b178

Root read the narrow interpreter repair and all thirteen direct shape witnesses.
Only an empty outer literal with at least two static, plain, dependency-free,
nonnegative managed axes and leading extent zero takes allocDense with the exact
declared indices/extents. Existing other literal routes remain intact. Optimizer,
Lowering and eight corpus inputs are unchanged. Original exact interpreter twins
and negative-zero expectations remain required. Luna is rebuilding and running
optimize before native validation; no repaired-source pass is yet claimed.

Attempt4 passes all706 optimizer checks with no skips. Root read the raw C04
focused output, including all thirteen I1 constructor controls and unchanged
R0/negative-zero exact interpreter comparisons. The source/transpose/output
layout checks also pass. Authorize handoff steps2–3:32 actual C++/LLVM runtime
arms and selected emitted/plan dataflow witnesses. Final SQL/interpreter/opt-diff
categories remain gated on review of those results. This is not yet integration
acceptance or a measured memory result.

Root's raw 040 native-source audit: optimized LLVM has M=%r2 (6 doubles),
rows=%r9 (3 doubles). Outer c=%r14 visits0..2; inner r=%r22 visits0..1;
%r26*3+%r27 addresses M via %r30, whose load feeds accumulator*10+cell.
Seed7 is set once per column; no fast-math flags occur. Each completed fold
stores rows[c]; both M and rows have actual later print loads. OFF materializes
%r9 (6 doubles) with destination c*2+r, then folds its rows into %r33 using the
same seed/arithmetic/ascending-r order. The separate 6-cell transpose is the
only removed payload. Optimized C++ independently reads M[r][c] inside the
same serial fold and retains required M/rows print loops.

Raw LLVM ON SHA7311A6ABC1FE26F4516B04BD6B0570493CD0165BBF67867DB89C665C50B7ED45;
OFF B5D45C0DD3E22F9E07DF8CEE06DFA2F72F8CEF4A45AF30E5DA55A43D82451813;
C++ ON50984A603E7BC0F84F87371BDA34818570051E74E7AC7A98E3DBDFFEBC32118A.
These are saved native-build input artifacts; actual runtime matrix acceptance
and remaining demanded/failure-source review are still pending.

The32 unique native arms now pass (24 successful programs,8 expected BL8013
aborts), with full normalized stdout/error/status agreement across all four
backend/fusion arms per fixture. Root read the saved comparison and actual044
LLVM: T=%r9 is filled before M[0,0]=99; rows read T, not the mutated M, and
sample reads the retained T. Root read045's completed two-dimensional transpose
fill calling late_transpose_source before entering the fold, and046's retained
transpose followed by per-row seed call before the fold. Runtime errors match
those sites. LLVM hashes04407A0FA80EC3E98FD33CB4E4B81CF7D7641F16E9B129E499C767FA02DBD89180F,
045EE2BE6F57BA4046854A2F6E107C9FCD90297BB6D17EEA74F93C954BBE73E3BBE,
0463D0E2550C23A4DC9947EC5F26168B3DBAC9D3F95F34C40A276B5DB64FB587372.

The first045 attempt never launched because its existing executable path was263
characters; a short-temp retry passed. One041 capture-wrapper attempt failed
before recording an executable result; reliable redirected capture replaced it.
Both failed attempts remain evidence and are excluded from the32 unique results.
After requested emit/plan checks pass, authorize only the final sql-reduce,
interp sql-reduce and opt-diff sql-reduce categories before integration.

## Acceptance

Accept C04 plus its separately identified interpreter representation repair.
Final targeted categories pass: SQL47/0/0, interpreter48/0/0, optimization
difference47/0 with two intentional rejection skips and31 inert cases. Root
read the final logs;16 changed-emission comparisons pass. All requested native,
emission, metadata/census/refusal and empty-shape gates are complete. No further
C04-specific sweep is justified without a new failure or integration concern.
Modelled removal remains one private8RC transpose payload; peak/RSS/performance
are unmeasured. Integration and later combined-source checks remain distinct.
