# Claude review of the first sketch draft

2026-10-21. Read-only review of `README.md`, `00-region-runtime.md`,
`03-recurrence-ad-providers.md` and `05-composed-pipelines.md`. Claude reviewed the
initial draft while separate local reviewers inspected complementary concerns.
[SECOND-LOOK.md](SECOND-LOOK.md) records the consolidated corrections; statements
below about the old draft do not supersede that record or constitute execution evidence.
The review did not inspect the production AD emitter.

---

Here is my delegated review deliverable (returned to the caller). **No execution of any kind was performed** — strictly read-only reading of the four sketch files.

## Peakmem sketch review — 00 / 03 / 05 (baseline 667250cd)

### Findings, prioritized by section

**HIGH — 03 PM38 (destination cotangent): the tiled subtotal silently assumes the original's parenthesization.** The sketch rebuilds `g[j] + ((0 + a[j]) + b[j])` by zero-seeding `subtotal`, accumulating in event order, then `gradient[j] = gradient[j] + subtotal[j]`. This matches the reference *only* if Blade's existing adjoint already has that shape (zero-seeded subtotal + one final add into `g`). If the emitted adjoint accumulates in place as `((g+a)+b)`, the rewrite re-associates (`(g+a)+b ≠ g+(a+b)`) and flips signed zero (`0 + -0.0 = +0.0`, but `g + -0.0` keeps `g`) — the very parenthesization contract the sketch otherwise protects. The admission list gates replayability/totality/aliasing but not the accumulation-shape match. **Fix:** add a gate requiring the existing adjoint be zero-seeded-subtotal + single-final-add (or synthesize the `0 +` only where the original had it); otherwise decline to the retained full subtotal.

**MEDIUM — 03 PM39 (sparse fresh cotangent): epoch/`seen` init collision.** `read(j)` trusts `values[j]` when `seen[j] == epoch`. Nothing pins the first live epoch away from the initial fill of `seen[]`; if both are 0, every cell reads as seen and returns uninitialized `values[]`, violating "never read an uninitialized value before testing its tag." **Fix:** start the first epoch at 1 (or init `seen[]` to a sentinel no epoch uses); make the wrap-reset re-establish that invariant.

**MEDIUM — 03 PM40 (recurrence AD checkpoints): lag-K primals below `segment.begin` are not supplied.** Reverse step `t` needs primals `x[t-1..t-K]`; replay saves only `[begin,end)`, and the reverse step receives only `local_tape`+`adjoint`. For `t` within K of `begin`, some lags lie in the restored K-row boundary and are unreachable. The note shows awareness but the code doesn't route them. **Fix:** seed `local_tape` with the restored checkpoint's K-row primal boundary (or have the replay helper emit those K lags from `state`).

**LOW–MEDIUM — 00 R04 (`reserve_bundle`): no rollback if `register_reservation` fails.** `e.reserved`/`high_water` are bumped before registration; a failed register leaks budget permanently. **Fix:** `e.reserved -= requested` on the register-failure path. (The subtraction overflow test and `acquire_buffer`'s `release(r)` are correct.)

**LOW — 00 R05 & 05 ST03 (zip cursor progress).** `end = min(..., next compute-block boundary, N)`; if that boundary can equal `cursor`, the step is zero-length and the loop spins. **Fix:** define the boundary term strictly `> cursor` and assert `end > cursor` per iteration.

### Judged sound on static reading
- **00 R06** block reader → joined folds: independent seeded ascending chains, Int64 `count`, publish after validation, `lo += len` overflow-safe. Meets strict-fold/seed/order.
- **00 R07** checked spool: ordered two-pass, staged buffer consumed before reuse, credit retained on partial write, explicit I/O faults.
- **03 PM33** ring: row `t mod (K+1)` never aliases lags `t-1..t-K`; `built` gates self-reads; zero guard precedes modulo.
- **03 PM34/35/41/43/44/45/46/47/48/59** and **PM36** (correct two-grid Jacobi, radius-shrink slabs, partitioned cores, GS/red-black declined), **PM37/42** (synchronous path; `lease` sizing only matters for the deferred async arm).
- **05 ST01–ST18**: seed/order/parenthesization preserved; ST08 partitions by key (no cross-partition partial-sum merge); ST14 stable `(key, ordinal)` ties; ST15 left-major/right-minor without distributivity; ST16 produce→validate→commit order preserved.

### Remaining risks static reading cannot settle
- **PM38**: confirming Blade's actual emitted gradient association needs the AD emitter/`Grad.fs` (out of scope; graft coverage absent). The gate is warranted regardless; its materiality depends on that shape.
- **PM40**: whether the replay helper already folds in the boundary K-row is not visible in the sketch.
- **ST12** random-window spool needs a random-access catalog R07 doesn't provide; the O(tiles) catalog is a growing-metadata term flagged open — not closeable by reading.
- Resource-fault ordering vs. the non-spooling baseline is asserted, not statically demonstrable.

**No execution performed.** (~1,180 words.)

