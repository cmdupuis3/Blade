# A03 design review

Root reviewed A03-census.md, SHA256
1E754EE59AC6A1CE9A77A31F6C24C6FC3E7120DCFFCB42F2D508B565B9AA1BCB,
against the accepted A02 emitted evidence. Design approved for the bounded
ordinary outlined array-return consumer, after C02-L1 is accepted and combined.
Implementation remains pending; this is not acceptance of a storage win.

The first positive is A02/030: reuse its already-planned dead local p backing
for the final return map after q is fully stored. Root requires the proposed
numeric callable/donor identity, exact current function scope ownership, one
tracking record, independent completed input backing and closed scalar kernel
proofs. A different SSA pointer alone does not prove independent storage.
Deferred inputs must decline without speculative forcing or duplicate rendering.
Caller-owned, released, wrong-scope, event-bearing and unsupported layout paths
remain fresh-allocation fallbacks. Backend admission must be reported separately
from the optimizer's shared plan.

The existing dense fill may be factored without changing arithmetic, traversal,
forcing or cleanup. Existing popPoolScope must spare the returned donor and free
q exactly once. No applyToArr or C02-L1 boundary changes are authorized by this
design. Actual emitted allocation/slot/free/return dataflow and targeted native,
interpreter and sanitizer evidence are required before integration.

This revises the initial A03 last-use-release pilot into the more precisely
evidenced LLVM return-donor gap. General let/tuple reuse, tuple-call cleanup and
C++ alias-copy retention remain separate work; none is declared complete here.
A02's 24 all-output native controls now pass. Its proposed broad category and
sanitizer follow-up is consolidated into this card's ownership implementation
validation; those unrun gates must not be represented as A02 passes.

## Source checkpoint reviewed

Root read the implementation and all focused ownership/refusal assertions, then
froze them on A's branch as `0ff2f3d3`. Content manifest SHA256
`1FD23043ACB4E4150AAA2FD53DDE63B546D99F3114EBEF4A2838265B15001A72`
and handoff `F7CABD4F1EA6875AE3EF107E0FFA40F8D0060CEFB9510A07C8ACCCA4EC740229`
matched the working files before commit. This is a source checkpoint, not a tested
integration or a measured memory result.

The preflight now explicitly rejects unit wrappers, virtual arrays, temporal and
dependent axes. It retains original logical producer metadata while reusing only
the already-planned physical backing. The common dense fill is unchanged; the
row-input forcing boundary and existing scope cleanup are untouched. Root asked
that opcode/flag sequence comparison stop claiming operand-graph identity; the
test and handoff now state that limit, and raw emitted graph review remains due.

Approve the staged sole-runner request: build, LLVM facts and optimizer checks
first; affected native/interpreter/LLVM categories, exact returned-root evidence,
failure-order controls and bounded sanitizer checks only after those pass.

## Raw LLVM ownership and arithmetic review

Primary gates passed (facts 128/0/0; optimize 608/0). Root independently read all
four complete ordinary-function bodies under A's `bench/.build/A03-runner/focused`.

| Arm | Owned roots / tracking slots | Final fill and cleanup |
| --- | --- | --- |
| 030 reuse1 | p `%r66/%r67`, q `%r107/%r108` | q finishes before the final loop reads q and writes p; q is freed via `%r138`; returns p |
| 030 reuse0 | same p/q, result `%r124/%r125` | final loop writes result; frees p/q via `%r140/%r141`; returns result |
| 032 reuse1 | p `%r128/%r129`, q `%r169/%r170` | final loop reads q and scalar mean `%r168`, writes p; frees q via `%r202`; returns p |
| 032 reuse0 | same p/q, result `%r186/%r187` | final loop writes result; frees p/q via `%r204/%r205`; returns result |

The original operand graph remains `(x*2)+1`, ascending serial sum divided by 7,
then subtraction of the saved mean. Final multiplication by 0.5 (plus the same
saved mean in 032) is unchanged. No reassociation/contract flag was introduced.
The returned donor's slot is not loaded for cleanup and its pointer is not freed;
each independent q is freed exactly once after the final loop. Main in 032 makes
two ordinary calls before reading their results. The backend Applied decision
names the actual donor backing/slot rather than only the shared planner result.

Raw `.ll` SHA256, in table order:
`7D63E25130F50844457DF8780A3B1D1CF01277C409DE30FC902A437925F4D719`,
`E971A66BE1A7DE1B4C395807680585909F045A199B75AB5498092E9AED8F7D11`,
`7B3B41020AFCC8EC264278732FDA915CF98E08B92D59938EF6155E00A68A9D51`,
`9E67E386822D745E697BA64CE8BF8942515D5107366B2950E701482C704B4E71`.
This establishes emitted buffer reuse and unchanged arithmetic, not measured
process capacity or speed. Native pins, affected controls and sanitizer gates
remain separate acceptance evidence.

Root also checked the emitted-only 027/028/029/031 controls from the same binary.
LLVM 027 and 028 still allocate each tuple-inlined stage (4 and 6 total allocation
calls including the input); 029 still has 5 including its input. C++ 029 explicitly
copies p8 into held9 with `copy_n`, frees only q13, and returns held9/r17; its
conservative retention is not repaired by A03. In 031 the shared planner still
declines for an inline lifetime region, with no donor entries. LLVM scratch `%r4`
is tracked in `%r5` and freed via `%r59` before caller allocation `%r70`. These
checks preserve known limits; they are not additional optimization successes.

## Final bounded sanitizer dispatch approved

Root reviewed A03-sanitizer-brief.md, SHA256
F0CF8EBF4E98F1252BFE5C0F7A959E609F21FDFB6196AD483B73BEF7153A72EF.
Approve four saved-IR LLVM diagnostic arms (030/032, reuse 0/1), four actual C++
memcheck arms, and the 031 lifetime boundary in both routes. The LLVM input copy
may differ only by adding sanitize_address to the exact #0 attribute line, with
reverse-byte equality and frozen hashes. A clean result counts only after actual
payload load/store checks and their object relocations are verified in the
returned function and caller reads. Original source/emission remains untouched.
This is diagnostic adapted-IR coverage; the ordinary LLVM CLI build does not
currently provide ASan instrumentation. Required main-owned arrays are not a
zero-live-byte criterion, and Windows LSan remains unsupported.
## Accepted implementation

Root reviewed the final 12-arm affected LLVM matrix and ten sanitizer results,
then independently inspected the 032 reuse1 instrumented function/caller and
native object. Its final q GEP r192 is checked by __asan_load8 before reading;
returned-p GEP r199 is checked by __asan_store8 before the overwrite. Main calls
the function twice before checked reads of both returned roots r18/r19. Native
object relocations at0x2c4 and0x30d precede the corresponding final-loop load and
store. Object SHA2567B9AC9ADAC180A4BD27ABFC393A90C5EE97F0228143EFA31307C1CC88AD05816
and executable2E513F8576739979EB978448FBF9144521A1AB8699E53E295B7ACE95D11CAA88
identify that diagnostic arm. Root read both 032 outputs and both 031 boundary
outputs; all required pins remain intact.

All five attribute-only input hashes match the approved static expectations.
The sanitizer manifest and command ledger bind the remaining arms. C++ reports
asan=1; the LLVM result is explicitly diagnostic adapted-IR coverage. Nonzero
outstanding main-owned arrays, unsupported Windows LSan and tiny cache-ineligible
payloads remain limitations, not failures or leak/retention claims.

Accept source0ff2f3d3 for integration: one already-planned dead owned backing is
used for the ordinary array return, q is freed once, and required caller inputs
and returned arrays survive. General LLVM let/tuple reuse and alias-copy retention
are not implemented by this card. Combined branch checks remain separate; no
full-suite launch or measured memory/performance result has occurred.