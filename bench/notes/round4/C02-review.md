# C02 orchestrator review

2026-10-22. Reviewed the complete Optimize.fs pass, unit witnesses, six corpus
fixtures and handoff on census checkpoint `c764b6dd`. Frozen handoff SHA256
`D107518B2649E51ECE2EA465A958E9B556AD182293C4480DFD7C2CA665F41884`.
Disposition: ready for sole-runner validation; not yet accepted as tested.

The initial runner verified all eight file hashes, then the Release build failed
on three FS0001 tuple-shape errors in the hidden-payload test-case list. No test
category ran. Root confirmed the list's inconsistent statement separators and
returned it for a test-only syntax correction with all witnesses preserved.
The production rewrite remains unvalidated; see [build evidence](C02-tests.md).

The first correction still failed because the inline list was parsed as tuple
application, with cascading module-type inference errors. Root reviewed the second
test-only correction: a separately bound `(string * IRModule) list` with explicit
semicolons preserves all ten witnesses. Latest handoff is
`184AF4594144F9074FEB997A41A7C515C51F50DC51F1992A437902B34345B4F8`;
Optimize.fs remains byte-identical to the originally reviewed production source.

The third build passed and optimize reached 560 passes with one failure: the
LLVM witness assumed three allocations with fusion disabled. Root read the
runner's complete ON/OFF LLVM modules. Both contain only main, one six-double
input allocation and one two-double result allocation. The OFF arm already
maps each input cell directly inside the row fold; ON does the same through
the newly explicit row view. The original census established the missing
materialization-elision route in C++, and checked only the explicit rowwise
LLVM spelling; it did not establish an LLVM baseline regression. Correct the
test to preserve that existing two-pool LLVM behavior, with a separately
demanded mapped-matrix control, rather than claim a new LLVM storage saving.
No production change is justified by this failed expectation.

The corrected witness subsequently passed: optimize 562/0, SQL-reduce 36/0,
interpreter 37/0 (36 SQL cases plus one provider check), and optimizer differential
36/0 with two explicit skips. Acceptance is nevertheless **held**: fixture 036's
actual LLVM ON and OFF executions both report `early_fold_failure` at source line
12, while C++ ON/OFF report the required `late_producer_failure` at line 8.
Root read all four logs. This exposes a pre-existing LLVM deferred-map evaluation
order issue even when C02 is disabled. It is not an acceptable fallback or a
passing refusal witness. C02-L1 is a separate source investigation into a narrow
backend admission/materialization repair; no production repair is authorized
until that cause and its semantic boundary have been reviewed.

The census established a specific missing route: one scalar map over a stored
matrix materializes a private matrix before partial reduction. The equivalent
explicit rowwise form already streams through IRReduceCompute in both emitters.
The rewrite uses that existing representation, retains the input and output,
constructs the concrete column axis from the source, and preserves the exact
consumer output type. It does not duplicate the existing two-vector outer
product rewrite or add backend-specific operations.

Admission requires a literal Float64 seed, reproducible serial policy, ordinary
static plain geometry, an immediate sole consumer and closed pure/total map and
fold call graphs. This permits moving evaluation into each row without moving
source-visible effects or failures. Named demanded producers retain storage.
The original fold argument order, seed and output row identity remain intact.

Review corrections incorporated before freeze:

- Subtract the matched expression only from the actual host ID. A second
  binding sharing that expression object remains a demand.
- Certify omitted Apply/MethodFor metadata explicitly, including carried axes,
  element types, roles, source IDs and structure state. Do not assume an
  expression-child walk covers metadata.
- Refuse additional host execution payloads even when they contain no reference
  to the private producer; rewriting the value alone cannot establish ownership
  of a side-table operation.

The witnesses check exact output/concrete-column metadata, actual C++ and LLVM
allocation differences, interpreter ON/OFF values, noncommutative order, empty
axes, cancellation, signed-zero seed and hidden demands. A paired-failure test
requires the later producer failure to precede an otherwise earlier row-fold
failure; the rewrite must decline. Source pins were derived independently.

Luna must verify the frozen hashes, build, run optimize and sql-reduce regression
blocks, and execute the six fixtures through both actual backends with fusion
ON/OFF. Successful default-policy category runs alone cannot prove this opt-in
route, so explicit repro emission/plan and runtime comparisons are required.
Record unsupported paths and internal skips, not just footer totals.

The modeled C++ saving is one private mapped `8*R*C` payload and row-pointer
metadata. LLVM already avoids that private pool in the baseline spelling.
Required M and the row result remain. Peak process memory, bandwidth
and timing are unmeasured. Combined checks with A01 remain pending; the new
pass runs before the later lifetime/scheduling planners.

## C02-L1 source disposition

The LLVM cause is established: named deferred maps remain AVirt in ArrSlots;
applyToArr obtains operands through emitArr and then passes lazy row views into
array-valued kernels. This bypasses the eager method_for input barrier specified
by formalism section 2.5 and implemented by the interpreter and C++ backend.

Approved a narrow repair for operands actually passed as array/row arguments:
classify from validated operand rank and loop peel count before emission, then
materializeExpr before the outer result allocation/seed/nest. Existing APool inputs
must remain no-copy, and named slots must upgrade once. Scalar inputs and direct
IRReduceCompute remain separate; virtual scalar range/reverse paths stay supported.
Malformed array-rank metadata must refuse explicitly. No new optimizer certificate
or blanket materialization of scalar map inputs is authorized.

Consequently the earlier two-pool LLVM OFF baseline was an accidental semantic
shortcut. Corrected pure C02 expectations are two pools ON and three OFF; demanded
producers retain three in both. This attribution change is contingent on L1 passing
paired-failure order in both settings plus shared-input/no-copy/seed-order controls.
C02 remains held until the actual repair and those results are reviewed.
Final L1 source freeze approved for build/optimize-first validation: handoff
`4459DFE7BF71761D642245415711CDEA4D489633CF4FD13B28427106407BFA15`.
Root verified all eight listed hashes; EmitLlvm is `C1E5B9C6...61D36FF3` and
OptimizeTests is `CB258195...6C6A95D`. The production pass remains byte-identical.
Actual implementation maps expanded virtual parameter spans, distinguishes scalar
axis rank from packed index-record rank, rejects negative metadata, and checks the
actual KArray choice after one operand evaluation. No context-set exception remains.

Root read new 037 and 038 and independently checked the shared-source totals
111 and 83. The shared test is private and has no prior print/keyed demand, so it
exercises named-slot forcing itself. Stored no-copy, packed row, multi-slot virtual,
scalar-route and malformed-metadata controls accompany the original failure witness.
The runner stops on the first build/optimize failure before later targeted gates.

Subsequent checkpoint review: the first build stopped on an offside test record
expression; syntax-only correction built. Optimize then passed580 and failed4
new control assertions: compact reduce is independently unsupported, while the
unannotated full rank-two fold uses the existing materialized rankKDesugar, not
a direct scalar IRReduceCompute. Neither finding justified changing L1 production.

Root approved the corrected control freeze, handoff
2BE90CED58EF46C8FF316406F52D42655ADC4E3D7D165F078191241EB2A21EF4.
EmitLlvm and Optimize hashes remain unchanged. Tests now distinguish genuine
rank-one IRReduceCompute from rank-two mapped-source/indexed IRForRange topology
before asserting their respective allocation counts. The packed row uses a
supported keyed read, with new039 independently pinning rows[1,7]. Existing
producer/fold/seed failure and private shared-input witnesses are unchanged.
Approved fresh build/optimize-only retry; later runtime gates remain pending.

## Final frozen native checkpoint

The corrected freeze builds and passes optimize 586/0, LLVM facts 107/0 and
golden components 23/0, 3/0, 107/0 and 19/0, without skips. All 36 native arms
(031–039 × fusion on/off × C++/direct LLVM) pass their independent values or
required failure sites. Root read the eight actual 036/037 errors and exit codes:
the producer frame wins at source lines 8 and 7 respectively; fold/seed failures
do not replace it.

Root inspected the actual LLVM primary loop: the six-cell stored M feeds each
three-cell scalar map/fold directly, with seed 7 per row, and only the two-cell
result is allocated beyond M. ON has two pools; repaired OFF has three. The
demanded 035 keeps M, P and rows in both settings, and its plan declines private
elision. The shared 038 has one complete P backing followed by two row-result
backings; it does not rematerialize P per consumer. Unsafe 036 completes its
producer loop before allocating/entering the row-fold result loop.

The positive saved plans corroborate admission, but raw emission is the authority
for arithmetic and storage. For example, 035's existing plan text mentions
`reassoc nsz` while its pinned repro LLVM fmul/fadd instructions have neither flag.
This text does not establish a reassociated runtime route.

Two runner matcher failures were caused by CRLF parsing, not execution. Saved
successful 038 artifacts were rechecked after the matcher correction, without
rerunning those programs; the original attempts remain identifiable. Existing
bounded row-argument controls and the affected category gates remain pending
before integration. No measured peak-memory or performance claim is made.

## Acceptance

Final categories pass: SQL 39/0/0, interpreter 40/0/0, optimizer-diff 39/0 with
two intentional rejection skips and 28 inert cases (11 changed cases compared).
The bounded LLVM controls have seven direct successful cases and one direct
expected abort; eight unsupported cases are explicitly retained as coverage gaps.
Root inspected their actual refusal messages and the line-17 abort in 485.

Accepted source commit `cd8eebbb`, integrated as `0c5c3e7d`. Automatic merges
preserve A01's scope events and ordinary scalar/array tuple-member call repair;
root inspected the combined LLVM difference against the tested C02 source.
Combined-source validation remains pending and will accompany A03's shared LLVM
work and the final integration gates. This closes C02's per-card source review and
targeted validation; it does not claim the entire round is validated.
