# Second look at the implementation sketches

2026-10-21, source baseline `667250cd`. This is a static design review of the
[sketchbook](README.md), not a code review of an implemented change. Nothing was
compiled, emitted, run, tested or benchmarked. The proposed helpers and snippets remain
in Markdown outside the compiler, runtime and build inputs.

The first draft covered all 60 PM families and all 18 ST pipelines. Three reviewers
cross-read files they had not authored, and the primary author reviewed the complete
set and reconciled their findings. A separate [Claude review](CLAUDE-REVIEW.md) covered
the initial shared runtime, recurrence/AD/provider sketches and composed pipelines.
Its five findings and dispositions are recorded below. Reviewers performed no execution.

## Corrections from the static second look

These are corrections to the sketches, not claims that the repository already has the
proposed behavior. References identify the revised section rather than a stale line.

| Finding | Correction and affected sketch |
|---|---|
| Reservation bookkeeping could fail after charging capacity; credit could be released while backing survived. | R04 now uses nonallocating, consuming reservation tokens and frees storage before returning credit. PM06/08/42/53 transfer the same credit into retained storage and reserve only additional capacity. |
| Worker credit was treated as if it were an allocated buffer. | PM53 separates reservation, allocation/adoption and worker slices. |
| A copied cache could double-free raw pointers, and insertion could allocate during release. | PM08 uses a noncopyable fixed-slot owner with a no-allocation insertion path. |
| A consumer or function return could retain an alias after a proposed local release. | PM02 and PM59 retain/transfer escaping root ownership; local release applies only after the actual last borrower. |
| Recycled recurrence slots could expose stale unwritten cells. | PM33 restores row initialization on every reuse unless complete overwrite before read is proved. |
| Shrinking a physically clipped stencil region could discard required boundary cells. | PM36 derives stage domains from exact dependencies and applies physical boundary rules at every stage. |
| Epoch-based sparse cotangent storage could mistake uninitialized values for current values. | PM39 starts tags at zero and the epoch at one; wrap clears tags before restarting. |
| Block-local missing-value refusal could replace a later source read failure or change diagnostics. | R06 and PM41/43 carry whole-read validation state and finalize at the original operation boundary. Provider pipelines inherit this contract. |
| A sink tracked only the first nonfinite value, losing the complete writer diagnostic. | PM45/ST16 retain the full count and first flat storage cell, defer the diagnostic until production/read barriers complete, and stage exact invalid bits privately. |
| The spool staging buffer was unspecified and could be overrun by a larger physical provider block. | R07 reserves input/decoder/staging/encoding/read capacity explicitly and subdivides input into at most B cells. The replay phase reuses staging for decoded cells. |
| Spool counter checks happened after writing, and quota metadata could grow per append. | R07 checks counts/ordinals first and atomically grows one spool-owned disk-credit counter; partial writes remain charged until deletion. |
| Checked replay and close-before-delete behavior were hidden in helper names. | R07 specifies bounded header/payload validation, integrity, counts, footer/trailing-data checks, borrow release and checked reader close before removal. |
| External merge sort did not show every generation/batch and admitted nonprogressing fan-in one. | ST14 requires fan-in at least two and spells out generation manifests, heap refill, run counts and old/new ownership. |
| External join hid oversized duplicate groups behind an unspecified fallback. | ST15 sorts supported keys, spools an equal-key right group, replays it per left row and restores pair ordinal order before the strict fold. RAM is bounded; pair disk remains O(J). |
| Sorting group results afterward cannot repair a kernel that used the wrong first-seen group ordinal. | ST08 initially admits only ordinal-independent seeds/kernels and retains the numeric-negative versus declared Enum membership distinction. |
| Sink commit failure did not distinguish an unpublished result from an uncertain or confirmed publication. | R08/ST16 have explicit private, ready, committing, committed, not-committed and uncertain outcomes. Cleanup preserves the original error and cannot destructively abort a confirmed result. |
| A Gram-action sketch made B access strided without reducing workspace. | PM30 restores the existing contiguous j-outer/k-inner traversal with the same per-output addition order. |
| Scalar LU pivot validation was attributed to a source route that does not provide it. | PM32 uses the existing scalar operation under valid-factor admission; new invalid-factor diagnostics are separate semantic work. |
| Dense-write and sparse-touch controls appeared to keep both allocations live together. | PM54 puts them in separate fixture bodies and keeps sparse touching a distinct diagnostic. |

## Claude review

| Finding in the first draft | Disposition |
|---|---|
| High: PM38's zero-seeded cotangent subtotal is correct only for a matching original accumulation graph. | Added an explicit graph-match admission: zero-seeded subtotal plus one final incoming-gradient add must already be the original shape. Direct updates such as `((g+a)+b)` cannot be reassociated into it. |
| Medium: PM39's initial epoch could collide with uninitialized tags. | Agrees with the local finding; explicit zero tags / epoch one and wrap reset are now shown. |
| Medium: PM40's reverse segment could not access lag-K primal rows before the segment start. | Added an explicitly retained checkpoint boundary and a reverse accessor resolving pre-segment primals as well as replayed local rows; this storage is included in the bound. |
| Low–medium: R04 could leak credit if reservation registration failed. | Removed fallible registration from the committed reservation path. Tokens are nonallocating and consuming; actual backing is freed before credit returns. |
| Low: R05/ST03 could stall if the next block boundary equaled the current cursor. | Boundaries must be strictly greater than the cursor, and every iteration checks positive progress. |

Claude also identified random-window spool indexing and resource-fault policy as remaining
risks. Those remain explicit prerequisites. Its read-only scope did not include the
production AD emitter, so the PM38 finding is treated as a required admission gate rather
than an assertion that the current emitter has a particular graph. Its favorable readings
do not override the independent corrections above, including physical halo boundaries,
cache ownership, diagnostics and external operator progress.

## What remains for implementation

Static document checks found exactly one section for each PM01–PM60 and ST01–ST18,
paired code fences and existing local Markdown link targets. The tracked documentation
diff passed its whitespace check. Those checks inspect text only; they provide no C++/F#
syntax, runtime correctness, memory-bound or performance evidence.

- Reconfirm source anchors and coordinate shared owners before porting a sketch. The
  speed campaign can change the relevant scalar, selection, grouping and tensor routes.
- Implement real IR facts, complete demand/effect/alias analysis, planner coordination,
  all-or-nothing emission and supported backend/interpreter behavior. Helper names are
  contracts, not evidence that these facilities exist.
- Establish supported numeric/ordering domains, empty-case behavior, exact fold graphs,
  provider validation order and resource-failure policy in actual code. Broad ordered
  failure proofs, arbitrary replay, nullable/floating external keys, random-window spool
  catalogs and generic provider transactions remain gated.
- Derive actual schedule bounds, including allocator capacity, metadata, retained caches,
  codec internals, workers and simultaneous disk generations. These analytical sketches
  neither measure peak memory nor enforce process RSS or OS page-cache limits.
- Later verification must check actual route execution as well as results: an unchanged
  fallback can pass differential equality. Odd extents/tails, empty cases, aliases,
  partial writes, quota boundaries, cancellation, I/O faults and uncertain commit recovery
  need concrete fixtures after implementation is authorized.

The proposed first cuts remain a missing stored-input terminal, then the rank-1 Zarr
map-to-joined-fold bridge, then a fixed-width sequential spool across an unchanged
two-pass statistics barrier. External operators and broad AD replay follow their stated
prerequisites; this review does not remove those prerequisites.
