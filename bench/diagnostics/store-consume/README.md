# PM52 immediate store/consume diagnostic (B04)

**Validated bounded diagnostic; performance unmeasured.** Fixed n=2053, R5 seeds 10000019/20000038.
Round-4 B04 passed 27 validator fixtures and all eight numeric, provenance, native
store and post-stop observation gates. See the [results](../../notes/round4/B04-tests.md)
and [independent review](../../notes/round4/B04-review.md) for exact artifact identities.
The reviewed [contract](../../notes/round4/B04-contract.md) requires both distinct full
outputs p=1.5*x+y and q=p*p through observation after stop, plus checksum=sum(q) and
input_checksum=sum(x)+sum(y). Required payload: input 32848 bytes, output 32848 bytes.
These are payload models, not allocator capacity or measured traffic.

The single Blade source uses implicit lifted arithmetic with explicit compute, with
module roots printed using `--print p,q,checksum,input_checksum`. Both Fortran tiers
always dump every p cell to `pm52_p_dump.txt` and q cell to `pm52_q_dump.txt` after stop,
before deallocation; no BENCH_DUMP/INPUTONLY alternative build exists. The natural tier
assigns p then q then reduces. Expert forwards v into v*v while writing both outputs.
Compilers may change passes; optimized executable audits establish actual behavior.

The exact same executable must provide its complete observed output and store/lifetime
audit. Blade module-root ownership may persist to process exit; report that as process
reclamation rather than an explicit pool release. A failed store or endpoint audit is
a failed diagnostic contract, even with correct numbers. No buffer identity registry,
runtime/header/compiler/harness edit or opaque observer is introduced.

Initial matrix is **eight arms maximum**, one fresh native process each:
Blade C++/LLVM and gfortran natural/expert, each default licensed and reproducible
contract-off serial. No ifx/mt/BLAS/large sizes/twins/retention-policy variants. Licensed
fold association is backend-dependent; reproducible uses the ascending serial chain
and contraction off. Printed precision prevents a bit-identity claim from text output.

The independent stdlib `reference.py` performs scalar exact-integer R5 and separately
rounded binary64 v then v*v. It provides ascending binary64 sums and math.fsum truth
as distinct references; it neither imports the production generator nor executes any
Blade/native program. All generated numerical data belong to Luna's absolute scratch
directory; no generated pins are committed.

`validate.py` reads saved output only. Every cell and count of both arrays and both
scalar checksums must pass (tolerance 1e-12*max(1,abs(reference))). It independently
checks a successful run record bound to executable/output hashes. `audit-template.json`
starts with absent claims; copy identities from the initial validation JSON and fill
each audit only after inspecting that exact executable and annotated artifacts. Give
absolute artifact paths, raw SHA-256 and reviewer. The validator checks identities and
artifact hashes; the content/assembly judgments require Luna and final root review.

Store and endpoint gates remain unmeasured without the audit. A synthetic manually
labelled pass is not an independently established fact; fixtures use fictional hashes
only to exercise acceptance/refusal behavior. No missing measurement becomes zero.
Metrics such as DRAM traffic/whole-process allocation capacity/retention stay null.

Validator exits: 0 all diagnostic gates pass; 1 a failed numeric/provenance/store/endpoint
gate; 2 malformed invocation/input/evidence; 3 numerical/provenance pass with unmeasured
store or endpoint gate. `performance_eligible` is always false. The parser checks observed
stop-line ordering, but only an executable audit establishes real timestamp/load order.

`test_validate.py` contains authored pure positive/refusal fixtures: exact three-cell
math, fixed R5 policy, serial-vs-fsum association, full counts/interior/tail errors,
swapped outputs, nonfinite values, malformed/order/duplicate output, missing audits,
checksum-only/early-free claims, mismatched executable/source/output/regime hashes and
failed native completion. They launch no native subprocesses.

This does not enter the benchmark harness. Its printed kernel time is **not a result**;
full-array formatting and teardown affect whole-process time/peaks. Same-binary opaque
post-stop observation for eventual timed admission remains a separate reviewed gate.
Distant/no reuse are separate unimplemented contracts. No performance ratio or bandwidth
claim follows from these runs. B03 remains separate source-tree C++ pool API accounting;
it is not LLVM/provider/process allocation instrumentation.

Sole Luna's exact request and frozen identities: [B04-handoff.md](../../notes/round4/B04-handoff.md).
