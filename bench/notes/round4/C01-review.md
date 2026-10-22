# C01 orchestrator review

2026-10-22. Reviewed the implementation draft in `c-terminals`, base `361504b6`.
Disposition: ready for frozen handoff and targeted runner validation; untested.

Runner update: build passed; optimize has 522 passes and one failed declared-index
payload refusal assertion. Both backend runtime twins preserve values and abort site.
Card is held for owner investigation: an unused source type alias may lower to an
erased placeholder rather than the intended surviving mask-dependent type payload.
Do not close the card or weaken its census until the exact lowered representation and
a genuine declared-type regression are documented and tested.

Resolution: `TypeLower.lowerTypeExpr` erases an unused standalone CompoundIdx alias to
an opaque `compound` parameter, whereas a real index axis carries the mask operand.
The compiler census was not weakened. Tests now admit that erased alias and construct
a genuine declared-index payload to check local and typedef-only foreign retention.
The corrected gate passes **524/0/0**. Both backends' runtime comparisons passed on
identical compiler implementation sources. Accepted as lane commit `780a5915`, integrated
as `8c6571a7`; combined-code integration checks remain queued.

This is a narrow PM19 implementation: a stored dense rank-one Float64 source, a
capture-free total scalar predicate, and one seeded scalar terminal. Only reproducible
mode is admitted. Top-level mask and selected-array bindings must be excluded by an
explicit print selection; `compound` is top-level-only in the current language.

The pass rewrites the shared IR before ordinary optimization, lifting, CSE and schedule
tables. It removes both private stores and their CompoundInits entry together. Independent
interpreter ON/OFF comparisons are required because both codegen and interpreter see the
rewritten tree. Tests must also assert actual admission and absence of selection storage.

Review corrections incorporated:

- Move the rewrite before schedule-table construction, so removed bindings cannot leave
  stale pool, membership or hot-row plans.
- Rebuild the guarded callable's arrow metadata without a reordering licence. Its false
  branch returns the accumulator without applying the fold. Each accepted value retains
  its original position in the scalar chain; the seed stays at the terminal site.
- Census declared types as well as expressions, captures, hidden side operands, foreign
  references and printing. Decline source mutations through aliases or hidden calls.

Static review found no remaining blocking issue in the draft. Focused tests include
noncommutative order, empty/negative-zero seeds, cancellation, aliases, extra observers,
may-fail kernels, source mutation and an intervening abort. They are authored, not run.
The runner must resolve any source-syntax/assertion mistakes without weakening admission
or value checks, and report actual backend coverage and skips. No performance gain is
claimed before measurement; default licensed builds retain their existing route.
