# W2-5 / C -- workload N10 `groupls` (grouped nonlinear least-squares local models), with corrected N02 as the fallback

Read `BUILDER-PREAMBLE.md` first (reset to the integration tip it names). Brief: `research/C-workloads.md` section N10 (source
`scratchpad/c-work/n10.blade`) and N02 (`n02.blade`). Review: `reviews/codex-review-C.md` -- N10 verdict ADD AFTER FIXES and
finding 9 (forced r / jt tapes vs "compiler temporaries"), 8 (storage), 10 (regimes / power-of-two component axes / outer omp),
11 (inputonly rule); N02 verdict ADD AFTER FIXES and finding 2 (the strict AD reference must be an explicit ordered update list,
repeated-offset −2 included). W1-5's two kernels (`bench/kernels/twofir`, `tensorop`, commit 6459275b) are the templates for
kernel.json / spec.md / twins / oracles / pins / `run.py check`; the harness procedure is `bench/kernels/README.md` and
`bench/harness/README.md`; the protocol is `bench/README.md`.

## Gate first: does the per-group packed H emit?

N10's shape returns a packed symmetric H per group from a row map (RESULTS 15.4-adjacent; gap G64 is the whole-array kernel
over a compound, a different shape). Run `blade emit` and `blade run` at smoke size on the corrected N10 source in BOTH lanes
first. If it refuses or BL9002s: record the exact diagnostic as a gap row (next free number, G67) with the probe, and switch to
the N02 fallback below; do not spend the item on a compiler fix.

## N10 as a kernel (if it emits)

Outputs: cost, gradient and the packed H per group ONLY (the catalog asks for no per-sample Jacobian); spell the residual and
derivative producers with the highest deferred / shared-summary idiom (no forced `r` / `jt` tapes -- if a tape is needed for
the shared summaries, it must be a compiler temporary, otherwise keep a SEPARATE "materialized control" variant); +0 seeds kept
exactly as the catalog; the logical-H checksum weighting (off-diagonal cells twice through their mirrors) matched by the expert;
equal-sized groups and hand-derived derivatives DECLARED as the specialization (ragged / AD variants are later); non-power-of-two
sizes everywhere (P = 4 components is power-of-two: use 5, or record an explicit exception); legal `where omp` on the
independent maps and the checksums (no captured writes); inputonly twin (input generation share > 5%). Natural = per-group
plain loops forming J explicitly; expert = fused per-group accumulation of cost / gradient / H with the same chains and no
Jacobian storage. Oracle ordered (python, no einsum). `run.py check groupls --regimes all` clean; validate_B ALL OK.

## N02 fallback (if N10 does not emit): `haloadjoint`

1-D weighted nonlinear halo primal + reverse action: the strict derivative reference is an EXPLICIT ORDERED UPDATE LIST
(both −2 occurrences, +0 initial dx, the source multiplication parentheses, skipped nonexistent windows, stable order within
an offset -- derived from the emitted AD's actual sweep order and written in spec.md), both Fortran twins implement exactly
that list; y stored (n−4 interior cells) and dx stored (n cells, zero boundary); loss / dw accounting disclosed; inputonly twin.

## Report

Which workload landed, the gate's outcome, checksums / sizes / regimes, the forms `blade plan` reports, `run.py check`
footers, validate_B, `git diff --check`; no compiler change; no full-size timing.
