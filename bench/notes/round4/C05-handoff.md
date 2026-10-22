# C05 handoff: one certified sparse wildcard scalar terminal

Source checkpoint for root review on base `63f378e04797ecd197be79f9a9576c9f18a3a30d`.
The coder performed source edits/inspection and static diff/hash checks only.
No build, lowering, emission, interpreter, native execution, test, oracle or
benchmark was performed by the coder. Luna alone validates the frozen source.
Full-suite launches remain 0. Existing `.r4tmp` is preserved.

## Evidence and exact scope

Root reviewed the completed C05 census's six early/final IR pairs, six C++
emissions and six plans before authorizing implementation. Actual primary:
host9, parent4, SparseInits[4]=Var2; host is a direct IRReduce of the anonymous
IRIndex(S,tuple[unit,1]), fold10, seed7. The residual's actual type is obtained
by `IR.typeOf operand` and printed explicitly by the new certificate witness:
plain rank1 Float64, dynamic IRCompoundProject(Var4,1) extent. It is not the
maximum free coordinate or a sorted coordinate space. Source let flags are
IsMutable=true despite no writes; fold10 has Effects.Unknown=true despite its
closed body `(v7*10)+v8`. Admission uses actual writes and structural totality.

Saved baseline C++ creates the sparse index, validates values/key length,
copies the parent compact values, gathers once into total__gath, seeds7 and
folds the residual, then calls its matched deallocate_gather_dense. This is
the existing bind-once/leak repair, not storage elision. Source/evidence links
are in C05-census.md and integration C05-review.md; raw artifacts remain in
`bench/.build/C05-runner-63f378e0`. None is new implementation evidence.

This checkpoint admits only one ordinary module-level scalar Float64 fold,
direct anonymous rank2 sparse partial, one nonnegative literal pin and one
free axis, literal Float64 seed, and closed total serial/repro fold/callees.
Parent is a dominating known SparseInits constructor from a dominating stored
plain rank1 Float64 literal and static distinct nonnegative rank2 keys. Values
count/declared extent/key count must agree; there is no fixture-size constant.
No trailing payload, units, structured cells, runtime key source, deferred or
aliased source constructor, named residual, unseeded fold, function/expression
consumer, generic sparse algorithm, new representation or new knob is added.

## Implementation

Optimize.certifySparseWildcardFold is a pure proof query returning only parent
ID, pin axis/value, original callable and original seed. It does not rewrite
IR, broaden storedArrayProof, mutate planner state or introduce a plan table.
It reads the existing owner facts and per-call FUSION/repro gates. Whole-program
evidence is mandatory. It matches the actual unique ordinary host ID/name/type
and Value object and the actual operand/kernel occurrences. Structurally equal
synthetic operands or another binding sharing the expression get no exemption.

The proof audits complete compactionRoots over the merged program: all binding
values and side execution operands, declared/embedded types, every callable's
body/parameter/return/capture types including UNUSED callables, source/name
references and captures. The certified operand's entire coordinate/type shape
is checked before exempting that occurrence at its actual host. Other residual
partials/projections, aliases, captures, shape/key/type observers and unknown
side payloads decline. Only the single known SparseInits payload is allowed;
the unit placeholder is not mistaken for its execution. All-program writes,
unknown callees and lifetime events in payloads AND embedded type extents
decline. This is deliberately conservative even for unrelated writers.

Shared structural IRTotality plus a closed transitive callable audit rejects
captures/globals/callbacks/recursion, unresolved calls, non-scalar signatures,
malformed parameter identities, failing casts/division/intrinsics, resource
events and parallel/device/static/arity strategies. Conservative Effects.Unknown
is not trusted as pure or used alone as a refusal. Root scalar argument order
and all source callable metadata remain unchanged.

Existing hot-row/cell/rec-step/epilogue/compaction/compact-row/membership and pool
donor overlaps involving host/parent/values decline. Pool return-donor keys AND
values are audited, using the identical read-only returnDonors accessor already
authored by A04/D08. Root owns duplicate-accessor merge resolution. Types.fs is
the only extra shared file authorized during review; no fsproj change is needed.

One genReduceBinding arm precedes GatherTeardown. The existing complete merged
module view in ctx.StreamTerminalModule supplies evidence; its comment now
describes both certified terminal uses. Provider admission is unchanged. The
arm also requires module scope, a named non-deferred parent, and no tile/read/
streamed context overlap. Low-level genModule callers have None and keep the
old allocating path.

The arm emits the original fold wrapper, seed exactly once, then ascending
`r < parent.idx->cardinality`. Only a matching `rank_to_tuple[r][pinAxis]`
invokes `fold(acc,parent.data[r])`; a false match does nothing. There is no
sort, hash iteration, numeric-zero substitute, reassociation, SIMD or OMP form.
No match returns the exact seed, including -0.0. Parent construction, validation,
copy and registered ownership remain at their existing site. Ordinary main's
baseline retains S to process exit without an explicit S free; assertions compare
that same lifetime behavior, rather than inventing a new parent deallocation.
The scalar host ID/name/type/prints and later reuse are retained.

IR/interpreter, shared optimizer passes, C++ runtime gather helpers and LLVM
are unchanged. Named, escaping and unsupported consumers retain the existing
producer-shaped gather/teardown. LLVM still refuses sparse initialization;
CLI fallback to C++ is not LLVM support. This is a C++ storage route only.

## Authored verification and independent pins

Focused OptimizeTests additions include:

- Emitted route versus optimizer OFF and demanded control: marker plus exact
  absence/presence of one allocating gather call, matched residual free,
  retained parent allocation/wrapper/BL8001 guard/prints, ascending supplied
  key ordinal, branch-skipped fold and exact original seed/argument order.
- Actual IR/certificate metadata, mutable source lets and stale unknown effect
  summary, default low-level/missing complete-program refusal, exact object
  identity and per-call optimizer/repro gates.
- Twenty-six synthetic refusal modules: reference-equal other host; source
  alias/capture; unused callable param/return extents; declared residual extent;
  hidden type writer; side execution tables; real writer/lifetime event;
  unknown/failing/capturing/parallel fold; missing/nonliteral seed; nonliteral/
  negative pin; wrong kernel type; duplicate/negative/runtime keys; trailing/
  structured payload and unknown values initializer. Separate donor witness
  covers direct and return-donor key/value overlap and restores all table state.
- Ten canonical interpreter + actual C++ route cases with independent pins:
  ordered7423 (sorting would7324), cardinality3 (no hardcoded5), all749283,
  no-match -0.0, parent read4 + scalar reuse14846, total transitive/block helper,
  named demand, function-expression fallback, private snapshot before parent
  mutation (total7423/after99), and late parent failure before early fold failure.

These are **authored, unexecuted assertions**, not results. The existing corpus
six census source bodies/pins are copied into sql-reduce/048-053 without oracle
generation: private ordered, demanded residual, private snapshot mutation,
paired parent/fold failure, no-match signed zero, all matches. Original C04 and
all earlier source/tests/corpus remain unchanged.

Modeled removable storage for this rank1 residual: vector<size_t> ranks capacity,
double[max(matches,1)] copy and one size_t extent. No residual key/hash table
exists at rank1, so none is claimed removed. Original vals and sparse parent
key/hash/compact storage stay required. No whole-process peak/RSS, allocator
coverage, timing, bandwidth or native memory saving is measured here.

## First build failure and layout-only repair

Root committed the original source checkpoint as `4c3c4601`. Luna's first
Release build stopped before any optimizer tests: FS3118/FS0010/FS3156 at
Optimize.fs8417-8418, with a cascading incomplete-construct error at8588.
The `plain` helper began its boolean body on the binding line but placed its
continuation left of that body's start. The repair moves the entire body onto
two consistently indented following lines; operands, operators and scope are
unchanged. Nearby multiline bindings in this certificate were inspected for
the same layout pattern; no other edit was made. Tests and corpus are unchanged.

Failure evidence remains at
`bench/.build/C05-implementation-runner/attempt1/build.stdout.log`, SHA256
`00F7828EABB71FED0A376FBAD4ABFBFEDF1EAB5F723E506174DE438264152CDA`.
No retry build/test/lowering/emission/execution was performed by the coder.
The unchanged first-stage request below waits for root review and commit.

## Staged sole-Luna request

First stage ONLY after root reviews/commits the exact hashes:

1. Record source HEAD/content/binary/tool hashes, private TEMP/TMP and complete
   logs in `bench/.build/C05-implementation-runner`. No source/pin regeneration.
2. `dotnet build Blade.fsproj -c Release`. Stop on unexpected failure.
3. `bin/Release/net10.0/Blade.exe test optimize`. Record all pass/fail/skip totals
   and every sparse_wildcard result; stop on unexpected failures. The focused
   gate runs authored IR/proof, pure C++ emission and canonical interpreter
   checks internally. Do not separately execute native/interpreter categories.

Return to root after this stage. Native C++ ON/OFF runs of 048-053, actual
saved emission/plan dataflow review, interpreter/SQL/opt-diff/category checks
and any LLVM refusal replay require the later root dispatch. Do not launch
them automatically. Root should trace real parent key/data operands and ensure
the gather disappearance is the new route, not an unchanged materialized
fallback. No full suite, sanitizer, benchmark or measurement is requested.

Hashes of the five changed source/test files and six new corpus files are in
C05-implementation-SHA256SUMS.txt. This handoff and the manifest's own hashes
are supplied externally. Static diff-check passes; tracked code retains LF
index/CRLF working files. Coder stops at this frozen review checkpoint.
